using VitaSharp.Core.Capture;

namespace VitaSharp.Tests;

/// <summary>
/// The capture report says first that the GPU device was lost, where (the Vulkan call, the present and guest frame)
/// and the renderer's state, from the renderer's report in log.txt: a capture saved by itself at the failure (FIFA 13
/// on a Mali-G57) listed stalls and hitches but never the device lost that ended the game's picture.
/// </summary>
public sealed class CaptureGpuFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-gpufail-" + Guid.NewGuid().ToString("N"));

    public CaptureGpuFailureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void TheDeviceLostAndTheRendererStateAreReported()
    {
        File.WriteAllLines(Path.Combine(_dir, "log.txt"),
        [
            "[14:38:16.900] [Info ] [Guest:SceGxmDisplayQueue] [hitch] present 3718: 120 ms since the previous one; gpuIdle 100 ms",
            "[14:38:17.048] [Error] [GPU worker] [gpu] VULKAN DEVICE LOST in vkQueueSubmit at present #3719 (guest frame 4832)",
            "renderer: present #3719, GPU frames 5000, frame slot 1, in scene False, rt 0x65604000, draws this frame 588, device lost True, shader jobs in flight 0",
            "  frame slot 0: recording False, submitted True (seq 29097), retired False, writebacks 2, signals 0",
        ]);

        List<string> lines = CaptureAnalyzer.GpuFailureLines(_dir);

        Assert.Equal(2, lines.Count);
        Assert.Contains("GPU DEVICE LOST", lines[0]);
        Assert.Contains("vkQueueSubmit at present #3719 (guest frame 4832)", lines[0]);
        Assert.StartsWith("  renderer: present #3719", lines[1]);
        Assert.Contains("GPU DEVICE LOST", CaptureAnalyzer.Analyze(_dir, quiet: true));
    }

    /// <summary>The programs the GPU was running (VulkanRenderer.ProgramsInFlight, after the "last frames" table) are in
    /// the report, so a player's capture names the vertex program and its uniform size without a GPU dump.</summary>
    [Fact]
    public void TheProgramsInFlightAreReported()
    {
        File.WriteAllLines(Path.Combine(_dir, "log.txt"),
        [
            "[15:21:27.586] [Error] [GPU worker] [gpu] VULKAN DEVICE LOST in vkQueueSubmit at present #5856 (guest frame 6608)",
            "renderer: present #5856, GPU frames 6844, frame slot 4, in scene False, rt 0x65406000, draws this frame 572, device lost True, shader jobs in flight 0",
            "last frames:",
            "p5855 f6607 35,0ms draws=89 scenes=2 newSh=0 newPipe=0 newRT=0 newTex=0 hitch=- fb=0x65802000 960x544",
            "programs in flight:",
            "frame slot 4 (recording): 2 program pairs",
            "  vs=E05AF95937F44FD8 fs=395C969A7F9C2061 rt=0x64402F00(2048x1024) vu=32800 fu=16 draws=252",
            "  vs=3FA0D8DA7398509E fs=43E4D84729D91B9B rt=0x64402F00(2048x1024) vu=16 fu=16 draws=4",
            "[15:21:27.600] [Error] [GPU worker] GPU backend signal failed",
        ]);

        List<string> lines = CaptureAnalyzer.GpuFailureLines(_dir);

        Assert.Contains(lines, l => l.Contains("vs=E05AF95937F44FD8", StringComparison.Ordinal) && l.Contains("vu=32800", StringComparison.Ordinal) && l.Contains("draws=252", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("frame slot 4 (recording)", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("p5855", StringComparison.Ordinal) || l.Contains("signal failed", StringComparison.Ordinal));
    }

    [Fact]
    public void NothingIsSaidWhenTheDeviceWasNotLost()
    {
        File.WriteAllLines(Path.Combine(_dir, "log.txt"), ["[14:38:16.900] [Info ] [GPU worker] [gpu] a draw of an open scene ran before the scene ended"]);
        Assert.Empty(CaptureAnalyzer.GpuFailureLines(_dir));
        Assert.Empty(CaptureAnalyzer.GpuFailureLines(Path.Combine(_dir, "missing")));
    }
}
