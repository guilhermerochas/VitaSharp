using VitaSharp.Core.Package;

namespace VitaSharp.Tests;

public class ZrifTests
{
    // a fake NoNpDrm license (content ID EP0001-PCSE00000_00-0000000000000001, klicensee 10..1F) compressed like
    // NoNpDrm does: zlib window 10 against the zRIF preset dictionary (made with Python's zlib zdict)
    const string Sample = "KO5ifR1dg/Fh7nENQDbVAGy/ASowxOc/AUEhYRFRMXEJSSlpGVk5+dEYH9wAAJu/CQk=";

    [Fact]
    public void DecodesDictionaryCompressedLicense()
    {
        Assert.True(Zrif.LooksLikeZrif(Sample));
        var rif = Zrif.Decode(Sample);
        Assert.Equal(Zrif.RifSize, rif.Length);
        Assert.Equal("EP0001-PCSE00000_00-0000000000000001", Zrif.ContentId(rif));
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(0x10 + i, rif[0x50 + i]);
        }
    }

    [Fact]
    public void RejectsCorruptedText()
    {
        Assert.False(Zrif.LooksLikeZrif("work.bin"));
        Assert.Throws<InvalidDataException>(() => Zrif.Decode("not base64 at all!"));
        var bad = Convert.FromBase64String(Sample);
        bad[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => Zrif.Decode(Convert.ToBase64String(bad)));
    }
}
