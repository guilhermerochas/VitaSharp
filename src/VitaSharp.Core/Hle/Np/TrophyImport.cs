using System.Buffers.Binary;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Np;

/// <summary>
/// Brings trophies earned elsewhere into the emulated console's user. Two sources:
/// <list type="bullet">
/// <item><b>A PS Vita's trophy folder</b> (<c>ur0:user/00/trophy</c>: <c>data/sce_trop/TRPUSER.DAT</c>, per title
/// <c>data/&lt;NPCOMMID&gt;/TRPTITLE.DAT, TRPTRANS.DAT</c>, <c>conf/&lt;NPCOMMID&gt;/</c>, <c>db/trophy_local.db</c>). These are
/// the files of the shell's own trophy server (SceNpTrophyServer in shell.self), which reads them through
/// sceAppMgrTrophyMount 302/303, so a copy of a console's folder is what the server of the emulated firmware reads. On the
/// console the data folders are protected file system folders sealed with that console's keys: a copy taken from the
/// memory with a file manager has the encrypted files (and a <c>sce_pfs</c> folder) and cannot be used here; a copy made
/// through the system's own mount (decrypted) can. Imported by replacing the emulated folder (the old one is kept as a
/// backup).</item>
/// <item><b>Vita3K's trophy files</b> (<c>ux0/user/00/trophy/data/&lt;NPCOMMID&gt;/TROPUSR.DAT</c>, Vita3K's own format: magic
/// 0x12D5819A, unlock bit field at +4, unlock times (Unix seconds) at +0x70, 8 bytes per trophy). Merged into VitaSharp's
/// store (<see cref="TrophyUnlocks"/>, a union keeping the earliest time), which the HLE SceNpTrophy, the launcher and the
/// shell's trophy server (through <see cref="TrophyServerBridge"/>, when the title runs under the shell) use.</item>
/// </list>
/// </summary>
public static class TrophyImport
{
    public enum SourceKind
    {
        /// <summary>Nothing recognised.</summary>
        Unknown,
        /// <summary>A Vita trophy folder with readable (decrypted) server files.</summary>
        VitaDecrypted,
        /// <summary>A Vita trophy folder copied as stored on the memory: encrypted with the console's keys.</summary>
        VitaEncrypted,
        /// <summary>Vita3K's trophy folder.</summary>
        Vita3K,
    }

    /// <summary>First word of a readable TRPUSER.DAT (the shell's trophy server, FW 3.74; big-endian 0x7F159936).</summary>
    static readonly byte[] TrpUserMagic = [0x7F, 0x15, 0x99, 0x36];
    /// <summary>Vita3K's TROPUSR.DAT magic (little-endian).</summary>
    public const uint Vita3KMagic = 0x12D5819A;
    const int Vita3KMaxTrophies = 128, Vita3KTimesOffset = 0x70, Vita3KSize = 0x670;
    /// <summary>Seconds from 0001-01-01 to 1970-01-01 (SceRtcTick counts microseconds from 0001-01-01).</summary>
    const ulong UnixEpochSeconds = 62_135_596_800UL;

    public sealed record TitleResult(string CommId, int Imported, int Total);

    public sealed record Report(SourceKind Kind, string Source, List<TitleResult> Titles, string? Backup, string Message);

    /// <summary>The trophy folder inside <paramref name="dir"/>: the folder itself or its user/00/trophy (a dump of ur0: or
    /// ux0:, or Vita3K's data folder with ux0/).</summary>
    public static string Locate(string dir)
    {
        foreach (var rel in new[] { "", "user/00/trophy", "ur0/user/00/trophy", "ux0/user/00/trophy", "trophy" })
        {
            string d = rel.Length == 0 ? dir : Path.Combine(dir, rel);
            if (Directory.Exists(Path.Combine(d, "data")) || Directory.Exists(Path.Combine(d, "conf"))) return d;
        }
        return dir;
    }

    /// <summary>Vita3K's trophy folder (ux0/user/00/trophy of its data folder), when there is one.</summary>
    public static string? FindVita3K() =>
        Firmware.FirmwareInstaller.Vita3KRoots().Select(r => Path.Combine(r, "ux0", "user", "00", "trophy"))
            .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "data")));

    public static SourceKind Detect(string trophyDir)
    {
        string data = Path.Combine(trophyDir, "data");
        if (!Directory.Exists(data)) return SourceKind.Unknown;
        bool vita3k = false, vita = false, encrypted = false;
        foreach (var title in SafeDirs(data))
        {
            if (Directory.Exists(Path.Combine(title, "sce_pfs"))) encrypted = true;
            if (File.Exists(Path.Combine(title, "TROPUSR.DAT")) && ReadU32(Path.Combine(title, "TROPUSR.DAT")) == Vita3KMagic) vita3k = true;
            string user = Path.Combine(title, "TRPUSER.DAT");
            if (File.Exists(user)) { vita = true; if (!StartsWith(user, TrpUserMagic)) encrypted = true; }
            if (File.Exists(Path.Combine(title, "TRPTITLE.DAT"))) vita = true;
        }
        if (vita) return encrypted ? SourceKind.VitaEncrypted : SourceKind.VitaDecrypted;
        return vita3k ? SourceKind.Vita3K : SourceKind.Unknown;
    }

    /// <summary>Imports <paramref name="source"/> (any of the folders <see cref="Locate"/> accepts) into the data folder
    /// whose file system root is <paramref name="fsRoot"/>.</summary>
    public static Report Import(string source, string fsRoot)
    {
        string dir = Locate(source);
        var kind = Detect(dir);
        return kind switch
        {
            SourceKind.Vita3K => ImportVita3K(dir, fsRoot),
            SourceKind.VitaDecrypted => ImportVita(dir, source, fsRoot),
            SourceKind.VitaEncrypted => new Report(kind, dir, [], null,
                "These are the trophy files as stored on the PS Vita's memory: encrypted with that console's own keys (sce_pfs). "
                + "They can only be read on that console. Copy the folder decrypted through the system (a homebrew that reads it "
                + "through the trophy mounts, with the console's keys), then import that copy."),
            _ => new Report(kind, dir, [], null, "No trophy data found (expected data/sce_trop/TRPUSER.DAT from a PS Vita, or Vita3K's data/<NPCOMMID>/TROPUSR.DAT)."),
        };
    }

    /// <summary>Unlocks in a Vita3K TROPUSR.DAT: trophy id to SceRtcTick of the unlock (0 when Vita3K kept no time).</summary>
    public static Dictionary<int, ulong> ReadVita3K(byte[] d)
    {
        var result = new Dictionary<int, ulong>();
        if (d.Length < Vita3KSize || BinaryPrimitives.ReadUInt32LittleEndian(d) != Vita3KMagic) throw new InvalidDataException("not a Vita3K TROPUSR.DAT");
        for (int id = 0; id < Vita3KMaxTrophies; id++)
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4 + id / 32 * 4));
            if ((bits & (1u << (id % 32))) == 0) continue;
            ulong t = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(Vita3KTimesOffset + id * 8));
            result[id] = UnixToTick(t);
        }
        return result;
    }

    /// <summary>Unix seconds to SceRtcTick; 0 stays 0 (no time).</summary>
    public static ulong UnixToTick(ulong unixSeconds) => unixSeconds == 0 ? 0 : (unixSeconds + UnixEpochSeconds) * 1_000_000UL;

    /// <summary>Adds <paramref name="incoming"/> to <paramref name="store"/>: new trophies are added, a trophy in both keeps
    /// the earlier known time. Returns how many trophies were new.</summary>
    public static int Merge(Dictionary<int, ulong> store, IReadOnlyDictionary<int, ulong> incoming)
    {
        int added = 0;
        foreach (var (id, tick) in incoming)
        {
            if (!store.TryGetValue(id, out var have)) { store[id] = tick; added++; }
            else if (tick != 0 && (have == 0 || tick < have)) store[id] = tick;
        }
        return added;
    }

    static Report ImportVita3K(string dir, string fsRoot)
    {
        var titles = new List<TitleResult>();
        foreach (var title in SafeDirs(Path.Combine(dir, "data")))
        {
            string file = Path.Combine(title, "TROPUSR.DAT");
            string commId = Path.GetFileName(title);
            if (!File.Exists(file) || !IsCommId(commId)) continue;
            Dictionary<int, ulong> unlocked;
            try { unlocked = ReadVita3K(File.ReadAllBytes(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Warn($"Trophy import: {file}: {e.Message}");
                continue;
            }
            string path = TrophyUnlocks.PathFor(fsRoot, commId);
            var store = TrophyUnlocks.Load(path);
            int added = Merge(store, unlocked);
            if (added > 0 || (unlocked.Count > 0 && !File.Exists(path))) TrophyUnlocks.Save(path, store);
            titles.Add(new TitleResult(commId, added, unlocked.Count));
        }
        int total = titles.Sum(t => t.Imported);
        return new Report(SourceKind.Vita3K, dir, titles, null,
            $"{total} trophies imported from Vita3K into VitaSharp's trophy list ({titles.Count(t => t.Total > 0)} titles with unlocks). "
            + "The real Trophy Collection shows a title after the game has been started once from the system (Sistema do Vita).");
    }

    static Report ImportVita(string dir, string source, string fsRoot)
    {
        string dest = Path.Combine(fsRoot, "ur0", "user", "00", "trophy");
        string? backup = null;
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            backup = $"{dest}.backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            Directory.Move(dest, backup);
        }
        CopyTree(dir, dest);
        var titles = SafeDirs(Path.Combine(dest, "data")).Select(Path.GetFileName).Where(n => n != null && IsCommId(n))
            .Select(n => new TitleResult(n!, 0, 0)).ToList();
        // the trophy icons the server keeps on the memory card (ux0:user/00/trophy/conf), when the copy has them: a dump
        // with ur0/ and ux0/ side by side
        string ux0Conf = Path.Combine(source, "ux0", "user", "00", "trophy", "conf");
        if (Directory.Exists(ux0Conf)) CopyTree(ux0Conf, Path.Combine(fsRoot, "ux0", "user", "00", "trophy", "conf"));
        return new Report(SourceKind.VitaDecrypted, dir, titles, backup,
            $"The PS Vita's trophy data ({titles.Count} titles) now is the system's: the real Trophy Collection reads it."
            + (backup != null ? $" The previous data was kept in {backup}." : ""));
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>NP communication id folder name: 4 letters, 5 digits, _ and 2 digits (NPWR01931_00).</summary>
    public static bool IsCommId(string name) =>
        name.Length == 12 && name[9] == '_' && name[..4].All(char.IsAsciiLetterUpper) && name[4..9].All(char.IsAsciiDigit) && name[10..].All(char.IsAsciiDigit);

    static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir).Order(StringComparer.Ordinal).ToArray() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static uint ReadU32(string path)
    {
        Span<byte> b = stackalloc byte[4];
        try
        {
            using var f = File.OpenRead(path);
            return f.Read(b) == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b) : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    static bool StartsWith(string path, byte[] magic)
    {
        Span<byte> b = stackalloc byte[4];
        try
        {
            using var f = File.OpenRead(path);
            return f.Read(b) == 4 && b.SequenceEqual(magic);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
