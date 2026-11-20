using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceUdcdForDriver (SceUdcd 3.74), no USB cable plugged in: drivers register, start after the bus controller and
/// activate; the state words follow (0x1000/0x2000, 0x100, cable 0x10, connection 1); requests, FIFO clears and
/// stalls need a connected cable (0x80243007) after their argument checks; buses other than 1 and 2 are 0x80243003.
/// </summary>
[Collection("guest")]
public sealed class UdcdKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Already = unchecked((int)0x80243002), Bus = unchecked((int)0x80243003);
    private const int Active = unchecked((int)0x80243004), InUse = unchecked((int)0x80243006);
    private const int NotReady = unchecked((int)0x80243007), NotActive = unchecked((int)0x80243009);
    private const int WaitArgument = unchecked((int)0x8024300A), DriverNull = unchecked((int)0x80243200);
    private const int DriverName = unchecked((int)0x80243201), Callbacks = unchecked((int)0x80243202);
    private const int Interfaces = unchecked((int)0x80243203), Unknown = unchecked((int)0x80243204);
    private const int RequestNull = unchecked((int)0x80243400), EndpointNull = unchecked((int)0x80243401);
    private const int EndpointNumber = unchecked((int)0x80243402);

    private readonly HleHarness _harness = new(fixture, "udcdk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private uint Driver(string name, int interfaces = 1)
    {
        uint driver = _harness.Alloc(0x54);
        GuestMemory.Fill(driver, 0, 0x54);
        uint interfaceBlock = _harness.Alloc(0xC);
        GuestMemory.W32(interfaceBlock, 0xFFFFFFFF);
        GuestMemory.W32(interfaceBlock + 4, 0);
        GuestMemory.W32(interfaceBlock + 8, (uint)interfaces);
        GuestMemory.W32(driver, _harness.Str(name));
        GuestMemory.W32(driver + 4, 2);
        GuestMemory.W32(driver + 0xC, interfaceBlock);
        GuestMemory.W32(driver + 0x40, 0x81000001);
        GuestMemory.W32(driver + 0x44, 0x81000003);
        return driver;
    }

    private uint Name(string name) => _harness.Str(name);

    [Fact]
    public void EveryExportHasAnEntry()
    {
        uint[] nids =
        [
            0x03781649, 0x1A5A3851, 0x28BFEC45, 0x2A234CAF, 0x2D91F8C3, 0x78BF2A28, 0x9E7F7EFE, 0xAA5FD90E, 0xB1454F11,
            0xB1C2EA5A, 0xD01CFD22, 0xDB7EF7AB, 0xE4F32EDA, 0xE9E75C49, 0xEBB1E86B, 0x4FDEA423, 0x9119247B, 0x9F53D64D,
            0xA35F3BAF, 0x8AE87657, 0xFDC10F93, 0xFBEA3703, 0x45EB0177, 0xE054B5E4, 0x409DD196, 0xC0CA5DDB, 0x512F77BC,
            0x4E55244D, 0x64675918, 0x38787672, 0xC60A74B2, 0x175E6179, 0x1ED0E89E, 0x2E3E622A, 0x34079250, 0x9B44DF29,
            0x9FD733EA, 0x360E95B9, 0x90F8BCAE, 0x1494293B, 0x0F3595AE, 0x150FD3BB, 0x0DECE532, 0x6CCD152E, 0x1C684884,
            0xD03017C0, 0x7AD0C8D1,
        ];
        Assert.Equal(47, nids.Distinct().Count());
        foreach (uint nid in nids)
        {
            var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
                .Where(b => b.Nid == nid && b.Library == "SceUdcdForDriver").ToList();
            Assert.True(bindings.Count == 1, $"0x{nid:X8}: {bindings.Count} entries");
        }
    }

    [Fact]
    public void RegistrationChecksTheDriverBlock()
    {
        uint noStop = Driver("a");
        GuestMemory.W32(noStop + 0x44, 0);
        Assert.Equal(DriverNull, Call(Services, "ksceUdcdRegister", 0u));
        Assert.Equal(DriverName, Call(Services, "ksceUdcdRegister", Driver("")));
        Assert.Equal(DriverName, Call(Services, "ksceUdcdRegister", Driver(new string('n', 64))));
        Assert.Equal(Callbacks, Call(Services, "ksceUdcdRegister", noStop));
        Assert.Equal(Interfaces, Call(Services, "ksceUdcdRegister", Driver("a", 9)));
        Assert.Equal(Bus, Call(Services, "ksceUdcdRegisterInternal", Driver("a"), 3));
        uint driver = Driver("USB_MTP_Driver");
        Assert.Equal(0, Call(Services, "ksceUdcdRegister", driver));
        Assert.Equal(2u, GuestMemory.R32(driver + 0x4C));
        Assert.Equal(Already, Call(Services, "ksceUdcdRegister", Driver("USB_MTP_Driver")));
        Assert.Equal(0, Call(Services, "ksceUdcdRegisterInternal", Driver("USB_MTP_Driver"), 1));
        Assert.Equal(1, Call(Services, "ksceUdcdGetDrvState", Name("USB_MTP_Driver")));
        Assert.Equal(-1, Call(Services, "ksceUdcdGetDrvState", Name("other")));
        Assert.Equal(DriverName, Call(Services, "ksceUdcdGetDrvState", 0u));
        Assert.Equal(0, Call(Services, "ksceUdcdUnregister", driver));
        Assert.Equal(Unknown, Call(Services, "ksceUdcdUnregister", driver));
        Assert.Equal(-1, Call(Services, "ksceUdcdGetDrvState", Name("USB_MTP_Driver")));
        Assert.Equal(1, Call(Services, "ksceUdcdGetDrvStateInternal", Name("USB_MTP_Driver"), 1));
    }

    [Fact]
    public void DriversStartAfterTheControllerAndActivateWithoutACable()
    {
        uint driver = Driver("USBSerDriver");
        Assert.Equal(0, Call(Services, "ksceUdcdRegister", driver));
        Assert.Equal(0x1111, Call(Services, "ksceUdcdGetDeviceState"));
        Assert.Equal(NotReady, Call(Services, "ksceUdcdStart", Name("USBSerDriver"), 0u, 0u));
        Assert.Equal(NotReady, Call(Services, "ksceUdcdActivate", 0x1CBu));
        Assert.Equal(0, Call(Services, "ksceUdcdStart", Name("USBDeviceControllerDriver"), 0u, 0u));
        Assert.Equal(Already, Call(Services, "ksceUdcdStart", Name("USBDeviceControllerDriver"), 0u, 0u));
        Assert.Equal(Unknown, Call(Services, "ksceUdcdActivate", 0x1CBu));
        Assert.Equal(Unknown, Call(Services, "ksceUdcdStart", Name("missing"), 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceUdcdStart", Name("USBSerDriver"), 0u, 0u));
        Assert.Equal(Already, Call(Services, "ksceUdcdStart", Name("USBSerDriver"), 0u, 0u));
        Assert.Equal(2, Call(Services, "ksceUdcdGetDrvState", Name("USBSerDriver")));
        Assert.Equal(InUse, Call(Services, "ksceUdcdUnregister", driver));
        Assert.Equal(InUse, Call(Services, "ksceUdcdStop", Name("USBDeviceControllerDriver"), 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceUdcdActivate", 0x1CBu));
        Assert.Equal(Already, Call(Services, "ksceUdcdActivate", 0x1CBu));
        Assert.Equal(0x2111, Call(Services, "ksceUdcdGetDeviceState"));
        Assert.Equal(0x1111, Call(Services, "ksceUdcdGetDeviceStateInternal", 1));
        Assert.Equal(Active, Call(Services, "ksceUdcdStop", Name("USBSerDriver"), 0u, 0u));
        Assert.Equal(Active, Call(Services, "ksceUdcdUnregister", driver));
        Assert.Equal(0, Call(Services, "ksceUdcdDeactivate"));
        Assert.Equal(NotActive, Call(Services, "ksceUdcdDeactivate"));
        Assert.Equal(0, Call(Services, "ksceUdcdStop", Name("USBSerDriver"), 0u, 0u));
        Assert.Equal(Already, Call(Services, "ksceUdcdStop", Name("USBSerDriver"), 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceUdcdStop", Name("USBDeviceControllerDriver"), 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceUdcdUnregister", driver));
        Assert.Equal(Bus, Call(Services, "ksceUdcdGetDeviceStateInternal", 0));
    }

    [Fact]
    public void StopCurrentReturnsTheBusToItsStart()
    {
        Assert.Equal(0, Call(Services, "ksceUdcdRegister", Driver("d")));
        Assert.Equal(0, Call(Services, "ksceUdcdStartCurrentInternal", 0u, 2));
        Assert.Equal(0, Call(Services, "ksceUdcdStart", Name("d"), 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceUdcdActivate", 1u));
        Assert.Equal(0, Call(Services, "ksceUdcdStopCurrentInternal", 2));
        Assert.Equal(0x1111, Call(Services, "ksceUdcdGetDeviceState"));
        Assert.Equal(1, Call(Services, "ksceUdcdGetDrvState", Name("d")));
        Assert.Equal(NotReady, Call(Services, "ksceUdcdStart", Name("d"), 0u, 0u));
    }

    [Fact]
    public void TransfersNeedAConnectedCable()
    {
        uint endpoint = _harness.Alloc(0x10);
        GuestMemory.Fill(endpoint, 0, 0x10);
        uint request = _harness.Alloc(0x40);
        GuestMemory.Fill(request, 0, 0x40);
        Assert.Equal(NotReady, Call(Services, "ksceUdcdReqSend", request));
        Assert.Equal(NotReady, Call(Services, "ksceUdcdReqRecv", 0u));
        Assert.Equal(NotReady, Call(Services, "ksceUdcdReqSendInternal", request, 1));
        Assert.Equal(Bus, Call(Services, "ksceUdcdReqRecvInternal", request, 0));
        Assert.Equal(NotReady, Call(Services, "ksceUdcdReqRecvPspcm", request));
        Assert.Equal(EndpointNull, Call(Services, "ksceUdcdClearFIFO", 0u));
        Assert.Equal(EndpointNumber, Call(Services, "ksceUdcdStall", endpoint));
        GuestMemory.W32(endpoint + 8, 1);
        Assert.Equal(NotReady, Call(Services, "ksceUdcdClearFIFOInternal", endpoint, 2));
        Assert.Equal(NotReady, Call(Services, "ksceUdcdStallInternal", endpoint, 1));
        Assert.Equal(EndpointNull, Call(Services, "ksceUdcdReqCancelAll", 0u));
        Assert.Equal(NotReady, Call(Services, "ksceUdcdReqCancelAll", endpoint));
        Assert.Equal(NotReady, Call(Services, "ksceUdcdReqQueueInternal", 0u, 2));

        Assert.Equal(0, Call(Services, "ksceUdcdRegister", Driver("d")));
        Assert.Equal(0, Call(Services, "ksceUdcdStart", Name("USBDeviceControllerDriver"), 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceUdcdStart", Name("d"), 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceUdcdActivate", 1u));
        Assert.Equal(0, Call(Services, "ksceUdcdReqCancelAll", endpoint));
        Assert.Equal(RequestNull, Call(Services, "ksceUdcdReqQueueInternal", 0u, 2));
        Assert.Equal(EndpointNull, Call(Services, "ksceUdcdReqQueueInternal", request, 2));
        Assert.Equal(NotReady, Call(Services, "ksceUdcdReqSend", request));
    }

    [Fact]
    public void AWaitForAStateTheUnitIsInReturnsAtOnce()
    {
        uint wait = _harness.Alloc(0x18);
        GuestMemory.Fill(wait, 0, 0x18);
        GuestMemory.W32(wait + 8, 0x40);
        Assert.Equal(WaitArgument, Call(Services, "ksceUdcdWaitState", wait, 0u));
        Assert.Equal(WaitArgument, Call(Services, "ksceUdcdWaitState", 0u, 0u));
        GuestMemory.W32(wait + 8, 0x30);
        GuestMemory.W32(wait + 4, 0x100);
        Assert.Equal(0, Call(Services, "ksceUdcdWaitState", wait, 1000u));
        Assert.Equal(Bus, Call(Services, "ksceUdcdWaitStateInternal", wait, 1000u, 3));
        Assert.Equal(0, Call(Services, "ksceUdcdWaitBusInitialized", 0u, 2u));
        Assert.Equal(Bus, Call(Services, "ksceUdcdWaitBusInitialized", 0u, 3u));
    }

    [Fact]
    public void InfoPowerAndSettings()
    {
        uint info = _harness.Alloc(0x40);
        GuestMemory.Fill(info, 0xAA, 0x40);
        Assert.Equal(0, Call(Services, "ksceUdcdGetDeviceInfo", info));
        Assert.Equal(0u, GuestMemory.R32(info));
        Assert.Equal(0u, GuestMemory.R32(info + 0x3C));
        Assert.Equal(Bus, Call(Services, "ksceUdcdGetDeviceInfoInternal", info, 0));
        Assert.Equal(0, Call(Services, "ksceUdcdSetPowerMode", 1));
        Assert.True(Services.UdcdBusPowered);
        Assert.Equal(0, Call(Services, "ksceUdcdSetPowerMode", 0));
        Assert.False(Services.UdcdBusPowered);
        Assert.Equal(Already, Call(Services, "ksceUdcdControllerHook0", 2));
        Assert.Equal(Already, Call(Services, "ksceUdcdControllerHook1", 2));
        Assert.Equal(0, Call(Services, "ksceUdcdSetConfiguration", info));
        Assert.Equal(Bus, Call(Services, "ksceUdcdSetConfigurationInternal", info, 0));
        Assert.Equal(0, Call(Services, "ksceUdcdStoreWord", 0u));
        Assert.Equal(0, Call(Services, "ksceUdcdReenumerate"));
        Assert.Equal(0, Call(Services, "ksceUdcdNotifyState"));
        Assert.Equal(Bus, Call(Services, "ksceUdcdNotifyStateInternal", 0));
        Assert.Equal(0, Call(Services, "ksceUdcdSuspend"));
        Assert.Equal(0, Call(Services, "ksceUdcdResume"));
        Assert.Equal(0, Call(Services, "ksceUdcdPowerOff"));
    }
}
