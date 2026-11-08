using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Tests;

/// <summary>HLE batch 3: the kernel syscall forms behind the SceLibKernel/SceDriverUser wrappers (arguments packed
/// in an opt struct, as the 3.74 wrappers pass them) and the SceRtabi helpers.</summary>
[Collection("guest")]
public sealed class HleBatch3Tests(GuestMemoryFixture fx)
{
    static object? Invoke(object module, string name, params object?[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    static int Call(object module, string name, params object?[] args) => Convert.ToInt32(Invoke(module, name, args));

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "hle3"); GuestMemory.Fill(p, 0, size); return p; }

    static void SetBacking(object o, string prop, object value) =>
        o.GetType().GetField($"<{prop}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    static Emulator Shell()
    {
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        SetBacking(emu, "Kernel", new VitaSharp.Core.Kernel.Kernel(emu));
        return emu;
    }

    [Fact]
    public void CreateSemaSyscallTakesMaxCountFromOpt()
    {
        var emu = Shell();
        var tm = new ThreadMgr { Emu = emu };
        uint opt = Alloc(8);
        GuestMemory.W32(opt, 5); // maxCount
        int uid = Call(tm, "_sceKernelCreateSema", "s", 0u, 2, opt);
        Assert.True(uid > 0);
        var s = emu.Kernel.Objects.Get<Semaphore>(uid)!;
        Assert.Equal(2, s.Count);
        Assert.Equal(5, s.MaxCount);
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(tm, "_sceKernelCreateSema", "s", 0u, 2, 0u));
        GuestMemory.W32(opt, 1);
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(tm, "_sceKernelCreateSema", "s", 0u, 2, opt)); // init > max
    }

    [Fact]
    public void GetInfoSyscallUsesOptSize()
    {
        var emu = Shell();
        var tm = new ThreadMgr { Emu = emu };
        int uid = Call(tm, "sceKernelCreateSema", "sema", 0u, 1, 4, 0u);
        uint info = Alloc(0x3C), opt = Alloc(8);
        GuestMemory.W32(info, 0x3C); // ignored: the syscall copies opt->size bytes
        GuestMemory.W32(opt, 0x10);
        GuestMemory.W32(info + 0x30, 0xAAAAAAAA);
        Assert.Equal(0, Call(tm, "_sceKernelGetSemaInfo", uid, info, opt));
        Assert.Equal(0x10u, GuestMemory.R32(info));
        Assert.Equal((uint)uid, GuestMemory.R32(info + 4));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(info + 0x30)); // past the opt size: untouched
        GuestMemory.W32(opt, 0x3C);
        Assert.Equal(0, Call(tm, "_sceKernelGetSemaInfo", uid, info, opt));
        Assert.Equal(1u, GuestMemory.R32(info + 0x30));
        Assert.Equal(4u, GuestMemory.R32(info + 0x34));
    }

    [Fact]
    public void RtabiDivisionMatchesFirmware()
    {
        var c = new Clib();
        Assert.Equal(7u, (uint)Invoke(c, "__rtabi_uidiv", 15u, 2u)!);
        Assert.Equal(uint.MaxValue, (uint)Invoke(c, "__rtabi_uidiv", 15u, 0u)!);
        Assert.Equal(0u, (uint)Invoke(c, "__rtabi_uidiv", 0u, 0u)!);
        Assert.Equal(-7, Call(c, "__rtabi_idiv", -15, 2));
        Assert.Equal(int.MaxValue, Call(c, "__rtabi_idiv", 3, 0));
        Assert.Equal(int.MinValue, Call(c, "__rtabi_idiv", -3, 0));
        Assert.Equal(int.MinValue, Call(c, "__rtabi_idiv", int.MinValue, -1)); // wraps, no trap
        Assert.Equal(-1, Call(c, "__rtabi_lcmp", -1L, 0L));
        Assert.Equal(1, Call(c, "__rtabi_ulcmp", ulong.MaxValue, 0UL));
        Assert.Equal(0UL, (ulong)Invoke(c, "__rtabi_llsl", 1UL, 64u)!);
        Assert.Equal(-1L, (long)Invoke(c, "__rtabi_lasr", -8L, 200u)!);
        Assert.Equal(-3L, (long)Invoke(c, "__rtabi_d2lz", BitConverter.DoubleToUInt64Bits(-3.9))!);
        Assert.Equal(0UL, (ulong)Invoke(c, "__rtabi_f2ulz", BitConverter.SingleToUInt32Bits(-1f))!);
    }
}
