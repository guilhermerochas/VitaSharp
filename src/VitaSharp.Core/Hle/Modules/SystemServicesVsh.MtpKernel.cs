using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceMtpIfForDriver (os0:kd SceMtpIfDriver 3.74) and SceUsbMtpForDriver (SceUsbMtp 3.74), read in the modules, study
// only: the media transfer protocol a PC talks to over USB. SceMtpIf hands out its one interface (GetInterface(1);
// other numbers NULL) and checks it on every call (another value, or a NULL argument, 0x80543603); each call goes
// to the transport's operation table, which a service registers, and without one answers 0x8054360C; start and stop
// then go to SceUsbMtp directly, and from user mode the start/stop pair is held by one process (another process's
// start, or a stop without a start, 0x80543602). SceUsbMtp starts the USB device controller and its MTP driver; every
// transfer, status and setting call needs the driver attached to a host, else 0x8054360C. The emulated unit has no USB
// host (no PC on the cable) and no registered transport: the driver starts but never attaches.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceMtpIf: the start/stop pair held by another process, or a stop without a start (0x80543602).</summary>
    const int MTP_ERR_OWNER = unchecked((int)0x80543602);

    /// <summary>SceMtpIf: not the module's interface, or a NULL argument (0x80543603).</summary>
    const int MTP_ERR_INVALID_ARGUMENT = unchecked((int)0x80543603);

    /// <summary>SceMtpIf: no transport registered / the driver not attached to a host (0x8054360C).</summary>
    const int MTP_ERR_NOT_READY = unchecked((int)0x8054360C);

    /// <summary>The process holding the start/stop pair: none.</summary>
    const int MtpNoOwner = -1;

    [SnapshotMissing] // states of builds before it: the interface not handed out yet
    uint _mtpInterface;

    [SnapshotMissing(MtpNoOwner)]
    int _mtpOwnerProcess = MtpNoOwner;

    /// <summary>The interface handle (a word of guest memory, handed out once).</summary>
    uint MtpInterface()
    {
        if (_mtpInterface == 0)
        {
            _mtpInterface = Emu.Memory.Alloc(4, "SceMtpIf interface");
        }

        return _mtpInterface;
    }

    /// <summary>The interface check of every call, then no transport (0x8054360C).</summary>
    int MtpCall(uint handle, bool argumentPresent = true)
    {
        if (handle == 0 || handle != _mtpInterface || !argumentPresent)
        {
            return MTP_ERR_INVALID_ARGUMENT;
        }

        return MTP_ERR_NOT_READY;
    }

    /// <summary>ksceMtpIfGetInterface(int number): the module's interface for 1, else NULL.</summary>
    [Hle(Nid = 0x16D7DBCC, Library = "SceMtpIfForDriver", ReturnsValue = true)]
    uint ksceMtpIfGetInterface(uint number)
    {
        return number == 1 ? MtpInterface() : 0;
    }

    /// <summary>ksceMtpIfGetStatus(interface): no transport (0x8054360C).</summary>
    [Hle(Nid = 0x92CD107E, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfGetStatus(uint handle)
    {
        return MtpCall(handle);
    }

    /// <summary>ksceMtpIfGetInfo(interface, void *info): NULL info 0x80543603; no transport (0x8054360C).</summary>
    [Hle(Nid = 0x117F7519, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfGetInfo(uint handle, uint info)
    {
        return MtpCall(handle, info != 0);
    }

    /// <summary>ksceMtpIfSetInfo(interface, const void *info): NULL info 0x80543603; no transport (0x8054360C).</summary>
    [Hle(Nid = 0x5766D257, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfSetInfo(uint handle, uint info)
    {
        return MtpCall(handle, info != 0);
    }

    /// <summary>ksceMtpIfReceive(interface, void *buffer, size, flags): NULL buffer 0x80543603; no transport.</summary>
    [Hle(Nid = 0xEDC3AB26, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfReceive(uint handle, uint buffer, uint size, uint flags)
    {
        return MtpCall(handle, buffer != 0);
    }

    /// <summary>ksceMtpIfReceiveRequest(interface, request *request): a request without its buffer 0x80543603; no
    /// transport.</summary>
    [Hle(Nid = 0x33F1826F, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfReceiveRequest(uint handle, uint request)
    {
        return MtpCall(handle, request != 0 && GuestMemory.R32(request + 4) != 0);
    }

    /// <summary>ksceMtpIfSend(interface, const void *buffer, size): NULL buffer 0x80543603; no transport.</summary>
    [Hle(Nid = 0xF0FA2D86, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfSend(uint handle, uint buffer, uint size)
    {
        return MtpCall(handle, buffer != 0);
    }

    /// <summary>ksceMtpIfSendRequest(interface, request *request): a request without its buffer 0x80543603; no
    /// transport.</summary>
    [Hle(Nid = 0x27D40294, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfSendRequest(uint handle, uint request)
    {
        return MtpCall(handle, request != 0 && GuestMemory.R32(request + 4) != 0);
    }

    /// <summary>ksceMtpIfSendResponse(interface, const void *container, void *response): NULL ones, or a response
    /// with more than 3 parameters, 0x80543603; no transport.</summary>
    [Hle(Nid = 0xB5E78858, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfSendResponse(uint handle, uint container, uint response)
    {
        bool valid = container != 0 && response != 0 && GuestMemory.R32(response + 0x18) < 4;
        return MtpCall(handle, valid);
    }

    /// <summary>ksceMtpIfCancel(interface): no transport (0x8054360C).</summary>
    [Hle(Nid = 0xF1EC5992, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfCancel(uint handle)
    {
        return MtpCall(handle);
    }

    /// <summary>ksceMtpIfSetEvent(interface, event): no transport (0x8054360C).</summary>
    [Hle(Nid = 0x05408B5B, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfSetEvent(uint handle, uint mtpEvent)
    {
        return MtpCall(handle);
    }

    /// <summary>ksceMtpIfIsConnected(interface): no transport (0x8054360C).</summary>
    [Hle(Nid = 0x2D9318E1, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfIsConnected(uint handle)
    {
        return MtpCall(handle);
    }

    /// <summary>ksceMtpIfSetState(interface, state): no transport (0x8054360C).</summary>
    [Hle(Nid = 0x8D16B997, Library = "SceMtpIfForDriver", Default = true)]
    int ksceMtpIfSetState(uint handle, uint state)
    {
        return MtpCall(handle);
    }

    /// <summary>ksceMtpIfStart(interface): a user-mode caller's process takes the start/stop pair (another process
    /// holding it: 0x80543602); with no transport, SceUsbMtp's start.</summary>
    [Hle(Nid = 0x5245D237, Library = "SceMtpIfForDriver")]
    int ksceMtpIfStart(uint handle)
    {
        if (GuestThread.Current is { KernelMode: false } thread)
        {
            if (_mtpOwnerProcess != MtpNoOwner && _mtpOwnerProcess != thread.Pid)
            {
                return MTP_ERR_OWNER;
            }

            _mtpOwnerProcess = thread.Pid;
        }

        if (handle == 0 || handle != _mtpInterface)
        {
            return MTP_ERR_INVALID_ARGUMENT;
        }

        return ksceUsbMtpStartThroughEntry();
    }

    /// <summary>ksceMtpIfStop(interface): a user-mode caller releases the start/stop pair (not taken: 0x80543602);
    /// with no transport, SceUsbMtp's stop.</summary>
    [Hle(Nid = 0xE6453F21, Library = "SceMtpIfForDriver")]
    int ksceMtpIfStop(uint handle)
    {
        if (GuestThread.Current is { KernelMode: false })
        {
            if (_mtpOwnerProcess == MtpNoOwner)
            {
                return MTP_ERR_OWNER;
            }

            _mtpOwnerProcess = MtpNoOwner;
        }

        if (handle == 0 || handle != _mtpInterface)
        {
            return MTP_ERR_INVALID_ARGUMENT;
        }

        return ksceUsbMtpStopThroughEntry();
    }

    /// <summary>ksceMtpIfStartDriver(int number): 1 is SceUsbMtp's start; else 0x80543603.</summary>
    [Hle(Nid = 0xCB685AC0, Library = "SceMtpIfForDriver")]
    int ksceMtpIfStartDriver(uint number)
    {
        return number == 1 ? ksceUsbMtpStartThroughEntry() : MTP_ERR_INVALID_ARGUMENT;
    }

    /// <summary>ksceMtpIfStopDriver(int number): 1 is SceUsbMtp's stop; else 0x80543603.</summary>
    [Hle(Nid = 0xFDF8C3C0, Library = "SceMtpIfForDriver")]
    int ksceMtpIfStopDriver(uint number)
    {
        return number == 1 ? ksceUsbMtpStopThroughEntry() : MTP_ERR_INVALID_ARGUMENT;
    }

    /// <summary>ksceUsbMtpStart(): starts the USB device controller and the MTP driver, which waits for a host (none
    /// here: it never attaches); 0.</summary>
    [Hle(Nid = 0x6FC97594, Library = "SceUsbMtpForDriver", Default = true)]
    int ksceUsbMtpStart()
    {
        return 0;
    }

    /// <summary>ksceUsbMtpStop(): the driver is not attached to a host (0x8054360C).</summary>
    [Hle(Nid = 0x08CD359F, Library = "SceUsbMtpForDriver", Default = true)]
    int ksceUsbMtpStop()
    {
        return MTP_ERR_NOT_READY;
    }

    /// <summary>ksceUsbMtpGetStatus(): the driver's status word (no host: 0).</summary>
    [Hle(Nid = 0x6F7DF4B8, Library = "SceUsbMtpForDriver", Default = true, ReturnsValue = true)]
    int ksceUsbMtpGetStatus()
    {
        return 0;
    }

    /// <summary>ksceUsbMtpStallEndpoint(): not attached to a host (0x8054360C).</summary>
    [Hle(Nid = 0xB958E5DE, Library = "SceUsbMtpForDriver", Default = true)]
    int ksceUsbMtpStallEndpoint()
    {
        return MTP_ERR_NOT_READY;
    }

    /// <summary>ksceUsbMtpCancelTransfers(): not attached to a host (0x8054360C).</summary>
    [Hle(Nid = 0x151947D2, Library = "SceUsbMtpForDriver", Default = true)]
    int ksceUsbMtpCancelTransfers()
    {
        return MTP_ERR_NOT_READY;
    }

    /// <summary>ksceUsbMtpWaitEvent(timeout): not attached to a host (0x8054360C).</summary>
    [Hle(Nid = 0x76D707B1, Library = "SceUsbMtpForDriver", Default = true)]
    int ksceUsbMtpWaitEvent(uint timeout)
    {
        return MTP_ERR_NOT_READY;
    }

    /// <summary>ksceUsbMtpSetState(int state): not attached to a host (0x8054360C).</summary>
    [Hle(Nid = 0xE64513A9, Library = "SceUsbMtpForDriver", Default = true)]
    int ksceUsbMtpSetState(uint state)
    {
        return MTP_ERR_NOT_READY;
    }

    /// <summary>ksceUsbMtpReceiveData(void *request): not attached to a host (0x8054360C).</summary>
    [Hle(Nid = 0x25D92570, Library = "SceUsbMtpForDriver", Default = true)]
    int ksceUsbMtpReceiveData(uint request)
    {
        return MTP_ERR_NOT_READY;
    }

    /// <summary>ksceUsbMtpSendData(void *request): not attached to a host (0x8054360C).</summary>
    [Hle(Nid = 0xB01C1369, Library = "SceUsbMtpForDriver", Default = true)]
    int ksceUsbMtpSendData(uint request)
    {
        return MTP_ERR_NOT_READY;
    }

    /// <summary>ksceUsbMtpReceiveRequest(void *container, size): not attached to a host (0x8054360C).</summary>
    [Hle(Nid = 0x576A266F, Library = "SceUsbMtpForDriver", Default = true)]
    int ksceUsbMtpReceiveRequest(uint container, uint size)
    {
        return MTP_ERR_NOT_READY;
    }

    /// <summary>ksceUsbMtpSendResponse(const void *response): not attached to a host (0x8054360C).</summary>
    [Hle(Nid = 0x5FA86BA4, Library = "SceUsbMtpForDriver", Default = true)]
    int ksceUsbMtpSendResponse(uint response)
    {
        return MTP_ERR_NOT_READY;
    }

    /// <summary>ksceUsbMtpSendEvent(const void *mtpEvent): not attached to a host (0x8054360C).</summary>
    [Hle(Nid = 0x4463009B, Library = "SceUsbMtpForDriver", Default = true)]
    int ksceUsbMtpSendEvent(uint mtpEvent)
    {
        return MTP_ERR_NOT_READY;
    }
}
