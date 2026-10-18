using System.Buffers.Binary;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Package;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>Finds every GXP program inside a title's files and checks it translates to valid SPIR-V.</summary>
static class ShaderTest
{
    public static int Run(string appDir, string[] args)
    {
        string outDir = Path.Combine(Program.DataRoot, "shadertest");
        Directory.CreateDirectory(outDir);
        bool dumpAll = args.Contains("--dump");
        byte[]? klic = null;
        var work = Path.Combine(appDir, "sce_sys", "package", "work.bin");
        if (File.Exists(work)) klic = PkgInstaller.ReadKlicensee(work);

        using var compiler = new ShaderCompiler(null);
        var seen = new HashSet<ulong>();
        int ok = 0, fail = 0, total = 0;
        var warnings = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(file);
                if (data.Length > 4 && data[0] == 'S' && data[1] == 'C' && data[2] == 'E' && data[3] == 0)
                    data = SelfDecryptor.ToElf(data, klic);
            }
            catch { continue; }
            var span = data.AsSpan();
            for (int i = 0; i + 64 < data.Length; i += 4)
            {
                if (data[i] != 'G' || data[i + 1] != 'X' || data[i + 2] != 'P' || data[i + 3] != 0) continue;
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(i + 8)..]);
                if (size < 0x40 || i + size > data.Length) continue;
                var prog = new GxpProgram(span.Slice(i, size).ToArray(), 0);
                if (!seen.Add(prog.Hash)) continue;
                total++;
                string name = $"{prog.Hash:x16}.{(prog.IsVertex ? "vert" : "frag")}";
                GeneratedShader gen;
                try
                {
                    gen = ShaderGenerator.Generate(prog, new ShaderHints(Enumerable.Repeat(0x0C000000u, 16).ToArray(), [], 0));
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.WriteLine($"{name}: translate exception {ex.GetType().Name}: {ex.Message}");
                    File.WriteAllBytes(Path.Combine(outDir, name + ".gxp"), prog.Data);
                    continue;
                }
                foreach (var w in gen.Warnings) warnings[w] = warnings.GetValueOrDefault(w) + 1;
                var spv = compiler.Compile(gen.Glsl, gen.IsVertex, out var err);
                if (spv == null)
                {
                    fail++;
                    Console.WriteLine($"{name} ({Path.GetFileName(file)}): {err?.Split('\n').FirstOrDefault()}");
                    File.WriteAllText(Path.Combine(outDir, name), gen.Glsl + "\n/*\n" + err + "\n*/\n");
                    File.WriteAllBytes(Path.Combine(outDir, name + ".gxp"), prog.Data);
                }
                else
                {
                    ok++;
                    if (dumpAll) File.WriteAllText(Path.Combine(outDir, name), gen.Glsl);
                }
            }
        }
        Console.WriteLine($"GXP programs: {total}, compiled {ok}, failed {fail}");
        foreach (var (w, n) in warnings.OrderByDescending(kv => kv.Value).Take(25)) Console.WriteLine($"  {n,5}x {w}");
        return fail == 0 ? 0 : 2;
    }
}
