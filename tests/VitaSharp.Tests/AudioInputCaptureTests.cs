using VitaSharp.Core.Capture;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// "Gravar tudo" keeps what the game hands to its audio decoders: each frame a decoder consumed (by hash, and with
/// its bytes only within the files limit, as the game's files), and a re-run without the game puts the bytes back
/// where the decoder reads them, or counts a frame that differs when only its hash was kept. Before, only the decoded
/// output (WAV) was recorded. Here a CELP decoder (12200 bps: 16-byte frames) decodes two frames.
/// </summary>
[Collection("guest")]
public sealed class AudioInputCaptureTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint Celp = 0x1006;
    private const int FrameBytes = 16;

    private readonly HleHarness _harness = new(fixture, "audioinputs");
    private readonly string _capture = Path.Combine(Path.GetTempPath(), "vitasharp-audioinputs-" + Guid.NewGuid().ToString("N"));

    private Audiodec Audiodec => _harness.Module<Audiodec>();

    public void Dispose()
    {
        CaptureHooks.AudioInputs = null;
        _harness.Dispose();
        try
        {
            Directory.Delete(_capture, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A CELP decoder (its control block) reading from <paramref name="stream"/>.</summary>
    private uint Decoder(uint stream)
    {
        uint info = _harness.Alloc(0x14);
        GuestMemory.W32(info, 0x14);
        GuestMemory.W32(info + 8, 8000);
        GuestMemory.W32(info + 12, 12200);
        uint ctrl = _harness.Alloc(0x28);
        GuestMemory.W32(ctrl, 0x28);
        GuestMemory.W32(ctrl + 36, info);
        Assert.Equal(0, Call(Audiodec, "sceAudiodecCreateDecoder", ctrl, Celp));
        GuestMemory.W32(ctrl + 8, stream);
        GuestMemory.W32(ctrl + 20, _harness.Alloc(4096));
        return ctrl;
    }

    private void DecodeTwoFrames(uint ctrl)
    {
        Assert.Equal(0, Call(Audiodec, "sceAudiodecDecodeNFrames", ctrl, 2u));
    }

    [Theory]
    [InlineData(1 << 20)]
    [InlineData(0)]
    public void TheDecodersInputIsRecordedAndARerunPutsItBackOrSaysItDiffers(long maxBytes)
    {
        Assert.Equal(0, Call(Audiodec, "sceAudiodecInitLibrary", Celp, _harness.Alloc(16)));
        uint stream = _harness.Alloc(64);
        for (uint i = 0; i < 2 * FrameBytes; i++)
        {
            GuestMemory.Write(stream + i, (byte)(0x40 + i));
        }

        byte[] original = GuestMemory.ReadSpan(stream, 2 * FrameBytes).ToArray();
        uint ctrl = Decoder(stream);
        var recorder = new AudioInputRecorder(maxBytes);
        CaptureHooks.AudioInputs = recorder;
        DecodeTwoFrames(ctrl);
        CaptureHooks.AudioInputs = null;
        string? summary = recorder.Write(_capture, long.MinValue);
        Assert.NotNull(summary);
        Assert.StartsWith("2 frames (1 sceAudiodec decoders, 0 NGS voices, 0 libatrac streams)", summary);
        string[] lines = File.ReadAllLines(Path.Combine(_capture, AudioInputRecorder.Folder, AudioInputRecorder.IndexFile));
        Assert.Equal(3, lines.Length); // header and two frames
        Assert.Contains($"\t0x1006\t0\t16\t{ChunkHash.Of(original.AsSpan(0, FrameBytes))}\t", lines[1]);
        Assert.EndsWith("\taudiodec", lines[1]);

        // the re-run: the stream the guest has is not the game's (no game here)
        CapturedAudioInputs replay = CapturedAudioInputs.Load(_capture, long.MinValue)!;
        Assert.Equal(2, replay.Frames);
        GuestMemory.Fill(stream, 0, 2 * FrameBytes);
        CaptureHooks.AudioInputs = replay;
        DecodeTwoFrames(ctrl);
        CaptureHooks.AudioInputs = null;
        if (maxBytes > 0)
        {
            Assert.Equal(2, replay.Restored);
            Assert.Equal(original, GuestMemory.ReadSpan(stream, 2 * FrameBytes).ToArray());
        }
        else
        {
            Assert.Equal(0, replay.Restored);
            Assert.Equal(2, replay.Differ);
            Assert.Equal(new byte[2 * FrameBytes], GuestMemory.ReadSpan(stream, 2 * FrameBytes).ToArray());
        }
    }
}
