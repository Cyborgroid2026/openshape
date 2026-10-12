using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;

namespace OpenShape;

/// <summary>
/// Uses the icons of an Autodesk Fusion installation that is already on this computer (for example the
/// Wine install in ~/.fusion360-wine), so the toolbar, browser and navigation bar look exactly like Fusion.
/// Nothing from Autodesk is bundled with OpenShape: when no Fusion install is found the app falls back to
/// its own drawn icons (see <see cref="Icons"/>).
/// </summary>
public static class FusionAssets
{
    private const string F = "Fusion/UI/FusionUI/Resources/";
    private const string SM = "Fusion/UI/FusionSheetMetalUI/Resources/";
    private const string N = "Neutron/UI/Commands/Resources/";
    private const string B = "Neutron/UI/Base/Resources/";

    /// <summary>OpenShape icon key → resource folder inside the Fusion install.</summary>
    private static readonly Dictionary<string, string> Map = new()
    {
        // commands (toolbar, menus, timeline)
        ["sketch"] = F + "sketch/sketch_create",
        ["extrude"] = F + "solid/extrude",
        ["revolve"] = F + "solid/revolve",
        ["hole"] = F + "solid/hole",
        ["form"] = F + "TSpline/TSplineBaseFeatureCreation",
        ["automodel"] = F + "solid/automated_modeling",
        ["box"] = F + "solid/primitive_box",
        ["cylinder"] = F + "solid/primitive_cylinder",
        ["sphere"] = F + "solid/primitive_sphere",
        ["torus"] = F + "solid/primitive_torus",
        ["coil"] = F + "solid/primitive_coil",
        ["pipe"] = F + "solid/primitive_pipe",
        ["rectpattern"] = F + "pattern/pattern_rectangular",
        ["circpattern"] = F + "pattern/pattern_circular",
        ["mirror"] = F + "pattern/pattern_mirror",
        ["newcomponent"] = F + "NewComponent",
        ["move"] = F + "modify/move",
        ["combine"] = F + "modify/combine",
        ["scale"] = F + "modify/scale",
        ["delete"] = F + "modify/delete",
        ["splitbody"] = F + "modify/split",
        ["appearance"] = "Neutron/UI/Components/Resources/Icons/Appearance",
        ["joint"] = F + "Assembly/joint",
        ["insertcomponent"] = F + "Assembly/InsertComponent",
        ["offsetplane"] = F + "construction/plane_offset",
        ["measure"] = F + "tools/measure",
        ["section"] = F + "VisualAnalysis/HalfSection",
        ["insertmesh"] = F + "Modeling/InsertMesh",
        ["insertcad"] = F + "File/Insert",
        ["derive"] = F + "File/PushDerive",
        ["canvas"] = F + "Image/AddCanvas",
        ["decal"] = F + "Image/AddDecal",
        ["fillet"] = F + "solid/fillet",
        ["chamfer"] = F + "solid/chamfer",
        ["shell"] = F + "solid/shell",
        ["draft"] = F + "solid/draft",
        ["presspull"] = F + "modify/presspull",
        ["loft"] = F + "solid/loft",
        ["sweep"] = F + "solid/sweep",
        ["thread"] = F + "solid/thread",
        ["web"] = F + "solid/web",
        ["surfextrude"] = F + "surface/extrude",
        ["surfrevolve"] = F + "surface/revolve",
        ["surfsweep"] = F + "surface/sweep",
        ["surfloft"] = F + "surface/loft",
        ["patch"] = F + "surface/patch",
        ["thicken"] = F + "surface/thicken",
        ["stitch"] = F + "surface/stitch",
        ["unstitch"] = F + "surface/unstitch",
        ["surftrim"] = F + "surface/trim",
        ["extend"] = F + "surface/extend",
        ["flange"] = SM + "Flange",
        ["edgeflange"] = SM + "EdgeFlange",
        ["sheetrule"] = SM + "Rules/RulesCmd",
        ["flatpattern"] = SM + "FlatPattern",
        ["rib"] = F + "solid/rib",
        ["boss"] = F + "PlasticFeatures/Boss",
        ["lip"] = F + "PlasticFeatures/Lip",
        ["snapfit"] = F + "PlasticFeatures/SnapFit",
        ["select"] = F + "select/select",
        ["export"] = F + "Assembly/export",
        ["parameters"] = F + "Parameters/ParametersCommand",
        ["line"] = F + "sketch/draw_polyline",
        ["rect"] = F + "sketch/shape_rectangle_twopoint",
        ["crect"] = F + "sketch/shape_rectangle_center",
        ["circle"] = F + "sketch/circle_center_radius",
        ["arc"] = F + "sketch/arc_three_point",
        ["polygon"] = F + "sketch/shape_polygon_inscribed",
        ["spline"] = F + "sketch/draw_spline",
        ["sketchmirror"] = F + "sketch/pattern_mirror",
        ["sketchfillet"] = F + "sketch/fillet",
        ["offset"] = F + "sketch/offset",
        ["trim"] = F + "sketch/edit_trim",
        ["dimension"] = F + "sketch/dimension",
        ["constraint"] = F + "sketch/Automatic",
        ["c_hv"] = F + "sketch/Constraint_HorizontalVertical",
        ["c_coincident"] = F + "sketch/Constraint_Coincident",
        ["c_parallel"] = F + "sketch/Constraint_Parallel",
        ["c_perpendicular"] = F + "sketch/Constraint_Perpendicular",
        ["c_equal"] = F + "sketch/Constraint_Equal",
        ["c_fix"] = F + "sketch/Constraint_Fix",
        ["c_midpoint"] = F + "sketch/Constraint_MidPoint",
        ["c_concentric"] = F + "sketch/Constraint_Concentric",
        ["c_tangent"] = F + "sketch/Constraint_Tangent",
        ["c_symmetric"] = F + "sketch/Constraint_Symmetry",
        ["c_collinear"] = F + "sketch/Constraint_Collinear",
        ["c_smooth"] = F + "sketch/Constraint_Smooth",
        ["finish"] = F + "sketch/sketch_stop",
        ["mesh_convert"] = F + "Mesh/Convert",
        ["meshcreate"] = F + "Mesh/CreateMesh",
        ["configmode"] = F + "Modeling/DesignConfigurationMode",
        ["configtable"] = F + "Modeling/DesignConfiguration",
        ["configrules"] = F + "Modeling/ConfigurationRules",
        ["interference"] = F + "Assembly/interference",
        ["centerofmass"] = F + "tools/adsk_sim",
        ["rest"] = F + "PlasticFeatures/Rest",
        ["plasticrule"] = "Fusion/UI/PlasticFeaturesUI/Resources/ImageSets/Rules/RulesCmd",
        // application bar
        ["file"] = N + "File/FileMenu",
        ["save"] = N + "File/Save",
        ["undo"] = N + "Common/Undo",
        ["redo"] = N + "Common/Redo",
        ["help"] = N + "Common/Help",
        ["home"] = N + "Web/Home",
        ["bell"] = B + "NotificationCenter",
        // navigation bar
        ["orbit"] = N + "Camera/FreeOrbit",
        ["lookat"] = N + "Camera/LookAt",
        ["pan"] = N + "Camera/Pan",
        ["zoom"] = N + "Camera/Zoom",
        ["fit"] = N + "Camera/ZoomAll",
        ["display"] = N + "Display/ViewDisplay",
        ["grid"] = N + "Display/Grid",
        ["viewports"] = N + "File/SingleView",
        // browser
        ["eye"] = B + "Browser/Eye",
        ["eyeoff"] = B + "Browser/EyeOff",
        ["folder"] = B + "Browser/Folder",
        ["body"] = B + "Browser/SolidBody",
        ["component"] = B + "Browser/Component",
        ["sketchnode"] = B + "Browser/Sketch",
        ["plane"] = B + "Browser/WorkPlane",
        ["axis"] = B + "Browser/WorkAxis",
        ["jointnode"] = B + "Browser/JointRevolute",
        // timeline
        ["first"] = F + "Timeline/RollBegin",
        ["prev"] = F + "Timeline/RollBack",
        ["play"] = F + "Timeline/RollPlay",
        ["next"] = F + "Timeline/RollFwd",
        ["last"] = F + "Timeline/RollEnd",
        ["gear"] = F + "Timeline/Settings",
    };

    /// <summary>Tests switch this off so screenshots do not depend on what is installed.</summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("OPENSHAPE_NO_FUSION_ICONS") == null;

    private static string? _root;
    private static bool _searched;

    /// <summary>The Fusion "webdeploy/production/&lt;hash&gt;" folder, or null when Fusion is not installed.</summary>
    public static string? Root
    {
        get
        {
            if (_searched) return _root;
            _searched = true;
            _root = Find();
            return _root;
        }
    }

    public static bool Available => Enabled && Root != null;

    private static string? Find()
    {
        static bool Ok(string d) => Directory.Exists(Path.Combine(d, "Fusion", "UI", "FusionUI", "Resources", "solid"));
        if (Environment.GetEnvironmentVariable("OPENSHAPE_FUSION_DIR") is { Length: > 0 } env && Ok(env)) return env;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var prefixes = new List<string>();
        if (Environment.GetEnvironmentVariable("WINEPREFIX") is { Length: > 0 } wp) prefixes.Add(wp);
        prefixes.Add(Path.Combine(home, ".fusion360-wine"));
        prefixes.Add(Path.Combine(home, ".wine"));
        try
        {
            prefixes.AddRange(Directory.GetDirectories(home, ".*wine*"));
            var bottles = Path.Combine(home, ".var", "app", "com.usebottles.bottles", "data", "bottles", "bottles");
            if (Directory.Exists(bottles)) prefixes.AddRange(Directory.GetDirectories(bottles));
        }
        catch (Exception)
        {
            // unreadable home entries
        }
        foreach (var prefix in prefixes.Distinct())
        {
            var users = Path.Combine(prefix, "drive_c", "users");
            if (!Directory.Exists(users)) continue;
            try
            {
                var best = Directory.GetDirectories(users)
                    .Select(u => Path.Combine(u, "AppData", "Local", "Autodesk", "webdeploy", "production"))
                    .Where(Directory.Exists)
                    .SelectMany(Directory.GetDirectories)
                    .Where(Ok)
                    .OrderByDescending(d => File.Exists(Path.Combine(d, "Fusion360.exe")))
                    .ThenByDescending(Directory.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (best != null) return best;
            }
            catch (Exception)
            {
                // permissions
            }
        }
        return null;
    }

    private static readonly Dictionary<(string, int), IImage?> Cache = new();

    public static bool Has(string key) => Available && Map.ContainsKey(key) && Image(key, 16) != null;

    /// <summary>The Fusion icon for a key at roughly this size, or null.</summary>
    public static IImage? Image(string key, double size)
    {
        if (!Available || !Map.TryGetValue(key, out var rel)) return null;
        int px = size > 20 ? 32 : 16;
        if (Cache.TryGetValue((key, px), out var hit)) return hit;
        var dir = Path.Combine(Root!, rel);
        IImage? img = null;
        foreach (var s in px == 32 ? new[] { 32, 24, 16 } : new[] { 16, 24, 32 })
        {
            foreach (var name in new[] { $"{s}x{s}@2x.png", $"{s}x{s}.png", $"{s}x{s}-weave_light.svg", $"{s}x{s}-light_gray.svg", $"{s}x{s}.svg" })
            {
                var f = Path.Combine(dir, name);
                if (!File.Exists(f)) continue;
                try
                {
                    img = name.EndsWith(".svg") ? new SvgImage { Source = SvgSource.Load(f, null) } : new Bitmap(f);
                }
                catch (Exception)
                {
                    img = null;
                }
                if (img != null) break;
            }
            if (img != null) break;
        }
        Cache[(key, px)] = img;
        return img;
    }

    /// <summary>An Image control for the key, or null when Fusion (or that icon) is not available.</summary>
    public static Control? Control(string key, double size, double opacity = 1)
    {
        var img = Image(key, size);
        if (img == null) return null;
        var c = new Image { Source = img, Width = size, Height = size, Stretch = Stretch.Uniform, Opacity = opacity };
        RenderOptions.SetBitmapInterpolationMode(c, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        return c;
    }
}
