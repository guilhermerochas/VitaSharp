using System.IO.Hashing;
using System.Text;

namespace VitaSharp.Core.Media;

/// <summary>
/// The guest-facing contract of the media decoders, recorded as text so that the desktop backends (FFmpeg) and the
/// Android ones (MediaCodec) can be compared line by line: the same synthetic clips go through the same calls the HLE
/// makes (sceAvcdecDecode feeds one access unit per call and takes the pictures that call yields; sceAudiodecDecode one
/// frame per call), and the trace records what came back per call.
/// <list type="bullet">
/// <item><c>video</c> lines: per call, the pictures it yielded (pts, size and a hash of the planes) or "-" for none.
/// The Vita's sceAvcdecDecode is synchronous: for a stream without reordering every call yields the picture of its
/// own unit, so the desktop and Android traces must be identical.</item>
/// <item><c>audio</c> lines: per frame, the samples per channel and the PCM (base64); decoders may round differently,
/// so <see cref="Compare"/> allows a stated difference per sample, never a different sample count.</item>
/// </list>
/// Used by the desktop tests (MediaParityTests) and by the developer APK's media parity run
/// (build/android-media-parity.sh, .ps1), whose trace the tests compare with the desktop's.
/// </summary>
public static class MediaContract
{
    /// <summary>First line of every trace (format version).</summary>
    public const string Header = "vitasharp-media-trace 1";

    /// <summary>Largest difference allowed between two decoders' 16-bit samples (AAC/MP3 decoders round differently:
    /// fixed-point FDK on Android, floating point in FFmpeg).</summary>
    public const int DefaultAudioTolerance = 64;

    /// <summary>The synthetic video clip both sides decode: 30 pictures of 64x64 (<see cref="SyntheticH264"/>).</summary>
    public static List<SyntheticH264.Unit> VideoClip() => SyntheticH264.Stream(30);

    /// <summary>
    /// Decodes <paramref name="units"/> one per call, as sceAvcdecDecode does, then flushes (sceAvcdecDecodeStop), and
    /// returns one line per call. <paramref name="pacingUs"/>: time between calls (a movie's frame period) so an
    /// asynchronous decoder gets the same time to work that it has in a game.
    /// </summary>
    public static List<string> VideoTrace(IVideoDecoder decoder, IReadOnlyList<SyntheticH264.Unit> units, string label, int pacingUs = 0)
    {
        var lines = new List<string>(units.Count + 1);
        for (int call = 0; call < units.Count; call++)
        {
            decoder.Send(units[call].AccessUnit, call);
            lines.Add(VideoLine(label, "send", call, decoder));
            if (pacingUs > 0)
            {
                Thread.Sleep(TimeSpan.FromMicroseconds(pacingUs));
            }
        }
        decoder.Flush();
        lines.Add(VideoLine(label, "flush", units.Count, decoder));
        return lines;
    }

    private static string VideoLine(string label, string operation, int call, IVideoDecoder decoder)
    {
        var pictures = new StringBuilder();
        while (decoder.Take() is { } picture)
        {
            if (pictures.Length > 0)
            {
                pictures.Append(',');
            }

            pictures.Append(picture.Pts).Append(':').Append(picture.Width).Append('x').Append(picture.Height).Append(':').Append(PictureHash(picture));
            decoder.Recycle(picture);
        }
        return $"video\t{label}\t{operation}\t{call}\t{(pictures.Length > 0 ? pictures.ToString() : "-")}";
    }

    /// <summary>A hash of the visible planes (Y, U, V), in hex.</summary>
    public static string PictureHash(DecodedPicture picture)
    {
        var hash = new XxHash64();
        int chromaBytes = ((picture.Width + 1) / 2) * ((picture.Height + 1) / 2);
        hash.Append(picture.Y.AsSpan(0, Math.Min(picture.Y.Length, picture.Width * picture.Height)));
        hash.Append(picture.U.AsSpan(0, Math.Min(picture.U.Length, chromaBytes)));
        hash.Append(picture.V.AsSpan(0, Math.Min(picture.V.Length, chromaBytes)));
        return Convert.ToHexString(hash.GetCurrentHash());
    }

    /// <summary>The hash of the planes a synthetic unit must decode to.</summary>
    public static string ExpectedHash(SyntheticH264.Unit unit, int width, int height) =>
        PictureHash(new DecodedPicture { Width = width, Height = height, Y = unit.Y, U = unit.U, V = unit.V });

    /// <summary>
    /// Decodes an ADTS AAC stream (<paramref name="mp3"/> false) or an MP3 stream frame by frame, as sceAudiodecDecode
    /// does, and returns one line per frame: samples per channel and the interleaved PCM in base64.
    /// </summary>
    public static unsafe List<string> AudioTrace(IAudioFrameDecoder decoder, byte[] stream, bool mp3, int channels, string label)
    {
        var lines = new List<string>();
        short[] pcm = new short[4608 * Math.Max(1, channels)];
        int offset = 0;
        int frame = 0;
        while (offset < stream.Length)
        {
            ReadOnlySpan<byte> rest = stream.AsSpan(offset);
            int length = mp3 ? FrameDecoder.Mp3FrameLength(rest) : FrameDecoder.AdtsFrameLength(rest);
            if (length <= 0 || length > rest.Length)
            {
                break;
            }

            int samples;
            fixed (byte* data = rest)
            {
                samples = decoder.Decode(data, length, pcm, channels);
            }
            byte[] bytes = new byte[samples * channels * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            lines.Add($"audio\t{label}\t{frame}\t{samples}\t{Convert.ToBase64String(bytes)}");
            offset += length;
            frame++;
        }
        return lines;
    }

    /// <summary>Labels of the clips in a trace.</summary>
    public const string VideoLabel = "h264-64x64", AacLabel = "aac-48k-stereo", Mp3Label = "mp3-44k-stereo";

    /// <summary>File names of the audio clips (tests/VitaSharp.Tests/Fixtures/Media; pushed to the device by the parity
    /// scripts).</summary>
    public const string AacClipFile = "tone48k-stereo.aac", Mp3ClipFile = "tone44k-stereo.mp3";

    /// <summary>
    /// The whole parity run, the same on every platform: the synthetic H.264 clip, then the AAC and MP3 clips, each
    /// through a new decoder from the given factories (a factory returning null records a "missing" line).
    /// </summary>
    public static List<string> Run(Func<IVideoDecoder?> video, Func<IAudioFrameDecoder?> aac, Func<IAudioFrameDecoder?> mp3,
        byte[] aacClip, byte[] mp3Clip, int videoPacingUs = 0)
    {
        var lines = new List<string>();
        using (IVideoDecoder? videoDecoder = video())
        {
            if (videoDecoder == null)
            {
                lines.Add($"missing\t{VideoLabel}");
            }
            else
            {
                lines.AddRange(VideoTrace(videoDecoder, VideoClip(), VideoLabel, videoPacingUs));
            }
        }

        foreach ((Func<IAudioFrameDecoder?> factory, byte[] clip, bool isMp3, string label) in new[] { (aac, aacClip, false, AacLabel), (mp3, mp3Clip, true, Mp3Label) })
        {
            using IAudioFrameDecoder? audioDecoder = factory();
            if (audioDecoder == null)
            {
                lines.Add($"missing\t{label}");
                continue;
            }

            lines.AddRange(AudioTrace(audioDecoder, clip, isMp3, 2, label));
        }
        return lines;
    }

    /// <summary><see cref="Run"/> with the decoders the HLE would create on this platform (<see cref="VideoDecoders"/>,
    /// <see cref="AudioFrameDecoders"/>: FFmpeg on the desktop, MediaCodec on Android).</summary>
    public static List<string> RunInstalledBackends(byte[] aacClip, byte[] mp3Clip, int videoPacingUs = 0) =>
        Run(() => VideoDecoders.CreateH264(new VideoDecoderOptions(64, 64)), () => AudioFrameDecoders.CreateAac(48000, 2, null),
            () => AudioFrameDecoders.CreateMp3(2), aacClip, mp3Clip, videoPacingUs);

    /// <summary>A whole trace: the header line, then the given lines.</summary>
    public static string Format(IEnumerable<string> lines) => Header + "\n" + string.Join("\n", lines) + "\n";

    /// <summary>The lines of a trace (header checked; empty lines dropped).</summary>
    public static List<string> Parse(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != Header)
        {
            throw new FormatException($"not a media trace (first line must be \"{Header}\")");
        }

        return lines.Skip(1).ToList();
    }

    /// <summary>
    /// The differences between two traces of the same clips, empty when they match: video lines must be identical;
    /// audio lines must have the same sample counts and samples within <paramref name="audioTolerance"/>.
    /// </summary>
    public static List<string> Compare(IReadOnlyList<string> expected, IReadOnlyList<string> actual, int audioTolerance = DefaultAudioTolerance)
    {
        var differences = new List<string>();
        int count = Math.Max(expected.Count, actual.Count);
        for (int i = 0; i < count; i++)
        {
            string? left = i < expected.Count ? expected[i] : null;
            string? right = i < actual.Count ? actual[i] : null;
            if (left == right)
            {
                continue;
            }

            if (left == null || right == null)
            {
                differences.Add($"line {i + 1}: only in {(left == null ? "the second" : "the first")} trace: {Shorten(left ?? right!)}");
                continue;
            }

            string[] leftFields = left.Split('\t');
            string[] rightFields = right.Split('\t');
            bool bothAudio = leftFields.Length == 5 && rightFields.Length == 5 && leftFields[0] == "audio" && rightFields[0] == "audio";
            if (!bothAudio || leftFields[1] != rightFields[1] || leftFields[2] != rightFields[2])
            {
                differences.Add($"line {i + 1}: {Shorten(left)}  <>  {Shorten(right)}");
                continue;
            }

            if (leftFields[3] != rightFields[3])
            {
                differences.Add($"line {i + 1}: {leftFields[1]} frame {leftFields[2]}: {leftFields[3]} samples <> {rightFields[3]}");
                continue;
            }

            int largest = LargestSampleDifference(Convert.FromBase64String(leftFields[4]), Convert.FromBase64String(rightFields[4]));
            if (largest > audioTolerance)
            {
                differences.Add($"line {i + 1}: {leftFields[1]} frame {leftFields[2]}: samples differ by up to {largest} (tolerance {audioTolerance})");
            }
        }
        return differences;
    }

    /// <summary>
    /// The comparison for a decoder that may hand pictures out later than the call of their unit (Android's MediaCodec
    /// after a stream's first picture, see SynchronousOutput.PipelinedAfterStart): the video must give the same pictures
    /// (pts, size, hash) in the same order and the first call must answer with the first picture, as on the Vita; the
    /// audio lines are compared as in <see cref="Compare"/>. <paramref name="largestLag"/>: the most calls a picture came
    /// out after its unit.
    /// </summary>
    public static List<string> CompareInOrder(IReadOnlyList<string> expected, IReadOnlyList<string> actual, out int largestLag,
        int audioTolerance = DefaultAudioTolerance)
    {
        var differences = new List<string>();
        List<(int Call, string Picture)> expectedPictures = VideoPictures(expected);
        List<(int Call, string Picture)> actualPictures = VideoPictures(actual);
        largestLag = 0;
        int count = Math.Max(expectedPictures.Count, actualPictures.Count);
        for (int i = 0; i < count; i++)
        {
            if (i >= expectedPictures.Count || i >= actualPictures.Count)
            {
                differences.Add($"picture {i}: only in {(i >= expectedPictures.Count ? "the second" : "the first")} trace");
                continue;
            }

            if (expectedPictures[i].Picture != actualPictures[i].Picture)
            {
                differences.Add($"picture {i}: {expectedPictures[i].Picture} <> {actualPictures[i].Picture}");
            }

            largestLag = Math.Max(largestLag, actualPictures[i].Call - expectedPictures[i].Call);
        }

        if (actualPictures.Count > 0 && expectedPictures.Count > 0 && actualPictures[0].Call != expectedPictures[0].Call)
        {
            differences.Add($"the first picture came out in call {actualPictures[0].Call}, not in call {expectedPictures[0].Call}");
        }

        var expectedAudio = expected.Where(line => !line.StartsWith("video\t", StringComparison.Ordinal)).ToList();
        var actualAudio = actual.Where(line => !line.StartsWith("video\t", StringComparison.Ordinal)).ToList();
        differences.AddRange(Compare(expectedAudio, actualAudio, audioTolerance));
        return differences;
    }

    /// <summary>(call, "pts:size:hash") of every picture of the video lines, in order.</summary>
    private static List<(int Call, string Picture)> VideoPictures(IReadOnlyList<string> lines)
    {
        var pictures = new List<(int Call, string Picture)>();
        foreach (string line in lines)
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 5 || fields[0] != "video" || fields[4] == "-")
            {
                continue;
            }

            int call = int.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture);
            foreach (string picture in fields[4].Split(','))
            {
                pictures.Add((call, picture));
            }
        }
        return pictures;
    }

    private static int LargestSampleDifference(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return int.MaxValue;
        }

        int largest = 0;
        for (int i = 0; i + 1 < left.Length; i += 2)
        {
            int leftSample = (short)(left[i] | (left[i + 1] << 8));
            int rightSample = (short)(right[i] | (right[i + 1] << 8));
            largest = Math.Max(largest, Math.Abs(leftSample - rightSample));
        }
        return largest;
    }

    private static string Shorten(string line) => line.Length <= 160 ? line : line[..160] + "...";
}
