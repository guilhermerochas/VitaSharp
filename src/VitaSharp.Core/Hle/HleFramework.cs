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
}

/// <summary>Base class for an HLE module (a group of related exported functions).</summary>
public abstract class HleModule
{
    public Emulator Emu { get; internal set; } = null!;
    protected GuestMemory Mem => Emu.Memory;
    protected Kernel.Kernel K => Emu.Kernel;
    public virtual void Initialize() { }
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
            if ((_words & 1) != 0) NextU32();
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
    bool? _cbWait;
    public bool IsCallbackWait => _cbWait ??= Name.EndsWith("CB", StringComparison.Ordinal);
    public long CallCount;
    /// <summary>The "unimplemented" warning was issued (skips building its key on every later call).</summary>
    public bool WarnedUnimplemented;
    /// <summary>Emulated CPU cycles charged for this function so far (profiling).</summary>
    public long ChargedCycles;
    /// <summary>--profile: guest threads record the HLE function they are in (GuestThread.CurrentHle).</summary>
    public static bool Track;
    /// <summary>--profile-hle: also times every HLE call per thread (GuestThread.HleProfile): two timestamps and two
    /// thread cycle-counter reads per call, which slows HLE-heavy games noticeably.</summary>
    public static bool Profile;

    long _cost = -1;
    /// <summary>Emulated CPU cost (guest instructions) charged per call: real syscalls (waits, signals, I/O, display)
    /// cost a kernel entry and often a context switch on the Vita; user-mode library calls (lwmutex, TLS, Clib, Gxm)
    /// are cheap.</summary>
    public long Cost => _cost >= 0 ? _cost : _cost = ClassifyCost(Name);

    static long ClassifyCost(string n)
    {
        // user-mode library code that never enters the kernel on the uncontended path: a TLS lookup is a few
        // instructions, an lwmutex lock/unlock an LDREX/STREX pair plus checks, a Clib call its prologue (the bytes a
        // memcpy/memset moves are charged by the implementation). Charging these like syscalls made software-rendering
        // games (thousands of calls per frame) run out of emulated CPU time.
        if (n.StartsWith("sceKernelGetTLSAddr") || n.StartsWith("sceKernelGetThreadId") || n.StartsWith("sceKernelAtomic"))
            return 20;
        if (n.StartsWith("sceKernelLockLwMutex") || n.StartsWith("sceKernelUnlockLwMutex") || n.StartsWith("sceKernelTryLockLwMutex")
            || n.StartsWith("sceClib"))
            return 50;
        if (n.StartsWith("sceGxm")
            || n.StartsWith("sceNgs") || n.StartsWith("sceKernelGetProcessTime") || n.StartsWith("sceRtc") || n.StartsWith("sceFiber")
            || n.StartsWith("sceKernelGetSystemTime") || n == "sceKernelGetThreadTLSAddr")
            return 200;
        // Delays are in this class too, so a DelayThread(1) polling loop costs 27 us of charge + the 30 us latency floor of
        // Wait.Sleep per iteration (57 us; Killzone's job waits make ~500 per frame). Charging delays as a kernel entry
        // only (2_000) looked more realistic but changed Killzone's scripted walk (another route, where the renderer ran
        // out of VRAM), so it stays until it can be checked against hardware: docs/notes/kz-mainthread.md.
        if (n.StartsWith("sceKernelWait") || n.StartsWith("sceKernelSignal") || n.StartsWith("sceKernelDelay") || n.StartsWith("sceKernelSetEventFlag")
            || n.StartsWith("sceKernelClearEventFlag") || n.StartsWith("sceKernelPollEventFlag") || n.StartsWith("sceKernelLockMutex") || n.StartsWith("sceKernelUnlockMutex")
            || n.StartsWith("sceKernelPollSema") || n.StartsWith("sceKernelStartThread") || n.StartsWith("sceKernelChangeThread") || n.StartsWith("sceKernelExitThread")
            || n.StartsWith("sceIo") || n.StartsWith("sceDisplay") || n.StartsWith("sceAudioOut") || n.StartsWith("sceCtrl") || n.StartsWith("sceTouch")
            || n.StartsWith("sceKernelSendMsgPipe") || n.StartsWith("sceKernelReceiveMsgPipe") || n.StartsWith("sceCamera")
            || n.StartsWith("sceNetEpollWait") || n.StartsWith("sceNetRecv") || n.StartsWith("sceRudpPollWait") || n.StartsWith("sceHttpWaitRequest")
            || n is "sceKernelSendSignal" or "sceKernelSetEvent" or "sceKernelPulseEvent" or "sceKernelPollEvent" or "sceKernelClearEvent"
            || (n.StartsWith("sceUlt") && (n.EndsWith("Lock") || n.EndsWith("LockRead") || n.EndsWith("LockWrite") || n.EndsWith("Acquire")
                || n.EndsWith("Wait") || n.EndsWith("Pop") || n.EndsWith("Push") || n.EndsWith("Join"))))
            return 12_000; // ~40 us at 300 MIPS: syscall + scheduler
        return 2_000;
    }
}

/// <summary>Registry of HLE functions and variables; binds C# methods to guest calling conventions.</summary>
public sealed class HleRegistry : IImportResolver
{
    readonly Emulator _emu;
    readonly List<HleFunction> _functions = [new HleFunction { Nid = 0, Name = "<invalid>" }];
    readonly Dictionary<uint, int> _indexByNid = new();
    readonly Dictionary<uint, Action<HleContext>> _impls = new();
    readonly Dictionary<uint, string> _implNames = new();
    readonly Dictionary<uint, Func<uint>> _varFactories = new();
    readonly Dictionary<uint, uint> _vars = new();
    public List<HleModule> Modules { get; } = new();

    public HleRegistry(Emulator emu) => _emu = emu;

    public IReadOnlyList<HleFunction> Functions => _functions;

    public void RegisterVariable(string name, Func<uint> factory)
    {
        foreach (var n in NidDatabase.LookupName(name)) _varFactories[n.Nid] = factory;
    }

    public void RegisterVariable(uint nid, Func<uint> factory) => _varFactories[nid] = factory;

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
            foreach (var nid in nids)
            {
                _impls[nid] = invoker;
                _implNames[nid] = name;
                if (_indexByNid.TryGetValue(nid, out var idx)) _functions[idx].Invoke = invoker;
            }
        }
        module.Initialize();
    }

    public bool IsImplemented(uint nid) => _impls.ContainsKey(nid);

    /// <summary>Registers host code as a callable HLE function without a NID (test harnesses: guest vtables and
    /// callbacks that land in C#). Returns the SVC number for <see cref="Loader.Linker.WriteHleStub"/>.</summary>
    public uint RegisterHostFunction(string name, Action<HleContext> body)
    {
        lock (_functions)
        {
            _functions.Add(new HleFunction { Nid = 0, Name = name, Library = "host", Invoke = body });
            return (uint)(_functions.Count - 1);
        }
    }

    public uint GetHleFunctionIndex(uint nid, uint libNid, string libName)
    {
        lock (_functions)
        {
            if (_indexByNid.TryGetValue(nid, out var idx)) return (uint)idx;
            var info = NidDatabase.Lookup(nid);
            var f = new HleFunction
            {
                Nid = nid,
                Name = _implNames.GetValueOrDefault(nid) ?? info?.Name ?? $"NID_{nid:X8}",
                Library = libName,
                Invoke = _impls.GetValueOrDefault(nid),
            };
            _functions.Add(f);
            _indexByNid[nid] = _functions.Count - 1;
            return (uint)(_functions.Count - 1);
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

    public HleFunction GetFunction(uint index) => index < _functions.Count ? _functions[(int)index] : _functions[0];

}

/// <summary>One [Hle] attribute on a module method, as emitted by the source generator.</summary>
/// <param name="Method">The C# method name.</param>
/// <param name="Name">The attribute's explicit export name (null: the method name).</param>
/// <param name="Nid">The attribute's explicit NID (0: looked up by name).</param>
/// <param name="Library">The attribute's library restriction for the NID lookup.</param>
/// <param name="Layout">Argument/return marshalling description (compared against a reflection reference in tests).</param>
/// <param name="Bind">Creates the invoker for a module instance.</param>
public sealed record HleBinding(string Method, string? Name, uint Nid, string? Library, string Layout, Func<HleModule, Action<HleContext>> Bind);

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
