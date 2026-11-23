using System.Text.RegularExpressions;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>
/// Every USSE register bank of a generated shader is a GLSL private array, and a private variable without an initializer
/// is undefined: a driver may hand out whatever its registers held. Programs read registers nothing loaded (secondary
/// attributes no container, literal or secondary program fills, predicates before a test, outputs before a write) and
/// later overwrite the result, but NVIDIA's compiler let that garbage reach unrelated results (The Amazing Spider-Man's
/// pause menu: skinned glass shards drawn somewhere else on every run). So every bank starts at zero, in both stages.
/// </summary>
[Collection("shadergen")]
public sealed class ShaderRegisterInitTests
{
    /// <summary>The register banks the generator declares (primary/secondary attributes, temporaries, outputs, internal
    /// registers, predicates, index registers).</summary>
    private static readonly string[] s_banks = ["pa", "sa", "r", "o", "fi", "p", "idx"];

    /// <summary>Whether <paramref name="bank"/> starts at a defined value: an initializer at its declaration, or a
    /// zeroing of the whole array before the program's first instruction.</summary>
    private static bool Initialized(string glsl, string bank)
    {
        Match declaration = Regex.Match(glsl, $@"^(vec4|bool|int) {bank}\[(\d+)\]( = [^;]+)?;", RegexOptions.Multiline);
        Assert.True(declaration.Success, $"no declaration of {bank}");
        if (declaration.Groups[3].Success)
        {
            return true;
        }

        int main = glsl.IndexOf("void main() {", StringComparison.Ordinal);
        int firstInstruction = glsl.IndexOf("    // 0: ", main, StringComparison.Ordinal);
        string preamble = firstInstruction < 0 ? glsl[main..] : glsl[main..firstInstruction];
        if (preamble.Contains($"for (int k = 0; k < {bank}.length(); k++) {bank}[k] = ", StringComparison.Ordinal))
        {
            return true;
        }

        int count = int.Parse(declaration.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < count; i++)
        {
            if (!preamble.Contains($"{bank}[{i}] = ", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public void EveryRegisterBankStartsDefinedInBothStages()
    {
        int vertex = 0, fragment = 0;
        var undefined = new List<string>();
        foreach ((string name, GxpProgram program, ShaderHints hints) in ShaderGeneratorVersionTests.Cases())
        {
            GeneratedShader generated;
            try
            {
                generated = ShaderGenerator.Generate(program, hints);
            }
            catch (Exception)
            {
                continue; // programs the translator rejects produce no shader
            }

            if (generated.IsVertex)
            {
                vertex++;
            }
            else
            {
                fragment++;
            }

            foreach (string bank in s_banks)
            {
                if (!Initialized(generated.Glsl, bank))
                {
                    undefined.Add($"{name}: {bank}");
                }
            }
        }

        Assert.True(vertex > 0 && fragment > 0, $"cases: {vertex} vertex, {fragment} fragment");
        Assert.True(undefined.Count == 0, $"{undefined.Count} register banks start undefined, e.g. {string.Join("; ", undefined.Take(8))}");
    }

    /// <summary>The pattern of the shard program: a secondary attribute past every container, literal and secondary
    /// program write is read; it reads zero, like the registers loaded with zero, not what the GPU held.</summary>
    [Fact]
    public void AnUnloadedSecondaryAttributeReadsZero()
    {
        GxpProgram program = new(RendererTextureTests.VertexGxp(), 0x82000000);
        string glsl = ShaderGenerator.Generate(program, new ShaderHints(new uint[16], [(0u, 4, 0)], 0)).Glsl;
        Match declaration = Regex.Match(glsl, @"^vec4 sa\[(\d+)\] = vec4\[\1\]\(((vec4\(0\.0\)(, )?)+)\);", RegexOptions.Multiline);
        Assert.True(declaration.Success, "sa is declared without a zero initializer");
        int count = int.Parse(declaration.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(count, Regex.Matches(declaration.Groups[2].Value, @"vec4\(0\.0\)").Count);
    }
}
