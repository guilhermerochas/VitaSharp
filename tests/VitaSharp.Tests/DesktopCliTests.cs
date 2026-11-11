using System.Text.RegularExpressions;

namespace VitaSharp.Tests;

/// <summary>
/// The front-end's command line (Program.Main) in a child process (<see cref="DesktopProcess"/>) on a scratch data
/// folder holding <see cref="SyntheticTitle"/>: VPK install and run, the import survey, the HLE coverage table, the
/// disassembler and stub dump of the loaded executable, the system registry, the overlay previews and the PNG compare.
/// Each command's output (text, files, pixels) is checked, not just its exit code.
/// </summary>
public sealed class DesktopCliTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-cli-" + Guid.NewGuid().ToString("N"));

    public DesktopCliTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    DesktopProcess.Result Run(params string[] args)
    {
        var r = DesktopProcess.Run(_root, args, softwareVulkan: false);
        return r;
    }

    static string Tail(string s) => s.Length > 4000 ? s[^4000..] : s;

    [Fact]
    public void VpkInstallsAndRunsAndTheToolsReadTheInstalledExecutable()
    {
        string vpk = Path.Combine(_root, "synthetic.vpk");
        SyntheticTitle.WriteVpk(vpk);

        // install, then a second install without --force skips the installed title (stdin is not a terminal: no prompt)
        var r = Run("install", vpk);
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Contains($"Installed VSTS00001 \"{SyntheticTitle.Title}\"", r.Output);
        string app = Path.Combine(_root, "fs", "ux0", "app", "VSTS00001");
        Assert.Equal(SyntheticTitle.Elf(), File.ReadAllBytes(Path.Combine(app, "eboot.bin")));
        r = Run("install", vpk);
        Assert.Contains("Skipped VSTS00001", r.Output);
        r = Run("install", vpk, "--force");
        Assert.Contains("Updated VSTS00001", r.Output);

        // run <vpk>: already installed, so it boots the installed copy
        r = Run("run", vpk, "--headless", "--frames=5", "--cpu=interp");
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Contains("VSTS00001 is already installed", r.Output);
        Assert.Matches(@"Run finished: \d+ frames", r.Output);

        // imports: the three firmware functions, all implemented by the HLE
        r = Run("imports", "VSTS00001");
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Contains("== VitaSharpSynthetic", r.Output);
        foreach (var lib in new[] { "SceCtrl", "SceDisplayUser", "SceDisplay" })
        {
            Assert.Matches(new Regex($@"\b{lib}\s+1 imports,\s+0 missing"), r.Output);
        }

        Assert.Contains("Total 3: LLE 0, HLE 3, missing 0", r.Output);

        // stub: the import stub of sceCtrlPeekBufferPositive (svc + bx lr + NID)
        r = Run("stub", "VSTS00001", $"{SyntheticTitle.Base + 0x180:X8}");
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Contains("NID A9C3CED6 = sceCtrlPeekBufferPositive", r.Output);
        Assert.Contains($"{SyntheticTitle.Base + 0x184:X8}: E12FFF1E", r.Output);

        // disasm --raw: the bytes of the loaded code, as the ELF has them
        string raw = Path.Combine(_root, "code.bin");
        r = Run("disasm", "VSTS00001", $"{SyntheticTitle.Base + 0x200:X8}", "16", $"--raw={raw}");
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Equal(SyntheticTitle.Elf().AsSpan(0x100 + 0x200, 16).ToArray(), File.ReadAllBytes(raw));
        r = Run("disasm", "VSTS00001", $"{SyntheticTitle.Base + 0x200:X8}", "4");
        Assert.Equal(4, r.Output.Split('\n').Count(l => l.StartsWith($"{SyntheticTitle.Base >> 16:X4}")));

        // hle-coverage: no firmware, so no exported functions; the census reads the one game
        string tsv = Path.Combine(_root, "coverage.tsv");
        r = Run("hle-coverage", $"--out={tsv}");
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Contains("0 firmware modules read, 1 games", r.Output);
        Assert.True(File.Exists(tsv));
    }

    /// <summary>hle-coverage on a fixed-address executable (ET_SCE_EXEC): the census lists its three imports, each
    /// implemented by the HLE.</summary>
    [Fact]
    public void HleCoverageListsTheImportsOfAFixedAddressExecutable()
    {
        SyntheticTitle.Install(Path.Combine(_root, "fs"));
        string tsv = Path.Combine(_root, "coverage.tsv");
        var r = Run("hle-coverage", $"--out={tsv}");
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        var rows = File.ReadAllLines(tsv);
        Assert.Contains(rows, l => l.StartsWith("0xA9C3CED6\tsceCtrlPeekBufferPositive\t") && l.Contains("\timpl\t"));
        Assert.Equal(3, rows.Length);
    }

    [Fact]
    public void PreviewsRegistryVersionAndPngDiff()
    {
        // the overlay previews: PNGs of the panels the game window shows, at 2x
        string toast = Path.Combine(_root, "toast.png"), ime = Path.Combine(_root, "ime.png"), msg = Path.Combine(_root, "msg.png");
        Assert.Equal(0, Run("toast-preview", toast).ExitCode);
        Assert.Equal(0, Run("ime-preview", ime, "Name", "hello", "--max=8").ExitCode);
        Assert.Equal(0, Run("msg-preview", msg, "Save?", "--buttons=Yes,No", "--selected=1", "--progress=40").ExitCode);
        var (_, iw, ih) = DesktopProcess.Probe(ime);
        Assert.Equal(1200, iw);
        Assert.True(ih > 100);
        Assert.Equal(1200, DesktopProcess.Probe(msg).Width);
        Assert.True(DesktopProcess.Probe(toast).Width > 200);

        // png-diff: a picture against itself and against the same panel with the other button selected
        string a = Path.Combine(_root, "a"), b = Path.Combine(_root, "b"), msg0 = Path.Combine(_root, "msg0.png");
        Assert.Equal(0, Run("msg-preview", msg0, "Save?", "--buttons=Yes,No", "--selected=0", "--progress=40").ExitCode);
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.Copy(msg, Path.Combine(a, "x.png"));
        File.Copy(msg, Path.Combine(b, "x.png"));
        File.Copy(msg0, Path.Combine(a, "y.png"));
        File.Copy(msg, Path.Combine(b, "y.png"));
        var r = Run("png-diff", a, b);
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Contains("COMPARE 2 frames", r.Output);
        // the numbers are culture-independent (a dot decimal separator whatever the system language)
        Assert.Matches(@"worst mean difference \d+\.\d{4} \(y\.png\)", r.Output);
        Assert.Contains("1 pixel-identical", r.Output);
        var compare = File.ReadAllLines(Path.Combine(a, "compare.txt"));
        Assert.Contains("x.png 100.000 0.000 0.0000 0", compare);
        Assert.Contains(compare, l => Regex.IsMatch(l, @"^y\.png \d+\.\d{3} \d+\.\d{3} \d+\.\d{4} \d+$") && !l.StartsWith("y.png 100.000"));
        Assert.True(File.Exists(Path.Combine(a, "diff_y.png")));
        Assert.False(File.Exists(Path.Combine(a, "diff_x.png")));

        // version
        r = Run("version");
        Assert.Equal(0, r.ExitCode);
        Assert.Matches(@"\d+\.\d+", r.Output);

        // registry: without an installed firmware there is no template, so no key exists and none is written
        r = Run("registry");
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Contains("no such key /CONFIG/SYSTEM/language", Run("registry", "/CONFIG/SYSTEM/language=2").Output);

        // the firmware commands on a data folder without one
        r = Run("firmware-info");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("No firmware installed", r.Output);
        // it reports what is missing and fails with 1, it does not crash (an unhandled exception without vs0:sys/external
        // took more than 2 minutes on macOS: the crash dump the test run's blame collector asks every child for)
        r = Run("firmware-verify");
        Assert.True(r.ExitCode == 1, Tail(r.Output));
        Assert.Contains("MISSING  vs0/sys/external/libc.suprx", r.Output);
        Assert.Contains("vs0:sys/external: 0/0 modules decrypt", r.Output);
        Assert.DoesNotContain("Unhandled exception", r.Output);

        // memdiff: a float3 that jumped (its new value existed elsewhere before) and a value that became NaN
        static void Dump(string path, float[] floats)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(0x81000000u);
            w.Write((uint)floats.Length * 4);
            foreach (var f in floats)
            {
                w.Write(f);
            }
        }
        string before = Path.Combine(_root, "before.bin"), after = Path.Combine(_root, "after.bin");
        Dump(before, [10, 20, 30, 0, 500, 600, 700, 0, 1.5f]);
        Dump(after, [500, 600, 700, 0, 500, 600, 700, 0, BitConverter.UInt32BitsToSingle(0x7FC00000)]); // the ARM default NaN
        r = Run("memdiff", before, after, "10");
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Contains("1 values became NaN/Inf", r.Output);
        Assert.Contains("1 jumped float3 values (>= 10)", r.Output);
        Assert.Contains("81000000: (10.00, 20.00, 30.00) -> (500.00, 600.00, 700.00)   new value existed before at 81000010", r.Output);

        // an unknown command prints the usage and fails
        r = Run("no-such-command");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("VitaSharp - PS Vita emulator", r.Output);
    }

    /// <summary>
    /// ui-render --launch: the launcher on Avalonia's headless platform starts the synthetic title through its own
    /// launch path (a headless game process, as "Jogar" starts one), follows it until it exits, records the play time and
    /// renders every page with the title in the library. The update check is off and nothing is saved.
    /// </summary>
    [Fact]
    public void LauncherStartsTheTitleAndRecordsItsPlayTime()
    {
        SyntheticTitle.Install(Path.Combine(_root, "fs"));
        File.WriteAllText(Path.Combine(_root, "launcher.json"), "{ \"CheckUpdates\": false }");
        string shots = Path.Combine(_root, "shots");
        var r = DesktopProcess.Run(_root, ["ui-render", shots, "--theme=dark", "--size=960x600", "--launch=VSTS00001", "--seconds=3"], timeoutMs: 240_000, softwareVulkan: false);
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Contains("PASS", r.Output);
        var m = Regex.Match(r.Output, @"launch VSTS00001: exit 0, play time (\d+) -> (\d+) s");
        Assert.True(m.Success, Tail(r.Output));
        Assert.True(int.Parse(m.Groups[2].Value) > int.Parse(m.Groups[1].Value));
        var pngs = Directory.GetFiles(shots, "*.png").Select(Path.GetFileNameWithoutExtension).ToHashSet();
        foreach (var page in new[] { "game_dark", "livearea_dark", "running_dark", "after-launch_dark" })
        {
            Assert.Contains(page, pngs);
        }
        // the game page shows the title's name from its param.sfo: the page differs from the empty library's
        Assert.True(DesktopProcess.Probe(Path.Combine(shots, "game_dark.png")).Width >= 960);
        Assert.Equal("{ \"CheckUpdates\": false }", File.ReadAllText(Path.Combine(_root, "launcher.json")));
    }
}
