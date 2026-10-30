using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceMtpIf and SceUdcd: the USB device side of the system's MTP service (Content Manager), as a PS Vita with no USB
/// cable plugged in. The shell's libmtp.suprx (vs0:vsh/common, LLE) starts its MTP device on the USB interface
/// (StartDriver, GetPort, StartPort through its interface table) and its device thread then polls IsConnected and
/// waits for a host with WaitConnect(port, 1 s) in a loop. With the port up but no host, the shell's SceMtpService
/// IPC server stays up and Content Manager shows its connection choices. Nothing is ever transferred.
/// </summary>
public sealed class MtpIf : HleModule
{
    const int WAIT_TIMEOUT = unchecked((int)0x80028005); // SCE_KERNEL_ERROR_WAIT_TIMEOUT, what libmtp's device loop expects
    const int PortId = 1; // the USB MTP port handle libmtp keeps (any non-zero value)
    volatile bool _portStarted;

    [Hle] int sceMtpIfStartDriver(int type) => 0;
    [Hle] int sceMtpIfStopDriver(int type) => 0;

    /// <summary>(int type): the port handle of the interface (0 = none, which libmtp reports as "unable to hook I/F").</summary>
    [Hle] int sceMtpIfGetPort(int type) => PortId;

    [Hle] int sceMtpIfStartPort(int port) { _portStarted = true; return 0; }
    [Hle] int sceMtpIfStopPort(int port) { _portStarted = false; return 0; }

    /// <summary>(int port): no host is ever connected.</summary>
    [Hle] int sceMtpIfIsConnected(int port) => 0;

    /// <summary>(int port, unsigned timeout_us): waits for a host; none comes, so it times out (0 waits while the port is
    /// started).</summary>
    [Hle]
    int sceMtpIfWaitConnect(int port, uint timeoutUs)
    {
        if (timeoutUs != 0) { Kernel.Wait.Sleep(timeoutUs); return WAIT_TIMEOUT; }
        while (_portStarted) Kernel.Wait.Sleep(100_000);
        return WAIT_TIMEOUT;
    }

    [Hle] int sceMtpIfReset(int port) => 0;
    [Hle] int sceMtpIfChangePhase(int port, int phase) => 0;
    [Hle] int sceMtpIfCancelTransfer(int port) => 0;

    // transfers need a host: without a connection they fail
    const int NOT_CONNECTED = unchecked((int)0x80540001);
    [Hle] int sceMtpIfRecvCommand(int port, uint buf, uint size) => NOT_CONNECTED;
    [Hle] int sceMtpIfSendResponse(int port, uint buf, uint size) => NOT_CONNECTED;
    [Hle] int sceMtpIfSendDataWithParam(int port, uint buf, uint size, uint param) => NOT_CONNECTED;
    [Hle] int sceMtpIfRecvDataWithParam(int port, uint buf, uint size, uint param) => NOT_CONNECTED;
    [Hle] int sceMtpIfSendEvent(int port, uint buf, uint size) => NOT_CONNECTED;

    // ---- SceUdcd (USB device controller): cable unplugged, controller deactivated
    const int UDCD_CABLE_DISCONNECTED = 0x10, UDCD_DEACTIVATED = 0x100;

    /// <summary>(SceUdcdDeviceState *state): +0 state, +4 cable, +8 connection, +0xC use_usb_charging (vitasdk udcd.h).</summary>
    [Hle]
    int sceUdcdGetDeviceState(uint state)
    {
        if (state == 0) return unchecked((int)0x80243002); // SCE_UDCD_ERROR_INVALID_ARGUMENT
        GuestMemory.W32(state, UDCD_CABLE_DISCONNECTED | UDCD_DEACTIVATED);
        GuestMemory.W32(state + 4, UDCD_CABLE_DISCONNECTED);
        GuestMemory.W32(state + 8, 0);
        GuestMemory.W32(state + 12, 0);
        return 0;
    }

    /// <summary>(SceUdcdWaitParam *param, int timeout): the state waited for (a cable, a host) never comes.</summary>
    [Hle]
    int sceUdcdWaitState(uint param, int timeoutUs)
    {
        Kernel.Wait.Sleep(timeoutUs > 0 ? (uint)timeoutUs : 100_000u);
        return unchecked((int)0x80243008); // SCE_UDCD_ERROR_WAIT_TIMEOUT
    }

    [Hle] int sceUdcdRegisterCallback(uint callback, int mask) => 0;
    [Hle] int sceUdcdUnregisterCallback(uint callback) => 0;
}
