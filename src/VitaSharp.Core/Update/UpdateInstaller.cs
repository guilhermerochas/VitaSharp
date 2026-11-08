using System.Formats.Tar;
using System.IO.Compression;

namespace VitaSharp.Core.Update;

/// <summary>
/// Unpacks a downloaded package and copies it over the installed emulator. Only the files of the package are written
/// (nothing in the installation folder is deleted), and the user's data folder is never inside what is replaced.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Extracts a .zip/.app.zip/.tar.gz into <paramref name="stagingDir"/> (emptied first) and returns the
    /// package's root: its single top-level folder (VitaSharp/, VitaSharp.app/) when it has one.</summary>
    public static string Extract(string package, string stagingDir)
    {
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(stagingDir);
        if (package.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || package.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var gz = new GZipStream(File.OpenRead(package), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, stagingDir, overwriteFiles: true);
        }
        else ZipFile.ExtractToDirectory(package, stagingDir, overwriteFiles: true);

        var dirs = Directory.GetDirectories(stagingDir);
        return dirs.Length == 1 && Directory.GetFiles(stagingDir).Length == 0 ? dirs[0] : stagingDir;
    }

    /// <summary>
    /// Copies every file of <paramref name="source"/> into <paramref name="target"/>, overwriting. A file that is still
    /// in use (the old process exiting) is retried for up to <paramref name="lockTimeout"/>. Refuses a target that
    /// holds <paramref name="dataRoot"/>. Returns the number of files written.
    /// </summary>
    public static int Apply(string source, string target, string? dataRoot, TimeSpan lockTimeout, Action<string>? log = null)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        if (dataRoot != null && IsInside(Path.GetFullPath(dataRoot), target))
            throw new InvalidOperationException($"The data folder {dataRoot} is inside the installation {target}: not updating it");
        if (IsInside(target, source) || IsInside(source, target))
            throw new InvalidOperationException("The update and the installation are the same folder");

        int written = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            string dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var deadline = DateTime.UtcNow + lockTimeout;
            while (true)
            {
                try
                {
                    File.Copy(file, dest, overwrite: true);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dest, File.GetUnixFileMode(file));
                    break;
                }
                catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(250); }
                catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline) { Thread.Sleep(250); }
            }
            written++;
        }
        log?.Invoke($"Update: {written} files copied to {target}");
        return written;
    }

    static bool IsInside(string path, string folder)
    {
        string f = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(f, cmp);
    }
}
