using VitaSharp.Core.Hle.Np;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>Reading a title's SceNpCommunicationSignature from its code (the migration of trophies earned before
/// VitaSharp kept the signature).</summary>
public class TrophySignatureTests
{
    static byte[] Signature(byte fill)
    {
        var s = new byte[TrophyUnlocks.SignatureSize];
        Array.Fill(s, fill);
        s[0] = 0xB9; s[1] = 0xDD; s[2] = 0xE1; s[3] = 0x3B;
        return s;
    }

    [Fact]
    public void FindsTheSignatureNearestToTheSetId()
    {
        // two sets in one executable, each id followed by its signature (as SceNpCommunicationId + signature constants)
        var image = new byte[0x1000];
        "NPWR01111\0"u8.CopyTo(image.AsSpan(0x100));
        Signature(0x11).CopyTo(image.AsSpan(0x110));
        "NPWR02222\0"u8.CopyTo(image.AsSpan(0x800));
        Signature(0x22).CopyTo(image.AsSpan(0x810));
        Assert.Equal(Signature(0x22), TrophySignature.Find(image, "NPWR02222_00"));
        Assert.Equal(Signature(0x11), TrophySignature.Find(image, "NPWR01111_00"));
        // the id is not in the image: the first one
        Assert.Equal(Signature(0x11), TrophySignature.Find(image, "NPWR03333_00"));
    }

    [Fact]
    public void NoSignatureNoResult()
    {
        var image = new byte[0x400];
        Assert.Null(TrophySignature.Find(image, "NPWR01111_00"));
        // the magic too close to the end for a whole signature
        Signature(0x11).AsSpan(0, 8).CopyTo(image.AsSpan(0x3F8));
        Assert.Null(TrophySignature.Find(image, "NPWR01111_00"));
    }
}
