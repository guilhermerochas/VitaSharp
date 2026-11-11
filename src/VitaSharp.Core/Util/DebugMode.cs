using System.Collections.Concurrent;

namespace VitaSharp.Core.Util;

/// <summary>
/// "Modo de depuração" (--debug-mode, launcher Configurações > Geral, or VITASHARP_DEBUG_MODE=1): extra, rate-limited
/// logging meant to diagnose problems from the log alone (first use of shaders/pipelines/targets/texture formats,
/// HLE error returns, address-overlap choices, periodic PERF lines, deduplicated warnings, frame anomaly checks).
/// Set at startup or toggled in game (F9); every site checks this flag first, so the cost when it is off is one
/// static read. Turned on mid-game, the first-use logs report everything used from then on. See
/// docs/notes/debug-mode.md.
/// </summary>
public static class DebugMode
{
    public static bool Enabled { get; private set; }

    public static void Enable()
    {
        bool was = Enabled;
        Enabled = true;
        Log.DedupeWarnings = true;
        if (!was)
        {
            AudioDiagnostics.OnDebugModeChanged(true); // snapshot of the ports and streams opened before
        }
    }

    public static void Disable()
    {
        Enabled = false;
        Log.DedupeWarnings = false;
    }

    /// <summary>A session capture is recording: HLE error returns and unimplemented calls are counted (and logged,
    /// rate-limited) as in debug mode, without its other checks.</summary>
    public static bool CollectHleErrors;

    /// <summary>Guest frame number for log lines (frames presented by the display).</summary>
    public static long Frame => Hle.Modules.Display.FramesPresented;

    private static readonly ConcurrentDictionary<(string, uint), int> s_hleErrors = new();
    private static readonly ConcurrentDictionary<string, int> s_unimplemented = new();

    /// <summary>The n-th occurrence of something repetitive is logged when n is 1-3 or a power of ten.</summary>
    public static bool ShouldLog(int n) => n <= 3 || n is 10 or 100 or 1000 or 10000 or 100000 or 1000000;

    /// <summary>
    /// An HLE function returned an SCE error code (0x80000000-0x80FFFFFF: the Vita's error range; user memory starts
    /// at 0x81000000, so pointers are not mistaken for errors). Functions returning nothing leave r0 untouched, which
    /// is why only that range is considered. Rate-limited per (function, code).
    /// </summary>
    public static void HleError(string thread, string function, uint ret, uint a0, uint a1, uint a2, uint a3, uint lr)
    {
        int n = s_hleErrors.AddOrUpdate((function, ret), 1, (_, c) => c + 1);
        if (ShouldLog(n))
        {
            Log.Info($"[dbg] f{Frame} HLE-ERR [{thread}] {function}(0x{a0:X}, 0x{a1:X}, 0x{a2:X}, 0x{a3:X}) = 0x{ret:X8} lr=0x{lr:X8}{(n > 1 ? $" (#{n})" : "")}");
        }
    }

    public static void HleUnimplemented(string thread, string library, string function, uint nid, uint lr)
    {
        int n = s_unimplemented.AddOrUpdate(function, 1, (_, c) => c + 1);
        if (ShouldLog(n))
        {
            Log.Info($"[dbg] f{Frame} HLE-UNIMPL [{thread}] {library}::{function} (NID 0x{nid:X8}) lr=0x{lr:X8} (#{n})");
        }
    }

    /// <summary>Summary lines: HLE error returns and unimplemented calls by count.</summary>
    public static IEnumerable<string> HleSummary()
    {
        foreach (KeyValuePair<(string, uint), int> kv in s_hleErrors.OrderByDescending(k => k.Value).Take(40))
        {
            yield return $"[dbg] HLE-ERR summary: {kv.Key.Item1} = 0x{kv.Key.Item2:X8} x{kv.Value}";
        }

        foreach (KeyValuePair<string, int> kv in s_unimplemented.OrderByDescending(k => k.Value).Take(40))
        {
            yield return $"[dbg] HLE-UNIMPL summary: {kv.Key} x{kv.Value}";
        }
    }
}
