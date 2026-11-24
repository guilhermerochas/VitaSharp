using System.Diagnostics;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// What the idle worker shows while the guest flips nothing (the emulation running, not paused: VulkanRenderer.Pause.cs
/// has that case).
/// <list type="bullet">
/// <item>The display's scanout of a CPU framebuffer (<see cref="DisplayScanout"/>): the Vita's display engine reads the
/// framebuffer memory at every vblank, so a program that set its framebuffer once and then writes text into it with the
/// CPU has that text on the panel at the next vblank. Here a frame was shown only at a flip: such a program stayed on
/// the picture of its first flip (a black screen) whatever it drew after. At most once per guest vblank, the last frame
/// shown, when it is a CPU framebuffer whose memory changed since, is shown again through the whole present path
/// (upload, shell overlay, frame dumps, the session capture's recording), as one more frame on screen
/// (<see cref="DisplayScanout.Frames"/>). A frame the GPU rendered is never looked at.</item>
/// <item>The emulator's own overlays (the "REC" badge of the recording, notices): drawn over the frame when it is shown,
/// so with no frame since, a badge that went stayed on screen. Once two vblanks passed without a frame shown, a change
/// of the overlays shows the last frame again with them.</item>
/// </list>
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
    /// <summary>The emulator's overlays on the last frame shown, and the guest vblank it was shown at.</summary>
    private OverlayImage? _shownOverlay, _shownCenterOverlay, _shownStatusOverlay;
    private long _shownVblank;
    private long _idleRepresents;

    /// <summary>The last frame shown is a CPU framebuffer whose memory the scanout watches.</summary>
    public bool CpuScanoutArmed => _scanout.Armed;

    /// <summary>Frames the idle worker showed again for a change of the emulator's overlays.</summary>
    public long IdleRepresents => Interlocked.Read(ref _idleRepresents);

    /// <inheritdoc/>
    public int IdleWakeMs => _scanout.Armed || OverlaysChanged ? DisplayScanout.MillisecondsToNextVblank() : 50;

    private bool OverlaysChanged => !ReferenceEquals(Volatile.Read(ref _overlay), _shownOverlay)
                                    || !ReferenceEquals(Volatile.Read(ref _centerOverlay), _shownCenterOverlay)
                                    || !ReferenceEquals(Volatile.Read(ref _statusOverlay), _shownStatusOverlay);

    /// <summary>A frame went to the screen (or was submitted, with no swapchain) with the current overlays.</summary>
    private void NoteShown()
    {
        _shownOverlay = Volatile.Read(ref _overlay);
        _shownCenterOverlay = Volatile.Read(ref _centerOverlay);
        _shownStatusOverlay = Volatile.Read(ref _statusOverlay);
        _shownVblank = VBlank.Count;
    }

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
            _lastPlacement = _presentCmd.Placement;
            _lastPresentFlip = _presentCmd.Flip;
        }
    }

    /// <summary>The idle worker, the emulation running: the scanout of a changed CPU framebuffer, or the last frame again
    /// for a change of the overlays.</summary>
    private void ShowWhileIdle()
    {
        if (_deviceLost || _inScene || _lastPresented is not { } last)
        {
            return;
        }

        (RenderTarget src, DisplayFrame frame) = last;
        long vblank = VBlank.Count;
        if (_scanout.Due(vblank))
        {
            // the framebuffer watched: the frame shown is the screen-sized composite when it was placed
            frame = _scanout.Frame;
            _scanoutCommand.Frame = frame;
            _scanoutCommand.Overlay = _lastShellOverlay;
            _scanoutCommand.Placement = _lastPlacement;
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
            return;
        }

        if (!OverlaysChanged || vblank < _shownVblank + 2)
        {
            return;
        }

        // the surface may have gone since it was shown (a reallocation, the process ended)
        if (!IsLiveSurface(src))
        {
            NoteShown();
            return;
        }

        BeginFrameIfNeeded();
        EndRendering();
        SyncRenderTargetForRead(src);
        PresentOut(src, frame);
        Interlocked.Increment(ref _idleRepresents);
    }

    /// <summary><paramref name="src"/>, a frame shown last, still exists: a CPU framebuffer, a render target, the
    /// memory-order copy of one, the shell overlay's composite or the placed plane's.</summary>
    private bool IsLiveSurface(RenderTarget src)
    {
        if (src == _cpuFramebuffer || src == _cpuFramebuffer16 || src == _blankScreen || src == _overlayComposite || src == _compatComposite
            || _renderTargets.ContainsValue(src))
        {
            return true;
        }

        foreach (MemoryOrderCopy copy in _memoryOrderCopies.Values)
        {
            if (copy.Target == src)
            {
                return true;
            }
        }
        return false;
    }
}
