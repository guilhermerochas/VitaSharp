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

    /// <summary>The param.sfo CONTENT_ID ("UP9000-PCSA00008_00-..."): its first letter is the store region the
    /// content was published for. Empty when the title has none (homebrew).</summary>
    public string ContentId { get; init; } = "";

    /// <summary>Region from the param.sfo content ID: U America, E Europe, J Japan, H/K Asia; no content ID
    /// (homebrew) "Homebrew". Labels come from <see cref="Ui.Strings"/>.</summary>
    public string Region => ContentId.Length == 0 ? Ui.Strings.RegionHomebrew : ContentId[0] switch
    {
        'U' => Ui.Strings.RegionUs,
        'E' => Ui.Strings.RegionEurope,
        'J' => Ui.Strings.RegionJapan,
        'H' or 'K' => Ui.Strings.RegionAsia,
        _ => Ui.Strings.RegionOther,
    };

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
        string id = System.IO.Path.GetFileName(dir), title = id, version = "", category = "gd", contentId = "";
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
                contentId = sfo.ContentId;
            }
        }
        catch (InvalidDataException) { }
        var icon = VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(dir, "sce_sys/icon0.png");
        return new GameEntry(id, title, version, category, dir, File.Exists(icon) ? icon : null, DirectorySize(dir)) { ContentId = contentId };
    }

    static long DirectorySize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
