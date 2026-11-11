using Android.App;
using Android.Content;
using Android.OS;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Game;

/// <summary>
/// The game process's side of the Android game APIs (docs/android/README.md, "CPU cores and ADPF"):
/// <list type="bullet">
/// <item><b>Game state</b> (API 33+, <c>GameManager.setGameState</c>): loading (the boot, a load state) or gameplay,
/// so the system and the OEM game service (HyperOS Game Turbo, Samsung Game Booster) can boost loads and know a
/// loading screen from play.</item>
/// <item><b>Game mode</b> (API 31+, game_mode_config.xml): performance or battery, set by the user in the system's game
/// dashboard or by the OEM game service, read every few seconds because it can change while the game runs. Battery
/// mode asks the ADPF session for efficient cores and clocks (API 35+ <c>setPreferPowerEfficiency</c>); standard and
/// performance ask for speed.</item>
/// <item><b>Thermal headroom</b> (API 30+): logged every 30 s next to the PERF lines (how close the phone is to
/// throttling: 1.0 = severe throttling), with the thermal status.</item>
/// </list>
/// The game state and the battery-mode wish need <c>VITASHARP_ANDROID_GAME_STATE=1</c> (A/B; the mode and the thermal
/// line are always logged). Best effort: a failing call is logged once and ignored.
/// </summary>
public sealed class NativeGameState
{
    readonly GameManager? _games;
    readonly PowerManager? _power;
    int _mode = -1;
    int _loading = -1;
    bool _failed;
    readonly bool _enabled = System.Environment.GetEnvironmentVariable(EnvVars.AndroidGameState)?.Trim() is "1" or "on" or "true" or "yes";

    public NativeGameState(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            _games = context.GetSystemService(Context.GameService) as GameManager;
        _power = context.GetSystemService(Context.PowerService) as PowerManager;
    }

    /// <summary>The current game mode is battery (or the ADPF session should otherwise prefer efficiency).</summary>
    public bool PreferPowerEfficiency => _enabled && _mode == 3; // GameManager.GAME_MODE_BATTERY

    /// <summary>Reads the game mode; true when it changed (the first read counts as a change).</summary>
    public bool PollMode()
    {
        if (_games == null || !OperatingSystem.IsAndroidVersionAtLeast(31))
            return false;
        int mode = (int)_games.GameMode;
        if (mode == _mode)
            return false;
        _mode = mode;
        Log.Info($"Android game mode: {ModeName(mode)}" + (PreferPowerEfficiency ? " (ADPF prefers power efficiency)" : ""));
        return true;
    }

    static string ModeName(int mode) => mode switch
    {
        1 => "standard",
        2 => "performance",
        3 => "battery",
        4 => "custom",
        _ => $"unsupported ({mode})",
    };

    /// <summary>GameManager.setGameState: loading or gameplay (only changes are sent).</summary>
    public void SetLoading(bool loading)
    {
        int v = loading ? 1 : 0;
        if (!_enabled || v == _loading || _failed || _games == null || !OperatingSystem.IsAndroidVersionAtLeast(33))
            return;
        _loading = v;
        try
        {
            // MODE_GAMEPLAY_UNINTERRUPTIBLE: the player is in the game (a phone call or notification interrupts play)
            using var state = new GameState(loading, (int)GameStateMode.GameplayUninterruptible);
            _games.SetGameState(state);
            if (loading)
                Log.Info("Android game state: loading");
        }
        catch (Exception e)
        {
            _failed = true;
            Log.Warn($"GameManager.setGameState failed: {e.Message}");
        }
    }

    /// <summary>One line with the thermal status and the headroom now and in 10 s (forecast).</summary>
    public void LogThermal()
    {
        if (_power == null || !OperatingSystem.IsAndroidVersionAtLeast(30))
            return;
        float now = _power.GetThermalHeadroom(0), soon = _power.GetThermalHeadroom(10);
        Log.Info($"PERF thermal: status {_power.CurrentThermalStatus}, headroom {Fmt(now)} now, {Fmt(soon)} in 10 s (1.0 = severe throttling)");
    }

    static string Fmt(float v) => float.IsNaN(v) ? "n/a" : v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
}
