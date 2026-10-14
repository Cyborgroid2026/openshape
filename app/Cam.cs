using System.Globalization;
using System.Text;

namespace OpenShape;

// ---------------------------------------------------------------- CAM document (제조 작업공간)

/// <summary>2.5-axis milling of the design: one setup (stock and work origin), a tool library and operations.</summary>
public sealed class CamDoc
{
    public CamSetup Setup { get; set; } = new();
    public List<CamTool> Tools { get; set; } = CamTool.Defaults();
    public List<CamOperation> Operations { get; set; } = new();
    /// <summary>grbl, linuxcnc or fanuc.</summary>
    public string Post { get; set; } = "grbl";
    public int ProgramNumber { get; set; } = 1001;

    public CamTool? Tool(int number) => Tools.FirstOrDefault(t => t.Number == number);

    public string NextOperationName(string type)
    {
        var baseName = CamOperation.TypeName(type);
        int n = 1;
        while (Operations.Any(o => o.Name == baseName + n)) n++;
        return baseName + n;
    }

    public static readonly string[] Posts = { "grbl", "linuxcnc", "fanuc" };

    public static string PostName(string p) => p switch { "linuxcnc" => "LinuxCNC", "fanuc" => "Fanuc", _ => "GRBL" };
}

public sealed class CamSetup
{
    /// <summary>Body to machine; empty = every visible body.</summary>
    public string Body { get; set; } = "";
    public double SideOffset { get; set; } = 2;
    public double TopOffset { get; set; } = 1;
    /// <summary>Work origin: corner (stock top, left front), center (stock top centre) or model (model origin).</summary>
    public string Origin { get; set; } = "corner";
    /// <summary>Clearance above the stock top for rapid moves.</summary>
    public double SafeHeight { get; set; } = 10;
    /// <summary>Height above the cut where rapids stop and feeds start.</summary>
    public double Retract { get; set; } = 2;

    public static readonly string[] Origins = { "corner", "center", "model" };
    public static string OriginName(string o) => o switch { "center" => "스톡 상단 중심", "model" => "모델 원점", _ => "스톡 상단 왼쪽 앞 모서리" };
}

public sealed class CamTool
{
    public int Number { get; set; } = 1;
    /// <summary>flat (flat end mill), drill or face (face mill).</summary>
    public string Type { get; set; } = "flat";
    public double Diameter { get; set; } = 6;
    public double Rpm { get; set; } = 10000;
    /// <summary>Cutting feed (mm/min).</summary>
    public double Feed { get; set; } = 800;
    /// <summary>Plunge feed (mm/min).</summary>
    public double Plunge { get; set; } = 250;

    public string Label => $"T{Number} {TypeName(Type)} ⌀{Diameter.ToString("0.##", CultureInfo.InvariantCulture)}";

    public static string TypeName(string t) => t switch { "drill" => "드릴", "face" => "페이스 밀", _ => "평 엔드밀" };

    public static List<CamTool> Defaults() => new()
    {
        new() { Number = 1, Type = "flat", Diameter = 6, Rpm = 10000, Feed = 800, Plunge = 250 },
        new() { Number = 2, Type = "flat", Diameter = 3, Rpm = 12000, Feed = 500, Plunge = 150 },
        new() { Number = 3, Type = "flat", Diameter = 10, Rpm = 8000, Feed = 1000, Plunge = 300 },
        new() { Number = 4, Type = "face", Diameter = 25, Rpm = 6000, Feed = 1200, Plunge = 300 },
        new() { Number = 5, Type = "drill", Diameter = 3, Rpm = 3000, Feed = 150, Plunge = 150 },
        new() { Number = 6, Type = "drill", Diameter = 5, Rpm = 2500, Feed = 150, Plunge = 150 },
        new() { Number = 7, Type = "drill", Diameter = 6, Rpm = 2200, Feed = 140, Plunge = 140 },
        new() { Number = 8, Type = "drill", Diameter = 8, Rpm = 1800, Feed = 120, Plunge = 120 },
        new() { Number = 9, Type = "drill", Diameter = 10, Rpm = 1500, Feed = 110, Plunge = 110 },
    };
}

public sealed class CamOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>face, pocket, contour or drill.</summary>
    public string Type { get; set; } = "pocket";
    public string Name { get; set; } = "";
    public int Tool { get; set; } = 1;
    /// <summary>Body and a point on the picked planar face (pocket floor / contour face).</summary>
    public string Body { get; set; } = "";
    public double[] Pick { get; set; } = Array.Empty<double>();
    public double Stepdown { get; set; } = 1.5;
    /// <summary>Stepover as a fraction of the tool diameter.</summary>
    public double Stepover { get; set; } = 0.45;
    public double StockToLeave { get; set; }
    /// <summary>Contour: cut this far below the model bottom so the part comes free.</summary>
    public double BottomOffset { get; set; } = 0.5;
    public bool Climb { get; set; } = true;
    /// <summary>Drill: holes of this diameter (0 = those matching the tool).</summary>
    public double HoleDiameter { get; set; }
    /// <summary>Drill: peck depth (0 = drill in one go).</summary>
    public double Peck { get; set; }

    public static string TypeName(string t) => t switch { "face" => "페이스", "contour" => "2D 윤곽", "drill" => "드릴", _ => "2D 포켓" };
}

// ---------------------------------------------------------------- toolpaths

public readonly record struct CamMove(bool Rapid, V3 P, double Feed);

public sealed class Toolpath
{
    public List<CamMove> Moves { get; } = new();
    public string? Error { get; set; }
    public double CutLength { get; private set; }
    public double RapidLength { get; private set; }
    public TimeSpan Time { get; private set; }

    /// <summary>Rapid traverse used for time estimates (mm/min).</summary>
    public const double RapidRate = 5000;

    public void Finish(V3 start)
    {
        double cut = 0, rapid = 0, minutes = 0;
        var at = start;
        foreach (var m in Moves)
        {
            double d = (m.P - at).Length;
            if (m.Rapid) { rapid += d; minutes += d / RapidRate; }
            else { cut += d; minutes += d / Math.Max(1, m.Feed); }
            at = m.P;
        }
        CutLength = cut;
        RapidLength = rapid;
        Time = TimeSpan.FromMinutes(minutes);
    }
}

public static class CamEngine
{
    /// <summary>Bodies the setup machines.</summary>
    public static List<Body> Parts(CamSetup s, ModelState state, ISet<string> hidden) =>
        state.Bodies.Where(b => (s.Body.Length == 0 ? !hidden.Contains(b.Id) : b.Id == s.Body) && b.Solid.Info.Solids > 0).ToList();

    /// <summary>Model bounds and stock box (model plus side and top offsets).</summary>
    public static (V3 modelLo, V3 modelHi, V3 lo, V3 hi)? Stock(CamSetup s, ModelState state, ISet<string> hidden)
    {
        var parts = Parts(s, state, hidden);
        if (parts.Count == 0) return null;
        var lo = parts.Select(b => b.Solid.Info.Min).Aggregate((a, b) => new V3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)));
        var hi = parts.Select(b => b.Solid.Info.Max).Aggregate((a, b) => new V3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
        return (lo, hi, new V3(lo.X - s.SideOffset, lo.Y - s.SideOffset, lo.Z), new V3(hi.X + s.SideOffset, hi.Y + s.SideOffset, hi.Z + s.TopOffset));
    }

    /// <summary>Work origin in model coordinates.</summary>
    public static V3 Origin(CamSetup s, V3 stockLo, V3 stockHi) => s.Origin switch
    {
        "center" => new V3((stockLo.X + stockHi.X) / 2, (stockLo.Y + stockHi.Y) / 2, stockHi.Z),
        "model" => V3.Zero,
        _ => new V3(stockLo.X, stockLo.Y, stockHi.Z),
    };

    /// <summary>Depth levels from just below <paramref name="top"/> down to <paramref name="bottom"/>.</summary>
    private static List<double> Levels(double top, double bottom, double stepdown)
    {
        var zs = new List<double>();
        stepdown = Math.Max(0.05, stepdown);
        for (double z = top - stepdown; z > bottom + 1e-6; z -= stepdown) zs.Add(z);
        zs.Add(bottom);
        return zs;
    }

    private static double SignedArea(IReadOnlyList<V3> l)
    {
        double s = 0;
        for (int i = 0; i < l.Count; i++) { var (a, b) = (l[i], l[(i + 1) % l.Count]); s += a.X * b.Y - b.X * a.Y; }
        return s / 2;
    }

    /// <summary>Loop running counter-clockwise (seen from above) when ccw, else clockwise; closed (ends at its start).</summary>
    private static List<V3> Oriented(IReadOnlyList<V3> loop, bool ccw)
    {
        var l = loop.ToList();
        if (l.Count > 1 && (l[0] - l[^1]).Length < 1e-9) l.RemoveAt(l.Count - 1);
        if (SignedArea(l) > 0 != ccw) l.Reverse();
        l.Add(l[0]);
        return l;
    }

    public static Toolpath Generate(CamDoc cam, CamOperation op, ModelState state, ISet<string> hidden)
    {
        var tp = new Toolpath();
        try
        {
            var tool = cam.Tool(op.Tool) ?? throw new InvalidOperationException("공구를 선택하세요.");
            var stock = Stock(cam.Setup, state, hidden) ?? throw new InvalidOperationException("가공할 본체가 없습니다.");
            var (mlo, mhi, slo, shi) = stock;
            double safe = shi.Z + cam.Setup.SafeHeight, r = tool.Diameter / 2;
            var start = new V3(slo.X, slo.Y, safe);
            void Rapid(V3 p) => tp.Moves.Add(new CamMove(true, p, 0));
            void Cut(V3 p, double f) => tp.Moves.Add(new CamMove(false, p, f));
            // rapid over, rapid down to just above the cut, feed in
            void Enter(V3 p, double above)
            {
                Rapid(new V3(p.X, p.Y, safe));
                Rapid(new V3(p.X, p.Y, Math.Min(safe, above + cam.Setup.Retract)));
                Cut(p, tool.Plunge);
            }
            void Leave() { if (tp.Moves.Count > 0) Rapid(tp.Moves[^1].P with { Z = safe }); }

            switch (op.Type)
            {
                case "face":
                {
                    if (tool.Type == "drill") throw new InvalidOperationException("페이스 가공에는 엔드밀이나 페이스 밀을 쓰세요.");
                    double step = Math.Clamp(op.Stepover, 0.05, 0.95) * tool.Diameter;
                    double x0 = slo.X - r - 1, x1 = shi.X + r + 1;
                    double target = mhi.Z + op.StockToLeave;
                    if (shi.Z - target < 1e-6) throw new InvalidOperationException("모델 위에 깎을 스톡이 없습니다 (설정의 위 여유를 늘리세요).");
                    double above = shi.Z;
                    foreach (var z in Levels(shi.Z, target, op.Stepdown))
                    {
                        bool forward = true;
                        bool first = true;
                        for (double y = slo.Y + r * 0.5; y < shi.Y + r - 1e-9; y += step)
                        {
                            var a = new V3(forward ? x0 : x1, y, z);
                            var b = new V3(forward ? x1 : x0, y, z);
                            if (first) { Enter(a, above); first = false; }
                            else Cut(a, tool.Feed);
                            Cut(b, tool.Feed);
                            forward = !forward;
                        }
                        Leave();
                        above = z;
                    }
                    break;
                }
                case "pocket":
                {
                    if (tool.Type == "drill") throw new InvalidOperationException("포켓 가공에는 엔드밀을 쓰세요.");
                    var body = state.Find(op.Body) ?? throw new InvalidOperationException("포켓 바닥 면을 선택하세요.");
                    if (op.Pick.Length != 3) throw new InvalidOperationException("포켓 바닥 면을 선택하세요.");
                    var pick = new V3(op.Pick[0], op.Pick[1], op.Pick[2]);
                    var frame = body.Solid.FaceFrame(pick) ?? throw new InvalidOperationException("평면인 면을 선택하세요.");
                    if (frame.N.Z < 0.999) throw new InvalidOperationException("위를 향한 수평인 면(포켓 바닥)을 선택하세요.");
                    double step = Math.Clamp(op.Stepover, 0.05, 0.95) * tool.Diameter;
                    // rings from the wall inwards; cut from the centre outwards
                    var rings = new List<List<V3[]>>();
                    for (int k = 0; k < 2000; k++)
                    {
                        var loops = body.Solid.FaceOffset(pick, -(r + op.StockToLeave) - k * step, false);
                        if (loops == null || loops.Count == 0) break;
                        rings.Add(loops);
                    }
                    if (rings.Count == 0) throw new InvalidOperationException("공구가 포켓에 들어가지 않습니다 (더 작은 공구를 쓰세요).");
                    double floor = rings[0][0][0].Z + op.StockToLeave;
                    double above = shi.Z;
                    foreach (var z in Levels(shi.Z, floor, op.Stepdown))
                    {
                        for (int k = rings.Count - 1; k >= 0; k--)
                            foreach (var loop in rings[k])
                            {
                                // climb milling inside a wall runs counter-clockwise (spindle clockwise)
                                var path = Oriented(loop, op.Climb);
                                Enter(path[0] with { Z = z }, above);
                                foreach (var q in path.Skip(1)) Cut(q with { Z = z }, tool.Feed);
                                Leave();
                            }
                        above = z;
                    }
                    break;
                }
                case "contour":
                {
                    if (tool.Type == "drill") throw new InvalidOperationException("윤곽 가공에는 엔드밀을 쓰세요.");
                    var body = state.Find(op.Body) ?? throw new InvalidOperationException("윤곽을 딸 면을 선택하세요.");
                    if (op.Pick.Length != 3) throw new InvalidOperationException("윤곽을 딸 면을 선택하세요.");
                    var loops = body.Solid.FaceOffset(new V3(op.Pick[0], op.Pick[1], op.Pick[2]), r + op.StockToLeave, true)
                                ?? throw new InvalidOperationException("평면인 면을 선택하세요.");
                    if (loops.Count == 0) throw new InvalidOperationException("윤곽을 만들 수 없습니다.");
                    var outer = loops.OrderByDescending(l => Math.Abs(SignedArea(l))).First();
                    var path = Oriented(outer, !op.Climb);   // outside a part, climb runs clockwise
                    double bottom = mlo.Z - op.BottomOffset;
                    double above = shi.Z;
                    foreach (var z in Levels(shi.Z, bottom, op.Stepdown))
                    {
                        Enter(path[0] with { Z = z }, above);
                        foreach (var q in path.Skip(1)) Cut(q with { Z = z }, tool.Feed);
                        Leave();
                        above = z;
                    }
                    break;
                }
                case "drill":
                {
                    if (tool.Type != "drill") throw new InvalidOperationException("드릴 가공에는 드릴 공구를 쓰세요.");
                    double want = op.HoleDiameter > 0 ? op.HoleDiameter : tool.Diameter;
                    var holes = Parts(cam.Setup, state, hidden).SelectMany(b => b.Solid.Holes())
                        .Where(h => Math.Abs(h.r * 2 - want) < 0.05).ToList();
                    if (holes.Count == 0) throw new InvalidOperationException($"⌀{want:0.##} 구멍이 없습니다.");
                    // nearest-neighbour order from the work origin side
                    var order = new List<(double x, double y, double r, double top, double bottom)>();
                    var at = new V3(slo.X, slo.Y, 0);
                    var left = holes.ToList();
                    while (left.Count > 0)
                    {
                        var next = left.OrderBy(h => (h.x - at.X) * (h.x - at.X) + (h.y - at.Y) * (h.y - at.Y)).First();
                        order.Add(next);
                        left.Remove(next);
                        at = new V3(next.x, next.y, 0);
                    }
                    double tip = r / Math.Tan(59 * Math.PI / 180);
                    foreach (var h in order)
                    {
                        bool through = h.bottom <= mlo.Z + 1e-6;
                        double bottom = h.bottom - (through ? tip + 0.5 : 0);
                        double top = Math.Max(h.top, shi.Z);
                        Rapid(new V3(h.x, h.y, safe));
                        Rapid(new V3(h.x, h.y, top + cam.Setup.Retract));
                        if (op.Peck > 0)
                        {
                            for (double z = top - op.Peck; ; z -= op.Peck)
                            {
                                double zz = Math.Max(z, bottom);
                                Cut(new V3(h.x, h.y, zz), tool.Feed);
                                if (zz <= bottom + 1e-9) break;
                                Rapid(new V3(h.x, h.y, top + cam.Setup.Retract));
                                Rapid(new V3(h.x, h.y, zz + 0.5));
                            }
                        }
                        else Cut(new V3(h.x, h.y, bottom), tool.Feed);
                        Rapid(new V3(h.x, h.y, safe));
                    }
                    break;
                }
            }
            tp.Finish(start);
        }
        catch (InvalidOperationException ex)
        {
            tp.Moves.Clear();
            tp.Error = ex.Message;
        }
        return tp;
    }

    private static string F(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>G-code of the operations (in order) for the chosen controller, relative to the work origin.</summary>
    public static string Post(CamDoc cam, IReadOnlyList<(CamOperation op, Toolpath path)> ops, V3 origin, double safeZ, string designName)
    {
        var sb = new StringBuilder();
        string post = cam.Post;
        bool grbl = post == "grbl", fanuc = post == "fanuc";
        string C(string text) => $"({text.Replace('(', '[').Replace(')', ']')})";
        if (!grbl) sb.AppendLine("%");
        if (fanuc) sb.AppendLine($"O{cam.ProgramNumber:0000} {C(designName)}");
        else sb.AppendLine(C($"{designName} - OpenShape CAD"));
        sb.AppendLine(C($"후처리: {CamDoc.PostName(post)}, 단위 mm"));
        foreach (var t in ops.Select(o => cam.Tool(o.op.Tool)).Where(t => t != null).Distinct())
            sb.AppendLine(C($"T{t!.Number} {CamTool.TypeName(t.Type)} D={F(t.Diameter)}"));
        sb.AppendLine(fanuc ? "G21 G90 G17 G40 G49 G80" : "G21 G90 G17 G94");
        if (!grbl) sb.AppendLine("G54");
        int? currentTool = null;
        double lx = double.NaN, ly = double.NaN, lz = double.NaN, lf = double.NaN;
        int lg = -1;
        foreach (var (op, path) in ops)
        {
            if (path.Moves.Count == 0) continue;
            var tool = cam.Tool(op.Tool)!;
            sb.AppendLine();
            sb.AppendLine(C(op.Name));
            if (currentTool != tool.Number)
            {
                if (currentTool != null) sb.AppendLine("M5");
                if (grbl)
                {
                    // GRBL has no tool changer: pause for a manual change
                    if (currentTool != null) { sb.AppendLine(C($"공구 교체: T{tool.Number}")); sb.AppendLine("M0"); }
                }
                else sb.AppendLine($"T{tool.Number} M6");
                sb.AppendLine($"S{F(tool.Rpm)} M3");
                if (!grbl) sb.AppendLine($"G43 H{tool.Number} Z{F(safeZ - origin.Z)}");
                currentTool = tool.Number;
                lg = -1;
                lx = ly = lf = double.NaN;
                lz = safeZ - origin.Z;
            }
            foreach (var m in path.Moves)
            {
                var p = m.P - origin;
                int g = m.Rapid ? 0 : 1;
                var line = new StringBuilder();
                if (g != lg) line.Append(g == 0 ? "G0" : "G1");
                if (Math.Abs(p.X - lx) > 1e-6 || double.IsNaN(lx)) line.Append($" X{F(p.X)}");
                if (Math.Abs(p.Y - ly) > 1e-6 || double.IsNaN(ly)) line.Append($" Y{F(p.Y)}");
                if (Math.Abs(p.Z - lz) > 1e-6 || double.IsNaN(lz)) line.Append($" Z{F(p.Z)}");
                if (line.Length == 0 || line.ToString() is "G0" or "G1") continue;
                if (g == 1 && Math.Abs(m.Feed - lf) > 1e-6) { line.Append($" F{F(m.Feed)}"); lf = m.Feed; }
                sb.AppendLine(line.ToString().TrimStart());
                (lx, ly, lz, lg) = (p.X, p.Y, p.Z, g);
            }
        }
        sb.AppendLine();
        sb.AppendLine("M5");
        sb.AppendLine(fanuc ? "G91 G28 Z0" : $"G0 Z{F(safeZ - origin.Z)}");
        if (fanuc) sb.AppendLine("G90");
        sb.AppendLine("M30");
        if (!grbl) sb.AppendLine("%");
        return sb.ToString();
    }
}
