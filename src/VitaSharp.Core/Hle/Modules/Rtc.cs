using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceRtc: ticks are microseconds since 0001-01-01 00:00:00 UTC.</summary>
public sealed class Rtc : HleModule
{
    const ulong UnixEpochTick = 62135596800000000UL;

    // wall-clock date at boot plus guest elapsed time, so RTC deltas agree with the other guest clocks
    static readonly ulong BootTick = (ulong)(DateTime.UtcNow.Ticks / 10) - (ulong)Util.GuestClock.Microseconds;
    static ulong NowTick => BootTick + (ulong)Util.GuestClock.Microseconds;

    static DateTime FromTick(ulong tick) => new DateTime((long)Math.Min(tick * 10, (ulong)DateTime.MaxValue.Ticks), DateTimeKind.Utc);

    internal static void WriteDateTime(uint p, DateTime t)
    {
        GuestMemory.Write(p, (ushort)t.Year);
        GuestMemory.Write(p + 2, (ushort)t.Month);
        GuestMemory.Write(p + 4, (ushort)t.Day);
        GuestMemory.Write(p + 6, (ushort)t.Hour);
        GuestMemory.Write(p + 8, (ushort)t.Minute);
        GuestMemory.Write(p + 10, (ushort)t.Second);
        GuestMemory.W32(p + 12, (uint)(t.Ticks % TimeSpan.TicksPerSecond / 10));
    }

    internal static DateTime ReadDateTime(uint p)
    {
        int y = GuestMemory.Read<ushort>(p), mo = GuestMemory.Read<ushort>(p + 2), d = GuestMemory.Read<ushort>(p + 4);
        int h = GuestMemory.Read<ushort>(p + 6), mi = GuestMemory.Read<ushort>(p + 8), s = GuestMemory.Read<ushort>(p + 10);
        uint us = GuestMemory.R32(p + 12);
        try { return new DateTime(Math.Max(1, y), Math.Clamp(mo, 1, 12), Math.Clamp(d, 1, 31), h % 24, mi % 60, s % 60, DateTimeKind.Utc).AddTicks(us * 10L); }
        catch { return DateTime.UnixEpoch; }
    }

    [Hle] int sceRtcGetCurrentTick(Ptr<ulong> tick) { tick.TrySet(NowTick); return 0; }
    [Hle] int sceRtcGetCurrentNetworkTick(Ptr<ulong> tick) { tick.TrySet(NowTick); return 0; }
    [Hle] uint sceRtcGetTickResolution() => 1_000_000;

    [Hle]
    int sceRtcGetCurrentClock(uint time, int tzMinutes)
    {
        WriteDateTime(time, DateTime.UtcNow.AddMinutes(tzMinutes));
        return 0;
    }

    [Hle]
    int sceRtcGetCurrentClockLocalTime(uint time)
    {
        WriteDateTime(time, DateTime.Now);
        return 0;
    }

    [Hle("sceRtcGetCurrentClockUtc")]
    int sceRtcGetCurrentClockUtc(uint time) { WriteDateTime(time, DateTime.UtcNow); return 0; }

    [Hle] int sceRtcGetDayOfWeek(int year, int month, int day)
    {
        try { return (int)new DateTime(year, month, day).DayOfWeek; } catch { return -1; }
    }

    [Hle] int sceRtcGetDaysInMonth(int year, int month) => DateTime.DaysInMonth(year, month);
    [Hle] int sceRtcIsLeapYear(int year) => DateTime.IsLeapYear(year) ? 1 : 0;

    [Hle]
    int sceRtcGetTick(uint time, Ptr<ulong> tick)
    {
        tick.TrySet((ulong)(ReadDateTime(time).Ticks / 10));
        return 0;
    }

    [Hle]
    int sceRtcSetTick(uint time, Ptr<ulong> tick)
    {
        WriteDateTime(time, FromTick(tick.Value));
        return 0;
    }

    [Hle]
    int sceRtcTickAddSeconds(Ptr<ulong> dst, Ptr<ulong> src, long seconds)
    {
        dst.Value = (ulong)((long)src.Value + seconds * 1_000_000);
        return 0;
    }

    [Hle] int sceRtcTickAddMicroseconds(Ptr<ulong> dst, Ptr<ulong> src, long us) { dst.Value = (ulong)((long)src.Value + us); return 0; }
    [Hle] int sceRtcTickAddMinutes(Ptr<ulong> dst, Ptr<ulong> src, long m) { dst.Value = (ulong)((long)src.Value + m * 60_000_000); return 0; }
    [Hle] int sceRtcTickAddHours(Ptr<ulong> dst, Ptr<ulong> src, int h) { dst.Value = (ulong)((long)src.Value + h * 3_600_000_000L); return 0; }
    [Hle] int sceRtcTickAddDays(Ptr<ulong> dst, Ptr<ulong> src, int d) { dst.Value = (ulong)((long)src.Value + d * 86_400_000_000L); return 0; }
    [Hle] int sceRtcCompareTick(Ptr<ulong> a, Ptr<ulong> b) => a.Value.CompareTo(b.Value);

    [Hle] int sceRtcGetTime_t(uint time, Ptr<uint> t) { t.TrySet((uint)((ReadDateTime(time).Ticks / 10 - (long)UnixEpochTick) / 1_000_000)); return 0; }
    [Hle] int sceRtcGetTime64_t(uint time, Ptr<long> t) { t.TrySet((ReadDateTime(time).Ticks / 10 - (long)UnixEpochTick) / 1_000_000); return 0; }
    [Hle] int sceRtcSetTime_t(uint time, uint t) { WriteDateTime(time, FromTick(UnixEpochTick + t * 1_000_000UL)); return 0; }
    [Hle] int sceRtcSetTime64_t(uint time, ulong t) { WriteDateTime(time, FromTick(UnixEpochTick + t * 1_000_000UL)); return 0; }

    [Hle]
    int sceRtcGetWin32FileTime(uint time, Ptr<ulong> ft)
    {
        ft.TrySet((ulong)ReadDateTime(time).ToFileTimeUtc());
        return 0;
    }

    [Hle]
    int sceRtcSetWin32FileTime(uint time, ulong ft)
    {
        WriteDateTime(time, DateTime.FromFileTimeUtc((long)ft));
        return 0;
    }

    [Hle]
    int sceRtcConvertUtcToLocalTime(Ptr<ulong> utc, Ptr<ulong> local)
    {
        var d = FromTick(utc.Value).ToLocalTime();
        local.Value = (ulong)(d.Ticks / 10);
        return 0;
    }

    [Hle]
    int sceRtcConvertLocalTimeToUtc(Ptr<ulong> local, Ptr<ulong> utc)
    {
        var d = DateTime.SpecifyKind(FromTick(local.Value), DateTimeKind.Local).ToUniversalTime();
        utc.Value = (ulong)(d.Ticks / 10);
        return 0;
    }

    [Hle]
    int sceRtcFormatRFC3339(uint buf, Ptr<ulong> tick, int tzMinutes)
    {
        var d = FromTick(tick.IsNull ? NowTick : tick.Value).AddMinutes(tzMinutes);
        string tz = tzMinutes == 0 ? "Z" : $"{(tzMinutes < 0 ? '-' : '+')}{Math.Abs(tzMinutes) / 60:00}:{Math.Abs(tzMinutes) % 60:00}";
        GuestMemory.WriteCString(buf, d.ToString("yyyy-MM-dd'T'HH:mm:ss.ff") + tz);
        return 0;
    }

    [Hle]
    int sceRtcFormatRFC3339LocalTime(uint buf, Ptr<ulong> tick) =>
        sceRtcFormatRFC3339(buf, tick, (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes);

    [Hle]
    int sceRtcFormatRFC2822(uint buf, Ptr<ulong> tick, int tzMinutes)
    {
        var d = FromTick(tick.IsNull ? NowTick : tick.Value).AddMinutes(tzMinutes);
        GuestMemory.WriteCString(buf, d.ToString("ddd, dd MMM yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + $" {(tzMinutes < 0 ? '-' : '+')}{Math.Abs(tzMinutes) / 60:00}{Math.Abs(tzMinutes) % 60:00}");
        return 0;
    }

    [Hle]
    int sceRtcFormatRFC2822LocalTime(uint buf, Ptr<ulong> tick) =>
        sceRtcFormatRFC2822(buf, tick, (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes);

    [Hle]
    int sceRtcParseRFC3339(Ptr<ulong> tick, string s)
    {
        if (!DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d))
            return unchecked((int)0x80251000);
        tick.Value = (ulong)(d.UtcDateTime.Ticks / 10);
        return 0;
    }

    [Hle]
    int sceRtcParseDateTime(Ptr<ulong> tick, string s) => sceRtcParseRFC3339(tick, s);

    [Hle] int sceRtcCheckValid(uint time) { try { _ = new DateTime(GuestMemory.Read<ushort>(time), GuestMemory.Read<ushort>(time + 2), GuestMemory.Read<ushort>(time + 4)); return 0; } catch { return unchecked((int)0x80251002); } }
}
