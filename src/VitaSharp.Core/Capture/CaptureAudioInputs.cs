using System.Globalization;
using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Capture;

/// <summary>Which reader of the game's compressed or raw audio a recorded frame went into.</summary>
public enum AudioInputSource : byte
{
    /// <summary>An sceAudiodec decoder (ATRAC9, MP3, AAC, CELP); the stream id is its handle.</summary>
    Audiodec,

    /// <summary>The source module (player or ATRAC9) of an NGS voice; the stream id is the voice handle.</summary>
    NgsVoice,

    /// <summary>A libatrac (sceAtrac) handle, which decodes on its own (not through sceAudiodec); the stream id is the
    /// guest address of its data buffer.</summary>
    Atrac,
}

/// <summary>The codec column of <c>audio_inputs/frames.tsv</c>: the sceAudiodec type for the decoders (0x1003 ATRAC9,
/// 0x1004 MP3, 0x1005 AAC, 0x1006 CELP) and, for the NGS player, these two.</summary>
public static class AudioInputCodec
{
    /// <summary>16-bit PCM (an NGS player buffer of type PCM).</summary>
    public const uint Pcm16 = 0;

    /// <summary>HEVAG ADPCM (an NGS player buffer of type ADPCM).</summary>
    public const uint Hevag = 1;

    /// <summary>ATRAC9 (as the sceAudiodec type).</summary>
    public const uint Atrac9 = 0x1003;
}

/// <summary>The audio the game hands to the decoders (sceAudiodec, libatrac) and to the NGS voices: a recording keeps
/// it (<see cref="AudioInputRecorder"/>), a re-run checks or puts it back (<see cref="CapturedAudioInputs"/>).</summary>
public interface IAudioInputHook
{
    /// <summary>Before stream <paramref name="id"/> of <paramref name="source"/> (<paramref name="codec"/>) reads from
    /// <paramref name="stream"/> (the bytes it may read): a re-run puts the recording's bytes back there.</summary>
    void Decoding(AudioInputSource source, uint id, uint codec, Span<byte> stream);

    /// <summary>The stream consumed <paramref name="consumed"/> (the start of the stream it read).</summary>
    void Decoded(AudioInputSource source, uint id, uint codec, ReadOnlySpan<byte> consumed);
}

/// <summary>One frame a stream consumed: when (guest time), which source, stream and codec, its number on that stream,
/// its size and SHA-256.</summary>
public readonly record struct AudioInputFrame(long GuestUs, AudioInputSource Source, uint Decoder, uint Codec, int Index, int Length, ChunkHash Hash);

/// <summary>The <c>frames.tsv</c> names of <see cref="AudioInputSource"/> (a fixed file format: the names are the
/// column's values).</summary>
internal static class AudioInputSourceNames
{
    public static string Name(AudioInputSource source) => source switch
    {
        AudioInputSource.NgsVoice => "ngs",
        AudioInputSource.Atrac => "atrac",
        _ => "audiodec",
    };

    /// <summary>A missing column (a capture from before the NGS voices and libatrac were recorded) is sceAudiodec.</summary>
    public static bool TryParse(string? name, out AudioInputSource source)
    {
        switch (name)
        {
            case null:
            case "audiodec":
                source = AudioInputSource.Audiodec;
                return true;
            case "ngs":
                source = AudioInputSource.NgsVoice;
                return true;
            case "atrac":
                source = AudioInputSource.Atrac;
                return true;
            default:
                source = AudioInputSource.Audiodec;
                return false;
        }
    }

    public static string Describe(AudioInputSource source) => source switch
    {
        AudioInputSource.NgsVoice => "NGS voice",
        AudioInputSource.Atrac => "libatrac stream",
        _ => "audio decoder",
    };

    public static ulong Key(AudioInputSource source, uint id) => ((ulong)source << 32) | id;

    /// <summary>"a sceAudiodec decoders, b NGS voices, c libatrac streams" over the distinct streams of
    /// <paramref name="keys"/>.</summary>
    public static string Streams(IEnumerable<ulong> keys)
    {
        int decoders = 0;
        int voices = 0;
        int atrac = 0;
        foreach (ulong key in keys)
        {
            switch ((AudioInputSource)(key >> 32))
            {
                case AudioInputSource.NgsVoice:
                    voices++;
                    break;
                case AudioInputSource.Atrac:
                    atrac++;
                    break;
                default:
                    decoders++;
                    break;
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{decoders} sceAudiodec decoders, {voices} NGS voices, {atrac} libatrac streams");
    }
}

/// <summary>
/// What the game handed to its audio decoders and NGS voices while a capture records: every frame a stream consumed
/// (source, stream, codec, number, size, SHA-256) and, within <see cref="MaxBytes"/>, its bytes, kept once per
/// distinct content. The audio is the game's data, as the files it reads: by default only the hashes leave with the
/// capture (the same limit as the files, VITASHARP_CAPTURE_FILES_MB / --capture-files-mb, keeps the bytes). A re-run
/// without the game then puts the bytes back where the stream reads them, or says where it differs from the recording.
/// <para>Files: <c>audio_inputs/frames.tsv</c> and <c>audio_inputs/blobs.bin</c>.</para>
/// </summary>
public sealed class AudioInputRecorder : IAudioInputHook
{
    public const string Folder = "audio_inputs", IndexFile = "frames.tsv", BlobFile = "blobs.bin";

    private readonly object _lock = new();
    private readonly List<(AudioInputFrame Frame, long Ns)> _frames = new();
    private readonly Dictionary<ChunkHash, byte[]> _blobs = new();
    private readonly Dictionary<ulong, int> _next = new();
    private long _keptBytes;

    public AudioInputRecorder(long maxBytes)
    {
        MaxBytes = Math.Max(0, maxBytes);
    }

    /// <summary>Most bytes of distinct frame contents kept (the rest is listed with its hash only).</summary>
    public long MaxBytes { get; }

    public void Decoding(AudioInputSource source, uint id, uint codec, Span<byte> stream)
    {
    }

    public void Decoded(AudioInputSource source, uint id, uint codec, ReadOnlySpan<byte> consumed)
    {
        if (consumed.IsEmpty)
        {
            return;
        }

        var hash = ChunkHash.Of(consumed);
        lock (_lock)
        {
            ulong key = AudioInputSourceNames.Key(source, id);
            int index = _next.GetValueOrDefault(key);
            _next[key] = index + 1;
            _frames.Add((new AudioInputFrame(GuestClock.Microseconds, source, id, codec, index, consumed.Length, hash), CaptureClock.NowNs));
            if (!_blobs.ContainsKey(hash) && _keptBytes + consumed.Length <= MaxBytes)
            {
                _blobs[hash] = consumed.ToArray();
                _keptBytes += consumed.Length;
            }
        }
    }

    /// <summary>Pre-roll: drops the frames consumed before <paramref name="keepFromNs"/> and the contents no frame
    /// still needs.</summary>
    public void Trim(long keepFromNs)
    {
        lock (_lock)
        {
            int drop = _frames.FindIndex(f => f.Ns >= keepFromNs);
            _frames.RemoveRange(0, drop < 0 ? _frames.Count : drop);
            var used = _frames.Select(f => f.Frame.Hash).ToHashSet();
            foreach (ChunkHash hash in _blobs.Keys.Where(h => !used.Contains(h)).ToList())
            {
                _keptBytes -= _blobs[hash].Length;
                _blobs.Remove(hash);
            }
        }
    }

    /// <summary>Writes audio_inputs/ with the frames consumed from <paramref name="fromNs"/> on; returns a summary
    /// line (session.txt), or null when no stream consumed anything.</summary>
    public string? Write(string captureDir, long fromNs)
    {
        lock (_lock)
        {
            var frames = _frames.Where(f => f.Ns >= fromNs).Select(f => f.Frame).ToList();
            if (frames.Count == 0)
            {
                return null;
            }

            string dir = Path.Combine(captureDir, Folder);
            Directory.CreateDirectory(dir);
            var offsets = new Dictionary<ChunkHash, long>();
            int kept = 0;
            using var blobs = new FileStream(Path.Combine(dir, BlobFile), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            using var index = new StreamWriter(Path.Combine(dir, IndexFile), false, new UTF8Encoding(false));
            index.WriteLine("# guestUs\tdecoder\tcodec\tindex\tlength\tsha256\tblob\tsource");
            foreach (AudioInputFrame frame in frames)
            {
                string blob = "-";
                if (offsets.TryGetValue(frame.Hash, out long at))
                {
                    blob = at.ToString(CultureInfo.InvariantCulture);
                }
                else if (_blobs.TryGetValue(frame.Hash, out byte[]? data))
                {
                    offsets[frame.Hash] = blobs.Position;
                    blob = blobs.Position.ToString(CultureInfo.InvariantCulture);
                    blobs.Write(data);
                }

                if (blob != "-")
                {
                    kept++;
                }

                index.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{frame.GuestUs}\t{frame.Decoder}\t0x{frame.Codec:X}\t{frame.Index}\t{frame.Length}\t{frame.Hash}\t{blob}\t{AudioInputSourceNames.Name(frame.Source)}"));
            }

            string streams = AudioInputSourceNames.Streams(frames.Select(f => AudioInputSourceNames.Key(f.Source, f.Decoder)).Distinct());
            return string.Create(CultureInfo.InvariantCulture,
                $"{frames.Count} frames ({streams}), {kept} with their bytes ({blobs.Length / 1024.0:F0} KB; the rest by hash: VITASHARP_CAPTURE_FILES_MB / --capture-files-mb keeps them)");
        }
    }
}

/// <summary>
/// A re-run's view of the audio a capture recorded going into the decoders and NGS voices
/// (<see cref="AudioInputRecorder"/>): each stream's frames in order, from the state's guest time on. Before a stream
/// reads, the frame it is at is checked against the recording: when it differs and the recording kept its bytes, they
/// are put back where the stream reads; when only its hash was kept, the difference is logged (once per stream) and
/// counted.
/// </summary>
public sealed class CapturedAudioInputs : IAudioInputHook
{
    private readonly Dictionary<ulong, List<(AudioInputFrame Frame, byte[]? Bytes)>> _byStream = new();
    private readonly Dictionary<ulong, int> _next = new();
    private readonly HashSet<ulong> _warned = new();
    private readonly object _lock = new();

    /// <summary>Frames the recording listed (from the state on).</summary>
    public int Frames { get; private set; }

    /// <summary>Frames whose bytes the re-run put back (the stream the guest had differed).</summary>
    public int Restored { get; private set; }

    /// <summary>Frames that differ from the recording and could not be put back (hash only).</summary>
    public int Differ { get; private set; }

    private CapturedAudioInputs()
    {
    }

    /// <summary>The audio inputs of <paramref name="captureDir"/> from guest time <paramref name="fromUs"/> on; null
    /// when the capture has none (no decoder or voice ran, or an earlier build).</summary>
    public static CapturedAudioInputs? Load(string captureDir, long fromUs)
    {
        string index = Path.Combine(captureDir, AudioInputRecorder.Folder, AudioInputRecorder.IndexFile);
        if (!File.Exists(index))
        {
            return null;
        }

        var inputs = new CapturedAudioInputs();
        string blobPath = Path.Combine(captureDir, AudioInputRecorder.Folder, AudioInputRecorder.BlobFile);
        using FileStream? blobs = File.Exists(blobPath) ? File.OpenRead(blobPath) : null;
        foreach (string line in File.ReadLines(index))
        {
            string[] f = line.Split('\t');
            if (line.StartsWith('#') || f.Length < 7
                || !long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long guestUs)
                || !uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint decoder)
                || !f[2].StartsWith("0x", StringComparison.Ordinal)
                || !uint.TryParse(f[2].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint codec)
                || !int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                || !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) || length <= 0
                || f[5].Length != 64 || !f[5].All(Uri.IsHexDigit)
                || !AudioInputSourceNames.TryParse(f.Length > 7 ? f[7] : null, out AudioInputSource source))
            {
                continue;
            }

            if (guestUs < fromUs)
            {
                continue;
            }

            byte[]? bytes = null;
            if (blobs != null && long.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)
                && at >= 0 && at + length <= blobs.Length)
            {
                bytes = new byte[length];
                blobs.Position = at;
                blobs.ReadExactly(bytes);
            }

            ulong key = AudioInputSourceNames.Key(source, decoder);
            if (!inputs._byStream.TryGetValue(key, out List<(AudioInputFrame Frame, byte[]? Bytes)>? list))
            {
                list = [];
                inputs._byStream[key] = list;
            }

            list.Add((new AudioInputFrame(guestUs, source, decoder, codec, number, length, ChunkHash.Parse(f[5])), bytes));
            inputs.Frames++;
        }

        return inputs;
    }

    /// <summary>The re-run summary's line: what the recording listed, by stream kind, and what the re-run did.</summary>
    public string Describe()
    {
        lock (_lock)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"audio inputs: {Frames} frames recorded ({AudioInputSourceNames.Streams(_byStream.Keys)}), {Restored} put back, {Differ} different without their bytes (the log names the first frame of each stream)");
        }
    }

    public void Decoding(AudioInputSource source, uint id, uint codec, Span<byte> stream)
    {
        lock (_lock)
        {
            ulong key = AudioInputSourceNames.Key(source, id);
            if (!_byStream.TryGetValue(key, out List<(AudioInputFrame Frame, byte[]? Bytes)>? list))
            {
                return;
            }

            int next = _next.GetValueOrDefault(key);
            if (next >= list.Count)
            {
                return;
            }

            (AudioInputFrame frame, byte[]? bytes) = list[next];
            if (frame.Length > stream.Length || ChunkHash.Of(stream[..frame.Length]) == frame.Hash)
            {
                return;
            }

            if (bytes != null)
            {
                bytes.CopyTo(stream);
                Restored++;
                return;
            }

            Differ++;
            if (_warned.Add(key))
            {
                Log.Warn($"[capture] re-run: {AudioInputSourceNames.Describe(source)} {id} (codec 0x{codec:X}) frame {frame.Index} is not the recording's "
                    + "(the recording kept its hash only; record with --capture-files-mb=MB to keep the bytes): the re-run plays other audio from there");
            }
        }
    }

    public void Decoded(AudioInputSource source, uint id, uint codec, ReadOnlySpan<byte> consumed)
    {
        if (consumed.IsEmpty)
        {
            // the recording lists no empty frame: the stream stays on the same one
            return;
        }

        lock (_lock)
        {
            ulong key = AudioInputSourceNames.Key(source, id);
            _next[key] = _next.GetValueOrDefault(key) + 1;
        }
    }
}
