using VitaSharp.Core.Host;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceCamera: the two Vita cameras (0 = front, 1 = back), backed by the host camera in <see cref="HostServices.Camera"/>.
/// Without a host camera the device still works and delivers neutral grey frames (face/scene libraries then simply
/// detect nothing). Frames are paced by the requested frame rate on the guest clock; each guest frame samples the
/// latest host frame, scaled (centre crop) and converted to the requested format. Semantics follow psp2/camera.h and
/// Vita3K's SceCamera ("from disasm" validation rules).
/// </summary>
public sealed class Camera : HleModule
{
    const int ERR_PARAM = unchecked((int)0x802E0000);
    const int ERR_ALREADY_OPEN = unchecked((int)0x802E0003);
    const int ERR_NOT_OPEN = unchecked((int)0x802E0004);
    const int ERR_ALREADY_START = unchecked((int)0x802E0005);
    const int ERR_NOT_START = unchecked((int)0x802E0006);
    const int ERR_FORMAT_UNKNOWN = unchecked((int)0x802E0007);
    const int ERR_RESOLUTION_UNKNOWN = unchecked((int)0x802E0008);
    const int ERR_BAD_FRAMERATE = unchecked((int)0x802E0009);
    const int ERR_ATTRIBUTE_UNKNOWN = unchecked((int)0x802E000C);
    const int ERR_DATA_RANGE_UNKNOWN = unchecked((int)0x802E0011);
    const int ERR_OTHER_ALREADY_START = unchecked((int)0x802E0012);

    // SceCameraInfo (0x30 bytes)
    const uint INFO_SIZE = 0x00, INFO_PRIORITY = 0x04, INFO_FORMAT = 0x06, INFO_RESOLUTION = 0x08, INFO_FRAMERATE = 0x0A,
        INFO_WIDTH = 0x0C, INFO_HEIGHT = 0x0E, INFO_RANGE = 0x10, INFO_SIZE_I = 0x14, INFO_SIZE_U = 0x18, INFO_SIZE_V = 0x1C,
        INFO_I = 0x20, INFO_U = 0x24, INFO_V = 0x28, INFO_PITCH = 0x2C, INFO_BUFFER = 0x2E;
    const uint INFO_STRUCT_SIZE = 0x30;

    // SceCameraRead (0x38 bytes)
    const uint READ_SIZE = 0x00, READ_MODE = 0x04, READ_STATUS = 0x0C, READ_FRAME = 0x10, READ_TIMESTAMP = 0x18,
        READ_SIZE_I = 0x20, READ_SIZE_U = 0x24, READ_SIZE_V = 0x28, READ_I = 0x2C, READ_U = 0x30, READ_V = 0x34;
    const uint READ_STRUCT_SIZE = 0x38;

    const int READ_MODE_WAIT_NEXTFRAME_ON = 0;
    const int STATUS_IS_ACTIVE = 0, STATUS_IS_ALREADY_READ = 1;
    const int BUFFER_SETBYOPEN = 0, BUFFER_SETBYREAD = 1;
    const int DATA_RANGE_FULL = 0;

    enum Attr
    {
        Saturation, Brightness, Contrast, Sharpness, Reverse, Effect, EV, Zoom, AntiFlicker, ISO, WhiteBalance,
        Backlight, Nightmode, ExposureCeiling, AutoControlHold, ImageQuality, NoiseReduction, SharpnessOff, Count,
    }

    // defaults from Vita3K camera.cpp (Camera::get_attribute)
    static readonly int[] DefaultAttrs = [10, 128, 128, 1, 0, 0, 0, 10, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>SCE_CAMERA_RESOLUTION_* → pixel size (0 = unsupported).</summary>
    public static (int W, int H) ResolutionSize(int r) => r switch
    {
        1 => (640, 480), 2 => (320, 240), 3 => (160, 120), 4 => (352, 288), 5 => (176, 144), 6 => (480, 272), 8 => (640, 360),
        _ => (0, 0),
    };

    /// <summary>SCE_CAMERA_FRAMERATE_* → frames per second (3 = 3.75 fps, 7 = 7.5 fps; 0 = unsupported).</summary>
    public static double FrameRate(int f) => f switch
    {
        3 => 3.75, 5 => 5, 7 => 7.5, 10 => 10, 15 => 15, 20 => 20, 30 => 30, 60 => 60, 120 => 120,
        _ => 0,
    };

    /// <summary>Line pitch in pixels. Games pass 0 (tight rows); treat a value ≥ width as the full pitch and a
    /// smaller one as padding added to the width.</summary>
    public static int StridePixels(int width, int pitch) => pitch <= 0 ? width : pitch >= width ? pitch : width + pitch;

    sealed class Device
    {
        public bool Open, Started;
        public int Priority, Format, Resolution, Framerate, Range, Pitch, BufferMode, Width, Height;
        public uint IBase, UBase, VBase, ISize, USize, VSize;
        public readonly int[] Attrs = (int[])DefaultAttrs.Clone();
        public long StartTicks, PeriodTicks;
        public ulong LastFrame, LastTimestamp;
        public ICameraStream? Stream;
        public bool LoggedRead;
        // latest host frame and its scaled copy (re-scaled only when the host frame or the output shape changes)
        public byte[] HostRgba = [], Scaled = [];
        public long ScaledSeq = -1;
        public int ScaledW, ScaledH, ScaledReverse = -1, ScaledEffect = -1;
    }

    readonly Device[] _dev = [new(), new()];
    readonly object _lock = new();

    /// <summary>Unit tests run the module without an <see cref="Emulator"/> (one guest address space per process).</summary>
    internal HostServices? HostOverride;
    HostServices Host => HostOverride ?? Emu.Host;

    public override void Initialize()
    {
        // a process exec recreates the modules: release the host captures the previous process left running
        ReleaseAllStreams();
    }

    void ReleaseAllStreams()
    {
        var list = Host.CameraStreams;
        lock (list)
        {
            foreach (var s in list) { try { s.Dispose(); } catch (Exception e) { Log.Warn($"Camera: closing host stream failed: {e.Message}"); } }
            list.Clear();
        }
    }

    static bool ValidDev(int dev) => dev is 0 or 1;

    // ------------------------------------------------------------------ open / close

    [Hle]
    int sceCameraOpen(int devnum, uint pInfo)
    {
        if (!ValidDev(devnum) || pInfo == 0) return ERR_PARAM;
        uint size = GuestMemory.R32(pInfo + INFO_SIZE);
        if (size < INFO_PITCH || size > INFO_STRUCT_SIZE) return ERR_PARAM;
        int priority = GuestMemory.Read<ushort>(pInfo + INFO_PRIORITY);
        int format = GuestMemory.Read<ushort>(pInfo + INFO_FORMAT);
        int resolution = GuestMemory.Read<ushort>(pInfo + INFO_RESOLUTION);
        int framerate = GuestMemory.Read<ushort>(pInfo + INFO_FRAMERATE);
        int range = GuestMemory.Read<ushort>(pInfo + INFO_RANGE);
        bool hasTail = size >= INFO_STRUCT_SIZE;
        int pitch = hasTail ? GuestMemory.Read<ushort>(pInfo + INFO_PITCH) : 0;
        int buffer = hasTail ? GuestMemory.Read<ushort>(pInfo + INFO_BUFFER) : BUFFER_SETBYOPEN;
        uint sizeI = GuestMemory.R32(pInfo + INFO_SIZE_I), sizeU = GuestMemory.R32(pInfo + INFO_SIZE_U), sizeV = GuestMemory.R32(pInfo + INFO_SIZE_V);
        uint pI = GuestMemory.R32(pInfo + INFO_I), pU = GuestMemory.R32(pInfo + INFO_U), pV = GuestMemory.R32(pInfo + INFO_V);

        if (priority > 1) return ERR_PARAM;
        if (buffer is not (BUFFER_SETBYOPEN or BUFFER_SETBYREAD)) return ERR_PARAM;
        if (format is <= VitaCameraFormat.Invalid or > VitaCameraFormat.Raw8) return ERR_FORMAT_UNKNOWN;
        if (buffer == BUFFER_SETBYOPEN)
        {
            if (pI == 0 || sizeI == 0) return ERR_PARAM;
            if (VitaCameraFormat.IsPlanar(format) && (pU == 0 || sizeU == 0 || pV == 0 || sizeV == 0)) return ERR_PARAM;
        }
        if (FrameRate(framerate) == 0) return ERR_BAD_FRAMERATE;
        if (range > 1) return ERR_DATA_RANGE_UNKNOWN;
        var (w, h) = ResolutionSize(resolution);
        if (w == 0) return ERR_RESOLUTION_UNKNOWN;

        lock (_lock)
        {
            var d = _dev[devnum];
            if (d.Open) return ERR_ALREADY_OPEN;
            d.Open = true;
            d.Started = false;
            d.Priority = priority; d.Format = format; d.Resolution = resolution; d.Framerate = framerate; d.Range = range;
            d.Pitch = pitch; d.BufferMode = buffer; d.Width = w; d.Height = h;
            d.IBase = pI; d.UBase = pU; d.VBase = pV; d.ISize = sizeI; d.USize = sizeU; d.VSize = sizeV;
            d.ScaledSeq = -1;
            d.LoggedRead = false;
        }
        GuestMemory.Write(pInfo + INFO_WIDTH, (ushort)w);
        GuestMemory.Write(pInfo + INFO_HEIGHT, (ushort)h);
        Log.Info($"Camera: open {(devnum == 0 ? "front" : "back")} {w}x{h} format={format} {FrameRate(framerate)} fps range={range} pitch={pitch} buffer={(buffer == 0 ? "open" : "read")}");
        return 0;
    }

    [Hle]
    int sceCameraClose(int devnum)
    {
        if (!ValidDev(devnum)) return ERR_PARAM;
        lock (_lock)
        {
            var d = _dev[devnum];
            if (!d.Open) return ERR_NOT_OPEN;
            if (d.Started) StopDevice(d);
            d.Open = false;
        }
        return 0;
    }

    // ------------------------------------------------------------------ start / stop

    [Hle]
    int sceCameraStart(int devnum)
    {
        if (!ValidDev(devnum)) return ERR_PARAM;
        lock (_lock)
        {
            var d = _dev[devnum];
            if (!d.Open) return ERR_NOT_OPEN;
            if (d.Started) return ERR_ALREADY_START;
            if (_dev[devnum ^ 1].Started) return ERR_OTHER_ALREADY_START; // one sensor active at a time
            double fps = FrameRate(d.Framerate);
            d.Started = true;
            d.PeriodTicks = Math.Max(1, GuestClock.FromSeconds(1.0 / fps));
            d.StartTicks = GuestClock.Ticks;
            d.LastFrame = 0;
            d.LastTimestamp = 0;
            d.ScaledSeq = -1;
            d.Stream = OpenHostStream(devnum, d.Width, d.Height, (int)Math.Ceiling(fps));
        }
        return 0;
    }

    ICameraStream? OpenHostStream(int devnum, int w, int h, int fps)
    {
        var src = Host.Camera;
        if (src == null) { Log.Info("Camera: no host camera, delivering grey frames"); return null; }
        ICameraStream? s = null;
        try { s = src.Open((CameraFacing)devnum, w, h, fps); }
        catch (Exception e) { Log.Warn($"Camera: host camera failed to open: {e.Message}"); }
        if (s == null) { Log.Info("Camera: host camera unavailable, delivering grey frames"); return null; }
        lock (Host.CameraStreams) Host.CameraStreams.Add(s);
        return s;
    }

    void StopDevice(Device d)
    {
        d.Started = false;
        if (d.Stream is { } s)
        {
            d.Stream = null;
            lock (Host.CameraStreams) Host.CameraStreams.Remove(s);
            try { s.Dispose(); } catch (Exception e) { Log.Warn($"Camera: closing host stream failed: {e.Message}"); }
        }
    }

    [Hle]
    int sceCameraStop(int devnum)
    {
        if (!ValidDev(devnum)) return ERR_PARAM;
        lock (_lock)
        {
            var d = _dev[devnum];
            if (!d.Open) return ERR_NOT_OPEN;
            if (!d.Started) return ERR_NOT_START;
            StopDevice(d);
        }
        return 0;
    }

    [Hle]
    int sceCameraIsActive(int devnum)
    {
        if (!ValidDev(devnum)) return ERR_PARAM;
        lock (_lock)
        {
            var d = _dev[devnum];
            if (!d.Open) return ERR_NOT_OPEN;
            if (!d.Started) return ERR_NOT_START;
            return 1;
        }
    }

    // ------------------------------------------------------------------ read

    [Hle]
    int sceCameraRead(int devnum, uint pRead)
    {
        if (!ValidDev(devnum) || pRead == 0) return ERR_PARAM;
        uint size = GuestMemory.R32(pRead + READ_SIZE);
        if (size <= 24 || size > READ_STRUCT_SIZE) return ERR_PARAM;
        int mode = (int)GuestMemory.R32(pRead + READ_MODE);
        var d = _dev[devnum];
        long target;
        lock (_lock)
        {
            if (!d.Open) return ERR_NOT_OPEN;
            if (!d.Started) return ERR_NOT_START;
            if (d.BufferMode == BUFFER_SETBYREAD)
            {
                if (size < READ_STRUCT_SIZE || GuestMemory.R32(pRead + READ_I) == 0) return ERR_PARAM;
                if (VitaCameraFormat.IsPlanar(d.Format) && (GuestMemory.R32(pRead + READ_U) == 0 || GuestMemory.R32(pRead + READ_V) == 0)) return ERR_PARAM;
            }
            target = d.StartTicks + (long)(d.LastFrame + 1) * d.PeriodTicks;
        }
        // wait for the next frame (outside the lock: other guest threads may use the other device meanwhile)
        if (mode == READ_MODE_WAIT_NEXTFRAME_ON && GuestClock.Ticks < target)
        {
            using var blocked = Scheduler.Block();
            GuestClock.WaitUntil(target);
        }
        lock (_lock)
        {
            if (!d.Open) return ERR_NOT_OPEN;
            if (!d.Started) return ERR_NOT_START;
            ulong available = (ulong)Math.Max(0, (GuestClock.Ticks - d.StartTicks) / d.PeriodTicks);
            int status;
            if (available <= d.LastFrame)
                status = STATUS_IS_ALREADY_READ; // no-wait mode and no new frame yet: previous frame number/timestamp
            else
            {
                d.LastFrame = available;
                d.LastTimestamp = (ulong)(GuestClock.Microseconds * TimeScale.Proc); // = sceKernelGetProcessTimeWide
                status = STATUS_IS_ACTIVE;
                DeliverFrame(d, pRead);
            }
            GuestMemory.W32(pRead + READ_STATUS, (uint)status);
            if (size >= READ_FRAME + 8) GuestMemory.Write(pRead + READ_FRAME, d.LastFrame);
            if (size >= READ_TIMESTAMP + 8) GuestMemory.Write(pRead + READ_TIMESTAMP, d.LastTimestamp);
        }
        return 0;
    }

    void DeliverFrame(Device d, uint pRead)
    {
        uint pI = d.IBase, pU = d.UBase, pV = d.VBase, sI = d.ISize, sU = d.USize, sV = d.VSize;
        if (d.BufferMode == BUFFER_SETBYREAD)
        {
            pI = GuestMemory.R32(pRead + READ_I); pU = GuestMemory.R32(pRead + READ_U); pV = GuestMemory.R32(pRead + READ_V);
            sI = GuestMemory.R32(pRead + READ_SIZE_I); sU = GuestMemory.R32(pRead + READ_SIZE_U); sV = GuestMemory.R32(pRead + READ_SIZE_V);
        }
        int stride = StridePixels(d.Width, d.Pitch);
        var need = CameraImage.PlaneSizes(d.Format, d.Width, d.Height, stride);
        // never write more than the guest declared (a zero size means "as big as the format needs")
        int nI = pI == 0 ? 0 : sI == 0 ? need.I : (int)Math.Min(sI, (uint)need.I);
        int nU = pU == 0 ? 0 : sU == 0 ? need.U : (int)Math.Min(sU, (uint)need.U);
        int nV = pV == 0 ? 0 : sV == 0 ? need.V : (int)Math.Min(sV, (uint)need.V);
        if (!d.LoggedRead)
        {
            d.LoggedRead = true;
            Log.Debug($"Camera: first read {d.Width}x{d.Height} format={d.Format} stride={stride} I={pI:X8}/{sI} U={pU:X8}/{sU} V={pV:X8}/{sV} host={(d.Stream != null ? "yes" : "grey")}");
        }
        var rgba = CurrentImage(d);
        CameraImage.Encode(rgba, d.Width, d.Height, d.Format, d.Range == DATA_RANGE_FULL, stride,
            nI > 0 ? GuestMemory.Span(pI, nI) : default, nU > 0 ? GuestMemory.Span(pU, nU) : default, nV > 0 ? GuestMemory.Span(pV, nV) : default);
    }

    /// <summary>The latest host frame at the device's output size (or neutral grey without a host frame).</summary>
    byte[] CurrentImage(Device d)
    {
        long seq = 0;
        int hw = 0, hh = 0;
        if (d.Stream != null && !d.Stream.Frames.CopyLatest(ref d.HostRgba, out hw, out hh, out seq)) seq = 0;
        int reverse = d.Attrs[(int)Attr.Reverse] & 3, effect = d.Attrs[(int)Attr.Effect];
        if (seq == d.ScaledSeq && d.ScaledW == d.Width && d.ScaledH == d.Height && reverse == d.ScaledReverse && effect == d.ScaledEffect)
            return d.Scaled;
        int n = d.Width * d.Height * 4;
        if (d.Scaled.Length != n) d.Scaled = new byte[n];
        if (seq == 0 || hw <= 0 || hh <= 0) d.Scaled.AsSpan().Fill(128);
        else CameraImage.Resample(d.HostRgba, hw, hh, d.Scaled, d.Width, d.Height, mirror: (reverse & 1) != 0, flip: (reverse & 2) != 0);
        if (effect != 0) ApplyEffect(d.Scaled, effect);
        d.ScaledSeq = seq; d.ScaledW = d.Width; d.ScaledH = d.Height; d.ScaledReverse = reverse; d.ScaledEffect = effect;
        return d.Scaled;
    }

    /// <summary>SCE_CAMERA_EFFECT_*: 1 negative, 2 black and white, 3 sepia, 4 blue, 5 red, 6 green.</summary>
    static void ApplyEffect(Span<byte> rgba, int effect)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            int y = (77 * r + 150 * g + 29 * b) >> 8;
            switch (effect)
            {
                case 1: r = 255 - r; g = 255 - g; b = 255 - b; break;
                case 2: r = g = b = y; break;
                case 3: r = Math.Min(255, y + 40); g = Math.Min(255, y + 20); b = Math.Max(0, y - 20); break;
                case 4: r = y * 3 / 4; g = y * 3 / 4; b = Math.Min(255, y + 48); break;
                case 5: r = Math.Min(255, y + 48); g = y * 3 / 4; b = y * 3 / 4; break;
                case 6: r = y * 3 / 4; g = Math.Min(255, y + 48); b = y * 3 / 4; break;
                default: return;
            }
            rgba[i] = (byte)r; rgba[i + 1] = (byte)g; rgba[i + 2] = (byte)b;
        }
    }

    // ------------------------------------------------------------------ attributes

    int SetAttr(int devnum, Attr a, int value)
    {
        if (!ValidDev(devnum)) return ERR_PARAM;
        lock (_lock)
        {
            var d = _dev[devnum];
            if (!d.Open) return ERR_NOT_OPEN;
            if (!d.Started) return ERR_NOT_START;
            d.Attrs[(int)a] = value;
        }
        return 0;
    }

    int GetAttr(int devnum, Attr a, uint pValue)
    {
        if (!ValidDev(devnum) || pValue == 0) return ERR_PARAM;
        lock (_lock)
        {
            var d = _dev[devnum];
            if (!d.Open) return ERR_NOT_OPEN;
            if (!d.Started) return ERR_NOT_START;
            GuestMemory.W32(pValue, (uint)d.Attrs[(int)a]);
        }
        return 0;
    }

    [Hle] int sceCameraSetSaturation(int dev, int v) => SetAttr(dev, Attr.Saturation, v);
    [Hle] int sceCameraGetSaturation(int dev, uint p) => GetAttr(dev, Attr.Saturation, p);
    [Hle] int sceCameraSetBrightness(int dev, int v) => SetAttr(dev, Attr.Brightness, v);
    [Hle] int sceCameraGetBrightness(int dev, uint p) => GetAttr(dev, Attr.Brightness, p);
    [Hle] int sceCameraSetContrast(int dev, int v) => SetAttr(dev, Attr.Contrast, v);
    [Hle] int sceCameraGetContrast(int dev, uint p) => GetAttr(dev, Attr.Contrast, p);
    [Hle] int sceCameraSetSharpness(int dev, int v) => SetAttr(dev, Attr.Sharpness, v);
    [Hle] int sceCameraGetSharpness(int dev, uint p) => GetAttr(dev, Attr.Sharpness, p);
    [Hle] int sceCameraSetReverse(int dev, int v) => SetAttr(dev, Attr.Reverse, v);
    [Hle] int sceCameraGetReverse(int dev, uint p) => GetAttr(dev, Attr.Reverse, p);
    [Hle] int sceCameraSetEffect(int dev, int v) => SetAttr(dev, Attr.Effect, v);
    [Hle] int sceCameraGetEffect(int dev, uint p) => GetAttr(dev, Attr.Effect, p);
    [Hle] int sceCameraSetEV(int dev, int v) => SetAttr(dev, Attr.EV, v);
    [Hle] int sceCameraGetEV(int dev, uint p) => GetAttr(dev, Attr.EV, p);
    [Hle] int sceCameraSetZoom(int dev, int v) => SetAttr(dev, Attr.Zoom, v);
    [Hle] int sceCameraGetZoom(int dev, uint p) => GetAttr(dev, Attr.Zoom, p);
    [Hle] int sceCameraSetAntiFlicker(int dev, int v) => SetAttr(dev, Attr.AntiFlicker, v);
    [Hle] int sceCameraGetAntiFlicker(int dev, uint p) => GetAttr(dev, Attr.AntiFlicker, p);
    [Hle] int sceCameraSetISO(int dev, int v) => SetAttr(dev, Attr.ISO, v);
    [Hle] int sceCameraGetISO(int dev, uint p) => GetAttr(dev, Attr.ISO, p);
    [Hle] int sceCameraSetWhiteBalance(int dev, int v) => SetAttr(dev, Attr.WhiteBalance, v);
    [Hle] int sceCameraGetWhiteBalance(int dev, uint p) => GetAttr(dev, Attr.WhiteBalance, p);
    [Hle] int sceCameraSetBacklight(int dev, int v) => SetAttr(dev, Attr.Backlight, v);
    [Hle] int sceCameraGetBacklight(int dev, uint p) => GetAttr(dev, Attr.Backlight, p);
    [Hle] int sceCameraSetNightmode(int dev, int v) => SetAttr(dev, Attr.Nightmode, v);
    [Hle] int sceCameraGetNightmode(int dev, uint p) => GetAttr(dev, Attr.Nightmode, p);
    [Hle] int sceCameraSetExposureCeiling(int dev, int v) => SetAttr(dev, Attr.ExposureCeiling, v);
    [Hle] int sceCameraGetExposureCeiling(int dev, uint p) => GetAttr(dev, Attr.ExposureCeiling, p);
    [Hle] int sceCameraSetAutoControlHold(int dev, int v) => SetAttr(dev, Attr.AutoControlHold, v);
    [Hle] int sceCameraGetAutoControlHold(int dev, uint p) => GetAttr(dev, Attr.AutoControlHold, p);
    [Hle] int sceCameraSetImageQuality(int dev, int v) => SetAttr(dev, Attr.ImageQuality, v);
    [Hle] int sceCameraGetImageQuality(int dev, uint p) => GetAttr(dev, Attr.ImageQuality, p);
    [Hle] int sceCameraSetNoiseReduction(int dev, int v) => SetAttr(dev, Attr.NoiseReduction, v);
    [Hle] int sceCameraGetNoiseReduction(int dev, uint p) => GetAttr(dev, Attr.NoiseReduction, p);
    [Hle] int sceCameraSetSharpnessOff(int dev, int v) => SetAttr(dev, Attr.SharpnessOff, v);
    [Hle] int sceCameraGetSharpnessOff(int dev, uint p) => GetAttr(dev, Attr.SharpnessOff, p);

    // gain can be neither read nor written on the Vita (Vita3K: "code from disasm")
    [Hle] int sceCameraSetGain(int dev, int v) => ValidDev(dev) ? ERR_ATTRIBUTE_UNKNOWN : ERR_PARAM;
    [Hle] int sceCameraGetGain(int dev, uint p) => ValidDev(dev) && p != 0 ? ERR_ATTRIBUTE_UNKNOWN : ERR_PARAM;

    /// <summary>Physical camera position (SceFVector3, mm). Real values unknown: (0, 0, 0) like Vita3K.</summary>
    [Hle]
    int sceCameraGetDeviceLocation(int dev, uint pLocation)
    {
        if (!ValidDev(dev) || pLocation == 0) return ERR_PARAM;
        GuestMemory.Fill(pLocation, 0, 12);
        return 0;
    }
}
