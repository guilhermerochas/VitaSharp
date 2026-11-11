using System.Buffers.Binary;
using System.Text;

namespace VitaSharp.Core.SaveState;

/// <summary>
/// State that goes into a save state. Each module writes and reads its own fields explicitly (no reflection over the
/// object graph); simple classes get the implementation from <see cref="GenerateSnapshotAttribute"/>. Load runs with
/// every guest thread stopped and the kernel objects already restored, so UIDs can be resolved.
/// </summary>
public interface ISnapshotState
{
    void SaveState(SnapshotWriter w);
    void LoadState(SnapshotReader r);

    /// <summary>Why the current state cannot be saved (a host resource the module cannot recreate yet), or null. The
    /// message names the resource: "host resource Y not supported yet".</summary>
    string? CheckSnapshot() => null;
}

/// <summary>
/// Generates <see cref="ISnapshotState"/> for a partial class from its instance fields (VitaSharp.Core.Generators
/// SnapshotGenerator): primitives, enums, strings, arrays, List/Dictionary/HashSet/Queue of those, kernel objects (by
/// UID) and other [GenerateSnapshot] or <see cref="ISnapshotState"/> types. Fields of other types are a compile error
/// unless marked <see cref="SnapshotIgnoreAttribute"/>. Readonly scalar fields are configuration and are skipped.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class GenerateSnapshotAttribute : Attribute;

/// <summary>Leaves a field out of the generated snapshot (caches rebuilt on demand, host handles restored by hand).</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SnapshotIgnoreAttribute : Attribute;

/// <summary>Leaves fields out of the generated snapshot by name, declared next to <see cref="GenerateSnapshotAttribute"/>
/// in the type's *.Snapshot.cs part (host handles, caches; the hand-written SaveExtra/LoadExtra cover what matters).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class SnapshotIgnoreFieldsAttribute(params string[] names) : Attribute
{
    public string[] Names { get; } = names;
}

/// <summary>A save or load that cannot be done; the message is meant for the user.</summary>
public class SnapshotException(string message) : Exception(message);

/// <summary>A save state of another VitaSharp build, format or game (<see cref="SaveStateFile.Incompatibility"/>): its
/// message is a complete sentence for the user.</summary>
public sealed class SaveStateIncompatibleException(string message) : SnapshotException(message);

/// <summary>Thrown while a state is written when some transient work is in flight (a frame in a queue): the save
/// resumes the emulation for a moment and tries again.</summary>
public sealed class SnapshotRetryException(string message) : SnapshotException(message);

/// <summary>Little-endian binary writer of snapshot data (a growable buffer; sections are length-prefixed).</summary>
public sealed class SnapshotWriter
{
    byte[] _buf = new byte[4096];
    int _len;

    public int Length => _len;
    public ReadOnlySpan<byte> Data => _buf.AsSpan(0, _len);

    Span<byte> Grow(int n)
    {
        if (_len + n > _buf.Length)
        {
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
        }

        var s = _buf.AsSpan(_len, n);
        _len += n;
        return s;
    }

    public void Write(bool v) => Grow(1)[0] = v ? (byte)1 : (byte)0;
    public void Write(byte v) => Grow(1)[0] = v;
    public void Write(sbyte v) => Grow(1)[0] = (byte)v;
    public void Write(short v) => BinaryPrimitives.WriteInt16LittleEndian(Grow(2), v);
    public void Write(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), v);
    public void Write(char v) => BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), v);
    public void Write(int v) => BinaryPrimitives.WriteInt32LittleEndian(Grow(4), v);
    public void Write(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), v);
    public void Write(long v) => BinaryPrimitives.WriteInt64LittleEndian(Grow(8), v);
    public void Write(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), v);
    public void Write(float v) => BinaryPrimitives.WriteSingleLittleEndian(Grow(4), v);
    public void Write(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Grow(8), v);
    public void Write(nint v) => Write((long)v);

    public void Write(string? s)
    {
        if (s == null)
        {
            Write(-1);
            return;
        }
        int n = Encoding.UTF8.GetByteCount(s);
        Write(n);
        Encoding.UTF8.GetBytes(s, Grow(n));
    }

    public void Write(byte[]? b)
    {
        if (b == null)
        {
            Write(-1);
            return;
        }
        Write(b.Length);
        b.CopyTo(Grow(b.Length));
    }

    /// <summary>Raw bytes, no length prefix.</summary>
    public void WriteRaw(ReadOnlySpan<byte> b) => b.CopyTo(Grow(b.Length));

    /// <summary>An array of unmanaged values with a length prefix (-1 = null).</summary>
    public void WriteArray<T>(T[]? a) where T : unmanaged
    {
        if (a == null)
        {
            Write(-1);
            return;
        }
        Write(a.Length);
        WriteRaw(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
    }

    public void WriteStruct<T>(in T v) where T : unmanaged
        => WriteRaw(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in v)));

    /// <summary>A nested state, length-prefixed so a reader can skip or bound it.</summary>
    public void Write(ISnapshotState? s)
    {
        if (s == null)
        {
            Write(false);
            return;
        }
        Write(true);
        int at = _len;
        Write(0);
        s.SaveState(this);
        BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(at), _len - at - 4);
    }

    /// <summary>Starts a length-prefixed block; <see cref="EndBlock"/> patches the length.</summary>
    public int BeginBlock()
    {
        int at = _len;
        Write(0);
        return at;
    }

    public void EndBlock(int at) => BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(at), _len - at - 4);

    public void WriteKernelObject(Kernel.KernelObject? o) => Write(o?.Uid ?? 0);
}

/// <summary>Reader matching <see cref="SnapshotWriter"/>. Reading past the end throws <see cref="SnapshotException"/>.</summary>
public sealed class SnapshotReader(byte[] data, int offset = 0, int length = -1)
{
    readonly byte[] _buf = data;
    int _pos = offset;
    readonly int _end = length < 0 ? data.Length : offset + length;

    /// <summary>Resolves kernel object UIDs while loading (the restored object table).</summary>
    public Kernel.ObjectTable? Objects { get; init; }

    public bool AtEnd => _pos >= _end;
    public int Remaining => _end - _pos;

    ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _end)
        {
            throw new SnapshotException("the save state is truncated or corrupt");
        }

        var s = _buf.AsSpan(_pos, n);
        _pos += n;
        return s;
    }

    public bool ReadBool() => Take(1)[0] != 0;
    public byte ReadByte() => Take(1)[0];
    public sbyte ReadSByte() => (sbyte)Take(1)[0];
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public char ReadChar() => (char)BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public nint ReadIntPtr() => (nint)ReadInt64();

    public string? ReadString()
    {
        int n = ReadInt32();
        return n < 0 ? null : Encoding.UTF8.GetString(Take(n));
    }

    public string ReadStringNotNull() => ReadString() ?? "";

    public byte[]? ReadBytes()
    {
        int n = ReadInt32();
        return n < 0 ? null : Take(n).ToArray();
    }

    public ReadOnlySpan<byte> ReadRaw(int n) => Take(n);

    public T[]? ReadArray<T>() where T : unmanaged
    {
        int n = ReadInt32();
        if (n < 0)
        {
            return null;
        }

        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(Take(checked(n * size))).ToArray();
    }

    public T ReadStruct<T>() where T : unmanaged
        => System.Runtime.InteropServices.MemoryMarshal.Read<T>(Take(System.Runtime.CompilerServices.Unsafe.SizeOf<T>()));

    /// <summary>Reads a nested state written by <see cref="SnapshotWriter.Write(ISnapshotState?)"/> into
    /// <paramref name="target"/>; false when it was null.</summary>
    public bool ReadInto(ISnapshotState target)
    {
        if (!ReadBool())
        {
            return false;
        }

        var sub = ReadBlock();
        target.LoadState(sub);
        return true;
    }

    /// <summary>A nested state: null when it was written as null, else created with <paramref name="create"/>.</summary>
    public T? ReadState<T>(Func<T> create) where T : class, ISnapshotState
    {
        if (!ReadBool())
        {
            return null;
        }

        var sub = ReadBlock();
        var o = create();
        o.LoadState(sub);
        return o;
    }

    /// <summary>A length-prefixed block as its own reader.</summary>
    public SnapshotReader ReadBlock()
    {
        int n = ReadInt32();
        if (n < 0 || _pos + n > _end)
        {
            throw new SnapshotException("the save state is truncated or corrupt");
        }

        var r = new SnapshotReader(_buf, _pos, n) { Objects = Objects };
        _pos += n;
        return r;
    }

    public T? ReadKernelObject<T>() where T : Kernel.KernelObject
    {
        int uid = ReadInt32();
        return uid == 0 ? null : Objects?.Get<T>(uid);
    }
}
