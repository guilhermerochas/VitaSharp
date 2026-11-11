using VitaSharp.Core.Capture;

namespace VitaSharp.Tests;

/// <summary>
/// "Gravar tudo" end to end on an emulator without a game: a recording started and stopped with frames presented in
/// between leaves a folder (frames file, input log, notes) and its zip; a second start while one runs is refused, a
/// stop without a recording does nothing, and the pre-roll can be armed and disarmed.
/// </summary>
[Collection("guest")]
public sealed class SessionCaptureFlowTests(GuestMemoryFixture fx) : IDisposable
{
    readonly HleHarness _h = new(fx, "capture");

    public void Dispose() => _h.Dispose();

    [Fact]
    public void RecordingWritesAFolderAndItsZip()
    {
        string root = Path.Combine(_h.Root, "captures");
        IPresentTap? tap = null;
        var status = new List<string>();
        var cap = new SessionCapture(_h.Emu, new SessionCaptureOptions
        {
            Root = root, SetPresentTap = t => tap = t, StartState = false, GpuDump = false, Zip = true,
            Status = s => { lock (status) status.Add(s); }, Threads = () => "no threads", Info = () => "test info",
        });
        Assert.False(cap.IsRecording);
        Assert.False(cap.Stop()); // nothing to stop
        Assert.True(cap.Start("test"));
        HleHarness.Until(() => cap.IsRecording && tap != null, "the recording to start");
        Assert.False(cap.Start("again")); // one at a time
        const int W = 32, H = 16;
        var px = new byte[W * H * 4];
        for (int f = 0; f < 5; f++)
        {
            for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 7 + f * 13);
            var info = new PresentInfo { Present = f + 1, Flip = f, VBlank = f * 2, PresentNs = CaptureClock.NowNs, IntervalMs = 16.7f, Hitch = "" };
            tap!.OnPresent(info, px, W, H);
        }
        cap.NoteKey("F8");
        Assert.True(cap.RecordingSeconds >= 0);
        Assert.True(cap.Stop("test end"));
        Assert.True(cap.Idle.Wait(120_000), "the recording was not saved");
        Assert.False(cap.IsRecording);
        Assert.NotNull(cap.Dir);
        Assert.True(File.Exists(Path.Combine(cap.Dir!, "frames.vsfr")));
        Assert.True(new FileInfo(Path.Combine(cap.Dir!, "frames.vsfr")).Length > 0);
        Assert.NotNull(cap.LastZip);
        Assert.True(File.Exists(cap.LastZip));
        Assert.Equal(-1, cap.SavePercent);
        lock (status) Assert.NotEmpty(status);

        cap.SetPreRoll(5);
        Assert.True(cap.Armed);
        Assert.Equal(5, cap.PreRollSeconds);
        cap.SetPreRoll(0);
        Assert.False(cap.Armed);
        cap.Hurry();
        cap.Dispose();
    }
}
