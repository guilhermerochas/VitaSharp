namespace VitaSharp.Core.Host;

/// <summary>
/// Which host audio output to use and when to move: the desktop backend (OpenAlAudio) asks it at start and then every
/// couple of seconds with the current output list. With a chosen output ("Saida de audio" in the launcher,
/// --audio-device) the stream never moves away from it on its own: only when it disappears does it fall back to the
/// system default, and it goes back to the chosen output as soon as that is listed again. With the system default it
/// follows the default as Windows changes it and logs each change, but never moves to a game controller's audio output
/// (the headset jack of a DualShock 4, DualSense or Xbox controller, which Windows can make the default when the
/// controller connects): only choosing it in the setting selects one. Headphones and headsets are ordinary outputs.
/// Platform-neutral so the rules are unit-tested.
/// </summary>
public sealed class AudioOutputFollower
{
    /// <summary>The output asked for (as given: a full name or part of one); null = the system default.</summary>
    public string? Requested { get; }

    /// <summary>The output currently open ("" = the backend's own default).</summary>
    public string Current { get; private set; } = "";

    /// <summary>The chosen output is gone and the stream plays on the system default until it returns.</summary>
    public bool OnFallback { get; private set; }

    private readonly Func<string, bool> _isController;
    private string? _lastDefault;

    /// <param name="requested">The chosen output; null or empty = the system default.</param>
    /// <param name="isController">Whether an output belongs to a game controller (the host's device check,
    /// <see cref="ControllerOutputs"/>); null = the name rule <see cref="LooksLikeController"/> alone.</param>
    public AudioOutputFollower(string? requested, Func<string, bool>? isController = null)
    {
        Requested = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        _isController = isController ?? LooksLikeController;
    }

    private static readonly string[] s_controllerMarkers =
        ["wireless controller", "dualsense", "dualshock", "xbox controller", "xbox wireless adapter", "8bitdo", "gamepad", "game controller"];

    /// <summary>
    /// The name rule, the fallback when the host cannot tell which device an output belongs to: the DualShock 4 shows up
    /// as "Wireless Controller", the DualSense as "DualSense Wireless Controller", Xbox controllers' headset jack as
    /// "Xbox Controller" / "Xbox Wireless Adapter" (the output name is "&lt;kind&gt; (&lt;device&gt;)", e.g. "Fone de ouvido do
    /// headset (Wireless Controller)"). Headphones and headsets named after their maker do not match.
    /// </summary>
    public static bool LooksLikeController(string name) => s_controllerMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>The empty container ID Windows gives devices it cannot group, and the one of the PC itself (built-in
    /// devices): neither identifies a game controller.</summary>
    private static readonly Guid s_noContainer = Guid.Empty, s_computerContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    /// <summary>
    /// The device rule: the outputs that belong to the same physical device (Windows container ID) as a game controller
    /// (a HID device of the game-controller kind, HID_DEVICE_SYSTEM_GAME, or an XInput controller of the XnaComposite /
    /// XboxComposite classes or the xusb22 / dc1-controller services).
    /// </summary>
    public static HashSet<string> ControllerOutputs(IEnumerable<(string Name, Guid Container)> outputs,
        IEnumerable<(Guid Container, IReadOnlyList<string> CompatibleIds, string Class, string Service)> devices)
    {
        var controllers = new HashSet<Guid>();
        foreach ((Guid Container, IReadOnlyList<string> CompatibleIds, string Class, string Service) d in devices)
        {
            if (d.Container == s_noContainer || d.Container == s_computerContainer)
            {
                continue;
            }

            bool game = d.CompatibleIds.Any(id => id.Equals("HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase))
                        || d.Class.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase) || d.Class.Equals("XboxComposite", StringComparison.OrdinalIgnoreCase)
                        || d.Service.Equals("xusb22", StringComparison.OrdinalIgnoreCase) || d.Service.Equals("dc1-controller", StringComparison.OrdinalIgnoreCase);
            if (game)
            {
                controllers.Add(d.Container);
            }
        }
        return outputs.Where(o => controllers.Contains(o.Container)).Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The listed output a request names: the exact name, else the first one containing it.</summary>
    public static string? Match(IReadOnlyList<string> outputs, string requested) =>
        outputs.FirstOrDefault(n => n.Equals(requested, StringComparison.OrdinalIgnoreCase))
        ?? outputs.FirstOrDefault(n => n.Contains(requested, StringComparison.OrdinalIgnoreCase));

    /// <summary>A name for menus: OpenAL Soft prefixes its WASAPI/DirectSound outputs with "OpenAL Soft on ".</summary>
    public static string FriendlyName(string name) =>
        name.StartsWith("OpenAL Soft on ", StringComparison.Ordinal) ? name["OpenAL Soft on ".Length..] : name;

    private bool IsController(string name) => _isController(name) || _isController(FriendlyName(name));

    /// <summary>The system default unless it is a game controller's output, in which case the first other output ("" =
    /// the backend's default when nothing is known).</summary>
    private string DefaultChoice(IReadOnlyList<string> outputs, string? systemDefault)
    {
        if (systemDefault != null && IsController(systemDefault) && outputs.FirstOrDefault(n => !IsController(n)) is { } other)
        {
            return other;
        }

        return systemDefault ?? "";
    }

    /// <summary>The output to open at start, with the log line that explains the choice.</summary>
    public string Initial(IReadOnlyList<string> outputs, string? systemDefault, out string message)
    {
        _lastDefault = systemDefault;
        string all = string.Join(" | ", outputs);
        if (Requested != null)
        {
            if (Match(outputs, Requested) is { } m)
            {
                message = $"Audio: using the chosen output '{m}' (system default: {systemDefault ?? "?"})";
                return Current = m;
            }
            OnFallback = true;
            Current = DefaultChoice(outputs, systemDefault);
            message = $"Audio: the chosen output '{Requested}' is not connected (outputs: {all}); using '{Current}' until it is";
            return Current;
        }
        Current = DefaultChoice(outputs, systemDefault);
        message = Current != (systemDefault ?? "")
            ? $"Audio: the system default '{systemDefault}' is a game controller's output; using '{Current}' instead (outputs: {all})"
            : $"Audio: system default '{Current}' (outputs: {all})";
        return Current;
    }

    /// <summary>
    /// A periodic check. <paramref name="connected"/>: the open output still works (ALC_CONNECTED). Returns the output
    /// to reopen, or null to stay; <paramref name="message"/> is a line for the log (also when nothing moves, e.g. a
    /// default change while a chosen output is pinned), null when nothing changed.
    /// </summary>
    public string? Check(IReadOnlyList<string> outputs, string? systemDefault, bool connected, out string? message)
    {
        message = null;
        bool defaultChanged = systemDefault != _lastDefault;
        string? oldDefault = _lastDefault;
        _lastDefault = systemDefault;
        if (Requested != null)
        {
            string? chosen = Match(outputs, Requested);
            if (!OnFallback)
            {
                if (connected && chosen != null)
                {
                    if (defaultChanged)
                    {
                        message = $"Audio: system default changed to '{systemDefault ?? "?"}'; staying on the chosen output '{Current}'";
                    }

                    return null;
                }
                OnFallback = true;
                string to = DefaultChoice(outputs, systemDefault);
                message = $"Audio: the chosen output '{Current}' disconnected; using the system default '{to}' until it returns";
                return Current = to;
            }
            if (chosen != null)
            {
                OnFallback = false;
                message = $"Audio: the chosen output '{chosen}' is back; switching to it";
                return Current = chosen;
            }
            if (!connected || defaultChanged)
            {
                string to = DefaultChoice(outputs, systemDefault);
                if (to == Current && connected)
                {
                    return null;
                }

                message = $"Audio: the chosen output '{Requested}' is still missing; system default now '{to}'";
                return Current = to;
            }
            return null;
        }
        if (!defaultChanged && connected)
        {
            return null;
        }
        // the system default never takes the stream to a game controller's output on its own: stay where it plays
        if (connected && systemDefault != null && IsController(systemDefault))
        {
            message = $"Audio: system default changed to the game controller output '{systemDefault}'; staying on '{Current}' (choose it under Audio output to use it)";
            return null;
        }
        string next = DefaultChoice(outputs, systemDefault);
        message = defaultChanged
            ? $"Audio: system default changed: '{oldDefault ?? "?"}' -> '{systemDefault ?? "?"}'" + (next != (systemDefault ?? "") ? $" (a game controller's output; using '{next}')" : "")
            : $"Audio: output '{Current}' disconnected; reopening the system default '{next}'";
        if (next == Current && connected)
        {
            return null;
        }

        return Current = next;
    }
}
