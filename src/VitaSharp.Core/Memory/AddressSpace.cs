namespace VitaSharp.Core.Memory;

/// <summary>
/// One process's view of guest memory: a host reservation of the whole 32-bit guest address space (fastmem, host
/// pointer = <see cref="Base"/> + guest address) with the page dirty table right below guest address 0.
/// <para>
/// Every process has its own (<see cref="GuestMemory.CreateSpace"/>), as on the Vita, where each process has its own user
/// address space: the shell and a game linked at the same fixed address (both at 0x81000000) run side by side. The
/// regions the Vita shares between processes are mapped into every space from one host memory: the kernel region
/// (HLE trampolines, objects visible to the guest) and CDRAM (SceSharedFb, display buffers, GPU memory). Private user
/// memory exists only in its owner's space.
/// </para>
/// <para>
/// The space a host thread works in is <see cref="Current"/>: a guest thread's process's space, set when its host thread
/// starts; a host operation for a process (<see cref="Kernel.ProcessScope.Enter(Emulator)"/>) switches to it and the
/// choice flows into the tasks and threads it starts; the GPU worker switches to the space of each command it executes.
/// The static accessors of <see cref="GuestMemory"/> address the current space, generated code addresses the space of
/// its thread through <see cref="Cpu.CpuState.MemBase"/>.
/// </para>
/// </summary>
public sealed unsafe class AddressSpace
{
    static int s_nextId;

    /// <summary>Identifies the space's current use: a new id each time the space is handed to a process (caches keyed by
    /// guest address compare it, since the same address holds other data in another space).</summary>
    public int Id { get; private set; }

    /// <summary>Host address of guest address 0 in this space.</summary>
    public byte* Base { get; }

    /// <summary>Who uses the space (logs).</summary>
    public string Label { get; internal set; } = "";

    /// <summary>True when the shared regions are mapped views of the system's shared memory (per-process spaces).</summary>
    public bool HasSharedViews { get; }

    internal readonly (nint Address, nuint Size, bool View)[] Parts;

    /// <summary>The space's private blocks (user memory, fixed ranges), by address.</summary>
    internal readonly Dictionary<uint, GuestMemory.Block> Blocks = new();

    /// <summary>One bit per 4 KB page of the private regions: set while the page belongs to a block of this space.</summary>
    internal readonly ulong[] MappedPages = new ulong[(1 << 20) / 64];

    internal AddressSpace(byte* @base, (nint, nuint, bool)[] parts, bool sharedViews)
    {
        Base = @base;
        Parts = parts;
        HasSharedViews = sharedViews;
        Renew();
    }

    internal void Renew() => Id = Interlocked.Increment(ref s_nextId);

    /// <summary>True when <paramref name="hostAddress"/> lies in this space's guest window.</summary>
    public bool ContainsHost(ulong hostAddress) => hostAddress >= (ulong)Base && hostAddress < (ulong)Base + GuestMemory.ReserveSize;

    public override string ToString() => $"address space {Id}{(Label.Length > 0 ? " (" + Label + ")" : "")}";

    // ------------------------------------------------------------------ the space of the current host thread

    [ThreadStatic] static AddressSpace? t_current;

    /// <summary>The space the calling host thread works in (the first process's when it chose none).</summary>
    public static AddressSpace? Current => t_current ?? GuestMemory.Instance?.Primary;

    static void SetThread(AddressSpace? s)
    {
        t_current = s;
        GuestMemory.t_base = s == null ? null : s.Base;
    }

    /// <summary>
    /// Makes <paramref name="space"/> the calling thread's space until the result is disposed (null = the first
    /// process's). For synchronous work on another process's memory: the GPU worker executing a process's commands, HLE
    /// code writing into a waiting thread of another process.
    /// </summary>
    public static Activation Use(AddressSpace? space)
    {
        var prev = t_current;
        if (!ReferenceEquals(prev, space)) SetThread(space);
        return new Activation(prev, space);
    }

    /// <summary>A long-lived worker (the GPU worker) switches to the space of the work item it takes next.</summary>
    public static void MakeCurrent(AddressSpace? space)
    {
        if (!ReferenceEquals(t_current, space)) SetThread(space);
    }

    public readonly struct Activation(AddressSpace? previous, AddressSpace? active) : IDisposable
    {
        public void Dispose() { if (!ReferenceEquals(previous, active)) SetThread(previous); }
    }

    // the space also flows with the execution context: tasks and host threads started by a process's code (async I/O,
    // decoders, HTTP exchanges) work in that process's memory
    static readonly AsyncLocal<AddressSpace?> s_flow = new(static a => SetThread(a.CurrentValue));

    /// <summary>Makes <paramref name="space"/> current for the calling thread and everything it starts, until disposed.</summary>
    public static FlowScope Enter(AddressSpace? space)
    {
        var prev = s_flow.Value;
        s_flow.Value = space;
        SetThread(space); // also when the flowing value did not change but the thread had picked another one with Use
        return new FlowScope(prev);
    }

    public readonly struct FlowScope(AddressSpace? previous) : IDisposable
    {
        public void Dispose()
        {
            s_flow.Value = previous;
            SetThread(previous);
        }
    }
}
