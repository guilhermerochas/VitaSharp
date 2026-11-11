using System.Text;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// paf::math::quaternion and paf::math::matrix (libpaf 3.74). A quaternion is four floats (x, y, z, w); a matrix is
/// four v4 rows of four floats stored the OpenGL way: memory row i is column i of the transform (the translation is
/// row 3, a projection keeps its -1 at [2][3]), so a point is transformed as p' = p.x*row0 + p.y*row1 + p.z*row2 +
/// row3. Arguments of these types come by reference and results go through the hidden result pointer. The
/// reciprocals use the NEON estimate refined twice (vrecpe + two vrecps), as the module does; sine, cosine and the
/// arc cosine are the host's (the module has its own polynomials, a few ulps apart).
/// </summary>
public sealed partial class Paf
{
    static float[] M(uint a)
    {
        var m = new float[16];
        for (int i = 0; i < 16; i++) m[i] = F(a + (uint)i * 4);
        return m;
    }

    static void M(uint a, float[] m) { for (int i = 0; i < 16; i++) F(a + (uint)i * 4, m[i]); }

    static (float X, float Y, float Z, float W) V4(uint a) => (F(a), F(a + 4), F(a + 8), F(a + 12));
    static void V4(uint a, float x, float y, float z, float w) { F(a, x); F(a + 4, y); F(a + 8, z); F(a + 12, w); }

    /// <summary>1/v the way the module computes it: the NEON estimate and two Newton-Raphson steps (vrecps).</summary>
    static float Recip(float v)
    {
        float e = Neon.RecipEstimate(v);
        e *= 2f - e * v;
        return e * (2f - e * v);
    }

    // ------------------------------------------------------------------ matrices

    [Hle("_ZNK3paf4math6matrix9transposeEv")]
    void MatrixTranspose(uint ret, uint me)
    {
        var m = M(me);
        var t = new float[16];
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++) t[c * 4 + r] = m[r * 4 + c];
        M(ret, t);
    }

    /// <summary>glOrtho(left, right, bottom, top, near, far).</summary>
    [Hle("_ZN3paf4math6matrix5OrthoEffffff")]
    void MatrixOrtho(float l, float r, float b, float t, float n, float f, uint ret)
    {
        float sx = 1f / (r - l), sy = 1f / (t - b), sz = 1f / (n - f);
        M(ret, [sx * 2f, 0, 0, 0,
                0, sy * 2f, 0, 0,
                0, 0, sz * 2f, 0,
                -((l + r) * sx), -((b + t) * sy), (f + n) * sz, 1f]);
    }

    /// <summary>(const v3 &amp;min, const v3 &amp;max): glOrtho(min.x, max.x, min.y, max.y, min.z, max.z).</summary>
    [Hle("_ZN3paf4math6matrix5OrthoENS0_2v3ES2_")]
    void MatrixOrthoBox(uint ret, uint lo, uint hi) => MatrixOrtho(F(lo), F(hi), F(lo + 4), F(hi + 4), F(lo + 8), F(hi + 8), ret);

    /// <summary>glFrustum(left, right, bottom, top, near, far).</summary>
    [Hle("_ZN3paf4math6matrix7FrustumEffffff")]
    void MatrixFrustum(float l, float r, float b, float t, float n, float f, uint ret)
    {
        float n2 = n * 2f, fn = f - n;
        M(ret, [n2 / (r - l), 0, 0, 0,
                0, n2 / (t - b), 0, 0,
                (l + r) / (r - l), (b + t) / (t - b), -(f + n) / fn, -1f,
                0, 0, f * -2f * n / fn, 0]);
    }

    /// <summary>The rotation of a quaternion (<see cref="QuatToMatrix"/> with the module's 1e-4).</summary>
    [Hle("_ZN3paf4math6matrixC1ENS0_10quaternionE")]
    [Hle("_ZN3paf4math6matrixC2ENS0_10quaternionE")]
    uint MatrixFromQuat(uint me, uint q) { QuatToMatrix(me, q, 1e-4f); return me; }

    // ------------------------------------------------------------------ quaternions

    /// <summary>The rotation matrix of the quaternion scaled by 2/|q|^2 (any length works); the identity for a zero
    /// quaternion. The tolerance argument is not used.</summary>
    [Hle("_ZNK3paf4math10quaternion8toMatrixEf")]
    void QuatToMatrix(uint ret, uint me, float eps)
    {
        var (x, y, z, w) = V4(me);
        float n = x * x + y * y + (z * z + w * w);
        if (n == 0)
        {
            M(ret, [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
            return;
        }
        float s = 2f / n;
        float xs = x * s, ys = y * s, zs = z * s;
        float xx = x * xs, yy = y * ys, zz = z * zs, xy = x * ys, xz = z * xs, yz = y * zs, wx = w * xs, wy = w * ys, wz = w * zs;
        M(ret, [1f - yy - zz, xy + wz, xz - wy, 0,
                xy - wz, 1f - xx - zz, yz + wx, 0,
                wy + xz, yz - wx, 1f - xx - yy, 0,
                0, 0, 0, 1]);
    }

    /// <summary>The inverse: the conjugate over the squared length.</summary>
    [Hle("_ZNK3paf4math10quaternion7inverseEv")]
    void QuatInverse(uint ret, uint me)
    {
        var (x, y, z, w) = V4(me);
        float r = Recip(x * x + y * y + (z * z + w * w));
        V4(ret, -(x * r), -(y * r), -(z * r), w * r);
    }

    /// <summary>(v3 &amp;axis, float &amp;angle): the unit axis (its fourth float is w / |xyz|) and the angle 2 acos(w);
    /// a quaternion without a vector part gives the axis (0, 0, 1) and angle 0.</summary>
    [Hle("_ZN3paf4math10quaternion11toAxisAngleERNS0_2v3ERf")]
    void QuatToAxisAngle(uint me, uint axis, uint angle)
    {
        var (x, y, z, w) = V4(me);
        float n = x * x + y * y + z * z;
        float len = n > 1.17549435e-38f ? MathF.Sqrt(n) : 0f;
        if (len > 0)
        {
            float r = Recip(len);
            F(angle, MathF.Acos(Math.Clamp(w, -1f, 1f)) * 2f);
            V4(axis, x * r, y * r, z * r, w * r);
        }
        else
        {
            F(angle, 0);
            V4(axis, 0, 0, 1, 0);
        }
    }

    /// <summary>(a, b, t, threshold): spherical interpolation along the shorter arc (b is negated when a.b &lt; 0); t = 0
    /// gives a, t = 1 gives b, and a when the angle between them is below the threshold.</summary>
    [Hle("_ZN3paf4math10quaternion5SlerpES1_S1_ff")]
    void QuatSlerp(float t, float threshold, uint ret, uint a, uint b)
    {
        var qa = V4(a);
        var qb = V4(b);
        if (t == 0) { V4(ret, qa.X, qa.Y, qa.Z, qa.W); return; }
        if (t == 1) { V4(ret, qb.X, qb.Y, qb.Z, qb.W); return; }
        if (qa.X * qb.X + qa.Y * qb.Y + (qa.Z * qb.Z + qa.W * qb.W) < 0) qb = (-qb.X, -qb.Y, -qb.Z, -qb.W);
        float cos = qa.X * qb.X + qa.Y * qb.Y + (qa.Z * qb.Z + qa.W * qb.W);
        float angle = MathF.Acos(Math.Clamp(cos, -1f, 1f));
        if (angle < threshold) { V4(ret, qa.X, qa.Y, qa.Z, qa.W); return; }
        float inv = 1f / MathF.Sin(angle);
        float wa = MathF.Sin((1f - t) * angle) * inv, wb = MathF.Sin(angle * t) * inv;
        V4(ret, qa.X * wa + qb.X * wb, qa.Y * wa + qb.Y * wb, qa.Z * wa + qb.Z * wb, qa.W * wa + qb.W * wb);
    }

    // ------------------------------------------------------------------ more matrix operations

    /// <summary>The general inverse (cofactors over the determinant).</summary>
    [Hle("_ZNK3paf4math6matrix7inverseEv")]
    void MatrixInverse(uint ret, uint me) => M(ret, Invert4(M(me)));

    static float[] Invert4(float[] m)
    {
        var inv = new float[16];
        inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
        inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
        inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
        inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
        inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
        inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
        inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
        inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
        inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
        inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
        inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
        inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
        inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
        inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
        inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
        inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
        float det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
        float r = 1f / det;
        for (int i = 0; i < 16; i++) inv[i] *= r;
        return inv;
    }

    /// <summary>The inverse of a rigid transform (a rotation and a translation): the transposed rotation and the
    /// translation taken back through it.</summary>
    [Hle("_ZNK3paf4math6matrix8inverse1Ev")]
    void MatrixInverseRigid(uint ret, uint me)
    {
        var m = M(me);
        var r = new float[16];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) r[i * 4 + j] = m[j * 4 + i];
        for (int j = 0; j < 3; j++) r[12 + j] = -(m[12] * r[j] + m[13] * r[4 + j] + m[14] * r[8 + j]);
        r[15] = 1;
        M(ret, r);
    }

    /// <summary>The inverse of the 3x3 part (any linear transform); the result has no translation.</summary>
    [Hle("_ZNK3paf4math6matrix8inverse3Ev")]
    void MatrixInverseAffine(uint ret, uint me)
    {
        var m = M(me);
        float a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], k = m[10];
        float c0 = e * k - f * h, c1 = f * g - d * k, c2 = d * h - e * g;
        float rdet = 1f / (a * c0 + b * c1 + c * c2);
        var r = new float[16];
        r[0] = c0 * rdet; r[1] = (c * h - b * k) * rdet; r[2] = (b * f - c * e) * rdet;
        r[4] = c1 * rdet; r[5] = (a * k - c * g) * rdet; r[6] = (c * d - a * f) * rdet;
        r[8] = c2 * rdet; r[9] = (b * g - a * h) * rdet; r[10] = (a * e - b * d) * rdet;
        r[15] = 1;
        M(ret, r);
    }

    // ------------------------------------------------------------------ coordinate systems

    /// <summary>The reason the last isCoordSys failed (one bit), read by getCoordSysError.</summary>
    uint _coordSysError;

    static readonly (uint Bit, string Name)[] CoordSysErrors =
    [
        (1, "MAT_X_NAN"), (2, "MAT_Y_NAN"), (4, "MAT_Z_NAN"), (8, "MAT_W_NAN"), (0x10, "NOT_RIGHT_HANDED"),
        (0x20, "X_NOT_UNIT_LENGTH"), (0x40, "Y_NOT_UNIT_LENGTH"), (0x80, "Z_NOT_UNIT_LENGTH"), (0x4000, "X_ZERO_LENGTH"),
        (0x8000, "Y_ZERO_LENGTH"), (0x10000, "Z_ZERO_LENGTH"), (0x100, "XY_NOT_PERPENDICULAR"), (0x200, "YZ_NOT_PERPENDICULAR"),
        (0x400, "ZX_NOT_PERPENDICULAR"), (0x800, "WW_NOT_1"), (0x1000, "LAST_ROW_NOT_0001"), (0x2000, "MAT_NOT_UNIFORM_SCALE"),
    ];

    static float Dot4(float[] m, int a, int b) => m[a] * m[b] + m[a + 1] * m[b + 1] + (m[a + 2] * m[b + 2] + m[a + 3] * m[b + 3]);

    /// <summary>A length the module's way: lengths whose square is below FLT_MIN count as 0.</summary>
    static float Len(float sq) => sq > 1.17549435e-38f ? MathF.Sqrt(sq) : 0f;

    /// <summary>(tolerance): whether the matrix is a right-handed orthonormal frame with a translation (no NaN, axes of
    /// unit length and perpendicular, W column 0 0 0 1); the first failed rule is kept for getCoordSysError.</summary>
    [Hle("_ZNK3paf4math6matrix10isCoordSysEf")]
    bool MatrixIsCoordSys(float eps, uint me)
    {
        var m = M(me);
        _coordSysError = 0;
        for (int r = 0; r < 4; r++)
            if (float.IsNaN(m[r * 4]) || float.IsNaN(m[r * 4 + 1]) || float.IsNaN(m[r * 4 + 2]) || float.IsNaN(m[r * 4 + 3]))
            { _coordSysError = 1u << r; return false; }
        float cx = m[1] * m[6] - m[2] * m[5], cy = m[2] * m[4] - m[0] * m[6], cz = m[0] * m[5] - m[1] * m[4];
        if (!(cx * m[8] + cy * m[9] + cz * m[10] > 0)) { _coordSysError = 0x10; return false; }
        for (int r = 0; r < 3; r++)
            if (!(MathF.Abs(Len(Dot4(m, r * 4, r * 4)) - 1f) <= eps)) { _coordSysError = 0x20u << r; return false; }
        if (!(MathF.Abs(Dot4(m, 4, 0)) < eps)) { _coordSysError = 0x100; return false; }
        if (!(MathF.Abs(Dot4(m, 8, 4)) < eps)) { _coordSysError = 0x200; return false; }
        if (!(MathF.Abs(Dot4(m, 0, 8)) < eps)) { _coordSysError = 0x400; return false; }
        if (m[15] != 1f) { _coordSysError = 0x800; return false; }
        if (m[3] != 0 || m[7] != 0 || m[11] != 0) { _coordSysError = 0x1000; return false; }
        return true;
    }

    /// <summary>The names of the last isCoordSys failure, concatenated into a paf::string.</summary>
    [Hle("_ZN3paf4math6matrix16getCoordSysErrorEv")]
    void MatrixCoordSysError(uint ret)
    {
        var sb = new StringBuilder();
        foreach (var (bit, name) in CoordSysErrors) if ((_coordSysError & bit) != 0) sb.Append(name);
        StrInit(ret);
        if (sb.Length != 0) StrAssign(ret, Encoding.ASCII.GetBytes(sb.ToString()));
    }

    /// <summary>Re-orthonormalizes the frame: X = row 0 over its length, Z = X x row 1 normalized, Y = Z x X; the
    /// translation is kept.</summary>
    [Hle("_ZNK3paf4math6matrix13fixedCoordSysEv")]
    void MatrixFixedCoordSys(uint ret, uint me)
    {
        var m = M(me);
        float rx = Recip(Len(Dot4(m, 0, 0)));
        float x0 = m[0] * rx, x1 = m[1] * rx, x2 = m[2] * rx, x3 = m[3] * rx;
        float z0 = x1 * m[6] - x2 * m[5], z1 = x2 * m[4] - x0 * m[6], z2 = x0 * m[5] - x1 * m[4];
        float rz = Recip(Len(z0 * z0 + z1 * z1 + z2 * z2));
        z0 *= rz; z1 *= rz; z2 *= rz;
        float y0 = z1 * x2 - z2 * x1, y1 = z2 * x0 - z0 * x2, y2 = z0 * x1 - z1 * x0;
        M(ret, [x0, x1, x2, x3, y0, y1, y2, 0, z0, z1, z2, 0, m[12], m[13], m[14], m[15]]);
    }

    // ------------------------------------------------------------------ Euler angles
    // "XYZ" is R = Rx(v.x) * Ry(v.y) * Rz(v.z) (column vectors), the quaternion qx * qy * qz; the matrix rows in memory
    // are the columns of R.

    static (float X, float Y, float Z, float W) QMul((float X, float Y, float Z, float W) a, (float X, float Y, float Z, float W) b) =>
        (a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
         a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
         a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
         a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    static (float X, float Y, float Z, float W) AxisQuat(int axis, float angle)
    {
        float s = MathF.Sin(angle * 0.5f), c = MathF.Cos(angle * 0.5f);
        return axis switch { 0 => (s, 0, 0, c), 1 => (0, s, 0, c), _ => (0, 0, s, c) };
    }

    void QuatFromEuler(uint ret, uint angles, int i, int j, int k)
    {
        // the module halves the angles in its by-value copy of the argument (the caller's temporary)
        float[] a = [F(angles), F(angles + 4), F(angles + 8)];
        for (uint n = 0; n < 4; n++) F(angles + n * 4, F(angles + n * 4) * 0.5f);
        var q = QMul(QMul(AxisQuat(i, a[i]), AxisQuat(j, a[j])), AxisQuat(k, a[k]));
        V4(ret, q.X, q.Y, q.Z, q.W);
    }

    [Hle("_ZN3paf4math10quaternion18FromEulerAnglesXYZENS0_2v3E")] void QuatFromEulerXYZ(uint ret, uint v) => QuatFromEuler(ret, v, 0, 1, 2);
    [Hle("_ZN3paf4math10quaternion18FromEulerAnglesXZYENS0_2v3E")] void QuatFromEulerXZY(uint ret, uint v) => QuatFromEuler(ret, v, 0, 2, 1);
    [Hle("_ZN3paf4math10quaternion18FromEulerAnglesYXZENS0_2v3E")] void QuatFromEulerYXZ(uint ret, uint v) => QuatFromEuler(ret, v, 1, 0, 2);
    [Hle("_ZN3paf4math10quaternion18FromEulerAnglesYZXENS0_2v3E")] void QuatFromEulerYZX(uint ret, uint v) => QuatFromEuler(ret, v, 1, 2, 0);
    [Hle("_ZN3paf4math10quaternion18FromEulerAnglesZXYENS0_2v3E")] void QuatFromEulerZXY(uint ret, uint v) => QuatFromEuler(ret, v, 2, 0, 1);
    [Hle("_ZN3paf4math10quaternion18FromEulerAnglesZYXENS0_2v3E")] void QuatFromEulerZYX(uint ret, uint v) => QuatFromEuler(ret, v, 2, 1, 0);

    /// <summary>The angles of R = Ri(a) Rj(b) Rk(c) (a Tait-Bryan decomposition), as a v3 by axis.</summary>
    void MatrixEuler(uint ret, uint me, int i, int j, int k)
    {
        var m = M(me);
        float R(int r, int c) => m[c * 4 + r];
        float s = (i, j, k) is (0, 1, 2) or (1, 2, 0) or (2, 0, 1) ? 1f : -1f;
        var v = new float[3];
        float e = s * R(i, k);
        if (e < 1f && e > -1f)
        {
            v[j] = MathF.Asin(e);
            v[i] = MathF.Atan2(-s * R(j, k), R(k, k));
            v[k] = MathF.Atan2(-s * R(i, j), R(i, i));
        }
        else
        {
            // gimbal lock: the middle angle is +-90 degrees, the last one is taken as 0 and the first is what is left
            // of the rotation about axis i (R * Rj(-b))
            v[j] = e >= 1f ? MathF.PI / 2 : -MathF.PI / 2;
            int i1 = (i + 1) % 3, i2 = (i + 2) % 3;
            float sb = MathF.Sin(-v[j]), cb = MathF.Cos(-v[j]);
            int j1 = (j + 1) % 3, j2 = (j + 2) % 3;
            // column c of R * Rj(t): Rj mixes columns j1 and j2
            float Col(int r, int col) => col == j1 ? R(r, j1) * cb + R(r, j2) * sb : col == j2 ? R(r, j2) * cb - R(r, j1) * sb : R(r, col);
            v[i] = MathF.Atan2(Col(i2, i1), Col(i1, i1));
            v[k] = 0;
        }
        V4(ret, v[0], v[1], v[2], 0);
    }

    [Hle("_ZNK3paf4math6matrix21extractEulerAnglesXYZEv")] void MatrixEulerXYZ(uint ret, uint me) => MatrixEuler(ret, me, 0, 1, 2);
    [Hle("_ZNK3paf4math6matrix21extractEulerAnglesXZYEv")] void MatrixEulerXZY(uint ret, uint me) => MatrixEuler(ret, me, 0, 2, 1);
    [Hle("_ZNK3paf4math6matrix21extractEulerAnglesYXZEv")] void MatrixEulerYXZ(uint ret, uint me) => MatrixEuler(ret, me, 1, 0, 2);
    [Hle("_ZNK3paf4math6matrix21extractEulerAnglesYZXEv")] void MatrixEulerYZX(uint ret, uint me) => MatrixEuler(ret, me, 1, 2, 0);
    [Hle("_ZNK3paf4math6matrix21extractEulerAnglesZXYEv")] void MatrixEulerZXY(uint ret, uint me) => MatrixEuler(ret, me, 2, 0, 1);
    [Hle("_ZNK3paf4math6matrix21extractEulerAnglesZYXEv")] void MatrixEulerZYX(uint ret, uint me) => MatrixEuler(ret, me, 2, 1, 0);

    // ------------------------------------------------------------------ quaternion from a matrix, exp and log

    /// <summary>The rotation of the matrix's 3x3 part as a quaternion (by the largest of the trace and the diagonal).</summary>
    [Hle("_ZN3paf4math10quaternionC1ERKNS0_6matrixE")]
    [Hle("_ZN3paf4math10quaternionC2ERKNS0_6matrixE")]
    uint QuatFromMatrix(uint me, uint mat)
    {
        var m = M(mat);
        float R(int r, int c) => m[c * 4 + r];
        float tr = R(0, 0) + R(1, 1) + R(2, 2);
        float x, y, z, w;
        if (tr > 0)
        {
            float s = MathF.Sqrt(tr + 1f) * 2f;
            w = 0.25f * s; x = (R(2, 1) - R(1, 2)) / s; y = (R(0, 2) - R(2, 0)) / s; z = (R(1, 0) - R(0, 1)) / s;
        }
        else if (R(0, 0) > R(1, 1) && R(0, 0) > R(2, 2))
        {
            float s = MathF.Sqrt(1f + R(0, 0) - R(1, 1) - R(2, 2)) * 2f;
            w = (R(2, 1) - R(1, 2)) / s; x = 0.25f * s; y = (R(0, 1) + R(1, 0)) / s; z = (R(0, 2) + R(2, 0)) / s;
        }
        else if (R(1, 1) > R(2, 2))
        {
            float s = MathF.Sqrt(1f + R(1, 1) - R(0, 0) - R(2, 2)) * 2f;
            w = (R(0, 2) - R(2, 0)) / s; x = (R(0, 1) + R(1, 0)) / s; y = 0.25f * s; z = (R(1, 2) + R(2, 1)) / s;
        }
        else
        {
            float s = MathF.Sqrt(1f + R(2, 2) - R(0, 0) - R(1, 1)) * 2f;
            w = (R(1, 0) - R(0, 1)) / s; x = (R(0, 2) + R(2, 0)) / s; y = (R(1, 2) + R(2, 1)) / s; z = 0.25f * s;
        }
        V4(me, x, y, z, w);
        return me;
    }

    /// <summary>(tolerance): the logarithm of a unit quaternion, (v * theta / sin theta, 0) with theta = acos(w); just
    /// (v, 0) when |w| &gt;= 1 or |sin theta| is below the tolerance.</summary>
    [Hle("_ZNK3paf4math10quaternion3logEf")]
    void QuatLog(float eps, uint ret, uint me)
    {
        var (x, y, z, w) = V4(me);
        if (MathF.Abs(w) < 1f)
        {
            float theta = MathF.Acos(Math.Clamp(w, -1f, 1f)), s = MathF.Sin(theta);
            if (eps <= MathF.Abs(s))
            {
                float k = theta / s;
                V4(ret, x * k, y * k, z * k, 0);
                return;
            }
        }
        V4(ret, x, y, z, 0);
    }

    /// <summary>(tolerance): the exponential, (v * sin theta / theta, cos theta) with theta = |v| (v kept as it is when
    /// |sin theta| is not above the tolerance).</summary>
    [Hle("_ZNK3paf4math10quaternion3expEf")]
    void QuatExp(float eps, uint ret, uint me)
    {
        var (x, y, z, _) = V4(me);
        float theta = Len(x * x + y * y + z * z), s = MathF.Sin(theta);
        if (eps < MathF.Abs(s))
        {
            float k = s / theta;
            x *= k; y *= k; z *= k;
        }
        V4(ret, x, y, z, MathF.Cos(theta));
    }
}
