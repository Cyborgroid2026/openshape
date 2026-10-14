using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using SkiaSharp;
using Path = System.IO.Path;

namespace OpenShape;

/// <summary>Render workspace: scene settings, in-canvas path tracing and rendering images to PNG.</summary>
public sealed partial class MainWindow
{
    private void EnterRender()
    {
        View.RayTrace = true;
        SetPrompt("캔버스 내 렌더: 화면을 움직이지 않으면 점점 선명해집니다. 장면 설정에서 조명과 배경을 바꿀 수 있습니다.");
    }

    private void LeaveRender()
    {
        if (_workspace != "render") return;
        View.RayTrace = false;
    }

    /// <summary>Restart the in-canvas render (materials or scene changed).</summary>
    private void ResetRayTrace()
    {
        if (View.RayTrace) View.Invalidate3D();
    }

    private static double Linear(byte c) => Math.Pow(c / 255.0, 2.2);

    private float[] RayMaterials(IReadOnlyList<RenderItem> items)
    {
        var m = new float[items.Count * 5];
        for (int i = 0; i < items.Count; i++)
        {
            var (hex, metallic, roughness) = i < _itemBodies.Count ? _doc.MaterialOf(_itemBodies[i]) : ("#D6D2CC", 1.0, 0.4);
            var c = Color.Parse(hex);
            m[i * 5] = (float)Linear(c.R);
            m[i * 5 + 1] = (float)Linear(c.G);
            m[i * 5 + 2] = (float)Linear(c.B);
            m[i * 5 + 3] = (float)metallic;
            m[i * 5 + 4] = (float)roughness;
        }
        return m;
    }

    private double[] RayEnvironment()
    {
        var bodies = State.Bodies.Where(b => !_doc.HiddenBodies.Contains(b.Id)).ToList();
        double z = bodies.Count == 0 ? 0 : bodies.Min(b => b.Solid.Info.Min.Z);
        return _doc.Render.ToArray(z - 1e-3);
    }

    private void ShowSceneSettings()
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "scenesettings" };
        var r = _doc.Render;
        void Changed()
        {
            _dirty = true;
            UpdateTitle();
            ResetRayTrace();
        }
        var p = new CommandPanel("장면 설정", "scenesettings", CancelCommand, CancelCommand, "닫기");
        p.AddHeader("환경");
        var envs = RenderScene.Environments;
        p.AddCombo("환경 프리셋", envs.Select(RenderScene.EnvironmentName).ToArray(), Math.Max(0, Array.IndexOf(envs, r.Environment)), i => { r.Environment = envs[i]; Changed(); });
        p.AddNumber("노출 (EV)", r.Exposure, "", v => { r.Exposure = Math.Clamp(v, -6, 6); Changed(); });
        p.AddNumber("태양 방위각", r.SunAzimuth, "deg", v => { r.SunAzimuth = v; Changed(); });
        p.AddNumber("태양 고도", r.SunElevation, "deg", v => { r.SunElevation = Math.Clamp(v, 1, 90); Changed(); });
        p.AddHeader("배경");
        p.AddCombo("배경", new[] { "환경", "단색" }, r.SolidBackdrop ? 1 : 0, i => { r.SolidBackdrop = i == 1; Changed(); });
        var colors = new[] { ("흰색", "#FFFFFF"), ("밝은 회색", "#D9D9D9"), ("어두운 회색", "#404040"), ("검은색", "#000000") };
        p.AddCombo("배경 색상", colors.Select(c => c.Item1).ToArray(), Math.Max(0, Array.FindIndex(colors, c => c.Item2 == r.BackdropColor)), i => { r.BackdropColor = colors[i].Item2; Changed(); });
        p.AddHeader("바닥");
        p.AddCheck("바닥 그림자", r.GroundShadow, v => { r.GroundShadow = v; Changed(); });
        p.AddNumber("바닥 반사", r.GroundReflection * 100, "%", v => { r.GroundReflection = Math.Clamp(v / 100, 0, 1); Changed(); });
        _cmd.Panel = p;
        ShowDialog(p);
    }

    private static readonly (string name, int w, int h)[] RenderSizes = { ("뷰포트 크기", 0, 0), ("1280 × 720 (HD)", 1280, 720), ("1920 × 1080 (Full HD)", 1920, 1080), ("2560 × 1440", 2560, 1440), ("3840 × 2160 (4K)", 3840, 2160) };
    private static readonly (string name, int spp)[] RenderQualities = { ("초안 (16회)", 16), ("표준 (64회)", 64), ("고급 (256회)", 256) };
    private int _renderSize = 1, _renderQuality = 1;
    private CancellationTokenSource? _renderCancel;

    private void ShowRenderImage()
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "renderimage" };
        var p = new CommandPanel("렌더링", "renderimage", null, () => { _renderCancel?.Cancel(); CancelCommand(); });
        p.AddCombo("이미지 크기", RenderSizes.Select(s => s.name).ToArray(), _renderSize, i => _renderSize = i);
        p.AddCombo("품질", RenderQualities.Select(q => q.name).ToArray(), _renderQuality, i => _renderQuality = i);
        p.AddText("현재 화면 방향으로 렌더링합니다. 장면 설정과 각 본체의 모양(재질)이 쓰입니다.");
        var status = Ui.Label("", 12, Ui.Muted);
        var preview = new Image { MaxWidth = 300, MaxHeight = 200, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        var go = new FlatButton { Height = 30, Normal = Ui.Accent, Child = Ui.Centered(Ui.Label("렌더", 12, Brushes.White)) }.Init();
        var save = new FlatButton { Height = 30, Normal = Ui.Brush("#EDEDED"), IsEnabled = false, Child = Ui.Centered(Ui.Label("PNG로 저장...", 12)) }.Init();
        byte[]? result = null;
        int rw = 0, rh = 0;
        go.Click += async () =>
        {
            var (_, w0, h0) = RenderSizes[_renderSize];
            rw = w0 == 0 ? Math.Max(16, (int)View.Bounds.Width) : w0;
            rh = h0 == 0 ? Math.Max(16, (int)View.Bounds.Height) : h0;
            int spp = RenderQualities[_renderQuality].spp;
            _renderCancel?.Cancel();
            _renderCancel = new CancellationTokenSource();
            var token = _renderCancel.Token;
            go.IsEnabled = false;
            save.IsEnabled = false;
            var started = DateTime.Now;
            var progress = new Progress<int>(n => status.Text = $"렌더링 중... {n}/{spp}회 반복 ({(DateTime.Now - started).TotalSeconds:0}초)");
            try
            {
                result = await RenderImage(rw, rh, spp, progress, token);
                status.Text = $"완료: {rw} × {rh}, {spp}회 반복, {(DateTime.Now - started).TotalSeconds:0.#}초";
                preview.Source = ToBitmap(result, rw, rh);
                save.IsEnabled = true;
            }
            catch (OperationCanceledException) { status.Text = "취소했습니다."; }
            go.IsEnabled = true;
        };
        save.Click += async () =>
        {
            if (result == null) return;
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "렌더링 저장",
                SuggestedFileName = $"{(_path != null ? Path.GetFileNameWithoutExtension(_path) : "렌더링")}.png",
                DefaultExtension = "png",
                FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } } },
            });
            if (f?.TryGetLocalPath() is { } path) SavePng(result, rw, rh, path);
        };
        p.AddControl(go);
        p.AddControl(status);
        p.AddControl(preview);
        p.AddControl(save);
        _cmd.Panel = p;
        ShowDialog(p);
    }

    /// <summary>Path-trace the current view at the given size; RGBA bytes.</summary>
    public async Task<byte[]> RenderImage(int w, int h, int spp, IProgress<int>? progress = null, CancellationToken token = default)
    {
        View.EnsureRendered();
        var items = View.Items;
        var handles = items.Select(i => i.Solid.Handle).ToArray();
        var mats = RayMaterials(items);
        var env = RayEnvironment();
        var cam = View.Cam.ToArray(w, h);
        var accum = new float[w * h * 3];
        var buf = new byte[w * h * 4];
        // a few samples per call keeps the progress moving and cancel responsive
        int done = 0;
        while (done < spp)
        {
            token.ThrowIfCancellationRequested();
            int step = Math.Min(spp - done, Math.Max(1, 4_000_000 / (w * h)));
            int total = done + step;
            int seed = done;
            // no UI work below, so callers may wait on this without a dispatcher
            await Task.Run(() => RayPass(handles, mats, cam, w, h, env, step, (ulong)(seed + 1), accum, total, buf), token).ConfigureAwait(false);
            done = total;
            progress?.Report(done);
        }
        GC.KeepAlive(items);
        return buf;
    }

    private static unsafe void RayPass(uint[] handles, float[] mats, double[] cam, int w, int h, double[] env, int spp, ulong seed, float[] accum, int total, byte[] buf)
    {
        fixed (uint* ph = handles)
        fixed (float* pm = mats, pa = accum)
        fixed (double* pc = cam, pe = env)
        fixed (byte* pb = buf)
            Native.osc_raytrace(ph, pm, handles.Length, pc, w, h, pe, spp, seed, pa, total, pb);
    }

    public static void SavePng(byte[] rgba, int w, int h, string path)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 95);
        using var f = File.Create(path);
        data.SaveTo(f);
    }

    private static unsafe WriteableBitmap ToBitmap(byte[] rgba, int w, int h)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        fixed (byte* p = rgba)
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(p + y * w * 4, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, w * 4);
        return bmp;
    }
}
