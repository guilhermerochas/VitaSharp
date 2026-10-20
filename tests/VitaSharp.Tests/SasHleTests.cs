using System.Reflection;
using System.Text;
using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>SceSas: ADPCM block decoding, envelope curves, pitch resampling, mixing and the API's error codes.</summary>
[Collection("guest")]
public sealed class SasHleTests(GuestMemoryFixture fx)
{
    static object? Invoke<T>(T module, string name, params object[] args) =>
        typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);
    static int Call<T>(T module, string name, params object[] args) => (int)Invoke(module, name, args)!;
    static Ptr<T> P<T>(uint a) where T : unmanaged => new(a);

    uint Str(string s)
    {
        uint p = fx.Memory.Alloc(0x100, "sascfg");
        var b = Encoding.ASCII.GetBytes(s + "\0");
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }

    Sas Init(string config = "numGrains=256 numVoices=8 numReverbs=1")
    {
        var s = new Sas();
        Assert.Equal(0, Call(s, "sceSasInit", Str(config), fx.Memory.Alloc(0x10000, "saswork"), 0x10000u));
        return s;
    }

    short[] Core(Sas s, int grain = 256)
    {
        uint o = fx.Memory.Alloc((uint)(grain * 4), "sasout");
        Assert.Equal(0, Call(s, "sceSasCore", o));
        return GuestMemory.Span<short>(o, grain * 2).ToArray();
    }

    static short[] Left(short[] interleaved) => interleaved.Where((_, i) => i % 2 == 0).ToArray();

    uint Pcm(short[] samples)
    {
        uint p = fx.Memory.Alloc((uint)(samples.Length * 2 + 16), "saspcm");
        samples.CopyTo(GuestMemory.Span<short>(p, samples.Length));
        return p;
    }

    static byte[] VagBlock(int predictor, int shift, int flags, params int[] nibbles)
    {
        var b = new byte[16];
        b[0] = (byte)(predictor << 4 | shift);
        b[1] = (byte)flags;
        for (int i = 0; i < nibbles.Length; i++) b[2 + i / 2] |= (byte)((nibbles[i] & 0xF) << ((i & 1) * 4));
        return b;
    }

    [Fact]
    public void AdpcmDecodesKnownBlocks()
    {
        var hist = new int[4];
        var o = new short[28];
        Adpcm.DecodeBlock(VagBlock(1, 0, 0, 1, 2), hist, o);
        // classic filter 1: s = nibble << 12 + h1 * 60 / 64
        Assert.Equal(new short[] { 4096, 12032, 11280, 10575 }, o[..4]);
        Assert.Equal(o[27], (short)hist[0]);
        Assert.Equal(o[26], (short)hist[1]);

        Array.Clear(hist);
        Adpcm.DecodeBlock(VagBlock(0, 4, 0, 7, -8, 1), hist, o); // shift 4: nibble << 8, no prediction
        Assert.Equal(new short[] { 7 * 256, -8 * 256, 256, 0 }, o[..4]);

        // filter 4 (122/64, -60/64) overflows and clamps
        hist[0] = 28672; hist[1] = 0;
        Adpcm.DecodeBlock(VagBlock(4, 0, 0, 7), hist, o);
        Assert.Equal(short.MaxValue, o[0]);

        // HEVAG: byte 1's high nibble extends the predictor (index 16 = 910, -7680 over 8192)
        Array.Clear(hist);
        hist[0] = 8192; hist[1] = 8192;
        var hb = VagBlock(0, 12, 0x10);
        Adpcm.DecodeBlock(hb, hist, o);
        Assert.Equal((short)((8192 * 910 + 8192 * -7680 >> 5) >> 8), o[0]);
        Assert.Equal(0, Adpcm.Flags(hb));
    }

    [Fact]
    public void VagVoicePlaysToEndAtUnityPitch()
    {
        var s = Init();
        byte[] b0 = VagBlock(1, 0, 0, 1, 2, 3, 4, 5, 6, 7), b1 = VagBlock(0, 2, Adpcm.FlagLoopEnd, 1, 1, 1, 1);
        uint vag = fx.Memory.Alloc(0x100, "vag");
        b0.CopyTo(GuestMemory.Span(vag, 16)); b1.CopyTo(GuestMemory.Span(vag + 16, 16));
        var expected = new short[56];
        var h = new int[4];
        Adpcm.DecodeBlock(b0, h, expected.AsSpan(0, 28));
        Adpcm.DecodeBlock(b1, h, expected.AsSpan(28, 28));

        Assert.Equal(1, Call(s, "sceSasGetEndState", 0));
        Assert.Equal(0, Call(s, "sceSasSetVoice", 0, vag, 32u, 0u));
        Assert.Equal(0, Call(s, "sceSasSetKeyOn", 0));
        Assert.Equal(0, Call(s, "sceSasGetEndState", 0));
        var outp = Core(s);
        Assert.Equal(expected, Left(outp)[..56]);
        Assert.All(Left(outp)[56..], x => Assert.Equal(0, x));
        Assert.Equal(outp[0], outp[1]); // same on both sides at default volumes
        Assert.Equal(1, Call(s, "sceSasGetEndState", 0));
    }

    [Fact]
    public void VagLoopFlagsRepeat()
    {
        var s = Init();
        uint vag = fx.Memory.Alloc(0x100, "vagloop");
        VagBlock(0, 4, 0, 1).CopyTo(GuestMemory.Span(vag, 16));
        VagBlock(0, 4, Adpcm.FlagLoopStart | Adpcm.FlagRepeat, 2).CopyTo(GuestMemory.Span(vag + 16, 16));
        VagBlock(0, 4, Adpcm.FlagLoopEnd | Adpcm.FlagRepeat, 3).CopyTo(GuestMemory.Span(vag + 32, 16));
        Assert.Equal(0, Call(s, "sceSasSetVoice", 0, vag, 48u, 1u));
        Assert.Equal(0, Call(s, "sceSasSetKeyOn", 0));
        var l = Left(Core(s));
        int[] starts = [0, 28, 56, 84, 112, 140];
        short[] want = [256, 512, 768, 512, 768, 512];
        for (int i = 0; i < starts.Length; i++) Assert.Equal(want[i], l[starts[i]]);
        Assert.Equal(0, Call(s, "sceSasGetEndState", 0));
    }

    [Fact]
    public void PitchResamples()
    {
        var src = Enumerable.Range(0, 600).Select(i => (short)(i * 10)).ToArray();
        uint p = Pcm(src);
        var s = Init();
        Assert.Equal(0, Call(s, "sceSasSetVoicePCM", 0, p, 600, 0));
        Assert.Equal(0, Call(s, "sceSasSetPitch", 0, 0x2000));
        Assert.Equal(0, Call(s, "sceSasSetKeyOn", 0));
        var l = Left(Core(s));
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (short)(i * 20)).ToArray(), l);

        Assert.Equal(0, Call(s, "sceSasSetPitch", 0, 0x800));
        Assert.Equal(0, Call(s, "sceSasSetKeyOn", 0));
        l = Left(Core(s));
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (short)(i * 5)).ToArray(), l); // linear interpolation

        Assert.Equal(Sas.ERR_INVALID_PITCH, Call(s, "sceSasSetPitch", 0, 0));
        Assert.Equal(Sas.ERR_INVALID_PITCH, Call(s, "sceSasSetPitch", 0, 0x4001));
    }

    [Fact]
    public void PcmLoopAndVolumes()
    {
        var src = Enumerable.Range(0, 100).Select(i => (short)(i * 100)).ToArray();
        uint p = Pcm(src);
        var s = Init();
        Assert.Equal(0, Call(s, "sceSasSetVoicePCM", 1, p, 100, 10));
        Assert.Equal(0, Call(s, "sceSasSetVolume", 1, 0x800, -0x1000, 0, 0));
        Assert.Equal(0, Call(s, "sceSasSetKeyOn", 1));
        var o = Core(s);
        for (int i = 0; i < 256; i++)
        {
            int srcIdx = i < 100 ? i : 90 + (i - 100) % 10;
            Assert.Equal((short)(src[srcIdx] / 2), o[i * 2]);
            Assert.Equal((short)-src[srcIdx], o[i * 2 + 1]);
        }
        Assert.Equal(Sas.ERR_INVALID_VOLUME, Call(s, "sceSasSetVolume", 1, 0x1001, 0, 0, 0));
        Assert.Equal(Sas.ERR_INVALID_PCM_SIZE, Call(s, "sceSasSetVoicePCM", 1, p, 0, 0));
        Assert.Equal(Sas.ERR_INVALID_LOOP_POS, Call(s, "sceSasSetVoicePCM", 1, p, 10, 11));

        // CoreWithMix: existing content scaled by lvol/rvol plus the new grain
        Assert.Equal(0, Call(s, "sceSasSetVolume", 1, 0, 0, 0, 0));
        uint buf = fx.Memory.Alloc(0x400, "mix");
        GuestMemory.Span<short>(buf, 512).Fill(1000);
        Assert.Equal(0, Call(s, "sceSasCoreWithMix", buf, 0x800, 0x1000));
        Assert.Equal(500, GuestMemory.Read<short>(buf));
        Assert.Equal(1000, GuestMemory.Read<short>(buf + 2));
        Assert.Equal(Sas.ERR_INVALID_VOLUME, Call(s, "sceSasCoreWithMix", buf, 0x1001, 0));
    }

    [Fact]
    public void EnvelopeCurves()
    {
        var env = new SasEnvelope();
        env.Modes[0] = SasCurve.LinearIncrease; env.Rates[0] = SasEnvelope.Max / 8;
        env.Modes[1] = SasCurve.ExponentDecrease; env.Rates[1] = 1 << 30; // halve per sample
        env.SustainLevel = SasEnvelope.Max / 8;
        env.Modes[2] = SasCurve.LinearDecrease; env.Rates[2] = 0;
        env.Modes[3] = SasCurve.LinearDecrease; env.Rates[3] = SasEnvelope.Max / 16;
        env.KeyOn();
        var h = new List<int>();
        for (int i = 0; i < 12; i++) { env.Step(); h.Add(env.Height); }
        Assert.Equal(Enumerable.Range(1, 8).Select(i => SasEnvelope.Max / 8 * i), h.Take(8));
        Assert.Equal(SasEnvPhase.Sustain, env.Phase);
        Assert.Equal([SasEnvelope.Max / 2, SasEnvelope.Max / 4, SasEnvelope.Max / 8, SasEnvelope.Max / 8], h.Skip(8));
        env.KeyOff();
        for (int i = 0; i < 2; i++) env.Step();
        Assert.Equal(0, env.Height);
        Assert.Equal(SasEnvPhase.Off, env.Phase);

        // bent attack slows to a quarter of the rate above 3/4 of the height
        env.Modes[0] = SasCurve.LinearBent; env.Rates[0] = SasEnvelope.Max / 4;
        env.KeyOn();
        for (int i = 0; i < 3; i++) env.Step();
        Assert.Equal(SasEnvelope.Max / 4 * 3, env.Height);
        env.Step();
        Assert.Equal(SasEnvelope.Max / 16 * 13, env.Height);

        // SPU registers: attack shift 0 step +7 reaches the top in 3 samples, sustain level (7+1)/16
        env.SetSimple(0x0007, 0x0000);
        Assert.Equal(SasCurve.LinearIncrease, env.Modes[0]);
        Assert.Equal(7 << 26, env.Rates[0]);
        Assert.Equal(SasEnvelope.Max / 2, env.SustainLevel);
        Assert.Equal(SasCurve.ExponentDecrease, env.Modes[1]);
        env.KeyOn();
        for (int i = 0; i < 3; i++) env.Step();
        Assert.Equal(SasEnvPhase.Decay, env.Phase);
        env.SetSimple(0x8000, 0xC020);
        Assert.Equal(SasCurve.LinearBent, env.Modes[0]);
        Assert.Equal(SasCurve.ExponentDecrease, env.Modes[2]);
        Assert.Equal(SasCurve.ExponentDecrease, env.Modes[3]);
    }

    [Fact]
    public void EnvelopeShapesVoiceOutput()
    {
        var src = Enumerable.Repeat((short)0x4000, 1000).ToArray();
        var s = Init();
        uint p = Pcm(src);
        Assert.Equal(0, Call(s, "sceSasSetVoicePCM", 0, p, 1000, 0));
        Assert.Equal(0, Call(s, "sceSasSetADSR", 0, 0xFu, (uint)(SasEnvelope.Max / 4), 0u, 0u, (uint)(SasEnvelope.Max / 2)));
        Assert.Equal(0, Call(s, "sceSasSetKeyOn", 0));
        var l = Left(Core(s));
        Assert.Equal(new short[] { 0x1000, 0x2000, 0x3000, 0x4000, 0x4000 }, l[..5]);
        Assert.Equal(SasEnvelope.Max, Call(s, "sceSasGetEnvelope", 0));
        Assert.Equal(0, Call(s, "sceSasSetKeyOff", 0));
        l = Left(Core(s));
        Assert.Equal(new short[] { 0x2000, 0, 0 }, l[..3]);
        Assert.Equal(1, Call(s, "sceSasGetEndState", 0));

        Assert.Equal(Sas.ERR_INVALID_ADSR_CURVE_MODE, Call(s, "sceSasSetADSRmode", 0, 1u, 1u, 0u, 0u, 0u));
        Assert.Equal(Sas.ERR_INVALID_ADSR_CURVE_MODE, Call(s, "sceSasSetADSRmode", 0, 8u, 0u, 0u, 0u, 0u));
        Assert.Equal(0, Call(s, "sceSasSetADSRmode", 0, 0xFu, 2u, 3u, 4u, 5u));
        Assert.Equal(Sas.ERR_INVALID_ADSR_RATE, Call(s, "sceSasSetADSR", 0, 2u, 0u, 0x80000000u, 0u, 0u));
        Assert.Equal(0, Call(s, "sceSasSetADSR", 0, 1u, 0u, 0x80000000u, 0u, 0u)); // only flagged phases are checked
        Assert.Equal(Sas.ERR_INVALID_PARAMETER, Call(s, "sceSasSetSL", 0, 0x40000001u));
        Assert.Equal(0, Call(s, "sceSasSetSimpleADSR", 0, 0x000Fu, 0x1FC0u));
    }

    [Fact]
    public void PauseHoldsPosition()
    {
        var src = Enumerable.Range(0, 1000).Select(i => (short)i).ToArray();
        var s = Init();
        Assert.Equal(0, Call(s, "sceSasSetVoicePCM", 0, Pcm(src), 1000, 0));
        Assert.Equal(0, Call(s, "sceSasSetKeyOn", 0));
        Core(s);
        Assert.Equal(0, Call(s, "sceSasSetPause", 0, 1u));
        Assert.Equal(1, Call(s, "sceSasGetPauseState", 0));
        Assert.Equal(Sas.ERR_VOICE_PAUSED, Call(s, "sceSasSetKeyOn", 0));
        Assert.All(Core(s), x => Assert.Equal(0, x));
        Assert.Equal(0, Call(s, "sceSasSetPause", 0, 0u));
        Assert.Equal(256, Left(Core(s))[0]);
    }

    [Fact]
    public void NoiseAndEcho()
    {
        var s = Init();
        Assert.Equal(Sas.ERR_INVALID_NOISE_FREQ, Call(s, "sceSasSetNoise", 2, 64u));
        Assert.Equal(0, Call(s, "sceSasSetNoise", 2, 40u));
        Assert.Equal(0, Call(s, "sceSasSetKeyOn", 2));
        var o = Core(s);
        Assert.Contains(o, x => x > 0);
        Assert.Contains(o, x => x < 0);
        Assert.Equal(0, Call(s, "sceSasSetKeyOff", 2));
        Core(s);

        // impulse through the echo with dry off: comes back delayTime * 192 samples later
        uint p = Pcm([1234]);
        Assert.Equal(0, Call(s, "sceSasSetVoicePCM", 0, p, 1, 0));
        Assert.Equal(0, Call(s, "sceSasSetEffectType", 6));
        Assert.Equal(0, Call(s, "sceSasSetEffectParam", 1u, 0u));
        Assert.Equal(0, Call(s, "sceSasSetEffect", 0, 1));
        Assert.Equal(0, Call(s, "sceSasSetKeyOn", 0));
        var l = Left(Core(s));
        Assert.Equal(1234, l[192]);
        Assert.Equal(1, l.Count(x => x != 0));
        uint pk = fx.Memory.Alloc(0x10, "peak");
        Assert.Equal(0, Call(s, "sceSasGetWetPeak", P<int>(pk), P<int>(pk + 4)));
        Assert.Equal(1234, (int)GuestMemory.R32(pk));
        Assert.Equal(0, Call(s, "sceSasGetDryPeak", P<int>(pk), P<int>(pk + 4)));
        Assert.Equal(1234, (int)GuestMemory.R32(pk)); // the dry bus is measured even with the dry output switched off
        Assert.Equal(0, Call(s, "sceSasGetPreMasterPeak", P<int>(pk), P<int>(pk + 4)));
        Assert.Equal(1234, (int)GuestMemory.R32(pk));
        Assert.Equal(Sas.ERR_REV_INVALID_TYPE, Call(s, "sceSasSetEffectType", 9));
        Assert.Equal(Sas.ERR_REV_INVALID_DELAY_TIME, Call(s, "sceSasSetEffectParam", 128u, 0u));
        Assert.Equal(Sas.ERR_REV_INVALID_FEEDBACK, Call(s, "sceSasSetEffectParam", 0u, 128u));
        Assert.Equal(Sas.ERR_REV_INVALID_VOLUME, Call(s, "sceSasSetEffectVolume", 0x1001, 0));
    }

    [Fact]
    public void InitGrainAndErrors()
    {
        var s = new Sas();
        uint work = fx.Memory.Alloc(0x10000, "saswork2"), sz = fx.Memory.Alloc(0x10, "sz");
        Assert.Equal(Sas.ERR_NOT_INIT, Call(s, "sceSasCore", work));
        Assert.Equal(Sas.ERR_NOT_INIT, Call(s, "sceSasGetGrain"));
        Assert.Equal(Sas.ERR_NOT_INIT, Call(s, "sceSasExit", P<uint>(0), P<uint>(0)));
        Assert.Equal(0, Call(s, "sceSasGetNeededMemorySize", Str("numGrains=512 numVoices=4"), P<uint>(sz)));
        Assert.True(GuestMemory.R32(sz) > 0);
        Assert.Equal(Sas.ERR_BAD_ADDRESS, Call(s, "sceSasGetNeededMemorySize", 0u, P<uint>(sz)));
        Assert.Equal(Sas.ERR_INVALID_MAX_VOICES, Call(s, "sceSasInit", Str("numVoices=0"), work, 0x10000u));
        Assert.Equal(Sas.ERR_INVALID_GRAIN, Call(s, "sceSasInit", Str("numGrains=100"), work, 0x10000u));
        Assert.Equal(Sas.ERR_INVALID_GRAIN, Call(s, "sceSasInitWithGrain", Str("numGrains=256"), 512u, work, 0x10000u));
        Assert.Equal(0, Call(s, "sceSasInitWithGrain", Str("numGrains=512 numVoices=4"), 128u, work, 0x10000u));
        Assert.Equal(Sas.ERR_ALREADY_INIT, Call(s, "sceSasInit", Str("numVoices=4"), work, 0x10000u));
        Assert.Equal(128, Call(s, "sceSasGetGrain"));
        uint o = fx.Memory.Alloc(0x1000, "o");
        GuestMemory.Fill(o, 0x55, 0x1000);
        Assert.Equal(0, Call(s, "sceSasCore", o));
        Assert.Equal(0u, GuestMemory.R32(o + 128 * 4 - 4));
        Assert.Equal(0x55555555u, GuestMemory.R32(o + 128 * 4)); // exactly one grain written
        Assert.Equal(0, Call(s, "sceSasSetGrain", 512u));
        Assert.Equal(Sas.ERR_INVALID_GRAIN, Call(s, "sceSasSetGrain", 544u));
        Assert.Equal(Sas.ERR_INVALID_GRAIN, Call(s, "sceSasSetGrain", 100u));
        Assert.Equal(Sas.ERR_INVALID_VOICE, Call(s, "sceSasSetKeyOn", 4));
        Assert.Equal(Sas.ERR_INVALID_VOICE, Call(s, "sceSasGetEndState", -1));
        Assert.Equal(Sas.ERR_INVALID_PARAMETER, Call(s, "sceSasSetVoice", 0, o, 20u, 0u));
        Assert.Equal(Sas.ERR_INVALID_PARAMETER, Call(s, "sceSasSetVoice", 0, o, 32u, 2u));
        Assert.Equal(Sas.ERR_BAD_ADDRESS, Call(s, "sceSasCore", 0u));
        Assert.Equal(0, Call(s, "sceSasSetOutputmode", 1u));
        Assert.Equal(1, Call(s, "sceSasGetOutputmode"));
        Assert.Equal(Sas.ERR_INVALID_OUTPUT_MODE, Call(s, "sceSasSetOutputmode", 2u));
        uint eb = fx.Memory.Alloc(0x10, "eb");
        Assert.Equal(0, Call(s, "sceSasExit", P<uint>(eb), P<uint>(eb + 4)));
        Assert.Equal(work, GuestMemory.R32(eb));
        Assert.Equal(0x10000u, GuestMemory.R32(eb + 4));
        Assert.Equal(Sas.ERR_NOT_INIT, Call(s, "sceSasSetKeyOn", 0));
    }
}
