using System.Globalization;
using System.Text;

namespace OpenShape;

/// <summary>
/// USD stage with UsdPhysics: one rigid body per component (or loose body), collision meshes,
/// and revolute / prismatic / fixed joints from the design's joints. Millimetres, Z up.
/// Components that never move through a joint are fixed to the world.
/// </summary>
public static class UsdPhysics
{
    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Ident(string name, HashSet<string> used, string prefix = "Body")
    {
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        var s = sb.ToString().Trim('_');
        while (s.Contains("__")) s = s.Replace("__", "_");
        if (!s.Any(char.IsAsciiLetter)) s = prefix + "_" + (s.Length == 0 ? "0" : s);
        if (char.IsDigit(s[0])) s = "_" + s;
        var u = s;
        for (int k = 2; !used.Add(u); k++) u = $"{s}_{k}";
        return u;
    }

    /// <summary>Quaternion (w, x, y, z) of the frame whose X is the joint axis and Y the in-plane direction.</summary>
    private static (double w, double x, double y, double z) Frame(V3 axis, V3 u)
    {
        var x = axis.Norm();
        var y = (u - x * u.Dot(x)).Norm();
        var z = x.Cross(y);
        double m00 = x.X, m01 = y.X, m02 = z.X, m10 = x.Y, m11 = y.Y, m12 = z.Y, m20 = x.Z, m21 = y.Z, m22 = z.Z;
        double tr = m00 + m11 + m22;
        if (tr > 0)
        {
            double s = Math.Sqrt(tr + 1) * 2;
            return (0.25 * s, (m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s);
        }
        if (m00 > m11 && m00 > m22)
        {
            double s = Math.Sqrt(1 + m00 - m11 - m22) * 2;
            return ((m21 - m12) / s, 0.25 * s, (m01 + m10) / s, (m02 + m20) / s);
        }
        if (m11 > m22)
        {
            double s = Math.Sqrt(1 + m11 - m00 - m22) * 2;
            return ((m02 - m20) / s, (m01 + m10) / s, 0.25 * s, (m12 + m21) / s);
        }
        double t = Math.Sqrt(1 + m22 - m00 - m11) * 2;
        return ((m10 - m01) / t, (m02 + m20) / t, (m12 + m21) / t, 0.25 * t);
    }

    /// <summary>Quaternion (w, x, y, z) turning +X onto <paramref name="axis"/>.</summary>
    private static (double w, double x, double y, double z) XTo(V3 axis)
    {
        var a = V3.UnitX;
        var b = axis.Norm();
        double d = a.Dot(b);
        if (d > 1 - 1e-12) return (1, 0, 0, 0);
        if (d < -1 + 1e-12) return (0, 0, 0, 1);
        var c = a.Cross(b);
        double w = 1 + d;
        double n = Math.Sqrt(w * w + c.Dot(c));
        return (w / n, c.X / n, c.Y / n, c.Z / n);
    }

    public static bool Write(string path, ModelState s, Doc doc, Func<string, string> bodyName)
    {
        var visible = s.Bodies.Where(b => !doc.HiddenBodies.Contains(b.Id)).ToList();
        if (visible.Count == 0) return false;
        // rigid groups: components, then each loose body
        var groups = new List<(string name, List<Body> bodies, bool grounded)>();
        var taken = new HashSet<string>();
        foreach (var c in doc.Components)
        {
            var bs = visible.Where(b => c.Bodies.Contains(b.Id)).ToList();
            if (bs.Count == 0) continue;
            groups.Add((c.Name, bs, c.Grounded));
            foreach (var b in bs) taken.Add(b.Id);
        }
        foreach (var b in visible.Where(b => !taken.Contains(b.Id))) groups.Add((bodyName(b.Id), new List<Body> { b }, false));
        var moving = s.Joints.SelectMany(j => j.Moving).ToHashSet();
        var used = new HashSet<string>();
        var groupPrim = new Dictionary<string, string>();   // body id -> prim path

        var o = new StringBuilder();
        o.Append("#usda 1.0\n(\n    defaultPrim = \"World\"\n    metersPerUnit = 0.001\n    kilogramsPerUnit = 1\n    upAxis = \"Z\"\n    doc = \"Exported by OpenShape CAD (UsdPhysics)\"\n)\n\n");
        o.Append("def Xform \"World\"\n{\n");
        o.Append("    def PhysicsScene \"physicsScene\"\n    {\n        vector3f physics:gravityDirection = (0, 0, -1)\n        float physics:gravityMagnitude = 9810\n    }\n\n");
        foreach (var (name, bodies, grounded) in groups)
        {
            var prim = Ident(name, used, doc.Components.Any(c => c.Name == name) ? "Component" : "Body");
            foreach (var b in bodies) groupPrim[b.Id] = "/World/" + prim;
            o.Append($"    def Xform \"{prim}\" (\n        prepend apiSchemas = [\"PhysicsRigidBodyAPI\", \"PhysicsMassAPI\"]\n    )\n    {{\n");
            o.Append("        float physics:density = 0.0000078\n");   // kg/mm³ (steel)
            var meshNames = new HashSet<string>();
            foreach (var b in bodies)
            {
                var tris = b.Solid.Triangles();
                var map = new Dictionary<(long, long, long), int>();
                var pts = new List<V3>();
                var idx = new List<int>();
                for (int i = 0; i + 8 < tris.Length; i += 9)
                {
                    var tri = new int[3];
                    for (int k = 0; k < 3; k++)
                    {
                        var p = new V3(tris[i + k * 3], tris[i + k * 3 + 1], tris[i + k * 3 + 2]);
                        var key = ((long)Math.Round(p.X * 1e5), (long)Math.Round(p.Y * 1e5), (long)Math.Round(p.Z * 1e5));
                        if (!map.TryGetValue(key, out var n)) { n = pts.Count; map[key] = n; pts.Add(p); }
                        tri[k] = n;
                    }
                    if (tri[0] != tri[1] && tri[1] != tri[2] && tri[0] != tri[2]) idx.AddRange(tri);
                }
                var mesh = Ident(bodyName(b.Id), meshNames);
                var info = b.Solid.Info;
                o.Append($"        def Mesh \"{mesh}\" (\n            prepend apiSchemas = [\"PhysicsCollisionAPI\", \"PhysicsMeshCollisionAPI\"]\n        )\n        {{\n");
                o.Append($"            float3[] extent = [({F(info.Min.X)}, {F(info.Min.Y)}, {F(info.Min.Z)}), ({F(info.Max.X)}, {F(info.Max.Y)}, {F(info.Max.Z)})]\n");
                o.Append("            int[] faceVertexCounts = [").Append(string.Join(", ", Enumerable.Repeat("3", idx.Count / 3))).Append("]\n");
                o.Append("            int[] faceVertexIndices = [").Append(string.Join(", ", idx)).Append("]\n");
                o.Append("            point3f[] points = [").Append(string.Join(", ", pts.Select(p => $"({F(p.X)}, {F(p.Y)}, {F(p.Z)})"))).Append("]\n");
                o.Append("            uniform token physics:approximation = \"convexDecomposition\"\n");
                o.Append("            uniform token subdivisionScheme = \"none\"\n        }\n");
            }
            o.Append("    }\n\n");
            // a component that no joint moves stays put: fix it to the world
            if (grounded || !bodies.Any(b => moving.Contains(b.Id)))
                o.Append($"    def PhysicsFixedJoint \"{Ident(prim + "_ground", used)}\"\n    {{\n        rel physics:body1 = </World/{prim}>\n    }}\n\n");
        }
        foreach (var j in s.Joints)
        {
            var b0 = j.Fixed.Select(id => groupPrim.GetValueOrDefault(id)).FirstOrDefault(p => p != null);
            var b1 = j.Moving.Select(id => groupPrim.GetValueOrDefault(id)).FirstOrDefault(p => p != null);
            if (b0 == null || b1 == null || b0 == b1) continue;
            string type = j.Type switch
            {
                "slider" => "PhysicsPrismaticJoint",
                "rigid" => "PhysicsFixedJoint",
                "ball" => "PhysicsSphericalJoint",
                "revolute" => "PhysicsRevoluteJoint",
                _ => "PhysicsJoint",   // cylindrical, pin-slot, planar: a D6 joint with locked axes
            };
            var q = Frame(j.Axis, j.U);
            // D6 limits: X = joint axis, Y = in-plane U, Z = V; low > high locks that degree of freedom
            string[] locked = j.Type switch
            {
                "cylindrical" => new[] { "transY", "transZ", "rotY", "rotZ" },
                "pinslot" => new[] { "transX", "transZ", "rotY", "rotZ" },
                "planar" => new[] { "transX", "rotY", "rotZ" },
                _ => Array.Empty<string>(),
            };
            var schemas = locked.Length > 0 ? $" (\n        prepend apiSchemas = [{string.Join(", ", locked.Select(l => $"\"PhysicsLimitAPI:{l}\""))}]\n    )" : "";
            o.Append($"    def {type} \"{Ident(j.Name, used, "Joint")}\"{schemas}\n    {{\n");
            foreach (var l in locked) o.Append($"        float limit:{l}:physics:low = 1\n        float limit:{l}:physics:high = -1\n");
            o.Append($"        rel physics:body0 = <{b0}>\n        rel physics:body1 = <{b1}>\n");
            o.Append($"        point3f physics:localPos0 = ({F(j.Origin.X)}, {F(j.Origin.Y)}, {F(j.Origin.Z)})\n");
            o.Append($"        point3f physics:localPos1 = ({F(j.Origin.X)}, {F(j.Origin.Y)}, {F(j.Origin.Z)})\n");
            o.Append($"        quatf physics:localRot0 = ({F(q.w)}, {F(q.x)}, {F(q.y)}, {F(q.z)})\n");
            o.Append($"        quatf physics:localRot1 = ({F(q.w)}, {F(q.x)}, {F(q.y)}, {F(q.z)})\n");
            if (type is "PhysicsRevoluteJoint" or "PhysicsPrismaticJoint" or "PhysicsSphericalJoint")
            {
                o.Append("        uniform token physics:axis = \"X\"\n");
                if (j.Lower is { } lo) o.Append($"        float physics:lowerLimit = {F(lo)}\n");
                if (j.Upper is { } hi) o.Append($"        float physics:upperLimit = {F(hi)}\n");
            }
            o.Append("    }\n\n");
        }
        o.Append("}\n");
        try
        {
            File.WriteAllText(path, o.ToString());
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
