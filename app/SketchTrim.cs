namespace OpenShape;

/// <summary>Sketch trim: remove the piece of a curve between its nearest intersections.</summary>
public static class SketchTrim
{
    private abstract record Geo;
    private sealed record Seg(P2 A, P2 B) : Geo;
    private sealed record Circ(P2 C, double R, double Start, double Sweep, bool Full) : Geo;   // counter-clockwise from Start

    private static double Norm(double a) => (a % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);

    /// <summary>Lines for rectangles/polygons, so their sides can be trimmed individually.</summary>
    public static List<SketchEntity> Explode(SketchEntity e)
    {
        if (e.Kind is not ("rect" or "polygon")) return new List<SketchEntity> { e };
        var pts = e.Points();
        return pts.Select((a, i) => { var b = pts[(i + 1) % pts.Count]; return new SketchEntity { Kind = "line", P = new[] { a.X, a.Y, b.X, b.Y } }; }).ToList();
    }

    private static Geo? GeoOf(SketchEntity e)
    {
        var p = e.P;
        switch (e.Kind)
        {
            case "line": return new Seg(new P2(p[0], p[1]), new P2(p[2], p[3]));
            case "circle": return new Circ(new P2(p[0], p[1]), p[2], 0, 2 * Math.PI, true);
            case "arc":
            {
                var c = SketchSolver.ArcCenter(e);
                double a0 = Math.Atan2(p[1] - c.Y, p[0] - c.X), a1 = Math.Atan2(p[3] - c.Y, p[2] - c.X), am = Math.Atan2(p[5] - c.Y, p[4] - c.X);
                double sweep = Norm(a1 - a0);
                if (Norm(am - a0) > sweep) return new Circ(c, c.Dist(new P2(p[0], p[1])), a1, Norm(a0 - a1), false);
                return new Circ(c, c.Dist(new P2(p[0], p[1])), a0, sweep, false);
            }
        }
        return null;
    }

    private static IEnumerable<P2> Intersect(Geo a, Geo b)
    {
        if (a is Seg s1 && b is Seg s2)
        {
            var d1 = s1.B - s1.A;
            var d2 = s2.B - s2.A;
            double den = d1.X * d2.Y - d1.Y * d2.X;
            if (Math.Abs(den) < 1e-12) yield break;
            var w = s2.A - s1.A;
            double t = (w.X * d2.Y - w.Y * d2.X) / den, u = (w.X * d1.Y - w.Y * d1.X) / den;
            if (t >= -1e-9 && t <= 1 + 1e-9 && u >= -1e-9 && u <= 1 + 1e-9) yield return s1.A + d1 * t;
            yield break;
        }
        if (a is Circ && b is Seg) { foreach (var p in Intersect(b, a)) yield return p; yield break; }
        if (a is Seg s && b is Circ c)
        {
            var d = s.B - s.A;
            var f = s.A - c.C;
            double A = d.X * d.X + d.Y * d.Y, B = 2 * (f.X * d.X + f.Y * d.Y), C = f.X * f.X + f.Y * f.Y - c.R * c.R;
            double disc = B * B - 4 * A * C;
            if (disc < 0 || A < 1e-18) yield break;
            foreach (var t in new[] { (-B - Math.Sqrt(disc)) / (2 * A), (-B + Math.Sqrt(disc)) / (2 * A) })
                if (t >= -1e-9 && t <= 1 + 1e-9)
                {
                    var p = s.A + d * t;
                    if (OnArc(c, p)) yield return p;
                }
            yield break;
        }
        if (a is Circ c1 && b is Circ c2)
        {
            double dist = c1.C.Dist(c2.C);
            if (dist < 1e-12 || dist > c1.R + c2.R + 1e-9 || dist < Math.Abs(c1.R - c2.R) - 1e-9) yield break;
            double x = (dist * dist + c1.R * c1.R - c2.R * c2.R) / (2 * dist);
            double h = Math.Sqrt(Math.Max(0, c1.R * c1.R - x * x));
            var dir = (c2.C - c1.C) * (1 / dist);
            var mid = c1.C + dir * x;
            var perp = new P2(-dir.Y, dir.X);
            foreach (var p in new[] { mid + perp * h, mid - perp * h })
                if (OnArc(c1, p) && OnArc(c2, p)) yield return p;
        }
    }

    private static bool OnArc(Circ c, P2 p) => c.Full || Norm(Math.Atan2(p.Y - c.C.Y, p.X - c.C.X) - c.Start) <= c.Sweep + 1e-9;

    private static double Param(Geo g, P2 p) => g switch
    {
        Seg s => ((p - s.A).X * (s.B - s.A).X + (p - s.A).Y * (s.B - s.A).Y) / Math.Max(1e-18, Math.Pow((s.B - s.A).Length, 2)),
        Circ c => Norm(Math.Atan2(p.Y - c.C.Y, p.X - c.C.X) - c.Start),
        _ => 0,
    };

    private static SketchEntity ArcEntity(Circ c, double from, double to)
    {
        P2 At(double t) => new(c.C.X + c.R * Math.Cos(c.Start + t), c.C.Y + c.R * Math.Sin(c.Start + t));
        var a = At(from);
        var b = At(to);
        var m = At((from + to) / 2);
        return new SketchEntity { Kind = "arc", P = new[] { a.X, a.Y, b.X, b.Y, m.X, m.Y } };
    }

    /// <summary>
    /// Trim entity <paramref name="index"/> at the clicked sketch point. Returns false when there is
    /// nothing to trim. The entity is replaced by its remaining pieces (appended at the end).
    /// </summary>
    public static bool Trim(SketchFeature s, int index, P2 click)
    {
        var target = s.Entities[index];
        if (target.Kind is "rect" or "polygon")
        {
            // explode first; then trim the side under the cursor
            var lines = Explode(target);
            SketchSolver.EntityRemoved(s, index);
            s.Entities.RemoveAt(index);
            s.Entities.AddRange(lines);
            int best = -1;
            double bd = double.MaxValue;
            for (int i = s.Entities.Count - lines.Count; i < s.Entities.Count; i++)
            {
                var g0 = (Seg)GeoOf(s.Entities[i])!;
                double t = Math.Clamp(Param(g0, click), 0, 1);
                double d = (g0.A + (g0.B - g0.A) * t).Dist(click);
                if (d < bd) { bd = d; best = i; }
            }
            return Trim(s, best, click) || true;
        }
        var g = GeoOf(target);
        if (g == null) return false;
        var cuts = new List<double>();
        for (int i = 0; i < s.Entities.Count; i++)
        {
            if (i == index) continue;
            foreach (var part in Explode(s.Entities[i]))
                if (GeoOf(part) is { } o)
                    foreach (var p in Intersect(g, o)) cuts.Add(Param(g, p));
        }
        double at = Param(g, click);
        var pieces = new List<SketchEntity>();
        if (g is Seg seg)
        {
            var ts = cuts.Where(t => t > 1e-6 && t < 1 - 1e-6).Append(0).Append(1).Distinct().OrderBy(t => t).ToList();
            double lo = ts.Last(t => t <= at), hi = ts.First(t => t >= at);
            if (lo <= 1e-9 && hi >= 1 - 1e-9) pieces.Clear();   // no intersections: delete the whole line
            P2 P(double t) => seg.A + (seg.B - seg.A) * t;
            if (lo > 1e-9) { var a = P(0); var b = P(lo); pieces.Add(new SketchEntity { Kind = "line", P = new[] { a.X, a.Y, b.X, b.Y } }); }
            if (hi < 1 - 1e-9) { var a = P(hi); var b = P(1); pieces.Add(new SketchEntity { Kind = "line", P = new[] { a.X, a.Y, b.X, b.Y } }); }
        }
        else if (g is Circ c)
        {
            if (c.Full)
            {
                var angles = cuts.Select(Norm).Distinct().OrderBy(a => a).ToList();
                if (angles.Count >= 2)
                {
                    double next = angles.FirstOrDefault(a => a > at, angles[0] + 2 * Math.PI);
                    double prev = angles.LastOrDefault(a => a < at, angles[^1] - 2 * Math.PI);
                    // keep the arc from "next" round to "prev" (+ 2π)
                    var keep = c with { Start = c.Start + next, Full = false, Sweep = 2 * Math.PI - (next - prev) };
                    pieces.Add(ArcEntity(keep, 0, keep.Sweep));
                }
            }
            else
            {
                var ts = cuts.Where(t => t > 1e-6 && t < c.Sweep - 1e-6).Append(0).Append(c.Sweep).Distinct().OrderBy(t => t).ToList();
                double lo = ts.Last(t => t <= at + 1e-12), hi = ts.First(t => t >= at - 1e-12);
                if (lo > 1e-9) pieces.Add(ArcEntity(c, 0, lo));
                if (hi < c.Sweep - 1e-9) pieces.Add(ArcEntity(c, hi, c.Sweep));
            }
        }
        SketchSolver.EntityRemoved(s, index);
        s.Entities.RemoveAt(index);
        s.Entities.AddRange(pieces);
        return true;
    }
}
