using System.Globalization;
using System.Text;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>
/// The persistent shader caches (shader pack, loose .spv files) are keyed by <see cref="ShaderGenerator.GeneratorVersion"/>:
/// a change to the translator that alters the GLSL of an existing program without a bump leaves the old modules in
/// every user's cache, and the emulator keeps running the old translation. This guard translates a fixed set of
/// synthetic programs (every USSE opcode group with operand fields from a fixed seed, in both stages, and the sampling
/// programs with each texture class, surface format and shader variant) and compares the hash of the output with the
/// one recorded for the current version.
/// <para>When it fails: if the GLSL change is intended, bump <see cref="ShaderGenerator.GeneratorVersion"/> and record
/// the new version and hash below (the failure message prints the hash); if it is not intended, the change altered the
/// translation by accident.</para>
/// </summary>
[Collection("shadergen")]
public sealed class ShaderGeneratorVersionTests
{
    /// <summary>The version the hash below was recorded with.</summary>
    private const int RecordedVersion = 33;
    /// <summary>The <see cref="TranslationHash"/> of <see cref="RecordedVersion"/>.</summary>
    private const string RecordedHash = "E3CC1FEC5DAC967B";

    private static GxpProgram CodeProgram(ulong[] code, bool fragment, uint address)
    {
        byte[] d = new byte[0x100 + code.Length * 8];
        void W32(int o, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
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

        return new GxpProgram(d, address);
    }

    /// <summary>The programs and hints translated, in a fixed order.</summary>
    private static IEnumerable<(string Name, GxpProgram Program, ShaderHints Hints)> Cases()
    {
        var rng = new Random(20261119);
        uint address = 0x81000000;
        for (uint opcode = 0; opcode < 32; opcode++)
        {
            for (int n = 0; n < 6; n++)
            {
                ulong low = (ulong)(uint)rng.Next() | ((ulong)(uint)rng.Next(1 << 27) << 32);
                ulong word = ((ulong)opcode << 59) | (low & ((1UL << 59) - 1));
                foreach (bool fragment in new[] { false, true })
                {
                    // the instruction, then the plain moves that write the position (vertex), as in ShaderFuzzTests
                    ulong[] code = fragment ? [word] : [word, 0x6081000460600003, 0x50C9000A20200180];
                    yield return ($"{(fragment ? "fs" : "vs")} 0x{word:X16}", CodeProgram(code, fragment, address += 0x100), new ShaderHints([], [], 0, false, 0));
                }
            }
        }

        var vertex = new GxpProgram(RendererTextureTests.VertexGxp(), 0x82000000);
        foreach (byte attributeFormat in new byte[] { 0, 3, 6, 9 })
        {
            yield return ($"vertex attr {attributeFormat}", vertex, new ShaderHints(new uint[16], [(0u, 4, attributeFormat)], 0));
        }

        var sampling = new GxpProgram(RendererTextureTests.SamplingFragmentGxp(), 0x82000100);
        var cube = new GxpProgram(RendererTextureTests.SamplingFragmentGxp(cube: true), 0x82000200);
        var fetch = new GxpProgram(RendererTextureTests.SamplingFragmentGxp(fragColor: true), 0x82000300);
        foreach (TexBase texBase in Enum.GetValues<TexBase>())
        {
            uint[] formats = new uint[16];
            Array.Fill(formats, 0x0C000000u);
            formats[3] = (uint)texBase;
            yield return ($"texture {texBase}", sampling, new ShaderHints(formats, [], 0));
            yield return ($"cube {texBase}", cube, new ShaderHints(formats, [], 0));
        }

        uint[] plain = new uint[16];
        Array.Fill(plain, 0x0C000000u);
        foreach (uint colorFormat in new uint[] { 0x00000000, 0x01000000, 0x10800000, 0x11000000, 0x41000000, 0x80800000, 0xC1000000 })
        {
            yield return ($"colour 0x{colorFormat:X8}", sampling, new ShaderHints(plain, [], colorFormat));
            yield return ($"fetch 0x{colorFormat:X8}", fetch, new ShaderHints(plain, [], colorFormat));
            yield return ($"fetch input 0x{colorFormat:X8}", fetch, new ShaderHints(plain, [], colorFormat, FetchInputAttachment: true));
            yield return ($"fetch single 0x{colorFormat:X8}", fetch, new ShaderHints(plain, [], colorFormat, FetchInputAttachment: true, FetchInputSingleSample: true));
            yield return ($"tile word 0x{colorFormat:X8}", fetch, new ShaderHints(plain, [], colorFormat, TileWord1: true));
            yield return ($"float bits 0x{colorFormat:X8}", sampling, new ShaderHints(plain, [], colorFormat, FloatBitsOutput: true));
        }
    }

    /// <summary>The GLSL and the metadata the renderer builds pipelines from, of every case (a throw counts by its type:
    /// the translator throwing for a program is part of its output too), hashed.</summary>
    private static string TranslationHash()
    {
        var all = new StringBuilder();
        foreach ((string name, GxpProgram program, ShaderHints hints) in Cases())
        {
            all.Append("== ").Append(name).Append('\n');
            try
            {
                GeneratedShader generated = ShaderGenerator.Generate(program, hints);
                all.Append(generated.Glsl).Append('\n');
                all.Append($"vertex {generated.IsVertex} fragcolor {generated.UsesFragColor} depth {generated.WritesDepth} raw {generated.RawColorBits} tile {generated.TileWord1} uniforms {generated.UniformBytes} textures {string.Join(",", generated.Textures)} inputs {generated.VertexInputs.Count}\n");
            }
            catch (Exception e)
            {
                all.Append("throws ").Append(e.GetType().Name).Append('\n');
            }
        }

        return HostIndependentHash(all.ToString());
    }

    /// <summary>The hash of the translation text with every line break as '\n'. The GLSL takes its line breaks from the
    /// host: AppendLine writes Environment.NewLine ("\r\n" on Windows) and the raw string literals of the generator
    /// keep the line breaks of the checkout (CRLF with core.autocrlf on Windows, LF elsewhere), and neither changes
    /// what the shader compiler builds, so the recorded hash must not see them.</summary>
    internal static string HostIndependentHash(string translation)
    {
        string normalized = translation.ReplaceLineEndings("\n");
        return System.IO.Hashing.XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(normalized)).ToString("X16", CultureInfo.InvariantCulture);
    }

    /// <summary>The same translation written on Windows (CRLF) and on Linux/macOS (LF) hashes the same.</summary>
    [Fact]
    public void HashDoesNotDependOnTheHostLineBreaks()
    {
        const string Lf = "== vs 0x0\nvoid main() {\n    gl_Position = vec4(0.0);\n}\n";
        string crlf = Lf.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.Equal(HostIndependentHash(Lf), HostIndependentHash(crlf));
        Assert.Equal(HostIndependentHash(Lf), HostIndependentHash(Lf.Replace("\n", Environment.NewLine, StringComparison.Ordinal)));
    }

    [Fact]
    public void GlslChangesOnlyWithAGeneratorVersionBump()
    {
        Assert.False(ShaderGenerator.DebugOverridesActive, "VITASHARP_DEBUG_FS / debug overrides change the GLSL: run without them");
        string hash = TranslationHash();
        Assert.True(ShaderGenerator.GeneratorVersion == RecordedVersion,
            $"GeneratorVersion is {ShaderGenerator.GeneratorVersion}, the hash was recorded for {RecordedVersion}: record RecordedVersion = {ShaderGenerator.GeneratorVersion} and RecordedHash = \"{hash}\"");
        Assert.True(hash == RecordedHash,
            $"the GLSL of the synthetic programs changed (hash {hash}, recorded {RecordedHash} for version {RecordedVersion}) without a GeneratorVersion bump: "
            + $"bump ShaderGenerator.GeneratorVersion and record its new value and RecordedHash = \"{hash}\" (if the change is intended)");
    }

    /// <summary>The translation does not depend on the host's culture (the caches are shared by machines and the
    /// recorded hash by every CI runner).</summary>
    [Fact]
    public void TranslationIsTheSameInEveryCulture()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = TranslationHash();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            string portuguese = TranslationHash();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(invariant, portuguese);
            Assert.Equal(invariant, TranslationHash());
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
