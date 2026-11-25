using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// "Carregar o cache de shaders antes de jogar" (<see cref="IGpuBackend.LoadShaderCacheBeforeStart"/>): the boot
/// rebuild or the pipeline prewarm that <see cref="BeginTitle"/> started from the title's shader cache (programs.pack,
/// pipelines.list, what its earlier sessions recorded) runs to the end before the game starts, like RPCS3's and
/// Ryujinx's shader cache loading and Dolphin's "compile shaders before starting". Without it the game starts at once
/// and draws that come before the prewarm reaches their pipeline build them on the GPU thread (the hitches of the first
/// minutes of a session). This is a cache load, not a precompilation: a first session has nothing recorded.
/// <para>Only recorded work is waited for. Translating ahead of their first use the GXP programs that the scan of the
/// game's files finds (<see cref="VitaSharp.Core.Gxm.Shader.GxpScanner"/>, used by the shadertest tool) was measured
/// and left out: the scan of two titles' app0 found 2 of the 88 programs one of them uses and 10 of
/// Killzone's 1,758 (the rest are in compressed archives), and only 7-13% of the recorded shader variants can be
/// predicted from the program alone (the hints are draw state: texture formats, vertex attribute formats, the
/// surface's colour format; 12,826 recipes of 67 titles).</para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Boot rebuild and prewarm items queued and finished (the progress of the wait).</summary>
    private int _bootItemsQueued, _bootItemsDone;

    /// <summary>How often the wait reports progress.</summary>
    private const int ShaderCacheLoadReportMs = 100;

    public bool LoadShaderCacheBeforeStart(Action<ShaderCacheLoadProgress>? progress, CancellationToken cancel)
    {
        Thread? boot;
        lock (_titleLock)
        {
            boot = _prewarmThread;
        }

        if (boot == null || !boot.IsAlive)
        {
            return false;
        }

        // the game waits for it now: the prewarm thread no longer yields to it
        try
        {
            boot.Priority = ThreadPriority.Normal;
        }
        catch (ThreadStateException)
        {
            // it just ended
        }

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // the game waits for the workers: the whole pool may run (ShaderWorkerBudget)
        BeginWaitForWorkers();
        try
        {
            while (!boot.Join(ShaderCacheLoadReportMs))
            {
                progress?.Invoke(new ShaderCacheLoadProgress(Volatile.Read(ref _bootItemsDone), Volatile.Read(ref _bootItemsQueued), false));
                if (cancel.IsCancellationRequested || _deviceLost)
                {
                    Log.Info($"[shadercache] load before start: the game starts before the end ({_bootItemsDone} of {_bootItemsQueued} done, the rest in the background)");
                    return true;
                }
            }
        }
        finally
        {
            EndWaitForWorkers();
        }

        progress?.Invoke(new ShaderCacheLoadProgress(Volatile.Read(ref _bootItemsDone), Volatile.Read(ref _bootItemsQueued), true));
        Log.Info($"[shadercache] load before start: {_bootItemsDone} recorded shaders/pipelines ready before the game started ({Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t0):F0} ms)");
        return true;
    }
}
