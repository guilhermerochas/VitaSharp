using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
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
    private const uint CdramBytes = GuestMemory.CdramEnd - GuestMemory.CdramStart;
    private const uint ScratchBytes = 32 << 20;
    private const int MappedWindowBytes = 1 << 20;

    private Buffer _mirror;
    private DeviceMemory _mirrorMem;
    private uint _scratchUsed;
    private long _scratchFrame = -1;
    private readonly Dictionary<RenderTarget, long> _mirroredRt = new();
    private readonly Dictionary<(DepthTarget, bool Stencil), long> _mirroredDepth = new();

    private void EnsureMirror()
    {
        if (_mirror.Handle != 0)
        {
            return;
        }

        (_mirror, _mirrorMem) = _core.CreateDeviceBuffer((ulong)CdramBytes + ScratchBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.VertexBufferBit, MemCat.PipelineBuffer);
        // the last 256 bytes stay zero: slots bound to a null address read from there
        EndRendering();
        _vk.CmdFillBuffer(Cmd, _mirror, (ulong)CdramBytes + ScratchBytes - 256, 256, 0);
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
    }

    /// <summary>Fills the header of every mapped slot in <paramref name="data"/> and returns the stage's mapped-memory binding.
    /// <paramref name="writes"/>: the program stores through its mapped slots (<see cref="GeneratedShader.WritesMemory"/>).</summary>
    private DescriptorBufferInfo ResolveMappedSlots(List<UniformBufferSlot> slots, Span<byte> data, uint[] addrs, bool writes = false)
    {
        var dummy = new DescriptorBufferInfo(_dummyBuffer, 0, 256);
        if (data.Length == 0 || addrs.Length == 0)
        {
            return dummy;
        }

        bool anyMapped = false;
        foreach (UniformBufferSlot s in slots)
        {
            if (s.Mapped)
            {
                anyMapped = true;
                break;
            }
        }

        if (!anyMapped)
        {
            return dummy;
        }

        EnsureMirror();
        if (_scratchFrame != FrameCount)
        {
            _scratchFrame = FrameCount;
            _scratchUsed = 0;
        }
        // a reader of memory an earlier draw's shader wrote waits for those writes
        if (!writes)
        {
            AwaitMirrorWrites(PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit, AccessFlags.ShaderReadBit);
        }

        foreach (UniformBufferSlot s in slots)
        {
            if (!s.Mapped)
            {
                continue;
            }

            uint addr = addrs[Math.Min(s.GuestIndex, 14)];
            if (addr == 0)
            {
                WriteHeader(data, s, CdramBytes + ScratchBytes - 256);
                continue;
            }
            WriteHeader(data, s, PrepareMapped(addr, s.DeclaredBytes, writes));
        }
        return new DescriptorBufferInfo(_mirror, 0, (ulong)CdramBytes + ScratchBytes);
    }

    // ------------------------------------------------------------------ memory written by shaders

    /// <summary>
    /// CDRAM ranges whose mirror content was written by a program's memory stores (<see cref="GeneratedShader.WritesMemory"/>),
    /// keyed by start: end and the dirty-page epoch when they were written. The guest memory there is stale (GPU stores do
    /// not reach it), so these ranges are not copied from guest memory again, and vertex streams inside them are read from
    /// the mirror. A CPU write to their pages since gives the memory back to the guest copy. Croixleur Sigma skins its
    /// meshes this way: point draws whose vertex program stores the transformed vertices, then the draws of the meshes
    /// with those vertices as a stream.
    /// </summary>
    private readonly SortedList<uint, (uint End, uint Stamp)> _gpuWritten = new();
    /// <summary>Shader writes into the mirror not yet made visible to later reads (barrier pending).</summary>
    private bool _mirrorWritesPending, _gpuWrittenStreamLogged;

    private void AwaitMirrorWrites(PipelineStageFlags dstStage, AccessFlags dstAccess)
    {
        if (!_mirrorWritesPending)
        {
            return;
        }

        _mirrorWritesPending = false;
        EndRendering();
        BufferBarrier(_mirror, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit, dstStage | PipelineStageFlags.TransferBit,
            AccessFlags.ShaderWriteBit, dstAccess | AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit);
    }

    /// <summary>
    /// Drops the shader-written ranges overlapping [start, end) whose pages inside [start, end) the CPU wrote since.
    /// Only the part being read counts: a stored range spans the program's whole mapped window (up to 1 MB from the
    /// slot's address) and the game writes other data in that megabyte every frame. With <paramref name="innerPages"/>
    /// (a vertex stream of three pages or more) the first and last pages do not count either, as they are shared
    /// with whatever lies next to the stream: Jak and Daxter extrudes its stencil shadow volumes with a storing
    /// program into a buffer that starts 1.7 KB after the volume's input vertices, which the CPU rewrites for the
    /// frame after next while the GPU worker is still on this one; testing that shared page dropped the range, the
    /// volume was drawn from the stale guest copy and the unbalanced stencil darkened everything below the horizon
    /// for a frame. A CPU rewrite of the stream itself (memory reused) touches its inner pages and still drops it.
    /// Only the pages the CPU wrote leave a range, not the whole range: the game double-buffers its volumes in two
    /// windows next to each other (the stored ranges of two frames merge into one), and a CPU write in the window of the
    /// other frame dropped the volume this frame had just extruded, so every other frame was dark for seconds.
    /// </summary>
    private void ExpireGpuWritten(uint start, uint end, bool innerPages = false)
    {
        if (_gpuWritten.Count == 0)
        {
            return;
        }

        SweepDirtyPages();
        const uint Page = 1u << DirtyPages.PageShift;
        for (int i = _gpuWritten.Count - 1; i >= 0; i--)
        {
            uint s = _gpuWritten.Keys[i];
            (uint e, uint stamp) = _gpuWritten.Values[i];
            if (e <= start || s >= end)
            {
                continue;
            }

            uint os = Math.Max(s, start), oe = Math.Min(e, end);
            if (innerPages)
            {
                os = (os + Page - 1 & ~(Page - 1)) + Page;
                oe &= ~(Page - 1);
            }
            if (oe > os && DirtyPages.WrittenBetween(os, oe - os, stamp, GpuQueue.ReadBound))
            {
                if (_diagActive != null)
                {
                    TraceLine($"[mirror] range 0x{s:X8}-0x{e:X8} stored by a program was written by the CPU since: its written pages go back to guest memory (before draw id={NextDrawId})");
                }

                _gpuWritten.RemoveAt(i);
                // only the pages the CPU wrote: the rest of the range keeps what the GPU stored there
                uint keep = s;
                for (uint p = os & ~(Page - 1); p < oe; p += Page)
                {
                    uint ps = Math.Max(p, os), pe = Math.Min(p + Page, oe);
                    if (!DirtyPages.WrittenBetween(ps, pe - ps, stamp, GpuQueue.ReadBound))
                    {
                        continue;
                    }

                    if (_diagActive != null)
                    {
                        TraceLine($"[mirror]   CPU-written 0x{ps:X8}-0x{pe:X8}");
                    }

                    if (ps > keep)
                    {
                        _gpuWritten[keep] = (ps, stamp);
                    }

                    keep = pe;
                }
                if (e > keep)
                {
                    _gpuWritten[keep] = (e, stamp);
                }
            }
        }
    }

    /// <summary>Records [start, end) as written by a shader (merged with the ranges it touches).</summary>
    private void NoteGpuWritten(uint start, uint end)
    {
        if (_gpuWritten.Count >= 4096)
        {
            _gpuWritten.Clear(); // runaway bookkeeping: back to guest copies
        }
        // the merged range gets a new stamp: the CPU writes since the older ranges were stored count first
        uint lo = start, hi = end;
        foreach ((uint s, (uint e, uint _)) in _gpuWritten)
        {
            if (e >= start && s <= end)
            {
                lo = Math.Min(lo, s);
                hi = Math.Max(hi, e);
            }
        }

        if (lo < start || hi > end)
        {
            ExpireGpuWritten(lo, hi);
        }

        for (int i = _gpuWritten.Count - 1; i >= 0; i--)
        {
            uint s = _gpuWritten.Keys[i];
            uint e = _gpuWritten.Values[i].End;
            if (e < start || s > end)
            {
                continue;
            }

            start = Math.Min(start, s);
            end = Math.Max(end, e);
            _gpuWritten.RemoveAt(i);
        }
        // stamped with the storing draw's scene end when known: the guest writes after it (the worker being behind)
        // are later than the store; before the first such scene, the sweep now
        uint bound = GpuQueue.ReadBound;
        _gpuWritten[start] = (end, bound != uint.MaxValue ? bound + 1 : DirtyPages.Epoch);
        GpuQueue.SceneEndSweeps = true;
        _mirrorWritesPending = true;
        if (_diagActive != null)
        {
            TraceLine($"[mirror] draw id={NextDrawId - 1} stores to 0x{start:X8}-0x{end:X8}: later streams there are read from the mirror");
        }
    }

    /// <summary>The mirror offset of a vertex stream that lies in shader-written memory, else -1 (read from guest memory).</summary>
    private long GpuWrittenStream(uint addr, uint length)
    {
        if (_gpuWritten.Count == 0 || addr < GuestMemory.CdramStart || (ulong)addr + length > GuestMemory.CdramEnd)
        {
            return -1;
        }

        ExpireGpuWritten(addr, addr + length, innerPages: length >= 3u << DirtyPages.PageShift);
        foreach ((uint s, (uint e, uint _)) in _gpuWritten)
        {
            if (s > addr)
            {
                break;
            }

            if (addr + length <= e)
            {
                AwaitMirrorWrites(PipelineStageFlags.VertexInputBit, AccessFlags.VertexAttributeReadBit);
                if (!_gpuWrittenStreamLogged)
                {
                    _gpuWrittenStreamLogged = true;
                    Log.Info($"[gpu] vertex stream 0x{addr:X8} ({length} bytes) is read from memory a program stored to");
                }
                return addr - GuestMemory.CdramStart;
            }
        }
        if (_diagActive != null)
        {
            foreach ((uint s, (uint e, uint _)) in _gpuWritten)
            {
                if (s < addr + length && addr < e)
                {
                    TraceLine($"[mirror] draw id={NextDrawId - 1} stream 0x{addr:X8}+{length} overlaps the stored range 0x{s:X8}-0x{e:X8} but is not inside it: read from guest memory");
                }
            }
        }

        return -1;
    }

    /// <summary>Copies [addr, addr + size) of guest CDRAM into the mirror except the parts shaders wrote.</summary>
    private void CopyGuestToMirrorExceptGpuWritten(uint addr, uint size)
    {
        uint end = addr + size, at = addr;
        ExpireGpuWritten(addr, end);
        foreach ((uint s, (uint e, uint _)) in _gpuWritten)
        {
            if (e <= at)
            {
                continue;
            }

            if (s >= end)
            {
                break;
            }

            if (s > at)
            {
                CopyGuestToMirror(at, s - at, at - GuestMemory.CdramStart);
            }

            at = Math.Max(at, e);
            if (at >= end)
            {
                return;
            }
        }
        if (at < end)
        {
            CopyGuestToMirror(at, end - at, at - GuestMemory.CdramStart);
        }
    }

    /// <summary>Makes the data at <paramref name="addr"/> current in the mirror; returns its offset in the mirror buffer.</summary>
    private uint PrepareMapped(uint addr, int declaredBytes, bool writes = false)
    {
        if (addr is >= GuestMemory.CdramStart and < GuestMemory.CdramEnd)
        {
            if (RenderTargetContaining(addr) is { } rt && MirrorRenderTarget(rt))
            {
                Log.DebugOnce($"mapped-{addr:X8}", $"[gpu] mapped 0x{addr:X8}: render target 0x{rt.Address:X8} {rt.Width}x{rt.Height} {rt.Format}");
                return addr - GuestMemory.CdramStart;
            }
            if (DepthTargetContaining(addr, out bool stencil) is { Scale: 1f } dt && MirrorDepthTarget(dt, stencil))
            {
                Log.DebugOnce($"mapped-{addr:X8}", $"[gpu] mapped 0x{addr:X8}: {(stencil ? "stencil" : "depth")} surface of {dt.Width}x{dt.Height}");
                return addr - GuestMemory.CdramStart;
            }
            Log.DebugOnce($"mapped-{addr:X8}", $"[gpu] mapped 0x{addr:X8}: guest memory");
            uint n = (uint)Math.Min(Math.Min(declaredBytes, MappedWindowBytes), GuestMemory.CdramEnd - addr);
            CopyGuestToMirrorExceptGpuWritten(addr, n);
            if (writes)
            {
                NoteGpuWritten(addr, addr + n);
            }

            return addr - GuestMemory.CdramStart;
        }
        if (writes)
        {
            Log.WarnOnce("mapped-store-scratch", $"[gpu] a program stores to memory at 0x{addr:X8}, outside CDRAM: later draws do not see what it wrote");
        }

        uint size = (uint)Math.Min(declaredBytes, MappedWindowBytes);
        if (_scratchUsed + size > ScratchBytes - 256)
        {
            _scratchUsed = 0; // wrap: draws recorded earlier read their windows before these copies (ordered by barriers)
        }

        uint off = CdramBytes + _scratchUsed;
        CopyGuestToMirror(addr, size, off);
        _scratchUsed += (size + 255) & ~255u;
        return off;
    }

    private void CopyGuestToMirror(uint addr, uint size, uint mirrorOffset)
    {
        (Buffer src, ulong srcOff) = UploadSpan(GuestMemory.ReadSpan(addr, (int)size), 16);
        _statMirrorCopyBytes += size;
        EndRendering();
        // shader stores into the mirror (other ranges) and the reads of earlier draws come before the copy
        BufferBarrier(_mirror, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit, AccessFlags.TransferWriteBit);
        var region = new BufferCopy(srcOff, mirrorOffset, size);
        _vk.CmdCopyBuffer(Cmd, src, _mirror, 1, in region);
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
    }

    private static void WriteHeader(Span<byte> data, UniformBufferSlot s, uint offset)
    {
        if (s.DataOffset + 4 <= data.Length)
        {
            BitConverter.TryWriteBytes(data[s.DataOffset..], ShaderGenerator.MappedFlag | offset);
        }
    }

    private RenderTarget? RenderTargetContaining(uint addr)
    {
        RenderTarget? best = null;
        if (s_texLookupOld)
        {
            foreach (RenderTarget rt in _renderTargets.Values)
            {
                uint bytes = rt.Stride * (uint)Formats.ColorBytesPerPixel(rt.ColorFormat) * rt.Height;
                if (addr < rt.Address || addr >= rt.Address + bytes)
                {
                    continue;
                }

                if (best == null || rt.LastUsedFrame > best.LastUsedFrame)
                {
                    best = rt;
                }
            }
            return best;
        }
        foreach (ref readonly RtRange r in RtRanges())
        {
            if (addr < r.Lo || addr >= r.Hi)
            {
                continue;
            }

            if (best == null || r.Rt.LastUsedFrame > best.LastUsedFrame)
            {
                best = r.Rt;
            }
        }
        return best;
    }

    /// <summary>The colour targets' memory ranges in <see cref="_renderTargets"/> order, rebuilt after the dictionary
    /// changes: the texture lookups of every draw test each target, and a dictionary walk with the size computed per
    /// target was a measurable part of the GPU worker on the phone.</summary>
    private ReadOnlySpan<RtRange> RtRanges()
    {
        if (_rtRangesValid)
        {
            return _rtRanges.AsSpan(0, _rtRangeCount);
        }

        if (_rtRanges.Length < _renderTargets.Count)
        {
            _rtRanges = new RtRange[Math.Max(16, _renderTargets.Count * 2)];
        }

        int n = 0;
        foreach (RenderTarget rt in _renderTargets.Values)
        {
            _rtRanges[n++] = new RtRange(rt.Address, rt.Address + rt.Stride * (uint)Formats.ColorBytesPerPixel(rt.ColorFormat) * rt.Height, rt);
        }

        _rtRangeCount = n;
        _rtRangesValid = true;
        return _rtRanges.AsSpan(0, n);
    }

    private readonly record struct RtRange(uint Lo, uint Hi, RenderTarget Rt);
    private RtRange[] _rtRanges = [];
    private int _rtRangeCount;
    /// <summary>Cleared wherever <see cref="_renderTargets"/> changes.</summary>
    private bool _rtRangesValid;

    private DepthTarget? DepthTargetContaining(uint addr, out bool stencil)
    {
        DepthTarget? best = null;
        stencil = false;
        foreach (DepthTarget dt in _depthTargets.Values)
        {
            bool inDepth = dt.Address != 0 && addr >= dt.Address && addr < dt.Address + dt.StrideBytes * dt.Height;
            bool inStencil = dt.StencilAddress != 0 && addr >= dt.StencilAddress && addr < dt.StencilAddress + dt.StencilStrideBytes * dt.Height;
            if (!inDepth && !inStencil)
            {
                continue;
            }

            if (best != null && dt.LastUsedFrame <= best.LastUsedFrame)
            {
                continue;
            }

            best = dt;
            stencil = !inDepth;
        }
        if (DebugMode.Enabled && best != null)
        {
            DbgDepthOverlap(addr, best, stencil);
        }

        return best != null && MultisampledDepthRead(best, stencil) ? null : best;
    }

    private static int HostBytesPerPixel(Format f) => f switch
    {
        Format.R8Unorm or Format.R8SNorm => 1,
        Format.R8G8Unorm or Format.R8G8SNorm or Format.R16Unorm or Format.R16SNorm or Format.R16Sfloat
            or Format.R5G6B5UnormPack16 or Format.A1R5G5B5UnormPack16 or Format.R4G4B4A4UnormPack16 => 2,
        Format.R16G16B16A16Sfloat or Format.R32G32Sfloat => 8,
        _ => 4,
    };

    /// <summary>Copies a render target into the mirror (guest layout) when it changed. False when the layouts differ.</summary>
    private bool MirrorRenderTarget(RenderTarget rt)
    {
        int guestBpp = Formats.ColorBytesPerPixel(rt.ColorFormat);
        if (rt.Format == Format.B8G8R8A8Unorm || HostBytesPerPixel(rt.Format) != guestBpp)
        {
            Log.WarnOnce($"mirror-fmt-{rt.ColorFormat:X8}", $"[gpu] render target 0x{rt.Address:X8} ({rt.Format}) cannot be read as memory: its layout differs from guest format 0x{rt.ColorFormat:X8}");
            return false;
        }
        rt.LastTouchedFrame = FrameCount;
        if (_mirroredRt.TryGetValue(rt, out long v) && v == rt.Version)
        {
            return true;
        }

        _mirroredRt[rt] = rt.Version;
        _mirroredRtScaled.Remove(rt); // (the scaled layout shares the memory: VulkanRenderer.ResScale.cs)
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
        DevStampOp();
        _vk.CmdCopyImageToBuffer(Cmd, NativeImageOf(rt), ImageLayout.General, _mirror, 1, in region); // guest size (VulkanRenderer.ResScale.cs)
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return true;
    }

    /// <summary>
    /// Copies a depth or stencil plane into the mirror. With D24S8 the depth aspect lands as X8U24 words and the
    /// stencil aspect as one byte per sample: the layouts of the Vita's S8D24 depth and S8 stencil surfaces.
    /// </summary>
    private bool MirrorDepthTarget(DepthTarget dt, bool stencil)
    {
        if (!stencil && _core.DepthFormat != Format.D24UnormS8Uint)
        {
            Log.WarnOnce("mirror-depth-fmt", "[gpu] depth surfaces cannot be read as memory with a D32 depth buffer");
            return false;
        }
        dt.LastTouchedFrame = FrameCount;
        if (_mirroredDepth.TryGetValue((dt, stencil), out long v) && v == dt.Version)
        {
            return true;
        }

        _mirroredDepth[(dt, stencil)] = dt.Version;
        EndRendering();
        const ImageAspectFlags DepthStencilAspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        Barrier(dt.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.TransferSrcOptimal, DepthStencilAspect,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.TransferBit,
            AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.TransferReadBit);
        BufferBarrier(_mirror, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            BufferOffset = (stencil ? dt.StencilAddress : dt.Address) - GuestMemory.CdramStart,
            BufferRowLength = dt.Scale != 1f ? dt.Width : stencil ? dt.StencilStrideBytes : dt.StrideBytes / 4, // scaled: packed rows (stencil textures only)
            BufferImageHeight = dt.Height,
            ImageSubresource = new ImageSubresourceLayers(stencil ? ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit, 0, 0, 1),
            ImageExtent = new Extent3D(dt.Scale != 1f ? dt.Width : Math.Min(dt.Width, stencil ? dt.StencilStrideBytes : dt.StrideBytes / 4), dt.Height, 1),
        };
        DevStampOp();
        _vk.CmdCopyImageToBuffer(Cmd, dt.Image, ImageLayout.TransferSrcOptimal, _mirror, 1, in region);
        Barrier(dt.Image, ImageLayout.TransferSrcOptimal, ImageLayout.DepthStencilAttachmentOptimal, DepthStencilAspect,
            PipelineStageFlags.TransferBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.TransferReadBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return true;
    }

    private void BufferBarrier(Buffer b, PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
    {
        var bb = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            Buffer = b,
            Offset = 0,
            Size = Vk.WholeSize,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        };
        _vk.CmdPipelineBarrier(Cmd, src, dst, 0, 0, null, 1, in bb, 0, null);
    }

    private void DestroyMirror()
    {
        if (_mirror.Handle != 0)
        {
            _core.DestroyBuffer(_mirror, _mirrorMem);
        }

        _mirror = default;
        DestroyStencilScratch();
    }
}
