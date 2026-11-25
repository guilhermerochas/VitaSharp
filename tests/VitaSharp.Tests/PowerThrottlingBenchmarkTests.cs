using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// A measurement, not a check (Category=Slow, Windows; no assertion on speed): what Windows' power throttling (EcoQoS,
/// which it applies to a process in the background on battery, or in Task Manager's efficiency mode) costs threads
/// that work like the emulated cores (above normal priority, CPU-bound), and whether a thread's own opt-out
/// (ThreadPowerThrottling, execution speed never throttled) keeps them at full speed. Four states, alternated three
/// times: as the emulator runs today, the process in EcoQoS, the process in EcoQoS with the threads opted out, the
/// threads opted out alone. Run by hand: <c>dotnet test --filter FullyQualifiedName~PowerThrottlingBenchmarkTests</c>.
/// </summary>
[Trait("Category", "Slow")]
public sealed class PowerThrottlingBenchmarkTests(ITestOutputHelper output)
{
    private const int ProcessPowerThrottling = 4, ThreadPowerThrottling = 3;
    private const uint ExecutionSpeed = 1, CurrentVersion = 1;
    private const int Threads = 4, MeasureMs = 1500;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(nint process, int informationClass, ref PowerThrottlingState information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadInformation(nint thread, int informationClass, ref PowerThrottlingState information, int size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    /// <summary>The process in EcoQoS (<paramref name="throttled"/>) or back to the system's choice.</summary>
    private static void SetProcessThrottling(bool throttled)
    {
        PowerThrottlingState state = new()
        {
            Version = CurrentVersion,
            ControlMask = throttled ? ExecutionSpeed : 0,
            StateMask = throttled ? ExecutionSpeed : 0,
        };
        Assert.True(SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>()));
    }

    /// <summary>Operations per second of <see cref="Threads"/> above-normal busy threads.</summary>
    private static double Throughput(bool optOut)
    {
        long total = 0;
        using Barrier start = new(Threads + 1);
        long stopAt = 0;
        List<Thread> threads = [];
        for (int i = 0; i < Threads; i++)
        {
            Thread thread = new(() =>
            {
                if (optOut)
                {
                    PowerThrottlingState state = new() { Version = CurrentVersion, ControlMask = ExecutionSpeed, StateMask = 0 };
                    SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
                }

                start.SignalAndWait();
                long count = 0;
                uint value = 1;
                while (Stopwatch.GetTimestamp() < Volatile.Read(ref stopAt))
                {
                    for (int j = 0; j < 4096; j++)
                    {
                        value = value * 1664525 + 1013904223;
                    }

                    count += 4096;
                }

                Interlocked.Add(ref total, count + (value & 1));
            })
            {
                Priority = ThreadPriority.AboveNormal,
                IsBackground = true,
            };
            threads.Add(thread);
            thread.Start();
        }

        Volatile.Write(ref stopAt, Stopwatch.GetTimestamp() + Stopwatch.Frequency * MeasureMs / 1000);
        start.SignalAndWait();
        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        return total / (MeasureMs / 1000.0);
    }

    [Fact]
    public void EcoQosAgainstTheThreadsOptOut()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("Windows only");
            return;
        }

        (string Name, bool Throttled, bool OptOut)[] states =
        [
            ("as today", false, false),
            ("process in EcoQoS", true, false),
            ("EcoQoS, threads opted out", true, true),
            ("threads opted out", false, true),
        ];
        Dictionary<string, List<double>> results = states.ToDictionary(s => s.Name, _ => new List<double>());
        try
        {
            for (int round = 0; round < 3; round++)
            {
                foreach ((string name, bool throttled, bool optOut) in states)
                {
                    SetProcessThrottling(throttled);
                    results[name].Add(Throughput(optOut));
                }
            }
        }
        finally
        {
            SetProcessThrottling(false);
        }

        double baseline = results["as today"].Average();
        foreach ((string name, _, _) in states)
        {
            List<double> values = results[name];
            output.WriteLine($"{name,-28} {values.Average() / 1e6,8:F0} M ops/s ({values.Average() / baseline * 100,5:F1}%; runs {string.Join(", ", values.Select(v => (v / 1e6).ToString("F0")))})");
        }
    }
}
