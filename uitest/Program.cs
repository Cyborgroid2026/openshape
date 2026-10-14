// Headless walkthrough for OpenShape CAD: drives the real MainWindow with simulated
// mouse/keyboard input, checks the model after each step and saves screenshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenShape;

var outDir = args.Length > 0 ? args[0] : "shots";
Directory.CreateDirectory(outDir);
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

// never talk to a running Isaac Sim or write into the real exchange folder from tests
var testExchange = Path.Combine(Path.GetFullPath(outDir), "exchange");
Directory.CreateDirectory(testExchange);
OpenShape.IsaacLink.Exchange = testExchange;
OpenShape.IsaacLink.Port = 1;   // nothing listens there
var w = new MainWindow(Array.Empty<string>()) { Width = 1500, Height = 920, AutoSyncOnSave = false, CheckForUpdates = false };
w.Show();
Pump();
int step = 0;
var failures = new List<string>();

void Pump(int n = 4)
{
    for (int i = 0; i < n; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}

void Shot(string name)
{
    Pump();
    w.View.EnsureRendered();
    w.View.InvalidateVisual();
    Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, $"{++step:00}_{name}.png"));
}

void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures.Add(what);
}

Point Center(Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value;

Point Text(string text) => Center(w.GetVisualDescendants().OfType<TextBlock>().Where(x => x.Text == text && x.IsEffectivelyVisible).LastOrDefault()
                                  ?? throw new Exception($"text not found: {text}"));

Point Tip(string tip) => Center(w.GetVisualDescendants().OfType<Control>().Where(x => ToolTip.GetTip(x) is string t && (t == tip || t.StartsWith(tip + " (")) && x.IsEffectivelyVisible).LastOrDefault()
                                ?? throw new Exception($"tooltip not found: {tip}"));

void Click(Point p)
{
    w.MouseMove(p);
    w.MouseDown(p, MouseButton.Left);
    w.MouseUp(p, MouseButton.Left);
    Pump();
}

void DoubleClick(Point p)
{
    w.MouseMove(p);
    w.MouseDown(p, MouseButton.Left);
    w.MouseUp(p, MouseButton.Left);
    w.MouseDown(p, MouseButton.Left);
    w.MouseUp(p, MouseButton.Left);
    Pump();
}

void Key(Key k) { w.KeyPress(k, RawInputModifiers.None, PhysicalKey.None, null); w.KeyRelease(k, RawInputModifiers.None, PhysicalKey.None, null); Pump(); }

// world point -> window point through the viewport camera
Point World(V3 p) => w.View.TranslatePoint(w.View.Project(p) ?? throw new Exception("behind camera"), w)!.Value;
Point SketchPt(double x, double y) => World(w.Sketch!.Draft.Frame.At(new P2(x, y)));

void SetField(string label, string value)
{
    var box = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Tag as string == label && t.IsEffectivelyVisible);
    box.Text = value;
    Pump();
}

void SetCombo(string label, int index)
{
    var c = w.GetVisualDescendants().OfType<ComboBox>().First(t => t.Tag as string == label && t.IsEffectivelyVisible);
    c.SelectedIndex = index;
    Pump();
}

double Volume() => w.State.Bodies.Sum(b => b.Solid.Info.Volume);
bool Near(double a, double b, double rel = 0.015) => Math.Abs(a - b) <= rel * Math.Abs(b);

if (Environment.GetEnvironmentVariable("OSC_JUMP") == "sketchtools") goto SketchTools;
if (Environment.GetEnvironmentVariable("OSC_JUMP") == "shortcuts") goto Shortcuts;
if (Environment.GetEnvironmentVariable("OSC_JUMP") == "updates") goto Updates;
if (Environment.GetEnvironmentVariable("OSC_JUMP") == "designtools") goto DesignTools;
if (Environment.GetEnvironmentVariable("OSC_JUMP") == "drawing") goto Drawing;
if (Environment.GetEnvironmentVariable("OSC_JUMP") == "render") goto Render;
if (Environment.GetEnvironmentVariable("OSC_JUMP") == "manufacture") goto Manufacture;
Shot("start");
Check(w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "솔리드") && w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "브라우저"),
    "toolbar tabs and browser are shown");

// --- sketch on XY: rectangle with a circular hole
Click(Tip("스케치 작성"));
Check(w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("스케치할 평면을 선택") == true && t.IsEffectivelyVisible), "'스케치 작성' asks for a plane");
Shot("pick_plane");
Click(Text("XY"));
Check(w.InSketch && w.Sketch!.Draft.Plane == "XY", "clicking the XY plane in the browser starts a sketch");
Click(Tip("2점 직사각형"));
Click(SketchPt(-30, -20));
Click(SketchPt(30, 20));
Click(Tip("중심 지름 원"));
Click(SketchPt(0, 0));
Click(SketchPt(8, 0));
Check(w.Sketch!.Draft.Entities.Count == 2, "rectangle and circle drawn on the canvas");
Shot("sketch");
Click(Tip("스케치 마무리"));
Check(!w.InSketch && w.Document.Features.Count == 1, "'스케치 마무리' adds the sketch to the timeline");
var sk1 = (SketchFeature)w.Document.Features[0];
Check(w.State.SketchProfiles[sk1.Id].Count == 2, "sketch has two profiles (plate and hole)");

// --- extrude the plate profile
Click(Tip("돌출"));
Check(w.Command?.Id == "extrude", "extrude dialog opens");
Click(World(sk1.Frame.At(new P2(20, 10))));
Check(w.Command?.Draft is ExtrudeFeature { Profiles.Count: 1 }, "clicking inside the plate selects its profile");
SetField("거리", "15");
w.RefreshPreview();
Shot("extrude_preview");
Click(Text("확인"));
double plate15 = (60 * 40 - Math.PI * 64) * 15;
Check(w.State.Bodies.Count == 1 && Near(Volume(), plate15), $"extrude makes a plate with a hole (volume {Volume():0} ≈ {plate15:0})");
Check(!((SketchFeature)w.Document.Features[0]).Visible, "the consumed sketch is hidden like in Fusion");
w.FitAll();
Shot("extruded");

// --- box primitive cutting the plate
w.RunCommand("box"); Pump();
SetField("길이", "20");
SetField("너비", "20");
SetField("높이", "40");
SetField("X", "30");
SetCombo("생성", 1);
w.RefreshPreview();
Click(Text("확인"));
Check(Near(Volume(), plate15 - 10 * 20 * 15), $"box with '절단' cuts the plate (volume {Volume():0})");
Shot("cut");

// --- parametric edit from the timeline
w.EditFeature(1);
Pump();
Check(w.Command?.Draft is ExtrudeFeature, "double-clicking a timeline item reopens its dialog");
SetField("거리", "20");
w.RefreshPreview();
Click(Text("확인"));
double plate20 = (60 * 40 - Math.PI * 64) * 20 - 10 * 20 * 20;
Check(Near(Volume(), plate20), $"changing the extrude distance re-runs later features (volume {Volume():0} ≈ {plate20:0})");
w.Undo();
Check(Near(Volume(), plate15 - 3000), "undo restores the previous distance");
w.Redo();
Check(Near(Volume(), plate20), "redo re-applies it");

// --- revolve on the front plane
Click(Tip("스케치 작성"));
Click(Text("XZ"));
w.View.Zoom(2.5);
Pump();
Click(Tip("2점 직사각형"));
Click(SketchPt(60, 0));
Click(SketchPt(70, 20));
Click(Tip("스케치 마무리"));
var sk2 = w.Document.Features.OfType<SketchFeature>().Last();
Check(sk2 != sk1 && sk2.Plane == "XZ" && sk2.Entities.Count == 1, "second sketch on the XZ plane");
Click(Tip("회전"));
Click(World(sk2.Frame.At(new P2(65, 10))));
SetCombo("생성", 3);
w.RefreshPreview();
Shot("revolve_preview");
Click(Text("확인"));
double ring = Math.PI * (70 * 70 - 60 * 60) * 20;
Check(w.State.Bodies.Count == 2 && Near(w.State.Bodies[1].Solid.Info.Volume, ring), $"revolve around the sketch Y axis makes a ring (volume {w.State.Bodies[1].Solid.Info.Volume:0} ≈ {ring:0})");
w.FitAll();
Shot("revolved");

// --- select the ring on the canvas and move it up
Click(World(new V3(0, -70, 10)));
Check(w.SelectedBodies.Count == 1, "clicking a body selects it");
Shot("selected");
Click(Tip("이동/복사"));
SetField("Z 거리", "25");
w.RefreshPreview();
Click(Text("확인"));
var moved = w.State.Bodies.Select(b => b.Solid.Info).OrderByDescending(i => i.Max.Z).First();
Check(Math.Abs(moved.Min.Z - 25) < 1e-6, "move/copy lifts the selected body by 25 mm");

// --- appearance and measure
w.RunCommand("appearance");
Pump();
Click(Tip("플라스틱 - 빨간색"));
Check(w.Document.BodyColors.Values.Contains("#D9342B"), "appearance colours the selected body");
Key(Avalonia.Input.Key.Escape);
Click(Tip("측정"));
Check(w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("mm³") == true), "measure shows the volume");
Shot("measure");
Key(Avalonia.Input.Key.Escape);
Key(Avalonia.Input.Key.Escape);

// --- rectangular pattern of the ring
w.RunCommand("rectpattern");
Click(World(new V3(0, -70, 35)));
SetField("수량", "2");
SetField("간격", "200");
w.RefreshPreview();
Click(Text("확인"));
Check(w.State.Bodies.Count == 3, "rectangular pattern copies the ring");
w.FitAll();
Shot("pattern");

// --- export and save/load
foreach (var (fmt, ext) in new[] { (0, "stl"), (1, "obj"), (2, "usda") })
{
    var path = Path.Combine(outDir, "export." + ext);
    Check(w.ExportTo(path, fmt, false) == 3 && new FileInfo(path).Length > 1000, $"export {ext.ToUpperInvariant()} writes all bodies");
}
var usda = File.ReadAllText(Path.Combine(outDir, "export.usda"));
Check(usda.Contains("metersPerUnit = 0.001") && usda.Contains("upAxis = \"Z\""), "USD export is in millimetres with Z up");
double before = Volume();
var proj = Path.Combine(outDir, "design.osc");
Check(w.SaveTo(proj), "design saves to .osc");
w.OpenPath(proj);
Pump();
Check(Near(Volume(), before, 1e-6) && w.Document.Features.Count == 7, "re-opened design rebuilds the same bodies");

// --- rollback with the timeline marker
w.MoveMarker(2);
Check(w.State.Bodies.Count == 1 && Near(Volume(), plate20 + 10 * 20 * 20), "moving the timeline marker rolls the model back");
Shot("rollback");
w.MoveMarker(w.Document.Features.Count);
Check(w.State.Bodies.Count == 3, "moving the marker to the end restores everything");
w.View.SetView(-55, 28);
w.FitAll();
Shot("final_iso");
// ================= sketch constraints and dimensions (SolveSpace) =================
w.MoveMarker(w.Document.Features.Count);
Click(Tip("스케치 작성"));
Click(Text("XY"));
w.View.Cam.Target = new V3(40, 12, 0);
w.View.Cam.Distance = 160;
w.View.Invalidate3D();
Pump();
Click(Tip("선"));
Click(SketchPt(0, 0));
Click(SketchPt(40, 1));
Click(SketchPt(40, 30));
Click(SketchPt(0, 0));
var dk = w.Sketch!.Draft;
Check(dk.Entities.Count == 3 && dk.Constraints.Any(c => c.Kind == "horizontal") && dk.Constraints.Any(c => c.Kind == "vertical"),
    "drawing near-axis lines adds horizontal and vertical constraints automatically");
Check(dk.Constraints.Count(c => c.Kind == "coincident") >= 3, "snapped line ends are joined with coincident constraints");
Click(Tip("스케치 치수"));
Click(SketchPt(20, 0));
Click(SketchPt(20, -12));
Key(Avalonia.Input.Key.Escape);
Check(dk.Constraints.Last() is { Kind: "distance" } d1 && Math.Abs(d1.Value - 40) < 1e-6, "dimension tool measures the line (40 mm)");
w.SetDimension(dk.Constraints.Count - 1, 50);
Check(Math.Abs(dk.Entities[0].P[2] - 50) < 1e-6 && Math.Abs(dk.Entities[1].P[0] - 50) < 1e-6, "changing the dimension to 50 moves the line and the joined vertical line");
Click(Tip("스케치 치수"));
Click(SketchPt(50, 15));
Click(SketchPt(62, 15));
Key(Avalonia.Input.Key.Escape);
w.SetDimension(dk.Constraints.Count - 1, 25);
Check(Math.Abs(dk.Entities[1].P[3] - 25) < 1e-6 && w.Sketch!.Dof == 0, $"triangle is fully constrained after two dimensions (dof {w.Sketch!.Dof})");
Shot("constrained_sketch");

Click(Tip("중심 지름 원"));
Click(SketchPt(35, 8));
Click(SketchPt(40, 8));
Click(Tip("스케치 치수"));
Click(SketchPt(35, 13));
Key(Avalonia.Input.Key.Escape);
w.SetDimension(dk.Constraints.Count - 1, 8);
Check(Math.Abs(dk.Entities[3].P[2] - 4) < 1e-6, "diameter dimension 8 sets the circle radius to 4");
Click(Tip("중심 지름 원"));
Click(SketchPt(44, 14));
Click(SketchPt(47, 14));
Click(Tip("동일"));
Click(SketchPt(35, 12));
Click(SketchPt(44, 17));
Check(Math.Abs(dk.Entities[4].P[2] - 4) < 1e-6, "equal constraint makes the second circle the same size");
Key(Avalonia.Input.Key.Escape);
Check(w.Sketch!.Tool == "", "Escape leaves the constraint tool");
var from = SketchPt(44, 14);
var to = SketchPt(44, 12);
w.MouseMove(from);
w.MouseDown(from, MouseButton.Left);
Pump(1);
foreach (var k in Enumerable.Range(1, 8))
{
    w.MouseMove(from + (to - from) * (k / 8.0), RawInputModifiers.LeftMouseButton);
    Pump(1);
}
w.MouseUp(to, MouseButton.Left);
Pump();
Check(Math.Abs(dk.Entities[4].P[1] - 12) < 0.3 && Math.Abs(dk.Entities[4].P[2] - 4) < 1e-6, $"dragging the circle centre moves it and keeps the equal radius (y {dk.Entities[4].P[1]:0.00})");
Shot("constraints_more");
Click(Tip("스케치 마무리"));
var sk4 = w.Document.Features.OfType<SketchFeature>().Last();
Check(sk4.Constraints.Count >= 9, "constraints are saved with the sketch");
Click(Tip("돌출"));
Click(World(sk4.Frame.At(new P2(30, 5))));
SetField("거리", "10");
SetCombo("생성", 3);
w.RefreshPreview();
Click(Text("확인"));
double tri = (0.5 * 50 * 25 - 2 * Math.PI * 16) * 10;
Check(Near(w.State.Bodies.Last().Solid.Info.Volume, tri, 0.001), $"extruded constrained triangle with two holes: {w.State.Bodies.Last().Solid.Info.Volume:0.0} ≈ {tri:0.0} mm³");

// ================= B-rep features (OpenCASCADE) =================

double BodyVol(int i) => w.State.Bodies[i].Solid.Info.Volume;

// fillet inserted in the middle of the timeline (after the cut), then replayed forward
w.MoveMarker(3);
double plateBefore = BodyVol(0);
w.View.SetView(-55, 28);
w.FitAll();
Pump();
Click(Tip("필렛"));
Check(w.Command?.SelectKind == "edges", "fillet dialog picks edges");
Click(World(new V3(-30, 0, 20)));
Check(w.Command?.Draft is FilletFeature { Edges.Count: 1 }, "clicking an edge selects it for the fillet");
SetField("반지름", "3");
w.RefreshPreview();
Shot("fillet_preview");
Click(Text("확인"));
double fillet = (1 - Math.PI / 4) * 9 * 40;
Check(Near(plateBefore - BodyVol(0), fillet, 0.02), $"fillet r=3 on a 40 mm edge removes {plateBefore - BodyVol(0):0.0} ≈ {fillet:0.0} mm³");

plateBefore = BodyVol(0);
w.RunCommand("chamfer"); Pump();
Click(World(new V3(30, 15, 20)));
SetField("거리", "2");
w.RefreshPreview();
Click(Text("확인"));
Check(Near(plateBefore - BodyVol(0), 0.5 * 4 * 10, 0.02), $"chamfer 2 mm on a 10 mm edge removes {plateBefore - BodyVol(0):0.0} ≈ 20 mm³");

// sketch on the plate's top face and cut a pocket
Click(Tip("스케치 작성"));
Click(World(new V3(-18, 12, 20)));
Check(w.InSketch && w.Sketch!.Draft.Plane == "face", "clicking a planar body face starts a sketch on it");
var ff = w.Sketch!.Draft.Frame;
Check(Math.Abs(ff.N.Z - 1) < 1e-9 && Math.Abs(ff.O.Z - 20) < 1e-9, "face sketch plane is the top face (normal +Z, z = 20)");
var c2 = ff.Project(new V3(-18, 12, 20));
Click(Tip("중심 지름 원"));
Click(SketchPt(c2.X, c2.Y));
Click(SketchPt(c2.X + 4, c2.Y));
Click(Tip("스케치 마무리"));
var sk3 = w.Document.Features.OfType<SketchFeature>().Last(f => f.Plane == "face");
plateBefore = BodyVol(0);
Click(Tip("돌출"));
Click(World(sk3.Frame.At(c2)));
SetField("거리", "-5");
SetCombo("생성", 1);
w.RefreshPreview();
Click(Text("확인"));
Check(Near(plateBefore - BodyVol(0), Math.PI * 16 * 5, 0.01), $"pocket cut from the face sketch removes {plateBefore - BodyVol(0):0} ≈ {Math.PI * 80:0} mm³");
Shot("face_pocket");

// replay the rest of the timeline on top of the inserted features
w.MoveMarker(w.Document.Features.Count);
Check(w.State.Bodies.Count == 4 && w.Document.Features.All(f => f.Error == null), "later features still rebuild after inserting fillet/chamfer/pocket");

// shell a new box from its top face
w.RunCommand("box"); Pump();
SetField("길이", "30");
SetField("너비", "30");
SetField("높이", "30");
SetField("X", "200");
w.RefreshPreview();
Click(Text("확인"));
w.FitAll();
Pump();
Click(Tip("쉘"));
w.View.SetView(-55, 40);
Pump();
Click(World(new V3(200, 0, 30)));
Check(w.Command?.Draft is ShellFeature { Faces.Count: 1 }, "clicking the box top selects the face to remove");
SetField("내부 두께", "2");
w.RefreshPreview();
Click(Text("확인"));
var boxVol = w.State.Bodies.Last().Solid.Info.Volume;
Check(Near(boxVol, 27000 - 26 * 26 * 28, 0.001), $"shell 2 mm leaves {boxVol:0} ≈ {27000 - 26 * 26 * 28} mm³");
w.FitAll();
Shot("shell");

// STEP round trip
var stepFile = Path.Combine(outDir, "design.step");
int nb = w.State.Bodies.Count;
double total = Volume();
Check(w.ExportTo(stepFile, 3, false) == nb && new FileInfo(stepFile).Length > 1000, "STEP export writes every body");
w.InsertFilePath(stepFile);
Check(w.State.Bodies.Count == nb * 2 && Near(Volume(), total * 2, 1e-4), "re-imported STEP has the same exact bodies");



// ================= components and joints =================
w.SaveTo(Path.Combine(outDir, "before_joints.osc"));
await w.NewDesign();
Pump();
Check(w.State.Bodies.Count == 0, "new design starts empty");
void Box(double l, double wd, double h, double x)
{
    w.RunCommand("box");
    Pump();
    SetField("길이", l.ToString());
    SetField("너비", wd.ToString());
    SetField("높이", h.ToString());
    SetField("X", x.ToString());
    w.RefreshPreview();
    Click(Text("확인"));
}
Box(60, 60, 10, 0);
Box(10, 10, 40, 100);
var plateId = w.State.Bodies[0].Id;
var armId = w.State.Bodies[1].Id;
w.FitAll();
Pump();
Click(Text(w.BodyName(plateId)));
w.RunCommand("newcomponent");
Pump();
Click(Text(w.BodyName(armId)));
w.RunCommand("newcomponent");
Pump();
Check(w.Document.Components.Count == 2 && w.Document.Components[0].Grounded, "bodies become components (the first one grounded)");
w.RunCommand("joint");
Pump();
Check(w.Command?.SelectKind == "jointA", "joint dialog asks for component 1's origin");
w.View.SetView(-55, -30);
Pump();
Click(World(new V3(100, 0, 0)));
w.View.SetView(-55, 35);
Pump();
Click(World(new V3(10, 10, 10)));
Check(w.Command?.Draft is JointFeature { PickA.Length: 3, PickB.Length: 3 }, "both joint origins picked on faces");
SetCombo("유형", 1);
SetField("각도", "45");
w.RefreshPreview();
Shot("joint_preview");
Click(Text("확인"));
var arm = w.State.Find(armId)!.Solid.Info;
Check(Math.Abs(arm.Min.Z - 10) < 1e-6 && Math.Abs((arm.Min.X + arm.Max.X) / 2) < 1e-6 && Math.Abs(arm.Max.X - arm.Min.X - 10 * Math.Sqrt(2)) < 1e-3,
    $"revolute joint seats the arm on the plate centre, turned 45° (z {arm.Min.Z:0.###}, width {arm.Max.X - arm.Min.X:0.###})");
Check(w.State.Joints.Count == 1 && w.State.Joints[0].Type == "revolute", "the joint is listed for the browser and export");
w.EditFeature(w.Document.Features.Count - 1);
Pump();
SetField("각도", "90");
w.RefreshPreview();
Click(Text("확인"));
arm = w.State.Find(armId)!.Solid.Info;
Check(Math.Abs(arm.Max.X - arm.Min.X - 10) < 1e-3, "driving the joint to 90° turns the arm square again");
w.View.SetView(-55, 28);
w.FitAll();
Shot("joint");
var usdPhys = Path.Combine(outDir, "robot.usda");
Check(w.ExportTo(usdPhys, 4, false) == 2, "USD with physics exports both components");
var usdText = File.ReadAllText(usdPhys);
Check(usdText.Contains("PhysicsRevoluteJoint") && usdText.Contains("PhysicsRigidBodyAPI") && usdText.Contains("PhysicsFixedJoint") && usdText.Contains("physics:body0 = </World/"),
    "USD has rigid bodies, the revolute joint and the grounded base for Isaac Sim");

// ================= loft, sweep, surfaces, sheet metal, plastic =================
async Task Fresh(string tag)
{
    w.SaveTo(Path.Combine(outDir, $"before_{tag}.osc"));
    await w.NewDesign();
    Pump();
}
void Iso()
{
    w.View.SetView(-55, 30);
    w.FitAll();
    Pump();
}
void SketchOn(string plane, double offset = 0)
{
    Click(Tip("스케치 작성"));
    Click(Text(plane));
    if (offset != 0) SetField("평면 간격띄우기", offset.ToString());
    w.View.Cam.Target = w.Sketch!.Draft.Frame.At(new P2(0, 0));
    w.View.Cam.Distance = 200;
    w.View.Invalidate3D();
    Pump();
}
void Shape(string tool, params (double x, double y)[] pts)
{
    w.RunCommand(tool);
    Pump();
    foreach (var (x, y) in pts) Click(SketchPt(x, y));
    Key(Avalonia.Input.Key.Escape);
}
void Finish() => Click(Tip("스케치 마무리"));
SketchFeature LastSketch() => w.Document.Features.OfType<SketchFeature>().Last();
void ClickProfile(SketchFeature sk, double x, double y) => Click(World(sk.Frame.At(new P2(x, y))));
void Ok() { w.RefreshPreview(); Click(Text("확인")); }

// loft square -> circle
await Fresh("loft");
SketchOn("XY");
Shape("crect", (0, 0), (20, 20));
Finish();
var la = LastSketch();
SketchOn("XY", 30);
Shape("circle", (0, 0), (10, 0));
Finish();
var lb = LastSketch();
Iso();
w.RunCommand("loft");
Pump();
ClickProfile(la, 15, -15);
ClickProfile(lb, 0, 0);
Ok();
var loftI = w.State.Bodies.Last().Solid.Info;
Check(w.State.Bodies.Count == 1 && loftI.Volume > Math.PI * 100 * 30 && loftI.Volume < 1600 * 30 && Math.Abs(loftI.Max.Z - 30) < 1e-6, $"loft from a square to a circle ({loftI.Volume:0} mm³)");
Shot("loft");

// sweep a circle along a line
await Fresh("sweep");
SketchOn("XZ");
Shape("circle", (0, 0), (3, 0));
Finish();
var sp = LastSketch();
SketchOn("XY");
Shape("line", (0, 0), (0, 50));
Finish();
var pathSk = LastSketch();
Iso();
w.RunCommand("sweep");
Pump();
ClickProfile(sp, 0, 0);
Check(w.Command?.SelectKind == "path", "sweep asks for the path after the profile");
Click(World(pathSk.Frame.At(new P2(0, 30))));
Ok();
Check(Near(w.State.Bodies.Last().Solid.Info.Volume, Math.PI * 9 * 50, 0.005), $"sweep of r=3 along 50 mm ({w.State.Bodies.Last().Solid.Info.Volume:0.0} ≈ {Math.PI * 450:0.0})");

// surface extrude, thicken, patch
await Fresh("surface");
SketchOn("XY");
Shape("line", (0, 0), (30, 0));
Shape("circle", (0, 20), (5, 20));
Finish();
var ss = LastSketch();
Iso();
w.RunCommand("surfextrude");
Pump();
Click(World(ss.Frame.At(new P2(15, 0))));
SetField("거리", "20");
Ok();
var surf = w.State.Bodies.Last().Solid.Info;
Check(surf.Solids == 0 && Near(surf.Area, 600, 1e-6), $"surface extrude makes a 600 mm² surface body (area {surf.Area:0.0})");
Click(Text(w.BodyName(w.State.Bodies.Last().Id)));
w.RunCommand("thicken");
Pump();
SetField("두께", "2");
Ok();
Check(Near(w.State.Bodies.Last().Solid.Info.Volume, 1200, 0.01), $"thicken 2 mm turns it into a 1200 mm³ solid ({w.State.Bodies.Last().Solid.Info.Volume:0.0})");
w.RunCommand("patch");
Pump();
ss = w.Document.Features.OfType<SketchFeature>().Last();
Click(World(ss.Frame.At(new P2(5, 20))));
Ok();
Check(Near(w.State.Bodies.Last().Solid.Info.Area, Math.PI * 25, 1e-4), "patch fills the circle with a flat surface");
Shot("surfaces");

// press/pull
await Fresh("presspull");
Box(20, 20, 20, 0);
Iso();
w.RunCommand("presspull");
Pump();
Click(World(new V3(3, 3, 20)));
SetField("거리", "5");
Ok();
Check(Near(Volume(), 20 * 20 * 25, 1e-6), "press/pull raises the top face by 5 mm");

// sheet metal: base flange + edge flange
await Fresh("sheet");
SketchOn("XY");
Shape("crect", (0, 0), (20, 15));
Finish();
var sm = LastSketch();
Iso();
w.RunCommand("flange");
Pump();
ClickProfile(sm, 5, 5);
Ok();
Check(Near(Volume(), 40 * 30 * 1, 1e-6), "base flange is the profile at sheet thickness (1 mm)");
w.RunCommand("edgeflange");
Pump();
Click(World(new V3(0, -15, 1)));
SetField("높이", "10");
Ok();
double flangeAdd = Math.PI / 4 * (4 - 1) * 40 + 1 * 9 * 40;
Check(Near(Volume() - 1200, flangeAdd, 0.01), $"edge flange adds the bend and wall ({Volume() - 1200:0.0} ≈ {flangeAdd:0.0} mm³)");
Iso();
Shot("sheet_metal");

// plastic: rib and boss
await Fresh("plastic");
Box(40, 40, 5, 0);
SketchOn("XZ");
Shape("line", (-15, 25), (15, 25));
Finish();
var rs = LastSketch();
Iso();
w.RunCommand("rib");
Pump();
Click(World(rs.Frame.At(new P2(0, 25))));
SetField("두께", "2");
SetField("깊이", "20");
Ok();
Check(Near(Volume(), 8000 + 30 * 20 * 2, 1e-4), $"rib adds a 30×20×2 wall ({Volume():0})");
w.RunCommand("boss");
Pump();
Click(World(new V3(12, -12, 5)));
SetField("높이", "10");
Ok();
double bossAdd = Math.PI * 16 * 10 - Math.PI * 2.25 * 15;
Check(Near(Volume() - 9200, bossAdd, 0.01), $"boss with a pilot hole ({Volume() - 9200:0.0} ≈ {bossAdd:0.0} mm³)");
Iso();
Shot("plastic");

// section analysis (display only)
w.RunCommand("section");
Pump();
SetCombo("평면", 2);
Pump();
double shown = w.View.Items.Sum(i => i.Solid.Info.Volume);
Check(shown < Volume() * 0.8 && shown > 0, "section analysis shows only one side of the cut");
Shot("section");
Click(Text("취소"));
Check(Math.Abs(w.View.Items.Sum(i => i.Solid.Info.Volume) - Volume()) < 1e-6, "cancelling turns the section off");

// ================= parameters =================
await Fresh("params");
w.RunCommand("parameters");
Pump();
Click(Text("사용자 매개변수 추가"));
SetField("이름1", "width");
SetField("식1", "40 mm");
Click(Text("확인"));
Check(w.Document.Parameters.Count == 1 && w.Document.Parameters[0].Name == "width", "a user parameter is added in 매개변수 관리");
w.RunCommand("box");
Pump();
SetField("길이", "width");
w.RefreshPreview();
Click(Text("확인"));
Check(Near(Volume(), 40 * 20 * 20, 1e-9) && w.Document.Features.Last().Expressions.ContainsKey("A"), "a field typed as 'width' uses the parameter and keeps the expression");
w.RunCommand("parameters");
Pump();
SetField("식1", "width_base * 2");
Click(Text("사용자 매개변수 추가"));
SetField("이름2", "width_base");
SetField("식2", "25");
Click(Text("확인"));
Check(Near(Volume(), 50 * 20 * 20, 1e-9), "changing the parameter (now a formula of another one) rebuilds the box at 50 mm");

// ================= trim, tangent, symmetric =================
SketchTools:
await Fresh("sketchtools");
SketchOn("XY");
Shape("line", (-20, 0), (20, 0));
Shape("line", (0, -20), (0, 20));
Shape("circle", (30, 0), (38, 0));
var tk = w.Sketch!.Draft;
w.RunCommand("trim");
Pump();
Click(SketchPt(10, 0));
var horizontal = tk.Entities.Where(e => e.Kind == "line" && Math.Abs(e.P[1]) < 1e-9 && Math.Abs(e.P[3]) < 1e-9).ToList();
Check(horizontal.Count == 1 && Math.Abs(Math.Max(horizontal[0].P[0], horizontal[0].P[2])) < 1e-6, "trim removes the half of the line beyond the crossing");
Key(Avalonia.Input.Key.Escape);
Shape("circle", (-15, 12), (-11, 12));
w.RunCommand("c_tangent");
Pump();
Click(SketchPt(-10, 0));
Click(SketchPt(-15, 16));
var tc = tk.Entities.Last();
Check(tk.Constraints.Any(c => c.Kind == "tangent") && Math.Abs(Math.Abs(tc.P[1]) - tc.P[2]) < 1e-6, $"tangent makes the circle touch the line (centre y {tc.P[1]:0.###}, r {tc.P[2]:0.###})");
Shape("circle", (-8, -10), (-6, -10));
Shape("circle", (9, -13), (11, -13));
w.RunCommand("c_symmetric");
Pump();
Click(SketchPt(-8, -10));
Click(SketchPt(9, -13));
Click(SketchPt(0, -17));
var sc1 = tk.Entities[^2];
var sc2 = tk.Entities[^1];
Check(Math.Abs(sc1.P[0] + sc2.P[0]) < 1e-6 && Math.Abs(sc1.P[1] - sc2.P[1]) < 1e-6, "symmetric mirrors the two centres about the vertical line");
Shot("sketch_tools");
Finish();

// ================= more joints =================
await Fresh("joints2");
Box(60, 60, 10, 0);
Box(10, 10, 40, 100);
var plate2 = w.State.Bodies[0].Id;
var arm2 = w.State.Bodies[1].Id;
w.FitAll();
Pump();
w.RunCommand("joint");
Pump();
w.View.SetView(-55, -30);
Pump();
Click(World(new V3(100, 0, 0)));
w.View.SetView(-55, 35);
Pump();
Click(World(new V3(10, 10, 10)));
SetCombo("유형", Array.IndexOf(JointFeature.Types, "planar"));
SetField("X 거리", "12");
SetField("Y 거리", "-7");
w.RefreshPreview();
Click(Text("확인"));
var armP = w.State.Find(arm2)!.Solid.Info;
var u = JointFeature.Perp(V3.UnitZ);
var vdir = V3.UnitZ.Cross(u);
var expect = u * 12 + vdir * -7;
Check(Math.Abs((armP.Min.X + armP.Max.X) / 2 - expect.X) < 1e-6 && Math.Abs((armP.Min.Y + armP.Max.Y) / 2 - expect.Y) < 1e-6, "planar joint slides the arm in the plate plane");
w.EditFeature(w.Document.Features.Count - 1);
Pump();
SetCombo("유형", Array.IndexOf(JointFeature.Types, "ball"));
SetField("요", "90");
w.RefreshPreview();
Click(Text("확인"));
armP = w.State.Find(arm2)!.Solid.Info;
Check(armP.Max.Z - armP.Min.Z < 15, "ball joint tips the arm over (yaw 90°)");

// ================= flat pattern + DXF =================
w.OpenPath(Path.Combine(outDir, "before_plastic.osc"));
Pump();
var sheetBody = w.State.Sheets.Keys.First();
var flat = w.FlatPatternOf(sheetBody)!;
double dev = Math.PI / 2 * (1 + 0.44) + (10 - 1 - 1);
Check(Near(flat.Info.Volume, 1200 + 40 * dev * 1, 1e-6), $"flat pattern unfolds the flange ({flat.Info.Volume:0.0} ≈ {1200 + 40 * dev:0.0} mm³)");
var dxfPath = Path.Combine(outDir, "flat.dxf");
Check(w.ExportDxf(sheetBody, dxfPath) && File.ReadAllText(dxfPath).Split("LINE").Length > 6, "flat pattern outline is written as DXF");
w.RunCommand("flatpattern");
Pump();
Iso();
Shot("flat_pattern");
Key(Avalonia.Input.Key.Escape);

// ================= IsaacSimLink (offline) =================
w.OpenPath(Path.Combine(outDir, "before_loft.osc"));
Pump();
var urdfPath = OpenShape.IsaacLink.WriteUrdf("test_robot", w.State, w.Document, w.BodyName, out var urdfWarn);
var urdf = System.Xml.Linq.XDocument.Load(urdfPath);
var linksX = urdf.Root!.Elements("link").ToList();
var jointX = urdf.Root.Elements("joint").ToList();
double massArm = linksX.Select(l => double.Parse(l.Element("inertial")?.Element("mass")?.Attribute("value")?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture)).Min();
Check(linksX.Count == 2 && jointX.Count == 1 && jointX[0].Attribute("type")!.Value == "continuous" && jointX[0].Element("axis")!.Attribute("xyz")!.Value == "0 0 1",
    "URDF has two links and the revolute joint (continuous, Z axis)");
Check(Math.Abs(massArm - 4000 * 7.85e-6) < 1e-9 && linksX.All(l => File.Exists(Path.Combine(Path.GetDirectoryName(urdfPath)!, l.Element("visual")!.Element("geometry")!.Element("mesh")!.Attribute("filename")!.Value))),
    $"URDF links carry steel mass ({massArm:0.#####} kg) and STL meshes");
var statusTask = w.RunIsaac("isaac_status");
for (int k = 0; k < 500 && !statusTask.IsCompleted; k++) { Pump(1); Thread.Sleep(10); }
var status = statusTask.IsCompleted ? statusTask.Result : "(timeout)";
Check(status.Contains("연결할 수 없습니다"), "without Isaac Sim running, the link reports that it cannot connect");
Key(Avalonia.Input.Key.Escape);

// ================= data panel =================
w.ToggleDataPanel();
Pump();
Check(w.DataPanelVisible && w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "데이터 패널"), "the data panel opens from the application bar");
Shot("data_panel");
w.ToggleDataPanel();

// ================= keyboard shortcuts =================
Shortcuts:
await Fresh("shortcuts");
w.View.Focus();
Key(Avalonia.Input.Key.L);
Check(w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("스케치할 평면을 선택") == true && t.IsEffectivelyVisible), "L outside a sketch asks for a plane first");
Click(Text("XY"));
Pump();
Check(w.InSketch && w.Sketch!.Tool == "line", "…then starts the line tool on the chosen plane");
Key(Avalonia.Input.Key.R);
Check(w.Sketch!.Tool == "rect", "R switches to the rectangle tool");
w.View.Cam.Distance = 200;
w.View.Invalidate3D();
Pump();
Click(SketchPt(-10, -10));
Click(SketchPt(10, 10));
Key(Avalonia.Input.Key.D);
Check(w.Sketch!.Tool == "dimension", "D is the sketch dimension tool");
Key(Avalonia.Input.Key.E);
Pump();
Check(!w.InSketch && w.Command?.Id == "extrude", "E inside a sketch finishes it and starts extrude");
Key(Avalonia.Input.Key.Escape);
Key(Avalonia.Input.Key.S);
Pump();
Check(w.ShortcutBoxOpen, "S opens the command search box");
var sbox = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Tag as string == "shortcutbox");
sbox.Text = "필렛";
Pump();
sbox.Focus();
w.KeyPress(Avalonia.Input.Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
w.KeyRelease(Avalonia.Input.Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
Pump();
Check(!w.ShortcutBoxOpen && w.Command?.Id == "fillet", "typing '필렛' and Enter runs fillet");
Key(Avalonia.Input.Key.Escape);
Box(10, 10, 10, 50);
Click(Text(w.BodyName(w.State.Bodies.Last().Id)));
w.View.Focus();
Key(Avalonia.Input.Key.V);
Check(w.Document.HiddenBodies.Contains(w.State.Bodies.Last().Id), "V hides the selected body");
Key(Avalonia.Input.Key.V);
w.KeyPress(Avalonia.Input.Key.C, RawInputModifiers.Control, PhysicalKey.None, null);
w.KeyRelease(Avalonia.Input.Key.C, RawInputModifiers.Control, PhysicalKey.None, null);
w.KeyPress(Avalonia.Input.Key.V, RawInputModifiers.Control, PhysicalKey.None, null);
w.KeyRelease(Avalonia.Input.Key.V, RawInputModifiers.Control, PhysicalKey.None, null);
Pump();
Check(w.Command?.Draft is MoveFeature { CreateCopy: true, Bodies.Count: 1 }, "Ctrl+C / Ctrl+V opens Move/Copy with a copy of the body");
SetField("X 거리", "30");
w.RefreshPreview();
Click(Text("확인"));
Check(w.State.Bodies.Count == 2, "the pasted copy is a new body");
Key(Avalonia.Input.Key.I);
Check(w.Command?.Id == "measure", "I opens measure");
Key(Avalonia.Input.Key.Escape);

// ---- updates: bell notification + OpenShape Client Downloader window
Updates:
Click(Tip("알림 센터"));
Pump();
Check(w.Command?.Id == "notifications" && w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("새 알림이 없습니다") == true && t.IsEffectivelyVisible), "bell opens the notification centre (no updates)");
Key(Avalonia.Input.Key.Escape);
w.ApplyUpdateCheck("latest=9.9.9 installed=1.1.0 asset=openshape_9.9.9_amd64.deb size=1 update=True");
Pump();
Check(w.UpdateAvailable, "installer --check output with update=True lights the bell");
Click(Tip("알림 센터"));
Pump();
Check(w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "OpenShape CAD 9.9.9을(를) 설치할 수 있습니다." && t.IsEffectivelyVisible), "notification names the new version");
Shot("update_notification");
Key(Avalonia.Input.Key.Escape);
Check(OpenShapeInstaller.Updater.IsNewer("1.1.0", "1.0.1") && !OpenShapeInstaller.Updater.IsNewer("1.0.1", "1.1.0") && !OpenShapeInstaller.Updater.IsNewer("1.1.0", "1.1.0") && OpenShapeInstaller.Updater.IsNewer("1.1.0", null), "installer version comparison");
var iw = new OpenShapeInstaller.InstallerWindow();
iw.Show();
iw.Progress("설치 프로그램 다운로드 중", 42);
iw.Detail.Text = "14.7 MB / 35.0 MB";
Pump(8);
iw.CaptureRenderedFrame()?.Save(Path.Combine(outDir, $"{++step:00}_client_downloader.png"));
Check(iw.Title == "OpenShape CAD 클라이언트 다운로더" && iw.Bar.Value == 42, "client downloader window opens with the progress bar");
_ = iw.AskLaunch();
Pump(8);
iw.CaptureRenderedFrame()?.Save(Path.Combine(outDir, $"{++step:00}_client_downloader_latest.png"));
Check(iw.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "최신 버전이 이미 설치되어 있습니다."), "'latest version already installed' dialog");
_ = iw.ShowError("관리자 인증이 취소되어 설치하지 못했습니다.", "설치 프로그램을 다시 실행하고 관리자 암호를 입력하십시오.");
Pump(8);
iw.CaptureRenderedFrame()?.Save(Path.Combine(outDir, $"{++step:00}_client_downloader_error.png"));
Check(OpenShapeInstaller.NativeCore.Available && OpenShapeInstaller.NativeCore.CompareVersions("1.0.10", "1.0.9") == 1
      && OpenShapeInstaller.NativeCore.CompareVersions("1.2.0~rc1", "1.2.0") == -1, "Rust installer core compares Debian versions");
var shaFile = Path.Combine(outDir, "abc.txt");
File.WriteAllText(shaFile, "abc");
Check(OpenShapeInstaller.NativeCore.Sha256File(shaFile) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "Rust installer core computes SHA-256");
iw.Close();

// ------------------------------------------------------------------ design tools added for Fusion parity
DesignTools:
int loadCount = 0;
void Load(Doc d)
{
    d.Marker = d.Features.Count;
    var file = Path.Combine(outDir, $"design_{++loadCount}.osc");
    File.WriteAllText(file, d.Save());
    w.OpenPath(file);
    Pump();
}
SketchFeature LineSketch(string plane, params double[][] lines) =>
    new() { Name = "스케치" + lines.Length, Plane = plane, Entities = lines.Select(l => new SketchEntity { Kind = "line", P = l }).ToList(), Visible = false };
double BoxVol = 40 * 40 * 20;
Doc BoxDoc() => new() { Features = { new PrimitiveFeature { Name = "상자1", Shape = "box", A = 40, B = 40, C = 20 } } };

// hole: click the top face, through all
Load(BoxDoc());
w.RunCommand("hole");
Check(w.Command?.Draft is HoleFeature, "hole dialog opens");
Click(World(new V3(5, 5, 20)));
Check(w.Command?.Draft is HoleFeature { Points.Count: 1 }, "clicking the top face places a hole point");
SetCombo("범위", 1);
SetField("지름", "6");
w.RefreshPreview();
Shot("hole_preview");
Click(Text("확인"));
Check(w.State.Bodies.Count == 1 && Near(Volume(), BoxVol - Math.PI * 9 * 20), $"through hole removes π·3²·20 (volume {Volume():0})");

// counterbore hole with flat bottom, evaluated from the model
var dt_cb = BoxDoc();
dt_cb.Features.Add(new HoleFeature { Name = "구멍1", Body = dt_cb.Features[0].Id + ":0", Points = { new[] { 0.0, 0.0, 20.0 } }, Type = "counterbore", Diameter = 6, Depth = 12, DrillPoint = false, CounterDiameter = 10, CounterDepth = 4 });
Load(dt_cb);
double dt_cbCut = Math.PI * 25 * 4 + Math.PI * 9 * 8;
Check(dt_cb.Features[1].Error == null && Near(Volume(), BoxVol - dt_cbCut), $"counterbore hole volume ({Volume():0} ≈ {BoxVol - dt_cbCut:0})");

// split body by the XY plane at half height
var dt_sp = BoxDoc();
dt_sp.Features.Add(new SplitBodyFeature { Name = "본체 분할1", Bodies = { dt_sp.Features[0].Id + ":0" }, Plane = "XY", Offset = 10 });
Load(dt_sp);
Check(w.State.Bodies.Count == 2 && w.State.Bodies.All(b => Near(b.Solid.Info.Volume, BoxVol / 2)), "split body makes two halves");

// draft the four side faces by 5° about the dt_bottom face
var dt_dr = BoxDoc();
dt_dr.Features.Add(new DraftFeature
{
    Name = "기울기1", Body = dt_dr.Features[0].Id + ":0", Neutral = new[] { 0.0, 0.0, 0.0 }, Angle = 5,
    Faces = { new[] { 20.0, 0, 10 }, new[] { -20.0, 0, 10 }, new[] { 0, 20.0, 10 }, new[] { 0, -20.0, 10 } },
});
Load(dt_dr);
// the top shrinks to (40 - 2·20·tan5°)²: a dt_frustum of a square pyramid
double dt_top = 40 - 2 * 20 * Math.Tan(5 * Math.PI / 180);
double dt_frustum = 20.0 / 3 * (1600 + dt_top * dt_top + 40 * dt_top);
Check(dt_dr.Features[1].Error == null && Near(Volume(), dt_frustum, 0.005), $"draft tapers the sides ({Volume():0} ≈ {dt_frustum:0})");

// coil
var dt_co = new Doc { Features = { new CoilFeature { Name = "코일1", Diameter = 30, Pitch = 8, Turns = 3, Section = 3 } } };
Load(dt_co);
double dt_helix = 3 * Math.Sqrt(Math.Pow(2 * Math.PI * 15, 2) + 64);
Check(dt_co.Features[0].Error == null && Near(Volume(), Math.PI * 1.5 * 1.5 * dt_helix, 0.02), $"coil volume ({Volume():0} ≈ {Math.PI * 2.25 * dt_helix:0})");
w.FitAll();
Shot("coil");

// pipe along an L-shaped path, hollow
var dt_pathSk = LineSketch("XZ", new[] { 0.0, 0, 50, 0 }, new[] { 50.0, 0, 50, 40 });
var dt_pd = new Doc { Features = { dt_pathSk, new PipeFeature { Name = "파이프1", Chains = { new ChainRef(dt_pathSk.Id, 0) }, Diameter = 8, Hollow = true, Thickness = 1, Operation = BodyOp.NewBody } } };
Load(dt_pd);
Check(dt_pd.Features[1].Error == null && w.State.Bodies.Count == 1 && Volume() > Math.PI * (16 - 9) * 80 && Volume() < Math.PI * (16 - 9) * 95, $"hollow pipe follows the path ({Volume():0} mm³)");
w.FitAll();
Shot("pipe");

// surface revolve: a vertical line 10 from the axis makes a cylinder surface
var dt_rs = LineSketch("XZ", new[] { 10.0, 0, 10, 30 });
var dt_rd = new Doc { Features = { dt_rs, new SurfaceFeature { Name = "회전1", Kind = "revolve", Chains = { new ChainRef(dt_rs.Id, 0) }, Axis = 1, Angle = 360 } } };
Load(dt_rd);
Check(dt_rd.Features[1].Error == null && w.State.Bodies.Count == 1 && Near(w.State.Bodies[0].Solid.Info.Area, 2 * Math.PI * 10 * 30), "surface revolve makes a cylindrical surface");

// surface loft between two parallel lines makes a flat strip
var dt_l1 = LineSketch("XY", new[] { 0.0, 0, 20, 0 });
var dt_l2 = new SketchFeature { Name = "스케치2", Plane = "XY", Offset = 10, Entities = { new SketchEntity { Kind = "line", P = new[] { 0.0, 0, 20, 0 } } }, Visible = false };
var dt_ld = new Doc { Features = { dt_l1, dt_l2, new SurfaceFeature { Name = "로프트1", Kind = "loft", Chains = { new ChainRef(dt_l1.Id, 0), new ChainRef(dt_l2.Id, 0) } } } };
Load(dt_ld);
Check(dt_ld.Features[2].Error == null && Near(w.State.Bodies.Single().Solid.Info.Area, 200), "surface loft between two lines");

// stitch: an open square dt_tube plus dt_top and dt_bottom patches closes into a solid
var dt_sq = new SketchFeature { Name = "스케치1", Plane = "XY", Entities = { new SketchEntity { Kind = "rect", P = new[] { 0.0, 0, 20, 20 } } }, Visible = false };
var dt_sqTop = new SketchFeature { Name = "스케치2", Plane = "XY", Offset = 15, Entities = { new SketchEntity { Kind = "rect", P = new[] { 0.0, 0, 20, 20 } } }, Visible = false };
var dt_tube = new SurfaceFeature { Name = "돌출1", Chains = { new ChainRef(dt_sq.Id, 0) }, Distance = 15 };
var dt_bottom = new SurfaceFeature { Name = "패치1", Patch = true, Chains = { new ChainRef(dt_sq.Id, 0) } };
var dt_topPatch = new SurfaceFeature { Name = "패치2", Patch = true, Chains = { new ChainRef(dt_sqTop.Id, 0) } };
var dt_st = new Doc { Features = { dt_sq, dt_sqTop, dt_tube, dt_bottom, dt_topPatch } };
dt_st.Features.Add(new StitchFeature { Name = "스티치1", Bodies = { dt_tube.Id + ":0", dt_bottom.Id + ":0", dt_topPatch.Id + ":0" } });
Load(dt_st);
Check(dt_st.Features[^1].Error == null && w.State.Bodies.Count == 1 && w.State.Bodies[0].Solid.Info.Solids == 1 && Near(Volume(), 20 * 20 * 15), $"stitch closes surfaces into a solid ({Volume():0})");

// interference between two overlapping boxes
var dt_it = new Doc
{
    Features =
    {
        new PrimitiveFeature { Name = "상자1", Shape = "box", A = 20, B = 20, C = 20 },
        new PrimitiveFeature { Name = "상자2", Shape = "box", X = 10, A = 20, B = 20, C = 20 },
    },
};
Load(dt_it);
w.RunCommand("interference");
Pump();
Check(w.State.Bodies.Count == 2 && w.Interferences(w.State.Bodies).Count == 1 && Near(w.Interferences(w.State.Bodies)[0].common.Info.Volume, 10 * 20 * 20), "interference finds the 10×20×20 overlap");
Check(w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "간섭 1건" && t.IsEffectivelyVisible), "interference panel lists one clash");
Shot("interference");
Key(Avalonia.Input.Key.Escape);

// centre of mass: steel box 40×40×20 = 32 cm³ · 7.85 = 251.2 g at (0, 0, 10)
Load(BoxDoc());
w.RunCommand("centerofmass");
Pump();
Check(w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "251.2 g" && t.IsEffectivelyVisible), "centre of mass panel shows the steel mass");
Check(w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "10 mm" && t.IsEffectivelyVisible), "centre of mass height is 10 mm");
Shot("center_of_mass");
Key(Avalonia.Input.Key.Escape);

// ------------------------------------------------------------------ drawing workspace
Drawing:
var dwgDoc = new Doc { Features = { new PrimitiveFeature { Name = "상자1", Shape = "box", A = 60, B = 40, C = 20 } } };
dwgDoc.Features.Add(new HoleFeature { Name = "구멍1", Body = dwgDoc.Features[0].Id + ":0", Points = { new[] { 0.0, 0.0, 20.0 } }, Diameter = 10, ThroughAll = true });
dwgDoc.Marker = 2;
var dwgFile = Path.Combine(outDir, "drawing_part.osc");
File.WriteAllText(dwgFile, dwgDoc.Save());
w.OpenPath(dwgFile);
Pump();
Click(Tip("작업공간 변경"));
Click(Text("도면"));
Pump(8);
Check(w.Workspace == "drawing" && w.SheetView.IsEffectivelyVisible && !w.View.IsEffectivelyVisible, "workspace menu switches to the drawing sheet");
Check(w.CurrentDrawing != null && w.Command?.Id == "dwg_base", "a new drawing opens the base view dialog");
Click(Text("확인"));
var dwg = w.CurrentDrawing!;
Check(dwg.Views.Count == 1 && dwg.Views[0].Orientation == "front", "base view (front) is placed");
Point SheetPt(P2 sheet) => w.SheetView.TranslatePoint(w.SheetView.ToScreen(sheet), w)!.Value;
Point ViewPt(DrawingViewDef v, double x, double y) => SheetPt(DrawingRenderer.ToSheet(dwg, v, w.GeometryOf(v), new P2(x, y)));
var front = dwg.Views[0];
// projected views: click right of and above the base view (third angle)
Click(SheetPt(new P2(front.X + 110, front.Y + 3)));
Click(SheetPt(new P2(front.X - 2, front.Y + 80)));
Click(SheetPt(new P2(front.X + 110, front.Y + 80)));
Key(Avalonia.Input.Key.Escape);
Check(dwg.Views.Count == 4, $"three projected views added ({dwg.Views.Count - 1})");
var rightV = dwg.Views.FirstOrDefault(v => v.Orientation == "right");
var topV = dwg.Views.FirstOrDefault(v => v.Orientation == "top");
Check(rightV != null && topV != null && dwg.Views.Any(v => v.Orientation == "iso"), "projected views are right, top and isometric");
Check(rightV != null && Math.Abs(rightV.Y - front.Y) < 1e-9 && topV != null && Math.Abs(topV.X - front.X) < 1e-9, "projected views stay aligned with the base view");
var fg = w.GeometryOf(front);
Check(Near(fg.Max.X - fg.Min.X, 60) && Near(fg.Max.Y - fg.Min.Y, 20), "front view spans 60 × 20");
Check(fg.Lines.Any(l => l.kind >= 3), "front view shows the hole as hidden lines");
var tg = w.GeometryOf(topV!);
Check(tg.Circles.Count == 1 && Near(tg.Circles[0].r, 5), "top view finds the hole circle");
// dimension the front view's bottom edge
w.RunCommand("dwg_dim");
Click(ViewPt(front, -30, 0));
Click(ViewPt(front, 30, 0));
Click(ViewPt(front, 0, -12));
Check(dwg.Dims.Count == 1 && dwg.Dims[0].Kind == "horizontal" && Near(dwg.Dims[0].Value, 60), "horizontal dimension of 60 mm");
Click(ViewPt(front, 30, 0));
Click(ViewPt(front, 30, 20));
Click(ViewPt(front, 42, 10));
Check(dwg.Dims.Count == 2 && dwg.Dims[1].Kind == "vertical" && Near(dwg.Dims[1].Value, 20), "vertical dimension of 20 mm");
Key(Avalonia.Input.Key.Escape);
w.RunCommand("dwg_dia");
Click(ViewPt(topV!, 5, 0));
Click(ViewPt(topV!, 18, 14));
Check(dwg.Dims.Count == 3 && dwg.Dims[2].Kind == "diameter" && dwg.Dims[2].Text == "⌀10", "diameter dimension ⌀10 on the hole");
Key(Avalonia.Input.Key.Escape);
Shot("drawing");
// undo removes the last dimension, redo brings it back
w.Undo();
Check(w.CurrentDrawing!.Dims.Count == 2, "undo removes the diameter dimension");
w.Redo();
dwg = w.CurrentDrawing!;
Check(dwg.Dims.Count == 3, "redo restores it");
// export
var pdf = Path.Combine(outDir, "drawing.pdf");
var dxf = Path.Combine(outDir, "drawing.dxf");
var svg = Path.Combine(outDir, "drawing.svg");
w.ExportDrawing(pdf);
w.ExportDrawing(dxf);
w.ExportDrawing(svg);
w.ExportDrawing(Path.Combine(outDir, $"{++step:00}_drawing_export.png"));
Check(File.Exists(pdf) && File.ReadAllText(pdf).StartsWith("%PDF") && new FileInfo(pdf).Length > 2000, "PDF is written");
var dxfText = File.ReadAllText(dxf);
Check(dxfText.Contains("\nHIDDEN\n") && dxfText.Contains("\nTEXT\n") && dxfText.Contains("\nDIM\n") && dxfText.TrimEnd().EndsWith("EOF"), "DXF has visible/hidden/dimension layers and text");
Check(File.ReadAllText(svg).Contains("stroke-dasharray") && File.ReadAllText(svg).Contains("⌀10"), "SVG has dashed hidden lines and the ⌀10 text");
// the drawing is saved with the design
var dwgSaved = Path.Combine(outDir, "drawing_saved.osc");
w.SaveTo(dwgSaved);
w.SetWorkspace("design");
Check(w.View.IsEffectivelyVisible && !w.SheetView.IsEffectivelyVisible, "back to the design workspace shows the 3D canvas");
w.OpenPath(dwgSaved);
Check(w.Document.Drawings.Count == 1 && w.Document.Drawings[0].Views.Count == 4 && w.Document.Drawings[0].Dims.Count == 3, "drawing is saved in the .osc design");

// ------------------------------------------------------------------ render workspace
Render:
var rDoc = new Doc
{
    Features =
    {
        new PrimitiveFeature { Name = "상자1", Shape = "box", A = 40, B = 40, C = 20 },
        new PrimitiveFeature { Name = "구1", Shape = "sphere", X = 0, Y = 0, Z = 35, A = 14 },
    },
};
rDoc.BodyColors[rDoc.Features[0].Id + ":0"] = "#D9342B";
rDoc.BodyAppearances[rDoc.Features[0].Id + ":0"] = "플라스틱 - 빨간색";
rDoc.BodyColors[rDoc.Features[1].Id + ":0"] = "#E6E8EA";
rDoc.BodyAppearances[rDoc.Features[1].Id + ":0"] = "크롬 - 광택";
rDoc.Marker = 2;
var rFile = Path.Combine(outDir, "render_part.osc");
File.WriteAllText(rFile, rDoc.Save());
w.OpenPath(rFile);
w.FitAll();
w.SetWorkspace("render");
Check(w.View.RayTrace, "render workspace turns on the in-canvas render");
var rayStart = DateTime.Now;
// headless: no dispatcher timers, so drive the passes the timer would start
Thread.Sleep(300);
while (w.View.RaySamples < 6 && (DateTime.Now - rayStart).TotalSeconds < 60) { w.View.RayStep(); Pump(); Thread.Sleep(30); }
Check(w.View.RaySamples >= 6, $"in-canvas render accumulates passes ({w.View.RaySamples})");
Shot("render_incanvas");
var (mHex, mMetal, mRough) = w.Document.MaterialOf(w.State.Bodies[1].Id);
Check(mMetal == 1 && mRough < 0.1 && w.Document.MaterialOf(w.State.Bodies[0].Id).metallic == 0, "appearances carry metal/plastic materials");
var rgba = w.RenderImage(360, 240, 48).GetAwaiter().GetResult();
var renderPng = Path.Combine(outDir, $"{++step:00}_render_image.png");
MainWindow.SavePng(rgba, 360, 240, renderPng);
int reddish = 0;
for (int i = 0; i < 360 * 240; i++) if (rgba[i * 4] > 110 && rgba[i * 4] > rgba[i * 4 + 1] * 1.8 && rgba[i * 4] > rgba[i * 4 + 2] * 1.8) reddish++;
Check(File.Exists(renderPng) && reddish > 400, $"rendered image shows the red plastic box ({reddish} red pixels)");
w.RunCommand("scenesettings");
Pump();
SetCombo("환경 프리셋", 2);
Check(w.Document.Render.Environment == "dark" && w.View.RaySamples == 0, "scene settings change the environment and restart the render");
Key(Avalonia.Input.Key.Escape);
w.SetWorkspace("design");
Check(!w.View.RayTrace, "leaving the render workspace turns the in-canvas render off");

// ------------------------------------------------------------------ manufacture workspace
Manufacture:
var mDoc = new Doc
{
    Features =
    {
        new PrimitiveFeature { Name = "상자1", Shape = "box", A = 60, B = 40, C = 20 },
        new PrimitiveFeature { Name = "상자2", Shape = "box", Z = 15, A = 30, B = 20, C = 10, Operation = BodyOp.Cut },
    },
};
mDoc.Features.Add(new HoleFeature { Name = "구멍1", Body = mDoc.Features[0].Id + ":0", Points = { new[] { 22.0, 12, 20 }, new[] { -22.0, -12, 20 } }, Diameter = 5, ThroughAll = true });
mDoc.Marker = 3;
var mFile = Path.Combine(outDir, "cam_part.osc");
File.WriteAllText(mFile, mDoc.Save());
w.OpenPath(mFile);
w.SetWorkspace("manufacture");
Pump();
Check(w.Workspace == "manufacture" && w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "밀링" && t.IsEffectivelyVisible), "manufacture workspace opens with the milling toolbar");
Check(w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("설정1 (64×44×21)") == true && t.IsEffectivelyVisible), "setup shows the 64×44×21 stock in the browser");
IEnumerable<V3> CutPts(Toolpath t) => t.Moves.Where(m => !m.Rapid).Select(m => m.P);
// facing
w.RunCommand("cam_face");
Pump();
Click(Text("확인"));
var faceTp = w.Toolpaths[w.Cam.Operations[^1].Id];
Check(faceTp.Error == null && CutPts(faceTp).All(p => Math.Abs(p.Z - 20) < 1e-6) && CutPts(faceTp).Max(p => p.X) > 32, "facing cuts the 1 mm of top stock across the whole stock");
// pocket: click the pocket floor
w.RunCommand("cam_pocket");
Pump();
Click(World(new V3(0, 0, 15)));
Shot("cam_pocket_preview");
Click(Text("확인"));
var pocketTp = w.Toolpaths[w.Cam.Operations[^1].Id];
var pz = CutPts(pocketTp).Select(p => Math.Round(p.Z, 3)).Distinct().OrderByDescending(z => z).ToList();
Check(pocketTp.Error == null && pz.SequenceEqual(new[] { 19.5, 18, 16.5, 15 }), $"pocket steps down 1.5 mm to the floor ({string.Join(", ", pz)})");
Check(CutPts(pocketTp).All(p => Math.Abs(p.X) <= 12 + 1e-3 && Math.Abs(p.Y) <= 7 + 1e-3), "pocket stays a tool radius inside the walls");
// contour around the top face
w.RunCommand("cam_contour");
Pump();
Click(World(new V3(25, -15, 20)));
Click(Text("확인"));
var contourTp = w.Toolpaths[w.Cam.Operations[^1].Id];
Check(contourTp.Error == null && Near(CutPts(contourTp).Max(p => p.X), 33, 0.002) && Near(CutPts(contourTp).Min(p => p.Z), -0.5, 0.002), "contour runs a tool radius outside the part down through the bottom");
// drilling finds both ⌀5 through holes
w.RunCommand("cam_drill");
Pump();
Click(Text("확인"));
var drillOp = w.Cam.Operations[^1];
var drillTp = w.Toolpaths[drillOp.Id];
Check(drillTp.Error == null && w.Cam.Tool(drillOp.Tool)!.Diameter == 5 && CutPts(drillTp).Count(p => p.Z < 0) == 2, "drill picks the ⌀5 drill and both through holes");
Shot("cam_toolpaths");
w.RunCommand("cam_simulate");
Pump();
Check(w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "예상 가공 시간" && t.IsEffectivelyVisible), "simulation shows the machining time");
Shot("cam_simulation");
Key(Avalonia.Input.Key.Escape);
// G-code
var gcode = w.PostProcess();
Check(gcode.Contains("G21") && gcode.Contains("S10000 M3") && gcode.Contains("M0") && gcode.TrimEnd().EndsWith("M30"), "GRBL program: metric, spindle, manual tool change pause, end");
Check(gcode.Contains("X54 Y34"), "G-code is relative to the stock's top front-left corner (hole at X54 Y34)");
w.Cam.Post = "fanuc";
var fanuc = w.PostProcess();
Check(fanuc.Contains("O1001") && fanuc.Contains("T6 M6") && fanuc.Contains("G43 H6"), "Fanuc program has tool changes with length offsets");
var camSaved = Path.Combine(outDir, "cam_saved.osc");
w.SaveTo(camSaved);
w.OpenPath(camSaved);
Check(w.Document.Cam.Operations.Count == 4 && w.Document.Cam.Post == "fanuc", "CAM operations are saved with the design");
w.SetWorkspace("design");

Console.WriteLine(failures.Count == 0 ? "ALL PASSED" : $"{failures.Count} FAILED");
return failures.Count == 0 ? 0 : 1;
