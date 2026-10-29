using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using VitaSharp.Core.Hle.Np;

namespace VitaSharp.Desktop.Launcher;

/// <summary>One trophy set of a game as the launcher shows it: the set from the game's TROPHY.TRP plus the unlocks the
/// emulator recorded (ux0:user/00/trophy/&lt;NPCOMMID&gt;.json).</summary>
public sealed record TrophySetView(TrophyPackage Package, Dictionary<int, ulong> Unlocked)
{
    public int UnlockedCount => Package.Trophies.Count(t => Unlocked.ContainsKey(t.Id));
    public int Percent => Package.Trophies.Count == 0 ? 0 : UnlockedCount * 100 / Package.Trophies.Count;

    /// <summary>The trophy sets of an installed game (texts in <paramref name="language"/> when the set has them).</summary>
    public static List<TrophySetView> ForGame(string appDir, string fsRoot, int language)
    {
        var list = new List<TrophySetView>();
        foreach (var (commId, trp) in TrophyPackage.FindSets(appDir))
            if (TrophyPackage.Load(trp, commId, language) is { } pkg)
                list.Add(new TrophySetView(pkg, TrophyUnlocks.Load(TrophyUnlocks.PathFor(fsRoot, commId))));
        return list;
    }
}

/// <summary>Per-game trophy list: icon, name, description, grade and unlock date of every trophy. Hidden trophies keep
/// their text secret until unlocked, as on the console.</summary>
public sealed class TrophyWindow : Window
{
    static readonly IBrush Muted = LauncherTheme.Muted;
    static readonly IBrush Panel = LauncherTheme.Panel;

    public static string GradeName(TrophyGrade g) => g switch
    {
        TrophyGrade.Platinum => "Platinum", TrophyGrade.Gold => "Gold", TrophyGrade.Silver => "Silver", _ => "Bronze",
    };

    public static IBrush GradeBrush(TrophyGrade g) => new SolidColorBrush(Color.Parse(g switch
    {
        TrophyGrade.Platinum => "#A9C4E8", TrophyGrade.Gold => "#E2B13C", TrophyGrade.Silver => "#C3C7CE", _ => "#C07A44",
    }));

    /// <summary>SceSystemParamLang of the PC's UI language (TROP_&lt;nn&gt;.SFM): 0 ja, 1 en-US, 2 fr, 3 es, 4 de, 5 it, 6 nl,
    /// 7 pt-PT, 8 ru, 9 ko, 10 zh-Hant, 11 zh-Hans, 12 fi, 13 sv, 14 da, 15 no, 16 pl, 17 pt-BR, 18 en-GB, 19 tr.</summary>
    public static int VitaLanguage(CultureInfo c) => c.Name switch
    {
        "pt-BR" => 17, "en-GB" => 18, "zh-TW" or "zh-HK" or "zh-Hant" => 10, "zh-CN" or "zh-SG" or "zh-Hans" => 11,
        _ => c.TwoLetterISOLanguageName switch
        {
            "ja" => 0, "en" => 1, "fr" => 2, "es" => 3, "de" => 4, "it" => 5, "nl" => 6, "pt" => 7, "ru" => 8, "ko" => 9,
            "fi" => 12, "sv" => 13, "da" => 14, "nb" or "nn" or "no" => 15, "pl" => 16, "tr" => 19, _ => 1,
        },
    };

    public TrophyWindow(GameEntry game, IReadOnlyList<TrophySetView> sets)
    {
        Title = $"Trophies — {game.Title}";
        Width = 760;
        Height = 720;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = LauncherTheme.WindowBackground;

        var content = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        if (sets.Count == 0)
            content.Children.Add(new TextBlock { Text = "This game has no trophies.", Foreground = Muted });
        foreach (var set in sets)
        {
            content.Children.Add(SetHeader(set));
            foreach (var t in set.Package.Trophies.OrderBy(t => t.Grade == TrophyGrade.Platinum ? -1 : t.Id))
                content.Children.Add(TrophyRow(set, t));
        }
        Content = new ScrollViewer { Content = content };
    }

    static Bitmap? Png(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try { return new Bitmap(new MemoryStream(bytes)); }
        catch (Exception) { return null; }
    }

    static Control IconBox(byte[]? png, double size, bool dim) => new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = Panel,
        Opacity = dim ? 0.35 : 1,
        Child = Png(png) is { } bmp ? new Image { Source = bmp, Stretch = Stretch.UniformToFill } : null,
    };

    static Control SetHeader(TrophySetView set)
    {
        var p = set.Package;
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = p.Title.Length > 0 ? p.Title : p.CommId, FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = $"{set.UnlockedCount} of {p.Trophies.Count} unlocked ({set.Percent}%)   ·   {p.CommId}", Foreground = Muted });
        var grades = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var g in new[] { TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze })
        {
            int total = p.Trophies.Count(t => t.Grade == g);
            if (total == 0) continue;
            int got = p.Trophies.Count(t => t.Grade == g && set.Unlocked.ContainsKey(t.Id));
            grades.Children.Add(new TextBlock { Text = $"● {GradeName(g)} {got}/{total}", Foreground = GradeBrush(g) });
        }
        text.Children.Add(grades);
        text.Children.Add(new ProgressBar { Value = set.Percent, Maximum = 100, Height = 6, MinWidth = 200, Margin = new Thickness(0, 4, 0, 0) });
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 8) };
        var icon = IconBox(p.GameIcon, 96, false);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }

    static Control TrophyRow(TrophySetView set, TrophyInfo t)
    {
        bool unlocked = set.Unlocked.TryGetValue(t.Id, out ulong tick);
        bool secret = t.Hidden && !unlocked;
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = secret ? "Hidden trophy" : t.Name, FontSize = 15, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, Foreground = unlocked ? Brushes.White : Muted,
        });
        text.Children.Add(new TextBlock
        {
            Text = secret ? "Keep playing to reveal this trophy." : t.Detail, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
        });
        var side = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0), MinWidth = 130 };
        side.Children.Add(new TextBlock { Text = GradeName(t.Grade), Foreground = GradeBrush(t.Grade), FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right });
        side.Children.Add(new TextBlock
        {
            Text = unlocked ? TrophyUnlocks.TickToUtc(tick).ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "Locked",
            Foreground = Muted, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right,
        });
        var row = new DockPanel();
        var icon = IconBox(secret ? null : set.Package.TrophyIcon(t.Id), 56, !unlocked);
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(side, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(side);
        row.Children.Add(text);
        return new Border { Background = Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = row };
    }
}
