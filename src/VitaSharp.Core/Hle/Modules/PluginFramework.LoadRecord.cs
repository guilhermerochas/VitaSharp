using System.Security.Cryptography;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// What the plugin loader did, kept so a recording can be analysed without the plugins: the emulator's build, the
/// installed firmware, the plugin configuration read (its Vita path and lines) and each plugin module listed in it (its
/// Vita path, module name, the SHA-256 and size of its file, what happened to it, the imported functions nothing
/// provides). Only Vita paths and the emulator's own words: a host path in a message becomes a placeholder.
/// </summary>
internal sealed partial class PluginHookStats
{
    /// <summary>One plugin module of the configuration.</summary>
    internal sealed class PluginRecord
    {
        public string Path = "";
        public bool Kernel;
        public string Module = "";
        public string Sha256 = "";
        public long Size;
        public string Result = "";
        public string Unresolved = "";
        /// <summary>When it loaded: "system boot" for a *KERNEL module, "with process TITLEID" for a user module.</summary>
        public string Moment = "";
    }

    /// <summary>The prefix of the loader's lines in a recording's context.txt (<see cref="LoadLines"/>).</summary>
    internal const string LoadPrefix = "plugins: ";

    string _build = "";
    string _firmware = "";
    string _configPath = "";
    List<string> _configLines = new();
    readonly List<PluginRecord> _plugins = new();

    /// <summary>The configuration the loader read (Vita path), its lines, the build and the firmware.</summary>
    internal void RecordConfig(string vitaPath, string text, string build, string firmware)
    {
        lock (_lock)
        {
            _configPath = vitaPath;
            _configLines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
            _build = build;
            _firmware = firmware;
        }
    }

    /// <summary>One plugin module the loader handled; <paramref name="hostFile"/> (may be null) gives its hash and
    /// size; <paramref name="fsRoot"/> is replaced in the result by a placeholder; <paramref name="moment"/> says when it
    /// loaded (the system's boot, a process's start).</summary>
    internal void RecordPlugin(string vitaPath, bool kernel, string module, string? hostFile, string result, string unresolved, string fsRoot,
        string moment = "")
    {
        var record = new PluginRecord
        {
            Path = vitaPath,
            Kernel = kernel,
            Module = module,
            Result = Placeholders(result, fsRoot),
            Unresolved = unresolved,
            Moment = moment,
        };
        if (hostFile != null && File.Exists(hostFile))
        {
            byte[] bytes = File.ReadAllBytes(hostFile);
            record.Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            record.Size = bytes.Length;
        }

        lock (_lock)
        {
            _plugins.Add(record);
        }
    }

    /// <summary>The data folder and the user's home in <paramref name="text"/> become placeholders.</summary>
    internal static string Placeholders(string text, string fsRoot)
    {
        string result = text;
        if (fsRoot.Length > 0)
        {
            result = result.Replace(Path.GetFullPath(fsRoot), "<data>", StringComparison.OrdinalIgnoreCase);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0)
        {
            result = result.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>The loader's lines of context.txt: build and firmware, the configuration and its lines, one line per
    /// plugin module (tab-separated: path, kernel or user, module, SHA-256, size, result, unresolved imports, when it
    /// loaded).</summary>
    internal IEnumerable<string> LoadLines()
    {
        lock (_lock)
        {
            if (_configPath.Length == 0 && _plugins.Count == 0)
            {
                return [];
            }

            var lines = new List<string>
            {
                $"{LoadPrefix}build {_build}, firmware {_firmware}",
                $"{LoadPrefix}configuration {_configPath} ({_configLines.Count} lines)",
            };
            foreach (string line in _configLines)
            {
                lines.Add($"{LoadPrefix}  | {line}");
            }

            foreach (PluginRecord plugin in _plugins)
            {
                string unresolved = plugin.Unresolved.Length > 0 ? $"\tunresolved imports: {plugin.Unresolved}" : "";
                string hash = plugin.Sha256.Length > 0 ? $"sha256 {plugin.Sha256}, {plugin.Size} bytes" : "file not found";
                string moment = plugin.Moment.Length > 0 ? $"\t{plugin.Moment}" : "";
                lines.Add($"{LoadPrefix}module {plugin.Path}\t{(plugin.Kernel ? "kernel" : "user")}\t{(plugin.Module.Length > 0 ? plugin.Module : "-")}\t{hash}\t{plugin.Result}{unresolved}{moment}");
            }

            return lines;
        }
    }
}
