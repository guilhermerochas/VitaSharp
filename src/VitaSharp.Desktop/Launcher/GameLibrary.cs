using VitaSharp.Core.Package;

namespace VitaSharp.Desktop.Launcher;

/// <summary>An installed title (a PS Vita application, a PSM title).</summary>
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

    /// <summary>What the title is (where the firmware keeps it).</summary>
    public VitaSharp.Core.Io.ContentKind Kind { get; init; } = VitaSharp.Core.Io.ContentKind.VitaApplication;

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

/// <summary>The installed titles (<see cref="InstalledContent"/>: PS Vita applications, PSM titles), as the library
/// shows them.</summary>
public static class GameLibrary
{
    public static List<GameEntry> Scan(string fsRoot)
    {
        return InstalledContent.Scan(fsRoot, System.Globalization.CultureInfo.CurrentUICulture.Name)
            .Select(From)
            .OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>A PS Vita application folder (an install, a folder copied in); null when it is not one.</summary>
    public static GameEntry? Read(string dir) => InstalledContent.ReadApplication(dir) is { } t ? From(t) : null;

    static GameEntry From(LibraryTitle t) =>
        new(t.TitleId, t.Title, t.Version, t.Category, t.Dir, t.IconPath, DirectorySize(t.Dir)) { ContentId = t.ContentId, Kind = t.Kind };

    static long DirectorySize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
