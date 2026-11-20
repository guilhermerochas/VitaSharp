using VitaSharp.Core.Kernel;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The kernel entries of functions the HLE already has for user code: the firmware's kernel modules export most of
/// their services twice, as a syscall (what libkernel and the user libraries call, after copying the arguments in from
/// user memory) and as a <c>*ForDriver</c>/<c>*ForKernel</c> export other kernel modules call directly, with the same
/// arguments and the same result (FW 3.74 export tables; prototypes from the SDK headers, psp2kern against psp2). Each
/// entry here runs the user function's HLE on the caller's registers. Twins whose arguments differ (a process id in
/// front, an id instead of a work area) are implemented on their own in the kernel modules, not here; the
/// application manager's are left to its own module.
/// </summary>
public sealed partial class KernelTwins : HleModule
{
    /// <summary>Runs the HLE of user function <paramref name="userNid"/> on the current registers (its result lands in
    /// r0/r1 as usual).</summary>
    void Forward(HleContext context, uint userNid)
    {
        if (Emu.Hle.ImplOf(userNid) is { } implementation)
        {
            implementation(context);
        }
        else
        {
            context.Return(SceError.ERROR);
        }
    }

    // ------------------------------------------------------------------ SceAudioForDriver

    /// <summary>ksceAudioOutSetAdoptMode (SceAudioForDriver 0xDB1C4A83): sceAudioOutSetAdoptMode.</summary>
    [Hle]
    void ksceAudioOutSetAdoptMode(HleContext context) => Forward(context, 0xDCC90EDB);

    /// <summary>ksceAudioOutSetAlcMode (SceAudioForDriver 0x216D8A91): sceAudioOutSetAlcMode.</summary>
    [Hle]
    void ksceAudioOutSetAlcMode(HleContext context) => Forward(context, 0x940CE469);

    /// <summary>ksceAudioOutSetEffectType (SceAudioForDriver 0x8D0AE24F): sceAudioOutSetEffectType.</summary>
    [Hle]
    void ksceAudioOutSetEffectType(HleContext context) => Forward(context, 0xB41FACCE);

    // ------------------------------------------------------------------ SceAudioInForDriver

    /// <summary>ksceAudioInInput (SceAudioInForDriver 0x0AB2743C): sceAudioInInput.</summary>
    [Hle]
    void ksceAudioInInput(HleContext context) => Forward(context, 0x638ADD2D);

    /// <summary>ksceAudioInOpenPort (SceAudioInForDriver 0x9C7404BF): sceAudioInOpenPort.</summary>
    [Hle]
    void ksceAudioInOpenPort(HleContext context) => Forward(context, 0x39B50DC1);

    /// <summary>ksceAudioInReleasePort (SceAudioInForDriver 0x313C969A): sceAudioInReleasePort.</summary>
    [Hle]
    void ksceAudioInReleasePort(HleContext context) => Forward(context, 0x3A61B8C4);

    /// <summary>ksceAudioInSelectInput (SceAudioInForDriver 0x44A497F8): sceAudioInSelectInput.</summary>
    [Hle]
    void ksceAudioInSelectInput(HleContext context) => Forward(context, 0xA0EB852F);

    // ------------------------------------------------------------------ SceAvcodecForDriver

    /// <summary>ksceAvcencCreateEncoder (SceAvcodecForDriver 0xB2A51EB0): sceAvcencCreateEncoder.</summary>
    [Hle]
    void ksceAvcencCreateEncoder(HleContext context) => Forward(context, 0x7F5728CC);

    /// <summary>ksceAvcencCreateEncoderBasic (SceAvcodecForDriver 0xC5E720E2): sceAvcencCreateEncoderBasic.</summary>
    [Hle]
    void ksceAvcencCreateEncoderBasic(HleContext context) => Forward(context, 0xC70B7E2E);

    /// <summary>ksceAvcencCreateEncoderInternal (SceAvcodecForDriver 0x71C37694): sceAvcencCreateEncoderInternal.</summary>
    [Hle]
    void ksceAvcencCreateEncoderInternal(HleContext context) => Forward(context, 0x11CB9524);

    /// <summary>ksceAvcencCsc (SceAvcodecForDriver 0x4F11F915): sceAvcencCsc.</summary>
    [Hle]
    void ksceAvcencCsc(HleContext context) => Forward(context, 0xAC758FF7);

    /// <summary>ksceAvcencDeleteEncoder (SceAvcodecForDriver 0x2895CB6D): sceAvcencDeleteEncoder.</summary>
    [Hle]
    void ksceAvcencDeleteEncoder(HleContext context) => Forward(context, 0xEC08A60C);

    /// <summary>ksceAvcencEncode (SceAvcodecForDriver 0xA0E63278): sceAvcencEncode.</summary>
    [Hle]
    void ksceAvcencEncode(HleContext context) => Forward(context, 0x4B171BEA);

    /// <summary>ksceAvcencEncodeFlush (SceAvcodecForDriver 0x229AB2CF): sceAvcencEncodeFlush.</summary>
    [Hle]
    void ksceAvcencEncodeFlush(HleContext context) => Forward(context, 0x03D1CFED);

    /// <summary>ksceAvcencEncodeStop (SceAvcodecForDriver 0xDE515BFF): sceAvcencEncodeStop.</summary>
    [Hle]
    void ksceAvcencEncodeStop(HleContext context) => Forward(context, 0x4BE87709);

    /// <summary>ksceAvcencGetNalUnit (SceAvcodecForDriver 0x1A584DB7): sceAvcencGetNalUnit.</summary>
    [Hle]
    void ksceAvcencGetNalUnit(HleContext context) => Forward(context, 0xC5609479);

    /// <summary>ksceAvcencQueryEncoderMemSize (SceAvcodecForDriver 0x5C3434FB): sceAvcencQueryEncoderMemSize.</summary>
    [Hle]
    void ksceAvcencQueryEncoderMemSize(HleContext context) => Forward(context, 0xAF317203);

    /// <summary>ksceAvcencQueryEncoderMemSizeBasic (SceAvcodecForDriver 0x7385FB51): sceAvcencQueryEncoderMemSizeBasic.</summary>
    [Hle]
    void ksceAvcencQueryEncoderMemSizeBasic(HleContext context) => Forward(context, 0xC6616EA8);

    /// <summary>ksceAvcencQueryEncoderMemSizeInternal (SceAvcodecForDriver 0xBF3835E2): sceAvcencQueryEncoderMemSizeInternal.</summary>
    [Hle]
    void ksceAvcencQueryEncoderMemSizeInternal(HleContext context) => Forward(context, 0x55F2FD3C);

    /// <summary>ksceAvcencSetAvailablePreset (SceAvcodecForDriver 0x7DC13C29): sceAvcencSetAvailablePreset.</summary>
    [Hle]
    void ksceAvcencSetAvailablePreset(HleContext context) => Forward(context, 0x2238693E);

    /// <summary>ksceAvcencSetEncoderParameter (SceAvcodecForDriver 0xE0232BFE): sceAvcencSetEncoderParameter.</summary>
    [Hle]
    void ksceAvcencSetEncoderParameter(HleContext context) => Forward(context, 0x5B4A70BD);

    /// <summary>ksceJpegEncoderCsc (SceAvcodecForDriver 0xB77B5BFC): sceJpegEncoderCsc.</summary>
    [Hle]
    void ksceJpegEncoderCsc(HleContext context) => Forward(context, 0x824A7D4F);

    /// <summary>ksceJpegEncoderEncode (SceAvcodecForDriver 0x468949D7): sceJpegEncoderEncode.</summary>
    [Hle]
    void ksceJpegEncoderEncode(HleContext context) => Forward(context, 0xC60DE94C);

    /// <summary>ksceJpegEncoderEnd (SceAvcodecForDriver 0x86AD5A4F): sceJpegEncoderEnd.</summary>
    [Hle]
    void ksceJpegEncoderEnd(HleContext context) => Forward(context, 0xC87AA849);

    /// <summary>ksceJpegEncoderGetContextSize (SceAvcodecForDriver 0x4DB66E3A): sceJpegEncoderGetContextSize.</summary>
    [Hle]
    void ksceJpegEncoderGetContextSize(HleContext context) => Forward(context, 0x2B55844D);

    /// <summary>ksceJpegEncoderInit (SceAvcodecForDriver 0x1BE9F5A4): sceJpegEncoderInit.</summary>
    [Hle]
    void ksceJpegEncoderInit(HleContext context) => Forward(context, 0x88DA92B4);

    /// <summary>ksceJpegEncoderSetCompressionRatio (SceAvcodecForDriver 0x91B27CF3): sceJpegEncoderSetCompressionRatio.</summary>
    [Hle]
    void ksceJpegEncoderSetCompressionRatio(HleContext context) => Forward(context, 0xB2B828EC);

    /// <summary>ksceJpegEncoderSetHeaderMode (SceAvcodecForDriver 0x58835DCB): sceJpegEncoderSetHeaderMode.</summary>
    [Hle]
    void ksceJpegEncoderSetHeaderMode(HleContext context) => Forward(context, 0x2F58B12C);

    /// <summary>ksceJpegEncoderSetOutputAddr (SceAvcodecForDriver 0x2C8271BA): sceJpegEncoderSetOutputAddr.</summary>
    [Hle]
    void ksceJpegEncoderSetOutputAddr(HleContext context) => Forward(context, 0x25D52D97);

    /// <summary>ksceJpegEncoderSetValidRegion (SceAvcodecForDriver 0xD0647B74): sceJpegEncoderSetValidRegion.</summary>
    [Hle]
    void ksceJpegEncoderSetValidRegion(HleContext context) => Forward(context, 0x9511F3BC);

    // ------------------------------------------------------------------ SceBacktraceForDriver

    /// <summary>ksceKernelBacktrace (SceBacktraceForDriver 0x166B9C8C): sceKernelBacktrace.</summary>
    [Hle]
    void ksceKernelBacktrace(HleContext context) => Forward(context, 0x894037E8);

    // ------------------------------------------------------------------ SceBtForDriver

    /// <summary>ksceBtAvrcpReadVolume (SceBtForDriver 0xC9C70056): sceBtAvrcpReadVolume.</summary>
    [Hle(Default = true)]
    void ksceBtAvrcpReadVolume(HleContext context) => Forward(context, 0x01000531);

    /// <summary>ksceBtAvrcpSendButton (SceBtForDriver 0xEC5F8519): sceBtAvrcpSendButton.</summary>
    [Hle(Default = true)]
    void ksceBtAvrcpSendButton(HleContext context) => Forward(context, 0x9F8724E7);

    /// <summary>ksceBtAvrcpSendVolume (SceBtForDriver 0x7689DA3D): sceBtAvrcpSendVolume.</summary>
    [Hle(Default = true)]
    void ksceBtAvrcpSendVolume(HleContext context) => Forward(context, 0xC5C7003B);

    /// <summary>ksceBtAvrcpSetPlayStatus (SceBtForDriver 0xC5E7349C): sceBtAvrcpSetPlayStatus.</summary>
    [Hle(Default = true)]
    void ksceBtAvrcpSetPlayStatus(HleContext context) => Forward(context, 0x0DADD51A);

    /// <summary>ksceBtAvrcpSetTitle (SceBtForDriver 0x91EF55AD): sceBtAvrcpSetTitle.</summary>
    [Hle(Default = true)]
    void ksceBtAvrcpSetTitle(HleContext context) => Forward(context, 0xDD3947B9);

    /// <summary>ksceBtDeleteRegisteredInfo (SceBtForDriver 0xE6F659E0): sceBtDeleteRegisteredInfo.</summary>
    [Hle(Default = true)]
    void ksceBtDeleteRegisteredInfo(HleContext context) => Forward(context, 0xACC959A0);

    /// <summary>ksceBtFreqAudio (SceBtForDriver 0xDA20DCC8): sceBtFreqAudio.</summary>
    [Hle(Default = true)]
    void ksceBtFreqAudio(HleContext context) => Forward(context, 0x376DE456);

    /// <summary>ksceBtGetConfiguration (SceBtForDriver 0x94C66A3B): sceBtGetConfiguration.</summary>
    [Hle(Default = true)]
    void ksceBtGetConfiguration(HleContext context) => Forward(context, 0x9D619F35);

    /// <summary>ksceBtGetConnectingInfo (SceBtForDriver 0xE9936446): sceBtGetConnectingInfo.</summary>
    [Hle(Default = true)]
    void ksceBtGetConnectingInfo(HleContext context) => Forward(context, 0xD9B65CAA);

    /// <summary>ksceBtGetDeviceName (SceBtForDriver 0x633F784C): sceBtGetDeviceName.</summary>
    [Hle(Default = true)]
    void ksceBtGetDeviceName(HleContext context) => Forward(context, 0x80977A26);

    /// <summary>ksceBtGetInfoForTest (SceBtForDriver 0x2C9DD7BB): sceBtGetInfoForTest.</summary>
    [Hle(Default = true)]
    void ksceBtGetInfoForTest(HleContext context) => Forward(context, 0xF7756C1F);

    /// <summary>ksceBtGetLastError (SceBtForDriver 0xAD31A43E): sceBtGetLastError.</summary>
    [Hle(Default = true)]
    void ksceBtGetLastError(HleContext context) => Forward(context, 0x4E159A08);

    /// <summary>ksceBtGetRegisteredInfo (SceBtForDriver 0xF86D25E2): sceBtGetRegisteredInfo.</summary>
    [Hle(Default = true)]
    void ksceBtGetRegisteredInfo(HleContext context) => Forward(context, 0xB6C9A094);

    /// <summary>ksceBtGetStatusForTest (SceBtForDriver 0x83781649): sceBtGetStatusForTest.</summary>
    [Hle(Default = true)]
    void ksceBtGetStatusForTest(HleContext context) => Forward(context, 0x48A27F7E);

    /// <summary>ksceBtHfpGetCurrentPhoneNumber (SceBtForDriver 0x7BC66D6D): sceBtHfpGetCurrentPhoneNumber.</summary>
    [Hle]
    void ksceBtHfpGetCurrentPhoneNumber(HleContext context) => Forward(context, 0xB0CD46A4);

    /// <summary>ksceBtHfpRequest (SceBtForDriver 0x6E49D11D): sceBtHfpRequest.</summary>
    [Hle]
    void ksceBtHfpRequest(HleContext context) => Forward(context, 0xD924787E);

    /// <summary>ksceBtHidGetReportDescriptor (SceBtForDriver 0x06CB4ABA): sceBtHidGetReportDescriptor.</summary>
    [Hle]
    void ksceBtHidGetReportDescriptor(HleContext context) => Forward(context, 0x95614634);

    /// <summary>ksceBtHidTransfer (SceBtForDriver 0xF9DCEC77): sceBtHidTransfer.</summary>
    [Hle]
    void ksceBtHidTransfer(HleContext context) => Forward(context, 0x88B34B5B);

    /// <summary>ksceBtHidVuGetReportFeature (SceBtForDriver 0xCA9B885C): sceBtHidVuGetReportFeature.</summary>
    [Hle(Default = true)]
    void ksceBtHidVuGetReportFeature(HleContext context) => Forward(context, 0x3F353762);

    /// <summary>ksceBtHidVuGetReportInput (SceBtForDriver 0x3BBC1EE1): sceBtHidVuGetReportInput.</summary>
    [Hle(Default = true)]
    void ksceBtHidVuGetReportInput(HleContext context) => Forward(context, 0x3CE546C2);

    /// <summary>ksceBtHidVuReadEvent (SceBtForDriver 0x3333A878): sceBtHidVuReadEvent.</summary>
    [Hle(Default = true)]
    void ksceBtHidVuReadEvent(HleContext context) => Forward(context, 0x847B87A3);

    /// <summary>ksceBtHidVuRegisterCallback (SceBtForDriver 0xAB57F8C1): sceBtHidVuRegisterCallback.</summary>
    [Hle(Default = true)]
    void ksceBtHidVuRegisterCallback(HleContext context) => Forward(context, 0x12CD34CC);

    /// <summary>ksceBtHidVuSetInterruptOutput (SceBtForDriver 0xD2449052): sceBtHidVuSetInterruptOutput.</summary>
    [Hle(Default = true)]
    void ksceBtHidVuSetInterruptOutput(HleContext context) => Forward(context, 0xFCDA0ED1);

    /// <summary>ksceBtHidVuSetReportFeature (SceBtForDriver 0x014A1D28): sceBtHidVuSetReportFeature.</summary>
    [Hle(Default = true)]
    void ksceBtHidVuSetReportFeature(HleContext context) => Forward(context, 0x26F68B2F);

    /// <summary>ksceBtHidVuSetReportOutput (SceBtForDriver 0xAB2032A3): sceBtHidVuSetReportOutput.</summary>
    [Hle(Default = true)]
    void ksceBtHidVuSetReportOutput(HleContext context) => Forward(context, 0x6FBD1E7E);

    /// <summary>ksceBtHidVuUnregisterCallback (SceBtForDriver 0x1AB31C7D): sceBtHidVuUnregisterCallback.</summary>
    [Hle(Default = true)]
    void ksceBtHidVuUnregisterCallback(HleContext context) => Forward(context, 0xE15FA065);

    /// <summary>ksceBtPushBip (SceBtForDriver 0x7FF77792): sceBtPushBip.</summary>
    [Hle(Default = true)]
    void ksceBtPushBip(HleContext context) => Forward(context, 0xE4EA1E59);

    /// <summary>ksceBtPushOpp (SceBtForDriver 0x21C788E2): sceBtPushOpp.</summary>
    [Hle]
    void ksceBtPushOpp(HleContext context) => Forward(context, 0xED78EEC3);

    /// <summary>ksceBtReadEvent (SceBtForDriver 0x5ABB9A9D): sceBtReadEvent.</summary>
    [Hle(Default = true)]
    void ksceBtReadEvent(HleContext context) => Forward(context, 0x26BAC23D);

    /// <summary>ksceBtRecvAudio (SceBtForDriver 0xCD7C0492): sceBtRecvAudio.</summary>
    [Hle(Default = true)]
    void ksceBtRecvAudio(HleContext context) => Forward(context, 0x469DD0DA);

    /// <summary>ksceBtRecvBip (SceBtForDriver 0xE8E95977): sceBtRecvBip.</summary>
    [Hle(Default = true)]
    void ksceBtRecvBip(HleContext context) => Forward(context, 0xCEF8F261);

    /// <summary>ksceBtRecvOpp (SceBtForDriver 0x8008AA2C): sceBtRecvOpp.</summary>
    [Hle]
    void ksceBtRecvOpp(HleContext context) => Forward(context, 0xA865A081);

    /// <summary>ksceBtRecvSpp (SceBtForDriver 0xC09D9E7C): sceBtRecvSpp.</summary>
    [Hle(Default = true)]
    void ksceBtRecvSpp(HleContext context) => Forward(context, 0x112A61AC);

    /// <summary>ksceBtRegisterCallback (SceBtForDriver 0x120AC1F7): sceBtRegisterCallback.</summary>
    [Hle(Default = true)]
    void ksceBtRegisterCallback(HleContext context) => Forward(context, 0xA23FB154);

    /// <summary>ksceBtReplyPinCode (SceBtForDriver 0xBB69A62A): sceBtReplyPinCode.</summary>
    [Hle(Default = true)]
    void ksceBtReplyPinCode(HleContext context) => Forward(context, 0xA4FD7F9A);

    /// <summary>ksceBtReplyUserConfirmation (SceBtForDriver 0xB5FA291C): sceBtReplyUserConfirmation.</summary>
    [Hle(Default = true)]
    void ksceBtReplyUserConfirmation(HleContext context) => Forward(context, 0xCAABAAC8);

    /// <summary>ksceBtSendAudio (SceBtForDriver 0x47F19727): sceBtSendAudio.</summary>
    [Hle(Default = true)]
    void ksceBtSendAudio(HleContext context) => Forward(context, 0xE30602A4);

    /// <summary>ksceBtSendL2capEchoRequestForTest (SceBtForDriver 0xE3A66814): sceBtSendL2capEchoRequestForTest.</summary>
    [Hle(Default = true)]
    void ksceBtSendL2capEchoRequestForTest(HleContext context) => Forward(context, 0xCA304973);

    /// <summary>ksceBtSendSpp (SceBtForDriver 0x92241DFB): sceBtSendSpp.</summary>
    [Hle(Default = true)]
    void ksceBtSendSpp(HleContext context) => Forward(context, 0x1FBA44DE);

    /// <summary>ksceBtSetConfiguration (SceBtForDriver 0xEBDCC992): sceBtSetConfiguration.</summary>
    [Hle(Default = true)]
    void ksceBtSetConfiguration(HleContext context) => Forward(context, 0x9B1F2964);

    /// <summary>ksceBtSetContentProtection (SceBtForDriver 0x08632086): sceBtSetContentProtection.</summary>
    [Hle]
    void ksceBtSetContentProtection(HleContext context) => Forward(context, 0x5F9ABEAE);

    /// <summary>ksceBtSetInquiryResultForTest (SceBtForDriver 0x1C26F878): sceBtSetInquiryResultForTest.</summary>
    [Hle(Default = true)]
    void ksceBtSetInquiryResultForTest(HleContext context) => Forward(context, 0x2BADF3A9);

    /// <summary>ksceBtSetInquiryScan (SceBtForDriver 0x4F5A852E): sceBtSetInquiryScan.</summary>
    [Hle(Default = true)]
    void ksceBtSetInquiryScan(HleContext context) => Forward(context, 0x78855913);

    /// <summary>ksceBtSetL2capEchoResponseBufferForTest (SceBtForDriver 0x0B7D8E0E): sceBtSetL2capEchoResponseBufferForTest.</summary>
    [Hle(Default = true)]
    void ksceBtSetL2capEchoResponseBufferForTest(HleContext context) => Forward(context, 0xB624CFFC);

    /// <summary>ksceBtSetStatusForTest (SceBtForDriver 0x9C195411): sceBtSetStatusForTest.</summary>
    [Hle(Default = true)]
    void ksceBtSetStatusForTest(HleContext context) => Forward(context, 0x32DA3573);

    /// <summary>ksceBtStartAudio (SceBtForDriver 0x8D47CABD): sceBtStartAudio.</summary>
    [Hle(Default = true)]
    void ksceBtStartAudio(HleContext context) => Forward(context, 0x3679D50F);

    /// <summary>ksceBtStartConnect (SceBtForDriver 0x6059113A): sceBtStartConnect.</summary>
    [Hle(Default = true)]
    void ksceBtStartConnect(HleContext context) => Forward(context, 0xFF3C57EB);

    /// <summary>ksceBtStartDisconnect (SceBtForDriver 0x50710281): sceBtStartDisconnect.</summary>
    [Hle(Default = true)]
    void ksceBtStartDisconnect(HleContext context) => Forward(context, 0x18286FFF);

    /// <summary>ksceBtStartInquiry (SceBtForDriver 0x9DC7E7E5): sceBtStartInquiry.</summary>
    [Hle(Default = true)]
    void ksceBtStartInquiry(HleContext context) => Forward(context, 0xA5CDDB17);

    /// <summary>ksceBtStopAudio (SceBtForDriver 0xCAE5A9F3): sceBtStopAudio.</summary>
    [Hle(Default = true)]
    void ksceBtStopAudio(HleContext context) => Forward(context, 0xE423D1CC);

    /// <summary>ksceBtStopInquiry (SceBtForDriver 0x6A1D9598): sceBtStopInquiry.</summary>
    [Hle(Default = true)]
    void ksceBtStopInquiry(HleContext context) => Forward(context, 0xF1C4D466);

    /// <summary>ksceBtUnregisterCallback (SceBtForDriver 0xBF72E1BE): sceBtUnregisterCallback.</summary>
    [Hle(Default = true)]
    void ksceBtUnregisterCallback(HleContext context) => Forward(context, 0x605EF71F);

    // ------------------------------------------------------------------ SceCpuForDriver

    /// <summary>ksceKernelCpuId (SceCpuForDriver 0x5E4D5DE1): sceKernelGetCpuId (one function in FW 3.74, exported
    /// under both NIDs).</summary>
    [Hle]
    void ksceKernelCpuId(HleContext context) => Forward(context, 0x2704CFEE);

    /// <summary>ksceKernelCpuAtomicAddAndGet16 (SceCpuForDriver 0x59F74E94): sceKernelAtomicAddAndGet16.</summary>
    [Hle]
    void ksceKernelCpuAtomicAddAndGet16(HleContext context) => Forward(context, 0x495C52EC);

    /// <summary>ksceKernelCpuAtomicAddAndGet32 (SceCpuForDriver 0x5F6A8743): sceKernelAtomicAddAndGet32.</summary>
    [Hle]
    void ksceKernelCpuAtomicAddAndGet32(HleContext context) => Forward(context, 0x2E84A93B);

    /// <summary>ksceKernelCpuAtomicAddAndGet64 (SceCpuForDriver 0x4E459A03): sceKernelAtomicAddAndGet64.</summary>
    [Hle]
    void ksceKernelCpuAtomicAddAndGet64(HleContext context) => Forward(context, 0xB6CE9B9A);

    /// <summary>ksceKernelCpuAtomicAddAndGet8 (SceCpuForDriver 0x1E850481): sceKernelAtomicAddAndGet8.</summary>
    [Hle]
    void ksceKernelCpuAtomicAddAndGet8(HleContext context) => Forward(context, 0x8F7BD940);

    /// <summary>ksceKernelCpuAtomicAddUnless16 (SceCpuForDriver 0x0F84AFE9): sceKernelAtomicAddUnless16.</summary>
    [Hle]
    void ksceKernelCpuAtomicAddUnless16(HleContext context) => Forward(context, 0x1CF4AA4B);

    /// <summary>ksceKernelCpuAtomicAddUnless32 (SceCpuForDriver 0x1F157DC3): sceKernelAtomicAddUnless32.</summary>
    [Hle]
    void ksceKernelCpuAtomicAddUnless32(HleContext context) => Forward(context, 0x4B33FD3C);

    /// <summary>ksceKernelCpuAtomicAddUnless64 (SceCpuForDriver 0x06CCFA4B): sceKernelAtomicAddUnless64.</summary>
    [Hle]
    void ksceKernelCpuAtomicAddUnless64(HleContext context) => Forward(context, 0xFFCE7438);

    /// <summary>ksceKernelCpuAtomicAddUnless8 (SceCpuForDriver 0x5CC62CEC): sceKernelAtomicAddUnless8.</summary>
    [Hle]
    void ksceKernelCpuAtomicAddUnless8(HleContext context) => Forward(context, 0x7FD94393);

    /// <summary>ksceKernelCpuAtomicAndAndGet16 (SceCpuForDriver 0xB281D52A): sceKernelAtomicAndAndGet16.</summary>
    [Hle]
    void ksceKernelCpuAtomicAndAndGet16(HleContext context) => Forward(context, 0xF9890F7E);

    /// <summary>ksceKernelCpuAtomicAndAndGet32 (SceCpuForDriver 0xDF899E4B): sceKernelAtomicAndAndGet32.</summary>
    [Hle]
    void ksceKernelCpuAtomicAndAndGet32(HleContext context) => Forward(context, 0x6709D30C);

    /// <summary>ksceKernelCpuAtomicAndAndGet64 (SceCpuForDriver 0xD18E7B54): sceKernelAtomicAndAndGet64.</summary>
    [Hle]
    void ksceKernelCpuAtomicAndAndGet64(HleContext context) => Forward(context, 0xAED2B370);

    /// <summary>ksceKernelCpuAtomicAndAndGet8 (SceCpuForDriver 0x32B62B1A): sceKernelAtomicAndAndGet8.</summary>
    [Hle]
    void ksceKernelCpuAtomicAndAndGet8(HleContext context) => Forward(context, 0x86B9170F);

    /// <summary>ksceKernelCpuAtomicClearAndGet16 (SceCpuForDriver 0x6B050D7C): sceKernelAtomicClearAndGet16.</summary>
    [Hle]
    void ksceKernelCpuAtomicClearAndGet16(HleContext context) => Forward(context, 0xCC36B775);

    /// <summary>ksceKernelCpuAtomicClearAndGet32 (SceCpuForDriver 0x78C1F148): sceKernelAtomicClearAndGet32.</summary>
    [Hle]
    void ksceKernelCpuAtomicClearAndGet32(HleContext context) => Forward(context, 0x9DAA0DE8);

    /// <summary>ksceKernelCpuAtomicClearAndGet64 (SceCpuForDriver 0x2149CD4C): sceKernelAtomicClearAndGet64.</summary>
    [Hle]
    void ksceKernelCpuAtomicClearAndGet64(HleContext context) => Forward(context, 0x264209F3);

    /// <summary>ksceKernelCpuAtomicClearAndGet8 (SceCpuForDriver 0x8E538AB5): sceKernelAtomicClearAndGet8.</summary>
    [Hle]
    void ksceKernelCpuAtomicClearAndGet8(HleContext context) => Forward(context, 0x17B40010);

    /// <summary>ksceKernelCpuAtomicClearMask16 (SceCpuForDriver 0x1BE58599): sceKernelAtomicClearMask16.</summary>
    [Hle]
    void ksceKernelCpuAtomicClearMask16(HleContext context) => Forward(context, 0x5FE7DFF8);

    /// <summary>ksceKernelCpuAtomicClearMask32 (SceCpuForDriver 0x4AE1BCC0): sceKernelAtomicClearMask32.</summary>
    [Hle]
    void ksceKernelCpuAtomicClearMask32(HleContext context) => Forward(context, 0xE3DF0CB3);

    /// <summary>ksceKernelCpuAtomicClearMask64 (SceCpuForDriver 0x55760309): sceKernelAtomicClearMask64.</summary>
    [Hle]
    void ksceKernelCpuAtomicClearMask64(HleContext context) => Forward(context, 0x953D118A);

    /// <summary>ksceKernelCpuAtomicClearMask8 (SceCpuForDriver 0x1B3336B0): sceKernelAtomicClearMask8.</summary>
    [Hle]
    void ksceKernelCpuAtomicClearMask8(HleContext context) => Forward(context, 0xD7D49E36);

    /// <summary>ksceKernelCpuAtomicCompareAndSet16 (SceCpuForDriver 0x6F63F56D): sceKernelAtomicCompareAndSet16.</summary>
    [Hle]
    void ksceKernelCpuAtomicCompareAndSet16(HleContext context) => Forward(context, 0xE8C01236);

    /// <summary>ksceKernelCpuAtomicCompareAndSet32 (SceCpuForDriver 0xCDA96E81): sceKernelAtomicCompareAndSet32.</summary>
    [Hle]
    void ksceKernelCpuAtomicCompareAndSet32(HleContext context) => Forward(context, 0x1124A1D4);

    /// <summary>ksceKernelCpuAtomicCompareAndSet64 (SceCpuForDriver 0x4B527009): sceKernelAtomicCompareAndSet64.</summary>
    [Hle]
    void ksceKernelCpuAtomicCompareAndSet64(HleContext context) => Forward(context, 0x1EBDFCCD);

    /// <summary>ksceKernelCpuAtomicCompareAndSet8 (SceCpuForDriver 0x3627F4E0): sceKernelAtomicCompareAndSet8.</summary>
    [Hle]
    void ksceKernelCpuAtomicCompareAndSet8(HleContext context) => Forward(context, 0x327DB4C0);

    /// <summary>ksceKernelCpuAtomicDecIfPositive16 (SceCpuForDriver 0x9A693F5B): sceKernelAtomicDecIfPositive16.</summary>
    [Hle]
    void ksceKernelCpuAtomicDecIfPositive16(HleContext context) => Forward(context, 0x323718FB);

    /// <summary>ksceKernelCpuAtomicDecIfPositive32 (SceCpuForDriver 0x2A71B03C): sceKernelAtomicDecIfPositive32.</summary>
    [Hle]
    void ksceKernelCpuAtomicDecIfPositive32(HleContext context) => Forward(context, 0xCA3294F1);

    /// <summary>ksceKernelCpuAtomicDecIfPositive64 (SceCpuForDriver 0x267D0B33): sceKernelAtomicDecIfPositive64.</summary>
    [Hle]
    void ksceKernelCpuAtomicDecIfPositive64(HleContext context) => Forward(context, 0x8BE2A007);

    /// <summary>ksceKernelCpuAtomicDecIfPositive8 (SceCpuForDriver 0x45153D4E): sceKernelAtomicDecIfPositive8.</summary>
    [Hle]
    void ksceKernelCpuAtomicDecIfPositive8(HleContext context) => Forward(context, 0x9DABE6C3);

    /// <summary>ksceKernelCpuAtomicGetAndAdd16 (SceCpuForDriver 0x225DF91A): sceKernelAtomicGetAndAdd16.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndAdd16(HleContext context) => Forward(context, 0x5674DB0C);

    /// <summary>ksceKernelCpuAtomicGetAndAdd32 (SceCpuForDriver 0x341B6E81): sceKernelAtomicGetAndAdd32.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndAdd32(HleContext context) => Forward(context, 0x2611CB0B);

    /// <summary>ksceKernelCpuAtomicGetAndAdd64 (SceCpuForDriver 0x043FD446): sceKernelAtomicGetAndAdd64.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndAdd64(HleContext context) => Forward(context, 0x63DAF37D);

    /// <summary>ksceKernelCpuAtomicGetAndAdd8 (SceCpuForDriver 0xFCDCD4DE): sceKernelAtomicGetAndAdd8.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndAdd8(HleContext context) => Forward(context, 0x27A2AAFA);

    /// <summary>ksceKernelCpuAtomicGetAndAnd16 (SceCpuForDriver 0x4A820BC5): sceKernelAtomicGetAndAnd16.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndAnd16(HleContext context) => Forward(context, 0x7A0CB056);

    /// <summary>ksceKernelCpuAtomicGetAndAnd32 (SceCpuForDriver 0x10EB35EB): sceKernelAtomicGetAndAnd32.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndAnd32(HleContext context) => Forward(context, 0x08266595);

    /// <summary>ksceKernelCpuAtomicGetAndAnd64 (SceCpuForDriver 0x18A17E07): sceKernelAtomicGetAndAnd64.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndAnd64(HleContext context) => Forward(context, 0x4828BC43);

    /// <summary>ksceKernelCpuAtomicGetAndAnd8 (SceCpuForDriver 0xD8E675C0): sceKernelAtomicGetAndAnd8.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndAnd8(HleContext context) => Forward(context, 0x53DCA02B);

    /// <summary>ksceKernelCpuAtomicGetAndClear16 (SceCpuForDriver 0x8E9C086D): sceKernelAtomicGetAndClear16.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndClear16(HleContext context) => Forward(context, 0x57CD7E7D);

    /// <summary>ksceKernelCpuAtomicGetAndClear32 (SceCpuForDriver 0xE36F3A46): sceKernelAtomicGetAndClear32.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndClear32(HleContext context) => Forward(context, 0x0226A017);

    /// <summary>ksceKernelCpuAtomicGetAndClear64 (SceCpuForDriver 0x88BA6002): sceKernelAtomicGetAndClear64.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndClear64(HleContext context) => Forward(context, 0x436AA0B5);

    /// <summary>ksceKernelCpuAtomicGetAndClear8 (SceCpuForDriver 0x382D1466): sceKernelAtomicGetAndClear8.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndClear8(HleContext context) => Forward(context, 0xA10873A4);

    /// <summary>ksceKernelCpuAtomicGetAndOr16 (SceCpuForDriver 0x004F09D1): sceKernelAtomicGetAndOr16.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndOr16(HleContext context) => Forward(context, 0x31E49E73);

    /// <summary>ksceKernelCpuAtomicGetAndOr32 (SceCpuForDriver 0x2A40BB93): sceKernelAtomicGetAndOr32.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndOr32(HleContext context) => Forward(context, 0x984AD276);

    /// <summary>ksceKernelCpuAtomicGetAndOr64 (SceCpuForDriver 0xCB73D6D5): sceKernelAtomicGetAndOr64.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndOr64(HleContext context) => Forward(context, 0xC39186CD);

    /// <summary>ksceKernelCpuAtomicGetAndOr8 (SceCpuForDriver 0xBDF6F8E4): sceKernelAtomicGetAndOr8.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndOr8(HleContext context) => Forward(context, 0x107A68DF);

    /// <summary>ksceKernelCpuAtomicGetAndSet16 (SceCpuForDriver 0x085532C8): sceKernelAtomicGetAndSet16.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndSet16(HleContext context) => Forward(context, 0xF7B9ADFC);

    /// <summary>ksceKernelCpuAtomicGetAndSet32 (SceCpuForDriver 0x0EE04C03): sceKernelAtomicGetAndSet32.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndSet32(HleContext context) => Forward(context, 0x9B43E667);

    /// <summary>ksceKernelCpuAtomicGetAndSet64 (SceCpuForDriver 0xD2DEE625): sceKernelAtomicGetAndSet64.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndSet64(HleContext context) => Forward(context, 0xD82B3A7A);

    /// <summary>ksceKernelCpuAtomicGetAndSet8 (SceCpuForDriver 0x29599FC8): sceKernelAtomicGetAndSet8.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndSet8(HleContext context) => Forward(context, 0x2672EA0B);

    /// <summary>ksceKernelCpuAtomicGetAndSub16 (SceCpuForDriver 0x3EE9B5B8): sceKernelAtomicGetAndSub16.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndSub16(HleContext context) => Forward(context, 0xAC51979C);

    /// <summary>ksceKernelCpuAtomicGetAndSub32 (SceCpuForDriver 0xF891CF2A): sceKernelAtomicGetAndSub32.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndSub32(HleContext context) => Forward(context, 0x115C516F);

    /// <summary>ksceKernelCpuAtomicGetAndSub64 (SceCpuForDriver 0xA7585370): sceKernelAtomicGetAndSub64.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndSub64(HleContext context) => Forward(context, 0x4AE9C8E6);

    /// <summary>ksceKernelCpuAtomicGetAndSub8 (SceCpuForDriver 0x7B43D0D7): sceKernelAtomicGetAndSub8.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndSub8(HleContext context) => Forward(context, 0xCDF5DF67);

    /// <summary>ksceKernelCpuAtomicGetAndXor16 (SceCpuForDriver 0x711801E6): sceKernelAtomicGetAndXor16.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndXor16(HleContext context) => Forward(context, 0x6E2D0B9E);

    /// <summary>ksceKernelCpuAtomicGetAndXor32 (SceCpuForDriver 0x77E34309): sceKernelAtomicGetAndXor32.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndXor32(HleContext context) => Forward(context, 0x38739E2F);

    /// <summary>ksceKernelCpuAtomicGetAndXor64 (SceCpuForDriver 0xE212ECAD): sceKernelAtomicGetAndXor64.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndXor64(HleContext context) => Forward(context, 0x6A19BBE9);

    /// <summary>ksceKernelCpuAtomicGetAndXor8 (SceCpuForDriver 0xBAF47F7B): sceKernelAtomicGetAndXor8.</summary>
    [Hle]
    void ksceKernelCpuAtomicGetAndXor8(HleContext context) => Forward(context, 0x7350B2DF);

    /// <summary>ksceKernelCpuAtomicOrAndGet16 (SceCpuForDriver 0xADD39B84): sceKernelAtomicOrAndGet16.</summary>
    [Hle]
    void ksceKernelCpuAtomicOrAndGet16(HleContext context) => Forward(context, 0x8E248EBD);

    /// <summary>ksceKernelCpuAtomicOrAndGet32 (SceCpuForDriver 0xBC248C30): sceKernelAtomicOrAndGet32.</summary>
    [Hle]
    void ksceKernelCpuAtomicOrAndGet32(HleContext context) => Forward(context, 0xC3B2F7F8);

    /// <summary>ksceKernelCpuAtomicOrAndGet64 (SceCpuForDriver 0x3E218AF7): sceKernelAtomicOrAndGet64.</summary>
    [Hle]
    void ksceKernelCpuAtomicOrAndGet64(HleContext context) => Forward(context, 0x809BBC7D);

    /// <summary>ksceKernelCpuAtomicOrAndGet8 (SceCpuForDriver 0x5D515F1B): sceKernelAtomicOrAndGet8.</summary>
    [Hle]
    void ksceKernelCpuAtomicOrAndGet8(HleContext context) => Forward(context, 0x51693931);

    /// <summary>ksceKernelCpuAtomicSet16 (SceCpuForDriver 0x532CA3E8): sceKernelAtomicSet16.</summary>
    [Hle]
    void ksceKernelCpuAtomicSet16(HleContext context) => Forward(context, 0x450BFECF);

    /// <summary>ksceKernelCpuAtomicSet32 (SceCpuForDriver 0x3168BC57): sceKernelAtomicSet32.</summary>
    [Hle]
    void ksceKernelCpuAtomicSet32(HleContext context) => Forward(context, 0xB69DA09B);

    /// <summary>ksceKernelCpuAtomicSet64 (SceCpuForDriver 0xC381CE8C): sceKernelAtomicSet64.</summary>
    [Hle]
    void ksceKernelCpuAtomicSet64(HleContext context) => Forward(context, 0xC8A4339C);

    /// <summary>ksceKernelCpuAtomicSet8 (SceCpuForDriver 0x0836537E): sceKernelAtomicSet8.</summary>
    [Hle]
    void ksceKernelCpuAtomicSet8(HleContext context) => Forward(context, 0xE960FDA2);

    /// <summary>ksceKernelCpuAtomicSubAndGet16 (SceCpuForDriver 0x515682C9): sceKernelAtomicSubAndGet16.</summary>
    [Hle]
    void ksceKernelCpuAtomicSubAndGet16(HleContext context) => Forward(context, 0xC26BBBB1);

    /// <summary>ksceKernelCpuAtomicSubAndGet32 (SceCpuForDriver 0xA4884C4E): sceKernelAtomicSubAndGet32.</summary>
    [Hle]
    void ksceKernelCpuAtomicSubAndGet32(HleContext context) => Forward(context, 0x01C9CD92);

    /// <summary>ksceKernelCpuAtomicSubAndGet64 (SceCpuForDriver 0xB5F8919C): sceKernelAtomicSubAndGet64.</summary>
    [Hle]
    void ksceKernelCpuAtomicSubAndGet64(HleContext context) => Forward(context, 0x9BB4A94B);

    /// <summary>ksceKernelCpuAtomicSubAndGet8 (SceCpuForDriver 0xEB085370): sceKernelAtomicSubAndGet8.</summary>
    [Hle]
    void ksceKernelCpuAtomicSubAndGet8(HleContext context) => Forward(context, 0x99E1796E);

    /// <summary>ksceKernelCpuAtomicXorAndGet16 (SceCpuForDriver 0x646003D6): sceKernelAtomicXorAndGet16.</summary>
    [Hle]
    void ksceKernelCpuAtomicXorAndGet16(HleContext context) => Forward(context, 0x6F524195);

    /// <summary>ksceKernelCpuAtomicXorAndGet32 (SceCpuForDriver 0x4244BE65): sceKernelAtomicXorAndGet32.</summary>
    [Hle]
    void ksceKernelCpuAtomicXorAndGet32(HleContext context) => Forward(context, 0x46940704);

    /// <summary>ksceKernelCpuAtomicXorAndGet64 (SceCpuForDriver 0x692C51B3): sceKernelAtomicXorAndGet64.</summary>
    [Hle]
    void ksceKernelCpuAtomicXorAndGet64(HleContext context) => Forward(context, 0xDDC6866E);

    /// <summary>ksceKernelCpuAtomicXorAndGet8 (SceCpuForDriver 0x03887992): sceKernelAtomicXorAndGet8.</summary>
    [Hle]
    void ksceKernelCpuAtomicXorAndGet8(HleContext context) => Forward(context, 0x634AF062);

    // ------------------------------------------------------------------ SceCtrlForDriver

    /// <summary>ksceCtrlClearRapidFire (SceCtrlForDriver 0xED138A26): sceCtrlClearRapidFire.</summary>
    [Hle]
    void ksceCtrlClearRapidFire(HleContext context) => Forward(context, 0xD8294C9C);

    /// <summary>ksceCtrlGetButtonIntercept (SceCtrlForDriver 0x6BEB1CD7): sceCtrlGetButtonIntercept.</summary>
    [Hle]
    void ksceCtrlGetButtonIntercept(HleContext context) => Forward(context, 0x6FAFAD05);

    /// <summary>ksceCtrlGetControllerPortInfo (SceCtrlForDriver 0xF11D0D30): sceCtrlGetControllerPortInfo.</summary>
    [Hle]
    void ksceCtrlGetControllerPortInfo(HleContext context) => Forward(context, 0x324F1B66);

    /// <summary>ksceCtrlGetSamplingMode (SceCtrlForDriver 0x145B13BC): sceCtrlGetSamplingMode.</summary>
    [Hle]
    void ksceCtrlGetSamplingMode(HleContext context) => Forward(context, 0xEC752AAF);

    /// <summary>ksceCtrlPeekBufferNegative (SceCtrlForDriver 0x19895843): sceCtrlPeekBufferNegative.</summary>
    [Hle]
    void ksceCtrlPeekBufferNegative(HleContext context) => Forward(context, 0x104ED1A7);

    /// <summary>ksceCtrlReadBufferNegative (SceCtrlForDriver 0x8D4E0DD1): sceCtrlReadBufferNegative.</summary>
    [Hle]
    void ksceCtrlReadBufferNegative(HleContext context) => Forward(context, 0x15F96FB0);

    /// <summary>ksceCtrlReadBufferPositive (SceCtrlForDriver 0x9B96A1AA): sceCtrlReadBufferPositive.</summary>
    [Hle]
    void ksceCtrlReadBufferPositive(HleContext context) => Forward(context, 0x67E7AB83);

    /// <summary>ksceCtrlSetButtonIntercept (SceCtrlForDriver 0x06AA5E1A): sceCtrlSetButtonIntercept.</summary>
    [Hle]
    void ksceCtrlSetButtonIntercept(HleContext context) => Forward(context, 0x433D71F4);

    /// <summary>ksceCtrlSetRapidFire (SceCtrlForDriver 0xA248CDAE): sceCtrlSetRapidFire.</summary>
    [Hle]
    void ksceCtrlSetRapidFire(HleContext context) => Forward(context, 0xE9CB69C8);

    /// <summary>ksceCtrlSetSamplingMode (SceCtrlForDriver 0x80F5E418): sceCtrlSetSamplingMode.</summary>
    [Hle]
    void ksceCtrlSetSamplingMode(HleContext context) => Forward(context, 0xA497B150);

    // ------------------------------------------------------------------ SceDisplayForDriver

    /// <summary>ksceDisplayGetFrameBuf (SceDisplayForDriver 0xEEDA2E54): sceDisplayGetFrameBuf.</summary>
    [Hle]
    void ksceDisplayGetFrameBuf(HleContext context) => Forward(context, 0x42AE6BBC);

    /// <summary>ksceDisplayGetFrameBufInternal (SceDisplayForDriver 0x19F94C63): sceDisplayGetFrameBufInternal.</summary>
    [Hle]
    void ksceDisplayGetFrameBufInternal(HleContext context) => Forward(context, 0x5213F74B);

    /// <summary>ksceDisplayGetMaximumFrameBufResolution (SceDisplayForDriver 0x5AFE6CD3): sceDisplayGetMaximumFrameBufResolution.</summary>
    [Hle]
    void ksceDisplayGetMaximumFrameBufResolution(HleContext context) => Forward(context, 0x8848ADDD);

    /// <summary>ksceDisplayGetPrimaryHead (SceDisplayForDriver 0xC8E554C5): sceDisplayGetPrimaryHead.</summary>
    [Hle]
    void ksceDisplayGetPrimaryHead(HleContext context) => Forward(context, 0x7178FADA);

    /// <summary>ksceDisplayGetResolutionInfoInternal (SceDisplayForDriver 0xB3C6D647): sceDisplayGetResolutionInfoInternal.</summary>
    [Hle]
    void ksceDisplayGetResolutionInfoInternal(HleContext context) => Forward(context, 0x0BFB78C6);

    /// <summary>ksceDisplayGetVcountInternal (SceDisplayForDriver 0x8B5DA27B): sceDisplayGetVcountInternal.</summary>
    [Hle]
    void ksceDisplayGetVcountInternal(HleContext context) => Forward(context, 0x9686859E);

    /// <summary>ksceDisplayRegisterVblankStartCallback (SceDisplayForDriver 0x7FB0BD28): sceDisplayRegisterVblankStartCallback.</summary>
    [Hle]
    void ksceDisplayRegisterVblankStartCallback(HleContext context) => Forward(context, 0x6BDF4C4D);

    /// <summary>ksceDisplaySetFrameBuf (SceDisplayForDriver 0x289D82FE): sceDisplaySetFrameBuf.</summary>
    [Hle]
    void ksceDisplaySetFrameBuf(HleContext context) => Forward(context, 0x7A410B64);

    /// <summary>ksceDisplaySetFrameBufInternal (SceDisplayForDriver 0x16466675): sceDisplaySetFrameBufInternal.</summary>
    [Hle]
    void ksceDisplaySetFrameBufInternal(HleContext context) => Forward(context, 0x89F86A1E);

    /// <summary>ksceDisplayUnregisterVblankStartCallback (SceDisplayForDriver 0x4B27191F): sceDisplayUnregisterVblankStartCallback.</summary>
    [Hle]
    void ksceDisplayUnregisterVblankStartCallback(HleContext context) => Forward(context, 0x98436A80);

    /// <summary>ksceDisplayWaitSetFrameBuf (SceDisplayForDriver 0x1C0C9C4A): sceDisplayWaitSetFrameBuf.</summary>
    [Hle]
    void ksceDisplayWaitSetFrameBuf(HleContext context) => Forward(context, 0x9423560C);

    /// <summary>ksceDisplayWaitSetFrameBufCB (SceDisplayForDriver 0x9D7F203C): sceDisplayWaitSetFrameBufCB.</summary>
    [Hle(Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void ksceDisplayWaitSetFrameBufCB(HleContext context) => Forward(context, 0x814C90AF);

    /// <summary>ksceDisplayWaitSetFrameBufMulti (SceDisplayForDriver 0xE6D27E0A): sceDisplayWaitSetFrameBufMulti.</summary>
    [Hle]
    void ksceDisplayWaitSetFrameBufMulti(HleContext context) => Forward(context, 0x7D9864A8);

    /// <summary>ksceDisplayWaitSetFrameBufMultiCB (SceDisplayForDriver 0x863EACBE): sceDisplayWaitSetFrameBufMultiCB.</summary>
    [Hle(Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void ksceDisplayWaitSetFrameBufMultiCB(HleContext context) => Forward(context, 0x3E796EF5);

    /// <summary>ksceDisplayWaitVblankStart (SceDisplayForDriver 0x984C27E7): sceDisplayWaitVblankStart.</summary>
    [Hle]
    void ksceDisplayWaitVblankStart(HleContext context) => Forward(context, 0x5795E898);

    /// <summary>ksceDisplayWaitVblankStartCB (SceDisplayForDriver 0x46F186C3): sceDisplayWaitVblankStartCB.</summary>
    [Hle(Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void ksceDisplayWaitVblankStartCB(HleContext context) => Forward(context, 0x78B41B92);

    /// <summary>ksceDisplayWaitVblankStartMulti (SceDisplayForDriver 0x40F1469C): sceDisplayWaitVblankStartMulti.</summary>
    [Hle]
    void ksceDisplayWaitVblankStartMulti(HleContext context) => Forward(context, 0xDD0A13B8);

    /// <summary>ksceDisplayWaitVblankStartMultiCB (SceDisplayForDriver 0x77ED8B3A): sceDisplayWaitVblankStartMultiCB.</summary>
    [Hle(Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void ksceDisplayWaitVblankStartMultiCB(HleContext context) => Forward(context, 0x05F27764);

    // ------------------------------------------------------------------ SceDmacmgrForDriver

    /// <summary>ksceDmacMemcpy (SceDmacmgrForDriver 0x00896B11): sceDmacMemcpy.</summary>
    [Hle]
    void ksceDmacMemcpy(HleContext context) => Forward(context, 0x9B2FF739);

    /// <summary>ksceDmacMemset (SceDmacmgrForDriver 0x4BAC049B): sceDmacMemset.</summary>
    [Hle]
    void ksceDmacMemset(HleContext context) => Forward(context, 0xA4C33F11);

    // ------------------------------------------------------------------ SceHidForDriver

    /// <summary>ksceHidConsumerControlEnumerate (SceHidForDriver 0xA60E34F7): sceHidConsumerControlEnumerate.</summary>
    [Hle]
    void ksceHidConsumerControlEnumerate(HleContext context) => Forward(context, 0x160B334A);

    /// <summary>ksceHidConsumerControlRead (SceHidForDriver 0x88F3ECF1): sceHidConsumerControlRead.</summary>
    [Hle]
    void ksceHidConsumerControlRead(HleContext context) => Forward(context, 0x55F64676);

    /// <summary>ksceHidConsumerControlRegisterEnumHintCallback (SceHidForDriver 0x7F444D9C): sceHidConsumerControlRegisterEnumHintCallback.</summary>
    [Hle]
    void ksceHidConsumerControlRegisterEnumHintCallback(HleContext context) => Forward(context, 0xC54BDDB1);

    /// <summary>ksceHidConsumerControlRegisterReadHintCallback (SceHidForDriver 0x710B353C): sceHidConsumerControlRegisterReadHintCallback.</summary>
    [Hle]
    void ksceHidConsumerControlRegisterReadHintCallback(HleContext context) => Forward(context, 0x0D964502);

    /// <summary>ksceHidConsumerControlUnregisterEnumHintCallback (SceHidForDriver 0x6FFC9302): sceHidConsumerControlUnregisterEnumHintCallback.</summary>
    [Hle]
    void ksceHidConsumerControlUnregisterEnumHintCallback(HleContext context) => Forward(context, 0x5D836BC5);

    /// <summary>ksceHidConsumerControlUnregisterReadHintCallback (SceHidForDriver 0x3EC4E469): sceHidConsumerControlUnregisterReadHintCallback.</summary>
    [Hle]
    void ksceHidConsumerControlUnregisterReadHintCallback(HleContext context) => Forward(context, 0xB9E89914);

    /// <summary>ksceHidControllerEnumerate (SceHidForDriver 0x480D225F): sceHidControllerEnumerate.</summary>
    [Hle]
    void ksceHidControllerEnumerate(HleContext context) => Forward(context, 0x983AF9E8);

    /// <summary>ksceHidControllerRead (SceHidForDriver 0xF7470359): sceHidControllerRead.</summary>
    [Hle]
    void ksceHidControllerRead(HleContext context) => Forward(context, 0x792C1505);

    /// <summary>ksceHidControllerRegisterEnumHintCallback (SceHidForDriver 0x8DE66028): sceHidControllerRegisterEnumHintCallback.</summary>
    [Hle]
    void ksceHidControllerRegisterEnumHintCallback(HleContext context) => Forward(context, 0x1E9B25E0);

    /// <summary>ksceHidControllerRegisterReadHintCallback (SceHidForDriver 0xD658B068): sceHidControllerRegisterReadHintCallback.</summary>
    [Hle]
    void ksceHidControllerRegisterReadHintCallback(HleContext context) => Forward(context, 0xBE5992CA);

    /// <summary>ksceHidControllerUnregisterEnumHintCallback (SceHidForDriver 0xFC1C743A): sceHidControllerUnregisterEnumHintCallback.</summary>
    [Hle]
    void ksceHidControllerUnregisterEnumHintCallback(HleContext context) => Forward(context, 0x7E52ACF1);

    /// <summary>ksceHidControllerUnregisterReadHintCallback (SceHidForDriver 0x42B43C21): sceHidControllerUnregisterReadHintCallback.</summary>
    [Hle]
    void ksceHidControllerUnregisterReadHintCallback(HleContext context) => Forward(context, 0x83B7B1D5);

    /// <summary>ksceHidKeyboardClear (SceHidForDriver 0x8CB405AE): sceHidKeyboardClear.</summary>
    [Hle]
    void ksceHidKeyboardClear(HleContext context) => Forward(context, 0x67C01C34);

    /// <summary>ksceHidKeyboardEnumerate (SceHidForDriver 0x270FAB21): sceHidKeyboardEnumerate.</summary>
    [Hle]
    void ksceHidKeyboardEnumerate(HleContext context) => Forward(context, 0x34BDFACE);

    /// <summary>ksceHidKeyboardGetIntercept (SceHidForDriver 0x18DE9BB0): sceHidKeyboardGetIntercept.</summary>
    [Hle]
    void ksceHidKeyboardGetIntercept(HleContext context) => Forward(context, 0xCDA9D24C);

    /// <summary>ksceHidKeyboardPeek (SceHidForDriver 0x1BE424FE): sceHidKeyboardPeek.</summary>
    [Hle]
    void ksceHidKeyboardPeek(HleContext context) => Forward(context, 0x91F5D4BE);

    /// <summary>ksceHidKeyboardRead (SceHidForDriver 0x4BC07107): sceHidKeyboardRead.</summary>
    [Hle]
    void ksceHidKeyboardRead(HleContext context) => Forward(context, 0x224A8AD8);

    /// <summary>ksceHidKeyboardRegisterEnumHintCallback (SceHidForDriver 0x21A23FCD): sceHidKeyboardRegisterEnumHintCallback.</summary>
    [Hle]
    void ksceHidKeyboardRegisterEnumHintCallback(HleContext context) => Forward(context, 0x743A066F);

    /// <summary>ksceHidKeyboardRegisterReadHintCallback (SceHidForDriver 0xB0B22694): sceHidKeyboardRegisterReadHintCallback.</summary>
    [Hle]
    void ksceHidKeyboardRegisterReadHintCallback(HleContext context) => Forward(context, 0x1C740A7A);

    /// <summary>ksceHidKeyboardSetIntercept (SceHidForDriver 0x754E48DF): sceHidKeyboardSetIntercept.</summary>
    [Hle]
    void ksceHidKeyboardSetIntercept(HleContext context) => Forward(context, 0x5D727742);

    /// <summary>ksceHidKeyboardUnregisterEnumHintCallback (SceHidForDriver 0x69DEE02F): sceHidKeyboardUnregisterEnumHintCallback.</summary>
    [Hle]
    void ksceHidKeyboardUnregisterEnumHintCallback(HleContext context) => Forward(context, 0x8068D61E);

    /// <summary>ksceHidKeyboardUnregisterReadHintCallback (SceHidForDriver 0x050351F2): sceHidKeyboardUnregisterReadHintCallback.</summary>
    [Hle]
    void ksceHidKeyboardUnregisterReadHintCallback(HleContext context) => Forward(context, 0xF73C5FBA);

    /// <summary>ksceHidMouseEnumerate (SceHidForDriver 0xA26B1E1C): sceHidMouseEnumerate.</summary>
    [Hle]
    void ksceHidMouseEnumerate(HleContext context) => Forward(context, 0x2A0D2F54);

    /// <summary>ksceHidMouseRead (SceHidForDriver 0xF655462B): sceHidMouseRead.</summary>
    [Hle]
    void ksceHidMouseRead(HleContext context) => Forward(context, 0xBDECBDC0);

    /// <summary>ksceHidMouseRegisterEnumHintCallback (SceHidForDriver 0x141CA100): sceHidMouseRegisterEnumHintCallback.</summary>
    [Hle]
    void ksceHidMouseRegisterEnumHintCallback(HleContext context) => Forward(context, 0xC9E35B47);

    /// <summary>ksceHidMouseRegisterReadHintCallback (SceHidForDriver 0x41690428): sceHidMouseRegisterReadHintCallback.</summary>
    [Hle]
    void ksceHidMouseRegisterReadHintCallback(HleContext context) => Forward(context, 0x90C5CB0A);

    /// <summary>ksceHidMouseUnregisterEnumHintCallback (SceHidForDriver 0x9AFBB275): sceHidMouseUnregisterEnumHintCallback.</summary>
    [Hle]
    void ksceHidMouseUnregisterEnumHintCallback(HleContext context) => Forward(context, 0x3E005E5B);

    /// <summary>ksceHidMouseUnregisterReadHintCallback (SceHidForDriver 0x63776265): sceHidMouseUnregisterReadHintCallback.</summary>
    [Hle]
    void ksceHidMouseUnregisterReadHintCallback(HleContext context) => Forward(context, 0xAC591B15);

    // ------------------------------------------------------------------ SceIofilemgrForDriver

    /// <summary>ksceIoCancel (SceIofilemgrForDriver 0x6D59658D): sceIoCancel.</summary>
    [Hle]
    void ksceIoCancel(HleContext context) => Forward(context, 0xCEF48835);

    /// <summary>ksceIoChstat (SceIofilemgrForDriver 0x7D42B8DC): sceIoChstat.</summary>
    [Hle]
    void ksceIoChstat(HleContext context) => Forward(context, 0x29482F7F);

    /// <summary>ksceIoChstatAsync (SceIofilemgrForDriver 0x7EC442BF): sceIoChstatAsync.</summary>
    [Hle]
    void ksceIoChstatAsync(HleContext context) => Forward(context, 0x9739A5E2);

    /// <summary>ksceIoChstatByFd (SceIofilemgrForDriver 0xDF57A75F): sceIoChstatByFd.</summary>
    [Hle]
    void ksceIoChstatByFd(HleContext context) => Forward(context, 0x6E903AB2);

    /// <summary>ksceIoChstatByFdAsync (SceIofilemgrForDriver 0xEC974400): sceIoChstatByFdAsync.</summary>
    [Hle]
    void ksceIoChstatByFdAsync(HleContext context) => Forward(context, 0xA9F89275);

    /// <summary>ksceIoCloseAsync (SceIofilemgrForDriver 0x11C57CC6): sceIoCloseAsync.</summary>
    [Hle]
    void ksceIoCloseAsync(HleContext context) => Forward(context, 0x8EA3616A);

    /// <summary>ksceIoDclose (SceIofilemgrForDriver 0x19C81DD6): sceIoDclose.</summary>
    [Hle]
    void ksceIoDclose(HleContext context) => Forward(context, 0x422A221A);

    /// <summary>ksceIoDcloseAsync (SceIofilemgrForDriver 0xC08F199F): sceIoDcloseAsync.</summary>
    [Hle]
    void ksceIoDcloseAsync(HleContext context) => Forward(context, 0xDC2D7D38);

    /// <summary>ksceIoDevctl (SceIofilemgrForDriver 0x16882FC4): sceIoDevctl.</summary>
    [Hle]
    void ksceIoDevctl(HleContext context) => Forward(context, 0x04B30CB2);

    /// <summary>ksceIoDevctlAsync (SceIofilemgrForDriver 0xA9302946): sceIoDevctlAsync.</summary>
    [Hle]
    void ksceIoDevctlAsync(HleContext context) => Forward(context, 0x950F78EB);

    /// <summary>ksceIoDopen (SceIofilemgrForDriver 0x463B25CC): sceIoDopen.</summary>
    [Hle]
    void ksceIoDopen(HleContext context) => Forward(context, 0xA9283DD0);

    /// <summary>ksceIoDopenAsync (SceIofilemgrForDriver 0x72F06BDE): sceIoDopenAsync.</summary>
    [Hle]
    void ksceIoDopenAsync(HleContext context) => Forward(context, 0xC49C312F);

    /// <summary>ksceIoDread (SceIofilemgrForDriver 0x20CF5FC7): sceIoDread.</summary>
    [Hle]
    void ksceIoDread(HleContext context) => Forward(context, 0x9C8B6624);

    /// <summary>ksceIoDreadAsync (SceIofilemgrForDriver 0x5982B0E3): sceIoDreadAsync.</summary>
    [Hle]
    void ksceIoDreadAsync(HleContext context) => Forward(context, 0xF59F37B0);

    /// <summary>ksceIoGetThreadDefaultPriorityForSystem (SceIofilemgrForDriver 0x6D0FEDB6): sceIoGetThreadDefaultPriorityForSystem.</summary>
    [Hle]
    void ksceIoGetThreadDefaultPriorityForSystem(HleContext context) => Forward(context, 0xFCBCEAED);

    /// <summary>ksceIoGetstat (SceIofilemgrForDriver 0x75C96D25): sceIoGetstat.</summary>
    [Hle]
    void ksceIoGetstat(HleContext context) => Forward(context, 0xBCA5B623);

    /// <summary>ksceIoGetstatAsync (SceIofilemgrForDriver 0x94A5304A): sceIoGetstatAsync.</summary>
    [Hle]
    void ksceIoGetstatAsync(HleContext context) => Forward(context, 0x82B20B41);

    /// <summary>ksceIoGetstatByFd (SceIofilemgrForDriver 0x462F059B): sceIoGetstatByFd.</summary>
    [Hle]
    void ksceIoGetstatByFd(HleContext context) => Forward(context, 0x57F8CD25);

    /// <summary>ksceIoGetstatByFdAsync (SceIofilemgrForDriver 0x0FEE1238): sceIoGetstatByFdAsync.</summary>
    [Hle]
    void ksceIoGetstatByFdAsync(HleContext context) => Forward(context, 0x5167AC1E);

    /// <summary>ksceIoIoctl (SceIofilemgrForDriver 0x161CD33F): sceIoIoctl.</summary>
    [Hle]
    void ksceIoIoctl(HleContext context) => Forward(context, 0x54ABACFA);

    /// <summary>ksceIoIoctlAsync (SceIofilemgrForDriver 0xB761E91B): sceIoIoctlAsync.</summary>
    [Hle]
    void ksceIoIoctlAsync(HleContext context) => Forward(context, 0x099C54B9);

    /// <summary>ksceIoLseekAsync (SceIofilemgrForDriver 0x541BAABD): sceIoLseekAsync.</summary>
    [Hle]
    void ksceIoLseekAsync(HleContext context) => Forward(context, 0xCAC5D672);

    /// <summary>ksceIoMkdir (SceIofilemgrForDriver 0x7F710B25): sceIoMkdir.</summary>
    [Hle]
    void ksceIoMkdir(HleContext context) => Forward(context, 0x9670D39F);

    /// <summary>ksceIoMkdirAsync (SceIofilemgrForDriver 0x27003443): sceIoMkdirAsync.</summary>
    [Hle]
    void ksceIoMkdirAsync(HleContext context) => Forward(context, 0x8E5FCBB1);

    /// <summary>ksceIoOpenAsync (SceIofilemgrForDriver 0x451001DE): sceIoOpenAsync.</summary>
    [Hle]
    void ksceIoOpenAsync(HleContext context) => Forward(context, 0x6A7EA9FD);

    /// <summary>ksceIoPread (SceIofilemgrForDriver 0x2A17515D): sceIoPread.</summary>
    [Hle]
    void ksceIoPread(HleContext context) => Forward(context, 0x52315AD7);

    /// <summary>ksceIoPreadAsync (SceIofilemgrForDriver 0x64A46A2C): sceIoPreadAsync.</summary>
    [Hle]
    void ksceIoPreadAsync(HleContext context) => Forward(context, 0xA010141E);

    /// <summary>ksceIoPwrite (SceIofilemgrForDriver 0x5F1512C8): sceIoPwrite.</summary>
    [Hle]
    void ksceIoPwrite(HleContext context) => Forward(context, 0x8FFFF5A8);

    /// <summary>ksceIoPwriteAsync (SceIofilemgrForDriver 0x202CDDE3): sceIoPwriteAsync.</summary>
    [Hle]
    void ksceIoPwriteAsync(HleContext context) => Forward(context, 0xED25BEEF);

    /// <summary>ksceIoReadAsync (SceIofilemgrForDriver 0x69047C81): sceIoReadAsync.</summary>
    [Hle]
    void ksceIoReadAsync(HleContext context) => Forward(context, 0x773EBD45);

    /// <summary>ksceIoRemove (SceIofilemgrForDriver 0x0D7BB3E1): sceIoRemove.</summary>
    [Hle]
    void ksceIoRemove(HleContext context) => Forward(context, 0xE20ED0F3);

    /// <summary>ksceIoRemoveAsync (SceIofilemgrForDriver 0xF9D6507D): sceIoRemoveAsync.</summary>
    [Hle]
    void ksceIoRemoveAsync(HleContext context) => Forward(context, 0x446A60AC);

    /// <summary>ksceIoRename (SceIofilemgrForDriver 0xDC0C4997): sceIoRename.</summary>
    [Hle]
    void ksceIoRename(HleContext context) => Forward(context, 0xF737E369);

    /// <summary>ksceIoRenameAsync (SceIofilemgrForDriver 0xAACBC47A): sceIoRenameAsync.</summary>
    [Hle]
    void ksceIoRenameAsync(HleContext context) => Forward(context, 0xEE9857CD);

    /// <summary>ksceIoRmdir (SceIofilemgrForDriver 0x1CC9C634): sceIoRmdir.</summary>
    [Hle]
    void ksceIoRmdir(HleContext context) => Forward(context, 0xE9F91EC8);

    /// <summary>ksceIoRmdirAsync (SceIofilemgrForDriver 0xF5B0B36C): sceIoRmdirAsync.</summary>
    [Hle]
    void ksceIoRmdirAsync(HleContext context) => Forward(context, 0x9694D00F);

    /// <summary>ksceIoSetThreadDefaultPriorityForSystem (SceIofilemgrForDriver 0x3F0FF9D5): sceIoSetThreadDefaultPriorityForSystem.</summary>
    [Hle]
    void ksceIoSetThreadDefaultPriorityForSystem(HleContext context) => Forward(context, 0xB9C9C9CF);

    /// <summary>ksceIoSync (SceIofilemgrForDriver 0xDDF78594): sceIoSync.</summary>
    [Hle]
    void ksceIoSync(HleContext context) => Forward(context, 0x98ACED6D);

    /// <summary>ksceIoSyncAsync (SceIofilemgrForDriver 0x4F9EA8B0): sceIoSyncAsync.</summary>
    [Hle]
    void ksceIoSyncAsync(HleContext context) => Forward(context, 0xF7C7FBFE);

    /// <summary>ksceIoSyncByFd (SceIofilemgrForDriver 0x338DCD68): sceIoSyncByFd.</summary>
    [Hle]
    void ksceIoSyncByFd(HleContext context) => Forward(context, 0x16512F59);

    /// <summary>ksceIoSyncByFdAsync (SceIofilemgrForDriver 0x041209CF): sceIoSyncByFdAsync.</summary>
    [Hle]
    void ksceIoSyncByFdAsync(HleContext context) => Forward(context, 0x7E1367CB);

    /// <summary>ksceIoWrite (SceIofilemgrForDriver 0x21EE91F0): sceIoWrite.</summary>
    [Hle]
    void ksceIoWrite(HleContext context) => Forward(context, 0x34EFD876);

    /// <summary>ksceIoWriteAsync (SceIofilemgrForDriver 0xA1BD13D0): sceIoWriteAsync.</summary>
    [Hle]
    void ksceIoWriteAsync(HleContext context) => Forward(context, 0xE0D63D2A);

    // ------------------------------------------------------------------ SceKernelSuspendForDriver

    /// <summary>ksceKernelPowerTick (SceKernelSuspendForDriver 0xE0489831): sceKernelPowerTick.</summary>
    [Hle]
    void ksceKernelPowerTick(HleContext context) => Forward(context, 0x2252890C);

    // ------------------------------------------------------------------ SceMotionDevForDriver

    /// <summary>ksceMotionDevGetCalibrationData (SceMotionDevForDriver 0xF0251700): sceMotionDevGetCalibrationData.</summary>
    [Hle(Default = true)]
    void ksceMotionDevGetCalibrationData(HleContext context) => Forward(context, 0x716C685B);

    /// <summary>ksceMotionDevGetCalibrationHeader (SceMotionDevForDriver 0x3B23DF55): sceMotionDevGetCalibrationHeader.</summary>
    [Hle(Default = true)]
    void ksceMotionDevGetCalibrationHeader(HleContext context) => Forward(context, 0xBDEC1E32);

    /// <summary>ksceMotionDevGetDeviceInfo (SceMotionDevForDriver 0x3E4BCBC0): sceMotionDevGetDeviceInfo.</summary>
    [Hle]
    void ksceMotionDevGetDeviceInfo(HleContext context) => Forward(context, 0x1F1EFEFB);

    /// <summary>ksceMotionDevGetEvaInfo (SceMotionDevForDriver 0x5B53AC26): sceMotionDevGetEvaInfo.</summary>
    [Hle(Default = true)]
    void ksceMotionDevGetEvaInfo(HleContext context) => Forward(context, 0xE6955280);

    /// <summary>ksceMotionDevIsReady (SceMotionDevForDriver 0x10AAC8EA): sceMotionDevIsReady.</summary>
    [Hle]
    void ksceMotionDevIsReady(HleContext context) => Forward(context, 0xE8A3E48E);

    /// <summary>ksceMotionDevRead (SceMotionDevForDriver 0x3A3407B5): sceMotionDevRead.</summary>
    [Hle]
    void ksceMotionDevRead(HleContext context) => Forward(context, 0xC0095F0F);

    /// <summary>ksceMotionDevSamplingStart (SceMotionDevForDriver 0xC02C85AB): sceMotionDevSamplingStart.</summary>
    [Hle]
    void ksceMotionDevSamplingStart(HleContext context) => Forward(context, 0x47948D9C);

    /// <summary>ksceMotionDevSamplingStop (SceMotionDevForDriver 0xFD0B0785): sceMotionDevSamplingStop.</summary>
    [Hle]
    void ksceMotionDevSamplingStop(HleContext context) => Forward(context, 0x56C1551E);

    /// <summary>ksceMotionDevSetSamplingMode (SceMotionDevForDriver 0xDBAF611A): sceMotionDevSetSamplingMode.</summary>
    [Hle]
    void ksceMotionDevSetSamplingMode(HleContext context) => Forward(context, 0x3EAA8A4F);

    // ------------------------------------------------------------------ SceNetPsForDriver

    /// <summary>ksceNetAccept (SceNetPsForDriver 0x880A5423): sceNetAccept.</summary>
    [Hle]
    void ksceNetAccept(HleContext context) => Forward(context, 0x1ADF9BB1);

    /// <summary>ksceNetBind (SceNetPsForDriver 0x84AB650F): sceNetBind.</summary>
    [Hle]
    void ksceNetBind(HleContext context) => Forward(context, 0x1296A94B);

    /// <summary>ksceNetConnect (SceNetPsForDriver 0x13491DA1): sceNetConnect.</summary>
    [Hle]
    void ksceNetConnect(HleContext context) => Forward(context, 0x11E5B6F6);

    /// <summary>ksceNetGetpeername (SceNetPsForDriver 0xB949AFD5): sceNetGetpeername.</summary>
    [Hle]
    void ksceNetGetpeername(HleContext context) => Forward(context, 0x2348D353);

    /// <summary>ksceNetGetsockname (SceNetPsForDriver 0x8F1BB0E7): sceNetGetsockname.</summary>
    [Hle]
    void ksceNetGetsockname(HleContext context) => Forward(context, 0x1C66A6DB);

    /// <summary>ksceNetGetsockopt (SceNetPsForDriver 0x92EE24A6): sceNetGetsockopt.</summary>
    [Hle]
    void ksceNetGetsockopt(HleContext context) => Forward(context, 0xBA652062);

    /// <summary>ksceNetListen (SceNetPsForDriver 0x080C7992): sceNetListen.</summary>
    [Hle]
    void ksceNetListen(HleContext context) => Forward(context, 0x7A8DA094);

    /// <summary>ksceNetRecvfrom (SceNetPsForDriver 0x49B1669C): sceNetRecvfrom.</summary>
    [Hle]
    void ksceNetRecvfrom(HleContext context) => Forward(context, 0xB226138B);

    /// <summary>ksceNetSendto (SceNetPsForDriver 0xAB746734): sceNetSendto.</summary>
    [Hle]
    void ksceNetSendto(HleContext context) => Forward(context, 0x52DB31D5);

    /// <summary>ksceNetSetsockopt (SceNetPsForDriver 0x4BF5FAB4): sceNetSetsockopt.</summary>
    [Hle]
    void ksceNetSetsockopt(HleContext context) => Forward(context, 0x065505CA);

    /// <summary>ksceNetShutdown (SceNetPsForDriver 0xEEB19FB6): sceNetShutdown.</summary>
    [Hle]
    void ksceNetShutdown(HleContext context) => Forward(context, 0x69E50BB5);

    /// <summary>ksceNetSocket (SceNetPsForDriver 0xEB95B024): sceNetSocket.</summary>
    [Hle]
    void ksceNetSocket(HleContext context) => Forward(context, 0xF084FCE3);

    // ------------------------------------------------------------------ SceProcessmgrForDriver

    /// <summary>ksceKernelGetProcessTimeCore (SceProcessmgrForDriver 0xEC283166): sceKernelGetProcessTimeCore.</summary>
    [Hle]
    void ksceKernelGetProcessTimeCore(HleContext context) => Forward(context, 0xD37A8437);

    /// <summary>ksceKernelGetProcessTimeLowCore (SceProcessmgrForDriver 0x02179E12): sceKernelGetProcessTimeLowCore.</summary>
    [Hle]
    void ksceKernelGetProcessTimeLowCore(HleContext context) => Forward(context, 0xF5D0D4C6);

    /// <summary>ksceKernelGetProcessTimeWideCore (SceProcessmgrForDriver 0x82D94BE9): sceKernelGetProcessTimeWideCore.</summary>
    [Hle]
    void ksceKernelGetProcessTimeWideCore(HleContext context) => Forward(context, 0x89DA0967);

    /// <summary>ksceKernelGetRemoteProcessTime (SceProcessmgrForDriver 0xC074EB31): sceKernelGetRemoteProcessTime.</summary>
    [Hle]
    void ksceKernelGetRemoteProcessTime(HleContext context) => Forward(context, 0xE6E9FCA3);

    /// <summary>ksceKernelIsCDialogAvailable (SceProcessmgrForDriver 0x2F6020B7): sceKernelIsCDialogAvailable.</summary>
    [Hle]
    void ksceKernelIsCDialogAvailable(HleContext context) => Forward(context, 0x143BC4D6);

    /// <summary>ksceKernelIsGameBudget (SceProcessmgrForDriver 0xF7A8BB25): sceKernelIsGameBudget.</summary>
    [Hle]
    void ksceKernelIsGameBudget(HleContext context) => Forward(context, 0xCE0F02F0);

    // ------------------------------------------------------------------ SceProcessmgrForKernel

    /// <summary>ksceKernelExitProcess (SceProcessmgrForKernel 0x905621F9): sceKernelExitProcess.</summary>
    [Hle]
    void ksceKernelExitProcess(HleContext context) => Forward(context, 0x7595D9AA);

    /// <summary>ksceKernelLibcGettimeofday (SceProcessmgrForKernel 0x014F5113): sceKernelLibcGettimeofday.</summary>
    [Hle]
    void ksceKernelLibcGettimeofday(HleContext context) => Forward(context, 0x4B879059);

    /// <summary>ksceKernelLibcTime (SceProcessmgrForKernel 0xB0E076D6): sceKernelLibcTime.</summary>
    [Hle]
    void ksceKernelLibcTime(HleContext context) => Forward(context, 0x0039BE45);

    // ------------------------------------------------------------------ ScePsmDrmForDriver

    /// <summary>kscePsmDrmRemoveActData (ScePsmDrmForDriver 0x4CD5375C): scePsmDrmRemoveActData.</summary>
    [Hle(Default = true)]
    void kscePsmDrmRemoveActData(HleContext context) => Forward(context, 0x0E193CBB);

    // ------------------------------------------------------------------ SceRtcForDriver

    /// <summary>ksceRtcConvertLocalTimeToUtc (SceRtcForDriver 0xAD2F3544): sceRtcConvertLocalTimeToUtc.</summary>
    [Hle]
    void ksceRtcConvertLocalTimeToUtc(HleContext context) => Forward(context, 0x0A05E201);

    /// <summary>ksceRtcFormatRFC2822 (SceRtcForDriver 0xBA3415F8): sceRtcFormatRFC2822.</summary>
    [Hle]
    void ksceRtcFormatRFC2822(HleContext context) => Forward(context, 0x147F2138);

    /// <summary>ksceRtcFormatRFC2822LocalTime (SceRtcForDriver 0x1F7FC209): sceRtcFormatRFC2822LocalTime.</summary>
    [Hle]
    void ksceRtcFormatRFC2822LocalTime(HleContext context) => Forward(context, 0x42CA8EB5);

    /// <summary>ksceRtcFormatRFC3339 (SceRtcForDriver 0xD32AC698): sceRtcFormatRFC3339.</summary>
    [Hle]
    void ksceRtcFormatRFC3339(HleContext context) => Forward(context, 0xCCEA2B54);

    /// <summary>ksceRtcFormatRFC3339LocalTime (SceRtcForDriver 0x35925318): sceRtcFormatRFC3339LocalTime.</summary>
    [Hle]
    void ksceRtcFormatRFC3339LocalTime(HleContext context) => Forward(context, 0x742250A9);

    /// <summary>ksceRtcGetAccumulativeTime (SceRtcForDriver 0x0A86FB04): sceRtcGetAccumulativeTime.</summary>
    [Hle]
    void ksceRtcGetAccumulativeTime(HleContext context) => Forward(context, 0x258BE8EC);

    /// <summary>ksceRtcGetCurrentAdNetworkTick (SceRtcForDriver 0x3E3C09A0): sceRtcGetCurrentAdNetworkTick.</summary>
    [Hle]
    void ksceRtcGetCurrentAdNetworkTick(HleContext context) => Forward(context, 0x5612AF12);

    /// <summary>ksceRtcGetCurrentClock (SceRtcForDriver 0xA930CD1A): sceRtcGetCurrentClock.</summary>
    [Hle]
    void ksceRtcGetCurrentClock(HleContext context) => Forward(context, 0x70FDE8F1);

    /// <summary>ksceRtcGetCurrentClockLocalTime (SceRtcForDriver 0x7DC0DF93): sceRtcGetCurrentClockLocalTime.</summary>
    [Hle]
    void ksceRtcGetCurrentClockLocalTime(HleContext context) => Forward(context, 0x0572EDDC);

    /// <summary>ksceRtcGetCurrentDebugNetworkTick (SceRtcForDriver 0xC141632A): sceRtcGetCurrentDebugNetworkTick.</summary>
    [Hle]
    void ksceRtcGetCurrentDebugNetworkTick(HleContext context) => Forward(context, 0x4ED6BA6C);

    /// <summary>ksceRtcGetCurrentGpsTick (SceRtcForDriver 0xF69B610F): sceRtcGetCurrentGpsTick.</summary>
    [Hle]
    void ksceRtcGetCurrentGpsTick(HleContext context) => Forward(context, 0xBB92534F);

    /// <summary>ksceRtcGetCurrentNetworkTick (SceRtcForDriver 0xF1517B38): sceRtcGetCurrentNetworkTick.</summary>
    [Hle]
    void ksceRtcGetCurrentNetworkTick(HleContext context) => Forward(context, 0xCDDD25FE);

    /// <summary>ksceRtcGetCurrentRetainedNetworkTick (SceRtcForDriver 0x40B4BCFF): sceRtcGetCurrentRetainedNetworkTick.</summary>
    [Hle]
    void ksceRtcGetCurrentRetainedNetworkTick(HleContext context) => Forward(context, 0xA086AE18);

    /// <summary>ksceRtcGetCurrentTick (SceRtcForDriver 0xDEC408D4): sceRtcGetCurrentTick.</summary>
    [Hle]
    void ksceRtcGetCurrentTick(HleContext context) => Forward(context, 0x23F79274);

    /// <summary>ksceRtcGetLastAdjustedTick (SceRtcForDriver 0x8A987573): sceRtcGetLastAdjustedTick.</summary>
    [Hle]
    void ksceRtcGetLastAdjustedTick(HleContext context) => Forward(context, 0x8148E95C);

    /// <summary>ksceRtcGetLastReincarnatedTick (SceRtcForDriver 0x3D614A9A): sceRtcGetLastReincarnatedTick.</summary>
    [Hle]
    void ksceRtcGetLastReincarnatedTick(HleContext context) => Forward(context, 0xA8C16615);

    // ------------------------------------------------------------------ SceSblGcAuthMgrPkgForDriver

    /// <summary>ksceSblGcAuthMgrPkgVry (SceSblGcAuthMgrPkgForDriver 0xE459A9A8): sceSblGcAuthMgrPkgVry.</summary>
    [Hle(Default = true)]
    void ksceSblGcAuthMgrPkgVry(HleContext context) => Forward(context, 0x12C7CD2B);

    // ------------------------------------------------------------------ SceSblPostSsMgrForDriver

    /// <summary>ksceSblLicMgrGetActivationKey (SceSblPostSsMgrForDriver 0xF7F1015B): sceSblLicMgrGetActivationKey.</summary>
    [Hle(Default = true)]
    void ksceSblLicMgrGetActivationKey(HleContext context) => Forward(context, 0x2A437187);

    /// <summary>ksceSblLicMgrGetLicenseStatus (SceSblPostSsMgrForDriver 0x15F37282): sceSblLicMgrGetLicenseStatus.</summary>
    [Hle(Default = true)]
    void ksceSblLicMgrGetLicenseStatus(HleContext context) => Forward(context, 0x0EA6A30C);

    /// <summary>ksceSblRtcMgrGetCpRtcLogical (SceSblPostSsMgrForDriver 0xDE5150FE): sceSblRtcMgrGetCpRtcLogical.</summary>
    [Hle(Default = true)]
    void ksceSblRtcMgrGetCpRtcLogical(HleContext context) => Forward(context, 0xDD44D726);

    /// <summary>ksceSblRtcMgrSetCpRtcLogical (SceSblPostSsMgrForDriver 0xDC68EE5C): sceSblRtcMgrSetCpRtcLogical.</summary>
    [Hle(Default = true)]
    void ksceSblRtcMgrSetCpRtcLogical(HleContext context) => Forward(context, 0x9DFB118B);

    /// <summary>ksceSblUtMgrResetUtokenFile (SceSblPostSsMgrForDriver 0x1FF699DD): sceSblUtMgrResetUtokenFile.</summary>
    [Hle(Default = true)]
    void ksceSblUtMgrResetUtokenFile(HleContext context) => Forward(context, 0x1CD57182);

    // ------------------------------------------------------------------ SceThreadmgrForDriver

    /// <summary>ksceKernelCancelCallback (SceThreadmgrForDriver 0xC040EC1C): sceKernelCancelCallback.</summary>
    [Hle]
    void ksceKernelCancelCallback(HleContext context) => Forward(context, 0x30741EF2);

    /// <summary>ksceKernelCancelMsgPipe (SceThreadmgrForDriver 0x9D6A2311): sceKernelCancelMsgPipe.</summary>
    [Hle]
    void ksceKernelCancelMsgPipe(HleContext context) => Forward(context, 0xEF14BA37);

    /// <summary>ksceKernelCancelMutex (SceThreadmgrForDriver 0x7204B846): sceKernelCancelMutex.</summary>
    [Hle]
    void ksceKernelCancelMutex(HleContext context) => Forward(context, 0x2144890D);

    /// <summary>ksceKernelChangeCurrentThreadAttr (SceThreadmgrForDriver 0x751C9B7A): sceKernelChangeCurrentThreadAttr.</summary>
    [Hle]
    void ksceKernelChangeCurrentThreadAttr(HleContext context) => Forward(context, 0x36833F93);

    /// <summary>ksceKernelChangeThreadCpuAffinityMask (SceThreadmgrForDriver 0x6D0733A8): sceKernelChangeThreadCpuAffinityMask.</summary>
    [Hle]
    void ksceKernelChangeThreadCpuAffinityMask(HleContext context) => Forward(context, 0x15129174);

    /// <summary>ksceKernelChangeThreadPriority (SceThreadmgrForDriver 0x63DAB420): sceKernelChangeThreadPriority.</summary>
    [Hle]
    void ksceKernelChangeThreadPriority(HleContext context) => Forward(context, 0xBD0139F2);

    /// <summary>ksceKernelChangeThreadVfpException (SceThreadmgrForDriver 0xF4C81683): sceKernelChangeThreadVfpException.</summary>
    [Hle]
    void ksceKernelChangeThreadVfpException(HleContext context) => Forward(context, 0xCC18FBAE);

    /// <summary>ksceKernelClearEvent (SceThreadmgrForDriver 0x9C335818): sceKernelClearEvent.</summary>
    [Hle]
    void ksceKernelClearEvent(HleContext context) => Forward(context, 0x7B2A4B28);

    /// <summary>ksceKernelClearEventFlag (SceThreadmgrForDriver 0x4F1DA3BE): sceKernelClearEventFlag.</summary>
    [Hle]
    void ksceKernelClearEventFlag(HleContext context) => Forward(context, 0x4CB87CA7);

    /// <summary>ksceKernelCreateCallback (SceThreadmgrForDriver 0x1C41614C): sceKernelCreateCallback.</summary>
    [Hle]
    void ksceKernelCreateCallback(HleContext context) => Forward(context, 0xB19CF7E9);

    /// <summary>ksceKernelCreateCond (SceThreadmgrForDriver 0xDB6CD34A): sceKernelCreateCond.</summary>
    [Hle]
    void ksceKernelCreateCond(HleContext context) => Forward(context, 0x50572FDA);

    /// <summary>ksceKernelCreateEventFlag (SceThreadmgrForDriver 0x4336BAA4): sceKernelCreateEventFlag.</summary>
    [Hle]
    void ksceKernelCreateEventFlag(HleContext context) => Forward(context, 0x8516D040);

    /// <summary>ksceKernelCreateMsgPipe (SceThreadmgrForDriver 0xBF631145): sceKernelCreateMsgPipe.</summary>
    [Hle]
    void ksceKernelCreateMsgPipe(HleContext context) => Forward(context, 0x0A10C1C8);

    /// <summary>ksceKernelCreateSema (SceThreadmgrForDriver 0x30E93C31): sceKernelCreateSema.</summary>
    [Hle]
    void ksceKernelCreateSema(HleContext context) => Forward(context, 0x1BD67366);

    /// <summary>ksceKernelCreateSimpleEvent (SceThreadmgrForDriver 0x357A8177): sceKernelCreateSimpleEvent.</summary>
    [Hle]
    void ksceKernelCreateSimpleEvent(HleContext context) => Forward(context, 0xE6DB2494);

    /// <summary>ksceKernelDeleteCallback (SceThreadmgrForDriver 0x3A7E17F6): sceKernelDeleteCallback.</summary>
    [Hle]
    void ksceKernelDeleteCallback(HleContext context) => Forward(context, 0xD469676B);

    /// <summary>ksceKernelDeleteCond (SceThreadmgrForDriver 0xAEE0D27C): sceKernelDeleteCond.</summary>
    [Hle]
    void ksceKernelDeleteCond(HleContext context) => Forward(context, 0x879E6EBD);

    /// <summary>ksceKernelDeleteEventFlag (SceThreadmgrForDriver 0x71ECB352): sceKernelDeleteEventFlag.</summary>
    [Hle]
    void ksceKernelDeleteEventFlag(HleContext context) => Forward(context, 0x5840162C);

    /// <summary>ksceKernelDeleteMsgPipe (SceThreadmgrForDriver 0xB3453F88): sceKernelDeleteMsgPipe.</summary>
    [Hle]
    void ksceKernelDeleteMsgPipe(HleContext context) => Forward(context, 0xE78BCCF7);

    /// <summary>ksceKernelDeleteSema (SceThreadmgrForDriver 0x16A35E58): sceKernelDeleteSema.</summary>
    [Hle]
    void ksceKernelDeleteSema(HleContext context) => Forward(context, 0xDB32948A);

    /// <summary>ksceKernelDeleteThread (SceThreadmgrForDriver 0xAC834F3F): sceKernelDeleteThread.</summary>
    [Hle]
    void ksceKernelDeleteThread(HleContext context) => Forward(context, 0x1BBDE3D9);

    /// <summary>ksceKernelGetCallbackCount (SceThreadmgrForDriver 0x0892D8DF): sceKernelGetCallbackCount.</summary>
    [Hle]
    void ksceKernelGetCallbackCount(HleContext context) => Forward(context, 0x038644D5);

    /// <summary>ksceKernelGetEventFlagInfo (SceThreadmgrForDriver 0x612277C9): sceKernelGetEventFlagInfo.</summary>
    [Hle]
    void ksceKernelGetEventFlagInfo(HleContext context) => Forward(context, 0x8BA4C0C1);

    /// <summary>ksceKernelGetMsgPipeInfo (SceThreadmgrForDriver 0x207E0E18): sceKernelGetMsgPipeInfo.</summary>
    [Hle]
    void ksceKernelGetMsgPipeInfo(HleContext context) => Forward(context, 0x4046D16B);

    /// <summary>ksceKernelGetSemaInfo (SceThreadmgrForDriver 0xC99DCF0D): sceKernelGetSemaInfo.</summary>
    [Hle]
    void ksceKernelGetSemaInfo(HleContext context) => Forward(context, 0x595D3FA6);

    /// <summary>ksceKernelGetTLSAddr (SceThreadmgrForDriver 0xE938FB20): sceKernelGetTLSAddr.</summary>
    [Hle]
    void ksceKernelGetTLSAddr(HleContext context) => Forward(context, 0xB295EB61);

    /// <summary>ksceKernelGetThreadCpuAffinityMask (SceThreadmgrForDriver 0x83DC703D): sceKernelGetThreadCpuAffinityMask.</summary>
    [Hle]
    void ksceKernelGetThreadCpuAffinityMask(HleContext context) => Forward(context, 0xF1AE5654);

    /// <summary>ksceKernelGetThreadId (SceThreadmgrForDriver 0x59D06540): sceKernelGetThreadId.</summary>
    [Hle]
    void ksceKernelGetThreadId(HleContext context) => Forward(context, 0x0FB972F9);

    /// <summary>ksceKernelGetThreadInfo (SceThreadmgrForDriver 0x283807E2): sceKernelGetThreadInfo.</summary>
    [Hle]
    void ksceKernelGetThreadInfo(HleContext context) => Forward(context, 0x8D9C5461);

    /// <summary>ksceKernelGetThreadStackFreeSize (SceThreadmgrForDriver 0x7B278A0B): sceKernelGetThreadStackFreeSize.</summary>
    [Hle]
    void ksceKernelGetThreadStackFreeSize(HleContext context) => Forward(context, 0x4F8A3DA0);

    /// <summary>ksceKernelGetThreadTLSAddr (SceThreadmgrForDriver 0x66EEA46A): sceKernelGetThreadTLSAddr.</summary>
    [Hle]
    void ksceKernelGetThreadTLSAddr(HleContext context) => Forward(context, 0xBACA6891);

    /// <summary>ksceKernelGetThreadmgrUIDClass (SceThreadmgrForDriver 0x0A20775A): sceKernelGetThreadmgrUIDClass.</summary>
    [Hle]
    void ksceKernelGetThreadmgrUIDClass(HleContext context) => Forward(context, 0xC9678F7F);

    /// <summary>ksceKernelGetTimerBaseWide (SceThreadmgrForDriver 0xA6D11DD3): sceKernelGetTimerBaseWide.</summary>
    [Hle]
    void ksceKernelGetTimerBaseWide(HleContext context) => Forward(context, 0x5DBC1960);

    /// <summary>ksceKernelGetTimerTimeWide (SceThreadmgrForDriver 0xC1286004): sceKernelGetTimerTimeWide.</summary>
    [Hle]
    void ksceKernelGetTimerTimeWide(HleContext context) => Forward(context, 0x3EFD3165);

    /// <summary>ksceKernelNotifyCallback (SceThreadmgrForDriver 0xC3E00919): sceKernelNotifyCallback.</summary>
    [Hle]
    void ksceKernelNotifyCallback(HleContext context) => Forward(context, 0xA4683592);

    /// <summary>ksceKernelPollEventFlag (SceThreadmgrForDriver 0x76C6555B): sceKernelPollEventFlag.</summary>
    [Hle]
    void ksceKernelPollEventFlag(HleContext context) => Forward(context, 0x1FBB0FE1);

    /// <summary>ksceKernelPollSema (SceThreadmgrForDriver 0x4FDDFE24): sceKernelPollSema.</summary>
    [Hle]
    void ksceKernelPollSema(HleContext context) => Forward(context, 0x866EF048);

    /// <summary>ksceKernelPulseEvent (SceThreadmgrForDriver 0x2427C81B): sceKernelPulseEvent.</summary>
    [Hle]
    void ksceKernelPulseEvent(HleContext context) => Forward(context, 0x8D27BAD6);

    /// <summary>ksceKernelPulseEventWithNotifyCallback (SceThreadmgrForDriver 0x714A107A): sceKernelPulseEventWithNotifyCallback.</summary>
    [Hle]
    void ksceKernelPulseEventWithNotifyCallback(HleContext context) => Forward(context, 0x92F162F3);

    /// <summary>ksceKernelReceiveMsgPipeVector (SceThreadmgrForDriver 0xDA1F256B): sceKernelReceiveMsgPipeVector.</summary>
    [Hle]
    void ksceKernelReceiveMsgPipeVector(HleContext context) => Forward(context, 0x9F899087);

    /// <summary>ksceKernelReceiveMsgPipeVectorCB (SceThreadmgrForDriver 0xDA5C9AC6): sceKernelReceiveMsgPipeVectorCB.</summary>
    [Hle(Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void ksceKernelReceiveMsgPipeVectorCB(HleContext context) => Forward(context, 0xBE5B3E27);

    /// <summary>ksceKernelRegisterCallbackToEvent (SceThreadmgrForDriver 0x832A7E0C): sceKernelRegisterCallbackToEvent.</summary>
    [Hle]
    void ksceKernelRegisterCallbackToEvent(HleContext context) => Forward(context, 0x76FB37E9);

    /// <summary>ksceKernelSendMsgPipeVector (SceThreadmgrForDriver 0x67DD3BAD): sceKernelSendMsgPipeVector.</summary>
    [Hle]
    void ksceKernelSendMsgPipeVector(HleContext context) => Forward(context, 0x94D506F7);

    /// <summary>ksceKernelSetEvent (SceThreadmgrForDriver 0x9EA3A45C): sceKernelSetEvent.</summary>
    [Hle]
    void ksceKernelSetEvent(HleContext context) => Forward(context, 0x324218CD);

    /// <summary>ksceKernelSetEventFlag (SceThreadmgrForDriver 0xD4780C3E): sceKernelSetEventFlag.</summary>
    [Hle]
    void ksceKernelSetEventFlag(HleContext context) => Forward(context, 0xEC94DFF7);

    /// <summary>ksceKernelSetTimerTimeWide (SceThreadmgrForDriver 0x85195A16): sceKernelSetTimerTimeWide.</summary>
    [Hle]
    void ksceKernelSetTimerTimeWide(HleContext context) => Forward(context, 0x273B4A4D);

    /// <summary>ksceKernelSignalCond (SceThreadmgrForDriver 0xAC616150): sceKernelSignalCond.</summary>
    [Hle]
    void ksceKernelSignalCond(HleContext context) => Forward(context, 0x6ED2E2DC);

    /// <summary>ksceKernelSignalCondAll (SceThreadmgrForDriver 0x6EC78CD0): sceKernelSignalCondAll.</summary>
    [Hle]
    void ksceKernelSignalCondAll(HleContext context) => Forward(context, 0xC2E7AC22);

    /// <summary>ksceKernelSignalCondTo (SceThreadmgrForDriver 0x61533DA9): sceKernelSignalCondTo.</summary>
    [Hle]
    void ksceKernelSignalCondTo(HleContext context) => Forward(context, 0x1269F4EC);

    /// <summary>ksceKernelSignalSema (SceThreadmgrForDriver 0xD270498B): sceKernelSignalSema.</summary>
    [Hle]
    void ksceKernelSignalSema(HleContext context) => Forward(context, 0xE6B761D1);

    /// <summary>ksceKernelStartTimer (SceThreadmgrForDriver 0x84C4CE4D): sceKernelStartTimer.</summary>
    [Hle]
    void ksceKernelStartTimer(HleContext context) => Forward(context, 0x48091E0C);

    /// <summary>ksceKernelStopTimer (SceThreadmgrForDriver 0x474F214B): sceKernelStopTimer.</summary>
    [Hle]
    void ksceKernelStopTimer(HleContext context) => Forward(context, 0x869E9F20);

    /// <summary>ksceKernelTryLockReadRWLock (SceThreadmgrForDriver 0xFC2B5A50): sceKernelTryLockReadRWLock.</summary>
    [Hle]
    void ksceKernelTryLockReadRWLock(HleContext context) => Forward(context, 0xEFDDA456);

    /// <summary>ksceKernelTryLockWriteRWLock (SceThreadmgrForDriver 0xA96F2E5A): sceKernelTryLockWriteRWLock.</summary>
    [Hle]
    void ksceKernelTryLockWriteRWLock(HleContext context) => Forward(context, 0x206CBB66);

    /// <summary>ksceKernelTryReceiveMsgPipeVector (SceThreadmgrForDriver 0xCE09221A): sceKernelTryReceiveMsgPipeVector.</summary>
    [Hle]
    void ksceKernelTryReceiveMsgPipeVector(HleContext context) => Forward(context, 0x86ECC0FF);

    /// <summary>ksceKernelTrySendMsgPipeVector (SceThreadmgrForDriver 0x4CF1BE58): sceKernelTrySendMsgPipeVector.</summary>
    [Hle]
    void ksceKernelTrySendMsgPipeVector(HleContext context) => Forward(context, 0x60DB346F);

    /// <summary>ksceKernelUnlockReadRWLock (SceThreadmgrForDriver 0xDE1B9EEE): sceKernelUnlockReadRWLock.</summary>
    [Hle]
    void ksceKernelUnlockReadRWLock(HleContext context) => Forward(context, 0x3EF91145);

    /// <summary>ksceKernelUnlockWriteRWLock (SceThreadmgrForDriver 0x94A73797): sceKernelUnlockWriteRWLock.</summary>
    [Hle]
    void ksceKernelUnlockWriteRWLock(HleContext context) => Forward(context, 0xB4151397);

    /// <summary>ksceKernelUnregisterCallbackFromEvent (SceThreadmgrForDriver 0x2E48D81C): sceKernelUnregisterCallbackFromEvent.</summary>
    [Hle]
    void ksceKernelUnregisterCallbackFromEvent(HleContext context) => Forward(context, 0x18462B11);

    /// <summary>ksceKernelUnregisterCallbackFromEventAll (SceThreadmgrForDriver 0x8DADBD16): sceKernelUnregisterCallbackFromEventAll.</summary>
    [Hle]
    void ksceKernelUnregisterCallbackFromEventAll(HleContext context) => Forward(context, 0x888A7361);

    /// <summary>ksceKernelWaitCond (SceThreadmgrForDriver 0xCC7E027D): sceKernelWaitCond.</summary>
    [Hle]
    void ksceKernelWaitCond(HleContext context) => Forward(context, 0xC88D44AD);

    /// <summary>ksceKernelWaitEvent (SceThreadmgrForDriver 0xC529EA32): sceKernelWaitEvent.</summary>
    [Hle]
    void ksceKernelWaitEvent(HleContext context) => Forward(context, 0x120F03AF);

    /// <summary>ksceKernelWaitEventCB (SceThreadmgrForDriver 0x360C655C): sceKernelWaitEventCB.</summary>
    [Hle(Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void ksceKernelWaitEventCB(HleContext context) => Forward(context, 0xA0490795);

    /// <summary>ksceKernelWaitEventFlag (SceThreadmgrForDriver 0x0C1D3F20): sceKernelWaitEventFlag.</summary>
    [Hle]
    void ksceKernelWaitEventFlag(HleContext context) => Forward(context, 0x83C0E2AF);

    /// <summary>ksceKernelWaitEventFlagCB (SceThreadmgrForDriver 0x8A35F714): sceKernelWaitEventFlagCB.</summary>
    [Hle(Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void ksceKernelWaitEventFlagCB(HleContext context) => Forward(context, 0xE737B1DF);

    /// <summary>ksceKernelWaitSema (SceThreadmgrForDriver 0x3C8B55A9): sceKernelWaitSema.</summary>
    [Hle]
    void ksceKernelWaitSema(HleContext context) => Forward(context, 0x0C7B834B);

    /// <summary>ksceKernelWaitThreadEndCB (SceThreadmgrForDriver 0x9EF4F62C): sceKernelWaitThreadEndCB.</summary>
    [Hle(Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void ksceKernelWaitThreadEndCB(HleContext context) => Forward(context, 0xC54941ED);

    // ------------------------------------------------------------------ SceTouchForDriver

    /// <summary>ksceTouchActivateRegion (SceTouchForDriver 0xC7F0DAA0): sceTouchActivateRegion.</summary>
    [Hle]
    void ksceTouchActivateRegion(HleContext context) => Forward(context, 0xBD59D652);

    /// <summary>ksceTouchDisableTouchForce (SceTouchForDriver 0xCDE79889): sceTouchDisableTouchForce.</summary>
    [Hle]
    void ksceTouchDisableTouchForce(HleContext context) => Forward(context, 0x41194411);

    /// <summary>ksceTouchEnableIdleTimerCancelSetting (SceTouchForDriver 0xEBF4C86A): sceTouchEnableIdleTimerCancelSetting.</summary>
    [Hle]
    void ksceTouchEnableIdleTimerCancelSetting(HleContext context) => Forward(context, 0x012EAE3A);

    /// <summary>ksceTouchEnableTouchForce (SceTouchForDriver 0xE41B4BF8): sceTouchEnableTouchForce.</summary>
    [Hle]
    void ksceTouchEnableTouchForce(HleContext context) => Forward(context, 0xB18370C2);

    /// <summary>ksceTouchGetDeviceInfo (SceTouchForDriver 0xFD2E0278): sceTouchGetDeviceInfo.</summary>
    [Hle]
    void ksceTouchGetDeviceInfo(HleContext context) => Forward(context, 0xD7889B91);

    /// <summary>ksceTouchGetPanelInfo (SceTouchForDriver 0x937DB4C0): sceTouchGetPanelInfo.</summary>
    [Hle]
    void ksceTouchGetPanelInfo(HleContext context) => Forward(context, 0x10A2CA25);

    /// <summary>ksceTouchGetProcessInfo (SceTouchForDriver 0x4DF3A37C): sceTouchGetProcessInfo.</summary>
    [Hle]
    void ksceTouchGetProcessInfo(HleContext context) => Forward(context, 0xAB364C23);

    /// <summary>ksceTouchGetSamplingState (SceTouchForDriver 0x174D0333): sceTouchGetSamplingState.</summary>
    [Hle]
    void ksceTouchGetSamplingState(HleContext context) => Forward(context, 0x26531526);

    /// <summary>ksceTouchPeek (SceTouchForDriver 0xBAD1960B): sceTouchPeek.</summary>
    [Hle]
    void ksceTouchPeek(HleContext context) => Forward(context, 0xFF082DF0);

    /// <summary>ksceTouchPeekRegion (SceTouchForDriver 0x9B3F7207): sceTouchPeekRegion.</summary>
    [Hle]
    void ksceTouchPeekRegion(HleContext context) => Forward(context, 0x04440622);

    /// <summary>ksceTouchRead (SceTouchForDriver 0x70C8AACE): sceTouchRead.</summary>
    [Hle]
    void ksceTouchRead(HleContext context) => Forward(context, 0x169A1D58);

    /// <summary>ksceTouchReadRegion (SceTouchForDriver 0x9A91F624): sceTouchReadRegion.</summary>
    [Hle]
    void ksceTouchReadRegion(HleContext context) => Forward(context, 0x93B5635B);

    /// <summary>ksceTouchSetRegion (SceTouchForDriver 0x7985B164): sceTouchSetRegion.</summary>
    [Hle]
    void ksceTouchSetRegion(HleContext context) => Forward(context, 0x6134DFBE);

    /// <summary>ksceTouchSetRegionAttr (SceTouchForDriver 0x10A998DE): sceTouchSetRegionAttr.</summary>
    [Hle]
    void ksceTouchSetRegionAttr(HleContext context) => Forward(context, 0x08DD4C7C);

    /// <summary>ksceTouchSetSamplingState (SceTouchForDriver 0x1247257A): sceTouchSetSamplingState.</summary>
    [Hle]
    void ksceTouchSetSamplingState(HleContext context) => Forward(context, 0x1B9C5D14);

    // ------------------------------------------------------------------ SceUsbServForDriver

    /// <summary>ksceUsbServAccessoryActivate (SceUsbServForDriver 0xAA6D4409): sceUsbServAccessoryActivate.</summary>
    [Hle(Default = true)]
    void ksceUsbServAccessoryActivate(HleContext context) => Forward(context, 0xB33AA2EB);

    /// <summary>ksceUsbServAccessoryDeactivate (SceUsbServForDriver 0x853CB8E4): sceUsbServAccessoryDeactivate.</summary>
    [Hle(Default = true)]
    void ksceUsbServAccessoryDeactivate(HleContext context) => Forward(context, 0x154246A9);
}
