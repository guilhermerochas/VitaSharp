using VitaSharp.Core.Util;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Process-wide runtime of the native backend: the code arena with its entry trampoline, the callout bridge into the
/// interpreter handlers, and block linking.
/// <para>
/// Linking: every static exit of a block owns an 8-byte slot in plain data memory. The exit jumps through the slot when
/// it is non-zero (and the pacing window and interrupt flag allow it), otherwise it returns to the dispatcher, which
/// fills the slot once the target block has native code. Slots are data, so linking and unlinking (invalidation) never
/// write to code pages: no W^X flips and no races with threads executing the code.
/// </para>
/// </summary>
public sealed unsafe partial class NativeRuntime
{
    static NativeRuntime? _instance;
    static readonly object InitLock = new();

    public static NativeRuntime Instance
    {
        get
        {
            if (_instance != null) return _instance;
            lock (InitLock) return _instance ??= new NativeRuntime();
        }
    }

    public readonly ExecutableMemory Code = new();
    readonly delegate* unmanaged<CpuState*, nint, void> _enter;
    /// <summary>Runtime entry points for x64 code (x64 hosts only).</summary>
    public readonly X64Emitter.Runtime Stubs;
    /// <summary>Runtime entry points for ARM64 code (ARM64 hosts only).</summary>
    public readonly Arm64Emitter.Runtime Arm64Stubs;

    NativeRuntime()
    {
        if (GuestMemory.Base == null) throw new InvalidOperationException("native backend needs the guest address space");
        // generated code addresses CpuState fields by their marshalled offsets: they must be the managed layout
        var probe = CpuState.Allocate();
        bool layoutOk = StateLayout.Of(nameof(CpuState.N)) == (int)((byte*)&probe->N - (byte*)probe)
                        && StateLayout.Of(nameof(CpuState.CycleAcc)) == (int)((byte*)&probe->CycleAcc - (byte*)probe)
                        && StateLayout.Of(nameof(CpuState.LinkBreak)) == (int)((byte*)&probe->LinkBreak - (byte*)probe);
        CpuState.Free(probe);
        if (!layoutOk) throw new InvalidOperationException("CpuState layout mismatch");
        InitDispatch();
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            int epi = 0;
            bool pinned = (Arm64Emitter.Opts & Arm64Emitter.A64Opt.PinnedCounters) != 0;
            int trampSize = 0;
            byte* entry64 = Code.Place(_ => { var t = Arm64Emitter.EmitTrampoline(out epi, pinned); trampSize = t.Length; return t; });
            if (NativeCodeMap.Enabled) NativeCodeMap.Add((nint)entry64, trampSize, 0, false, "trampoline");
            _enter = (delegate* unmanaged<CpuState*, nint, void>)entry64;
            ulong epilogue = (ulong)(entry64 + epi);
            if (!NoLink) ResumeEntry = (nint)Code.Place(_ => Arm64Emitter.EmitDispatchEntry(epilogue));
            if (SkipGcTransition)
            {
                _enterFast = (delegate* unmanaged[SuppressGCTransition]<CpuState*, nint, void>)entry64;
                Util.Log.Info("Native backend: entered without the GC transition (Mono)");
            }
            Arm64Stubs = new Arm64Emitter.Runtime
            {
                Epilogue = (ulong)(entry64 + epi),
                Callout = (ulong)(delegate* unmanaged<CpuState*, int, uint, uint, int>)&Callout,
                DispatchTable = (ulong)Dispatch,
                PinnedCounters = pinned,
                PatchLinks = pinned && (Arm64Emitter.Opts & Arm64Emitter.A64Opt.PatchLinks) != 0,
            };
            PatchLinks = Arm64Stubs.PatchLinks;
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            // native pre-filter: only faults with RIP inside the arena reach the managed reporter (managed code faulting
            // on lazily committed guest pages runs in cooperative mode and must never re-enter the runtime here)
            byte* filter = Code.Place(rip => X64Emitter.EmitFaultFilter(rip, (ulong)Code.ExecBase, (ulong)Code.ExecBase + ExecutableMemory.ReserveSize, (ulong)(delegate* unmanaged<void*, int>)&OnFault));
            AddVectoredExceptionHandler(1, filter);
        }
        int epilogueOffset = 0;
        byte* tramp = Code.Place(rip => X64Emitter.EmitTrampoline(rip, out epilogueOffset));
        _enter = (delegate* unmanaged<CpuState*, nint, void>)tramp;
        Stubs = new X64Emitter.Runtime
        {
            Epilogue = (ulong)(tramp + epilogueOffset),
            Callout = (ulong)(delegate* unmanaged<CpuState*, int, uint, uint, int>)&Callout,
            DispatchTable = (ulong)Dispatch,
        };
    }

    /// <summary>Runs native code from <paramref name="entry"/> until a block returns to the dispatcher. The code addresses
    /// guest memory and looks indirect exits up through the state (<see cref="CpuState.MemBase"/>,
    /// <see cref="CpuState.DispatchTable"/>: its process's); a state bound to neither uses the calling thread's address
    /// space and the runtime's own table (tests).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Run(CpuState* s, nint entry)
    {
        if (s->DispatchTable == 0) s->DispatchTable = (nint)Dispatch;
        if (s->MemBase == null) s->MemBase = GuestMemory.Base;
        if (_enterFast != null) _enterFast(s, entry);
        else _enter(s, entry);
    }

    /// <summary>
    /// Mono (the Android app): enter native code without the GC transition. Every HLE call leaves native code and
    /// enters it again, and Mono's transition (thread-state CAS, TLS lookups, LMF) was several percent of a phone core
    /// in HLE-heavy games. Mono's hybrid suspend stops a thread running in managed mode with a signal, so the GC does not
    /// wait for guest code, and an [UnmanagedCallersOnly] callout from that mode is a no-op transition there. CoreCLR
    /// (the desktop) keeps the transition: its reverse P/Invoke from cooperative mode is a fatal error.
    /// <see cref="EnvVars.NativeGcTransition"/>=on keeps it on Mono too.
    /// </summary>
    /// <summary>ARM64: linked static exits are patched into direct branches (<see cref="Arm64Emitter.Runtime.PatchLinks"/>).</summary>
    public readonly bool PatchLinks;

    /// <summary>
    /// Rewrites the patch site of a two-word link slot (target, site): a direct B to <paramref name="target"/>, or the
    /// unlinked B to the slot path when it is 0. A B replaced by another B is one of the instructions the architecture
    /// allows to be modified while other cores execute it; a target out of B range keeps the slot path.
    /// </summary>
    void PatchSite(nint slot, nint target)
    {
        nint site = ((nint*)slot)[1];
        if (site == 0) return;
        uint word = target == 0 ? Arm64Emitter.UnlinkedSiteWord : Arm64Emitter.BranchWord(site, target);
        if (word != 0) Code.PatchWord((byte*)site, word);
    }

    /// <summary>ARM64: native code that continues at the state's pc through the dispatch table
    /// (<see cref="Arm64Emitter.EmitDispatchEntry"/>); 0 elsewhere.</summary>
    public readonly nint ResumeEntry;

    static readonly bool SkipGcTransition = Type.GetType("Mono.RuntimeStructs") != null
        && Environment.GetEnvironmentVariable(EnvVars.NativeGcTransition) is not ("on" or "1");
    readonly delegate* unmanaged[SuppressGCTransition]<CpuState*, nint, void> _enterFast;

    // ------------------------------------------------------------------ callouts

    /// <summary>Address of the callout bridge (tests that run generated code under an emulator call it themselves).</summary>
    public static nint CalloutAddress => (nint)(delegate* unmanaged<CpuState*, int, uint, uint, int>)&Callout;

    [ThreadStatic] static ExceptionDispatchInfo? _fault;

    /// <summary>Executed callouts per guest op (diagnostics: what to translate inline next).</summary>
    public static readonly long[] CalloutHits = new long[(int)Op.Count];

    /// <summary>
    /// Runs one interpreter handler for generated code. Returns nonzero when the block must stop because the handler
    /// threw (rethrown on the managed side by <see cref="ThrowPendingFault"/>). An exit the handler raises (SVC, UDF,
    /// yield) does not stop the block: like the interpreter, the rest of the block runs, and the block's exits return
    /// to the dispatcher instead of chaining while Exit is set.
    /// </summary>
    [UnmanagedCallersOnly]
    static int Callout(CpuState* s, int op, uint ins, uint pc)
    {
        try
        {
            if (op == (int)ArmFrontend.HleCalloutOp) return HleCall(s);
            CalloutHits[op]++; // racy on purpose: statistics only
            CalloutClasses.Hits[op * CalloutClasses.Sub + CalloutClasses.Of(op, ins)]++;
            HandlerTable.Pointers[op](s, ins, pc);
            return 0; // an exit the handler raised (UDF, SVC...) is seen by the block exits, exactly like the interpreter
        }
        catch (Exception e)
        {
            _fault = ExceptionDispatchInfo.Capture(e);
            s->CalloutFault = 1;
            return 1;
        }
    }

    /// <summary>Executed HLE callouts (statistics).</summary>
    public static long HleCallouts;

    /// <summary>
    /// An SVC from native code (<see cref="ArmFrontend.HleCallouts"/>): the thread's HLE dispatch runs here and the block goes
    /// on at the pc it set. What the dispatcher must handle (the return trap, callback waits, no handler) is left to it
    /// by raising the SVC exit, which the block's indirect exit sees. A throwing call is caught by the caller.
    /// </summary>
    static int HleCall(CpuState* s)
    {
        HleCallouts++;
        if (CpuCore.CurrentSvcHandler is not { } h || !h.TryHandleSvcNative(s)) s->Exit = ExitReason.Svc;
        return 0;
    }

    public static void ThrowPendingFault(CpuState* s)
    {
        s->CalloutFault = 0;
        var f = _fault;
        _fault = null;
        f?.Throw();
    }

    // ------------------------------------------------------------------ indirect-exit dispatch table

    /// <summary>
    /// Direct-mapped table of native entry points for indirect exits (BX LR, POP {PC}, computed jumps): entry
    /// (pc >> 1) &amp; mask holds {key = pc | thumb, code}. Generated code compares the key and jumps to the code, so
    /// returns stay in native code without a trip through the dispatcher. Publishing writes key-invalid, code, key; readers
    /// check key, read code, check key again (x64 keeps loads in order), so a slot being republished is never misread.
    /// </summary>
    public const int DispatchBits = 18;
    /// <summary>The runtime's own table: threads not bound to a process (tests). Every process has its own
    /// (<see cref="BlockCache.DispatchTable"/>): two processes can have different code at the same address.</summary>
    public readonly DispatchEntry* Dispatch = (DispatchEntry*)AllocateDispatchTable();
    public const uint NoKey = 0xFFFFFFFF; // never a valid key: block addresses are even

    public struct DispatchEntry { public uint Key, Pad; public nint Code; }

    /// <summary>A new, empty indirect-exit table.</summary>
    public static nint AllocateDispatchTable()
    {
        var t = (DispatchEntry*)NativeMemory.AlignedAlloc((nuint)(sizeof(DispatchEntry) << DispatchBits), 64);
        for (int i = 0; i < 1 << DispatchBits; i++) { t[i].Key = NoKey; t[i].Code = 0; }
        return (nint)t;
    }

    public static void FreeDispatchTable(nint table) => NativeMemory.AlignedFree((void*)table);

    void InitDispatch() { }

    static uint KeyOf(Block b) => b.Start | (b.Thumb ? 1u : 0u);
    DispatchEntry* EntryOf(Block b) =>
        (b.Owner != null ? (DispatchEntry*)b.Owner.DispatchTable : Dispatch) + ((b.Start >> 1) & ((1u << DispatchBits) - 1));

    /// <summary>Makes a native block reachable from indirect exits.</summary>
    public void Publish(Block b)
    {
        if (b.NativeEntry == 0 || b.Invalidated || NoLink) return;
        var e = EntryOf(b);
        lock (_dispatchLock)
        {
            Volatile.Write(ref e->Key, NoKey);
            Volatile.Write(ref e->Code, b.NativeEntry);
            Volatile.Write(ref e->Key, KeyOf(b));
        }
    }


    /// <summary>
    /// An indirect exit missed the table and the dispatcher found <paramref name="b"/>: if another block took its entry
    /// (the table is direct-mapped by address), it gets it back, so a target that lost a collision does not take the slow
    /// path on every jump to it from then on.
    /// </summary>
    public void Republish(Block b)
    {
        var e = EntryOf(b);
        if (Volatile.Read(ref e->Key) != KeyOf(b)) Publish(b);
    }

    readonly object _dispatchLock = new();

    // ------------------------------------------------------------------ link slots

    const int SlotChunk = 8192;
    nint* _slotChunk;
    int _slotUsed = SlotChunk;
    readonly object _slotLock = new();

    /// <summary>Zeroed link slots for the static exits of one block (never freed: slots are tiny).</summary>
    public nint* AllocateSlots(int count)
    {
        if (count == 0) return null;
        lock (_slotLock)
        {
            if (_slotUsed + count > SlotChunk)
            {
                _slotChunk = (nint*)NativeMemory.AllocZeroed((nuint)(SlotChunk * sizeof(nint)));
                _slotUsed = 0;
            }
            var p = _slotChunk + _slotUsed;
            _slotUsed += count;
            return p;
        }
    }

    public static long Links, Unlinks;

    /// <summary>Points an exit slot at the target block's code (called by the dispatcher after a static exit).</summary>
    /// <summary>Debug bisection: VITASHARP_NATIVE_NOLINK=1 disables exit linking and the indirect-exit table.</summary>
    public static readonly bool NoLink = Environment.GetEnvironmentVariable(EnvVars.NativeNolink) == "1";

    public static void Link(nint slot, Block target)
    {
        lock (target)
        {
            if (target.NativeEntry == 0 || target.Invalidated || NoLink) return;
            (target.LinkedFrom ??= new List<nint>()).Add(slot);
            Volatile.Write(ref *(nint*)slot, target.NativeEntry);
            if (_instance is { PatchLinks: true } rt) rt.PatchSite(slot, target.NativeEntry);
        }
        Interlocked.Increment(ref Links);
    }

    /// <summary>Clears every slot that jumps into the block (its code is stale: the guest rewrote it).</summary>
    public static void Unlink(Block b)
    {
        lock (b)
        {
            b.Invalidated = true;
            DetachNative(b, b.NativeEntry);
        }
    }

    /// <summary>
    /// Tier-up: another backend now runs the block. Its native code stays mapped (a thread may still be inside it)
    /// but nothing jumps into it any more and the dispatcher stops choosing it.
    /// </summary>
    public static void Retire(Block b)
    {
        lock (b)
        {
            nint entry = b.NativeEntry;
            if (entry == 0) return;
            b.NativeEntry = 0;
            DetachNative(b, entry);
        }
        Interlocked.Increment(ref TieredUp);
    }

    public static long TieredUp;

    static void DetachNative(Block b, nint entry)
    {
        if (_instance != null)
        {
            var e = _instance.EntryOf(b);
            lock (_instance._dispatchLock)
                if (e->Key == KeyOf(b) && e->Code == entry) Volatile.Write(ref e->Key, NoKey);
        }
        if (b.LinkedFrom == null) return;
        foreach (var slot in b.LinkedFrom)
            if (*(nint*)slot == entry)
            {
                Volatile.Write(ref *(nint*)slot, 0);
                if (_instance is { PatchLinks: true } rt) rt.PatchSite(slot, 0);
            }
        Interlocked.Add(ref Unlinks, b.LinkedFrom.Count);
        b.LinkedFrom = null;
    }

    // ------------------------------------------------------------------ crash diagnostics

    readonly List<(nint Entry, int Size, Block Block)> _blocks = new();

    /// <summary>Remembers which guest block owns a code range (crash reports).</summary>
    public void Register(nint entry, int size, Block b) { lock (_blocks) _blocks.Add((entry, size, b)); }

    [LibraryImport("kernel32.dll")]
    private static partial nint AddVectoredExceptionHandler(uint first, void* handler);

    static int _reported;

    /// <summary>
    /// Access violations inside generated code that the lazy-commit handler will not fix (address outside guest
    /// memory): log the instruction, the registers and the guest block, then let the process fail as before.
    /// </summary>
    [UnmanagedCallersOnly]
    static int OnFault(void* pointers)
    {
        var rec = *(byte**)pointers;
        var ctx = ((byte**)pointers)[1];
        if (*(uint*)rec != 0xC0000005 || _instance == null) return 0;
        ulong rip = *(ulong*)(ctx + 0xF8);
        var code = _instance.Code;
        if (rip < (ulong)code.ExecBase || rip >= (ulong)code.ExecBase + ExecutableMemory.ReserveSize) return 0;
        ulong addr = *(ulong*)(rec + 0x28);
        if (GuestMemory.Instance?.SpaceOfHost(addr) != null) return 0; // guest page (any address space): the lazy-commit handler commits it
        ulong gb = (ulong)GuestMemory.Base;
        if (Interlocked.Exchange(ref _reported, 1) != 0) return 0;
        try
        {
            string[] names = ["rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"];
            var regs = string.Join(" ", names.Select((n, i) => $"{n}={*(ulong*)(ctx + 0x78 + 8 * i):X}"));
            string where = "?";
            lock (_instance._blocks)
                foreach (var (e, size, b) in _instance._blocks)
                    if (rip >= (ulong)e && rip < (ulong)e + (ulong)size)
                    {
                        var ir = new ArmFrontend().Translate(b);
                        IrPasses.DeadCode(ir);
                        where = $"{b} at +0x{rip - (ulong)e:X}\n  {string.Join(" ", b.Insts.Select(i => $"{i.Address:X8}:{i.Raw:X8}:{i.Op}"))}\nIR:\n{ir}\nx64:\n{X64Disassembler.Disassemble(e, size)}";
                        break;
                    }
            var msg = $"NATIVE FAULT: access violation at rip={rip:X} (arena +0x{rip - (ulong)code.ExecBase:X}) address={addr:X} (guest base {gb:X})\n  {regs}\n  {where}";
            Console.Error.WriteLine(msg);
            Console.Error.Flush();
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "vitasharp-native-fault.txt"), msg + "\n");
        }
        catch { }
        return 0;
    }
}
