using VitaSharp.Core.Util;
﻿using System.Diagnostics;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Host;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Shared 59.94 Hz vertical blank clock.</summary>
public static class VBlank
{
    public const double PeriodSeconds = 1.0 / 59.94;

    public static long Count => (long)(GuestClock.Seconds * TimeScale.VBlank / PeriodSeconds);

    /// <summary>Blocks until vblank number <paramref name="target"/> (immediately in turbo mode).</summary>
    public static long WaitFor(long target)
    {
        if (Display.FrameSleepMs > 0) Thread.Sleep(Display.FrameSleepMs); // debug: slow every vblank wait (host time)
        if (Count >= target) return Count;
        using var blocked = Scheduler.Block();
        while (true)
        {
            long cur = Count, at = GuestClock.FromSeconds(target * PeriodSeconds / TimeScale.VBlank) + 1; // +1 tick: no rounding below the boundary
            if (cur >= target) { GuestThread.Current?.Core.WokeAt(at); return cur; } // resumes at the vblank, not at the late host wake-up
            GuestClock.WaitUntil(at);
        }
    }

    public static long WaitNext() => WaitFor(Count + 1);
}

public sealed class Display : HleModule
{
    const int ERR_INVALID_VALUE = unchecked((int)0x80290001), ERR_INVALID_ADDR = unchecked((int)0x80290002);

    uint _base, _pitch, _format, _width, _height;
    long _lastSetVcount;

    [Hle("sceDisplaySetFrameBuf")]
    int SetFrameBuf(uint pParam, uint sync)
    {
        if (pParam == 0) { _base = 0; return 0; }
        uint size = GuestMemory.R32(pParam);
        if (size is not (0x18 or 0x1C)) return ERR_INVALID_VALUE; // SceDisplayFrameBuf or SceDisplayFrameBufExt
        _base = GuestMemory.R32(pParam + 4);
        _pitch = GuestMemory.R32(pParam + 8);
        _format = GuestMemory.R32(pParam + 12);
        _width = GuestMemory.R32(pParam + 16);
        _height = GuestMemory.R32(pParam + 20);
        if (_base == 0) return 0;
        Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(_base, _pitch, _width, _height, (DisplayPixelFormat)_format) });
        _lastSetVcount = VBlank.Count;
        PresentTimes[FramesPresented & (PresentTimes.Length - 1)] = System.Diagnostics.Stopwatch.GetTimestamp();
        FramesPresented++;
        return 0;
    }

    public static long FramesPresented;
    /// <summary>Host timestamps of the last 8192 presented frames, indexed by frame number (profiling).</summary>
    public static readonly long[] PresentTimes = new long[8192];
    public static int FrameSleepMs;

    [Hle("sceDisplaySetFrameBufForCompat")]
    int SetFrameBufForCompat(uint pParam, uint sync) => SetFrameBuf(pParam, sync);

    [Hle("sceDisplayGetFrameBuf")]
    int GetFrameBuf(uint pParam, uint sync)
    {
        if (pParam == 0) return ERR_INVALID_ADDR;
        uint size = GuestMemory.R32(pParam);
        if (size is not (0x18 or 0x1C)) return ERR_INVALID_VALUE;
        if (size == 0x1C) GuestMemory.W32(pParam + 0x18, 0x00100000); // flags: bilinear filter (what the driver uses for 0x18 structs)
        GuestMemory.W32(pParam + 4, _base);
        GuestMemory.W32(pParam + 8, _pitch);
        GuestMemory.W32(pParam + 12, _format);
        GuestMemory.W32(pParam + 16, _width);
        GuestMemory.W32(pParam + 20, _height);
        return 0;
    }

    [Hle] int sceDisplayGetRefreshRate(Ptr<float> rate) { rate.TrySet(59.94005f); return 0; }
    [Hle] uint sceDisplayGetVcount() => (uint)VBlank.Count;
    [Hle] int sceDisplayGetPrimaryHead() => 0;

    [Hle("sceDisplayGetMaximumFrameBufResolution")]
    int GetMaxRes(Ptr<int> w, Ptr<int> h) { w.TrySet(960); h.TrySet(544); return 0; }

    [Hle] int sceDisplayWaitVblankStart() { VBlank.WaitNext(); return 0; }
    [Hle] int sceDisplayWaitVblankStartCB() { VBlank.WaitNext(); RunCallbacks(); return 0; }

    [Hle]
    int sceDisplayWaitVblankStartMulti(uint count)
    {
        VBlank.WaitFor(VBlank.Count + Math.Max(count, 1));
        return 0;
    }

    [Hle] int sceDisplayWaitVblankStartMultiCB(uint count) { sceDisplayWaitVblankStartMulti(count); RunCallbacks(); return 0; }

    [Hle] int sceDisplayWaitSetFrameBuf() { VBlank.WaitFor(_lastSetVcount + 1); return 0; }
    [Hle] int sceDisplayWaitSetFrameBufCB() { VBlank.WaitFor(_lastSetVcount + 1); RunCallbacks(); return 0; }
    [Hle] int sceDisplayWaitSetFrameBufMulti(uint count) { VBlank.WaitFor(_lastSetVcount + Math.Max(count, 1)); return 0; }
    [Hle] int sceDisplayWaitSetFrameBufMultiCB(uint count) { VBlank.WaitFor(_lastSetVcount + Math.Max(count, 1)); RunCallbacks(); return 0; }

    /// <summary>On hardware the callbacks notified during a *CB wait run inside the wait, before it returns.</summary>
    static void RunCallbacks()
    {
        if (GuestThread.Current is { CallbackWait: true } t && Volatile.Read(ref t.PendingCallbacks) > 0)
            GuestThread.CallbackProcessor?.Invoke(t);
    }

    // ---- vblank start callbacks: a host thread observes the vblank clock (it never advances guest time, which matters in
    // turbo mode) and notifies every registered callback once per vblank; they run when the owner enters a *CB wait or
    // calls sceKernelCheckCallback.

    readonly System.Collections.Concurrent.ConcurrentDictionary<int, Callback> _vblankCallbacks = new();
    Thread? _vblankNotifier;

    [Hle]
    int sceDisplayRegisterVblankStartCallback(int uid)
    {
        var cb = K.Objects.Get<Callback>(uid);
        if (cb == null) return ERR_INVALID_VALUE;
        _vblankCallbacks[uid] = cb;
        lock (_vblankCallbacks)
        {
            if (_vblankNotifier == null)
            {
                _vblankNotifier = new Thread(VblankNotifierLoop) { IsBackground = true, Name = "VBlank notifier" };
                _vblankNotifier.Start();
            }
        }
        return 0;
    }

    [Hle] int sceDisplayUnregisterVblankStartCallback(int uid) => _vblankCallbacks.TryRemove(uid, out _) ? 0 : ERR_INVALID_VALUE;

    void VblankNotifierLoop()
    {
        long last = VBlank.Count;
        while (Emu.Running || Emu.MainModule == null)
        {
            Thread.Sleep(1);
            long now = VBlank.Count;
            if (now <= last) continue;
            int n = (int)Math.Min(now - last, 4);
            last = now;
            foreach (var (uid, cb) in _vblankCallbacks)
            {
                if (K.Objects.Get<Callback>(uid) == null) { _vblankCallbacks.TryRemove(uid, out _); continue; }
                for (int i = 0; i < n; i++) cb.Notify(0);
            }
        }
    }

    // ---- display "Internal" variants (SceDisplayUser: head + framebuffer slot). VitaSharp has only head 0 (LCD/OLED)
    // and slot 0 (the game); slot 1 (LiveArea overlay) is accepted and ignored.

    const int ERR_INVALID_HEAD = unchecked((int)0x80290000), ERR_INVALID_UPDATETIMING = unchecked((int)0x80290006);

    [Hle] int sceDisplayGetVcountInternal(int display) => display == 0 ? (int)VBlank.Count : ERR_INVALID_HEAD;

    /// <summary>Inferred: (head, fb_idx, SceDisplayFrameBuf *pParam, sync), the _sceDisplayGetFrameBufInternal arguments without opt.</summary>
    [Hle]
    int sceDisplayGetFrameBufInternal(int head, int index, uint pParam, uint sync)
    {
        if (head != 0) return ERR_INVALID_HEAD;
        if (pParam == 0) return ERR_INVALID_ADDR;
        if (sync > 1) return ERR_INVALID_UPDATETIMING;
        if (index == 0) return GetFrameBuf(pParam, sync);
        uint size = GuestMemory.R32(pParam);
        if (size is not (0x18 or 0x1C)) return ERR_INVALID_VALUE;
        GuestMemory.Fill(pParam + 4, 0, size - 4);
        return 0;
    }

    /// <summary>Inferred: (head, fb_idx, const SceDisplayFrameBuf *pParam, sync).</summary>
    [Hle]
    int sceDisplaySetFrameBufInternal(int head, int index, uint pParam, uint sync)
    {
        if (head != 0) return ERR_INVALID_HEAD;
        if (sync > 1) return ERR_INVALID_UPDATETIMING;
        if (index == 0) return SetFrameBuf(pParam, sync);
        if (pParam != 0 && GuestMemory.R32(pParam) is not (0x18 or 0x1C)) return ERR_INVALID_VALUE;
        return 0;
    }

    /// <summary>Inferred: (head, SceDisplayResolutionInfo *info). Info (0x1C): size, screen_mode, width, height,
    /// output_format, scan_mode, fps (float).</summary>
    [Hle]
    int sceDisplayGetResolutionInfoInternal(int head, uint info)
    {
        if (head != 0) return ERR_INVALID_HEAD;
        if (info == 0) return ERR_INVALID_ADDR;
        if (GuestMemory.R32(info) != 0x1C) return ERR_INVALID_VALUE;
        GuestMemory.W32(info + 0x04, 0);   // SCE_DISPLAY_SCREENMODE_INTERNAL_960X544
        GuestMemory.W32(info + 0x08, 960);
        GuestMemory.W32(info + 0x0C, 544);
        GuestMemory.W32(info + 0x10, 0);   // output format
        GuestMemory.W32(info + 0x14, 0);   // progressive
        GuestMemory.Write(info + 0x18, 59.94005f);
        return 0;
    }
}

public sealed class Ctrl : HleModule
{
    uint _samplingMode;
    long _lastRead;
    static readonly string CtrlExp = Environment.GetEnvironmentVariable("VITASHARP_CTRL_EXP") ?? "";

    ControllerState State(int port) => Emu.Host.Input?.GetController(port) ?? ControllerState.Neutral;

    void WriteData(uint dst, uint count, bool negative, int port, bool ext)
    {
        var s = State(port);
        for (uint i = 0; i < Math.Max(count, 1); i++)
        {
            uint p = dst + i * 32;
            GuestMemory.Fill(p, 0, 32);
            GuestMemory.Write(p, (ulong)(Emu.Kernel.ProcessTimeUs));
            uint buttons = ApplyRapidFire((uint)s.Buttons, VBlank.Count);
            if (buttons != 0) VitaSharp.Core.Util.Log.Debug($"ctrl port {port} buttons 0x{buttons:X} -> 0x{p:X8}");
            if (!ext) buttons &= 0xFFFF; // standard buttons only
            if (CtrlExp.Contains("l1"))
            {
                if ((buttons & (uint)VitaButtons.LTrigger) != 0) buttons |= (uint)VitaButtons.L1;
                if ((buttons & (uint)VitaButtons.RTrigger) != 0) buttons |= (uint)VitaButtons.R1;
            }
            GuestMemory.W32(p + 8, negative ? ~buttons : buttons);
            if (CtrlExp.Contains("pressure"))
            {
                // SceCtrlData +16: up, right, down, left, lt, rt, l1, r1, triangle, circle, cross, square
                ReadOnlySpan<uint> order = [0x10, 0x20, 0x40, 0x80, 0x100, 0x200, 0x100, 0x200, 0x1000, 0x2000, 0x4000, 0x8000];
                for (int b = 0; b < order.Length; b++)
                    if ((buttons & order[b]) != 0) GuestMemory.Write(p + 16 + (uint)b, (byte)255);
            }
            bool analog = _samplingMode != 0;
            GuestMemory.Write(p + 12, analog ? s.Lx : (byte)128);
            GuestMemory.Write(p + 13, analog ? s.Ly : (byte)128);
            GuestMemory.Write(p + 14, analog ? s.Rx : (byte)128);
            GuestMemory.Write(p + 15, analog ? s.Ry : (byte)128);
        }
    }

    int Peek(int port, uint data, uint count, bool negative, bool ext)
    {
        if (data == 0) return unchecked((int)0x80340001); // SCE_CTRL_ERROR_INVALID_ARG
        if (count == 0) return 0;
        WriteData(data, 1, negative, port, ext); // newest sample only
        return 1;
    }

    int Read(int port, uint data, uint count, bool negative, bool ext)
    {
        // reads block until the next sample (one per vblank)
        long target = Math.Max(_lastRead + 1, VBlank.Count);
        _lastRead = VBlank.WaitFor(target);
        return Peek(port, data, count, negative, ext);
    }

    static int Port(int port) => port <= 1 ? 0 : port - 1;

    [Hle] int sceCtrlPeekBufferPositive(int port, uint data, uint count) => Peek(Port(port), data, count, false, false);
    [Hle] int sceCtrlPeekBufferNegative(int port, uint data, uint count) => Peek(Port(port), data, count, true, false);
    [Hle] int sceCtrlReadBufferPositive(int port, uint data, uint count) => Read(Port(port), data, count, false, false);
    [Hle] int sceCtrlReadBufferNegative(int port, uint data, uint count) => Read(Port(port), data, count, true, false);
    [Hle] int sceCtrlPeekBufferPositiveExt(int port, uint data, uint count) => Peek(Port(port), data, count, false, true);
    [Hle] int sceCtrlReadBufferPositiveExt(int port, uint data, uint count) => Read(Port(port), data, count, false, true);
    [Hle] int sceCtrlPeekBufferPositive2(int port, uint data, uint count) => Peek(Port(port), data, count, false, true);
    [Hle] int sceCtrlReadBufferPositive2(int port, uint data, uint count) => Read(Port(port), data, count, false, true);
    [Hle] int sceCtrlPeekBufferPositiveExt2(int port, uint data, uint count) => Peek(Port(port), data, count, false, true);
    [Hle] int sceCtrlReadBufferPositiveExt2(int port, uint data, uint count) => Read(Port(port), data, count, false, true);
    [Hle] int sceCtrlPeekBufferNegative2(int port, uint data, uint count) => Peek(Port(port), data, count, true, true);
    [Hle] int sceCtrlReadBufferNegative2(int port, uint data, uint count) => Read(Port(port), data, count, true, true);

    [Hle] int sceCtrlSetSamplingMode(uint mode) { uint old = _samplingMode; _samplingMode = mode; return (int)old; }
    [Hle] int sceCtrlSetSamplingModeExt(uint mode) { uint old = _samplingMode; _samplingMode = mode; return (int)old; }
    [Hle] int sceCtrlGetSamplingMode(Ptr<uint> mode) { mode.TrySet(_samplingMode); return 0; }
    [Hle] int sceCtrlGetSamplingModeExt(Ptr<uint> mode) { mode.TrySet(_samplingMode); return 0; }
    // analog stick check (semantics unconfirmed: the values are only stored and reported back)
    int _stickCheckMode, _stickCheckTarget;
    [Hle] int sceCtrlSetAnalogStickCheckMode(int mode) { _stickCheckMode = mode; return 0; }
    [Hle] int sceCtrlGetAnalogStickCheckMode(Ptr<int> mode) { if (mode.IsNull) return ERR_INVALID_ARG; mode.Value = _stickCheckMode; return 0; }
    [Hle] int sceCtrlSetAnalogStickCheckTarget(int target) { _stickCheckTarget = target; return 0; }
    [Hle] int sceCtrlGetAnalogStickCheckTarget(Ptr<int> target) { if (target.IsNull) return ERR_INVALID_ARG; target.Value = _stickCheckTarget; return 0; }
    [Hle] int sceCtrlSetButtonIntercept(uint v) => 0;
    [Hle] int sceCtrlGetButtonIntercept(Ptr<uint> v) { v.TrySet(0); return 0; }

    const int ERR_INVALID_ARG = unchecked((int)0x80340001);

    /// <summary>SceCtrlRapidFireRule (0x18): Mask, Trigger, Target, Delay, Make, Break (u32 each).</summary>
    sealed class RapidFireRule
    {
        public uint Mask, Trigger, Target, Delay, Make, Break;
        /// <summary>Vblank at which the trigger was first seen satisfied (-1 = not active).</summary>
        public long Start = -1;
    }

    readonly RapidFireRule?[] _rapid = new RapidFireRule?[16];

    /// <summary>Applies the rapid-fire rules to one sample (one per vblank): while (buttons &amp; Mask) == Trigger the
    /// Target bits stay as read for Delay samples, then are forced on for Make samples and off for Break samples, in a
    /// cycle. PSP sceCtrl semantics; exact ordering on the Vita unconfirmed.</summary>
    internal uint ApplyRapidFire(uint buttons, long vcount)
    {
        uint raw = buttons;
        lock (_rapid)
        {
            foreach (var r in _rapid)
            {
                if (r == null) continue;
                if ((raw & r.Mask) != r.Trigger) { r.Start = -1; continue; }
                if (r.Start < 0) r.Start = vcount;
                long t = vcount - r.Start;
                if (t < r.Delay) continue;
                long period = (long)r.Make + r.Break;
                if (period == 0) continue;
                long phase = (t - r.Delay) % period;
                buttons = phase < r.Make ? buttons | r.Target : buttons & ~r.Target;
            }
        }
        return buttons;
    }

    [Hle]
    int sceCtrlSetRapidFire(int port, int idx, uint rule)
    {
        if (idx is < 0 or > 15 || rule == 0 || port is < 0 or > 1) return ERR_INVALID_ARG;
        var r = new RapidFireRule
        {
            Mask = GuestMemory.R32(rule), Trigger = GuestMemory.R32(rule + 4), Target = GuestMemory.R32(rule + 8),
            Delay = GuestMemory.R32(rule + 12), Make = GuestMemory.R32(rule + 16), Break = GuestMemory.R32(rule + 20),
        };
        lock (_rapid) _rapid[idx] = r;
        return 0;
    }

    [Hle]
    int sceCtrlClearRapidFire(int port, int idx)
    {
        if (idx is < 0 or > 15 || port is < 0 or > 1) return ERR_INVALID_ARG;
        lock (_rapid) _rapid[idx] = null;
        return 0;
    }
    [Hle] int sceCtrlSetActuator(int port, uint state) => 0;
    [Hle] int sceCtrlSetLightBar(int port, byte r, byte g, byte b) => 0;
    [Hle] int sceCtrlResetLightBar(int port) => 0;
    [Hle] int sceCtrlIsMultiControllerSupported() => 0;
    [Hle] int sceCtrlSingleControllerMode(int mode) => 0;

    [Hle]
    int sceCtrlGetControllerPortInfo(uint info)
    {
        if (info == 0) return unchecked((int)0x80340002);
        GuestMemory.Fill(info, 0, 5);
        GuestMemory.Write(info, (byte)1);     // port 0: vita built-in
        GuestMemory.Write(info + 1, (byte)1);
        return 0;
    }

    [Hle]
    int sceCtrlGetWirelessControllerInfo(uint info)
    {
        if (info == 0) return unchecked((int)0x80340002);
        GuestMemory.Fill(info, 0, 16); // connected[4] + reserved[12]: no wireless controllers paired
        return 0;
    }

    [Hle]
    int sceCtrlGetBatteryInfo(int port, Ptr<byte> level) { level.TrySet(5); return 0; }
}

/// <summary>SceHid: USB/Bluetooth keyboards and mice. None are attached, so enumeration finds no devices.</summary>
public sealed class Hid : HleModule
{
    /// <summary>sceHid{Keyboard,Mouse}Enumerate(int *handles, int count): returns the number of devices written.</summary>
    [Hle("sceHidKeyboardEnumerate")]
    [Hle("sceHidMouseEnumerate")]
    int sceHidEnumerate(uint handles, int count) => 0;

    /// <summary>
    /// psp2/hid.h: <c>int sceHidKeyboardRead/Peek(SceUInt32 handle, SceHidKeyboardReport *reports[], int nReports)</c>
    /// and <c>int sceHidMouseRead(SceUInt32 handle, SceHidMouseReport *reports[], int nReports)</c>; the result is the
    /// number of reports filled. No device is ever attached, so there is never a report: return 0 at once instead of
    /// blocking (Read is documented as blocking, which would hang a caller polling a handle it never enumerated).
    /// The report buffers are not touched.
    /// </summary>
    [Hle("sceHidKeyboardRead")]
    [Hle("sceHidKeyboardPeek")]
    [Hle("sceHidMouseRead")]
    int sceHidRead(uint handle, uint reports, int nReports) => 0;
}

public sealed class Touch : HleModule
{
    const int ERR_INVALID_ARG = unchecked((int)0x80350001);
    const int MaxBuffers = 64;

    /// <summary>SCE_TOUCH_SAMPLING_STATE_STOP (0) / START (1) per panel. Hardware (and Vita3K) start with both panels
    /// stopped: games call sceTouchSetSamplingState before reading.</summary>
    readonly uint[] _sampling = [0, 0];
    /// <summary>sceTouchEnableTouchForce: reports carry a force value only when enabled.</summary>
    readonly bool[] _force = [false, false];

    internal uint SamplingState(int port) => _sampling[port];

    [Hle]
    int sceTouchGetPanelInfo(int port, uint info)
    {
        if (info == 0 || port is < 0 or > 1) return ERR_INVALID_ARG;
        GuestMemory.Fill(info, 0, 0x30);
        // minAaX minAaY maxAaX maxAaY minDispX minDispY maxDispX maxDispY (s16), minForce maxForce (u8)
        short[] v = port == 0 ? [0, 0, 1919, 1087, 0, 0, 1919, 1087] : [0, 108, 1919, 889, 0, 108, 1919, 889];
        for (int i = 0; i < 8; i++) GuestMemory.Write(info + (uint)i * 2, v[i]);
        GuestMemory.Write(info + 16, (byte)1);
        GuestMemory.Write(info + 17, (byte)128);
        return 0;
    }

    // Each new finger contact gets a fresh report id (1..127), as on hardware: games use the id to tell a new tap
    // from a finger that is still down from the previous screen.
    readonly Dictionary<int, byte>[] _ids = [new(), new()];
    readonly byte[] _nextId = [1, 1];

    int Peek(int port, uint data, uint count)
    {
        if (data == 0 || port is < 0 or > 1 || count > MaxBuffers) return ERR_INVALID_ARG;
        if (count == 0) return 0;
        // stopped panel: the read still succeeds, with a header and no reports (Vita3K: reportNum = 0)
        // polled every frame: stack buffers instead of lists/LINQ (no garbage per call)
        Span<TouchPoint> touches = stackalloc TouchPoint[8];
        int reports = _sampling[port] != 0 && Emu.Host.Input is { } input ? input.GetTouches(port == 1, touches) : 0;
        Span<byte> ids = stackalloc byte[8];
        lock (_ids)
        {
            var map = _ids[port];
            // forget contacts that are no longer reported
            Span<int> gone = stackalloc int[8];
            int nGone = 0;
            bool overflow = false;
            foreach (var k in map.Keys)
            {
                bool present = false;
                for (int r = 0; r < reports; r++) if (touches[r].Id == k) { present = true; break; }
                if (present) continue;
                if (nGone < gone.Length) gone[nGone++] = k; else overflow = true;
            }
            for (int i = 0; i < nGone; i++) map.Remove(gone[i]);
            if (overflow)
                foreach (var k in map.Keys.ToList())
                {
                    bool present = false;
                    for (int r = 0; r < reports; r++) if (touches[r].Id == k) { present = true; break; }
                    if (!present) map.Remove(k);
                }
            for (int r = 0; r < reports; r++)
            {
                if (!map.TryGetValue(touches[r].Id, out ids[r]))
                {
                    ids[r] = _nextId[port];
                    _nextId[port] = (byte)(_nextId[port] % 127 + 1); // 1..127: the game treats id 0 as no contact
                    map[touches[r].Id] = ids[r];
                }
            }
        }
        // only the newest sample is reported (the count requested is a maximum)
        GuestMemory.Fill(data, 0, 0x90);
        GuestMemory.Write(data, (ulong)Emu.Kernel.ProcessTimeUs);
        GuestMemory.W32(data + 12, (uint)reports);
        // front panel covers 1920x1088; the rear panel's active area is y 108..889 (see sceTouchGetPanelInfo)
        float yMin = port == 0 ? 0 : 108, yMax = port == 0 ? 1087 : 889;
        byte force = _force[port] ? (byte)128 : (byte)0; // panel force range is 1..128 and the host has no pressure
        for (int r = 0; r < reports; r++)
        {
            uint rp = data + 16 + (uint)r * 16;
            var t = touches[r];
            GuestMemory.Write(rp, ids[r]);
            GuestMemory.Write(rp + 1, force);
            GuestMemory.Write(rp + 2, (ushort)Math.Clamp(t.X * 1919, 0, 1919));
            GuestMemory.Write(rp + 4, (ushort)Math.Clamp(yMin + t.Y * (yMax - yMin), yMin, yMax));
        }
        return 1;
    }

    [Hle("sceTouchPeek")]
    [Hle("sceTouchPeek2")]
    int sceTouchPeek(int port, uint data, uint count) => Peek(port, data, count);

    readonly long[] _lastRead = [-1, -1];

    /// <summary>Blocks only until a sample newer than the previous read exists (one per vblank), like the hardware.</summary>
    [Hle("sceTouchRead")]
    [Hle("sceTouchRead2")]
    int sceTouchRead(int port, uint data, uint count)
    {
        if (data == 0 || port is < 0 or > 1 || count > MaxBuffers) return ERR_INVALID_ARG;
        _lastRead[port] = VBlank.WaitFor(Math.Max(_lastRead[port] + 1, VBlank.Count));
        return Peek(port, data, count);
    }

    // Regions split the panel between processes/overlays; a game owns the whole panel, so the region is ignored.
    [Hle("sceTouchPeekRegion")]
    [Hle("sceTouchPeekRegionExt")]
    int sceTouchPeekRegion(int port, uint data, uint count, int region) => region > 0xFF ? ERR_INVALID_ARG : sceTouchPeek(port, data, count);

    [Hle("sceTouchReadRegion")]
    [Hle("sceTouchReadRegionExt")]
    int sceTouchReadRegion(int port, uint data, uint count, int region) => region > 0xFF ? ERR_INVALID_ARG : sceTouchRead(port, data, count);

    [Hle] int sceTouchSetRegion() => 0;
    [Hle] int sceTouchSetRegionAttr() => 0;
    [Hle] int sceTouchActivateRegion() => 0;
    [Hle] int sceTouchClearRegion() => 0;
    [Hle] int sceTouchEnableIdleTimerCancelSetting(int port, int enable) => 0; // no idle timer

    [Hle("sceTouchSetSamplingState")]
    [Hle("sceTouchSetSamplingStateExt")]
    int sceTouchSetSamplingState(int port, uint state)
    {
        if (port is < 0 or > 1 || state > 1) return ERR_INVALID_ARG;
        _sampling[port] = state;
        Log.Debug($"sceTouchSetSamplingState(port={port}, state={state})");
        return 0;
    }

    [Hle("sceTouchGetSamplingState")]
    [Hle("sceTouchGetSamplingStateExt")]
    int sceTouchGetSamplingState(int port, Ptr<uint> state)
    {
        if (port is < 0 or > 1 || state.IsNull) return ERR_INVALID_ARG;
        state.Value = _sampling[port];
        return 0;
    }

    [Hle("sceTouchEnableTouchForce")]
    [Hle("sceTouchEnableTouchForceExt")]
    int sceTouchEnableTouchForce(int port) { if (port is < 0 or > 1) return ERR_INVALID_ARG; _force[port] = true; return 0; }

    [Hle("sceTouchDisableTouchForce")]
    [Hle("sceTouchDisableTouchForceExt")]
    int sceTouchDisableTouchForce(int port) { if (port is < 0 or > 1) return ERR_INVALID_ARG; _force[port] = false; return 0; }

    /// <summary>Panel density; Vita3K reports 22.0 for its default hardware info (0x30), 17.54 for the other model.</summary>
    const float PixelDensity = 22.0f;

    [Hle]
    int sceTouchGetPixelDensity(Ptr<float> front, Ptr<float> back)
    {
        if (front.IsNull || back.IsNull) return ERR_INVALID_ARG;
        front.Value = back.Value = PixelDensity;
        return 0;
    }

    [Hle]
    int sceTouchGetPixelDensity2(Ptr<float> p1, Ptr<float> p2, Ptr<float> p3, Ptr<float> p4)
    {
        if (p1.IsNull || p2.IsNull || p3.IsNull || p4.IsNull) return ERR_INVALID_ARG;
        p1.Value = p2.Value = p3.Value = p4.Value = PixelDensity;
        return 0;
    }
}
