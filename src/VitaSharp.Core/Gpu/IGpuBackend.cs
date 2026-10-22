namespace VitaSharp.Core.Gpu;

/// <summary>
/// Host graphics backend (Vulkan on desktop; Android later). The GXM HLE layer records API-agnostic
/// <see cref="GpuCommand"/>s; the backend executes them in order on the GPU worker thread.
/// Guest memory referenced by a command (vertex/index data, textures) may be read during <see cref="Execute"/>;
/// the guest is only told the work finished (notifications) after Execute returns.
/// </summary>
public interface IGpuBackend : IDisposable
{
    void Execute(GpuCommand command);

    /// <summary>Called on the worker thread when the queue runs empty (good moment to flush/submit).</summary>
    void Idle() { }

    /// <summary>
    /// Queues <paramref name="signal"/> to run once all work executed so far has completed on the GPU and its results
    /// are in guest memory. Returns false when nothing is outstanding, so the caller runs it immediately.
    /// </summary>
    bool DeferSignal(Action signal) => false;

    /// <summary>
    /// A title is about to start on this backend (called once its id is known, before its first command).
    /// Backends use it to load per-title caches (shaders, pipelines) and warm them up in the background.
    /// </summary>
    void BeginTitle(string titleId) { }

    /// <summary>Backend statistics for the end-of-run PERF report (null: nothing to report).</summary>
    string? Report() => null;
}

public enum DisplayPixelFormat : uint
{
    A8B8G8R8 = 0x0,
    A2B10G10R10 = 0x60000000,
}

public readonly record struct DisplayFrame(uint Address, uint Pitch, uint Width, uint Height, DisplayPixelFormat Format);
