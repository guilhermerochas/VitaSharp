using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Save states and the HLE's service threads (guest threads whose body is the emulator's code, made when a library first
/// needs one): the GXM display queue's, the SceNpMatching2 callback thread, the SceNpCommon callout threads and the
/// process workers ("tworker") of the thread manager. Each follows the state (<see cref="IServiceThreadOwner"/>): a load
/// to before the thread existed lets the running one go (it ends: no thread keeps working outside the object table), a
/// load of a state that had one while this session has none gives the saved thread back with its saved UID, and the
/// thread then does its work once. Before, they were only matched by name with the running session's: the one made
/// after the save stayed alive, and the state that had one was refused ("the service thread ... does not exist in this
/// session yet"). Runs <see cref="SyntheticTitle"/>.
/// </summary>
[Collection("guest")]
public sealed class SaveStateServiceThreadTests : IDisposable
{
    private const int Uninitialized = unchecked((int)0x805B0000), AlreadyInitialized = unchecked((int)0x805B0001);
    private const string DisplayQueueName = "SceGxmDisplayQueue", MatchingName = "SceNpMatching2Callback", CalloutName = "SceNpCallout";
    private readonly HleHarness _harness;
    private readonly Emulator _emu;

    public SaveStateServiceThreadTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "ssservice");
        _emu = _harness.Emu;
        _emu.LoadTitle(SyntheticTitle.Install(_harness.Root));
        _emu.Start();
    }

    public void Dispose()
    {
        _emu.Stop();
        _harness.Dispose();
    }

    private string StateA => Path.Combine(_harness.Root, "a.vss");

    private string StateB => Path.Combine(_harness.Root, "b.vss");

    private Emulator Kernel => _emu.Vita.KernelProcessFor(_emu);

    private void Save(string path)
    {
        SaveStates.Save(_emu, path, timeoutMs: 60_000);
    }

    /// <summary>The live threads of <paramref name="process"/> named <paramref name="name"/>.</summary>
    private static List<GuestThread> Live(Emulator process, string name) => process.Kernel.Threads.Where(t => t.Name == name && t.IsLive).ToList();

    /// <summary>A function in user memory: adds 1 to the word its argument points to.</summary>
    private uint CountingFunction()
    {
        uint code = _harness.Alloc(0x10);
        GuestMemory.W32(code, 0xE5901000); // ldr r1, [r0]
        GuestMemory.W32(code + 4, 0xE2811001); // add r1, r1, #1
        GuestMemory.W32(code + 8, 0xE5801000); // str r1, [r0]
        GuestMemory.W32(code + 12, 0xE12FFF1E); // bx lr
        return code;
    }

    // ------------------------------------------------------------------ GXM display queue

    private Gxm Gxm => _emu.Hle.Modules.OfType<Gxm>().Single();

    private void InitGxm()
    {
        uint parameters = _harness.Alloc(0x14);
        GuestMemory.W32(parameters + 4, 2); // displayQueueMaxPendingCount
        GuestMemory.W32(parameters + 12, 4); // display callback data size
        GuestMemory.W32(parameters + 16, 0x200000);
        Assert.Equal(0, Call(Gxm, "sceGxmInitialize", parameters));
    }

    /// <summary>Queues one display entry (no callback) and waits until the display queue thread took it.</summary>
    private void FlipOnce()
    {
        uint syncOut = _harness.Alloc(8);
        Assert.Equal(0, Call(Gxm, "sceGxmSyncObjectCreate", new Ptr<uint>(syncOut)));
        uint first = GuestMemory.R32(syncOut);
        Assert.Equal(0, Call(Gxm, "sceGxmSyncObjectCreate", new Ptr<uint>(syncOut)));
        uint second = GuestMemory.R32(syncOut);
        uint data = _harness.Word(0x1234);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueAddEntry", first, second, data));
            Assert.Equal(0, Call(Gxm, "sceGxmDisplayQueueFinish"));
        });
    }

    /// <summary>
    /// The display queue thread (made by sceGxmInitialize) follows the state: a load to before GXM was initialised lets
    /// it go and GXM is uninitialised again; a load of the state that had it, while this session has none, gives it back
    /// with its saved UID and its queue, and it takes the next entry; a new initialisation after a load to before makes a
    /// new one.
    /// </summary>
    [Fact]
    public void TheGxmDisplayQueueThreadFollowsTheState()
    {
        Save(StateA);
        InitGxm();
        GuestThread saved = Assert.Single(Live(_emu, DisplayQueueName));
        int savedUid = saved.Uid;
        Assert.Equal(savedUid, Call(Gxm, "sceGxmGetDisplayQueueThreadIdInternal"));
        Save(StateB);

        SaveStates.Load(_emu, StateA);
        Until(() => !saved.IsLive, "the display queue thread made after the save to end");
        Assert.Empty(Live(_emu, DisplayQueueName));
        Assert.Equal(Uninitialized, Call(Gxm, "sceGxmGetDisplayQueueThreadIdInternal"));
        Assert.Equal(Uninitialized, Call(Gxm, "sceGxmDisplayQueueAddEntry", 1u, 2u, 0u));

        SaveStates.Load(_emu, StateB);
        GuestThread back = Assert.Single(Live(_emu, DisplayQueueName));
        Assert.Equal(savedUid, back.Uid);
        Assert.Same(back, _emu.Kernel.Objects.Get<GuestThread>(savedUid));
        Assert.Equal(savedUid, Call(Gxm, "sceGxmGetDisplayQueueThreadIdInternal"));
        Assert.Equal(AlreadyInitialized, Call(Gxm, "sceGxmInitialize", _harness.Alloc(0x14)));
        FlipOnce();
        Assert.Same(back, Assert.Single(Live(_emu, DisplayQueueName)));

        SaveStates.Load(_emu, StateA);
        Until(() => !back.IsLive, "the restored display queue thread to end");
        InitGxm();
        GuestThread fresh = Assert.Single(Live(_emu, DisplayQueueName));
        FlipOnce();
        Assert.Same(fresh, Assert.Single(Live(_emu, DisplayQueueName)));
        Assert.Equal(0, Call(Gxm, "sceGxmTerminate"));
    }

    // ------------------------------------------------------------------ SceNpMatching2's callback thread

    private HleCallbackThread Matching2 => _emu.Hle.Modules.OfType<NpMatching2>().Single().Callbacks;

    private void PostAndWait(HleCallbackThread callbacks, uint function, uint counter, uint expected)
    {
        callbacks.Post(_emu, 160, function, [counter]);
        Until(() => GuestMemory.R32(counter) == expected, "the callback to run");
    }

    /// <summary>
    /// The SceNpMatching2 callback thread (made with the first callback) follows the state: a load to before it lets
    /// it go; a load of the state that had it gives it back with its saved UID, and it runs the next callback once.
    /// </summary>
    [Fact]
    public void TheMatchingCallbackThreadFollowsTheState()
    {
        uint function = CountingFunction();
        uint counter = _harness.Word(0);
        Save(StateA);
        PostAndWait(Matching2, function, counter, 1);
        GuestThread saved = Assert.Single(Live(_emu, MatchingName));
        int savedUid = saved.Uid;
        Save(StateB);

        SaveStates.Load(_emu, StateA);
        Until(() => !saved.IsLive, "the callback thread made after the save to end");
        Assert.Empty(Live(_emu, MatchingName));
        Assert.Equal(0u, GuestMemory.R32(counter));

        SaveStates.Load(_emu, StateB);
        GuestThread back = Assert.Single(Live(_emu, MatchingName));
        Assert.Equal(savedUid, back.Uid);
        Assert.Equal(1u, GuestMemory.R32(counter));
        PostAndWait(Matching2, function, counter, 2);
        Assert.Same(back, Assert.Single(Live(_emu, MatchingName)));
        Assert.Equal(2u, GuestMemory.R32(counter));
    }

    // ------------------------------------------------------------------ SceNpCommon's callout threads

    private NpManager Np => _emu.Hle.Modules.OfType<NpManager>().Single();

    /// <summary>Queues a callout due at once on <paramref name="context"/> and delivers it.</summary>
    private void Callout(uint context, uint function, uint counter, uint expected)
    {
        uint item = _harness.Alloc(0x18);
        Assert.Equal(0, Call(Np, "NpCommonCalloutQueue", context, item, 0UL, function, counter));
        Assert.Equal(1, Call(Np, "DeliverDueCallouts", context, ulong.MaxValue));
        Until(() => GuestMemory.R32(counter) == expected, "the callout to run");
    }

    /// <summary>The thread of a callout context's library thread.</summary>
    private GuestThread? CalloutThreadOf(uint context)
    {
        var threads = (Dictionary<uint, HleCallbackThread>)typeof(NpManager)
            .GetField("_calloutThreads", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Np)!;
        lock (threads)
        {
            // the library thread's guest thread (read as a field: the same check runs on a build without the accessor)
            return threads.TryGetValue(context, out HleCallbackThread? thread)
                ? (GuestThread?)typeof(HleCallbackThread).GetField("_thread", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(thread)
                : null;
        }
    }

    /// <summary>
    /// The callout threads of two started contexts (both with the default name) follow the state: their first word holds
    /// the thread's UID; a load to before the threads lets them go (the contexts stay started, the next callout makes a
    /// new one); a load of the state that had them gives each context its own saved thread back, which runs its next
    /// callout once.
    /// </summary>
    [Fact]
    public void TheCalloutThreadsFollowTheState()
    {
        uint function = CountingFunction();
        uint first = _harness.Alloc(0x18), second = _harness.Alloc(0x18);
        uint firstCounter = _harness.Word(0), secondCounter = _harness.Word(0);
        Assert.Equal(0, Call(Np, "NpCommonCalloutStart", first, 0u, 0x4000u, 0x10000100, 0));
        Assert.Equal(0, Call(Np, "NpCommonCalloutStart", second, 0u, 0x4000u, 0x10000100, 0));
        Save(StateA);
        Callout(first, function, firstCounter, 1);
        Callout(second, function, secondCounter, 1);
        GuestThread firstThread = CalloutThreadOf(first)!, secondThread = CalloutThreadOf(second)!;
        Assert.NotSame(firstThread, secondThread);
        Assert.Equal((uint)firstThread.Uid, GuestMemory.R32(first));
        Assert.Equal((uint)secondThread.Uid, GuestMemory.R32(second));
        Save(StateB);

        SaveStates.Load(_emu, StateA);
        Until(() => !firstThread.IsLive && !secondThread.IsLive, "the callout threads made after the save to end");
        Assert.Empty(Live(_emu, CalloutName));
        Assert.Null(CalloutThreadOf(first));
        Assert.Null(CalloutThreadOf(second));

        SaveStates.Load(_emu, StateB);
        Assert.Equal(2, Live(_emu, CalloutName).Count);
        GuestThread firstBack = CalloutThreadOf(first)!, secondBack = CalloutThreadOf(second)!;
        Assert.Equal(firstThread.Uid, firstBack.Uid);
        Assert.Equal(secondThread.Uid, secondBack.Uid);
        Callout(second, function, secondCounter, 2);
        Callout(first, function, firstCounter, 2);
        Assert.Same(firstBack, CalloutThreadOf(first));
        Assert.Same(secondBack, CalloutThreadOf(second));
        Assert.Equal(2, Live(_emu, CalloutName).Count);

        // back to before the threads: the started contexts make a new one with their next callout, which runs it once
        SaveStates.Load(_emu, StateA);
        Until(() => !firstBack.IsLive && !secondBack.IsLive, "the restored callout threads to end");
        Callout(first, function, firstCounter, 1);
        Assert.Single(Live(_emu, CalloutName));
        Assert.Equal(0u, GuestMemory.R32(secondCounter));
    }

    // ------------------------------------------------------------------ the thread manager's process workers

    private ThreadmgrKernel KernelThreadmgr => Kernel.Hle.Modules.OfType<ThreadmgrKernel>().Single();

    /// <summary>A process object block in kernel memory, as the process manager passes it.</summary>
    private uint ProcessObject()
    {
        uint process;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            process = _emu.Memory.Alloc(0x400, "test:process object", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(process, 0, 0x400);
        GuestMemory.W32(process + 0x64, (uint)KernelSpace.KernelPid);
        return process;
    }

    /// <summary>The worker waits on its process's event flag.</summary>
    private static void UntilWaitingOn(GuestThread worker, int flag)
    {
        Until(() => worker.CurrentWaitSync is EventFlag eventFlag && eventFlag.Uid == flag, "the worker to wait on its process's event flag");
    }

    /// <summary>
    /// A process worker (the "tworker" thread NID_B46502E6 makes for a new process) follows the state: a load to before
    /// the process lets it go; a load of the state that had it gives it back with its saved UID, waiting on the saved
    /// process's event flag, and the end of the process's worker (NID_5A23A1BE) ends it.
    /// </summary>
    [Fact]
    public void AProcessWorkerFollowsTheState()
    {
        uint process = ProcessObject();
        Save(StateA);
        using (ProcessScope.Enter(Kernel))
        {
            Assert.Equal(0, Call(KernelThreadmgr, "NID_B46502E6", process));
        }

        int workerUid = (int)GuestMemory.R32(process + 0x330), flag = (int)GuestMemory.R32(process + 0x334);
        GuestThread saved = Kernel.Kernel.Objects.Get<GuestThread>(workerUid)!;
        Assert.NotNull(saved.HostBody);
        UntilWaitingOn(saved, flag);
        Save(StateB);

        SaveStates.Load(_emu, StateA);
        Until(() => !saved.IsLive, "the worker made after the save to end");
        Assert.Empty(Live(Kernel, saved.Name));
        Assert.Null(Kernel.Kernel.Objects.Get<GuestThread>(workerUid));

        SaveStates.Load(_emu, StateB);
        GuestThread back = Assert.Single(Live(Kernel, saved.Name));
        Assert.Equal(workerUid, back.Uid);
        Assert.Same(back, Kernel.Kernel.Objects.Get<GuestThread>(workerUid));
        UntilWaitingOn(back, flag);
        using (ProcessScope.Enter(Kernel))
        {
            Invoke(KernelThreadmgr, "NID_5A23A1BE", process);
        }

        Assert.False(back.IsLive);
        Assert.Empty(Live(Kernel, saved.Name));
    }
}
