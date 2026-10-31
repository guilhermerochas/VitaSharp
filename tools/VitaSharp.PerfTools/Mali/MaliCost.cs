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
sealed class MaliCost(MaliCost.Settings s)
{
    public sealed record Settings(string Core, string Malioc, string CacheDir, string OutDir, double Mhz, double Cores, double Gbps, double Scale,
        long FirstFrame, long LastFrame, bool Fp16WhatIf);

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Units = ["arith_total", "load_store", "varying", "texture"];
    static readonly string[] UnitNames = ["A", "LS", "V", "T"];

    /// <summary>malioc result of one shader (vertex: the position and varying variants summed).</summary>
    internal sealed record Stats(double[] Cycles, int Bound, bool Spills, int Occupancy, int WorkRegisters, int Fp16, string? Error)
    {
        public double BoundCycles => Bound < 0 ? 0 : Cycles[Bound];
    }

    readonly string _cacheDir = Path.Combine(s.CacheDir, "malioc", s.Core);
    readonly List<DrawCostRecord> _draws = new();
    IReadOnlyDictionary<(ulong Hash, ulong Hint, bool Vertex), string> _glsl = new Dictionary<(ulong, ulong, bool), string>();
    IReadOnlyDictionary<long, (long LoadBytes, long StoreBytes)> _tile = new Dictionary<long, (long, long)>();

    public static string? FindMalioc(string? opt)
    {
        foreach (var c in new[] { opt, Environment.GetEnvironmentVariable(EnvVars.Malioc) })
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
        string exe = OperatingSystem.IsWindows() ? "malioc.exe" : "malioc";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
        var arm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Arm");
        if (Directory.Exists(arm))
            foreach (var d in Directory.GetDirectories(arm, "Arm Performance Studio*").OrderDescending())
                if (File.Exists(Path.Combine(d, "mali_offline_compiler", exe))) return Path.Combine(d, "mali_offline_compiler", exe);
        return null;
    }

    /// <summary>The renderer's sink (GPU thread, after each frame's fence).</summary>
    public void Add(IReadOnlyList<DrawCostRecord> draws)
    {
        lock (_draws)
            foreach (var d in draws)
                if (d.Present >= s.FirstFrame && d.Present <= s.LastFrame) _draws.Add(d);
    }

    /// <summary>At the end of the replay, before the renderer is disposed.</summary>
    public void Capture(VulkanRenderer r)
    {
        _glsl = new Dictionary<(ulong, ulong, bool), string>(r.DrawCostShaders);
        _tile = new Dictionary<long, (long, long)>(r.DrawCostTileBytes);
    }

    // =========================================================== GLSL variants (what-if)

    /// <summary>mediump on the values the GXP holds in half registers: the temporaries the translator unpacks them into,
    /// the varyings the GXP loads as F16 (every use a copy into such a temporary) and a half/8-bit colour output.
    /// The real generator is unchanged.</summary>
    internal static string RelaxHalf(string glsl)
    {
        var s = Regex.Replace(glsl, @"(^|[\s;{(])(vec[234]|float) (\w+) = unpackHalf2x16", "$1mediump $2 $3 = unpackHalf2x16", RegexOptions.Multiline);
        s = Regex.Replace(s, @"(^|[\s;{(])(vec[234]|float) (\w+) = (vec[234])\(unpackHalf2x16", "$1mediump $2 $3 = $4(unpackHalf2x16", RegexOptions.Multiline);
        var halfTemps = Regex.Matches(s, @"mediump vec2 (\w+) = unpackHalf2x16").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (Match m in Regex.Matches(s, @"^(layout\([^)]*\) (?:centroid |flat |noperspective )*)in (vec[234]|float) (\w+);", RegexOptions.Multiline).Cast<Match>().ToList())
        {
            string name = m.Groups[3].Value;
            int uses = Regex.Matches(s, $@"(?<![\w.]){Regex.Escape(name)}\b").Count - 1;
            int copies = Regex.Matches(s, $@"(\w+)\.[xy] = {Regex.Escape(name)}\.[xyzw];").Count(c => halfTemps.Contains(c.Groups[1].Value));
            if (uses > 0 && copies == uses) s = s.Replace(m.Value, $"{m.Groups[1].Value}in mediump {m.Groups[2].Value} {name};");
        }
        if (Regex.IsMatch(s, @"^// gxp .* out (F16|U8|C10)", RegexOptions.Multiline))
            s = s.Replace("layout(location = 0) out vec4 out_color;", "layout(location = 0) out mediump vec4 out_color;");
        return s;
    }

    /// <summary>Upper bound: every float of a fragment shader mediump (would break 32-bit values; cost only).</summary>
    internal static string RelaxAll(string glsl)
    {
        int nl = glsl.IndexOf('\n');
        return nl < 0 ? glsl : glsl[..(nl + 1)] + "precision mediump float;\n" + glsl[(nl + 1)..];
    }

    // =========================================================== malioc

    readonly ConcurrentDictionary<string, Lazy<Stats>> _compiled = new();

    Stats Compile(ulong hash, ulong hint, bool vertex, string variant, string glsl)
    {
        if (glsl.Length == 0) return new Stats(new double[4], -1, false, 100, 0, 0, "no GLSL");
        // the GLSL's own hash in the name: a generator or what-if change compiles again
        string textHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(glsl)))[..8];
        string name = $"{hash:x16}_{hint:x16}{(variant.Length > 0 ? "_" + variant : "")}_{textHash}";
        // one malioc run per name (two workers may ask for the same shader at once)
        return _compiled.GetOrAdd(name, n => new Lazy<Stats>(() => CompileOnce(n, vertex, glsl))).Value;
    }

    Stats CompileOnce(string name, bool vertex, string glsl)
    {
        string src = Path.Combine(_cacheDir, name + (vertex ? ".vert" : ".frag")), json = Path.Combine(_cacheDir, name + ".json");
        if (File.Exists(json)) return Parse(File.ReadAllText(json));
        File.WriteAllText(src, glsl);
        var psi = new ProcessStartInfo(s.Malioc) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-c", s.Core, "--vulkan", "--format", "json", src }) psi.ArgumentList.Add(a);
        // malioc writes its SPIR-V to a fixed name under %TEMP%\moc-temp: concurrent runs need their own TEMP
        string tmp = Path.Combine(_cacheDir, "tmp", name);
        Directory.CreateDirectory(tmp);
        psi.Environment["TEMP"] = psi.Environment["TMP"] = psi.Environment["TMPDIR"] = tmp;
        string text;
        using (var p = Process.Start(psi)!)
        {
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            text = stdout.Result;
            if (!text.TrimStart().StartsWith('{'))
                return new Stats(new double[4], -1, false, 100, 0, 0, stderr.Trim().Split('\n').LastOrDefault()?.Trim() is { Length: > 0 } msg ? msg : $"malioc exit code {p.ExitCode}");
        }
        try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (!text.Contains("\"name\":\"error\"")) File.WriteAllText(json, text); // errors are not cached: the next run retries
        return Parse(text);
    }

    internal static Stats Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var cycles = new double[4];
        bool spills = false;
        int occupancy = 100, regs = 0, fp16 = 0;
        string? error = null;
        foreach (var sh in doc.RootElement.GetProperty("shaders").EnumerateArray())
        {
            if (sh.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0) error = errs[0].ToString();
            if (!sh.TryGetProperty("variants", out var variants)) continue;
            foreach (var v in variants.EnumerateArray())
            {
                var perf = v.GetProperty("performance");
                var names = perf.GetProperty("pipelines").EnumerateArray().Select(e => e.GetString()).ToList();
                // longest path per invocation (loops: total)
                var path = perf.TryGetProperty("longest_path_cycles", out var lp) && lp.TryGetProperty("cycle_count", out var lc) && lc.ValueKind == JsonValueKind.Array && lc.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number)
                    ? lc : perf.GetProperty("total_cycles").GetProperty("cycle_count");
                var vals = path.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToArray();
                for (int u = 0; u < Units.Length; u++)
                    if (names.IndexOf(Units[u]) is var i and >= 0 && i < vals.Length) cycles[u] += vals[i];
                foreach (var prop in v.GetProperty("properties").EnumerateArray())
                {
                    var val = prop.GetProperty("value");
                    switch (prop.GetProperty("name").GetString())
                    {
                        case "has_stack_spilling" when val.ValueKind == JsonValueKind.True: spills = true; break;
                        case "thread_occupancy" when val.ValueKind == JsonValueKind.Number: occupancy = Math.Min(occupancy, val.GetInt32()); break;
                        case "work_registers_used" when val.ValueKind == JsonValueKind.Number: regs = Math.Max(regs, val.GetInt32()); break;
                        case "fp16_arithmetic" when val.ValueKind == JsonValueKind.Number: fp16 = Math.Max(fp16, val.GetInt32()); break;
                    }
                }
            }
        }
        int bound = -1;
        for (int u = 0; u < 4; u++) if (bound < 0 || cycles[u] > cycles[bound]) bound = u;
        if (cycles.All(c => c == 0)) bound = -1;
        return new Stats(cycles, bound, spills, occupancy, regs, fp16, error);
    }

    // =========================================================== report

    sealed record DrawCost(DrawCostRecord D, double VsMs, double FsMs, Stats Vs, Stats Fs)
    {
        public double Ms => VsMs + FsMs;
    }

    public void Report()
    {
        List<DrawCostRecord> draws;
        lock (_draws) draws = _draws.ToList();
        Directory.CreateDirectory(_cacheDir);
        var sb = new StringBuilder();
        sb.AppendLine($"Mali cost estimate: {s.Core}, {s.Cores} cores at {s.Mhz} MHz, {s.Gbps} GB/s for tile load/store, scale {s.Scale}; malioc {s.Malioc}");
        sb.AppendLine("model: draw = fragments (passing samples / samples per pixel) x fragment bound cycles + vertices (indices x instances) x vertex bound cycles (position + varying), / (cores x clock); frame = draws + tile bytes / bandwidth");
        if (draws.Count == 0) { sb.AppendLine("no draws recorded"); Write(sb); return; }

        var keys = draws.SelectMany(d => new[] { (d.VsHash, d.VsHint, true), (d.FsHash, d.FsHint, false) }).Distinct().ToList();
        string[] variants = s.Fp16WhatIf ? ["", "half", "allmp"] : [""];
        var stats = new ConcurrentDictionary<(ulong, ulong, bool, string), Stats>();
        var sw = Stopwatch.StartNew();
        // two malioc processes at a time (CPU cap)
        Parallel.ForEach(keys.SelectMany(k => variants.Where(v => v != "allmp" || !k.Item3).Select(v => (k, v))), new ParallelOptions { MaxDegreeOfParallelism = 2 }, kv =>
        {
            var ((h, hint, vertex), v) = kv;
            string glsl = _glsl.GetValueOrDefault((h, hint, vertex)) ?? "";
            // Mali Bifrost/Valhall has no clip distances (the device path is FarPlaneDiscard) and malioc crashes on them
            if (vertex) glsl = Regex.Replace(glsl, @"^\s*gl_ClipDistance\[\d\] = [^;]*;\r?\n", "", RegexOptions.Multiline);
            string src = v switch { "half" => RelaxHalf(glsl), "allmp" => RelaxAll(glsl), _ => glsl };
            // a variant identical to the base shader compiles under the base name
            stats[(h, hint, vertex, v)] = Compile(h, hint, vertex, src == glsl ? "" : v, src);
        });
        Stats S(ulong h, ulong hint, bool vertex, string v) => stats.TryGetValue((h, hint, vertex, v), out var st) ? st : stats[(h, hint, vertex, "")];
        sb.AppendLine($"{keys.Count} shaders ({keys.Count(k => k.Item3)} vertex, {keys.Count(k => !k.Item3)} fragment), malioc {sw.Elapsed.TotalSeconds:F1} s (cached in {_cacheDir})");
        foreach (var e in keys.Select(k => (k, st: S(k.Item1, k.Item2, k.Item3, ""))).Where(x => x.st.Error != null).Take(10))
            sb.AppendLine($"  malioc failed on {e.k.Item1:x16}/{e.k.Item2:x16} {(e.k.Item3 ? "vert" : "frag")}: {e.st.Error}");

        double cyclesPerMs = s.Cores * s.Mhz * 1000 / s.Scale;
        DrawCost Cost(DrawCostRecord d, string v)
        {
            var vs = S(d.VsHash, d.VsHint, true, v == "allmp" ? "" : v);
            var fs = S(d.FsHash, d.FsHint, false, v);
            // unmeasured (a programmable-blending draw split into pieces with a visibility test): a quarter of the target
            double frags = d.Samples < 0 ? (double)d.RtW * d.RtH * 0.25 : (double)d.Samples / Math.Max(1, d.SampleCount);
            return new DrawCost(d, d.Vertices * vs.BoundCycles / cyclesPerMs, frags * fs.BoundCycles / cyclesPerMs, vs, fs);
        }
        double TileMs(long present) => _tile.TryGetValue(present, out var t) ? (t.LoadBytes + t.StoreBytes) / (s.Gbps * 1e6) : 0;

        var frames = draws.GroupBy(d => d.Present).OrderBy(g => g.Key).ToList();
        sb.AppendLine();
        sb.AppendLine("frame  draws  unmeasured  vertices   fragments   vertex ms  fragment ms  tile MB (load/store)  tile ms  Mali ms" + (s.Fp16WhatIf ? "  | fp16 half: ms (saving)  | all-fragment mediump: ms" : ""));
        var totals = new List<(long Present, double Ms, double Half, double All)>();
        foreach (var g in frames)
        {
            var c = g.Select(d => Cost(d, "")).ToList();
            double vms = c.Sum(x => x.VsMs), fms = c.Sum(x => x.FsMs), tms = TileMs(g.Key);
            double ms = vms + fms + tms;
            double half = s.Fp16WhatIf ? g.Sum(d => Cost(d, "half").Ms) + tms : ms, all = s.Fp16WhatIf ? g.Sum(d => Cost(d, "allmp").Ms) + tms : ms;
            var t = _tile.GetValueOrDefault(g.Key);
            totals.Add((g.Key, ms, half, all));
            sb.Append(Inv, $"{g.Key,5} {g.Count(),6} {g.Count(d => d.Samples < 0),11} {g.Sum(d => d.Vertices),9} {g.Sum(d => Math.Max(0, d.Samples) / (double)Math.Max(1, d.SampleCount)),11:F0} {vms,11:F2} {fms,12:F2} {t.LoadBytes / 1048576.0,9:F1}/{t.StoreBytes / 1048576.0,-9:F1} {tms,9:F2} {ms,8:F2}");
            sb.AppendLine(s.Fp16WhatIf ? string.Create(Inv, $"  | {half,8:F2} ({(ms - half) / Math.Max(ms, 1e-9) * 100,5:F1}%)    | {all,8:F2}") : "");
        }
        double avg = totals.Average(t => t.Ms);
        sb.Append(Inv, $"average {avg:F2} ms over {totals.Count} frames");
        if (s.Fp16WhatIf)
        {
            double avgHalf = totals.Average(t => t.Half), avgAll = totals.Average(t => t.All);
            sb.Append(Inv, $"; fp16 on half registers {avgHalf:F2} ms ({(avg - avgHalf) / avg * 100:F1}% less); every fragment float mediump {avgAll:F2} ms ({(avg - avgAll) / avg * 100:F1}% less, upper bound)");
        }
        sb.AppendLine();
        var inRange = totals.Select(t => t.Present).ToHashSet();
        long allLoad = _tile.Where(k => inRange.Contains(k.Key)).Sum(k => k.Value.LoadBytes), allStore = _tile.Where(k => inRange.Contains(k.Key)).Sum(k => k.Value.StoreBytes);
        sb.AppendLine(Inv, $"tile load/store bytes, all frames: {allLoad / 1048576.0:F1} MB load, {allStore / 1048576.0:F1} MB store ({(allLoad + allStore) / 1048576.0 / totals.Count:F1} MB per frame)");

        // the most expensive frame's top draws
        var worst = totals.MaxBy(t => t.Ms);
        var top = frames.First(g => g.Key == worst.Present).Select(d => Cost(d, "")).OrderByDescending(x => x.Ms).Take(10).ToList();
        sb.AppendLine();
        sb.AppendLine(Inv, $"top 10 draws of frame {worst.Present} ({worst.Ms:F2} ms):");
        sb.AppendLine("  draw   ms     %   vertices  fragments  stage bound  vs hash           fs hash           fs cycles A/LS/V/T");
        foreach (var x in top)
        {
            bool fsHeavy = x.FsMs >= x.VsMs;
            var st = fsHeavy ? x.Fs : x.Vs;
            string bound = st.Bound < 0 ? "-" : UnitNames[st.Bound];
            sb.AppendLine(Inv, $"  {x.D.Serial,4} {x.Ms,6:F3} {x.Ms / worst.Ms * 100,4:F1} {x.D.Vertices,9} {(x.D.Samples < 0 ? "?" : (x.D.Samples / Math.Max(1, x.D.SampleCount)).ToString(Inv)),10}  {(fsHeavy ? "frag" : "vert")} {bound,-5} {x.D.VsHash:x16}  {x.D.FsHash:x16}  {string.Join("/", x.Fs.Cycles.Select(c => c.ToString("F2", Inv)))}");
        }

        // shaders that spill or run below full occupancy, weighted by their share of the estimate
        var share = new Dictionary<(ulong, ulong, bool), double>();
        foreach (var x in draws.Select(d => Cost(d, "")))
        {
            share[(x.D.VsHash, x.D.VsHint, true)] = share.GetValueOrDefault((x.D.VsHash, x.D.VsHint, true)) + x.VsMs;
            share[(x.D.FsHash, x.D.FsHint, false)] = share.GetValueOrDefault((x.D.FsHash, x.D.FsHint, false)) + x.FsMs;
        }
        var bad = keys.Select(k => (k, st: S(k.Item1, k.Item2, k.Item3, ""))).Where(x => x.st.Spills || x.st.Occupancy < 100).OrderByDescending(x => share.GetValueOrDefault(x.k)).ToList();
        sb.AppendLine();
        sb.AppendLine($"shaders that spill or run at reduced occupancy: {bad.Count} of {keys.Count}");
        foreach (var (k, st) in bad.Take(20))
            sb.AppendLine(Inv, $"  {k.Item1:x16}/{k.Item2:x16} {(k.Item3 ? "vert" : "frag")}: {(st.Spills ? "spills, " : "")}occupancy {st.Occupancy}%, {st.WorkRegisters} work registers, {share.GetValueOrDefault(k) / totals.Count:F3} ms per frame");
        if (s.Fp16WhatIf)
            sb.AppendLine($"fp16 what-if: {keys.Count(k => S(k.Item1, k.Item2, k.Item3, "half").Fp16 > S(k.Item1, k.Item2, k.Item3, "").Fp16)} of {keys.Count} shaders gain 16-bit arithmetic from the half-register relaxation");
        Write(sb);
    }

    void Write(StringBuilder sb)
    {
        string path = Path.Combine(s.OutDir, "mali_cost.txt");
        Directory.CreateDirectory(s.OutDir);
        File.WriteAllText(path, sb.ToString());
        Console.Write(sb.ToString());
        Console.WriteLine($"Mali cost estimate written to {path}");
    }
}
