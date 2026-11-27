using VitaSharp.Core.Gpu;

namespace VitaSharp.Tests;

/// <summary>
/// The ASTC disk cache's read-ahead (TranscodeDiskCache.StartPreload): at the title start the most recently used
/// entries are read into memory, so a later lookup is served without a file read (on Android's shared storage each
/// file call goes through FUSE, on the GPU thread); the entry and its checks are those of the file, it leaves memory
/// when used, and the budget bounds what is read.
/// </summary>
public sealed class TranscodeDiskCachePreloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-astc-preload-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static byte[] Payload(byte seed, int length)
    {
        byte[] b = new byte[length];
        for (int i = 0; i < length; i++)
        {
            b[i] = (byte)(seed + i);
        }

        return b;
    }

    [Fact]
    public void AReadAheadEntryIsServedWithoutTheFile()
    {
        Directory.CreateDirectory(_dir);
        new TranscodeDiskCache(_dir, 1 << 20).Put(0x1234, 0x77, Payload(3, 256));
        var cache = new TranscodeDiskCache(_dir, 1 << 20);
        cache.StartPreload(1 << 20);
        Assert.True(cache.PreloadDone.Wait(10_000));
        // the file is gone: only the read-ahead copy can answer
        File.Delete(Path.Combine(_dir, "0000000000001234.astc"));

        byte[]? got = cache.TryGet(0x1234, 0x77, 256);

        Assert.Equal(Payload(3, 256), got);
        Assert.Equal(1, cache.PreloadHits);
    }

    [Fact]
    public void AReadAheadEntryKeepsTheTagAndLengthChecks()
    {
        Directory.CreateDirectory(_dir);
        new TranscodeDiskCache(_dir, 1 << 20).Put(0x55, 0x10, Payload(9, 64));
        var cache = new TranscodeDiskCache(_dir, 1 << 20);
        cache.StartPreload(1 << 20);
        Assert.True(cache.PreloadDone.Wait(10_000));

        Assert.Null(cache.TryGet(0x55, 0x11, 64));
    }

    [Fact]
    public void TheDefaultBudgetCoversAWholeTitleCache()
    {
        // Killzone's cache is 110 MB: a 96 MB budget left out the weapon textures (read on the GPU thread at the
        // first weapon change)
        Assert.True(TranscodeDiskCache.DefaultPreloadMb >= 110);
    }

    [Fact]
    public void EveryEntryIsReadWhenTheBudgetCoversTheCache()
    {
        Directory.CreateDirectory(_dir);
        var writer = new TranscodeDiskCache(_dir, 1 << 20);
        for (ulong k = 1; k <= 8; k++)
        {
            writer.Put(k, 0x5, Payload((byte)k, 128));
        }

        var cache = new TranscodeDiskCache(_dir, 1 << 20);
        cache.StartPreload(1 << 20);
        Assert.True(cache.PreloadDone.Wait(10_000));
        Directory.Delete(_dir, true);

        for (ulong k = 1; k <= 8; k++)
        {
            Assert.Equal(Payload((byte)k, 128), cache.TryGet(k, 0x5, 128));
        }
    }

    [Fact]
    public void NothingIsReadAheadWithoutABudget()
    {
        Directory.CreateDirectory(_dir);
        new TranscodeDiskCache(_dir, 1 << 20).Put(0x99, 0x1, Payload(1, 32));
        var cache = new TranscodeDiskCache(_dir, 1 << 20);
        cache.StartPreload(0);
        Assert.True(cache.PreloadDone.Wait(10_000));

        Assert.Equal(0, cache.Preloaded);
        Assert.Equal(Payload(1, 32), cache.TryGet(0x99, 0x1, 32));
        Assert.Equal(0, cache.PreloadHits);
    }
}
