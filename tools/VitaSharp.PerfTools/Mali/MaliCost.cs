using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.PerfTools.Mali;

/// <summary>
/// Estimates the GPU time of replayed frames on a Mali GPU. Every draw is counted by the renderer (vertices from the
/// dump, fragments from a precise occlusion query: VulkanRenderer.DrawCost.cs); the GLSL the translator generated for
/// its shaders goes through Arm's Mali Offline Compiler (malioc), cached per shader; a draw costs fragments x the
/// fragment shader's bound cycles + vertices x the vertex shader's (position + varying variants), spread over the GPU's
/// cores at its clock, plus the frame's tile load/store bytes over the memory bandwidth. The fp16 what-if recompiles
/// the shaders with the GXP's half-precision values (and, as an upper bound, every fragment float) relaxed to mediump.
/// See docs/notes/mobile-gpu.md ("Mali cost estimator").
/// </summary>
internal sealed class MaliCost(MaliCost.Settings s)
{
    public sealed record Settings(string Core, string Malioc, string CacheDir, string OutDir, double Mhz, double Cores, double Gbps, double Scale,
        long FirstFrame, long LastFrame, bool Fp16WhatIf);

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;
    private static readonly string[] s_units = ["arith_total", "load_store", "varying", "texture"];
    private static readonly string[] s_unitNames = ["A", "LS", "V", "T"];

    /// <summary>malioc result of one shader (vertex: the position and varying variants summed).</summary>
    internal sealed record Stats(double[] Cycles, int Bound, bool Spills, int Occupancy, int WorkRegisters, int Fp16, string? Error)
    {
        public double BoundCycles => Bound < 0 ? 0 : Cycles[Bound];
    }

    private readonly string _cacheDir = Path.Combine(s.CacheDir, "malioc", s.Core);
    private readonly List<DrawCostRecord> _draws = new();
    private IReadOnlyDictionary<(ulong Hash, ulong Hint, bool Vertex), string> _glsl = new Dictionary<(ulong, ulong, bool), string>();
    private IReadOnlyDictionary<long, (long LoadBytes, long StoreBytes)> _tile = new Dictionary<long, (long, long)>();
    private IReadOnlyList<PassCostRecord> _passes = [];
    private IReadOnlyList<CopyCostRecord> _copies = [];

    public static string? FindMalioc(string? opt)
    {
        foreach (string? c in new[] { opt, Environment.GetEnvironmentVariable(EnvVars.Malioc) })
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c))
            {
                return c;
            }
        }

        string exe = OperatingSystem.IsWindows() ? "malioc.exe" : "malioc";
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, exe)))
            {
                return Path.Combine(dir, exe);
            }
        }

        string arm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Arm");
        if (Directory.Exists(arm))
        {
            foreach (string? d in Directory.GetDirectories(arm, "Arm Performance Studio*").OrderDescending())
            {
                if (File.Exists(Path.Combine(d, "mali_offline_compiler", exe)))
                {
                    return Path.Combine(d, "mali_offline_compiler", exe);
                }
            }
        }

        return null;
    }

    /// <summary>The renderer's sink (GPU thread, after each frame's fence).</summary>
    public void Add(IReadOnlyList<DrawCostRecord> draws)
    {
        lock (_draws)
        {
            foreach (DrawCostRecord drawCostRecord in draws)
            {
                if (drawCostRecord.Present >= s.FirstFrame && drawCostRecord.Present <= s.LastFrame)
                {
                    _draws.Add(drawCostRecord);
                }
            }
        }
    }

    /// <summary>At the end of the replay, before the renderer is disposed.</summary>
    public void Capture(VulkanRenderer r)
    {
        _glsl = new Dictionary<(ulong, ulong, bool), string>(r.DrawCostShaders);
        _tile = new Dictionary<long, (long, long)>(r.DrawCostTileBytes);
        _passes = r.DrawCostPasses.Where(p => p.Present >= s.FirstFrame && p.Present <= s.LastFrame).ToList();
        _copies = r.DrawCostCopies.Where(c => c.Present >= s.FirstFrame && c.Present <= s.LastFrame).ToList();
    }

    // =========================================================== GLSL variants (what-if)

    /// <summary>Upper bound: every float of a fragment shader mediump (would break 32-bit values; cost only).</summary>
    internal static string RelaxAll(string glsl)
    {
        int nl = glsl.IndexOf('\n');
        return nl < 0 ? glsl : glsl[..(nl + 1)] + "precision mediump float;\n" + glsl[(nl + 1)..];
    }

    // =========================================================== malioc

    private readonly ConcurrentDictionary<string, Lazy<Stats>> _compiled = new();

    private Stats Compile(ulong hash, ulong hint, bool vertex, string variant, string glsl)
    {
        if (glsl.Length == 0)
        {
            return new Stats(new double[4], -1, false, 100, 0, 0, "no GLSL");
        }
        // the GLSL's own hash in the name: a generator or what-if change compiles again
        string textHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(glsl)))[..8];
        string name = $"{hash:x16}_{hint:x16}{(variant.Length > 0 ? "_" + variant : "")}_{textHash}";
        // one malioc run per name (two workers may ask for the same shader at once)
        return _compiled.GetOrAdd(name, n => new Lazy<Stats>(() => CompileOnce(n, vertex, glsl))).Value;
    }

    private Stats CompileOnce(string name, bool vertex, string glsl)
    {
        string src = Path.Combine(_cacheDir, name + (vertex ? ".vert" : ".frag")), json = Path.Combine(_cacheDir, name + ".json");
        if (File.Exists(json))
        {
            return Parse(File.ReadAllText(json));
        }

        File.WriteAllText(src, glsl);
        var psi = new ProcessStartInfo(s.Malioc) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string? a in new[] { "-c", s.Core, "--vulkan", "--format", "json", src })
        {
            psi.ArgumentList.Add(a);
        }
        // malioc writes its SPIR-V to a fixed name under %TEMP%\moc-temp: concurrent runs need their own TEMP
        string tmp = Path.Combine(_cacheDir, "tmp", name);
        Directory.CreateDirectory(tmp);
        psi.Environment["TEMP"] = psi.Environment["TMP"] = psi.Environment["TMPDIR"] = tmp;
        string text;
        using (Process process = Process.Start(psi)!)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            text = stdout.Result;
            if (!text.TrimStart().StartsWith('{'))
            {
                return new Stats(new double[4], -1, false, 100, 0, 0, stderr.Trim().Split('\n').LastOrDefault()?.Trim() is { Length: > 0 } msg ? msg : $"malioc exit code {process.ExitCode}");
            }
        }
        try
        {
            Directory.Delete(tmp, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (!text.Contains("\"name\":\"error\""))
        {
            File.WriteAllText(json, text); // errors are not cached: the next run retries
        }

        return Parse(text);
    }

    internal static Stats Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        double[] cycles = new double[4];
        bool spills = false;
        int occupancy = 100, regs = 0, fp16 = 0;
        string? error = null;
        foreach (JsonElement sh in doc.RootElement.GetProperty("shaders").EnumerateArray())
        {
            if (sh.TryGetProperty("errors", out JsonElement errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
            {
                error = errs[0].ToString();
            }

            if (!sh.TryGetProperty("variants", out JsonElement variants))
            {
                continue;
            }

            foreach (JsonElement v in variants.EnumerateArray())
            {
                JsonElement perf = v.GetProperty("performance");
                var names = perf.GetProperty("pipelines").EnumerateArray().Select(e => e.GetString()).ToList();
                // longest path per invocation (loops: total)
                JsonElement path = perf.TryGetProperty("longest_path_cycles", out JsonElement lp) && lp.TryGetProperty("cycle_count", out JsonElement lc) && lc.ValueKind == JsonValueKind.Array && lc.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number)
                    ? lc : perf.GetProperty("total_cycles").GetProperty("cycle_count");
                double[] vals = path.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToArray();
                for (int u = 0; u < s_units.Length; u++)
                {
                    if (names.IndexOf(s_units[u]) is var i and >= 0 && i < vals.Length)
                    {
                        cycles[u] += vals[i];
                    }
                }

                foreach (JsonElement prop in v.GetProperty("properties").EnumerateArray())
                {
                    JsonElement val = prop.GetProperty("value");
                    switch (prop.GetProperty("name").GetString())
                    {
                        case "has_stack_spilling" when val.ValueKind == JsonValueKind.True:
                            spills = true;
                            break;
                        case "thread_occupancy" when val.ValueKind == JsonValueKind.Number:
                            occupancy = Math.Min(occupancy, val.GetInt32());
                            break;
                        case "work_registers_used" when val.ValueKind == JsonValueKind.Number:
                            regs = Math.Max(regs, val.GetInt32());
                            break;
                        case "fp16_arithmetic" when val.ValueKind == JsonValueKind.Number:
                            fp16 = Math.Max(fp16, val.GetInt32());
                            break;
                    }
                }
            }
        }
        int bound = -1;
        for (int u = 0; u < 4; u++)
        {
            if (bound < 0 || cycles[u] > cycles[bound])
            {
                bound = u;
            }
        }

        if (cycles.All(c => c == 0))
        {
            bound = -1;
        }

        return new Stats(cycles, bound, spills, occupancy, regs, fp16, error);
    }

    // =========================================================== report

    private sealed record DrawCost(DrawCostRecord D, double VsMs, double FsMs, Stats Vs, Stats Fs)
    {
        public double Ms => VsMs + FsMs;
    }

    public void Report()
    {
        List<DrawCostRecord> draws;
        lock (_draws)
        {
            draws = _draws.ToList();
        }

        Directory.CreateDirectory(_cacheDir);
        var sb = new StringBuilder();
        sb.AppendLine($"Mali cost estimate: {s.Core}, {s.Cores} cores at {s.Mhz} MHz, {s.Gbps} GB/s for tile load/store, scale {s.Scale}; malioc {s.Malioc}");
        sb.AppendLine("model: draw = fragments (passing samples / samples per pixel) x fragment bound cycles + vertices (indices x instances) x vertex bound cycles (position + varying), / (cores x clock); frame = draws + tile bytes / bandwidth");
        if (draws.Count == 0)
        {
            sb.AppendLine("no draws recorded");
            Write(sb);
            return;
        }

        var keys = draws.SelectMany(d => new[] { (d.VsHash, d.VsHint, true), (d.FsHash, d.FsHint, false) }).Distinct().ToList();
        string[] variants = s.Fp16WhatIf ? ["", "half", "allmp"] : [""];
        var stats = new ConcurrentDictionary<(ulong, ulong, bool, string), Stats>();
        var sw = Stopwatch.StartNew();
        // two malioc processes at a time (CPU cap)
        Parallel.ForEach(keys.SelectMany(k => variants.Where(v => v != "allmp" || !k.Item3).Select(v => (k, v))), new ParallelOptions { MaxDegreeOfParallelism = 2 }, kv =>
        {
            ((ulong h, ulong hint, bool vertex), string? v) = kv;
            string glsl = _glsl.GetValueOrDefault((h, hint, vertex)) ?? "";
            // Mali Bifrost/Valhall has no clip distances (the device path is FarPlaneDiscard) and malioc crashes on them
            if (vertex)
            {
                glsl = Regex.Replace(glsl, @"^\s*gl_ClipDistance\[\d\] = [^;]*;\r?\n", "", RegexOptions.Multiline);
            }

            string src = v switch
            {
                "half" => VitaSharp.Core.Gxm.Shader.ShaderGenerator.RelaxHalf(glsl),
                "allmp" => RelaxAll(glsl),
                _ => glsl
            };
            // a variant identical to the base shader compiles under the base name
            stats[(h, hint, vertex, v)] = Compile(h, hint, vertex, src == glsl ? "" : v, src);
        });
        Stats S(ulong h, ulong hint, bool vertex, string v) => stats.TryGetValue((h, hint, vertex, v), out Stats? st) ? st : stats[(h, hint, vertex, "")];
        sb.AppendLine($"{keys.Count} shaders ({keys.Count(k => k.Item3)} vertex, {keys.Count(k => !k.Item3)} fragment), malioc {sw.Elapsed.TotalSeconds:F1} s (cached in {_cacheDir})");
        foreach (((ulong, ulong, bool) k, Stats st) e in keys.Select(k => (k, st: S(k.Item1, k.Item2, k.Item3, ""))).Where(x => x.st.Error != null).Take(10))
        {
            sb.AppendLine($"  malioc failed on {e.k.Item1:x16}/{e.k.Item2:x16} {(e.k.Item3 ? "vert" : "frag")}: {e.st.Error}");
        }

        double cyclesPerMs = s.Cores * s.Mhz * 1000 / s.Scale;
        DrawCost Cost(DrawCostRecord drawCostRecord, string v)
        {
            Stats vs = S(drawCostRecord.VsHash, drawCostRecord.VsHint, true, v == "allmp" ? "" : v);
            Stats fs = S(drawCostRecord.FsHash, drawCostRecord.FsHint, false, v);
            // unmeasured (a programmable-blending draw split into pieces with a visibility test): a quarter of the target
            double frags = drawCostRecord.Samples < 0 ? (double)drawCostRecord.RtW * drawCostRecord.RtH * 0.25 : (double)drawCostRecord.Samples / Math.Max(1, drawCostRecord.SampleCount);
            return new DrawCost(drawCostRecord, drawCostRecord.Vertices * vs.BoundCycles / cyclesPerMs, frags * fs.BoundCycles / cyclesPerMs, vs, fs);
        }
        double TileMs(long present) => _tile.TryGetValue(present, out (long LoadBytes, long StoreBytes) t) ? (t.LoadBytes + t.StoreBytes) / (s.Gbps * 1e6) : 0;

        var frames = draws.GroupBy(d => d.Present).OrderBy(g => g.Key).ToList();
        sb.AppendLine();
        sb.AppendLine("frame  draws  unmeasured  vertices   fragments   vertex ms  fragment ms  tile MB (load/store)  tile ms  Mali ms" + (s.Fp16WhatIf ? "  | fp16 half: ms (saving)  | all-fragment mediump: ms" : ""));
        var totals = new List<(long Present, double Ms, double Half, double All)>();
        foreach (IGrouping<long, DrawCostRecord>? g in frames)
        {
            var c = g.Select(d => Cost(d, "")).ToList();
            double vms = c.Sum(x => x.VsMs), fms = c.Sum(x => x.FsMs), tms = TileMs(g.Key);
            double ms = vms + fms + tms;
            double half = s.Fp16WhatIf ? g.Sum(d => Cost(d, "half").Ms) + tms : ms, all = s.Fp16WhatIf ? g.Sum(d => Cost(d, "allmp").Ms) + tms : ms;
            (long LoadBytes, long StoreBytes) t = _tile.GetValueOrDefault(g.Key);
            totals.Add((g.Key, ms, half, all));
            sb.Append(s_inv, $"{g.Key,5} {g.Count(),6} {g.Count(d => d.Samples < 0),11} {g.Sum(d => d.Vertices),9} {g.Sum(d => Math.Max(0, d.Samples) / (double)Math.Max(1, d.SampleCount)),11:F0} {vms,11:F2} {fms,12:F2} {t.LoadBytes / 1048576.0,9:F1}/{t.StoreBytes / 1048576.0,-9:F1} {tms,9:F2} {ms,8:F2}");
            sb.AppendLine(s.Fp16WhatIf ? string.Create(s_inv, $"  | {half,8:F2} ({(ms - half) / Math.Max(ms, 1e-9) * 100,5:F1}%)    | {all,8:F2}") : "");
        }
        double avg = totals.Average(t => t.Ms);
        sb.Append(s_inv, $"average {avg:F2} ms over {totals.Count} frames");
        if (s.Fp16WhatIf)
        {
            double avgHalf = totals.Average(t => t.Half), avgAll = totals.Average(t => t.All);
            sb.Append(s_inv, $"; fp16 on half registers {avgHalf:F2} ms ({(avg - avgHalf) / avg * 100:F1}% less); every fragment float mediump {avgAll:F2} ms ({(avg - avgAll) / avg * 100:F1}% less, upper bound)");
        }
        sb.AppendLine();
        var inRange = totals.Select(t => t.Present).ToHashSet();
        long allLoad = _tile.Where(k => inRange.Contains(k.Key)).Sum(k => k.Value.LoadBytes), allStore = _tile.Where(k => inRange.Contains(k.Key)).Sum(k => k.Value.StoreBytes);
        sb.AppendLine(s_inv, $"tile load/store bytes, all frames: {allLoad / 1048576.0:F1} MB load, {allStore / 1048576.0:F1} MB store ({(allLoad + allStore) / 1048576.0 / totals.Count:F1} MB per frame)");

        // the most expensive frame's top draws
        (long Present, double Ms, double Half, double All) worst = totals.MaxBy(t => t.Ms);
        var top = frames.First(g => g.Key == worst.Present).Select(d => Cost(d, "")).OrderByDescending(x => x.Ms).Take(10).ToList();
        sb.AppendLine();
        sb.AppendLine(s_inv, $"top 10 draws of frame {worst.Present} ({worst.Ms:F2} ms):");
        sb.AppendLine("  draw   ms     %   vertices  fragments  stage bound  vs hash           fs hash           fs cycles A/LS/V/T");
        foreach (DrawCost? x in top)
        {
            bool fsHeavy = x.FsMs >= x.VsMs;
            Stats st = fsHeavy ? x.Fs : x.Vs;
            string bound = st.Bound < 0 ? "-" : s_unitNames[st.Bound];
            sb.AppendLine(s_inv, $"  {x.D.Serial,4} {x.Ms,6:F3} {x.Ms / worst.Ms * 100,4:F1} {x.D.Vertices,9} {(x.D.Samples < 0 ? "?" : (x.D.Samples / Math.Max(1, x.D.SampleCount)).ToString(s_inv)),10}  {(fsHeavy ? "frag" : "vert")} {bound,-5} {x.D.VsHash:x16}  {x.D.FsHash:x16}  {string.Join("/", x.Fs.Cycles.Select(c => c.ToString("F2", s_inv)))}");
        }

        // the draws with the most vertex work in that frame: how many shader invocations the indices really need
        var frameCosts = frames.First(g => g.Key == worst.Present).Select(d => Cost(d, "")).ToList();
        long heavyScene = frameCosts.GroupBy(x => x.D.Scene).OrderByDescending(g => g.Sum(x => x.VsMs)).First().Key;
        // the heaviest draws, then the lightest of the scene with the most vertex work (a draw's fixed cost on the GPU)
        var vtop = frameCosts.OrderByDescending(x => x.VsMs).Take(20)
            .Concat(frameCosts.Where(x => x.D.Scene == heavyScene).OrderBy(x => x.D.Vertices).Take(8)).ToList();
        string[] prims = ["tris", "lines", "points", "strip", "fan", "edges"];
        sb.AppendLine();
        sb.AppendLine(s_inv, $"vertex work of frame {worst.Present}: the 20 draws with the most (indices = vertex invocations the model counts; unique = distinct indices; fifo32 = invocations a 32-entry post-transform cache leaves):");
        sb.AppendLine("  draw  scene  prim   idx  indices   unique    range   fifo32  idx/uniq  vertex ms  ms if unique  vs hash           vs cycles");
        foreach (DrawCost x in vtop)
        {
            DrawCostRecord d = x.D;
            double perVertex = d.Vertices > 0 ? x.VsMs / d.Vertices : 0;
            string prim = d.PrimitiveType < prims.Length ? prims[d.PrimitiveType] : d.PrimitiveType.ToString(s_inv);
            sb.AppendLine(s_inv, $"  {d.Serial,4} {d.Scene,6} {prim,-6} {(d.Index32 ? "32" : "16"),3} {d.Vertices,8} {d.UniqueVertices,8} {d.IndexRange,8} {d.CacheMisses32,8} {(double)d.Vertices / Math.Max(1, d.UniqueVertices),9:F2} {x.VsMs,10:F3} {perVertex * d.UniqueVertices,13:F3}  {d.VsHash:x16}  {x.Vs.BoundCycles,6:F1}");
        }

        sb.AppendLine(s_inv, $"  (the last 8 rows: the draws with the fewest vertices of scene {heavyScene}, {frameCosts.Count(x => x.D.Scene == heavyScene)} draws, {frameCosts.Where(x => x.D.Scene == heavyScene).Sum(x => x.VsMs):F2} ms of vertex work in the model)");
        long fIdx = 0, fUnique = 0, fMiss = 0;
        foreach (DrawCostRecord d in frames.First(g => g.Key == worst.Present))
        {
            fIdx += d.Vertices;
            fUnique += d.UniqueVertices;
            fMiss += d.CacheMisses32;
        }

        sb.AppendLine(s_inv, $"  frame {worst.Present}: {fIdx} indices x instances, {fUnique} distinct indices per draw summed ({(double)fIdx / Math.Max(1, fUnique):F2}x), {fMiss} misses of a 32-entry FIFO cache ({(double)fIdx / Math.Max(1, fMiss):F2}x)");

        // shaders that spill or run below full occupancy, weighted by their share of the estimate
        var share = new Dictionary<(ulong, ulong, bool), double>();
        foreach (DrawCost? x in draws.Select(d => Cost(d, "")))
        {
            share[(x.D.VsHash, x.D.VsHint, true)] = share.GetValueOrDefault((x.D.VsHash, x.D.VsHint, true)) + x.VsMs;
            share[(x.D.FsHash, x.D.FsHint, false)] = share.GetValueOrDefault((x.D.FsHash, x.D.FsHint, false)) + x.FsMs;
        }
        var bad = keys.Select(k => (k, st: S(k.Item1, k.Item2, k.Item3, ""))).Where(x => x.st.Spills || x.st.Occupancy < 100).OrderByDescending(x => share.GetValueOrDefault(x.k)).ToList();
        sb.AppendLine();
        sb.AppendLine($"shaders that spill or run at reduced occupancy: {bad.Count} of {keys.Count}");
        foreach (((ulong, ulong, bool) k, Stats? st) in bad.Take(20))
        {
            sb.AppendLine(s_inv, $"  {k.Item1:x16}/{k.Item2:x16} {(k.Item3 ? "vert" : "frag")}: {(st.Spills ? "spills, " : "")}occupancy {st.Occupancy}%, {st.WorkRegisters} work registers, {share.GetValueOrDefault(k) / totals.Count:F3} ms per frame");
        }

        ReportScenes(sb, draws.Select(d => Cost(d, "")).ToList(), totals.Count);

        if (s.Fp16WhatIf)
        {
            sb.AppendLine($"fp16 what-if: {keys.Count(k => S(k.Item1, k.Item2, k.Item3, "half").Fp16 > S(k.Item1, k.Item2, k.Item3, "").Fp16)} of {keys.Count} shaders gain 16-bit arithmetic from the half-register relaxation");
        }

        Write(sb);
    }

    private sealed class SurfaceRow(string name)
    {
        public readonly string Name = name;
        public long Scenes, Renderings, Draws, LoadBytes, StoreBytes, ResolveBytes, CopyBytes;
        public double VertexMs, FragmentMs, Fragments, Pixels;
    }

    /// <summary>The colour surface a scene renders to (address, size, format, samples), the key that groups scenes across frames.</summary>
    private static string SurfaceName(List<DrawCost> sceneDraws, List<PassCostRecord> scenePasses)
    {
        PassCostRecord? pass = null;
        foreach (PassCostRecord p in scenePasses)
        {
            if (p.HasColor)
            {
                pass = p;
                break;
            }
        }

        uint width = sceneDraws.Count > 0 ? sceneDraws[0].D.RtW : pass?.Width ?? 0;
        uint height = sceneDraws.Count > 0 ? sceneDraws[0].D.RtH : pass?.Height ?? 0;
        if (pass is not { } colorPass)
        {
            return $"depth-only {width}x{height}";
        }

        return $"0x{colorPass.RtAddress:X8} {width}x{height} {colorPass.ColorFormat} {colorPass.Samples}x";
    }

    /// <summary>
    /// Where the estimate goes: scenes grouped by colour surface and averaged per frame (draws, overdraw, tile load /
    /// store / resolve bytes, transfers recorded during the scene), the most expensive first, then the transfers by the
    /// renderer method that recorded them.
    /// </summary>
    private void ReportScenes(StringBuilder sb, List<DrawCost> costs, int frameCount)
    {
        double bytesPerMs = s.Gbps * 1e6;
        double frames = Math.Max(1, frameCount);
        var drawsByScene = costs.GroupBy(c => (c.D.Present, c.D.Scene)).ToDictionary(g => g.Key, g => g.ToList());
        var passesByScene = _passes.GroupBy(p => (p.Present, p.Scene)).ToDictionary(g => g.Key, g => g.ToList());
        var copiesByScene = _copies.GroupBy(c => (c.Present, c.Scene)).ToDictionary(g => g.Key, g => g.Sum(c => c.Bytes));
        var rows = new Dictionary<string, SurfaceRow>();
        foreach ((long Present, long Scene) key in drawsByScene.Keys.Union(passesByScene.Keys).Union(copiesByScene.Keys))
        {
            List<DrawCost> sceneDraws = drawsByScene.GetValueOrDefault(key) ?? [];
            List<PassCostRecord> scenePasses = passesByScene.GetValueOrDefault(key) ?? [];
            string name = sceneDraws.Count == 0 && scenePasses.Count == 0 ? "transfers outside scenes" : SurfaceName(sceneDraws, scenePasses);
            if (!rows.TryGetValue(name, out SurfaceRow? row))
            {
                row = new SurfaceRow(name);
                rows[name] = row;
            }

            row.Scenes++;
            row.Renderings += scenePasses.Count;
            row.Draws += sceneDraws.Count;
            double pixels = 0;
            foreach (DrawCost c in sceneDraws)
            {
                row.VertexMs += c.VsMs;
                row.FragmentMs += c.FsMs;
                row.Fragments += c.D.Samples < 0 ? (double)c.D.RtW * c.D.RtH * 0.25 : (double)c.D.Samples / Math.Max(1, c.D.SampleCount);
                pixels = Math.Max(pixels, (double)c.D.RtW * c.D.RtH);
            }

            foreach (PassCostRecord p in scenePasses)
            {
                row.LoadBytes += p.LoadBytes;
                row.StoreBytes += p.StoreBytes;
                row.ResolveBytes += p.ResolveBytes;
            }

            row.Pixels += pixels;
            row.CopyBytes += copiesByScene.GetValueOrDefault(key);
        }

        double Total(SurfaceRow r) => r.VertexMs + r.FragmentMs + (r.LoadBytes + r.StoreBytes + r.ResolveBytes + r.CopyBytes) / bytesPerMs;
        var ordered = rows.Values.OrderByDescending(Total).ToList();
        double all = ordered.Sum(Total) / frames;
        sb.AppendLine();
        sb.AppendLine(s_inv, $"per surface, ms per frame (scenes grouped by colour surface; tile = load + store + resolve at {s.Gbps} GB/s; copies = transfers recorded during those scenes); frame with transfers {all:F2} ms:");
        sb.AppendLine("  rank  ms/frame    %  vertex  fragment  tile(L/S/R MB)        copies MB  scenes renderings draws  overdraw  surface");
        int rank = 0;
        foreach (SurfaceRow r in ordered.Take(15))
        {
            rank++;
            double ms = Total(r) / frames;
            double overdraw = r.Pixels > 0 ? r.Fragments / r.Pixels : 0;
            sb.AppendLine(s_inv, $"  {rank,4} {ms,9:F2} {ms / Math.Max(all, 1e-9) * 100,4:F0} {r.VertexMs / frames,7:F2} {r.FragmentMs / frames,9:F2}  {r.LoadBytes / frames / 1048576.0,5:F1}/{r.StoreBytes / frames / 1048576.0,5:F1}/{r.ResolveBytes / frames / 1048576.0,5:F1} {(r.LoadBytes + r.StoreBytes + r.ResolveBytes) / frames / bytesPerMs,6:F2} ms {r.CopyBytes / frames / 1048576.0,8:F1} {r.Scenes / frames,7:F1} {r.Renderings / frames,10:F1} {r.Draws / frames,5:F0} {overdraw,9:F2}  {r.Name}");
        }

        // one frame in recording order: what each scene rendering and transfer moves
        long sample = _passes.Count > 0 ? _passes[_passes.Count / 2].Present : 0;
        sb.AppendLine();
        sb.AppendLine(s_inv, $"renderings and transfers of frame {sample} in recording order (scene, site, area, format x samples, colour L/S, depth L/S, resolve, MB):");
        IOrderedEnumerable<(long Order, string Text)> events = _passes.Where(p => p.Present == sample).Select(p => (p.Order, Text: string.Create(s_inv, $"  s{p.Scene,-6} pass {p.Site,-22} 0x{p.RtAddress:X8} {p.Width}x{p.Height} {p.ColorFormat} x{p.Samples} colour {(p.ColorLoad ? "L" : "-")}{(p.ColorStore ? "S" : "-")} depth {(p.DepthLoad ? "L" : "-")}{(p.DepthStore ? "S" : "-")}{(p.Resolve ? " resolve" : "")} {(p.LoadBytes + p.StoreBytes + p.ResolveBytes) / 1048576.0:F2} MB")))
            .Concat(_copies.Where(c => c.Present == sample).Select(c => (c.Order, Text: string.Create(s_inv, $"  s{c.Scene,-6} copy {c.Site,-22} {c.Bytes / 1048576.0:F2} MB")))).OrderBy(e => e.Order);
        foreach ((long Order, string Text) e in events)
        {
            sb.AppendLine(e.Text);
        }

        sb.AppendLine();
        sb.AppendLine("transfers by renderer site, per frame:");
        foreach (IGrouping<string, CopyCostRecord> g in _copies.GroupBy(c => c.Site).OrderByDescending(g => g.Sum(c => c.Bytes)))
        {
            long bytes = g.Sum(c => c.Bytes);
            sb.AppendLine(s_inv, $"  {g.Key,-36} {g.Count() / frames,7:F1} copies {bytes / frames / 1048576.0,8:F2} MB {bytes / frames / bytesPerMs,7:F2} ms");
        }

        sb.AppendLine();
        sb.AppendLine("renderings by renderer site, per frame:");
        foreach (IGrouping<string, PassCostRecord> g in _passes.GroupBy(p => p.Site).OrderByDescending(g => g.Sum(p => p.LoadBytes + p.StoreBytes + p.ResolveBytes)))
        {
            long bytes = g.Sum(p => p.LoadBytes + p.StoreBytes + p.ResolveBytes);
            sb.AppendLine(s_inv, $"  {g.Key,-36} {g.Count() / frames,7:F1} renderings {bytes / frames / 1048576.0,8:F2} MB {bytes / frames / bytesPerMs,7:F2} ms");
        }
    }

    private void Write(StringBuilder sb)
    {
        string path = Path.Combine(s.OutDir, "mali_cost.txt");
        Directory.CreateDirectory(s.OutDir);
        File.WriteAllText(path, sb.ToString());
        Console.Write(sb.ToString());
        Console.WriteLine($"Mali cost estimate written to {path}");
    }
}
