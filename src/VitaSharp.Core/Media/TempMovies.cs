using VitaSharp.Core.Util;

namespace VitaSharp.Core.Media;

/// <summary>
/// Temp copies of movies a game serves through its own file callbacks (SceMp4, SceAvPlayer). They are deleted when the
/// game closes the movie, but a session that ends mid-movie (the window closed, a crash, a timed headless run) left
/// them behind: 161 files, 4.7 GB in %TEMP% on 07/11/2026. The first copy a process makes deletes the ones of
/// processes that are no longer running (the file name carries the process id).
/// </summary>
public static class TempMovies
{
    static int _swept;

    /// <summary>A new temp path "vitasharp-{kind}-{pid}-{suffix}.mp4".</summary>
    public static string NewPath(string kind, string suffix)
    {
        if (Interlocked.Exchange(ref _swept, 1) == 0) SweepStale();
        return Path.Combine(Path.GetTempPath(), $"vitasharp-{kind}-{Environment.ProcessId}-{suffix}.mp4");
    }

    /// <summary>Deletes the temp movies of VitaSharp processes that ended.</summary>
    public static int SweepStale()
    {
        int deleted = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "vitasharp-*-*-*.mp4"))
            {
                var parts = Path.GetFileNameWithoutExtension(file).Split('-');
                if (parts.Length < 4 || !int.TryParse(parts[2], out int pid) || Running(pid)) continue;
                try { File.Delete(file); deleted++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (deleted > 0) Log.Info($"Media: deleted {deleted} temp movie(s) left by earlier sessions");
        return deleted;
    }

    static bool Running(int pid)
    {
        if (pid == Environment.ProcessId) return true;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // cannot tell: keep it
    }
}
