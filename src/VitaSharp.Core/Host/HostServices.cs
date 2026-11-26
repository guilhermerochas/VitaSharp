namespace VitaSharp.Core.Host;

[Flags]
public enum VitaButtons : uint
{
    None = 0,
    Select = 0x1, L3 = 0x2, R3 = 0x4, Start = 0x8,
    Up = 0x10, Right = 0x20, Down = 0x40, Left = 0x80,
    LTrigger = 0x100, RTrigger = 0x200, L1 = 0x400, R1 = 0x800,
    Triangle = 0x1000, Circle = 0x2000, Cross = 0x4000, Square = 0x8000,
    Intercepted = 0x10000, PsButton = 0x10000, HeadphoneConnected = 0x80000,
    // SCE_CTRL_VOLUP / VOLDOWN / POWER (vita-headers psp2common/ctrl.h; ScePower's button thread tests 0x40000000)
    VolumeUp = 0x100000, VolumeDown = 0x200000, PowerButton = 0x40000000,
}

public struct ControllerState
{
    public VitaButtons Buttons;
    public byte Lx, Ly, Rx, Ry; // 0..255, 128 = centre
    public static ControllerState Neutral => new() { Lx = 128, Ly = 128, Rx = 128, Ry = 128 };
}

public readonly record struct TouchPoint(int Id, float X, float Y); // normalised 0..1

/// <summary>Input provided by the front-end.</summary>
public interface IInputBackend
{
    ControllerState GetController(int port);
    IReadOnlyList<TouchPoint> GetTouches(bool back);

    /// <summary>Copies up to <c>dest.Length</c> current touches into <paramref name="dest"/> and returns how many were
    /// written; front-ends override it to avoid allocating a list on every poll (games poll touch every frame).</summary>
    int GetTouches(bool back, Span<TouchPoint> dest)
    {
        IReadOnlyList<TouchPoint> list = GetTouches(back);
        int n = Math.Min(list.Count, dest.Length);
        for (int i = 0; i < n; i++)
        {
            dest[i] = list[i];
        }

        return n;
    }
}

/// <summary>
/// The host's controllers one by one, for a PS TV's ports 1-4 (<see cref="Hle.ConsoleModel.PsTv"/>): the first is the
/// keyboard with the first controller, as on the handheld's single port.
/// </summary>
public interface IMultiControllerInput
{
    /// <summary>How many controllers the host has now (at least 1: the keyboard is the first player), at most 4.</summary>
    int ControllerCount { get; }

    /// <summary>The state of controller <paramref name="index"/> (0-based); neutral when it is not connected.</summary>
    ControllerState GetPad(int index);
}

/// <summary>Vibration of the host controller of a port (sceCtrlSetActuator on a PS TV).</summary>
public interface IControllerVibration
{
    /// <summary>Sets the motors of the controller on <paramref name="port"/> (1-4): the small (high frequency) and the
    /// large (low frequency) motor, 0 = off, 255 = full.</summary>
    void SetVibration(int port, byte small, byte large);
}

/// <summary>
/// An input source that is told which read of the guest it answers: the <paramref name="guestRead"/>-th read of that
/// port (or panel) by the process, counted by its controller and touch drivers and kept in its save states. A "Gravar
/// tudo" recording notes at which read the guest first saw each change; its re-run hands the change to that same read,
/// so the guest sees the same input at the same point of its own work however fast the host ran it.
/// </summary>
public interface IGuestReadInput
{
    ControllerState GetController(int port, long guestRead);

    int GetTouches(bool back, Span<TouchPoint> dest, long guestRead);
}

/// <summary>An audio output stream fed with interleaved signed 16-bit samples.</summary>
public interface IAudioStream : IDisposable
{
    /// <summary>Queues samples; blocks while the host buffer is full (this provides the guest's audio pacing).</summary>
    void Submit(ReadOnlySpan<short> interleaved);
    float Volume { get; set; }
    /// <summary>Sample frames queued but not yet played (sceAudioOutGetRestSample); games use it for A/V sync.</summary>
    int PendingSamples { get; }
    /// <summary>Times the host stream ran dry (the device played silence because no grain was queued in time).</summary>
    int Underruns => 0;
}

public interface IAudioBackend : IDisposable
{
    IAudioStream OpenStream(int sampleRate, int channels, int grainSamples);

    /// <summary>The emulation was paused (true) or resumed (<see cref="EmulationPause"/>): the device stops playing the
    /// queued grains, without counting it as underruns, and goes on where it stopped. Any thread.</summary>
    void SetPaused(bool paused)
    {
    }
}

/// <summary>
/// Host keyboard backing the in-game IME (SceIme). The callbacks may be invoked from any thread (typically the UI
/// thread); the core queues them and delivers the matching guest events from sceImeUpdate.
/// </summary>
public interface ITextInputBackend
{
    /// <summary>Shows the host keyboard with <paramref name="initial"/> text. <paramref name="onTextChanged"/> receives
    /// the full new text and caret index; <paramref name="onEnter"/>/<paramref name="onClose"/> the Enter/Close keys.</summary>
    void Begin(string initial, int maxLength, int type, bool multiline,
               Action<string, int> onTextChanged, Action onEnter, Action onClose);
    /// <summary>The game replaced the text (sceImeSetText).</summary>
    void SetText(string text, int caret);
    void End();
}

/// <summary>What sceImeDialogInit asks for. <paramref name="Type"/> is SceImeType (0 default, 1 basic Latin, 2 URL,
/// 3 mail, 4 number, 5 extended number); <paramref name="TextBoxMode"/> 0 default, 1 password, 2 password with the
/// last character shown.</summary>
public sealed record ImeDialogRequest(string Title, string InitialText, int MaxLength, int Type, bool Multiline, int TextBoxMode)
{
    public bool Password => TextBoxMode is 1 or 2;
}

/// <summary>
/// Host text entry backing the system IME dialog (sceImeDialog*): a modal text field over the game. <see cref="Open"/>
/// is called from a guest thread and must not block; the front-end calls <c>done</c> once, from any thread, with the
/// confirmed text or null when the user cancelled. <see cref="Close"/> hides it without an answer (sceImeDialogAbort).
/// </summary>
public interface IImeDialogBackend
{
    void Open(ImeDialogRequest request, Action<string?> done);
    void Close();
}

/// <summary>One button of a system message dialog: its label and the value the guest gets back (a
/// SCE_MSG_DIALOG_BUTTON_ID / SCE_SAVEDATA_DIALOG_BUTTON_ID). <paramref name="IsCancel"/>: the button Circle / Esc
/// presses (NO, Cancel).</summary>
public sealed record MessageDialogButton(string Label, int Id, bool IsCancel = false);

/// <summary>What a system message dialog shows (sceMsgDialog, the SaveDataDialog's messages): the message, its buttons
/// (none: a wait / progress message the application closes itself) and an optional progress bar (0..100).</summary>
public sealed record MessageDialogRequest(string Message, IReadOnlyList<MessageDialogButton> Buttons, int? Progress = null)
{
    /// <summary>Index of the button selected when the dialog opens.</summary>
    public int DefaultButton { get; init; }
    /// <summary>The console's enter button is Circle (Japanese / Asian systems): Circle presses the selected button and
    /// Cross cancels, instead of the other way round.</summary>
    public bool CircleConfirms { get; init; }
}

/// <summary>
/// Host UI backing the system message dialogs (sceMsgDialog*, sceSaveDataDialog* messages and confirmations): a modal
/// panel over the game. <see cref="Open"/> is called from a guest thread and must not block; the front-end calls
/// <c>done</c> once, from any thread, with the chosen button's index in <see cref="MessageDialogRequest.Buttons"/>.
/// A dialog without buttons is closed by the core with <see cref="Close"/> (also sceMsgDialogAbort / Term).
/// </summary>
public interface IMessageDialogBackend
{
    void Open(MessageDialogRequest request, Action<int> done);
    /// <summary>The progress bar of the open dialog moved (0..100) or its text changed (null keeps it). Any thread.</summary>
    void SetProgress(int percent, string? message)
    {
    }
    void Close();
}

/// <summary>Host services the core needs from a front-end. Everything is optional (headless runs).</summary>
public sealed class HostServices : IDisposable
{
    public IInputBackend? Input { get; set; }
    public IAudioBackend? Audio { get; set; }
    /// <summary>Keyboard for the in-game IME (sceImeOpen); null = no host keyboard (scripted --ime-text only).</summary>
    public ITextInputBackend? TextInput { get; set; }
    /// <summary>Text entry for the IME dialog (sceImeDialogInit); null = answered at once with --ime-text or the
    /// initial text.</summary>
    public IImeDialogBackend? ImeDialog { get; set; }
    /// <summary>UI for the message dialogs and the save data dialog's messages (sceMsgDialogInit,
    /// sceSaveDataDialogInit); null = answered at once with the automatic choice (headless and scripted runs).</summary>
    public IMessageDialogBackend? MessageDialog { get; set; }
    /// <summary>Webcam / device camera backing SceCamera; null = emulated camera delivering grey frames.</summary>
    public ICameraSource? Camera { get; set; }
    /// <summary>Motion sensors backing SceMotion (controller gyro, phone sensors); null = device at rest.</summary>
    public IMotionSource? Motion { get; set; }

    /// <summary>Camera streams the guest has started; survives a process exec so the new process can release them.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "used by Hle/Modules/Camera.cs, which is still in build/format-pending.txt: renamed to _cameraStreams with the Hle conversion")]
    internal readonly List<ICameraStream> CameraStreams = new();

    /// <summary>Host microphone backing SceAudioIn (capture device, Android AudioRecord, scripted tone); null = no
    /// microphone, the port delivers silence at its pace.</summary>
    public IMicrophoneSource? Microphone { get; set; }

    /// <summary>Where the unit's position comes from (SceLibLocation): a fixed position or the host system's location
    /// service; null = location off (the default: the application is not allowed to use it).</summary>
    public ILocationSource? Location { get; set; }

    /// <summary>The image file backing the internal eMMC at the block level (SceSdif host 0,
    /// <see cref="Util.EnvVars.EmmcImage"/>); null = no image, the sector commands reach no device (the partitions
    /// are served at the file level either way).</summary>
    public BlockImage? EmmcImage { get; set; } = BlockImage.FromEnvironment(Util.EnvVars.EmmcImage);
    /// <summary>The Wi-Fi's ad hoc mode between emulator instances (the LAN medium, <see cref="UdpAdhocMedium"/>); null =
    /// off (the default: ad hoc finds no one, as a Vita alone).</summary>
    public AdhocMedium? Adhoc { get; set; }

    /// <summary>Where the controllers' vibration goes (the host's gamepads); null = dropped.</summary>
    public IControllerVibration? Vibration { get; set; }

    /// <summary>The host keyboard as a paired Bluetooth keyboard of the Vita (SceHid; the emulator option, off by
    /// default); null = no keyboard paired. Setting it connects or disconnects the device
    /// (<see cref="HidDevicesChanged"/>).</summary>
    public HidKeyboard? HidKeyboard
    {
        get => _hidKeyboard;
        set
        {
            if (ReferenceEquals(_hidKeyboard, value))
            {
                return;
            }

            _hidKeyboard = value;
            HidDevicesChanged?.Invoke();
        }
    }

    /// <summary>The host mouse as a paired Bluetooth mouse of the Vita (SceHid); null = no mouse paired.</summary>
    public HidMouse? HidMouse
    {
        get => _hidMouse;
        set
        {
            if (ReferenceEquals(_hidMouse, value))
            {
                return;
            }

            _hidMouse = value;
            HidDevicesChanged?.Invoke();
        }
    }

    private HidKeyboard? _hidKeyboard;
    private HidMouse? _hidMouse;

    /// <summary>A paired keyboard or mouse was connected or disconnected (SceHid's enumeration hint).</summary>
    public event Action? HidDevicesChanged;

    /// <summary>"Gravar tudo" is armed or recording without --capture-location: the position is withheld (the service
    /// answers "undetermined"), so a capture, which may go to a public report, never holds where the player is.</summary>
    public bool LocationWithheld { get; set; }

    /// <summary>The host capture of each process's microphone port; survives a process exec so the new process image
    /// closes what the old one left open (the host device must not stay on without a port).</summary>
    private readonly Dictionary<object, MicrophoneFeed> _microphoneFeeds = new();

    /// <summary>Makes <paramref name="feed"/> the capture of <paramref name="process"/>'s port (null = none), closing the
    /// one it replaces.</summary>
    public void ReplaceMicrophoneFeed(object process, MicrophoneFeed? feed)
    {
        MicrophoneFeed? previous;
        lock (_microphoneFeeds)
        {
            _microphoneFeeds.Remove(process, out previous);
            if (feed != null)
            {
                _microphoneFeeds[process] = feed;
            }
        }

        if (previous != null && !ReferenceEquals(previous, feed))
        {
            previous.Dispose();
        }
    }

    /// <summary>Microphone captures open now (tests; the host device is on while this is above 0).</summary>
    public int OpenMicrophoneFeeds
    {
        get
        {
            lock (_microphoneFeeds)
            {
                return _microphoneFeeds.Count;
            }
        }
    }

    public void Dispose()
    {
        List<MicrophoneFeed> feeds;
        lock (_microphoneFeeds)
        {
            feeds = _microphoneFeeds.Values.ToList();
            _microphoneFeeds.Clear();
        }

        foreach (MicrophoneFeed feed in feeds)
        {
            feed.Dispose();
        }

        Microphone?.Dispose();
        Location?.Dispose();
        EmmcImage?.Dispose();
        Adhoc?.Dispose();
        lock (CameraStreams)
        {
            foreach (ICameraStream cameraStream in CameraStreams)
            {
                cameraStream.Dispose();
            }

            CameraStreams.Clear();
        }
        Audio?.Dispose();
        Camera?.Dispose();
    }
}
