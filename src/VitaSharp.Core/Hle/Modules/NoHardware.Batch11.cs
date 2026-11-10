using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// Kernel services whose hardware or secure-processor service the emulated console does not have (FW 3.74 bootimage
// modules SceBt, SceBbmc, SceUsbd, SceUsbAudio, SceUsbSerial, SceUsbServ, SceMotionDev, gcauthmgr, SceSblPostSsMgr and
// ss_mgr, read in the modules' code). Each keeps its module's own checks (the caller's permission, NULL pointers, the
// argument ranges) and then answers what the module answers without the device: no Bluetooth device paired or
// connected, no 3G modem, no USB device attached to the host port, no USB microphone, no USB host on the serial
// function; the secure-processor services (game card and ad hoc authentication, licence activation, PSM/PC
// activation, the DMAC5 crypto engine, the QA flags) refuse as on a retail unit with no special token. All are
// documented defaults ([Hle(Default = true)]), counted apart from the real implementations by hle-coverage.

public sealed partial class SystemServicesVsh
{
    const int SBL_ERR_PERM = unchecked((int)0x80010058);

    // ------------------------------------------------------------------ SceBt: the stack with nothing paired or connected
    const int BT_ERR_INTR = unchecked((int)0x802F1901), BT_ERR_AVRCP_NO_DEVICE = unchecked((int)0x802F0604),
        BT_ERR_AUDIO_NO_DEVICE = unchecked((int)0x802F0D01), BT_ERR_AUDIO_STOP_NO_DEVICE = unchecked((int)0x802F0D03),
        BT_ERR_AUDIO_SEND_NO_DEVICE = unchecked((int)0x802F0D05), BT_ERR_AUDIO_RECV_NO_DEVICE = unchecked((int)0x802F0D08),
        BT_ERR_AUDIO_FREQ = unchecked((int)0x802F0D0F), BT_ERR_SPP_NO_DEVICE = unchecked((int)0x802F1602),
        BT_ERR_BIP_NO_DEVICE = unchecked((int)0x802F0F01), BT_ERR_BIP_RECV = unchecked((int)0x802F0F10),
        BT_ERR_DEVICE_ID = unchecked((int)0x802F5001), BT_ERR_TEST = unchecked((int)0x802F1401),
        BT_ERR_L2CAP_TEST = unchecked((int)0x802F1402), BT_ERR_L2CAP_BUFFER = unchecked((int)0x802F1404),
        BT_ERR_STATUS_TEST = unchecked((int)0x802F1701), BT_ERR_HIDVU_NOT_REGISTERED = unchecked((int)0x802F4003),
        BT_ERR_HIDVU_NO_DEVICE = unchecked((int)0x802F4004);

    /// <summary>(uint mac0, uint mac1, ...): AVRCP to a connected headset: none is connected.</summary>
    [Hle(Default = true)] int sceBtAvrcpReadVolume(uint mac0, uint mac1, uint a, uint b) => BT_ERR_AVRCP_NO_DEVICE;
    [Hle(Default = true)] int sceBtAvrcpSendButton(uint mac0, uint mac1, uint button) => BT_ERR_AVRCP_NO_DEVICE;
    [Hle(Default = true)] int sceBtAvrcpSendVolume(uint mac0, uint mac1, uint volume) => BT_ERR_AVRCP_NO_DEVICE;
    /// <summary>(uint mac0, uint mac1, uint type 4/8/0x20000000): the audio channel of a connected device: none.</summary>
    [Hle(Default = true)] int sceBtStartAudio(uint mac0, uint mac1, uint type) => type is 4 or 8 or 0x20000000 ? BT_ERR_AUDIO_NO_DEVICE : unchecked((int)0x802F0D0B);
    [Hle(Default = true)] int sceBtStopAudio(uint mac0, uint mac1, uint type) => type is 4 or 8 or 0x20000000 ? BT_ERR_AUDIO_STOP_NO_DEVICE : unchecked((int)0x802F0D0C);
    [Hle(Default = true)] int sceBtSendAudio(uint mac0, uint mac1, uint buf, uint size) => BT_ERR_AUDIO_SEND_NO_DEVICE;
    [Hle(Default = true)] int sceBtRecvAudio(uint mac0, uint mac1, uint buf, uint size) => BT_ERR_AUDIO_RECV_NO_DEVICE;
    [Hle(Default = true)] int sceBtFreqAudio(uint mac0, uint mac1, uint a, uint b) => BT_ERR_AUDIO_FREQ;
    [Hle(Default = true)] int sceBtSendSpp(uint mac0, uint mac1, uint buf, uint size) => BT_ERR_SPP_NO_DEVICE;
    [Hle(Default = true)] int sceBtRecvSpp(uint mac0, uint mac1, uint buf, uint size) => BT_ERR_SPP_NO_DEVICE;
    [Hle(Default = true)] int sceBtPushBip(uint mac0, uint mac1, uint a, uint b) => BT_ERR_BIP_NO_DEVICE;
    [Hle(Default = true)] int sceBtRecvBip(uint a, uint b) => BT_ERR_BIP_RECV;
    /// <summary>(uint mac0, uint mac1, uint *id): the device id of a paired or seen device: none is known.</summary>
    [Hle(Default = true)] int sceBtGetDeviceId(uint mac0, uint mac1, uint id) => BT_ERR_DEVICE_ID;
    /// <summary>(int mode): page/inquiry scan of the idle stack: accepted.</summary>
    [Hle(Default = true)] int sceBtSetInquiryScan(int mode) => 0;
    // factory test hooks (need a test unit's flag)
    [Hle(Default = true)] int sceBtGetInfoForTest(uint a, uint b, uint c) => BT_ERR_TEST;
    [Hle(Default = true)] int sceBtGetStatusForTest(uint a, uint b, uint c) => BT_ERR_STATUS_TEST;
    [Hle(Default = true)] int sceBtSetStatusForTest(uint a, uint b, uint c) => BT_ERR_STATUS_TEST;
    [Hle(Default = true)] int sceBtSendL2capEchoRequestForTest(uint mac0, uint mac1, uint buf, uint size) => BT_ERR_L2CAP_TEST;
    [Hle(Default = true)] int sceBtSetL2capEchoResponseBufferForTest(uint mac0, uint mac1, uint buf, uint size) => BT_ERR_L2CAP_BUFFER;
    [Hle(Default = true)] int sceBtSetInquiryResultForTest(uint result) => 0;
    // the vendor HID channel (DualShock-style accessories): registration works, no device reports
    int _hidVuCallback;
    [Hle(Default = true)] int sceBtHidVuRegisterCallback(int cb, uint a, uint b) { _hidVuCallback = cb; return 0; }
    [Hle(Default = true)] int sceBtHidVuUnregisterCallback() { if (_hidVuCallback == 0) return BT_ERR_HIDVU_NOT_REGISTERED; _hidVuCallback = 0; return 0; }
    [Hle(Default = true)] int sceBtHidVuReadEvent(uint events, int max) => _hidVuCallback == 0 ? BT_ERR_HIDVU_NOT_REGISTERED : 0;
    [Hle(Default = true)] int sceBtHidVuGetReportInput(uint mac0, uint mac1, uint report) => BT_ERR_HIDVU_NO_DEVICE;
    [Hle(Default = true)] int sceBtHidVuGetReportFeature(uint mac0, uint mac1, uint report) => BT_ERR_HIDVU_NO_DEVICE;
    [Hle(Default = true)] int sceBtHidVuSetReportOutput(uint mac0, uint mac1, uint report, uint size) => BT_ERR_HIDVU_NO_DEVICE;
    [Hle(Default = true)] int sceBtHidVuSetReportFeature(uint mac0, uint mac1, uint report, uint size) => BT_ERR_HIDVU_NO_DEVICE;
    [Hle(Default = true)] int sceBtHidVuSetInterruptOutput(uint mac0, uint mac1, uint report, uint size) => BT_ERR_HIDVU_NO_DEVICE;

    // ------------------------------------------------------------------ SceBbmc: no 3G modem (state pointer NULL)
    [Hle("sceBbmcDLGetSwdlError", Default = true)]
    [Hle("sceBbmcDLQAbort", Default = true)]
    [Hle("sceBbmcDLQBackupEFSNV", Default = true)]
    [Hle("sceBbmcDLQConfirmChangePartition", Default = true)]
    [Hle("sceBbmcDLQDownloadSafeImage", Default = true)]
    [Hle("sceBbmcDLQDownloadSlotImage", Default = true)]
    [Hle("sceBbmcDLQGetCurrentFwDone", Default = true)]
    [Hle("sceBbmcDLQGetCurrentFwStart", Default = true)]
    [Hle("sceBbmcDLQGetFwListDone", Default = true)]
    [Hle("sceBbmcDLQGetFwListStart", Default = true)]
    [Hle("sceBbmcDLQRestoreEFSNV", Default = true)]
    [Hle("sceBbmcDLUpdaterContinue", Default = true)]
    [Hle("sceBbmcDLUpdaterFin", Default = true)]
    [Hle("sceBbmcDLUpdaterGetPkgId", Default = true)]
    [Hle("sceBbmcDLUpdaterGetSequence", Default = true)]
    [Hle("sceBbmcDLUpdaterStart", Default = true)]
    [Hle("sceBbmcEnvelopeCmd", Default = true)]
    [Hle("sceBbmcGetIdStorageFrom3GModule", Default = true)]
    [Hle("sceBbmcGetProactiveCmd", Default = true)]
    [Hle("sceBbmcIsAbleToRunUnderCurrentVoltage", Default = true)]
    [Hle("sceBbmcIsPowerConfigForbiddenMode", Default = true)]
    [Hle("sceBbmcNetworkAttach", Default = true)]
    [Hle("sceBbmcReadSimContentDone", Default = true)]
    [Hle("sceBbmcReadSimContentStart", Default = true)]
    [Hle("sceBbmcTerminalResponseCmd", Default = true)]
    int sceBbmcNoModem2() => Bbmc();
    [Hle("sceBbmcUsbExtIfRead", Default = true)]
    [Hle("sceBbmcUsbExtIfWrite", Default = true)]
    int sceBbmcUsbExtIfIo(uint buf, uint size) => BBMC_ERR_NO_MODEM;

    // ------------------------------------------------------------------ SceUsbd: the USB host port with nothing attached
    const int USBD_ERR_INVALID = unchecked((int)0x80240003);
    [Hle(Default = true)] int sceUsbdInit() => 0;
    [Hle(Default = true)] int sceUsbdEnd() => 0;
    /// <summary>(void *list, int max, int *count): the attached devices: none.</summary>
    [Hle(Default = true)] int sceUsbdGetDeviceList(uint list, int max, uint count) { if (count != 0) GuestMemory.W32(count, 0); return 0; }
    [Hle(Default = true)] int sceUsbdRegisterLdd(uint ldd) => ldd == 0 ? USBD_ERR_INVALID : 0;
    [Hle(Default = true)] int sceUsbdRegisterCompositeLdd(uint ldd) => ldd == 0 ? USBD_ERR_INVALID : 0;
    [Hle(Default = true)] int sceUsbdUnregisterLdd(uint ldd) => ldd == 0 ? USBD_ERR_INVALID : 0;
    // everything about a device: no device id is valid
    [Hle(Default = true)] int sceUsbdGetDescriptorSize(int dev, uint a, uint b, uint c) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdGetDescriptor(int dev, uint a, uint b, uint c) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdOpenPipe(int dev, uint ep, uint pipe) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdClosePipe(int pipe) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdOpenDefaultPipe(int dev) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdReceiveEvent(uint ev) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdAttach(int dev, uint a, uint b, uint c) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdAttachCompositeLdd(int dev, uint a) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdTransferData(uint req) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdIsochTransferData(uint req) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdGetTransferStatus(uint status) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdGetIsochTransferStatus(uint status) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdGetDeviceAddress(int dev, uint addr) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdGetDeviceSpeed(int dev, uint speed) => USBD_ERR_INVALID;
    [Hle(Default = true)] int sceUsbdResetDevice(int dev) => USBD_ERR_INVALID;

    // ------------------------------------------------------------------ SceUsbAudioIn: no USB microphone
    const int USBAUDIO_ERR_NO_DEVICE = unchecked((int)0x803E0004), USBAUDIO_ERR_NULL = unchecked((int)0x803E0001);
    /// <summary>(int *ids, int max, int *count): the USB audio input devices: none.</summary>
    [Hle(Default = true)]
    int sceUsbAudioInGetDeviceIdList(uint ids, int max, uint count)
    {
        if (count == 0) return USBAUDIO_ERR_NULL;
        GuestMemory.W32(count, 0);
        return 0;
    }
    [Hle(Default = true)] int sceUsbAudioInOpenDevice(int id, uint param, uint a) => USBAUDIO_ERR_NO_DEVICE;
    [Hle(Default = true)] int sceUsbAudioInCloseDevice(int id) => USBAUDIO_ERR_NO_DEVICE;
    [Hle(Default = true)] int sceUsbAudioInInput(int id, uint buf) => USBAUDIO_ERR_NO_DEVICE;
    [Hle(Default = true)] int sceUsbAudioInGetDeviceInformation(int id, uint info) => info == 0 ? USBAUDIO_ERR_NULL : USBAUDIO_ERR_NO_DEVICE;
    [Hle(Default = true)] int sceUsbAudioInSetCurrentValueOfVolume(int id, int value) => USBAUDIO_ERR_NO_DEVICE;
    [Hle(Default = true)] int sceUsbAudioInGetMaxValueOfVolume(int id, uint value) => value == 0 ? USBAUDIO_ERR_NULL : USBAUDIO_ERR_NO_DEVICE;
    [Hle(Default = true)] int sceUsbAudioInGetMinValueOfVolume(int id, uint value) => value == 0 ? USBAUDIO_ERR_NULL : USBAUDIO_ERR_NO_DEVICE;

    // ------------------------------------------------------------------ SceUsbSerial: the serial function, no USB host
    const int USBSERIAL_ERR_NOT_STARTED = unchecked((int)0x80244401);
    bool UsbSerialAllowed => Emu.IsSystemProgram;
    [Hle(Default = true)] int sceUsbSerialStart() => UsbSerialAllowed ? USBSERIAL_ERR_NOT_STARTED : SBL_ERR_PERM;
    [Hle(Default = true)] int sceUsbSerialSetup(uint a) => UsbSerialAllowed ? USBSERIAL_ERR_NOT_STARTED : SBL_ERR_PERM;
    [Hle(Default = true)] int sceUsbSerialClose() => UsbSerialAllowed ? USBSERIAL_ERR_NOT_STARTED : SBL_ERR_PERM;
    [Hle(Default = true)] int sceUsbSerialStatus() => UsbSerialAllowed ? USBSERIAL_ERR_NOT_STARTED : SBL_ERR_PERM;
    [Hle(Default = true)] int sceUsbSerialGetRecvBufferSize() => UsbSerialAllowed ? USBSERIAL_ERR_NOT_STARTED : SBL_ERR_PERM;
    [Hle(Default = true)] int sceUsbSerialSend(uint buf, uint size, uint a, uint b) => UsbSerialAllowed ? USBSERIAL_ERR_NOT_STARTED : SBL_ERR_PERM;
    [Hle(Default = true)] int sceUsbSerialRecv(uint buf, uint size, uint a, uint b) => UsbSerialAllowed ? USBSERIAL_ERR_NOT_STARTED : SBL_ERR_PERM;

    // ------------------------------------------------------------------ SceUsbServ: accessory (charging) mode switch
    [Hle(Default = true)] int sceUsbServAccessoryActivate() => 0;
    [Hle(Default = true)] int sceUsbServAccessoryDeactivate() => 0;

    // ------------------------------------------------------------------ secure-processor services (system programs only)
    bool Sbl => Emu.IsSystemProgram;
    int SblUnsupported() => Sbl ? KERNEL_ERR_UNSUP : SBL_ERR_PERM;

    // SceSblGcAuthMgr: game card, ad hoc (PSP BB160/BB224), MS save BBMac/cipher, PC/PSM activation, secure clock
    [Hle("_sceSblGcAuthMgrAdhocBB160Auth1", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB160Auth2", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB160Auth3", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB160Auth4", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB160Auth5", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB160BroadCastDecrypt", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB160BroadCastEncrypt", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB160GetKeys", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB160UniCastDecrypt", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB160UniCastEncrypt", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB224Auth1", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB224Auth2", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB224Auth3", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB224Auth4", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB224Auth5", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB224GetKeys", Default = true)]
    [Hle("_sceSblGcAuthMgrGetMediaIdType01", Default = true)]
    [Hle("_sceSblGcAuthMgrMsSaveBBCipherFinal", Default = true)]
    [Hle("_sceSblGcAuthMgrMsSaveBBCipherInit", Default = true)]
    [Hle("_sceSblGcAuthMgrMsSaveBBCipherUpdate", Default = true)]
    [Hle("_sceSblGcAuthMgrMsSaveBBMacFinal", Default = true)]
    [Hle("_sceSblGcAuthMgrMsSaveBBMacInit", Default = true)]
    [Hle("_sceSblGcAuthMgrMsSaveBBMacUpdate", Default = true)]
    [Hle("_sceSblGcAuthMgrPcactActivation", Default = true)]
    [Hle("_sceSblGcAuthMgrPcactGetChallenge", Default = true)]
    [Hle("_sceSblGcAuthMgrPkgVry", Default = true)]
    [Hle("_sceSblGcAuthMgrPsmactCreateC1", Default = true)]
    [Hle("_sceSblGcAuthMgrPsmactVerifyR1", Default = true)]
    [Hle("_sceSblGcAuthMgrSclkGetData1", Default = true)]
    [Hle("_sceSblGcAuthMgrSclkSetData2", Default = true)]
    int sceSblGcAuthMgrSecure() => SblUnsupported();
    /// <summary>The ad hoc authentication sessions: opened and closed without the secure module.</summary>
    [Hle("_sceSblGcAuthMgrAdhocBB160Init", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB160Shutdown", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB224Init", Default = true)]
    [Hle("_sceSblGcAuthMgrAdhocBB224Shutdown", Default = true)]
    int sceSblGcAuthMgrAdhocSession() => 0;

    // SceSblLicMgr: devkit/testkit activation; a retail unit has none
    [Hle(Default = true)] int sceSblLicMgrGetLicenseStatus() => Sbl ? 0 : SBL_ERR_PERM;
    [Hle(Default = true)] int sceSblLicMgrClearActivationData() => Sbl ? 0 : SBL_ERR_PERM;
    [Hle("sceSblLicMgrActivateDevkit", Default = true)]
    [Hle("sceSblLicMgrActivateFromFs", Default = true)]
    [Hle("sceSblLicMgrGetActivationKey", Default = true)]
    [Hle("sceSblLicMgrGetExpireDate", Default = true)]
    [Hle("sceSblLicMgrGetIssueNo", Default = true)]
    [Hle("sceSblLicMgrGetUsageTimeLimit", Default = true)]
    int sceSblLicMgrSecure() => SblUnsupported();

    // SceSblPmMgr: the product mode (factory/service); off on a retail unit
    [Hle("sceSblPmMgrGetProductModeForUser", Default = true)]
    [Hle("sceSblPmMgrGetProductModeFromNVS", Default = true)]
    int sceSblPmMgrGetProductMode(uint mode)
    {
        if (!Sbl) return SBL_ERR_PERM;
        if (mode == 0) return unchecked((int)0x800F0A16);
        GuestMemory.Write(mode, (byte)0);
        return 0;
    }
    [Hle(Default = true)] int sceSblPmMgrSetProductModeOffForUser() => Sbl ? 0 : SBL_ERR_PERM;
    [Hle(Default = true)] int sceSblPmMgrAuthEtoI(uint a, uint b) => SblUnsupported();

    // SceSblRtcMgr: the content-protection clock (the secure RTC) and its activation
    [Hle("sceSblRtcMgrGetCpRtcLogical", Default = true)]
    [Hle("sceSblRtcMgrGetCpRtcPhysicalForUser", Default = true)]
    int sceSblRtcMgrGetCpRtc(uint tick)
    {
        if (!Sbl) return SBL_ERR_PERM;
        if (tick == 0) return unchecked((int)0x800F1016);
        GuestMemory.Write(tick, Rtc.NowTick);
        return 0;
    }
    [Hle(Default = true)] int sceSblRtcMgrSetCpRtcLogical(uint a, uint b, uint c) => Sbl ? 0 : SBL_ERR_PERM;
    [Hle("sceSblRtcMgrGetCpSerialId", Default = true)]
    [Hle("sceSblRtcMgrSetCpActivationKey", Default = true)]
    [Hle("sceSblRtcMgrSetCpRtcPhysicalAndKey", Default = true)]
    [Hle("sceSblRtcMgrSetCpRtcPhysicalForUser", Default = true)]
    int sceSblRtcMgrSecure() => SblUnsupported();

    // SceSblUtMgr: the user token (QA/test) file and the secure tick
    [Hle(Default = true)]
    int sceSblUtMgrGetCurrentSecureTick(uint tick)
    {
        if (!Sbl) return SBL_ERR_PERM;
        if (tick == 0) return unchecked((int)0x800F1A16);
        GuestMemory.Write(tick, Rtc.NowTick);
        return 0;
    }
    [Hle(Default = true)] int sceSblUtMgrResetUtokenFile() => Sbl ? 0 : SBL_ERR_PERM;
    [Hle("sceSblUtMgrGetUtName", Nid = 0x04CA1311, Library = "SceSblUtMgr", Default = true)]
    [Hle("sceSblUtMgrReadUtoken", Default = true)]
    [Hle("sceSblUtMgrUpdateUtoken", Default = true)]
    int sceSblUtMgrSecure() => SblUnsupported();

    // SceSblQafMgr: the QA flags; a retail unit has no QAF token and allows nothing
    [Hle(Default = true)] int sceSblQafManagerIsAllowKernelDebugForUser() => 0;
    [Hle(Default = true)] int sceSblQafMgrIsAllowForceUpdate() => 0;
    [Hle(Default = true)] int sceSblQafMgrDeleteQafToken2() => Sbl ? 0 : SBL_ERR_PERM;
    [Hle("sceSblQafManagerGetQafNameForUser", Default = true)]
    [Hle("sceSblQafMgrGetQafName", Default = true)]
    [Hle("sceSblQafMgrGetQafToken2", Default = true)]
    [Hle("sceSblQafMgrSetQafToken2", Default = true)]
    int sceSblQafMgrSecure() => SblUnsupported();

    // SceSblDmac5Mgr: the DMAC5 crypto engine with key slots in the secure module
    [Hle("sceSblDmac5EncDec", Default = true)]
    [Hle("sceSblDmac5EncDecKeyGen", Default = true)]
    [Hle("sceSblDmac5HmacKeyGen", Default = true)]
    int sceSblDmac5Secure() => SblUnsupported();
}

public sealed partial class Sensors
{
    const int MOTIONDEV_ERR_NULL = unchecked((int)0x80360001), MOTIONDEV_ERR_NOMEM = unchecked((int)0x80360005);

    /// <summary>(void *info 0x48): the sensor's evaluation (factory test) record: empty on the emulated sensor.</summary>
    [Hle(Default = true)]
    int sceMotionDevGetEvaInfo(uint info) { if (info == 0) return MOTIONDEV_ERR_NULL; GuestMemory.Fill(info, 0, 0x48); return 0; }
    /// <summary>(int type, void *header 0xC): the header of a calibration record: empty.</summary>
    [Hle(Default = true)]
    int sceMotionDevGetCalibrationHeader(int type, uint header) { if (header == 0) return MOTIONDEV_ERR_NULL; GuestMemory.Fill(header, 0, 0xC); return 0; }
    /// <summary>(int type, void *data, uint count &lt; 0x1A): calibration records (0x18 bytes each): none stored (0).</summary>
    [Hle(Default = true)]
    int sceMotionDevGetCalibrationData(int type, uint data, uint count) => data == 0 || count >= 0x1A ? MOTIONDEV_ERR_NULL : 0;
    /// <summary>(void *calib 0x34): the factory magnetometer calibration: empty.</summary>
    [Hle(Default = true)]
    int sceMotionDevGetFactoryMagnCalibData(uint calib) { if (calib == 0) return MOTIONDEV_ERR_NULL; GuestMemory.Fill(calib, 0, 0x34); return 0; }
    /// <summary>(int *level): the magnetometer's stability level: 0.</summary>
    [Hle(Default = true)]
    int sceMotionDevGetCurrentMagnStabilityLevel(uint level) { if (level == 0) return MOTIONDEV_ERR_NULL; GuestMemory.W32(level, 0); return 0; }
    /// <summary>(int mode): the driver's sampling mode; the emulated sensors are always sampled.</summary>
    [Hle] int sceMotionDevSetSamplingMode(int mode) => 0;
}
