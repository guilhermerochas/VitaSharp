using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceSysconForDriver over the emulated chip (FW 3.74 syscon.skprx, read in the module, study only). Every export is
// what the module does: it builds the command frame (command, length, data), sends it through the packet path
// (ksceSysconCmdExec) and reads the reply, or answers from what the module keeps (the flags it derives from the
// chip's status word, the versions read at boot). The chip is the system's one (VitaSystem.Syscon).
public sealed partial class SysconKernel
{
    /// <summary>SceSyscon's errors: a bad argument, a NULL pointer, a packet that is not queued, a frame length
    /// out of range, a reply of the wrong length.</summary>
    internal const int SYSCON_ERR_INVALID = unchecked((int)0x80250001), SYSCON_ERR_NULL = unchecked((int)0x80250002),
        SYSCON_ERR_NOT_QUEUED = unchecked((int)0x80250003), SYSCON_ERR_LENGTH = unchecked((int)0x80250006),
        SYSCON_ERR_REPLY_LENGTH = unchecked((int)0x80250100), SYSCON_ERR_RESULT = unchecked((int)0x80250200);

    /// <summary>The packet word +4 flags: the reply is in (bit 19); the caller's long transmit frame (+0x50, bit 11)
    /// and long receive buffer (+0x54, size +0x58, bit 10); the caller computed the checksum (bit 8).</summary>
    const uint PacketDone = 0x80000, FlagLongTx = 0x800, FlagLongRx = 0x400, FlagOwnChecksum = 0x100;

    /// <summary>The hardware info word bit that says the extended hardware information exists (bit 5).</summary>
    const uint HardwareInfo2Present = 0x20;

    /// <summary>The module's words set by its own setters (ksceSysconSetLedMask-like pairs 0x884/0x885): the masks
    /// start at 3 and 7 (module_start).</summary>
    [SnapshotMissing(3u)]
    uint _wakeupMask884 = 3;

    [SnapshotMissing(7u)]
    uint _wakeupMask885 = 7;

    /// <summary>The jig port is open (ksceSysconJigOpenPort): -1 closed.</summary>
    [SnapshotMissing(-1)]
    int _jigPort = -1;

    SysconChip Chip => Emu.Vita.Syscon;

    /// <summary>The hardware info word of the boot parameters (SceSysroot), which the module reads at start.</summary>
    static uint BootHardwareInfo => ConsoleIdentity.HardwareInfo;

    SystemServicesVsh Services => Emu.Hle.Modules.OfType<SystemServicesVsh>().First();

    /// <summary>The reply's result: 0, or 0x80250200 with the result byte when its low 6 bits are set.</summary>
    static int ReplyResult(ReadOnlySpan<byte> rx)
    {
        return (rx[3] & 0x3F) != 0 ? SYSCON_ERR_RESULT | rx[3] : 0;
    }

    /// <summary>Sends command <paramref name="command"/> with <paramref name="data"/> and reads the reply frame into
    /// <paramref name="rx"/> (32 bytes or more). A frame of the packet itself carries up to 0x1C data bytes; a long
    /// one (<paramref name="longFrame"/>, the packet's +0x50 frame) up to 0xFC.</summary>
    int Exec(ushort command, ReadOnlySpan<byte> data, Span<byte> rx, bool longFrame = false)
    {
        if (data.Length > (longFrame ? 0xFC : 0x1C))
        {
            return SYSCON_ERR_LENGTH;
        }

        Span<byte> tx = stackalloc byte[0x100];
        tx[0] = (byte)command;
        tx[1] = (byte)(command >> 8);
        tx[2] = (byte)(data.Length + 1);
        data.CopyTo(tx[3..]);
        tx[3 + data.Length] = SysconChip.Checksum(tx[..(3 + data.Length)]);
        rx.Clear();
        Chip.Execute(tx[..(4 + data.Length)], rx);
        return ReplyResult(rx);
    }

    /// <summary>A command another module sends through the packet path (ScePower's charging commands): its result.
    /// </summary>
    internal int SendToChip(ushort command, ReadOnlySpan<byte> data)
    {
        Span<byte> rx = stackalloc byte[0x20];
        return Exec(command, data, rx);
    }

    /// <summary>A command without data whose reply the caller reads.</summary>
    int Exec(ushort command, Span<byte> rx) => Exec(command, ReadOnlySpan<byte>.Empty, rx);

    /// <summary>The module's "send a value" helper: <paramref name="length"/> - 1 bytes of <paramref name="value"/>
    /// (little-endian) as the frame's data.</summary>
    int Send(ushort command, uint value, int length)
    {
        Span<byte> data = stackalloc byte[4];
        data[0] = (byte)value;
        data[1] = (byte)(value >> 8);
        data[2] = (byte)(value >> 16);
        data[3] = (byte)(value >> 24);
        Span<byte> rx = stackalloc byte[0x20];
        return Exec(command, data[..(length - 1)], rx);
    }

    /// <summary>The module's "read a word" helper: a command without data whose reply has 1 to 4 bytes, stored as a
    /// word at <paramref name="output"/> (0x80250002 for NULL, 0x80250100 for another reply length).</summary>
    int ReadWord(uint output, ushort command)
    {
        if (output == 0)
        {
            return SYSCON_ERR_NULL;
        }

        int result = ReadWord(command, out uint value);
        if (result == 0)
        {
            GuestMemory.W32(output, value);
        }

        return result;
    }

    int ReadWord(ushort command, out uint value)
    {
        value = 0;
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, rx);
        if (result < 0)
        {
            return result;
        }

        int length = rx[2];
        if ((uint)(length - 3) >= 4)
        {
            return SYSCON_ERR_REPLY_LENGTH;
        }

        for (int i = 0; i < length - 2; i++)
        {
            value |= (uint)rx[4 + i] << (8 * i);
        }

        return 0;
    }

    /// <summary>A command without data whose first reply byte goes to <paramref name="output"/> when it is not NULL
    /// (the module's touch panel and battery version reads).</summary>
    int ReadByte(uint output, ushort command)
    {
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, rx);
        if (result >= 0 && output != 0)
        {
            GuestMemory.W32(output, rx[4]);
        }

        return result;
    }

    /// <summary>A command without data whose first <paramref name="words"/> reply words go to
    /// <paramref name="output"/>.</summary>
    int ReadWords(uint output, ushort command, int words)
    {
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, rx);
        if (result >= 0)
        {
            rx.Slice(4, words * 4).CopyTo(GuestMemory.Span(output, words * 4));
            result = 0;
        }

        return result;
    }

    /// <summary>A read of <paramref name="size"/> bytes at <paramref name="offset"/> of a chip area (data {offset
    /// low, offset high, size}): 1..0x18 bytes inside <paramref name="end"/>, else 0x80250001.</summary>
    int ReadArea(ushort command, uint offset, uint buffer, uint size, uint end)
    {
        if (size - 1 >= 0x18 || size + offset > end)
        {
            return SYSCON_ERR_INVALID;
        }

        ReadOnlySpan<byte> data = [(byte)offset, (byte)(offset >> 8), (byte)size];
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, data, rx);
        if (result >= 0)
        {
            rx.Slice(4, (int)size).CopyTo(GuestMemory.Span(buffer, (int)size));
            result = 0;
        }

        return result;
    }

    /// <summary>A write of <paramref name="size"/> bytes at <paramref name="offset"/> of a chip area (data {offset
    /// low, offset high, size, bytes}).</summary>
    int WriteArea(ushort command, uint offset, uint buffer, uint size, uint end)
    {
        if (size - 1 >= 0x18 || size + offset > end)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> data = stackalloc byte[3 + (int)size];
        data[0] = (byte)offset;
        data[1] = (byte)(offset >> 8);
        data[2] = (byte)size;
        GuestMemory.ReadSpan(buffer, (int)size).CopyTo(data[3..]);
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, data, rx);
        return Math.Min(result, 0);
    }

    /// <summary>A read of the touch panel/battery firmware areas: {offset (2 bytes), size} with size 1..10, the
    /// reply's first byte 0 and the bytes after it.</summary>
    int ReadFirmwareArea(ushort command, uint offset, uint buffer, uint size)
    {
        if (size - 1 >= 10)
        {
            return SYSCON_ERR_INVALID;
        }

        ReadOnlySpan<byte> data = [(byte)offset, (byte)(offset >> 8), (byte)size];
        Span<byte> rx = stackalloc byte[0x20];
        int result = Exec(command, data, rx);
        if (result < 0)
        {
            return result;
        }

        if (rx[4] != 0)
        {
            return SYSCON_ERR_INVALID;
        }

        rx.Slice(5, (int)size).CopyTo(GuestMemory.Span(buffer, (int)size));
        return 0;
    }

    /// <summary>A write of the touch panel/battery firmware areas: {offset (2 bytes), size, 10 bytes (the given
    /// ones, zero after)} with size 1..10.</summary>
    int WriteFirmwareArea(ushort command, uint offset, uint buffer, uint size)
    {
        if (size - 1 >= 10)
        {
            return SYSCON_ERR_INVALID;
        }

        Span<byte> data = stackalloc byte[13];
        data.Clear();
        data[0] = (byte)offset;
        data[1] = (byte)(offset >> 8);
        data[2] = (byte)size;
        GuestMemory.ReadSpan(buffer, (int)size).CopyTo(data[3..]);
        Span<byte> rx = stackalloc byte[0x20];
        return Math.Min(Exec(command, data, rx), 0);
    }

    /// <summary>A command whose data is the given bytes and whose result is all the caller gets.</summary>
    int SendBytes(ushort command, ReadOnlySpan<byte> data)
    {
        Span<byte> rx = stackalloc byte[0x20];
        return Math.Min(Exec(command, data, rx), 0);
    }

    /// <summary>A flag the module keeps from one bit of the chip's status word.</summary>
    int StatusFlag(int bit) => Chip.StatusBit(bit) ? 1 : 0;

    // ------------------------------------------------------------------ the packet path

    /// <summary>ksceSysconCmdExec(SceSysconPacket *packet, uint flags): sends the packet's frame (+0x10, or the long
    /// one at +0x50), waits for the reply (+0x30, or the long buffer at +0x54 of size +0x58) and returns its result
    /// (ksceSysconCmdSync).</summary>
    [Hle(Nid = 0x9ADDCA4A, Library = "SceSysconForDriver")]
    int ksceSysconCmdExec(uint packet, uint flags)
    {
        int queued = RunPacket(packet, flags);
        return queued < 0 ? queued : ksceSysconCmdSync(packet, 0);
    }

    /// <summary>ksceSysconCmdExecAsync(packet, flags, callback, argument): the same exchange; the callback(packet,
    /// argument) runs once the reply is in (the module's interrupt handler calls it). 0.</summary>
    [Hle(Nid = 0xC2224E82, Library = "SceSysconForDriver")]
    int ksceSysconCmdExecAsync(uint packet, uint flags, uint callback, uint argument)
    {
        GuestMemory.W32(packet + 0x60, callback);
        GuestMemory.W32(packet + 0x64, argument);
        int queued = RunPacket(packet, flags);
        if (queued < 0)
        {
            return queued;
        }

        if (callback != 0 && GuestThread.Current is { } thread)
        {
            thread.CallGuest(callback, packet, argument);
        }

        return 0;
    }

    /// <summary>The frame checks of the packet path, then the exchange with the chip.</summary>
    int RunPacket(uint packet, uint flags)
    {
        uint txAddress = packet + 0x10;
        if ((flags & FlagLongTx) != 0)
        {
            txAddress = GuestMemory.R32(packet + 0x50);
            if (txAddress == 0)
            {
                return SYSCON_ERR_NULL;
            }

            GuestMemory.Write(packet + 0x10, GuestMemory.Read<ushort>(txAddress));
            GuestMemory.Write(packet + 0x12, GuestMemory.Read<byte>(txAddress + 2));
            if ((byte)(GuestMemory.Read<byte>(txAddress + 2) - 1) > 0xFC)
            {
                return SYSCON_ERR_LENGTH;
            }
        }
        else
        {
            GuestMemory.W32(packet + 0x50, 0);
            if ((uint)(GuestMemory.Read<byte>(packet + 0x12) - 1) > 0x1C)
            {
                return SYSCON_ERR_LENGTH;
            }
        }

        uint rxAddress = packet + 0x30;
        int rxSize = 0x20;
        if ((flags & FlagLongRx) != 0)
        {
            rxAddress = GuestMemory.R32(packet + 0x54);
            if (rxAddress == 0)
            {
                return SYSCON_ERR_NULL;
            }

            rxSize = (int)GuestMemory.R32(packet + 0x58);
            if (rxSize < 0x21)
            {
                return SYSCON_ERR_INVALID;
            }
        }
        else
        {
            GuestMemory.W32(packet + 0x54, 0);
            GuestMemory.W32(packet + 0x58, 0);
        }

        int txLength = GuestMemory.Read<byte>(txAddress + 2);
        Span<byte> tx = GuestMemory.Span(txAddress, txLength + 3);
        if ((flags & FlagOwnChecksum) == 0)
        {
            tx[txLength + 2] = SysconChip.Checksum(tx[..(txLength + 2)]);
        }

        Span<byte> rx = GuestMemory.Span(rxAddress, rxSize);
        rx.Fill(0xFF);
        Chip.Execute(tx, rx);
        if ((flags & FlagLongRx) != 0)
        {
            // the module copies the reply's first word into the packet's own buffer too
            GuestMemory.W32(packet + 0x30, GuestMemory.R32(rxAddress));
        }

        GuestMemory.W32(packet, 0);
        GuestMemory.W32(packet + 4, (flags & 0xFFFF) | 0x10000 | PacketDone);
        GuestMemory.W32(packet + 8, uint.MaxValue);
        return 0;
    }

    /// <summary>ksceSysconCmdSync(packet, mode): mode 0 waits for the packet's reply and returns its result
    /// (0x80250101 for a transfer error, 0x80250105 an overflow of the long buffer, 0x80250200 | result); mode 1
    /// polls: 1 while it is pending (packet NULL: any packet pending), else the result.</summary>
    [Hle(Nid = 0x6E517D22, Library = "SceSysconForDriver")]
    int ksceSysconCmdSync(uint packet, int mode)
    {
        if (mode is not (0 or 1))
        {
            return unchecked((int)0x80250000);
        }

        if (packet == 0)
        {
            // every packet is answered when it is sent: none is pending
            return mode == 1 ? 0 : SYSCON_ERR_NULL;
        }

        uint state = GuestMemory.R32(packet + 4);
        if ((state & PacketDone) == 0)
        {
            return 1;
        }

        if ((state & 0xB00000) != 0)
        {
            return unchecked((int)0x80250101);
        }

        if ((state & 0x2000000) != 0)
        {
            return unchecked((int)0x80250105);
        }

        uint rxAddress = (state & FlagLongRx) != 0 ? GuestMemory.R32(packet + 0x54) : packet + 0x30;
        byte result = GuestMemory.Read<byte>(rxAddress + 3);
        return (result & 0x3F) == 0 ? 0 : SYSCON_ERR_RESULT | result;
    }

    /// <summary>NID_C9DB5BFF(packet): takes a queued packet off its queue; every packet here is answered when sent,
    /// so none is queued: 0x80250003.</summary>
    [Hle(Nid = 0xC9DB5BFF, Library = "SceSysconForDriver")]
    int NID_C9DB5BFF(uint packet) => SYSCON_ERR_NOT_QUEUED;

    // ------------------------------------------------------------------ what the module keeps

    /// <summary>ksceSysconGetBaryonVersion(): the syscon firmware version.</summary>
    [Hle(Nid = 0xFF86F4C5, Library = "SceSysconForDriver", ReturnsValue = true)]
    uint ksceSysconGetBaryonVersion() => Chip.BaryonVersion;

    /// <summary>ksceSysconGetBaryonTimestamp(): the syscon firmware's build time (64 bits).</summary>
    [Hle(Nid = 0x4D588A0A, Library = "SceSysconForDriver", ReturnsValue = true)]
    void ksceSysconGetBaryonTimestamp(HleContext context) => context.Return(Chip.BaryonTimestamp);

    /// <summary>ksceSysconGetHardwareInfo(): the hardware info word the module reads from the boot parameters
    /// (SceSysroot).</summary>
    [Hle(Nid = 0xCBD6D8BC, Library = "SceSysconForDriver", ReturnsValue = true)]
    uint ksceSysconGetHardwareInfo() => BootHardwareInfo;

    /// <summary>ksceSysconGetHardwareInfo2(uint info[4]): the extended hardware information, only on units whose
    /// hardware info has bit 5; else 0x80250003.</summary>
    [Hle(Nid = 0x965C68C3, Library = "SceSysconForDriver")]
    int ksceSysconGetHardwareInfo2(uint info)
    {
        if ((BootHardwareInfo & HardwareInfo2Present) == 0)
        {
            return SYSCON_ERR_NOT_QUEUED;
        }

        GuestMemory.Fill(info, 0, 0x10);
        return 0;
    }

    /// <summary>NID_EBE3262C(): the status byte of the chip's last reply.</summary>
    [Hle(Nid = 0xEBE3262C, Library = "SceSysconForDriver", ReturnsValue = true)]
    int NID_EBE3262C() => Chip.StatusByte;

    /// <summary>NID_B7BCC638(): the chip's status word (the event bits).</summary>
    [Hle(Nid = 0xB7BCC638, Library = "SceSysconForDriver", ReturnsValue = true)]
    uint NID_B7BCC638() => Chip.StatusWord;

    /// <summary>NID_9DA2A5AB(): the AC adapter is plugged (status byte bit 3; ScePower's "power online").</summary>
    [Hle(Nid = 0x9DA2A5AB, Library = "SceSysconForDriver")]
    int NID_9DA2A5AB() => Chip.AcAdapter ? 1 : 0;

    /// <summary>ksceSysconIsDownLoaderMode(): status byte bit 6.</summary>
    [Hle(Nid = 0x9ADD60D2, Library = "SceSysconForDriver")]
    int ksceSysconIsDownLoaderMode() => (Chip.StatusByte & SysconChip.StatusDownloaderMode) != 0 ? 1 : 0;

    /// <summary>NID_2E4A2198(): the mask NID_3FDD29D6 keeps (3 at start).</summary>
    [Hle(Nid = 0x2E4A2198, Library = "SceSysconForDriver", ReturnsValue = true)]
    uint NID_2E4A2198() => _wakeupMask884;

    /// <summary>NID_1E08A40F(): the mask NID_79E6DD8B keeps (7 at start).</summary>
    [Hle(Nid = 0x1E08A40F, Library = "SceSysconForDriver", ReturnsValue = true)]
    uint NID_1E08A40F() => _wakeupMask885;

    /// <summary>NID_3FDD29D6(bits, set): command 0x884 {bits | 0x80 to set}; the module's mask follows.</summary>
    [Hle(Nid = 0x3FDD29D6, Library = "SceSysconForDriver")]
    int NID_3FDD29D6(uint bits, int set) => SetMask(0x884, bits, set, ref _wakeupMask884);

    /// <summary>NID_79E6DD8B(bits, set): command 0x885 {bits | 0x80 to set}; the module's mask follows.</summary>
    [Hle(Nid = 0x79E6DD8B, Library = "SceSysconForDriver")]
    int NID_79E6DD8B(uint bits, int set) => SetMask(0x885, bits, set, ref _wakeupMask885);

    int SetMask(ushort command, uint bits, int set, ref uint mask)
    {
        int result = Send(command, set != 0 ? bits | 0x80 : bits, 2);
        if (result >= 0)
        {
            mask = set != 0 ? mask | bits : mask & ~bits;
        }

        return result;
    }

    /// <summary>ksceSysconGetMicroUsbInfo(uint *info): what the chip's micro USB event last reported (nothing
    /// plugged: 0). 0.</summary>
    [Hle(Nid = 0xD6F6D472, Library = "SceSysconForDriver")]
    int ksceSysconGetMicroUsbInfo(uint info)
    {
        if (info != 0)
        {
            GuestMemory.W32(info, Chip.MicroUsbInfo);
        }

        return 0;
    }

    /// <summary>ksceSysconGetMultiCnInfo(uint *info): the multi connector's information (nothing plugged: 0x4FF00);
    /// on boards before 0x40 the device byte is translated (0x65 → 4, 0x55 → 5, 0x51 → 2, 0x60 → 0, else 0xFF), and
    /// a PS TV has none. 0.</summary>
    [Hle(Nid = 0x1503D6A0, Library = "SceSysconForDriver")]
    int ksceSysconGetMultiCnInfo(uint info)
    {
        uint value = Chip.MultiCnInfo;
        if ((BootHardwareInfo & 0xFF0000) < 0x400000)
        {
            uint device = (value & 0xFFFF) >> 8;
            uint translated = device switch
            {
                0x65 => 4,
                0x55 => 5,
                0x51 => 2,
                0x60 => 0,
                _ => 0xFF,
            };
            value = (value & 0xFFFF00FF) | translated << 8;
        }

        if (info != 0)
        {
            if (Services.ksceSblAimgrIsGenuineDolce() != 0)
            {
                value &= 0xFFFF00FF;
            }

            GuestMemory.W32(info, value);
        }

        return 0;
    }

    // ------------------------------------------------------------------ the flags of the status word

    /// <summary>NID_C0F215B7(): status bit 0.</summary>
    [Hle(Nid = 0xC0F215B7, Library = "SceSysconForDriver")]
    int NID_C0F215B7() => StatusFlag(0);

    /// <summary>NID_C3504ADE(): status bit 1.</summary>
    [Hle(Nid = 0xC3504ADE, Library = "SceSysconForDriver")]
    int NID_C3504ADE() => StatusFlag(1);

    /// <summary>NID_B832B72C(): status bit 2.</summary>
    [Hle(Nid = 0xB832B72C, Library = "SceSysconForDriver")]
    int NID_B832B72C() => StatusFlag(2);

    /// <summary>NID_86BAAF7D(): status bit 3 (SceRtc reads it with the alarm).</summary>
    [Hle(Nid = 0x86BAAF7D, Library = "SceSysconForDriver")]
    int NID_86BAAF7D() => StatusFlag(3);

    /// <summary>NID_A57B5433(): status bit 4.</summary>
    [Hle(Nid = 0xA57B5433, Library = "SceSysconForDriver")]
    int NID_A57B5433() => StatusFlag(4);

    /// <summary>NID_ACC7F71E(): status bit 5 (the multi connector; never negative).</summary>
    [Hle(Nid = 0xACC7F71E, Library = "SceSysconForDriver")]
    int NID_ACC7F71E() => StatusFlag(5);

    /// <summary>NID_769F9AC4(): 1 on a PS TV, else status bit 6.</summary>
    [Hle(Nid = 0x769F9AC4, Library = "SceSysconForDriver")]
    int NID_769F9AC4() => Services.ksceSblAimgrIsDolce() != 0 ? 1 : StatusFlag(6);

    /// <summary>NID_4BC63A40(): status bit 8.</summary>
    [Hle(Nid = 0x4BC63A40, Library = "SceSysconForDriver")]
    int NID_4BC63A40() => StatusFlag(8);

    /// <summary>NID_99A254A9(): status bit 9.</summary>
    [Hle(Nid = 0x99A254A9, Library = "SceSysconForDriver")]
    int NID_99A254A9() => StatusFlag(9);

    /// <summary>NID_9A4F4B7C(): status bit 10.</summary>
    [Hle(Nid = 0x9A4F4B7C, Library = "SceSysconForDriver")]
    int NID_9A4F4B7C() => StatusFlag(10);

    /// <summary>NID_92D2C6A4(): status bit 14 (the micro USB).</summary>
    [Hle(Nid = 0x92D2C6A4, Library = "SceSysconForDriver")]
    int NID_92D2C6A4() => StatusFlag(14);

    /// <summary>NID_B9EA2FA8(): status bit 15.</summary>
    [Hle(Nid = 0xB9EA2FA8, Library = "SceSysconForDriver")]
    int NID_B9EA2FA8() => StatusFlag(15);

    /// <summary>NID_D7F5A797(): status bit 16 (ScePower: a suspend is required).</summary>
    [Hle(Nid = 0xD7F5A797, Library = "SceSysconForDriver")]
    int NID_D7F5A797() => StatusFlag(SysconChip.BitSuspendRequired);

    /// <summary>NID_27758A64(): status bit 17 (ScePower: low battery).</summary>
    [Hle(Nid = 0x27758A64, Library = "SceSysconForDriver")]
    int NID_27758A64() => StatusFlag(SysconChip.BitLowBattery);

    /// <summary>NID_4A184B7C(): status bit 18.</summary>
    [Hle(Nid = 0x4A184B7C, Library = "SceSysconForDriver")]
    int NID_4A184B7C() => StatusFlag(18);

    /// <summary>NID_ACEE1C70(): status bit 19.</summary>
    [Hle(Nid = 0xACEE1C70, Library = "SceSysconForDriver")]
    int NID_ACEE1C70() => StatusFlag(19);

    /// <summary>NID_03C50DC3(): status bit 20.</summary>
    [Hle(Nid = 0x03C50DC3, Library = "SceSysconForDriver")]
    int NID_03C50DC3() => StatusFlag(20);

    /// <summary>NID_BFDA5590(): status bit 21.</summary>
    [Hle(Nid = 0xBFDA5590, Library = "SceSysconForDriver")]
    int NID_BFDA5590() => StatusFlag(21);

    /// <summary>NID_63B14156(): status bit 22.</summary>
    [Hle(Nid = 0x63B14156, Library = "SceSysconForDriver")]
    int NID_63B14156() => StatusFlag(22);

    /// <summary>NID_50CAE242(): status bit 23 (the thermal alert).</summary>
    [Hle(Nid = 0x50CAE242, Library = "SceSysconForDriver")]
    int NID_50CAE242() => StatusFlag(23);

    /// <summary>NID_29CF4335(): status bit 25.</summary>
    [Hle(Nid = 0x29CF4335, Library = "SceSysconForDriver")]
    int NID_29CF4335() => StatusFlag(25);

    /// <summary>NID_9F4042F8(): status bit 26.</summary>
    [Hle(Nid = 0x9F4042F8, Library = "SceSysconForDriver")]
    int NID_9F4042F8() => StatusFlag(26);

    /// <summary>NID_C50568E9(): status bit 27.</summary>
    [Hle(Nid = 0xC50568E9, Library = "SceSysconForDriver")]
    int NID_C50568E9() => StatusFlag(27);

    /// <summary>ksceSysconIsLowBatteryInhibitUpdateReboot(): status bit 28.</summary>
    [Hle(Nid = 0x1A0C140F, Library = "SceSysconForDriver")]
    int ksceSysconIsLowBatteryInhibitUpdateReboot() => StatusFlag(28);

    /// <summary>ksceSysconIsLowBatteryInhibitUpdateDownload(): status bit 29.</summary>
    [Hle(Nid = 0x1E3130EE, Library = "SceSysconForDriver")]
    int ksceSysconIsLowBatteryInhibitUpdateDownload() => StatusFlag(29);

    /// <summary>NID_012B57B3(): the first flag of the chip's event 0x104 (<see cref="SysconChip.Event104Flags"/>).</summary>
    [Hle(Nid = 0x012B57B3, Library = "SceSysconForDriver")]
    int NID_012B57B3() => Chip.Event104Flags >> 0 & 1;

    /// <summary>NID_32B2DB3D(): the second flag of the chip's event 0x104.</summary>
    [Hle(Nid = 0x32B2DB3D, Library = "SceSysconForDriver")]
    int NID_32B2DB3D() => Chip.Event104Flags >> 1 & 1;

    /// <summary>NID_142D5E82(): the third flag of the chip's event 0x104.</summary>
    [Hle(Nid = 0x142D5E82, Library = "SceSysconForDriver")]
    int NID_142D5E82() => Chip.Event104Flags >> 2 & 1;

    /// <summary>NID_490C5548(): the fourth flag of the chip's event 0x104.</summary>
    [Hle(Nid = 0x490C5548, Library = "SceSysconForDriver")]
    int NID_490C5548() => Chip.Event104Flags >> 3 & 1;
}
