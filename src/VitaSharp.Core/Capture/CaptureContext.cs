using System.Globalization;
using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Capture;

/// <summary>
/// What a "Gravar tudo" capture says about where the player is and what is sounding, so the report answers it without
/// anyone digging through the log: the process in front (game, system app or the shell, and for the shell its lock
/// screen, home screen or which title's LiveArea page), the other processes, and every audio port with the process and
/// thread that opened it, its role and whether it is playing. <see cref="Where"/> is one line (the timeline of screen
/// changes in input.tsv, kind <c>ctx</c>); <see cref="Describe"/> is context.txt, written at the start and the stop.
/// </summary>
public static class CaptureContext
{
    /// <summary>One line: where the player is now.</summary>
    public static string Where(Emulator emu)
    {
        var v = emu.Vita;
        var fg = v.Foreground ?? emu;
        if (v.Shell != null && v.ShellImposesSystemUi)
        {
            return "lock screen (the shell's system screen is on top" + (fg.IsShell ? ")" : $"; {Name(fg)} behind it)");
        }

        if (fg.IsShell)
        {
            return v.ShellLiveArea is { } la
                ? $"shell: LiveArea page of {la.TitleId} '{la.Title}' (the last LiveArea the shell opened; frames.png shows if it went back to the bubbles)"
                : "shell: home screen (bubble pages; no LiveArea opened yet)";
        }

        return $"{Kind(fg)} {Name(fg)} in front";
    }

    static string Kind(Emulator e) => e.IsShell ? "shell" : e.IsGameProgram && !e.IsSystemProgram ? "game" : e.IsSystemProgram ? "system app" : "application";

    static string Name(Emulator e) => e.Title.Length > 0 ? $"{e.TitleId} '{e.Title}'" : e.TitleId.Length > 0 ? e.TitleId : $"process 0x{e.Pid:X}";

    /// <summary>context.txt: the screen, the processes and the audio (ports, the BGM port, decoders).</summary>
    public static string Describe(Emulator emu, string when)
    {
        var v = emu.Vita;
        var sb = new StringBuilder();
        sb.AppendLine($"[{when}]");
        sb.AppendLine($"where: {Where(emu)}");
        var fg = v.Foreground ?? emu;
        foreach (var p in v.Processes)
        {
            string state = ReferenceEquals(p, fg) ? "in front" : p.Suspended ? "suspended (in the background)" : "running in the background";
            sb.AppendLine($"process: {Kind(p)} {Name(p)} pid 0x{p.Pid:X}: {state}");
        }
        foreach (var l in AudioLines(emu))
        {
            sb.AppendLine(l);
        }

        return sb.ToString();
    }

    /// <summary>One line per audio port (open, or released while the capture ran), the BGM port and the decoders.</summary>
    public static List<string> AudioLines(Emulator emu)
    {
        var v = emu.Vita;
        var lines = new List<string>();
        var ports = AudioDiagnostics.AllPorts();
        foreach (var p in ports)
        {
            if (p.ClosedAt > 0)
            {
                continue;
            }

            double since = AudioDiagnostics.SecondsSinceOutput(p);
            string activity = since < 0 ? "no output yet" : since < 0.5 ? "outputting now" : FormattableString.Invariant($"idle (last output {since:F1} s ago)");
            string muted = p.Type == 1 && p.Owner is { } o && v.BgmMuted(o) ? ", muted: another application is in front" : "";
            lines.Add(FormattableString.Invariant($"audio: {p.Name} ({p.DumpName}.wav): {Role(p)}; opened by {(p.Owner is { } ow ? Name(ow) : "?")} thread '{p.OwnerThread}'; {p.Freq} Hz grain {p.Grain}; {activity}{muted}"));
        }
        if (lines.Count == 0)
        {
            lines.Add("audio: no audio port open");
        }

        var holder = v.BgmHolder(out int prio);
        lines.Add(holder == null ? "audio: BGM: no process (nothing to route it to)"
            : $"audio: BGM routed to {Name(holder)}{(holder.IsShell ? " (the shell's music player, SceShellMusicCore: the home screen / LiveArea music)" : "")}" +
              (prio == Kernel.VitaSystem.BgmNoPriority ? ", as the process in front (no BGM priority set)" : $", BGM priority 0x{prio:X}") +
              string.Concat(v.Processes.Where(p => p.BgmPriority > 0).Select(p => $"; {Name(p)} priority 0x{p.BgmPriority:X}{(p.Suspended ? " (suspended: not counted)" : "")}")));
        foreach (var s in AudioDiagnostics.StreamDescriptions())
        {
            lines.Add("audio: decoder/stream " + s);
        }

        return lines;
    }

    /// <summary>What a port is for, from its type and owner.</summary>
    public static string Role(AudioDiagnostics.PortStats p)
    {
        bool shell = p.Owner?.IsShell == true;
        bool system = shell || p.Owner?.IsSystemProgram == true; // the system software: run from vs0:
        return (p.Type, shell, system) switch
        {
            (1, true, _) => "background music (the shell's BGM)",
            (1, _, _) => "background music (BGM port)",
            (2, _, _) => "voice",
            (0, true, _) => "system sounds (the shell's UI sounds)",
            (0, false, true) => "system app sounds",
            (0, false, false) => "game sound",
            _ => "audio",
        };
    }

    /// <summary>"music playing on port N" vs "no continuous audio stream": from the capture's per-port WAVs, a port
    /// whose sound covers most of the recording is a continuous stream (music, ambience, a video's sound).</summary>
    public static string StreamVerdict(IReadOnlyList<(string File, double Seconds, double SoundSeconds)> ports, double recordedSeconds)
    {
        var cont = ports.Where(p => recordedSeconds > 0 && p.SoundSeconds >= 0.6 * recordedSeconds).ToList();
        if (cont.Count == 0)
        {
            return FormattableString.Invariant($"no continuous audio stream (music) during the recording: {(ports.Count == 0 ? "no port output anything" : string.Join(", ", ports.Select(p => FormattableString.Invariant($"{p.File} sounded {p.SoundSeconds:F1} s"))))} of {recordedSeconds:F1} s");
        }

        return string.Join("; ", cont.Select(p => FormattableString.Invariant($"continuous sound (music or a stream) on {p.File}: {p.SoundSeconds:F1} s of {recordedSeconds:F1} s")));
    }
}
