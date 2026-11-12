using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    private SwapchainKHR _swapchain;
    private Image[] _swapImages = [];
    /// <summary>Render-done semaphores, one per swapchain image index (kept across swapchain recreation).</summary>
    private readonly List<Silk.NET.Vulkan.Semaphore> _swapDone = new();
    private Extent2D _swapExtent;
    private Format _swapFormat;
    private bool _swapchainDirty, _swapchainZeroLogged;
    /// <summary>The swapchain's preTransform is not the surface's currentTransform (on purpose: IDENTITY on a rotated
    /// Android display). Android then returns VK_SUBOPTIMAL_KHR from every acquire and present; recreating the
    /// swapchain would not change that, so SUBOPTIMAL is ignored (OUT_OF_DATE and resizes still recreate it).</summary>
    private bool _suboptimalExpected;

    private void CreateSwapchain()
    {
        KhrSurface khrSurface = _core.SurfaceExt!;
        KhrSwapchain khrSwapchain = _core.SwapchainExt!;
        khrSurface.GetPhysicalDeviceSurfaceCapabilities(_core.Physical, _core.Surface, out SurfaceCapabilitiesKHR caps);
        uint fc = 0;
        khrSurface.GetPhysicalDeviceSurfaceFormats(_core.Physical, _core.Surface, ref fc, null);
        var formats = new SurfaceFormatKHR[fc];
        fixed (SurfaceFormatKHR* p = formats)
        {
            khrSurface.GetPhysicalDeviceSurfaceFormats(_core.Physical, _core.Surface, ref fc, p);
        }

        SurfaceFormatKHR chosen = formats.FirstOrDefault(f => f.Format == Format.B8G8R8A8Unorm, formats[0]);
        if (formats.Any(f => f.Format == Format.R8G8B8A8Unorm) && !formats.Any(f => f.Format == Format.B8G8R8A8Unorm))
        {
            chosen = formats.First(f => f.Format == Format.R8G8B8A8Unorm);
        }

        uint pc = 0;
        khrSurface.GetPhysicalDeviceSurfacePresentModes(_core.Physical, _core.Surface, ref pc, null);
        var modes = new PresentModeKHR[pc];
        fixed (PresentModeKHR* p = modes)
        {
            khrSurface.GetPhysicalDeviceSurfacePresentModes(_core.Physical, _core.Surface, ref pc, p);
        }

        PresentModeKHR mode = _options.VSync ? PresentModeKHR.FifoKhr : modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr
            : modes.Contains(PresentModeKHR.ImmediateKhr) ? PresentModeKHR.ImmediateKhr : PresentModeKHR.FifoKhr;

        (int w, int h) = _options.Surface!.FramebufferSize;
        Extent2D extent = caps.CurrentExtent.Width != uint.MaxValue ? caps.CurrentExtent
            : new Extent2D((uint)Math.Clamp(w, (int)caps.MinImageExtent.Width, (int)caps.MaxImageExtent.Width),
                           (uint)Math.Clamp(h, (int)caps.MinImageExtent.Height, (int)caps.MaxImageExtent.Height));
        // Android reports the display's rotation as currentTransform (ROTATE_90 for a landscape app on a portrait
        // phone), with currentExtent possibly in the display's native orientation. The blit is not pre-rotated, so the
        // swapchain asks for IDENTITY (the compositor rotates) with the extent in the window's orientation (the
        // front-end's FramebufferSize). Desktop surfaces are always IDENTITY: nothing changes there.
        SurfaceTransformFlagsKHR transform = caps.CurrentTransform;
        _suboptimalExpected = false;
        if (transform != SurfaceTransformFlagsKHR.IdentityBitKhr && (caps.SupportedTransforms & SurfaceTransformFlagsKHR.IdentityBitKhr) != 0)
        {
            if ((transform & (SurfaceTransformFlagsKHR.Rotate90BitKhr | SurfaceTransformFlagsKHR.Rotate270BitKhr)) != 0
                && w > 0 && h > 0 && (w > h) != (extent.Width > extent.Height))
            {
                extent = new Extent2D(extent.Height, extent.Width);
            }

            transform = SurfaceTransformFlagsKHR.IdentityBitKhr;
            _suboptimalExpected = true;
        }
        if (extent.Width == 0 || extent.Height == 0)
        {
            if (!_swapchainZeroLogged)
            {
                _swapchainZeroLogged = true;
                Log.Info("Swapchain: the surface is 0x0 (minimized): no presentation until it has a size again");
            }

            _swapchainDirty = true;
            return;
        }

        _swapchainZeroLogged = false;
        // Android: 3 images (triple buffering under FIFO) unless the driver needs more; minImageCount + 1 there gave 4
        // on drivers that report 3, one more frame of latency for nothing. The desktop is unchanged.
        uint count = OperatingSystem.IsAndroid() ? Math.Max(caps.MinImageCount, 3) : Math.Max(caps.MinImageCount + 1, 3);
        if (caps.MaxImageCount > 0)
        {
            count = Math.Min(count, caps.MaxImageCount);
        }

        SwapchainKHR old = _swapchain;
        var ci = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _core.Surface,
            MinImageCount = count,
            ImageFormat = chosen.Format,
            ImageColorSpace = chosen.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = transform,
            // opaque everywhere on desktop; Android surfaces typically only offer INHERIT
            CompositeAlpha = (caps.SupportedCompositeAlpha & CompositeAlphaFlagsKHR.OpaqueBitKhr) != 0 ? CompositeAlphaFlagsKHR.OpaqueBitKhr
                : (caps.SupportedCompositeAlpha & CompositeAlphaFlagsKHR.InheritBitKhr) != 0 ? CompositeAlphaFlagsKHR.InheritBitKhr
                : (CompositeAlphaFlagsKHR)((uint)caps.SupportedCompositeAlpha & (uint)-(int)caps.SupportedCompositeAlpha),
            PresentMode = mode,
            Clipped = true,
            OldSwapchain = old,
        };
        khrSwapchain.CreateSwapchain(_dev, in ci, null, out _swapchain).Ok("vkCreateSwapchainKHR");
        if (old.Handle != 0)
        {
            khrSwapchain.DestroySwapchain(_dev, old, null);
        }

        uint ic = 0;
        khrSwapchain.GetSwapchainImages(_dev, _swapchain, ref ic, null);
        _swapImages = new Image[ic];
        fixed (Image* p = _swapImages)
        {
            khrSwapchain.GetSwapchainImages(_dev, _swapchain, ref ic, p);
        }
        // the semaphore a frame signals for its present belongs to the swapchain image, not to the frame in flight:
        // per frame, a frame signalled it again while the presentation of an earlier image could still be waiting on it
        // (validation: "may still be in use by VkSwapchainKHR")
        var sci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        while (_swapDone.Count < ic)
        {
            _vk.CreateSemaphore(_dev, in sci, null, out Silk.NET.Vulkan.Semaphore s).Ok("vkCreateSemaphore");
            _swapDone.Add(s);
        }
        _swapExtent = extent;
        _swapFormat = chosen.Format;
        _swapchainDirty = false;
        Log.Info($"Swapchain {extent.Width}x{extent.Height} {chosen.Format} {mode} ({ic} images)"
            + (_suboptimalExpected ? $", IDENTITY on a {caps.CurrentTransform} surface (the compositor rotates)" : ""));
    }

    public void NotifyResized() => _swapchainDirty = true;

    private readonly object _presentLock = new();

    /// <summary>
    /// The window surface went away while the renderer lives on (Android: SurfaceView.surfaceDestroyed when the
    /// activity goes to the background). Waits for the GPU, then destroys the swapchain and the VkSurfaceKHR; frames
    /// keep being rendered and submitted, without presenting, until <see cref="AttachSurface"/>. Call it from the thread
    /// that owns the window, before the native window is released.
    /// </summary>
    public void DetachSurface()
    {
        lock (_presentLock)
        {
            if (_core.SurfaceExt == null)
            {
                return;
            }

            DrainPresent();
            _vk.DeviceWaitIdle(_dev);
            DestroySwapchain();
            _core.ReplaceSurface(null);
            Log.Info("Presentation surface detached");
        }
    }

    /// <summary>A new window surface (Android: surfaceCreated after a <see cref="DetachSurface"/>): creates its
    /// VkSurfaceKHR and a swapchain for it. The renderer must have been created with a surface (instance extensions).</summary>
    public void AttachSurface(IPresentationSurface surface)
    {
        lock (_presentLock)
        {
            if (_core.SurfaceExt == null)
            {
                throw new InvalidOperationException("the renderer was created without a presentation surface");
            }

            DrainPresent();
            _vk.DeviceWaitIdle(_dev);
            DestroySwapchain();
            _core.ReplaceSurface(surface);
            _options.Surface = surface;
            CreateSwapchain();
            Log.Info("Presentation surface attached");
        }
    }

    private bool AcquireSwapchain(out uint index)
    {
        index = 0;
        DrainPresent(); // the swapchain is not used by the present thread and this acquire at once
        if (_swapchainDirty)
        {
            DrainPresent();
            _vk.DeviceWaitIdle(_dev);
            CreateSwapchain();
            if (_swapchainDirty)
            {
                return false;
            }
        }
        Result r = _core.SwapchainExt!.AcquireNextImage(_dev, _swapchain, 1_000_000_000, F.Acquire, default, ref index);
        NoteSwapchainResult("vkAcquireNextImageKHR", r);
        if (r is Result.ErrorOutOfDateKhr)
        {
            _swapchainDirty = true;
            return false;
        }
        if (r is not (Result.Success or Result.SuboptimalKhr))
        {
            return false;
        }

        if (r == Result.SuboptimalKhr && !_suboptimalExpected)
        {
            _swapchainDirty = true;
        }

        return true;
    }

    private void BlitToSwapchain(RenderTarget src, uint w, uint h, uint index)
    {
        Image dst = _swapImages[index];
        Barrier(dst, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        var clear = new ClearColorValue(0, 0, 0, 1);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        _vk.CmdClearColorImage(Cmd, dst, ImageLayout.TransferDstOptimal, in clear, 1, in range);
        Barrier(dst, ImageLayout.TransferDstOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferWriteBit);

        w = Math.Min(w, src.Width);
        h = Math.Min(h, src.Height);
        // letterbox, keep aspect ratio
        float scale = Math.Min((float)_swapExtent.Width / w, (float)_swapExtent.Height / h);
        int dw = (int)(w * scale), dh = (int)(h * scale);
        int dx = ((int)_swapExtent.Width - dw) / 2, dy = ((int)_swapExtent.Height - dh) / 2;
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
        // a target at a lower internal resolution is scaled up from its image (VulkanRenderer.ResScale.cs)
        if (src.Scale != 1f)
        {
            w = Math.Min(ScaleDim(w, src.Scale), src.ImgW);
            h = Math.Min(ScaleDim(h, src.Scale), src.ImgH);
        }
        blit.SrcOffsets[1] = new Offset3D((int)w, (int)h, 1);
        blit.DstOffsets[0] = new Offset3D(dx, dy, 0);
        blit.DstOffsets[1] = new Offset3D(dx + dw, dy + dh, 1);
        _vk.CmdBlitImage(Cmd, src.Image, ImageLayout.General, dst, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
        // later renderings and copies into the surface come after this read (write-after-read: an execution dependency;
        // synchronization validation on the Mali-G57 caught the next frames' render passes storing into it unordered)
        Barrier(src.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit, 0, 0);
        DrawOverlay(dst);
        Barrier(dst, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit, AccessFlags.TransferWriteBit, 0);
    }

    private void PresentSwapchain(uint index, Silk.NET.Vulkan.Semaphore wait)
    {
        if (s_presentThreadOn)
        {
            QueuePresentAsync(index, wait); // VulkanRenderer.PresentThread.cs
        }
        else
        {
            PresentNow(_swapchain, index, wait);
        }
    }

    /// <summary>vkQueuePresentKHR (the GPU worker, or the present thread with VITASHARP_PRESENT_THREAD=1).</summary>
    private void PresentNow(SwapchainKHR swapchainKHR, uint index, Silk.NET.Vulkan.Semaphore wait)
    {
        var pi = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &wait,
            SwapchainCount = 1,
            PSwapchains = &swapchainKHR,
            PImageIndices = &index,
        };
        Result r;
        lock (_queueLock)
        {
            r = _core.SwapchainExt!.QueuePresent(_core.Queue, in pi);
        }

        NoteSwapchainResult("vkQueuePresentKHR", r);
        if (r == Result.ErrorOutOfDateKhr || (r == Result.SuboptimalKhr && !_suboptimalExpected))
        {
            _swapchainDirty = true;
        }
    }

    private Result _lastAcquireResult, _lastPresentResult;

    /// <summary>Logs an acquire / present result when it differs from the previous one of that call (minimizing,
    /// restoring and losing fullscreen show up in the log once, not every frame).</summary>
    private void NoteSwapchainResult(string call, Result r)
    {
        ref Result last = ref call == "vkQueuePresentKHR" ? ref _lastPresentResult : ref _lastAcquireResult;
        if (r == last)
        {
            return;
        }

        last = r;
        Log.Info($"Swapchain: {call} -> {r}");
    }

    private void DestroySwapchain()
    {
        if (_swapchain.Handle != 0)
        {
            _core.SwapchainExt!.DestroySwapchain(_dev, _swapchain, null);
        }

        _swapchain = default;
    }
}
