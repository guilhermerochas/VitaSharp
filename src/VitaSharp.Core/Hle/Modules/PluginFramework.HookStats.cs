using System.Runtime.CompilerServices;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// What the plugin hooks of one emulated system did (emulator option "Enable Kernel Plugins"): every hook installed (or
/// refused), who installed it, how many calls entered it and how many it continued, to the next hook of its chain or to
/// the original. The counting is guest code: each hook's reference record points at a small ARM stub of its own
/// (<see cref="PluginFramework.NewCountingStub"/>) that adds one to the hook's counter in kernel memory and jumps on,
/// so it costs a few instructions in every CPU backend (interpreter, JIT, precompiled code) and on every path into the
/// hook (the patched function, the import stubs a hook took, a syscall export's HLE stub, a call through the hook
/// chain). Only the first call, the first continuations and the first traced calls (<see cref="TraceCalls"/>) enter C#
/// (an SVC into <see cref="OnEvent"/>); the traced calls' returns come back through the stub's return words
/// (<see cref="OnReturn"/>, PluginFramework.HookTrace.cs).
/// </summary>
internal sealed partial class PluginHookStats
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
        /// <summary>What the traced calls did (<see cref="TraceCalls"/>).</summary>
        public readonly TraceTotals Trace = new();
    }

    /// <summary>Events the stub passes in the low byte of r1 (the counter after the call's increment above it): the
    /// counter went from 0 to 1 (calls, continuations to the next hook, to the original), or the call or continuation is
    /// one of the first <see cref="TraceCalls"/>.</summary>
    internal const uint FirstCall = 1, FirstToNext = 2, FirstToOriginal = 4, TracedCall = 8, TracedOriginal = 16;

    /// <summary>The first calls of each hook whose action is logged (arguments, continuation, returns, writes):
    /// <see cref="Util.EnvVars.PluginHookTrace"/>, 8 by default, 0 for none, at most 256.</summary>
    internal static readonly int TraceCalls = ReadTraceCalls();

    static int ReadTraceCalls()
    {
        string? value = Environment.GetEnvironmentVariable(Util.EnvVars.PluginHookTrace);
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int calls))
        {
            return Math.Clamp(calls, 0, 256);
        }

        return 8;
    }

    readonly object _lock = new();
    readonly List<Entry> _entries = new();
    readonly Dictionary<uint, Entry> _byData = new();
    uint _svc;
    uint _returnSvc;

    static readonly ConditionalWeakTable<Emulator, PluginHookStats> s_own = new();

    /// <summary>The hooks of <paramref name="emu"/>'s system (an emulator made without one keeps its own: tests).</summary>
    internal static PluginHookStats Of(Emulator emu)
    {
        return emu.Vita is { } vita ? vita.PluginHooks : s_own.GetValue(emu, _ => new PluginHookStats());
    }

    const string EventName = "PluginFramework:hookEvent";
    const string ReturnName = "PluginFramework:hookReturn";

    /// <summary>The first version's name of <see cref="OnEvent"/> (its states' stubs call it until the load replaces
    /// them).</summary>
    const string EventNameV1 = "PluginFramework:hookFirstEvent";

    /// <summary>Every process declares the stubs' host functions when it starts (<see cref="PluginFramework.Initialize"/>),
    /// so a save state with hooks loads in a session that has none yet.</summary>
    internal void Declare(Emulator emu)
    {
        emu.Hle.DeclareSharedHostFunction(EventName, OnEvent);
        emu.Hle.DeclareSharedHostFunction(EventNameV1, OnEvent);
        emu.Hle.DeclareSharedHostFunction(ReturnName, OnReturn);
    }

    /// <summary>The SVC numbers of <see cref="OnEvent"/> and <see cref="OnReturn"/> in every process of
    /// <paramref name="emu"/>'s system: those its registry has (a loaded state's), else new ones.</summary>
    internal (uint Event, uint Return) SvcIndexes(Emulator emu)
    {
        lock (_lock)
        {
            if (_svc == 0)
            {
                _svc = IndexOf(emu, EventName) ?? emu.Hle.RegisterSharedHostFunction(EventName, OnEvent);
                _returnSvc = IndexOf(emu, ReturnName) ?? emu.Hle.RegisterSharedHostFunction(ReturnName, OnReturn);
            }

            return (_svc, _returnSvc);
        }
    }

    /// <summary>A save state was loaded: the SVC numbers are looked up again (the state's table is the one now).</summary>
    internal void ForgetSvcIndexes()
    {
        lock (_lock)
        {
            _svc = 0;
            _returnSvc = 0;
        }
    }

    static uint? IndexOf(Emulator emu, string name)
    {
        IReadOnlyList<HleFunction> functions = emu.Hle.Functions;
        for (int i = 1; i < functions.Count; i++)
        {
            if (functions[i].Nid == 0 && functions[i].Library == "host" && functions[i].Name == name)
            {
                return (uint)i;
            }
        }

        return null;
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

            _frames.RemoveAll(f => f.Entry.Owner == owner);
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
            _frames.RemoveAll(f => f.Entry == entry);
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

    /// <summary>The calls that neither continued to the next hook nor to the original (each continues at most once).</summary>
    static uint AnsweredItself(uint calls, uint toNext, uint toOriginal)
    {
        return calls > toNext + toOriginal ? calls - toNext - toOriginal : 0;
    }

    /// <summary>The stub's SVC (r0: its counters, r1: the events in the low byte and the counter above them; sp: the
    /// saved r12, lr, r0-r3 and then the caller's stack): one line per first event, the traced calls' frames.</summary>
    void OnEvent(HleContext context)
    {
        uint data;
        uint events;
        uint sp;
        unsafe
        {
            data = context.Cpu->R[0];
            events = context.Cpu->R[1] & 0xFF;
            sp = context.Cpu->R[13];
        }

        uint counter;
        unsafe
        {
            counter = context.Cpu->R[1] >> 8;
        }

        Entry? entry = ByData(data);
        if (entry == null)
        {
            return;
        }

        string caller = $"pid 0x{context.Thread.Pid:X} (thread {context.Thread.Name})";
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

        if ((events & TracedCall) != 0)
        {
            BeginCall(context.Thread, entry, data, counter, sp);
        }

        if ((events & TracedOriginal) != 0)
        {
            BeginOriginal(context.Thread, entry, data, counter, sp);
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
    /// The plugins and their hooks as a recording's context.txt keeps them (at its start and its stop), so the analysis
    /// (report.txt) needs neither the game nor the plugins: the build and the firmware, the plugin configuration read
    /// and each plugin module (<see cref="LoadLines"/>), then one tab-separated line per hook: number, kind, target,
    /// pid, address, installer, hook function, result (UID or error), calls, continuations to the original, to the next
    /// hook, first caller, state (installed, released, refused), calls that answered themselves, what the traced calls
    /// did.
    /// </summary>
    internal IEnumerable<string> ContextLines()
    {
        foreach (string line in LoadLines())
        {
            yield return line;
        }

        foreach (Entry entry in Entries())
        {
            (uint calls, uint toNext, uint toOriginal) = Counters(entry);
            string state = entry.Result <= 0 ? "refused" : entry.Released ? "released" : "installed";
            yield return string.Join('\t', ContextPrefix + entry.Number, entry.Kind, entry.Target, $"pid 0x{entry.Pid:X}", $"0x{entry.Address:X8}",
                entry.Installer, $"0x{entry.HookFunction:X8}", $"0x{entry.Result:X8}", calls, toOriginal, toNext, entry.FirstCaller, state,
                AnsweredItself(calls, toNext, toOriginal), DescribeTrace(entry));
        }
    }

    /// <summary>
    /// report.txt's "Plugins" and "Plugin hooks" sections from a recording's context.txt lines at its start and its stop
    /// (<see cref="ContextLines"/>): the build, the firmware, the configuration and the plugin modules, then each hook's
    /// calls and continuations during the recording and in the whole session, "never called" for a hook no call
    /// entered, the error of a refused one, what its traced calls did. Empty when no plugin was configured or hooked
    /// anything.
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
        var stopLines = stop.ToList();
        foreach (string line in stopLines)
        {
            if (line.StartsWith(LoadPrefix, StringComparison.Ordinal))
            {
                if (lines.Count == 0)
                {
                    lines.Add("Plugins (at the stop of the recording):");
                }

                lines.Add("  " + line[LoadPrefix.Length..].Replace('\t', ' '));
            }
        }

        bool hooksHeader = false;
        foreach (string line in stopLines)
        {
            string[] stopFields = line.Split('\t');
            if (!line.StartsWith(ContextPrefix, StringComparison.Ordinal) || stopFields.Length < 13)
            {
                continue;
            }

            if (!hooksHeader)
            {
                hooksHeader = true;
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

            string answered = stopFields.Length > 13 ? $", answered itself {stopFields[13]}" : "";
            string traced = stopFields.Length > 14 && stopFields[14].Length > 0 ? $"; {stopFields[14]}" : "";
            lines.Add($"{head}: calls {During(8)} / {stopFields[8]}, continued to the original {During(9)} / {stopFields[9]}, to the next hook {During(10)} / {stopFields[10]}{answered}; first call from {stopFields[11]}{installedNow}{state}{traced}");
        }

        return lines;
    }

    /// <summary>One hook's line of the summary.</summary>
    internal string Describe(Entry entry)
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

        string trace = DescribeTrace(entry);
        string traced = trace.Length > 0 ? "; " + trace : "";
        return $"{head}: {calls} calls (first from {entry.FirstCaller}), continued to the original {toOriginal}, to the next hook {toNext}, answered itself {AnsweredItself(calls, toNext, toOriginal)}{released}{traced}";
    }
}
