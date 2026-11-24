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
    /// <summary>Old single setting for both Vita cameras (launcher.json of earlier builds); <see cref="Load"/> moves it
    /// to <see cref="CameraFront"/> and <see cref="CameraBack"/>.</summary>
    public string Camera { get; set; } = "";
    /// <summary>"Camera frontal" (--camera-front=): "" = the first webcam, "none" = grey frames, a webcam name or a
    /// .png/.jpg picture path (<see cref="Core.Host.CameraSpec"/>).</summary>
    public string CameraFront { get; set; } = "";
    /// <summary>"Camera traseira" (--camera-back=): "" = the next webcam other than the front one, else the same one;
    /// otherwise as <see cref="CameraFront"/>.</summary>
    public string CameraBack { get; set; } = "";
    /// <summary>"Microfone" (--microphone=): "" = the system's default capture device, "none" = off (silence), "tone" = a
    /// 440 Hz test tone, or a capture device name (<see cref="Core.Host.MicrophoneSpec"/>).</summary>
    public string Microphone { get; set; } = "";
    /// <summary>"Localizacao" (--location=): "" = off (the default), or "latitude,longitude[,altitude[,accuracy]]", a fixed
    /// position the Vita's location service answers (<see cref="Core.Host.LocationSpec"/>).</summary>
    public string Location { get; set; } = "";
    /// <summary>"Modelo" (--model=): "" = the PS Vita handheld (PCH-1000, the default), "pstv" = a PlayStation TV (up to four
    /// controllers on ports 1-4, vibration; <see cref="Core.Hle.ConsoleModel"/>).</summary>
    public string Model { get; set; } = "";
    /// <summary>CPU backend: "" = best supported on this PC, or "native", "il", "interp" (--cpu=).</summary>
    public string CpuBackend { get; set; } = "";
    /// <summary>Emulated ARM clock in MHz (--cpu-mhz=): 0 = the Vita's default for applications (333), 444 = the
    /// maximum a game can request. Faster loads, but games that rely on retail timing can break.</summary>
    public int CpuMhz { get; set; }
    /// <summary>"Carregamento rapido" for every game without its own choice (<see cref="GameOptions.FastLoading"/>):
    /// storage reads with no speed limit (--fast-loading). Off by default: games load at the Vita memory card's speed,
    /// which some of them need (Core/Util/IoTiming.cs).</summary>
    public bool FastLoading { get; set; }
    /// <summary>"Resolucao interna" of every game without its own choice (<see cref="GameOptions.ResScale"/>): render
    /// targets at this multiple of the Vita's resolution (1 = native 960x544, the default; 2-4), --res-scale=
    /// (VulkanRenderer.ResScale.cs).</summary>
    public int ResScale { get; set; } = 1;
    /// <summary>"Compilacao rapida de shaders": shaderc optimisation Zero (on) or Performance (off), --shaderc-opt=.</summary>
    public bool FastShaderCompile { get; set; } = true;
    /// <summary>"Compilar shaders e pipelines em segundo plano": shader/pipeline prefetch workers, --async-shaders=.</summary>
    public bool BackgroundShaderCompile { get; set; } = true;
    /// <summary>"Carregar o cache de shaders antes de jogar" for every game without its own choice
    /// (<see cref="GameOptions.LoadShaderCacheBeforePlaying"/>): the shaders and pipelines recorded in the game's earlier
    /// sessions (its shader cache) are built before it starts, behind a progress bar with "Cancelar"
    /// (--load-shader-cache). On by default, like RPCS3 and Ryujinx, which always load their shader caches at boot: with
    /// a warm driver cache it takes about a second, and the game then has no first-use pipeline hitches for what it used
    /// before; a first session has nothing recorded and starts at once.</summary>
    public bool LoadShaderCacheBeforePlaying { get; set; } = true;
    /// <summary>The option's earlier name in launcher.json ("Precompilar shaders"); <see cref="MigrateShaderCacheLoad"/>
    /// moves it to <see cref="LoadShaderCacheBeforePlaying"/>. Never written back.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? PrecompileShaders { get; set; }
    /// <summary>"Precompilar codigo ARM" for every game without its own choice (<see cref="GameOptions.PrecompileCode"/>):
    /// before a game starts, its modules' ARM code is translated on every free core, behind its own progress bar with
    /// "Cancelar" (--precompile-code): the blocks its earlier sessions ran (the code cache), or in a first session the
    /// code reached from the modules' entry points; the code near what the game runs follows in the background. On by
    /// default, like RPCS3's "Compiling PPU modules".</summary>
    public bool PrecompileCode { get; set; } = true;
    /// <summary>"Modo de depuracao": detailed diagnostic logging (--debug-mode, docs/notes/debug-mode.md).</summary>
    public bool DebugMode { get; set; }
    /// <summary>"Pre-gravacao": F8's session capture keeps the seconds before it was pressed (--capture-preroll,
    /// docs/notes/session-capture.md).</summary>
    public bool CapturePreRoll { get; set; }
    /// <summary>"Simular conta PSN conectada (offline)": the emulated PSN account is signed in (--psn=on) or
    /// signed out (--psn=off, the default: no network, like Vita3K). Signed in, sign-in checks pass and PSN server requests fail as unreachable.</summary>
    public bool PsnSignedIn { get; set; }
    /// <summary>Online ID of the emulated PSN account (--psn-id=).</summary>
    public string PsnOnlineId { get; set; } = VitaSharp.Core.Hle.PsnAccount.DefaultOnlineId;
    /// <summary>"Servicos PSN locais": TUS / Cross-Save, rankings, tickets and matching served from &lt;data&gt;/np
    /// (--psn-local=on, the default) or every PSN server request failing as unreachable (--psn-local=off).</summary>
    public bool PsnLocalServices { get; set; } = true;
    /// <summary>"Internet real": game sockets, DNS and HTTP go through this PC's network (--net=real). Off by default:
    /// game traffic would leave the PC (Sony's servers stay blocked).</summary>
    public bool RealInternet { get; set; }

    /// <summary>The emulator option "Enable Kernel Plugins": the plugins of ur0:tai / ux0:tai config.txt load (off: the
    /// pure firmware, no plugin; --kernel-plugins).</summary>
    public bool KernelPlugins { get; set; }
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

    /// <summary>Interface language: "system" (the default), "en" or "pt-BR" (<see cref="Core.Localization.Loc"/>).</summary>
    public string Language { get; set; } = Core.Localization.Loc.System;

    /// <summary>Library sort: "az" (title), "recent" (last played first) or "time" (most played first).</summary>
    public string LibrarySort { get; set; } = "az";

    /// <summary>Library region filter (<see cref="GameEntry.Region"/>); "" = every region.</summary>
    public string LibraryRegion { get; set; } = "";

    /// <summary>"Procurar atualizacoes ao iniciar" (MainWindow.Update.cs).</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Update channel: "builds" (every automatic build) or "releases" (tagged versions only).</summary>
    public string UpdateChannel { get; set; } = "builds";

    /// <summary>The release tag the user chose "Pular esta versao" for: the check at start does not offer it again.</summary>
    public string SkippedUpdate { get; set; } = "";

    /// <summary>Home screen theme id (<see cref="VitaSharp.Core.Shell.ShellTheme.Id"/>); "" = the firmware's default.</summary>
    public string Theme { get; set; } = "";
    /// <summary>Play the system BGM (pd0:data/systembgm/home.at9 or the theme's) on the home screen.</summary>
    public bool HomeBgm { get; set; }
    /// <summary>Play the system sound effects (common_resource.rco) on the home screen and LiveArea.</summary>
    public bool SystemSounds { get; set; }
    /// <summary>"Som de trofeu": a chime with the in-game trophy notification (the firmware's notice sound, else a
    /// built-in one), --trophy-sound=.</summary>
    public bool TrophySound { get; set; } = true;

    /// <summary>"Saida de audio" (--audio-device=): "" = the system default (never a game controller's headset jack on
    /// its own), else an output name as OpenAL lists it, kept even while that output is unplugged.</summary>
    public string AudioDevice { get; set; } = "";

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
            if (File.Exists(FilePath) && FromJson(File.ReadAllText(FilePath)) is { } s)
            {
                // the old launcher's home view switch: keep offering it
                if (s.View == ViewHome)
                {
                    s.RecreatedHome = true;
                }

                s.MigrateCamera();
                s.MigrateShaderCacheLoad();
                return s;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new();
    }

    /// <summary>The settings in <paramref name="json"/> (launcher.json's text), with a file from before the global
    /// "Resolucao interna" moved to it (<see cref="MigrateResScale"/>).</summary>
    public static LauncherSettings? FromJson(string json)
    {
        LauncherSettings? settings = JsonSerializer.Deserialize(json, LauncherJson.Default.LauncherSettings);
        if (settings == null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        bool hasGlobalResScale = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(nameof(ResScale), out _);
        if (!hasGlobalResScale)
        {
            settings.MigrateResScale();
        }

        return settings;
    }

    /// <summary>A launcher.json from before the global <see cref="ResScale"/>: a game's resolution was 1 (native) unless
    /// chosen, and 1 was then the only default, so a game's 1 becomes "follow the global choice" (null); 2-4 stay the
    /// game's own. (A newer file always has the global key, and there a game's 1 is its own choice of native.)</summary>
    public void MigrateResScale()
    {
        foreach (GameOptions game in Games.Values)
        {
            if (game.ResScale is not (>= 2 and <= 4))
            {
                game.ResScale = null;
            }
        }
    }

    /// <summary>The internal resolution <paramref name="titleId"/> starts with: its own choice, else the global one
    /// (1 = native; 2-4).</summary>
    public int EffectiveResScale(string? titleId)
    {
        int scale = ResScale;
        if (titleId != null && Games.TryGetValue(titleId, out GameOptions? game) && game.ResScale is int own)
        {
            scale = own;
        }

        return scale is >= 2 and <= 4 ? scale : 1;
    }

    /// <summary>The old single camera setting becomes the setting of both cameras.</summary>
    public void MigrateCamera()
    {
        if (string.IsNullOrWhiteSpace(Camera))
        {
            return;
        }

        if (CameraFront.Length == 0)
        {
            CameraFront = Camera;
        }

        if (CameraBack.Length == 0)
        {
            CameraBack = Camera;
        }

        Camera = "";
    }

    /// <summary>The option's earlier name ("PrecompileShaders", global and per game) becomes
    /// <see cref="LoadShaderCacheBeforePlaying"/>.</summary>
    public void MigrateShaderCacheLoad()
    {
        if (PrecompileShaders is bool global)
        {
            LoadShaderCacheBeforePlaying = global;
            PrecompileShaders = null;
        }

        foreach (GameOptions game in Games.Values)
        {
            if (game.PrecompileShaders is bool own)
            {
                game.LoadShaderCacheBeforePlaying ??= own;
                game.PrecompileShaders = null;
            }
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Program.DataRoot);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, LauncherJson.Default.LauncherSettings));
    }

    /// <summary>Command-line options for "run" (with <paramref name="titleId"/>'s own options, <see cref="Games"/>).</summary>
    public IEnumerable<string> RunOptions(string? titleId = null)
    {
        GameOptions? game = null;
        if (titleId != null && Games.TryGetValue(titleId, out game))
        {
            foreach (var o in game.RunOptions())
            {
                yield return o;
            }
        }

        int resScale = EffectiveResScale(titleId);
        if (resScale > 1)
        {
            yield return $"--res-scale={resScale}";
        }

        if (game?.FastLoading ?? FastLoading)
        {
            yield return "--fast-loading";
        }

        yield return Core.Gpu.ShaderCacheLoad.Option + ((game?.LoadShaderCacheBeforePlaying ?? LoadShaderCacheBeforePlaying) ? "=on" : "=off");
        yield return Core.Cpu.CodePrecompileLog.Option + ((game?.PrecompileCode ?? PrecompileCode) ? "=on" : "=off");

        if (Fullscreen)
        {
            yield return "--fullscreen";
        }

        if (Scale > 1)
        {
            yield return $"--scale={Scale}";
        }

        if (!VSync)
        {
            yield return "--no-vsync";
        }

        if (Validation)
        {
            yield return "--validation";
        }

        if (!string.IsNullOrWhiteSpace(CameraFront))
        {
            yield return $"--camera-front={CameraFront}";
        }

        if (!string.IsNullOrWhiteSpace(CameraBack))
        {
            yield return $"--camera-back={CameraBack}";
        }

        if (!string.IsNullOrWhiteSpace(Microphone))
        {
            yield return $"--microphone={Microphone}";
        }

        if (!string.IsNullOrWhiteSpace(Location))
        {
            yield return $"--location={Location.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(Model))
        {
            yield return $"--model={Model.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(AudioDevice))
        {
            yield return $"--audio-device={AudioDevice}";
        }

        if (!string.IsNullOrWhiteSpace(CpuBackend))
        {
            yield return $"--cpu={CpuBackend}";
        }

        if (CpuMhz > 0)
        {
            yield return $"--cpu-mhz={CpuMhz}";
        }

        yield return FastShaderCompile ? "--shaderc-opt=zero" : "--shaderc-opt=perf";
        yield return BackgroundShaderCompile ? "--async-shaders=on" : "--async-shaders=off";
        if (DebugMode)
        {
            yield return "--debug-mode";
        }

        if (CapturePreRoll)
        {
            yield return "--capture-preroll";
        }

        yield return TrophySound ? "--trophy-sound=on" : "--trophy-sound=off";
        yield return PsnSignedIn ? "--psn=on" : "--psn=off";
        if (VitaSharp.Core.Hle.PsnAccount.IsValidOnlineId(PsnOnlineId))
        {
            yield return $"--psn-id={PsnOnlineId}";
        }

        yield return PsnLocalServices ? "--psn-local=on" : "--psn-local=off";
        yield return RealInternet ? "--net=real" : "--net=off";
        yield return KernelPlugins ? "--kernel-plugins=on" : "--kernel-plugins=off";
        if (RealInternet && UsesRpcn)
        {
            yield return $"--rpcn={RpcnServer.Trim()}";
            yield return $"--rpcn-user={RpcnUser.Trim()}";
            if (!string.IsNullOrWhiteSpace(RpcnToken))
            {
                yield return $"--rpcn-token={RpcnToken.Trim()}";
            }
        }
        var hle = string.Join(",", HleModules.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (hle.Length > 0)
        {
            yield return $"--hle-modules={hle}";
        }
    }
}

/// <summary>Options of one game ("Ajustes do jogo" on its page in the launcher).</summary>
public sealed class GameOptions
{
    /// <summary>"Resolucao interna": render targets at this multiple of the Vita's resolution (1 = native 960x544, the
    /// default; 2-4 like PCSX2's upscaling), --res-scale= (VulkanRenderer.ResScale.cs). Costs GPU time with the pixel count.
    /// Null = the global default (<see cref="LauncherSettings.ResScale"/>); passed by <see cref="LauncherSettings.RunOptions"/>.</summary>
    public int? ResScale { get; set; }

    /// <summary>The per-game choices: "Padrao (the global one)" (null) first, then <see cref="ResScales"/>.</summary>
    public static (int? Factor, string Label)[] ResScalesWithDefault(int global)
    {
        (int Factor, string Label)[] scales = ResScales;
        int globalIndex = Math.Max(0, Array.FindIndex(scales, c => c.Factor == global));
        string globalLabel = scales[globalIndex].Label;
        return [(null, Core.Localization.Loc.Format("MDefaultParen", globalLabel)), .. scales.Select(c => ((int?)c.Factor, c.Label))];
    }

    /// <summary>Internal resolution choices: factor and label.</summary>
    public static (int Factor, string Label)[] ResScales =>
        [(1, Core.Localization.Loc.Get("ResNative")), (2, "2x (1920\u00D71088)"), (3, "3x (2880\u00D71632)"), (4, "4x (3840\u00D72176)")];

    /// <summary>"Taxa de quadros": "native" (the default: the game's own pacing), "30", "60" or "unlimited", --frame-rate=
    /// (Hle/Modules/Display.cs FrameRate).</summary>
    public string FrameRate { get; set; } = "native";

    /// <summary>Frame rate choices: option value and label.</summary>
    public static (string Value, string Label)[] FrameRates =>
        [("native", Core.Localization.Loc.Get("RateNative")), ("30", Core.Localization.Loc.Get("RateFixed30")),
         ("60", Core.Localization.Loc.Get("RateFixed60")), ("unlimited", Core.Localization.Loc.Get("RateUnlimited"))];

    /// <summary>"Carregamento rapido": true/false = this game's own choice, null = the global default
    /// (<see cref="LauncherSettings.FastLoading"/>); passed by <see cref="LauncherSettings.RunOptions"/>.</summary>
    public bool? FastLoading { get; set; }

    /// <summary>"Carregar o cache de shaders antes de jogar": this game's own choice, null = the global default
    /// (<see cref="LauncherSettings.LoadShaderCacheBeforePlaying"/>).</summary>
    public bool? LoadShaderCacheBeforePlaying { get; set; }
    /// <summary>The option's earlier name (<see cref="LauncherSettings.MigrateShaderCacheLoad"/>). Never written back.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? PrecompileShaders { get; set; }

    /// <summary>"Precompilar codigo ARM": this game's own choice, null = the global default
    /// (<see cref="LauncherSettings.PrecompileCode"/>).</summary>
    public bool? PrecompileCode { get; set; }

    /// <summary>Fast loading choices: value (null = the global default, its state in the label) and label.</summary>
    public static (bool? Value, string Label)[] FastLoadings(bool global) =>
        [(null, Core.Localization.Loc.Format("MDefaultParen", Core.Localization.Loc.Get(global ? "MOn" : "MOff"))),
         (true, Core.Localization.Loc.Get("MOn")), (false, Core.Localization.Loc.Get("MOff"))];

    /// <summary>"Idioma do sistema": the SceSystemParamLang (0-19) this game reads instead of the system's language, for
    /// games without it (null = the system's, the default), --system-language= (EmulatorOptions.SystemLanguage).</summary>
    public int? SystemLanguage { get; set; }

    /// <summary>System language choices: value (null = the system's) and label (each language in its own name).</summary>
    public static (int? Value, string Label)[] SystemLanguages =>
        [(null, Core.Localization.Loc.Get("SystemLanguageFollow")),
         .. Core.Firmware.SystemLanguage.Names.Select((name, language) => ((int?)language, name))];

    public IEnumerable<string> RunOptions()
    {
        if (SystemLanguage is int language && Core.Firmware.SystemLanguage.IsValid(language))
        {
            yield return $"--system-language={language}";
        }

        if (FrameRate is "30" or "60" or "unlimited")
        {
            yield return $"--frame-rate={FrameRate}";
        }
    }

    /// <summary>Every option at its default: the entry can be dropped.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDefault => ResScale == null && FrameRate is not ("30" or "60" or "unlimited") && FastLoading == null
        && SystemLanguage == null && LoadShaderCacheBeforePlaying == null && PrecompileCode == null;
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

/// <summary>"Tempo jogado" / "Ultima vez": &lt;data&gt;/playtime.json, by title ID ("shell" for the Vita system).</summary>
public static class PlayHistory
{
    static string FilePath => Path.Combine(Program.DataRoot, "playtime.json");

    public static Dictionary<string, PlayRecord> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), LauncherJson.Default.DictionaryStringPlayRecord) ?? new();
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new();
    }

    /// <summary>Adds a session that started at <paramref name="startUtc"/> and ended now.</summary>
    public static Dictionary<string, PlayRecord> Add(string titleId, DateTime startUtc)
    {
        var all = Load();
        if (!all.TryGetValue(titleId, out var r))
        {
            all[titleId] = r = new PlayRecord();
        }

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
