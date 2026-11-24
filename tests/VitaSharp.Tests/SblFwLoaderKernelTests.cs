using System.Buffers.Binary;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblFwLoaderForDriver (post_ss_mgr.skprx 3.74): the lock / load / unlock protocol with the module's checks;
/// the load gives the blob (the second segment of the decrypted SELF), or the decrypted image the user placed next to
/// it; a blob the secure processor decrypts again (pcbc) needs that image.
/// </summary>
[Collection("guest")]
public sealed class SblFwLoaderKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x800F0E16), NotLocked = unchecked((int)0x800F0E29);
    private const int Unavailable = unchecked((int)0x80020004), NoFile = unchecked((int)0x80010002);

    private readonly HleHarness _harness = new(fixture, "fwload");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x91C73A54u, false)]
    [InlineData(0xA6278D27u, false)]
    [InlineData(0xBB59FC7Au, false)]
    public void EveryExportHasAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceSblFwLoaderForDriver").ToList();
        Assert.Single(bindings);
        Assert.Equal(isDefault, bindings[0].Default);
    }

    [Fact]
    public void LockLoadUnlock()
    {
        uint buffer = _harness.Alloc(0x100), loaded = _harness.Word(0);
        Assert.Equal(NotLocked, Call(Services, "ksceSblFwLoaderUnlock"));
        Assert.Equal(NotLocked, Call(Services, "ksceSblFwLoaderLoad", 1, buffer, 0x100u, loaded));
        Assert.Equal(Invalid, Call(Services, "ksceSblFwLoaderLock", 0u, 0));
        Assert.Equal(Invalid, Call(Services, "ksceSblFwLoaderLock", _harness.Str("os0:kd/wlanbt.bin"), 1));
        Assert.Equal(Invalid, Call(Services, "ksceSblFwLoaderLock", _harness.Str(new string('a', 0x100)), 0));
        Assert.Equal(0, Call(Services, "ksceSblFwLoaderLock", _harness.Str("os0:kd/wlanbt_robin_img_ax.skprx"), 0));
        Assert.Equal(Invalid, Call(Services, "ksceSblFwLoaderLoad", 2, buffer, 0x100u, loaded));
        Assert.Equal(Invalid, Call(Services, "ksceSblFwLoaderLoad", 1, 0u, 0x100u, loaded));
        Assert.Equal(NoFile, Call(Services, "ksceSblFwLoaderLoad", 1, buffer, 0x100u, loaded)); // not in the test firmware
        Assert.Equal(0, Call(Services, "ksceSblFwLoaderUnlock"));
        Assert.Equal(NotLocked, Call(Services, "ksceSblFwLoaderUnlock"));
    }

    private string HostPath(string guest)
    {
        string host = _harness.Emu.Vfs.Resolve(guest, forWrite: true)!;
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        return host;
    }

    /// <summary>An ELF of two loadable segments, as a firmware blob's: the module information, then the blob.</summary>
    private static byte[] BlobElf(byte[] moduleInfo, byte[] blob)
    {
        const int Headers = 0x34, Entry = 0x20;
        int infoOffset = Headers + 2 * Entry;
        int blobOffset = infoOffset + moduleInfo.Length;
        byte[] elf = new byte[blobOffset + blob.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(elf, 0x464C457F);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x1C), Headers);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x2C), 2);
        int[] offsets = [infoOffset, blobOffset];
        int[] sizes = [moduleInfo.Length, blob.Length];
        for (int i = 0; i < 2; i++)
        {
            Span<byte> header = elf.AsSpan(Headers + i * Entry);
            BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x10..], (uint)sizes[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x14..], (uint)sizes[i]);
        }

        moduleInfo.CopyTo(elf, infoOffset);
        blob.CopyTo(elf, blobOffset);
        return elf;
    }

    private int Load(string path, uint buffer, uint size, uint loaded)
    {
        Assert.Equal(0, Call(Services, "ksceSblFwLoaderLock", _harness.Str(path), 0));
        int result = Call(Services, "ksceSblFwLoaderLoad", 1, buffer, size, loaded);
        Assert.Equal(0, Call(Services, "ksceSblFwLoaderUnlock"));
        return result;
    }

    /// <summary>Load gives the blob segment (not the module information), refuses a buffer smaller than the blob, and
    /// takes the user's decrypted image next to the blob instead when there is one.</summary>
    [Fact]
    public void LoadGivesTheBlobSegmentOrTheUsersDecryptedImage()
    {
        byte[] blob = [1, 2, 3, 4, 5, 6, 7, 8];
        File.WriteAllBytes(HostPath("vs0:app/NPXS10028/pcff.skprx"), BlobElf(new byte[0x90], blob));
        uint buffer = _harness.Alloc(0x100), loaded = _harness.Word(0);

        Assert.Equal(0, Load("vs0:app/NPXS10028/pcff.skprx", buffer, 0x100, loaded));
        Assert.Equal(8u, GuestMemory.R32(loaded));
        Assert.True(GuestMemory.ReadSpan(buffer, 8).SequenceEqual(blob));
        Assert.Equal(Invalid, Load("vs0:app/NPXS10028/pcff.skprx", buffer, 4, loaded));

        byte[] dump = [9, 9, 9];
        File.WriteAllBytes(HostPath("vs0:app/NPXS10028/pcff.bin"), dump);
        Assert.Equal(0, Load("vs0:app/NPXS10028/pcff.skprx", buffer, 0x100, loaded));
        Assert.Equal(3u, GuestMemory.R32(loaded));
        Assert.True(GuestMemory.ReadSpan(buffer, 3).SequenceEqual(dump));
    }

    /// <summary>The PSP compat boot code is encrypted again inside its SELF: without the user's decrypted image Load is
    /// "not supported" (the secure processor's), even with the blob file present; with it, the image is loaded.</summary>
    [Fact]
    public void TheBootCodeNeedsTheUsersDecryptedImage()
    {
        File.WriteAllBytes(HostPath("os0:kd/pcbc.skprx"), BlobElf(new byte[0x90], [0xEE, 0xEE, 0xEE, 0xEE]));
        uint buffer = _harness.Alloc(0x100), loaded = _harness.Word(0);
        Assert.Equal(Unavailable, Load("os0:kd/pcbc.skprx", buffer, 0x100, loaded));

        File.WriteAllBytes(HostPath("os0:kd/pcbc.bin"), [0x50, 0x53, 0x42, 0x43]);
        Assert.Equal(0, Load("os0:kd/pcbc.skprx", buffer, 0x100, loaded));
        Assert.Equal(0x43425350u, GuestMemory.R32(buffer));
    }
}
