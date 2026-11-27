namespace VitaSharp.Core.Storage;

/// <summary>Copy the data to the new folder (the old one stays as it is) or move it (each file leaves the old folder
/// only after it is in the new one with its size checked).</summary>
public enum DataTransferMode
{
    Copy,
    Move,
}

/// <summary>How far a <see cref="DataFolderTransfer"/> is.</summary>
public readonly record struct DataTransferProgress(long FilesDone, long FilesTotal, long BytesDone, long BytesTotal);

/// <summary>How a <see cref="DataFolderTransfer.Run"/> ended.</summary>
public enum DataTransferStatus
{
    /// <summary>Every file is in the target (or was already there with the same content).</summary>
    Done,

    /// <summary>The target's disk has no room for the data (checked before anything is copied): nothing was changed.</summary>
    NoSpace,

    /// <summary>A file could not be copied; nothing that is not in the target was deleted. Running again resumes.</summary>
    Failed,

    /// <summary>The target and the source are the same folder or one is inside the other: nothing was done.</summary>
    InvalidTarget,
}

/// <summary>The end of a run; <paramref name="Kept"/> lists the files the target already had with other content (the
/// target's file was kept, the source's was left where it was: nothing is overwritten).</summary>
public readonly record struct DataTransferResult(DataTransferStatus Status, string Message, long Files, long Bytes, IReadOnlyList<string> Kept);

/// <summary>
/// Copies or moves a whole data folder to another one (the settings' "Pasta de dados", <see cref="DataFolder"/>),
/// keeping its layout, the way PPSSPP moves its memstick folder:
/// <list type="bullet">
/// <item>the free space of the target is checked first against the bytes still to copy (plus a margin); without room
/// nothing is touched;</item>
/// <item>nothing in the target is ever replaced: a file it already has with the same size and content counts as done
/// (a resumed run); one with other content keeps the target's and leaves the source's in place, listed in
/// <see cref="DataTransferResult.Kept"/>;</item>
/// <item>each file is copied to <c>name.part</c>, its size checked and renamed to its name; in
/// <see cref="DataTransferMode.Move"/> a whole top-level entry the target lacks is renamed at once when both folders
/// are on the same volume, otherwise the source file is deleted only after its copy is complete;</item>
/// <item>the source folder itself is never deleted (only its emptied subfolders in a move).</item>
/// </list>
/// </summary>
public class DataFolderTransfer(string source, string target, DataTransferMode mode)
{
    public const string PartSuffix = ".part";

    /// <summary>Room left free in the target beyond the data.</summary>
    public const long SpaceMargin = 64L << 20;

    public string Source { get; } = Path.GetFullPath(source);
    public string Target { get; } = Path.GetFullPath(target);
    public DataTransferMode Mode { get; } = mode;

    /// <summary>Files and bytes the source holds.</summary>
    public static (long Files, long Bytes) Measure(string folder)
    {
        long files = 0;
        long bytes = 0;
        if (!Directory.Exists(folder))
        {
            return (0, 0);
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            files++;
            bytes += new FileInfo(file).Length;
        }

        return (files, bytes);
    }

    public DataTransferResult Run(IProgress<DataTransferProgress>? progress = null, CancellationToken cancel = default)
    {
        List<string> kept = [];
        if (DataFolder.IsSameOrInside(Target, Source) || DataFolder.IsSameOrInside(Source, Target))
        {
            return new DataTransferResult(DataTransferStatus.InvalidTarget, "the new folder is the old one or inside it (or the other way round)", 0, 0, kept);
        }

        if (!Directory.Exists(Source))
        {
            Directory.CreateDirectory(Target);
            return new DataTransferResult(DataTransferStatus.Done, "", 0, 0, kept);
        }

        Directory.CreateDirectory(Target);
        if (Mode == DataTransferMode.Move)
        {
            RenameTopLevelEntries();
        }

        List<(string File, long Size)> files = [];
        long totalBytes = 0;
        long toCopy = 0;
        foreach (string file in Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories))
        {
            long size = new FileInfo(file).Length;
            files.Add((file, size));
            totalBytes += size;
            if (!File.Exists(TargetPath(file)))
            {
                toCopy += size;
            }
        }

        long free = FreeBytes(Target);
        if (free < toCopy + SpaceMargin)
        {
            return new DataTransferResult(DataTransferStatus.NoSpace,
                $"the new folder's disk has {free >> 20} MiB free; the data needs {toCopy >> 20} MiB (+{SpaceMargin >> 20} MiB margin)", 0, 0, kept);
        }

        long doneFiles = 0;
        long doneBytes = 0;
        progress?.Report(new DataTransferProgress(0, files.Count, 0, totalBytes));
        foreach ((string sourceFile, long size) in files)
        {
            cancel.ThrowIfCancellationRequested();
            string targetFile = TargetPath(sourceFile);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                if (File.Exists(targetFile))
                {
                    if (new FileInfo(targetFile).Length == size && SameContent(sourceFile, targetFile))
                    {
                        if (Mode == DataTransferMode.Move)
                        {
                            File.Delete(sourceFile);
                        }
                    }
                    else
                    {
                        kept.Add(Relative(sourceFile));
                    }
                }
                else
                {
                    string part = targetFile + PartSuffix;
                    CopyFile(sourceFile, part);
                    long copied = new FileInfo(part).Length;
                    if (copied != size)
                    {
                        File.Delete(part);
                        return new DataTransferResult(DataTransferStatus.Failed, $"{Relative(sourceFile)}: copied {copied} of {size} bytes", doneFiles, doneBytes, kept);
                    }

                    File.Move(part, targetFile, false);
                    if (Mode == DataTransferMode.Move)
                    {
                        File.Delete(sourceFile);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new DataTransferResult(DataTransferStatus.Failed, $"{Relative(sourceFile)}: {e.Message}", doneFiles, doneBytes, kept);
            }

            doneFiles++;
            doneBytes += size;
            progress?.Report(new DataTransferProgress(doneFiles, files.Count, doneBytes, totalBytes));
        }

        // empty folders exist in the target too (a save folder the game made but has not written yet)
        foreach (string folder in Directory.EnumerateDirectories(Source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(TargetPath(folder));
        }

        if (Mode == DataTransferMode.Move)
        {
            DeleteEmptyFolders(Source);
        }

        return new DataTransferResult(DataTransferStatus.Done, kept.Count > 0 ? $"{kept.Count} files kept in the old folder: the new one already had them with other content" : "",
            doneFiles, doneBytes, kept);
    }

    /// <summary>Copies <paramref name="from"/> to <paramref name="to"/> (the .part file) and flushes it to the disk.</summary>
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
            return (long)Math.Min(Util.HostDisk.Of(path).Free, long.MaxValue);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>Renames a file or folder (false when the system cannot, e.g. between two volumes).</summary>
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void RenameTopLevelEntries()
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(Source).ToList())
        {
            string destination = Path.Combine(Target, Path.GetFileName(entry));
            if (Path.Exists(destination))
            {
                continue;
            }

            TryRename(entry, destination, Directory.Exists(entry));
        }
    }

    private static void DeleteEmptyFolders(string root)
    {
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            DeleteEmptyFolders(folder);
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
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

    private string TargetPath(string sourcePath) => Path.Combine(Target, Path.GetRelativePath(Source, sourcePath));

    private string Relative(string sourceFile) => Path.GetRelativePath(Source, sourceFile).Replace(Path.DirectorySeparatorChar, '/');
}
