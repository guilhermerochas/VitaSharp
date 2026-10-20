using System.Text;
using System.Text.Json;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceRegMgrForGame: the system parameters a game may read (and the few it may write) by SceRegMgrSystemParam id.
/// The values that also exist in SceAppUtil come from <see cref="AppUtil.SystemParamInt"/> so both agree; the writable
/// keys persist in &lt;FsRoot&gt;/vd0/registry/vitasharp-regmgr.json.
/// </summary>
public sealed class RegMgr : HleModule
{
    internal const int ERR_INVALID = unchecked((int)0x800D0004), ERR_READ_ONLY = unchecked((int)0x800D0015);
    /// <summary>GetBin copies at most this much; SetStr/SetBin accept at most <see cref="MaxSetSize"/> bytes (FW 3.60).</summary>
    internal const int MaxGetBinSize = 0x800, MaxSetSize = 0x7C0;

    internal enum KeyType { Int, Str, Bin }

    /// <summary>A registry key: path (for logs and the JSON file), type, writability and default value
    /// (int, or byte[] for strings and binaries).</summary>
    internal sealed record Key(uint Id, string Path, KeyType Type, bool Writable, Func<object> Default);

    static Key I(uint id, string path, int def = 0, bool w = false) => new(id, path, KeyType.Int, w, () => def);
    static Key Id(uint id, string path, Func<int> def) => new(id, path, KeyType.Int, false, () => def());
    static Key S(uint id, string path, string def = "", bool w = false) => new(id, path, KeyType.Str, w, () => Encoding.UTF8.GetBytes(def));
    static Key B(uint id, string path, int size = 0, bool w = false) => new(id, path, KeyType.Bin, w, () => new byte[size]);

    /// <summary>Every SceRegMgrSystemParam id of psp2/registrymgr.h. Debug/fake flags are 0.</summary>
    internal static readonly IReadOnlyDictionary<uint, Key> Table = new Key[]
    {
        Id(0x00229142, "/CONFIG/SYSTEM/button_assign", () => AppUtil.SystemParamInt(2)),
        Id(0x00037502, "/CONFIG/SYSTEM/language", () => AppUtil.SystemParamInt(1)),
        Id(0x00088776, "/CONFIG/DATE/date_format", () => AppUtil.SystemParamInt(4)),
        Id(0x00668503, "/CONFIG/DATE/time_format", () => AppUtil.SystemParamInt(5)),
        Id(0x00100591, "/CONFIG/DATE/time_zone", () => AppUtil.SystemParamInt(6)),
        Id(0x00760538, "/CONFIG/DATE/summer_time", () => AppUtil.SystemParamInt(7)),
        Id(0x007F9315, "/CONFIG/DATE/is_summer_time", () => AppUtil.SystemParamInt(7)),
        S(0x00186122, "/CONFIG/SECURITY/PARENTAL/passcode", "0000"),
        S(0x00598438, "/CONFIG/SYSTEM/username", AppUtil.UserName),
        B(0x00450F32, "/CONFIG/NP/account_id", 8),
        I(0x0081649F, "/CONFIG/BROWSER/ADDIN/TRENDMICRO/tm_service", w: true),
        I(0x00068303, "/CONFIG/BROWSER/ADDIN/TRENDMICRO/tm_ec_ttl", w: true),
        I(0x001B2292, "/CONFIG/BROWSER/ADDIN/TRENDMICRO/tm_ec_ttl_update_time", w: true),
        I(0x00872621, "/CONFIG/BROWSER/ADDIN/TRENDMICRO/tm_service_sub_status", w: true),
        I(0x00156489, "/CONFIG/NP/debug_ingame_commerce2"),
        S(0x00033818, "/CONFIG/NP/env", "np"),
        I(0x003CB6A4, "/DEVENV/TOOL/gpi_switch"),
        I(0x000B73CD, "/CONFIG/NP/debug"),
        I(0x000A0495, "/CONFIG/NP/nav_only"),
        I(0x000B6ECD, "/CONFIG/NP/np_ad_clock_diff"),
        I(0x000D18E5, "/CONFIG/NP/np_geo_filtering"),
        I(0x00313905, "/CONFIG/NP2/test_patch"),
        S(0x00646A8E, "/CONFIG/NP2/tpps_proxy_server"),
        I(0x0022B191, "/CONFIG/NP2/tpps_proxy_port"),
        S(0x008EB468, "/CONFIG/NP2/tpps_proxy_user_name"),
        S(0x00168B9B, "/CONFIG/NP2/tpps_proxy_password"),
        I(0x006FF829, "/CONFIG/NP2/tpps_proxy_flag"),
        I(0x00612B3E, "/CONFIG/BROWSER/web_security_status", w: true),
        I(0x003317A1, "/CONFIG/NP2/trophy_setup_dialog_debug"),
        S(0x008F94F9, "/CONFIG/NP/country", "us"),
        S(0x00134C03, "/CONFIG/NET/pspnet_adhoc_ssid_prefix", "PSP"),
        I(0x00683DCD, "/CONFIG/SYSTEM/key_pad"),
        I(0x008E3939, "/CONFIG/MUSIC/MUSIC_APP/impose_audio_balance"),
        I(0x00146E23, "/CONFIG/GAME/show_debug_info"),
        I(0x00424500, "/CONFIG/GAME/fake_sdslot_broken"),
        I(0x0051F6AE, "/CONFIG/SPECIFIC/idu_mode"),
        I(0x00154A2C, "/CONFIG/GAME/fake_free_space"),
        I(0x0025CE9A, "/CONFIG/GAME/fake_free_space_quota"),
        S(0x005F6737, "/CONFIG/NP2/TWITTER/access_token", w: true),
        S(0x0091F34F, "/CONFIG/NP2/TWITTER/access_token_secret", w: true),
        I(0x0093C981, "/CONFIG/PSM/revocation_check_req"),
        I(0x007C9764, "/CONFIG/NP2/fake_plus"),
        I(0x00971FA1, "/CONFIG/SHELL/voice_priority"),
        I(0x00563BFE, "/CONFIG/NET/ssl_cert_ignorable"),
        B(0x008D89EB, "/CONFIG/NP2/TELEPORT/wol_target_mac_address", w: true),
        S(0x004E7A16, "/CONFIG/NP2/TELEPORT/target_name", w: true),
        S(0x00987180, "/CONFIG/NP2/TELEPORT/initial_target", w: true),
        S(0x000504E4, "/CONFIG/NP2/TELEPORT/passcode_client", w: true),
        I(0x0036F14E, "/CONFIG/NP2/TELEPORT/enable_media_transfer"),
        I(0x002FDFB4, "/CONFIG/DISPLAY/hdmi_out_scaling_ratio"),
        I(0x004F7E60, "/CONFIG/PS4LINK/counter", w: true),
        B(0x0094E320, "/CONFIG/PS4LINK/keys", w: true),
        I(0x009623D0, "/CONFIG/GAME/fake_no_memory_card"),
        I(0x007D12C4, "/CONFIG/GAME/fake_contents_max"),
        I(0x0089C9CF, "/CONFIG/SECURITY/PARENTAL/store_start_control"),
        S(0x00988B81, "/CONFIG/PSNOW/app_cached_url"),
        I(0x008C3860, "/CONFIG/BROWSER/DEBUG/net_dbg_config", w: true),
        I(0x00505BCE, "/CONFIG/NP2/fake_ratelimit"),
        S(0x00528C0D, "/CONFIG/NP2/ignore_titleid"),
        I(0x00023FC2, "/CONFIG/ACCESSIBILITY/large_text"),
        I(0x00611DC9, "/CONFIG/ACCESSIBILITY/bold_text"),
        I(0x008A2AD7, "/CONFIG/ACCESSIBILITY/contrast"),
        I(0x00711659, "/CONFIG/SECURITY/PARENTAL/content_start_control"),
    }.ToDictionary(k => k.Id);

    readonly object _lock = new();
    /// <summary>Values written by the game (writable keys only), loaded lazily from the JSON file.</summary>
    Dictionary<uint, object>? _written;

    /// <summary>Test hook: the filesystem root used instead of the emulator's (null = Emu.Options.FsRoot).</summary>
    internal string? FsRootOverride { get; set; }
    string FsRoot => FsRootOverride ?? Emu.Options.FsRoot;
    internal string StorePath => Path.Combine(FsRoot, "vd0", "registry", "vitasharp-regmgr.json");

    Dictionary<uint, object> Written()
    {
        if (_written != null) return _written;
        _written = new();
        try
        {
            if (File.Exists(StorePath))
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? new();
                foreach (var (k, v) in raw)
                {
                    if (!uint.TryParse(k.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? k[2..] : k,
                            System.Globalization.NumberStyles.HexNumber, null, out uint id)) continue;
                    if (!Table.TryGetValue(id, out var key) || !key.Writable) continue;
                    if (key.Type == KeyType.Int) { if (int.TryParse(v, out int iv)) _written[id] = iv; }
                    else _written[id] = Convert.FromBase64String(v);
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or UnauthorizedAccessException)
        {
            Log.Warn($"SceRegMgr: cannot read {StorePath}: {e.Message}");
        }
        return _written;
    }

    void Save()
    {
        var raw = new SortedDictionary<string, string>();
        foreach (var (id, v) in _written!)
            raw[$"0x{id:X8}"] = v is int i ? i.ToString() : Convert.ToBase64String((byte[])v);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"SceRegMgr: cannot write {StorePath}: {e.Message}");
        }
    }

    /// <summary>Current value of a key (int or byte[]), or null for an unknown id.</summary>
    internal object? Value(uint id, out Key? key)
    {
        if (!Table.TryGetValue(id, out key)) return null;
        lock (_lock) return Written().TryGetValue(id, out var v) ? v : key.Default();
    }

    static bool IsBytes(KeyType t) => t is KeyType.Str or KeyType.Bin;

    [Hle]
    int sceRegMgrSystemParamGetInt(uint id, uint buf)
    {
        if (buf == 0 || Value(id, out var key) is not int v || key!.Type != KeyType.Int)
        {
            Log.Debug($"sceRegMgrSystemParamGetInt(0x{id:X8}): unknown or not an int");
            return ERR_INVALID;
        }
        GuestMemory.W32(buf, (uint)v);
        return 0;
    }

    /// <summary>Copies the string up to its first NUL, truncated to <paramref name="size"/> - 1 bytes plus a NUL.</summary>
    [Hle]
    int sceRegMgrSystemParamGetStr(uint id, uint buf, int size)
    {
        if (buf == 0 || size <= 0 || Value(id, out var key) is not byte[] v || !IsBytes(key!.Type)) return ERR_INVALID;
        int len = Array.IndexOf(v, (byte)0);
        if (len < 0) len = v.Length;
        len = Math.Min(len, size - 1);
        v.AsSpan(0, len).CopyTo(GuestMemory.Span(buf, len));
        GuestMemory.Span(buf + (uint)len, 1)[0] = 0;
        return 0;
    }

    /// <summary>Copies exactly min(buf_size, 0x800) bytes, zero-filling past the stored value.</summary>
    [Hle]
    int sceRegMgrSystemParamGetBin(uint id, uint buf, uint bufSize)
    {
        if (buf == 0 || Value(id, out var key) is not byte[] v || !IsBytes(key!.Type)) return ERR_INVALID;
        int n = (int)Math.Min(bufSize, (uint)MaxGetBinSize);
        var dst = GuestMemory.Span(buf, n);
        dst.Clear();
        v.AsSpan(0, Math.Min(v.Length, n)).CopyTo(dst);
        return 0;
    }

    int Set(uint id, object value, bool isInt, string fn)
    {
        if (!Table.TryGetValue(id, out var key)) return ERR_INVALID;
        if (!key.Writable) { Log.Debug($"{fn}({key.Path}): read-only"); return ERR_READ_ONLY; }
        if ((key.Type == KeyType.Int) != isInt) return ERR_INVALID;
        lock (_lock)
        {
            Written()[id] = value;
            Save();
        }
        Log.Debug($"{fn}({key.Path})");
        return 0;
    }

    [Hle] int sceRegMgrSystemParamSetInt(uint id, int value) => Set(id, value, true, nameof(sceRegMgrSystemParamSetInt));

    /// <summary>Stores exactly <paramref name="bufSize"/> bytes (no NUL is added).</summary>
    [Hle]
    int sceRegMgrSystemParamSetStr(uint id, uint buf, uint bufSize)
    {
        if (buf == 0 || bufSize > MaxSetSize) return ERR_INVALID;
        return Set(id, GuestMemory.Span(buf, (int)bufSize).ToArray(), false, nameof(sceRegMgrSystemParamSetStr));
    }

    [Hle]
    int sceRegMgrSystemParamSetBin(uint id, uint buf, uint bufSize)
    {
        if (buf == 0 || bufSize > MaxSetSize) return ERR_INVALID;
        return Set(id, GuestMemory.Span(buf, (int)bufSize).ToArray(), false, nameof(sceRegMgrSystemParamSetBin));
    }

    /// <summary>No public prototype; the system is never in "blue screen" (demo / kiosk) mode.</summary>
    [Hle] int sceRegMgrSystemIsBlueScreen() => 0;
}
