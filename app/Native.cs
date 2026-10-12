using System.Runtime.InteropServices;
using System.Text;

namespace OpenShape;

internal static unsafe class Native
{
    private const string Lib = "openshape_engine";

    [DllImport(Lib)] public static extern void osc_free(uint h);
    [DllImport(Lib)] public static extern uint osc_count();
    [DllImport(Lib)] public static extern uint osc_box(double x0, double y0, double z0, double x1, double y1, double z1);
    [DllImport(Lib)] public static extern uint osc_cylinder(double x, double y, double z, double r, double h);
    [DllImport(Lib)] public static extern uint osc_cone(double x, double y, double z, double r1, double r2, double h);
    [DllImport(Lib)] public static extern uint osc_sphere(double x, double y, double z, double r);
    [DllImport(Lib)] public static extern uint osc_torus(double x, double y, double z, double major, double minor);
    [DllImport(Lib)] public static extern uint osc_extrude(double* frame, double* pts, int* counts, int n, double h0, double h1);
    [DllImport(Lib)] public static extern uint osc_revolve(double* frame, double* pts, int* counts, int n, double ax, double ay, double dx, double dy, double angle);
    [DllImport(Lib)] public static extern uint osc_boolean(uint a, uint b, uint op);
    [DllImport(Lib)] public static extern uint osc_transform(uint h, double* m);
    [DllImport(Lib)] public static extern int osc_info(uint h, double* out8);
    [DllImport(Lib)] public static extern void osc_render(uint* handles, float* colors, uint* flags, int n, double* cam, int w, int h, byte* outRgba, uint opts, double gridStep);
    [DllImport(Lib)] public static extern uint osc_pick(int x, int y, double* depth);
    [DllImport(Lib)] public static extern int osc_export(uint* handles, byte* names, int n, byte* path, uint format);
    [DllImport(Lib)] public static extern uint osc_import(byte* path, uint format);

    public static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes(s + "\0");
}

public readonly record struct SolidInfo(int Triangles, double Volume, double Area, V3 Min, V3 Max);

public enum BoolOp : uint { Union = 0, Subtract = 1, Intersect = 2 }

/// <summary>An immutable solid owned by the Rust engine; freed when collected.</summary>
public sealed unsafe class Solid
{
    public uint Handle { get; }
    private readonly long _pressure;

    private Solid(uint h)
    {
        Handle = h;
        double* o = stackalloc double[8];
        int tris = Native.osc_info(h, o);
        _pressure = Math.Max(1, tris) * 200L;
        GC.AddMemoryPressure(_pressure);
    }

    ~Solid()
    {
        Native.osc_free(Handle);
        GC.RemoveMemoryPressure(_pressure);
    }

    private static Solid? Wrap(uint h) => h == 0 ? null : new Solid(h);

    public static Solid Box(V3 a, V3 b) => Wrap(Native.osc_box(a.X, a.Y, a.Z, b.X, b.Y, b.Z))!;
    public static Solid Cylinder(V3 c, double r, double h) => Wrap(Native.osc_cylinder(c.X, c.Y, c.Z, r, h))!;
    public static Solid Cone(V3 c, double r1, double r2, double h) => Wrap(Native.osc_cone(c.X, c.Y, c.Z, r1, r2, h))!;
    public static Solid Sphere(V3 c, double r) => Wrap(Native.osc_sphere(c.X, c.Y, c.Z, r))!;
    public static Solid Torus(V3 c, double major, double minor) => Wrap(Native.osc_torus(c.X, c.Y, c.Z, major, minor))!;

    private static (double[] pts, int[] counts) Flatten(IReadOnlyList<IReadOnlyList<P2>> loops)
    {
        var pts = new List<double>();
        var counts = new int[loops.Count];
        for (int i = 0; i < loops.Count; i++)
        {
            counts[i] = loops[i].Count;
            foreach (var p in loops[i]) { pts.Add(p.X); pts.Add(p.Y); }
        }
        return (pts.ToArray(), counts);
    }

    public static Solid? Extrude(PlaneFrame f, IReadOnlyList<IReadOnlyList<P2>> loops, double h0, double h1)
    {
        var (pts, counts) = Flatten(loops);
        var fr = f.ToArray();
        fixed (double* pf = fr, pp = pts)
        fixed (int* pc = counts)
            return Wrap(Native.osc_extrude(pf, pp, pc, counts.Length, h0, h1));
    }

    public static Solid? Revolve(PlaneFrame f, IReadOnlyList<IReadOnlyList<P2>> loops, P2 axisPoint, P2 axisDir, double angle)
    {
        var (pts, counts) = Flatten(loops);
        var fr = f.ToArray();
        fixed (double* pf = fr, pp = pts)
        fixed (int* pc = counts)
            return Wrap(Native.osc_revolve(pf, pp, pc, counts.Length, axisPoint.X, axisPoint.Y, axisDir.X, axisDir.Y, angle));
    }

    public Solid Boolean(Solid tool, BoolOp op) => Wrap(Native.osc_boolean(Handle, tool.Handle, (uint)op))!;

    public Solid Transform(Affine m)
    {
        var a = m.ToArray();
        fixed (double* p = a) return Wrap(Native.osc_transform(Handle, p))!;
    }

    public SolidInfo Info
    {
        get
        {
            double* o = stackalloc double[8];
            int n = Native.osc_info(Handle, o);
            return new SolidInfo(n, o[0], o[1], new V3(o[2], o[3], o[4]), new V3(o[5], o[6], o[7]));
        }
    }

    public static Solid? Import(string path)
    {
        uint fmt = path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ? 1u : 0u;
        var p = Native.Utf8Z(path);
        fixed (byte* pp = p) return Wrap(Native.osc_import(pp, fmt));
    }

    /// <summary>format: 0 STL, 1 OBJ, 2 USDA.</summary>
    public static bool Export(IReadOnlyList<(string name, Solid solid)> bodies, string path, uint format)
    {
        var hs = bodies.Select(b => b.solid.Handle).ToArray();
        var names = Native.Utf8Z(string.Join("\n", bodies.Select(b => b.name.Replace('\n', ' '))));
        var p = Native.Utf8Z(path);
        fixed (uint* ph = hs)
        fixed (byte* pn = names, pp = p)
            return Native.osc_export(ph, pn, hs.Length, pp, format) == 1;
    }
}
