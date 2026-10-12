namespace OpenShape;

public readonly record struct P2(double X, double Y)
{
    public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Y + b.Y);
    public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);
    public static P2 operator *(P2 a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Dist(P2 o) => (this - o).Length;
}

public readonly record struct V3(double X, double Y, double Z)
{
    public static readonly V3 Zero = new(0, 0, 0);
    public static readonly V3 UnitX = new(1, 0, 0);
    public static readonly V3 UnitY = new(0, 1, 0);
    public static readonly V3 UnitZ = new(0, 0, 1);
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);
    public static V3 operator *(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public double Dot(V3 o) => X * o.X + Y * o.Y + Z * o.Z;
    public V3 Cross(V3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
    public double Length => Math.Sqrt(Dot(this));
    public V3 Norm() { var l = Length; return l > 1e-15 ? this * (1 / l) : UnitZ; }
}

/// <summary>Sketch plane: origin and orthonormal axes, N = U x V.</summary>
public readonly record struct PlaneFrame(V3 O, V3 U, V3 V, V3 N)
{
    public V3 At(P2 p, double h = 0) => O + U * p.X + V * p.Y + N * h;
    public P2 Project(V3 p) { var d = p - O; return new P2(d.Dot(U), d.Dot(V)); }
    public double[] ToArray() => new[] { O.X, O.Y, O.Z, U.X, U.Y, U.Z, V.X, V.Y, V.Z, N.X, N.Y, N.Z };

    /// <summary>Origin planes; XZ looks from the front (normal -Y) like the "front" view.</summary>
    public static PlaneFrame Of(string plane, double offset)
    {
        var f = plane switch
        {
            "XZ" => new PlaneFrame(V3.Zero, V3.UnitX, V3.UnitZ, -V3.UnitY),
            "YZ" => new PlaneFrame(V3.Zero, V3.UnitY, V3.UnitZ, V3.UnitX),
            _ => new PlaneFrame(V3.Zero, V3.UnitX, V3.UnitY, V3.UnitZ),
        };
        return f with { O = f.N * offset };
    }
}

/// <summary>3x4 affine matrix (rows), p' = R p + t.</summary>
public readonly struct Affine
{
    private readonly double[] _m;
    public Affine(double[] m) => _m = m;
    public static Affine Identity => new(new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 });
    public double[] ToArray() => (double[])_m.Clone();

    public V3 Apply(V3 p) => new(
        _m[0] * p.X + _m[1] * p.Y + _m[2] * p.Z + _m[3],
        _m[4] * p.X + _m[5] * p.Y + _m[6] * p.Z + _m[7],
        _m[8] * p.X + _m[9] * p.Y + _m[10] * p.Z + _m[11]);

    public static Affine Translation(V3 t) => new(new double[] { 1, 0, 0, t.X, 0, 1, 0, t.Y, 0, 0, 1, t.Z });

    public static Affine Scale(V3 center, double k) =>
        new(new double[] { k, 0, 0, center.X * (1 - k), 0, k, 0, center.Y * (1 - k), 0, 0, k, center.Z * (1 - k) });

    /// <summary>Rotation by deg about the line through p with direction axis.</summary>
    public static Affine Rotation(V3 p, V3 axis, double deg)
    {
        var a = axis.Norm();
        double r = deg * Math.PI / 180, s = Math.Sin(r), c = Math.Cos(r), t = 1 - c;
        double[] R =
        {
            t * a.X * a.X + c, t * a.X * a.Y - s * a.Z, t * a.X * a.Z + s * a.Y,
            t * a.X * a.Y + s * a.Z, t * a.Y * a.Y + c, t * a.Y * a.Z - s * a.X,
            t * a.X * a.Z - s * a.Y, t * a.Y * a.Z + s * a.X, t * a.Z * a.Z + c,
        };
        var m = new double[12];
        double[] pv = { p.X, p.Y, p.Z };
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++) m[i * 4 + j] = R[i * 3 + j];
            m[i * 4 + 3] = pv[i] - (R[i * 3] * p.X + R[i * 3 + 1] * p.Y + R[i * 3 + 2] * p.Z);
        }
        return new Affine(m);
    }

    /// <summary>Mirror through the plane with normal n passing through the origin.</summary>
    public static Affine Mirror(V3 n)
    {
        n = n.Norm();
        return new Affine(new double[]
        {
            1 - 2 * n.X * n.X, -2 * n.X * n.Y, -2 * n.X * n.Z, 0,
            -2 * n.Y * n.X, 1 - 2 * n.Y * n.Y, -2 * n.Y * n.Z, 0,
            -2 * n.Z * n.X, -2 * n.Z * n.Y, 1 - 2 * n.Z * n.Z, 0,
        });
    }

    /// <summary>this ∘ o (apply o first).</summary>
    public Affine Then(Affine next)
    {
        var a = next._m;
        var b = _m;
        var m = new double[12];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++) m[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j];
            m[i * 4 + 3] = a[i * 4] * b[3] + a[i * 4 + 1] * b[7] + a[i * 4 + 2] * b[11] + a[i * 4 + 3];
        }
        return new Affine(m);
    }
}

/// <summary>Orbit camera around a target, Z up, perspective.</summary>
public sealed class Camera
{
    public V3 Target = new(0, 0, 0);
    public double Distance = 260;
    public double Yaw = -55;    // degrees, around Z
    public double Pitch = 28;   // degrees above the XY plane
    public double Fov = 30;     // vertical field of view

    public V3 Forward
    {
        get
        {
            double y = Yaw * Math.PI / 180, p = Pitch * Math.PI / 180;
            return new V3(-Math.Cos(p) * Math.Cos(y), -Math.Cos(p) * Math.Sin(y), -Math.Sin(p)).Norm();
        }
    }

    public V3 Eye => Target - Forward * Distance;

    public (V3 right, V3 up) Basis()
    {
        var f = Forward;
        var worldUp = Math.Abs(f.Z) > 0.999 ? new V3(-Math.Cos(Yaw * Math.PI / 180), -Math.Sin(Yaw * Math.PI / 180), 0) * Math.Sign(-f.Z) : V3.UnitZ;
        var right = f.Cross(worldUp).Norm();
        return (right, right.Cross(f));
    }

    public double Focal(double height) => height / 2 / Math.Tan(Fov * Math.PI / 360);

    public double[] ToArray(double w, double h)
    {
        var e = Eye;
        var f = Forward;
        var (r, u) = Basis();
        return new[] { e.X, e.Y, e.Z, r.X, r.Y, r.Z, u.X, u.Y, u.Z, f.X, f.Y, f.Z, Focal(h), w / 2, h / 2 };
    }

    /// <summary>World point to screen (x, y, depth); depth &lt;= 0 when behind the camera.</summary>
    public (double x, double y, double z) Project(V3 p, double w, double h)
    {
        var (r, u) = Basis();
        var d = p - Eye;
        double z = d.Dot(Forward);
        if (z <= 1e-6) return (double.NaN, double.NaN, z);
        double f = Focal(h);
        return (w / 2 + f * d.Dot(r) / z, h / 2 - f * d.Dot(u) / z, z);
    }

    /// <summary>Ray through a screen pixel.</summary>
    public (V3 origin, V3 dir) Ray(double x, double y, double w, double h)
    {
        var (r, u) = Basis();
        double f = Focal(h);
        var dir = (Forward * f + r * (x - w / 2) - u * (y - h / 2)).Norm();
        return (Eye, dir);
    }

    public Camera Clone() => (Camera)MemberwiseClone();
}
