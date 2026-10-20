using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    SwapchainKHR _swapchain;
    Image[] _swapImages = [];
    Extent2D _swapExtent;
    Format _swapFormat;
    bool _swapchainDirty;

    void CreateSwapchain()
    {
        var se = _core.SurfaceExt!;
        var sw = _core.SwapchainExt!;
        se.GetPhysicalDeviceSurfaceCapabilities(_core.Physical, _core.Surface, out var caps);
        uint fc = 0;
        se.GetPhysicalDeviceSurfaceFormats(_core.Physical, _core.Surface, ref fc, null);
        var formats = new SurfaceFormatKHR[fc];
        fixed (SurfaceFormatKHR* p = formats) se.GetPhysicalDeviceSurfaceFormats(_core.Physical, _core.Surface, ref fc, p);
        var chosen = formats.FirstOrDefault(f => f.Format == Format.B8G8R8A8Unorm, formats[0]);
        if (formats.Any(f => f.Format == Format.R8G8B8A8Unorm) && !formats.Any(f => f.Format == Format.B8G8R8A8Unorm))
            chosen = formats.First(f => f.Format == Format.R8G8B8A8Unorm);
        uint pc = 0;
        se.GetPhysicalDeviceSurfacePresentModes(_core.Physical, _core.Surface, ref pc, null);
        var modes = new PresentModeKHR[pc];
        fixed (PresentModeKHR* p = modes) se.GetPhysicalDeviceSurfacePresentModes(_core.Physical, _core.Surface, ref pc, p);
        var mode = _opt.VSync ? PresentModeKHR.FifoKhr : modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr
            : modes.Contains(PresentModeKHR.ImmediateKhr) ? PresentModeKHR.ImmediateKhr : PresentModeKHR.FifoKhr;

        var (w, h) = _opt.Surface!.FramebufferSize;
        var extent = caps.CurrentExtent.Width != uint.MaxValue ? caps.CurrentExtent
            : new Extent2D((uint)Math.Clamp(w, (int)caps.MinImageExtent.Width, (int)caps.MaxImageExtent.Width),
                           (uint)Math.Clamp(h, (int)caps.MinImageExtent.Height, (int)caps.MaxImageExtent.Height));
        if (extent.Width == 0 || extent.Height == 0) { _swapchainDirty = true; return; }
        uint count = Math.Max(caps.MinImageCount + 1, 3);
        if (caps.MaxImageCount > 0) count = Math.Min(count, caps.MaxImageCount);
        var old = _swapchain;
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
            PreTransform = caps.CurrentTransform,
            // opaque everywhere on desktop; Android surfaces typically only offer INHERIT
            CompositeAlpha = (caps.SupportedCompositeAlpha & CompositeAlphaFlagsKHR.OpaqueBitKhr) != 0 ? CompositeAlphaFlagsKHR.OpaqueBitKhr
                : (caps.SupportedCompositeAlpha & CompositeAlphaFlagsKHR.InheritBitKhr) != 0 ? CompositeAlphaFlagsKHR.InheritBitKhr
                : (CompositeAlphaFlagsKHR)((uint)caps.SupportedCompositeAlpha & (uint)-(int)caps.SupportedCompositeAlpha),
            PresentMode = mode,
            Clipped = true,
            OldSwapchain = old,
        };
        sw.CreateSwapchain(_dev, in ci, null, out _swapchain).Ok("vkCreateSwapchainKHR");
        if (old.Handle != 0) sw.DestroySwapchain(_dev, old, null);
        uint ic = 0;
        sw.GetSwapchainImages(_dev, _swapchain, ref ic, null);
        _swapImages = new Image[ic];
        fixed (Image* p = _swapImages) sw.GetSwapchainImages(_dev, _swapchain, ref ic, p);
        _swapExtent = extent;
        _swapFormat = chosen.Format;
        _swapchainDirty = false;
        Log.Info($"Swapchain {extent.Width}x{extent.Height} {chosen.Format} {mode} ({ic} images)");
    }

    public void NotifyResized() => _swapchainDirty = true;

    bool AcquireSwapchain(out uint index)
    {
        index = 0;
        if (_swapchainDirty)
        {
            _vk.DeviceWaitIdle(_dev);
            CreateSwapchain();
            if (_swapchainDirty) return false;
        }
        var r = _core.SwapchainExt!.AcquireNextImage(_dev, _swapchain, 1_000_000_000, F.Acquire, default, ref index);
        if (r is Result.ErrorOutOfDateKhr) { _swapchainDirty = true; return false; }
        if (r is not (Result.Success or Result.SuboptimalKhr)) return false;
        if (r == Result.SuboptimalKhr) _swapchainDirty = true;
        return true;
    }

    void BlitToSwapchain(RenderTarget src, uint w, uint h, uint index)
    {
        var dst = _swapImages[index];
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
        blit.SrcOffsets[1] = new Offset3D((int)w, (int)h, 1);
        blit.DstOffsets[0] = new Offset3D(dx, dy, 0);
        blit.DstOffsets[1] = new Offset3D(dx + dw, dy + dh, 1);
        _vk.CmdBlitImage(Cmd, src.Image, ImageLayout.General, dst, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
        Barrier(dst, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit, AccessFlags.TransferWriteBit, 0);
    }

    void PresentSwapchain(uint index, Silk.NET.Vulkan.Semaphore wait)
    {
        var sc = _swapchain;
        var pi = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &wait,
            SwapchainCount = 1,
            PSwapchains = &sc,
            PImageIndices = &index,
        };
        var r = _core.SwapchainExt!.QueuePresent(_core.Queue, in pi);
        if (r is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr) _swapchainDirty = true;
    }

    void DestroySwapchain()
    {
        if (_swapchain.Handle != 0) _core.SwapchainExt!.DestroySwapchain(_dev, _swapchain, null);
        _swapchain = default;
    }
}
