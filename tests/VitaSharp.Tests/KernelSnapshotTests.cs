using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Save states keep the kernel object table: objects a game made through the thread manager (a dormant thread, a
/// semaphore, event flags, a mutex with its condition variable, a message pipe holding data, a reader-writer lock, a
/// callback, a running timer, simple events and a memory block) are saved, loaded into a second emulator and saved
/// again, and the two must be the same; the loaded objects answer the same Get*Info / poll calls.
/// </summary>
[Collection("guest")]
public sealed class KernelSnapshotTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _saved = new(fixture, "ksnap-a"), _restored = new(fixture, "ksnap-b");

    public void Dispose()
    {
        _saved.Dispose();
        _restored.Dispose();
    }

    [Fact]
    public void ObjectTableRoundTrips()
    {
        ThreadMgr tm = _saved.Module<ThreadMgr>();
        var ids = new Dictionary<string, int>();
        GuestRun run = _saved.Start(_ =>
        {
            ids["thread"] = Call(tm, "sceKernelCreateThread", "dormant", 0x81000001u, 0x10000100, 0x4000, 0u, 0, 0u);
            ids["sema"] = Call(tm, "sceKernelCreateSema", "sema", 0x1u, 2, 5, 0u);
            Call(tm, "sceKernelSignalSema", ids["sema"], 1);
            ids["evf"] = Call(tm, "sceKernelCreateEventFlag", "evf", EventFlag.AttrMulti, 0x5u, 0u);
            Call(tm, "sceKernelSetEventFlag", ids["evf"], 0x30u);
            ids["mutex"] = Call(tm, "sceKernelCreateMutex", "mtx", 0x2u, 0, 0u);
            ids["cond"] = Call(tm, "sceKernelCreateCond", "cond", 0u, ids["mutex"], 0u);
            ids["pipe"] = Call(tm, "sceKernelCreateMsgPipe", "pipe", 0x40u, 0u, 0x100u, 0u);
            uint data = _saved.Str("message in the pipe");
            Call(tm, "sceKernelSendMsgPipe", ids["pipe"], data, 19u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0));
            ids["rw"] = Call(tm, "sceKernelCreateRWLock", "rw", 0u, 0u);
            ids["cb"] = Call(tm, "sceKernelCreateCallback", "cb", 0u, 0x81000003u, 0x55u);
            ids["timer"] = Call(tm, "sceKernelCreateTimer", "timer", 0u, 0u);
            Call(tm, "sceKernelStartTimer", ids["timer"]);
            ids["sev"] = Call(tm, "sceKernelCreateSimpleEvent", _saved.Str("sev"), EventBits.AttrOpenable, 0u, 0u);
        }, "ksnap-maker");
        _saved.Join(run);
        // the thread that made them has ended; it stays in the table (the callback belongs to it)
        Assert.All(ids, kv => Assert.True(kv.Value > 0, kv.Key));

        byte[] first = KernelSnapshot.Save(_saved.Emu, []).Data.ToArray();
        List<GuestThread> live = KernelSnapshot.Load(_restored.Emu, new SnapshotReader(first) { Objects = _restored.Emu.Kernel.Objects });
        Assert.Empty(live); // the dormant thread is not started
        byte[] again = KernelSnapshot.Save(_restored.Emu, []).Data.ToArray();
        Assert.True(first.AsSpan().SequenceEqual(again), $"{first.Length} bytes saved, {again.Length} after a load");

        // the loaded objects are the saved ones, under the same UIDs
        ThreadMgr tb = _restored.Module<ThreadMgr>();
        _restored.OnGuest(_ =>
        {
            Assert.Equal(0, Call(tb, "sceKernelPollSema", ids["sema"], 3));
            Assert.Equal(SceError.SEMA_ZERO, Call(tb, "sceKernelPollSema", ids["sema"], 1));
            uint pat = _restored.Word(0);
            Assert.Equal(0, Call(tb, "sceKernelPollEventFlag", ids["evf"], 0x35u, 1u, new Ptr<uint>(pat)));
            Assert.Equal(0x35u, GuestMemory.R32(pat));
            uint buf = _restored.Alloc(32), res = _restored.Word(0);
            Assert.Equal(0, Call(tb, "sceKernelTryReceiveMsgPipe", ids["pipe"], buf, 32u, MsgPipe.WaitAsap, new Ptr<uint>(res)));
            Assert.Equal(19u, GuestMemory.R32(res));
            Assert.Equal("message in the pipe", GuestMemory.ReadCString(buf));
            Assert.Equal(0, Call(tb, "sceKernelGetCallbackCount", ids["cb"]));
            Assert.Equal(0, Call(tb, "sceKernelLockMutex", ids["mutex"], 1, new Ptr<uint>(0)));
            Assert.Equal(0, Call(tb, "sceKernelUnlockMutex", ids["mutex"], 1));
            Assert.Equal(7, Call(tb, "sceKernelGetThreadmgrUIDClass", ids["pipe"]));
        });
    }
}
