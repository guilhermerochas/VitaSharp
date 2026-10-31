using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>Counts of one <see cref="GpuDumpPlayer.Play"/>.</summary>
public sealed class GpuDumpPlayStats
{
    public long Commands, Draws, Skipped, MemoryBytes, Frames;
}

/// <summary>
/// Plays a GPU dump (<see cref="GpuDumpReader"/>) on a renderer: the recorded guest memory is written into the guest
/// address space before each command that read it, and the commands are executed in the recorded order. Shared by
/// <c>replay-gpu</c> (VitaSharp.Desktop) and the performance tools (tools/VitaSharp.PerfTools). The caller owns the
/// <see cref="GuestMemory"/> and the renderer, and executes a <see cref="FinishCommand"/> afterwards.
/// </summary>
public static class GpuDumpPlayer
{
    /// <summary>Recorded environment switches that are never replayed (paths, dump/debug triggers, CPU backend).</summary>
    public static readonly string[] NotReplayedEnv = [EnvVars.Data, EnvVars.GpuDumpAt, EnvVars.DebugMode, EnvVars.Cpu, EnvVars.Lockstep];

    /// <summary>The renderer/shader switches recorded in the dump header (env.*) that are not set in this process.</summary>
    public static Dictionary<string, string> RecordedEnvironment(GpuDumpHeader header)
    {
        var set = new Dictionary<string, string>();
        foreach (var (k, v) in header.Entries)
            if (k.StartsWith("env.") && !NotReplayedEnv.Contains(k[4..]) && Environment.GetEnvironmentVariable(k[4..]) == null) set[k[4..]] = v;
        return set;
    }

    /// <param name="executeDraw">called for each draw with its number in the frame (1-based); false skips it</param>
    /// <param name="presented">called after each present with the frame's draw count; false stops the replay</param>
    /// <param name="cancel">checked before each record; true stops the replay</param>
    public static GpuDumpPlayStats Play(GpuDumpReader reader, VulkanRenderer renderer, bool idle = true, bool importState = true,
        Func<long, bool>? executeDraw = null, Func<long, bool>? presented = null, Func<bool>? cancel = null)
    {
        var st = new GpuDumpPlayStats();
        long frameDraws = 0;
        while (reader.Next())
        {
            if (cancel?.Invoke() == true) break;
            switch (reader.Kind)
            {
                case GpuDumpRecord.Memory:
                    reader.Data.CopyTo(GuestMemory.Span(reader.Address, reader.Data.Length)); // marks the pages written
                    st.MemoryBytes += reader.Data.Length;
                    continue;
                case GpuDumpRecord.Idle:
                    if (idle) renderer.Idle();
                    continue;
                case GpuDumpRecord.BackendState:
                    if (importState) renderer.ImportDumpState(reader.Data);
                    continue;
            }
            var cmd = reader.Command!;
            st.Commands++;
            switch (cmd)
            {
                case DrawCommand:
                    frameDraws++;
                    st.Draws++;
                    if (executeDraw?.Invoke(frameDraws) == false) { st.Skipped++; continue; }
                    renderer.Execute(cmd);
                    break;
                case SignalCommand sig:
                    if (!renderer.DeferSignal(sig.Signal)) sig.Signal();
                    break;
                default:
                    renderer.Execute(cmd);
                    break;
            }
            if (cmd is not PresentCommand) continue;
            st.Frames++;
            long n = frameDraws;
            frameDraws = 0;
            if (presented?.Invoke(n) == false) break;
        }
        return st;
    }
}
