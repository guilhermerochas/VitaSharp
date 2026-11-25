namespace VitaSharp.Core.Host;

/// <summary>One keyboard report (psp2/hid.h SceHidKeyboardReport): the modifier bits (bit 0 left Ctrl .. bit 7 right GUI,
/// the USB HID order), up to six USB HID usages of the keys held, and the guest time (microseconds).</summary>
public readonly record struct HidKeyboardReport(byte Modifiers, ulong Keys, ulong TimestampUs)
{
    /// <summary>Usage <paramref name="index"/> (0..5) of the keys held, 0 = none.</summary>
    public byte Key(int index) => (byte)(Keys >> (index * 8));
}

/// <summary>One mouse report (psp2/hid.h SceHidMouseReport): the buttons (bit 0 left, 1 right, 2 middle), the
/// movement since the previous report, the wheel and the tilt, and the guest time (microseconds).</summary>
public readonly record struct HidMouseReport(byte Buttons, short DeltaX, short DeltaY, sbyte Wheel, sbyte Tilt, ulong TimestampUs);

/// <summary>
/// The reports a paired HID device (a Bluetooth keyboard or mouse of the Vita) produced, newest last, numbered so each
/// reader takes the ones it has not read yet (FW 3.74 SceHid keeps a ring per device and a position per reader).
/// Thread-safe: the host's input thread adds, guest threads read.
/// </summary>
public class HidReportRing<T>
    where T : struct
{
    /// <summary>The reports kept (older ones are dropped).</summary>
    public const int Capacity = 64;

    private readonly T[] _ring = new T[Capacity];
    private readonly object _lock = new();
    private long _next = 1;

    /// <summary>The number the next report gets; a reader that has read up to here has nothing new.</summary>
    public long NextNumber
    {
        get
        {
            lock (_lock)
            {
                return _next;
            }
        }
    }

    protected void Add(T report)
    {
        lock (_lock)
        {
            _ring[_next % Capacity] = report;
            _next++;
        }

        ReportAdded?.Invoke();
    }

    /// <summary>A report was added (SceHid's read hint), raised outside the ring's lock.</summary>
    public event Action? ReportAdded;

    /// <summary>Copies the reports numbered from <paramref name="position"/> on (the oldest still kept when it is older)
    /// into <paramref name="dest"/>, oldest first, as many as fit, and moves <paramref name="position"/> past them when
    /// <paramref name="consume"/>; returns how many were copied.</summary>
    public int Read(ref long position, Span<T> dest, bool consume)
    {
        lock (_lock)
        {
            long from = Math.Max(position, Math.Max(1, _next - Capacity));
            int count = (int)Math.Min(dest.Length, _next - from);
            if (count <= 0)
            {
                if (consume)
                {
                    position = _next;
                }

                return 0;
            }

            for (int i = 0; i < count; i++)
            {
                dest[i] = _ring[(from + i) % Capacity];
            }

            // more than fit: the rest waits for the next read
            if (consume)
            {
                position = from + count;
            }

            return count;
        }
    }

    /// <summary>Drops the reports not read yet (sceHidKeyboardClear): the reader's position moves to the end.</summary>
    public void Clear(ref long position)
    {
        position = NextNumber;
    }
}

/// <summary>
/// The host keyboard as a paired Bluetooth keyboard of the Vita (SceHid): each change of the keys held becomes a report.
/// Keys are USB HID usages (0x04 = A ... 0xE0..0xE7 the modifiers); the front-end maps its key events to them.
/// </summary>
public sealed class HidKeyboard : HidReportRing<HidKeyboardReport>
{
    private readonly List<byte> _held = [];
    private byte _modifiers;

    /// <summary>The keyboard's media keys (its consumer-control collection, which SceHid keeps as a device of its own).</summary>
    public HidConsumerControl ConsumerControl { get; } = new();

    /// <summary>The usages of the modifier keys: 0xE0 left Ctrl .. 0xE7 right GUI (each its bit in the modifiers).</summary>
    public const byte FirstModifier = 0xE0, LastModifier = 0xE7;

    /// <summary>A key went down (<paramref name="down"/>) or up at guest time <paramref name="timestampUs"/>.</summary>
    public void Key(byte usage, bool down, ulong timestampUs)
    {
        if (usage == 0)
        {
            return;
        }

        lock (_held)
        {
            if (usage is >= FirstModifier and <= LastModifier)
            {
                byte bit = (byte)(1 << (usage - FirstModifier));
                byte modifiers = down ? (byte)(_modifiers | bit) : (byte)(_modifiers & ~bit);
                if (modifiers == _modifiers)
                {
                    return;
                }

                _modifiers = modifiers;
            }
            else if (down)
            {
                if (_held.Contains(usage))
                {
                    return;
                }

                _held.Add(usage);
            }
            else if (!_held.Remove(usage))
            {
                return;
            }

            ulong keys = 0;
            // the last six keys pressed (a seventh pushes the oldest out, as a 6-key rollover report does)
            int first = Math.Max(0, _held.Count - 6);
            for (int i = first; i < _held.Count; i++)
            {
                keys |= (ulong)_held[i] << ((i - first) * 8);
            }

            Add(new HidKeyboardReport(_modifiers, keys, timestampUs));
        }
    }
}

/// <summary>The host mouse as a paired Bluetooth mouse of the Vita (SceHid): each movement, button change or wheel step
/// becomes a report.</summary>
public sealed class HidMouse : HidReportRing<HidMouseReport>
{
    private byte _buttons;
    private readonly object _stateLock = new();

    /// <summary>The mouse moved by (<paramref name="deltaX"/>, <paramref name="deltaY"/>) pixels and the wheel by
    /// <paramref name="wheel"/> steps (positive away from the user).</summary>
    public void Move(int deltaX, int deltaY, int wheel, ulong timestampUs)
    {
        if (deltaX == 0 && deltaY == 0 && wheel == 0)
        {
            return;
        }

        lock (_stateLock)
        {
            Add(new HidMouseReport(_buttons, (short)Math.Clamp(deltaX, short.MinValue, short.MaxValue), (short)Math.Clamp(deltaY, short.MinValue, short.MaxValue),
                (sbyte)Math.Clamp(wheel, sbyte.MinValue, sbyte.MaxValue), 0, timestampUs));
        }
    }

    /// <summary>Button <paramref name="button"/> (0 left, 1 right, 2 middle) went down or up.</summary>
    public void Button(int button, bool down, ulong timestampUs)
    {
        if (button is < 0 or > 7)
        {
            return;
        }

        lock (_stateLock)
        {
            byte bit = (byte)(1 << button);
            byte buttons = down ? (byte)(_buttons | bit) : (byte)(_buttons & ~bit);
            if (buttons == _buttons)
            {
                return;
            }

            _buttons = buttons;
            Add(new HidMouseReport(_buttons, 0, 0, 0, 0, timestampUs));
        }
    }
}

/// <summary>One consumer-control report (FW 3.74 SceHid, 0x10 bytes): up to four consumer-page usages held (USB HID
/// page 0x0C, 16 bits each, 0 = none) and the guest time (microseconds).</summary>
public readonly record struct HidConsumerReport(ulong Usages, ulong TimestampUs)
{
    /// <summary>Usage <paramref name="index"/> (0..3) of the controls held, 0 = none.</summary>
    public ushort Usage(int index) => (ushort)(Usages >> (index * 16));
}

/// <summary>
/// The media keys of the paired keyboard as SceHid's consumer-control device: each change of the controls held
/// becomes a report. FW 3.74 SceHid (FUN_81002218, its report parser) keeps at most four usages per report, in the
/// order it finds them, and only the ones the HID Usage Tables define for the consumer page: 0 and the reserved
/// ranges (0x07..0x1F, 0x23..0x2F, 0x37..0x3F, 0x49..0x5F, 0x67..0x7F, 0x9F, 0xA5..0xAF, 0xCF..0xDF, 0xEB..0xEF,
/// 0xF6..0xFF, 0x10E..0x14F, 0x156..0x15F, 0x16B..0x16F, 0x175..0x17F, 0x1BB, 0x1C8..0x1FF, 0x20A..0x219, 0x29D and
/// up) are dropped (FUN_810016d0 for the upper ones).
/// </summary>
public sealed class HidConsumerControl : HidReportRing<HidConsumerReport>
{
    /// <summary>The usages one report carries at most.</summary>
    public const int MaxUsages = 4;

    private readonly List<ushort> _held = [];

    /// <summary>Whether SceHid keeps <paramref name="usage"/> in a report (see the class).</summary>
    public static bool IsKept(ushort usage)
    {
        return usage switch
        {
            0 => false,
            >= 0x07 and <= 0x1F => false,
            >= 0x23 and <= 0x2F => false,
            >= 0x37 and <= 0x3F => false,
            >= 0x49 and <= 0x5F => false,
            >= 0x67 and <= 0x7F => false,
            0x9F => false,
            >= 0xA5 and <= 0xAF => false,
            >= 0xCF and <= 0xDF => false,
            >= 0xEB and <= 0xEF => false,
            >= 0xF6 and <= 0xFF => false,
            >= 0x10E and <= 0x14F => false,
            >= 0x156 and <= 0x15F => false,
            >= 0x16B and <= 0x16F => false,
            >= 0x175 and <= 0x17F => false,
            0x1BB => false,
            >= 0x1C8 and <= 0x1FF => false,
            >= 0x20A and <= 0x219 => false,
            >= 0x29D => false,
            _ => true,
        };
    }

    /// <summary>A control went down (<paramref name="down"/>) or up at guest time <paramref name="timestampUs"/>.</summary>
    public void Key(ushort usage, bool down, ulong timestampUs)
    {
        if (!IsKept(usage))
        {
            return;
        }

        lock (_held)
        {
            if (down)
            {
                if (_held.Contains(usage))
                {
                    return;
                }

                _held.Add(usage);
            }
            else if (!_held.Remove(usage))
            {
                return;
            }

            ulong usages = 0;
            int count = Math.Min(_held.Count, MaxUsages);
            for (int i = 0; i < count; i++)
            {
                usages |= (ulong)_held[i] << (i * 16);
            }

            Add(new HidConsumerReport(usages, timestampUs));
        }
    }
}
