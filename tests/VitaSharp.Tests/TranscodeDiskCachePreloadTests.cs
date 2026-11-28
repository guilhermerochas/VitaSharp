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

    /// <summary>A previous session's entry: written by its cache's writer thread before the next cache opens.</summary>
    private void PutAndFlush(ulong key, ulong tag, byte[] payload)
    {
        var writer = new TranscodeDiskCache(_dir, 1 << 20);
        writer.Put(key, tag, payload);
        Assert.True(writer.Flush(TimeSpan.FromSeconds(30)));
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
        PutAndFlush(0x1234, 0x77, Payload(3, 256));
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
    public void AReadAheadEntryServesItsLevelAgainWithoutTheFile()
    {
        // the in-memory transcode cache in front drops levels; asked again, the level must not cost a file read
        Directory.CreateDirectory(_dir);
        PutAndFlush(0x42, 0x7, Payload(5, 96));
        var cache = new TranscodeDiskCache(_dir, 1 << 20);
        cache.StartPreload(1 << 20);
        Assert.True(cache.PreloadDone.Wait(10_000));
        Assert.Equal(Payload(5, 96), cache.TryGet(0x42, 0x7, 96));
        File.Delete(Path.Combine(_dir, "0000000000000042.astc"));

        Assert.Equal(Payload(5, 96), cache.TryGet(0x42, 0x7, 96));
        Assert.Equal(2, cache.PreloadHits);
    }

    [Fact]
    public void AReadAheadEntryKeepsTheTagAndLengthChecks()
    {
        Directory.CreateDirectory(_dir);
        PutAndFlush(0x55, 0x10, Payload(9, 64));
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

        Assert.True(writer.Flush(TimeSpan.FromSeconds(30)));

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
    public void AReadAheadHitAllocatesNoManagedMemory()
    {
        // the read-ahead lives in native memory and is copied straight into the caller's buffer: 110 MB of managed
        // arrays plus one array per hit made a collection during Killzone's first aim cost 810 ms
        Directory.CreateDirectory(_dir);
        PutAndFlush(0x77, 0x3, Payload(2, 4096));
        var cache = new TranscodeDiskCache(_dir, 1 << 20);
        cache.StartPreload(1 << 20);
        Assert.True(cache.PreloadDone.Wait(10_000));
        byte[] destination = new byte[4096];
        Assert.True(cache.TryCopyPreloaded(0x77, 0x3, destination));

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool hit = cache.TryCopyPreloaded(0x77, 0x3, destination);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(hit);
        Assert.Equal(Payload(2, 4096), destination);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ReleasedReadAheadEntriesAreGone()
    {
        Directory.CreateDirectory(_dir);
        PutAndFlush(0x31, 0x2, Payload(4, 64));
        var cache = new TranscodeDiskCache(_dir, 1 << 20);
        cache.StartPreload(1 << 20);
        Assert.True(cache.PreloadDone.Wait(10_000));

        cache.ReleasePreloaded();

        Assert.False(cache.IsPreloaded(0x31));
        Assert.False(cache.TryCopyPreloaded(0x31, 0x2, new byte[64]));
    }

    [Fact]
    public void NothingIsReadAheadWithoutABudget()
    {
        Directory.CreateDirectory(_dir);
        PutAndFlush(0x99, 0x1, Payload(1, 32));
        var cache = new TranscodeDiskCache(_dir, 1 << 20);
        cache.StartPreload(0);
        Assert.True(cache.PreloadDone.Wait(10_000));

        Assert.Equal(0, cache.Preloaded);
        Assert.Equal(Payload(1, 32), cache.TryGet(0x99, 0x1, 32));
        Assert.Equal(0, cache.PreloadHits);
    }
}
