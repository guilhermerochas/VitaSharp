using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceNetInternal's exports that reach the kernel through sceNetSyscallControl, sceNetSyscallIoctl and the route
// sysctl, as libnet.suprx 3.74 does (read in its code, study only): each checks its arguments, needs sceNetInit
// (0x804101C8), builds the command's block and turns the kernel's -errno into 0x80410100 | errno (errno set too).
// The interface index of these calls is libnet's (0 lo0, 1 eth0 ... 9 pppoe0, see IfNames); libnet looks it up in
// the kernel's interface list and passes the kernel index (lo0's is 1) to the command.
public sealed partial class Net
{
    const int LIBNET_ENODEV = unchecked((int)0x80410113), LIBNET_IF_NOT_FOUND = unchecked((int)0x80410159);

    /// <summary>A small guest block for the commands' arguments (libnet builds them on its stack), used under the net
    /// lock.</summary>
    [SnapshotMissing]
    uint _controlBlock;

    uint ControlBlock()
    {
        if (_controlBlock == 0)
        {
            _controlBlock = Emu.Memory.Alloc(0x40, "libnet control block", MemoryRegion.User, 16);
        }

        GuestMemory.Fill(_controlBlock, 0, 0x40);
        return _controlBlock;
    }

    /// <summary>libnet's syscall result: the kernel's -errno as 0x80410100 | errno (errno set), an error outside the
    /// errno range as it is (errno 0xCD); a success as it is.</summary>
    int KernelToNet(int result)
    {
        if (result >= 0)
        {
            return result;
        }

        int errno = -result;
        if ((errno & 0x0FFFFFFF) >> 16 == 0)
        {
            return NetErr(unchecked((int)0x80410100) | errno);
        }

        if (_errnoLoc != 0)
        {
            GuestMemory.W32(_errnoLoc, 0xCD);
        }

        return result;
    }

    int LibnetControl(int kernelIndex, int cmd, uint data, int size)
    {
        return KernelToNet(sceNetSyscallControlThroughEntry(kernelIndex, cmd, data, size));
    }

    /// <summary>libnet's interface lookup: -1 is no interface (EINVAL); a name of IfNames found in the kernel's list
    /// gives that interface's kernel index, else 0x80410159.</summary>
    static int LibnetKernelIndex(int ifIndex)
    {
        if (ifIndex == -1)
        {
            return EINVAL;
        }

        return (uint)ifIndex < IfNames.Length && IfNames[ifIndex] == "lo0" ? Lo0KernelIndex : LIBNET_IF_NOT_FOUND;
    }

    /// <summary>The kernel index for a command that takes "the stack" as -1: -1 stays, an interface is looked up and a
    /// failed lookup is 0x80410113.</summary>
    static int LibnetTarget(int ifIndex)
    {
        if (ifIndex == -1)
        {
            return -1;
        }

        int kernelIndex = LibnetKernelIndex(ifIndex);
        return kernelIndex < 0 ? LIBNET_ENODEV : kernelIndex;
    }

    /// <summary>NID_01B090F1 (int ifIndex, void *link, int reserved) / NID_E03F6A77: read / write an interface's 0x14-byte
    /// link record (commands 4 / 0x20000004 on the interface): link non-NULL and reserved 0, the interface found.</summary>
    [Hle("NID_01B090F1", Nid = 0x01B090F1, Library = "SceNetInternal")]
    int NetInternalGetLinkRecord(int ifIndex, uint link, int reserved)
    {
        return InterfaceLinkCommand(ifIndex, link, reserved, 4);
    }

    [Hle("NID_E03F6A77", Nid = 0xE03F6A77, Library = "SceNetInternal")]
    int NetInternalSetLinkRecord(int ifIndex, uint link, int reserved)
    {
        return InterfaceLinkCommand(ifIndex, link, reserved, 0x20000004);
    }

    int InterfaceLinkCommand(int ifIndex, uint link, int reserved, int cmd)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (link == 0 || reserved != 0)
        {
            return NetErr(EINVAL);
        }

        int kernelIndex = LibnetKernelIndex(ifIndex);
        if (kernelIndex < 0)
        {
            return NetErr(LIBNET_ENODEV);
        }

        return LibnetControl(kernelIndex, cmd, link, 0x14);
    }

    /// <summary>NID_26EFF46D (int ifIndex, int value, uint flags): flags bit 0 only; command 0x20000006 { value, flags }
    /// on the interface (-1: every interface).</summary>
    [Hle("NID_26EFF46D", Nid = 0x26EFF46D, Library = "SceNetInternal")]
    int NetInternalAnnounceLink(int ifIndex, uint value, uint flags)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if ((flags & ~1u) != 0)
        {
            return NetErr(EINVAL);
        }

        int target = LibnetTarget(ifIndex);
        if (target == LIBNET_ENODEV)
        {
            return NetErr(target);
        }

        lock (_netLock)
        {
            uint block = ControlBlock();
            GuestMemory.W32(block, value);
            GuestMemory.W32(block + 4, flags);
            return LibnetControl(target, 0x20000006, block, 8);
        }
    }

    /// <summary>NID_4F5E0E8A (int ifIndex, int reserved1, int reserved2): both 0; command 0x20000011 { 0 } (clears the
    /// interface's address-conflict record; -1 every interface).</summary>
    [Hle("NID_4F5E0E8A", Nid = 0x4F5E0E8A, Library = "SceNetInternal")]
    int NetInternalClearConflict(int ifIndex, int reserved1, int reserved2)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (reserved1 != 0 || reserved2 != 0)
        {
            return NetErr(EINVAL);
        }

        int target = LibnetTarget(ifIndex);
        if (target == LIBNET_ENODEV)
        {
            return NetErr(target);
        }

        lock (_netLock)
        {
            return LibnetControl(target, 0x20000011, ControlBlock(), 4);
        }
    }

    /// <summary>NID_EE94BF52 (int pid, int *state, int reserved) / NID_0DFC23D6 (int pid, uint state, int reserved): a
    /// process's network state (commands 0x10000017 / 0x20000017); the state is 0..3, the kernel knows the process.</summary>
    [Hle("NID_EE94BF52", Nid = 0xEE94BF52, Library = "SceNetInternal")]
    int NetInternalGetProcessState(int pid, uint state, int reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (reserved != 0 || state == 0)
        {
            return NetErr(EINVAL);
        }

        lock (_netLock)
        {
            uint block = ControlBlock();
            GuestMemory.W32(block, (uint)pid);
            int result = LibnetControl(-1, 0x10000017, block, 8);
            if (result >= 0)
            {
                GuestMemory.W32(state, GuestMemory.R32(block + 4));
            }

            return result;
        }
    }

    [Hle("NID_0DFC23D6", Nid = 0x0DFC23D6, Library = "SceNetInternal")]
    int NetInternalSetProcessState(int pid, uint state, int reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (reserved != 0 || (state & ~3u) != 0)
        {
            return NetErr(EINVAL);
        }

        lock (_netLock)
        {
            uint block = ControlBlock();
            GuestMemory.W32(block, (uint)pid);
            GuestMemory.W32(block + 4, state);
            return LibnetControl(-1, 0x20000017, block, 8);
        }
    }

    /// <summary>NID_EF421A65 (int key, int *value, uint flags): flags bit 0 only (the key is then the process id itself);
    /// command 0x10000019 { key, flags, 0 }, the value from +8.</summary>
    [Hle("NID_EF421A65", Nid = 0xEF421A65, Library = "SceNetInternal")]
    int NetInternalGetSetting19(int key, uint value, uint flags)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if ((flags & ~1u) != 0 || value == 0)
        {
            return NetErr(EINVAL);
        }

        lock (_netLock)
        {
            uint block = ControlBlock();
            GuestMemory.W32(block, (uint)key);
            GuestMemory.W32(block + 4, flags);
            int result = LibnetControl(-1, 0x10000019, block, 0xC);
            if (result >= 0)
            {
                GuestMemory.W32(value, GuestMemory.R32(block + 8));
            }

            return result;
        }
    }

    /// <summary>NID_DEF2357F (entry *entries, int count, int reserved): reserved 0; command 0x16, count 12-byte entries
    /// { pid, state, - } of the processes using the network.</summary>
    [Hle("NID_DEF2357F", Nid = 0xDEF2357F, Library = "SceNetInternal")]
    int NetInternalListProcesses(uint entries, int count, int reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (reserved != 0)
        {
            return NetErr(EINVAL);
        }

        return LibnetControl(-1, 0x16, entries, count * 12);
    }

    /// <summary>NID_A9F2277C (counters *counters, int reserved): reads and clears the stack's 0x44-byte traffic counters
    /// (command 0x22).</summary>
    [Hle("NID_A9F2277C", Nid = 0xA9F2277C, Library = "SceNetInternal", Default = true)]
    int NetInternalReadCounters(uint counters, int reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (counters == 0 || reserved != 0)
        {
            return NetErr(EINVAL);
        }

        Log.InfoOnce("libnet-counters", "SceNetInternal: traffic counters read (no network interface: all zero, default)");
        return LibnetControl(-1, 0x22, counters, 0x44);
    }

    /// <summary>NID_A6B9F5D2 (const uint pair[2], int reserved): reserved 0; the stack setting 0x23 (command 0x20000023,
    /// the two words as given).</summary>
    [Hle("NID_A6B9F5D2", Nid = 0xA6B9F5D2, Library = "SceNetInternal")]
    int NetInternalSetSetting23(uint pair, int reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (reserved != 0)
        {
            return NetErr(EINVAL);
        }

        return LibnetControl(-1, 0x20000023, pair, 8);
    }

    /// <summary>NID_EE1985D9 (int value): the stack setting 0x20 (command 0x20000020 { value }).</summary>
    [Hle("NID_EE1985D9", Nid = 0xEE1985D9, Library = "SceNetInternal")]
    int NetInternalSetSetting20(uint value)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        lock (_netLock)
        {
            uint block = ControlBlock();
            GuestMemory.W32(block, value);
            return LibnetControl(-1, 0x20000020, block, 4);
        }
    }

    /// <summary>NID_16222ED6 (const uint dns[2], int reserved): the stack's DNS servers (command 0x20000002 with a
    /// 0x28-byte record holding the two addresses).</summary>
    [Hle("NID_16222ED6", Nid = 0x16222ED6, Library = "SceNetInternal")]
    int NetInternalSetStackDns(uint dns, int reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (reserved != 0 || dns == 0)
        {
            return NetErr(EINVAL);
        }

        lock (_netLock)
        {
            uint block = ControlBlock();
            GuestMemory.W32(block, GuestMemory.R32(dns));
            GuestMemory.W32(block + 4, GuestMemory.R32(dns + 4));
            return LibnetControl(-1, 0x20000002, block, 0x28);
        }
    }

    /// <summary>NID_7F63379E (uint dns[2], int reserved): the DNS servers in use: the process's own (sceNetSetDnsInfo)
    /// when set, else the stack's (command 2; its "changed" bit drops libnet's resolver cache, which the HLE resolver
    /// does not keep). Returns how many are set (0, 1 or 2).</summary>
    [Hle("NID_7F63379E", Nid = 0x7F63379E, Library = "SceNetInternal")]
    int NetInternalGetDns(uint dns, int reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (dns == 0 || reserved != 0)
        {
            return NetErr(EINVAL);
        }

        lock (_netLock)
        {
            uint block = ControlBlock();
            int result = LibnetControl(-1, 2, block, 0x28);
            if (result < 0)
            {
                return result;
            }

            uint primary = _dns0 != 0 ? _dns0 : GuestMemory.R32(block);
            uint secondary = _dns0 != 0 ? _dns1 : GuestMemory.R32(block + 4);
            GuestMemory.W32(dns, primary);
            GuestMemory.W32(dns + 4, secondary);
            return primary == 0 ? 0 : secondary == 0 ? 1 : 2;
        }
    }

    /// <summary>NID_AA414742 (int ifIndex, int s, int cmd, void *data, int size): any control command. With ifIndex -1
    /// s is the kernel target itself (it must not be negative); otherwise s must be 0 and the interface is looked up
    /// (its lookup error returned as it is).</summary>
    [Hle("NID_AA414742", Nid = 0xAA414742, Library = "SceNetInternal")]
    int NetInternalControl(int ifIndex, int s, int cmd, uint data, int size)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        int target = s;
        if (ifIndex == -1)
        {
            if (s < 0)
            {
                return NetErr(EINVAL);
            }
        }
        else
        {
            if (s != 0)
            {
                return NetErr(EINVAL);
            }

            target = LibnetKernelIndex(ifIndex);
            if (target < 0)
            {
                return NetErr(target);
            }
        }

        return LibnetControl(target, cmd, data, size);
    }

    /// <summary>NID_333F9CB3 (int s, uint cmd, void *argp): sceNetSyscallIoctl with libnet's error conversion.</summary>
    [Hle("NID_333F9CB3", Nid = 0x333F9CB3, Library = "SceNetInternal")]
    int NetInternalIoctl(int s, uint cmd, uint argp)
    {
        if (!Initialized)
        {
            return ENOTINIT;
        }

        return KernelToNet(sceNetSyscallIoctlThroughEntry(s, cmd, argp));
    }

    /// <summary>
    /// NID_BDA92B76 (int family, route *routes, int max, uint flags): the IPv4 routes (0x58-byte records) from the
    /// stack's routing sysctl { CTL_NET, PF_ROUTE, 0, AF_INET, NET_RT_DUMP, 0 }; family must be AF_INET (2), flags bit
    /// 0 (default route only) needs one record. With routes NULL it returns the count. The emulated kernel has no MIB
    /// tree (see <see cref="sceNetSyscallSysctl"/>), so the dump fails as there: ENOENT.
    /// </summary>
    [Hle("NID_BDA92B76", Nid = 0xBDA92B76, Library = "SceNetInternal", Default = true)]
    int NetInternalGetRoutes(int family, uint routes, int max, uint flags)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        Log.InfoOnce("libnet-routes", "SceNetInternal: route table read (no network interface: no routes, default)");
        return LibnetRoutes(-1, family, routes, max, flags);
    }

    int LibnetRoutes(int ifIndex, int family, uint routes, int max, uint flags)
    {
        if (family != 2 || ((flags & 1) != 0 && (routes == 0 || max != 1)))
        {
            return NetErr(EINVAL);
        }

        if (ifIndex != -1)
        {
            int kernelIndex = LibnetKernelIndex(ifIndex);
            if (kernelIndex < 0)
            {
                return NetErr(kernelIndex);
            }
        }

        lock (_netLock)
        {
            uint block = ControlBlock();
            uint mib = block + 0x20;
            uint length = block + 0x38;
            uint[] name = [4, 0x11, 0, 2, 1, 0];
            for (int i = 0; i < name.Length; i++)
            {
                GuestMemory.W32(mib + (uint)i * 4, name[i]);
            }

            GuestMemory.W32(block, mib);
            GuestMemory.W32(block + 4, 6);
            GuestMemory.W32(block + 12, length);
            int result = KernelToNet(sceNetSyscallSysctlThroughEntry(block));
            if (result < 0)
            {
                return result;
            }

            return 0; // the size query succeeded with no route to list
        }
    }

    /// <summary>NID_5B0E370C (int ifIndex, int *count): the interface's routes in a libnet allocation (count records of
    /// 0x58 bytes, freed with NID_CF49E9C4), NULL when there are none or the dump fails; NULL before sceNetInit.</summary>
    [Hle("NID_5B0E370C", Nid = 0x5B0E370C, Library = "SceNetInternal", Default = true)]
    uint NetInternalAllocRoutes(int ifIndex, uint count)
    {
        if (!Initialized)
        {
            return 0;
        }

        Log.InfoOnce("libnet-routes", "SceNetInternal: route table read (no network interface: no routes, default)");
        int routes = LibnetRoutes(ifIndex, 2, 0, 0, 0);
        if (routes <= 0)
        {
            return 0;
        }

        uint records = LibnetAlloc(routes * 0x58, true);
        if (records == 0)
        {
            return 0;
        }

        int filled = LibnetRoutes(ifIndex, 2, records, routes, 0);
        GuestMemory.W32(count, (uint)filled);
        if (filled < 1)
        {
            LibnetFree(records);
            return 0;
        }

        return records;
    }

    /// <summary>libnet's allocator (the pool sceNetInit was given): size &gt; 0, 8-byte aligned blocks, zeroed when
    /// asked; NULL when it cannot.</summary>
    uint LibnetAlloc(int size, bool zero)
    {
        if (size <= 0)
        {
            return 0;
        }

        uint block = Emu.Memory.Alloc((uint)(size + 7) & ~7u, "libnet heap", MemoryRegion.User, 8);
        if (block != 0 && zero)
        {
            GuestMemory.Fill(block, 0, (uint)size);
        }

        if (block != 0)
        {
            lock (_netLock)
            {
                _libnetBlocks[block] = size;
            }
        }

        return block;
    }

    /// <summary>libnet's free: the block is poisoned with 0xDD, then returned; NULL or a block libnet did not hand out:
    /// nothing.</summary>
    void LibnetFree(uint block)
    {
        int size;
        lock (_netLock)
        {
            if (block == 0 || !_libnetBlocks.Remove(block, out size))
            {
                return;
            }
        }

        GuestMemory.Fill(block, 0xDD, (uint)size);
        Emu.Memory.Free(block);
    }

    /// <summary>NID_8157DE3E (int *indexes, int max, uint flags): the libnet indexes of the interfaces in the kernel's
    /// list, in the list's order; flags bit 0 also counts lo0 and pppoe0 (indexes 0 and 9), without it only 1..8.
    /// indexes non-NULL and max &gt; 0, else EINVAL. Only lo0 exists here.</summary>
    [Hle("NID_8157DE3E", Nid = 0x8157DE3E, Library = "SceNetInternal")]
    int NetInternalListInterfaces(uint indexes, int max, uint flags)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if ((flags & ~1u) != 0 || indexes == 0 || max <= 0)
        {
            return NetErr(EINVAL);
        }

        int first = (flags & 1) != 0 ? 0 : 1;
        int last = (flags & 1) != 0 ? 9 : 8;
        int found = 0;
        foreach (string kernelInterface in KernelInterfaces)
        {
            for (int index = first; index <= last; index++)
            {
                if (IfNames[index] == kernelInterface)
                {
                    GuestMemory.W32(indexes + (uint)found * 4, (uint)index);
                    found++;
                    break;
                }
            }

            if (found >= max)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>The kernel's interface list in order (<see cref="sceNetSyscallGetIfList"/>): the loopback only.</summary>
    static readonly string[] KernelInterfaces = ["lo0"];

    /// <summary>
    /// NID_EE94BA15 (int ifIndex, uint *delta, int one, history *history): the interface's 17 statistics words since
    /// the last call (the record's +0x60..+0x9C and +0x10C), kept per interface in the caller's 0x48-byte history
    /// slots (history[ifIndex]); a new interface generation (+0x110) restarts the slot. one must be 1 and delta
    /// non-NULL, ifIndex not -1 (EINVAL); an interface the kernel does not list gives 0 and nothing is written. lo0's
    /// statistics are all zero here.
    /// </summary>
    [Hle("NID_EE94BA15", Nid = 0xEE94BA15, Library = "SceNetInternal", Default = true)]
    int NetInternalStatisticsDelta(int ifIndex, uint delta, int one, uint history)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (one != 1 || delta == 0 || ifIndex == -1)
        {
            return NetErr(EINVAL);
        }

        if (LibnetKernelIndex(ifIndex) < 0)
        {
            return 0;
        }

        Log.InfoOnce("libnet-statistics", "SceNetInternal: interface statistics read (no network interface: all zero, default)");
        Span<uint> current = stackalloc uint[18]; // 16 counters, +0x10C, the generation +0x110 (lo0: none)
        current.Clear(); // the module skips locals init
        uint slot = history + (uint)ifIndex * 0x48;
        if (GuestMemory.R32(slot + 0x44) != current[17])
        {
            GuestMemory.Fill(slot, 0, 0x48);
            GuestMemory.W32(slot + 0x44, current[17]);
        }

        for (int i = 0; i < 17; i++)
        {
            uint offset = (uint)i * 4;
            GuestMemory.W32(delta + offset, current[i] - GuestMemory.R32(slot + offset));
            GuestMemory.W32(slot + offset, current[i]);
        }

        return 0;
    }
}
