using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace OpenShape;

/// <summary>Sketch constraints, dimensions and point dragging.</summary>
public sealed partial class MainWindow
{
    private static readonly string[] ConstraintTools = { "c_hv", "c_coincident", "c_parallel", "c_perpendicular", "c_equal", "c_fix", "c_midpoint", "c_concentric", "c_tangent", "c_symmetric" };
    private readonly List<(Rect box, int index)> _dimBoxes = new();

    /// <summary>Points that can be snapped to, with their refs (-1 = sketch origin, -3 = no ref).</summary>
    private static IEnumerable<(P2 p, int r)> SnapRefs(SketchFeature s)
    {
        yield return (new P2(0, 0), -1);
        for (int i = 0; i < s.Entities.Count; i++)
        {
            var e = s.Entities[i];
            switch (e.Kind)
            {
                case "line":
                    yield return (new P2(e.P[0], e.P[1]), SketchSolver.Ref(i, 0));
                    yield return (new P2(e.P[2], e.P[3]), SketchSolver.Ref(i, 1));
                    break;
                case "rect":
                    for (int k = 0; k < 4; k++) yield return (SketchSolver.Point(s, SketchSolver.Ref(i, k))!.Value, SketchSolver.Ref(i, k));
                    break;
                case "circle":
                    yield return (new P2(e.P[0], e.P[1]), SketchSolver.Ref(i, 0));
                    break;
                case "polygon":
                    yield return (new P2(e.P[0], e.P[1]), SketchSolver.Ref(i, 0));
                    foreach (var q in e.Points()) yield return (q, -3);
                    break;
                case "arc":
                    yield return (new P2(e.P[0], e.P[1]), SketchSolver.Ref(i, 0));
                    yield return (new P2(e.P[2], e.P[3]), SketchSolver.Ref(i, 1));
                    yield return (SketchSolver.ArcCenter(e), SketchSolver.Ref(i, 2));
                    break;
            }
        }
    }

    /// <summary>What lies under the cursor: a point ref, else a curve ref ("line", "circle").</summary>
    private (int r, string kind)? HitRef(Point screen)
    {
        var s = _sk!;
        var f = s.Draft.Frame;
        double best = 9;
        (int, string)? hit = null;
        foreach (var (p, r) in SnapRefs(s.Draft))
        {
            if (r == -3 || ToScreen(f, p) is not { } sp) continue;
            double d = Math.Sqrt((sp.X - screen.X) * (sp.X - screen.X) + (sp.Y - screen.Y) * (sp.Y - screen.Y));
            if (d < best) { best = d; hit = (r, "point"); }
        }
        if (hit != null) return hit;
        best = 7;
        for (int i = 0; i < s.Draft.Entities.Count; i++)
        {
            var e = s.Draft.Entities[i];
            var pts = e.Points();
            if (e.Closed) pts.Add(pts[0]);
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                if (ToScreen(f, pts[k]) is not { } a || ToScreen(f, pts[k + 1]) is not { } b) continue;
                double d = SegmentDistance(screen, a, b);
                if (d >= best) continue;
                best = d;
                hit = e.Kind switch
                {
                    "line" => (SketchSolver.Ref(i, 0), "line"),
                    "rect" => (SketchSolver.Ref(i, k), "line"),
                    "circle" or "arc" or "polygon" => (SketchSolver.Ref(i, 0), "circle"),
                    _ => null,
                };
            }
        }
        return hit;
    }

    /// <summary>Solve the sketch being edited; keeps the remaining degrees of freedom for display.</summary>
    private bool SolveSketch(IReadOnlyCollection<int>? dragged = null)
    {
        if (_sk == null) return true;
        try
        {
            int dof = SketchSolver.Solve(_sk.Draft, dragged);
            if (dof < 0) return false;
            _sk.Dof = dof;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Add constraints (one undo step); rolls back when the sketch cannot satisfy them.</summary>
    public bool AddConstraints(params SketchConstraint[] cs) => AddConstraintsKeeping(null, cs);

    /// <summary>Add constraints, preferring to keep the <paramref name="keep"/> points where they are (like Fusion, the first pick stays).</summary>
    public bool AddConstraintsKeeping(IReadOnlyCollection<int>? keep, params SketchConstraint[] cs)
    {
        var s = _sk!;
        s.Remember();
        s.Draft.Constraints.AddRange(cs);
        if (keep is { Count: > 0 })
        {
            // solve with the kept points temporarily fixed so only the other picks move; a pin that
            // would over-constrain (SolveSpace treats redundancy as failure) is simply left out
            var backup = s.Draft.Entities.Select(e => e.Clone()).ToList();
            var pins = new List<SketchConstraint>();
            foreach (var r in keep)
            {
                // every attempt starts from the original geometry, so pins hold the original positions
                s.Draft.Entities = backup.Select(e => e.Clone()).ToList();
                var pin = new SketchConstraint { Kind = "fix", Refs = new[] { r } };
                s.Draft.Constraints.Add(pin);
                if (SolveSketch()) pins.Add(pin);
                else s.Draft.Constraints.Remove(pin);
            }
            s.Draft.Entities = backup.Select(e => e.Clone()).ToList();
            bool ok = SolveSketch();
            foreach (var pin in pins) s.Draft.Constraints.Remove(pin);
            if (ok && SolveSketch()) return true;
            s.Draft.Entities = backup;
        }
        if (SolveSketch(keep)) return true;
        s.Restore(s.History[^1]);
        s.History.RemoveAt(s.History.Count - 1);
        SolveSketch();
        SetPrompt("구속조건이 충돌하거나 이미 구속되어 있습니다.");
        return false;
    }

    /// <summary>Change a dimension's value and re-solve (reverts if impossible).</summary>
    public bool SetDimension(int index, double value)
    {
        var s = _sk!;
        if (index < 0 || index >= s.Draft.Constraints.Count) return false;
        s.Remember();
        s.Draft.Constraints[index].Value = value;
        if (!SolveSketch())
        {
            s.Restore(s.History[^1]);
            s.History.RemoveAt(s.History.Count - 1);
            SolveSketch();
            SetPrompt("이 치수로는 스케치를 풀 수 없습니다.");
            View.InvalidateVisual();
            return false;
        }
        ShowSketchPalette();
        View.InvalidateVisual();
        return true;
    }

    private void EditDimension(int index, Point at)
    {
        var s = _sk!;
        var c = s.Draft.Constraints[index];
        var box = new TextBox { Text = CommandPanel.Format(c.Value, c.Kind == "angle" ? "deg" : "mm"), Width = 110 };
        var flyout = new Flyout { Content = box, Placement = Avalonia.Controls.PlacementMode.Pointer, OverlayDismissEventPassThrough = true };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (CommandPanel.TryParse(box.Text, out var v) && v > 0) SetDimension(index, v);
            flyout.Hide();
            e.Handled = true;
        };
        flyout.ShowAt(View, true);
        box.Focus();
        box.SelectAll();
    }

    /// <summary>A click while a constraint or dimension tool is active.</summary>
    private void ConstraintClick(Point screen)
    {
        var s = _sk!;
        var hit = HitRef(screen);
        if (hit == null) return;
        var (r, kind) = hit.Value;
        s.Picks.Add(r);
        var kinds = new List<string>(_pickKinds) { kind };
        _pickKinds.Add(kind);
        SketchConstraint C(string k, params int[] refs) => new() { Kind = k, Refs = refs };
        SketchConstraint? done = null;
        bool reset = false;
        int n = s.Picks.Count;
        int[] p = s.Picks.ToArray();
        switch (s.Tool)
        {
            case "c_hv":
                if (kind == "line" && SketchSolver.Segment(s.Draft, r) is { } seg)
                    done = C(Math.Abs(seg.b.X - seg.a.X) >= Math.Abs(seg.b.Y - seg.a.Y) ? "horizontal" : "vertical", r);
                reset = true;
                break;
            case "c_fix":
                if (kind == "point") done = C("fix", r);
                reset = true;
                break;
            case "c_coincident" when n == 2:
                if (kinds[0] == "point" && kinds[1] == "point") done = C("coincident", p[0], p[1]);
                else if (kinds[0] == "point" && kinds[1] == "line") done = C("pointonline", p[0], p[1]);
                else if (kinds[0] == "line" && kinds[1] == "point") done = C("pointonline", p[1], p[0]);
                reset = true;
                break;
            case "c_parallel" or "c_perpendicular" when n == 2:
                if (kinds.All(k => k == "line")) done = C(s.Tool == "c_parallel" ? "parallel" : "perpendicular", p[0], p[1]);
                reset = true;
                break;
            case "c_equal" when n == 2:
                if (kinds[0] == kinds[1] && kinds[0] is "line" or "circle") done = C("equal", p[0], p[1]);
                reset = true;
                break;
            case "c_midpoint" when n == 2:
                if (kinds[0] == "point" && kinds[1] == "line") done = C("midpoint", p[0], p[1]);
                else if (kinds[0] == "line" && kinds[1] == "point") done = C("midpoint", p[1], p[0]);
                reset = true;
                break;
            case "c_concentric" when n == 2:
                if (kinds.All(k => k == "circle"))
                {
                    int Center(int x) => s.Draft.Entities[SketchSolver.Ent(x)].Kind == "arc" ? SketchSolver.Ref(SketchSolver.Ent(x), 2) : x;
                    done = C("concentric", Center(p[0]), Center(p[1]));
                }
                reset = true;
                break;
            case "c_tangent" when n == 2:
                if (kinds.Contains("circle") && kinds.All(k => k is "line" or "circle") && !(kinds[0] == "line" && kinds[1] == "line")) done = C("tangent", p[0], p[1]);
                reset = true;
                break;
            case "c_symmetric" when n == 3:
                if (kinds[0] == "point" && kinds[1] == "point" && kinds[2] == "line") done = C("symmetric", p[0], p[1], p[2]);
                reset = true;
                break;
            case "dimension":
                if (n == 1 && kind == "circle")
                {
                    done = C("diameter", r);
                    reset = true;
                }
                else if (n == 2 && kinds[0] == "point" && kinds[1] == "point")
                {
                    done = C("distance", p[0], p[1]);
                    reset = true;
                }
                else if (n == 2 && kinds[0] == "line" && kinds[1] == "line")
                {
                    done = C("angle", p[0], p[1]);
                    reset = true;
                }
                else if (n == 2 && kinds[0] == "line" && kinds[1] == "point" || n == 2 && kinds[0] == "point" && kinds[1] == "line")
                    reset = true;
                break;
        }
        // a lone line picked with the dimension tool is dimensioned on its second click elsewhere, or right away on a double pick
        if (s.Tool == "dimension" && n == 1 && kind == "line")
        {
            var e = s.Draft.Entities[SketchSolver.Ent(r)];
            int a, b;
            if (e.Kind == "rect") { a = SketchSolver.Ref(SketchSolver.Ent(r), SketchSolver.Sub(r)); b = SketchSolver.Ref(SketchSolver.Ent(r), (SketchSolver.Sub(r) + 1) % 4); }
            else { a = SketchSolver.Ref(SketchSolver.Ent(r), 0); b = SketchSolver.Ref(SketchSolver.Ent(r), 1); }
            _pendingLineDim = (a, b);
            SetPrompt("선 길이 치수: 다른 곳을 클릭하면 길이 치수를, 다른 선을 클릭하면 각도 치수를 만듭니다.");
        }
        if (done == null && s.Tool == "dimension" && n == 2 && _pendingLineDim is { } ld && kinds[1] != "line")
        {
            done = C("distance", ld.a, ld.b);
            reset = true;
        }
        if (reset)
        {
            s.Picks.Clear();
            _pickKinds.Clear();
            _pendingLineDim = null;
        }
        if (done != null)
        {
            if (SketchConstraint.IsDimension(done.Kind)) done.Value = SketchSolver.Measure(s.Draft, done);
            // keep the first picked item in place; for symmetry keep the mirror line
            var keep = KeepPoints(done.Kind == "symmetric" ? done.Refs[2] : p[0], done.Kind == "symmetric" ? "line" : kinds[0]);
            if (done.Kind == "symmetric") keep.Add(done.Refs[0]);
            if (AddConstraintsKeeping(keep, done))
            {
                SetPrompt(null);
                if (SketchConstraint.IsDimension(done.Kind)) EditDimension(s.Draft.Constraints.Count - 1, screen);
            }
            ShowSketchPalette();
        }
        View.InvalidateVisual();
    }

    private readonly List<string> _pickKinds = new();

    /// <summary>Point refs that pin a picked item: a point, a line's two ends, or a circle's centre.</summary>
    private List<int> KeepPoints(int r, string kind)
    {
        if (kind == "point") return new List<int> { r };
        int e = SketchSolver.Ent(r), k = SketchSolver.Sub(r);
        var en = _sk!.Draft.Entities[e];
        return kind == "line"
            ? en.Kind == "rect" ? new List<int> { SketchSolver.Ref(e, k), SketchSolver.Ref(e, (k + 1) % 4) } : new List<int> { SketchSolver.Ref(e, 0), SketchSolver.Ref(e, 1) }
            : new List<int> { en.Kind == "arc" ? SketchSolver.Ref(e, 2) : SketchSolver.Ref(e, 0) };
    }
    private (int a, int b)? _pendingLineDim;

    /// <summary>Line-length dimension from the pending line pick (clicking empty space finishes it).</summary>
    private void FinishPendingLineDimension(Point screen)
    {
        if (_sk == null || _pendingLineDim is not { } ld) return;
        var c = new SketchConstraint { Kind = "distance", Refs = new[] { ld.a, ld.b } };
        c.Value = SketchSolver.Measure(_sk.Draft, c);
        _sk.Picks.Clear();
        _pickKinds.Clear();
        _pendingLineDim = null;
        if (AddConstraints(c))
        {
            SetPrompt(null);
            EditDimension(_sk.Draft.Constraints.Count - 1, screen);
        }
        ShowSketchPalette();
        View.InvalidateVisual();
    }

    // ------------------------------------------------------------ dragging

    private bool SketchDragStart(Point p)
    {
        if (_sk is not { Tool: "" } s) return false;
        var hit = HitRef(p);
        if (hit is not { kind: "point" } h || h.r < 0) return false;
        s.Remember();
        s.DragRef = h.r;
        return true;
    }

    private void SketchDrag(Point p)
    {
        var s = _sk;
        if (s == null || s.DragRef < 0) return;
        var q = HitPlane(s.Draft.Frame, p)?.p;
        if (q == null) return;
        var backup = s.Draft.Entities.Select(e => e.Clone()).ToList();
        SetPointRef(s.Draft, s.DragRef, q.Value);
        if (!SolveSketch(new[] { s.DragRef })) s.Draft.Entities = backup;
        View.InvalidateVisual();
    }

    private void SketchDragEnd()
    {
        if (_sk == null) return;
        _sk.DragRef = -2;
        ShowSketchPalette();
    }

    /// <summary>Move one point of an entity (rectangles keep their sides axis-aligned).</summary>
    public static void SetPointRef(SketchFeature s, int r, P2 q)
    {
        int e = SketchSolver.Ent(r), k = SketchSolver.Sub(r);
        if (e < 0 || e >= s.Entities.Count) return;
        var en = s.Entities[e];
        var p = en.P;
        switch (en.Kind)
        {
            case "line" when k <= 1:
                p[k * 2] = q.X;
                p[k * 2 + 1] = q.Y;
                break;
            case "rect":
                if (k is 0 or 3) p[0] = q.X; else p[2] = q.X;
                if (k is 0 or 1) p[1] = q.Y; else p[3] = q.Y;
                break;
            case "circle" or "polygon":
                p[0] = q.X;
                p[1] = q.Y;
                break;
            case "arc" when k <= 1:
                p[k * 2] = q.X;
                p[k * 2 + 1] = q.Y;
                break;
            case "arc":
            {
                var c = SketchSolver.ArcCenter(en);
                double dx = q.X - c.X, dy = q.Y - c.Y;
                for (int i = 0; i < 6; i += 2) { p[i] += dx; p[i + 1] += dy; }
                break;
            }
        }
    }

    // ------------------------------------------------------------ drawing

    private void DrawConstraints(DrawingContext ctx)
    {
        var s = _sk!;
        var f = s.Draft.Frame;
        _dimBoxes.Clear();
        var dimPen = new Pen(new SolidColorBrush(Color.FromRgb(70, 70, 70)), 1);
        var glyphBrush = new SolidColorBrush(Color.FromRgb(40, 90, 170));
        Point? S(P2? p) => p is { } q ? ToScreen(f, q) : null;
        void Label(string text, Point at, int index, bool dimension)
        {
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, dimension ? 12 : 11, dimension ? Ui.Text : glyphBrush);
            var r = new Rect(at.X - ft.Width / 2 - 4, at.Y - ft.Height / 2 - 1, ft.Width + 8, ft.Height + 2);
            ctx.FillRectangle(dimension ? Brushes.White : new SolidColorBrush(Color.FromArgb(230, 235, 243, 255)), r, 2);
            ctx.DrawRectangle(null, new Pen(dimension ? Ui.Line : glyphBrush, 0.8), r, 2);
            ctx.DrawText(ft, new Point(r.X + 4, r.Y + 1));
            if (dimension) _dimBoxes.Add((r, index));
        }
        string F(double v) => Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < s.Draft.Constraints.Count; i++)
        {
            var c = s.Draft.Constraints[i];
            try
            {
                switch (c.Kind)
                {
                    case "distance" when S(SketchSolver.Point(s.Draft, c.Refs[0])) is { } a && S(SketchSolver.Point(s.Draft, c.Refs[1])) is { } b:
                    {
                        var d = b - a;
                        double len = Math.Max(1, Math.Sqrt(d.X * d.X + d.Y * d.Y));
                        var off = new Vector(-d.Y / len, d.X / len) * 18;
                        ctx.DrawLine(dimPen, a + off, b + off);
                        ctx.DrawLine(dimPen, a, a + off * 1.2);
                        ctx.DrawLine(dimPen, b, b + off * 1.2);
                        Label(F(c.Value), (a + b) / 2 + off, i, true);
                        break;
                    }
                    case "diameter":
                    {
                        var e = s.Draft.Entities[SketchSolver.Ent(c.Refs[0])];
                        var center = e.Kind == "arc" ? SketchSolver.ArcCenter(e) : new P2(e.P[0], e.P[1]);
                        double r = e.Kind == "arc" ? center.Dist(new P2(e.P[0], e.P[1])) : e.P[2];
                        var rim = new P2(center.X + r * 0.707, center.Y + r * 0.707);
                        if (S(center) is { } sc && S(rim) is { } sr)
                        {
                            ctx.DrawLine(dimPen, sc, sr);
                            Label("⌀" + F(c.Value), sr + (sr - sc) * 0.25, i, true);
                        }
                        break;
                    }
                    case "angle" when SketchSolver.Segment(s.Draft, c.Refs[0]) is { } l1 && S((l1.a + l1.b) * 0.5) is { } m:
                        Label(F(c.Value) + "°", m + new Vector(0, -16), i, true);
                        break;
                    case "tangent" or "symmetric":
                    {
                        var at = c.Kind == "symmetric" ? SketchSolver.Segment(s.Draft, c.Refs[2]) is { } sg2 ? (sg2.a + sg2.b) * 0.5 : (P2?)null : SketchSolver.Point(s.Draft, c.Refs[1] >= 0 && s.Draft.Entities[SketchSolver.Ent(c.Refs[1])].Kind != "line" ? c.Refs[1] : c.Refs[0]);
                        if (S(at) is { } sp) Label(c.Kind == "tangent" ? "⌒" : "⇋", sp + new Vector(12, -14), i, false);
                        break;
                    }
                    case "horizontal" or "vertical" or "parallel" or "perpendicular" or "equal" or "midpoint" or "pointonline":
                    {
                        int lineRef = c.Kind is "midpoint" or "pointonline" ? c.Refs[1] : c.Refs[0];
                        P2? mid = SketchSolver.Segment(s.Draft, lineRef) is { } sg ? (sg.a + sg.b) * 0.5 : SketchSolver.Point(s.Draft, lineRef);
                        string g = c.Kind switch { "horizontal" => "━", "vertical" => "┃", "parallel" => "∥", "perpendicular" => "⊥", "equal" => "=", "midpoint" => "△", _ => "•" };
                        if (S(mid) is { } sm) Label(g, sm + new Vector(10, 12), i, false);
                        if (c.Kind == "equal" && c.Refs.Length > 1 && SketchSolver.Segment(s.Draft, c.Refs[1]) is { } s2 && S((s2.a + s2.b) * 0.5) is { } m2) Label(g, m2 + new Vector(10, 12), i, false);
                        break;
                    }
                    case "fix" when S(SketchSolver.Point(s.Draft, c.Refs[0])) is { } fp:
                        Label("고정", fp + new Vector(14, -12), i, false);
                        break;
                    case "concentric" when S(SketchSolver.Point(s.Draft, c.Refs[0])) is { } cp:
                        Label("◎", cp + new Vector(12, -12), i, false);
                        break;
                }
            }
            catch (Exception)
            {
                // a constraint whose geometry was removed is simply not drawn
            }
        }
        // points picked so far by a constraint tool
        foreach (var r in s.Picks)
            if (S(SketchSolver.Point(s.Draft, r)) is { } pp) ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(6, 150, 215)), null, pp, 4, 4);
    }

    private int DimensionAt(Point p)
    {
        foreach (var (box, index) in _dimBoxes)
            if (box.Inflate(2).Contains(p)) return index;
        return -1;
    }
}
