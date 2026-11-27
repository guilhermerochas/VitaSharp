using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Localization;
using VitaSharp.Mobile.Core.Storage;

namespace VitaSharp.Mobile.Core.Library;

/// <summary>One row of the Trofeus list. Hidden trophies keep their text secret until unlocked, as on the console. The
/// picture stays in the TRP (<paramref name="Set"/>, texts only) and <see cref="LoadIcon"/> reads it when the row is shown;
/// a row from the <see cref="TrophyListCache"/> has no set and points at the picture already extracted
/// (<paramref name="IconFile"/>), which the screen loads as a file (decoded off the UI thread, at the view's size).</summary>
public sealed record TrophyRow(string CommId, int Id, TrophyGrade Grade, string Name, string Detail, bool Hidden, DateTime? UnlockedUtc, TrophyPackage? Set,
    string? IconFile = null)
{
    /// <summary>The set has a picture for this trophy.</summary>
    public bool HasIcon => IconFile != null || Set?.HasTrophyIcon(Id) == true;

    /// <summary>The trophy's PNG, read now (not kept) from the extracted file or the TRP; null when there is none.</summary>
    public byte[]? LoadIcon()
    {
        if (IconFile != null)
        {
            try
            {
                return File.ReadAllBytes(IconFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return Set?.TrophyIcon(Id);
    }

    public bool Unlocked => UnlockedUtc != null;
    public string ShownName => Hidden && !Unlocked ? Loc.Get("MHiddenTrophy") : Name;
    public string ShownDetail => Hidden && !Unlocked ? Loc.Get("MHiddenTrophyDetail") : Detail;
}

/// <summary>The Trofeus screen (docs/android/mockups/trofeus.html) of one game: every set it ships, its rows and the
/// filters "Todos", "Conquistados" and "Faltam".</summary>
public sealed class TrophyList
{
    public string Title { get; }
    public IReadOnlyList<TrophyRow> Rows { get; }
    public TrophyProgress Progress { get; }

    public enum Filter
    {
        All, Earned, Missing
    }

    /// <summary>A list from rows already built (<see cref="TrophyListCache"/>).</summary>
    internal static TrophyList FromRows(string title, IReadOnlyList<TrophyRow> rows) => new(title, rows, Summarize(rows));

    private TrophyList(string title, IReadOnlyList<TrophyRow> rows, TrophyProgress progress)
    {
        Title = title;
        Rows = rows;
        Progress = progress;
    }

    public static TrophyList Load(GameEntry game, DataLayout data, int language = -1, TrophyTextsCache? cache = null)
    {
        var rows = new List<TrophyRow>();
        string title = game.Title;
        foreach ((string commId, string trp) in TrophyPackage.FindSets(game.AppDir))
        {
            TrophyPackage? package = cache != null ? cache.Get(trp, commId, language) : TrophyPackage.LoadTexts(trp, commId, language);
            if (package is not { } pkg)
            {
                continue;
            }

            if (pkg.Title.Length > 0)
            {
                title = pkg.Title;
            }

            Dictionary<int, ulong> unlocked = TrophyUnlocks.Load(TrophyUnlocks.PathFor(data.Fs, commId));
            foreach (TrophyInfo trophyInfo in pkg.Trophies)
            {
                rows.Add(new TrophyRow(commId, trophyInfo.Id, trophyInfo.Grade, trophyInfo.Name, trophyInfo.Detail, trophyInfo.Hidden,
                    unlocked.TryGetValue(trophyInfo.Id, out ulong tick) ? TrophyUnlocks.TickToUtc(tick) : null, pkg));
            }
        }
        return new TrophyList(title, rows, Summarize(rows));
    }

    /// <summary>Rows for a filter: unlocked first (most recent first), then locked by grade and id.</summary>
    public IReadOnlyList<TrophyRow> Filtered(Filter f) => Rows
        .Where(r => f == Filter.All || (f == Filter.Earned) == r.Unlocked)
        .OrderByDescending(r => r.Unlocked)
        .ThenByDescending(r => r.UnlockedUtc)
        .ThenBy(r => r.Grade)
        .ThenBy(r => r.Id)
        .ToList();

    public static TrophyProgress Summarize(IEnumerable<TrophyRow> rows)
    {
        var grades = new Dictionary<TrophyGrade, (int Earned, int Total)>();
        int earned = 0, total = 0;
        foreach (TrophyRow r in rows)
        {
            total++;
            if (r.Unlocked)
            {
                earned++;
            }

            (int Earned, int Total) g = grades.GetValueOrDefault(r.Grade);
            grades[r.Grade] = (g.Earned + (r.Unlocked ? 1 : 0), g.Total + 1);
        }
        return total == 0 ? TrophyProgress.None : new TrophyProgress(earned, total, grades);
    }

    public static string GradeName(TrophyGrade trophyGrade) => trophyGrade switch
    {
        TrophyGrade.Platinum => Loc.Get("MPlatinum"),
        TrophyGrade.Gold => Loc.Get("MGold"),
        TrophyGrade.Silver => Loc.Get("MSilver"),
        _ => "Bronze",
    };

    /// <summary>The grade colours of the mockups (and the desktop's trophy window).</summary>
    public static string GradeColor(TrophyGrade trophyGrade) => trophyGrade switch
    {
        TrophyGrade.Platinum => "#A9C4E8",
        TrophyGrade.Gold => "#E2B13C",
        TrophyGrade.Silver => "#C3C7CE",
        _ => "#C07A44",
    };
}
