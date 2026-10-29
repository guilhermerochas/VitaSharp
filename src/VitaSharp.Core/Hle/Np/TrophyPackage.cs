using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Np;

/// <summary>SceNpTrophyGrade: PLATINUM 1, GOLD 2, SILVER 3, BRONZE 4.</summary>
public enum TrophyGrade { Unknown = 0, Platinum = 1, Gold = 2, Silver = 3, Bronze = 4 }

public sealed record TrophyInfo(int Id, TrophyGrade Grade, bool Hidden, int PlatinumLink, int Group, string Name, string Detail);
public sealed record TrophyGroupInfo(int Id, string Name, string Detail);

/// <summary>
/// A game's trophy set: app0:sce_sys/trophy/&lt;NPCOMMID&gt;/TROPHY.TRP. The TRP is a flat archive (big-endian header:
/// magic 0xDCA24D00, version, total size, entry count +0x10, entry size +0x14; entries from 0x40: name[32], offset u64,
/// size u64, flags) holding TROPCONF.SFM (structure), TROP.SFM / TROP_&lt;lang&gt;.SFM (XML with the texts) and the PNG
/// icons (ICON0.PNG, GRxxx.PNG, TROPxxx.PNG). The SFM files of Vita sets are plain XML preceded by a signature comment.
/// </summary>
public sealed class TrophyPackage
{
    public string CommId { get; init; } = "";
    public string Title { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public List<TrophyInfo> Trophies { get; } = new();
    public List<TrophyGroupInfo> Groups { get; } = new();
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public const uint Magic = 0x004DA2DC; // DC A2 4D 00 read little-endian

    /// <summary>Parses a TRP; <paramref name="language"/> (SceSystemParamLang, -1 = default) picks TROP_&lt;nn&gt;.SFM when the
    /// set has that translation. Throws <see cref="InvalidDataException"/> on a malformed file.</summary>
    public static TrophyPackage Parse(byte[] data, string commId = "", int language = -1)
    {
        if (data.Length < 0x40 || BitConverter.ToUInt32(data, 0) != Magic) throw new InvalidDataException("bad TRP magic");
        var set = new TrophyPackage { CommId = commId };
        int count = (int)BinaryHelpers.U32BE(data, 0x10), entrySize = (int)BinaryHelpers.U32BE(data, 0x14);
        if (count < 0 || count > 4096 || entrySize < 0x30) throw new InvalidDataException("bad TRP header");
        for (int i = 0; i < count; i++)
        {
            int e = 0x40 + i * entrySize;
            if (e + 0x30 > data.Length) throw new InvalidDataException("truncated TRP entry table");
            string name = Encoding.ASCII.GetString(data, e, 32).TrimEnd('\0');
            long off = (long)BinaryHelpers.U64BE(data, e + 0x20), len = (long)BinaryHelpers.U64BE(data, e + 0x28);
            if (off >= 0 && len >= 0 && off + len <= data.Length) set.Files[name] = data.AsSpan((int)off, (int)len).ToArray();
        }
        try { set.ParseConf(language); }
        catch (System.Xml.XmlException e) { throw new InvalidDataException(e.Message, e); }
        return set;
    }

    /// <summary>Loads a TRP file; null (with a warning) when it is missing or malformed.</summary>
    public static TrophyPackage? Load(string trpPath, string commId = "", int language = -1)
    {
        try { return Parse(File.ReadAllBytes(trpPath), commId, language); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or OverflowException)
        {
            Log.Warn($"Trophy: {trpPath}: {e.Message}");
            return null;
        }
    }

    static XElement ParseSfm(byte[] bytes)
    {
        string xml = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        // skip the signature comment and any XML declaration: parse from the root element
        int start = xml.IndexOf("<trophyconf", StringComparison.Ordinal);
        int end = xml.LastIndexOf('>');
        if (start < 0 || end < start) throw new InvalidDataException("no <trophyconf> element");
        return XDocument.Parse(xml[start..(end + 1)]).Root ?? throw new InvalidDataException("empty trophy configuration");
    }

    void ParseConf(int language)
    {
        // TROP_<lang>.SFM / TROP.SFM carry the texts; TROPCONF.SFM the structure (texts may be missing there)
        byte[]? bytes = null;
        if (language >= 0) bytes = Files.GetValueOrDefault($"TROP_{language:D2}.SFM");
        bytes ??= Files.GetValueOrDefault("TROP.SFM") ?? Files.GetValueOrDefault("TROPCONF.SFM")
                  ?? throw new InvalidDataException("no trophy configuration");
        var root = ParseSfm(bytes);
        Title = root.Element("title-name")?.Value ?? "";
        Detail = root.Element("title-detail")?.Value ?? "";
        foreach (var g in root.Elements("group"))
            if (int.TryParse(g.Attribute("id")?.Value, out int gid))
                Groups.Add(new TrophyGroupInfo(gid, g.Element("name")?.Value ?? "", g.Element("detail")?.Value ?? ""));
        foreach (var t in root.Elements("trophy"))
        {
            if (!int.TryParse(t.Attribute("id")?.Value, out int id)) continue;
            var grade = (t.Attribute("ttype")?.Value ?? "B") switch
            {
                "P" => TrophyGrade.Platinum, "G" => TrophyGrade.Gold, "S" => TrophyGrade.Silver, _ => TrophyGrade.Bronze,
            };
            Trophies.Add(new TrophyInfo(id, grade, t.Attribute("hidden")?.Value == "yes",
                int.TryParse(t.Attribute("pid")?.Value, out var pid) ? pid : -1,
                int.TryParse(t.Attribute("gid")?.Value, out var gr) ? gr : -1,
                t.Element("name")?.Value ?? "", t.Element("detail")?.Value ?? ""));
        }
    }

    public byte[]? GameIcon => Files.GetValueOrDefault("ICON0.PNG");
    public byte[]? TrophyIcon(int id) => Files.GetValueOrDefault($"TROP{id:D3}.PNG");
    public byte[]? GroupIcon(int id) => Files.GetValueOrDefault($"GR{id:D3}.PNG");

    /// <summary>The trophy sets a title ships: the folder names under app0:sce_sys/trophy (one per communication id).</summary>
    public static IEnumerable<(string CommId, string TrpPath)> FindSets(string appDir)
    {
        string dir = Io.Vfs.ResolveCaseInsensitive(appDir, "sce_sys/trophy");
        if (!Directory.Exists(dir)) yield break;
        foreach (var sub in Directory.GetDirectories(dir).Order())
        {
            string trp = Io.Vfs.ResolveCaseInsensitive(sub, "TROPHY.TRP");
            if (File.Exists(trp)) yield return (Path.GetFileName(sub), trp);
        }
    }
}

/// <summary>
/// The unlocked trophies of the emulated console's user, one JSON file per trophy set:
/// ux0:user/00/trophy/&lt;NPCOMMID&gt;.json = { trophy id: SceRtcTick of the unlock }.
/// </summary>
public static class TrophyUnlocks
{
    public static string PathFor(string fsRoot, string commId) => Path.Combine(fsRoot, "ux0", "user", "00", "trophy", commId + ".json");

    public static Dictionary<int, ulong> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.DictionaryInt32UInt64) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Trophy: could not read {path}: {e.Message}");
        }
        return new();
    }

    public static void Save(string path, Dictionary<int, ulong> unlocked)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(unlocked, CoreJson.Default.DictionaryInt32UInt64));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Trophy: could not save {path}: {e.Message}"); }
    }

    /// <summary>SceRtcTick (µs since 0001-01-01 UTC) to a UTC date.</summary>
    public static DateTime TickToUtc(ulong tick) => tick >= (ulong)(DateTime.MaxValue.Ticks / 10) ? DateTime.MaxValue : new DateTime((long)tick * 10, DateTimeKind.Utc);
}

/// <summary>A trophy unlocked by the running game (for front-end notifications).</summary>
public sealed record TrophyUnlockedEvent(string CommId, string SetTitle, TrophyInfo Trophy, byte[]? Icon = null);

/// <summary>Process-wide notification of trophy unlocks: the desktop window shows a short message.</summary>
public static class TrophyNotifications
{
    public static event Action<TrophyUnlockedEvent>? Unlocked;
    internal static void Raise(TrophyUnlockedEvent e)
    {
        try { Unlocked?.Invoke(e); }
        catch (Exception ex) { Log.Warn($"Trophy notification handler failed: {ex.Message}"); }
    }
}
