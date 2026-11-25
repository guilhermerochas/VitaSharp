namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// How many shader workers (GLSL translation, shaderc and the driver's pipeline builds) may run at once, from the CPU
/// budget: the process keeps the whole machine at or below 90% of its logical processors, and more cores are used when
/// they are free. <see cref="Steady"/> workers run while the GPU thread is working: the guest CPU thread and the GPU
/// thread keep a core each. <see cref="Stall"/> workers run while the GPU thread is waiting for shader or pipeline work
/// (and while the game waits for the shader cache load): the frame cannot go on, so the GPU thread's core and the
/// guest's, which soon waits for the GPU, go to the compiles. The pool has <see cref="Stall"/> threads; the ones above
/// <see cref="Steady"/> are the spare workers: they wait until a stall begins and run at below-normal priority, so even
/// then they only take the cores the guest threads, the GPU thread and the steady workers leave idle (the Windows and
/// Linux schedulers give a below-normal thread a core no normal one wants; yuzu and RPCS3 run all their compile
/// threads at low priority for the same reason). This is the only rule that sizes the pool: there is no other count of
/// "spare-time" workers.
/// <para>References: yuzu (and Eden) build pipelines on all logical processors but one at low priority; Dolphin uses
/// the cores minus 3 (1-4); RPCS3 uses a table (8 threads: 2, 12: 4, 16: 8, 24: 12) at low priority. All three share
/// one VkPipelineCache between the workers and leave the driver to synchronise it, as this renderer does. A cold replay
/// of a GPU dump showed that throughput is the limit: the first frame of a scene with 127 new shaders and 40 pipelines
/// waited 2-4 s for 2 workers, and 4 workers instead of 2 on 4 CPUs cut Killzone's worst frame from 3.7-4.5 s to
/// 0.1-1.6 s.</para>
/// <para>Phones (big.LITTLE): the emulation threads want the big cores and the compiles run on the LITTLE ones, with
/// fewer workers than a desktop of the same core count because a long burst on every core heats the phone and lowers
/// the big cores' clocks. Two LITTLE cores stay free for the system, audio and presentation, and at most 4 workers run
/// (Redmi Note 14, 2 big + 6 LITTLE: 2 steady, 4 during a stall; today's half of the processors gave 4 all the
/// time).</para>
/// </summary>
/// <param name="Steady">workers that may run while the GPU thread is working</param>
/// <param name="Stall">workers that may run while the GPU thread waits for them (the pool size)</param>
public readonly record struct ShaderWorkerBudget(int Steady, int Stall)
{
    /// <summary>Share of the logical processors the process may keep busy, in tenths (90%).</summary>
    private const int CpuBudgetTenths = 9;

    /// <summary>Cores kept for the emulation while the GPU thread is working: the guest CPU thread and the GPU thread.</summary>
    private const int EmulationThreads = 2;

    /// <summary>Most workers on a desktop while the GPU thread works / waits (the driver's compile memory grows with them).</summary>
    private const int DesktopSteadyMax = 8, DesktopStallMax = 12;

    /// <summary>Most workers on a phone during a stall, and the LITTLE cores left to the system.</summary>
    private const int MobileStallMax = 4, MobileFreeLittleCores = 2;

    /// <summary>The spare workers: above the steady share, at below-normal priority, running only during a stall.</summary>
    public int Spare => Stall - Steady;

    /// <summary>The same steady share with <paramref name="spare"/> spare workers above it (VITASHARP_SHADER_SPARE_WORKERS,
    /// A/B; at most 64 workers in all).</summary>
    public ShaderWorkerBudget WithSpare(int spare) => this with { Stall = Math.Min(Steady + Math.Max(spare, 0), 64) };

    /// <summary>A fixed number of workers (--shader-workers, VITASHARP_SHADER_WORKERS): as many in every state.</summary>
    public static ShaderWorkerBudget Fixed(int workers)
    {
        int count = Math.Clamp(workers, 1, 64);
        return new ShaderWorkerBudget(count, count);
    }

    /// <summary>The budget for a machine with <paramref name="logicalProcessors"/> logical processors (the ones the
    /// process may run on); <paramref name="mobile"/> with <paramref name="littleCores"/> LITTLE cores (0 = not known).</summary>
    public static ShaderWorkerBudget For(int logicalProcessors, bool mobile, int littleCores)
    {
        int processors = Math.Max(1, logicalProcessors);
        if (mobile)
        {
            int little = littleCores > 0 && littleCores < processors ? littleCores : processors / 2;
            int stallWorkers = Math.Clamp(little - MobileFreeLittleCores, 1, MobileStallMax);
            return new ShaderWorkerBudget(Math.Clamp(stallWorkers / 2, 1, 2), stallWorkers);
        }

        int budget = Math.Max(1, processors * CpuBudgetTenths / 10);
        int steady = Math.Clamp(budget - EmulationThreads, Math.Min(2, budget), DesktopSteadyMax);
        return new ShaderWorkerBudget(steady, Math.Clamp(budget, steady, DesktopStallMax));
    }

    /// <summary>The budget of this machine: the processors the process may use and, on Android, its LITTLE cores.</summary>
    public static ShaderWorkerBudget ForHost() =>
        For(Environment.ProcessorCount, OperatingSystem.IsAndroid(), OperatingSystem.IsAndroid() ? LittleCores(ReadMaxFrequencies()) : 0);

    /// <summary>The cores of the slowest cluster when the cores have different top clocks (big.LITTLE); 0 when they
    /// are all alike or nothing is known.</summary>
    /// <param name="maxFrequencies">each core's top clock (cpuinfo_max_freq), 0 when unknown</param>
    public static int LittleCores(ReadOnlySpan<long> maxFrequencies)
    {
        long lowest = long.MaxValue, highest = 0;
        foreach (long frequency in maxFrequencies)
        {
            if (frequency <= 0)
            {
                return 0;
            }

            lowest = Math.Min(lowest, frequency);
            highest = Math.Max(highest, frequency);
        }

        if (highest == 0 || lowest == highest)
        {
            return 0;
        }

        int little = 0;
        foreach (long frequency in maxFrequencies)
        {
            if (frequency == lowest)
            {
                little++;
            }
        }

        return little;
    }

    /// <summary>Linux and Android: each core's cpufreq/cpuinfo_max_freq (empty when the files cannot be read).</summary>
    private static long[] ReadMaxFrequencies()
    {
        try
        {
            var frequencies = new List<long>();
            for (int core = 0; core < 256; core++)
            {
                string path = $"/sys/devices/system/cpu/cpu{core}/cpufreq/cpuinfo_max_freq";
                if (!File.Exists(path))
                {
                    break;
                }

                frequencies.Add(long.TryParse(File.ReadAllText(path).Trim(), out long frequency) ? frequency : 0);
            }

            return frequencies.ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
