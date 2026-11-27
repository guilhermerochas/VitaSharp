using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// The phone profile of the desktop (<c>--phone-profile</c> or VITASHARP_PHONE_PROFILE=1, docs/notes/cpu-phases.md,
/// "Reproducing the phone's timing on the PC"): the desktop runs with what the Android build turns on, so the phone's
/// slow paths show up on the PC.
/// <list type="bullet">
/// <item>waits without spinning (<see cref="HostTuning.AvoidBusyWaits"/>, as GamePerformance sets on the phone);</item>
/// <item>the Mali GPU profile (VITASHARP_GPU_PROFILE=mali: no BCn, the tile-based quirks, the phone's limits);</item>
/// <item>the phone's guest clock, 333 MHz (VITASHARP_CPU_MHZ);</item>
/// <item>two host cores (VITASHARP_PHONE_AFFINITY, a mask; default two cores of different physical pairs in the
/// upper half), like the phone's two fast cores;</item>
/// <item>the Android build's GC gen0 budget, 4 MB (DOTNET_GCgen0size: read when the runtime starts, so the desktop
/// starts itself again with it when it is missing);</item>
/// <item>and it reports what it cannot set itself: the runtime (Native AOT, no JIT, as on the phone, only from the
/// Native AOT publish) and whether the data folder is case-sensitive (Android's is; build/phone-profile makes one).</item>
/// </list>
/// Values set explicitly in the environment win over the profile's.
/// </summary>
public static class PhoneProfile
{
    /// <summary>The Android build's GC gen0 budget (VitaSharp.Android.csproj, VitaSharpGcGen0Size).</summary>
    public const string Gen0Budget = "0x400000";
    /// <summary>The runtime's environment variable for the gen0 budget (CoreCLR and Native AOT alike).</summary>
    public const string Gen0Variable = "DOTNET_GCgen0size";
    public const string GpuProfileValue = "mali";
    public const string CpuMhzValue = "333";

    /// <summary>True once <see cref="Apply"/> ran in this process.</summary>
    public static bool Active { get; private set; }

    /// <summary>The host cores the process was pinned to (0: not pinned).</summary>
    public static ulong AppliedAffinity { get; private set; }

    /// <summary>Why the cores were not pinned (empty when they were or nothing was asked).</summary>
    public static string AffinityNote { get; private set; } = "";

    /// <summary>Whether the data folder is case-sensitive (null: not checked).</summary>
    public static bool? DataCaseSensitive { get; private set; }

    /// <summary>True for "1", "on", "true", "yes" (any case).</summary>
    public static bool IsRequested(string? value)
    {
        return value?.Trim().ToLowerInvariant() is "1" or "on" or "true" or "yes";
    }

    /// <summary>The environment the profile wants, in the order applied: only the unset ones are set.</summary>
    public static (string Name, string Value)[] EnvironmentDefaults()
    {
        return
        [
            (EnvVars.GpuProfile, GpuProfileValue),
            (EnvVars.CpuMhz, CpuMhzValue),
            (Gen0Variable, Gen0Budget),
        ];
    }

    /// <summary>True when the runtime already started without the profile's gen0 budget (the process must start
    /// again with it).</summary>
    public static bool NeedsRestartForGc(Func<string, string?> getEnv)
    {
        return string.IsNullOrEmpty(getEnv(Gen0Variable));
    }

    /// <summary>Sets the unset environment defaults and the host tuning. Call before the emulator's static state is
    /// read (CpuCore reads VITASHARP_CPU_MHZ once).</summary>
    public static void Apply(Func<string, string?> getEnv, Action<string, string> setEnv)
    {
        foreach ((string name, string value) in EnvironmentDefaults())
        {
            if (string.IsNullOrEmpty(getEnv(name)))
            {
                setEnv(name, value);
            }
        }

        HostTuning.AvoidBusyWaits = true;
        Active = true;
    }

    /// <summary>The default two cores: on 4+ logical CPUs, CPUs n-4 and n-2 (upper half, different SMT pairs where
    /// siblings are adjacent); on fewer, the first two.</summary>
    public static ulong DefaultAffinity(int logicalCpus)
    {
        if (logicalCpus >= 4)
        {
            return (1UL << (logicalCpus - 4)) | (1UL << (logicalCpus - 2));
        }

        return logicalCpus >= 2 ? 0x3UL : 0x1UL;
    }

    /// <summary>VITASHARP_PHONE_AFFINITY: hex (0x..) or decimal mask; "0" = no pinning; unset or invalid = the default.</summary>
    public static ulong ParseAffinity(string? value, int logicalCpus)
    {
        string text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            return DefaultAffinity(logicalCpus);
        }

        bool parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong mask)
            : ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask);
        if (!parsed)
        {
            return DefaultAffinity(logicalCpus);
        }

        if (logicalCpus < 64)
        {
            mask &= (1UL << logicalCpus) - 1;
        }

        return mask;
    }

    /// <summary>Pins the whole process to <paramref name="mask"/> (Windows: the process; Linux: every thread that
    /// exists now, new ones inherit it; macOS has no affinity API).</summary>
    public static void ApplyAffinity(ulong mask)
    {
        if (mask == 0)
        {
            AffinityNote = "not pinned (VITASHARP_PHONE_AFFINITY=0)";
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using Process self = Process.GetCurrentProcess();
                self.ProcessorAffinity = (nint)mask;
            }
            else if (OperatingSystem.IsLinux())
            {
                PinLinuxThreads(mask);
            }
            else
            {
                AffinityNote = "not pinned (no CPU affinity on this system)";
                return;
            }

            AppliedAffinity = mask;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            AffinityNote = $"not pinned ({e.Message})";
        }
    }

    static void PinLinuxThreads(ulong mask)
    {
        ulong set = mask;
        foreach (string task in Directory.EnumerateDirectories("/proc/self/task"))
        {
            if (int.TryParse(Path.GetFileName(task), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tid)
                && sched_setaffinity(tid, (nint)sizeof(ulong), ref set) != 0)
            {
                throw new IOException($"sched_setaffinity({tid}) failed: errno {Marshal.GetLastPInvokeError()}");
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, nint cpusetsize, ref ulong mask);

    /// <summary>Checks (with a probe file) whether <paramref name="folder"/> tells names apart by case.</summary>
    public static bool? CheckCaseSensitive(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, $".CaseProbe{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            try
            {
                return !File.Exists(Path.Combine(folder, $".caseprobe{Environment.ProcessId}"));
            }
            finally
            {
                File.Delete(probe);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Records the data folder's case sensitivity for <see cref="Describe"/>.</summary>
    public static void CheckDataFolder(string folder)
    {
        DataCaseSensitive = CheckCaseSensitive(folder);
    }

    /// <summary>One line for the log and the F8 capture: what the profile set and what this process really has.</summary>
    public static string Describe(Func<string, string?> getEnv)
    {
        if (!Active)
        {
            return "off";
        }

        string cores = AppliedAffinity != 0
            ? FormattableString.Invariant($"cores 0x{AppliedAffinity:X} ({ulong.PopCount(AppliedAffinity)} of {Environment.ProcessorCount})")
            : AffinityNote.Length > 0 ? AffinityNote : "cores not pinned";
        string runtime = RuntimeFeature.IsDynamicCodeSupported
            ? "runtime with JIT (the phone has none: build/phone-profile publishes the Native AOT build)"
            : "Native AOT runtime (no JIT, as on the phone)";
        string caseText = DataCaseSensitive switch
        {
            true => "data folder case-sensitive",
            false => "data folder NOT case-sensitive (the phone's is; build/phone-profile makes one)",
            null => "data folder case sensitivity unknown",
        };
        return $"on: waits without spinning, GPU profile {getEnv(EnvVars.GpuProfile)}, guest clock {getEnv(EnvVars.CpuMhz)} MHz, {cores}, "
            + $"GC gen0 budget {getEnv(Gen0Variable) ?? "runtime default"}, {runtime}, {caseText}";
    }

    /// <summary>For tests: back to off.</summary>
    internal static void ResetForTests()
    {
        Active = false;
        AppliedAffinity = 0;
        AffinityNote = "";
        DataCaseSensitive = null;
        HostTuning.AvoidBusyWaits = false;
    }
}
