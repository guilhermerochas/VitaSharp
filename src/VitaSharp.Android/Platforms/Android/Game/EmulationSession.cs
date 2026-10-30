using System.Diagnostics;
using VitaSharp.Android.Audio;
using VitaSharp.Android.Gpu;
using VitaSharp.Android.Input;
using VitaSharp.Android.Library;
using VitaSharp.Android.Logging;
using VitaSharp.Android.Media;
using VitaSharp.Android.Services;
using VitaSharp.Android.Settings;
using VitaSharp.Android.Storage;
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
/// <item>lifecycle and pause menu: onPause and the menu pause the whole emulation (<see cref="EmulationPause"/>: guest
/// threads of every process stop at their next safe point, the guest clock stops, the audio streams pause); onResume
/// and closing the menu resume it, with no time jump for the game;</item>
/// <item>input: <see cref="AndroidInput"/> (on-screen pad + physical controllers + a hardware keyboard) is the core's
/// IInputBackend, the keyboard's tilt keys its IMotionSource;</item>
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
    readonly AndroidAudioBackend? _audio;
    readonly Stopwatch _played = new();
    VulkanRenderer? _renderer;
    Thread? _boot;
    long _framesAtStart;
    bool _started, _disposed;
    volatile bool _emuStarted;

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
        // the keyboard's tilt keys (VirtualMotion), from their first use; the phone's own sensors are not wired yet
        _emu.Host.Motion = input.Keyboard;
        _emu.Host.Audio = _audio = AndroidAudioBackend.TryCreate(AppServices.AppContext);
        // no FFmpeg in the app: H.264 (SceAvcdec, SceVideodec, movies) through MediaCodec, SceAvPlayer movies demuxed by
        // MediaExtractor with AAC through MediaCodec
        VitaSharp.Core.Media.VideoDecoders.Backend = new MediaCodecVideoBackend();
        VitaSharp.Core.Media.MediaPlayers.Backend = AndroidMediaPlayer.Open;
        TrophyNotifications.Unlocked += OnTrophy;

        // a custom Adreno/Turnip driver (Driver da GPU) goes in before the first Vulkan call of the process
        var ctx = AppServices.AppContext;
        var gpuSettings = GpuDriverSettings.Load(data.GpuDriverSettingsFile);
        var gpuStore = new CustomDriverStore(data.GpuDrivers);
        VulkanLoader.DriverProvider = AdrenoToolsDriverLoader.Create(gpuSettings, gpuStore, data, ctx.ApplicationInfo!.NativeLibraryDir!, ctx.CacheDir!.AbsolutePath);
        // the screens' process reads this to know which driver the running game loaded ("Driver da GPU" restarts it)
        try
        {
            GameProcessMarker.Write(data.GameProcessFile, new GameProcessInfo(global::Android.OS.Process.MyPid(), game.TitleId, game.Title,
                GpuDriverScreen.ConfigOf(gpuSettings, gpuStore), DateTime.UtcNow));
        }
        catch (IOException e) { Log.Warn($"Game process marker: {e.Message}"); }

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

    VitaSharp.Core.Shell.PcmSound? _trophySound;

    /// <summary>"Som de troféu" (on by default): the firmware's notice chime, else the built-in one, on a device stream
    /// of its own (the guest's ports are untouched). Called when the trophy toast appears.</summary>
    public void PlayTrophySound()
    {
        if (!AppServices.Prefs.TrophySound || _audio is not { } audio) return;
        // the first time reads the firmware's RCO: off the UI thread
        Task.Run(() =>
        {
            if (Volatile.Read(ref _trophySound) is not { } pcm)
            {
                (pcm, string source) = VitaSharp.Core.Shell.TrophySound.Load(AppServices.Data.Fs);
                Log.Info($"Trophy sound: {source}, {pcm.Seconds:F2} s");
                Volatile.Write(ref _trophySound, pcm);
            }
            audio.PlayEffect(pcm, 0.8f);
        });
    }

    /// <summary>The host text entry for the IME dialog and the in-game keyboard (the activity's soft keyboard).</summary>
    public void SetTextInput(VitaSharp.Core.Host.IImeDialogBackend dialog, VitaSharp.Core.Host.ITextInputBackend keyboard)
    {
        _emu.Host.ImeDialog = dialog;
        _emu.Host.TextInput = keyboard;
    }

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
            _emuStarted = true;
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

    /// <summary>The pause menu or the activity going to the background: the Core's pause of the whole emulation (the
    /// desktop's pause key does the same), which also pauses the audio streams.</summary>
    public void Pause(string reason = "pause menu")
    {
        if (Paused || _disposed) return;
        Paused = true;
        _played.Stop();
        EmulationPause.Pause(reason);
        Log.Flush();
    }

    public void Resume(string reason = "pause menu")
    {
        if (!Paused || _disposed) return;
        Paused = false;
        EmulationPause.Resume(reason);
        if (_started) _played.Start();
    }

    /// <summary>The game was started and its process has stopped by itself (the guest exited, a stack-protector
    /// failure or a crash terminated it): nothing more will be drawn, the activity offers to leave.</summary>
    public bool Ended => _emuStarted && !_disposed && !_emu.Running && _emu.ExecRequest == null;

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
        RecordStatistics();
        try { GameProcessMarker.Clear(AppServices.Data.GameProcessFile, global::Android.OS.Process.MyPid()); } catch (IOException) { }
        Log.Info($"Session of {_game.TitleId} ends after {_played.Elapsed:hh\\:mm\\:ss}, {Frames} frames");
        try
        {
            _emu.Stop();
            EmulationPause.Resume("session ended"); // the clock and the parked threads let go (they see the stop)
            _boot?.Join(5000);
            _emu.Dispose();
        }
        catch (Exception e) { Log.Warn($"Shutdown: {e.Message}"); }
        Log.Flush();
    }

    int _recorded;

    /// <summary>
    /// Adds the play time and average FPS to the game's statistics (once). Called when the player leaves the game,
    /// before the activity finishes: the screens' activity resumes (and re-reads the statistics) as soon as this one
    /// finishes, before its onDestroy runs, so recording only there showed the previous numbers.
    /// </summary>
    public void RecordStatistics()
    {
        if (Interlocked.Exchange(ref _recorded, 1) != 0) return;
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
    }
}
