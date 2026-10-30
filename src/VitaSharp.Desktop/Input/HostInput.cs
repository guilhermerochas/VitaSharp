using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Input;

/// <summary>
/// Keyboard + controllers of the game window mapped onto the Vita through the profiles of input.json (defaults when
/// a device has none). Every connected controller drives the Vita at once; keys come from the window's key events.
/// Thread-safe: the emulator polls from its own threads. Also the motion source (SceMotion): the gyroscope and
/// accelerometer of the first controller that has them, else the tilt keys and mouse look (<see cref="VirtualMotion"/>),
/// as the keyboard profile's <see cref="ControllerProfile.MotionSource"/> chooses.
/// </summary>
public sealed class HostInput : IMotionSource, IDisposable
{
    readonly object _lock = new();
    readonly InputProfileStore _store;
    readonly CompiledProfile _keyboard;
    readonly HashSet<string> _keys = new();
    readonly SdlGamepads? _pads;
    readonly bool _ownsPads;
    readonly Dictionary<int, CompiledProfile> _padProfiles = new();
    readonly PadSnapshot _scratch = new();
    PadSnapshot? _fallback;
    CompiledProfile? _fallbackProfile;
    int _motionPad = -1;
    ulong _motionAfter;
    readonly PadMotionSample[] _motionBuf = new PadMotionSample[SdlGamepads.MotionCapacity];
    readonly VirtualMotion _virtual;
    bool _virtualLogged;

    public static string ConfigPath(string dataRoot) => Path.Combine(dataRoot, InputProfileStore.FileName);

    /// <param name="pads">Controller backend to share (pad-test); null = start one when <paramref name="controllers"/> is set.</param>
    public HostInput(string dataRoot, bool controllers = true, SdlGamepads? pads = null)
    {
        _store = InputProfileStore.Load(ConfigPath(dataRoot));
        _keyboard = new CompiledProfile(_store.KeyboardProfile());
        _virtual = new VirtualMotion(_keyboard.Profile.Tilt);
        if (_store.Config.Keyboard != null || _store.Config.Controllers.Count > 0)
            Log.Info($"Input: profiles from {ConfigPath(dataRoot)} (keyboard {(_store.Config.Keyboard != null ? "custom" : "default")}, {_store.Config.Controllers.Count} controller profile(s))");
        if (pads != null) _pads = pads.Available ? pads : null;
        else if (controllers)
        {
            _pads = new SdlGamepads(dataRoot);
            _ownsPads = true;
            if (!_pads.Available) { _pads.Dispose(); _pads = null; }
        }
    }

    /// <summary>True when SDL reads the controllers; otherwise the window feeds them through <see cref="SetFallbackPad"/>.</summary>
    public bool ControllersAvailable => _pads != null;

    public void KeyChanged(string key, bool down)
    {
        lock (_lock)
        {
            if (down) _keys.Add(key); else _keys.Remove(key);
            // tilt keys take effect (and a quick re-centre tap is seen) even between two SceMotion polls
            if (SourceMode is not (MotionSourceMode.Off or MotionSourceMode.Controller)) UpdateVirtualControls();
        }
    }

    /// <summary>Controller state from another backend (GLFW gamepads) when SDL is not available; null = none.</summary>
    public void SetFallbackPad(PadSnapshot? state, string guid, string name)
    {
        lock (_lock)
        {
            if (state == null) { _fallback = null; return; }
            _fallback ??= new PadSnapshot();
            state.CopyTo(_fallback);
            if (_fallbackProfile == null || _fallbackProfile.Profile.Id != guid)
            {
                var kind = ControllerClassifier.Classify(guid, name);
                _fallbackProfile = new CompiledProfile(_store.ProfileFor(guid, name, kind, true));
            }
        }
    }

    CompiledProfile ProfileFor(PadInfo info)
    {
        if (!_padProfiles.TryGetValue(info.InstanceId, out var p) || p.Profile.Id != info.Guid)
        {
            var profile = _store.ProfileFor(info.Guid, info.Name, info.Kind, info.IsGamepad);
            _padProfiles[info.InstanceId] = p = new CompiledProfile(profile);
            Log.Info($"Input: {info.Name}: {(_store.FindSaved(info.Guid, info.Name) != null ? "saved profile" : "default mapping")}");
        }
        return p;
    }

    /// <summary>Current Vita input from the keyboard and every controller.</summary>
    public MappedInput Poll()
    {
        lock (_lock)
        {
            var m = InputMapper.Map(_keyboard, null, _keys);
            if (_pads != null)
            {
                int k = 0;
                foreach (var info in _pads.Devices)
                {
                    if (!_pads.TryGetState(info.InstanceId, _scratch)) continue;
                    m = InputMapper.Combine(m, InputMapper.Map(ProfileFor(info), _scratch, null), 100 * ++k);
                }
            }
            else if (_fallback != null && _fallbackProfile != null)
                m = InputMapper.Combine(m, InputMapper.Map(_fallbackProfile, _fallback, null));
            return m;
        }
    }

    // ------------------------------------------------------------------ motion (IMotionSource)

    /// <summary>Tilt keys + mouse look, the motion source when no controller with sensors drives the Vita (or when
    /// the keyboard profile picks "Teclado e mouse").</summary>
    public VirtualMotion Virtual => _virtual;

    MotionSourceMode SourceMode => _keyboard.Profile.MotionSource;

    /// <summary>The mouse moved by (dx, dy) pixels in the game window while the middle button was held.</summary>
    public void MouseLook(float dx, float dy)
    {
        if (!_keyboard.Profile.Tilt.MouseLook || SourceMode is MotionSourceMode.Off or MotionSourceMode.Controller) return;
        _virtual.AddMouse(dx, dy);
    }

    /// <summary>Feeds the tilt controls of the keyboard and of every controller to the virtual motion. Called under _lock.</summary>
    void UpdateVirtualControls()
    {
        float fwd = 0, back = 0, left = 0, right = 0, tl = 0, tr = 0;
        bool recenter = false;
        void Add(CompiledProfile cp, PadSnapshot? pad, IReadOnlySet<string>? keys)
        {
            float V(VitaControl c) => InputMapper.Value(cp, c, pad, keys);
            fwd = Math.Max(fwd, V(VitaControl.TiltForward)); back = Math.Max(back, V(VitaControl.TiltBack));
            left = Math.Max(left, V(VitaControl.TiltLeft)); right = Math.Max(right, V(VitaControl.TiltRight));
            tl = Math.Max(tl, V(VitaControl.TwistLeft)); tr = Math.Max(tr, V(VitaControl.TwistRight));
            recenter |= V(VitaControl.MotionRecenter) >= 0.5f;
        }
        Add(_keyboard, null, _keys);
        // controllers may bind the tilt controls too (a pad without a gyroscope)
        if (_pads != null)
        {
            foreach (var info in _pads.Devices)
            {
                var cp = ProfileFor(info);
                if (cp.HasMotionBindings && _pads.TryGetState(info.InstanceId, _scratch)) Add(cp, _scratch, null);
            }
        }
        else if (_fallback != null && _fallbackProfile is { HasMotionBindings: true }) Add(_fallbackProfile, _fallback, null);
        _virtual.SetControls(fwd, back, left, right, tl, tr, recenter);
    }

    /// <summary>The controller whose sensors drive the Vita: the first connected one with a gyroscope and an
    /// accelerometer whose profile keeps "Usar giroscópio do controle" on. Called under _lock.</summary>
    PadInfo? MotionPad()
    {
        if (_pads == null || SourceMode is MotionSourceMode.Off or MotionSourceMode.KeyboardMouse) return null;
        foreach (var info in _pads.Devices)
            if (info.HasMotion && ProfileFor(info).Profile.MotionEnabled) return info;
        return null;
    }

    /// <summary>Whether the virtual motion drives the Vita: picked explicitly, or automatically when no controller
    /// with sensors does, from the first use of a tilt control or of mouse look (until then the Vita keeps the
    /// historic at-rest state). Called under _lock.</summary>
    bool VirtualActive(PadInfo? pad)
    {
        if (pad != null || SourceMode is MotionSourceMode.Off or MotionSourceMode.Controller) return false;
        UpdateVirtualControls();
        return _virtual.Used;
    }

    public bool HasSensors
    {
        get
        {
            lock (_lock)
            {
                var pad = MotionPad();
                return pad != null || VirtualActive(pad);
            }
        }
    }

    public int ReadSamples(Span<MotionSample> dest)
    {
        lock (_lock)
        {
            var pad = MotionPad();
            if (pad == null)
            {
                _motionPad = -1;
                if (!VirtualActive(null)) return 0;
                if (!_virtualLogged)
                {
                    _virtualLogged = true;
                    Log.Info($"Input: tilt keys / mouse look drive the Vita's motion sensors ({SourceMode}, max {_keyboard.Profile.Tilt.MaxAngle:0} deg)");
                }
                return _virtual.Generate(NowUs(), dest);
            }
            var orientation = ProfileFor(pad).Profile.MotionOrientation;
            if (pad.InstanceId != _motionPad)
            {
                _motionPad = pad.InstanceId;
                _motionAfter = 0;
                _virtualLogged = false;
                Log.Info($"Input: motion sensors of {pad.Name} drive the Vita ({orientation})");
            }
            int n = _pads!.ReadMotion(pad.InstanceId, ref _motionAfter, _motionBuf.AsSpan(0, Math.Min(dest.Length, _motionBuf.Length)));
            for (int i = 0; i < n; i++)
            {
                var m = _motionBuf[i];
                dest[i] = MotionAxes.FromGamepad(m.TimestampUs, m.Accel, m.Gyro, orientation);
            }
            return n;
        }
    }

    static long NowUs() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency * 1e6);

    public void Dispose() { if (_ownsPads) _pads?.Dispose(); }
}
