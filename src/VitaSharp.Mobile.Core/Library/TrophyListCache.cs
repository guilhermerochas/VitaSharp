using System.Text.Json;
using System.Text.Json.Serialization;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Storage;

namespace VitaSharp.Mobile.Core.Library;

/// <summary>
/// The Trofeus screen's disk cache (<see cref="DataLayout.TrophyCache"/>), one folder per trophy set (communication
/// id): the set's texts in one language (texts_LANG.json) and its pictures extracted from the TRP once (TROPnnn.PNG,
/// with icons.stamp). Each part carries the TRP's path, size and write time and is rebuilt when they change. The
/// user's progress (ux0:user/00/trophy/COMMID.json) is read again only when its size or write time changes, without
/// reading the TRP. Opening the screen again (also after the app restarts) reads no TRP and decodes nothing on the UI
/// thread: the rows point at the extracted files, which the screen loads as images (decoded on a worker thread at the
/// view's size). Thread-safe.
/// </summary>
public sealed class TrophyListCache(string directory)
{
    private const int FormatVersion = 1;
    private readonly object _lock = new();
    private readonly Dictionary<string, (long Ticks, long Length, Dictionary<int, ulong> Unlocked)> _unlocks = new();
    private readonly Dictionary<(string CommId, int Language), (FileStamp Stamp, CachedSet Set)> _sets = new();

    public string Directory { get; } = directory;

    /// <summary>TRPs read for their texts (a set missing from the cache or changed).</summary>
    public int TrpReads { get; private set; }

    /// <summary>Sets whose pictures were extracted from the TRP.</summary>
    public int IconExtractions { get; private set; }

    /// <summary>Progress files read (the first time, or after the user's progress changed).</summary>
    public int UnlockReads { get; private set; }

    /// <summary>Sets taken from the cache files on disk (an app restart).</summary>
    public int DiskHits { get; private set; }

    /// <summary>The game's list: every set it ships, from the cache when nothing changed.</summary>
    public TrophyList Load(GameEntry game, DataLayout data, int language = -1)
    {
        var rows = new List<TrophyRow>();
        string title = game.Title;
        foreach ((string commId, string trp) in TrophyPackage.FindSets(game.AppDir))
        {
            CachedSet? set = Set(commId, trp, language);
            if (set == null)
            {
                continue;
            }

            if (set.Title.Length > 0)
            {
                title = set.Title;
            }

            Dictionary<int, ulong> unlocked = Unlocks(TrophyUnlocks.PathFor(data.Fs, commId));
            string setDir = SetDirectory(commId);
            foreach (CachedTrophy trophy in set.Trophies)
            {
                DateTime? unlockedUtc = unlocked.TryGetValue(trophy.Id, out ulong tick) ? TrophyUnlocks.TickToUtc(tick) : null;
                string? iconFile = trophy.HasIcon ? Path.Combine(setDir, IconName(trophy.Id)) : null;
                rows.Add(new TrophyRow(commId, trophy.Id, (TrophyGrade)trophy.Grade, trophy.Name, trophy.Detail, trophy.Hidden, unlockedUtc, null, iconFile));
            }
        }

        return TrophyList.FromRows(title, rows);
    }

    /// <summary>Every set of the game was loaded by this cache in this language (a <see cref="Load"/> now only checks
    /// the files' sizes and dates: quick enough for the UI thread).</summary>
    public bool IsLoaded(GameEntry game, int language = -1)
    {
        bool any = false;
        foreach ((string commId, _) in TrophyPackage.FindSets(game.AppDir))
        {
            any = true;
            lock (_lock)
            {
                if (!_sets.ContainsKey((commId, language)))
                {
                    return false;
                }
            }
        }

        return any;
    }

    private static string IconName(int id) => $"TROP{id:D3}.PNG";

    private string SetDirectory(string commId) => Path.Combine(Directory, Path.GetFileName(commId));

    private Dictionary<int, ulong> Unlocks(string path)
    {
        FileInfo file = new(path);
        long ticks = file.Exists ? file.LastWriteTimeUtc.Ticks : 0;
        long length = file.Exists ? file.Length : -1;
        lock (_lock)
        {
            if (_unlocks.TryGetValue(path, out (long Ticks, long Length, Dictionary<int, ulong> Unlocked) held) && held.Ticks == ticks && held.Length == length)
            {
                return held.Unlocked;
            }
        }

        Dictionary<int, ulong> unlocked = TrophyUnlocks.Load(path);
        lock (_lock)
        {
            UnlockReads++;
            _unlocks[path] = (ticks, length, unlocked);
        }

        return unlocked;
    }

    private CachedSet? Set(string commId, string trp, int language)
    {
        FileInfo file = new(trp);
        if (!file.Exists)
        {
            return null;
        }

        FileStamp stamp = new(Path.GetFullPath(trp), file.LastWriteTimeUtc.Ticks, file.Length);
        lock (_lock)
        {
            if (_sets.TryGetValue((commId, language), out (FileStamp Stamp, CachedSet Set) held) && held.Stamp == stamp)
            {
                return held.Set;
            }
        }

        string setDir = SetDirectory(commId);
        string textsFile = Path.Combine(setDir, $"texts_{language}.json");
        CachedSet? set = ReadCached(textsFile, stamp);
        bool fromDisk = set != null && IconsMatch(setDir, stamp, set);
        if (!fromDisk)
        {
            set = Build(commId, trp, language, stamp, setDir, textsFile);
            if (set == null)
            {
                return null;
            }
        }

        lock (_lock)
        {
            if (fromDisk)
            {
                DiskHits++;
            }

            _sets[(commId, language)] = (stamp, set!);
        }

        return set;
    }

    private static CachedSet? ReadCached(string textsFile, FileStamp stamp)
    {
        try
        {
            if (!File.Exists(textsFile))
            {
                return null;
            }

            CachedSet? set = JsonSerializer.Deserialize(File.ReadAllText(textsFile), TrophyCacheJson.Default.CachedSet);
            bool current = set != null && set.Version == FormatVersion && set.TrpPath == stamp.Path && set.TrpTicks == stamp.Ticks && set.TrpLength == stamp.Length;
            return current ? set : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The pictures on disk come from this TRP (icons.stamp) and none of the set's is missing.</summary>
    private static bool IconsMatch(string setDir, FileStamp stamp, CachedSet set)
    {
        try
        {
            string stampFile = Path.Combine(setDir, "icons.stamp");
            if (!File.Exists(stampFile) || File.ReadAllText(stampFile) != stamp.Text)
            {
                return false;
            }

            foreach (CachedTrophy trophy in set.Trophies)
            {
                if (trophy.HasIcon && !File.Exists(Path.Combine(setDir, IconName(trophy.Id))))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private CachedSet? Build(string commId, string trp, int language, FileStamp stamp, string setDir, string textsFile)
    {
        var package = TrophyPackage.LoadTexts(trp, commId, language);
        lock (_lock)
        {
            TrpReads++;
        }

        if (package == null)
        {
            return null;
        }

        var set = new CachedSet
        {
            Version = FormatVersion,
            TrpPath = stamp.Path,
            TrpTicks = stamp.Ticks,
            TrpLength = stamp.Length,
            Language = language,
            Title = package.Title,
        };
        foreach (TrophyInfo trophy in package.Trophies)
        {
            set.Trophies.Add(new CachedTrophy
            {
                Id = trophy.Id,
                Grade = (int)trophy.Grade,
                Name = trophy.Name,
                Detail = trophy.Detail,
                Hidden = trophy.Hidden,
                HasIcon = package.HasTrophyIcon(trophy.Id),
            });
        }

        try
        {
            System.IO.Directory.CreateDirectory(setDir);
            if (!IconsMatch(setDir, stamp, set))
            {
                ExtractIcons(package, setDir, stamp);
            }

            string tmp = textsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(set, TrophyCacheJson.Default.CachedSet));
            File.Move(tmp, textsFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the list still shows (without a cache file the next opening builds it again); pictures missing on disk
            // are dropped from the rows
            Log.Warn($"Trophy cache: {setDir}: {e.Message}");
            foreach (CachedTrophy trophy in set.Trophies)
            {
                trophy.HasIcon = trophy.HasIcon && File.Exists(Path.Combine(setDir, IconName(trophy.Id)));
            }
        }

        return set;
    }

    private void ExtractIcons(TrophyPackage package, string setDir, FileStamp stamp)
    {
        string stampFile = Path.Combine(setDir, "icons.stamp");
        File.Delete(stampFile);
        foreach ((string name, byte[] bytes) in package.ReadEntries(IsTrophyIcon))
        {
            File.WriteAllBytes(Path.Combine(setDir, name.ToUpperInvariant()), bytes);
        }

        File.WriteAllText(stampFile, stamp.Text);
        lock (_lock)
        {
            IconExtractions++;
        }
    }

    // the TRP format names a trophy's picture TROPnnn.PNG (fixed by the format, see TrophyPackage.TrophyIcon)
    private static bool IsTrophyIcon(string name) =>
        name.Length == 11 && name.StartsWith("TROP", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase);

    private readonly record struct FileStamp(string Path, long Ticks, long Length)
    {
        public string Text => $"{FormatVersion}|{Path}|{Ticks}|{Length}";
    }
}

/// <summary>A set's texts in one language, as written to texts_LANG.json.</summary>
internal sealed class CachedSet
{
    public int Version { get; set; }
    public string TrpPath { get; set; } = "";
    public long TrpTicks { get; set; }
    public long TrpLength { get; set; }
    public int Language { get; set; }
    public string Title { get; set; } = "";
    public List<CachedTrophy> Trophies { get; set; } = new();
}

internal sealed class CachedTrophy
{
    public int Id { get; set; }
    public int Grade { get; set; }
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Hidden { get; set; }
    public bool HasIcon { get; set; }
}

/// <summary>Source-generated JSON metadata (trimmed/AOT Android builds have no reflection-based serializer).</summary>
[JsonSerializable(typeof(CachedSet))]
internal sealed partial class TrophyCacheJson : JsonSerializerContext;
