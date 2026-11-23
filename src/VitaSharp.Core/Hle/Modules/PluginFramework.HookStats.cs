using System.Runtime.CompilerServices;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// What the plugin hooks of one emulated system did (emulator option "Enable Kernel Plugins"): every hook installed (or
/// refused), who installed it, how many calls entered it and how many it continued, to the next hook of its chain or to
/// the original. The counting is guest code: each hook's reference record points at a small ARM stub of its own
/// (<see cref="PluginFramework.WriteCountingStub"/>) that adds one to the hook's counter in kernel memory and jumps on,
/// so it costs a few instructions in every CPU backend (interpreter, JIT, precompiled code) and on every path into the
/// hook (the patched function, the import stubs a hook took, a syscall export's HLE stub, a call through the hook
/// chain). Only the first call, the first continuation to the next hook and the first continuation to the original
/// enter C# (an SVC into <see cref="OnFirst"/>), which writes one log line each.
/// </summary>
internal sealed class PluginHookStats
{
    /// <summary>One hook: what it hooks, who installed it and its counters.</summary>
    internal sealed class Entry
    {
        public int Number;
        /// <summary>export, import, offset or absolute; kernel or user.</summary>
        public string Kind = "";
        /// <summary>The hooked function, as the log names it ("sceFoo of library 0x12345678", "code at 0x81000000").</summary>
        public string Target = "";
        /// <summary>The module whose code the hook function is (the plugin that installed the hook).</summary>
        public string Installer = "";
        public int Pid;
        public uint Address;
        public uint HookFunction;
        /// <summary>The framework's result: the hook's UID, or its error code (the hook was refused).</summary>
        public int Result;
        /// <summary>The hook's reference record (0: refused).</summary>
        public uint Record;
        /// <summary>The counters of the hook's stub in kernel memory (0: refused, or the stub went with its process).</summary>
        public uint Data;
        /// <summary>The process whose address space reads <see cref="Data"/>.</summary>
        public Emulator? Owner;
        /// <summary>The counters when the stub was freed (the hook's process ended).</summary>
        public uint FinalCalls, FinalToNext, FinalToOriginal;
        public bool Released;
        /// <summary>The first call: process and thread (empty before it).</summary>
        public string FirstCaller = "";
    }

    /// <summary>Events the stub passes in r1 (each once: the counter went from 0 to 1).</summary>
    internal const uint FirstCall = 1, FirstToNext = 2, FirstToOriginal = 4;

    readonly object _lock = new();
    readonly List<Entry> _entries = new();
    readonly Dictionary<uint, Entry> _byData = new();
    uint _svc;

    static readonly ConditionalWeakTable<Emulator, PluginHookStats> s_own = new();

    /// <summary>The hooks of <paramref name="emu"/>'s system (an emulator made without one keeps its own: tests).</summary>
    internal static PluginHookStats Of(Emulator emu)
    {
        return emu.Vita is { } vita ? vita.PluginHooks : s_own.GetValue(emu, _ => new PluginHookStats());
    }

    /// <summary>The SVC number of <see cref="OnFirst"/> in every process of <paramref name="emu"/>'s system (registered
    /// on the first hook).</summary>
    internal uint SvcIndex(Emulator emu)
    {
        lock (_lock)
        {
            if (_svc == 0)
            {
                _svc = emu.Hle.RegisterSharedHostFunction("PluginFramework:hookFirstEvent", OnFirst);
            }

            return _svc;
        }
    }

    internal Entry Add(Entry entry)
    {
        lock (_lock)
        {
            entry.Number = _entries.Count + 1;
            _entries.Add(entry);
            if (entry.Data != 0)
            {
                _byData[entry.Data] = entry;
            }

            return entry;
        }
    }

    internal Entry? ByData(uint data)
    {
        lock (_lock)
        {
            return _byData.GetValueOrDefault(data);
        }
    }

    /// <summary>The hooks so far, oldest first (a copy).</summary>
    internal List<Entry> Entries()
    {
        lock (_lock)
        {
            return new List<Entry>(_entries);
        }
    }

    /// <summary>A save state was loaded: the hooks <paramref name="owner"/>'s framework installed before it go (the
    /// state's come back, <see cref="PluginFramework.RestoreCountingStubs"/>); refused ones stay.</summary>
    internal void RemoveOwnedBy(Emulator owner)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Owner == owner);
            foreach (uint data in _byData.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key).ToList())
            {
                _byData.Remove(data);
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                _entries[i].Number = i + 1;
            }
        }
    }

    /// <summary>The hook's stub goes (its process ended): its counters are kept as they are now.</summary>
    internal void Freeze(Entry entry)
    {
        (entry.FinalCalls, entry.FinalToNext, entry.FinalToOriginal) = Counters(entry);
        lock (_lock)
        {
            _byData.Remove(entry.Data);
            entry.Data = 0;
        }
    }

    /// <summary>Calls, continuations to the next hook and continuations to the original of <paramref name="entry"/>.</summary>
    internal static (uint Calls, uint ToNext, uint ToOriginal) Counters(Entry entry)
    {
        if (entry.Data == 0 || entry.Owner == null)
        {
            return (entry.FinalCalls, entry.FinalToNext, entry.FinalToOriginal);
        }

        using (ProcessScope.Enter(entry.Owner))
        {
            return (GuestMemory.R32(entry.Data + PluginFramework.StubCalls), GuestMemory.R32(entry.Data + PluginFramework.StubToNext),
                GuestMemory.R32(entry.Data + PluginFramework.StubToOriginal));
        }
    }

    /// <summary>The stub's SVC (r0: its counters, r1: <see cref="FirstCall"/> and the others): one line per event.</summary>
    void OnFirst(HleContext context)
    {
        uint data;
        uint events;
        unsafe
        {
            data = context.Cpu->R[0];
            events = context.Cpu->R[1];
        }

        string caller = $"pid 0x{context.Thread.Pid:X} (thread {context.Thread.Name})";
        Entry? entry = ByData(data);
        if (entry == null)
        {
            return;
        }

        if ((events & FirstCall) != 0)
        {
            entry.FirstCaller = caller;
            Util.Log.Info($"plugin hook {entry.Number} called: {entry.Target} from {caller} -> hook function 0x{entry.HookFunction:X8}");
        }

        if ((events & FirstToNext) != 0 && ByData(GuestMemory.R32(data + PluginFramework.StubPredecessor)) is { } previous)
        {
            Util.Log.Info($"plugin hook {previous.Number} continued to the next hook (plugin hook {entry.Number}): {previous.Target} from {caller}");
        }

        if ((events & FirstToOriginal) != 0)
        {
            Util.Log.Info($"plugin hook {entry.Number} continued to the original: {entry.Target} from {caller}");
        }
    }

    /// <summary>The session's summary (session end): each hook, its installer and counters, "never called" for those
    /// no call entered; nothing when no plugin hooked anything.</summary>
    internal IEnumerable<string> SummaryLines()
    {
        List<Entry> entries = Entries();
        if (entries.Count == 0)
        {
            yield break;
        }

        int installed = 0;
        int called = 0;
        foreach (Entry entry in entries)
        {
            if (entry.Result > 0)
            {
                installed++;
                if (Counters(entry).Calls > 0)
                {
                    called++;
                }
            }
        }

        yield return $"Plugin hooks: {installed} installed, {called} called, {installed - called} never called, {entries.Count - installed} refused";
        foreach (Entry entry in entries)
        {
            yield return "  " + Describe(entry);
        }
    }

    /// <summary>The prefix of a hook's line in a recording's context.txt (<see cref="ContextLines"/>).</summary>
    internal const string ContextPrefix = "plugin hook: ";

    /// <summary>
    /// The hooks as a recording's context.txt keeps them (at its start and its stop), one tab-separated line each, so
    /// the analysis (report.txt) needs neither the game nor the plugins: number, kind, target, pid, address, installer,
    /// hook function, result (UID or error), calls, continuations to the original, to the next hook, first caller,
    /// state (installed, released, refused).
    /// </summary>
    internal IEnumerable<string> ContextLines()
    {
        foreach (Entry entry in Entries())
        {
            (uint calls, uint toNext, uint toOriginal) = Counters(entry);
            string state = entry.Result <= 0 ? "refused" : entry.Released ? "released" : "installed";
            yield return string.Join('\t', ContextPrefix + entry.Number, entry.Kind, entry.Target, $"pid 0x{entry.Pid:X}", $"0x{entry.Address:X8}",
                entry.Installer, $"0x{entry.HookFunction:X8}", $"0x{entry.Result:X8}", calls, toOriginal, toNext, entry.FirstCaller, state);
        }
    }

    /// <summary>
    /// report.txt's "Plugin hooks" section from a recording's context.txt lines at its start and its stop
    /// (<see cref="ContextLines"/>): each hook's calls and continuations during the recording and in the whole session,
    /// "never called" for a hook no call entered, the error of a refused one. Empty when no plugin hooked anything.
    /// </summary>
    public static List<string> ReportSection(IEnumerable<string> start, IEnumerable<string> stop)
    {
        var before = new Dictionary<string, string[]>();
        foreach (string line in start)
        {
            string[] fields = line.Split('\t');
            if (line.StartsWith(ContextPrefix, StringComparison.Ordinal) && fields.Length >= 13)
            {
                before[fields[0]] = fields;
            }
        }

        var lines = new List<string>();
        foreach (string line in stop)
        {
            string[] stopFields = line.Split('\t');
            if (!line.StartsWith(ContextPrefix, StringComparison.Ordinal) || stopFields.Length < 13)
            {
                continue;
            }

            if (lines.Count == 0)
            {
                lines.Add("Plugin hooks (calls and continuations: during the recording / in the session):");
            }

            string number = stopFields[0][ContextPrefix.Length..];
            string head = $"  hook {number}: {stopFields[1]}, {stopFields[2]}, {stopFields[3]} at {stopFields[4]}, installed by {stopFields[5]} (hook function {stopFields[6]})";
            if (stopFields[12] == "refused")
            {
                lines.Add($"{head}: refused ({stopFields[7]})");
                continue;
            }

            string[]? startFields = before.GetValueOrDefault(stopFields[0]);
            long During(int index)
            {
                long total = long.Parse(stopFields[index], System.Globalization.CultureInfo.InvariantCulture);
                long atStart = startFields != null ? long.Parse(startFields[index], System.Globalization.CultureInfo.InvariantCulture) : 0;
                return total - atStart;
            }

            string state = stopFields[12] == "released" ? ", released" : "";
            string installedNow = startFields == null ? ", installed during the recording" : "";
            if (stopFields[8] == "0")
            {
                lines.Add($"{head}: never called{installedNow}{state}");
                continue;
            }

            lines.Add($"{head}: calls {During(8)} / {stopFields[8]}, continued to the original {During(9)} / {stopFields[9]}, to the next hook {During(10)} / {stopFields[10]}; first call from {stopFields[11]}{installedNow}{state}");
        }

        return lines;
    }

    /// <summary>One hook's line of the summary.</summary>
    internal static string Describe(Entry entry)
    {
        string head = $"plugin hook {entry.Number} ({entry.Kind}, pid 0x{entry.Pid:X}): {entry.Target}, installed by {entry.Installer}";
        if (entry.Result <= 0)
        {
            return $"{head}: refused (0x{entry.Result:X8})";
        }

        (uint calls, uint toNext, uint toOriginal) = Counters(entry);
        string released = entry.Released ? ", released" : "";
        if (calls == 0)
        {
            return $"{head}: never called{released}";
        }

        return $"{head}: {calls} calls (first from {entry.FirstCaller}), continued to the original {toOriginal}, to the next hook {toNext}{released}";
    }
}

/// <summary>The counting stubs of the hooks (<see cref="PluginHookStats"/>).</summary>
public sealed partial class PluginFramework
{
    // the stub: 32 words entering the hook, 22 continuing to the original, then its data
    const int StubOldWord = 32;
    const int StubDataWord = 54;

    /// <summary>The words of the two SVCs (an HLE call returns to lr: each sets lr to the word after it).</summary>
    internal const int StubEntrySvcWord = 27, StubOldSvcWord = 49;
    const uint StubSize = (StubDataWord + 7) * 4;
    internal const uint StubCalls = 0, StubToNext = 4, StubToOriginal = 8, StubHookFunction = 12, StubTrampoline = 16, StubPredecessor = 20, StubMagic = 24;

    /// <summary>Marks a counting stub's data (a state saved by a build without them has none).</summary>
    const uint StubMagicValue = 0x4B4F4F48; // "HOOK"

    PluginHookStats Stats => PluginHookStats.Of(Emu);

    /// <summary>A conditional branch from word <paramref name="from"/> to word <paramref name="to"/>.</summary>
    static uint StubBranch(uint cond, int from, int to)
    {
        return (cond << 28) | 0x0A000000 | ((uint)(to - from - 2) & 0xFFFFFF);
    }

    /// <summary>
    /// The counting stub of one hook (ARM, at <paramref name="stub"/>): its entry (the record's func) adds one to the
    /// hook's calls and, when the hook is not the first of its chain, to the previous hook's continuations to the next
    /// hook, then jumps to the hook function; its continuation (the record's old) adds one to the continuations to the
    /// original and jumps to the trampoline. Each counter goes up atomically (ldrex/strex: hooks run on every core) and
    /// only the call that takes a counter from 0 to 1 enters C# (an SVC, r0-r3, r12 and lr kept; the HLE call returns to
    /// lr, so the stub points lr at the word after the SVC). The caller's
    /// registers and stack are untouched (r12 and the flags are scratch at a call), so the hook sees the call as it was.
    /// </summary>
    static uint[] CountingStubCode(uint svc)
    {
        uint svcWord = 0xEF000000 | (svc & 0xFFFFFF);
        const uint EQ = 0x0, NE = 0x1;
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
            0xE59C2000 | StubPredecessor,                // 10 ldr r2, [r12, #predecessor]
            0xE3520000,                                  // 11 cmp r2, #0
            StubBranch(EQ, 12, 21),                      // 12 beq 21              the first of the chain
            0xE2822000 | StubToNext,                     // 13 add r2, r2, #toNext
            0xE1920F9F,                                  // 14 ldrex r0, [r2]
            0xE2800001,                                  // 15 add r0, r0, #1
            0xE1821F90,                                  // 16 strex r1, r0, [r2]
            0xE3510000,                                  // 17 cmp r1, #0
            StubBranch(NE, 18, 14),                      // 18 bne 14
            0xE3500001,                                  // 19 cmp r0, #1
            0x03833002,                                  // 20 orreq r3, r3, #2    the previous hook's first continuation
            0xE3530000,                                  // 21 cmp r3, #0
            StubBranch(EQ, 22, 29),                      // 22 beq 29
            0xE1A0000C,                                  // 23 mov r0, r12
            0xE1A01003,                                  // 24 mov r1, r3
            0xE92D5000,                                  // 25 push {r12, lr}
            0xE28FE000,                                  // 26 add lr, pc, #0      the HLE call returns to 28
            svcWord,                                     // 27 svc: the log line
            0xE8BD5000,                                  // 28 pop {r12, lr}
            0xE8BD000F,                                  // 29 pop {r0-r3}
            0xE59CC000 | StubHookFunction,               // 30 ldr r12, [r12, #hook]
            0xE12FFF1C,                                  // 31 bx r12
            // continuation to the original (the record's old)
            0xE92D000F,                                  // 32 push {r0-r3}
            0xE28FC000 | ((StubDataWord - StubOldWord - 1) * 4 - 8), // 33 add r12, pc, #data
            0xE3A03000,                                  // 34 mov r3, #0
            0xE28C2000 | StubToOriginal,                 // 35 add r2, r12, #toOriginal
            0xE1920F9F,                                  // 36 ldrex r0, [r2]
            0xE2800001,                                  // 37 add r0, r0, #1
            0xE1821F90,                                  // 38 strex r1, r0, [r2]
            0xE3510000,                                  // 39 cmp r1, #0
            StubBranch(NE, 40, 36),                      // 40 bne 36
            0xE3500001,                                  // 41 cmp r0, #1
            0x03833004,                                  // 42 orreq r3, r3, #4    first continuation to the original
            0xE3530000,                                  // 43 cmp r3, #0
            StubBranch(EQ, 44, 51),                      // 44 beq 51
            0xE1A0000C,                                  // 45 mov r0, r12
            0xE1A01003,                                  // 46 mov r1, r3
            0xE92D5000,                                  // 47 push {r12, lr}
            0xE28FE000,                                  // 48 add lr, pc, #0      the HLE call returns to 50
            svcWord,                                     // 49 svc: the log line
            0xE8BD5000,                                  // 50 pop {r12, lr}
            0xE8BD000F,                                  // 51 pop {r0-r3}
            0xE59CC000 | StubTrampoline,                 // 52 ldr r12, [r12, #trampoline]
            0xE12FFF1C,                                  // 53 bx r12
        ];
    }

    /// <summary>Writes the counting stub of a hook at <paramref name="stub"/>: its code, the hook function, the
    /// trampoline, counters at 0; returns the address of its data.</summary>
    uint WriteCountingStub(uint stub, uint hookFunction, uint trampoline)
    {
        uint[] code = CountingStubCode(Stats.SvcIndex(Emu));
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
        InvalidateEverywhere(stub, StubSize);
        return data;
    }

    /// <summary>The data of the counting stub a record's func points at, or 0 (a record of a state saved without
    /// them).</summary>
    static uint StubDataOf(uint record)
    {
        uint stub = GuestMemory.R32(record + 4);
        if ((stub & 3) != 0 || !KernelSpace.IsMappedRange(stub, StubSize))
        {
            return 0;
        }

        uint data = stub + StubDataWord * 4;
        return GuestMemory.R32(data + StubMagic) == StubMagicValue ? data : 0;
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
    /// memory; the SVC number of this session goes into each stub, a record of a state saved without a stub gets one
    /// (its func and old become the stub's, which goes on to them), and the summary takes the state's hooks in place of
    /// the ones this framework had before the load.
    /// </summary>
    void RestoreCountingStubs()
    {
        Stats.RemoveOwnedBy(Emu);
        uint svcWord = 0xEF000000 | (Stats.SvcIndex(Emu) & 0xFFFFFF);
        foreach (HookPatch patch in _hooks)
        {
            using ProcessScope.Scope space = EnterPatchSpace(patch.Pid);
            foreach (uint record in patch.Records)
            {
                uint data = StubDataOf(record);
                if (data == 0)
                {
                    uint stub = KernelAllocation(StubSize, "plugin hook counter");
                    data = WriteCountingStub(stub, GuestMemory.R32(record + 4), GuestMemory.R32(record + 8));
                    GuestMemory.W32(record + 4, stub);
                    GuestMemory.W32(record + 8, stub + StubOldWord * 4);
                }
                else
                {
                    uint stub = data - StubDataWord * 4;
                    GuestMemory.W32(stub + StubEntrySvcWord * 4, svcWord);
                    GuestMemory.W32(stub + StubOldSvcWord * 4, svcWord);
                    InvalidateEverywhere(stub, StubSize);
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

    /// <summary>The hooks of <paramref name="emu"/>'s system as a recording's context.txt keeps them.</summary>
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
