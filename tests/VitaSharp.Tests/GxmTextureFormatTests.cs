using Silk.NET.Vulkan;
using VitaSharp.Core.Gxm;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// GXM texture and colour formats against their definitions in vita-headers (gxm.h), the findings of the VitaSDK test
/// game (11/11/2026): U2U10U10U10_ABGR is A2B10G10R10 (red in the low 10 bits).
/// </summary>
public sealed class GxmTextureFormatTests
{
    private const uint U2U10U10U10Abgr = 0x0E000000;
    private const uint ColorU2U10U10U10Abgr = 0x60800000;

    [Fact]
    public void U2U10U10U10AbgrKeepsRedInTheLowBits()
    {
        // gxm.h: SCE_GXM_COLOR_FORMAT_U2U10U10U10_ABGR "32-bit unsigned A2B10G10R10 color format"; the texture format
        // of the same name has the same memory order. Vulkan's A2B10G10R10 has red in bits 0-9 and alpha in 30-31.
        Assert.Equal(Format.A2B10G10R10UnormPack32, Formats.Texture(GxmFormats.BaseOf(U2U10U10U10Abgr)).Format);
        Assert.Equal(Format.A2B10G10R10UnormPack32, Formats.ColorFormat(ColorU2U10U10U10Abgr));
        // _ABGR is the identity swizzle, as for U8U8U8U8_ABGR on R8G8B8A8
        ComponentMapping mapping = Formats.TextureSwizzle(U2U10U10U10Abgr);
        Assert.Equal(ComponentSwizzle.R, mapping.R);
        Assert.Equal(ComponentSwizzle.B, mapping.B);
    }
}
