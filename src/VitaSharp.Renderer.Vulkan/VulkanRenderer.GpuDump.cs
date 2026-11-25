using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// GPU dump support (docs/notes/gpu-dump.md): render targets live on the GPU, so a dump that starts mid-game also
/// carries the contents of every colour target at that moment (<see cref="ExportDumpState"/>), and the replay
/// recreates them before the first command (<see cref="ImportDumpState"/>). Depth/stencil surfaces are not carried:
/// games clear or re-render them every frame.
/// <para>Version 2 carries each target at its internal resolution (VulkanRenderer.ResScale.cs: a dump taken at a
/// resolution scale holds the scaled pixels, not a native copy): a replay at the same scale gets the same pixels, one
/// at another scale (or native) gets them resampled (nearest). Version 1 dumps (native only) still load.</para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private const uint DumpStateVersion = 2;

    /// <summary>Reads back every colour render target at its internal resolution (GPU worker, at a frame boundary;
    /// waits for the GPU).</summary>
    public byte[]? ExportDumpState()
    {
        if (F.Recording)
        {
            SubmitFrame(false, 0);
            _drawsSinceSubmit = 0;
        }
        DrainPresent();
        _core.WaitIdle();
        var targets = _renderTargets.Values.OrderBy(t => t.LastScene).ThenBy(t => t.LastUsedFrame).ToList();
        var w = new DumpBuffer(1 << 20);
        w.U32(DumpStateVersion);
        w.U32((uint)targets.Count);
        var pci = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _core.QueueFamily };
        _vk.CreateCommandPool(_dev, in pci, null, out CommandPool pool);
        try
        {
            foreach (RenderTarget? rt in targets)
            {
                uint bpp = (uint)HostBytesPerPixel(rt.Format);
                uint imageWidth = rt.ImgW, imageHeight = rt.ImgH, bytes = imageWidth * imageHeight * bpp;
                w.U32(rt.Address);
                w.U32(rt.Width);
                w.U32(rt.Height);
                w.U32(rt.ColorFormat);
                w.U32(rt.Stride);
                w.I32((int)rt.Format);
                w.U32(bpp);
                // v2: the image's own size (the internal resolution) and scale
                w.U32(imageWidth);
                w.U32(imageHeight);
                w.U32(BitConverter.SingleToUInt32Bits(rt.Scale));
                (Silk.NET.Vulkan.Buffer buf, DeviceMemory mem, nint ptr) = _core.CreateHostBuffer(bytes, BufferUsageFlags.TransferDstBit, MemCat.GpuDump);
                var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
                CommandBuffer cmd = default;
                try
                {
                    _vk.AllocateCommandBuffers(_dev, in ai, out cmd).Ok("vkAllocateCommandBuffers");
                    var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
                    _vk.BeginCommandBuffer(cmd, in bi);
                    var region = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D(imageWidth, imageHeight, 1) };
                    _vk.CmdCopyImageToBuffer(cmd, rt.Image, ImageLayout.General, buf, 1, in region);
                    _vk.EndCommandBuffer(cmd);
                    var si = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
                    _vk.QueueSubmit(_core.Queue, 1, in si, default).Ok("vkQueueSubmit (dump export)");
                    _vk.QueueWaitIdle(_core.Queue);
                    w.Bytes(new ReadOnlySpan<byte>((void*)ptr, (int)bytes));
                }
                finally
                {
                    // one buffer per target, released before the next (a failure must not leak it)
                    _core.DestroyBuffer(buf, mem);
                    if (cmd.Handle != 0)
                    {
                        _vk.FreeCommandBuffers(_dev, pool, 1, in cmd);
                    }
                }
            }
        }
        finally
        {
            _vk.DestroyCommandPool(_dev, pool, null);
        }
        Log.Info($"[gpudump] exported {targets.Count} render targets ({w.Length / 1048576.0:F1} MB{(_resScale != 1f ? $", at the resolution scale {_resScale:0.##}" : "")})");
        return w.Span.ToArray();
    }

    /// <summary>Recreates the recorded render targets with their contents (replay, before the first command).</summary>
    public void ImportDumpState(ReadOnlySpan<byte> state)
    {
        byte[] buffer = state.ToArray();
        var r = new DumpCursor();
        r.Reset(buffer, buffer.Length);
        uint version = r.U32();
        if (version is not (1 or DumpStateVersion))
        {
            Log.Warn($"[replay] renderer state v{version} not supported: render targets start from guest memory");
            return;
        }
        int count = (int)r.U32(), imported = 0, resampled = 0;
        for (int i = 0; i < count; i++)
        {
            uint addr = r.U32(), w = r.U32(), h = r.U32(), colorFormat = r.U32(), stride = r.U32();
            var format = (Format)r.I32();
            uint bpp = r.U32();
            uint imageWidth = w, imageHeight = h;
            if (version >= 2)
            {
                imageWidth = r.U32();
                imageHeight = r.U32();
                r.U32(); // the scale (the image size says it)
            }

            int length = (int)(imageWidth * imageHeight * bpp);
            int offset = r.Skip(length);
            RenderTarget rt = GetRenderTarget(new ColorSurfaceInfo(addr, w, h, stride, colorFormat, 0, false, 0));
            rt.LastScene = _sceneSerial = ++_sceneSerialNext; // the recorded order: the newest target wins where targets overlap
            rt.LastUsedFrame = FrameCount;
            if (rt.Format != format || (uint)HostBytesPerPixel(rt.Format) != bpp)
            {
                Log.Warn($"[replay] render target 0x{addr:X8} {w}x{h} was {format} when recorded, {rt.Format} here: left as in guest memory");
                continue;
            }

            ReadOnlySpan<byte> pixels = buffer.AsSpan(offset, length);
            // into the image itself at its size: the recorded pixels when the scales match, else resampled to it
            if (imageWidth != rt.ImgW || imageHeight != rt.ImgH)
            {
                pixels = Resample(pixels, imageWidth, imageHeight, rt.ImgW, rt.ImgH, (int)bpp);
                resampled++;
            }

            BeginFrameIfNeeded();
            EndRendering();
            (Silk.NET.Vulkan.Buffer buf, ulong off) = UploadSpan(pixels, 16);
            Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, AccessFlags.MemoryWriteBit, AccessFlags.TransferWriteBit);
            var region = new BufferImageCopy
            {
                BufferOffset = off,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(rt.ImgW, rt.ImgH, 1),
            };
            rt.Version++; // a native copy of a scaled target is refreshed from the image when it is needed
            _vk.CmdCopyBufferToImage(Cmd, buf, rt.Image, ImageLayout.General, 1, in region);
            Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
                AccessFlags.TransferWriteBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
            rt.Dirty = true;
            imported++;
        }
        Log.Info($"[replay] imported {imported} of {count} render targets" + (resampled > 0 ? $" ({resampled} recorded at another resolution scale, resampled)" : ""));
    }

    /// <summary>Nearest-neighbour resample of a <paramref name="sw"/> x <paramref name="sh"/> image of
    /// <paramref name="bpp"/>-byte pixels to <paramref name="dw"/> x <paramref name="dh"/> (whole pixels: raw bits kept).</summary>
    internal static byte[] Resample(ReadOnlySpan<byte> src, uint sw, uint sh, uint dw, uint dh, int bpp)
    {
        byte[] dst = new byte[dw * dh * (uint)bpp];
        for (uint y = 0; y < dh; y++)
        {
            uint sy = Math.Min(sh - 1, (uint)((y + 0.5) * sh / dh));
            for (uint x = 0; x < dw; x++)
            {
                uint sx = Math.Min(sw - 1, (uint)((x + 0.5) * sw / dw));
                src.Slice((int)((sy * sw + sx) * bpp), bpp).CopyTo(dst.AsSpan((int)((y * dw + x) * bpp), bpp));
            }
        }

        return dst;
    }
}
