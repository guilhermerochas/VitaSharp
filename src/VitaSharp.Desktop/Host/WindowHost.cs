using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using VitaSharp.Core;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Input;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop.Host;

/// <summary>Receives the keyboard while a text entry is open over the game (<see cref="WindowHost.TextCapture"/>).</summary>
public interface ITextCapture
{
    void OnKey(Key key);
    void OnChar(char c);
}

/// <summary>Desktop window: Vulkan surface, keyboard/gamepad/mouse input.</summary>
public sealed unsafe class WindowHost : IPresentationSurface, IInputBackend, IDisposable
{
    readonly IWindow _window;
    // windowed size/position to restore when F11 leaves fullscreen (no position when the window started fullscreen)
    Vector2D<int> _windowedSize;
    Vector2D<int>? _windowedPosition;
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
    public event Action? Resized;
    /// <summary>F10: "Capturar diagnóstico" (raised on the window thread; the handler must not block).</summary>
    public event Action? DiagnosticRequested;
    /// <summary>F9: turns debug mode on or off in game (raised on the window thread).</summary>
    public event Action? DebugModeToggled;
    /// <summary>F8: starts or stops a GPU dump recording (raised on the window thread; see docs/notes/gpu-dump.md).</summary>
    public event Action? GpuDumpToggled;
    /// <summary>The pause hotkey (<see cref="VitaControl.PauseEmulation"/>: a key of the keyboard profile, P by default,
    /// or a controller binding / button combination) was pressed: pause or resume the emulation (window thread).</summary>
    public event Action? PauseToggled;

    /// <summary>On-screen name of the keyboard's pause key ("P"), "" when it has none.</summary>
    public string PauseKeyLabel => _hostInput.KeyboardHotkey(VitaControl.PauseEmulation) is { } b
        ? Launcher.ControlNames.Describe(b, ControllerKind.Keyboard) : "";

    readonly HashSet<Key> _pauseKeysDown = new();
    bool _padPauseHeld;
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
        };
        _window = Window.Create(opts);
        _windowedSize = opts.Size;
        _window.Load += OnLoad;
        _window.Closing += () => Closing?.Invoke();
        _window.FramebufferResize += s => { _fbW = s.X; _fbH = s.Y; Resized?.Invoke(); };
        // GLFW gamepad fallback (only without SDL): sampled on the window thread, as GLFW requires
        _window.Update += _ =>
        {
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
    }

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

    public string Title { get => _window.Title; set => _window.Title = value; }

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
            // left button = front touch screen, right button = rear touch panel
            m.MouseDown += (mouse, b) => { if (b == MouseButton.Left) UpdateTouch(mouse.Position, true, false); else if (b == MouseButton.Right) UpdateTouch(mouse.Position, true, true); };
            m.MouseUp += (mouse, b) => { if (b == MouseButton.Left) UpdateTouch(mouse.Position, false, false); else if (b == MouseButton.Right) UpdateTouch(mouse.Position, false, true); };
            // middle button held = mouse look: moving the mouse rotates the Vita (virtual motion sensors)
            System.Numerics.Vector2? look = null;
            m.MouseDown += (mouse, b) => { if (b == MouseButton.Middle) look = mouse.Position; };
            m.MouseUp += (_, b) => { if (b == MouseButton.Middle) look = null; };
            m.MouseMove += (mouse, pos) =>
            {
                if (mouse.IsButtonPressed(MouseButton.Left)) UpdateTouch(pos, true, false);
                if (mouse.IsButtonPressed(MouseButton.Right)) UpdateTouch(pos, true, true);
                if (look is { } last && mouse.IsButtonPressed(MouseButton.Middle))
                {
                    _hostInput.MouseLook(pos.X - last.X, pos.Y - last.Y);
                    look = pos;
                }
            };
        }
        Loaded?.Invoke();
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

    public void Run() => _window.Run();
    public void Close() => _window.Close();

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

    void OnKey(Key key, bool down)
    {
        if (key == Key.F11 && down) { ToggleFullscreen(); return; }
        if (key == Key.F10) { if (down) DiagnosticRequested?.Invoke(); return; }
        if (key == Key.F9) { if (down) DebugModeToggled?.Invoke(); return; }
        if (key == Key.F8) { if (down) GpuDumpToggled?.Invoke(); return; }
        if (down && TextCapture is { } capture) { capture.OnKey(key); return; }
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

    void ToggleFullscreen()
    {
        if (_window.WindowState != WindowState.Fullscreen)
        {
            _windowedSize = _window.Size;
            _windowedPosition = _window.Position;
            _window.WindowState = WindowState.Fullscreen;
            return;
        }
        // leaving fullscreen keeps the monitor-sized window (GLFW reuses the current size), which still covers the
        // screen: put back the size and position the window had, or the startup size when it started fullscreen
        _window.WindowState = WindowState.Normal;
        _window.Size = _windowedSize;
        if (_windowedPosition is { } pos) _window.Position = pos;
        else
        {
            var area = _window.Monitor?.Bounds.Size ?? _windowedSize;
            _window.Position = new Vector2D<int>(Math.Max(0, (area.X - _windowedSize.X) / 2), Math.Max(0, (area.Y - _windowedSize.Y) / 2));
        }
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
                    ButtonName.A => PadButton.South, ButtonName.B => PadButton.East, ButtonName.X => PadButton.West, ButtonName.Y => PadButton.North,
                    ButtonName.LeftBumper => PadButton.LeftShoulder, ButtonName.RightBumper => PadButton.RightShoulder,
                    ButtonName.Start => PadButton.Start, ButtonName.Back => PadButton.Back, ButtonName.Home => PadButton.Guide,
                    ButtonName.DPadUp => PadButton.DpadUp, ButtonName.DPadDown => PadButton.DpadDown,
                    ButtonName.DPadLeft => PadButton.DpadLeft, ButtonName.DPadRight => PadButton.DpadRight,
                    ButtonName.LeftStick => PadButton.LeftStick, ButtonName.RightStick => PadButton.RightStick,
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
