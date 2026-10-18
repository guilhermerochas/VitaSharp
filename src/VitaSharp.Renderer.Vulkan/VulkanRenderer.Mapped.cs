using Silk.NET.Vulkan;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Large uniform buffers ("mapped" slots) are read in place instead of being copied per draw. Games point them at
/// render targets and depth surfaces to read rendered data with memory loads (downsampling, luminance, depth-aware
/// effects). Shaders read them from one device buffer that mirrors guest CDRAM, where those surfaces live: render
/// targets and depth surfaces are copied in (in guest layout) whenever they change, other CDRAM ranges come from
/// guest memory. Ranges outside CDRAM go to a per-frame scratch area at the end of the same buffer.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    const uint CdramBytes = GuestMemory.CdramEnd - GuestMemory.CdramStart;
    const uint ScratchBytes = 32 << 20;
    const int MappedWindowBytes = 1 << 20;

    Buffer _mirror;
    DeviceMemory _mirrorMem;
    uint _scratchUsed;
    long _scratchFrame = -1;
    readonly Dictionary<RenderTarget, long> _mirroredRt = new();
    readonly Dictionary<(DepthTarget, bool Stencil), long> _mirroredDepth = new();

    void EnsureMirror()
    {
        if (_mirror.Handle != 0) return;
        (_mirror, _mirrorMem) = _core.CreateDeviceBuffer((ulong)CdramBytes + ScratchBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit);
        // the last 256 bytes stay zero: slots bound to a null address read from there
        EndRendering();
        _vk.CmdFillBuffer(Cmd, _mirror, (ulong)CdramBytes + ScratchBytes - 256, 256, 0);
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
    }

    /// <summary>Fills the header of every mapped slot in <paramref name="data"/> and returns the stage's mapped-memory binding.</summary>
    DescriptorBufferInfo ResolveMappedSlots(List<UniformBufferSlot> slots, byte[] data, int dataLength, uint[] addrs)
    {
        var dummy = new DescriptorBufferInfo(_dummyBuffer, 0, 256);
        if (dataLength == 0 || addrs.Length == 0) return dummy;
        bool anyMapped = false;
        foreach (var s in slots) if (s.Mapped) { anyMapped = true; break; }
        if (!anyMapped) return dummy;
        EnsureMirror();
        if (_scratchFrame != FrameCount) { _scratchFrame = FrameCount; _scratchUsed = 0; }

        foreach (var s in slots)
        {
            if (!s.Mapped) continue;
            uint addr = addrs[Math.Min(s.GuestIndex, 14)];
            if (addr == 0) { WriteHeader(data, s, CdramBytes + ScratchBytes - 256); continue; }
            WriteHeader(data, s, PrepareMapped(addr, s.DeclaredBytes));
        }
        return new DescriptorBufferInfo(_mirror, 0, (ulong)CdramBytes + ScratchBytes);
    }

    /// <summary>Makes the data at <paramref name="addr"/> current in the mirror; returns its offset in the mirror buffer.</summary>
    uint PrepareMapped(uint addr, int declaredBytes)
    {
        if (addr is >= GuestMemory.CdramStart and < GuestMemory.CdramEnd)
        {
            if (RenderTargetContaining(addr) is { } rt && MirrorRenderTarget(rt))
            {
                Log.DebugOnce($"mapped-{addr:X8}", $"[gpu] mapped 0x{addr:X8}: render target 0x{rt.Address:X8} {rt.Width}x{rt.Height} {rt.Format}");
                return addr - GuestMemory.CdramStart;
            }
            if (DepthTargetContaining(addr, out bool stencil) is { } dt && MirrorDepthTarget(dt, stencil))
            {
                Log.DebugOnce($"mapped-{addr:X8}", $"[gpu] mapped 0x{addr:X8}: {(stencil ? "stencil" : "depth")} surface of {dt.Width}x{dt.Height}");
                return addr - GuestMemory.CdramStart;
            }
            Log.DebugOnce($"mapped-{addr:X8}", $"[gpu] mapped 0x{addr:X8}: guest memory");
            uint n = (uint)Math.Min(Math.Min(declaredBytes, MappedWindowBytes), GuestMemory.CdramEnd - addr);
            CopyGuestToMirror(addr, n, addr - GuestMemory.CdramStart);
            return addr - GuestMemory.CdramStart;
        }
        uint size = (uint)Math.Min(declaredBytes, MappedWindowBytes);
        if (_scratchUsed + size > ScratchBytes - 256) _scratchUsed = 0; // wrap: draws recorded earlier read their windows before these copies (ordered by barriers)
        uint off = CdramBytes + _scratchUsed;
        CopyGuestToMirror(addr, size, off);
        _scratchUsed += (size + 255) & ~255u;
        return off;
    }

    void CopyGuestToMirror(uint addr, uint size, uint mirrorOffset)
    {
        var (src, srcOff) = UploadSpan(GuestMemory.Span(addr, (int)size), 16);
        EndRendering();
        BufferBarrier(_mirror, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var region = new BufferCopy(srcOff, mirrorOffset, size);
        _vk.CmdCopyBuffer(Cmd, src, _mirror, 1, in region);
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
    }

    static void WriteHeader(byte[] data, UniformBufferSlot s, uint offset)
    {
        if (s.ByteOffset + 4 <= data.Length)
            BitConverter.TryWriteBytes(data.AsSpan(s.ByteOffset), ShaderGenerator.MappedFlag | offset);
    }

    RenderTarget? RenderTargetContaining(uint addr)
    {
        RenderTarget? best = null;
        foreach (var rt in _renderTargets.Values)
        {
            uint bytes = rt.Stride * (uint)Formats.ColorBytesPerPixel(rt.ColorFormat) * rt.Height;
            if (addr < rt.Address || addr >= rt.Address + bytes) continue;
            if (best == null || rt.LastUsedFrame > best.LastUsedFrame) best = rt;
        }
        return best;
    }

    DepthTarget? DepthTargetContaining(uint addr, out bool stencil)
    {
        DepthTarget? best = null;
        stencil = false;
        foreach (var dt in _depthTargets.Values)
        {
            bool inDepth = dt.Address != 0 && addr >= dt.Address && addr < dt.Address + dt.StrideBytes * dt.Height;
            bool inStencil = dt.StencilAddress != 0 && addr >= dt.StencilAddress && addr < dt.StencilAddress + dt.StencilStrideBytes * dt.Height;
            if (!inDepth && !inStencil) continue;
            if (best != null && dt.LastUsedFrame <= best.LastUsedFrame) continue;
            best = dt;
            stencil = !inDepth;
        }
        return best;
    }

    static int HostBytesPerPixel(Format f) => f switch
    {
        Format.R8Unorm or Format.R8SNorm => 1,
        Format.R8G8Unorm or Format.R8G8SNorm or Format.R16Unorm or Format.R16SNorm or Format.R16Sfloat
            or Format.R5G6B5UnormPack16 or Format.A1R5G5B5UnormPack16 or Format.R4G4B4A4UnormPack16 => 2,
        Format.R16G16B16A16Sfloat or Format.R32G32Sfloat => 8,
        _ => 4,
    };

    /// <summary>Copies a render target into the mirror (guest layout) when it changed. False when the layouts differ.</summary>
    bool MirrorRenderTarget(RenderTarget rt)
    {
        int guestBpp = Formats.ColorBytesPerPixel(rt.ColorFormat);
        if (rt.Format == Format.B8G8R8A8Unorm || HostBytesPerPixel(rt.Format) != guestBpp)
        {
            Log.WarnOnce($"mirror-fmt-{rt.ColorFormat:X8}", $"[gpu] render target 0x{rt.Address:X8} ({rt.Format}) cannot be read as memory: its layout differs from guest format 0x{rt.ColorFormat:X8}");
            return false;
        }
        if (_mirroredRt.TryGetValue(rt, out var v) && v == rt.Version) return true;
        _mirroredRt[rt] = rt.Version;
        rt.Dirty = true;
        SyncRenderTargetForRead(rt);
        EndRendering();
        BufferBarrier(_mirror, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            BufferOffset = rt.Address - GuestMemory.CdramStart,
            BufferRowLength = rt.Stride,
            BufferImageHeight = rt.Height,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(Math.Min(rt.Width, rt.Stride), rt.Height, 1),
        };
        _vk.CmdCopyImageToBuffer(Cmd, rt.Image, ImageLayout.General, _mirror, 1, in region);
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return true;
    }

    /// <summary>
    /// Copies a depth or stencil plane into the mirror. With D24S8 the depth aspect lands as X8U24 words and the
    /// stencil aspect as one byte per sample: the layouts of the Vita's S8D24 depth and S8 stencil surfaces.
    /// </summary>
    bool MirrorDepthTarget(DepthTarget dt, bool stencil)
    {
        if (!stencil && _core.DepthFormat != Format.D24UnormS8Uint)
        {
            Log.WarnOnce("mirror-depth-fmt", "[gpu] depth surfaces cannot be read as memory with a D32 depth buffer");
            return false;
        }
        if (_mirroredDepth.TryGetValue((dt, stencil), out var v) && v == dt.Version) return true;
        _mirroredDepth[(dt, stencil)] = dt.Version;
        EndRendering();
        const ImageAspectFlags ds = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        Barrier(dt.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.TransferSrcOptimal, ds,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.TransferBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.TransferReadBit);
        BufferBarrier(_mirror, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            BufferOffset = (stencil ? dt.StencilAddress : dt.Address) - GuestMemory.CdramStart,
            BufferRowLength = stencil ? dt.StencilStrideBytes : dt.StrideBytes / 4,
            BufferImageHeight = dt.Height,
            ImageSubresource = new ImageSubresourceLayers(stencil ? ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit, 0, 0, 1),
            ImageExtent = new Extent3D(Math.Min(dt.Width, stencil ? dt.StencilStrideBytes : dt.StrideBytes / 4), dt.Height, 1),
        };
        _vk.CmdCopyImageToBuffer(Cmd, dt.Image, ImageLayout.TransferSrcOptimal, _mirror, 1, in region);
        Barrier(dt.Image, ImageLayout.TransferSrcOptimal, ImageLayout.DepthStencilAttachmentOptimal, ds,
            PipelineStageFlags.TransferBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.TransferReadBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return true;
    }

    void BufferBarrier(Buffer b, PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
    {
        var bb = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier, Buffer = b, Offset = 0, Size = Vk.WholeSize,
            SrcAccessMask = srcAccess, DstAccessMask = dstAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        };
        _vk.CmdPipelineBarrier(Cmd, src, dst, 0, 0, null, 1, in bb, 0, null);
    }

    void DestroyMirror()
    {
        if (_mirror.Handle != 0) _core.DestroyBuffer(_mirror, _mirrorMem);
        _mirror = default;
    }
}
