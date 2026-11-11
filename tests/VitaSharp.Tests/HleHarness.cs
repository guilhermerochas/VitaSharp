using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// A full interpreter-backed <see cref="Emulator"/> (no game loaded) for HLE module tests, with helpers to call module
/// functions by name (they are mostly private: the binder reaches them through [Hle]) and to run code on guest threads,
/// which the kernel calls need (the calling thread, blocking waits, the emulated cores).
/// </summary>
public sealed class HleHarness : IDisposable
{
    readonly GuestMemoryFixture _fx;
    readonly string _tag;
    readonly List<GuestThread> _threads = [];
    public string Root { get; }
    public Emulator Emu { get; }

    public HleHarness(GuestMemoryFixture fx, string tag)
    {
        _fx = fx;
        _tag = tag;
        Root = Path.Combine(Path.GetTempPath(), $"vitasharp-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Emu = new Emulator(new EmulatorOptions { FsRoot = Root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    public void Dispose()
    {
        foreach (var t in _threads)
        {
            t.Core.StopRequested = true;
            t.WaitForExit(5000);
        }
        Emu.Dispose();
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public T Module<T>() => Emu.Hle.Modules.OfType<T>().Single();

    public static object? Invoke(object module, string name, params object?[] args)
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        // the C# method, or the method exporting the name as an alias ([Hle("sceKernelWaitSemaCB")] on sceKernelWaitSema)
        var m = module.GetType().GetMethod(name, All)
            ?? module.GetType().GetMethods(All).SingleOrDefault(x => x.GetCustomAttributes<VitaSharp.Core.Hle.HleAttribute>().Any(a => a.Name == name))
            ?? throw new MissingMethodException(module.GetType().Name, name);
        try
        {
            return m.Invoke(module, args);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    public static int Call(object module, string name, params object?[] args) => Convert.ToInt32(Invoke(module, name, args));

    public uint Alloc(uint size)
    {
        uint p = _fx.Memory.Alloc(size, _tag);
        GuestMemory.Fill(p, 0, size);
        return p;
    }

    public uint Str(string s)
    {
        uint p = Alloc((uint)System.Text.Encoding.UTF8.GetByteCount(s) + 1);
        GuestMemory.WriteCString(p, s);
        return p;
    }

    /// <summary>A guest word holding <paramref name="v"/> (timeouts, in/out counts).</summary>
    public uint Word(uint v)
    {
        uint p = Alloc(8);
        GuestMemory.W32(p, v);
        return p;
    }

    /// <summary>Starts <paramref name="body"/> on a new guest thread; <see cref="Join"/> waits for it and rethrows.</summary>
    public GuestRun Start(Action<GuestThread> body, string name = "hle-test", int priority = 0x10000100)
    {
        var run = new GuestRun();
        var t = Emu.Kernel.CreateThread(name, 0, priority, 0x10000, 0, 0x70000);
        run.Thread = t;
        t.HostBody = th =>
        {
            try
            {
                body(th);
            }
            catch (GuestThreadExitException)
            {
                throw;
            }
            catch (Exception e)
            {
                run.Failure = e;
            }
        };
        _threads.Add(t);
        Assert.Equal(0, t.Start(0, 0));
        return run;
    }

    public void Join(GuestRun run, int timeoutMs = 30_000)
    {
        Assert.True(run.Thread.WaitForExit(timeoutMs), $"guest thread '{run.Thread.Name}' did not finish");
        if (run.Failure != null)
        {
            throw new Xunit.Sdk.XunitException(run.Failure.ToString());
        }
    }

    /// <summary>Runs <paramref name="body"/> on a guest thread and waits for it.</summary>
    public void OnGuest(Action<GuestThread> body, string name = "hle-test")
    {
        var r = Start(body, name);
        Join(r);
    }

    /// <summary>Polls until <paramref name="cond"/> holds (another guest thread reaching a wait); fails after 10 s.</summary>
    public static void Until(Func<bool> cond, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > 10_000)
            {
                throw new Xunit.Sdk.XunitException($"timed out waiting for {what}");
            }

            Thread.Sleep(1);
        }
    }

    public sealed class GuestRun
    {
        public GuestThread Thread = null!;
        public Exception? Failure;
        public int Result;
    }
}
