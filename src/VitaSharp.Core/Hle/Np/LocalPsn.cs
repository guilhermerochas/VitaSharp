using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Np;

/// <summary>
/// The local stand-in for the PSN community servers: Title User Storage (TUS, used for Cross-Save and shared game
/// state) and score rankings, stored as files under <see cref="Root"/> (<c>&lt;DataRoot&gt;/np</c>). Nothing ever leaves
/// the PC. Every entry is keyed by the owner's online ID, so each emulated PSN account (<see cref="PsnAccount"/>) has its
/// own data, like a real account on the real servers.
/// <code>
/// np/tus/&lt;npCommId&gt;/&lt;owner&gt;/variables.json      slot -> 64-bit variable (+ old value, date, author)
/// np/tus/&lt;npCommId&gt;/&lt;owner&gt;/data_&lt;slot&gt;.bin      TUS data blob
/// np/tus/&lt;npCommId&gt;/&lt;owner&gt;/data_&lt;slot&gt;.json     its status (date, author, info bytes)
/// np/score/&lt;npCommId&gt;/board_&lt;boardId&gt;.json        board settings + entries (one per online ID and character)
/// </code>
/// </summary>
public sealed class LocalPsn(string root)
{
    public string Root { get; } = root;
    public TusStore Tus { get; } = new(Path.Combine(root, "tus"));
    public ScoreStore Score { get; } = new(Path.Combine(root, "score"));

    /// <summary><c>&lt;DataRoot&gt;/np</c>, where DataRoot is the folder that holds the virtual filesystem root.</summary>
    public static string DefaultRoot(string fsRoot) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? fsRoot, "np");

    /// <summary>SceRtcTick of now (microseconds since 0001-01-01 UTC).</summary>
    public static ulong NowTick() => (ulong)(DateTime.UtcNow.Ticks / 10);

    /// <summary>A file/folder name for an id that may contain any byte (virtual user ids): online IDs and communication
    /// IDs are used as they are, anything else is hex-encoded with an "x-" prefix.</summary>
    public static string SafeName(string id)
    {
        if (id.Length > 0 && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && !id.StartsWith("x-"))
            return id;
        return "x-" + Convert.ToHexString(Encoding.UTF8.GetBytes(id));
    }

    internal static T? ReadJson<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"PSN (local): could not read {path}: {e.Message}");
            return null;
        }
    }

    internal static void WriteJson<T>(string path, T value, JsonTypeInfo<T> type)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // write-then-rename: a crash mid-write never leaves a truncated store
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, type));
            File.Move(tmp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"PSN (local): could not write {path}: {e.Message}");
        }
    }
}

/// <summary>A TUS variable as stored (and as the server reports it).</summary>
public sealed class TusVariable
{
    public long Value { get; set; }
    public long OldValue { get; set; }
    public ulong LastChanged { get; set; }
    public string Author { get; set; } = "";
}

/// <summary>The status of a TUS data slot (the blob itself is a separate file).</summary>
public sealed class TusDataStatus
{
    public ulong LastChanged { get; set; }
    public string Author { get; set; } = "";
    /// <summary>The SceNpTusDataInfo bytes the writer attached (at most 384).</summary>
    public byte[] Info { get; set; } = [];
    public int Size { get; set; }
}

/// <summary>TUS comparison operators of sceNpTusTryAndSetVariable (SCE_NP_TUS_OPETYPE_*).</summary>
public enum TusOpeType { Equal = 1, NotEqual = 2, GreaterThan = 3, GreaterOrEqual = 4, LessThan = 5, LessOrEqual = 6 }

/// <summary>Title User Storage: per title (communication id) and owner, numbered slots of 64-bit variables and of data
/// blobs. Thread-safe; every change is written to disk at once.</summary>
public sealed class TusStore(string root)
{
    public const int MaxDataInfoSize = 384;
    readonly object _lock = new();

    string OwnerDir(string commId, string owner) => Path.Combine(root, LocalPsn.SafeName(commId), LocalPsn.SafeName(owner));
    string VarPath(string commId, string owner) => Path.Combine(OwnerDir(commId, owner), "variables.json");

    SortedDictionary<int, TusVariable> LoadVars(string commId, string owner) =>
        LocalPsn.ReadJson(VarPath(commId, owner), NpJson.Default.SortedDictionaryInt32TusVariable) ?? new();

    public TusVariable? GetVariable(string commId, string owner, int slot)
    {
        lock (_lock) return LoadVars(commId, owner).GetValueOrDefault(slot);
    }

    /// <summary>Sets several slots in one transaction (sceNpTusSetMultiSlotVariable).</summary>
    public void SetVariables(string commId, string owner, IReadOnlyList<(int Slot, long Value)> values, string author)
    {
        lock (_lock)
        {
            var vars = LoadVars(commId, owner);
            ulong now = LocalPsn.NowTick();
            foreach (var (slot, value) in values)
            {
                long old = vars.TryGetValue(slot, out var v) ? v.Value : 0;
                vars[slot] = new TusVariable { Value = value, OldValue = old, LastChanged = now, Author = author };
            }
            LocalPsn.WriteJson(VarPath(commId, owner), vars, NpJson.Default.SortedDictionaryInt32TusVariable);
        }
    }

    /// <summary>sceNpTusAddAndGetVariable: adds to the slot (0 when it has no value yet) and returns the result.</summary>
    public TusVariable AddAndGet(string commId, string owner, int slot, long add, string author)
    {
        lock (_lock)
        {
            var vars = LoadVars(commId, owner);
            long old = vars.TryGetValue(slot, out var v) ? v.Value : 0;
            var nv = new TusVariable { Value = unchecked(old + add), OldValue = old, LastChanged = LocalPsn.NowTick(), Author = author };
            vars[slot] = nv;
            LocalPsn.WriteJson(VarPath(commId, owner), vars, NpJson.Default.SortedDictionaryInt32TusVariable);
            return nv;
        }
    }

    /// <summary>
    /// sceNpTusTryAndSetVariable: writes <paramref name="value"/> when "<c>compare</c> &lt;op&gt; current value" holds
    /// (compare = the option's compareValue, else the value itself: GREATER_THAN keeps a maximum). A slot without a
    /// value always accepts. Returns the slot after the attempt and whether it was written.
    /// </summary>
    public (TusVariable Variable, bool Written) TryAndSet(string commId, string owner, int slot, TusOpeType op, long value, long? compareValue, string author)
    {
        lock (_lock)
        {
            var vars = LoadVars(commId, owner);
            bool has = vars.TryGetValue(slot, out var cur);
            long current = cur?.Value ?? 0, lhs = compareValue ?? value;
            bool ok = !has || op switch
            {
                TusOpeType.Equal => lhs == current,
                TusOpeType.NotEqual => lhs != current,
                TusOpeType.GreaterThan => lhs > current,
                TusOpeType.GreaterOrEqual => lhs >= current,
                TusOpeType.LessThan => lhs < current,
                TusOpeType.LessOrEqual => lhs <= current,
                _ => false,
            };
            if (!ok) return (cur!, false);
            var nv = new TusVariable { Value = value, OldValue = current, LastChanged = LocalPsn.NowTick(), Author = author };
            vars[slot] = nv;
            LocalPsn.WriteJson(VarPath(commId, owner), vars, NpJson.Default.SortedDictionaryInt32TusVariable);
            return (nv, true);
        }
    }

    public void DeleteVariables(string commId, string owner, IEnumerable<int> slots)
    {
        lock (_lock)
        {
            var vars = LoadVars(commId, owner);
            bool changed = false;
            foreach (int s in slots) changed |= vars.Remove(s);
            if (changed) LocalPsn.WriteJson(VarPath(commId, owner), vars, NpJson.Default.SortedDictionaryInt32TusVariable);
        }
    }

    string DataPath(string commId, string owner, int slot) => Path.Combine(OwnerDir(commId, owner), $"data_{slot}.bin");
    string DataStatusPath(string commId, string owner, int slot) => Path.Combine(OwnerDir(commId, owner), $"data_{slot}.json");

    public TusDataStatus? GetDataStatus(string commId, string owner, int slot)
    {
        lock (_lock)
            return File.Exists(DataPath(commId, owner, slot))
                ? LocalPsn.ReadJson(DataStatusPath(commId, owner, slot), NpJson.Default.TusDataStatus) : null;
    }

    public (TusDataStatus Status, byte[] Data)? GetData(string commId, string owner, int slot)
    {
        lock (_lock)
        {
            var st = GetDataStatus(commId, owner, slot);
            if (st == null) return null;
            try { return (st, File.ReadAllBytes(DataPath(commId, owner, slot))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public void SetData(string commId, string owner, int slot, byte[] data, byte[] info, string author)
    {
        lock (_lock)
        {
            string path = DataPath(commId, owner, slot);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path + ".tmp", data);
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"PSN (local): could not write {path}: {e.Message}");
                return;
            }
            var st = new TusDataStatus { LastChanged = LocalPsn.NowTick(), Author = author, Info = info.Length > MaxDataInfoSize ? info[..MaxDataInfoSize] : info, Size = data.Length };
            LocalPsn.WriteJson(DataStatusPath(commId, owner, slot), st, NpJson.Default.TusDataStatus);
        }
    }

    public void DeleteData(string commId, string owner, IEnumerable<int> slots)
    {
        lock (_lock)
            foreach (int s in slots)
            {
                try
                {
                    File.Delete(DataPath(commId, owner, s));
                    File.Delete(DataStatusPath(commId, owner, s));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"PSN (local): {e.Message}"); }
            }
    }
}

/// <summary>One ranking entry (best score of an online ID and player character on a board).</summary>
public sealed class ScoreEntry
{
    public string OnlineId { get; set; } = "";
    public int PcId { get; set; }
    public long Score { get; set; }
    public string Comment { get; set; } = "";
    public byte[] GameInfo { get; set; } = [];
    public byte[]? GameData { get; set; }
    public ulong RecordDate { get; set; }
    /// <summary>Best rank this entry ever had (SceNpScoreRankData.highestRank).</summary>
    public uint HighestRank { get; set; }
}

/// <summary>A board file: settings the real server keeps per board, then the entries.</summary>
public sealed class ScoreBoard
{
    /// <summary>SCE_NP_SCORE_SORT_MODE: 0 = descending (higher is better, the default), 1 = ascending (times).
    /// The real value lives on Sony's server; edit the file to change it.</summary>
    public uint SortMode { get; set; }
    /// <summary>SCE_NP_SCORE_UPDATE_MODE: 0 = keep the best score, 1 = always keep the latest.</summary>
    public uint UpdateMode { get; set; }
    public uint RankLimit { get; set; } = 1000;
    public List<ScoreEntry> Entries { get; set; } = new();
}

/// <summary>A ranked view of an entry.</summary>
public readonly record struct RankedScore(ScoreEntry Entry, uint SerialRank, uint Rank);

/// <summary>Score rankings: one board file per title and board id; entries ranked by score (ties: earlier first).</summary>
public sealed class ScoreStore(string root)
{
    readonly object _lock = new();

    string BoardPath(string commId, uint board) => Path.Combine(root, LocalPsn.SafeName(commId), $"board_{board}.json");

    ScoreBoard Load(string commId, uint board) => LocalPsn.ReadJson(BoardPath(commId, board), NpJson.Default.ScoreBoard) ?? new();

    public ScoreBoard GetBoard(string commId, uint board) { lock (_lock) return Load(commId, board); }

    static List<RankedScore> Rank(ScoreBoard b)
    {
        var sorted = (b.SortMode == 1 ? b.Entries.OrderBy(e => e.Score) : b.Entries.OrderByDescending(e => e.Score))
            .ThenBy(e => e.RecordDate).ToList();
        var list = new List<RankedScore>(sorted.Count);
        uint rank = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (i == 0 || sorted[i].Score != sorted[i - 1].Score) rank = (uint)i + 1; // ties share a rank ("1224" ranking)
            list.Add(new RankedScore(sorted[i], (uint)i + 1, rank));
        }
        return list;
    }

    bool Better(ScoreBoard b, long a, long than) => b.SortMode == 1 ? a < than : a > than;

    /// <summary>
    /// sceNpScoreRecordScore. Returns false (NOT_BEST_SCORE) when the board keeps best scores and the entry already has a
    /// score at least as good; otherwise stores it and returns its provisional rank.
    /// </summary>
    public bool Record(string commId, uint board, string onlineId, int pcId, long score, string comment, byte[] gameInfo, out uint tmpRank)
    {
        lock (_lock)
        {
            var b = Load(commId, board);
            var e = b.Entries.FirstOrDefault(x => x.OnlineId == onlineId && x.PcId == pcId);
            if (e != null && b.UpdateMode == 0 && !Better(b, score, e.Score))
            {
                tmpRank = Rank(b).First(r => r.Entry == e).Rank;
                return false;
            }
            if (e == null) b.Entries.Add(e = new ScoreEntry { OnlineId = onlineId, PcId = pcId });
            e.Score = score;
            e.Comment = comment;
            e.GameInfo = gameInfo;
            e.GameData = null; // game data belongs to the score it was recorded with
            e.RecordDate = LocalPsn.NowTick();
            var ranked = Rank(b);
            tmpRank = ranked.First(r => r.Entry == e).Rank;
            foreach (var r in ranked)
                if (r.Entry.HighestRank == 0 || r.Rank < r.Entry.HighestRank) r.Entry.HighestRank = r.Rank;
            LocalPsn.WriteJson(BoardPath(commId, board), b, NpJson.Default.ScoreBoard);
            return true;
        }
    }

    /// <summary>Entries from serial rank <paramref name="startSerialRank"/> (1-based), at most <paramref name="count"/>.</summary>
    public (List<RankedScore> Entries, uint Total, ulong LastSortDate) GetRange(string commId, uint board, uint startSerialRank, int count)
    {
        lock (_lock)
        {
            var b = Load(commId, board);
            var ranked = Rank(b);
            var page = ranked.Skip((int)Math.Max(startSerialRank, 1) - 1).Take(count).ToList();
            return (page, (uint)ranked.Count, ranked.Count == 0 ? 0 : ranked.Max(r => r.Entry.RecordDate));
        }
    }

    /// <summary>The ranked entry of each (online id, pcId) asked for (null when it has no score).</summary>
    public (List<RankedScore?> Entries, uint Total, ulong LastSortDate) GetByIds(string commId, uint board, IReadOnlyList<(string OnlineId, int PcId)> ids)
    {
        lock (_lock)
        {
            var ranked = Rank(Load(commId, board));
            var list = ids.Select(id => ranked.Cast<RankedScore?>().FirstOrDefault(r => r!.Value.Entry.OnlineId == id.OnlineId && r.Value.Entry.PcId == id.PcId)).ToList();
            return (list, (uint)ranked.Count, ranked.Count == 0 ? 0 : ranked.Max(r => r.Entry.RecordDate));
        }
    }

    /// <summary>sceNpScoreRecordGameData: attaches a blob to the entry's current score (false: no such score).</summary>
    public bool RecordGameData(string commId, uint board, string onlineId, int pcId, long score, byte[] data)
    {
        lock (_lock)
        {
            var b = Load(commId, board);
            var e = b.Entries.FirstOrDefault(x => x.OnlineId == onlineId && x.PcId == pcId && x.Score == score);
            if (e == null) return false;
            e.GameData = data;
            LocalPsn.WriteJson(BoardPath(commId, board), b, NpJson.Default.ScoreBoard);
            return true;
        }
    }

    public byte[]? GetGameData(string commId, uint board, string onlineId, int pcId)
    {
        lock (_lock) return Load(commId, board).Entries.FirstOrDefault(x => x.OnlineId == onlineId && x.PcId == pcId)?.GameData;
    }
}

/// <summary>Source-generated JSON metadata of the local PSN stores (no reflection under Native AOT).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SortedDictionary<int, TusVariable>))]
[JsonSerializable(typeof(TusDataStatus))]
[JsonSerializable(typeof(ScoreBoard))]
internal sealed partial class NpJson : JsonSerializerContext;
