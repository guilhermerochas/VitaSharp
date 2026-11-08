using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceLibc time.h: time_t is 32 bits (seconds since 1970, as sceKernelLibcTime returns), clock() is
// sceKernelLibcClock (microseconds), struct tm is the C layout (9 ints). gmtime/localtime/asctime/ctime keep one
// result per thread as libc.suprx does (its TLS block); asctime is strftime(buf, 26, "%c\n") in the C locale.
public sealed partial class Libc
{
    [Hle]
    uint time(uint t)
    {
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (t != 0) GuestMemory.W32(t, now);
        return now;
    }

    [Hle] uint clock() => (uint)K.ProcessTimeUs;

    /// <summary>difftime(t1, t0) as libc.suprx computes it: both biased by 0x7C558180, the unsigned difference.</summary>
    [Hle]
    double difftime(uint t1, uint t0)
    {
        uint a = t0 + 0x7C558180, b = t1 + 0x7C558180;
        return a > b ? -(double)(a - b) : b - a;
    }

    static DateTime FromTime(uint t) => DateTimeOffset.FromUnixTimeSeconds((int)t).UtcDateTime;

    static void WriteTm(uint p, DateTime d, bool dst)
    {
        GuestMemory.W32(p + 0, (uint)d.Second);
        GuestMemory.W32(p + 4, (uint)d.Minute);
        GuestMemory.W32(p + 8, (uint)d.Hour);
        GuestMemory.W32(p + 12, (uint)d.Day);
        GuestMemory.W32(p + 16, (uint)(d.Month - 1));
        GuestMemory.W32(p + 20, (uint)(d.Year - 1900));
        GuestMemory.W32(p + 24, (uint)d.DayOfWeek);
        GuestMemory.W32(p + 28, (uint)(d.DayOfYear - 1));
        GuestMemory.W32(p + 32, dst ? 1u : 0u);
    }

    readonly Dictionary<int, uint> _threadTm = new(), _threadAsc = new();

    uint PerThread(Dictionary<int, uint> d, uint size)
    {
        int tid = GuestThread.Current?.Uid ?? 0;
        lock (d)
        {
            if (!d.TryGetValue(tid, out var p)) d[tid] = p = StaticAlloc(size);
            return p;
        }
    }

    uint GmtimeTo(uint t, uint tm) { WriteTm(tm, FromTime(GuestMemory.R32(t)), false); return tm; }

    uint LocaltimeTo(uint t, uint tm)
    {
        var d = FromTime(GuestMemory.R32(t)).ToLocalTime();
        WriteTm(tm, d, TimeZoneInfo.Local.IsDaylightSavingTime(d));
        return tm;
    }

    [Hle] uint gmtime(uint t) => t == 0 ? 0 : GmtimeTo(t, PerThread(_threadTm, 36));
    [Hle] uint localtime(uint t) => t == 0 ? 0 : LocaltimeTo(t, PerThread(_threadTm, 36));

    /// <summary>gmtime_s(time, result) (C11 Annex K order).</summary>
    [Hle]
    uint gmtime_s(uint t, uint result)
    {
        if (t == 0 || result == 0) { Constraint("gmtime_s: bad argument"); return 0; }
        return GmtimeTo(t, result);
    }

    [Hle]
    uint localtime_s(uint t, uint result)
    {
        if (t == 0 || result == 0) { Constraint("localtime_s: bad argument"); return 0; }
        return LocaltimeTo(t, result);
    }

    /// <summary>mktime(tm): normalizes the fields (local time) and returns the time, -1 when not representable.</summary>
    [Hle]
    int mktime(uint tm)
    {
        try
        {
            int I(uint o) => (int)GuestMemory.R32(tm + o);
            var d = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Local).AddYears(I(20)).AddMonths(I(16)).AddDays(I(12) - 1)
                .AddHours(I(8)).AddMinutes(I(4)).AddSeconds(I(0));
            if (I(32) > 0 && !TimeZoneInfo.Local.IsDaylightSavingTime(d)) d = d.AddHours(-1);
            long s = new DateTimeOffset(d).ToUnixTimeSeconds();
            if (s is < int.MinValue or > uint.MaxValue) return -1;
            WriteTm(tm, d, TimeZoneInfo.Local.IsDaylightSavingTime(d));
            return (int)s;
        }
        catch (ArgumentOutOfRangeException) { return -1; }
    }

    // ------------------------------------------------------------------ strftime (C locale)

    static readonly string[] Days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    static readonly string[] Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    sealed record Tm(int Sec, int Min, int Hour, int Mday, int Mon, int Year, int Wday, int Yday, int Isdst);

    static Tm ReadTm(uint p)
    {
        int I(uint o) => (int)GuestMemory.R32(p + o);
        return new Tm(I(0), I(4), I(8), I(12), I(16), I(20), I(24), I(28), I(32));
    }

    static int IsoWeek(Tm t, out int isoYear)
    {
        try
        {
            var d = new DateTime(Math.Clamp(t.Year + 1900, 1, 9999), 1, 1).AddDays(Math.Clamp(t.Yday, 0, 365));
            isoYear = System.Globalization.ISOWeek.GetYear(d);
            return System.Globalization.ISOWeek.GetWeekOfYear(d);
        }
        catch (ArgumentOutOfRangeException) { isoYear = t.Year + 1900; return 1; }
    }

    static string StrfTime(string fmt, Tm t)
    {
        var sb = new StringBuilder();
        string D2(int v) => v.ToString("00");
        string Day3() => t.Wday is >= 0 and < 7 ? Days[t.Wday][..3] : "?";
        string Mon3() => t.Mon is >= 0 and < 12 ? Months[t.Mon][..3] : "?";
        for (int i = 0; i < fmt.Length; i++)
        {
            char c = fmt[i];
            if (c != '%' || i + 1 >= fmt.Length) { sb.Append(c); continue; }
            c = fmt[++i];
            if (c is 'E' or 'O' && i + 1 < fmt.Length) c = fmt[++i]; // C locale: the alternative forms are the plain ones
            int year = t.Year + 1900;
            switch (c)
            {
                case 'a': sb.Append(Day3()); break;
                case 'A': sb.Append(t.Wday is >= 0 and < 7 ? Days[t.Wday] : "?"); break;
                case 'b': case 'h': sb.Append(Mon3()); break;
                case 'B': sb.Append(t.Mon is >= 0 and < 12 ? Months[t.Mon] : "?"); break;
                case 'c': sb.Append($"{Day3()} {Mon3()} {t.Mday,2} {D2(t.Hour)}:{D2(t.Min)}:{D2(t.Sec)} {year}"); break;
                case 'C': sb.Append(D2(year / 100)); break;
                case 'd': sb.Append(D2(t.Mday)); break;
                case 'D': sb.Append($"{D2(t.Mon + 1)}/{D2(t.Mday)}/{D2(year % 100)}"); break;
                case 'e': sb.Append($"{t.Mday,2}"); break;
                case 'F': sb.Append($"{year}-{D2(t.Mon + 1)}-{D2(t.Mday)}"); break;
                case 'g': { IsoWeek(t, out int iy); sb.Append(D2(iy % 100)); break; }
                case 'G': { IsoWeek(t, out int iy); sb.Append(iy); break; }
                case 'H': sb.Append(D2(t.Hour)); break;
                case 'I': sb.Append(D2(t.Hour % 12 == 0 ? 12 : t.Hour % 12)); break;
                case 'j': sb.Append((t.Yday + 1).ToString("000")); break;
                case 'm': sb.Append(D2(t.Mon + 1)); break;
                case 'M': sb.Append(D2(t.Min)); break;
                case 'n': sb.Append('\n'); break;
                case 'p': sb.Append(t.Hour < 12 ? "AM" : "PM"); break;
                case 'r': sb.Append($"{D2(t.Hour % 12 == 0 ? 12 : t.Hour % 12)}:{D2(t.Min)}:{D2(t.Sec)} {(t.Hour < 12 ? "AM" : "PM")}"); break;
                case 'R': sb.Append($"{D2(t.Hour)}:{D2(t.Min)}"); break;
                case 'S': sb.Append(D2(t.Sec)); break;
                case 't': sb.Append('\t'); break;
                case 'T': case 'X': sb.Append($"{D2(t.Hour)}:{D2(t.Min)}:{D2(t.Sec)}"); break;
                case 'u': sb.Append(t.Wday == 0 ? 7 : t.Wday); break;
                case 'U': sb.Append(D2((t.Yday + 7 - t.Wday) / 7)); break;
                case 'V': sb.Append(D2(IsoWeek(t, out _))); break;
                case 'w': sb.Append(t.Wday); break;
                case 'W': sb.Append(D2((t.Yday + 7 - (t.Wday + 6) % 7) / 7)); break;
                case 'x': sb.Append($"{D2(t.Mon + 1)}/{D2(t.Mday)}/{D2(year % 100)}"); break;
                case 'y': sb.Append(D2(year % 100)); break;
                case 'Y': sb.Append(year); break;
                case 'z': { var off = TimeZoneInfo.Local.BaseUtcOffset; if (t.Isdst > 0) off += TimeSpan.FromHours(1); sb.Append(off < TimeSpan.Zero ? '-' : '+').Append(Math.Abs(off.Hours).ToString("00")).Append(Math.Abs(off.Minutes).ToString("00")); break; }
                case 'Z': sb.Append(t.Isdst > 0 ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName); break;
                case '%': sb.Append('%'); break;
                default: sb.Append('%').Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>strftime: the formatted text if it fits with its NUL in max bytes, else 0 (contents undefined).</summary>
    [Hle]
    uint strftime(uint buf, uint max, string fmt, uint tm)
    {
        var b = Encoding.UTF8.GetBytes(StrfTime(fmt, ReadTm(tm)));
        if ((uint)b.Length + 1 > max) return 0;
        b.CopyTo(GuestMemory.Span(buf, b.Length));
        GuestMemory.Write(buf + (uint)b.Length, (byte)0);
        return (uint)b.Length;
    }

    [Hle]
    uint wcsftime(uint buf, uint max, uint fmt, uint tm)
    {
        string s = StrfTime(ReadWide(fmt), ReadTm(tm));
        if ((uint)s.Length + 1 > max) return 0;
        for (int i = 0; i < s.Length; i++) GuestMemory.Write(buf + (uint)i * 2, (ushort)s[i]);
        GuestMemory.Write(buf + (uint)s.Length * 2, (ushort)0);
        return (uint)s.Length;
    }

    [Hle]
    uint asctime(uint tm)
    {
        uint buf = PerThread(_threadAsc, 32);
        strftime(buf, 26, "%c\n", tm);
        return buf;
    }

    [Hle]
    uint ctime(uint t)
    {
        uint tm = localtime(t);
        return tm == 0 ? 0 : asctime(tm);
    }

    /// <summary>asctime_s(buf, size, tm): size at least 26 and fields in range, else a constraint violation.</summary>
    [Hle]
    int asctime_s(uint buf, uint size, uint tm)
    {
        if (buf == 0 || BadSize(size)) return Constraint("asctime_s: bad destination argument");
        if (size < 26 || tm == 0) { GuestMemory.Write(buf, (byte)0); return Constraint("asctime_s: bad argument"); }
        var t = ReadTm(tm);
        if (t.Wday is < 0 or > 6 || t.Mon is < 0 or > 11 || t.Mday is < 1 or > 31 || t.Hour is < 0 or > 23 || t.Min is < 0 or > 59
            || t.Sec is < 0 or > 60 || t.Year + 1900 is < 0 or > 9999)
        { GuestMemory.Write(buf, (byte)0); return Constraint("asctime_s: bad argument"); }
        strftime(buf, size, "%c\n", tm);
        return 0;
    }

    [Hle]
    int ctime_s(uint buf, uint size, uint t)
    {
        if (buf == 0 || BadSize(size)) return Constraint("ctime_s: bad destination argument");
        if (size < 26 || t == 0) { GuestMemory.Write(buf, (byte)0); return Constraint("ctime_s: bad argument"); }
        uint tm = PerThread(_threadTm, 36);
        LocaltimeTo(t, tm);
        return asctime_s(buf, size, tm);
    }
}
