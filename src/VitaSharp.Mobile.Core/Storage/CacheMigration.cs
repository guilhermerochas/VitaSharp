using System.Diagnostics;

namespace VitaSharp.Mobile.Core.Storage;

/// <summary>
/// Moves the rebuildable caches out of a data folder in shared storage into the internal folder
/// (<see cref="DataLayout.CachesInternal"/>): the shader cache and the code cache. A cache is copied next to its new
/// place, renamed into it in one step and only then removed from the old place, so a session never sees half of it:
/// until the rename it keeps using the old folder (<see cref="DataLayout.ShaderCache"/>), after it the new one. An old
/// folder left behind next to a new one (a session that was still writing the old one) is removed: the new one wins.
/// Runs in the background of the screens' process, never while a game runs.
/// </summary>
public static class CacheMigration
{
    /// <summary>Suffix of the copy being made, renamed to the cache's folder once complete.</summary>
    public const string PartialSuffix = ".moving";

    /// <summary>What one run did, for the log.</summary>
    public readonly record struct Result(int Moved, int RemovedStale, long Files, long Bytes, long Milliseconds, string Error);

    /// <summary>Moves every cache of <paramref name="layout"/> still in the data folder. Nothing to do (the caches are
    /// already in their place, or the data folder is the internal folder): an empty result.</summary>
    public static Result Run(DataLayout layout)
    {
        var watch = Stopwatch.StartNew();
        int moved = 0;
        int stale = 0;
        long files = 0;
        long bytes = 0;
        if (!layout.CachesInternal)
        {
            return default;
        }

        foreach ((string legacy, string target) in new[]
        {
            (layout.LegacyShaderCache, layout.InternalShaderCache),
            (layout.LegacyCodeCache, layout.InternalCodeCache),
        })
        {
            try
            {
                if (!Directory.Exists(legacy))
                {
                    continue;
                }

                if (Directory.Exists(target))
                {
                    Directory.Delete(legacy, recursive: true);
                    stale++;
                    continue;
                }

                string partial = target + PartialSuffix;
                if (Directory.Exists(partial))
                {
                    Directory.Delete(partial, recursive: true);
                }

                (long copiedFiles, long copiedBytes) = CopyTree(legacy, partial);
                Directory.Move(partial, target);
                Directory.Delete(legacy, recursive: true);
                moved++;
                files += copiedFiles;
                bytes += copiedBytes;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new Result(moved, stale, files, bytes, watch.ElapsedMilliseconds, $"{Path.GetFileName(legacy)}: {e.Message}");
            }
        }

        return new Result(moved, stale, files, bytes, watch.ElapsedMilliseconds, "");
    }

    /// <summary>One log line for <paramref name="result"/> (empty when nothing was there to move).</summary>
    public static string Describe(Result result, DataLayout layout)
    {
        if (result.Moved == 0 && result.RemovedStale == 0 && result.Error.Length == 0)
        {
            return "";
        }

        string text = $"[storage] caches moved to the internal folder {layout.CacheRoot}: {result.Moved} moved ({result.Files} files, " +
                      $"{result.Bytes / 1048576.0:F1} MB), {result.RemovedStale} old copies removed, in {result.Milliseconds} ms";
        return result.Error.Length == 0 ? text : $"{text}; stopped: {result.Error} (tried again at the next start)";
    }

    private static (long Files, long Bytes) CopyTree(string from, string to)
    {
        long files = 0;
        long bytes = 0;
        Directory.CreateDirectory(to);
        foreach (string directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(to, Path.GetRelativePath(from, file));
            File.Copy(file, destination, overwrite: true);
            files++;
            bytes += new FileInfo(destination).Length;
        }

        return (files, bytes);
    }
}
