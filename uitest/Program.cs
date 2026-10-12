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

var w = new MainWindow(Array.Empty<string>()) { Width = 1500, Height = 920 };
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

Point Tip(string tip) => Center(w.GetVisualDescendants().OfType<Control>().Where(x => ToolTip.GetTip(x) as string == tip && x.IsEffectivelyVisible).LastOrDefault()
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
Click(Tip("스케치 마침"));
Check(!w.InSketch && w.Document.Features.Count == 1, "'스케치 마침' adds the sketch to the timeline");
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
Click(Tip("상자"));
SetField("길이", "20");
SetField("너비", "20");
SetField("높이", "40");
SetField("X", "30");
SetCombo("작업", 1);
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
Click(Tip("스케치 마침"));
var sk2 = w.Document.Features.OfType<SketchFeature>().Last();
Check(sk2 != sk1 && sk2.Plane == "XZ" && sk2.Entities.Count == 1, "second sketch on the XZ plane");
Click(Tip("회전"));
Click(World(sk2.Frame.At(new P2(65, 10))));
SetCombo("작업", 3);
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
Click(Tip("스케치 마침"));
var sk4 = w.Document.Features.OfType<SketchFeature>().Last();
Check(sk4.Constraints.Count >= 9, "constraints are saved with the sketch");
Click(Tip("돌출"));
Click(World(sk4.Frame.At(new P2(30, 5))));
SetField("거리", "10");
SetCombo("작업", 3);
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
Click(Tip("모깎기"));
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
Click(Tip("모따기"));
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
Click(Tip("스케치 마침"));
var sk3 = w.Document.Features.OfType<SketchFeature>().Last(f => f.Plane == "face");
plateBefore = BodyVol(0);
Click(Tip("돌출"));
Click(World(sk3.Frame.At(c2)));
SetField("거리", "-5");
SetCombo("작업", 1);
w.RefreshPreview();
Click(Text("확인"));
Check(Near(plateBefore - BodyVol(0), Math.PI * 16 * 5, 0.01), $"pocket cut from the face sketch removes {plateBefore - BodyVol(0):0} ≈ {Math.PI * 80:0} mm³");
Shot("face_pocket");

// replay the rest of the timeline on top of the inserted features
w.MoveMarker(w.Document.Features.Count);
Check(w.State.Bodies.Count == 4 && w.Document.Features.All(f => f.Error == null), "later features still rebuild after inserting fillet/chamfer/pocket");

// shell a new box from its top face
Click(Tip("상자"));
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

// a real Fusion cloud design (STEP from the cloud sync), when present on this machine
var cloud = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "fusion-webviewer", "exchange", "cloud", "XL_430_new", "XL_430_new.step");
if (File.Exists(cloud))
{
    w.OpenPath(cloud);
    Pump();
    Check(w.State.Bodies.Count > 0 && Volume() > 0 && w.Document.Features[0].Error == null, $"Fusion design XL_430_new.step opens ({w.State.Bodies.Count} bodies, {Volume() / 1000:0.0} cm³)");
    w.View.SetView(-55, 28);
    w.FitAll();
    Shot("fusion_step");
}
else Console.WriteLine("SKIP  Fusion cloud STEP not found");


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
void Finish() => Click(Tip("스케치 마침"));
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

Console.WriteLine(failures.Count == 0 ? "ALL PASSED" : $"{failures.Count} FAILED");
return failures.Count == 0 ? 0 : 1;
