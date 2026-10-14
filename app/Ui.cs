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
        ["fillet"] = new (string, string?, string?)[] { ("M4,20 V10 C4,6.5 6.5,4 10,4 H20 V20 Z", "#D6D6D6", G), ("M4,10 C4,6.5 6.5,4 10,4", null, SB), ("M9.5,9.5 L13,13", null, K) },
        ["chamfer"] = new (string, string?, string?)[] { ("M4,20 V10 L10,4 H20 V20 Z", "#D6D6D6", G), ("M4,10 L10,4", null, SB) },
        ["shell"] = new (string, string?, string?)[] { ("M3,8 L12,4 L21,8 V17 L12,21 L3,17 Z", "#D6D6D6", G), ("M6.5,9 L12,6.6 L17.5,9 V15.5 L12,18 L6.5,15.5 Z", "#FFFFFF", G), ("M6.5,9 L12,11.4 L17.5,9 M12,11.4 V18", null, G) },
        ["presspull"] = new (string, string?, string?)[] { ("M4,9 L11,6 L18,8 V18 L11,21 L4,19 Z", "#D6D6D6", G), ("M4,9 L11,11 L18,8 M11,11 V21", null, G), ("M20,15 V3 M18.4,4.8 L20,3 L21.6,4.8", null, K) },
        ["insertcad"] = new (string, string?, string?)[] { ("M4,4 H14 L19,9 V21 H4 Z", "#FFFFFF", G), ("M14,4 V9 H19", null, G), ("M7,13 L11.5,11 L16,13 L11.5,15 Z", SB, B), ("M7,13 V17 L11.5,19 V15 M16,13 V17 L11.5,19", null, B) },
        ["loft"] = new (string, string?, string?)[] { ("M4,18 C4,16 10,15 12,15 C14,15 20,16 20,18 C20,20 14,21 12,21 C10,21 4,20 4,18 Z", LB, B), ("M7,6 H17 L20,18 H4 Z", SB, B), ("M7,6 C7,4.5 17,4.5 17,6", null, B) },
        ["sweep"] = new (string, string?, string?)[] { ("M3,20 C5,10 12,6 21,5", null, "#E0662B"), ("M3,15 C6,7 13,3 21,2 V8 C14,9 9,12 7,20 H3 Z", SB, B) },
        ["surfextrude"] = new (string, string?, string?)[] { ("M4,20 L4,9 L12,5 L20,9 L20,20 L12,16 Z", "#F4E3B5", "#B98B2A"), ("M4,9 L12,13 L20,9 M12,13 V16", null, "#B98B2A"), ("M12,4 V1 M10.5,2.5 L12,1 L13.5,2.5", null, K) },
        ["patch"] = new (string, string?, string?)[] { ("M3,12 C3,5 21,5 21,12 C21,19 3,19 3,12 Z", "#F4E3B5", "#B98B2A"), ("M6,12 C9,9 15,9 18,12", null, "#B98B2A") },
        ["thicken"] = new (string, string?, string?)[] { ("M3,10 C8,6 16,6 21,10 V15 C16,11 8,11 3,15 Z", SB, B), ("M12,3 V8 M10.5,6.5 L12,8 L13.5,6.5 M12,21 V16 M10.5,17.5 L12,16 L13.5,17.5", null, K) },
        ["flange"] = new (string, string?, string?)[] { ("M3,12 L12,8 L21,12 L12,16 Z", "#C9CED4", G), ("M3,12 V14 L12,18 L21,14 V12", "#AEB4BA", G) },
        ["edgeflange"] = new (string, string?, string?)[] { ("M3,14 L12,18 L19,15 L10,11 Z", "#C9CED4", G), ("M19,15 V5 L10,1 V11", "#AEB4BA", G), ("M19,15 V5", null, SB) },
        ["sheetrule"] = new (string, string?, string?)[] { ("M3,6 H21 V18 H3 Z", "#FFFFFF", G), ("M6,10 H18 M6,14 H14", null, G), ("M15,17 L19,13", null, SB) },
        ["flatpattern"] = new (string, string?, string?)[] { ("M2,15 L8,9 H22 L16,15 Z", "#C9CED4", G), ("M2,15 V17 H16 L22,11 V9", null, G), ("M9,3 V8 M7,6 L9,8 L11,6", null, SB) },
        ["isaac_cad"] = new (string, string?, string?)[] { ("M4,8 L12,4 L20,8 V16 L12,20 L4,16 Z", "#76B900", "#4A7400"), ("M4,8 L12,12 L20,8 M12,12 V20", null, "#4A7400"), ("M15,3 H22 M19,0.5 L22,3 L19,5.5", null, K) },
        ["isaac_robot"] = new (string, string?, string?)[] { ("M7,8 H17 V16 H7 Z", "#76B900", "#4A7400"), ("M10,11 V11.5 M14,11 V11.5 M12,8 V4 M12,3.5 V3.6 M7,12 H4 V18 M17,12 H20 V18 M9,16 V21 M15,16 V21", null, K) },
        ["isaac_auto"] = new (string, string?, string?)[] { ("M12,4 A8,8 0 1 0 20,12 M17,3 L20,6 L16.5,8", null, "#4A7400"), ("M9,9 H15 V15 H9 Z", "#76B900", "#4A7400") },
        ["isaac_status"] = new (string, string?, string?)[] { ("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z", "#E8F5D0", "#4A7400"), ("M7.5,12 L10.5,15 L16.5,9", null, "#4A7400") },
        ["rib"] = new (string, string?, string?)[] { ("M3,18 H21 V21 H3 Z", "#D6D6D6", G), ("M10,18 V6 H14 V18", SB, B) },
        ["boss"] = new (string, string?, string?)[] { ("M3,17 H21 V21 H3 Z", "#D6D6D6", G), ("M7,17 V8 C7,6 17,6 17,8 V17 Z", SB, B), ("M10,7.5 C10,6.8 14,6.8 14,7.5 C14,8.2 10,8.2 10,7.5 Z", "#FFFFFF", B) },
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
        ["c_hv"] = new (string, string?, string?)[] { ("M3,8 H15 M18,4 V20", null, K), ("M3,15 H11", null, B) },
        ["c_coincident"] = new (string, string?, string?)[] { ("M3,20 L11,12 M13,12 L21,4", null, K), ("M9.5,9.5 H14.5 V14.5 H9.5 Z", "#FFFFFF", B) },
        ["c_parallel"] = new (string, string?, string?)[] { ("M3,16 L13,4 M11,20 L21,8", null, K) },
        ["c_perpendicular"] = new (string, string?, string?)[] { ("M5,4 V19 H20", null, K), ("M5,14 H10 V19", null, B) },
        ["c_equal"] = new (string, string?, string?)[] { ("M4,9 H20 M4,15 H20", null, K) },
        ["c_fix"] = new (string, string?, string?)[] { ("M7,11 V7.5 A5,5 0 0 1 17,7.5 V11", null, K), ("M5,11 H19 V21 H5 Z", "#F2C230", "#9A7400") },
        ["c_midpoint"] = new (string, string?, string?)[] { ("M3,18 H21", null, K), ("M12,10 L16,16 H8 Z", "#FFFFFF", B) },
        ["c_concentric"] = new (string, string?, string?)[] { ("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,7.5 A4.5,4.5 0 1 0 12,16.5 A4.5,4.5 0 1 0 12,7.5 Z", null, K), ("M11,11 H13 V13 H11 Z", B, B) },
        ["c_tangent"] = new (string, string?, string?)[] { ("M12,4 A7,7 0 1 0 12,18 A7,7 0 1 0 12,4 Z", null, K), ("M2,18 H22", null, B) },
        ["c_symmetric"] = new (string, string?, string?)[] { ("M12,2 V22", null, "#E0662B"), ("M3,8 L9,12 L3,16 Z M21,8 L15,12 L21,16 Z", "#FFFFFF", K) },
        ["trim"] = new (string, string?, string?)[] { ("M4,19 L20,5", null, K), ("M4,5 L20,19", null, "#D9342B") },
        ["dimension"] = new (string, string?, string?)[] { ("M4,7 V17 M20,7 V17 M4,12 H20 M7,10 L4,12 L7,14 M17,10 L20,12 L17,14", null, K) },
        ["constraint"] = new (string, string?, string?)[] { ("M5,19 L19,5", null, K), ("M14,14 H20 V20 H14 Z", null, B) },
        ["export"] = new (string, string?, string?)[] { ("M4,8 H14 V21 H4 Z", "#FFFFFF", G), ("M11,12 H21 M18,9 L21,12 L18,15", null, SB) },
        ["form"] = new (string, string?, string?)[] { ("M3,9 C7,3 17,3 21,9 C21,15 17,21 12,21 C7,21 3,15 3,9 Z", "#D9C2F0", "#7A3FB0"), ("M3,9 C8,12 16,12 21,9 M12,4.5 V21 M6,5.5 C9,9 9,16 7,19.5 M18,5.5 C15,9 15,16 17,19.5", null, "#7A3FB0") },
        ["automodel"] = new (string, string?, string?)[] { ("M3,14 L11,10 L19,14 L11,18 Z", LB, B), ("M3,14 V17 L11,21 L19,17 V14", SB, B), ("M18,2 L19.2,5.3 L22.5,6.5 L19.2,7.7 L18,11 L16.8,7.7 L13.5,6.5 L16.8,5.3 Z", "#F5B820", "#9A7400") },
        ["hole"] = new (string, string?, string?)[] { ("M3,9 L12,5 L21,9 V16 L12,20 L3,16 Z", "#D6D6D6", G), ("M3,9 L12,13 L21,9", null, G), ("M9,9 C9,7.6 15,7.6 15,9 C15,10.4 9,10.4 9,9 Z", "#FFFFFF", G), ("M12,1.5 V7 M10.5,5.5 L12,7 L13.5,5.5", null, SB) },
        ["coil"] = new (string, string?, string?)[] { ("M6,4 C19,4 19,8 6,8 C19,8 19,12 6,12 C19,12 19,16 6,16 C19,16 19,20 6,20", null, B), ("M6,4 C3,4 3,8 6,8 M6,12 C3,12 3,16 6,16", null, LB) },
        ["pipe"] = new (string, string?, string?)[] { ("M6,7 H19 C21.5,7 21.5,17 19,17 H6 Z", "#D3D3D3", G), ("M6,7 C3.5,7 3.5,17 6,17 C8.5,17 8.5,7 6,7 Z", "#F2F2F2", G), ("M6,10 C4.8,10 4.8,14 6,14 C7.2,14 7.2,10 6,10 Z", "#FFFFFF", G) },
        ["thread"] = new (string, string?, string?)[] { ("M7,4 H17 V20 H7 Z", "#D3D3D3", G), ("M7,7.5 L17,5.5 M7,11 L17,9 M7,14.5 L17,12.5 M7,18 L17,16", null, K) },
        ["draft"] = new (string, string?, string?)[] { ("M5,20 L8.5,4 H19 V20 Z", "#D6D6D6", G), ("M5,20 L8.5,4", null, SB), ("M8.5,4 V20", null, "#9A9A9A"), ("M8.5,10 C7.6,10 6.9,9.8 6.2,9.4", null, K) },
        ["web"] = new (string, string?, string?)[] { ("M3,17 H21 V21 H3 Z", "#D6D6D6", G), ("M3,17 L17,5 H20 L7,17 Z", SB, B) },
        ["splitbody"] = new (string, string?, string?)[] { ("M3,8 H11 V20 H3 Z", SB, B), ("M13,4 H21 V16 H13 Z", "#D6D6D6", G), ("M12,2 V22", null, "#E0662B") },
        ["stitch"] = new (string, string?, string?)[] { ("M2.5,6 H11 V18 H2.5 Z M13,6 H21.5 V18 H13 Z", "#F4E3B5", "#B98B2A"), ("M10,8 L14,10 L10,12 L14,14 L10,16", null, K) },
        ["unstitch"] = new (string, string?, string?)[] { ("M2.5,6 H9.5 V18 H2.5 Z M14.5,6 H21.5 V18 H14.5 Z", "#F4E3B5", "#B98B2A"), ("M10.5,9.5 L13.5,14.5 M13.5,9.5 L10.5,14.5", null, "#D9342B") },
        ["surfrevolve"] = new (string, string?, string?)[] { ("M2.5,14 C2.5,10 21.5,10 21.5,14 V18 C21.5,14.5 2.5,14.5 2.5,18 Z", "#F4E3B5", "#B98B2A"), ("M5,9 C8,4.5 16,4.5 19,8 M17,8.6 L19,8 L19.2,6", null, K) },
        ["surfsweep"] = new (string, string?, string?)[] { ("M3,20 C5,10 12,6 21,5", null, "#E0662B"), ("M3,15 C6,7 13,3 21,2 V8 C14,9 9,12 7,20 H3 Z", "#F4E3B5", "#B98B2A") },
        ["surfloft"] = new (string, string?, string?)[] { ("M7,6 H17 L20,18 H4 Z", "#F4E3B5", "#B98B2A"), ("M4,18 C4,20.5 20,20.5 20,18 M7,6 C7,4.5 17,4.5 17,6", null, "#B98B2A") },
        ["surftrim"] = new (string, string?, string?)[] { ("M3,12 C3,5 21,5 21,12 C21,19 3,19 3,12 Z", "#F4E3B5", "#B98B2A"), ("M14,3 L10,21", null, "#D9342B") },
        ["extend"] = new (string, string?, string?)[] { ("M2.5,6 H12 V18 H2.5 Z", "#F4E3B5", "#B98B2A"), ("M12,6 H15 M12,18 H15", null, "#B98B2A"), ("M14,12 H21.5 M19,9.5 L21.5,12 L19,14.5", null, K) },
        ["lip"] = new (string, string?, string?)[] { ("M3,9 H12 V12 H6 V21 H3 Z", SB, B), ("M21,15 H12 V12 H18 V3 H21 Z", "#D6D6D6", G) },
        ["snapfit"] = new (string, string?, string?)[] { ("M3,18 H21 V21 H3 Z", "#D6D6D6", G), ("M8,18 V5 H11 L15,9 L11,10 V18 Z", SB, B) },
        ["rest"] = new (string, string?, string?)[] { ("M3,13 L12,9 L21,13 L12,17 Z", "#D6D6D6", G), ("M3,13 V16 L12,20 L21,16 V13", null, G), ("M8,11.5 L12,9.7 L16,11.5 L12,13.3 Z", SB, B), ("M12,3 V8 M10.5,6.5 L12,8 L13.5,6.5", null, K) },
        ["plasticrule"] = new (string, string?, string?)[] { ("M3,6 H21 V18 H3 Z", "#FFFFFF", G), ("M6,10 H18 M6,14 H14", null, G), ("M15,17 L19,13", null, GR) },
        ["centerofmass"] = new (string, string?, string?)[] { ("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z", "#FFFFFF", K), ("M12,3 A9,9 0 0 1 21,12 H12 Z M12,21 A9,9 0 0 1 3,12 H12 Z", K, null) },
        ["interference"] = new (string, string?, string?)[] { ("M3,9 H14 V20 H3 Z", SB, B), ("M10,4 H21 V15 H10 Z", "#D6D6D6", G), ("M10,9 H14 V15 H10 Z", "#D9342B", "#9A1F18") },
        ["configmode"] = new (string, string?, string?)[] { ("M4,8 L12,4 L20,8 V16 L12,20 L4,16 Z", "#D6D6D6", G), ("M4,8 L12,12 L20,8 M12,12 V20", null, G), ("M17,14 H23 V20 H17 Z", "#FFFFFF", B), ("M17,16 H23 M19.5,14 V20", null, B) },
        ["configtable"] = new (string, string?, string?)[] { ("M3,4 H21 V20 H3 Z", "#FFFFFF", G), ("M3,8 H21 M3,12 H21 M3,16 H21 M9,4 V20 M15,4 V20", null, G), ("M3,4 H21 V8 H3 Z", LB, B) },
        ["configrules"] = new (string, string?, string?)[] { ("M3,4 H21 V20 H3 Z", "#FFFFFF", G), ("M3,8 H21 M3,12 H21 M3,16 H21 M9,4 V20", null, G), ("M13,15 L16,18 L22,11", null, GR) },
        ["canvas"] = new (string, string?, string?)[] { ("M3,4 H21 V20 H3 Z", "#FFFFFF", G), ("M5,17 L10,11 L13,14.5 L15.5,12 L19,17 Z", SB, B), ("M15.5,6.5 A1.5,1.5 0 1 0 15.5,9.5 A1.5,1.5 0 1 0 15.5,6.5 Z", "#F5B820", null) },
        ["decal"] = new (string, string?, string?)[] { ("M3,4 H21 V14 L15,20 H3 Z", "#FFFFFF", G), ("M21,14 H15 V20", "#E6E6E6", G), ("M5,15 L9,10 L12,13 L14,11 L16,13", null, B) },
        ["derive"] = new (string, string?, string?)[] { ("M3,3 H11 L14,6 V18 H3 Z", "#FFFFFF", G), ("M6,9 L9,7.5 L12,9 L9,10.5 Z", SB, B), ("M12,15 H21 M18,12 L21,15 L18,18", null, SB) },
        ["insertcomponent"] = new (string, string?, string?)[] { ("M9,9 H20 V20 H9 Z", "#D6D6D6", G), ("M13,5 H17 V9 H13 Z", SB, B), ("M2,5 H8 M6,3 L8,5 L6,7", null, SB) },
        ["meshcreate"] = new (string, string?, string?)[] { ("M3,19 L8,6 L14,12 L20,4 L21,19 Z", "#D6B656", "#8A6D1E"), ("M8,6 L9,19 L14,12 L15,19 M14,12 L21,19 M3,19 L14,12 M20,4 L15,19", null, "#8A6D1E") },
        ["mesh_convert"] = new (string, string?, string?)[] { ("M2,16 L6,7 L10,16 Z", "#D6B656", "#8A6D1E"), ("M6,7 V16", null, "#8A6D1E"), ("M10.5,11.5 H14 M12.5,10 L14,11.5 L12.5,13", null, K), ("M15,9 L18.5,7 L22,9 V15 L18.5,17 L15,15 Z", SB, B) },
        ["offset"] = new (string, string?, string?)[] { ("M3,20 C3,10 10,4 20,4", null, K), ("M8,20 C8,13 13,9 20,9", null, B) },
        ["sketchfillet"] = new (string, string?, string?)[] { ("M4,20 V12 C4,7.5 7.5,4 12,4 H20", null, K), ("M4,12 C4,7.5 7.5,4 12,4", null, SB), ("M2.5,10.5 H5.5 V13.5 H2.5 Z M10.5,2.5 H13.5 V5.5 H10.5 Z", "#FFFFFF", B) },
        ["sketchmirror"] = new (string, string?, string?)[] { ("M12,2 V22", null, "#E0662B"), ("M3,6 L9,12 L3,18 Z", null, K), ("M21,6 L15,12 L21,18 Z", null, B) },
        ["spline"] = new (string, string?, string?)[] { ("M4,18 C7,4 12,4 13,12 C14,20 19,19 21,6", null, K), ("M2.5,16.5 H5.5 V19.5 H2.5 Z M11.5,10.5 H14.5 V13.5 H11.5 Z M19.5,4.5 H22.5 V7.5 H19.5 Z", "#FFFFFF", B) },
        ["c_collinear"] = new (string, string?, string?)[] { ("M3,19 L10,12 M14,8 L21,1", null, K), ("M10,12 L14,8", null, B) },
        ["c_smooth"] = new (string, string?, string?)[] { ("M3,19 C3,11 8,8 12,12", null, K), ("M12,12 C16,16 21,13 21,5", null, B), ("M10.5,10.5 H13.5 V13.5 H10.5 Z", "#FFFFFF", B) },
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
        ["jointnode"] = "M12,6 A6,6 0 1 0 12,18 A6,6 0 1 0 12,6 Z M12,10 A2,2 0 1 0 12,14 A2,2 0 1 0 12,10 Z M12,2 V6 M12,18 V22",
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
        if (!Defs.TryGetValue(key, out var layers)) return Line(key, size);
        var canvas = new Canvas { Width = 24, Height = 24 };
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
