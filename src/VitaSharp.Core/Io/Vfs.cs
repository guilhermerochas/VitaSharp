namespace VitaSharp.Core.Io;

/// <summary>
/// Maps Vita device paths ("app0:eboot.bin", "ux0:/data/x", "savedata0:") to host paths.
/// Lookups are case-insensitive like the Vita's exFAT/PFS filesystems.
/// </summary>
public sealed class Vfs
{
    readonly Dictionary<string, string> _mounts = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _app0Overlays = new();

    public string FsRoot { get; }

    public Vfs(string fsRoot)
    {
        FsRoot = fsRoot;
        foreach (var dev in new[] { "ux0", "ur0", "uma0", "imc0", "tm0", "vs0", "os0", "sa0", "pd0", "ud0", "gro0", "grw0", "host0", "sd0", "photo0", "music0", "video0" })
            Mount(dev, Path.Combine(fsRoot, dev));
    }

    public void Mount(string device, string hostPath)
    {
        _mounts[device.TrimEnd(':')] = hostPath;
        Directory.CreateDirectory(hostPath);
    }

    /// <summary>Additional read-only layers searched before app0 (e.g. installed patches).</summary>
    public void AddApp0Overlay(string hostPath) => _app0Overlays.Insert(0, hostPath);

    public static (string device, string rel) Split(string vitaPath)
    {
        int c = vitaPath.IndexOf(':');
        if (c < 0) return ("", vitaPath.TrimStart('/'));
        return (vitaPath[..c], vitaPath[(c + 1)..].Replace('\\', '/').TrimStart('/'));
    }

    /// <summary>Debug console character devices "tty0:".."tty3:" (no host directory behind them).</summary>
    public static bool IsTty(string vitaPath, out int index)
    {
        index = -1;
        var (dev, rel) = Split(vitaPath);
        if (rel.Length != 0 || dev.Length != 4 || !dev.StartsWith("tty", StringComparison.OrdinalIgnoreCase)) return false;
        if (dev[3] is < '0' or > '3') return false;
        index = dev[3] - '0';
        return true;
    }

    /// <summary>Normalizes "." and ".." segments.</summary>
    static string Normalize(string rel)
    {
        var parts = new List<string>();
        foreach (var p in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p == ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(p);
        }
        return string.Join('/', parts);
    }

    public string? Resolve(string vitaPath, bool forWrite = false)
    {
        var (dev, rel) = Split(vitaPath);
        rel = Normalize(rel);
        if (dev.Equals("app0", StringComparison.OrdinalIgnoreCase) && !forWrite)
        {
            foreach (var overlay in _app0Overlays)
            {
                var o = FindCaseInsensitive(overlay, rel);
                if (o != null && (File.Exists(o) || Directory.Exists(o))) return o;
            }
        }
        if (!_mounts.TryGetValue(dev, out var root)) return null;
        return FindCaseInsensitive(root, rel) ?? Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Resolves path components case-insensitively on case-sensitive hosts.</summary>
    static string? FindCaseInsensitive(string root, string rel)
    {
        string direct = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        if (OperatingSystem.IsWindows() || File.Exists(direct) || Directory.Exists(direct)) return direct;
        string cur = root;
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(cur)) return direct;
            var match = Directory.EnumerateFileSystemEntries(cur)
                .FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
            cur = match ?? Path.Combine(cur, part);
        }
        return cur;
    }
}
