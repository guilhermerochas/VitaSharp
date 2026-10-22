using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Silk.NET.SDL;
using Thread = System.Threading.Thread;
using VitaSharp.Core.Input;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Input;

/// <summary>A connected controller as seen by <see cref="SdlGamepads"/>.</summary>
public sealed record PadInfo(int InstanceId, string Guid, string Name, ControllerKind Kind, bool IsGamepad, bool HasTouchpad, string Model, bool IsVirtual)
{
    public string Label => $"{Name} ({ControllerClassifier.KindLabel(Kind)})";
}

/// <summary>
/// Game controllers through SDL2 (joystick + game controller subsystems only, no SDL video): SDL's GameControllerDB
/// gives every known controller the standard positional layout, hot-plug is detected as devices come and go, and the
/// touch pads of DualShock 4 / DualSense (and other pads that have one) are read. SDL runs on its own thread, so it
/// works the same next to a GLFW game window or the Avalonia launcher on every desktop platform; callers read the
/// latest sampled state with <see cref="TryGetState"/>.
/// <para>Extra mappings: <c>&lt;data&gt;/gamecontrollerdb.txt</c> (the SDL_GameControllerDB file format) and the
/// SDL_GAMECONTROLLERCONFIG environment variable are loaded on top of SDL's built-in database.</para>
/// </summary>
public sealed unsafe class SdlGamepads : IDisposable
{
    const uint InitJoystick = 0x200, InitGameController = 0x2000;

    sealed class Device
    {
        public required PadInfo Info;
        public GameController* Controller;
        public Joystick* Joystick;
        public readonly PadSnapshot State = new();
    }

    readonly Thread _thread;
    readonly object _lock = new();
    readonly Dictionary<int, Device> _devices = new(); // owned by the SDL thread
    readonly Dictionary<int, PadSnapshot> _states = new(); // published copies, under _lock
    PadInfo[] _infos = [];
    readonly ConcurrentQueue<Action> _commands = new();
    readonly ManualResetEventSlim _started = new();
    volatile bool _stop;
    Sdl? _sdl;
    readonly string? _mappingFile;

    /// <summary>Raised (on the SDL thread) when a controller is connected or disconnected.</summary>
    public event Action? DevicesChanged;

    /// <summary>False when SDL could not be loaded or initialised (then no controllers are reported).</summary>
    public bool Available { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<PadInfo> Devices { get { lock (_lock) return _infos; } }

    public const string MappingFileName = "gamecontrollerdb.txt";

    /// <param name="dataRoot">Data folder searched for gamecontrollerdb.txt (optional).</param>
    public SdlGamepads(string? dataRoot)
    {
        if (dataRoot != null)
        {
            var f = Path.Combine(dataRoot, MappingFileName);
            if (File.Exists(f)) _mappingFile = f;
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "SDL input" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    void Run()
    {
        try
        {
            _sdl = Sdl.GetApi();
            // input keeps working while another window (the game window, the launcher) has focus
            _sdl.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            // full DualShock 4 / DualSense reports over Bluetooth: the touch pad is only sent in that mode
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_PS4_RUMBLE", "1");
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_PS5_RUMBLE", "1");
            // no SDL window exists: the player LEDs / home LED stay as the controller sets them
            _sdl.SetHint("SDL_JOYSTICK_HIDAPI_PS5_PLAYER_LED", "0");
            if (_mappingFile != null) _sdl.SetHint("SDL_GAMECONTROLLERCONFIG_FILE", _mappingFile);
            if (_sdl.Init(InitJoystick | InitGameController) != 0)
                throw new InvalidOperationException(_sdl.GetErrorS());
            // states are polled; SDL's event queue is not used (nothing would drain it)
            _sdl.JoystickEventState(0);
            _sdl.GameControllerEventState(0);
            Available = true;
            if (_mappingFile != null) Log.Info($"Input: extra controller mappings from {_mappingFile}");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or FileNotFoundException or BadImageFormatException)
        {
            Error = e.Message;
            Log.Warn($"Input: SDL game controller support unavailable ({e.Message})");
            _started.Set();
            return;
        }
        _started.Set();

        while (!_stop)
        {
            while (_commands.TryDequeue(out var cmd))
            {
                try { cmd(); } catch (Exception e) { Log.Warn($"Input: {e.Message}"); }
            }
            _sdl.JoystickUpdate();
            bool changed = SyncDevices();
            lock (_lock)
            {
                foreach (var d in _devices.Values)
                {
                    Sample(d);
                    if (!_states.TryGetValue(d.Info.InstanceId, out var pub)) _states[d.Info.InstanceId] = pub = new PadSnapshot();
                    d.State.CopyTo(pub);
                }
            }
            if (changed) DevicesChanged?.Invoke();
            PreciseSleep.Microseconds(4000);
        }

        foreach (var d in _devices.Values) Close(d);
        _devices.Clear();
        _sdl.QuitSubSystem(InitJoystick | InitGameController);
    }

    /// <summary>Opens newly attached devices and closes removed ones; true when the set changed.</summary>
    bool SyncDevices()
    {
        var sdl = _sdl!;
        bool changed = false;
        int n = sdl.NumJoysticks();
        Span<int> present = stackalloc int[Math.Max(n, 1)];
        for (int i = 0; i < n; i++)
        {
            int id = sdl.JoystickGetDeviceInstanceID(i);
            present[i] = id;
            if (id < 0 || _devices.ContainsKey(id)) continue;
            var d = Open(i, id);
            if (d == null) continue;
            _devices[id] = d;
            changed = true;
            Log.Info($"Input: controller connected: {d.Info.Name} [{d.Info.Model}, {(d.Info.IsGamepad ? "mapped" : "raw joystick")}{(d.Info.HasTouchpad ? ", touch pad" : "")}] guid {d.Info.Guid}");
        }
        foreach (var id in _devices.Keys.ToArray())
        {
            if (present[..n].Contains(id)) continue;
            Log.Info($"Input: controller disconnected: {_devices[id].Info.Name}");
            Close(_devices[id]);
            _devices.Remove(id);
            lock (_lock) _states.Remove(id);
            changed = true;
        }
        if (changed)
        {
            var infos = _devices.Values.Select(d => d.Info).OrderBy(i => i.InstanceId).ToArray();
            lock (_lock) _infos = infos;
        }
        return changed;
    }

    Device? Open(int index, int id)
    {
        var sdl = _sdl!;
        bool isGamepad = sdl.IsGameController(index) == SdlBool.True;
        GameController* gc = null;
        Joystick* js;
        if (isGamepad)
        {
            gc = sdl.GameControllerOpen(index);
            if (gc == null) isGamepad = false;
        }
        js = gc != null ? sdl.GameControllerGetJoystick(gc) : sdl.JoystickOpen(index);
        if (js == null) return null;

        var guidBytes = stackalloc byte[33];
        sdl.JoystickGetGUIDString(sdl.JoystickGetGUID(js), guidBytes, 33);
        string guid = Marshal.PtrToStringAnsi((nint)guidBytes) ?? "";
        string name = (gc != null ? sdl.GameControllerNameS(gc) : sdl.JoystickNameS(js)) ?? "Controller";
        var type = gc != null ? sdl.GameControllerGetType(gc) : GameControllerType.Unknown;
        var kind = type switch
        {
            GameControllerType.Xbox360 or GameControllerType.Xboxone => ControllerKind.Xbox,
            GameControllerType.PS3 or GameControllerType.PS4 or GameControllerType.PS5 => ControllerKind.PlayStation,
            GameControllerType.NintendoSwitchPro or GameControllerType.NintendoSwitchJoyconLeft or GameControllerType.NintendoSwitchJoyconRight
                or GameControllerType.NintendoSwitchJoyconPair => ControllerKind.Nintendo,
            _ => ControllerClassifier.Classify(guid, name),
        };
        bool touch = gc != null && sdl.GameControllerGetNumTouchpads(gc) > 0;
        string model = type switch
        {
            GameControllerType.Xbox360 => "Xbox 360", GameControllerType.Xboxone => "Xbox One / Series",
            GameControllerType.PS3 => "DualShock 3", GameControllerType.PS4 => "DualShock 4", GameControllerType.PS5 => "DualSense",
            GameControllerType.NintendoSwitchPro => "Switch Pro", GameControllerType.NintendoSwitchJoyconPair => "Joy-Con pair",
            GameControllerType.NintendoSwitchJoyconLeft or GameControllerType.NintendoSwitchJoyconRight => "Joy-Con",
            GameControllerType.Virtual => "virtual", GameControllerType.AmazonLuna => "Luna", GameControllerType.GoogleStadia => "Stadia",
            GameControllerType.NvidiaShield => "Shield",
            _ => isGamepad ? "standard gamepad" : "joystick",
        };
        return new Device
        {
            Info = new PadInfo(id, guid, name, kind, isGamepad, touch, model, sdl.JoystickIsVirtual(index) == SdlBool.True),
            Controller = gc, Joystick = js,
        };
    }

    void Close(Device d)
    {
        if (d.Controller != null) _sdl!.GameControllerClose(d.Controller);
        else if (d.Joystick != null) _sdl!.JoystickClose(d.Joystick);
        d.Controller = null;
        d.Joystick = null;
    }

    void Sample(Device d)
    {
        var sdl = _sdl!;
        var s = d.State;
        s.IsGamepad = d.Controller != null;
        if (d.Controller != null)
        {
            var gc = d.Controller;
            for (int i = 0; i < s.Buttons.Length; i++) s.Buttons[i] = sdl.GameControllerGetButton(gc, (GameControllerButton)i) != 0;
            for (int i = 0; i < 4; i++) s.Axes[i] = Math.Max(-1f, sdl.GameControllerGetAxis(gc, (GameControllerAxis)i) / 32767f);
            for (int i = 4; i < 6; i++) s.Axes[i] = Math.Clamp(sdl.GameControllerGetAxis(gc, (GameControllerAxis)i) / 32767f, 0f, 1f);
            s.HasTouchpad = d.Info.HasTouchpad;
            if (s.HasTouchpad)
            {
                int fingers = Math.Min(PadSnapshot.MaxFingers, sdl.GameControllerGetNumTouchpadFingers(gc, 0));
                for (int f = 0; f < PadSnapshot.MaxFingers; f++)
                {
                    byte down = 0;
                    float x = 0, y = 0, pressure = 0;
                    if (f < fingers) sdl.GameControllerGetTouchpadFinger(gc, 0, f, &down, &x, &y, &pressure);
                    s.FingerDown[f] = down != 0;
                    s.FingerX[f] = x;
                    s.FingerY[f] = y;
                }
            }
        }
        var js = d.Joystick;
        s.JoyAxisCount = Math.Min(PadSnapshot.MaxJoyAxes, Math.Max(0, sdl.JoystickNumAxes(js)));
        s.JoyButtonCount = Math.Min(PadSnapshot.MaxJoyButtons, Math.Max(0, sdl.JoystickNumButtons(js)));
        s.JoyHatCount = Math.Min(PadSnapshot.MaxJoyHats, Math.Max(0, sdl.JoystickNumHats(js)));
        for (int i = 0; i < s.JoyAxisCount; i++) s.JoyAxes[i] = Math.Max(-1f, sdl.JoystickGetAxis(js, i) / 32767f);
        for (int i = 0; i < s.JoyButtonCount; i++) s.JoyButtons[i] = sdl.JoystickGetButton(js, i) != 0;
        for (int i = 0; i < s.JoyHatCount; i++) s.JoyHats[i] = sdl.JoystickGetHat(js, i);
    }

    /// <summary>Copies the latest state of a controller; false when it is no longer connected.</summary>
    public bool TryGetState(int instanceId, PadSnapshot into)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(instanceId, out var s)) return false;
            s.CopyTo(into);
            return true;
        }
    }

    // ------------------------------------------------------------------ simulated controllers (tests, previews)

    /// <summary>
    /// Attaches an SDL virtual game controller (it goes through the same detection, database mapping and polling as
    /// a real one). Returns a handle for <see cref="SetVirtual"/>; <paramref name="vendor"/>/<paramref name="product"/>
    /// make it look like a given model (e.g. 0x054C/0x0CE6 for a DualSense).
    /// </summary>
    public VirtualPad? AttachVirtual(string name, ushort vendor = 0, ushort product = 0)
    {
        if (!Available) return null;
        var done = new TaskCompletionSource<VirtualPad?>();
        _commands.Enqueue(() =>
        {
            var sdl = _sdl!;
            var nameBytes = (byte*)Marshal.StringToHGlobalAnsi(name);
            var desc = new VirtualJoystickDesc
            {
                Version = 1, Type = (ushort)JoystickType.Gamecontroller, Naxes = 6, Nbuttons = 15, Nhats = 0,
                VendorId = vendor, ProductId = product, Name = nameBytes,
            };
            int index = sdl.JoystickAttachVirtualEx(&desc);
            if (index < 0) { Marshal.FreeHGlobal((nint)nameBytes); done.SetResult(null); return; }
            var js = sdl.JoystickOpen(index); // keeps a reference for SetVirtual*
            done.SetResult(new VirtualPad(this, index, (nint)js, (nint)nameBytes));
        });
        return done.Task.Wait(TimeSpan.FromSeconds(5)) ? done.Task.Result : null;
    }

    public sealed class VirtualPad(SdlGamepads owner, int index, nint joystick, nint name)
    {
        public int Index => index;
        internal nint Joystick => joystick;
        internal nint Name => name;
        internal SdlGamepads Owner => owner;
    }

    /// <summary>Sets a button (0..14, SDL order) or axis (-1..1; triggers 0..1) of a virtual controller.</summary>
    public void SetVirtual(VirtualPad pad, PadButton? button = null, bool pressed = false, PadAxis? axis = null, float value = 0)
    {
        _commands.Enqueue(() =>
        {
            var js = (Joystick*)pad.Joystick;
            if (button is { } b) _sdl!.JoystickSetVirtualButton(js, (int)b, (byte)(pressed ? 1 : 0));
            if (axis is { } a)
            {
                // SDL virtual triggers use the full -32768..32767 range (released = -32768)
                float v = a >= PadAxis.LeftTrigger ? value * 2 - 1 : value;
                _sdl!.JoystickSetVirtualAxis(js, (int)a, (short)Math.Clamp((int)MathF.Round(v * 32767f), -32768, 32767));
            }
        });
    }

    public void DetachVirtual(VirtualPad pad)
    {
        _commands.Enqueue(() =>
        {
            var sdl = _sdl!;
            // the virtual device index may have moved: detach by matching the joystick's instance id
            int id = sdl.JoystickInstanceID((Joystick*)pad.Joystick);
            sdl.JoystickClose((Joystick*)pad.Joystick);
            int n = sdl.NumJoysticks();
            for (int i = 0; i < n; i++)
                if (sdl.JoystickIsVirtual(i) == SdlBool.True && sdl.JoystickGetDeviceInstanceID(i) == id)
                {
                    sdl.JoystickDetachVirtual(i);
                    break;
                }
            Marshal.FreeHGlobal(pad.Name);
        });
    }

    /// <summary>Waits until the SDL thread has run every queued command and sampled the devices once more.</summary>
    public void Flush()
    {
        if (!Available) return;
        for (int pass = 0; pass < 2; pass++)
        {
            var done = new ManualResetEventSlim();
            _commands.Enqueue(done.Set);
            done.Wait(TimeSpan.FromSeconds(2));
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
    }
}
