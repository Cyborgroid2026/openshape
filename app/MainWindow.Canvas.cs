using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace OpenShape;

public sealed partial class MainWindow
{
    private SketchSession? _sk;
    private bool _pickPlane;
    private double _pendingOffset;
    private string? _hoverPlane;
    private ProfileRef? _hoverProfile;

    private static readonly IBrush ProfileFill = new SolidColorBrush(Color.FromArgb(70, 255, 170, 60));
    private static readonly IBrush ProfileHover = new SolidColorBrush(Color.FromArgb(110, 120, 180, 255));
    private static readonly IBrush ProfileSelected = new SolidColorBrush(Color.FromArgb(150, 60, 130, 240));
    private static readonly IPen CurvePen = new Pen(new SolidColorBrush(Color.FromRgb(44, 98, 190)), 1.4);
    private static readonly IPen CurveDim = new Pen(new SolidColorBrush(Color.FromArgb(120, 44, 98, 190)), 1.1);
    private static readonly IPen SelectedCurvePen = new Pen(new SolidColorBrush(Color.FromRgb(6, 150, 215)), 3);
    private static readonly IPen RubberPen = new Pen(new SolidColorBrush(Color.FromRgb(44, 98, 190)), 1.2, new DashStyle(new double[] { 5, 3 }, 0));

    // ------------------------------------------------------------ geometry helpers

    private Point? ToScreen(PlaneFrame f, P2 p) => View.Project(f.At(p));

    private Geometry? PolyGeometry(PlaneFrame f, IReadOnlyList<P2> pts, bool closed)
    {
        var sp = new List<Point>(pts.Count);
        foreach (var p in pts)
        {
            if (ToScreen(f, p) is not { } s) return null;
            sp.Add(s);
        }
        return sp.Count < 2 ? null : new PolylineGeometry(sp, closed);
    }

    /// <summary>Ray hit on a sketch plane: sketch coordinates and distance along the ray.</summary>
    private (P2 p, double t)? HitPlane(PlaneFrame f, Point screen)
    {
        var (o, d) = View.Ray(screen);
        double denom = d.Dot(f.N);
        if (Math.Abs(denom) < 1e-9) return null;
        double t = (f.O - o).Dot(f.N) / denom;
        if (t <= 0) return null;
        return (f.Project(o + d * t), t);
    }

    private Geometry RegionGeometry(PlaneFrame f, List<Profile> all, int index)
    {
        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        foreach (var loop in Profiles.Region(all, index))
            if (PolyGeometry(f, loop.Loop, true) is { } pg) g.Children.Add(pg);
        return g;
    }

    /// <summary>Sketches shown on the canvas: visible ones, plus every sketch while a command picks profiles.</summary>
    private IEnumerable<SketchFeature> ShownSketches()
    {
        var s = _cmd?.Base ?? State;
        bool picking = _cmd?.SelectKind is "profiles" or "chains" or "path";
        return (_cmd != null ? s : State).Sketches.Where(k => k.Visible || picking);
    }

    /// <summary>Sketch curve chain under the cursor (within 7 px).</summary>
    private ChainRef? HitChain(Point screen)
    {
        ChainRef? best = null;
        double bd = 7;
        foreach (var sk in ShownSketches())
        {
            var chains = Profiles.Chains(sk);
            foreach (var c in chains)
                for (int k = 0; k + 1 < c.Points.Count; k++)
                {
                    if (ToScreen(sk.Frame, c.Points[k]) is not { } a || ToScreen(sk.Frame, c.Points[k + 1]) is not { } b) continue;
                    double d = SegmentDistance(screen, a, b);
                    if (d < bd) { bd = d; best = new ChainRef(sk.Id, c.Index); }
                }
        }
        return best;
    }

    private (ProfileRef? r, double t) HitProfile(Point screen)
    {
        ProfileRef? best = null;
        double bestT = double.PositiveInfinity;
        var states = _cmd?.Base ?? State;
        foreach (var sk in ShownSketches())
        {
            if (!states.SketchProfiles.TryGetValue(sk.Id, out var profiles) || profiles.Count == 0) continue;
            if (HitPlane(sk.Frame, screen) is not { } hit) continue;
            int i = Profiles.Hit(profiles, hit.p);
            if (i >= 0 && hit.t < bestT)
            {
                best = new ProfileRef(sk.Id, i);
                bestT = hit.t;
            }
        }
        return (best, bestT);
    }

    private string? HitOriginPlane(Point screen)
    {
        double size = View.Cam.Distance * 0.1;
        string? best = null;
        double bestT = double.PositiveInfinity;
        foreach (var pl in new[] { "XY", "XZ", "YZ" })
        {
            var f = PlaneFrame.Of(pl, 0);
            if (HitPlane(f, screen) is not { } h) continue;
            if (Math.Abs(h.p.X) <= size && Math.Abs(h.p.Y) <= size && h.t < bestT) { best = pl; bestT = h.t; }
        }
        return best;
    }

    // ------------------------------------------------------------ overlay

    private void DrawOverlay(DrawingContext ctx)
    {
        if (_doc.OriginVisible || _pickPlane) DrawOriginPlanes(ctx);
        if (_sk != null)
        {
            DrawSketchSession(ctx);
            return;
        }
        DrawEdges(ctx);
        var profileState = _cmd?.Base ?? State;
        var selected = _cmd?.Draft switch
        {
            ExtrudeFeature ex => ex.Profiles.ToHashSet(),
            RevolveFeature rv => rv.Profiles.ToHashSet(),
            _ => _selProfiles.ToHashSet(),
        };
        foreach (var sk in ShownSketches())
        {
            var f = sk.Frame;
            if (profileState.SketchProfiles.TryGetValue(sk.Id, out var profiles))
                for (int i = 0; i < profiles.Count; i++)
                {
                    var r = new ProfileRef(sk.Id, i);
                    var fill = selected.Contains(r) ? ProfileSelected : _hoverProfile?.Equals(r) == true ? ProfileHover : sk.Visible ? ProfileFill : null;
                    if (fill != null) ctx.DrawGeometry(fill, null, RegionGeometry(f, profiles, i));
                }
            foreach (var e in sk.Entities)
                if (PolyGeometry(f, e.Points(), e.Closed) is { } g) ctx.DrawGeometry(null, sk.Visible ? CurvePen : CurveDim, g);
        }
    }

    private void DrawOriginPlanes(DrawingContext ctx)
    {
        double s = View.Cam.Distance * 0.1;
        foreach (var pl in new[] { "XY", "XZ", "YZ" })
        {
            var f = PlaneFrame.Of(pl, 0);
            var g = PolyGeometry(f, new[] { new P2(-s, -s), new P2(s, -s), new P2(s, s), new P2(-s, s) }, true);
            if (g == null) continue;
            bool hot = _hoverPlane == pl;
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(hot ? (byte)110 : (byte)45, 245, 166, 35)),
                new Pen(new SolidColorBrush(Color.FromArgb(hot ? (byte)255 : (byte)160, 222, 140, 20)), hot ? 2 : 1), g);
        }
    }

    private void DrawSketchSession(DrawingContext ctx)
    {
        var s = _sk!;
        var f = s.Draft.Frame;
        // sketch grid on the plane
        if (s.ShowGrid)
        {
            double step = View.GridStep;
            var center = HitPlane(f, new Point(View.Bounds.Width / 2, View.Bounds.Height / 2))?.p ?? new P2(0, 0);
            double ext = View.Cam.Distance * 1.2;
            double x0 = Math.Floor((center.X - ext) / step) * step, y0 = Math.Floor((center.Y - ext) / step) * step;
            var minor = new Pen(new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)), 1);
            var major = new Pen(new SolidColorBrush(Color.FromArgb(55, 0, 0, 0)), 1);
            for (double x = x0; x <= center.X + ext; x += step)
                if (PolyGeometry(f, new[] { new P2(x, center.Y - ext), new P2(x, center.Y + ext) }, false) is { } g)
                    ctx.DrawGeometry(null, Math.Abs(x / (step * 10) - Math.Round(x / (step * 10))) < 1e-6 ? major : minor, g);
            for (double y = y0; y <= center.Y + ext; y += step)
                if (PolyGeometry(f, new[] { new P2(center.X - ext, y), new P2(center.X + ext, y) }, false) is { } g)
                    ctx.DrawGeometry(null, Math.Abs(y / (step * 10) - Math.Round(y / (step * 10))) < 1e-6 ? major : minor, g);
        }
        double big = View.Cam.Distance * 2;
        if (PolyGeometry(f, new[] { new P2(-big, 0), new P2(big, 0) }, false) is { } ax) ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 220, 60, 60)), 1), ax);
        if (PolyGeometry(f, new[] { new P2(0, -big), new P2(0, big) }, false) is { } ay) ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 50, 160, 70)), 1), ay);

        var profiles = Profiles.Compute(s.Draft);
        for (int i = 0; i < profiles.Count; i++) ctx.DrawGeometry(ProfileFill, null, RegionGeometry(f, profiles, i));
        // like Fusion: blue while under-constrained, black once fully constrained
        var pen = s.Dof == 0 && s.Draft.Entities.Count > 0 ? new Pen(new SolidColorBrush(Color.FromRgb(25, 25, 25)), 1.4) : CurvePen;
        for (int i = 0; i < s.Draft.Entities.Count; i++)
        {
            var e = s.Draft.Entities[i];
            if (PolyGeometry(f, e.Points(), e.Closed) is not { } g) continue;
            if (i == s.Selected) ctx.DrawGeometry(null, SelectedCurvePen, g);
            ctx.DrawGeometry(null, pen, g);
            foreach (var p in e.SnapPoints())
                if (ToScreen(f, p) is { } sp) ctx.DrawRectangle(Brushes.White, new Pen(pen.Brush, 1), new Rect(sp.X - 2.5, sp.Y - 2.5, 5, 5));
        }
        DrawConstraints(ctx);
        if (ToScreen(f, new P2(0, 0)) is { } o) ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(200, 120, 20)), null, new Rect(o.X - 3, o.Y - 3, 6, 6));

        // rubber band for the active tool
        if (s.Hover is { } h)
        {
            var preview = PreviewEntity(s, h);
            if (preview != null && PolyGeometry(f, preview.Points(), preview.Closed) is { } pg) ctx.DrawGeometry(null, RubberPen, pg);
            if (ToScreen(f, h) is { } hs)
            {
                if (s.HoverSnapped) ctx.DrawRectangle(null, new Pen(Brushes.OrangeRed, 1.5), new Rect(hs.X - 5, hs.Y - 5, 10, 10));
                var text = DimensionText(s, h);
                if (text != null)
                {
                    var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Ui.Text);
                    var r = new Rect(hs.X + 14, hs.Y + 12, ft.Width + 10, ft.Height + 4);
                    ctx.FillRectangle(Brushes.White, r);
                    ctx.DrawRectangle(null, new Pen(Ui.Line, 1), r);
                    ctx.DrawText(ft, new Point(r.X + 5, r.Y + 2));
                }
            }
        }
    }

    private static SketchEntity? PreviewEntity(SketchSession s, P2 h)
    {
        if (s.Pts.Count == 0) return null;
        var a = s.Pts[0];
        return s.Tool switch
        {
            "line" => new SketchEntity { Kind = "line", P = new[] { s.Pts[^1].X, s.Pts[^1].Y, h.X, h.Y } },
            "rect" => new SketchEntity { Kind = "rect", P = new[] { a.X, a.Y, h.X, h.Y } },
            "crect" => new SketchEntity { Kind = "rect", P = new[] { 2 * a.X - h.X, 2 * a.Y - h.Y, h.X, h.Y } },
            "circle" => new SketchEntity { Kind = "circle", P = new[] { a.X, a.Y, a.Dist(h) } },
            "polygon" => new SketchEntity { Kind = "polygon", P = new[] { a.X, a.Y, a.Dist(h), s.Sides } },
            "arc" when s.Pts.Count == 1 => new SketchEntity { Kind = "line", P = new[] { a.X, a.Y, h.X, h.Y } },
            "arc" => new SketchEntity { Kind = "arc", P = new[] { a.X, a.Y, s.Pts[1].X, s.Pts[1].Y, h.X, h.Y } },
            _ => null,
        };
    }

    private static string? DimensionText(SketchSession s, P2 h)
    {
        string F(double v) => Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s.Pts.Count == 0) return s.Tool == "" ? null : $"{F(h.X)}, {F(h.Y)}";
        var a = s.Pts[^1];
        return s.Tool switch
        {
            "line" => $"{F(a.Dist(h))} mm",
            "rect" => $"{F(Math.Abs(h.X - s.Pts[0].X))} × {F(Math.Abs(h.Y - s.Pts[0].Y))} mm",
            "crect" => $"{F(Math.Abs(h.X - s.Pts[0].X) * 2)} × {F(Math.Abs(h.Y - s.Pts[0].Y) * 2)} mm",
            "circle" => $"지름 {F(s.Pts[0].Dist(h) * 2)} mm",
            "polygon" => $"반지름 {F(s.Pts[0].Dist(h))} mm",
            _ => null,
        };
    }

    // ------------------------------------------------------------ canvas input

    private V3[]? _hoverEdge;

    /// <summary>Nearest B-rep edge to the cursor (within 8 px) among the bodies a command can pick from.</summary>
    private (string body, V3 point, V3[] edge)? HitEdge(Point screen)
    {
        var bodies = (_cmd?.Base ?? State).Bodies.Where(b => !_doc.HiddenBodies.Contains(b.Id));
        (string, V3, V3[])? best = null;
        double bestD = 8;
        foreach (var b in bodies)
            foreach (var edge in b.Solid.Edges)
                for (int k = 0; k + 1 < edge.Length; k++)
                {
                    if (View.Project(edge[k]) is not { } a || View.Project(edge[k + 1]) is not { } c) continue;
                    var ab = c - a;
                    double len2 = ab.X * ab.X + ab.Y * ab.Y;
                    double t = len2 < 1e-9 ? 0 : Math.Clamp(((screen.X - a.X) * ab.X + (screen.Y - a.Y) * ab.Y) / len2, 0, 1);
                    var q = a + ab * t;
                    double d = Math.Sqrt((screen.X - q.X) * (screen.X - q.X) + (screen.Y - q.Y) * (screen.Y - q.Y));
                    if (d < bestD)
                    {
                        bestD = d;
                        var w = edge[k] + (edge[k + 1] - edge[k]) * t;
                        best = (b.Id, w, edge);
                    }
                }
        return best;
    }

    private static double DistToPolyline(V3 p, V3[] line)
    {
        double best = double.MaxValue;
        for (int k = 0; k + 1 < line.Length; k++)
        {
            var ab = line[k + 1] - line[k];
            double len2 = ab.Dot(ab);
            double t = len2 < 1e-12 ? 0 : Math.Clamp((p - line[k]).Dot(ab) / len2, 0, 1);
            best = Math.Min(best, (p - (line[k] + ab * t)).Length);
        }
        return line.Length == 1 ? (p - line[0]).Length : best;
    }

    /// <summary>Base-state edge closest to a stored edge point.</summary>
    private V3[]? EdgeAt(string bodyId, V3 point)
    {
        var body = (_cmd?.Base ?? State).Find(bodyId);
        if (body == null) return null;
        V3[]? best = null;
        double bd = 0.5 + 1e-3 * (body.Solid.Info.Max - body.Solid.Info.Min).Length;
        foreach (var e in body.Solid.Edges)
        {
            double d = DistToPolyline(point, e);
            if (d < bd) { bd = d; best = e; }
        }
        return best;
    }

    private void DrawEdges(DrawingContext ctx)
    {
        void Line(V3[] e, IPen pen)
        {
            var pts = new List<Point>();
            foreach (var p in e)
            {
                if (View.Project(p) is not { } s) return;
                pts.Add(s);
            }
            if (pts.Count > 1) ctx.DrawGeometry(null, pen, new PolylineGeometry(pts, false));
        }
        var chosen = _cmd?.Draft switch { SurfaceFeature sf => sf.Chains, RibFeature rb => rb.Chains, SweepFeature sw => sw.Path, _ => null };
        if (chosen != null)
            foreach (var cr in chosen)
                if ((_cmd?.Base ?? State).Sketches.FirstOrDefault(k => k.Id == cr.Sketch) is { } sk && Profiles.Chains(sk) is var chs && cr.Chain < chs.Count)
                    if (PolyGeometry(sk.Frame, chs[cr.Chain].Points, false) is { } g) ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(6, 150, 215)), 3.5), g);
        if (_cmd?.Draft is FlangeFeature { Edge: true } ef && ef.Pick.Length == 3 && EdgeAt(ef.Body, new V3(ef.Pick[0], ef.Pick[1], ef.Pick[2])) is { } fe) Line(fe, new Pen(new SolidColorBrush(Color.FromRgb(6, 150, 215)), 3.5));
        if (_cmd?.Draft is PressPullFeature or BossFeature)
        {
            var pk = _cmd.Draft is PressPullFeature pp ? pp.Pick : ((BossFeature)_cmd.Draft).Pick;
            if (pk.Length == 3 && View.Project(new V3(pk[0], pk[1], pk[2])) is { } sp) ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(6, 150, 215)), new Pen(Brushes.White, 1.5), sp, 5, 5);
        }
        if (_cmd?.Draft is FilletFeature fl)
            foreach (var e in fl.Edges)
                if (EdgeAt(fl.Body, new V3(e[0], e[1], e[2])) is { } line) Line(line, new Pen(new SolidColorBrush(Color.FromRgb(6, 150, 215)), 3.5));
        var facePicks = _cmd?.Draft switch
        {
            ShellFeature sh => sh.Faces,
            HoleFeature ho => ho.Points,
            DraftFeature dr => dr.Faces.Concat(dr.Neutral.Length == 3 ? new[] { dr.Neutral } : Array.Empty<double[]>()).ToList(),
            _ => null,
        };
        if (facePicks != null)
            foreach (var f in facePicks)
                if (View.Project(new V3(f[0], f[1], f[2])) is { } s)
                    ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(6, 150, 215)), new Pen(Brushes.White, 1.5), s, 5, 5);
        if (_hoverEdge != null) Line(_hoverEdge, new Pen(new SolidColorBrush(Color.FromArgb(200, 90, 180, 255)), 3));
        // joints: a small disc with the axis, like Fusion's joint glyphs
        void Joint(V3 o, V3 axis, Color col)
        {
            if (View.Project(o) is not { } so) return;
            double len = View.Cam.Distance * 0.06;
            if (View.Project(o + axis * len) is { } sa) ctx.DrawLine(new Pen(new SolidColorBrush(col), 2), so, sa);
            ctx.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(col), 2), so, 7, 7);
            ctx.DrawEllipse(new SolidColorBrush(col), null, so, 3, 3);
        }
        foreach (var j in State.Joints) Joint(j.Origin, j.Axis, Color.FromRgb(60, 60, 60));
        DrawInspectOverlay(ctx);
        DrawCamOverlay(ctx);
        if (_cmd?.Draft is JointFeature jd && _cmd.Base is { } bs)
        {
            if (jd.PickA.Length == 3 && bs.Find(jd.BodyA)?.Solid.JointFrame(new V3(jd.PickA[0], jd.PickA[1], jd.PickA[2])) is { } fa) Joint(fa.origin, fa.axis, Color.FromRgb(6, 150, 215));
            if (jd.PickB.Length == 3 && bs.Find(jd.BodyB)?.Solid.JointFrame(new V3(jd.PickB[0], jd.PickB[1], jd.PickB[2])) is { } fb) Joint(fb.origin, fb.axis, Color.FromRgb(230, 120, 20));
        }
    }

    private void OnCanvasHover(Point p)
    {
        if (_cmd?.SelectKind is "edges" or "edge1")
        {
            var h = HitEdge(p)?.edge;
            if (!ReferenceEquals(h, _hoverEdge)) { _hoverEdge = h; View.InvalidateVisual(); }
            return;
        }
        if (_hoverEdge != null) { _hoverEdge = null; View.InvalidateVisual(); }
        if (_sk != null)
        {
            var snapped = SketchPoint(p, out bool snap);
            _sk.Hover = snapped;
            _sk.HoverSnapped = snap;
            View.InvalidateVisual();
            return;
        }
        if (_pickPlane)
        {
            var hp = HitOriginPlane(p);
            if (hp != _hoverPlane) { _hoverPlane = hp; View.InvalidateVisual(); }
            return;
        }
        var (r, t) = HitProfile(p);
        if (r != null)
        {
            var (_, bodyT) = View.Pick(p);
            if (bodyT < t - Math.Max(0.01, t * 1e-4)) r = null;
        }
        if (!Equals(r, _hoverProfile))
        {
            _hoverProfile = r;
            View.InvalidateVisual();
        }
    }

    private void OnCanvasClick(Point p, KeyModifiers mods)
    {
        bool add = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Shift);
        if (_sk != null)
        {
            SketchClick(p);
            return;
        }
        if (_pickPlane)
        {
            // a planar face of a body, or one of the origin planes
            var (fi, ft, face) = View.PickFace(p);
            if (HitOriginPlane(p) is { } pl && (fi < 0 || HitPlane(PlaneFrame.Of(pl, 0), p)?.t < ft)) EnterSketch(pl, -1, null);
            else if (fi >= 0 && face > 0 && fi < _itemBodies.Count && State.Find(_itemBodies[fi]) is { } fb)
            {
                var (o, d) = View.Ray(p);
                if (fb.Solid.FaceFrame(o + d * ft) is { } frame) EnterSketch("face", -1, frame);
                else SetPrompt("평면인 면이나 원점 평면을 선택하세요");
            }
            return;
        }
        var (r, t) = HitProfile(p);
        var (bi, bt) = View.Pick(p);
        bool profileWins = r != null && t <= bt + Math.Max(0.01, t * 1e-4);
        string? body = bi >= 0 && bi < _itemBodies.Count ? _itemBodies[bi] : null;

        if (_cmd != null)
        {
            if (_cmd.SelectKind == "edges") { if (HitEdge(p) is { } he) CommandPickEdge(he.body, he.point); }
            else if (_cmd.SelectKind is "chains" or "path")
            {
                if (HitChain(p) is { } ch) CommandPickChain(ch);
            }
            else if (_cmd.SelectKind == "edge1")
            {
                if (HitEdge(p) is { } he) CommandPickPoint(he.body, he.point);
            }
            else if (_cmd.SelectKind is "face1" or "neutral" or "toolface")
            {
                var (fi, ft, face) = View.PickFace(p);
                if (fi >= 0 && face > 0 && fi < _itemBodies.Count)
                {
                    var (o, d) = View.Ray(p);
                    CommandPickPoint(_itemBodies[fi], o + d * ft);
                }
            }
            else if (_cmd.SelectKind is "jointA" or "jointB")
            {
                var (fi, ft, face) = View.PickFace(p);
                if (fi >= 0 && face > 0 && fi < _itemBodies.Count)
                {
                    var (o, d) = View.Ray(p);
                    CommandPickJoint(_itemBodies[fi], o + d * ft);
                }
            }
            else if (_cmd.SelectKind == "camface")
            {
                var (fi, ft, face) = View.PickFace(p);
                if (fi >= 0 && face > 0 && fi < _itemBodies.Count)
                {
                    var (o, d) = View.Ray(p);
                    CamPickFace(_itemBodies[fi], o + d * ft);
                }
            }
            else if (_cmd.SelectKind is "faces" or "holepts")
            {
                var (fi, ft, face) = View.PickFace(p);
                if (fi >= 0 && face > 0 && fi < _itemBodies.Count)
                {
                    var (o, d) = View.Ray(p);
                    CommandPickFace(_itemBodies[fi], o + d * ft);
                }
            }
            else if (_cmd.SelectKind == "profiles" && r != null) CommandPickProfile(r);
            else if (_cmd.SelectKind is "bodies" or "target" or "tools" && body != null) CommandPickBody(body);
            return;
        }
        if (profileWins)
        {
            if (!add) { _selBodies.Clear(); _selProfiles.Clear(); }
            if (!_selProfiles.Remove(r!)) _selProfiles.Add(r!);
        }
        else if (body != null)
        {
            if (!add) { _selBodies.Clear(); _selProfiles.Clear(); }
            if (!_selBodies.Remove(body)) _selBodies.Add(body);
        }
        else if (!add)
        {
            _selBodies.Clear();
            _selProfiles.Clear();
        }
        SelectionChanged();
    }

    private void OnCanvasDoubleClick(Point p)
    {
        if (_sk != null && DimensionAt(p) is var dim and >= 0)
        {
            EditDimension(dim, p);
            return;
        }
        if (_sk != null)
        {
            // double-click ends a line chain
            _sk.Pts.Clear();
            _sk.ChainStart = null;
            View.InvalidateVisual();
        }
    }

    private void OnCanvasRightClick(Point p)
    {
        if (_sk != null && _sk.Pts.Count > 0)
        {
            _sk.Pts.Clear();
            _sk.ChainStart = null;
            View.InvalidateVisual();
            return;
        }
        // marking-menu style shortcut list
        var m = new MenuFlyout();
        void Item(string text, string icon, Action a)
        {
            var mi = new MenuItem { Header = text, Icon = Icons.Color(icon, 16) };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        if (_cmd != null)
        {
            Item("확인", "finish", OkCommand);
            Item("취소", "delete", CancelCommand);
            m.Items.Add(new Separator());
        }
        if (_sk != null)
        {
            Item("스케치 마무리", "finish", () => FinishSketch(true));
            Item("선", "line", () => RunCommand("line"));
            Item("2점 직사각형", "rect", () => RunCommand("rect"));
            Item("중심 지름 원", "circle", () => RunCommand("circle"));
        }
        else
        {
            Item("스케치 작성", "sketch", () => RunCommand("sketch"));
            Item("돌출", "extrude", () => RunCommand("extrude"));
            Item("이동/복사", "move", () => RunCommand("move"));
            if (_selBodies.Count > 0) Item("제거", "delete", () => RunCommand("delete"));
            Item("맞춤", "select", FitAll);
        }
        m.ShowAt(View, true);
    }

    // ------------------------------------------------------------ sketch mode

    public void StartPlanePick(double offset = 0)
    {
        CancelCommand();
        _pendingOffset = offset;
        _pickPlane = true;
        _expanded["origin"] = true;
        RebuildBrowser();
        SetPrompt("스케치할 평면을 선택하세요 (XY, XZ, YZ)");
        View.InvalidateVisual();
    }

    public void EnterSketch(string plane, int editIndex, PlaneFrame? face)
    {
        CancelCommand();
        _pickPlane = false;
        _hoverPlane = null;
        SetPrompt(null);
        SketchFeature draft;
        if (editIndex >= 0) draft = (SketchFeature)_doc.Features[editIndex].Copy();
        else draft = new SketchFeature { Name = _doc.NextName("sketch"), Plane = plane, Offset = _pendingOffset, Custom = face?.ToArray() };
        draft.Visible = true;
        var baseState = Eval(_doc.Features, editIndex >= 0 ? editIndex : _doc.Marker);
        _sk = new SketchSession { Draft = draft, EditIndex = editIndex, Base = baseState };
        SolveSketch();
        if (_pendingSketchTool is { } tool)
        {
            _pendingSketchTool = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RunCommand(tool));
        }
        _preview = baseState;
        _selBodies.Clear();
        _selProfiles.Clear();
        LookAtPlane(draft);
        BuildToolbarGroups();
        ShowSketchPalette();
        UpdateItems();
        RebuildTimeline();
        View.InvalidateVisual();
    }

    public void FinishSketch(bool keep)
    {
        if (_sk == null) return;
        var s = _sk;
        _sk = null;
        CloseDialog();
        BuildToolbarGroups();
        if (keep && (s.Draft.Entities.Count > 0 || s.EditIndex >= 0))
            Commit(() =>
            {
                if (s.EditIndex >= 0) _doc.Features[s.EditIndex] = s.Draft;
                else
                {
                    _doc.Features.Insert(_doc.Marker, s.Draft);
                    _doc.Marker++;
                }
            });
        else Rebuild();
    }

    /// <summary>Screen point to snapped sketch coordinates.</summary>
    private P2 SketchPoint(Point screen, out bool snapped) => SketchPoint(screen, out snapped, out _);

    private P2 SketchPoint(Point screen, out bool snapped, out int snapRef)
    {
        snapped = false;
        snapRef = -2;
        var s = _sk!;
        var f = s.Draft.Frame;
        var raw = HitPlane(f, screen)?.p ?? new P2(0, 0);
        // snap to existing points within 10 px
        double best = 10;
        P2? hit = null;
        foreach (var (c, r) in SnapRefs(s.Draft))
        {
            if (ToScreen(f, c) is not { } sp) continue;
            double d = Math.Sqrt((sp.X - screen.X) * (sp.X - screen.X) + (sp.Y - screen.Y) * (sp.Y - screen.Y));
            if (d < best) { best = d; hit = c; snapRef = r == -3 ? -2 : r; }
        }
        if (hit is { } h) { snapped = true; return h; }
        if (s.Snap && s.SnapStep > 0) return new P2(Math.Round(raw.X / s.SnapStep) * s.SnapStep, Math.Round(raw.Y / s.SnapStep) * s.SnapStep);
        return raw;
    }

    /// <summary>Add a sketch point (as if clicked) in sketch coordinates. Used by the canvas and by tests.</summary>
    /// <summary>
    /// Add a sketch point (as if clicked) in sketch coordinates; <paramref name="snapRef"/> is the
    /// point it snapped to (-2 none, -1 origin). Like Fusion, snapping and near-axis lines add
    /// coincident / horizontal / vertical constraints automatically.
    /// </summary>
    public void SketchInput(P2 q, int snapRef = -2)
    {
        var s = _sk!;
        var auto = new List<SketchConstraint>();
        int added = -1;
        void Add(string kind, params double[] p)
        {
            s.Remember();
            s.Draft.Entities.Add(new SketchEntity { Kind = kind, P = p });
            added = s.Draft.Entities.Count - 1;
        }
        void Coincide(int a, int b)
        {
            if (a != -2 && b != -2 && a != b) auto.Add(new SketchConstraint { Kind = "coincident", Refs = new[] { a, b } });
        }
        switch (s.Tool)
        {
            case "line":
            {
                if (s.Pts.Count == 0)
                {
                    s.Pts.Add(q);
                    s.ChainStart = q;
                    s.StartRef = snapRef;
                    s.ChainEntity = -1;
                    break;
                }
                var last = s.Pts[^1];
                if (last.Dist(q) < 1e-9) break;
                string? axis = null;
                double tan = Math.Tan(1.5 * Math.PI / 180);
                if (snapRef == -2)
                {
                    if (Math.Abs(q.Y - last.Y) <= tan * Math.Abs(q.X - last.X)) { q = new P2(q.X, last.Y); axis = "horizontal"; }
                    else if (Math.Abs(q.X - last.X) <= tan * Math.Abs(q.Y - last.Y)) { q = new P2(last.X, q.Y); axis = "vertical"; }
                }
                Add("line", last.X, last.Y, q.X, q.Y);
                Coincide(SketchSolver.Ref(added, 0), s.ChainEntity >= 0 ? SketchSolver.Ref(s.ChainEntity, 1) : s.StartRef);
                Coincide(SketchSolver.Ref(added, 1), snapRef);
                if (axis != null) auto.Add(new SketchConstraint { Kind = axis, Refs = new[] { SketchSolver.Ref(added, 0) } });
                if (s.ChainStart is { } st && st.Dist(q) < 1e-9)
                {
                    s.Pts.Clear();
                    s.ChainStart = null;
                    s.ChainEntity = -1;
                }
                else
                {
                    s.Pts.Clear();
                    s.Pts.Add(q);
                    s.ChainEntity = added;
                }
                break;
            }
            case "rect" or "crect" or "circle" or "polygon":
            {
                if (s.Pts.Count == 0) { s.Pts.Add(q); s.StartRef = snapRef; break; }
                var a = s.Pts[0];
                s.Pts.Clear();
                if (a.Dist(q) < 1e-9) break;
                if (s.Tool == "rect")
                {
                    Add("rect", a.X, a.Y, q.X, q.Y);
                    Coincide(SketchSolver.Ref(added, 0), s.StartRef);
                    Coincide(SketchSolver.Ref(added, 2), snapRef);
                }
                else if (s.Tool == "crect") Add("rect", 2 * a.X - q.X, 2 * a.Y - q.Y, q.X, q.Y);
                else
                {
                    if (s.Tool == "circle") Add("circle", a.X, a.Y, a.Dist(q));
                    else Add("polygon", a.X, a.Y, a.Dist(q), s.Sides);
                    Coincide(SketchSolver.Ref(added, 0), s.StartRef);
                }
                break;
            }
            case "arc":
                s.Pts.Add(q);
                if (s.Pts.Count == 1) s.StartRef = snapRef;
                if (s.Pts.Count == 2) s.ChainEntity = snapRef;   // end point's snap, kept until the arc is made
                if (s.Pts.Count == 3)
                {
                    var (p0, p1, pm) = (s.Pts[0], s.Pts[1], s.Pts[2]);
                    s.Pts.Clear();
                    Add("arc", p0.X, p0.Y, p1.X, p1.Y, pm.X, pm.Y);
                    Coincide(SketchSolver.Ref(added, 0), s.StartRef);
                    Coincide(SketchSolver.Ref(added, 1), s.ChainEntity);
                    s.ChainEntity = -1;
                }
                break;
        }
        if (added >= 0)
        {
            s.Draft.Constraints.AddRange(auto);
            if (!SolveSketch())
            {
                // auto constraints that over-constrain are dropped
                foreach (var c in auto) s.Draft.Constraints.Remove(c);
                SolveSketch();
            }
        }
        ShowSketchPalette();
        View.InvalidateVisual();
    }

    private void SketchClick(Point screen)
    {
        var s = _sk!;
        if (s.Tool == "trim")
        {
            if (HitRef(screen) is { } tr && tr.kind is "line" or "circle" || NearestEntity(screen) >= 0)
            {
                int ent = HitRef(screen) is { kind: "line" or "circle" } h ? SketchSolver.Ent(h.r) : NearestEntity(screen);
                if (ent >= 0 && HitPlane(s.Draft.Frame, screen) is { } hp)
                {
                    s.Remember();
                    SketchTrim.Trim(s.Draft, ent, hp.p);
                    SolveSketch();
                    ShowSketchPalette();
                    View.InvalidateVisual();
                }
            }
            return;
        }
        if (s.Tool == "dimension" || ConstraintTools.Contains(s.Tool))
        {
            if (_pendingLineDim != null && HitRef(screen) == null) FinishPendingLineDimension(screen);
            else ConstraintClick(screen);
            return;
        }
        if (s.Tool == "")
        {
            int dim = DimensionAt(screen);
            if (dim >= 0)
            {
                EditDimension(dim, screen);
                return;
            }
            // select a curve
            var f = s.Draft.Frame;
            s.Selected = -1;
            double best = 7;
            for (int i = 0; i < s.Draft.Entities.Count; i++)
            {
                var e = s.Draft.Entities[i];
                var pts = e.Points();
                if (e.Closed) pts.Add(pts[0]);
                for (int k = 0; k + 1 < pts.Count; k++)
                {
                    if (ToScreen(f, pts[k]) is not { } a || ToScreen(f, pts[k + 1]) is not { } b) continue;
                    double d = SegmentDistance(screen, a, b);
                    if (d < best) { best = d; s.Selected = i; }
                }
            }
            ShowSketchPalette();
            View.InvalidateVisual();
            return;
        }
        var q = SketchPoint(screen, out _, out int snapRef);
        SketchInput(q, snapRef);
    }

    /// <summary>Entity whose curve passes within 7 px of the point (-1 if none).</summary>
    private int NearestEntity(Point screen)
    {
        var s = _sk!;
        var f = s.Draft.Frame;
        int found = -1;
        double best = 7;
        for (int i = 0; i < s.Draft.Entities.Count; i++)
        {
            var e = s.Draft.Entities[i];
            var pts = e.Points();
            if (e.Closed) pts.Add(pts[0]);
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                if (ToScreen(f, pts[k]) is not { } a || ToScreen(f, pts[k + 1]) is not { } b) continue;
                double d = SegmentDistance(screen, a, b);
                if (d < best) { best = d; found = i; }
            }
        }
        return found;
    }

    private static double SegmentDistance(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.X * ab.X + ab.Y * ab.Y;
        double t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        var c = a + ab * t;
        return Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y));
    }

    private void ShowSketchPalette()
    {
        var s = _sk;
        if (s == null) return;
        var panel = new CommandPanel("스케치 팔레트", "sketch", null, () => FinishSketch(true));
        panel.AddHeader("옵션");
        panel.AddCheck("스케치 격자", s.ShowGrid, v => { s.ShowGrid = v; View.InvalidateVisual(); });
        panel.AddCheck("스냅", s.Snap, v => s.Snap = v);
        panel.AddNumber("스냅 간격", s.SnapStep, "mm", v => s.SnapStep = Math.Max(0, v));
        panel.AddNumber("평면 간격띄우기", s.Draft.Offset, "mm", v => { s.Draft.Offset = v; View.InvalidateVisual(); });
        panel.AddNumber("다각형 면 수", s.Sides, "", v => s.Sides = Math.Clamp((int)Math.Round(v), 3, 64));
        panel.AddInfo("곡선", $"{s.Draft.Entities.Count}개 · 프로파일 {Profiles.Compute(s.Draft).Count}개");
        panel.AddInfo("구속 상태", s.Draft.Entities.Count == 0 ? "-" : s.Dof == 0 ? "완전히 구속됨" : s.Dof > 0 ? $"자유도 {s.Dof}" : "확인 필요");
        if (s.Draft.Constraints.Count > 0)
        {
            panel.AddHeader($"구속조건 및 치수 ({s.Draft.Constraints.Count})");
            for (int i = 0; i < s.Draft.Constraints.Count; i++)
            {
                int k = i;
                var c = s.Draft.Constraints[i];
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,24"), Height = 26 };
                row.Children.Add(Ui.Label(SketchConstraint.DisplayName(c.Kind), 12));
                if (SketchConstraint.IsDimension(c.Kind))
                {
                    var box = new TextBox { Text = c.Expression ?? CommandPanel.Format(c.Value, c.Kind == "angle" ? "deg" : "mm"), Width = 96, Height = 24, MinHeight = 0, Padding = new Thickness(4, 1), FontSize = 12, Tag = $"치수{i + 1}" };
                    box.KeyDown += (_, e) =>
                    {
                        if (e.Key != Key.Enter || !CommandPanel.TryParse(box.Text, out var v) || v <= 0) return;
                        c.Expression = Params.HasName(box.Text ?? "") ? box.Text!.Trim() : null;
                        SetDimension(k, v);
                    };
                    Grid.SetColumn(box, 1);
                    row.Children.Add(box);
                }
                var del = new FlatButton { Width = 20, Height = 20, Child = Ui.Centered(Icons.Line("close", 10, Ui.Muted)) }.Init();
                ToolTip.SetTip(del, "삭제");
                del.Click += () =>
                {
                    s.Remember();
                    s.Draft.Constraints.RemoveAt(k);
                    SolveSketch();
                    ShowSketchPalette();
                    View.InvalidateVisual();
                };
                Grid.SetColumn(del, 2);
                row.Children.Add(del);
                panel.AddControl(row);
            }
        }
        if (s.Selected >= 0 && s.Selected < s.Draft.Entities.Count)
        {
            var e = s.Draft.Entities[s.Selected];
            panel.AddHeader("선택한 곡선: " + SketchEntity.DisplayName(e.Kind));
            string[] labels = e.Kind switch
            {
                "line" => new[] { "시작 X", "시작 Y", "끝 X", "끝 Y" },
                "rect" => new[] { "구석 1 X", "구석 1 Y", "구석 2 X", "구석 2 Y" },
                "circle" => new[] { "중심 X", "중심 Y", "반지름" },
                "polygon" => new[] { "중심 X", "중심 Y", "반지름", "면 수" },
                _ => new[] { "시작 X", "시작 Y", "끝 X", "끝 Y", "통과점 X", "통과점 Y" },
            };
            for (int i = 0; i < labels.Length && i < e.P.Length; i++)
            {
                int k = i;
                panel.AddNumber(labels[i], e.P[i], labels[i] == "면 수" ? "" : "mm", v => { e.P[k] = v; View.InvalidateVisual(); });
            }
        }
        var finish = new FlatButton
        {
            Height = 30,
            Normal = Ui.Brush("#E8F5EA"),
            HoverBrush = Ui.Brush("#D3EDD7"),
            BorderBrush = Ui.Brush("#9FD3A8"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 6, 0, 0),
            Child = Ui.Centered(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Children = { Icons.Color("finish", 16), Ui.Label("스케치 마무리", 12) } }),
        }.Init();
        finish.Click += () => FinishSketch(true);
        panel.AddControl(finish);
        ShowDialog(panel);
    }
}
