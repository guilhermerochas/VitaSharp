using System.Globalization;
using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Capture;

/// <summary>The compressed audio the game hands to the decoders (sceAudiodec): a recording keeps it
/// (<see cref="AudioInputRecorder"/>), a re-run checks or puts it back (<see cref="CapturedAudioInputs"/>).</summary>
public interface IAudioInputHook
{
    /// <summary>Before decoder <paramref name="decoder"/> (<paramref name="codec"/>) decodes from
    /// <paramref name="stream"/> (the bytes it may read): a re-run puts the recording's bytes back there.</summary>
    void Decoding(uint decoder, uint codec, Span<byte> stream);

    /// <summary>The decoder consumed <paramref name="consumed"/> (the start of the stream it read).</summary>
    void Decoded(uint decoder, uint codec, ReadOnlySpan<byte> consumed);
}

/// <summary>One frame a decoder consumed: when (guest time), which decoder and codec, its number on that decoder,
/// its size and SHA-256.</summary>
public readonly record struct AudioInputFrame(long GuestUs, uint Decoder, uint Codec, int Index, int Length, ChunkHash Hash);

/// <summary>
/// What the game handed to its audio decoders while a capture records: every frame a decoder consumed (decoder,
/// codec, number, size, SHA-256) and, within <see cref="MaxBytes"/>, its bytes, kept once per distinct content. The
/// audio is the game's data, as the files it reads: by default only the hashes leave with the capture (the same limit
/// as the files, VITASHARP_CAPTURE_FILES_MB / --capture-files-mb, keeps the bytes). A re-run without the game then
/// puts the bytes back where the decoder reads them, or says where the stream differs from the recording.
/// <para>Files: <c>audio_inputs/frames.tsv</c> and <c>audio_inputs/blobs.bin</c>.</para>
/// </summary>
public sealed class AudioInputRecorder : IAudioInputHook
{
    public const string Folder = "audio_inputs", IndexFile = "frames.tsv", BlobFile = "blobs.bin";

    private readonly object _lock = new();
    private readonly List<(AudioInputFrame Frame, long Ns)> _frames = new();
    private readonly Dictionary<ChunkHash, byte[]> _blobs = new();
    private readonly Dictionary<uint, int> _next = new();
    private long _keptBytes;

    public AudioInputRecorder(long maxBytes)
    {
        MaxBytes = Math.Max(0, maxBytes);
    }

    /// <summary>Most bytes of distinct frame contents kept (the rest is listed with its hash only).</summary>
    public long MaxBytes { get; }

    public void Decoding(uint decoder, uint codec, Span<byte> stream)
    {
    }

    public void Decoded(uint decoder, uint codec, ReadOnlySpan<byte> consumed)
    {
        if (consumed.IsEmpty)
        {
            return;
        }

        var hash = ChunkHash.Of(consumed);
        lock (_lock)
        {
            int index = _next.GetValueOrDefault(decoder);
            _next[decoder] = index + 1;
            _frames.Add((new AudioInputFrame(GuestClock.Microseconds, decoder, codec, index, consumed.Length, hash), CaptureClock.NowNs));
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
    /// line (session.txt), or null when no decoder consumed anything.</summary>
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
            index.WriteLine("# guestUs\tdecoder\tcodec\tindex\tlength\tsha256\tblob");
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
                    $"{frame.GuestUs}\t{frame.Decoder}\t0x{frame.Codec:X}\t{frame.Index}\t{frame.Length}\t{frame.Hash}\t{blob}"));
            }

            int decoders = frames.Select(f => f.Decoder).Distinct().Count();
            return string.Create(CultureInfo.InvariantCulture,
                $"{frames.Count} frames of {decoders} audio decoders, {kept} with their bytes ({blobs.Length / 1024.0:F0} KB; the rest by hash: VITASHARP_CAPTURE_FILES_MB / --capture-files-mb keeps them)");
        }
    }
}

/// <summary>
/// A re-run's view of the audio a capture recorded going into the decoders (<see cref="AudioInputRecorder"/>): each
/// decoder's frames in order, from the state's guest time on. Before a decoder decodes, the frame it is at is checked
/// against the recording: when it differs and the recording kept its bytes, they are put back where the decoder
/// reads; when only its hash was kept, the difference is logged (once per decoder) and counted.
/// </summary>
public sealed class CapturedAudioInputs : IAudioInputHook
{
    private readonly Dictionary<uint, List<(AudioInputFrame Frame, byte[]? Bytes)>> _byDecoder = new();
    private readonly Dictionary<uint, int> _next = new();
    private readonly HashSet<uint> _warned = new();
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
    /// when the capture has none (no decoder ran, or an earlier build).</summary>
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
                || f[5].Length != 64 || !f[5].All(Uri.IsHexDigit))
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

            if (!inputs._byDecoder.TryGetValue(decoder, out List<(AudioInputFrame Frame, byte[]? Bytes)>? list))
            {
                list = [];
                inputs._byDecoder[decoder] = list;
            }

            list.Add((new AudioInputFrame(guestUs, decoder, codec, number, length, ChunkHash.Parse(f[5])), bytes));
            inputs.Frames++;
        }

        return inputs;
    }

    public void Decoding(uint decoder, uint codec, Span<byte> stream)
    {
        lock (_lock)
        {
            if (!_byDecoder.TryGetValue(decoder, out List<(AudioInputFrame Frame, byte[]? Bytes)>? list))
            {
                return;
            }

            int next = _next.GetValueOrDefault(decoder);
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
            if (_warned.Add(decoder))
            {
                Log.Warn($"[capture] re-run: audio decoder {decoder} (codec 0x{codec:X}) frame {frame.Index} is not the recording's "
                    + "(the recording kept its hash only; record with --capture-files-mb=MB to keep the bytes): the re-run decodes other audio from there");
            }
        }
    }

    public void Decoded(uint decoder, uint codec, ReadOnlySpan<byte> consumed)
    {
        lock (_lock)
        {
            _next[decoder] = _next.GetValueOrDefault(decoder) + 1;
        }
    }
}
