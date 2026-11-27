using System.Collections.Concurrent;

namespace VitaSharp.Core.Util;

/// <summary>
/// The detailed log: extra, rate-limited logging meant to diagnose problems from the log alone (first use of
/// shaders/pipelines/targets/texture formats, HLE error returns, address-overlap choices, periodic PERF lines,
/// deduplicated warnings, frame anomaly checks). For the player it is part of "Gravar tudo" (F8): it is on while a
/// session capture records or keeps its pre-roll (<see cref="CaptureStarted"/>), so the capture's log.txt has it;
/// there is no separate switch in the apps. Developers can also keep it on for a whole run (--debug-mode,
/// VITASHARP_DEBUG_MODE=1: <see cref="Enable"/>). Every site checks <see cref="Enabled"/> first, so the cost when it is
/// off is one static read. Turned on mid-game, the first-use logs report everything used from then on. See
/// docs/notes/debug-mode.md.
/// </summary>
public static class DebugMode
{
    private static readonly object s_lock = new();
    private static bool s_explicit;
    private static int s_captures;

    /// <summary>The detailed log is on: set explicitly (--debug-mode) or while a session capture runs.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>Set explicitly for the whole run (--debug-mode, VITASHARP_DEBUG_MODE=1), not by a capture.</summary>
    public static bool Explicit => s_explicit;

    public static void Enable()
    {
        lock (s_lock)
        {
            s_explicit = true;
            Refresh();
        }
    }

    public static void Disable()
    {
        lock (s_lock)
        {
            s_explicit = false;
            Refresh();
        }
    }

    /// <summary>A session capture began recording or keeping its pre-roll: the detailed log is on until the matching
    /// <see cref="CaptureStopped"/> (nested captures count).</summary>
    public static void CaptureStarted()
    {
        lock (s_lock)
        {
            s_captures++;
            Refresh();
        }
    }

    /// <summary>The session capture of a <see cref="CaptureStarted"/> stopped recording and keeping its pre-roll.</summary>
    public static void CaptureStopped()
    {
        lock (s_lock)
        {
            s_captures = Math.Max(0, s_captures - 1);
            Refresh();
        }
    }

    private static void Refresh()
    {
        bool was = Enabled;
        bool now = s_explicit || s_captures > 0;
        Enabled = now;
        Log.DedupeWarnings = now;
        Cpu.PhaseProfile.SetDetailed(now); // the CPU phase profile counts while the detailed log is on (Cpu/PhaseProfile.cs)
        if (now && !was)
        {
            AudioDiagnostics.OnDebugModeChanged(true); // snapshot of the ports and streams opened before
        }
    }

    /// <summary>A session capture is recording: HLE error returns and unimplemented calls are counted (and logged,
    /// rate-limited) as in debug mode, without its other checks.</summary>
    public static bool CollectHleErrors;

    /// <summary>Guest frame number for log lines (frames presented by the display).</summary>
    public static long Frame => Hle.Modules.Display.FramesPresented;

    private static readonly ConcurrentDictionary<(string, uint), int> s_hleErrors = new();
    private static readonly ConcurrentDictionary<string, int> s_unimplemented = new();
    private static readonly ConcurrentDictionary<uint, string> s_unlinked = new();

    /// <summary>
    /// A call of an import that no loaded module exports and FW 3.74 does not have (its NID is not in the firmware's
    /// NID list): on a Vita 3.74 the module manager writes the "not linked" stub there (sub r12, pc, #8; mov pc, #0)
    /// and the call jumps to address 0. VitaSharp keeps returning 0 (the NID list may miss a real function); the first
    /// call of each NID is logged as a warning and listed in <see cref="HleSummary"/> (the F8 capture's
    /// hle_errors.txt, the session report), so a report already names the cause. Returns whether it is the first.
    /// </summary>
    public static bool UnlinkedImport(uint nid, string library, uint lr)
    {
        if (!s_unlinked.TryAdd(nid, library))
        {
            return false;
        }

        Log.Warn($"import sem quem exporte: NID 0x{nid:X8}, biblioteca {(string.IsNullOrEmpty(library) ? "?" : library)} (chamado de 0x{lr:X8}); "
            + "no Vita 3.74 este stub saltaria para 0 (sub r12, pc, #8; mov pc, #0); o VitaSharp devolve 0");
        return true;
    }

    /// <summary>The imports without an exporter called so far: NID and library.</summary>
    public static IReadOnlyDictionary<uint, string> UnlinkedImports => s_unlinked;

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

        foreach (KeyValuePair<uint, string> kv in s_unlinked.OrderBy(k => k.Key))
        {
            yield return $"[dbg] UNLINKED import: NID 0x{kv.Key:X8}, library {(string.IsNullOrEmpty(kv.Value) ? "?" : kv.Value)}: no module exports it and FW 3.74 has no such function; on a Vita 3.74 its stub jumps to 0 (sub r12, pc, #8; mov pc, #0), VitaSharp returns 0";
        }
    }
}
