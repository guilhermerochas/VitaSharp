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
        // __stack_chk_guard, sceLibSspStackChkGuard (SceLibSsp) and SceKernelStackChkGuard all name the same guard word
        uint StackChkGuard()
        {
            if (_stackChkGuard == 0)
            {
                _stackChkGuard = Mem.Alloc(0x1000, "__stack_chk_guard", MemoryRegion.Kernel);
                GuestMemory.W32(_stackChkGuard, 0x0DEADBEE);
            }
            return _stackChkGuard;
        }
        Emu.Hle.RegisterVariable("__stack_chk_guard", StackChkGuard);
        Emu.Hle.RegisterVariable(0x1EA375E6u, StackChkGuard);
        Emu.Hle.RegisterVariable(0x4458BCF3u, StackChkGuard);
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

    /// <summary>Unnamed SceLibKernel export used by the old libc's openStdio to report "stdin : open failed" and the
    /// like (r0 = message without newline, result ignored). Behaves as an unformatted debug print.</summary>
    [Hle("NID_023EAA62", Nid = 0x023EAA62, Library = "SceLibKernel")]
    int Nid023EAA62(string msg) { Log.Info("[guest] " + msg.TrimEnd('\n')); return 0; }
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
        // an application the shell launched: the shell comes back
        if (!Emu.EndProcess(res)) Emu.Stop();
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

    [Hle] int sceKernelGetCurrentProcess() => Emu.Pid;
    [Hle] int sceKernelIsCDialogAvailable() => 1;
    [Hle] int sceKernelIsGameBudget() => 1;
    [Hle] int sceKernelGetProcessTitleId(uint buf, uint size) { GuestMemory.WriteCString(buf, Emu.TitleId, (int)size); return 0; }

    /// <summary>Process name: SceProcessParam process_name (+0x20), else the title id. NULL, 0 or &gt; 32 → INVALID_ARGUMENT.</summary>
    [Hle]
    int sceKernelGetProcessName(uint name, uint len)
    {
        if (name == 0 || len == 0 || len > 32) return SceError.INVALID_ARGUMENT;
        string? s = null;
        uint pp = Emu.Linker.ProcessParam;
        if (pp != 0 && GuestMemory.R32(pp) > 0x20 && GuestMemory.R32(pp + 0x20) is var p and not 0) s = GuestMemory.ReadCString(p, 64);
        GuestMemory.WriteCString(name, string.IsNullOrEmpty(s) ? Emu.TitleId : s, (int)len);
        return 0;
    }

    // ------------------------------------------------------------------ stack protector & runtime traps

    /// <summary>
    /// __stack_chk_fail / sceLibSspStackChkFail (the same SceLibKernel entry on FW 3.60) and sceKernelStackChkFail: the
    /// stack protector found a clobbered canary. The firmware executes bkpt and the process is killed (coredump), so
    /// this logs the crashing thread and stops the emulated process. Only reached on real stack corruption.
    /// </summary>
    [Hle("__stack_chk_fail", Library = "SceLibKernel")]
    [Hle("sceLibSspStackChkFail")]
    [Hle("sceKernelStackChkFail")]
    void __stack_chk_fail()
    {
        var t = GuestThread.Current;
        Log.Error($"__stack_chk_fail: stack smashing detected in thread {t?.Name ?? "?"} (0x{t?.Uid ?? 0:X}); terminating the process");
        t?.DumpState();
        Emu.Stop();
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

    /// <summary>
    /// Call frames {sp, pc} (SceKernelCallFrame, 8 bytes) of a thread (0 = current), found with the same heuristic as
    /// the emulator's own backtraces: stack words pointing just after a Thumb BL/BLX. mode 2 (DONT_EXCEED) returns 0,
    /// other modes return the total depth even when the buffer holds fewer frames.
    /// </summary>
    [Hle]
    unsafe int sceKernelBacktrace(int thid, uint buf, uint bufBytes, Ptr<uint> numFrames, int mode)
    {
        var t = K.GetThread(thid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        if (buf != 0 && bufBytes < 8) return SceError.INVALID_ARGUMENT;
        uint cap = buf == 0 ? 0 : bufBytes / 8;
        uint sp = t.Core.State->R[13], n = 0;
        if (thid == 0 || t == GuestThread.Current)
        {
            if (cap > 0) { GuestMemory.W32(buf, sp); GuestMemory.W32(buf + 4, t.Core.State->R[14] & ~1u); }
            n = 1;
        }
        for (uint a = sp; a < t.StackAddr + t.StackSize && n < 64; a += 4)
        {
            uint v = GuestMemory.R32(a);
            if ((v & 1) == 0 || v < 0x81000000 || v >= 0x90000000) continue;
            uint ret = v & ~1u;
            uint hw1 = GuestMemory.Read<ushort>(ret - 4), hw2 = GuestMemory.Read<ushort>(ret - 2);
            bool bl = (hw1 & 0xF800) == 0xF000 && (hw2 & 0xD000) is 0xD000 or 0xC000;
            bool blxReg = (hw2 & 0xFF87) == 0x4780;
            if (!bl && !blxReg) continue;
            if (n < cap) { GuestMemory.W32(buf + n * 8, a); GuestMemory.W32(buf + n * 8 + 4, ret); }
            n++;
        }
        numFrames.TrySet(Math.Min(n, cap));
        return (mode & BacktraceDontExceed) != 0 ? 0 : (int)n;
    }

    const int BacktraceKernel = 1, BacktraceDontExceed = 2; // SCE_KERNEL_BACKTRACE_MODE_*

    /// <summary>
    /// SceBacktrace _sceKernelBacktrace(thread_id, call_frame_buffer, num_bytes_buffer, const SceBacktraceArgs *args);
    /// SceBacktraceArgs (0x10) = { SceUInt32 *num_frames, SceInt32 mode, reserved[2] }. The user-mode unwind is shared
    /// with sceKernelBacktrace; kernel-mode stacks are not available to an application.
    /// </summary>
    [Hle]
    int _sceKernelBacktrace(int thid, uint buf, uint bufBytes, uint args)
    {
        if (args == 0) return SceError.INVALID_ARGUMENT;
        uint numFrames = GuestMemory.R32(args);
        int mode = (int)GuestMemory.R32(args + 4);
        if ((mode & BacktraceKernel) != 0) return SceError.INVALID_ARGUMENT;
        if (buf != 0 && bufBytes < 8) return SceError.INVALID_ARGUMENT;
        return sceKernelBacktrace(thid, buf, bufBytes, new Ptr<uint>(numFrames), mode);
    }

    [Hle]
    int sceKernelPrintBacktrace(uint frames, uint count)
    {
        var sb = new System.Text.StringBuilder("[guest backtrace]");
        for (uint i = 0; i < count && frames != 0; i++) sb.Append($" {GuestMemory.R32(frames + i * 8 + 4):X8}");
        Log.Info(sb.ToString());
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
