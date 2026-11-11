using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using System.Runtime.InteropServices;
using VitaSharp.Core;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Input;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop.Host;

/// <summary>Receives the keyboard while a text entry is open over the game (<see cref="WindowHost.TextCapture"/>).</summary>
public interface ITextCapture
{
    void OnKey(Key key);
    void OnChar(char c);
    /// <summary>A left click at <paramref name="x"/>, <paramref name="y"/> in window image (framebuffer) pixels.</summary>
    void OnClick(int x, int y) { }
}

/// <summary>Desktop window: Vulkan surface, keyboard/gamepad/mouse input.</summary>
public sealed unsafe class WindowHost : IPresentationSurface, IInputBackend, IDisposable
{
    readonly IWindow _window;
    // windowed size/position to restore when F11 leaves fullscreen (no position when the window started fullscreen)
    Vector2D<int> _windowedSize;
    Vector2D<int>? _windowedPosition;
    // our own record of the mode: F11 decides from it, not from Silk.NET's WindowState (see ToggleFullscreen)
    bool _fullscreen, _f11Down;
    IInputContext? _input;
    volatile int _fbW, _fbH;
    readonly object _inputLock = new();
    // keyboard + controllers through the input.json profiles (SDL controllers, defaults when no profile exists)
    readonly HostInput _hostInput = new(Program.DataRoot);
    readonly PadSnapshot _fallbackPad = new();
    long _lastFallbackPoll;
    readonly List<TouchPoint> _touches = new();
    // rear touch panel: right mouse button (the Vita's back panel has no screen, so the window maps onto its full area)
    readonly List<TouchPoint> _backTouches = new();

    public event Action? Loaded;
    public event Action? Closing;
    /// <summary>Asked on the window thread when the user closes the window (X, Alt+F4, WM_CLOSE from the launcher): true
    /// keeps it open (a "Gravar tudo" recording is still being saved); the owner calls <see cref="Close"/> later.</summary>
    public Func<bool>? CloseVeto;
    public event Action? Resized;
    /// <summary>F10: "Capturar diagnostico" (raised on the window thread; the handler must not block).</summary>
    public event Action? DiagnosticRequested;
    /// <summary>F9: turns debug mode on or off in game (raised on the window thread).</summary>
    public event Action? DebugModeToggled;
    /// <summary>Ctrl+F8: starts or stops a GPU dump recording (raised on the window thread; see docs/notes/gpu-dump.md).</summary>
    public event Action? GpuDumpToggled;
    /// <summary>F8: starts or stops "Gravar tudo", the session capture (docs/notes/session-capture.md).</summary>
    public event Action? SessionCaptureToggled;
    bool _f8Down;
    /// <summary>Save states (window thread): F5 quick save, Shift+F5 save to the selected slot, F7 quick load, Shift+F7
    /// load the selected slot, F6 next slot. The argument is the slot (0 = quick), or -1 for F6.</summary>
    public event Action<int>? SaveStateKey, LoadStateKey;
    /// <summary>The pause hotkey (<see cref="VitaControl.PauseEmulation"/>: a key of the keyboard profile, P by default,
    /// or a controller binding / button combination) was pressed: pause or resume the emulation (window thread).</summary>
    public event Action? PauseToggled;
    /// <summary>F1 / F2 / F3 (Shift = the previous value): internal resolution / frame rate mode / MSAA mode while the game runs
    /// (<see cref="GraphicsHotkeys"/>; window thread, the handler must not block). The argument is the key name
    /// ("F1", "Shift+F1", "F2", "Shift+F2").</summary>
    public event Action<string>? GraphicsKey;
    readonly HashSet<Key> _graphicsKeysDown = new();

    /// <summary>On-screen name of the keyboard's pause key ("P"), "" when it has none.</summary>
    public string PauseKeyLabel => _hostInput.KeyboardHotkey(VitaControl.PauseEmulation) is { } b
        ? Launcher.ControlNames.Describe(b, ControllerKind.Keyboard) : "";

    /// <summary>The mouse mode hotkey (<see cref="MouseSettings.ToggleKey"/>, F4) changed the mode: the on-screen notice
    /// text (window thread).</summary>
    public event Action<string>? MouseModeChanged;

    /// <summary>The game whose saved mouse mode applies (window thread, once the title is loaded): a stick mode
    /// captures the cursor and shows the mode notice.</summary>
    public string? TitleId
    {
        get => _hostInput.TitleId;
        set
        {
            _hostInput.TitleId = value;
            CaptureMouse(_hostInput.MouseIsStick);
            if (_hostInput.MouseIsStick) MouseModeChanged?.Invoke(MouseModeNotice());
        }
    }

    // the mouse as a stick: the cursor is captured (hidden, unbounded raw motion) while the window has it
    bool _mouseCaptured;
    System.Numerics.Vector2? _mouseLast;
    IMouse? _mouseDevice;

    readonly HashSet<Key> _pauseKeysDown = new();
    bool _padPauseHeld, _mouseToggleDown;
    long _lastHotkeyPoll;

    /// <summary>
    /// Text entry (the IME overlay): while set, key presses and typed characters go to it instead of the Vita button
    /// mapping (key releases still reach the mapping, so nothing stays held) and the game sees a neutral controller.
    /// Called on the window thread; F8-F11 keep their meaning.
    /// </summary>
    public ITextCapture? TextCapture { get; set; }

    /// <summary>Raised on the window thread at the window's update rate (the text entry polls the gamepad here).</summary>
    public event Action? Tick;

    /// <summary>Clipboard text (Ctrl+V in the text entry); "" when unavailable. Window thread only.</summary>
    public string ClipboardText
    {
        get
        {
            try { return _input?.Keyboards.FirstOrDefault()?.ClipboardText ?? ""; }
            catch (Exception) { return ""; }
        }
    }

    /// <summary>The mapped controller state, also while <see cref="TextCapture"/> hides it from the game.</summary>
    public ControllerState RawController() => _hostInput.Poll().State;

    /// <summary>Whether Shift / Control is held (window thread).</summary>
    public bool IsShiftDown => _input?.Keyboards.Any(k => k.IsKeyPressed(Key.ShiftLeft) || k.IsKeyPressed(Key.ShiftRight)) == true;
    public bool IsControlDown => _input?.Keyboards.Any(k => k.IsKeyPressed(Key.ControlLeft) || k.IsKeyPressed(Key.ControlRight)) == true;

    /// <summary>
    /// Registers the GLFW window/input platforms explicitly: Silk.NET otherwise finds them by loading assemblies by
    /// name through reflection, which trimming / Native AOT removes.
    /// </summary>
    static WindowHost()
    {
        Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
        Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();
    }

    public WindowHost(string title, int scale = 1, bool fullscreen = false)
    {
        SelectGlfwPlatform();
        var opts = WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(960 * scale, 544 * scale),
            Title = title,
            VSync = false,
            WindowState = fullscreen ? WindowState.Fullscreen : WindowState.Normal,
            IsVisible = Environment.GetEnvironmentVariable(VitaSharp.Core.Util.EnvVars.HiddenWindow) != "1",
        };
        _window = Window.Create(opts);
        _title = title;
        _windowedSize = opts.Size;
        _fullscreen = fullscreen;
        _window.Load += OnLoad;
        _window.Closing += () =>
        {
            if (CloseVeto?.Invoke() == true) { _window.IsClosing = false; return; }
            Closing?.Invoke();
        };
        _window.FramebufferResize += s => { _fbW = s.X; _fbH = s.Y; Resized?.Invoke(); };
        // keys held when the window loses the focus never get their release: forget them, or the next press of F8
        // (one toggle per press) would be taken for key repeat and lost
        _window.FocusChanged += focused =>
        {
            if (focused) return;
            if (_f8Down || _f11Down || _graphicsKeysDown.Count > 0) Log.Info("Window: focus lost with F8/F11/F1-F3 held: their release is assumed");
            _f8Down = _f11Down = false;
            _graphicsKeysDown.Clear();
        };
        // GLFW gamepad fallback (only without SDL): sampled on the window thread, as GLFW requires
        Action update = () =>
        {
            // the window thread must never stop for long (a frozen window): a gap is logged with its length
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastUpdate != 0)
            {
                double gapMs = (t - _lastUpdate) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                var gc = GC.GetTotalPauseDuration();
                if (gapMs >= 1000)
                    Log.Warn($"Window: the window thread did not run for {gapMs:F0} ms; garbage collection paused every thread for {(gc - _gcPauseAtUpdate).TotalMilliseconds:F0} ms of it (moving or resizing the window also holds it)");
                _gcPauseAtUpdate = gc;
                MaxUpdateGapMs = Math.Max(MaxUpdateGapMs, gapMs);
            }
            else _gcPauseAtUpdate = GC.GetTotalPauseDuration();
            _lastUpdate = t;
            DrainWindowWork();
            ApplyPendingTitle();
            Tick?.Invoke();
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _lastHotkeyPoll >= System.Diagnostics.Stopwatch.Frequency / 100)
            {
                _lastHotkeyPoll = now;
                PollPadHotkeys();
            }
            if (now - _lastFallbackPoll < System.Diagnostics.Stopwatch.Frequency / 250) return;
            _lastFallbackPoll = now;
            PollFallbackGamepad();
        };
        // Silk.NET's loop never sleeps (no render, no vsync here): without a wait the window thread spun a whole core
        // (53 s of CPU in a 62 s Spider-Man run, 85% of a core) that the game threads and the GPU worker needed. A 2 ms
        // wait (1 ms system timer: Runner's timeBeginPeriod) keeps ~400 updates/s for the 100-250 Hz input polls; work
        // for the window thread (RunOnWindowThread) wakes it at once.
        _window.Update += _ =>
        {
            update();
            _windowWake.WaitOne(WindowIdleMs);
        };
    }

    /// <summary>Longest wait of the window loop between two updates when nothing wakes it (ms).</summary>
    public const int WindowIdleMs = 2;
    readonly AutoResetEvent _windowWake = new(false);

    /// <summary>
    /// Linux: GLFW 3.4 prefers Wayland whenever WAYLAND_DISPLAY is set and fails outright (no X11 fallback) when the
    /// Wayland client libraries are missing, which is common on minimal installs and WSLg. Then X11 (XWayland) is
    /// selected up front. VITASHARP_GLFW_PLATFORM=x11|wayland forces a backend. Init hints are global state of the
    /// GLFW library, so setting one here applies to Silk.NET's own glfwInit that follows.
    /// </summary>
    static void SelectGlfwPlatform()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD()) return;
        var want = Environment.GetEnvironmentVariable(EnvVars.GlfwPlatform)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(want))
        {
            bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            bool x11 = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
            if (!wayland || !x11) return;
            var missing = new[] { "libwayland-client.so.0", "libwayland-cursor.so.0", "libxkbcommon.so.0" }.Where(l => !CanLoad(l)).ToList();
            if (missing.Count == 0) return;
            Log.Info($"Window: Wayland libraries missing ({string.Join(", ", missing)}), using X11");
            want = "x11";
        }
        const int GLFW_PLATFORM = 0x00050003;
        int platform = want switch { "x11" => 0x00060004, "wayland" => 0x00060003, _ => 0x00060000 /* any */ };
        try { Silk.NET.GLFW.Glfw.GetApi().InitHint((Silk.NET.GLFW.InitHint)GLFW_PLATFORM, platform); }
        catch (Exception e) { Log.Warn($"Window: could not select the GLFW platform '{want}': {e.Message}"); }
    }

    static bool CanLoad(string lib)
    {
        if (!NativeLibrary.TryLoad(lib, out var h)) return false;
        NativeLibrary.Free(h);
        return true;
    }

    /// <summary>
    /// The title bar text. Any thread may set it; only the window thread hands it to GLFW (at once when set there, else
    /// on the loop's next update, the latest text wins). glfwSetWindowTitle is main-thread only: GLFW 3.4 frees and
    /// re-duplicates its own copy of the title on every call, so the 1 s FPS timer and a hotkey's status line setting
    /// it at the same time from two pool threads freed one copy twice (native heap corruption, c0000374 in free_base
    /// under glfwSetWindowTitle, Dead Space after quick F1 resolution switches).
    /// </summary>
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            if (_windowThread == Environment.CurrentManagedThreadId) { Volatile.Write(ref _pendingTitle, null); _window.Title = value; }
            else { Volatile.Write(ref _pendingTitle, value); _windowWake.Set(); }
        }
    }
    string _title = "";
    string? _pendingTitle;

    void ApplyPendingTitle()
    {
        if (Interlocked.Exchange(ref _pendingTitle, null) is { } t) _window.Title = t;
    }

    void OnLoad()
    {
        _fbW = _window.FramebufferSize.X;
        _fbH = _window.FramebufferSize.Y;
        SetIcon();
        _input = _window.CreateInput();
        foreach (var k in _input.Keyboards)
        {
            k.KeyDown += (_, key, _) => OnKey(key, true);
            k.KeyUp += (_, key, _) => OnKey(key, false);
            k.KeyChar += (_, c) => TextCapture?.OnChar(c);
        }
        foreach (var m in _input.Mice)
        {
            _mouseDevice ??= m;
            // left button = front touch screen, right button = rear touch panel, middle = mouse look, unless the
            // keyboard profile binds the button to a Vita control ("key:MouseLeft"...: then it presses that control, also
            // with the cursor captured); with the cursor captured (mouse as a stick) the pointer has no position on the
            // screen, so the buttons do not touch, and a left click with the cursor released captures it again
            m.MouseDown += (mouse, b) =>
            {
                // a dialog over the game takes the left clicks (its buttons) and nothing touches the game; a mouse button
                // bound to a Vita button still presses it (the dialog reads the mapped controller)
                if (TextCapture is { } capture && !_mouseCaptured)
                {
                    if (b == MouseButton.Left) { var (fx, fy) = ToFramebuffer(mouse.Position); capture.OnClick(fx, fy); }
                    if (b == MouseButton.Left || MouseKey(b) is not { } bound || !_hostInput.IsKeyBound(bound)) return;
                }
                if (!_mouseCaptured && _hostInput.MouseIsStick && b == MouseButton.Left) { CaptureMouse(true); return; }
                if (MouseKey(b) is { } key && _hostInput.IsKeyBound(key))
                {
                    _mouseKeysDown.Add(b);
                    if (_hostInput.IsHotkeyKey(VitaControl.PauseEmulation, key)) PauseToggled?.Invoke();
                    else _hostInput.KeyChanged(key, true);
                    return;
                }
                if (_mouseCaptured) return;
                if (b == MouseButton.Left) UpdateTouch(mouse.Position, true, false);
                else if (b == MouseButton.Right) UpdateTouch(mouse.Position, true, true);
                else if (b == MouseButton.Middle) _look = mouse.Position; // middle button held = mouse look (virtual motion)
            };
            m.MouseUp += (mouse, b) =>
            {
                if (_mouseKeysDown.Remove(b)) { if (MouseKey(b) is { } key) _hostInput.KeyChanged(key, false); return; }
                if (b == MouseButton.Left) UpdateTouch(mouse.Position, false, false);
                else if (b == MouseButton.Right) UpdateTouch(mouse.Position, false, true);
                else if (b == MouseButton.Middle) _look = null;
            };
            m.MouseMove += (mouse, pos) =>
            {
                if (_mouseCaptured)
                {
                    if (_mouseLast is { } prev) _hostInput.MouseMoved(pos.X - prev.X, pos.Y - prev.Y);
                    _mouseLast = pos;
                    return;
                }
                if (mouse.IsButtonPressed(MouseButton.Left) && !_mouseKeysDown.Contains(MouseButton.Left)) UpdateTouch(pos, true, false);
                if (mouse.IsButtonPressed(MouseButton.Right) && !_mouseKeysDown.Contains(MouseButton.Right)) UpdateTouch(pos, true, true);
                if (_look is { } last && mouse.IsButtonPressed(MouseButton.Middle))
                {
                    _hostInput.MouseLook(pos.X - last.X, pos.Y - last.Y);
                    _look = pos;
                }
            };
        }
        Loaded?.Invoke();
    }

    // mouse buttons bound as keys that are held (their release goes to the mapping, not the touch panel); mouse look
    readonly HashSet<MouseButton> _mouseKeysDown = new();
    System.Numerics.Vector2? _look;

    /// <summary>The key name of a mouse button in the keyboard profile ("key:MouseRight"), null for the others.</summary>
    public static string? MouseKey(MouseButton b) => b switch
    {
        MouseButton.Left => "MouseLeft",
        MouseButton.Right => "MouseRight",
        MouseButton.Middle => "MouseMiddle",
        MouseButton.Button4 => "Mouse4",
        MouseButton.Button5 => "Mouse5",
        _ => null,
    };

    /// <summary>Captures (hidden, raw unbounded motion) or releases the cursor. Window thread.</summary>
    void CaptureMouse(bool capture)
    {
        if (_mouseDevice is not { } m || capture == _mouseCaptured) return;
        try
        {
            m.Cursor.CursorMode = capture ? (m.Cursor.IsSupported(CursorMode.Raw) ? CursorMode.Raw : CursorMode.Disabled) : CursorMode.Normal;
        }
        catch (Exception e) { Log.Warn($"Window: could not {(capture ? "capture" : "release")} the mouse cursor: {e.Message}"); return; }
        _mouseCaptured = capture;
        _mouseLast = null;
        lock (_inputLock) { _touches.Clear(); _backTouches.Clear(); }
        if (!capture) _hostInput.MouseReleased();
        Log.Info(capture ? $"Window: mouse captured ({MouseSettings.Label(_hostInput.MouseMode)}; {_hostInput.MouseSettings.ReleaseKey} releases it, a click captures it again)" : "Window: mouse released");
    }

    /// <summary>The on-screen hint of the mouse mode: "Mouse: analogico direito (Esc solta, F4 muda)".</summary>
    public string MouseModeNotice()
    {
        var s = _hostInput.MouseSettings;
        string keys = _hostInput.MouseIsStick ? Loc.Format("MouseKeysStick", s.ReleaseKey, s.ToggleKey) : Loc.Format("MouseKeys", s.ToggleKey);
        return $"Mouse: {MouseSettings.Label(_hostInput.MouseMode)} ({keys})";
    }

    /// <summary>Title bar / taskbar icon. macOS (bundle icon) and Wayland (desktop file) have no per-window icon.</summary>
    void SetIcon()
    {
        if (OperatingSystem.IsMacOS()) return;
        try
        {
            var icons = AppIcon.ForGlfw();
            if (icons.Length > 0) _window.SetWindowIcon(icons);
        }
        catch (Exception e) { Log.Warn($"Window: could not set the window icon: {e.Message}"); }
    }

    public void Run()
    {
        _windowThread = Environment.CurrentManagedThreadId;
        VitaSharp.Core.Util.NativeThread.Register("Window");
        try { _window.Run(); }
        finally { _windowThread = -1; DrainWindowWork(); }
    }

    public void Close() => _window.Close();

    /// <summary>Closes the window as its X button does: <see cref="CloseVeto"/> may keep it open (a recording being
    /// saved), which <see cref="Close"/> skips. Window thread (scripted "Close" key).</summary>
    public void RequestClose()
    {
        if (CloseVeto?.Invoke() == true) return;
        _window.Close();
    }

    int _windowThread = -1;
    readonly System.Collections.Concurrent.ConcurrentQueue<(Action Work, ManualResetEventSlim Done)> _windowWork = new();

    /// <summary>
    /// Runs <paramref name="work"/> on the window thread and waits for it (exceptions are rethrown here); at once when
    /// called there or once the window loop has ended. The GPU backend is replaced through this: a Vulkan instance,
    /// device and swapchain for this window made and destroyed on another thread corrupted the native heap on the
    /// NVIDIA driver (state loads: c0000374, then a TDR as the process died).
    /// </summary>
    public void RunOnWindowThread(Action work)
    {
        if (_windowThread == -1 || _windowThread == Environment.CurrentManagedThreadId) { work(); return; }
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        _windowWork.Enqueue((() => { try { work(); } catch (Exception e) { error = e; } }, done));
        _windowWake.Set();
        while (!done.Wait(50))
            if (_windowThread == -1) DrainWindowWork(); // the loop ended meanwhile
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
    }

    void DrainWindowWork()
    {
        while (_windowWork.TryDequeue(out var w))
        {
            w.Work();
            w.Done.Set();
        }
    }

    // ---------------------------------------------------------------- IPresentationSurface

    public string[] RequiredInstanceExtensions
    {
        get
        {
            var names = _window.VkSurface!.GetRequiredExtensions(out uint count);
            var list = new string[count];
            for (int i = 0; i < count; i++) list[i] = Marshal.PtrToStringAnsi((nint)names[i])!;
            return list;
        }
    }

    public SurfaceKHR CreateSurface(Instance instance)
        => _window.VkSurface!.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();

    public (int Width, int Height) FramebufferSize => (_fbW, _fbH);

    // ---------------------------------------------------------------- input

    /// <summary>One F8 press: "Gravar tudo", or with Ctrl the GPU dump (the key handler and --input steps).</summary>
    public void PressF8(bool ctrl)
    {
        Log.Info($"Window: {(ctrl ? "Ctrl+F8 (GPU dump)" : "F8 (Gravar tudo)")} pressed");
        (ctrl ? GpuDumpToggled : SessionCaptureToggled)?.Invoke();
    }

    long _lastUpdate;
    TimeSpan _gcPauseAtUpdate;
    /// <summary>Longest time the window thread went without an update (ms): tests and the capture check.</summary>
    public double MaxUpdateGapMs { get; private set; }

    /// <summary>A save state key from an --input script ("F5", "F6", "F7", "Shift+F5", "Shift+F7"), as the keyboard
    /// sends it: to the open picker / dialog, else the save state events. Window thread.</summary>
    public void PressStateKey(string name)
    {
        bool shift = name.StartsWith("Shift+", StringComparison.Ordinal);
        var key = Enum.Parse<Key>(shift ? name[6..] : name);
        if (TextCapture is { } capture) { capture.OnKey(key); return; }
        (key == Key.F7 ? LoadStateKey : SaveStateKey)?.Invoke(key == Key.F6 ? -1 : shift ? 1 : 0);
    }

    void OnKey(Key key, bool down)
    {
        if (key == Key.F11)
        {
            if (down && !_f11Down) ToggleFullscreen(); // one toggle per press, whatever key repeat sends
            _f11Down = down;
            return;
        }
        if (key == Key.F10) { if (down) DiagnosticRequested?.Invoke(); return; }
        if (key == Key.F9) { if (down) DebugModeToggled?.Invoke(); return; }
        if (key == Key.F8)
        {
            // F8 alone: "Gravar tudo"; Ctrl+F8: the GPU dump (one toggle per press, whatever key repeat sends)
            if (down && !_f8Down) PressF8(IsControlDown);
            _f8Down = down;
            return;
        }
        if (key is Key.F1 or Key.F2 or Key.F3 && TextCapture == null)
        {
            // one change per press, whatever key repeat sends
            if (!down) _graphicsKeysDown.Remove(key);
            else if (_graphicsKeysDown.Add(key)) GraphicsKey?.Invoke((IsShiftDown ? "Shift+" : "") + key);
            return;
        }
        if (key is Key.F5 or Key.F6 or Key.F7 && TextCapture == null)
        {
            if (down) (key == Key.F7 ? LoadStateKey : SaveStateKey)?.Invoke(key == Key.F6 ? -1 : IsShiftDown ? 1 : 0);
            return;
        }
        if (down && TextCapture is { } capture) { capture.OnKey(key); return; }
        // the mouse keys never reach the game: release the captured cursor, change the mouse mode
        var mouse = _hostInput.MouseSettings;
        if (key.ToString() == mouse.ReleaseKey && _mouseCaptured)
        {
            if (down) CaptureMouse(false);
            return;
        }
        if (key.ToString() == mouse.ToggleKey)
        {
            if (down && !_mouseToggleDown)
            {
                _hostInput.CycleMouseMode();
                CaptureMouse(_hostInput.MouseIsStick);
                MouseModeChanged?.Invoke(MouseModeNotice());
            }
            _mouseToggleDown = down;
            return;
        }
        // the pause hotkey never reaches the game; a held key toggles once (key repeat is ignored)
        if (_hostInput.IsHotkeyKey(VitaControl.PauseEmulation, key.ToString()))
        {
            if (!down) _pauseKeysDown.Remove(key);
            else if (_pauseKeysDown.Add(key)) PauseToggled?.Invoke();
            return;
        }
        // the mapping (input.json keyboard profile, else the historic default) uses Silk.NET key names
        _hostInput.KeyChanged(key.ToString(), down);
    }

    /// <summary>A controller's pause binding or combination: toggles when it becomes held.</summary>
    void PollPadHotkeys()
    {
        bool held = _hostInput.PadHotkeyHeld(VitaControl.PauseEmulation);
        if (held && !_padPauseHeld) PauseToggled?.Invoke();
        _padPauseHeld = held;
    }

    /// <summary>
    /// F11. Leaving fullscreen goes through GLFW directly (<c>glfwSetWindowMonitor(window, NULL, x, y, w, h)</c>):
    /// Silk.NET 2.23's WindowState = Normal left the window attached to the monitor (still fullscreen), and its
    /// WindowState then no longer said whether the window was fullscreen, so the mode is tracked here. The windowed
    /// size and position come back (the startup size, centred, when the window started fullscreen).
    /// </summary>
    public void ToggleFullscreen()
    {
        var glfw = Silk.NET.GLFW.GlfwProvider.GLFW.Value;
        var handle = (Silk.NET.GLFW.WindowHandle*)(_window.Native?.Glfw ?? 0);
        if (!_fullscreen)
        {
            _windowedSize = _window.Size;
            _windowedPosition = _window.Position;
            _fullscreen = true;
            _window.WindowState = WindowState.Fullscreen;
            if (handle != null && glfw.GetWindowMonitor(handle) == null) // Silk.NET did not attach it
            {
                var monitor = glfw.GetPrimaryMonitor();
                var mode = glfw.GetVideoMode(monitor);
                if (mode != null) glfw.SetWindowMonitor(handle, monitor, 0, 0, mode->Width, mode->Height, mode->RefreshRate);
            }
            Log.Info($"Window: fullscreen ({_window.Size.X}x{_window.Size.Y})");
            return;
        }
        _fullscreen = false;
        var size = _windowedSize;
        Vector2D<int> pos;
        if (_windowedPosition is { } p) pos = p;
        else
        {
            var area = _window.Monitor?.Bounds.Size ?? size;
            pos = new Vector2D<int>(Math.Max(0, (area.X - size.X) / 2), Math.Max(0, (area.Y - size.Y) / 2));
        }
        _window.WindowState = WindowState.Normal;
        if (handle != null) glfw.SetWindowMonitor(handle, null, pos.X, pos.Y, size.X, size.Y, Silk.NET.GLFW.Glfw.DontCare);
        else
        {
            _window.Size = size;
            _window.Position = pos;
        }
        Log.Info($"Window: windowed {size.X}x{size.Y} at {pos.X},{pos.Y} (monitor attached: {handle != null && glfw.GetWindowMonitor(handle) != null})");
    }

    /// <summary>Window (cursor) coordinates to framebuffer pixels (they differ on high-DPI displays).</summary>
    (int X, int Y) ToFramebuffer(System.Numerics.Vector2 pos)
    {
        var size = _window.Size;
        float sx = size.X > 0 ? _fbW / (float)size.X : 1, sy = size.Y > 0 ? _fbH / (float)size.Y : 1;
        return ((int)(pos.X * sx), (int)(pos.Y * sy));
    }

    void UpdateTouch(System.Numerics.Vector2 pos, bool down, bool back)
    {
        // map window coordinates into the letterboxed 960x544 image
        var size = _window.Size;
        float scale = Math.Min(size.X / 960f, size.Y / 544f);
        float ox = (size.X - 960 * scale) / 2, oy = (size.Y - 544 * scale) / 2;
        float x = (pos.X - ox) / (960 * scale), y = (pos.Y - oy) / (544 * scale);
        lock (_inputLock)
        {
            var list = back ? _backTouches : _touches;
            list.Clear();
            if (down && x is >= 0 and <= 1 && y is >= 0 and <= 1) list.Add(new TouchPoint(back ? 2 : 1, x, y));
        }
    }

    /// <summary>
    /// GLFW gamepads, used only when SDL (the normal controller backend) could not be loaded: the first connected
    /// gamepad is handed to the mapper in the standard layout.
    /// </summary>
    void PollFallbackGamepad()
    {
        if (_hostInput.ControllersAvailable || _input == null) return;
        foreach (var g in _input.Gamepads)
        {
            if (!g.IsConnected) continue;
            _fallbackPad.Clear();
            _fallbackPad.IsGamepad = true;
            foreach (var b in g.Buttons)
            {
                PadButton? pb = b.Name switch
                {
                    ButtonName.A => PadButton.South,
                    ButtonName.B => PadButton.East,
                    ButtonName.X => PadButton.West,
                    ButtonName.Y => PadButton.North,
                    ButtonName.LeftBumper => PadButton.LeftShoulder,
                    ButtonName.RightBumper => PadButton.RightShoulder,
                    ButtonName.Start => PadButton.Start,
                    ButtonName.Back => PadButton.Back,
                    ButtonName.Home => PadButton.Guide,
                    ButtonName.DPadUp => PadButton.DpadUp,
                    ButtonName.DPadDown => PadButton.DpadDown,
                    ButtonName.DPadLeft => PadButton.DpadLeft,
                    ButtonName.DPadRight => PadButton.DpadRight,
                    ButtonName.LeftStick => PadButton.LeftStick,
                    ButtonName.RightStick => PadButton.RightStick,
                    _ => null,
                };
                if (pb is { } p) _fallbackPad.Buttons[(int)p] = b.Pressed;
            }
            if (g.Thumbsticks.Count > 0) { _fallbackPad.Axes[0] = g.Thumbsticks[0].X; _fallbackPad.Axes[1] = g.Thumbsticks[0].Y; }
            if (g.Thumbsticks.Count > 1) { _fallbackPad.Axes[2] = g.Thumbsticks[1].X; _fallbackPad.Axes[3] = g.Thumbsticks[1].Y; }
            // GLFW triggers rest at -1
            if (g.Triggers.Count > 0) _fallbackPad.Axes[4] = Math.Clamp((g.Triggers[0].Position + 1) / 2, 0, 1);
            if (g.Triggers.Count > 1) _fallbackPad.Axes[5] = Math.Clamp((g.Triggers[1].Position + 1) / 2, 0, 1);
            _hostInput.SetFallbackPad(_fallbackPad, g.Name, g.Name);
            return;
        }
        _hostInput.SetFallbackPad(null, "", "");
    }

    public ControllerState GetController(int port) => TextCapture != null ? ControllerState.Neutral : _hostInput.Poll().State;

    /// <summary>Controller gyroscope/accelerometer for SceMotion.</summary>
    public IMotionSource Motion => _hostInput;

    public IReadOnlyList<TouchPoint> GetTouches(bool back)
    {
        Span<TouchPoint> buf = stackalloc TouchPoint[16];
        int n = GetTouches(back, buf);
        return buf[..n].ToArray();
    }

    public int GetTouches(bool back, Span<TouchPoint> dest)
    {
        int n;
        lock (_inputLock)
        {
            var list = back ? _backTouches : _touches;
            n = Math.Min(list.Count, dest.Length);
            for (int i = 0; i < n; i++) dest[i] = list[i];
        }
        // touch pad fingers and buttons mapped to a touch point
        var m = _hostInput.Poll();
        return n + m.CopyTouches(back, dest[n..]);
    }

    public void Dispose()
    {
        _hostInput.Dispose();
        _input?.Dispose();
        _window.Dispose();
    }
}
