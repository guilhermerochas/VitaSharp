using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using Mutex = VitaSharp.Core.Kernel.Mutex;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Core.SaveState;

/// <summary>
/// Save states: the kernel object table. Each object is a record {kind, UID, name, owner, open count, body}. The
/// process, application and module records of the running session are kept (the loaded modules must be the saved
/// ones, at the same addresses); every other object is recreated from the state.
/// </summary>
internal static class KernelSnapshot
{
    internal const string Thread = "thread", Sema = "sema", Evf = "evf", Mtx = "mutex", Cond = "cond", Pipe = "msgpipe",
        Rw = "rwlock", Cb = "callback", Timer = "timer", SEvent = "simpleevent", ThEvent = "threadevent",
        Block = "memblock", Async = "ioasync", Process = "process", App = "app", Module = "module", Data = "kerneldata",
        PluginHook = "pluginhook", PluginInjection = "plugininject";

    public static SnapshotWriter Save(Emulator emu, Dictionary<GuestThread, GuestThread.SafeKind> kinds)
    {
        ObjectTable table = emu.Kernel.Objects;
        var w = new SnapshotWriter();
        w.Write(table.NextUid);
        List<KernelObject> all = table.Snapshot();
        w.Write(all.Count);
        foreach (KernelObject kernelObject in all)
        {
            string kind = kernelObject switch
            {
                GuestThread => Thread,
                Semaphore => Sema,
                EventFlag => Evf,
                Mutex => Mtx,
                CondVar => Cond,
                MsgPipe => Pipe,
                RwLock => Rw,
                Callback => Cb,
                TimerObject => Timer,
                SimpleEvent => SEvent,
                ThreadEventHandler => ThEvent,
                MemBlock => Block,
                IoAsyncOp => Async,
                ProcessObject => Process,
                AppObject => App,
                ModuleObject => Module,
                KernelData => Data,
                PluginFramework.HookPatch => PluginHook,
                PluginFramework.Injection => PluginInjection,
                _ => throw new SnapshotException($"kernel object {kernelObject.Name} ({kernelObject.GetType().Name}) is not supported by save states yet"),
            };
            w.Write(kind);
            w.Write(kernelObject.Uid);
            w.Write(kernelObject.Name);
            w.Write(kernelObject.OwnerPid);
            w.Write(kernelObject.OpenCount);
            int at = w.BeginBlock();
            switch (kernelObject)
            {
                case GuestThread t:
                    // every process's threads are part of the state (the shell's and its applications' under the Vita
                    // shell, the kernel process's with the kernel's pid as their record owner)
                    if (!ReferenceEquals(t.Process.Vita, emu.Vita))
                    {
                        throw new SnapshotException($"thread {t.Name} belongs to another system");
                    }

                    t.WriteSnapshot(w, kinds.TryGetValue(t, out GuestThread.SafeKind k) ? k : GuestThread.SafeKind.Dormant);
                    break;
                case Semaphore s:
                    s.Save(w);
                    break;
                case EventFlag e:
                    e.Save(w);
                    break;
                case Mutex m:
                    m.Save(w);
                    break;
                case CondVar c:
                    c.Save(w);
                    break;
                case MsgPipe p:
                    p.Save(w);
                    break;
                case RwLock l:
                    l.Save(w);
                    break;
                case Callback c:
                    c.Save(w);
                    break;
                case TimerObject t:
                    t.Save(w);
                    break;
                case SimpleEvent e:
                    e.Save(w);
                    break;
                case ThreadEventHandler h:
                    h.Save(w);
                    break;
                case MemBlock b:
                    w.Write(b.Address);
                    w.Write(b.Size);
                    w.Write(b.Type);
                    w.Write(b.IsVm);
                    break;
                case KernelData d:
                    w.Write(d.Kind);
                    w.Write(d.Address);
                    w.Write(d.Size);
                    w.Write(d.Aux);
                    w.Write(d.References);
                    break;
                case IoAsyncOp a:
                    if (!a.Done)
                    {
                        throw new SnapshotException("an asynchronous I/O request is in flight");
                    }

                    w.Write(a.Fd);
                    w.Write(a.Result);
                    w.Write(a.Cancelled);
                    w.Write(a.Pattern);
                    w.Write(a.UserData);
                    break;
                case PluginFramework.HookPatch hook:
                    hook.Save(w);
                    break;
                case PluginFramework.Injection injection:
                    injection.Save(w);
                    break;
                case ModuleObject mo:
                    w.Write(mo.Module.Path);
                    w.Write(mo.Module.Segments.TryGetValue(0, out SegmentInfo seg) ? seg.Address : 0);
                    break;
            }
            w.EndBlock(at);
        }
        return w;
    }

    /// <summary>The process a saved object of <paramref name="owner"/> goes back to: the kernel process for the kernel's
    /// pid, the running process with that pid (the shell or one of its applications), else the first process (states
    /// of builds before the kernel process name it for everything).</summary>
    private static Emulator OwnerProcess(Emulator emu, int owner)
    {
        if (owner == KernelSpace.KernelPid && emu.Vita.KernelProcess is { } kernelOwner)
        {
            return kernelOwner;
        }

        foreach (Emulator process in emu.Vita.Processes)
        {
            if (process.Pid == owner)
            {
                return process;
            }
        }

        return emu;
    }

    /// <summary>Replaces the object table; returns the restored live threads, to start once everything else is loaded.</summary>
    public static List<GuestThread> Load(Emulator emu, SnapshotReader r)
    {
        ObjectTable table = emu.Kernel.Objects;
        List<KernelObject> current = table.Snapshot();
        // which process's list each loaded module is in (the lists are rebuilt from the saved UIDs below)
        var moduleProcess = new Dictionary<LoadedModule, Emulator>();
        foreach (Emulator process in emu.Vita.ProcessesAndKernel.Prepend(emu))
        {
            foreach (LoadedModule loaded in process.Kernel.Modules.Values)
            {
                moduleProcess.TryAdd(loaded, process);
            }
        }
        int next = r.ReadInt32();
        int n = r.ReadInt32();
        var objects = new List<KernelObject>(n);
        var live = new List<GuestThread>();
        var condGiveBack = new List<(GuestThread T, int Mutex, int Count)>();
        var modulesSeen = new HashSet<ModuleObject>();
        var hostKept = new HashSet<GuestThread>();
        for (int i = 0; i < n; i++)
        {
            string kind = r.ReadStringNotNull();
            int uid = r.ReadInt32();
            string name = r.ReadStringNotNull();
            int owner = r.ReadInt32(), open = r.ReadInt32();
            SnapshotReader b = r.ReadBlock();
            KernelObject o;
            switch (kind)
            {
                case Thread:
                {
                    // a thread goes back to the process that owned it (the kernel process's: states of builds before
                    // it have none)
                    Emulator threadProcess = OwnerProcess(emu, owner);
                    var t = GuestThread.ReadSnapshot(threadProcess, b, out GuestThread.SafeKind k, out int cm, out int cc);
                    if (k == GuestThread.SafeKind.HostBody)
                    {
                        // a module's service thread: the running session's own one carries on under the saved UID
                        GuestThread host = current.OfType<GuestThread>().FirstOrDefault(x => x.HostBody != null && x.Name == t.Name && !hostKept.Contains(x))
                            ?? throw new SnapshotException($"the service thread '{t.Name}' does not exist in this session yet; let the game start further, then load the state");
                        t.Dispose();
                        hostKept.Add(host);
                        host.Rekey(uid);
                        o = host;
                        break;
                    }
                    if (k is GuestThread.SafeKind.Parked or GuestThread.SafeKind.Reissue)
                    {
                        live.Add(t);
                    }

                    if (cm != 0)
                    {
                        condGiveBack.Add((t, cm, cc));
                    }

                    o = t;
                    break;
                }
                case Sema:
                {
                    var s = new Semaphore { Name = name };
                    s.Load(b);
                    o = s;
                    break;
                }
                case Evf:
                {
                    var e = new EventFlag { Name = name };
                    e.Load(b);
                    o = e;
                    break;
                }
                case Mtx:
                {
                    var m = new Mutex { Name = name };
                    m.Load(b);
                    o = m;
                    break;
                }
                case Cond:
                {
                    var c = new CondVar { Name = name };
                    c.Load(b);
                    o = c;
                    break;
                }
                case Pipe:
                {
                    var p = new MsgPipe { Name = name };
                    p.Load(b);
                    o = p;
                    break;
                }
                case Rw:
                {
                    var l = new RwLock { Name = name };
                    l.Load(b);
                    o = l;
                    break;
                }
                case Cb:
                {
                    var c = new Callback { Name = name };
                    c.Load(b);
                    o = c;
                    break;
                }
                case Timer:
                {
                    Emulator timerProcess = OwnerProcess(emu, owner);
                    var t = new TimerObject { Name = name, Clock = () => timerProcess.Kernel.ProcessTimeUs };
                    t.Load(b);
                    o = t;
                    break;
                }
                case SEvent:
                {
                    var e = new SimpleEvent { Name = name };
                    e.Load(b);
                    o = e;
                    break;
                }
                case ThEvent:
                {
                    var h = new ThreadEventHandler { Name = name };
                    h.Load(b);
                    o = h;
                    break;
                }
                case Block:
                    o = new MemBlock { Name = name, Address = b.ReadUInt32(), Size = b.ReadUInt32(), Type = b.ReadUInt32(), IsVm = b.ReadBool() };
                    break;
                case Data:
                    o = new KernelData { Name = name, Kind = b.ReadStringNotNull(), Address = b.ReadUInt32(), Size = b.ReadUInt32(), Aux = b.ReadUInt32(), References = b.ReadInt32() };
                    break;
                case Async:
                    o = new IoAsyncOp
                    { Name = name, Fd = b.ReadInt32(), Result = b.ReadInt64(), Done = true, Cancelled = b.ReadBool(), Pattern = b.ReadUInt32(), UserData = b.ReadUInt64() };
                    break;
                case PluginHook:
                    o = PluginFramework.HookPatch.Load(b, name);
                    break;
                case PluginInjection:
                    o = PluginFramework.Injection.Load(b, name);
                    break;
                case Process or App:
                    o = current.FirstOrDefault(x => x.Uid == uid && (kind == Process ? x is ProcessObject : x is AppObject))
                        ?? throw new SnapshotException($"the {kind} record 0x{uid:X} differs from this session's");
                    break;
                case Module:
                {
                    string path = b.ReadStringNotNull();
                    uint seg0 = b.ReadUInt32();
                    // found by file and address, not by UID: a session that booted the program directly (or reached
                    // it through a LoadExec at another moment) numbered its objects differently; the module takes
                    // the saved UID, which the game's memory and the HLE state hold
                    ModuleObject mo = current.OfType<ModuleObject>().FirstOrDefault(x => !modulesSeen.Contains(x) && x.Module.Path == path
                        && (x.Module.Segments.TryGetValue(0, out SegmentInfo segmentInfo) ? segmentInfo.Address : 0) == seg0)
                        ?? throw new SnapshotException($"module {name} ({path}) is not loaded the same way in this session; restart the game and load the state again");
                    modulesSeen.Add(mo);
                    o = mo;
                    break;
                }
                default:
                    throw new SnapshotException($"unknown kernel object kind {kind}");
            }
            o.Uid = uid;
            o.OwnerPid = owner;
            o.OpenCount = open;
            objects.Add(o);
        }
        foreach (ModuleObject mo in current.OfType<ModuleObject>())
        {
            if (!modulesSeen.Contains(mo))
            {
                throw new SnapshotException($"module {mo.Module.Name} was loaded in this session after the state was saved; restart the game and load the state again");
            }
        }

        // the old threads ended; their records and every other old object go (a module may still hold an old thread
        // object, so their CPU state is not freed)
        table.Clear();
        foreach (KernelObject o in objects)
        {
            table.Put(o);
        }

        // the kernel process's record stays (a state saved before the kernel was one process for the whole system has
        // none: its kernel modules were the process's)
        if (emu.Vita.KernelProcess is { } kernelProcess && table.Get<ProcessObject>(KernelSpace.KernelPid) == null)
        {
            emu.Vita.RegisterKernelProcess(kernelProcess);
        }

        table.NextUid = next;
        // the module lists follow the UIDs the modules took: the kernel modules are the kernel process's
        Emulator kernelModules = emu.Vita.KernelProcess ?? emu;
        foreach (Emulator process in moduleProcess.Values.Distinct())
        {
            process.Kernel.Modules.Clear();
        }

        kernelModules.Kernel.Modules.Clear();
        foreach (ModuleObject mo in modulesSeen)
        {
            mo.Module.Uid = mo.Uid;
            Emulator holder = mo.Module.IsKernel ? kernelModules : moduleProcess.GetValueOrDefault(mo.Module) ?? emu;
            holder.Kernel.Modules[mo.Uid] = mo.Module;
        }
        foreach (KernelObject o in objects)
        {
            switch (o)
            {
                case Mutex m:
                    m.Resolve(table);
                    break;
                case CondVar c:
                    c.Resolve(table);
                    break;
                case RwLock l:
                    l.Resolve(table);
                    break;
                case Callback c:
                    c.Resolve(table);
                    break;
                case EventObject e:
                    e.ResolveRegistrations(table);
                    break;
            }
        }

        foreach ((GuestThread? t, int _, int c) in condGiveBack)
        {
            t._condReissueCount = c;
        }

        return live;
    }
}
