using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// A small host-drawn overlay (notifications such as trophy unlocks) copied onto the swapchain image after the game
/// frame, in the top-right corner. The front end rasterises it on the CPU (BGRA, premultiplied onto its own opaque
/// background) and hands it over with <see cref="SetOverlay"/>; null removes it. The copy is a plain buffer-to-image
/// transfer, so the overlay never touches the guest's render targets or the captured frames.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    sealed record OverlayImage(byte[] Bgra, int Width, int Height);

    OverlayImage? _overlay;
    readonly (Buffer Buffer, DeviceMemory Memory, nint Mapped, ulong Size)[] _overlayBufs = new (Buffer, DeviceMemory, nint, ulong)[FramesInFlight];

    /// <summary>Shows <paramref name="bgra"/> (width x height, B,G,R,A bytes) over the top-right corner of the window; null
    /// hides it. Any thread.</summary>
    public void SetOverlay(byte[]? bgra, int width, int height) =>
        Volatile.Write(ref _overlay, bgra == null || width <= 0 || height <= 0 || bgra.Length < width * height * 4 ? null : new OverlayImage(bgra, width, height));

    /// <summary>After the game frame was blitted (dst in TRANSFER_DST layout).</summary>
    void DrawOverlay(Image dst)
    {
        var o = Volatile.Read(ref _overlay);
        if (o == null) return;
        int margin = (int)Math.Max(8, _swapExtent.Height / 40);
        int w = Math.Min(o.Width, (int)_swapExtent.Width - margin), h = Math.Min(o.Height, (int)_swapExtent.Height - margin);
        if (w <= 0 || h <= 0) return;
        ulong size = (ulong)o.Width * (ulong)o.Height * 4;
        ref var slot = ref _overlayBufs[_frameIndex];
        if (slot.Size < size)
        {
            if (slot.Buffer.Handle != 0) { _vk.DestroyBuffer(_dev, slot.Buffer, null); _vk.FreeMemory(_dev, slot.Memory, null); }
            var (b, m, p) = _core.CreateHostBuffer(size, BufferUsageFlags.TransferSrcBit, MemCat.Other);
            slot = (b, m, p, size);
        }
        var span = new Span<byte>((void*)slot.Mapped, (int)size);
        o.Bgra.AsSpan(0, (int)size).CopyTo(span);
        if (_swapFormat is Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb)
            for (int i = 0; i < span.Length; i += 4) (span[i], span[i + 2]) = (span[i + 2], span[i]);
        var region = new BufferImageCopy
        {
            BufferOffset = 0, BufferRowLength = (uint)o.Width, BufferImageHeight = (uint)o.Height,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D((int)_swapExtent.Width - margin - w, margin, 0),
            ImageExtent = new Extent3D((uint)w, (uint)h, 1),
        };
        Barrier(dst, ImageLayout.TransferDstOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferWriteBit);
        _vk.CmdCopyBufferToImage(Cmd, slot.Buffer, dst, ImageLayout.TransferDstOptimal, 1, in region);
    }

    void DestroyOverlay()
    {
        for (int i = 0; i < _overlayBufs.Length; i++)
        {
            if (_overlayBufs[i].Buffer.Handle == 0) continue;
            _vk.DestroyBuffer(_dev, _overlayBufs[i].Buffer, null);
            _vk.FreeMemory(_dev, _overlayBufs[i].Memory, null);
            _overlayBufs[i] = default;
        }
    }
}
