using VitaSharp.Core.Util;

namespace VitaSharp.Core.Input;

/// <summary>
/// The emulator's "waiting for input" detector (<see cref="InputWaitDetector"/>) fed from the real run: the renderer
/// hands it every presented frame (its debug copy of the picture), the HLE modules count the controller reads, the
/// file reads/opens and the module loads, the touch driver its reads per panel (<see cref="TouchDiagnostics"/>), the
/// renderer its shader compiles and the JIT its compiled blocks. A state change is one log line
/// ("<c>[input-wait] WAITING FOR INPUT: PCSC00045 'Killzone' — reads front touch 60/s, ...; screen static since frame
/// F</c>"), and <see cref="Describe"/> is the line of the F8 capture's context.txt/report.txt. Runs only when
/// <see cref="Active"/> (debug mode, a "Gravar tudo" capture, or <see cref="EnvVars.InputWait"/>=1): otherwise every
/// hook costs one static read.
/// </summary>
public static class InputWaitMonitor
{
    private static readonly bool s_env = Environment.GetEnvironmentVariable(EnvVars.InputWait) == "1";
    private static readonly int s_window = int.TryParse(Environment.GetEnvironmentVariable(EnvVars.InputWaitFrames), out int w) && w > 0 ? w : 60;
    private static int s_capturing;
    private static long s_ctrlReads, s_fileBytes, s_fileOpens, s_moduleLoads;
    private static readonly object s_lock = new();
    private static InputWaitDetector s_detector = new(s_window);
    private static WeakReference<Emulator>? s_emu;
    private static string? s_lastLine;

    /// <summary>The detector runs: debug mode, a capture recording, or <see cref="EnvVars.InputWait"/>=1.</summary>
    public static bool Active => s_env || DebugMode.Enabled || Volatile.Read(ref s_capturing) > 0;

    /// <summary>A "Gravar tudo" capture started recording (its pre-roll included).</summary>
    public static void CaptureStarted() => Interlocked.Increment(ref s_capturing);

    /// <summary>The capture stopped recording.</summary>
    public static void CaptureStopped()
    {
        if (Interlocked.Decrement(ref s_capturing) < 0)
        {
            Interlocked.Exchange(ref s_capturing, 0);
        }
    }

    /// <summary>The emulator whose process in front names the title of the lines (the last one created).</summary>
    public static void Attach(Emulator emu) => s_emu = new WeakReference<Emulator>(emu);

    /// <summary>One sceCtrl Peek/Read call (any port).</summary>
    public static void CtrlRead() => Interlocked.Increment(ref s_ctrlReads);

    /// <summary><paramref name="bytes"/> read from a file.</summary>
    public static void FileRead(long bytes) => Interlocked.Add(ref s_fileBytes, Math.Max(0, bytes));

    /// <summary>A file was opened.</summary>
    public static void FileOpened() => Interlocked.Increment(ref s_fileOpens);

    /// <summary>A module file was loaded.</summary>
    public static void ModuleLoaded() => Interlocked.Increment(ref s_moduleLoads);

    /// <summary>The counters now; <paramref name="shaderCompiles"/> is the renderer's total.</summary>
    public static ActivityCounters Counters(long shaderCompiles)
    {
        long blocks = 0;
        foreach (Cpu.Backends.IBlockCompiler compiler in Cpu.Jit.Chain)
        {
            blocks += compiler.Stats.Blocks;
        }

        return new ActivityCounters(Interlocked.Read(ref s_ctrlReads), TouchDiagnostics.Reads(0), TouchDiagnostics.Reads(1),
            Interlocked.Read(ref s_fileBytes), Interlocked.Read(ref s_fileOpens), Interlocked.Read(ref s_moduleLoads),
            shaderCompiles, blocks);
    }

    /// <summary>One presented frame (RGBA8; the renderer's GPU worker, after the frame retired).</summary>
    public static void OnFrame(long present, long guestFrame, ReadOnlySpan<byte> rgba, int width, int height, long shaderCompiles)
    {
        ActivityCounters counters = Counters(shaderCompiles);
        string? line = null;
        lock (s_lock)
        {
            if (s_detector.Feed(present, guestFrame, GuestClock.Microseconds, rgba, width, height, counters))
            {
                line = s_detector.Line(Title());
                s_lastLine = line;
            }
        }

        if (line != null)
        {
            Log.Info("[input-wait] " + line);
        }
    }

    /// <summary>The current state's line, recomputed with the current title (context.txt "screen:").</summary>
    public static string Describe()
    {
        lock (s_lock)
        {
            if (s_detector.State == ScreenActivity.Unknown)
            {
                return Active
                    ? $"UNKNOWN: {Title()} — fewer than {s_detector.Window} frames presented since the detector started"
                    : "UNKNOWN: the detector is off (debug mode, a capture or VITASHARP_INPUT_WAIT=1 turns it on)";
            }

            return s_detector.Line(Title());
        }
    }

    /// <summary>The state now (tests, scripts).</summary>
    public static ScreenActivity State
    {
        get
        {
            lock (s_lock)
            {
                return s_detector.State;
            }
        }
    }

    /// <summary>The last line logged, or null.</summary>
    public static string? LastLine => s_lastLine;

    /// <summary>Back to the start (a new session; tests).</summary>
    public static void Reset(int window = 0)
    {
        lock (s_lock)
        {
            s_detector = new InputWaitDetector(window > 0 ? window : s_window);
            s_lastLine = null;
        }
    }

    private static string Title()
    {
        if (s_emu != null && s_emu.TryGetTarget(out Emulator? emu))
        {
            try
            {
                return Capture.CaptureContext.Name(emu.Vita.Foreground ?? emu);
            }
            catch (Exception)
            {
                return emu.TitleId;
            }
        }

        return "?";
    }
}
