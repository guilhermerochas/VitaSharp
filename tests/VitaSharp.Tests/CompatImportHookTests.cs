using System.Buffers.Binary;
using VitaSharp.Core.Compat;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceCompat's start path asks ksceSblAimgrIsDEX through the module's own import of it (SceCompat 3.74 0x81000228,
/// only after a step that failed: the activation data, then the title's license), so a kernel plugin that writes
/// over SceCompat's import stub (an import hook on the module, what a PSP compatibility plugin installs to start the
/// compat core without a retail PSP license) answers those two questions, as on the console. Everything is synthetic:
/// a firmware SceCompat in os0:kd that imports SceSblAIMgrForDriver ksceSblAimgrIsDEX (the FW 3.74 NIDs), a kernel
/// module whose function answers a constant, and a jump to it written over the import stub.
/// </summary>
[Collection("guest")]
public sealed class CompatImportHookTests : IDisposable
{
    private const uint AimgrForDriver = 0xFD00C69A, IsDex = 0xF4B98F66;
    private const uint LdrPcMinus4 = 0xE51FF004;
    private const int BadLicense = unchecked((int)0x8001000D);

    private readonly HleHarness _harness;

    public CompatImportHookTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "compatimport", kernelPlugins: true);
        SceModuleBuilder compat = new SceModuleBuilder("SceCompat").Start(0).Import("SceSblAIMgrForDriver", AimgrForDriver, IsDex);
        string kd = Path.Combine(_harness.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "compat.skprx"), compat.Build());
        FirmwareKernelImage.Forget(_harness.Root);
    }

    public void Dispose()
    {
        FirmwareKernelImage.Forget(_harness.Root);
        _harness.Dispose();
    }

    private Compat Compat => _harness.Module<Compat>();

    private string HostPath(string guest)
    {
        string host = _harness.Emu.Vfs.Resolve(guest, forWrite: true)!;
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        return host;
    }

    /// <summary>Loads a kernel module with one function that answers <paramref name="answer"/>; returns that
    /// function's address (Thumb).</summary>
    private uint LoadAnsweringModule(byte answer)
    {
        SceModuleBuilder plugin = new SceModuleBuilder("AnsweringModule").Start(0);
        int function = plugin.ReturnConstant(answer);
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "answer.skprx"), plugin.Build());
        File.WriteAllText(Path.Combine(dir, "config.txt"), "*KERNEL\nur0:tai/answer.skprx\n");
        List<string> results = _harness.Emu.LoadPlugins();
        Assert.Single(results);
        Assert.Contains(": loaded and started (", results[0]);
        LoadedModule loaded = HleHarness.KernelModules(_harness.Emu).Values.Single(m => m.Path == "ur0:tai/answer.skprx");
        return SceModuleBuilder.FunctionAddress(loaded.Segments[0].Address, function);
    }

    /// <summary>"ldr pc, [pc, #-4]; target" written over SceCompat's import stub of ksceSblAimgrIsDEX, with
    /// ksceKernelCpuUnrestrictedMemcpy and the cache flushes, as kernel code patches kernel memory.</summary>
    private void HookTheImport(uint target)
    {
        uint stub = _harness.Emu.FirmwareKernel.ImportStubOf("SceCompat", AimgrForDriver, IsDex);
        Assert.NotEqual(0u, stub);
        SysmemKernel sysmem = _harness.Module<SysmemKernel>();
        uint patch = _harness.Alloc(8);
        GuestMemory.W32(patch, LdrPcMinus4);
        GuestMemory.W32(patch + 4, target);
        Assert.Equal(0, Call(sysmem, "ksceKernelCpuUnrestrictedMemcpy", stub, patch, 8u));
        Invoke(sysmem, "ksceKernelCpuDcacheWritebackInvalidateRange", stub, 8u);
        Invoke(sysmem, "ksceKernelCpuIcacheAndL2WritebackInvalidateRange", stub, 8u);
        Assert.True(_harness.Emu.FirmwareKernel.IsImportPatched("SceCompat", AimgrForDriver, IsDex));
    }

    /// <summary>The window taken and InitEx done on a retail unit, with the boot images in place but no activation
    /// data and no license for the title.</summary>
    private void InitWithoutLicense()
    {
        _harness.Emu.IsSystemProgram = true;
        byte[] pcbc = new byte[0x200];
        BinaryPrimitives.WriteUInt32LittleEndian(pcbc, 0x43425350);
        File.WriteAllBytes(HostPath("os0:kd/pcbc.bin"), pcbc);
        File.WriteAllBytes(HostPath("vs0:app/NPXS10028/pcff.bin"), new byte[0x100]);
        Compat.CoreFactory = _ => new HaltedCore();
        uint bases = _harness.Alloc(8);
        Assert.Equal(0, Call(Compat, "sceCompatAllocCdramWithHole", bases));
        Assert.Equal(0, Call(Compat, "sceCompatInitEx", 1));
    }

    /// <summary>With SceCompat's IsDEX import answering 1, a retail unit without activation data nor license starts the
    /// core (state 2): the module took the import's answer at both steps. Without the hook it does not (the export's
    /// answer, 0 on a retail unit).</summary>
    [Fact]
    public void AnImportHookOnSceCompatsIsDexLetsStartPassTheLicenseSteps()
    {
        uint answerOne = LoadAnsweringModule(1);
        _harness.OnGuest(_ =>
        {
            InitWithoutLicense();
            Assert.True(Call(Compat, "sceCompatStart") < 0);
            Assert.Equal(1, Call(Compat, "sceCompatGetStatus"));

            Assert.Equal(0, Call(Compat, "sceCompatInitEx", 1));
            HookTheImport(answerOne);
            Assert.Equal(0, Call(Compat, "sceCompatStart"));
            Assert.Equal(2, Call(Compat, "sceCompatGetStatus"));
            Assert.NotNull(Compat.Runner);
            Assert.Equal(0, Call(Compat, "sceCompatStop"));
        });
    }

    /// <summary>The hook's answer is what counts, not that a hook is there: answering 0 the start path stops at the
    /// first failed step (the activation data), with that step's error, as without the hook; with activation data
    /// present it stops at the license (0x8001000D).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnImportHookAnsweringZeroKeepsTheRetailChecks(bool activationPresent)
    {
        uint answerZero = LoadAnsweringModule(0);
        _harness.OnGuest(_ =>
        {
            if (activationPresent)
            {
                byte[] activation = new byte[0x1038];
                BinaryPrimitives.WriteUInt16BigEndian(activation, 1);
                File.WriteAllBytes(HostPath("tm0:npdrm/act.dat"), activation);
            }

            InitWithoutLicense();
            HookTheImport(answerZero);
            int result = Call(Compat, "sceCompatStart");
            if (activationPresent)
            {
                Assert.Equal(BadLicense, result);
            }
            else
            {
                Assert.True(result < 0 && result != BadLicense, $"0x{result:X8}");
            }

            Assert.Equal(1, Call(Compat, "sceCompatGetStatus"));
            Assert.Null(Compat.Runner);
        });
    }

    /// <summary>A core that waits for an interrupt from its reset: the start path's thread runs, nothing else.</summary>
    private sealed class HaltedCore : ICompatCore
    {
        public bool Halted => true;

        public void Reset(uint pc)
        {
        }

        public int Run(int cycles)
        {
            return 0;
        }

        public void RaiseInterrupt(int line)
        {
        }

        public void ClearInterrupt(int line)
        {
        }

        public void Stop()
        {
        }
    }
}
