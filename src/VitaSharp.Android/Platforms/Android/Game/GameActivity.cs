using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Input;
using Android.OS;
using Android.Views;
using Android.Widget;
using VitaSharp.Android.Input;
using VitaSharp.Android.Library;
using VitaSharp.Android.Services;
using VitaSharp.Android.Storage;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Util;
using AView = Android.Views.View;
using Color = Android.Graphics.Color;
using Format = Android.Graphics.Format;

namespace VitaSharp.Android.Game;

/// <summary>
/// The in-game screen (docs/android/mockups/jogo.html), landscape and immersive: the game's SurfaceView (Vulkan), the
/// on-screen controls over it, a small FPS + pause bar at the top and the trophy toast. A plain Android activity rather
/// than a MAUI page: it owns the SurfaceHolder callbacks, the key/motion events of physical controllers and the
/// lifecycle the emulation follows. It runs in its own process (":game"): every game starts in a fresh process, like the
/// desktop launcher's "run", so no emulator state carries over between games and a GPU driver change applies on the
/// next start. Leaving the game ends the process.
/// </summary>
[Activity(Label = "VitaSharp", Process = ":game", Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.SensorLandscape, LaunchMode = LaunchMode.SingleTask, Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.UiMode | ConfigChanges.Density)]
public sealed class GameActivity : Activity, ISurfaceHolderCallback, InputManager.IInputDeviceListener
{
    public const string ExtraTitleId = "titleId";

    /// <summary>The "title id" of "Sistema do Vita": the firmware's own shell (vs0:vsh/shell/shell.self).</summary>
    public const string SystemTitleId = "SYSTEM";

    /// <summary>The shell as a game entry (boots shell.self instead of eboot.bin).</summary>
    public static GameEntry SystemShell() =>
        new(SystemTitleId, "Sistema do Vita", "", "sys", System.IO.Path.Combine(AppServices.Data.Fs, "vs0", "vsh", "shell"), null, 0);

    EmulationSession? _session;
    GameDisplayTuning? _displayTuning;
    AndroidVulkanSurface? _surface;
    SurfaceView? _view;
    OnScreenControlsView? _controls;
    TextView? _fps, _toast;
    readonly OnScreenPad _pad = new();
    AndroidKeyboard? _keyboard;
    AndroidInput? _input;
    InputManager? _inputManager;
    System.Threading.Timer? _fpsTimer;
    long _lastFrames, _framesAtLog, _fpsTicks;
    bool _menuOpen, _isSystem, _endedShown;

    /// <summary>Starts a game from the UI process.</summary>
    public static void Launch(Context context, string titleId)
    {
        var intent = new Intent(context, typeof(GameActivity));
        intent.PutExtra(ExtraTitleId, titleId);
        if (context is not Activity) intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    /// <summary>
    /// From the UI process: ends the running game process (same app, so the UI may kill it) and starts the same game in
    /// a new one, which loads the GPU driver configuration chosen since (a process loads one Vulkan driver).
    /// </summary>
    public static async Task RestartAsync(Context context, GameProcessInfo running)
    {
        Log.Info($"Restarting {running.TitleId} (pid {running.Pid}) for a new GPU driver configuration");
        Process.KillProcess(running.Pid);
        for (int i = 0; i < 60 && GameProcessMarker.IsGameProcess(running.Pid); i++) await Task.Delay(50);
        // the killed process could not remove its marker; the next game writes its own
        GameProcessMarker.Clear(AppServices.Data.GameProcessFile, running.Pid);
        Launch(context, running.TitleId);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AppServices.Initialize(this, gameProcess: true);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        string titleId = Intent?.GetStringExtra(ExtraTitleId) ?? "";
        bool system = titleId == SystemTitleId;
        var game = system ? SystemShell() : AppServices.FindGame(titleId);
        if (game == null)
        {
            Log.Error($"Game {titleId} is not installed");
            Toast.MakeText(this, $"Jogo {titleId} não encontrado", ToastLength.Long)?.Show();
            Finish();
            return;
        }

        var prefs = AppServices.Prefs;
        // a hardware/Bluetooth keyboard: the keyboard profile of input.json (the desktop's default mapping)
        _keyboard = new AndroidKeyboard(AppServices.InputProfiles);
        _input = new AndroidInput(_pad, AppServices.Gamepads, _keyboard);
        if (System.Environment.GetEnvironmentVariable(EnvVars.AndroidInput) is { Length: > 0 } script)
        {
            try { _input.Script = new ScriptedPad(script); Log.Info($"{EnvVars.AndroidInput}: {_input.Script.Count} scripted input steps"); }
            catch (FormatException e) { Log.Warn($"{EnvVars.AndroidInput} ignored: {e.Message}"); }
        }
        // the shell takes the PS button itself (the running game's LiveArea); a game on its own opens the pause menu
        if (system) _pad.Layout = OnScreenPad.SystemLayout;
        else _pad.PsPressed += OnPsPressed;
        _isSystem = system;

        var root = new FrameLayout(this);
        root.SetBackgroundColor(Color.Black);
        _view = new SurfaceView(this);
        _view.Holder?.AddCallback(this);
        root.AddView(_view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _controls = new OnScreenControlsView(this, _pad) { ControlsOpacity = prefs.OnScreenOpacity, Haptics = prefs.Haptics };
        root.AddView(_controls, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        root.AddView(TopBar(), new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.CenterHorizontal));
        _toast = Chip("", 14);
        _toast.Visibility = ViewStates.Gone;
        root.AddView(_toast, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Right)
        { TopMargin = Dp(12), RightMargin = Dp(96) });
        SetContentView(root);
        // after SetContentView: Window.InsetsController needs the decor view, which does not exist before it (a
        // NullPointerException inside PhoneWindow.getInsetsController on API 30+)
        EnterImmersive();
        _view.ViewTreeObserver!.GlobalLayout += (_, _) => UpdateImageRect();

        // physical controllers: the ones already connected, then hot-plug
        _inputManager = (InputManager?)GetSystemService(InputService);
        _inputManager?.RegisterInputDeviceListener(this, null);
        foreach (int id in InputDevice.GetDeviceIds() ?? []) OnInputDeviceAdded(id);
        AppServices.Gamepads.DevicesChanged += OnGamepadsChanged;
        OnGamepadsChanged();

        // back opens the pause menu: OnBackPressed below API 33, the back-invoked callback from 33 on (predictive
        // back, the default for apps targeting API 36, no longer calls OnBackPressed)
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, new BackCallback(OpenMenu));

        _ = StartSessionAsync(game);
    }

    async Task StartSessionAsync(GameEntry game)
    {
        string password = await AppServices.RpcnPasswordAsync();
        try
        {
            _session = new EmulationSession(game, _input!, password);
        }
        catch (Exception e)
        {
            Log.Error($"Session for {game.TitleId}: {e}");
            ShowFatal($"Não foi possível preparar o jogo: {e.Message}");
            return;
        }
        _session.Failed += msg => RunOnUiThread(() => ShowFatal(msg));
        _displayTuning = GameDisplayTuning.Start(this, _view!, _session.Performance);
        _session.TrophyUnlocked += e => RunOnUiThread(() => ShowTrophy(e));
        // games ask for text through the IME dialog / SceIme: an Android text field with the system soft keyboard
        var ime = new AndroidImeDialog(this);
        _session.SetTextInput(ime, ime);
        if (_surface != null) _session.SurfaceCreated(_surface);
        _fpsTimer = new System.Threading.Timer(_ => RunOnUiThread(UpdateFps), null, 1000, 1000);
    }

    // ------------------------------------------------------------------ surface

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        if (holder.Surface is not { IsValid: true } s) return;
        _surface = new AndroidVulkanSurface(s);
        _session?.SurfaceCreated(_surface);
        _displayTuning?.SurfaceCreated();
    }

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
    {
        Log.Info($"Surface {width}x{height}");
        UpdateImageRect();
        _session?.SurfaceChanged();
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        _session?.SurfaceDestroyed();
        _surface?.Dispose();
        _surface = null;
    }

    /// <summary>The 960x544 image is scaled to the view's height and centred (the renderer letterboxes the same way):
    /// the pad needs its rectangle to turn image touches into front-touch points.</summary>
    void UpdateImageRect()
    {
        if (_view is not { Width: > 0, Height: > 0 } v) return;
        float scale = Math.Min(v.Width / 960f, v.Height / 544f);
        float iw = 960 * scale / v.Width, ih = 544 * scale / v.Height;
        _pad.Image = ((1 - iw) / 2, (1 - ih) / 2, iw, ih);
    }

    // ------------------------------------------------------------------ lifecycle

    protected override void OnPause()
    {
        base.OnPause();
        _pad.Reset();
        _keyboard?.ReleaseAll();
        _session?.Pause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        EnterImmersive();
        if (!_menuOpen) _session?.Resume();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) EnterImmersive();
        // a dialog (pause menu, IME text entry) took the key events: the key-up of a key held now would never come
        // back here, and the game would see it held after the dialog closes
        else _keyboard?.ReleaseAll();
    }

    protected override void OnDestroy()
    {
        _fpsTimer?.Dispose();
        AppServices.Gamepads.DevicesChanged -= OnGamepadsChanged;
        _inputManager?.UnregisterInputDeviceListener(this);
        _session?.Dispose();
        _session = null;
        _surface?.Dispose();
        base.OnDestroy();
        // the game process exists for one game: end it so the next game starts clean
        if (IsFinishing) Process.KillProcess(Process.MyPid());
    }

    public override void OnBackPressed() => OpenMenu();

    void EnterImmersive()
    {
        if (Window is not { } w) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (w.InsetsController is { } c)
            {
                c.Hide(WindowInsets.Type.SystemBars());
                c.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
        }
        else
        {
#pragma warning disable CA1422 // the pre-API 30 way, only taken below API 30
            w.DecorView.SystemUiFlags = SystemUiFlags.ImmersiveSticky | SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen
                | SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen;
#pragma warning restore CA1422
        }
    }

    // ------------------------------------------------------------------ physical controllers

    static bool IsGameController(InputDevice? d) => d != null && !d.IsVirtual
        && ((d.Sources & InputSourceType.Gamepad) == InputSourceType.Gamepad || (d.Sources & InputSourceType.Joystick) == InputSourceType.Joystick);

    public void OnInputDeviceAdded(int deviceId)
    {
        var d = InputDevice.GetDevice(deviceId);
        if (!IsGameController(d)) return;
        var axes = d!.MotionRanges?.Select(r => (int)r.Axis).Distinct().ToArray() ?? [];
        AppServices.Gamepads.Connect(deviceId, d.Name ?? "Controle", d.VendorId, d.ProductId, axes);
        Log.Info($"Controller connected: {d.Name} ({d.VendorId:X4}:{d.ProductId:X4}), axes {string.Join(",", axes)}");
    }

    public void OnInputDeviceChanged(int deviceId) => OnInputDeviceAdded(deviceId);

    public void OnInputDeviceRemoved(int deviceId)
    {
        AppServices.Gamepads.Disconnect(deviceId);
        Log.Info($"Controller {deviceId} disconnected");
    }

    void OnGamepadsChanged() => RunOnUiThread(() =>
    {
        bool hide = AppServices.Prefs.HideOnScreenWithGamepad && AppServices.Gamepads.AnyConnected;
        _pad.Visible = !hide;
        _controls?.Invalidate();
    });

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e != null && e.Action is KeyEventActions.Down or KeyEventActions.Up && e.RepeatCount == 0
            && AppServices.Gamepads.OnKey(e.DeviceId, (int)e.KeyCode, e.Action == KeyEventActions.Down))
        {
            // the PS/Home button of a controller in a game without the shell: the pause menu, like the on-screen PS
            if (e.KeyCode == Keycode.ButtonMode && e.Action == KeyEventActions.Down && !_isSystem) OnPsPressed();
            return true;
        }
        return base.DispatchKeyEvent(e);
    }

    // ------------------------------------------------------------------ hardware keyboard

    // keys no view consumed (nothing in this activity takes the focus) and that are not a controller's: the keyboard
    // profile. Unmapped keys (volume, Back, Escape, which Android turns into Back) keep their system meaning.
    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e) => OnKeyboardKey(keyCode, e, true) || base.OnKeyDown(keyCode, e);

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e) => OnKeyboardKey(keyCode, e, false) || base.OnKeyUp(keyCode, e);

    bool OnKeyboardKey(Keycode keyCode, KeyEvent? e, bool down)
    {
        if (_keyboard == null || e == null || (e.Source & InputSourceType.Keyboard) != InputSourceType.Keyboard) return false;
        // a controller's own keys (and their auto-repeats, which DispatchKeyEvent leaves to us) are not keyboard keys
        if (AppServices.Gamepads.IsConnected(e.DeviceId)) return false;
        var action = _keyboard.OnKey((int)keyCode, down, e.RepeatCount > 0);
        if ((action & KeyboardKeyAction.Pause) != 0) OpenMenu();
        // the PS key in a game without the shell: the pause menu, like the on-screen PS and a controller's PS button
        if ((action & KeyboardKeyAction.PsButton) != 0 && !_isSystem) OnPsPressed();
        return action != KeyboardKeyAction.None;
    }

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && (e.Source & InputSourceType.Joystick) == InputSourceType.Joystick && e.Action == MotionEventActions.Move
            && AppServices.Gamepads.OnMotion(e.DeviceId, axis => e.GetAxisValue((Axis)axis)))
            return true;
        return base.OnGenericMotionEvent(e);
    }

    // ------------------------------------------------------------------ chrome: FPS bar, pause menu, toasts

    AView TopBar()
    {
        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.SetPadding(Dp(4), Dp(6), Dp(4), 0);
        _fps = Chip("— FPS", 12);
        bar.AddView(_fps);
        var pause = Chip("❚❚", 12);
        pause.Click += (_, _) => OpenMenu();
        // not focusable: arrow keys of a keyboard (or a D-pad that is not a game controller) moved the focus onto it
        // and drew Android's focus highlight over the game
        pause.Focusable = false;
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(8) };
        bar.AddView(pause, lp);
        return bar;
    }

    TextView Chip(string text, int sp)
    {
        var t = new TextView(this) { Text = text, TextSize = sp };
        t.SetTextColor(Color.ParseColor("#E8ECF2"));
        t.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        var bg = new global::Android.Graphics.Drawables.GradientDrawable();
        bg.SetColor(Color.ParseColor("#B3171B22"));
        bg.SetCornerRadius(Dp(999));
        t.Background = bg;
        return t;
    }

    int Dp(float dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 2f) + 0.5f);

    void UpdateFps()
    {
        if (_session == null || _fps == null) return;
        // the guest process stopped by itself (exit, stack-protector failure, crash): say so instead of a frozen image
        if (_session.Ended && !_endedShown)
        {
            _endedShown = true;
            Log.Warn($"The process of {_session.Game.TitleId} ended");
            ShowFatal("O jogo terminou.");
            return;
        }
        long frames = _session.Frames;
        long fps = frames - _lastFrames;
        _lastFrames = frames;
        _fps.Text = _session.Paused ? "Pausado" : $"{fps} FPS";
        // every 30 s in the log (device runs are read from logcat): frame rate and what the CPU backends compiled
        if (++_fpsTicks % 30 == 0 && !_session.Paused)
        {
            Log.Info($"[perf] {(frames - _framesAtLog) / 30.0:F1} fps over 30 s, {frames} frames");
            if (Core.Cpu.Jit.Enabled) Log.Info($"[perf] {Core.Cpu.Jit.Describe()}");
            Log.Info($"[perf] audio: {_session.AudioSummary}");
            _framesAtLog = frames;
        }
        _fps.Visibility = _session.Settings.ShowFps || _session.Paused ? ViewStates.Visible : ViewStates.Invisible;
    }

    void ShowTrophy(TrophyUnlockedEvent e)
    {
        if (_toast == null) return;
        _toast.Text = $"🏆 Troféu desbloqueado\n{e.Trophy.Name} · {TrophyList.GradeName(e.Trophy.Grade)}";
        _toast.Visibility = ViewStates.Visible;
        _toast.PostDelayed(() => _toast.Visibility = ViewStates.Gone, 5000);
        _session?.PlayTrophySound();
    }

    void OnPsPressed() => RunOnUiThread(OpenMenu);

    /// <summary>The pause menu (pausa.html, reduced): continue, rear touch, on-screen controls, leave.</summary>
    void OpenMenu()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        _session?.Pause();
        string[] items =
        [
            "Continuar",
            _pad.RearTouch ? "Toque traseiro: ligado" : "Toque traseiro: desligado",
            _pad.Visible ? "Esconder controles na tela" : "Mostrar controles na tela",
            "Sair do jogo",
        ];
        var title = _session != null ? $"{_session.Game.Title}" : "VitaSharp";
        new AlertDialog.Builder(this)
            .SetTitle(title)!
            .SetItems(items, (_, a) =>
            {
                switch (a.Which)
                {
                    case 1: _pad.RearTouch = !_pad.RearTouch; break;
                    case 2: _pad.Visible = !_pad.Visible; break;
                    case 3: Leave(); return;
                }
                _controls?.Invalidate();
            })!
            .SetOnDismissListener(new DismissListener(() =>
            {
                _menuOpen = false;
                if (!IsFinishing) _session?.Resume();
                EnterImmersive();
            }))!
            .Show();
    }

    /// <summary>Leaves the game: the statistics are written first, since the screens re-read them as soon as this
    /// activity finishes (before its onDestroy).</summary>
    void Leave()
    {
        _session?.RecordStatistics();
        Finish();
    }

    void ShowFatal(string message)
    {
        _session?.Pause();
        new AlertDialog.Builder(this)
            .SetTitle("VitaSharp")!
            .SetMessage(message + "\n\nO log completo está em " + System.IO.Path.Combine(AppServices.Data.Logs, "last.log"))!
            .SetPositiveButton("Sair", (_, _) => Leave())!
            .SetCancelable(false)!
            .Show();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    sealed class BackCallback(Action onBack) : Java.Lang.Object, global::Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked() => onBack();
    }

    sealed class DismissListener(Action onDismiss) : Java.Lang.Object, IDialogInterfaceOnDismissListener
    {
        public void OnDismiss(IDialogInterface? dialog) => onDismiss();
    }
}
