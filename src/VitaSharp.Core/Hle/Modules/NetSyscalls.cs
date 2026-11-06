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
    [Hle] int sceNetSyscallClose(int s) => Kerr(sceNetSocketClose(s));

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
        return Kerr(sceNetSendto(s, msg, len, (int)GuestMemory.R32(p + 12), GuestMemory.R32(p + 16), GuestMemory.R32(p + 20)));
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

    /// <summary>(int s, int cmd, void *data, int size): SceNetPs control requests (s &lt; 0: the stack itself). 0x22:
    /// read and clear the 0x44-byte traffic counters (none on the loopback-only stack); 0x14: an int; 0x10000002: the
    /// state of an entry of a three-slot table looked up by the id in data[0] (empty here: -EIO, as the kernel);
    /// 0x20000003/0x12/0x19/0x20: settings the stack takes (the shell's network setup). Unknown commands or short buffers: -EINVAL, the kernel's default.</summary>
    [Hle]
    int sceNetSyscallControl(int s, int cmd, uint data, int size)
    {
        if (data == 0) return -22;
        switch (cmd)
        {
            case 0x22 when size >= 0x44: GuestMemory.Fill(data, 0, 0x44); return 0;
            case 0x14 when size >= 4: GuestMemory.W32(data, 0); return 0;
            case 0x10000002 when size >= 0x20: return -5;
            case 0x20000012 when size >= 0x200: return 0;
            case 0x20000019 when size >= 0xC: return 0;
            case 0x20000003 when size >= 8: return 0;
            case 0x20000020 when size >= 4: return 0;
            case 0x10000018 when size >= 8: // { int value; int flags }: read setting 0x18 (bit 0 of flags picks the slot)
                lock (_netLock) GuestMemory.W32(data, (uint)((GuestMemory.R32(data + 4) & 1) == 0 ? _ctl18a : _ctl18b));
                return 0;
            case 0x20000018 when size >= 8:
                lock (_netLock) SetCtl18((int)GuestMemory.R32(data + 4) & 1, (int)GuestMemory.R32(data));
                return 0;
            default:
                Log.WarnOnce($"netctl{cmd:X}", $"sceNetSyscallControl: command 0x{cmd:X} (size {size}) not modelled");
                return -22;
        }
    }

    /// <summary>(int s, int flags): attaches a socket to the interface connection manager (the kernel looks the socket
    /// up first: EBADF for an unknown one). Bit 0x80 = do not wait.</summary>
    [Hle] int sceNetSyscallIcmConnect(int s, int flags) => CheckSock(s) < 0 ? -9 : 0;

    /// <summary>SceNetInternal's user-side version (libnet): the same, with SceNet error codes and errno.</summary>
    [Hle] int sceNetInternalIcmConnect(int s, int flags) => CheckSock(s);

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
        if (ifIndex == -1) { int r = sceNetSyscallGetIfList(entry, count); return r < 0 ? NetErr(unchecked((int)0x80410100) | -r) : r; }
        if (!(entry == 0 && count == 0) && (entry == 0 || count != 1)) return NetErr(EINVAL);
        if ((uint)ifIndex >= IfNames.Length) return 0; // libnet compares against "" (errno EINVAL): never found
        if (IfNames[ifIndex] != "lo0") return 0;
        if (entry != 0) sceNetSyscallGetIfList(entry, 1);
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
