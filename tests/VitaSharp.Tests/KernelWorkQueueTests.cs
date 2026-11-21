using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// ksceKernelEnqueueWorkQueue as FW 3.74 threadmgr has it (decompiled, study only): a task on the global work queue
/// (UID 0x10023) is called by the queue's worker thread with its argument, in the order the tasks came; the call
/// answers the task's UID, and any other queue UID is SCE_KERNEL_ERROR_UNKNOWN_WORK_QUEUE_ID. The waits end on the
/// tasks' own writes (bounded only against a hang), never on the host's clock.
/// </summary>
[Collection("guest")]
public sealed class KernelWorkQueueTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int UnknownWorkQueueId = unchecked((int)0x80028301);

    private readonly HleHarness _harness = new(fixture, "kworkq");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    /// <summary>ARM code: r1 = *r0 (the shared counter's address), r2 = *r1 + 1, *r1 = r2, r0[1] = r2 (the order the
    /// task ran in), return.</summary>
    private uint CountingTask()
    {
        uint code = _harness.Alloc(0x20);
        GuestMemory.W32(code, 0xE5901000); // ldr r1, [r0]
        GuestMemory.W32(code + 4, 0xE5912000); // ldr r2, [r1]
        GuestMemory.W32(code + 8, 0xE2822001); // add r2, r2, #1
        GuestMemory.W32(code + 12, 0xE5812000); // str r2, [r1]
        GuestMemory.W32(code + 16, 0xE5802004); // str r2, [r0, #4]
        GuestMemory.W32(code + 20, 0xE12FFF1E); // bx lr
        return code;
    }

    [Fact]
    public void TasksOfTheGlobalQueueRunInOrderOnTheWorker()
    {
        uint task = CountingTask();
        uint counter = _harness.Word(0);
        uint[] arguments = new uint[3];
        for (int i = 0; i < arguments.Length; i++)
        {
            arguments[i] = _harness.Alloc(8);
            GuestMemory.W32(arguments[i], counter);
        }

        _harness.OnGuest(_ =>
        {
            foreach (uint argument in arguments)
            {
                Assert.True(Call(Threadmgr, "ksceKernelEnqueueWorkQueue", 0x10023, "kworkq-task", task, argument) > 0);
            }
        });
        Until(() => (int)Invoke(Threadmgr, "get_WorkTasksRun")! == arguments.Length, "the worker to run the three tasks");
        Assert.Equal(3u, GuestMemory.R32(counter));
        for (int i = 0; i < arguments.Length; i++)
        {
            Assert.Equal((uint)(i + 1), GuestMemory.R32(arguments[i] + 4));
        }
    }

    private void RunOneTask(uint task, uint argument, int expectedRun)
    {
        _harness.OnGuest(_ => Assert.True(Call(Threadmgr, "ksceKernelEnqueueWorkQueue", 0x10023, "kworkq-task", task, argument) > 0));
        Until(() => Threadmgr.WorkTasksRun == expectedRun, "the worker to run the task");
    }

    /// <summary>An idle worker sleeps until something signals it: no wake-up of its own while no task comes (it looked
    /// at the queue every 50 ms of host time), one per task after that, and the tasks still run in order. The pause
    /// only gives a polling worker the time to show itself; the correct worker has no wake-up whatever the host's
    /// speed.</summary>
    [Fact]
    public void AnIdleWorkerSleepsUntilATaskComes()
    {
        uint task = CountingTask();
        uint counter = _harness.Word(0);
        uint argument = _harness.Alloc(8);
        GuestMemory.W32(argument, counter);
        RunOneTask(task, argument, 1);
        Until(WorkerWaiting, "the worker to sleep");
        int idle = Threadmgr.WorkWakeups;
        Thread.Sleep(300);
        Assert.Equal(idle, Threadmgr.WorkWakeups);

        RunOneTask(task, argument, 2);
        Assert.Equal(2u, GuestMemory.R32(counter));
        Assert.Equal(2u, GuestMemory.R32(argument + 4));
    }

    private VitaSharp.Core.Kernel.GuestThread Worker()
    {
        VitaSharp.Core.Emulator kernel = _harness.Emu.Vita.KernelProcess ?? _harness.Emu;
        return Assert.Single(kernel.Kernel.Threads, t => t.Name == "SceKernelGlobalWorkQueue");
    }

    private bool WorkerWaiting() => Worker().BlockedInHost;

    /// <summary>A task the object table got without an enqueue (a state load restores the waiting tasks) runs once
    /// the load arms its scheduled work, and a stop request ends the sleeping worker.</summary>
    [Fact]
    public void ALoadedTaskRunsAndAStopEndsTheSleepingWorker()
    {
        uint task = CountingTask();
        uint counter = _harness.Word(0);
        uint argument = _harness.Alloc(8);
        GuestMemory.W32(argument, counter);
        RunOneTask(task, argument, 1);
        Until(WorkerWaiting, "the worker to sleep");

        var loaded = new VitaSharp.Core.Kernel.KernelData { Name = "kworkq-loaded", Kind = "worktask", Address = task, Aux = argument };
        using (VitaSharp.Core.Kernel.ProcessScope.Enter(VitaSharp.Core.Kernel.KernelSpace.KernelPid))
        {
            _harness.Emu.Kernel.Objects.Register(loaded);
        }

        ((VitaSharp.Core.SaveState.ISnapshotTimers)Threadmgr).RearmTimers();
        Until(() => Threadmgr.WorkTasksRun == 2, "the loaded task to run");
        Assert.Equal(2u, GuestMemory.R32(counter));

        VitaSharp.Core.Kernel.GuestThread worker = Worker();
        Until(WorkerWaiting, "the worker to sleep again");
        worker.RequestStop();
        Until(() => !worker.IsLive, "the worker to end on the stop request");
    }

    [Fact]
    public void AnotherQueueIsUnknown()
    {
        _harness.OnGuest(_ => Assert.Equal(UnknownWorkQueueId, Call(Threadmgr, "ksceKernelEnqueueWorkQueue", 0x10025, "x", 0x81000001u, 0u)));
    }
}
