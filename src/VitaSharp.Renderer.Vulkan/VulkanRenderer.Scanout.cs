using System.Diagnostics;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// What the idle worker shows while the guest flips nothing (the emulation running, not paused: VulkanRenderer.Pause.cs
/// has that case): the display's scanout of a CPU framebuffer (<see cref="DisplayScanout"/>). The Vita's display engine
/// reads the framebuffer memory at every vblank, so a program that set its framebuffer once and then writes text into it with the
/// CPU has that text on the panel at the next vblank. Here a frame was shown only at a flip: such a program stayed on
/// the picture of its first flip (a black screen) whatever it drew after. At most once per guest vblank, the last frame
/// shown, when it is a CPU framebuffer whose memory changed since, is shown again through the whole present path
/// (upload, shell overlay, frame dumps, the session capture's recording), as one more frame on screen
/// (<see cref="DisplayScanout.Frames"/>). A frame the GPU rendered is never looked at.
/// Never inside an open scene (the guest is drawing), and nothing a guest thread waits for: guest pacing is unchanged.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private readonly DisplayScanout _scanout = new();
    /// <summary>The shell's overlay plane of the last present (shown again with the scanout's frames).</summary>
    private DisplayFrame? _lastShellOverlay;
    /// <summary>The flip of the last present (the scanout's frames carry it: they show that flip's buffer).</summary>
    private long _lastPresentFlip = -1;
    private readonly PresentCommand _scanoutCommand = new() { Frame = default };

    /// <summary>The last frame shown is a CPU framebuffer whose memory the scanout watches.</summary>
    public bool CpuScanoutArmed => _scanout.Armed;

    /// <inheritdoc/>
    public int IdleWakeMs => _scanout.Armed ? DisplayScanout.MillisecondsToNextVblank() : 50;

    /// <summary>The present of <paramref name="frame"/> shows a CPU framebuffer (<paramref name="cpu"/>, its memory
    /// just uploaded) or a surface the GPU rendered.</summary>
    private void NoteScanoutSource(DisplayFrame frame, bool cpu)
    {
        if (cpu)
        {
            _scanout.Shown(frame, VBlank.Count);
        }
        else
        {
            _scanout.Disarm();
        }

        if (_presentCmd != null && !ReferenceEquals(_presentCmd, _scanoutCommand))
        {
            _lastShellOverlay = _presentCmd.Overlay;
            _lastPresentFlip = _presentCmd.Flip;
        }
    }

    /// <summary>The idle worker, the emulation running: the scanout of a changed CPU framebuffer.</summary>
    private void ShowWhileIdle()
    {
        if (_deviceLost || _inScene || _lastPresented is not { } last)
        {
            return;
        }

        DisplayFrame frame = last.Frame;
        long vblank = VBlank.Count;
        if (_scanout.Due(vblank))
        {
            _scanoutCommand.Frame = frame;
            _scanoutCommand.Overlay = _lastShellOverlay;
            _scanoutCommand.Flip = _lastPresentFlip;
            _scanoutCommand.VBlank = vblank;
            _scanoutCommand.GuestUs = GuestClock.Microseconds;
            _scanoutCommand.HostTicks = Stopwatch.GetTimestamp();
            _presentCmd = _scanoutCommand;
            try
            {
                Present(frame);
            }
            finally
            {
                _presentCmd = null;
            }
            Interlocked.Increment(ref DisplayScanout.Frames);
        }
    }
}
