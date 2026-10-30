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
    /// <summary>Library view: "list" (game list with details) or "home" ("Tela inicial": Vita-style bubbles and LiveArea).</summary>
    public string View { get; set; } = ViewList;
    /// <summary>Home screen theme id (<see cref="VitaSharp.Core.Shell.ShellTheme.Id"/>); "" = the firmware's default.</summary>
    public string Theme { get; set; } = "";
    /// <summary>Play the system BGM (pd0:data/systembgm/home.at9 or the theme's) on the home screen.</summary>
    public bool HomeBgm { get; set; }
    /// <summary>Play the system sound effects (common_resource.rco) on the home screen and LiveArea.</summary>
    public bool SystemSounds { get; set; }
    /// <summary>"Som de troféu": a chime with the in-game trophy notification (the firmware's notice sound, else a
    /// built-in one), --trophy-sound=.</summary>
    public bool TrophySound { get; set; } = true;

    public const string ViewList = "list", ViewHome = "home";

    /// <summary>An RPCN server and account are configured.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesRpcn => !string.IsNullOrWhiteSpace(RpcnServer) && !string.IsNullOrWhiteSpace(RpcnUser) && RpcnPassword.Length > 0;

    static string FilePath => Path.Combine(Program.DataRoot, "launcher.json");

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize(File.ReadAllText(FilePath), LauncherJson.Default.LauncherSettings) ?? new();
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

    /// <summary>Command-line options for "run".</summary>
    public IEnumerable<string> RunOptions()
    {
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

/// <summary>Source-generated JSON metadata for the settings file (Native AOT has no reflection-based serializer).</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(LauncherSettings))]
internal sealed partial class LauncherJson : System.Text.Json.Serialization.JsonSerializerContext;
