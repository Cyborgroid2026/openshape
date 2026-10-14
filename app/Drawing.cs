using System.Globalization;
using System.Text;
using SkiaSharp;

namespace OpenShape;

// ---------------------------------------------------------------- drawing document

/// <summary>
/// A 2D drawing of the design (도면 작업공간): one sheet with orthographic and isometric views made by
/// hidden-line removal, dimensions, notes and a title block. All sheet coordinates are millimetres
/// from the sheet's lower-left corner, y up.
/// </summary>
public sealed class DrawingDoc
{
    public string Name { get; set; } = "도면1";
    public string Sheet { get; set; } = "A3";
    /// <summary>Default view scale (sheet mm per model mm): 0.5 is 1:2.</summary>
    public double Scale { get; set; } = 1;
    /// <summary>First-angle projection (ISO-E); third-angle (KS/ASME) otherwise.</summary>
    public bool FirstAngle { get; set; }
    public bool CenterMarks { get; set; } = true;
    public List<DrawingViewDef> Views { get; set; } = new();
    public List<DrawingDim> Dims { get; set; } = new();
    public List<DrawingNote> Notes { get; set; } = new();
    public TitleBlock Title { get; set; } = new();

    public static readonly string[] Sheets = { "A4", "A3", "A2", "A1", "A0" };

    /// <summary>Landscape sheet size in mm.</summary>
    public (double w, double h) Size => Sheet switch
    {
        "A4" => (297, 210),
        "A2" => (594, 420),
        "A1" => (841, 594),
        "A0" => (1189, 841),
        _ => (420, 297),
    };

    public static readonly double[] Scales = { 10, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01 };

    public static string ScaleText(double s) =>
        s >= 1 ? $"{s.ToString("0.##", CultureInfo.InvariantCulture)}:1" : $"1:{(1 / s).ToString("0.##", CultureInfo.InvariantCulture)}";

    public DrawingViewDef? View(string id) => Views.FirstOrDefault(v => v.Id == id);

    public double ScaleOf(DrawingViewDef v) => v.Scale ?? (v.Parent != null && View(v.Parent) is { } p ? ScaleOf(p) : Scale);

    public string NextViewId() { int n = 1; while (Views.Any(v => v.Id == "V" + n)) n++; return "V" + n; }
}

public sealed class DrawingViewDef
{
    public string Id { get; set; } = "V1";
    /// <summary>front, back, top, bottom, right, left or iso.</summary>
    public string Orientation { get; set; } = "front";
    /// <summary>Centre of the view on the sheet (mm).</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Own scale (base views); projected views follow their parent.</summary>
    public double? Scale { get; set; }
    public string? Parent { get; set; }
    public bool HiddenLines { get; set; } = true;
    public bool TangentEdges { get; set; } = true;

    public static readonly string[] Orientations = { "front", "top", "right", "back", "bottom", "left", "iso" };

    public static string OrientationName(string o) => o switch
    {
        "top" => "위",
        "right" => "오른쪽",
        "back" => "뒤",
        "bottom" => "아래",
        "left" => "왼쪽",
        "iso" => "등각",
        _ => "앞",
    };

    /// <summary>View direction (from the viewer into the model) and the sheet's right direction.</summary>
    public static (V3 dir, V3 right) Directions(string o) => o switch
    {
        "back" => (new V3(0, -1, 0), new V3(-1, 0, 0)),
        "top" => (new V3(0, 0, -1), new V3(1, 0, 0)),
        "bottom" => (new V3(0, 0, 1), new V3(1, 0, 0)),
        "right" => (new V3(-1, 0, 0), new V3(0, 1, 0)),
        "left" => (new V3(1, 0, 0), new V3(0, -1, 0)),
        "iso" => (new V3(-1, 1, -1).Norm(), new V3(1, 1, 0).Norm()),
        _ => (new V3(0, 1, 0), new V3(1, 0, 0)),
    };

    /// <summary>
    /// Orientation of a view projected from <paramref name="parent"/> towards a sheet direction
    /// (dx, dy = -1, 0 or 1). Third angle puts the top view above the front view; first angle below.
    /// </summary>
    public static string Projected(string parent, int dx, int dy, bool firstAngle)
    {
        if (dx != 0 && dy != 0) return "iso";
        if (firstAngle) { dx = -dx; dy = -dy; }
        // turning the model: which face is seen when looking from the side we move towards
        string[] ring = { "front", "right", "back", "left" };
        int i = Array.IndexOf(ring, parent);
        if (dy == 0 && dx != 0)
        {
            if (i >= 0) return ring[(i + (dx > 0 ? 1 : 3)) % 4];
            return dx > 0 ? "right" : "left";
        }
        if (dy != 0)
        {
            if (parent is "top" or "bottom") return parent == "top" ? (dy > 0 ? "back" : "front") : (dy > 0 ? "front" : "back");
            return dy > 0 ? "top" : "bottom";
        }
        return parent;
    }
}

/// <summary>
/// A dimension in a view. Points are in the view's model projection (mm, before scaling), so the
/// dimension moves with the view. Kind: horizontal, vertical, aligned or diameter (A = centre).
/// </summary>
public sealed class DrawingDim
{
    public string View { get; set; } = "";
    public string Kind { get; set; } = "horizontal";
    public double[] A { get; set; } = new double[2];
    public double[] B { get; set; } = new double[2];
    public double[] Place { get; set; } = new double[2];
    public double Radius { get; set; }

    public double Value => Kind switch
    {
        "horizontal" => Math.Abs(B[0] - A[0]),
        "vertical" => Math.Abs(B[1] - A[1]),
        "diameter" => Radius * 2,
        _ => Math.Sqrt((B[0] - A[0]) * (B[0] - A[0]) + (B[1] - A[1]) * (B[1] - A[1])),
    };

    public string Text => (Kind == "diameter" ? "⌀" : "") + Value.ToString("0.##", CultureInfo.InvariantCulture);
}

public sealed class DrawingNote
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = "";
    public double Size { get; set; } = 3.5;
}

public sealed class TitleBlock
{
    public string Title { get; set; } = "";
    public string Number { get; set; } = "";
    public string Author { get; set; } = "";
    public string Checker { get; set; } = "";
    public string Material { get; set; } = "";
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public string Company { get; set; } = "";
}

// ---------------------------------------------------------------- view geometry

/// <summary>Hidden-line projection of a view, in model mm around the view's own centre.</summary>
public sealed class ViewGeometry
{
    public List<(int kind, P2[] pts)> Lines { get; } = new();
    /// <summary>Circles seen face-on (centre, radius) for centre marks and diameter dimensions.</summary>
    public List<(P2 c, double r)> Circles { get; } = new();
    public P2 Min { get; private set; }
    public P2 Max { get; private set; }
    public P2 Center => new((Min.X + Max.X) / 2, (Min.Y + Max.Y) / 2);
    public bool IsEmpty => Lines.Count == 0;

    public static ViewGeometry Build(IReadOnlyList<Solid> solids, string orientation)
    {
        var g = new ViewGeometry();
        if (solids.Count == 0) return g;
        var (dir, right) = DrawingViewDef.Directions(orientation);
        double diag = 1;
        if (solids.Count > 0)
        {
            var lo = solids.Select(s => s.Info.Min).Aggregate((a, b) => new V3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)));
            var hi = solids.Select(s => s.Info.Max).Aggregate((a, b) => new V3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
            diag = Math.Max(1e-3, (hi - lo).Length);
        }
        foreach (var l in Solid.Project(solids, dir, right, diag * 0.0008)) g.Lines.Add(l);
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (_, pts) in g.Lines)
            foreach (var p in pts)
            {
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
            }
        if (g.Lines.Count > 0) { g.Min = new P2(x0, y0); g.Max = new P2(x1, y1); }
        // closed polylines whose points sit on a circle are circles seen face-on
        foreach (var (kind, pts) in g.Lines)
        {
            if (kind is 2 || pts.Length < 8 || pts[0].Dist(pts[^1]) > 1e-6 * diag + 1e-6) continue;
            var c = new P2(pts.Take(pts.Length - 1).Average(p => p.X), pts.Take(pts.Length - 1).Average(p => p.Y));
            double r = pts.Average(p => p.Dist(c));
            if (r < 1e-6 || pts.Any(p => Math.Abs(p.Dist(c) - r) > r * 0.01)) continue;
            if (!g.Circles.Any(q => q.c.Dist(c) < r * 0.01 && Math.Abs(q.r - r) < r * 0.01)) g.Circles.Add((c, r));
        }
        return g;
    }
}

// ---------------------------------------------------------------- rendering

public enum TextAlign { Left, Center, Right }

/// <summary>Line styles of a drawing (ISO 128 widths in mm) with their DXF layer.</summary>
public readonly record struct Stroke(double Width, uint Color, bool Dashed, string Layer)
{
    public static readonly Stroke Visible = new(0.35, 0xFF000000, false, "VISIBLE");
    public static readonly Stroke Tangent = new(0.18, 0xFF8C8C8C, false, "TANGENT");
    public static readonly Stroke Hidden = new(0.18, 0xFF000000, true, "HIDDEN");
    public static readonly Stroke Thin = new(0.18, 0xFF000000, false, "DIM");
    public static readonly Stroke Center = new(0.18, 0xFF000000, false, "CENTER");
    public static readonly Stroke Border = new(0.5, 0xFF000000, false, "BORDER");
    public static readonly Stroke Frame = new(0.25, 0xFF000000, false, "BORDER");
    public static readonly Stroke Highlight = new(0.6, 0xFF0696D7, false, "UI");
}

/// <summary>Output device of the drawing renderer: screen, PDF, SVG or DXF. Coordinates are sheet mm.</summary>
public interface ISheetPainter
{
    void Polyline(IReadOnlyList<P2> pts, Stroke stroke, bool closed = false);
    void Fill(IReadOnlyList<P2> pts, uint color, string layer);
    /// <summary>Text with its baseline-left/centre/right at <paramref name="at"/>, rotated counter-clockwise.</summary>
    void Text(P2 at, string text, double height, TextAlign align, double angleDeg, string layer);
}

public static class DrawingRenderer
{
    /// <summary>Sheet position of a model point of a view.</summary>
    public static P2 ToSheet(DrawingDoc d, DrawingViewDef v, ViewGeometry g, P2 p)
    {
        double s = d.ScaleOf(v);
        var c = g.Center;
        return new P2(v.X + (p.X - c.X) * s, v.Y + (p.Y - c.Y) * s);
    }

    public static P2 ToModel(DrawingDoc d, DrawingViewDef v, ViewGeometry g, P2 sheet)
    {
        double s = d.ScaleOf(v);
        var c = g.Center;
        return new P2(c.X + (sheet.X - v.X) / s, c.Y + (sheet.Y - v.Y) / s);
    }

    /// <summary>Sheet rectangle of a view (min, max), with a small margin.</summary>
    public static (P2 min, P2 max) Bounds(DrawingDoc d, DrawingViewDef v, ViewGeometry g, double margin = 3)
    {
        if (g.IsEmpty) return (new P2(v.X - 10, v.Y - 10), new P2(v.X + 10, v.Y + 10));
        var a = ToSheet(d, v, g, g.Min);
        var b = ToSheet(d, v, g, g.Max);
        return (new P2(a.X - margin, a.Y - margin), new P2(b.X + margin, b.Y + margin));
    }

    public static void Render(DrawingDoc d, Func<DrawingViewDef, ViewGeometry> geometry, ISheetPainter p)
    {
        var (w, h) = d.Size;
        // frame: 10 mm margins (20 mm binding margin on the left for A3 and up, like KS A 0106)
        double left = w >= 400 ? 20 : 10, m = 10;
        p.Polyline(new[] { new P2(0, 0), new P2(w, 0), new P2(w, h), new P2(0, h) }, Stroke.Frame, true);
        p.Polyline(new[] { new P2(left, m), new P2(w - m, m), new P2(w - m, h - m), new P2(left, h - m) }, Stroke.Border, true);
        // centring marks
        foreach (var (a, b) in new[] { (new P2(w / 2, 0), new P2(w / 2, m)), (new P2(w / 2, h - m), new P2(w / 2, h)), (new P2(0, h / 2), new P2(left, h / 2)), (new P2(w - m, h / 2), new P2(w, h / 2)) })
            p.Polyline(new[] { a, b }, Stroke.Border);
        TitleBlockRender(d, p, w - m, m);

        foreach (var v in d.Views)
        {
            var g = geometry(v);
            // hidden lines first so visible edges lying on top of them cover the dashes
            foreach (var (kind, pts) in g.Lines.OrderByDescending(l => l.kind >= 3 ? 2 : l.kind == 2 ? 1 : 0))
            {
                if (kind >= 3 && !v.HiddenLines) continue;
                if (kind == 2 && !v.TangentEdges) continue;
                var stroke = kind switch { 2 => Stroke.Tangent, >= 3 => Stroke.Hidden, _ => Stroke.Visible };
                p.Polyline(pts.Select(q => ToSheet(d, v, g, q)).ToList(), stroke);
            }
            if (d.CenterMarks && v.Orientation != "iso")
                foreach (var (c, r) in g.Circles)
                {
                    var sc = ToSheet(d, v, g, c);
                    double rs = r * d.ScaleOf(v), ext = Math.Min(3, rs * 0.5), arm = rs + Math.Max(1.5, Math.Min(3, rs * 0.3));
                    // centre mark: a small cross plus centre lines running past the circle
                    p.Polyline(new[] { new P2(sc.X - ext, sc.Y), new P2(sc.X + ext, sc.Y) }, Stroke.Center);
                    p.Polyline(new[] { new P2(sc.X, sc.Y - ext), new P2(sc.X, sc.Y + ext) }, Stroke.Center);
                    if (rs > 4)
                    {
                        p.Polyline(new[] { new P2(sc.X - arm, sc.Y), new P2(sc.X - ext - 1, sc.Y) }, Stroke.Center);
                        p.Polyline(new[] { new P2(sc.X + ext + 1, sc.Y), new P2(sc.X + arm, sc.Y) }, Stroke.Center);
                        p.Polyline(new[] { new P2(sc.X, sc.Y - arm), new P2(sc.X, sc.Y - ext - 1) }, Stroke.Center);
                        p.Polyline(new[] { new P2(sc.X, sc.Y + ext + 1), new P2(sc.X, sc.Y + arm) }, Stroke.Center);
                    }
                }
            if (!g.IsEmpty)
            {
                var (lo, _) = Bounds(d, v, g, 0);
                string label = v.Parent == null || v.Scale != null ? $"{DrawingViewDef.OrientationName(v.Orientation)} ({DrawingDoc.ScaleText(d.ScaleOf(v))})" : DrawingViewDef.OrientationName(v.Orientation);
                p.Text(new P2(v.X, lo.Y - 7), label, 3.5, TextAlign.Center, 0, "TEXT");
            }
        }
        foreach (var dim in d.Dims)
            if (d.View(dim.View) is { } v)
                DimRender(d, v, geometry(v), dim, p);
        foreach (var n in d.Notes)
        {
            var lines = n.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++) p.Text(new P2(n.X, n.Y - i * n.Size * 1.6), lines[i], n.Size, TextAlign.Left, 0, "TEXT");
        }
    }

    private static void Arrow(ISheetPainter p, P2 tip, P2 towards)
    {
        var d = towards - tip;
        double len = d.Length;
        if (len < 1e-9) return;
        var u = d * (1 / len);
        var n = new P2(-u.Y, u.X);
        double a = 3, b = 0.5;
        p.Fill(new[] { tip, tip + u * a + n * b, tip + u * a - n * b }, 0xFF000000, "DIM");
    }

    public static void DimRender(DrawingDoc d, DrawingViewDef v, ViewGeometry g, DrawingDim dim, ISheetPainter p)
    {
        P2 S(double[] q) => ToSheet(d, v, g, new P2(q[0], q[1]));
        var a = S(dim.A);
        var place = S(dim.Place);
        const double gap = 1, over = 1.5, th = 3.5;
        if (dim.Kind == "diameter")
        {
            double r = dim.Radius * d.ScaleOf(v);
            var dir = place - a;
            if (dir.Length < 1e-9) dir = new P2(1, 1);
            var u = dir * (1 / dir.Length);
            var edge = a + u * r;
            var far = a - u * r;
            // a leader through the centre with arrows on both sides of the circle, text at the end
            p.Polyline(new[] { far, place }, Stroke.Thin);
            Arrow(p, edge, a);
            Arrow(p, far, a);
            bool rightSide = place.X >= a.X;
            var shelf = new P2(place.X + (rightSide ? 1 : -1) * (dim.Text.Length * th * 0.6 + 1), place.Y);
            p.Polyline(new[] { place, shelf }, Stroke.Thin);
            p.Text(new P2((place.X + shelf.X) / 2, place.Y + 0.8), dim.Text, th, TextAlign.Center, 0, "DIM");
            return;
        }
        var b = S(dim.B);
        P2 da, db, ea, eb;
        double angle;
        switch (dim.Kind)
        {
            case "horizontal":
            {
                double y = place.Y;
                (da, db) = (new P2(a.X, y), new P2(b.X, y));
                double s = Math.Sign(y - (a.Y + b.Y) / 2);
                if (s == 0) s = 1;
                ea = new P2(a.X, a.Y + s * gap);
                eb = new P2(b.X, b.Y + s * gap);
                p.Polyline(new[] { ea, new P2(a.X, y + s * over) }, Stroke.Thin);
                p.Polyline(new[] { eb, new P2(b.X, y + s * over) }, Stroke.Thin);
                angle = 0;
                break;
            }
            case "vertical":
            {
                double x = place.X;
                (da, db) = (new P2(x, a.Y), new P2(x, b.Y));
                double s = Math.Sign(x - (a.X + b.X) / 2);
                if (s == 0) s = 1;
                p.Polyline(new[] { new P2(a.X + s * gap, a.Y), new P2(x + s * over, a.Y) }, Stroke.Thin);
                p.Polyline(new[] { new P2(b.X + s * gap, b.Y), new P2(x + s * over, b.Y) }, Stroke.Thin);
                angle = 90;
                break;
            }
            default:
            {
                var ab = b - a;
                double len = ab.Length;
                if (len < 1e-9) return;
                var n = new P2(-ab.Y / len, ab.X / len);
                double off = (place - a).X * n.X + (place - a).Y * n.Y;
                double s = Math.Sign(off);
                if (s == 0) s = 1;
                (da, db) = (a + n * off, b + n * off);
                p.Polyline(new[] { a + n * (s * gap), da + n * (s * over) }, Stroke.Thin);
                p.Polyline(new[] { b + n * (s * gap), db + n * (s * over) }, Stroke.Thin);
                angle = Math.Atan2(ab.Y, ab.X) * 180 / Math.PI;
                if (angle > 90.001) angle -= 180;
                if (angle < -89.999) angle += 180;
                break;
            }
        }
        p.Polyline(new[] { da, db }, Stroke.Thin);
        Arrow(p, da, db);
        Arrow(p, db, da);
        var mid = new P2((da.X + db.X) / 2, (da.Y + db.Y) / 2);
        double rad = angle * Math.PI / 180;
        var up = new P2(-Math.Sin(rad), Math.Cos(rad));
        p.Text(mid + up * 0.8, dim.Text, th, TextAlign.Center, angle, "DIM");
    }

    /// <summary>KS-style title block anchored at its lower-right corner.</summary>
    private static void TitleBlockRender(DrawingDoc d, ISheetPainter p, double right, double bottom)
    {
        const double W = 180, H = 36;
        double x0 = right - W, y0 = bottom;
        void Box(double x, double y, double w, double h) =>
            p.Polyline(new[] { new P2(x, y), new P2(x + w, y), new P2(x + w, y + h), new P2(x, y + h) }, Stroke.Frame, true);
        void Cell(double x, double y, double w, double h, string label, string value, double size = 3.5)
        {
            Box(x, y, w, h);
            p.Text(new P2(x + 1.2, y + h - 2.6), label, 1.8, TextAlign.Left, 0, "TEXT");
            if (value.Length > 0) p.Text(new P2(x + w / 2, y + h / 2 - size / 2 + (h > 10 ? -1 : -0.3)), value, size, TextAlign.Center, 0, "TEXT");
        }
        Box(x0, y0, W, H);
        var t = d.Title;
        // row 1 (top): title | number
        Cell(x0, y0 + 18, 120, 18, "제목", t.Title.Length > 0 ? t.Title : d.Name, 5);
        Cell(x0 + 120, y0 + 18, 60, 18, "도면 번호", t.Number, 4);
        // row 2: author, checker, material, date
        Cell(x0, y0 + 9, 40, 9, "작성", t.Author, 3);
        Cell(x0 + 40, y0 + 9, 40, 9, "검토", t.Checker, 3);
        Cell(x0 + 80, y0 + 9, 60, 9, "재료", t.Material, 3);
        Cell(x0 + 140, y0 + 9, 40, 9, "날짜", t.Date, 3);
        // row 3: company, scale, projection, units, sheet
        Cell(x0, y0, 80, 9, "회사", t.Company, 3);
        Cell(x0 + 80, y0, 25, 9, "척도", DrawingDoc.ScaleText(d.Scale), 3);
        Cell(x0 + 105, y0, 30, 9, "투상법", d.FirstAngle ? "1각법" : "3각법", 3);
        Cell(x0 + 135, y0, 20, 9, "단위", "mm", 3);
        Cell(x0 + 155, y0, 25, 9, "시트", d.Sheet, 3);
    }
}

// ---------------------------------------------------------------- exporters

/// <summary>PDF (vector, fonts embedded) through SkiaSharp.</summary>
public sealed class SkiaSheetPainter : ISheetPainter, IDisposable
{
    private readonly SKCanvas _c;
    private readonly double _h, _k;
    private readonly SKTypeface _face;

    /// <param name="sheetHeight">Sheet height in mm (y is flipped).</param>
    /// <param name="unitsPerMm">Device units per mm (72/25.4 for PDF points).</param>
    public SkiaSheetPainter(SKCanvas c, double sheetHeight, double unitsPerMm)
    {
        _c = c;
        _h = sheetHeight;
        _k = unitsPerMm;
        _face = KoreanTypeface();
    }

    public static SKTypeface KoreanTypeface()
    {
        foreach (var name in new[] { "Noto Sans CJK KR", "NanumGothic", "Nanum Gothic", "NanumSquare", "Noto Sans KR", "UnDotum" })
        {
            var f = SKTypeface.FromFamilyName(name);
            if (f != null && f.FamilyName.Replace(" ", "").Contains(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) return f;
        }
        return SKFontManager.Default.MatchCharacter('가') ?? SKTypeface.Default;
    }

    private SKPoint P(P2 p) => new((float)(p.X * _k), (float)((_h - p.Y) * _k));

    public void Polyline(IReadOnlyList<P2> pts, Stroke s, bool closed = false)
    {
        if (pts.Count < 2) return;
        using var paint = new SKPaint { Color = new SKColor(s.Color), StrokeWidth = (float)(s.Width * _k), IsStroke = true, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        if (s.Dashed) paint.PathEffect = SKPathEffect.CreateDash(new[] { (float)(3 * _k), (float)(1.5 * _k) }, 0);
        using var path = new SKPath();
        path.MoveTo(P(pts[0]));
        for (int i = 1; i < pts.Count; i++) path.LineTo(P(pts[i]));
        if (closed) path.Close();
        _c.DrawPath(path, paint);
    }

    public void Fill(IReadOnlyList<P2> pts, uint color, string layer)
    {
        using var paint = new SKPaint { Color = new SKColor(color), IsStroke = false, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo(P(pts[0]));
        for (int i = 1; i < pts.Count; i++) path.LineTo(P(pts[i]));
        path.Close();
        _c.DrawPath(path, paint);
    }

    public void Text(P2 at, string text, double height, TextAlign align, double angleDeg, string layer)
    {
        float size = (float)(height * _k / 0.72);   // cap height ≈ 0.72 em
        // split into runs by font: symbols such as ⌀ are missing from CJK fonts and come from a fallback
        var runs = new List<(string text, SKFont font)>();
        try
        {
            foreach (var ch in text)
            {
                var face = _face.ContainsGlyph(ch) ? _face : SKFontManager.Default.MatchCharacter(ch) ?? _face;
                if (runs.Count > 0 && runs[^1].font.Typeface == face) runs[^1] = (runs[^1].text + ch, runs[^1].font);
                else runs.Add((ch.ToString(), new SKFont(face, size)));
            }
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            float width = runs.Sum(r => Measure(r.text, r.font));
            float x = align switch { TextAlign.Center => -width / 2, TextAlign.Right => -width, _ => 0 };
            var o = P(at);
            _c.Save();
            _c.Translate(o.X, o.Y);
            _c.RotateDegrees((float)-angleDeg);
            foreach (var (t, f) in runs)
            {
                _c.DrawText(t, x, 0, f, paint);
                x += Measure(t, f);
            }
            _c.Restore();
        }
        finally
        {
            foreach (var r in runs) r.font.Dispose();
        }
    }

    private static float Measure(string text, SKFont font)
    {
        using var m = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        return m.MeasureText(text);
    }

    public void Dispose() => _face.Dispose();
}

/// <summary>Collects polylines and text as DXF R12 entities on per-style layers.</summary>
public sealed class DxfSheetPainter : ISheetPainter
{
    private readonly StringBuilder _e = new();
    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
            if (ch < 128) sb.Append(ch);
            else sb.Append($"\\U+{(int)ch:X4}");
        return sb.ToString();
    }

    public void Polyline(IReadOnlyList<P2> pts, Stroke s, bool closed = false)
    {
        int n = closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            _e.Append($"0\nLINE\n8\n{s.Layer}\n10\n{F(a.X)}\n20\n{F(a.Y)}\n30\n0\n11\n{F(b.X)}\n21\n{F(b.Y)}\n31\n0\n");
        }
    }

    public void Fill(IReadOnlyList<P2> pts, uint color, string layer)
    {
        if (pts.Count < 3) return;
        var q = pts.Count == 3 ? new[] { pts[0], pts[1], pts[2], pts[2] } : pts.Take(4).ToArray();
        _e.Append($"0\nSOLID\n8\n{layer}\n");
        // SOLID vertex order is 1, 2, 4, 3
        for (int i = 0; i < 4; i++) _e.Append($"{10 + i}\n{F(q[i == 2 ? 3 : i == 3 ? 2 : i].X)}\n{20 + i}\n{F(q[i == 2 ? 3 : i == 3 ? 2 : i].Y)}\n{30 + i}\n0\n");
    }

    public void Text(P2 at, string text, double height, TextAlign align, double angleDeg, string layer)
    {
        int h = align switch { TextAlign.Center => 1, TextAlign.Right => 2, _ => 0 };
        _e.Append($"0\nTEXT\n8\n{layer}\n10\n{F(at.X)}\n20\n{F(at.Y)}\n30\n0\n40\n{F(height)}\n1\n{Escape(text)}\n50\n{F(angleDeg)}\n");
        if (h != 0) _e.Append($"72\n{h}\n11\n{F(at.X)}\n21\n{F(at.Y)}\n31\n0\n");
    }

    public string Document()
    {
        var sb = new StringBuilder();
        sb.Append("0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1009\n9\n$INSUNITS\n70\n4\n0\nENDSEC\n");
        sb.Append("0\nSECTION\n2\nTABLES\n");
        sb.Append("0\nTABLE\n2\nLTYPE\n70\n2\n");
        sb.Append("0\nLTYPE\n2\nCONTINUOUS\n70\n0\n3\nSolid line\n72\n65\n73\n0\n40\n0\n");
        sb.Append("0\nLTYPE\n2\nHIDDEN\n70\n0\n3\n__ __ __\n72\n65\n73\n2\n40\n4.5\n49\n3\n49\n-1.5\n");
        sb.Append("0\nENDTAB\n0\nTABLE\n2\nLAYER\n70\n7\n");
        foreach (var (name, color, lt) in new[] { ("VISIBLE", 7, "CONTINUOUS"), ("HIDDEN", 7, "HIDDEN"), ("TANGENT", 8, "CONTINUOUS"), ("DIM", 3, "CONTINUOUS"), ("CENTER", 1, "CONTINUOUS"), ("TEXT", 7, "CONTINUOUS"), ("BORDER", 7, "CONTINUOUS") })
            sb.Append($"0\nLAYER\n2\n{name}\n70\n0\n62\n{color}\n6\n{lt}\n");
        sb.Append("0\nENDTAB\n0\nENDSEC\n");
        sb.Append("0\nSECTION\n2\nENTITIES\n").Append(_e).Append("0\nENDSEC\n0\nEOF\n");
        return sb.ToString();
    }
}

/// <summary>SVG of the sheet (mm units), for quick viewing in a browser.</summary>
public sealed class SvgSheetPainter : ISheetPainter
{
    private readonly StringBuilder _b = new();
    private readonly double _w, _h;
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public SvgSheetPainter(double w, double h) { _w = w; _h = h; }

    private string Pt(P2 p) => $"{F(p.X)},{F(_h - p.Y)}";

    private static string Hex(uint c) => $"#{c & 0xFFFFFF:X6}";

    public void Polyline(IReadOnlyList<P2> pts, Stroke s, bool closed = false)
    {
        if (pts.Count < 2) return;
        _b.Append($"<{(closed ? "polygon" : "polyline")} points=\"{string.Join(' ', pts.Select(Pt))}\" fill=\"none\" stroke=\"{Hex(s.Color)}\" stroke-width=\"{F(s.Width)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"{(s.Dashed ? " stroke-dasharray=\"3 1.5\"" : "")}/>\n");
    }

    public void Fill(IReadOnlyList<P2> pts, uint color, string layer) =>
        _b.Append($"<polygon points=\"{string.Join(' ', pts.Select(Pt))}\" fill=\"{Hex(color)}\"/>\n");

    public void Text(P2 at, string text, double height, TextAlign align, double angleDeg, string layer)
    {
        string anchor = align switch { TextAlign.Center => "middle", TextAlign.Right => "end", _ => "start" };
        var esc = System.Security.SecurityElement.Escape(text);
        _b.Append($"<text x=\"{F(at.X)}\" y=\"{F(_h - at.Y)}\" font-size=\"{F(height / 0.72)}\" text-anchor=\"{anchor}\" font-family=\"Noto Sans CJK KR, NanumGothic, sans-serif\"{(Math.Abs(angleDeg) > 1e-9 ? $" transform=\"rotate({F(-angleDeg)} {F(at.X)} {F(_h - at.Y)})\"" : "")}>{esc}</text>\n");
    }

    public string Document() =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(_w)}mm\" height=\"{F(_h)}mm\" viewBox=\"0 0 {F(_w)} {F(_h)}\">\n<rect width=\"100%\" height=\"100%\" fill=\"white\"/>\n{_b}</svg>\n";
}

public static class DrawingExport
{
    public static void Pdf(DrawingDoc d, Func<DrawingViewDef, ViewGeometry> geometry, string path)
    {
        var (w, h) = d.Size;
        const double k = 72 / 25.4;
        using var stream = File.Create(path);
        using var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = d.Title.Title.Length > 0 ? d.Title.Title : d.Name, Creator = "OpenShape CAD", Producer = "OpenShape CAD" });
        var canvas = doc.BeginPage((float)(w * k), (float)(h * k));
        using (var painter = new SkiaSheetPainter(canvas, h, k)) DrawingRenderer.Render(d, geometry, painter);
        doc.EndPage();
        doc.Close();
    }

    public static void Dxf(DrawingDoc d, Func<DrawingViewDef, ViewGeometry> geometry, string path)
    {
        var p = new DxfSheetPainter();
        DrawingRenderer.Render(d, geometry, p);
        File.WriteAllText(path, p.Document());
    }

    public static void Svg(DrawingDoc d, Func<DrawingViewDef, ViewGeometry> geometry, string path)
    {
        var (w, h) = d.Size;
        var p = new SvgSheetPainter(w, h);
        DrawingRenderer.Render(d, geometry, p);
        File.WriteAllText(path, p.Document());
    }

    /// <summary>PNG raster of the sheet (for previews and tests).</summary>
    public static void Png(DrawingDoc d, Func<DrawingViewDef, ViewGeometry> geometry, string path, double pxPerMm = 4)
    {
        var (w, h) = d.Size;
        using var bmp = new SKBitmap((int)(w * pxPerMm), (int)(h * pxPerMm));
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.White);
        using (var painter = new SkiaSheetPainter(c, h, pxPerMm)) DrawingRenderer.Render(d, geometry, painter);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        using var f = File.Create(path);
        data.SaveTo(f);
    }
}
