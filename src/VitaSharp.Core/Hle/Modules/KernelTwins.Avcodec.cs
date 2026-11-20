namespace VitaSharp.Core.Hle.Modules;

// SceAvcodecForDriver (os0:bootimage SceAvcodec 3.74, read in the module, study only): the decoder side of the
// library. Each SceAvcodec syscall copies its control blocks in from user memory and calls the ForDriver export
// with the same arguments in the same order (the JPEG syscalls take a block holding the arguments after the third
// and pass them on unpacked, which is the user library's own argument list again). So each kernel entry here runs
// the user function's HLE on the caller's registers, over the same decoders, libraries and codec engine memory the
// user side keeps (one model, never a parallel one).
public sealed partial class KernelTwins
{
    /// <summary>SceAvcodecForDriver 0x7E8CC4E5: sceAvcdecCreateDecoder.</summary>
    [Hle(Nid = 0x7E8CC4E5, Library = "SceAvcodecForDriver")]
    void ksceAvcdecCreateDecoder(HleContext context) => Forward(context, 0xE82BB69B);

    /// <summary>SceAvcodecForDriver 0x663B3195: sceAvcdecCreateDecoderInternal.</summary>
    [Hle(Nid = 0x663B3195, Library = "SceAvcodecForDriver")]
    void ksceAvcdecCreateDecoderInternal(HleContext context) => Forward(context, 0x808FECBD);

    /// <summary>SceAvcodecForDriver 0x301CCA25: sceAvcdecCreateDecoderNongameapp.</summary>
    [Hle(Nid = 0x301CCA25, Library = "SceAvcodecForDriver")]
    void ksceAvcdecCreateDecoderNongameapp(HleContext context) => Forward(context, 0x3102196C);

    /// <summary>SceAvcodecForDriver 0x16B501C9: sceAvcdecCsc.</summary>
    [Hle(Nid = 0x16B501C9, Library = "SceAvcodecForDriver")]
    void ksceAvcdecCsc(HleContext context) => Forward(context, 0xB2A428DB);

    /// <summary>SceAvcodecForDriver 0xB8A9632A: sceAvcdecCscInternal.</summary>
    [Hle(Nid = 0xB8A9632A, Library = "SceAvcodecForDriver")]
    void ksceAvcdecCscInternal(HleContext context) => Forward(context, 0xC300BF46);

    /// <summary>SceAvcodecForDriver 0x48DECA56: sceAvcdecDecode.</summary>
    [Hle(Nid = 0x48DECA56, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecode(HleContext context) => Forward(context, 0xD6190A06);

    /// <summary>SceAvcodecForDriver 0xD2ACCE22: sceAvcdecDecodeAuInternal.</summary>
    [Hle(Nid = 0xD2ACCE22, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeAuInternal(HleContext context) => Forward(context, 0x2385A82B);

    /// <summary>SceAvcodecForDriver 0xF714DB9A: sceAvcdecDecodeAuNalAuInternal.</summary>
    [Hle(Nid = 0xF714DB9A, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeAuNalAuInternal(HleContext context) => Forward(context, 0x68628BC4);

    /// <summary>SceAvcodecForDriver 0xC9EF7057: sceAvcdecDecodeAuNalAuNongameapp.</summary>
    [Hle(Nid = 0xC9EF7057, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeAuNalAuNongameapp(HleContext context) => Forward(context, 0x0E4A6EE3);

    /// <summary>SceAvcodecForDriver 0x78BA8E98: sceAvcdecDecodeAuNongameapp.</summary>
    [Hle(Nid = 0x78BA8E98, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeAuNongameapp(HleContext context) => Forward(context, 0x867C75AD);

    /// <summary>SceAvcodecForDriver 0x43A9CD35: sceAvcdecDecodeAvailableSize.</summary>
    [Hle(Nid = 0x43A9CD35, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeAvailableSize(HleContext context) => Forward(context, 0x441673E3);

    /// <summary>SceAvcodecForDriver 0xD46B8463: sceAvcdecDecodeFlush.</summary>
    [Hle(Nid = 0xD46B8463, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeFlush(HleContext context) => Forward(context, 0x25F31020);

    /// <summary>SceAvcodecForDriver 0xD7CD7F82: sceAvcdecDecodeGetPictureInternal.</summary>
    [Hle(Nid = 0xD7CD7F82, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeGetPictureInternal(HleContext context) => Forward(context, 0x63237A8F);

    /// <summary>SceAvcodecForDriver 0xF8BCBE20: sceAvcdecDecodeGetPictureNongameapp.</summary>
    [Hle(Nid = 0xF8BCBE20, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeGetPictureNongameapp(HleContext context) => Forward(context, 0xCF3CFC70);

    /// <summary>SceAvcodecForDriver 0x6E250A5F: sceAvcdecDecodeGetPictureWithWorkPictureInternal.</summary>
    [Hle(Nid = 0x6E250A5F, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeGetPictureWithWorkPictureInternal(HleContext context) => Forward(context, 0x66F65155);

    /// <summary>SceAvcodecForDriver 0x0CC84413: sceAvcdecDecodeNalAu.</summary>
    [Hle(Nid = 0x0CC84413, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeNalAu(HleContext context) => Forward(context, 0x6C68A38F);

    /// <summary>SceAvcodecForDriver 0x20892154: sceAvcdecDecodeNalAuWithWorkPicture.</summary>
    [Hle(Nid = 0x20892154, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeNalAuWithWorkPicture(HleContext context) => Forward(context, 0xDDF41798);

    /// <summary>SceAvcodecForDriver 0xC518A109: sceAvcdecDecodeSetTrickModeNongameapp.</summary>
    [Hle(Nid = 0xC518A109, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeSetTrickModeNongameapp(HleContext context) => Forward(context, 0x7AEB56AB);

    /// <summary>SceAvcodecForDriver 0xA2085B6B: sceAvcdecDecodeSetUserDataSei1FieldMemSizeNongameapp.</summary>
    [Hle(Nid = 0xA2085B6B, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeSetUserDataSei1FieldMemSizeNongameapp(HleContext context) => Forward(context, 0xD037206A);

    /// <summary>SceAvcodecForDriver 0xDFC0AAC6: sceAvcdecDecodeStop.</summary>
    [Hle(Nid = 0xDFC0AAC6, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeStop(HleContext context) => Forward(context, 0x9648D853);

    /// <summary>SceAvcodecForDriver 0xE355CAAE: sceAvcdecDecodeStopWithWorkPicture.</summary>
    [Hle(Nid = 0xE355CAAE, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeStopWithWorkPicture(HleContext context) => Forward(context, 0x67AD42C3);

    /// <summary>SceAvcodecForDriver 0xEAEB16D5: sceAvcdecDecodeWithWorkPicture.</summary>
    [Hle(Nid = 0xEAEB16D5, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDecodeWithWorkPicture(HleContext context) => Forward(context, 0xBA5A009B);

    /// <summary>SceAvcodecForDriver 0xEA07A3B7: sceAvcdecDeleteDecoder.</summary>
    [Hle(Nid = 0xEA07A3B7, Library = "SceAvcodecForDriver")]
    void ksceAvcdecDeleteDecoder(HleContext context) => Forward(context, 0x8A0E359E);

    /// <summary>SceAvcodecForDriver 0xBC244D21: sceAvcdecGetSeiPictureTimingInternal.</summary>
    [Hle(Nid = 0xBC244D21, Library = "SceAvcodecForDriver")]
    void ksceAvcdecGetSeiPictureTimingInternal(HleContext context) => Forward(context, 0x8EB07E2F);

    /// <summary>SceAvcodecForDriver 0x427914D5: sceAvcdecGetSeiUserDataNongameapp.</summary>
    [Hle(Nid = 0x427914D5, Library = "SceAvcodecForDriver")]
    void ksceAvcdecGetSeiUserDataNongameapp(HleContext context) => Forward(context, 0xD6B26396);

    /// <summary>SceAvcodecForDriver 0xC114C499: sceAvcdecQueryDecoderMemSize.</summary>
    [Hle(Nid = 0xC114C499, Library = "SceAvcodecForDriver")]
    void ksceAvcdecQueryDecoderMemSize(HleContext context) => Forward(context, 0x97E95EDB);

    /// <summary>SceAvcodecForDriver 0xD5A34236: sceAvcdecQueryDecoderMemSizeInternal.</summary>
    [Hle(Nid = 0xD5A34236, Library = "SceAvcodecForDriver")]
    void ksceAvcdecQueryDecoderMemSizeInternal(HleContext context) => Forward(context, 0x7BC8DFEA);

    /// <summary>SceAvcodecForDriver 0x01387A93: sceAvcdecQueryDecoderMemSizeNongameapp.</summary>
    [Hle(Nid = 0x01387A93, Library = "SceAvcodecForDriver")]
    void ksceAvcdecQueryDecoderMemSizeNongameapp(HleContext context) => Forward(context, 0xFA072F66);

    /// <summary>SceAvcodecForDriver 0x90F394E6: sceAvcdecRegisterCallbackInternal.</summary>
    [Hle(Nid = 0x90F394E6, Library = "SceAvcodecForDriver")]
    void ksceAvcdecRegisterCallbackInternal(HleContext context) => Forward(context, 0x8FA94451);

    /// <summary>SceAvcodecForDriver 0xE7E14DE4: sceAvcdecRegisterCallbackNongameapp.</summary>
    [Hle(Nid = 0xE7E14DE4, Library = "SceAvcodecForDriver")]
    void ksceAvcdecRegisterCallbackNongameapp(HleContext context) => Forward(context, 0x497F7EC7);

    /// <summary>SceAvcodecForDriver 0x73401748: sceAvcdecSetDecodeMode.</summary>
    [Hle(Nid = 0x73401748, Library = "SceAvcodecForDriver")]
    void ksceAvcdecSetDecodeMode(HleContext context) => Forward(context, 0x75803C83);

    /// <summary>SceAvcodecForDriver 0x866B49BF: sceAvcdecSetDecodeModeInternal.</summary>
    [Hle(Nid = 0x866B49BF, Library = "SceAvcodecForDriver")]
    void ksceAvcdecSetDecodeModeInternal(HleContext context) => Forward(context, 0xF57CC2BC);

    /// <summary>SceAvcodecForDriver 0xC8366BB8: sceAvcdecSetInterlacedStreamMode.</summary>
    [Hle(Nid = 0xC8366BB8, Library = "SceAvcodecForDriver")]
    void ksceAvcdecSetInterlacedStreamMode(HleContext context) => Forward(context, 0x2DD70FC8);

    /// <summary>SceAvcodecForDriver 0x3CB7661A: sceAvcdecSetLowDelayModeNongameapp.</summary>
    [Hle(Nid = 0x3CB7661A, Library = "SceAvcodecForDriver")]
    void ksceAvcdecSetLowDelayModeNongameapp(HleContext context) => Forward(context, 0xD57F82C1);

    /// <summary>SceAvcodecForDriver 0x20C83C00: sceAvcdecSetRecoveryPointSEIMode.</summary>
    [Hle(Nid = 0x20C83C00, Library = "SceAvcodecForDriver")]
    void ksceAvcdecSetRecoveryPointSEIMode(HleContext context) => Forward(context, 0x58C620EC);

    /// <summary>SceAvcodecForDriver 0x27211864: sceAvcdecUnregisterCallbackInternal.</summary>
    [Hle(Nid = 0x27211864, Library = "SceAvcodecForDriver")]
    void ksceAvcdecUnregisterCallbackInternal(HleContext context) => Forward(context, 0x24E905BF);

    /// <summary>SceAvcodecForDriver 0x13C530E4: sceAvcdecUnregisterCallbackNongameapp.</summary>
    [Hle(Nid = 0x13C530E4, Library = "SceAvcodecForDriver")]
    void ksceAvcdecUnregisterCallbackNongameapp(HleContext context) => Forward(context, 0xE07A91B5);

    /// <summary>SceAvcodecForDriver 0xD9FCDF3E: sceAvcdecUnregisterCallbackWithCbidInternal.</summary>
    [Hle(Nid = 0xD9FCDF3E, Library = "SceAvcodecForDriver")]
    void ksceAvcdecUnregisterCallbackWithCbidInternal(HleContext context) => Forward(context, 0x001069FB);

    /// <summary>SceAvcodecForDriver 0xE4F668C5: sceAvcdecUnregisterCallbackWithCbidNongameapp.</summary>
    [Hle(Nid = 0xE4F668C5, Library = "SceAvcodecForDriver")]
    void ksceAvcdecUnregisterCallbackWithCbidNongameapp(HleContext context) => Forward(context, 0x7761522A);

    /// <summary>SceAvcodecForDriver 0x361238E2: sceJpegCreateSplitDecoder.</summary>
    [Hle(Nid = 0x361238E2, Library = "SceAvcodecForDriver")]
    void ksceJpegCreateSplitDecoder(HleContext context) => Forward(context, 0x123B4734);

    /// <summary>SceAvcodecForDriver 0x80F63723: sceJpegDecodeMJpeg.</summary>
    [Hle(Nid = 0x80F63723, Library = "SceAvcodecForDriver")]
    void ksceJpegDecodeMJpeg(HleContext context) => Forward(context, 0x6215B095);

    /// <summary>SceAvcodecForDriver 0x2A0AF392: sceJpegDecodeMJpegYCbCr.</summary>
    [Hle(Nid = 0x2A0AF392, Library = "SceAvcodecForDriver")]
    void ksceJpegDecodeMJpegYCbCr(HleContext context) => Forward(context, 0x2A769BD8);

    /// <summary>SceAvcodecForDriver 0x864FC454: sceJpegDeleteSplitDecoder.</summary>
    [Hle(Nid = 0x864FC454, Library = "SceAvcodecForDriver")]
    void ksceJpegDeleteSplitDecoder(HleContext context) => Forward(context, 0xDE8D5FA1);

    /// <summary>SceAvcodecForDriver 0x04080710: sceJpegFinishMJpeg.</summary>
    [Hle(Nid = 0x04080710, Library = "SceAvcodecForDriver")]
    void ksceJpegFinishMJpeg(HleContext context) => Forward(context, 0x62842598);

    /// <summary>SceAvcodecForDriver 0xF305E3B3: sceJpegGetOutputInfo.</summary>
    [Hle(Nid = 0xF305E3B3, Library = "SceAvcodecForDriver")]
    void ksceJpegGetOutputInfo(HleContext context) => Forward(context, 0x353BA9B0);

    /// <summary>SceAvcodecForDriver 0x48247A20: sceJpegInitMJpeg.</summary>
    [Hle(Nid = 0x48247A20, Library = "SceAvcodecForDriver")]
    void ksceJpegInitMJpeg(HleContext context) => Forward(context, 0xB030773B);

    /// <summary>SceAvcodecForDriver 0x6F1CF77F: sceJpegInitMJpegWithParam.</summary>
    [Hle(Nid = 0x6F1CF77F, Library = "SceAvcodecForDriver")]
    void ksceJpegInitMJpegWithParam(HleContext context) => Forward(context, 0x518163A7);

    /// <summary>SceAvcodecForDriver 0xEAAE7737: sceJpegMJpegCsc.</summary>
    [Hle(Nid = 0xEAAE7737, Library = "SceAvcodecForDriver")]
    void ksceJpegMJpegCsc(HleContext context) => Forward(context, 0xC2380E3A);

    /// <summary>SceAvcodecForDriver 0xB6882013: sceJpegSplitDecodeMJpeg.</summary>
    [Hle(Nid = 0xB6882013, Library = "SceAvcodecForDriver")]
    void ksceJpegSplitDecodeMJpeg(HleContext context) => Forward(context, 0x4598EC9C);

    /// <summary>SceAvcodecForDriver 0x47D8E150: sceM4vdecCreateDecoder.</summary>
    [Hle(Nid = 0x47D8E150, Library = "SceAvcodecForDriver")]
    void ksceM4vdecCreateDecoder(HleContext context) => Forward(context, 0x17C6AC9E);

    /// <summary>SceAvcodecForDriver 0xC363B31A: sceM4vdecCreateDecoderInternal.</summary>
    [Hle(Nid = 0xC363B31A, Library = "SceAvcodecForDriver")]
    void ksceM4vdecCreateDecoderInternal(HleContext context) => Forward(context, 0x4D4CA34E);

    /// <summary>SceAvcodecForDriver 0xA6CAE217: sceM4vdecCsc.</summary>
    [Hle(Nid = 0xA6CAE217, Library = "SceAvcodecForDriver")]
    void ksceM4vdecCsc(HleContext context) => Forward(context, 0xB4BC325B);

    /// <summary>SceAvcodecForDriver 0x2C7A9841: sceM4vdecDecode.</summary>
    [Hle(Nid = 0x2C7A9841, Library = "SceAvcodecForDriver")]
    void ksceM4vdecDecode(HleContext context) => Forward(context, 0x624664DB);

    /// <summary>SceAvcodecForDriver 0x91E94B76: sceM4vdecDecodeAvailableSize.</summary>
    [Hle(Nid = 0x91E94B76, Library = "SceAvcodecForDriver")]
    void ksceM4vdecDecodeAvailableSize(HleContext context) => Forward(context, 0xA8CF1942);

    /// <summary>SceAvcodecForDriver 0xFD805BF1: sceM4vdecDecodeFlush.</summary>
    [Hle(Nid = 0xFD805BF1, Library = "SceAvcodecForDriver")]
    void ksceM4vdecDecodeFlush(HleContext context) => Forward(context, 0x7C460D75);

    /// <summary>SceAvcodecForDriver 0xF67F93DC: sceM4vdecDecodeStop.</summary>
    [Hle(Nid = 0xF67F93DC, Library = "SceAvcodecForDriver")]
    void ksceM4vdecDecodeStop(HleContext context) => Forward(context, 0x87CFD23B);

    /// <summary>SceAvcodecForDriver 0x3F146942: sceM4vdecDecodeStopWithWorkPicture.</summary>
    [Hle(Nid = 0x3F146942, Library = "SceAvcodecForDriver")]
    void ksceM4vdecDecodeStopWithWorkPicture(HleContext context) => Forward(context, 0xC1CF351F);

    /// <summary>SceAvcodecForDriver 0x823E187E: sceM4vdecDecodeWithWorkPicture.</summary>
    [Hle(Nid = 0x823E187E, Library = "SceAvcodecForDriver")]
    void ksceM4vdecDecodeWithWorkPicture(HleContext context) => Forward(context, 0x8F36574D);

    /// <summary>SceAvcodecForDriver 0x77529FD2: sceM4vdecDeleteDecoder.</summary>
    [Hle(Nid = 0x77529FD2, Library = "SceAvcodecForDriver")]
    void ksceM4vdecDeleteDecoder(HleContext context) => Forward(context, 0x0EB2E4E7);

    /// <summary>SceAvcodecForDriver 0xC5C10C8B: sceM4vdecQueryDecoderMemSize.</summary>
    [Hle(Nid = 0xC5C10C8B, Library = "SceAvcodecForDriver")]
    void ksceM4vdecQueryDecoderMemSize(HleContext context) => Forward(context, 0xC67C1A80);

    /// <summary>SceAvcodecForDriver 0xC99242F5: sceM4vdecQueryDecoderMemSizeInternal.</summary>
    [Hle(Nid = 0xC99242F5, Library = "SceAvcodecForDriver")]
    void ksceM4vdecQueryDecoderMemSizeInternal(HleContext context) => Forward(context, 0x84DC8101);

    /// <summary>SceAvcodecForDriver 0x95E8AFCA: sceVideodecInitLibrary.</summary>
    [Hle(Nid = 0x95E8AFCA, Library = "SceAvcodecForDriver")]
    void ksceVideodecInitLibrary(HleContext context) => Forward(context, 0xF1AF65A3);

    /// <summary>SceAvcodecForDriver 0x4AE6B437: sceVideodecInitLibraryInternal.</summary>
    [Hle(Nid = 0x4AE6B437, Library = "SceAvcodecForDriver")]
    void ksceVideodecInitLibraryInternal(HleContext context) => Forward(context, 0x0FD286E0);

    /// <summary>SceAvcodecForDriver 0x4DD3826D: sceVideodecInitLibraryNongameapp.</summary>
    [Hle(Nid = 0x4DD3826D, Library = "SceAvcodecForDriver")]
    void ksceVideodecInitLibraryNongameapp(HleContext context) => Forward(context, 0x26F3C35D);

    /// <summary>SceAvcodecForDriver 0xC93DA0C7: sceVideodecInitLibraryWithUnmapMem.</summary>
    [Hle(Nid = 0xC93DA0C7, Library = "SceAvcodecForDriver")]
    void ksceVideodecInitLibraryWithUnmapMem(HleContext context) => Forward(context, 0x230A78FF);

    /// <summary>SceAvcodecForDriver 0xBC55D10F: sceVideodecInitLibraryWithUnmapMemInternal.</summary>
    [Hle(Nid = 0xBC55D10F, Library = "SceAvcodecForDriver")]
    void ksceVideodecInitLibraryWithUnmapMemInternal(HleContext context) => Forward(context, 0x36ADE1E5);

    /// <summary>SceAvcodecForDriver 0x6DC39EDC: sceVideodecInitLibraryWithUnmapMemNongameapp.</summary>
    [Hle(Nid = 0x6DC39EDC, Library = "SceAvcodecForDriver")]
    void ksceVideodecInitLibraryWithUnmapMemNongameapp(HleContext context) => Forward(context, 0x108C1FEC);

    /// <summary>SceAvcodecForDriver 0xF9E271DE: sceVideodecQueryInstanceNongameapp.</summary>
    [Hle(Nid = 0xF9E271DE, Library = "SceAvcodecForDriver")]
    void ksceVideodecQueryInstanceNongameapp(HleContext context) => Forward(context, 0xEF03E31B);

    /// <summary>SceAvcodecForDriver 0x536C8296: sceVideodecQueryMemSize.</summary>
    [Hle(Nid = 0x536C8296, Library = "SceAvcodecForDriver")]
    void ksceVideodecQueryMemSize(HleContext context) => Forward(context, 0x6731BB2D);

    /// <summary>SceAvcodecForDriver 0x22D7B653: sceVideodecQueryMemSizeInternal.</summary>
    [Hle(Nid = 0x22D7B653, Library = "SceAvcodecForDriver")]
    void ksceVideodecQueryMemSizeInternal(HleContext context) => Forward(context, 0xEB48554F);

    /// <summary>SceAvcodecForDriver 0xFACCD284: sceVideodecQueryMemSizeNongameapp.</summary>
    [Hle(Nid = 0xFACCD284, Library = "SceAvcodecForDriver")]
    void ksceVideodecQueryMemSizeNongameapp(HleContext context) => Forward(context, 0x2D9AE981);

    /// <summary>SceAvcodecForDriver 0x12DAE04A: sceVideodecSetConfig.</summary>
    [Hle(Nid = 0x12DAE04A, Library = "SceAvcodecForDriver")]
    void ksceVideodecSetConfig(HleContext context) => Forward(context, 0x9B871779);

    /// <summary>SceAvcodecForDriver 0x43985AA8: sceVideodecSetConfigInternal.</summary>
    [Hle(Nid = 0x43985AA8, Library = "SceAvcodecForDriver")]
    void ksceVideodecSetConfigInternal(HleContext context) => Forward(context, 0x4A9CB138);

    /// <summary>SceAvcodecForDriver 0x2641888C: sceVideodecTermLibrary.</summary>
    [Hle(Nid = 0x2641888C, Library = "SceAvcodecForDriver")]
    void ksceVideodecTermLibrary(HleContext context) => Forward(context, 0x3A5F4924);

    /// <summary>SceAvcodecForDriver 0xE782E735: sceVideoencInitLibrary.</summary>
    [Hle(Nid = 0xE782E735, Library = "SceAvcodecForDriver")]
    void ksceVideoencInitLibrary(HleContext context) => Forward(context, 0xACA59885);

    /// <summary>SceAvcodecForDriver 0x04F734CC: sceVideoencInitLibraryInternal.</summary>
    [Hle(Nid = 0x04F734CC, Library = "SceAvcodecForDriver")]
    void ksceVideoencInitLibraryInternal(HleContext context) => Forward(context, 0xAF0F17AD);

    /// <summary>SceAvcodecForDriver 0xCD7FE8D0: sceVideoencInitLibraryWithUnmapMem.</summary>
    [Hle(Nid = 0xCD7FE8D0, Library = "SceAvcodecForDriver")]
    void ksceVideoencInitLibraryWithUnmapMem(HleContext context) => Forward(context, 0x8F970948);

    /// <summary>SceAvcodecForDriver 0xBCB769EB: sceVideoencInitLibraryWithUnmapMemInternal.</summary>
    [Hle(Nid = 0xBCB769EB, Library = "SceAvcodecForDriver")]
    void ksceVideoencInitLibraryWithUnmapMemInternal(HleContext context) => Forward(context, 0x54D750B8);

    /// <summary>SceAvcodecForDriver 0x32DEE3C5: sceVideoencQueryMemSize.</summary>
    [Hle(Nid = 0x32DEE3C5, Library = "SceAvcodecForDriver")]
    void ksceVideoencQueryMemSize(HleContext context) => Forward(context, 0x7772157D);

    /// <summary>SceAvcodecForDriver 0x86B1ACC5: sceVideoencQueryMemSizeInternal.</summary>
    [Hle(Nid = 0x86B1ACC5, Library = "SceAvcodecForDriver")]
    void ksceVideoencQueryMemSizeInternal(HleContext context) => Forward(context, 0x3E459BA8);

    /// <summary>SceAvcodecForDriver 0x58F1E353: sceVideoencTermLibrary.</summary>
    [Hle(Nid = 0x58F1E353, Library = "SceAvcodecForDriver")]
    void ksceVideoencTermLibrary(HleContext context) => Forward(context, 0x7FE7EDA3);
}
