using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="ShaderHints.CanonicalKey"/>, what "compilar shaders ao carregar o programa" relies on
/// (VulkanRenderer.ProgramLoad.cs): hints with the same canonical key translate to the same shader, so a module
/// translated with the predicted formats can serve a draw with other exact formats. Checked over every texture base
/// format and swizzle, and every colour surface base format and swizzle, against the generator itself.
/// </summary>
[Collection("guest")]
public sealed class CanonicalHintsTests
{
    private static readonly GxpProgram s_sampling = new(RendererTextureTests.SamplingFragmentGxp(), 0x81000100);
    private static readonly GxpProgram s_samplingFetch = new(RendererTextureTests.SamplingFragmentGxp(fragColor: true), 0x81000101);

    /// <summary>The GLSL without its header comment (which names the exact surface format for debugging).</summary>
    private static string Body(GeneratedShader generated) =>
        string.Join('\n', generated.Glsl.Split('\n').Where(line => !line.StartsWith("// gxp ", StringComparison.Ordinal)));

    private static ShaderHints Hints(uint textureFormat, uint colorFormat, bool fetch = false)
    {
        uint[] formats = new uint[16];
        Array.Fill(formats, 0x0C000000u);
        formats[3] = textureFormat;
        return new ShaderHints(formats, [], colorFormat, FetchInputAttachment: fetch);
    }

    public static IEnumerable<uint> TextureFormats()
    {
        foreach (TexBase texBase in Enum.GetValues<TexBase>())
        {
            for (uint swizzle = 0; swizzle < 8; swizzle++)
            {
                yield return (uint)texBase | swizzle << 12;
            }
        }
    }

    [Fact]
    public void TextureFormatsWithTheSameCanonicalKeyTranslateAlike()
    {
        var byKey = new Dictionary<ulong, (uint Format, string Glsl)>();
        int shared = 0;
        foreach (uint format in TextureFormats())
        {
            ShaderHints hints = Hints(format, 0);
            string glsl = Body(ShaderGenerator.Generate(s_sampling, hints));
            if (byKey.TryGetValue(hints.CanonicalKey(), out (uint Format, string Glsl) first))
            {
                Assert.True(first.Glsl == glsl, $"texture formats 0x{first.Format:X8} and 0x{format:X8} share a canonical key but translate differently");
                shared++;
            }
            else
            {
                byKey[hints.CanonicalKey()] = (format, glsl);
            }
        }

        // the classes do merge (the compressed, palette and byte formats with four components are one shader)
        Assert.True(shared > 100, $"{shared} formats share a class");
        Assert.Equal(Hints(0x0C000000, 0).CanonicalKey(), Hints((uint)TexBase.UBC1, 0).CanonicalKey());
        Assert.Equal(Hints(0x0C000000, 0).CanonicalKey(), Hints((uint)TexBase.ETC1, 0).CanonicalKey());
        Assert.NotEqual(Hints(0x0C000000, 0).CanonicalKey(), Hints((uint)TexBase.F16F16F16F16, 0).CanonicalKey());
    }

    [Fact]
    public void ColourFormatsWithTheSameCanonicalKeyTranslateAlike()
    {
        foreach (GxpProgram program in new[] { s_sampling, s_samplingFetch })
        {
            var byKey = new Dictionary<ulong, (uint Format, string Glsl)>();
            for (uint baseFormat = 0; baseFormat < 32; baseFormat++)
            {
                for (uint swizzle = 0; swizzle < 4; swizzle++)
                {
                    // the base format sits in bits 23-24 and 28-31 (SceGxmColorBaseFormat), the swizzle in 20-21
                    uint format = (baseFormat & 3) << 23 | (baseFormat >> 2) << 28 | swizzle << 20;
                    ShaderHints hints = Hints(0x0C000000, format, fetch: program == s_samplingFetch);
                    string glsl = Body(ShaderGenerator.Generate(program, hints));
                    if (byKey.TryGetValue(hints.CanonicalKey(), out (uint Format, string Glsl) first))
                    {
                        Assert.True(first.Glsl == glsl, $"colour formats 0x{first.Format:X8} and 0x{format:X8} share a canonical key but translate differently");
                    }
                    else
                    {
                        byKey[hints.CanonicalKey()] = (format, glsl);
                    }
                }
            }
        }
    }

    /// <summary>The canonical key never equals the exact one, and every hint the generator reads still separates keys.</summary>
    [Fact]
    public void TheCanonicalKeyKeepsEveryHintTheGeneratorReads()
    {
        ShaderHints plain = Hints(0x0C000000, 0);
        Assert.NotEqual(plain.Key(), plain.CanonicalKey());
        Assert.NotEqual(plain.CanonicalKey(), (plain with { FetchInputAttachment = true }).CanonicalKey());
        Assert.NotEqual(plain.CanonicalKey(), (plain with { TileWord1 = true }).CanonicalKey());
        Assert.NotEqual(plain.CanonicalKey(), (plain with { OutputRegisterFormat = 3 }).CanonicalKey());
        Assert.NotEqual(plain.CanonicalKey(), (plain with { FloatBitsOutput = true }).CanonicalKey());
        Assert.NotEqual(plain.CanonicalKey(), (plain with { VaryingMask = 0x30 }).CanonicalKey());
        Assert.NotEqual(plain.CanonicalKey(), (plain with { TexcoordRemap = 0x12 }).CanonicalKey());
        Assert.NotEqual(plain.CanonicalKey(), (plain with { Attributes = [(0, 4, 0)] }).CanonicalKey());
        // a render-target reinterpretation is its own shader
        Assert.NotEqual(plain.CanonicalKey(), Hints(0x0C000000 | ShaderGenerator.RtReinterpretHint(2, 2), 0).CanonicalKey());
        Assert.NotEqual(plain.CanonicalKey(), Hints(0, 0x01000000).CanonicalKey());
    }
}
