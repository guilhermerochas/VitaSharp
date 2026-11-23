using VitaSharp.Core.Gpu;

namespace VitaSharp.Tests;

/// <summary>
/// A live graphics change (internal resolution, MSAA) replaces the backend on the GPU worker itself, at the first point
/// of the executed command stream where no scene is open (<see cref="GpuQueue.ReplaceBackendBetweenScenes"/>): the guest
/// threads are not held, so a thread that never reaches a safe point cannot refuse it. The swap never splits a scene
/// (the backend records a scene's draws into the render pass its BeginScene opened), work queued after the boundary
/// runs on the new backend, and a scene that never ends refuses the change without leaving it pending.
/// </summary>
[Collection("guest")]
public sealed class GpuQueueLiveReplaceTests(GuestMemoryFixture fixture)
{
    /// <summary>Records the commands it executed.</summary>
    private sealed class RecordingBackend : IGpuBackend
    {
        public readonly List<GpuCommand> Executed = [];
        public bool Disposed;

        public void Execute(GpuCommand command)
        {
            Assert.False(Disposed, "a disposed backend executed a command");
            Executed.Add(command);
        }

        public void Dispose() => Disposed = true;
    }

    private static BeginSceneCommand Begin() => new() { Width = 64, Height = 64 };

    /// <summary>The swap is asked while a scene is open in the worker's stream: it waits (the scene's later work still
    /// runs on the old backend) until the scene ends, and the next scene is the new backend's first command.</summary>
    [Fact]
    public async Task ASwapWaitsForTheOpenSceneAndTheNextSceneRunsOnTheNewBackend()
    {
        _ = fixture;
        using var queue = new GpuQueue();
        var first = new RecordingBackend();
        queue.Backend = first;
        BeginSceneCommand begin1 = Begin();
        var inScene1 = new FinishCommand();
        queue.Submit(begin1);
        queue.Submit(inScene1);
        queue.WaitIdle(); // the worker executed the BeginScene: a scene is open in its stream

        RecordingBackend? second = null;
        Task<string?> swap = Task.Run(() => queue.ReplaceBackendBetweenScenes(() => second = new RecordingBackend(), timeoutMs: 120_000));
        HleHarness.Until(() => queue.ReplacePending, "the swap request");
        var inScene2 = new FinishCommand();
        queue.Submit(inScene2);
        queue.WaitIdle();
        Assert.False(swap.IsCompleted, "the backend was replaced inside an open scene");
        Assert.Same(first, queue.Backend);

        var end1 = new EndSceneCommand();
        BeginSceneCommand begin2 = Begin();
        queue.Submit(end1);
        queue.Submit(begin2);
        Assert.Null(await swap.WaitAsync(TimeSpan.FromMinutes(2))); // the swap happens at the scene boundary
        queue.WaitIdle();

        Assert.Equal<GpuCommand>([begin1, inScene1, inScene2, end1], first.Executed);
        Assert.True(first.Disposed);
        Assert.NotNull(second);
        Assert.Same(second, queue.Backend);
        Assert.Equal<GpuCommand>([begin2], second!.Executed);
        Assert.False(queue.ReplacePending);
    }

    /// <summary>An idle queue between scenes (a paused game, a menu that draws nothing) takes the swap at once.</summary>
    [Fact]
    public void AnIdleQueueBetweenScenesSwapsAtOnce()
    {
        _ = fixture;
        using var queue = new GpuQueue();
        var first = new RecordingBackend();
        queue.Backend = first;
        queue.Submit(Begin());
        queue.Submit(new EndSceneCommand());
        queue.WaitIdle();

        RecordingBackend? second = null;
        Assert.Null(queue.ReplaceBackendBetweenScenes(() => second = new RecordingBackend(), timeoutMs: 120_000));
        Assert.True(first.Disposed);
        Assert.Same(second, queue.Backend);
    }

    /// <summary>A scene that stays open past the wait refuses the change with the reason, keeps the backend, and the
    /// withdrawn request is not made later when the scene ends (the caller already restored its options).</summary>
    [Fact]
    public void ASceneThatNeverEndsRefusesTheSwapAndLeavesNothingPending()
    {
        _ = fixture;
        using var queue = new GpuQueue();
        var first = new RecordingBackend();
        queue.Backend = first;
        queue.Submit(Begin());
        queue.WaitIdle();

        bool made = false;
        string? why = queue.ReplaceBackendBetweenScenes(() =>
        {
            made = true;
            return new RecordingBackend();
        }, timeoutMs: 50);
        Assert.NotNull(why);
        Assert.Contains("scene", why);
        Assert.False(queue.ReplacePending);

        var end = new EndSceneCommand();
        var after = new FinishCommand();
        queue.Submit(end);
        queue.Submit(after);
        queue.WaitIdle();
        Assert.False(made);
        Assert.False(first.Disposed);
        Assert.Same(first, queue.Backend);
        Assert.Equal(after, first.Executed[^1]);
    }

    /// <summary>One change at a time: a second request while one waits is refused, the first one goes on.</summary>
    [Fact]
    public async Task ASecondChangeWhileOneWaitsIsRefused()
    {
        _ = fixture;
        using var queue = new GpuQueue();
        queue.Backend = new RecordingBackend();
        queue.Submit(Begin());
        queue.WaitIdle();

        RecordingBackend? second = null;
        Task<string?> swap = Task.Run(() => queue.ReplaceBackendBetweenScenes(() => second = new RecordingBackend(), timeoutMs: 120_000));
        HleHarness.Until(() => queue.ReplacePending, "the first swap request");
        Assert.Contains("in progress", queue.ReplaceBackendBetweenScenes(() => new RecordingBackend(), timeoutMs: 120_000));

        queue.Submit(new EndSceneCommand());
        Assert.Null(await swap.WaitAsync(TimeSpan.FromMinutes(2)));
        Assert.Same(second, queue.Backend);
    }
}
