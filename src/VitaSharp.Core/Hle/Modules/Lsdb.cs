using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLsdb (vs0:vsh/shell/livespace_db.suprx, the LiveArea / application database of the shell): the parts that do
/// not need the database, read in the firmware 3.74 module. An AppInfo object is { -> { std::map header } }: the
/// map's nodes are { +4 the larger keys, +8 the smaller ones, +0x10 key, +0x18 value type (0 an int64 at +0x20, 1
/// a string whose char* is at *(+0x20)), +0x29 "nil" byte }, the header is also the end; the getters look a key up
/// (strings answer NULL when the key is missing and "" when it holds no string, integers 0). The keys are the
/// module's (0xA1541070 name, 0x21CA76B1 category, 0x19FAFFCF attribute...); the title and short title look for the
/// system language's key first. Firmware fallback only: the shell loads the real module.
/// </summary>
public sealed partial class Lsdb : HleModule
{
    public override bool FirmwareFallbackOnly => true;
    public override bool FirmwareForEveryImporter => true;

    uint _empty;
    /// <summary>The shell's content rating callback (sceLsdbSetContentRatingCallback).</summary>
    internal uint ContentRatingCallback { get; private set; }

    /// <summary>The module's empty string (rodata), for a key that holds no string.</summary>
    uint Empty
    {
        get
        {
            if (_empty == 0) { _empty = Mem.Alloc(4, "SceLsdb empty string (HLE)"); GuestMemory.W32(_empty, 0); }
            return _empty;
        }
    }

    /// <summary>The map node holding <paramref name="key"/> in an AppInfo, or 0 (no map, or not found).</summary>
    internal static uint Find(uint info, uint key)
    {
        if (info == 0) return 0;
        uint head = GuestMemory.R32(GuestMemory.R32(info));
        if (head == 0) return 0;
        uint n = GuestMemory.R32(head);
        for (int guard = 0; guard < 4096 && GuestMemory.Read<byte>(n + 0x29) == 0; guard++)
        {
            uint k = GuestMemory.R32(n + 0x10);
            if (k == key) return n;
            n = GuestMemory.R32(n + (k > key ? 4u : 8u));
        }
        return 0;
    }

    uint Str(uint info, uint key)
    {
        uint n = Find(info, key);
        if (n == 0) return 0;
        uint v = GuestMemory.R32(n + 0x20);
        return GuestMemory.R32(n + 0x18) == 1 && v != 0 ? GuestMemory.R32(v) : Empty;
    }

    static ulong Int(uint info, uint key)
    {
        uint n = Find(info, key);
        return n != 0 && GuestMemory.R32(n + 0x18) == 0 ? GuestMemory.Read<ulong>(n + 0x20) : 0;
    }

    const uint KeyTitle = 0x222641E9, KeyStitle = 0xEED70EC4, KeyAttribute = 0x19FAFFCF, KeyCategory = 0x21CA76B1;

    // ------------------------------------------------------------------ string values

    [Hle] uint sceLsdbGetLiveAreaContentsPath(uint info) => Str(info, 0x9CCBEDE2);
    [Hle] uint sceLsdbGetOriginalPath(uint info) => Str(info, 0x9A9B3542);
    [Hle] uint sceLsdbGetCategory(uint info) => Str(info, KeyCategory);
    [Hle] uint sceLsdbGetName(uint info) => Str(info, 0xA1541070);
    [Hle] uint sceLsdbGetForegroundTitleId(uint info) => Str(info, 0x308A7798);
    [Hle] uint sceLsdbGetBackgroundTitleId(uint info) => Str(info, 0xC1C050C4);
    [Hle] uint sceLsdbGetDiscId(uint info) => Str(info, 0xF960D54F);
    [Hle] uint sceLsdbGetSelfPath(uint info) => Str(info, 0xB4232566);
    [Hle] uint sceLsdbGetAppVer(uint info) => Str(info, 0xBCD7161E);
    [Hle] uint sceLsdbGetContentId(uint info) => Str(info, 0x4D6FEFCA);
    [Hle] uint sceLsdbGetNpCommunicationId(uint info) => Str(info, 0x10C1A2E0);
    [Hle] uint sceLsdbGetGameDataId(uint info) => Str(info, 0x924BC88D);
    [Hle] uint sceLsdbGetBootInstallDir(uint info) => Str(info, 0xFACE5965);
    [Hle] uint sceLsdbGetContentLocation(uint info) => Str(info, 0x39A02870);
    [Hle] uint sceLsdbGetPic0Path(uint info) => Str(info, 0x6711F0AD);
    [Hle] uint sceLsdbGetLastLaunchTime(uint info) => Str(info, 0x4C5BFEC6);
    [Hle] uint sceLsdbGetSupportUri(uint info) => Str(info, 0x8FE4356D);
    [Hle] uint sceLsdbGetInstallDirSavedataAdd1(uint info) => Str(info, 0xC79BFEEC);

    static readonly uint[] TitleKeys =
    [
        0xCF222A72, 0xD0222C05, 0xCD22274C, 0xCE2228DF, 0xCB222426, 0xCC2225B9, 0xC9222100, 0xCA222293, 0xD722370A, 0xD822389D,
        0x55253BFB, 0x54253A68, 0x57253F21, 0x56253D8E, 0x59254247, 0x582540B4, 0x5B25456D, 0x5A2543DA, 0x4D252F63, 0x4C252DD0,
    ];

    static readonly uint[] StitleKeys =
    [
        0x11F082E9, 0x10F08156, 0x0FF07FC3, 0x0EF07E30, 0x15F08935, 0x14F087A2, 0x13F0860F, 0x12F0847C, 0x19F08F81, 0x18F08DEE,
        0x0BEE3AE0, 0x0CEE3C73, 0x0DEE3E06, 0x0EEE3F99, 0x0FEE412C, 0x10EE42BF, 0x11EE4452, 0x12EE45E5, 0x13EE4778, 0x14EE490B,
    ];

    /// <summary>(int language): the key of the short title in that language (the default one past the 20 languages).</summary>
    [Hle] uint sceLsdbGetStitleKeyForLanguage(int lang) => lang is >= 0 and < 20 ? StitleKeys[lang] : KeyStitle;

    /// <summary>The system language (SCE_SYSTEM_PARAM_LANG_*); English when it cannot be read.</summary>
    [Hle] int sceLsdbGetSystemLanguageIndex() => AppUtil.SystemParamInt(1, AppUtil.SystemRegistry(Emu));

    /// <summary>The title in the system language, else the default title.</summary>
    [Hle]
    uint sceLsdbGetTitle(uint info)
    {
        int lang = sceLsdbGetSystemLanguageIndex();
        uint key = lang is >= 0 and < 20 ? TitleKeys[lang] : KeyTitle;
        return Find(info, key) != 0 ? Str(info, key) : Str(info, KeyTitle);
    }

    [Hle]
    uint sceLsdbGetStitle(uint info)
    {
        uint key = sceLsdbGetStitleKeyForLanguage(sceLsdbGetSystemLanguageIndex());
        return Find(info, key) != 0 ? Str(info, key) : Str(info, KeyStitle);
    }

    // ------------------------------------------------------------------ integer values

    [Hle] ulong sceLsdbGetAttribute(uint info) => Int(info, KeyAttribute);
    [Hle(Nid = 0x9117289F)] ulong sceLsdbGetLiveAreaType(uint info) => Int(info, 0x2A9A9293);
    [Hle] ulong sceLsdbGetTotalMemoryBudget(uint info) => Int(info, 0xDCE149FA);
    [Hle] ulong sceLsdbGetPromoteAttribute(uint info) => Int(info, 0xCF78B9ED);
    [Hle] ulong sceLsdbGetShellApplicationType(uint info) => Int(info, 0xFAEA72AD);
    [Hle] ulong sceLsdbGetPhysicalContiguousMemoryBudget(uint info) => Int(info, 0xAA162102);
    [Hle] ulong sceLsdbGetBootParamAttribute(uint info) => Int(info, 0xEA522F51);
    [Hle] ulong sceLsdbGetParentalLevel(uint info) => Int(info, 0x879D390D);
    [Hle] ulong sceLsdbGetBootable(uint info) => Int(info, 0x78CF5165);
    [Hle] ulong sceLsdbGetUpdateStatus(uint info) => Int(info, 0xF1A6E2F1);

    /// <summary>Bits 13-14 of the attribute.</summary>
    [Hle(Nid = 0x9AE94B9F)] int sceLsdbGetLiveAreaSystemVersion(uint info) => (int)(Int(info, KeyAttribute) >> 13) & 3;
    /// <summary>Bit 15 of the attribute.</summary>
    [Hle] int sceLsdbGetLiveAreaActivityDisabled(uint info) => (int)(Int(info, KeyAttribute) >> 15) & 1;
    /// <summary>Bit 16 of the attribute.</summary>
    [Hle] int sceLsdbGetLiveAreaPreviewDisabled(uint info) => (int)(Int(info, KeyAttribute) >> 16) & 1;
    /// <summary>Bits 34-36 of the attribute.</summary>
    [Hle] ulong sceLsdbGetExtendedMemoryMode(uint info) => (Int(info, KeyAttribute) >> 34) & 7;

    // ------------------------------------------------------------------ categories

    static readonly string[] GameCategories = ["gd", "gp", "gdc", "gdd", "gpc", "gpd"];

    string? Category(uint info) => sceLsdbGetCategory(info) is var p and not 0 ? GuestMemory.ReadCString(p, 64) : null;

    [Hle] bool sceLsdbIsCategoryGda(uint info) => Category(info) == "gda";

    [Hle]
    bool sceLsdbIsGameCategoryOrLiveAreaSystemVersion1Or2(uint info) =>
        Category(info) is { } c && GameCategories.Contains(c) || sceLsdbGetLiveAreaSystemVersion(info) is 1 or 2;

    [Hle] bool sceLsdbIsGameOrEgCategory(uint info) => Category(info) is { } c && (GameCategories.Contains(c) || c == "EG");

    /// <summary>These ask the shell's game_plugin (paf::Plugin::Find("game_plugin") interface 1); without it in the
    /// process the module answers 0.</summary>
    [Hle] int sceLsdbGetType(uint info) => 0;
    [Hle] int sceLsdbGetAppMetadataLocationType(uint info) => 0;
    [Hle] bool sceLsdbIsLiveAreaActivitySupported(uint info) => false;

    // ------------------------------------------------------------------ small helpers

    [Hle] bool sceLsdbNormalizeBool(int v) => v != 0;

    [Hle] void sceLsdbSetContentRatingCallback(uint cb) => ContentRatingCallback = cb;

    /// <summary>(const String* {char*, size}): "NN.NN" as NNNN (e.g. "01.20" = 120), -1 otherwise.</summary>
    [Hle("sceLsdbParseVersion")]
    [Hle("sceLsdbParseVersion2")]
    int ParseVersion(uint str)
    {
        if (str == 0 || GuestMemory.R32(str + 4) != 5) return -1;
        var s = GuestMemory.ReadSpan(GuestMemory.R32(str), 5);
        static bool D(byte c) => c is >= (byte)'0' and <= (byte)'9';
        if (!D(s[0]) || !D(s[1]) || s[2] != '.' || !D(s[3]) || !D(s[4])) return -1;
        return (s[0] - '0') * 1000 + (s[1] - '0') * 100 + (s[3] - '0') * 10 + (s[4] - '0');
    }

    /// <summary>(SceRtcTick* out): paf::rtc::GetCurrentNetworkTick.</summary>
    [Hle] int sceLsdbGetCurrentRtcTick(uint outp) { if (outp != 0) GuestMemory.Write(outp, Rtc.NowTick); return 0; }

    /// <summary>(SceRtcTick* out): sceRtcGetCurrentAdNetworkTick (the emulator's ad network clock follows the network
    /// one, see Rtc).</summary>
    [Hle] int sceLsdbGetAdNetworkRtcTick(uint outp) { if (outp != 0) GuestMemory.Write(outp, Rtc.NowTick); return 0; }

    /// <summary>(const { SceRtcTick start, end }* range, bool endOnly, bool adNetwork): true for the open range
    /// { 0, ~0 }; else whether the current (or ad network) tick lies in [start, end] (with endOnly only start &lt;= end
    /// and now &lt;= end are required).</summary>
    [Hle]
    bool sceLsdbIsCurrentTimeInRange(uint range, bool endOnly, bool adNetwork)
    {
        ulong start = GuestMemory.Read<ulong>(range), end = GuestMemory.Read<ulong>(range + 8);
        if (start == 0 && end == ulong.MaxValue) return true;
        ulong now = Rtc.NowTick;
        return endOnly ? start <= end && now <= end : start <= now && now <= end;
    }

    // ------------------------------------------------------------------ objects that start empty

    [Hle("sceLsdbQueryContextInit")]
    [Hle("sceLsdbQueryContextInit2")]
    [Hle("sceLsdbNewEventDatabaseInit")]
    [Hle("sceLsdbNewEventDatabaseInit2")]
    uint ZeroInit(uint me) { if (me != 0) GuestMemory.W32(me, 0); return me; }

    /// <summary>(const LiveAreaFrame*, int i): the frame's i-th item (count at +0x3C, array at +0x38), 0 out of range.</summary>
    [Hle]
    uint sceLsdbLiveAreaFrameGetItem(uint frame, int i) =>
        i >= 0 && i < (int)GuestMemory.R32(frame + 0x3C) ? GuestMemory.R32(GuestMemory.R32(frame + 0x38) + (uint)i * 4) : 0;

    // ------------------------------------------------------------------ settings as paf::string { char*, length }

    Paf? _paf;
    Libc? _libc;
    Paf P => _paf ??= Emu.Hle.Modules.OfType<Paf>().First();
    Libc C => _libc ??= Emu.Hle.Modules.OfType<Libc>().First();

    /// <summary>Writes a paf::string the way the module's assign does: a heap copy, or the shared empty string.</summary>
    void PafStr(uint s, string? text)
    {
        if (s == 0) return;
        byte[] b = text == null ? [] : System.Text.Encoding.UTF8.GetBytes(text);
        uint p = b.Length == 0 ? 0 : C.PafHeapAlloc((uint)b.Length + 1);
        if (p == 0) { GuestMemory.W32(s, P.EmptyString); GuestMemory.W32(s + 4, 0); return; }
        b.CopyTo(GuestMemory.Span(p, b.Length));
        GuestMemory.Write<byte>(p + (uint)b.Length, 0);
        GuestMemory.W32(s, p); GuestMemory.W32(s + 4, (uint)b.Length);
    }

    /// <summary>A registry string as sceRegMgrGetKeyStr would read it into a buffer of <paramref name="size"/> bytes
    /// (null when the key does not exist).</summary>
    string? RegStr(string category, string name, int size)
    {
        if (Firmware.Registry.For(Emu.Options.FsRoot).Get(category, name) is not { } v) return null;
        int nul = Array.IndexOf(v, (byte)0);
        int n = Math.Min(nul < 0 ? v.Length : nul, size - 1);
        return System.Text.Encoding.UTF8.GetString(v, 0, Math.Max(n, 0));
    }

    /// <summary>(paf::string* out): the NP language code (/CONFIG/NP/lang), empty when unset.</summary>
    [Hle] void sceLsdbGetNpLanguage(uint outp) => PafStr(outp, RegStr("/CONFIG/NP", "lang", 6));

    /// <summary>(paf::string* out): the NP environment (/CONFIG/NP/env), empty when unset.</summary>
    [Hle] void sceLsdbGetNpEnvironment(uint outp) => PafStr(outp, RegStr("/CONFIG/NP", "env", 0x11));

    /// <summary>(paf::string* out, int source): the account's country (/CONFIG/NP/country); source 2 asks the shell's
    /// telephony plugin first, which a process without it skips.</summary>
    [Hle] void sceLsdbGetCountry(uint outp, int source) => PafStr(outp, RegStr("/CONFIG/NP", "country", 3));

    /// <summary>(paf::string* out): the unit's region code (sceRegMgrSrvGetRegionStr: "UC2" for the emulated
    /// North American unit, as SystemServicesVsh answers).</summary>
    [Hle] void sceLsdbGetRegion(uint outp) => PafStr(outp, "UC2");

    /// <summary>The language codes of the LiveArea templates, in SCE_SYSTEM_PARAM_LANG_* order.</summary>
    static readonly string[] LanguageCodes =
        ["ja", "en", "fr", "es", "de", "it", "nl", "pt", "ru", "ko", "ch", "zh", "fi", "sv", "da", "no", "pl", "pt-br", "en-gb", "tr"];

    /// <summary>(int source, int language): source 1 maps the NP language code (/CONFIG/NP/lang) to its index (-1 when
    /// unset or unknown); otherwise a language outside 0..19 becomes the system language.</summary>
    [Hle]
    int sceLsdbMapLanguageIndex(int source, int lang)
    {
        if (source == 1) return RegStr("/CONFIG/NP", "lang", 6) is { } code ? Array.IndexOf(LanguageCodes, code) : -1;
        return lang is < 0 or > 19 ? sceLsdbGetSystemLanguageIndex() : lang;
    }

    /// <summary>An AppDatabase handle: a one-byte object from the paf heap (operator new); End frees it.</summary>
    [Hle("sceLsdbAppDatabaseInit")]
    [Hle("sceLsdbAppDatabaseInit2")]
    uint AppDatabaseInit(uint me) { GuestMemory.W32(me, C.PafHeapAlloc(1)); return me; }

    [Hle("sceLsdbAppDatabaseEnd")]
    [Hle("sceLsdbAppDatabaseEnd2")]
    uint AppDatabaseEnd(uint me) { if (GuestMemory.R32(me) is var p and not 0) C.PafHeapFree(p); return me; }
}
