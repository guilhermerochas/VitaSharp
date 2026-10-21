using Silk.NET.OpenAL;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>OpenAL Soft output: one streaming source per guest port.</summary>
public sealed unsafe class OpenAlAudio : IAudioBackend
{
    readonly AL _al;
    readonly ALContext _alc;
    readonly Device* _device;
    readonly Context* _context;
    readonly object _lock = new();

    OpenAlAudio(AL al, ALContext alc, Device* device, Context* context)
    {
        _al = al; _alc = alc; _device = device; _context = context;
    }

    public static OpenAlAudio? TryCreate()
    {
        try
        {
            var alc = ALContext.GetApi(true);
            var al = AL.GetApi(true);
            var dev = alc.OpenDevice("");
            if (dev == null) { Log.Warn("OpenAL: no output device"); return null; }
            var ctx = alc.CreateContext(dev, null);
            alc.MakeContextCurrent(ctx);
            Log.Info("Audio: OpenAL Soft");
            return new OpenAlAudio(al, alc, dev, ctx);
        }
        catch (Exception e)
        {
            Log.Warn($"OpenAL unavailable: {e.Message}");
            return null;
        }
    }

    public IAudioStream OpenStream(int sampleRate, int channels, int grainSamples)
    {
        lock (_lock)
        {
            _alc.MakeContextCurrent(_context);
            return new Stream(this, sampleRate, grainSamples);
        }
    }

    /// <summary>Times an output stream ran dry and was restarted (diagnostics for audio/video pacing).</summary>
    public static int Underruns;

    sealed class Stream : IAudioStream
    {
        // the Vita output is double-buffered: sceAudioOutOutput returns once the previous grain finished, so at most one
        // grain is queued behind the one playing; games pace and A/V-sync against that latency
        const int MaxQueued = 4; // host-side depth (jitter tolerance); the guest-visible depth is capped in sceAudioOutGetRestSample
        readonly OpenAlAudio _o;
        readonly uint _source;
        readonly int _rate, _grain;
        readonly Queue<uint> _free = new();
        int _queued;
        float _volume = 1;

        public Stream(OpenAlAudio o, int rate, int grain)
        {
            _o = o;
            _rate = rate;
            _grain = grain;
            _source = o._al.GenSource();
            for (int i = 0; i < MaxQueued + 2; i++) _free.Enqueue(o._al.GenBuffer());
        }

        public float Volume
        {
            get => _volume;
            set { _volume = value; lock (_o._lock) _o._al.SetSourceProperty(_source, SourceFloat.Gain, value); }
        }

        void Reclaim()
        {
            _o._al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
            while (processed-- > 0)
            {
                uint b = 0;
                _o._al.SourceUnqueueBuffers(_source, 1, &b);
                _free.Enqueue(b);
                _queued--;
            }
        }

        public void Submit(ReadOnlySpan<short> interleaved)
        {
            while (true)
            {
                lock (_o._lock)
                {
                    _o._alc.MakeContextCurrent(_o._context);
                    Reclaim();
                    if (_queued < MaxQueued && _free.Count > 0)
                    {
                        uint b = _free.Dequeue();
                        fixed (short* p = interleaved)
                            _o._al.BufferData(b, BufferFormat.Stereo16, p, interleaved.Length * 2, _rate);
                        _o._al.SourceQueueBuffers(_source, 1, &b);
                        _queued++;
                        _o._al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
                        if (state != (int)SourceState.Playing)
                        {
                            // Stopped (not Initial): the queue ran dry, the guest did not submit in time (audible gap)
                            if (state == (int)SourceState.Stopped && Interlocked.Increment(ref Underruns) is var n && (n <= 10 || n % 100 == 0))
                                Log.Warn($"Audio: host stream underrun #{n} (guest output late)");
                            _o._al.SourcePlay(_source);
                        }
                        return;
                    }
                }
                Thread.Sleep(1);
            }
        }

        public int PendingSamples
        {
            get
            {
                lock (_o._lock)
                {
                    _o._alc.MakeContextCurrent(_o._context);
                    Reclaim();
                    _o._al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int played);
                    return Math.Max(0, _queued * _grain - played);
                }
            }
        }

        public void Dispose()
        {
            lock (_o._lock)
            {
                _o._alc.MakeContextCurrent(_o._context);
                _o._al.SourceStop(_source);
                Reclaim();
                _o._al.DeleteSource(_source);
                foreach (var b in _free) _o._al.DeleteBuffer(b);
            }
        }
    }

    public void Dispose()
    {
        _alc.MakeContextCurrent(null);
        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);
    }
}
