using System.Globalization;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Io;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Package;

namespace VitaSharp.Core.Capture;

/// <summary>
/// What a capture says about the program and the system it was recorded on, so that whoever analyses it knows which
/// title, version and setup to look at without having them (session.txt): the title's guest folder (not the host
/// path), its version as the system reads it (the installed update's param.sfo overlays the application's, as appmgr
/// mounts it), its category and content id, the firmware, the emulator options in effect (the VITASHARP_* environment
/// variables set) and every module loaded in the process and the kernel with its image hash (the same hash = the same
/// executable).
/// </summary>
public static class CaptureIdentity
{
    /// <summary>The session.txt lines (not yet redacted: the caller passes them through <see cref="CapturePrivacy"/>).</summary>
    public static IEnumerable<string> Describe(Emulator emu)
    {
        var lines = new List<string>();
        string fsRoot = emu.Options.FsRoot;
        string appGuest = SafeGuestPath(fsRoot, emu.AppPath) ?? "(outside the data folder)";
        lines.Add($"app-guest-path: {appGuest}");
        lines.Add($"executable: app0:{emu.CurrentSelf}");
        Sfo? shown = emu.ParamSfo;
        Sfo? original = string.IsNullOrEmpty(emu.AppPath) ? null : ReadSfo(Vfs.ResolveCaseInsensitive(emu.AppPath, "sce_sys/param.sfo"));
        if (shown != null)
        {
            lines.Add($"app-version: {Value(shown.AppVersion)} (APP_VER the system reads{(original != null && original.AppVersion != shown.AppVersion ? $"; the application's own is {Value(original.AppVersion)}, an installed update overlays it" : "")})");
            lines.Add($"category: {Value(shown.Category)}");
            lines.Add($"content-id: {Value(shown.ContentId)}");
            uint attribute = shown.GetUInt("ATTRIBUTE");
            lines.Add(FormattableString.Invariant($"sfo: VERSION {Value(shown.GetString("VERSION"))}, PSP2_SYSTEM_VER 0x{shown.GetUInt("PSP2_SYSTEM_VER"):X8}, ATTRIBUTE 0x{attribute:X8}"));
        }
        else
        {
            lines.Add("app-version: (no param.sfo)");
        }

        FirmwareInfo? firmware = null;
        try
        {
            firmware = FirmwareInfo.Read(fsRoot);
        }
        catch (IOException)
        {
        }

        lines.Add(firmware != null
            ? FormattableString.Invariant($"firmware: {Value(firmware.Version)} build {firmware.Build} ({string.Join(", ", firmware.Partitions)})")
            : "firmware: none installed");
        lines.Add($"kernel-plugins: {(emu.KernelPluginsEnabled ? "on" : "off")}");
        lines.Add($"system-language: {(emu.Options.SystemLanguage is int language ? language.ToString(CultureInfo.InvariantCulture) : "the system's")}");
        lines.Add($"psn: {(emu.Options.Psn.SignedIn ? "signed in" : "signed out")}, local services {(emu.Options.Psn.LocalServices ? "on" : "off")}");
        foreach (string variable in EnvironmentOptions())
        {
            lines.Add($"env: {variable}");
        }

        foreach (string module in Modules(emu))
        {
            lines.Add($"module: {module}");
        }

        return lines;
    }

    /// <summary>The VITASHARP_* environment variables set (the options they change are in effect), by name.</summary>
    internal static IEnumerable<string> EnvironmentOptions()
    {
        var list = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = entry.Key?.ToString() ?? "";
            if (!name.StartsWith("VITASHARP_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // a secret never leaves the computer (the RPCN password the launcher passes this way)
            bool secret = name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase);
            list.Add($"{name}={(secret ? "<secret>" : entry.Value)}");
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>Every loaded module of the process and the kernel: name, guest path, image hash, kernel or user.</summary>
    private static IEnumerable<string> Modules(Emulator emu)
    {
        var list = new List<string>();
        AddModules(list, emu, "user");
        if (emu.Vita.KernelProcess is { } kernel && !ReferenceEquals(kernel, emu))
        {
            AddModules(list, kernel, "kernel");
        }

        return list;
    }

    private static void AddModules(List<string> list, Emulator process, string kind)
    {
        foreach (LoadedModule module in process.Kernel.Modules.Values.OrderBy(m => m.Uid).ToList())
        {
            uint seg0 = module.Segments.TryGetValue(0, out SegmentInfo segment) ? segment.Address : 0;
            list.Add(FormattableString.Invariant($"{(module.IsKernel ? "kernel" : kind)} {module.Name} {module.Path} at 0x{seg0:X8} hash {module.ImageHash:X16}"));
        }
    }

    private static string? SafeGuestPath(string fsRoot, string host)
    {
        try
        {
            return string.IsNullOrEmpty(host) ? null : FirmwareLayout.HostToGuest(fsRoot, host);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Sfo? ReadSfo(string path)
    {
        try
        {
            return File.Exists(path) ? Sfo.Load(path) : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string Value(string? s) => string.IsNullOrEmpty(s) ? "?" : s;
}
