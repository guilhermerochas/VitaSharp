using VitaSharp.Core.Kernel;
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
// microsecond divided by the prescaler + 1 (the source clocks are the hardware's). The setters refuse to run in an
// interrupt handler (0x80027101). Each timer has its interrupt line, registered at boot ("SceSystimerWordTimer<n>" on
// 0x80 + n, "SceSystimerLongrangeTimer<n>" on 0x88 + n, priority 0x80, all four cores), enabled by Alloc, disabled by
// Free and aimed at the handler's cores by SetHandler. A timer with an interval counts to it and starts again from 0,
// raising its line each time; the line's handler (the module's, at +0 of its code) adds the interval to the offset and
// calls the driver's handler (id, argument). A word timer without an interval raises its line when it wraps, and the
// handler adds 2^32 to the offset, so the count with the offset keeps going past 32 bits; a long-range timer without
// an interval raises nothing. The raises are timed in guest time by the interrupt scheduler (GuestInterruptScheduler)
// and delivered through SceIntrmgr. A state load drops the running session's arms and arms each counting timer's
// line again for its next raise after the loaded guest time (the timers' saved settings and start times decide it).
public sealed partial class SystemServicesVsh : ISnapshotTimers
{
    /// <summary>A state load: the device lines armed in the running session no longer fire.</summary>
    void ISnapshotTimers.ClearTimers()
    {
        _intrScheduler?.DisarmAll();
    }

    /// <summary>A state load: every counting timer of the loaded state raises its line at its time again.</summary>
    void ISnapshotTimers.RearmTimers()
    {
        lock (_systimers)
        {
            for (int index = 0; index < SystimerTimers; index++)
            {
                SystimerArm(index);
            }
        }
    }

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

    const uint SystimerWordLine = 0x80, SystimerLongRangeLine = 0x88;

    const ulong SystimerWordWrap = 1UL << 32;

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

    /// <summary>The interrupt line of the timer at table index <paramref name="index"/>.</summary>
    static uint SystimerLine(int index)
    {
        return index < SystimerLongRange ? SystimerLongRangeLine + (uint)index : SystimerWordLine + (uint)(index - SystimerLongRange);
    }

    /// <summary>The timer index of an interrupt line, or -1.</summary>
    static int SystimerOfLine(uint line)
    {
        if (line - SystimerLongRangeLine < SystimerLongRange)
        {
            return (int)(line - SystimerLongRangeLine);
        }

        return line - SystimerWordLine < SystimerWord ? SystimerLongRange + (int)(line - SystimerWordLine) : -1;
    }

    /// <summary>The timer's id at table index <paramref name="index"/>.</summary>
    static uint SystimerId(int index)
    {
        return index < SystimerLongRange ? 0x10000u | (uint)index : 0x20000u | (uint)(index - SystimerLongRange);
    }

    /// <summary>The twelve lines as the module's module_start registers them (flags 0, priority 0x80, target 0xF),
    /// their argument the timer's index.</summary>
    void SystimerRegisterLines()
    {
        for (int index = 0; index < SystimerTimers; index++)
        {
            string name = index < SystimerLongRange
                ? "SceSystimerLongrangeTimer" + index
                : "SceSystimerWordTimer" + (index - SystimerLongRange);
            IntrRegisterDevice(SystimerLine(index), name, (uint)index, 0x80, 0xF, SystimerInterruptStub);
        }
    }

    /// <summary>The module's line handler (line, argument = the timer's index).</summary>
    void SystimerInterruptStub(HleContext context)
    {
        context.Return(SystimerInterrupt(context.Gpr(1)));
    }

    /// <summary>The line handler: with an interval, the offset grows by it and the driver's handler is called (id,
    /// argument); a word timer without one adds a wrap (2^32) to the offset; -1.</summary>
    int SystimerInterrupt(uint index)
    {
        if (index >= SystimerTimers)
        {
            return -1;
        }

        uint handler;
        uint argument;
        lock (_systimers)
        {
            ref SystimerTimer timer = ref _systimers[index];
            if (timer.Interval == 0)
            {
                if (index >= SystimerLongRange)
                {
                    timer.Offset += SystimerWordWrap;
                }

                return -1;
            }

            timer.Offset += timer.Interval;
            handler = timer.Handler;
            argument = timer.HandlerArgument;
        }

        if (handler != 0 && GuestThread.Current is { } thread)
        {
            thread.CallGuest(handler, SystimerId((int)index), argument);
        }

        return -1;
    }

    /// <summary>The period the counter runs to before starting again (ticks): the interval (a word timer's register
    /// holds its low 32 bits), a word timer's wrap without one, 0 for none.</summary>
    static ulong SystimerPeriod(int index, in SystimerTimer timer)
    {
        if (index >= SystimerLongRange)
        {
            uint interval = (uint)timer.Interval;
            return interval == 0 ? SystimerWordWrap : interval;
        }

        return timer.Interval;
    }

    /// <summary>The guest microsecond of the timer's next raise after <paramref name="afterUs"/>, or null (not
    /// counting, no period, or a counter set past its period).</summary>
    ulong? SystimerNextRaise(int index, ulong afterUs)
    {
        ref SystimerTimer timer = ref _systimers[index];
        ulong period = SystimerPeriod(index, timer);
        if (!timer.Counting || period == 0 || timer.CountBase >= period)
        {
            return null;
        }

        ulong tickUs = timer.Prescaler + 1UL;
        ulong first = timer.StartedUs + (period - timer.CountBase) * tickUs;
        if (afterUs < first)
        {
            return first;
        }

        ulong periods = (afterUs - first) / (period * tickUs) + 1;
        return first + periods * period * tickUs;
    }

    /// <summary>The timer's line armed for its next raise (or disarmed).</summary>
    void SystimerArm(int index)
    {
        uint line = SystimerLine(index);
        ulong? next = SystimerNextRaise(index, Emu.Kernel.ProcessTimeUs);
        if (next is { } deadline)
        {
            IntrScheduler.Arm(line, deadline);
        }
        else
        {
            _intrScheduler?.Disarm(line);
        }
    }

    /// <summary>The scheduler's call at the time of a raise (<paramref name="raisedUs"/>): the next one armed (a later
    /// period already past fires right after), then the line raised.</summary>
    void SystimerExpire(uint line, ulong raisedUs)
    {
        int index = SystimerOfLine(line);
        lock (_systimers)
        {
            if (!_systimers[index].Counting)
            {
                return;
            }

            if (SystimerNextRaise(index, raisedUs) is { } next)
            {
                IntrScheduler.Arm(line, next);
            }
        }

        IntrRaise(line);
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

    /// <summary>The counter now: the base plus the ticks of guest time since it started, starting again from 0 at the
    /// period (the interval; a word timer's wrap at 32 bits without one).</summary>
    ulong SystimerCount(int index)
    {
        ref SystimerTimer timer = ref _systimers[index];
        ulong count = timer.CountBase;
        if (timer.Counting)
        {
            count += (Emu.Kernel.ProcessTimeUs - timer.StartedUs) / (timer.Prescaler + 1);
        }

        ulong period = SystimerPeriod(index, timer);
        if (period != 0 && timer.CountBase < period)
        {
            return count % period;
        }

        return index >= SystimerLongRange ? (uint)count : count;
    }

    /// <summary>0x80027101 inside an interrupt handler (the setters' first check), else 0.</summary>
    static int SystimerInInterrupt()
    {
        return t_intrDepth > 0 ? INTR_ERR_IN_INTERRUPT : 0;
    }

    /// <summary>Sets the counter to <paramref name="value"/> (a counting timer keeps counting from it).</summary>
    void SystimerLoad(int index, ulong value)
    {
        _systimers[index].CountBase = value;
        _systimers[index].StartedUs = Emu.Kernel.ProcessTimeUs;
    }

    /// <summary>The checks of a setting: outside an interrupt handler (0x80027101; StartCount does not check it), the
    /// timer allocated (0x80027302) and not counting (0x80027303).</summary>
    int SystimerSettable(uint id, out int index, bool inInterruptRefused = true)
    {
        index = -1;
        int inInterrupt = inInterruptRefused ? SystimerInInterrupt() : 0;
        if (inInterrupt != 0)
        {
            return inInterrupt;
        }

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
        int inInterrupt = SystimerInInterrupt();
        if (inInterrupt != 0)
        {
            return inInterrupt;
        }

        IntrDevices();
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

        lock (_systimers)
        {
            for (int n = 0; n < count; n++)
            {
                if (!_systimers[first + n].Allocated)
                {
                    _systimers[first + n] = new SystimerTimer { Allocated = true };
                    uint line = SystimerLine(first + n);
                    _intrLines[line].Enabled = true;
                    _intrLines[line].Target = 0xF;
                    return (int)(idBase | (uint)n);
                }
            }
        }

        return SYSTIMER_ERR_NO_TIMER;
    }

    /// <summary>ksceKernelSysTimerFree(SceSysTimerId id): the line disabled, the timer stopped and cleared; not
    /// allocated 0x80027302.</summary>
    [Hle(Nid = 0x8358611F, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerFree(uint id)
    {
        int inInterrupt = SystimerInInterrupt();
        if (inInterrupt != 0)
        {
            return inInterrupt;
        }

        IntrDevices();
        int table = SystimerIndex(id);
        if (table >= 0)
        {
            _intrLines[SystimerLine(table)].Enabled = false;
        }

        lock (_systimers)
        {
            int index = SystimerAllocated(id);
            if (index < 0)
            {
                return SYSTIMER_ERR_NOT_ALLOCATED;
            }

            _systimers[index] = default;
            SystimerArm(index);
        }

        return 0;
    }

    /// <summary>ksceKernelSysTimerSetClockSource(id, int source, int prescaler): source 0, 1, 3..13, 16..21 (else
    /// 0x80020005); 0.</summary>
    [Hle(Nid = 0x0450933B, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerSetClockSource(uint id, uint source, uint prescaler)
    {
        int inInterrupt = SystimerInInterrupt();
        if (inInterrupt != 0)
        {
            return inInterrupt;
        }

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
        int inInterrupt = SystimerInInterrupt();
        if (inInterrupt != 0)
        {
            return inInterrupt;
        }

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
        if (handler != 0)
        {
            IntrDevices();
            _intrLines[SystimerLine(index)].Target = (uint)IntrTarget(highMask ? mask >> 16 : mask);
        }

        return 0;
    }

    /// <summary>ksceKernelSysTimerStartCount(id): starts counting from the counter's value; 0.</summary>
    [Hle(Nid = 0x59EBD22D, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerStartCount(uint id)
    {
        int check = SystimerSettable(id, out int index, inInterruptRefused: false);
        if (check != 0)
        {
            return check;
        }

        lock (_systimers)
        {
            _systimers[index].StartedUs = Emu.Kernel.ProcessTimeUs;
            _systimers[index].Counting = true;
            SystimerArm(index);
        }

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

        lock (_systimers)
        {
            _systimers[index].CountBase = SystimerCount(index);
            _systimers[index].Counting = false;
            SystimerArm(index);
        }

        return 0;
    }

    /// <summary>ksceKernelSysTimerResetCount(id): the counter and the offset 0 (a counting timer keeps counting); 0.</summary>
    [Hle(Nid = 0x7DEBA6A5, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerResetCount(uint id)
    {
        int inInterrupt = SystimerInInterrupt();
        if (inInterrupt != 0)
        {
            return inInterrupt;
        }

        int index = SystimerAllocated(id);
        if (index < 0)
        {
            return SYSTIMER_ERR_NOT_ALLOCATED;
        }

        lock (_systimers)
        {
            SystimerLoad(index, 0);
            _systimers[index].Offset = 0;
            SystimerArm(index);
        }

        return 0;
    }

    /// <summary>ksceKernelSysTimerSetCount(id, SceUInt64 count): the counter set to the value, which is also added to
    /// the offset; 0.</summary>
    [Hle(Nid = 0x1B14DFD6, Library = "SceSystimerForDriver")]
    int ksceKernelSysTimerSetCount(uint id, uint alignment, uint countLow, uint countHigh)
    {
        int inInterrupt = SystimerInInterrupt();
        if (inInterrupt != 0)
        {
            return inInterrupt;
        }

        int index = SystimerAllocated(id);
        if (index < 0)
        {
            return SYSTIMER_ERR_NOT_ALLOCATED;
        }

        ulong value = ((ulong)countHigh << 32) | countLow;
        lock (_systimers)
        {
            SystimerLoad(index, index >= SystimerLongRange ? (uint)value : value);
            _systimers[index].Offset += value;
            SystimerArm(index);
        }

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

        ulong value;
        lock (_systimers)
        {
            value = SystimerCount(index);
        }

        GuestMemory.Write(count, value);
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

        ulong value;
        lock (_systimers)
        {
            value = SystimerCount(index) + _systimers[index].Offset;
        }

        GuestMemory.Write(count, value);
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
        int inInterrupt = SystimerInInterrupt();
        if (inInterrupt != 0)
        {
            return inInterrupt;
        }

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
        int inInterrupt = SystimerInInterrupt();
        if (inInterrupt != 0)
        {
            return inInterrupt;
        }

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
