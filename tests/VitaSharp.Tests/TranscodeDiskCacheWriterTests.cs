using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The ASTC disk cache's background writer (TranscodeDiskCache.Put): a transcoded level is queued, never written on the
/// caller's thread (the GPU thread), since on Android's shared storage each new file goes through FUSE (God of War
/// Collection's level loads stood still 7-13 s in texUpload while the GPU thread wrote ~200 new entries). While it is
/// queued the entry is found by the index and served from memory; once written, a later session reads it from disk.
/// The slow storage is injected (BeforeFileWrite) and held on an event: no host clock decides a result.
/// </summary>
[Collection("AstcTranscoder")]
public sealed class TranscodeDiskCacheWriterTests : IDisposable
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-astc-writer-" + Guid.NewGuid().ToString("N"));
    private readonly ManualResetEventSlim _storage = new(false);
    private readonly List<TranscodeDiskCache> _caches = [];

    public void Dispose()
    {
        _storage.Set();
        foreach (TranscodeDiskCache cache in _caches)
        {
            cache.Flush(s_wait); // the writer may still be writing into the folder deleted below
        }

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

    /// <summary>A cache whose file writes stand still until <see cref="_storage"/> is set (a slow FUSE write); a write
    /// on the given thread (the caller's) is counted in <paramref name="onCaller"/> instead and not held, so the old
    /// synchronous Put fails the test rather than hanging it.</summary>
    private TranscodeDiskCache SlowCache(int callerThread, StrongBox<int> onCaller)
    {
        var cache = new TranscodeDiskCache(_dir, 64 << 20);
        _caches.Add(cache);
        cache.BeforeFileWrite = _ =>
        {
            if (Environment.CurrentManagedThreadId == callerThread)
            {
                Interlocked.Increment(ref onCaller.Value);
                return;
            }

            _storage.Wait();
        };
        return cache;
    }

    [Fact]
    public void PutDoesNotWriteTheFileOnTheCallersThread()
    {
        var onCaller = new StrongBox<int>();
        TranscodeDiskCache cache = SlowCache(Environment.CurrentManagedThreadId, onCaller);

        for (ulong key = 1; key <= 200; key++)
        {
            cache.Put(key, 0x10, Payload((byte)key, 64));
        }

        Assert.Equal(0, onCaller.Value);
        Assert.Equal(0, cache.Writes); // the storage is still held: nothing was written yet
        _storage.Set();
        Assert.True(cache.Flush(s_wait));
        Assert.Equal(200, cache.Writes);
        Assert.Equal(200, Directory.GetFiles(_dir, "*.astc").Length);
    }

    [Fact]
    public void AQueuedEntryIsFoundAndServedBeforeItsFileIsWritten()
    {
        var onCaller = new StrongBox<int>();
        TranscodeDiskCache cache = SlowCache(Environment.CurrentManagedThreadId, onCaller);
        cache.BuildIndex();

        cache.Put(0x51, 0x3, Payload(7, 96));

        Assert.True(cache.Contains(0x51));
        Assert.Equal(Payload(7, 96), cache.TryGet(0x51, 0x3, 96));
        Assert.Null(cache.TryGet(0x51, 0x4, 96)); // another format/size: not this entry
        Assert.Null(cache.TryGet(0x51, 0x3, 112));
        Assert.False(File.Exists(Path.Combine(_dir, "0000000000000051.astc")));
        Assert.Equal(0, onCaller.Value);
    }

    /// <summary>The folder is listed (the index built) while an entry queued before it is still waiting for the
    /// writer: the listing does not see its file, the index still has it once written (else the level would be
    /// encoded again in the session that wrote it).</summary>
    [Fact]
    public void AnIndexBuiltWhileAnEntryIsQueuedStillHasIt()
    {
        var onCaller = new StrongBox<int>();
        TranscodeDiskCache cache = SlowCache(Environment.CurrentManagedThreadId, onCaller);
        cache.Put(0x71, 0x2, Payload(1, 48)); // no index yet

        cache.BuildIndex(); // the listing runs while the write is held
        _storage.Set();
        Assert.True(cache.Flush(s_wait));

        Assert.True(cache.Contains(0x71));
        Assert.Equal(Payload(1, 48), cache.TryGet(0x71, 0x2, 48));
    }

    [Fact]
    public void AWrittenEntryIsReadByTheNextSession()
    {
        var onCaller = new StrongBox<int>();
        TranscodeDiskCache cache = SlowCache(Environment.CurrentManagedThreadId, onCaller);
        cache.Put(0x61, 0x9, Payload(3, 128));
        cache.Put(0x61, 0x9, Payload(3, 128)); // the same level again while queued: one write
        _storage.Set();
        Assert.True(cache.Flush(s_wait));
        Assert.Equal(1, cache.Writes);

        var next = new TranscodeDiskCache(_dir, 64 << 20);
        next.BuildIndex();
        Assert.True(next.Contains(0x61));
        Assert.Equal(Payload(3, 128), next.TryGet(0x61, 0x9, 128));
    }

    /// <summary>The GPU thread's transcode of a new level (GPU-thread mode, the default): it returns the ASTC blocks
    /// while the disk cache's storage is still held, and writes no file itself.</summary>
    [Fact]
    public void ATranscodeOnTheGpuThreadDoesNotWaitForTheDiskWrite()
    {
        TranscodeDiskCache? saved = AstcTranscoder.Disk;
        AstcMode mode = AstcTranscoder.Mode;
        try
        {
            AstcTranscoder.Reset();
            AstcTranscoder.Mode = AstcMode.GpuThread;
            var onCaller = new StrongBox<int>();
            AstcTranscoder.Disk = SlowCache(Environment.CurrentManagedThreadId, onCaller);
            var rng = new Random(11);
            byte[] blocks = new byte[(64 / 4) * (64 / 4) * 8];
            rng.NextBytes(blocks);

            using Texels first = AstcTranscoder.FromBc(TexBase.UBC1, blocks, 64, 64, AstcChannels.Rgba);

            Assert.Equal(0, onCaller.Value);
            Assert.Equal(1, AstcTranscoder.Encoded);
            Assert.Equal(0, AstcTranscoder.Disk.Writes);
            // a new run (memory cleared) while the write is still queued: served by the cache, not encoded again
            AstcTranscoder.Reset();
            using Texels second = AstcTranscoder.FromBc(TexBase.UBC1, blocks, 64, 64, AstcChannels.Rgba);
            Assert.Equal(0, AstcTranscoder.Encoded);
            Assert.Equal(first.Span.ToArray(), second.Span.ToArray());
            _storage.Set();
            Assert.True(AstcTranscoder.Disk.Flush(s_wait));
            Assert.Equal(1, AstcTranscoder.Disk.Writes);
        }
        finally
        {
            _storage.Set();
            AstcTranscoder.Disk?.Flush(s_wait);
            AstcTranscoder.Disk = saved;
            AstcTranscoder.Mode = mode;
            AstcTranscoder.Reset();
        }
    }

    private sealed class StrongBox<T>
    {
        public T Value = default!;
    }
}
