namespace VitaSharp.Tests;

/// <summary>
/// replay-gpu's shader cache: the one given with --shader-cache, or else a new temporary folder for each replay, never
/// the user's shadercache in the data folder (a replay records the dump's shaders and pipelines into the cache it runs
/// with). <see cref="RunnerProcessTests.ScheduledSaveStateCaptureAndGpuDumpThatReplays"/> checks the whole replay.
/// </summary>
public sealed class ReplayShaderCacheTests
{
    [Fact]
    public void WithoutTheOptionEachReplayGetsANewTemporaryCacheOutsideTheDataFolder()
    {
        string first = VitaSharp.Desktop.ReplayGpu.ShaderCacheDir(["--out=x"], out bool temporary);
        string second = VitaSharp.Desktop.ReplayGpu.ShaderCacheDir([], out bool alsoTemporary);
        Assert.True(temporary && alsoTemporary);
        Assert.NotEqual(first, second);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), first, StringComparison.OrdinalIgnoreCase);
        string userCache = Path.GetFullPath(Path.Combine(VitaSharp.Desktop.Program.DataRoot, "shadercache"));
        Assert.False(first.StartsWith(userCache, StringComparison.OrdinalIgnoreCase), first);
        Assert.False(Directory.Exists(first));
    }

    [Fact]
    public void TheGivenCacheIsUsedAsIs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-replaycache-given");
        Assert.Equal(Path.GetFullPath(dir), VitaSharp.Desktop.ReplayGpu.ShaderCacheDir([$"--shader-cache={dir}"], out bool temporary));
        Assert.False(temporary);
    }
}
