using System.Buffers.Binary;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="ShaderHints.FloatBitsOutput"/>: on a surface of 32-bit float channels a float colour can go out as its
/// bits through the surface's integer view, so a draw joins a rendering that raw-bit programs keep open on that view
/// (Uncharted's 720x408 F32F32 G-buffer: a few float draws among raw ones ended the rendering twice per frame). The
/// words are the ones the float view stores (<c>floatBitsToUint</c> of the same colour) and what the Vita writes: its
/// output register words go to the surface unconverted.
/// </summary>
[Collection("guest")]
public sealed class FloatBitsOutputTests
{
    private const uint F32F32 = 0x11000000;
    private const uint F32 = 0x10800000;
    private const uint F16F16F16F16 = 0x01000000;
    private const uint U8U8U8U8 = 0x00000000;

    /// <summary>A fragment program with a float colour output and one plain move (the fuzz tests' program shape).</summary>
    private static GxpProgram Program()
    {
        ulong[] code = [0x6081000460600003];
        byte[] d = new byte[0x100 + code.Length * 8];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        W32(0x14, GxpProgram.FlagFragment);
        W32(0x3C, (uint)code.Length);
        W32(0x40, 0x100 - 0x40);
        for (int i = 0; i < code.Length; i++)
        {
            W32(0x100 + i * 8, (uint)code[i]);
            W32(0x104 + i * 8, (uint)(code[i] >> 32));
        }
        return new GxpProgram(d, 0x81000000);
    }

    private static GeneratedShader Generate(uint colorFormat, bool floatBits) =>
        ShaderGenerator.Generate(Program(), new ShaderHints([], [], colorFormat, FloatBitsOutput: floatBits));

    [Theory]
    [InlineData(F32F32)]
    [InlineData(F32)]
    public void AFloatColourGoesOutAsItsBitsThroughTheIntegerView(uint colorFormat)
    {
        GeneratedShader gen = Generate(colorFormat, floatBits: true);

        Assert.Equal(32, gen.RawColorBits);
        Assert.Contains("layout(location = 0) out uvec4 out_raw;", gen.Glsl);
        Assert.DoesNotContain("out vec4 out_color", gen.Glsl);
        // the same colour the float view would store, then its bits (after any debug override of out_color)
        int colour = gen.Glsl.IndexOf("out_color = ", StringComparison.Ordinal);
        int bits = gen.Glsl.LastIndexOf("out_raw = floatBitsToUint(out_color);", StringComparison.Ordinal);
        Assert.True(colour > 0 && bits > colour, "the bits must be taken from the final colour");
    }

    [Fact]
    public void WithoutTheHintTheColourKeepsTheFloatView()
    {
        GeneratedShader gen = Generate(F32F32, floatBits: false);

        Assert.Equal(0, gen.RawColorBits);
        Assert.Contains("layout(location = 0) out vec4 out_color;", gen.Glsl);
        Assert.DoesNotContain("out_raw", gen.Glsl);
    }

    [Theory]
    [InlineData(F16F16F16F16)]
    [InlineData(U8U8U8U8)]
    public void SurfacesWhoseFloatViewConvertsIgnoreTheHint(uint colorFormat)
    {
        // halves and normalised bytes are conversions of the colour: their bits are not the colour's float bits
        Assert.False(ShaderGenerator.FloatBitsOutputPossible(colorFormat));
        GeneratedShader gen = Generate(colorFormat, floatBits: true);

        Assert.Equal(0, gen.RawColorBits);
        Assert.Contains("layout(location = 0) out vec4 out_color;", gen.Glsl);
    }

    [Fact]
    public void TheVariantHasItsOwnKeyAndLeavesTheOthersUnchanged()
    {
        var plain = new ShaderHints([0x0C000000], [], F32F32);

        Assert.NotEqual(plain.Key(), (plain with
        {
            FloatBitsOutput = true
        }).Key());
        Assert.Equal(new ShaderHints([0x0C000000], [], F32F32, false, 0, false, false, 0, false).Key(), plain.Key());
    }

    [Fact]
    public void TheVariantsRecipeRoundTripsAndOtherRecordsKeepTheirFlagByte()
    {
        var plain = new ShaderHints([0x0C000000], [], F32F32);
        ShaderHints bits = plain with
        {
            FloatBitsOutput = true
        };

        var back = ShaderRecipe.Decode(new ShaderRecipe(7, bits.Key(), false, bits).Encode());

        Assert.NotNull(back);
        Assert.True(back!.Hints.FloatBitsOutput);
        Assert.Equal(bits.Key(), back.HintKey);
        Assert.Equal(0, new ShaderRecipe(7, plain.Key(), false, plain).Encode()[^1]);
        Assert.Equal(16, new ShaderRecipe(7, bits.Key(), false, bits).Encode()[^1]);
    }

    [ShadercFact]
    public void TheVariantCompiles()
    {
        using var compiler = new ShaderCompiler(null);

        Assert.NotNull(compiler.Compile(Generate(F32F32, floatBits: true).Glsl, false, out string? error));
        Assert.Null(error);
    }
}
