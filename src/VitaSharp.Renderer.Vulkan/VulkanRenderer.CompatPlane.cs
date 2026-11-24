using Silk.NET.Vulkan;
using System.Buffers;
using System.Buffers.Binary;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Plane 0 placed in a rectangle (<see cref="PresentCommand.Placement"/>): the framebuffer sceDisplaySetFrameBufForCompat
/// set (ScePspemu's PSP picture) is scaled into its rectangle of the 960x544 screen, black around it, as FW 3.74
/// display.skprx programs the head's plane 0 IFTU unit (a destination rectangle, the filter of the flags' bit 20). The
/// placed picture is a composite the shell's overlay (plane 1) is then blended over, and it is what is shown, recorded
/// and scanned again. A present without a placement does none of this.
/// <para>
/// The other compat format, <see cref="DisplayPixelFormat.A1B5G5R5"/> (16 bits), is uploaded from guest memory as
/// RGBA8: red sits in the low bits, which no format Vulkan guarantees holds as it is (A1R5G5B5 has red high), and the
/// display ignores its alpha.
/// </para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private RenderTarget? _compatComposite;
    private RenderTarget? _cpuFramebuffer16;
    /// <summary>The placement of the last present (the scanout's frames are placed the same way).</summary>
    private DisplayPlacement? _lastPlacement;

    /// <summary>A placed picture was composed (the composite exists): never without a placement.</summary>
    public bool PlacementComposed => _compatComposite != null;

    /// <summary>The screen-sized frame a placed picture is shown as (sizes of the swapchain blit, the readback and the
    /// frame summary); the address stays the source's.</summary>
    private static DisplayFrame ScreenFrame(DisplayFrame source) =>
        new(source.Address, DisplayPlacement.ScreenWidth, DisplayPlacement.ScreenWidth, DisplayPlacement.ScreenHeight, DisplayPixelFormat.A8B8G8R8);

    /// <summary>A 16-bit CPU framebuffer (<see cref="DisplayPixelFormat.A1B5G5R5"/>), its memory uploaded as RGBA8.</summary>
    private RenderTarget UploadCpuFramebuffer16(DisplayFrame frame)
    {
        RenderTarget? target = _cpuFramebuffer16;
        if (target == null || target.Width != frame.Width || target.Height != frame.Height)
        {
            if (target != null)
            {
                DestroyLater(target);
            }

            (Image image, ImageView view, ImageMemoryPool.Allocation memory) = CreateImage(MemCat.RenderTarget, frame.Width, frame.Height, Format.R8G8B8A8Unorm,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
            target = _cpuFramebuffer16 = new RenderTarget
            {
                Address = frame.Address,
                Width = frame.Width,
                Height = frame.Height,
                Stride = frame.Pitch,
                Image = image,
                View = view,
                Mem = memory,
                Format = Format.R8G8B8A8Unorm,
            };
        }

        target.Address = frame.Address;
        target.Stride = frame.Pitch;
        target.Version++;
        target.LastTouchedFrame = FrameCount;
        int rgbaBytes = (int)(frame.Width * frame.Height * 4);
        byte[] rgba = ArrayPool<byte>.Shared.Rent(rgbaBytes);
        ReadOnlySpan<byte> memory16 = GuestMemory.ReadSpan(frame.Address, DisplayScanout.Bytes(frame));
        A1B5G5R5ToRgba8(memory16, rgba.AsSpan(0, rgbaBytes), (int)frame.Width, (int)frame.Height, (int)frame.Pitch);
        (Buffer buffer, ulong offset) = UploadSpan(rgba.AsSpan(0, rgbaBytes), 16);
        ArrayPool<byte>.Shared.Return(rgba);

        Barrier(target.Image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            BufferOffset = offset,
            BufferRowLength = frame.Width,
            BufferImageHeight = frame.Height,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(frame.Width, frame.Height, 1),
        };
        _vk.CmdCopyBufferToImage(Cmd, buffer, target.Image, ImageLayout.General, 1, in region);
        Barrier(target.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit | AccessFlags.ShaderReadBit);
        return target;
    }

    /// <summary>A1B5G5R5 pixels (red in the low bits, <paramref name="pitch"/> pixels a row) to opaque RGBA8 rows of
    /// <paramref name="width"/> pixels: each 5-bit component widened as the display does (the top bits repeated).</summary>
    public static void A1B5G5R5ToRgba8(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int pitch)
    {
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = source.Slice(y * pitch * 2, width * 2);
            Span<byte> outRow = destination.Slice(y * width * 4, width * 4);
            for (int x = 0; x < width; x++)
            {
                int pixel = BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(x * 2, 2));
                int red = pixel & 0x1F;
                int green = (pixel >> 5) & 0x1F;
                int blue = (pixel >> 10) & 0x1F;
                outRow[x * 4] = (byte)((red << 3) | (red >> 2));
                outRow[x * 4 + 1] = (byte)((green << 3) | (green >> 2));
                outRow[x * 4 + 2] = (byte)((blue << 3) | (blue >> 2));
                outRow[x * 4 + 3] = 0xFF;
            }
        }
    }

    /// <summary><paramref name="src"/> scaled into <paramref name="placement"/>'s rectangle of a black screen.</summary>
    private RenderTarget ComposeCompatPlacement(RenderTarget src, DisplayPlacement placement)
    {
        uint screenWidth = ScaleDim(DisplayPlacement.ScreenWidth, src.Scale);
        uint screenHeight = ScaleDim(DisplayPlacement.ScreenHeight, src.Scale);
        RenderTarget? composite = _compatComposite;
        if (composite == null || composite.ImgW != screenWidth || composite.ImgH != screenHeight || composite.Format != src.Format || composite.Scale != src.Scale)
        {
            if (composite != null)
            {
                DestroyLater(composite);
            }

            (Image image, ImageView view, ImageMemoryPool.Allocation memory) = CreateImage(MemCat.RenderTarget, screenWidth, screenHeight, src.Format,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
            composite = _compatComposite = new RenderTarget
            {
                Width = DisplayPlacement.ScreenWidth,
                Height = DisplayPlacement.ScreenHeight,
                Stride = DisplayPlacement.ScreenWidth,
                Image = image,
                View = view,
                Mem = memory,
                Format = src.Format,
                Scale = src.Scale,
            };
        }

        composite.Address = src.Address;
        composite.ColorFormat = src.ColorFormat;
        composite.LastUsedFrame = composite.LastTouchedFrame = FrameCount;

        EndRendering();
        CommandBuffer cmd = Cmd;
        Barrier(src.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        Barrier(composite.Image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit | PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        var black = new ClearColorValue(0, 0, 0, 1);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        _vk.CmdClearColorImage(cmd, composite.Image, ImageLayout.General, in black, 1, in range);
        if (placement.TryClip(src.Width, src.Height, out (int X0, int Y0, int X1, int Y1) source, out (int X0, int Y0, int X1, int Y1) destination))
        {
            Barrier(composite.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferWriteBit);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };
            blit.SrcOffsets[0] = new Offset3D(ScaleOffset(source.X0, src.Scale, src.ImgW), ScaleOffset(source.Y0, src.Scale, src.ImgH), 0);
            blit.SrcOffsets[1] = new Offset3D(ScaleOffset(source.X1, src.Scale, src.ImgW), ScaleOffset(source.Y1, src.Scale, src.ImgH), 1);
            blit.DstOffsets[0] = new Offset3D(ScaleOffset(destination.X0, src.Scale, screenWidth), ScaleOffset(destination.Y0, src.Scale, screenHeight), 0);
            blit.DstOffsets[1] = new Offset3D(ScaleOffset(destination.X1, src.Scale, screenWidth), ScaleOffset(destination.Y1, src.Scale, screenHeight), 1);
            // the IFTU's bilinear filter (flags bit 20); the formats whose linear filtering Vulkan guarantees
            bool linear = placement.Bilinear && src.Format is Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm;
            _vk.CmdBlitImage(cmd, src.Image, ImageLayout.General, composite.Image, ImageLayout.General, 1, in blit, linear ? Filter.Linear : Filter.Nearest);
        }

        composite.Version++;
        Barrier(composite.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.TransferWriteBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
        // later renderings into the source come after this read
        Barrier(src.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit);
        return composite;
    }

    /// <summary>A guest-pixel offset in an image at <paramref name="scale"/>, within its <paramref name="limit"/>.</summary>
    private static int ScaleOffset(int offset, float scale, uint limit)
    {
        int scaled = scale == 1f ? offset : (int)MathF.Round(offset * scale);
        return Math.Clamp(scaled, 0, (int)limit);
    }
}
