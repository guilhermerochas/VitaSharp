using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// paf::math (quaternion, matrix) against the firmware: with libpaf.suprx installed the same inputs go through the
/// module's own exports (loaded without starting it: the math needs no state) and through the HLE, and every result
/// float must agree (to 2e-5 absolute or relative: the module's sine, cosine, arc cosine and square-root
/// estimates are its own).
/// </summary>
[Collection("guest")]
public sealed unsafe class PafMathHleTests(GuestMemoryFixture fx)
{
    static readonly Lazy<string?> FirmwarePath = new(() =>
        JsonFirmware.ModulePath is { } json && Path.Combine(Path.GetDirectoryName(json)!, "libpaf.suprx") is var p && File.Exists(p) ? p : null);

    sealed record Case(string Fw, string Hle, uint[] Ints, float[] Floats, uint Out, uint OutSize);

    [PafFirmwareFact]
    public void MathMatchesFirmwareModule()
    {
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-pafmath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "vs0", "sys", "external"));
        File.Copy(FirmwarePath.Value!, Path.Combine(root, "vs0", "sys", "external", "libpaf.suprx"));
        try
        {
            using var emu = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
            emu.LoadModule("vs0:sys/external/libpaf.suprx");
            var paf = emu.Hle.Modules.OfType<Paf>().Single();
            uint arena = emu.Memory.Alloc(0x10000, "paf math test");
            uint bump = 0;
            uint Alloc(uint n) { uint p = arena + bump; bump += (n + 15) & ~15u; GuestMemory.Fill(p, 0, n); return p; }
            uint Floats(params float[] v) { uint p = Alloc((uint)v.Length * 4); for (int i = 0; i < v.Length; i++) GuestMemory.Write(p + (uint)i * 4, v[i]); return p; }

            // inputs: quaternions and matrices
            float h = MathF.Sqrt(0.5f);
            uint qUnit = Floats(0.18257419f, 0.36514837f, 0.5477226f, 0.73029674f), qOther = Floats(-h, 0, 0, h), qRaw = Floats(1, 2, 3, 4),
                qZero = Floats(0, 0, 0, 0), qSmall = Floats(0.1f, -0.2f, 0.3f, 0);
            uint ident = Floats(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            uint rot = Alloc(64);
            paf.GetType().GetMethod("QuatToMatrix", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(paf, [rot, qUnit, 1e-4f]);
            uint rigid = Alloc(64);
            GuestMemory.Copy(rigid, rot, 48);
            GuestMemory.Write(rigid + 48, 3f); GuestMemory.Write(rigid + 52, -2f); GuestMemory.Write(rigid + 56, 5f); GuestMemory.Write(rigid + 60, 1f);
            uint affine = Floats(2, 0.5f, 0, 0, -0.25f, 1.5f, 0.3f, 0, 0.1f, 0, 0.8f, 0, 4, 5, 6, 1);
            uint general = Floats(2, 1, 0, 0.5f, 1, 3, 1, 0, 0, 1, 4, 1, 1, 0, 2, 3);
            uint skew = Floats(1, 0, 0, 0, 0.1f, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            uint euler = Floats(0.3f, -0.7f, 1.1f, 0);
            uint lo = Floats(-1, -2, 0.5f, 0), hi = Floats(3, 4, 100, 0);
            uint[] mats = [ident, rot, rigid, affine, general];
            // exact quarter turns about Y, X and Z (gimbal lock for the orders whose middle axis it is)
            uint quarterY = Floats(0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1);
            uint quarterX = Floats(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);
            uint quarterZ = Floats(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

            var cases = new List<Case>();
            void C(string fw, string hle, uint outSize, uint[] ints, params float[] floats) => cases.Add(new(fw, hle, ints, floats, 0, outSize));
            foreach (uint m in mats)
            {
                C("_ZNK3paf4math6matrix9transposeEv", "MatrixTranspose", 64, [m]);
                C("_ZNK3paf4math6matrix7inverseEv", "MatrixInverse", 64, [m]);
                C("_ZNK3paf4math6matrix13fixedCoordSysEv", "MatrixFixedCoordSys", 64, [m]);
                C("_ZN3paf4math10quaternionC1ERKNS0_6matrixE", "QuatFromMatrix", 16, [m]);
            }
            // the Euler angles of rotations (a general matrix has no meaningful decomposition)
            foreach (uint m in new[] { ident, rot, rigid, quarterY, quarterX, quarterZ })
                foreach (var o in new[] { "XYZ", "XZY", "YXZ", "YZX", "ZXY", "ZYX" })
                    C($"_ZNK3paf4math6matrix21extractEulerAngles{o}Ev", "MatrixEuler" + o, 16, [m]);
            foreach (uint m in new[] { ident, rot, rigid })
                C("_ZNK3paf4math6matrix8inverse1Ev", "MatrixInverseRigid", 64, [m]);
            foreach (uint m in new[] { ident, rigid, affine })
                C("_ZNK3paf4math6matrix8inverse3Ev", "MatrixInverseAffine", 64, [m]);
            C("_ZN3paf4math6matrix5OrthoEffffff", "MatrixOrtho", 64, [], -1, 3, -2, 4, 0.5f, 100);
            C("_ZN3paf4math6matrix5OrthoENS0_2v3ES2_", "MatrixOrthoBox", 64, [lo, hi]);
            C("_ZN3paf4math6matrix7FrustumEffffff", "MatrixFrustum", 64, [], -1, 3, -2, 4, 0.5f, 100);
            foreach (uint q in new[] { qUnit, qOther, qRaw, qZero })
            {
                C("_ZN3paf4math6matrixC1ENS0_10quaternionE", "MatrixFromQuat", 64, [q]);
                C("_ZNK3paf4math10quaternion8toMatrixEf", "QuatToMatrix", 64, [q], 1e-4f);
                C("_ZNK3paf4math10quaternion7inverseEv", "QuatInverse", 16, [q]);
                C("_ZNK3paf4math10quaternion3logEf", "QuatLog", 16, [q], 1e-4f);
            }
            foreach (uint q in new[] { qSmall, qZero })
                C("_ZNK3paf4math10quaternion3expEf", "QuatExp", 16, [q], 1e-4f);
            foreach (var o in new[] { "XYZ", "XZY", "YXZ", "YZX", "ZXY", "ZYX" })
                C($"_ZN3paf4math10quaternion18FromEulerAngles{o}ENS0_2v3E", "QuatFromEuler" + o, 16, [euler]);
            foreach (float t in new[] { 0f, 0.25f, 0.5f, 1f })
                C("_ZN3paf4math10quaternion5SlerpES1_S1_ff", "QuatSlerp", 16, [qUnit, qOther], t, 1e-4f);

            var failures = new List<string>();
            var t0 = emu.Kernel.CreateThread("paf-math-test", 0, 0x10000100, 0x10000, 0, 0x70000);
            t0.HostBody = th =>
            {
                try
                {
                    foreach (var c in cases)
                    {
                        uint nid = NidDatabase.LookupName(c.Fw).First(n => n.Library.StartsWith("ScePaf", StringComparison.Ordinal)).Nid;
                        Assert.True(emu.Linker.TryGetFunctionExport(nid, out uint fn), c.Fw);
                        uint fwOut = Alloc(c.OutSize), hleOut = Alloc(c.OutSize);
                        // the constructors take their object as the first argument and fill it
                        for (int i = 0; i < c.Floats.Length; i++) ((float*)th.Core.State->D)[i] = c.Floats[i];
                        // the module may write into its by-value arguments: both sides start from the same inputs
                        var saved = c.Ints.Select(a => GuestMemory.ReadSpan(a, 64).ToArray()).ToArray();
                        th.CallGuest(fn, [fwOut, .. c.Ints]);
                        var fwArgs = c.Ints.Select(a => GuestMemory.ReadSpan(a, 64).ToArray()).ToArray();
                        for (int i = 0; i < c.Ints.Length; i++) saved[i].CopyTo(GuestMemory.Span(c.Ints[i], 64));
                        var mi = paf.GetType().GetMethod(c.Hle, BindingFlags.Instance | BindingFlags.NonPublic)!;
                        var args = new List<object>();
                        int fi = 0, ui = 0;
                        uint[] ints = [hleOut, .. c.Ints];
                        foreach (var p in mi.GetParameters()) args.Add(p.ParameterType == typeof(float) ? (object)c.Floats[fi++] : ints[ui++]);
                        mi.Invoke(paf, args.ToArray());
                        for (int i = 0; i < c.Ints.Length; i++)
                            if (!GuestMemory.ReadSpan(c.Ints[i], 64).SequenceEqual(fwArgs[i])) failures.Add($"{c.Hle}: argument {i} written differently");
                        for (int i = 0; i < c.Ints.Length; i++) saved[i].CopyTo(GuestMemory.Span(c.Ints[i], 64));
                        for (uint k = 0; k < c.OutSize; k += 4)
                        {
                            float a = GuestMemory.Read<float>(fwOut + k), b = GuestMemory.Read<float>(hleOut + k);
                            bool same = a.Equals(b) || MathF.Abs(a - b) <= 2e-5f || MathF.Abs(a - b) <= 2e-5f * MathF.Max(MathF.Abs(a), MathF.Abs(b));
                            if (!same) { failures.Add($"{c.Hle}({string.Join(",", c.Ints.Select(x => (x - arena).ToString("X")))};{string.Join(",", c.Floats)}) [{k / 4}] fw={a:R} hle={b:R}"); }
                        }
                    }
                    // isCoordSys: the answer for each matrix and tolerance
                    uint isNid = NidDatabase.LookupName("_ZNK3paf4math6matrix10isCoordSysEf").First().Nid;
                    Assert.True(emu.Linker.TryGetFunctionExport(isNid, out uint isFn));
                    foreach (uint m in new[] { ident, rot, rigid, affine, general, skew })
                        foreach (float eps in new[] { 1e-4f, 0.2f })
                        {
                            ((float*)th.Core.State->D)[0] = eps;
                            bool fw = th.CallGuest(isFn, m) != 0;
                            bool hle = (bool)paf.GetType().GetMethod("MatrixIsCoordSys", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(paf, [eps, m])!;
                            if (fw != hle) failures.Add($"isCoordSys({m - arena:X}, {eps}) fw={fw} hle={hle}");
                        }
                }
                catch (Exception e) { failures.Add("EXCEPTION " + e); }
            };
            t0.Start(0, 0);
            Assert.True(t0.WaitForExit(120_000), "paf math test thread did not finish");
            emu.Kernel.DeleteThread(t0);
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CoordSysErrorNamesTheFailedRule()
    {
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-pafmath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var emu = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
            var paf = emu.Hle.Modules.OfType<Paf>().Single();
            object? Call(string name, params object[] a) => paf.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(paf, a);
            uint m = fx.Memory.Alloc(64, "paf math"), s = fx.Memory.Alloc(16, "paf math");
            float[] skew = [1, 0, 0, 0, 0.1f, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
            for (int i = 0; i < 16; i++) GuestMemory.Write(m + (uint)i * 4, skew[i]);
            Assert.Equal(false, Call("MatrixIsCoordSys", 1e-4f, m));
            Call("MatrixCoordSysError", s);
            Assert.Equal("Y_NOT_UNIT_LENGTH", GuestMemory.ReadCString(GuestMemory.R32(s)));
            GuestMemory.Write(m + 16, 0f);
            Assert.Equal(true, Call("MatrixIsCoordSys", 1e-4f, m));
            Call("MatrixCoordSysError", s);
            Assert.Equal(0u, GuestMemory.R32(s + 4));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}

public sealed class PafFirmwareFactAttribute : FactAttribute
{
    public PafFirmwareFactAttribute()
    {
        if (JsonFirmware.ModulePath is not { } json || !File.Exists(Path.Combine(Path.GetDirectoryName(json)!, "libpaf.suprx")))
            Skip = $"libpaf.suprx not found (set {EnvVars.FirmwareDir} to a Vita3K data folder or vs0, or {EnvVars.FirmwarePup})";
    }
}
