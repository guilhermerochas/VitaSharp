namespace VitaSharp.Android.Settings;

/// <summary>
/// <c>&lt;data&gt;/config/env.txt</c>: the VITASHARP_* environment variables of the desktop (GPU quirks, ASTC, native
/// verifier, logging switches...) for the Android app, where a process cannot be started with an environment. One
/// <c>NAME=value</c> per line, <c>#</c> comments; only names starting with <c>VITASHARP_</c> are taken. Each process applies
/// it at startup, before any emulator type reads its switches; push it with <c>adb push env.txt
/// /sdcard/Android/data/io.github.vitasharp/files/config/</c> (docs/android/README.md, "Logging").
/// </summary>
public static class EnvironmentFile
{
    public const string FileName = "env.txt";
    public const string Prefix = "VITASHARP_";

    /// <summary>The variables of the file's text, in order (a later line wins when applied).</summary>
    public static List<KeyValuePair<string, string>> Parse(string text)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string name = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'') value = value[1..^1];
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Any(char.IsWhiteSpace)) continue;
            list.Add(new(name, value));
        }
        return list;
    }

    /// <summary>Sets the file's variables in this process; returns them (for the log). A missing file sets nothing.</summary>
    public static List<KeyValuePair<string, string>> Apply(string path)
    {
        if (!File.Exists(path)) return [];
        var vars = Parse(File.ReadAllText(path));
        foreach (var (name, value) in vars) Environment.SetEnvironmentVariable(name, value.Length > 0 ? value : null);
        return vars;
    }
}
