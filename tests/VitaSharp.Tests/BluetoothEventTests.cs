using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceBt's event queue as FW 3.74 (SceBt 0xA23FB154 / 0x26BAC23D / 0x605EF71F): a process registers a callback with
/// a mask of event kinds; a paired keyboard or mouse connecting is event 5 (status 0) and disconnecting event 6 (HCI
/// reason 0x13), each with the device's address; the callback is notified and sceBtReadEvent returns the 16-byte
/// records oldest first. A full ring of 16 loses the next events and the read answers 0x802F0C04 once; a process
/// without a slot gets 0x802F0C03. Before, sceBtReadEvent always returned 0 and nothing was ever queued.
/// </summary>
[Collection("guest")]
public sealed class BluetoothEventTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotRegistered = unchecked((int)0x802F0C03), EventsLost = unchecked((int)0x802F0C04);
    private const uint ConnectionMask = 1u << 5 | 1u << 6;
    private const uint SyntheticEntry = 0x81000001;

    private readonly HleHarness _harness = new(fixture, "btevents");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Bt => _harness.Module<SystemServicesVsh>();

    [Fact]
    public void ConnectionsOfThePairedDevicesAreQueuedAndNotified()
    {
        ThreadMgr threads = _harness.Module<ThreadMgr>();
        uint events = _harness.Alloc(0x10 * 16);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(NotRegistered, Call(Bt, "sceBtReadEvent", events, 16));
            int callback = Call(threads, "sceKernelCreateCallback", "bt", 0u, SyntheticEntry, 0u);
            Assert.Equal(0, Call(Bt, "sceBtRegisterCallback", callback, 0, ConnectionMask, 0u));
            Assert.Equal(0, Call(Bt, "sceBtReadEvent", events, 16));

            _harness.Emu.Host.HidKeyboard = new HidKeyboard();   // the keyboard connects
            Assert.Equal(1, Call(threads, "sceKernelGetCallbackCount", callback));
            _harness.Emu.Host.HidKeyboard = null;                // and goes away
            Assert.Equal(2, Call(threads, "sceKernelGetCallbackCount", callback));

            Assert.Equal(2, Call(Bt, "sceBtReadEvent", events, 16));
            Assert.Equal((byte)5, GuestMemory.Read<byte>(events));
            Assert.Equal((byte)0, GuestMemory.Read<byte>(events + 1));
            Assert.Equal(SystemServicesVsh.PairedKeyboardMac0, GuestMemory.R32(events + 8));
            Assert.Equal(SystemServicesVsh.PairedMac1, GuestMemory.R32(events + 0xC));
            Assert.Equal((byte)6, GuestMemory.Read<byte>(events + 0x10));
            Assert.Equal((byte)0x13, GuestMemory.Read<byte>(events + 0x11));
            Assert.Equal(SystemServicesVsh.PairedKeyboardMac0, GuestMemory.R32(events + 0x18));
            Assert.Equal(0, Call(Bt, "sceBtReadEvent", events, 16));   // each event once

            // max limits a read; the rest stays for the next one
            _harness.Emu.Host.HidMouse = new HidMouse();
            _harness.Emu.Host.HidMouse = null;
            Assert.Equal(1, Call(Bt, "sceBtReadEvent", events, 1));
            Assert.Equal(SystemServicesVsh.PairedMouseMac0, GuestMemory.R32(events + 8));
            Assert.Equal(1, Call(Bt, "sceBtReadEvent", events, 16));
            Assert.Equal((byte)6, GuestMemory.Read<byte>(events));

            Assert.Equal(0, Call(Bt, "sceBtUnregisterCallback", callback));
            Assert.Equal(NotRegistered, Call(Bt, "sceBtReadEvent", events, 16));
            Assert.Equal(NotRegistered, Call(Bt, "sceBtUnregisterCallback", callback));
        });
    }

    [Fact]
    public void OnlyTheKindsOfTheMaskAreQueued()
    {
        ThreadMgr threads = _harness.Module<ThreadMgr>();
        uint events = _harness.Alloc(0x10 * 16);
        _harness.OnGuest(_ =>
        {
            int callback = Call(threads, "sceKernelCreateCallback", "bt", 0u, SyntheticEntry, 0u);
            Assert.Equal(0, Call(Bt, "sceBtRegisterCallback", callback, 0, 1u << 6, 0u));   // disconnections only
            _harness.Emu.Host.HidMouse = new HidMouse();
            Assert.Equal(0, Call(threads, "sceKernelGetCallbackCount", callback));
            _harness.Emu.Host.HidMouse = null;
            Assert.Equal(1, Call(Bt, "sceBtReadEvent", events, 16));
            Assert.Equal((byte)6, GuestMemory.Read<byte>(events));
        });
    }

    [Fact]
    public void AFullQueueLosesEventsAndSaysSoOnce()
    {
        ThreadMgr threads = _harness.Module<ThreadMgr>();
        uint events = _harness.Alloc(0x10 * 16);
        _harness.OnGuest(_ =>
        {
            int callback = Call(threads, "sceKernelCreateCallback", "bt", 0u, SyntheticEntry, 0u);
            Assert.Equal(0, Call(Bt, "sceBtRegisterCallback", callback, 0, ConnectionMask, 0u));
            for (int i = 0; i < 9; i++)   // 18 events into a ring of 16
            {
                _harness.Emu.Host.HidMouse = new HidMouse();
                _harness.Emu.Host.HidMouse = null;
            }

            Assert.Equal(EventsLost, Call(Bt, "sceBtReadEvent", events, 16));
            Assert.Equal(16, Call(Bt, "sceBtReadEvent", events, 16));
            Assert.Equal((byte)5, GuestMemory.Read<byte>(events));     // the oldest kept
            Assert.Equal(0, Call(Bt, "sceBtReadEvent", events, 16));
        });
    }
}
