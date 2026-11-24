using System.IO.Hashing;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Gpu;

/// <summary>
/// The display engine's scanout of a framebuffer the CPU draws. On the Vita, sceDisplaySetFrameBuf only hands the
/// display engine the framebuffer's address, pitch and size (SceDisplay of FW 3.74 stores the head's descriptor; no
/// pixel is copied), and the engine reads that memory at every vblank: what a program writes into the framebuffer after
/// setting it once (text drawn with the CPU over a cleared screen, then a wait for a button) is on the panel at the next
/// vblank without another flip. The emulator shows a frame at each flip; for a framebuffer no GPU surface renders (a CPU
/// framebuffer), the GPU worker asks this, at most once per guest vblank while its queue is empty, whether the memory
/// changed since it was last shown, and shows it again when it did. A framebuffer the GPU renders is never hashed (its
/// surface holds the picture, not guest memory), and the guest's pacing is untouched: no guest thread waits for these
/// frames or is told about them.
/// </summary>
public sealed unsafe class DisplayScanout
{
    /// <summary>Frames shown by the scanout, not by a flip (the frame rate on screen adds them to the flips:
    /// <see cref="Display.FramesShown"/>).</summary>
    public static long Frames;

    private DisplayFrame _frame;
    private bool _armed;
    private ulong _hash;
    private long _vblank = long.MinValue;

    /// <summary>The frame last shown is a CPU framebuffer: its memory is watched.</summary>
    public bool Armed => _armed;

    /// <summary>A CPU framebuffer was just shown (its memory uploaded) at guest vblank <paramref name="vblank"/>.</summary>
    public void Shown(DisplayFrame frame, long vblank)
    {
        _frame = frame;
        _armed = true;
        _hash = Hash(frame);
        _vblank = vblank;
    }

    /// <summary>A frame the GPU rendered was shown: nothing is watched.</summary>
    public void Disarm()
    {
        _armed = false;
    }

    /// <summary>The scanout at guest vblank <paramref name="vblank"/> would show a picture other than the one on screen:
    /// a new vblank since the last look, and the framebuffer's memory changed since it was shown. The caller shows the
    /// frame again (which calls <see cref="Shown"/>).</summary>
    public bool Due(long vblank)
    {
        if (!_armed || vblank == _vblank)
        {
            return false;
        }

        _vblank = vblank;
        return Hash(_frame) != _hash;
    }

    /// <summary>Host milliseconds until the next guest vblank (how long an idle worker may sleep while a framebuffer is
    /// watched), between 2 and 50.</summary>
    public static int MillisecondsToNextVblank()
    {
        long ticks = VBlank.HostTicksUntil(VBlank.Count + 1);
        long ms = ticks * 1000 / System.Diagnostics.Stopwatch.Frequency + 1;
        return (int)Math.Clamp(ms, 2, 50);
    }

    /// <summary>Bytes the display reads for <paramref name="frame"/>: both display formats are 32 bits per pixel.</summary>
    public static int Bytes(DisplayFrame frame) => (int)Math.Min(int.MaxValue, (long)frame.Pitch * frame.Height * 4);

    /// <summary>The framebuffer's memory, read in place (not through <see cref="GuestMemory.ReadSpan(uint, int)"/>: a
    /// look that shows nothing must not enter a GPU dump).</summary>
    private static ulong Hash(DisplayFrame frame) => XxHash3.HashToUInt64(new ReadOnlySpan<byte>(GuestMemory.Ptr(frame.Address), Bytes(frame)));
}
