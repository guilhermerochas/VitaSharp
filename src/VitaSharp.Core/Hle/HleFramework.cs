using System.Linq.Expressions;
using System.Reflection;
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

    long _cost = -1;
    /// <summary>Emulated CPU cost (guest instructions) charged per call: real syscalls (waits, signals, I/O, display)
    /// cost a kernel entry and often a context switch on the Vita; user-mode library calls (lwmutex, TLS, Clib, Gxm)
    /// are cheap.</summary>
    public long Cost => _cost >= 0 ? _cost : _cost = ClassifyCost(Name);

    static long ClassifyCost(string n)
    {
        if (n.StartsWith("sceKernelLockLwMutex") || n.StartsWith("sceKernelUnlockLwMutex") || n.StartsWith("sceKernelTryLockLwMutex")
            || n.StartsWith("sceKernelGetTLSAddr") || n.StartsWith("sceKernelGetThreadId") || n.StartsWith("sceClib") || n.StartsWith("sceGxm")
            || n.StartsWith("sceNgs") || n.StartsWith("sceKernelGetProcessTime") || n.StartsWith("sceRtc") || n.StartsWith("sceFiber")
            || n.StartsWith("sceKernelAtomic") || n.StartsWith("sceKernelGetSystemTime"))
            return 200;
        if (n.StartsWith("sceKernelWait") || n.StartsWith("sceKernelSignal") || n.StartsWith("sceKernelDelay") || n.StartsWith("sceKernelSetEventFlag")
            || n.StartsWith("sceKernelClearEventFlag") || n.StartsWith("sceKernelPollEventFlag") || n.StartsWith("sceKernelLockMutex") || n.StartsWith("sceKernelUnlockMutex")
            || n.StartsWith("sceKernelPollSema") || n.StartsWith("sceKernelStartThread") || n.StartsWith("sceKernelChangeThread") || n.StartsWith("sceKernelExitThread")
            || n.StartsWith("sceIo") || n.StartsWith("sceDisplay") || n.StartsWith("sceAudioOut") || n.StartsWith("sceCtrl") || n.StartsWith("sceTouch")
            || n.StartsWith("sceKernelSendMsgPipe") || n.StartsWith("sceKernelReceiveMsgPipe") || n.StartsWith("sceCamera"))
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

    public void RegisterModule(HleModule module)
    {
        module.Emu = _emu;
        Modules.Add(module);
        foreach (var mi in module.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            foreach (var attr in mi.GetCustomAttributes<HleAttribute>())
            {
                string name = attr.Name ?? mi.Name;
                var nids = attr.Nid != 0 ? [attr.Nid]
                    : NidDatabase.LookupName(name).Where(n => attr.Library == null || n.Library == attr.Library).Select(n => n.Nid).ToArray();
                if (nids.Length == 0)
                {
                    Log.Warn($"HLE: no NID known for {module.GetType().Name}.{name}");
                    continue;
                }
                var invoker = Bind(module, mi);
                foreach (var nid in nids)
                {
                    _impls[nid] = invoker;
                    _implNames[nid] = name;
                    if (_indexByNid.TryGetValue(nid, out var idx)) _functions[idx].Invoke = invoker;
                }
            }
        }
        module.Initialize();
    }

    public bool IsImplemented(uint nid) => _impls.ContainsKey(nid);

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

    // ------------------------------------------------------------------ binder

    static readonly MethodInfo MArgU32 = typeof(HleContext).GetMethod(nameof(HleContext.ArgU32))!;
    static readonly MethodInfo MArgU64 = typeof(HleContext).GetMethod(nameof(HleContext.ArgU64))!;
    static readonly MethodInfo MArgU64Stack = typeof(HleContext).GetMethod(nameof(HleContext.ArgU64Stack))!;
    static readonly MethodInfo MArgStack = typeof(HleContext).GetMethod(nameof(HleContext.ArgStack))!;
    static readonly MethodInfo MArgF32 = typeof(HleContext).GetMethod(nameof(HleContext.ArgF32))!;
    static readonly MethodInfo MArgString = typeof(HleContext).GetMethod(nameof(HleContext.ArgString))!;

    /// <summary>
    /// Builds a compiled invoker following the AAPCS-VFP layout used by the Vita: 32-bit integers in
    /// r0-r3 then stack, 64-bit values in aligned register pairs, floats in s0-s15.
    /// </summary>
    static Action<HleContext> Bind(HleModule module, MethodInfo mi)
    {
        var ctx = Expression.Parameter(typeof(HleContext), "ctx");
        var args = new List<Expression>();
        int gpr = 0, stack = 0, fpr = 0;
        foreach (var p in mi.GetParameters())
        {
            var t = p.ParameterType;
            if (t == typeof(HleContext)) { args.Add(ctx); continue; }
            if (t == typeof(VarArgs)) { args.Add(Expression.New(typeof(VarArgs).GetConstructors()[0], ctx, Expression.Constant(gpr))); continue; }
            if (t == typeof(float))
            {
                args.Add(Expression.Call(MArgF32, ctx, Expression.Constant(fpr++)));
                continue;
            }
            bool wide = t == typeof(long) || t == typeof(ulong) || t == typeof(double);
            Expression raw;
            if (wide)
            {
                if ((gpr & 1) != 0) gpr++;
                if (gpr + 2 <= 4) { raw = Expression.Call(MArgU64, ctx, Expression.Constant(gpr)); gpr += 2; }
                else { gpr = 4; stack = (stack + 7) & ~7; raw = Expression.Call(MArgU64Stack, ctx, Expression.Constant(stack)); stack += 8; }
            }
            else
            {
                if (gpr < 4) raw = Expression.Call(MArgU32, ctx, Expression.Constant(gpr++));
                else { raw = Expression.Call(MArgStack, ctx, Expression.Constant(stack)); stack += 4; }
            }
            args.Add(Convert(raw, t));
        }
        Expression call = Expression.Call(Expression.Constant(module), mi, args);
        Expression body;
        var rt = mi.ReturnType;
        if (rt == typeof(void)) body = call;
        else
        {
            var ret = typeof(HleContext).GetMethods().First(m => m.Name == nameof(HleContext.Return)
                && m.GetParameters()[0].ParameterType == NormalizeReturn(rt));
            body = Expression.Call(ctx, ret, ConvertReturn(call, rt));
        }
        return Expression.Lambda<Action<HleContext>>(body, ctx).Compile();
    }

    static Type NormalizeReturn(Type t)
    {
        if (t == typeof(int) || t == typeof(uint) || t == typeof(ulong) || t == typeof(long) || t == typeof(float) || t == typeof(bool) || t == typeof(double)) return t;
        if (t.IsEnum) return NormalizeReturn(Enum.GetUnderlyingType(t));
        if (t == typeof(short) || t == typeof(sbyte)) return typeof(int);
        return typeof(uint); // ushort, byte, Ptr<T>
    }

    static Expression ConvertReturn(Expression e, Type t)
    {
        var target = NormalizeReturn(t);
        if (t == target) return e;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Ptr<>)) return Expression.Field(e, "Address");
        return Expression.Convert(e, target);
    }

    static Expression Convert(Expression raw, Type t)
    {
        if (t == raw.Type) return raw;
        if (t == typeof(string)) return Expression.Call(MArgString, raw);
        if (t == typeof(bool)) return Expression.NotEqual(raw, Expression.Constant(0u));
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Ptr<>))
            return Expression.New(t.GetConstructor([typeof(uint)])!, raw);
        if (t == typeof(double)) return Expression.Call(typeof(BitConverter).GetMethod(nameof(BitConverter.UInt64BitsToDouble))!, raw);
        if (t.IsEnum) return Expression.Convert(Expression.Convert(raw, Enum.GetUnderlyingType(t)), t);
        return Expression.Convert(raw, t);
    }
}
