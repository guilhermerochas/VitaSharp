using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Small host-drawn overlays copied onto the swapchain image after the game frame: a notification (trophy unlocks) in
/// the top-right corner, a status badge (the paused emulation) in the top-left one and a centred panel (the IME text
/// entry). The front end rasterises each on the CPU (BGRA,
/// premultiplied onto its own opaque background) and hands it over with <see cref="SetOverlay"/> /
/// <see cref="SetCenterOverlay"/>; null removes it. The copy is a plain buffer-to-image transfer, so an overlay never
/// touches the guest's render targets or the captured frames.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    sealed record OverlayImage(byte[] Bgra, int Width, int Height);

    OverlayImage? _overlay, _centerOverlay, _statusOverlay;
    readonly (Buffer Buffer, DeviceMemory Memory, nint Mapped, ulong Size)[] _overlayBufs = new (Buffer, DeviceMemory, nint, ulong)[FramesInFlight];
    readonly (Buffer Buffer, DeviceMemory Memory, nint Mapped, ulong Size)[] _centerOverlayBufs = new (Buffer, DeviceMemory, nint, ulong)[FramesInFlight];
    readonly (Buffer Buffer, DeviceMemory Memory, nint Mapped, ulong Size)[] _statusOverlayBufs = new (Buffer, DeviceMemory, nint, ulong)[FramesInFlight];

    static OverlayImage? NewOverlay(byte[]? bgra, int width, int height) =>
        bgra == null || width <= 0 || height <= 0 || bgra.Length < width * height * 4 ? null : new OverlayImage(bgra, width, height);

    /// <summary>Shows <paramref name="bgra"/> (width x height, B,G,R,A bytes) over the top-right corner of the window; null
    /// hides it. Any thread.</summary>
    public void SetOverlay(byte[]? bgra, int width, int height) => Volatile.Write(ref _overlay, NewOverlay(bgra, width, height));

    /// <summary>Like <see cref="SetOverlay"/>, centred horizontally in the upper part of the window (the IME text
    /// entry); drawn under the corner overlay. Any thread.</summary>
    public void SetCenterOverlay(byte[]? bgra, int width, int height) => Volatile.Write(ref _centerOverlay, NewOverlay(bgra, width, height));

    /// <summary>Like <see cref="SetOverlay"/>, in the top-left corner (a status badge: the paused emulation). While the
    /// emulation is paused the last frame is presented again with it (VulkanRenderer.Pause.cs). Any thread.</summary>
    public void SetStatusOverlay(byte[]? bgra, int width, int height) => Volatile.Write(ref _statusOverlay, NewOverlay(bgra, width, height));

    /// <summary>After the game frame was blitted (dst in TRANSFER_DST layout).</summary>
    void DrawOverlay(Image dst)
    {
        int margin = (int)Math.Max(8, _swapExtent.Height / 40);
        if (Volatile.Read(ref _statusOverlay) is { } st) CopyOverlay(dst, st, _statusOverlayBufs, margin, margin);
        if (Volatile.Read(ref _centerOverlay) is { } c)
        {
            int w = Math.Min(c.Width, (int)_swapExtent.Width - 2 * margin);
            // centred, in the upper third (the Vita's keyboard dialog sits at the top of the screen)
            CopyOverlay(dst, c, _centerOverlayBufs, Math.Max(margin, ((int)_swapExtent.Width - w) / 2), Math.Max(margin, (int)_swapExtent.Height / 6));
        }
        if (Volatile.Read(ref _overlay) is { } o)
        {
            int w = Math.Min(o.Width, (int)_swapExtent.Width - margin);
            CopyOverlay(dst, o, _overlayBufs, (int)_swapExtent.Width - margin - w, margin);
        }
    }

    void CopyOverlay(Image dst, OverlayImage o, (Buffer Buffer, DeviceMemory Memory, nint Mapped, ulong Size)[] bufs, int x, int y)
    {
        int w = Math.Min(o.Width, (int)_swapExtent.Width - x), h = Math.Min(o.Height, (int)_swapExtent.Height - y);
        if (w <= 0 || h <= 0) return;
        ulong size = (ulong)o.Width * (ulong)o.Height * 4;
        ref var slot = ref bufs[_frameIndex];
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
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D((uint)w, (uint)h, 1),
        };
        Barrier(dst, ImageLayout.TransferDstOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferWriteBit);
        _vk.CmdCopyBufferToImage(Cmd, slot.Buffer, dst, ImageLayout.TransferDstOptimal, 1, in region);
    }

    void DestroyOverlay()
    {
        foreach (var bufs in new[] { _overlayBufs, _centerOverlayBufs, _statusOverlayBufs })
            for (int i = 0; i < bufs.Length; i++)
            {
                if (bufs[i].Buffer.Handle == 0) continue;
                _vk.DestroyBuffer(_dev, bufs[i].Buffer, null);
                _vk.FreeMemory(_dev, bufs[i].Memory, null);
                bufs[i] = default;
            }
    }
}
