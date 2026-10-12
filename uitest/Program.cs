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
Click(Tip("모양"));
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

Console.WriteLine(failures.Count == 0 ? "ALL PASSED" : $"{failures.Count} FAILED");
return failures.Count == 0 ? 0 : 1;
