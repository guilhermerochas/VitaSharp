using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceRtc: ticks are microseconds since 0001-01-01 00:00:00 UTC.</summary>
public sealed class Rtc : HleModule
{
    const ulong UnixEpochTick = 62135596800000000UL;

    // wall-clock date at boot plus guest elapsed time, so RTC deltas agree with the other guest clocks
    static readonly ulong BootTick = (ulong)(DateTime.UtcNow.Ticks / 10) - (ulong)Util.GuestClock.Microseconds;
    internal static ulong NowTick => BootTick + (ulong)Util.GuestClock.ToMicroseconds(Util.GuestClock.Observed(Kernel.GuestThread.Current?.Core.EmulatedPosition() ?? 0)); // as the calling thread sees it

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

    // ------------------------------------------------------------------ calendar helpers (psp2common/kernel/rtc.h errors)

    const int RTC_INVALID_VALUE = unchecked((int)0x80251000), RTC_INVALID_POINTER = unchecked((int)0x80251001),
        RTC_INVALID_YEAR = unchecked((int)0x80251081), RTC_INVALID_MONTH = unchecked((int)0x80251082), RTC_INVALID_DAY = unchecked((int)0x80251083);

    /// <summary>Day of the year, 1-based (1 January = 1).</summary>
    [Hle]
    int sceRtcGetDayOfYear(int year, int month, int day)
    {
        if (year < 1 || year > 9999) return RTC_INVALID_YEAR;
        if (month is < 1 or > 12) return RTC_INVALID_MONTH;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return RTC_INVALID_DAY;
        return new DateTime(year, month, day).DayOfYear;
    }

    /// <summary>FAT date/time: years since 1980 (7 bits), month, day, hour, minute, seconds / 2.</summary>
    [Hle]
    int sceRtcGetDosTime(uint time, Ptr<uint> dos)
    {
        if (time == 0 || dos.IsNull) return RTC_INVALID_POINTER;
        int y = GuestMemory.Read<ushort>(time), mo = GuestMemory.Read<ushort>(time + 2), d = GuestMemory.Read<ushort>(time + 4);
        int h = GuestMemory.Read<ushort>(time + 6), mi = GuestMemory.Read<ushort>(time + 8), s = GuestMemory.Read<ushort>(time + 10);
        if (y < 1980) { dos.Value = 0; return RTC_INVALID_VALUE; }
        if (y >= 2108) { dos.Value = 0xFF9FBF7D; return RTC_INVALID_VALUE; }
        dos.Value = (uint)(((y - 1980) & 0x7F) << 25 | (mo & 0xF) << 21 | (d & 0x1F) << 16 | (h & 0x1F) << 11 | (mi & 0x3F) << 5 | ((s >> 1) & 0x1F));
        return 0;
    }

    [Hle]
    int sceRtcSetDosTime(uint time, uint dos)
    {
        if (time == 0) return RTC_INVALID_POINTER;
        GuestMemory.Write(time, (ushort)(1980 + (dos >> 25)));
        GuestMemory.Write(time + 2, (ushort)((dos >> 21) & 0xF));
        GuestMemory.Write(time + 4, (ushort)((dos >> 16) & 0x1F));
        GuestMemory.Write(time + 6, (ushort)((dos >> 11) & 0x1F));
        GuestMemory.Write(time + 8, (ushort)((dos >> 5) & 0x3F));
        GuestMemory.Write(time + 10, (ushort)((dos & 0x1F) * 2));
        GuestMemory.W32(time + 12, 0);
        return 0;
    }

    [Hle]
    int sceRtcTickAddTicks(Ptr<ulong> dst, Ptr<ulong> src, long add)
    {
        if (dst.IsNull || src.IsNull) return RTC_INVALID_POINTER;
        dst.Value = (ulong)((long)src.Value + add);
        return 0;
    }

    [Hle]
    int sceRtcTickAddWeeks(Ptr<ulong> dst, Ptr<ulong> src, int weeks)
    {
        if (dst.IsNull || src.IsNull) return RTC_INVALID_POINTER;
        dst.Value = (ulong)((long)src.Value + weeks * 604_800_000_000L);
        return 0;
    }

    /// <summary>Adds calendar months/years, clamping the day to the end of the resulting month (31/01 + 1 month = 28 or 29/02).</summary>
    int AddCalendar(Ptr<ulong> dst, Ptr<ulong> src, int months)
    {
        if (dst.IsNull || src.IsNull) return RTC_INVALID_POINTER;
        var d = FromTick(src.Value);
        long targetMonth = (long)d.Year * 12 + (d.Month - 1) + months;
        if (targetMonth < 12 || targetMonth >= 10000L * 12) return RTC_INVALID_YEAR;
        dst.Value = (ulong)(d.AddMonths(months).Ticks / 10);
        return 0;
    }

    [Hle] int sceRtcTickAddMonths(Ptr<ulong> dst, Ptr<ulong> src, int months) => AddCalendar(dst, src, months);
    [Hle] int sceRtcTickAddYears(Ptr<ulong> dst, Ptr<ulong> src, int years) => AddCalendar(dst, src, years * 12);

    /// <summary>Ad hoc, debug, GPS and retained network clocks: no such sources here, they follow the network tick.</summary>
    [Hle("sceRtcGetCurrentAdNetworkTick")]
    [Hle("sceRtcGetCurrentDebugNetworkTick")]
    [Hle("sceRtcGetCurrentGpsTick")]
    [Hle("sceRtcGetCurrentRetainedNetworkTick")]
    int sceRtcGetCurrentOtherNetworkTick(Ptr<ulong> tick)
    {
        if (tick.IsNull) return RTC_INVALID_POINTER;
        tick.Value = NowTick;
        return 0;
    }

    /// <summary>Last user clock adjustment / RTC reset: reported as the emulator's boot time.</summary>
    [Hle("sceRtcGetLastAdjustedTick")]
    [Hle("sceRtcGetLastReincarnatedTick")]
    int sceRtcGetLastAdjustedTick(Ptr<ulong> tick)
    {
        if (tick.IsNull) return RTC_INVALID_POINTER;
        tick.Value = BootTick;
        return 0;
    }

    /// <summary>Monotonic accumulated time; follows the current tick (as Vita3K).</summary>
    [Hle] ulong sceRtcGetAccumulativeTime() => NowTick;
}
