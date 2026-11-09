using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>kubridge's user API (the so_loader ports' fixed-address code blocks, cache flushes, exception handlers).</summary>
[Collection("guest")]
public sealed class KubridgeHleTests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-kubridge-" + Guid.NewGuid().ToString("N"));
    Emulator? _emu;

    Emulator Emu()
    {
        if (_emu != null) return _emu;
        Directory.CreateDirectory(_root);
        return _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    Kubridge Ku => Emu().Hle.Modules.OfType<Kubridge>().Single();
    SysMem Sys => Emu().Hle.Modules.OfType<SysMem>().Single();

    static T Call<T>(object module, string name, params object?[] args) =>
        (T)module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!;

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "kubridge test"); GuestMemory.Fill(p, 0, size); return p; }

    public void Dispose()
    {
        _emu?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void AllocMemBlockHonoursTheVirtualBase()
    {
        using var scope = ProcessScope.Enter(Emu()); // the blocks belong to the process (sceKernel* look them up by owner)
        // the address the so_loader ports link their library at (user memory, normally unused)
        const uint probe = 0xA0000000;
        uint opt = Alloc(0x58);
        GuestMemory.W32(opt, 0x58);
        GuestMemory.W32(opt + 8, 0x1);      // HAS_VBASE
        GuestMemory.W32(opt + 0xC, probe);
        int uid = Call<int>(Ku, "kuKernelAllocMemBlock", "rx_block", SysMem.TypeUserRx, 0x12345u, opt);
        Assert.True(uid > 0, $"0x{uid:X8}");
        uint basePtr = Alloc(4);
        Assert.Equal(0, Call<int>(Sys, "sceKernelGetMemBlockBase", uid, new Ptr<uint>(basePtr)));
        Assert.Equal(probe, GuestMemory.R32(basePtr));
        // the same range again is taken
        Assert.True(Call<int>(Ku, "kuKernelAllocMemBlock", "rx_block", SysMem.TypeUserRx, 0x1000u, opt) < 0);
        // an executable block written through the unrestricted copy, then flushed
        uint src = Alloc(16);
        GuestMemory.W32(src, 0xE12FFF1E);
        Assert.Equal(0, Call<int>(Ku, "kuKernelCpuUnrestrictedMemcpy", probe + 0x100, src, 4u));
        Assert.Equal(0xE12FFF1Eu, GuestMemory.R32(probe + 0x100));
        Call<object?>(Ku, "kuKernelFlushCaches", probe + 0x100, 4u);
        // protection: a block range is accepted, an unknown bit or a hole is not
        Assert.Equal(0, Call<int>(Ku, "kuKernelMemProtect", probe, 0x2000u, 0x50u));
        Assert.Equal(SceError.INVALID_ARGUMENT, Call<int>(Ku, "kuKernelMemProtect", probe, 0x1000u, 0x80u));
        Assert.Equal(SceError.INVALID_ARGUMENT, Call<int>(Ku, "kuKernelMemProtect", probe + 4, 0x1000u, 0x40u));
        Assert.True(Call<int>(Ku, "kuKernelMemProtect", probe + 0x40000, 0x1000u, 0x40u) < 0);
        Assert.Equal(0, Call<int>(Sys, "sceKernelFreeMemBlock", uid));
    }

    [Fact]
    public void ExceptionHandlersChain()
    {
        uint old = Alloc(4);
        Assert.Equal(0, Call<int>(Ku, "kuKernelRegisterExceptionHandler", 2u, 0x81000001u, new Ptr<uint>(old), 0u));
        Assert.Equal(0u, GuestMemory.R32(old));
        Assert.Equal(0, Call<int>(Ku, "kuKernelRegisterExceptionHandler", 2u, 0x81000101u, new Ptr<uint>(old), 0u));
        Assert.Equal(0x81000001u, GuestMemory.R32(old));
        Assert.Equal(0x81000101u, Ku.HandlerFor(2));
        Assert.Equal(SceError.INVALID_ARGUMENT, Call<int>(Ku, "kuKernelRegisterExceptionHandler", 3u, 0x81000001u, new Ptr<uint>(old), 0u));
        Call<object?>(Ku, "kuKernelReleaseExceptionHandler", 2u);
        Assert.Equal(0u, Ku.HandlerFor(2));
    }
}
