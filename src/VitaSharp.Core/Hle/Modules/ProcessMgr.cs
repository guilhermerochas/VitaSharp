using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceProcessmgr, libc time hooks, module manager, misc process services and SceClib.</summary>
public sealed class ProcessMgr : HleModule
{
    uint _stackChkGuard;

    public override void Initialize()
    {
        Emu.Hle.RegisterVariable("__stack_chk_guard", () =>
        {
            _stackChkGuard = Mem.Alloc(0x1000, "__stack_chk_guard", MemoryRegion.Kernel);
            GuestMemory.W32(_stackChkGuard, 0x0DEADBEE);
            return _stackChkGuard;
        });
        Emu.Hle.RegisterVariable("__sce_libcparam", () =>
        {
            uint pp = Emu.Linker.ProcessParam;
            return pp != 0 && GuestMemory.R32(pp) >= 0x30 ? GuestMemory.R32(pp + 0x2C) : 0;
        });
    }

    [Hle] uint sceKernelGetProcessParam() => Emu.Linker.ProcessParam;
    [Hle] int sceKernelGetStdin() => 0x10;
    [Hle] int sceKernelGetStdout() => 0x11;
    [Hle] int sceKernelGetStderr() => 0x12;
    [Hle] int sceKernelPowerTick(int type) => 0;
    [Hle] int sceKernelPowerLock(int type) => 0;
    [Hle] int sceKernelPowerUnlock(int type) => 0;

    [Hle] uint sceKernelLibcClock() => (uint)K.ProcessTimeUs;

    [Hle]
    int sceKernelLibcGettimeofday(uint tv, uint tz)
    {
        var now = DateTimeOffset.UtcNow;
        if (tv != 0)
        {
            GuestMemory.W32(tv, (uint)now.ToUnixTimeSeconds());
            GuestMemory.W32(tv + 4, (uint)(now.Ticks % TimeSpan.TicksPerSecond / 10));
        }
        if (tz != 0)
        {
            GuestMemory.W32(tz, (uint)(-(int)TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes));
            GuestMemory.W32(tz + 4, 0);
        }
        return 0;
    }

    [Hle]
    uint sceKernelLibcTime(Ptr<uint> t)
    {
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        t.TrySet(now);
        return now;
    }

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

    [Hle]
    uint sceKernelLibcGmtime_r(Ptr<uint> time, uint tm)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds((int)time.Value).UtcDateTime;
        WriteTm(tm, d, false);
        return tm;
    }

    [Hle]
    uint sceKernelLibcLocaltime_r(Ptr<uint> time, uint tm)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds((int)time.Value).LocalDateTime;
        WriteTm(tm, d, TimeZoneInfo.Local.IsDaylightSavingTime(d));
        return tm;
    }

    [Hle]
    int sceKernelLibcMktime(uint tm)
    {
        try
        {
            var d = new DateTime((int)GuestMemory.R32(tm + 20) + 1900, 1, 1, 0, 0, 0, DateTimeKind.Local)
                .AddMonths((int)GuestMemory.R32(tm + 16)).AddDays((int)GuestMemory.R32(tm + 12) - 1)
                .AddHours((int)GuestMemory.R32(tm + 8)).AddMinutes((int)GuestMemory.R32(tm + 4)).AddSeconds((int)GuestMemory.R32(tm));
            return (int)new DateTimeOffset(d).ToUnixTimeSeconds();
        }
        catch { return -1; }
    }

    [Hle]
    void sceKernelExitProcess(int res)
    {
        Log.Info($"sceKernelExitProcess({res})");
        Emu.Stop();
        throw new GuestThreadExitException(res, false);
    }

    [Hle]
    int sceKernelCallAbortHandler()
    {
        Log.Error("sceKernelCallAbortHandler: the game aborted");
        GuestThread.Current?.DumpState();
        throw new GuestThreadExitException(-1, false);
    }

    [Hle]
    int sceKernelGetOpenPsId(uint id)
    {
        for (uint i = 0; i < 16; i++) GuestMemory.Write(id + i, (byte)(0x10 + i));
        return 0;
    }

    [Hle] int sceKernelGetCurrentProcess() => 0x40010003;
    [Hle] int sceKernelIsCDialogAvailable() => 1;
    [Hle] int sceKernelIsGameBudget() => 1;
    [Hle] int sceKernelGetProcessTitleId(uint buf, uint size) { GuestMemory.WriteCString(buf, Emu.TitleId, (int)size); return 0; }

    // ------------------------------------------------------------------ stack protector & runtime traps

    [Hle]
    void __stack_chk_fail()
    {
        Log.Error("__stack_chk_fail: stack smashing detected");
        GuestThread.Current?.DumpState();
        throw new GuestThreadExitException(-1, false);
    }

    [Hle] int __sce_aeabi_idiv0() { Log.Warn("Integer division by zero"); return 0; }
    [Hle] long __sce_aeabi_ldiv0() { Log.Warn("Long division by zero"); return 0; }

    [Hle]
    int sceKernelBacktraceSelf(uint buf, uint size, Ptr<uint> count, int flags)
    {
        count.TrySet(0);
        return 0;
    }

    [Hle]
    int sceDbgAssertionHandler(string file, int line, int stop, string component, string msg, VarArgs va)
    {
        Log.Error($"Assertion: {file}:{line} [{component}] {CFormat.Format(msg, va)}");
        return 0;
    }

    [Hle] int sceDbgSetMinimumLogLevel(int level) => 0;
    [Hle] int sceDbgSetBreakOnErrorState(int s) => 0;
    [Hle] int sceDbgSetBreakOnWarningState(int s) => 0;

    [Hle]
    int sceDbgLoggingHandler(string file, int line, int severity, string component, string msg, VarArgs va)
    {
        Log.Info($"[guest] {component}: {CFormat.Format(msg, va)}");
        return 0;
    }

    // ------------------------------------------------------------------ SceClib

    [Hle]
    int sceClibPrintf(string fmt, VarArgs va)
    {
        string s = CFormat.Format(fmt, va);
        Log.Info("[guest] " + s.TrimEnd('\n'));
        return s.Length;
    }

    [Hle]
    int sceClibSnprintf(uint buf, uint size, string fmt, VarArgs va)
    {
        string s = CFormat.Format(fmt, va);
        if (size > 0) GuestMemory.WriteCString(buf, s, (int)size);
        return System.Text.Encoding.UTF8.GetByteCount(s);
    }

    [Hle]
    int sceClibVsnprintf(uint buf, uint size, string fmt, uint vaList)
    {
        // va_list on ARM is a pointer to the stacked arguments
        string s = CFormat.Format(fmt, new VarArgs(new StackVaContext(vaList), 0));
        if (size > 0) GuestMemory.WriteCString(buf, s, (int)size);
        return System.Text.Encoding.UTF8.GetByteCount(s);
    }

    [Hle("sceClibMemcpy")]
    [Hle("sceClibMemcpy_safe")]
    [Hle("sceClibMemmove")]
    uint sceClibMemcpy(uint dst, uint src, uint len) { GuestMemory.Copy(dst, src, len); Kernel.GuestThread.Current!.Core.ChargeInstructions(len / 4); return dst; }

    [Hle] uint sceClibMemset(uint dst, int ch, uint len) { GuestMemory.Fill(dst, (byte)ch, len); Kernel.GuestThread.Current!.Core.ChargeInstructions(len / 8); return dst; }

    [Hle]
    int sceClibMemcmp(uint a, uint b, uint len) => GuestMemory.Span(a, (int)len).SequenceCompareTo(GuestMemory.Span(b, (int)len));

    [Hle] uint sceClibStrnlen(uint s, uint max) { uint n = 0; while (n < max && GuestMemory.Read<byte>(s + n) != 0) n++; return n; }

    [Hle]
    uint sceClibStrncpy(uint dst, uint src, uint n)
    {
        uint i = 0;
        for (; i < n; i++) { byte c = GuestMemory.Read<byte>(src + i); GuestMemory.Write(dst + i, c); if (c == 0) break; }
        for (; i < n; i++) GuestMemory.Write(dst + i, (byte)0);
        return dst;
    }

    [Hle] int sceClibStrcmp(string a, string b) => string.CompareOrdinal(a, b);
    [Hle] int sceClibStrncmp(uint a, uint b, uint n) { for (uint i = 0; i < n; i++) { int ca = GuestMemory.Read<byte>(a + i), cb = GuestMemory.Read<byte>(b + i); if (ca != cb) return ca - cb; if (ca == 0) return 0; } return 0; }

    /// <summary>Minimal context exposing a va_list pointer as the argument stack.</summary>
    sealed class StackVaContext : IVaSource
    {
        uint _p;
        public StackVaContext(uint p) => _p = p;
        public uint Next() { uint v = GuestMemory.R32(_p); _p += 4; return v; }
    }
}
