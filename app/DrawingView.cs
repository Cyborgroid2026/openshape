using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace OpenShape;

/// <summary>Draws the sheet onto an Avalonia drawing context (sheet mm → screen pixels).</summary>
public sealed class AvaloniaSheetPainter : ISheetPainter
{
    private readonly DrawingContext _ctx;
    private readonly Func<P2, Point> _map;
    private readonly double _pxPerMm;
    private static readonly Dictionary<(uint, double, bool), IPen> Pens = new();
    private static readonly Typeface Face = new("Noto Sans CJK KR, NanumGothic, Inter, sans-serif");

    public AvaloniaSheetPainter(DrawingContext ctx, Func<P2, Point> map, double pxPerMm)
    {
        _ctx = ctx;
        _map = map;
        _pxPerMm = pxPerMm;
    }

    private IPen PenOf(Stroke s)
    {
        // keep thin lines visible when zoomed out
        double w = Math.Max(s.Width * _pxPerMm, s.Width >= 0.3 ? 1.2 : 0.7);
        var key = (s.Color, Math.Round(w, 2), s.Dashed);
        if (!Pens.TryGetValue(key, out var pen))
        {
            pen = new Pen(new SolidColorBrush(Color.FromUInt32(s.Color)), w, s.Dashed ? new DashStyle(new[] { 3 * _pxPerMm / w, 1.5 * _pxPerMm / w }, 0) : null, PenLineCap.Round, PenLineJoin.Round);
            if (Pens.Count > 400) Pens.Clear();
            Pens[key] = pen;
        }
        return pen;
    }

    public void Polyline(IReadOnlyList<P2> pts, Stroke stroke, bool closed = false)
    {
        if (pts.Count < 2) return;
        var g = new PolylineGeometry(pts.Select(_map).ToList(), closed);
        _ctx.DrawGeometry(null, PenOf(stroke), g);
    }

    public void Fill(IReadOnlyList<P2> pts, uint color, string layer) =>
        _ctx.DrawGeometry(new SolidColorBrush(Color.FromUInt32(color)), null, new PolylineGeometry(pts.Select(_map).ToList(), true));

    public void Text(P2 at, string text, double height, TextAlign align, double angleDeg, string layer)
    {
        double size = height / 0.72 * _pxPerMm;
        if (size < 2) return;
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, Brushes.Black);
        var o = _map(at);
        double dx = align switch { TextAlign.Center => -ft.Width / 2, TextAlign.Right => -ft.Width, _ => 0 };
        // the anchor is on the baseline
        var origin = new Point(dx, -ft.Baseline);
        using (_ctx.PushTransform(Matrix.CreateRotation(-angleDeg * Math.PI / 180) * Matrix.CreateTranslation(o.X, o.Y)))
            _ctx.DrawText(ft, origin);
    }
}

/// <summary>Sheet canvas of the drawing workspace: zoom with the wheel, pan with the middle button.</summary>
public sealed class DrawingView : Control
{
    public DrawingDoc? Drawing { get; set; }
    public Func<DrawingViewDef, ViewGeometry>? Geometry { get; set; }
    public string? SelectedView { get; set; }
    public int SelectedDim { get; set; } = -1;
    public int SelectedNote { get; set; } = -1;
    /// <summary>Extra drawing on top of the sheet (rubber bands, snap markers), in sheet mm.</summary>
    public Action<ISheetPainter>? Overlay { get; set; }

    public event Action<P2, KeyModifiers>? Clicked;
    public event Action<P2>? DoubleClicked;
    public event Action<P2>? Hovered;
    /// <summary>Return true to take a left-button drag (moving a view or a dimension).</summary>
    public Func<P2, bool>? DragStarting { get; set; }
    public Action<P2>? Dragging { get; set; }
    public Action? DragEnded { get; set; }

    private double _zoom = 2;      // px per mm
    private Point _pan;            // screen position of the sheet origin (lower-left)
    private bool _fitted;
    private Point _down, _last;
    private bool _dragging, _custom;
    private MouseButton _button;

    public DrawingView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public double PxPerMm => _zoom;

    public Point ToScreen(P2 p) => new(_pan.X + p.X * _zoom, _pan.Y - p.Y * _zoom);
    public P2 ToSheet(Point s) => new((s.X - _pan.X) / _zoom, (_pan.Y - s.Y) / _zoom);

    public void Fit()
    {
        FitCore();
        InvalidateVisual();
    }

    /// <summary>Zoom so the whole sheet shows (deferred until the control has a size).</summary>
    private void FitCore()
    {
        if (Drawing == null || Bounds.Width < 10) { _fitted = false; return; }
        var (w, h) = Drawing.Size;
        _zoom = Math.Min((Bounds.Width - 60) / w, (Bounds.Height - 60) / h);
        _pan = new Point((Bounds.Width - w * _zoom) / 2, (Bounds.Height + h * _zoom) / 2);
        _fitted = true;
    }

    public override void Render(DrawingContext ctx)
    {
        if (!_fitted) FitCore();
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0xB9, 0xBD, 0xC2)), new Rect(Bounds.Size));
        if (Drawing is not { } d || Geometry == null) return;
        var (w, h) = d.Size;
        var lo = ToScreen(new P2(0, 0));
        var hi = ToScreen(new P2(w, h));
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), new Rect(new Point(hi.X + 4, hi.Y + 4), new Point(hi.X + 4 + (lo.X - hi.X), lo.Y + 4)).Normalize());
        ctx.FillRectangle(Brushes.White, new Rect(lo, hi).Normalize());
        var painter = new AvaloniaSheetPainter(ctx, ToScreen, _zoom);
        DrawingRenderer.Render(d, Geometry, painter);
        if (SelectedView != null && d.View(SelectedView) is { } v)
        {
            var (a, b) = DrawingRenderer.Bounds(d, v, Geometry(v));
            painter.Polyline(new[] { a, new P2(b.X, a.Y), b, new P2(a.X, b.Y) }, new Stroke(0.35, 0xFF0696D7, true, "UI"), true);
        }
        if (SelectedDim >= 0 && SelectedDim < d.Dims.Count && d.View(d.Dims[SelectedDim].View) is { } dv)
        {
            var hl = new HighlightPainter(painter);
            DrawingRenderer.DimRender(d, dv, Geometry(dv), d.Dims[SelectedDim], hl);
        }
        if (SelectedNote >= 0 && SelectedNote < d.Notes.Count)
        {
            var n = d.Notes[SelectedNote];
            painter.Polyline(new[] { new P2(n.X - 1, n.Y - 1.5), new P2(n.X + 40, n.Y - 1.5) }, Stroke.Highlight);
        }
        Overlay?.Invoke(painter);
    }

    /// <summary>Re-draws strokes in the highlight colour (selected dimension).</summary>
    private sealed class HighlightPainter : ISheetPainter
    {
        private readonly ISheetPainter _p;
        public HighlightPainter(ISheetPainter p) => _p = p;
        public void Polyline(IReadOnlyList<P2> pts, Stroke stroke, bool closed = false) => _p.Polyline(pts, Stroke.Highlight, closed);
        public void Fill(IReadOnlyList<P2> pts, uint color, string layer) => _p.Fill(pts, 0xFF0696D7, layer);
        public void Text(P2 at, string text, double height, TextAlign align, double angleDeg, string layer) { }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var at = e.GetPosition(this);
        var s = ToSheet(at);
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.1, 80);
        _pan = new Point(at.X - s.X * _zoom, at.Y + s.Y * _zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        _down = _last = pt.Position;
        _dragging = false;
        _button = pt.Properties.IsMiddleButtonPressed ? MouseButton.Middle : pt.Properties.IsRightButtonPressed ? MouseButton.Right : MouseButton.Left;
        if (_button == MouseButton.Left && e.ClickCount == 2)
        {
            DoubleClicked?.Invoke(ToSheet(pt.Position));
            _button = MouseButton.None;
        }
        _custom = _button == MouseButton.Left && DragStarting?.Invoke(ToSheet(pt.Position)) == true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        bool down = props.IsLeftButtonPressed || props.IsMiddleButtonPressed || props.IsRightButtonPressed;
        if (down && !_dragging && (Math.Abs(p.X - _down.X) > 3 || Math.Abs(p.Y - _down.Y) > 3)) _dragging = true;
        if (down && _dragging)
        {
            if (_custom) Dragging?.Invoke(ToSheet(p));
            else if (_button is MouseButton.Middle or MouseButton.Right || _button == MouseButton.Left)
            {
                _pan += p - _last;
                InvalidateVisual();
            }
        }
        else if (!down) Hovered?.Invoke(ToSheet(p));
        _last = p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        var p = e.GetPosition(this);
        if (_custom && _dragging) DragEnded?.Invoke();
        else if (!_dragging && _button == MouseButton.Left) Clicked?.Invoke(ToSheet(p), e.KeyModifiers);
        _custom = false;
        _dragging = false;
        _button = MouseButton.None;
    }
}
