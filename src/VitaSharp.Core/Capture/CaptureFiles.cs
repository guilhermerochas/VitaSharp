using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VitaSharp.Core.Io;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Capture;

/// <summary>A SHA-256 as four words (a dictionary key without a string).</summary>
public readonly record struct ChunkHash(ulong A, ulong B, ulong C, ulong D)
{
    public static ChunkHash Of(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return new ChunkHash(BinaryPrimitives.ReadUInt64BigEndian(hash), BinaryPrimitives.ReadUInt64BigEndian(hash[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(hash[16..]), BinaryPrimitives.ReadUInt64BigEndian(hash[24..]));
    }

    public override string ToString() => $"{A:x16}{B:x16}{C:x16}{D:x16}";

    public static ChunkHash Parse(string hex) => new(ulong.Parse(hex[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        ulong.Parse(hex[16..32], NumberStyles.HexNumber, CultureInfo.InvariantCulture), ulong.Parse(hex[32..48], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        ulong.Parse(hex[48..64], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}

/// <summary>
/// The files of the title's own mounts (app0, savedata0, addcont0, a PFS or game data mount: <see cref="Vfs.IsTitleDevice"/>)
/// the guest touched while a capture records: each file opened or whose status was read (its size), each folder
/// listed (its entries and their sizes), each read (path, offset, length, SHA-256 and, within
/// <see cref="MaxBytes"/>, the bytes, kept once per distinct content) and the decrypted image of each module loaded
/// from them. A re-run without the game then reads the same bytes (<see cref="CapturedFiles"/>) instead of "not found".
/// Only what was read leaves with the capture, never a whole file that was not read; firmware files (os0, vs0) are the
/// analyst's own and are not recorded; the save data is the player's, so its reads keep their hash only unless
/// <see cref="KeepSaveData"/>. Paths are the guest's (app0:...), never the host's.
/// <para>A firmware module (os0, vs0, sa0, pd0) loaded while recording is listed with its hash only (kind
/// <c>fwmodule</c>): a re-run loads it from the analyst's firmware and says when that one differs.</para>
/// <para>Files: <c>files/files.tsv</c> (one line per item) and <c>files/blobs.bin</c> (the kept contents).</para>
/// </summary>
public sealed class CaptureFileRecorder : IFileHook
{
    /// <summary>The files.tsv kind of a firmware module listed with its hash only.</summary>
    public const string FirmwareModuleKind = "fwmodule";

    /// <summary>The firmware's own devices: their modules are the analyst's, listed by hash only.</summary>
    internal static bool IsFirmwareDevice(string device) =>
        device.Equals("os0", StringComparison.OrdinalIgnoreCase) || device.Equals("vs0", StringComparison.OrdinalIgnoreCase)
        || device.Equals("sa0", StringComparison.OrdinalIgnoreCase) || device.Equals("pd0", StringComparison.OrdinalIgnoreCase);

    /// <summary>The firmware modules loaded while recording: guest path, SHA-256 and size of the decrypted image.</summary>
    private readonly Dictionary<string, (ChunkHash Hash, int Length)> _firmwareModules = new(StringComparer.OrdinalIgnoreCase);

    public const string Folder = "files", IndexFile = "files.tsv", BlobFile = "blobs.bin";

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReadRecord> _reads = new();
    private readonly Dictionary<ChunkHash, byte[]> _blobs = new();
    private long _keptBytes;

    /// <summary>Most bytes of distinct contents kept (the rest is listed with its hash only).</summary>
    public long MaxBytes { get; }

    /// <summary>The bytes read from the title's save data (<see cref="Vfs.IsSaveDataDevice"/>) are kept too; off (the
    /// default), they are listed with their hash only: a save is the player's, not the game's
    /// (VITASHARP_CAPTURE_SAVEDATA=1 / --capture-savedata).</summary>
    public bool KeepSaveData { get; }

    public CaptureFileRecorder(long maxBytes, bool keepSaveData = false)
    {
        MaxBytes = Math.Max(0, maxBytes);
        KeepSaveData = keepSaveData;
    }

    private sealed class Entry(string title, string path, bool directory, bool hashOnly)
    {
        public readonly string Title = title, Path = path;
        public readonly bool Directory = directory;
        /// <summary>Save data without <see cref="KeepSaveData"/>: listed, never put back by a re-run.</summary>
        public readonly bool HashOnly = hashOnly;
        public long Length;
        public ChunkHash? Module;
    }

    private readonly record struct ReadRecord(string Title, string Path, long Offset, int Length, ChunkHash Hash, long Ns, long GuestUs, bool HashOnly);

    /// <summary>The prefix of the files.tsv kinds a re-run does not put back (save data listed with hashes only:
    /// hashdir, hashfile, hashread); their reads still count as made by the recording.</summary>
    public const string HashOnlyPrefix = "hash";

    private bool HashOnly(Emulator emu, string guestPath)
    {
        int colon = guestPath.IndexOf(':');
        return !KeepSaveData && colon > 0 && emu.Vfs.IsSaveDataDevice(guestPath[..colon]);
    }

    /// <summary>The guest path as one spelling: "app0:dir/file" (no leading slash, no "." or ".."), the device in lower case.</summary>
    public static string Canonical(string guestPath)
    {
        (string device, string rel) = Vfs.Split(guestPath);
        return device.ToLowerInvariant() + ":" + Vfs.NormalizeRelative(rel);
    }

    private static bool Recorded(Emulator emu, string guestPath)
    {
        int colon = guestPath.IndexOf(':');
        return colon > 0 && emu.Vfs.IsTitleDevice(guestPath[..colon]);
    }

    private static string TitleOf(Emulator emu) => string.IsNullOrEmpty(emu.TitleId) ? "-" : emu.TitleId;

    private static string Key(string title, string path) => title + "|" + path;

    public void Opened(Emulator emu, string guestPath, string hostPath)
    {
        if (!Recorded(emu, guestPath))
        {
            return;
        }

        string path = Canonical(guestPath);
        string title = TitleOf(emu);
        bool directory = Directory.Exists(hostPath);
        long length = directory || !File.Exists(hostPath) ? 0 : new FileInfo(hostPath).Length;
        bool hashOnly = HashOnly(emu, guestPath);
        lock (_lock)
        {
            Note(title, path, directory, length, hashOnly);
        }
    }

    private Entry Note(string title, string path, bool directory, long length, bool hashOnly)
    {
        string key = Key(title, path);
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            entry = new Entry(title, path, directory, hashOnly);
            _entries[key] = entry;
        }

        entry.Length = Math.Max(entry.Length, length);
        return entry;
    }

    public void Read(Emulator emu, string guestPath, long fileLength, long offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || !Recorded(emu, guestPath))
        {
            return;
        }

        RecordRead(TitleOf(emu), guestPath, fileLength, offset, data, HashOnly(emu, guestPath));
    }

    /// <summary>A read of a file of <paramref name="title"/>'s mounts (the caller decided it is one); a
    /// <paramref name="hashOnly"/> one keeps no bytes.</summary>
    internal void RecordRead(string title, string guestPath, long fileLength, long offset, ReadOnlySpan<byte> data, bool hashOnly = false)
    {
        var hash = ChunkHash.Of(data);
        string path = Canonical(guestPath);
        long ns = CaptureClock.NowNs;
        long guestUs = GuestClock.Microseconds;
        lock (_lock)
        {
            Note(title, path, false, fileLength, hashOnly);
            _reads.Add(new ReadRecord(title, path, offset, data.Length, hash, ns, guestUs, hashOnly));
            if (!hashOnly)
            {
                Keep(hash, data);
            }
        }
    }

    /// <summary>Keeps one copy of a content within the limit. Under <see cref="_lock"/>.</summary>
    private bool Keep(ChunkHash hash, ReadOnlySpan<byte> data)
    {
        if (_blobs.ContainsKey(hash))
        {
            return true;
        }

        if (_keptBytes + data.Length > MaxBytes)
        {
            return false;
        }

        _blobs[hash] = data.ToArray();
        _keptBytes += data.Length;
        return true;
    }

    public void Listed(Emulator emu, string guestPath, string hostPath)
    {
        if (!Recorded(emu, guestPath))
        {
            return;
        }

        string path = Canonical(guestPath);
        string title = TitleOf(emu);
        bool hashOnly = HashOnly(emu, guestPath);
        List<(string Path, bool Directory, long Length)> children = [];
        try
        {
            foreach (FileSystemInfo info in new DirectoryInfo(hostPath).EnumerateFileSystemInfos())
            {
                bool directory = info is DirectoryInfo;
                string child = path.EndsWith(':') ? path + info.Name : path + "/" + info.Name;
                children.Add((child, directory, directory ? 0 : ((FileInfo)info).Length));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        lock (_lock)
        {
            Note(title, path, true, 0, hashOnly);
            foreach ((string child, bool directory, long length) in children)
            {
                Note(title, child, directory, length, hashOnly);
            }
        }
    }

    public void ModuleLoaded(Emulator emu, string guestPath, byte[] elf)
    {
        int colon = guestPath.IndexOf(':');
        if (colon > 0 && IsFirmwareDevice(guestPath[..colon]))
        {
            lock (_lock)
            {
                _firmwareModules[Canonical(guestPath)] = (ChunkHash.Of(elf), elf.Length);
            }

            return;
        }

        if (!Recorded(emu, guestPath))
        {
            return;
        }

        var hash = ChunkHash.Of(elf);
        string path = Canonical(guestPath);
        lock (_lock)
        {
            Entry entry = Note(TitleOf(emu), path, false, 0, false);
            entry.Module = hash;
            Keep(hash, elf);
        }
    }

    public byte[]? RecordedModule(Emulator emu, string guestPath) => null;

    /// <summary>Pre-roll: drops the reads made before <paramref name="keepFromNs"/> (before the oldest state kept) and
    /// the contents no read or module still needs.</summary>
    public void Trim(long keepFromNs)
    {
        lock (_lock)
        {
            int drop = 0;
            while (drop < _reads.Count && _reads[drop].Ns < keepFromNs)
            {
                drop++;
            }

            if (drop == 0)
            {
                return;
            }

            _reads.RemoveRange(0, drop);
            HashSet<ChunkHash> used = Used(long.MinValue);
            foreach (ChunkHash hash in _blobs.Keys.ToList())
            {
                if (!used.Contains(hash))
                {
                    _keptBytes -= _blobs[hash].Length;
                    _blobs.Remove(hash);
                }
            }
        }
    }

    private HashSet<ChunkHash> Used(long fromNs)
    {
        var used = new HashSet<ChunkHash>();
        foreach (ReadRecord read in _reads)
        {
            if (read.Ns >= fromNs && !read.HashOnly)
            {
                used.Add(read.Hash);
            }
        }

        foreach (Entry entry in _entries.Values)
        {
            if (entry.Module is { } module)
            {
                used.Add(module);
            }
        }

        return used;
    }

    /// <summary>What <see cref="Write"/> wrote.</summary>
    public readonly record struct Summary(int Files, int Folders, int Reads, long ReadBytes, int Chunks, long KeptBytes, int NotKept, long NotKeptBytes, int Modules, long MaxBytes, int SaveDataReads = 0)
    {
        public override string ToString()
        {
            string text = string.Create(CultureInfo.InvariantCulture, $"{Files} files and {Folders} folders of the title's mounts, {Reads} reads ({ReadBytes / 1048576.0:F1} MB), {Chunks} distinct contents kept ({KeptBytes / 1048576.0:F1} MB of the {MaxBytes / 1048576.0:F0} MB limit, VITASHARP_CAPTURE_FILES_MB / --capture-files-mb)");
            if (NotKept > 0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $", {NotKept} reads ({NotKeptBytes / 1048576.0:F1} MB) over the limit listed with their hash only");
            }

            if (Modules > 0)
            {
                text += $", {Modules} module images";
            }

            if (SaveDataReads > 0)
            {
                text += $", {SaveDataReads} save data reads with their hash only (--capture-savedata / VITASHARP_CAPTURE_SAVEDATA=1 keeps them)";
            }

            return text;
        }
    }

    /// <summary>The report's section on files/ (<see cref="CaptureAnalyzer"/>): what the game read from its own mounts,
    /// how much of it left with the capture, and the files it read most; empty when the capture has no files/.</summary>
    public static List<string> ReportSection(string captureDir, int top = 8)
    {
        List<string> lines = [];
        string index = Path.Combine(captureDir, Folder, IndexFile);
        if (!File.Exists(index))
        {
            return lines;
        }

        int files = 0, folders = 0, reads = 0, notKept = 0, modules = 0, saveDataReads = 0;
        long readBytes = 0, notKeptBytes = 0;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var perFile = new Dictionary<string, (int Reads, long Bytes, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(index))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split('\t');
            if (f.Length < 8)
            {
                continue;
            }

            string name = f[2];
            switch (f[0])
            {
                case "dir":
                case HashOnlyPrefix + "dir":
                    folders++;
                    break;
                case "file":
                case HashOnlyPrefix + "file":
                {
                    files++;
                    long size = long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
                    (int fileReads, long fileBytes, long _) = perFile.GetValueOrDefault(name);
                    perFile[name] = (fileReads, fileBytes, size);
                    break;
                }
                case "module":
                    modules++;
                    break;
                case "read":
                case HashOnlyPrefix + "read":
                {
                    if (!int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length))
                    {
                        break;
                    }

                    reads++;
                    readBytes += length;
                    distinct.Add(f[5]);
                    if (f[0] != "read")
                    {
                        saveDataReads++;
                    }
                    else if (f[6] == "-")
                    {
                        notKept++;
                        notKeptBytes += length;
                    }

                    (int fileReads, long fileBytes, long size) = perFile.GetValueOrDefault(name);
                    perFile[name] = (fileReads + 1, fileBytes + length, size);
                    break;
                }
            }
        }

        lines.Add("Files the game read from its own mounts (files/; the re-run puts them back over app0, savedata0...):");
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {files} files, {folders} folders, {reads} reads ({readBytes / 1048576.0:F1} MB, {distinct.Count} distinct contents), {modules} module images"));
        lines.Add(notKept == 0
            ? "  every read of the game's files left with the capture: a re-run without the game reads the same bytes"
            : string.Create(CultureInfo.InvariantCulture, $"  {notKept} reads ({notKeptBytes / 1048576.0:F1} MB) over the limit, listed with their hash only: a re-run without the game reads zeros there (VITASHARP_CAPTURE_FILES_MB / --capture-files-mb raises it)"));
        if (saveDataReads > 0)
        {
            lines.Add($"  {saveDataReads} reads of the save data listed with their hash only (the player's data; --capture-savedata / VITASHARP_CAPTURE_SAVEDATA=1 keeps them): a re-run reads its own data folder's save");
        }

        long blobBytes = File.Exists(Path.Combine(captureDir, Folder, BlobFile)) ? new FileInfo(Path.Combine(captureDir, Folder, BlobFile)).Length : 0;
        if (blobBytes > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  NOTE: this capture holds data of the game ({blobBytes / 1048576.0:F1} MB of what it read, files/blobs.bin): share it only where the game's data may go, or record with --capture-files-mb=0 (hashes only)"));
        }
        foreach ((string name, (int fileReads, long fileBytes, long size)) in perFile.Where(p => p.Value.Reads > 0).OrderByDescending(p => p.Value.Bytes).ThenBy(p => p.Key, StringComparer.Ordinal).Take(top))
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {name}: {fileReads} reads, {fileBytes} bytes of {size}"));
        }

        return lines;
    }

    /// <summary>Writes files/files.tsv and files/blobs.bin into <paramref name="captureDir"/>: every file and folder,
    /// and the reads made from <paramref name="fromNs"/> on (the pre-roll's state or the start).</summary>
    public Summary Write(string captureDir, long fromNs)
    {
        string dir = Path.Combine(captureDir, Folder);
        Directory.CreateDirectory(dir);
        lock (_lock)
        {
            var offsets = new Dictionary<ChunkHash, long>();
            long readBytes = 0;
            int reads = 0, notKept = 0, modules = 0, saveDataReads = 0;
            long notKeptBytes = 0;
            using var blobs = new FileStream(Path.Combine(dir, BlobFile), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            using var index = new StreamWriter(Path.Combine(dir, IndexFile), false, new UTF8Encoding(false));
            index.WriteLine("# kind\ttitle\tpath\toffset\tlength\tsha256\tblob\tguestUs");
            index.WriteLine(string.Create(CultureInfo.InvariantCulture, $"# limit {MaxBytes} bytes of distinct contents"));
            string BlobOf(ChunkHash hash)
            {
                if (offsets.TryGetValue(hash, out long at))
                {
                    return at.ToString(CultureInfo.InvariantCulture);
                }

                if (!_blobs.TryGetValue(hash, out byte[]? data))
                {
                    return "-";
                }

                at = blobs.Position;
                blobs.Write(data);
                offsets[hash] = at;
                return at.ToString(CultureInfo.InvariantCulture);
            }

            int files = 0, folders = 0;
            foreach (Entry entry in _entries.Values.OrderBy(e => e.Title, StringComparer.Ordinal).ThenBy(e => e.Path, StringComparer.Ordinal))
            {
                string prefix = entry.HashOnly ? HashOnlyPrefix : "";
                if (entry.Directory)
                {
                    folders++;
                    index.WriteLine($"{prefix}dir\t{entry.Title}\t{entry.Path}\t-\t-\t-\t-\t-");
                    continue;
                }

                files++;
                index.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{prefix}file\t{entry.Title}\t{entry.Path}\t-\t{entry.Length}\t-\t-\t-"));
                if (entry.Module is { } module)
                {
                    modules++;
                    index.WriteLine(string.Create(CultureInfo.InvariantCulture, $"module\t{entry.Title}\t{entry.Path}\t-\t{(_blobs.TryGetValue(module, out byte[]? image) ? image.Length : 0)}\t{module}\t{BlobOf(module)}\t-"));
                }
            }

            foreach ((string path, (ChunkHash hash, int length)) in _firmwareModules.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                index.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{FirmwareModuleKind}\t-\t{path}\t-\t{length}\t{hash}\t-\t-"));
            }

            foreach (ReadRecord read in _reads)
            {
                if (read.Ns < fromNs)
                {
                    continue;
                }

                reads++;
                readBytes += read.Length;
                if (read.HashOnly)
                {
                    saveDataReads++;
                    index.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{HashOnlyPrefix}read\t{read.Title}\t{read.Path}\t{read.Offset}\t{read.Length}\t{read.Hash}\t-\t{read.GuestUs}"));
                    continue;
                }

                string blob = BlobOf(read.Hash);
                if (blob == "-")
                {
                    notKept++;
                    notKeptBytes += read.Length;
                }

                index.WriteLine(string.Create(CultureInfo.InvariantCulture, $"read\t{read.Title}\t{read.Path}\t{read.Offset}\t{read.Length}\t{read.Hash}\t{blob}\t{read.GuestUs}"));
            }

            return new Summary(files, folders, reads, readBytes, offsets.Count, blobs.Length, notKept, notKeptBytes, modules, MaxBytes, saveDataReads);
        }
    }
}

/// <summary>
/// A re-run's view of the files a capture recorded (<see cref="CaptureFileRecorder"/>): they are put back as files
/// of the size the recording saw (sparse: only what was read takes space) in a folder of the re-run, which goes over
/// the title's mounts (<see cref="Vfs.AddReplayLayer"/>), so the guest opens, lists and reads them as it did, without
/// the game. A read the recording did not make (the re-run diverged) is logged with its path and counted.
/// </summary>
public sealed class CapturedFiles : IFileHook
{
    private readonly Dictionary<string, List<(long Offset, long End)>> _ranges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private long _outside;
    private long _withoutBytes;
    /// <summary>Per file, the recorded reads whose bytes the capture does not hold (hashes only).</summary>
    private readonly Dictionary<string, List<(long Offset, long End)>> _hashOnly = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedHashOnly = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The firmware modules the recording loaded: guest path and the SHA-256 of their image there.</summary>
    private readonly Dictionary<string, ChunkHash> _firmwareModules = new(StringComparer.OrdinalIgnoreCase);
    private int _firmwareMismatches;

    /// <summary>The re-run's files: every process brought up from a state puts its title's over its mounts.</summary>
    public static CapturedFiles? Active { get; set; }

    /// <summary>Per title, per device: the folder that holds the recorded files.</summary>
    public Dictionary<string, Dictionary<string, string>> Layers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Files { get; private set; }
    public int Reads { get; private set; }
    public int MissingContents { get; private set; }
    /// <summary>Lines of files.tsv not put back: they do not parse or their path would leave the re-run's folder.</summary>
    public int Skipped { get; private set; }
    /// <summary>Reads of the re-run the recording did not make.</summary>
    public long ReadsOutside => Interlocked.Read(ref _outside);
    /// <summary>Reads of the re-run the recording made but kept with their hash only: their bytes could not be put back
    /// (the re-run read zeros there).</summary>
    public long ReadsWithoutBytes => Interlocked.Read(ref _withoutBytes);
    /// <summary>The firmware modules the recording listed.</summary>
    public int FirmwareModules => _firmwareModules.Count;
    /// <summary>Firmware modules the re-run loaded from this computer's firmware that are not the recording's (another
    /// firmware version or a patched module): the re-run may differ from there.</summary>
    public int FirmwareMismatches => Volatile.Read(ref _firmwareMismatches);

    private CapturedFiles()
    {
    }

    /// <summary>Puts the recorded files of <paramref name="captureDir"/> back under <paramref name="outDir"/>; null when
    /// the capture recorded none (made by an earlier build).</summary>
    public static CapturedFiles? Materialize(string captureDir, string outDir)
    {
        string index = Path.Combine(captureDir, CaptureFileRecorder.Folder, CaptureFileRecorder.IndexFile);
        if (!File.Exists(index))
        {
            return null;
        }

        var files = new CapturedFiles();
        string blobPath = Path.Combine(captureDir, CaptureFileRecorder.Folder, CaptureFileRecorder.BlobFile);
        using FileStream? blobs = File.Exists(blobPath) ? File.OpenRead(blobPath) : null;
        foreach (string line in File.ReadLines(index))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split('\t');
            if (f.Length < 8)
            {
                continue;
            }

            // a capture comes from someone else (a GitHub report): a line that does not parse, or whose path would
            // leave the re-run's folder (a drive or root path, "..", a device that is not a plain name), is skipped
            string kind = f[0], title = f[1], path = f[2];
            (string device, string rel) = Vfs.Split(path);
            if (!IsPlainName(device) || rel.Contains("..", StringComparison.Ordinal))
            {
                files.Skipped++;
                continue;
            }

            if (kind == CaptureFileRecorder.FirmwareModuleKind)
            {
                // a firmware module: nothing is put back (the analyst's firmware has it), its hash is compared at load
                if (f[5].Length == 64 && f[5].All(Uri.IsHexDigit))
                {
                    files._firmwareModules[CaptureFileRecorder.Canonical(path)] = ChunkHash.Parse(f[5]);
                }
                else
                {
                    files.Skipped++;
                }

                continue;
            }

            if (kind.StartsWith(CaptureFileRecorder.HashOnlyPrefix, StringComparison.Ordinal))
            {
                // save data listed with hashes only: nothing is put back (no layer: the re-run's own save data folder is
                // the one read), its reads still count as the recording's
                if (!files.PutHashOnly(kind, title, path, f))
                {
                    files.Skipped++;
                }

                continue;
            }

            string root = files.LayerOf(outDir, title, device);
            string host = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(root, host))
            {
                files.Skipped++;
                continue;
            }

            try
            {
                if (!files.Put(kind, title, path, host, f, blobs))
                {
                    files.Skipped++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                files.Skipped++;
                Log.Warn($"[capture] re-run: {path} not put back: {e.Message}");
            }
        }

        return files;
    }

    private bool PutHashOnly(string kind, string title, string path, string[] f)
    {
        if (kind != CaptureFileRecorder.HashOnlyPrefix + "read")
        {
            return kind is CaptureFileRecorder.HashOnlyPrefix + "dir" or CaptureFileRecorder.HashOnlyPrefix + "file";
        }

        if (!long.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset) || offset < 0
            || !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) || length < 0)
        {
            return false;
        }

        Reads++;
        AddRange(title + "|" + CaptureFileRecorder.Canonical(path), offset, length);
        return true;
    }

    /// <summary>One line of files.tsv put back; false when its numbers do not parse.</summary>
    private bool Put(string kind, string title, string path, string host, string[] f, FileStream? blobs)
    {
        switch (kind)
        {
            case "dir":
                Directory.CreateDirectory(host);
                return true;
            case "file":
            {
                if (!long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) || size < 0)
                {
                    return false;
                }

                Files++;
                CreateSized(host, size);
                return true;
            }
            case "read":
            {
                if (!long.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset) || offset < 0
                    || !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) || length < 0)
                {
                    return false;
                }

                Reads++;
                string key = title + "|" + CaptureFileRecorder.Canonical(path);
                AddRange(key, offset, length);
                if (Content(blobs, f[6], length) is { } data)
                {
                    WriteAt(host, offset, data);
                }
                else
                {
                    MissingContents++;
                    if (!_hashOnly.TryGetValue(key, out List<(long Offset, long End)>? missing))
                    {
                        missing = [];
                        _hashOnly[key] = missing;
                    }

                    missing.Add((offset, offset + length));
                }

                return true;
            }
            case "module":
            {
                if (!int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) || length < 0)
                {
                    return false;
                }

                if (Content(blobs, f[6], length) is { } image)
                {
                    _modules[title + "|" + CaptureFileRecorder.Canonical(path)] = image;
                }

                return true;
            }
            default:
                return false;
        }
    }

    private void AddRange(string key, long offset, int length)
    {
        if (!_ranges.TryGetValue(key, out List<(long Offset, long End)>? list))
        {
            list = [];
            _ranges[key] = list;
        }

        list.Add((offset, offset + length));
    }

    /// <summary>A device or title as a folder name: letters, digits and '_' only.</summary>
    private static bool IsPlainName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInside(string root, string host)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return host.TrimEnd(Path.DirectorySeparatorChar).Equals(folder, comparison) || host.StartsWith(folder + Path.DirectorySeparatorChar, comparison);
    }

    private string LayerOf(string outDir, string title, string device)
    {
        if (!Layers.TryGetValue(title, out Dictionary<string, string>? devices))
        {
            devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Layers[title] = devices;
        }

        if (!devices.TryGetValue(device, out string? root))
        {
            root = Path.Combine(outDir, string.Concat(title.Select(c => char.IsLetterOrDigit(c) ? c : '_')), device);
            Directory.CreateDirectory(root);
            devices[device] = root;
        }

        return root;
    }

    private static byte[]? Content(FileStream? blobs, string at, int length)
    {
        if (blobs == null || at == "-" || !long.TryParse(at, NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset) || offset < 0 || offset + length > blobs.Length)
        {
            return null;
        }

        byte[] data = new byte[length];
        blobs.Position = offset;
        blobs.ReadExactly(data);
        return data;
    }

    /// <summary>A file of <paramref name="length"/> bytes where only what is written takes space (sparse on NTFS, as
    /// every file extended without writing is on ext4, APFS and the other host file systems).</summary>
    private static void CreateSized(string host, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        using var file = new FileStream(host, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (OperatingSystem.IsWindows())
        {
            MarkSparse(file);
        }

        if (file.Length < length)
        {
            file.SetLength(length);
        }
    }

    private static void WriteAt(string host, long offset, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        using var file = new FileStream(host, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (OperatingSystem.IsWindows() && file.Length == 0)
        {
            MarkSparse(file);
        }

        RandomAccess.Write(file.SafeFileHandle, data, offset);
    }

    private const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    private static void MarkSparse(FileStream file)
    {
        // a file system without sparse files (FAT) refuses it: the file is then just as long, with its space taken
        DeviceIoControl(file.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    /// <summary>The layers go over the mounts of <paramref name="emu"/> (its title's).</summary>
    public void Apply(Emulator emu)
    {
        string title = string.IsNullOrEmpty(emu.TitleId) ? "-" : emu.TitleId;
        if (!Layers.TryGetValue(title, out Dictionary<string, string>? devices))
        {
            return;
        }

        foreach ((string device, string root) in devices)
        {
            emu.Vfs.AddReplayLayer(device, root);
        }
    }

    public void Opened(Emulator emu, string guestPath, string hostPath)
    {
    }

    public void Listed(Emulator emu, string guestPath, string hostPath)
    {
    }

    /// <summary>A firmware module the recording also loaded: when this computer's image is not the recording's, the
    /// log says so once per module and it is counted (the re-run runs another firmware's code from there).</summary>
    public void ModuleLoaded(Emulator emu, string guestPath, byte[] elf)
    {
        string path = CaptureFileRecorder.Canonical(guestPath);
        if (!_firmwareModules.TryGetValue(path, out ChunkHash recorded))
        {
            return;
        }

        var here = ChunkHash.Of(elf);
        if (here == recorded)
        {
            return;
        }

        lock (_lock)
        {
            if (!_warned.Add("fw|" + path))
            {
                return;
            }
        }

        Interlocked.Increment(ref _firmwareMismatches);
        Log.Warn($"[capture] re-run: the firmware module {path} is not the recording's (sha256 {here.ToString()[..16]}... here, "
            + $"{recorded.ToString()[..16]}... there): it comes from this computer's firmware, another version or a patched one; the re-run may differ from there");
    }

    public byte[]? RecordedModule(Emulator emu, string guestPath) =>
        _modules.TryGetValue((string.IsNullOrEmpty(emu.TitleId) ? "-" : emu.TitleId) + "|" + CaptureFileRecorder.Canonical(guestPath), out byte[]? image) ? image : null;

    public void Read(Emulator emu, string guestPath, long fileLength, long offset, ReadOnlySpan<byte> data)
    {
        int colon = guestPath.IndexOf(':');
        if (data.IsEmpty || colon <= 0 || !emu.Vfs.IsTitleDevice(guestPath[..colon]))
        {
            return;
        }

        Check(string.IsNullOrEmpty(emu.TitleId) ? "-" : emu.TitleId, guestPath, offset, data.Length);
    }

    /// <summary>Whether the recording read all of those bytes; a read it did not make is counted (and logged once
    /// per file).</summary>
    internal bool Check(string title, string guestPath, long offset, int length)
    {
        string key = title + "|" + CaptureFileRecorder.Canonical(guestPath);
        long end = offset + length;
        lock (_lock)
        {
            if (_ranges.TryGetValue(key, out List<(long Offset, long End)>? list) && Covered(list, offset, end))
            {
                if (_hashOnly.TryGetValue(key, out List<(long Offset, long End)>? missing) && Overlaps(missing, offset, end))
                {
                    _withoutBytes++;
                    if (_warnedHashOnly.Add(key))
                    {
                        Log.Warn($"[capture] re-run: {guestPath} read at {offset} ({length} bytes): the recording kept its hash only, the re-run reads zeros there (record with --capture-files-mb=MB to keep the bytes)");
                    }
                }

                return true;
            }

            _outside++;
            if (_warned.Add(key))
            {
                Log.Warn($"[capture] re-run: {guestPath} read at {offset} ({length} bytes), which the recording did not read: the re-run diverged here (the bytes are the installed game's, or zeros)");
            }

            return false;
        }
    }

    private static bool Overlaps(List<(long Offset, long End)> list, long offset, long end)
    {
        foreach ((long from, long to) in list)
        {
            if (from < end && to > offset)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Covered(List<(long Offset, long End)> list, long offset, long end)
    {
        long at = offset;
        bool advanced = true;
        while (at < end && advanced)
        {
            advanced = false;
            foreach ((long from, long to) in list)
            {
                if (from <= at && to > at)
                {
                    at = to;
                    advanced = true;
                }
            }
        }

        return at >= end;
    }
}
