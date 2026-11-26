using System.Buffers.Binary;
using System.Text.RegularExpressions;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="ShaderGenerator.VectorLoads"/>: a USSE memory load of four words (Killzone's skinning vertex programs
/// read their bone matrices that way) becomes one <c>fetch4</c>, which reads the four words with one 128-bit load
/// through a uvec4 view of the same binding when the address is 16-byte aligned and all four words are in range, and
/// falls back to the four <c>fetchMemory</c> word loads otherwise, so the values never change. Off, the program
/// keeps the word loads. The instruction is the load Killzone's vertex programs use (VLDST, four F32 words through
/// the pointer in sa[5].z), in a synthetic program with one uniform buffer.
/// </summary>
[Collection("guest")]
public sealed class VectorLoadsTests
{
    private const ulong FourWordLoad = 0xE8833084A3C58000;
    private const ulong PositionMoveA = 0x6081000460600003;
    private const ulong PositionMoveB = 0x50C9000A20200180;

    /// <summary>A program with one uniform buffer of <paramref name="bufferBytes"/> bytes (memory only) and <paramref name="code"/>.</summary>
    private static GxpProgram Program(ulong[] code, bool fragment, uint bufferBytes)
    {
        byte[] d = new byte[0x100 + code.Length * 8];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        if (fragment)
        {
            W32(0x14, GxpProgram.FlagFragment);
        }

        W32(0x24, 1); // parameter count
        W32(0x28, 0xC0 - 0x28); // parameter table
        W32(0xC0, 0xF0 - 0xC0); // name (empty)
        d[0xC4] = (byte)ParamCategory.UniformBuffer | ((byte)ParamType.Aggregate << 4);
        W32(0xC8, bufferBytes); // array size: bytes of the buffer
        W32(0xCC, 0); // resource index: buffer 0
        W32(0x3C, (uint)code.Length);
        W32(0x40, 0x100 - 0x40);
        for (int i = 0; i < code.Length; i++)
        {
            W32(0x100 + i * 8, (uint)code[i]);
            W32(0x104 + i * 8, (uint)(code[i] >> 32));
        }
        return new GxpProgram(d, 0x81000000);
    }

    private static string Generate(bool vectorLoads, bool fragment, uint bufferBytes = 256, bool split = false)
    {
        bool savedVector = ShaderGenerator.VectorLoads;
        bool savedSplit = ShaderGenerator.SplitUniformSlots;
        ShaderGenerator.VectorLoads = vectorLoads;
        ShaderGenerator.SplitUniformSlots = split;
        try
        {
            ulong[] code = fragment ? [FourWordLoad] : [FourWordLoad, PositionMoveA, PositionMoveB];
            return ShaderGenerator.Generate(Program(code, fragment, bufferBytes), new ShaderHints([], [], 0x00000000, false, 0)).Glsl;
        }
        finally
        {
            ShaderGenerator.VectorLoads = savedVector;
            ShaderGenerator.SplitUniformSlots = savedSplit;
        }
    }

    private static int Count(string glsl, string pattern) => Regex.Matches(glsl, pattern).Count;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffTheFourWordsAreLoadedOneByOne(bool fragment)
    {
        string glsl = Generate(false, fragment);

        Assert.DoesNotContain("fetch4", glsl);
        Assert.Equal(4, Count(glsl, @"= fetchMemory\(t\d+ \+ (0|4|8|12)\);"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnTheFourWordsComeFromOneAlignedLoadWithTheWordLoadsAsFallback(bool fragment)
    {
        string glsl = Generate(true, fragment);

        // one fetch4 for the four words, its components into the four destination registers
        Assert.Equal(1, Count(glsl, @"vec4 t\d+ = fetch4\(t\d+ \+ 0\);"));
        Assert.Equal(0, Count(glsl, @"= fetchMemory\(t\d+ \+ (0|4|8|12)\);"));
        Assert.Equal(4, Count(glsl, @"= t\d+\.[xyzw];"));
        // the uvec4 view shares the uniform data's binding; the aligned in-range test guards the vector load
        int binding = fragment ? 1 : 0;
        Assert.Contains($"layout(std430, set = 0, binding = {binding}) readonly restrict buffer UniformData {{ uint ub[]; }};", glsl);
        Assert.Contains($"layout(std430, set = 0, binding = {binding}) readonly restrict buffer UniformDataQ {{ uvec4 ubq[]; }};", glsl);
        Assert.Contains("if ((addr & 15) == 0)", glsl);
        Assert.Contains("if (i < uint(ubq.length())) { return uintBitsToFloat(ubq[i]); }", glsl);
        Assert.Contains("return vec4(fetchMemory(addr), fetchMemory(addr + 4), fetchMemory(addr + 8), fetchMemory(addr + 12));", glsl);
    }

    [Fact]
    public void SplitSlotsHaveTheirOwnViewTestedBeforeTheUniformData()
    {
        string glsl = Generate(true, fragment: false, bufferBytes: 8192, split: true);

        Assert.Contains($"binding = {ShaderGenerator.SplitBinding(true, 0)}) readonly restrict buffer SplitData0Q {{ uvec4 us0q[]; }};", glsl);
        int split = glsl.IndexOf("us0q.length()", StringComparison.Ordinal);
        int uniform = glsl.IndexOf("ubq.length()", StringComparison.Ordinal);
        Assert.True(split > 0 && split < uniform, "fetch4 must test the split slot's flag before the uniform data, as fetchMemory does");
    }

    /// <summary>The load's pointer register in the secondary program (per-draw setup), sa[5].z: secondary-attribute word 22.</summary>
    private const int PointerWord = 22;

    private static string TranslateLoad(bool constantPointer)
    {
        bool saved = ShaderGenerator.VectorLoads;
        ShaderGenerator.VectorLoads = true;
        try
        {
            var translator = new UsseTranslator(Program([FourWordLoad], fragment: true, 256));
            if (constantPointer)
            {
                translator.ConstantPointerWords.Add(PointerWord);
            }

            translator.TranslateProgram([FourWordLoad], true);
            return translator.Body;
        }
        finally
        {
            ShaderGenerator.VectorLoads = saved;
        }
    }

    [Fact]
    public void ALoadThroughAConstantPointerStaysWordLoads()
    {
        // a compile-time address: the Mali driver promotes the word loads to uniform registers (malioc, Killzone's
        // dd6a78762dffd68a varying shader: 30 load/store cycles with them as word loads, 78 as vector loads)
        string constant = TranslateLoad(constantPointer: true);
        string computed = TranslateLoad(constantPointer: false);

        Assert.False(constant.Contains("fetch4"), constant);
        Assert.Equal(4, Count(constant, @"fetchMemory\(t\d+ \+ (0|4|8|12)\)"));
        Assert.Contains("fetch4", computed);
    }

    [ShadercTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TheVectorLoadsCompile(bool fragment, bool split)
    {
        using var compiler = new ShaderCompiler(null);
        string glsl = Generate(true, fragment, split ? 8192u : 256u, split);

        Assert.NotNull(compiler.Compile(glsl, !fragment, out string? error));
        Assert.Null(error);
    }
}
