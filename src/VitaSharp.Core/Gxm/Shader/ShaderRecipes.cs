using System.Buffers.Binary;
using System.IO.Hashing;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Gxm.Shader;

/// <summary>
/// Everything needed to translate one shader again: the GXP program (by hash, its bytes live in the same store) and
/// the hints it was translated with. Independent of <see cref="ShaderGenerator.GeneratorVersion"/>: after a generator
/// bump the renderer retranslates every recipe in the background instead of during gameplay.
/// </summary>
public sealed record ShaderRecipe(ulong ProgramHash, ulong HintKey, bool Vertex, ShaderHints Hints)
{
    const byte Tag = 2;

    public byte[] Encode()
    {
        var h = Hints;
        var buf = new byte[1 + 1 + 16 + 1 + h.TextureFormats.Length * 4 + 2 + h.Attributes.Length * 9 + 4 + 1];
        var s = buf.AsSpan();
        s[0] = Tag;
        s[1] = Vertex ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(s[2..], ProgramHash);
        BinaryPrimitives.WriteUInt64LittleEndian(s[10..], HintKey);
        int o = 18;
        s[o++] = checked((byte)h.TextureFormats.Length);
        foreach (var t in h.TextureFormats) { BinaryPrimitives.WriteUInt32LittleEndian(s[o..], t); o += 4; }
        BinaryPrimitives.WriteUInt16LittleEndian(s[o..], checked((ushort)h.Attributes.Length)); o += 2;
        foreach (var a in h.Attributes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[o..], a.RegIndex);
            BinaryPrimitives.WriteInt32LittleEndian(s[(o + 4)..], a.ComponentCount);
            s[o + 8] = a.Format;
            o += 9;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(s[o..], h.ColorFormat); o += 4;
        s[o] = h.FetchInputAttachment ? (byte)1 : (byte)0;
        return buf;
    }

    /// <summary>Null when the record is malformed or its hints no longer produce the recorded key.</summary>
    public static ShaderRecipe? Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length < 19 || s[0] != Tag || s[1] > 1) return null;
        ulong program = BinaryPrimitives.ReadUInt64LittleEndian(s[2..]), hintKey = BinaryPrimitives.ReadUInt64LittleEndian(s[10..]);
        int o = 18;
        int nt = s[o++];
        if (s.Length < o + nt * 4 + 2) return null;
        var tex = new uint[nt];
        for (int i = 0; i < nt; i++) { tex[i] = BinaryPrimitives.ReadUInt32LittleEndian(s[o..]); o += 4; }
        int na = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]); o += 2;
        // exact size: attributes, colour format, fetch flag
        if (s.Length != o + na * 9 + 5) return null;
        var attrs = new (uint, int, byte)[na];
        for (int i = 0; i < na; i++)
        {
            attrs[i] = (BinaryPrimitives.ReadUInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..]), s[o + 8]);
            o += 9;
        }
        uint color = BinaryPrimitives.ReadUInt32LittleEndian(s[o..]); o += 4;
        if (s[o] > 1) return null;
        var hints = new ShaderHints(tex, attrs, color, s[o] != 0);
        // the key is the identity used by every cache: a record whose hints disagree with it is useless
        return hints.Key() == hintKey ? new ShaderRecipe(program, hintKey, s[1] != 0, hints) : null;
    }
}

/// <summary>
/// A pipeline the title used, described by version-independent inputs only: the shader identities (program hash +
/// hint key), the resolved fixed-function state and the guest vertex program's attributes/streams. The Vulkan vertex
/// input layout is derived from the latter and the (possibly retranslated) vertex shader's inputs, so the record
/// stays valid across <see cref="ShaderGenerator.GeneratorVersion"/> bumps. <see cref="FsUsesFragColor"/> and
/// <see cref="FsRawColorBits"/> are what the fixed-function state was resolved against: a retranslated fragment shader
/// that disagrees makes the record stale (it is skipped, the draw builds its pipeline normally).
/// </summary>
public sealed record PipelineRecipe(ulong VsHash, ulong VsHint, ulong FsHash, ulong FsHint, int AttachFormat, bool HasColor, bool Blend,
    byte ColorFunc, byte AlphaFunc, byte ColorSrc, byte ColorDst, byte AlphaSrc, byte AlphaDst, byte Mask,
    byte Topology, byte Polygon, byte Samples, bool FsUsesFragColor, byte FsRawColorBits,
    GxmVertexAttribute[] Attributes, GxmVertexStream[] Streams)
{
    public const int HeadSize = 32 + 4 + 2 + 7 + 3 + 2 + 2 + 1;

    public byte[] Encode()
    {
        var buf = new byte[HeadSize + Attributes.Length * 8 + Streams.Length * 4];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(s, VsHash);
        BinaryPrimitives.WriteUInt64LittleEndian(s[8..], VsHint);
        BinaryPrimitives.WriteUInt64LittleEndian(s[16..], FsHash);
        BinaryPrimitives.WriteUInt64LittleEndian(s[24..], FsHint);
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], AttachFormat);
        s[36] = HasColor ? (byte)1 : (byte)0;
        s[37] = Blend ? (byte)1 : (byte)0;
        s[38] = ColorFunc; s[39] = AlphaFunc; s[40] = ColorSrc; s[41] = ColorDst; s[42] = AlphaSrc; s[43] = AlphaDst; s[44] = Mask;
        s[45] = Topology; s[46] = Polygon; s[47] = Samples;
        s[48] = (byte)((FsUsesFragColor ? 1 : 0) | (FsRawColorBits switch { 8 => 1, 16 => 2, 32 => 3, _ => 0 }) << 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[49..], checked((ushort)Attributes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(s[51..], checked((ushort)Streams.Length));
        int o = HeadSize;
        foreach (var a in Attributes)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[o..], a.StreamIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 2)..], a.Offset);
            s[o + 4] = a.Format;
            s[o + 5] = a.ComponentCount;
            BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 6)..], a.RegIndex);
            o += 8;
        }
        foreach (var st in Streams)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[o..], st.Stride);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 2)..], st.IndexSource);
            o += 4;
        }
        return buf;
    }

    public static PipelineRecipe? Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length < HeadSize || s[36] > 1 || s[37] > 1 || s[48] > 7) return null;
        int na = BinaryPrimitives.ReadUInt16LittleEndian(s[49..]), ns = BinaryPrimitives.ReadUInt16LittleEndian(s[51..]);
        if (ns > 16 || s.Length != HeadSize + na * 8 + ns * 4) return null;
        var attrs = new GxmVertexAttribute[na];
        int o = HeadSize;
        for (int i = 0; i < na; i++, o += 8)
            attrs[i] = new GxmVertexAttribute(BinaryPrimitives.ReadUInt16LittleEndian(s[o..]), BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 2)..]),
                s[o + 4], s[o + 5], BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 6)..]));
        var streams = new GxmVertexStream[ns];
        for (int i = 0; i < ns; i++, o += 4)
            streams[i] = new GxmVertexStream(BinaryPrimitives.ReadUInt16LittleEndian(s[o..]), BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 2)..]));
        return new PipelineRecipe(BinaryPrimitives.ReadUInt64LittleEndian(s), BinaryPrimitives.ReadUInt64LittleEndian(s[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(s[16..]), BinaryPrimitives.ReadUInt64LittleEndian(s[24..]), BinaryPrimitives.ReadInt32LittleEndian(s[32..]),
            s[36] != 0, s[37] != 0, s[38], s[39], s[40], s[41], s[42], s[43], s[44], s[45], s[46], s[47],
            (s[48] & 1) != 0, (byte)(((s[48] >> 1) & 3) switch { 1 => 8, 2 => 16, 3 => 32, _ => 0 }), attrs, streams);
    }

    /// <summary>Record key: the same pipeline always maps to the same key, whatever the generator version.</summary>
    public static ulong KeyOf(ReadOnlySpan<byte> encoded) => XxHash64.HashToUInt64(encoded);
}

/// <summary>
/// programs.pack: the version-independent shader record of a title (see <see cref="ShaderRecipe"/>), a
/// <see cref="BlobPack"/> holding two kinds of blobs: GXP program bytes (tag 1, once per program, verified against
/// the program hash on read) and recipes (tag 2, one per (program, hint key), in first-use order).
/// </summary>
public sealed class ShaderRecipeStore : IDisposable
{
    /// <summary>Bump when the blob layout changes (the generator version is deliberately not part of it).</summary>
    public const uint FormatVersion = 1;
    const uint FileVersion = 0x52430000 | FormatVersion; // "RC" + format
    const byte ProgramTag = 1;

    readonly BlobPack _pack;

    ShaderRecipeStore(BlobPack pack) { _pack = pack; }

    public string Path => _pack.Path;
    public bool IsReadOnly => _pack.IsReadOnly;

    public static ShaderRecipeStore? Open(string path) => BlobPack.Open(path, FileVersion) is { } p ? new ShaderRecipeStore(p) : null;

    static ulong ProgramKey(ulong programHash) => Mix(programHash, 0x50524F47); // "PROG"
    static ulong RecipeKey(ulong programHash, ulong hintKey) => Mix(Mix(programHash, 0x52435045), hintKey); // "RCPE"

    static ulong Mix(ulong a, ulong b)
    {
        Span<ulong> k = stackalloc ulong[2] { a, b };
        return XxHash64.HashToUInt64(System.Runtime.InteropServices.MemoryMarshal.AsBytes(k));
    }

    public bool Has(ulong programHash, ulong hintKey) => _pack.Contains(RecipeKey(programHash, hintKey));

    /// <summary>Records a shader (program bytes first, so a recipe never refers to a program that was not written).</summary>
    public void Add(GxpProgram program, ulong hintKey, ShaderHints hints)
    {
        ulong pk = ProgramKey(program.Hash);
        if (!_pack.Contains(pk))
        {
            var b = new byte[1 + program.Data.Length];
            b[0] = ProgramTag;
            program.Data.CopyTo(b, 1);
            _pack.Add(pk, b);
        }
        _pack.Add(RecipeKey(program.Hash, hintKey), new ShaderRecipe(program.Hash, hintKey, program.IsVertex, hints).Encode());
    }

    /// <summary>Every readable recipe in first-use order (corrupted or malformed records are skipped).</summary>
    public List<ShaderRecipe> Recipes(out int corrupted)
    {
        var list = new List<ShaderRecipe>();
        corrupted = 0;
        foreach (ulong key in _pack.Keys())
        {
            var data = _pack.TryRead(key);
            if (data == null) { corrupted++; continue; }
            if (data.Length > 0 && data[0] == ProgramTag) continue;
            var r = ShaderRecipe.Decode(data);
            if (r == null || RecipeKey(r.ProgramHash, r.HintKey) != key) { corrupted++; continue; }
            list.Add(r);
        }
        return list;
    }

    /// <summary>The program's bytes, or null when missing or corrupted (verified against the hash).</summary>
    public byte[]? ProgramBytes(ulong programHash)
    {
        var data = _pack.TryRead(ProgramKey(programHash));
        if (data == null || data.Length < 2 || data[0] != ProgramTag) return null;
        var bytes = data.AsSpan(1);
        return XxHash64.HashToUInt64(bytes) == programHash ? bytes.ToArray() : null;
    }

    public void Dispose() => _pack.Dispose();
}
