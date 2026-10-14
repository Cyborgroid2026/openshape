using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Path = System.IO.Path;

namespace OpenShape;

public sealed partial class MainWindow : Window
{
    private static readonly Color DefaultBody = Color.Parse("#D6D2CC");

    private Doc _doc = new();
    private ModelState _state = new();
    private ModelState? _preview;
    private readonly List<string> _undo = new(), _redo = new();
    private string? _path;
    private bool _dirty;

    private readonly HashSet<string> _selBodies = new();
    private readonly HashSet<ProfileRef> _selProfiles = new();
    private List<string> _itemBodies = new();

    public readonly Viewport View = new();
    private readonly Grid _canvasLayer = new();
    private readonly ContentControl _toolbarGroups = new();
    private readonly StackPanel _tabsRow = new() { Orientation = Orientation.Horizontal, Spacing = 34 };
    private readonly StackPanel _browserRows = new();
    private readonly StackPanel _timelineItems = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _dialogHost = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 150, 0) };
    private readonly TextBlock _prompt = new() { FontSize = 12, Foreground = Ui.Text };
    private readonly Border _promptBox;
    private readonly TextBlock _docTitle = new() { FontSize = 13, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, bool> _expanded = new() { ["root"] = true, ["origin"] = false, ["bodies"] = true, ["sketches"] = true };
    private string _tab = "solid";
    private FlatButton? _undoBtn, _redoBtn;

    public MainWindow(string[] args)
    {
        Width = 1500;
        Height = 920;
        MinWidth = 1100;
        MinHeight = 650;
        Background = Ui.Bar;
        var icon = Path.Combine(AppContext.BaseDirectory, "openshape.png");
        if (File.Exists(icon)) Icon = new WindowIcon(icon);

        _promptBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
            IsVisible = false,
            IsHitTestVisible = false,
            Child = _prompt,
        };

        View.DrawOverlay = DrawOverlay;
        View.DragStarting = SketchDragStart;
        View.Dragging = SketchDrag;
        View.DragEnded = SketchDragEnd;
        View.Clicked += OnCanvasClick;
        View.Hovered += OnCanvasHover;
        View.DoubleClicked += OnCanvasDoubleClick;
        View.RightClicked += OnCanvasRightClick;

        var root = new DockPanel();
        var top = new StackPanel { Children = { BuildAppBar(), BuildToolbar() } };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var timeline = BuildTimeline();
        DockPanel.SetDock(timeline, Dock.Bottom);
        root.Children.Add(timeline);

        var cube = new ViewCube(View) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 14, 0) };
        cube.Home += HomeView;
        _canvasLayer.Children.Add(View);
        _canvasLayer.Children.Add(BuildBrowser());
        _canvasLayer.Children.Add(BuildComments());
        _canvasLayer.Children.Add(BuildNavBar());
        _canvasLayer.Children.Add(cube);
        _canvasLayer.Children.Add(_promptBox);
        _canvasLayer.Children.Add(_dialogHost);
        var center = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        _dataPanel.IsVisible = false;
        center.Children.Add(_dataPanel);
        Grid.SetColumn(_canvasLayer, 1);
        center.Children.Add(_canvasLayer);
        root.Children.Add(center);
        Content = root;

        KeyDown += OnKey;
        Closing += OnClosing;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RefreshPreview(); };

        BuildToolbarGroups();
        Rebuild();
        if (args.Length > 0 && File.Exists(args[0]))
            Opened += (_, _) => Dispatcher.UIThread.Post(() => OpenPath(args[0]));
        Opened += (_, _) => DispatcherTimer.RunOnce(() => _ = CheckUpdatesAsync(), TimeSpan.FromSeconds(3));
    }

    // ================================================================ state

    public Doc Document => _doc;
    public ModelState State => _preview ?? _state;
    public bool InSketch => _sk != null;
    public SketchSession? Sketch => _sk;
    public CommandSession? Command => _cmd;
    public IReadOnlyCollection<string> SelectedBodies => _selBodies;
    public IReadOnlyCollection<ProfileRef> SelectedProfiles => _selProfiles;

    /// <summary>Evaluate features with this document's components.</summary>
    private ModelState Eval(IReadOnlyList<Feature> features, int to, ModelState? start = null, int from = 0)
    {
        Evaluator.Components = _doc.Components;
        Evaluator.ParameterValues = Params.Compute(_doc.Parameters);
        CommandPanel.ParameterValues = Evaluator.ParameterValues;
        return Evaluator.Run(features, to, start, from);
    }

    /// <summary>Re-evaluate the timeline up to the marker and refresh every view.</summary>
    public void Rebuild()
    {
        _doc.Marker = Math.Clamp(_doc.Marker, 0, _doc.Features.Count);
        _state = Eval(_doc.Features, _doc.Marker);
        foreach (var b in _state.Bodies)
            if (!_doc.BodyNames.ContainsKey(b.Id))
            {
                int n = 1;
                while (_doc.BodyNames.ContainsValue("본체" + n)) n++;
                _doc.BodyNames[b.Id] = "본체" + n;
            }
        _selBodies.RemoveWhere(id => _state.Find(id) == null);
        _selProfiles.RemoveWhere(r => !_state.SketchProfiles.TryGetValue(r.Sketch, out var pr) || r.Loop >= pr.Count);
        _preview = null;
        UpdateItems();
        RebuildBrowser();
        RebuildTimeline();
        UpdateTitle();
    }

    private void UpdateItems()
    {
        var s = State;
        var items = new List<RenderItem>();
        _itemBodies = new List<string>();
        foreach (var b in s.Bodies)
        {
            if (_doc.HiddenBodies.Contains(b.Id)) continue;
            var col = _doc.BodyColors.TryGetValue(b.Id, out var hex) ? Color.Parse(hex) : DefaultBody;
            uint flags = _selBodies.Contains(b.Id) && _sk == null ? Viewport.Selected : 0;
            var shown = _flatView is { } fv && fv.body == b.Id ? fv.flat : b.Solid;
            items.Add(new RenderItem(_section != null ? SectionOf(shown) : shown, col, flags));
            _itemBodies.Add(b.Id);
        }
        View.Items = items;
        View.Invalidate3D();
    }

    public string BodyName(string id) => _doc.BodyNames.GetValueOrDefault(id, id);

    private void UpdateTitle()
    {
        var name = _path != null ? Path.GetFileNameWithoutExtension(_path) : "제목 없음";
        Title = $"{name}{(_dirty ? "*" : "")} - OpenShape CAD";
        _docTitle.Text = $"{name} v1{(_dirty ? "*" : "")}";
        if (_undoBtn != null) _undoBtn.IsEnabled = _undo.Count > 0;
        if (_redoBtn != null) _redoBtn.IsEnabled = _redo.Count > 0;
    }

    /// <summary>Apply an undoable change to the document.</summary>
    public void Commit(Action change)
    {
        _undo.Add(_doc.Save());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        change();
        _dirty = true;
        Rebuild();
    }

    public void Undo()
    {
        if (_cmd != null) { CancelCommand(); return; }
        if (_sk != null) { if (_sk.History.Count > 0) { _sk.Restore(_sk.History[^1]); _sk.History.RemoveAt(_sk.History.Count - 1); _sk.Selected = -1; SolveSketch(); ShowSketchPalette(); View.InvalidateVisual(); } return; }
        if (_undo.Count == 0) return;
        _redo.Add(_doc.Save());
        _doc = Doc.Load(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        _dirty = true;
        Rebuild();
    }

    public void Redo()
    {
        if (_cmd != null || _sk != null || _redo.Count == 0) return;
        _undo.Add(_doc.Save());
        _doc = Doc.Load(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        _dirty = true;
        Rebuild();
    }

    private void SetPrompt(string? text)
    {
        _prompt.Text = text ?? "";
        _promptBox.IsVisible = !string.IsNullOrEmpty(text);
    }

    // ============================================================ app bar

    private static FlatButton BarButton(Control content, string tip, Action click, double width = 32)
    {
        var b = new FlatButton { Width = width, Height = 30, Child = Ui.Centered(content), HoverBrush = Ui.Brush("#CACACA") }.Init();
        ToolTip.SetTip(b, tip);
        b.Click += click;
        return b;
    }

    private Control BuildAppBar()
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0), Spacing = 2 };
        left.Children.Add(BarButton(Icons.Line("home"), "홈", () => { }));
        var data = BarButton(Icons.Line("datapanel"), "데이터 패널 표시", ToggleDataPanel);
        left.Children.Add(data);
        var file = BarButton(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { Icons.Line("file"), Icons.Line("chevdown", 10) } }, "파일", () => { }, 44);
        FlyoutBase.SetAttachedFlyout(file, FileMenu());
        file.Click += () => FlyoutBase.ShowAttachedFlyout(file);
        left.Children.Add(file);
        left.Children.Add(BarButton(Icons.Line("save"), "저장 (Ctrl+S)", () => _ = Save()));
        _undoBtn = BarButton(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { Icons.Line("undo"), Icons.Line("chevdown", 10) } }, "명령취소 (Ctrl+Z)", Undo, 44);
        _redoBtn = BarButton(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { Icons.Line("redo"), Icons.Line("chevdown", 10) } }, "명령복구 (Ctrl+Y)", Redo, 44);
        left.Children.Add(_undoBtn);
        left.Children.Add(_redoBtn);

        var close = BarButton(Icons.Line("close", 14), "닫기", () => _ = NewDesign(), 28);
        var tab = new Border
        {
            Background = Ui.Bar,
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(14, 0, 4, 0),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,*,Auto"),
                Children = { },
            },
        };
        var tg = (Grid)tab.Child;
        var cubeIcon = Icons.Color("design", 18);
        Grid.SetColumn(cubeIcon, 1);
        _docTitle.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_docTitle, 2);
        Grid.SetColumn(close, 4);
        tg.Children.Add(cubeIcon);
        tg.Children.Add(_docTitle);
        tg.Children.Add(close);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0), Spacing = 6 };
        right.Children.Add(BarButton(Icons.Line("plus"), "새 디자인", () => _ = NewDesign()));
        foreach (var (k, tip) in new[] { ("extension", "익스텐션"), ("clock", "작업 상태") })
        {
            var b = BarButton(Icons.Line(k), tip, () => { });
            b.IsEnabled = false;
            right.Children.Add(b);
        }
        // notification centre: shows when a newer OpenShape release is available
        _bellDot.IsVisible = false;
        var bell = BarButton(new Grid { Children = { Icons.Line("bell"), _bellDot } }, "알림 센터", ShowNotifications);
        _bell = bell;
        right.Children.Add(bell);
        right.Children.Add(BarButton(Icons.Line("help"), "도움말", ShowAbout));
        right.Children.Add(new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Brushes.White,
            Child = Ui.Centered(Ui.Label("나", 12)),
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 38, Children = { left } };
        Grid.SetColumn(tab, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(tab);
        grid.Children.Add(right);
        return new Border { Background = Ui.AppBar, Child = grid };
    }

    private MenuFlyout FileMenu()
    {
        var m = new MenuFlyout();
        void Item(string text, Action a, string? key = null)
        {
            var mi = new MenuItem { Header = text, InputGesture = key != null ? KeyGesture.Parse(key) : null };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        Item("새 디자인", () => _ = NewDesign(), "Ctrl+N");
        Item("열기...", () => _ = OpenFile(), "Ctrl+O");
        m.Items.Add(new Separator());
        Item("저장", () => _ = Save(), "Ctrl+S");
        Item("다른 이름으로 저장...", () => _ = SaveAs());
        m.Items.Add(new Separator());
        Item("내보내기...", () => RunCommand("export"));
        Item("메쉬 삽입...", () => RunCommand("insertmesh"));
        Item("CAD 파일 삽입 (STEP, IGES)...", () => RunCommand("insertcad"));
        m.Items.Add(new Separator());
        Item("끝내기", Close);
        return m;
    }

    // ============================================================ toolbar

    private Control BuildToolbar()
    {
        var ws = new FlatButton
        {
            Width = 112,
            Height = 64,
            Margin = new Thickness(10, 8, 10, 8),
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = Ui.Centered(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { Ui.Label("디자인", 14, Ui.Text, FontWeight.SemiBold), Icons.Line("chevdown", 10) },
            }),
        }.Init();
        ToolTip.SetTip(ws, "작업공간: 디자인");
        var right = new DockPanel();
        DockPanel.SetDock(_tabsRow, Dock.Top);
        _tabsRow.Margin = new Thickness(18, 6, 0, 0);
        right.Children.Add(_tabsRow);
        right.Children.Add(_toolbarGroups);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { ws } };
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return new Border { Background = Ui.Bar, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = g, Height = 92 };
    }

    private void BuildTabs()
    {
        _tabsRow.Children.Clear();
        var tabs = new List<(string id, string label, bool enabled)>
        {
            ("solid", "솔리드", true), ("surface", "곡면", true), ("mesh", "메쉬", true), ("sheet", "판금", true),
            ("plastic", "플라스틱", true), ("manage", "매니지", true), ("utilities", "유틸리티", true),
        };
        if (_sk != null) tabs.Add(("sketch", "스케치", true));
        foreach (var (id, label, enabled) in tabs)
        {
            bool active = _sk != null ? id == "sketch" : id == _tab;
            var text = Ui.Label(label, 13, enabled ? (id == "sketch" ? Ui.Accent : Ui.Text) : Ui.Disabled, active ? FontWeight.Bold : FontWeight.Normal);
            var under = new Border { Height = 3, Background = active ? (id == "sketch" ? Ui.Accent : Ui.Text) : Brushes.Transparent, Margin = new Thickness(-6, 2, -6, 0) };
            var b = new FlatButton { HoverBrush = Brushes.Transparent, Child = new StackPanel { Children = { text, under } } }.Init();
            if (!enabled) ToolTip.SetTip(b, "OpenShape CAD에서는 지원하지 않습니다");
            else
            {
                var tabId = id;
                b.Click += () =>
                {
                    if (tabId == "sketch") return;
                    _tab = tabId;
                    BuildToolbarGroups();
                };
            }
            _tabsRow.Children.Add(b);
        }
    }

    private sealed record Tool(string Id, string Label, bool Enabled = true, string? Shortcut = null);

    private static readonly Dictionary<string, string> ToolNames = new()
    {
        ["sketch"] = "스케치 작성",
        ["extrude"] = "돌출",
        ["revolve"] = "회전",
        ["box"] = "상자",
        ["cylinder"] = "원통",
        ["sphere"] = "구",
        ["torus"] = "원환",
        ["rectpattern"] = "직사각형 패턴",
        ["circpattern"] = "원형 패턴",
        ["mirror"] = "미러",
        ["newcomponent"] = "새 구성요소",
        ["move"] = "이동/복사",
        ["combine"] = "결합",
        ["scale"] = "축척",
        ["delete"] = "삭제",
        ["appearance"] = "색상",
        ["joint"] = "접합",
        ["offsetplane"] = "평면 간격띄우기",
        ["measure"] = "측정",
        ["section"] = "단면 분석",
        ["insertmesh"] = "메쉬 삽입",
        ["insertcad"] = "CAD 파일 삽입 (STEP, IGES)",
        ["fillet"] = "필렛",
        ["chamfer"] = "챔퍼",
        ["shell"] = "쉘",
        ["presspull"] = "밀고 당기기",
        ["loft"] = "로프트",
        ["sweep"] = "스윕",
        ["surfextrude"] = "돌출",
        ["patch"] = "패치",
        ["thicken"] = "두껍게 하기",
        ["flange"] = "플랜지",
        ["edgeflange"] = "모서리 플랜지",
        ["sheetrule"] = "판금 규칙",
        ["isaac_cad"] = "Isaac Sim으로 보내기 (형상)",
        ["isaac_robot"] = "Isaac Sim으로 보내기 (로봇·URDF)",
        ["isaac_auto"] = "저장할 때 자동 동기화",
        ["isaac_status"] = "Isaac Sim 연결 확인",
        ["flatpattern"] = "플랫 패턴 작성",
        ["rib"] = "리브",
        ["boss"] = "보스",
        ["select"] = "선택",
        ["export"] = "내보내기",
        ["parameters"] = "매개변수 관리",
        ["line"] = "선",
        ["rect"] = "2점 직사각형",
        ["crect"] = "중심 직사각형",
        ["circle"] = "중심 지름 원",
        ["arc"] = "3점 호",
        ["polygon"] = "내접 폴리곤",
        ["trim"] = "자르기",
        ["dimension"] = "스케치 치수",
        ["constraint"] = "구속조건",
        ["c_hv"] = "수평/수직",
        ["c_coincident"] = "일치",
        ["c_parallel"] = "평행선",
        ["c_perpendicular"] = "직각",
        ["c_equal"] = "동일",
        ["c_fix"] = "고정/고정해제",
        ["c_midpoint"] = "중간점",
        ["c_concentric"] = "동심",
        ["c_tangent"] = "접선",
        ["c_symmetric"] = "대칭",
        ["finish"] = "스케치 마무리",
        ["hole"] = "홀",
        ["form"] = "자유형 작성",
        ["automodel"] = "자동 모델링",
        ["web"] = "웹",
        ["thread"] = "스레드",
        ["coil"] = "코일",
        ["pipe"] = "파이프",
        ["draft"] = "기울기",
        ["splitbody"] = "본체 분할",
        ["insertcomponent"] = "현재 설계에 삽입",
        ["configmode"] = "디자인 구성",
        ["configtable"] = "구성 테이블",
        ["configrules"] = "구성 규칙",
        ["interference"] = "간섭",
        ["centerofmass"] = "무게 중심",
        ["derive"] = "삽입 파생",
        ["canvas"] = "캔버스",
        ["decal"] = "전사",
        ["surfrevolve"] = "회전",
        ["surfsweep"] = "스윕",
        ["surfloft"] = "로프트",
        ["stitch"] = "스티치",
        ["unstitch"] = "분리",
        ["surftrim"] = "자르기",
        ["extend"] = "연장",
        ["plasticrule"] = "플라스틱 규칙",
        ["snapfit"] = "스냅 맞춤",
        ["lip"] = "립",
        ["rest"] = "레스트",
        ["meshcreate"] = "메쉬 작성",
        ["spline"] = "맞춤점 스플라인",
        ["sketchmirror"] = "미러",
        ["sketchfillet"] = "필렛",
        ["offset"] = "간격띄우기",
        ["c_collinear"] = "동일선상",
        ["c_smooth"] = "부드럽게(G2)",
    };

    private static readonly HashSet<string> Unsupported = new()
    {
        "hole", "form", "automodel", "web", "thread", "coil", "pipe", "draft", "splitbody", "insertcomponent", "configmode", "configtable", "configrules",
        "interference", "centerofmass", "derive", "canvas", "decal", "surfrevolve", "surfsweep", "surfloft", "stitch", "unstitch", "surftrim",
        "extend", "plasticrule", "snapfit", "lip", "rest", "meshcreate", "spline", "sketchmirror", "sketchfillet", "offset", "c_collinear", "c_smooth",
    };

    /// <summary>One toolbar group: a row of big icons with the group name (and ▾ menu) underneath.</summary>
    private Control Group(string title, string[] shown, string[] menu)
    {
        var icons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var id in shown)
        {
            var tid = id;
            var b = new FlatButton { Width = 40, Height = 40, Child = Ui.Centered(Icons.Color(id, 30)) }.Init();
            ToolTip.SetTip(b, Unsupported.Contains(id) ? $"{ToolNames[id]} (지원하지 않음)" : WithKey(id, ToolNames.GetValueOrDefault(id, id)));
            b.Click += () => RunCommand(tid);
            icons.Children.Add(b);
        }
        var label = new FlatButton
        {
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { Ui.Label(title, 12, Ui.Text), Icons.Line("chevdown", 9, Ui.Text) },
            },
            Padding = new Thickness(6, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
        }.Init();
        var flyout = new MenuFlyout();
        foreach (var id in menu)
        {
            if (id == "-") { flyout.Items.Add(new Separator()); continue; }
            var tid = id;
            var mi = new MenuItem
            {
                Header = ToolNames.GetValueOrDefault(id, id),
                Icon = Icons.Color(id, 18),
                InputGesture = ShortcutLabels.TryGetValue(id, out var key) && key.Length == 1 ? new KeyGesture(Enum.Parse<Key>(key)) : null,
            };
            mi.Click += (_, _) => RunCommand(tid);
            flyout.Items.Add(mi);
        }
        FlyoutBase.SetAttachedFlyout(label, flyout);
        label.Click += () => FlyoutBase.ShowAttachedFlyout(label);
        ToolTip.SetTip(label, title);
        return new StackPanel { Margin = new Thickness(8, 0), Children = { icons, label } };
    }

    private static Control Separator() => new Border { Width = 1, Background = Ui.Line, Margin = new Thickness(4, 4, 4, 6) };

    private void BuildToolbarGroups()
    {
        BuildTabs();
        // panel contents follow Fusion's own toolbar definition (TabToolbars.xml): the icons shown in each panel
        // and the order of the panel menus. Commands OpenShape does not have yet keep their place and say so when clicked.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 0, 0) };
        void Add(string title, string[] shown, string[] menu)
        {
            if (row.Children.Count > 0) row.Children.Add(Separator());
            row.Children.Add(Group(title, shown, menu));
        }
        void Common(bool assemble = true)
        {
            if (assemble) Add("조립", new[] { "insertcomponent", "newcomponent", "joint" }, new[] { "insertcomponent", "newcomponent", "-", "joint" });
            Add("구성", new[] { "configmode", "configtable", "configrules" }, new[] { "configmode", "configtable", "configrules" });
            Add("생성", new[] { "offsetplane" }, new[] { "offsetplane" });
            Add("검사", new[] { "measure", "section" }, new[] { "measure", "interference", "section", "centerofmass" });
            Add("삽입", new[] { "derive", "canvas", "insertmesh" }, new[] { "decal", "canvas", "insertmesh", "insertcad", "-", "derive" });
            Add("선택", new[] { "select" }, new[] { "select" });
        }
        if (_sk != null)
        {
            Add("작성", new[] { "line", "rect", "circle", "spline", "sketchmirror", "dimension" },
                new[] { "line", "-", "rect", "crect", "-", "circle", "arc", "polygon", "spline", "-", "sketchmirror", "-", "dimension" });
            Add("수정", new[] { "sketchfillet", "trim", "offset" }, new[] { "sketchfillet", "offset", "trim", "-", "move", "parameters" });
            Add("구속조건", new[] { "constraint", "c_hv", "c_coincident", "c_tangent", "c_equal", "c_parallel", "c_perpendicular", "c_fix", "c_midpoint", "c_concentric", "c_collinear", "c_symmetric", "c_smooth" },
                new[] { "constraint", "-", "c_hv", "c_coincident", "c_tangent", "c_equal", "c_parallel", "c_perpendicular", "c_fix", "c_midpoint", "c_concentric", "c_collinear", "c_symmetric", "c_smooth" });
            Add("구성", new[] { "configtable" }, new[] { "configtable", "configrules" });
            Add("검사", new[] { "measure" }, new[] { "measure", "section" });
            Add("삽입", new[] { "canvas", "insertmesh" }, new[] { "canvas", "insertmesh", "insertcad" });
            Add("선택", new[] { "select" }, new[] { "select" });
            row.Children.Add(Separator());
            var finish = new FlatButton
            {
                Child = new StackPanel
                {
                    Margin = new Thickness(10, 0),
                    Children = { Ui.Centered(Icons.Color("finish", 30)), Ui.Centered(Ui.Label("스케치 마무리", 12, Ui.Text)) },
                },
            }.Init();
            ToolTip.SetTip(finish, "스케치 마무리");
            finish.Click += () => FinishSketch(true);
            row.Children.Add(finish);
            foreach (var c in row.Children.OfType<StackPanel>())
                foreach (var b in c.Children.OfType<StackPanel>().SelectMany(s => s.Children.OfType<FlatButton>()))
                    b.IsSelected = false;
        }
        else if (_tab == "surface")
        {
            Add("작성", new[] { "sketch", "patch", "surfextrude", "surfrevolve", "surfsweep", "form" },
                new[] { "newcomponent", "sketch", "form", "-", "surfextrude", "surfrevolve", "surfsweep", "surfloft", "patch", "-", "loft", "sweep", "-", "rectpattern", "circpattern", "mirror", "thicken" });
            Add("수정", new[] { "stitch", "unstitch", "surftrim", "extend" },
                new[] { "presspull", "fillet", "chamfer", "surftrim", "extend", "stitch", "unstitch", "-", "scale", "splitbody", "move", "delete", "-", "appearance", "parameters" });
            Common();
        }
        else if (_tab == "sheet")
        {
            Add("작성", new[] { "newcomponent", "sketch", "flange", "flatpattern", "extrude" },
                new[] { "newcomponent", "sketch", "-", "flange", "edgeflange", "-", "flatpattern", "extrude" });
            Add("수정", new[] { "sheetrule", "fillet", "chamfer" }, new[] { "sheetrule", "-", "presspull", "fillet", "chamfer", "move", "delete", "-", "appearance", "parameters" });
            Common();
        }
        else if (_tab == "plastic")
        {
            Add("설정", new[] { "plasticrule" }, new[] { "plasticrule" });
            Add("작성", new[] { "sketch", "extrude", "revolve", "hole", "web", "rib", "boss", "snapfit", "lip", "rest", "rectpattern" },
                new[] { "sketch", "-", "extrude", "revolve", "hole", "-", "web", "rib", "boss", "snapfit", "lip", "rest", "-", "rectpattern", "circpattern", "mirror" });
            Add("수정", new[] { "presspull", "fillet", "shell", "combine", "splitbody", "move" },
                new[] { "presspull", "fillet", "chamfer", "shell", "draft", "scale", "combine", "splitbody", "move", "delete", "-", "appearance", "parameters" });
            Common();
        }
        else if (_tab == "mesh")
        {
            Add("작성", new[] { "insertmesh", "meshcreate" }, new[] { "newcomponent", "insertmesh", "meshcreate", "insertcad" });
            Add("수정", new[] { "move", "scale" }, new[] { "move", "scale", "delete" });
            Add("검사", new[] { "measure", "section" }, new[] { "measure", "section" });
            Add("내보내기", new[] { "export" }, new[] { "export" });
            Add("선택", new[] { "select" }, new[] { "select" });
        }
        else if (_tab == "manage")
        {
            Add("매개변수", new[] { "parameters" }, new[] { "parameters" });
            Add("검사", new[] { "measure" }, new[] { "measure" });
        }
        else if (_tab == "utilities")
        {
            Add("만들기", new[] { "export" }, new[] { "export" });
            Add("애드인", new[] { "isaac_cad", "isaac_robot", "isaac_auto", "isaac_status" }, new[] { "isaac_cad", "isaac_robot", "-", "isaac_auto", "isaac_status" });
            Add("유틸리티", new[] { "parameters" }, new[] { "parameters" });
            Add("검사", new[] { "measure", "interference", "section" }, new[] { "measure", "interference", "section" });
            Add("선택", new[] { "select" }, new[] { "select" });
        }
        else
        {
            Add("작성", new[] { "sketch", "extrude", "revolve", "hole", "rectpattern", "form", "automodel" },
                new[] { "sketch", "form", "automodel", "-", "extrude", "revolve", "sweep", "loft", "rib", "web", "hole", "thread", "-",
                        "box", "cylinder", "sphere", "torus", "coil", "pipe", "-", "rectpattern", "circpattern", "mirror", "thicken" });
            Add("수정", new[] { "presspull", "fillet", "shell", "combine", "splitbody", "move" },
                new[] { "presspull", "fillet", "chamfer", "shell", "draft", "scale", "combine", "splitbody", "move", "delete", "-", "appearance", "-", "parameters" });
            Common();
            Add("Isaac Sim", new[] { "isaac_cad", "isaac_robot" }, new[] { "isaac_cad", "isaac_robot", "-", "isaac_auto", "isaac_status" });
        }
        _toolbarGroups.Content = row;
    }

    // ============================================================ browser

    private Control BuildBrowser()
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 28, Background = Ui.Bar };
        head.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(8, 0),
            Children = { Icons.Line("collapse", 10, Ui.Muted), Ui.Label("브라우저", 12, Ui.Text, FontWeight.Normal) },
        });
        var min = Icons.Line("minus", 12, Ui.Muted);
        min.Margin = new Thickness(8, 0);
        Grid.SetColumn(min, 2);
        head.Children.Add(min);
        var body = new ScrollViewer { Content = _browserRows, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return new Border
        {
            Width = 280,
            Margin = new Thickness(8, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(242, 245, 245, 245)),
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { head, body } },
        };
    }

    private Control BrowserRow(int depth, string? expandKey, bool? visible, Action? toggleVisible, Control icon, string text, bool bold, bool selected,
        Action? click = null, Action? doubleClick = null, Action<PointerPressedEventArgs>? menu = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 26, Margin = new Thickness(6 + depth * 18, 0, 0, 0) };
        if (expandKey != null)
        {
            bool open = _expanded.GetValueOrDefault(expandKey);
            var chev = new FlatButton { Width = 18, Height = 22, HoverBrush = Brushes.Transparent, Child = Ui.Centered(Icons.Line(open ? "chevdown" : "chevright", 12, Ui.Text)) }.Init();
            chev.Click += () => { _expanded[expandKey] = !open; RebuildBrowser(); };
            row.Children.Add(chev);
        }
        else row.Children.Add(new Border { Width = 18 });
        if (visible is { } v)
        {
            var eye = new FlatButton { Width = 24, Height = 22, HoverBrush = Brushes.Transparent, Child = Ui.Centered(Icons.Line(v ? "eye" : "eyeoff", 16, v ? Ui.Text : Ui.Muted)) }.Init();
            ToolTip.SetTip(eye, v ? "숨기기" : "표시");
            eye.Click += () => toggleVisible?.Invoke();
            row.Children.Add(eye);
        }
        else row.Children.Add(new Border { Width = 24 });
        var label = new FlatButton
        {
            Padding = new Thickness(4, 0, 10, 0),
            Normal = selected ? Ui.AccentSoft : Ui.Row,
            HoverBrush = Ui.Brush("#E0E0E0"),
            Margin = new Thickness(2, 1),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, Ui.Label(text, 13, Ui.Text, bold ? FontWeight.SemiBold : FontWeight.Normal) } },
        }.Init();
        if (click != null) label.Click += click;
        if (doubleClick != null) label.DoubleClick += doubleClick;
        if (menu != null) label.RightClick += menu;
        row.Children.Add(label);
        return row;
    }

    public void RebuildBrowser()
    {
        _browserRows.Children.Clear();
        var name = _path != null ? Path.GetFileNameWithoutExtension(_path) : "(제목 없음)";
        _browserRows.Children.Add(BrowserRow(0, "root", true, null, Icons.Line("component", 16), name, true, false));
        if (!_expanded["root"]) return;
        _browserRows.Children.Add(BrowserRow(1, "settings", null, null, Icons.Line("gear", 16), "문서 설정", false, false));
        if (_expanded.GetValueOrDefault("settings"))
            _browserRows.Children.Add(BrowserRow(2, null, null, null, Icons.Line("axis", 14), "단위: mm", false, false));
        _browserRows.Children.Add(BrowserRow(1, "views", null, null, Icons.Line("folder", 16), "명명된 뷰", false, false));
        if (_expanded.GetValueOrDefault("views"))
            foreach (var (label, yaw, pitch) in new[] { ("위", -90.0, 89.9), ("앞", -90.0, 0.0), ("오른쪽", 0.0, 0.0), ("홈", -55.0, 28.0) })
            {
                var (y, p) = (yaw, pitch);
                _browserRows.Children.Add(BrowserRow(2, null, null, null, Icons.Line("lookat", 14), label, false, false, () => View.SetView(y, p)));
            }
        _browserRows.Children.Add(BrowserRow(1, "origin", _doc.OriginVisible, () => { _doc.OriginVisible = !_doc.OriginVisible; RebuildBrowser(); View.InvalidateVisual(); },
            Icons.Line("folder", 16), "원점", false, false));
        if (_expanded["origin"])
        {
            foreach (var pl in new[] { "XY", "XZ", "YZ" })
            {
                var plane = pl;
                _browserRows.Children.Add(BrowserRow(2, null, null, null, Icons.Line("plane", 14), plane, false, _hoverPlane == plane, () =>
                {
                    if (_pickPlane) EnterSketch(plane, -1, null);
                }));
            }
            foreach (var ax in new[] { "X", "Y", "Z" })
                _browserRows.Children.Add(BrowserRow(2, null, null, null, Icons.Line("axis", 14), ax, false, false));
        }
        foreach (var comp in _doc.Components)
        {
            var c = comp;
            var cb = State.Bodies.Where(b => c.Bodies.Contains(b.Id)).ToList();
            if (cb.Count == 0) continue;
            string key = "comp:" + c.Id;
            bool vis = cb.Any(b => !_doc.HiddenBodies.Contains(b.Id));
            _browserRows.Children.Add(BrowserRow(1, key, vis, () =>
            {
                foreach (var b in cb) { if (vis) _doc.HiddenBodies.Add(b.Id); else _doc.HiddenBodies.Remove(b.Id); }
                RebuildBrowser();
                UpdateItems();
            }, Icons.Line("component", 16), c.Name + ":1" + (c.Grounded ? " (고정)" : ""), false, cb.All(b => _selBodies.Contains(b.Id)),
                () => { _selBodies.Clear(); foreach (var b in cb) _selBodies.Add(b.Id); SelectionChanged(); }, null, e => ComponentMenu(c, e)));
            if (_expanded.GetValueOrDefault(key))
                foreach (var b in cb)
                {
                    var id = b.Id;
                    bool bv = !_doc.HiddenBodies.Contains(id);
                    _browserRows.Children.Add(BrowserRow(2, null, bv, () =>
                    {
                        if (!_doc.HiddenBodies.Remove(id)) _doc.HiddenBodies.Add(id);
                        RebuildBrowser();
                        UpdateItems();
                    }, Icons.Line("body", 16), BodyName(id), false, _selBodies.Contains(id), () => SelectBody(id, false), () => RenameBody(id), e => BodyMenu(id, e)));
                }
        }
        var inComponents = _doc.Components.SelectMany(c => c.Bodies).ToHashSet();
        var bodies = State.Bodies.Where(b => !inComponents.Contains(b.Id)).ToList();
        bool anyVisible = bodies.Any(b => !_doc.HiddenBodies.Contains(b.Id));
        _browserRows.Children.Add(BrowserRow(1, "bodies", bodies.Count == 0 ? null : anyVisible, () =>
        {
            if (anyVisible) foreach (var b in bodies) _doc.HiddenBodies.Add(b.Id);
            else _doc.HiddenBodies.Clear();
            RebuildBrowser();
            UpdateItems();
        }, Icons.Line("folder", 16), "본체", false, false));
        if (_expanded["bodies"])
            foreach (var b in bodies)
            {
                var id = b.Id;
                bool vis = !_doc.HiddenBodies.Contains(id);
                _browserRows.Children.Add(BrowserRow(2, null, vis, () =>
                {
                    if (!_doc.HiddenBodies.Remove(id)) _doc.HiddenBodies.Add(id);
                    RebuildBrowser();
                    UpdateItems();
                }, Icons.Line("body", 16), BodyName(id), false, _selBodies.Contains(id), () => SelectBody(id, false), () => RenameBody(id), e => BodyMenu(id, e)));
            }
        if (State.Joints.Count > 0)
        {
            _browserRows.Children.Add(BrowserRow(1, "joints", null, null, Icons.Line("folder", 16), "접합", false, false));
            if (_expanded.GetValueOrDefault("joints", true))
                foreach (var j in State.Joints)
                {
                    var jointName = j.Name;
                    int index = _doc.Features.FindIndex(f => f.Name == jointName && f is JointFeature);
                    _browserRows.Children.Add(BrowserRow(2, null, null, null, Icons.Color("joint", 16), $"{jointName} ({JointFeature.TypeName(j.Type)})", false, false, null, () => EditFeature(index)));
                }
        }
        var sketches = State.Sketches;
        _browserRows.Children.Add(BrowserRow(1, "sketches", sketches.Count == 0 ? null : sketches.Any(s => s.Visible), () =>
        {
            bool any = sketches.Any(s => s.Visible);
            foreach (var s in _doc.Features.OfType<SketchFeature>()) s.Visible = !any;
            RebuildBrowser();
            View.InvalidateVisual();
        }, Icons.Line("folder", 16), "스케치", false, false));
        if (_expanded["sketches"])
            foreach (var s in sketches)
            {
                var sk = s;
                int index = _doc.Features.IndexOf(sk);
                _browserRows.Children.Add(BrowserRow(2, null, sk.Visible, () => { sk.Visible = !sk.Visible; RebuildBrowser(); View.InvalidateVisual(); },
                    Icons.Line("sketchnode", 16), sk.Name, false, false, null, () => EditFeature(index)));
            }
    }

    private void SelectBody(string id, bool add)
    {
        if (_cmd != null)
        {
            CommandPickBody(id);
            return;
        }
        if (!add) { _selBodies.Clear(); _selProfiles.Clear(); }
        if (!_selBodies.Remove(id)) _selBodies.Add(id);
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        UpdateItems();
        RebuildBrowser();
        View.InvalidateVisual();
        if (_cmd?.Id is "measure" or "appearance") _cmd.Rebuild?.Invoke();
    }

    private void RenameBody(string id)
    {
        var box = new TextBox { Text = BodyName(id), Width = 180 };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 6, Children = { Ui.Label("본체 이름"), box } } };
        flyout.Closed += (_, _) =>
        {
            var t = (box.Text ?? "").Trim();
            if (t.Length > 0 && t != BodyName(id)) Commit(() => _doc.BodyNames[id] = t);
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) flyout.Hide(); };
        flyout.ShowAt(_browserRows);
        box.Focus();
    }

    private void ComponentMenu(ComponentDef c, PointerPressedEventArgs e)
    {
        var m = new MenuFlyout();
        void Item(string text, Action a)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        Item(c.Grounded ? "고정 해제" : "고정", () => Commit(() => _doc.Components.First(x => x.Id == c.Id).Grounded = !c.Grounded));
        Item("이름 바꾸기", () =>
        {
            var box = new TextBox { Text = c.Name, Width = 180 };
            var flyout = new Flyout { Content = new StackPanel { Spacing = 6, Children = { Ui.Label("구성요소 이름"), box } } };
            flyout.Closed += (_, _) =>
            {
                var t = (box.Text ?? "").Trim();
                if (t.Length > 0 && t != c.Name) Commit(() => _doc.Components.First(x => x.Id == c.Id).Name = t);
            };
            flyout.ShowAt(_browserRows);
            box.Focus();
        });
        Item("구성요소 해제", () => Commit(() => _doc.Components.RemoveAll(x => x.Id == c.Id)));
        m.ShowAt((Control)e.Source!);
    }

    private void BodyMenu(string id, PointerPressedEventArgs e)
    {
        var m = new MenuFlyout();
        void Item(string text, Action a)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        Item("이름 바꾸기", () => RenameBody(id));
        Item("모양...", () => { _selBodies.Clear(); _selBodies.Add(id); RunCommand("appearance"); });
        Item("이동/복사", () => { _selBodies.Clear(); _selBodies.Add(id); RunCommand("move"); });
        Item("내보내기...", () => { _selBodies.Clear(); _selBodies.Add(id); RunCommand("export"); });
        Item("제거", () => { _selBodies.Clear(); _selBodies.Add(id); RunCommand("delete"); });
        m.ShowAt((Control)e.Source!);
    }

    // ============================================================ timeline

    private Control BuildTimeline()
    {
        var play = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var (k, tip, act) in new (string, string, Action)[]
        {
            ("first", "처음으로", () => MoveMarker(0)),
            ("prev", "이전 단계", () => MoveMarker(_doc.Marker - 1)),
            ("play", "재생", () => _ = PlayTimeline()),
            ("next", "다음 단계", () => MoveMarker(_doc.Marker + 1)),
            ("last", "끝으로", () => MoveMarker(_doc.Features.Count)),
        })
        {
            var b = new FlatButton { Width = 30, Height = 30, Child = Ui.Centered(Icons.Line(k, 16)) }.Init();
            ToolTip.SetTip(b, tip);
            b.Click += act;
            play.Children.Add(b);
        }
        var scroll = new ScrollViewer { Content = _timelineItems, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { play } };
        Grid.SetColumn(scroll, 1);
        g.Children.Add(scroll);
        var gear = Icons.Line("gear", 18);
        gear.Margin = new Thickness(12, 0);
        Grid.SetColumn(gear, 2);
        g.Children.Add(gear);
        return new Border { Height = 46, Background = Ui.Bar, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = g };
    }

    private async Task PlayTimeline()
    {
        for (int i = 0; i <= _doc.Features.Count; i++)
        {
            _doc.Marker = i;
            Rebuild();
            await Task.Delay(250);
        }
    }

    public void MoveMarker(int m)
    {
        if (_cmd != null || _sk != null) return;
        m = Math.Clamp(m, 0, _doc.Features.Count);
        if (m == _doc.Marker) return;
        _doc.Marker = m;
        _dirty = true;
        Rebuild();
    }

    private Control MarkerHandle() => new Grid
    {
        Width = 10,
        Height = 38,
        Margin = new Thickness(2, 0),
        Children =
        {
            new Border { Width = 2, Background = Ui.Brush("#2B2B2B"), HorizontalAlignment = HorizontalAlignment.Center },
            new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M0,0 H10 V5 L5,10 L0,5 Z"), Fill = Ui.Brush("#2B2B2B"), VerticalAlignment = VerticalAlignment.Top },
        },
    };

    private void RebuildTimeline()
    {
        _timelineItems.Children.Clear();
        for (int i = 0; i < _doc.Features.Count; i++)
        {
            if (i == _doc.Marker) _timelineItems.Children.Add(MarkerHandle());
            var f = _doc.Features[i];
            int index = i;
            var b = new FlatButton
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(1, 0),
                Normal = f.Error != null ? Ui.Brush("#F8C9C5") : Brushes.Transparent,
                Child = Ui.Centered(Icons.Color(f.Command, 22)),
                Opacity = i < _doc.Marker ? 1 : 0.4,
            }.Init();
            if (_cmd?.EditIndex == i || _sk?.EditIndex == i) b.IsSelected = true;
            ToolTip.SetTip(b, f.Error != null ? $"{f.Name}\n오류: {f.Error}" : f.Name);
            b.DoubleClick += () => EditFeature(index);
            b.RightClick += e => TimelineMenu(index, e);
            _timelineItems.Children.Add(b);
        }
        if (_doc.Marker >= _doc.Features.Count) _timelineItems.Children.Add(MarkerHandle());
    }

    private void TimelineMenu(int index, PointerPressedEventArgs e)
    {
        var m = new MenuFlyout();
        void Item(string text, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        Item("피쳐 편집", () => EditFeature(index));
        Item("여기까지 롤백", () => MoveMarker(index + 1));
        Item("이름 바꾸기", () => RenameFeature(index));
        m.Items.Add(new Separator());
        Item("삭제", () => DeleteFeature(index));
        m.ShowAt((Control)e.Source!);
    }

    private void RenameFeature(int index)
    {
        var f = _doc.Features[index];
        var box = new TextBox { Text = f.Name, Width = 180 };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 6, Children = { Ui.Label("피쳐 이름"), box } } };
        flyout.Closed += (_, _) =>
        {
            var t = (box.Text ?? "").Trim();
            if (t.Length > 0 && t != f.Name) Commit(() => _doc.Features[index].Name = t);
        };
        flyout.ShowAt(_timelineItems);
        box.Focus();
    }

    public void DeleteFeature(int index)
    {
        if (index < 0 || index >= _doc.Features.Count) return;
        Commit(() =>
        {
            _doc.Features.RemoveAt(index);
            if (_doc.Marker > index) _doc.Marker--;
        });
    }

    public void EditFeature(int index)
    {
        if (index < 0 || index >= _doc.Features.Count || _cmd != null) return;
        if (_sk != null) return;
        var f = _doc.Features[index];
        if (f is SketchFeature sk) EnterSketch(sk.Plane, index, null);
        else OpenCommand(f.Copy(), index);
    }

    // ============================================================ nav bar / comments

    private Control BuildNavBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2) };
        FlatButton Nav(string icon, string tip, Action a, bool chevron = false)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Children = { Icons.Line(icon, 18) } };
            if (chevron) sp.Children.Add(Icons.Line("chevdown", 9, Ui.Brush("#4A4A4A")));
            var b = new FlatButton { Height = 30, Padding = new Thickness(6, 0), Child = Ui.Centered(sp) }.Init();
            ToolTip.SetTip(b, tip);
            b.Click += a;
            return b;
        }
        FlatButton? orbit = null, pan = null, zoom = null;
        void Mode(string m)
        {
            View.NavMode = View.NavMode == m ? "" : m;
            orbit!.IsSelected = View.NavMode == "orbit";
            pan!.IsSelected = View.NavMode == "pan";
            zoom!.IsSelected = View.NavMode == "zoom";
        }
        orbit = Nav("orbit", "자유 회전 (Shift+가운데 버튼 끌기)", () => Mode("orbit"), true);
        var look = Nav("lookat", "보기", LookAt);
        pan = Nav("pan", "초점이동 (가운데 버튼 끌기)", () => Mode("pan"));
        zoom = Nav("zoom", "줌 (휠)", () => Mode("zoom"));
        var fit = Nav("fit", "맞춤", FitAll, true);
        var display = Nav("display", "화면표시 설정", () => { }, true);
        var dm = new MenuFlyout();
        var edges = new MenuItem { Header = "가시적 모서리로만 음영처리", ToggleType = MenuItemToggleType.Radio, IsChecked = true };
        var shaded = new MenuItem { Header = "음영처리", ToggleType = MenuItemToggleType.Radio };
        edges.Click += (_, _) => { View.ShowEdges = true; edges.IsChecked = true; shaded.IsChecked = false; View.Invalidate3D(); };
        shaded.Click += (_, _) => { View.ShowEdges = false; shaded.IsChecked = true; edges.IsChecked = false; View.Invalidate3D(); };
        dm.Items.Add(edges);
        dm.Items.Add(shaded);
        FlyoutBase.SetAttachedFlyout(display, dm);
        display.Click += () => FlyoutBase.ShowAttachedFlyout(display);
        var grid = Nav("grid", "그리드 및 스냅", () => { }, true);
        var gm = new MenuFlyout();
        var layout = new MenuItem { Header = "배치 그리드", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
        layout.Click += (_, _) => { View.ShowGrid = !View.ShowGrid; layout.IsChecked = View.ShowGrid; View.Invalidate3D(); };
        gm.Items.Add(layout);
        FlyoutBase.SetAttachedFlyout(grid, gm);
        grid.Click += () => FlyoutBase.ShowAttachedFlyout(grid);
        var vps = Nav("viewports", "뷰포트", () => { }, true);
        vps.IsEnabled = false;
        foreach (var c in new Control[] { orbit, look, pan, zoom, fit, display, grid, vps }) row.Children.Add(c);
        return new Border
        {
            Background = Ui.Bar,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1, 1, 1, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = row,
        };
    }

    private Control BuildComments()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 30 };
        g.Children.Add(Ui.Label("주석", 12, Ui.Text));
        var plus = Icons.Line("plus", 14, Ui.Muted);
        Grid.SetColumn(plus, 1);
        g.Children.Add(plus);
        return new Border
        {
            Width = 280,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(10, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Ui.Bar,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1, 1, 1, 0),
            Child = g,
        };
    }

    private void HomeView()
    {
        View.SetView(-55, 28);
        FitAll();
    }

    public void FitAll()
    {
        var lo = new V3(double.MaxValue, double.MaxValue, double.MaxValue);
        var hi = -lo;
        bool any = false;
        foreach (var b in State.Bodies.Where(b => !_doc.HiddenBodies.Contains(b.Id)))
        {
            var i = b.Solid.Info;
            lo = new V3(Math.Min(lo.X, i.Min.X), Math.Min(lo.Y, i.Min.Y), Math.Min(lo.Z, i.Min.Z));
            hi = new V3(Math.Max(hi.X, i.Max.X), Math.Max(hi.Y, i.Max.Y), Math.Max(hi.Z, i.Max.Z));
            any = true;
        }
        foreach (var s in State.Sketches.Where(s => s.Visible))
            foreach (var e in s.Entities)
                foreach (var p in e.Points())
                {
                    var w = s.Frame.At(p);
                    lo = new V3(Math.Min(lo.X, w.X), Math.Min(lo.Y, w.Y), Math.Min(lo.Z, w.Z));
                    hi = new V3(Math.Max(hi.X, w.X), Math.Max(hi.Y, w.Y), Math.Max(hi.Z, w.Z));
                    any = true;
                }
        if (!any) { View.Cam.Target = V3.Zero; View.Cam.Distance = 260; View.Invalidate3D(); return; }
        View.Fit(lo, hi);
    }

    private void LookAt()
    {
        if (_sk != null) LookAtPlane(_sk.Draft);
        else View.SetView(-90, 0);
    }

    private void LookAtPlane(SketchFeature sk)
    {
        switch (sk.Plane)
        {
            case "XZ": View.SetView(-90, 0); break;
            case "YZ": View.SetView(0, 0); break;
            case "XY": View.SetView(-90, 89.9); break;
            default:
            {
                // look straight at the face: the eye sits on its normal
                var n = sk.Frame.N;
                double pitch = Math.Asin(Math.Clamp(n.Z, -1, 1)) * 180 / Math.PI;
                double yaw = Math.Abs(n.Z) > 0.999 ? -90 : Math.Atan2(n.Y, n.X) * 180 / Math.PI;
                View.SetView(yaw, Math.Clamp(pitch, -89.9, 89.9));
                break;
            }
        }
    }

    private void ShowAbout()
    {
        var panel = new CommandPanel("정보", null, () => CloseDialog(), null);
        panel.AddText("OpenShape CAD 1.3.0\n\nAutodesk Fusion의 화면 구성과 사용 흐름을 참고해 C#(Avalonia), Rust, OpenCASCADE(B-rep 형상 엔진), SolveSpace(구속조건 해석기)로 새로 만든 Ubuntu용 파라메트릭 CAD입니다. " +
                      "Autodesk의 코드나 리소스는 포함되어 있지 않으며 Autodesk와 관련이 없습니다. " +
                      "아이콘과 리소스는 모두 OpenShape가 직접 그린 것이며 Wine이나 Windows용 프로그램 없이 Ubuntu에서만 동작합니다.\n\n" +
                      "IsaacSimLink(형상·로봇 보내기, 저장 시 자동 동기화)가 내장되어 있습니다.");
        panel.AddText(ShortcutHelp());
        var upd = new FlatButton { Height = 30, Normal = Ui.Brush("#EDEDED"), Child = Ui.Centered(Ui.Label("업데이트 확인 (OpenShape Client Downloader)", 12)) }.Init();
        upd.Click += () =>
        {
            CloseDialog();
            if (File.Exists(InstallerPath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(InstallerPath) { UseShellExecute = false });
        };
        panel.AddControl(upd);
        ShowDialog(panel);
    }
}
