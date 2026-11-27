namespace VitaSharp.Core.Hle.Modules;

// SceGrabForDriver (os0:kd/lowio.skprx 3.74, read in the module, study only): the display grabber and the PSP
// compatibility LCD DMA, which live in the secure world: each entry is a secure monitor call (SMC services 0x11B..0x121,
// the sub-command in r0) that programs the grabber's registers there. The emulated unit has no secure world nor
// grabber hardware: the calls are accepted (0), as documented defaults.
public sealed partial class SystemServicesVsh
{
    [Hle("ksceGrabSetClientRequestMask", Nid = 0xB22EE8BC, Library = "SceGrabForDriver", Default = true)]
    [Hle("ksceGrabWriteIndexedRegister", Nid = 0x188BBCC8, Library = "SceGrabForDriver", Default = true)]
    [Hle("ksceGrabCompatStartEx", Nid = 0x072B8D93, Library = "SceGrabForDriver", Default = true)]
    [Hle("ksceGrabCompatLcdDmacInit", Nid = 0x379130B2, Library = "SceGrabForDriver", Default = true)]
    [Hle("ksceGrabCompatStop", Nid = 0xF13C63DD, Library = "SceGrabForDriver", Default = true)]
    [Hle("ksceGrabWaitForSonyRegbusFlags", Nid = 0x5F36ABC4, Library = "SceGrabForDriver", Default = true)]
    [Hle("ksceGrabSetGpuClockSelectors", Nid = 0x1F292554, Library = "SceGrabForDriver", Default = true)]
    int GrabSecureMonitorCall()
    {
        return 0;
    }

    /// <summary>ksceGrabSetMemoryBankAddress (0xE9C25A28)(int bank, uintptr_t physicalAndEnable): the Vita memory
    /// one 16 MB bank of the PSP compat core's RAM is (bit 0 set: mapped), which SceCompat's InitEx programs for its
    /// window (lowio 3.74: the grabber's bank register in the secure world). Here the bank of the core's bus; the
    /// emulated unit's physical addresses are its virtual ones. Always 0, as the secure monitor call.</summary>
    [Hle(Nid = 0xE9C25A28, Library = "SceGrabForDriver")]
    internal int ksceGrabSetMemoryBankAddress(int bank, uint physicalAndEnable)
    {
        foreach (HleModule module in Emu.Hle.Modules)
        {
            if (module is Compat compat)
            {
                compat.Bus.SetBank(bank, physicalAndEnable);
            }
        }

        return 0;
    }
}
