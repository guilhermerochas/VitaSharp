namespace VitaSharp.Core.Hle;

/// <summary>The console model the emulated unit is (the emulator option "Modelo"; --model=, VITASHARP_MODEL).</summary>
public enum ConsoleModel
{
    /// <summary>A PS Vita handheld, PCH-1000 (the default).</summary>
    Handheld,

    /// <summary>A PlayStation TV (VTE-1000, "dolce"): no screen, touch panels or motion sensors of its own; up to four
    /// controllers on ports 1-4.</summary>
    PsTv,
}

/// <summary>
/// The identity of the emulated unit as the boot loader hands it to the kernel (the KBL parameters, read by
/// SceSblAIMgr in SceSysmem 3.74): the product code block (pscode, +0xA0: company, product, sub code, factory code;
/// stored big-endian there) and the hardware info word (+0xD4). The emulated unit is a retail PS Vita handheld
/// (PCH-1000, CEX, North America), the one every SceSblAimgr query, the bridge and the registry manager report.
/// </summary>
public static class ConsoleIdentity
{
    /// <summary>The company code: 1, Sony Computer Entertainment.</summary>
    public const ushort CompanyCode = 1;

    /// <summary>The product code: 0x100 test, 0x101 tool, 0x102 DEX, 0x103..0x111 CEX by region (0x104 North
    /// America, the region the registry manager reports).</summary>
    public const ushort ProductCode = 0x104;

    /// <summary>The product sub code: 0x10, a PCH-1000 handheld (bit 0x200 marks a PS TV).</summary>
    public const ushort ProductSubCode = 0x10;

    /// <summary>The product sub code of the PS TV model: bit 0x200, the one FW 3.74's SceSblAIMgr tests
    /// (ksceSblAimgrIsGenuineDolce / IsDolce). The exact sub code of a retail VTE-1000 is unconfirmed; only the bit is
    /// read by the firmware modules studied.</summary>
    public const ushort PsTvProductSubCode = 0x201;

    /// <summary>The product sub code of <paramref name="model"/>.</summary>
    public static ushort ProductSubCodeOf(ConsoleModel model) => model == ConsoleModel.PsTv ? PsTvProductSubCode : ProductSubCode;

    /// <summary>The model a --model= / VITASHARP_MODEL value names: "pstv" (also "vte-1000", "dolce", "tv") is the PS
    /// TV; anything else, or nothing, the handheld.</summary>
    public static ConsoleModel ParseModel(string? value)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        return text is "pstv" or "ps-tv" or "vte-1000" or "vte1000" or "dolce" or "tv" ? ConsoleModel.PsTv : ConsoleModel.Handheld;
    }

    /// <summary>The factory code: 0.</summary>
    public const ushort FactoryCode = 0;

    /// <summary>The hardware info word: bits 16..23 hold the board kind that makes a PS TV (0x51, 0x70, 0x72);
    /// the emulated handheld's word is 0 (the one ksceSysconGetHardwareInfo reports through the bridge).</summary>
    public const uint HardwareInfo = 0;

    /// <summary>
    /// The SoC revision word (ScePervasive misc register 0xE3100000, "revision0", what kscePervasiveGetSoCRevision
    /// answers): 0x80000042, the CXD5315GG of a retail PCH-1000 (IRS-002 board, 2 x 256 MiB LPDDR2), as identification
    /// tools list the values read on real units (vita-PSVident getSoC: 0x80000042 fat CEX, 0x00000042 DevKit,
    /// 0x80000115 the newer CXD5316GG). Bits 0..7 are the revision (4.2), bits 8..16 zero on the first SoC; bit 31 set
    /// with those bits zero puts the second RAM bank at 0x58000000 (no extra 512 MiB), as the coredump module reads it.
    /// The FW 3.74 readers (SceGpuEs4, ScePower, msif, sdif, crashdump) test exactly those fields.
    /// </summary>
    public const uint SocRevision = 0x80000042;

    /// <summary>The secure module information word (KBL parameters +8): 0.</summary>
    public const uint SecureModuleInfo = 0;

    /// <summary>The unit's console id (16 bytes): +3 company 1, +4 product 0x104 (big-endian), +7 sub code 0x10,
    /// +8 the factory/chassis byte 4, +9..+15 the serial part (the last bytes of the OpenPSID), stable between runs.
    /// </summary>
    public static ReadOnlySpan<byte> ConsoleId =>
        [0, 0, 0, 1, 0x01, 0x04, 0, 0x10, 0x04, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F];

    /// <summary>The unit's OpenPSID (sceKernelGetOpenPsId; the id a unit-bound license names): 0x10..0x1F, the
    /// same on every run.</summary>
    public static ReadOnlySpan<byte> OpenPsId =>
        [0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F];
}
