using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceNetPsForDriver, the network driver side (os0:kd/bootimage.skprx SceNetPs 3.74, read in the module, study only):
// a driver (SceWlanBt, SceBt, SceUsbEther*, SceBbmc) attaches its interface object to the stack, registers one packet
// hook and one kernel gateway, and undoes each. The stack keeps the attached interfaces in a doubly linked list
// (+0 next, +4 previous), the hook and the gateway in a slot each; it writes its own entry points into the objects
// and the driver calls them (packet buffers, receive, events, MTU; the hook's operations; the gateway's buffers and
// send). The emulated network is the host's sockets (NetSockets.cs): an attached interface carries no traffic of its
// own, so what the stack would route through it (its receive queue, its send queue) stays empty, and a packet given
// to the stack is offered to the gateway and then dropped.
public sealed partial class Net
{
    /// <summary>SceNetPs: an invalid argument, or a driver object that is not registered (0x80418101).</summary>
    const int NetPsErrInvalid = unchecked((int)0x80418101);

    /// <summary>The kernel gateway's socket could not be opened (0x80418105).</summary>
    const int NetPsErrGatewaySocket = unchecked((int)0x80418105);

    /// <summary>The gateway's socket options could not be set (0x80418106; also the slot missing before the stack
    /// starts, which never happens here: the stack is up from boot).</summary>
    const int NetPsErrGatewaySetup = unchecked((int)0x80418106);

    /// <summary>A gateway send with no gateway object (0x80418107) or through one that is not the registered one
    /// (0x80418108).</summary>
    const int NetPsErrGatewayNull = unchecked((int)0x80418107);

    const int NetPsErrGatewayNotRegistered = unchecked((int)0x80418108);

    /// <summary>A hook operation through no hook (0x80418109) or through one that is not the registered one
    /// (0x8041810A); an unknown operation (0x8041810B); an argument size or field out of range (0x8041810C); an
    /// unknown interface kind (0x8041810D); no such interface attached (0x8041810E).</summary>
    const int NetPsErrHookNull = unchecked((int)0x80418109);

    const int NetPsErrHookNotRegistered = unchecked((int)0x8041810A);

    const int NetPsErrHookOperation = unchecked((int)0x8041810B);

    const int NetPsErrHookArgument = unchecked((int)0x8041810C);

    const int NetPsErrHookKind = unchecked((int)0x8041810D);

    const int NetPsErrHookNoInterface = unchecked((int)0x8041810E);

    // The driver's interface object.
    const uint InterfaceNext = 0x00;
    const uint InterfacePrevious = 0x04;
    const uint InterfaceStackEntries = 0x08;
    const uint InterfaceStackArea = 0x30;
    const uint InterfaceInternal = 0x34;
    const uint InterfaceInit = 0x38;
    const uint InterfaceTerm = 0x3C;
    const uint InterfaceCapabilities = 0x54;
    const uint InterfaceKind = 0x58;
    const uint InterfaceMtu = 0x64;
    const uint InterfaceFlags = 0x68;

    /// <summary>The callbacks a driver must give (+0x40, +0x44, +0x48, +0x50).</summary>
    static readonly uint[] RequiredInterfaceCallbacks = [0x40, 0x44, 0x48, 0x50];

    // The stack's two internal objects of an interface: the interface record (0x360) and its link-level record
    // (0x1F8), which holds the name the stack lists the interface by.
    const uint InterfaceRecordSize = 0x360;
    const uint LinkRecordSize = 0x1F8;
    const uint LinkRecordName = 0x14;
    const uint LinkRecordNameSize = 0x10;
    const uint LinkRecordMtu = 0x40;

    /// <summary>The 0x200-byte configuration record of an interface (hook operations 1 and 2) in its interface
    /// record.</summary>
    const uint InterfaceRecordConfig = 0x154;
    const uint InterfaceConfigSize = 0x200;

    /// <summary>The largest MTU the stack keeps (Ethernet's 1500).</summary>
    const uint NetPsMaxMtu = 0x5DC;

    /// <summary>The attached interface objects, in list order.</summary>
    [SnapshotMissing] // states of builds before it: no driver could attach one
    readonly List<uint> _netPsInterfaces = new();

    /// <summary>The registered packet hook object (0 = none).</summary>
    [SnapshotMissing] // states of builds before it: no driver could register one
    uint _netPsHook;

    /// <summary>The registered kernel gateway object (0 = none) and its raw socket.</summary>
    [SnapshotMissing] // states of builds before it: no driver could register one
    uint _netPsGateway;

    [SnapshotMissing] // states of builds before it: no driver could register one
    int _netPsGatewaySocket;

    [SnapshotIgnore] // host stubs, made again on first use
    HleGuestStubs? _netPsStubs;

    HleGuestStubs NetPsStubs => _netPsStubs ??= new HleGuestStubs(this, "SceNetPs");

    /// <summary>The stack heap (the module's allocator): kernel memory, at least word aligned.</summary>
    uint NetPsAlloc(uint size, uint align, string what)
    {
        uint alignment = align == 0 || (align & (align - 1)) != 0 ? 8u : align;
        uint block = Emu.Memory.Alloc(size, "SceNetPs " + what, MemoryRegion.Kernel, alignment);
        GuestMemory.Fill(block, 0, size);
        return block;
    }

    void NetPsFree(uint block)
    {
        if (block != 0)
        {
            Emu.Memory.Free(block);
        }
    }

    // ------------------------------------------------------------------ interfaces

    /// <summary>The interface names by kind: the stack numbers the '*' with the interface's unit.</summary>
    static string InterfaceNameTemplate(uint kind)
    {
        switch (kind)
        {
            case 1:
                return "eth*";
            case 2:
                return "wlan*";
            case 3:
                return "bt*";
            case 4:
                return "phone*";
            default:
                return "other*";
        }
    }

    /// <summary>NID_1ABF937D (SceNetPsForDriver)(ifnet *ifp): attaches a driver's interface. The driver gives its
    /// callbacks (+0x40, +0x44, +0x48, +0x50) and no reserved flag (+0x68 bits 0-1); an interface already attached
    /// answers 0. The unit is the lowest of 0-7 no attached interface of the same kind (+0x58) uses; the driver's init
    /// (+0x38, with the stack's two context words) runs first and its error is returned; then the stack writes its
    /// nine entry points (+0x08..+0x28), makes the interface record and the link record (+0x34), names the interface
    /// by kind and unit and appends it to the list.</summary>
    [Hle("NID_1ABF937D", Nid = 0x1ABF937D, Library = "SceNetPsForDriver")]
    int NetPsAttachInterface(uint interfaceObject)
    {
        if (interfaceObject == 0)
        {
            return NetPsErrInvalid;
        }

        foreach (uint offset in RequiredInterfaceCallbacks)
        {
            if (GuestMemory.R32(interfaceObject + offset) == 0)
            {
                return NetPsErrInvalid;
            }
        }

        if ((GuestMemory.R32(interfaceObject + InterfaceFlags) & 3) != 0)
        {
            return NetPsErrInvalid;
        }

        uint kind = GuestMemory.R32(interfaceObject + InterfaceKind);
        int unit;
        lock (_netLock)
        {
            if (_netPsInterfaces.Contains(interfaceObject))
            {
                return 0;
            }

            GuestMemory.Fill(interfaceObject + InterfaceStackEntries, 0, InterfaceStackArea);
            unit = FreeInterfaceUnit(kind);
        }

        if (unit < 0)
        {
            return NetPsErrInvalid;
        }

        uint init = GuestMemory.R32(interfaceObject + InterfaceInit);
        if (init != 0)
        {
            // The stack's context words (its start arguments) are not modelled: the driver gets 0 for both.
            int initResult = (int)GuestThread.Current!.CallGuest(init, interfaceObject, 0, 0);
            if (initResult < 0)
            {
                return initResult;
            }
        }

        WriteInterfaceEntries(interfaceObject);
        uint interfaceRecord = NetPsAlloc(InterfaceRecordSize, 8, "interface record");
        uint linkRecord = NetPsAlloc(LinkRecordSize, 8, "link record");
        GuestMemory.W32(interfaceRecord, interfaceObject);
        GuestMemory.W32(interfaceRecord + 4, 1u << unit);
        GuestMemory.W32(interfaceRecord + 0x30, linkRecord);
        GuestMemory.W32(linkRecord, interfaceRecord);
        GuestMemory.W32(linkRecord + 0x30, 0x8802);
        GuestMemory.W32(linkRecord + LinkRecordMtu, Math.Min(GuestMemory.R32(interfaceObject + InterfaceMtu), NetPsMaxMtu));
        WriteLinkCapabilities(linkRecord, GuestMemory.R32(interfaceObject + InterfaceCapabilities));
        string name = InterfaceNameTemplate(kind).Replace("*", unit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        GuestMemory.WriteCString(linkRecord + LinkRecordName, name, (int)LinkRecordNameSize);
        bool hasLink = kind is 1 or 2;
        GuestMemory.W32(interfaceRecord + 0x118, uint.MaxValue);
        GuestMemory.W32(interfaceRecord + 0x134, 0x3C);
        GuestMemory.W32(interfaceRecord + 0x138, hasLink ? 1u : 0u);
        if (hasLink)
        {
            GuestMemory.W32(interfaceRecord + 0x140, 4);
        }

        GuestMemory.W32(interfaceObject + InterfaceInternal, interfaceRecord);
        lock (_netLock)
        {
            uint tail = _netPsInterfaces.Count == 0 ? 0 : _netPsInterfaces[^1];
            GuestMemory.W32(interfaceObject + InterfacePrevious, tail);
            if (tail != 0)
            {
                GuestMemory.W32(tail + InterfaceNext, interfaceObject);
            }

            GuestMemory.W32(interfaceObject + InterfaceNext, 0);
            _netPsInterfaces.Add(interfaceObject);
        }

        return 0;
    }

    /// <summary>The lowest unit 0-7 that no attached interface of <paramref name="kind"/> uses (-1 if all are taken;
    /// the net lock held).</summary>
    int FreeInterfaceUnit(uint kind)
    {
        uint used = 0;
        foreach (uint attached in _netPsInterfaces)
        {
            if (GuestMemory.R32(attached + InterfaceKind) == kind)
            {
                used |= GuestMemory.R32(GuestMemory.R32(attached + InterfaceInternal) + 4);
            }
        }

        for (int unit = 0; unit < 8; unit++)
        {
            if ((used & (1u << unit)) == 0)
            {
                return unit;
            }
        }

        return -1;
    }

    /// <summary>The link record's capability words (+0x158 receive, +0x15C transmit) from the driver's capability
    /// bits (+0x54): bits 20 / 21 checksum offloads, 18 / 19 their second form, 16 / 17 the third.</summary>
    static void WriteLinkCapabilities(uint linkRecord, uint capabilities)
    {
        uint receive = 0;
        uint transmit = 0;
        if ((capabilities & (1u << 20)) != 0)
        {
            receive |= 0x11;
        }

        if ((capabilities & (1u << 18)) != 0)
        {
            receive |= 0x22;
        }

        if ((capabilities & (1u << 16)) != 0)
        {
            receive |= 0x40;
        }

        if ((capabilities & (1u << 21)) != 0)
        {
            transmit |= 0x11;
        }

        if ((capabilities & (1u << 19)) != 0)
        {
            transmit |= 0x22;
        }

        if ((capabilities & (1u << 17)) != 0)
        {
            transmit |= 0x40;
        }

        GuestMemory.W32(linkRecord + 0x158, receive);
        GuestMemory.W32(linkRecord + 0x15C, transmit);
    }

    /// <summary>The stack's nine entry points a driver calls through its interface object (+0x08..+0x28).</summary>
    void WriteInterfaceEntries(uint interfaceObject)
    {
        uint[] entries =
        [
            NetPsStubs.Host("interface alloc", context =>
            {
                context.Return(NetPsAlloc(context.Gpr(1), context.Gpr(2), "driver buffer"));
            }),
            NetPsStubs.Host("interface free", context =>
            {
                NetPsFree(context.Gpr(1));
            }),
            NetPsStubs.Host("interface packet alloc", context =>
            {
                context.Return(context.Gpr(0) == 0 ? 0 : AllocatePacket(context.Gpr(0), context.Gpr(1), context.Gpr(2)));
            }),
            NetPsStubs.Host("interface packet segment", context =>
            {
                context.Return(AddPacketSegment(context.Gpr(1), context.Gpr(2), context.Gpr(3)));
            }),
            NetPsStubs.Host("interface packet free", context =>
            {
                FreePacket(context.Gpr(1));
            }),
            NetPsStubs.Host("interface input", context =>
            {
                InterfaceInput(context.Gpr(0), context.Gpr(1));
            }),
            NetPsStubs.Host("interface dequeue", context =>
            {
                // The emulated stack sends through host sockets: an interface's send queue is always empty.
                context.Return(0);
            }),
            NetPsStubs.Host("interface event", context =>
            {
                context.Return(InterfaceEvent(context.Gpr(0), context.Gpr(2), context.Gpr(3), HleContext.ArgStack(context, 0), HleContext.ArgStack(context, 4)));
            }),
            NetPsStubs.Host("interface set mtu", context =>
            {
                context.Return(InterfaceSetMtu(context.Gpr(0), context.Gpr(1)));
            }),
        ];
        for (int i = 0; i < entries.Length; i++)
        {
            GuestMemory.W32(interfaceObject + InterfaceStackEntries + (uint)i * 4, entries[i]);
        }
    }

    // A packet: one buffer with the data area (after the interface's headroom, +0x68 of the interface object) and,
    // at its end, the 0x40-byte header: +0x0C the buffer's size, +0x20 the first segment, +0x24 / +0x28 the queue
    // links; a segment (0x14 bytes) is +0 its buffer, +4 the end of the data area, +8 its start, +0x0C the read
    // position, +0x10 the next segment.
    const uint PacketHeaderSize = 0x40;
    const uint PacketFirstSegment = 0x20;
    const uint PacketSegmentArea = 0x2C;
    const uint SegmentSize = 0x14;
    const uint SegmentNext = 0x10;

    static uint AlignTo(uint value, uint align)
    {
        return (value + align - 1) & ~(align - 1);
    }

    /// <summary>The packet-alloc entry (interface, data size, alignment): the stack's packets hold at most 0x640
    /// bytes; the alignment is a multiple of 4 (else 4).</summary>
    uint AllocatePacket(uint interfaceObject, uint size, uint align)
    {
        uint alignment = align == 0 || (align & 3) != 0 ? 4u : align;
        uint headroom = GuestMemory.R32(interfaceObject + InterfaceFlags);
        uint headerOffset = AlignTo(size, alignment) + headroom;
        uint total = headerOffset + PacketHeaderSize;
        uint buffer = NetPsAlloc(total, alignment, "packet");
        uint header = buffer + headerOffset;
        GuestMemory.W32(header + 0x0C, total);
        uint segment = header + PacketSegmentArea;
        GuestMemory.W32(header + PacketFirstSegment, segment);
        GuestMemory.W32(segment, buffer);
        GuestMemory.W32(segment + 4, buffer + size + headroom);
        GuestMemory.W32(segment + 8, buffer + headroom);
        GuestMemory.W32(segment + 0x0C, buffer + headroom);
        return header;
    }

    /// <summary>The segment entry (interface, packet, size, alignment): a new segment appended to the packet's
    /// chain.</summary>
    uint AddPacketSegment(uint packet, uint size, uint align)
    {
        uint alignment = align == 0 || (align & (align - 1)) != 0 ? 4u : align;
        uint dataSize = AlignTo(size, alignment);
        uint buffer = NetPsAlloc(dataSize + SegmentSize, alignment, "packet segment");
        uint segment = buffer + dataSize;
        GuestMemory.W32(segment, buffer);
        GuestMemory.W32(segment + 4, buffer + size);
        GuestMemory.W32(segment + 8, buffer);
        GuestMemory.W32(segment + 0x0C, buffer);
        uint last = packet == 0 ? 0 : GuestMemory.R32(packet + PacketFirstSegment);
        if (last != 0)
        {
            while (GuestMemory.R32(last + SegmentNext) != 0)
            {
                last = GuestMemory.R32(last + SegmentNext);
            }

            GuestMemory.W32(last + SegmentNext, segment);
        }

        return segment;
    }

    /// <summary>Frees a packet: its added segments, then its own buffer (which holds the header).</summary>
    void FreePacket(uint packet)
    {
        if (packet == 0)
        {
            return;
        }

        uint first = GuestMemory.R32(packet + PacketFirstSegment);
        if (first == 0)
        {
            return;
        }

        uint segment = GuestMemory.R32(first + SegmentNext);
        while (segment != 0)
        {
            uint next = GuestMemory.R32(segment + SegmentNext);
            NetPsFree(GuestMemory.R32(segment));
            segment = next;
        }

        NetPsFree(GuestMemory.R32(first));
    }

    /// <summary>The receive entry (interface, packet): the registered gateway's filter (+0x14 of the gateway object,
    /// called with the gateway, the interface and the packet) sees it first; anything but 1 means the gateway kept or
    /// dropped it and the stack frees it. The stack's own receive queue is not modelled (nothing in the emulated stack
    /// would read it), so the packet is freed either way.</summary>
    void InterfaceInput(uint interfaceObject, uint packet)
    {
        uint gateway;
        lock (_netLock)
        {
            gateway = _netPsGateway;
        }

        if (gateway != 0)
        {
            GuestThread.Current!.CallGuest(GuestMemory.R32(gateway + 0x14), gateway, interfaceObject, packet);
        }

        FreePacket(packet);
    }

    /// <summary>The event entry (interface, unused, events, link events, value*, value size): the interface record
    /// collects the events (+0x58) and the link events (+0x5C); a link event 8 with no event and a 4-byte value also
    /// stores the value (+0x60).</summary>
    static int InterfaceEvent(uint interfaceObject, uint events, uint linkEvents, uint value, uint valueSize)
    {
        if (interfaceObject == 0 || linkEvents == 0)
        {
            return 0;
        }

        uint interfaceRecord = GuestMemory.R32(interfaceObject + InterfaceInternal);
        if (interfaceRecord == 0)
        {
            return 0;
        }

        GuestMemory.W32(interfaceRecord + 0x58, GuestMemory.R32(interfaceRecord + 0x58) | events);
        GuestMemory.W32(interfaceRecord + 0x5C, GuestMemory.R32(interfaceRecord + 0x5C) | linkEvents);
        if (value != 0 && linkEvents == 8 && events == 0 && valueSize == 4)
        {
            GuestMemory.W32(interfaceRecord + 0x60, GuestMemory.R32(value));
        }

        return 0;
    }

    /// <summary>The MTU entry (interface, mtu): the link record's MTU (+0x40, high word +0x44 cleared).</summary>
    static int InterfaceSetMtu(uint interfaceObject, uint mtu)
    {
        if (interfaceObject == 0)
        {
            return NetPsErrInvalid;
        }

        uint linkRecord = GuestMemory.R32(GuestMemory.R32(interfaceObject + InterfaceInternal) + 0x30);
        GuestMemory.W32(linkRecord + LinkRecordMtu, mtu);
        GuestMemory.W32(linkRecord + LinkRecordMtu + 4, 0);
        return 0;
    }

    /// <summary>NID_F5788FF8 (SceNetPsForDriver)(ifnet *ifp): detaches an attached interface: out of the list, its
    /// internal objects freed, the driver's term (+0x3C) called, the stack's area (+0x08..+0x37) cleared; an object
    /// that is not attached (NULL too) answers 0x80418101.</summary>
    [Hle("NID_F5788FF8", Nid = 0xF5788FF8, Library = "SceNetPsForDriver")]
    int NetPsDetachInterface(uint interfaceObject)
    {
        lock (_netLock)
        {
            int index = interfaceObject == 0 ? -1 : _netPsInterfaces.IndexOf(interfaceObject);
            if (index < 0)
            {
                return NetPsErrInvalid;
            }

            uint next = GuestMemory.R32(interfaceObject + InterfaceNext);
            uint previous = GuestMemory.R32(interfaceObject + InterfacePrevious);
            if (next != 0)
            {
                GuestMemory.W32(next + InterfacePrevious, previous);
            }

            if (previous != 0)
            {
                GuestMemory.W32(previous + InterfaceNext, next);
            }

            _netPsInterfaces.RemoveAt(index);
        }

        uint interfaceRecord = GuestMemory.R32(interfaceObject + InterfaceInternal);
        if (interfaceRecord != 0)
        {
            NetPsFree(GuestMemory.R32(interfaceRecord + 0x30));
            NetPsFree(interfaceRecord);
        }

        GuestMemory.W32(interfaceObject + InterfaceInternal, 0);
        uint term = GuestMemory.R32(interfaceObject + InterfaceTerm);
        if (term != 0)
        {
            GuestThread.Current!.CallGuest(term, interfaceObject, 0);
        }

        GuestMemory.Fill(interfaceObject + InterfaceStackEntries, 0, InterfaceStackArea);
        return 0;
    }

    /// <summary>The attached interface named <paramref name="name"/>' interface record (0 if none).</summary>
    uint FindInterfaceRecord(string name)
    {
        lock (_netLock)
        {
            foreach (uint attached in _netPsInterfaces)
            {
                uint interfaceRecord = GuestMemory.R32(attached + InterfaceInternal);
                uint linkRecord = GuestMemory.R32(interfaceRecord + 0x30);
                if (GuestMemory.ReadCString(linkRecord + LinkRecordName, (int)LinkRecordNameSize) == name)
                {
                    return interfaceRecord;
                }
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ packet hook

    /// <summary>NID_83A12CCC (SceNetPsForDriver)(hook *hook): registers the packet hook (one slot; taken, or NULL:
    /// 0x80418101) and writes the stack's two entry points into it: +0 the operations (locked), +4 the same ones for a
    /// caller that already holds the stack lock. Flags (+8): bit 0 = do not take the stack lock, bit 1 = wait at most
    /// 3 s for it (0x8041810F on timeout, which the emulated stack's lock never reaches).</summary>
    [Hle("NID_83A12CCC", Nid = 0x83A12CCC, Library = "SceNetPsForDriver")]
    int NetPsRegisterHook(uint hook)
    {
        if (hook == 0)
        {
            return NetPsErrInvalid;
        }

        lock (_netLock)
        {
            if (_netPsHook != 0)
            {
                return NetPsErrInvalid;
            }

            _netPsHook = hook;
        }

        uint operations = NetPsStubs.Host("hook operations", context =>
        {
            context.Return(HookOperation(context.Gpr(0), context.Gpr(1), context.Gpr(2), context.Gpr(3), HleContext.ArgStack(context, 0), HleContext.ArgStack(context, 4)));
        });
        GuestMemory.W32(hook, operations);
        GuestMemory.W32(hook + 4, operations);
        return 0;
    }

    /// <summary>NID_C000CA8F (SceNetPsForDriver)(hook *hook): unregisters the packet hook; NULL or a hook that is not
    /// the registered one answers 0x80418101.</summary>
    [Hle("NID_C000CA8F", Nid = 0xC000CA8F, Library = "SceNetPsForDriver")]
    int NetPsUnregisterHook(uint hook)
    {
        if (hook == 0)
        {
            return NetPsErrInvalid;
        }

        lock (_netLock)
        {
            if (_netPsHook != hook)
            {
                return NetPsErrInvalid;
            }

            _netPsHook = 0;
            return 0;
        }
    }

    /// <summary>The hook's operations (hook, operation, then four arguments): 1 read and 2 write an Ethernet or
    /// Wi-Fi interface's configuration record, 3 the stack's four counters, 4 an interface's status, 5 the stack's
    /// neighbour list, 6 the stack's DNS servers.</summary>
    int HookOperation(uint hook, uint operation, uint first, uint second, uint third, uint fourth)
    {
        uint registered;
        lock (_netLock)
        {
            registered = _netPsHook;
        }

        if (registered != hook)
        {
            return NetPsErrHookNotRegistered;
        }

        if (registered == 0)
        {
            return NetPsErrHookNull;
        }

        switch (operation)
        {
            case 1:
                return HookReadConfig(first, second, third, fourth);
            case 2:
                return HookWriteConfig(first, second, fourth);
            case 3:
                return HookReadCounters(second, third, fourth);
            case 4:
                return HookReadStatus(first, second, third, fourth);
            case 5:
                return HookReadNeighbours(first, second, third, fourth);
            case 6:
                return HookReadDns(second, third, fourth);
            default:
                return NetPsErrHookOperation;
        }
    }

    /// <summary>The interface a hook request names by kind (+0 of the request): 1 lo0, 2 eth0, 3 wlan0, 4 bt0,
    /// 5 phone0, 6 pppoe.</summary>
    static string? HookInterfaceName(uint kind)
    {
        switch (kind)
        {
            case 1:
                return "lo0";
            case 2:
                return "eth0";
            case 3:
                return "wlan0";
            case 4:
                return "bt0";
            case 5:
                return "phone0";
            case 6:
                return "pppoe";
            default:
                return null;
        }
    }

    /// <summary>Operation 1 (request {kind, index}, size, out, out size): out = {kind, 0x200, record}: the
    /// configuration record of eth0 or wlan0 (index 0; above 4 the stack's EINVAL).</summary>
    int HookReadConfig(uint request, uint requestSize, uint output, uint outputSize)
    {
        if (request == 0)
        {
            return NetPsErrInvalid;
        }

        if (requestSize < 8)
        {
            return NetPsErrHookArgument;
        }

        if (output == 0)
        {
            return NetPsErrInvalid;
        }

        if (outputSize < InterfaceConfigSize + 8)
        {
            return NetPsErrHookArgument;
        }

        GuestMemory.Fill(output, 0, InterfaceConfigSize + 8);
        uint index = GuestMemory.R32(request + 4) & 0xFFFF;
        uint kind = GuestMemory.R32(request);
        if (kind is not (2 or 3))
        {
            return NetPsErrHookKind;
        }

        uint interfaceRecord = FindInterfaceRecord(HookInterfaceName(kind)!);
        if (interfaceRecord == 0)
        {
            return NetPsErrHookNoInterface;
        }

        if (index > 4)
        {
            return EINVAL;
        }

        // Entry 0 is the record itself (it overwrites the index word); entries 1-4 are views of the same record in the
        // emulated stack, which keeps only one, so they keep the index.
        GuestMemory.ReadSpan(interfaceRecord + InterfaceRecordConfig, (int)InterfaceConfigSize).CopyTo(GuestMemory.Span(output + 8, (int)InterfaceConfigSize));
        if (index != 0)
        {
            GuestMemory.Write<ushort>(output + 10, (ushort)index);
        }

        GuestMemory.Write<ushort>(output + 12, 0x104);
        GuestMemory.W32(output, kind);
        GuestMemory.W32(output + 4, InterfaceConfigSize);
        return (int)(InterfaceConfigSize + 8);
    }

    /// <summary>Operation 2 (request {kind, index, 0x200, record}, size, must be 0): stores the configuration record
    /// of eth0 or wlan0.</summary>
    int HookWriteConfig(uint request, uint requestSize, uint mustBeZero)
    {
        if (request == 0)
        {
            return NetPsErrInvalid;
        }

        if (requestSize < InterfaceConfigSize + 0x0C || mustBeZero != 0 || GuestMemory.R32(request + 8) != InterfaceConfigSize)
        {
            return NetPsErrHookArgument;
        }

        GuestMemory.Write<ushort>(request + 0x0E, (ushort)GuestMemory.R32(request + 4));
        uint kind = GuestMemory.R32(request);
        if (kind is not (2 or 3))
        {
            return NetPsErrHookKind;
        }

        uint interfaceRecord = FindInterfaceRecord(HookInterfaceName(kind)!);
        if (interfaceRecord == 0)
        {
            return NetPsErrHookNoInterface;
        }

        GuestMemory.ReadSpan(request + 0x0C, (int)InterfaceConfigSize).CopyTo(GuestMemory.Span(interfaceRecord + InterfaceRecordConfig, (int)InterfaceConfigSize));
        return 0;
    }

    /// <summary>Operation 3 (must be 0, out, out size): the stack's four counters (the two buffer pools, the packets
    /// in use and the stack-wide count), all zero in the emulated stack, which keeps no packets of its own.</summary>
    static int HookReadCounters(uint mustBeZero, uint output, uint outputSize)
    {
        if (mustBeZero != 0)
        {
            return NetPsErrHookArgument;
        }

        if (output == 0)
        {
            return NetPsErrInvalid;
        }

        if (outputSize < 0x10)
        {
            return NetPsErrHookArgument;
        }

        GuestMemory.Fill(output, 0, 0x10);
        return 0x10;
    }

    /// <summary>Operation 4 (request {kind}, size, out, out size): out = {kind, 0x140, status}; the status record of
    /// an attached interface is not modelled beyond the header (zero).</summary>
    int HookReadStatus(uint request, uint requestSize, uint output, uint outputSize)
    {
        if (request == 0)
        {
            return NetPsErrInvalid;
        }

        if (requestSize < 4)
        {
            return NetPsErrHookArgument;
        }

        if (output == 0)
        {
            return NetPsErrInvalid;
        }

        if (outputSize < 0x148)
        {
            return NetPsErrHookArgument;
        }

        uint kind = GuestMemory.R32(request);
        string? name = HookInterfaceName(kind);
        if (name == null)
        {
            return NetPsErrHookKind;
        }

        if (FindInterfaceRecord(name) == 0)
        {
            return NetPsErrHookNoInterface;
        }

        GuestMemory.Fill(output, 0, 0x148);
        GuestMemory.W32(output, kind);
        GuestMemory.W32(output + 4, 0x140);
        return 0x148;
    }

    /// <summary>Operation 5 (request {kind}, size, out, out size): out = {kind, count, 0x6C, entries}; the emulated
    /// stack learns no neighbours, so the count is 0.</summary>
    static int HookReadNeighbours(uint request, uint requestSize, uint output, uint outputSize)
    {
        if (request == 0)
        {
            return NetPsErrInvalid;
        }

        if (requestSize < 4)
        {
            return NetPsErrHookArgument;
        }

        if (output == 0)
        {
            return NetPsErrInvalid;
        }

        if (outputSize < 0x0C)
        {
            return NetPsErrHookArgument;
        }

        GuestMemory.W32(output, GuestMemory.R32(request));
        GuestMemory.W32(output + 4, 0);
        GuestMemory.W32(output + 8, 0x6C);
        return 0x0C;
    }

    /// <summary>Operation 6 (must be 0, out, out size): the first 0x0C bytes of the stack's DNS record, the two
    /// server addresses in host order.</summary>
    int HookReadDns(uint mustBeZero, uint output, uint outputSize)
    {
        if (mustBeZero != 0)
        {
            return NetPsErrHookArgument;
        }

        if (output == 0)
        {
            return NetPsErrInvalid;
        }

        if (outputSize < 0x0C)
        {
            return NetPsErrHookArgument;
        }

        lock (_netLock)
        {
            _stackDns.AsSpan(0, 0x0C).CopyTo(GuestMemory.Span(output, 0x0C));
        }

        GuestMemory.W32(output, System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(GuestMemory.R32(output)));
        GuestMemory.W32(output + 4, System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(GuestMemory.R32(output + 4)));
        return 0x0C;
    }

    // ------------------------------------------------------------------ kernel gateway

    /// <summary>NID_3D4A1D74 (SceNetPsForDriver)(gateway *gateway): registers the kernel gateway. It needs its two
    /// callbacks (+0x14 the receive filter, +0x18), else 0x80418101; the stack opens a raw IPv4 socket
    /// "SceNetKernelGateway" (failure 0x80418105) and sets the options (0, 2), (SOL_SOCKET, 0x1002) and (0, 0x19)
    /// to 1 (failure 0x80418106); a gateway already registered answers 0x80418101 (the socket is closed). Then
    /// it writes the gateway's entries: +0 alloc, +4 free, +8 send, +0x0C 40000, +0x10 0x23C0.</summary>
    [Hle("NID_3D4A1D74", Nid = 0x3D4A1D74, Library = "SceNetPsForDriver")]
    int NetPsRegisterGateway(uint gateway)
    {
        if (gateway == 0 || GuestMemory.R32(gateway + 0x14) == 0 || GuestMemory.R32(gateway + 0x18) == 0)
        {
            return NetPsErrInvalid;
        }

        int socket = sceNetSocket("SceNetKernelGateway", 2, SOCK_RAW, 0);
        if (socket < 0)
        {
            return NetPsErrGatewaySocket;
        }

        byte[] one = [1, 0, 0, 0];
        if (SetSocketOption(socket, 0, 2, one) < 0 || SetSocketOption(socket, SOL_SOCKET, 0x1002, one) < 0 || SetSocketOption(socket, 0, 0x19, one) < 0)
        {
            sceNetSocketClose(socket);
            return NetPsErrGatewaySetup;
        }

        lock (_netLock)
        {
            if (_netPsGateway != 0)
            {
                sceNetSocketClose(socket);
                return NetPsErrInvalid;
            }

            _netPsGateway = gateway;
            _netPsGatewaySocket = socket;
        }

        GuestMemory.W32(gateway, NetPsStubs.Host("gateway alloc", context =>
        {
            context.Return(NetPsAlloc(context.Gpr(1), context.Gpr(2), "gateway buffer"));
        }));
        GuestMemory.W32(gateway + 4, NetPsStubs.Host("gateway free", context =>
        {
            NetPsFree(context.Gpr(1));
        }));
        GuestMemory.W32(gateway + 8, NetPsStubs.Host("gateway send", context =>
        {
            context.Return(GatewaySend(context.Gpr(0), context.Gpr(1), context.Gpr(2), context.Gpr(3)));
        }));
        GuestMemory.W32(gateway + 0x0C, 40000);
        GuestMemory.W32(gateway + 0x10, 0x23C0);
        return 0;
    }

    /// <summary>NID_8C4346CA (SceNetPsForDriver)(gateway *gateway): unregisters the kernel gateway and closes its
    /// socket; a gateway that is not the registered one answers 0x80418101 (with none registered, NULL is the
    /// registered one and answers 0).</summary>
    [Hle("NID_8C4346CA", Nid = 0x8C4346CA, Library = "SceNetPsForDriver")]
    int NetPsUnregisterGateway(uint gateway)
    {
        int socket;
        lock (_netLock)
        {
            if (_netPsGateway != gateway)
            {
                return NetPsErrInvalid;
            }

            if (gateway == 0)
            {
                return 0;
            }

            _netPsGateway = 0;
            socket = _netPsGatewaySocket;
            _netPsGatewaySocket = 0;
        }

        if (socket >= 0)
        {
            sceNetSocketClose(socket);
        }

        return 0;
    }

    /// <summary>The gateway's send entry (gateway, buffer, length, IPv4 address): a sendto on its raw socket to
    /// {address, port 0}.</summary>
    int GatewaySend(uint gateway, uint buffer, uint length, uint address)
    {
        int socket;
        lock (_netLock)
        {
            if (_netPsGateway != gateway)
            {
                return NetPsErrGatewayNotRegistered;
            }

            if (gateway == 0)
            {
                return NetPsErrGatewayNull;
            }

            socket = _netPsGatewaySocket;
        }

        uint sockaddr = NetPsAlloc(0x10, 8, "gateway sockaddr");
        try
        {
            GuestMemory.Write<byte>(sockaddr, 0x10);
            GuestMemory.Write<byte>(sockaddr + 1, 2);
            GuestMemory.W32(sockaddr + 4, address);
            return sceNetSendto(socket, buffer, length, 0, sockaddr, 0x10);
        }
        finally
        {
            NetPsFree(sockaddr);
        }
    }

    // ------------------------------------------------------------------ other

    /// <summary>NID_E41C0794 (SceNetPsForDriver)(): empty in the module; 0.</summary>
    [Hle("NID_E41C0794", Nid = 0xE41C0794, Library = "SceNetPsForDriver")]
    int NetPsNop()
    {
        return 0;
    }
}
