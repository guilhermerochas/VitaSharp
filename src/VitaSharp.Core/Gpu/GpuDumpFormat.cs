using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Core.Gpu;

/// <summary>
/// GPU dump file (".vsgd"): the exact command stream the GPU worker executed, with the guest memory the backend read,
/// so a frame can be replayed draw by draw without the game (see docs/notes/gpu-dump.md).
/// <code>
/// file   = "VSGPUDMP" u32 version u32 headerBytes header(UTF-8 "key: value" lines) chunk*
/// chunk  = u8 type u32 rawBytes u32 storedBytes u8 codec payload      (codec 0 = stored, 1 = Brotli)
///          type 1 = records, type 2 = end (UTF-8 "key: value" summary)
/// record = u8 kind ...                                                 (records never span chunks)
/// </code>
/// All integers little endian; "var" = unsigned LEB128.
/// </summary>
public static class GpuDumpFormat
{
    public static ReadOnlySpan<byte> Magic => "VSGPUDMP"u8;
    /// <summary>Format version: readers accept files up to this version.</summary>
    public const uint Version = 1;
    public const string Extension = ".vsgd";

    public const byte ChunkRecords = 1, ChunkEnd = 2;
    public const byte CodecStored = 0, CodecBrotli = 1;
}

/// <summary>Record kinds inside a records chunk.</summary>
public enum GpuDumpRecord : byte
{
    /// <summary>var-less u32 address, var length, bytes: guest memory as the backend read it, applied before the next record.</summary>
    Memory = 1,
    /// <summary>var id, u32 guest address, var length, GXP bytes.</summary>
    ProgramInfo = 2,
    /// <summary>var id, var info id, u32 handle, attributes, streams.</summary>
    VertexProgram = 3,
    /// <summary>var id, var info id + 1 (0 = none), u32 handle, blend, output format, MSAA mode, mask update.</summary>
    FragmentProgram = 4,
    /// <summary>var length, bytes: the recording backend's GPU-only state at the start (<see cref="IGpuBackend.ExportDumpState"/>).</summary>
    BackendState = 5,
    BeginScene = 0x10,
    Draw = 0x11,
    EndScene = 0x12,
    Finish = 0x13,
    MidSceneFlush = 0x14,
    /// <summary>display frame, then var host microseconds since the dump started, var guest frame, var renderer present number.</summary>
    Present = 0x15,
    Signal = 0x16,
    /// <summary>u32 write address, u32 write size (a GPU transfer done on the CPU; its effect reaches the dump through later reads).</summary>
    Callback = 0x17,
    /// <summary>The queue ran empty and the backend's Idle ran.</summary>
    Idle = 0x20,
}

/// <summary>Growable little-endian byte buffer (no allocation per record).</summary>
public sealed class DumpBuffer
{
    byte[] _buf;

    public DumpBuffer(int capacity = 1 << 16) => _buf = new byte[Math.Max(capacity, 16)];
    /// <summary>Writes into <paramref name="buffer"/> (grown by copying when it is too small).</summary>
    public DumpBuffer(byte[] buffer) => _buf = buffer;

    public int Length { get; private set; }
    public byte[] Buffer => _buf;
    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, Length);
    public void Clear() => Length = 0;

    Span<byte> Grow(int n)
    {
        if (Length + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
        var s = _buf.AsSpan(Length, n);
        Length += n;
        return s;
    }

    public void U8(byte v) => Grow(1)[0] = v;
    public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), v);
    public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), v);
    public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Grow(4), v);
    public void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Grow(4), v);

    public void Var(ulong v)
    {
        while (v >= 0x80) { U8((byte)(v | 0x80)); v >>= 7; }
        U8((byte)v);
    }

    public void Bytes(ReadOnlySpan<byte> data) => data.CopyTo(Grow(data.Length));
    public void Append(DumpBuffer other) => Bytes(other.Span);
}

/// <summary>Sequential reader over a byte array (a decompressed chunk).</summary>
public sealed class DumpCursor
{
    public byte[] Data = [];
    public int Position, End;

    public void Reset(byte[] data, int length) { Data = data; Position = 0; End = length; }
    public bool AtEnd => Position >= End;

    ReadOnlySpan<byte> Take(int n)
    {
        if (Position + n > End) throw new InvalidDataException("GPU dump record truncated");
        var s = Data.AsSpan(Position, n);
        Position += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public bool Bool() => U8() != 0;
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public ulong Var()
    {
        ulong v = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = U8();
            v |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return v;
            if (shift > 63) throw new InvalidDataException("GPU dump varint too long");
        }
    }

    public int VarInt() => checked((int)Var());
    /// <summary>The next <paramref name="n"/> bytes, as an offset into <see cref="Data"/>.</summary>
    public int Skip(int n) { int p = Position; Take(n); return p; }
    public ReadOnlySpan<byte> BytesSpan(int n) => Take(n);
}

/// <summary>"key: value" header/summary lines (values are single-line; newlines are replaced).</summary>
public sealed class GpuDumpHeader
{
    public readonly List<KeyValuePair<string, string>> Entries = new();

    public string? this[string key]
    {
        get { foreach (var e in Entries) if (e.Key == key) return e.Value; return null; }
        set
        {
            Entries.RemoveAll(e => e.Key == key);
            if (value != null) Entries.Add(new(key, value));
        }
    }

    public void Add(string key, string? value) { if (value != null) this[key] = value; }

    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var (k, v) in Entries) sb.Append(k).Append(": ").Append(v.Replace("\r", "").Replace("\n", " | ")).Append('\n');
            return sb.ToString();
        }
    }

    public static GpuDumpHeader Parse(string text)
    {
        var h = new GpuDumpHeader();
        foreach (var line in text.Split('\n'))
        {
            int c = line.IndexOf(": ", StringComparison.Ordinal);
            if (c > 0) h.Entries.Add(new(line[..c], line[(c + 2)..]));
        }
        return h;
    }

    public long Long(string key, long fallback = 0) => long.TryParse(this[key], out var v) ? v : fallback;
}

/// <summary>
/// Serialises GPU commands into records and back. The writer side keeps program identities (objects already
/// described in this dump); the reader side rebuilds the program objects.
/// </summary>
public sealed class GpuDumpCodec
{
    // ---- writer state
    readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
    int _nextInfo, _nextVp, _nextFp;

    // ---- reader state
    readonly List<GxmProgramInfo> _infos = new();
    readonly List<GxmVertexProgram> _vps = new();
    readonly List<GxmFragmentProgram> _fps = new();

    public int ProgramCount => _ids.Count + _infos.Count;

    // =========================================================== writing

    int InfoId(GxmProgramInfo info, DumpBuffer w)
    {
        if (_ids.TryGetValue(info, out int id)) return id;
        id = _ids[info] = _nextInfo++;
        w.U8((byte)GpuDumpRecord.ProgramInfo);
        w.Var((uint)id);
        w.U32(info.Address);
        w.Var((uint)info.Program.Data.Length);
        w.Bytes(info.Program.Data);
        return id;
    }

    int VertexProgramId(GxmVertexProgram vp, DumpBuffer w)
    {
        if (_ids.TryGetValue(vp, out int id)) return id;
        int info = InfoId(vp.Info, w);
        id = _ids[vp] = _nextVp++;
        w.U8((byte)GpuDumpRecord.VertexProgram);
        w.Var((uint)id);
        w.Var((uint)info);
        w.U32(vp.Handle);
        w.Var((uint)vp.Attributes.Length);
        foreach (var a in vp.Attributes) { w.U16(a.StreamIndex); w.U16(a.Offset); w.U8(a.Format); w.U8(a.ComponentCount); w.U16(a.RegIndex); }
        w.Var((uint)vp.Streams.Length);
        foreach (var s in vp.Streams) { w.U16(s.Stride); w.U16(s.IndexSource); }
        return id;
    }

    int FragmentProgramId(GxmFragmentProgram fp, DumpBuffer w)
    {
        if (_ids.TryGetValue(fp, out int id)) return id;
        int info = fp.Info != null ? InfoId(fp.Info, w) + 1 : 0;
        id = _ids[fp] = _nextFp++;
        w.U8((byte)GpuDumpRecord.FragmentProgram);
        w.Var((uint)id);
        w.Var((uint)info);
        w.U32(fp.Handle);
        w.Bool(fp.Blend.HasValue);
        if (fp.Blend is { } b) { w.U8(b.ColorMask); w.U8(b.ColorFunc); w.U8(b.AlphaFunc); w.U8(b.ColorSrc); w.U8(b.ColorDst); w.U8(b.AlphaSrc); w.U8(b.AlphaDst); }
        w.U32(fp.OutputFormat);
        w.U32(fp.MultisampleMode);
        w.Bool(fp.IsMaskUpdate);
        return id;
    }

    /// <summary>
    /// Writes <paramref name="cmd"/>: program descriptions it needs go to <paramref name="defs"/> (first use in this
    /// dump), the command record itself to <paramref name="w"/>. False for command types the format does not know.
    /// </summary>
    public bool Write(GpuCommand cmd, DumpBuffer defs, DumpBuffer w)
    {
        switch (cmd)
        {
            case DrawCommand d: WriteDraw(d, defs, w); return true;
            case BeginSceneCommand b:
                w.U8((byte)GpuDumpRecord.BeginScene);
                w.U32(b.Width); w.U32(b.Height); w.U32(b.Multisample);
                w.Bool(b.Color != null);
                if (b.Color is { } c)
                {
                    w.U32(c.Data); w.U32(c.Width); w.U32(c.Height); w.U32(c.StrideInPixels); w.U32(c.ColorFormat); w.U32(c.SurfaceType);
                    w.Bool(c.Downscale); w.U32(c.GammaMode);
                }
                w.Bool(b.Depth != null);
                if (b.Depth is { } z)
                {
                    w.U32(z.DepthData); w.U32(z.StencilData); w.U32(z.StrideInSamples); w.U32(z.Format); w.U32(z.Type);
                    w.F32(z.BackgroundDepth); w.U8(z.BackgroundStencil); w.Bool(z.ForceLoad); w.Bool(z.ForceStore);
                }
                return true;
            case EndSceneCommand e:
                w.U8((byte)GpuDumpRecord.EndScene);
                w.Var((uint)e.Notifications.Length);
                foreach (var (a, v) in e.Notifications) { w.U32(a); w.U32(v); }
                return true;
            case FinishCommand: w.U8((byte)GpuDumpRecord.Finish); return true;
            case MidSceneFlushCommand: w.U8((byte)GpuDumpRecord.MidSceneFlush); return true;
            case SignalCommand: w.U8((byte)GpuDumpRecord.Signal); return true;
            case CallbackCommand cb:
                w.U8((byte)GpuDumpRecord.Callback);
                w.U32(cb.WriteAddress); w.U32(cb.WriteSize);
                return true;
            case PresentCommand p:
                // the timing fields follow (see WritePresentInfo)
                w.U8((byte)GpuDumpRecord.Present);
                w.U32(p.Frame.Address); w.U32(p.Frame.Pitch); w.U32(p.Frame.Width); w.U32(p.Frame.Height); w.U32((uint)p.Frame.Format);
                return true;
        }
        return false;
    }

    /// <summary>Completes a present record: host time since the dump started, guest frame, renderer present number.</summary>
    public static void WritePresentInfo(DumpBuffer w, long micros, long guestFrame, long present)
    {
        w.Var((ulong)Math.Max(0, micros));
        w.Var((ulong)Math.Max(0, guestFrame));
        w.Var((ulong)Math.Max(0, present));
    }

    static void WriteSide(DumpBuffer w, in SideState s)
    {
        w.U8(s.DepthFunc); w.Bool(s.DepthWrite);
        w.U8(s.Stencil.Func); w.U8(s.Stencil.StencilFail); w.U8(s.Stencil.DepthFail); w.U8(s.Stencil.DepthPass);
        w.U8(s.Stencil.CompareMask); w.U8(s.Stencil.WriteMask); w.U8(s.Stencil.Ref);
        w.U8(s.PolygonMode); w.Bool(s.FragmentEnabled); w.I32(s.DepthBiasFactor); w.I32(s.DepthBiasUnits);
        w.U8(s.PointLineWidth); w.Bool(s.LineFillLastPixel);
    }

    static SideState ReadSide(DumpCursor r) => new()
    {
        DepthFunc = r.U8(), DepthWrite = r.Bool(),
        Stencil = new StencilState { Func = r.U8(), StencilFail = r.U8(), DepthFail = r.U8(), DepthPass = r.U8(), CompareMask = r.U8(), WriteMask = r.U8(), Ref = r.U8() },
        PolygonMode = r.U8(), FragmentEnabled = r.Bool(), DepthBiasFactor = r.I32(), DepthBiasUnits = r.I32(),
        PointLineWidth = r.U8(), LineFillLastPixel = r.Bool(),
    };

    static void WriteWords(DumpBuffer w, ReadOnlySpan<uint> words)
    {
        uint mask = 0;
        for (int i = 0; i < words.Length; i++) if (words[i] != 0) mask |= 1u << i;
        w.U16((ushort)mask);
        for (int i = 0; i < words.Length; i++) if (words[i] != 0) w.U32(words[i]);
    }

    static void ReadWords(DumpCursor r, Span<uint> words)
    {
        uint mask = r.U16();
        for (int i = 0; i < words.Length; i++) words[i] = (mask & (1u << i)) != 0 ? r.U32() : 0;
    }

    static void WriteTextures(DumpBuffer w, GxmTextureDesc[] t)
    {
        uint mask = 0;
        for (int i = 0; i < t.Length; i++) if (t[i] != default) mask |= 1u << i;
        w.U16((ushort)mask);
        for (int i = 0; i < t.Length; i++)
            if (t[i] != default) { w.U32(t[i].W0); w.U32(t[i].W1); w.U32(t[i].W2); w.U32(t[i].W3); }
    }

    static void ReadTextures(DumpCursor r, GxmTextureDesc[] t)
    {
        uint mask = r.U16();
        for (int i = 0; i < t.Length; i++) t[i] = (mask & (1u << i)) != 0 ? new GxmTextureDesc(r.U32(), r.U32(), r.U32(), r.U32()) : default;
    }

    void WriteDraw(DrawCommand d, DumpBuffer defs, DumpBuffer w)
    {
        int vp = VertexProgramId(d.VertexProgram, defs), fp = FragmentProgramId(d.FragmentProgram, defs);
        w.U8((byte)GpuDumpRecord.Draw);
        w.Var((uint)vp);
        w.Var((uint)fp);
        ref readonly var s = ref d.State;
        w.U8(s.CullMode); w.Bool(s.TwoSided);
        WriteSide(w, s.Front); WriteSide(w, s.Back);
        w.Bool(s.ViewportEnabled);
        w.F32(s.VpOffX); w.F32(s.VpOffY); w.F32(s.VpOffZ); w.F32(s.VpScaleX); w.F32(s.VpScaleY); w.F32(s.VpScaleZ);
        w.U8(s.RegionClipMode); w.I32(s.ClipMinX); w.I32(s.ClipMinY); w.I32(s.ClipMaxX); w.I32(s.ClipMaxY);
        w.Bool(s.WBuffer); w.Bool(s.WClampEnabled); w.F32(s.WClampValue);
        w.Var((uint)d.VertexUniformsLength);
        w.Bytes(d.VertexUniformSpan);
        w.Var((uint)d.FragmentUniformsLength);
        w.Bytes(d.FragmentUniformSpan);
        WriteWords(w, d.VertexUniformAddrs);
        WriteWords(w, d.FragmentUniformAddrs);
        WriteTextures(w, d.VertexTextures);
        WriteTextures(w, d.FragmentTextures);
        WriteWords(w, d.Streams);
        w.U8(d.PrimitiveType);
        w.U8((byte)((d.Index32 ? 1 : 0) | (d.VisIncrement ? 2 : 0)));
        w.U32(d.IndexAddress);
        w.Var(d.IndexCount);
        w.Var(d.InstanceCount);
        w.U32(d.VisAddress);
    }

    // =========================================================== reading

    /// <summary>Reads a program description record (<paramref name="kind"/> already consumed).</summary>
    public void ReadDefinition(GpuDumpRecord kind, DumpCursor r)
    {
        switch (kind)
        {
            case GpuDumpRecord.ProgramInfo:
            {
                int id = r.VarInt();
                uint address = r.U32();
                var data = r.BytesSpan(r.VarInt()).ToArray();
                var p = new GxpProgram(data, address);
                var (slots, bytes) = ShaderGenerator.UniformLayout(p);
                Put(_infos, id, new GxmProgramInfo { Address = address, Program = p, UniformSlots = slots, UniformBytes = bytes, TexturesUsed = p.TexturesUsed(), CubeSamplers = p.CubeSamplers() });
                break;
            }
            case GpuDumpRecord.VertexProgram:
            {
                int id = r.VarInt(), info = r.VarInt();
                uint handle = r.U32();
                var attrs = new GxmVertexAttribute[r.VarInt()];
                for (int i = 0; i < attrs.Length; i++) attrs[i] = new GxmVertexAttribute(r.U16(), r.U16(), r.U8(), r.U8(), r.U16());
                var streams = new GxmVertexStream[r.VarInt()];
                for (int i = 0; i < streams.Length; i++) streams[i] = new GxmVertexStream(r.U16(), r.U16());
                Put(_vps, id, new GxmVertexProgram { Handle = handle, Info = _infos[info], Attributes = attrs, Streams = streams });
                break;
            }
            case GpuDumpRecord.FragmentProgram:
            {
                int id = r.VarInt(), info = r.VarInt();
                uint handle = r.U32();
                GxmBlendInfo? blend = r.Bool() ? new GxmBlendInfo(r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8()) : null;
                Put(_fps, id, new GxmFragmentProgram
                {
                    Handle = handle, Info = info > 0 ? _infos[info - 1] : null!, Blend = blend,
                    OutputFormat = r.U32(), MultisampleMode = r.U32(), IsMaskUpdate = r.Bool(),
                });
                break;
            }
            default: throw new InvalidDataException($"not a program record: {kind}");
        }
    }

    static void Put<T>(List<T> list, int id, T value)
    {
        if (id != list.Count) throw new InvalidDataException($"GPU dump program id {id} out of order (expected {list.Count})");
        list.Add(value);
    }

    /// <summary>
    /// Reads a command record (<paramref name="kind"/> already consumed). Draws reuse <paramref name="draw"/> and point
    /// their uniforms into the cursor's buffer, so the command is valid until the cursor moves to another chunk.
    /// </summary>
    public GpuCommand ReadCommand(GpuDumpRecord kind, DumpCursor r, DrawCommand draw)
    {
        switch (kind)
        {
            case GpuDumpRecord.Draw: return ReadDraw(r, draw);
            case GpuDumpRecord.BeginScene:
            {
                var b = new BeginSceneCommand { Width = r.U32(), Height = r.U32(), Multisample = r.U32() };
                if (r.Bool()) b.Color = new ColorSurfaceInfo(r.U32(), r.U32(), r.U32(), r.U32(), r.U32(), r.U32(), r.Bool(), r.U32());
                if (r.Bool()) b.Depth = new DepthSurfaceInfo(r.U32(), r.U32(), r.U32(), r.U32(), r.U32(), r.F32(), r.U8(), r.Bool(), r.Bool());
                return b;
            }
            case GpuDumpRecord.EndScene:
            {
                var n = new (uint, uint)[r.VarInt()];
                for (int i = 0; i < n.Length; i++) n[i] = (r.U32(), r.U32());
                return new EndSceneCommand { Notifications = n };
            }
            case GpuDumpRecord.Finish: return new FinishCommand();
            case GpuDumpRecord.MidSceneFlush: return new MidSceneFlushCommand();
            case GpuDumpRecord.Signal: return new SignalCommand { Signal = static () => { } };
            case GpuDumpRecord.Callback: return new CallbackCommand { WriteAddress = r.U32(), WriteSize = r.U32(), Action = static () => { } };
            case GpuDumpRecord.Present:
                return new PresentCommand { Frame = new DisplayFrame(r.U32(), r.U32(), r.U32(), r.U32(), (DisplayPixelFormat)r.U32()) };
        }
        throw new InvalidDataException($"unknown GPU dump record kind 0x{(byte)kind:X2}");
    }

    public static (long Micros, long GuestFrame, long Present) ReadPresentInfo(DumpCursor r) => ((long)r.Var(), (long)r.Var(), (long)r.Var());

    DrawCommand ReadDraw(DumpCursor r, DrawCommand d)
    {
        d.Reset();
        d.VertexProgram = _vps[r.VarInt()];
        d.FragmentProgram = _fps[r.VarInt()];
        var s = new DrawState { CullMode = r.U8(), TwoSided = r.Bool() };
        s.Front = ReadSide(r);
        s.Back = ReadSide(r);
        s.ViewportEnabled = r.Bool();
        s.VpOffX = r.F32(); s.VpOffY = r.F32(); s.VpOffZ = r.F32(); s.VpScaleX = r.F32(); s.VpScaleY = r.F32(); s.VpScaleZ = r.F32();
        s.RegionClipMode = r.U8(); s.ClipMinX = r.I32(); s.ClipMinY = r.I32(); s.ClipMaxX = r.I32(); s.ClipMaxY = r.I32();
        s.WBuffer = r.Bool(); s.WClampEnabled = r.Bool(); s.WClampValue = r.F32();
        d.State = s;
        d.VertexUniformsLength = r.VarInt();
        d.VertexUniforms = r.Data;
        d.VertexUniformsOffset = r.Skip(d.VertexUniformsLength);
        d.FragmentUniformsLength = r.VarInt();
        d.FragmentUniforms = r.Data;
        d.FragmentUniformsOffset = r.Skip(d.FragmentUniformsLength);
        ReadWords(r, d.VertexUniformAddrs);
        ReadWords(r, d.FragmentUniformAddrs);
        ReadTextures(r, d.VertexTextures);
        ReadTextures(r, d.FragmentTextures);
        ReadWords(r, d.Streams);
        d.PrimitiveType = r.U8();
        byte flags = r.U8();
        d.Index32 = (flags & 1) != 0;
        d.VisIncrement = (flags & 2) != 0;
        d.IndexAddress = r.U32();
        d.IndexCount = (uint)r.Var();
        d.InstanceCount = (uint)r.Var();
        d.VisAddress = r.U32();
        return d;
    }

    // =========================================================== chunks

    /// <summary>Compresses <paramref name="raw"/> into a chunk (Brotli at <paramref name="quality"/>, stored when that does not help).</summary>
    public static void WriteChunk(Stream output, byte type, ReadOnlySpan<byte> raw, int quality, ref byte[]? scratch)
    {
        int max = BrotliEncoder.GetMaxCompressedLength(raw.Length);
        if (scratch == null || scratch.Length < max) scratch = new byte[Math.Max(max, 1 << 16)];
        byte codec = GpuDumpFormat.CodecStored;
        ReadOnlySpan<byte> payload = raw;
        if (quality > 0 && raw.Length > 64 && BrotliEncoder.TryCompress(raw, scratch, out int written, quality, 22) && written < raw.Length)
        {
            codec = GpuDumpFormat.CodecBrotli;
            payload = scratch.AsSpan(0, written);
        }
        Span<byte> head = stackalloc byte[10];
        head[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(head[1..], (uint)raw.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(head[5..], (uint)payload.Length);
        head[9] = codec;
        output.Write(head);
        output.Write(payload);
    }

    /// <summary>Reads the next chunk into <paramref name="buffer"/>; false at the end of the file (or a truncated tail).</summary>
    public static bool ReadChunk(Stream input, ref byte[] buffer, ref byte[]? scratch, out byte type, out int length)
    {
        type = 0; length = 0;
        Span<byte> head = stackalloc byte[10];
        if (input.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
        type = head[0];
        length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(head[1..]));
        int stored = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(head[5..]));
        byte codec = head[9];
        if (buffer.Length < length) buffer = new byte[Math.Max(length, buffer.Length * 2)];
        if (codec == GpuDumpFormat.CodecStored)
        {
            if (stored != length) throw new InvalidDataException("GPU dump: stored chunk size mismatch");
            return input.ReadAtLeast(buffer.AsSpan(0, length), length, throwOnEndOfStream: false) == length;
        }
        if (codec != GpuDumpFormat.CodecBrotli) throw new InvalidDataException($"GPU dump: unknown chunk codec {codec}");
        if (scratch == null || scratch.Length < stored) scratch = new byte[Math.Max(stored, 1 << 16)];
        if (input.ReadAtLeast(scratch.AsSpan(0, stored), stored, throwOnEndOfStream: false) < stored) return false;
        if (!BrotliDecoder.TryDecompress(scratch.AsSpan(0, stored), buffer.AsSpan(0, length), out int got) || got != length)
            throw new InvalidDataException("GPU dump: corrupt compressed chunk");
        return true;
    }

    public static void WriteFileHeader(Stream output, GpuDumpHeader header)
    {
        var text = Encoding.UTF8.GetBytes(header.Text);
        output.Write(GpuDumpFormat.Magic);
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, GpuDumpFormat.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)text.Length);
        output.Write(b);
        output.Write(text);
    }

    public static GpuDumpHeader ReadFileHeader(Stream input, out uint version)
    {
        Span<byte> b = stackalloc byte[16];
        if (input.ReadAtLeast(b, 16, throwOnEndOfStream: false) < 16 || !b[..8].SequenceEqual(GpuDumpFormat.Magic))
            throw new InvalidDataException("not a VitaSharp GPU dump (bad magic)");
        version = BinaryPrimitives.ReadUInt32LittleEndian(b[8..]);
        if (version == 0 || version > GpuDumpFormat.Version)
            throw new InvalidDataException($"GPU dump version {version} is newer than this build supports ({GpuDumpFormat.Version})");
        int len = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(b[12..]));
        var text = new byte[len];
        input.ReadExactly(text);
        return GpuDumpHeader.Parse(Encoding.UTF8.GetString(text));
    }
}
