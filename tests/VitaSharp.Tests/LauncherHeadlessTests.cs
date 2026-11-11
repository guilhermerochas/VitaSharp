using VitaSharp.Core.Util;
using VitaSharp.Desktop.Launcher;

namespace VitaSharp.Tests;

/// <summary>
/// The launcher on Avalonia's headless platform (no window on screen), as <c>ui-render</c> drives it: every page
/// (library grid, list and home views, a game's page and LiveArea, system, trophies, captures, install, settings and
/// each settings section) is laid out and rendered from a scratch data folder with one installed game, and saved as a
/// PNG. The data folder is the test's own (VITASHARP_DATA), never the user's; nothing is saved and the update check
/// is off.
/// </summary>
[Collection("launcher")]
public sealed class LauncherHeadlessTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-launcher-" + Guid.NewGuid().ToString("N"));
    readonly string? _savedData = Environment.GetEnvironmentVariable(EnvVars.Data);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVars.Data, _savedData);
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void EveryPageRenders()
    {
        string data = Path.Combine(_root, "data"), outDir = Path.Combine(_root, "out");
        string game = Path.Combine(data, "fs", "ux0", "app", "PCSB00123");
        Directory.CreateDirectory(Path.Combine(game, "sce_sys"));
        File.WriteAllBytes(Path.Combine(game, "eboot.bin"), new byte[64]);
        File.WriteAllText(Path.Combine(data, "launcher.json"), "{ \"CheckUpdates\": false }");
        Environment.SetEnvironmentVariable(EnvVars.Data, data);
        Assert.Equal(data, VitaSharp.Desktop.Program.DataRoot);

        int rc = UiRender.Run([outDir, "--theme=dark", "--size=1280x720"]);
        var shots = Directory.GetFiles(outDir, "*.png").Select(Path.GetFileNameWithoutExtension).ToHashSet();
        Assert.Equal(0, rc);
        foreach (var page in new[] { "library_dark", "library-list_dark", "library-home_dark", "game_dark", "livearea_dark", "settings_dark" })
        {
            Assert.Contains(page, shots);
        }

        Assert.True(shots.Count >= 12, $"{shots.Count} pages rendered");
        // nothing was written to the data folder but what the test put there (settings are not saved)
        Assert.Equal("{ \"CheckUpdates\": false }", File.ReadAllText(Path.Combine(data, "launcher.json")));
    }
}
