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

    /// <summary>The backend was installed on <paramref name="queue"/> (lets <see cref="Idle"/> notice newly queued work).</summary>
    void Attach(GpuQueue queue) { }

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

    /// <summary>Called on the submitting thread for each command before it is queued (no worker state may be touched):
    /// lets a backend start slow preparation, e.g. shader compilation, ahead of execution.</summary>
    void Prefetch(GpuCommand command) { }

    /// <summary>Presents executed so far (frame numbering of dumps and traces); -1 when the backend does not count them.</summary>
    long PresentCount => -1;

    /// <summary>GPU dump start (GPU worker, frame boundary): backend state that lives only on the GPU (render target
    /// contents), opaque to the core; the replaying backend gets it back before the first command. Null: none.</summary>
    byte[]? ExportDumpState() => null;

    /// <summary>Backend statistics for the end-of-run PERF report (null: nothing to report).</summary>
    string? Report() => null;
}

public enum DisplayPixelFormat : uint
{
    A8B8G8R8 = 0x0,
    A2B10G10R10 = 0x60000000,
}

public readonly record struct DisplayFrame(uint Address, uint Pitch, uint Width, uint Height, DisplayPixelFormat Format);
