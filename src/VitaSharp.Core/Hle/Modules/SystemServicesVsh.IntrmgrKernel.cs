using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceIntrmgrForDriver (os0:kd/intrmgr.skprx 3.74, read in the module, study only): the table of the 256 interrupt
// lines of the GIC (0..15 software interrupts, 16..255 the devices). A line gets one handler (Register: line, name,
// trigger flags (bits 0 and 4), handler, argument, priority (bits 4..7), target CPUs, options { size 0x14, sub-interrupt
// count, the parent driver's sub-interrupt operations, flags, configuration }); a hook can stand in for the handler and be
// taken back; enable, disable, suspend and resume work on the device lines (16..255) and keep the GIC's enable bit; the
// priority and target are kept per line. A line with sub-interrupts dispatches them through the operations table its
// driver gave (+4 and +8 around a registration, +0xC and +0x10 around a release, +0x14 enable, +0x18 disable, +0x1C
// suspend, +0x20 resume, +0x24 "occurred"); a sub-interrupt is raised by TriggerSubIntr, which calls its handler at
// once (argument, the handler's own argument, the line's priority) and returns the handler's result. A software
// interrupt (TriggerSGI) runs the line's handler (line, argument) on the calling thread here, as an interrupt taken
// right away. SceIntrmgrForKernel sets the hooks around a sub-interrupt handler and around a registration and a
// release, and the service NID 0xC568F145 calls; its pending bits are the GIC's. A device line raised by an emulated
// device in guest time (GuestInterruptScheduler: the Systimer's so far) runs its handler (line, argument) in interrupt
// context on the interrupt thread when it is enabled, else stays pending until it is enabled (then taken)
// or cleared. The lines the firmware's drivers register at boot (the Systimer's twelve) are registered before the first
// call that looks at a line. Interrupt context is per thread (the core taking the interrupt).
public sealed partial class SystemServicesVsh
{
    const int INTR_ERR_IN_INTERRUPT = unchecked((int)0x80027101);
    const int INTR_ERR_CODE = unchecked((int)0x80027102);
    const int INTR_ERR_OPTION = unchecked((int)0x80027103);
    const int INTR_ERR_PRIORITY = unchecked((int)0x80027104);
    const int INTR_ERR_TARGET = unchecked((int)0x80027105);
    const int INTR_ERR_SGI_MODE = unchecked((int)0x80027106);
    const int INTR_ERR_FLAGS = unchecked((int)0x80027107);
    const int INTR_ERR_HANDLER = unchecked((int)0x80027108);
    const int INTR_ERR_REGISTERED = unchecked((int)0x80027109);
    const int INTR_ERR_NOT_REGISTERED = unchecked((int)0x8002710A);
    const int INTR_ERR_NO_SUB_OPS = unchecked((int)0x80020001);

    const int IntrLines = 256;

    /// <summary>One interrupt line: its handler (0: free), the original handler while hooked, the argument, flags,
    /// priority and target CPUs, the GIC enable bit and the sub-interrupt count and operations.</summary>
    struct IntrLine
    {
        public uint Handler;
        public uint Hooked;
        public uint Argument;
        public uint Flags;
        public uint Priority;
        public uint Target;
        public bool Enabled;
        public uint SubCount;
        public uint SubOps;
    }

    /// <summary>A registered sub-interrupt: its handler and argument and how often it was raised.</summary>
    struct IntrSub
    {
        public uint Handler;
        public uint Argument;
        public uint Raised;
    }

    [SnapshotMissing] // states of builds before it: no handler registered
    readonly IntrLine[] _intrLines = new IntrLine[IntrLines];

    [SnapshotMissing] // states of builds before it: no sub-interrupt registered
    readonly Dictionary<uint, IntrSub> _intrSubs = [];

    /// <summary>Interrupt handlers running on this thread (the core taking the interrupt): per thread, since the
    /// dispatcher of the hardware lines runs handlers while other guest threads go on outside interrupt context.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic]
    static int t_intrDepth;

    static uint IntrSubKey(uint line, uint sub) => (line << 16) | sub;

    /// <summary>The target CPU mask of a target word (the mask in bits 0..3, or in bits 16..19), or -1.</summary>
    static int IntrTarget(uint target)
    {
        if (target == 0)
        {
            return -1;
        }

        if ((target & 0xF0000) == 0)
        {
            if ((target & 0xF) != 0)
            {
                return (int)target < 0 ? -1 : (int)(target & 0xFF);
            }

            return (int)((target >> 16) & 0xFF);
        }

        return (target & 0xF) != 0 ? -1 : (int)((target >> 16) & 0xFF);
    }

    /// <summary>NID 0x182EE3E3: 1 while an interrupt handler runs (the callers refuse to block there), else 0.</summary>
    [Hle(Nid = 0x182EE3E3, Library = "SceIntrmgrForDriver")]
    int ksceKernelIsIntrContext()
    {
        return t_intrDepth > 0 ? 1 : 0;
    }

    /// <summary>ksceKernelRegisterIntrHandler(int line, const char *name, int flags, handler, void *argument, int
    /// priority, int target, SceKernelIntrOptParam *option).</summary>
    [Hle(Nid = 0x5C1FEB29, Library = "SceIntrmgrForDriver")]
    int ksceKernelRegisterIntrHandler(uint line, uint name, uint flags, uint handler, uint argument, uint priority, uint target, uint option)
    {
        IntrDevices();
        if (t_intrDepth > 0)
        {
            return INTR_ERR_IN_INTERRUPT;
        }

        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        if ((flags & ~0x11u) != 0)
        {
            return INTR_ERR_FLAGS;
        }

        if (handler == 0)
        {
            return INTR_ERR_HANDLER;
        }

        if ((priority & ~0xF0u) != 0)
        {
            return INTR_ERR_PRIORITY;
        }

        int mask = IntrTarget(target);
        if (mask < 0)
        {
            return INTR_ERR_TARGET;
        }

        uint subCount = 0;
        uint subOps = 0;
        if (option != 0)
        {
            if (GuestMemory.R32(option) != 0x14)
            {
                return INTR_ERR_OPTION;
            }

            if ((GuestMemory.R32(option + 0xC) & 1) != 0 && (GuestMemory.R32(option + 0x10) & ~2u) != 1)
            {
                return INTR_ERR_OPTION;
            }

            subCount = GuestMemory.R32(option + 4);
            subOps = GuestMemory.R32(option + 8);
        }

        if (_intrLines[line].Handler != 0)
        {
            return INTR_ERR_REGISTERED;
        }

        _intrLines[line] = new IntrLine
        {
            Handler = handler,
            Argument = argument,
            Flags = flags,
            Priority = priority,
            Target = (uint)mask,
            SubCount = subCount,
            SubOps = subCount == 0 ? 0 : subOps,
        };
        IntrNotify(_intrOnRegister, line);
        return 0;
    }

    /// <summary>A registration hook told the line, on the calling guest thread.</summary>
    void IntrNotify(uint hook, uint line)
    {
        if (hook != 0 && GuestThread.Current is { } thread)
        {
            thread.CallGuest(hook, line);
        }
    }

    /// <summary>ksceKernelReleaseIntrHandler(int line): the line freed (and disabled), its sub-interrupts dropped.</summary>
    [Hle(Nid = 0xD6009B98, Library = "SceIntrmgrForDriver")]
    int ksceKernelReleaseIntrHandler(uint line)
    {
        IntrDevices();
        if (t_intrDepth > 0)
        {
            return INTR_ERR_IN_INTERRUPT;
        }

        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        if (_intrLines[line].Handler == 0)
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        _intrLines[line] = default;
        foreach (uint key in _intrSubs.Keys.Where(key => key >> 16 == line).ToList())
        {
            _intrSubs.Remove(key);
        }

        IntrNotify(_intrOnRelease, line);
        return 0;
    }

    /// <summary>ksceKernelRegisterIntrHookHandler(int line, handler, void **original): the handler replaced, the
    /// original returned and kept; a NULL handler 0x80027108, a NULL output 0x80020005, hooked already
    /// 0x80027109.</summary>
    [Hle(Nid = 0x99048AEC, Library = "SceIntrmgrForDriver")]
    int ksceKernelRegisterIntrHookHandler(uint line, uint handler, uint original)
    {
        IntrDevices();
        if (t_intrDepth > 0)
        {
            return INTR_ERR_IN_INTERRUPT;
        }

        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        if (handler == 0)
        {
            return INTR_ERR_HANDLER;
        }

        if (original == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        ref IntrLine entry = ref _intrLines[line];
        if (entry.Handler == 0)
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        if (entry.Hooked != 0)
        {
            return INTR_ERR_REGISTERED;
        }

        entry.Hooked = entry.Handler;
        GuestMemory.W32(original, entry.Handler);
        entry.Handler = handler;
        return 0;
    }

    /// <summary>ksceKernelReleaseIntrHookHandler(int line): the original handler back; not hooked 0x8002710A.</summary>
    [Hle(Nid = 0xA0B7F44E, Library = "SceIntrmgrForDriver")]
    int ksceKernelReleaseIntrHookHandler(uint line)
    {
        IntrDevices();
        if (t_intrDepth > 0)
        {
            return INTR_ERR_IN_INTERRUPT;
        }

        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        ref IntrLine entry = ref _intrLines[line];
        if (entry.Handler == 0 || entry.Hooked == 0)
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        entry.Handler = entry.Hooked;
        entry.Hooked = 0;
        return 0;
    }

    static bool IntrDeviceLine(uint line) => line - 16 < IntrLines - 16;

    [Hle(Nid = 0x7117E827, Library = "SceIntrmgrForDriver")]
    int ksceKernelEnableIntr(uint line) => IntrSetEnabled(line, true);

    [Hle(Nid = 0x180435EC, Library = "SceIntrmgrForDriver")]
    int ksceKernelDisableIntr(uint line) => IntrSetEnabled(line, false);

    int IntrSetEnabled(uint line, bool enabled)
    {
        IntrDevices();
        if (!IntrDeviceLine(line))
        {
            return INTR_ERR_CODE;
        }

        if (_intrLines[line].Handler == 0)
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        _intrLines[line].Enabled = enabled;
        IntrTakePending(line);
        return 0;
    }

    /// <summary>ksceKernelSuspendIntr(int line, int *state): whether it was enabled, then disabled.</summary>
    [Hle(Nid = 0x6EC07C56, Library = "SceIntrmgrForDriver")]
    int ksceKernelSuspendIntr(uint line, uint state)
    {
        IntrDevices();
        if (!IntrDeviceLine(line))
        {
            return INTR_ERR_CODE;
        }

        if (state == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        GuestMemory.W32(state, _intrLines[line].Enabled ? 1u : 0u);
        _intrLines[line].Enabled = false;
        return 0;
    }

    /// <summary>ksceKernelResumeIntr(int line, int state): enabled for 1, disabled for 0, else 0x80020005.</summary>
    [Hle(Nid = 0xA4C772AF, Library = "SceIntrmgrForDriver")]
    int ksceKernelResumeIntr(uint line, uint state)
    {
        IntrDevices();
        if (!IntrDeviceLine(line))
        {
            return INTR_ERR_CODE;
        }

        if (state > 1)
        {
            return SceError.INVALID_ARGUMENT;
        }

        _intrLines[line].Enabled = state == 1;
        IntrTakePending(line);
        return 0;
    }

    [Hle(Nid = 0x71020E29, Library = "SceIntrmgrForDriver")]
    int ksceKernelSetIntrPriority(uint line, uint priority)
    {
        IntrDevices();
        if (!IntrDeviceLine(line))
        {
            return INTR_ERR_CODE;
        }

        if (_intrLines[line].Handler == 0)
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        if ((priority & ~0xF0u) != 0)
        {
            return INTR_ERR_PRIORITY;
        }

        _intrLines[line].Priority = priority;
        return 0;
    }

    [Hle(Nid = 0xE427D050, Library = "SceIntrmgrForDriver")]
    int ksceKernelGetIntrPriority(uint line, uint priority)
    {
        IntrDevices();
        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        if (_intrLines[line].Handler == 0)
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        GuestMemory.W32(priority, _intrLines[line].Priority);
        return 0;
    }

    [Hle(Nid = 0x973BACCC, Library = "SceIntrmgrForDriver")]
    int ksceKernelSetIntrTarget(uint line, uint target)
    {
        IntrDevices();
        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        if (_intrLines[line].Handler == 0)
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        int mask = IntrTarget(target);
        if (mask < 0)
        {
            return INTR_ERR_TARGET;
        }

        _intrLines[line].Target = (uint)mask;
        return 0;
    }

    [Hle(Nid = 0x353CFAAE, Library = "SceIntrmgrForDriver")]
    int ksceKernelGetIntrTarget(uint line, uint target)
    {
        IntrDevices();
        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        if (_intrLines[line].Handler == 0)
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        GuestMemory.W32(target, _intrLines[line].Target);
        return 0;
    }

    /// <summary>ksceKernelTriggerSGI(int line, int mode, int target): line 0..15, mode 0 (the target CPUs), 1 (the
    /// others) or 2 (this one), else 0x80027106; the line's handler runs here (line, argument) when registered.</summary>
    [Hle(Nid = 0x29F62500, Library = "SceIntrmgrForDriver")]
    int ksceKernelTriggerSGI(uint line, uint mode, uint target)
    {
        if (line > 0xF)
        {
            return INTR_ERR_CODE;
        }

        if (mode > 2)
        {
            return INTR_ERR_SGI_MODE;
        }

        if (mode == 0 && IntrTarget(target) < 0)
        {
            return INTR_ERR_TARGET;
        }

        IntrLine entry = _intrLines[line];
        if (entry.Handler != 0)
        {
            IntrCall(entry.Handler, line, entry.Argument, 0);
        }

        return 0;
    }

    /// <summary>A handler call in interrupt context on the calling guest thread; its result.</summary>
    uint IntrCall(uint function, uint first, uint second, uint third)
    {
        if (GuestThread.Current is not { } thread)
        {
            return 0;
        }

        t_intrDepth++;
        try
        {
            return thread.CallGuest(function, first, second, third);
        }
        finally
        {
            t_intrDepth--;
        }
    }

    /// <summary>A call of the parent driver's sub-interrupt operation at <paramref name="offset"/>; null when the line
    /// has none.</summary>
    int? IntrSubOperation(uint line, uint offset, uint sub, uint third = 0, uint fourth = 0)
    {
        uint table = _intrLines[line].SubOps;
        uint function = table == 0 ? 0 : GuestMemory.R32(table + offset);
        if (function == 0 || GuestThread.Current is not { } thread)
        {
            return null;
        }

        return (int)thread.CallGuest(function, line, sub, third, fourth);
    }

    /// <summary>ksceKernelRegisterSubIntrHandler(int line, int sub, const char *name, handler, void *argument): the
    /// parent's operations around it (+4 before, +8 after; a failure of either is returned and undoes it).</summary>
    [Hle(Nid = 0x96576C18, Library = "SceIntrmgrForDriver")]
    int ksceKernelRegisterSubIntrHandler(uint line, uint sub, uint name, uint handler, uint argument)
    {
        if (t_intrDepth > 0)
        {
            return INTR_ERR_IN_INTERRUPT;
        }

        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        if (handler == 0)
        {
            return INTR_ERR_HANDLER;
        }

        IntrLine entry = _intrLines[line];
        if (entry.Handler == 0)
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        if (entry.SubOps == 0)
        {
            return INTR_ERR_NO_SUB_OPS;
        }

        if ((int)sub < 0 || sub >= entry.SubCount)
        {
            return INTR_ERR_CODE;
        }

        uint key = IntrSubKey(line, sub);
        if (_intrSubs.ContainsKey(key))
        {
            return INTR_ERR_REGISTERED;
        }

        int before = IntrSubOperation(line, 4, sub, handler, argument) ?? 0;
        if (before < 0)
        {
            return before;
        }

        _intrSubs[key] = new IntrSub { Handler = handler, Argument = argument };
        int after = IntrSubOperation(line, 8, sub, handler, argument) ?? 0;
        if (after < 0)
        {
            _intrSubs.Remove(key);
            return after;
        }

        return 0;
    }

    /// <summary>ksceKernelReleaseSubIntrHandler(int line, int sub): the parent's operations around it (+0xC before,
    /// +0x10 after; a failure of the second restores the handler).</summary>
    [Hle(Nid = 0x7B9657B3, Library = "SceIntrmgrForDriver")]
    int ksceKernelReleaseSubIntrHandler(uint line, uint sub)
    {
        if (t_intrDepth > 0)
        {
            return INTR_ERR_IN_INTERRUPT;
        }

        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        if (_intrLines[line].Handler == 0)
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        if ((int)sub < 0 || sub >= _intrLines[line].SubCount)
        {
            return INTR_ERR_CODE;
        }

        uint key = IntrSubKey(line, sub);
        if (!_intrSubs.TryGetValue(key, out IntrSub registered))
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        int before = IntrSubOperation(line, 0xC, sub) ?? 0;
        if (before < 0)
        {
            return before;
        }

        _intrSubs.Remove(key);
        int after = IntrSubOperation(line, 0x10, sub) ?? 0;
        if (after < 0)
        {
            _intrSubs[key] = registered;
            return after;
        }

        return 0;
    }

    /// <summary>The sub-interrupt entries that hand over to the parent's operation at <paramref name="offset"/>.</summary>
    int IntrSubDispatch(uint line, uint sub, uint offset, uint third = 0, uint fourth = 0)
    {
        if (line >= IntrLines || (int)sub < 0 || sub >= _intrLines[line].SubCount)
        {
            return INTR_ERR_CODE;
        }

        if (_intrLines[line].Handler == 0)
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        return IntrSubOperation(line, offset, sub, third, fourth) ?? INTR_ERR_NOT_REGISTERED;
    }

    [Hle(Nid = 0x901E41D8, Library = "SceIntrmgrForDriver")]
    int ksceKernelEnableSubIntr(uint line, uint sub) => IntrSubDispatch(line, sub, 0x14);

    [Hle(Nid = 0x259C6D9E, Library = "SceIntrmgrForDriver")]
    int ksceKernelDisableSubIntr(uint line, uint sub) => IntrSubDispatch(line, sub, 0x18);

    /// <summary>ksceKernelSuspendSubIntr(int line, int sub, int *state, ...): a NULL state 0x80020001.</summary>
    [Hle(Nid = 0x3A9EA7D1, Library = "SceIntrmgrForDriver")]
    int ksceKernelSuspendSubIntr(uint line, uint sub, uint state, uint extra)
    {
        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        if (state == 0)
        {
            return INTR_ERR_NO_SUB_OPS;
        }

        return IntrSubDispatch(line, sub, 0x1C, state, extra);
    }

    [Hle(Nid = 0x957023D0, Library = "SceIntrmgrForDriver")]
    int ksceKernelResumeSubIntr(uint line, uint sub, uint state, uint extra) => IntrSubDispatch(line, sub, 0x20, state, extra);

    [Hle(Nid = 0x950B864B, Library = "SceIntrmgrForDriver")]
    int ksceKernelIsSubInterruptOccurred(uint line, uint sub) => IntrSubDispatch(line, sub, 0x24);

    /// <summary>ksceKernelTriggerSubIntr(int line, int sub, void *argument): the sub-interrupt's handler runs now
    /// (argument, its own argument, the line's priority); its result, or 0x8002710A without a handler.</summary>
    [Hle(Nid = 0xCC94B294, Library = "SceIntrmgrForDriver")]
    int ksceKernelTriggerSubIntr(uint line, uint sub, uint argument)
    {
        if (line >= IntrLines || (int)sub < 0 || sub >= _intrLines[line].SubCount)
        {
            return INTR_ERR_CODE;
        }

        uint key = IntrSubKey(line, sub);
        if (!_intrSubs.TryGetValue(key, out IntrSub registered))
        {
            return INTR_ERR_NOT_REGISTERED;
        }

        registered.Raised++;
        _intrSubs[key] = registered;
        if (_intrBeforeSub != 0 && GuestThread.Current is { } before)
        {
            before.CallGuest(_intrBeforeSub, 4, line, sub, argument, registered.Argument);
        }

        int result = (int)IntrCall(registered.Handler, argument, registered.Argument, _intrLines[line].Priority & 0xFF);
        if (_intrAfterSub != 0 && GuestThread.Current is { } after)
        {
            after.CallGuest(_intrAfterSub, 3, line, sub, argument);
        }

        return result;
    }

    // ------------------------------------------------------------------ SceIntrmgrForKernel: the hooks and pending bits

    /// <summary>The trace hooks around a sub-interrupt's handler, the hooks told of a registration and a release, and
    /// the service function NID 0xC568F145 forwards to (SceIntrmgrForKernel's setters).</summary>
    [SnapshotMissing] // states of builds before it: no hook set
    uint _intrBeforeSub;

    [SnapshotMissing] // states of builds before it: no hook set
    uint _intrAfterSub;

    [SnapshotMissing] // states of builds before it: no hook set
    uint _intrOnRegister;

    [SnapshotMissing] // states of builds before it: no hook set
    uint _intrOnRelease;

    [SnapshotMissing] // states of builds before it: no service set
    uint _intrService;

    [SnapshotMissing] // states of builds before it: nothing pending
    readonly uint[] _intrPending = new uint[IntrLines / 32];

    /// <summary>NID 0x04DBA732 (before, after): the hooks around a sub-interrupt handler (before: 4, line, sub,
    /// argument, the handler's argument; after: 3, line, sub, argument).</summary>
    [Hle(Nid = 0x04DBA732, Library = "SceIntrmgrForKernel")]
    void ksceKernelSetSubIntrTraceHooks(uint before, uint after)
    {
        _intrBeforeSub = before;
        _intrAfterSub = after;
    }

    /// <summary>NID 0x6F5B8AF2 (onRegister, onRelease): the hooks told the line of a registration and a release.</summary>
    [Hle(Nid = 0x6F5B8AF2, Library = "SceIntrmgrForKernel")]
    void ksceKernelSetIntrRegistrationHooks(uint onRegister, uint onRelease)
    {
        _intrOnRegister = onRegister;
        _intrOnRelease = onRelease;
    }

    /// <summary>NID 0xE3B670BF (function): the service NID 0xC568F145 calls.</summary>
    [Hle(Nid = 0xE3B670BF, Library = "SceIntrmgrForKernel")]
    void ksceKernelSetIntrService(uint function)
    {
        _intrService = function;
    }

    /// <summary>NID 0xC568F145 (value): the service called with (0x100000, 4, &amp;value); nothing without one.</summary>
    [Hle(Nid = 0xC568F145, Library = "SceIntrmgrForDriver")]
    void ksceKernelCallIntrService(uint value)
    {
        if (_intrService == 0 || GuestThread.Current is not { } thread)
        {
            return;
        }

        uint word = Mem.Alloc(4, "SceIntrmgrService", MemoryRegion.Kernel, 4);
        try
        {
            GuestMemory.W32(word, value);
            thread.CallGuest(_intrService, 0x100000, 4, word);
        }
        finally
        {
            Mem.Free(word);
        }
    }

    /// <summary>NID 0x2CC44FF3 (line): the GIC's pending bit of the line (nothing raises one here); a line past 255
    /// 0x80027102.</summary>
    [Hle(Nid = 0x2CC44FF3, Library = "SceIntrmgrForKernel")]
    int ksceKernelIsIntrPending(uint line)
    {
        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        return (int)((_intrPending[line >> 5] >> (int)(line & 31)) & 1);
    }

    /// <summary>NID 0xC63C96BC (line): the pending bit cleared; 0.</summary>
    [Hle(Nid = 0xC63C96BC, Library = "SceIntrmgrForKernel")]
    int ksceKernelClearIntrPending(uint line)
    {
        if (line >= IntrLines)
        {
            return INTR_ERR_CODE;
        }

        _intrPending[line >> 5] &= ~(1u << (int)(line & 31));
        return 0;
    }

    // ------------------------------------------------------------------ the hardware lines in guest time

    /// <summary>The lines the firmware's own drivers register at their module_start (the Systimer's twelve), made
    /// before the first call that looks at a line, as they are there before any other driver runs.</summary>
    [SnapshotMissing] // states of builds before it: the device lines not registered yet
    bool _intrDevicesRegistered;

    /// <summary>The device lines' timing: made with the first line a device arms (none: nothing runs).</summary>
    [SnapshotIgnore]
    GuestInterruptScheduler? _intrScheduler;

    [SnapshotIgnore]
    HleGuestStubs? _intrStubs;

    GuestInterruptScheduler IntrScheduler => _intrScheduler ??= new GuestInterruptScheduler(Emu, IntrExpire);

    HleGuestStubs IntrStubs => _intrStubs ??= new HleGuestStubs(this, "SceIntrmgr");

    /// <summary>The device lines registered by the firmware's drivers at boot (see <see cref="_intrDevicesRegistered"/>).</summary>
    void IntrDevices()
    {
        if (_intrDevicesRegistered)
        {
            return;
        }

        _intrDevicesRegistered = true;
        SystimerRegisterLines();
    }

    /// <summary>A line registered by an HLE driver: its handler a guest-callable stub of <paramref name="body"/>, so a
    /// hook gets a real original to call.</summary>
    void IntrRegisterDevice(uint line, string name, uint argument, uint priority, uint target, Action<HleContext> body)
    {
        _intrLines[line] = new IntrLine
        {
            Handler = IntrStubs.Host(name, body),
            Argument = argument,
            Priority = priority,
            Target = target,
        };
    }

    /// <summary>The scheduler's call when a device line's time came: the device re-arms and raises it.</summary>
    void IntrExpire(uint line, ulong raisedUs)
    {
        if (SystimerOfLine(line) >= 0)
        {
            SystimerExpire(line, raisedUs);
        }
    }

    /// <summary>A device raises <paramref name="line"/>, as the GIC takes it: an enabled line with a handler runs it now
    /// in interrupt context on the calling thread (the core taking the interrupt) with (line, argument); otherwise the
    /// line stays pending until it is enabled or the pending bit is cleared.</summary>
    internal void IntrRaise(uint line)
    {
        if (line >= IntrLines)
        {
            return;
        }

        IntrLine entry = _intrLines[line];
        if (entry.Handler == 0 || !entry.Enabled)
        {
            lock (_intrPending)
            {
                _intrPending[line >> 5] |= 1u << (int)(line & 31);
            }

            return;
        }

        IntrCall(entry.Handler, line, entry.Argument, 0);
    }

    /// <summary>A pending line that is now enabled with a handler is taken (the GIC signals it once enabled).</summary>
    void IntrTakePending(uint line)
    {
        IntrLine entry = _intrLines[line];
        if (entry.Handler == 0 || !entry.Enabled)
        {
            return;
        }

        uint bit = 1u << (int)(line & 31);
        lock (_intrPending)
        {
            if ((_intrPending[line >> 5] & bit) == 0)
            {
                return;
            }

            _intrPending[line >> 5] &= ~bit;
        }

        IntrCall(entry.Handler, line, entry.Argument, 0);
    }

    /// <summary>Tests: the device lines fire only by <see cref="IntrDispatchDue"/> (no dispatcher thread).</summary>
    internal void IntrDispatchByHand() => IntrScheduler.ManualDispatch = true;

    /// <summary>Tests: fires the device lines due at guest microsecond <paramref name="nowUs"/> on the calling thread.</summary>
    internal int IntrDispatchDue(ulong nowUs) => IntrScheduler.DispatchDue(nowUs);

    /// <summary>Tests: the guest microsecond a device line fires at (null: not armed), and whether the dispatcher
    /// thread exists.</summary>
    internal (ulong? Deadline, bool Dispatcher) IntrSchedule(uint line)
    {
        return (_intrScheduler?.Deadline(line), _intrScheduler?.DispatcherStarted ?? false);
    }

    /// <summary>The state of a line, for the tests.</summary>
    internal (uint Handler, bool Enabled, uint Priority, uint Target) IntrState(uint line)
    {
        IntrDevices();
        IntrLine entry = _intrLines[line];
        return (entry.Handler, entry.Enabled, entry.Priority, entry.Target);
    }
}
