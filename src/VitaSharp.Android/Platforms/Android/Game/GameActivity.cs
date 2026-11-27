using VitaSharp.Core.Localization;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Input;
using Android.OS;
using Android.Views;
using Android.Widget;
using VitaSharp.Android.Services;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Input;
using VitaSharp.Mobile.Core.Library;
using VitaSharp.Mobile.Core.Settings;
using VitaSharp.Mobile.Core.Storage;
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
[Activity(Label = "VitaSharp", Process = ":game", Theme = "@style/VitaSharp.GameTheme",
    ScreenOrientation = ScreenOrientation.SensorLandscape, LaunchMode = LaunchMode.SingleTask, Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.UiMode | ConfigChanges.Density)]
public sealed partial class GameActivity : Activity, ISurfaceHolderCallback, InputManager.IInputDeviceListener
{
    public const string ExtraTitleId = "titleId";

    /// <summary>The "title id" of "Sistema do Vita": the firmware's own shell (vs0:vsh/shell/shell.self).</summary>
    public const string SystemTitleId = "SYSTEM";

    /// <summary>The shell as a game entry (boots shell.self instead of eboot.bin).</summary>
    public static GameEntry SystemShell() =>
        new(SystemTitleId, Loc.Get("MVitaSystem"), "", "sys", System.IO.Path.Combine(AppServices.Data.Fs, "vs0", "vsh", "shell"), null, 0);

    EmulationSession? _session;
    GameDisplayTuning? _displayTuning;
    AndroidVulkanSurface? _surface;
    SurfaceView? _view;
    OnScreenControlsView? _controls;
    TextView? _fps, _toast;
    // the pause chip over the top centre (the Vita's status bar): it hides while the screen is left alone
    TextView? _pauseChip;
    TextView? _controlsChip;
    readonly ChromeAutoHide _chrome = new(SystemClock.UptimeMillis());
    readonly OnScreenPad _pad = new();
    AndroidKeyboard? _keyboard;
    AndroidInput? _input;
    GameSensors? _sensors;
    InputManager? _inputManager;
    System.Threading.Timer? _fpsTimer;
    long _lastFrames, _framesAtLog, _fpsTicks;
    bool _menuOpen, _isSystem, _endedShown;

    /// <summary>
    /// Set by a launch-intent start (<c>am start ... --es titleId</c>: shortcuts and the device-test scripts): the game
    /// turns the screen on and shows over a non-secure lock screen, so a scripted run on a sleeping phone still renders.
    /// Starts from the library leave the screen and the keyguard alone.
    /// </summary>
    public const string ExtraWakeScreen = "wakeScreen";

    /// <summary>The save state slot (1-6) the game opens in: a slot tapped on the game page, the desktop's --load-state.</summary>
    public const string ExtraLoadSlot = "loadSlot";

    /// <summary>Starts a game from the UI process; <paramref name="loadSlot"/> 1-6 opens it already in that save state.</summary>
    public static void Launch(Context context, string titleId, bool wakeScreen = false, int loadSlot = 0)
    {
        var intent = new Intent(context, typeof(GameActivity));
        intent.PutExtra(ExtraTitleId, titleId);
        if (wakeScreen)
        {
            intent.PutExtra(ExtraWakeScreen, true);
        }

        int slot = GameStates.LaunchSlot(loadSlot);
        if (slot > 0)
        {
            intent.PutExtra(ExtraLoadSlot, slot);
        }
        if (context is not Activity)
            intent.AddFlags(ActivityFlags.NewTask);
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
        for (int i = 0; i < 60 && GameProcessMarker.IsGameProcess(running.Pid); i++)
            await Task.Delay(50);
        // the killed process could not remove its marker; the next game writes its own
        GameProcessMarker.Clear(AppServices.Data.GameProcessFile, running.Pid);
        Launch(context, running.TitleId);
    }

    /// <summary>
    /// A launch-intent start (<see cref="ExtraWakeScreen"/>): screen on, shown over the lock screen, and a non-secure
    /// keyguard dismissed (a PIN/pattern keyguard stays; the game then renders behind it until it is unlocked).
    /// </summary>
    void WakeScreen()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(27))
        {
            SetTurnScreenOn(true);
            SetShowWhenLocked(true);
            if (GetSystemService(KeyguardService) is KeyguardManager km)
            {
                if (!km.IsKeyguardSecure)
                    km.RequestDismissKeyguard(this, null);
                else if (km.IsKeyguardLocked)
                    Log.Warn("Launch intent: the lock screen is secure (PIN/pattern); unlock the phone to see the game");
            }
        }
        else
        {
#pragma warning disable CA1422 // the pre-27 flags
            Window?.AddFlags(WindowManagerFlags.TurnScreenOn | WindowManagerFlags.ShowWhenLocked | WindowManagerFlags.DismissKeyguard);
#pragma warning restore CA1422
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AppServices.Initialize(this, gameProcess: true);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        if (Intent?.GetBooleanExtra(ExtraWakeScreen, false) == true)
            WakeScreen();

        string titleId = Intent?.GetStringExtra(ExtraTitleId) ?? "";
        bool system = titleId == SystemTitleId;
        var game = system ? SystemShell() : AppServices.FindGame(titleId);
        if (game == null)
        {
            Log.Error($"Game {titleId} is not installed");
            Toast.MakeText(this, Loc.Format("MGameNotFound", titleId), ToastLength.Long)?.Show();
            Finish();
            return;
        }

        var prefs = AppServices.Prefs;
        // a hardware/Bluetooth keyboard: the keyboard profile of input.json (the desktop's default mapping)
        _keyboard = new AndroidKeyboard(AppServices.InputProfiles);
        _input = new AndroidInput(_pad, AppServices.Gamepads, _keyboard);
        string? script = System.Environment.GetEnvironmentVariable(EnvVars.AndroidInput) is { Length: > 0 } sc ? sc : null;
        string? livePath = System.Environment.GetEnvironmentVariable(EnvVars.AndroidInputLive) is { Length: > 0 } lp ? lp : null;
        if (script != null || livePath != null)
        {
            try
            {
                _input.Script = new ScriptedPad(script ?? "") { LivePath = livePath };
                _input.Script.StateRequested += (save, slot) => RunOnUiThread(() => ScriptedState(save, slot));
                Log.Info($"{EnvVars.AndroidInput}: {_input.Script.Count} scripted input steps");
            }
            catch (FormatException e)
            {
                Log.Warn($"{EnvVars.AndroidInput} ignored: {e.Message}");
            }
        }
        _sensors = new GameSensors(this); // SceMotion from the phone's sensors, SceCamera from its cameras
        // the shell takes the PS button itself (the running game's LiveArea); a game on its own opens the pause menu
        if (system)
            _pad.Layout = OnScreenPad.SystemLayout;
        else
            _pad.PsPressed += OnPsPressed;
        _isSystem = system;

        var root = _root = new FrameLayout(this);
        root.SetBackgroundColor(Color.Black);
        _view = new SurfaceView(this);
        _view.Holder?.AddCallback(this);
        root.AddView(_view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _controls = new OnScreenControlsView(this, _pad) { ControlsOpacity = prefs.OnScreenOpacity, Haptics = prefs.Haptics, HapticStrength = prefs.HapticStrength };
        root.AddView(_controls, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _controls.FingerDown += OnFingerDown;
        root.AddView(TopBar(), new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top));
        _toast = Chip("", 14);
        _toast.Visibility = ViewStates.Gone;
        root.AddView(_toast, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Right)
        {
            TopMargin = Dp(12),
            RightMargin = Dp(96)
        });
        // the loading screen from the tap on, over everything until the game is ready (GameActivity.LoadingScreen.cs)
        AddLoadingScreen(root, game);
        SetContentView(root);
        // after SetContentView: Window.InsetsController needs the decor view, which does not exist before it (a
        // NullPointerException inside PhoneWindow.getInsetsController on API 30+)
        EnterImmersive();
        _view.ViewTreeObserver!.GlobalLayout += (_, _) => UpdateImageRect();

        // physical controllers: the ones already connected, then hot-plug
        _inputManager = (InputManager?)GetSystemService(InputService);
        _inputManager?.RegisterInputDeviceListener(this, null);
        foreach (int id in InputDevice.GetDeviceIds() ?? [])
            OnInputDeviceAdded(id);
        AppServices.Gamepads.DevicesChanged += OnGamepadsChanged;
        OnGamepadsChanged();

        // back opens the pause menu: OnBackPressed below API 33, the back-invoked callback from 33 on (predictive
        // back, the default for apps targeting API 36, no longer calls OnBackPressed)
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, new BackCallback(OpenMenu));

#if VITASHARP_DEVELOPER
        Developer.UiScript.Start(this, ".game");
#endif
        int loadSlot = system ? 0 : GameStates.LaunchSlot(Intent?.GetIntExtra(ExtraLoadSlot, 0) ?? 0);
        _ = StartSessionAsync(game, loadSlot);
    }

    /// <summary>The running game started again from its page with a save state slot (singleTask: the same activity):
    /// that state is loaded into it, as picking the slot in the pause menu does.</summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        int slot = GameStates.LaunchSlot(intent?.GetIntExtra(ExtraLoadSlot, 0) ?? 0);
        if (slot > 0 && _session is { } session && intent?.GetStringExtra(ExtraTitleId) == session.Game.TitleId)
        {
            ScriptedState(save: false, slot);
        }
    }

    async Task StartSessionAsync(GameEntry game, int loadSlot)
    {
        string password = await AppServices.RpcnPasswordAsync();
        try
        {
            _session = new EmulationSession(game, _input!, password, loadSlot);
        }
        catch (Exception e)
        {
            Log.Error($"Session for {game.TitleId}: {e}");
            string message = Loc.Format("MPrepareFailed", e.Message);
            FailLoadingScreen(message);
            ShowFatal(message);
            return;
        }
        _session.Failed += msg => RunOnUiThread(() =>
        {
            FailLoadingScreen(msg);
            ShowFatal(msg);
        });
        // the game quit by itself: back to the screens, as the Vita goes back to its home screen
        _session.Exited += () => RunOnUiThread(Leave);
        // the emulated system rebooted: the same title boots again on this screen, as the console's boot runs again
        _session.Rebooted += () => RunOnUiThread(() => Reboot(game));
        _displayTuning = GameDisplayTuning.Start(this, _view!, _session.Performance);
        _session.TrophyUnlocked += e => RunOnUiThread(() => ShowTrophy(e));
        // "Gravar tudo": started, saving, saved (with the folder), failed
        _session.CaptureStatus += s => RunOnUiThread(() => Toast.MakeText(this, s, ToastLength.Long)?.Show());
        // games ask for text through the IME dialog / SceIme: an Android text field with the system soft keyboard
        var ime = new AndroidImeDialog(this);
        _session.SetTextInput(ime, ime);
        _sensors?.Attach(_session);
        if (_surface != null)
            _session.SurfaceCreated(_surface);
        _fpsTimer = new System.Threading.Timer(_ => RunOnUiThread(UpdateFps), null, 1000, 1000);
    }

    // ------------------------------------------------------------------ surface

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        if (holder.Surface is not { IsValid: true } s)
            return;
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

    (int, int) _touchLayoutLogged;

    /// <summary>The 960x544 image is scaled to the view's height and centred (the renderer letterboxes the same way):
    /// the pad needs its rectangle to turn image touches into front-touch points.</summary>
    void UpdateImageRect()
    {
        if (_view is not { Width: > 0, Height: > 0 } v)
            return;
        float scale = Math.Min(v.Width / 960f, v.Height / 544f);
        float iw = 960 * scale / v.Width, ih = 544 * scale / v.Height;
        _pad.Image = ((1 - iw) / 2, (1 - ih) / 2, iw, ih);
        // the controls' view is the same size: round controls are hit as the circles drawn on it
        _pad.Aspect = v.Width / (float)v.Height;
        // the touch mapping for the log, once per layout change: view size, density and where the image sits
        if (_touchLayoutLogged != (v.Width, v.Height))
        {
            _touchLayoutLogged = (v.Width, v.Height);
            Log.Info(FormattableString.Invariant($"Touch: view {v.Width}x{v.Height}, density {Resources?.DisplayMetrics?.Density ?? 0:F2}, image at x {(1 - iw) / 2:F4} y {(1 - ih) / 2:F4} size {iw:F4}x{ih:F4}"));
        }
        ApplySurfaceSize(v);
    }

    /// <summary><see cref="EnvVars.AndroidSurfaceSize"/>: the size the game's surface buffers are allocated at, below the
    /// view's (the display hardware scales them up); "game" = the height of the Vita's screen.</summary>
    static readonly string? SurfaceSize = System.Environment.GetEnvironmentVariable(EnvVars.AndroidSurfaceSize)?.Trim().ToLowerInvariant();
    (int, int) _surfaceSizeSet;

    void ApplySurfaceSize(SurfaceView v)
    {
        if (string.IsNullOrEmpty(SurfaceSize) || v.Holder is not { } holder)
            return;
        int h = SurfaceSize switch
        {
            "game" => 544,
            "half" => v.Height / 2,
            _ => int.TryParse(SurfaceSize, out int px) ? px : 0,
        };
        if (h <= 0 || h >= v.Height)
            return;
        int w = (int)Math.Round((double)v.Width * h / v.Height);
        if (_surfaceSizeSet == (w, h))
            return;
        _surfaceSizeSet = (w, h);
        holder.SetFixedSize(w, h);
        Log.Info($"Game surface buffers {w}x{h} (view {v.Width}x{v.Height}, {EnvVars.AndroidSurfaceSize}={SurfaceSize})");
    }

    // ------------------------------------------------------------------ lifecycle

    protected override void OnPause()
    {
        base.OnPause();
        _pad.Reset();
        _keyboard?.ReleaseAll();
        _session?.Pause();
        _sensors?.Pause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        EnterImmersive();
        // back from the screens (another process): a language changed there applies to the controls and the menu
        AppServices.RefreshPreferences();
        _controls?.Invalidate();
        if (!_menuOpen)
            _session?.Resume();
        _sensors?.Resume();
    }

    // the CAMERA and RECORD_AUDIO permissions, asked when a game first starts a camera (AndroidCamera) or opens its
    // microphone port (AndroidMicrophone)
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (_sensors?.OnRequestPermissionsResult(requestCode, permissions, grantResults) != true)
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
            EnterImmersive();
        // a dialog (IME text entry, the fatal message) took the key events: the key-up of a key held now would never come
        // back here, and the game would see it held after the dialog closes
        else
            _keyboard?.ReleaseAll();
    }

    protected override void OnDestroy()
    {
        _fpsTimer?.Dispose();
        RemoveLoadingScreen();
        AppServices.Gamepads.DevicesChanged -= OnGamepadsChanged;
        _inputManager?.UnregisterInputDeviceListener(this);
        _session?.Dispose();
        _session = null;
        _sensors?.Dispose();
        _surface?.Dispose();
        base.OnDestroy();
        // the game process exists for one game: end it so the next game starts clean
        if (IsFinishing)
            Process.KillProcess(Process.MyPid());
    }

    public override void OnBackPressed() => OpenMenu();

    void EnterImmersive()
    {
        if (Window is not { } w)
            return;
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
        if (!IsGameController(d))
            return;
        var axes = d!.MotionRanges?.Select(r => (int)r.Axis).Distinct().ToArray() ?? [];
        AppServices.Gamepads.Connect(deviceId, d.Name ?? Loc.Get("MController"), d.VendorId, d.ProductId, axes);
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
        OnScreenVisibility.Apply(_pad, AppServices.Prefs, AppServices.Gamepads.AnyConnected);
        OnControlsVisibilityChanged();
    });

    /// <summary>The button by the fps counter and the pause menu's "Controles na tela": shows or hides the on-screen
    /// controls and saves the choice (<see cref="OnScreenVisibility"/>).</summary>
    void SetControlsShown(bool shown)
    {
        OnScreenVisibility.Set(_pad, AppServices.Prefs, shown);
        AppServices.SavePrefs();
        OnControlsVisibilityChanged();
    }

    void OnControlsVisibilityChanged()
    {
        _controls?.Invalidate();
        if (_controlsChip is { } chip)
        {
            // dimmed while the controls are hidden, like a switch that is off
            chip.Alpha = _pad.Visible ? 1f : 0.55f;
        }
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        // the pause menu takes a controller's keys (focus, press, back) instead of the paused game
        if (e != null && MenuVisible && (AppServices.Gamepads.IsConnected(e.DeviceId) || (e.Source & InputSourceType.Gamepad) == InputSourceType.Gamepad) && MenuControllerKey(e))
            return true;
        if (e != null && !MenuVisible && e.Action is KeyEventActions.Down or KeyEventActions.Up && e.RepeatCount == 0
            && AppServices.Gamepads.OnKey(e.DeviceId, (int)e.KeyCode, e.Action == KeyEventActions.Down))
        {
            // the PS/Home button of a controller in a game without the shell: the pause menu, like the on-screen PS
            if (e.KeyCode == Keycode.ButtonMode && e.Action == KeyEventActions.Down && !_isSystem)
                OnPsPressed();
            // the pause hotkey of Controles > Mapeamento (a combination fires only with all its buttons held)
            if (AppServices.Gamepads.PauseHotkeyPressed())
            {
                OpenMenu();
            }

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
        if (_keyboard == null || e == null || (e.Source & InputSourceType.Keyboard) != InputSourceType.Keyboard)
            return false;
        // the pause menu is open: arrows and Enter move and press its buttons (Android's focus), Escape goes back
        if (MenuVisible)
            return false;
        // a controller's own keys (and their auto-repeats, which DispatchKeyEvent leaves to us) are not keyboard keys
        if (AppServices.Gamepads.IsConnected(e.DeviceId))
            return false;
        // the paired Bluetooth keyboard (the setting): the key also goes to it as a USB HID usage
        bool toHid = false;
        if (_session?.HidKeyboard is { } hidKeyboard && e.RepeatCount == 0
            && VitaSharp.Core.Host.HidUsages.FromAndroidKeyCode((int)keyCode) is var usage and not 0)
        {
            hidKeyboard.Key(usage, down, VitaSharp.Core.Hle.Modules.Hid.NowUs);
            toHid = true;
        }

        // its media keys go to its consumer-control device, and on to the phone too (the volume keys keep working)
        if (_session?.HidKeyboard is { } mediaKeyboard && e.RepeatCount == 0
            && VitaSharp.Core.Host.HidUsages.ConsumerFromAndroidKeyCode((int)keyCode) is var consumerUsage and not 0)
        {
            mediaKeyboard.ConsumerControl.Key(consumerUsage, down, VitaSharp.Core.Hle.Modules.Hid.NowUs);
        }

        var action = _keyboard.OnKey((int)keyCode, down, e.RepeatCount > 0);
        if ((action & KeyboardKeyAction.Pause) != 0)
            OpenMenu();
        // the PS key in a game without the shell: the pause menu, like the on-screen PS and a controller's PS button
        if ((action & KeyboardKeyAction.PsButton) != 0 && !_isSystem)
            OnPsPressed();
        return action != KeyboardKeyAction.None || toHid;
    }

    /// <summary>The last pointer position the paired mouse moved from.</summary>
    float _hidMouseX = float.NaN, _hidMouseY = float.NaN;

    /// <summary>A mouse event for the paired Bluetooth mouse (the setting): its travel, wheel and buttons.</summary>
    bool OnHidMouse(MotionEvent e)
    {
        if (_session?.HidMouse is not { } mouse || (e.Source & InputSourceType.Mouse) != InputSourceType.Mouse)
            return false;
        ulong now = VitaSharp.Core.Hle.Modules.Hid.NowUs;
        switch (e.Action)
        {
            case MotionEventActions.HoverMove:
            case MotionEventActions.Move:
                if (!float.IsNaN(_hidMouseX))
                    mouse.Move((int)MathF.Round(e.GetX() - _hidMouseX), (int)MathF.Round(e.GetY() - _hidMouseY), 0, now);
                _hidMouseX = e.GetX();
                _hidMouseY = e.GetY();
                return true;
            case MotionEventActions.Scroll:
                mouse.Move(0, 0, (int)MathF.Round(e.GetAxisValue(Axis.Vscroll)), now);
                return true;
            case MotionEventActions.ButtonPress:
            case MotionEventActions.ButtonRelease:
                int button = e.ActionButton switch
                {
                    MotionEventButtonState.Primary => 0,
                    MotionEventButtonState.Secondary => 1,
                    MotionEventButtonState.Tertiary => 2,
                    _ => -1,
                };
                mouse.Button(button, e.Action == MotionEventActions.ButtonPress, now);
                return true;
            default:
                return false;
        }
    }

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && !MenuVisible && OnHidMouse(e))
        {
            return true;
        }

        // the pause menu: sticks and hats are left to Android, which turns them into D-pad keys that move the focus
        if (e != null && !MenuVisible && (e.Source & InputSourceType.Joystick) == InputSourceType.Joystick && e.Action == MotionEventActions.Move
            && AppServices.Gamepads.OnMotion(e.DeviceId, axis => e.GetAxisValue((Axis)axis)))
        {
            if (AppServices.Gamepads.PauseHotkeyPressed())
            {
                OpenMenu(); // a hotkey with a trigger in it
            }

            return true;
        }
        return base.OnGenericMotionEvent(e);
    }

    // ------------------------------------------------------------------ chrome: FPS bar, pause menu, toasts

    /// <summary>The top row (<see cref="GameChromeLayout"/>): the fps counter in the centre, the pause chip on the right
    /// and the on-screen controls chip on the left.</summary>
    AView TopBar()
    {
        var bar = new FrameLayout(this);
        _fps = Chip("— FPS", 12);
        bar.AddView(_fps, ChromeSlot(GameChromeItem.Fps));
        var pause = Chip("❚❚", 12);
        _pauseChip = pause;
        pause.Click += (_, _) => OpenMenu();
        // not focusable: arrow keys of a keyboard (or a D-pad that is not a game controller) moved the focus onto it
        // and drew Android's focus highlight over the game
        pause.Focusable = false;
        bar.AddView(pause, ChromeSlot(GameChromeItem.Pause));
        pause.PostDelayed(UpdatePauseChip, ChromeAutoHide.HideAfterMs);
        // shows/hides the on-screen controls; it hides and comes back with the pause chip (ChromeAutoHide), also
        // when the fps counter is off (the counter keeps its place, invisible)
        var controlsChip = Chip("🎮", 12);
        _controlsChip = controlsChip;
        controlsChip.ContentDescription = Loc.Get("MOnScreenControls");
        controlsChip.Focusable = false;
        controlsChip.Click += (_, _) => SetControlsShown(!_pad.Visible);
        bar.AddView(controlsChip, ChromeSlot(GameChromeItem.Controls));
        OnControlsVisibilityChanged();
        return bar;
    }

    /// <summary>A chip's place along the top edge (<see cref="GameChromeLayout.SlotOf"/>).</summary>
    FrameLayout.LayoutParams ChromeSlot(GameChromeItem item)
    {
        GravityFlags horizontal = GameChromeLayout.SlotOf(item) switch
        {
            GameChromeSlot.Left => GravityFlags.Left,
            GameChromeSlot.Right => GravityFlags.Right,
            _ => GravityFlags.CenterHorizontal,
        };
        return new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | horizontal)
        {
            TopMargin = Dp(GameChromeLayout.TopMarginDp),
            LeftMargin = Dp(GameChromeLayout.EdgeMarginDp),
            RightMargin = Dp(GameChromeLayout.EdgeMarginDp),
        };
    }

    /// <summary>A finger went down on the game screen (the controls' view coordinates): the pause chip shows again
    /// unless the finger is on its place while it is hidden (that touch is the game's).</summary>
    void OnFingerDown(float x, float y)
    {
        if (_pauseChip is not { } chip || _controls is not { } controls)
        {
            return;
        }

        bool onChip = IsOnView(chip, controls, x, y) || (_controlsChip is { } controlsChip && IsOnView(controlsChip, controls, x, y));
        _chrome.FingerDown(SystemClock.UptimeMillis(), onChip);
        UpdatePauseChip();
    }

    // whether (x, y) of the controls' view is on the place of view
    static bool IsOnView(AView view, AView controls, float x, float y)
    {
        int[] viewAt = new int[2], controlsAt = new int[2];
        view.GetLocationInWindow(viewAt);
        controls.GetLocationInWindow(controlsAt);
        float cx = x + controlsAt[0] - viewAt[0], cy = y + controlsAt[1] - viewAt[1];
        return cx >= 0 && cy >= 0 && cx < view.Width && cy < view.Height;
    }

    /// <summary>Shows or hides the pause chip by <see cref="ChromeAutoHide"/> and checks again when it is due to hide.</summary>
    void UpdatePauseChip()
    {
        if (_pauseChip is not { } chip)
        {
            return;
        }

        long now = SystemClock.UptimeMillis();
        bool visible = _chrome.Visible(now);
        // invisible: the chip takes no touches, they reach the game below it
        chip.Visibility = visible ? ViewStates.Visible : ViewStates.Invisible;
        if (_controlsChip is { } controlsChip)
        {
            controlsChip.Visibility = chip.Visibility;
        }
        if (visible)
        {
            chip.RemoveCallbacks(UpdatePauseChip);
            chip.PostDelayed(UpdatePauseChip, Math.Max(1, _chrome.HidesAtMs - now));
        }
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
        if (_session == null || _fps == null)
            return;
        // the guest process stopped by itself (exit, stack-protector failure, crash): say so instead of a frozen image
        if (_session.Ended && !_endedShown)
        {
            _endedShown = true;
            Log.Warn($"The process of {_session.Game.TitleId} ended");
            ShowFatal(Loc.Get("MGameEnded"));
            return;
        }
        long frames = _session.Frames;
        long fps = frames - _lastFrames;
        _lastFrames = frames;
        _fps.Text = _session.Paused ? Loc.Get("MPaused") : SettingsChoices.FpsText(fps, _session.FrameRateValue);
        bool rec = _session.Capture.IsRecording;
#if VITASHARP_DEVELOPER
        rec |= _session.CaptureDir != null;
#endif
        if (rec)
            _fps.Text = "\u25CF REC  " + _fps.Text;

        // every 30 s in the log (device runs are read from logcat): frame rate and what the CPU backends compiled
        if (++_fpsTicks % 30 == 0 && !_session.Paused)
        {
            Log.Info($"[perf] {(frames - _framesAtLog) / 30.0:F1} fps over 30 s, {frames} frames");
            if (Core.Cpu.Jit.Enabled)
                Log.Info($"[perf] {Core.Cpu.Jit.Describe()}");
            Log.Info($"[perf] audio: {_session.AudioSummary}");
            // the touch path (raw MotionEvents, the driver's samples and reads, a hash of the reports): counts only
            Log.Info($"[perf] {Core.Input.TouchDiagnostics.Describe()}");
            _framesAtLog = frames;
        }
        _fps.Visibility = _session.Settings.ShowFps || _session.Paused ? ViewStates.Visible : ViewStates.Invisible;
    }

    void ShowTrophy(TrophyUnlockedEvent e)
    {
        if (_toast == null)
            return;
        _toast.Text = Loc.Format("MTrophyUnlocked", e.Trophy.Name, TrophyList.GradeName(e.Trophy.Grade));
        _toast.Visibility = ViewStates.Visible;
        _toast.PostDelayed(() => _toast.Visibility = ViewStates.Gone, 5000);
        _session?.PlayTrophySound();
    }

    /// <summary>Leaves the game: the statistics are written first, since the screens re-read them as soon as this
    /// activity finishes (before its onDestroy).</summary>
    void Leave()
    {
        _session?.RecordStatistics();
        // a "Gravar tudo" recording running or being saved is completed first, with a notice on screen
        if (_session?.Capture is { } c && (c.IsRecording || !c.Idle.IsSet))
        {
            _menuOpen = true; // Back and the PS button do not open the menu over the notice
            var saving = SavingNotice(); // the pause menu's card (GameActivity.PauseMenu.cs)
            new System.Threading.Thread(() =>
            {
                while (c.IsRecording && !c.Stop("leaving the game"))
                    System.Threading.Thread.Sleep(250);
                c.Idle.Wait();
                RunOnUiThread(() => { _root?.RemoveView(saving); Finish(); });
            })
            {
                IsBackground = true,
                Name = "capture leave"
            }.Start();
            return;
        }
        Finish();
    }

    /// <summary>A reboot of the emulated system: the session ends and a new one boots the same title (no save state).</summary>
    void Reboot(GameEntry game)
    {
        _fpsTimer?.Dispose();
        _fpsTimer = null;
        _session?.Dispose();
        _session = null;
        _ = StartSessionAsync(game, 0);
    }

    void ShowFatal(string message)
    {
        _session?.Pause();
        // the file this session writes (a stamped one when last.log was locked); a reason that already names it
        // (TitleLaunchFailure) does not get it twice
        string log = VitaSharp.Core.Util.Log.FilePath ?? System.IO.Path.Combine(AppServices.Data.Logs, "last.log");
        string text = message.Contains(log, StringComparison.Ordinal) ? message : message + Loc.Get("MFullLogAt") + log;
        new AlertDialog.Builder(this)
            .SetTitle("VitaSharp")!
            .SetMessage(text)!
            .SetPositiveButton(Loc.Get("MQuit"), (_, _) => Leave())!
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
