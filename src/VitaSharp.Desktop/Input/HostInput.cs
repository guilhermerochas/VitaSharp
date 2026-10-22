using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Input;

/// <summary>
/// Keyboard + controllers of the game window mapped onto the Vita through the profiles of input.json (defaults when
/// a device has none). Every connected controller drives the Vita at once; keys come from the window's key events.
/// Thread-safe: the emulator polls from its own threads.
/// </summary>
public sealed class HostInput : IDisposable
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

    public static string ConfigPath(string dataRoot) => Path.Combine(dataRoot, InputProfileStore.FileName);

    /// <param name="pads">Controller backend to share (pad-test); null = start one when <paramref name="controllers"/> is set.</param>
    public HostInput(string dataRoot, bool controllers = true, SdlGamepads? pads = null)
    {
        _store = InputProfileStore.Load(ConfigPath(dataRoot));
        _keyboard = new CompiledProfile(_store.KeyboardProfile());
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
        lock (_lock) { if (down) _keys.Add(key); else _keys.Remove(key); }
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

    public void Dispose() { if (_ownsPads) _pads?.Dispose(); }
}
