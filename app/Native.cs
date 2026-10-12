using System.Runtime.InteropServices;
using System.Text;

namespace OpenShape;

/// <summary>Rust engine: display meshes, viewport rendering, picking, mesh files.</summary>
internal static unsafe class Native
{
    private const string Lib = "openshape_engine";

    [DllImport(Lib)] public static extern void osc_free(uint h);
    [DllImport(Lib)] public static extern uint osc_count();
    [DllImport(Lib)] public static extern uint osc_mesh(double* xyz, uint* faces, int n);
    [DllImport(Lib)] public static extern int osc_tris(uint h, double* xyz);
    [DllImport(Lib)] public static extern int osc_info(uint h, double* out8);
    [DllImport(Lib)] public static extern void osc_render(uint* handles, float* colors, uint* flags, int n, double* cam, int w, int h, byte* outRgba, uint opts, double gridStep);
    [DllImport(Lib)] public static extern uint osc_pick(int x, int y, double* depth, uint* face);
    [DllImport(Lib)] public static extern int osc_export(uint* handles, byte* names, int n, byte* path, uint format);
    [DllImport(Lib)] public static extern uint osc_import(byte* path, uint format);

    public static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes(s + "\0");
}

/// <summary>OpenCASCADE B-rep kernel (C++ shim).</summary>
internal static unsafe class Occt
{
    private const string Lib = "openshape_occt";

    [DllImport(Lib)] public static extern void ok_init();
    [DllImport(Lib)] public static extern void ok_free(uint h);
    [DllImport(Lib)] public static extern int ok_error(byte* buf, int len);
    [DllImport(Lib)] public static extern uint ok_box(double x0, double y0, double z0, double x1, double y1, double z1);
    [DllImport(Lib)] public static extern uint ok_cylinder(double x, double y, double z, double r, double h);
    [DllImport(Lib)] public static extern uint ok_cone(double x, double y, double z, double r1, double r2, double h);
    [DllImport(Lib)] public static extern uint ok_sphere(double x, double y, double z, double r);
    [DllImport(Lib)] public static extern uint ok_torus(double x, double y, double z, double major, double minor);
    [DllImport(Lib)] public static extern uint ok_profile_face(double* frame, int* kinds, double* data, int* loopCounts, int nloops);
    [DllImport(Lib)] public static extern uint ok_prism(uint face, double dx, double dy, double dz, double h0, double h1);
    [DllImport(Lib)] public static extern uint ok_revol(uint face, double ox, double oy, double oz, double dx, double dy, double dz, double deg);
    [DllImport(Lib)] public static extern uint ok_bool(uint a, uint b, int op);
    [DllImport(Lib)] public static extern uint ok_transform(uint h, double* m);
    [DllImport(Lib)] public static extern uint ok_blend(uint h, int kind, double* pts, int n, double size);
    [DllImport(Lib)] public static extern uint ok_shell(uint h, double* pts, int n, double thickness);
    [DllImport(Lib)] public static extern int ok_info(uint h, double* out8);
    [DllImport(Lib)] public static extern int ok_mesh(uint h, double relDeflection);
    [DllImport(Lib)] public static extern void ok_mesh_copy(double* xyz, uint* faceIds);
    [DllImport(Lib)] public static extern int ok_edges(uint h, double relDeflection);
    [DllImport(Lib)] public static extern int ok_edges_points();
    [DllImport(Lib)] public static extern void ok_edges_copy(double* xyz, int* counts);
    [DllImport(Lib)] public static extern int ok_face_frame(uint h, double x, double y, double z, double* out12);
    [DllImport(Lib)] public static extern int ok_joint_frame(uint h, double x, double y, double z, double* out6);
    [DllImport(Lib)] public static extern int ok_mass(uint h, double* out10);
    [DllImport(Lib)] public static extern uint ok_wire(double* frame, int* kinds, double* data, int count);
    [DllImport(Lib)] public static extern uint ok_loft(uint* hs, int n, int solid);
    [DllImport(Lib)] public static extern uint ok_sweep(uint profile, uint path);
    [DllImport(Lib)] public static extern uint ok_patch(uint wire);
    [DllImport(Lib)] public static extern uint ok_thicken(uint h, double t);
    [DllImport(Lib)] public static extern uint ok_press_pull(uint h, double x, double y, double z, double d);
    [DllImport(Lib)] public static extern int ok_edge_flange_frame(uint h, double x, double y, double z, double* out13);
    [DllImport(Lib)] public static extern uint ok_read(byte* path, int kind);
    [DllImport(Lib)] public static extern int ok_solids(uint h, uint* outHandles, int max);
    [DllImport(Lib)] public static extern int ok_write_step(uint* hs, int n, byte* path);
    [DllImport(Lib)] public static extern uint ok_from_mesh(double* tris, int n);

    private static bool _init;

    public static void Init()
    {
        if (_init) return;
        _init = true;
        ok_init();
    }

    public static string LastError()
    {
        byte* buf = stackalloc byte[512];
        int n = ok_error(buf, 512);
        return n == 0 ? "형상 연산에 실패했습니다" : Encoding.UTF8.GetString(buf, Math.Min(n, 511));
    }
}

public readonly record struct SolidInfo(int Solids, double Volume, double Area, V3 Min, V3 Max)
{
    /// <summary>Nothing left: no solid volume and (for surface bodies) no area.</summary>
    public bool IsEmpty => Solids > 0 ? Math.Abs(Volume) < 1e-9 : Area < 1e-9;
}

public enum BoolOp { Union = 0, Subtract = 1, Intersect = 2 }

/// <summary>
/// An immutable B-rep solid owned by OpenCASCADE, with a lazily built display mesh in the Rust
/// engine. Both are freed when the object is collected.
/// </summary>
public sealed unsafe class Solid
{
    public uint Shape { get; }
    private uint _mesh;
    private SolidInfo? _info;
    private List<V3[]>? _edges;

    /// <summary>Display deflection relative to the bounding-box diagonal.</summary>
    public const double Deflection = 0.0012;

    private Solid(uint shape)
    {
        Shape = shape;
        GC.AddMemoryPressure(64_000);
    }

    ~Solid()
    {
        Occt.ok_free(Shape);
        if (_mesh != 0) Native.osc_free(_mesh);
        GC.RemoveMemoryPressure(64_000);
    }

    private static Solid Wrap(uint h) => h == 0 ? throw new InvalidOperationException(Occt.LastError()) : new Solid(h);

    /// <summary>Rust mesh used for display, picking and mesh export.</summary>
    public uint Handle
    {
        get
        {
            if (_mesh != 0) return _mesh;
            lock (this)
            {
                if (_mesh != 0) return _mesh;
                int n = Occt.ok_mesh(Shape, Deflection);
                if (n <= 0) return _mesh = Native.osc_mesh(null, null, 0);
                var xyz = new double[n * 9];
                var faces = new uint[n];
                fixed (double* px = xyz)
                fixed (uint* pf = faces)
                {
                    Occt.ok_mesh_copy(px, pf);
                    _mesh = Native.osc_mesh(px, pf, n);
                }
                return _mesh;
            }
        }
    }

    public static Solid Box(V3 a, V3 b) { Occt.Init(); return Wrap(Occt.ok_box(a.X, a.Y, a.Z, b.X, b.Y, b.Z)); }
    public static Solid Cylinder(V3 c, double r, double h) { Occt.Init(); return Wrap(Occt.ok_cylinder(c.X, c.Y, c.Z, r, h)); }
    public static Solid Cone(V3 c, double r1, double r2, double h) { Occt.Init(); return Wrap(Occt.ok_cone(c.X, c.Y, c.Z, r1, r2, h)); }
    public static Solid Sphere(V3 c, double r) { Occt.Init(); return Wrap(Occt.ok_sphere(c.X, c.Y, c.Z, r)); }
    public static Solid Torus(V3 c, double major, double minor) { Occt.Init(); return Wrap(Occt.ok_torus(c.X, c.Y, c.Z, major, minor)); }

    /// <summary>Planar face of a profile region (outer loop first, then holes); freed by the caller.</summary>
    private static uint Face(PlaneFrame f, IReadOnlyList<Profile> region)
    {
        Occt.Init();
        var kinds = new List<int>();
        var data = new List<double>();
        var counts = new int[region.Count];
        for (int i = 0; i < region.Count; i++)
        {
            counts[i] = region[i].Pieces.Count;
            foreach (var p in region[i].Pieces)
            {
                kinds.Add(p.Kind);
                data.AddRange(p.P);
            }
        }
        var fr = f.ToArray();
        var k = kinds.ToArray();
        var d = data.ToArray();
        fixed (double* pf = fr, pd = d)
        fixed (int* pk = k, pc = counts)
        {
            uint face = Occt.ok_profile_face(pf, pk, pd, pc, counts.Length);
            if (face == 0) throw new InvalidOperationException(Occt.LastError());
            return face;
        }
    }

    /// <summary>Wire (open or closed) through sketch pieces; freed by the caller.</summary>
    private static uint WireOf(PlaneFrame f, IReadOnlyList<Piece> pieces)
    {
        Occt.Init();
        var k = pieces.Select(p => p.Kind).ToArray();
        var d = pieces.SelectMany(p => p.P).ToArray();
        var fr = f.ToArray();
        fixed (double* pf = fr, pd = d)
        fixed (int* pk = k)
        {
            uint w = Occt.ok_wire(pf, pk, pd, k.Length);
            if (w == 0) throw new InvalidOperationException(Occt.LastError());
            return w;
        }
    }

    /// <summary>Solid loft through closed profile regions (outer loops), in order.</summary>
    public static Solid Loft(IReadOnlyList<(PlaneFrame frame, IReadOnlyList<Profile> region)> sections)
    {
        var faces = sections.Select(s => Face(s.frame, s.region)).ToArray();
        try
        {
            fixed (uint* pf = faces) return Wrap(Occt.ok_loft(pf, faces.Length, 1));
        }
        finally
        {
            foreach (var f in faces) Occt.ok_free(f);
        }
    }

    public static Solid Sweep(PlaneFrame pf, IReadOnlyList<Profile> region, PlaneFrame pathFrame, IReadOnlyList<Piece> path)
    {
        uint face = Face(pf, region);
        uint wire = WireOf(pathFrame, path);
        try { return Wrap(Occt.ok_sweep(face, wire)); }
        finally { Occt.ok_free(face); Occt.ok_free(wire); }
    }

    /// <summary>Surface (not solid) extruded from sketch curves.</summary>
    public static Solid SurfaceExtrude(PlaneFrame f, IReadOnlyList<Piece> curves, double h0, double h1)
    {
        uint wire = WireOf(f, curves);
        try { return Wrap(Occt.ok_prism(wire, f.N.X, f.N.Y, f.N.Z, h0, h1)); }
        finally { Occt.ok_free(wire); }
    }

    public static Solid Patch(PlaneFrame f, IReadOnlyList<Piece> loop)
    {
        uint wire = WireOf(f, loop);
        try { return Wrap(Occt.ok_patch(wire)); }
        finally { Occt.ok_free(wire); }
    }

    public Solid Thicken(double t) => Wrap(Occt.ok_thicken(Shape, t));

    public Solid PressPull(V3 p, double d) => Wrap(Occt.ok_press_pull(Shape, p.X, p.Y, p.Z, d));

    /// <summary>Straight edge at p: start, outward (in the sheet), sheet normal, edge direction, length.</summary>
    public (V3 o, V3 outward, V3 normal, V3 dir, double length)? EdgeFlangeFrame(V3 p)
    {
        double* r = stackalloc double[13];
        if (Occt.ok_edge_flange_frame(Shape, p.X, p.Y, p.Z, r) == 0) return null;
        return (new V3(r[0], r[1], r[2]), new V3(r[3], r[4], r[5]), new V3(r[6], r[7], r[8]), new V3(r[9], r[10], r[11]), r[12]);
    }

    public static Solid Extrude(PlaneFrame f, IReadOnlyList<Profile> region, double h0, double h1)
    {
        uint face = Face(f, region);
        try { return Wrap(Occt.ok_prism(face, f.N.X, f.N.Y, f.N.Z, h0, h1)); }
        finally { Occt.ok_free(face); }
    }

    public static Solid Revolve(PlaneFrame f, IReadOnlyList<Profile> region, P2 axisPoint, P2 axisDir, double angle)
    {
        uint face = Face(f, region);
        var o = f.At(axisPoint);
        var d = (f.U * axisDir.X + f.V * axisDir.Y).Norm();
        try { return Wrap(Occt.ok_revol(face, o.X, o.Y, o.Z, d.X, d.Y, d.Z, angle)); }
        finally { Occt.ok_free(face); }
    }

    public Solid Boolean(Solid tool, BoolOp op) => Wrap(Occt.ok_bool(Shape, tool.Shape, (int)op));

    public Solid Transform(Affine m)
    {
        var a = m.ToArray();
        fixed (double* p = a) return Wrap(Occt.ok_transform(Shape, p));
    }

    private Solid PointOp(IReadOnlyList<V3> pts, Func<IntPtr, int, uint> op)
    {
        var flat = pts.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        fixed (double* pf = flat) return Wrap(op((IntPtr)pf, pts.Count));
    }

    /// <summary>Fillet the edges nearest to the points (every edge when the list is empty).</summary>
    public Solid Fillet(IReadOnlyList<V3> edgePoints, double radius) => PointOp(edgePoints, (p, n) => Occt.ok_blend(Shape, 0, (double*)p, n, radius));

    public Solid Chamfer(IReadOnlyList<V3> edgePoints, double distance) => PointOp(edgePoints, (p, n) => Occt.ok_blend(Shape, 1, (double*)p, n, distance));

    /// <summary>Hollow out, removing the faces nearest to the points; negative thickness grows inward.</summary>
    public Solid Shell(IReadOnlyList<V3> facePoints, double thickness) => PointOp(facePoints, (p, n) => Occt.ok_shell(Shape, (double*)p, n, thickness));

    public SolidInfo Info
    {
        get
        {
            if (_info is { } i) return i;
            double* o = stackalloc double[8];
            int n = Occt.ok_info(Shape, o);
            var info = new SolidInfo(n, o[0], o[1], new V3(o[2], o[3], o[4]), new V3(o[5], o[6], o[7]));
            _info = info;
            return info;
        }
    }

    /// <summary>B-rep edges as polylines (for edge picking and highlighting).</summary>
    public List<V3[]> Edges
    {
        get
        {
            if (_edges != null) return _edges;
            lock (this)
            {
                var list = new List<V3[]>();
                int n = Occt.ok_edges(Shape, Deflection);
                if (n > 0)
                {
                    var xyz = new double[Occt.ok_edges_points() * 3];
                    var counts = new int[n];
                    fixed (double* px = xyz)
                    fixed (int* pc = counts)
                        Occt.ok_edges_copy(px, pc);
                    int o = 0;
                    foreach (var c in counts)
                    {
                        var pts = new V3[c];
                        for (int i = 0; i < c; i++, o++) pts[i] = new V3(xyz[o * 3], xyz[o * 3 + 1], xyz[o * 3 + 2]);
                        list.Add(pts);
                    }
                }
                return _edges = list;
            }
        }
    }

    /// <summary>Sketch frame of the planar face at the point, or null when the face is curved.</summary>
    public PlaneFrame? FaceFrame(V3 p)
    {
        double* o = stackalloc double[12];
        if (Occt.ok_face_frame(Shape, p.X, p.Y, p.Z, o) == 0) return null;
        return new PlaneFrame(new V3(o[0], o[1], o[2]), new V3(o[3], o[4], o[5]), new V3(o[6], o[7], o[8]), new V3(o[9], o[10], o[11]));
    }

    /// <summary>Joint origin and axis on the face at the point (face centre/normal, cylinder axis, sphere centre).</summary>
    public (V3 origin, V3 axis)? JointFrame(V3 p)
    {
        double* o = stackalloc double[6];
        if (Occt.ok_joint_frame(Shape, p.X, p.Y, p.Z, o) == 0) return null;
        return (new V3(o[0], o[1], o[2]), new V3(o[3], o[4], o[5]).Norm());
    }

    /// <summary>Volume, centre of mass and inertia tensor about it (Ixx Iyy Izz Ixy Iyz Ixz) for unit density (mm).</summary>
    public (double volume, V3 center, double[] inertia) MassProperties
    {
        get
        {
            double* o = stackalloc double[10];
            if (Occt.ok_mass(Shape, o) == 0) return (0, V3.Zero, new double[6]);
            return (o[0], new V3(o[1], o[2], o[3]), new[] { o[4], o[5], o[6], o[7], o[8], o[9] });
        }
    }

    /// <summary>Display triangles (9 doubles each) of the solid.</summary>
    public double[] Triangles()
    {
        uint h = Handle;
        int n = Native.osc_tris(h, null);
        var t = new double[Math.Max(0, n) * 9];
        if (n > 0)
            fixed (double* p = t) Native.osc_tris(h, p);
        return t;
    }

    /// <summary>Solids from a STEP/IGES/STL/OBJ file.</summary>
    public static List<Solid> Import(string path)
    {
        Occt.Init();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var p = Native.Utf8Z(path);
        uint whole;
        if (ext is ".step" or ".stp" or ".iges" or ".igs")
        {
            fixed (byte* pp = p) whole = Occt.ok_read(pp, ext is ".step" or ".stp" ? 0 : 1);
        }
        else
        {
            uint mesh;
            fixed (byte* pp = p) mesh = Native.osc_import(pp, ext == ".obj" ? 1u : 0u);
            if (mesh == 0) throw new InvalidOperationException("STL/OBJ 파일을 읽을 수 없습니다.");
            try
            {
                int n = Native.osc_tris(mesh, null);
                var tris = new double[n * 9];
                fixed (double* pt = tris)
                {
                    Native.osc_tris(mesh, pt);
                    whole = Occt.ok_from_mesh(pt, n);
                }
            }
            finally { Native.osc_free(mesh); }
        }
        if (whole == 0) throw new InvalidOperationException(Occt.LastError());
        try
        {
            var handles = new uint[4096];
            int count;
            fixed (uint* ph = handles) count = Occt.ok_solids(whole, ph, handles.Length);
            if (count <= 0) throw new InvalidOperationException("파일에 솔리드가 없습니다.");
            return handles.Take(count).Select(h => new Solid(h)).ToList();
        }
        finally { Occt.ok_free(whole); }
    }

    /// <summary>format: 0 STL, 1 OBJ, 2 USDA, 3 STEP.</summary>
    public static bool Export(IReadOnlyList<(string name, Solid solid)> bodies, string path, uint format)
    {
        var p = Native.Utf8Z(path);
        if (format == 3)
        {
            var shapes = bodies.Select(b => b.solid.Shape).ToArray();
            fixed (uint* ph = shapes)
            fixed (byte* pp = p)
                return Occt.ok_write_step(ph, shapes.Length, pp) == 1;
        }
        var hs = bodies.Select(b => b.solid.Handle).ToArray();
        var names = Native.Utf8Z(string.Join("\n", bodies.Select(b => b.name.Replace('\n', ' '))));
        fixed (uint* ph = hs)
        fixed (byte* pn = names, pp = p)
            return Native.osc_export(ph, pn, hs.Length, pp, format) == 1;
    }
}
