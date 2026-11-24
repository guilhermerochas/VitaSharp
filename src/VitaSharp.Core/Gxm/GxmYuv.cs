namespace VitaSharp.Core.Gxm;

/// <summary>
/// The YUV -> RGB matrices of the GPU's two colour space converters. A YUV texture format names its converter (CSC0 or
/// CSC1, <see cref="CscOf"/>); the converter's matrix is the SceGxmYuvProfile the context gave it with
/// sceGxmSetYuvProfile, latched by sceGxmBeginScene. libgxm 3.74 (study of libgxm_es4.suprx) keeps one 6-word entry per
/// profile (luma scale, the four chroma terms and the per-channel offsets, 9 fraction bits) and sends the two selected
/// entries with each scene.
/// </summary>
public static class GxmYuv
{
    /// <summary>SceGxmYuvProfile.</summary>
    public const byte Bt601Standard = 0, Bt709Standard = 1, Bt601FullRange = 2, Bt709FullRange = 3;

    /// <summary>The converter a YUV texture format uses: 0 (CSC0) or 1 (CSC1). YUV420P2/P3 name it in swizzle bit 1
    /// (SCE_GXM_TEXTURE_SWIZZLE_YUV_CSC1 / YVU_CSC1), YUV422 in bit 2 (the *_CSC1 orders, 0x4000-0x7000); -1 for the
    /// other formats.</summary>
    public static int CscOf(uint textureFormat)
    {
        uint swizzle = GxmFormats.SwizzleOf(textureFormat) >> 12;
        switch (GxmFormats.BaseOf(textureFormat))
        {
            case TexBase.YUV420P2:
            case TexBase.YUV420P3:
                return (int)((swizzle >> 1) & 1);
            case TexBase.YUV422:
                return (int)((swizzle >> 2) & 1);
            default:
                return -1;
        }
    }

    /// <summary>
    /// The conversion of <paramref name="profile"/> (its low 2 bits: a higher value, which the setter takes only from
    /// programs built before SDK 2.50, indexes past libgxm's 4-entry table) in 8 fraction bits:
    /// R = Luma * (Y - LumaOffset) + RFromV * (V - 128), G = Luma * (Y - LumaOffset) - GFromU * (U - 128) - GFromV *
    /// (V - 128), B = Luma * (Y - LumaOffset) + BFromU * (U - 128). From the standards' Kr/Kb (BT.601: 0.299/0.114,
    /// BT.709: 0.2126/0.0722); standard range scales luma by 255/219 from 16 and chroma by 255/224, full range by 1.
    /// </summary>
    public static (int Luma, int LumaOffset, int RFromV, int GFromU, int GFromV, int BFromU) Coefficients(int profile)
    {
        bool bt709 = (profile & 1) != 0;
        bool fullRange = (profile & 2) != 0;
        double kr = bt709 ? 0.2126 : 0.299;
        double kb = bt709 ? 0.0722 : 0.114;
        double kg = 1 - kr - kb;
        double lumaScale = fullRange ? 1 : 255.0 / 219;
        double chromaScale = fullRange ? 1 : 255.0 / 224;
        double rFromV = 2 * (1 - kr) * chromaScale;
        double bFromU = 2 * (1 - kb) * chromaScale;
        double gFromU = bFromU * kb / kg;
        double gFromV = rFromV * kr / kg;
        return (Fixed(lumaScale), fullRange ? 0 : 16, Fixed(rFromV), Fixed(gFromU), Fixed(gFromV), Fixed(bFromU));
    }

    private static int Fixed(double value) => (int)Math.Round(value * 256, MidpointRounding.AwayFromZero);
}

/// <summary>The SceGxmYuvProfile of each colour space converter (CSC0, CSC1) for a scene.</summary>
public readonly record struct GxmYuvProfiles(byte Csc0, byte Csc1)
{
    /// <summary>What sceGxmCreateContext sets (libgxm 3.74): CSC0 BT.601 standard, CSC1 BT.709 standard.</summary>
    public static GxmYuvProfiles Default => new(GxmYuv.Bt601Standard, GxmYuv.Bt709Standard);

    /// <summary>The profile (0-3) that converts a texture of <paramref name="textureFormat"/>; 0 for a format that is
    /// not YUV.</summary>
    public int ProfileOf(uint textureFormat)
    {
        int csc = GxmYuv.CscOf(textureFormat);
        if (csc < 0)
        {
            return 0;
        }

        return (csc == 0 ? Csc0 : Csc1) & 3;
    }
}
