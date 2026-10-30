using System.Globalization;
using VitaSharp.Android.Settings;
using VitaSharp.Android.Storage;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Io;
using VitaSharp.Core.Package;

namespace VitaSharp.Android.Library;

/// <summary>Trophies earned and total of a game, with the earned/total per grade (the trophy card of detalhes.html).</summary>
public sealed record TrophyProgress(int Earned, int Total, IReadOnlyDictionary<TrophyGrade, (int Earned, int Total)> ByGrade)
{
    public static TrophyProgress None { get; } = new(0, 0, new Dictionary<TrophyGrade, (int, int)>());
    public bool HasTrophies => Total > 0;
    public int Percent => Total == 0 ? 0 : Earned * 100 / Total;
    public double Fraction => Total == 0 ? 0 : (double)Earned / Total;
    /// <summary>"18/48", or "Sem troféus" for a game without a trophy set.</summary>
    public string Text => HasTrophies ? $"{Earned}/{Total}" : "Sem troféus";
}

/// <summary>An installed game as the Biblioteca shows it.</summary>
public sealed record GameEntry(string TitleId, string Title, string Version, string Category, string AppDir, string? IconPath, long SizeBytes)
{
    /// <summary>The two or three letters on the placeholder cover (the mockups' "KZ", "UC", "RBS").</summary>
    public string Initials => GameLibrary.Initials(Title);

    public string SizeText => SizeBytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{SizeBytes / (double)(1L << 30):F1} GB"),
        >= 1L << 20 => $"{SizeBytes / (1L << 20)} MB",
        _ => $"{SizeBytes / 1024} KB",
    };

    /// <summary>"PCSC00045 · v1.12 · 3,2 GB".</summary>
    public string Subtitle => string.Join(" · ", new[] { TitleId, Version.Length > 0 ? "v" + Version : "", SizeBytes > 0 ? SizeText : "" }.Where(s => s.Length > 0));
}

/// <summary>
/// The Biblioteca: the games installed under &lt;data&gt;/fs/ux0/app (VPK installs or decrypted folders copied in),
/// with their trophy progress (TROPHY.TRP of the game + the unlocks in ux0:user/00/trophy) and play statistics.
/// </summary>
public static class GameLibrary
{
    public static List<GameEntry> Scan(DataLayout data)
    {
        var list = new List<GameEntry>();
        if (!Directory.Exists(data.Apps)) return list;
        foreach (var dir in Directory.GetDirectories(data.Apps))
            if (Read(dir) is { } e) list.Add(e);
        return list.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static GameEntry? Read(string dir)
    {
        if (!File.Exists(Vfs.ResolveCaseInsensitive(dir, "eboot.bin"))) return null;
        string id = Path.GetFileName(dir), title = id, version = "", category = "gd";
        var sfoPath = Vfs.ResolveCaseInsensitive(dir, "sce_sys/param.sfo");
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
        catch (Exception e) when (e is InvalidDataException or IOException) { }
        var icon = Vfs.ResolveCaseInsensitive(dir, "sce_sys/icon0.png");
        return new GameEntry(id, title, version, category, dir, File.Exists(icon) ? icon : null, DirectorySize(dir));
    }

    /// <summary>Trophy progress over every set the game ships (usually one).</summary>
    public static TrophyProgress Trophies(GameEntry game, DataLayout data)
    {
        int earned = 0, total = 0;
        var grades = new Dictionary<TrophyGrade, (int Earned, int Total)>();
        foreach (var (commId, trp) in TrophyPackage.FindSets(game.AppDir))
        {
            if (TrophyPackage.Load(trp, commId) is not { } pkg) continue;
            var unlocked = TrophyUnlocks.Load(TrophyUnlocks.PathFor(data.Fs, commId));
            foreach (var t in pkg.Trophies)
            {
                bool got = unlocked.ContainsKey(t.Id);
                total++;
                if (got) earned++;
                var g = grades.GetValueOrDefault(t.Grade);
                grades[t.Grade] = (g.Earned + (got ? 1 : 0), g.Total + 1);
            }
        }
        return total == 0 ? TrophyProgress.None : new TrophyProgress(earned, total, grades);
    }

    /// <summary>Library filters (the chips "Todos · N", "Recentes", "Jogáveis").</summary>
    public enum Filter { All, Recent, Playable }

    /// <summary>
    /// The games matching the search text and filter. "Recentes": played at least once, most recent first.
    /// "Jogáveis": applications (category gd), not patches or add-ons copied into app/.
    /// </summary>
    public static IReadOnlyList<GameEntry> Query(IEnumerable<GameEntry> games, string search, Filter filter, Func<GameEntry, GameSettings> settings)
    {
        var q = games.Where(g => search.Length == 0
            || g.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || g.TitleId.Contains(search, StringComparison.OrdinalIgnoreCase));
        return filter switch
        {
            Filter.Recent => q.Select(g => (g, s: settings(g))).Where(x => x.s.LastPlayedUtc != null)
                .OrderByDescending(x => x.s.LastPlayedUtc).Select(x => x.g).ToList(),
            Filter.Playable => q.Where(g => g.Category.StartsWith("gd", StringComparison.OrdinalIgnoreCase)).ToList(),
            _ => q.ToList(),
        };
    }

    /// <summary>"12h 40m", "35m" or "—".</summary>
    public static string PlayTime(long seconds) => seconds switch
    {
        <= 0 => "—",
        < 3600 => $"{Math.Max(1, seconds / 60)}m",
        _ => $"{seconds / 3600}h {seconds % 3600 / 60:D2}m",
    };

    /// <summary>"Hoje", "Ontem", "há 3 dias" or the date.</summary>
    public static string LastPlayed(DateTime? utc, DateTime nowUtc)
    {
        if (utc is not { } t) return "Nunca";
        int days = (nowUtc.ToLocalTime().Date - t.ToLocalTime().Date).Days;
        return days switch
        {
            <= 0 => "Hoje",
            1 => "Ontem",
            < 7 => $"há {days} dias",
            _ => t.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Initials of a title: the first letters of its first three significant words ("Killzone Mercenary" =
    /// "KM", "Resistance: Burning Skies" = "RBS"), or its first two letters.</summary>
    public static string Initials(string title)
    {
        var words = title.Split([' ', ':', '-', '_', '.', '™', '®'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0]) && !(w.Length <= 2 && char.IsLower(w[0]))).ToArray();
        if (words.Length >= 2) return string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])));
        var s = words.Length == 1 ? words[0] : title;
        return s.Length >= 2 ? s[..2].ToUpperInvariant() : s.ToUpperInvariant();
    }

    /// <summary>A stable cover colour for a game without artwork (the mockups' muted tiles).</summary>
    public static string CoverColor(string titleId)
    {
        string[] palette = ["#4A3A28", "#4A2A30", "#3B2F4F", "#2A3D5C", "#2E4A3A", "#4A4128", "#34405C", "#4A2E45"];
        uint h = 2166136261;
        foreach (char c in titleId) h = (h ^ c) * 16777619;
        return palette[h % (uint)palette.Length];
    }

    static long DirectorySize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}
