using System.Text;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceNpTrophy: trophy information comes from the game's own app0:sce_sys/trophy/&lt;NPCOMMID&gt;/TROPHY.TRP
/// (<see cref="TrophyPackage"/>: TROPCONF/TROP*.SFM XML plus PNG icons). Unlocks are recorded locally in
/// ux0:user/00/trophy/&lt;NPCOMMID&gt;.json (<see cref="TrophyUnlocks"/>, read by the launcher's trophy list) and announced
/// through <see cref="TrophyNotifications"/> (the emulator window shows them).
/// </summary>
public sealed class NpTrophy : HleModule
{
    const int ERR_NOT_INITIALIZED = unchecked((int)0x80551601), ERR_INVALID_ARGUMENT = unchecked((int)0x80551604),
        ERR_INSUFFICIENT_BUFFER = unchecked((int)0x80551605), ERR_INVALID_HANDLE = unchecked((int)0x80551608),
        ERR_INVALID_CONTEXT = unchecked((int)0x80551609), ERR_INVALID_GROUP_ID = unchecked((int)0x8055160d),
        ERR_INVALID_TROPHY_ID = unchecked((int)0x8055160e), ERR_ALREADY_UNLOCKED = unchecked((int)0x8055160f),
        ERR_PLATINUM_CANNOT_UNLOCK = unchecked((int)0x80551610), ERR_ICON_NOT_FOUND = unchecked((int)0x80551618),
        ERR_TRP_NOT_FOUND = unchecked((int)0x80551619);

    const uint GameDetailsSize = 4 * 7 + 128 + 1024, GameDataSize = 4 * 7;
    const uint GroupDetailsSize = 4 * 7 + 128 + 1024, GroupDataSize = 4 * 8;
    const uint TrophyDetailsSize = 4 * 5 + 128 + 1024, TrophyDataSize = 4 * 3 + 4 + 8;

    sealed class TrophySet(TrophyPackage pkg)
    {
        public readonly TrophyPackage Pkg = pkg;
        public string CommId => Pkg.CommId;
        public string Title => Pkg.Title;
        public string Detail => Pkg.Detail;
        public List<TrophyInfo> Trophies => Pkg.Trophies;
        public List<TrophyGroupInfo> Groups => Pkg.Groups;
        public Dictionary<string, byte[]> Files => Pkg.Files;
        public Dictionary<int, ulong> Unlocked = new(); // trophy id -> SceRtcTick (µs since 0001-01-01)
        public string SavePath = "";
    }

    bool _initialized;
    readonly Dictionary<int, TrophySet> _contexts = new();
    readonly HashSet<int> _handles = new();
    int _nextContext = 1, _nextHandle = 1;

    [Hle] int sceNpTrophyInit(uint opt) { _initialized = true; return 0; }
    [Hle] int sceNpTrophyTerm() { _initialized = false; return 0; }

    [Hle]
    int sceNpTrophyCreateHandle(Ptr<int> handle)
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        if (handle.Address == 0) return ERR_INVALID_ARGUMENT;
        lock (_handles) { handle.Value = _nextHandle; _handles.Add(_nextHandle++); }
        return 0;
    }

    [Hle] int sceNpTrophyDestroyHandle(int h) { lock (_handles) return _handles.Remove(h) ? 0 : ERR_INVALID_HANDLE; }
    [Hle] int sceNpTrophyAbortHandle(int h) { lock (_handles) return _handles.Contains(h) ? 0 : ERR_INVALID_HANDLE; }

    [Hle]
    int sceNpTrophyCreateContext(Ptr<int> ctx, uint commId, uint commSign, ulong options)
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        if (ctx.Address == 0) return ERR_INVALID_ARGUMENT;
        // SceNpCommunicationId: char data[9]; char term; uint8 num; char dummy. NULL = the application's default
        // communication id (Vita3K uses the one given to sceNpInit): the trophy set the game ships in app0:sce_sys/trophy.
        string? id = commId != 0 ? $"{GuestMemory.ReadCString(commId, 9)}_{GuestMemory.Read<byte>(commId + 10):D2}" : DefaultCommId();
        var set = id != null ? Load(id) : null;
        if (set == null) return ERR_TRP_NOT_FOUND;
        lock (_contexts)
        {
            ctx.Value = _nextContext;
            _contexts[_nextContext++] = set;
        }
        return 0;
    }

    string? DefaultCommId()
    {
        var dir = Emu.Vfs.Resolve("app0:sce_sys/trophy");
        return dir != null && Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(Path.GetFileName).Order().FirstOrDefault() : null;
    }

    [Hle] int sceNpTrophyDestroyContext(int ctx) { lock (_contexts) return _contexts.Remove(ctx) ? 0 : ERR_INVALID_CONTEXT; }

    TrophySet? Load(string commId)
    {
        var trp = Emu.Vfs.Resolve($"app0:sce_sys/trophy/{commId}/TROPHY.TRP");
        if (trp == null || !File.Exists(trp))
        {
            Log.Warn($"Trophy: {commId}/TROPHY.TRP not found");
            return null;
        }
        // texts in the system language when the set has that translation (TROP_<lang>.SFM)
        var pkg = TrophyPackage.Load(trp, commId, AppUtil.SystemParamInt(1));
        if (pkg == null) return null;
        var set = new TrophySet(pkg) { SavePath = TrophyUnlocks.PathFor(Emu.Options.FsRoot, commId) };
        set.Unlocked = TrophyUnlocks.Load(set.SavePath);
        Log.Info($"Trophy set {commId} \"{set.Title}\": {set.Trophies.Count} trophies, {set.Unlocked.Count} unlocked");
        return set;
    }

    static void Save(TrophySet set) => TrophyUnlocks.Save(set.SavePath, set.Unlocked);

    int Lookup(int ctx, int handle, out TrophySet set)
    {
        set = null!;
        if (!_initialized) return ERR_NOT_INITIALIZED;
        lock (_handles) if (!_handles.Contains(handle)) return ERR_INVALID_HANDLE;
        lock (_contexts) if (!_contexts.TryGetValue(ctx, out set!)) return ERR_INVALID_CONTEXT;
        return 0;
    }

    static ulong NowTick() => LocalPsn.NowTick();

    [Hle]
    int sceNpTrophyUnlockTrophy(int ctx, int handle, int trophyId, Ptr<int> platinumId)
    {
        int r = Lookup(ctx, handle, out var set);
        if (r != 0) return r;
        var t = set.Trophies.FirstOrDefault(x => x.Id == trophyId);
        if (t == null) return ERR_INVALID_TROPHY_ID;
        if (t.Grade == TrophyGrade.Platinum) return ERR_PLATINUM_CANNOT_UNLOCK;
        platinumId.TrySet(-1);
        var unlocked = new List<TrophyInfo>(2);
        lock (set)
        {
            if (set.Unlocked.ContainsKey(trophyId)) return ERR_ALREADY_UNLOCKED;
            set.Unlocked[trophyId] = NowTick();
            unlocked.Add(t);
            Log.Info($"Trophy unlocked: \"{t.Name}\" ({trophyId}, {t.Grade})");
            // the platinum unlocks itself once every trophy linked to it is unlocked
            var plat = set.Trophies.FirstOrDefault(x => x.Grade == TrophyGrade.Platinum);
            if (plat != null && !set.Unlocked.ContainsKey(plat.Id)
                && set.Trophies.Where(x => x.PlatinumLink == plat.Id).All(x => set.Unlocked.ContainsKey(x.Id)))
            {
                set.Unlocked[plat.Id] = NowTick();
                platinumId.TrySet(plat.Id);
                unlocked.Add(plat);
                Log.Info($"Trophy unlocked: \"{plat.Name}\" (platinum)");
            }
            Save(set);
        }
        foreach (var u in unlocked) TrophyNotifications.Raise(new TrophyUnlockedEvent(set.CommId, set.Title, u, set.Pkg.TrophyIcon(u.Id)));
        return 0;
    }

    [Hle]
    int sceNpTrophyGetTrophyUnlockState(int ctx, int handle, uint flags, Ptr<uint> count)
    {
        int r = Lookup(ctx, handle, out var set);
        if (r != 0) return r;
        if (flags == 0 || count.Address == 0) return ERR_INVALID_ARGUMENT;
        GuestMemory.Fill(flags, 0, 16); // SceNpTrophyFlagArray: 128 bits
        lock (set)
            foreach (var id in set.Unlocked.Keys.Where(i => i is >= 0 and < 128))
                GuestMemory.W32(flags + (uint)(id / 32) * 4, GuestMemory.R32(flags + (uint)(id / 32) * 4) | (1u << (id % 32)));
        count.Value = (uint)set.Trophies.Count;
        return 0;
    }

    (uint Plat, uint Gold, uint Silver, uint Bronze) Grades(IEnumerable<TrophyInfo> ts) =>
        ((uint)ts.Count(t => t.Grade == TrophyGrade.Platinum), (uint)ts.Count(t => t.Grade == TrophyGrade.Gold), (uint)ts.Count(t => t.Grade == TrophyGrade.Silver), (uint)ts.Count(t => t.Grade == TrophyGrade.Bronze));

    uint Progress(TrophySet set, IEnumerable<TrophyInfo> ts)
    {
        var list = ts.ToList();
        return list.Count == 0 ? 0 : (uint)(list.Count(t => set.Unlocked.ContainsKey(t.Id)) * 100 / list.Count);
    }

    [Hle]
    int sceNpTrophyGetGameInfo(int ctx, int handle, uint details, uint data)
    {
        int r = Lookup(ctx, handle, out var set);
        if (r != 0) return r;
        if ((details == 0 && data == 0) || (details != 0 && GuestMemory.R32(details) != GameDetailsSize)
            || (data != 0 && GuestMemory.R32(data) != GameDataSize)) return ERR_INVALID_ARGUMENT;
        lock (set)
        {
            if (details != 0)
            {
                var g = Grades(set.Trophies);
                uint[] v = [(uint)set.Groups.Count, (uint)set.Trophies.Count, g.Plat, g.Gold, g.Silver, g.Bronze];
                for (int i = 0; i < v.Length; i++) GuestMemory.W32(details + 4 + (uint)i * 4, v[i]);
                WriteString(details + 28, set.Title, 128);
                WriteString(details + 28 + 128, set.Detail, 1024);
            }
            if (data != 0)
            {
                var u = Grades(set.Trophies.Where(t => set.Unlocked.ContainsKey(t.Id)));
                uint[] v = [(uint)set.Unlocked.Count, u.Plat, u.Gold, u.Silver, u.Bronze, Progress(set, set.Trophies)];
                for (int i = 0; i < v.Length; i++) GuestMemory.W32(data + 4 + (uint)i * 4, v[i]);
            }
        }
        return 0;
    }

    [Hle]
    int sceNpTrophyGetGroupInfo(int ctx, int handle, int groupId, uint details, uint data)
    {
        int r = Lookup(ctx, handle, out var set);
        if (r != 0) return r;
        if ((details == 0 && data == 0) || (details != 0 && GuestMemory.R32(details) != GroupDetailsSize)
            || (data != 0 && GuestMemory.R32(data) != GroupDataSize)) return ERR_INVALID_ARGUMENT;
        // group -1 is the base game (trophies without a gid)
        var group = set.Groups.FirstOrDefault(g => g.Id == groupId);
        if (groupId != -1 && group == null) return ERR_INVALID_GROUP_ID;
        var members = set.Trophies.Where(t => t.Group == groupId).ToList();
        lock (set)
        {
            if (details != 0)
            {
                var g = Grades(members);
                uint[] v = [(uint)groupId, (uint)members.Count, g.Plat, g.Gold, g.Silver, g.Bronze];
                for (int i = 0; i < v.Length; i++) GuestMemory.W32(details + 4 + (uint)i * 4, v[i]);
                WriteString(details + 28, group?.Name ?? set.Title, 128);
                WriteString(details + 28 + 128, group?.Detail ?? set.Detail, 1024);
            }
            if (data != 0)
            {
                var unlocked = members.Where(t => set.Unlocked.ContainsKey(t.Id)).ToList();
                var u = Grades(unlocked);
                uint[] v = [(uint)groupId, (uint)unlocked.Count, u.Plat, u.Gold, u.Silver, u.Bronze, Progress(set, members)];
                for (int i = 0; i < v.Length; i++) GuestMemory.W32(data + 4 + (uint)i * 4, v[i]);
            }
        }
        return 0;
    }

    [Hle]
    int sceNpTrophyGetTrophyInfo(int ctx, int handle, int trophyId, uint details, uint data)
    {
        int r = Lookup(ctx, handle, out var set);
        if (r != 0) return r;
        if ((details == 0 && data == 0) || (details != 0 && GuestMemory.R32(details) != TrophyDetailsSize)
            || (data != 0 && GuestMemory.R32(data) != TrophyDataSize)) return ERR_INVALID_ARGUMENT;
        var t = set.Trophies.FirstOrDefault(x => x.Id == trophyId);
        if (t == null) return ERR_INVALID_TROPHY_ID;
        lock (set)
        {
            bool unlocked = set.Unlocked.TryGetValue(trophyId, out var tick);
            if (details != 0)
            {
                GuestMemory.W32(details + 4, (uint)t.Id);
                GuestMemory.W32(details + 8, (uint)t.Grade);
                GuestMemory.W32(details + 12, (uint)t.Group);
                GuestMemory.W32(details + 16, t.Hidden ? 1u : 0u);
                // hidden trophies keep their text secret until unlocked, as on the console
                WriteString(details + 20, t.Hidden && !unlocked ? "" : t.Name, 128);
                WriteString(details + 20 + 128, t.Hidden && !unlocked ? "" : t.Detail, 1024);
            }
            if (data != 0)
            {
                GuestMemory.W32(data + 4, (uint)t.Id);
                GuestMemory.W32(data + 8, unlocked ? 1u : 0u);
                GuestMemory.W32(data + 12, 0);
                GuestMemory.Write(data + 16, unlocked ? tick : 0UL);
            }
        }
        return 0;
    }

    int CopyFile(int ctx, int handle, string name, uint buffer, Ptr<uint> size)
    {
        int r = Lookup(ctx, handle, out var set);
        if (r != 0) return r;
        if (size.Address == 0) return ERR_INVALID_ARGUMENT;
        if (!set.Files.TryGetValue(name, out var file)) return ERR_ICON_NOT_FOUND;
        if (buffer == 0)
        {
            size.Value = (uint)file.Length; // size query
            return 0;
        }
        if (size.Value < file.Length) return ERR_INSUFFICIENT_BUFFER;
        file.CopyTo(GuestMemory.Span(buffer, file.Length));
        size.Value = (uint)file.Length;
        return 0;
    }

    [Hle] int sceNpTrophyGetGameIcon(int ctx, int handle, uint buffer, Ptr<uint> size) => CopyFile(ctx, handle, "ICON0.PNG", buffer, size);
    [Hle] int sceNpTrophyGetGroupIcon(int ctx, int handle, int groupId, uint buffer, Ptr<uint> size) => CopyFile(ctx, handle, $"GR{groupId:D3}.PNG", buffer, size);

    [Hle]
    int sceNpTrophyGetTrophyIcon(int ctx, int handle, int trophyId, uint buffer, Ptr<uint> size)
    {
        if (trophyId is < 0 or >= 128) return ERR_INVALID_TROPHY_ID;
        return CopyFile(ctx, handle, $"TROP{trophyId:D3}.PNG", buffer, size);
    }

    static void WriteString(uint addr, string s, int max)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        int n = Math.Min(bytes.Length, max - 1);
        bytes.AsSpan(0, n).CopyTo(GuestMemory.Span(addr, n));
        GuestMemory.Write(addr + (uint)n, (byte)0);
    }
}
