using VitaSharp.Core.Cpu;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceFiber: user-mode cooperative fibers. A fiber is a saved register context that a thread switches into with
/// sceFiberRun and out of with sceFiberSwitch / sceFiberReturnToThread. The guest SceFiber object (128 bytes,
/// 8-aligned) only carries identity here; the contexts live host-side, keyed by the object's address.
/// </summary>
public sealed unsafe class Fiber : HleModule
{
    const int OK = 0;
    const int ERR_NULL = unchecked((int)0x80590001), ERR_ALIGNMENT = unchecked((int)0x80590002), ERR_RANGE = unchecked((int)0x80590003),
        ERR_INVALID = unchecked((int)0x80590004), ERR_PERMISSION = unchecked((int)0x80590005), ERR_STATE = unchecked((int)0x80590006);
    const uint ContextMinimumSize = 512;

    enum Status { Init, Suspended, Running }

    sealed class FiberObj
    {
        public uint Address, Entry, ArgOnInitialize, ContextAddr, ContextSize, ArgOnRunPtr;
        public string Name = "";
        public Status Status;
        public CpuState Context;
    }

    sealed class ThreadFibers
    {
        public FiberObj? Current;
        public CpuState ThreadContext;
        public uint ArgOnReturnPtr;
    }

    readonly Dictionary<uint, FiberObj> _fibers = new();
    /// <summary>Finalized fiber objects, reused by the next initialize (guarded by the _fibers lock).</summary>
    readonly Stack<FiberObj> _freeFibers = new();
    /// <summary>Fiber names already decoded (keyed by the UTF-8 bytes hash, verified on hit): no string per initialize.</summary>
    readonly Dictionary<ulong, string> _names = new();

    string FiberName(uint addr)
    {
        // same result as GuestMemory.ReadCString(addr, 31)
        int n = 0;
        while (n < 31 && GuestMemory.Read<byte>(addr + (uint)n) != 0) n++;
        var raw = GuestMemory.Span(addr, n);
        ulong h = System.IO.Hashing.XxHash3.HashToUInt64(raw);
        lock (_names)
        {
            if (_names.TryGetValue(h, out var s) && Matches(s, raw)) return s;
            s = GuestMemory.ReadCString(addr, 31);
            if (_names.Count < 4096) _names[h] = s;
            return s;
        }

        static bool Matches(string s, ReadOnlySpan<byte> raw)
        {
            Span<char> chars = stackalloc char[64];
            int c = System.Text.Encoding.UTF8.GetChars(raw, chars);
            return chars[..c].SequenceEqual(s);
        }
    }
    readonly Dictionary<GuestThread, ThreadFibers> _threads = new();

    ThreadFibers Self(GuestThread t)
    {
        if (!_threads.TryGetValue(t, out var tf)) _threads[t] = tf = new ThreadFibers();
        return tf;
    }

    /// <summary>Snapshot of the calling context that resumes after the HLE call with R0 = <paramref name="r0"/>.</summary>
    static CpuState ResumeContext(CpuState* s, uint r0)
    {
        CpuState c = *s;
        c.R[0] = r0;
        c.R[15] = s->R[14] & ~1u;
        c.Thumb = s->R[14] & 1;
        c.ITState = 0;
        c.Exit = ExitReason.None;
        return c;
    }

    /// <summary>Loads <paramref name="ctx"/> into the CPU; the HLE dispatcher then resumes there instead of returning to LR.</summary>
    static int Load(GuestThread t, CpuState* s, in CpuState ctx)
    {
        uint tls = s->TpidrUro, tlsRw = s->TpidrUrw;
        *s = ctx;
        s->TpidrUro = tls; s->TpidrUrw = tlsRw; // thread-local registers belong to the thread, not the fiber
        s->ExclusiveValid = 0;
        t.ContextReplaced = true;
        return (int)ctx.R[0];
    }

    /// <summary>
    /// Puts a fiber in its running context, starting it at its entry point if it never ran. A fiber without its own
    /// context area keeps no state between runs: every run or switch into it starts it again at its entry, on the
    /// stack of the thread that ran the fibers (<paramref name="threadSp"/>). libult's per-worker scheduler fiber
    /// relies on this (the ULT threads switch back to it to be rescheduled; resuming it asserts).
    /// </summary>
    void Prepare(FiberObj f, CpuState* s, uint argOnRunTo, uint threadSp)
    {
        if (f.ContextAddr == 0) f.Status = Status.Init;
        if (f.Status == Status.Init)
        {
            CpuState c = *s;
            c.R[0] = f.ArgOnInitialize;
            c.R[1] = argOnRunTo;
            c.R[13] = f.ContextAddr != 0 ? (f.ContextAddr + f.ContextSize) & ~7u : threadSp & ~7u;
            c.R[14] = Emu.Kernel.ThreadExitTrap; // fiber entry functions must never return
            c.R[15] = f.Entry & ~1u;
            c.Thumb = f.Entry & 1;
            c.ITState = 0;
            c.Exit = ExitReason.None;
            f.Context = c;
        }
        else
        {
            if (f.ArgOnRunPtr != 0) GuestMemory.W32(f.ArgOnRunPtr, argOnRunTo);
            f.Context.R[0] = OK;
        }
        f.Status = Status.Running;
    }

    int Initialize(uint fiber, uint name, uint entry, uint argOnInitialize, uint addrContext, uint sizeContext)
    {
        if (fiber == 0 || entry == 0 || name == 0) return ERR_NULL;
        if ((fiber & 7) != 0) return ERR_ALIGNMENT;
        if (sizeContext != 0 && sizeContext < ContextMinimumSize) return ERR_RANGE;
        if ((addrContext == 0) != (sizeContext == 0) || (sizeContext & 7) != 0) return ERR_INVALID;
        FiberObj f;
        lock (_fibers) f = _freeFibers.Count > 0 ? _freeFibers.Pop() : new FiberObj(); // job systems create fibers continuously
        f.Address = fiber; f.Entry = entry; f.ArgOnInitialize = argOnInitialize; f.ContextAddr = addrContext; f.ContextSize = sizeContext;
        f.ArgOnRunPtr = 0; f.Context = default;
        f.Name = FiberName(name);
        f.Status = Status.Init;
        GuestMemory.Fill(fiber, 0, 128);
        GuestMemory.W32(fiber, entry);
        GuestMemory.WriteCString(fiber + 16, f.Name, 32);
        lock (_fibers) _fibers[fiber] = f;
        return OK;
    }

    [Hle("_sceFiberInitializeImpl")]
    int _sceFiberInitializeImpl(uint fiber, uint name, uint entry, uint argOnInitialize, uint addrContext, uint sizeContext) =>
        Initialize(fiber, name, entry, argOnInitialize, addrContext, sizeContext);

    [Hle("_sceFiberInitializeWithInternalOptionImpl")]
    int _sceFiberInitializeWithInternalOptionImpl(uint fiber, uint name, uint entry, uint argOnInitialize, uint addrContext, uint sizeContext) =>
        Initialize(fiber, name, entry, argOnInitialize, addrContext, sizeContext);

    [Hle] int sceFiberOptParamInitialize(uint param) { if (param == 0) return ERR_NULL; GuestMemory.Fill(param, 0, 128); return OK; }

    [Hle]
    int sceFiberFinalize(uint fiber)
    {
        if (fiber == 0) return ERR_NULL;
        lock (_fibers)
        {
            if (!_fibers.TryGetValue(fiber, out var f)) return ERR_INVALID;
            if (f.Status == Status.Running) return ERR_STATE;
            _fibers.Remove(fiber);
            if (_freeFibers.Count < 256) _freeFibers.Push(f);
        }
        return OK;
    }

    [Hle]
    int sceFiberRun(uint fiber, uint argOnRunTo, uint argOnReturn)
    {
        var t = GuestThread.Current!;
        var s = t.Core.State;
        lock (_fibers)
        {
            if (fiber == 0) return ERR_NULL;
            if (!_fibers.TryGetValue(fiber, out var f)) return ERR_INVALID;
            if (f.Status == Status.Running) return ERR_STATE;
            var tf = Self(t);
            if (tf.Current != null) return ERR_PERMISSION;
            tf.ThreadContext = ResumeContext(s, OK);
            tf.ArgOnReturnPtr = argOnReturn;
            Prepare(f, s, argOnRunTo, s->R[13]);
            tf.Current = f;
            return Load(t, s, f.Context);
        }
    }

    [Hle]
    int sceFiberSwitch(uint fiber, uint argOnRunTo, uint argOnRun)
    {
        var t = GuestThread.Current!;
        var s = t.Core.State;
        lock (_fibers)
        {
            if (fiber == 0) return ERR_NULL;
            if (!_fibers.TryGetValue(fiber, out var f)) return ERR_INVALID;
            var tf = Self(t);
            var cur = tf.Current;
            if (cur == null) return ERR_PERMISSION;
            if (f.Status == Status.Running) return ERR_STATE;
            cur.Context = ResumeContext(s, OK);
            cur.ArgOnRunPtr = argOnRun;
            cur.Status = Status.Suspended;
            Prepare(f, s, argOnRunTo, tf.ThreadContext.R[13]);
            tf.Current = f;
            return Load(t, s, f.Context);
        }
    }

    [Hle]
    int sceFiberReturnToThread(uint argOnReturnTo, uint argOnRun)
    {
        var t = GuestThread.Current!;
        var s = t.Core.State;
        lock (_fibers)
        {
            var tf = Self(t);
            var cur = tf.Current;
            if (cur == null) return ERR_PERMISSION;
            cur.Context = ResumeContext(s, OK);
            cur.ArgOnRunPtr = argOnRun;
            cur.Status = Status.Suspended;
            tf.Current = null;
            if (tf.ArgOnReturnPtr != 0) GuestMemory.W32(tf.ArgOnReturnPtr, argOnReturnTo);
            return Load(t, s, tf.ThreadContext);
        }
    }

    [Hle]
    int sceFiberGetSelf(Ptr<uint> fiber)
    {
        if (fiber.Address == 0) return ERR_NULL;
        lock (_fibers)
        {
            var cur = Self(GuestThread.Current!).Current;
            fiber.Value = cur?.Address ?? 0;
            return cur != null ? OK : ERR_PERMISSION;
        }
    }

    [Hle]
    int sceFiberGetInfo(uint fiber, uint info)
    {
        if (fiber == 0 || info == 0) return ERR_NULL;
        lock (_fibers)
        {
            if (!_fibers.TryGetValue(fiber, out var f)) return ERR_INVALID;
            // entry, argOnInitialize, addrContext, sizeContext, name[32], sizeContextMargin
            GuestMemory.Fill(info, 0, 128);
            GuestMemory.W32(info, f.Entry);
            GuestMemory.W32(info + 4, f.ArgOnInitialize);
            GuestMemory.W32(info + 8, f.ContextAddr);
            GuestMemory.W32(info + 12, f.ContextSize);
            GuestMemory.WriteCString(info + 16, f.Name, 32);
            GuestMemory.W32(info + 48, f.ContextSize == 0 ? 0 : MeasureMargin(f));
        }
        return OK;
    }

    /// <summary>Bytes of the context area never touched (initialised to 0xCC by sceFiberStartContextSizeCheck semantics).</summary>
    static uint MeasureMargin(FiberObj f)
    {
        var span = GuestMemory.Span(f.ContextAddr, (int)f.ContextSize);
        uint n = 0;
        while (n < span.Length && span[(int)n] == 0xCC) n++;
        return n;
    }

    bool _sizeCheck;
    [Hle] int sceFiberStartContextSizeCheck(uint flags) { _sizeCheck = true; return OK; }
    [Hle] int sceFiberStopContextSizeCheck() { _sizeCheck = false; return OK; }

    [Hle]
    int sceFiberRenameSelf(uint name)
    {
        if (name == 0) return ERR_NULL;
        lock (_fibers)
        {
            var cur = Self(GuestThread.Current!).Current;
            if (cur == null) return ERR_PERMISSION;
            cur.Name = GuestMemory.ReadCString(name, 31);
            GuestMemory.WriteCString(cur.Address + 16, cur.Name, 32);
        }
        return OK;
    }

    [Hle] int sceFiberPushUserMarkerWithHud(uint color, uint name) => OK;
    [Hle] int sceFiberPopUserMarkerWithHud() => OK;

    int AttachContext(uint fiber, uint addrContext, uint sizeContext)
    {
        if (!_fibers.TryGetValue(fiber, out var f)) return ERR_INVALID;
        if (f.ContextAddr != 0 || f.Status != Status.Init) return ERR_STATE;
        if (addrContext == 0 || sizeContext < ContextMinimumSize || (sizeContext & 7) != 0) return ERR_RANGE;
        f.ContextAddr = addrContext;
        f.ContextSize = sizeContext;
        if (_sizeCheck) GuestMemory.Fill(addrContext, 0xCC, sizeContext);
        return OK;
    }

    [Hle("_sceFiberAttachContextAndRun")]
    int _sceFiberAttachContextAndRun(uint fiber, uint addrContext, uint sizeContext, uint argOnRunTo, uint argOnReturn)
    {
        lock (_fibers) { int r = AttachContext(fiber, addrContext, sizeContext); if (r != OK) return r; }
        return sceFiberRun(fiber, argOnRunTo, argOnReturn);
    }

    [Hle("_sceFiberAttachContextAndSwitch")]
    int _sceFiberAttachContextAndSwitch(uint fiber, uint addrContext, uint sizeContext, uint argOnRunTo, uint argOnRun)
    {
        lock (_fibers) { int r = AttachContext(fiber, addrContext, sizeContext); if (r != OK) return r; }
        return sceFiberSwitch(fiber, argOnRunTo, argOnRun);
    }
}
