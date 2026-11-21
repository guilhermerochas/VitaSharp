using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceNetPsForDriver (SceNetPs 3.74), the network driver side: a driver attaches its interface object (validated,
/// numbered by kind, linked, given the stack's entry points and named), registers one packet hook (whose operations
/// it then calls) and one kernel gateway (a raw socket plus alloc / free / send entries), and undoes each; with
/// nothing registered the undo calls answer 0x80418101. ksceNetClose is the socket close; NID_E41C0794 is empty.
/// </summary>
[Collection("guest")]
public sealed class NetPsKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80418101);
    private const int HookNotRegistered = unchecked((int)0x8041810A);
    private const int HookOperation = unchecked((int)0x8041810B);
    private const int HookArgument = unchecked((int)0x8041810C);
    private const int HookKind = unchecked((int)0x8041810D);
    private const int HookNoInterface = unchecked((int)0x8041810E);
    private const int GatewayNotRegistered = unchecked((int)0x80418108);

    private readonly HleHarness _harness = new(fixture, "netpsk");

    private readonly List<(uint Object, uint Second, uint Third)> _initCalls = new();
    private readonly List<uint> _termCalls = new();
    private readonly List<(uint Gateway, uint Interface, uint Packet)> _filterCalls = new();
    private int _initResult;

    public void Dispose() => _harness.Dispose();

    private Net Net => _harness.Module<Net>();

    /// <summary>A guest address that runs <paramref name="body"/> (an HLE host stub) and returns its result.</summary>
    private uint Stub(string name, Func<HleContext, uint> body)
    {
        uint stub = _harness.Alloc(16);
        Core.Loader.Linker.WriteHleStub(stub, _harness.Emu.Hle.RegisterHostFunction(name, context => context.Return(body(context))), 0);
        _harness.Emu.Code.Invalidate(stub, 16);
        return stub;
    }

    /// <summary>A driver's interface object of <paramref name="kind"/> with every callback and an MTU.</summary>
    private uint NewInterface(uint kind, uint mtu = 1500)
    {
        uint interfaceObject = _harness.Alloc(0x80);
        uint callback = Stub("netpsk callback", _ => 0);
        GuestMemory.W32(interfaceObject + 0x38, Stub("netpsk init", context =>
        {
            _initCalls.Add((context.Gpr(0), context.Gpr(1), context.Gpr(2)));
            return unchecked((uint)_initResult);
        }));
        GuestMemory.W32(interfaceObject + 0x3C, Stub("netpsk term", context =>
        {
            _termCalls.Add(context.Gpr(0));
            return 0;
        }));
        foreach (uint offset in new uint[] { 0x40, 0x44, 0x48, 0x50 })
        {
            GuestMemory.W32(interfaceObject + offset, callback);
        }

        GuestMemory.W32(interfaceObject + 0x58, kind);
        GuestMemory.W32(interfaceObject + 0x64, mtu);
        return interfaceObject;
    }

    private static string NameOf(uint interfaceObject)
    {
        uint linkRecord = GuestMemory.R32(GuestMemory.R32(interfaceObject + 0x34) + 0x30);
        return GuestMemory.ReadCString(linkRecord + 0x14, 0x10);
    }

    private static uint UnitBitOf(uint interfaceObject)
    {
        return GuestMemory.R32(GuestMemory.R32(interfaceObject + 0x34) + 4);
    }

    [Theory]
    [InlineData(0x21F4428Du)]
    [InlineData(0x1ABF937Du)]
    [InlineData(0xF5788FF8u)]
    [InlineData(0x83A12CCCu)]
    [InlineData(0xC000CA8Fu)]
    [InlineData(0x3D4A1D74u)]
    [InlineData(0x8C4346CAu)]
    [InlineData(0xE41C0794u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceNetPsForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void NothingIsRegisteredToUndo()
    {
        uint driver = _harness.Alloc(0x80);
        Assert.Equal(Invalid, Call(Net, "NetPsDetachInterface", driver));
        Assert.Equal(Invalid, Call(Net, "NetPsDetachInterface", 0u));
        Assert.Equal(Invalid, Call(Net, "NetPsUnregisterHook", 0u));
        Assert.Equal(Invalid, Call(Net, "NetPsUnregisterHook", driver));
        Assert.Equal(Invalid, Call(Net, "NetPsUnregisterGateway", driver));
        // The gateway slot holds NULL: NULL is "the registered one" and the module answers 0.
        Assert.Equal(0, Call(Net, "NetPsUnregisterGateway", 0u));
        Assert.Equal(0, Call(Net, "NetPsNop"));
    }

    [Theory]
    [InlineData(0x40u)]
    [InlineData(0x44u)]
    [InlineData(0x48u)]
    [InlineData(0x50u)]
    public void AttachNeedsEveryDriverCallback(uint offset)
    {
        _harness.OnGuest(_ =>
        {
            uint interfaceObject = NewInterface(2);
            GuestMemory.W32(interfaceObject + offset, 0);
            Assert.Equal(Invalid, Call(Net, "NetPsAttachInterface", interfaceObject));
            Assert.Empty(_initCalls);
        });
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void AttachRefusesTheReservedFlags(uint flags)
    {
        _harness.OnGuest(_ =>
        {
            Assert.Equal(Invalid, Call(Net, "NetPsAttachInterface", 0u));
            uint interfaceObject = NewInterface(2);
            GuestMemory.W32(interfaceObject + 0x68, flags);
            Assert.Equal(Invalid, Call(Net, "NetPsAttachInterface", interfaceObject));
            Assert.Empty(_initCalls);
        });
    }

    [Fact]
    public void AttachNumbersByKindLinksTheListAndNamesTheInterfaces()
    {
        _harness.OnGuest(_ =>
        {
            uint wlan0 = NewInterface(2, 2304);
            uint wlan1 = NewInterface(2);
            uint eth0 = NewInterface(1);
            uint bt0 = NewInterface(3);
            uint phone0 = NewInterface(4);
            uint other0 = NewInterface(9);
            foreach (uint interfaceObject in new[] { wlan0, wlan1, eth0, bt0, phone0, other0 })
            {
                Assert.Equal(0, Call(Net, "NetPsAttachInterface", interfaceObject));
            }

            Assert.Equal("wlan0", NameOf(wlan0));
            Assert.Equal("wlan1", NameOf(wlan1));
            Assert.Equal("eth0", NameOf(eth0));
            Assert.Equal("bt0", NameOf(bt0));
            Assert.Equal("phone0", NameOf(phone0));
            Assert.Equal("other0", NameOf(other0));
            Assert.Equal(1u, UnitBitOf(wlan0));
            Assert.Equal(2u, UnitBitOf(wlan1));
            Assert.Equal(1u, UnitBitOf(eth0));

            // The driver's init ran once per interface with the object first.
            Assert.Equal(new uint[] { wlan0, wlan1, eth0, bt0, phone0, other0 }, _initCalls.Select(c => c.Object));

            // Doubly linked in attach order: +0 next, +4 previous.
            Assert.Equal(0u, GuestMemory.R32(wlan0 + 4));
            Assert.Equal(wlan1, GuestMemory.R32(wlan0));
            Assert.Equal(wlan0, GuestMemory.R32(wlan1 + 4));
            Assert.Equal(eth0, GuestMemory.R32(wlan1));
            Assert.Equal(0u, GuestMemory.R32(other0));

            // The stack's nine entries, the interface record (back pointer) and the MTU capped at 1500.
            for (uint slot = 0; slot < 9; slot++)
            {
                Assert.NotEqual(0u, GuestMemory.R32(wlan0 + 8 + slot * 4));
            }

            uint interfaceRecord = GuestMemory.R32(wlan0 + 0x34);
            Assert.Equal(wlan0, GuestMemory.R32(interfaceRecord));
            uint linkRecord = GuestMemory.R32(interfaceRecord + 0x30);
            Assert.Equal(interfaceRecord, GuestMemory.R32(linkRecord));
            Assert.Equal(1500u, GuestMemory.R32(linkRecord + 0x40));

            // Attaching an attached interface answers 0 and does nothing.
            Assert.Equal(0, Call(Net, "NetPsAttachInterface", wlan1));
            Assert.Equal(6, _initCalls.Count);
        });
    }

    [Fact]
    public void AttachReturnsTheInitErrorAndUsesAtMostEightUnitsPerKind()
    {
        _harness.OnGuest(_ =>
        {
            _initResult = unchecked((int)0x80001234);
            uint refused = NewInterface(2);
            Assert.Equal(_initResult, Call(Net, "NetPsAttachInterface", refused));
            Assert.Equal(0u, GuestMemory.R32(refused + 0x34));
            Assert.Equal(Invalid, Call(Net, "NetPsDetachInterface", refused));

            _initResult = 0;
            for (int unit = 0; unit < 8; unit++)
            {
                Assert.Equal(0, Call(Net, "NetPsAttachInterface", NewInterface(2)));
            }

            Assert.Equal(Invalid, Call(Net, "NetPsAttachInterface", NewInterface(2)));
            Assert.Equal(0, Call(Net, "NetPsAttachInterface", NewInterface(1)));
        });
    }

    [Fact]
    public void DetachUnlinksCallsTheTermClearsTheStackAreaAndFreesTheUnit()
    {
        _harness.OnGuest(_ =>
        {
            uint first = NewInterface(2);
            uint middle = NewInterface(2);
            uint last = NewInterface(2);
            foreach (uint interfaceObject in new[] { first, middle, last })
            {
                Assert.Equal(0, Call(Net, "NetPsAttachInterface", interfaceObject));
            }

            Assert.Equal(0, Call(Net, "NetPsDetachInterface", middle));
            Assert.Equal(new[] { middle }, _termCalls);
            Assert.Equal(last, GuestMemory.R32(first));
            Assert.Equal(first, GuestMemory.R32(last + 4));
            for (uint offset = 8; offset < 0x38; offset += 4)
            {
                Assert.Equal(0u, GuestMemory.R32(middle + offset));
            }

            Assert.Equal(Invalid, Call(Net, "NetPsDetachInterface", middle));

            // The freed unit (1) is the lowest free one again.
            uint again = NewInterface(2);
            Assert.Equal(0, Call(Net, "NetPsAttachInterface", again));
            Assert.Equal("wlan1", NameOf(again));
        });
    }

    [Fact]
    public void PacketEntriesBuildTheStacksPacketLayout()
    {
        _harness.OnGuest(thread =>
        {
            uint interfaceObject = NewInterface(2);
            GuestMemory.W32(interfaceObject + 0x68, 0x10); // headroom, bits 0-1 clear
            Assert.Equal(0, Call(Net, "NetPsAttachInterface", interfaceObject));
            uint packetAlloc = GuestMemory.R32(interfaceObject + 0x10);
            uint packet = thread.CallGuest(packetAlloc, interfaceObject, 0x100, 4);
            Assert.NotEqual(0u, packet);
            uint segment = GuestMemory.R32(packet + 0x20);
            Assert.Equal(packet + 0x2C, segment);
            uint buffer = GuestMemory.R32(segment);
            Assert.Equal(packet, buffer + 0x110);
            Assert.Equal(buffer + 0x110, GuestMemory.R32(segment + 4));
            Assert.Equal(buffer + 0x10, GuestMemory.R32(segment + 8));
            Assert.Equal(0x150u, GuestMemory.R32(packet + 0x0C));

            uint added = thread.CallGuest(GuestMemory.R32(interfaceObject + 0x14), interfaceObject, packet, 0x40, 4);
            Assert.Equal(added, GuestMemory.R32(segment + 0x10));

            // No interface: no packet.
            Assert.Equal(0u, thread.CallGuest(packetAlloc, 0, 0x100, 4));

            // The send queue of the emulated stack is always empty; the MTU entry sets the link record's MTU.
            Assert.Equal(0u, thread.CallGuest(GuestMemory.R32(interfaceObject + 0x20), interfaceObject));
            Assert.Equal(0u, thread.CallGuest(GuestMemory.R32(interfaceObject + 0x28), interfaceObject, 1400));
            uint linkRecord = GuestMemory.R32(GuestMemory.R32(interfaceObject + 0x34) + 0x30);
            Assert.Equal(1400u, GuestMemory.R32(linkRecord + 0x40));
            Assert.Equal(unchecked((uint)Invalid), thread.CallGuest(GuestMemory.R32(interfaceObject + 0x28), 0, 1400));
            thread.CallGuest(GuestMemory.R32(interfaceObject + 0x18), interfaceObject, packet);
        });
    }

    [Fact]
    public void HookRegistersOnceAndItsOperationsCheckTheHook()
    {
        _harness.OnGuest(thread =>
        {
            Assert.Equal(Invalid, Call(Net, "NetPsRegisterHook", 0u));
            uint hook = _harness.Alloc(0x10);
            uint other = _harness.Alloc(0x10);
            Assert.Equal(0, Call(Net, "NetPsRegisterHook", hook));
            Assert.Equal(Invalid, Call(Net, "NetPsRegisterHook", other));
            uint operations = GuestMemory.R32(hook);
            Assert.NotEqual(0u, operations);
            Assert.NotEqual(0u, GuestMemory.R32(hook + 4));
            Assert.Equal(0u, GuestMemory.R32(other));

            Assert.Equal(unchecked((uint)HookNotRegistered), thread.CallGuest(operations, other, 6, 0, 0));
            Assert.Equal(unchecked((uint)HookOperation), thread.CallGuest(operations, hook, 7, 0, 0));
            Assert.Equal(unchecked((uint)HookOperation), thread.CallGuest(operations, hook, 0, 0, 0));

            Assert.Equal(Invalid, Call(Net, "NetPsUnregisterHook", other));
            Assert.Equal(0, Call(Net, "NetPsUnregisterHook", hook));
            Assert.Equal(Invalid, Call(Net, "NetPsUnregisterHook", hook));
            Assert.Equal(unchecked((uint)HookNotRegistered), thread.CallGuest(operations, hook, 6, 0, 0));
            Assert.Equal(0, Call(Net, "NetPsRegisterHook", other));
            Assert.Equal(0, Call(Net, "NetPsUnregisterHook", other));
        });
    }

    /// <summary>A hook operation with the two stack arguments (the fifth and sixth) pushed by the caller.</summary>
    private static uint Operation(GuestThread thread, uint operations, uint hook, uint operation, uint first, uint second, uint third, uint fourth)
    {
        return thread.CallGuest(operations, hook, operation, first, second, third, fourth);
    }

    [Fact]
    public void HookOperationsReadAndWriteTheInterfacesAndTheStack()
    {
        _harness.OnGuest(thread =>
        {
            uint hook = _harness.Alloc(0x10);
            Assert.Equal(0, Call(Net, "NetPsRegisterHook", hook));
            uint operations = GuestMemory.R32(hook);
            uint request = _harness.Alloc(0x220);
            uint output = _harness.Alloc(0x220);

            // 6: the DNS servers (host order), 0x0C bytes.
            Assert.Equal(unchecked((uint)HookArgument), Operation(thread, operations, hook, 6, 0, 1, output, 0x0C));
            Assert.Equal(unchecked((uint)Invalid), Operation(thread, operations, hook, 6, 0, 0, 0, 0x0C));
            Assert.Equal(unchecked((uint)HookArgument), Operation(thread, operations, hook, 6, 0, 0, output, 0x0B));
            Assert.Equal(0x0Cu, Operation(thread, operations, hook, 6, 0, 0, output, 0x0C));

            // 3: four counters.
            Assert.Equal(0x10u, Operation(thread, operations, hook, 3, 0, 0, output, 0x10));
            Assert.Equal(unchecked((uint)HookArgument), Operation(thread, operations, hook, 3, 0, 0, output, 0x0F));

            // 1: the configuration record of eth0 / wlan0 only, and only when attached.
            GuestMemory.W32(request, 3);
            Assert.Equal(unchecked((uint)HookArgument), Operation(thread, operations, hook, 1, request, 7, output, 0x208));
            Assert.Equal(unchecked((uint)HookArgument), Operation(thread, operations, hook, 1, request, 8, output, 0x207));
            Assert.Equal(unchecked((uint)HookNoInterface), Operation(thread, operations, hook, 1, request, 8, output, 0x208));
            GuestMemory.W32(request, 1);
            Assert.Equal(unchecked((uint)HookKind), Operation(thread, operations, hook, 1, request, 8, output, 0x208));

            uint wlan = NewInterface(2);
            Assert.Equal(0, Call(Net, "NetPsAttachInterface", wlan));

            // 2 then 1: the record written is the record read.
            GuestMemory.W32(request, 3);
            GuestMemory.W32(request + 4, 0);
            GuestMemory.W32(request + 8, 0x200);
            for (uint i = 0; i < 0x200; i += 4)
            {
                GuestMemory.W32(request + 0x0C + i, 0xA5000000 | i);
            }

            Assert.Equal(unchecked((uint)HookArgument), Operation(thread, operations, hook, 2, request, 0x20B, 0, 0));
            Assert.Equal(unchecked((uint)HookArgument), Operation(thread, operations, hook, 2, request, 0x20C, 0, 1));
            Assert.Equal(0u, Operation(thread, operations, hook, 2, request, 0x20C, 0, 0));
            Assert.Equal(0x208u, Operation(thread, operations, hook, 1, request, 8, output, 0x208));
            Assert.Equal(3u, GuestMemory.R32(output));
            Assert.Equal(0x200u, GuestMemory.R32(output + 4));
            Assert.Equal(0xA5000010u, GuestMemory.R32(output + 8 + 0x10));
            GuestMemory.W32(request + 4, 5);
            Assert.Equal(0x80410116u, Operation(thread, operations, hook, 1, request, 8, output, 0x208));

            // 4: an attached interface's status header; 5: no neighbours.
            Assert.Equal(0x148u, Operation(thread, operations, hook, 4, request, 4, output, 0x148));
            Assert.Equal(0x140u, GuestMemory.R32(output + 4));
            GuestMemory.W32(request, 2);
            Assert.Equal(unchecked((uint)HookNoInterface), Operation(thread, operations, hook, 4, request, 4, output, 0x148));
            GuestMemory.W32(request, 7);
            Assert.Equal(unchecked((uint)HookKind), Operation(thread, operations, hook, 4, request, 4, output, 0x148));
            Assert.Equal(0x0Cu, Operation(thread, operations, hook, 5, request, 4, output, 0x0C));
            Assert.Equal(0u, GuestMemory.R32(output + 4));
            Assert.Equal(0x6Cu, GuestMemory.R32(output + 8));
            Assert.Equal(0, Call(Net, "NetPsUnregisterHook", hook));
        });
    }

    [Fact]
    public void GatewayOpensItsSocketWritesItsEntriesAndFiltersTheInput()
    {
        _harness.OnGuest(thread =>
        {
            uint gateway = _harness.Alloc(0x20);
            Assert.Equal(Invalid, Call(Net, "NetPsRegisterGateway", gateway));
            GuestMemory.W32(gateway + 0x14, Stub("netpsk filter", context =>
            {
                _filterCalls.Add((context.Gpr(0), context.Gpr(1), context.Gpr(2)));
                return 0;
            }));
            Assert.Equal(Invalid, Call(Net, "NetPsRegisterGateway", gateway));
            GuestMemory.W32(gateway + 0x18, Stub("netpsk gateway other", _ => 0));
            Assert.Equal(0, Call(Net, "NetPsRegisterGateway", gateway));
            Assert.Equal(40000u, GuestMemory.R32(gateway + 0x0C));
            Assert.Equal(0x23C0u, GuestMemory.R32(gateway + 0x10));
            for (uint slot = 0; slot < 3; slot++)
            {
                Assert.NotEqual(0u, GuestMemory.R32(gateway + slot * 4));
            }

            uint second = _harness.Alloc(0x20);
            GuestMemory.W32(second + 0x14, GuestMemory.R32(gateway + 0x14));
            GuestMemory.W32(second + 0x18, GuestMemory.R32(gateway + 0x18));
            Assert.Equal(Invalid, Call(Net, "NetPsRegisterGateway", second));

            // The gateway's buffers come from the stack heap; a send through another object is refused.
            uint buffer = thread.CallGuest(GuestMemory.R32(gateway), gateway, 0x80, 8);
            Assert.NotEqual(0u, buffer);
            thread.CallGuest(GuestMemory.R32(gateway + 4), gateway, buffer);
            Assert.Equal(unchecked((uint)GatewayNotRegistered), thread.CallGuest(GuestMemory.R32(gateway + 8), second, 0, 0, 0x0100007F));

            // A packet a driver gives the stack goes through the gateway's filter.
            uint interfaceObject = NewInterface(2);
            Assert.Equal(0, Call(Net, "NetPsAttachInterface", interfaceObject));
            uint packet = thread.CallGuest(GuestMemory.R32(interfaceObject + 0x10), interfaceObject, 0x40, 4);
            thread.CallGuest(GuestMemory.R32(interfaceObject + 0x1C), interfaceObject, packet);
            Assert.Equal(new[] { (gateway, interfaceObject, packet) }, _filterCalls);

            Assert.Equal(Invalid, Call(Net, "NetPsUnregisterGateway", second));
            Assert.Equal(0, Call(Net, "NetPsUnregisterGateway", gateway));
            Assert.Equal(Invalid, Call(Net, "NetPsUnregisterGateway", gateway));
            Assert.Equal(0, Call(Net, "NetPsRegisterGateway", second));
            Assert.Equal(0, Call(Net, "NetPsUnregisterGateway", second));
        });
    }
}
