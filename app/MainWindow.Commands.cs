using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Path = System.IO.Path;

namespace OpenShape;

public sealed partial class MainWindow
{
    private CommandSession? _cmd;
    private readonly DispatcherTimer _previewTimer;

    private static readonly string[] OpNames = { "접합", "잘라내기", "교차", "새 본체" };
    private static readonly BodyOp[] OpOrder = { BodyOp.Join, BodyOp.Cut, BodyOp.Intersect, BodyOp.NewBody };

    private static IEnumerable<(string name, string hex)> Appearances => Doc.AppearanceLibrary.Select(a => (a.name, a.hex));

    // ------------------------------------------------------------ dialogs

    private void ShowDialog(CommandPanel p) => _dialogHost.Content = p;

    private void CloseDialog() => _dialogHost.Content = null;

    // ------------------------------------------------------------ dispatch

    public void RunCommand(string id)
    {
        if (Unsupported.Contains(id))
        {
            SetPrompt($"{ToolNames.GetValueOrDefault(id, id)}: 이 명령은 OpenShape CAD에서 아직 지원하지 않습니다.");
            return;
        }
        if (id.StartsWith("dwg_") && _workspace != "drawing")
        {
            // e.g. picked from the command search in another workspace
            SetWorkspace("drawing");
            if (id == "dwg_base" && _cmd?.Id == "dwg_base") return;
        }
        if (_workspace == "drawing" && (id.StartsWith("dwg_") || id == "select"))
        {
            RunDrawingCommand(id);
            return;
        }
        if (id.StartsWith("cam_"))
        {
            RunCamCommand(id);
            return;
        }
        if (_sk != null)
        {
            switch (id)
            {
                case "trim":
                    _sk.Tool = "trim";
                    _sk.Pts.Clear();
                    _sk.Selected = -1;
                    SetPrompt("잘라낼 곡선 부분을 클릭하세요. 다른 곡선과 만나는 점 사이가 지워집니다.");
                    break;
                case "dimension" or "c_hv" or "c_coincident" or "c_parallel" or "c_perpendicular" or "c_equal" or "c_fix" or "c_midpoint" or "c_concentric" or "c_tangent" or "c_symmetric":
                    _sk.Tool = id;
                    _sk.Pts.Clear();
                    _sk.Picks.Clear();
                    _pickKinds.Clear();
                    _pendingLineDim = null;
                    SetPrompt(id switch
                    {
                        "dimension" => "치수를 줄 선, 원 또는 두 점을 클릭하세요.",
                        "c_hv" => "수평/수직으로 만들 선을 클릭하세요.",
                        "c_fix" => "고정할 점을 클릭하세요.",
                        "c_midpoint" => "점과 선을 차례로 클릭하세요.",
                        "c_coincident" => "두 점(또는 점과 선)을 차례로 클릭하세요.",
                        "c_concentric" => "두 원을 차례로 클릭하세요.",
                        "c_tangent" => "선과 원(또는 두 원)을 차례로 클릭하세요.",
                        "c_symmetric" => "두 점과 대칭선을 차례로 클릭하세요.",
                        _ => "두 개체를 차례로 클릭하세요.",
                    });
                    break;
                case "line" or "rect" or "crect" or "circle" or "arc" or "polygon":
                    _sk.Tool = id;
                    _sk.Pts.Clear();
                    _sk.ChainStart = null;
                    _sk.Selected = -1;
                    SetPrompt(id switch
                    {
                        "line" => "선의 시작점을 클릭하세요. 계속 클릭하면 이어서 그리고, Esc 또는 오른쪽 클릭으로 끝냅니다.",
                        "arc" => "호의 시작점, 끝점, 통과점을 차례로 클릭하세요.",
                        "circle" => "원의 중심을 클릭한 뒤 지름을 정하세요.",
                        "polygon" => "다각형의 중심을 클릭한 뒤 크기를 정하세요.",
                        _ => "직사각형의 첫 번째 점을 클릭하세요.",
                    });
                    ShowSketchPalette();
                    break;
                case "select":
                    _sk.Tool = "";
                    _sk.Pts.Clear();
                    SetPrompt(null);
                    break;
                case "finish":
                    FinishSketch(true);
                    break;
                case "measure":
                    ShowMeasure();
                    break;
            }
            View.InvalidateVisual();
            return;
        }
        switch (id)
        {
            case "sketch":
                StartPlanePick();
                break;
            case "offsetplane":
                StartPlanePick(10);
                SetPrompt("평면 간격띄우기을 만들 기준 평면을 선택하세요. 간격은 스케치 팔레트에서 바꿀 수 있습니다.");
                break;
            case "extrude":
                OpenCommand(new ExtrudeFeature { Name = _doc.NextName("extrude"), Profiles = _selProfiles.ToList(), Operation = _state.Bodies.Count > 0 ? BodyOp.Join : BodyOp.NewBody }, -1);
                break;
            case "revolve":
                OpenCommand(new RevolveFeature { Name = _doc.NextName("revolve"), Profiles = _selProfiles.ToList(), Operation = _state.Bodies.Count > 0 ? BodyOp.Join : BodyOp.NewBody }, -1);
                break;
            case "box" or "cylinder" or "sphere" or "torus":
                OpenCommand(new PrimitiveFeature
                {
                    Name = _doc.NextName(id),
                    Shape = id,
                    A = id == "torus" ? 30 : id is "cylinder" or "sphere" ? 10 : 20,
                    B = id == "torus" ? 5 : 20,
                    C = 20,
                    Operation = BodyOp.NewBody,
                }, -1);
                break;
            case "move":
                OpenCommand(new MoveFeature { Name = _doc.NextName("move"), Bodies = _selBodies.ToList() }, -1);
                break;
            case "mirror":
                OpenCommand(new MirrorFeature { Name = _doc.NextName("mirror"), Bodies = _selBodies.ToList() }, -1);
                break;
            case "rectpattern":
                OpenCommand(new PatternFeature { Name = _doc.NextName("rectpattern"), Bodies = _selBodies.ToList() }, -1);
                break;
            case "circpattern":
                OpenCommand(new PatternFeature { Name = _doc.NextName("circpattern"), Bodies = _selBodies.ToList(), Circular = true }, -1);
                break;
            case "combine":
            {
                var sel = _selBodies.ToList();
                OpenCommand(new CombineFeature { Name = _doc.NextName("combine"), Target = sel.FirstOrDefault() ?? "", Tools = sel.Skip(1).ToList() }, -1);
                break;
            }
            case "newcomponent":
            {
                var ids = _selBodies.ToList();
                if (ids.Count == 0)
                {
                    SetPrompt("구성요소로 만들 본체를 먼저 선택하세요.");
                    break;
                }
                Commit(() =>
                {
                    foreach (var c in _doc.Components) c.Bodies.RemoveAll(ids.Contains);
                    _doc.Components.RemoveAll(c => c.Bodies.Count == 0);
                    int n = 1;
                    while (_doc.Components.Any(c => c.Name == "구성요소" + n)) n++;
                    _doc.Components.Add(new ComponentDef { Name = "구성요소" + n, Bodies = ids, Grounded = _doc.Components.Count == 0 });
                });
                SetPrompt(null);
                break;
            }
            case "joint":
                OpenCommand(new JointFeature { Name = _doc.NextName("joint") }, -1);
                break;
            case "loft":
                OpenCommand(new LoftFeature { Name = _doc.NextName("loft"), Profiles = _selProfiles.ToList(), Operation = _state.Bodies.Count > 0 ? BodyOp.Join : BodyOp.NewBody }, -1);
                break;
            case "sweep":
                OpenCommand(new SweepFeature { Name = _doc.NextName("sweep"), Profiles = _selProfiles.Take(1).ToList(), Operation = _state.Bodies.Count > 0 ? BodyOp.Join : BodyOp.NewBody }, -1);
                break;
            case "surfextrude" or "patch":
                OpenCommand(new SurfaceFeature { Name = _doc.NextName(id), Patch = id == "patch" }, -1);
                break;
            case "thicken":
                OpenCommand(new ThickenFeature { Name = _doc.NextName("thicken"), Bodies = _selBodies.ToList() }, -1);
                break;
            case "presspull":
                OpenCommand(new PressPullFeature { Name = _doc.NextName("presspull") }, -1);
                break;
            case "flange":
                OpenCommand(new FlangeFeature { Name = _doc.NextName("flange"), Profiles = _selProfiles.ToList(), Thickness = _doc.SheetThickness, BendRadius = _doc.SheetBendRadius }, -1);
                break;
            case "edgeflange":
                OpenCommand(new FlangeFeature { Name = _doc.NextName("edgeflange"), Edge = true, Thickness = _doc.SheetThickness, BendRadius = _doc.SheetBendRadius }, -1);
                break;
            case "sheetrule":
                ShowSheetRule();
                break;
            case "rib":
                OpenCommand(new RibFeature { Name = _doc.NextName("rib") }, -1);
                break;
            case "boss":
                OpenCommand(new BossFeature { Name = _doc.NextName("boss") }, -1);
                break;
            case "hole":
                OpenCommand(new HoleFeature { Name = _doc.NextName("hole") }, -1);
                SetPrompt("구멍을 놓을 평면인 면 위의 점을 클릭하세요. 여러 점을 클릭하면 같은 구멍을 여러 개 만듭니다.");
                break;
            case "splitbody":
                OpenCommand(new SplitBodyFeature { Name = _doc.NextName("splitbody"), Bodies = _selBodies.ToList() }, -1);
                break;
            case "draft":
                OpenCommand(new DraftFeature { Name = _doc.NextName("draft") }, -1);
                SetPrompt("고정 평면(당기는 방향의 기준이 되는 평면인 면)을 클릭한 뒤 기울기를 줄 면들을 클릭하세요.");
                break;
            case "coil":
                OpenCommand(new CoilFeature { Name = _doc.NextName("coil"), Operation = BodyOp.NewBody }, -1);
                break;
            case "pipe":
                OpenCommand(new PipeFeature { Name = _doc.NextName("pipe"), Operation = _state.Bodies.Count > 0 ? BodyOp.Join : BodyOp.NewBody }, -1);
                break;
            case "surfrevolve" or "surfsweep" or "surfloft":
                OpenCommand(new SurfaceFeature { Name = _doc.NextName(id), Kind = id[4..] }, -1);
                break;
            case "stitch":
                OpenCommand(new StitchFeature { Name = _doc.NextName("stitch"), Bodies = _selBodies.ToList() }, -1);
                break;
            case "interference":
                ShowInterference();
                break;
            case "centerofmass":
                ShowCenterOfMass();
                break;
            case "section":
                ShowSection();
                break;
            case "parameters":
                ShowParameters();
                break;
            case "flatpattern":
                ShowFlatPattern();
                break;
            case "isaac_cad" or "isaac_robot" or "isaac_status":
                _ = RunIsaac(id);
                break;
            case "isaac_auto":
                IsaacLink.AutoSync = !IsaacLink.AutoSync;
                ShowIsaacResult(IsaacLink.AutoSync
                    ? $"저장할 때 자동으로 Isaac Sim에 보냅니다 (이 디자인: {(IsaacLink.LastMode(DesignName) == "robot" ? "로봇" : "형상")})."
                    : "자동 동기화를 껐습니다.", false);
                break;
            case "fillet" or "chamfer":
                OpenCommand(new FilletFeature { Name = _doc.NextName(id), Chamfer = id == "chamfer", Size = id == "chamfer" ? 1 : 2 }, -1);
                break;
            case "shell":
                OpenCommand(new ShellFeature { Name = _doc.NextName("shell") }, -1);
                break;
            case "scale":
                OpenCommand(new ScaleFeature { Name = _doc.NextName("scale"), Bodies = _selBodies.ToList() }, -1);
                break;
            case "delete":
                if (_selBodies.Count > 0)
                {
                    var ids = _selBodies.ToList();
                    Commit(() =>
                    {
                        _doc.Features.Insert(_doc.Marker, new DeleteFeature { Name = _doc.NextName("delete"), Bodies = ids });
                        _doc.Marker++;
                    });
                }
                else OpenCommand(new DeleteFeature { Name = _doc.NextName("delete") }, -1);
                break;
            case "appearance":
                ShowAppearance();
                break;
            case "physmaterial":
                ShowCenterOfMass();
                break;
            case "scenesettings":
                ShowSceneSettings();
                break;
            case "renderimage":
                ShowRenderImage();
                break;
            case "incanvas":
                View.RayTrace = !View.RayTrace;
                SetPrompt(View.RayTrace ? "캔버스 내 렌더를 켰습니다." : "캔버스 내 렌더를 껐습니다.");
                break;
            case "measure":
                ShowMeasure();
                break;
            case "insertmesh":
                _ = InsertFile(false);
                break;
            case "insertcad":
                _ = InsertFile(true);
                break;
            case "export":
                ShowExport();
                break;
            case "select":
                CancelCommand();
                View.NavMode = "";
                break;
        }
    }

    // ------------------------------------------------------------ parametric commands

    private static List<string>? BodyList(Feature? f) => f switch
    {
        MoveFeature m => m.Bodies,
        MirrorFeature m => m.Bodies,
        PatternFeature p => p.Bodies,
        ScaleFeature s => s.Bodies,
        ThickenFeature t => t.Bodies,
        DeleteFeature d => d.Bodies,
        SplitBodyFeature sp => sp.Bodies,
        StitchFeature st => st.Bodies,
        _ => null,
    };

    private static List<ProfileRef>? ProfileList(Feature? f) => f switch
    {
        ExtrudeFeature e => e.Profiles,
        RevolveFeature r => r.Profiles,
        LoftFeature l => l.Profiles,
        SweepFeature w => w.Profiles,
        FlangeFeature { Edge: false } fl => fl.Profiles,
        _ => null,
    };

    public void OpenCommand(Feature draft, int editIndex)
    {
        CancelCommand();
        _pickPlane = false;
        SetPrompt(null);
        var baseState = Eval(_doc.Features, editIndex >= 0 ? editIndex : _doc.Marker);
        _cmd = new CommandSession { Id = draft.Command, Draft = draft, EditIndex = editIndex, Base = baseState };
        _cmd.SelectKind = draft switch
        {
            ExtrudeFeature or RevolveFeature => "profiles",
            CombineFeature c => string.IsNullOrEmpty(c.Target) ? "target" : "tools",
            FilletFeature => "edges",
            JointFeature => "jointA",
            LoftFeature => "profiles",
            SweepFeature sw => sw.Profiles.Count == 0 ? "profiles" : "path",
            SurfaceFeature or RibFeature => "chains",
            PressPullFeature or BossFeature => "face1",
            FlangeFeature { Edge: true } => "edge1",
            FlangeFeature => "profiles",
            ShellFeature => "faces",
            HoleFeature => "holepts",
            DraftFeature d => d.Neutral.Length == 3 ? "faces" : "neutral",
            SurfaceFeature or PipeFeature => "chains",
            CoilFeature => "",
            PrimitiveFeature => "",
            _ => "bodies",
        };
        if (ProfileList(draft) is { Count: > 0 } && draft is ExtrudeFeature or RevolveFeature or LoftFeature) _cmd.SelectKind = "profiles";
        _cmd.Rebuild = BuildCommandPanel;
        BuildCommandPanel();
        RefreshPreview();
        RebuildTimeline();
    }

    private void BuildCommandPanel()
    {
        var c = _cmd;
        if (c?.Draft is not { } f) return;
        var title = Doc.CommandNames.GetValueOrDefault(f.Command, f.Command);
        var p = new CommandPanel(title, f.Command, OkCommand, CancelCommand);
        void Changed() => SchedulePreview();
        string Count(int n, string what) => n == 0 ? "선택" : $"{n}개 {what} 선택됨";
        FlatButton Select(string label, string kind, int n, string what, Action clear) =>
            p.AddSelect(label, Count(n, what), c.SelectKind == kind, () => { c.SelectKind = kind; BuildCommandPanel(); View.InvalidateVisual(); }, () => { clear(); BuildCommandPanel(); Changed(); });
        int OpIndex(BodyOp op) => Array.IndexOf(OpOrder, op);

        switch (f)
        {
            case ExtrudeFeature ex:
                p.AddInfo("유형", "돌출");
                Select("프로파일", "profiles", ex.Profiles.Count, "프로파일", () => ex.Profiles.Clear());
                p.AddCombo("시작", new[] { "프로파일 평면" }, 0, _ => { });
                p.AddCombo("방향", new[] { "한쪽 방향", "두 방향", "대칭" }, ex.Direction switch { 2 => 1, 1 => 2, _ => 0 },
                    i => { ex.Direction = i switch { 1 => 2, 2 => 1, _ => 0 }; BuildCommandPanel(); Changed(); });
                p.AddCombo("범위 유형", new[] { "거리" }, 0, _ => { });
                p.AddNumber(ex.Direction == 2 ? "거리 - 측면 1" : "거리", ex.Distance, "mm", v => ex.Distance = v, Changed, f, "Distance");
                if (ex.Direction == 2) p.AddNumber("거리 - 측면 2", ex.Distance2, "mm", v => ex.Distance2 = v, Changed, f, "Distance2");
                p.AddCombo("생성", OpNames, OpIndex(ex.Operation), i => { ex.Operation = OpOrder[i]; Changed(); });
                break;

            case RevolveFeature rv:
            {
                Select("프로파일", "profiles", rv.Profiles.Count, "프로파일", () => rv.Profiles.Clear());
                var axes = new List<string> { "스케치 X축", "스케치 Y축" };
                var sketch = rv.Profiles.Count > 0 ? c.Base!.Sketches.FirstOrDefault(s => s.Id == rv.Profiles[0].Sketch) : null;
                if (sketch != null) axes.AddRange(sketch.Entities.Where(e => e.Kind == "line").Select((_, i) => $"스케치 선 {i + 1}"));
                p.AddCombo("축", axes.ToArray(), Math.Min(rv.Axis, axes.Count - 1), i => { rv.Axis = i; Changed(); });
                p.AddCombo("유형", new[] { "각도" }, 0, _ => { });
                p.AddNumber("각도", rv.Angle, "deg", v => rv.Angle = v, Changed, f, "Angle");
                p.AddCombo("생성", OpNames, OpIndex(rv.Operation), i => { rv.Operation = OpOrder[i]; Changed(); });
                break;
            }

            case PrimitiveFeature pr:
                switch (pr.Shape)
                {
                    case "box":
                        p.AddNumber("길이", pr.A, "mm", v => pr.A = v, Changed, f, "A");
                        p.AddNumber("너비", pr.B, "mm", v => pr.B = v, Changed, f, "B");
                        p.AddNumber("높이", pr.C, "mm", v => pr.C = v, Changed, f, "C");
                        break;
                    case "cylinder":
                        p.AddNumber("지름", pr.A * 2, "mm", v => pr.A = v / 2, Changed);
                        p.AddNumber("높이", pr.B, "mm", v => pr.B = v, Changed, f, "B");
                        break;
                    case "sphere":
                        p.AddNumber("지름", pr.A * 2, "mm", v => pr.A = v / 2, Changed);
                        break;
                    case "torus":
                        p.AddNumber("내부 지름", (pr.A - pr.B) * 2, "mm", v => { var minor = pr.B; pr.A = v / 2 + minor; }, Changed);
                        p.AddNumber("원환 지름", pr.B * 2, "mm", v => { var inner = pr.A - pr.B; pr.B = v / 2; pr.A = inner + pr.B; }, Changed);
                        break;
                }
                p.AddHeader("위치");
                p.AddNumber("X", pr.X, "mm", v => pr.X = v, Changed, f, "X");
                p.AddNumber("Y", pr.Y, "mm", v => pr.Y = v, Changed, f, "Y");
                p.AddNumber(pr.Shape == "sphere" || pr.Shape == "torus" ? "Z (중심)" : "Z (바닥)", pr.Z, "mm", v => pr.Z = v, Changed, f, "Z");
                p.AddCombo("생성", OpNames, OpIndex(pr.Operation), i => { pr.Operation = OpOrder[i]; Changed(); });
                break;

            case MoveFeature mv:
                Select("객체", "bodies", mv.Bodies.Count, "본체", () => mv.Bodies.Clear());
                p.AddCombo("이동 유형", new[] { "자유 이동" }, 0, _ => { });
                p.AddNumber("X 거리", mv.Tx, "mm", v => mv.Tx = v, Changed, f, "Tx");
                p.AddNumber("Y 거리", mv.Ty, "mm", v => mv.Ty = v, Changed, f, "Ty");
                p.AddNumber("Z 거리", mv.Tz, "mm", v => mv.Tz = v, Changed, f, "Tz");
                p.AddNumber("X 각도", mv.Rx, "deg", v => mv.Rx = v, Changed, f, "Rx");
                p.AddNumber("Y 각도", mv.Ry, "deg", v => mv.Ry = v, Changed, f, "Ry");
                p.AddNumber("Z 각도", mv.Rz, "deg", v => mv.Rz = v, Changed, f, "Rz");
                p.AddCheck("복사 작성", mv.CreateCopy, v => { mv.CreateCopy = v; Changed(); });
                break;

            case MirrorFeature mi:
                Select("객체", "bodies", mi.Bodies.Count, "본체", () => mi.Bodies.Clear());
                p.AddCombo("대칭 평면", new[] { "XY 평면", "XZ 평면", "YZ 평면" }, mi.Plane switch { "XY" => 0, "XZ" => 1, _ => 2 },
                    i => { mi.Plane = new[] { "XY", "XZ", "YZ" }[i]; Changed(); });
                p.AddCombo("생성", new[] { "새 본체", "접합" }, mi.Join ? 1 : 0, i => { mi.Join = i == 1; Changed(); });
                break;

            case PatternFeature pt when pt.Circular:
                Select("객체", "bodies", pt.Bodies.Count, "본체", () => pt.Bodies.Clear());
                p.AddCombo("축", new[] { "X축", "Y축", "Z축" }, pt.Axis switch { "X" => 0, "Y" => 1, _ => 2 }, i => { pt.Axis = new[] { "X", "Y", "Z" }[i]; Changed(); });
                p.AddCombo("분산", new[] { "전체" }, 0, _ => { });
                p.AddNumber("수량", pt.Count, "", v => pt.Count = (int)Math.Round(v), Changed, f, "Count");
                p.AddNumber("전체 각도", pt.Angle, "deg", v => pt.Angle = v, Changed, f, "Angle");
                break;

            case PatternFeature pt:
                Select("객체", "bodies", pt.Bodies.Count, "본체", () => pt.Bodies.Clear());
                p.AddCombo("방향 1", new[] { "X축", "Y축", "Z축" }, pt.Dir1 switch { "X" => 0, "Y" => 1, _ => 2 }, i => { pt.Dir1 = new[] { "X", "Y", "Z" }[i]; Changed(); });
                p.AddNumber("수량", pt.Count1, "", v => pt.Count1 = (int)Math.Round(v), Changed, f, "Count1");
                p.AddNumber("간격", pt.Spacing1, "mm", v => pt.Spacing1 = v, Changed, f, "Spacing1");
                p.AddCombo("방향 2", new[] { "X축", "Y축", "Z축" }, pt.Dir2 switch { "X" => 0, "Y" => 1, _ => 2 }, i => { pt.Dir2 = new[] { "X", "Y", "Z" }[i]; Changed(); });
                p.AddNumber("수량 2", pt.Count2, "", v => pt.Count2 = (int)Math.Round(v), Changed, f, "Count2");
                p.AddNumber("간격 2", pt.Spacing2, "mm", v => pt.Spacing2 = v, Changed, f, "Spacing2");
                break;

            case CombineFeature cb:
                Select("대상 본체", "target", string.IsNullOrEmpty(cb.Target) ? 0 : 1, "본체", () => cb.Target = "");
                Select("도구 본체", "tools", cb.Tools.Count, "본체", () => cb.Tools.Clear());
                p.AddCombo("생성", new[] { "접합", "잘라내기", "교차" }, cb.Operation switch { BodyOp.Cut => 1, BodyOp.Intersect => 2, _ => 0 },
                    i => { cb.Operation = i switch { 1 => BodyOp.Cut, 2 => BodyOp.Intersect, _ => BodyOp.Join }; Changed(); });
                p.AddCheck("도구 유지", cb.KeepTools, v => { cb.KeepTools = v; Changed(); });
                break;

            case ScaleFeature sc:
                Select("객체", "bodies", sc.Bodies.Count, "본체", () => sc.Bodies.Clear());
                p.AddCombo("축척 유형", new[] { "균일" }, 0, _ => { });
                p.AddNumber("축척 계수", sc.Factor, "", v => sc.Factor = v, Changed, f, "Factor");
                break;

            case LoftFeature lf:
                Select("프로파일", "profiles", lf.Profiles.Count, "프로파일", () => lf.Profiles.Clear());
                p.AddText("서로 다른 스케치의 닫힌 프로파일을 순서대로 클릭하세요.");
                p.AddCombo("생성", OpNames, OpIndex(lf.Operation), i => { lf.Operation = OpOrder[i]; Changed(); });
                break;

            case SweepFeature sw:
                Select("프로파일", "profiles", sw.Profiles.Count, "프로파일", () => sw.Profiles.Clear());
                Select("경로", "path", sw.Path.Count, "경로", () => sw.Path.Clear());
                p.AddCombo("생성", OpNames, OpIndex(sw.Operation), i => { sw.Operation = OpOrder[i]; Changed(); });
                break;

            case SurfaceFeature { Kind: "revolve" } sf:
            {
                Select("프로파일", "chains", sf.Chains.Count, "곡선", () => sf.Chains.Clear());
                var axes = new List<string> { "스케치 X축", "스케치 Y축" };
                var sketch = sf.Chains.Count > 0 ? c.Base!.Sketches.FirstOrDefault(s => s.Id == sf.Chains[0].Sketch) : null;
                if (sketch != null) axes.AddRange(sketch.Entities.Where(e => e.Kind == "line").Select((_, i) => $"스케치 선 {i + 1}"));
                p.AddCombo("축", axes.ToArray(), Math.Min(sf.Axis, axes.Count - 1), i => { sf.Axis = i; Changed(); });
                p.AddNumber("각도", sf.Angle, "deg", v => sf.Angle = v, Changed, f, "Angle");
                break;
            }

            case SurfaceFeature { Kind: "sweep" } sf:
                Select("프로파일", "chains", sf.Chains.Count, "곡선", () => sf.Chains.Clear());
                Select("경로", "path", sf.Path.Count, "경로", () => sf.Path.Clear());
                break;

            case SurfaceFeature { Kind: "loft" } sf:
                Select("프로파일", "chains", sf.Chains.Count, "곡선", () => sf.Chains.Clear());
                p.AddText("서로 다른 스케치의 곡선을 순서대로 클릭하세요. 열린 곡선과 닫힌 곡선을 섞을 수 없습니다.");
                break;

            case SurfaceFeature sf:
                Select(sf.Patch ? "경계" : "프로파일", "chains", sf.Chains.Count, "곡선", () => sf.Chains.Clear());
                if (!sf.Patch) p.AddNumber("거리", sf.Distance, "mm", v => sf.Distance = v, Changed, f, "Distance");
                break;

            case HoleFeature ho:
            {
                Select("위치", "holepts", ho.Points.Count, "점", () => { ho.Points.Clear(); ho.Body = ""; });
                var holeTypes = HoleFeature.Types;
                p.AddCombo("구멍 유형", holeTypes.Select(HoleFeature.TypeName).ToArray(), Math.Max(0, Array.IndexOf(holeTypes, ho.Type)), i => { ho.Type = holeTypes[i]; BuildCommandPanel(); Changed(); });
                p.AddCombo("범위", new[] { "거리", "전체 관통" }, ho.ThroughAll ? 1 : 0, i => { ho.ThroughAll = i == 1; BuildCommandPanel(); Changed(); });
                p.AddNumber("지름", ho.Diameter, "mm", v => ho.Diameter = v, Changed, f, "Diameter");
                if (!ho.ThroughAll) p.AddNumber("깊이", ho.Depth, "mm", v => ho.Depth = v, Changed, f, "Depth");
                if (ho.Type == "counterbore")
                {
                    p.AddNumber("카운터보어 지름", ho.CounterDiameter, "mm", v => ho.CounterDiameter = v, Changed, f, "CounterDiameter");
                    p.AddNumber("카운터보어 깊이", ho.CounterDepth, "mm", v => ho.CounterDepth = v, Changed, f, "CounterDepth");
                }
                if (ho.Type == "countersink")
                {
                    p.AddNumber("카운터싱크 지름", ho.CounterDiameter, "mm", v => ho.CounterDiameter = v, Changed, f, "CounterDiameter");
                    p.AddNumber("카운터싱크 각도", ho.SinkAngle, "deg", v => ho.SinkAngle = v, Changed, f, "SinkAngle");
                }
                if (!ho.ThroughAll) p.AddCombo("드릴 끝", new[] { "평면", "각도 (118°)" }, ho.DrillPoint ? 1 : 0, i => { ho.DrillPoint = i == 1; Changed(); });
                break;
            }

            case SplitBodyFeature sp:
            {
                Select("분할할 본체", "bodies", sp.Bodies.Count, "본체", () => sp.Bodies.Clear());
                var planes = new[] { "XY", "XZ", "YZ", "face" };
                p.AddCombo("분할 도구", new[] { "XY 평면", "XZ 평면", "YZ 평면", "평면인 면" }, Math.Max(0, Array.IndexOf(planes, sp.Plane)),
                    i => { sp.Plane = planes[i]; if (i == 3) c.SelectKind = "toolface"; BuildCommandPanel(); Changed(); });
                if (sp.Plane == "face") Select("도구 면", "toolface", sp.ToolPick.Length == 3 ? 1 : 0, "면", () => { sp.ToolPick = Array.Empty<double>(); sp.ToolBody = ""; });
                p.AddNumber("간격띄우기", sp.Offset, "mm", v => sp.Offset = v, Changed, f, "Offset");
                break;
            }

            case DraftFeature dr:
                Select("고정 평면", "neutral", dr.Neutral.Length == 3 ? 1 : 0, "면", () => { dr.Neutral = Array.Empty<double>(); dr.Body = ""; dr.Faces.Clear(); });
                Select("면", "faces", dr.Faces.Count, "면", () => dr.Faces.Clear());
                p.AddNumber("각도", dr.Angle, "deg", v => dr.Angle = v, Changed, f, "Angle");
                p.AddCheck("방향 뒤집기", dr.Flip, v => { dr.Flip = v; Changed(); });
                break;

            case CoilFeature co:
                p.AddNumber("지름", co.Diameter, "mm", v => co.Diameter = v, Changed, f, "Diameter");
                p.AddNumber("회전수", co.Turns, "", v => co.Turns = v, Changed, f, "Turns");
                p.AddNumber("피치", co.Pitch, "mm", v => co.Pitch = v, Changed, f, "Pitch");
                p.AddInfo("높이", $"{co.Pitch * co.Turns:0.##} mm");
                p.AddNumber("단면 크기", co.Section, "mm", v => co.Section = v, Changed, f, "Section");
                p.AddHeader("위치");
                p.AddNumber("X", co.X, "mm", v => co.X = v, Changed, f, "X");
                p.AddNumber("Y", co.Y, "mm", v => co.Y = v, Changed, f, "Y");
                p.AddNumber("Z (바닥)", co.Z, "mm", v => co.Z = v, Changed, f, "Z");
                p.AddCombo("생성", OpNames, OpIndex(co.Operation), i => { co.Operation = OpOrder[i]; Changed(); });
                break;

            case PipeFeature pi:
                Select("경로", "chains", pi.Chains.Count, "곡선", () => pi.Chains.Clear());
                p.AddNumber("단면 크기", pi.Diameter, "mm", v => pi.Diameter = v, Changed, f, "Diameter");
                p.AddCheck("속이 빈", pi.Hollow, v => { pi.Hollow = v; BuildCommandPanel(); Changed(); });
                if (pi.Hollow) p.AddNumber("단면 두께", pi.Thickness, "mm", v => pi.Thickness = v, Changed, f, "Thickness");
                p.AddCombo("생성", OpNames, OpIndex(pi.Operation), i => { pi.Operation = OpOrder[i]; Changed(); });
                break;

            case StitchFeature st:
                Select("곡면 본체", "bodies", st.Bodies.Count, "본체", () => st.Bodies.Clear());
                p.AddNumber("스티치 공차", st.Tolerance, "mm", v => st.Tolerance = v, Changed, f, "Tolerance");
                p.AddText("닫힌 곡면이 되면 솔리드 본체가 됩니다.");
                break;

            case ThickenFeature th:
                Select("면", "bodies", th.Bodies.Count, "곡면 본체", () => th.Bodies.Clear());
                p.AddNumber("두께", th.Thickness, "mm", v => th.Thickness = v, Changed, f, "Thickness");
                break;

            case PressPullFeature pp:
                Select("면", "face1", pp.Pick.Length == 3 ? 1 : 0, "면", () => { pp.Pick = Array.Empty<double>(); pp.Body = ""; });
                p.AddNumber("거리", pp.Distance, "mm", v => pp.Distance = v, Changed, f, "Distance");
                break;

            case FlangeFeature { Edge: false } fl:
                Select("프로파일", "profiles", fl.Profiles.Count, "프로파일", () => fl.Profiles.Clear());
                p.AddNumber("두께", fl.Thickness, "mm", v => fl.Thickness = v, Changed, f, "Thickness");
                break;

            case FlangeFeature fl:
                Select("모서리", "edge1", fl.Pick.Length == 3 ? 1 : 0, "모서리", () => { fl.Pick = Array.Empty<double>(); fl.Body = ""; });
                p.AddNumber("높이", fl.Height, "mm", v => fl.Height = v, Changed, f, "Height");
                p.AddNumber("굽힘 반지름", fl.BendRadius, "mm", v => fl.BendRadius = v, Changed, f, "BendRadius");
                p.AddNumber("두께", fl.Thickness, "mm", v => fl.Thickness = v, Changed, f, "Thickness");
                p.AddCombo("방향", new[] { "위", "아래" }, fl.Down ? 1 : 0, i => { fl.Down = i == 1; Changed(); });
                p.AddInfo("펼친 길이", $"{fl.DevelopedLength:0.##} mm (K = 0.44)");
                break;

            case RibFeature rb:
                Select("곡선", "chains", rb.Chains.Count, "곡선", () => rb.Chains.Clear());
                p.AddNumber("두께", rb.Thickness, "mm", v => rb.Thickness = v, Changed, f, "Thickness");
                p.AddNumber("깊이", rb.Depth, "mm", v => rb.Depth = v, Changed, f, "Depth");
                p.AddCheck("방향 뒤집기", rb.Flip, v => { rb.Flip = v; Changed(); });
                break;

            case BossFeature bo:
                Select("위치", "face1", bo.Pick.Length == 3 ? 1 : 0, "점", () => { bo.Pick = Array.Empty<double>(); bo.Body = ""; });
                p.AddNumber("지름", bo.Diameter, "mm", v => bo.Diameter = v, Changed, f, "Diameter");
                p.AddNumber("구멍 지름", bo.HoleDiameter, "mm", v => bo.HoleDiameter = v, Changed, f, "HoleDiameter");
                p.AddNumber("높이", bo.Height, "mm", v => bo.Height = v, Changed, f, "Height");
                break;

            case JointFeature jt:
                p.AddHeader("위치");
                Select("구성요소 1", "jointA", jt.PickA.Length == 3 ? 1 : 0, "원점", () => { jt.PickA = Array.Empty<double>(); jt.BodyA = ""; });
                Select("구성요소 2", "jointB", jt.PickB.Length == 3 ? 1 : 0, "원점", () => { jt.PickB = Array.Empty<double>(); jt.BodyB = ""; });
                p.AddHeader("동작");
                var types = JointFeature.Types;
                p.AddCombo("유형", types.Select(JointFeature.TypeName).ToArray(), Math.Max(0, Array.IndexOf(types, jt.Type)), i => { jt.Type = types[i]; BuildCommandPanel(); Changed(); });
                if (jt.Type != "slider") p.AddNumber(jt.Type == "ball" ? "피치(축 회전)" : "각도", jt.Angle, "deg", v => jt.Angle = v, Changed, f, "Angle");
                if (jt.Type == "ball")
                {
                    p.AddNumber("요", jt.Angle2, "deg", v => jt.Angle2 = v, Changed, f, "Angle2");
                    p.AddNumber("롤", jt.Angle3, "deg", v => jt.Angle3 = v, Changed, f, "Angle3");
                }
                if (jt.Type is "slider" or "cylindrical" or "pinslot" or "planar") p.AddNumber(jt.Type == "planar" ? "X 거리" : jt.Type == "pinslot" ? "슬롯 거리" : "거리", jt.Distance, "mm", v => jt.Distance = v, Changed, f, "Distance");
                if (jt.Type == "planar") p.AddNumber("Y 거리", jt.Distance2, "mm", v => jt.Distance2 = v, Changed, f, "Distance2");
                if (jt.Type is "revolute" or "slider")
                {
                    string unit = jt.Type == "slider" ? "mm" : "deg";
                    p.AddCheck("최소 한계", jt.LowerLimit != null, v => { jt.LowerLimit = v ? (jt.Type == "slider" ? 0 : -90) : null; BuildCommandPanel(); });
                    if (jt.LowerLimit is { } lo) p.AddNumber("최소", lo, unit, v => jt.LowerLimit = v);
                    p.AddCheck("최대 한계", jt.UpperLimit != null, v => { jt.UpperLimit = v ? (jt.Type == "slider" ? 50 : 90) : null; BuildCommandPanel(); });
                    if (jt.UpperLimit is { } hi) p.AddNumber("최대", hi, unit, v => jt.UpperLimit = v);
                }
                p.AddNumber("간격띄우기", jt.Offset, "mm", v => jt.Offset = v, Changed, f, "Offset");
                p.AddCheck("뒤집기", jt.Flip, v => { jt.Flip = v; Changed(); });
                break;

            case FilletFeature fl:
                p.AddInfo("유형", fl.Chamfer ? "챔퍼 - 동일 거리" : "필렛 - 일정 반지름");
                Select("모서리", "edges", fl.Edges.Count, "모서리", () => { fl.Edges.Clear(); fl.Body = ""; });
                p.AddCheck("모든 모서리", fl.AllEdges, v => { fl.AllEdges = v; if (v && fl.Body == "" && c.Base!.Bodies.Count > 0) fl.Body = c.Base.Bodies[^1].Id; Changed(); });
                p.AddNumber(fl.Chamfer ? "거리" : "반지름", fl.Size, "mm", v => fl.Size = v, Changed, f, "Size");
                break;

            case ShellFeature sh:
                Select("면/본체", "faces", sh.Faces.Count, "면", () => { sh.Faces.Clear(); sh.Body = ""; });
                p.AddNumber(sh.Outside ? "외부 두께" : "내부 두께", sh.Thickness, "mm", v => sh.Thickness = v, Changed, f, "Thickness");
                p.AddCombo("방향", new[] { "내부", "외부" }, sh.Outside ? 1 : 0, i => { sh.Outside = i == 1; BuildCommandPanel(); Changed(); });
                break;

            case DeleteFeature del:
                Select("객체", "bodies", del.Bodies.Count, "본체", () => del.Bodies.Clear());
                break;
        }
        c.Panel = p;
        p.SetError(f.Error);
        ShowDialog(p);
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    /// <summary>Evaluate the timeline with the dialog's draft feature and show the result.</summary>
    public void RefreshPreview()
    {
        var c = _cmd;
        if (c?.Draft is not { } draft) return;
        var list = _doc.Features.ToList();
        int at, upto;
        if (c.EditIndex >= 0)
        {
            list[c.EditIndex] = draft;
            at = c.EditIndex;
            upto = _doc.Marker;
        }
        else
        {
            list.Insert(_doc.Marker, draft);
            at = _doc.Marker;
            upto = _doc.Marker + 1;
        }
        _preview = Eval(list, upto, c.Base, at);
        c.Panel?.SetError(draft.Error);
        UpdateItems();
        View.InvalidateVisual();
    }

    public void OkCommand()
    {
        var c = _cmd;
        if (c == null) return;
        if (c.Draft is not { } draft) { CancelCommand(); return; }
        RefreshPreview();
        if (draft.Error != null && !(draft is ExtrudeFeature or RevolveFeature && ProfileList(draft)!.Count > 0))
        {
            c.Panel?.SetError(draft.Error);
            return;
        }
        _cmd = null;
        CloseDialog();
        SetPrompt(null);
        var used = ProfileList(draft)?.Select(r => r.Sketch).ToHashSet() ?? new HashSet<string>();
        var chains = draft switch { SurfaceFeature sf => sf.Chains.Concat(sf.Path).ToList(), RibFeature rb => rb.Chains, SweepFeature sw => sw.Path, PipeFeature pi => pi.Chains, _ => null };
        if (chains != null) used.UnionWith(chains.Select(c => c.Sketch));
        Commit(() =>
        {
            if (c.EditIndex >= 0) _doc.Features[c.EditIndex] = draft;
            else
            {
                _doc.Features.Insert(_doc.Marker, draft);
                _doc.Marker++;
            }
            // like Fusion, a sketch is hidden once it has been consumed by a feature
            foreach (var sk in _doc.Features.OfType<SketchFeature>().Where(s => used.Contains(s.Id))) sk.Visible = false;
        });
        _selProfiles.Clear();
    }

    public void CancelCommand()
    {
        if (_cmd == null) return;
        if (_cmd.Draft != null || _cmd.Id.StartsWith("cam_")) SetPrompt(null);
        _cmd = null;
        _flatView = null;
        _interference.Clear();
        _massMarker = null;
        _camDraft = null;
        _camSim = null;
        _previewTimer.Stop();
        _preview = null;
        CloseDialog();
        UpdateItems();
        RebuildTimeline();
        View.InvalidateVisual();
    }

    private void CommandPickProfile(ProfileRef r)
    {
        if (ProfileList(_cmd?.Draft) is not { } list) return;
        if (_cmd!.Draft is SweepFeature)
        {
            // one profile, then the path
            list.Clear();
            list.Add(r);
            _cmd.SelectKind = "path";
        }
        else if (!list.Remove(r)) list.Add(r);
        BuildCommandPanel();
        RefreshPreview();
    }

    private void CommandPickEdge(string bodyId, V3 point)
    {
        if (_cmd?.Draft is not FilletFeature fl) return;
        if (fl.Body != bodyId)
        {
            fl.Body = bodyId;
            fl.Edges.Clear();
        }
        var edge = EdgeAt(bodyId, point);
        int existing = edge == null ? -1 : fl.Edges.FindIndex(e => DistToPolyline(new V3(e[0], e[1], e[2]), edge) < 1e-6 + 1e-4 * (edge[0] - edge[^1]).Length + 0.01);
        if (existing >= 0) fl.Edges.RemoveAt(existing);
        else fl.Edges.Add(new[] { point.X, point.Y, point.Z });
        BuildCommandPanel();
        RefreshPreview();
    }

    private void CommandPickChain(ChainRef r)
    {
        var list = _cmd?.Draft switch
        {
            SurfaceFeature sf => _cmd.SelectKind == "path" ? sf.Path : sf.Chains,
            RibFeature rb => rb.Chains,
            SweepFeature sw => sw.Path,
            PipeFeature pi => pi.Chains,
            _ => null,
        };
        if (list == null) return;
        if (_cmd!.Draft is SweepFeature || _cmd.Draft is SurfaceFeature { Kind: "sweep" } || _cmd.Draft is SurfaceFeature { Kind: "revolve" } && _cmd.SelectKind == "path") list.Clear();
        if (!list.Remove(r)) list.Add(r);
        if (_cmd.Draft is SurfaceFeature { Kind: "sweep" } && _cmd.SelectKind == "chains") _cmd.SelectKind = "path";
        BuildCommandPanel();
        RefreshPreview();
    }

    /// <summary>Single face/point picks (press-pull, boss) and single edges (edge flange).</summary>
    private void CommandPickPoint(string bodyId, V3 point)
    {
        switch (_cmd?.Draft)
        {
            case PressPullFeature pp: pp.Body = bodyId; pp.Pick = new[] { point.X, point.Y, point.Z }; break;
            case BossFeature bo: bo.Body = bodyId; bo.Pick = new[] { point.X, point.Y, point.Z }; break;
            case FlangeFeature fl: fl.Body = bodyId; fl.Pick = new[] { point.X, point.Y, point.Z }; break;
            case SplitBodyFeature sp: sp.ToolBody = bodyId; sp.ToolPick = new[] { point.X, point.Y, point.Z }; break;
            case DraftFeature dr:
                if (dr.Body != bodyId) dr.Faces.Clear();
                dr.Body = bodyId;
                dr.Neutral = new[] { point.X, point.Y, point.Z };
                _cmd.SelectKind = "faces";
                break;
            default: return;
        }
        BuildCommandPanel();
        RefreshPreview();
    }

    /// <summary>"매개변수 관리": user parameters, plus the model fields that use them.</summary>
    private void ShowParameters()
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "parameters" };
        var rows = _doc.Parameters.Select(p => new UserParameter { Name = p.Name, Expression = p.Expression, Unit = p.Unit, Comment = p.Comment }).ToList();
        void Build()
        {
            var p = new CommandPanel("매개변수", "parameters", () =>
            {
                var bad = rows.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Name) || !System.Text.RegularExpressions.Regex.IsMatch(r.Name, @"^[A-Za-z_가-힣][A-Za-z0-9_가-힣]*$"));
                if (bad != null) { _cmd?.Panel?.SetError($"이름이 올바르지 않습니다: '{bad.Name}'"); return; }
                var values = Params.Compute(rows);
                var unresolved = rows.FirstOrDefault(r => !values.ContainsKey(r.Name));
                if (unresolved != null) { _cmd?.Panel?.SetError($"식을 계산할 수 없습니다: {unresolved.Name} = {unresolved.Expression}"); return; }
                Commit(() => _doc.Parameters = rows);
                CancelCommand();
            }, CancelCommand);
            p.AddHeader("사용자 매개변수");
            var values = Params.Compute(rows);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,70,24"), Height = 28 };
                var name = new TextBox { Text = r.Name, Height = 26, MinHeight = 0, Padding = new Thickness(4, 2), FontSize = 12, Tag = $"이름{i + 1}" };
                name.TextChanged += (_, _) => r.Name = (name.Text ?? "").Trim();
                var expr = new TextBox { Text = r.Expression, Height = 26, MinHeight = 0, Padding = new Thickness(4, 2), FontSize = 12, Margin = new Thickness(4, 0), Tag = $"식{i + 1}" };
                expr.TextChanged += (_, _) => r.Expression = expr.Text ?? "";
                var val = Ui.Label(values.TryGetValue(r.Name, out var v) ? CommandPanel.Format(v, r.Unit) : "?", 12, Ui.Muted);
                var del = new FlatButton { Width = 20, Height = 20, Child = Ui.Centered(Icons.Line("close", 10, Ui.Muted)) }.Init();
                ToolTip.SetTip(del, "삭제");
                del.Click += () => { rows.Remove(r); Build(); };
                Grid.SetColumn(expr, 1);
                Grid.SetColumn(val, 2);
                Grid.SetColumn(del, 3);
                g.Children.Add(name);
                g.Children.Add(expr);
                g.Children.Add(val);
                g.Children.Add(del);
                p.AddControl(g);
            }
            var add = new FlatButton
            {
                Height = 28,
                Normal = Ui.Brush("#EDEDED"),
                Child = Ui.Centered(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icons.Line("plus", 12), Ui.Label("사용자 매개변수 추가", 12) } }),
            }.Init();
            add.Click += () =>
            {
                int n = 1;
                while (rows.Any(r => r.Name == "d" + n)) n++;
                rows.Add(new UserParameter { Name = "d" + n, Expression = "10 mm" });
                Build();
            };
            p.AddControl(add);
            var used = _doc.Features.SelectMany(f => f.Expressions.Select(e => $"{f.Name}.{e.Key} = {e.Value}"))
                .Concat(_doc.Features.OfType<SketchFeature>().SelectMany(sk => sk.Constraints.Where(c => c.Expression != null).Select(c => $"{sk.Name} {SketchConstraint.DisplayName(c.Kind)} = {c.Expression}"))).ToList();
            p.AddHeader("모형 매개변수");
            p.AddText(used.Count == 0 ? "매개변수를 쓰는 값이 아직 없습니다. 대화상자의 숫자 칸에 매개변수 이름(예: d1 * 2)을 입력하면 연결됩니다." : string.Join("\n", used));
            _cmd!.Panel = p;
            ShowDialog(p);
        }
        Build();
    }

    /// <summary>Name used for exchange folders and Isaac Sim prims.</summary>
    public string DesignName => IsaacLink.SafeName(_path != null ? Path.GetFileNameWithoutExtension(_path) : "untitled");

    private void ShowIsaacResult(string text, bool busy)
    {
        var p = new CommandPanel("IsaacSimLink", "isaac_robot", null, CancelCommand);
        p.AddText(text);
        if (busy) p.AddText("잠시 기다려 주세요...");
        _cmd = new CommandSession { Id = "isaac", Panel = p };
        ShowDialog(p);
    }

    /// <summary>Send the design to Isaac Sim (or check the connection); runs off the UI thread.</summary>
    public async Task<string> RunIsaac(string id)
    {
        CancelCommand();
        string design = DesignName;
        var state = State;
        var doc = _doc;
        Func<string, string> name = BodyName;
        ShowIsaacResult(id switch { "isaac_robot" => "로봇(URDF)을 Isaac Sim으로 보내는 중", "isaac_cad" => "형상(STEP)을 Isaac Sim으로 보내는 중", _ => "Isaac Sim 연결을 확인하는 중" }, true);
        string result;
        try
        {
            result = await Task.Run(() => id switch
            {
                "isaac_robot" => IsaacLink.SendRobot(design, state, doc, name, out _),
                "isaac_cad" => IsaacLink.SendCad(design, path => Solid.Export(state.Bodies.Where(b => !doc.HiddenBodies.Contains(b.Id)).Select(b => (name(b.Id), b.Solid)).ToList(), path, 3)),
                _ => IsaacLink.Status(),
            });
        }
        catch (Exception ex)
        {
            result = ex.Message;
        }
        if (_cmd?.Id == "isaac") ShowIsaacResult(result, false);
        return result;
    }

    private (string body, Solid flat)? _flatView;

    /// <summary>Flat pattern of a sheet metal body, or null if the body is not sheet metal.</summary>
    public Solid? FlatPatternOf(string bodyId) =>
        State.Sheets.TryGetValue(bodyId, out var sheet) ? FlatPattern.Build(sheet) : null;

    public bool ExportDxf(string bodyId, string path)
    {
        if (!State.Sheets.TryGetValue(bodyId, out var sheet)) return false;
        var dxf = FlatPattern.Dxf(FlatPattern.Build(sheet), sheet);
        if (dxf.Length == 0) return false;
        File.WriteAllText(path, dxf);
        return true;
    }

    private void ShowFlatPattern()
    {
        CancelCommand();
        var id = _selBodies.FirstOrDefault(State.Sheets.ContainsKey) ?? State.Sheets.Keys.FirstOrDefault(k => State.Find(k) != null);
        var p = new CommandPanel("플랫 패턴", "flatpattern", null, () => { _flatView = null; CancelCommand(); UpdateItems(); });
        _cmd = new CommandSession { Id = "flatpattern", Panel = p };
        if (id == null)
        {
            p.AddText("판금 본체가 없습니다. 판금 탭의 플랜지로 만든 본체를 선택하세요.");
            ShowDialog(p);
            return;
        }
        try
        {
            var flat = FlatPattern.Build(State.Sheets[id]);
            _flatView = (id, flat);
            var i = flat.Info;
            var f = State.Sheets[id].Base[0].frame;
            var corners = new[] { i.Min, i.Max }.Select(f.Project).ToList();
            p.AddInfo("본체", BodyName(id));
            p.AddInfo("두께", $"{State.Sheets[id].Thickness:0.###} mm");
            p.AddInfo("펼친 크기", $"{Math.Abs(corners[1].X - corners[0].X):0.##} × {Math.Abs(corners[1].Y - corners[0].Y):0.##} mm");
            p.AddInfo("플랜지", $"{State.Sheets[id].Flanges.Count}개 (K = 0.44)");
            var dxf = new FlatButton { Height = 30, Normal = Ui.Brush("#EDEDED"), Child = Ui.Centered(Ui.Label("DXF로 내보내기...", 12)) }.Init();
            dxf.Click += async () =>
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "플랫 패턴 DXF 내보내기",
                    SuggestedFileName = BodyName(id) + "_flat.dxf",
                    DefaultExtension = "dxf",
                    FileTypeChoices = new[] { new FilePickerFileType("DXF") { Patterns = new[] { "*.dxf" } } },
                });
                if (file?.TryGetLocalPath() is { } path && !ExportDxf(id, path)) p.SetError("DXF를 쓸 수 없습니다.");
            };
            p.AddControl(dxf);
            UpdateItems();
        }
        catch (Exception ex)
        {
            p.SetError(ex.Message);
        }
        ShowDialog(p);
    }

    private void ShowSheetRule()
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "sheetrule" };
        double t = _doc.SheetThickness, r = _doc.SheetBendRadius;
        var p = new CommandPanel("판금 규칙", "sheetrule", () =>
        {
            Commit(() => { _doc.SheetThickness = t; _doc.SheetBendRadius = r; });
            CancelCommand();
        }, CancelCommand);
        p.AddNumber("두께", t, "mm", v => t = v);
        p.AddNumber("굽힘 반지름", r, "mm", v => r = v);
        p.AddInfo("K 계수", "0.44");
        _cmd.Panel = p;
        ShowDialog(p);
    }

    private (string plane, double offset, bool flip)? _section;
    private readonly Dictionary<(uint, string, double, bool), Solid> _sectionCache = new();

    /// <summary>Display-only half-space cut of a body for section analysis.</summary>
    private Solid SectionOf(Solid s)
    {
        if (_section is not { } sec) return s;
        var key = (s.Shape, sec.plane, sec.offset, sec.flip);
        if (_sectionCache.TryGetValue(key, out var cut)) return cut;
        var i = s.Info;
        double big = (i.Max - i.Min).Length + 10;
        var lo = i.Min - new V3(big, big, big);
        var hi = i.Max + new V3(big, big, big);
        var n = sec.plane switch { "XY" => V3.UnitZ, "XZ" => V3.UnitY, _ => V3.UnitX };
        // keep the side below the plane (or above when flipped)
        if (sec.plane == "XY") { if (sec.flip) lo = lo with { Z = sec.offset }; else hi = hi with { Z = sec.offset }; }
        else if (sec.plane == "XZ") { if (sec.flip) lo = lo with { Y = sec.offset }; else hi = hi with { Y = sec.offset }; }
        else { if (sec.flip) lo = lo with { X = sec.offset }; else hi = hi with { X = sec.offset }; }
        _ = n;
        try { cut = s.Boolean(Solid.Box(lo, hi), BoolOp.Intersect); }
        catch (InvalidOperationException) { cut = s; }
        if (_sectionCache.Count > 200) _sectionCache.Clear();
        _sectionCache[key] = cut;
        return cut;
    }

    private void ShowSection()
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "section" };
        var cur = _section ?? ("XZ", 0, false);
        string plane = cur.plane;
        double offset = cur.offset;
        bool flip = cur.flip;
        void Apply()
        {
            _section = (plane, offset, flip);
            UpdateItems();
        }
        var p = new CommandPanel("단면 분석", "section", CancelCommand, () => { _section = null; UpdateItems(); CancelCommand(); }, "확인");
        var planes = new[] { "XY", "XZ", "YZ" };
        p.AddCombo("평면", new[] { "XY 평면", "XZ 평면", "YZ 평면" }, Array.IndexOf(planes, plane), i => { plane = planes[i]; Apply(); });
        p.AddNumber("간격띄우기", offset, "mm", v => { offset = v; Apply(); });
        p.AddCheck("뒤집기", flip, v => { flip = v; Apply(); });
        p.AddText("취소를 누르면 단면 분석을 끕니다.");
        _cmd.Panel = p;
        ShowDialog(p);
        Apply();
    }

    private void CommandPickJoint(string bodyId, V3 point)
    {
        if (_cmd?.Draft is not JointFeature j) return;
        if (_cmd.SelectKind == "jointA")
        {
            j.BodyA = bodyId;
            j.PickA = new[] { point.X, point.Y, point.Z };
            _cmd.SelectKind = j.PickB.Length == 3 ? "" : "jointB";
        }
        else
        {
            j.BodyB = bodyId;
            j.PickB = new[] { point.X, point.Y, point.Z };
            _cmd.SelectKind = "";
        }
        BuildCommandPanel();
        RefreshPreview();
    }

    private void CommandPickFace(string bodyId, V3 point)
    {
        if (_cmd?.Draft is HoleFeature ho)
        {
            if (ho.Body != bodyId) { ho.Body = bodyId; ho.Points.Clear(); }
            ho.Points.Add(new[] { point.X, point.Y, point.Z });
            BuildCommandPanel();
            RefreshPreview();
            return;
        }
        if (_cmd?.Draft is DraftFeature dr)
        {
            if (dr.Body != bodyId) return;   // faces of the body with the fixed plane only
            dr.Faces.Add(new[] { point.X, point.Y, point.Z });
            BuildCommandPanel();
            RefreshPreview();
            return;
        }
        if (_cmd?.Draft is not ShellFeature sh) return;
        if (sh.Body != bodyId)
        {
            sh.Body = bodyId;
            sh.Faces.Clear();
        }
        sh.Faces.Add(new[] { point.X, point.Y, point.Z });
        BuildCommandPanel();
        RefreshPreview();
    }

    private void CommandPickBody(string id)
    {
        var c = _cmd;
        if (c?.Draft == null) return;
        if (c.Draft is CombineFeature cb)
        {
            if (c.SelectKind == "target")
            {
                cb.Target = id;
                cb.Tools.Remove(id);
                c.SelectKind = "tools";
            }
            else if (id != cb.Target && !cb.Tools.Remove(id)) cb.Tools.Add(id);
        }
        else if (BodyList(c.Draft) is { } list)
        {
            if (!list.Remove(id)) list.Add(id);
        }
        else if (c.Draft is ExtrudeFeature or RevolveFeature or PrimitiveFeature)
        {
            var targets = c.Draft switch { ExtrudeFeature e => e.Targets, RevolveFeature r => r.Targets, PrimitiveFeature p => p.Targets, _ => null };
            if (targets != null && !targets.Remove(id)) targets.Add(id);
        }
        BuildCommandPanel();
        RefreshPreview();
    }

    // ------------------------------------------------------------ non-parametric panels

    private void ShowMeasure()
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "measure" };
        _cmd.Rebuild = () =>
        {
            var p = new CommandPanel("측정", "measure", null, CancelCommand);
            var bodies = (_selBodies.Count > 0 ? _selBodies.Select(State.Find).Where(b => b != null).Cast<Body>() : State.Bodies).ToList();
            if (bodies.Count == 0)
            {
                p.AddText("측정할 본체가 없습니다. 본체를 선택하세요.");
            }
            else
            {
                double vol = 0, area = 0;
                var lo = new V3(double.MaxValue, double.MaxValue, double.MaxValue);
                var hi = -lo;
                int tris = 0;
                foreach (var b in bodies)
                {
                    var i = b.Solid.Info;
                    vol += i.Volume;
                    area += i.Area;
                    tris += i.Solids;
                    lo = new V3(Math.Min(lo.X, i.Min.X), Math.Min(lo.Y, i.Min.Y), Math.Min(lo.Z, i.Min.Z));
                    hi = new V3(Math.Max(hi.X, i.Max.X), Math.Max(hi.Y, i.Max.Y), Math.Max(hi.Z, i.Max.Z));
                }
                string F(double v) => v.ToString("#,0.###", System.Globalization.CultureInfo.InvariantCulture);
                p.AddInfo("대상", _selBodies.Count > 0 ? $"선택한 본체 {bodies.Count}개" : $"모든 본체 {bodies.Count}개");
                p.AddInfo("부피", $"{F(vol)} mm³ ({F(vol / 1000)} cm³)");
                p.AddInfo("면적", $"{F(area)} mm²");
                p.AddInfo("크기 X", $"{F(hi.X - lo.X)} mm");
                p.AddInfo("크기 Y", $"{F(hi.Y - lo.Y)} mm");
                p.AddInfo("크기 Z", $"{F(hi.Z - lo.Z)} mm");
                p.AddInfo("최소점", $"{F(lo.X)}, {F(lo.Y)}, {F(lo.Z)}");
                p.AddInfo("최대점", $"{F(hi.X)}, {F(hi.Y)}, {F(hi.Z)}");
                p.AddInfo("솔리드", F(tris));
            }
            ShowDialog(p);
        };
        _cmd.Rebuild();
    }

    private void ShowAppearance()
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "appearance" };
        _cmd.Rebuild = () =>
        {
            var p = new CommandPanel("모양", "appearance", null, CancelCommand);
            p.AddText(_selBodies.Count > 0 ? $"선택한 본체 {_selBodies.Count}개에 적용합니다." : "본체를 선택한 뒤 재질을 클릭하세요.");
            var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
            foreach (var (name, hex) in Appearances)
            {
                var h = hex;
                var b = new FlatButton
                {
                    Height = 62,
                    Margin = new Thickness(2),
                    Child = new StackPanel
                    {
                        Spacing = 3,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            Ui.Centered(new Border
                            {
                                Width = 30,
                                Height = 30,
                                CornerRadius = new CornerRadius(15),
                                Background = new RadialGradientBrush
                                {
                                    GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
                                    GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.Parse(hex), 0.55), new GradientStop(Blend(Color.Parse(hex), Colors.Black, 0.35), 1) },
                                },
                            }),
                            new TextBlock { Text = name, FontSize = 10, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text },
                        },
                    },
                }.Init();
                ToolTip.SetTip(b, name);
                b.Click += () =>
                {
                    if (_selBodies.Count == 0) return;
                    var ids = _selBodies.ToList();
                    var appearance = name;
                    Commit(() => { foreach (var id in ids) { _doc.BodyColors[id] = h; _doc.BodyAppearances[id] = appearance; } });
                    ResetRayTrace();
                };
                grid.Children.Add(b);
            }
            p.AddControl(grid);
            ShowDialog(p);
        };
        _cmd.Rebuild();
    }

    private static Color Blend(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private int _exportFormat;
    private bool _exportSelected;

    private void ShowExport()
    {
        CancelCommand();
        _cmd = new CommandSession { Id = "export" };
        _cmd.Rebuild = () =>
        {
            var p = new CommandPanel("내보내기", "export", () => _ = ExportDialog(), CancelCommand, "내보내기");
            p.AddCombo("유형", new[] { "STL 파일 (*.stl)", "OBJ 파일 (*.obj)", "USD 파일 (*.usda)", "STEP 파일 (*.step)", "USD 물리·접합 포함 (*.usda, Isaac Sim)" }, _exportFormat, i => _exportFormat = i);
            p.AddCombo("대상", new[] { "보이는 모든 본체", "선택한 본체" }, _exportSelected ? 1 : 0, i => _exportSelected = i == 1);
            p.AddText("단위는 mm입니다. USD는 Z 위쪽, metersPerUnit = 0.001로 저장되어 Isaac Sim에서 그대로 열 수 있습니다. STEP은 다른 CAD(Fusion 포함)에서 정확한 형상으로 열립니다.");
            ShowDialog(p);
        };
        _cmd.Rebuild();
    }

    private async Task ExportDialog()
    {
        string ext = new[] { "stl", "obj", "usda", "step", "usda" }[_exportFormat];
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "내보내기",
            SuggestedFileName = $"{(_path != null ? Path.GetFileNameWithoutExtension(_path) : "디자인")}.{ext}",
            DefaultExtension = ext,
            FileTypeChoices = new[] { new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = new[] { "*." + ext } } },
        });
        if (f?.TryGetLocalPath() is not { } path) return;
        if (!path.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase)) path += "." + ext;
        var n = ExportTo(path, _exportFormat, _exportSelected);
        if (n < 0) await Ask("내보내기", "내보낼 본체가 없습니다.", "확인");
        else CancelCommand();
    }

    /// <summary>Write the visible (or selected) bodies; returns the body count or -1 if there was nothing to write.</summary>
    public int ExportTo(string path, int format, bool selectedOnly)
    {
        var bodies = State.Bodies.Where(b => !_doc.HiddenBodies.Contains(b.Id) && (!selectedOnly || _selBodies.Contains(b.Id)))
            .Select(b => (BodyName(b.Id), b.Solid)).ToList();
        if (bodies.Count == 0) return -1;
        if (format == 4) return UsdPhysics.Write(path, State, _doc, b => BodyName(b)) ? bodies.Count : -1;
        return Solid.Export(bodies, path, (uint)format) ? bodies.Count : -1;
    }

    private static readonly FilePickerFileType CadTypes = new("CAD 파일 (STEP, IGES)") { Patterns = new[] { "*.step", "*.stp", "*.STEP", "*.STP", "*.iges", "*.igs" } };
    private static readonly FilePickerFileType MeshTypes = new("메쉬 (STL, OBJ)") { Patterns = new[] { "*.stl", "*.STL", "*.obj" } };

    private async Task InsertFile(bool cad)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = cad ? "CAD 파일 삽입" : "메쉬 삽입",
            FileTypeFilter = new[] { cad ? CadTypes : MeshTypes },
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } p) InsertFilePath(p);
    }

    public void InsertFilePath(string path)
    {
        Commit(() =>
        {
            _doc.Features.Insert(_doc.Marker, new ImportFeature { Name = _doc.NextName("insertmesh"), Path = path });
            _doc.Marker++;
        });
        FitAll();
    }

    // ------------------------------------------------------------ keys

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox && e.Key != Key.Escape && !(e.Key == Key.Enter && _cmd?.Draft != null)) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Z when ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Shift): Undo(); break;
            case Key.Y when ctrl: Redo(); break;
            case Key.S when ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Shift): _ = Save(); break;
            case Key.O when ctrl: _ = OpenFile(); break;
            case Key.N when ctrl: _ = NewDesign(); break;
            case Key.Escape when _workspace == "drawing" && _cmd == null:
                if (_sheetTool != null) CancelDrawingTool();
                else SelectSheetItem(null, -1, -1);
                break;
            case Key.Delete when _workspace == "drawing" && _cmd == null:
                DeleteSheetSelection();
                break;
            case Key.Escape:
                if (_pickPlane) _pendingSketchTool = null;
                if (_sk != null)
                {
                    if (_sk.Pts.Count > 0) { _sk.Pts.Clear(); _sk.ChainStart = null; }
                    else { _sk.Tool = ""; SetPrompt(null); }
                    View.InvalidateVisual();
                }
                else if (_cmd != null) CancelCommand();
                else if (_pickPlane) { _pickPlane = false; SetPrompt(null); View.InvalidateVisual(); }
                else { _selBodies.Clear(); _selProfiles.Clear(); SelectionChanged(); }
                break;
            case Key.Enter:
                if (_cmd?.Draft != null) OkCommand();
                else if (_sk != null) { _sk.Pts.Clear(); _sk.ChainStart = null; View.InvalidateVisual(); }
                break;
            case Key.Delete:
                if (_sk != null && _sk.Selected >= 0)
                {
                    _sk.Remember();
                    _sk.Draft.Entities.RemoveAt(_sk.Selected);
                    SketchSolver.EntityRemoved(_sk.Draft, _sk.Selected);
                    _sk.Selected = -1;
                    SolveSketch();
                    ShowSketchPalette();
                    View.InvalidateVisual();
                }
                else if (_sk == null && _cmd == null && _selBodies.Count > 0) RunCommand("delete");
                break;
            default:
                if (!HandleShortcut(e)) return;
                break;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------ files

    private static readonly FilePickerFileType ProjectType = new("OpenShape CAD 디자인") { Patterns = new[] { "*.osc" } };
    private static readonly FilePickerFileType AnyType = new("열 수 있는 모든 파일") { Patterns = new[] { "*.osc", "*.step", "*.stp", "*.STEP", "*.STP", "*.iges", "*.igs", "*.stl", "*.STL", "*.obj" } };

    // ------------------------------------------------------------ data panel

    private readonly Border _dataPanel = new()
    {
        Width = 300,
        Background = Ui.White,
        BorderBrush = Ui.Line,
        BorderThickness = new Thickness(0, 0, 1, 0),
    };

    public bool DataPanelVisible => _dataPanel.IsVisible;

    public void ToggleDataPanel()
    {
        _dataPanel.IsVisible = !_dataPanel.IsVisible;
        if (_dataPanel.IsVisible) BuildDataPanel();
    }

    private void BuildDataPanel()
    {
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 40, Margin = new Thickness(12, 0, 6, 0) };
        head.Children.Add(Ui.Label("데이터 패널", 15, Ui.Text, FontWeight.SemiBold));
        var close = new FlatButton { Width = 28, Height = 28, Child = Ui.Centered(Icons.Line("close", 12)) }.Init();
        close.Click += ToggleDataPanel;
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        list.Children.Add(head);
        FlatButton Action(string text, string icon, Action a)
        {
            var b = new FlatButton { Height = 32, Margin = new Thickness(10, 2), Normal = Ui.Brush("#EDEDED"), Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center, Children = { Icons.Line(icon, 14), Ui.Label(text, 12) } } }.Init();
            b.Click += a;
            return b;
        }
        list.Children.Add(Action("새 디자인", "plus", () => _ = NewDesign()));
        list.Children.Add(Action("열기...", "file", () => _ = OpenFile()));
        void Section(string title) => list.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted, Margin = new Thickness(12, 14, 12, 4) });
        FlatButton Item(string title, string sub, string icon, Action a)
        {
            var b = new FlatButton
            {
                Padding = new Thickness(12, 6),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        Icons.Color(icon, 26),
                        new StackPanel { Children = { Ui.Label(title, 13), Ui.Label(sub, 11, Ui.Muted) } },
                    },
                },
            }.Init();
            ToolTip.SetTip(b, sub);
            b.Click += a;
            return b;
        }
        Section("최근 문서");
        var recent = Recent();
        if (recent.Count == 0) list.Children.Add(new TextBlock { Text = "아직 없습니다.", FontSize = 11, Foreground = Ui.Muted, Margin = new Thickness(12, 0) });
        foreach (var r in recent)
        {
            var p = r;
            list.Children.Add(Item(Path.GetFileNameWithoutExtension(r), r, "design", () => OpenPath(p)));
        }
        _dataPanel.Child = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private async Task<bool> ConfirmDiscard()
    {
        if (_sk != null) FinishSketch(true);
        CancelCommand();
        if (!_dirty) return true;
        var r = await Ask("OpenShape CAD", "변경 내용을 저장할까요?", "저장", "저장 안 함", "취소");
        return r switch { 0 => await Save(), 1 => true, _ => false };
    }

    public async Task NewDesign()
    {
        if (!await ConfirmDiscard()) return;
        ResetTo(new Doc(), null);
        HomeView();
    }

    private void ResetTo(Doc d, string? path)
    {
        _doc = d;
        _path = path;
        _undo.Clear();
        _redo.Clear();
        _dirty = false;
        _selBodies.Clear();
        _selProfiles.Clear();
        _viewCache.Clear();
        Rebuild();
        if (_workspace == "drawing")
        {
            if (CurrentDrawing == null) SetWorkspace("design");
            else RefreshDrawing(true);
        }
    }

    private async Task OpenFile()
    {
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "열기", FileTypeFilter = new[] { AnyType, ProjectType, CadTypes, MeshTypes } });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } p) OpenPath(p);
    }

    public void OpenPath(string p)
    {
        try
        {
            if (p.EndsWith(".osc", StringComparison.OrdinalIgnoreCase))
            {
                ResetTo(Doc.Load(File.ReadAllText(p)), p);
                AddRecent(p);
            }
            else
            {
                // a STEP/IGES/mesh file opens as a new design that inserts it
                var d = new Doc();
                d.Features.Add(new ImportFeature { Name = "삽입1", Path = p });
                d.Marker = 1;
                ResetTo(d, null);
                if (d.Features[0].Error is { } err) throw new InvalidOperationException(err);
            }
            FitAll();
        }
        catch (Exception ex)
        {
            _ = Ask("열 수 없음", $"{Path.GetFileName(p)}\n{ex.Message}", "확인");
        }
    }

    public async Task<bool> Save()
    {
        if (_sk != null) FinishSketch(true);
        if (_path == null) return await SaveAs();
        return SaveTo(_path);
    }

    public bool SaveTo(string path)
    {
        try
        {
            File.WriteAllText(path, _doc.Save());
            _path = path;
            _dirty = false;
            UpdateTitle();
            RebuildBrowser();
            AddRecent(path);
            // like the IsaacSimLink add-in: optionally re-send to Isaac Sim after every save
            if (AutoSyncOnSave && IsaacLink.AutoSync)
                _ = RunIsaac(IsaacLink.LastMode(DesignName) == "robot" ? "isaac_robot" : "isaac_cad");
            return true;
        }
        catch (Exception ex)
        {
            _ = Ask("저장할 수 없음", ex.Message, "확인");
            return false;
        }
    }

    private async Task<bool> SaveAs()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "다른 이름으로 저장",
            SuggestedFileName = (_path != null ? Path.GetFileNameWithoutExtension(_path) : "디자인") + ".osc",
            DefaultExtension = "osc",
            FileTypeChoices = new[] { ProjectType },
        });
        if (f?.TryGetLocalPath() is not { } p) return false;
        if (!p.EndsWith(".osc", StringComparison.OrdinalIgnoreCase)) p += ".osc";
        return SaveTo(p);
    }

    /// <summary>Tests turn this off so saving never talks to a running Isaac Sim.</summary>
    public bool AutoSyncOnSave { get; set; } = true;

    private static string ConfigDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openshape");

    public static List<string> Recent()
    {
        var f = Path.Combine(ConfigDir, "recent");
        return File.Exists(f) ? File.ReadAllLines(f).Where(File.Exists).Take(10).ToList() : new List<string>();
    }

    private static void AddRecent(string path)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllLines(Path.Combine(ConfigDir, "recent"), new[] { path }.Concat(Recent().Where(p => p != path)).Take(10));
        }
        catch (IOException) { }
    }

    private bool _forceClose;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose || !_dirty) return;
        e.Cancel = true;
        if (await ConfirmDiscard())
        {
            _forceClose = true;
            Close();
        }
    }

    private async Task<int> Ask(string title, string message, params string[] buttons)
    {
        int result = -1;
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            int k = i;
            var b = new Button { Content = buttons[i], MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => { result = k; dlg.Close(); };
            row.Children.Add(b);
        }
        dlg.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, row } };
        await dlg.ShowDialog(this);
        return result;
    }
}
