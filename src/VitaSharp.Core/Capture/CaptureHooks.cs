namespace VitaSharp.Core.Capture;

/// <summary>The HLE results a session capture records, and a re-run hands back (sceKernelGetRandomNumber).</summary>
public interface IRandomHook
{
    /// <summary>Re-run: fills <paramref name="dst"/> with the next recorded result and returns true, else false.</summary>
    bool Replay(Span<byte> dst);
    /// <summary>Recording: the bytes the guest got.</summary>
    void Recorded(ReadOnlySpan<byte> bytes);
}

/// <summary>The files of a title's own mounts (app0, savedata0, addcont0...) the guest reads: a recording keeps what
/// was read (<see cref="CaptureFileRecorder"/>), a re-run checks its reads against it (<see cref="CapturedFiles"/>).</summary>
public interface IFileHook
{
    /// <summary>A file of <paramref name="emu"/>'s file system was opened or its status read.</summary>
    void Opened(Emulator emu, string guestPath, string hostPath);
    /// <summary><paramref name="data"/> was read from <paramref name="guestPath"/> at <paramref name="offset"/>; the file
    /// is <paramref name="fileLength"/> bytes long.</summary>
    void Read(Emulator emu, string guestPath, long fileLength, long offset, ReadOnlySpan<byte> data);
    /// <summary>A folder was listed.</summary>
    void Listed(Emulator emu, string guestPath, string hostPath);
    /// <summary>A module file was loaded: its decrypted image.</summary>
    void ModuleLoaded(Emulator emu, string guestPath, byte[] elf);
    /// <summary>Re-run: the decrypted image of a module the recording loaded from that path, or null.</summary>
    byte[]? RecordedModule(Emulator emu, string guestPath);
}

/// <summary>Static hook points of the session capture in the HLE modules: null (the default) costs one read.</summary>
public static class CaptureHooks
{
    public static IRandomHook? Random;
    public static IFileHook? Files;
    public static IAudioInputHook? AudioInputs;
}
