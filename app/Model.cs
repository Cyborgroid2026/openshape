using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenShape;

// ---------------------------------------------------------------- sketches

/// <summary>
/// One sketch curve. P holds the defining numbers:
/// line x1 y1 x2 y2 · rect x1 y1 x2 y2 · circle cx cy r · polygon cx cy r sides · arc x1 y1 x2 y2 xm ym (three-point arc).
/// </summary>
public sealed class SketchEntity
{
    public string Kind { get; set; } = "line";
    public double[] P { get; set; } = Array.Empty<double>();

    [JsonIgnore] public bool Closed => Kind is "rect" or "circle" or "polygon";

    public SketchEntity Clone() => new() { Kind = Kind, P = (double[])P.Clone() };

    /// <summary>Points along the curve (closed curves do not repeat the first point).</summary>
    public List<P2> Points()
    {
        var p = P;
        switch (Kind)
        {
            case "rect":
                return new List<P2> { new(p[0], p[1]), new(p[2], p[1]), new(p[2], p[3]), new(p[0], p[3]) };
            case "circle":
                return Enumerable.Range(0, 64).Select(i => new P2(p[0] + p[2] * Math.Cos(i * Math.PI / 32), p[1] + p[2] * Math.Sin(i * Math.PI / 32))).ToList();
            case "polygon":
            {
                int n = Math.Clamp((int)p[3], 3, 64);
                return Enumerable.Range(0, n).Select(i =>
                {
                    double a = Math.PI / 2 + i * 2 * Math.PI / n;
                    return new P2(p[0] + p[2] * Math.Cos(a), p[1] + p[2] * Math.Sin(a));
                }).ToList();
            }
            case "arc":
                return Arc(new P2(p[0], p[1]), new P2(p[2], p[3]), new P2(p[4], p[5]));
            default:
                return new List<P2> { new(p[0], p[1]), new(p[2], p[3]) };
        }
    }

    /// <summary>Tessellate the circle through a, m, b from a to b passing m.</summary>
    public static List<P2> Arc(P2 a, P2 b, P2 m)
    {
        double d = 2 * (a.X * (m.Y - b.Y) + m.X * (b.Y - a.Y) + b.X * (a.Y - m.Y));
        if (Math.Abs(d) < 1e-9) return new List<P2> { a, b };
        double a2 = a.X * a.X + a.Y * a.Y, m2 = m.X * m.X + m.Y * m.Y, b2 = b.X * b.X + b.Y * b.Y;
        var c = new P2((a2 * (m.Y - b.Y) + m2 * (b.Y - a.Y) + b2 * (a.Y - m.Y)) / d, (a2 * (b.X - m.X) + m2 * (a.X - b.X) + b2 * (m.X - a.X)) / d);
        double r = c.Dist(a);
        double t0 = Math.Atan2(a.Y - c.Y, a.X - c.X), t1 = Math.Atan2(b.Y - c.Y, b.X - c.X), tm = Math.Atan2(m.Y - c.Y, m.X - c.X);
        double Norm(double t) => (t % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
        double sweep = Norm(t1 - t0);
        if (Norm(tm - t0) > sweep) sweep -= 2 * Math.PI;   // go the other way round
        int n = Math.Max(4, (int)Math.Ceiling(Math.Abs(sweep) / (2 * Math.PI) * 64));
        var pts = new List<P2>();
        for (int i = 0; i <= n; i++)
        {
            double t = t0 + sweep * i / n;
            pts.Add(new P2(c.X + r * Math.Cos(t), c.Y + r * Math.Sin(t)));
        }
        pts[0] = a;
        pts[^1] = b;
        return pts;
    }

    /// <summary>Points worth snapping to (ends, corners, centres).</summary>
    public IEnumerable<P2> SnapPoints()
    {
        var p = P;
        switch (Kind)
        {
            case "circle":
            case "polygon":
                yield return new P2(p[0], p[1]);
                if (Kind == "polygon") foreach (var q in Points()) yield return q;
                break;
            case "rect":
                foreach (var q in Points()) yield return q;
                break;
            case "arc":
                yield return new P2(p[0], p[1]);
                yield return new P2(p[2], p[3]);
                break;
            default:
                yield return new P2(p[0], p[1]);
                yield return new P2(p[2], p[3]);
                break;
        }
    }

    public static string DisplayName(string kind) => kind switch
    {
        "line" => "선",
        "rect" => "직사각형",
        "circle" => "원",
        "polygon" => "다각형",
        "arc" => "호",
        _ => kind,
    };
}

/// <summary>
/// One exact curve of a profile: 0 line (x1 y1 x2 y2), 1 three-point arc (x1 y1 xm ym x2 y2), 2 circle (cx cy r).
/// </summary>
public sealed record Piece(int Kind, double[] P)
{
    public Piece Reversed() => Kind switch
    {
        0 => new Piece(0, new[] { P[2], P[3], P[0], P[1] }),
        1 => new Piece(1, new[] { P[4], P[5], P[2], P[3], P[0], P[1] }),
        _ => this,
    };

    /// <summary>Pieces of a sketch entity in drawing order.</summary>
    public static List<Piece> Of(SketchEntity e)
    {
        var p = e.P;
        switch (e.Kind)
        {
            case "circle":
                return new List<Piece> { new(2, new[] { p[0], p[1], p[2] }) };
            case "arc":
                // stored as start, end, through point
                return new List<Piece> { new(1, new[] { p[0], p[1], p[4], p[5], p[2], p[3] }) };
            case "line":
                return new List<Piece> { new(0, new[] { p[0], p[1], p[2], p[3] }) };
            default:
            {
                var pts = e.Points();
                return pts.Select((a, i) => { var b = pts[(i + 1) % pts.Count]; return new Piece(0, new[] { a.X, a.Y, b.X, b.Y }); }).ToList();
            }
        }
    }
}

/// <summary>A connected run of sketch curves (Points is a polyline for display and picking).</summary>
public sealed record Chain(int Index, List<P2> Points, List<Piece> Pieces, bool Closed);

/// <summary>A closed loop of a sketch; Holes are the loops directly inside it. Loop is a polyline for display.</summary>
public sealed record Profile(int Index, List<P2> Loop, double Area, List<int> Holes, List<Piece> Pieces)
{
    public bool Contains(P2 p) => Profiles.Inside(Loop, p);
}

public static class Profiles
{
    public static double SignedArea(IReadOnlyList<P2> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var (a, b) = (p[i], p[(i + 1) % p.Count]);
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    public static bool Inside(IReadOnlyList<P2> poly, P2 p)
    {
        bool c = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var (a, b) = (poly[i], poly[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) c = !c;
        }
        return c;
    }

    /// <summary>Closed loops of the sketch: closed curves plus chains of lines/arcs that return to their start.</summary>
    public static List<Profile> Compute(SketchFeature s)
    {
        var loops = new List<(List<P2> pts, List<Piece> pieces)>();
        foreach (var e in s.Entities.Where(e => e.Closed)) loops.Add((e.Points(), Piece.Of(e)));

        const double tol = 1e-6;
        var open = s.Entities.Where(e => !e.Closed).Select(e => (pts: e.Points(), piece: Piece.Of(e)[0]))
            .Where(o => o.pts.Count >= 2 && o.pts[0].Dist(o.pts[^1]) > tol).ToList();
        var used = new bool[open.Count];
        for (int i = 0; i < open.Count; i++)
        {
            if (used[i]) continue;
            var chain = new List<P2>(open[i].pts);
            var pieces = new List<Piece> { open[i].piece };
            var parts = new List<int> { i };
            used[i] = true;
            bool grown = true;
            while (grown && chain[0].Dist(chain[^1]) > tol)
            {
                grown = false;
                for (int j = 0; j < open.Count; j++)
                {
                    if (used[j]) continue;
                    var q = open[j].pts;
                    if (q[0].Dist(chain[^1]) <= tol)
                    {
                        chain.AddRange(q.Skip(1));
                        pieces.Add(open[j].piece);
                    }
                    else if (q[^1].Dist(chain[^1]) <= tol)
                    {
                        chain.AddRange(Enumerable.Reverse(q).Skip(1));
                        pieces.Add(open[j].piece.Reversed());
                    }
                    else continue;
                    used[j] = true;
                    parts.Add(j);
                    grown = true;
                    break;
                }
            }
            if (chain.Count >= 4 && chain[0].Dist(chain[^1]) <= tol)
            {
                chain.RemoveAt(chain.Count - 1);
                loops.Add((chain, pieces));
            }
            else
                foreach (var k in parts.Skip(1)) used[k] = false;   // let the other pieces form their own loops
        }

        // orient counter-clockwise and find nesting
        var areas = new List<double>();
        for (int i = 0; i < loops.Count; i++)
        {
            double a = SignedArea(loops[i].pts);
            if (a < 0)
            {
                loops[i].pts.Reverse();
                loops[i] = (loops[i].pts, Enumerable.Reverse(loops[i].pieces).Select(p => p.Reversed()).ToList());
            }
            areas.Add(Math.Abs(a));
        }
        var parent = new int[loops.Count];
        for (int i = 0; i < loops.Count; i++)
        {
            parent[i] = -1;
            double best = double.MaxValue;
            for (int j = 0; j < loops.Count; j++)
            {
                if (i == j || areas[j] <= areas[i] || areas[j] >= best) continue;
                var (li, lj) = (loops[i].pts, loops[j].pts);
                if (li.All(p => Inside(lj, p)) || Inside(lj, Centroid(li)) && Inside(lj, li[0]))
                {
                    parent[i] = j;
                    best = areas[j];
                }
            }
        }
        return loops.Select((l, i) => new Profile(i, l.pts, areas[i], Enumerable.Range(0, loops.Count).Where(k => parent[k] == i).ToList(), l.pieces)).ToList();
    }

    public static P2 Centroid(IReadOnlyList<P2> p) => new(p.Average(q => q.X), p.Average(q => q.Y));

    /// <summary>The region of a profile: its outer loop first, then the loops directly inside it (holes).</summary>
    public static List<Profile> Region(List<Profile> all, int index)
    {
        var pr = all[index];
        var r = new List<Profile> { pr };
        r.AddRange(pr.Holes.Select(h => all[h]));
        return r;
    }

    /// <summary>
    /// Connected curve chains of a sketch (open chains and closed loops), for paths, ribs and
    /// surface extrusions.
    /// </summary>
    public static List<Chain> Chains(SketchFeature s)
    {
        var result = new List<Chain>();
        foreach (var e in s.Entities.Where(e => e.Closed))
        {
            var pts = e.Points();
            pts.Add(pts[0]);
            result.Add(new Chain(result.Count, pts, Piece.Of(e), true));
        }
        const double tol = 1e-6;
        var open = s.Entities.Where(e => !e.Closed).Select(e => (pts: e.Points(), piece: Piece.Of(e)[0])).Where(o => o.pts.Count >= 2).ToList();
        var used = new bool[open.Count];
        for (int i = 0; i < open.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var pts = new List<P2>(open[i].pts);
            var pieces = new List<Piece> { open[i].piece };
            bool grown = true;
            while (grown)
            {
                grown = false;
                for (int j = 0; j < open.Count; j++)
                {
                    if (used[j]) continue;
                    var q = open[j].pts;
                    if (q[0].Dist(pts[^1]) <= tol) { pts.AddRange(q.Skip(1)); pieces.Add(open[j].piece); }
                    else if (q[^1].Dist(pts[^1]) <= tol) { pts.AddRange(Enumerable.Reverse(q).Skip(1)); pieces.Add(open[j].piece.Reversed()); }
                    else if (q[^1].Dist(pts[0]) <= tol) { pts.InsertRange(0, q.Take(q.Count - 1)); pieces.Insert(0, open[j].piece); }
                    else if (q[0].Dist(pts[0]) <= tol) { pts.InsertRange(0, Enumerable.Reverse(q).Take(q.Count - 1)); pieces.Insert(0, open[j].piece.Reversed()); }
                    else continue;
                    used[j] = true;
                    grown = true;
                }
            }
            result.Add(new Chain(result.Count, pts, pieces, pts[0].Dist(pts[^1]) <= tol));
        }
        return result;
    }

    /// <summary>Innermost profile containing p, or -1.</summary>
    public static int Hit(List<Profile> all, P2 p) =>
        all.Where(pr => pr.Contains(p)).OrderBy(pr => pr.Area).Select(pr => pr.Index).DefaultIfEmpty(-1).First();
}

// ---------------------------------------------------------------- features

public enum BodyOp { NewBody, Join, Cut, Intersect }

public sealed class ProfileRef
{
    public string Sketch { get; set; } = "";
    public int Loop { get; set; }
    public ProfileRef() { }
    public ProfileRef(string sketch, int loop) { Sketch = sketch; Loop = loop; }
    public override bool Equals(object? o) => o is ProfileRef r && r.Sketch == Sketch && r.Loop == Loop;
    public override int GetHashCode() => HashCode.Combine(Sketch, Loop);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SketchFeature), "sketch")]
[JsonDerivedType(typeof(ExtrudeFeature), "extrude")]
[JsonDerivedType(typeof(RevolveFeature), "revolve")]
[JsonDerivedType(typeof(PrimitiveFeature), "primitive")]
[JsonDerivedType(typeof(MoveFeature), "move")]
[JsonDerivedType(typeof(MirrorFeature), "mirror")]
[JsonDerivedType(typeof(PatternFeature), "pattern")]
[JsonDerivedType(typeof(CombineFeature), "combine")]
[JsonDerivedType(typeof(ScaleFeature), "scale")]
[JsonDerivedType(typeof(DeleteFeature), "delete")]
[JsonDerivedType(typeof(ImportFeature), "import")]
[JsonDerivedType(typeof(FilletFeature), "fillet")]
[JsonDerivedType(typeof(ShellFeature), "shell")]
[JsonDerivedType(typeof(JointFeature), "joint")]
[JsonDerivedType(typeof(LoftFeature), "loft")]
[JsonDerivedType(typeof(SweepFeature), "sweep")]
[JsonDerivedType(typeof(SurfaceFeature), "surface")]
[JsonDerivedType(typeof(ThickenFeature), "thicken")]
[JsonDerivedType(typeof(PressPullFeature), "presspull")]
[JsonDerivedType(typeof(FlangeFeature), "flange")]
[JsonDerivedType(typeof(RibFeature), "rib")]
[JsonDerivedType(typeof(BossFeature), "boss")]
public abstract class Feature
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    /// <summary>Fields driven by expressions (property name → expression using user parameters).</summary>
    public Dictionary<string, string> Expressions { get; set; } = new();
    [JsonIgnore] public string? Error { get; set; }

    /// <summary>Command id (also the icon key).</summary>
    [JsonIgnore] public abstract string Command { get; }

    public Feature Copy()
    {
        var json = JsonSerializer.Serialize(this, typeof(Feature), Doc.Json);
        return (Feature)JsonSerializer.Deserialize(json, typeof(Feature), Doc.Json)!;
    }
}

public sealed class SketchFeature : Feature
{
    public string Plane { get; set; } = "XY";
    public double Offset { get; set; }
    public List<SketchEntity> Entities { get; set; } = new();
    public bool Visible { get; set; } = true;
    /// <summary>Frame (origin, u, v, n) when the sketch sits on a model face (Plane = "face").</summary>
    public double[]? Custom { get; set; }
    public List<SketchConstraint> Constraints { get; set; } = new();
    public override string Command => "sketch";

    [JsonIgnore]
    public PlaneFrame Frame
    {
        get
        {
            if (Plane != "face" || Custom is not { Length: 12 } c) return PlaneFrame.Of(Plane, Offset);
            var n = new V3(c[9], c[10], c[11]);
            return new PlaneFrame(new V3(c[0], c[1], c[2]) + n * Offset, new V3(c[3], c[4], c[5]), new V3(c[6], c[7], c[8]), n);
        }
    }
}

public sealed class ExtrudeFeature : Feature
{
    public List<ProfileRef> Profiles { get; set; } = new();
    public double Distance { get; set; } = 10;
    public int Direction { get; set; }      // 0 one side, 1 symmetric, 2 two sides
    public double Distance2 { get; set; } = 10;
    public BodyOp Operation { get; set; }
    public List<string> Targets { get; set; } = new();
    public override string Command => "extrude";
}

public sealed class RevolveFeature : Feature
{
    public List<ProfileRef> Profiles { get; set; } = new();
    public int Axis { get; set; } = 1;      // 0 sketch X axis, 1 sketch Y axis, 2+k = sketch line k
    public double Angle { get; set; } = 360;
    public BodyOp Operation { get; set; }
    public List<string> Targets { get; set; } = new();
    public override string Command => "revolve";
}

public sealed class PrimitiveFeature : Feature
{
    public string Shape { get; set; } = "box";   // box, cylinder, sphere, cone, torus
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double A { get; set; } = 20;          // box length / radius / major radius
    public double B { get; set; } = 20;          // box width / height / top radius / minor radius
    public double C { get; set; } = 20;          // box height / cone height
    public BodyOp Operation { get; set; }
    public List<string> Targets { get; set; } = new();
    public override string Command => Shape;
}

public sealed class MoveFeature : Feature
{
    public List<string> Bodies { get; set; } = new();
    public double Tx { get; set; }
    public double Ty { get; set; }
    public double Tz { get; set; }
    public double Rx { get; set; }
    public double Ry { get; set; }
    public double Rz { get; set; }
    public bool CreateCopy { get; set; }
    public override string Command => "move";
}

public sealed class MirrorFeature : Feature
{
    public List<string> Bodies { get; set; } = new();
    public string Plane { get; set; } = "YZ";
    public bool Join { get; set; }
    public override string Command => "mirror";
}

public sealed class PatternFeature : Feature
{
    public List<string> Bodies { get; set; } = new();
    public bool Circular { get; set; }
    public string Dir1 { get; set; } = "X";
    public int Count1 { get; set; } = 3;
    public double Spacing1 { get; set; } = 30;
    public string Dir2 { get; set; } = "Y";
    public int Count2 { get; set; } = 1;
    public double Spacing2 { get; set; } = 30;
    public string Axis { get; set; } = "Z";
    public int Count { get; set; } = 6;
    public double Angle { get; set; } = 360;
    public override string Command => Circular ? "circpattern" : "rectpattern";
}

public sealed class CombineFeature : Feature
{
    public string Target { get; set; } = "";
    public List<string> Tools { get; set; } = new();
    public BodyOp Operation { get; set; } = BodyOp.Join;
    public bool KeepTools { get; set; }
    public override string Command => "combine";
}

public sealed class ScaleFeature : Feature
{
    public List<string> Bodies { get; set; } = new();
    public double Factor { get; set; } = 2;
    public override string Command => "scale";
}

public sealed class DeleteFeature : Feature
{
    public List<string> Bodies { get; set; } = new();
    public override string Command => "delete";
}

/// <summary>Fillet or chamfer. Edges are remembered by a point on each edge (re-found on replay).</summary>
public sealed class FilletFeature : Feature
{
    public string Body { get; set; } = "";
    public List<double[]> Edges { get; set; } = new();
    public bool AllEdges { get; set; }
    public double Size { get; set; } = 2;
    public bool Chamfer { get; set; }
    public override string Command => Chamfer ? "chamfer" : "fillet";
}

/// <summary>Hollow a body; removed faces are remembered by a point on each face.</summary>
public sealed class ShellFeature : Feature
{
    public string Body { get; set; } = "";
    public List<double[]> Faces { get; set; } = new();
    public double Thickness { get; set; } = 2;
    public bool Outside { get; set; }
    public override string Command => "shell";
}

/// <summary>
/// Joint: moves component 1 so its joint origin meets component 2's, then applies the motion
/// (rotation about / sliding along the joint axis). Origins are re-found from the picked points.
/// </summary>
public sealed class JointFeature : Feature
{
    public string BodyA { get; set; } = "";
    public double[] PickA { get; set; } = Array.Empty<double>();
    public string BodyB { get; set; } = "";
    public double[] PickB { get; set; } = Array.Empty<double>();
    public string Type { get; set; } = "revolute";   // rigid, revolute, slider, cylindrical, pinslot, planar, ball
    public double Angle { get; set; }
    public double Angle2 { get; set; }
    public double Angle3 { get; set; }
    public double Distance { get; set; }
    public double Distance2 { get; set; }
    public double Offset { get; set; }
    public bool Flip { get; set; }
    public double? LowerLimit { get; set; }
    public double? UpperLimit { get; set; }
    public override string Command => "joint";

    public static string TypeName(string t) => t switch
    {
        "rigid" => "강체",
        "slider" => "슬라이더",
        "cylindrical" => "원통형",
        "pinslot" => "핀-슬롯",
        "planar" => "평면형",
        "ball" => "볼",
        _ => "회전",
    };

    public static readonly string[] Types = { "rigid", "revolute", "slider", "cylindrical", "pinslot", "planar", "ball" };

    /// <summary>A unit vector perpendicular to n (the joint's in-plane X direction).</summary>
    public static V3 Perp(V3 n) => (Math.Abs(n.Z) < 0.9 ? n.Cross(V3.UnitZ) : n.Cross(V3.UnitX)).Norm();
}

public sealed class ChainRef
{
    public string Sketch { get; set; } = "";
    public int Chain { get; set; }
    public ChainRef() { }
    public ChainRef(string sketch, int chain) { Sketch = sketch; Chain = chain; }
    public override bool Equals(object? o) => o is ChainRef r && r.Sketch == Sketch && r.Chain == Chain;
    public override int GetHashCode() => HashCode.Combine(Sketch, Chain);
}

public sealed class LoftFeature : Feature
{
    public List<ProfileRef> Profiles { get; set; } = new();
    public BodyOp Operation { get; set; }
    public List<string> Targets { get; set; } = new();
    public override string Command => "loft";
}

public sealed class SweepFeature : Feature
{
    public List<ProfileRef> Profiles { get; set; } = new();
    public List<ChainRef> Path { get; set; } = new();
    public BodyOp Operation { get; set; }
    public List<string> Targets { get; set; } = new();
    public override string Command => "sweep";
}

/// <summary>Surface body: extruded from curves, or a patch filling a closed curve.</summary>
public sealed class SurfaceFeature : Feature
{
    public bool Patch { get; set; }
    public List<ChainRef> Chains { get; set; } = new();
    public double Distance { get; set; } = 10;
    public override string Command => Patch ? "patch" : "surfextrude";
}

public sealed class ThickenFeature : Feature
{
    public List<string> Bodies { get; set; } = new();
    public double Thickness { get; set; } = 1;
    public override string Command => "thicken";
}

public sealed class PressPullFeature : Feature
{
    public string Body { get; set; } = "";
    public double[] Pick { get; set; } = Array.Empty<double>();
    public double Distance { get; set; } = 5;
    public override string Command => "presspull";
}

/// <summary>Sheet metal: a base flange (profile at sheet thickness) or an edge flange (bend + wall).</summary>
public sealed class FlangeFeature : Feature
{
    public bool Edge { get; set; }
    public List<ProfileRef> Profiles { get; set; } = new();
    public string Body { get; set; } = "";
    public double[] Pick { get; set; } = Array.Empty<double>();
    public double Thickness { get; set; } = 1;
    public double BendRadius { get; set; } = 1;
    public double Height { get; set; } = 10;
    public bool Down { get; set; }
    public override string Command => Edge ? "edgeflange" : "flange";

    /// <summary>Flat (developed) length of an edge flange with K-factor 0.44.</summary>
    public double DevelopedLength => Math.PI / 2 * (BendRadius + 0.44 * Thickness) + Math.Max(0, Height - BendRadius - Thickness);
}

/// <summary>Plastic rib: a wall of given thickness grown from an open sketch curve into the part.</summary>
public sealed class RibFeature : Feature
{
    public List<ChainRef> Chains { get; set; } = new();
    public double Thickness { get; set; } = 2;
    public double Depth { get; set; } = 10;
    public bool Flip { get; set; }
    public override string Command => "rib";
}

/// <summary>Plastic boss: a cylinder with a pilot hole on a planar face.</summary>
public sealed class BossFeature : Feature
{
    public string Body { get; set; } = "";
    public double[] Pick { get; set; } = Array.Empty<double>();
    public double Diameter { get; set; } = 8;
    public double HoleDiameter { get; set; } = 3;
    public double Height { get; set; } = 10;
    public override string Command => "boss";
}

public sealed class UserParameter
{
    public string Name { get; set; } = "";
    public string Expression { get; set; } = "0";
    public string Unit { get; set; } = "mm";
    public string Comment { get; set; } = "";
}

/// <summary>Evaluates user parameters and field expressions.</summary>
public static class Params
{
    private static readonly System.Text.RegularExpressions.Regex Word = new(@"[A-Za-z_가-힣][A-Za-z0-9_가-힣]*");

    /// <summary>Value of an expression with parameter names replaced (units mm/deg are ignored).</summary>
    public static double? Eval(string? expr, IReadOnlyDictionary<string, double> values)
    {
        if (string.IsNullOrWhiteSpace(expr)) return null;
        var t = expr.Replace("mm", " ").Replace("deg", " ").Replace("°", " ").Replace(",", ".");
        bool missing = false;
        t = Word.Replace(t, m =>
        {
            if (values.TryGetValue(m.Value, out var v)) return "(" + v.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")";
            missing = true;
            return m.Value;
        });
        if (missing) return null;
        try
        {
            var r = new System.Data.DataTable().Compute(t, null);
            double d = Convert.ToDouble(r, System.Globalization.CultureInfo.InvariantCulture);
            return double.IsFinite(d) ? d : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool HasName(string text) => Word.IsMatch(text.Replace("mm", "").Replace("deg", ""));

    /// <summary>All user parameter values (parameters may use earlier or later ones).</summary>
    public static Dictionary<string, double> Compute(IEnumerable<UserParameter> ps)
    {
        var list = ps.ToList();
        var values = new Dictionary<string, double>();
        for (int pass = 0; pass < list.Count + 1; pass++)
        {
            bool changed = false;
            foreach (var p in list)
                if (!values.ContainsKey(p.Name) && Eval(p.Expression, values) is { } v)
                {
                    values[p.Name] = v;
                    changed = true;
                }
            if (!changed) break;
        }
        return values;
    }

    /// <summary>Write expression results into the feature's numeric properties.</summary>
    public static void Apply(Feature f, IReadOnlyDictionary<string, double> values)
    {
        foreach (var (prop, expr) in f.Expressions)
        {
            var pi = f.GetType().GetProperty(prop);
            if (pi == null) continue;
            var v = Eval(expr, values) ?? throw new InvalidOperationException($"식을 계산할 수 없습니다: {expr}");
            if (pi.PropertyType == typeof(double)) pi.SetValue(f, v);
            else if (pi.PropertyType == typeof(int)) pi.SetValue(f, (int)Math.Round(v));
            else if (pi.PropertyType == typeof(double?)) pi.SetValue(f, (double?)v);
        }
        if (f is SketchFeature sk && sk.Constraints.Any(c => c.Expression != null))
        {
            foreach (var c in sk.Constraints.Where(c => c.Expression != null))
                c.Value = Eval(c.Expression, values) ?? throw new InvalidOperationException($"치수 식을 계산할 수 없습니다: {c.Expression}");
            if (SketchSolver.Solve(sk) < 0) throw new InvalidOperationException("매개변수 값으로 스케치를 풀 수 없습니다.");
        }
    }
}

public sealed class ComponentDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "구성요소1";
    public List<string> Bodies { get; set; } = new();
    public bool Grounded { get; set; }
}

/// <summary>A joint after evaluation (for display and physics export).</summary>
public sealed record JointInfo(string Name, string Type, V3 Origin, V3 Axis, List<string> Moving, List<string> Fixed, double? Lower, double? Upper)
{
    /// <summary>In-plane direction (slot direction for pin-slot, X for planar).</summary>
    public V3 U => JointFeature.Perp(Axis);
}

public sealed class ImportFeature : Feature
{
    public string Path { get; set; } = "";
    public double Scale { get; set; } = 1;
    public override string Command => "insertmesh";
}

// ---------------------------------------------------------------- document

public sealed class Doc
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Format { get; set; } = "openshape-cad/1";
    public string Units { get; set; } = "mm";
    public List<Feature> Features { get; set; } = new();
    public int Marker { get; set; }
    public Dictionary<string, string> BodyNames { get; set; } = new();
    public List<ComponentDef> Components { get; set; } = new();
    public List<UserParameter> Parameters { get; set; } = new();
    /// <summary>Sheet metal rule (판금 규칙).</summary>
    public double SheetThickness { get; set; } = 1;
    public double SheetBendRadius { get; set; } = 1;
    public Dictionary<string, string> BodyColors { get; set; } = new();
    public HashSet<string> HiddenBodies { get; set; } = new();
    public bool OriginVisible { get; set; }

    public string Save() => JsonSerializer.Serialize(this, Json);
    public static Doc Load(string json) => JsonSerializer.Deserialize<Doc>(json, Json) ?? new Doc();
    public Doc Clone() => Load(Save());

    public static readonly Dictionary<string, string> CommandNames = new()
    {
        ["sketch"] = "스케치",
        ["extrude"] = "돌출",
        ["revolve"] = "회전",
        ["box"] = "상자",
        ["cylinder"] = "원통",
        ["sphere"] = "구",
        ["cone"] = "원뿔",
        ["torus"] = "원환",
        ["move"] = "이동/복사",
        ["mirror"] = "미러",
        ["rectpattern"] = "직사각형 패턴",
        ["circpattern"] = "원형 패턴",
        ["combine"] = "결합",
        ["scale"] = "축척",
        ["delete"] = "제거",
        ["insertmesh"] = "메쉬 삽입",
        ["fillet"] = "필렛",
        ["chamfer"] = "챔퍼",
        ["shell"] = "쉘",
        ["insert"] = "삽입",
        ["joint"] = "접합",
        ["loft"] = "로프트",
        ["sweep"] = "스윕",
        ["surfextrude"] = "돌출",
        ["patch"] = "패치",
        ["thicken"] = "두껍게 하기",
        ["presspull"] = "밀고 당기기",
        ["flange"] = "플랜지",
        ["edgeflange"] = "모서리 플랜지",
        ["rib"] = "리브",
        ["boss"] = "보스",
    };

    /// <summary>Next free default name for a feature of this command ("돌출1", "돌출2" ...).</summary>
    public string NextName(string command)
    {
        var baseName = CommandNames.GetValueOrDefault(command, command);
        int n = 1;
        while (Features.Any(f => f.Name == baseName + n)) n++;
        return baseName + n;
    }
}

// ---------------------------------------------------------------- evaluation

public sealed class Body
{
    public required string Id { get; init; }
    public required Solid Solid { get; set; }
    public Body With(Solid s) => new() { Id = Id, Solid = s };
}

/// <summary>How a sheet metal body was built (for its flat pattern).</summary>
public sealed record SheetInfo(List<(PlaneFrame frame, List<Profile> region)> Base, double Thickness, List<FlangeInfo> Flanges);

public sealed record FlangeInfo(V3 Origin, V3 Outward, V3 Normal, V3 Dir, double Length, double Developed);

public sealed class ModelState
{
    public Dictionary<string, SheetInfo> Sheets { get; } = new();

    public List<Body> Bodies { get; } = new();
    public List<SketchFeature> Sketches { get; } = new();
    public List<JointInfo> Joints { get; } = new();
    public Dictionary<string, List<Profile>> SketchProfiles { get; } = new();

    public ModelState Clone()
    {
        var s = new ModelState();
        s.Bodies.AddRange(Bodies.Select(b => b.With(b.Solid)));
        s.Sketches.AddRange(Sketches);
        s.Joints.AddRange(Joints);
        foreach (var (k, v) in Sheets) s.Sheets[k] = v;
        foreach (var (k, v) in SketchProfiles) s.SketchProfiles[k] = v;
        return s;
    }

    public Body? Find(string id) => Bodies.FirstOrDefault(b => b.Id == id);
}

public static class Evaluator
{
    /// <summary>User parameter values of the document being evaluated.</summary>
    public static IReadOnlyDictionary<string, double> ParameterValues { get; set; } = new Dictionary<string, double>();

    /// <summary>Components of the document being evaluated (joints move a whole component).</summary>
    public static IReadOnlyList<ComponentDef> Components { get; set; } = Array.Empty<ComponentDef>();

    /// <summary>The bodies that move together with <paramref name="body"/>.</summary>
    public static List<string> GroupOf(string body)
    {
        var c = Components.FirstOrDefault(c => c.Bodies.Contains(body));
        return c != null ? c.Bodies.ToList() : new List<string> { body };
    }

    /// <summary>Rotation about <paramref name="center"/> turning direction a onto direction b.</summary>
    public static Affine Align(V3 center, V3 a, V3 b)
    {
        a = a.Norm();
        b = b.Norm();
        var axis = a.Cross(b);
        double dot = Math.Clamp(a.Dot(b), -1, 1);
        if (axis.Length < 1e-9)
        {
            if (dot > 0) return Affine.Identity;
            var perp = Math.Abs(a.X) < 0.9 ? a.Cross(V3.UnitX) : a.Cross(V3.UnitY);
            return Affine.Rotation(center, perp, 180);
        }
        return Affine.Rotation(center, axis, Math.Acos(dot) * 180 / Math.PI);
    }

    /// <summary>Run features [from, to) on top of <paramref name="start"/> (which is not modified).</summary>
    public static ModelState Run(IReadOnlyList<Feature> features, int to, ModelState? start = null, int from = 0)
    {
        var s = start?.Clone() ?? new ModelState();
        for (int i = from; i < Math.Min(to, features.Count); i++)
        {
            var f = features[i];
            try
            {
                f.Error = null;
                Params.Apply(f, ParameterValues);
                Apply(s, f);
            }
            catch (Exception ex)
            {
                f.Error = ex.Message;
            }
        }
        return s;
    }

    private static List<Profile> RegionOf(ModelState s, ProfileRef r, out SketchFeature sketch)
    {
        sketch = s.Sketches.FirstOrDefault(k => k.Id == r.Sketch) ?? throw new InvalidOperationException("참조한 스케치가 없습니다.");
        var all = s.SketchProfiles[sketch.Id];
        if (r.Loop < 0 || r.Loop >= all.Count) throw new InvalidOperationException("참조한 프로필이 스케치에서 사라졌습니다.");
        return Profiles.Region(all, r.Loop);
    }

    private static void Apply(ModelState s, Feature f)
    {
        switch (f)
        {
            case SketchFeature sk:
                s.Sketches.Add(sk);
                s.SketchProfiles[sk.Id] = Profiles.Compute(sk);
                break;

            case ExtrudeFeature ex:
            {
                if (ex.Profiles.Count == 0) throw new InvalidOperationException("프로필을 선택하세요.");
                (double h0, double h1) = ex.Direction switch
                {
                    1 => (-ex.Distance / 2, ex.Distance / 2),
                    2 => (-ex.Distance2, ex.Distance),
                    _ => (Math.Min(0, ex.Distance), Math.Max(0, ex.Distance)),
                };
                if (Math.Abs(h1 - h0) < 1e-9) throw new InvalidOperationException("거리가 0입니다.");
                Solid? tool = null;
                foreach (var r in ex.Profiles)
                {
                    var region = RegionOf(s, r, out var sketch);
                    var part = Solid.Extrude(sketch.Frame, region, h0, h1);
                    if (part == null) continue;
                    tool = tool == null ? part : tool.Boolean(part, BoolOp.Union);
                }
                if (tool == null) throw new InvalidOperationException("프로필에서 바디를 만들 수 없습니다.");
                Combine(s, f.Id, tool, ex.Operation, ex.Targets);
                break;
            }

            case RevolveFeature rv:
            {
                if (rv.Profiles.Count == 0) throw new InvalidOperationException("프로필을 선택하세요.");
                Solid? tool = null;
                foreach (var r in rv.Profiles)
                {
                    var region = RegionOf(s, r, out var sketch);
                    (P2 a, P2 d) = rv.Axis switch
                    {
                        0 => (new P2(0, 0), new P2(1, 0)),
                        1 => (new P2(0, 0), new P2(0, 1)),
                        _ => LineAxis(sketch, rv.Axis - 2),
                    };
                    var part = Solid.Revolve(sketch.Frame, region, a, d, rv.Angle);
                    if (part == null) continue;
                    tool = tool == null ? part : tool.Boolean(part, BoolOp.Union);
                }
                if (tool == null) throw new InvalidOperationException("프로필에서 바디를 만들 수 없습니다.");
                Combine(s, f.Id, tool, rv.Operation, rv.Targets);
                break;
            }

            case PrimitiveFeature p:
            {
                var c = new V3(p.X, p.Y, p.Z);
                Solid tool = p.Shape switch
                {
                    "cylinder" => Solid.Cylinder(c, Pos(p.A), Pos(p.B)),
                    "sphere" => Solid.Sphere(c, Pos(p.A)),
                    "cone" => Solid.Cone(c, Math.Max(0, p.A), Math.Max(0, p.B), Pos(p.C)),
                    "torus" => Solid.Torus(c, Pos(p.A), Pos(Math.Min(p.B, p.A * 0.99))),
                    _ => Solid.Box(new V3(p.X - Pos(p.A) / 2, p.Y - Pos(p.B) / 2, p.Z), new V3(p.X + Pos(p.A) / 2, p.Y + Pos(p.B) / 2, p.Z + Pos(p.C))),
                };
                Combine(s, f.Id, tool, p.Operation, p.Targets);
                break;
            }

            case MoveFeature mv:
            {
                var bodies = Pick(s, mv.Bodies);
                var center = Center(bodies);
                var m = Affine.Rotation(center, V3.UnitX, mv.Rx)
                    .Then(Affine.Rotation(center, V3.UnitY, mv.Ry))
                    .Then(Affine.Rotation(center, V3.UnitZ, mv.Rz))
                    .Then(Affine.Translation(new V3(mv.Tx, mv.Ty, mv.Tz)));
                int k = 0;
                foreach (var b in bodies)
                {
                    var moved = b.Solid.Transform(m);
                    if (mv.CreateCopy) s.Bodies.Add(new Body { Id = $"{f.Id}:{k++}", Solid = moved });
                    else Replace(s, b, moved);
                }
                break;
            }

            case MirrorFeature mi:
            {
                var bodies = Pick(s, mi.Bodies);
                var n = mi.Plane switch { "XY" => V3.UnitZ, "XZ" => V3.UnitY, _ => V3.UnitX };
                int k = 0;
                foreach (var b in bodies)
                {
                    var copy = b.Solid.Transform(Affine.Mirror(n));
                    if (mi.Join) Replace(s, b, b.Solid.Boolean(copy, BoolOp.Union));
                    else s.Bodies.Add(new Body { Id = $"{f.Id}:{k++}", Solid = copy });
                }
                break;
            }

            case PatternFeature pt:
            {
                var bodies = Pick(s, pt.Bodies);
                int k = 0;
                if (pt.Circular)
                {
                    var axis = Axis(pt.Axis);
                    int n = Math.Clamp(pt.Count, 1, 360);
                    bool full = Math.Abs(Math.Abs(pt.Angle) - 360) < 1e-9;
                    double step = full ? pt.Angle / n : pt.Angle / Math.Max(1, n - 1);
                    for (int i = 1; i < n; i++)
                        foreach (var b in bodies)
                            s.Bodies.Add(new Body { Id = $"{f.Id}:{k++}", Solid = b.Solid.Transform(Affine.Rotation(V3.Zero, axis, step * i)) });
                }
                else
                {
                    var d1 = Axis(pt.Dir1) * pt.Spacing1;
                    var d2 = Axis(pt.Dir2) * pt.Spacing2;
                    for (int i = 0; i < Math.Clamp(pt.Count1, 1, 200); i++)
                        for (int j = 0; j < Math.Clamp(pt.Count2, 1, 200); j++)
                        {
                            if (i == 0 && j == 0) continue;
                            foreach (var b in bodies)
                                s.Bodies.Add(new Body { Id = $"{f.Id}:{k++}", Solid = b.Solid.Transform(Affine.Translation(d1 * i + d2 * j)) });
                        }
                }
                break;
            }

            case CombineFeature cb:
            {
                var target = s.Find(cb.Target) ?? throw new InvalidOperationException("대상 바디를 선택하세요.");
                var tools = Pick(s, cb.Tools.Where(t => t != cb.Target).ToList());
                var op = cb.Operation switch { BodyOp.Cut => BoolOp.Subtract, BodyOp.Intersect => BoolOp.Intersect, _ => BoolOp.Union };
                var result = target.Solid;
                foreach (var t in tools) result = result.Boolean(t.Solid, op);
                Replace(s, target, result);
                if (!cb.KeepTools) s.Bodies.RemoveAll(b => tools.Any(t => t.Id == b.Id));
                break;
            }

            case ScaleFeature sc:
            {
                var bodies = Pick(s, sc.Bodies);
                if (sc.Factor <= 0) throw new InvalidOperationException("축척은 0보다 커야 합니다.");
                foreach (var b in bodies)
                {
                    var i = b.Solid.Info;
                    var c = (i.Min + i.Max) * 0.5;
                    Replace(s, b, b.Solid.Transform(Affine.Scale(new V3(c.X, c.Y, i.Min.Z), sc.Factor)));
                }
                break;
            }

            case DeleteFeature del:
                s.Bodies.RemoveAll(b => del.Bodies.Contains(b.Id));
                break;

            case ImportFeature im:
            {
                if (!File.Exists(im.Path)) throw new InvalidOperationException($"파일을 찾을 수 없습니다: {im.Path}");
                int k = 0;
                foreach (var solid0 in Solid.Import(im.Path))
                {
                    var solid = Math.Abs(im.Scale - 1) > 1e-12 ? solid0.Transform(Affine.Scale(V3.Zero, im.Scale)) : solid0;
                    s.Bodies.Add(new Body { Id = $"{f.Id}:{k++}", Solid = solid });
                }
                break;
            }

            case FilletFeature fl:
            {
                var body = s.Find(fl.Body) ?? throw new InvalidOperationException("모서리를 선택하세요.");
                if (fl.Edges.Count == 0 && !fl.AllEdges) throw new InvalidOperationException("모서리를 선택하세요.");
                var pts = fl.AllEdges ? new List<V3>() : fl.Edges.Select(e => new V3(e[0], e[1], e[2])).ToList();
                if (fl.Size <= 0) throw new InvalidOperationException(fl.Chamfer ? "거리는 0보다 커야 합니다." : "반지름은 0보다 커야 합니다.");
                Replace(s, body, fl.Chamfer ? body.Solid.Chamfer(pts, fl.Size) : body.Solid.Fillet(pts, fl.Size));
                break;
            }

            case LoftFeature lf:
            {
                if (lf.Profiles.Count < 2) throw new InvalidOperationException("프로파일을 두 개 이상 선택하세요.");
                var sections = lf.Profiles.Select(r =>
                {
                    var region = RegionOf(s, r, out var sk);
                    return (sk.Frame, (IReadOnlyList<Profile>)region.Take(1).ToList());
                }).ToList();
                Combine(s, f.Id, Solid.Loft(sections), lf.Operation, lf.Targets);
                break;
            }

            case SweepFeature sw:
            {
                if (sw.Profiles.Count == 0) throw new InvalidOperationException("프로파일을 선택하세요.");
                if (sw.Path.Count == 0) throw new InvalidOperationException("경로를 선택하세요.");
                var region = RegionOf(s, sw.Profiles[0], out var psk);
                var (pathSketch, chain) = ChainOf(s, sw.Path[0]);
                Combine(s, f.Id, Solid.Sweep(psk.Frame, region, pathSketch.Frame, chain.Pieces), sw.Operation, sw.Targets);
                break;
            }

            case SurfaceFeature sf:
            {
                if (sf.Chains.Count == 0) throw new InvalidOperationException("곡선을 선택하세요.");
                int k = 0;
                foreach (var cr in sf.Chains)
                {
                    var (sk, chain) = ChainOf(s, cr);
                    Solid surface;
                    if (sf.Patch)
                    {
                        if (!chain.Closed) throw new InvalidOperationException("패치에는 닫힌 곡선이 필요합니다.");
                        surface = Solid.Patch(sk.Frame, chain.Pieces);
                    }
                    else surface = Solid.SurfaceExtrude(sk.Frame, chain.Pieces, Math.Min(0, sf.Distance), Math.Max(0, sf.Distance));
                    s.Bodies.Add(new Body { Id = $"{f.Id}:{k++}", Solid = surface });
                }
                break;
            }

            case ThickenFeature th:
            {
                var bodies = Pick(s, th.Bodies);
                if (Math.Abs(th.Thickness) < 1e-9) throw new InvalidOperationException("두께를 입력하세요.");
                foreach (var b in bodies) Replace(s, b, b.Solid.Thicken(th.Thickness));
                break;
            }

            case PressPullFeature pp:
            {
                var body = s.Find(pp.Body) ?? throw new InvalidOperationException("면을 선택하세요.");
                if (pp.Pick.Length != 3) throw new InvalidOperationException("면을 선택하세요.");
                Replace(s, body, body.Solid.PressPull(new V3(pp.Pick[0], pp.Pick[1], pp.Pick[2]), pp.Distance));
                break;
            }

            case FlangeFeature fl when !fl.Edge:
            {
                if (fl.Profiles.Count == 0) throw new InvalidOperationException("프로파일을 선택하세요.");
                Solid? tool = null;
                var bases = new List<(PlaneFrame, List<Profile>)>();
                foreach (var r in fl.Profiles)
                {
                    var region = RegionOf(s, r, out var sk);
                    var part = Solid.Extrude(sk.Frame, region, 0, fl.Thickness);
                    tool = tool == null ? part : tool.Boolean(part, BoolOp.Union);
                    bases.Add((sk.Frame, region));
                }
                s.Bodies.Add(new Body { Id = f.Id + ":0", Solid = tool! });
                s.Sheets[f.Id + ":0"] = new SheetInfo(bases, fl.Thickness, new List<FlangeInfo>());
                break;
            }

            case FlangeFeature fl:
            {
                var body = s.Find(fl.Body) ?? throw new InvalidOperationException("모서리를 선택하세요.");
                if (fl.Pick.Length != 3) throw new InvalidOperationException("모서리를 선택하세요.");
                var fr = body.Solid.EdgeFlangeFrame(new V3(fl.Pick[0], fl.Pick[1], fl.Pick[2])) ?? throw new InvalidOperationException("직선 모서리를 선택하세요.");
                double t = fl.Thickness, r = fl.BendRadius, h = Math.Max(fl.Height, r + t + 1e-3);
                // the edge is on the sheet's top face; bend up (or down) around the edge and rise to the height
                var n = fl.Down ? -fr.normal : fr.normal;
                var origin = fl.Down ? fr.o - fr.normal * t : fr.o;
                var plane = new PlaneFrame(origin, fr.outward, n, fr.outward.Cross(n));
                double sgn = plane.N.Dot(fr.dir) >= 0 ? 1 : -1;
                var c45 = Math.Cos(Math.PI / 4);
                var pieces = new List<Piece>
                {
                    new(1, new[] { 0.0, 0.0, r * c45, r - r * c45, r, r }),
                    new(0, new[] { r, r, r, h }),
                    new(0, new[] { r, h, r + t, h }),
                    new(0, new[] { r + t, h, r + t, r }),
                    // outer arc: centre (0, r), radius r + t, through its -45° point
                    new(1, new[] { r + t, r, (r + t) * c45, r - (r + t) * c45, 0.0, -t }),
                    new(0, new[] { 0.0, -t, 0.0, 0.0 }),
                };
                var profile = new Profile(0, new List<P2>(), 1, new List<int>(), pieces);
                var wall = Solid.Extrude(plane, new[] { profile }, sgn > 0 ? 0 : -fr.length, sgn > 0 ? fr.length : 0);
                Replace(s, body, body.Solid.Boolean(wall, BoolOp.Union));
                if (s.Sheets.TryGetValue(body.Id, out var sheet))
                    s.Sheets[body.Id] = sheet with { Flanges = sheet.Flanges.Append(new FlangeInfo(fr.o, fr.outward, fr.normal, fr.dir, fr.length, fl.DevelopedLength)).ToList() };
                break;
            }

            case RibFeature rb:
            {
                if (rb.Chains.Count == 0) throw new InvalidOperationException("리브 곡선을 선택하세요.");
                Solid? tool = null;
                foreach (var cr in rb.Chains)
                {
                    var (sk, chain) = ChainOf(s, cr);
                    foreach (var piece in chain.Pieces.Where(p => p.Kind == 0))
                    {
                        var a = new P2(piece.P[0], piece.P[1]);
                        var b = new P2(piece.P[2], piece.P[3]);
                        var d = b - a;
                        double len = d.Length;
                        if (len < 1e-9) continue;
                        // the wall grows from the curve towards the part (in the sketch plane) and is
                        // centred on the sketch plane through its thickness, like Fusion's rib
                        var side = new P2(-d.Y / len, d.X / len) * (rb.Flip ? rb.Depth : -rb.Depth);
                        var rect = new List<P2> { a, b, b + side, a + side };
                        var loop = rect.Select((q, i) => { var q2 = rect[(i + 1) % 4]; return new Piece(0, new[] { q.X, q.Y, q2.X, q2.Y }); }).ToList();
                        var region = new List<Profile> { new(0, rect, 1, new List<int>(), loop) };
                        var part = Solid.Extrude(sk.Frame, region, -rb.Thickness / 2, rb.Thickness / 2);
                        tool = tool == null ? part : tool.Boolean(part, BoolOp.Union);
                    }
                }
                if (tool == null) throw new InvalidOperationException("리브에는 직선 곡선이 필요합니다.");
                Combine(s, f.Id, tool, BodyOp.Join, new List<string>());
                break;
            }

            case BossFeature bo:
            {
                var body = s.Find(bo.Body) ?? throw new InvalidOperationException("보스를 놓을 면을 선택하세요.");
                if (bo.Pick.Length != 3) throw new InvalidOperationException("보스를 놓을 면을 선택하세요.");
                var pick = new V3(bo.Pick[0], bo.Pick[1], bo.Pick[2]);
                var frame = body.Solid.FaceFrame(pick) ?? throw new InvalidOperationException("평면인 면을 선택하세요.");
                var c = frame.Project(pick);
                var outer = new List<Profile> { CircleProfile(c, bo.Diameter / 2) };
                var boss = Solid.Extrude(frame, outer, 0, bo.Height);
                var result = body.Solid.Boolean(boss, BoolOp.Union);
                if (bo.HoleDiameter > 0)
                {
                    var hole = Solid.Extrude(frame, new List<Profile> { CircleProfile(c, bo.HoleDiameter / 2) }, -bo.Height, bo.Height + 1e-3);
                    result = result.Boolean(hole, BoolOp.Subtract);
                }
                Replace(s, body, result);
                break;
            }

            case JointFeature j:
            {
                var a = s.Find(j.BodyA) ?? throw new InvalidOperationException("구성요소 1의 접합 원점을 선택하세요.");
                var b = s.Find(j.BodyB) ?? throw new InvalidOperationException("구성요소 2의 접합 원점을 선택하세요.");
                if (j.PickA.Length != 3 || j.PickB.Length != 3) throw new InvalidOperationException("접합 원점을 선택하세요.");
                var moving = GroupOf(j.BodyA);
                var fixedGroup = GroupOf(j.BodyB);
                if (moving.Contains(j.BodyB)) throw new InvalidOperationException("서로 다른 구성요소를 선택하세요.");
                var fa = a.Solid.JointFrame(new V3(j.PickA[0], j.PickA[1], j.PickA[2])) ?? throw new InvalidOperationException("구성요소 1의 접합 원점을 찾을 수 없습니다.");
                var fb = b.Solid.JointFrame(new V3(j.PickB[0], j.PickB[1], j.PickB[2])) ?? throw new InvalidOperationException("구성요소 2의 접합 원점을 찾을 수 없습니다.");
                var nb = fb.axis;
                var origin = fb.origin + nb * j.Offset;
                double angle = j.Type is "slider" ? 0 : j.Angle;
                double slide = j.Type is "slider" or "cylindrical" ? j.Distance : 0;
                var u = JointFeature.Perp(nb);
                var v = nb.Cross(u);
                var m = Align(fa.origin, fa.axis, j.Flip ? nb : -nb)
                    .Then(Affine.Translation(origin - fa.origin))
                    .Then(Affine.Rotation(origin, nb, angle))
                    .Then(Affine.Translation(nb * slide));
                switch (j.Type)
                {
                    case "pinslot":
                        m = m.Then(Affine.Translation(u * j.Distance));
                        break;
                    case "planar":
                        m = m.Then(Affine.Translation(u * j.Distance + v * j.Distance2));
                        break;
                    case "ball":
                        m = m.Then(Affine.Rotation(origin, u, j.Angle2)).Then(Affine.Rotation(origin, v, j.Angle3));
                        break;
                }
                if (j.Type is "pinslot") slide = 0;
                foreach (var id in moving)
                    if (s.Find(id) is { } body) Replace(s, body, body.Solid.Transform(m));
                var at = origin + nb * slide + (j.Type switch { "pinslot" => u * j.Distance, "planar" => u * j.Distance + v * j.Distance2, _ => V3.Zero });
                s.Joints.Add(new JointInfo(j.Name, j.Type, at, nb, moving, fixedGroup, j.LowerLimit, j.UpperLimit));
                break;
            }

            case ShellFeature sh:
            {
                var body = s.Find(sh.Body) ?? throw new InvalidOperationException("제거할 면을 선택하세요.");
                if (sh.Thickness <= 0) throw new InvalidOperationException("두께는 0보다 커야 합니다.");
                var pts = sh.Faces.Select(e => new V3(e[0], e[1], e[2])).ToList();
                Replace(s, body, body.Solid.Shell(pts, sh.Outside ? sh.Thickness : -sh.Thickness));
                break;
            }
        }
    }

    private static double Pos(double v) => Math.Max(1e-3, Math.Abs(v));

    private static Profile CircleProfile(P2 c, double r) =>
        new(0, Enumerable.Range(0, 32).Select(i => new P2(c.X + r * Math.Cos(i * Math.PI / 16), c.Y + r * Math.Sin(i * Math.PI / 16))).ToList(), Math.PI * r * r, new List<int>(), new List<Piece> { new(2, new[] { c.X, c.Y, r }) });

    private static (SketchFeature sketch, Chain chain) ChainOf(ModelState s, ChainRef r)
    {
        var sk = s.Sketches.FirstOrDefault(k => k.Id == r.Sketch) ?? throw new InvalidOperationException("참조한 스케치가 없습니다.");
        var chains = Profiles.Chains(sk);
        if (r.Chain < 0 || r.Chain >= chains.Count) throw new InvalidOperationException("참조한 곡선이 스케치에서 사라졌습니다.");
        return (sk, chains[r.Chain]);
    }

    public static V3 Axis(string a) => a switch { "X" => V3.UnitX, "Y" => V3.UnitY, _ => V3.UnitZ };

    private static (P2, P2) LineAxis(SketchFeature sk, int k)
    {
        var lines = sk.Entities.Where(e => e.Kind == "line").ToList();
        if (k < 0 || k >= lines.Count) throw new InvalidOperationException("회전축 선이 스케치에 없습니다.");
        var p = lines[k].P;
        var d = new P2(p[2] - p[0], p[3] - p[1]);
        if (d.Length < 1e-9) throw new InvalidOperationException("회전축 선의 길이가 0입니다.");
        return (new P2(p[0], p[1]), d);
    }

    private static List<Body> Pick(ModelState s, List<string> ids)
    {
        var list = ids.Select(s.Find).Where(b => b != null).Cast<Body>().ToList();
        if (list.Count == 0) throw new InvalidOperationException("바디를 선택하세요.");
        return list;
    }

    private static V3 Center(List<Body> bodies)
    {
        var lo = new V3(double.MaxValue, double.MaxValue, double.MaxValue);
        var hi = -lo;
        foreach (var b in bodies)
        {
            var i = b.Solid.Info;
            lo = new V3(Math.Min(lo.X, i.Min.X), Math.Min(lo.Y, i.Min.Y), Math.Min(lo.Z, i.Min.Z));
            hi = new V3(Math.Max(hi.X, i.Max.X), Math.Max(hi.Y, i.Max.Y), Math.Max(hi.Z, i.Max.Z));
        }
        return (lo + hi) * 0.5;
    }

    private static void Replace(ModelState s, Body b, Solid solid)
    {
        int i = s.Bodies.FindIndex(x => x.Id == b.Id);
        if (i < 0) return;
        if (solid.Info.IsEmpty) s.Bodies.RemoveAt(i);
        else s.Bodies[i] = b.With(solid);
    }

    private static bool Overlaps(Solid a, Solid b)
    {
        var (x, y) = (a.Info, b.Info);
        return x.Min.X <= y.Max.X && y.Min.X <= x.Max.X && x.Min.Y <= y.Max.Y && y.Min.Y <= x.Max.Y && x.Min.Z <= y.Max.Z && y.Min.Z <= x.Max.Z;
    }

    /// <summary>Apply a new tool solid with the feature's body operation.</summary>
    private static void Combine(ModelState s, string featureId, Solid tool, BodyOp op, List<string> targets)
    {
        var chosen = targets.Select(s.Find).Where(b => b != null).Cast<Body>().ToList();
        switch (op)
        {
            case BodyOp.NewBody:
                s.Bodies.Add(new Body { Id = featureId + ":0", Solid = tool });
                break;
            case BodyOp.Join:
            {
                var target = chosen.FirstOrDefault() ?? s.Bodies.LastOrDefault(b => Overlaps(b.Solid, tool));
                if (target == null) s.Bodies.Add(new Body { Id = featureId + ":0", Solid = tool });
                else Replace(s, target, target.Solid.Boolean(tool, BoolOp.Union));
                break;
            }
            default:
            {
                var list = chosen.Count > 0 ? chosen : s.Bodies.Where(b => Overlaps(b.Solid, tool)).ToList();
                if (list.Count == 0) throw new InvalidOperationException(op == BodyOp.Cut ? "잘라낼 바디가 없습니다." : "교차할 바디가 없습니다.");
                foreach (var b in list) Replace(s, b, b.Solid.Boolean(tool, op == BodyOp.Cut ? BoolOp.Subtract : BoolOp.Intersect));
                break;
            }
        }
    }
}
