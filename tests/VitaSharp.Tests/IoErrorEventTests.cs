using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// I/O error events as FW 3.74 iofilemgr (0x3C0343DB / 0xDD46CD63 / 0x40B933C7 / 0xC6158F8D / 0x5B7E5AB8): an event is
/// made for one of the module's mount points (type 1, a 0x1C-byte record with its reserved words zero) as an event
/// flag named after the device ("SceIoErrorEventUx0"); a driver's notice on that mount writes the record
/// { mount, info +4, 0, 0, info +0x10, 0, 0 } and sets the flag, once until the event is cleared; a notice of another
/// mount or mask leaves it. Before, the create took a callback, the notice answered 0x8001000C and nothing was written.
/// </summary>
[Collection("guest")]
public sealed class IoErrorEventTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Inval = unchecked((int)0x80010016);

    private readonly HleHarness _harness = new(fixture, "ioerrev");

    public void Dispose() => _harness.Dispose();

    private IoFileMgr Io => _harness.Module<IoFileMgr>();

    [Fact]
    public void AnErrorEventIsNotifiedOnceUntilCleared()
    {
        uint record = _harness.Alloc(0x20);
        uint info = _harness.Alloc(0x20);
        GuestMemory.W32(info + 4, 0x11111111);
        GuestMemory.W32(info + 0x10, 0x22222222);

        Assert.Equal(Inval, Call(Io, "ksceIoCreateErrorEvent", 0x800u, 2u, record));      // type 1 only
        Assert.Equal(Inval, Call(Io, "ksceIoCreateErrorEvent", 0x1234u, 1u, record));     // no such mount point
        GuestMemory.W32(record + 0x14, 1);
        Assert.Equal(Inval, Call(Io, "ksceIoCreateErrorEvent", 0x800u, 1u, record));      // a reserved word set
        GuestMemory.W32(record + 0x14, 0);

        int uid = Call(Io, "ksceIoCreateErrorEvent", 0x800u, 1u, record);
        Assert.True(uid > 0, $"0x{uid:X8}");
        EventFlag flag = _harness.Emu.Kernel.Objects.Get<EventFlag>(uid)!;
        Assert.Equal("SceIoErrorEventUx0", flag.Name);

        Assert.Equal(0, Call(Io, "ksceIofilemgr_DD46CD63", 0x600u, 1u, info));             // another mount
        Assert.Equal(0, Call(Io, "ksceIofilemgr_DD46CD63", 0x800u, 2u, info));             // another mask
        Assert.Equal(0u, flag.Pattern);
        Assert.Equal(0u, GuestMemory.R32(record));

        Assert.Equal(0, Call(Io, "ksceIofilemgr_DD46CD63", 0x800u, 1u, info));
        Assert.Equal(1u, flag.Pattern & 1);
        Assert.Equal(0x800u, GuestMemory.R32(record));
        Assert.Equal(0x11111111u, GuestMemory.R32(record + 4));
        Assert.Equal(0x22222222u, GuestMemory.R32(record + 0x10));

        GuestMemory.W32(info + 4, 0x33333333);
        Assert.Equal(0, Call(Io, "ksceIofilemgr_DD46CD63", 0x800u, 1u, info));             // notified already
        Assert.Equal(0x11111111u, GuestMemory.R32(record + 4));
        Assert.Equal(0, Call(Io, "ksceIoClearErrorEvent", uid));
        Assert.Equal(0, Call(Io, "ksceIofilemgr_DD46CD63", 0x800u, 1u, info));
        Assert.Equal(0x33333333u, GuestMemory.R32(record + 4));

        Assert.Equal(0, Call(Io, "ksceIoDeleteErrorEvent", uid));
        Assert.Equal(unchecked((int)0x80028002), Call(Io, "ksceIoDeleteErrorEvent", uid));
        Assert.Null(_harness.Emu.Kernel.Objects.Get<EventFlag>(uid));
        Assert.Equal(SceError.UNKNOWN_UID, Call(Io, "ksceIoClearErrorEvent", uid));
    }

    [Fact]
    public void MediaChangeNeedsAGuestMountAtTheMountPoint()
    {
        Assert.Equal(Inval, Call(Io, "ksceIofilemgr_5B7E5AB8", 0x1234u));                  // no such mount point
        Assert.Equal(Inval, Call(Io, "ksceIofilemgr_5B7E5AB8", 0x800u));                   // ux0 is a host folder
        Assert.Equal("SceIoErrorEventSdstor0", IoFileMgr.ErrorEventName("sdstor0:"));
        Assert.Equal("SceIoErrorEventGro0", IoFileMgr.ErrorEventName("gro0:"));
    }
}
