using System.Diagnostics;

namespace VitaSharp.Core.Util;

public static partial class GuestClock
{
    /// <summary>Save states: the guest time in microseconds (host tick rates differ between devices).</summary>
    internal static long SnapshotMicroseconds => ToMicroseconds(Math.Max(Ticks, Volatile.Read(ref _lastObserved)));

    /// <summary>Save state load, while the emulation is paused: the frozen clock reads the saved time and resumes from
    /// it, so guest time continues from where the state was saved (also the observed-time floor).</summary>
    internal static void RestoreMicroseconds(long us)
    {
        if (!Frozen) throw new InvalidOperationException("the guest clock is restored only while the emulation is paused");
        long ticks = (long)(us * (Stopwatch.Frequency / 1_000_000.0));
        Volatile.Write(ref _frozen, ticks);
        Volatile.Write(ref _lastObserved, ticks);
    }
}
