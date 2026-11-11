using System.Buffers.Binary;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The USSE translator over every opcode group with pseudo-random operand fields, in vertex and fragment programs: a
/// game can hand the GPU any program, so the translation must never throw (a throw on the GPU worker drops the
/// draw), and the GLSL it writes must compile with shaderc, which the renderer feeds it to.
/// </summary>
[Collection("shadergen")]
public sealed class ShaderFuzzTests
{
    private static GxpProgram Program(ulong[] code, bool fragment)
    {
        byte[] d = new byte[0x100 + code.Length * 8];
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        if (fragment)
        {
            W32(0x14, GxpProgram.FlagFragment);
        }

        W32(0x3C, (uint)code.Length);
        W32(0x40, 0x100 - 0x40);
        for (int i = 0; i < code.Length; i++)
        {
            W32(0x100 + i * 8, (uint)code[i]);
            W32(0x104 + i * 8, (uint)(code[i] >> 32));
        }
        return new GxpProgram(d, 0x81000000);
    }

    /// <summary>12 programs per opcode (bits 59-63) and stage, from a fixed seed: (name, instruction word, GLSL or the
    /// exception the translation threw).</summary>
    private static IEnumerable<(string Name, ulong Word, bool Fragment, string? Glsl, Exception? Error)> Programs()
    {
        var rng = new Random(20261110);
        for (uint opcode = 0; opcode < 32; opcode++)
        {
            for (int n = 0; n < 12; n++)
            {
                ulong low = (ulong)(uint)rng.Next() | ((ulong)(uint)rng.Next(1 << 27) << 32);
                ulong word = ((ulong)opcode << 59) | (low & ((1UL << 59) - 1));
                foreach (bool fragment in new[] { false, true })
                {
                    // the instruction, then the plain moves that write the position (vertex) as in the other tests
                    ulong[] code = fragment ? [word] : [word, 0x6081000460600003, 0x50C9000A20200180];
                    string? glsl = null;
                    Exception? error = null;
                    try
                    {
                        glsl = ShaderGenerator.Generate(Program(code, fragment), new ShaderHints([], [], 0x00000000, false, 0)).Glsl;
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                    yield return ($"{(fragment ? "fs" : "vs")} 0x{word:X16}", word, fragment, glsl, error);
                }
            }
        }
    }

    [Fact]
    public void EveryOpcodeTranslatesWithoutThrowing()
    {
        var all = Programs().ToList();
        var throws = all.Where(p => p.Error != null).Select(p => $"{p.Name}: {p.Error!.GetType().Name}: {p.Error.Message}").ToList();
        Assert.True(throws.Count == 0, $"{throws.Count} of {all.Count} translations threw: " + string.Join(" | ", throws.Take(15)));
    }

    private static GeneratedShader Translate(ulong word, bool fragment) =>
        ShaderGenerator.Generate(Program(fragment ? [word] : [word, 0x6081000460600003, 0x50C9000A20200180], fragment),
            new ShaderHints([], [], 0x00000000, false, 0));

    /// <summary>The fuzz programs that did not compile: a derivative in a vertex program (the USSE takes it across the
    /// pixel quad, which a vertex has none of: 0), DEPTHF in a vertex program (no fragment depth: ignored) and the
    /// index bank past its two registers (i.l / i.h: a write changes nothing, a read gives 0).</summary>
    [Fact]
    public void StageAndRegisterLimitsFollowTheHardware()
    {
        const ulong Dsx = 0x0DBA30DF384131F5, Depthf = 0xFB7092E470237659, Index = 0x1A3B247670E1028A;
        Assert.DoesNotContain("dFd", Translate(Dsx, false).Glsl);
        Assert.Contains("dFd", Translate(Dsx, true).Glsl);
        GeneratedShader v = Translate(Depthf, false);
        Assert.DoesNotContain("gl_FragDepth", v.Glsl);
        Assert.Contains("DEPTHF in a vertex program (ignored)", v.Warnings);
        Assert.Contains("gl_FragDepth", Translate(Depthf, true).Glsl);
        GeneratedShader i = Translate(Index, true);
        Assert.Contains(i.Warnings, w => w.StartsWith("write to index register", StringComparison.Ordinal));
        Assert.DoesNotContain("idx[5]", i.Glsl);
    }

    [ShadercFact]
    public void EveryTranslationCompiles()
    {
        using var compiler = new ShaderCompiler(null);
        var all = Programs().Where(p => p.Glsl != null).ToList();
        var errors = new List<string>();
        foreach ((string Name, ulong Word, bool Fragment, string? Glsl, Exception? Error) p in all)
        {
            if (compiler.Compile(p.Glsl!, !p.Fragment, out string? error) == null)
            {
                errors.Add($"{p.Name}: {error?.Split('\n').FirstOrDefault()}");
            }
        }

        Assert.True(errors.Count == 0, $"{errors.Count} of {all.Count} programs did not compile: " + string.Join(" | ", errors.Take(15)));
    }
}
