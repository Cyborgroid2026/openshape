using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace OpenShape;

/// <summary>Colours of the CAD chrome (light theme, measured from the reference screenshots).</summary>
public static class Ui
{
    public static readonly IBrush AppBar = Brush("#D9D9D9");
    public static readonly IBrush Bar = Brush("#F5F5F5");
    public static readonly IBrush Hover = Brush("#E3E3E3");
    public static readonly IBrush Pressed = Brush("#D5E8F6");
    public static readonly IBrush Line = Brush("#C7C7C7");
    public static readonly IBrush Text = Brush("#3C3C3C");
    public static readonly IBrush Muted = Brush("#808080");
    public static readonly IBrush Disabled = Brush("#B4B4B4");
    public static readonly IBrush Accent = Brush("#0696D7");
    public static readonly IBrush AccentSoft = Brush("#DDF1FB");
    public static readonly IBrush White = Brush("#FFFFFF");
    public static readonly IBrush Row = Brush("#EDEDED");

    public static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    public static TextBlock Label(string text, double size = 12, IBrush? color = null, FontWeight weight = FontWeight.Normal) =>
        new() { Text = text, FontSize = size, Foreground = color ?? Text, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };

    public static T Centered<T>(T c) where T : Control
    {
        c.HorizontalAlignment = HorizontalAlignment.Center;
        c.VerticalAlignment = VerticalAlignment.Center;
        return c;
    }
}

/// <summary>Flat clickable area with hover / selected backgrounds.</summary>
public class FlatButton : Border
{
    public IBrush Normal { get; set; } = Brushes.Transparent;
    public IBrush HoverBrush { get; set; } = Ui.Hover;
    public IBrush? SelectedBrush { get; set; } = Ui.Pressed;
    public event Action? Click;
    public event Action? DoubleClick;
    public event Action<PointerPressedEventArgs>? RightClick;
    private bool _over, _pressed, _selected;

    public FlatButton()
    {
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsEnabledProperty)
            {
                Opacity = IsEnabled ? 1 : 0.4;
                Update();
            }
        };
    }

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; Update(); }
    }

    public FlatButton Init()
    {
        Update();
        return this;
    }

    private void Update() => Background = _selected && SelectedBrush != null ? SelectedBrush : _over && IsEnabled ? HoverBrush : Normal;

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); _over = true; Update(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _over = false; Update(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this).Properties;
        if (pt.IsRightButtonPressed)
        {
            RightClick?.Invoke(e);
            e.Handled = true;
            return;
        }
        if (!pt.IsLeftButtonPressed) return;
        if (e.ClickCount == 2 && DoubleClick != null)
        {
            DoubleClick.Invoke();
            e.Handled = true;
            return;
        }
        _pressed = true;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        var p = e.GetPosition(this);
        if (IsEnabled && p.X >= 0 && p.Y >= 0 && p.X <= Bounds.Width && p.Y <= Bounds.Height) Click?.Invoke();
        e.Handled = true;
    }
}

/// <summary>
/// Icons drawn on a 24px grid. Each layer is (path, fill, stroke): fill/stroke null = none,
/// "ink" = default dark outline.
/// </summary>
public static class Icons
{
    private const string G = "#6A6A6A";      // grey outline
    private const string B = "#2F7CC4";      // blue outline
    private const string SB = "#3FA9F5";     // solid blue
    private const string LB = "#9AD3FA";     // light blue
    private const string DB = "#1F78C0";     // dark blue
    private const string GR = "#2DB24A";     // green
    private const string K = "#333333";      // ink

    private static readonly Dictionary<string, (string d, string? fill, string? stroke)[]> Defs = new()
    {
        ["sketch"] = new (string, string?, string?)[] { ("M3,4 H15 V16 H3 Z", "#FFFFFF", G), ("M6,13.5 L12.5,7", null, G), ("M5,6 H7 M11,6 H13 M5,14 V12", null, G), ("M16,13 H18.4 V16.6 H22 V19 H18.4 V22.6 H16 V19 H12.4 V16.6 H16 Z", GR, null) },
        ["extrude"] = new (string, string?, string?)[] { ("M4,8.5 L11,5.5 L18,7.5 L11,10.5 Z", LB, B), ("M4,8.5 L11,10.5 V21 L4,19 Z", SB, B), ("M11,10.5 L18,7.5 V18 L11,21 Z", DB, B), ("M21,14 V3 M19.4,4.8 L21,3 L22.6,4.8", null, K) },
        ["revolve"] = new (string, string?, string?)[] { ("M2.5,14 C2.5,10 21.5,10 21.5,14 V18 C21.5,14.5 2.5,14.5 2.5,18 Z", SB, B), ("M5,9 C8,4.5 16,4.5 19,8 M17,8.6 L19,8 L19.2,6", null, K) },
        ["box"] = new (string, string?, string?)[] { ("M4,8 L12,4 L20,8 L12,12 Z", "#F2F2F2", G), ("M4,8 L12,12 V21 L4,17 Z", "#D6D6D6", G), ("M20,8 L12,12 V21 L20,17 Z", "#BDBDBD", G) },
        ["cylinder"] = new (string, string?, string?)[] { ("M5,7 V17 C5,20.5 19,20.5 19,17 V7 Z", "#D3D3D3", G), ("M5,7 C5,3.5 19,3.5 19,7 C19,10.5 5,10.5 5,7 Z", "#F2F2F2", G) },
        ["sphere"] = new (string, string?, string?)[] { ("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z", "#D9D9D9", G), ("M7.5,9 A5.5,5.5 0 0 1 12,6", null, "#FFFFFF") },
        ["torus"] = new (string, string?, string?)[] { ("M2.5,12 C2.5,5.5 21.5,5.5 21.5,12 C21.5,18.5 2.5,18.5 2.5,12 Z", "#D3D3D3", G), ("M8,12 C8,10.3 16,10.3 16,12 C16,13.7 8,13.7 8,12 Z", "#FFFFFF", G) },
        ["rectpattern"] = new (string, string?, string?)[] { ("M3,3 H8 V8 H3 Z M16,3 H21 V8 H16 Z M3,16 H8 V21 H3 Z M16,16 H21 V21 H16 Z", SB, B), ("M8,5.5 H16 M5.5,8 V16 M8,18.5 H16 M18.5,8 V16", null, G) },
        ["circpattern"] = new (string, string?, string?)[] { ("M10.3,2.5 H13.7 V5.9 H10.3 Z M10.3,18.1 H13.7 V21.5 H10.3 Z M2.5,10.3 H5.9 V13.7 H2.5 Z M18.1,10.3 H21.5 V13.7 H18.1 Z M4.8,4.8 H8 V8 H4.8 Z M16,4.8 H19.2 V8 H16 Z M4.8,16 H8 V19.2 H4.8 Z M16,16 H19.2 V19.2 H16 Z", SB, B), ("M12,9.5 A2.5,2.5 0 1 0 12,14.5 A2.5,2.5 0 1 0 12,9.5 Z", null, G) },
        ["mirror"] = new (string, string?, string?)[] { ("M2.5,6 L10,9 V19.5 L2.5,16.5 Z", SB, B), ("M21.5,6 L14,9 V19.5 L21.5,16.5 Z", "#D6D6D6", G), ("M12,2.5 V22", null, "#E0662B") },
        ["move"] = new (string, string?, string?)[] { ("M12,1.8 L15.4,5.6 H13.1 V10.9 H18.4 V8.6 L22.2,12 L18.4,15.4 V13.1 H13.1 V18.4 H15.4 L12,22.2 L8.6,18.4 H10.9 V13.1 H5.6 V15.4 L1.8,12 L5.6,8.6 V10.9 H10.9 V5.6 H8.6 Z", "#2B2B2B", null) },
        ["combine"] = new (string, string?, string?)[] { ("M3,9 H14 V20 H3 Z", SB, B), ("M9,4 H20 V15 H9 Z", LB, B) },
        ["scale"] = new (string, string?, string?)[] { ("M3,12 H12 V21 H3 Z", "#D6D6D6", G), ("M8,3 H21 V16 H8 Z", null, B), ("M10,14 L18,6 M14,6 H18 V10", null, K) },
        ["delete"] = new (string, string?, string?)[] { ("M5.5,3.5 L12,10 L18.5,3.5 L20.5,5.5 L14,12 L20.5,18.5 L18.5,20.5 L12,14 L5.5,20.5 L3.5,18.5 L10,12 L3.5,5.5 Z", "#D9342B", null) },
        ["appearance"] = new (string, string?, string?)[] { ("M12,3 A9,9 0 0 0 12,21 Z", "#E8553E", null), ("M12,3 A9,9 0 0 1 12,21 Z", SB, null), ("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z", null, G) },
        ["measure"] = new (string, string?, string?)[] { ("M2,13 H22 V19 H2 Z", "#F5B820", "#9A7400"), ("M5,13 V16 M8,13 V15 M11,13 V16 M14,13 V15 M17,13 V16 M20,13 V15", null, "#7A5C00"), ("M3,7.5 H21 M5,5.5 L3,7.5 L5,9.5 M19,5.5 L21,7.5 L19,9.5", null, K) },
        ["section"] = new (string, string?, string?)[] { ("M3,4 H21 V20 H3 Z", "#FFFFFF", G), ("M3,4 H12 V20 H3 Z", LB, B), ("M12,2 V22", null, DB) },
        ["insertmesh"] = new (string, string?, string?)[] { ("M4,9 L12,4 L20,9 V16 L12,21 L4,16 Z", "#D6B656", "#8A6D1E"), ("M4,9 L12,13 L20,9 M12,13 V21 M8,11 V18.5 M16,11 V18.5 M4,12.5 L12,17 L20,12.5", null, "#8A6D1E"), ("M2,5 H8 M6,3 L8,5 L6,7", null, SB) },
        ["select"] = new (string, string?, string?)[] { ("M3,3 H17 V15 H3 Z", "#DDF1FB", GR), ("M13.5,10.5 V22 L16,19.6 L18,23 L19.7,22.2 L17.8,18.9 H21.2 Z", "#FFFFFF", "#222222") },
        ["newcomponent"] = new (string, string?, string?)[] { ("M3,9 H12 V20 H3 Z", "#D6D6D6", G), ("M8,3 H12 V9 H8 Z", SB, B), ("M17,3 V11 M13,7 H21", null, GR) },
        ["joint"] = new (string, string?, string?)[] { ("M3,4 H13 V14 H3 Z", "#E6E6E6", G), ("M14,14 C16,12 19,12 20.5,14 C22,16 22,19 20,20.5 C18,22 15,22 13.5,20 Z", null, K), ("M13,4 L16,1 H22 V7 L19,10", null, SB) },
        ["offsetplane"] = new (string, string?, string?)[] { ("M3,8 L9,4 V16 L3,20 Z", "#E8E8E8", G), ("M9,8 L15,4 V16 L9,20 Z", "#30B656", "#1D7F33"), ("M15,8 L21,4 V16 L15,20 Z", "#F29A5B", "#B3622A") },
        ["parameters"] = new (string, string?, string?)[] { ("M3,4 H21 V20 H3 Z", "#FFFFFF", G), ("M3,8 H21 M3,12 H21 M3,16 H21 M9,4 V20", null, G), ("M15,16 H22 M18.5,12.5 V19.5", null, B) },
        ["finish"] = new (string, string?, string?)[] { ("M2.5,12.5 L5.2,9.8 L9.2,13.8 L18.8,4.2 L21.5,6.9 L9.2,19.2 Z", GR, null) },
        ["line"] = new (string, string?, string?)[] { ("M4.5,19.5 L19.5,4.5", null, K), ("M2.5,17.5 H6.5 V21.5 H2.5 Z M17.5,2.5 H21.5 V6.5 H17.5 Z", "#FFFFFF", B) },
        ["rect"] = new (string, string?, string?)[] { ("M4,6 H20 V18 H4 Z", null, K), ("M2.5,4.5 H5.5 V7.5 H2.5 Z M18.5,16.5 H21.5 V19.5 H18.5 Z", "#FFFFFF", B) },
        ["crect"] = new (string, string?, string?)[] { ("M4,6 H20 V18 H4 Z", null, K), ("M10.5,10.5 H13.5 V13.5 H10.5 Z M18.5,16.5 H21.5 V19.5 H18.5 Z", "#FFFFFF", B) },
        ["circle"] = new (string, string?, string?)[] { ("M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4 Z", null, K), ("M10.5,10.5 H13.5 V13.5 H10.5 Z", "#FFFFFF", B) },
        ["polygon"] = new (string, string?, string?)[] { ("M12,3 L19.8,7.5 V16.5 L12,21 L4.2,16.5 V7.5 Z", null, K), ("M10.5,10.5 H13.5 V13.5 H10.5 Z", "#FFFFFF", B) },
        ["arc"] = new (string, string?, string?)[] { ("M4,18 A9,9 0 0 1 20,18", null, K), ("M2.5,16.5 H5.5 V19.5 H2.5 Z M18.5,16.5 H21.5 V19.5 H18.5 Z M10.5,7.5 H13.5 V10.5 H10.5 Z", "#FFFFFF", B) },
        ["trim"] = new (string, string?, string?)[] { ("M4,19 L20,5", null, K), ("M4,5 L20,19", null, "#D9342B") },
        ["dimension"] = new (string, string?, string?)[] { ("M4,7 V17 M20,7 V17 M4,12 H20 M7,10 L4,12 L7,14 M17,10 L20,12 L17,14", null, K) },
        ["constraint"] = new (string, string?, string?)[] { ("M5,19 L19,5", null, K), ("M14,14 H20 V20 H14 Z", null, B) },
        ["export"] = new (string, string?, string?)[] { ("M4,8 H14 V21 H4 Z", "#FFFFFF", G), ("M11,12 H21 M18,9 L21,12 L18,15", null, SB) },
        ["design"] = new (string, string?, string?)[] { ("M4,8 L12,4 L20,8 V16 L12,20 L4,16 Z", "#E8A43B", "#9C6512"), ("M4,8 L12,12 L20,8 M12,12 V20", null, "#9C6512") },
    };

    /// <summary>Monochrome line icons for the application bar, browser and navigation bar.</summary>
    private static readonly Dictionary<string, string> Lines = new()
    {
        ["home"] = "M3,11 L12,3 L21,11 M5.5,9 V21 H10 V15 H14 V21 H18.5 V9",
        ["datapanel"] = "M4,4 H8 V8 H4 Z M10,4 H14 V8 H10 Z M16,4 H20 V8 H16 Z M4,10 H8 V14 H4 Z M10,10 H14 V14 H10 Z M16,10 H20 V14 H16 Z M4,16 H8 V20 H4 Z M10,16 H14 V20 H10 Z M16,16 H20 V20 H16 Z",
        ["file"] = "M5,3 H14 L19,8 V21 H5 Z M14,3 V8 H19",
        ["save"] = "M4,4 H17 L20,7 V20 H4 Z M8,4 V9 H15 V4 M7,20 V13 H17 V20",
        ["undo"] = "M9,5 L4,10 L9,15 M4,10 H15 A5,5 0 0 1 15,20 H11",
        ["redo"] = "M15,5 L20,10 L15,15 M20,10 H9 A5,5 0 0 0 9,20 H13",
        ["plus"] = "M12,4 V20 M4,12 H20",
        ["close"] = "M6,6 L18,18 M18,6 L6,18",
        ["help"] = "M12,2.5 A9.5,9.5 0 1 0 12,21.5 A9.5,9.5 0 1 0 12,2.5 Z M9.5,9.5 C9.5,6 14.5,6 14.5,9.5 C14.5,11.5 12,11.5 12,14 M12,17 V17.3",
        ["bell"] = "M6,17 V11 A6,6 0 0 1 18,11 V17 L20,19 H4 Z M10,21 H14",
        ["clock"] = "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,7 V12 L15.5,14",
        ["extension"] = "M4,9 H9 V4 H15 V9 H20 V15 H15 V20 H9 V15 H4 Z",
        ["eye"] = "M2,12 C5,6.5 19,6.5 22,12 C19,17.5 5,17.5 2,12 Z M12,9 A3,3 0 1 0 12,15 A3,3 0 1 0 12,9 Z",
        ["eyeoff"] = "M2,12 C5,6.5 19,6.5 22,12 C19,17.5 5,17.5 2,12 Z M12,9 A3,3 0 1 0 12,15 A3,3 0 1 0 12,9 Z M4,20 L20,4",
        ["gear"] = "M12,8.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 12,8.5 Z M12,2 V5 M12,19 V22 M2,12 H5 M19,12 H22 M4.9,4.9 L7,7 M17,17 L19.1,19.1 M4.9,19.1 L7,17 M17,7 L19.1,4.9",
        ["folder"] = "M3,6 H9.5 L11.5,8 H21 V19 H3 Z",
        ["body"] = "M4,8 L12,4 L20,8 V16 L12,20 L4,16 Z M4,8 L12,12 L20,8 M12,12 V20",
        ["component"] = "M3,4 H11 V12 H3 Z M3,12 H11 V20 H3 Z M11,12 H19 V20 H11 Z",
        ["sketchnode"] = "M3,5 H17 V19 H3 Z M7,15 L15,7 M18,16 L21,13",
        ["plane"] = "M3,17 L8,7 H21 L16,17 Z",
        ["axis"] = "M3,21 L21,3 M3,21 H5 M3,21 V19",
        ["chevdown"] = "M7,10 L12,15 L17,10",
        ["chevright"] = "M10,7 L15,12 L10,17",
        ["collapse"] = "M14,6 L8,12 L14,18 M20,6 L14,12 L20,18",
        ["minus"] = "M5,12 H19",
        ["orbit"] = "M12,4 A8,8 0 1 0 20,12 M17,3 L20,6 L16.5,8 M9,12 H15 M12,9 V15",
        ["lookat"] = "M3,6 H21 V18 H3 Z M8,21 H16 M12,10 V14 M10,12 H14",
        ["pan"] = "M7,12 V7 C7,5.5 9,5.5 9,7 V11 M9,10 V5 C9,3.5 11,3.5 11,5 V10 M11,10 V5.5 C11,4 13,4 13,5.5 V10 M13,10 V7 C13,5.5 15,5.5 15,7 V14 C15,18 13,21 9.5,21 C7,21 5.5,19 4.5,17 L3,13.5 C2.5,12 4.5,11.5 5.5,12.5 L7,14",
        ["zoom"] = "M10,4 A6,6 0 1 0 10,16 A6,6 0 1 0 10,4 Z M14.5,14.5 L20,20 M17,4 V9 M14.5,6.5 H19.5",
        ["fit"] = "M10,4 A6,6 0 1 0 10,16 A6,6 0 1 0 10,4 Z M14.5,14.5 L20,20 M7.5,8 H12.5 V12 H7.5 Z",
        ["display"] = "M3,5 H21 V16 H3 Z M9,20 H15 M12,16 V20",
        ["grid"] = "M4,4 H20 V20 H4 Z M9.3,4 V20 M14.6,4 V20 M4,9.3 H20 M4,14.6 H20",
        ["viewports"] = "M3,4 H21 V20 H3 Z M12,4 V20 M3,12 H21",
        ["first"] = "M6,5 V19 M18,5 L9,12 L18,19 Z",
        ["prev"] = "M17,5 L8,12 L17,19 Z M6,5 V19",
        ["play"] = "M7,4 L19,12 L7,20 Z",
        ["next"] = "M7,5 L16,12 L7,19 Z M18,5 V19",
        ["last"] = "M18,5 V19 M6,5 L15,12 L6,19 Z",
        ["info"] = "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,10.5 V16.5 M12,7.5 V7.7",
        ["flip"] = "M7,4 V20 M4,7 L7,4 L10,7 M17,20 V4 M14,17 L17,20 L20,17",
        ["user"] = "M12,4 A4,4 0 1 0 12,12 A4,4 0 1 0 12,4 Z M4,21 C4,15 20,15 20,21",
    };

    public static Control Color(string key, double size = 32)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        if (Defs.TryGetValue(key, out var layers))
            foreach (var (d, fill, stroke) in layers)
                canvas.Children.Add(new Path
                {
                    Data = Geometry.Parse(d),
                    Fill = fill == null ? null : Ui.Brush(fill),
                    Stroke = stroke == null ? null : Ui.Brush(stroke),
                    StrokeThickness = 1.1,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                });
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    public static Control Line(string key, double size = 18, IBrush? color = null)
    {
        var path = new Path
        {
            Data = Geometry.Parse(Lines.GetValueOrDefault(key, "M4,4 H20 V20 H4 Z")),
            Stroke = color ?? Ui.Brush("#5A5A5A"),
            StrokeThickness = 1.5 * 24 / size,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        if (key is "play" or "first" or "prev" or "next" or "last") path.Fill = color ?? Ui.Brush("#5A5A5A");
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }
}
