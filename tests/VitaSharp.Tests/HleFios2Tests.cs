using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>HLE batch 4: SceFios2 (files, directories, ops, PSARC archives) over a temporary app0:.</summary>
[Collection("guest")]
public sealed class HleFios2Tests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-fios2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    static object? Invoke(object module, string name, params object?[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    static T Call<T>(object module, string name, params object?[] args) => (T)Invoke(module, name, args)!;

    uint Buf(uint n)
    {
        uint p = fx.Memory.Alloc(n, "fios-test");
        GuestMemory.Fill(p, 0, n);
        return p;
    }

    Fios2 NewFios()
    {
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        void Set(string prop, object v) => typeof(Emulator).GetField($"<{prop}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(emu, v);
        Set("Memory", fx.Memory);
        Set("Vfs", new VitaSharp.Core.Io.Vfs(_root));
        Set("Hle", new HleRegistry(emu));
        emu.Vfs.Mount("app0", Path.Combine(_root, "app"));
        Directory.CreateDirectory(Path.Combine(_root, "app", "dir"));
        var fios = new Fios2 { Emu = emu };
        var libc = new Libc { Emu = emu };
        typeof(Fios2).GetField("_io", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fios, new IoFileMgr { Emu = emu });
        typeof(Fios2).GetField("_libc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fios, libc);
        return fios;
    }

    [Fact]
    public void FileHandlesReadWriteSeekAndStat()
    {
        var f = NewFios();
        File.WriteAllText(Path.Combine(_root, "app", "a.txt"), "hello fios");
        uint fhOut = Buf(4), buf = Buf(64);
        Assert.Equal(0, Call<int>(f, "sceFiosFHOpenSync", 0u, fhOut, "app0:a.txt", 0u));
        int fh = (int)GuestMemory.R32(fhOut);
        Assert.Equal(2, (fh >> 14) & 3); // file handle type
        Assert.Equal(1, Call<int>(f, "sceFiosIsValidHandle", fh));
        Assert.Equal(10L, Call<long>(f, "sceFiosFHGetSize", fh));
        Assert.Equal(5L, Call<long>(f, "sceFiosFHReadSync", 0u, fh, buf, 5L));
        Assert.Equal("hello", Encoding.ASCII.GetString(GuestMemory.ReadSpan(buf, 5)));
        Assert.Equal(5L, Call<long>(f, "sceFiosFHTell", fh));
        Assert.Equal(8L, Call<long>(f, "sceFiosFHSeek", fh, -2L, 2));
        Assert.Equal(2L, Call<long>(f, "sceFiosFHReadSync", 0u, fh, buf, 10L));
        Assert.Equal(0L, Call<long>(f, "sceFiosFHReadSync", 0u, fh, buf, 10L)); // at the end: 0 bytes (EOF is success for sync forms)
        int eofOp = Call<int>(f, "sceFiosFHPread", 0u, fh, buf, 10L, 6L); // a read cut short by the end
        Assert.Equal(unchecked((int)0x80820010), Call<int>(f, "sceFiosOpGetError", eofOp));
        Assert.Equal(4L, Call<long>(f, "sceFiosOpSyncWait", eofOp));
        Assert.Equal(unchecked((long)(int)0x80820015), Call<long>(f, "sceFiosFHWriteSync", 0u, fh, buf, 1L)); // read only
        uint stat = Buf(0x58);
        GuestMemory.W32(stat + 0x50, 0xA5A5A5A5); // SceFiosStat is 0x50 bytes: nothing past it is written
        Assert.Equal(0, Call<int>(f, "sceFiosFHStatSync", 0u, fh, stat));
        Assert.Equal(10L, GuestMemory.Read<long>(stat));
        Assert.Equal(0u, GuestMemory.R32(stat + 32) & 1);
        Assert.Equal(0xA5A5A5A5u, GuestMemory.R32(stat + 0x50));
        // an async read is already done when it returns
        int op = Call<int>(f, "sceFiosFHPread", 0u, fh, buf, 4L, 6L);
        Assert.Equal(1, Call<int>(f, "sceFiosOpIsDone", op));
        Assert.Equal(0, Call<int>(f, "sceFiosOpWait", op));
        Assert.Equal(4L, Call<long>(f, "sceFiosOpGetActualCount", op));
        Assert.Equal("fios", Encoding.ASCII.GetString(GuestMemory.ReadSpan(buf, 4)));
        Invoke(f, "sceFiosOpDelete", op);
        Assert.Equal(0, Call<int>(f, "sceFiosOpIsDone", op));
        Assert.Equal(0, Call<int>(f, "sceFiosFHCloseSync", 0u, fh));
        Assert.Equal(unchecked((int)0x8082000B), Call<int>(f, "sceFiosFHCloseSync", 0u, fh));
        Assert.Equal(unchecked((int)0x80820005), Call<int>(f, "sceFiosFHOpenSync", 0u, fhOut, "app0:missing", 0u));
    }

    [Fact]
    public void PathOperationsAndDirectories()
    {
        var f = NewFios();
        uint data = Buf(16), exists = Buf(4);
        GuestMemory.WriteCString(data, "0123456789");
        Assert.Equal(10L, Call<long>(f, "sceFiosFileWriteSync", 0u, "app0:dir/b.bin", data, 10L, 0L));
        Assert.Equal(10L, Call<long>(f, "sceFiosFileGetSizeSync", 0u, "app0:dir/b.bin"));
        Assert.Equal(0, Call<int>(f, "sceFiosFileExistsSync", 0u, "app0:dir/b.bin", exists));
        Assert.Equal(1, GuestMemory.Read<byte>(exists));
        Assert.Equal(0, Call<int>(f, "sceFiosDirectoryExistsSync", 0u, "app0:dir/b.bin", exists));
        Assert.Equal(0, GuestMemory.Read<byte>(exists));
        Assert.Equal(0, Call<int>(f, "sceFiosDirectoryCreateSync", 0u, "app0:dir/sub"));
        uint dhOut = Buf(4), entry = Buf(0x418);
        Assert.Equal(0, Call<int>(f, "sceFiosDHOpenSync", 0u, dhOut, "app0:dir", 0u, 0u));
        int dh = (int)GuestMemory.R32(dhOut);
        Assert.Equal(0, Call<int>(f, "sceFiosDHReadSync", 0u, dh, entry));
        Assert.Equal("app0:dir/b.bin", GuestMemory.ReadCString(entry + 0x18));
        Assert.Equal(10L, GuestMemory.Read<long>(entry));
        Assert.Equal(5, GuestMemory.Read<ushort>(entry + 12)); // name length
        Assert.Equal(0, Call<int>(f, "sceFiosDHReadSync", 0u, dh, entry));
        Assert.Equal(1u, GuestMemory.R32(entry + 8) & 1); // "sub" is a directory
        Assert.Equal(unchecked((int)0x80820010), Call<int>(f, "sceFiosDHReadSync", 0u, dh, entry));
        Assert.Equal(0, Call<int>(f, "sceFiosDHCloseSync", 0u, dh));
        Assert.Equal(unchecked((int)0x8082000E), Call<int>(f, "sceFiosFileDeleteSync", 0u, "app0:dir/sub"));
        Assert.Equal(0, Call<int>(f, "sceFiosRenameSync", 0u, "app0:dir/b.bin", "app0:dir/c.bin"));
        Assert.Equal(0, Call<int>(f, "sceFiosDeleteSync", 0u, "app0:dir/c.bin"));
        Assert.Equal(unchecked((int)0x80820005), Call<int>(f, "sceFiosDeleteSync", 0u, "app0:dir/c.bin"));
    }

    /// <summary>A PSARC with a manifest and one zlib-compressed file.</summary>
    static byte[] MakePsarc(string name, byte[] content)
    {
        byte[] manifest = Encoding.ASCII.GetBytes(name);
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true))
        {
            zs.Write(content);
        }

        byte[] comp = z.ToArray();
        const uint entrySize = 30, blockSize = 0x10000;
        uint count = 2, tocLen = 32 + count * entrySize + 2 * 2;
        var a = new byte[tocLen + manifest.Length + comp.Length];
        Encoding.ASCII.GetBytes("PSAR").CopyTo(a, 0);
        BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(6), 4);
        Encoding.ASCII.GetBytes("zlib").CopyTo(a, 8);
        BinaryPrimitives.WriteUInt32BigEndian(a.AsSpan(12), tocLen);
        BinaryPrimitives.WriteUInt32BigEndian(a.AsSpan(16), entrySize);
        BinaryPrimitives.WriteUInt32BigEndian(a.AsSpan(20), count);
        BinaryPrimitives.WriteUInt32BigEndian(a.AsSpan(24), blockSize);
        BinaryPrimitives.WriteUInt32BigEndian(a.AsSpan(28), 1);
        void Entry(int i, uint block, long size, long off)
        {
            var e = a.AsSpan(32 + i * (int)entrySize);
            BinaryPrimitives.WriteUInt32BigEndian(e[16..], block);
            e[20] = (byte)(size >> 32);
            BinaryPrimitives.WriteUInt32BigEndian(e[21..], (uint)size);
            e[25] = (byte)(off >> 32);
            BinaryPrimitives.WriteUInt32BigEndian(e[26..], (uint)off);
        }
        Entry(0, 0, manifest.Length, tocLen);
        Entry(1, 1, content.Length, tocLen + manifest.Length);
        BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(32 + 2 * (int)entrySize), (ushort)manifest.Length);
        BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(32 + 2 * (int)entrySize + 2), (ushort)comp.Length);
        manifest.CopyTo(a, tocLen);
        comp.CopyTo(a, tocLen + manifest.Length);
        return a;
    }

    [Fact]
    public void PsarcArchiveMountReadsCompressedFiles()
    {
        var f = NewFios();
        byte[] content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("archived data ", 50)));
        File.WriteAllBytes(Path.Combine(_root, "app", "data.psarc"), MakePsarc("levels/one.txt", content));
        long need = Call<long>(f, "sceFiosArchiveGetMountBufferSizeSync", 0u, "app0:data.psarc", 0u);
        Assert.True(need > 0);
        uint mountBuf = Buf((uint)need), fhOut = Buf(4), arcFh = Buf(4);
        Assert.Equal(0, Call<int>(f, "sceFiosArchiveMountSync", 0u, arcFh, "app0:data.psarc", "/arc", mountBuf, (uint)need, 0u));
        Assert.Equal(content.Length, (int)Call<long>(f, "sceFiosFileGetSizeSync", 0u, "/arc/LEVELS/one.txt")); // case-insensitive archive
        Assert.Equal(0, Call<int>(f, "sceFiosFHOpenSync", 0u, fhOut, "/arc/levels/one.txt", 0u));
        int fh = (int)GuestMemory.R32(fhOut);
        uint buf = Buf((uint)content.Length);
        Assert.Equal((long)content.Length, Call<long>(f, "sceFiosFHReadSync", 0u, fh, buf, (long)content.Length));
        Assert.Equal(content, GuestMemory.ReadSpan(buf, content.Length).ToArray());
        uint exists = Buf(4);
        Assert.Equal(0, Call<int>(f, "sceFiosDirectoryExistsSync", 0u, "/arc/levels", exists));
        Assert.Equal(1, GuestMemory.Read<byte>(exists));
        Assert.Equal(0, Call<int>(f, "sceFiosArchiveUnmountSync", 0u, (int)GuestMemory.R32(arcFh)));
        Assert.Equal(0, Call<int>(f, "sceFiosExistsSync", 0u, "/arc/levels/one.txt", exists));
        Assert.Equal(0, GuestMemory.Read<byte>(exists));
    }

    [Fact]
    public void DatesAndTimes()
    {
        var f = NewFios();
        Assert.Equal(5000L, Call<long>(f, "sceFiosTimeIntervalToNanoseconds", 5L));
        Assert.Equal(2L, Call<long>(f, "sceFiosTimeIntervalFromNanoseconds", 1001L));
        uint tm = Buf(36);
        long date = 86400L * 1_000_000_000L; // 1970-01-02
        Assert.Equal(tm, Call<uint>(f, "sceFiosDateToComponents", date, tm));
        Assert.Equal(2u, GuestMemory.R32(tm + 12));
        Assert.Equal(70u, GuestMemory.R32(tm + 20));
        Assert.Equal(date, Call<long>(f, "sceFiosDateFromComponents", tm));
        Assert.Equal(0, Call<int>(f, "sceFiosPathcmp", "APP0:/A\\b", "app0:/a/B"));
    }
}
