using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using VitaSharp.Core.Input;
using VitaSharp.Core.Shell;
using VitaSharp.Desktop.Input;

namespace VitaSharp.Desktop.Launcher.Shell;

/// <summary>
/// The "Tela inicial" view of the launcher: the home screen, and an app's LiveArea on top of it once its bubble is
/// opened. Drives both with a game controller (D-pad / left stick, Cross = open/start, Circle = back, L/R = page) and
/// plays the optional system BGM and sound effects.
/// </summary>
public sealed class ShellView : Panel, IDisposable
{
    readonly LauncherSettings _settings;
    readonly ShellVisuals _visuals;
    readonly HomeScreenView _home;
    readonly ShellAudio _audio;
    LiveAreaView? _liveArea;
    SdlGamepads? _pads;
    readonly DispatcherTimer _padTimer;
    readonly PadSnapshot _pad = new();
    readonly bool[] _prev = new bool[PadSnapshot.ButtonCount];
    int _repeatDir = -1;
    DateTime _repeatAt;
    bool _gameRunning;
    string? _runningTitle;

    /// <summary>Start was pressed in a LiveArea.</summary>
    public event Action<ShellApp>? LaunchRequested;

    public HomeScreenView Home => _home;
    public LiveAreaView? LiveArea => _liveArea;
    public ShellVisuals Visuals => _visuals;

    public ShellView(LauncherSettings settings, string fsRoot)
    {
        _settings = settings;
        _visuals = new ShellVisuals(fsRoot, settings.Theme);
        _audio = new ShellAudio(_visuals.Assets);
        _home = new HomeScreenView(_visuals);
        _home.Opened += OpenLiveArea;
        Children.Add(_home);
        _padTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _padTimer.Tick += (_, _) => PollPad();
    }

    /// <summary>Installed games followed by the firmware's system apps.</summary>
    public void SetApps(IEnumerable<GameEntry> games)
    {
        var apps = games.Select(g => new ShellApp(g.TitleId, g.Title, g.Version, g.Path, g.IconPath, false, g)).ToList();
        apps.AddRange(_visuals.Assets.SystemApps().Select(s => new ShellApp(s.TitleId, s.Title, "", s.Path, s.IconPath, true, null)));
        _home.Apps = apps;
    }

    public void ApplySettings()
    {
        _visuals.SetTheme(_settings.Theme);
        _home.InvalidateVisual();
        _liveArea?.InvalidateVisual();
        UpdateBgm();
    }

    public void OpenLiveArea(ShellApp app)
    {
        CloseLiveArea(sound: false);
        Sound(ShellSoundEffect.Confirm);
        _liveArea = new LiveAreaView(_visuals, app) { Running = _gameRunning && _runningTitle == app.TitleId };
        _liveArea.Closed += () => CloseLiveArea(sound: true);
        _liveArea.Started += a => { Sound(ShellSoundEffect.Confirm); LaunchRequested?.Invoke(a); };
        Children.Add(_liveArea);
        _liveArea.Focus();
    }

    public void CloseLiveArea(bool sound)
    {
        if (_liveArea == null) return;
        if (sound) Sound(ShellSoundEffect.Cancel);
        Children.Remove(_liveArea);
        _liveArea = null;
        _home.Focus();
    }

    /// <summary>A game started or stopped: BGM and controller polling pause while it runs.</summary>
    public void SetRunning(ShellApp? app)
    {
        _gameRunning = app != null;
        _runningTitle = app?.TitleId;
        if (_liveArea != null) { _liveArea.Running = _gameRunning && _liveArea.App.TitleId == _runningTitle; _liveArea.InvalidateVisual(); }
        UpdateBgm();
    }

    void Sound(ShellSoundEffect e)
    {
        if (_settings.SystemSounds) _audio.Play(e);
    }

    void UpdateBgm()
    {
        bool play = _settings.HomeBgm && IsVisible && !_gameRunning && IsAttached;
        _audio.PlayBgm(play ? _visuals.Theme?.BgmPath ?? _visuals.Assets.SystemBgm : null);
    }

    bool IsAttached => this.GetVisualRoot() != null;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _padTimer.Start();
        UpdateBgm();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _padTimer.Stop();
        _audio.StopBgm();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            UpdateBgm();
            if (IsVisible) _padTimer.Start(); else _padTimer.Stop();
        }
    }

    // ------------------------------------------------------------------ controller

    void PollPad()
    {
        if (_gameRunning || !IsVisible) return;
        if (this.GetVisualRoot() is Window { IsActive: false }) return;
        _pads ??= new SdlGamepads(Program.DataRoot);
        var dev = _pads.Devices.FirstOrDefault(d => d.IsGamepad);
        if (dev == null || !_pads.TryGetState(dev.InstanceId, _pad)) return;
        bool Pressed(PadButton b) => _pad.Buttons[(int)b] && !_prev[(int)b];

        // direction: D-pad or the left stick, repeating while held
        int dir = -1;
        float lx = _pad.Axes[0], ly = _pad.Axes[1];
        if (_pad.Buttons[(int)PadButton.DpadLeft] || lx < -0.6f) dir = 0;
        else if (_pad.Buttons[(int)PadButton.DpadRight] || lx > 0.6f) dir = 1;
        else if (_pad.Buttons[(int)PadButton.DpadUp] || ly < -0.6f) dir = 2;
        else if (_pad.Buttons[(int)PadButton.DpadDown] || ly > 0.6f) dir = 3;
        var now = DateTime.UtcNow;
        if (dir != _repeatDir) { _repeatDir = dir; _repeatAt = now.AddMilliseconds(380); if (dir >= 0) Direction(dir); }
        else if (dir >= 0 && now >= _repeatAt) { _repeatAt = now.AddMilliseconds(120); Direction(dir); }

        if (Pressed(PadButton.South))
        {
            if (_liveArea != null) _liveArea.Start(); else _home.Activate();
        }
        if (Pressed(PadButton.East) && _liveArea != null) CloseLiveArea(sound: true);
        if (_liveArea == null)
        {
            if (Pressed(PadButton.LeftShoulder)) _home.PageBy(-1);
            if (Pressed(PadButton.RightShoulder)) _home.PageBy(1);
        }
        Array.Copy(_pad.Buttons, _prev, _prev.Length);
    }

    void Direction(int dir)
    {
        if (_liveArea != null) return;
        switch (dir)
        {
            case 0: _home.Move(-1, 0); break;
            case 1: _home.Move(1, 0); break;
            case 2: _home.Move(0, -1); break;
            case 3: _home.Move(0, 1); break;
        }
    }

    /// <summary>Closes the controller backend (SDL is process wide: the settings dialog opens its own); it is opened
    /// again on the next poll.</summary>
    public void ReleaseController()
    {
        _pads?.Dispose();
        _pads = null;
    }

    public void Dispose()
    {
        _padTimer.Stop();
        _pads?.Dispose();
        _audio.Dispose();
    }
}
