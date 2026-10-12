using System.Runtime.InteropServices;

namespace OpenShape;

/// <summary>
/// A sketch constraint or dimension. Refs address points and curves as entity * 8 + sub:
/// line points 0/1; rectangle corners 0..3 (sides 0..3 as lines); circle/polygon centre 0;
/// arc start 0, end 1, centre 2. Ref -1 is the sketch origin.
/// </summary>
public sealed class SketchConstraint
{
    public string Kind { get; set; } = "coincident";
    public int[] Refs { get; set; } = Array.Empty<int>();
    public double Value { get; set; }

    public static bool IsDimension(string kind) => kind is "distance" or "diameter" or "angle";

    public static string DisplayName(string kind) => kind switch
    {
        "coincident" => "일치",
        "horizontal" => "수평",
        "vertical" => "수직",
        "parallel" => "평행",
        "perpendicular" => "직각",
        "equal" => "동일",
        "fix" => "고정",
        "midpoint" => "중간점",
        "concentric" => "동심",
        "pointonline" => "점-선 일치",
        "distance" => "거리 치수",
        "diameter" => "지름 치수",
        "angle" => "각도 치수",
        _ => kind,
    };
}

public static unsafe class SketchSolver
{
    [DllImport("openshape_occt")]
    private static extern int ok_solve(double* p, int np, int* pts, int npts, int* ents, int nents, double* cons, int ncons, int* dragged, int ndragged, int* dof);

    public static int Ent(int r) => r >> 3;
    public static int Sub(int r) => r & 7;
    public static int Ref(int ent, int sub) => ent * 8 + sub;

    /// <summary>Location of a point ref in sketch coordinates.</summary>
    public static P2? Point(SketchFeature s, int r)
    {
        if (r == -1) return new P2(0, 0);
        int e = Ent(r), k = Sub(r);
        if (e < 0 || e >= s.Entities.Count) return null;
        var en = s.Entities[e];
        var p = en.P;
        return en.Kind switch
        {
            "line" when k <= 1 => new P2(p[k * 2], p[k * 2 + 1]),
            "rect" when k <= 3 => k switch { 0 => new P2(p[0], p[1]), 1 => new P2(p[2], p[1]), 2 => new P2(p[2], p[3]), _ => new P2(p[0], p[3]) },
            "circle" or "polygon" when k == 0 => new P2(p[0], p[1]),
            "arc" when k <= 1 => new P2(p[k * 2], p[k * 2 + 1]),
            "arc" when k == 2 => ArcCenter(en),
            _ => null,
        };
    }

    /// <summary>Endpoints of a line ref (a line, or one side of a rectangle).</summary>
    public static (P2 a, P2 b)? Segment(SketchFeature s, int r)
    {
        int e = Ent(r), k = Sub(r);
        if (e < 0 || e >= s.Entities.Count) return null;
        var en = s.Entities[e];
        if (en.Kind == "line") return (new P2(en.P[0], en.P[1]), new P2(en.P[2], en.P[3]));
        if (en.Kind == "rect" && k <= 3) return (Point(s, Ref(e, k))!.Value, Point(s, Ref(e, (k + 1) % 4))!.Value);
        return null;
    }

    public static P2 ArcCenter(SketchEntity a)
    {
        var p = a.P;
        P2 s = new(p[0], p[1]), e = new(p[2], p[3]), m = new(p[4], p[5]);
        double d = 2 * (s.X * (m.Y - e.Y) + m.X * (e.Y - s.Y) + e.X * (s.Y - m.Y));
        if (Math.Abs(d) < 1e-12) return new P2((s.X + e.X) / 2, (s.Y + e.Y) / 2);
        double s2 = s.X * s.X + s.Y * s.Y, m2 = m.X * m.X + m.Y * m.Y, e2 = e.X * e.X + e.Y * e.Y;
        return new P2((s2 * (m.Y - e.Y) + m2 * (e.Y - s.Y) + e2 * (s.Y - m.Y)) / d, (s2 * (e.X - m.X) + m2 * (s.X - e.X) + e2 * (m.X - s.X)) / d);
    }

    /// <summary>Current measured value for a new dimension.</summary>
    public static double Measure(SketchFeature s, SketchConstraint c)
    {
        switch (c.Kind)
        {
            case "distance":
                return Point(s, c.Refs[0]) is { } a && Point(s, c.Refs[1]) is { } b ? a.Dist(b) : 0;
            case "diameter":
            {
                var e = s.Entities[Ent(c.Refs[0])];
                return e.Kind == "arc" ? ArcCenter(e).Dist(new P2(e.P[0], e.P[1])) * 2 : e.P[2] * 2;
            }
            case "angle":
            {
                if (Segment(s, c.Refs[0]) is not { } l1 || Segment(s, c.Refs[1]) is not { } l2) return 0;
                double a1 = Math.Atan2(l1.b.Y - l1.a.Y, l1.b.X - l1.a.X), a2 = Math.Atan2(l2.b.Y - l2.a.Y, l2.b.X - l2.a.X);
                double d = Math.Abs((a2 - a1) * 180 / Math.PI) % 180;
                return d > 90 ? 180 - d : d;
            }
        }
        return 0;
    }

    /// <summary>
    /// Solve the sketch's constraints, moving its entities in place. Returns the remaining degrees
    /// of freedom, or -1 (with the sketch unchanged) when the constraints cannot be satisfied.
    /// </summary>
    public static int Solve(SketchFeature s, IReadOnlyCollection<int>? draggedRefs = null)
    {
        var prm = new List<double>();
        var pts = new List<int>();
        var ents = new List<int>();
        var cons = new List<double>();
        var pointOf = new Dictionary<int, int>();     // ref -> point index
        var lineOf = new Dictionary<int, int>();      // ref -> solver entity
        var circleOf = new Dictionary<int, int>();
        var write = new List<Action<double[]>>();
        var swappedArcs = new HashSet<int>();

        int AddPoint(P2 p)
        {
            prm.Add(p.X);
            prm.Add(p.Y);
            pts.Add(prm.Count - 2);
            pts.Add(prm.Count - 1);
            return pts.Count / 2 - 1;
        }
        int AddEnt(int type, int a, int b, int c)
        {
            ents.AddRange(new[] { type, a, b, c });
            return ents.Count / 4 - 1;
        }
        void Con(int type, double value = 0, int pa = -1, int pb = -1, int ea = -1, int eb = -1) => cons.AddRange(new double[] { type, value, pa, pb, ea, eb });

        for (int i = 0; i < s.Entities.Count; i++)
        {
            var e = s.Entities[i];
            var p = e.P;
            int idx = i;
            switch (e.Kind)
            {
                case "line":
                {
                    int a = AddPoint(new P2(p[0], p[1])), b = AddPoint(new P2(p[2], p[3]));
                    pointOf[Ref(i, 0)] = a;
                    pointOf[Ref(i, 1)] = b;
                    lineOf[Ref(i, 0)] = AddEnt(0, a, b, 0);
                    write.Add(v => { var q = s.Entities[idx].P; q[0] = v[pts[a * 2]]; q[1] = v[pts[a * 2 + 1]]; q[2] = v[pts[b * 2]]; q[3] = v[pts[b * 2 + 1]]; });
                    break;
                }
                case "rect":
                {
                    var c = Enumerable.Range(0, 4).Select(k => AddPoint(Point(s, Ref(i, k))!.Value)).ToArray();
                    for (int k = 0; k < 4; k++)
                    {
                        pointOf[Ref(i, k)] = c[k];
                        int side = AddEnt(0, c[k], c[(k + 1) % 4], 0);
                        lineOf[Ref(i, k)] = side;
                        Con(k % 2 == 0 ? 1 : 2, ea: side);     // rectangles stay axis-aligned
                    }
                    write.Add(v => { var q = s.Entities[idx].P; q[0] = v[pts[c[0] * 2]]; q[1] = v[pts[c[0] * 2 + 1]]; q[2] = v[pts[c[2] * 2]]; q[3] = v[pts[c[2] * 2 + 1]]; });
                    break;
                }
                case "circle" or "polygon":
                {
                    int c = AddPoint(new P2(p[0], p[1]));
                    prm.Add(p[2]);
                    int r = prm.Count - 1;
                    pointOf[Ref(i, 0)] = c;
                    circleOf[Ref(i, 0)] = AddEnt(1, c, r, 0);
                    write.Add(v => { var q = s.Entities[idx].P; q[0] = v[pts[c * 2]]; q[1] = v[pts[c * 2 + 1]]; q[2] = Math.Abs(v[r]); });
                    break;
                }
                case "arc":
                {
                    var center = ArcCenter(e);
                    P2 st = new(p[0], p[1]), end = new(p[2], p[3]), m = new(p[4], p[5]);
                    // SolveSpace arcs run counter-clockwise from start to end
                    double a0 = Math.Atan2(st.Y - center.Y, st.X - center.X), a1 = Math.Atan2(end.Y - center.Y, end.X - center.X), am = Math.Atan2(m.Y - center.Y, m.X - center.X);
                    double Ccw(double from, double to) => ((to - from) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
                    bool swap = Ccw(a0, am) > Ccw(a0, a1);
                    int pc = AddPoint(center), ps = AddPoint(swap ? end : st), pe = AddPoint(swap ? st : end);
                    pointOf[Ref(i, 0)] = swap ? pe : ps;
                    pointOf[Ref(i, 1)] = swap ? ps : pe;
                    pointOf[Ref(i, 2)] = pc;
                    circleOf[Ref(i, 0)] = AddEnt(2, pc, ps, pe);
                    write.Add(v =>
                    {
                        P2 P(int k) => new(v[pts[k * 2]], v[pts[k * 2 + 1]]);
                        var (c2, s2, e2) = (P(pc), P(ps), P(pe));
                        double r = c2.Dist(s2), b0 = Math.Atan2(s2.Y - c2.Y, s2.X - c2.X), b1 = Math.Atan2(e2.Y - c2.Y, e2.X - c2.X);
                        double mid = b0 + Ccw(b0, b1) / 2;
                        var through = new P2(c2.X + r * Math.Cos(mid), c2.Y + r * Math.Sin(mid));
                        var (first, last) = swap ? (e2, s2) : (s2, e2);
                        s.Entities[idx].P = new[] { first.X, first.Y, last.X, last.Y, through.X, through.Y };
                    });
                    break;
                }
            }
        }
        int origin = -1;
        int PointIdx(int r)
        {
            if (r == -1)
            {
                if (origin < 0)
                {
                    origin = AddPoint(new P2(0, 0));
                    Con(10, pa: origin);
                }
                return origin;
            }
            return pointOf.TryGetValue(r, out var k) ? k : throw new InvalidOperationException("구속조건이 가리키는 점이 없습니다.");
        }
        int LineIdx(int r) => lineOf.TryGetValue(r, out var k) ? k : throw new InvalidOperationException("구속조건이 가리키는 선이 없습니다.");
        int CircleIdx(int r) => circleOf.TryGetValue(r, out var k) ? k : throw new InvalidOperationException("구속조건이 가리키는 원이 없습니다.");

        foreach (var c in s.Constraints)
        {
            var r = c.Refs;
            switch (c.Kind)
            {
                case "coincident": Con(0, pa: PointIdx(r[0]), pb: PointIdx(r[1])); break;
                case "concentric": Con(0, pa: PointIdx(r[0]), pb: PointIdx(r[1])); break;
                case "horizontal": Con(1, ea: LineIdx(r[0])); break;
                case "vertical": Con(2, ea: LineIdx(r[0])); break;
                case "distance": Con(3, c.Value, PointIdx(r[0]), PointIdx(r[1])); break;
                case "diameter": Con(4, c.Value, ea: CircleIdx(r[0])); break;
                case "parallel": Con(5, ea: LineIdx(r[0]), eb: LineIdx(r[1])); break;
                case "perpendicular": Con(6, ea: LineIdx(r[0]), eb: LineIdx(r[1])); break;
                case "equal":
                    if (lineOf.ContainsKey(r[0]) && lineOf.ContainsKey(r[1])) Con(7, ea: LineIdx(r[0]), eb: LineIdx(r[1]));
                    else Con(9, ea: CircleIdx(r[0]), eb: CircleIdx(r[1]));
                    break;
                case "angle": Con(8, c.Value, ea: LineIdx(r[0]), eb: LineIdx(r[1])); break;
                case "fix": Con(10, pa: PointIdx(r[0])); break;
                case "pointonline": Con(11, pa: PointIdx(r[0]), ea: LineIdx(r[1])); break;
                case "midpoint": Con(13, pa: PointIdx(r[0]), ea: LineIdx(r[1])); break;
            }
        }
        if (cons.Count == 0 && (draggedRefs == null || draggedRefs.Count == 0)) return Dof(s);

        var dragged = new List<int>();
        if (draggedRefs != null)
            foreach (var r in draggedRefs)
                if (pointOf.TryGetValue(r, out var k)) { dragged.Add(pts[k * 2]); dragged.Add(pts[k * 2 + 1]); }
        var pa = prm.ToArray();
        var pt = pts.ToArray();
        var en = ents.ToArray();
        var cs = cons.ToArray();
        var dr = dragged.ToArray();
        int dof, res;
        Occt.Init();
        fixed (double* pp = pa, pc = cs)
        fixed (int* ppt = pt, pe = en, pd = dr)
            res = ok_solve(pp, pa.Length, ppt, pt.Length / 2, pe, en.Length / 4, pc, cs.Length / 6, pd, dr.Length, &dof);
        if (res != 0) return -1;
        foreach (var w in write) w(pa);
        return dof;
    }

    /// <summary>Degrees of freedom with no constraints (2 per point, 1 per radius).</summary>
    private static int Dof(SketchFeature s) => s.Entities.Sum(e => e.Kind switch { "line" => 4, "rect" => 4, "circle" or "polygon" => 3, "arc" => 5, _ => 0 });

    /// <summary>Remove constraints that use entity <paramref name="index"/> and renumber the rest after deleting it.</summary>
    public static void EntityRemoved(SketchFeature s, int index)
    {
        s.Constraints.RemoveAll(c => c.Refs.Any(r => r >= 0 && Ent(r) == index));
        foreach (var c in s.Constraints)
            for (int i = 0; i < c.Refs.Length; i++)
                if (c.Refs[i] >= 0 && Ent(c.Refs[i]) > index) c.Refs[i] -= 8;
    }
}
