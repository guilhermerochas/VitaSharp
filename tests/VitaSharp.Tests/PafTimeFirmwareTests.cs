using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// paf::rtc and paf::datetime::DateTime (Paf.Time.cs) against libpaf.suprx: leap years and month lengths, dates to
/// ticks and back, the tick additions (with month ends), RFC 3339 and SQLite date parsing and formatting, and the
/// DateTime constructor, conversions, day of the week, parsing, addition and comparison. Every call's result and the
/// memory it wrote are logged; with the firmware module the same calls run through its exports and the logs must
/// match, without it the HLE log must hash to the firmware's (<see cref="FirmwareLogHash"/>) and a few values are
/// checked directly.
/// </summary>
[Collection("guest")]
public sealed unsafe class PafTimeFirmwareTests(GuestMemoryFixture fx, ITestOutputHelper output)
{
    /// <summary>The hash of the firmware's log (MatchesFirmwareModule prints it when it changes).</summary>
    private const string FirmwareLogHash = "172B6FD7BEE9FE4D";

    private static readonly Lazy<string?> s_firmwarePath = new(() =>
        JsonFirmware.ModulePath is { } json && Path.Combine(Path.GetDirectoryName(json)!, "libpaf.suprx") is var p && File.Exists(p) ? p : null);

    private sealed class Run : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-paftime-" + Guid.NewGuid().ToString("N"));
        public readonly Emulator Emu;
        private readonly Paf _paf;
        private readonly uint _arena;
        private uint _bump;
        private readonly bool _lle;
        public readonly List<string> Log = [];

        public Run(GuestMemoryFixture fx, string? module)
        {
            Directory.CreateDirectory(Path.Combine(_root, "vs0", "sys", "external"));
            if (module != null)
            {
                File.Copy(module, Path.Combine(_root, "vs0", "sys", "external", "libpaf.suprx"));
            }

            Emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
            if (module != null)
            {
                Emu.LoadModule("vs0:sys/external/libpaf.suprx");
            }

            _lle = module != null;
            _paf = Emu.Hle.Modules.OfType<Paf>().Single();
            _arena = Emu.Memory.Alloc(0x10000, "paf time test");
        }

        public void Dispose()
        {
            Emu.Dispose();
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException) { }
        }

        public uint Alloc(uint n)
        {
            uint p = _arena + _bump;
            _bump += (n + 15) & ~15u;
            GuestMemory.Fill(p, 0xCD, n);
            return p;
        }
        public uint Str(string s)
        {
            uint p = Alloc((uint)s.Length + 1);
            GuestMemory.WriteCString(p, s);
            return p;
        }
        public uint Tick(ulong v)
        {
            uint p = Alloc(8);
            GuestMemory.Write(p, v);
            return p;
        }
        public uint Date(ushort y, ushort mo, ushort d, ushort h = 0, ushort mi = 0, ushort s = 0, uint us = 0)
        {
            uint p = Alloc(0x18);
            GuestMemory.Write(p, y);
            GuestMemory.Write(p + 2, mo);
            GuestMemory.Write(p + 4, d);
            GuestMemory.Write(p + 6, h);
            GuestMemory.Write(p + 8, mi);
            GuestMemory.Write(p + 10, s);
            GuestMemory.Write(p + 12, us);
            return p;
        }

        private string Hex(uint p, uint n) => p == 0 ? "-" : Convert.ToHexString(GuestMemory.ReadSpan(p, (int)n));

        /// <summary>Calls <paramref name="fw"/> (registers <paramref name="regs"/>; a 64-bit result in r0:r1) or the HLE
        /// method <paramref name="hle"/> with <paramref name="args"/>, then logs the result and the outputs.</summary>
        public void Call(GuestThread t, string fw, string hle, uint[] regs, object[] args, params (uint P, uint N)[] outs)
        {
            ulong r;
            if (_lle)
            {
                uint nid = NidDatabase.LookupName(fw).First(n => n.Library.StartsWith("ScePaf", StringComparison.Ordinal)).Nid;
                Assert.True(Emu.Linker.TryGetFunctionExport(nid, out uint fn), fw);
                uint lo = t.CallGuest(fn, regs);
                r = lo | ((ulong)t.LastCallR1 << 32);
            }
            else
            {
                MethodInfo m = typeof(Paf).GetMethod(hle, BindingFlags.Instance | BindingFlags.NonPublic)!;
                object? v = m.Invoke(_paf, args);
                r = v switch { null => 0, bool b => b ? 1u : 0u, int i => (uint)i, uint u => u, ulong ul => ul, _ => throw new InvalidOperationException(hle) };
            }
            bool wide = typeof(Paf).GetMethod(hle, BindingFlags.Instance | BindingFlags.NonPublic)!.ReturnType == typeof(ulong);
            bool none = typeof(Paf).GetMethod(hle, BindingFlags.Instance | BindingFlags.NonPublic)!.ReturnType == typeof(void);
            uint r32 = (uint)r;
            string ret = none ? "" : wide ? $" = {r:X16}" : typeof(Paf).GetMethod(hle, BindingFlags.Instance | BindingFlags.NonPublic)!.ReturnType == typeof(bool) ? $" = {(r & 0xFF) != 0}"
                : r32 >= _arena && r32 < _arena + 0x10000 ? $" = @{r32 - _arena:X}" : $" = {r32:X8}";
            // an error leaves the outputs undefined (libpaf copies an uninitialised local)
            if (!none && !wide && r32 >= 0x80000000 && !(r32 >= _arena && r32 < _arena + 0x10000))
            {
                outs = [];
            }

            Log.Add($"{hle}({string.Join(",", args.Select(a => a is uint u && u >= _arena && u < _arena + 0x10000 ? $"@{u - _arena:X}" : a.ToString()))}){ret} {string.Join(" ", outs.Select(o => Hex(o.P, o.N)))}");
        }
    }

    private static readonly string[] s_rfc3339 = ["2020-02-29T12:34:56Z", "2020-02-29T12:34:56.5+09:00", "1999-12-31T23:59:59-05:30", "2020-13-01T00:00:00Z", "junk", ""];
    private static readonly string[] s_sqlite = ["2020-02-29 12:34:56", "2020-02-29", "2020-02-29 12:34", "2020-02-29T12:34:56.123", "12:34:56", "junk", ""];

    /// <summary>Every call, on a guest thread of <paramref name="r"/>.</summary>
    private static void Scenario(Run r)
    {
        GuestThread th = r.Emu.Kernel.CreateThread("paf-time-test", 0, 0x10000100, 0x10000, 0, 0x70000);
        Exception? failure = null;
        th.HostBody = t =>
        {
            try
            {
                void C(string fw, string hle, uint[] regs, object[] args, params (uint, uint)[] outs) => r.Call(t, fw, hle, regs, args, outs);
                foreach (uint y in new uint[] { 1900, 2000, 2020, 2023 })
                {
                    C("_ZN3paf3rtc10IsLeapYearEt", "RtcIsLeapYear", [y], [(int)y]);
                }

                const ulong Ticks2020 = 63718562096789000; // 2020-02-29 12:34:56.789 (SceRtc ticks: microseconds since year 1)
                ulong[] ticks = [Ticks2020, Ticks2020 - 59L * 86_400_000_000, 62135596800000000, 0];
                foreach (ulong k in ticks)
                {
                    uint p = r.Tick(k);
                    C("_ZN3paf3rtc10IsLeapYearEPKy", "RtcIsLeapYearTick", [p], [p]);
                    C("_ZN3paf3rtc14GetDaysInMonthEPKy", "RtcDaysInMonth", [p], [p]);
                    uint d = r.Alloc(0x10), ex = r.Alloc(8);
                    C("_ZN3paf3rtc21ConvertTickToDateTimeEPKyPNS0_8DateTimeEPNS0_10DateTimeExE", "RtcTickToDate", [p, d, 0], [p, d, 0u], (d, 0x10));
                    _ = ex;
                    uint dt = r.Alloc(0x18);
                    C("_ZN3paf8datetime8DateTime24ConvertRtcTickToDateTimeEPy", "DtFromTick", [dt, p], [dt, p], (dt, 0x15));
                    C("_ZN3paf8datetime8DateTime24ConvertDateTimeToRtcTickERS1_", "DtToTick", [dt], [dt]);
                }
                foreach (uint date in new[] { r.Date(2020, 2, 29, 12, 34, 56, 789000), r.Date(1, 1, 1), r.Date(2020, 13, 1), r.Date(2021, 2, 29) })
                {
                    uint tk = r.Alloc(8);
                    C("_ZN3paf3rtc21ConvertDateTimeToTickEPKNS0_8DateTimeEPy", "RtcDateToTick", [date, tk], [date, tk], (tk, 8));
                }
                foreach (long n in new long[] { 1, -7, 1_000_000 })
                {
                    uint src = r.Tick(Ticks2020), dst = r.Alloc(8);
                    foreach ((string fw, string hle) in new[] { ("_ZN3paf3rtc19TickAddMicrosecondsEPyPKyx", "RtcAddUs"), ("_ZN3paf3rtc14TickAddSecondsEPyPKyx", "RtcAddSeconds"), ("_ZN3paf3rtc14TickAddMinutesEPyPKyx", "RtcAddMinutes") })
                    {
                        C(fw, hle, [dst, src, (uint)n, (uint)(n >> 32)], [dst, src, n], (dst, 8));
                    }
                }
                foreach (int n in new[] { 1, -1, 12, -25 })
                {
                    uint src = r.Tick(Ticks2020 - 29L * 86_400_000_000), dst = r.Alloc(8); // 2020-01-31
                    foreach ((string fw, string hle) in new[] { ("_ZN3paf3rtc12TickAddHoursEPyPKyi", "RtcAddHours"), ("_ZN3paf3rtc11TickAddDaysEPyPKyi", "RtcAddDays"),
                                 ("_ZN3paf3rtc12TickAddWeeksEPyPKyi", "RtcAddWeeks"), ("_ZN3paf3rtc13TickAddMonthsEPyPKyi", "RtcAddMonths"), ("_ZN3paf3rtc12TickAddYearsEPyPKyi", "RtcAddYears") })
                    {
                        C(fw, hle, [dst, src, (uint)n], [dst, src, n], (dst, 8));
                    }
                }
                foreach (string s in s_rfc3339)
                {
                    uint tk = r.Alloc(8), str = r.Str(s), dt = r.Alloc(0x18);
                    C("_ZN3paf3rtc12ParseRFC3339EPyPKc", "RtcParseRfc3339", [tk, str], [tk, str], (tk, 8));
                    C("_ZN3paf8datetime8DateTime12ParseRFC3339EPKc", "DtParseRfc3339", [dt, str], [dt, str], (dt, 0x15));
                }
                uint none = r.Str("2020-02-29T12:34:56Z");
                C("_ZN3paf3rtc12ParseRFC3339EPyPKc", "RtcParseRfc3339", [0, none], [0u, none]);
                foreach (int tz in new[] { 0, 540, -330 })
                {
                    uint buf = r.Alloc(40), tk = r.Tick(Ticks2020);
                    C("_ZN3paf3rtc13FormatRFC3339EPcPKyi", "RtcFormatRfc3339", [buf, tk, (uint)tz], [buf, tk, tz], (buf, 40));
                }
                foreach (string s in s_sqlite)
                {
                    uint tk = r.Alloc(8), fmt = r.Alloc(4), str = r.Str(s), dt = r.Alloc(0x18);
                    C("_ZN3paf3rtc19ParseSQLiteDateTimeEPyPKc", "RtcParseSqlite", [tk, str], [tk, str], (tk, 8));
                    C("_ZN3paf3rtc19ParseSQLiteDateTimeEPyPNS0_24SQLiteDateTimeFormatTypeEPKc", "RtcParseSqliteF", [tk, fmt, str], [tk, fmt, str], (tk, 8), (fmt, 4));
                    C("_ZN3paf8datetime8DateTime19ParseSQLiteDateTimeEPKc", "DtParseSqlite", [dt, str], [dt, str], (dt, 0x15));
                }
                uint a = r.Alloc(0x18), b = r.Alloc(0x18), sum = r.Alloc(0x18);
                C("_ZN3paf8datetime8DateTimeC1Ettttttj", "DtCtor", [a, 2020, 1, 31, 23, 59, 58, 999999], [a, 2020u, 1u, 31u, 23u, 59u, 58u, 999999u], (a, 0x15));
                C("_ZN3paf8datetime8DateTimeC1Ettttttj", "DtCtor", [b, 1, 1, 1, 1, 1, 3, 2], [b, 1u, 1u, 1u, 1u, 1u, 3u, 2u], (b, 0x15));
                C("_ZNK3paf8datetime8DateTimeplERKS1_", "DtAdd", [sum, a, b], [sum, a, b], (sum, 0x15));
                C("_ZNK3paf8datetime8DateTimeltERKS1_", "DtLess", [a, sum], [a, sum]);
                C("_ZNK3paf8datetime8DateTimeltERKS1_", "DtLess", [sum, a], [sum, a]);
                foreach ((uint y, uint m, uint d) in new[] { (2020u, 2u, 29u), (2000u, 1u, 1u), (1999u, 12u, 31u), (2024u, 3u, 1u) })
                {
                    C("_ZN3paf8datetime8DateTime9DayOfWeekEttt", "DtDayOfWeek", [y, m, d], [y, m, d]);
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        };
        th.Start(0, 0);
        Assert.True(th.WaitForExit(120_000), "paf time test thread did not finish");
        r.Emu.Kernel.DeleteThread(th);
        if (failure != null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static string Hash(List<string> log) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", log))))[..16];

    [Fact]
    public void Hle_MatchesTheFirmwareLog()
    {
        using var r = new Run(fx, null);
        Scenario(r);
        output.WriteLine(string.Join("\n", r.Log));
        // a few values directly: 2020 is a leap year, 2020-02-29 was a Saturday, the RFC 3339 tick of the test date
        Assert.Contains("RtcIsLeapYear(2020) = 00000001 ", r.Log);
        Assert.Contains("RtcIsLeapYear(1900) = 00000000 ", r.Log);
        Assert.Contains("DtDayOfWeek(2020,2,29) = 00000006 ", r.Log);
        Assert.Equal(FirmwareLogHash, Hash(r.Log));
    }

    [PafFirmwareFact]
    public void MatchesFirmwareModule()
    {
        List<string> fw, hle;
        using (var r = new Run(fx, s_firmwarePath.Value))
        {
            Scenario(r);
            fw = r.Log;
        }
        using (var r = new Run(fx, null))
        {
            Scenario(r);
            hle = r.Log;
        }
        var diff = fw.Zip(hle).Where(p => p.First != p.Second).Select(p => $"fw:  {p.First}\nhle: {p.Second}").ToList();
        Assert.True(diff.Count == 0 && fw.Count == hle.Count, $"{diff.Count} calls differ from libpaf ({fw.Count} / {hle.Count} lines):\n" + string.Join("\n", diff.Take(20)));
        Assert.True(Hash(fw) == FirmwareLogHash, $"FirmwareLogHash is out of date: the firmware log hashes to {Hash(fw)}");
    }
}
