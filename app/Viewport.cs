using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace OpenShape;

public readonly record struct RenderItem(Solid Solid, Color Color, uint Flags);

/// <summary>3D canvas: renders solids through the Rust engine and handles view navigation.</summary>
public sealed unsafe class Viewport : Control
{
    public const uint Selected = 1, Ghost = 2, NoEdges = 4;

    public Camera Cam { get; } = new();
    public IReadOnlyList<RenderItem> Items { get; set; } = Array.Empty<RenderItem>();
    public bool ShowGrid { get; set; } = true;
    public bool ShowEdges { get; set; } = true;
    /// <summary>"" (select), "orbit", "pan" or "zoom": what a left drag does.</summary>
    public string NavMode { get; set; } = "";
    public Action<DrawingContext>? DrawOverlay { get; set; }
    /// <summary>Called on a left press in select mode; returning true routes the drag to Dragging/DragEnded.</summary>
    public Func<Point, bool>? DragStarting { get; set; }
    public Action<Point>? Dragging { get; set; }
    public Action? DragEnded { get; set; }
    private bool _customDrag;

    public event Action<Point, KeyModifiers>? Clicked;
    public event Action<Point>? DoubleClicked;
    public event Action<Point>? Hovered;
    public event Action<Point>? RightClicked;
    public event Action? CameraChanged;

    private WriteableBitmap? _bmp;
    private bool _dirty = true, _queued;
    private Point _down, _last;
    private bool _dragging;
    private MouseButton _button;

    public Viewport()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public double GridStep
    {
        get
        {
            double s = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1e-3, Cam.Distance / 12))));
            return s;
        }
    }

    /// <summary>Re-render the 3D image (coalesced to one render per frame).</summary>
    public void Invalidate3D()
    {
        _dirty = true;
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            EnsureRendered();
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    /// <summary>Render now if anything changed (picking reads the last image).</summary>
    public void EnsureRendered()
    {
        int w = Math.Max(1, (int)Bounds.Width), h = Math.Max(1, (int)Bounds.Height);
        if (_bmp == null || _bmp.PixelSize.Width != w || _bmp.PixelSize.Height != h)
        {
            _bmp?.Dispose();
            _bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
            _dirty = true;
        }
        if (!_dirty) return;
        _dirty = false;
        var items = Items;
        int n = items.Count;
        var handles = new uint[n];
        var colors = new float[n * 3];
        var flags = new uint[n];
        for (int i = 0; i < n; i++)
        {
            handles[i] = items[i].Solid.Handle;
            colors[i * 3] = items[i].Color.R / 255f;
            colors[i * 3 + 1] = items[i].Color.G / 255f;
            colors[i * 3 + 2] = items[i].Color.B / 255f;
            flags[i] = items[i].Flags;
        }
        var cam = Cam.ToArray(w, h);
        using var fb = _bmp.Lock();
        var buf = new byte[w * h * 4];
        fixed (uint* ph = handles, pf = flags)
        fixed (float* pc = colors)
        fixed (double* pcam = cam)
        fixed (byte* pb = buf)
        {
            Native.osc_render(ph, pc, pf, n, pcam, w, h, pb, (ShowGrid ? 1u : 0u) | (ShowEdges ? 2u : 0u), GridStep);
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(pb + y * w * 4, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, w * 4);
        }
        GC.KeepAlive(items);
    }

    /// <summary>Index of the item under p (or -1) and its distance along the view ray.</summary>
    public (int index, double rayDistance) Pick(Point p)
    {
        var (i, t, _) = PickFace(p);
        return (i, t);
    }

    /// <summary>Item, ray distance and B-rep face index (1-based, 0 = none) under p.</summary>
    public (int index, double rayDistance, int face) PickFace(Point p)
    {
        EnsureRendered();
        double depth;
        uint face;
        uint id = Native.osc_pick((int)p.X, (int)p.Y, &depth, &face);
        if (id == 0) return (-1, double.PositiveInfinity, 0);
        var (_, dir) = Ray(p);
        return ((int)id - 1, depth / Math.Max(1e-9, dir.Dot(Cam.Forward)), (int)face);
    }

    public (V3 origin, V3 dir) Ray(Point p) => Cam.Ray(p.X, p.Y, Bounds.Width, Bounds.Height);

    public Point? Project(V3 p)
    {
        var (x, y, z) = Cam.Project(p, Bounds.Width, Bounds.Height);
        return z <= 1e-6 || double.IsNaN(x) ? null : new Point(x, y);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Invalidate3D();
    }

    public override void Render(DrawingContext ctx)
    {
        if (_dirty) EnsureRendered();
        if (_bmp != null) ctx.DrawImage(_bmp, new Rect(_bmp.Size));
        DrawOverlay?.Invoke(ctx);
    }

    // ------------------------------------------------------------ navigation

    public void Orbit(double dx, double dy)
    {
        Cam.Yaw -= dx * 0.4;
        Cam.Pitch = Math.Clamp(Cam.Pitch + dy * 0.4, -89.9, 89.9);
        Changed();
    }

    public void Pan(double dx, double dy)
    {
        double k = Cam.Distance * 2 * Math.Tan(Cam.Fov * Math.PI / 360) / Math.Max(1, Bounds.Height);
        var (r, u) = Cam.Basis();
        Cam.Target = Cam.Target - r * (dx * k) + u * (dy * k);
        Changed();
    }

    public void Zoom(double factor, Point? at = null)
    {
        if (at is { } p)
        {
            // keep the point under the cursor (at the target depth) fixed
            var (o, d) = Ray(p);
            var under = o + d * (Cam.Distance / Math.Max(1e-6, d.Dot(Cam.Forward)));
            Cam.Target = Cam.Target + (under - Cam.Target) * (1 - factor);
        }
        Cam.Distance = Math.Clamp(Cam.Distance * factor, 0.5, 1e6);
        Changed();
    }

    private void Changed()
    {
        Invalidate3D();
        CameraChanged?.Invoke();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Zoom(e.Delta.Y > 0 ? 1 / 1.15 : 1.15, e.GetPosition(this));
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
            DoubleClicked?.Invoke(pt.Position);
            _button = MouseButton.None;
        }
        _customDrag = _button == MouseButton.Left && NavMode == "" && DragStarting?.Invoke(pt.Position) == true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var d = p - _last;
        var props = e.GetCurrentPoint(this).Properties;
        bool down = props.IsLeftButtonPressed || props.IsMiddleButtonPressed || props.IsRightButtonPressed;
        if (down && !_dragging && (Math.Abs(p.X - _down.X) > 3 || Math.Abs(p.Y - _down.Y) > 3)) _dragging = true;
        if (down && _dragging && _customDrag)
        {
            Dragging?.Invoke(p);
        }
        else if (down && _dragging)
        {
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            string mode = _button switch
            {
                MouseButton.Middle => shift ? "orbit" : "pan",
                MouseButton.Right => "orbit",
                MouseButton.Left => NavMode,
                _ => "",
            };
            switch (mode)
            {
                case "orbit": Orbit(d.X, d.Y); break;
                case "pan": Pan(d.X, d.Y); break;
                case "zoom": Zoom(Math.Pow(1.01, d.Y)); break;
            }
        }
        else if (!down) Hovered?.Invoke(p);
        _last = p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        var p = e.GetPosition(this);
        if (_customDrag && _dragging) DragEnded?.Invoke();
        else if (!_dragging)
        {
            if (_button == MouseButton.Left && NavMode == "") Clicked?.Invoke(p, e.KeyModifiers);
            else if (_button == MouseButton.Right) RightClicked?.Invoke(p);
        }
        _customDrag = false;
        _dragging = false;
        _button = MouseButton.None;
    }

    /// <summary>Frame the given bounds.</summary>
    public void Fit(V3 lo, V3 hi)
    {
        var c = (lo + hi) * 0.5;
        double r = Math.Max(1, (hi - lo).Length / 2);
        Cam.Target = c;
        Cam.Distance = r / Math.Sin(Cam.Fov * Math.PI / 360) * 1.15;
        Changed();
    }

    public void SetView(double yaw, double pitch)
    {
        Cam.Yaw = yaw;
        Cam.Pitch = pitch;
        Changed();
    }
}

/// <summary>Navigation cube in the top-right corner: faces snap the view, drag orbits.</summary>
public sealed class ViewCube : Control
{
    private readonly Viewport _vp;
    private Point _down, _last;
    private bool _drag;

    private static readonly (string label, V3 n, double yaw, double pitch)[] Faces =
    {
        ("위", V3.UnitZ, -90, 89.9),
        ("아래", -V3.UnitZ, -90, -89.9),
        ("앞", -V3.UnitY, -90, 0),
        ("뒤", V3.UnitY, 90, 0),
        ("오른쪽", V3.UnitX, 0, 0),
        ("왼쪽", -V3.UnitX, 180, 0),
    };

    public ViewCube(Viewport vp)
    {
        _vp = vp;
        Width = 120;
        Height = 120;
        Cursor = new Cursor(StandardCursorType.Hand);
        vp.CameraChanged += InvalidateVisual;
    }

    private (Point center, double scale) Layout() => (new Point(Bounds.Width / 2, Bounds.Height / 2 + 4), 26);

    private Point P(V3 v)
    {
        var (c, s) = Layout();
        var (r, u) = _vp.Cam.Basis();
        return new Point(c.X + v.Dot(r) * s, c.Y - v.Dot(u) * s);
    }

    private IEnumerable<(string label, V3 n, double yaw, double pitch, Point[] poly, double depth)> VisibleFaces()
    {
        var f = _vp.Cam.Forward;
        foreach (var face in Faces)
        {
            if (face.n.Dot(f) >= -0.02) continue;
            var n = face.n;
            var a = Math.Abs(n.X) > 0.5 ? V3.UnitY : V3.UnitX;
            var b = n.Cross(a);
            var corners = new[] { n + a + b, n + a - b, n - a - b, n - a + b }.Select(P).ToArray();
            yield return (face.label, n, face.yaw, face.pitch, corners, n.Dot(f));
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var (c, s) = Layout();
        // shadow
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)), null, new Point(c.X, c.Y + s * 1.55), s * 1.3, s * 0.22);
        var (r, u) = _vp.Cam.Basis();
        foreach (var face in VisibleFaces().OrderByDescending(f => f.depth))
        {
            double light = 0.86 + 0.12 * Math.Max(0, -face.n.Dot(_vp.Cam.Forward));
            byte g = (byte)(225 * light);
            var geo = new PolylineGeometry(face.poly, true);
            ctx.DrawGeometry(new SolidColorBrush(Color.FromRgb(g, g, (byte)Math.Min(255, g + 3))), new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 0.8), geo);
            // inner face panel
            var mid = new Point(face.poly.Average(p => p.X), face.poly.Average(p => p.Y));
            var inner = face.poly.Select(p => mid + (p - mid) * 0.72).ToArray();
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null, new PolylineGeometry(inner, true));
            double facing = -face.n.Dot(_vp.Cam.Forward);
            if (facing > 0.35)
            {
                var ft = new FormattedText(face.label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 12 * Math.Min(1, facing + 0.2), new SolidColorBrush(Color.FromRgb(110, 112, 118)));
                ctx.DrawText(ft, new Point(mid.X - ft.Width / 2, mid.Y - ft.Height / 2));
            }
        }
        // axis triad from the back-left-bottom corner
        var origin = P(new V3(-1, -1, -1));
        void Axis(V3 d, string name, Color col)
        {
            var end = P(new V3(-1, -1, -1) + d * 2.5);
            ctx.DrawLine(new Pen(new SolidColorBrush(col), 1.6), origin, end);
            var t = new FormattedText(name, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, new SolidColorBrush(col));
            ctx.DrawText(t, new Point(end.X - t.Width / 2 + (end.X - origin.X) * 0.12, end.Y - t.Height / 2 + (end.Y - origin.Y) * 0.12));
        }
        Axis(V3.UnitX, "X", Color.Parse("#F06A6A"));
        Axis(V3.UnitY, "Y", Color.Parse("#46C35A"));
        Axis(V3.UnitZ, "Z", Color.Parse("#6A7CF0"));
        // home button
        var home = new Rect(4, 4, 18, 18);
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(110, 110, 110)), 1.2), Geometry.Parse("M5,13 L13,6 L21,13 M7.5,11 V20 H18.5 V11"));
        _ = home;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _down = _last = e.GetPosition(this);
        _drag = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (!_drag && (p - _down).X * (p - _down).X + (p - _down).Y * (p - _down).Y > 9) _drag = true;
        if (_drag) _vp.Orbit(p.X - _last.X, p.Y - _last.Y);
        _last = p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (_drag) return;
        var p = e.GetPosition(this);
        if (p.X < 26 && p.Y < 26)
        {
            Home?.Invoke();
            return;
        }
        foreach (var f in VisibleFaces().OrderBy(f => f.depth))
            if (Inside(f.poly, p))
            {
                _vp.SetView(f.yaw, f.pitch);
                return;
            }
    }

    public event Action? Home;

    private static bool Inside(Point[] poly, Point p)
    {
        bool c = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) c = !c;
        return c;
    }
}
