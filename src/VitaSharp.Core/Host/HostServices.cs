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
    VolumeUp = 0x100000, VolumeDown = 0x200000, PowerButton = 0x4000000,
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
        var list = GetTouches(back);
        int n = Math.Min(list.Count, dest.Length);
        for (int i = 0; i < n; i++) dest[i] = list[i];
        return n;
    }
}

/// <summary>An audio output stream fed with interleaved signed 16-bit samples.</summary>
public interface IAudioStream : IDisposable
{
    /// <summary>Queues samples; blocks while the host buffer is full (this provides the guest's audio pacing).</summary>
    void Submit(ReadOnlySpan<short> interleaved);
    float Volume { get; set; }
    /// <summary>Sample frames queued but not yet played (sceAudioOutGetRestSample); games use it for A/V sync.</summary>
    int PendingSamples { get; }
}

public interface IAudioBackend : IDisposable
{
    IAudioStream OpenStream(int sampleRate, int channels, int grainSamples);
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

/// <summary>Host services the core needs from a front-end. Everything is optional (headless runs).</summary>
public sealed class HostServices : IDisposable
{
    public IInputBackend? Input { get; set; }
    public IAudioBackend? Audio { get; set; }
    /// <summary>Keyboard for the in-game IME (sceImeOpen); null = no host keyboard (scripted --ime-text only).</summary>
    public ITextInputBackend? TextInput { get; set; }
    /// <summary>Webcam / device camera backing SceCamera; null = emulated camera delivering grey frames.</summary>
    public ICameraSource? Camera { get; set; }

    /// <summary>Camera streams the guest has started; survives a process exec so the new process can release them.</summary>
    internal readonly List<ICameraStream> CameraStreams = new();

    public void Dispose()
    {
        lock (CameraStreams)
        {
            foreach (var s in CameraStreams) s.Dispose();
            CameraStreams.Clear();
        }
        Audio?.Dispose();
        Camera?.Dispose();
    }
}
