using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Path = System.IO.Path;

namespace OpenShape;

/// <summary>Fusion's default keyboard shortcuts and the "S" shortcut box (command search).</summary>
public sealed partial class MainWindow
{
    /// <summary>Single-key shortcuts outside sketches (Fusion defaults).</summary>
    private static readonly Dictionary<Key, string> ModelKeys = new()
    {
        [Key.E] = "extrude",
        [Key.Q] = "presspull",
        [Key.F] = "fillet",
        [Key.M] = "move",
        [Key.J] = "joint",
        [Key.I] = "measure",
        [Key.A] = "appearance",
        [Key.L] = "sketch",
        [Key.R] = "sketch",
        [Key.C] = "sketch",
    };

    /// <summary>Single-key shortcuts inside a sketch.</summary>
    private static readonly Dictionary<Key, string> SketchKeys = new()
    {
        [Key.L] = "line",
        [Key.R] = "rect",
        [Key.C] = "circle",
        [Key.D] = "dimension",
        [Key.T] = "trim",
        [Key.P] = "polygon",
        [Key.E] = "extrude",
        [Key.I] = "measure",
    };

    /// <summary>Key label shown in tooltips and menus.</summary>
    public static readonly Dictionary<string, string> ShortcutLabels = new()
    {
        ["extrude"] = "E", ["presspull"] = "Q", ["fillet"] = "F", ["move"] = "M", ["joint"] = "J", ["measure"] = "I",
        ["appearance"] = "A", ["line"] = "L", ["rect"] = "R", ["circle"] = "C", ["dimension"] = "D", ["trim"] = "T",
        ["polygon"] = "P", ["delete"] = "Delete",
    };

    private static string WithKey(string id, string name) => ShortcutLabels.TryGetValue(id, out var k) ? $"{name} ({k})" : name;

    private readonly List<string> _copied = new();

    /// <summary>Single-key and Ctrl shortcuts; returns true when the key was used.</summary>
    private bool HandleShortcut(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control), shift = mods.HasFlag(KeyModifiers.Shift), alt = mods.HasFlag(KeyModifiers.Alt);
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.S when shift: _ = SaveAs(); return true;
                case Key.Z when shift: Redo(); return true;
                case Key.C when _sk == null && _selBodies.Count > 0:
                    // like Fusion: copy the selected bodies; Ctrl+V places a copy with Move
                    _copied.Clear();
                    _copied.AddRange(_selBodies);
                    SetPrompt($"본체 {_copied.Count}개를 복사했습니다. Ctrl+V로 붙여넣습니다.");
                    return true;
                case Key.V when _sk == null && _cmd == null && _copied.Count > 0:
                    OpenCommand(new MoveFeature { Name = _doc.NextName("move"), Bodies = _copied.Where(id => State.Find(id) != null).ToList(), CreateCopy = true }, -1);
                    return true;
                case Key.A when _sk == null:
                    _selBodies.Clear();
                    foreach (var b in State.Bodies.Where(b => !_doc.HiddenBodies.Contains(b.Id))) _selBodies.Add(b.Id);
                    SelectionChanged();
                    return true;
            }
            return false;
        }
        if (alt || shift) return false;
        switch (e.Key)
        {
            case Key.S:
                ShowShortcutBox();
                return true;
            case Key.V when _sk == null && _selBodies.Count > 0:
                // toggle visibility of the selection
                foreach (var id in _selBodies.ToList())
                    if (!_doc.HiddenBodies.Remove(id)) _doc.HiddenBodies.Add(id);
                RebuildBrowser();
                UpdateItems();
                return true;
            case Key.F6:
                FitAll();
                return true;
        }
        if (_sk != null)
        {
            if (!SketchKeys.TryGetValue(e.Key, out var sk)) return false;
            if (sk is "extrude") { FinishSketch(true); RunCommand("extrude"); }
            else RunCommand(sk);
            return true;
        }
        if (_cmd != null || !ModelKeys.TryGetValue(e.Key, out var cmd)) return false;
        RunCommand(cmd);
        // outside a sketch L/R/C first ask for a plane, then start that tool (Fusion does the same)
        if (cmd == "sketch") _pendingSketchTool = e.Key switch { Key.L => "line", Key.R => "rect", _ => "circle" };
        return true;
    }

    private string? _pendingSketchTool;

    private Border? _shortcutBox;

    /// <summary>All commands that can be searched in the shortcut box.</summary>
    private IEnumerable<(string id, string name)> SearchableCommands()
    {
        var sketchOnly = new HashSet<string> { "line", "rect", "crect", "circle", "arc", "polygon", "trim", "dimension", "finish" };
        foreach (var (id, name) in ToolNames)
        {
            if (Unsupported.Contains(id)) continue;
            if (id.StartsWith("c_") || sketchOnly.Contains(id)) { if (_sk != null) yield return (id, name); }
            else yield return (id, name);
        }
        yield return ("parameters", "매개변수 변경");
        yield return ("export", "내보내기");
    }

    /// <summary>"S": a search box listing commands; Enter runs the first match (like Fusion's shortcut box).</summary>
    public void ShowShortcutBox(string text = "")
    {
        if (_shortcutBox != null) { _canvasLayer.Children.Remove(_shortcutBox); _shortcutBox = null; }
        var box = new TextBox { Text = text, Watermark = "명령 검색 (예: 돌출, 모깎기)", Height = 32, MinHeight = 0, Padding = new Thickness(8, 4), Tag = "shortcutbox" };
        var list = new StackPanel();
        void Close()
        {
            if (_shortcutBox != null) _canvasLayer.Children.Remove(_shortcutBox);
            _shortcutBox = null;
            View.Focus();
        }
        List<(string id, string name)> Matches()
        {
            var q = (box.Text ?? "").Trim();
            return SearchableCommands().Where(c => q.Length == 0 || c.name.Contains(q, StringComparison.OrdinalIgnoreCase) || c.id.Contains(q, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(c => c.id).Take(10).ToList();
        }
        void Fill()
        {
            list.Children.Clear();
            foreach (var (id, name) in Matches())
            {
                var cid = id;
                var row = new FlatButton
                {
                    Height = 30,
                    Padding = new Thickness(8, 0),
                    Child = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                        Children = { Icons.Color(id, 18) },
                    },
                }.Init();
                var g = (Grid)row.Child!;
                var label = Ui.Label(name, 12);
                label.Margin = new Thickness(8, 0);
                Grid.SetColumn(label, 1);
                g.Children.Add(label);
                if (ShortcutLabels.TryGetValue(id, out var k))
                {
                    var kl = Ui.Label(k, 11, Ui.Muted);
                    Grid.SetColumn(kl, 2);
                    g.Children.Add(kl);
                }
                row.Click += () => { Close(); RunCommand(cid); };
                list.Children.Add(row);
            }
        }
        box.TextChanged += (_, _) => Fill();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                var first = Matches().FirstOrDefault();
                Close();
                if (first.id != null) RunCommand(first.id);
                e.Handled = true;
            }
        };
        Fill();
        _shortcutBox = new Border
        {
            Width = 320,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 40, 0, 0),
            Background = Ui.White,
            BorderBrush = Ui.Brush("#A9A9A9"),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 4 16 0 #40000000"),
            Padding = new Thickness(6),
            Child = new StackPanel { Spacing = 4, Children = { box, list } },
        };
        _canvasLayer.Children.Add(_shortcutBox);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => box.Focus());
    }

    public bool ShortcutBoxOpen => _shortcutBox != null;

    // ------------------------------------------------------------ updates

    private readonly Avalonia.Controls.Shapes.Ellipse _bellDot = new()
    {
        Width = 8, Height = 8, Fill = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
    };
    private FlatButton? _bell;
    private string? _updateInfo;
    public bool UpdateAvailable => _updateInfo != null;

    /// <summary>Tests turn this off so the app never checks GitHub.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public static string InstallerPath => Path.Combine(AppContext.BaseDirectory, "openshape-installer");

    /// <summary>Ask the bundled installer (OpenShape Client Downloader) whether a newer release exists.</summary>
    public async Task CheckUpdatesAsync()
    {
        if (!CheckForUpdates || !File.Exists(InstallerPath)) return;
        var line = await Task.Run(() =>
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(InstallerPath, "--check") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                if (p == null) return null;
                var o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(30000);
                return p.ExitCode == 0 ? o.Trim() : null;
            }
            catch (Exception)
            {
                return null;
            }
        });
        if (line != null) ApplyUpdateCheck(line);
    }

    /// <summary>Parse the installer's "--check" line and light the bell when an update exists.</summary>
    public void ApplyUpdateCheck(string line)
    {
        if (!line.Contains("update=True")) return;
        var latest = line.Split(' ').FirstOrDefault(x => x.StartsWith("latest="))?[7..];
        _updateInfo = $"OpenShape CAD {latest}을(를) 설치할 수 있습니다.";
        _bellDot.IsVisible = true;
        if (_bell != null) ToolTip.SetTip(_bell, "알림 센터 (업데이트 있음)");
    }

    private void ShowNotifications()
    {
        var p = new CommandPanel("알림 센터", null, null, CancelCommand);
        p.AddText(_updateInfo ?? "새 알림이 없습니다. OpenShape CAD가 최신 버전입니다.");
        var run = new FlatButton { Height = 30, Normal = _updateInfo != null ? Ui.Accent : Ui.Brush("#EDEDED"), Child = Ui.Centered(Ui.Label(_updateInfo != null ? "업데이트" : "업데이트 확인 / 다시 설치", 12, _updateInfo != null ? Brushes.White : Ui.Text)) }.Init();
        run.Click += () =>
        {
            CancelCommand();
            if (File.Exists(InstallerPath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(InstallerPath) { UseShellExecute = false });
        };
        p.AddControl(run);
        CancelCommand();
        _cmd = new CommandSession { Id = "notifications", Panel = p };
        ShowDialog(p);
    }

    private static string ShortcutHelp() =>
        "키보드 단축키 (Fusion 기본값과 같음)\n" +
        "  S  명령 검색 · E 돌출 · Q 누르기/당기기 · F 모깎기 · M 이동/복사 · J 접합 · I 측정 · A 모양 · V 표시/숨기기\n" +
        "  스케치: L 선 · R 직사각형 · C 원 · P 다각형 · D 치수 · T 자르기 · Enter/오른쪽 클릭 선 끝내기 · E 스케치 마치고 돌출\n" +
        "  Ctrl+Z/Y 명령취소/다시 실행 · Ctrl+S 저장 · Ctrl+Shift+S 다른 이름으로 저장 · Ctrl+O 열기 · Ctrl+N 새 디자인\n" +
        "  Ctrl+C/V 본체 복사/붙여넣기 · Ctrl+A 모두 선택 · Delete 삭제 · Esc 취소 · F6 맞춤\n" +
        "  마우스: 휠 줌 · 가운데 버튼 끌기 초점이동 · Shift+가운데 버튼(또는 오른쪽 버튼) 끌기 궤도";
}
