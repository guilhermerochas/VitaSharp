namespace VitaSharp.Core.Hle.Modules;

// SceDmacmgrForDriver (os0:kd/dmacmgr.skprx 3.74, read in the module, study only): the DMA controllers. Two entries
// that keep no operation: the channel reset (a controller kind and a channel, 0xFFFF for all; else 0x80020005) and an
// entry that always refuses (0x80020002). The sixth controller (kind 6) is only mapped when one of the debug switches
// 0x9F, 0xC0..0xC2 is set. The DMA operations (DmaOp*) are in SystemServicesVsh.DmacmgrOps.cs.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceDmacmgr: a kind or channel out of range (0x80020005).</summary>
    const int DMAC_ERR_INVALID_ARGUMENT = unchecked((int)0x80020005);

    /// <summary>The debug switches that map the sixth controller.</summary>
    static ReadOnlySpan<uint> DmacSixthControllerSwitches => [0x9F, 0xC0, 0xC1, 0xC2];

    bool DmacSixthControllerMapped()
    {
        foreach (uint bit in DmacSixthControllerSwitches)
        {
            if (ksceKernelCheckDipswThroughEntry(bit) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>ksceDmacResetChannel(int kind, int channel): kinds 0, 2, 3, 5 (and 6 when mapped) take channel 0, 1 or
    /// 0xFFFF; kind 1 the same; kind 4 channels 0..15 or 0xFFFF; anything else 0x80020005; the channel's registers
    /// cleared; 0.</summary>
    [Hle(Nid = 0xBCBB5E85, Library = "SceDmacmgrForDriver")]
    int ksceDmacResetChannel(uint kind, uint channel)
    {
        const uint AllChannels = 0xFFFF;
        bool valid = kind switch
        {
            0 or 1 or 2 or 3 or 5 => channel < 2 || channel == AllChannels,
            4 => channel <= 0xF || channel == AllChannels,
            6 => DmacSixthControllerMapped() && (channel < 2 || channel == AllChannels),
            _ => false,
        };
        return valid ? 0 : DMAC_ERR_INVALID_ARGUMENT;
    }

    /// <summary>ksceDmacUnsupported(): the module's entry that always answers 0x80020002.</summary>
    [Hle(Nid = 0x893FCB80, Library = "SceDmacmgrForDriver")]
    int ksceDmacUnsupported()
    {
        return unchecked((int)0x80020002);
    }
}
