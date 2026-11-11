using System.Globalization;
using System.Resources;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Localization;

/// <summary>
/// The interface texts of the launcher, the game window and the Android app, in English (<c>Texts.resx</c>, also the
/// fallback for a missing key) and Brazilian Portuguese (<c>Texts.pt-BR.resx</c>). The language follows the system
/// unless the user picks one (<see cref="EnvVars.Language"/>, set by the launcher for the games it starts). Logs stay
/// in English.
/// </summary>
public static class Loc
{
    public const string System = "system", English = "en", Portuguese = "pt-BR";

    private static readonly ResourceManager s_resources = new("VitaSharp.Core.Localization.Texts", typeof(Loc).Assembly);

    /// <summary>The culture of the texts (also used to format numbers and dates in them).</summary>
    public static CultureInfo Culture { get; private set; } = Pick(Environment.GetEnvironmentVariable(EnvVars.Language));

    /// <summary>"system", "en" or "pt-BR" (anything else follows the system); also passed to the processes started
    /// from here.</summary>
    public static void SetLanguage(string? language)
    {
        Culture = Pick(language);
        Environment.SetEnvironmentVariable(EnvVars.Language, string.IsNullOrEmpty(language) || language == System ? null : language);
    }

    /// <summary>pt-BR for a Portuguese system (or choice), English otherwise.</summary>
    public static CultureInfo Pick(string? language)
    {
        string name = string.IsNullOrEmpty(language) || language == System ? CultureInfo.CurrentUICulture.Name : language;
        return name.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? CultureInfo.GetCultureInfo(Portuguese) : CultureInfo.GetCultureInfo(English);
    }

    public static bool IsPortuguese => Culture.Name == Portuguese;

    /// <summary>The text of <paramref name="key"/> in the current language (English when it has none; the key itself
    /// when no language has it, which the tests catch).</summary>
    public static string Get(string key) => s_resources.GetString(key, Culture) ?? key;

    /// <summary>The format text of <paramref name="key"/> with <paramref name="args"/>.</summary>
    public static string Format(string key, params object?[] args) => string.Format(Culture, Get(key), args);

    /// <summary>Every key and text of one language file only (no fallback): tests.</summary>
    public static IReadOnlyDictionary<string, string> Table(CultureInfo culture)
    {
        ResourceSet? set = s_resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        var table = new Dictionary<string, string>();
        if (set == null)
        {
            return table;
        }

        foreach (System.Collections.DictionaryEntry e in set)
        {
            if (e.Value is string s)
            {
                table[(string)e.Key] = s;
            }
        }

        return table;
    }
}
