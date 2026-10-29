using System.Text;

namespace VitaSharp.Core.Hle.Np.Rpcn;

/// <summary>
/// The protobuf (proto3) wire format, just enough for RPCN's np2_structs messages: varints, 64-bit two's complement
/// int32/int64 (no zigzag: the schema uses int32/int64/uint32/uint64/bool), length-delimited strings, bytes and nested
/// messages, packed repeated scalars. Default values are not written, as protoc does.
/// </summary>
public sealed class ProtoWriter
{
    readonly MemoryStream _ms = new();

    public byte[] ToArray() => _ms.ToArray();
    public int Length => (int)_ms.Length;

    void Varint(ulong v)
    {
        while (v >= 0x80) { _ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
        _ms.WriteByte((byte)v);
    }

    void Tag(int field, int wire) => Varint((ulong)(field << 3 | wire));

    public ProtoWriter UInt64(int field, ulong v) { if (v != 0) { Tag(field, 0); Varint(v); } return this; }
    public ProtoWriter UInt32(int field, uint v) => UInt64(field, v);
    /// <summary>int32 and int64: negative values are 10-byte varints of the sign-extended value.</summary>
    public ProtoWriter Int64(int field, long v) => UInt64(field, unchecked((ulong)v));
    public ProtoWriter Int32(int field, int v) => Int64(field, v);
    public ProtoWriter Bool(int field, bool v) => UInt64(field, v ? 1u : 0u);

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> b)
    {
        if (b.Length == 0) return this;
        Tag(field, 2);
        Varint((ulong)b.Length);
        _ms.Write(b);
        return this;
    }

    public ProtoWriter String(int field, string? s) => s == null ? this : Bytes(field, Encoding.UTF8.GetBytes(s));

    /// <summary>A nested message (written even when empty, like a set sub-message).</summary>
    public ProtoWriter Message(int field, ProtoWriter m)
    {
        var b = m.ToArray();
        Tag(field, 2);
        Varint((ulong)b.Length);
        _ms.Write(b);
        return this;
    }

    /// <summary>A packed repeated varint field (int32/int64/uint64 values as two's complement).</summary>
    public ProtoWriter Packed(int field, IEnumerable<long> values)
    {
        var inner = new ProtoWriter();
        int n = 0;
        foreach (var v in values) { inner.Varint(unchecked((ulong)v)); n++; }
        if (n == 0) return this;
        return Bytes(field, inner.ToArray());
    }
}

/// <summary>Reads a protobuf message into its fields; unknown fields are kept and ignored by the accessors.</summary>
public sealed class ProtoReader
{
    readonly List<(int Field, int Wire, ulong Value, ReadOnlyMemory<byte> Data)> _fields = new();

    public ProtoReader(ReadOnlyMemory<byte> data)
    {
        var s = data.Span;
        int i = 0;
        while (i < s.Length)
        {
            ulong tag = ReadVarint(s, ref i);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: _fields.Add((field, 0, ReadVarint(s, ref i), default)); break;
                case 1: Need(s, i, 8); _fields.Add((field, 1, BitConverter.ToUInt64(s.Slice(i, 8)), default)); i += 8; break;
                case 5: Need(s, i, 4); _fields.Add((field, 5, BitConverter.ToUInt32(s.Slice(i, 4)), default)); i += 4; break;
                case 2:
                    int len = checked((int)ReadVarint(s, ref i));
                    Need(s, i, len);
                    _fields.Add((field, 2, 0, data.Slice(i, len)));
                    i += len;
                    break;
                default: throw new InvalidDataException($"protobuf: wire type {wire} not supported");
            }
        }
    }

    static void Need(ReadOnlySpan<byte> s, int i, int n)
    {
        if (n < 0 || i + n > s.Length) throw new InvalidDataException("protobuf: truncated message");
    }

    static ulong ReadVarint(ReadOnlySpan<byte> s, ref int i)
    {
        ulong v = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (i >= s.Length) throw new InvalidDataException("protobuf: truncated varint");
            byte b = s[i++];
            v |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return v;
        }
        throw new InvalidDataException("protobuf: varint too long");
    }

    public ulong UInt64(int field) { ulong v = 0; foreach (var f in _fields) if (f.Field == field && f.Wire == 0) v = f.Value; return v; }
    public uint UInt32(int field) => (uint)UInt64(field);
    public long Int64(int field) => unchecked((long)UInt64(field));
    public int Int32(int field) => unchecked((int)UInt64(field));
    public bool Bool(int field) => UInt64(field) != 0;

    public ReadOnlyMemory<byte> Bytes(int field)
    {
        ReadOnlyMemory<byte> v = default;
        foreach (var f in _fields) if (f.Field == field && f.Wire == 2) v = f.Data;
        return v;
    }

    public string String(int field) => Encoding.UTF8.GetString(Bytes(field).Span);
    public bool Has(int field) => _fields.Any(f => f.Field == field);
    public ProtoReader Message(int field) => new(Bytes(field));

    public List<ProtoReader> Messages(int field) => _fields.Where(f => f.Field == field && f.Wire == 2).Select(f => new ProtoReader(f.Data)).ToList();
    public List<string> Strings(int field) => _fields.Where(f => f.Field == field && f.Wire == 2).Select(f => Encoding.UTF8.GetString(f.Data.Span)).ToList();
    public List<ReadOnlyMemory<byte>> BytesList(int field) => _fields.Where(f => f.Field == field && f.Wire == 2).Select(f => f.Data).ToList();

    /// <summary>A repeated varint field, packed or not.</summary>
    public List<long> Repeated(int field)
    {
        var list = new List<long>();
        foreach (var f in _fields)
        {
            if (f.Field != field) continue;
            if (f.Wire == 0) list.Add(unchecked((long)f.Value));
            else if (f.Wire == 2)
            {
                var s = f.Data.Span;
                int i = 0;
                while (i < s.Length) list.Add(unchecked((long)ReadVarint(s, ref i)));
            }
        }
        return list;
    }
}
