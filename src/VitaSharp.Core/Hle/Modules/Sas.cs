using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSas (libsas): the PSP-heritage software voice mixer (VAG/HEVAG ADPCM, PCM and noise voices, pitch, ADSR, dry/wet
/// volumes, one effect unit). sceSasCore writes one grain of interleaved stereo S16 at 48 kHz into a game buffer.
/// There is no header: the prototypes are the PSP ones without the SasCore pointer plus a config string at init
/// (docs/hle/G1-media-libs.md), and the error codes are the PSP's 0x8042xxxx. sceSysmoduleLoadModule(SCE_SYSMODULE_SAS)
/// prefers the real libsas.suprx by LLE when present; this HLE is the fallback.
/// </summary>
public sealed class Sas : HleModule
{
    internal const int ERR_INVALID_GRAIN = unchecked((int)0x80420001), ERR_INVALID_MAX_VOICES = unchecked((int)0x80420002),
        ERR_INVALID_OUTPUT_MODE = unchecked((int)0x80420003), ERR_BAD_ADDRESS = unchecked((int)0x80420005),
        ERR_INVALID_VOICE = unchecked((int)0x80420010), ERR_INVALID_NOISE_FREQ = unchecked((int)0x80420011),
        ERR_INVALID_PITCH = unchecked((int)0x80420012), ERR_INVALID_ADSR_CURVE_MODE = unchecked((int)0x80420013),
        ERR_INVALID_PARAMETER = unchecked((int)0x80420014), ERR_INVALID_LOOP_POS = unchecked((int)0x80420015),
        ERR_VOICE_PAUSED = unchecked((int)0x80420016), ERR_INVALID_VOLUME = unchecked((int)0x80420018),
        ERR_INVALID_ADSR_RATE = unchecked((int)0x80420019), ERR_INVALID_PCM_SIZE = unchecked((int)0x8042001A),
        ERR_REV_INVALID_TYPE = unchecked((int)0x80420020), ERR_REV_INVALID_FEEDBACK = unchecked((int)0x80420021),
        ERR_REV_INVALID_DELAY_TIME = unchecked((int)0x80420022), ERR_REV_INVALID_VOLUME = unchecked((int)0x80420023),
        ERR_NOT_INIT = unchecked((int)0x80420100), ERR_ALREADY_INIT = unchecked((int)0x80420101);
    const int VOL_MAX = 0x1000, PITCH_MIN = 1, PITCH_MAX = 0x4000, NOISE_MAX = 63, MAX_VOICES = 256;
    const int DEFAULT_GRAIN = 256, DEFAULT_VOICES = 32;

    readonly object _lock = new();
    SasEngine? _engine;
    int _maxGrain;
    uint _buffer, _bufferSize;

    internal SasEngine? Engine => _engine;

    record struct SasConfig(int Grains, bool GrainsGiven, int Voices, int Reverbs);

    /// <summary>"numGrains=256 numVoices=32 numReverbs=1": whitespace/comma separated key=value pairs; unknown keys are
    /// ignored. Keys and defaults are from the SDK's sample usage (unconfirmed).</summary>
    static int ParseConfig(uint config, out SasConfig cfg)
    {
        cfg = new SasConfig(DEFAULT_GRAIN, false, DEFAULT_VOICES, 1);
        if (config == 0) return ERR_BAD_ADDRESS;
        string s = GuestMemory.ReadCString(config, 1024);
        foreach (var tok in s.Split([' ', '\t', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = tok.IndexOf('=');
            if (eq <= 0 || !TryParseInt(tok[(eq + 1)..], out int v)) { Log.WarnOnce("sas-cfg-" + tok, $"sceSas: unparsed config token '{tok}'"); continue; }
            switch (tok[..eq].ToLowerInvariant())
            {
                case "numgrains": cfg = cfg with { Grains = v, GrainsGiven = true }; break;
                case "numvoices": cfg = cfg with { Voices = v }; break;
                case "numreverbs": cfg = cfg with { Reverbs = v }; break;
                default: Log.WarnOnce("sas-cfg-" + tok, $"sceSas: unknown config key '{tok}'"); break;
            }
        }
        if (cfg.Voices is < 1 or > MAX_VOICES) return ERR_INVALID_MAX_VOICES;
        if (cfg.GrainsGiven && !ValidGrain(cfg.Grains, SasEngine.MaxGrain)) return ERR_INVALID_GRAIN;
        return 0;
    }

    static bool TryParseInt(string s, out int v) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out v)
            : int.TryParse(s, out v);

    /// <summary>PSP rule: 64..2048 samples in steps of 32 (and here also at most the init's numGrains).</summary>
    static bool ValidGrain(int grain, int max) => grain >= 64 && grain <= Math.Min(max, SasEngine.MaxGrain) && grain % 32 == 0;

    [Hle]
    int sceSasGetNeededMemorySize(uint config, Ptr<uint> outSize)
    {
        int r = ParseConfig(config, out var cfg);
        if (r != 0) return r;
        if (outSize.IsNull) return ERR_BAD_ADDRESS;
        // the HLE keeps its state on the host: any stable size works
        outSize.Value = (uint)(0x1000 + cfg.Voices * 0x100 + cfg.Grains * 8 + 0xFF) & ~0xFFu;
        return 0;
    }

    [Hle] int sceSasInit(uint config, uint buffer, uint bufferSize) => InitCore(config, 0, buffer, bufferSize);
    [Hle] int sceSasInitWithGrain(uint config, uint grain, uint buffer, uint bufferSize) => InitCore(config, (int)grain, buffer, bufferSize, true);

    int InitCore(uint config, int grain, uint buffer, uint bufferSize, bool explicitGrain = false)
    {
        int r = ParseConfig(config, out var cfg);
        if (r != 0) return r;
        if (buffer == 0) return ERR_BAD_ADDRESS;
        int max = cfg.GrainsGiven ? cfg.Grains : SasEngine.MaxGrain;
        if (!explicitGrain) grain = cfg.Grains;
        else if (!ValidGrain(grain, max)) return ERR_INVALID_GRAIN;
        lock (_lock)
        {
            if (_engine != null) return ERR_ALREADY_INIT;
            _engine = new SasEngine(grain, cfg.Voices);
            _maxGrain = max;
            _buffer = buffer;
            _bufferSize = bufferSize;
        }
        Log.Debug($"sceSasInit: grain {grain}, {cfg.Voices} voices, {cfg.Reverbs} reverbs");
        return 0;
    }

    [Hle]
    int sceSasExit(Ptr<uint> outBuffer, Ptr<uint> outBufferSize)
    {
        lock (_lock)
        {
            if (_engine == null) return ERR_NOT_INIT;
            outBuffer.TrySet(_buffer);
            outBufferSize.TrySet(_bufferSize);
            _engine = null;
        }
        return 0;
    }

    // ---- helpers

    int WithEngine(Func<SasEngine, int> f)
    {
        lock (_lock) return _engine == null ? ERR_NOT_INIT : f(_engine);
    }

    int WithVoice(int voice, Func<SasVoice, int> f)
    {
        lock (_lock)
        {
            if (_engine == null) return ERR_NOT_INIT;
            if (voice < 0 || voice >= _engine.Voices.Length) return ERR_INVALID_VOICE;
            return f(_engine.Voices[voice]);
        }
    }

    // ---- mixing

    [Hle]
    int sceSasCore(uint output) => Core(output, false, VOL_MAX, VOL_MAX);

    [Hle]
    int sceSasCoreWithMix(uint inOut, int lvol, int rvol)
    {
        if (Math.Abs(lvol) > VOL_MAX || Math.Abs(rvol) > VOL_MAX) return _engine == null ? ERR_NOT_INIT : ERR_INVALID_VOLUME;
        return Core(inOut, true, lvol, rvol);
    }

    int Core(uint output, bool mix, int lvol, int rvol) => WithEngine(e =>
    {
        if (output == 0) return ERR_BAD_ADDRESS;
        if (e.OutputMode != 0) Log.WarnOnce("sas-outmode", $"sceSasCore: output mode {e.OutputMode} rendered as stereo");
        e.Mix(GuestMemory.Span<short>(output, e.Grain * 2), mix, lvol, rvol);
        return 0;
    });

    // ---- voices

    [Hle]
    int sceSasSetVoice(int iVoiceNum, uint vagBuf, uint size, uint loopflag) => WithVoice(iVoiceNum, v =>
    {
        if (size == 0 || (size & 0xF) != 0 || loopflag > 1) return ERR_INVALID_PARAMETER;
        if (vagBuf == 0) return ERR_BAD_ADDRESS;
        // a playing voice keeps its position when the game only refreshes size/loop on the same data
        bool changed = v.Type != SasVoiceType.Vag || v.Addr != vagBuf;
        v.Type = SasVoiceType.Vag;
        v.Addr = vagBuf;
        v.Size = size;
        v.Loop = loopflag != 0;
        if (changed && v.On) v.ResetSource();
        return 0;
    });

    [Hle]
    int sceSasSetVoicePCM(int iVoiceNum, uint pcmBuf, int size, int loopsize) => WithVoice(iVoiceNum, v =>
    {
        if (size <= 0) return ERR_INVALID_PCM_SIZE;
        if (loopsize > size) return ERR_INVALID_LOOP_POS;
        if (pcmBuf == 0) return ERR_BAD_ADDRESS;
        bool changed = v.Type != SasVoiceType.Pcm || v.Addr != pcmBuf;
        v.Type = SasVoiceType.Pcm;
        v.Addr = pcmBuf;
        v.Size = (uint)size;
        v.PcmLoopSize = Math.Max(0, loopsize);
        if (changed && v.On) v.ResetSource();
        return 0;
    });

    [Hle]
    int sceSasSetNoise(int iVoiceNum, uint uClk) => WithVoice(iVoiceNum, v =>
    {
        if (uClk > NOISE_MAX) return ERR_INVALID_NOISE_FREQ;
        v.Type = SasVoiceType.Noise;
        v.NoiseClock = (int)uClk;
        return 0;
    });

    [Hle]
    int sceSasSetPitch(int iVoiceNum, int pitch) => WithVoice(iVoiceNum, v =>
    {
        if (pitch is < PITCH_MIN or > PITCH_MAX) return ERR_INVALID_PITCH;
        v.Pitch = pitch;
        return 0;
    });

    [Hle]
    int sceSasSetVolume(int iVoiceNum, int l, int r, int wl, int wr) => WithVoice(iVoiceNum, v =>
    {
        if (Math.Abs(l) > VOL_MAX || Math.Abs(r) > VOL_MAX || Math.Abs(wl) > VOL_MAX || Math.Abs(wr) > VOL_MAX) return ERR_INVALID_VOLUME;
        v.VolL = l; v.VolR = r; v.WetL = wl; v.WetR = wr;
        return 0;
    });

    [Hle]
    int sceSasSetDistortion(int iVoiceNum, int distortion) => WithVoice(iVoiceNum, v =>
    {
        Log.WarnOnce("sas-distortion", "sceSasSetDistortion: stored, not applied (semantics unknown)");
        v.Distortion = distortion;
        return 0;
    });

    [Hle]
    int sceSasSetADSR(int iVoiceNum, uint flag, uint a, uint d, uint s, uint r) => WithVoice(iVoiceNum, v =>
    {
        uint[] rates = [a, d, s, r];
        for (int i = 0; i < 4; i++)
            if ((flag & (1u << i)) != 0 && rates[i] > int.MaxValue) return ERR_INVALID_ADSR_RATE;
        for (int i = 0; i < 4; i++)
            if ((flag & (1u << i)) != 0) v.Env.Rates[i] = (int)rates[i];
        return 0;
    });

    [Hle]
    int sceSasSetADSRmode(int iVoiceNum, uint flag, uint a, uint d, uint s, uint r) => WithVoice(iVoiceNum, v =>
    {
        uint[] modes = [a, d, s, r];
        for (int i = 0; i < 4; i++)
            if ((flag & (1u << i)) != 0 && !SasEnvelope.ValidMode(i, modes[i])) return ERR_INVALID_ADSR_CURVE_MODE;
        for (int i = 0; i < 4; i++)
            if ((flag & (1u << i)) != 0) v.Env.Modes[i] = (int)modes[i];
        return 0;
    });

    [Hle]
    int sceSasSetSL(int iVoiceNum, uint sl) => WithVoice(iVoiceNum, v =>
    {
        if (sl > SasEnvelope.Max) return ERR_INVALID_PARAMETER;
        v.Env.SustainLevel = (int)sl;
        return 0;
    });

    [Hle]
    int sceSasSetSimpleADSR(int iVoiceNum, uint adsr1, uint adsr2) => WithVoice(iVoiceNum, v =>
    {
        v.Env.SetSimple(adsr1 & 0xFFFF, adsr2 & 0xFFFF);
        return 0;
    });

    [Hle]
    int sceSasSetKeyOn(int iVoiceNum) => WithVoice(iVoiceNum, v =>
    {
        if (v.Paused) return ERR_VOICE_PAUSED;
        v.KeyOn();
        return 0;
    });

    [Hle]
    int sceSasSetKeyOff(int iVoiceNum) => WithVoice(iVoiceNum, v =>
    {
        if (v.Paused) return ERR_VOICE_PAUSED;
        v.KeyOff();
        return 0;
    });

    [Hle]
    int sceSasSetPause(int iVoiceNum, uint pauseFlag) => WithVoice(iVoiceNum, v => { v.Paused = pauseFlag != 0; return 0; });

    [Hle] int sceSasGetEndState(int iVoiceNum) => WithVoice(iVoiceNum, v => v.Ended ? 1 : 0);
    [Hle] int sceSasGetPauseState(int iVoiceNum) => WithVoice(iVoiceNum, v => v.Paused ? 1 : 0);
    [Hle] int sceSasGetEnvelope(int iVoiceNum) => WithVoice(iVoiceNum, v => v.Env.Height);

    // ---- effect

    /// <summary>Nonzero switches are treated as on (the PSP accepts only 0/1; unconfirmed on the Vita).</summary>
    [Hle] int sceSasSetEffect(int drySwitch, int wetSwitch) => WithEngine(e => { e.DryOn = drySwitch != 0; e.WetOn = wetSwitch != 0; return 0; });

    [Hle]
    int sceSasSetEffectType(int type) => WithEngine(e =>
    {
        if (type is < SasEngine.EffectOff or > 8) return ERR_REV_INVALID_TYPE;
        if (type is not (SasEngine.EffectOff or SasEngine.EffectEcho or SasEngine.EffectDelay))
            Log.WarnOnce("sas-reverb" + type, $"sceSasSetEffectType({type}): reverb not modelled, the wet send passes through");
        if (e.EffectType != type) e.ClearEffect();
        e.EffectType = type;
        return 0;
    });

    [Hle]
    int sceSasSetEffectParam(uint delayTime, uint feedback) => WithEngine(e =>
    {
        if (delayTime > 127) return ERR_REV_INVALID_DELAY_TIME;
        if (feedback > 127) return ERR_REV_INVALID_FEEDBACK;
        e.EffectDelayTime = (int)delayTime;
        e.EffectFeedback = (int)feedback;
        return 0;
    });

    [Hle]
    int sceSasSetEffectVolume(int valL, int valR) => WithEngine(e =>
    {
        if (Math.Abs(valL) > VOL_MAX || Math.Abs(valR) > VOL_MAX) return ERR_REV_INVALID_VOLUME;
        e.EffectVolL = valL; e.EffectVolR = valR;
        return 0;
    });

    // ---- global settings

    [Hle]
    int sceSasSetGrain(uint grain) => WithEngine(e =>
    {
        if (!ValidGrain((int)Math.Min(grain, int.MaxValue), _maxGrain)) return ERR_INVALID_GRAIN;
        e.Grain = (int)grain;
        return 0;
    });

    [Hle] int sceSasGetGrain() => WithEngine(e => e.Grain);

    [Hle]
    int sceSasSetOutputmode(uint outputmode) => WithEngine(e =>
    {
        if (outputmode > 1) return ERR_INVALID_OUTPUT_MODE;
        e.OutputMode = (int)outputmode;
        return 0;
    });

    [Hle] int sceSasGetOutputmode() => WithEngine(e => e.OutputMode);

    [Hle]
    int sceSasGetDryPeak(Ptr<int> pPeakL, Ptr<int> pPeakR) => Peak(pPeakL, pPeakR, e => (e.DryPeakL, e.DryPeakR));
    [Hle]
    int sceSasGetWetPeak(Ptr<int> pPeakL, Ptr<int> pPeakR) => Peak(pPeakL, pPeakR, e => (e.WetPeakL, e.WetPeakR));
    [Hle]
    int sceSasGetPreMasterPeak(Ptr<int> pPeakL, Ptr<int> pPeakR) => Peak(pPeakL, pPeakR, e => (e.PreMasterPeakL, e.PreMasterPeakR));

    int Peak(Ptr<int> l, Ptr<int> r, Func<SasEngine, (int, int)> get) => WithEngine(e =>
    {
        if (l.IsNull && r.IsNull) return ERR_BAD_ADDRESS;
        var (pl, pr) = get(e);
        l.TrySet(pl);
        r.TrySet(pr);
        return 0;
    });
}
