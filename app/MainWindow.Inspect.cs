using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenShape;

/// <summary>Inspect commands: interference and centre of mass (with physical materials).</summary>
public sealed partial class MainWindow
{
    /// <summary>Overlapping volumes shown in red while the interference panel is open.</summary>
    private readonly List<Solid> _interference = new();
    private V3? _massMarker;

    private static readonly Color InterferenceColor = Color.FromRgb(225, 45, 45);

    private List<Body> InspectedBodies() =>
        (_selBodies.Count > 0 ? _selBodies.Select(State.Find).Where(b => b != null).Cast<Body>() : State.Bodies.Where(b => !_doc.HiddenBodies.Contains(b.Id))).ToList();

    /// <summary>Pairs of bodies that overlap, with the shared volume.</summary>
    public List<(string a, string b, Solid common)> Interferences(IReadOnlyList<Body> bodies)
    {
        var result = new List<(string, string, Solid)>();
        for (int i = 0; i < bodies.Count; i++)
            for (int j = i + 1; j < bodies.Count; j++)
            {
                var (x, y) = (bodies[i].Solid.Info, bodies[j].Solid.Info);
                if (x.Solids == 0 || y.Solids == 0) continue;
                if (x.Min.X > y.Max.X || y.Min.X > x.Max.X || x.Min.Y > y.Max.Y || y.Min.Y > x.Max.Y || x.Min.Z > y.Max.Z || y.Min.Z > x.Max.Z) continue;
                try
                {
                    var c = bodies[i].Solid.Boolean(bodies[j].Solid, BoolOp.Intersect);
                    if (c.Info.Volume > 1e-6) result.Add((bodies[i].Id, bodies[j].Id, c));
                }
                catch (InvalidOperationException) { }
            }
        return result;
    }

    private void ShowInterference()
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "interference" };
        var p = new CommandPanel("간섭", "interference", CancelCommand, CancelCommand, "닫기");
        var bodies = InspectedBodies();
        p.AddInfo("대상", _selBodies.Count > 0 ? $"선택한 본체 {bodies.Count}개" : $"보이는 본체 {bodies.Count}개");
        var found = Interferences(bodies);
        _interference.AddRange(found.Select(f => f.common));
        if (found.Count == 0) p.AddText("간섭이 없습니다.");
        else
        {
            p.AddHeader($"간섭 {found.Count}건");
            foreach (var (a, b, common) in found)
                p.AddInfo($"{BodyName(a)} ↔ {BodyName(b)}", $"{common.Info.Volume:#,0.###} mm³");
            p.AddText("겹치는 부분이 빨간색으로 표시됩니다.");
        }
        _cmd.Panel = p;
        ShowDialog(p);
        UpdateItems();
    }

    private void ShowCenterOfMass()
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "centerofmass" };
        _cmd.Rebuild = () =>
        {
            var p = new CommandPanel("무게 중심", "centerofmass", CancelCommand, CancelCommand, "닫기");
            var bodies = InspectedBodies().Where(b => b.Solid.Info.Solids > 0).ToList();
            if (bodies.Count == 0)
            {
                p.AddText("솔리드 본체가 없습니다.");
                _massMarker = null;
            }
            else
            {
                double mass = 0, volume = 0;
                var moment = V3.Zero;
                foreach (var b in bodies)
                {
                    var (v, c, _) = b.Solid.MassProperties;
                    double m = v / 1000 * _doc.Density(b.Id);   // mm³ → cm³ → g
                    mass += m;
                    volume += v;
                    moment += c * m;
                }
                var com = mass > 0 ? moment * (1 / mass) : V3.Zero;
                _massMarker = com;
                string F(double v) => v.ToString("#,0.###", System.Globalization.CultureInfo.InvariantCulture);
                p.AddInfo("대상", _selBodies.Count > 0 ? $"선택한 본체 {bodies.Count}개" : $"보이는 본체 {bodies.Count}개");
                p.AddInfo("질량", mass >= 1000 ? $"{F(mass / 1000)} kg" : $"{F(mass)} g");
                p.AddInfo("부피", $"{F(volume)} mm³");
                p.AddInfo("무게 중심 X", $"{F(com.X)} mm");
                p.AddInfo("무게 중심 Y", $"{F(com.Y)} mm");
                p.AddInfo("무게 중심 Z", $"{F(com.Z)} mm");
                p.AddHeader("물리 재질");
                var names = Doc.Materials.Select(m => $"{m.name} ({m.density:0.##} g/cm³)").ToArray();
                foreach (var b in bodies)
                {
                    var id = b.Id;
                    int cur = Math.Max(0, Array.FindIndex(Doc.Materials, m => m.name == _doc.BodyMaterials.GetValueOrDefault(id, "강철")));
                    p.AddCombo(BodyName(id), names, cur, i =>
                    {
                        var name = Doc.Materials[i].name;
                        if (_doc.BodyMaterials.GetValueOrDefault(id, "강철") == name) return;
                        _doc.BodyMaterials[id] = name;
                        _dirty = true;
                        UpdateTitle();
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => _cmd?.Rebuild?.Invoke());
                    });
                }
            }
            _cmd!.Panel = p;
            ShowDialog(p);
            View.InvalidateVisual();
        };
        _cmd.Rebuild();
    }

    /// <summary>Overlay for inspect results (the centre of mass glyph).</summary>
    private void DrawInspectOverlay(DrawingContext ctx)
    {
        if (_massMarker is not { } m || View.Project(m) is not { } s) return;
        var pen = new Pen(Brushes.Black, 1.2);
        ctx.DrawEllipse(Brushes.White, pen, s, 9, 9);
        // quartered disc like the usual centre-of-gravity symbol
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(s, true);
            c.LineTo(new Point(s.X + 9, s.Y));
            c.ArcTo(new Point(s.X, s.Y + 9), new Size(9, 9), 0, false, SweepDirection.Clockwise);
            c.EndFigure(true);
            c.BeginFigure(s, true);
            c.LineTo(new Point(s.X - 9, s.Y));
            c.ArcTo(new Point(s.X, s.Y - 9), new Size(9, 9), 0, false, SweepDirection.Clockwise);
            c.EndFigure(true);
        }
        ctx.DrawGeometry(Brushes.Black, null, g);
    }
}
