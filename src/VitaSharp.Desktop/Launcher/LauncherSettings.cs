using System.Text.Json;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Launcher preferences, stored as JSON in the data folder.</summary>
public sealed class LauncherSettings
{
    public bool Fullscreen { get; set; }
    public int Scale { get; set; } = 1;
    public bool VSync { get; set; } = true;
    public bool Validation { get; set; }
    public bool ShowLog { get; set; }
    /// <summary>Webcam used for the Vita cameras: "" = first device, "none" = no webcam (grey frames), or a device name.</summary>
    public string Camera { get; set; } = "";
    /// <summary>CPU backend: "" = best supported on this PC, or "native", "il", "interp" (--cpu=).</summary>
    public string CpuBackend { get; set; } = "";
    /// <summary>Emulated ARM clock in MHz (--cpu-mhz=): 0 = the Vita's default for applications (333), 444 = the
    /// maximum a game can request. Faster loads, but games that rely on retail timing can break.</summary>
    public int CpuMhz { get; set; }
    /// <summary>"Compilação rápida de shaders": shaderc optimisation Zero (on) or Performance (off), --shaderc-opt=.</summary>
    public bool FastShaderCompile { get; set; } = true;
    /// <summary>"Compilar shaders e pipelines em segundo plano": shader/pipeline prefetch workers, --async-shaders=.</summary>
    public bool BackgroundShaderCompile { get; set; } = true;
    /// <summary>"Modo de depuração": detailed diagnostic logging (--debug-mode, docs/notes/debug-mode.md).</summary>
    public bool DebugMode { get; set; }
    /// <summary>"Pré-gravação": F8's session capture keeps the seconds before it was pressed (--capture-preroll,
    /// docs/notes/session-capture.md).</summary>
    public bool CapturePreRoll { get; set; }
    /// <summary>"Simular conta PSN conectada (offline)": the emulated PSN account is signed in (--psn=on, the default) or
    /// signed out (--psn=off). Signed in, sign-in checks pass and PSN server requests fail as unreachable.</summary>
    public bool PsnSignedIn { get; set; } = true;
    /// <summary>Online ID of the emulated PSN account (--psn-id=).</summary>
    public string PsnOnlineId { get; set; } = VitaSharp.Core.Hle.PsnAccount.DefaultOnlineId;
    /// <summary>"Serviços PSN locais": TUS / Cross-Save, rankings, tickets and matching served from &lt;data&gt;/np
    /// (--psn-local=on, the default) or every PSN server request failing as unreachable (--psn-local=off).</summary>
    public bool PsnLocalServices { get; set; } = true;
    /// <summary>"Internet real": game sockets, DNS and HTTP go through this PC's network (--net=real). Off by default:
    /// game traffic would leave the PC (Sony's servers stay blocked).</summary>
    public bool RealInternet { get; set; }
    /// <summary>RPCN server for Score/TUS ("host" or "host:port", --rpcn=); empty = the local PSN services. Used only
    /// with <see cref="RealInternet"/>.</summary>
    public string RpcnServer { get; set; } = "";
    /// <summary>RPCN account name (--rpcn-user=).</summary>
    public string RpcnUser { get; set; } = "";
    /// <summary>RPCN password: stored in launcher.json in plain text (like RPCS3's rpcn.yml) and handed to the game
    /// process in VITASHARP_RPCN_PASSWORD, never on its command line.</summary>
    public string RpcnPassword { get; set; } = "";
    /// <summary>RPCN e-mail validation token (--rpcn-token=), needed only for the first login of a new account.</summary>
    public string RpcnToken { get; set; } = "";
    /// <summary>"Manter em HLE": firmware/game libraries kept on HLE even when the module is available, comma separated
    /// (--hle-modules=, e.g. "libSceJson"). Empty: use every module the game or the installed firmware provides.</summary>
    public string HleModules { get; set; } = "";
    /// <summary>Library view: "grid" (large covers, the default), "list" (one row per game) or "home" ("Tela inicial":
    /// the recreated Vita-style bubbles and LiveArea, offered only with <see cref="RecreatedHome"/>).</summary>
    public string View { get; set; } = ViewGrid;

    /// <summary>The recreated home screen (provisional until the firmware's own shell.self replaces it) is offered as a
    /// library view. Off by default.</summary>
    public bool RecreatedHome { get; set; }

    /// <summary>"Ao abrir o emulador": "library" (the default) or "system" (the Vita system starts right away, like
    /// booting a console).</summary>
    public string StartupView { get; set; } = StartLibrary;

    /// <summary>"Ao escolher um jogo": "play" (start it, the default) or "livearea" (its LiveArea first).</summary>
    public string GameClick { get; set; } = ClickPlay;

    /// <summary>Launcher theme: "system" (follows the OS, the default), "dark" or "light".</summary>
    public string UiTheme { get; set; } = ThemeSystem;

    /// <summary>Library sort: "az" (title), "recent" (last played first) or "time" (most played first).</summary>
    public string LibrarySort { get; set; } = "az";

    /// <summary>Library region filter (<see cref="GameEntry.Region"/>); "" = every region.</summary>
    public string LibraryRegion { get; set; } = "";

    /// <summary>"Procurar atualizações ao iniciar" (auto-update roadmap item; the check itself is not built yet).</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Update channel: "builds" (every automatic build) or "releases" (tagged versions only).</summary>
    public string UpdateChannel { get; set; } = "builds";
    /// <summary>Home screen theme id (<see cref="VitaSharp.Core.Shell.ShellTheme.Id"/>); "" = the firmware's default.</summary>
    public string Theme { get; set; } = "";
    /// <summary>Play the system BGM (pd0:data/systembgm/home.at9 or the theme's) on the home screen.</summary>
    public bool HomeBgm { get; set; }
    /// <summary>Play the system sound effects (common_resource.rco) on the home screen and LiveArea.</summary>
    public bool SystemSounds { get; set; }
    /// <summary>"Som de troféu": a chime with the in-game trophy notification (the firmware's notice sound, else a
    /// built-in one), --trophy-sound=.</summary>
    public bool TrophySound { get; set; } = true;

    /// <summary>"Ajustes do jogo": options of one game by title ID, set on the game's page and passed only when that game
    /// starts. Games without an entry run with the defaults.</summary>
    public Dictionary<string, GameOptions> Games { get; set; } = new();

    public const string ViewGrid = "grid", ViewList = "list", ViewHome = "home";
    public const string StartLibrary = "library", StartSystem = "system", ClickPlay = "play", ClickLiveArea = "livearea";
    public const string ThemeSystem = "system", ThemeDark = "dark", ThemeLight = "light";

    /// <summary>An RPCN server and account are configured.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesRpcn => !string.IsNullOrWhiteSpace(RpcnServer) && !string.IsNullOrWhiteSpace(RpcnUser) && RpcnPassword.Length > 0;

    static string FilePath => Path.Combine(Program.DataRoot, "launcher.json");

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize(File.ReadAllText(FilePath), LauncherJson.Default.LauncherSettings) is { } s)
            {
                // the old launcher's home view switch: keep offering it
                if (s.View == ViewHome) s.RecreatedHome = true;
                return s;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Program.DataRoot);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, LauncherJson.Default.LauncherSettings));
    }

    /// <summary>Command-line options for "run" (with <paramref name="titleId"/>'s own options, <see cref="Games"/>).</summary>
    public IEnumerable<string> RunOptions(string? titleId = null)
    {
        if (titleId != null && Games.TryGetValue(titleId, out var game))
            foreach (var o in game.RunOptions()) yield return o;
        if (Fullscreen) yield return "--fullscreen";
        if (Scale > 1) yield return $"--scale={Scale}";
        if (!VSync) yield return "--no-vsync";
        if (Validation) yield return "--validation";
        if (!string.IsNullOrWhiteSpace(Camera)) yield return $"--camera={Camera}";
        if (!string.IsNullOrWhiteSpace(CpuBackend)) yield return $"--cpu={CpuBackend}";
        if (CpuMhz > 0) yield return $"--cpu-mhz={CpuMhz}";
        yield return FastShaderCompile ? "--shaderc-opt=zero" : "--shaderc-opt=perf";
        yield return BackgroundShaderCompile ? "--async-shaders=on" : "--async-shaders=off";
        if (DebugMode) yield return "--debug-mode";
        if (CapturePreRoll) yield return "--capture-preroll";
        yield return TrophySound ? "--trophy-sound=on" : "--trophy-sound=off";
        yield return PsnSignedIn ? "--psn=on" : "--psn=off";
        if (VitaSharp.Core.Hle.PsnAccount.IsValidOnlineId(PsnOnlineId)) yield return $"--psn-id={PsnOnlineId}";
        yield return PsnLocalServices ? "--psn-local=on" : "--psn-local=off";
        yield return RealInternet ? "--net=real" : "--net=off";
        if (RealInternet && UsesRpcn)
        {
            yield return $"--rpcn={RpcnServer.Trim()}";
            yield return $"--rpcn-user={RpcnUser.Trim()}";
            if (!string.IsNullOrWhiteSpace(RpcnToken)) yield return $"--rpcn-token={RpcnToken.Trim()}";
        }
        var hle = string.Join(",", HleModules.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (hle.Length > 0) yield return $"--hle-modules={hle}";
    }
}

/// <summary>Options of one game ("Ajustes do jogo" on its page in the launcher).</summary>
public sealed class GameOptions
{
    /// <summary>"Resolução interna": render targets at this multiple of the Vita's resolution (1 = native 960x544, the
    /// default; 2-4 like PCSX2's upscaling), --res-scale= (VulkanRenderer.ResScale.cs). Costs GPU time with the pixel count.</summary>
    public int ResScale { get; set; } = 1;

    /// <summary>Internal resolution choices: factor and label.</summary>
    public static readonly (int Factor, string Label)[] ResScales =
        [(1, "1x nativa (960×544)"), (2, "2x (1920×1088)"), (3, "3x (2880×1632)"), (4, "4x (3840×2176)")];

    /// <summary>"Taxa de quadros": "native" (the default: the game's own pacing), "30", "60" or "unlimited", --frame-rate=
    /// (Hle/Modules/Display.cs FrameRate).</summary>
    public string FrameRate { get; set; } = "native";

    /// <summary>Frame rate choices: option value and label.</summary>
    public static readonly (string Value, string Label)[] FrameRates =
        [("native", "Nativo (do jogo)"), ("30", "Fixo em 30"), ("60", "Fixo em 60"), ("unlimited", "Sem limite")];

    public IEnumerable<string> RunOptions()
    {
        if (ResScale is >= 2 and <= 4) yield return $"--res-scale={ResScale}";
        if (FrameRate is "30" or "60" or "unlimited") yield return $"--frame-rate={FrameRate}";
    }

    /// <summary>Every option at its default: the entry can be dropped.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDefault => ResScale is < 2 or > 4 && FrameRate is not ("30" or "60" or "unlimited");
}

/// <summary>Source-generated JSON metadata for the settings file (Native AOT has no reflection-based serializer).</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(LauncherSettings))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, PlayRecord>))]
internal sealed partial class LauncherJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>Play time and last session of one title, recorded by the launcher for the games it starts.</summary>
public sealed class PlayRecord
{
    public long Seconds { get; set; }
    public DateTime LastPlayedUtc { get; set; }
}

/// <summary>"Tempo jogado" / "Última vez": &lt;data&gt;/playtime.json, by title ID ("shell" for the Vita system).</summary>
public static class PlayHistory
{
    static string FilePath => Path.Combine(Program.DataRoot, "playtime.json");

    public static Dictionary<string, PlayRecord> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize(File.ReadAllText(FilePath), LauncherJson.Default.DictionaryStringPlayRecord) ?? new();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new();
    }

    /// <summary>Adds a session that started at <paramref name="startUtc"/> and ended now.</summary>
    public static Dictionary<string, PlayRecord> Add(string titleId, DateTime startUtc)
    {
        var all = Load();
        if (!all.TryGetValue(titleId, out var r)) all[titleId] = r = new PlayRecord();
        r.Seconds += Math.Max(0, (long)(DateTime.UtcNow - startUtc).TotalSeconds);
        r.LastPlayedUtc = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(Program.DataRoot);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(all, LauncherJson.Default.DictionaryStringPlayRecord));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (IOException) { }
        return all;
    }
}