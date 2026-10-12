using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using SkiaSharp;

namespace OpenShape;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--export-icon")
        {
            ExportIcon(args[1]);
            return 0;
        }
        return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>App icon: an amber isometric block with a carved slot.</summary>
    private static void ExportIcon(string path)
    {
        const int n = 256;
        using var surface = SKSurface.Create(new SKImageInfo(n, n, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        void Face(string d, SKColor a, SKColor b)
        {
            using var p = SKPath.ParseSvgPathData(d);
            using var paint = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 20), new SKPoint(0, 240), new[] { a, b }, SKShaderTileMode.Clamp) };
            c.DrawPath(p, paint);
            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = new SKColor(0x6B, 0x3A, 0x0A, 120) };
            c.DrawPath(p, edge);
        }
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 60), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10) })
            c.DrawOval(new SKRect(40, 210, 216, 244), shadow);
        Face("M128,24 L224,74 L128,124 L32,74 Z", new SKColor(0xFF, 0xD3, 0x7A), new SKColor(0xF7, 0xB2, 0x45));
        Face("M32,74 L128,124 V228 L32,178 Z", new SKColor(0xF0, 0x9A, 0x2C), new SKColor(0xD9, 0x78, 0x14));
        Face("M224,74 L128,124 V228 L224,178 Z", new SKColor(0xC9, 0x6A, 0x12), new SKColor(0xA8, 0x52, 0x0A));
        // slot cut into the top face
        Face("M128,52 L176,77 L128,102 L80,77 Z", new SKColor(0x9C, 0x55, 0x10), new SKColor(0x8A, 0x48, 0x0C));
        using var data = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }
}

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        var uiFont = new FontFamily("Noto Sans CJK KR, Noto Sans KR, NanumGothic, Ubuntu, DejaVu Sans");
        Styles.Add(new Style(x => x.OfType<Window>()) { Setters = { new Setter(TemplatedControl.FontFamilyProperty, uiFont) } });
        Styles.Add(new Style(x => x.OfType<PopupRoot>()) { Setters = { new Setter(TemplatedControl.FontFamilyProperty, uiFont) } });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(desktop.Args ?? Array.Empty<string>());
        base.OnFrameworkInitializationCompleted();
    }
}
