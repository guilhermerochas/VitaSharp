using System.Runtime.CompilerServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle;

/// <summary>Marks an HLE implementation. The NID is looked up by name unless given explicitly.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class HleAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
    public uint Nid { get; init; }
    /// <summary>Restrict the NID lookup to a library when the same name exists in several.</summary>
    public string? Library { get; init; }
    /// <summary>A default, not a real implementation: the function needs hardware or a service the emulator does not
    /// have (Bluetooth, USB, system update, game card authentication, PSP ad hoc, debug hooks...) and returns the
    /// firmware's documented result (success or its specific error) without doing anything. Counted apart from the
    /// real implementations by hle-coverage (docs/hle/progress.md).</summary>
    public bool Default { get; init; }
    /// <summary>The function returns a value (a texture type, a size, an address, a count...), not an SCE error code: a
    /// return in 0x80000000-0x80FFFFFF is a normal result and the HLE error counter (debug mode, session captures)
    /// leaves it out.</summary>
    public bool ReturnsValue { get; init; }
    /// <summary>What the function is on the Vita (user-mode code, a kernel entry, a wait in the scheduler), which sets
    /// the emulated CPU time charged per call. <see cref="HleCost.Module"/> (not given): the module's
    /// <see cref="HleModule.DefaultCost"/>.</summary>
    public HleCost Cost { get; init; }
    /// <summary>The function is the callback-processing variant of a wait (the firmware's "CB" syscalls,
    /// sceKernelWaitSemaCB, sceDisplayWaitVblankStartCB...): the thread runs its notified callbacks before and while it
    /// waits.</summary>
    public bool CallbackWait { get; init; }

    /// <summary>For a callback wait the NID database has no name for (an unnamed kernel export): the NID of the wait
    /// it is the callback-processing twin of (the named ones pair by their "CB" suffix).</summary>
    public uint CallbackTwinOf { get; init; }
}

/// <summary>
/// What an HLE function is on the Vita, declared per function (<see cref="HleAttribute.Cost"/>) or per library
/// (<see cref="HleModule.DefaultCost"/>), never derived from its name. It sets the emulated CPU time (guest cycles,
/// <see cref="HleCosts.Cycles"/>) a call charges to the calling thread: the instructions the firmware runs for it, so
/// guest clocks and the CPU pacing see the call take time. The same declared model as Dolphin's HLE patch table
/// (HookType/HookFlag per function) and PPSSPP's HLEFunction flags (HLE_KERNEL_SYSCALL, HLE_NOT_IN_INTERRUPT); the
/// sizes follow the Cortex-A9 cost model and PPSSPP's measured charges (hleEatCycles).
/// </summary>
public enum HleCost
{
    /// <summary>Not declared on the function: the module's <see cref="HleModule.DefaultCost"/>.</summary>
    Module = 0,
    /// <summary>A few user-mode instructions, no kernel entry: a TLS slot address, the thread ID kept in user memory,
    /// an LDREX/STREX atomic (20 cycles).</summary>
    UserInline,
    /// <summary>A short user-mode library call: an uncontended lwmutex (LDREX/STREX plus checks), a Clib routine's
    /// prologue (the bytes it moves are charged by the implementation), a libgxm field getter or state setter
    /// (50 cycles).</summary>
    UserCall,
    /// <summary>A user-mode library routine with real work and no kernel entry on its common path: libgxm command
    /// building, libngs, librtc conversions, libfiber switches, the user-mode clock reads (200 cycles).</summary>
    UserLibrary,
    /// <summary>A syscall that does not go through the scheduler: the kernel entry and exit and the service's work
    /// (2,000 cycles; PPSSPP charges its syscalls in the same range).</summary>
    KernelEntry,
    /// <summary>A syscall that waits in, or reschedules through, the thread manager: a wait, a signal that may wake a
    /// thread, blocking I/O and device reads (12,000 cycles, ~40 us at 300 MIPS: syscall plus scheduler).</summary>
    SchedulerWait,
}

/// <summary>The emulated CPU cycles of each <see cref="HleCost"/> class.</summary>
public static class HleCosts
{
    /// <summary>Guest cycles charged per call of <paramref name="cost"/> (<see cref="HleCost.Module"/> counts as a
    /// kernel entry: a function that declares nothing is a syscall).</summary>
    public static long Cycles(HleCost cost) => cost switch
    {
        HleCost.UserInline => 20,
        HleCost.UserCall => 50,
        HleCost.UserLibrary => 200,
        HleCost.SchedulerWait => 12_000,
        _ => 2_000,
    };
}

/// <summary>Base class for an HLE module (a group of related exported functions).</summary>
public abstract class HleModule
{
    public Emulator Emu { get; internal set; } = null!;
    protected GuestMemory Mem => Emu.Memory;
    protected Kernel.Kernel K => Emu.Kernel;
    public virtual void Initialize() { }

    /// <summary>The module only stands in for firmware code (the C runtime, FIOS2, functions whose firmware body is a
    /// constant): its functions serve a process that does not load the firmware module, and never make the HLE
    /// take a library over from the firmware when a system program's dependencies are resolved.</summary>
    public virtual bool FirmwareFallbackOnly => false;

    /// <summary>The module is only a fallback for a firmware module the loader gives every program that imports its
    /// library, games included (the HLE has a few getters of it; the real module does the work).</summary>
    public virtual bool FirmwareForEveryImporter => false;

    /// <summary>What the module's functions are on the Vita unless one declares otherwise
    /// (<see cref="HleAttribute.Cost"/>): a kernel entry for a firmware service reached by syscall; user-mode libraries (libgxm, libngs, librtc, libfiber,
    /// Clib) declare their class here.</summary>
    public virtual HleCost DefaultCost => HleCost.KernelEntry;

}

/// <summary>Per-thread view used by HLE functions: argument access and the calling guest thread.</summary>
public sealed unsafe class HleContext
{
    public CpuState* Cpu;
    public GuestThread Thread = null!;
    public Emulator Emu = null!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Gpr(int slot) => slot < 4 ? Cpu->R[slot] : GuestMemory.R32(Cpu->R[13] + (uint)(slot - 4) * 4);
    public uint StackWord(int byteOffset) => GuestMemory.R32(Cpu->R[13] + (uint)byteOffset);
    public float Fpr(int idx) => ((float*)Cpu->D)[idx];

    public void Return(uint v) => Cpu->R[0] = v;
    public void Return(int v) => Cpu->R[0] = (uint)v;
    public void Return(ulong v) { Cpu->R[0] = (uint)v; Cpu->R[1] = (uint)(v >> 32); }
    public void Return(long v) => Return((ulong)v);
    public void Return(float v) { ((float*)Cpu->D)[0] = v; Cpu->R[0] = BitConverter.SingleToUInt32Bits(v); }
    public void Return(bool v) => Cpu->R[0] = v ? 1u : 0u;
    public void Return(double v) { ((double*)Cpu->D)[0] = v; Return(BitConverter.DoubleToUInt64Bits(v)); }

    // helpers for the generated binders
    public static uint ArgU32(HleContext c, int slot) => c.Gpr(slot);
    public static ulong ArgU64(HleContext c, int slot) => c.Gpr(slot) | ((ulong)c.Gpr(slot + 1) << 32);
    public static ulong ArgU64Stack(HleContext c, int byteOffset) => c.StackWord(byteOffset) | ((ulong)c.StackWord(byteOffset + 4) << 32);
    public static uint ArgStack(HleContext c, int byteOffset) => c.StackWord(byteOffset);
    public static float ArgF32(HleContext c, int idx) => c.Fpr(idx);
    public static double ArgF64(HleContext c, int idx) => ((double*)c.Cpu->D)[idx];
    public static string ArgString(uint addr) => GuestMemory.ReadCString(addr);
}

/// <summary>Source of 32-bit variadic argument words (e.g. a va_list pointer).</summary>
public interface IVaSource
{
    uint Next();
    /// <summary>Guest address of the next word for a va_list in memory (null otherwise): doubleword arguments sit at
    /// the next 8-byte aligned address (AAPCS, GCC's va_arg), wherever the list started.</summary>
    uint? Address => null;
}

/// <summary>Variadic argument reader (for printf-style functions): register/stack slots or a va_list.</summary>
public sealed class VarArgs
{
    readonly HleContext? _ctx;
    readonly IVaSource? _src;
    int _slot;
    int _words;

    public VarArgs(HleContext ctx, int firstSlot) { _ctx = ctx; _slot = firstSlot; }
    public VarArgs(IVaSource src, int unused) { _src = src; }

    public uint NextU32()
    {
        _words++;
        return _src != null ? _src.Next() : _ctx!.Gpr(_slot++);
    }

    public ulong NextU64()
    {
        if (_src != null)
        {
            // a va_list made by va_start usually starts at the saved r1 (4 mod 8): the parity of the words read so far
            // put every %lld / double after an odd number of words (FIOS2's own trace) one word off
            if (_src.Address is uint a ? (a & 7) != 0 : (_words & 1) != 0) NextU32();
        }
        else if ((_slot & 1) != 0) { _slot++; _words++; }
        uint lo = NextU32(), hi = NextU32();
        return lo | ((ulong)hi << 32);
    }

    public double NextDouble() => BitConverter.UInt64BitsToDouble(NextU64());
}

public sealed class HleFunction
{
    public required uint Nid;
    public required string Name;
    public string Library = "";
    public Action<HleContext>? Invoke;
    public bool Implemented => Invoke != null;
    /// <summary>The callback-processing variant of a wait (<see cref="HleAttribute.CallbackWait"/>).</summary>
    public bool IsCallbackWait;
    public long CallCount;
    /// <summary>The "unimplemented" warning was issued (skips building its key on every later call).</summary>
    public bool WarnedUnimplemented;
    /// <summary>The implementation returns a value, not an error code (<see cref="HleAttribute.ReturnsValue"/>).</summary>
    public bool ReturnsValue;
    /// <summary>An exception of the function was logged (GuestThread.InvokeGuarded).</summary>
    public bool ExceptionLogged;
    /// <summary>Emulated CPU cycles charged for this function so far (profiling).</summary>
    public long ChargedCycles;
    // declared before Track and Profile: static initializers run in textual order
    /// <summary><see cref="EnvVars.PerfHleTime"/>=1: HLE calls are timed from the start (wall clock only, see
    /// <see cref="ProfileWallOnly"/>) for the periodic "PERF hle time" lines (Util/HleTimeReport.cs).</summary>
    public static readonly bool TimeFromEnv = Environment.GetEnvironmentVariable(EnvVars.PerfHleTime) == "1";
    /// <summary>--profile: guest threads record the HLE function they are in (GuestThread.CurrentHle).</summary>
    public static bool Track = TimeFromEnv;
    /// <summary>--profile-hle: also times every HLE call per thread (GuestThread.HleProfile): two timestamps and two
    /// thread cycle-counter reads per call, which slows HLE-heavy games noticeably.</summary>
    public static bool Profile = TimeFromEnv;
    /// <summary>With <see cref="Profile"/>: only the wall clock is read per call (no thread cycle counter, which costs a
    /// system call on Linux/Android).</summary>
    public static bool ProfileWallOnly = TimeFromEnv;

    /// <summary>What the function is on the Vita (<see cref="HleCost"/>, declared by its [Hle] attribute or
    /// module).</summary>
    public HleCost CostClass = HleCost.KernelEntry;
    /// <summary>Emulated CPU cost (guest cycles) charged per call: <see cref="HleCosts.Cycles"/> of
    /// <see cref="CostClass"/>.</summary>
    public long Cost = HleCosts.Cycles(HleCost.KernelEntry);

    /// <summary>Sets the declared class and its per-call charge.</summary>
    public void SetCost(HleCost cost)
    {
        CostClass = cost;
        Cost = HleCosts.Cycles(cost);
    }
}

/// <summary>Registry of HLE functions and variables; binds C# methods to guest calling conventions.</summary>
public sealed partial class HleRegistry : IImportResolver
{
    readonly Emulator _emu;
    readonly List<HleFunction> _functions = [new HleFunction { Nid = 0, Name = "<invalid>" }];
    /// <summary>The slot of an index another process gave out and this one has not used yet.</summary>
    static readonly HleFunction s_unbound = new() { Nid = 0, Name = "<unbound>" };
    HleIndexTable? _ownIndexes;

    /// <summary>The emulated Vita's index table (a registry of an emulator made without one, in tests, keeps its own).</summary>
    HleIndexTable Indexes => _emu.Vita is { } vita ? vita.HleIndexes : _ownIndexes ??= new HleIndexTable();
    readonly Dictionary<uint, int> _indexByNid = new();
    readonly Dictionary<uint, Action<HleContext>> _impls = new();
    readonly Dictionary<uint, string> _implNames = new();
    /// <summary>NIDs whose implementation returns a value, not an error code (<see cref="HleAttribute.ReturnsValue"/>).</summary>
    readonly HashSet<uint> _valueReturns = new();
    /// <summary>What each implemented NID is on the Vita: its declared cost class and callback-wait flag.</summary>
    readonly Dictionary<uint, (HleCost Cost, bool CallbackWait)> _declarations = new();
    /// <summary>The <see cref="HleModule.DefaultCost"/> of the HLE modules that implement functions of each firmware
    /// library (by the library's name in the NID database, a name the firmware fixes), null when two of them declare
    /// different classes: what the library's other NIDs are when the HLE does not implement them.</summary>
    readonly Dictionary<string, HleCost?> _libraryCosts = new(StringComparer.Ordinal);
    readonly Dictionary<uint, Func<uint>> _varFactories = new();
    readonly Dictionary<uint, uint> _vars = new();
    public List<HleModule> Modules { get; } = new();

    public HleRegistry(Emulator emu) => _emu = emu;

    /// <summary>Ends the modules that hold host resources (the <see cref="IDisposable"/> ones: SceCompat's core and its GE
    /// device) when their process ends or re-executes; each once, a failure logged.</summary>
    public void DisposeModules()
    {
        foreach (HleModule module in Modules)
        {
            if (module is not IDisposable disposable)
            {
                continue;
            }

            try
            {
                disposable.Dispose();
            }
            catch (Exception failure) when (failure is InvalidOperationException or ObjectDisposedException)
            {
                Log.Warn($"{module.GetType().Name}: end failed: {failure.Message}");
            }
        }
    }

    public IReadOnlyList<HleFunction> Functions => _functions;

    public void RegisterVariable(string name, Func<uint> factory)
    {
        foreach (var n in NidDatabase.LookupName(name)) _varFactories[n.Nid] = factory;
    }

    public void RegisterVariable(uint nid, Func<uint> factory) => _varFactories[nid] = factory;

    /// <summary>The data exports (variables) the HLE provides to importers: their NIDs.</summary>
    public IReadOnlyCollection<uint> VariableNids { get { lock (_vars) return _varFactories.Keys.ToArray(); } }

    /// <summary>
    /// Registers every [Hle] method of the module. The bindings (attribute data plus an argument marshalling invoker)
    /// are generated at compile time by VitaSharp.Core.Generators, in declaration order; later registrations of a NID
    /// replace earlier ones.
    /// </summary>
    public void RegisterModule(HleModule module)
    {
        var bindings = HleBindings.For(module.GetType())
            ?? throw new InvalidOperationException($"HLE module {module.GetType().FullName} has no generated bindings (modules must live in VitaSharp.Core)");
        module.Emu = _emu;
        Modules.Add(module);
        foreach (var b in bindings)
        {
            string name = b.Name ?? b.Method;
            var nids = b.Nid != 0 ? [b.Nid]
                : NidDatabase.LookupName(name).Where(n => b.Library == null || n.Library == b.Library).Select(n => n.Nid).ToArray();
            if (nids.Length == 0)
            {
                Log.Warn($"HLE: no NID known for {module.GetType().Name}.{name}");
                continue;
            }
            var invoker = b.Bind(module);
            HleCost cost = b.Cost != HleCost.Module ? b.Cost : module.DefaultCost;
            foreach (var nid in nids)
            {
                NoteModuleNid(module, nid);
                NoteLibraryCost(NidDatabase.Lookup(nid)?.Library ?? b.Library, module.DefaultCost);
                if (!module.FirmwareFallbackOnly) _serviceNids.Add(nid);
                if (module.FirmwareForEveryImporter) _firmwareForEveryImporterNids.Add(nid);
                _impls[nid] = invoker;
                _implNames[nid] = name;
                if (b.ReturnsValue) _valueReturns.Add(nid); else _valueReturns.Remove(nid);
                (HleCost Cost, bool CallbackWait) declaration = (cost, b.CallbackWait);
                _declarations[nid] = declaration;
                if (_indexByNid.TryGetValue(nid, out var idx))
                {
                    _functions[idx].Invoke = invoker;
                    _functions[idx].ReturnsValue = b.ReturnsValue;
                    Declare(_functions[idx], declaration);
                }
            }
        }
        module.Initialize();
    }

    public bool IsImplemented(uint nid) => _impls.ContainsKey(nid);

    HashSet<string>? _implementedLibraries;
    readonly HashSet<uint> _serviceNids = new();

    /// <summary>The HLE has function <paramref name="nid"/>.</summary>
    public bool Implements(uint nid) => _impls.ContainsKey(nid);

    /// <summary>The HLE has function <paramref name="nid"/> in a module that is not
    /// <see cref="HleModule.FirmwareFallbackOnly"/> (a function it serves, not only a fallback of the firmware's).</summary>
    public bool ServesFunction(uint nid) => _serviceNids.Contains(nid);

    /// <summary>True when VitaSharp's HLE emulates <paramref name="library"/>: it implements at least one of its functions
    /// in a module that is not <see cref="HleModule.FirmwareFallbackOnly"/> (by the NID database's library of each NID).</summary>
    public bool ImplementsLibrary(string library)
    {
        _implementedLibraries ??= _serviceNids.Select(NidDatabase.Lookup).Where(i => i != null).Select(i => i!.Library).ToHashSet(StringComparer.Ordinal);
        return _implementedLibraries.Contains(library);
    }

    /// <summary>
    /// True when the importers of <paramref name="library"/> get its firmware module for the functions the HLE does not
    /// have: the HLE serves none of it (it knows nothing of it, or has it only as a firmware fallback), or its module is
    /// <see cref="HleModule.FirmwareForEveryImporter"/>. System software takes such a library from the firmware
    /// (<see cref="Emulator.LoadFirmwareDependencies"/>); a game takes it from the preloads and sysmodule's
    /// dependencies (<see cref="Emulator.GameTakesFirmwarePreload"/>). The coverage counts those functions as LLE.
    /// </summary>
    public bool FirmwareServesImporters(string library) => FirmwareForEveryImporter(library) || !ImplementsLibrary(library);

    HashSet<string>? _knownLibraries, _firmwareForEveryImporter;
    readonly HashSet<uint> _firmwareForEveryImporterNids = new();

    /// <summary>True when <paramref name="library"/> comes from its firmware module for every importer: the HLE module
    /// that has its functions is <see cref="HleModule.FirmwareForEveryImporter"/>.</summary>
    public bool FirmwareForEveryImporter(string library)
    {
        _firmwareForEveryImporter ??= _firmwareForEveryImporterNids.Select(NidDatabase.Lookup).Where(i => i != null).Select(i => i!.Library).ToHashSet(StringComparer.Ordinal);
        return _firmwareForEveryImporter.Contains(library);
    }

    /// <summary>True when the HLE has any function of <paramref name="library"/>, firmware fallbacks included: a library
    /// it knows nothing of can only come from the firmware module.</summary>
    public bool KnowsLibrary(string library)
    {
        _knownLibraries ??= _impls.Keys.Select(NidDatabase.Lookup).Where(i => i != null).Select(i => i!.Library).ToHashSet(StringComparer.Ordinal);
        return _knownLibraries.Contains(library);
    }

    /// <summary>Registers host code as a callable HLE function without a NID (test harnesses: guest vtables and
    /// callbacks that land in C#). Returns the SVC number for <see cref="Loader.Linker.WriteHleStub"/>.</summary>
    public uint RegisterHostFunction(string name, Action<HleContext> body)
    {
        lock (_functions)
        {
            int index = Indexes.NewHostIndex();
            if (index < _functions.Count && !ReferenceEquals(_functions[index], s_unbound))
            {
                // this process's table differs from the shared one (a state of an older build): a slot of its own
                index = _functions.Count;
            }

            Place(index, new HleFunction { Nid = 0, Name = name, Library = "host", Invoke = body });
            return (uint)index;
        }
    }

    /// <summary>Registers host code at an index every process of the system serves (code in shared kernel memory that
    /// enters C# from a thread of any process, such as the plugin hooks' counting stubs). Returns the SVC number.</summary>
    public uint RegisterSharedHostFunction(string name, Action<HleContext> body)
    {
        return (uint)Indexes.NewSharedHostIndex(name, body);
    }

    /// <summary>Declares a shared host function by name (no index yet): a save state that calls it finds it on load.</summary>
    public void DeclareSharedHostFunction(string name, Action<HleContext> body)
    {
        Indexes.DeclareSharedHost(name, body);
    }

    /// <summary>The record of a shared host function in this registry: the emulator's own code (no firmware function),
    /// so r0 is not an error code, and it charges no more guest time than a few instructions.</summary>
    static HleFunction SharedHostRecord(string name, Action<HleContext> body)
    {
        var shared = new HleFunction { Nid = 0, Name = name, Library = "host", Invoke = body, ReturnsValue = true };
        shared.SetCost(HleCost.UserInline);
        return shared;
    }

    /// <summary>Puts <paramref name="function"/> at <paramref name="index"/>, the slots before it unbound.</summary>
    void Place(int index, HleFunction function)
    {
        while (_functions.Count <= index)
        {
            _functions.Add(s_unbound);
        }

        _functions[index] = function;
    }

    public uint GetHleFunctionIndex(uint nid, uint libNid, string libName)
    {
        if (libNid != 0 && !string.IsNullOrEmpty(libName))
        {
            _libraryNames.TryAdd(libNid, libName);
        }

        lock (_functions)
        {
            if (_indexByNid.TryGetValue(nid, out var idx)) return (uint)idx;
            // the index every process uses for the NID; a table that differs from the shared one (a state of an
            // older build) gets a slot of its own
            int index = Indexes.IndexOf(nid);
            if (index < _functions.Count && !ReferenceEquals(_functions[index], s_unbound))
            {
                index = _functions.Count;
            }

            var info = NidDatabase.Lookup(nid);
            HleFunction f = NewFunction(nid, _implNames.GetValueOrDefault(nid) ?? info?.Name ?? $"NID_{nid:X8}", libName);
            Place(index, f);
            _indexByNid[nid] = index;
            return (uint)index;
        }
    }

    public uint GetHleVariable(uint nid)
    {
        lock (_vars)
        {
            if (_vars.TryGetValue(nid, out var a)) return a;
            if (!_varFactories.TryGetValue(nid, out var f)) return 0;
            a = f();
            _vars[nid] = a;
            return a;
        }
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<(uint LibNid, uint Nid), uint> _ownRedirects = new();

    /// <summary>The HLE functions plugin hooks took, by library and function NID: the emulated Vita's (its one kernel's
    /// hooks hold for every process, those that exist and those made later), or this registry's own for an emulator
    /// made without one (tests).</summary>
    System.Collections.Concurrent.ConcurrentDictionary<(uint LibNid, uint Nid), uint> Redirects =>
        _emu.Vita is { } vita ? vita.HookRedirects : _ownRedirects;

    /// <summary>Import stubs bound to HLE function <paramref name="nid"/> of library <paramref name="libNid"/> from now
    /// on jump to <paramref name="target"/> (a plugin's hook on the firmware export of the function, emulator option
    /// "Enable Kernel Plugins"). Another library's function with the same NID (a kernel twin) is another function.</summary>
    public void Redirect(uint libNid, uint nid, uint target) => Redirects[(libNid, nid)] = target;

    /// <summary>Import stubs of <paramref name="nid"/> of library <paramref name="libNid"/> bind to the HLE function again.</summary>
    public void ClearRedirect(uint libNid, uint nid) => Redirects.TryRemove((libNid, nid), out _);

    /// <summary>The HLE functions a user plugin's hook took in this process (a hook on a firmware user module's export,
    /// which on the Vita is this process's code): by library and function NID.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<(uint LibNid, uint Nid), uint> _processRedirects = new();

    /// <summary>This process's import stubs bound to HLE function <paramref name="nid"/> of library
    /// <paramref name="libNid"/> from now on jump to <paramref name="target"/> (a user plugin's hook).</summary>
    public void RedirectInProcess(uint libNid, uint nid, uint target)
    {
        lock (_processRedirects)
        {
            _processRedirects[(libNid, nid)] = target;
            CountProcessRedirects();
        }
    }

    /// <summary>How many registries (processes) have a user plugin's hook on an HLE function: the one static field the
    /// <see cref="HleEntryCalls"/> wrappers read for user hooks (0 in a session without them).</summary>
    static int s_registriesWithProcessHooks;

    /// <summary>A user plugin's hook took an HLE function in some process of this host (the wrappers' first check;
    /// <see cref="ProcessHookOf"/> then looks in the calling module's own process).</summary>
    internal static bool AnyProcessHooks => Volatile.Read(ref s_registriesWithProcessHooks) != 0;

    /// <summary>Publishes this registry's redirect count and keeps <see cref="s_registriesWithProcessHooks"/> (called
    /// under the lock of <see cref="_processRedirects"/>).</summary>
    void CountProcessRedirects()
    {
        int before = _processRedirectCount;
        int after = _processRedirects.Count;
        Volatile.Write(ref _processRedirectCount, after);
        if (before == 0 && after != 0)
        {
            Interlocked.Increment(ref s_registriesWithProcessHooks);
        }
        else if (before != 0 && after == 0)
        {
            Interlocked.Decrement(ref s_registriesWithProcessHooks);
        }
    }

    /// <summary>How many HLE functions a user plugin's hook took in this process (<see cref="_processRedirects"/>):
    /// the field <see cref="HasProcessHooks"/> reads.</summary>
    int _processRedirectCount;

    /// <summary>A user plugin's hook took an HLE function in this process (one field read: the check every
    /// <see cref="HleEntryCalls"/> wrapper makes first).</summary>
    public bool HasProcessHooks => Volatile.Read(ref _processRedirectCount) != 0;

    /// <summary>The name of each library an import or export bound to this registry's functions belongs to, by its
    /// library NID (<see cref="GetHleFunctionIndex"/>: the user shells' export tables and the modules' imports): what
    /// <see cref="ProcessHookOf"/> tells two libraries exporting the same function NID apart by.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> _libraryNames = new();

    /// <summary>
    /// The thunk of the user plugin's hook that took function <paramref name="nid"/> of library
    /// <paramref name="library"/> in this process, or 0. A NID can be exported by two libraries (two functions, in
    /// two modules' code): a hook on one is not the other's, so the hook's library (its NID, named by the export
    /// table the hook was resolved in) has to be <paramref name="library"/>; null takes the NID in any library. A
    /// hook whose library NID was never bound here (no name known) is taken for any library.
    /// </summary>
    public uint ProcessHookOf(uint nid, string? library)
    {
        if (!HasProcessHooks)
        {
            return 0;
        }

        foreach (KeyValuePair<(uint LibNid, uint Nid), uint> redirect in _processRedirects)
        {
            if (redirect.Key.Nid != nid)
            {
                continue;
            }

            if (library == null || !_libraryNames.TryGetValue(redirect.Key.LibNid, out string? hookLibrary) || hookLibrary == library)
            {
                return redirect.Value;
            }
        }

        return 0;
    }

    /// <summary>This process's import stubs of <paramref name="nid"/> of library <paramref name="libNid"/> bind to the
    /// HLE function again.</summary>
    public void ClearRedirectInProcess(uint libNid, uint nid)
    {
        lock (_processRedirects)
        {
            _processRedirects.TryRemove((libNid, nid), out _);
            CountProcessRedirects();
        }
    }

    /// <summary>A guest-code implementation (<see cref="HleGuestCode"/>) of an implemented NID, placed in the calling
    /// thread's address space; 0 when there is none (or VITASHARP_HLE_GUEST_CODE=0).</summary>
    public uint GetHleGuestCode(uint nid) =>
        _impls.ContainsKey(nid) ? HleGuestCode.AddressOf(nid, _emu.Memory, n => GetHleFunctionIndex(n, 0, NidDatabase.Lookup(n)?.Library ?? ""), this) : 0;

    /// <summary>The thunk of the plugin hook that took function <paramref name="nid"/> of library
    /// <paramref name="libNid"/> (this process's user hook first, then the system's kernel hook), else
    /// <see cref="GetHleGuestCode(uint)"/>.</summary>
    public uint GetHleGuestCode(uint nid, uint libNid)
    {
        if (!_processRedirects.IsEmpty && _processRedirects.TryGetValue((libNid, nid), out uint own))
        {
            return own;
        }

        return !Redirects.IsEmpty && Redirects.TryGetValue((libNid, nid), out uint redirect) ? redirect : GetHleGuestCode(nid);
    }

    /// <summary>After a save state load: places this build's guest-code routines again (<see cref="HleGuestCode.Replace"/>).</summary>
    public Func<uint, bool> ReplaceGuestCode() => HleGuestCode.Replace(_emu.Memory, n => GetHleFunctionIndex(n, 0, NidDatabase.Lookup(n)?.Library ?? ""), this);

    /// <summary>The record of <paramref name="nid"/> imported from <paramref name="library"/>: its implementation and
    /// declaration, or, for a NID the HLE does not implement, the class of its library
    /// (<see cref="UnimplementedCost"/>).</summary>
    HleFunction NewFunction(uint nid, string name, string library)
    {
        var f = new HleFunction
        {
            Nid = nid,
            Name = name,
            Library = library,
            Invoke = _impls.GetValueOrDefault(nid),
            ReturnsValue = _valueReturns.Contains(nid),
        };
        if (_declarations.TryGetValue(nid, out (HleCost Cost, bool CallbackWait) declaration))
        {
            Declare(f, declaration);
        }
        else
        {
            f.SetCost(UnimplementedCost(nid, library));
        }

        return f;
    }

    void NoteLibraryCost(string? library, HleCost cost)
    {
        if (string.IsNullOrEmpty(library))
        {
            return;
        }

        if (!_libraryCosts.TryGetValue(library, out HleCost? known))
        {
            _libraryCosts[library] = cost;
        }
        else if (known != cost)
        {
            _libraryCosts[library] = null;
        }
    }

    /// <summary>
    /// What a NID the HLE does not implement charges: a function of a user-mode library (libgxm, librtc, libngs,
    /// libfiber, the C library: the library's HLE module declares a user-mode <see cref="HleModule.DefaultCost"/>) is
    /// user code on the Vita, its library's class; anything else is a kernel entry, the class of a syscall (a service
    /// function the HLE does not have is not assumed to wait). The library is the NID database's, else the importing
    /// stub's.
    /// </summary>
    public HleCost UnimplementedCost(uint nid, string library)
    {
        string? name = NidDatabase.Lookup(nid)?.Library;
        if (string.IsNullOrEmpty(name))
        {
            name = library;
        }

        if (!string.IsNullOrEmpty(name) && _libraryCosts.TryGetValue(name, out HleCost? cost)
            && cost is HleCost.UserInline or HleCost.UserCall or HleCost.UserLibrary)
        {
            return cost.Value;
        }

        return HleCost.KernelEntry;
    }

    static void Declare(HleFunction function, (HleCost Cost, bool CallbackWait) declaration)
    {
        function.SetCost(declaration.Cost);
        function.IsCallbackWait = declaration.CallbackWait;
    }

    public HleFunction GetFunction(uint index)
    {
        if (index < _functions.Count && !ReferenceEquals(_functions[(int)index], s_unbound))
        {
            return _functions[(int)index];
        }

        return Unbound(index);
    }

    /// <summary>An index another process gave out (a stub in shared kernel memory, a hook's trampoline): this process's
    /// function for the same NID, made on its first call.</summary>
    HleFunction Unbound(uint index)
    {
        uint nid = Indexes.NidAt(index);
        if (nid == 0)
        {
            if (Indexes.SharedHostFunction(index, out string name, out Action<HleContext> body))
            {
                lock (_functions)
                {
                    HleFunction shared = SharedHostRecord(name, body);
                    Place((int)index, shared);
                    return shared;
                }
            }

            return _functions[0];
        }

        uint mine = GetHleFunctionIndex(nid, 0, NidDatabase.Lookup(nid)?.Library ?? "");
        return mine == index ? _functions[(int)index] : _functions[(int)mine];
    }

}

/// <summary>One [Hle] attribute on a module method, as emitted by the source generator.</summary>
/// <param name="Method">The C# method name.</param>
/// <param name="Name">The attribute's explicit export name (null: the method name).</param>
/// <param name="Nid">The attribute's explicit NID (0: looked up by name).</param>
/// <param name="Library">The attribute's library restriction for the NID lookup.</param>
/// <param name="Layout">Argument/return marshalling description (compared against a reflection reference in tests).</param>
/// <param name="Bind">Creates the invoker for a module instance.</param>
/// <param name="Default">The attribute's <see cref="HleAttribute.Default"/>: a default, not a real implementation.</param>
/// <param name="Cost">The attribute's <see cref="HleAttribute.Cost"/>.</param>
/// <param name="CallbackWait">The attribute's <see cref="HleAttribute.CallbackWait"/>.</param>
public sealed record HleBinding(string Method, string? Name, uint Nid, string? Library, string Layout, Func<HleModule, Action<HleContext>> Bind,
    bool Default = false, bool ReturnsValue = false, HleCost Cost = HleCost.Module, bool CallbackWait = false);

/// <summary>A concrete HLE module type with its factory and bindings.</summary>
public sealed record HleModuleEntry(Type Type, Func<HleModule> Create, Func<HleBinding[]> Bindings);

/// <summary>
/// Compile-time HLE binder (VitaSharp.Core.Generators.HleBinderGenerator): the module table and every [Hle] method's
/// AAPCS-VFP marshalling, generated from the source instead of built with reflection and System.Linq.Expressions at
/// startup, so it works under Native AOT and trimming.
/// </summary>
public static partial class HleBindings
{
    static readonly HleModuleEntry[] _modules = GeneratedModules();
    static readonly Dictionary<Type, HleBinding[]> _byType = new();

    private static partial HleModuleEntry[] GeneratedModules();

    /// <summary>Every concrete module type of the core.</summary>
    public static IReadOnlyList<HleModuleEntry> Modules => _modules;

    /// <summary>The bindings of a module type, in declaration order (null: not a generated module).</summary>
    public static HleBinding[]? For(Type moduleType)
    {
        lock (_byType)
        {
            if (_byType.TryGetValue(moduleType, out var b)) return b;
            foreach (var m in _modules)
                if (m.Type == moduleType) { b = m.Bindings(); _byType[moduleType] = b; return b; }
            return null;
        }
    }
}
