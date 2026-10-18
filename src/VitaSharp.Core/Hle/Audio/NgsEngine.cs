using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Audio;

public enum NgsBuss : uint
{
    Master = 0, Compressor = 1, SideChainCompressor = 2, Delay = 3, Distortion = 4, Envelope = 5, Equalization = 6, Mixer = 7,
    Pauser = 8, PitchShift = 9, Reverb = 10, SasEmulation = 11, Simple = 12, Atrac9 = 13, SimpleAtrac9 = 14, Scream = 15,
    ScreamAtrac9 = 16, NormalPlayer = 17, Max = 18,
}

public enum NgsModuleKind
{
    InputMixer, Output, Player, Atrac9, Equalizer, Envelope, Pauser, Filter, Generator, Mixer, Distortion, Compressor, Delay, PitchShift, Reverb,
}

public enum NgsVoiceState { Available, Active, Finalizing, Unloading }

/// <summary>NGS module metadata: the module id reported to the guest and the parameter buffer size.</summary>
public static class NgsModules
{
    public static uint Id(NgsModuleKind k) => k switch
    {
        NgsModuleKind.InputMixer => 0x5CE0, NgsModuleKind.Player => 0x5CE6, NgsModuleKind.Atrac9 => 0x5CAA, NgsModuleKind.Equalizer => 0x5CEC,
        NgsModuleKind.Envelope => 0x5CE3, NgsModuleKind.Pauser => 0x5CE5, NgsModuleKind.Filter => 0x5CE4, NgsModuleKind.Generator => 0x5CE8,
        NgsModuleKind.Mixer => 0x5CE9, NgsModuleKind.Distortion => 0x5CE2, NgsModuleKind.Compressor => 0x5CE1, NgsModuleKind.Delay => 0x5CEB,
        NgsModuleKind.PitchShift => 0x5CEA, NgsModuleKind.Reverb => 0x5CED, _ => 0,
    };

    public static uint ParamSize(NgsModuleKind k) => k switch
    {
        NgsModuleKind.Player => 96 /* 84 (12-byte buffer entries) or 96 (16-byte entries, older SDK): allocate the larger */, NgsModuleKind.Atrac9 => 96, NgsModuleKind.Equalizer => 88, NgsModuleKind.Envelope => 72,
        NgsModuleKind.Pauser => 16, NgsModuleKind.Filter => 28, NgsModuleKind.Generator => 32, NgsModuleKind.Mixer => 24,
        NgsModuleKind.Distortion => 32, NgsModuleKind.Compressor => 40, NgsModuleKind.Delay => 128, NgsModuleKind.PitchShift => 12,
        NgsModuleKind.Reverb => 128, _ => 0,
    };

    public static NgsModuleKind[] Chain(NgsBuss t) => t switch
    {
        NgsBuss.Master => [NgsModuleKind.InputMixer, NgsModuleKind.Output],
        NgsBuss.Compressor => [NgsModuleKind.InputMixer, NgsModuleKind.Compressor],
        NgsBuss.SideChainCompressor => [NgsModuleKind.InputMixer, NgsModuleKind.InputMixer, NgsModuleKind.Compressor],
        NgsBuss.Delay => [NgsModuleKind.InputMixer, NgsModuleKind.Delay],
        NgsBuss.Distortion => [NgsModuleKind.InputMixer, NgsModuleKind.Distortion],
        NgsBuss.Envelope => [NgsModuleKind.InputMixer, NgsModuleKind.Envelope],
        NgsBuss.Equalization => [NgsModuleKind.InputMixer, NgsModuleKind.Equalizer],
        NgsBuss.Mixer => [NgsModuleKind.InputMixer],
        NgsBuss.Pauser => [NgsModuleKind.InputMixer, NgsModuleKind.Pauser],
        NgsBuss.PitchShift => [NgsModuleKind.InputMixer, NgsModuleKind.PitchShift],
        NgsBuss.Reverb => [NgsModuleKind.InputMixer, NgsModuleKind.Reverb],
        NgsBuss.SasEmulation => [NgsModuleKind.Player, NgsModuleKind.Envelope],
        NgsBuss.Simple => [NgsModuleKind.Player, NgsModuleKind.Equalizer, NgsModuleKind.Envelope, NgsModuleKind.Pauser, NgsModuleKind.Filter, NgsModuleKind.Filter],
        NgsBuss.Atrac9 => [NgsModuleKind.Atrac9, NgsModuleKind.Generator, NgsModuleKind.Mixer, NgsModuleKind.Equalizer, NgsModuleKind.Envelope,
            NgsModuleKind.Distortion, NgsModuleKind.Equalizer, NgsModuleKind.Equalizer, NgsModuleKind.Equalizer, NgsModuleKind.Equalizer],
        NgsBuss.SimpleAtrac9 => [NgsModuleKind.Atrac9, NgsModuleKind.Equalizer, NgsModuleKind.Envelope, NgsModuleKind.Pauser, NgsModuleKind.Filter, NgsModuleKind.Filter],
        NgsBuss.Scream => [NgsModuleKind.Player, NgsModuleKind.Envelope, NgsModuleKind.Distortion, NgsModuleKind.Equalizer, NgsModuleKind.Filter, NgsModuleKind.Filter],
        NgsBuss.ScreamAtrac9 => [NgsModuleKind.Atrac9, NgsModuleKind.Envelope, NgsModuleKind.Distortion, NgsModuleKind.Equalizer, NgsModuleKind.Filter, NgsModuleKind.Filter],
        NgsBuss.NormalPlayer => [NgsModuleKind.Player, NgsModuleKind.Generator, NgsModuleKind.Mixer, NgsModuleKind.Equalizer, NgsModuleKind.Envelope,
            NgsModuleKind.Distortion, NgsModuleKind.Equalizer, NgsModuleKind.Equalizer, NgsModuleKind.Equalizer, NgsModuleKind.Equalizer],
        _ => [],
    };

    public static uint OutputCount(NgsBuss t) => t switch
    {
        NgsBuss.Simple or NgsBuss.SimpleAtrac9 or NgsBuss.Scream or NgsBuss.ScreamAtrac9 => 2,
        NgsBuss.Atrac9 or NgsBuss.NormalPlayer => 4,
        _ => 1,
    };
}

public sealed class NgsPatch
{
    public uint Handle;
    public required NgsVoice Source, Dest;
    public int OutputIndex, SubIndex, DestIndex;
    public readonly float[,] Volume = new float[2, 2];
    public bool Active => SubIndex != -1;
}

public sealed class NgsModuleData
{
    public float[]? EmitBuf;
    public required NgsVoice Voice;
    public int Index;
    public NgsModuleKind Kind;
    public uint ParamAddr, ParamSize;
    public byte[]? Locked;            // parameters snapshot while the guest edits them (LockParams)
    public bool Bypassed;
    public uint Callback, UserData;
    public byte[] GuestState = [];    // visible through sceNgsVoiceGetStateData
    public NgsSourceState? Source;    // player / atrac9 decoding state

    public uint Param32(int off) => Locked != null ? BitConverter.ToUInt32(Locked, off) : GuestMemory.R32(ParamAddr + (uint)off);
    public int ParamI16(int off) => Locked != null ? BitConverter.ToInt16(Locked, off) : GuestMemory.Read<short>(ParamAddr + (uint)off);
    public sbyte ParamI8(int off) => Locked != null ? (sbyte)Locked[off] : GuestMemory.Read<sbyte>(ParamAddr + (uint)off);
    public float ParamF(int off) => BitConverter.Int32BitsToSingle((int)Param32(off));
}

public sealed class NgsVoice
{
    public uint Handle;
    public required NgsRack Rack;
    public NgsModuleData[] Modules = [];
    public NgsVoiceState State;
    public bool Paused, KeyedOff, Pending;
    public uint FrameCount;
    public NgsPatch?[][] Patches = [];
    public float[] Input = [];
    public float[]? Product;
    public uint FinishedCallback, FinishedUserData;
}

public sealed class NgsRack
{
    public uint Handle;
    public required NgsSystem System;
    public NgsBuss Type;
    public NgsModuleKind[] Chain = [];
    public int ChannelsPerVoice, PatchesPerOutput, MaxPatchesPerInput;
    public uint OutputCount;
    public readonly List<NgsVoice> Voices = new();
    public uint AllocCursor, AllocEnd;

    public uint Alloc(uint size)
    {
        uint a = (AllocCursor + 15) & ~15u;
        if (a + size > AllocEnd) return 0;
        AllocCursor = a + size;
        return a;
    }
}

public sealed class NgsSystem
{
    public uint Handle;
    public int Granularity, SampleRate, MaxVoices;
    public readonly List<NgsRack> Racks = new();
    public readonly List<NgsVoice> Queue = new();
    public readonly object Lock = new();
    public uint CallbackInfo;   // guest scratch for SceNgsCallbackInfo
}

/// <summary>Decoding state of a player / ATRAC9 module (host-only).</summary>
public sealed class NgsSourceState
{
    public readonly List<float> Pcm = new();   // interleaved stereo frames waiting to be consumed
    public int PcmRead;
    public int LoopCount;
    public float[] DecodeBuf = [];
    /// <summary>Scratch for one decoded block (reused: audio ticks must not allocate).</summary>
    public float[] DecodeBuffer(int floats) { if (DecodeBuf.Length < floats) DecodeBuf = new float[Math.Max(floats, DecodeBuf.Length * 2)]; Array.Clear(DecodeBuf, 0, floats); return DecodeBuf; }
    public readonly int[,] AdpcmHist = new int[2, 4];
    public byte[] Staging = [];
    public int StagingLen;
    public Atrac9.Atrac9Decoder? At9;
    public uint At9Config;
    public short[][]? At9Out;
    // linear resampler
    public double ResamplePos;
    public float LastL, LastR;

    public int Available => (Pcm.Count - PcmRead * 2) / 2;

    public void Compact()
    {
        if (PcmRead == 0) return;
        Pcm.RemoveRange(0, Math.Min(PcmRead * 2, Pcm.Count));
        PcmRead = 0;
    }

    public void Reset()
    {
        Pcm.Clear();
        PcmRead = 0;
        LoopCount = 0;
        Array.Clear(AdpcmHist);
        StagingLen = 0;
        ResamplePos = 0;
        LastL = LastR = 0;
    }

    /// <summary>Appends source-rate stereo frames, resampling to the system rate if needed.</summary>
    public void Push(ReadOnlySpan<float> stereo, double ratio)
    {
        int frames = stereo.Length / 2;
        if (Math.Abs(ratio - 1.0) < 1e-6)
        {
            foreach (var s in stereo) Pcm.Add(s);
            if (frames > 0) { LastL = stereo[^2]; LastR = stereo[^1]; }
            return;
        }
        // linear interpolation between the previous block's last frame and this block
        double pos = ResamplePos;
        while (pos < frames)
        {
            int i = (int)Math.Floor(pos);
            float t = (float)(pos - i);
            float l0 = i == 0 ? LastL : stereo[(i - 1) * 2], r0 = i == 0 ? LastR : stereo[(i - 1) * 2 + 1];
            float l1 = stereo[i * 2], r1 = stereo[i * 2 + 1];
            Pcm.Add(l0 + (l1 - l0) * t);
            Pcm.Add(r0 + (r1 - r0) * t);
            pos += ratio;
        }
        ResamplePos = pos - frames;
        if (frames > 0) { LastL = stereo[^2]; LastR = stereo[^1]; }
    }
}
