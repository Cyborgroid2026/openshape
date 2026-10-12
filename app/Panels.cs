using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenShape;

/// <summary>Floating command dialog (title, label/value rows, OK / Cancel).</summary>
public sealed class CommandPanel : Border
{
    private readonly StackPanel _rows = new() { Margin = new Thickness(12, 8, 12, 10), Spacing = 6 };
    private readonly TextBlock _error = new() { Foreground = Ui.Brush("#C62828"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    public FlatButton? OkButton { get; }

    public CommandPanel(string title, string? icon, Action? ok, Action? cancel, string okText = "확인")
    {
        Width = 330;
        Background = Ui.White;
        BorderBrush = Ui.Brush("#A9A9A9");
        BorderThickness = new Thickness(1);
        BoxShadow = BoxShadows.Parse("0 3 12 0 #40000000");

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 34, Background = Ui.Bar };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 0) };
        if (icon != null) titleRow.Children.Add(Icons.Color(icon, 18));
        titleRow.Children.Add(Ui.Label(title, 13, Ui.Text, FontWeight.Bold));
        head.Children.Add(titleRow);
        var collapse = Icons.Line("collapse", 10, Ui.Muted);
        collapse.Margin = new Thickness(10, 0);
        collapse.RenderTransform = new RotateTransform(180);
        Grid.SetColumn(collapse, 2);
        head.Children.Add(collapse);

        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(10, 0, 10, 10) };
        foot.Children.Add(Icons.Line("info", 16, Ui.Muted));
        if (ok != null)
        {
            OkButton = new FlatButton
            {
                Width = 74,
                Height = 28,
                Normal = Ui.Accent,
                HoverBrush = Ui.Brush("#0582BC"),
                Margin = new Thickness(0, 0, 8, 0),
                Child = Ui.Centered(Ui.Label(okText, 12, Brushes.White)),
            }.Init();
            OkButton.Click += ok;
            Grid.SetColumn(OkButton, 2);
            foot.Children.Add(OkButton);
        }
        var cancelBtn = new FlatButton
        {
            Width = 74,
            Height = 28,
            Normal = Ui.Brush("#E8E8E8"),
            HoverBrush = Ui.Brush("#D6D6D6"),
            BorderBrush = Ui.Brush("#BDBDBD"),
            BorderThickness = new Thickness(1),
            Child = Ui.Centered(Ui.Label(ok == null ? "닫기" : "취소", 12)),
        }.Init();
        if (cancel != null) cancelBtn.Click += cancel;
        Grid.SetColumn(cancelBtn, 3);
        foot.Children.Add(cancelBtn);

        var body = new ScrollViewer { Content = _rows, MaxHeight = 560, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Child = new StackPanel { Children = { head, body, new Border { Margin = new Thickness(12, 0, 12, 8), Child = _error }, foot } };
    }

    private Grid Row(string label, Control value)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), MinHeight = 28 };
        g.Children.Add(Ui.Label(label, 12, Ui.Text));
        Grid.SetColumn(value, 1);
        g.Children.Add(value);
        _rows.Children.Add(g);
        return g;
    }

    public void SetError(string? message)
    {
        _error.Text = message ?? "";
        _error.IsVisible = !string.IsNullOrEmpty(message);
    }

    /// <summary>Current user parameter values (for expressions typed into number fields).</summary>
    public static IReadOnlyDictionary<string, double> ParameterValues { get; set; } = new Dictionary<string, double>();

    public TextBox AddNumber(string label, double value, string unit, Action<double> set, Action? changed = null, Feature? owner = null, string? bind = null)
    {
        string initial = owner != null && bind != null && owner.Expressions.TryGetValue(bind, out var ex) ? ex : Format(value, unit);
        var box = new TextBox
        {
            Text = initial,
            Height = 28,
            MinHeight = 0,
            Padding = new Thickness(6, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Ui.White,
            BorderBrush = Ui.Brush("#BDBDBD"),
        };
        box.Tag = label;
        void Apply()
        {
            if (TryParse(box.Text, out var v))
            {
                if (owner != null && bind != null)
                {
                    // a field that names a parameter keeps the expression, so it follows parameter changes
                    if (Params.HasName(box.Text ?? "")) owner.Expressions[bind] = box.Text!.Trim();
                    else owner.Expressions.Remove(bind);
                }
                set(v);
                changed?.Invoke();
            }
        }
        box.TextChanged += (_, _) => Apply();
        box.LostFocus += (_, _) => { if (TryParse(box.Text, out var v) && !Params.HasName(box.Text ?? "")) box.Text = Format(v, unit); };
        Row(label, box);
        return box;
    }

    public static string Format(double v, string unit) => $"{Math.Round(v, 4).ToString(System.Globalization.CultureInfo.InvariantCulture)} {unit}".Trim();

    /// <summary>Numbers with an optional unit; simple + - * / expressions are evaluated.</summary>
    public static bool TryParse(string? text, out double v)
    {
        v = 0;
        if (Params.HasName(text ?? ""))
        {
            if (Params.Eval(text, ParameterValues) is { } pv) { v = pv; return true; }
            return false;
        }
        var t = (text ?? "").Replace("mm", "").Replace("deg", "").Replace("°", "").Replace(",", ".").Trim();
        if (t.Length == 0) return false;
        try
        {
            var r = new System.Data.DataTable().Compute(t, null);
            v = Convert.ToDouble(r, System.Globalization.CultureInfo.InvariantCulture);
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
        catch
        {
            return false;
        }
    }

    public ComboBox AddCombo(string label, string[] items, int index, Action<int> set)
    {
        var c = new ComboBox { ItemsSource = items, SelectedIndex = index, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 28, MinHeight = 0, Padding = new Thickness(6, 2), FontSize = 12, Tag = label };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) set(c.SelectedIndex); };
        Row(label, c);
        return c;
    }

    /// <summary>Selection field: highlighted while it receives canvas picks.</summary>
    public FlatButton AddSelect(string label, string text, bool active, Action activate, Action clear)
    {
        var x = new FlatButton { Width = 20, Height = 20, Child = Ui.Centered(Icons.Line("close", 10, Ui.Muted)) }.Init();
        x.Click += clear;
        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(6, 0) };
        var arrow = Icons.Line("chevright", 12, active ? Ui.Accent : Ui.Muted);
        inner.Children.Add(arrow);
        var t = Ui.Label(text, 12, active ? Ui.Text : Ui.Muted);
        t.Margin = new Thickness(6, 0);
        Grid.SetColumn(t, 1);
        inner.Children.Add(t);
        Grid.SetColumn(x, 2);
        inner.Children.Add(x);
        var b = new FlatButton
        {
            Height = 28,
            Normal = active ? Ui.Brush("#0696D7") : Ui.Brush("#EDEDED"),
            HoverBrush = active ? Ui.Brush("#0696D7") : Ui.Brush("#E0E0E0"),
            Child = inner,
            Tag = label,
        }.Init();
        if (active)
        {
            t.Foreground = Brushes.White;
            ((Avalonia.Controls.Shapes.Path)((Canvas)((Viewbox)arrow).Child!).Children[0]).Stroke = Brushes.White;
        }
        b.Click += activate;
        Row(label, b);
        return b;
    }

    public CheckBox AddCheck(string label, bool value, Action<bool> set)
    {
        var c = new CheckBox { IsChecked = value, Tag = label, MinHeight = 0 };
        c.IsCheckedChanged += (_, _) => set(c.IsChecked == true);
        Row(label, c);
        return c;
    }

    public void AddInfo(string label, string value) => Row(label, Ui.Label(value, 12, Ui.Text));

    public void AddText(string text) =>
        _rows.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Text });

    public void AddControl(Control c) => _rows.Children.Add(c);

    public void AddHeader(string text) =>
        _rows.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Text, Margin = new Thickness(0, 6, 0, 0) });
}

public sealed class CommandSession
{
    public required string Id { get; init; }
    public Feature? Draft { get; set; }
    public int EditIndex { get; init; } = -1;
    public ModelState? Base { get; set; }
    /// <summary>What canvas clicks pick: "profiles", "bodies", "target", "tools" or "".</summary>
    public string SelectKind { get; set; } = "";
    public CommandPanel? Panel { get; set; }
    public Action? Rebuild { get; set; }
}

public sealed class SketchSession
{
    public required SketchFeature Draft { get; set; }
    public int EditIndex { get; init; } = -1;
    public ModelState? Base { get; init; }
    public string Tool { get; set; } = "";
    public List<P2> Pts { get; } = new();
    public P2? ChainStart { get; set; }
    public P2? Hover { get; set; }
    public bool HoverSnapped { get; set; }
    public int Selected { get; set; } = -1;
    public bool ShowGrid { get; set; } = true;
    public bool Snap { get; set; } = true;
    public double SnapStep { get; set; } = 1;
    public int Sides { get; set; } = 6;
    public List<(List<SketchEntity> ents, List<SketchConstraint> cons)> History { get; } = new();
    /// <summary>Refs picked so far by a constraint or dimension tool.</summary>
    public List<int> Picks { get; } = new();
    /// <summary>Snap ref of the first point of the shape being drawn (-2 = none).</summary>
    public int StartRef { get; set; } = -2;
    /// <summary>Previous line of a line chain (-1 = none).</summary>
    public int ChainEntity { get; set; } = -1;
    public int Dof { get; set; } = -1;
    public int DragRef { get; set; } = -2;

    public void Remember() => History.Add((Draft.Entities.Select(e => e.Clone()).ToList(),
        Draft.Constraints.Select(c => new SketchConstraint { Kind = c.Kind, Refs = (int[])c.Refs.Clone(), Value = c.Value }).ToList()));

    public void Restore((List<SketchEntity> ents, List<SketchConstraint> cons) h)
    {
        Draft.Entities = h.ents;
        Draft.Constraints = h.cons;
    }
}
