using System.Diagnostics;
using VitaSharp.Android.Audio;
using VitaSharp.Android.Gpu;
using VitaSharp.Android.Input;
using VitaSharp.Android.Library;
using VitaSharp.Android.Logging;
using VitaSharp.Android.Media;
using VitaSharp.Android.Services;
using VitaSharp.Android.Settings;
using VitaSharp.Core;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Android.Game;

/// <summary>
/// One game run in the game process: the Android counterpart of the desktop's Runner. The emulator runs on its own
/// threads (guest threads, GPU worker, audio); this class connects it to the activity:
/// <list type="bullet">
/// <item>surface: the renderer is created on the first surfaceCreated (with the Android surface's instance
/// extensions), detached in surfaceDestroyed and attached to the new window in the next surfaceCreated;</item>
/// <item>lifecycle: onPause suspends the process (Emulator.Suspend: guest threads stop at their next safe point) and
/// pauses the AAudio streams; onResume resumes both. The guest clock keeps running meanwhile, as the Vita's RTC does
/// across a suspend;</item>
/// <item>input: <see cref="AndroidInput"/> (on-screen pad + physical controllers) is the core's IInputBackend;</item>
/// <item>settings: the game's settings over the global ones (<see cref="EffectiveSettings"/>): CPU backend, clock,
/// GPU profile, VSync; play time and average FPS are added to the game's statistics when it ends.</item>
/// </list>
/// </summary>
public sealed class EmulationSession : IDisposable
{
    readonly object _surfaceLock = new();
    readonly GameEntry _game;
    readonly EffectiveSettings _settings;
    readonly Emulator _emu;
    readonly VulkanRendererOptions _rendererOptions;
    readonly AAudioBackend? _audio;
    readonly Stopwatch _played = new();
    VulkanRenderer? _renderer;
    Thread? _boot;
    long _framesAtStart;
    bool _started, _disposed;

    /// <summary>Raised (on an emulator thread) when the game crashes or cannot start.</summary>
    public event Action<string>? Failed;

    /// <summary>Raised (on an emulator thread) on a trophy unlock.</summary>
    public event Action<TrophyUnlockedEvent>? TrophyUnlocked;

    public GameEntry Game => _game;
    public EffectiveSettings Settings => _settings;
    public bool Paused { get; private set; }
    public string? DeviceName => _renderer?.DeviceName;

    public EmulationSession(GameEntry game, AndroidInput input, string rpcnPassword)
    {
        _game = game;
        var data = AppServices.Data;
        var prefs = AppServices.Prefs;
        _settings = EffectiveSettings.Resolve(prefs, AppServices.GameSettingsFor(game.TitleId));
        SessionLog.Open(data.Logs, game.TitleId);
        foreach (var line in RuntimeReport.Lines()) Log.Info(line);
        Log.Info($"Game {game.TitleId} '{game.Title}' {game.Version}: CPU {(_settings.CpuBackend.Length > 0 ? _settings.CpuBackend : "auto")}, " +
                 $"{_settings.ClockHz / 1_000_000} MHz, GPU profile {(_settings.GpuProfile.Length > 0 ? _settings.GpuProfile : "native")}, VSync {_settings.VSync}");

        CpuCore.InstructionsPerSecond = _settings.ClockHz;
        HostThreads.Enabled = true;
        var (psn, net, rpcn) = prefs.Network.ToOptions(rpcnPassword);
        _emu = new Emulator(new EmulatorOptions
        {
            FsRoot = data.Fs,
            CpuBackend = _settings.CpuBackend.Length > 0 ? _settings.CpuBackend : null,
            PatchDir = data.Patches,
            NpRoot = data.Np,
            Psn = psn, Net = net, Rpcn = rpcn,
        });
        _emu.Fatal += (t, e) =>
        {
            Log.Error($"Fatal error in thread {t.Name}: {e}");
            Failed?.Invoke($"O jogo parou com um erro na thread {t.Name}: {e.Message}");
        };
        _emu.Host.Input = input;
        _emu.Host.Audio = _audio = AAudioBackend.TryCreate();
        // hardware H.264 (MediaCodec), FFmpeg software as the fallback when the app carries it
        VitaSharp.Core.Media.VideoDecoders.Backend = new MediaCodecVideoBackend();
        TrophyNotifications.Unlocked += OnTrophy;

        // a custom Adreno/Turnip driver (Driver da GPU) goes in before the first Vulkan call of the process
        var ctx = AppServices.AppContext;
        var gpuSettings = GpuDriverSettings.Load(Path.Combine(data.Root, GpuDriverScreen.SettingsFile));
        VulkanLoader.DriverProvider = AdrenoToolsDriverLoader.Create(gpuSettings, new CustomDriverStore(Path.Combine(data.Root, GpuDriverScreen.DriversDir)),
            ctx.ApplicationInfo!.NativeLibraryDir!, ctx.CacheDir!.AbsolutePath, Path.Combine(data.Root, "gpu_driver_files"));

        _rendererOptions = new VulkanRendererOptions
        {
            ShaderCacheDir = data.ShaderCache,
            VSync = _settings.VSync,
            GpuProfile = _settings.GpuProfile.Length > 0 ? _settings.GpuProfile : null,
        };
        // sceAppMgrLoadExec: restart the process with the requested executable (new renderer: all GPU state is stale)
        _emu.ExecRequested += () => Task.Run(() =>
        {
            try
            {
                _emu.PerformExec(() =>
                {
                    lock (_surfaceLock) return _renderer = new VulkanRenderer(_rendererOptions);
                });
            }
            catch (Exception e)
            {
                Log.Error($"LoadExec failed: {e}");
                Failed?.Invoke($"Falha ao reiniciar o jogo: {e.Message}");
            }
        });
    }

    void OnTrophy(TrophyUnlockedEvent e) => TrophyUnlocked?.Invoke(e);

    /// <summary>surfaceCreated: the first one boots the game, later ones re-attach the renderer.</summary>
    public void SurfaceCreated(AndroidVulkanSurface surface)
    {
        lock (_surfaceLock)
        {
            _rendererOptions.Surface = surface;
            if (_renderer != null)
            {
                _renderer.AttachSurface(surface);
                return;
            }
            if (_started) return;
            _started = true;
        }
        _boot = new Thread(Boot) { Name = "boot", IsBackground = true };
        _boot.Start();
    }

    void Boot()
    {
        try
        {
            lock (_surfaceLock)
            {
                if (_disposed) return;
                // the window went away before the boot thread ran: the next surfaceCreated boots
                if (_rendererOptions.Surface == null) { _started = false; return; }
                _renderer = new VulkanRenderer(_rendererOptions);
            }
            Log.Info($"Renderer: {_renderer.DeviceName}");
            _emu.Gpu.Backend = _renderer;
            _emu.LoadTitle(_game.AppDir, _game.TitleId == GameActivity.SystemTitleId ? Emulator.ShellSelf : "eboot.bin");
            _framesAtStart = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
            _played.Start();
            _emu.Start();
        }
        catch (Exception e)
        {
            Log.Error($"Could not start {_game.TitleId}: {e}");
            Failed?.Invoke(e is DllNotFoundException
                ? $"Uma biblioteca nativa está faltando: {e.Message}"
                : $"Não foi possível iniciar o jogo: {e.Message}");
        }
    }

    /// <summary>surfaceChanged: the window was resized (rotation within landscape, multi-window).</summary>
    public void SurfaceChanged() => _renderer?.NotifyResized();

    /// <summary>surfaceDestroyed: must return only once nothing presents to the window any more.</summary>
    public void SurfaceDestroyed()
    {
        lock (_surfaceLock)
        {
            _renderer?.DetachSurface();
            _rendererOptions.Surface = null;
        }
    }

    public void Pause()
    {
        if (Paused || _disposed) return;
        Paused = true;
        _played.Stop();
        _emu.Suspend();
        _audio?.Pause();
        Log.Flush();
    }

    public void Resume()
    {
        if (!Paused || _disposed) return;
        Paused = false;
        _audio?.Resume();
        _emu.Resume();
        if (_started) _played.Start();
    }

    /// <summary>Frames the guest presented since the game started.</summary>
    public long Frames => VitaSharp.Core.Hle.Modules.Display.FramesPresented - _framesAtStart;

    /// <summary>Stops the game and records the session in the game's statistics.</summary>
    public void Dispose()
    {
        lock (_surfaceLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        TrophyNotifications.Unlocked -= OnTrophy;
        _played.Stop();
        try
        {
            var stats = GameSettings.Load(AppServices.Data.GameSettingsFile(_game.TitleId));
            stats.RecordSession(_played.Elapsed, Frames, DateTime.UtcNow);
            AppServices.SaveGameSettings(_game.TitleId, stats);
            AppServices.ReloadPrefs();
            AppServices.Prefs.LastPlayed = _game.TitleId;
            AppServices.SavePrefs();
        }
        catch (Exception e) { Log.Warn($"Play statistics: {e.Message}"); }
        Log.Info($"Session of {_game.TitleId} ends after {_played.Elapsed:hh\\:mm\\:ss}, {Frames} frames");
        try
        {
            _emu.Stop();
            _boot?.Join(5000);
            _emu.Dispose();
        }
        catch (Exception e) { Log.Warn($"Shutdown: {e.Message}"); }
        Log.Flush();
    }
}
