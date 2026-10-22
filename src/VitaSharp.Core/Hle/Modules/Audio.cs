using VitaSharp.Core.Util;
using System.Diagnostics;
using VitaSharp.Core.Host;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceAudioOut: ports feed host audio streams; Output() blocks to pace the guest like the real hardware.</summary>
public sealed class Audio : HleModule
{
    const int ERR_INVALID_PORT = unchecked((int)0x80260003), ERR_INVALID_POINTER = unchecked((int)0x80260004),
        ERR_PORT_FULL = unchecked((int)0x80260005), ERR_INVALID_SIZE = unchecked((int)0x80260006), ERR_INVALID_FORMAT = unchecked((int)0x80260007),
        ERR_INVALID_SAMPLE_FREQ = unchecked((int)0x80260008), ERR_INVALID_PORT_TYPE = unchecked((int)0x8026000A), ERR_INVALID_CONF_TYPE = unchecked((int)0x8026000C);

    sealed class Port
    {
        public int Type, Grain, Freq, Channels;
        public IAudioStream? Stream;
        public float[] Volume = [1, 1];
        public double NextDeadline;
        public short[] Buffer = [];
        public readonly object Lock = new();
    }

    readonly Dictionary<int, Port> _ports = new();
    /// <summary>Handle of the extended MAIN port (sceAudioOutOpenExtPort); normal ports use 0..7.</summary>
    const int ExtPortId = 8;

    [Hle]
    int sceAudioOutOpenPort(int type, int len, int freq, int mode)
    {
        if (type is < 0 or > 2) return ERR_INVALID_PORT_TYPE;
        if (type == 0 && freq != 48000) return ERR_INVALID_SAMPLE_FREQ;
        return OpenPort(type, len, freq, mode, ext: false);
    }

    /// <summary>The process's extended MAIN port (48 kHz), a ninth port next to the eight normal ones.</summary>
    [Hle]
    int sceAudioOutOpenExtPort(int len, int mode) => OpenPort(0, len, 48000, mode, ext: true);

    int OpenPort(int type, int len, int freq, int mode, bool ext)
    {
        if (len <= 0 || len > 65472 || (len & 63) != 0) return ERR_INVALID_SIZE;
        if (mode is not (0 or 1)) return ERR_INVALID_FORMAT;
        lock (_ports)
        {
            int id;
            if (ext) { if (_ports.ContainsKey(ExtPortId)) return ERR_PORT_FULL; id = ExtPortId; }
            else
            {
                id = -1;
                for (int i = 0; i < ExtPortId; i++) if (!_ports.ContainsKey(i)) { id = i; break; } // lowest free handle
                if (id < 0) return ERR_PORT_FULL;
            }
            var p = new Port { Type = type, Grain = len, Freq = freq <= 0 ? 48000 : freq, Channels = mode == 0 ? 1 : 2 };
            try { p.Stream = Emu.Host.Audio?.OpenStream(p.Freq, 2, len); } catch { p.Stream = null; }
            _ports[id] = p;
            return id;
        }
    }

    [Hle]
    int sceAudioOutReleasePort(int port)
    {
        Port? p;
        lock (_ports) { if (!_ports.Remove(port, out p)) return ERR_INVALID_PORT; }
        p.Stream?.Dispose();
        return 0;
    }

    Port? Get(int port) { lock (_ports) return _ports.GetValueOrDefault(port); }

    [Hle]
    int sceAudioOutOutput(int port, uint buf)
    {
        var p = Get(port);
        if (p == null) return ERR_INVALID_PORT;
        lock (p.Lock)
        {
            if (p.Stream != null)
            {
                int n = p.Grain * 2;
                if (p.Buffer.Length != n) p.Buffer = new short[n];
                if (buf == 0) Array.Clear(p.Buffer);
                else if (p.Channels == 2)
                {
                    var src = GuestMemory.Span<short>(buf, n);
                    float g = ProfileGain(p.Type), l = p.Volume[0] * g, r = p.Volume[1] * g;
                    for (int i = 0; i < n; i += 2)
                    {
                        p.Buffer[i] = (short)Math.Clamp(src[i] * l, short.MinValue, short.MaxValue);
                        p.Buffer[i + 1] = (short)Math.Clamp(src[i + 1] * r, short.MinValue, short.MaxValue);
                    }
                }
                else
                {
                    var src = GuestMemory.Span<short>(buf, p.Grain);
                    float v = p.Volume[0] * ProfileGain(p.Type);
                    for (int i = 0; i < p.Grain; i++) p.Buffer[2 * i] = p.Buffer[2 * i + 1] = (short)Math.Clamp(src[i] * v, short.MinValue, short.MaxValue);
                }
                using (Scheduler.Block()) p.Stream.Submit(p.Buffer); // blocks while the host buffer is full: not CPU time
            }
            else
            {
                // no audio device: keep real-time pacing so games run at the right speed (none in turbo mode)
                if (GuestClock.Turbo) return 0;
                double now = GuestClock.Seconds;
                double period = (double)p.Grain / p.Freq;
                if (p.NextDeadline < now - 0.1) p.NextDeadline = now;
                p.NextDeadline += period;
                double wait = p.NextDeadline - now - period; // allow one grain of latency
                if (wait > 0.001) { using var blocked = Scheduler.Block(); PreciseSleep.Microseconds((long)(wait * 1_000_000)); }
            }
        }
        return 0;
    }

    [Hle]
    int sceAudioOutSetVolume(int port, uint flag, uint vols)
    {
        var p = Get(port);
        if (p == null) return ERR_INVALID_PORT;
        if (vols == 0) return ERR_INVALID_POINTER;
        if ((flag & 1) != 0) p.Volume[0] = Math.Clamp(GuestMemory.R32(vols) / 32768f, 0, 1);
        if ((flag & 2) != 0) p.Volume[1] = Math.Clamp(GuestMemory.R32(vols + 4) / 32768f, 0, 1);
        return 0;
    }

    [Hle]
    int sceAudioOutSetConfig(int port, int len, int freq, int mode)
    {
        var p = Get(port);
        if (p == null) return ERR_INVALID_PORT;
        lock (p.Lock)
        {
            if (len > 0) p.Grain = len;
            if (freq > 0) p.Freq = freq;
            if (mode >= 0) p.Channels = mode == 0 ? 1 : 2;
            if (len > 0 || freq > 0)
            {
                p.Stream?.Dispose();
                try { p.Stream = Emu.Host.Audio?.OpenStream(p.Freq, 2, p.Grain); } catch { p.Stream = null; }
            }
        }
        return 0;
    }

    [Hle]
    int sceAudioOutGetConfig(int port, int type)
    {
        var p = Get(port);
        if (p == null) return ERR_INVALID_PORT;
        return type switch { 0 => p.Grain, 1 => p.Freq, 2 => p.Channels == 1 ? 0 : 1, _ => ERR_INVALID_CONF_TYPE };
    }

    /// <summary>Samples queued on the port and not yet played. Bink-style players poll this to pace and sync their
    /// output; a constant 0 made God of War restart its video audio port every 100 ms.</summary>
    [Hle]
    int sceAudioOutGetRestSample(int port)
    {
        var p = Get(port);
        if (p == null) return ERR_INVALID_PORT;
        lock (p.Lock)
        {
            // the hardware port is double-buffered: at most one grain waits behind the one playing. The host queue is
            // deeper to survive scheduling jitter, so report the hardware-visible amount.
            if (p.Stream != null) return Math.Min(p.Stream.PendingSamples, p.Grain * 2);
            double pending = (p.NextDeadline - GuestClock.Seconds) * p.Freq;
            return (int)Math.Clamp(pending, 0, p.Grain * 2);
        }
    }
    // ---- adoption, compression and per-profile gain (MAIN 0, BGM 1, VOICE 2)

    readonly bool[] _adopted = [true, true, true];
    readonly int[] _profileGain = [256, 256, 256];
    int _adoptMode;
    const int ERR_INVALID_VOLUME = unchecked((int)0x80260009);

    float ProfileGain(int type) => type is >= 0 and <= 2 ? _profileGain[type] / 256f : 1f;

    [Hle] int sceAudioOutGetAdopt(int type) => type is >= 0 and <= 2 && !_adopted[type] ? 0 : 1;

    [Hle]
    int sceAudioOutSetAdopt_forUser(int type, int adopt, int rampLength, int waitForCompletion)
    {
        if (type is < 0 or > 2) return ERR_INVALID_PORT_TYPE;
        _adopted[type] = adopt != 0;
        return 0;
    }

    [Hle] int sceAudioOutSetAdoptMode(int mode) { _adoptMode = mode; return 0; }

    /// <summary>Compressed external-codec profile for BGM/VOICE ports: no audible effect on the host.</summary>
    [Hle]
    int sceAudioOutSetCompress(int port, int enable)
    {
        var p = Get(port);
        if (p == null) return ERR_INVALID_PORT;
        if (p.Type == 0) return ERR_INVALID_PORT_TYPE; // MAIN has no compressed profile (code unconfirmed)
        return 0;
    }

    /// <summary>Private 0..256 gain per profile, selected by mask (MAIN 2, BGM 4, VOICE 8); applied on top of the
    /// per-channel volume. The ramp is not emulated.</summary>
    [Hle]
    int sceAudioOutSetPortVolume_forUser(int portMask, uint volume, int rampLength)
    {
        if (volume > 256) return ERR_INVALID_VOLUME;
        for (int t = 0; t < 3; t++)
            if ((portMask & (2 << t)) != 0) _profileGain[t] = (int)volume;
        return 0;
    }

    [Hle] int sceAudioOutGetPortVolume_forUser(int type) => type is >= 0 and <= 2 ? _profileGain[type] : ERR_INVALID_PORT_TYPE;

    [Hle] int sceAudioOutSetAlcMode(int mode) => 0;
    [Hle] int sceAudioOutSetEffectType(int type) => 0;

    // ---- SceAudioIn: virtual microphone (no host capture) that opens and delivers silence at the right pace

    const int IN_ERR_INVALID_SIZE = unchecked((int)0x80260102), IN_ERR_INVALID_SAMPLE_FREQ = unchecked((int)0x80260103),
        IN_ERR_INVALID_PORT_TYPE = unchecked((int)0x80260104), IN_ERR_INVALID_POINTER = unchecked((int)0x80260105),
        IN_ERR_INVALID_PORT_PARAM = unchecked((int)0x80260106), IN_ERR_PORT_FULL = unchecked((int)0x80260107),
        IN_ERR_NOT_OPENED = unchecked((int)0x80260109), IN_ERR_INVALID_PARAMETER = unchecked((int)0x8026010B);
    const int InPortId = 0x80;

    sealed class InPort
    {
        public int Type, Grain, Freq;
        public short MicGain = 0x1060;
        public double NextDeadline;
        public int RouteReports; // CAMERA profile: captures left that report a route change
        public readonly object Lock = new();
    }

    InPort? _inPort;
    bool _micMuted;
    int _inputRoute; // SCE_AUDIO_IN_INPUT_MODE_BUILTIN_MIC

    [Hle] int sceAudioInGetStatus(int select) => select == 1 ? (_micMuted ? 1 : 0) : IN_ERR_INVALID_PARAMETER;

    [Hle]
    int sceAudioInOpenPort(int type, int grain, int freq, int param)
    {
        if (param != 0) return IN_ERR_INVALID_PORT_PARAM; // S16 mono only
        switch (type)
        {
            case 0 or 0x20: // VOICE, CAMERA: 256/512 samples at 16 kHz
                if (freq != 16000) return IN_ERR_INVALID_SAMPLE_FREQ;
                if (grain is not (256 or 512)) return IN_ERR_INVALID_SIZE;
                break;
            case 2: // RAW: 256 at 16 kHz or 768 at 48 kHz
                if (freq is not (16000 or 48000)) return IN_ERR_INVALID_SAMPLE_FREQ;
                if ((freq == 16000 && grain != 256) || (freq == 48000 && grain != 768)) return IN_ERR_INVALID_SIZE;
                break;
            default: return IN_ERR_INVALID_PORT_TYPE;
        }
        lock (this)
        {
            if (_inPort != null) return IN_ERR_PORT_FULL; // one port per process
            _inPort = new InPort { Type = type, Grain = grain, Freq = freq, RouteReports = 2 };
        }
        return InPortId;
    }

    /// <summary>Diagnostic entry (psp2/audioin.h): like sceAudioInOpenPort, and also accepts the DIAG port type (14)
    /// without validating grain/frequency.</summary>
    [Hle]
    int sceAudioInOpenPortForDiag(int type, int grain, int freq, int param)
    {
        if (type != 14) return sceAudioInOpenPort(type, grain, freq, param);
        if (param != 0) return IN_ERR_INVALID_PORT_PARAM;
        lock (this)
        {
            if (_inPort != null) return IN_ERR_PORT_FULL;
            // unvalidated values still have to pace and size the silent capture
            _inPort = new InPort { Type = type, Grain = grain is > 0 and <= 4096 ? grain : 256, Freq = freq > 0 ? freq : 16000, RouteReports = 2 };
        }
        return InPortId;
    }

    InPort? InGet(int port) { lock (this) return port == InPortId ? _inPort : null; }

    [Hle]
    int sceAudioInReleasePort(int port)
    {
        lock (this)
        {
            if (port != InPortId || _inPort == null) return IN_ERR_NOT_OPENED;
            _inPort = null;
        }
        return 0;
    }

    [Hle] int sceAudioInInput(int port, uint dest) => Input(port, dest, 0);

    [Hle] int sceAudioInInputWithInputDeviceState(int port, uint dest, uint deviceState) => Input(port, dest, deviceState);

    /// <summary>Writes grain samples of silence, paced at grain/freq like a capture device (no wait in turbo mode).</summary>
    int Input(int port, uint dest, uint deviceState)
    {
        var p = InGet(port);
        if (p == null) return IN_ERR_NOT_OPENED;
        if (dest == 0) return IN_ERR_INVALID_POINTER;
        lock (p.Lock)
        {
            GuestMemory.Fill(dest, 0, (uint)(p.Grain * 2));
            if (p.Type == 0x20 && deviceState != 0)
            {
                GuestMemory.W32(deviceState, p.RouteReports > 0 ? 1u : 0u);
                if (p.RouteReports > 0) p.RouteReports--;
            }
            if (GuestClock.Turbo) return 0;
            double now = GuestClock.Seconds, period = (double)p.Grain / p.Freq;
            if (p.NextDeadline < now - 0.1) p.NextDeadline = now;
            p.NextDeadline += period;
            double wait = p.NextDeadline - now;
            if (wait > 0.001) { using var blocked = Scheduler.Block(); PreciseSleep.Microseconds((long)(wait * 1_000_000)); }
        }
        return 0;
    }

    [Hle] int sceAudioInGetAdopt(int type) => type is 0 or 2 ? (_inPort != null ? 1 : 0) : IN_ERR_INVALID_PORT_TYPE;
    [Hle] int sceAudioInGetInput() => _inputRoute;

    [Hle]
    int sceAudioInSelectInput(int mode)
    {
        if (_inputRoute == mode) return 0;
        _inputRoute = mode;
        if (_inPort is { } p) p.RouteReports = 2;
        return 0;
    }

    /// <summary>1 = MUTE, 2 = UNMUTE; other commands are ignored and returned unchanged.</summary>
    [Hle]
    int sceAudioInSetMute(int command)
    {
        if (command is not (1 or 2)) return command;
        _micMuted = command == 1;
        return _micMuted ? 1 : 0;
    }

    [Hle]
    int sceAudioInSetMicGain(int port, int gain)
    {
        var p = InGet(port);
        if (p == null) return IN_ERR_INVALID_PORT_TYPE;
        p.MicGain = (short)gain;
        return 0;
    }

    [Hle] int sceAudioInGetMicGain(int port) => InGet(port) is { } p ? p.MicGain : IN_ERR_INVALID_PORT_TYPE;
}
