using Silk.NET.Vulkan;
using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// BC1-BC5 and PVRTC textures transcoded to ASTC 4x4 for GPUs that sample ASTC but not BCn (most Android GPUs):
/// a quarter of the memory of the RGBA8 fallback (<see cref="BcSoftware"/>), the same as BC2/BC3 and twice BC1/BC4.
/// <para>
/// The source is decoded to RGBA8 (BC by <see cref="BcSoftware"/>, PVRTC by <see cref="Pvrtc"/>) and encoded by
/// <see cref="AstcEncoder"/> (pure C#, real-time: a handful of fixed block configurations instead of a search; a
/// direct BC-to-ASTC block mapping is not used because BC1's punch-through and BC3's independent alpha indices have no
/// exact ASTC equivalent). BC4 becomes luminance blocks and BC5 luminance+alpha blocks, sampled through a view swizzle
/// (<see cref="ChannelSwizzle"/>). Signed BC4/BC5 stay on the RGBA8 SNORM fallback (ASTC LDR is unsigned), and so do
/// sRGB BC4/BC5 (ASTC applies sRGB to the luminance only).
/// </para>
/// <para>
/// Results are cached: in memory (content-addressed, LRU, <see cref="MemoryBudget"/>) and on disk per title
/// (<see cref="TranscodeDiskCache"/> in the shader cache folder), so a texture is encoded once per title, not once per
/// run. The key is the xxHash3 of the source data plus format, size and <see cref="EncoderVersion"/>.
/// </para>
/// </summary>
public static class AstcTranscoder
{
    /// <summary>Bump when the encoder's output changes (old disk entries are then simply not found).</summary>
    public const int EncoderVersion = 1;
    public static long MemoryBudget = 32L << 20;
    /// <summary>Per-title disk cache (set when a title starts; null = memory only).</summary>
    public static TranscodeDiskCache? Disk;
    /// <summary>Encoder threads for one large level (the renderer's GPU thread waits for them).</summary>
    public static int Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>
    /// Where a level that is in neither cache is encoded (VITASHARP_ASTC_ASYNC=0|wait|placeholder):
    /// <list type="bullet">
    /// <item><see cref="AstcMode.GpuThread"/> (desktop default, =0): on the GPU thread (plus <see cref="Threads"/>
    /// thread-pool threads for a large level), as before;</item>
    /// <item><see cref="AstcMode.Wait"/> (Android default): by the <see cref="Workers"/> texture transcoder threads,
    /// which take rows of blocks of the level in parallel (pinned to a phone's slow cores, below the game's threads);
    /// the GPU thread waits for the whole level, so every draw samples the exact texture, as on the console;</item>
    /// <item><see cref="AstcMode.Placeholder"/> (opt-in, not the console's behaviour): the GPU thread does not wait; it
    /// uploads a coarse placeholder (one colour per 4x4 block, <see cref="BcBlockMeans"/>) and the texture is uploaded
    /// again once the transcoder threads are done (<see cref="BeginPlaceholders"/>, <see cref="Settled"/>). Only the
    /// uploads inside a <see cref="BeginPlaceholders"/> scope get placeholders; elsewhere it waits.</item>
    /// </list>
    /// </summary>
    public static AstcMode Mode = ParseMode(Environment.GetEnvironmentVariable(EnvVars.AstcAsync)) ?? (OperatingSystem.IsAndroid() ? AstcMode.Wait : AstcMode.GpuThread);
    /// <summary>Texture transcoder threads (<see cref="AstcMode.Wait"/> and <see cref="AstcMode.Placeholder"/>):
    /// VITASHARP_ASTC_WORKERS, default a quarter of the processors, 1-2.</summary>
    public static int Workers = int.TryParse(Environment.GetEnvironmentVariable(EnvVars.AstcWorkers), out int workers) && workers > 0
        ? Math.Min(workers, 8) : Math.Clamp(Environment.ProcessorCount / 4, 1, 2);
    /// <summary>Wait mode: the GPU thread runs queued row jobs itself while it waits (VITASHARP_ASTC_HELP=0: it only
    /// waits). The result is the same; the GPU thread is just never idle while its level is encoded.</summary>
    public static bool Help = ParseFlag(Environment.GetEnvironmentVariable(EnvVars.AstcHelp)) ?? true;
    /// <summary>Rows of blocks per transcoder job: a 512x512 level is 32 jobs, a 64x64 one a single job.</summary>
    public const int RowsPerJob = 4;

    private static readonly object s_lock = new();
    private static readonly Dictionary<ulong, LinkedListNode<(ulong Key, byte[] Data)>> s_map = new();
    private static readonly LinkedList<(ulong Key, byte[] Data)> s_lru = new();
    private static long s_bytes;
    public static long Encoded, EncodedTexels, EncodeTicks, MemoryHits, DiskHits, Placeholders, BackgroundEncoded, GpuThreadWaitTicks, GpuThreadJobs;

    // transcoder threads: queued row jobs, the levels in flight, finished levels a placeholder upload has not taken
    private static readonly Queue<(LevelWork Work, int FirstRow, int Rows)> s_jobs = new();
    private static readonly Dictionary<ulong, LevelWork> s_inFlight = new();
    private static readonly Dictionary<ulong, byte[]> s_ready = new();
    private static readonly HashSet<ulong> s_failed = new();
    private static readonly ManualResetEventSlim s_idle = new(true);
    private static readonly SemaphoreSlim s_work = new(0);
    private static int s_started;

    [ThreadStatic] private static List<ulong>? s_threadPlaceholders;

    /// <summary>Tests: when set, the transcoder threads wait for it before each job.</summary>
    internal static ManualResetEventSlim? TestGate { get; set; }

    /// <summary>One level being transcoded by the transcoder threads: its source, output and the jobs left.</summary>
    private sealed class LevelWork
    {
        public required ulong Key, Tag;
        public required uint W, H;
        public required AstcChannels Channels;
        public required byte[] Source;
        public required int SourceLength;
        public required TexBase TexBase;
        public required bool TwoBpp, Ii;
        public required byte[] Output;
        public int JobsLeft;
        public bool Failed;
        public long StartTicks;
        public readonly ManualResetEventSlim Done = new(false);
        private readonly object _gate = new();
        private Texels? _rgba;

        public ReadOnlySpan<byte> SourceSpan => Source.AsSpan(0, SourceLength);

        /// <summary>The level decoded to RGBA8, once, by the first job that needs it.</summary>
        public Texels Rgba()
        {
            lock (_gate)
            {
                return _rgba ??= DecodeSource(SourceSpan, TexBase, W, H, TwoBpp, Ii);
            }
        }

        public void Release()
        {
            _rgba?.Dispose();
            _rgba = null;
            ArrayPool<byte>.Shared.Return(Source);
        }
    }

    private static AstcMode? ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "0" or "off" or "false" or "no" or "gpu" => AstcMode.GpuThread,
        "1" or "on" or "true" or "yes" or "wait" => AstcMode.Wait,
        "placeholder" => AstcMode.Placeholder,
        _ => null,
    };

    private static bool? ParseFlag(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "0" or "off" or "false" or "no" => false,
        "1" or "on" or "true" or "yes" => true,
        _ => null,
    };

    /// <summary>Starts collecting the levels the calling thread gets placeholders for (one texture upload).</summary>
    public static void BeginPlaceholders()
    {
        (s_threadPlaceholders ??= new List<ulong>()).Clear();
    }

    /// <summary>Ends the collection: the keys of the levels uploaded as placeholders, null when there were none.</summary>
    public static ulong[]? EndPlaceholders()
    {
        List<ulong>? list = s_threadPlaceholders;
        s_threadPlaceholders = null;
        return list is { Count: > 0 } ? list.ToArray() : null;
    }

    /// <summary>True once no level of <paramref name="keys"/> is being transcoded any more (its result, or its
    /// failure, is known): the texture can be uploaded again.</summary>
    public static bool Settled(ulong[] keys)
    {
        lock (s_lock)
        {
            foreach (ulong key in keys)
            {
                if (s_inFlight.ContainsKey(key))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Tests: waits until the transcoder threads have nothing queued or running.</summary>
    public static bool WaitIdle(TimeSpan timeout) => s_idle.Wait(timeout);

    /// <summary>Levels being transcoded now.</summary>
    public static int PendingJobs
    {
        get
        {
            lock (s_lock)
            {
                return s_inFlight.Count;
            }
        }
    }

    /// <summary>The level from memory: a finished background level (moved into the memory cache) or the LRU.</summary>
    private static byte[]? TakeKnown(ulong key, int length)
    {
        lock (s_lock)
        {
            if (s_ready.Remove(key, out byte[]? ready) && ready.Length == length)
            {
                MemoryHits++;
                RememberLocked(key, ready);
                return ready;
            }
            if (s_map.TryGetValue(key, out LinkedListNode<(ulong Key, byte[] Data)>? node) && node.Value.Data.Length == length)
            {
                s_lru.Remove(node);
                s_lru.AddFirst(node);
                MemoryHits++;
                return node.Value.Data;
            }
        }

        return null;
    }

    /// <summary>Queues the level for the transcoder threads (rows of blocks, <see cref="RowsPerJob"/> per job), or
    /// returns the level already in flight with the same key (another texture with the same contents).</summary>
    private static LevelWork Enqueue(ulong key, ulong tag, uint w, uint h, AstcChannels channels, ReadOnlySpan<byte> source, TexBase texBase, bool twoBpp, bool ii)
    {
        LevelWork work;
        lock (s_lock)
        {
            if (s_inFlight.TryGetValue(key, out LevelWork? running))
            {
                return running;
            }

            byte[] copy = ArrayPool<byte>.Shared.Rent(source.Length);
            source.CopyTo(copy);
            int rows = (int)((h + 3) / 4);
            int jobs = (rows + RowsPerJob - 1) / RowsPerJob;
            work = new LevelWork
            {
                Key = key,
                Tag = tag,
                W = w,
                H = h,
                Channels = channels,
                Source = copy,
                SourceLength = source.Length,
                TexBase = texBase,
                TwoBpp = twoBpp,
                Ii = ii,
                Output = new byte[((w + 3) / 4) * ((h + 3) / 4) * AstcEncoder.BlockBytes],
                JobsLeft = jobs,
                StartTicks = Stopwatch.GetTimestamp(),
            };
            s_inFlight[key] = work;
            for (int row = 0; row < rows; row += RowsPerJob)
            {
                s_jobs.Enqueue((work, row, Math.Min(RowsPerJob, rows - row)));
            }

            s_idle.Reset();
        }
        if (Interlocked.CompareExchange(ref s_started, 1, 0) == 0)
        {
            for (int i = 0; i < Workers; i++)
            {
                new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = $"Texture transcoder {i}",
                }.Start();
            }
        }
        s_work.Release(work.JobsLeft);
        return work;
    }

    private static void WorkerLoop()
    {
        // never on a phone's fast cores (ThreadPlacement: Background), below the game's threads on the others
        NativeThread.Register("Texture transcoder", NativeThread.Role.Background);
        HostThreads.LowerCurrent(ThreadPriority.BelowNormal);
        while (true)
        {
            s_work.Wait();
            TestGate?.Wait();
            RunQueuedJob(onGpuThread: false);
        }
    }

    /// <summary>Takes one queued row job and runs it; false when the queue is empty (a job the GPU thread took
    /// leaves a transcoder thread's wake-up without one: it goes back to waiting).</summary>
    private static bool RunQueuedJob(bool onGpuThread)
    {
        (LevelWork Work, int FirstRow, int Rows) job;
        lock (s_lock)
        {
            if (!s_jobs.TryDequeue(out job))
            {
                return false;
            }
        }

        if (onGpuThread)
        {
            Interlocked.Increment(ref GpuThreadJobs);
        }

        LevelWork work = job.Work;
        try
        {
            if (!Volatile.Read(ref work.Failed))
            {
                EncodeRows(work, job.FirstRow, job.Rows);
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref work.Failed, true);
            Log.WarnOnce("astc-job", $"[texcache] ASTC transcode of a {work.W}x{work.H} {work.TexBase} level failed on a transcoder thread ({e.Message}): it is encoded on the GPU thread");
        }
        finally
        {
            if (Interlocked.Decrement(ref work.JobsLeft) == 0)
            {
                Finish(work);
            }
        }

        return true;
    }

    /// <summary>Encodes block rows [<paramref name="firstRow"/>, + <paramref name="rows"/>) of the level.</summary>
    private static void EncodeRows(LevelWork work, int firstRow, int rows)
    {
        int blocksWide = (int)((work.W + 3) / 4);
        int first = firstRow * blocksWide, count = rows * blocksWide;
        Span<byte> output = work.Output.AsSpan(first * AstcEncoder.BlockBytes, count * AstcEncoder.BlockBytes);
        if (work.TexBase == TexBase.UBC1 && Bc1Direct && work.Channels == AstcChannels.Rgba)
        {
            bool[] fallback = ArrayPool<bool>.Shared.Rent(count);
            try
            {
                if (AstcEncoder.EncodeFromBc1(work.SourceSpan.Slice(first * 8, count * 8), blocksWide * 4, rows * 4, output, fallback) == 0)
                {
                    return;
                }

                ReadOnlySpan<byte> rgba = work.Rgba().Span;
                for (int i = 0; i < count; i++)
                {
                    if (fallback[i])
                    {
                        AstcEncoder.EncodeBlockAt(rgba, (int)work.W, (int)work.H, i % blocksWide, firstRow + i / blocksWide, output.Slice(i * AstcEncoder.BlockBytes, AstcEncoder.BlockBytes), work.Channels);
                    }
                }
            }
            finally
            {
                ArrayPool<bool>.Shared.Return(fallback);
            }

            return;
        }

        ReadOnlySpan<byte> texels = work.Rgba().Span;
        for (int i = 0; i < count; i++)
        {
            AstcEncoder.EncodeBlockAt(texels, (int)work.W, (int)work.H, i % blocksWide, firstRow + i / blocksWide, output.Slice(i * AstcEncoder.BlockBytes, AstcEncoder.BlockBytes), work.Channels);
        }
    }

    /// <summary>The last job of a level: the result goes to the caches (or the failure is recorded).</summary>
    private static void Finish(LevelWork work)
    {
        work.Release();
        bool failed = Volatile.Read(ref work.Failed);
        if (!failed)
        {
            Disk?.Put(work.Key, work.Tag, work.Output);
        }

        lock (s_lock)
        {
            if (failed)
            {
                s_failed.Add(work.Key);
            }
            else
            {
                Encoded++;
                BackgroundEncoded++;
                EncodedTexels += (long)work.W * work.H;
                EncodeTicks += Stopwatch.GetTimestamp() - work.StartTicks;
                s_ready[work.Key] = work.Output;
                RememberLocked(work.Key, work.Output);
            }

            s_inFlight.Remove(work.Key);
            if (s_inFlight.Count == 0 && s_jobs.Count == 0)
            {
                s_idle.Set();
            }
        }
        work.Done.Set();
    }

    /// <summary>The placeholder of one level: one colour per 4x4 block (the block's mean), as void-extent blocks.</summary>
    private static Texels Coarse(ReadOnlySpan<byte> source, TexBase texBase, uint w, uint h, AstcChannels channels, bool twoBpp, bool ii, int length)
    {
        var t = Texels.Rent(length);
        if (!IsPvrtc(texBase) && BcBlockMeans.TryEncode(texBase, source, w, h, channels, t.Span))
        {
            return t;
        }

        using Texels rgba = DecodeSource(source, texBase, w, h, twoBpp, ii);
        AstcEncoder.EncodeCoarse(rgba.Span, (int)w, (int)h, t.Span, channels);
        return t;
    }

    public static Format FormatFor(bool gamma) => gamma ? Format.Astc4x4SrgbBlock : Format.Astc4x4UnormBlock;

    /// <summary>The ASTC channel layout for a GXM BC/PVRTC base format; null when it stays on the RGBA8 fallback.</summary>
    public static AstcChannels? ChannelsFor(TexBase texBase, bool gamma) => texBase switch
    {
        TexBase.UBC1 or TexBase.UBC2 or TexBase.UBC3 => AstcChannels.Rgba,
        TexBase.PVRT2BPP or TexBase.PVRT4BPP or TexBase.PVRTII2BPP or TexBase.PVRTII4BPP => AstcChannels.Rgba,
        TexBase.UBC4 when !gamma => AstcChannels.R,
        TexBase.UBC5 when !gamma => AstcChannels.Rg,
        _ => null,
    };

    /// <summary>How the decoded ASTC channels map to the source format's (R, G, B, A).</summary>
    public static (ComponentSwizzle R, ComponentSwizzle G, ComponentSwizzle B, ComponentSwizzle A) ChannelSwizzle(AstcChannels astcChannels) => astcChannels switch
    {
        AstcChannels.R => (ComponentSwizzle.R, ComponentSwizzle.Zero, ComponentSwizzle.Zero, ComponentSwizzle.One),
        AstcChannels.Rg => (ComponentSwizzle.R, ComponentSwizzle.A, ComponentSwizzle.Zero, ComponentSwizzle.One),
        _ => (ComponentSwizzle.R, ComponentSwizzle.G, ComponentSwizzle.B, ComponentSwizzle.A),
    };

    /// <summary>The texture's own swizzle applied on top of <see cref="ChannelSwizzle"/>.</summary>
    public static ComponentMapping Compose(ComponentMapping texture, AstcChannels astcChannels)
    {
        (ComponentSwizzle r, ComponentSwizzle g, ComponentSwizzle b, ComponentSwizzle a) = ChannelSwizzle(astcChannels);
        ComponentSwizzle Map(ComponentSwizzle s, ComponentSwizzle identity) => (s == ComponentSwizzle.Identity ? identity : s) switch
        {
            ComponentSwizzle.R => r,
            ComponentSwizzle.G => g,
            ComponentSwizzle.B => b,
            ComponentSwizzle.A => a,
            var other => other,
        };
        return new ComponentMapping(Map(texture.R, ComponentSwizzle.R), Map(texture.G, ComponentSwizzle.G), Map(texture.B, ComponentSwizzle.B), Map(texture.A, ComponentSwizzle.A));
    }

    public static string Summary()
    {
        lock (s_lock)
        {
            return $"ASTC 4x4 transcoding: {Encoded} levels encoded ({EncodedTexels / 1e6:F1} M texels, {EncodeTicks * 1000.0 / Stopwatch.Frequency:F0} ms, " +
                   $"{(EncodeTicks > 0 ? EncodedTexels / (EncodeTicks / (double)Stopwatch.Frequency) / 1e6 : 0):F1} M texels/s), {MemoryHits} from memory, {DiskHits} from disk" +
                   (Mode == AstcMode.GpuThread ? ", on the GPU thread"
                       : $", {Mode} mode: {BackgroundEncoded} encoded by the {Workers} transcoder threads (the GPU thread waited {GpuThreadWaitTicks * 1000.0 / Stopwatch.Frequency:F0} ms for them, running {GpuThreadJobs} of their row jobs meanwhile), {Placeholders} placeholders, {s_inFlight.Count} in flight") +
                   (Disk != null ? $" ({Disk.Summary()})" : " (no disk cache)");
        }
    }

    /// <summary>
    /// BC1 levels are mapped block by block to ASTC (<see cref="AstcEncoder.EncodeFromBc1"/>: exact for BC1's
    /// four-colour blocks, a fraction of the search's time) and only the three-colour blocks are searched
    /// (VITASHARP_ASTC_BC1_DIRECT=0: every block searched). Part of the cache key: the two give different blocks.
    /// </summary>
    public static bool Bc1Direct = ParseFlag(Environment.GetEnvironmentVariable(EnvVars.AstcBc1Direct)) ?? true;

    private static ulong Key(ReadOnlySpan<byte> source, TexBase texBase, uint w, uint h) =>
        XxHash3.HashToUInt64(source, (long)(((ulong)texBase * 0x9E3779B97F4A7C15ul) ^ ((ulong)w << 32 | h) ^ ((ulong)EncoderVersion << 56)
            ^ (texBase == TexBase.UBC1 && Bc1Direct ? 1ul << 63 : 0)));

    private static ulong Tag(TexBase texBase, uint w, uint h) => (ulong)(uint)texBase << 40 ^ (ulong)w << 20 ^ h;

    /// <summary>One level of BC blocks (row order) to ASTC 4x4 blocks.</summary>
    public static Texels FromBc(TexBase texBase, ReadOnlySpan<byte> blocks, uint w, uint h, AstcChannels channels) =>
        Transcode(Key(blocks, texBase, w, h), Tag(texBase, w, h), w, h, channels, blocks, texBase, twoBpp: false, ii: false);

    /// <summary>One level of PVRTC data to ASTC 4x4 blocks.</summary>
    public static Texels FromPvrtc(TexBase texBase, ReadOnlySpan<byte> data, uint w, uint h, bool twoBpp, bool ii) =>
        Transcode(Key(data, texBase, w, h), Tag(texBase, w, h), w, h, AstcChannels.Rgba, data, texBase, twoBpp, ii);

    private static bool IsPvrtc(TexBase texBase) => texBase is TexBase.PVRT2BPP or TexBase.PVRT4BPP or TexBase.PVRTII2BPP or TexBase.PVRTII4BPP;

    private static Texels DecodeSource(ReadOnlySpan<byte> source, TexBase texBase, uint w, uint h, bool twoBpp, bool ii) =>
        IsPvrtc(texBase) ? Pvrtc.Decode(source, w, h, twoBpp, ii) : BcSoftware.Decode(texBase, source, w, h);

    private static Texels Transcode(ulong key, ulong tag, uint w, uint h, AstcChannels channels, ReadOnlySpan<byte> source, TexBase texBase, bool twoBpp, bool ii)
    {
        int length = (int)(((w + 3) / 4) * ((h + 3) / 4) * AstcEncoder.BlockBytes);
        if (TakeKnown(key, length) is { } known)
        {
            return Copy(known);
        }

        // the disk cache: through the folder index once it is built (no file system call for a level that is not
        // there: on Android's shared storage each one goes through FUSE), a direct lookup until then
        if (Disk is { } disk && disk.Contains(key) != false && disk.TryGet(key, tag, length) is { } cached)
        {
            Interlocked.Increment(ref DiskHits);
            Remember(key, cached);
            return Copy(cached);
        }

        bool failed;
        lock (s_lock)
        {
            failed = s_failed.Contains(key);
        }

        if (Mode == AstcMode.GpuThread || failed)
        {
            return Copy(EncodeNow(key, tag, w, h, channels, source, texBase, twoBpp, ii, Threads));
        }

        LevelWork work = Enqueue(key, tag, w, h, channels, source, texBase, twoBpp, ii);
        if (Mode == AstcMode.Placeholder && s_threadPlaceholders is { } placeholders)
        {
            placeholders.Add(key);
            Interlocked.Increment(ref Placeholders);
            return Coarse(source, texBase, w, h, channels, twoBpp, ii, length);
        }

        // the exact texture: the GPU thread waits for the transcoder threads, taking queued row jobs itself meanwhile
        // (Help) so that it is never idle while its level is being encoded
        long t0 = Stopwatch.GetTimestamp();
        while (!work.Done.IsSet)
        {
            if (!Help || !RunQueuedJob(onGpuThread: true))
            {
                work.Done.Wait();
            }
        }

        Interlocked.Add(ref GpuThreadWaitTicks, Stopwatch.GetTimestamp() - t0);
        if (Volatile.Read(ref work.Failed))
        {
            return Copy(EncodeNow(key, tag, w, h, channels, source, texBase, twoBpp, ii, Threads));
        }

        lock (s_lock)
        {
            s_ready.Remove(key);
        }

        return Copy(work.Output);
    }

    /// <summary>Decodes and encodes one level (on the calling thread plus up to <paramref name="threads"/> - 1 more),
    /// writes it to the disk cache and remembers it.</summary>
    private static byte[] EncodeNow(ulong key, ulong tag, uint w, uint h, AstcChannels channels, ReadOnlySpan<byte> source, TexBase texBase, bool twoBpp, bool ii, int threads)
    {
        long t0 = Stopwatch.GetTimestamp();
        byte[] outBlocks = new byte[((w + 3) / 4) * ((h + 3) / 4) * AstcEncoder.BlockBytes];
        if (texBase == TexBase.UBC1 && Bc1Direct && channels == AstcChannels.Rgba)
        {
            int count = (int)(((w + 3) / 4) * ((h + 3) / 4));
            bool[] fallback = ArrayPool<bool>.Shared.Rent(count);
            try
            {
                if (AstcEncoder.EncodeFromBc1(source, (int)w, (int)h, outBlocks, fallback) > 0)
                {
                    using Texels rgba = DecodeSource(source, texBase, w, h, twoBpp, ii);
                    int blocksWide = (int)((w + 3) / 4);
                    for (int i = 0; i < count; i++)
                    {
                        if (fallback[i])
                        {
                            AstcEncoder.EncodeBlockAt(rgba.Span, (int)w, (int)h, i % blocksWide, i / blocksWide, outBlocks.AsSpan(i * AstcEncoder.BlockBytes, AstcEncoder.BlockBytes), channels);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<bool>.Shared.Return(fallback);
            }
        }
        else
        {
            using Texels rgba = DecodeSource(source, texBase, w, h, twoBpp, ii);
            AstcEncoder.EncodeParallel(rgba.Span, (int)w, (int)h, outBlocks, channels, threads);
        }

        long dt = Stopwatch.GetTimestamp() - t0;
        lock (s_lock)
        {
            Encoded++;
            EncodedTexels += (long)w * h;
            EncodeTicks += dt;
        }
        Disk?.Put(key, tag, outBlocks);
        Remember(key, outBlocks);
        return outBlocks;
    }

    private static Texels Copy(byte[] data)
    {
        var t = Texels.Rent(data.Length);
        data.CopyTo(t.Array, 0);
        return t;
    }

    private static void Remember(ulong key, byte[] data)
    {
        if (data.Length > MemoryBudget / 8)
        {
            return;
        }

        lock (s_lock)
        {
            RememberLocked(key, data);
        }
    }

    private static void RememberLocked(ulong key, byte[] data)
    {
        if (data.Length > MemoryBudget / 8 || s_map.ContainsKey(key))
        {
            return;
        }

        s_map[key] = s_lru.AddFirst((key, data));
        s_bytes += data.Length;
        while (s_bytes > MemoryBudget && s_lru.Last is { } last)
        {
            s_lru.RemoveLast();
            s_map.Remove(last.Value.Key);
            s_bytes -= last.Value.Data.Length;
        }
    }

    public static void Reset()
    {
        lock (s_lock)
        {
            s_map.Clear();
            s_lru.Clear();
            s_bytes = 0;
            s_ready.Clear();
            s_failed.Clear();
            Encoded = EncodedTexels = EncodeTicks = MemoryHits = DiskHits = Placeholders = BackgroundEncoded = GpuThreadWaitTicks = GpuThreadJobs = 0;
        }
    }
}

/// <summary>Where a missing ASTC level is encoded (<see cref="AstcTranscoder.Mode"/>).</summary>
public enum AstcMode
{
    /// <summary>On the GPU thread.</summary>
    GpuThread,
    /// <summary>By the transcoder threads; the GPU thread waits (exact textures).</summary>
    Wait,
    /// <summary>By the transcoder threads; a coarse placeholder meanwhile (opt-in).</summary>
    Placeholder,
}
