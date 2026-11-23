using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePervasiveForDriver's setters of the base clock page (0xE3103000) and of the misc page (0xE3100000): each one
/// checks its argument as the firmware does (0x803F0000 when it is out of range), writes the value into its register
/// and answers 0, or, for the switches, sets or clears the register and answers whether it was on before.
/// </summary>
public sealed partial class PervasiveKernel
{
    const uint BaseClockPage = 0xE3103000 - PhysicalBase;

    /// <summary>The error the module answers for an argument out of range.</summary>
    const int InvalidArgument = unchecked((int)0x803F0000);

    uint ReadRegister(uint offset) => GuestMemory.R32(PageAddress(PhysicalBase) + offset);

    void WriteRegister(uint offset, uint value) => GuestMemory.W32(PageAddress(PhysicalBase) + offset, value);

    /// <summary>Writes <paramref name="value"/> into the base clock register; 0.</summary>
    int SetBaseClock(uint register, uint value)
    {
        lock (_registerLock)
        {
            WriteRegister(BaseClockPage + register, value);
        }

        return 0;
    }

    /// <summary>Writes the register at <paramref name="offset"/> bytes into the pages with <paramref name="value"/> and
    /// answers whether it held a value other than 0 before (the module's switches).</summary>
    int Switch(uint offset, uint value)
    {
        lock (_registerLock)
        {
            uint before = ReadRegister(offset);
            WriteRegister(offset, value);
            return before != 0 ? 1 : 0;
        }
    }

    /// <summary>Replaces the <paramref name="clear"/> bits of the misc register with <paramref name="set"/>.</summary>
    void ChangeMisc(uint register, uint clear, uint set)
    {
        lock (_registerLock)
        {
            WriteRegister(MiscPage + register, (ReadRegister(MiscPage + register) & ~clear) | set);
        }
    }

    // ------------------------------------------------------------------ base clock page (0xE3103000)

    /// <summary>NID_6AF0196F(high, low): base clock register 0x10 = high &lt;&lt; 16 | low, both 0..7 (the module sets
    /// it at start: 5 with the 0xEC dip switch, else 3).</summary>
    [Hle(Nid = 0x6AF0196F, Library = "ScePervasiveForDriver")]
    int NID_6AF0196F(uint high, uint low) => high < 8 && low < 8 ? SetBaseClock(0x10, high << 16 | low) : InvalidArgument;

    /// <summary>NID_725B4F59(value): base clock register 0x20, 1..7.</summary>
    [Hle(Nid = 0x725B4F59, Library = "ScePervasiveForDriver")]
    int NID_725B4F59(uint value) => value - 1 < 7 ? SetBaseClock(0x20, value) : InvalidArgument;

    /// <summary>NID_12D17D47(value): base clock register 0x30, 1..6.</summary>
    [Hle(Nid = 0x12D17D47, Library = "ScePervasiveForDriver")]
    int NID_12D17D47(uint value) => value - 1 < 6 ? SetBaseClock(0x30, value) : InvalidArgument;

    /// <summary>NID_589D0291(value): base clock register 0x44, 1..5.</summary>
    [Hle(Nid = 0x589D0291, Library = "ScePervasiveForDriver")]
    int NID_589D0291(uint value) => value - 1 < 5 ? SetBaseClock(0x44, value) : InvalidArgument;

    /// <summary>NID_925D9D24(on): base clock register 0x70 = 0x10000 | on, on 0..1.</summary>
    [Hle(Nid = 0x925D9D24, Library = "ScePervasiveForDriver")]
    int NID_925D9D24(uint on) => on < 2 ? SetBaseClock(0x70, 0x10000 | on) : InvalidArgument;

    /// <summary>NID_59B7CA34(source, value): base clock registers 0x60 (value - 1 for source 0, value 1..16; 0 for
    /// source 1, value 1) and 0x64 (the source); a gate on in register 0xA4 is turned off around the change, as the
    /// firmware does.</summary>
    [Hle(Nid = 0x59B7CA34, Library = "ScePervasiveForDriver")]
    int NID_59B7CA34(uint source, uint value)
    {
        uint divider;
        if (source == 0)
        {
            divider = value - 1;
            if (divider > 0xF)
            {
                return InvalidArgument;
            }
        }
        else if (source == 1 && value == 1)
        {
            divider = 0;
        }
        else
        {
            return InvalidArgument;
        }

        lock (_registerLock)
        {
            uint gate = ReadRegister(BaseClockPage + 0xA4);
            if (gate != 0)
            {
                WriteRegister(BaseClockPage + 0xA4, 0);
            }

            WriteRegister(BaseClockPage + 0x60, divider);
            WriteRegister(BaseClockPage + 0x64, source);
            if (gate != 0)
            {
                WriteRegister(BaseClockPage + 0xA4, gate);
            }
        }

        return 0;
    }

    /// <summary>NID_8847825F(): base clock register 0xA4 = 1; whether it was on before.</summary>
    [Hle(Nid = 0x8847825F, Library = "ScePervasiveForDriver")]
    int NID_8847825F() => Switch(BaseClockPage + 0xA4, 1);

    /// <summary>NID_9E81E057(): base clock register 0xA4 = 0; whether it was on before.</summary>
    [Hle(Nid = 0x9E81E057, Library = "ScePervasiveForDriver")]
    int NID_9E81E057() => Switch(BaseClockPage + 0xA4, 0);

    /// <summary>kscePervasiveMsifSetClock(clock): base clock register 0xB0 = (clock &amp; 3) | 0x10000 when bit 2 is
    /// set, for clock 0..2 and 4..6.</summary>
    [Hle(Nid = 0x64ABE589, Library = "ScePervasiveForDriver")]
    int kscePervasiveMsifSetClock(uint clock) =>
        (clock & ~4u) < 3 ? SetBaseClock(0xB0, (clock & 3) | ((clock & 4) != 0 ? 0x10000u : 0)) : InvalidArgument;

    /// <summary>NID_26B51075(): base clock register 0x1D0 = 1; 0.</summary>
    [Hle(Nid = 0x26B51075, Library = "ScePervasiveForDriver")]
    int NID_26B51075() => SetBaseClock(0x1D0, 1);

    /// <summary>NID_D185D235(): base clock register 0x1D0 = 0; 0.</summary>
    [Hle(Nid = 0xD185D235, Library = "ScePervasiveForDriver")]
    int NID_D185D235() => SetBaseClock(0x1D0, 0);

    /// <summary>NID_A6832B33(value): base clock register 0x1F0, 1..4.</summary>
    [Hle(Nid = 0xA6832B33, Library = "ScePervasiveForDriver")]
    int NID_A6832B33(uint value) => value - 1 < 4 ? SetBaseClock(0x1F0, value) : InvalidArgument;

    /// <summary>NID_2FB5F88F(value): base clock register 0x210, 0..2.</summary>
    [Hle(Nid = 0x2FB5F88F, Library = "ScePervasiveForDriver")]
    int NID_2FB5F88F(uint value) => value < 3 ? SetBaseClock(0x210, value) : InvalidArgument;

    /// <summary>NID_98D8914A(value): base clock register 0x214, 0..1.</summary>
    [Hle(Nid = 0x98D8914A, Library = "ScePervasiveForDriver")]
    int NID_98D8914A(uint value) => value < 2 ? SetBaseClock(0x214, value) : InvalidArgument;

    /// <summary>NID_A96642E3(value): base clock register 0x68 = value | 2 for 0..1, 0 for 0xFF.</summary>
    [Hle(Nid = 0xA96642E3, Library = "ScePervasiveForDriver")]
    int NID_A96642E3(uint value)
    {
        if (value < 2)
        {
            return SetBaseClock(0x68, value | 2);
        }

        return value == 0xFF ? SetBaseClock(0x68, 0) : InvalidArgument;
    }

    // ------------------------------------------------------------------ misc page (0xE3100000)

    /// <summary>NID_A1D2E440(value): misc register 0x190 = value; 0.</summary>
    [Hle(Nid = 0xA1D2E440, Library = "ScePervasiveForDriver")]
    int NID_A1D2E440(uint value)
    {
        lock (_registerLock)
        {
            WriteRegister(MiscPage + 0x190, value);
        }

        return 0;
    }

    /// <summary>NID_F1908D3C(which): misc register 0x140 (which 0) or 0x148 (any other) = 1; whether it was on.</summary>
    [Hle(Nid = 0xF1908D3C, Library = "ScePervasiveForDriver")]
    int NID_F1908D3C(uint which) => Switch(MiscPage + (which == 0 ? 0x140u : 0x148u), 1);

    /// <summary>NID_350BF570(which): misc register 0x140 (which 0) or 0x148 (any other) = 0; whether it was on.</summary>
    [Hle(Nid = 0x350BF570, Library = "ScePervasiveForDriver")]
    int NID_350BF570(uint which) => Switch(MiscPage + (which == 0 ? 0x140u : 0x148u), 0);

    /// <summary>NID_0C1A7FFA(): misc register 0x144 = 1; whether it was on.</summary>
    [Hle(Nid = 0x0C1A7FFA, Library = "ScePervasiveForDriver")]
    int NID_0C1A7FFA() => Switch(MiscPage + 0x144, 1);

    /// <summary>NID_5B4EF352(): misc register 0x144 = 0; whether it was on.</summary>
    [Hle(Nid = 0x5B4EF352, Library = "ScePervasiveForDriver")]
    int NID_5B4EF352() => Switch(MiscPage + 0x144, 0);

    /// <summary>NID_453BD2EC(bit, value): bit <paramref name="bit"/> (0..3) of misc register 0x124 = value (0..1).</summary>
    [Hle(Nid = 0x453BD2EC, Library = "ScePervasiveForDriver")]
    int NID_453BD2EC(uint bit, uint value)
    {
        if (bit >= 4 || value >= 2)
        {
            return InvalidArgument;
        }

        ChangeMisc(0x124, 1u << (int)bit, value << (int)bit);
        return 0;
    }

    /// <summary>NID_043B33F5(field, value): the 3-bit field <paramref name="field"/> (0..3, at bit 8 * field) of
    /// misc register 0x310 = value (0..6).</summary>
    [Hle(Nid = 0x043B33F5, Library = "ScePervasiveForDriver")]
    int NID_043B33F5(uint field, uint value)
    {
        if (field >= 4 || value >= 7)
        {
            return InvalidArgument;
        }

        int shift = (int)field * 8;
        ChangeMisc(0x310, 7u << shift, value << shift);
        return 0;
    }

    /// <summary>NID_9F8E589C(index, value): misc register 0x110 + 4 * index (0..3) = value (only bits 8..10).</summary>
    [Hle(Nid = 0x9F8E589C, Library = "ScePervasiveForDriver")]
    int NID_9F8E589C(uint index, uint value)
    {
        if (index >= 4 || (value & ~0x700u) != 0)
        {
            return InvalidArgument;
        }

        lock (_registerLock)
        {
            WriteRegister(MiscPage + 0x110 + index * 4, value);
        }

        return 0;
    }

    /// <summary>NID_5E20D7D5(): bit 0 of misc register 0x194 on; 0.</summary>
    [Hle(Nid = 0x5E20D7D5, Library = "ScePervasiveForDriver")]
    int NID_5E20D7D5()
    {
        ChangeMisc(0x194, 1, 1);
        return 0;
    }

    /// <summary>NID_E6728E30(): bit 0 of misc register 0x194 off; 0.</summary>
    [Hle(Nid = 0xE6728E30, Library = "ScePervasiveForDriver")]
    int NID_E6728E30()
    {
        ChangeMisc(0x194, 1, 0);
        return 0;
    }

    /// <summary>NID_03EA9418(): bit 0 of misc register 0x198 on; 0.</summary>
    [Hle(Nid = 0x03EA9418, Library = "ScePervasiveForDriver")]
    int NID_03EA9418()
    {
        ChangeMisc(0x198, 1, 1);
        return 0;
    }

    /// <summary>NID_BD868E6E(): bit 0 of misc register 0x198 off; 0.</summary>
    [Hle(Nid = 0xBD868E6E, Library = "ScePervasiveForDriver")]
    int NID_BD868E6E()
    {
        ChangeMisc(0x198, 1, 0);
        return 0;
    }

    /// <summary>NID_FDD445C3(): bit 0 of misc register 0x188 on; 0.</summary>
    [Hle(Nid = 0xFDD445C3, Library = "ScePervasiveForDriver")]
    int NID_FDD445C3()
    {
        ChangeMisc(0x188, 1, 1);
        return 0;
    }

    /// <summary>NID_132E514A(): bit 0 of misc register 0x188 off; 0.</summary>
    [Hle(Nid = 0x132E514A, Library = "ScePervasiveForDriver")]
    int NID_132E514A()
    {
        ChangeMisc(0x188, 1, 0);
        return 0;
    }

    /// <summary>NID_34537B12(): bits 0..1 of misc registers 0x1AC and 0x18C off; 0.</summary>
    [Hle(Nid = 0x34537B12, Library = "ScePervasiveForDriver")]
    int NID_34537B12()
    {
        ChangeMisc(0x1AC, 3, 0);
        ChangeMisc(0x18C, 3, 0);
        return 0;
    }

    /// <summary>NID_4B99558F(): bits 0..1 of misc registers 0x1AC and 0x18C on; 0.</summary>
    [Hle(Nid = 0x4B99558F, Library = "ScePervasiveForDriver")]
    int NID_4B99558F()
    {
        ChangeMisc(0x1AC, 3, 3);
        ChangeMisc(0x18C, 3, 3);
        return 0;
    }

    /// <summary>NID_6B2BE37E(port, on): misc register 0x84 + 4 * port (USB port 0..2) = on &amp; 1; whether it was on.</summary>
    [Hle(Nid = 0x6B2BE37E, Library = "ScePervasiveForDriver")]
    int NID_6B2BE37E(uint port, uint on) => port < 3 ? Switch(MiscPage + 0x84 + port * 4, on & 1) : InvalidArgument;

    /// <summary>NID_4F64DEF6(port): brings USB port <paramref name="port"/> up as the firmware does: reset bits 0xB on
    /// and gate bits 0xB off in its registers, misc register 0x84 + 4 * port = 1 (ports 0..2), reset bit 3 off, and
    /// its 2-bit field of misc register 0x17C = 2; 1.</summary>
    [Hle(Nid = 0x4F64DEF6, Library = "ScePervasiveForDriver")]
    int NID_4F64DEF6(uint port)
    {
        NID_4AF7A01EThroughEntry(port, 0xB);
        NID_AD1E81EBThroughEntry(port, 0xB);
        if (port < 3)
        {
            Switch(MiscPage + 0x84 + port * 4, 1);
        }

        NID_13CC07C9ThroughEntry(port, 8);
        if (port < 3)
        {
            int shift = (int)port * 2;
            ChangeMisc(0x17C, 3u << shift, 2u << shift);
        }

        return 1;
    }

    /// <summary>NID_BB67EF58(port): puts USB port <paramref name="port"/> down: reset bit 3 on and its 2-bit field of
    /// misc register 0x17C cleared (ports 0..2); 1.</summary>
    [Hle(Nid = 0xBB67EF58, Library = "ScePervasiveForDriver")]
    int NID_BB67EF58(uint port)
    {
        NID_4AF7A01EThroughEntry(port, 8);
        if (port < 3)
        {
            ChangeMisc(0x17C, 3u << ((int)port * 2), 0);
        }

        return 1;
    }

    /// <summary>NID_29E65AE1(port, first, second): misc registers 0x150 + 4 * port = first and 0x15C + 4 * port =
    /// second (ports 0..2; others are ignored); 1.</summary>
    [Hle(Nid = 0x29E65AE1, Library = "ScePervasiveForDriver")]
    int NID_29E65AE1(uint port, uint first, uint second)
    {
        if (port < 3)
        {
            lock (_registerLock)
            {
                WriteRegister(MiscPage + 0x150 + port * 4, first);
                WriteRegister(MiscPage + 0x15C + port * 4, second);
            }
        }

        return 1;
    }

    /// <summary>NID_E3A735B9(port): misc register 0x150 + 4 * port (ports 0..2), else 0.</summary>
    [Hle(Nid = 0xE3A735B9, Library = "ScePervasiveForDriver", ReturnsValue = true)]
    uint NID_E3A735B9(uint port) => port < 3 ? ReadRegister(MiscPage + 0x150 + port * 4) : 0;

    /// <summary>kscePervasiveRemovableMemoryGetCardInsertState(): bit 0 of register 0xF40 of the 0xE3110000 page.</summary>
    [Hle(Nid = 0x551EEE82, Library = "ScePervasiveForDriver")]
    int kscePervasiveRemovableMemoryGetCardInsertState() => (int)(ReadRegister(0xE3110000 - PhysicalBase + 0xF40) & 1);

    /// <summary>NID_F9E7B023(): writes register 0xF44 of the 0xE3110000 page with its own value (the firmware's
    /// acknowledge of the card interrupt).</summary>
    [Hle(Nid = 0xF9E7B023, Library = "ScePervasiveForDriver")]
    void NID_F9E7B023()
    {
        uint offset = 0xE3110000 - PhysicalBase + 0xF44;
        lock (_registerLock)
        {
            WriteRegister(offset, ReadRegister(offset));
        }
    }
}
