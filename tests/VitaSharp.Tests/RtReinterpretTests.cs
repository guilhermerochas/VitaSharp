using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>
/// Render targets reinterpreted with a smaller texel above native resolution (Resistance's F16x4 G-buffer read as
/// U8x4): the texture's hint carries the target's scale and texels per pixel in bits no GXM format uses, and the
/// shader reads it through a texelFetch helper at the native texel mapping.
/// </summary>
public class RtReinterpretTests
{
    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 4)]
    [InlineData(1, 2)]
    public void HintBitsLeaveTheFormatAlone(int k, int ratio)
    {
        uint hint = ShaderGenerator.RtReinterpretHint(k, ratio);
        Assert.Equal(0u, hint & ~ShaderGenerator.RtReinterpretBits);
        Assert.NotEqual(0u, hint & ShaderGenerator.RtReinterpretFlag);
        uint fmt = 0x0C000000 | 0x1000; // U8U8U8U8 with a swizzle
        Assert.Equal(GxmFormats.TextureComponentType(fmt), GxmFormats.TextureComponentType(fmt | hint));
        Assert.Equal(GxmFormats.TextureComponentCount(fmt), GxmFormats.TextureComponentCount(fmt | hint));
        Assert.Equal(GxmFormats.BaseOf(fmt), GxmFormats.BaseOf(fmt | hint));
    }

    [Fact]
    public void HelperFetchesTheNativeTexelsCopy()
    {
        string scaled = ShaderGenerator.RtFetchHelper("rtfetch_ftex0", "ftex0", ShaderGenerator.RtReinterpretHint(3, 2));
        Assert.Contains("vec4 rtfetch_ftex0(vec2 uv)", scaled);
        Assert.Contains("vec2(sz / 3)", scaled);
        Assert.Contains("ivec2((t.x / 2 * 3 + sub.x) * 2 + t.x % 2, t.y * 3 + sub.y)", scaled);
        Assert.Contains("texelFetch(ftex0,", scaled);
        Assert.DoesNotContain("texture(", scaled);
        // a native-size alias read by a scaled scene: only the native texel
        string native = ShaderGenerator.RtFetchHelper("rtfetch_ftex1", "ftex1", ShaderGenerator.RtReinterpretHint(1, 2));
        Assert.Contains("return texelFetch(ftex1, t, 0);", native);
        Assert.DoesNotContain("sub", native);
    }
}
