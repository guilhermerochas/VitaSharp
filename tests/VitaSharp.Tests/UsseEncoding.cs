namespace VitaSharp.Tests;

/// <summary>
/// Builds USSE instruction words from the decoder's bit patterns (UsseDecoder: one letter per field, MSB first), so
/// tests can write small shader programs. Only what the tests use.
/// </summary>
public static class UsseEncoding
{
    const string VmovPattern = "00111pppstrydecbmmaanoooiwwwwkllffgghhhhjjjjjjqqqqqquuuuuuvvvvvv";

    /// <summary>Sets the fields of <paramref name="pattern"/> to <paramref name="fields"/> (the fixed bits as given).</summary>
    public static ulong Encode(string pattern, params (char Field, uint Value)[] fields)
    {
        ulong word = 0;
        for (int i = 0; i < 64; i++)
        {
            if (pattern[i] == '1')
            {
                word |= 1ul << (63 - i);
            }
        }

        foreach (var (field, value) in fields)
        {
            var bits = Enumerable.Range(0, 64).Where(i => pattern[i] == field).ToArray(); // MSB first
            if (bits.Length == 0)
            {
                throw new ArgumentException($"no field '{field}'");
            }

            if (value >> bits.Length != 0)
            {
                throw new ArgumentException($"{value} does not fit field '{field}'");
            }

            for (int k = 0; k < bits.Length; k++)
            {
                if ((value >> (bits.Length - 1 - k) & 1) != 0)
                {
                    word |= 1ul << (63 - bits[k]);
                }
            }
        }
        return word;
    }

    /// <summary>VMOV.F32 o[dest..dest+3] = pa[src..src+3] (two repeats of two 32-bit words, swizzle XYZW).</summary>
    public static ulong MovF32OutputFromPa(uint destWord, uint srcWord) => Encode(VmovPattern,
        ('s', 1), ('o', 5 /* F32 */), ('a', 1 /* 2 repeats */), ('w', 4 /* XYZW */), ('h', 3),
        ('l', 1 /* output bank */), ('j', destWord / 2), ('f', 2 /* primary attributes */), ('u', srcWord / 2));
}

/// <summary>The renderer tests' shader programs (<see cref="RendererTextureTests"/>) translate to what they are meant
/// to do: the encoder rebuilds a game's VMOV word, the vertex program passes the vertex through as position and texture
/// coordinate, and the fragment program outputs the sampled texture.</summary>
[Collection("shadergen")]
public sealed class TestShaderProgramTests
{
    [Fact]
    public void EncoderRebuildsAGameInstruction()
    {
        // o0..o1 = VMOV.F16 pa0..pa1 (Tearaway fs A726102642A48D16, NativeColorOutputTests)
        Assert.Equal(0x3880042185000000ul, UsseEncoding.Encode("00111pppstrydecbmmaanoooiwwwwkllffgghhhhjjjjjjqqqqqquuuuuuvvvvvv",
            ('s', 1), ('o', 4), ('w', 4), ('h', 5), ('l', 1), ('f', 2)));
        Assert.Throws<ArgumentException>(() => UsseEncoding.Encode("00111pppstrydecbmmaanoooiwwwwkllffgghhhhjjjjjjqqqqqquuuuuuvvvvvv", ('o', 8)));
    }

    [Fact]
    public void VertexProgramPassesTheVertexThrough()
    {
        var p = new VitaSharp.Core.Gxm.Shader.GxpProgram(RendererTextureTests.VertexGxp(), 0x81000000);
        Assert.True(p.IsVertex);
        var glsl = VitaSharp.Core.Gxm.Shader.ShaderGenerator.Generate(p, new VitaSharp.Core.Gxm.Shader.ShaderHints([], [(0, 4, 9)], 0, false, 0)).Glsl;
        Assert.True(glsl.Contains("vec4 vpos = vec4(o[0].x, o[0].y, o[0].z, o[0].w);"), glsl);
        Assert.True(glsl.Contains("v_TexCoord0 = vec4(o[1].x, o[1].y, o[1].z, o[1].w);"), glsl);
        // both outputs are written from the primary attributes
        Assert.True(glsl.Contains("layout(location = 0) in"), glsl); // the attribute feeds pa0
    }

    [Fact]
    public void FragmentProgramOutputsTheTexture()
    {
        var p = new VitaSharp.Core.Gxm.Shader.GxpProgram(RendererTextureTests.SamplingFragmentGxp(), 0x81000000);
        Assert.True(p.IsFragment);
        var glsl = VitaSharp.Core.Gxm.Shader.ShaderGenerator.Generate(p, new VitaSharp.Core.Gxm.Shader.ShaderHints([0, 0, 0, 0x0C000000], [], 0, false, 0)).Glsl;
        Assert.True(glsl.Contains("texture(ftex3, v_TexCoord0.xy)"), glsl);
        Assert.True(glsl.Contains("out_color = vec4(pa[0].x, pa[0].y, pa[0].z, pa[0].w);"), glsl);
    }

    [Fact]
    public void CubeFragmentProgramSamplesThroughACubeSampler()
    {
        var p = new VitaSharp.Core.Gxm.Shader.GxpProgram(RendererTextureTests.SamplingFragmentGxp(cube: true), 0x81000000);
        Assert.Equal(1u << 3, p.CubeSamplers());
        var glsl = VitaSharp.Core.Gxm.Shader.ShaderGenerator.Generate(p, new VitaSharp.Core.Gxm.Shader.ShaderHints([0, 0, 0, 0x0C000000], [], 0, false, 0)).Glsl;
        Assert.True(glsl.Contains("samplerCube ftex3"), glsl);
        Assert.True(glsl.Contains("texture(ftex3, v_TexCoord0.xyz)"), glsl);
    }
}
