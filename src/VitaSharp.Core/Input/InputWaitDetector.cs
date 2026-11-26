using System.Globalization;

namespace VitaSharp.Core.Input;

/// <summary>What the game in front is doing, judged from its presented frames and what it asks of the system.</summary>
public enum ScreenActivity
{
    /// <summary>Not enough frames seen yet.</summary>
    Unknown,
    /// <summary>The picture changes (gameplay, a video, an animation).</summary>
    Active,
    /// <summary>The picture is still (or loops over a few pictures) and the game reads the buttons or the touch panels
    /// every frame, without loading anything: it waits for the player.</summary>
    WaitingForInput,
    /// <summary>The picture is still and files are read, modules loaded or shaders/code compiled: it loads.</summary>
    Loading,
    /// <summary>The picture is still, nothing is loaded and no input is read: the CPU/GPU works on something (or hangs).</summary>
    Busy,
}

/// <summary>Cumulative counters at one presented frame (the detector takes differences over its window).</summary>
public readonly record struct ActivityCounters(long CtrlReads, long FrontTouchReads, long RearTouchReads, long FileBytes,
    long FileOpens, long ModuleLoads, long ShaderCompiles, long CodeBlocks);

/// <summary>
/// "Waiting for input" detector: over a window of presented frames, a still picture (identical, a short loop of a few
/// pictures, or a coarse luminance grid that barely changes: a blinking "Press X", a pulsing frame, a small spinner)
/// with input reads every frame and no loading is <see cref="ScreenActivity.WaitingForInput"/>; with file I/O, module
/// loads or compilation it is <see cref="ScreenActivity.Loading"/>; with neither, <see cref="ScreenActivity.Busy"/>. A
/// changing picture is <see cref="ScreenActivity.Active"/>. Pure logic over what it is fed (the frame pixels, the
/// counters and the guest time), so a test drives it without a renderer or a host clock; no allocation per frame
/// except the line of a state change.
/// </summary>
public sealed class InputWaitDetector
{
    /// <summary>The coarse luminance grid of a frame.</summary>
    public const int GridW = 32, GridH = 18;
    /// <summary>A grid cell "changed" when its mean luminance moved by more than this (0-255).</summary>
    public const int CellDelta = 16;
    /// <summary>A frame is still the same picture when at most this fraction of the cells changed against the first
    /// picture of the still run.</summary>
    public const double ChangedCellsLimit = 0.06;
    /// <summary>Distinct pictures a still run may loop over (blinking text, a 2-4 frame pulse).</summary>
    public const int LoopPictures = 8;
    /// <summary>Input reads per second (any source) that count as "reads the input every frame" (a 30 fps game reads at
    /// least 30/s; 10 leaves room for a game that polls every few frames).</summary>
    public const double ReadsPerSecond = 10;
    /// <summary>Loading thresholds over the window: file bytes per second (streamed music is ~40 KB/s and stays under),
    /// file opens per second, code blocks compiled per second.</summary>
    public const double LoadBytesPerSecond = 256 * 1024, OpensPerSecond = 2, CodeBlocksPerSecond = 200;

    private readonly int _window;
    private readonly long[] _present, _frame, _timeUs;
    private readonly ActivityCounters[] _counters;
    private long _fed;
    private readonly byte[] _anchorGrid = new byte[GridW * GridH];
    private readonly byte[] _grid = new byte[GridW * GridH];
    private readonly ulong[] _loopHashes = new ulong[LoopPictures];
    private int _loopCount;
    private long _stillSincePresent = -1, _stillSinceFrame = -1;
    private ScreenActivity _candidate;
    private int _candidateRun;

    /// <param name="window">presented frames the decision looks at (default 60, about 1-2 s)</param>
    public InputWaitDetector(int window = 60)
    {
        _window = Math.Max(4, window);
        _present = new long[_window];
        _frame = new long[_window];
        _timeUs = new long[_window];
        _counters = new ActivityCounters[_window];
    }

    /// <summary>Presented frames per decision.</summary>
    public int Window => _window;

    /// <summary>The state last reported (a change is reported once it held for a quarter of the window).</summary>
    public ScreenActivity State { get; private set; }

    /// <summary>What <see cref="State"/> rests on (the rates and since when the picture is still), or null before the
    /// first decision.</summary>
    public string? Detail { get; private set; }

    /// <summary>The guest frame the picture has been still since, or -1 while it changes.</summary>
    public long StillSinceFrame => _stillSinceFrame;

    /// <summary>Feeds one presented frame (RGBA8, <paramref name="width"/> x <paramref name="height"/>) with the counters
    /// at its present and the guest time (µs). Returns true when <see cref="State"/> changed.</summary>
    public bool Feed(long present, long guestFrame, long timeUs, ReadOnlySpan<byte> rgba, int width, int height, in ActivityCounters counters)
    {
        ulong hash = Signature(rgba, width, height, _grid);
        TrackStillness(present, guestFrame, hash);
        int slot = (int)(_fed % _window);
        _present[slot] = present;
        _frame[slot] = guestFrame;
        _timeUs[slot] = timeUs;
        _counters[slot] = counters;
        _fed++;
        if (_fed < _window)
        {
            return false;
        }

        int oldest = (int)(_fed % _window);
        ScreenActivity now = Classify(oldest, slot, present, out Rates rates);
        if (now == _candidate)
        {
            _candidateRun++;
        }
        else
        {
            _candidate = now;
            _candidateRun = 1;
        }

        if (now == State || _candidateRun < Math.Max(1, _window / 4))
        {
            return false;
        }

        State = now;
        Detail = Describe(now, rates, guestFrame);
        return true;
    }

    /// <summary>The window's per-second rates.</summary>
    private readonly record struct Rates(double Ctrl, double Front, double Rear, double FileBytes, double Opens, long Modules, long Shaders, double Blocks);

    private ScreenActivity Classify(int oldest, int newest, long present, out Rates rates)
    {
        ActivityCounters a = _counters[oldest];
        ActivityCounters b = _counters[newest];
        double seconds = Math.Max(1e-3, (_timeUs[newest] - _timeUs[oldest]) / 1e6);
        rates = new Rates(
            (b.CtrlReads - a.CtrlReads) / seconds,
            (b.FrontTouchReads - a.FrontTouchReads) / seconds,
            (b.RearTouchReads - a.RearTouchReads) / seconds,
            (b.FileBytes - a.FileBytes) / seconds,
            (b.FileOpens - a.FileOpens) / seconds,
            b.ModuleLoads - a.ModuleLoads,
            b.ShaderCompiles - a.ShaderCompiles,
            (b.CodeBlocks - a.CodeBlocks) / seconds);
        bool still = _stillSincePresent >= 0 && present - _stillSincePresent >= _window - 1;
        if (!still)
        {
            return ScreenActivity.Active;
        }

        bool loading = rates.FileBytes >= LoadBytesPerSecond || rates.Opens >= OpensPerSecond || rates.Modules > 0 ||
                       rates.Shaders > 0 || rates.Blocks >= CodeBlocksPerSecond;
        if (loading)
        {
            return ScreenActivity.Loading;
        }

        return rates.Ctrl + rates.Front + rates.Rear >= ReadsPerSecond ? ScreenActivity.WaitingForInput : ScreenActivity.Busy;
    }

    private string Describe(ScreenActivity state, in Rates r, long guestFrame)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        string reads = string.Format(inv, "reads front touch {0:F0}/s, ctrl buttons {1:F0}/s, rear touch {2:F0}/s", r.Front, r.Ctrl, r.Rear);
        string since = string.Format(inv, "screen static since frame {0}", _stillSinceFrame);
        return state switch
        {
            ScreenActivity.WaitingForInput => $"{reads}; {since}",
            ScreenActivity.Loading => string.Format(inv,
                "file reads {0:F0} KB/s, {1:F1} opens/s, {2} modules loaded, {3} shaders compiled, {4:F0} code blocks/s; {5}; {6}",
                r.FileBytes / 1024, r.Opens, r.Modules, r.Shaders, r.Blocks, reads, since),
            ScreenActivity.Busy => $"no input read, no file I/O or compilation (the CPU/GPU works or the game hangs); {since}",
            _ => string.Format(inv, "the picture changes (frame {0}); {1}", guestFrame, reads),
        };
    }

    /// <summary>The state's label at the start of the line: "WAITING FOR INPUT", "LOADING", "BUSY", "ACTIVE".</summary>
    public static string Label(ScreenActivity state) => state switch
    {
        ScreenActivity.WaitingForInput => "WAITING FOR INPUT",
        ScreenActivity.Loading => "LOADING",
        ScreenActivity.Busy => "BUSY",
        ScreenActivity.Active => "ACTIVE",
        _ => "UNKNOWN",
    };

    /// <summary>The full line: "<c>LABEL: title — detail</c>".</summary>
    public string Line(string title) => $"{Label(State)}: {title} — {Detail ?? "not enough frames yet"}";

    /// <summary>Continues or restarts the still run with this frame's picture (its grid in <see cref="_grid"/>).</summary>
    private void TrackStillness(long present, long guestFrame, ulong hash)
    {
        if (_stillSincePresent >= 0)
        {
            for (int i = 0; i < _loopCount; i++)
            {
                if (_loopHashes[i] == hash)
                {
                    return;
                }
            }

            if (ChangedCells(_anchorGrid, _grid) <= ChangedCellsLimit * _grid.Length)
            {
                if (_loopCount < LoopPictures)
                {
                    _loopHashes[_loopCount++] = hash;
                }

                return;
            }
        }

        _grid.CopyTo(_anchorGrid, 0);
        _loopHashes[0] = hash;
        _loopCount = 1;
        _stillSincePresent = present;
        _stillSinceFrame = guestFrame;
    }

    private static int ChangedCells(byte[] a, byte[] b)
    {
        int changed = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - b[i]) > CellDelta)
            {
                changed++;
            }
        }

        return changed;
    }

    /// <summary>The picture's hash (every 2nd pixel of every 2nd row, 6 bits per channel) and its luminance grid
    /// (<see cref="GridW"/> x <see cref="GridH"/> cell means over the same samples).</summary>
    public static ulong Signature(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> grid)
    {
        Span<int> sums = stackalloc int[GridW * GridH];
        Span<int> counts = stackalloc int[GridW * GridH];
        sums.Clear();
        counts.Clear();
        ulong hash = 14695981039346656037UL;
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            grid.Clear();
            return hash;
        }

        for (int y = 0; y < height; y += 2)
        {
            int row = y * width * 4;
            int cellRow = y * GridH / height * GridW;
            for (int x = 0; x < width; x += 2)
            {
                int p = row + x * 4;
                int r = rgba[p];
                int g = rgba[p + 1];
                int b = rgba[p + 2];
                int cell = cellRow + x * GridW / width;
                sums[cell] += (r * 54 + g * 183 + b * 19) >> 8;
                counts[cell]++;
                hash = (hash ^ (ulong)((r >> 2) | (g >> 2) << 8 | (b >> 2) << 16)) * 1099511628211UL;
            }
        }

        for (int i = 0; i < grid.Length && i < sums.Length; i++)
        {
            grid[i] = counts[i] == 0 ? (byte)0 : (byte)(sums[i] / counts[i]);
        }

        return hash;
    }
}
