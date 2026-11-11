using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace VitaSharp.Core.Capture;

/// <summary>
/// One presented frame as the renderer showed it (session capture, docs/notes/session-capture.md). Times are host
/// nanoseconds since the capture's clock origin (<see cref="CaptureClock"/>); the guest flip number, vblank count and
/// guest clock come from the sceDisplaySetFrameBuf that queued the picture (-1 when the renderer presented by itself,
/// e.g. the process coming back to the front).
/// </summary>
public struct PresentInfo
{
    /// <summary>Renderer present number (the numbers of GPU dumps, frame_N.png and frames.txt).</summary>
    public long Present;
    /// <summary>Guest flip number: frames presented by the display when this one was set (-1 = not a guest flip).</summary>
    public long Flip;
    public long VBlank;
    public long GuestUs;
    /// <summary>Host time of the guest's sceDisplaySetFrameBuf, and of the renderer's present (the swapchain present
    /// follows in the same call).</summary>
    public long FlipNs, PresentNs;
    public float IntervalMs, GpuMs, HitchMs;
    public int Draws, Scenes, NewShaders, NewPipelines;
    /// <summary>The biggest stall cause of the frame (FrameLedger category name), or empty.</summary>
    public string Hitch;
    public uint Address, SrcW, SrcH;
}

/// <summary>Receives every presented frame while a session capture is armed or recording. Called on the GPU worker
/// when the frame retired; <paramref name="rgba"/> is only valid during the call.</summary>
public interface IPresentTap
{
    void OnPresent(in PresentInfo info, ReadOnlySpan<byte> rgba, int w, int h);
}

/// <summary>The capture's host clock: nanoseconds from an origin shared by every file of one capture.</summary>
public static class CaptureClock
{
    static readonly long Origin = System.Diagnostics.Stopwatch.GetTimestamp();
    public static long FromTicks(long ticks) => (long)((ticks - Origin) * (1e9 / System.Diagnostics.Stopwatch.Frequency));
    public static long NowNs => FromTicks(System.Diagnostics.Stopwatch.GetTimestamp());
}

/// <summary>
/// <c>frames.vsfr</c>: every presented frame of a capture as QOI pictures with their timing.
/// <code>
/// file   = "VSFRAMES" u32 version record*
/// record = u32 bytes (of what follows) | i64 present flip vblank guestUs flipNs presentNs | f32 intervalMs gpuMs hitchMs
///          | i32 draws scenes newShaders newPipelines | u32 address srcW srcH | u8 flags
///          | string hitch (u8 length, UTF-8) | u32 pictureBytes | picture
/// flags  = 1 pre-roll, 2 no picture (the encoder fell behind), 4 key picture: a plain QOI image;
///          8 delta: Brotli(QOI(this picture - the previous picture, per byte, mod 256)), against the last record
///          that has a picture. A key picture comes every <see cref="KeyInterval"/> pictures and after any gap.
/// </code>
/// Game frames change little from one to the next, so the deltas are 5-10x smaller than the pictures (FIFA 13
/// menus: 138 KB per QOI picture, 23 KB per QOI delta, ~13 KB after Brotli). A file cut short (crash) reads up to
/// its last complete record.
/// </summary>
public static class FramesFile
{
    public static ReadOnlySpan<byte> Magic => "VSFRAMES"u8;
    public const int Version = 1;
    public const byte FlagPreRoll = 1, FlagNoPicture = 2, FlagKey = 4, FlagDelta = 8;
    public const int KeyInterval = 30;

    /// <summary>Encodes the pictures of one stream: a key picture every <see cref="KeyInterval"/>, deltas between.
    /// Not thread-safe (the capture's encoder thread).</summary>
    public sealed class Encoder
    {
        byte[]? _prev;
        int _prevW, _prevH, _sinceKey;
        byte[] _qoi = [], _delta = [];

        /// <summary>The next picture starts a new chain (a gap, or a new stream).</summary>
        public void ForceKey() => _prev = null;

        /// <summary>Returns the encoded picture and its flags (<see cref="FlagKey"/> or <see cref="FlagDelta"/>).</summary>
        public (byte[] Data, byte Flags) Encode(ReadOnlySpan<byte> rgba, int w, int h)
        {
            int bytes = w * h * 4, max = Qoi.MaxSize(w, h);
            if (_qoi.Length < max)
            {
                _qoi = new byte[max];
            }

            bool key = _prev == null || _prevW != w || _prevH != h || ++_sinceKey >= KeyInterval;
            byte[] result;
            if (key)
            {
                int n = Qoi.Encode(rgba, w, h, _qoi);
                result = _qoi.AsSpan(0, n).ToArray();
                _sinceKey = 0;
            }
            else
            {
                if (_delta.Length < bytes)
                {
                    _delta = new byte[bytes];
                }

                var prev = _prev!;
                for (int i = 0; i < bytes; i++)
                {
                    _delta[i] = (byte)(rgba[i] - prev[i]);
                }

                int n = Qoi.Encode(_delta.AsSpan(0, bytes), w, h, _qoi);
                using var ms = new MemoryStream(n / 2 + 64);
                using (var z = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                {
                    z.Write(_qoi, 0, n);
                }

                result = ms.ToArray();
            }
            if (_prev == null || _prev.Length != bytes)
            {
                _prev = new byte[bytes];
            }

            rgba[..bytes].CopyTo(_prev);
            _prevW = w;
            _prevH = h;
            return (result, key ? FlagKey : FlagDelta);
        }
    }

    /// <summary>Decodes the pictures of a frames file in order (a delta needs the picture before it).</summary>
    public sealed class Decoder
    {
        byte[]? _prev;

        public (byte[] Rgba, int W, int H)? Decode(Frame f, ReadOnlySpan<byte> data)
        {
            if (!f.HasPicture)
            {
                return null;
            }

            if ((f.Flags & FlagDelta) == 0)
            {
                var k = Qoi.Decode(data);
                _prev = k.Rgba;
                return (k.Rgba.ToArray(), k.W, k.H);
            }
            if (_prev == null)
            {
                return null; // a delta without its key picture (the start of a cut file)
            }

            using var z = new BrotliStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            z.CopyTo(raw);
            var d = Qoi.Decode(raw.GetBuffer().AsSpan(0, (int)raw.Length));
            if (d.Rgba.Length != _prev.Length)
            {
                return null;
            }

            var px = d.Rgba;
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = (byte)(px[i] + _prev[i]);
            }

            for (int i = 3; i < px.Length; i += 4)
            {
                px[i] = 255;
            }

            _prev = px;
            return (px.ToArray(), d.W, d.H);
        }
    }

    /// <summary>Every picture of the file in order (null for a frame without one).</summary>
    public static IEnumerable<(Frame Frame, (byte[] Rgba, int W, int H)? Picture)> ReadAll(string path)
    {
        var index = ReadIndex(path);
        var dec = new Decoder();
        using var s = File.OpenRead(path);
        var buf = Array.Empty<byte>();
        foreach (var f in index)
        {
            if (!f.HasPicture)
            {
                yield return (f, null);
                continue;
            }
            if (buf.Length < f.QoiLength)
            {
                buf = new byte[f.QoiLength];
            }

            s.Position = f.QoiOffset;
            s.ReadExactly(buf, 0, f.QoiLength);
            yield return (f, dec.Decode(f, buf.AsSpan(0, f.QoiLength)));
        }
    }

    public static void WriteHeader(Stream s)
    {
        s.Write(Magic);
        Span<byte> v = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(v, Version);
        s.Write(v);
    }

    public static void WriteRecord(Stream s, in PresentInfo p, byte flags, ReadOnlySpan<byte> qoi)
    {
        var hitch = Encoding.UTF8.GetBytes(p.Hitch ?? "");
        if (hitch.Length > 255)
        {
            Array.Resize(ref hitch, 255);
        }

        int len = 6 * 8 + 3 * 4 + 4 * 4 + 3 * 4 + 1 + 1 + hitch.Length + 4 + qoi.Length;
        var b = new byte[4 + len - qoi.Length];
        int o = 0;
        void I32(int v)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
            o += 4;
        }
        void U32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
            o += 4;
        }
        void I64(long v)
        {
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(o), v);
            o += 8;
        }
        void F32(float v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), v);
            o += 4;
        }
        I32(len);
        I64(p.Present);
        I64(p.Flip);
        I64(p.VBlank);
        I64(p.GuestUs);
        I64(p.FlipNs);
        I64(p.PresentNs);
        F32(p.IntervalMs);
        F32(p.GpuMs);
        F32(p.HitchMs);
        I32(p.Draws);
        I32(p.Scenes);
        I32(p.NewShaders);
        I32(p.NewPipelines);
        U32(p.Address);
        U32(p.SrcW);
        U32(p.SrcH);
        b[o++] = flags;
        b[o++] = (byte)hitch.Length;
        hitch.CopyTo(b, o);
        o += hitch.Length;
        I32(qoi.Length);
        s.Write(b, 0, o);
        s.Write(qoi);
    }

    public sealed record Frame(PresentInfo Info, byte Flags, long QoiOffset, int QoiLength)
    {
        public bool PreRoll => (Flags & FlagPreRoll) != 0;
        public bool HasPicture => QoiLength > 0;
    }

    /// <summary>Reads the index of a frames file (no pictures; <see cref="ReadPicture"/> decodes one).</summary>
    public static List<Frame> ReadIndex(string path)
    {
        var list = new List<Frame>();
        using var s = File.OpenRead(path);
        Span<byte> head = stackalloc byte[12];
        if (s.Read(head) < 12 || !head[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException($"{path}: not a VitaSharp frames file");
        }

        var lenBuf = new byte[4];
        while (s.Position + 4 <= s.Length)
        {
            long at = s.Position;
            s.ReadExactly(lenBuf);
            int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (len <= 0 || at + 4 + len > s.Length)
            {
                break;
            }

            int fixedLen = Math.Min(len, 6 * 8 + 3 * 4 + 4 * 4 + 3 * 4 + 2 + 255 + 4);
            var b = new byte[fixedLen];
            s.ReadExactly(b);
            int o = 0;
            int I32()
            {
                int v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
                o += 4;
                return v;
            }
            uint U32()
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
                o += 4;
                return v;
            }
            long I64()
            {
                long v = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(o));
                o += 8;
                return v;
            }
            float F32()
            {
                float v = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));
                o += 4;
                return v;
            }
            var p = new PresentInfo
            {
                Present = I64(),
                Flip = I64(),
                VBlank = I64(),
                GuestUs = I64(),
                FlipNs = I64(),
                PresentNs = I64(),
                IntervalMs = F32(),
                GpuMs = F32(),
                HitchMs = F32(),
                Draws = I32(),
                Scenes = I32(),
                NewShaders = I32(),
                NewPipelines = I32(),
                Address = U32(),
                SrcW = U32(),
                SrcH = U32(),
            };
            byte flags = b[o++];
            int hl = b[o++];
            p.Hitch = Encoding.UTF8.GetString(b, o, hl);
            o += hl;
            int qoiLen = I32();
            list.Add(new Frame(p, flags, at + 4 + o, qoiLen));
            s.Position = at + 4 + len;
        }
        return list;
    }

    /// <summary>The picture of frame <paramref name="i"/> of <paramref name="index"/> (decoded from the key picture
    /// before it), or null.</summary>
    public static (byte[] Rgba, int W, int H)? ReadPicture(string path, IReadOnlyList<Frame> index, int i)
    {
        if (i < 0 || i >= index.Count || !index[i].HasPicture)
        {
            return null;
        }

        int k = i;
        while (k > 0 && ((index[k].Flags & FlagDelta) != 0 || !index[k].HasPicture))
        {
            k--;
        }

        var dec = new Decoder();
        using var s = File.OpenRead(path);
        (byte[], int, int)? last = null;
        for (int j = k; j <= i; j++)
        {
            var f = index[j];
            if (!f.HasPicture)
            {
                continue;
            }

            var buf = new byte[f.QoiLength];
            s.Position = f.QoiOffset;
            s.ReadExactly(buf);
            last = dec.Decode(f, buf);
        }
        return last;
    }
}
