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
    private const uint GrabForDriver = 0x81C54BED, SetMemoryBankAddress = 0xE9C25A28;
    private const uint SysmemForDriver = 0x6F25E18A, AllocMemBlock = 0xC94850C9, FreeMemBlock = 0x009E1C61, UnmapMemBlock = 0xFFCD9B60,
        GetMemBlockBase = 0xA841EDDA;
    private const int Exists = unchecked((int)0x80010078);
    private const uint Compat374ModuleNid = 0x0ED0A986, SecureHandshakeOffset = 0x2AE8;
    private const uint ThumbLdrPc = 0xF000F8DF; // ldr.w pc, [pc, #0]
    private const uint LdrPcMinus4 = 0xE51FF004;
    private const int BadLicense = unchecked((int)0x8001000D);

    private readonly HleHarness _harness;

    public CompatImportHookTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "compatimport", kernelPlugins: true);
        SceModuleBuilder compat = new SceModuleBuilder("SceCompat").ModuleNid(Compat374ModuleNid).SegmentSizes(0x5000, 0x1000).Start(0)
            .Import("SceSblAIMgrForDriver", AimgrForDriver, IsDex)
            .Import("SceGrabForDriver", GrabForDriver, SetMemoryBankAddress)
            .Import("SceSysmemForDriver", SysmemForDriver, AllocMemBlock, FreeMemBlock, UnmapMemBlock, GetMemBlockBase);
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

    /// <summary>"ldr pc, [pc, #-4]; target" written over SceCompat's import stub of a function, with
    /// ksceKernelCpuUnrestrictedMemcpy and the cache flushes, as kernel code patches kernel memory.</summary>
    private void HookTheImport(uint target, uint libNid = AimgrForDriver, uint nid = IsDex)
    {
        uint stub = _harness.Emu.FirmwareKernel.ImportStubOf("SceCompat", libNid, nid);
        Assert.NotEqual(0u, stub);
        WritePatch(stub, LdrPcMinus4, target);
        Assert.True(_harness.Emu.FirmwareKernel.IsImportPatched("SceCompat", libNid, nid));
    }

    /// <summary>Two words written over kernel code the way a kernel plugin does it.</summary>
    private void WritePatch(uint at, uint first, uint second)
    {
        SysmemKernel sysmem = _harness.Module<SysmemKernel>();
        uint patch = _harness.Alloc(8);
        GuestMemory.W32(patch, first);
        GuestMemory.W32(patch + 4, second);
        Assert.Equal(0, Call(sysmem, "ksceKernelCpuUnrestrictedMemcpy", at, patch, 8u));
        Invoke(sysmem, "ksceKernelCpuDcacheWritebackInvalidateRange", at, 8u);
        Invoke(sysmem, "ksceKernelCpuIcacheAndL2WritebackInvalidateRange", at, 8u);
    }

    /// <summary>The window taken and InitEx done on a retail unit, with the boot images in place but no activation
    /// data and no license for the title.</summary>
    private uint InitWithoutLicense()
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
        return GuestMemory.R32(bases);
    }

    /// <summary>InitEx maps each bank through SceCompat's import of ksceGrabSetMemoryBankAddress (SceCompat 3.74
    /// 0x810003f0): a hook there that answers without going on to the grabber leaves the core's banks unmapped; without
    /// it the grabber maps them over the window.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitExMapsTheBanksThroughSceCompatsGrabImport(bool hooked)
    {
        uint answerZero = LoadAnsweringModule(0);
        _harness.OnGuest(_ =>
        {
            if (hooked)
            {
                HookTheImport(answerZero, GrabForDriver, SetMemoryBankAddress);
            }

            uint window = InitWithoutLicense();
            Assert.Equal(hooked ? 0u : window, Compat.Bus.Bank(0));
            Assert.Equal(hooked ? 0u : window + 0x1000000, Compat.Bus.Bank(2));
            Assert.Equal(hooked ? 0u : window + 0x3000000, Compat.Bus.Bank(3));
        });
    }

    /// <summary>The boot handshake calls SceCompat's function at +0x2AE8 (compat_sm.self and the secure processor's
    /// command 0x30006) at its address once the core reached state 1, and stops with what it returns: an offset hook
    /// written over it that answers 5 makes sceCompatWaitSpecialRequest(1) return 5 and leave the word at 1.</summary>
    [Fact]
    public void AnOffsetHookOnTheSecureHandshakeStepAnswersTheBootHandshake()
    {
        uint answerFive = LoadAnsweringModule(5);
        LoadedModule shell = _harness.Emu.FirmwareKernel.Modules.Single(m => m.Name == "SceCompat");
        WritePatch(shell.Segments[0].Address + SecureHandshakeOffset, ThumbLdrPc, answerFive);
        _harness.OnGuest(_ =>
        {
            uint window = InitWithoutLicense();
            GuestMemory.W32(window + 0x3D78000, 1); // the core's state 1
            Assert.Equal(5, Call(Compat, "sceCompatWaitSpecialRequest", 1));
            Assert.Equal(1u, GuestMemory.R32(window + 0x3D78000));
        });
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

    private int WindowBlocksNamed(string name) => _harness.Emu.Kernel.Objects.All<MemBlock>().Count(b => b.Name == name);

    /// <summary>
    /// The window as SceCompat 3.74 builds it (0x81001748): out[0] the cached CDRAM, out[1] its uncached mirror, one
    /// memory at both addresses; the hole (+0x2000000..+0x3000000) unmapped in both views, the rest mapped; nine blocks,
    /// of which InitEx frees the middle kernel view (0x810003f0).
    /// </summary>
    [Fact]
    public void TheWindowIsOneMemoryAtTheCachedAndUncachedBasesWithAnUnmappedHole()
    {
        _harness.OnGuest(_ =>
        {
            _harness.Emu.IsSystemProgram = true;
            uint bases = _harness.Alloc(8);
            Assert.Equal(0, Call(Compat, "sceCompatAllocCdramWithHole", bases));
            uint cached = GuestMemory.R32(bases), uncached = GuestMemory.R32(bases + 4);
            Assert.Equal(Compat.Window, cached);
            Assert.NotEqual(cached, uncached);
            GuestMemory.W32(cached + 0x3D78000, 0x1234);
            Assert.Equal(0x1234u, GuestMemory.R32(uncached + 0x3D78000));
            GuestMemory.W32(uncached + 0x1FFFFFC, 0x5678);
            Assert.Equal(0x5678u, GuestMemory.R32(cached + 0x1FFFFFC));
            foreach (uint view in (ReadOnlySpan<uint>)[cached, uncached])
            {
                Assert.True(GuestMemory.IsPageMapped(view));
                Assert.True(GuestMemory.IsPageMapped(view + 0x1FFF000));
                Assert.False(GuestMemory.IsPageMapped(view + 0x2000000));
                Assert.False(GuestMemory.IsPageMapped(view + 0x2FFF000));
                Assert.True(GuestMemory.IsPageMapped(view + 0x3000000));
                Assert.True(GuestMemory.IsPageMapped(view + 0x3FFF000));
            }

            Assert.Equal(3, WindowBlocksNamed("SceCompatCached"));
            Assert.Equal(3, WindowBlocksNamed("SceCompatUncached"));
            Assert.Equal(3, WindowBlocksNamed("SceCompatCdram"));
            Assert.Equal(Exists, Call(Compat, "sceCompatAllocCdramWithHole", bases));
        });
    }

    /// <summary>The cached blocks must follow each other (the third at the first + 0x3000000): with CDRAM laid out so
    /// the middle one lands elsewhere the call fails with 0x80010078, out[1] is the third block's base and every block
    /// is freed, as in the firmware.</summary>
    [Fact]
    public void ACachedWindowThatIsNotContiguousIsRefusedAndFreed()
    {
        GuestMemory memory = _harness.Emu.Memory;
        var slots = new List<uint>();
        try
        {
            // every free 16 MB slot of CDRAM taken, then one low slot and three adjacent higher ones given back: the
            // 32 MB block takes two of the three, the next 16 MB one the low slot, the last the third
            while (true)
            {
                slots.Add(memory.Alloc(0x1000000, "cdram filler", MemoryRegion.Cdram, 0x1000000));
            }
        }
        catch (OutOfMemoryException)
        {
        }

        slots.Sort();
        int run = -1;
        for (int i = 2; i + 2 < slots.Count; i++)
        {
            if (slots[i + 1] == slots[i] + 0x1000000 && slots[i + 2] == slots[i] + 0x2000000 && slots[i] > slots[0] + 0x1000000)
            {
                run = i;
                break;
            }
        }

        Assert.True(run > 0, $"CDRAM has no room for the layout ({slots.Count} slots)");
        var freed = new List<uint> { slots[0], slots[run], slots[run + 1], slots[run + 2] };
        foreach (uint slot in freed)
        {
            memory.Free(slot);
        }

        try
        {
            _harness.OnGuest(_ =>
            {
                _harness.Emu.IsSystemProgram = true;
                uint bases = _harness.Alloc(8);
                Assert.Equal(Exists, Call(Compat, "sceCompatAllocCdramWithHole", bases));
                Assert.Equal(slots[run], GuestMemory.R32(bases));
                Assert.Equal(slots[run + 2], GuestMemory.R32(bases + 4));
                Assert.Equal(0, WindowBlocksNamed("SceCompatCached"));
                Assert.Equal(0, WindowBlocksNamed("SceCompatUncached"));
                Assert.Equal(0, WindowBlocksNamed("SceCompatCdram"));
            });
        }
        finally
        {
            foreach (uint slot in slots)
            {
                if (!freed.Contains(slot))
                {
                    memory.Free(slot);
                }
            }
        }
    }

    /// <summary>The window's sysmem calls go through SceCompat's own imports: a hook on ksceKernelUnmapMemBlock that
    /// answers 0 without unmapping leaves the hole mapped (the whole 64 MB usable); a hook on ksceKernelAllocMemBlock
    /// that answers a block id of 0 fails the window (no base: 0x80010078); a hook on ksceKernelFreeMemBlock keeps
    /// InitEx's middle kernel view. Without the hooks the sysmem behaviour of the other tests.</summary>
    [Theory]
    [InlineData(UnmapMemBlock)]
    [InlineData(AllocMemBlock)]
    [InlineData(FreeMemBlock)]
    public void ImportHooksOnTheWindowsSysmemCallsAnswer(uint hookedNid)
    {
        uint answerZero = LoadAnsweringModule(0);
        _harness.OnGuest(_ =>
        {
            HookTheImport(answerZero, SysmemForDriver, hookedNid);
            _harness.Emu.IsSystemProgram = true;
            uint bases = _harness.Alloc(8);
            int result = Call(Compat, "sceCompatAllocCdramWithHole", bases);
            if (hookedNid == AllocMemBlock)
            {
                Assert.Equal(Exists, result);
                return;
            }

            Assert.Equal(0, result);
            uint window = GuestMemory.R32(bases);
            Assert.Equal(hookedNid == UnmapMemBlock, GuestMemory.IsPageMapped(window + 0x2000000));
            Assert.Equal(hookedNid == UnmapMemBlock, GuestMemory.IsPageMapped(GuestMemory.R32(bases + 4) + 0x2000000));
            Compat.CoreFactory = _ => new HaltedCore();
            byte[] pcbc = new byte[0x200];
            BinaryPrimitives.WriteUInt32LittleEndian(pcbc, 0x43425350);
            File.WriteAllBytes(HostPath("os0:kd/pcbc.bin"), pcbc);
            File.WriteAllBytes(HostPath("vs0:app/NPXS10028/pcff.bin"), new byte[0x100]);
            Assert.Equal(0, Call(Compat, "sceCompatInitEx", 1));
            Assert.Equal(hookedNid == FreeMemBlock ? 3 : 2, WindowBlocksNamed("SceCompatCdram"));
            if (hookedNid == FreeMemBlock)
            {
                // the kept middle view is nobody's any more (as on the console, where the hook keeps it): the test
                // frees it so the next window finds the kernel views' range free again
                SysmemKernel sysmem = _harness.Module<SysmemKernel>();
                MemBlock kept = _harness.Emu.Kernel.Objects.All<MemBlock>()
                    .Where(b => b.Name == "SceCompatCdram" && b.Size == 0x1000000)
                    .MinBy(b => b.Address)!;
                Assert.Equal(0, Call(sysmem, "ksceKernelFreeMemBlock", kept.Uid));
            }
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
