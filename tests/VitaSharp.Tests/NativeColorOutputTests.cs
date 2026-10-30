using System.Buffers.Binary;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>
/// Colour output of native-colour fragment programs created with an explicit output register format (Tearaway: half4
/// declared, UCHAR4 output registers, colour packed to U8 by the program itself), with instructions from its menu.
/// </summary>
public class NativeColorOutputTests
{
    // pa0.x = VPCK.U8.F16 scaled (pa0.z, pa0.w); pa0.y = VPCK.F16 sa0; o0..o1 = VMOV.F16 pa0..pa1 (fs A726102642A48D16)
    static readonly ulong[] PackedBytes = [0x40810A3EA01D8100, 0x40810B52E0000000, 0x3880042185000000];
    // o0..o1 = VPCK.F16.U8 scaled r0 (fs 124F80244364D367, the final full-screen pass)
    static readonly ulong[] HalfColour = [0x4081017D201D8000];

    // LittleBigPlanet UI blend tail (fs 57824E964A7FD2D2, declared uchar4, created with HALF4): pa0 = VPCK.U8.F16
    // scaled pa0..pa1, r0 = SOP2 blend of pa0 over the framebuffer colour o0 (packed bytes), o0 = VBW r0
    static readonly ulong[] ByteBlend = [0x40850A3EA01D8000, 0x8180821C90000000, 0x50A1000920000000];

    /// <summary>A native-colour fragment GXP declaring an F16x4 output (or <paramref name="output"/>), running <paramref name="code"/>.</summary>
    static GxpProgram Program(ulong[] code, uint extraFlags = 0, ParamType output = ParamType.F16)
    {
        var d = new byte[0x100 + code.Length * 8];
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

    static string OutColor(string glsl) => glsl.Split('\n').Last(l => l.TrimStart().StartsWith("out_color = "));

    static string Generate(ulong[] code, uint outputFormat) =>
        ShaderGenerator.Generate(Program(code), new ShaderHints([], [], 0x00000000, false, outputFormat)).Glsl;

    [Fact]
    public void PackedBytesInO0AreTheColourWithUchar4()
    {
        var glsl = Generate(PackedBytes, 1);
        Assert.Contains("bitfieldExtract(floatBitsToUint(o[0].x), 24, 8)", OutColor(glsl));
        Assert.DoesNotContain("unpackHalf2x16", OutColor(glsl));
    }

    [Fact]
    public void HalfColourInO0IsConvertedWithUchar4()
    {
        var glsl = Generate(HalfColour, 1);
        Assert.Contains("unpackHalf2x16(floatBitsToUint(o[0].y))", OutColor(glsl));
    }

    [Fact]
    public void DeclaredOutputFormatKeepsTheDeclaredType()
    {
        var glsl = Generate(PackedBytes, 0);
        Assert.Contains("unpackHalf2x16(floatBitsToUint(o[0].x))", OutColor(glsl));
    }

    [Fact]
    public void DeclaredBytesStayBytesWithHalf4()
    {
        var glsl = ShaderGenerator.Generate(Program(ByteBlend, GxpProgram.FlagFragColorUsed, ParamType.U8),
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
        var glsl = Generate(PackedBytes, 1);
        Assert.DoesNotContain("o[0].x = uintBitsToFloat(packHalf2x16(", glsl);
        Assert.DoesNotContain("o[0].y = uintBitsToFloat(packHalf2x16(", glsl);
    }

    // the line of the framebuffer-fetch preload of o0 (programmable blending)
    static string FetchPreload(ulong[] code, uint flags) =>
        string.Join("\n", ShaderGenerator.Generate(Program(code, flags), new ShaderHints([], [], 0x00000000, false, 1)).Glsl
            .Split('\n').SkipWhile(l => !l.Contains("last_frag_tex, ivec2")).Take(6));

    [Fact]
    public void FragColourIsFetchedAsPackedBytesWithUchar4()
    {
        // Tearaway's blend programs copy o0 and unpack it as bytes (VBW + SOP2): the fetch packs the pixel into o0.x
        var fetch = FetchPreload(HalfColour, GxpProgram.FlagFragColorUsed);
        Assert.Contains("bitfieldInsert", fetch);
        Assert.DoesNotContain("packHalf2x16", fetch);
    }

    [Fact]
    public void FragColourIsFetchedInTheDeclaredTypeWithFlag4()
    {
        // fs FBCBF73FF511C077 (pause cross-fade) / CED46C72A7EE1040 (intro camera sky) blend o0 with F16 instructions
        var fetch = FetchPreload(HalfColour, GxpProgram.FlagFragColorUsed | GxpProgram.FlagFragColorDeclared);
        Assert.Contains("packHalf2x16", fetch);
        Assert.DoesNotContain("bitfieldInsert", fetch);
    }

    [Fact]
    public void OutputFormatChangesTheHintKeyOnlyWhenSet()
    {
        var plain = new ShaderHints([0x0C000000], [], 0);
        Assert.Equal(plain.Key(), (plain with { OutputRegisterFormat = 0 }).Key());
        Assert.NotEqual(plain.Key(), (plain with { OutputRegisterFormat = 1 }).Key());
        Assert.NotEqual((plain with { OutputRegisterFormat = 1 }).Key(), (plain with { OutputRegisterFormat = 5 }).Key());
    }
}
