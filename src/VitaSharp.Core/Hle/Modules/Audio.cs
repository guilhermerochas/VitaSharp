using VitaSharp.Core.Util;
using System.Diagnostics;
using VitaSharp.Core.Host;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceAudioOut: ports feed host audio streams; Output() blocks to pace the guest like the real hardware.</summary>
public sealed class Audio : HleModule
{
    const int ERR_INVALID_PORT = unchecked((int)0x80260007), ERR_PORT_FULL = unchecked((int)0x80260109),
        ERR_INVALID_SIZE = unchecked((int)0x80260004), ERR_INVALID_FORMAT = unchecked((int)0x80260108);

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
    int _nextPort;

    [Hle]
    int sceAudioOutOpenPort(int type, int len, int freq, int mode)
    {
        if (len <= 0 || len > 65472 || (len & 63) != 0) return ERR_INVALID_SIZE;
        if (mode is not (0 or 1)) return ERR_INVALID_FORMAT;
        lock (_ports)
        {
            if (_ports.Count >= 8) return ERR_PORT_FULL;
            var p = new Port { Type = type, Grain = len, Freq = freq <= 0 ? 48000 : freq, Channels = mode == 0 ? 1 : 2 };
            try { p.Stream = Emu.Host.Audio?.OpenStream(p.Freq, 2, len); } catch { p.Stream = null; }
            int id = _nextPort++;
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
                    float l = p.Volume[0], r = p.Volume[1];
                    for (int i = 0; i < n; i += 2)
                    {
                        p.Buffer[i] = (short)Math.Clamp(src[i] * l, short.MinValue, short.MaxValue);
                        p.Buffer[i + 1] = (short)Math.Clamp(src[i + 1] * r, short.MinValue, short.MaxValue);
                    }
                }
                else
                {
                    var src = GuestMemory.Span<short>(buf, p.Grain);
                    float v = p.Volume[0];
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
        if (vols == 0) return unchecked((int)0x80260003);
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
        return type switch { 0 => p.Grain, 1 => p.Freq, 2 => p.Channels == 1 ? 0 : 1, _ => unchecked((int)0x80260003) };
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
    [Hle] int sceAudioOutGetAdopt(int type) => 1;
    [Hle] int sceAudioOutSetAlcMode(int mode) => 0;
    [Hle] int sceAudioOutSetEffectType(int type) => 0;

    // microphone: not available
    [Hle] int sceAudioInOpenPort(int type, int grain, int freq, int param) => unchecked((int)0x80260106);
    [Hle] int sceAudioInReleasePort(int port) => 0;
    [Hle] int sceAudioInInput(int port, uint buf) => unchecked((int)0x80260107);
}
