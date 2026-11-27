using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// Guest threads run on Linux and Android at the nice of their guest priority (Android's display levels), above the
/// emulator's background work and the other apps, as a game's threads run above the system's on the Vita: at nice 0
/// a woken guest thread on the phone waited 2-4 ms for a core at p99 (TestGame wake_latency_sema).
/// </summary>
public sealed class GuestThreadNiceTests
{
    [Theory]
    [InlineData(64, -8)]
    [InlineData(96, -8)]
    [InlineData(127, -8)]
    [InlineData(128, -4)]
    [InlineData(160, -4)]
    [InlineData(191, -4)]
    public void GuestPriorityMapsToAndroidDisplayNiceLevels(int guestPriority, int nice)
    {
        Assert.Equal(nice, HostThreads.GuestNiceOf(guestPriority));
    }

    [Fact]
    public void NoNiceIsSetOutsideLinuxAndAndroid()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
        {
            return;
        }

        Assert.Null(HostThreads.ApplyGuestPriority(160));
    }
}
