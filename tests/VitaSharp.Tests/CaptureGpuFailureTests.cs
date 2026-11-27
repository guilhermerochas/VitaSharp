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

    [Fact]
    public void NothingIsSaidWhenTheDeviceWasNotLost()
    {
        File.WriteAllLines(Path.Combine(_dir, "log.txt"), ["[14:38:16.900] [Info ] [GPU worker] [gpu] a draw of an open scene ran before the scene ended"]);
        Assert.Empty(CaptureAnalyzer.GpuFailureLines(_dir));
        Assert.Empty(CaptureAnalyzer.GpuFailureLines(Path.Combine(_dir, "missing")));
    }
}
