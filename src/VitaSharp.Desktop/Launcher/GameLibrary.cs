using VitaSharp.Core.Package;

namespace VitaSharp.Desktop.Launcher;

/// <summary>An installed title found under ux0:app.</summary>
public sealed record GameEntry(string TitleId, string Title, string Version, string Category, string Path, string? IconPath, long SizeBytes)
{
    public string SizeText => SizeBytes switch
    {
        >= 1L << 30 => $"{SizeBytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{SizeBytes / (double)(1L << 20):F0} MB",
        _ => $"{SizeBytes / 1024.0:F0} KB",
    };

    public string CategoryText => Category switch
    {
        "gd" => "Game",
        "gp" => "Game patch",
        "ac" => "Add-on",
        _ => Category,
    };

    /// <summary>Region from the title ID: PCSA/PCSE US, PCSB/PCSF Europe, PCSC/PCSG Japan, PCSD/PCSH Asia; anything
    /// else (homebrew IDs) "Homebrew". Labels come from <see cref="Ui.Strings"/>.</summary>
    public string Region => TitleId.Length >= 4 && TitleId.StartsWith("PCS", StringComparison.Ordinal) ? TitleId[3] switch
    {
        'A' or 'E' => Ui.Strings.RegionUs,
        'B' or 'F' => Ui.Strings.RegionEurope,
        'C' or 'G' => Ui.Strings.RegionJapan,
        'D' or 'H' => Ui.Strings.RegionAsia,
        _ => Ui.Strings.RegionOther,
    } : Ui.Strings.RegionHomebrew;

    /// <summary>sce_sys/pic0.png: the 960x544 picture shown behind the game (LiveArea background), when the game has one.</summary>
    public string? Pic0Path => VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(Path, "sce_sys/pic0.png") is var p && File.Exists(p) ? p : null;

    /// <summary>Up to two initials of the title (placeholder for a missing icon).</summary>
    public string Initials => string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));
}

/// <summary>Scans the virtual ux0:app folder (titles installed with "install" or copied in as decrypted folders).</summary>
public static class GameLibrary
{
    public static List<GameEntry> Scan(string ux0)
    {
        var list = new List<GameEntry>();
        var appDir = System.IO.Path.Combine(ux0, "app");
        if (!Directory.Exists(appDir)) return list;
        foreach (var dir in Directory.GetDirectories(appDir))
        {
            var entry = Read(dir);
            if (entry != null) list.Add(entry);
        }
        return list.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static GameEntry? Read(string dir)
    {
        if (!File.Exists(VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(dir, "eboot.bin"))) return null;
        string id = System.IO.Path.GetFileName(dir), title = id, version = "", category = "gd";
        var sfoPath = VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(dir, "sce_sys/param.sfo");
        try
        {
            if (File.Exists(sfoPath))
            {
                var sfo = Sfo.Load(sfoPath);
                if (sfo.Title.Length > 0) title = sfo.Title.Replace('\n', ' ');
                if (sfo.TitleId.Length > 0) id = sfo.TitleId;
                version = sfo.AppVersion.TrimStart('0');
                if (version.StartsWith('.')) version = "0" + version;
                category = sfo.Category;
            }
        }
        catch (InvalidDataException) { }
        var icon = VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(dir, "sce_sys/icon0.png");
        return new GameEntry(id, title, version, category, dir, File.Exists(icon) ? icon : null, DirectorySize(dir));
    }

    static long DirectorySize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
