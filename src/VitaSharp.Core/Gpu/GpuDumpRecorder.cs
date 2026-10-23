using System.Collections.Concurrent;
using System.Diagnostics;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Gpu;

/// <summary>What to record and where (see <see cref="GpuDumpRecorder.Start"/>).</summary>
public sealed class GpuDumpOptions
{
    public required string Path;
    /// <summary>Title, build, GPU and settings lines written into the file header.</summary>
    public GpuDumpHeader Header = new();
    /// <summary>Start right after the present that makes the backend's present count reach this value (-1: the next present).</summary>
    public long StartAfterPresent = -1;
    /// <summary>Stop after this many frames (presents); 0 = until <see cref="GpuDumpRecorder.Stop"/>.</summary>
    public long MaxFrames;
    /// <summary>Safety limit on the uncompressed record bytes (stops at the next present once exceeded).</summary>
    public long MaxRawBytes = 8L << 30;
    /// <summary>Brotli quality of the chunks (0 = stored, 1-11).</summary>
    public int Quality = 3; // q5 compressed at ~30 MB/s and stalled the GPU worker; q3 ~150 MB/s, +7% size (replay-gpu --recompress shrinks it later)
    /// <summary>Called once the file is complete (on the writer thread), also when recording failed.</summary>
    public Action<GpuDumpResult>? Finished;
    /// <summary>Called on the GPU worker when recording actually starts.</summary>
    public Action<GpuDumpRecorder>? Started;
}

public sealed class GpuDumpResult
{
    public string Path = "";
    public long Frames, Commands, Draws, MemoryRecords, MemoryBytes, RawBytes, FileBytes, ShadowBytes, TapBytes, ComparedBytes;
    public (long Inline, long Refs, long Deltas, long RawBytes, long StoredBytes) Uniforms;
    public long FirstPresent, LastPresent;
    public double Seconds, WorkerMs, WriterMs, WriterWaitMs;
    public string? Error;

    public override string ToString() =>
        $"{Frames} frames (presents {FirstPresent + 1}-{LastPresent}) in {Seconds:F1} s: {FileBytes / 1048576.0:F1} MB on disk " +
        $"({RawBytes / 1048576.0:F1} MB raw, {MemoryBytes / 1048576.0:F1} MB of guest memory in {MemoryRecords} updates, {Draws} draws, {Commands} commands), " +
        $"{(Seconds > 0 ? FileBytes / 1048576.0 / Seconds : 0):F1} MB/s; recorder cost {WorkerMs / Math.Max(1, Frames):F2} ms/frame on the GPU worker " +
        $"(+{WriterWaitMs / Math.Max(1, Frames):F2} ms/frame waiting for the writer), " +
        $"{WriterMs / Math.Max(1, Frames):F2} ms/frame compressing (writer thread), {ShadowBytes / 1048576.0:F0} MB shadow memory; " +
        $"backend reads {TapBytes / 1048576.0 / Math.Max(1, Frames):F1} MB/frame, compared {ComparedBytes / 1048576.0 / Math.Max(1, Frames):F1} MB/frame; " +
        $"uniform blocks {Uniforms.Inline} inline, {Uniforms.Refs} repeated, {Uniforms.Deltas} deltas ({Uniforms.RawBytes / 1048576.0:F0} MB -> {Uniforms.StoredBytes / 1048576.0:F0} MB)";
}

/// <summary>
/// Records the GPU work as the worker executes it, into a GPU dump (<see cref="GpuDumpFormat"/>).
///
/// <para>Memory: every guest read the backend makes on the GPU worker goes through <see cref="GuestMemory.ReadSpan(uint, int)"/>,
/// which calls <see cref="Tap"/> while a recording runs. The recorder keeps a shadow copy of each guest page the backend
/// has read so far (the replay's memory, as of now): the first read of a page stores the whole page, later reads
/// compare the range with the shadow and store only the bytes that changed. Updates are written before the command
/// whose execution read them, so the replay applies them first. Pages the backend never reads cost nothing.</para>
///
/// <para>At the start every guest page is marked dirty (<see cref="DirtyPages"/>), so the renderer re-validates its
/// cached textures in the first frame and the dump gets their contents.</para>
///
/// <para>Off (no recorder): GpuQueue does one static read per command, ReadSpan one static flag check.</para>
/// </summary>
public sealed unsafe class GpuDumpRecorder
{
    static volatile GpuDumpRecorder? _current;
    /// <summary>The active (armed or recording) recorder, null when none.</summary>
    public static GpuDumpRecorder? Current => _current;

    /// <summary>Set while a recording runs: <see cref="GuestMemory.ReadSpan(uint, int)"/> calls <see cref="Tap"/>.</summary>
    public static bool TapEnabled;
    static int _tapThread;

    const int PageShift = 12, PageSize = 1 << PageShift;
    const int ChunkFlushBytes = 32 << 20;

    readonly GpuDumpOptions _opt;
    readonly GpuDumpCodec _codec = new();
    DumpBuffer _main = new(1 << 20);
    readonly DumpBuffer _cmd = new(1 << 12), _defs = new(1 << 12);
    byte[]?[]? _shadow;
    uint[]? _verified;
    bool _cmdPending, _ranSinceIdle;
    volatile bool _stopRequested, _stopNow;
    int _state; // 0 armed, 1 recording, 2 finished
    readonly GpuDumpResult _result = new();
    long _startTicks, _workerTicks, _waitTicks;
    readonly BlockingCollection<(byte Type, byte[] Data, int Length)> _out = new(boundedCapacity: 16);
    readonly ConcurrentBag<byte[]> _spare = new();
    Thread? _writer;
    FileStream? _file;
    readonly TaskCompletionSource<GpuDumpResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    GpuDumpRecorder(GpuDumpOptions o) => _opt = o;

    public GpuDumpOptions Options => _opt;
    public bool Recording => Volatile.Read(ref _state) == 1;
    public long FramesRecorded => Interlocked.Read(ref _result.Frames);
    public Task<GpuDumpResult> Completion => _done.Task;

    /// <summary>Arms a recording (any thread). It starts on the GPU worker at the next present (or the one given in
    /// the options). Null when another recording is active.</summary>
    public static GpuDumpRecorder? Start(GpuDumpOptions options)
    {
        var r = new GpuDumpRecorder(options);
        return Interlocked.CompareExchange(ref _current, r, null) == null ? r : null;
    }

    /// <summary>Stops at the next present (the dump ends on a frame boundary). Any thread.</summary>
    public void Stop() => _stopRequested = true;

    /// <summary>Stops at the next command or idle point of the GPU worker (emulator shutting down), and waits up to
    /// <paramref name="timeoutMs"/> for the file to be complete.</summary>
    public GpuDumpResult? StopNow(int timeoutMs = 5000)
    {
        _stopNow = true;
        if (Volatile.Read(ref _state) == 0)
        {
            // never started: nothing to write
            Interlocked.CompareExchange(ref _current, null, this);
            Volatile.Write(ref _state, 2);
            _result.Error = "stopped before the first present";
            _done.TrySetResult(_result);
            return _result;
        }
        return _done.Task.Wait(timeoutMs) ? _done.Task.Result : null;
    }

    // =========================================================== GPU worker hooks (GpuQueue)

    internal void BeforeExecute(GpuCommand cmd)
    {
        if (_state != 1) return;
        long t0 = Stopwatch.GetTimestamp();
        _cmd.Clear();
        _defs.Clear();
        try { _cmdPending = _codec.Write(cmd, _defs, _cmd); }
        catch (Exception e) { Fail(e); return; }
        if (_defs.Length > 0) _main.Append(_defs);
        _workerTicks += Stopwatch.GetTimestamp() - t0;
    }

    internal void AfterExecute(GpuCommand cmd, IGpuBackend? backend)
    {
        if (_state == 0)
        {
            if (_stopNow) return;
            if (cmd is PresentCommand && (_opt.StartAfterPresent < 0 || PresentCount(backend) >= _opt.StartAfterPresent)) Activate(backend);
            return;
        }
        if (_state != 1) return;
        long t0 = Stopwatch.GetTimestamp();
        _ranSinceIdle = true;
        if (_cmdPending)
        {
            _cmdPending = false;
            if (cmd is PresentCommand)
            {
                // the present number is known only now (the backend counted this present)
                long present = PresentCount(backend);
                GpuDumpCodec.WritePresentInfo(_cmd, (Stopwatch.GetTimestamp() - _startTicks) * 1_000_000 / Stopwatch.Frequency,
                    Hle.Modules.Display.FramesPresented, present);
                _result.LastPresent = present;
            }
            _main.Append(_cmd);
            _result.Commands++;
            if (cmd is DrawCommand) _result.Draws++;
        }
        if (cmd is PresentCommand)
        {
            Interlocked.Increment(ref _result.Frames);
            bool limit = (_opt.MaxFrames > 0 && _result.Frames >= _opt.MaxFrames) || _result.RawBytes + _main.Length > _opt.MaxRawBytes;
            Flush();
            if (_stopRequested || _stopNow || limit) { _workerTicks += Stopwatch.GetTimestamp() - t0; Finish(); return; }
        }
        else if (_main.Length > ChunkFlushBytes) Flush();
        if (_stopNow) { _workerTicks += Stopwatch.GetTimestamp() - t0; Finish(); return; }
        _workerTicks += Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>The worker ran the backend's Idle (the queue was empty).</summary>
    internal void AfterIdle()
    {
        if (_state != 1) return;
        if (_stopNow) { Finish(); return; }
        if (!_ranSinceIdle) return;
        _ranSinceIdle = false;
        _main.U8((byte)GpuDumpRecord.Idle);
    }

    static long PresentCount(IGpuBackend? backend) => backend?.PresentCount ?? -1;

    void Activate(IGpuBackend? backend)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_opt.Path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _file = new FileStream(_opt.Path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
            long first = PresentCount(backend);
            _result.Path = _opt.Path;
            _result.FirstPresent = _result.LastPresent = first;
            _opt.Header["first-present"] = first.ToString();
            _opt.Header["first-guest-frame"] = Hle.Modules.Display.FramesPresented.ToString();
            _opt.Header["started"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            GpuDumpCodec.WriteFileHeader(_file, _opt.Header);
        }
        catch (Exception e)
        {
            Log.Warn($"[gpudump] cannot create {_opt.Path}: {e.Message}");
            _result.Error = e.Message;
            Volatile.Write(ref _state, 2);
            Interlocked.CompareExchange(ref _current, null, this);
            _done.TrySetResult(_result);
            _opt.Finished?.Invoke(_result);
            return;
        }
        try
        {
            if (backend?.ExportDumpState() is { } state)
            {
                _main.U8((byte)GpuDumpRecord.BackendState);
                _main.Var((uint)state.Length);
                _main.Bytes(state);
            }
        }
        catch (Exception e) { Log.Warn($"[gpudump] backend state export failed: {e.Message}"); }
        _shadow = new byte[]?[1 << (32 - PageShift)];
        _verified = new uint[1 << (32 - PageShift)];
        // every page counts as written once: the renderer re-validates (re-reads) its cached textures next frame
        if (DirtyPages.Enabled && GuestMemory.Base != null) new Span<byte>(DirtyPages.Table, (int)DirtyPages.TableBytes).Fill(1);
        _writer = new Thread(WriterLoop) { Name = "GPU dump writer", IsBackground = true };
        _writer.Start();
        _startTicks = Stopwatch.GetTimestamp();
        _tapThread = Environment.CurrentManagedThreadId;
        Volatile.Write(ref _state, 1);
        TapEnabled = true;
        Log.Info($"[gpudump] recording to {_opt.Path} from present #{_result.FirstPresent + 1}" + (_opt.MaxFrames > 0 ? $" for {_opt.MaxFrames} frames" : ""));
        try { _opt.Started?.Invoke(this); } catch (Exception e) { Log.Warn($"[gpudump] start callback failed: {e.Message}"); }
    }

    // =========================================================== memory tap

    /// <summary>A guest read through <see cref="GuestMemory.ReadSpan(uint, int)"/> while recording (any thread; only
    /// the GPU worker's reads are recorded).</summary>
    public static void Tap(uint addr, long length)
    {
        if (Environment.CurrentManagedThreadId != _tapThread || length <= 0) return;
        if (_current is { _state: 1 } r) r.OnRead(addr, length);
    }

    void OnRead(uint addr, long length)
    {
        long t0 = Stopwatch.GetTimestamp();
        var shadow = _shadow!;
        var verified = _verified!;
        // a page whose dirty byte was clear when it was last compared, and is still clear in the same sweep epoch,
        // has not been written since (producers mark after every store): no need to compare it again
        byte* dirty = DirtyPages.Enabled ? DirtyPages.Table : null;
        uint stamp = DirtyPages.Epoch + 1;
        ulong end = Math.Min((ulong)addr + (ulong)length, 1UL << 32);
        _result.TapBytes += (long)(end - addr);
        for (ulong p = addr >> PageShift; p <= (end - 1) >> PageShift; p++)
        {
            if (dirty != null && verified[p] == stamp && dirty[p] == 0) continue;
            byte before = dirty != null ? dirty[p] : (byte)1;
            uint pa = (uint)(p << PageShift);
            int a = (int)(Math.Max(addr, pa) - pa), b = (int)(Math.Min(end, (ulong)pa + PageSize) - pa);
            var guest = new ReadOnlySpan<byte>(GuestMemory.Base + pa, PageSize);
            var s = shadow[p];
            if (s == null)
            {
                // first read of this page: the whole page (the replay may read a little more than we saw)
                s = shadow[p] = new byte[PageSize];
                guest.CopyTo(s);
                _result.ShadowBytes += PageSize;
                EmitMemory(pa, s);
                verified[p] = before == 0 ? stamp : 0;
                continue;
            }
            // the whole page is compared (and so verified), not only the range read
            a = 0; b = PageSize;
            var g = guest[a..b];
            var sh = s.AsSpan(a, b - a);
            _result.ComparedBytes += b - a;
            verified[p] = before == 0 ? stamp : 0;
            if (g.SequenceEqual(sh)) continue;
            int first = g.CommonPrefixLength(sh);
            int last = LastMismatchEnd(g, sh);
            if (last <= first) continue; // changed back while comparing
            g[first..last].CopyTo(sh[first..]);
            EmitMemory(pa + (uint)(a + first), sh[first..last]);
        }
        _workerTicks += Stopwatch.GetTimestamp() - t0;
    }

    static int LastMismatchEnd(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        int n = x.Length;
        while (n >= 64 && x.Slice(n - 64, 64).SequenceEqual(y.Slice(n - 64, 64))) n -= 64;
        while (n > 0 && x[n - 1] == y[n - 1]) n--;
        return n;
    }

    void EmitMemory(uint addr, ReadOnlySpan<byte> data)
    {
        _main.U8((byte)GpuDumpRecord.Memory);
        _main.U32(addr);
        _main.Var((uint)data.Length);
        _main.Bytes(data);
        _result.MemoryRecords++;
        _result.MemoryBytes += data.Length;
    }

    // =========================================================== output

    void Flush()
    {
        if (_main.Length == 0) return;
        _result.RawBytes += _main.Length;
        var data = _main.Buffer;
        int len = _main.Length;
        // hand the buffer over to the writer and continue in a spare one (the writer gives buffers back)
        _main = new DumpBuffer(_spare.TryTake(out var b) ? b : new byte[1 << 20]);
        long w0 = Stopwatch.GetTimestamp();
        _out.Add((GpuDumpFormat.ChunkRecords, data, len)); // blocks when the writer is 16 chunks behind
        long waited = Stopwatch.GetTimestamp() - w0;
        _waitTicks += waited;
        _workerTicks -= waited; // reported separately
    }

    void Finish()
    {
        if (_state != 1) return;
        TapEnabled = false;
        Volatile.Write(ref _state, 2);
        if (_cmdPending) { _main.Append(_cmd); _cmdPending = false; }
        Flush();
        _result.Seconds = (Stopwatch.GetTimestamp() - _startTicks) / (double)Stopwatch.Frequency;
        _result.WorkerMs = _workerTicks * 1000.0 / Stopwatch.Frequency;
        _result.Uniforms = _codec.UniformStats;
        _result.WriterWaitMs = _waitTicks * 1000.0 / Stopwatch.Frequency;
        var summary = new GpuDumpHeader();
        summary["frames"] = _result.Frames.ToString();
        summary["first-present"] = _result.FirstPresent.ToString();
        summary["last-present"] = _result.LastPresent.ToString();
        summary["commands"] = _result.Commands.ToString();
        summary["draws"] = _result.Draws.ToString();
        summary["memory-records"] = _result.MemoryRecords.ToString();
        summary["memory-bytes"] = _result.MemoryBytes.ToString();
        summary["raw-bytes"] = _result.RawBytes.ToString();
        summary["seconds"] = _result.Seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        summary["recorder-worker-ms"] = _result.WorkerMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        var text = System.Text.Encoding.UTF8.GetBytes(summary.Text);
        _out.Add((GpuDumpFormat.ChunkEnd, text, text.Length));
        _out.CompleteAdding();
        _shadow = null;
        _verified = null;
        Interlocked.CompareExchange(ref _current, null, this);
    }

    void Fail(Exception e)
    {
        Log.Warn($"[gpudump] recording failed: {e}");
        _result.Error ??= e.Message;
        _stopNow = true;
    }

    void WriterLoop()
    {
        byte[]? scratch = null;
        long ticks = 0;
        try
        {
            foreach (var (type, data, len) in _out.GetConsumingEnumerable())
            {
                long t0 = Stopwatch.GetTimestamp();
                GpuDumpCodec.WriteChunk(_file!, type, data.AsSpan(0, len), _opt.Quality, ref scratch);
                ticks += Stopwatch.GetTimestamp() - t0;
                if (type == GpuDumpFormat.ChunkRecords && data.Length <= 64 << 20 && _spare.Count < 4) _spare.Add(data);
            }
            _file!.Flush();
        }
        catch (Exception e)
        {
            Log.Warn($"[gpudump] writing {_opt.Path} failed: {e.Message}");
            _result.Error ??= e.Message;
            _stopNow = true;
            foreach (var _ in _out.GetConsumingEnumerable()) { } // unblock the worker
        }
        finally
        {
            _result.FileBytes = _file?.Length ?? 0;
            _file?.Dispose();
        }
        _result.WriterMs = ticks * 1000.0 / Stopwatch.Frequency;
        Log.Info($"[gpudump] {(_result.Error != null ? "FAILED (" + _result.Error + "): " : "saved")} {_opt.Path}: {_result}");
        _done.TrySetResult(_result);
        try { _opt.Finished?.Invoke(_result); } catch (Exception e) { Log.Warn($"[gpudump] finished callback failed: {e.Message}"); }
    }
}
