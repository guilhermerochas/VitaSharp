using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using VitaSharp.Core;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop.Host;

/// <summary>Desktop window: Vulkan surface, keyboard/gamepad/mouse input.</summary>
public sealed unsafe class WindowHost : IPresentationSurface, IInputBackend, IDisposable
{
    readonly IWindow _window;
    IInputContext? _input;
    volatile int _fbW, _fbH;
    readonly object _inputLock = new();
    ControllerState _keys = ControllerState.Neutral;
    readonly List<TouchPoint> _touches = new();
    // rear touch panel: right mouse button (the Vita's back panel has no screen, so the window maps onto its full area)
    readonly List<TouchPoint> _backTouches = new();

    public event Action? Loaded;
    public event Action? Closing;
    public event Action? Resized;

    public WindowHost(string title, int scale = 1, bool fullscreen = false)
    {
        var opts = WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(960 * scale, 544 * scale),
            Title = title,
            VSync = false,
            WindowState = fullscreen ? WindowState.Fullscreen : WindowState.Normal,
        };
        _window = Window.Create(opts);
        _window.Load += OnLoad;
        _window.Closing += () => Closing?.Invoke();
        _window.FramebufferResize += s => { _fbW = s.X; _fbH = s.Y; Resized?.Invoke(); };
    }

    public string Title { get => _window.Title; set => _window.Title = value; }

    void OnLoad()
    {
        _fbW = _window.FramebufferSize.X;
        _fbH = _window.FramebufferSize.Y;
        _input = _window.CreateInput();
        foreach (var k in _input.Keyboards)
        {
            k.KeyDown += (_, key, _) => OnKey(key, true);
            k.KeyUp += (_, key, _) => OnKey(key, false);
        }
        foreach (var m in _input.Mice)
        {
            // left button = front touch screen, right button = rear touch panel
            m.MouseDown += (mouse, b) => { if (b == MouseButton.Left) UpdateTouch(mouse.Position, true, false); else if (b == MouseButton.Right) UpdateTouch(mouse.Position, true, true); };
            m.MouseUp += (mouse, b) => { if (b == MouseButton.Left) UpdateTouch(mouse.Position, false, false); else if (b == MouseButton.Right) UpdateTouch(mouse.Position, false, true); };
            m.MouseMove += (mouse, pos) =>
            {
                if (mouse.IsButtonPressed(MouseButton.Left)) UpdateTouch(pos, true, false);
                if (mouse.IsButtonPressed(MouseButton.Right)) UpdateTouch(pos, true, true);
            };
        }
        Loaded?.Invoke();
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

    static readonly Dictionary<Key, VitaButtons> KeyMap = new()
    {
        [Key.Up] = VitaButtons.Up, [Key.Down] = VitaButtons.Down, [Key.Left] = VitaButtons.Left, [Key.Right] = VitaButtons.Right,
        [Key.X] = VitaButtons.Cross, [Key.C] = VitaButtons.Circle, [Key.Z] = VitaButtons.Square, [Key.V] = VitaButtons.Triangle,
        [Key.Q] = VitaButtons.LTrigger, [Key.E] = VitaButtons.RTrigger, [Key.Enter] = VitaButtons.Start, [Key.Backspace] = VitaButtons.Select,
        [Key.ShiftRight] = VitaButtons.Select, [Key.Space] = VitaButtons.Cross,
    };

    void OnKey(Key key, bool down)
    {
        lock (_inputLock)
        {
            if (KeyMap.TryGetValue(key, out var b))
            {
                if (down) _keys.Buttons |= b; else _keys.Buttons &= ~b;
            }
            byte v = down ? (byte)0 : (byte)128, v2 = down ? (byte)255 : (byte)128;
            switch (key)
            {
                case Key.A: _keys.Lx = down ? (byte)0 : (byte)128; break;
                case Key.D: _keys.Lx = v2; break;
                case Key.W: _keys.Ly = v; break;
                case Key.S: _keys.Ly = v2; break;
                case Key.J: _keys.Rx = v; break;
                case Key.L: _keys.Rx = v2; break;
                case Key.I: _keys.Ry = v; break;
                case Key.K: _keys.Ry = v2; break;
                case Key.F11 when down: ToggleFullscreen(); break;
            }
        }
    }

    void ToggleFullscreen() => _window.WindowState = _window.WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;

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

    static byte Axis(float v) => (byte)Math.Clamp((int)((v + 1) * 127.5f), 0, 255);

    public ControllerState GetController(int port)
    {
        ControllerState s;
        lock (_inputLock) s = _keys;
        var pads = _input?.Gamepads;
        if (pads != null)
        {
            foreach (var g in pads)
            {
                if (!g.IsConnected) continue;
                foreach (var b in g.Buttons)
                {
                    if (!b.Pressed) continue;
                    s.Buttons |= b.Name switch
                    {
                        ButtonName.A => VitaButtons.Cross, ButtonName.B => VitaButtons.Circle, ButtonName.X => VitaButtons.Square, ButtonName.Y => VitaButtons.Triangle,
                        ButtonName.LeftBumper => VitaButtons.LTrigger, ButtonName.RightBumper => VitaButtons.RTrigger, ButtonName.Start => VitaButtons.Start, ButtonName.Back => VitaButtons.Select,
                        ButtonName.DPadUp => VitaButtons.Up, ButtonName.DPadDown => VitaButtons.Down, ButtonName.DPadLeft => VitaButtons.Left, ButtonName.DPadRight => VitaButtons.Right,
                        ButtonName.LeftStick => VitaButtons.L3, ButtonName.RightStick => VitaButtons.R3,
                        _ => VitaButtons.None,
                    };
                }
                if (g.Thumbsticks.Count > 0) { s.Lx = Axis(g.Thumbsticks[0].X); s.Ly = Axis(g.Thumbsticks[0].Y); }
                if (g.Thumbsticks.Count > 1) { s.Rx = Axis(g.Thumbsticks[1].X); s.Ry = Axis(g.Thumbsticks[1].Y); }
                if (g.Triggers.Count > 0 && g.Triggers[0].Position > 0.5f) s.Buttons |= VitaButtons.LTrigger; // the handheld has a single L/R pair (SCE_CTRL_LTRIGGER/RTRIGGER)
                if (g.Triggers.Count > 1 && g.Triggers[1].Position > 0.5f) s.Buttons |= VitaButtons.RTrigger;
                break;
            }
        }
        return s;
    }

    public IReadOnlyList<TouchPoint> GetTouches(bool back)
    {
        lock (_inputLock) return back ? _backTouches.ToArray() : _touches.ToArray();
    }

    public void Dispose()
    {
        _input?.Dispose();
        _window.Dispose();
    }
}
