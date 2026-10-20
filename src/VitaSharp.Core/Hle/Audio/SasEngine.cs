using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Audio;

public enum SasVoiceType { Off, Vag, Pcm, Noise }
public enum SasEnvPhase { Attack, Decay, Sustain, Release, Off }

/// <summary>ADSR curve shapes (PSP numbering).</summary>
public static class SasCurve
{
    public const int LinearIncrease = 0, LinearDecrease = 1, LinearBent = 2, ExponentDecrease = 3, ExponentIncrease = 4, Direct = 5;
}

/// <summary>
/// PSP-style ADSR envelope, stepped once per output sample. Height runs 0..<see cref="Max"/> (unity gain at Max).
/// Curves: linear increase/decrease add/subtract the rate; bent increases at the full rate below 3/4 of Max and at a
/// quarter of it above (the SPU "exponential" attack); exponential decrease/increase move by rate/2^31 of the distance
/// to 0/Max; direct jumps to the rate value and moves to the next phase.
/// </summary>
public sealed class SasEnvelope
{
    public const int Max = 0x40000000;
    public int Height;
    public SasEnvPhase Phase = SasEnvPhase.Off;
    /// <summary>Indexed by phase: attack, decay, sustain, release.</summary>
    public readonly int[] Rates = new int[4], Modes = new int[4];
    public int SustainLevel;

    public SasEnvelope() => SetDefaults();

    /// <summary>Defaults (unconfirmed on hardware): instant attack to full height, held until key off, instant release.</summary>
    public void SetDefaults()
    {
        Modes[0] = SasCurve.LinearIncrease; Rates[0] = Max;
        Modes[1] = SasCurve.LinearDecrease; Rates[1] = 0;
        Modes[2] = SasCurve.LinearDecrease; Rates[2] = 0;
        Modes[3] = SasCurve.LinearDecrease; Rates[3] = Max;
        SustainLevel = Max;
        Height = 0;
        Phase = SasEnvPhase.Off;
    }

    public void KeyOn() { Height = 0; Phase = SasEnvPhase.Attack; }
    public void KeyOff() { if (Phase != SasEnvPhase.Off) Phase = SasEnvPhase.Release; }

    public void Step()
    {
        switch (Phase)
        {
            case SasEnvPhase.Attack:
                Walk(0);
                if (Height >= Max || Modes[0] == SasCurve.Direct) { if (Height >= Max) Height = Max; Phase = SasEnvPhase.Decay; }
                break;
            case SasEnvPhase.Decay:
                Walk(1);
                if (Height <= SustainLevel || Modes[1] == SasCurve.Direct) { if (Height <= SustainLevel) Height = SustainLevel; Phase = SasEnvPhase.Sustain; }
                break;
            case SasEnvPhase.Sustain:
                Walk(2);
                if (Height <= 0) { Height = 0; Phase = SasEnvPhase.Release; }
                break;
            case SasEnvPhase.Release:
                Walk(3);
                if (Height <= 0) { Height = 0; Phase = SasEnvPhase.Off; }
                break;
        }
    }

    void Walk(int phase)
    {
        long h = Height, rate = Rates[phase];
        switch (Modes[phase])
        {
            case SasCurve.LinearIncrease: h += rate; break;
            case SasCurve.LinearDecrease: h -= rate; break;
            case SasCurve.LinearBent: h += h < Max / 4 * 3 ? rate : rate / 4; break;
            case SasCurve.ExponentDecrease: h -= rate == 0 ? 0 : Math.Max(1, h * rate >> 31); break;
            case SasCurve.ExponentIncrease: h += rate == 0 ? 0 : Math.Max(1, (Max - h) * rate >> 31); break;
            case SasCurve.Direct: h = rate; break;
        }
        Height = (int)Math.Clamp(h, 0, Max);
    }

    public static bool ValidMode(int phase, uint mode) => phase switch
    {
        0 => mode is SasCurve.LinearIncrease or SasCurve.LinearBent or SasCurve.Direct,
        1 or 3 => mode is SasCurve.LinearDecrease or SasCurve.ExponentDecrease or SasCurve.Direct,
        _ => mode <= SasCurve.Direct,
    };

    /// <summary>
    /// Loads the envelope from SPU-format ADSR registers. adsr1: [15] attack exponential, [14:10] attack shift,
    /// [9:8] attack step (+7..+4), [7:4] decay shift (step -8, exponential), [3:0] sustain level ((n+1)/16 of Max).
    /// adsr2: [15] sustain exponential, [14] sustain decreasing, [12:8] sustain shift, [7:6] sustain step,
    /// [5] release exponential, [4:0] release shift (step -8). An SPU tick changes the 15-bit level by
    /// step &lt;&lt; (11 - shift) per sample (exponential decrease scales it by the level), here rescaled to the
    /// 0..0x40000000 height.
    /// </summary>
    public void SetSimple(uint adsr1, uint adsr2)
    {
        bool attackExp = (adsr1 & 0x8000) != 0;
        int attackShift = (int)(adsr1 >> 10) & 0x1F, attackStep = 7 - (int)((adsr1 >> 8) & 3);
        int decayShift = (int)(adsr1 >> 4) & 0xF;
        int sl = (int)(adsr1 & 0xF);
        bool sustainExp = (adsr2 & 0x8000) != 0, sustainDec = (adsr2 & 0x4000) != 0;
        int sustainShift = (int)(adsr2 >> 8) & 0x1F, sustainStep = (int)(adsr2 >> 6) & 3;
        bool releaseExp = (adsr2 & 0x20) != 0;
        int releaseShift = (int)adsr2 & 0x1F;

        Modes[0] = attackExp ? SasCurve.LinearBent : SasCurve.LinearIncrease;
        Rates[0] = LinearRate(attackStep, attackShift);
        Modes[1] = SasCurve.ExponentDecrease;
        Rates[1] = ExpRate(8, decayShift);
        SustainLevel = (sl + 1) * (Max / 16);
        if (sustainDec)
        {
            Modes[2] = sustainExp ? SasCurve.ExponentDecrease : SasCurve.LinearDecrease;
            Rates[2] = sustainExp ? ExpRate(8 - sustainStep, sustainShift) : LinearRate(8 - sustainStep, sustainShift);
        }
        else
        {
            Modes[2] = sustainExp ? SasCurve.LinearBent : SasCurve.LinearIncrease;
            Rates[2] = LinearRate(7 - sustainStep, sustainShift);
        }
        Modes[3] = releaseExp ? SasCurve.ExponentDecrease : SasCurve.LinearDecrease;
        Rates[3] = releaseExp ? ExpRate(8, releaseShift) : LinearRate(8, releaseShift);
    }

    /// <summary>step * 2^(11-shift) SPU units per sample, times 2^15 for the height scale.</summary>
    static int LinearRate(int step, int shift) => (int)Math.Clamp(Math.Max(1, Math.Round(step * Math.Pow(2, 26 - shift))), 1, int.MaxValue);
    /// <summary>Relative decrease step * 2^(11-shift) / 2^15 per sample, as rate/2^31.</summary>
    static int ExpRate(int step, int shift) => (int)Math.Clamp(Math.Max(1, Math.Round(step * Math.Pow(2, 27 - shift))), 1, int.MaxValue);
}

/// <summary>One mixer voice: a sample source (VAG/HEVAG ADPCM or S16 PCM in guest memory, or noise), pitch
/// resampling with linear interpolation (0x1000 = one source sample per output sample), envelope and volumes.</summary>
public sealed class SasVoice
{
    public SasVoiceType Type;
    public uint Addr, Size;
    public bool Loop;          // VAG: honour the block loop flags
    public int PcmLoopSize;    // PCM: the last PcmLoopSize samples repeat (0 = no loop)
    public int NoiseClock;
    public int Pitch = 0x1000;
    public int VolL = 0x1000, VolR = 0x1000, WetL = 0x1000, WetR = 0x1000;
    public int Distortion;
    public bool On, Paused;
    public readonly SasEnvelope Env = new();

    // source state
    int _block, _loopBlock = -1, _blockPos = Adpcm.SamplesPerBlock, _lastFlags;
    bool _sourceEnded;
    readonly int[] _hist = new int[4];
    readonly short[] _pcm = new short[Adpcm.SamplesPerBlock];
    readonly byte[] _blk = new byte[Adpcm.BlockBytes];
    uint _pcmPos;
    uint _lfsr = 1, _noiseCounter;
    // resampler
    int _s0, _s1, _frac;
    bool _s0Valid, _s1Valid;

    public void ResetSource()
    {
        _block = 0; _loopBlock = -1; _blockPos = Adpcm.SamplesPerBlock; _lastFlags = 0;
        _sourceEnded = false;
        Array.Clear(_hist);
        _pcmPos = 0;
        _lfsr = 1; _noiseCounter = 0;
        _frac = 0;
        (_s0, _s0Valid) = Next();
        (_s1, _s1Valid) = Next();
    }

    public void KeyOn()
    {
        ResetSource();
        Env.KeyOn();
        On = true;
    }

    public void KeyOff() => Env.KeyOff();

    void End()
    {
        On = false;
        Env.Height = 0;
        Env.Phase = SasEnvPhase.Off;
    }

    (int, bool) Next()
    {
        switch (Type)
        {
            case SasVoiceType.Vag:
                if (_blockPos >= Adpcm.SamplesPerBlock && !LoadBlock()) return (0, false);
                return (_pcm[_blockPos++], true);
            case SasVoiceType.Pcm:
                if (_pcmPos >= Size)
                {
                    if (PcmLoopSize <= 0 || Size == 0) { _sourceEnded = true; return (0, false); }
                    _pcmPos = Size - (uint)PcmLoopSize;
                }
                return (GuestMemory.Read<short>(Addr + _pcmPos++ * 2), true);
            case SasVoiceType.Noise:
                return (0, true); // produced per output sample in Sample()
            default:
                return (0, false);
        }
    }

    bool LoadBlock()
    {
        if (_sourceEnded) return false;
        if ((_lastFlags & Adpcm.FlagLoopEnd) != 0)
        {
            if (Loop && (_lastFlags & Adpcm.FlagRepeat) != 0) _block = Math.Max(0, _loopBlock);
            else { _sourceEnded = true; return false; }
        }
        if ((uint)(_block + 1) * Adpcm.BlockBytes > Size) { _sourceEnded = true; return false; }
        GuestMemory.Span(Addr + (uint)_block * Adpcm.BlockBytes, Adpcm.BlockBytes).CopyTo(_blk);
        int flags = Adpcm.Flags(_blk);
        if (flags == Adpcm.FlagEndOfStream) { _sourceEnded = true; return false; }
        if ((flags & Adpcm.FlagLoopStart) != 0) _loopBlock = _block;
        Adpcm.DecodeBlock(_blk, _hist, _pcm);
        _lastFlags = flags;
        _blockPos = 0;
        _block++;
        return true;
    }

    int NoiseSample()
    {
        // 15-bit LFSR clocked at a rate that doubles every 4 steps of the clock value (SPU-like, approximate)
        _noiseCounter += (uint)(4 + (NoiseClock & 3)) << (NoiseClock >> 2);
        while (_noiseCounter >= 0x8000)
        {
            _noiseCounter -= 0x8000;
            uint bit = (_lfsr ^ (_lfsr >> 1)) & 1;
            _lfsr = (_lfsr >> 1) | (bit << 14);
        }
        return (_lfsr & 1) != 0 ? 0x7FFF : -0x7FFF;
    }

    /// <summary>Next output sample before envelope and volume; false once the source is exhausted.</summary>
    public bool Sample(out int value)
    {
        if (Type == SasVoiceType.Noise) { value = NoiseSample(); return true; }
        if (!_s0Valid) { value = 0; return false; }
        value = _s1Valid ? _s0 + ((_s1 - _s0) * _frac >> 12) : _s0;
        _frac += Pitch;
        while (_frac >= 0x1000)
        {
            _frac -= 0x1000;
            _s0 = _s1; _s0Valid = _s1Valid;
            (_s1, _s1Valid) = _s1Valid ? Next() : (0, false);
        }
        return true;
    }

    /// <summary>Renders <paramref name="count"/> samples into the dry and wet buses.</summary>
    public void Render(int count, int[] dryL, int[] dryR, int[] wetL, int[] wetR)
    {
        for (int i = 0; i < count && On && !Paused; i++)
        {
            if (!Sample(out int s)) { End(); break; }
            Env.Step();
            s = (int)((long)s * Env.Height >> 30);
            dryL[i] += s * VolL >> 12;
            dryR[i] += s * VolR >> 12;
            wetL[i] += s * WetL >> 12;
            wetR[i] += s * WetR >> 12;
            if (Env.Phase == SasEnvPhase.Off) End();
        }
    }

    public bool Ended => !On;
}

/// <summary>
/// The libsas software mixer: N voices mixed into a dry bus and a wet (effect send) bus, the wet bus through a simple
/// delay/feedback effect, then dry + wet into interleaved stereo S16. No dependency on HLE plumbing.
/// </summary>
public sealed class SasEngine
{
    public const int MaxGrain = 2048;
    public const int EffectOff = -1, EffectEcho = 6, EffectDelay = 7;
    public int Grain, OutputMode;
    public readonly SasVoice[] Voices;
    public bool DryOn = true, WetOn;
    public int EffectType = EffectOff, EffectDelayTime, EffectFeedback, EffectVolL = 0x1000, EffectVolR = 0x1000;
    public int DryPeakL, DryPeakR, WetPeakL, WetPeakR, PreMasterPeakL, PreMasterPeakR;

    readonly int[] _dryL = new int[MaxGrain], _dryR = new int[MaxGrain], _wetL = new int[MaxGrain], _wetR = new int[MaxGrain];
    readonly int[] _lineL = new int[128 * DelayUnit + 1], _lineR = new int[128 * DelayUnit + 1];
    int _linePos;
    /// <summary>Output samples per delayTime unit: 0..127 maps to 0..~0.5 s at 48 kHz (unconfirmed).</summary>
    const int DelayUnit = 192;

    public SasEngine(int grain, int voices)
    {
        Grain = grain;
        Voices = new SasVoice[voices];
        for (int i = 0; i < voices; i++) Voices[i] = new SasVoice();
    }

    public void ClearEffect() { Array.Clear(_lineL); Array.Clear(_lineR); _linePos = 0; }

    /// <summary>Mixes one grain into <paramref name="output"/> (Grain stereo frames). With
    /// <paramref name="mixWithExisting"/>, the existing contents scaled by lvol/rvol (0x1000 = unity) are added.</summary>
    public void Mix(Span<short> output, bool mixWithExisting = false, int lvol = 0x1000, int rvol = 0x1000)
    {
        int n = Math.Min(Grain, output.Length / 2);
        Array.Clear(_dryL, 0, n); Array.Clear(_dryR, 0, n); Array.Clear(_wetL, 0, n); Array.Clear(_wetR, 0, n);
        foreach (var v in Voices) v.Render(n, _dryL, _dryR, _wetL, _wetR);
        DryPeakL = DryPeakR = WetPeakL = WetPeakR = PreMasterPeakL = PreMasterPeakR = 0;
        int delay = EffectDelayTime * DelayUnit;
        for (int i = 0; i < n; i++)
        {
            int wl = 0, wr = 0;
            switch (EffectType)
            {
                case EffectOff: break;
                case EffectEcho or EffectDelay:
                    if (delay == 0) { wl = _wetL[i]; wr = _wetR[i]; break; }
                    int rd = (_linePos - delay + _lineL.Length) % _lineL.Length;
                    wl = _lineL[rd]; wr = _lineR[rd];
                    _lineL[_linePos] = Math.Clamp(_wetL[i] + wl * EffectFeedback / 128, -0x10000, 0x10000);
                    _lineR[_linePos] = Math.Clamp(_wetR[i] + wr * EffectFeedback / 128, -0x10000, 0x10000);
                    _linePos = (_linePos + 1) % _lineL.Length;
                    break;
                default: // reverb algorithms are not modelled: the send passes through unprocessed
                    wl = _wetL[i]; wr = _wetR[i];
                    break;
            }
            wl = wl * EffectVolL >> 12;
            wr = wr * EffectVolR >> 12;
            int l = (DryOn ? _dryL[i] : 0) + (WetOn ? wl : 0);
            int r = (DryOn ? _dryR[i] : 0) + (WetOn ? wr : 0);
            DryPeakL = Math.Max(DryPeakL, Math.Abs(_dryL[i])); DryPeakR = Math.Max(DryPeakR, Math.Abs(_dryR[i]));
            WetPeakL = Math.Max(WetPeakL, Math.Abs(wl)); WetPeakR = Math.Max(WetPeakR, Math.Abs(wr));
            PreMasterPeakL = Math.Max(PreMasterPeakL, Math.Abs(l)); PreMasterPeakR = Math.Max(PreMasterPeakR, Math.Abs(r));
            if (mixWithExisting)
            {
                l += output[i * 2] * lvol >> 12;
                r += output[i * 2 + 1] * rvol >> 12;
            }
            output[i * 2] = (short)Math.Clamp(l, short.MinValue, short.MaxValue);
            output[i * 2 + 1] = (short)Math.Clamp(r, short.MinValue, short.MaxValue);
        }
    }
}
