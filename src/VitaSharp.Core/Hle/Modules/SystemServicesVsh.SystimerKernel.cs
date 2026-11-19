using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceSystimerForDriver (os0:kd/systimer.skprx 3.74, read in the module, study only): the hardware timers, five
// 64-bit long-range timers (ids 0x10000 + n) and seven 32-bit word timers (ids 0x20000 + n). Alloc(kind 1 or 2, else
// 0x80020005) takes the first free one (none: 0x80027301) and returns its id; every other call on a timer that is not
// allocated is 0x80027302, and the settings (clock source, interval, handler, the two modes) are refused while it
// counts (0x80027303); stopping one that does not count is 0x80027304. The clock source takes 0, 1, 3..13, 16..21
// (else 0x80020005) and a prescaler; the handler needs an interrupt mask (0: 0x80027105). The counter counts while
// started; ResetCount and SetCount restart it from 0 or a value (SetCount also adds the value to the timer's offset,
// which GetCountWithOffset adds to the counter). The emulated counters run on guest time: one tick per guest
// microsecond divided by the prescaler + 1 (the source clocks are the hardware's). The interrupts the timers raise at
// their interval go through SceIntrmgr, which the emulated kernel does not deliver yet: the handler is kept, not called.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSystimer: a kind or setting out of range (0x80020005).</summary>
    const int SYSTIMER_ERR_INVALID_ARGUMENT = unchecked((int)0x80020005);

    /// <summary>SceSystimer: no free timer of the kind (0x80027301).</summary>
    const int SYSTIMER_ERR_NO_TIMER = unchecked((int)0x80027301);

    /// <summary>SceSystimer: the timer is not allocated (0x80027302).</summary>
    const int SYSTIMER_ERR_NOT_ALLOCATED = unchecked((int)0x80027302);

    /// <summary>SceSystimer: the timer is counting (0x80027303).</summary>
    const int SYSTIMER_ERR_COUNTING = unchecked((int)0x80027303);

    /// <summary>SceSystimer: the timer is not counting (0x80027304).</summary>
    const int SYSTIMER_ERR_NOT_COUNTING = unchecked((int)0x80027304);

    /// <summary>SceSystimer: a handler without an interrupt mask (0x80027105).</summary>
    const int SYSTIMER_ERR_NO_MASK = unchecked((int)0x80027105);

    const int SystimerLongRange = 5, SystimerWord = 7, SystimerTimers = SystimerLongRange + SystimerWord;

    const uint SystimerKindLongRange = 1, SystimerKindWord = 2;

    [SnapshotMissing] // states of builds before it: no timer allocated
    readonly SystimerTimer[] _systimers = new SystimerTimer[SystimerTimers];

    /// <summary>One timer's state: allocated, counting, its settings, the counter's base value and the guest time it
    /// started counting from, the offset SetCount adds to.</summary>
    struct SystimerTimer
    {
        public bool Allocated;
        public bool Counting;
        public uint ClockSource;
        public uint Prescaler;
        public ulong Interval;
        public uint Handler;
        public uint HandlerArgument;
        public uint ModeA;
        public uint ModeB;
        public ulong CountBase;
        public ulong StartedUs;
        public ulong Offset;
    }

    /// <summary>A timer by id (0x10000 + 0..4, 0x20000 + 0..6), as the index into the table; -1 for no such timer.</summary>
    static int SystimerIndex(uint id)
    {
        uint index = id & 0xFFFF;
        uint kind = id >> 16;
        if (kind == SystimerKindLongRange)
        {
            return index < SystimerLongRange ? (int)index : -1;
        }

        return index < SystimerWord ? SystimerLongRange + (int)index : -1;
    }

    /// <summary>The timer of an allocated id, or null.</summary>
    int SystimerAllocated(uint id)
    {
        int index = SystimerIndex(id);
        return index >= 0 && _systimers[index].Allocated ? index : -1;
    }

    /// <summary>The counter now: the base plus the ticks of guest time since it started (word timers wrap at 32 bits).</summary>
    ulong SystimerCount(int index)
    {
        ref SystimerTimer timer = ref _systimers[index];
        ulong count = timer.CountBase;
        if (timer.Counting)
        {
            count += (Emu.Kernel.ProcessTimeUs - timer.StartedUs) / (timer.Prescaler + 1);
        }

        return index >= SystimerLongRange ? (uint)count : count;
    }

    /// <summary>Sets the counter to <paramref name="value"/> (a counting timer keeps counting from it).</summary>
    void SystimerLoad(int index, ulong value)
    {
        _systimers[index].CountBase = value;
        _systimers[index].StartedUs = Emu.Kernel.ProcessTimeUs;
    }

    /// <summary>The checks of a setting: the timer allocated (0x80027302) and not counting (0x80027303).</summary>
    int SystimerSettable(uint id, out int index)
    {
        index = SystimerAllocated(id);
        if (index < 0)
        {
            return SYSTIMER_ERR_NOT_ALLOCATED;
        }

        return _systimers[index].Counting ? SYSTIMER_ERR_COUNTING : 0;
    }

    /// <summary>The state of a timer, for the tests.</summary>
    internal (bool Allocated, bool Counting, ulong Interval, uint Handler, uint Source, uint Prescaler) SystimerState(
        uint id)
    {
        int index = SystimerIndex(id);
        SystimerTimer timer = _systimers[index];
        return (timer.Allocated, timer.Counting, timer.Interval, timer.Handler, timer.ClockSource, timer.Prescaler);
    }

    /// <summary>ksceKernelSysTimerAlloc(int kind): 1 long-range, 2 word (else 0x80020005); the first free timer's id,
    /// none free 0x80027301.</summary>
    [Hle(Nid = 0xE2B9D8E9, Library = "SceSystimerForDriver", ReturnsValue = true)]
    int ksceKernelSysTimerAlloc(uint kind)
    {
        int first;
        int count;
        uint idBase;
        if (kind == SystimerKindLongRange)
        {
            (first, count, idBase) = (0, SystimerLongRange, 0x10000);
        }
        else if (kind == SystimerKindWord)
        {
            (first, count, idBase) = (SystimerLongRange, SystimerWord, 0x20000);
        }
        else
        {
            return SYSTIMER_ERR_INVALID_ARGUMENT;
        }

        for (int n = 0; n < count; n++)
        {
            if (!_systimers[first + n].Allocated)
            {
                _systimers[first + n] = new SystimerTimer { Allocated = true };
                return (int)(idBase | (uint)n);
            }
        }

        return SYSTIMER_ERR_NO_TIMER;
    }

    /// <summary>ksceKernelSysTimerFree(SceSysTimerId id): stops and clears the timer; not allocated 0x80027302.</summary>
    [Hle(Nid = 0x8358611F, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerFree(uint id)
    {
        int index = SystimerAllocated(id);
        if (index < 0)
        {
            return SYSTIMER_ERR_NOT_ALLOCATED;
        }

        _systimers[index] = default;
        return 0;
    }

    /// <summary>ksceKernelSysTimerSetClockSource(id, int source, int prescaler): source 0, 1, 3..13, 16..21 (else
    /// 0x80020005); 0.</summary>
    [Hle(Nid = 0x0450933B, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerSetClockSource(uint id, uint source, uint prescaler)
    {
        if (source is 2 or 0xE or 0xF || source > 0x15)
        {
            return SYSTIMER_ERR_INVALID_ARGUMENT;
        }

        int check = SystimerSettable(id, out int index);
        if (check != 0)
        {
            return check;
        }

        _systimers[index].ClockSource = source;
        _systimers[index].Prescaler = prescaler & 0xFF;
        return 0;
    }

    /// <summary>ksceKernelSysTimerSetInterval(id, SceUInt64 interval): kept (a word timer's low 32 bits count); 0.</summary>
    [Hle(Nid = 0xA73351C1, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerSetInterval(uint id, uint alignment, uint intervalLow, uint intervalHigh)
    {
        int check = SystimerSettable(id, out int index);
        if (check != 0)
        {
            return check;
        }

        _systimers[index].Interval = ((ulong)intervalHigh << 32) | intervalLow;
        return 0;
    }

    /// <summary>ksceKernelSysTimerSetHandler(id, handler, uint mask, void *argument): no mask, or a mask with both its
    /// low nibble and bits 16..19 (or a negative one) 0x80027105; a NULL handler clears the argument; 0.</summary>
    [Hle(Nid = 0xCCCD1EE3, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerSetHandler(uint id, uint handler, uint mask, uint argument)
    {
        bool highMask = (mask & 0xF0000) != 0;
        bool lowMask = (mask & 0xF) != 0;
        if (mask == 0 || (highMask && lowMask) || (!highMask && lowMask && (int)mask < 0))
        {
            return SYSTIMER_ERR_NO_MASK;
        }

        int check = SystimerSettable(id, out int index);
        if (check != 0)
        {
            return check;
        }

        _systimers[index].Handler = handler;
        _systimers[index].HandlerArgument = handler == 0 ? 0 : argument;
        return 0;
    }

    /// <summary>ksceKernelSysTimerStartCount(id): starts counting from the counter's value; 0.</summary>
    [Hle(Nid = 0x59EBD22D, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerStartCount(uint id)
    {
        int check = SystimerSettable(id, out int index);
        if (check != 0)
        {
            return check;
        }

        _systimers[index].StartedUs = Emu.Kernel.ProcessTimeUs;
        _systimers[index].Counting = true;
        return 0;
    }

    /// <summary>ksceKernelSysTimerStopCount(id): not counting 0x80027304; the counter keeps its value; 0.</summary>
    [Hle(Nid = 0xBF8D42B0, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerStopCount(uint id)
    {
        int index = SystimerIndex(id);
        if (index < 0 || !_systimers[index].Counting)
        {
            return SYSTIMER_ERR_NOT_COUNTING;
        }

        _systimers[index].CountBase = SystimerCount(index);
        _systimers[index].Counting = false;
        return 0;
    }

    /// <summary>ksceKernelSysTimerResetCount(id): the counter and the offset 0 (a counting timer keeps counting); 0.</summary>
    [Hle(Nid = 0x7DEBA6A5, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerResetCount(uint id)
    {
        int index = SystimerAllocated(id);
        if (index < 0)
        {
            return SYSTIMER_ERR_NOT_ALLOCATED;
        }

        SystimerLoad(index, 0);
        _systimers[index].Offset = 0;
        return 0;
    }

    /// <summary>ksceKernelSysTimerSetCount(id, SceUInt64 count): the counter set to the value, which is also added to
    /// the offset; 0.</summary>
    [Hle(Nid = 0x1B14DFD6, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerSetCount(uint id, uint alignment, uint countLow, uint countHigh)
    {
        int index = SystimerAllocated(id);
        if (index < 0)
        {
            return SYSTIMER_ERR_NOT_ALLOCATED;
        }

        ulong value = ((ulong)countHigh << 32) | countLow;
        SystimerLoad(index, index >= SystimerLongRange ? (uint)value : value);
        _systimers[index].Offset += value;
        return 0;
    }

    /// <summary>ksceKernelSysTimerGetCount(id, SceUInt64 *count): the counter (a word timer's high word 0); 0.</summary>
    [Hle(Nid = 0x963F7C0D, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerGetCount(uint id, uint count)
    {
        int index = SystimerAllocated(id);
        if (index < 0)
        {
            return SYSTIMER_ERR_NOT_ALLOCATED;
        }

        GuestMemory.Write(count, SystimerCount(index));
        return 0;
    }

    /// <summary>ksceKernelSysTimerGetCountWithOffset(id, SceUInt64 *count): the counter plus the offset; 0.</summary>
    [Hle(Nid = 0x85AAE0DD, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerGetCountWithOffset(uint id, uint count)
    {
        int index = SystimerAllocated(id);
        if (index < 0)
        {
            return SYSTIMER_ERR_NOT_ALLOCATED;
        }

        GuestMemory.Write(count, SystimerCount(index) + _systimers[index].Offset);
        return 0;
    }

    /// <summary>ksceKernelSysTimerGetCapture(id, SceUInt64 *value): the timer's capture register (nothing captured on
    /// the emulated timers: 0); 0.</summary>
    [Hle(Nid = 0xFD5F1742, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerGetCapture(uint id, uint value)
    {
        int index = SystimerAllocated(id);
        if (index < 0)
        {
            return SYSTIMER_ERR_NOT_ALLOCATED;
        }

        GuestMemory.Write(value, 0UL);
        return 0;
    }

    /// <summary>ksceKernelSysTimerSetCaptureMode(id, int mode, int edge): mode 0..13 (else 0x80020005); kept; 0.</summary>
    [Hle(Nid = 0x904DF737, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerSetCaptureMode(uint id, uint mode, uint edge)
    {
        if (mode >= 0xE)
        {
            return SYSTIMER_ERR_INVALID_ARGUMENT;
        }

        int check = SystimerSettable(id, out int index);
        if (check != 0)
        {
            return check;
        }

        _systimers[index].ModeA = mode | ((edge & 1) << 8);
        return 0;
    }

    /// <summary>ksceKernelSysTimerSetOutputMode(id, int mode): mode 0..13 (else 0x80020005); kept; 0.</summary>
    [Hle(Nid = 0xC34D7940, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerSetOutputMode(uint id, uint mode)
    {
        if (mode >= 0xE)
        {
            return SYSTIMER_ERR_INVALID_ARGUMENT;
        }

        int check = SystimerSettable(id, out int index);
        if (check != 0)
        {
            return check;
        }

        _systimers[index].ModeB = mode;
        return 0;
    }
}
