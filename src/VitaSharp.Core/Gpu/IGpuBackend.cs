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
    void Idle()
    {
    }

    /// <summary>How long (host milliseconds) the idle worker may sleep before it calls <see cref="Idle"/> again when no
    /// command comes: shorter while the backend watches a CPU framebuffer for the display's scanout
    /// (<see cref="DisplayScanout"/>), so the next vblank is not missed.</summary>
    int IdleWakeMs => 50;

    /// <summary>The backend was installed on <paramref name="queue"/> (lets <see cref="Idle"/> notice newly queued work).</summary>
    void Attach(GpuQueue queue)
    {
    }

    /// <summary>
    /// Queues <paramref name="signal"/> to run once all work executed so far has completed on the GPU and its results
    /// are in guest memory. Returns false when nothing is outstanding, so the caller runs it immediately.
    /// </summary>
    bool DeferSignal(Action signal) => false;

    /// <summary>
    /// A title is about to start on this backend (called once its id is known, before its first command).
    /// Backends use it to load per-title caches (shaders, pipelines) and warm them up in the background.
    /// </summary>
    void BeginTitle(string titleId)
    {
    }

    /// <summary>The title passed to <see cref="BeginTitle"/>, or null (a backend without per-title state).</summary>
    string? TitleId => null;

    /// <summary>
    /// The emulator option "Carregar o cache de shaders antes de jogar": after <see cref="BeginTitle"/>, before the game starts, waits until
    /// the shaders and pipelines recorded in earlier sessions of the title are built (the boot rebuild after a
    /// generator change, then the pipeline prewarm), reporting <paramref name="progress"/>. Cancelling stops the wait
    /// only: what is left keeps building in the background, as without the option. False when there was nothing to wait for.
    /// </summary>
    bool LoadShaderCacheBeforeStart(Action<ShaderCacheLoadProgress>? progress, CancellationToken cancel) => false;

    /// <summary>The game created a vertex program object (sceGxmShaderPatcherCreateVertexProgram), on its own thread:
    /// a backend may start compiling what the program's draws will need (games create their programs while loading).</summary>
    void VertexProgramCreated(Gxm.GxmVertexProgram vertexProgram)
    {
    }

    /// <summary>The game created a fragment program object (sceGxmShaderPatcherCreateFragmentProgram[ForGles]) for the
    /// vertex program <paramref name="vertexProgram"/> (its SceGxmProgram, null when none was given), on its own thread.</summary>
    void FragmentProgramCreated(Gxm.GxmFragmentProgram fragmentProgram, Gxm.GxmProgramInfo? vertexProgram)
    {
    }

    /// <summary>The game created a render target with <paramref name="multisample"/> (SceGxmMultisampleMode), on its own
    /// thread: the scenes of its downscaled colour surfaces will be multisampled.</summary>
    void RenderTargetCreated(uint multisample)
    {
    }

    /// <summary>The game initialised a colour surface (sceGxmColorSurfaceInit), on its own thread: a surface its scenes
    /// may render into, so a backend may start building what the created programs need there.</summary>
    void ColorSurfaceInitialised(uint colorFormat, bool downscale)
    {
    }

    /// <summary>Called on the submitting thread for each command before it is queued (no worker state may be touched):
    /// lets a backend start slow preparation, e.g. shader compilation, ahead of execution. The queue calls it without
    /// its backend lock (it is on every command's path), so it can overlap <see cref="IDisposable.Dispose"/> of a
    /// backend being detached or replaced: a backend that does work here makes its Dispose wait for the calls in
    /// progress and turns later calls into no-ops (the Vulkan renderer: <c>_prefetchActive</c> / StopShaderWorkers).</summary>
    void Prefetch(GpuCommand command)
    {
    }

    /// <summary>Presents executed so far (frame numbering of dumps and traces); -1 when the backend does not count them.</summary>
    long PresentCount => -1;

    /// <summary>GPU dump start (GPU worker, frame boundary): backend state that lives only on the GPU (render target
    /// contents), opaque to the core; the replaying backend gets it back before the first command. Null: none.</summary>
    byte[]? ExportDumpState() => null;

    /// <summary>Called on the worker thread right before a <see cref="CallbackCommand"/>'s CPU action runs: content the
    /// backend holds only on the GPU (rendered surfaces) inside the range the action reads
    /// (<see cref="CallbackCommand.ReadAddress"/>) must be in guest memory when this returns.</summary>
    void BeforeCallback(CallbackCommand command)
    {
    }

    /// <summary>Called on the worker thread before <see cref="BeforeCallback"/>: true when the backend takes the action
    /// and runs it itself once the GPU work it reads is complete (in order with the sync points after it), instead of
    /// the worker waiting for that work now.</summary>
    bool DeferCallback(CallbackCommand command) => false;

    /// <summary>Backend statistics for the end-of-run PERF report (null: nothing to report).</summary>
    string? Report() => null;
}

public enum DisplayPixelFormat : uint
{
    A8B8G8R8 = 0x0,
    A2B10G10R10 = 0x60000000,
}

public readonly record struct DisplayFrame(uint Address, uint Pitch, uint Width, uint Height, DisplayPixelFormat Format);
