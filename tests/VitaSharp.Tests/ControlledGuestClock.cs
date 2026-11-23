using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// Guest time decided by the test: the guest clock stops when this is made and moves only by <see cref="Advance"/>, so a
/// wait's timeout passes exactly when the test says, never because the host was slow to run a step (a 50 ms timeout of
/// a clock that follows the host ran out on a loaded machine before the test reached its signal). Disposing it starts
/// the clock again from where it stopped. Tests in the "guest" collection run one at a time, so the shared clock is
/// theirs while it is stopped.
/// </summary>
public sealed class ControlledGuestClock : IDisposable
{
    private ControlledGuestClock()
    {
        GuestClock.Freeze();
    }

    public static ControlledGuestClock Stop() => new();

    /// <summary>Moves guest time forward by <paramref name="microseconds"/>.</summary>
    public void Advance(long microseconds) => GuestClock.AdvanceFrozen(microseconds);

    /// <summary>
    /// Moves guest time to at least the guest microsecond <paramref name="microseconds"/> for every reader. A guest
    /// time read (process time, <see cref="GuestClock.Observed"/>) is never behind what any thread already read, and a
    /// paced guest thread reads its own CPU position, which runs up to its pacing window ahead of the stopped clock:
    /// advancing the stopped clock by "target minus the time read" leaves it that much short of the target, and the
    /// readers stay at the floor that thread put ahead, short of the target too (a kernel alarm due at the target never
    /// fired after a load, the restored title's thread having read the clock ahead). The stopped clock itself is
    /// moved there.
    /// </summary>
    public void AdvanceTo(ulong microseconds)
    {
        long stopped = GuestClock.ToMicroseconds(GuestClock.Ticks);
        if ((long)microseconds >= stopped)
        {
            // +1: the clock's microseconds are its ticks rounded down
            Advance((long)microseconds - stopped + 1);
        }
    }

    public void Dispose() => GuestClock.Thaw();
}
