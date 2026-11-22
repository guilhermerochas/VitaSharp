using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The argument-block forms of the I/O calls of SceIofilemgrForDriver (iofilemgr.skprx 3.74: vfsOpen, vfsClose,
/// vfsRead, vfsWrite, vfsPread, vfsPwrite, vfsIoctl, vfsChstat, vfsChstatByFd), what the I/O manager's request thread
/// and ScePfsMgr run: each reads its arguments from one block of words and does what the plain call does on the
/// same file, with the same errors; and NID_A39A9CA7's argument check before vfsRmdev.
/// </summary>
[Collection("guest")]
public sealed class IofilemgrArgumentBlockTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Inval = unchecked((int)0x80010016), BadFile = unchecked((int)0x80010009);
    private const int NoEnt = unchecked((int)0x80010002), Unsup = unchecked((int)0x80010030);

    private static readonly uint[] s_nids = [0x0E518FA9, 0x1AE14011, 0x809892C1, 0x8598ADC3, 0x0B54F9E0, 0xE5DEA6B7, 0xC1DD4317, 0xBCE4865B, 0x7517FE29];

    private readonly HleHarness _harness = new(fixture, "ioargblock");

    public void Dispose() => _harness.Dispose();

    private IoFileMgr Io => _harness.Module<IoFileMgr>();

    private uint Block(params uint[] words)
    {
        uint block = _harness.Alloc(0x20);
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(block + (uint)i * 4, words[i]);
        }

        return block;
    }

    private int Open(string path, int flags)
    {
        return Call(Io, "vfsOpen", Block(_harness.Str(path), (uint)flags, 0x1B6));
    }

    [Fact]
    public void EveryExportIsARealImplementation()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Library == "SceIofilemgrForDriver").ToList();
        Assert.All(s_nids, nid => Assert.False(bindings.Single(b => b.Nid == nid).Default));
    }

    [Fact]
    public void ANullBlockIsAnInvalidArgument()
    {
        foreach (string name in new[] { "vfsOpen", "vfsClose", "vfsRead", "vfsWrite", "vfsPread", "vfsPwrite", "vfsIoctl", "vfsChstat", "vfsChstatByFd" })
        {
            Assert.Equal(Inval, Call(Io, name, 0u));
        }
    }

    /// <summary>Open, write, read back, positioned write and read, close: the same file the plain calls see.</summary>
    [Fact]
    public void TheBlocksDoTheFileOperations()
    {
        string? host = _harness.Emu.Vfs.Resolve("ux0:argblock.bin", true);
        File.Delete(host!);
        int fd = Open("ux0:argblock.bin", 0x0602); // write | create | truncate
        Assert.True(fd > 0, $"0x{fd:X8}");
        uint data = _harness.Alloc(0x10);
        "0123456789ABCDEF"u8.CopyTo(GuestMemory.Span(data, 0x10));
        Assert.Equal(0x10, Call(Io, "vfsWrite", Block((uint)fd, data, 0x10)));
        Assert.Equal(0, Call(Io, "vfsClose", Block((uint)fd)));
        Assert.Equal(BadFile, Call(Io, "vfsClose", Block((uint)fd)));
        Assert.Equal("0123456789ABCDEF", File.ReadAllText(host!));

        // the request kind in the top nibble of the flags is not an open flag
        fd = Open("ux0:argblock.bin", 0x30000003); // read | write
        Assert.True(fd > 0, $"0x{fd:X8}");
        uint buffer = _harness.Alloc(0x10);
        Assert.Equal(4, Call(Io, "vfsRead", Block((uint)fd, buffer, 4)));
        Assert.Equal("0123"u8.ToArray(), GuestMemory.Span(buffer, 4).ToArray());

        uint patch = _harness.Str("xy");
        Assert.Equal(2, Call(Io, "vfsPwrite", Block((uint)fd, patch, 2, 0, 8, 0)));
        Assert.Equal(3, Call(Io, "vfsPread", Block((uint)fd, buffer, 3, 0, 7, 0)));
        Assert.Equal("7xy"u8.ToArray(), GuestMemory.Span(buffer, 3).ToArray());

        // the positioned calls leave the file position where the read left it
        Assert.Equal(2, Call(Io, "vfsRead", Block((uint)fd, buffer, 2)));
        Assert.Equal("45"u8.ToArray(), GuestMemory.Span(buffer, 2).ToArray());
        Assert.Equal(0, Call(Io, "vfsClose", Block((uint)fd)));
        Assert.Equal("01234567xyABCDEF", File.ReadAllText(host!));

        Assert.Equal(BadFile, Call(Io, "vfsRead", Block(0x7FFF, buffer, 4)));
        Assert.Equal(NoEnt, Open("ux0:argblock-missing.bin", 1));
        File.Delete(host!);
    }

    /// <summary>The ioctl and chstat blocks give the same answers as the plain calls on the same file.</summary>
    [Fact]
    public void IoctlAndChstatMatchThePlainCalls()
    {
        string? host = _harness.Emu.Vfs.Resolve("ux0:argstat.bin", true);
        File.WriteAllBytes(host!, new byte[8]);
        int fd = Call(Io, "sceIoOpen", "ux0:argstat.bin", 1, 0);
        Assert.True(fd > 0);
        try
        {
            uint output = _harness.Alloc(0x10);
            int plain = Call(Io, "sceIoIoctl", fd, 0x3001, 0u, 0u, output, 0x10u);
            Assert.Equal(plain, Call(Io, "vfsIoctl", Block((uint)fd, 0x3001, 0, 0, output, 0x10)));

            uint stat = _harness.Alloc(0x58);
            int plainByFd = Call(Io, "sceIoChstatByFd", fd, stat, 0);
            Assert.Equal(plainByFd, Call(Io, "vfsChstatByFd", Block((uint)fd, stat, 0)));
            Assert.Equal(Call(Io, "sceIoChstat", "ux0:argstat.bin", stat, 0), Call(Io, "vfsChstat", Block(_harness.Str("ux0:argstat.bin"), stat, 0)));
            Assert.Equal(Call(Io, "sceIoChstat", "ux0:argstat-none.bin", stat, 0), Call(Io, "vfsChstat", Block(_harness.Str("ux0:argstat-none.bin"), stat, 0)));
        }
        finally
        {
            Assert.Equal(0, Call(Io, "sceIoClose", fd));
            File.Delete(host!);
        }
    }

    /// <summary>NID_A39A9CA7(path, ?, a, b): a or b not 0 is 0x80010016 (checked before anything else); otherwise
    /// the call is vfsRmdev's on { path, 1 }.</summary>
    [Fact]
    public void RmdevByPathChecksItsLastArguments()
    {
        uint path = _harness.Str("sdstor0:gcd-lp-ign-entire");
        Assert.Equal(Inval, Call(Io, "ksceIofilemgr_A39A9CA7", path, 0u, 1u, 0u));
        Assert.Equal(Inval, Call(Io, "ksceIofilemgr_A39A9CA7", path, 0u, 0u, 1u));
        Assert.Equal(Call(Io, "vfsRmdev", Block(path, 1)), Call(Io, "ksceIofilemgr_A39A9CA7", path, 0u, 0u, 0u));
        Assert.Equal(Unsup, Call(Io, "ksceIofilemgr_A39A9CA7", path, 0u, 0u, 0u));
    }
}
