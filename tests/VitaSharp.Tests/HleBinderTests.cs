using System.Linq.Expressions;
using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The compile-time HLE binder (VitaSharp.Core.Generators) against the reflection + System.Linq.Expressions binder it
/// replaced, kept here as the reference: every [Hle] method of every module must produce the same bindings (order,
/// attribute data, argument/return layout), the module table must match reflection discovery, and for a probe module
/// covering every supported parameter and return type both invokers must pass identical arguments and leave
/// identical return registers.
/// </summary>
[Collection("guest")]
public sealed unsafe class HleBinderTests(GuestMemoryFixture fx)
{
    // ------------------------------------------------------------------ reference: the former reflection binder

    static readonly MethodInfo MArgU32 = typeof(HleContext).GetMethod(nameof(HleContext.ArgU32))!;
    static readonly MethodInfo MArgU64 = typeof(HleContext).GetMethod(nameof(HleContext.ArgU64))!;
    static readonly MethodInfo MArgU64Stack = typeof(HleContext).GetMethod(nameof(HleContext.ArgU64Stack))!;
    static readonly MethodInfo MArgStack = typeof(HleContext).GetMethod(nameof(HleContext.ArgStack))!;
    static readonly MethodInfo MArgF32 = typeof(HleContext).GetMethod(nameof(HleContext.ArgF32))!;
    static readonly MethodInfo MArgString = typeof(HleContext).GetMethod(nameof(HleContext.ArgString))!;

    sealed record RefBinding(string Method, string? Name, uint Nid, string? Library, string Layout, MethodInfo Info);

    /// <summary>Reflection discovery exactly as HleRegistry.RegisterModule did it (declared instance methods, every [Hle]).</summary>
    static List<RefBinding> ReflectionBindings(Type module)
    {
        var list = new List<RefBinding>();
        foreach (var mi in module.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            foreach (var attr in mi.GetCustomAttributes<HleAttribute>())
                list.Add(new RefBinding(mi.Name, attr.Name, attr.Nid, attr.Library, Layout(mi), mi));
        return list;
    }

    /// <summary>The layout string of the reference binder's argument walk (same tokens as the generator).</summary>
    static string Layout(MethodInfo mi)
    {
        var parts = new List<string>();
        int gpr = 0, stack = 0, fpr = 0;
        foreach (var p in mi.GetParameters())
        {
            var t = p.ParameterType;
            if (t == typeof(HleContext)) { parts.Add("ctx"); continue; }
            if (t == typeof(VarArgs)) { parts.Add($"va@{gpr}"); continue; }
            if (t == typeof(float)) { parts.Add($"s{fpr++}=float"); continue; }
            bool wide = t == typeof(long) || t == typeof(ulong) || t == typeof(double);
            string token;
            if (wide)
            {
                if ((gpr & 1) != 0) gpr++;
                if (gpr + 2 <= 4) { token = $"r{gpr}r{gpr + 1}"; gpr += 2; }
                else { gpr = 4; stack = (stack + 7) & ~7; token = $"sp{stack}+8"; stack += 8; }
            }
            else if (gpr < 4) token = $"r{gpr++}";
            else { token = $"sp{stack}"; stack += 4; }
            parts.Add($"{token}={Name(t)}");
        }
        var rt = mi.ReturnType;
        string ret;
        if (rt == typeof(void)) ret = "void";
        else
        {
            var norm = NormalizeReturn(rt);
            ret = rt == norm ? Name(norm) : rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Ptr<>) ? $"{Name(norm)}<-ptr" : $"{Name(norm)}<-{Name(rt)}";
        }
        return string.Join(",", parts) + "->" + ret;
    }

    static string Name(Type t)
    {
        if (t == typeof(int)) return "int";
        if (t == typeof(uint)) return "uint";
        if (t == typeof(long)) return "long";
        if (t == typeof(ulong)) return "ulong";
        if (t == typeof(short)) return "short";
        if (t == typeof(ushort)) return "ushort";
        if (t == typeof(byte)) return "byte";
        if (t == typeof(sbyte)) return "sbyte";
        if (t == typeof(float)) return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(string)) return "string";
        if (t == typeof(char)) return "char";
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition().FullName!;
            return $"{def[..def.IndexOf('`')].Replace('+', '.')}<{string.Join(",", t.GetGenericArguments().Select(Name))}>";
        }
        return t.FullName!.Replace('+', '.');
    }

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

    // ------------------------------------------------------------------ tests

    [Fact]
    public void ModuleTableMatchesReflectionDiscovery()
    {
        var reflected = typeof(Emulator).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(HleModule)) && !t.IsAbstract)
            .OrderBy(t => t.FullName).ToList();
        var generated = HleBindings.Modules.OrderBy(m => m.Type.FullName).Select(m => m.Type).ToList();
        Assert.Equal(reflected, generated);
        foreach (var m in HleBindings.Modules) Assert.IsType(m.Type, m.Create());
    }

    [Fact]
    public void EveryHleMethodBindsLikeTheReflectionBinder()
    {
        int total = 0;
        foreach (var m in HleBindings.Modules)
        {
            var expected = ReflectionBindings(m.Type).Select(b => $"{b.Method}|{b.Name}|{b.Nid:X8}|{b.Library}|{b.Layout}").ToList();
            var actual = HleBindings.For(m.Type)!.Select(b => $"{b.Method}|{b.Name}|{b.Nid:X8}|{b.Library}|{b.Layout}").ToList();
            Assert.Equal(expected, actual);
            total += actual.Count;
        }
        Assert.True(total > 2000, $"only {total} bindings");
    }

    const uint DataBase = 0x0040_0000, DataSize = 0x10000;

    /// <summary>Random registers/stack/VFP state whose 32-bit values all point at NUL-terminated text in guest memory.</summary>
    static void RandomState(CpuState* s, Random rng)
    {
        uint Word() => rng.Next(4) == 0 ? (uint)rng.NextInt64() : DataBase + (uint)rng.Next((int)DataSize - 0x100);
        for (int i = 0; i < 13; i++) s->R[i] = Word();
        s->R[13] = DataBase + 0x8000;
        for (uint i = 0; i < 64; i++) GuestMemory.Write(s->R[13] + i * 4, Word());
        for (int i = 0; i < 32; i++) ((ulong*)s->D)[i] = (ulong)rng.NextInt64();
    }

    static (uint R0, uint R1, ulong D0) Result(CpuState* s) => (s->R[0], s->R[1], ((ulong*)s->D)[0]);

    [Fact]
    public void GeneratedInvokersMarshalLikeTheReflectionBinder()
    {
        if (fx.Memory.FindBlock(DataBase) == null) fx.Memory.MapFixed(DataBase, DataSize, "binder test data");
        var rng = new Random(1234);
        var text = new byte[DataSize];
        for (int i = 0; i < text.Length; i++) text[i] = rng.Next(8) == 0 ? (byte)0 : (byte)rng.Next(0x20, 0x7F);
        text.CopyTo(GuestMemory.Span(DataBase, (int)DataSize));

        var entry = VitaSharp.Generated.HleLocalBindings.Modules().Single(e => e.Type == typeof(ProbeModule));
        var probe = (ProbeModule)entry.Create();
        var generated = entry.Bindings();
        var reference = ReflectionBindings(typeof(ProbeModule));
        Assert.Equal(reference.Select(b => b.Method + b.Layout), generated.Select(b => b.Method + b.Layout));
        Assert.True(generated.Length >= 12);

        var s = CpuState.Allocate();
        try
        {
            var ctx = new HleContext { Cpu = s };
            for (int i = 0; i < generated.Length; i++)
            {
                var gen = generated[i].Bind(probe);
                var refl = Bind(probe, reference[i].Info);
                for (int round = 0; round < 200; round++)
                {
                    int seed = rng.Next();
                    RandomState(s, new Random(seed));
                    probe.Seed = seed;
                    refl(ctx);
                    var expectedArgs = probe.Take();
                    Assert.True(expectedArgs.Length > 0 || reference[i].Info.GetParameters().Length == 0, "probe recorded nothing");
                    var expected = Result(s);
                    RandomState(s, new Random(seed));
                    probe.Seed = seed;
                    gen(ctx);
                    var actualArgs = probe.Take();
                    Assert.Equal(expectedArgs, actualArgs);
                    Assert.Equal(expected, Result(s));
                }
            }
        }
        finally { CpuState.Free(s); }
    }
}

public enum ProbeEnum { A = 1, B = -7 }
public enum ProbeByteEnum : byte { X = 3 }
public enum ProbeLongEnum : long { Y = 5 }
public struct ProbeStruct { public uint A; }

/// <summary>Records the arguments it receives and returns values derived from <see cref="Seed"/>.</summary>
public sealed class ProbeModule : HleModule
{
    public int Seed;
    readonly List<string> _log = new();
    public string Take() { var s = string.Join("|", _log); _log.Clear(); return s; }
    void Log(params object?[] a)
    {
        foreach (var x in a)
            _log.Add(x switch
            {
                float f => BitConverter.SingleToUInt32Bits(f).ToString("X"),
                double d => BitConverter.DoubleToUInt64Bits(d).ToString("X"),
                Ptr<ProbeStruct> p => $"ptr {p.Address:X}",
                VarArgs v => $"va {v.NextU32():X} {v.NextU64():X} {v.NextU32():X}",
                HleContext => "ctx",
                null => "null",
                _ => $"{x.GetType().Name}:{x}",
            });
    }

    [Hle("probeInts", Nid = 0x1)] int Ints(int a, uint b, short c, ushort d, byte e, sbyte f, char g) { Log(a, b, c, d, e, f, g); return Seed; }
    [Hle("probeWide", Nid = 0x2)] long Wide(uint a, long b, ulong c, int d, long e) { Log(a, b, c, d, e); return (long)Seed * 31; }
    [Hle("probeWide2", Nid = 0x3)] ulong Wide2(ulong a, uint b, uint c, double d, uint e) { Log(a, b, c, d, e); return (ulong)Seed << 20; }
    [Hle("probeFloats", Nid = 0x4)] float Floats(float a, uint b, float c, long d, float e, double f) { Log(a, b, c, d, e, f); return Seed / 3f; }
    [Hle("probeDouble", Nid = 0x5)] double Double(double a, float b) { Log(a, b); return Seed / 7.0; }
    [Hle("probeMisc", Nid = 0x6)] bool Misc(bool a, string s, Ptr<ProbeStruct> p, ProbeEnum e, ProbeByteEnum be, ProbeLongEnum le) { Log(a, s, p, e, be, le); return (Seed & 1) != 0; }
    [Hle("probeVa", Nid = 0x7)] uint Va(uint a, string fmt, VarArgs va) { Log(a, fmt, va); return (uint)Seed; }
    [Hle("probeStackVa", Nid = 0x8)] int StackVa(uint a, uint b, uint c, uint d, uint e, VarArgs va) { Log(a, b, c, d, e, va); return -Seed; }
    [Hle("probeCtx", Nid = 0x9)] void Ctx(HleContext ctx, uint a, long b) { Log(ctx, a, b); }
    [Hle("probeRetEnum", Nid = 0xA)] ProbeEnum RetEnum(uint a) { Log(a); return (ProbeEnum)Seed; }
    [Hle("probeRetShort", Nid = 0xB)] short RetShort(uint a) { Log(a); return (short)Seed; }
    [Hle("probeRetByte", Nid = 0xC)] byte RetByte(uint a) { Log(a); return (byte)Seed; }
    [Hle("probeRetPtr", Nid = 0xD)] Ptr<ProbeStruct> RetPtr(Ptr<ProbeStruct> p) { Log(p); return new Ptr<ProbeStruct>((uint)Seed); }
    [Hle("probeRetByteEnum", Nid = 0xE)] ProbeByteEnum RetByteEnum() { Log(); return (ProbeByteEnum)Seed; }
    [Hle("probeManyStack", Nid = 0xF)] uint ManyStack(uint a, uint b, uint c, long d, uint e, long f, ushort g, string h) { Log(a, b, c, d, e, f, g, h); return 0; }
    [Hle("probeA", Nid = 0x10), Hle("probeB", Nid = 0x11, Library = "SceProbe")] public uint TwoAttrs(uint a) { Log(a); return a + 1; }
}
