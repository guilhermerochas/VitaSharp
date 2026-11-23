using System.Diagnostics;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Util;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The hitch ledger's "fileIo" cause (the stall cause of a capture's timeline and report) is the host time of a file
/// read, not the storage model's wait (<see cref="IoTiming"/>). The modelled wait blocks the reading thread off its
/// core, as a read on the Vita's memory card does, while the other threads go on: a game's streaming thread
/// (Assassin's Creed III Liberation's Gear::AsynchGeneric, 1375 reads in 47 s) charged it to every frame, and a 98 ms
/// shader stall of the GPU worker showed up in the capture as "cause: fileIo 134.5 ms". Each read path (sceIoRead,
/// sceIoPread, the async request that runs it) still waits for the device: the guest clock moves on by the device time.
/// </summary>
[Collection("guest")]
public sealed class IoHitchLedgerTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint ORdOnly = 1;
    // 100 KB at 0.25 MB/s: 0.4 s of modelled device time, far above the host read of a 100 KB temporary file
    private const int FileBytes = 100_000;
    private const double SlowBytesPerSecond = 250_000;

    private readonly HleHarness _h = new(fixture, "io-ledger");

    public void Dispose() => _h.Dispose();

    private IoFileMgr Io => _h.Module<IoFileMgr>();

    private int OpenTestFile()
    {
        string dir = Path.Combine(_h.Root, "ux0", "data");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "stream.bin"), new byte[FileBytes]);
        int fd = Io.sceIoOpen("ux0:data/stream.bin", (int)ORdOnly, 0);
        Assert.True(fd > 0, $"open 0x{fd:X8}");
        return fd;
    }

    /// <summary>Runs <paramref name="read"/> with the slow device model; returns the fileIo ledger delta and the guest
    /// time the read took, both in ms.</summary>
    private (double LedgerMs, double GuestMs) TimedRead(Func<int, uint, int> read)
    {
        double ledgerMs = 0;
        double guestMs = 0;
        double savedSpeed = IoTiming.BytesPerSecond;
        _h.OnGuest(_ =>
        {
            int fd = OpenTestFile();
            uint buffer = _h.Alloc(FileBytes);
            IoTiming.BytesPerSecond = SlowBytesPerSecond;
            try
            {
                long ledgerBefore = FrameLedger.Total(FrameLedger.Cat.FileIo);
                long guestBefore = GuestClock.Ticks;
                Assert.Equal(FileBytes, read(fd, buffer));
                guestMs = (GuestClock.Ticks - guestBefore) * 1000.0 / Stopwatch.Frequency;
                ledgerMs = (FrameLedger.Total(FrameLedger.Cat.FileIo) - ledgerBefore) * 1000.0 / Stopwatch.Frequency;
            }
            finally
            {
                IoTiming.BytesPerSecond = savedSpeed;
            }
        });
        return (ledgerMs, guestMs);
    }

    private static void AssertDeviceWaitIsNotAHitch((double LedgerMs, double GuestMs) r)
    {
        double deviceMs = (IoTiming.LatencySeconds + FileBytes / SlowBytesPerSecond) * 1000;
        // the read still takes the device's time in guest time (the storage model is unchanged)
        Assert.True(r.GuestMs >= deviceMs * 0.99, $"the read did not wait for the device: {r.GuestMs:F1} ms of {deviceMs:F1}");
        // and the ledger counts the host read only, not the modelled wait
        Assert.True(r.LedgerMs < deviceMs / 2, $"the modelled device wait was charged as a fileIo hitch: {r.LedgerMs:F1} ms (device {deviceMs:F1} ms)");
    }

    [Fact]
    public void ReadChargesTheHostReadNotTheDeviceWait()
        => AssertDeviceWaitIsNotAHitch(TimedRead((fd, buffer) => Io.sceIoRead(fd, buffer, FileBytes)));

    [Fact]
    public void PreadChargesTheHostReadNotTheDeviceWait()
        => AssertDeviceWaitIsNotAHitch(TimedRead((fd, buffer) => Io.sceIoPread(fd, buffer, FileBytes, 0)));

    [Fact]
    public void AsyncReadChargesTheHostReadNotTheDeviceWait()
        => AssertDeviceWaitIsNotAHitch(TimedRead((fd, buffer) =>
        {
            int op = Call(Io, "sceIoReadAsync", fd, buffer, (uint)FileBytes, 0u);
            Assert.True(op > 0, $"sceIoReadAsync 0x{op:X8}");
            return Call(Io, "sceIoComplete", op);
        }));
}
