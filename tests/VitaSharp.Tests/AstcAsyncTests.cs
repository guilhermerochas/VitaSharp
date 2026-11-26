using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

[CollectionDefinition("AstcTranscoder", DisableParallelization = true)]
public sealed class AstcTranscoderCollection;

/// <summary>
/// Where the ASTC transcoder encodes a BC/PVRTC level that is in no cache (Renderer.Vulkan AstcTranscoder.Mode). The
/// first session of a game on a phone spent 37 s of encodes on the GPU worker (frames with 1-2 s of texUpload), with
/// the encode's thread-pool threads on every core. Android's default (wait) hands the level to the texture transcoder
/// threads in rows of blocks and the GPU thread waits for the exact result; the placeholder mode (opt-in) does not
/// wait, uploads a coarse placeholder and the texture again when the level is ready; a later session reads the
/// indexed disk cache. The order between the GPU thread and the transcoder threads is forced with
/// <c>AstcTranscoder.TestGate</c>, never with the clock.
/// </summary>
[Collection("AstcTranscoder")]
public sealed class AstcAsyncTests : IDisposable
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(60);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-astc-async-" + Guid.NewGuid().ToString("N"));
    private readonly AstcMode _mode = AstcTranscoder.Mode;
    private readonly TranscodeDiskCache? _disk = AstcTranscoder.Disk;
    private readonly bool _bc1Direct = AstcTranscoder.Bc1Direct;
    private readonly bool _help = AstcTranscoder.Help;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public AstcAsyncTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        AstcTranscoder.WaitIdle(s_wait);
        AstcTranscoder.Reset();
        AstcTranscoder.Mode = AstcMode.Wait;
        AstcTranscoder.Bc1Direct = false; // the search on every block: comparable with Reference
        AstcTranscoder.Help = false; // the uploading thread only waits (the tests that hold the transcoders need that)
        AstcTranscoder.Disk = null;
    }

    public void Dispose()
    {
        AstcTranscoder.TestGate?.Set();
        AstcTranscoder.WaitIdle(s_wait);
        AstcTranscoder.TestGate = null;
        AstcTranscoder.Mode = _mode;
        AstcTranscoder.Disk = _disk;
        AstcTranscoder.Bc1Direct = _bc1Direct;
        AstcTranscoder.Help = _help;
        AstcTranscoder.Reset();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Random BC1 blocks (both colour modes), as a game's DXT1 texture.</summary>
    private static byte[] Bc1Blocks(int w, int h, int seed)
    {
        var rng = new Random(seed);
        byte[] data = new byte[(w / 4) * (h / 4) * 8];
        rng.NextBytes(data);
        return data;
    }

    private static byte[] RandomBlocks(TexBase texBase, int w, int h, int seed)
    {
        var rng = new Random(seed);
        byte[] data = new byte[(w / 4) * (h / 4) * (texBase is TexBase.UBC1 or TexBase.UBC4 ? 8 : 16)];
        rng.NextBytes(data);
        return data;
    }

    /// <summary>What the synchronous path produces: the level decoded and encoded on the calling thread.</summary>
    private static byte[] Reference(TexBase texBase, byte[] blocks, uint w, uint h, AstcChannels channels)
    {
        byte[] output = new byte[((w + 3) / 4) * ((h + 3) / 4) * AstcEncoder.BlockBytes];
        using Texels rgba = BcSoftware.Decode(texBase, blocks, w, h);
        AstcEncoder.Encode(rgba.Span, (int)w, (int)h, output, channels);
        return output;
    }

    private static byte[] Upload(TexBase texBase, byte[] blocks, uint w, uint h, out ulong[]? placeholders)
    {
        AstcTranscoder.BeginPlaceholders();
        using Texels texels = AstcTranscoder.FromBc(texBase, blocks, w, h, AstcTranscoder.ChannelsFor(texBase, false)!.Value);
        placeholders = AstcTranscoder.EndPlaceholders();
        return texels.Span.ToArray();
    }

    // ---------------------------------------------------------------- wait mode (Android's default)

    /// <summary>The level is encoded by the transcoder threads, not by the uploading (GPU) thread, which waits for it
    /// and gets exactly the synchronous encoder's blocks: no placeholder, nothing a draw could see half done.</summary>
    [Fact]
    public async Task TheGpuThreadWaitsForTheTranscodersExactLevel()
    {
        byte[] blocks = Bc1Blocks(128, 64, 1);
        AstcTranscoder.TestGate = new ManualResetEventSlim(false);
        ulong[]? placeholders = null;
        Task<byte[]> upload = Task.Run(() => Upload(TexBase.UBC1, blocks, 128, 64, out placeholders));
        // the level is queued and the upload waits for it: with the transcoders held, it cannot finish
        Assert.True(SpinWait.SpinUntil(() => AstcTranscoder.PendingJobs == 1, s_wait));
        Assert.False(upload.IsCompleted);
        Assert.Equal(0, AstcTranscoder.Encoded);
        AstcTranscoder.TestGate.Set();
        byte[] uploaded = await upload.WaitAsync(s_wait);
        Assert.Null(placeholders);
        Assert.Equal(Reference(TexBase.UBC1, blocks, 128, 64, AstcChannels.Rgba), uploaded);
        Assert.Equal(1, AstcTranscoder.Encoded);
        Assert.Equal(1, AstcTranscoder.BackgroundEncoded);
        // the next upload of the same contents comes from memory
        Assert.Equal(uploaded, Upload(TexBase.UBC1, blocks, 128, 64, out _));
        Assert.Equal(1, AstcTranscoder.Encoded);
    }

    /// <summary>With <c>Help</c> (the default) the waiting GPU thread runs queued row jobs itself: even with every
    /// transcoder thread held, the level is done, exactly the same.</summary>
    [Fact]
    public void TheWaitingGpuThreadRunsRowJobsItself()
    {
        AstcTranscoder.Help = true;
        byte[] blocks = Bc1Blocks(128, 128, 8);
        AstcTranscoder.TestGate = new ManualResetEventSlim(false); // the transcoder threads take nothing
        byte[] result = Upload(TexBase.UBC1, blocks, 128, 128, out ulong[]? placeholders);
        Assert.Null(placeholders);
        Assert.Equal(Reference(TexBase.UBC1, blocks, 128, 128, AstcChannels.Rgba), result);
        Assert.Equal(128 / 4 / AstcTranscoder.RowsPerJob, AstcTranscoder.GpuThreadJobs);
    }

    /// <summary>The rows of blocks the transcoders take in parallel join into the same level as one encode, for every
    /// format (colour, alpha, luminance, luminance+alpha) and with BC1's direct mapping, also for sizes that are not a
    /// multiple of the job's rows.</summary>
    [Theory]
    [InlineData(TexBase.UBC1, 256, 256, false)]
    [InlineData(TexBase.UBC1, 256, 200, true)]
    [InlineData(TexBase.UBC3, 128, 72, false)]
    [InlineData(TexBase.UBC4, 64, 132, false)]
    [InlineData(TexBase.UBC5, 96, 96, false)]
    public void RowJobsMakeTheSameLevelAsOneEncode(TexBase texBase, int w, int h, bool bc1Direct)
    {
        AstcTranscoder.Bc1Direct = bc1Direct;
        byte[] blocks = RandomBlocks(texBase, w, h, w + h);
        AstcChannels channels = AstcTranscoder.ChannelsFor(texBase, false)!.Value;
        AstcTranscoder.Mode = AstcMode.GpuThread;
        byte[] onGpuThread;
        using (Texels texels = AstcTranscoder.FromBc(texBase, blocks, (uint)w, (uint)h, channels))
        {
            onGpuThread = texels.Span.ToArray();
        }

        AstcTranscoder.Reset();
        AstcTranscoder.Mode = AstcMode.Wait;
        byte[] byRows = Upload(texBase, blocks, (uint)w, (uint)h, out ulong[]? placeholders);
        Assert.Null(placeholders);
        Assert.Equal(1, AstcTranscoder.BackgroundEncoded);
        Assert.Equal(onGpuThread, byRows);
        if (!bc1Direct)
        {
            Assert.Equal(Reference(texBase, blocks, (uint)w, (uint)h, channels), byRows);
        }
    }

    [Fact]
    public async Task TexturesWithTheSameContentsShareOneLevelJob()
    {
        byte[] blocks = Bc1Blocks(64, 64, 2);
        AstcTranscoder.TestGate = new ManualResetEventSlim(false);
        Task<byte[]> first = Task.Run(() => Upload(TexBase.UBC1, blocks, 64, 64, out _));
        Assert.True(SpinWait.SpinUntil(() => AstcTranscoder.PendingJobs == 1, s_wait));
        Task<byte[]> second = Task.Run(() => Upload(TexBase.UBC1, blocks, 64, 64, out _));
        Assert.Equal(1, AstcTranscoder.PendingJobs);
        AstcTranscoder.TestGate.Set();
        byte[][] both = await Task.WhenAll(first, second).WaitAsync(s_wait);
        Assert.Equal(both[0], both[1]);
        Assert.Equal(1, AstcTranscoder.Encoded);
    }

    [Fact]
    public void TheDefaultModeEncodesOnTheGpuThreadOnEveryPlatform()
    {
        // Android included: the transcoder threads (Wait) were measured slower than the GPU worker on the phone
        Assert.Equal(AstcMode.GpuThread, AstcTranscoder.DefaultMode);
    }

    [Fact]
    public void TheGpuThreadModeStillEncodesOnTheCallingThread()
    {
        AstcTranscoder.Mode = AstcMode.GpuThread;
        byte[] blocks = Bc1Blocks(64, 64, 4);
        byte[] result = Upload(TexBase.UBC1, blocks, 64, 64, out ulong[]? pending);
        Assert.Null(pending);
        Assert.Equal(1, AstcTranscoder.Encoded);
        Assert.Equal(0, AstcTranscoder.BackgroundEncoded);
        Assert.Equal(Reference(TexBase.UBC1, blocks, 64, 64, AstcChannels.Rgba), result);
    }

    /// <summary>The next session of the game: the encodes are on disk and the folder is indexed at the title's start;
    /// the level is read at its first upload, nothing is encoded.</summary>
    [Fact]
    public void ALaterSessionReadsTheIndexedDiskCache()
    {
        byte[] blocks = Bc1Blocks(128, 128, 5);
        AstcTranscoder.Disk = new TranscodeDiskCache(_dir, 64 << 20);
        byte[] first = Upload(TexBase.UBC1, blocks, 128, 128, out _);
        Assert.Equal(1, AstcTranscoder.Disk.Writes);

        // a new process: empty memory cache, the folder indexed at the title's start
        AstcTranscoder.Reset();
        AstcTranscoder.Disk = new TranscodeDiskCache(_dir, 64 << 20);
        AstcTranscoder.Disk.BuildIndex();
        Assert.True(AstcTranscoder.Disk.Contains(OnlyDiskKey()));
        Assert.Equal(first, Upload(TexBase.UBC1, blocks, 128, 128, out ulong[]? none));
        Assert.Null(none);
        Assert.Equal(0, AstcTranscoder.Encoded);
        Assert.Equal(1, AstcTranscoder.DiskHits);
    }

    /// <summary>Before the index is built the disk is still looked up (once per level); once it is built, a level
    /// that is not on disk costs no file system call (it is not counted as a miss there).</summary>
    [Fact]
    public void TheIndexSkipsTheDiskForLevelsThatAreNotThere()
    {
        byte[] blocks = Bc1Blocks(64, 64, 6);
        AstcTranscoder.Disk = new TranscodeDiskCache(_dir, 64 << 20);
        byte[] first = Upload(TexBase.UBC1, blocks, 64, 64, out _);
        AstcTranscoder.Reset();
        AstcTranscoder.Disk = new TranscodeDiskCache(_dir, 64 << 20); // not indexed yet
        Assert.Equal(first, Upload(TexBase.UBC1, blocks, 64, 64, out _));
        Assert.Equal(1, AstcTranscoder.DiskHits);
        Assert.Equal(0, AstcTranscoder.Encoded);

        AstcTranscoder.Disk.BuildIndex();
        long misses = AstcTranscoder.Disk.Misses;
        Upload(TexBase.UBC1, Bc1Blocks(64, 64, 7), 64, 64, out _);
        Assert.Equal(misses, AstcTranscoder.Disk.Misses);
        Assert.Equal(1, AstcTranscoder.Encoded);
    }

    // ---------------------------------------------------------------- placeholder mode (opt-in)

    [Fact]
    public void InThePlaceholderModeTheGpuThreadDoesNotWait()
    {
        AstcTranscoder.Mode = AstcMode.Placeholder;
        byte[] blocks = Bc1Blocks(128, 64, 1);
        AstcTranscoder.TestGate = new ManualResetEventSlim(false);
        byte[] first = Upload(TexBase.UBC1, blocks, 128, 64, out ulong[]? pending);

        // the GPU thread did not encode: it got the block means, and the level is queued
        Assert.Equal(0, AstcTranscoder.Encoded);
        Assert.Equal(1, AstcTranscoder.Placeholders);
        Assert.NotNull(pending);
        Assert.Single(pending);
        Assert.False(AstcTranscoder.Settled(pending));
        byte[] means = new byte[first.Length];
        Assert.True(BcBlockMeans.TryEncode(TexBase.UBC1, blocks, 128, 64, AstcChannels.Rgba, means));
        Assert.Equal(means, first);

        // the transcoder threads encode it; then the texture's next upload is the real encode, from memory
        AstcTranscoder.TestGate.Set();
        Assert.True(AstcTranscoder.WaitIdle(s_wait));
        Assert.True(AstcTranscoder.Settled(pending));
        Assert.Equal(1, AstcTranscoder.Encoded);
        byte[] second = Upload(TexBase.UBC1, blocks, 128, 64, out ulong[]? none);
        Assert.Null(none);
        Assert.Equal(Reference(TexBase.UBC1, blocks, 128, 64, AstcChannels.Rgba), second);
        Assert.Equal(1, AstcTranscoder.Encoded);
    }

    /// <summary>Outside an upload scope (inspection, replay checks, the mip paths that never upload again) the
    /// placeholder mode waits like the wait mode: a placeholder there would never be replaced.</summary>
    [Fact]
    public void OutsideAnUploadScopeThePlaceholderModeWaits()
    {
        AstcTranscoder.Mode = AstcMode.Placeholder;
        byte[] blocks = Bc1Blocks(64, 32, 3);
        using Texels texels = AstcTranscoder.FromBc(TexBase.UBC1, blocks, 64, 32, AstcChannels.Rgba);
        Assert.Equal(0, AstcTranscoder.Placeholders);
        Assert.Equal(Reference(TexBase.UBC1, blocks, 64, 32, AstcChannels.Rgba), texels.Span.ToArray());
    }

    /// <summary>The key of the one entry in the disk cache (the transcoder's: format, size, encoder version, contents).</summary>
    private ulong OnlyDiskKey()
    {
        string file = Assert.Single(Directory.GetFiles(_dir, "*.astc"));
        return Convert.ToUInt64(Path.GetFileNameWithoutExtension(file), 16);
    }

    // ---------------------------------------------------------------- BC1 mapped block by block

    /// <summary>BC1 data with both colour modes: four-colour blocks (mapped directly) and three-colour ones with
    /// transparent black (searched).</summary>
    private static byte[] MixedBc1(int w, int h, int seed)
    {
        byte[] data = Bc1Blocks(w, h, seed);
        for (int i = 0; i < data.Length; i += 8)
        {
            int c0 = data[i] | data[i + 1] << 8, c1 = data[i + 2] | data[i + 3] << 8;
            bool four = (i / 8) % 3 != 0;
            if (four == c0 <= c1)
            {
                (data[i], data[i + 1], data[i + 2], data[i + 3]) = (data[i + 2], data[i + 3], data[i], data[i + 1]);
            }

            if (c0 == c1)
            {
                data[i] ^= 1; // equal endpoints are a three-colour block in either order
            }
        }

        return data;
    }

    /// <summary>The direct BC1 mapping decodes to BC1's own texels (within the ASTC weights' rounding) for the
    /// four-colour blocks, and the three-colour blocks are still encoded by the search, so the level as a whole stays
    /// at the encoder's quality; and it is much faster than the search (printed).</summary>
    [Fact]
    public void Bc1BlocksMapStraightToAstc()
    {
        const int Width = 128, Height = 128;
        byte[] bc1 = MixedBc1(Width, Height, 7);
        using Texels reference = BcSoftware.Decode(TexBase.UBC1, bc1, Width, Height);
        byte[] direct = new byte[(Width / 4) * (Height / 4) * AstcEncoder.BlockBytes];
        bool[] fallback = new bool[(Width / 4) * (Height / 4)];
        int flagged = AstcEncoder.EncodeFromBc1(bc1, Width, Height, direct, fallback);
        Assert.InRange(flagged, fallback.Length / 4, fallback.Length / 2); // a third of them are three-colour
        byte[] back = new byte[Width * Height * 4];
        AstcDecoder.Decode(direct, Width, Height, back);
        byte[] rgba = reference.Span.ToArray();
        int worst = 0;
        for (int i = 0; i < fallback.Length; i++)
        {
            if (fallback[i])
            {
                continue;
            }

            int bx = i % (Width / 4), by = i / (Width / 4);
            for (int t = 0; t < 16; t++)
            {
                int o = ((by * 4 + t / 4) * Width + bx * 4 + t % 4) * 4;
                for (int c = 0; c < 4; c++)
                {
                    worst = Math.Max(worst, Math.Abs(back[o + c] - rgba[o + c]));
                }
            }
        }

        Assert.True(worst <= 2, $"a directly mapped texel is {worst} levels off BC1's decode");

        // the transcoder's whole path (direct blocks + searched three-colour blocks) against the search alone
        AstcTranscoder.Mode = AstcMode.GpuThread;
        AstcTranscoder.Bc1Direct = true;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        byte[] mixed;
        using (Texels texels = AstcTranscoder.FromBc(TexBase.UBC1, bc1, Width, Height, AstcChannels.Rgba))
        {
            mixed = texels.Span.ToArray();
        }

        double directMs = timer.Elapsed.TotalMilliseconds;
        byte[] searched = Reference(TexBase.UBC1, bc1, Width, Height, AstcChannels.Rgba);
        byte[] mixedBack = new byte[back.Length], searchedBack = new byte[back.Length];
        AstcDecoder.Decode(mixed, Width, Height, mixedBack);
        AstcDecoder.Decode(searched, Width, Height, searchedBack);
        double mixedPsnr = AstcDecoder.Psnr(rgba, mixedBack, 0xF), searchedPsnr = AstcDecoder.Psnr(rgba, searchedBack, 0xF);
        Assert.True(mixedPsnr >= searchedPsnr - 0.5, $"direct+search {mixedPsnr:F2} dB, search only {searchedPsnr:F2} dB");
        timer.Restart();
        Reference(TexBase.UBC1, bc1, Width, Height, AstcChannels.Rgba);
        double searchMs = timer.Elapsed.TotalMilliseconds;
        _output.WriteLine($"BC1 {Width}x{Height} ({flagged} of {fallback.Length} blocks three-colour): direct+search {directMs:F2} ms, {mixedPsnr:F2} dB; search only {searchMs:F2} ms, {searchedPsnr:F2} dB");
    }

    // ---------------------------------------------------------------- placeholders

    /// <summary>The placeholder of each BC format is the mean of its decoded block (within rounding), for every
    /// palette mode: BC1 four-colour and three-colour with transparent black, BC2 explicit alpha, BC3/BC4/BC5
    /// eight-level and six-level interpolation.</summary>
    [Theory]
    [InlineData(TexBase.UBC1)]
    [InlineData(TexBase.UBC2)]
    [InlineData(TexBase.UBC3)]
    [InlineData(TexBase.UBC4)]
    [InlineData(TexBase.UBC5)]
    public void PlaceholdersAreTheBlockMeans(TexBase texBase)
    {
        const int Width = 32, Height = 32;
        int blockBytes = texBase is TexBase.UBC1 or TexBase.UBC4 ? 8 : 16;
        var rng = new Random((int)texBase);
        byte[] blocks = new byte[(Width / 4) * (Height / 4) * blockBytes];
        rng.NextBytes(blocks);
        AstcChannels channels = AstcTranscoder.ChannelsFor(texBase, false)!.Value;
        byte[] placeholder = new byte[(Width / 4) * (Height / 4) * AstcEncoder.BlockBytes];
        Assert.True(BcBlockMeans.TryEncode(texBase, blocks, Width, Height, channels, placeholder));
        byte[] coarse = new byte[Width * Height * 4];
        AstcDecoder.Decode(placeholder, Width, Height, coarse);
        using Texels decoded = BcSoftware.Decode(texBase, blocks, Width, Height);
        byte[] rgba = decoded.Span.ToArray();
        for (int by = 0; by < Height / 4; by++)
        {
            for (int bx = 0; bx < Width / 4; bx++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            sum += rgba[((by * 4 + y) * Width + bx * 4 + x) * 4 + c];
                        }
                    }

                    // the ASTC luminance layouts: BC4 (L, L, L, 1) holds R; BC5 (L, L, L, A) holds R in L and G in A
                    int astcChannel = channels switch
                    {
                        AstcChannels.R => c == 0 ? 0 : -1,
                        AstcChannels.Rg => c == 0 ? 0 : c == 1 ? 3 : -1,
                        _ => c,
                    };
                    if (astcChannel < 0)
                    {
                        continue;
                    }

                    int expected = (sum + 8) / 16;
                    int actual = coarse[((by * 4) * Width + bx * 4) * 4 + astcChannel];
                    Assert.True(Math.Abs(expected - actual) <= 2, $"{texBase} block ({bx},{by}) channel {c}: mean {expected}, placeholder {actual}");
                }
            }
        }
    }

    [Fact]
    public void CoarseEncodingOfTexelsIsTheirBlockMean()
    {
        byte[] img = new byte[6 * 5 * 4]; // partial blocks on both edges
        for (int i = 0; i < img.Length; i++)
        {
            img[i] = (byte)(i * 37);
        }

        byte[] blocks = new byte[2 * 2 * AstcEncoder.BlockBytes];
        AstcEncoder.EncodeCoarse(img, 6, 5, blocks, AstcChannels.Rgba);
        byte[] back = new byte[img.Length];
        AstcDecoder.Decode(blocks, 6, 5, back);
        // the bottom-right block holds texels x 4-5, y 4
        for (int c = 0; c < 4; c++)
        {
            int expected = (img[(4 * 6 + 4) * 4 + c] + img[(4 * 6 + 5) * 4 + c] + 1) / 2;
            Assert.Equal(expected, back[(4 * 6 + 5) * 4 + c]);
        }
    }
}
