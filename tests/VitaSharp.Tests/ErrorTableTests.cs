using VitaSharp.Core.Hle.Modules;

namespace VitaSharp.Tests;

/// <summary>sceErrorGetExternalString's text: os0:kd/error_table.bin lookup as SceError (FW 3.74) does it.</summary>
public class ErrorTableTests
{
    /// <summary>A table laid out like error_table.bin: default prefix, ranges, base 2000, codes.</summary>
    static byte[] Table(params uint[] codes)
    {
        var b = new List<byte>();
        void U16(int v) => b.AddRange(BitConverter.GetBytes((ushort)v));
        void Pfx(string s) { var p = new byte[4]; System.Text.Encoding.ASCII.GetBytes(s).CopyTo(p, 0); b.AddRange(p); }
        Pfx("C0"); U16(2); U16(codes.Length);
        U16(1); U16(0x0F); Pfx("C1");
        U16(0x41); U16(1); Pfx("NW");
        U16(2000); U16(codes.Length);
        foreach (uint c in codes) b.AddRange(BitConverter.GetBytes(c));
        return b.ToArray();
    }

    [Fact]
    public void FormatsPrefixNumberAndCheckDigit()
    {
        var t = ErrorTable.Parse(Table(0x80020600, 0x80010002, 0x80410001, 0x82000001, 0x80F00001));
        Assert.NotNull(t);
        Assert.Equal("C1-2000-2", ErrorTable.Format(t, 0x80020600)); // facility 0x002 in C1's range, 2+0+0+0 = 2
        Assert.Equal("C1-2001-3", ErrorTable.Format(t, 0x80010002));
        Assert.Equal("NW-2002-4", ErrorTable.Format(t, 0x80410001)); // facility 0x041: the NW range
        Assert.Equal("NS-2003-5", ErrorTable.Format(t, 0x82000001)); // facility 0x200..0x2FF: NS
        Assert.Equal("C0-2004-6", ErrorTable.Format(t, 0x80F00001)); // no range: the default prefix
        Assert.Equal("E-80123456", ErrorTable.Format(t, 0x80123456)); // not in the table
        Assert.Equal("E-80010002", ErrorTable.Format(null, 0x80010002));
    }
}
