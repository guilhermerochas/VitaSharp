using System.Diagnostics;
using VitaSharp.Core;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The paused emulation (<see cref="EmulationPause"/>): the guest presents nothing, so the GPU worker, idle, shows the
/// last presented frame again whenever an overlay changed (the "Pausado" badge appears or goes), the window was
/// resized, or about once a second; and a diagnostic capture requested meanwhile (F10, --diag-at) captures that frame
/// at once instead of waiting for a next one that never comes. Never inside an open scene: the thread drawing it
/// finishes it first (it keeps running until EndScene).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    (RenderTarget Src, DisplayFrame Frame)? _lastPresented;
    long _pausedPresentAt;
    OverlayImage? _pausedOverlay, _pausedCenterOverlay, _pausedStatusOverlay;

    void PresentWhilePaused()
    {
        if (_deviceLost || _inScene || _lastPresented is not { } last) return;
        var (src, frame) = last;
        // the surface may have been destroyed since it was presented (the pause came after a reallocation)
        if (src != _cpuFramebuffer && !_renderTargets.ContainsValue(src)) return;
        bool capture = _diagRequest != null && _diagActive == null;
        bool overlays = !ReferenceEquals(_overlay, _pausedOverlay) || !ReferenceEquals(_centerOverlay, _pausedCenterOverlay)
                        || !ReferenceEquals(_statusOverlay, _pausedStatusOverlay);
        long now = Stopwatch.GetTimestamp();
        bool periodic = now - _pausedPresentAt > Stopwatch.Frequency;
        if (!capture && (_swapchain.Handle == 0 || !(overlays || _swapchainDirty || periodic))) return;
        _pausedPresentAt = now;
        _pausedOverlay = _overlay; _pausedCenterOverlay = _centerOverlay; _pausedStatusOverlay = _statusOverlay;

        BeginFrameIfNeeded();
        EndRendering();
        SyncRenderTargetForRead(src);
        if (capture && _diagRequest is { } pending)
        {
            ArmCapture(pending);
            if (_diagActive is { } active)
            {
                active.Draws.AppendLine("# the emulation is paused: the frame is the last one presented before the pause, no draws were issued since");
                PresentOut(src, frame);
                CompleteCapture(active, src, frame);
                return;
            }
        }
        PresentOut(src, frame);
    }
}
