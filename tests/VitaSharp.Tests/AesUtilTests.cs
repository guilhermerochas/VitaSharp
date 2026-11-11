using VitaSharp.Core.Crypto;

namespace VitaSharp.Tests;

public class AesUtilTests
{
    /// <summary>CTR keystream built the slow way: one ECB block per counter, byte-wise big-endian increment.</summary>
    static byte[] ReferenceCtr(byte[] key, byte[] counter, byte[] data)
    {
        var c = (byte[])counter.Clone();
        var outp = (byte[])data.Clone();
        var ks = new byte[16];
        for (int off = 0; off < outp.Length; off += 16)
        {
            AesUtil.EcbEncrypt(key, c, ks);
            for (int i = 0; i < 16 && off + i < outp.Length; i++)
            {
                outp[off + i] ^= ks[i];
            }

            for (int i = 15; i >= 0; i--)
            {
                if (++c[i] != 0)
                {
                    break;
                }
            }
        }
        return outp;
    }

    [Theory]
    [InlineData("000102030405060708090A0B0C0D0E0F", 100)]
    [InlineData("00000000000000FFFFFFFFFFFFFFFFF0", 0x10000 * 2 + 37)] // carry into the high 64 bits mid-buffer
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFE", 64)]               // wraps around 2^128
    public void Ctr_MatchesBlockByBlockReference(string counterHex, int length)
    {
        var key = Convert.FromHexString("2B7E151628AED2A6ABF7158809CF4F3C");
        var counter = Convert.FromHexString(counterHex);
        var data = new byte[length];
        new Random(length).NextBytes(data);

        var expected = ReferenceCtr(key, counter, data);
        var actual = (byte[])data.Clone();
        var c = (byte[])counter.Clone();
        using (var ctr = new AesUtil.Ctr(key))
        {
            // two calls: the counter must carry on where the first one stopped
            int split = (length / 2) & ~15;
            ctr.Transform(actual.AsSpan(0, split), c);
            ctr.Transform(actual.AsSpan(split), c);
        }
        Assert.Equal(expected, actual);
        Assert.Equal(AesUtil.CounterAt(counter, (ulong)((length + 15) / 16)), c);
    }
}
