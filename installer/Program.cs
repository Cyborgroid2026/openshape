using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;
using IOPath = System.IO.Path;

namespace OpenShapeInstaller;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // command-line modes (used by the app's update check and by tests)
        if (args.Length > 0 && args[0] == "--check")
        {
            var r = Updater.Latest().GetAwaiter().GetResult();
            var installed = Updater.Installed();
            Console.WriteLine($"latest={r.Version} installed={installed ?? "-"} asset={r.AssetName} size={r.Size} update={Updater.IsNewer(r.Version, installed)}");
            return 0;
        }
        if (args.Length > 1 && args[0] == "--download")
        {
            var r = Updater.Latest().GetAwaiter().GetResult();
            Console.WriteLine(Updater.Download(r, args[1]).GetAwaiter().GetResult());
            return 0;
        }
        if (args.Length > 1 && args[0] == "--sha256")
        {
            Console.WriteLine($"{NativeCore.Sha256File(args[1])}  rust={NativeCore.Available}");
            return 0;
        }
        InstallerApp.Reinstall = args.Contains("--reinstall");
        return AppBuilder.Configure<InstallerApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class InstallerApp : Application
{
    public static bool Reinstall;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            var w = new InstallerWindow();
            d.MainWindow = w;
            w.Opened += (_, _) => _ = w.Run(Reinstall);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// The streaming installer window, laid out like Autodesk's "Fusion Client Downloader": a borderless 860×500
/// splash picture that changes as the install progresses, the step text and a small percent bar near the bottom,
/// and white message dialogs ("최신 버전이 이미 설치되어 있습니다", "설치 중 문제 발생") on top.
/// </summary>
public sealed class InstallerWindow : Window
{
    private const string Product = "OpenShape CAD";
    private static readonly FontFamily UbuntuBold = new("Ubuntu, Noto Sans CJK KR, DejaVu Sans, sans-serif");
    private static readonly FontFamily UbuntuFont = new("Ubuntu, Noto Sans CJK KR, Noto Sans KR, DejaVu Sans, sans-serif");
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public readonly TextBlock Status = new()
    {
        FontFamily = UbuntuBold, FontWeight = FontWeight.Bold, FontSize = 14, Foreground = B("#4D4D4D"),
        HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 3, OffsetX = 0, OffsetY = 0, Opacity = 0.85 },
    };
    public readonly PercentBar Bar = new() { Width = 258, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5) };
    /// <summary>Kept for the update notification and tests: extra detail (sizes) shown under the bar.</summary>
    public readonly TextBlock Detail = new() { FontFamily = UbuntuBold, FontSize = 11, Foreground = B("#4D4D4D"), HorizontalAlignment = HorizontalAlignment.Center };

    private readonly Grid _slides = new();
    private readonly Grid _overlay = new();
    private int _slide = -1;
    private CancellationTokenSource _cts = new();

    public InstallerWindow()
    {
        Title = $"{Product} 클라이언트 다운로더";
        Width = 860;
        Height = 500;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Black;
        FontFamily = UbuntuFont;
        Icon = null;

        var progress = new StackPanel
        {
            Margin = new Thickness(0, 400, 0, 0),
            Children = { Status, Bar, Detail },
        };
        Content = new Grid { Children = { _slides, progress, _overlay } };
        ShowSlide(0, false);
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && _overlay.Children.Count == 0) BeginMoveDrag(e);
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closing += (_, _) => _cts.Cancel();
    }

    // ------------------------------------------------------------------ artwork

    /// <summary>Background picture n (0 = product splash). Drawn here: dark backdrop with lit metal bands.</summary>
    private static Control Slide(int n)
    {
        var c = new Canvas { Width = 860, Height = 500, ClipToBounds = true };
        c.Children.Add(new Rectangle
        {
            Width = 860, Height = 500,
            Fill = new RadialGradientBrush
            {
                Center = new RelativePoint(0.75, 0.25, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.75, 0.25, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.9, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.9, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#26221E"), 0), new GradientStop(Color.Parse("#050505"), 1) },
            },
        });
        // two folded metal plates; each slide moves the light along them
        double shift = n * 38;
        void Band(string d, string a, string b, double edgeOpacity)
        {
            c.Children.Add(new Path
            {
                Data = Geometry.Parse(d),
                Fill = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse(a), 0), new GradientStop(Color.Parse(b), 1) },
                },
                Stroke = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromArgb((byte)(255 * edgeOpacity), 0xE8, 0xD8, 0xC0), 0), new GradientStop(Color.FromArgb(0, 0xE8, 0xD8, 0xC0), 1) },
                },
                StrokeThickness = 2.5,
            });
        }
        Band($"M{430 - shift},-10 L{520 - shift},-10 L{760 - shift * 0.5},400 L{720 - shift * 0.5},410 Z", "#3A3632", "#0B0B0B", 0.9);
        Band($"M-20,{300 + shift * 0.4} L{360},{400} L{650},{370 - shift * 0.3} L{660},{385 - shift * 0.3} L{380},{470} L{-20},{345 + shift * 0.4} Z", "#141414", "#2C2925", 0.7);
        Band($"M{560 - shift * 0.3},0 L860,0 L860,{260 + shift} L{730 - shift * 0.4},420 Z", "#1E1D1C", "#090909", 0.25);
        // warm reflection at the fold
        c.Children.Add(new Path
        {
            Data = Geometry.Parse($"M{350},{405} L{650},{372 - shift * 0.3} L{655},{380 - shift * 0.3} L{360},{416} Z"),
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#00F2A33A"), 0), new GradientStop(Color.Parse("#C8F2A33A"), 0.6), new GradientStop(Color.Parse("#00F2A33A"), 1) },
            },
        });
        if (n == 0)
        {
            var logo = Logo(100);
            Canvas.SetLeft(logo, 48);
            Canvas.SetTop(logo, 196);
            c.Children.Add(logo);
            var name = new TextBlock { Text = "OpenShape", FontSize = 38, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, FontFamily = UbuntuFont };
            Canvas.SetLeft(name, 170);
            Canvas.SetTop(name, 218);
            c.Children.Add(name);
        }
        else
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Logo(34), new TextBlock { Text = "OPENSHAPE CAD", FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center } } };
            Canvas.SetLeft(head, 20);
            Canvas.SetTop(head, 18);
            c.Children.Add(head);
            c.Children.Add(new Rectangle { Width = 860, Height = 1, Fill = B("#55FFFFFF"), [Canvas.TopProperty] = 73.0 });
        }
        var brand = new TextBlock { Text = "OPENSHAPE", FontSize = 17, FontWeight = FontWeight.Black, Foreground = Brushes.White, FontFamily = UbuntuFont };
        Canvas.SetRight(brand, 26);
        Canvas.SetBottom(brand, 18);
        c.Children.Add(brand);
        return c;
    }

    /// <summary>The app icon: an amber isometric block with a carved slot.</summary>
    private static Control Logo(double size)
    {
        var canvas = new Canvas { Width = 256, Height = 256 };
        void Face(string d, string a, string b) => canvas.Children.Add(new Path
        {
            Data = Geometry.Parse(d),
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(a), 0), new GradientStop(Color.Parse(b), 1) },
            },
            Stroke = new SolidColorBrush(Color.FromArgb(120, 0x6B, 0x3A, 0x0A)),
            StrokeThickness = 3,
        });
        Face("M128,24 L224,74 L128,124 L32,74 Z", "#FFD37A", "#F7B245");
        Face("M32,74 L128,124 V228 L32,178 Z", "#F09A2C", "#D97814");
        Face("M224,74 L128,124 V228 L224,178 Z", "#C96A12", "#A8520A");
        Face("M128,52 L176,77 L128,102 L80,77 Z", "#9C5510", "#8A480C");
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    private const int SlideCount = 5;

    /// <summary>Switch the background (1.2 s cross-fade, like the original's back buffer).</summary>
    private void ShowSlide(int n, bool fade = true)
    {
        n = Math.Clamp(n, 0, SlideCount - 1);
        if (n == _slide) return;
        _slide = n;
        var next = Slide(n);
        if (!fade)
        {
            _slides.Children.Clear();
            _slides.Children.Add(next);
            return;
        }
        next.Opacity = 0;
        next.Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(1200) } };
        _slides.Children.Add(next);
        Dispatcher.UIThread.Post(() => next.Opacity = 1, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() =>
        {
            while (_slides.Children.Count > 1) _slides.Children.RemoveAt(0);
        }, TimeSpan.FromMilliseconds(1300));
    }

    /// <summary>Step text and bar. percent == null shows the moving (indeterminate) bar.</summary>
    public void Progress(string text, double? percent)
    {
        Status.Text = text;
        Bar.Value = percent;
        if (percent is { } p) ShowSlide((int)Math.Floor(SlideCount * p / 100));
    }

    // ------------------------------------------------------------------ dialogs

    private static Button DialogButton(string text, bool primary)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 13,
            Height = 34,
            Padding = new Thickness(40, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Foreground = primary ? Brushes.White : B("#6E6E6E"),
            Background = primary ? B("#00A5E1") : B("#FAFAFA"),
            BorderBrush = primary ? B("#00A5E1") : B("#A2A2A2"),
        };
        return b;
    }

    private Task<bool> Dialog(string title, Control body, string? secondary, string primary)
    {
        var done = new TaskCompletionSource<bool>();
        void Finish(bool v)
        {
            _overlay.Children.Clear();
            done.TrySetResult(v);
        }
        var close = new Button
        {
            Content = new Path { Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10"), Stroke = B("#999999"), StrokeThickness = 1.6, Width = 10, Height = 10 },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => Finish(false);
        var titleBar = new Grid
        {
            Height = 78,
            Children =
            {
                new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.Light, Foreground = B("#6B6B6B"), Margin = new Thickness(34, 0, 60, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                new Border { Margin = new Thickness(0, 0, 24, 0), Child = close, HorizontalAlignment = HorizontalAlignment.Right },
            },
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 34, 34) };
        if (secondary != null)
        {
            var s = DialogButton(secondary, false);
            s.Click += (_, _) => Finish(false);
            buttons.Children.Add(s);
        }
        var p = DialogButton(primary, true);
        p.Click += (_, _) => Finish(true);
        buttons.Children.Add(p);
        var box = new Border
        {
            Width = 500,
            Background = Brushes.White,
            BorderBrush = B("#EFEFEF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            BoxShadow = BoxShadows.Parse("0 2 5 0 #000000"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 65, 0, 0),
            Child = new StackPanel
            {
                Children =
                {
                    titleBar,
                    new Border { Height = 1, Background = B("#D8D8D8") },
                    new Border { Padding = new Thickness(34, 24, 34, 40), Child = body },
                    buttons,
                },
            },
        };
        _overlay.Children.Clear();
        _overlay.Children.Add(new Border { Background = B("#01000000") });
        _overlay.Children.Add(box);
        return done.Task;
    }

    private static TextBlock BodyText(string t, string color = "#767676") =>
        new() { Text = t, FontSize = 13, Foreground = B(color), TextWrapping = TextWrapping.Wrap, LineHeight = 23 };

    public Task<bool> AskLaunch() =>
        Dialog("최신 버전이 이미 설치되어 있습니다.", BodyText($"최신 버전의 {Product}이(가) 설치되어 있습니다.\n\n응용프로그램을 시작하시겠습니까?"), "아니오", "예");

    public Task<bool> ShowError(string what, string todo)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("113,20,*"), RowDefinitions = new RowDefinitions("Auto,24,Auto") };
        void Cell(Control c, int r, int col) { Grid.SetRow(c, r); Grid.SetColumn(c, col); g.Children.Add(c); }
        Cell(new TextBlock { Text = "무엇이 문제입니까?", FontSize = 13, Foreground = B("#111111"), HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right }, 0, 0);
        Cell(BodyText(what), 0, 2);
        Cell(new TextBlock { Text = "어떻게 해야 합니까?", FontSize = 13, Foreground = B("#111111"), HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right }, 2, 0);
        Cell(BodyText(todo), 2, 2);
        return Dialog($"{Product} 설치 중 문제 발생", g, "로그 파일 위치 열기", "확인");
    }

    // ------------------------------------------------------------------ flow

    /// <summary>Check → download → install → shortcuts → launch, the same steps as Fusion's streaming installer.</summary>
    public async Task Run(bool reinstall = false)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Detail.Text = "";
        Progress("초기화 중...", null);
        string log = IOPath.Combine(IOPath.GetTempPath(), "openshape-installer", "install.log");
        Directory.CreateDirectory(IOPath.GetDirectoryName(log)!);
        try
        {
            Progress("사전 확인 실행", null);
            var latest = await Task.Run(() => Updater.Latest(ct), ct);
            var installed = Updater.Installed();
            if (!reinstall && !Updater.IsNewer(latest.Version, installed))
            {
                Progress($"업데이트할 필요가 없습니다. {Product} {installed}이(가) 설치되어 있습니다.", 100);
                if (await AskLaunch()) Updater.Launch();
                Close();
                return;
            }
            var dir = IOPath.Combine(IOPath.GetTempPath(), "openshape-installer");
            Progress("설치 프로그램 다운로드 중", 0);
            var deb = await Updater.Download(latest, dir, new Progress<(long done, long total)>(p =>
            {
                // download is the first 80 % of the bar, like the streamer's deploy steps
                Progress("설치 프로그램 다운로드 중", p.total > 0 ? 80.0 * p.done / p.total : 0);
                Detail.Text = $"{p.done / 1048576.0:0.0} MB / {p.total / 1048576.0:0.0} MB";
            }), ct);
            Detail.Text = "";
            Progress("설정 중", 85);
            var (code, output) = await Updater.Install(deb, ct);
            await File.WriteAllTextAsync(log, output, ct);
            if (code != 0)
            {
                Progress("설정 중", null);
                bool auth = code is 126 or 127;
                var open = await ShowError(
                    auth ? "관리자 인증이 취소되어 설치하지 못했습니다." : (output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "설치하지 못했습니다."),
                    auth ? "설치 프로그램을 다시 실행하고 관리자 암호를 입력하십시오." : "인터넷 연결과 디스크 공간을 확인한 후 설치 프로그램을 다시 실행하십시오.");
                if (!open) Updater.OpenFolder(IOPath.GetDirectoryName(log)!);
                Close();
                return;
            }
            Progress("바탕 화면에 바로 가기 작성 중", 95);
            await Task.Run(Updater.CreateShortcuts, ct);
            Progress("시작", 100);
            await Task.Delay(800, ct);
            Updater.Launch();
            Close();
        }
        catch (OperationCanceledException)
        {
            // closed
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(log, ex.ToString());
            var net = ex is HttpRequestException;
            var ok = await ShowError(
                net ? $"{Product}을(를) 설치하려면 인터넷에 연결해야 하는데 인터넷에 액세스할 수 없습니다." : ex.Message,
                net ? "인터넷이 정상적으로 작동 중인지 확인하거나 보다 안정적인 연결로 전환한 후에 설치 프로그램을 다시 실행하십시오."
                    : "설치 프로그램을 다시 실행하십시오. 문제가 계속되면 로그 파일을 확인하십시오.");
            if (!ok) Updater.OpenFolder(IOPath.GetDirectoryName(log)!);
            Close();
        }
    }
}

/// <summary>jQuery-UI style progress bar with the percentage written in the middle (null = moving stripe).</summary>
public sealed class PercentBar : Control
{
    private double? _value;
    private readonly DispatcherTimer _timer;
    private double _phase;

    public PercentBar()
    {
        Opacity = 0.7;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => { _phase = (_phase + 0.012) % 1; InvalidateVisual(); });
        _timer.Start();
    }

    public double? Value
    {
        get => _value;
        set { _value = value is { } v ? Math.Clamp(v, 0, 100) : null; InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        var border = new Pen(new SolidColorBrush(Color.Parse("#AAAAAA")), 1);
        ctx.DrawRectangle(Brushes.White, border, r.Deflate(0.5), 4, 4);
        if (_value is { } v)
        {
            if (v > 0)
            {
                var fill = new Rect(r.X, r.Y, Math.Max(2, r.Width * v / 100), r.Height);
                ctx.DrawRectangle(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#E6E6E6"), 0), new GradientStop(Color.Parse("#C4C4C4"), 1) },
                }, border, fill.Deflate(0.5), 4, 4);
            }
            var text = new FormattedText($"{(int)v}%", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Ubuntu, DejaVu Sans, sans-serif"), FontStyle.Normal, FontWeight.Bold), 13, new SolidColorBrush(Color.Parse("#4D4D4D")));
            ctx.DrawText(text, new Point((r.Width - text.Width) / 2, (r.Height - text.Height) / 2));
        }
        else
        {
            using (ctx.PushClip(new RoundedRect(r.Deflate(1), 4)))
            {
                double w = r.Width * 0.35, x = -w + (r.Width + w) * _phase;
                ctx.DrawRectangle(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#00C4C4C4"), 0), new GradientStop(Color.Parse("#FFC4C4C4"), 0.5), new GradientStop(Color.Parse("#00C4C4C4"), 1) },
                }, null, new Rect(x, r.Y, w, r.Height));
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
