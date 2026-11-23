using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The SceSysconForDriver exports that are one chip command each (FW 3.74 syscon.skprx, read in the module, study
// only), in the module's frame layouts. The chip keeps what a set command sent and answers a read (VitaSystem.Syscon).
public sealed partial class SysconKernel
{
    // ------------------------------------------------------------------ one value sent, one word read

    /// <summary>NID_00AE3AEB(value): command 0xb5 with 1 byte of the value.</summary>
    [Hle(Nid = 0x00AE3AEB, Library = "SceSysconForDriver")]
    int NID_00AE3AEB(uint value) => Send(0xb5, value, 2);

    /// <summary>NID_010F95D9(): command 0x3a9 without data.</summary>
    [Hle(Nid = 0x010F95D9, Library = "SceSysconForDriver")]
    int NID_010F95D9() => Send(0x3a9, 0, 1);

    /// <summary>NID_058941D7(value): command 0xb0 with 1 byte of the value.</summary>
    [Hle(Nid = 0x058941D7, Library = "SceSysconForDriver")]
    int NID_058941D7(uint value) => Send(0xb0, value, 2);

    /// <summary>NID_060E55C1(): command 0x392 with {1}.</summary>
    [Hle(Nid = 0x060E55C1, Library = "SceSysconForDriver")]
    int NID_060E55C1() => Send(0x392, 1, 2);

    /// <summary>NID_063425AE(value): command 0x887 with 1 byte of the value.</summary>
    [Hle(Nid = 0x063425AE, Library = "SceSysconForDriver")]
    int NID_063425AE(uint value) => Send(0x887, value, 2);

    /// <summary>NID_0D300158(value): command 0xb6 with 1 byte of the value.</summary>
    [Hle(Nid = 0x0D300158, Library = "SceSysconForDriver")]
    int NID_0D300158(uint value) => Send(0xb6, value, 2);

    /// <summary>NID_10327C64(): command 0x3c2 with {1}.</summary>
    [Hle(Nid = 0x10327C64, Library = "SceSysconForDriver")]
    int NID_10327C64() => Send(0x3c2, 1, 2);

    /// <summary>NID_1C29C00E(uint *value): command 0x842, its reply word.</summary>
    [Hle(Nid = 0x1C29C00E, Library = "SceSysconForDriver")]
    int NID_1C29C00E(uint value) => ReadWord(value, 0x842);

    /// <summary>NID_240A604E(value): command 0x3bf with 1 byte of the value.</summary>
    [Hle(Nid = 0x240A604E, Library = "SceSysconForDriver")]
    int NID_240A604E(uint value) => Send(0x3bf, value, 2);

    /// <summary>NID_2659535C(): command 0x84 without data.</summary>
    [Hle(Nid = 0x2659535C, Library = "SceSysconForDriver")]
    int NID_2659535C() => Send(0x84, 0, 1);

    /// <summary>NID_285594F8(value): command 0x890 with 1 byte of the value.</summary>
    [Hle(Nid = 0x285594F8, Library = "SceSysconForDriver")]
    int NID_285594F8(uint value) => Send(0x890, value, 2);

    /// <summary>NID_2A61ACE1(uint *value): command 0x3b4, its first reply byte (when the pointer is not NULL).</summary>
    [Hle(Nid = 0x2A61ACE1, Library = "SceSysconForDriver")]
    int NID_2A61ACE1(uint value) => ReadByte(value, 0x3b4);

    /// <summary>NID_2E6D97CD(uint *value): command 0x3bc, its reply word.</summary>
    [Hle(Nid = 0x2E6D97CD, Library = "SceSysconForDriver")]
    int NID_2E6D97CD(uint value) => ReadWord(value, 0x3bc);

    /// <summary>NID_2EC6D55D(value): command 0x1081 with 1 byte of the value.</summary>
    [Hle(Nid = 0x2EC6D55D, Library = "SceSysconForDriver")]
    int NID_2EC6D55D(uint value) => Send(0x1081, value, 2);

    /// <summary>ksceSysconCtrlDolceUsbPower(value): command 0x8c5 with 1 byte of the value.</summary>
    [Hle(Nid = 0x3274A925, Library = "SceSysconForDriver")]
    int ksceSysconCtrlDolceUsbPower(uint value) => Send(0x8c5, value, 2);

    /// <summary>NID_33B5CDB3(uint *value): command 0xb3, its reply word.</summary>
    [Hle(Nid = 0x33B5CDB3, Library = "SceSysconForDriver")]
    int NID_33B5CDB3(uint value) => ReadWord(value, 0xb3);

    /// <summary>NID_351946B0(uint *value): command 0x1310, its reply word.</summary>
    [Hle(Nid = 0x351946B0, Library = "SceSysconForDriver")]
    int NID_351946B0(uint value) => ReadWord(value, 0x1310);

    /// <summary>NID_3664E2C0(uint *value): command 0x3ac, its reply word.</summary>
    [Hle(Nid = 0x3664E2C0, Library = "SceSysconForDriver")]
    int NID_3664E2C0(uint value) => ReadWord(value, 0x3ac);

    /// <summary>NID_373ECF8A(value): command 0x83 with 2 bytes of the value.</summary>
    [Hle(Nid = 0x373ECF8A, Library = "SceSysconForDriver")]
    int NID_373ECF8A(uint value) => Send(0x83, value, 3);

    /// <summary>ksceSysconClearTemperatureLog(value): command 0x8cf with 1 byte of the value.</summary>
    [Hle(Nid = 0x3843D657, Library = "SceSysconForDriver")]
    int ksceSysconClearTemperatureLog(uint value) => Send(0x8cf, value, 2);

    /// <summary>NID_3C3B949C(uint *value): command 0x841, its reply word.</summary>
    [Hle(Nid = 0x3C3B949C, Library = "SceSysconForDriver")]
    int NID_3C3B949C(uint value) => ReadWord(value, 0x841);

    /// <summary>ksceSysconGetManufacturesStatus(uint *value): command 0x15, its reply word.</summary>
    [Hle(Nid = 0x3E09A1F4, Library = "SceSysconForDriver")]
    int ksceSysconGetManufacturesStatus(uint value) => ReadWord(value, 0x15);

    /// <summary>NID_41942B50(uint *value): command 0x3a4, its first reply byte (when the pointer is not NULL).</summary>
    [Hle(Nid = 0x41942B50, Library = "SceSysconForDriver")]
    int NID_41942B50(uint value) => ReadByte(value, 0x3a4);

    /// <summary>NID_4295D497(): command 0x85 without data.</summary>
    [Hle(Nid = 0x4295D497, Library = "SceSysconForDriver")]
    int NID_4295D497() => Send(0x85, 0, 1);

    /// <summary>NID_42E599AC(): command 0x3b9 without data.</summary>
    [Hle(Nid = 0x42E599AC, Library = "SceSysconForDriver")]
    int NID_42E599AC() => Send(0x3b9, 0, 1);

    /// <summary>NID_451C1662(value): command 0x8c9 with 1 byte of the value.</summary>
    [Hle(Nid = 0x451C1662, Library = "SceSysconForDriver")]
    int NID_451C1662(uint value) => Send(0x8c9, value, 2);

    /// <summary>NID_48ED8981(): command 0x392 with {0}.</summary>
    [Hle(Nid = 0x48ED8981, Library = "SceSysconForDriver")]
    int NID_48ED8981() => Send(0x392, 0, 2);

    /// <summary>ksceSysconEnableHibernateIO(): command 0x8b0 without data.</summary>
    [Hle(Nid = 0x4946538A, Library = "SceSysconForDriver")]
    int ksceSysconEnableHibernateIO() => Send(0x8b0, 0, 1);

    /// <summary>ksceSysconLogStart(): command 0x87 without data.</summary>
    [Hle(Nid = 0x4E55CF5E, Library = "SceSysconForDriver")]
    int ksceSysconLogStart() => Send(0x87, 0, 1);

    /// <summary>ksceSysconGetManualChargeMode(uint *value): command 0x840, its reply word.</summary>
    [Hle(Nid = 0x4FEC564C, Library = "SceSysconForDriver")]
    int ksceSysconGetManualChargeMode(uint value) => ReadWord(value, 0x840);

    /// <summary>NID_567865A3(uint *value): command 0x3a1, its first reply byte (when the pointer is not NULL).</summary>
    [Hle(Nid = 0x567865A3, Library = "SceSysconForDriver")]
    int NID_567865A3(uint value) => ReadByte(value, 0x3a1);

    /// <summary>NID_5946B29B(): command 0x3c2 with {0}.</summary>
    [Hle(Nid = 0x5946B29B, Library = "SceSysconForDriver")]
    int NID_5946B29B() => Send(0x3c2, 0, 2);

    /// <summary>NID_596B17B7(value): command 0x892 with 1 byte of the value.</summary>
    [Hle(Nid = 0x596B17B7, Library = "SceSysconForDriver")]
    int NID_596B17B7(uint value) => Send(0x892, value, 2);

    /// <summary>NID_59DC5938(value): command 0x89a with 2 bytes of the value.</summary>
    [Hle(Nid = 0x59DC5938, Library = "SceSysconForDriver")]
    int NID_59DC5938(uint value) => Send(0x89a, value, 3);

    /// <summary>NID_5A614349(value): command 0x88b with 1 byte of the value.</summary>
    [Hle(Nid = 0x5A614349, Library = "SceSysconForDriver")]
    int NID_5A614349(uint value) => Send(0x88b, value, 2);

    /// <summary>ksceSysconCtrlHdmiCecPower(value): command 0x886 with 1 byte of the value.</summary>
    [Hle(Nid = 0x62155962, Library = "SceSysconForDriver")]
    int ksceSysconCtrlHdmiCecPower(uint value) => Send(0x886, value, 2);

    /// <summary>NID_730E4725(uint *value): command 0x843, its reply word.</summary>
    [Hle(Nid = 0x730E4725, Library = "SceSysconForDriver")]
    int NID_730E4725(uint value) => ReadWord(value, 0x843);

    /// <summary>ksceSysconCommitConfigstorageTransaction(): command 0x1384 without data.</summary>
    [Hle(Nid = 0x7B9B3617, Library = "SceSysconForDriver")]
    int ksceSysconCommitConfigstorageTransaction() => Send(0x1384, 0, 1);

    /// <summary>NID_7BAFE083(value): command 0xa82 with 1 byte of the value.</summary>
    [Hle(Nid = 0x7BAFE083, Library = "SceSysconForDriver")]
    int NID_7BAFE083(uint value) => Send(0xa82, value, 2);

    /// <summary>NID_7BFA95DA(value): command 0x8c8 with 1 byte of the value.</summary>
    [Hle(Nid = 0x7BFA95DA, Library = "SceSysconForDriver")]
    int NID_7BFA95DA(uint value) => Send(0x8c8, value, 2);

    /// <summary>NID_87FF8041(value): command 0x8c7 with 1 byte of the value.</summary>
    [Hle(Nid = 0x87FF8041, Library = "SceSysconForDriver")]
    int NID_87FF8041(uint value) => Send(0x8c7, value, 2);

    /// <summary>NID_8874EF45(): command 0x393 with {1}.</summary>
    [Hle(Nid = 0x8874EF45, Library = "SceSysconForDriver")]
    int NID_8874EF45() => Send(0x393, 1, 2);

    /// <summary>ksceSysconSetMultiCnPort(value): command 0x190 with 2 bytes of the value.</summary>
    [Hle(Nid = 0x8AAB6308, Library = "SceSysconForDriver")]
    int ksceSysconSetMultiCnPort(uint value) => Send(0x190, value, 3);

    /// <summary>NID_8B0B4A97(uint *value): command 0x3b0, its first reply byte (when the pointer is not NULL).</summary>
    [Hle(Nid = 0x8B0B4A97, Library = "SceSysconForDriver")]
    int NID_8B0B4A97(uint value) => ReadByte(value, 0x3b0);

    /// <summary>ksceSysconCtrlAccPower(value): command 0x889 with 1 byte of the value.</summary>
    [Hle(Nid = 0x8D1D97E8, Library = "SceSysconForDriver")]
    int ksceSysconCtrlAccPower(uint value) => Send(0x889, value, 2);

    /// <summary>NID_91EF4EC3(uint *value): command 0xb7, its reply word.</summary>
    [Hle(Nid = 0x91EF4EC3, Library = "SceSysconForDriver")]
    int NID_91EF4EC3(uint value) => ReadWord(value, 0xb7);

    /// <summary>NID_93075DD1(uint *value): command 0x14, its reply word.</summary>
    [Hle(Nid = 0x93075DD1, Library = "SceSysconForDriver")]
    int NID_93075DD1(uint value) => ReadWord(value, 0x14);

    /// <summary>NID_94AB13CC(value): command 0xc1 with 1 byte of the value.</summary>
    [Hle(Nid = 0x94AB13CC, Library = "SceSysconForDriver")]
    int NID_94AB13CC(uint value) => Send(0xc1, value, 2);

    /// <summary>NID_998ECD6D(uint *value): command 0x3a5, its first reply byte (when the pointer is not NULL).</summary>
    [Hle(Nid = 0x998ECD6D, Library = "SceSysconForDriver")]
    int NID_998ECD6D(uint value) => ReadByte(value, 0x3a5);

    /// <summary>NID_9A28BEEF(): command 0x393 with {0}.</summary>
    [Hle(Nid = 0x9A28BEEF, Library = "SceSysconForDriver")]
    int NID_9A28BEEF() => Send(0x393, 0, 2);

    /// <summary>NID_9B6A6F64(value): command 0x81 with 4 bytes of the value.</summary>
    [Hle(Nid = 0x9B6A6F64, Library = "SceSysconForDriver")]
    int NID_9B6A6F64(uint value) => Send(0x81, value, 5);

    /// <summary>NID_9B6B8BB9(uint *value): command 0x3a7, its first reply byte (when the pointer is not NULL).</summary>
    [Hle(Nid = 0x9B6B8BB9, Library = "SceSysconForDriver")]
    int NID_9B6B8BB9(uint value) => ReadByte(value, 0x3a7);

    /// <summary>NID_9BF78047(uint *value): command 0x881, its reply word.</summary>
    [Hle(Nid = 0x9BF78047, Library = "SceSysconForDriver")]
    int NID_9BF78047(uint value) => ReadWord(value, 0x881);

    /// <summary>ksceSysconLogStartWaiting(): command 0x89 without data.</summary>
    [Hle(Nid = 0x9C0B1E61, Library = "SceSysconForDriver")]
    int ksceSysconLogStartWaiting() => Send(0x89, 0, 1);

    /// <summary>NID_A039B563(uint *value): command 0x880, its reply word.</summary>
    [Hle(Nid = 0xA039B563, Library = "SceSysconForDriver")]
    int NID_A039B563(uint value) => ReadWord(value, 0x880);

    /// <summary>NID_A2E85DB9(value): command 0x88a with 1 byte of the value.</summary>
    [Hle(Nid = 0xA2E85DB9, Library = "SceSysconForDriver")]
    int NID_A2E85DB9(uint value) => Send(0x88a, value, 2);

    /// <summary>NID_A2FE9BF9(uint *value): command 0x800, its reply word.</summary>
    [Hle(Nid = 0xA2FE9BF9, Library = "SceSysconForDriver")]
    int NID_A2FE9BF9(uint value) => ReadWord(value, 0x800);

    /// <summary>ksceSysconBeginConfigstorageTransaction(): command 0x1382 without data.</summary>
    [Hle(Nid = 0xA4968B8C, Library = "SceSysconForDriver")]
    int ksceSysconBeginConfigstorageTransaction() => Send(0x1382, 0, 1);

    /// <summary>NID_AD0A8275(uint *value): command 0x902, its reply word.</summary>
    [Hle(Nid = 0xAD0A8275, Library = "SceSysconForDriver")]
    int NID_AD0A8275(uint value) => ReadWord(value, 0x902);

    /// <summary>NID_AE127A96(uint *value): command 0x3b1, its first reply byte (when the pointer is not NULL).</summary>
    [Hle(Nid = 0xAE127A96, Library = "SceSysconForDriver")]
    int NID_AE127A96(uint value) => ReadByte(value, 0x3b1);

    /// <summary>ksceSysconCtrlDevKitUsbPower(value): command 0x89c with 1 byte of the value.</summary>
    [Hle(Nid = 0xB1F88B11, Library = "SceSysconForDriver")]
    int ksceSysconCtrlDevKitUsbPower(uint value) => Send(0x89c, value, 2);

    /// <summary>NID_B872E904(value): command 0x88c with 1 byte of the value.</summary>
    [Hle(Nid = 0xB872E904, Library = "SceSysconForDriver")]
    int NID_B872E904(uint value) => Send(0x88c, value, 2);

    /// <summary>NID_B8F4F4E3(value): command 0x3af with 1 byte of the value.</summary>
    [Hle(Nid = 0xB8F4F4E3, Library = "SceSysconForDriver")]
    int NID_B8F4F4E3(uint value) => Send(0x3af, value, 2);

    /// <summary>NID_BC1D93D7(uint *value): command 0x3a0, its first reply byte (when the pointer is not NULL).</summary>
    [Hle(Nid = 0xBC1D93D7, Library = "SceSysconForDriver")]
    int NID_BC1D93D7(uint value) => ReadByte(value, 0x3a0);

    /// <summary>NID_C4A61241(uint *value): command 0x3b7, its first reply byte (when the pointer is not NULL).</summary>
    [Hle(Nid = 0xC4A61241, Library = "SceSysconForDriver")]
    int NID_C4A61241(uint value) => ReadByte(value, 0x3b7);

    /// <summary>ksceSysconCtrlManualChargeMode(value): command 0x8c0 with 1 byte of the value.</summary>
    [Hle(Nid = 0xC6A2C9EF, Library = "SceSysconForDriver")]
    int ksceSysconCtrlManualChargeMode(uint value) => Send(0x8c0, value, 2);

    /// <summary>NID_CD73079D(uint *value): command 0x98c, its reply word.</summary>
    [Hle(Nid = 0xCD73079D, Library = "SceSysconForDriver")]
    int NID_CD73079D(uint value) => ReadWord(value, 0x98c);

    /// <summary>NID_CEE2B8F7(uint *value): command 0x3b5, its first reply byte (when the pointer is not NULL).</summary>
    [Hle(Nid = 0xCEE2B8F7, Library = "SceSysconForDriver")]
    int NID_CEE2B8F7(uint value) => ReadByte(value, 0x3b5);

    /// <summary>NID_CF5B2F2F(uint *value): command 0x10, its reply word.</summary>
    [Hle(Nid = 0xCF5B2F2F, Library = "SceSysconForDriver")]
    int NID_CF5B2F2F(uint value) => ReadWord(value, 0x10);

    /// <summary>NID_CFCEE733(value): command 0x181 with 1 byte of the value.</summary>
    [Hle(Nid = 0xCFCEE733, Library = "SceSysconForDriver")]
    int NID_CFCEE733(uint value) => Send(0x181, value, 2);

    /// <summary>NID_D01E64FC(uint *value): command 0x480, its reply word.</summary>
    [Hle(Nid = 0xD01E64FC, Library = "SceSysconForDriver")]
    int NID_D01E64FC(uint value) => ReadWord(value, 0x480);

    /// <summary>NID_D7BEFF8B(uint *value): command 0x11, its reply word.</summary>
    [Hle(Nid = 0xD7BEFF8B, Library = "SceSysconForDriver")]
    int NID_D7BEFF8B(uint value) => ReadWord(value, 0x11);

    /// <summary>NID_DD16ABD9(value): command 0x88d with 1 byte of the value.</summary>
    [Hle(Nid = 0xDD16ABD9, Library = "SceSysconForDriver")]
    int NID_DD16ABD9(uint value) => Send(0x88d, value, 2);

    /// <summary>ksceSysconCtrlHostOutputViaDongle(value): command 0xb2 with 1 byte of the value.</summary>
    [Hle(Nid = 0xDECCB2B4, Library = "SceSysconForDriver")]
    int ksceSysconCtrlHostOutputViaDongle(uint value) => Send(0xb2, value, 2);

    /// <summary>NID_DFB024C4(uint *value): command 0x8c6, its reply word.</summary>
    [Hle(Nid = 0xDFB024C4, Library = "SceSysconForDriver")]
    int NID_DFB024C4(uint value) => ReadWord(value, 0x8c6);

    /// <summary>NID_E1885F68(uint *value): command 0x98b, its reply word.</summary>
    [Hle(Nid = 0xE1885F68, Library = "SceSysconForDriver")]
    int NID_E1885F68(uint value) => ReadWord(value, 0x98b);

    /// <summary>NID_E7893732(value): command 0x1b0 with 2 bytes of the value.</summary>
    [Hle(Nid = 0xE7893732, Library = "SceSysconForDriver")]
    int NID_E7893732(uint value) => Send(0x1b0, value, 3);

    /// <summary>NID_E7F5D3DC(uint *value): command 0x806, its reply word.</summary>
    [Hle(Nid = 0xE7F5D3DC, Library = "SceSysconForDriver")]
    int NID_E7F5D3DC(uint value) => ReadWord(value, 0x806);

    /// <summary>ksceSysconGetUsbDetStatus(uint *value): command 0x805, its reply word.</summary>
    [Hle(Nid = 0xEF810687, Library = "SceSysconForDriver")]
    int ksceSysconGetUsbDetStatus(uint value) => ReadWord(value, 0x805);

    /// <summary>NID_F6D4DDC4(uint *value): command 0xb4, its reply word.</summary>
    [Hle(Nid = 0xF6D4DDC4, Library = "SceSysconForDriver")]
    int NID_F6D4DDC4(uint value) => ReadWord(value, 0xb4);

    /// <summary>NID_F87679EE(value): command 0x8d0 with 1 byte of the value.</summary>
    [Hle(Nid = 0xF87679EE, Library = "SceSysconForDriver")]
    int NID_F87679EE(uint value) => Send(0x8d0, value, 2);

    /// <summary>NID_F93CF833(uint *value): command 0x98a, its reply word.</summary>
    [Hle(Nid = 0xF93CF833, Library = "SceSysconForDriver")]
    int NID_F93CF833(uint value) => ReadWord(value, 0x98a);

    /// <summary>NID_F99BC858(uint *value): command 0x131, its reply word.</summary>
    [Hle(Nid = 0xF99BC858, Library = "SceSysconForDriver")]
    int NID_F99BC858(uint value) => ReadWord(value, 0x131);

    /// <summary>ksceSysconEndConfigstorageTransaction(): command 0x1383 without data.</summary>
    [Hle(Nid = 0xFCC3E8EE, Library = "SceSysconForDriver")]
    int ksceSysconEndConfigstorageTransaction() => Send(0x1383, 0, 1);

    // ------------------------------------------------------------------ commands with their own frame

    /// <summary>NID_0D0B6D25(): command 0 without data (the module sends it to refresh the chip's status byte).</summary>
    [Hle(Nid = 0x0D0B6D25, Library = "SceSysconForDriver")]
    int NID_0D0B6D25()
    {
        Span<byte> rx = stackalloc byte[0x20];
        return Exec(0, rx);
    }

    /// <summary>
    /// ksceSysconSetPowerMode(type, mode) (FW 3.74 syscon.skprx, decompiled, study only: an smc 0 to the secure
    /// monitor, which hands the power sequence to the chip; the processor does not come back from it): the system goes
    /// the way the type asks. The types, as vita-headers names them (SceSysconResetType) and ScePower's suspend path
    /// uses them: 0 power off; 1 suspend (standby, POWER wakes it); 2 cold reset and 0x11 soft reset (the boot runs
    /// again: a reboot). ScePower also sends 0x10, 3 and 4 on its power-off path (the variants for an AC adapter / a
    /// charging state) and 5 on its update-restart path (NID_4278E614): power off and reboot. Another type is kept on
    /// the chip only. 0.
    /// </summary>
    [Hle(Nid = 0x8A95D35C, Library = "SceSysconForDriver")]
    int ksceSysconSetPowerMode(uint type, uint mode)
    {
        Chip.PowerMode = type;
        switch (type)
        {
            case 0:
            case 3:
            case 4:
            case 0x10:
                Emu.Vita.Power.PowerOff();
                break;
            case 1:
                Emu.Vita.Power.Suspend();
                break;
            case 2:
            case 5:
            case 0x11:
                Emu.Vita.Power.Reboot();
                break;
        }

        return 0;
    }

    /// <summary>NID_4278E614(): ksceSysconSetPowerMode(5, 0), the restart ScePower asks for after an update.</summary>
    [Hle(Nid = 0x4278E614, Library = "SceSysconForDriver")]
    int NID_4278E614() => ksceSysconSetPowerModeThroughEntry(5, 0);

    /// <summary>NID_14B99945(): ksceSysconSetPowerMode(2, 0), the cold reset.</summary>
    [Hle(Nid = 0x14B99945, Library = "SceSysconForDriver")]
    int NID_14B99945() => ksceSysconSetPowerModeThroughEntry(2, 0);

    /// <summary>ksceSysconCtrlLED(led, on): command 0x891 {led | 0x80 when on}.</summary>
    [Hle(Nid = 0x04EC7579, Library = "SceSysconForDriver")]
    int ksceSysconCtrlLED(uint led, int on) => Send(0x891, on != 0 ? led | 0x80 : led, 2);

    /// <summary>ksceSysconCtrlDolceLED(led): command 0x820 + led (0..2) without data; else 0x80250001.</summary>
    [Hle(Nid = 0x727F985A, Library = "SceSysconForDriver")]
    int ksceSysconCtrlDolceLED(uint led) => led < 3 ? Send((ushort)(0x820 + led), 0, 1) : SYSCON_ERR_INVALID;

    /// <summary>ksceSysconCtrlDeviceReset(device, reset): command 0x88F {reset, device}.</summary>
    [Hle(Nid = 0x40FF3898, Library = "SceSysconForDriver")]
    int ksceSysconCtrlDeviceReset(uint device, uint reset) => Send(0x88F, reset | device << 8, 3);

    /// <summary>NID_2A4B0437(a, b): command 0x893 {b, a}.</summary>
    [Hle(Nid = 0x2A4B0437, Library = "SceSysconForDriver")]
    int NID_2A4B0437(uint a, uint b) => Send(0x893, b | a << 8, 3);

    /// <summary>NID_9A7858B6(a, b): command 0x481 {a &amp; 1, b}.</summary>
    [Hle(Nid = 0x9A7858B6, Library = "SceSysconForDriver")]
    int NID_9A7858B6(uint a, uint b) => Send(0x481, (a & 1) | b << 8, 3);

    /// <summary>NID_357CC9D9(a, b): command 0x3AB {a, b}.</summary>
    [Hle(Nid = 0x357CC9D9, Library = "SceSysconForDriver")]
    int NID_357CC9D9(uint a, uint b) => Send(0x3AB, (a & 0xFF) | (b & 0xFFFF) << 8, 3);

    /// <summary>NID_CCA56A16(a, b): command 0x3BB {a, b}.</summary>
    [Hle(Nid = 0xCCA56A16, Library = "SceSysconForDriver")]
    int NID_CCA56A16(uint a, uint b) => Send(0x3BB, (a & 0xFF) | (b & 0xFFFF) << 8, 3);

    /// <summary>NID_6F586D1A(device, value): only device 0x40: command 0x89D {1, value}; else 0x80250001.</summary>
    [Hle(Nid = 0x6F586D1A, Library = "SceSysconForDriver")]
    int NID_6F586D1A(uint device, uint value) => device == 0x40 ? Send(0x89D, value << 8 | 1, 3) : SYSCON_ERR_INVALID;

    /// <summary>NID_FD65FFCB(a, b): a below 0x100 and b below 3: command 0x1392 {a, b}; else 0x80250001.</summary>
    [Hle(Nid = 0xFD65FFCB, Library = "SceSysconForDriver")]
    int NID_FD65FFCB(uint a, uint b) => a < 0x100 && b < 3 ? Send(0x1392, a | b << 8, 3) : SYSCON_ERR_INVALID;

    /// <summary>NID_02350352(value): below 0x100: command 0x1393 {value}; else 0x80250001.</summary>
    [Hle(Nid = 0x02350352, Library = "SceSysconForDriver")]
    int NID_02350352(uint value) => value < 0x100 ? Send(0x1393, value, 2) : SYSCON_ERR_INVALID;

    /// <summary>NID_7DE84CE3(value): below 0x100: command 0x1394 {value}; else 0x80250001.</summary>
    [Hle(Nid = 0x7DE84CE3, Library = "SceSysconForDriver")]
    int NID_7DE84CE3(uint value) => value < 0x100 ? Send(0x1394, value, 2) : SYSCON_ERR_INVALID;

    /// <summary>NID_5CDDA14D(value): command 0x899 {value} on a syscon after 0x80001; 0 before.</summary>
    [Hle(Nid = 0x5CDDA14D, Library = "SceSysconForDriver")]
    int NID_5CDDA14D(uint value) => Chip.BaryonVersion < 0x80001 ? 0 : Send(0x899, value, 2);

    /// <summary>NID_9CA6EB70(value): command 0x89E {value} on boards from 0x40; 0 before.</summary>
    [Hle(Nid = 0x9CA6EB70, Library = "SceSysconForDriver")]
    int NID_9CA6EB70(uint value) => (BootHardwareInfo & 0xFF0000) < 0x400000 ? 0 : Send(0x89E, value, 2);

    /// <summary>ksceSysconCtrlSdPower(on): refreshes the status byte (command 0); in downloader mode nothing more,
    /// else command 0x888 {on}.</summary>
    [Hle(Nid = 0xBE1ADE4F, Library = "SceSysconForDriver")]
    int ksceSysconCtrlSdPower(uint on) => PowerUnlessDownloader(0x888, on);

    /// <summary>ksceSysconCtrlRMRPower(on): as ksceSysconCtrlSdPower, command 0x89B.</summary>
    [Hle(Nid = 0x710A7CF0, Library = "SceSysconForDriver")]
    int ksceSysconCtrlRMRPower(uint on) => PowerUnlessDownloader(0x89B, on);

    int PowerUnlessDownloader(ushort command, uint on)
    {
        NID_0D0B6D25ThroughEntry();
        if (ksceSysconIsDownLoaderModeThroughEntry() != 0)
        {
            return 0;
        }

        return Send(command, on, 2);
    }

    /// <summary>NID_7F198FA2(a, level): command 0x88E {a, level}; the level is checked against the board (below 0x40
    /// on handhelds of boards 0x60 and 0x80 needs 0x3C or more; from 0x40, on boards before 0x60 and on the 0x70 PS
    /// TV, 0x4D..0x8C, sent as 0x8C - level).</summary>
    [Hle(Nid = 0x7F198FA2, Library = "SceSysconForDriver")]
    int NID_7F198FA2(uint a, uint level)
    {
        uint board = BootHardwareInfo & 0xFF0000;
        if (level < 0x40)
        {
            bool newHandheld = board > 0x5FFFFF && board != 0x700000;
            if (newHandheld && ((board < 0x800000 && board != 0x720000) || level < 0x3C))
            {
                return SYSCON_ERR_INVALID;
            }
        }
        else if (board < 0x600000 || board == 0x700000)
        {
            if (level - 0x4D > 0x3F)
            {
                return SYSCON_ERR_INVALID;
            }

            level = 0x8C - level;
        }

        return SendBytes(0x88E, [(byte)a, (byte)level]);
    }

    /// <summary>NID_CB41B531(a, b, c, d): command 0x89F {a, b, c (2 bytes), d (2 bytes)}.</summary>
    [Hle(Nid = 0xCB41B531, Library = "SceSysconForDriver")]
    int NID_CB41B531(uint a, uint b, uint c, uint d)
    {
        return SendBytes(0x89F, [(byte)a, (byte)b, (byte)c, (byte)(c >> 8), (byte)d, (byte)(d >> 8)]);
    }

    /// <summary>NID_1546A141(first, second): command 0x387 {first ≥ 0, first, 0, 0, second ≥ 0, second, 0, 0} (a
    /// negative value is sent as "absent", 0).</summary>
    [Hle(Nid = 0x1546A141, Library = "SceSysconForDriver")]
    int NID_1546A141(int first, int second)
    {
        byte firstPresent = first < 0 ? (byte)0 : (byte)1;
        byte secondPresent = second < 0 ? (byte)0 : (byte)1;
        byte firstValue = (byte)Math.Max(first, 0);
        byte secondValue = (byte)Math.Max(second, 0);
        return SendBytes(0x387, [firstPresent, firstValue, 0, 0, secondPresent, secondValue, 0, 0]);
    }

    /// <summary>NID_8A97E690(const uint data[4]): command 0x184 with the 16 bytes.</summary>
    [Hle(Nid = 0x8A97E690, Library = "SceSysconForDriver")]
    int NID_8A97E690(uint data) => SendBytes(0x184, GuestMemory.ReadSpan(data, 0x10));

    /// <summary>NID_D2ADABCA(index, value): index below 0x100: command 0x8CC {index, value}; else 0x80250001.</summary>
    [Hle(Nid = 0xD2ADABCA, Library = "SceSysconForDriver")]
    int NID_D2ADABCA(uint index, uint value) => index < 0x100 ? SendBytes(0x8CC, [(byte)index, (byte)value]) : SYSCON_ERR_INVALID;

    /// <summary>NID_CCC71C28(index, value): index below 0x100: command 0x896 {index, value (2 bytes)}.</summary>
    [Hle(Nid = 0xCCC71C28, Library = "SceSysconForDriver")]
    int NID_CCC71C28(uint index, uint value) => WriteIndexed16(0x896, index, value);

    /// <summary>NID_F39300D3(index, value): as NID_CCC71C28, command 0x898.</summary>
    [Hle(Nid = 0xF39300D3, Library = "SceSysconForDriver")]
    int NID_F39300D3(uint index, uint value) => WriteIndexed16(0x898, index, value);

    int WriteIndexed16(ushort command, uint index, uint value)
    {
        if (index >= 0x100)
        {
            return SYSCON_ERR_INVALID;
        }

        // a syscon before 0x70504 takes the value high byte first
        bool swapped = Chip.BaryonVersion < 0x70504;
        byte low = swapped ? (byte)(value >> 8) : (byte)value;
        byte high = swapped ? (byte)value : (byte)(value >> 8);
        return SendBytes(command, [(byte)index, low, high]);
    }

    /// <summary>NID_5BF765BB(index, ushort *value): index below 0x100: command 0x895 {index}, the reply's first two
    /// bytes.</summary>
    [Hle(Nid = 0x5BF765BB, Library = "SceSysconForDriver")]
    int NID_5BF765BB(uint index, uint value) => ReadIndexed16(0x895, index, value);

    /// <summary>NID_95975DD1(index, ushort *value): as NID_5BF765BB, command 0x897.</summary>
    [Hle(Nid = 0x95975DD1, Library = "SceSysconForDriver")]
    int NID_95975DD1(uint index, uint value) => ReadIndexed16(0x897, index, value);

    int ReadIndexed16(ushort command, uint index, uint value)
    {
        if (index >= 0x100)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, [(byte)index], rx);
        if (result >= 0)
        {
            bool swapped = Chip.BaryonVersion < 0x70504;
            ushort word = swapped ? (ushort)(rx[4] << 8 | rx[5]) : (ushort)(rx[4] | rx[5] << 8);
            GuestMemory.Write(value, word);
            result = 0;
        }

        return result;
    }

    /// <summary>NID_7D25F6D2(index, byte *value): index below 0x100: command 0x8CB {index}, the reply's byte.</summary>
    [Hle(Nid = 0x7D25F6D2, Library = "SceSysconForDriver")]
    int NID_7D25F6D2(uint index, uint value)
    {
        if (index >= 0x100)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0x8CB, [(byte)index], rx);
        if (result >= 0)
        {
            GuestMemory.Write(value, rx[4]);
            result = 0;
        }

        return result;
    }

    /// <summary>NID_51164951(time, flag): command 0x82 {time (4 bytes), flag} (a syscon before 0x90907 takes only
    /// the time).</summary>
    [Hle(Nid = 0x51164951, Library = "SceSysconForDriver")]
    int NID_51164951(uint time, uint flag)
    {
        if (Chip.BaryonVersion < 0x90907)
        {
            return Send(0x82, time, 5);
        }

        return SendBytes(0x82, [(byte)time, (byte)(time >> 8), (byte)(time >> 16), (byte)(time >> 24), (byte)flag]);
    }

    // ------------------------------------------------------------------ reads with their own reply

    /// <summary>ksceSysconGetControlsInfo(uint *info): command 0x100, its reply word (stored only on success).</summary>
    [Hle(Nid = 0x145F59A4, Library = "SceSysconForDriver")]
    int ksceSysconGetControlsInfo(uint info) => ReadWord(info, 0x100);

    /// <summary>NID_76272CB9(uint *info): command 0x120, its reply word.</summary>
    [Hle(Nid = 0x76272CB9, Library = "SceSysconForDriver")]
    int NID_76272CB9(uint info) => ReadWord(info, 0x120);

    /// <summary>NID_C562AF3A(uint *value): command 0x900, its reply word.</summary>
    [Hle(Nid = 0xC562AF3A, Library = "SceSysconForDriver")]
    int NID_C562AF3A(uint value) => ReadWord(value, 0x900);

    /// <summary>NID_03F11220(uint *value): command 0x901, its reply word.</summary>
    [Hle(Nid = 0x03F11220, Library = "SceSysconForDriver")]
    int NID_03F11220(uint value) => ReadWord(value, 0x901);

    /// <summary>NID_9070F139(uint *value): command 0x981, its reply word.</summary>
    [Hle(Nid = 0x9070F139, Library = "SceSysconForDriver")]
    int NID_9070F139(uint value) => ReadWord(value, 0x981);

    /// <summary>NID_00A65FC1(uint *value): command 0x982, its reply word.</summary>
    [Hle(Nid = 0x00A65FC1, Library = "SceSysconForDriver")]
    int NID_00A65FC1(uint value) => ReadWord(value, 0x982);

    /// <summary>NID_B841C141(uint *value): command 0x984, its reply word.</summary>
    [Hle(Nid = 0xB841C141, Library = "SceSysconForDriver")]
    int NID_B841C141(uint value) => ReadWord(value, 0x984);

    /// <summary>NID_0826BA07(int *value): command 0x983, its first reply byte as a signed value.</summary>
    [Hle(Nid = 0x0826BA07, Library = "SceSysconForDriver")]
    int NID_0826BA07(uint value) => ReadSignedByte(value, 0x983);

    /// <summary>NID_79074DE4(int *value): command 0x8CA, its first reply byte as a signed value.</summary>
    [Hle(Nid = 0x79074DE4, Library = "SceSysconForDriver")]
    int NID_79074DE4(uint value) => ReadSignedByte(value, 0x8CA);

    int ReadSignedByte(uint output, ushort command)
    {
        int result = ReadWord(command, out uint value);
        if (result == 0)
        {
            GuestMemory.W32(output, (uint)(sbyte)value);
        }

        return result;
    }

    /// <summary>NID_91D3B7A3(uint *first, uint *second): command 0x985, its first and second reply bytes.</summary>
    [Hle(Nid = 0x91D3B7A3, Library = "SceSysconForDriver")]
    int NID_91D3B7A3(uint first, uint second) => ReadTwoBytes(0x985, first, second);

    /// <summary>NID_26F9D729(uint *first, uint *second): command 0x903, its first and second reply bytes.</summary>
    [Hle(Nid = 0x26F9D729, Library = "SceSysconForDriver")]
    int NID_26F9D729(uint first, uint second) => ReadTwoBytes(0x903, first, second);

    /// <summary>NID_BA09F171(uint *first, uint *second): command 0x13, its first and second reply bytes.</summary>
    [Hle(Nid = 0xBA09F171, Library = "SceSysconForDriver")]
    int NID_BA09F171(uint first, uint second) => ReadTwoBytes(0x13, first, second);

    int ReadTwoBytes(ushort command, uint first, uint second)
    {
        int result = ReadWord(command, out uint value);
        if (result == 0)
        {
            if (first != 0)
            {
                GuestMemory.W32(first, value & 0xFF);
            }

            if (second != 0)
            {
                GuestMemory.W32(second, value >> 8 & 0xFF);
            }
        }

        return result;
    }

    /// <summary>NID_253CC522(byte out[2]): command 0x807, its first two reply bytes.</summary>
    [Hle(Nid = 0x253CC522, Library = "SceSysconForDriver")]
    int NID_253CC522(uint output)
    {
        int result = ReadWord(0x807, out uint value);
        if (result == 0)
        {
            GuestMemory.Write(output, (byte)value);
            GuestMemory.Write(output + 1, (byte)(value >> 8));
        }

        return result;
    }

    /// <summary>NID_A1F1B973(): command 0x38A, its reply word as the result (or the error).</summary>
    [Hle(Nid = 0xA1F1B973, Library = "SceSysconForDriver", ReturnsValue = true)]
    int NID_A1F1B973()
    {
        int result = ReadWord(0x38A, out uint value);
        return result < 0 ? result : (int)value;
    }

    /// <summary>NID_3168F3AF(uint *time, uint *flag): command 0x12: the reply word, and the byte after it when the
    /// reply has one (else 0); a NULL time is 0x80250002.</summary>
    [Hle(Nid = 0x3168F3AF, Library = "SceSysconForDriver")]
    int NID_3168F3AF(uint time, uint flag)
    {
        if (time == 0)
        {
            return SYSCON_ERR_NULL;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0x12, rx);
        if (result < 0)
        {
            return result;
        }

        GuestMemory.W32(time, BitConverter.ToUInt32(rx[4..]));
        if (flag != 0)
        {
            GuestMemory.W32(flag, rx[2] < 7 ? 0u : rx[8]);
        }

        return 0;
    }

    /// <summary>ksceSysconGetLogInfo(uint info[2]): command 0x86, two reply words.</summary>
    [Hle(Nid = 0x701535FC, Library = "SceSysconForDriver")]
    int ksceSysconGetLogInfo(uint info) => ReadWords(info, 0x86, 2);

    /// <summary>NID_175CE5A1(uint out[2]): command 0x883, two reply words.</summary>
    [Hle(Nid = 0x175CE5A1, Library = "SceSysconForDriver")]
    int NID_175CE5A1(uint output) => ReadWords(output, 0x883, 2);

    /// <summary>NID_3B57B777(uint out[2]): command 0x3A8, two reply words.</summary>
    [Hle(Nid = 0x3B57B777, Library = "SceSysconForDriver")]
    int NID_3B57B777(uint output) => ReadWords(output, 0x3A8, 2);

    /// <summary>NID_08A561C7(uint out[2]): command 0x3B8, two reply words.</summary>
    [Hle(Nid = 0x08A561C7, Library = "SceSysconForDriver")]
    int NID_08A561C7(uint output) => ReadWords(output, 0x3B8, 2);

    /// <summary>NID_C314B64B(uint out[3]): command 0x185, three reply words.</summary>
    [Hle(Nid = 0xC314B64B, Library = "SceSysconForDriver")]
    int NID_C314B64B(uint output) => ReadWords(output, 0x185, 3);

    /// <summary>NID_B7E294AF(uint out[4]): command 0x182, four reply words.</summary>
    [Hle(Nid = 0xB7E294AF, Library = "SceSysconForDriver")]
    int NID_B7E294AF(uint output) => ReadWords(output, 0x182, 4);

    /// <summary>NID_FDB3AE9D(uint out[4]): command 0x183, four reply words.</summary>
    [Hle(Nid = 0xFDB3AE9D, Library = "SceSysconForDriver")]
    int NID_FDB3AE9D(uint output) => ReadWords(output, 0x183, 4);

    /// <summary>ksceSysconGetTemperatureLog(uint log[4]): command 0x8CE, four reply words.</summary>
    [Hle(Nid = 0x3B354824, Library = "SceSysconForDriver")]
    int ksceSysconGetTemperatureLog(uint log) => ReadWords(log, 0x8CE, 4);

    /// <summary>NID_C51B4E6F(uint out[6]): command 0xA00, six reply words.</summary>
    [Hle(Nid = 0xC51B4E6F, Library = "SceSysconForDriver")]
    int NID_C51B4E6F(uint output) => ReadWords(output, 0xA00, 6);

    /// <summary>NID_B3960346(byte *value): command 0x3AD, its first reply byte; NULL is 0x80250002.</summary>
    [Hle(Nid = 0xB3960346, Library = "SceSysconForDriver")]
    int NID_B3960346(uint value) => ReadOneByte(value, 0x3AD);

    /// <summary>NID_830A9347(byte *value): command 0x3BD, its first reply byte; NULL is 0x80250002.</summary>
    [Hle(Nid = 0x830A9347, Library = "SceSysconForDriver")]
    int NID_830A9347(uint value) => ReadOneByte(value, 0x3BD);

    int ReadOneByte(uint output, ushort command)
    {
        if (output == 0)
        {
            return SYSCON_ERR_NULL;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, rx);
        if (result >= 0)
        {
            GuestMemory.Write(output, rx[4]);
            result = 0;
        }

        return result;
    }

    /// <summary>NID_93FE51F5(uint *value): command 0x3AE, its first three reply bytes; NULL is 0x80250002.</summary>
    [Hle(Nid = 0x93FE51F5, Library = "SceSysconForDriver")]
    int NID_93FE51F5(uint value) => ReadThreeBytes(value, 0x3AE);

    /// <summary>NID_6A093056(uint *value): command 0x3BE, its first three reply bytes; NULL is 0x80250002.</summary>
    [Hle(Nid = 0x6A093056, Library = "SceSysconForDriver")]
    int NID_6A093056(uint value) => ReadThreeBytes(value, 0x3BE);

    int ReadThreeBytes(uint output, ushort command)
    {
        if (output == 0)
        {
            return SYSCON_ERR_NULL;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, rx);
        if (result >= 0)
        {
            GuestMemory.W32(output, (uint)(rx[4] | rx[5] << 8 | rx[6] << 16));
            result = 0;
        }

        return result;
    }

    /// <summary>ksceSysconGetTouchpanelDeviceInfo(ushort info[4]): command 0x380, four big-endian reply words;
    /// NULL is 0x80250002.</summary>
    [Hle(Nid = 0xF492E69E, Library = "SceSysconForDriver")]
    int ksceSysconGetTouchpanelDeviceInfo(uint info) => ReadBigEndianWords(info, 0x380, 4, 0);

    /// <summary>NID_CE48E8EB(ushort info[4]): command 0x1300, four big-endian reply words; NULL is 0x80250002.</summary>
    [Hle(Nid = 0xCE48E8EB, Library = "SceSysconForDriver")]
    int NID_CE48E8EB(uint info) => ReadBigEndianWords(info, 0x1300, 4, 0);

    int ReadBigEndianWords(uint output, ushort command, int count, int replyOffset)
    {
        if (output == 0)
        {
            return SYSCON_ERR_NULL;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, rx);
        if (result >= 0)
        {
            WriteBigEndianWords(output, rx.Slice(4 + replyOffset, count * 2));
            result = 0;
        }

        return result;
    }

    static void WriteBigEndianWords(uint output, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            GuestMemory.Write(output + (uint)i, (ushort)(bytes[i] << 8 | bytes[i + 1]));
        }
    }

    /// <summary>NID_030D447F(info): command 0x390, the touch panel's configuration (0x14 bytes: three big-endian
    /// words, four bytes, three big-endian words, four bytes).</summary>
    [Hle(Nid = 0x030D447F, Library = "SceSysconForDriver")]
    int NID_030D447F(uint info)
    {
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0x390, rx);
        if (result < 0)
        {
            return result;
        }

        WriteBigEndianWords(info, rx.Slice(4, 6));
        rx.Slice(10, 4).CopyTo(GuestMemory.Span(info + 6, 4));
        WriteBigEndianWords(info + 10, rx.Slice(14, 6));
        rx.Slice(20, 4).CopyTo(GuestMemory.Span(info + 16, 4));
        return 0;
    }

    /// <summary>NID_9A4304F2(byte out[8]): command 0x3C5 {0}: reply bytes 0, 1, a word from 3, bytes 5..8.</summary>
    [Hle(Nid = 0x9A4304F2, Library = "SceSysconForDriver")]
    int NID_9A4304F2(uint output) => ReadPanelVersion(0, output);

    /// <summary>NID_C2955814(byte out[8]): as NID_9A4304F2, for the second panel {1}.</summary>
    [Hle(Nid = 0xC2955814, Library = "SceSysconForDriver")]
    int NID_C2955814(uint output) => ReadPanelVersion(1, output);

    int ReadPanelVersion(byte panel, uint output)
    {
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0x3C5, [panel], rx);
        if (result >= 0 && output != 0)
        {
            Span<byte> target = GuestMemory.Span(output, 8);
            target[0] = rx[4];
            target[1] = rx[5];
            target[2] = rx[7];
            target[3] = rx[8];
            rx.Slice(9, 4).CopyTo(target[4..]);
        }

        return Math.Min(result, 0);
    }

    /// <summary>NID_F162D3D6(uint *value): command 0x3C6 {0}, the first reply byte.</summary>
    [Hle(Nid = 0xF162D3D6, Library = "SceSysconForDriver")]
    int NID_F162D3D6(uint value) => ReadPanelByte(0x3C6, 0, value);

    /// <summary>NID_25144105(uint *value): command 0x3C6 {1}, the first reply byte.</summary>
    [Hle(Nid = 0x25144105, Library = "SceSysconForDriver")]
    int NID_25144105(uint value) => ReadPanelByte(0x3C6, 1, value);

    /// <summary>NID_9C840439(uint *value): command 0x3C7 {0}, the first reply byte.</summary>
    [Hle(Nid = 0x9C840439, Library = "SceSysconForDriver")]
    int NID_9C840439(uint value) => ReadPanelByte(0x3C7, 0, value);

    /// <summary>NID_D632D197(uint *value): command 0x3C7 {1}, the first reply byte.</summary>
    [Hle(Nid = 0xD632D197, Library = "SceSysconForDriver")]
    int NID_D632D197(uint value) => ReadPanelByte(0x3C7, 1, value);

    /// <summary>NID_D9B11C0A(uint *value): command 0x39D {0}, the first reply byte.</summary>
    [Hle(Nid = 0xD9B11C0A, Library = "SceSysconForDriver")]
    int NID_D9B11C0A(uint value) => ReadPanelByte(0x39D, 0, value);

    /// <summary>NID_FB37D7AD(uint *value): command 0x39D {1}, the first reply byte.</summary>
    [Hle(Nid = 0xFB37D7AD, Library = "SceSysconForDriver")]
    int NID_FB37D7AD(uint value) => ReadPanelByte(0x39D, 1, value);

    int ReadPanelByte(ushort command, byte panel, uint output)
    {
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, [panel], rx);
        if (result >= 0 && output != 0)
        {
            GuestMemory.W32(output, rx[4]);
        }

        return Math.Min(result, 0);
    }

    /// <summary>NID_68366D29(address, uint *value): command 0x3C4 {0, address (2 bytes)}, reply byte 2.</summary>
    [Hle(Nid = 0x68366D29, Library = "SceSysconForDriver")]
    int NID_68366D29(uint address, uint value) => ReadPanelAddress(0x3C4, 0, address, value);

    /// <summary>NID_2BCE7F5A(address, uint *value): command 0x3C4 {1, address}, reply byte 2.</summary>
    [Hle(Nid = 0x2BCE7F5A, Library = "SceSysconForDriver")]
    int NID_2BCE7F5A(uint address, uint value) => ReadPanelAddress(0x3C4, 1, address, value);

    /// <summary>NID_6450D6BE(address, uint *value): command 0x3C8 {0, address}, reply byte 2.</summary>
    [Hle(Nid = 0x6450D6BE, Library = "SceSysconForDriver")]
    int NID_6450D6BE(uint address, uint value) => ReadPanelAddress(0x3C8, 0, address, value);

    /// <summary>NID_16A466E7(address, uint *value): command 0x3C8 {1, address}, reply byte 2.</summary>
    [Hle(Nid = 0x16A466E7, Library = "SceSysconForDriver")]
    int NID_16A466E7(uint address, uint value) => ReadPanelAddress(0x3C8, 1, address, value);

    int ReadPanelAddress(ushort command, byte panel, uint address, uint output)
    {
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, [panel, (byte)address, (byte)(address >> 8)], rx);
        if (result >= 0 && output != 0)
        {
            GuestMemory.W32(output, rx[6]);
        }

        return Math.Min(result, 0);
    }

    /// <summary>NID_B63AF451(address, uint *value): command 0x3A3 {address (2 bytes)}, reply byte 2.</summary>
    [Hle(Nid = 0xB63AF451, Library = "SceSysconForDriver")]
    int NID_B63AF451(uint address, uint value) => ReadAddressByte(0x3A3, address, value);

    /// <summary>NID_8EBA8A26(address, uint *value): command 0x3B3 {address}, reply byte 2.</summary>
    [Hle(Nid = 0x8EBA8A26, Library = "SceSysconForDriver")]
    int NID_8EBA8A26(uint address, uint value) => ReadAddressByte(0x3B3, address, value);

    int ReadAddressByte(ushort command, uint address, uint output)
    {
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, [(byte)address, (byte)(address >> 8)], rx);
        if (result >= 0)
        {
            GuestMemory.W32(output, rx[6]);
        }

        return Math.Min(result, 0);
    }

    /// <summary>NID_9962CE6F(index, uint *a, ushort *b, ushort *c): command 0x3C9 {0, index}: reply byte 0, words
    /// at 1 and 3.</summary>
    [Hle(Nid = 0x9962CE6F, Library = "SceSysconForDriver")]
    int NID_9962CE6F(uint index, uint a, uint b, uint c) => ReadPanelEntry(0, index, a, b, c);

    /// <summary>NID_31B8F7BE(index, a, b, c): as NID_9962CE6F, for the second panel {1, index}.</summary>
    [Hle(Nid = 0x31B8F7BE, Library = "SceSysconForDriver")]
    int NID_31B8F7BE(uint index, uint a, uint b, uint c) => ReadPanelEntry(1, index, a, b, c);

    int ReadPanelEntry(byte panel, uint index, uint a, uint b, uint c)
    {
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0x3C9, [panel, (byte)index], rx);
        if (result >= 0)
        {
            if (a != 0)
            {
                GuestMemory.W32(a, rx[4]);
            }

            if (b != 0)
            {
                GuestMemory.Write(b, (ushort)(rx[5] | rx[6] << 8));
            }

            if (c != 0)
            {
                GuestMemory.Write(c, (ushort)(rx[7] | rx[8] << 8));
            }
        }

        return Math.Min(result, 0);
    }

    /// <summary>NID_9F9156BE(const byte in[6], out): command 0x3AA with the 6 bytes; the reply's word, half word and
    /// byte go to <paramref name="output"/> when it is not NULL.</summary>
    [Hle(Nid = 0x9F9156BE, Library = "SceSysconForDriver")]
    int NID_9F9156BE(uint input, uint output) => Exchange7(0x3AA, input, output);

    /// <summary>NID_F648BE61(in, out): as NID_9F9156BE, command 0x3BA.</summary>
    [Hle(Nid = 0xF648BE61, Library = "SceSysconForDriver")]
    int NID_F648BE61(uint input, uint output) => Exchange7(0x3BA, input, output);

    int Exchange7(ushort command, uint input, uint output)
    {
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, GuestMemory.ReadSpan(input, 6), rx);
        if (result >= 0 && output != 0)
        {
            rx.Slice(4, 7).CopyTo(GuestMemory.Span(output, 7));
        }

        return Math.Min(result, 0);
    }

    /// <summary>ksceSysconGetBatteryVersion(uint *a, uint *b, uint *c): command 0x980: three reply half words.</summary>
    [Hle(Nid = 0x68E0031E, Library = "SceSysconForDriver")]
    int ksceSysconGetBatteryVersion(uint a, uint b, uint c) => ReadHalfWords(0x980, [a, b, c]);

    /// <summary>ksceSysconGetBatteryCalibData(a, b, c, d): command 0x904: four reply half words.</summary>
    [Hle(Nid = 0x9ADC9936, Library = "SceSysconForDriver")]
    int ksceSysconGetBatteryCalibData(uint a, uint b, uint c, uint d) => ReadHalfWords(0x904, [a, b, c, d]);

    int ReadHalfWords(ushort command, ReadOnlySpan<uint> outputs)
    {
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, rx);
        if (result < 0)
        {
            return result;
        }

        for (int i = 0; i < outputs.Length; i++)
        {
            if (outputs[i] != 0)
            {
                GuestMemory.W32(outputs[i], (uint)(rx[4 + 2 * i] | rx[5 + 2 * i] << 8));
            }
        }

        return 0;
    }

    /// <summary>NID_C2FB5565(register, ushort *value): register below 0x10000: command 0x986 {register (2 bytes)},
    /// the reply's half word.</summary>
    [Hle(Nid = 0xC2FB5565, Library = "SceSysconForDriver")]
    int NID_C2FB5565(uint register, uint value) => ReadBatteryRegister(0x986, register, value);

    /// <summary>NID_A5AB19B1(register, ushort *value): as NID_C2FB5565, command 0x988.</summary>
    [Hle(Nid = 0xA5AB19B1, Library = "SceSysconForDriver")]
    int NID_A5AB19B1(uint register, uint value) => ReadBatteryRegister(0x988, register, value);

    int ReadBatteryRegister(ushort command, uint register, uint output)
    {
        if (register >= 0x10000)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, [(byte)register, (byte)(register >> 8)], rx);
        if (result >= 0)
        {
            GuestMemory.Write(output, (ushort)(rx[4] | rx[5] << 8));
            result = 0;
        }

        return result;
    }

    /// <summary>NID_9B779DB0(register, value): register below 0x10000: command 0x987 {register (2 bytes), value
    /// high byte first}.</summary>
    [Hle(Nid = 0x9B779DB0, Library = "SceSysconForDriver")]
    int NID_9B779DB0(uint register, uint value)
    {
        if (register >= 0x10000)
        {
            return SYSCON_ERR_INVALID;
        }

        return SendBytes(0x987, [(byte)register, (byte)(register >> 8), (byte)(value >> 8), (byte)value]);
    }

    /// <summary>ksceSysconBatterySWReset(): command 0x989 {0}.</summary>
    [Hle(Nid = 0x87DA378D, Library = "SceSysconForDriver")]
    int ksceSysconBatterySWReset() => SendBytes(0x989, [0]);

    /// <summary>ksceSysconBatteryStartBLMode(): command 0x9B0 {0} (the battery's boot loader mode).</summary>
    [Hle(Nid = 0x2CEF078E, Library = "SceSysconForDriver")]
    int ksceSysconBatteryStartBLMode() => SendBytes(0x9B0, [0]);

    /// <summary>ksceSysconBatteryStopBLMode(): command 0x9B1 {0}.</summary>
    [Hle(Nid = 0xE4AE7852, Library = "SceSysconForDriver")]
    int ksceSysconBatteryStopBLMode() => SendBytes(0x9B1, [0]);

    /// <summary>ksceSysconBatteryExecBLCommand(command): 0x9B3 {command (2 bytes), 0}.</summary>
    [Hle(Nid = 0x74B2AB55, Library = "SceSysconForDriver")]
    int ksceSysconBatteryExecBLCommand(uint command) => SendBytes(0x9B3, [(byte)command, (byte)(command >> 8), 0]);

    /// <summary>ksceSysconBatterySetBLCommand(command, a, data, size): size 1..16: 0x9B2 {command (2 bytes), size, a,
    /// data}.</summary>
    [Hle(Nid = 0xE4F29744, Library = "SceSysconForDriver")]
    int ksceSysconBatterySetBLCommand(uint command, uint a, uint data, uint size)
    {
        if (size - 1 >= 0x10)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> frame = stackalloc byte[4 + (int)size];
        frame[0] = (byte)command;
        frame[1] = (byte)(command >> 8);
        frame[2] = (byte)size;
        frame[3] = (byte)a;
        GuestMemory.ReadSpan(data, (int)size).CopyTo(frame[4..]);
        return SendBytes(0x9B2, frame);
    }

    /// <summary>ksceSysconBatteryReadBLCommand(command, a, b, data, size): size 1..16: 0x9B4 {command (2 bytes),
    /// size, a, b}; the reply's bytes from 2 go to <paramref name="data"/>.</summary>
    [Hle(Nid = 0x448DAFF1, Library = "SceSysconForDriver")]
    int ksceSysconBatteryReadBLCommand(uint command, uint a, uint b, uint data, uint size)
    {
        if (size - 1 >= 0x10)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0x9B4, [(byte)command, (byte)(command >> 8), (byte)size, (byte)a, (byte)b], rx);
        if (result >= 0)
        {
            rx.Slice(6, (int)size).CopyTo(GuestMemory.Span(data, (int)size));
            result = 0;
        }

        return result;
    }

    /// <summary>ksceSysconLogReadData(offset, buffer, size): size 1..0x18: command 0x88 {offset (2 bytes)}; the reply
    /// says how many bytes it has (byte 2), 0x80250100 when more than <paramref name="size"/>.</summary>
    [Hle(Nid = 0x487D97F3, Library = "SceSysconForDriver", ReturnsValue = true)]
    int ksceSysconLogReadData(uint offset, uint buffer, uint size)
    {
        if (size - 1 >= 0x18)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0x88, [(byte)offset, (byte)(offset >> 8)], rx);
        if (result < 0)
        {
            return result;
        }

        int count = rx[6];
        if (count > size)
        {
            return SYSCON_ERR_REPLY_LENGTH;
        }

        rx.Slice(7, count).CopyTo(GuestMemory.Span(buffer, count));
        return count;
    }

    // ------------------------------------------------------------------ the chip's memory areas

    /// <summary>ksceSysconReadCommand(offset, buffer, size): the scratchpad (command 0x90), 1..0x18 bytes inside its
    /// 0x100.</summary>
    [Hle(Nid = 0x299B1CE7, Library = "SceSysconForDriver")]
    int ksceSysconReadCommand(uint offset, uint buffer, uint size) => ReadArea(0x90, offset, buffer, size, SysconChip.ScratchpadSize);

    /// <summary>ksceSysconSendCommand(offset, buffer, size): writes the scratchpad (command 0x91).</summary>
    [Hle(Nid = 0xE26488B9, Library = "SceSysconForDriver")]
    int ksceSysconSendCommand(uint offset, uint buffer, uint size) => WriteArea(0x91, offset, buffer, size, SysconChip.ScratchpadSize);

    /// <summary>NID_BF5CDB61(offset, buffer, size): the second scratchpad (command 0x92), inside its 0x80.</summary>
    [Hle(Nid = 0xBF5CDB61, Library = "SceSysconForDriver")]
    int NID_BF5CDB61(uint offset, uint buffer, uint size) => ReadArea(0x92, offset, buffer, size, SysconChip.Scratchpad2Size);

    /// <summary>NID_4E0EB57B(offset, buffer, size): writes the second scratchpad (command 0x93).</summary>
    [Hle(Nid = 0x4E0EB57B, Library = "SceSysconForDriver")]
    int NID_4E0EB57B(uint offset, uint buffer, uint size) => WriteArea(0x93, offset, buffer, size, SysconChip.Scratchpad2Size);

    /// <summary>NID_A6F05153(offset, buffer, size): command 0x1380, 1..0x18 bytes at the offset.</summary>
    [Hle(Nid = 0xA6F05153, Library = "SceSysconForDriver")]
    int NID_A6F05153(uint offset, uint buffer, uint size) => ReadArea(0x1380, offset, buffer, size, 0x10000 + size);

    /// <summary>NID_95C16911(offset, buffer, size): command 0x1381, writes 1..0x18 bytes at the offset.</summary>
    [Hle(Nid = 0x95C16911, Library = "SceSysconForDriver")]
    int NID_95C16911(uint offset, uint buffer, uint size) => WriteArea(0x1381, offset, buffer, size, 0x10000 + size);

    /// <summary>NID_36AC812B(offset, size, buffer): command 0xA80 {offset, size}, 1..0x1B bytes inside 0x100.</summary>
    [Hle(Nid = 0x36AC812B, Library = "SceSysconForDriver")]
    int NID_36AC812B(uint offset, uint size, uint buffer)
    {
        if (size - 1 >= 0x1B || size + offset > 0x100)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0xA80, [(byte)offset, (byte)size], rx);
        if (result >= 0)
        {
            rx.Slice(4, (int)size).CopyTo(GuestMemory.Span(buffer, (int)size));
            result = 0;
        }

        return result;
    }

    /// <summary>NID_26E36625(offset, size, buffer): command 0xA81 {offset, size, bytes}, inside 0x100.</summary>
    [Hle(Nid = 0x26E36625, Library = "SceSysconForDriver")]
    int NID_26E36625(uint offset, uint size, uint buffer)
    {
        if (size - 1 >= 0x1B || size + offset > 0x100)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> data = stackalloc byte[2 + (int)size];
        data[0] = (byte)offset;
        data[1] = (byte)size;
        GuestMemory.ReadSpan(buffer, (int)size).CopyTo(data[2..]);
        return SendBytes(0xA81, data);
    }

    /// <summary>NID_1C5D466C(offset, buffer, size): the touch panel firmware area (command 0x384).</summary>
    [Hle(Nid = 0x1C5D466C, Library = "SceSysconForDriver")]
    int NID_1C5D466C(uint offset, uint buffer, uint size) => ReadFirmwareArea(0x384, offset, buffer, size);

    /// <summary>NID_6D408E30(offset, buffer, size): the second panel's area (command 0x386).</summary>
    [Hle(Nid = 0x6D408E30, Library = "SceSysconForDriver")]
    int NID_6D408E30(uint offset, uint buffer, uint size) => ReadFirmwareArea(0x386, offset, buffer, size);

    /// <summary>NID_856DFC0E(offset, buffer, size): writes the touch panel firmware area (command 0x383).</summary>
    [Hle(Nid = 0x856DFC0E, Library = "SceSysconForDriver")]
    int NID_856DFC0E(uint offset, uint buffer, uint size) => WriteFirmwareArea(0x383, offset, buffer, size);

    /// <summary>NID_E38045F5(offset, buffer, size): writes the second panel's area (command 0x385).</summary>
    [Hle(Nid = 0xE38045F5, Library = "SceSysconForDriver")]
    int NID_E38045F5(uint offset, uint buffer, uint size) => WriteFirmwareArea(0x385, offset, buffer, size);

    /// <summary>NID_B9835A63(address, offset, data, size, uint *result): size up to 0x18: command 0x3A2 {address,
    /// offset (2 bytes each), size, data}; reply byte 2 to <paramref name="result"/>.</summary>
    [Hle(Nid = 0xB9835A63, Library = "SceSysconForDriver")]
    int NID_B9835A63(uint address, uint offset, uint data, uint size, uint result) => WritePanelBlock(0x3A2, address, offset, data, size, result);

    /// <summary>NID_CD6A6BC7(address, offset, data, size, result): as NID_B9835A63, command 0x3B2.</summary>
    [Hle(Nid = 0xCD6A6BC7, Library = "SceSysconForDriver")]
    int NID_CD6A6BC7(uint address, uint offset, uint data, uint size, uint result) => WritePanelBlock(0x3B2, address, offset, data, size, result);

    int WritePanelBlock(ushort command, uint address, uint offset, uint data, uint size, uint output)
    {
        if (size >= 0x19)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> frame = stackalloc byte[5 + (int)size];
        frame[0] = (byte)address;
        frame[1] = (byte)(address >> 8);
        frame[2] = (byte)offset;
        frame[3] = (byte)(offset >> 8);
        frame[4] = (byte)size;
        GuestMemory.ReadSpan(data, (int)size).CopyTo(frame[5..]);
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, frame, rx);
        if (result >= 0)
        {
            GuestMemory.W32(output, rx[6]);
        }

        return Math.Min(result, 0);
    }

    /// <summary>NID_4DFA12FB(address, offset, data, size, uint *result): size up to 0x20: the long frame 0x3C3 {0,
    /// address, offset (2 bytes each), size, data}; reply byte 2 to <paramref name="result"/>.</summary>
    [Hle(Nid = 0x4DFA12FB, Library = "SceSysconForDriver")]
    int NID_4DFA12FB(uint address, uint offset, uint data, uint size, uint result) => WritePanelLong(0x3C3, 0, address, offset, data, size, result, 2);

    /// <summary>NID_3B06F619(...): as NID_4DFA12FB for the second panel {1}.</summary>
    [Hle(Nid = 0x3B06F619, Library = "SceSysconForDriver")]
    int NID_3B06F619(uint address, uint offset, uint data, uint size, uint result) => WritePanelLong(0x3C3, 1, address, offset, data, size, result, 2);

    /// <summary>NID_94418CB4(index, offset, size, data, uint *result): size up to 0x20: the long frame 0x3CB {0,
    /// index, offset (2 bytes), size, data}; the reply's first byte to <paramref name="result"/>.</summary>
    [Hle(Nid = 0x94418CB4, Library = "SceSysconForDriver")]
    int NID_94418CB4(uint index, uint offset, uint size, uint data, uint result) => WritePanelIndexed(0, index, offset, size, data, result);

    /// <summary>NID_7EC67149(...): as NID_94418CB4 for the second panel {1}.</summary>
    [Hle(Nid = 0x7EC67149, Library = "SceSysconForDriver")]
    int NID_7EC67149(uint index, uint offset, uint size, uint data, uint result) => WritePanelIndexed(1, index, offset, size, data, result);

    int WritePanelLong(ushort command, byte panel, uint address, uint offset, uint data, uint size, uint output, int replyByte)
    {
        if (size >= 0x21)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> frame = stackalloc byte[6 + (int)size];
        frame[0] = panel;
        frame[1] = (byte)address;
        frame[2] = (byte)(address >> 8);
        frame[3] = (byte)offset;
        frame[4] = (byte)(offset >> 8);
        frame[5] = (byte)size;
        GuestMemory.ReadSpan(data, (int)size).CopyTo(frame[6..]);
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, frame, rx, longFrame: true);
        if (result >= 0 && output != 0)
        {
            GuestMemory.W32(output, rx[4 + replyByte]);
        }

        return Math.Min(result, 0);
    }

    int WritePanelIndexed(byte panel, uint index, uint offset, uint size, uint data, uint output)
    {
        if (size >= 0x21)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> frame = stackalloc byte[5 + (int)size];
        frame[0] = panel;
        frame[1] = (byte)index;
        frame[2] = (byte)offset;
        frame[3] = (byte)(offset >> 8);
        frame[4] = (byte)size;
        GuestMemory.ReadSpan(data, (int)size).CopyTo(frame[5..]);
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0x3CB, frame, rx, longFrame: true);
        if (result >= 0 && output != 0)
        {
            GuestMemory.W32(output, rx[4]);
        }

        return Math.Min(result, 0);
    }

    /// <summary>NID_CA203A5F(index, offset, size, buffer, uint *count): size up to 0x20: command 0x3CA {0, index,
    /// offset (2 bytes), size, 0} with a long reply; the count of bytes and the bytes.</summary>
    [Hle(Nid = 0xCA203A5F, Library = "SceSysconForDriver")]
    int NID_CA203A5F(uint index, uint offset, uint size, uint buffer, uint count) => ReadPanelLong(0, index, offset, size, buffer, count);

    /// <summary>NID_55B6FEF5(...): as NID_CA203A5F for the second panel {1}.</summary>
    [Hle(Nid = 0x55B6FEF5, Library = "SceSysconForDriver")]
    int NID_55B6FEF5(uint index, uint offset, uint size, uint buffer, uint count) => ReadPanelLong(1, index, offset, size, buffer, count);

    int ReadPanelLong(byte panel, uint index, uint offset, uint size, uint buffer, uint count)
    {
        if (size >= 0x21)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x80];
        int result = Exec(0x3CA, [panel, (byte)index, (byte)offset, (byte)(offset >> 8), (byte)size, 0], rx);
        if (result < 0)
        {
            return result;
        }

        int received = Math.Min(rx[4], (int)size);
        if (count != 0)
        {
            GuestMemory.W32(count, rx[4]);
        }

        if (buffer != 0)
        {
            rx.Slice(5, received).CopyTo(GuestMemory.Span(buffer, received));
        }

        return 0;
    }

    /// <summary>NID_76506A57(index, offset, buffer, size): offset below 0x3F0, size 1..0x40: command 0x1390 {index,
    /// offset (2 bytes), size} with a long reply; the bytes.</summary>
    [Hle(Nid = 0x76506A57, Library = "SceSysconForDriver")]
    int NID_76506A57(uint index, uint offset, uint buffer, uint size)
    {
        if (offset >= 0x3F0 || size - 1 >= 0x40)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x80];
        int result = Exec(0x1390, [(byte)index, (byte)offset, (byte)(offset >> 8), (byte)size], rx);
        if (result >= 0)
        {
            rx.Slice(4, (int)size).CopyTo(GuestMemory.Span(buffer, (int)size));
            result = 0;
        }

        return result;
    }

    /// <summary>NID_F9584DC9(index, offset, buffer, size): size 1..0x40: the long frame 0x1391 {index, offset (2
    /// bytes), size, bytes}.</summary>
    [Hle(Nid = 0xF9584DC9, Library = "SceSysconForDriver")]
    int NID_F9584DC9(uint index, uint offset, uint buffer, uint size)
    {
        if (size - 1 >= 0x40)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> frame = stackalloc byte[4 + (int)size];
        frame[0] = (byte)index;
        frame[1] = (byte)offset;
        frame[2] = (byte)(offset >> 8);
        frame[3] = (byte)size;
        GuestMemory.ReadSpan(buffer, (int)size).CopyTo(frame[4..]);
        Span<byte> rx = stackalloc byte[0x20];
        return Math.Min(Exec(0x1391, frame, rx, longFrame: true), 0);
    }

    /// <summary>NID_270B7B0B(buffer, size): command 0x400 with its reply in the caller's buffer; the count of bytes
    /// the reply had.</summary>
    [Hle(Nid = 0x270B7B0B, Library = "SceSysconForDriver", ReturnsValue = true)]
    int NID_270B7B0B(uint buffer, uint size)
    {
        if (size < 0x21)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = GuestMemory.Span(buffer, (int)size);
        int result = Exec(0x400, rx);
        return result < 0 ? result : rx[2] + 3;
    }

    // ------------------------------------------------------------------ the configuration storage and modes

    /// <summary>ksceSysconLoadConfigstorageScript(offset, script, size): size 1..0x40: the long frame 0x1385 {offset
    /// (2 bytes), size, script}.</summary>
    [Hle(Nid = 0x89C5CFD6, Library = "SceSysconForDriver")]
    int ksceSysconLoadConfigstorageScript(uint offset, uint script, uint size) => SendScript(0x1385, offset, script, size);

    /// <summary>ksceSysconVerifyConfigstorageScript(offset, script, size): as the load, command 0x1386.</summary>
    [Hle(Nid = 0xCC6F90A8, Library = "SceSysconForDriver")]
    int ksceSysconVerifyConfigstorageScript(uint offset, uint script, uint size) => SendScript(0x1386, offset, script, size);

    int SendScript(ushort command, uint offset, uint script, uint size)
    {
        if (size - 1 >= 0x40)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> frame = stackalloc byte[3 + (int)size];
        frame[0] = (byte)offset;
        frame[1] = (byte)(offset >> 8);
        frame[2] = (byte)size;
        GuestMemory.ReadSpan(script, (int)size).CopyTo(frame[3..]);
        Span<byte> rx = stackalloc byte[0x20];
        return Math.Min(Exec(command, frame, rx, longFrame: true), 0);
    }

    /// <summary>ksceSysconIduModeSet(): the module's configuration transaction (begin 0x1382, two 0x20-byte scripts
    /// of its own, commit 0x1384, end 0x1383) that turns the IDU (demo unit) mode on.</summary>
    [Hle(Nid = 0x956D07CB, Library = "SceSysconForDriver")]
    int ksceSysconIduModeSet() => ConfigTransaction(SysconChip.ModeIdu, true);

    /// <summary>ksceSysconIduModeClear(): the transaction that turns the IDU mode off.</summary>
    [Hle(Nid = 0x34574496, Library = "SceSysconForDriver")]
    int ksceSysconIduModeClear() => ConfigTransaction(SysconChip.ModeIdu | SysconChip.ModeShow, false);

    /// <summary>ksceSysconShowModeSet(): the transaction that turns the show (store display) mode on.</summary>
    [Hle(Nid = 0x6D65B70F, Library = "SceSysconForDriver")]
    int ksceSysconShowModeSet() => ConfigTransaction(SysconChip.ModeShow, true);

    /// <summary>ksceSysconShowModeClear(): the same transaction as the IDU clear (the module sends the same
    /// scripts): both modes off.</summary>
    [Hle(Nid = 0x8D7724C0, Library = "SceSysconForDriver")]
    int ksceSysconShowModeClear() => ConfigTransaction(SysconChip.ModeIdu | SysconChip.ModeShow, false);

    int ConfigTransaction(uint mode, bool set)
    {
        int result = ksceSysconBeginConfigstorageTransactionThroughEntry();
        if (result < 0)
        {
            return result;
        }

        // the scripts are the module's own data; the chip keeps what they turn on
        result = ksceSysconCommitConfigstorageTransactionThroughEntry();
        if (result < 0)
        {
            ksceSysconEndConfigstorageTransactionThroughEntry();
            return result;
        }

        Chip.ConfigModes = set ? Chip.ConfigModes | mode : Chip.ConfigModes & ~mode;
        return Math.Min(ksceSysconEndConfigstorageTransactionThroughEntry(), 0);
    }

    // ------------------------------------------------------------------ the updater

    /// <summary>ksceSysconUpdaterSetSegment(segment): command 0x1180 {segment, 0}.</summary>
    [Hle(Nid = 0x9B00BC7F, Library = "SceSysconForDriver")]
    int ksceSysconUpdaterSetSegment(uint segment) => SendBytes(0x1180, [(byte)segment, 0]);

    /// <summary>NID_356B9696(data, offset, size): command 0x1181 {offset (2 bytes), size, data}; NULL data is
    /// 0x80250001.</summary>
    [Hle(Nid = 0x356B9696, Library = "SceSysconForDriver")]
    int NID_356B9696(uint data, uint offset, uint size) => UpdaterWrite(data, offset, size, longFrame: false);

    /// <summary>NID_734544E4(data, offset, size): the long-frame form of NID_356B9696.</summary>
    [Hle(Nid = 0x734544E4, Library = "SceSysconForDriver")]
    int NID_734544E4(uint data, uint offset, uint size) => UpdaterWrite(data, offset, size, longFrame: true);

    int UpdaterWrite(uint data, uint offset, uint size, bool longFrame)
    {
        if (data == 0)
        {
            return SYSCON_ERR_INVALID;
        }

        size &= 0xFF;
        Span<byte> frame = stackalloc byte[3 + (int)size];
        frame[0] = (byte)offset;
        frame[1] = (byte)(offset >> 8);
        frame[2] = (byte)size;
        GuestMemory.ReadSpan(data, (int)size).CopyTo(frame[3..]);
        Span<byte> rx = stackalloc byte[0x20];
        return Exec(0x1181, frame, rx, longFrame);
    }

    /// <summary>ksceSysconUpdaterExecProgramming(address): command 0x1182 {address (4 bytes), 0}.</summary>
    [Hle(Nid = 0x69AD76E4, Library = "SceSysconForDriver")]
    int ksceSysconUpdaterExecProgramming(uint address)
    {
        Span<byte> rx = stackalloc byte[0x20];
        return Exec(0x1182, [(byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24), 0], rx);
    }

    /// <summary>ksceSysconUpdaterSetRunMode(mode): one of the module's five mode keys (0x152E, 0x3665, 0x72BA,
    /// 0x9A54, 0xC5E7): command 0x1183 {mode (2 bytes), 0}; else 0x80250001.</summary>
    [Hle(Nid = 0xB487C2FB, Library = "SceSysconForDriver")]
    int ksceSysconUpdaterSetRunMode(uint mode)
    {
        if (mode is not (0x152E or 0x3665 or 0x72BA or 0x9A54 or 0xC5E7))
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        return Exec(0x1183, [(byte)mode, (byte)(mode >> 8), 0], rx);
    }

    /// <summary>ksceSysconUpdaterExecFinalize(data, size): exactly 0x14 bytes: command 0x1184 with them.</summary>
    [Hle(Nid = 0xC7747A63, Library = "SceSysconForDriver")]
    int ksceSysconUpdaterExecFinalize(uint data, uint size) => SendExact(0x1184, data, size, 0x14);

    /// <summary>NID_CBA836FF(data, size): exactly 0x18 bytes: command 0x1185 with them.</summary>
    [Hle(Nid = 0xCBA836FF, Library = "SceSysconForDriver")]
    int NID_CBA836FF(uint data, uint size) => SendExact(0x1185, data, size, 0x18);

    int SendExact(ushort command, uint data, uint size, uint expected)
    {
        if (data == 0 || size != expected)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        return Exec(command, GuestMemory.ReadSpan(data, (int)size), rx);
    }

    /// <summary>ksceSysconUpdaterCalcChecksum(data, size, uint *sum): the inverted 32-bit sum of the bytes (no chip
    /// command); a NULL pointer or no bytes is 0x80250001.</summary>
    [Hle(Nid = 0xD27C3D80, Library = "SceSysconForDriver")]
    int ksceSysconUpdaterCalcChecksum(uint data, uint size, uint sum)
    {
        if (size == 0 || data == 0 || sum == 0)
        {
            return SYSCON_ERR_INVALID;
        }

        uint total = 0;
        foreach (byte value in GuestMemory.ReadSpan(data, (int)size))
        {
            total += value;
        }

        GuestMemory.W32(sum, ~total);
        return 0;
    }

    /// <summary>NID_D2F456DC(byte version[4]): command 0x1100: the reply's four bytes in reverse order; NULL is
    /// 0x80250001.</summary>
    [Hle(Nid = 0xD2F456DC, Library = "SceSysconForDriver")]
    int NID_D2F456DC(uint version)
    {
        if (version == 0)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0x1100, [0], rx);
        if (result >= 0)
        {
            Span<byte> target = GuestMemory.Span(version, 4);
            target[0] = rx[7];
            target[1] = rx[6];
            target[2] = rx[5];
            target[3] = rx[4];
        }

        return result;
    }

    // ------------------------------------------------------------------ the secure exchange (SceSblSsMgr)

    /// <summary>The syscon authentication with the security processor (commands 0xD0..0xD3): its answers come from
    /// keys only the secure side and the real chip have: "not supported" (0x80020004), after the module's own
    /// argument checks.</summary>
    const int SECURE_UNSUPPORTED = unchecked((int)0x80020004);

    /// <summary>NID_4D03754A(challenge, 0x28, response, 0x28): the first authentication step (command 0xD0).</summary>
    [Hle(Nid = 0x4D03754A, Library = "SceSysconForDriver", Default = true)]
    int NID_4D03754A(uint challenge, uint challengeSize, uint response, uint responseSize)
    {
        bool valid = challenge != 0 && challengeSize == 0x28 && response != 0 && responseSize == 0x28;
        return valid ? SECURE_UNSUPPORTED : SYSCON_ERR_INVALID;
    }

    /// <summary>NID_EBDF88B9(challenge, 0x10, response, 0x30): the second step (command 0xD2).</summary>
    [Hle(Nid = 0xEBDF88B9, Library = "SceSysconForDriver", Default = true)]
    int NID_EBDF88B9(uint challenge, uint challengeSize, uint response, uint responseSize)
    {
        bool valid = challenge != 0 && challengeSize == 0x10 && response != 0 && responseSize == 0x30;
        return valid ? SECURE_UNSUPPORTED : SYSCON_ERR_INVALID;
    }

    /// <summary>NID_63683B9B(challenge, 0x30, response, 0x10): the third step (command 0xD3).</summary>
    [Hle(Nid = 0x63683B9B, Library = "SceSysconForDriver", Default = true)]
    int NID_63683B9B(uint challenge, uint challengeSize, uint response, uint responseSize)
    {
        bool valid = challenge != 0 && challengeSize == 0x30 && response != 0 && responseSize == 0x10;
        return valid ? SECURE_UNSUPPORTED : SYSCON_ERR_INVALID;
    }

    /// <summary>NID_C14BD637(): command 0xD1 without data (resets the exchange).</summary>
    [Hle(Nid = 0xC14BD637, Library = "SceSysconForDriver")]
    int NID_C14BD637()
    {
        Span<byte> rx = stackalloc byte[0x20];
        return Exec(0xD1, rx);
    }

    // ------------------------------------------------------------------ the jig port

    /// <summary>ksceSysconJigOpenPort(): command 0x2080 {0}; the port is then open. Open already: 0x80250001.</summary>
    [Hle(Nid = 0x44A173F5, Library = "SceSysconForDriver")]
    int ksceSysconJigOpenPort()
    {
        if (_jigPort >= 1)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0x2080, [0], rx);
        if (result >= 0 && _jigPort < 0)
        {
            _jigPort = 1;
        }

        return result;
    }

    /// <summary>ksceSysconJigClosePort(): command 0x2081 {0}; closed already: 0x80250001.</summary>
    [Hle(Nid = 0x483FAE05, Library = "SceSysconForDriver")]
    int ksceSysconJigClosePort()
    {
        if (_jigPort < 0)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(0x2081, [0], rx);
        if (result >= 0)
        {
            _jigPort = -1;
        }

        return result;
    }

    /// <summary>ksceSysconJigSetConfig(a, b): on an open port, command 0x2082 {a, b, 0, 0}.</summary>
    [Hle(Nid = 0xD24BF916, Library = "SceSysconForDriver")]
    int ksceSysconJigSetConfig(uint a, uint b)
    {
        if (_jigPort < 0)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        return Exec(0x2082, [(byte)a, (byte)b, 0, 0], rx);
    }

    /// <summary>NID_3C80B529(offset, size, buffer): on an open port, reads in pieces of up to 0x18 bytes (command
    /// 0x2083 {offset, piece}) inside 0x40; NULL buffer 0x80250002.</summary>
    [Hle(Nid = 0x3C80B529, Library = "SceSysconForDriver")]
    int NID_3C80B529(uint offset, uint size, uint buffer)
    {
        if (buffer == 0)
        {
            return SYSCON_ERR_NULL;
        }

        if (offset >= 0x40 || size == 0 || _jigPort < 0)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        int result = 0;
        while (size != 0)
        {
            uint piece = Math.Min(size, 0x18u);
            result = Exec(0x2083, [(byte)offset, (byte)piece], rx);
            if (result < 0)
            {
                break;
            }

            rx.Slice(4, (int)piece).CopyTo(GuestMemory.Span(buffer, (int)piece));
            offset += piece;
            size -= piece;
            buffer += piece;
        }

        return result;
    }

    /// <summary>NID_CE346793(size, data): on an open port, command 0x2084 {0, size, bytes} (1..0x18 bytes); NULL data
    /// 0x80250002.</summary>
    [Hle(Nid = 0xCE346793, Library = "SceSysconForDriver")]
    int NID_CE346793(uint size, uint data)
    {
        if (data == 0)
        {
            return SYSCON_ERR_NULL;
        }

        if (size - 1 >= 0x18 || _jigPort < 0)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> frame = stackalloc byte[2 + (int)size];
        frame[0] = 0;
        frame[1] = (byte)size;
        GuestMemory.ReadSpan(data, (int)size).CopyTo(frame[2..]);
        Span<byte> rx = stackalloc byte[0x20];
        return Exec(0x2084, frame, rx);
    }

    /// <summary>NID_7BFBA09E(index, offset, size, data): on an open port, writes in pieces of up to 0x18 bytes
    /// (command 0x2085 {index, offset, piece, bytes}) inside 0x40; NULL data 0x80250002.</summary>
    [Hle(Nid = 0x7BFBA09E, Library = "SceSysconForDriver")]
    int NID_7BFBA09E(uint index, uint offset, uint size, uint data)
    {
        if (data == 0)
        {
            return SYSCON_ERR_NULL;
        }

        if (offset >= 0x40 || size == 0 || offset + size >= 0x40 || _jigPort < 0)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> frame = stackalloc byte[3 + 0x18];
        Span<byte> rx = stackalloc byte[0x20];
        int result = 0;
        while (size != 0)
        {
            uint piece = Math.Min(size, 0x18u);
            frame[0] = (byte)index;
            frame[1] = (byte)offset;
            frame[2] = (byte)piece;
            GuestMemory.ReadSpan(data, (int)piece).CopyTo(frame[3..]);
            result = Exec(0x2085, frame[..(3 + (int)piece)], rx);
            if (result < 0)
            {
                break;
            }

            size -= piece;
            offset += piece;
            data += piece;
        }

        return result;
    }

    /// <summary>NID_933D813F(index, offset, size): on an open port, command 0x2085 {index, offset, size} (inside
    /// 0x40).</summary>
    [Hle(Nid = 0x933D813F, Library = "SceSysconForDriver")]
    int NID_933D813F(uint index, uint offset, uint size)
    {
        if (offset >= 0x40 || size == 0 || offset + size >= 0x40 || _jigPort < 0)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> rx = stackalloc byte[0x20];
        return Exec(0x2085, [(byte)index, (byte)offset, (byte)size], rx);
    }

    /// <summary>NID_0396BA09(): command 0x3A6 without data.</summary>
    [Hle(Nid = 0x0396BA09, Library = "SceSysconForDriver")]
    int NID_0396BA09()
    {
        Span<byte> rx = stackalloc byte[0x20];
        return Exec(0x3A6, rx);
    }

    /// <summary>NID_23558805(): command 0x3B6 without data.</summary>
    [Hle(Nid = 0x23558805, Library = "SceSysconForDriver")]
    int NID_23558805()
    {
        Span<byte> rx = stackalloc byte[0x20];
        return Exec(0x3B6, rx);
    }
}
