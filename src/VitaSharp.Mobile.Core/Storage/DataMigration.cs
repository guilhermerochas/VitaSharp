using System.Text;

namespace VitaSharp.Mobile.Core.Storage;

/// <summary>How far a <see cref="DataMigration"/> is (files and bytes moved of the inventory).</summary>
public readonly record struct MigrationProgress(long FilesDone, long FilesTotal, long BytesDone, long BytesTotal);

/// <summary>How a <see cref="DataMigration.Run"/> ended.</summary>
public enum MigrationStatus
{
    /// <summary>Everything is in the target, checked (count and size), and the source folder is gone.</summary>
    Done,

    /// <summary>A file could not be moved or copied; nothing was deleted that is not in the target. Run again to resume.</summary>
    Failed,

    /// <summary>The target has no room for the next file; nothing was deleted that is not in the target.</summary>
    NoSpace,

    /// <summary>Everything the source still had was moved, but the check found a listed file missing or of another size
    /// in the target (lost before the move); the list is in <see cref="DataMigration.ProblemsFileName"/>.</summary>
    VerifyFailed,
}

/// <summary>The end of a <see cref="DataMigration.Run"/>; <paramref name="Message"/> says what failed.</summary>
public readonly record struct MigrationResult(MigrationStatus Status, string Message, long Files, long Bytes);

/// <summary>
/// Moves the whole data folder (fs with games and firmware, saves, save states, captures, logs, config/env.txt...) from
/// the app-specific folder to the visible one (<see cref="DataRoot"/>), keeping the same layout. It never loses data:
/// <list type="number">
/// <item>the source is listed first into a journal in the target (<see cref="JournalFileName"/>), merged with the
/// journal of an interrupted run, so the check at the end covers every file the source had;</item>
/// <item>each top-level entry the target does not have yet is renamed at once (atomic, when both folders are on the same
/// mount); otherwise each file is renamed, or copied to <c>name.part</c>, its size checked, renamed to its name, and only
/// then deleted from the source; a copy error or a full disk stops the run with the source file in place;</item>
/// <item>a file the target already has is never replaced: the same content drops the source copy (a resumed run);
/// other content (both folders were used, e.g. after a reinstall) keeps the target's in place and puts the source's
/// beside it as <c>name.from-app-folder</c> (<see cref="KeptSuffix"/>), so a merge overwrites nothing;</item>
/// <item>at the end every journal entry must be in the target with the size it had when it moved, under its name or
/// beside it (count and bytes);
/// only the empty source folders are deleted then. A listed file that is in neither folder is reported in
/// <see cref="ProblemsFileName"/> (there is nothing left to move).</item>
/// </list>
/// A run stopped anywhere (app killed, phone off, error) resumes from where it was on the next one
/// (<see cref="IsPending"/>).
/// </summary>
public class DataMigration(string source, string target)
{
    public const string JournalFileName = ".migration-journal";
    public const string ProblemsFileName = "migration-problems.txt";
    private const string MovedTag = "m";
    public const string PartSuffix = ".part";
    /// <summary>The source's copy of a file the target already had with other content, next to the target's.</summary>
    public const string KeptSuffix = ".from-app-folder";

    /// <summary>Room left free in the target beyond the file being copied.</summary>
    public const long SpaceMargin = 16L << 20;

    public string Source { get; } = Path.GetFullPath(source);
    public string Target { get; } = Path.GetFullPath(target);

    private string JournalPath => Path.Combine(Target, JournalFileName);

    /// <summary>The source still has something, or a run was interrupted (its journal is in the target).</summary>
    public static bool IsPending(string source, string target)
    {
        if (File.Exists(Path.Combine(target, JournalFileName)))
        {
            return true;
        }

        return Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any();
    }

    /// <summary>A run stopped before it ended (its journal is still in the target): the next one resumes it.</summary>
    public static bool IsInterrupted(string target)
    {
        return File.Exists(Path.Combine(target, JournalFileName));
    }

    public MigrationResult Run(IProgress<MigrationProgress>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(Target);
        HashSet<string> folders = new(StringComparer.Ordinal);
        Dictionary<string, long> files = LoadJournal(folders);
        if (Directory.Exists(Source))
        {
            Inventory(Source, "", files, folders);
        }

        SaveJournal(files, folders);
        long totalBytes = files.Values.Sum();
        if (Directory.Exists(Source))
        {
            RenameTopLevelEntries();
        }

        (long doneFiles, long doneBytes) = CountMoved(files);
        progress?.Report(new MigrationProgress(doneFiles, files.Count, doneBytes, totalBytes));
        if (Directory.Exists(Source))
        {
            // one "m<TAB>size<TAB>path" line per moved file: its size when it moved (a file changed after the
            // listing is checked against that), kept for a resumed run
            using StreamWriter moves = new(JournalPath, append: true);
            foreach (string sourceFile in Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories).ToList())
            {
                cancel.ThrowIfCancellationRequested();
                string relative = Relative(sourceFile);
                long size = new FileInfo(sourceFile).Length;
                MigrationResult? stop = MoveOne(sourceFile, TargetPath(relative), size, out string landed);
                if (stop is { } failed)
                {
                    return failed with { Files = doneFiles, Bytes = doneBytes };
                }

                totalBytes += size - files.GetValueOrDefault(relative);
                string landedRelative = Path.GetRelativePath(Target, landed).Replace(Path.DirectorySeparatorChar, '/');
                if (landedRelative != relative)
                {
                    // the target kept its own file under this name: the check looks for the source's beside it
                    files.Remove(relative);
                }

                files[landedRelative] = size;
                moves.Write($"{MovedTag}\t{size}\t{landedRelative}\n");
                moves.Flush();
                doneFiles++;
                doneBytes += size;
                progress?.Report(new MigrationProgress(doneFiles, files.Count, doneBytes, totalBytes));
            }
        }

        foreach (string folder in folders)
        {
            Directory.CreateDirectory(TargetPath(folder));
        }

        if (Directory.Exists(Source) && Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories).Any())
        {
            return new MigrationResult(MigrationStatus.Failed, "files appeared in the source during the move: run again",
                doneFiles, doneBytes);
        }

        // the check: every listed file in the target with its size (count and bytes)
        var problems = new List<string>();
        long verifiedFiles = 0;
        long verifiedBytes = 0;
        foreach (KeyValuePair<string, long> file in files)
        {
            string moved = TargetPath(file.Key);
            if (!File.Exists(moved))
            {
                problems.Add($"missing in the target: {file.Key}");
                continue;
            }

            long size = new FileInfo(moved).Length;
            if (size != file.Value && !KeptBeside(moved, file.Value))
            {
                problems.Add($"{file.Key}: {size} bytes in the target, {file.Value} listed");
                continue;
            }

            verifiedFiles++;
            verifiedBytes += file.Value;
        }

        // only empty folders are left in the source here: deleting them loses nothing
        if (Directory.Exists(Source))
        {
            Directory.Delete(Source, true);
        }

        if (problems.Count > 0)
        {
            // nothing more can be done (the source has no file left): the list stays for the user and the visible
            // folder is used from now on
            File.WriteAllLines(Path.Combine(Target, ProblemsFileName), problems);
            File.Delete(JournalPath);
            return new MigrationResult(MigrationStatus.VerifyFailed, string.Join("; ", problems.Take(5)), verifiedFiles, verifiedBytes);
        }

        File.Delete(JournalPath);
        progress?.Report(new MigrationProgress(files.Count, files.Count, totalBytes, totalBytes));
        return new MigrationResult(MigrationStatus.Done, "", verifiedFiles, verifiedBytes);
    }

    /// <summary>Renames a file or folder (false when the system cannot, e.g. EXDEV between two mounts).</summary>
    protected virtual bool TryRename(string from, string to, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to, false);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Copies <paramref name="from"/> over <paramref name="to"/> (the .part file) and flushes it to the disk.</summary>
    protected virtual void CopyFile(string from, string to)
    {
        using FileStream input = new(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        using FileStream output = new(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        input.CopyTo(output, 1 << 20);
        output.Flush(true);
    }

    /// <summary>Free bytes on the target's disk.</summary>
    protected virtual long FreeBytes(string path)
    {
        try
        {
            // the file system that holds the target, not the root of its path (Android: "/" is full and read-only)
            return (long)Math.Min(VitaSharp.Core.Util.HostDisk.Of(path).Free, long.MaxValue);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>Moves one file; null when it is in the target (at <paramref name="landed"/>: its name, or beside the
    /// target's own file of that name), else why the run stops (the source file is kept).</summary>
    private MigrationResult? MoveOne(string sourceFile, string targetFile, long size, out string landed)
    {
        landed = targetFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            if (File.Exists(targetFile))
            {
                if (new FileInfo(targetFile).Length == size && SameContent(sourceFile, targetFile))
                {
                    File.Delete(sourceFile);
                    return null;
                }

                // the target's file stays where it is (it may be the user's save or settings): the source's goes beside it
                targetFile = FreeName(targetFile + KeptSuffix);
                landed = targetFile;
            }

            if (TryRename(sourceFile, targetFile, false))
            {
                return null;
            }

            if (FreeBytes(Target) < size + SpaceMargin)
            {
                return new MigrationResult(MigrationStatus.NoSpace, $"no room for {Relative(sourceFile)} ({size} bytes)", 0, 0);
            }

            string part = targetFile + PartSuffix;
            CopyFile(sourceFile, part);
            long copied = new FileInfo(part).Length;
            if (copied != size)
            {
                File.Delete(part);
                return new MigrationResult(MigrationStatus.Failed, $"{Relative(sourceFile)}: copied {copied} of {size} bytes", 0, 0);
            }

            File.Move(part, targetFile, false);
            File.Delete(sourceFile);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new MigrationResult(MigrationStatus.Failed, $"{Relative(sourceFile)}: {e.Message}", 0, 0);
        }
    }

    /// <summary>Whole top-level folders and files the target does not have yet, in one rename each.</summary>
    private void RenameTopLevelEntries()
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(Source).ToList())
        {
            string name = Path.GetFileName(entry);
            string destination = Path.Combine(Target, name);
            if (name == JournalFileName || Path.Exists(destination))
            {
                continue;
            }

            TryRename(entry, destination, Directory.Exists(entry));
        }
    }

    /// <summary>Journal files already out of the source and in the target.</summary>
    private (long Files, long Bytes) CountMoved(Dictionary<string, long> files)
    {
        long count = 0;
        long bytes = 0;
        foreach (KeyValuePair<string, long> file in files)
        {
            if (!File.Exists(SourcePath(file.Key)) && File.Exists(TargetPath(file.Key)))
            {
                count++;
                bytes += file.Value;
            }
        }

        return (count, bytes);
    }

    private static bool SameContent(string first, string second)
    {
        using FileStream a = new(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using FileStream b = new(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        byte[] bufferA = new byte[1 << 16];
        byte[] bufferB = new byte[1 << 16];
        while (true)
        {
            int readA = a.ReadAtLeast(bufferA, bufferA.Length, false);
            int readB = b.ReadAtLeast(bufferB, bufferB.Length, false);
            if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
            {
                return false;
            }

            if (readA == 0)
            {
                return true;
            }
        }
    }

    /// <summary>The source's copy of <paramref name="targetFile"/> with <paramref name="size"/> bytes is beside it
    /// (<see cref="KeptSuffix"/>: a run stopped before its journal line was written).</summary>
    private static bool KeptBeside(string targetFile, long size)
    {
        string candidate = targetFile + KeptSuffix;
        int n = 1;
        while (File.Exists(candidate))
        {
            if (new FileInfo(candidate).Length == size)
            {
                return true;
            }

            candidate = $"{targetFile}{KeptSuffix}.{n++}";
        }

        return false;
    }

    private static string FreeName(string path)
    {
        string candidate = path;
        int n = 1;
        while (Path.Exists(candidate))
        {
            candidate = $"{path}.{n++}";
        }

        return candidate;
    }

    private void Inventory(string folder, string prefix, Dictionary<string, long> files, HashSet<string> folders)
    {
        foreach (string sub in Directory.EnumerateDirectories(folder))
        {
            string relative = prefix + Path.GetFileName(sub);
            folders.Add(relative);
            Inventory(sub, relative + "/", files, folders);
        }

        foreach (string file in Directory.EnumerateFiles(folder))
        {
            string relative = prefix + Path.GetFileName(file);
            if (prefix.Length == 0 && relative == JournalFileName)
            {
                continue;
            }

            files[relative] = new FileInfo(file).Length;
        }
    }

    private Dictionary<string, long> LoadJournal(HashSet<string> folders)
    {
        Dictionary<string, long> files = new(StringComparer.Ordinal);
        if (!File.Exists(JournalPath))
        {
            return files;
        }

        foreach (string line in File.ReadAllLines(JournalPath))
        {
            int tab = line.IndexOf('\t');
            if (line.StartsWith(MovedTag + "\t", StringComparison.Ordinal))
            {
                string rest = line[(MovedTag.Length + 1)..];
                int split = rest.IndexOf('\t');
                if (split > 0 && long.TryParse(rest.AsSpan(0, split), out long movedSize))
                {
                    files[rest[(split + 1)..]] = movedSize;
                }
            }
            else if (tab > 0 && line.AsSpan(0, tab).SequenceEqual("dir"))
            {
                folders.Add(line[(tab + 1)..]);
            }
            else if (tab > 0 && long.TryParse(line.AsSpan(0, tab), out long size))
            {
                files[line[(tab + 1)..]] = size;
            }
        }

        return files;
    }

    private void SaveJournal(Dictionary<string, long> files, HashSet<string> folders)
    {
        var text = new StringBuilder();
        text.Append("# VitaSharp data move: ").Append(Source).Append('\n');
        foreach (string folder in folders)
        {
            text.Append("dir\t").Append(folder).Append('\n');
        }

        foreach (KeyValuePair<string, long> file in files)
        {
            text.Append(file.Value).Append('\t').Append(file.Key).Append('\n');
        }

        string temporary = JournalPath + PartSuffix;
        File.WriteAllText(temporary, text.ToString());
        File.Move(temporary, JournalPath, true);
    }

    private string SourcePath(string relative) => Path.Combine(Source, relative.Replace('/', Path.DirectorySeparatorChar));

    private string TargetPath(string relative) => Path.Combine(Target, relative.Replace('/', Path.DirectorySeparatorChar));

    private string Relative(string sourceFile) => Path.GetRelativePath(Source, sourceFile).Replace(Path.DirectorySeparatorChar, '/');
}
