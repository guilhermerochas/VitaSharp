namespace VitaSharp.Core.Hle;

public sealed partial class HleRegistry
{
    /// <summary>The C# version of NID <paramref name="nid"/> (null when the HLE has none): for libraries whose exports
    /// only pass their arguments on to another library's function (a wrapper picking one of several tables), so the
    /// call lands in that function with the caller's registers and stack untouched.</summary>
    public Action<HleContext>? ImplOf(uint nid) => _impls.GetValueOrDefault(nid);
}

/// <summary>Calls into a private instance of an HLE module (not registered): a firmware library that is another
/// copy of a library the HLE already has (libmtphttp is libhttp built over the MTP host link) gets its own state by
/// running that module's C# functions on a second instance.</summary>
public sealed class HleInstance<T> where T : HleModule, new()
{
    readonly Dictionary<uint, Action<HleContext>> _byNid = new();
    public T Module { get; }

    public HleInstance(Emulator emu, Action<T>? configure = null)
    {
        Module = new T { Emu = emu };
        configure?.Invoke(Module);
        foreach (var b in HleBindings.For(typeof(T)) ?? [])
        {
            var invoker = b.Bind(Module);
            if (b.Nid != 0) _byNid[b.Nid] = invoker;
            else
                foreach (var n in NidDatabase.LookupName(b.Name ?? b.Method))
                    if (b.Library == null || n.Library == b.Library) _byNid[n.Nid] = invoker;
        }
        Module.Initialize();
    }

    /// <summary>Runs the instance's version of NID <paramref name="nid"/> on the caller's arguments (its return value
    /// lands in r0 as usual); <paramref name="missing"/> is returned when the module has no such function.</summary>
    public void Call(HleContext ctx, uint nid, int missing)
    {
        if (_byNid.TryGetValue(nid, out var f)) f(ctx);
        else ctx.Return(missing);
    }
}
