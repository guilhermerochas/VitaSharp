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
        foreach (var dev in new[] { "ux0", "ur0", "uma0", "imc0", "tm0", "vs0", "os0", "sa0", "pd0", "ud0", "host0", "sd0", "photo0", "music0", "video0" })
            Mount(dev, Path.Combine(fsRoot, dev));
        // the game card: gro0 (the card's read-only application) and grw0 (its writable area) exist only while a card
        // is in the slot. With them always mounted the shell's game card promoter saw a card (sceIoGetstat("gro0:")
        // succeeded), found no gro0:app and reported "Could not install. (C0-11274-5)" (0x80AF5022) at every boot.
        // A card dump placed in <fs>/gro0/app is a card in the slot.
        if (Directory.Exists(Path.Combine(fsRoot, "gro0", "app")))
        {
            Mount("gro0", Path.Combine(fsRoot, "gro0"));
            Mount("grw0", Path.Combine(fsRoot, "grw0"));
        }
    }

    public void Mount(string device, string hostPath)
    {
        _mounts[device.TrimEnd(':')] = hostPath;
        Directory.CreateDirectory(hostPath);
    }

    /// <summary>Removes a device mapping (the host directory is kept). Returns false if it was not mounted.</summary>
    public bool Unmount(string device) => _mounts.Remove(device.TrimEnd(':'));

    public bool IsMounted(string device) => _mounts.ContainsKey(device.TrimEnd(':'));

    /// <summary>Additional read-only layers searched before app0 (e.g. installed patches).</summary>
    public void AddApp0Overlay(string hostPath) => _app0Overlays.Insert(0, hostPath);

    public void ClearApp0Overlays() => _app0Overlays.Clear();

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
                var o = ResolveCaseInsensitive(overlay, rel);
                if (File.Exists(o) || Directory.Exists(o)) return o;
            }
        }
        if (!_mounts.TryGetValue(dev, out var root)) return null;
        return ResolveCaseInsensitive(root, rel);
    }

    /// <summary>
    /// Resolves <paramref name="rel"/> ("dir/sub/file", '/'-separated) under <paramref name="root"/> the way the
    /// Vita's case-insensitive file systems would: on case-sensitive hosts (Linux, Android, case-sensitive APFS) every
    /// existing path component is matched ignoring case, an exact-case entry winning over the others. Components that
    /// do not exist yet (files about to be created) keep the guest's spelling, appended to the matched prefix.
    /// On Windows the host file system already ignores case and the path is returned as is.
    /// </summary>
    public static string ResolveCaseInsensitive(string root, string rel)
    {
        rel = rel.Replace('\\', '/');
        string direct = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!CaseSensitiveHost || rel.Length == 0 || File.Exists(direct) || Directory.Exists(direct)) return direct;
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string cur = root;
        int i = 0;
        for (; i < parts.Length; i++)
        {
            var match = FindEntry(cur, parts[i]);
            if (match == null) break;
            cur = match;
        }
        for (; i < parts.Length; i++) cur = Path.Combine(cur, parts[i]);
        return cur;
    }

    static readonly bool CaseSensitiveHost = !OperatingSystem.IsWindows();

    /// <summary>Directory listings by folded name, keyed by directory path; revalidated against the directory's
    /// modification time so files created, renamed or deleted by the guest (or the user) are seen.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, Dictionary<string, string> Entries)> Listings = new();

    static string? FindEntry(string dir, string name)
    {
        string exact = Path.Combine(dir, name);
        if (File.Exists(exact) || Directory.Exists(exact)) return exact;
        DateTime stamp;
        try
        {
            if (!Directory.Exists(dir)) return null;
            stamp = Directory.GetLastWriteTimeUtc(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        if (!Listings.TryGetValue(dir, out var listing) || listing.Stamp != stamp)
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var e in Directory.EnumerateFileSystemEntries(dir))
                    entries.TryAdd(Path.GetFileName(e), e); // several spellings of one name: keep the first
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            listing = (stamp, entries);
            if (Listings.Count > 4096) Listings.Clear();
            Listings[dir] = listing;
        }
        return listing.Entries.TryGetValue(name, out var hit) ? hit : null;
    }
}
