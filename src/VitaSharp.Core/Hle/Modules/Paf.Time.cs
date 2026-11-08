using System.Text;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// paf::rtc (ticks in microseconds and SceDateTime-shaped dates over SceRtc) and paf::datetime::DateTime (the date
/// fields of SceDateTime at 0..0xF, a time-zone offset at +0x10, a "parse failed" flag at +0x14 and the parser's error
/// at +0x16; 0x18 bytes). A NULL tick or string is 0x80251000 (SCE_RTC_ERROR_INVALID_POINTER-like) as libpaf
/// returns it; a failed parse leaves libpaf's default date (2011-01-01, flag set).
/// </summary>
public sealed partial class Paf
{
    const int RtcNullArg = unchecked((int)0x80251000), RtcBadFormat = unchecked((int)0x80251080);

    /// <summary>The date libpaf's tick parser starts from when the string stops early (2010-01-01 00:00:00).</summary>
    static readonly (ushort Y, ushort Mo, ushort D) ParseBase = (2010, 1, 1);

    void WriteDate(uint p, ushort y, ushort mo, ushort d, ushort h, ushort mi, ushort s, uint us)
    {
        H(p, y); H(p + 2, mo); H(p + 4, d); H(p + 6, h); H(p + 8, mi); H(p + 0xA, s); U(p + 0xC, us);
    }

    // ---- paf::rtc
    [Hle("_ZN3paf3rtc10IsLeapYearEt")] int RtcIsLeapYear(int year) => R.sceRtcIsLeapYear((ushort)year);

    [Hle("_ZN3paf3rtc10IsLeapYearEPKy")]
    int RtcIsLeapYearTick(uint tick)
    {
        uint d = Scratch(0x10);
        RtcTickToDate(tick, d, 0);
        return R.sceRtcIsLeapYear(H(d));
    }

    [Hle("_ZN3paf3rtc14GetDaysInMonthEPKy")]
    int RtcDaysInMonth(uint tick)
    {
        uint d = Scratch(0x10);
        RtcTickToDate(tick, d, 0);
        return R.sceRtcGetDaysInMonth(H(d), H(d + 2));
    }

    uint _scratch;
    /// <summary>A small per-module work area for the dates the wrappers pass to SceRtc.</summary>
    uint Scratch(uint size) { if (_scratch == 0) _scratch = Static(0x100); GuestMemory.Fill(_scratch, 0, size); return _scratch; }

    [Hle("_ZN3paf3rtc14GetCurrentTickEPy")]
    int RtcCurrentTick(uint tick) { if (tick != 0) Q(tick, Rtc.TrophyServerTick(Emu) ?? Rtc.NowTick); return 0; }

    [Hle("_ZN3paf3rtc21GetCurrentNetworkTickEPy")]
    int RtcCurrentNetworkTick(uint tick) { if (tick != 0) Q(tick, Rtc.NowTick); return 0; }

    [Hle("_ZN3paf3rtc19GetCurrentLocalTickEPy")]
    int RtcCurrentLocalTick(uint tick)
    {
        if (tick == 0) return 0;
        uint t = Scratch(0x10);
        Q(t, Rtc.TrophyServerTick(Emu) ?? Rtc.NowTick);
        int r = R.sceRtcConvertUtcToLocalTime(new Ptr<ulong>(t), new Ptr<ulong>(t + 8));
        if (r == 0) Q(tick, Q(t + 8));
        return r;
    }

    [Hle("_ZN3paf3rtc21ConvertUtcToLocalTimeEPKyPy")]
    int RtcUtcToLocal(uint utc, uint local)
    {
        uint t = Scratch(0x10);
        Q(t, Q(utc));
        int r = R.sceRtcConvertUtcToLocalTime(new Ptr<ulong>(t), new Ptr<ulong>(t + 8));
        if (local != 0) Q(local, Q(t + 8));
        return r;
    }

    [Hle("_ZN3paf3rtc21ConvertLocalTimeToUtcEPKyPy")]
    int RtcLocalToUtc(uint local, uint utc)
    {
        uint t = Scratch(0x10);
        Q(t, Q(local));
        int r = R.sceRtcConvertLocalTimeToUtc(new Ptr<ulong>(t), new Ptr<ulong>(t + 8));
        if (utc != 0) Q(utc, Q(t + 8));
        return r;
    }

    /// <summary>(const DateTime*, Tick*): SceRtc's date to tick.</summary>
    [Hle("_ZN3paf3rtc21ConvertDateTimeToTickEPKNS0_8DateTimeEPy")]
    int RtcDateToTick(uint date, uint tick)
    {
        uint t = Scratch(0x18);
        GuestMemory.Copy(t, date, 0x10);
        int r = R.sceRtcGetTick(t, new Ptr<ulong>(t + 0x10));
        if (tick != 0) Q(tick, Q(t + 0x10));
        return r;
    }

    /// <summary>(const Tick*, DateTime*, DateTimeEx*): the date of the tick (2010-01-01 when SceRtc refuses it) and,
    /// when asked, the extras: day of the week, day of the year (from 0), the summer-time and 12/24-hour settings.</summary>
    [Hle("_ZN3paf3rtc21ConvertTickToDateTimeEPKyPNS0_8DateTimeEPNS0_10DateTimeExE")]
    int RtcTickToDate(uint tick, uint date, uint ex)
    {
        uint t = _scratch != 0 ? _scratch + 0x80 : Scratch(0) + 0x80;
        Q(t, Q(tick));
        int r = R.sceRtcSetTick(t + 8, new Ptr<ulong>(t));
        if (r == 0) GuestMemory.Copy(date, t + 8, 0x10);
        else WriteDate(date, ParseBase.Y, ParseBase.Mo, ParseBase.D, 0, 0, 0, 0);
        if (ex != 0 && r == 0)
        {
            GuestMemory.Fill(ex, 0, 8);
            int y = H(date), mo = H(date + 2), d = H(date + 4);
            H(ex, (ushort)R.sceRtcGetDayOfWeek(y, mo, d));
            int yday = d - 1;
            for (int m = 1; m < mo && m <= 12; m++) yday += DateTime.DaysInMonth(Math.Max(1, y), m);
            H(ex + 2, (ushort)yday);
            H(ex + 4, (ushort)RegInt(0x00760538)); // /CONFIG/DATE/summer_time
            H(ex + 6, (ushort)RegInt(0x00668503)); // /CONFIG/DATE/time_format
        }
        return r;
    }

    int RegInt(uint id)
    {
        if (Emu.Hle.Modules.OfType<RegMgrSystem>().FirstOrDefault() is not { } reg) return 0;
        uint v = Scratch(0) + 0xF0;
        return reg.sceRegMgrUtilityGetInt(id, new Ptr<int>(v)) < 0 ? 0 : (int)U(v);
    }

    /// <summary>The tick additions return 0 and wrap like libpaf's 64-bit arithmetic (no range check).</summary>
    [Hle("_ZN3paf3rtc19TickAddMicrosecondsEPyPKyx")] int RtcAddUs(uint dst, uint src, long n) { Q(dst, Q(src) + (ulong)n); return 0; }
    [Hle("_ZN3paf3rtc14TickAddSecondsEPyPKyx")] int RtcAddSeconds(uint dst, uint src, long n) { Q(dst, Q(src) + (ulong)(n * 1_000_000)); return 0; }
    [Hle("_ZN3paf3rtc14TickAddMinutesEPyPKyx")] int RtcAddMinutes(uint dst, uint src, long n) { Q(dst, Q(src) + (ulong)(n * 60_000_000)); return 0; }
    [Hle("_ZN3paf3rtc12TickAddHoursEPyPKyi")] int RtcAddHours(uint dst, uint src, int n) { Q(dst, Q(src) + (ulong)(n * 3_600_000_000L)); return 0; }
    [Hle("_ZN3paf3rtc11TickAddDaysEPyPKyi")] int RtcAddDays(uint dst, uint src, int n) { Q(dst, Q(src) + (ulong)(n * 86_400_000_000L)); return 0; }
    [Hle("_ZN3paf3rtc12TickAddWeeksEPyPKyi")] int RtcAddWeeks(uint dst, uint src, int n) { Q(dst, Q(src) + (ulong)(n * 604_800_000_000L)); return 0; }

    /// <summary>Calendar months: the day is kept, or cut to the last day of the new month.</summary>
    [Hle("_ZN3paf3rtc13TickAddMonthsEPyPKyi")]
    int RtcAddMonths(uint dst, uint src, int n)
    {
        uint t = Scratch(0x10);
        Q(t, Q(src));
        int r = R.sceRtcTickAddMonths(new Ptr<ulong>(t + 8), new Ptr<ulong>(t), n);
        if (r == 0) Q(dst, Q(t + 8));
        return r;
    }

    [Hle("_ZN3paf3rtc12TickAddYearsEPyPKyi")]
    int RtcAddYears(uint dst, uint src, int n)
    {
        uint t = Scratch(0x10);
        Q(t, Q(src));
        int r = R.sceRtcTickAddYears(new Ptr<ulong>(t + 8), new Ptr<ulong>(t), n);
        if (r == 0) Q(dst, Q(t + 8));
        return r;
    }

    [Hle("_ZN3paf3rtc12ParseRFC3339EPyPKc")]
    int RtcParseRfc3339(uint tick, uint s)
    {
        if (tick == 0 || s == 0) return RtcNullArg + 1;
        uint t = Scratch(8);
        int r = R.sceRtcParseRFC3339(new Ptr<ulong>(t), CRead(s));
        Q(tick, Q(t));
        return r;
    }

    /// <summary>(char *buf, const Tick *tick or NULL for now, int tz minutes).</summary>
    [Hle("_ZN3paf3rtc13FormatRFC3339EPcPKyi")]
    int RtcFormatRfc3339(uint buf, uint tick, int tz)
    {
        uint t = Scratch(8);
        Q(t, tick == 0 ? Rtc.TrophyServerTick(Emu) ?? Rtc.NowTick : Q(tick));
        return R.sceRtcFormatRFC3339(buf, new Ptr<ulong>(t), tz);
    }

    [Hle("_ZN3paf3rtc22FormatRFC3339LocalTimeEPcPKy")]
    int RtcFormatRfc3339Local(uint buf, uint tick)
    {
        uint t = Scratch(8);
        if (tick == 0)
        {
            Q(t + 0x10, Rtc.TrophyServerTick(Emu) ?? Rtc.NowTick);
            int r = R.sceRtcConvertUtcToLocalTime(new Ptr<ulong>(t + 0x10), new Ptr<ulong>(t));
            if (r != 0) return r;
        }
        else Q(t, Q(tick));
        return R.sceRtcFormatRFC3339LocalTime(buf, new Ptr<ulong>(t));
    }

    /// <summary>
    /// libpaf's SQLite date parser: "YYYY[-MM[-DD[( |T)HH[:MM[:SS[.fff]]]]]]". With a format pointer a shorter string
    /// is accepted (the missing fields stay at 2010-01-01 00:00:00) and the pointer receives how far it went (1 year
    /// .. 7 fractional seconds); without one the date part is required. Each bad field has its own error, 0x80251081
    /// (year) .. 0x80251087 (fraction), a missing separator 0x80251080; the result is checked like sceRtcCheckValid.
    /// </summary>
    internal static int ParseSqliteDate(string s, out (ushort Y, ushort Mo, ushort D, ushort H, ushort Mi, ushort S, uint Us) dt, out int format, bool partialOk)
    {
        dt = (ParseBase.Y, ParseBase.Mo, ParseBase.D, 0, 0, 0, 0);
        format = 0;
        if (s.Length < 10 && !partialOk) return RtcNullArg;
        static bool Digits(string s, int at, int n) { for (int i = at; i < at + n; i++) if (i >= s.Length || s[i] < '0' || s[i] > '9') return false; return true; }
        static int Num(string s, int at, int n) { int v = 0; for (int i = at; i < at + n; i++) v = v * 10 + s[i] - '0'; return v; }
        if (!Digits(s, 0, 4)) return RtcBadFormat + 1;
        dt.Y = (ushort)Num(s, 0, 4);
        if (s.Length <= 4 && partialOk) { format = 1; return CheckDate(dt) ? 0 : unchecked((int)0x80251002); }
        if (s.Length <= 4 || s[4] != '-') return RtcBadFormat;
        if (!Digits(s, 5, 2)) return RtcBadFormat + 2;
        dt.Mo = (ushort)Num(s, 5, 2);
        if (s.Length <= 7 && partialOk) { format = 2; return CheckDate(dt) ? 0 : unchecked((int)0x80251002); }
        if (s.Length <= 7 || s[7] != '-') return RtcBadFormat;
        if (!Digits(s, 8, 2)) return RtcBadFormat + 3;
        dt.D = (ushort)Num(s, 8, 2);
        if (s.Length <= 10) { format = 3; return CheckDate(dt) ? 0 : unchecked((int)0x80251002); }
        if (s[10] != ' ' && s[10] != 'T') return RtcBadFormat;
        if (!Digits(s, 11, 2)) return RtcBadFormat + 4;
        dt.H = (ushort)Num(s, 11, 2);
        if (s.Length <= 13 && partialOk) { format = 4; return CheckDate(dt) ? 0 : unchecked((int)0x80251002); }
        if (s.Length <= 13 || s[13] != ':') return RtcBadFormat;
        if (!Digits(s, 14, 2)) return RtcBadFormat + 5;
        dt.Mi = (ushort)Num(s, 14, 2);
        if (s.Length <= 16) { format = 5; return CheckDate(dt) ? 0 : unchecked((int)0x80251002); }
        if (s[16] != ':') return RtcBadFormat;
        if (!Digits(s, 17, 2)) return RtcBadFormat + 6;
        dt.S = (ushort)Num(s, 17, 2);
        if (s.Length <= 19 || s[19] != '.') { format = 6; return CheckDate(dt) ? 0 : unchecked((int)0x80251002); }
        int n = 0;
        uint us = 0;
        for (int i = 20; i < s.Length && s[i] >= '0' && s[i] <= '9'; i++, n++) if (n < 6) us = us * 10 + (uint)(s[i] - '0');
        if (n == 0) return RtcBadFormat + 7;
        for (int i = n; i < 6; i++) us *= 10;
        dt.Us = us;
        { format = 7; return CheckDate(dt) ? 0 : unchecked((int)0x80251002); }
    }

    static bool CheckDate((ushort Y, ushort Mo, ushort D, ushort H, ushort Mi, ushort S, uint Us) d) =>
        d.Y is >= 1 and <= 9999 && d.Mo is >= 1 and <= 12 && d.D >= 1 && d.D <= DateTime.DaysInMonth(d.Y, d.Mo)
        && d.H < 24 && d.Mi < 60 && d.S < 60 && d.Us < 1_000_000;

    /// <summary>"now" is the current tick; otherwise the parsed date as a tick (SceRtc's GetTick).</summary>
    [Hle("_ZN3paf3rtc19ParseSQLiteDateTimeEPyPKc")]
    int RtcParseSqlite(uint tick, uint s) => RtcParseSqliteF(tick, 0, s);

    [Hle("_ZN3paf3rtc19ParseSQLiteDateTimeEPyPNS0_24SQLiteDateTimeFormatTypeEPKc")]
    int RtcParseSqliteF(uint tick, uint fmt, uint s)
    {
        if (tick == 0 || s == 0) return RtcNullArg;
        string text = CRead(s);
        if (text == "now") { Q(tick, Rtc.TrophyServerTick(Emu) ?? Rtc.NowTick); if (fmt != 0) U(fmt, 7); return 0; }
        int r = ParseSqliteDate(text, out var d, out int f, fmt != 0);
        if (fmt != 0) U(fmt, (uint)f);
        if (r != 0) return r;
        uint t = Scratch(0x18);
        WriteDate(t, d.Y, d.Mo, d.D, d.H, d.Mi, d.S, d.Us);
        r = R.sceRtcGetTick(t, new Ptr<ulong>(t + 0x10));
        Q(tick, Q(t + 0x10));
        return r;
    }

    // ---- paf::datetime::DateTime
    void DtSet(uint p, ushort y, ushort mo, ushort d, ushort h, ushort mi, ushort s, uint us, int tz = 0)
    {
        WriteDate(p, y, mo, d, h, mi, s, us);
        U(p + 0x10, (uint)tz);
        B(p + 0x14, 0);
    }

    /// <summary>libpaf's "no date": 2011-01-01 00:00:00, parse-failed flag set, the parser's error kept.</summary>
    void DtFailed(uint p, int err)
    {
        WriteDate(p, 2011, 1, 1, 0, 0, 0, 0);
        U(p + 0x10, 0);
        B(p + 0x14, 1);
        H(p + 0x16, (ushort)err);
    }

    [Hle("_ZN3paf8datetime8DateTimeC1Ettttttj")]
    [Hle("_ZN3paf8datetime8DateTimeC2Ettttttj")]
    uint DtCtor(uint me, uint y, uint mo, uint d, uint h, uint mi, uint s, uint us)
    {
        DtSet(me, (ushort)y, (ushort)mo, (ushort)d, (ushort)h, (ushort)mi, (ushort)s, us);
        return me;
    }

    void DtFromClock(uint ret, bool local)
    {
        uint t = Scratch(0x10);
        if (local) R.sceRtcGetCurrentClockLocalTime(t); else R.sceRtcGetCurrentClock(t, 0);
        DtSet(ret, H(t), H(t + 2), H(t + 4), H(t + 6), H(t + 8), H(t + 0xA), U(t + 0xC));
    }

    [Hle("_ZN3paf8datetime8DateTime3UTCEv")] void DtUtc(uint ret) => DtFromClock(ret, false);
    [Hle("_ZN3paf8datetime8DateTime3NowEv", Nid = 0x96345146)] void DtNow(uint ret) => DtFromClock(ret, true);

    [Hle("_ZN3paf8datetime8DateTime10NetworkUTCEv")]
    void DtNetworkUtc(uint ret)
    {
        uint t = Scratch(0x20);
        Q(t, Rtc.NowTick);
        R.sceRtcSetTick(t + 8, new Ptr<ulong>(t));
        DtSet(ret, H(t + 8), H(t + 0xA), H(t + 0xC), H(t + 0xE), H(t + 0x10), H(t + 0x12), U(t + 0x14));
    }

    /// <summary>(DateTime &ret, Tick *tick).</summary>
    [Hle("_ZN3paf8datetime8DateTime24ConvertRtcTickToDateTimeEPy")]
    void DtFromTick(uint ret, uint tick)
    {
        uint d = Scratch(0) + 0x40;
        RtcTickToDate(tick, d, 0);
        DtSet(ret, H(d), H(d + 2), H(d + 4), H(d + 6), H(d + 8), H(d + 0xA), U(d + 0xC));
    }

    /// <summary>Returns the tick (a 64-bit value) of the date fields.</summary>
    [Hle("_ZN3paf8datetime8DateTime24ConvertDateTimeToRtcTickERS1_")]
    ulong DtToTick(uint dt)
    {
        uint t = Scratch(0x18);
        GuestMemory.Copy(t, dt, 0x10);
        R.sceRtcGetTick(t, new Ptr<ulong>(t + 0x10));
        return Q(t + 0x10);
    }

    /// <summary>Zeller's congruence with January and February as months 13 and 14: 0 = Sunday.</summary>
    [Hle("_ZN3paf8datetime8DateTime9DayOfWeekEttt")]
    int DtDayOfWeek(uint year, uint month, uint day)
    {
        int y = (ushort)year, m = (ushort)month, d = (ushort)day;
        if (m is 1 or 2) { y--; m += 12; }
        return (y + y / 4 - y / 100 + y / 400 + (13 * m + 8) / 5 + d) % 7;
    }

    [Hle("_ZN3paf8datetime8DateTime19ParseSQLiteDateTimeEPKc")]
    void DtParseSqlite(uint ret, uint s)
    {
        uint t = Scratch(8) + 0xC0;
        int r = RtcParseSqlite(t, s);
        if (r != 0) { DtFailed(ret, r); return; }
        DtFromTick(ret, t);
        H(ret + 0x16, 0);
    }

    /// <summary>An RFC 3339 string: the UTC date, and the offset the string carried (in minutes) at +0x10.</summary>
    [Hle("_ZN3paf8datetime8DateTime12ParseRFC3339EPKc")]
    void DtParseRfc3339(uint ret, uint s)
    {
        uint t = Scratch(8) + 0xC0;
        int r = RtcParseRfc3339(t, s);
        if (r != 0) { DtFailed(ret, r); return; }
        DtFromTick(ret, t);
        U(ret + 0x10, (uint)Rfc3339Offset(CRead(s)));
        H(ret + 0x16, 0);
    }

    static int Rfc3339Offset(string s)
    {
        int i = Math.Max(s.LastIndexOf('+'), s.LastIndexOf('-'));
        if (i < 10 || i + 6 > s.Length || s[i + 3] != ':') return 0;
        if (!int.TryParse(s.AsSpan(i + 1, 2), out int h) || !int.TryParse(s.AsSpan(i + 4, 2), out int m)) return 0;
        return (s[i] == '-' ? -1 : 1) * (h * 60 + m);
    }

    /// <summary>this + other: the other date's fields are counts of years, months, days, hours, minutes, seconds and
    /// microseconds added to this date in that order.</summary>
    [Hle("_ZNK3paf8datetime8DateTimeplERKS1_")]
    void DtAdd(uint ret, uint me, uint other)
    {
        uint t = Scratch(0x20) + 0x40;
        Q(t, DtToTick(me));
        var tick = new Ptr<ulong>(t);
        if (H(other) != 0) R.sceRtcTickAddYears(tick, tick, H(other));
        if (H(other + 2) != 0) R.sceRtcTickAddMonths(tick, tick, H(other + 2));
        ulong v = Q(t);
        v += H(other + 4) * 86_400_000_000UL + H(other + 6) * 3_600_000_000UL + H(other + 8) * 60_000_000UL + H(other + 0xA) * 1_000_000UL + U(other + 0xC);
        Q(t, v);
        DtFromTick(ret, t);
    }

    [Hle("_ZNK3paf8datetime8DateTimeltERKS1_")]
    bool DtLess(uint me, uint other) => DtToTick(me) < DtToTick(other);

    // the format strings of DateTime::Wcsftime: three static wide strings
    uint _dateFormat, _timeFormat, _dateTimeFormat;
    uint FormatString(ref uint field) { if (field == 0) { field = Static(12); StrInit(field, true); } return field; }

    [Hle("_ZN3paf8datetime8DateTime13GetDateFormatEv")] uint DtGetDateFormat() => FormatString(ref _dateFormat);
    [Hle("_ZN3paf8datetime8DateTime13GetTimeFormatEv")] uint DtGetTimeFormat() => FormatString(ref _timeFormat);
    [Hle("_ZN3paf8datetime8DateTime13SetDateFormatEPKw")] void DtSetDateFormat(uint f) { if (f != 0) WStrAssign(FormatString(ref _dateFormat), WRead(f)); }
    [Hle("_ZN3paf8datetime8DateTime13SetTimeFormatEPKw")] void DtSetTimeFormat(uint f) { if (f != 0) WStrAssign(FormatString(ref _timeFormat), WRead(f)); }
    [Hle("_ZN3paf8datetime8DateTime17SetDateTimeFormatEPKw")] void DtSetDateTimeFormat(uint f) { if (f != 0) WStrAssign(FormatString(ref _dateTimeFormat), WRead(f)); }
}
