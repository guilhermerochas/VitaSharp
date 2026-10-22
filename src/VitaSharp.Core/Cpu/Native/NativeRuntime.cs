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
    public readonly X64Emitter.Runtime Stubs;

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
        if (OperatingSystem.IsWindows())
        {
            // native pre-filter: only faults with RIP inside the arena reach the managed reporter (managed code faulting
            // on lazily committed guest pages runs in cooperative mode and must never re-enter the runtime here)
            byte* filter = Code.Place(rip => X64Emitter.EmitFaultFilter(rip, (ulong)Code.ExecBase, (ulong)Code.ExecBase + ExecutableMemory.ReserveSize, (ulong)(delegate* unmanaged<void*, int>)&OnFault));
            AddVectoredExceptionHandler(1, filter);
        }
        int epilogueOffset = 0;
        byte* tramp = Code.Place(rip => X64Emitter.EmitTrampoline(rip, (ulong)GuestMemory.Base, out epilogueOffset));
        _enter = (delegate* unmanaged<CpuState*, nint, void>)tramp;
        Stubs = new X64Emitter.Runtime
        {
            Epilogue = (ulong)(tramp + epilogueOffset),
            Callout = (ulong)(delegate* unmanaged<CpuState*, int, uint, uint, int>)&Callout,
            DispatchTable = (ulong)Dispatch,
        };
    }

    /// <summary>Runs native code from <paramref name="entry"/> until a block returns to the dispatcher.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Run(CpuState* s, nint entry) => _enter(s, entry);

    // ------------------------------------------------------------------ callouts

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
    public const int DispatchBits = 16;
    public readonly DispatchEntry* Dispatch = (DispatchEntry*)NativeMemory.AlignedAlloc((nuint)(sizeof(DispatchEntry) << DispatchBits), 64);
    public const uint NoKey = 0xFFFFFFFF; // never a valid key: block addresses are even

    public struct DispatchEntry { public uint Key, Pad; public nint Code; }

    void InitDispatch()
    {
        for (int i = 0; i < 1 << DispatchBits; i++) { Dispatch[i].Key = NoKey; Dispatch[i].Code = 0; }
    }

    static uint KeyOf(Block b) => b.Start | (b.Thumb ? 1u : 0u);
    DispatchEntry* EntryOf(Block b) => Dispatch + ((b.Start >> 1) & ((1u << DispatchBits) - 1));

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
    public static readonly bool NoLink = Environment.GetEnvironmentVariable("VITASHARP_NATIVE_NOLINK") == "1";

    public static void Link(nint slot, Block target)
    {
        lock (target)
        {
            if (target.NativeEntry == 0 || target.Invalidated || NoLink) return;
            (target.LinkedFrom ??= new List<nint>()).Add(slot);
            Volatile.Write(ref *(nint*)slot, target.NativeEntry);
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
            if (*(nint*)slot == entry) Volatile.Write(ref *(nint*)slot, 0);
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
        ulong gb = (ulong)GuestMemory.Base;
        if (addr >= gb && addr < gb + 0x1_0001_0000UL) return 0; // guest page: the lazy-commit handler commits it
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
