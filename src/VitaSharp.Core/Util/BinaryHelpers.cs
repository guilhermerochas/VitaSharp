using System.Buffers.Binary;
using System.Text;

namespace VitaSharp.Core.Util;

public static class BinaryHelpers
{
    public static uint U32BE(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
    public static ulong U64BE(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64BigEndian(b[o..]);
    public static ushort U16LE(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    public static uint U32LE(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    public static ulong U64LE(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);

    public static string CString(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        return Encoding.UTF8.GetString(n < 0 ? b : b[..n]);
    }

    public static bool IsZero(ReadOnlySpan<byte> b) => !b.ContainsAnyExcept((byte)0);

    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);
}
