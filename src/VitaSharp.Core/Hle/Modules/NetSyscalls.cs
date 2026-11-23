using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceNetPsForSyscalls: the kernel side of libnet (SceNetPs in os0:kd/bootimage.skprx, FW 3.74). The shell loads the
/// real libnet.suprx (LLE) for SceNetInternal, and that libnet reaches the kernel through these syscalls. They share
/// the HLE socket table with SceNet (NetSockets.cs) and answer like the kernel: a negative errno on failure (libnet
/// turns -e into 0x80410100|e and sets errno), the result otherwise. The kernel of an emulated unit has one network
/// interface, the loopback (no Wi-Fi chip): the shell's boot-time network setup (lo0 address, flags and route) and its
/// polls of the statistics run against it.
/// </summary>
public sealed partial class Net
{
    const int AF_ROUTE = 0x11;
    const uint SIOCSIFFLAGS = 0x80206910, SIOCGIFFLAGS = 0xC0206911, SIOCDIFADDR = 0x80206919, SIOCAIFADDR = 0x8040691A;
    const int IFF_UP = 0x1, IFF_LOOPBACK = 0x8, IFF_RUNNING = 0x40, IFF_MULTICAST = 0x8000;
    /// <summary>lo0's flags: the kernel brings it up at attach (BSD if_loop), the shell then sets them again.</summary>
    int _lo0Flags = IFF_UP | IFF_LOOPBACK | IFF_RUNNING | IFF_MULTICAST;
    uint _lo0Addr = 0x0100007F, _lo0Mask = 0x000000FF; // 127.0.0.1 / 255.0.0.0, network byte order

    /// <summary>An SceNet result (0x804101xx on failure) as the kernel's -errno.</summary>
    static int Kerr(int r) => r < 0 && ((uint)r & 0xFFFFFF00) == 0x80410100 ? -(r & 0xFF) : r;

    [Hle] int sceNetSyscallSocket(string name, int domain, int type, int protocol) => Kerr(sceNetSocket(name, domain, type, protocol));
    [Hle] int sceNetSyscallClose(int s) => Kerr(sceNetSocketCloseThroughEntry(s));

    /// <summary>(const struct { int s; const void *msg; uint len; int flags; const SceNetSockaddr *to; uint tolen; } *p):
    /// the arguments come in one 0x18-byte block. A routing socket takes the message (rt_msghdr, its length first) like
    /// the kernel's route_output: the whole message is accepted.</summary>
    [Hle]
    int sceNetSyscallSendto(uint p)
    {
        if (p == 0) return -14; // EFAULT: the kernel copies the block in
        int s = (int)GuestMemory.R32(p);
        uint msg = GuestMemory.R32(p + 4), len = GuestMemory.R32(p + 8);
        lock (_netLock)
        {
            if (Sock(s) is { Domain: AF_ROUTE, Host: null })
                return msg == 0 || len < 4 || GuestMemory.Read<ushort>(msg) > len ? -22 : (int)len;
        }
        return Kerr(sceNetSendtoThroughEntry(s, msg, len, (int)GuestMemory.R32(p + 12), GuestMemory.R32(p + 16), GuestMemory.R32(p + 20)));
    }

    /// <summary>(int s, uint cmd, void *argp): the interface ioctls the shell's network setup issues on lo0 (BSD
    /// struct ifreq: name[16], then the flags or address). Any other interface does not exist (ENXIO).</summary>
    [Hle]
    int sceNetSyscallIoctl(int s, uint cmd, uint argp)
    {
        if (CheckSock(s) < 0) return -9; // EBADF
        if (argp == 0) return -14;
        switch (cmd)
        {
            case SIOCSIFFLAGS or SIOCGIFFLAGS or SIOCDIFADDR or SIOCAIFADDR:
                if (GuestMemory.ReadCString(argp, 16) != "lo0") return -6;
                break;
            default:
                return 0; // driver-private requests (group 'P') of devices the unit does not have: accepted, no effect
        }
        lock (_netLock)
        {
            switch (cmd)
            {
                case SIOCGIFFLAGS: GuestMemory.Write(argp + 16, (short)_lo0Flags); break;
                case SIOCSIFFLAGS: _lo0Flags = GuestMemory.Read<ushort>(argp + 16) | (_lo0Flags & IFF_RUNNING) | IFF_LOOPBACK; break;
                case SIOCDIFADDR: _lo0Addr = 0; break;
                case SIOCAIFADDR: // struct ifaliasreq: name, addr, broadaddr, mask (sockaddr_in each: len, family, port, addr)
                    _lo0Addr = GuestMemory.R32(argp + 16 + 4);
                    _lo0Mask = GuestMemory.R32(argp + 48 + 4);
                    break;
            }
        }
        return 0;
    }

    /// <summary>(SceNetIfEntry *list, int max): one 0x140-byte record per interface (the kernel's if_list walk): +0
    /// name, +0x20 index, +0x28 IPv4 address, +0x2C/+0x30 mask, +0x48 flags, +0x58/+0x5C MTU. Returns the count (lo0 only).</summary>
    [Hle]
    int sceNetSyscallGetIfList(uint list, int max)
    {
        if (list == 0 || max <= 0) return -22;
        lock (_netLock)
        {
            GuestMemory.Fill(list, 0, 0x140);
            GuestMemory.WriteCString(list, "lo0", 16);
            GuestMemory.W32(list + 0x20, 1);
            GuestMemory.W32(list + 0x28, _lo0Addr);
            GuestMemory.W32(list + 0x2C, _lo0Mask);
            GuestMemory.W32(list + 0x30, _lo0Mask);
            GuestMemory.W32(list + 0x48, (uint)_lo0Flags);
            GuestMemory.W32(list + 0x58, 16384);
            GuestMemory.W32(list + 0x5C, 16384);
        }
        return 1;
    }

    /// <summary>
    /// (int s, int cmd, void *data, int size): SceNetPs control requests (FW 3.74 SceNetPs read in its code, study
    /// only). s &lt; 0 is the stack itself, s &gt;= 0 the kernel index of an interface: lo0 (1) is the only one here,
    /// any other index -EIO like the kernel's interface walk. A NULL block, a short one or an unknown command: -EINVAL,
    /// the kernel's default. The stack's own commands (2, 0x20000002, 0x22) are -EIO on an interface, the interface
    /// ones (4, 0x20000004) on the stack. The commands and their state are in NetSyscalls.Control.cs.
    /// </summary>
    [Hle]
    int sceNetSyscallControl(int s, int cmd, uint data, int size)
    {
        if (data == 0)
        {
            return KernelEinval;
        }

        bool onInterface = s >= 0;
        if (onInterface && s != Lo0KernelIndex)
        {
            return KernelEio;
        }

        lock (_netLock)
        {
            return StackControl(onInterface, cmd, data, size);
        }
    }

    /// <summary>(int s, int flags): attaches a socket to the interface connection manager (the kernel looks the socket
    /// up first: EBADF for an unknown one). Bit 0x80 = do not wait.</summary>
    [Hle] int sceNetSyscallIcmConnect(int s, int flags) => CheckSock(s) < 0 ? -9 : 0;

    /// <summary>SceNetInternal's user-side version (libnet): the same, with SceNet error codes and errno.</summary>
    [Hle("NID_84257F65", Nid = 0x84257F65, Library = "SceNet")] // same entry as sceNetInternalIcmConnect in libnet.suprx 3.74
    [Hle("sceNetInternalIcmConnect")] int sceNetInternalIcmConnect(int s, int flags) => CheckSock(s);

    /// <summary>libnet.suprx 3.74 NID_522D2118 (SceNet) = NID_9DBD2F60 (SceNetInternal)(value, flags, reserved): needs
    /// sceNetInit (ENOTINIT), reserved 0 and only flag bit 0 (EINVAL), then hands the stack { value, 0, flags } as
    /// control 0x20000019 (s = -1), a setting the stack takes (see <see cref="sceNetSyscallControl"/>).</summary>
    [Hle("NID_522D2118", Nid = 0x522D2118, Library = "SceNet")]
    [Hle("NID_9DBD2F60", Nid = 0x9DBD2F60, Library = "SceNetInternal")]
    int NetStackSetting19(uint value, uint flags, uint reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (reserved != 0 || (flags & ~1u) != 0)
        {
            return NetErr(EINVAL);
        }

        return 0;
    }

    /// <summary>libnet.suprx 3.74 NID_1F0080B3 (SceNet) = NID_46E28336 (SceNetInternal)(entries, count, reserved):
    /// needs sceNetInit, reserved 0 (EINVAL), then control 0x21 (s = -1) with count 8-byte entries; the stack's error
    /// comes back as a SceNet error (0x80410100 | errno).</summary>
    [Hle("NID_1F0080B3", Nid = 0x1F0080B3, Library = "SceNet")]
    [Hle("NID_46E28336", Nid = 0x46E28336, Library = "SceNetInternal")]
    int NetStackFlaggedList(uint entries, int count, uint reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (reserved != 0)
        {
            return NetErr(EINVAL);
        }

        int result = sceNetSyscallControlThroughEntry(-1, 0x21, entries, count << 3);
        return result < 0 ? NetErr(unchecked((int)0x80410100) | -result) : result;
    }

    /// <summary>libnet.suprx 3.74 NID_D886F498 (SceNet)(mac, flags): needs sceNetInit; a non-NULL mac and exactly one of
    /// flag bits 0 (eth0) and 1 (wlan0), else EINVAL. It looks the interface up (NID_235DE96C's search) and copies the
    /// 6-byte hardware address of its record, 0 when the lookup does not fail. Only lo0 exists on this stack: the
    /// interface is not found and the address stays the empty one.</summary>
    [Hle("NID_D886F498", Nid = 0xD886F498, Library = "SceNet")]
    int NetInterfaceHardwareAddress(uint mac, uint flags)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (mac == 0 || flags == 0 || (flags & ~3u) != 0 || flags == 3)
        {
            return NetErr(EINVAL);
        }

        GuestMemory.Fill(mac, 0, 6);
        return 0;
    }

    /// <summary>The stack setting 0x18 (control 0x10000018 reads it, 0x20000018 writes it), two slots by flag bit 0.</summary>
    int _ctl18a, _ctl18b;
    void SetCtl18(int slot, int v) { if (slot == 0) _ctl18a = v; else _ctl18b = v; }

    /// <summary>libnet's interface names, the index NID_235DE96C takes.</summary>
    static readonly string[] IfNames = ["lo0", "eth0", "eth1", "eth2", "wlan0", "wlan1", "wlan2", "bt0", "phone0", "pppoe0"];

    /// <summary>libnet.suprx (FW 3.74) NID_235DE96C (int ifIndex, SceNetIfEntry *entry, int count): with ifIndex -1, the
    /// kernel's interface list itself (count entries, the number back). Otherwise whether interface IfNames[ifIndex]
    /// exists (1) or not (0), its 0x140-byte record copied to entry when one is given (then count must be 1: else
    /// EINVAL). Only lo0 exists on a unit without a Wi-Fi chip, so PS3 Remote Play and PS4 Link see no wlan0.</summary>
    [Hle("sceNetInternal_235DE96C", Nid = 0x235DE96C, Library = "SceNetInternal")]
    int sceNetInternalGetIfEntry(int ifIndex, uint entry, int count)
    {
        if (!_netInited) return NetErr(ENOTINIT);
        if (ifIndex == -1) { int r = sceNetSyscallGetIfListThroughEntry(entry, count); return r < 0 ? NetErr(unchecked((int)0x80410100) | -r) : r; }
        if (!(entry == 0 && count == 0) && (entry == 0 || count != 1)) return NetErr(EINVAL);
        if ((uint)ifIndex >= IfNames.Length) return 0; // libnet compares against "" (errno EINVAL): never found
        if (IfNames[ifIndex] != "lo0") return 0;
        if (entry != 0) sceNetSyscallGetIfListThroughEntry(entry, 1);
        return 1;
    }

    /// <summary>NID_DE8DD55E (int *value, int flag) / NID_7493FB0C (int value, int flag): get / set the stack setting
    /// 0x18 through the control syscall; flag must be 0 or 1 (EINVAL), the getter needs its pointer.</summary>
    [Hle("sceNetInternal_DE8DD55E", Nid = 0xDE8DD55E, Library = "SceNetInternal")]
    int sceNetInternalGetSetting18(uint value, int flag)
    {
        if (!_netInited) return NetErr(ENOTINIT);
        if (value == 0 || (flag & ~1) != 0) return NetErr(EINVAL);
        lock (_netLock) GuestMemory.W32(value, (uint)(flag == 0 ? _ctl18a : _ctl18b));
        return 0;
    }

    [Hle("sceNetInternal_7493FB0C", Nid = 0x7493FB0C, Library = "SceNetInternal")]
    int sceNetInternalSetSetting18(int value, int flag)
    {
        if (!_netInited) return NetErr(ENOTINIT);
        if ((flag & ~1) != 0) return NetErr(EINVAL);
        lock (_netLock) SetCtl18(flag, value);
        return 0;
    }
}
