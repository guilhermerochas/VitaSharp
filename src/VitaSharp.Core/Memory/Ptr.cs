using System.Runtime.CompilerServices;

namespace VitaSharp.Core.Memory;

/// <summary>Typed guest pointer (32-bit guest address).</summary>
public readonly struct Ptr<T>(uint address) where T : unmanaged
{
    public readonly uint Address = address;

    public bool IsNull => Address == 0;
    public unsafe ref T Value => ref Unsafe.AsRef<T>(GuestMemory.Ptr(Address));
    public unsafe ref T this[int i] => ref Unsafe.AsRef<T>(GuestMemory.Ptr(Address + (uint)(i * sizeof(T))));
    public unsafe Span<T> AsSpan(int count) => new(GuestMemory.Ptr(Address), count);
    public unsafe Ptr<T> Offset(int elements) => new(Address + (uint)(elements * sizeof(T)));
    public Ptr<U> Cast<U>() where U : unmanaged => new(Address);

    /// <summary>Writes the value if the pointer is non-null (common "optional out parameter" pattern).</summary>
    public void TrySet(T value)
    {
        if (Address != 0) Value = value;
    }

    public static implicit operator uint(Ptr<T> p) => p.Address;
    public override string ToString() => $"0x{Address:X8}";
}
