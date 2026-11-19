using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceDsiForDriver (os0:kd/lowio.skprx 3.74, read in the module, study only): the two display serial interface heads
// (0 the built-in panel, 1 the HDMI transmitter; another head 0x803F0600). The module keeps a table of fifteen video
// modes (VIC codes: the panel's three, 960x544 or 960x540, and the CEA modes the HDMI head can send), each with its
// timings and its pixel clock for 24- and 30-bit pixels; a code outside the table, or whose entry the panel type
// removed, is 0x803F0601. The panel type of head 0 picks the panel's entries (type 0 the OLED's three, type 1 the
// LCD's one; head 1 with type 1 removes them). Lanes and pixel size: head 0 two lanes of 24 bits, head 1 two or three
// lanes of 24 or 30 bits (else 0x803F0602). A head is enabled, started (mode 0..4, else 0x803F0602; not enabled
// 0x803F0601) and disabled; the packet calls send DSI commands to its panel. The emulated unit's panel takes every
// command and answers reads with nothing; the scanline counter of an enabled head reads 0 (no raster here).
public sealed partial class SystemServicesVsh
{
    /// <summary>SceDsi: a head above 1 (0x803F0600).</summary>
    const int DSI_ERR_HEAD = unchecked((int)0x803F0600);

    /// <summary>SceDsi: a mode outside the table, or the head not set up (0x803F0601).</summary>
    const int DSI_ERR_MODE = unchecked((int)0x803F0601);

    /// <summary>SceDsi: a setting out of range (0x803F0602).</summary>
    const int DSI_ERR_SETTING = unchecked((int)0x803F0602);

    /// <summary>SceDsi: the head is not enabled (0x803F0603).</summary>
    const int DSI_ERR_NOT_ENABLED = unchecked((int)0x803F0603);

    const int DsiHeads = 2;

    /// <summary>One video mode's timings: the flags (bit 3: two pixels less of width), the pixel clocks for 24- and
    /// 30-bit pixels, the totals, interlacing and the front porch, sync and back porch of each axis.</summary>
    readonly record struct DsiTiming(uint Flags, uint Clock24, uint Clock30, int HTotal, int VTotal, bool Interlaced,
        int HFront, int HSync, int HBack, int VFront, int VSync, int VBack)
    {
        public int Width => HTotal - (HFront + HSync + HBack) - ((Flags & 8) != 0 ? 2 : 0);

        public int Height => Interlaced ? VTotal + 1 - (VFront + VSync + VBack) * 2 : VTotal - (VFront + VSync + VBack);
    }

    /// <summary>The OLED panel's three modes (VIC 0 and 0x20 960x544, 0x80 960x540) and the LCD panel's one (VIC 0
    /// with panel type 1).</summary>
    static readonly DsiTiming[] s_dsiPanelOled =
    [
        new(5, 2243100, 0, 1050, 594, false, 20, 66, 4, 4, 4, 42),
        new(5, 2243100, 0, 1250, 594, false, 20, 266, 4, 4, 4, 46),
        new(5, 2243100, 0, 1375, 567, false, 20, 391, 4, 4, 4, 15),
    ];

    static readonly DsiTiming s_dsiPanelLcd = new(4, 2356363, 0, 1120, 585, false, 136, 4, 20, 4, 4, 33);

    /// <summary>The VIC codes of the table, in its order (the first three are the panel's).</summary>
    static ReadOnlySpan<uint> DsiVicCodes =>
    [
        0, 0x80, 0x20, 0x8900, 0x8300, 0x8370, 0x8480, 0x84F0, 0x8500, 0x8580, 0x8600, 0x8680, 0x8710, 0x8790, 0x8730,
    ];

    /// <summary>The HDMI modes of the table (entries 3..14): 720p of the PS TV, 480p/576p (two variants each), 1080i,
    /// 720p and 1080p at 60 and 50 Hz, 1080p at 24 Hz.</summary>
    static readonly DsiTiming[] s_dsiHdmiModes =
    [
        new(15, 2653427, 0, 1428, 775, false, 114, 8, 24, 4, 4, 47),
        new(1, 1080000, 1350000, 858, 525, false, 16, 62, 60, 9, 6, 30),
        new(1, 1080000, 1350000, 858, 526, false, 16, 62, 60, 10, 6, 30),
        new(1, 1080000, 1350000, 864, 625, false, 12, 64, 68, 5, 5, 39),
        new(1, 1080000, 1350000, 864, 626, false, 12, 64, 68, 6, 5, 40),
        new(0, 2967033, 3708791, 2200, 1125, true, 88, 44, 148, 2, 5, 16),
        new(0, 2970000, 3712500, 2640, 1125, true, 528, 44, 148, 2, 5, 16),
        new(0, 2967033, 3708791, 1650, 750, false, 110, 40, 220, 5, 5, 20),
        new(0, 2970000, 3712500, 1980, 750, false, 440, 40, 220, 5, 5, 20),
        new(0, 2967033, 3708791, 2200, 1125, false, 88, 44, 148, 4, 5, 36),
        new(0, 2970000, 3712500, 2640, 1125, false, 528, 44, 148, 4, 5, 36),
        new(0, 2967033, 3708791, 2750, 1125, false, 638, 44, 148, 4, 5, 36),
    ];

    [SnapshotMissing] // states of builds before it: panel type 0 (the OLED's modes), nothing set on the heads
    uint _dsiPanelModes;

    [SnapshotMissing]
    readonly uint[] _dsiPanelType = new uint[DsiHeads];

    [SnapshotMissing]
    readonly uint[] _dsiLanes = new uint[DsiHeads];

    [SnapshotMissing]
    readonly uint[] _dsiPixelBits = new uint[DsiHeads];

    [SnapshotMissing]
    readonly int[] _dsiVicIndex = new int[DsiHeads];

    [SnapshotMissing]
    readonly bool[] _dsiVicSet = new bool[DsiHeads];

    [SnapshotMissing]
    readonly bool[] _dsiEnabled = new bool[DsiHeads];

    [SnapshotMissing]
    readonly uint[] _dsiStartMode = new uint[DsiHeads];

    /// <summary>Which panel entries the table holds: 0 the OLED's three, 1 the LCD's one, 2 none.</summary>
    const uint DsiPanelOled = 0, DsiPanelLcd = 1, DsiPanelNone = 2;

    /// <summary>The table's entry <paramref name="index"/>, or null where the panel type removed it.</summary>
    DsiTiming? DsiEntry(int index)
    {
        if (index >= 3)
        {
            return s_dsiHdmiModes[index - 3];
        }

        return _dsiPanelModes switch
        {
            DsiPanelOled => s_dsiPanelOled[index],
            DsiPanelLcd when index == 0 => s_dsiPanelLcd,
            _ => null,
        };
    }

    /// <summary>The table index of a VIC code with an entry, or -1.</summary>
    int DsiFind(uint vic)
    {
        int index = DsiVicCodes.IndexOf(vic);
        return index >= 0 && DsiEntry(index) != null ? index : -1;
    }

    /// <summary>The heads' settings, for the tests.</summary>
    internal (uint Lanes, uint Bits, bool VicSet, bool Enabled) DsiHeadState(int head) =>
        (_dsiLanes[head], _dsiPixelBits[head], _dsiVicSet[head], _dsiEnabled[head]);

    /// <summary>ksceDsiSetPanelType(int head, int type): kept; head 0 type 0 the OLED's modes, head 0 type 1 the
    /// LCD's, head 1 type 1 none; 0.</summary>
    [Hle(Nid = 0xF2921E29, Library = "SceDsiForDriver")]
    int ksceDsiSetPanelType(uint head, uint type)
    {
        if (head >= DsiHeads)
        {
            return DSI_ERR_HEAD;
        }

        _dsiPanelType[head] = type & 0xFF;
        if (head == 0 && type == 0)
        {
            _dsiPanelModes = DsiPanelOled;
        }
        else if (head == 0 && type == 1)
        {
            _dsiPanelModes = DsiPanelLcd;
        }
        else if (head == 1 && type == 1)
        {
            _dsiPanelModes = DsiPanelNone;
        }

        return 0;
    }

    /// <summary>ksceDsiSetLanesAndPixelSize(int head, int lanes, int bits): head 0 two lanes of 24 bits, head 1 two
    /// or three lanes of 24 or 30 bits (else 0x803F0602); 0.</summary>
    [Hle(Nid = 0x78E6E3CF, Library = "SceDsiForDriver")]
    int ksceDsiSetLanesAndPixelSize(uint head, uint lanes, uint bits)
    {
        if (head >= DsiHeads)
        {
            return DSI_ERR_HEAD;
        }

        bool valid = head == 0 ? lanes == 2 && bits == 24 : (bits is 24 or 30) && lanes - 2 <= 1;
        if (!valid)
        {
            return DSI_ERR_SETTING;
        }

        _dsiLanes[head] = lanes;
        _dsiPixelBits[head] = bits;
        return 0;
    }

    /// <summary>ksceDsiGetVicResolution(int vic, int *width, int *height): the mode's visible size (either pointer may
    /// be NULL); a code without an entry 0x803F0601.</summary>
    [Hle(Nid = 0xB3A70C05, Library = "SceDsiForDriver")]
    int ksceDsiGetVicResolution(uint vic, uint width, uint height)
    {
        int index = DsiFind(vic);
        if (index < 0)
        {
            return DSI_ERR_MODE;
        }

        DsiTiming timing = DsiEntry(index)!.Value;
        if (width != 0)
        {
            GuestMemory.W32(width, (uint)timing.Width);
        }

        if (height != 0)
        {
            GuestMemory.W32(height, (uint)timing.Height);
        }

        return 0;
    }

    /// <summary>ksceDsiSetVic(int head, int vic): the head's mode; a code without an entry 0x803F0601; 0.</summary>
    [Hle(Nid = 0x97BFEA76, Library = "SceDsiForDriver")]
    int ksceDsiSetVic(uint head, uint vic)
    {
        if (head >= DsiHeads)
        {
            return DSI_ERR_HEAD;
        }

        int index = DsiFind(vic);
        if (index < 0)
        {
            return DSI_ERR_MODE;
        }

        _dsiVicIndex[head] = index;
        _dsiVicSet[head] = true;
        return 0;
    }

    /// <summary>ksceDsiGetPixelClock(int head): the head's mode's pixel clock for its pixel size (24 bits, else the
    /// 30-bit one); no mode set 0x803F0601.</summary>
    [Hle(Nid = 0x4DF9E924, Library = "SceDsiForDriver", ReturnsValue = true)]
    int ksceDsiGetPixelClock(uint head)
    {
        if (head >= DsiHeads)
        {
            return DSI_ERR_HEAD;
        }

        if (!_dsiVicSet[head] || DsiEntry(_dsiVicIndex[head]) is not { } timing)
        {
            return DSI_ERR_MODE;
        }

        return (int)(_dsiPixelBits[head] == 24 ? timing.Clock24 : timing.Clock30);
    }

    /// <summary>ksceDsiSetRefreshScale(int head, int numerator, int denominator): rescales the head's line rate;
    /// 0.</summary>
    [Hle(Nid = 0x8610B795, Library = "SceDsiForDriver")]
    int ksceDsiSetRefreshScale(uint head, uint numerator, uint denominator)
    {
        return head < DsiHeads ? 0 : DSI_ERR_HEAD;
    }

    /// <summary>ksceDsiEnableHead(int head): powers the head's controller; 0.</summary>
    [Hle(Nid = 0x5BE5AA9B, Library = "SceDsiForDriver")]
    int ksceDsiEnableHead(uint head)
    {
        if (head >= DsiHeads)
        {
            return DSI_ERR_HEAD;
        }

        _dsiEnabled[head] = true;
        return 0;
    }

    /// <summary>ksceDsiDisableHead(int head): stops the head (not enabled: nothing); 0.</summary>
    [Hle(Nid = 0x114D1413, Library = "SceDsiForDriver")]
    int ksceDsiDisableHead(uint head)
    {
        if (head >= DsiHeads)
        {
            return DSI_ERR_HEAD;
        }

        _dsiEnabled[head] = false;
        return 0;
    }

    /// <summary>ksceDsiStartDisplay(int head, int mode): mode 0..4 (else 0x803F0602); the head not enabled
    /// 0x803F0601; starts sending its mode; 0.</summary>
    [Hle(Nid = 0xC2E85919, Library = "SceDsiForDriver")]
    int ksceDsiStartDisplay(uint head, uint mode)
    {
        if (head >= DsiHeads)
        {
            return DSI_ERR_HEAD;
        }

        if (mode > 4)
        {
            return DSI_ERR_SETTING;
        }

        if (!_dsiEnabled[head])
        {
            return DSI_ERR_MODE;
        }

        _dsiStartMode[head] = mode;
        return 0;
    }

    /// <summary>ksceDsiSendBlankingPacket(int head): sends a blanking packet on an enabled head; 0.</summary>
    [Hle(Nid = 0x7640F607, Library = "SceDsiForDriver")]
    int ksceDsiSendBlankingPacket(uint head)
    {
        return head < DsiHeads ? 0 : DSI_ERR_HEAD;
    }

    /// <summary>ksceDsiGetScanline(int head): the enabled head's line counter (no raster here: 0); not enabled
    /// 0x803F0603.</summary>
    [Hle(Nid = 0x6F8029A1, Library = "SceDsiForDriver", ReturnsValue = true)]
    int ksceDsiGetScanline(uint head)
    {
        if (head >= DsiHeads)
        {
            return DSI_ERR_HEAD;
        }

        return _dsiEnabled[head] ? 0 : DSI_ERR_NOT_ENABLED;
    }

    /// <summary>ksceDsiGenericShortWrite(int head, int first, int second): a short write packet to the panel;
    /// 0.</summary>
    [Hle(Nid = 0x89C00D2F, Library = "SceDsiForDriver", Default = true)]
    int ksceDsiGenericShortWrite(uint head, uint first, uint second)
    {
        return 0;
    }

    /// <summary>ksceDsiDcsShortWrite(int head, int command, int parameter): a DCS short write to the panel; 0.</summary>
    [Hle(Nid = 0xBA6BC89F, Library = "SceDsiForDriver", Default = true)]
    int ksceDsiDcsShortWrite(uint head, uint command, uint parameter)
    {
        return 0;
    }

    /// <summary>ksceDsiGenericReadRequest(int head, int parameter, void *buffer): a read packet; the panel answers
    /// nothing; 0.</summary>
    [Hle(Nid = 0x98120684, Library = "SceDsiForDriver", Default = true)]
    int ksceDsiGenericReadRequest(uint head, uint parameter, uint buffer)
    {
        return 0;
    }

    /// <summary>ksceDsiDcsRead(int head, int command, void *buffer): a DCS read; the panel answers nothing; 0.</summary>
    [Hle(Nid = 0x3FB0DF1F, Library = "SceDsiForDriver", Default = true)]
    int ksceDsiDcsRead(uint head, uint command, uint buffer)
    {
        return 0;
    }
}
