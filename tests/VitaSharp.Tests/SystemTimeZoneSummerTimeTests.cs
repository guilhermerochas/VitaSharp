using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;

namespace VitaSharp.Tests;

/// <summary>
/// The Vita keeps the time zone as an id of its own list (/CONFIG/DATE/time_zone, a standard offset) and summer time
/// as a separate flag (/CONFIG/DATE/summer_time, one hour more); a fresh system takes both from the host. Summer time
/// is the host's offset now past its standard offset, and where the platform gives its own clock
/// (<see cref="SystemTimeZone.Platform"/>: Android's java.util.TimeZone) both come from it, not from the .NET runtime's
/// copy of the zone rules: on a phone whose runtime still had the rules of before 2019 São Paulo was in summer time
/// in November and the Vita clock showed an hour more than the phone (03:26 against 02:26).
/// </summary>
[Collection("guest")]
public sealed class SystemTimeZoneSummerTimeTests : IDisposable
{
    private const int SaoPaulo = 22, NewYork = 12;

    public void Dispose() => SystemTimeZone.Platform = null;

    [Fact]
    public void AZoneWithoutSummerTimeHasNone()
    {
        Assert.Equal(SaoPaulo, SystemTimeZone.FromZone("America/Sao_Paulo", -180));
        Assert.Equal(0, SystemTimeZone.SummerTime(-180, -180));
    }

    [Fact]
    public void AZoneInSummerTimeHasIt()
    {
        Assert.Equal(NewYork, SystemTimeZone.FromZone("America/New_York", -300));
        Assert.Equal(1, SystemTimeZone.SummerTime(-240, -300)); // July
        Assert.Equal(0, SystemTimeZone.SummerTime(-300, -300)); // January
    }

    [Fact]
    public void ThePlatformsClockDecidesForAZoneWithoutSummerTime()
    {
        // São Paulo today: UTC-3 all year
        SystemTimeZone.Platform = ("America/Sao_Paulo", _ => -180);

        Assert.Equal(SaoPaulo, SystemTimeZone.Host);
        Assert.Equal(0, SystemTimeZone.HostSummerTime);
        Assert.Equal(-180, AppUtil.SystemParamInt(6));
        Assert.Equal(0, AppUtil.SystemParamInt(7));
    }

    [Fact]
    public void ThePlatformsClockDecidesForAZoneInSummerTime()
    {
        // New York now in summer time (UTC-4), UTC-5 half a year before and after
        DateTime now = DateTime.UtcNow;
        SystemTimeZone.Platform = ("America/New_York", utc => Math.Abs((utc - now).TotalDays) < 30 ? -240 : -300);

        Assert.Equal(NewYork, SystemTimeZone.Host);
        Assert.Equal(1, SystemTimeZone.HostSummerTime);
        Assert.Equal(-300, AppUtil.SystemParamInt(6));
        Assert.Equal(1, AppUtil.SystemParamInt(7));
    }
}
