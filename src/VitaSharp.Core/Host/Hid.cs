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
