using System.Diagnostics;
using VitaSharp.Core;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Kernel;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// A thread's gaps between two HLE calls (GuestThread.HleGaps.cs), with a controlled clock: each keeps its host time,
/// the pacing sleep in it and the guest cycles run in it, and what those do not explain is the host's (preempted, or
/// slower than the emulated CPU); the longest one is kept, a gap across a pause of the emulation is not counted, and
/// the thread's description (the watchdog's STALL) shows the open gap and the longest. Before, a STALL listed where
/// each thread was, not how long it had gone without a call nor why.
/// </summary>
[Collection("guest")]
public sealed class HleGapTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "hlegap");

    public void Dispose() => _harness.Dispose();

    private GuestThread NewThread() => _harness.Emu.Kernel.CreateThread("HleGapTest", 0, 0x40, 0x4000, 0, 0);

    [Fact]
    public void TheLongestGapKeepsItsPacingAndItsGuestCycles()
    {
        GuestThread thread = NewThread();
        long rate = CpuCore.InstructionsPerSecond;
        thread.NoteHleReturn(1_000);
        thread.Core.BlockCycles += rate / 5; // 200 ms of guest time at the emulated clock
        thread.Core.PacingTicks += Stopwatch.Frequency / 100; // 10 ms of pacing sleep
        thread.NoteHleEntry(1_250);
        GuestThread.HleGap gap = thread.LongestHleGap;
        Assert.Equal(250, gap.Milliseconds);
        Assert.Equal(10, gap.PacingMilliseconds);
        Assert.Equal(rate / 5, gap.GuestCycles);
        Assert.Equal(1_250, gap.EndedAt);
        Assert.Equal(rate > 0 ? 40 : -1, gap.HostLateMilliseconds);

        // a shorter one does not replace it
        thread.NoteHleReturn(1_300);
        thread.NoteHleEntry(1_350);
        Assert.Equal(250, thread.LongestHleGap.Milliseconds);

        // a longer one does, with its own pacing and cycles
        thread.NoteHleReturn(2_000);
        thread.NoteHleEntry(2_400);
        Assert.Equal(400, thread.LongestHleGap.Milliseconds);
        Assert.Equal(0, thread.LongestHleGap.PacingMilliseconds);
        Assert.Equal(0, thread.LongestHleGap.GuestCycles);
        Assert.Equal(rate > 0 ? 400 : -1, thread.LongestHleGap.HostLateMilliseconds);
    }

    [Fact]
    public void AGapAcrossAPauseIsNotCounted()
    {
        GuestThread thread = NewThread();
        thread.NoteHleReturn(10_000);
        Assert.Equal(500, thread.OpenHleGap(10_500)!.Value.Milliseconds);
        bool paused = EmulationPause.Pause("hle gap test");
        try
        {
            Assert.Null(thread.OpenHleGap(11_000));
        }
        finally
        {
            if (paused)
            {
                EmulationPause.Resume("hle gap test");
            }
        }

        thread.NoteHleEntry(60_000);
        Assert.Equal(0, thread.LongestHleGap.Milliseconds);
        thread.NoteHleReturn(60_100);
        thread.NoteHleEntry(60_150);
        Assert.Equal(50, thread.LongestHleGap.Milliseconds);
    }

    [Fact]
    public void TheDescriptionShowsTheOpenAndTheLongestGap()
    {
        GuestThread thread = NewThread();
        Assert.DoesNotContain("hle", thread.Describe());
        long now = Environment.TickCount64;
        thread.NoteHleReturn(now - 3_000);
        thread.NoteHleEntry(now - 1_000);
        thread.NoteHleReturn(now - 900);
        string description = thread.Describe();
        Assert.Contains("longest-hle-gap=2000 ms", description);
        Assert.Contains("since-hle=", description);
    }
}
