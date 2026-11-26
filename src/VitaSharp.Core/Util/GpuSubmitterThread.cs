namespace VitaSharp.Core.Util;

/// <summary>
/// The host thread recording the guest's GXM scenes: the one that called <c>sceGxmBeginScene</c> last. A game's job
/// system may record each frame's draws on a different job thread (Killzone Mercenary: the main thread submits a render
/// job and polls until the job thread that ran it clears its flag), so the thread the frame waits for moves. The
/// Android app listens to <see cref="Changed"/> to move that thread to the fast cores at once
/// (<c>VITASHARP_ANDROID_GXM_THREAD=big</c>, Mobile.Core Performance/ThreadPlacement.cs). With no listener a scene
/// costs one field read.
/// </summary>
public static class GpuSubmitterThread
{
    private static int s_current;

    /// <summary>Raised on the guest thread that began a scene when it is not the one that began the previous scene:
    /// (new host thread id, previous host thread id or 0).</summary>
    public static Action<int, int>? Changed;

    /// <summary>The host thread id of the last scene's recorder (0: none yet).</summary>
    public static int Current => Volatile.Read(ref s_current);

    /// <summary>A scene began on the host thread <paramref name="hostTid"/>.</summary>
    public static void SceneBegan(int hostTid)
    {
        Action<int, int>? changed = Changed;
        if (changed == null || hostTid == 0)
        {
            return;
        }

        int previous = Interlocked.Exchange(ref s_current, hostTid);
        if (previous != hostTid)
        {
            changed(hostTid, previous);
        }
    }

    /// <summary>Forgets the recorder (a new session, tests).</summary>
    public static void Reset()
    {
        Volatile.Write(ref s_current, 0);
    }
}
