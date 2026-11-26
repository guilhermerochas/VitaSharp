using System.Diagnostics;
using System.Runtime.InteropServices;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// PC model of a phone's slow cluster during a level load (Redmi Note 14: 6 x A55 shared by the game's helper threads,
/// 4 shader workers, 2 JIT precompile threads and the texture encodes): a guest helper thread with a fixed amount of
/// work per 16.7 ms frame shares two pinned CPUs with four busy background threads (real ASTC encodes), first at the
/// same priority (what Thread.Priority gave on Linux/Android: nothing), then lowered by
/// <see cref="HostThreads.LowerCurrent"/>. Prints how late the helper's frames end. Windows models the priorities with
/// its own classes (a below-normal thread only runs on a core no normal thread wants), Linux with nice values (shares
/// of 75-90%); the direction is the same. Slow, Windows and Linux only (thread affinity); numbers printed, not asserted.
/// </summary>
[Trait("Category", "Slow")]
public sealed class BackgroundThreadModelBenchmark(ITestOutputHelper output)
{
    private const int Frames = 90;
    private const double FrameMs = 16.7, WorkMs = 7;

    [Fact]
    public void AGuestHelperThreadNextToBackgroundWork()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        ulong mask = PickTwoCpus();
        foreach (ThreadPriority background in new[] { ThreadPriority.Normal, ThreadPriority.BelowNormal, ThreadPriority.Lowest })
        {
            (double[] lateness, long encodes) = Run(mask, background);
            Array.Sort(lateness);
            output.WriteLine($"background {background,-11}: helper frame end after its start median {lateness[Frames / 2]:F1} ms, p90 {lateness[Frames * 9 / 10]:F1}, worst {lateness[^1]:F1}; " +
                             $"frames over {FrameMs} ms {lateness.Count(l => l > FrameMs)}, over 50 ms {lateness.Count(l => l > 50)} of {Frames}; background encodes {encodes}");
        }
    }

    private static ulong PickTwoCpus()
    {
        int cpus = Environment.ProcessorCount;
        return cpus >= 4 ? 0b1100UL : (1UL << Math.Max(cpus, 1)) - 1;
    }

    private static (double[] Lateness, long Encodes) Run(ulong mask, ThreadPriority background)
    {
        using var stop = new CancellationTokenSource();
        long encodes = 0;
        var workers = new List<Thread>();
        byte[] rgba = new byte[128 * 128 * 4];
        new Random(1).NextBytes(rgba);
        for (int i = 0; i < 4; i++)
        {
            var worker = new Thread(() =>
            {
                Pin(mask);
                if (background != ThreadPriority.Normal)
                {
                    HostThreads.LowerCurrent(background);
                }

                byte[] blocks = new byte[32 * 32 * AstcEncoder.BlockBytes];
                while (!stop.IsCancellationRequested)
                {
                    AstcEncoder.Encode(rgba, 128, 128, blocks, AstcChannels.Rgba);
                    Interlocked.Increment(ref encodes);
                }
            })
            { IsBackground = true };
            worker.Start();
            workers.Add(worker);
        }

        double[] lateness = new double[Frames];
        var helper = new Thread(() =>
        {
            Pin(mask);
            long frequency = Stopwatch.Frequency;
            long start = Stopwatch.GetTimestamp();
            for (int f = 0; f < Frames; f++)
            {
                long frameStart = start + (long)(f * FrameMs * frequency / 1000);
                while (Stopwatch.GetTimestamp() < frameStart)
                {
                    Thread.Sleep(1);
                }

                // a fixed amount of work (calibrated as wall time on an idle core would be: a spin of WorkMs of CPU)
                long spun = 0, last = Stopwatch.GetTimestamp(), need = (long)(WorkMs * frequency / 1000);
                while (spun < need)
                {
                    long now = Stopwatch.GetTimestamp();
                    long step = now - last;
                    last = now;
                    if (step < frequency / 2000)
                    {
                        spun += step; // time slices taken by another thread do not count as work
                    }
                }

                lateness[f] = (Stopwatch.GetTimestamp() - frameStart) * 1000.0 / frequency;
            }
        });
        helper.Start();
        helper.Join();
        stop.Cancel();
        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        return (lateness, encodes);
    }

    private static void Pin(ulong mask)
    {
        if (OperatingSystem.IsWindows())
        {
            SetThreadAffinityMask(GetCurrentThread(), (nuint)mask);
        }
        else
        {
            ulong set = mask;
            sched_setaffinity(0, (nuint)sizeof(ulong), ref set);
        }
    }

    [DllImport("kernel32")] private static extern nint GetCurrentThread();
    [DllImport("kernel32")] private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);
    [DllImport("libc")] private static extern int sched_setaffinity(int pid, nuint size, ref ulong mask);
}
