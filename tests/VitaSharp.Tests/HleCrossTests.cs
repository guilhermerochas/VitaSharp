using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Io;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>Cross-package HLE fixes: error code values, tty devices, struct offsets.</summary>
[Collection("guest")]
public sealed class HleCrossTests(GuestMemoryFixture fx)
{
    static int Call<T>(T module, string name, params object[] args) =>
        (int)typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!;

    [Fact]
    public void ErrorCodesMatchHeader()
    {
        Assert.Equal(unchecked((int)0x80020005), SceError.INVALID_ARGUMENT);
        Assert.Equal(unchecked((int)0x80020006), SceError.ILLEGAL_ADDR);
        Assert.Equal(unchecked((int)0x80028185), SceError.LW_MUTEX_FAILED_TO_OWN);
        Assert.Equal(unchecked((int)0x80028101), SceError.UNKNOWN_SEMA_ID);
        Assert.Equal(8, (int)ThreadStatus.Waiting);
        Assert.Equal(0x100, (int)ThreadStatus.Suspended);
    }

    [Theory]
    [InlineData("tty0:", true, 0)]
    [InlineData("TTY1:", true, 1)]
    [InlineData("tty3:", true, 3)]
    [InlineData("tty4:", false, -1)]
    [InlineData("tty0:/x", false, -1)]
    [InlineData("ux0:tty0", false, -1)]
    public void TtyDevices(string path, bool tty, int index)
    {
        Assert.Equal(tty, Vfs.IsTty(path, out int n));
        Assert.Equal(index, n);
    }

    [Fact]
    public void SystemSwVersionLayout()
    {
        uint p = fx.Memory.Alloc(0x40, "swver");
        GuestMemory.Fill(p, 0xCC, 0x40);
        GuestMemory.W32(p, 0x28);
        Assert.Equal(0, Call(new SystemMisc(), "sceKernelGetSystemSwVersion", p));
        Assert.Equal("3.74", GuestMemory.ReadCString(p + 4));
        Assert.Equal(0x03740011u, GuestMemory.R32(p + 0x20));
        Assert.Equal(0u, GuestMemory.R32(p + 0x24));
        Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(p + 0x28)); // nothing past the struct
    }
}
