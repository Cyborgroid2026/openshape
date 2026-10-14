using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Path = System.IO.Path;

namespace OpenShape;

/// <summary>Manufacture workspace: setup, 2.5-axis operations, toolpath preview, simulation and G-code.</summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<string, Toolpath> _toolpaths = new();
    private string? _camSelected;
    /// <summary>Operation being created or edited (shown instead of the stored one).</summary>
    private (CamOperation op, Toolpath path, int index)? _camDraft;
    /// <summary>Simulation: (operation id, move index) of the tool.</summary>
    private (string op, int move)? _camSim;

    public CamDoc Cam => _doc.Cam;
    public IReadOnlyDictionary<string, Toolpath> Toolpaths => _toolpaths;

    private void EnterManufacture()
    {
        RefreshManufacture();
        if (CamEngine.Stock(Cam.Setup, _state, _doc.HiddenBodies) == null) SetPrompt("가공할 본체가 없습니다. 디자인 작업공간에서 모델을 만드세요.");
        else if (Cam.Operations.Count == 0) SetPrompt("설정에서 스톡과 원점을 확인한 뒤 2D 작업(페이스, 포켓, 윤곽, 드릴)을 추가하세요.");
    }

    /// <summary>Recompute every toolpath (the model changed) and redraw.</summary>
    private void RefreshManufacture()
    {
        _toolpaths.Clear();
        foreach (var op in Cam.Operations) _toolpaths[op.Id] = CamEngine.Generate(Cam, op, _state, _doc.HiddenBodies);
        RebuildBrowser();
        View.InvalidateVisual();
    }

    private void CommitCam(Action change)
    {
        _undo.Add(_doc.Save());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        change();
        _dirty = true;
        UpdateTitle();
        RefreshManufacture();
    }

    /// <summary>Browser rows of the CAM setup and its operations (manufacture workspace).</summary>
    private void AddCamBrowserRows()
    {
        var stock = CamEngine.Stock(Cam.Setup, _state, _doc.HiddenBodies);
        string size = stock is { } s ? $"{s.hi.X - s.lo.X:0.#}×{s.hi.Y - s.lo.Y:0.#}×{s.hi.Z - s.lo.Z:0.#}" : "본체 없음";
        _browserRows.Children.Add(BrowserRow(0, null, null, null, Icons.Color("cam_setup", 16), $"설정1 ({size})", true, false, null, () => RunCamCommand("cam_setup")));
        for (int i = 0; i < Cam.Operations.Count; i++)
        {
            var op = Cam.Operations[i];
            int index = i;
            var tp = _toolpaths.GetValueOrDefault(op.Id);
            string text = tp?.Error != null ? $"{op.Name} ⚠" : $"{op.Name} (T{op.Tool})";
            var row = BrowserRow(1, null, null, null, Icons.Color("cam_" + op.Type, 16), text, false, _camSelected == op.Id,
                () => { _camSelected = _camSelected == op.Id ? null : op.Id; RebuildBrowser(); View.InvalidateVisual(); },
                () => EditCamOperation(index),
                e => CamOperationMenu(index, e));
            if (tp?.Error != null) ToolTip.SetTip(row, tp.Error);
            _browserRows.Children.Add(row);
        }
    }

    private void CamOperationMenu(int index, PointerPressedEventArgs e)
    {
        var m = new MenuFlyout();
        void Item(string text, Action a)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        Item("편집", () => EditCamOperation(index));
        Item("시뮬레이션", () => { _camSelected = Cam.Operations[index].Id; RunCamCommand("cam_simulate"); });
        Item("삭제", () => CommitCam(() => Cam.Operations.RemoveAt(index)));
        m.ShowAt((Control)e.Source!);
    }

    private void RunCamCommand(string id)
    {
        if (_workspace != "manufacture") SetWorkspace("manufacture");
        CancelCommand();
        switch (id)
        {
            case "cam_setup": ShowCamSetup(); break;
            case "cam_face" or "cam_pocket" or "cam_contour" or "cam_drill":
            {
                string type = id[4..];
                var tool = type switch
                {
                    "drill" => Cam.Tools.FirstOrDefault(t => t.Type == "drill"),
                    "face" => Cam.Tools.FirstOrDefault(t => t.Type == "face") ?? Cam.Tools.FirstOrDefault(t => t.Type == "flat"),
                    _ => Cam.Tools.FirstOrDefault(t => t.Type == "flat"),
                };
                var op = new CamOperation { Type = type, Name = Cam.NextOperationName(type), Tool = tool?.Number ?? 1, Stepover = type == "face" ? 0.7 : 0.45 };
                if (type == "drill")
                {
                    // default to a hole size the model has and a drill matching it
                    var holes = CamEngine.Parts(Cam.Setup, _state, _doc.HiddenBodies).SelectMany(b => b.Solid.Holes()).Select(h => Math.Round(h.r * 2, 3)).Distinct().OrderBy(d => d).ToList();
                    var match = holes.Select(d => (d, t: Cam.Tools.FirstOrDefault(t => t.Type == "drill" && Math.Abs(t.Diameter - d) < 0.05))).FirstOrDefault(x => x.t != null);
                    if (match.t != null) { op.Tool = match.t.Number; op.HoleDiameter = match.d; }
                    else if (holes.Count > 0) op.HoleDiameter = holes[0];
                }
                OpenCamOperation(op, -1);
                break;
            }
            case "cam_simulate": ShowCamSimulation(); break;
            case "cam_post": ShowCamPost(); break;
            case "cam_tools": ShowCamTools(); break;
        }
    }

    private void ShowCamSetup()
    {
        _cmd = new CommandSession { Id = "cam_setup" };
        var s = new CamSetup { Body = Cam.Setup.Body, SideOffset = Cam.Setup.SideOffset, TopOffset = Cam.Setup.TopOffset, Origin = Cam.Setup.Origin, SafeHeight = Cam.Setup.SafeHeight, Retract = Cam.Setup.Retract };
        var p = new CommandPanel("설정", "cam_setup", () => { CommitCam(() => Cam.Setup = s); CancelCommand(); }, CancelCommand);
        var bodies = _state.Bodies.Where(b => b.Solid.Info.Solids > 0).ToList();
        p.AddCombo("모델", new[] { "보이는 모든 본체" }.Concat(bodies.Select(b => BodyName(b.Id))).ToArray(),
            s.Body.Length == 0 ? 0 : bodies.FindIndex(b => b.Id == s.Body) + 1, i => s.Body = i == 0 ? "" : bodies[i - 1].Id);
        p.AddHeader("스톡 (상자)");
        p.AddNumber("옆 여유", s.SideOffset, "mm", v => s.SideOffset = Math.Max(0, v));
        p.AddNumber("위 여유", s.TopOffset, "mm", v => s.TopOffset = Math.Max(0, v));
        p.AddHeader("원점 (WCS)");
        p.AddCombo("원점", CamSetup.Origins.Select(CamSetup.OriginName).ToArray(), Math.Max(0, Array.IndexOf(CamSetup.Origins, s.Origin)), i => s.Origin = CamSetup.Origins[i]);
        p.AddText("Z축은 모델의 위쪽(+Z)입니다. 모델의 바닥이 테이블에 놓입니다.");
        p.AddHeader("높이");
        p.AddNumber("안전 높이 (스톡 위)", s.SafeHeight, "mm", v => s.SafeHeight = Math.Max(1, v));
        p.AddNumber("후퇴 높이", s.Retract, "mm", v => s.Retract = Math.Max(0.2, v));
        _cmd.Panel = p;
        ShowDialog(p);
    }

    private void EditCamOperation(int index)
    {
        if (index < 0 || index >= Cam.Operations.Count) return;
        CancelCommand();
        var json = System.Text.Json.JsonSerializer.Serialize(Cam.Operations[index]);
        OpenCamOperation(System.Text.Json.JsonSerializer.Deserialize<CamOperation>(json)!, index);
    }

    private void OpenCamOperation(CamOperation op, int index)
    {
        _cmd = new CommandSession { Id = "cam_" + op.Type, SelectKind = op.Type is "pocket" or "contour" && op.Pick.Length != 3 ? "camface" : "" };
        _camDraft = (op, CamEngine.Generate(Cam, op, _state, _doc.HiddenBodies), index);
        _cmd.Rebuild = () => BuildCamPanel(op, index);
        if (op.Type is "pocket") SetPrompt("포켓 바닥(위를 향한 평면인 면)을 클릭하세요.");
        else if (op.Type is "contour") SetPrompt("윤곽을 딸 평면인 면(보통 모델의 윗면)을 클릭하세요. 그 면의 바깥 경계를 따라 바닥까지 깎습니다.");
        BuildCamPanel(op, index);
        View.InvalidateVisual();
    }

    private void UpdateCamDraft()
    {
        if (_camDraft is not { } d) return;
        _camDraft = (d.op, CamEngine.Generate(Cam, d.op, _state, _doc.HiddenBodies), d.index);
        _cmd?.Panel?.SetError(_camDraft.Value.path.Error);
        View.InvalidateVisual();
    }

    private void BuildCamPanel(CamOperation op, int index)
    {
        var c = _cmd;
        if (c == null) return;
        var p = new CommandPanel(CamOperation.TypeName(op.Type), "cam_" + op.Type, () =>
        {
            if (_camDraft is { } d && d.path.Error != null) { _cmd?.Panel?.SetError(d.path.Error); return; }
            CommitCam(() =>
            {
                if (index >= 0) Cam.Operations[index] = op;
                else Cam.Operations.Add(op);
            });
            _camSelected = op.Id;
            CancelCommand();
            RebuildBrowser();
        }, CancelCommand);
        void Changed() => UpdateCamDraft();
        var tools = Cam.Tools.Where(t => op.Type == "drill" ? t.Type == "drill" : t.Type != "drill").ToList();
        p.AddHeader("공구");
        p.AddCombo("공구", tools.Select(t => t.Label).ToArray(), Math.Max(0, tools.FindIndex(t => t.Number == op.Tool)), i => { op.Tool = tools[i].Number; Changed(); });
        if (op.Type is "pocket" or "contour")
        {
            p.AddHeader("형상");
            p.AddSelect(op.Type == "pocket" ? "포켓 바닥" : "윤곽 면", op.Pick.Length == 3 ? "1개 면 선택됨" : "선택", c.SelectKind == "camface",
                () => { c.SelectKind = "camface"; BuildCamPanel(op, index); }, () => { op.Pick = Array.Empty<double>(); op.Body = ""; BuildCamPanel(op, index); Changed(); });
        }
        if (op.Type == "drill")
        {
            var sizes = CamEngine.Parts(Cam.Setup, _state, _doc.HiddenBodies).SelectMany(b => b.Solid.Holes()).GroupBy(h => Math.Round(h.r * 2, 3)).OrderBy(g => g.Key).ToList();
            p.AddHeader("구멍");
            if (sizes.Count == 0) p.AddText("모델에 수직인 원통형 구멍이 없습니다.");
            else
                p.AddCombo("구멍 지름", sizes.Select(g => $"⌀{g.Key:0.###} ({g.Count()}개)").ToArray(), Math.Max(0, sizes.FindIndex(g => Math.Abs(g.Key - op.HoleDiameter) < 1e-3)),
                    i => { op.HoleDiameter = sizes[i].Key; Changed(); });
            p.AddNumber("펙 깊이 (0 = 한 번에)", op.Peck, "mm", v => { op.Peck = Math.Max(0, v); Changed(); });
        }
        else
        {
            p.AddHeader("패스");
            p.AddNumber("최대 절삭 깊이", op.Stepdown, "mm", v => { op.Stepdown = Math.Max(0.05, v); Changed(); });
            if (op.Type != "contour") p.AddNumber("스텝오버 (공구 지름 비율)", op.Stepover * 100, "%", v => { op.Stepover = Math.Clamp(v / 100, 0.05, 0.95); Changed(); });
            p.AddNumber("남길 스톡", op.StockToLeave, "mm", v => { op.StockToLeave = Math.Max(0, v); Changed(); });
            if (op.Type == "contour") p.AddNumber("바닥 아래로", op.BottomOffset, "mm", v => { op.BottomOffset = Math.Max(0, v); Changed(); });
            if (op.Type != "face") p.AddCombo("방향", new[] { "하향 절삭 (Climb)", "상향 절삭 (Conventional)" }, op.Climb ? 0 : 1, i => { op.Climb = i == 0; Changed(); });
        }
        if (_camDraft is { } draft && draft.path.Error == null)
        {
            p.AddHeader("결과");
            p.AddInfo("예상 시간", FormatTime(draft.path.Time));
            p.AddInfo("절삭 거리", $"{draft.path.CutLength:#,0} mm");
        }
        c.Panel = p;
        p.SetError(_camDraft?.path.Error is { } err && !(op.Type is "pocket" or "contour" && op.Pick.Length != 3) ? err : null);
        ShowDialog(p);
    }

    private static string FormatTime(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간 {t.Minutes}분" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}분 {t.Seconds}초" : $"{t.Seconds}초";

    /// <summary>Canvas pick of a planar face for the pocket/contour being edited.</summary>
    private void CamPickFace(string body, V3 point)
    {
        if (_camDraft is not { } d) return;
        d.op.Body = body;
        d.op.Pick = new[] { point.X, point.Y, point.Z };
        if (_cmd != null) _cmd.SelectKind = "";
        SetPrompt(null);
        UpdateCamDraft();
        BuildCamPanel(d.op, d.index);
    }

    private void ShowCamSimulation()
    {
        var ops = Cam.Operations.Where(o => _toolpaths.TryGetValue(o.Id, out var t) && t.Moves.Count > 0).ToList();
        _cmd = new CommandSession { Id = "cam_simulate" };
        var p = new CommandPanel("시뮬레이션", "cam_simulate", CancelCommand, CancelCommand, "닫기");
        if (ops.Count == 0)
        {
            p.AddText("시뮬레이션할 공구경로가 없습니다. 먼저 작업을 추가하세요.");
            _cmd.Panel = p;
            ShowDialog(p);
            return;
        }
        // one timeline over all operations
        var steps = ops.SelectMany(o => Enumerable.Range(0, _toolpaths[o.Id].Moves.Count).Select(i => (o.Id, i))).ToList();
        var total = TimeSpan.FromTicks(ops.Sum(o => _toolpaths[o.Id].Time.Ticks));
        p.AddInfo("작업", $"{ops.Count}개");
        p.AddInfo("예상 가공 시간", FormatTime(total));
        p.AddInfo("절삭 거리", $"{ops.Sum(o => _toolpaths[o.Id].CutLength):#,0} mm");
        var where = Ui.Label("", 12, Ui.Text);
        var slider = new Slider { Minimum = 0, Maximum = steps.Count - 1, Value = steps.Count - 1, Tag = "시뮬레이션 위치" };
        void Show(int k)
        {
            k = Math.Clamp(k, 0, steps.Count - 1);
            _camSim = steps[k];
            var op = Cam.Operations.First(o => o.Id == steps[k].Id);
            var m = _toolpaths[op.Id].Moves[steps[k].i];
            var origin = CamOrigin();
            var q = m.P - origin;
            where.Text = $"{op.Name}  X{q.X:0.###} Y{q.Y:0.###} Z{q.Z:0.###}";
            View.InvalidateVisual();
        }
        slider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Show((int)slider.Value); };
        var play = new FlatButton { Height = 30, Normal = Ui.Brush("#EDEDED"), Child = Ui.Centered(Ui.Label("재생", 12)) }.Init();
        play.Click += async () =>
        {
            int stride = Math.Max(1, steps.Count / 300);
            for (int k = 0; k < steps.Count && _cmd?.Id == "cam_simulate"; k += stride)
            {
                slider.Value = k;
                await Task.Delay(15);
            }
            if (_cmd?.Id == "cam_simulate") slider.Value = steps.Count - 1;
        };
        p.AddControl(slider);
        p.AddControl(where);
        p.AddControl(play);
        _cmd.Panel = p;
        ShowDialog(p);
        Show(steps.Count - 1);
    }

    private V3 CamOrigin()
    {
        var stock = CamEngine.Stock(Cam.Setup, _state, _doc.HiddenBodies);
        return stock is { } s ? CamEngine.Origin(Cam.Setup, s.lo, s.hi) : V3.Zero;
    }

    /// <summary>G-code of all operations for the selected controller.</summary>
    public string PostProcess()
    {
        var stock = CamEngine.Stock(Cam.Setup, _state, _doc.HiddenBodies) ?? throw new InvalidOperationException("가공할 본체가 없습니다.");
        var ops = Cam.Operations.Select(o => (o, _toolpaths.TryGetValue(o.Id, out var t) ? t : CamEngine.Generate(Cam, o, _state, _doc.HiddenBodies))).ToList();
        var bad = ops.FirstOrDefault(o => o.Item2.Error != null);
        if (bad.o != null) throw new InvalidOperationException($"{bad.o.Name}: {bad.Item2.Error}");
        if (ops.Count == 0) throw new InvalidOperationException("작업이 없습니다.");
        return CamEngine.Post(Cam, ops, CamEngine.Origin(Cam.Setup, stock.lo, stock.hi), stock.hi.Z + Cam.Setup.SafeHeight,
            _path != null ? Path.GetFileNameWithoutExtension(_path) : "OpenShape");
    }

    private void ShowCamPost()
    {
        _cmd = new CommandSession { Id = "cam_post" };
        var p = new CommandPanel("포스트 처리", "cam_post", null, CancelCommand);
        p.AddCombo("컨트롤러", CamDoc.Posts.Select(CamDoc.PostName).ToArray(), Math.Max(0, Array.IndexOf(CamDoc.Posts, Cam.Post)), i => { Cam.Post = CamDoc.Posts[i]; _dirty = true; UpdateTitle(); });
        p.AddNumber("프로그램 번호", Cam.ProgramNumber, "", v => { Cam.ProgramNumber = (int)Math.Clamp(v, 1, 9999); _dirty = true; });
        p.AddText($"작업 {Cam.Operations.Count}개를 순서대로 내보냅니다. 원점: {CamSetup.OriginName(Cam.Setup.Origin)}. GRBL은 공구를 바꿀 때 M0으로 멈춥니다.");
        var save = new FlatButton { Height = 30, Normal = Ui.Accent, Child = Ui.Centered(Ui.Label("G코드 저장...", 12, Brushes.White)) }.Init();
        save.Click += async () =>
        {
            string code;
            try { code = PostProcess(); }
            catch (InvalidOperationException ex) { p.SetError(ex.Message); return; }
            string ext = Cam.Post switch { "linuxcnc" => "ngc", "fanuc" => "nc", _ => "gcode" };
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "G코드 저장",
                SuggestedFileName = $"{(_path != null ? Path.GetFileNameWithoutExtension(_path) : "program")}.{ext}",
                DefaultExtension = ext,
                FileTypeChoices = new[] { new FilePickerFileType("G-code") { Patterns = new[] { "*." + ext, "*.nc", "*.gcode", "*.ngc", "*.tap" } } },
            });
            if (f?.TryGetLocalPath() is { } path) { File.WriteAllText(path, code); CancelCommand(); }
        };
        p.AddControl(save);
        _cmd.Panel = p;
        ShowDialog(p);
    }

    private void ShowCamTools()
    {
        _cmd = new CommandSession { Id = "cam_tools" };
        var rows = Cam.Tools.Select(t => new CamTool { Number = t.Number, Type = t.Type, Diameter = t.Diameter, Rpm = t.Rpm, Feed = t.Feed, Plunge = t.Plunge }).ToList();
        void Build()
        {
            var p = new CommandPanel("공구 라이브러리", "cam_tools", () => { CommitCam(() => Cam.Tools = rows.OrderBy(t => t.Number).ToList()); CancelCommand(); }, CancelCommand);
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("34,86,52,62,56,24") };
            string[] titles = { "번호", "종류", "지름", "회전수", "이송", "" };
            for (int i = 0; i < titles.Length; i++) { var l = Ui.Label(titles[i], 11, Ui.Muted); Grid.SetColumn(l, i); head.Children.Add(l); }
            p.AddControl(head);
            foreach (var t in rows)
            {
                var tool = t;
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("34,86,52,62,56,24"), Height = 28 };
                TextBox Num(double v, int col, Action<double> set)
                {
                    var box = new TextBox { Text = v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), Height = 26, MinHeight = 0, Padding = new Thickness(3, 2), FontSize = 11, Margin = new Thickness(1, 0) };
                    box.TextChanged += (_, _) => { if (CommandPanel.TryParse(box.Text, out var d) && d > 0) set(d); };
                    Grid.SetColumn(box, col);
                    g.Children.Add(box);
                    return box;
                }
                Num(tool.Number, 0, v => tool.Number = (int)v);
                var type = new ComboBox { ItemsSource = new[] { "평 엔드밀", "드릴", "페이스 밀" }, SelectedIndex = tool.Type switch { "drill" => 1, "face" => 2, _ => 0 }, FontSize = 11, MinHeight = 0, Height = 26 };
                type.SelectionChanged += (_, _) => tool.Type = type.SelectedIndex switch { 1 => "drill", 2 => "face", _ => "flat" };
                Grid.SetColumn(type, 1);
                g.Children.Add(type);
                Num(tool.Diameter, 2, v => tool.Diameter = v);
                Num(tool.Rpm, 3, v => tool.Rpm = v);
                Num(tool.Feed, 4, v => { tool.Feed = v; tool.Plunge = tool.Type == "drill" ? v : v / 3; });
                var del = new FlatButton { Width = 20, Height = 20, Child = Ui.Centered(Icons.Line("close", 10, Ui.Muted)) }.Init();
                del.Click += () => { rows.Remove(tool); Build(); };
                Grid.SetColumn(del, 5);
                g.Children.Add(del);
                p.AddControl(g);
            }
            var add = new FlatButton { Height = 28, Normal = Ui.Brush("#EDEDED"), Child = Ui.Centered(Ui.Label("공구 추가", 12)) }.Init();
            add.Click += () => { rows.Add(new CamTool { Number = rows.Count == 0 ? 1 : rows.Max(t => t.Number) + 1 }); Build(); };
            p.AddControl(add);
            p.AddText("이송은 mm/분입니다. 엔드밀의 플런지 이송은 이송의 1/3로 잡힙니다.");
            _cmd!.Panel = p;
            ShowDialog(p);
        }
        Build();
    }

    // ------------------------------------------------------------ overlay

    private static readonly IPen StockPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 224, 138, 60)), 1.2, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IPen CutPen = new Pen(new SolidColorBrush(Color.FromRgb(30, 110, 230)), 1.3);
    private static readonly IPen CutPenDim = new Pen(new SolidColorBrush(Color.FromArgb(110, 30, 110, 230)), 1);
    private static readonly IPen RapidPen = new Pen(new SolidColorBrush(Color.FromArgb(170, 230, 50, 40)), 1, new DashStyle(new double[] { 3, 3 }, 0));

    private void DrawCamOverlay(DrawingContext ctx)
    {
        if (_workspace != "manufacture") return;
        if (CamEngine.Stock(Cam.Setup, _state, _doc.HiddenBodies) is { } s)
        {
            var (lo, hi) = (s.lo, s.hi);
            var c = new[] { new V3(lo.X, lo.Y, lo.Z), new V3(hi.X, lo.Y, lo.Z), new V3(hi.X, hi.Y, lo.Z), new V3(lo.X, hi.Y, lo.Z), new V3(lo.X, lo.Y, hi.Z), new V3(hi.X, lo.Y, hi.Z), new V3(hi.X, hi.Y, hi.Z), new V3(lo.X, hi.Y, hi.Z) };
            foreach (var (a, b) in new[] { (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7) })
                if (View.Project(c[a]) is { } pa && View.Project(c[b]) is { } pb) ctx.DrawLine(StockPen, pa, pb);
            // work origin triad
            var o = CamEngine.Origin(Cam.Setup, lo, hi);
            double len = (hi - lo).Length * 0.12;
            if (View.Project(o) is { } po)
                foreach (var (axis, col) in new[] { (V3.UnitX, Colors.Red), (V3.UnitY, Colors.Green), (V3.UnitZ, Colors.Blue) })
                    if (View.Project(o + axis * len) is { } pe) ctx.DrawLine(new Pen(new SolidColorBrush(col), 2), po, pe);
        }
        void Path(Toolpath tp, bool strong, int upTo = int.MaxValue)
        {
            V3? prev = null;
            int i = 0;
            foreach (var m in tp.Moves)
            {
                if (i++ > upTo) break;
                if (prev is { } a && View.Project(a) is { } pa && View.Project(m.P) is { } pb)
                    ctx.DrawLine(m.Rapid ? RapidPen : strong ? CutPen : CutPenDim, pa, pb);
                prev = m.P;
            }
        }
        if (_camDraft is { } d) Path(d.path, true);
        else
            foreach (var op in Cam.Operations)
                if (_toolpaths.TryGetValue(op.Id, out var tp))
                {
                    bool sim = _camSim?.op == op.Id;
                    if (_camSim != null && !sim)
                    {
                        // operations before the simulated one are done, later ones not started
                        int at = Cam.Operations.FindIndex(x => x.Id == _camSim.Value.op);
                        if (Cam.Operations.IndexOf(op) > at) continue;
                    }
                    Path(tp, _camSelected == null || _camSelected == op.Id || sim, sim ? _camSim!.Value.move : int.MaxValue);
                }
        // the tool at the simulated position: a cylinder outline of the tool diameter
        if (_camSim is { } cs && Cam.Operations.FirstOrDefault(x => x.Id == cs.op) is { } sop && _toolpaths.TryGetValue(sop.Id, out var stp) && cs.move < stp.Moves.Count)
        {
            var tip = stp.Moves[cs.move].P;
            double r = (Cam.Tool(sop.Tool)?.Diameter ?? 6) / 2;
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(90, 90, 90)), 1.5);
            for (int ring = 0; ring < 2; ring++)
            {
                double z = tip.Z + ring * r * 6;
                Point? last = null;
                for (int k = 0; k <= 24; k++)
                {
                    var q = new V3(tip.X + r * Math.Cos(k * Math.PI / 12), tip.Y + r * Math.Sin(k * Math.PI / 12), z);
                    if (View.Project(q) is { } pq) { if (last is { } l) ctx.DrawLine(pen, l, pq); last = pq; }
                }
            }
            foreach (var a in new[] { 0.0, Math.PI / 2, Math.PI, Math.PI * 1.5 })
                if (View.Project(new V3(tip.X + r * Math.Cos(a), tip.Y + r * Math.Sin(a), tip.Z)) is { } p0 && View.Project(new V3(tip.X + r * Math.Cos(a), tip.Y + r * Math.Sin(a), tip.Z + r * 6)) is { } p1)
                    ctx.DrawLine(pen, p0, p1);
            if (View.Project(tip) is { } pt) ctx.DrawEllipse(Brushes.OrangeRed, null, pt, 3, 3);
        }
    }
}
