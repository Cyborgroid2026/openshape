using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Path = System.IO.Path;

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
        return AppBuilder.Configure<InstallerApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class InstallerApp : Application
{
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
            w.Opened += (_, _) => _ = w.Run();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>The streaming installer window: logo, product name, status, progress, buttons.</summary>
public sealed class InstallerWindow : Window
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#0696D7"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#3C3C3C"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#808080"));

    public readonly TextBlock Status = new() { FontSize = 14, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    public readonly TextBlock Detail = new() { FontSize = 12, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center };
    public readonly ProgressBar Bar = new() { Height = 4, MinHeight = 4, Minimum = 0, Maximum = 1, Foreground = Accent, Background = new SolidColorBrush(Color.Parse("#E3E3E3")) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private CancellationTokenSource _cts = new();

    public InstallerWindow()
    {
        Title = "OpenShape CAD 설치";
        Width = 560;
        Height = 360;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        FontFamily = new FontFamily("Noto Sans CJK KR, Noto Sans KR, NanumGothic, Ubuntu, DejaVu Sans");

        var logo = Logo(84);
        var title = new TextBlock { Text = "OpenShape CAD", FontSize = 26, FontWeight = FontWeight.Light, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center };
        var body = new StackPanel
        {
            Margin = new Thickness(48, 34, 48, 0),
            Spacing = 10,
            Children = { logo, title, new Border { Height = 10 }, Status, Bar, Detail },
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 0, 20, 18), VerticalAlignment = VerticalAlignment.Bottom };
        footer.Children.Add(new TextBlock { Text = $"OpenShape Client Downloader {typeof(InstallerWindow).Assembly.GetName().Version?.ToString(3)} · github.com/{Updater.Repo}", FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_buttons, 1);
        footer.Children.Add(_buttons);
        Content = new Grid { Children = { body, footer } };
        Closing += (_, _) => _cts.Cancel();
    }

    /// <summary>The app icon: an amber isometric block with a carved slot.</summary>
    private static Control Logo(double size)
    {
        var canvas = new Canvas { Width = 256, Height = 256 };
        void Face(string d, string a, string b) => canvas.Children.Add(new Avalonia.Controls.Shapes.Path
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
        return new Viewbox { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, Child = canvas };
    }

    public void Buttons(params (string text, bool primary, Action click)[] bs)
    {
        _buttons.Children.Clear();
        foreach (var (text, primary, click) in bs)
        {
            var b = new Button
            {
                Content = text,
                MinWidth = 84,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = primary ? Accent : new SolidColorBrush(Color.Parse("#E8E8E8")),
                Foreground = primary ? Brushes.White : Ink,
                CornerRadius = new CornerRadius(2),
            };
            b.Click += (_, _) => click();
            _buttons.Children.Add(b);
        }
    }

    /// <summary>Check → download → install → launch, like Fusion's streaming installer.</summary>
    public async Task Run(bool reinstall = false)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Bar.IsIndeterminate = true;
        Detail.Text = "";
        Status.Text = "최신 버전을 확인하는 중...";
        Buttons(("취소", false, Close));
        try
        {
            var latest = await Task.Run(() => Updater.Latest(ct), ct);
            var installed = Updater.Installed();
            if (!reinstall && !Updater.IsNewer(latest.Version, installed))
            {
                Bar.IsIndeterminate = false;
                Bar.Value = 1;
                Status.Text = $"OpenShape CAD {installed}이(가) 최신 버전입니다.";
                Detail.Text = "업데이트할 내용이 없습니다.";
                Buttons(("다시 설치", false, () => _ = Run(true)), ("닫기", false, Close), ("실행", true, () => { Updater.Launch(); Close(); }));
                return;
            }
            Status.Text = installed == null ? $"OpenShape CAD {latest.Version}을(를) 내려받는 중..." : $"OpenShape CAD {installed} → {latest.Version} 업데이트를 내려받는 중...";
            Bar.IsIndeterminate = false;
            var dir = Path.Combine(Path.GetTempPath(), "openshape-installer");
            var deb = await Updater.Download(latest, dir, new Progress<(long done, long total)>(p =>
            {
                Bar.Value = p.total > 0 ? (double)p.done / p.total : 0;
                Detail.Text = $"{p.done / 1048576.0:0.0} MB / {p.total / 1048576.0:0.0} MB";
            }), ct);
            Status.Text = "설치하는 중... 관리자 암호를 입력하세요.";
            Detail.Text = "OpenCASCADE 등 필요한 패키지도 함께 설치합니다.";
            Bar.IsIndeterminate = true;
            Buttons();
            var (code, output) = await Updater.Install(deb, ct);
            Bar.IsIndeterminate = false;
            if (code != 0)
            {
                Status.Text = code is 126 or 127 ? "관리자 인증이 취소되었습니다." : "설치에 실패했습니다.";
                Detail.Text = output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
                Buttons(("닫기", false, Close), ("다시 시도", true, () => _ = Run(reinstall)));
                return;
            }
            Bar.Value = 1;
            Status.Text = $"OpenShape CAD {latest.Version} 설치를 마쳤습니다.";
            Detail.Text = "잠시 후 OpenShape CAD를 실행합니다.";
            Buttons(("닫기", false, Close));
            await Task.Delay(1500, ct);
            Updater.Launch();
            Close();
        }
        catch (OperationCanceledException)
        {
            // closed
        }
        catch (Exception ex)
        {
            Bar.IsIndeterminate = false;
            Status.Text = "OpenShape CAD를 설치할 수 없습니다.";
            Detail.Text = ex.Message;
            Buttons(("닫기", false, Close), ("다시 시도", true, () => _ = Run(reinstall)));
        }
    }
}
