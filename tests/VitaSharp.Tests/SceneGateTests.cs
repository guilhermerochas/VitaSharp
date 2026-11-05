using VitaSharp.Core.Gpu;

namespace VitaSharp.Tests;

/// <summary>The GPU worker holds a draw of an open scene (its bound uniform buffers are read late) until the scene ends,
/// unless a guest waits for that draw.</summary>
[Collection("guest")]
public sealed class SceneGateTests(GuestMemoryFixture fx)
{
    sealed class CountingBackend : IGpuBackend
    {
        public int Draws;
        public long PresentCount => 0;
        public byte[]? ExportDumpState() => null;
        public void Execute(GpuCommand command) { if (command is DrawCommand) Interlocked.Increment(ref Draws); }
        public void Dispose() { }
    }

    [Fact]
    public void DrawWaitsForItsSceneToEnd()
    {
        _ = fx;
        using var queue = new GpuQueue();
        var backend = new CountingBackend();
        queue.Backend = backend;
        var gate = new SceneGate();
        long seq = queue.Submit(new DrawCommand { Gate = gate });
        Thread.Sleep(80);
        Assert.Equal(0, Volatile.Read(ref backend.Draws));
        Assert.True(queue.Completed < seq);
        gate.End();
        queue.WaitIdle();
        Assert.Equal(1, backend.Draws);
    }

    [Fact]
    public void GuestWaitingForTheDrawReleasesTheGate()
    {
        _ = fx;
        using var queue = new GpuQueue();
        var backend = new CountingBackend();
        queue.Backend = backend;
        var gate = new SceneGate();
        long seq = queue.Submit(new DrawCommand { Gate = gate });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        queue.WaitFor(seq); // e.g. sceGxmFinish inside the scene: must not wait for the 250 ms timeout
        Assert.True(sw.ElapsedMilliseconds < 200, $"waited {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, backend.Draws);
        Assert.True(gate.Ended);
    }
}
