using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace OpenShape;

/// <summary>
/// Built-in replacement for the Fusion "IsaacSimLink" add-in: sends the design to Isaac Sim through
/// the same code server (127.0.0.1:8226) and bridge module (~/fusion-webviewer/isaac_bridge), either
/// as CAD (STEP → import_cad) or as a robot (URDF with links, joints, mass → import_urdf).
/// Settings are shared with the add-in (exchange/isaacsimlink_settings.json).
/// </summary>
public static class IsaacLink
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    /// <summary>Exchange folder shared with the Fusion add-in and the cloud sync (tests point it elsewhere).</summary>
    public static string Exchange { get; set; } = Path.Combine(Home, "fusion-webviewer", "exchange");
    public static string Bridge => Path.Combine(Home, "fusion-webviewer", "isaac_bridge");
    public static string Host { get; set; } = "127.0.0.1";
    public static int Port { get; set; } = 8226;
    private const string Mark = "__ISAACSIMLINK_RESULT__";

    private static readonly JsonSerializerOptions PyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        var s = sb.ToString().Trim('_');
        while (s.Contains("__")) s = s.Replace("__", "_");
        if (s.Length == 0) s = "item";
        if (char.IsDigit(s[0])) s = "_" + s;
        return s;
    }

    // ------------------------------------------------------------------ settings

    private static string SettingsFile => Path.Combine(Exchange, "isaacsimlink_settings.json");

    public static JsonObject LoadSettings()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(SettingsFile)) is JsonObject o) return o;
        }
        catch (Exception)
        {
            // missing or unreadable: defaults
        }
        return new JsonObject { ["autosync"] = false, ["last_mode"] = new JsonObject() };
    }

    public static void SaveSettings(JsonObject o)
    {
        Directory.CreateDirectory(Exchange);
        File.WriteAllText(SettingsFile, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    public static bool AutoSync
    {
        get => LoadSettings()["autosync"]?.GetValue<bool>() == true;
        set
        {
            var o = LoadSettings();
            o["autosync"] = value;
            SaveSettings(o);
        }
    }

    public static string LastMode(string design) => LoadSettings()["last_mode"]?[design]?.GetValue<string>() ?? "cad";

    private static void RememberMode(string design, string mode)
    {
        var o = LoadSettings();
        if (o["last_mode"] is not JsonObject m) o["last_mode"] = m = new JsonObject();
        m[design] = mode;
        SaveSettings(o);
    }

    // ------------------------------------------------------------------ bridge

    private static string Py(object? v) => v switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        string s => JsonSerializer.Serialize(s, PyJson),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "None",
    };

    /// <summary>Call fusion_bridge.&lt;function&gt; inside Isaac Sim and return its result object.</summary>
    public static JsonNode Call(string function, object?[] args, (string key, object? value)[]? kwargs = null, int timeoutMs = 600_000)
    {
        var call = string.Join(", ", args.Select(Py).Concat((kwargs ?? Array.Empty<(string, object?)>()).Select(k => $"{k.key}={Py(k.value)}")));
        var code = "import sys, importlib, json\n" +
                   $"_p = {Py(Bridge)}\n" +
                   "if _p not in sys.path: sys.path.insert(0, _p)\n" +
                   "import fusion_bridge\n" +
                   "importlib.reload(fusion_bridge)\n" +
                   $"_r = fusion_bridge.{function}({call})\n" +
                   $"print({Py(Mark)} + json.dumps(_r, ensure_ascii=False, default=str))\n";
        using var client = new TcpClient();
        try
        {
            if (!client.ConnectAsync(Host, Port).Wait(5000)) throw new IOException();
        }
        catch (Exception)
        {
            throw new InvalidOperationException($"Isaac Sim에 연결할 수 없습니다 ({Host}:{Port}). Isaac Sim과 코드 서버가 실행 중인지 확인하세요.");
        }
        client.ReceiveTimeout = timeoutMs;
        var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(code);
        stream.Write(bytes);
        client.Client.Shutdown(SocketShutdown.Send);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var reply = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray())) ?? throw new InvalidOperationException("Isaac Sim 응답이 비어 있습니다.");
        if (reply["status"]?.GetValue<string>() != "ok")
            throw new InvalidOperationException($"{reply["ename"]}: {reply["evalue"]}");
        var output = reply["output"]?.GetValue<string>() ?? "";
        foreach (var line in output.Split('\n').Reverse())
            if (line.StartsWith(Mark)) return JsonNode.Parse(line[Mark.Length..])!;
        throw new InvalidOperationException("Isaac Sim 응답에서 결과를 찾지 못했습니다:\n" + output);
    }

    public static string Describe(JsonNode r)
    {
        if (r["prim"] != null)
            return $"Isaac Sim에 {(r["created"]?.GetValue<bool>() == true ? "추가" : "갱신")}됨: {r["prim"]} ({r["seconds"]}초)";
        if (r["items"] is JsonArray items)
            return "Isaac Sim 연결됨\n보낸 항목:\n" + (items.Count == 0 ? "  (아직 없음)" : string.Join("\n", items.Select(i => "  - " + i)));
        return r.ToJsonString();
    }

    public static string Status() => Describe(Call("status", Array.Empty<object?>()));

    // ------------------------------------------------------------------ export

    public static string ExportDir(string design, string kind)
    {
        var d = Path.Combine(Exchange, design, kind);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Write the design as STEP and place it in Isaac Sim.</summary>
    public static string SendCad(string design, Func<string, bool> writeStep)
    {
        var path = Path.Combine(ExportDir(design, "cad"), design + ".step");
        if (!writeStep(path)) throw new InvalidOperationException("STEP 내보내기에 실패했습니다.");
        var r = Describe(Call("import_cad", new object?[] { path, design }, new[] { ("up_axis", (object?)"Z") }));
        RememberMode(design, "cad");
        return r;
    }

    public static string SendRobot(string design, ModelState s, Doc doc, Func<string, string> bodyName, out List<string> warnings)
    {
        var path = WriteUrdf(design, s, doc, bodyName, out warnings);
        var r = Describe(Call("import_urdf", new object?[] { path, design }, new[] { ("fix_base", (object?)true) }));
        RememberMode(design, "robot");
        return warnings.Count == 0 ? r : r + "\n\n경고:\n- " + string.Join("\n- ", warnings);
    }

    /// <summary>Density (kg/mm³) by appearance colour; steel by default.</summary>
    private static double Density(string? hex) => hex switch
    {
        "#C9CED4" => 2.70e-6,                       // aluminium
        "#C8A951" => 8.50e-6,                       // brass
        "#B87333" => 8.96e-6,                       // copper
        "#3A3A3A" => 1.10e-6,                       // rubber
        "#F2F2F2" or "#2E2E2E" or "#D9342B" or "#2F6FD6" or "#2DA44E" or "#F2C230" or "#F07C21" => 1.20e-6,   // plastics
        _ => 7.85e-6,
    };

    private sealed record LinkData(string Name, List<Body> Bodies, bool Grounded);
    private sealed record JointData(string Name, string Type, string Parent, string Child, V3 Origin, V3 Axis, double? Lower, double? Upper);

    /// <summary>URDF (links = components or loose bodies, joints from the design) with STL meshes. Returns the URDF path.</summary>
    public static string WriteUrdf(string design, ModelState s, Doc doc, Func<string, string> bodyName, out List<string> warnings)
    {
        warnings = new List<string>();
        var visible = s.Bodies.Where(b => !doc.HiddenBodies.Contains(b.Id)).ToList();
        if (visible.Count == 0) throw new InvalidOperationException("보낼 본체가 없습니다.");
        var links = new List<LinkData>();
        var used = new HashSet<string>();
        string Unique(string n) { var u = n; for (int k = 2; !used.Add(u); k++) u = $"{n}_{k}"; return u; }
        var taken = new HashSet<string>();
        foreach (var c in doc.Components)
        {
            var bs = visible.Where(b => c.Bodies.Contains(b.Id)).ToList();
            if (bs.Count == 0) continue;
            links.Add(new LinkData(Unique(SafeName(c.Name) is "item" ? "link" : SafeName(c.Name)), bs, c.Grounded));
            foreach (var b in bs) taken.Add(b.Id);
        }
        foreach (var b in visible.Where(b => !taken.Contains(b.Id)))
            links.Add(new LinkData(Unique(SafeName(bodyName(b.Id)) is var n && n.StartsWith('_') ? "body" + n : SafeName(bodyName(b.Id))), new List<Body> { b }, false));
        string? LinkOf(IEnumerable<string> ids) => links.FirstOrDefault(l => l.Bodies.Any(b => ids.Contains(b.Id)))?.Name;

        // joints: moving side = child, fixed side = parent (like Fusion occurrenceOne/Two)
        var raw = new List<JointData>();
        foreach (var j in s.Joints)
        {
            var child = LinkOf(j.Moving);
            var parent = LinkOf(j.Fixed);
            if (child == null || parent == null) continue;
            string type = j.Type switch
            {
                "revolute" => j.Lower != null || j.Upper != null ? "revolute" : "continuous",
                "slider" => "prismatic",
                "rigid" => "fixed",
                "planar" => "planar",
                _ => "fixed",
            };
            if (j.Type is "cylindrical" or "pinslot" or "ball")
                warnings.Add($"'{j.Name}' ({JointFeature.TypeName(j.Type)})은 URDF에 없는 접합이라 고정(fixed)으로 보냅니다");
            raw.Add(new JointData(SafeName(j.Name) is "item" ? $"joint{raw.Count + 1}" : SafeName(j.Name), type, parent, child, j.Origin, j.Axis, j.Lower, j.Upper));
        }
        // tree: first parent per child, roots fixed to the (grounded) base, parents before children
        var children = new HashSet<string>();
        var tree = new List<JointData>();
        foreach (var j in raw)
        {
            if (j.Parent == j.Child) continue;
            if (!children.Add(j.Child))
            {
                warnings.Add($"'{j.Child}'에 부모 접합이 여러 개라 '{j.Name}'은 건너뜁니다 (URDF는 루프를 지원하지 않음)");
                continue;
            }
            tree.Add(j);
        }
        var roots = links.Select(l => l.Name).Where(n => !children.Contains(n)).ToList();
        var baseLink = roots.FirstOrDefault(n => links.First(l => l.Name == n).Grounded) ?? roots.FirstOrDefault() ?? links[0].Name;
        foreach (var r in roots.Where(r => r != baseLink))
        {
            warnings.Add($"'{r}'은 접합이 없어 베이스('{baseLink}')에 고정합니다");
            tree.Add(new JointData("fixed_" + r, "fixed", baseLink, r, V3.Zero, V3.UnitZ, null, null));
        }
        var ordered = new List<JointData>();
        var frontier = new Queue<string>(new[] { baseLink });
        var remaining = tree.ToList();
        while (frontier.Count > 0)
        {
            var cur = frontier.Dequeue();
            foreach (var j in remaining.Where(j => j.Parent == cur).ToList())
            {
                ordered.Add(j);
                remaining.Remove(j);
                frontier.Enqueue(j.Child);
            }
        }
        foreach (var j in remaining) warnings.Add($"'{j.Name}'은 베이스에서 이어지지 않아 건너뜁니다");
        var frames = new Dictionary<string, V3> { [baseLink] = V3.Zero };
        foreach (var j in ordered) frames[j.Child] = j.Origin;

        var dir = ExportDir(design, "urdf");
        Directory.CreateDirectory(Path.Combine(dir, "meshes"));
        string G(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        string Xyz(V3 v) => $"{G(v.X)} {G(v.Y)} {G(v.Z)}";
        const double M = 0.001;   // mm → m
        var robot = new XElement("robot", new XAttribute("name", design));
        foreach (var link in links)
        {
            var origin = frames.GetValueOrDefault(link.Name, V3.Zero);
            var el = new XElement("link", new XAttribute("name", link.Name));
            // mass properties: sum over the link's bodies, inertia about the link's centre of mass
            double mass = 0;
            var com = V3.Zero;
            var parts = new List<(double m, V3 c, double[] i)>();
            foreach (var b in link.Bodies)
            {
                double rho = Density(doc.BodyColors.GetValueOrDefault(b.Id));
                var mp = b.Solid.MassProperties;
                if (mp.volume <= 0) continue;
                double m = mp.volume * rho;
                parts.Add((m, mp.center, mp.inertia.Select(x => x * rho).ToArray()));
                mass += m;
                com = com + mp.center * m;
            }
            if (mass > 0)
            {
                com = com * (1 / mass);
                double ixx = 0, iyy = 0, izz = 0, ixy = 0, iyz = 0, ixz = 0;
                foreach (var (m, c, i) in parts)
                {
                    var d = c - com;
                    // parallel-axis theorem (OCCT gives the tensor: products are -∫xy dm)
                    ixx += i[0] + m * (d.Y * d.Y + d.Z * d.Z);
                    iyy += i[1] + m * (d.X * d.X + d.Z * d.Z);
                    izz += i[2] + m * (d.X * d.X + d.Y * d.Y);
                    ixy += i[3] - m * d.X * d.Y;
                    iyz += i[4] - m * d.Y * d.Z;
                    ixz += i[5] - m * d.X * d.Z;
                }
                const double I = 1e-6;   // kg·mm² → kg·m²
                el.Add(new XElement("inertial",
                    new XElement("origin", new XAttribute("xyz", Xyz((com - origin) * M)), new XAttribute("rpy", "0 0 0")),
                    new XElement("mass", new XAttribute("value", G(mass))),
                    new XElement("inertia", new XAttribute("ixx", G(ixx * I)), new XAttribute("iyy", G(iyy * I)), new XAttribute("izz", G(izz * I)),
                        new XAttribute("ixy", G(ixy * I)), new XAttribute("iyz", G(iyz * I)), new XAttribute("ixz", G(ixz * I)))));
            }
            var mesh = $"meshes/{link.Name}.stl";
            if (Solid.Export(link.Bodies.Select(b => (bodyName(b.Id), b.Solid)).ToList(), Path.Combine(dir, mesh), 0))
            {
                var col = doc.BodyColors.GetValueOrDefault(link.Bodies[0].Id, "#D6D2CC");
                var rgb = Avalonia.Media.Color.Parse(col);
                foreach (var tag in new[] { "visual", "collision" })
                {
                    var g = new XElement(tag,
                        new XElement("origin", new XAttribute("xyz", Xyz(-origin * M)), new XAttribute("rpy", "0 0 0")),
                        new XElement("geometry", new XElement("mesh", new XAttribute("filename", mesh), new XAttribute("scale", "0.001 0.001 0.001"))));
                    if (tag == "visual")
                        g.Add(new XElement("material", new XAttribute("name", link.Name + "_color"),
                            new XElement("color", new XAttribute("rgba", $"{G(rgb.R / 255.0)} {G(rgb.G / 255.0)} {G(rgb.B / 255.0)} 1"))));
                    el.Add(g);
                }
            }
            else warnings.Add($"'{link.Name}' 메쉬 내보내기 실패");
            robot.Add(el);
        }
        foreach (var j in ordered)
        {
            var el = new XElement("joint", new XAttribute("name", j.Name), new XAttribute("type", j.Type),
                new XElement("parent", new XAttribute("link", j.Parent)),
                new XElement("child", new XAttribute("link", j.Child)),
                new XElement("origin", new XAttribute("xyz", Xyz((j.Origin - frames.GetValueOrDefault(j.Parent, V3.Zero)) * M)), new XAttribute("rpy", "0 0 0")));
            if (j.Type != "fixed") el.Add(new XElement("axis", new XAttribute("xyz", Xyz(j.Axis))));
            if (j.Type is "revolute" or "prismatic")
            {
                double scale = j.Type == "prismatic" ? M : Math.PI / 180;
                double lo = (j.Lower ?? (j.Type == "prismatic" ? -1000 : -180)) * scale, hi = (j.Upper ?? (j.Type == "prismatic" ? 1000 : 180)) * scale;
                el.Add(new XElement("limit", new XAttribute("lower", G(lo)), new XAttribute("upper", G(hi)), new XAttribute("effort", "100"), new XAttribute("velocity", "10")));
            }
            robot.Add(el);
        }
        var path = Path.Combine(dir, design + ".urdf");
        File.WriteAllText(path, "<?xml version=\"1.0\"?>\n" + robot.ToString() + "\n");
        return path;
    }
}
