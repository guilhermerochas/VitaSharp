using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>The counting stubs of the hooks (<see cref="PluginHookStats"/>).</summary>
public sealed partial class PluginFramework
{
    // the stub: 36 words entering the hook, 26 continuing to the original, 8 words of data, then the return words of
    // the traced calls (one SVC each: the hook's returns, then the original's) and their saved lr
    internal const int StubOldWord = 36;
    internal const int StubDataWord = 62;
    internal const int StubReturnWord = 70;

    /// <summary>The words of the two SVCs (an HLE call returns to lr: each sets lr to the word after it).</summary>
    internal const int StubEntrySvcWord = 31, StubOldSvcWord = 57;

    internal const uint StubCalls = 0, StubToNext = 4, StubToOriginal = 8, StubHookFunction = 12, StubTrampoline = 16, StubPredecessor = 20, StubMagic = 24,
        StubTraceLimit = 28;

    /// <summary>Marks a counting stub's data.</summary>
    const uint StubMagicValue = 0x324B4F48; // "HOK2"

    /// <summary>The stubs of the first version (no tracing: data at word 54, entry SVC at 27, continuation SVC at 49),
    /// found in save states: replaced on load.</summary>
    const uint StubMagicV1 = 0x4B4F4F48; // "HOOK"
    const int StubDataWordV1 = 54;

    PluginHookStats Stats => PluginHookStats.Of(Emu);

    /// <summary>The counting stubs' host functions are declared in every process (a save state with hooks names them).</summary>
    public override void Initialize()
    {
        Stats.Declare(Emu);
    }

    /// <summary>The size of a stub tracing <paramref name="traced"/> calls.</summary>
    static uint StubSize(int traced)
    {
        return (uint)(StubReturnWord + 4 * traced) * 4;
    }

    /// <summary>The address of the saved lr of return word <paramref name="slot"/> of a stub tracing
    /// <paramref name="traced"/> calls (slots 0..traced-1: the hook's returns, traced..2*traced-1: the original's).</summary>
    internal static uint StubSavedLr(uint data, int traced, int slot)
    {
        uint stub = data - StubDataWord * 4;
        return stub + (uint)(StubReturnWord + 2 * traced + slot) * 4;
    }

    /// <summary>The address of return word <paramref name="slot"/> (see <see cref="StubSavedLr"/>).</summary>
    internal static uint StubReturnAddress(uint data, int slot)
    {
        return data - StubDataWord * 4 + (uint)(StubReturnWord + slot) * 4;
    }

    /// <summary>A conditional branch from word <paramref name="from"/> to word <paramref name="to"/>.</summary>
    static uint StubBranch(uint cond, int from, int to)
    {
        return (cond << 28) | 0x0A000000 | ((uint)(to - from - 2) & 0xFFFFFF);
    }

    /// <summary>
    /// The counting stub of one hook (ARM): its entry (the record's func) adds one to the hook's calls and, when the hook
    /// is not the first of its chain, to the previous hook's continuations to the next hook, then jumps to the hook
    /// function; its continuation (the record's old) adds one to the continuations to the original and jumps to the
    /// trampoline. Each counter goes up atomically (ldrex/strex: hooks run on every core). Only a call that takes a
    /// counter from 0 to 1, or one of the first traced calls (the counter at most the data's trace limit), enters C#:
    /// an SVC with the data in r0 and the events and the counter in r1, r0-r3, r12 and lr saved on the stack (the HLE
    /// call returns to lr, so the stub points lr at the word after the SVC). The caller's registers and stack are
    /// untouched (r12 and the flags are scratch at a call), so the hook sees the call as it was.
    /// </summary>
    static uint[] CountingStubCode(uint svc)
    {
        uint svcWord = 0xEF000000 | (svc & 0xFFFFFF);
        const uint EQ = 0x0, NE = 0x1, LS = 0x9;
        return
        [
            // entry
            0xE92D000F,                                  // 0  push {r0-r3}
            0xE28FC000 | (StubDataWord * 4 - 12),        // 1  add r12, pc, #data
            0xE3A03000,                                  // 2  mov r3, #0          events
            0xE19C0F9F,                                  // 3  ldrex r0, [r12]     calls
            0xE2800001,                                  // 4  add r0, r0, #1
            0xE18C1F90,                                  // 5  strex r1, r0, [r12]
            0xE3510000,                                  // 6  cmp r1, #0
            StubBranch(NE, 7, 3),                        // 7  bne 3
            0xE3500001,                                  // 8  cmp r0, #1
            0x03833001,                                  // 9  orreq r3, r3, #1    first call
            0xE59C2000 | StubTraceLimit,                 // 10 ldr r2, [r12, #traceLimit]
            0xE1500002,                                  // 11 cmp r0, r2
            (LS << 28) | 0x03833008,                     // 12 orrls r3, r3, #8    a traced call
            0xE1833400,                                  // 13 orr r3, r3, r0, lsl #8   the call's number
            0xE59C2000 | StubPredecessor,                // 14 ldr r2, [r12, #predecessor]
            0xE3520000,                                  // 15 cmp r2, #0
            StubBranch(EQ, 16, 25),                      // 16 beq 25              the first of the chain
            0xE2822000 | StubToNext,                     // 17 add r2, r2, #toNext
            0xE1920F9F,                                  // 18 ldrex r0, [r2]
            0xE2800001,                                  // 19 add r0, r0, #1
            0xE1821F90,                                  // 20 strex r1, r0, [r2]
            0xE3510000,                                  // 21 cmp r1, #0
            StubBranch(NE, 22, 18),                      // 22 bne 18
            0xE3500001,                                  // 23 cmp r0, #1
            0x03833002,                                  // 24 orreq r3, r3, #2    the previous hook's first continuation
            0xE31300FF,                                  // 25 tst r3, #0xFF
            StubBranch(EQ, 26, 33),                      // 26 beq 33
            0xE1A0000C,                                  // 27 mov r0, r12
            0xE1A01003,                                  // 28 mov r1, r3
            0xE92D5000,                                  // 29 push {r12, lr}
            0xE28FE000,                                  // 30 add lr, pc, #0      the HLE call returns to 32
            svcWord,                                     // 31 svc
            0xE8BD5000,                                  // 32 pop {r12, lr}
            0xE8BD000F,                                  // 33 pop {r0-r3}
            0xE59CC000 | StubHookFunction,               // 34 ldr r12, [r12, #hook]
            0xE12FFF1C,                                  // 35 bx r12
            // continuation to the original (the record's old)
            0xE92D000F,                                  // 36 push {r0-r3}
            0xE28FC000 | ((StubDataWord - StubOldWord - 1) * 4 - 8), // 37 add r12, pc, #data
            0xE3A03000,                                  // 38 mov r3, #0
            0xE28C2000 | StubToOriginal,                 // 39 add r2, r12, #toOriginal
            0xE1920F9F,                                  // 40 ldrex r0, [r2]
            0xE2800001,                                  // 41 add r0, r0, #1
            0xE1821F90,                                  // 42 strex r1, r0, [r2]
            0xE3510000,                                  // 43 cmp r1, #0
            StubBranch(NE, 44, 40),                      // 44 bne 40
            0xE3500001,                                  // 45 cmp r0, #1
            0x03833004,                                  // 46 orreq r3, r3, #4    first continuation to the original
            0xE59C2000 | StubTraceLimit,                 // 47 ldr r2, [r12, #traceLimit]
            0xE1500002,                                  // 48 cmp r0, r2
            (LS << 28) | 0x03833010,                     // 49 orrls r3, r3, #16   a traced continuation
            0xE1833400,                                  // 50 orr r3, r3, r0, lsl #8   its number
            0xE31300FF,                                  // 51 tst r3, #0xFF
            StubBranch(EQ, 52, 59),                      // 52 beq 59
            0xE1A0000C,                                  // 53 mov r0, r12
            0xE1A01003,                                  // 54 mov r1, r3
            0xE92D5000,                                  // 55 push {r12, lr}
            0xE28FE000,                                  // 56 add lr, pc, #0      the HLE call returns to 58
            svcWord,                                     // 57 svc
            0xE8BD5000,                                  // 58 pop {r12, lr}
            0xE8BD000F,                                  // 59 pop {r0-r3}
            0xE59CC000 | StubTrampoline,                 // 60 ldr r12, [r12, #trampoline]
            0xE12FFF1C,                                  // 61 bx r12
        ];
    }

    /// <summary>Writes the SVC numbers of this session into a stub (its two SVCs and its return words).</summary>
    void WriteStubSvcs(uint data, int traced)
    {
        (uint eventSvc, uint returnSvc) = Stats.SvcIndexes(Emu);
        uint stub = data - StubDataWord * 4;
        GuestMemory.W32(stub + StubEntrySvcWord * 4, 0xEF000000 | (eventSvc & 0xFFFFFF));
        GuestMemory.W32(stub + StubOldSvcWord * 4, 0xEF000000 | (eventSvc & 0xFFFFFF));
        for (int slot = 0; slot < 2 * traced; slot++)
        {
            GuestMemory.W32(StubReturnAddress(data, slot), 0xEF000000 | (returnSvc & 0xFFFFFF));
        }

        InvalidateEverywhere(stub, StubSize(traced));
    }

    /// <summary>Allocates and writes the counting stub of a hook: its code, the hook function, the trampoline, the
    /// trace limit, counters at 0; returns the address of its data.</summary>
    uint NewCountingStub(uint hookFunction, uint trampoline)
    {
        int traced = PluginHookStats.TraceCalls;
        uint stub = KernelAllocation(StubSize(traced), "plugin hook counter");
        uint[] code = CountingStubCode(Stats.SvcIndexes(Emu).Event);
        for (int i = 0; i < code.Length; i++)
        {
            GuestMemory.W32(stub + (uint)i * 4, code[i]);
        }

        uint data = stub + StubDataWord * 4;
        GuestMemory.W32(data + StubCalls, 0);
        GuestMemory.W32(data + StubToNext, 0);
        GuestMemory.W32(data + StubToOriginal, 0);
        GuestMemory.W32(data + StubHookFunction, hookFunction);
        GuestMemory.W32(data + StubTrampoline, trampoline);
        GuestMemory.W32(data + StubPredecessor, 0);
        GuestMemory.W32(data + StubMagic, StubMagicValue);
        GuestMemory.W32(data + StubTraceLimit, (uint)traced);
        WriteStubSvcs(data, traced);
        return data;
    }

    /// <summary>The record's func and old become the stub's entry and continuation.</summary>
    static void PointRecordAtStub(uint record, uint data)
    {
        uint stub = data - StubDataWord * 4;
        GuestMemory.W32(record + 4, stub);
        GuestMemory.W32(record + 8, stub + StubOldWord * 4);
    }

    /// <summary>The data of the counting stub a record's func points at, or 0 (a record of a state saved without
    /// them, or with a stub of the first version: <paramref name="firstVersion"/>).</summary>
    static uint StubDataOf(uint record, out bool firstVersion)
    {
        firstVersion = false;
        uint stub = GuestMemory.R32(record + 4);
        if ((stub & 3) != 0 || !KernelSpace.IsMappedRange(stub, StubSize(0)))
        {
            return 0;
        }

        uint data = stub + StubDataWord * 4;
        if (GuestMemory.R32(data + StubMagic) == StubMagicValue)
        {
            return data;
        }

        uint dataV1 = stub + StubDataWordV1 * 4;
        firstVersion = GuestMemory.R32(dataV1 + StubMagic) == StubMagicV1;
        return firstVersion ? dataV1 : 0;
    }

    static uint StubDataOf(uint record)
    {
        uint data = StubDataOf(record, out bool firstVersion);
        return firstVersion ? 0 : data;
    }

    /// <summary>The stub's allocation (for freeing).</summary>
    static uint StubOf(uint data)
    {
        return data - StubDataWord * 4;
    }

    /// <summary>Each hook's stub of <paramref name="patch"/> counts its continuations into the hook before it in the
    /// chain (the first one's calls come from the hooked function).</summary>
    static void LinkStubs(HookPatch patch)
    {
        uint previous = 0;
        foreach (uint record in patch.Records)
        {
            uint data = StubDataOf(record);
            if (data != 0)
            {
                GuestMemory.W32(data + StubPredecessor, previous);
            }

            previous = data;
        }
    }

    /// <summary>The module of process <paramref name="pid"/> (or of the kernel) whose code is at
    /// <paramref name="address"/>: the plugin that installed a hook.</summary>
    string ModuleNameAt(int pid, uint address)
    {
        foreach (int owner in pid == KernelSpace.KernelPid ? new[] { pid } : new[] { pid, KernelSpace.KernelPid })
        {
            if (KernelSpace.ModulesOf(Emu, owner) is not { } modules)
            {
                continue;
            }

            foreach (Loader.LoadedModule module in modules)
            {
                foreach (Loader.SegmentInfo segment in module.Segments.Values)
                {
                    if ((address & ~1u) - segment.Address < segment.Size)
                    {
                        return module.Name;
                    }
                }
            }
        }

        return $"code at 0x{address:X8}";
    }

    /// <summary>
    /// After a save state load: the hooks of the state count again. Their stubs and counters are the state's guest
    /// memory; the SVC numbers of this session go into each stub (a traced call in flight returns through its saved lr,
    /// which is guest memory too); a record of a state saved without a stub, or with a stub of the first version, gets
    /// a new one (the counters carried over); the summary takes the state's hooks in place of the ones this framework
    /// had before the load.
    /// </summary>
    void RestoreCountingStubs()
    {
        Stats.RemoveOwnedBy(Emu);
        Stats.ForgetSvcIndexes();
        foreach (HookPatch patch in _hooks)
        {
            using ProcessScope.Scope space = EnterPatchSpace(patch.Pid);
            foreach (uint record in patch.Records)
            {
                uint data = StubDataOf(record, out bool firstVersion);
                if (data == 0 || firstVersion)
                {
                    uint hook = data != 0 ? GuestMemory.R32(data + StubHookFunction) : GuestMemory.R32(record + 4);
                    uint trampoline = data != 0 ? GuestMemory.R32(data + StubTrampoline) : GuestMemory.R32(record + 8);
                    uint fresh = NewCountingStub(hook, trampoline);
                    if (data != 0)
                    {
                        GuestMemory.W32(fresh + StubCalls, GuestMemory.R32(data + StubCalls));
                        GuestMemory.W32(fresh + StubToNext, GuestMemory.R32(data + StubToNext));
                        GuestMemory.W32(fresh + StubToOriginal, GuestMemory.R32(data + StubToOriginal));
                    }

                    PointRecordAtStub(record, fresh);
                    data = fresh;
                }
                else
                {
                    WriteStubSvcs(data, (int)GuestMemory.R32(data + StubTraceLimit));
                }

                uint hookFunction = GuestMemory.R32(data + StubHookFunction);
                string target = patch.RedirectedNid != 0 ? $"{NidDatabase.NameOf(patch.RedirectedNid)} of library 0x{patch.RedirectedLibNid:X8}" : $"code at 0x{patch.Address:X8}";
                Stats.Add(new PluginHookStats.Entry
                {
                    Kind = $"hook, {(patch.Pid == KernelSpace.KernelPid ? "kernel" : "user")} (from the save state)",
                    Target = target,
                    Installer = ModuleNameAt(patch.Pid, hookFunction),
                    Pid = patch.Pid,
                    Address = patch.Address,
                    HookFunction = hookFunction,
                    Result = patch.Uid,
                    Record = record,
                    Data = data,
                    Owner = Emu,
                    FirstCaller = "before the save state",
                });
            }

            LinkStubs(patch);
        }
    }

    /// <summary>The plugins and hooks of <paramref name="emu"/>'s system as a recording's context.txt keeps them.</summary>
    public static IEnumerable<string> HookContextLines(Emulator emu)
    {
        return PluginHookStats.Of(emu).ContextLines();
    }

    /// <summary>The summary lines of the hooks of <paramref name="emu"/>'s system (session end).</summary>
    public static IEnumerable<string> HookSummaryLines(Emulator emu)
    {
        return PluginHookStats.Of(emu).SummaryLines();
    }
}
