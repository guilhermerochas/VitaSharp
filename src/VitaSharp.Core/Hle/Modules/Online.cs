using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Network, PSN and web services. VitaSharp behaves like a console on a LAN without internet access (PSN account: see PsnAccount):
/// initialisation succeeds, connection attempts fail with the errors games handle gracefully.
/// </summary>
public sealed partial class Net : HleModule
{
    const int ERR_NET_ENOTCONN = unchecked((int)0x80410139), ERR_NET_ENETUNREACH = unchecked((int)0x80410133);
    const int ERR_NETCTL_INVALID_ADDR = unchecked((int)0x80412107), ERR_NETCTL_CALLBACK_MAX = unchecked((int)0x80412103);
    const int ERR_NETCTL_NOT_CONNECTED = unchecked((int)0x80412108);
    [Hle] uint sceNetHtonl(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    [Hle] uint sceNetNtohl(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    [Hle] uint sceNetHtons(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((ushort)v);
    [Hle] uint sceNetNtohs(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((ushort)v);

    [Hle]
    int sceNetGetMacAddress(uint addr, int flags)
    {
        if (addr == 0) return unchecked((int)0x80410116);
        byte[] mac = [0x00, 0x1F, 0xA7, 0x56, 0x49, 0x54];
        mac.CopyTo(GuestMemory.Span(addr, 6));
        return 0;
    }

    [Hle]
    uint sceNetInetNtop(int af, uint src, uint dst, uint size)
    {
        if (src == 0 || dst == 0) return 0;
        var b = GuestMemory.Span(src, 4);
        GuestMemory.WriteCString(dst, $"{b[0]}.{b[1]}.{b[2]}.{b[3]}", (int)size);
        return dst;
    }

    [Hle]
    int sceNetInetPton(int af, string src, uint dst)
    {
        if (dst == 0 || !System.Net.IPAddress.TryParse(src, out var ip)) return 0;
        ip.GetAddressBytes().AsSpan(0, 4).CopyTo(GuestMemory.Span(dst, 4));
        return 1;
    }

    [Hle]
    int sceNetGetStatisticsInfo(uint info, int flags)
    {
        if (info == 0) return unchecked((int)0x80410116);
        // SceNetStatisticsInfo: kernel free/min, packet count, qos count, libnet free/min (24 bytes)
        GuestMemory.W32(info, 0x40000); GuestMemory.W32(info + 4, 0x30000); GuestMemory.W32(info + 8, 0);
        GuestMemory.W32(info + 12, 0); GuestMemory.W32(info + 16, 0x20000); GuestMemory.W32(info + 20, 0x18000);
        return 0;
    }
    [Hle] int sceNetShowNetstat() => 0;

    // NetCtl: initialised. Registered callbacks are told the connection state once, which is what games wait for
    // before leaving their "checking connection" states: infrastructure callbacks get IPOBTAINED while the emulated
    // PSN account is signed in (a LAN with an IP address, see PsnAccount) and DISCONNECTED otherwise (cable unplugged,
    // the same event Vita3K sends); ad hoc callbacks always get DISCONNECTED (no ad hoc network).
    internal const int NETCTL_EVENT_DISCONNECTED = 1, NETCTL_EVENT_IPOBTAINED = 3;
    sealed class NetCallback { public uint Func, Arg; public bool Notified; }
    readonly Dictionary<int, NetCallback> _inetCallbacks = new(), _adhocCallbacks = new();
    int _nextCid;

    [Hle]
    int sceNetCtlCheckCallback()
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        var pending = new List<(NetCallback Cb, int Event)>();
        int inetEvent = PsnAccount.Current.SignedIn || Cfg.Real ? NETCTL_EVENT_IPOBTAINED : NETCTL_EVENT_DISCONNECTED;
        lock (_inetCallbacks)
        {
            foreach (var c in _inetCallbacks.Values)
                if (!c.Notified) { c.Notified = true; pending.Add((c, inetEvent)); }
            foreach (var c in _adhocCallbacks.Values)
                if (!c.Notified) { c.Notified = true; pending.Add((c, NETCTL_EVENT_DISCONNECTED)); }
        }
        foreach (var (c, ev) in pending) Kernel.GuestThread.Current?.CallGuest(c.Func, (uint)ev, c.Arg);
        return 0;
    }

    // The emulated console is on a LAN (IP obtained) but has no route to Sony's servers (PsnAccount decides the NP state),
    // the same profile Vita3K presents; socket/HTTP calls fail like an unreachable server. With real internet
    // (--net=real) it is a Wi-Fi connection with the host's IPv4 address, netmask, gateway and DNS servers.
    [Hle] int sceNetCtlInetGetState(Ptr<int> state) { if (state.Address == 0) return ERR_NETCTL_INVALID_ADDR; state.Value = 3; return 0; }
    [Hle] int sceNetCtlInetGetResult(int eventType, Ptr<int> error) { if (error.Address == 0) return ERR_NETCTL_INVALID_ADDR; error.Value = 0; return 0; }

    /// <summary>The host's primary IPv4 configuration (an up interface with a gateway, else any up non-loopback one).</summary>
    internal sealed record HostIpInfo(string Ip, string Netmask, string Gateway, string Dns1, string Dns2, int Mtu);

    static HostIpInfo? _hostIp;
    static long _hostIpAt;

    internal static HostIpInfo HostIp()
    {
        if (_hostIp is { } cached && Environment.TickCount64 - Interlocked.Read(ref _hostIpAt) < 10_000) return cached;
        var info = new HostIpInfo("192.168.0.10", "255.255.255.0", "192.168.0.1", "192.168.0.1", "0.0.0.0", 1500);
        try
        {
            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .Select(n => (Nic: n, Props: n.GetIPProperties()))
                .OrderByDescending(n => n.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)));
            foreach (var (nic, p) in nics)
            {
                var ua = p.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (ua == null) continue;
                var gw = p.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                var dns = p.DnsAddresses.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                int mtu = 1500;
                try { mtu = p.GetIPv4Properties()?.Mtu ?? 1500; } catch (System.Net.NetworkInformation.NetworkInformationException) { } catch (PlatformNotSupportedException) { }
                info = new HostIpInfo(ua.Address.ToString(), ua.IPv4Mask?.ToString() ?? "255.255.255.0", gw?.ToString() ?? "0.0.0.0",
                    dns.ElementAtOrDefault(0) ?? gw?.ToString() ?? "0.0.0.0", dns.ElementAtOrDefault(1) ?? "0.0.0.0", mtu is > 0 and < 65536 ? mtu : 1500);
                break;
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        _hostIp = info;
        Interlocked.Exchange(ref _hostIpAt, Environment.TickCount64);
        return info;
    }

    [Hle]
    int sceNetCtlInetGetInfo(int code, uint info)
    {
        if (info == 0) return ERR_NETCTL_INVALID_ADDR;
        var host = Cfg.Real ? HostIp() : null;
        switch (code)
        {
            case 1: GuestMemory.WriteCString(info, "VitaSharp", 65); break;           // configuration name
            case 2: GuestMemory.W32(info, 0); break;                                // device: wireless
            case 3: sceNetGetMacAddress(info, 0); break;                           // ether address (emulated: the host's is not exposed)
            case 4: GuestMemory.W32(info, (uint)(host?.Mtu ?? 1500)); break;        // MTU
            case 5: GuestMemory.W32(info, 1); break;                                // link up
            case 6: new byte[] { 0x00, 0x1F, 0xA7, 0x00, 0x00, 0x01 }.CopyTo(GuestMemory.Span(info, 6)); break;
            case 7: GuestMemory.WriteCString(info, "VitaSharpNet", 33); break;     // SSID
            case 8: GuestMemory.W32(info, 3); break;                                // WPA/WPA2
            case 9: GuestMemory.W32(info, 40); break;                               // rssi dBm
            case 10: GuestMemory.W32(info, 100); break;                             // rssi %
            case 11: GuestMemory.W32(info, 6); break;                               // channel
            case 12: GuestMemory.W32(info, 0); break;                               // DHCP
            case 13: GuestMemory.WriteCString(info, "", 256); break;
            case 14: GuestMemory.WriteCString(info, "", 128); break;
            case 15: GuestMemory.WriteCString(info, host?.Ip ?? "192.168.0.10", 16); break;
            case 16: GuestMemory.WriteCString(info, host?.Netmask ?? "255.255.255.0", 16); break;
            case 17: GuestMemory.WriteCString(info, host?.Gateway ?? "192.168.0.1", 16); break;
            case 18: GuestMemory.WriteCString(info, host?.Dns1 ?? "192.168.0.1", 16); break;
            case 19: GuestMemory.WriteCString(info, host?.Dns2 ?? "0.0.0.0", 16); break;
            case 20: GuestMemory.W32(info, 0); break;                               // no proxy
            case 21: GuestMemory.WriteCString(info, "", 256); break;
            case 22: GuestMemory.W32(info, 0); break;
            default: return unchecked((int)0x80412106); // SCE_NET_CTL_ERROR_INVALID_CODE
        }
        return 0;
    }

    int RegisterNetCallback(Dictionary<int, NetCallback> table, uint func, Ptr<int> cid)
    {
        if (func == 0 || cid.Address == 0) return ERR_NETCTL_INVALID_ADDR;
        lock (_inetCallbacks)
        {
            if (table.Count >= 8) return ERR_NETCTL_CALLBACK_MAX;
            int id = _nextCid++;
            table[id] = new NetCallback { Func = func, Arg = 0 };
            cid.Value = id;
            return id;
        }
    }

    [Hle]
    int sceNetCtlInetRegisterCallback(uint func, uint arg, Ptr<int> cid)
    {
        int r = RegisterNetCallback(_inetCallbacks, func, cid);
        if (r < 0) return r;
        lock (_inetCallbacks) _inetCallbacks[r].Arg = arg;
        return 0;
    }

    [Hle] int sceNetCtlInetUnregisterCallback(int cid) { lock (_inetCallbacks) _inetCallbacks.Remove(cid); return 0; }
    [Hle] int sceNetCtlGetNatInfo(uint info) { if (info != 0) { GuestMemory.Fill(info, 0, 16); GuestMemory.W32(info, 16); } return 0; }
    [Hle] int sceNetCtlAdhocGetState(Ptr<int> state) { state.TrySet(0); return 0; }
    [Hle] int sceNetCtlAdhocGetInAddr(uint addr) => ERR_NETCTL_NOT_CONNECTED;
    [Hle]
    int sceNetCtlAdhocRegisterCallback(uint func, uint arg, Ptr<int> cid)
    {
        int r = RegisterNetCallback(_adhocCallbacks, func, cid);
        if (r < 0) return r;
        lock (_inetCallbacks) _adhocCallbacks[r].Arg = arg;
        return 0;
    }

    [Hle] int sceNetCtlAdhocUnregisterCallback(int cid) { lock (_inetCallbacks) _adhocCallbacks.Remove(cid); return 0; }

    [Hle] int sceNetCtlAdhocDisconnect() => 0;

}

public sealed class Np : HleModule
{

    // NP initialisation, service state and auth live in NpManager (Np.cs); Lookup in NpUtility (NpCommunity.cs).
    [Hle] int sceNpBasicInit(uint opt) => 0;
    [Hle] int sceNpBasicTerm() => 0;
    [Hle] int sceNpBasicCheckCallback() => 0;
    [Hle] int sceNpBasicRegisterHandler(uint handlers, uint ctx, uint arg) => 0;
    [Hle] int sceNpBasicUnregisterHandler() => 0;
    [Hle] int sceNpBasicRegisterJoinGameAckHandler(uint h, uint arg) => 0;
    [Hle] int sceNpBasicUnregisterJoinGameAckHandler() => 0;
    [Hle] int sceNpBasicJoinGameAckResponseSend(uint p) => PsnAccount.Current.ServerError;
    [Hle] int sceNpBasicSendInGameDataMessage(uint to, uint data, uint size) => PsnAccount.Current.ServerError;
    [Hle] int sceNpBasicSetInGamePresence(uint p) => PsnAccount.Current.UsesLocalServices ? 0 : PsnAccount.Current.ServerError; // no friends to see it
    [Hle] int sceNpBasicGetFriendListEntryCount(Ptr<uint> count) { count.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetFriendListEntries(uint start, uint entries, uint num, Ptr<uint> retrieved) { retrieved.TrySet(0); return 0; }
    [Hle] int sceNpBasicRecordPlaySessionLog(uint npId, uint type) => 0; // the "recently played with" list lives on PSN
    [Hle] int sceNpManagerGetChatRestrictionFlag(Ptr<int> flag) { flag.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetBlockListEntryCount(Ptr<uint> count) { count.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetBlockListEntries(uint start, uint entries, uint num, Ptr<uint> retrieved) { retrieved.TrySet(0); return 0; }

    [Hle] int sceNpPartyInit(uint p) => 0;
    [Hle] int sceNpPartyTerm() => 0;
    [Hle] int sceNpPartyCheckCallback() => 0;
    [Hle] int sceNpPartyRegisterHandler(uint h, uint arg) => 0;
    [Hle] int sceNpPartyGetMembers(uint members) => unchecked((int)0x80553402);
    [Hle] int sceNpPartyGetMemberInfo(uint id, uint info) => unchecked((int)0x80553402);
    [Hle] int sceNpPartyGetMemberVoiceInfo(uint id, uint info) => unchecked((int)0x80553402);

    [Hle] int sceNpBasicRegisterInGameDataMessageHandler(uint handler, uint arg) => 0; // messages only arrive from PSN
    [Hle] int sceNpBasicUnregisterInGameDataMessageHandler() => 0;

}
