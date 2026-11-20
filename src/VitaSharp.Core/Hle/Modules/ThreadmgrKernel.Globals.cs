using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The thread manager's own data words other kernel modules set and read through SceThreadmgrForDriver and
/// SceThreadmgrForKernel, as the FW 3.74 threadmgr.skprx keeps them (decompiled, study only). The firmware holds them
/// in one block it allocates at boot (its global data pointer, which NID_19440C83 returns); VitaSharp allocates a block
/// of the same layout in kernel memory on first use, so every word sits at the firmware's offset:
/// <list type="bullet">
/// <item>+0x2390 a UID class processmgr hands over at boot (NID_59D1D5B0);</item>
/// <item>+0x239C two tables of eight debugger event handlers (NID_6C2E3A49 registers one, NID_47F26712 takes it out)
/// and +0x23DC three more (NID_1378F6EF, NID_0D0644DA);</item>
/// <item>+0x23E8/+0x23EC the process event handler the application manager registers and its argument
/// (NID_5311CFB5);</item>
/// <item>+0x2434 a flag the process manager raises and lowers (NID_1394E856, NID_5D270A94);</item>
/// <item>+0x28A8/+0x28AC two words read back by NID_60798313 and NID_A7E5C83D.</item>
/// </list>
/// The firmware calls the handlers from its scheduler and its process worker thread; VitaSharp's thread manager is not
/// the firmware's (LLE) one, so they are kept and answered exactly as registered but never called. The nine scheduler
/// probe hooks (the words after the data pointer, set by the ForKernel setters) are kept the same way.
/// </summary>
public sealed partial class ThreadmgrKernel
{
    /// <summary>The size of the firmware's thread manager data block (the highest word read is +0x28AC).</summary>
    const uint GlobalsSize = 0x2900;

    const uint GlobalsUidClass = 0x2390, GlobalsDebugHandlers = 0x239C, GlobalsEventHandlers = 0x23DC,
        GlobalsProcessHandler = 0x23E8, GlobalsProcessHandlerArgument = 0x23EC, GlobalsProcessFlag = 0x2434,
        GlobalsWord28A8 = 0x28A8, GlobalsWord28AC = 0x28AC;

    /// <summary>The thread manager's answer when a handler slot already holds a handler.</summary>
    const int HandlerSlotBusy = unchecked((int)0x80028003);

    /// <summary>The thread manager's data block in kernel memory (0 until first used).</summary>
    [SnapshotMissing] // states of builds before it: no call had used it
    uint _globals;

    /// <summary>The scheduler probe hooks after the firmware's data pointer (its words 0x04 to 0x24), by index.</summary>
    [SnapshotMissing] // states of builds before it: no hook had been set
    readonly uint[] _probeHooks = new uint[9];

    [SnapshotIgnore]
    readonly object _globalsLock = new();

    /// <summary>The data block, allocated (zeroed) in kernel memory on first use.</summary>
    uint Globals()
    {
        lock (_globalsLock)
        {
            if (_globals == 0)
            {
                using (ProcessScope.Enter(KernelSpace.KernelPid))
                {
                    _globals = Mem.Alloc(GlobalsSize, "kernel:SceKernelThreadMgr", MemoryRegion.Kernel);
                }

                GuestMemory.Fill(_globals, 0, GlobalsSize);
            }

            return _globals;
        }
    }

    /// <summary>NID_19440C83(): the thread manager's data block.</summary>
    [Hle(Nid = 0x19440C83, Library = "SceThreadmgrForKernel", ReturnsValue = true)]
    uint NID_19440C83() => Globals();

    /// <summary>NID_59D1D5B0(cls): keeps the UID class processmgr gives (+0x2390); 0.</summary>
    [Hle(Nid = 0x59D1D5B0, Library = "SceThreadmgrForKernel")]
    int NID_59D1D5B0(uint cls)
    {
        GuestMemory.W32(Globals() + GlobalsUidClass, cls);
        return 0;
    }

    /// <summary>NID_5311CFB5(handler, argument): the process event handler and its argument (+0x23E8, +0x23EC); 0.</summary>
    [Hle(Nid = 0x5311CFB5, Library = "SceThreadmgrForDriver")]
    int NID_5311CFB5(uint handler, uint argument)
    {
        uint globals = Globals();
        GuestMemory.W32(globals + GlobalsProcessHandler, handler);
        GuestMemory.W32(globals + GlobalsProcessHandlerArgument, argument);
        return 0;
    }

    /// <summary>NID_1394E856(): raises the +0x2434 flag; 0.</summary>
    [Hle(Nid = 0x1394E856, Library = "SceThreadmgrForKernel")]
    int NID_1394E856()
    {
        GuestMemory.W32(Globals() + GlobalsProcessFlag, 1);
        return 0;
    }

    /// <summary>NID_5D270A94(): lowers the +0x2434 flag; 0.</summary>
    [Hle(Nid = 0x5D270A94, Library = "SceThreadmgrForKernel")]
    int NID_5D270A94()
    {
        GuestMemory.W32(Globals() + GlobalsProcessFlag, 0);
        return 0;
    }

    /// <summary>NID_60798313(uint *out): *out = the +0x28A8 word; 0 (no check of the pointer).</summary>
    [Hle(Nid = 0x60798313, Library = "SceThreadmgrForKernel")]
    int NID_60798313(uint output)
    {
        GuestMemory.W32(output, GuestMemory.R32(Globals() + GlobalsWord28A8));
        return 0;
    }

    /// <summary>NID_A7E5C83D(uint *out): *out = the +0x28AC word; 0.</summary>
    [Hle(Nid = 0xA7E5C83D, Library = "SceThreadmgrForKernel")]
    int NID_A7E5C83D(uint output)
    {
        GuestMemory.W32(output, GuestMemory.R32(Globals() + GlobalsWord28AC));
        return 0;
    }

    /// <summary>NID_6C2E3A49(group, index, handler): puts a debugger event handler in slot [group][index] (group below
    /// 2, index below 8, else SCE_KERNEL_ERROR_INVALID_ARGUMENT) when the slot is empty; a taken slot is 0x80028003.</summary>
    [Hle(Nid = 0x6C2E3A49, Library = "SceThreadmgrForDriver")]
    int NID_6C2E3A49(uint group, uint index, uint handler)
    {
        if (group >= 2 || index >= 8)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return ClaimSlot(GlobalsDebugHandlers + (group * 8 + index) * 4, handler);
    }

    /// <summary>NID_47F26712(group, index): empties debugger handler slot [group][index]; 0, or
    /// SCE_KERNEL_ERROR_INVALID_ARGUMENT out of range.</summary>
    [Hle(Nid = 0x47F26712, Library = "SceThreadmgrForDriver")]
    int NID_47F26712(uint group, uint index)
    {
        if (group >= 2 || index >= 8)
        {
            return SceError.INVALID_ARGUMENT;
        }

        GuestMemory.W32(Globals() + GlobalsDebugHandlers + (group * 8 + index) * 4, 0);
        return 0;
    }

    /// <summary>NID_1378F6EF(slot, handler): puts an event handler in slot 0-2 (the firmware's compare-and-set from 0);
    /// a taken slot is 0x80028003, a slot above 2 SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    [Hle(Nid = 0x1378F6EF, Library = "SceThreadmgrForDriver")]
    int NID_1378F6EF(uint slot, uint handler)
    {
        if (slot >= 3)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return ClaimSlot(GlobalsEventHandlers + slot * 4, handler);
    }

    /// <summary>NID_0D0644DA(slot): empties event handler slot 0-2; 0, or SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    [Hle(Nid = 0x0D0644DA, Library = "SceThreadmgrForDriver")]
    int NID_0D0644DA(uint slot)
    {
        if (slot >= 3)
        {
            return SceError.INVALID_ARGUMENT;
        }

        GuestMemory.W32(Globals() + GlobalsEventHandlers + slot * 4, 0);
        return 0;
    }

    int ClaimSlot(uint offset, uint handler)
    {
        uint address = Globals() + offset;
        lock (_globalsLock)
        {
            if (GuestMemory.R32(address) != 0)
            {
                return HandlerSlotBusy;
            }

            GuestMemory.W32(address, handler);
            return 0;
        }
    }

    void SetProbeHook(int index, uint hook)
    {
        lock (_globalsLock)
        {
            _probeHooks[index] = hook;
        }
    }

    /// <summary>The scheduler probe hook at the firmware's word 0x04 + 4 * <paramref name="index"/> (0 = none).</summary>
    internal uint ProbeHook(int index)
    {
        lock (_globalsLock)
        {
            return _probeHooks[index];
        }
    }

    /// <summary>NID_B4A05763(hook): the first scheduler probe hook (word 0x04; SceLowio sets it).</summary>
    [Hle(Nid = 0xB4A05763, Library = "SceThreadmgrForDriver")]
    void NID_B4A05763(uint hook) => SetProbeHook(0, hook);

    /// <summary>NID_F885ECCA(hook): the probe hook the scheduler calls when a thread leaves a core (word 0x08).</summary>
    [Hle(Nid = 0xF885ECCA, Library = "SceThreadmgrForKernel")]
    void NID_F885ECCA(uint hook) => SetProbeHook(1, hook);

    /// <summary>NID_F55FDEFB(hook): probe hook word 0x0C.</summary>
    [Hle(Nid = 0xF55FDEFB, Library = "SceThreadmgrForKernel")]
    void NID_F55FDEFB(uint hook) => SetProbeHook(2, hook);

    /// <summary>NID_A3975A5A(hook): probe hook word 0x10.</summary>
    [Hle(Nid = 0xA3975A5A, Library = "SceThreadmgrForKernel")]
    void NID_A3975A5A(uint hook) => SetProbeHook(3, hook);

    /// <summary>NID_9605DF7D(hook): probe hook word 0x14.</summary>
    [Hle(Nid = 0x9605DF7D, Library = "SceThreadmgrForKernel")]
    void NID_9605DF7D(uint hook) => SetProbeHook(4, hook);

    /// <summary>NID_8C07C8EC(hook): probe hook word 0x18.</summary>
    [Hle(Nid = 0x8C07C8EC, Library = "SceThreadmgrForKernel")]
    void NID_8C07C8EC(uint hook) => SetProbeHook(5, hook);

    /// <summary>NID_E2DACBC2(hook): probe hook word 0x1C.</summary>
    [Hle(Nid = 0xE2DACBC2, Library = "SceThreadmgrForKernel")]
    void NID_E2DACBC2(uint hook) => SetProbeHook(6, hook);

    /// <summary>NID_ABBE9BA5(hook): probe hook word 0x20.</summary>
    [Hle(Nid = 0xABBE9BA5, Library = "SceThreadmgrForKernel")]
    void NID_ABBE9BA5(uint hook) => SetProbeHook(7, hook);

    /// <summary>NID_10853CCD(hook): probe hook word 0x24.</summary>
    [Hle(Nid = 0x10853CCD, Library = "SceThreadmgrForKernel")]
    void NID_10853CCD(uint hook) => SetProbeHook(8, hook);
}
