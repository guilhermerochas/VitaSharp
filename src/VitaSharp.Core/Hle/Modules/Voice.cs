using VitaSharp.Core.Host;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceVoice: voice-chat port graph (psp2/voice.h, FW 3.60 behaviour). Input ports (microphone, app PCM, encoded
/// voice from the network) are connected to output ports (app PCM, encoded voice to send, speaker) and mixed every
/// 10 ms. There is no CELP codec (FFmpeg has none) and no host microphone: IN_DEVICE contributes silence (like the
/// virtual SceAudioIn mic), IN_VOICE frames are accepted and decode to silence, OUT_VOICE produces correctly sized
/// zero frames. IN_PCMAUDIO to OUT_PCMAUDIO/OUT_DEVICE is a real mix (volume, mute, s16 clamp).
/// Processing has no thread of its own: it runs lazily on each port call from the elapsed guest time.
/// </summary>
public sealed class Voice : HleModule
{
    internal const int ERR_NOT_INITIALIZED = unchecked((int)0x804E0801), ERR_ALREADY_INITIALIZED = unchecked((int)0x804E0802),
        ERR_INVALID_PORT_ID = unchecked((int)0x804E0804), ERR_INVALID_ARGUMENT = unchecked((int)0x804E0805),
        ERR_INVALID_MEMBLOCK = unchecked((int)0x804E0806), ERR_INVALID_PORT = unchecked((int)0x804E0807),
        ERR_RESOURCE_LIMIT = unchecked((int)0x804E0808), ERR_NOT_STARTED = unchecked((int)0x804E080A);

    internal const uint IN_DEVICE = 0, IN_PCMAUDIO = 1, IN_VOICE = 2, OUT_PCMAUDIO = 3, OUT_VOICE = 4, OUT_DEVICE = 5;
    internal const uint STATE_INACTIVE = 0, STATE_IDLE = 1, STATE_BUFFERING = 2, STATE_ACTIVE = 3;
    const uint Version = 100, AttrAudioInputOwnership = 1000;
    const int MaxPorts = 64, MaxVoiceIn = 7, MaxVoiceOut = 7, MaxDeviceIn = 1, MaxDeviceOut = 1, MaxConnections = 128;
    /// <summary>Processing tick: 10 ms of 16 kHz mono S16 = 160 samples / 320 bytes.</summary>
    const int TickMs = 10, TickSamples = 160, TickBytes = TickSamples * 2;
    const int VoiceRingFrames = 16;
    const int MaxCatchUpTicks = 100;

    /// <summary>CELP bitrate → (bytes per frame, frame duration in ms).</summary>
    static (int Bytes, int Ms)? VoiceFrame(uint bitRate) => bitRate switch
    {
        3850 => (20, 40), 4650 => (24, 40), 5700 => (15, 20), 7300 => (19, 20), 12200 => (16, 10), _ => null,
    };

    sealed class Port
    {
        public uint Type;
        public ushort Threshold;
        public bool Mute;
        public float Volume;
        public uint BitRate;
        public int FrameBytes = TickBytes, FrameMs = TickMs;
        public uint State, Reported;
        public bool Paused;
        public byte[] Ring = [];
        public int Head, Count;
        public int AccumMs;      // OUT_VOICE: PCM time gathered for the current frame; IN_VOICE: time played of the current frame
        public bool AccumSignal; // OUT_VOICE: an input produced audio during the current frame
        public IAudioStream? Stream;
        public bool StreamTried;

        public bool IsInput => Type <= IN_VOICE;
        public bool IsVoice => Type is IN_VOICE or OUT_VOICE;
        public int Free => Ring.Length - Count;

        public int Write(ReadOnlySpan<byte> src)
        {
            int n = Math.Min(src.Length, Free);
            for (int i = 0; i < n; i++) Ring[(Head + Count + i) % Ring.Length] = src[i];
            Count += n;
            return n;
        }

        public int Read(Span<byte> dst)
        {
            int n = Math.Min(dst.Length, Count);
            for (int i = 0; i < n; i++) dst[i] = Ring[(Head + i) % Ring.Length];
            Head = Ring.Length == 0 ? 0 : (Head + n) % Ring.Length;
            Count -= n;
            return n;
        }

        /// <summary>Output ring full: the oldest data is dropped to make room.</summary>
        public void Push(ReadOnlySpan<byte> src)
        {
            if (Ring.Length == 0) return;
            int over = src.Length - Free;
            if (over > 0) { Head = (Head + over) % Ring.Length; Count -= over; }
            Write(src);
        }

        public void Clear() { Head = Count = 0; AccumMs = 0; AccumSignal = false; }
    }

    readonly object _lock = new();
    readonly Port?[] _ports = new Port?[MaxPorts];
    readonly List<(int In, int Out)> _connections = new();
    bool _initialized, _started, _muteAll, _allPaused;
    uint _eventCallback, _userData;
    double _lastProcess;

    /// <summary>user_data given to sceVoiceInit (copied into SceVoiceEvent when events are delivered).</summary>
    internal uint UserData => _userData;

    /// <summary>Guest time source in seconds (tests replace it).</summary>
    internal Func<double> Clock = () => GuestClock.Seconds;
    /// <summary>Validates sceVoiceStart's mem_block_id (tests replace it).</summary>
    internal Func<int, bool>? MemBlockExists;

    bool IsMemBlock(int uid) => MemBlockExists != null ? MemBlockExists(uid) : uid > 0 && K.Objects.Get<MemBlock>(uid) != null;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>SceVoiceInitParam (0x20): +0 application_type, +4 event_callback, +8 user_data, +0xC reserved[5].</summary>
    [Hle]
    int sceVoiceInit(uint param, uint version)
    {
        lock (_lock)
        {
            if (_initialized) return ERR_ALREADY_INITIALIZED;
            if (param == 0 || version != Version) return ERR_INVALID_ARGUMENT;
            uint appType = GuestMemory.R32(param);
            if (appType is not (0x20000000 or 0x80000000))
                Log.WarnOnce("voice-apptype", $"sceVoiceInit: unknown application_type 0x{appType:X8}");
            _eventCallback = GuestMemory.R32(param + 4);
            _userData = GuestMemory.R32(param + 8);
            if (_eventCallback != 0)
                Log.WarnOnce("voice-events", "sceVoice: event callback registered but events are not delivered (poll-only HLE)");
            _initialized = true;
            _started = _muteAll = _allPaused = false;
            return 0;
        }
    }

    [Hle]
    int sceVoiceEnd()
    {
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            StopLocked();
            for (int i = 0; i < MaxPorts; i++) { _ports[i]?.Stream?.Dispose(); _ports[i] = null; }
            _connections.Clear();
            _initialized = false;
            _eventCallback = _userData = 0;
            return 0;
        }
    }

    /// <summary>SceVoiceStartParam (0x20): +0 mem_block_id. The working memory is not touched (state is host-side).</summary>
    [Hle]
    int sceVoiceStart(uint param)
    {
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            if (param == 0) return ERR_INVALID_ARGUMENT;
            if (_started) return 0;
            if (!IsMemBlock((int)GuestMemory.R32(param))) return ERR_INVALID_MEMBLOCK;
            _started = true;
            _lastProcess = Clock();
            foreach (var p in _ports)
                if (p != null) { StartPort(p); p.Reported = p.State; }
            return 0;
        }
    }

    [Hle]
    int sceVoiceStop()
    {
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            StopLocked();
            return 0;
        }
    }

    void StopLocked()
    {
        if (!_started) return;
        _started = false;
        foreach (var p in _ports)
        {
            if (p == null) continue;
            p.Clear();
            p.State = p.Reported = STATE_INACTIVE;
            p.Stream?.Dispose();
            p.Stream = null;
            p.StreamTried = false;
        }
    }

    /// <summary>Processing state a port gets when processing starts.</summary>
    void StartPort(Port p)
    {
        p.Clear();
        p.State = p.Type is IN_PCMAUDIO or IN_VOICE ? STATE_IDLE : STATE_ACTIVE;
    }

    [Hle]
    int sceVoiceGetResourceInfo(uint info)
    {
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            if (info == 0) return ERR_INVALID_ARGUMENT;
        }
        GuestMemory.Write(info, (ushort)MaxVoiceIn);
        GuestMemory.Write(info + 2, (ushort)MaxVoiceOut);
        GuestMemory.Write(info + 4, (ushort)MaxDeviceIn);
        GuestMemory.Write(info + 6, (ushort)MaxDeviceOut);
        GuestMemory.Write(info + 8, (ushort)MaxPorts);
        return 0;
    }

    // ------------------------------------------------------------------ ports

    /// <summary>Common port lookup: NOT_INITIALIZED, then INVALID_PORT_ID for an out-of-range or empty slot.</summary>
    Port? Get(uint id, out int error)
    {
        error = 0;
        if (!_initialized) { error = ERR_NOT_INITIALIZED; return null; }
        if (id >= MaxPorts || _ports[id] == null) { error = ERR_INVALID_PORT_ID; return null; }
        return _ports[id];
    }

    int CountType(uint type) => _ports.Count(p => p != null && p.Type == type);

    /// <summary>SceVoicePortParam (0x18): +0 port_type, +4 threshold (u16 ms), +6 mute_flag (u16), +8 volume (float),
    /// +0xC buffer_size / bit_rate, +0x10 pcm_data_type, +0x14 sampling_rate.</summary>
    [Hle]
    int sceVoiceCreatePort(uint portId, uint param)
    {
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            if (portId == 0 || param == 0) return ERR_INVALID_ARGUMENT;
            GuestMemory.W32(portId, 0xFF);
            uint type = GuestMemory.R32(param), data = GuestMemory.R32(param + 0xC);
            if (type > OUT_DEVICE) return ERR_INVALID_ARGUMENT;
            var p = new Port
            {
                Type = type,
                Threshold = GuestMemory.Read<ushort>(param + 4),
                Mute = GuestMemory.Read<ushort>(param + 6) != 0,
                Volume = GuestMemory.Read<float>(param + 8),
            };
            int bufferMs;
            switch (type)
            {
                case IN_PCMAUDIO:
                case OUT_PCMAUDIO:
                {
                    if (GuestMemory.R32(param + 0x10) != 0 || GuestMemory.R32(param + 0x14) != 16000) return ERR_INVALID_ARGUMENT;
                    uint cap = data & ~511u;
                    if (cap is < 1024 or > 64512) return ERR_INVALID_ARGUMENT;
                    p.Ring = new byte[cap];
                    bufferMs = (int)(cap / 32); // 16000 Hz * 2 bytes = 32 bytes per ms
                    break;
                }
                case IN_VOICE:
                case OUT_VOICE:
                {
                    if (VoiceFrame(data) is not { } f) return ERR_INVALID_ARGUMENT;
                    p.BitRate = data;
                    (p.FrameBytes, p.FrameMs) = f;
                    p.Ring = new byte[VoiceRingFrames * f.Bytes];
                    bufferMs = VoiceRingFrames * f.Ms;
                    break;
                }
                default:
                    if (data != 0) return ERR_INVALID_ARGUMENT;
                    bufferMs = 0;
                    break;
            }
            if (type is IN_PCMAUDIO or IN_VOICE && p.Threshold >= bufferMs) return ERR_INVALID_ARGUMENT;
            int limit = type switch { IN_VOICE => MaxVoiceIn, OUT_VOICE => MaxVoiceOut, IN_DEVICE => MaxDeviceIn, OUT_DEVICE => MaxDeviceOut, _ => MaxPorts };
            if (CountType(type) >= limit) return ERR_RESOURCE_LIMIT;
            int slot = Array.IndexOf(_ports, null);
            if (slot < 0) return ERR_RESOURCE_LIMIT;
            _ports[slot] = p;
            if (_started) { StartPort(p); p.Reported = p.State; }
            GuestMemory.W32(portId, (uint)slot);
            return 0;
        }
    }

    [Hle]
    int sceVoiceUpdatePort(uint id, uint param)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            if (param == 0 || GuestMemory.R32(param) != p.Type) return ERR_INVALID_ARGUMENT;
            if (p.IsVoice)
            {
                uint rate = GuestMemory.R32(param + 0xC);
                if (VoiceFrame(rate) is not { } f) return ERR_INVALID_ARGUMENT;
                SetBitRate(p, rate, f);
            }
            // PCM buffer_size is ignored: FW 3.60 never reallocates the ring on update; threshold is not revalidated
            p.Threshold = GuestMemory.Read<ushort>(param + 4);
            p.Mute = GuestMemory.Read<ushort>(param + 6) != 0;
            p.Volume = GuestMemory.Read<float>(param + 8);
            return 0;
        }
    }

    static void SetBitRate(Port p, uint rate, (int Bytes, int Ms) f)
    {
        if (rate == p.BitRate) return;
        p.BitRate = rate;
        (p.FrameBytes, p.FrameMs) = f;
        p.Ring = new byte[VoiceRingFrames * f.Bytes];
        p.Clear();
        if (p.State == STATE_ACTIVE && p.Type == IN_VOICE) p.State = STATE_IDLE;
    }

    [Hle]
    int sceVoiceDeletePort(uint id)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            p.Stream?.Dispose();
            _connections.RemoveAll(c => c.In == id || c.Out == id);
            _ports[id] = null;
            return 0;
        }
    }

    [Hle]
    int sceVoiceConnectIPortToOPort(uint input, uint output)
    {
        lock (_lock)
        {
            var i = Get(input, out int err);
            if (i == null) return err;
            var o = Get(output, out err);
            if (o == null) return err;
            if (!i.IsInput || o.IsInput) return ERR_INVALID_PORT;
            if (i.Type == IN_VOICE && o.Type == OUT_VOICE) return ERR_INVALID_PORT;
            if (_connections.Contains(((int)input, (int)output))) return ERR_INVALID_ARGUMENT;
            if (_connections.Count >= MaxConnections) return ERR_RESOURCE_LIMIT;
            _connections.Add(((int)input, (int)output));
            return 0;
        }
    }

    [Hle]
    int sceVoiceDisconnectIPortFromOPort(uint input, uint output)
    {
        lock (_lock)
        {
            if (Get(input, out int err) == null || Get(output, out err) == null) return err;
            return _connections.Remove(((int)input, (int)output)) ? 0 : ERR_INVALID_ARGUMENT;
        }
    }

    /// <summary>FW 3.60 rules: every port has a connection; adjacent OUT_VOICE ports (creation order) share bitrate,
    /// mute state and volume; with both IN_DEVICE and IN_PCMAUDIO present, an OUT_VOICE fed from IN_PCMAUDIO is
    /// rejected while processing is stopped (its encoder is not assigned yet). Error codes per rule to confirm.</summary>
    [Hle]
    int sceVoiceCheckTopology()
    {
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            for (int id = 0; id < MaxPorts; id++)
                if (_ports[id] != null && !_connections.Any(c => c.In == id || c.Out == id)) return ERR_INVALID_PORT;
            Port? prev = null;
            foreach (var p in _ports)
            {
                if (p is not { Type: OUT_VOICE }) continue;
                if (prev != null && (prev.BitRate != p.BitRate || prev.Mute != p.Mute || prev.Volume != p.Volume)) return ERR_INVALID_PORT;
                prev = p;
            }
            if (!_started && CountType(IN_DEVICE) > 0 && CountType(IN_PCMAUDIO) > 0
                && _connections.Any(c => _ports[c.In]!.Type == IN_PCMAUDIO && _ports[c.Out]!.Type == OUT_VOICE))
                return ERR_INVALID_PORT;
            return 0;
        }
    }

    // ------------------------------------------------------------------ data

    [Hle]
    int sceVoiceWriteToIPort(uint id, uint data, uint size, uint frameGap)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            if (p.Type is not (IN_PCMAUDIO or IN_VOICE)) return ERR_INVALID_PORT;
            if (data == 0 || size == 0) return ERR_INVALID_ARGUMENT;
            if (!_started) { GuestMemory.W32(size, 0); return ERR_NOT_STARTED; }
            Process();
            int want = (int)Math.Min(GuestMemory.R32(size), (uint)p.Free);
            if (p.Type == IN_VOICE)
            {
                want -= want % p.FrameBytes; // whole frames only; the CELP payload cannot be decoded and plays as silence
                if ((short)frameGap != 0)
                    Log.DebugOnce("voice-framegap", $"sceVoiceWriteToIPort: frame_gap {(short)frameGap} accounted as silence");
            }
            else want &= ~1;
            int n = p.Write(GuestMemory.Span(data, want));
            GuestMemory.W32(size, (uint)n);
            if (p.State is STATE_IDLE or STATE_BUFFERING && !p.Paused)
                p.State = p.Count >= ThresholdBytes(p) ? STATE_ACTIVE : STATE_BUFFERING;
            return 0;
        }
    }

    /// <summary>Bytes that must be buffered before an app-fed input starts playing: threshold, at least two frames.</summary>
    static int ThresholdBytes(Port p)
    {
        int ms = Math.Max(p.Threshold, 2 * p.FrameMs);
        int bytes = p.Type == IN_PCMAUDIO ? ms * 32 : (ms + p.FrameMs - 1) / p.FrameMs * p.FrameBytes;
        return Math.Min(bytes, p.Ring.Length);
    }

    [Hle]
    int sceVoiceReadFromOPort(uint id, uint data, uint size)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            if (p.Type is not (OUT_PCMAUDIO or OUT_VOICE)) return ERR_INVALID_PORT;
            if (data == 0 || size == 0) return ERR_INVALID_ARGUMENT;
            if (!_started) { GuestMemory.W32(size, 0); return ERR_NOT_STARTED; }
            Process();
            int want = (int)Math.Min(GuestMemory.R32(size), (uint)p.Count);
            want -= p.Type == OUT_VOICE ? want % p.FrameBytes : want & 1;
            int n = p.Read(GuestMemory.Span(data, want));
            GuestMemory.W32(size, (uint)n);
            return 0;
        }
    }

    /// <summary>SceVoicePortInfo (0x18): +0 port_type, +4 state, +0xC data_size, +0x10 frame_size (reserved words untouched).</summary>
    [Hle]
    int sceVoiceGetPortInfo(uint id, uint info)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            if (info == 0) return ERR_INVALID_ARGUMENT;
            if (!_started) { GuestMemory.Fill(info, 0, 0x18); return ERR_NOT_STARTED; }
            Process();
            if (!p.Paused && !_allPaused) p.Reported = p.State; // paused ports keep reporting their previous state
            uint dataSize = p.Type switch
            {
                IN_PCMAUDIO or IN_VOICE => (uint)p.Free,
                OUT_PCMAUDIO or OUT_VOICE => (uint)p.Count,
                _ => 0,
            };
            GuestMemory.W32(info, p.Type);
            GuestMemory.W32(info + 4, p.Reported);
            GuestMemory.W32(info + 0xC, dataSize);
            GuestMemory.W32(info + 0x10, (uint)p.FrameBytes);
            return 0;
        }
    }

    [Hle]
    int sceVoiceResetPort(uint id)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            p.Clear();
            if (p.State == STATE_ACTIVE && p.Type is IN_PCMAUDIO or IN_VOICE) p.State = STATE_IDLE;
            return 0;
        }
    }

    /// <summary>
    /// Runs the 10 ms processing ticks that elapsed since the last call: every input yields one tick of PCM (app PCM
    /// from its ring, silence for the microphone and decoded voice), each output mixes its connected inputs.
    /// </summary>
    void Process()
    {
        if (!_started) return;
        double now = Clock();
        int ticks = (int)Math.Min(Math.Floor((now - _lastProcess) * 1000 / TickMs + 1e-6), int.MaxValue); // epsilon: accumulated float error
        if (ticks <= 0) return;
        if (_allPaused) { _lastProcess = now; return; }
        if (ticks > MaxCatchUpTicks) { ticks = MaxCatchUpTicks; _lastProcess = now; }
        else _lastProcess += ticks * TickMs / 1000.0;
        var frames = new short[MaxPorts][];
        var produced = new bool[MaxPorts];
        var bytes = new byte[TickBytes];
        var mix = new int[TickSamples];
        var pcm = new short[TickSamples];
        for (int t = 0; t < ticks; t++)
        {
            // inputs
            for (int id = 0; id < MaxPorts; id++)
            {
                produced[id] = false;
                var p = _ports[id];
                if (p is not { IsInput: true } || p.Paused) continue;
                switch (p.Type)
                {
                    case IN_DEVICE:
                        produced[id] = true; // no host microphone: silence
                        frames[id] = null!;
                        break;
                    case IN_PCMAUDIO:
                        if (p.State != STATE_ACTIVE) break;
                        int got = p.Read(bytes);
                        if (got == 0) { p.State = STATE_IDLE; break; }
                        bytes.AsSpan(got).Clear();
                        frames[id] ??= new short[TickSamples];
                        for (int i = 0; i < TickSamples; i++) frames[id][i] = BitConverter.ToInt16(bytes, i * 2);
                        produced[id] = true;
                        if (p.Count == 0) p.State = STATE_IDLE;
                        break;
                    case IN_VOICE:
                        if (p.State != STATE_ACTIVE) break;
                        produced[id] = true; // decodes to silence
                        frames[id] = null!;
                        p.AccumMs += TickMs;
                        if (p.AccumMs >= p.FrameMs)
                        {
                            p.AccumMs = 0;
                            p.Read(new byte[p.FrameBytes]);
                            if (p.Count == 0) p.State = STATE_IDLE;
                        }
                        break;
                }
            }
            // outputs
            for (int id = 0; id < MaxPorts; id++)
            {
                var o = _ports[id];
                if (o is not { IsInput: false } || o.Paused) continue;
                Array.Clear(mix);
                bool any = false;
                foreach (var c in _connections)
                {
                    if (c.Out != id || !produced[c.In]) continue;
                    any = true;
                    var i = _ports[c.In]!;
                    if (frames[c.In] is not { } f || i.Mute) continue;
                    float g = i.Volume * o.Volume;
                    for (int s = 0; s < TickSamples; s++) mix[s] += (int)(f[s] * g);
                }
                if (o.Mute) Array.Clear(mix);
                switch (o.Type)
                {
                    case OUT_PCMAUDIO:
                        if (!any) break;
                        for (int s = 0; s < TickSamples; s++) BitConverter.TryWriteBytes(bytes.AsSpan(s * 2), (short)Math.Clamp(mix[s], short.MinValue, short.MaxValue));
                        o.Push(bytes);
                        break;
                    case OUT_VOICE:
                        o.AccumSignal |= any;
                        o.AccumMs += TickMs;
                        if (o.AccumMs >= o.FrameMs)
                        {
                            if (o.AccumSignal) o.Push(new byte[o.FrameBytes]); // CELP frame of the right size, zero payload
                            o.AccumMs = 0;
                            o.AccumSignal = false;
                        }
                        break;
                    case OUT_DEVICE:
                        if (!o.StreamTried)
                        {
                            o.StreamTried = true;
                            try { o.Stream = Emu?.Host.Audio?.OpenStream(16000, 1, TickSamples); } catch { o.Stream = null; }
                        }
                        // never block the guest: drop the tick when the host queue already holds ~100 ms
                        if (any && o.Stream is { } st && st.PendingSamples < 1600)
                        {
                            for (int s = 0; s < TickSamples; s++) pcm[s] = (short)Math.Clamp(mix[s], short.MinValue, short.MaxValue);
                            st.Submit(pcm);
                        }
                        break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ simple attributes

    [Hle]
    int sceVoiceSetMuteFlagAll(uint mute)
    {
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            _muteAll = mute != 0; // only what GetMuteFlag reports: processing reads the per-port flag
            return 0;
        }
    }

    [Hle]
    int sceVoiceSetMuteFlag(uint id, uint mute)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            p.Mute = mute != 0;
            return 0;
        }
    }

    [Hle]
    int sceVoiceGetMuteFlag(uint id, uint mute)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            if (mute == 0) return ERR_INVALID_ARGUMENT;
            GuestMemory.Write(mute, (ushort)(_muteAll || p.Mute ? 1 : 0));
            return 0;
        }
    }

    [Hle]
    int sceVoiceSetVolume(uint id, float volume)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            p.Volume = volume; // range not limited; samples are clamped when mixed
            return 0;
        }
    }

    [Hle]
    int sceVoiceGetVolume(uint id, uint volume)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            if (volume == 0) return ERR_INVALID_ARGUMENT;
            GuestMemory.Write(volume, p.Volume);
            return 0;
        }
    }

    [Hle]
    int sceVoiceSetBitRate(uint id, uint bitRate)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            if (!p.IsVoice) return ERR_INVALID_PORT;
            if (VoiceFrame(bitRate) is not { } f) return ERR_INVALID_ARGUMENT;
            SetBitRate(p, bitRate, f);
            return 0;
        }
    }

    [Hle]
    int sceVoiceGetBitRate(uint id, uint bitRate)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            if (!p.IsVoice) return ERR_INVALID_PORT;
            if (bitRate == 0) return ERR_INVALID_ARGUMENT;
            GuestMemory.W32(bitRate, p.BitRate);
            return 0;
        }
    }

    /// <summary>FW 3.60 rejects every attribute of an existing port.</summary>
    [Hle]
    int sceVoiceSetPortAttr(uint id, uint attr, uint value)
    {
        lock (_lock)
            return Get(id, out int err) == null ? err : ERR_INVALID_ARGUMENT;
    }

    /// <summary>AUDIO_INPUT_OWNERSHIP is defined only for IN_DEVICE (no host microphone: 0 = not owned); other port
    /// types succeed without writing.</summary>
    [Hle]
    int sceVoiceGetPortAttr(uint id, uint attr, uint value)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            if (attr != AttrAudioInputOwnership || value == 0) return ERR_INVALID_ARGUMENT;
            if (p.Type == IN_DEVICE) GuestMemory.W32(value, 0);
            return 0;
        }
    }

    [Hle]
    int sceVoicePausePort(uint id)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            Process();
            p.Paused = true;
            if (!p.IsInput || p.Type == IN_DEVICE) p.State = STATE_INACTIVE; // the reported state stays frozen
            return 0;
        }
    }

    [Hle]
    int sceVoiceResumePort(uint id)
    {
        lock (_lock)
        {
            var p = Get(id, out int err);
            if (p == null) return err;
            Process();
            if (p.Paused && _started && (!p.IsInput || p.Type == IN_DEVICE)) p.State = STATE_ACTIVE;
            p.Paused = false;
            return 0;
        }
    }

    [Hle]
    int sceVoicePausePortAll()
    {
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            Process();
            _allPaused = true;
            return 0;
        }
    }

    [Hle]
    int sceVoiceResumePortAll()
    {
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            _allPaused = false;
            _lastProcess = Clock(); // do not catch up the paused time
            return 0;
        }
    }
}
