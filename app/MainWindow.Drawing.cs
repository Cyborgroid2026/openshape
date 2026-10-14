using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Path = System.IO.Path;

namespace OpenShape;

/// <summary>Workspaces (디자인 / 렌더 / 도면 / 제조) and the drawing workspace.</summary>
public sealed partial class MainWindow
{
    private string _workspace = "design";
    private readonly TextBlock _wsLabel = Ui.Label("디자인", 14, Ui.Text, FontWeight.SemiBold);
    private readonly Grid _workArea = new();
    private readonly Grid _drawingLayer = new() { IsVisible = false };
    private readonly DrawingView _drawingView = new();
    private readonly StackPanel _drawingRows = new();
    private Control? _timelineBar;

    public static readonly (string id, string name)[] Workspaces =
    {
        ("design", "디자인"), ("render", "렌더"), ("drawing", "도면"), ("manufacture", "제조"),
    };

    public string Workspace => _workspace;
    public DrawingView SheetView => _drawingView;

    private MenuFlyout WorkspaceMenu()
    {
        var m = new MenuFlyout();
        foreach (var (id, name) in Workspaces)
        {
            var ws = id;
            var mi = new MenuItem { Header = name, Icon = Icons.Color("ws_" + id, 18), ToggleType = MenuItemToggleType.Radio, IsChecked = id == _workspace };
            mi.Click += (_, _) => SetWorkspace(ws);
            m.Items.Add(mi);
        }
        return m;
    }

    /// <summary>Switch the workspace: its toolbar, and the canvas (3D model or drawing sheet).</summary>
    public void SetWorkspace(string ws)
    {
        if (ws == _workspace) return;
        if (_sk != null) FinishSketch(true);
        CancelCommand();
        CancelDrawingTool();
        LeaveRender();
        _workspace = ws;
        _wsLabel.Text = Workspaces.First(w => w.id == ws).name;
        _tab = ws switch { "render" => "render", "drawing" => "drawing", "manufacture" => "milling", _ => "solid" };
        bool drawing = ws == "drawing";
        _canvasLayer.IsVisible = !drawing;
        _drawingLayer.IsVisible = drawing;
        if (_timelineBar != null) _timelineBar.IsVisible = !drawing;
        SetPrompt(null);
        BuildToolbarGroups();
        if (drawing) EnterDrawing();
        if (ws == "render") EnterRender();
        if (ws == "manufacture") EnterManufacture();
        UpdateItems();
        View.InvalidateVisual();
    }

    // ------------------------------------------------------------ drawing workspace

    public DrawingDoc? CurrentDrawing => _doc.Drawings.FirstOrDefault();

    private Control BuildDrawingLayer()
    {
        _drawingView.Geometry = GeometryOf;
        _drawingView.Clicked += OnSheetClick;
        _drawingView.Hovered += OnSheetHover;
        _drawingView.DoubleClicked += OnSheetDoubleClick;
        _drawingView.DragStarting = SheetDragStart;
        _drawingView.Dragging = SheetDrag;
        _drawingView.DragEnded = SheetDragEnd;
        _drawingView.Overlay = DrawSheetOverlay;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 28, Background = Ui.Bar };
        head.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(8, 0),
            Children = { Icons.Line("collapse", 10, Ui.Muted), Ui.Label("브라우저", 12, Ui.Text) },
        });
        var browser = new Border
        {
            Width = 260,
            Margin = new Thickness(8, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(242, 245, 245, 245)),
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { head, new ScrollViewer { Content = _drawingRows, MaxHeight = 480 } } },
        };
        _drawingLayer.Children.Add(_drawingView);
        _drawingLayer.Children.Add(browser);
        return _drawingLayer;
    }

    private void EnterDrawing()
    {
        if (CurrentDrawing == null)
        {
            // like Fusion's "From Design": a new A3 sheet, then the base view dialog
            _doc.Drawings.Add(new DrawingDoc { Title = new TitleBlock { Title = _path != null ? Path.GetFileNameWithoutExtension(_path) : "제목 없음" } });
            _dirty = true;
            UpdateTitle();
        }
        RefreshDrawing(true);
        if (CurrentDrawing!.Views.Count == 0) RunCommand("dwg_base");
    }

    /// <summary>Re-point the sheet canvas at the document's drawing (after undo/open) and redraw.</summary>
    private void RefreshDrawing(bool fit = false)
    {
        _drawingView.Drawing = CurrentDrawing;
        var d = CurrentDrawing;
        if (d != null)
        {
            if (_drawingView.SelectedView != null && d.View(_drawingView.SelectedView) == null) _drawingView.SelectedView = null;
            if (_drawingView.SelectedDim >= d.Dims.Count) _drawingView.SelectedDim = -1;
            if (_drawingView.SelectedNote >= d.Notes.Count) _drawingView.SelectedNote = -1;
        }
        if (fit) _drawingView.Fit();
        RebuildDrawingBrowser();
        _drawingView.InvalidateVisual();
    }

    private void RebuildDrawingBrowser()
    {
        _drawingRows.Children.Clear();
        if (CurrentDrawing is not { } d) return;
        _drawingRows.Children.Add(BrowserRow(0, null, null, null, Icons.Line("file", 16), $"{d.Name} ({d.Sheet})", true, false, () => RunCommand("dwg_sheet")));
        foreach (var v in d.Views)
        {
            var id = v.Id;
            string text = $"{DrawingViewDef.OrientationName(v.Orientation)} 뷰 ({DrawingDoc.ScaleText(d.ScaleOf(v))})";
            _drawingRows.Children.Add(BrowserRow(1, null, null, null, Icons.Line("lookat", 14), text, false, _drawingView.SelectedView == id, () => SelectSheetItem(id, -1, -1)));
        }
        if (d.Dims.Count > 0) _drawingRows.Children.Add(BrowserRow(1, null, null, null, Icons.Line("axis", 14), $"치수 {d.Dims.Count}개", false, false));
        if (d.Notes.Count > 0) _drawingRows.Children.Add(BrowserRow(1, null, null, null, Icons.Line("file", 14), $"텍스트 {d.Notes.Count}개", false, false));
    }

    private void SelectSheetItem(string? view, int dim, int note)
    {
        _drawingView.SelectedView = view;
        _drawingView.SelectedDim = dim;
        _drawingView.SelectedNote = note;
        RebuildDrawingBrowser();
        _drawingView.InvalidateVisual();
    }

    private readonly Dictionary<string, ViewGeometry> _viewCache = new();

    /// <summary>Hidden-line geometry of a view of the visible bodies (cached per solid set and orientation).</summary>
    public ViewGeometry GeometryOf(DrawingViewDef v)
    {
        var solids = _state.Bodies.Where(b => !_doc.HiddenBodies.Contains(b.Id)).Select(b => b.Solid).ToList();
        var key = v.Orientation + "|" + string.Join(",", solids.Select(s => s.Shape));
        if (_viewCache.TryGetValue(key, out var g)) return g;
        try { g = ViewGeometry.Build(solids, v.Orientation); }
        catch (InvalidOperationException) { g = new ViewGeometry(); }
        if (_viewCache.Count > 64) _viewCache.Clear();
        _viewCache[key] = g;
        return g;
    }

    /// <summary>Apply an undoable drawing change without re-evaluating the model.</summary>
    public void CommitDrawing(Action change)
    {
        _undo.Add(_doc.Save());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        change();
        _dirty = true;
        UpdateTitle();
        RefreshDrawing();
    }

    /// <summary>A scale from the standard series so the model fits about a third of the sheet.</summary>
    private double AutoScale(DrawingDoc d, string orientation)
    {
        var g = GeometryOf(new DrawingViewDef { Orientation = orientation });
        if (g.IsEmpty) return 1;
        var (w, h) = d.Size;
        double size = Math.Max((g.Max.X - g.Min.X) / (w * 0.32), (g.Max.Y - g.Min.Y) / (h * 0.36));
        return DrawingDoc.Scales.FirstOrDefault(s => s * size <= 1, DrawingDoc.Scales[^1]);
    }

    // ------------------------------------------------------------ drawing tools

    /// <summary>Active sheet tool: projected, dim, aligned, dia, text (null = select/move).</summary>
    private string? _sheetTool;
    private readonly List<P2> _sheetPicks = new();
    private string? _sheetPickView;
    private P2? _sheetHover;
    private (P2 c, double r)? _sheetCircle;

    private void CancelDrawingTool()
    {
        _sheetTool = null;
        _sheetPicks.Clear();
        _sheetPickView = null;
        _sheetCircle = null;
        if (_workspace == "drawing") SetPrompt(null);
        _drawingView.InvalidateVisual();
    }

    private void RunDrawingCommand(string id)
    {
        if (CurrentDrawing is not { } d) return;
        CancelDrawingTool();
        switch (id)
        {
            case "dwg_base":
                ShowBaseViewDialog(d);
                break;
            case "dwg_projected":
            {
                var parent = _drawingView.SelectedView ?? d.Views.FirstOrDefault(v => v.Parent == null)?.Id;
                if (parent == null) { SetPrompt("먼저 기준 뷰를 만드세요."); return; }
                _drawingView.SelectedView = parent;
                _sheetTool = "projected";
                SetPrompt("투영 뷰를 놓을 곳을 클릭하세요 (부모 뷰의 위·아래·옆, 대각선은 등각 뷰). Esc로 끝냅니다.");
                break;
            }
            case "dwg_dim":
                _sheetTool = "dim";
                SetPrompt("치수를 줄 두 점(꼭짓점 또는 원 중심)을 클릭한 뒤 치수 위치를 클릭하세요.");
                break;
            case "dwg_aligned":
                _sheetTool = "aligned";
                SetPrompt("두 점을 클릭한 뒤 정렬 치수 위치를 클릭하세요.");
                break;
            case "dwg_dia":
                _sheetTool = "dia";
                SetPrompt("원(구멍)을 클릭한 뒤 지름 치수 위치를 클릭하세요.");
                break;
            case "dwg_text":
                _sheetTool = "text";
                SetPrompt("텍스트를 놓을 곳을 클릭하세요.");
                break;
            case "dwg_centermark":
                CommitDrawing(() => d.CenterMarks = !d.CenterMarks);
                break;
            case "dwg_delete":
                DeleteSheetSelection();
                break;
            case "dwg_sheet":
                ShowSheetDialog(d);
                break;
            case "dwg_pdf" or "dwg_dxf" or "dwg_svg":
                _ = ExportDrawingDialog(id[4..]);
                break;
            case "select":
                break;
        }
        _drawingView.InvalidateVisual();
    }

    private void ShowBaseViewDialog(DrawingDoc d)
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "dwg_base" };
        string orientation = "front";
        double? scale = null;
        bool hidden = true, tangent = true;
        var p = new CommandPanel("기준 뷰", "dwg_base", () =>
        {
            var (w, h) = d.Size;
            double s = scale ?? AutoScale(d, orientation);
            CommitDrawing(() =>
            {
                if (d.Views.Count == 0) d.Scale = s;
                // the first view sits left of centre, leaving room for projected views to the right and above
                var v = new DrawingViewDef
                {
                    Id = d.NextViewId(), Orientation = orientation, HiddenLines = hidden, TangentEdges = tangent,
                    X = d.Views.Count == 0 ? w * 0.3 : w * 0.5, Y = d.Views.Count == 0 ? h * 0.42 : h * 0.5,
                    Scale = d.Views.Count == 0 || Math.Abs(s - d.Scale) < 1e-12 ? null : s,
                };
                d.Views.Add(v);
                _drawingView.SelectedView = v.Id;
            });
            CancelCommand();
            if (d.Views.Count == 1) RunDrawingCommand("dwg_projected");
        }, CancelCommand);
        p.AddCombo("방향", DrawingViewDef.Orientations.Select(DrawingViewDef.OrientationName).ToArray(), 0, i => orientation = DrawingViewDef.Orientations[i]);
        var scales = new[] { "자동" }.Concat(DrawingDoc.Scales.Select(DrawingDoc.ScaleText)).ToArray();
        p.AddCombo("축척", scales, 0, i => scale = i == 0 ? null : DrawingDoc.Scales[i - 1]);
        p.AddCheck("숨은선", hidden, v => hidden = v);
        p.AddCheck("접선 모서리", tangent, v => tangent = v);
        p.AddText("보이는 본체가 모두 뷰에 들어갑니다. 확인한 뒤 투영 뷰를 놓을 곳을 클릭하세요.");
        _cmd.Panel = p;
        ShowDialog(p);
    }

    private void ShowSheetDialog(DrawingDoc d)
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "dwg_sheet" };
        var copy = new DrawingDoc { Sheet = d.Sheet, Scale = d.Scale, FirstAngle = d.FirstAngle, Title = new TitleBlock { Title = d.Title.Title, Number = d.Title.Number, Author = d.Title.Author, Checker = d.Title.Checker, Material = d.Title.Material, Date = d.Title.Date, Company = d.Title.Company } };
        var p = new CommandPanel("시트 설정", "dwg_sheet", () =>
        {
            CommitDrawing(() =>
            {
                d.Sheet = copy.Sheet;
                d.Scale = copy.Scale;
                d.FirstAngle = copy.FirstAngle;
                d.Title = copy.Title;
            });
            _drawingView.Fit();
            CancelCommand();
        }, CancelCommand);
        p.AddCombo("시트 크기", DrawingDoc.Sheets.Select(s => s + " 가로").ToArray(), Math.Max(0, Array.IndexOf(DrawingDoc.Sheets, d.Sheet)), i => copy.Sheet = DrawingDoc.Sheets[i]);
        p.AddCombo("기본 축척", DrawingDoc.Scales.Select(DrawingDoc.ScaleText).ToArray(), Math.Max(0, Array.FindIndex(DrawingDoc.Scales, s => Math.Abs(s - d.Scale) < 1e-12)), i => copy.Scale = DrawingDoc.Scales[i]);
        p.AddCombo("투상법", new[] { "3각법 (KS, ASME)", "1각법 (ISO-E)" }, d.FirstAngle ? 1 : 0, i => copy.FirstAngle = i == 1);
        p.AddHeader("제목 블록");
        TextBox Field(string label, string value, Action<string> set)
        {
            var box = new TextBox { Text = value, Height = 26, MinHeight = 0, Padding = new Thickness(4, 2), FontSize = 12, Tag = label };
            box.TextChanged += (_, _) => set(box.Text ?? "");
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*"), Margin = new Thickness(0, 2) };
            g.Children.Add(Ui.Label(label, 12, Ui.Text));
            Grid.SetColumn(box, 1);
            g.Children.Add(box);
            p.AddControl(g);
            return box;
        }
        Field("제목", copy.Title.Title, v => copy.Title.Title = v);
        Field("도면 번호", copy.Title.Number, v => copy.Title.Number = v);
        Field("작성", copy.Title.Author, v => copy.Title.Author = v);
        Field("검토", copy.Title.Checker, v => copy.Title.Checker = v);
        Field("재료", copy.Title.Material, v => copy.Title.Material = v);
        Field("날짜", copy.Title.Date, v => copy.Title.Date = v);
        Field("회사", copy.Title.Company, v => copy.Title.Company = v);
        _cmd.Panel = p;
        ShowDialog(p);
    }

    private void DeleteSheetSelection()
    {
        if (CurrentDrawing is not { } d) return;
        if (_drawingView.SelectedDim >= 0)
        {
            int i = _drawingView.SelectedDim;
            CommitDrawing(() => d.Dims.RemoveAt(i));
            SelectSheetItem(null, -1, -1);
        }
        else if (_drawingView.SelectedNote >= 0)
        {
            int i = _drawingView.SelectedNote;
            CommitDrawing(() => d.Notes.RemoveAt(i));
            SelectSheetItem(null, -1, -1);
        }
        else if (_drawingView.SelectedView is { } id)
        {
            CommitDrawing(() =>
            {
                // a view takes its projected children and its dimensions with it
                var gone = new HashSet<string> { id };
                bool grown = true;
                while (grown)
                {
                    grown = false;
                    foreach (var v in d.Views.Where(v => v.Parent != null && gone.Contains(v.Parent) && !gone.Contains(v.Id)).ToList()) { gone.Add(v.Id); grown = true; }
                }
                d.Views.RemoveAll(v => gone.Contains(v.Id));
                d.Dims.RemoveAll(m => gone.Contains(m.View));
            });
            SelectSheetItem(null, -1, -1);
        }
    }

    private async Task ExportDrawingDialog(string kind)
    {
        if (CurrentDrawing is not { } d) return;
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = kind switch { "pdf" => "PDF 출력", "dxf" => "DXF 출력", _ => "SVG 출력" },
            SuggestedFileName = $"{(d.Title.Title.Length > 0 ? d.Title.Title : d.Name)}.{kind}",
            DefaultExtension = kind,
            FileTypeChoices = new[] { new FilePickerFileType(kind.ToUpperInvariant()) { Patterns = new[] { "*." + kind } } },
        });
        if (f?.TryGetLocalPath() is not { } path) return;
        if (!path.EndsWith("." + kind, StringComparison.OrdinalIgnoreCase)) path += "." + kind;
        try { ExportDrawing(path); }
        catch (Exception ex) { await Ask("도면 출력", ex.Message, "확인"); }
    }

    /// <summary>Write the drawing as PDF, DXF, SVG or PNG (by extension).</summary>
    public void ExportDrawing(string path)
    {
        var d = CurrentDrawing ?? throw new InvalidOperationException("도면이 없습니다.");
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".dxf": DrawingExport.Dxf(d, GeometryOf, path); break;
            case ".svg": DrawingExport.Svg(d, GeometryOf, path); break;
            case ".png": DrawingExport.Png(d, GeometryOf, path); break;
            default: DrawingExport.Pdf(d, GeometryOf, path); break;
        }
    }

    // ------------------------------------------------------------ sheet input

    private DrawingViewDef? ViewAt(P2 p)
    {
        if (CurrentDrawing is not { } d) return null;
        DrawingViewDef? best = null;
        double bestArea = double.MaxValue;
        foreach (var v in d.Views)
        {
            var (a, b) = DrawingRenderer.Bounds(d, v, GeometryOf(v));
            double area = (b.X - a.X) * (b.Y - a.Y);
            if (p.X >= a.X && p.X <= b.X && p.Y >= a.Y && p.Y <= b.Y && area < bestArea) { best = v; bestArea = area; }
        }
        return best;
    }

    /// <summary>Nearest vertex (polyline end or circle centre) of a view within the snap radius, in model coordinates.</summary>
    private P2? SnapVertex(DrawingViewDef v, P2 sheet)
    {
        var d = CurrentDrawing!;
        var g = GeometryOf(v);
        double tol = 8 / _drawingView.PxPerMm / d.ScaleOf(v);
        var m = DrawingRenderer.ToModel(d, v, g, sheet);
        P2? best = null;
        double bd = tol;
        void Try(P2 q) { double dd = q.Dist(m); if (dd < bd) { bd = dd; best = q; } }
        foreach (var (kind, pts) in g.Lines)
        {
            if (kind >= 3 && !v.HiddenLines) continue;
            Try(pts[0]);
            Try(pts[^1]);
        }
        foreach (var (c, _) in g.Circles) Try(c);
        return best;
    }

    private (P2 c, double r)? CircleAt(DrawingViewDef v, P2 sheet)
    {
        var d = CurrentDrawing!;
        var g = GeometryOf(v);
        double tol = 8 / _drawingView.PxPerMm / d.ScaleOf(v);
        var m = DrawingRenderer.ToModel(d, v, g, sheet);
        return g.Circles.Where(c => Math.Abs(c.c.Dist(m) - c.r) < tol || c.c.Dist(m) < tol).OrderBy(c => Math.Abs(c.c.Dist(m) - c.r)).Select(c => ((P2, double)?)c).FirstOrDefault();
    }

    private int DimAt(P2 p)
    {
        if (CurrentDrawing is not { } d) return -1;
        double tol = 6 / _drawingView.PxPerMm;
        for (int i = d.Dims.Count - 1; i >= 0; i--)
        {
            var m = d.Dims[i];
            if (d.View(m.View) is not { } v) continue;
            var g = GeometryOf(v);
            var place = DrawingRenderer.ToSheet(d, v, g, new P2(m.Place[0], m.Place[1]));
            var a = DrawingRenderer.ToSheet(d, v, g, new P2(m.A[0], m.A[1]));
            var b = m.Kind == "diameter" ? a : DrawingRenderer.ToSheet(d, v, g, new P2(m.B[0], m.B[1]));
            var mid = m.Kind switch
            {
                "horizontal" => new P2((a.X + b.X) / 2, place.Y),
                "vertical" => new P2(place.X, (a.Y + b.Y) / 2),
                "diameter" => place,
                _ => place,
            };
            if (mid.Dist(p) < tol * 2 || place.Dist(p) < tol) return i;
        }
        return -1;
    }

    private int NoteAt(P2 p)
    {
        if (CurrentDrawing is not { } d) return -1;
        for (int i = d.Notes.Count - 1; i >= 0; i--)
        {
            var n = d.Notes[i];
            double w = n.Text.Split('\n').Max(l => l.Length) * n.Size;
            int lines = n.Text.Split('\n').Length;
            if (p.X >= n.X - 1 && p.X <= n.X + w && p.Y <= n.Y + n.Size + 1 && p.Y >= n.Y - (lines - 1) * n.Size * 1.6 - 1) return i;
        }
        return -1;
    }

    private void OnSheetHover(P2 p)
    {
        _sheetHover = p;
        if (_sheetTool != null) _drawingView.InvalidateVisual();
    }

    private void OnSheetClick(P2 p, KeyModifiers mods)
    {
        if (CurrentDrawing is not { } d) return;
        switch (_sheetTool)
        {
            case "projected":
            {
                if (_drawingView.SelectedView is not { } pid || d.View(pid) is not { } parent) { CancelDrawingTool(); return; }
                double dx = p.X - parent.X, dy = p.Y - parent.Y;
                double ang = Math.Atan2(dy, dx) * 180 / Math.PI;
                // 8 sectors: the four sides give orthographic views, the diagonals an isometric one
                int sx = 0, sy = 0;
                double a = (ang + 360) % 360;
                if (a < 22.5 || a >= 337.5) sx = 1;
                else if (a < 67.5) (sx, sy) = (1, 1);
                else if (a < 112.5) sy = 1;
                else if (a < 157.5) (sx, sy) = (-1, 1);
                else if (a < 202.5) sx = -1;
                else if (a < 247.5) (sx, sy) = (-1, -1);
                else if (a < 292.5) sy = -1;
                else (sx, sy) = (1, -1);
                string o = DrawingViewDef.Projected(parent.Orientation, sx, sy, d.FirstAngle);
                // orthographic views stay on the projection line through the parent; isometric ones go where clicked
                bool iso = o == "iso";
                CommitDrawing(() => d.Views.Add(new DrawingViewDef
                {
                    Id = d.NextViewId(), Orientation = o, Parent = parent.Id,
                    X = sx == 0 && !iso ? parent.X : p.X, Y = sy == 0 && !iso ? parent.Y : p.Y,
                    HiddenLines = !iso && parent.HiddenLines, TangentEdges = parent.TangentEdges,
                }));
                _drawingView.SelectedView = pid;
                _drawingView.InvalidateVisual();
                return;
            }
            case "dim" or "aligned":
            {
                var v = _sheetPickView != null ? d.View(_sheetPickView) : ViewAt(p);
                if (v == null) return;
                if (_sheetPicks.Count < 2)
                {
                    if (SnapVertex(v, p) is not { } q) { SetPrompt("꼭짓점이나 원 중심 가까이를 클릭하세요."); return; }
                    if (_sheetPicks.Count == 1 && q.Dist(_sheetPicks[0]) < 1e-9) return;
                    _sheetPickView = v.Id;
                    _sheetPicks.Add(q);
                    SetPrompt(_sheetPicks.Count == 1 ? "두 번째 점을 클릭하세요." : "치수 위치를 클릭하세요.");
                    _drawingView.InvalidateVisual();
                    return;
                }
                var g = GeometryOf(v);
                var place = DrawingRenderer.ToModel(d, v, g, p);
                var (a, b) = (_sheetPicks[0], _sheetPicks[1]);
                string kind = _sheetTool == "aligned" ? "aligned" : DimKind(a, b, place);
                var dim = new DrawingDim { View = v.Id, Kind = kind, A = new[] { a.X, a.Y }, B = new[] { b.X, b.Y }, Place = new[] { place.X, place.Y } };
                CommitDrawing(() => d.Dims.Add(dim));
                _sheetPicks.Clear();
                _sheetPickView = null;
                SetPrompt(_sheetTool == "aligned" ? "두 점을 클릭한 뒤 정렬 치수 위치를 클릭하세요." : "치수를 줄 두 점(꼭짓점 또는 원 중심)을 클릭한 뒤 치수 위치를 클릭하세요.");
                return;
            }
            case "dia":
            {
                var v = _sheetPickView != null ? d.View(_sheetPickView) : ViewAt(p);
                if (v == null) return;
                if (_sheetCircle == null)
                {
                    if (CircleAt(v, p) is not { } c) { SetPrompt("원의 둘레나 중심 가까이를 클릭하세요."); return; }
                    _sheetCircle = c;
                    _sheetPickView = v.Id;
                    SetPrompt("지름 치수 위치를 클릭하세요.");
                    return;
                }
                var circle = _sheetCircle.Value;
                var place = DrawingRenderer.ToModel(d, v, GeometryOf(v), p);
                CommitDrawing(() => d.Dims.Add(new DrawingDim { View = v.Id, Kind = "diameter", A = new[] { circle.c.X, circle.c.Y }, B = new[] { circle.c.X, circle.c.Y }, Radius = circle.r, Place = new[] { place.X, place.Y } }));
                _sheetCircle = null;
                _sheetPickView = null;
                SetPrompt("원(구멍)을 클릭한 뒤 지름 치수 위치를 클릭하세요.");
                return;
            }
            case "text":
                EditNote(-1, p);
                return;
        }
        // selection: dimensions and notes before views
        int di = DimAt(p);
        if (di >= 0) { SelectSheetItem(null, di, -1); return; }
        int ni = NoteAt(p);
        if (ni >= 0) { SelectSheetItem(null, -1, ni); return; }
        SelectSheetItem(ViewAt(p)?.Id, -1, -1);
    }

    /// <summary>Horizontal when the placement is above/below the points, vertical when beside them.</summary>
    public static string DimKind(P2 a, P2 b, P2 place)
    {
        double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
        bool besideX = place.X < x0 || place.X > x1;
        bool besideY = place.Y < y0 || place.Y > y1;
        if (besideY && !besideX) return "horizontal";
        if (besideX && !besideY) return "vertical";
        double ex = Math.Max(x0 - place.X, place.X - x1), ey = Math.Max(y0 - place.Y, place.Y - y1);
        return ey >= ex ? "horizontal" : "vertical";
    }

    private void OnSheetDoubleClick(P2 p)
    {
        int ni = NoteAt(p);
        if (ni >= 0) EditNote(ni, p);
    }

    private void EditNote(int index, P2 at)
    {
        if (CurrentDrawing is not { } d) return;
        CancelCommand();
        _cmd = new CommandSession { Id = "dwg_text" };
        string text = index >= 0 ? d.Notes[index].Text : "";
        double size = index >= 0 ? d.Notes[index].Size : 3.5;
        var p = new CommandPanel("텍스트", "dwg_text", () =>
        {
            if (text.Trim().Length > 0)
                CommitDrawing(() =>
                {
                    if (index >= 0) { d.Notes[index].Text = text; d.Notes[index].Size = size; }
                    else d.Notes.Add(new DrawingNote { X = at.X, Y = at.Y, Text = text, Size = size });
                });
            CancelCommand();
        }, CancelCommand);
        var box = new TextBox { Text = text, AcceptsReturn = true, Height = 70, TextWrapping = TextWrapping.Wrap, FontSize = 12, Tag = "텍스트" };
        box.TextChanged += (_, _) => text = box.Text ?? "";
        p.AddControl(box);
        p.AddCombo("글자 높이", new[] { "2.5 mm", "3.5 mm", "5 mm", "7 mm" }, Math.Max(0, Array.IndexOf(new[] { 2.5, 3.5, 5, 7 }, size)), i => size = new[] { 2.5, 3.5, 5, 7 }[i]);
        _cmd.Panel = p;
        ShowDialog(p);
        box.Focus();
    }

    // dragging views (projected views stay aligned with their parent) and dimension placements
    private string? _dragView;
    private int _dragDim = -1;
    private P2 _dragFrom;
    private string? _dragSnapshot;

    private bool SheetDragStart(P2 p)
    {
        if (_sheetTool != null || CurrentDrawing is not { } d) return false;
        int di = DimAt(p);
        if (di >= 0)
        {
            _dragDim = di;
            SelectSheetItem(null, di, -1);
        }
        else if (ViewAt(p) is { } v)
        {
            _dragView = v.Id;
            SelectSheetItem(v.Id, -1, -1);
        }
        else return false;
        _dragFrom = p;
        _dragSnapshot = _doc.Save();
        return true;
    }

    private void SheetDrag(P2 p)
    {
        if (CurrentDrawing is not { } d) return;
        var delta = p - _dragFrom;
        _dragFrom = p;
        if (_dragDim >= 0 && _dragDim < d.Dims.Count)
        {
            var m = d.Dims[_dragDim];
            if (d.View(m.View) is { } dv)
            {
                double s = d.ScaleOf(dv);
                m.Place = new[] { m.Place[0] + delta.X / s, m.Place[1] + delta.Y / s };
            }
        }
        else if (_dragView != null && d.View(_dragView) is { } v)
        {
            var parent = v.Parent != null ? d.View(v.Parent) : null;
            double mx = delta.X, my = delta.Y;
            if (parent != null && v.Orientation != "iso")
            {
                // stay on the projection line through the parent
                if (Math.Abs(v.X - parent.X) < 1e-6) mx = 0;
                else if (Math.Abs(v.Y - parent.Y) < 1e-6) my = 0;
            }
            MoveViewTree(d, v, mx, my);
        }
        _drawingView.InvalidateVisual();
    }

    private static void MoveViewTree(DrawingDoc d, DrawingViewDef v, double dx, double dy)
    {
        v.X += dx;
        v.Y += dy;
        foreach (var c in d.Views.Where(c => c.Parent == v.Id).ToList()) MoveViewTree(d, c, dx, dy);
    }

    private void SheetDragEnd()
    {
        if (_dragSnapshot != null)
        {
            _undo.Add(_dragSnapshot);
            _redo.Clear();
            _dirty = true;
            UpdateTitle();
        }
        _dragSnapshot = null;
        _dragView = null;
        _dragDim = -1;
        RefreshDrawing();
    }

    private void DrawSheetOverlay(ISheetPainter p)
    {
        if (CurrentDrawing is not { } d || _sheetHover is not { } h) return;
        var mark = new Stroke(0.3, 0xFF0696D7, false, "UI");
        void Dot(P2 q) { double r = 4 / _drawingView.PxPerMm; p.Polyline(Enumerable.Range(0, 13).Select(i => new P2(q.X + r * Math.Cos(i * Math.PI / 6), q.Y + r * Math.Sin(i * Math.PI / 6))).ToList(), mark); }
        var v = _sheetPickView != null ? d.View(_sheetPickView) : ViewAt(h);
        if (v == null) return;
        var g = GeometryOf(v);
        foreach (var q in _sheetPicks) Dot(DrawingRenderer.ToSheet(d, v, g, q));
        if (_sheetTool is "dim" or "aligned" && _sheetPicks.Count < 2 && SnapVertex(v, h) is { } s) Dot(DrawingRenderer.ToSheet(d, v, g, s));
        if (_sheetTool is "dim" or "aligned" && _sheetPicks.Count == 2)
        {
            var place = DrawingRenderer.ToModel(d, v, g, h);
            var (a, b) = (_sheetPicks[0], _sheetPicks[1]);
            DrawingRenderer.DimRender(d, v, g, new DrawingDim { View = v.Id, Kind = _sheetTool == "aligned" ? "aligned" : DimKind(a, b, place), A = new[] { a.X, a.Y }, B = new[] { b.X, b.Y }, Place = new[] { place.X, place.Y } }, p);
        }
        if (_sheetTool == "dia")
        {
            if (_sheetCircle is { } c)
            {
                var place = DrawingRenderer.ToModel(d, v, g, h);
                DrawingRenderer.DimRender(d, v, g, new DrawingDim { View = v.Id, Kind = "diameter", A = new[] { c.c.X, c.c.Y }, Radius = c.r, Place = new[] { place.X, place.Y } }, p);
            }
            else if (CircleAt(v, h) is { } hc)
            {
                var sc = DrawingRenderer.ToSheet(d, v, g, hc.c);
                double r = hc.r * d.ScaleOf(v);
                p.Polyline(Enumerable.Range(0, 49).Select(i => new P2(sc.X + r * Math.Cos(i * Math.PI / 24), sc.Y + r * Math.Sin(i * Math.PI / 24))).ToList(), mark);
            }
        }
        if (_sheetTool == "projected" && _drawingView.SelectedView is { } pid && d.View(pid) is { } parent)
            p.Polyline(new[] { new P2(parent.X, parent.Y), h }, new Stroke(0.25, 0xFF0696D7, true, "UI"));
    }
}
