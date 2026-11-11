using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Globalization;
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
        {
            if (TrophyPackage.Load(trp, commId, language) is { } pkg)
            {
                list.Add(new TrophySetView(pkg, TrophyUnlocks.Load(TrophyUnlocks.PathFor(fsRoot, commId))));
            }
        }

        return list;
    }

    /// <summary>SceSystemParamLang of the PC's UI language (TROP_&lt;nn&gt;.SFM).</summary>
    public static int VitaLanguage(CultureInfo c) => VitaSharp.Core.Firmware.SystemLanguage.FromCulture(c);
}
