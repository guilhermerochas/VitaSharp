using VitaSharp.Core.Gpu;

namespace VitaSharp.Tests;

/// <summary>The GPU worker holds a draw of an open scene (its bound uniform buffers are read late) until the scene ends,
/// unless a guest waits for that draw.</summary>
[Collection("guest")]
public sealed class SceneGateTests(GuestMemoryFixture fixture)
{
    private sealed class CountingBackend : IGpuBackend
    {
        public int Draws;
        public long PresentCount => 0;
        public byte[]? ExportDumpState() => null;
        public void Execute(GpuCommand command)
        {
            if (command is DrawCommand)
            {
                Interlocked.Increment(ref Draws);
            }
        }
        public void Dispose()
        {
        }
    }

    /// <summary>Only a guard against a hang: what releases a draw is the scene's end or a guest's wait, never the
    /// host's clock (the worker's own timeout is off in these tests).</summary>
    private static readonly TimeSpan s_hangGuard = TimeSpan.FromMinutes(2);

    [Fact]
    public void DrawWaitsForItsSceneToEnd()
    {
        _ = fixture;
        using var queue = new GpuQueue { SceneGateTimeoutMs = Timeout.Infinite };
        var backend = new CountingBackend();
        queue.Backend = backend;
        var gate = new SceneGate();
        long seq = queue.Submit(new DrawCommand { Gate = gate });
        Thread.Sleep(80); // gives a worker that would not wait the time to run the draw (only a false pass is possible)
        Assert.Equal(0, Volatile.Read(ref backend.Draws));
        Assert.True(queue.Completed < seq);
        gate.End();
        queue.WaitIdle();
        Assert.Equal(1, backend.Draws);
    }

    [Fact]
    public async Task GuestWaitingForTheDrawReleasesTheGate()
    {
        _ = fixture;
        using var queue = new GpuQueue { SceneGateTimeoutMs = Timeout.Infinite };
        var backend = new CountingBackend();
        queue.Backend = backend;
        var gate = new SceneGate();
        long seq = queue.Submit(new DrawCommand { Gate = gate });
        // e.g. sceGxmFinish inside the scene: with no timeout, only the wait itself can let the draw run
        await Task.Run(() => queue.WaitFor(seq)).WaitAsync(s_hangGuard);
        Assert.Equal(1, backend.Draws);
        Assert.True(gate.Ended);
    }

    /// <summary>The worker's own fallback: a scene nobody ends nor waits for stops holding its draw after the timeout.</summary>
    [Fact]
    public void AnAbandonedSceneReleasesItsDrawAfterTheTimeout()
    {
        _ = fixture;
        using var queue = new GpuQueue { SceneGateTimeoutMs = 1 };
        var backend = new CountingBackend();
        queue.Backend = backend;
        var gate = new SceneGate();
        queue.Submit(new DrawCommand { Gate = gate });
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref backend.Draws) == 1, s_hangGuard), "the held draw never ran");
        Assert.True(gate.Ended);
    }
}
