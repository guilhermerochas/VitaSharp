using System.Buffers.Binary;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>
/// Colour output of native-colour fragment programs created with an explicit output register format (Tearaway: half4
/// declared, UCHAR4 output registers, colour packed to U8 by the program itself), with instructions from its menu.
/// </summary>
[Collection("shadergen")]
public class NativeColorOutputTests
{
    // pa0.x = VPCK.U8.F16 scaled (pa0.z, pa0.w); pa0.y = VPCK.F16 sa0; o0..o1 = VMOV.F16 pa0..pa1 (fs A726102642A48D16)
    private static readonly ulong[] s_packedBytes = [0x40810A3EA01D8100, 0x40810B52E0000000, 0x3880042185000000];
    // o0..o1 = VPCK.F16.U8 scaled r0 (fs 124F80244364D367, the final full-screen pass)
    private static readonly ulong[] s_halfColour = [0x4081017D201D8000];

    // LittleBigPlanet UI blend tail (fs 57824E964A7FD2D2, declared uchar4, created with HALF4): pa0 = VPCK.U8.F16
    // scaled pa0..pa1, r0 = SOP2 blend of pa0 over the framebuffer colour o0 (packed bytes), o0 = VBW r0
    private static readonly ulong[] s_byteBlend = [0x40850A3EA01D8000, 0x8180821C90000000, 0x50A1000920000000];

    /// <summary>A native-colour fragment GXP declaring an F16x4 output (or <paramref name="output"/>), running <paramref name="code"/>.</summary>
    private static GxpProgram Program(ulong[] code, uint extraFlags = 0, ParamType output = ParamType.F16)
    {
        byte[] d = new byte[0x100 + code.Length * 8];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        W32(0x14, GxpProgram.FlagFragment | GxpProgram.FlagNativeColorUsed | extraFlags);
        W32(0x2C, 0x60 - 0x2C); // varyings
        d[0x60 + 10] = (byte)output; // output parameter type
        d[0x60 + 11] = 4; // output components
        W32(0x3C, (uint)code.Length);
        W32(0x40, 0x100 - 0x40); // primary program
        for (int i = 0; i < code.Length; i++)
        {
            W32(0x100 + i * 8, (uint)code[i]);
            W32(0x104 + i * 8, (uint)(code[i] >> 32));
        }
        var p = new GxpProgram(d, 0x81000000);
        // the reader must give back the instructions in the order written
        Assert.Equal(code[0], p.ReadInstruction(p.PrimaryProgramStart, 0));
        return p;
    }

    private static string OutColor(string glsl) => glsl.Split('\n').Last(l => l.TrimStart().StartsWith("out_color = "));

    private static string Generate(ulong[] code, uint outputFormat) =>
        ShaderGenerator.Generate(Program(code), new ShaderHints([], [], 0x00000000, false, outputFormat)).Glsl;

    [Fact]
    public void PackedBytesInO0AreTheColourWithUchar4()
    {
        string glsl = Generate(s_packedBytes, 1);
        Assert.Contains("bitfieldExtract(floatBitsToUint(o[0].x), 24, 8)", OutColor(glsl));
        Assert.DoesNotContain("unpackHalf2x16", OutColor(glsl));
    }

    [Fact]
    public void HalfColourInO0IsConvertedWithUchar4()
    {
        string glsl = Generate(s_halfColour, 1);
        Assert.Contains("unpackHalf2x16(floatBitsToUint(o[0].y))", OutColor(glsl));
    }

    [Fact]
    public void DeclaredOutputFormatKeepsTheDeclaredType()
    {
        string glsl = Generate(s_packedBytes, 0);
        Assert.Contains("unpackHalf2x16(floatBitsToUint(o[0].x))", OutColor(glsl));
    }

    [Fact]
    public void DeclaredBytesStayBytesWithHalf4()
    {
        string glsl = ShaderGenerator.Generate(Program(s_byteBlend, GxpProgram.FlagFragColorUsed, ParamType.U8),
            new ShaderHints([], [], 0x00000000, false, 5)).Glsl;
        // the framebuffer colour is preloaded as packed bytes and the blended bytes are the colour
        Assert.DoesNotContain("unpackHalf2x16", OutColor(glsl));
        Assert.Contains("bitfieldExtract(floatBitsToUint(o[0].x), 24, 8)", OutColor(glsl));
        Assert.DoesNotContain("o[0].x = uintBitsToFloat(packHalf2x16(", glsl);
    }

    [Fact]
    public void UnconditionalF16MoveCopiesBits()
    {
        // through unpackHalf2x16/packHalf2x16 the packed bytes forming half NaNs would be canonicalised
        string glsl = Generate(s_packedBytes, 1);
        Assert.DoesNotContain("o[0].x = uintBitsToFloat(packHalf2x16(", glsl);
        Assert.DoesNotContain("o[0].y = uintBitsToFloat(packHalf2x16(", glsl);
    }

    // the line of the framebuffer-fetch preload of o0 (programmable blending)
    private static string FetchPreload(ulong[] code, uint flags) =>
        string.Join("\n", ShaderGenerator.Generate(Program(code, flags), new ShaderHints([], [], 0x00000000, false, 1)).Glsl
            .Split('\n').SkipWhile(l => !l.Contains("last_frag_tex, ivec2")).Take(6));

    [Fact]
    public void FragColourIsFetchedAsPackedBytesWithUchar4()
    {
        // Tearaway's blend programs copy o0 and unpack it as bytes (VBW + SOP2): the fetch packs the pixel into o0.x
        string fetch = FetchPreload(s_halfColour, GxpProgram.FlagFragColorUsed);
        Assert.Contains("bitfieldInsert", fetch);
        Assert.DoesNotContain("packHalf2x16", fetch);
    }

    [Fact]
    public void FragColourIsFetchedInTheDeclaredTypeWithFlag4()
    {
        // fs FBCBF73FF511C077 (pause cross-fade) / CED46C72A7EE1040 (intro camera sky) blend o0 with F16 instructions
        string fetch = FetchPreload(s_halfColour, GxpProgram.FlagFragColorUsed | GxpProgram.FlagFragColorDeclared);
        Assert.Contains("packHalf2x16", fetch);
        Assert.DoesNotContain("bitfieldInsert", fetch);
    }

    // Assassin's Creed III Liberation G-buffer tail (fs 256C5AF9CD46C573, declared half4, created with HALF4, F32F32
    // surface): pa0.x = VPCK.U8.F16 albedo, pa0.y = two VPCK.S8.F16 normal halves, o0..o1 = VMOV pa0..pa1
    private static readonly ulong[] s_gbufferWords = [0x40810A3EA01D8000, 0x40810A4EA0250100, 0x40810A72A0270102, 0x3880054183000000];

    private static GeneratedShader GenerateOn(ulong[] code, uint colorFormat, uint outputFormat) =>
        ShaderGenerator.Generate(Program(code), new ShaderHints([], [], colorFormat, false, outputFormat));

    [Fact]
    public void PackedWordsOnF32F32SurfaceAreStoredAsRawBits()
    {
        // the 64-bit output register goes to the 8-byte pixel unconverted: the game samples the surface back as
        // U8U8U8U8 albedo at +0 and S8S8S8S8 normal at +4. Read as bytes of o0.x into a float view, the albedo became
        // two floats and the normal was dropped (cyan/yellow noise over the whole scene)
        GeneratedShader gen = GenerateOn(s_gbufferWords, 0x11000000, 5);
        Assert.Equal(32, gen.RawColorBits);
        Assert.Contains("uvec4 raw_words = uvec4(uvec2(floatBitsToUint(o[0].x), floatBitsToUint(o[0].y)), 0u, 0u);", gen.Glsl);
        Assert.Contains("out_raw = raw_words;", gen.Glsl);
    }

    [Fact]
    public void FetchedWordsOnF32F32SurfaceAreTheRawRegisterWords()
    {
        // the decal blend programs of the same G-buffer (fs E75AC07A68CC79A1) read the pixel and SOP2 its packed
        // bytes: the fetch must give back o0.x/o0.y as the surface's words, not as four halves, and the blended words
        // go out raw too (the bushes/decals were blue-green noise)
        GeneratedShader gen = ShaderGenerator.Generate(Program(s_gbufferWords, GxpProgram.FlagFragColorUsed), new ShaderHints([], [], 0x11000000, false, 5));
        string fetch = string.Join("\n", gen.Glsl.Split('\n').SkipWhile(l => !l.Contains("last_frag_words, ivec2")).Take(6));
        Assert.DoesNotContain("packHalf2x16", fetch);
        Assert.Matches(@"o\[0\]\.x = t\d+\.x;", fetch);
        Assert.Matches(@"o\[0\]\.y = t\d+\.y;", fetch);
        Assert.Equal(32, gen.RawColorBits);
        Assert.Contains("out_raw = raw_words;", gen.Glsl);
    }

    [Theory]
    [InlineData(0x11000000u, 5u)] // HALF4 on F32F32 (the decal programs)
    [InlineData(0x11000000u, 7u)] // FLOAT2 on F32F32
    [InlineData(0x10800000u, 1u)] // UCHAR4 on F32
    [InlineData(0x10800000u, 8u)] // FLOAT on F32
    public void FetchedRawWordsAreReadThroughAnIntegerImage(uint colorFormat, uint outputFormat)
    {
        // packed bytes are denormal/NaN float patterns (an RGBA8 albedo word with alpha 0 is a denormal): through the
        // R32G32_SFLOAT copy a float texel fetch may flush or canonicalise them, and the decals of fs E75AC07A68CC79A1
        // wrote the altered words back where their alpha test kept the pixel (blue/green noise on the ground). The
        // single-sampled copy is an unsigned-integer image whose words become the registers' bits unchanged
        GeneratedShader gen = ShaderGenerator.Generate(Program(s_gbufferWords, GxpProgram.FlagFragColorUsed), new ShaderHints([], [], colorFormat, false, outputFormat));
        Assert.True(gen.FetchesRawWords);
        Assert.Contains("layout(set = 3, binding = 0) uniform usampler2D last_frag_words;", gen.Glsl);
        Assert.DoesNotContain("uniform sampler2D last_frag_tex;", gen.Glsl);
        Assert.Contains("uintBitsToFloat(texelFetch(last_frag_words, ivec2(gl_FragCoord.xy), 0))", gen.Glsl);
        Assert.Equal(32, gen.RawColorBits);
    }

    [Theory]
    [InlineData(0x11000000u, 1u)] // UCHAR4 on F32F32: converted colour
    [InlineData(0x00000000u, 5u)] // U8U8U8U8 surface
    [InlineData(0x01000000u, 5u)] // F16F16F16F16 surface
    [InlineData(0x11000000u, 0u)] // no explicit register format
    public void ConvertedColourFetchKeepsTheFloatCopy(uint colorFormat, uint outputFormat)
    {
        GeneratedShader gen = ShaderGenerator.Generate(Program(s_gbufferWords, GxpProgram.FlagFragColorUsed), new ShaderHints([], [], colorFormat, false, outputFormat));
        Assert.False(gen.FetchesRawWords);
        Assert.Contains("layout(set = 3, binding = 0) uniform sampler2D last_frag_tex;", gen.Glsl);
        Assert.DoesNotContain("last_frag_words", gen.Glsl);
    }

    [Theory]
    [InlineData(0x11000000u, 7u, 2)] // FLOAT2 on F32F32: two words
    [InlineData(0x10800000u, 1u, 1)] // UCHAR4 on F32: one word
    [InlineData(0x10800000u, 6u, 1)] // HALF2 on F32: one word
    [InlineData(0x10800000u, 8u, 1)] // FLOAT on F32: one word
    public void RegisterThatFillsAFloatChannelPixelIsStoredAsRawBits(uint colorFormat, uint outputFormat, int words)
    {
        GeneratedShader gen = GenerateOn(s_gbufferWords, colorFormat, outputFormat);
        Assert.Equal(32, gen.RawColorBits);
        string expected = words == 2 ? "uvec4(uvec2(floatBitsToUint(o[0].x), floatBitsToUint(o[0].y)), 0u, 0u)" : "uvec4(floatBitsToUint(o[0].x), 0u, 0u, 0u)";
        Assert.Contains($"uvec4 raw_words = {expected};", gen.Glsl);
    }

    [Theory]
    [InlineData(0x11000000u, 1u)] // UCHAR4 (4 bytes) on F32F32 (8 bytes): sizes differ
    [InlineData(0x10800000u, 5u)] // HALF4 (8 bytes) on F32 (4 bytes)
    [InlineData(0x00000000u, 5u)] // U8U8U8U8 surface: converted colour, as before
    [InlineData(0x01000000u, 6u)] // HALF2 (4 bytes) on F16F16F16F16 (8 bytes): sizes differ
    [InlineData(0x00800000u, 5u)] // HALF4 (8 bytes) on F16F16 (4 bytes)
    [InlineData(0x01100000u, 5u)] // swizzled F16F16F16F16 surface: host layout differs
    [InlineData(0x11000000u, 0u)] // not created with an explicit register format: declared type, as before
    [InlineData(0x01000000u, 0u)] // F16F16F16F16 without an explicit register format: declared type
    public void OtherSurfacesAndRegisterSizesKeepTheConvertedColour(uint colorFormat, uint outputFormat)
    {
        GeneratedShader gen = GenerateOn(s_gbufferWords, colorFormat, outputFormat);
        Assert.Equal(0, gen.RawColorBits);
        Assert.DoesNotContain("out_raw", gen.Glsl);
    }

    [Fact]
    public void PackedWordsOnF16x4SurfaceAreStoredAsRawBits()
    {
        // HELLDIVERS G-buffer (fs 5CA57D88E2153AF0, declared half4, created with HALF4, 960x544 F16F16F16F16 surface):
        // the 64-bit output register goes to the pixel unconverted and the surface is sampled back as U8U8U8U8 albedo
        // at +0 and S8S8S8S8 normal at +4. Read as the bytes of o0.x into the half view, the albedo became four halves
        // and the normal was dropped (green, saturated ships and planet on the title screen)
        GeneratedShader gen = GenerateOn(s_gbufferWords, 0x01000000, 5);
        Assert.Equal(16, gen.RawColorBits);
        Assert.Contains("uvec4 raw_words = uvec4(uvec2(floatBitsToUint(o[0].x), floatBitsToUint(o[0].y)), 0u, 0u);", gen.Glsl);
        Assert.Contains("out_raw = uvec4(raw_words.x & 0xFFFFu, raw_words.x >> 16, raw_words.y & 0xFFFFu, raw_words.y >> 16);", gen.Glsl);
        Assert.Equal(0, (int)gen.TileWord1);
    }

    [Theory]
    [InlineData(1u)] // UCHAR4 on F16F16
    [InlineData(6u)] // HALF2 on F16F16
    public void PackedWordOnF16F16SurfaceIsStoredAsRawBits(uint outputFormat)
    {
        GeneratedShader gen = GenerateOn(s_gbufferWords, 0x00800000, outputFormat);
        Assert.Equal(16, gen.RawColorBits);
        Assert.Contains("uvec4 raw_words = uvec4(floatBitsToUint(o[0].x), 0u, 0u, 0u);", gen.Glsl);
    }

    [Fact]
    public void HalfColourOnF16x4SurfaceKeepsTheConvertedColour()
    {
        // a real half colour in o0 (VPCK.F16.U8) is the colour the float attachment receives, and keeps blending
        GeneratedShader gen = GenerateOn(s_halfColour, 0x01000000, 5);
        Assert.Equal(0, gen.RawColorBits);
        Assert.Contains("unpackHalf2x16(floatBitsToUint(o[0].y))", OutColor(gen.Glsl));
    }

    [Fact]
    public void OutputFormatChangesTheHintKeyOnlyWhenSet()
    {
        var plain = new ShaderHints([0x0C000000], [], 0);
        Assert.Equal(plain.Key(), (plain with
        {
            OutputRegisterFormat = 0
        }).Key());
        Assert.NotEqual(plain.Key(), (plain with
        {
            OutputRegisterFormat = 1
        }).Key());
        Assert.NotEqual((plain with
        {
            OutputRegisterFormat = 1
        }).Key(), (plain with
        {
            OutputRegisterFormat = 5
        }).Key());
    }
}
