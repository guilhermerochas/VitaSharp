using System.Collections.Concurrent;
using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Asynchronous I/O runs on the I/O scheduler, as the firmware's iofilemgr does ("SceIoScheduler"): sceIoXxxAsync
/// returns at once and the request completes when the storage model's device time (<see cref="IoTiming"/>) ends, in
/// guest time. Before, the whole request ran inside the call with the device wait, so a game reading asynchronously on
/// its main thread stood still for the card's time (100 ms for 700 KB at 7 MB/s). Guest time is the test's
/// (<see cref="ControlledGuestClock"/>): a request ends exactly when the clock passes its device time, never by the
/// host's speed. Covers the completion order (submission order, so also per fd), fast loading (no device time) and a
/// save state taken with a request in the device.
/// </summary>
[Collection("guest")]
public sealed class IoAsyncSchedulerTests : IDisposable
{
    private const uint ORdOnly = 1;
    private const int FileBytes = 700_000;
    private const double CardBytesPerSecond = 7e6;

    private readonly GuestMemoryFixture _fixture;
    private readonly HleHarness _h;
    private readonly double _savedSpeed = IoTiming.BytesPerSecond;
    private readonly byte[] _data = MakeData();

    public IoAsyncSchedulerTests(GuestMemoryFixture fixture)
    {
        _fixture = fixture;
        _h = new HleHarness(fixture, "io-async");
        IoTiming.BytesPerSecond = CardBytesPerSecond;
        IoTiming.ResetDeviceTimeline();
        string dir = Path.Combine(_h.Root, "ux0", "data");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "stream.bin"), _data);
    }

    public void Dispose()
    {
        IoTiming.BytesPerSecond = _savedSpeed;
        _h.Dispose();
    }

    private IoFileMgr Io => _h.Module<IoFileMgr>();

    private static byte[] MakeData()
    {
        byte[] data = new byte[FileBytes];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 7 + (i >> 9));
        }

        return data;
    }

    /// <summary>The device time of one read of <paramref name="bytes"/> (latency + size / throughput), microseconds.</summary>
    private static long DeviceUs(int bytes) => (long)Math.Ceiling((IoTiming.LatencySeconds + bytes / CardBytesPerSecond) * 1e6);

    private static IoAsyncOp Op(IoFileMgr io, int opid)
    {
        var ops = (ConcurrentDictionary<int, IoAsyncOp>)typeof(IoFileMgr).GetField("_ops", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(io)!;
        return ops[opid];
    }

    private int Open()
    {
        int fd = 0;
        _h.OnGuest(_ => fd = Io.sceIoOpen("ux0:data/stream.bin", (int)ORdOnly, 0));
        Assert.True(fd > 0, $"open 0x{fd:X8}");
        return fd;
    }

    private uint Param()
    {
        uint param = _h.Alloc(0x18);
        GuestMemory.Write(param, -1L);
        return param;
    }

    [Fact]
    public void AsyncReadReturnsAtOnceAndCompletesAfterTheDeviceTime()
    {
        using var clock = ControlledGuestClock.Stop();
        int fd = Open();
        uint buffer = _h.Alloc(FileBytes), param = Param();
        int opid = 0;
        long submitted = 0, returned = 0;
        _h.OnGuest(_ =>
        {
            submitted = GuestClock.Ticks;
            opid = Call(Io, "sceIoReadAsync", fd, buffer, (uint)FileBytes, param);
            returned = GuestClock.Ticks;
        }, "io-async-main");
        Assert.True(opid > 0, $"sceIoReadAsync 0x{opid:X8}");
        Assert.Equal(submitted, returned);

        // the host read is done off the guest thread; the request is still in the device
        Io.WaitForAsyncHostWork();
        IoAsyncOp op = Op(Io, opid);
        Assert.False(op.Done, "the request completed inside the call, without the device time");
        Assert.Equal(-1L, GuestMemory.Read<long>(param));
        uint m = _h.Alloc(16);
        Assert.Equal(SceError.EVENT_COND, op.PollEvent(IoAsyncOp.CompletionBit, new Ptr<uint>(m), new Ptr<ulong>(m + 8)));

        long deviceUs = DeviceUs(FileBytes);
        clock.Advance(deviceUs - 50);
        Assert.False(op.Done, "the request completed before its device time");

        clock.Advance(100);
        Until(() => op.Done, "the request's completion at its device time");
        Assert.Equal(FileBytes, GuestMemory.Read<long>(param));
        Assert.Equal(0, op.PollEvent(IoAsyncOp.CompletionBit, new Ptr<uint>(m), new Ptr<ulong>(m + 8)));
        int result = 0;
        _h.OnGuest(_ => result = Call(Io, "sceIoComplete", opid));
        Assert.Equal(FileBytes, result);
        Assert.True(GuestMemory.ReadSpan(buffer, FileBytes).SequenceEqual(_data));
    }

    [Fact]
    public void RequestsCompleteInSubmissionOrderAtTheirDeviceTimes()
    {
        using var clock = ControlledGuestClock.Stop();
        int fd = Open();
        const int Half = FileBytes / 2;
        uint first = _h.Alloc(Half), second = _h.Alloc(Half), stat = _h.Alloc(0x60);
        int a = 0, b = 0, c = 0;
        _h.OnGuest(_ =>
        {
            a = Call(Io, "sceIoPreadAsync", fd, first, (uint)Half, 0L, 0u);
            // no device time of its own: it still ends after the read queued before it
            b = Call(Io, "sceIoGetstatByFdAsync", fd, stat, 0u);
            c = Call(Io, "sceIoPreadAsync", fd, second, (uint)Half, (long)Half, 0u);
        });
        Io.WaitForAsyncHostWork();
        IoAsyncOp readA = Op(Io, a), statB = Op(Io, b), readC = Op(Io, c);
        Assert.False(readA.Done || statB.Done || readC.Done, "a request completed without its device time");

        clock.Advance(DeviceUs(Half) + 10);
        Until(() => statB.Done, "the stat after the first read");
        Assert.True(readA.Done, "the stat completed before the read submitted before it");
        Assert.False(readC.Done, "the second read completed before its device time (the device serves one read at a time)");

        clock.Advance(DeviceUs(Half) + 10);
        Until(() => readC.Done, "the second read");
        Assert.Equal(Half, readA.Result);
        Assert.Equal(Half, readC.Result);
        Assert.True(GuestMemory.ReadSpan(second, Half).SequenceEqual(_data.AsSpan(Half)));
        _h.OnGuest(_ =>
        {
            Assert.Equal(Half, Call(Io, "sceIoComplete", a));
            Assert.Equal(0, Call(Io, "sceIoComplete", b));
            Assert.Equal(Half, Call(Io, "sceIoComplete", c));
        });
    }

    [Fact]
    public void FastLoadingCompletesWhenTheHostWorkIsDone()
    {
        using var clock = ControlledGuestClock.Stop();
        IoTiming.BytesPerSecond = 0;
        int fd = Open();
        uint buffer = _h.Alloc(FileBytes);
        int opid = 0;
        _h.OnGuest(_ => opid = Call(Io, "sceIoReadAsync", fd, buffer, (uint)FileBytes, 0u));
        Io.WaitForAsyncHostWork();
        Assert.True(Op(Io, opid).Done, "with no device time the request ends with its host work");
    }

    [Fact]
    public void SaveStateKeepsARequestInTheDevice()
    {
        using var clock = ControlledGuestClock.Stop();
        using var restored = new HleHarness(_fixture, "io-async-b");
        int fd = Open();
        uint buffer = _h.Alloc(FileBytes), param = Param();
        int opid = 0;
        _h.OnGuest(_ => opid = Call(Io, "sceIoReadAsync", fd, buffer, (uint)FileBytes, param));
        Io.WaitForAsyncHostWork();
        Assert.False(Op(Io, opid).Done);

        // the state holds the request with its result and completion time
        byte[] kernel = KernelSnapshot.Save(_h.Emu, []).Data.ToArray();
        var module = new SnapshotWriter();
        ((ISnapshotState)Io).SaveState(module);
        KernelSnapshot.Load(restored.Emu, new SnapshotReader(kernel) { Objects = restored.Emu.Kernel.Objects });
        IoFileMgr loadedIo = restored.Module<IoFileMgr>();
        ((ISnapshotState)loadedIo).LoadState(new SnapshotReader(module.Data.ToArray()) { Objects = restored.Emu.Kernel.Objects });

        // the saved session goes away (its request must not be the one that writes the param below)
        ((IoScheduler)typeof(IoFileMgr).GetField("_scheduler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Io)!).Reset();
        IoAsyncOp loaded = Op(loadedIo, opid);
        Assert.False(loaded.Done, "the loaded request completed before its device time");
        GuestMemory.Write(param, -1L);
        clock.Advance(DeviceUs(FileBytes) + 10);
        Until(() => loaded.Done, "the loaded request's completion");
        Assert.Equal(FileBytes, GuestMemory.Read<long>(param));
        int result = 0;
        restored.OnGuest(_ => result = Call(loadedIo, "sceIoComplete", opid));
        Assert.Equal(FileBytes, result);
    }

    [Fact]
    public void SaveStateRetriesWhileARequestIsNotServedYet()
    {
        // a request still queued (its host work not done) cannot be in a state: the save retries until it is served
        var op = new IoAsyncOp { Name = "SceIoAsyncEvent", Done = false, State = IoAsyncState.Queued };
        _h.Emu.Kernel.Objects.Register(op);
        Assert.Throws<SnapshotRetryException>(() => KernelSnapshot.Save(_h.Emu, []));
        _h.Emu.Kernel.Objects.Remove(op.Uid);
    }
}
