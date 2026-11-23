using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePervasiveForDriver of the FW 3.74 SceLowio kernel module (lowio.skprx; decompiled, study only): the SoC's
/// "pervasive" register pages (misc 0xE3100000, reset 0xE3101000, clock gate 0xE3102000, base clock 0xE3103000, ...
/// up to 0xE3110000, one 4 KB page each), which the module maps at start and the drivers of the other kernel modules
/// change through its exports. VitaSharp has no SoC behind them, so the pages are kernel memory laid out as the
/// physical ones: every export does the firmware's read-modify-write on its register (under one lock, as the module's
/// spinlock), and the getters hand out the page addresses the drivers then read and write themselves. The pages
/// start zeroed (the values the boot loader leaves in them are not documented). The exports that go through the
/// secure monitor (smc 0x110..0x114) are not here.
/// </summary>
public sealed partial class PervasiveKernel : HleModule
{
    /// <summary>The physical address of the first page (misc).</summary>
    const uint PhysicalBase = 0xE3100000;

    /// <summary>The pages kept, 0xE3100000 .. 0xE3110FFF.</summary>
    const uint RegisterSpan = 0x11000;

    const uint MiscPage = 0xE3100000 - PhysicalBase;
    const uint ResetPage = 0xE3101000 - PhysicalBase;

    /// <summary>The kernel address of the register pages (0 until an export first needs them).</summary>
    [SnapshotMissing] // states of builds before it: the pages were never made
    uint _registers;

    readonly object _registerLock = new();

    /// <summary>The kernel address of a register page by its physical address, made on the first request.</summary>
    internal uint PageAddress(uint physical)
    {
        lock (_registerLock)
        {
            if (_registers == 0)
            {
                using (ProcessScope.Enter(KernelSpace.KernelPid))
                {
                    _registers = Mem.Alloc(RegisterSpan, "kernel:ScePervasive", MemoryRegion.Kernel);
                }

                GuestMemory.Fill(_registers, 0, RegisterSpan);
                PowerOnMiscWords(_registers);
            }

            return _registers + (physical - PhysicalBase);
        }
    }

    /// <summary>The value of the register at <paramref name="offset"/> bytes into the pages (0 = 0xE3100000).</summary>
    internal uint Register(uint offset) => GuestMemory.R32(PageAddress(PhysicalBase) + offset);

    /// <summary>
    /// Sets (<paramref name="set"/>) or clears the <paramref name="mask"/> bits of the register at
    /// <paramref name="offset"/> bytes into the pages; 1 when any of them was set before, else 0 (the firmware's
    /// result). A register past the pages (an index the firmware does not check) is left alone with a warning.
    /// </summary>
    int UpdateBits(uint offset, uint mask, bool set)
    {
        if (offset > RegisterSpan - 4)
        {
            Log.Warn($"ScePervasive: register 0x{PhysicalBase + offset:X8} is outside the pervasive pages; ignored");
            return 0;
        }

        uint address = PageAddress(PhysicalBase) + offset;
        lock (_registerLock)
        {
            uint before = GuestMemory.R32(address);
            uint after = set ? before | mask : before & ~mask;
            GuestMemory.W32(address, after);
            return (before & mask) != 0 ? 1 : 0;
        }
    }

    /// <summary>Puts a device in reset: sets its bits in the reset page.</summary>
    int ResetEnable(uint register, uint mask) => UpdateBits(ResetPage + register, mask, true);

    /// <summary>Takes a device out of reset: clears its bits in the reset page.</summary>
    int ResetDisable(uint register, uint mask) => UpdateBits(ResetPage + register, mask, false);

    /// <summary>The register of entry <paramref name="index"/> of a 4-byte array at <paramref name="register"/>,
    /// computed without a bound as the firmware does.</summary>
    static uint Indexed(uint register, uint index) => unchecked(register + index * 4);

    // ------------------------------------------------------------------ page getters

    /// <summary>NID_81A155F1(): the address of the misc page (0xE3100000).</summary>
    [Hle(Nid = 0x81A155F1, Library = "ScePervasiveForDriver", ReturnsValue = true)]
    uint NID_81A155F1() => PageAddress(PhysicalBase + MiscPage);

    /// <summary>NID_47708C03(): the address of the 0xE3104000 page.</summary>
    [Hle(Nid = 0x47708C03, Library = "ScePervasiveForDriver", ReturnsValue = true)]
    uint NID_47708C03() => PageAddress(0xE3104000);

    /// <summary>NID_04D145C7(): the address of the 0xE3106000 page.</summary>
    [Hle(Nid = 0x04D145C7, Library = "ScePervasiveForDriver", ReturnsValue = true)]
    uint NID_04D145C7() => PageAddress(0xE3106000);

    /// <summary>NID_2A563E35(): the address of the 0xE3110000 page (the removable memory card's).</summary>
    [Hle(Nid = 0x2A563E35, Library = "ScePervasiveForDriver", ReturnsValue = true)]
    uint NID_2A563E35() => PageAddress(0xE3110000);

    // ------------------------------------------------------------------ reset page (0xE3101000)

    /// <summary>NID_3E79D3D3(): puts the device of reset register 0x10 in reset (bit 0); 1 if it already was.</summary>
    [Hle(Nid = 0x3E79D3D3, Library = "ScePervasiveForDriver")]
    int NID_3E79D3D3() => ResetEnable(0x10, 1);

    /// <summary>NID_8A85E36B(): takes the device of reset register 0x10 out of reset (bit 0); 1 if it was in.</summary>
    [Hle(Nid = 0x8A85E36B, Library = "ScePervasiveForDriver")]
    int NID_8A85E36B() => ResetDisable(0x10, 1);

    /// <summary>NID_28731EC5(): reset register 0x30, bit 0 on.</summary>
    [Hle(Nid = 0x28731EC5, Library = "ScePervasiveForDriver")]
    int NID_28731EC5() => ResetEnable(0x30, 1);

    /// <summary>NID_A7E64C6F(): reset register 0x30, bit 0 off.</summary>
    [Hle(Nid = 0xA7E64C6F, Library = "ScePervasiveForDriver")]
    int NID_A7E64C6F() => ResetDisable(0x30, 1);

    /// <summary>NID_31C0A98B(): reset register 0x34, bit 0 on.</summary>
    [Hle(Nid = 0x31C0A98B, Library = "ScePervasiveForDriver")]
    int NID_31C0A98B() => ResetEnable(0x34, 1);

    /// <summary>NID_E2D8F6C3(): reset register 0x34, bit 0 off.</summary>
    [Hle(Nid = 0xE2D8F6C3, Library = "ScePervasiveForDriver")]
    int NID_E2D8F6C3() => ResetDisable(0x34, 1);

    /// <summary>NID_FF4B3CA6(index): reset register 0x50 + 4 * index, bit 0 on.</summary>
    [Hle(Nid = 0xFF4B3CA6, Library = "ScePervasiveForDriver")]
    int NID_FF4B3CA6(uint index) => ResetEnable(Indexed(0x50, index), 1);

    /// <summary>NID_AC475961(index): reset register 0x50 + 4 * index, bit 0 off.</summary>
    [Hle(Nid = 0xAC475961, Library = "ScePervasiveForDriver")]
    int NID_AC475961(uint index) => ResetDisable(Indexed(0x50, index), 1);

    /// <summary>NID_66BF2885(index): reset register 0x70 + 4 * index, bit 0 on.</summary>
    [Hle(Nid = 0x66BF2885, Library = "ScePervasiveForDriver")]
    int NID_66BF2885(uint index) => ResetEnable(Indexed(0x70, index), 1);

    /// <summary>NID_D3B59C33(index): reset register 0x70 + 4 * index, bit 0 off.</summary>
    [Hle(Nid = 0xD3B59C33, Library = "ScePervasiveForDriver")]
    int NID_D3B59C33(uint index) => ResetDisable(Indexed(0x70, index), 1);

    /// <summary>kscePervasiveDsiResetEnable(port, mask): the DSI port's reset register (0x80 + 4 * port), the
    /// <paramref name="mask"/> bits on.</summary>
    [Hle(Nid = 0xE3FC1C8D, Library = "ScePervasiveForDriver")]
    int kscePervasiveDsiResetEnable(uint port, uint mask) => ResetEnable(Indexed(0x80, port), mask);

    /// <summary>kscePervasiveDsiResetDisable(port, mask): the DSI port's reset register, the mask bits off.</summary>
    [Hle(Nid = 0xFFB43AC2, Library = "ScePervasiveForDriver")]
    int kscePervasiveDsiResetDisable(uint port, uint mask) => ResetDisable(Indexed(0x80, port), mask);

    /// <summary>NID_B68254AD(): reset register 0x88, bit 0 on.</summary>
    [Hle(Nid = 0xB68254AD, Library = "ScePervasiveForDriver")]
    int NID_B68254AD() => ResetEnable(0x88, 1);

    /// <summary>NID_E92E28FF(): reset register 0x88, bit 0 off.</summary>
    [Hle(Nid = 0xE92E28FF, Library = "ScePervasiveForDriver")]
    int NID_E92E28FF() => ResetDisable(0x88, 1);

    /// <summary>NID_7AE2F8E8(): reset register 0x8C, bit 0 on.</summary>
    [Hle(Nid = 0x7AE2F8E8, Library = "ScePervasiveForDriver")]
    int NID_7AE2F8E8() => ResetEnable(0x8C, 1);

    /// <summary>NID_17109C28(): reset register 0x8C, bit 0 off.</summary>
    [Hle(Nid = 0x17109C28, Library = "ScePervasiveForDriver")]
    int NID_17109C28() => ResetDisable(0x8C, 1);

    /// <summary>NID_4AF7A01E(port, mask): the USB port's reset register (0x90 + 4 * port), the mask bits on.</summary>
    [Hle(Nid = 0x4AF7A01E, Library = "ScePervasiveForDriver")]
    int NID_4AF7A01E(uint port, uint mask) => ResetEnable(Indexed(0x90, port), mask);

    /// <summary>NID_13CC07C9(port, mask): the USB port's reset register, the mask bits off.</summary>
    [Hle(Nid = 0x13CC07C9, Library = "ScePervasiveForDriver")]
    int NID_13CC07C9(uint port, uint mask) => ResetDisable(Indexed(0x90, port), mask);

    /// <summary>NID_3D8A8712(index): the SD host's reset register (0xA0 + 4 * index), bit 0 on.</summary>
    [Hle(Nid = 0x3D8A8712, Library = "ScePervasiveForDriver")]
    int NID_3D8A8712(uint index) => ResetEnable(Indexed(0xA0, index), 1);

    /// <summary>NID_D326E15F(index): the SD host's reset register, bit 0 off.</summary>
    [Hle(Nid = 0xD326E15F, Library = "ScePervasiveForDriver")]
    int NID_D326E15F(uint index) => ResetDisable(Indexed(0xA0, index), 1);

    /// <summary>kscePervasiveMsifResetEnable(): the memory card interface's reset register (0xB0), bit 0 on.</summary>
    [Hle(Nid = 0xA3569FF1, Library = "ScePervasiveForDriver")]
    int kscePervasiveMsifResetEnable() => ResetEnable(0xB0, 1);

    /// <summary>kscePervasiveMsifResetDisable(): the memory card interface's reset register, bit 0 off.</summary>
    [Hle(Nid = 0xCB0F15CD, Library = "ScePervasiveForDriver")]
    int kscePervasiveMsifResetDisable() => ResetDisable(0xB0, 1);

    /// <summary>NID_146108D4(index): the audio interface's reset register (0xC0 + 4 * index), bit 0 on.</summary>
    [Hle(Nid = 0x146108D4, Library = "ScePervasiveForDriver")]
    int NID_146108D4(uint index) => ResetEnable(Indexed(0xC0, index), 1);

    /// <summary>NID_EEE340ED(index): the audio interface's reset register, bit 0 off.</summary>
    [Hle(Nid = 0xEEE340ED, Library = "ScePervasiveForDriver")]
    int NID_EEE340ED(uint index) => ResetDisable(Indexed(0xC0, index), 1);

    /// <summary>NID_821AE6C7(index): reset register 0xE0 + 4 * index, bit 0 on.</summary>
    [Hle(Nid = 0x821AE6C7, Library = "ScePervasiveForDriver")]
    int NID_821AE6C7(uint index) => ResetEnable(Indexed(0xE0, index), 1);

    /// <summary>NID_5BAFA0DC(index): reset register 0xE0 + 4 * index, bit 0 off.</summary>
    [Hle(Nid = 0x5BAFA0DC, Library = "ScePervasiveForDriver")]
    int NID_5BAFA0DC(uint index) => ResetDisable(Indexed(0xE0, index), 1);

    /// <summary>NID_B8AF1249(): reset register 0xF0, bit 0 on.</summary>
    [Hle(Nid = 0xB8AF1249, Library = "ScePervasiveForDriver")]
    int NID_B8AF1249() => ResetEnable(0xF0, 1);

    /// <summary>NID_6A2FB697(): reset register 0xF0, bit 0 off.</summary>
    [Hle(Nid = 0x6A2FB697, Library = "ScePervasiveForDriver")]
    int NID_6A2FB697() => ResetDisable(0xF0, 1);

    /// <summary>kscePervasiveGpioResetEnable(): the GPIO block's reset register (0x100), bit 0 on.</summary>
    [Hle(Nid = 0x731A097D, Library = "ScePervasiveForDriver")]
    int kscePervasiveGpioResetEnable() => ResetEnable(0x100, 1);

    /// <summary>kscePervasiveGpioResetDisable(): the GPIO block's reset register, bit 0 off.</summary>
    [Hle(Nid = 0x78C34032, Library = "ScePervasiveForDriver")]
    int kscePervasiveGpioResetDisable() => ResetDisable(0x100, 1);

    /// <summary>kscePervasiveSpiResetEnable(port): the SPI port's reset register (0x104 + 4 * port), bit 0 on.</summary>
    [Hle(Nid = 0x7B16F900, Library = "ScePervasiveForDriver")]
    int kscePervasiveSpiResetEnable(uint port) => ResetEnable(Indexed(0x104, port), 1);

    /// <summary>kscePervasiveSpiResetDisable(port): the SPI port's reset register, bit 0 off.</summary>
    [Hle(Nid = 0xE4B145AE, Library = "ScePervasiveForDriver")]
    int kscePervasiveSpiResetDisable(uint port) => ResetDisable(Indexed(0x104, port), 1);

    /// <summary>NID_41F9E1C3(index): reset register 0x110 + 4 * index, bit 0 on.</summary>
    [Hle(Nid = 0x41F9E1C3, Library = "ScePervasiveForDriver")]
    int NID_41F9E1C3(uint index) => ResetEnable(Indexed(0x110, index), 1);

    /// <summary>NID_86BEA9A4(index): reset register 0x110 + 4 * index, bit 0 off.</summary>
    [Hle(Nid = 0x86BEA9A4, Library = "ScePervasiveForDriver")]
    int NID_86BEA9A4(uint index) => ResetDisable(Indexed(0x110, index), 1);

    /// <summary>kscePervasiveUartResetEnable(port): the UART port's reset register (0x120 + 4 * port), bit 0 on.</summary>
    [Hle(Nid = 0x788B6C61, Library = "ScePervasiveForDriver")]
    int kscePervasiveUartResetEnable(uint port) => ResetEnable(Indexed(0x120, port), 1);

    /// <summary>kscePervasiveUartResetDisable(port): the UART port's reset register, bit 0 off.</summary>
    [Hle(Nid = 0xA7CE7DCC, Library = "ScePervasiveForDriver")]
    int kscePervasiveUartResetDisable(uint port) => ResetDisable(Indexed(0x120, port), 1);
}
