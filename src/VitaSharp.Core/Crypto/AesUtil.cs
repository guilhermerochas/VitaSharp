using System.Security.Cryptography;

namespace VitaSharp.Core.Crypto;

/// <summary>Thin wrappers over System.Security.Cryptography for the AES modes the Vita formats need.</summary>
public static class AesUtil
{
    static Aes Create(ReadOnlySpan<byte> key)
    {
        var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes;
    }

    public static void EcbEncrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        using var aes = Create(key);
        aes.EncryptEcb(src, dst, PaddingMode.None);
    }

    public static void EcbDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        using var aes = Create(key);
        aes.DecryptEcb(src, dst, PaddingMode.None);
    }

    public static void CbcEncrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        using var aes = Create(key);
        aes.EncryptCbc(src, iv, dst, PaddingMode.None);
    }

    public static void CbcDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        using var aes = Create(key);
        aes.DecryptCbc(src, iv, dst, PaddingMode.None);
    }

    /// <summary>
    /// Big-endian counter mode. <paramref name="counter"/> is the counter block for the first byte
    /// of <paramref name="data"/>, which must start on a 16-byte boundary of the stream.
    /// </summary>
    public sealed class Ctr : IDisposable
    {
        readonly Aes _aes;
        byte[] _ks = new byte[0x10000];
        byte[] _ctrs = new byte[0x10000];

        public Ctr(ReadOnlySpan<byte> key) => _aes = Create(key);

        public void Transform(Span<byte> data, Span<byte> counter)
        {
            while (data.Length > 0)
            {
                int n = Math.Min(data.Length, _ks.Length);
                int blocks = (n + 15) / 16;
                for (int b = 0; b < blocks; b++)
                {
                    counter.CopyTo(_ctrs.AsSpan(b * 16, 16));
                    Increment(counter);
                }
                _aes.EncryptEcb(_ctrs.AsSpan(0, blocks * 16), _ks.AsSpan(0, blocks * 16), PaddingMode.None);
                XorInPlace(data[..n], _ks.AsSpan(0, n));
                data = data[n..];
            }
        }

        static void Increment(Span<byte> c)
        {
            for (int i = 15; i >= 0; i--)
                if (++c[i] != 0) break;
        }

        public void Dispose() => _aes.Dispose();
    }

    public static void XorInPlace(Span<byte> data, ReadOnlySpan<byte> key)
    {
        int i = 0;
        var vd = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, System.Numerics.Vector<byte>>(data);
        var vk = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, System.Numerics.Vector<byte>>(key);
        for (; i < vd.Length; i++) vd[i] ^= vk[i];
        for (i *= System.Numerics.Vector<byte>.Count; i < data.Length; i++) data[i] ^= key[i];
    }

    /// <summary>counter = iv + blockIndex (128-bit big-endian add).</summary>
    public static byte[] CounterAt(ReadOnlySpan<byte> iv, ulong blockIndex)
    {
        var c = new byte[16];
        ulong n = blockIndex;
        for (int i = 15; i >= 0; i--)
        {
            n += iv[i];
            c[i] = (byte)n;
            n >>= 8;
        }
        return c;
    }
}
