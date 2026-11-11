using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// paf::math::rect (a centre and a size: four floats) against libpaf: the constructors and setters, the edge setters
/// that keep the opposite edge (and stop at it), the align_* moves, move, the overlap / inside / point tests and the
/// comparison. Each call's result and the rect it left are logged; with libpaf.suprx the module's exports give the same
/// log, without it the HLE log must hash to the firmware's and a few results are checked directly.
/// </summary>
[Collection("guest")]
public sealed unsafe class PafRectFirmwareTests(GuestMemoryFixture fx)
{
    /// <summary>The hash of the firmware's log (MatchesFirmwareModule names it when it changes).</summary>
    const string FirmwareLogHash = "809626268B605A11";

    static readonly Lazy<string?> FirmwarePath = new(() =>
        JsonFirmware.ModulePath is { } json && Path.Combine(Path.GetDirectoryName(json)!, "libpaf.suprx") is var p && File.Exists(p) ? p : null);

    static List<string> RunAll(GuestMemoryFixture fx, string? module)
    {
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-pafrect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "vs0", "sys", "external"));
        if (module != null)
        {
            File.Copy(module, Path.Combine(root, "vs0", "sys", "external", "libpaf.suprx"));
        }

        var log = new List<string>();
        try
        {
            using var emu = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
            if (module != null)
            {
                emu.LoadModule("vs0:sys/external/libpaf.suprx");
            }

            var paf = emu.Hle.Modules.OfType<Paf>().Single();
            uint arena = emu.Memory.Alloc(0x1000, "paf rect test");
            uint me = arena, other = arena + 0x20, v = arena + 0x40;
            void Rect(uint p, float cx, float cy, float w, float h)
            {
                GuestMemory.Write(p, cx);
                GuestMemory.Write(p + 4, cy);
                GuestMemory.Write(p + 8, w);
                GuestMemory.Write(p + 12, h);
            }
            string Dump(uint p) => string.Join(",", Enumerable.Range(0, 4).Select(i => GuestMemory.Read<float>(p + (uint)i * 4).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            var th = emu.Kernel.CreateThread("paf-rect-test", 0, 0x10000100, 0x10000, 0, 0x70000);
            Exception? failure = null;
            th.HostBody = t =>
            {
                try
                {
                    // name, HLE method, pointer arguments, float arguments
                    void C(string fw, string hle, uint[] ptrs, params float[] floats)
                    {
                        uint r;
                        if (module != null)
                        {
                            uint nid = NidDatabase.LookupName(fw).First(n => n.Library.StartsWith("ScePaf", StringComparison.Ordinal)).Nid;
                            Assert.True(emu.Linker.TryGetFunctionExport(nid, out uint fn), fw);
                            for (int i = 0; i < floats.Length; i++)
                            {
                                ((float*)t.Core.State->D)[i] = floats[i];
                            }

                            r = t.CallGuest(fn, ptrs);
                        }
                        else
                        {
                            var m = typeof(Paf).GetMethod(hle, BindingFlags.Instance | BindingFlags.NonPublic)!;
                            int pi = 0, fi = 0;
                            var args = m.GetParameters().Select(p => p.ParameterType == typeof(float) ? (object)floats[fi++] : ptrs[pi++]).ToArray();
                            object? res = m.Invoke(paf, args);
                            r = res switch { bool b => b ? 1u : 0u, uint u => u, _ => 0 };
                        }
                        var ret = typeof(Paf).GetMethod(hle, BindingFlags.Instance | BindingFlags.NonPublic)!.ReturnType;
                        string rs = ret == typeof(bool) ? $" = {(r & 0xFF) != 0}" : ret == typeof(uint) ? $" = @{r - arena:X}" : "";
                        log.Add($"{hle}({string.Join(",", floats)}){rs} {Dump(me)}");
                    }
                    var start = new (float, float, float, float)[] { (0, 0, 10, 4), (5, -3, 0, 2), (-2.5f, 7, 3, 3) };
                    foreach (var (cx, cy, w, h) in start)
                    {
                        void Reset() => Rect(me, cx, cy, w, h);
                        Reset();
                        C("_ZN3paf4math4rect5clearEv", "RectClear", [me]);
                        Reset();
                        C("_ZN3paf4math4rect5_0011Ev", "RectUnit", [me]);
                        Reset();
                        C("_ZN3paf4math4rect3setEffff", "RectSet", [me], 1, 2, -3, 4);
                        Rect(v, 9, 8, 0, 0);
                        Rect(v + 8, 6, -5, 0, 0);
                        Reset();
                        C("_ZN3paf4math4rect3setERKNS0_2v2ES4_", "RectSetV2", [me, v, v + 8]);
                        Reset();
                        C("_ZN3paf4math4rect8set_sizeEff", "RectSetSize", [me], 3, -1);
                        Reset();
                        C("_ZN3paf4math4rect9set_widthEf", "RectSetWidth", [me], -2);
                        Reset();
                        C("_ZN3paf4math4rect10set_heightEf", "RectSetHeight", [me], 6);
                        Reset();
                        C("_ZN3paf4math4rect4moveEff", "RectMove", [me], 1.5f, -2);
                        foreach (float e in new[] { -20f, 0f, 1f, 20f })
                        {
                            Reset();
                            C("_ZN3paf4math4rect8set_leftEf", "RectSetLeft", [me], e);
                            Reset();
                            C("_ZN3paf4math4rect9set_rightEf", "RectSetRight", [me], e);
                            Reset();
                            C("_ZN3paf4math4rect7set_topEf", "RectSetTop", [me], e);
                            Reset();
                            C("_ZN3paf4math4rect10set_bottomEf", "RectSetBottom", [me], e);
                            Reset();
                            C("_ZN3paf4math4rect10align_leftEf", "RectAlignLeft", [me], e);
                            Reset();
                            C("_ZN3paf4math4rect11align_rightEf", "RectAlignRight", [me], e);
                            Reset();
                            C("_ZN3paf4math4rect9align_topEf", "RectAlignTop", [me], e);
                            Reset();
                            C("_ZN3paf4math4rect12align_bottomEf", "RectAlignBottom", [me], e);
                        }
                        foreach (var (ox, oy, ow, oh) in new (float, float, float, float)[] { (0, 0, 10, 4), (10, 0, 10, 4), (1, 1, 2, 1), (100, 100, 1, 1), (5, 2, 0, 0) })
                        {
                            Reset();
                            Rect(other, ox, oy, ow, oh);
                            C("_ZN3paf4math4rect3hitERKS1_", "RectHit", [me, other]);
                            C("_ZN3paf4math4rect10is_in_rectERKS1_", "RectIsIn", [me, other]);
                            C("_ZNK3paf4math4recteqERKS1_", "RectDiffers", [me, other]);
                        }
                        foreach (var (px, py) in new (float, float)[] { (0, 0), (5, 2), (5.01f, 0), (-5, -2), (cx, cy) })
                        {
                            Reset();
                            C("_ZN3paf4math4rect16is_point_in_rectEff", "RectHasPoint", [me], px, py);
                        }
                        Reset();
                        Rect(other, 1, 1, 1, 1);
                        C("_ZN3paf4math4rectaSERKS1_", "RectCopy", [me, other]);
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }
            };
            th.Start(0, 0);
            Assert.True(th.WaitForExit(60_000), "paf rect test thread did not finish");
            emu.Kernel.DeleteThread(th);
            if (failure != null)
            {
                throw new Xunit.Sdk.XunitException(failure.ToString());
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
        return log;
    }

    static string Hash(List<string> log) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", log))))[..16];

    [Fact]
    public void Hle_MatchesTheFirmwareLog()
    {
        var log = RunAll(fx, null);
        // a left edge past the right one stops there (width 0, centre on the right edge); a touching rect is no hit
        Assert.Contains("RectSetLeft(20) 5,0,0,4", log);
        Assert.Contains("RectHit() = False 0,0,10,4", log);
        Assert.Equal(FirmwareLogHash, Hash(log));
    }

    [PafFirmwareFact]
    public void MatchesFirmwareModule()
    {
        var fw = RunAll(fx, FirmwarePath.Value);
        var hle = RunAll(fx, null);
        var diff = fw.Zip(hle).Where(p => p.First != p.Second).Select(p => $"fw:  {p.First}\nhle: {p.Second}").ToList();
        Assert.True(diff.Count == 0 && fw.Count == hle.Count, $"{diff.Count} calls differ from libpaf ({fw.Count} / {hle.Count} lines):\n" + string.Join("\n", diff.Take(20)));
        Assert.True(Hash(fw) == FirmwareLogHash, $"FirmwareLogHash is out of date: the firmware log hashes to {Hash(fw)}");
    }
}
