using VitaSharp.Core.Host;

namespace VitaSharp.Tests;

/// <summary>One host capture shared by both Vita cameras: reuse across sensor switches, linger, mode changes, stop order.</summary>
public sealed class SharedCaptureTests
{
    sealed class FakeCapture(Task after, List<string> events, string name) : IHostCapture
    {
        public CameraFrameBuffer Frames { get; } = new();
        public readonly Task After = after;
        public int Retries;
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void RetryNow() => Retries++;
        public Task StopAsync()
        {
            lock (events) events.Add("stop " + name);
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }
    }

    readonly List<string> _events = new();
    readonly List<FakeCapture> _made = new();

    Func<Task, IHostCapture> Factory(string name) => after =>
    {
        lock (_events) _events.Add("open " + name);
        var c = new FakeCapture(after, _events, name);
        _made.Add(c);
        return c;
    };

    [Fact]
    public void SensorSwitchReusesTheRunningCapture()
    {
        using var shared = new SharedCapture(TimeSpan.FromSeconds(30));
        var back = shared.Acquire("640x480", Factory("a"), out bool r1)!;
        Assert.False(r1);
        back.Dispose(); // sceCameraStop(back) ...
        var front = shared.Acquire("640x480", Factory("b"), out bool r2)!; // ... sceCameraStart(front)
        Assert.True(r2);
        Assert.Equal(1, shared.Created);
        Assert.Same(_made[0].Frames, front.Frames);
        Assert.Equal(1, _made[0].Retries); // a capture waiting to retry a failed start retries at once
        front.Dispose();
        Assert.Equal(["open a"], _events);
    }

    [Fact]
    public async Task IdleCaptureStopsAfterTheLinger()
    {
        using var shared = new SharedCapture(TimeSpan.FromMilliseconds(50));
        var s = shared.Acquire("m", Factory("a"), out _)!;
        s.Dispose();
        s.Dispose(); // idempotent
        await _made[0].Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // the next start opens a new capture, which waits for the previous stop
        var s2 = shared.Acquire("m", Factory("b"), out bool reused)!;
        Assert.False(reused);
        await _made[1].After.WaitAsync(TimeSpan.FromSeconds(5));
        s2.Dispose();
    }

    [Fact]
    public async Task LiveLeaseKeepsTheCaptureRunning()
    {
        using var shared = new SharedCapture(TimeSpan.Zero);
        var a = shared.Acquire("m", Factory("a"), out _)!;
        var b = shared.Acquire("other mode", Factory("b"), out bool reused)!; // in use: shared even in another mode
        Assert.True(reused);
        a.Dispose();
        Assert.False(_made[0].Stopped.Task.IsCompleted);
        b.Dispose();
        await _made[0].Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ModeChangeReplacesTheIdleCaptureAfterItStops()
    {
        using var shared = new SharedCapture(TimeSpan.FromSeconds(30));
        shared.Acquire("640x480", Factory("a"), out _)!.Dispose();
        var s = shared.Acquire("320x240", Factory("b"), out bool reused)!;
        Assert.False(reused);
        await _made[1].After.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_made[0].Stopped.Task.IsCompleted); // opened only after the old capture released the device
        s.Dispose();
    }

    [Fact]
    public void DisposeStopsEvenWithLiveStreams()
    {
        var shared = new SharedCapture(TimeSpan.FromSeconds(30));
        var s = shared.Acquire("m", Factory("a"), out _)!;
        shared.Dispose();
        Assert.True(_made[0].Stopped.Task.IsCompleted);
        s.Dispose(); // late release after the source is gone: harmless
        Assert.Null(shared.Acquire("m", Factory("b"), out _));
        Assert.Equal(["open a", "stop a"], _events);
    }
}
