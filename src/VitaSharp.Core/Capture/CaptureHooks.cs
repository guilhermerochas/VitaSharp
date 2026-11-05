namespace VitaSharp.Core.Capture;

/// <summary>The HLE results a session capture records, and a re-run hands back (sceKernelGetRandomNumber).</summary>
public interface IRandomHook
{
    /// <summary>Re-run: fills <paramref name="dst"/> with the next recorded result and returns true, else false.</summary>
    bool Replay(Span<byte> dst);
    /// <summary>Recording: the bytes the guest got.</summary>
    void Recorded(ReadOnlySpan<byte> bytes);
}

/// <summary>Static hook points of the session capture in the HLE modules: null (the default) costs one read.</summary>
public static class CaptureHooks
{
    public static IRandomHook? Random;
}
