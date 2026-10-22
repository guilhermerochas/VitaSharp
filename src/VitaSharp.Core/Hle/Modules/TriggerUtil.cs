using System.Text.Json;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceTriggerUtil: schedules automatic app starts (daily or one-time events, at most 6 per app). Nothing is ever
/// triggered here; the registrations are kept (and persisted in &lt;FsRoot&gt;/ux0/vitasharp/trigger/&lt;TITLEID&gt;.json)
/// so the queries return what the app registered. Structures from psp2/triggerutil.h.
/// </summary>
public sealed class TriggerUtil : HleModule
{
    internal const int ERR_BUSY = unchecked((int)0x80103600), ERR_NOT_FOUND_USER = unchecked((int)0x80103611),
        ERR_NOT_FOUND_SYSTEM = unchecked((int)0x80103614), ERR_NOT_REGISTERED = unchecked((int)0x80103621),
        ERR_EVENT_TYPE_MISMATCH = unchecked((int)0x80103624), ERR_INVALID_ARG = unchecked((int)0x80103660);
    /// <summary>sizeof(SceTriggerUtilEventParamDaily) / sizeof(SceTriggerUtilEventParamOneTime).</summary>
    internal const int DailyParamSize = 0x50, OneTimeParamSize = 0x58;
    internal const int UserAppInfoSize = 0x46A, MaxEventsPerApp = 6;
    /// <summary>Width of one entry of the registered title id lists (9 characters + NUL; inferred).</summary>
    internal const int TitleIdEntrySize = 10;

    internal sealed class Event
    {
        public bool Daily { get; set; }
        public string TitleId { get; set; } = "";
        public byte[] Param { get; set; } = [];
    }

    readonly object _lock = new();
    SortedDictionary<int, Event>? _events;

    /// <summary>Test hooks: filesystem root, title id and title name used instead of the emulator's.</summary>
    internal string? FsRootOverride { get; set; }
    internal string? TitleIdOverride { get; set; }
    internal string? TitleOverride { get; set; }
    string FsRoot => FsRootOverride ?? Emu.Options.FsRoot;
    string CurrentTitleId => TitleIdOverride ?? Emu.TitleId;
    string CurrentTitle => TitleOverride ?? Emu.Title;
    internal string StorePath => Path.Combine(FsRoot, "ux0", "vitasharp", "trigger", $"{CurrentTitleId}.json");

    internal IReadOnlyDictionary<int, Event> Events { get { lock (_lock) return new Dictionary<int, Event>(Load()); } }

    SortedDictionary<int, Event> Load()
    {
        if (_events != null) return _events;
        _events = new();
        try
        {
            if (File.Exists(StorePath))
                _events = JsonSerializer.Deserialize(File.ReadAllText(StorePath), CoreJson.Default.SortedDictionaryInt32Event) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"SceTriggerUtil: cannot read {StorePath}: {e.Message}");
        }
        return _events;
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_events, CoreJson.Default.SortedDictionaryInt32Event));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"SceTriggerUtil: cannot write {StorePath}: {e.Message}");
        }
    }

    string TitleOrCurrent(uint titleIdPtr) =>
        titleIdPtr != 0 && GuestMemory.ReadCString(titleIdPtr, 16) is { Length: > 0 } t ? t : CurrentTitleId;

    int Register(uint titleIdPtr, uint param, int eventId, bool daily)
    {
        if (param == 0) return ERR_INVALID_ARG;
        if (daily && (GuestMemory.Read<ushort>(param + 0x0C) & 0x7F) == 0) return ERR_INVALID_ARG; // repeatDays
        string title = TitleOrCurrent(titleIdPtr);
        var data = GuestMemory.Span(param, daily ? DailyParamSize : OneTimeParamSize).ToArray();
        lock (_lock)
        {
            var events = Load();
            if (!events.ContainsKey(eventId) && events.Values.Count(e => e.TitleId.Equals(title, StringComparison.OrdinalIgnoreCase)) >= MaxEventsPerApp)
                return ERR_INVALID_ARG;
            events[eventId] = new Event { Daily = daily, TitleId = title, Param = data };
            Save();
        }
        Log.Info($"sceTriggerUtilRegister{(daily ? "Daily" : "OneTime")}Event({title}, id {eventId})");
        return 0;
    }

    [Hle]
    int sceTriggerUtilRegisterDailyEvent(uint titleId, uint param, int eventId, int a4, int a5) => Register(titleId, param, eventId, true);

    [Hle]
    int sceTriggerUtilRegisterOneTimeEvent(uint titleId, uint param, int eventId, int a4, int a5) => Register(titleId, param, eventId, false);

    /// <summary>Finds a registration of the given kind: NOT_REGISTERED or EVENT_TYPE_MISMATCH otherwise.</summary>
    int Find(int eventId, bool daily, out Event? ev)
    {
        if (!Load().TryGetValue(eventId, out ev)) return ERR_NOT_REGISTERED;
        return ev.Daily == daily ? 0 : ERR_EVENT_TYPE_MISMATCH;
    }

    int Unregister(int eventId, bool daily)
    {
        lock (_lock)
        {
            int r = Find(eventId, daily, out _);
            if (r < 0) return r;
            _events!.Remove(eventId);
            Save();
        }
        return 0;
    }

    [Hle] int sceTriggerUtilUnregisterDailyEvent(int eventId, int a2, int a3) => Unregister(eventId, true);
    [Hle] int sceTriggerUtilUnregisterOneTimeEvent(int eventId, int a2, int a3) => Unregister(eventId, false);

    [Hle]
    int sceTriggerUtilGetDailyEventInfo(int eventId, uint param, int a5, int a6)
    {
        if (param == 0) return ERR_INVALID_ARG;
        lock (_lock)
        {
            int r = Find(eventId, true, out var ev);
            if (r < 0) return r;
            ev!.Param.AsSpan(0, Math.Min(ev.Param.Length, DailyParamSize)).CopyTo(GuestMemory.Span(param, DailyParamSize));
        }
        return 0;
    }

    /// <summary>Returns the registered triggerTime (SceRtcTick at +8 of the one-time param).</summary>
    [Hle]
    int sceTriggerUtilGetOneTimeEventInfo(int eventId, uint triggerTime, int a4, int a5)
    {
        if (triggerTime == 0) return ERR_INVALID_ARG;
        lock (_lock)
        {
            int r = Find(eventId, false, out var ev);
            if (r < 0) return r;
            GuestMemory.Write(triggerTime, ev!.Param.Length >= 16 ? BitConverter.ToUInt64(ev.Param, 8) : 0ul);
        }
        return 0;
    }

    /// <summary>The app was never started by a trigger.</summary>
    [Hle]
    int sceTriggerUtilGetAutoStartStatus(uint status, int a2, int a3)
    {
        if (status == 0) return ERR_INVALID_ARG;
        GuestMemory.W32(status, 0);
        return 0;
    }

    /// <summary>SceTriggerUtilUserAppInfo (0x46A): name (UTF-16[0x34]), iconPath[0x400], unk. Known for the running
    /// title and for titles installed under ux0:app.</summary>
    [Hle]
    int sceTriggerUtilGetUserAppInfo(uint titleIdPtr, uint appInfo, int a4, int a5)
    {
        if (titleIdPtr == 0 || appInfo == 0) return ERR_INVALID_ARG;
        string id = GuestMemory.ReadCString(titleIdPtr, 16);
        if (id.Length == 0) return ERR_INVALID_ARG;
        string? name = null;
        if (id.Equals(CurrentTitleId, StringComparison.OrdinalIgnoreCase)) name = CurrentTitle;
        else
        {
            var sfo = Path.Combine(FsRoot, "ux0", "app", id, "sce_sys", "param.sfo");
            try { if (File.Exists(sfo)) name = Sfo.Load(sfo).Title; }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { name = id; }
            if (name == null && Directory.Exists(Path.Combine(FsRoot, "ux0", "app", id))) name = id;
        }
        if (name == null) return ERR_NOT_FOUND_USER;
        GuestMemory.Fill(appInfo, 0, UserAppInfoSize);
        CommonDialog.WriteUtf16(appInfo, name, 0x34 - 1);
        GuestMemory.WriteCString(appInfo + 0x68, $"ux0:app/{id}/sce_sys/icon0.png", 0x400);
        return 0;
    }

    /// <summary>There are no system applications.</summary>
    [Hle] int sceTriggerUtilGetSystemAppInfo(uint titleId, uint appInfo, int a4, int a5) => ERR_NOT_FOUND_SYSTEM;

    /// <summary>Writes the distinct title ids with registered events (10-byte entries) and returns how many.</summary>
    [Hle]
    int sceTriggerUtilGetRegisteredUserTitleIdList(uint buffer, int numOfIds)
    {
        if (buffer == 0 || numOfIds < 0) return ERR_INVALID_ARG;
        List<string> ids;
        lock (_lock) ids = Load().Values.Select(e => e.TitleId).Distinct(StringComparer.OrdinalIgnoreCase).Take(numOfIds).ToList();
        for (int i = 0; i < ids.Count; i++)
        {
            uint e = buffer + (uint)(i * TitleIdEntrySize);
            GuestMemory.Fill(e, 0, TitleIdEntrySize);
            GuestMemory.WriteCString(e, ids[i], TitleIdEntrySize);
        }
        return ids.Count;
    }

    [Hle] int sceTriggerUtilGetRegisteredSystemTitleIdList(uint buffer, int numOfIds) => 0;
}
