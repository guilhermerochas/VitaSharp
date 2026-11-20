using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The ScePervasiveForDriver exports of the FW 3.74 SceLowio that the module hands to the secure monitor (smc 0x110,
/// 0x111, 0x113, 0x114: the TrustZone-protected reset and gate registers and the secure devices' power and clock
/// settings), the ARM clock divider and the DSI pixel clocks. The monitor makes the same read-modify-write the
/// non-secure exports do (bit 0 of the register; 0 returned), so the reset and gate exports change the same register
/// pages; the device modes and clock values it keeps are kept here. The pixel clocks are the module's table of 20 PLL
/// settings (by the clock's value in 100 Hz units), each with its byte and pixel clock in MHz.
/// </summary>
public sealed partial class PervasiveKernel
{
    const int PervasiveInvalidArgument = unchecked((int)0x803F0000);

    /// <summary>The secure devices' modes (smc 0x110: 0 the GPU, 1 the codec engine, 2 the third device).</summary>
    [SnapshotMissing] // states of builds before it: the boot state
    readonly uint[] _secureDeviceModes = new uint[3];

    /// <summary>The secure clock settings (smc 0x114, by index 0..3).</summary>
    [SnapshotMissing]
    readonly uint[] _secureClocks = new uint[4];

    /// <summary>The ARM clock: the domain selected (low nibble) and the divider word (16 - ratio).</summary>
    [SnapshotMissing]
    uint _armClockDomain, _armClockDivider;

    /// <summary>The pixel clock of each DSI port (0 off).</summary>
    [SnapshotMissing]
    readonly uint[] _dsiPixelClocks = new uint[2];

    /// <summary>The DSI pixel clocks the module knows: the clock and the bits of its byte clock and pixel clock (MHz,
    /// doubles).</summary>
    static readonly (uint Clock, ulong ByteClock, ulong PixelClock)[] PixelClocks =
    [
        (0xF5ADD, 0x40592EC500000000, 0x40392EC500000000),
        (0x107AC0, 0x405B000000000000, 0x403B000000000000),
        (0x1334F5, 0x405F77F1A0000000, 0x40392CC140000000),
        (0x149970, 0x4070E00000000000, 0x404B000000000000),
        (0x17C7AA, 0x40637B0340000000, 0x40437B0340000000),
        (0x186A00, 0x4064000000000000, 0x4044000000000000),
        (0x1DB994, 0x406859C440000000, 0x40437B0360000000),
        (0x1E8480, 0x4069000000000000, 0x4044000000000000),
        (0x223A1C, 0x406C09D8A0000000, 0x4042B13B20000000),
        (0x23125E, 0x406CBB13C0000000, 0x4043276280000000),
        (0x23F48B, 0x406B000000000000, 0x4042000000000000),
        (0x27AC40, 0x4070400000000000, 0x4050400000000000),
        (0x287CF3, 0x4070957B80000000, 0x4050957B80000000),
        (0x299D69, 0x40710BA2E0000000, 0x40510BA2E0000000),
        (0x2D45F9, 0x40728B40C0000000, 0x40528B40C0000000),
        (0x2D5190, 0x4072900000000000, 0x4052900000000000),
        (0x31A031, 0x407453A300000000, 0x405042E8C0000000),
        (0x3404C3, 0x40754E8BA0000000, 0x40510BA2E0000000),
        (0x389777, 0x40772E10E0000000, 0x40528B40C0000000),
        (0x38A5F4, 0x4077340000000000, 0x4052900000000000),
    ];

    static int PixelClockIndex(uint clock)
    {
        for (int i = 0; i < PixelClocks.Length; i++)
        {
            if (PixelClocks[i].Clock == clock)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>smc 0x111 / 0x113: the monitor's read-modify-write of bit 0 of a reset or gate register; 0.</summary>
    int SecureUpdate(uint page, uint register, bool set)
    {
        UpdateBits(page + register, 1, set);
        return 0;
    }

    int SecureDeviceMode(uint device, uint mode)
    {
        _secureDeviceModes[device] = mode;
        return 0;
    }

    int SecureClock(uint index, uint value)
    {
        _secureClocks[index] = value;
        return 0;
    }

    // ------------------------------------------------------------------ smc 0x111: the secure reset registers

    /// <summary>NID_377126CD(): secure reset register 0x20 on.</summary>
    [Hle(Nid = 0x377126CD, Library = "ScePervasiveForDriver")]
    int NID_377126CD() => SecureUpdate(ResetPage, 0x20, true);

    /// <summary>NID_6E11EB97(): secure reset register 0x20 off.</summary>
    [Hle(Nid = 0x6E11EB97, Library = "ScePervasiveForDriver")]
    int NID_6E11EB97() => SecureUpdate(ResetPage, 0x20, false);

    /// <summary>NID_7B0F388B(): secure reset register 0x24 on.</summary>
    [Hle(Nid = 0x7B0F388B, Library = "ScePervasiveForDriver")]
    int NID_7B0F388B() => SecureUpdate(ResetPage, 0x24, true);

    /// <summary>NID_4CCD40E6(): secure reset register 0x24 off.</summary>
    [Hle(Nid = 0x4CCD40E6, Library = "ScePervasiveForDriver")]
    int NID_4CCD40E6() => SecureUpdate(ResetPage, 0x24, false);

    /// <summary>NID_7C285361(): secure reset register 0x28 on.</summary>
    [Hle(Nid = 0x7C285361, Library = "ScePervasiveForDriver")]
    int NID_7C285361() => SecureUpdate(ResetPage, 0x28, true);

    /// <summary>NID_E40BED0F(): secure reset register 0x28 off.</summary>
    [Hle(Nid = 0xE40BED0F, Library = "ScePervasiveForDriver")]
    int NID_E40BED0F() => SecureUpdate(ResetPage, 0x28, false);

    /// <summary>NID_F345FB53(index): secure reset register 0x40 + 4 * index on.</summary>
    [Hle(Nid = 0xF345FB53, Library = "ScePervasiveForDriver")]
    int NID_F345FB53(uint index) => SecureUpdate(ResetPage, Indexed(0x40, index), true);

    /// <summary>NID_EDCA3961(index): secure reset register 0x40 + 4 * index off.</summary>
    [Hle(Nid = 0xEDCA3961, Library = "ScePervasiveForDriver")]
    int NID_EDCA3961(uint index) => SecureUpdate(ResetPage, Indexed(0x40, index), false);

    /// <summary>NID_EBE9C84E(): the codec engine's secure reset register (0x180) on.</summary>
    [Hle(Nid = 0xEBE9C84E, Library = "ScePervasiveForDriver")]
    int NID_EBE9C84E() => SecureUpdate(ResetPage, 0x180, true);

    /// <summary>NID_8CF567AD(): the codec engine's secure reset register (0x180) off.</summary>
    [Hle(Nid = 0x8CF567AD, Library = "ScePervasiveForDriver")]
    int NID_8CF567AD() => SecureUpdate(ResetPage, 0x180, false);

    // ------------------------------------------------------------------ smc 0x113: the secure gate registers

    /// <summary>NID_8EE3AEDF(): secure gate register 0x20 on.</summary>
    [Hle(Nid = 0x8EE3AEDF, Library = "ScePervasiveForDriver")]
    int NID_8EE3AEDF() => SecureUpdate(GatePage, 0x20, true);

    /// <summary>NID_3BF2A9B5(): secure gate register 0x20 off.</summary>
    [Hle(Nid = 0x3BF2A9B5, Library = "ScePervasiveForDriver")]
    int NID_3BF2A9B5() => SecureUpdate(GatePage, 0x20, false);

    /// <summary>NID_7F4AB4AA(): secure gate register 0x24 on.</summary>
    [Hle(Nid = 0x7F4AB4AA, Library = "ScePervasiveForDriver")]
    int NID_7F4AB4AA() => SecureUpdate(GatePage, 0x24, true);

    /// <summary>NID_0EBBE8DE(): secure gate register 0x24 off.</summary>
    [Hle(Nid = 0x0EBBE8DE, Library = "ScePervasiveForDriver")]
    int NID_0EBBE8DE() => SecureUpdate(GatePage, 0x24, false);

    /// <summary>NID_B2EE45C9(): secure gate register 0x28 on.</summary>
    [Hle(Nid = 0xB2EE45C9, Library = "ScePervasiveForDriver")]
    int NID_B2EE45C9() => SecureUpdate(GatePage, 0x28, true);

    /// <summary>NID_39979C55(): secure gate register 0x28 off.</summary>
    [Hle(Nid = 0x39979C55, Library = "ScePervasiveForDriver")]
    int NID_39979C55() => SecureUpdate(GatePage, 0x28, false);

    /// <summary>NID_D34E74C5(index): secure gate register 0x40 + 4 * index on.</summary>
    [Hle(Nid = 0xD34E74C5, Library = "ScePervasiveForDriver")]
    int NID_D34E74C5(uint index) => SecureUpdate(GatePage, Indexed(0x40, index), true);

    /// <summary>NID_701D5355(index): secure gate register 0x40 + 4 * index off.</summary>
    [Hle(Nid = 0x701D5355, Library = "ScePervasiveForDriver")]
    int NID_701D5355(uint index) => SecureUpdate(GatePage, Indexed(0x40, index), false);

    /// <summary>NID_A10583FF(index): secure gate register 0x50 + 4 * index on.</summary>
    [Hle(Nid = 0xA10583FF, Library = "ScePervasiveForDriver")]
    int NID_A10583FF(uint index) => SecureUpdate(GatePage, Indexed(0x50, index), true);

    /// <summary>NID_15237AF8(index): secure gate register 0x50 + 4 * index off.</summary>
    [Hle(Nid = 0x15237AF8, Library = "ScePervasiveForDriver")]
    int NID_15237AF8(uint index) => SecureUpdate(GatePage, Indexed(0x50, index), false);

    // ------------------------------------------------------------------ smc 0x110: the secure devices

    /// <summary>NID_84EAB5BC(): the GPU's secure mode 1.</summary>
    [Hle(Nid = 0x84EAB5BC, Library = "ScePervasiveForDriver")]
    int NID_84EAB5BC() => SecureDeviceMode(0, 1);

    /// <summary>NID_E8656ADF(): the GPU's secure mode 0.</summary>
    [Hle(Nid = 0xE8656ADF, Library = "ScePervasiveForDriver")]
    int NID_E8656ADF() => SecureDeviceMode(0, 0);

    /// <summary>NID_D58E3A30(): the codec engine's secure mode 1.</summary>
    [Hle(Nid = 0xD58E3A30, Library = "ScePervasiveForDriver")]
    int NID_D58E3A30() => SecureDeviceMode(1, 1);

    /// <summary>NID_E0711D42(): the codec engine's secure mode 0.</summary>
    [Hle(Nid = 0xE0711D42, Library = "ScePervasiveForDriver")]
    int NID_E0711D42() => SecureDeviceMode(1, 0);

    /// <summary>NID_B2F232E5(on): device 2's secure mode 1 (on 0) or 2.</summary>
    [Hle(Nid = 0xB2F232E5, Library = "ScePervasiveForDriver")]
    int NID_B2F232E5(uint selector) => SecureDeviceMode(2, selector == 0 ? 1u : 2u);

    /// <summary>NID_A7855D3A(): device 2's secure mode 3.</summary>
    [Hle(Nid = 0xA7855D3A, Library = "ScePervasiveForDriver")]
    int NID_A7855D3A() => SecureDeviceMode(2, 3);

    /// <summary>NID_4712740D(): device 2's secure mode 0.</summary>
    [Hle(Nid = 0x4712740D, Library = "ScePervasiveForDriver")]
    int NID_4712740D() => SecureDeviceMode(2, 0);

    // ------------------------------------------------------------------ smc 0x114: the secure clocks

    /// <summary>NID_C0F7CC39(value): secure clock 0.</summary>
    [Hle(Nid = 0xC0F7CC39, Library = "ScePervasiveForDriver")]
    int NID_C0F7CC39(uint value) => SecureClock(0, value);

    /// <summary>NID_09C35980(): secure clock 1 to 0.</summary>
    [Hle(Nid = 0x09C35980, Library = "ScePervasiveForDriver")]
    int NID_09C35980() => SecureClock(1, 0);

    /// <summary>NID_7D02215D(value): secure clock 2.</summary>
    [Hle(Nid = 0x7D02215D, Library = "ScePervasiveForDriver")]
    int NID_7D02215D(uint value) => SecureClock(2, value);

    /// <summary>NID_ECA675E0(): secure clock 3 to 0.</summary>
    [Hle(Nid = 0xECA675E0, Library = "ScePervasiveForDriver")]
    int NID_ECA675E0() => SecureClock(3, 0);

    // ------------------------------------------------------------------ the ARM clock and the DSI pixel clocks

    /// <summary>NID_E9D95643(domain, ratio): the ARM clock: domain below 16 but not 2, 11 or 15, ratio 8..16 (the
    /// divider word 16 - ratio; domain 0 changes only the divider); else 0x803F0000.</summary>
    [Hle(Nid = 0xE9D95643, Library = "ScePervasiveForDriver")]
    int NID_E9D95643(uint domain, uint ratio)
    {
        if (domain >= 0x10 || domain == 2 || (domain & ~4u) == 0xB || ratio - 8 >= 9)
        {
            return PervasiveInvalidArgument;
        }

        if (domain != 0)
        {
            _armClockDomain = domain;
        }

        _armClockDivider = 0x10 - ratio;
        return 0;
    }

    /// <summary>kscePervasiveDsiSetPixelClock (0x91C80C41)(port, clock): the DSI port's pixel clock from the table (0
    /// stops it); a port above 1 or a clock not in the table 0x803F0000.</summary>
    [Hle(Nid = 0x91C80C41, Library = "ScePervasiveForDriver")]
    int kscePervasiveDsiSetPixelClock(uint port, uint clock)
    {
        if (port >= 2)
        {
            return PervasiveInvalidArgument;
        }

        if (clock != 0 && PixelClockIndex(clock) < 0)
        {
            return PervasiveInvalidArgument;
        }

        _dsiPixelClocks[port] = clock;
        return 0;
    }

    /// <summary>NID_7E813FC5(port, uint *clock): the DSI port's pixel clock (0 off); a port above 1 0x803F0000.</summary>
    [Hle(Nid = 0x7E813FC5, Library = "ScePervasiveForDriver")]
    int NID_7E813FC5(uint port, uint clockOut)
    {
        if (port >= 2)
        {
            return PervasiveInvalidArgument;
        }

        GuestMemory.W32(clockOut, _dsiPixelClocks[port]);
        return 0;
    }

    /// <summary>NID_243D0E78(clock, double *byteClock, double *pixelClock): a table clock's byte and pixel clock (MHz);
    /// not in the table 0x803F0000.</summary>
    [Hle(Nid = 0x243D0E78, Library = "ScePervasiveForDriver")]
    int NID_243D0E78(uint clock, uint byteClockOut, uint pixelClockOut)
    {
        int index = PixelClockIndex(clock);
        if (index < 0)
        {
            return PervasiveInvalidArgument;
        }

        if (byteClockOut != 0)
        {
            GuestMemory.Write(byteClockOut, PixelClocks[index].ByteClock);
        }

        if (pixelClockOut != 0)
        {
            GuestMemory.Write(pixelClockOut, PixelClocks[index].PixelClock);
        }

        return 0;
    }
}
