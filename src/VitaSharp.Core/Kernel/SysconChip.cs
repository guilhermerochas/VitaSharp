using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The syscon chip of the emulated unit (the "Ernie" controller that SceSyscon, syscon.skprx FW 3.74, talks to over
/// SPI): one per system, the single state the kernel's SceSysconForDriver, ScePowerForDriver and SceRtcForDriver and
/// the user-mode ScePower/SceRtc answers come from.
/// <para>
/// The firmware sends a frame <c>tx = {command low, command high, length, data[length - 1], checksum}</c> (the
/// checksum is the inverted byte sum of the frame before it) and gets back <c>rx = {status byte, 0, length, result,
/// data[length - 2], checksum}</c>. The status byte carries the chip's own flags (bit 3 the AC adapter, bit 6 the
/// downloader mode); a result with any of its low 6 bits set fails the command (SceSyscon reports
/// 0x80250200 | result). The chip answers here at once, the way the module's interrupt handler hands the reply to
/// the waiting packet.
/// </para>
/// <para>
/// The status word (32 bits) holds the event bits the module turns into its flags and event handlers (table at
/// 0x81009584: bit n of byte b is one flag; 16 low battery, 17 battery low, 23 thermal alert, 28/29 low battery for
/// an update...): an unplugged retail unit with a charged battery has none set.
/// </para>
/// </summary>
[GenerateSnapshot]
public sealed partial class SysconChip
{
    /// <summary>The first byte of every reply: the chip's own state.</summary>
    public const byte StatusAcAdapter = 0x08, StatusDownloaderMode = 0x40;

    /// <summary>A reply whose data the firmware reads as 1 to 4 bytes (the module's word reads): 4 zero bytes for a
    /// command without its own answer.</summary>
    const int DefaultReplyLength = 4;

    /// <summary>The scratchpad (commands 0x90 read, 0x91 write: 256 bytes) and the second one (0x92/0x93: 128 bytes):
    /// RAM the chip keeps while the unit is off (SceRtc keeps its clock offsets there).</summary>
    public const int ScratchpadSize = 0x100, Scratchpad2Size = 0x80;

    /// <summary>The syscon firmware version (ksceSysconGetBaryonVersion). No retail unit's value is documented; this
    /// is the newest family the 3.74 modules test for (ScePower's 0x1040105, syscon's 0x70504/0x80001/0x90907), so
    /// every command takes its current form.</summary>
    public const uint DefaultBaryonVersion = 0x01040105;

    /// <summary>Status word bits ScePower reads: a suspend is required (16), the battery is low (17).</summary>
    public const int BitSuspendRequired = 16, BitLowBattery = 17;

    /// <summary>The four flags SceSyscon keeps from the chip's event 0x104 (bit 0..3: the module's 0x8100C0DB..DE).
    /// The module starts them from the boot parameters' word +0x14 (each the inverse of one of its bits); with that
    /// word 0 they are 1, 1, 0, 1.</summary>
    public const int DefaultEvent104Flags = 0b1011;

    /// <summary>The multi connector's information with nothing plugged (the module's value when status bit 5 is
    /// clear).</summary>
    public const uint NothingOnMultiConnector = 0x4FF00;

    /// <summary>The chip's own flags (the first byte of every reply).</summary>
    [SnapshotMissing]
    public byte StatusByte;

    /// <summary>The event bits (see the class summary).</summary>
    [SnapshotMissing]
    public uint StatusWord;

    [SnapshotMissing(DefaultBaryonVersion)]
    public uint BaryonVersion = DefaultBaryonVersion;

    /// <summary>The syscon firmware's build time (ksceSysconGetBaryonTimestamp): not documented for a retail unit, 0.</summary>
    [SnapshotMissing]
    public ulong BaryonTimestamp;

    [SnapshotMissing(DefaultEvent104Flags)]
    public int Event104Flags = DefaultEvent104Flags;

    /// <summary>What the chip's micro USB event (0x130) reported: nothing plugged, 0.</summary>
    [SnapshotMissing]
    public uint MicroUsbInfo;

    /// <summary>What the chip's multi connector event (0x103) reported.</summary>
    [SnapshotMissing(NothingOnMultiConnector)]
    public uint MultiCnInfo = NothingOnMultiConnector;

    /// <summary>The configuration storage modes the module's transactions turn on: the IDU (demo unit) mode and the
    /// show mode.</summary>
    public const uint ModeIdu = 1, ModeShow = 2;

    [SnapshotMissing]
    public uint ConfigModes;

    /// <summary>The last power mode SceSyscon asked for (ksceSysconSetPowerMode); the power sequence itself is not
    /// emulated.</summary>
    [SnapshotMissing]
    public uint PowerMode;

    [SnapshotMissing]
    public readonly byte[] Scratchpad = new byte[ScratchpadSize];

    [SnapshotMissing]
    public readonly byte[] Scratchpad2 = new byte[Scratchpad2Size];

    /// <summary>The battery the chip reports (<see cref="SysconBattery"/>).</summary>
    [SnapshotMissing]
    public readonly SysconBattery Battery = new();

    /// <summary>What the firmware last sent to each command that only sets something (an LED, a device's power, a
    /// mode): by command number, the frame's data as a little-endian word.</summary>
    [SnapshotMissing]
    public readonly Dictionary<ushort, uint> Registers = new();

    /// <summary>How many commands the chip has answered (for tests and the log).</summary>
    [SnapshotIgnore]
    public long Commands;

    readonly object _lock = new();

    /// <summary>The inverted byte sum the frames end with.</summary>
    public static byte Checksum(ReadOnlySpan<byte> frame)
    {
        byte sum = 0;
        foreach (byte value in frame)
        {
            sum += value;
        }

        return (byte)~sum;
    }

    /// <summary>Sends <paramref name="tx"/> (a whole frame) and writes the reply frame into <paramref name="rx"/>
    /// (at least 4 bytes plus what the command answers; a longer answer is cut to it). Returns the reply's length
    /// byte.</summary>
    public int Execute(ReadOnlySpan<byte> tx, Span<byte> rx)
    {
        ushort command = (ushort)(tx[0] | tx[1] << 8);
        int dataLength = Math.Max(0, tx[2] - 1);
        ReadOnlySpan<byte> data = tx.Slice(3, Math.Min(dataLength, tx.Length - 3));
        Span<byte> answer = stackalloc byte[0x100];
        int answerLength;
        lock (_lock)
        {
            Commands++;
            answerLength = Answer(command, data, answer);
        }

        int room = Math.Max(0, rx.Length - 5);
        answerLength = Math.Min(answerLength, room);
        rx[0] = StatusByte;
        rx[1] = 0;
        rx[2] = (byte)(answerLength + 2);
        rx[3] = 0;
        answer[..answerLength].CopyTo(rx[4..]);
        rx[4 + answerLength] = Checksum(rx[..(4 + answerLength)]);
        return answerLength + 2;
    }

    /// <summary>The chip's answer to one command: fills <paramref name="answer"/> and returns its length.</summary>
    int Answer(ushort command, ReadOnlySpan<byte> data, Span<byte> answer)
    {
        answer.Clear();
        switch (command)
        {
            case 0x90:
                return ReadArea(Scratchpad, data, answer);
            case 0x91:
                WriteArea(Scratchpad, data);
                return 0;
            case 0x92:
                return ReadArea(Scratchpad2, data, answer);
            case 0x93:
                WriteArea(Scratchpad2, data);
                return 0;
            case 0x800:
                return Word(answer, BatteryStatusWord);
            case 0x900:
            case 0x902:
                return Word(answer, (uint)Battery.RemainCapacity);
            case 0x901:
                return Word(answer, (uint)Battery.Voltage);
            case 0x903:
                answer[0] = (byte)Battery.MaxLevel;
                answer[1] = (byte)Battery.Level;
                return 2;
            case 0x981:
                return Word(answer, (uint)Battery.Temperature);
            case 0x983:
                answer[0] = (byte)(sbyte)Battery.Current;
                return 1;
            case 0x984:
                return Word(answer, (uint)Battery.LifeTimeMinutes);
            case 0x985:
                answer[0] = (byte)Battery.StateOfHealth;
                return 2;
            case 0x98A:
                return Word(answer, (uint)Battery.FullCapacity);
            case 0x98C:
                return Word(answer, (uint)Battery.CycleCount);
        }

        if (data.Length > 0)
        {
            uint value = 0;
            for (int i = 0; i < Math.Min(4, data.Length); i++)
            {
                value |= (uint)data[i] << (8 * i);
            }

            Registers[command] = value;
            return 0;
        }

        // a read: what was set through the command's pair (the set command is the read's + 1 in the families that
        // have both: 0x480/0x481, 0x3AC/0x3AB...), else nothing
        if (Registers.TryGetValue(command, out uint stored))
        {
            Write32(answer, stored);
        }

        return DefaultReplyLength;
    }

    static int Word(Span<byte> answer, uint value)
    {
        Write32(answer, value);
        return 4;
    }

    /// <summary>The battery's status word (command 0x800, ScePower's poll): 0x40 a battery is in, bit 1 it charges.
    /// </summary>
    public uint BatteryStatusWord
    {
        get
        {
            uint word = Battery.Present ? BatteryPresentBit : 0;
            if (Battery.Present && Battery.Charging)
            {
                word |= BatteryChargingBit;
            }

            return word;
        }
    }

    public const uint BatteryPresentBit = 0x40, BatteryChargingBit = 0x02;

    static void Write32(Span<byte> target, uint value)
    {
        target[0] = (byte)value;
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 16);
        target[3] = (byte)(value >> 24);
    }

    /// <summary>{offset (2 bytes), size}: the bytes, inside the area.</summary>
    static int ReadArea(byte[] area, ReadOnlySpan<byte> data, Span<byte> answer)
    {
        if (data.Length < 3)
        {
            return 0;
        }

        int offset = data[0] | data[1] << 8;
        int size = Math.Min(data[2], Math.Max(0, area.Length - offset));
        area.AsSpan(offset, size).CopyTo(answer);
        return data[2];
    }

    /// <summary>{offset (2 bytes), size, bytes}: stored inside the area.</summary>
    static void WriteArea(byte[] area, ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
        {
            return;
        }

        int offset = data[0] | data[1] << 8;
        int size = Math.Min(Math.Min(data[2], data.Length - 3), Math.Max(0, area.Length - offset));
        data.Slice(3, size).CopyTo(area.AsSpan(offset, size));
    }

    /// <summary>One event bit of <see cref="StatusWord"/> (the module's flag for it).</summary>
    public bool StatusBit(int bit) => (StatusWord >> bit & 1) != 0;

    /// <summary>The AC adapter is plugged (status byte bit 3): an unplugged retail unit by default.</summary>
    public bool AcAdapter
    {
        get => (StatusByte & StatusAcAdapter) != 0;
        set => StatusByte = value ? (byte)(StatusByte | StatusAcAdapter) : (byte)(StatusByte & ~StatusAcAdapter);
    }
}
