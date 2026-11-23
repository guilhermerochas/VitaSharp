using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The action of each hook in its first calls (<see cref="TraceCalls"/>): the arguments that came in, whether the hook
/// continued (to the next hook or to the original) or answered itself, what the original returned, what the hook
/// returned to its caller ("changed" when that differs), and the memory the arguments point to that the call wrote.
/// A traced call's stub (<see cref="OnEvent"/>) keeps the caller's lr in the stub's own saved-lr word of that call and
/// gives the hook the stub's return word instead (an SVC into <see cref="OnReturn"/>, which puts the saved lr back), so
/// the guest's registers and stack stay as they were and a call in flight survives a save state. After the traced
/// calls a hook costs only its counters.
/// </summary>
internal sealed partial class PluginHookStats
{
    /// <summary>What the traced calls of one hook did.</summary>
    internal sealed class TraceTotals
    {
        public int Started;
        public int Finished;
        public int Answered;
        public int Changed;
        public int Wrote;
        public readonly Dictionary<uint, int> Returns = new();
    }

    /// <summary>One traced call in flight: the hook's (or, <see cref="Original"/>, its continuation to the original's).</summary>
    sealed class Frame
    {
        public required Entry Entry;
        public required uint Data;
        public required GuestThread Thread;
        public bool Original;
        public int Slot;
        public int Call;
        public readonly uint[] Registers = new uint[4];
        public readonly uint[] Stack = new uint[4];
        public int StackWords;
        public readonly byte[]?[] Before = new byte[]?[4];
        public bool ToNext;
        public bool ToOriginal;
        public bool HasInner;
        public uint Inner;
        public Frame? Parent;
    }

    /// <summary>The bytes an argument pointer's memory is compared over (before the call and at its return).</summary>
    const int PointedBytes = 32;

    readonly List<Frame> _frames = new();

    /// <summary>The newest frame of <paramref name="thread"/> for the hook with data <paramref name="data"/> that
    /// matches <paramref name="match"/>, or null (callers hold the lock).</summary>
    Frame? NewestFrame(GuestThread thread, uint data, Func<Frame, bool> match)
    {
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            Frame frame = _frames[i];
            if (frame.Thread == thread && frame.Data == data && !frame.Original && match(frame))
            {
                return frame;
            }
        }

        return null;
    }

    /// <summary>A traced call enters the hook (call number <paramref name="call"/>): its arguments, the memory they point
    /// to, and its return through the stub's return word.</summary>
    void BeginCall(GuestThread thread, Entry entry, uint data, uint call, uint sp)
    {
        int traced = (int)GuestMemory.R32(data + PluginFramework.StubTraceLimit);
        int slot = (int)call - 1;
        if (slot < 0 || slot >= traced)
        {
            return;
        }

        var frame = new Frame { Entry = entry, Data = data, Thread = thread, Slot = slot, Call = (int)call };
        for (int i = 0; i < 4; i++)
        {
            uint value = GuestMemory.R32(sp + 8 + (uint)i * 4);
            frame.Registers[i] = value;
            if (value != 0 && KernelSpace.IsMappedRange(value, PointedBytes))
            {
                frame.Before[i] = GuestMemory.ReadSpan(value, PointedBytes).ToArray();
            }
        }

        while (frame.StackWords < 4 && KernelSpace.IsMappedRange(sp + 24 + (uint)frame.StackWords * 4, 4))
        {
            frame.Stack[frame.StackWords] = GuestMemory.R32(sp + 24 + (uint)frame.StackWords * 4);
            frame.StackWords++;
        }

        GuestMemory.W32(PluginFramework.StubSavedLr(data, traced, slot), GuestMemory.R32(sp + 4));
        GuestMemory.W32(sp + 4, PluginFramework.StubReturnAddress(data, slot));
        uint previous = GuestMemory.R32(data + PluginFramework.StubPredecessor);
        lock (_lock)
        {
            if (previous != 0 && NewestFrame(thread, previous, f => !f.ToNext && !f.ToOriginal) is { } caller)
            {
                caller.ToNext = true;
            }

            entry.Trace.Started++;
            _frames.Add(frame);
        }
    }

    /// <summary>A traced call continues to the original (continuation number <paramref name="continuation"/>): the
    /// original returns through the stub's return word, which records its value.</summary>
    void BeginOriginal(GuestThread thread, Entry entry, uint data, uint continuation, uint sp)
    {
        int traced = (int)GuestMemory.R32(data + PluginFramework.StubTraceLimit);
        int index = (int)continuation - 1;
        if (index < 0 || index >= traced)
        {
            return;
        }

        int slot = traced + index;
        lock (_lock)
        {
            if (NewestFrame(thread, data, f => !f.ToNext && !f.ToOriginal) is not { } parent)
            {
                return;
            }

            parent.ToOriginal = true;
            _frames.Add(new Frame { Entry = entry, Data = data, Thread = thread, Original = true, Slot = slot, Parent = parent });
        }

        GuestMemory.W32(PluginFramework.StubSavedLr(data, traced, slot), GuestMemory.R32(sp + 4));
        GuestMemory.W32(sp + 4, PluginFramework.StubReturnAddress(data, slot));
    }

    /// <summary>A traced call returns through its stub's return word (the SVC's address; lr is not that word when the
    /// code returned with a pop into pc): the saved lr goes back, then the frame is finished (the original's value goes
    /// to its call; the hook's call is logged).</summary>
    void OnReturn(HleContext context)
    {
        uint svcAddress;
        uint nextAddress;
        uint result;
        unsafe
        {
            svcAddress = context.Cpu->SvcPc;
            nextAddress = context.Cpu->R[15] - 4;
            result = context.Cpu->R[0];
        }

        Entry? entry = null;
        uint returnWord = svcAddress;
        uint data = 0;
        int traced = 0;
        lock (_lock)
        {
            foreach ((uint candidate, Entry owner) in _byData)
            {
                int limit = (int)GuestMemory.R32(candidate + PluginFramework.StubTraceLimit);
                uint first = PluginFramework.StubReturnAddress(candidate, 0);
                uint span = (uint)(2 * limit * 4);
                uint word = svcAddress - first < span ? svcAddress : nextAddress;
                if (word - first < span)
                {
                    returnWord = word;
                    entry = owner;
                    data = candidate;
                    traced = limit;
                    break;
                }
            }
        }

        if (entry == null)
        {
            Util.Log.Error($"plugin hook: a return through 0x{returnWord:X8} belongs to no hook (its stub is gone)");
            return;
        }

        int slot = (int)(returnWord - PluginFramework.StubReturnAddress(data, 0)) / 4;
        uint savedLr = GuestMemory.R32(PluginFramework.StubSavedLr(data, traced, slot));
        unsafe
        {
            context.Cpu->R[14] = savedLr;
        }

        Frame? frame = null;
        lock (_lock)
        {
            for (int i = _frames.Count - 1; i >= 0; i--)
            {
                if (_frames[i].Data == data && _frames[i].Slot == slot)
                {
                    frame = _frames[i];
                    _frames.RemoveAt(i);
                    break;
                }
            }

            if (frame == null)
            {
                // a call in flight when a save state was taken: its lr is back, its arguments are not known
                return;
            }

            if (frame.Original)
            {
                if (frame.Parent != null)
                {
                    frame.Parent.HasInner = true;
                    frame.Parent.Inner = result;
                }

                return;
            }

            uint previous = GuestMemory.R32(data + PluginFramework.StubPredecessor);
            if (previous != 0 && NewestFrame(frame.Thread, previous, f => f.ToNext && !f.HasInner) is { } caller)
            {
                caller.HasInner = true;
                caller.Inner = result;
            }
        }

        Finish(frame, result);
    }

    /// <summary>The hook's traced call returned <paramref name="result"/> to its caller: one line with what it did.</summary>
    void Finish(Frame frame, uint result)
    {
        var line = new StringBuilder();
        line.Append($"plugin hook {frame.Entry.Number} call {frame.Call}:");
        for (int i = 0; i < 4; i++)
        {
            line.Append($" r{i}=0x{frame.Registers[i]:X8}");
        }

        if (frame.StackWords > 0)
        {
            line.Append(" stack=");
            for (int i = 0; i < frame.StackWords; i++)
            {
                line.Append(i > 0 ? "," : "").Append($"0x{frame.Stack[i]:X8}");
            }
        }

        bool continued = frame.ToNext || frame.ToOriginal;
        if (frame.ToOriginal)
        {
            line.Append(frame.HasInner ? $"; continued to the original, which returned 0x{frame.Inner:X8}" : "; continued to the original");
        }
        else if (frame.ToNext)
        {
            line.Append(frame.HasInner ? $"; continued to the next hook, which returned 0x{frame.Inner:X8}" : "; continued to the next hook");
        }
        else
        {
            line.Append("; answered itself");
        }

        bool changed = continued && frame.HasInner && frame.Inner != result;
        line.Append($"; returned 0x{result:X8} to the caller");
        if (changed)
        {
            line.Append(" (changed)");
        }

        bool wrote = false;
        for (int i = 0; i < 4; i++)
        {
            if (frame.Before[i] is not { } before || !KernelSpace.IsMappedRange(frame.Registers[i], PointedBytes))
            {
                continue;
            }

            ReadOnlySpan<byte> after = GuestMemory.ReadSpan(frame.Registers[i], PointedBytes);
            int first = -1;
            int last = -1;
            for (int b = 0; b < PointedBytes; b++)
            {
                if (after[b] != before[b])
                {
                    first = first < 0 ? b : first;
                    last = b;
                }
            }

            if (first >= 0)
            {
                wrote = true;
                line.Append($"; wrote r{i} 0x{frame.Registers[i]:X8} bytes {first}-{last} (now {Convert.ToHexString(after.Slice(first, last - first + 1))})");
            }
        }

        lock (_lock)
        {
            TraceTotals totals = frame.Entry.Trace;
            totals.Finished++;
            totals.Answered += continued ? 0 : 1;
            totals.Changed += changed ? 1 : 0;
            totals.Wrote += wrote ? 1 : 0;
            totals.Returns[result] = totals.Returns.GetValueOrDefault(result) + 1;
        }

        Util.Log.Info(line.ToString());
    }

    /// <summary>What the traced calls of <paramref name="entry"/> did, in one phrase (empty before the first).</summary>
    internal string DescribeTrace(Entry entry)
    {
        lock (_lock)
        {
            TraceTotals totals = entry.Trace;
            if (totals.Started == 0)
            {
                return "";
            }

            string values = string.Join(", ", totals.Returns.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(5).Select(kv => $"0x{kv.Key:X} x{kv.Value}"));
            int unfinished = totals.Started - totals.Finished;
            string open = unfinished > 0 ? $", {unfinished} did not return yet" : "";
            return $"first {totals.Started} calls traced: returned {values}, changed {totals.Changed}, answered itself {totals.Answered}, wrote outputs {totals.Wrote}{open}";
        }
    }
}
