using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Two helpers np_common.suprx 3.74 (decompiled, study only) exports to the other NP modules: the initializer of its
/// 0x248-byte request record and atoi on top of its strtol.
/// </summary>
public sealed partial class NpManager
{
    /// <summary>NID_650D111B(record, first, second, third): the 0x248-byte record zeroed, with first at +0, second at
    /// +0x240 and third at +0x244; 0.</summary>
    [Hle(Nid = 0x650D111B, Library = "SceNpCommon")]
    int NID_650D111B(uint record, uint first, uint second, uint third)
    {
        GuestMemory.Fill(record, 0, 0x248);
        ResetXml(record);
        GuestMemory.W32(record, first);
        GuestMemory.W32(record + 0x240, second);
        GuestMemory.W32(record + 0x244, third);
        return 0;
    }

    /// <summary>NID_CC8E6AC6(s): atoi, the module's strtol (NID_4C4AA451) in base 10 without an end pointer.</summary>
    [Hle(Nid = 0xCC8E6AC6, Library = "SceNpCommon")]
    int NID_CC8E6AC6(uint text) => NpCommonStrtolThroughEntry(text, 0, 10);
}
