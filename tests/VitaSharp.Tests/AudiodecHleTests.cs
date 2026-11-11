using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceAudiodec: the library and decoder lifecycle with SCE_AUDIODEC_ERROR_* (0x807F00xx), the ATRAC9 decoder's
/// stream info from its config word, and an MP3 frame decoded through sceAudiodecDecode (an MPEG-1 Layer III frame
/// with empty side information: 1152 samples of silence per channel, 417 bytes at 128 kbps / 44.1 kHz).
/// </summary>
[Collection("guest")]
public sealed class AudiodecHleTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int INVALID_TYPE = unchecked((int)0x807F0001), NOT_INITIALIZED = unchecked((int)0x807F0005), INVALID_PTR = unchecked((int)0x807F0008),
        INVALID_HANDLE = unchecked((int)0x807F0009), NOT_HANDLE_IN_USE = unchecked((int)0x807F000A), INVALID_SIZE = unchecked((int)0x807F000D),
        MP3_INVALID_VERSION = unchecked((int)0x807F2801), INVALID_NSTREAMS = unchecked((int)0x807F0010), DIFFERENT_TYPES = unchecked((int)0x807F0011),
        SAME_HANDLES = unchecked((int)0x807F0012);
    private const uint AT9 = 0x1003, MP3 = 0x1004;

    private readonly HleHarness _harness = new(fixture, "audiodec");
    private Audiodec Audiodec => _harness.Module<Audiodec>();

    public void Dispose() => _harness.Dispose();

    private uint Ctrl(uint info)
    {
        uint c = _harness.Alloc(0x28);
        GuestMemory.W32(c, 0x28);
        GuestMemory.W32(c + 36, info);
        return c;
    }

    [Fact]
    public void LibraryAndDecoderLifecycle()
    {
        uint init = _harness.Alloc(16);
        Assert.Equal(INVALID_TYPE, Call(Audiodec, "sceAudiodecInitLibrary", 0x1000u, init));
        Assert.Equal(INVALID_PTR, Call(Audiodec, "sceAudiodecInitLibrary", MP3, 0u));
        Assert.Equal(NOT_INITIALIZED, Call(Audiodec, "sceAudiodecTermLibrary", MP3));
        uint info = _harness.Alloc(32);
        GuestMemory.W32(info, 0x10);
        GuestMemory.W32(info + 4, 2);
        GuestMemory.W32(info + 8, 3); // stereo MPEG-1
        uint ctrl = Ctrl(info);
        Assert.Equal(NOT_INITIALIZED, Call(Audiodec, "sceAudiodecCreateDecoder", ctrl, MP3));
        Assert.Equal(0, Call(Audiodec, "sceAudiodecInitLibrary", MP3, init));
        Assert.Equal(INVALID_PTR, Call(Audiodec, "sceAudiodecCreateDecoder", 0u, MP3));
        uint bad = _harness.Alloc(0x28);
        GuestMemory.W32(bad, 0x24);
        Assert.Equal(INVALID_SIZE, Call(Audiodec, "sceAudiodecCreateDecoder", bad, MP3));
        Assert.Equal(INVALID_TYPE, Call(Audiodec, "sceAudiodecCreateDecoder", ctrl, 0x2000u));
        Assert.Equal(INVALID_PTR, Call(Audiodec, "sceAudiodecCreateDecoder", Ctrl(0), MP3));
        GuestMemory.W32(info + 8, 1); // no such MPEG version
        Assert.Equal(MP3_INVALID_VERSION, Call(Audiodec, "sceAudiodecCreateDecoder", ctrl, MP3));
        GuestMemory.W32(info + 8, 3);
        Assert.Equal(0, Call(Audiodec, "sceAudiodecCreateDecoder", ctrl, MP3));
        Assert.NotEqual(0u, GuestMemory.R32(ctrl + 4));
        Assert.Equal(1441u, GuestMemory.R32(ctrl + 16));      // maxEsSize: the largest MP3 frame
        Assert.Equal(2u * 2304, GuestMemory.R32(ctrl + 28));  // maxPcmSize: 1152 stereo S16 samples
        Assert.Equal(0x10000, Call(Audiodec, "sceAudiodecGetContextSize", ctrl, MP3));

        // lookups: a zero handle, a handle not in use, a bad struct size
        uint stale = Ctrl(info);
        Assert.Equal(INVALID_HANDLE, Call(Audiodec, "sceAudiodecClearContext", stale));
        GuestMemory.W32(stale + 4, 0x7777);
        Assert.Equal(NOT_HANDLE_IN_USE, Call(Audiodec, "sceAudiodecClearContext", stale));
        Assert.Equal(INVALID_SIZE, Call(Audiodec, "sceAudiodecDecode", bad));
        Assert.Equal(INVALID_PTR, Call(Audiodec, "sceAudiodecDecode", 0u));
        Assert.Equal(INVALID_PTR, Call(Audiodec, "sceAudiodecDecode", ctrl)); // no ES / PCM buffers yet
        Assert.Equal(0, Call(Audiodec, "sceAudiodecClearContext", ctrl));

        // N streams: same codec, distinct handles
        Assert.Equal(INVALID_PTR, Call(Audiodec, "sceAudiodecDecodeNStreams", 0u, 1u));
        uint arr = _harness.Alloc(8);
        GuestMemory.W32(arr, ctrl);
        GuestMemory.W32(arr + 4, ctrl);
        Assert.Equal(INVALID_NSTREAMS, Call(Audiodec, "sceAudiodecDecodeNStreams", arr, 0u));
        Assert.Equal(SAME_HANDLES, Call(Audiodec, "sceAudiodecDecodeNStreams", arr, 2u));
        uint at9Info = _harness.Alloc(32);
        GuestMemory.W32(at9Info, 0x1C);
        new byte[] { 0xFE, 0x74, 0x1F, 0xF0 }.CopyTo(GuestMemory.Span(at9Info + 4, 4));
        uint at9 = Ctrl(at9Info);
        Assert.Equal(0, Call(Audiodec, "sceAudiodecCreateDecoderExternal", at9, AT9, _harness.Alloc(16), 16u));
        GuestMemory.W32(arr + 4, at9);
        Assert.Equal(DIFFERENT_TYPES, Call(Audiodec, "sceAudiodecDecodeNStreams", arr, 2u));

        Assert.Equal(0, Call(Audiodec, "sceAudiodecDeleteDecoder", ctrl));
        Assert.Equal(NOT_HANDLE_IN_USE, Call(Audiodec, "sceAudiodecDeleteDecoder", ctrl));
        Assert.Equal(0, Call(Audiodec, "sceAudiodecDeleteDecoder", at9));
        Assert.Equal(0, Call(Audiodec, "sceAudiodecTermLibrary", MP3));
        Assert.Equal(INVALID_TYPE, Call(Audiodec, "sceAudiodecTermLibrary", 0u));
    }

    [Fact]
    public void Atrac9InfoComesFromTheConfigWord()
    {
        uint info = _harness.Alloc(32);
        GuestMemory.W32(info, 0x1C);
        new byte[] { 0xFE, 0x74, 0x1F, 0xF0 }.CopyTo(GuestMemory.Span(info + 4, 4)); // stereo, 48 kHz
        uint ctrl = Ctrl(info);
        Assert.Equal(0, Call(Audiodec, "sceAudiodecCreateDecoderResident", ctrl, AT9));
        Assert.Equal(2u, GuestMemory.R32(info + 8));       // channels
        Assert.Equal(48000u, GuestMemory.R32(info + 16));  // sampling rate
        uint sfBytes = GuestMemory.R32(info + 20), frames = GuestMemory.R32(info + 24);
        Assert.True(sfBytes > 0 && frames is 1 or 4);
        Assert.True(GuestMemory.R32(info + 12) > 0);       // bit rate (kbps)
        Assert.Equal(2u * 2 * 256, GuestMemory.R32(ctrl + 28)); // one 256-sample stereo frame
        Assert.Equal(0, Call(Audiodec, "sceAudiodecDeleteDecoderResident", ctrl));
        // a config without the sync byte is refused
        uint bad = _harness.Alloc(32);
        GuestMemory.W32(bad, 0x1C);
        GuestMemory.W32(bad + 4, 0x12345678);
        Assert.Equal(unchecked((int)0x807F2000), Call(Audiodec, "sceAudiodecCreateDecoderResident", Ctrl(bad), AT9));
    }

    [Fact]
    public void Mp3FrameDecodesToSilence()
    {
        Assert.Equal(0, Call(Audiodec, "sceAudiodecInitLibrary", MP3, _harness.Alloc(16)));
        uint info = _harness.Alloc(32);
        GuestMemory.W32(info, 0x10);
        GuestMemory.W32(info + 4, 2);
        GuestMemory.W32(info + 8, 3);
        uint ctrl = Ctrl(info);
        Assert.Equal(0, Call(Audiodec, "sceAudiodecCreateDecoder", ctrl, MP3));
        // two frames: header FF FB 90 64 (MPEG-1 Layer III, 128 kbps, 44.1 kHz, joint stereo), zero side info and data
        uint es = _harness.Alloc(417 * 2 + 64), pcm = _harness.Alloc(2 * 2304 * 2);
        for (uint f = 0; f < 2; f++)
        {
            new byte[] { 0xFF, 0xFB, 0x90, 0x64 }.CopyTo(GuestMemory.Span(es + f * 417, 4));
        }

        GuestMemory.Fill(pcm, 0x55, 2 * 2304 * 2);
        GuestMemory.W32(ctrl + 8, es);
        GuestMemory.W32(ctrl + 16, 417 * 2);
        GuestMemory.W32(ctrl + 20, pcm);
        Assert.Equal(0, Call(Audiodec, "sceAudiodecDecode", ctrl));
        Assert.Equal(417u, GuestMemory.R32(ctrl + 12));    // inputEsSize: one frame consumed
        Assert.Equal(1152u * 4, GuestMemory.R32(ctrl + 24)); // outputPcmSize: 1152 stereo S16 samples
        Assert.All(GuestMemory.ReadSpan(pcm, 1152 * 4).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0, Call(Audiodec, "sceAudiodecDecodeNFrames", ctrl, 2u));
        Assert.Equal(417u * 2, GuestMemory.R32(ctrl + 12));
        Assert.Equal(1152u * 4 * 2, GuestMemory.R32(ctrl + 24));
        // a buffer that holds no frame header
        GuestMemory.Fill(es, 0, 8);
        Assert.Equal(unchecked((int)0x807F0000), Call(Audiodec, "sceAudiodecDecode", ctrl));
        Assert.Equal(0, Call(Audiodec, "sceAudiodecDeleteDecoder", ctrl));
    }
}
