namespace VitaSharp.Tests;

/// <summary>
/// A "Gravar tudo" capture re-run where its game is not installed (someone sends a capture; whoever analyses it does
/// not have the game, its update or the plugins of the computer that recorded it): <c>rerun DIR</c> brings the program
/// up from the capture's own state and replays its input, and the re-run shows the same frames and makes the same HLE
/// calls as the recording. Synthetic: <see cref="SyntheticTitle"/> is recorded with a button pressed in the middle of
/// the recording (its frames change colour), in a child process with a data folder of its own; the capture is copied
/// to another data folder, without the title, and re-run there.
/// </summary>
public sealed class SessionCaptureRerunTests : IDisposable
{
    private readonly string _recorded = Path.Combine(Path.GetTempPath(), "vitasharp-caprec-" + Guid.NewGuid().ToString("N"));
    private readonly string _analyst = Path.Combine(Path.GetTempPath(), "vitasharp-capana-" + Guid.NewGuid().ToString("N"));

    public SessionCaptureRerunTests()
    {
        Directory.CreateDirectory(_recorded);
        Directory.CreateDirectory(_analyst);
    }

    public void Dispose()
    {
        foreach (string dir in new[] { _recorded, _analyst })
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Tail(string s) => s.Length > 6000 ? s[^6000..] : s;

    /// <summary>The re-run's comparison lines, then the end of its output.</summary>
    private static string Summary(string s) =>
        string.Join("\n", s.Split('\n').Where(l => l.Contains("compare", StringComparison.Ordinal) || l.Contains("HLE calls", StringComparison.Ordinal))) + "\n" + Tail(s);

    [SoftwareVulkanFact]
    public void ACaptureReRunsWithoutItsGameAndShowsTheSameFramesAndCalls()
    {
        string app = SyntheticTitle.Install(Path.Combine(_recorded, "fs"));
        DesktopProcess.Result run = DesktopProcess.Run(_recorded, ["run", app, "--headless", "--vulkan", "--no-vsync", "--cpu=interp", "--frames=170",
            "--input=cross@f80:30", "--capture-at=40:140"], timeoutMs: 240_000);
        Assert.True(run.ExitCode == 0, Tail(run.Output));
        string capture = Directory.GetDirectories(Path.Combine(_recorded, "captures"), "VSTS00001_*_tudo").Single();
        Assert.True(File.Exists(Path.Combine(capture, "state_start.vss")), Tail(run.Output));
        Assert.True(File.Exists(Path.Combine(capture, "hle_calls.tsv")));
        Assert.Contains("sceCtrlPeekBufferPositive", File.ReadAllText(Path.Combine(capture, "hle_calls.tsv")));
        Assert.Contains("app-guest-path: ux0:app/VSTS00001", File.ReadAllText(Path.Combine(capture, "session.txt")));

        // the analyst's computer: the capture, no title, no firmware, no plugins
        string copy = Path.Combine(_analyst, "received", Path.GetFileName(capture));
        CopyFolder(capture, copy);
        Directory.Delete(_recorded, true);
        Assert.False(Directory.Exists(Path.Combine(_analyst, "fs", "ux0", "app", "VSTS00001")));

        // booting the installed game (what a re-run did before) cannot work here
        DesktopProcess.Result withTitle = DesktopProcess.Run(_analyst, ["rerun", copy, "--boot=title"], timeoutMs: 60_000);
        Assert.Equal(1, withTitle.ExitCode);
        Assert.Contains("is not installed", withTitle.Output);

        DesktopProcess.Result rerun = DesktopProcess.Run(_analyst, ["rerun", copy, "--cpu=interp", "--seconds=180"], timeoutMs: 240_000);
        Assert.True(rerun.ExitCode == 0, Tail(rerun.Output));
        Assert.True(rerun.Output.Contains("--boot-state=", StringComparison.Ordinal), Summary(rerun.Output));
        Assert.True(rerun.Output.Contains("no file of the program read", StringComparison.Ordinal), Summary(rerun.Output));
        Assert.True(rerun.Output.Contains("100% look the same in the re-run", StringComparison.Ordinal), Summary(rerun.Output));
        Assert.True(rerun.Output.Contains("no divergence", StringComparison.Ordinal), Summary(rerun.Output));
        Assert.True(rerun.Output.Contains("HLE calls: the same calls and results in every thread", StringComparison.Ordinal), Summary(rerun.Output));
        Assert.False(Directory.Exists(Path.Combine(_analyst, "fs", "ux0", "app", "VSTS00001")), "the re-run made the game's folder");
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
