using System.Text;
using System.Text.RegularExpressions;
using VitaSharp.Core.Hle;

namespace VitaSharp.Core.Capture;

/// <summary>
/// What a "Gravar tudo" capture must not carry when it is shared (a GitHub issue, docs/notes/session-capture.md):
/// the folders of the person who recorded it (their data folder, their home folder), their user and computer names,
/// their PSN / RPCN account names, MAC and e-mail addresses. Every text the capture writes (session.txt, log.txt,
/// report.txt, context.txt, threads, the GPU dump's header) goes through <see cref="Redact"/>, which puts stable
/// placeholders in their place: <c>&lt;data&gt;</c>, <c>&lt;user&gt;</c>, <c>&lt;pc&gt;</c>, <c>&lt;psn-id&gt;</c>,
/// <c>&lt;mac&gt;</c>, <c>&lt;email&gt;</c>. The analysis and the re-run only need paths inside the data folder,
/// which stay readable as <c>&lt;data&gt;/...</c>.
/// </summary>
public sealed partial class CapturePrivacy
{
    /// <summary>The placeholders <see cref="Redact"/> writes.</summary>
    public const string DataPlaceholder = "<data>", UserPlaceholder = "<user>", ComputerPlaceholder = "<pc>",
        AccountPlaceholder = "<psn-id>", MacPlaceholder = "<mac>", EmailPlaceholder = "<email>";

    /// <summary>Run options whose value names an account (their value becomes <see cref="AccountPlaceholder"/>).</summary>
    private static readonly string[] s_accountOptions = ["--psn-id=", "--rpcn-user=", "--rpcn-token="];

    private readonly (string Text, string Placeholder)[] _paths;
    private readonly (Regex Pattern, string Placeholder)[] _words;

    /// <param name="dataRoot">The data folder (&lt;data&gt;).</param>
    /// <param name="homeFolder">The user's home folder (&lt;user&gt;), when there is one.</param>
    /// <param name="userName">The user's login name.</param>
    /// <param name="computerName">The computer's name.</param>
    /// <param name="accountNames">Online ids and account names of the session (the default online id is no account).</param>
    public CapturePrivacy(string? dataRoot, string? homeFolder, string? userName, string? computerName, IEnumerable<string?>? accountNames = null)
    {
        var paths = new List<(string, string)>();
        AddPath(paths, dataRoot, DataPlaceholder);
        AddPath(paths, homeFolder, UserPlaceholder);
        // the longest first: the data folder usually sits inside the home folder
        paths.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        _paths = paths.ToArray();

        var words = new List<(Regex, string)>();
        foreach (string? account in accountNames ?? [])
        {
            AddWord(words, account, AccountPlaceholder);
        }

        AddWord(words, computerName, ComputerPlaceholder);
        AddWord(words, userName, UserPlaceholder);
        // the longest first: a computer is often named after its user ("ZELINDA-DESKTOP")
        words.Sort((a, b) => b.Item1.ToString().Length.CompareTo(a.Item1.ToString().Length));
        _words = words.ToArray();
    }

    /// <summary>The privacy of this computer and session: the data folder, the home folder, the user and computer
    /// names and the session's PSN account (unless it is the default online id, which names nobody).</summary>
    public static CapturePrivacy ForThisComputer(string? dataRoot, PsnAccount? account = null)
    {
        string? online = account?.OnlineId;
        if (online == PsnAccount.DefaultOnlineId)
        {
            online = null;
        }

        return new CapturePrivacy(dataRoot, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), SafeUserName(),
            SafeComputerName(), [online]);
    }

    private static string? SafeUserName()
    {
        try
        {
            return Environment.UserName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? SafeComputerName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void AddPath(List<(string, string)> paths, string? path, string placeholder)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 4)
        {
            return;
        }

        string full = Path.TrimEndingDirectorySeparator(path);
        // both separators: logs and options write either
        paths.Add((full, placeholder));
        paths.Add((full.Replace('\\', '/'), placeholder));
        paths.Add((full.Replace('/', '\\'), placeholder));
    }

    private static void AddWord(List<(Regex, string)> words, string? word, string placeholder)
    {
        // a name of one or two letters would match inside ordinary words
        if (string.IsNullOrWhiteSpace(word) || word.Trim().Length < 3)
        {
            return;
        }

        words.Add((new Regex($@"(?<![A-Za-z0-9_]){Regex.Escape(word.Trim())}(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), placeholder));
    }

    /// <summary>The text with every personal item replaced by its placeholder.</summary>
    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var sb = new StringBuilder(text);
        foreach ((string path, string placeholder) in _paths)
        {
            ReplaceIgnoreCase(sb, path, placeholder);
        }

        string s = sb.ToString();
        s = AccountOption().Replace(s, m => m.Groups[1].Value + AccountPlaceholder);
        // whole addresses before the names: an e-mail address often holds the user's name
        s = EmailAddress().Replace(s, EmailPlaceholder);
        s = MacAddress().Replace(s, MacPlaceholder);
        foreach ((Regex pattern, string placeholder) in _words)
        {
            s = pattern.Replace(s, placeholder);
        }

        return s;
    }

    /// <summary>The run options without personal items (session.txt "options:", reused by the re-run).</summary>
    public string[] RedactOptions(IEnumerable<string> options)
    {
        var result = new List<string>();
        foreach (string option in options)
        {
            string? account = s_accountOptions.FirstOrDefault(o => option.StartsWith(o, StringComparison.Ordinal));
            result.Add(account != null ? account + AccountPlaceholder : Redact(option));
        }

        return result.ToArray();
    }

    /// <summary>Rewrites every text file of a capture folder (and its sub-folders) through <see cref="Redact"/>.</summary>
    public void RedactFolder(string dir)
    {
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            // the HLE call trace holds guest values only (and can be large): nothing of the host is in it
            if (!IsTextFile(file) || Path.GetFileName(file) == HleCallTrace.FileName)
            {
                continue;
            }

            string text = File.ReadAllText(file);
            string clean = Redact(text);
            if (!string.Equals(text, clean, StringComparison.Ordinal))
            {
                File.WriteAllText(file, clean);
            }
        }
    }

    /// <summary>The capture's text files: what <see cref="RedactFolder"/> rewrites (the binary files hold no text of
    /// the host: pictures, sound, the GPU dump's commands; its header is redacted when it is made).</summary>
    public static bool IsTextFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".tsv" or ".log" or ".csv" or ".json" or ".md";

    private static void ReplaceIgnoreCase(StringBuilder sb, string find, string replacement)
    {
        string s = sb.ToString();
        int at = s.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return;
        }

        var result = new StringBuilder(s.Length);
        int from = 0;
        while (at >= 0)
        {
            result.Append(s, from, at - from);
            result.Append(replacement);
            from = at + find.Length;
            at = s.IndexOf(find, from, StringComparison.OrdinalIgnoreCase);
        }

        result.Append(s, from, s.Length - from);
        sb.Clear();
        sb.Append(result);
    }

    [GeneratedRegex(@"(--(?:psn-id|rpcn-user|rpcn-token)=)[^\s""]+", RegexOptions.CultureInvariant)]
    private static partial Regex AccountOption();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:-])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:-])", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddress();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddress();
}
