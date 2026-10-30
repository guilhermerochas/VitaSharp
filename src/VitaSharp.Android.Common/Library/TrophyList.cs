using VitaSharp.Android.Storage;
using VitaSharp.Core.Hle.Np;

namespace VitaSharp.Android.Library;

/// <summary>One row of the Troféus list. Hidden trophies keep their text secret until unlocked, as on the console.</summary>
public sealed record TrophyRow(string CommId, int Id, TrophyGrade Grade, string Name, string Detail, bool Hidden, DateTime? UnlockedUtc, byte[]? Icon)
{
    public bool Unlocked => UnlockedUtc != null;
    public string ShownName => Hidden && !Unlocked ? "Troféu oculto" : Name;
    public string ShownDetail => Hidden && !Unlocked ? "Continue jogando para descobrir." : Detail;
}

/// <summary>The Troféus screen (docs/android/mockups/trofeus.html) of one game: every set it ships, its rows and the
/// filters "Todos", "Conquistados" and "Faltam".</summary>
public sealed class TrophyList
{
    public string Title { get; }
    public IReadOnlyList<TrophyRow> Rows { get; }
    public TrophyProgress Progress { get; }

    public enum Filter { All, Earned, Missing }

    TrophyList(string title, IReadOnlyList<TrophyRow> rows, TrophyProgress progress)
    {
        Title = title;
        Rows = rows;
        Progress = progress;
    }

    public static TrophyList Load(GameEntry game, DataLayout data, int language = -1)
    {
        var rows = new List<TrophyRow>();
        string title = game.Title;
        foreach (var (commId, trp) in TrophyPackage.FindSets(game.AppDir))
        {
            if (TrophyPackage.Load(trp, commId, language) is not { } pkg) continue;
            if (pkg.Title.Length > 0) title = pkg.Title;
            var unlocked = TrophyUnlocks.Load(TrophyUnlocks.PathFor(data.Fs, commId));
            foreach (var t in pkg.Trophies)
                rows.Add(new TrophyRow(commId, t.Id, t.Grade, t.Name, t.Detail, t.Hidden,
                    unlocked.TryGetValue(t.Id, out var tick) ? TrophyUnlocks.TickToUtc(tick) : null, pkg.TrophyIcon(t.Id)));
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
        foreach (var r in rows)
        {
            total++;
            if (r.Unlocked) earned++;
            var g = grades.GetValueOrDefault(r.Grade);
            grades[r.Grade] = (g.Earned + (r.Unlocked ? 1 : 0), g.Total + 1);
        }
        return total == 0 ? TrophyProgress.None : new TrophyProgress(earned, total, grades);
    }

    public static string GradeName(TrophyGrade g) => g switch
    {
        TrophyGrade.Platinum => "Platina", TrophyGrade.Gold => "Ouro", TrophyGrade.Silver => "Prata", _ => "Bronze",
    };

    /// <summary>The grade colours of the mockups (and the desktop's trophy window).</summary>
    public static string GradeColor(TrophyGrade g) => g switch
    {
        TrophyGrade.Platinum => "#A9C4E8", TrophyGrade.Gold => "#E2B13C", TrophyGrade.Silver => "#C3C7CE", _ => "#C07A44",
    };
}
