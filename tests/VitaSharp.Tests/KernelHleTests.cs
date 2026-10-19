using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Mutex = VitaSharp.Core.Kernel.Mutex;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Tests;

/// <summary>Package A (kernel) HLE: info structs, open/close handles, atomics, simple events, timers, cond SignalTo,
/// message pipe vectors, rw lock cancel, RTC calendar helpers.</summary>
[Collection("guest")]
public sealed class KernelHleTests(GuestMemoryFixture fx)
{
    static T Call<T>(object module, string name, params object[] args) =>
        (T)module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!;

    uint Alloc(uint size, string tag = "kt") { uint p = fx.Memory.Alloc(size, tag); GuestMemory.Fill(p, 0, size); return p; }

    [Fact]
    public void WriteInfoHonoursSize()
    {
        uint p = Alloc(0x40);
        Span<byte> full = stackalloc byte[0x3C];
        full.Clear();
        KernelInfo.Header(full, 0x1234, "sema", 7);
        KernelInfo.Put(full, 0x38, 9);
        GuestMemory.W32(p, 0x10);
        GuestMemory.W32(p + 0x10, 0xAAAAAAAA);
        Assert.Equal(0, KernelInfo.Write(p, full));
        Assert.Equal(0x10u, GuestMemory.R32(p));
        Assert.Equal(0x1234u, GuestMemory.R32(p + 4));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(p + 0x10)); // past size: untouched
        GuestMemory.W32(p, 0x3C);
        Assert.Equal(0, KernelInfo.Write(p, full));
        Assert.Equal(9u, GuestMemory.R32(p + 0x38));
        GuestMemory.W32(p, 0x40);
        Assert.Equal(SceError.NO_MEMORY, KernelInfo.Write(p, full));
        Assert.Equal(SceError.ILLEGAL_ADDR, KernelInfo.Write(0, full));
    }

    [Fact]
    public void OpenCloseByName()
    {
        var table = new ObjectTable();
        var heavy = new Mutex { Name = "m" };
        var light = new Mutex { Name = "lw", WorkArea = 0x100 };
        table.Register(heavy);
        table.Register(light);
        uint name = Alloc(64);
        GuestMemory.WriteCString(name, "m");
        Assert.Equal(heavy.Uid, KernelInfo.OpenByName<Mutex>(table, name, m => !m.IsLight));
        Assert.Equal(1, heavy.OpenCount);
        GuestMemory.WriteCString(name, "lw");
        Assert.Equal(SceError.UID_CANNOT_FIND_BY_NAME, KernelInfo.OpenByName<Mutex>(table, name, m => !m.IsLight));
        GuestMemory.WriteCString(name, new string('x', 40));
        Assert.Equal(SceError.UID_NAME_TOO_LONG, KernelInfo.OpenByName<Mutex>(table, name));
        Assert.Equal(SceError.ILLEGAL_ADDR, KernelInfo.OpenByName<Mutex>(table, 0));
        Assert.Equal(0, KernelInfo.CloseHandle<Mutex>(table, heavy.Uid, SceError.UNKNOWN_MUTEX_ID));
        Assert.Equal(0, heavy.OpenCount);
        Assert.Equal(0, KernelInfo.CloseHandle<Mutex>(table, heavy.Uid, SceError.UNKNOWN_MUTEX_ID)); // never negative
        Assert.Equal(0, heavy.OpenCount);
        Assert.Equal(SceError.UNKNOWN_MUTEX_ID, KernelInfo.CloseHandle<Mutex>(table, 0x123, SceError.UNKNOWN_MUTEX_ID));
        Assert.NotNull(table.Get<Mutex>(heavy.Uid)); // close never deletes
    }

    [Fact]
    public void UidClasses()
    {
        Assert.Equal(2, new Semaphore().UidClass);
        Assert.Equal(3, new EventFlag().UidClass);
        Assert.Equal(4, new Mutex().UidClass);
        Assert.Equal(10, new Mutex { WorkArea = 4 }.UidClass);
        Assert.Equal(5, new CondVar().UidClass);
        Assert.Equal(11, new CondVar { WorkArea = 4 }.UidClass);
        Assert.Equal(6, new TimerObject { Clock = () => 0 }.UidClass);
        Assert.Equal(7, new MsgPipe().UidClass);
        Assert.Equal(9, new ThreadEventHandler().UidClass);
        Assert.Equal(12, new RwLock().UidClass);
        Assert.Equal(13, new SimpleEvent().UidClass);
    }

    [Fact]
    public void Atomics()
    {
        var a = new Atomic();
        uint p = Alloc(16);
        GuestMemory.W32(p, 0x11223344);
        GuestMemory.Write<byte>(p + 1, 0x7F);
        Assert.Equal(-128, Call<int>(a, "sceKernelAtomicAddAndGet8", p + 1, 1)); // 8-bit wrap, sign-extended
        Assert.Equal(0x11228044u, GuestMemory.R32(p)); // neighbours untouched
        Assert.Equal(0x44, Call<int>(a, "sceKernelAtomicGetAndSet8", p, 0x55));
        Assert.Equal(0x11228055u, GuestMemory.R32(p));

        GuestMemory.W32(p + 4, 5);
        Assert.Equal(5, Call<int>(a, "sceKernelAtomicCompareAndSet32", p + 4, 4, 9)); // fails: returns old
        Assert.Equal(5u, GuestMemory.R32(p + 4));
        Assert.Equal(5, Call<int>(a, "sceKernelAtomicCompareAndSet32", p + 4, 5, 9));
        Assert.Equal(9u, GuestMemory.R32(p + 4));

        GuestMemory.W32(p + 4, 0);
        Assert.Equal(0, Call<int>(a, "sceKernelAtomicDecIfPositive32", p + 4));
        Assert.Equal(0u, GuestMemory.R32(p + 4)); // 0 is not positive at 32 bits
        GuestMemory.Write<short>(p + 8, 0);
        Assert.Equal(0, Call<int>(a, "sceKernelAtomicDecIfPositive16", p + 8));
        Assert.Equal((short)-1, GuestMemory.Read<short>(p + 8)); // ...but is at 16 bits (Vita3K)

        GuestMemory.W32(p + 4, 8);
        Assert.Equal(0, Call<int>(a, "sceKernelAtomicAddUnless32", p + 4, 1, 10));
        Assert.Equal(9u, GuestMemory.R32(p + 4));
        Assert.Equal(1, Call<int>(a, "sceKernelAtomicAddUnless32", p + 4, 5, 10));
        Assert.Equal(10u, GuestMemory.R32(p + 4)); // clamped to the limit

        GuestMemory.Write<long>(p + 8, 0x1_0000_0000L);
        Assert.Equal(0x1_0000_0000L, Call<long>(a, "sceKernelAtomicGetAndSub64", p + 8, 1L));
        Assert.Equal(0xFFFF_FFFFL, GuestMemory.Read<long>(p + 8));
        Assert.Equal(0xFFFF_FFF0L, Call<long>(a, "sceKernelAtomicClearAndGet64", p + 8, 0xFL));
    }

    [Fact]
    public void SimpleEventPulseOnlyReachesWaiters()
    {
        var e = new SimpleEvent { Attr = EventBits.AttrAutoReset, Pattern = EventBits.Create };
        uint res = Alloc(16);
        int r = int.MinValue;
        var waiter = new Thread(() => r = e.Wait(null, 0x01000000, new Ptr<uint>(res), new Ptr<ulong>(res + 8), default));
        waiter.Start();
        while (e.NumWaiters == 0) Thread.Sleep(1);
        Assert.Equal(0, e.SetOrPulse(0x01000000, 77, pulse: true));
        waiter.Join();
        Assert.Equal(0, r);
        Assert.Equal(0x01000000u, GuestMemory.R32(res));
        Assert.Equal(77ul, GuestMemory.Read<ulong>(res + 8));
        // the pulse is gone: a new wait times out
        uint tmo = Alloc(4);
        GuestMemory.W32(tmo, 2000);
        Assert.Equal(SceError.WAIT_TIMEOUT, e.Wait(null, 0x01000000, new Ptr<uint>(res), default, new Ptr<uint>(tmo)));
        Assert.Equal(0u, GuestMemory.R32(res));
        // set + auto reset consumes only the requested bits
        Assert.Equal(0, e.SetOrPulse(0x03000000, 1, pulse: false));
        Assert.Equal(0, e.Poll(0x01000000, new Ptr<uint>(res), default));
        Assert.Equal(0x01000000u, GuestMemory.R32(res));
        Assert.Equal(0, e.Poll(0x02000000, default, default));
        Assert.Equal(SceError.EVENT_COND, e.Poll(0x03000000, default, default));
        Assert.Equal(SceError.ILLEGAL_PATTERN, e.SetOrPulse(0x1, 0, false));
    }

    [Fact]
    public void SimpleEventCancelAndDelete()
    {
        var e = new SimpleEvent { Pattern = EventBits.Create };
        uint res = Alloc(4);
        int r = 0;
        var t = new Thread(() => r = e.Wait(null, 0x01000000, new Ptr<uint>(res), default, default));
        t.Start();
        while (e.NumWaiters == 0) Thread.Sleep(1);
        uint n = Alloc(4);
        e.Cancel(new Ptr<uint>(n));
        t.Join();
        Assert.Equal(SceError.WAIT_CANCEL, r);
        Assert.Equal(1u, GuestMemory.R32(n));
        Assert.Equal(EventBits.Error, GuestMemory.R32(res));

        t = new Thread(() => r = e.Wait(null, EventBits.Delete, new Ptr<uint>(res), default, default));
        t.Start();
        while (e.NumWaiters == 0) Thread.Sleep(1);
        e.Delete();
        t.Join();
        Assert.Equal(0, r); // waiting for the DELETE bit succeeds
        Assert.Equal(EventBits.Delete, GuestMemory.R32(res));
    }

    [Fact]
    public void TimerCountsAndFiresEvents()
    {
        ulong now = 1_000_000;
        var timer = new TimerObject { Clock = () => Volatile.Read(ref now) };
        Assert.Equal(0, timer.Start());
        Assert.Equal(1, timer.Start());
        now += 500;
        Assert.Equal(500ul, timer.Count());
        Assert.Equal(1, timer.Stop());
        Assert.Equal(0, timer.Stop());
        now += 1000;
        Assert.Equal(500ul, timer.Count());
        Assert.Equal(500ul, timer.SetTime(100));
        Assert.Equal(100ul, timer.Count());
        timer.Start();
        Assert.Equal(0, timer.SetEvent(0, 1000, repeat: true));
        Assert.Equal(SceError.EVENT_COND, timer.Poll(default, default));
        uint res = Alloc(4);
        now += 1500;
        Assert.Equal(0, timer.Poll(new Ptr<uint>(res), default));
        Assert.Equal(EventBits.Timer, GuestMemory.R32(res));
        Assert.Equal(SceError.ILLEGAL_TYPE, timer.SetEvent(2, 1, false));
        uint num = Alloc(4);
        Assert.Equal(0, timer.Cancel(new Ptr<uint>(num)));
        Assert.Equal(ulong.MaxValue, timer.NextEvent);
    }

    [Fact]
    public void TimerWaitWakesAtEvent()
    {
        var timer = new TimerObject { Clock = () => (ulong)VitaSharp.Core.Util.GuestClock.Microseconds };
        timer.Start();
        timer.SetEvent(0, 2000, repeat: false);
        uint res = Alloc(4), tmo = Alloc(4);
        GuestMemory.W32(tmo, 1_000_000);
        Assert.Equal(0, timer.Wait(new Ptr<uint>(res), default, new Ptr<uint>(tmo)));
        Assert.Equal(EventBits.Timer, GuestMemory.R32(res));
        Assert.True(GuestMemory.R32(tmo) > 500_000); // returned well before the timeout
    }

    [Fact]
    public void MsgPipeVectorKeepsOrder()
    {
        var pipe = new MsgPipe { BufferSize = 64 };
        uint a = Alloc(4), b = Alloc(8), r1 = Alloc(6), r2 = Alloc(6), res = Alloc(4);
        new byte[] { 1, 2, 3, 4 }.CopyTo(GuestMemory.Span(a, 4));
        new byte[] { 5, 6, 7, 8, 9, 10, 11, 12 }.CopyTo(GuestMemory.Span(b, 8));
        var send = new MsgPipe.Segments([a, b + 3, b], [4, 0, 8]);
        Assert.Equal(0, pipe.Send(send, MsgPipe.WaitAll, new Ptr<uint>(res), default, false));
        Assert.Equal(12u, GuestMemory.R32(res));
        var recv = new MsgPipe.Segments([r1, r2], [6, 6]);
        Assert.Equal(0, pipe.Receive(recv, MsgPipe.WaitAll, new Ptr<uint>(res), default, false));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, GuestMemory.Span(r1, 6).ToArray());
        Assert.Equal(new byte[] { 7, 8, 9, 10, 11, 12 }, GuestMemory.Span(r2, 6).ToArray());
    }

    [Fact]
    public void RwLockCancelWakesWaiters()
    {
        // distinct thread identities (only compared by reference)
        var reader = (GuestThread)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GuestThread));
        var writer = (GuestThread)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GuestThread));
        var l = new RwLock();
        Assert.Equal(0, l.TryLockRead(reader));
        int r = 0;
        var t = new Thread(() => r = l.LockWrite(writer, default));
        t.Start();
        while (l.WriteWaiters == 0) Thread.Sleep(1);
        uint nr = Alloc(4), nw = Alloc(4);
        Assert.Equal(0, l.Cancel(reader, new Ptr<uint>(nr), new Ptr<uint>(nw), false));
        t.Join();
        Assert.Equal(SceError.WAIT_CANCEL, r);
        Assert.Equal(1u, GuestMemory.R32(nw));
        Assert.Equal(0, l.Readers);
    }

    [Fact]
    public void RtcCalendar()
    {
        var rtc = new Rtc();
        Assert.Equal(60, Call<int>(rtc, "sceRtcGetDayOfYear", 2024, 2, 29));
        Assert.Equal(unchecked((int)0x80251083), Call<int>(rtc, "sceRtcGetDayOfYear", 2023, 2, 29));
        Assert.Equal(unchecked((int)0x80251082), Call<int>(rtc, "sceRtcGetDayOfYear", 2023, 13, 1));

        uint dt = Alloc(16), dos = Alloc(4);
        ushort[] f = [2012, 3, 15, 13, 45, 31];
        for (int i = 0; i < 6; i++) GuestMemory.Write(dt + (uint)i * 2, f[i]);
        Assert.Equal(0, Call<int>(rtc, "sceRtcGetDosTime", dt, new Ptr<uint>(dos)));
        uint v = GuestMemory.R32(dos);
        Assert.Equal(0, Call<int>(rtc, "sceRtcSetDosTime", dt, v));
        Assert.Equal((ushort)2012, GuestMemory.Read<ushort>(dt));
        Assert.Equal((ushort)45, GuestMemory.Read<ushort>(dt + 8));
        Assert.Equal((ushort)30, GuestMemory.Read<ushort>(dt + 10)); // 2-second resolution

        uint t0 = Alloc(8), t1 = Alloc(8);
        GuestMemory.Write(t1, (ulong)(new DateTime(2023, 1, 31, 0, 0, 0, DateTimeKind.Utc).Ticks / 10));
        Assert.Equal(0, Call<int>(rtc, "sceRtcTickAddMonths", new Ptr<ulong>(t0), new Ptr<ulong>(t1), 1));
        Assert.Equal(new DateTime(2023, 2, 28), new DateTime((long)GuestMemory.Read<ulong>(t0) * 10).Date);
        Assert.Equal(0, Call<int>(rtc, "sceRtcTickAddYears", new Ptr<ulong>(t0), new Ptr<ulong>(t1), -1));
        Assert.Equal(2022, new DateTime((long)GuestMemory.Read<ulong>(t0) * 10).Year);
        Assert.Equal(unchecked((int)0x80251001), Call<int>(rtc, "sceRtcTickAddWeeks", new Ptr<ulong>(0), new Ptr<ulong>(t1), 1));
    }
}
