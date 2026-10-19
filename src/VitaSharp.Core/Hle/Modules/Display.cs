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
            long cur = Count;
            if (cur >= target) return cur;
            // +1 tick so rounding cannot land just before the boundary
            GuestClock.WaitUntil(GuestClock.FromSeconds(target * PeriodSeconds / TimeScale.VBlank) + 1);
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
        if (size != 24) return ERR_INVALID_VALUE;
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
        GuestMemory.W32(pParam, 24);
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
    [Hle] int sceDisplayWaitVblankStartCB() { VBlank.WaitNext(); return 0; }

    [Hle]
    int sceDisplayWaitVblankStartMulti(uint count)
    {
        VBlank.WaitFor(VBlank.Count + Math.Max(count, 1));
        return 0;
    }

    [Hle] int sceDisplayWaitVblankStartMultiCB(uint count) => sceDisplayWaitVblankStartMulti(count);

    [Hle] int sceDisplayWaitSetFrameBuf() { VBlank.WaitFor(_lastSetVcount + 1); return 0; }
    [Hle] int sceDisplayWaitSetFrameBufCB() { VBlank.WaitFor(_lastSetVcount + 1); return 0; }
    [Hle] int sceDisplayWaitSetFrameBufMulti(uint count) { VBlank.WaitFor(_lastSetVcount + Math.Max(count, 1)); return 0; }
    [Hle] int sceDisplayWaitSetFrameBufMultiCB(uint count) { VBlank.WaitFor(_lastSetVcount + Math.Max(count, 1)); return 0; }
    [Hle] int sceDisplayRegisterVblankStartCallback(int uid) => 0;
    [Hle] int sceDisplayUnregisterVblankStartCallback(int uid) => 0;
}

public sealed class Ctrl : HleModule
{
    uint _samplingMode;
    long _lastRead;

    ControllerState State(int port) => Emu.Host.Input?.GetController(port) ?? ControllerState.Neutral;

    void WriteData(uint dst, uint count, bool negative, int port, bool ext)
    {
        var s = State(port);
        for (uint i = 0; i < Math.Max(count, 1); i++)
        {
            uint p = dst + i * 32;
            GuestMemory.Fill(p, 0, 32);
            GuestMemory.Write(p, (ulong)(Emu.Kernel.ProcessTimeUs));
            uint buttons = (uint)s.Buttons;
            if (buttons != 0) VitaSharp.Core.Util.Log.Debug($"ctrl port {port} buttons 0x{buttons:X} -> 0x{p:X8}");
            if (!ext) buttons &= 0xFFFF; // standard buttons only
            GuestMemory.W32(p + 8, negative ? ~buttons : buttons);
            bool analog = _samplingMode != 0;
            GuestMemory.Write(p + 12, analog ? s.Lx : (byte)128);
            GuestMemory.Write(p + 13, analog ? s.Ly : (byte)128);
            GuestMemory.Write(p + 14, analog ? s.Rx : (byte)128);
            GuestMemory.Write(p + 15, analog ? s.Ry : (byte)128);
        }
    }

    int Peek(int port, uint data, uint count, bool negative, bool ext)
    {
        if (data == 0) return unchecked((int)0x80340002);
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
    [Hle] int sceCtrlSetAnalogStickCheckMode(uint mode) => 0;
    [Hle] int sceCtrlSetButtonIntercept(uint v) => 0;
    [Hle] int sceCtrlGetButtonIntercept(Ptr<uint> v) { v.TrySet(0); return 0; }
    [Hle] int sceCtrlSetRapidFire(int port, int idx, uint p) => 0;
    [Hle] int sceCtrlClearRapidFire(int port, int idx) => 0;
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
}

public sealed class Touch : HleModule
{
    [Hle]
    int sceTouchGetPanelInfo(int port, uint info)
    {
        if (info == 0) return unchecked((int)0x80350001);
        GuestMemory.Fill(info, 0, 0x20);
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
        if (data == 0 || port is < 0 or > 1) return unchecked((int)0x80350001);
        if (count == 0) return 0;
        var touches = Emu.Host.Input?.GetTouches(port == 1) ?? [];
        int reports = Math.Min(touches.Count, 8);
        byte[] ids = new byte[reports];
        lock (_ids)
        {
            var map = _ids[port];
            foreach (var gone in map.Keys.Where(k => !touches.Take(reports).Any(t => t.Id == k)).ToList()) map.Remove(gone);
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
        for (int r = 0; r < reports; r++)
        {
            uint rp = data + 16 + (uint)r * 16;
            var t = touches[r];
            GuestMemory.Write(rp, ids[r]);
            GuestMemory.Write(rp + 1, (byte)128); // panel force range is 1..128 and there is no pressure input
            GuestMemory.Write(rp + 2, (ushort)Math.Clamp(t.X * 1919, 0, 1919));
            GuestMemory.Write(rp + 4, (ushort)Math.Clamp(yMin + t.Y * (yMax - yMin), yMin, yMax));
        }
        return 1;
    }

    [Hle] int sceTouchPeek(int port, uint data, uint count) => Peek(port, data, count);
    readonly long[] _lastRead = [-1, -1];

    /// <summary>Blocks only until a sample newer than the previous read exists (one per vblank), like the hardware.</summary>
    [Hle]
    int sceTouchRead(int port, uint data, uint count)
    {
        if (port is 0 or 1) _lastRead[port] = VBlank.WaitFor(Math.Max(_lastRead[port] + 1, VBlank.Count));
        return Peek(port, data, count);
    }
    [Hle] int sceTouchSetSamplingState(int port, uint state) => 0;
    [Hle] int sceTouchGetSamplingState(int port, Ptr<uint> state) { state.TrySet(1); return 0; }
    [Hle] int sceTouchEnableTouchForce(int port) => 0;
    [Hle] int sceTouchDisableTouchForce(int port) => 0;
}
