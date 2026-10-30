using VitaSharp.Core.Util;

namespace VitaSharp.Android.Logging;

/// <summary>
/// The file log in &lt;data&gt;/logs, next to logcat (which the system rotates away): the current run writes
/// last.log; the previous one is kept as &lt;yyyyMMdd_HHmmss&gt;_&lt;tag&gt;.log, and the newest <see cref="Keep"/> of those
/// stay. The app opens it at start ("app"); a game session reopens it with the title id, so a bug report can attach
/// the log of the run that failed.
/// </summary>
public static class SessionLog
{
    public const int Keep = 10;
    const string Header = "VitaSharp Android session ";

    public static string Open(string dir, string tag)
    {
        string last = Path.Combine(dir, "last.log");
        Directory.CreateDirectory(dir);
        Log.Flush();
        string? previous = Log.FilePath;
        try
        {
            if (File.Exists(last) && new FileInfo(last).Length > 0)
            {
                string stamp = File.GetLastWriteTime(last).ToString("yyyyMMdd_HHmmss");
                string oldTag = ReadTag(last);
                string dest = Path.Combine(dir, $"{stamp}_{oldTag}.log");
                for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, $"{stamp}_{oldTag}_{i}.log");
                // the file may be the one Log still has open (the app's own run): copy it, Log.OpenFile truncates it
                using (var src = new FileStream(last, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = File.Create(dest))
                    src.CopyTo(dst);
            }
            Log.OpenFile(last);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            last = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Clean(tag)}.log");
            Log.OpenFile(last);
        }
        Log.Info($"{Header}{Clean(tag)} {DateTime.Now:yyyyMMdd_HHmmss}" + (previous != null ? $" (previous log: {Path.GetFileName(previous)})" : ""));
        Prune(dir);
        return last;
    }

    static string ReadTag(string path)
    {
        try
        {
            using var r = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            for (int i = 0; i < 5 && r.ReadLine() is { } line; i++)
            {
                int k = line.IndexOf(Header, StringComparison.Ordinal);
                if (k >= 0) return Clean(line[(k + Header.Length)..].Split(' ')[0]);
            }
        }
        catch (IOException) { }
        return "unknown";
    }

    static string Clean(string s)
    {
        var c = string.Concat(s.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'));
        return c.Length > 0 ? c : "unknown";
    }

    static void Prune(string dir)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*_*.log").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Skip(Keep))
                try { f.Delete(); } catch (IOException) { }
        }
        catch (IOException) { }
    }
}
