using Silk.NET.Vulkan;
using System.Text;
using VitaSharp.Core.Capture;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// What a replay asks the draw probe for (<c>replay-gpu --probe</c>, <see cref="DrawAttribution"/>): the surface shown
/// at the finding's present (guest address) and the finding's region in guest pixels. The replay sets
/// <see cref="Present"/> and <see cref="Draw"/> before every draw it executes (-1: not probed); the renderer adds one
/// <see cref="DrawAttribution.Probed"/> per probed draw into that surface to <see cref="Results"/> when its frame
/// retires.
/// </summary>
public sealed class DrawProbe
{
    /// <summary>The surface shown at <see cref="Present"/> (set with it: the neighbours of a double-buffered display
    /// show the other buffer).</summary>
    public uint Address;
    public required uint X, Y, Width, Height;
    public long Present = -1;
    public long Draw;

    /// <summary>
    /// The probe for the presents around <paramref name="present"/> (one before and one after) in the region of cell
    /// <paramref name="cell"/> of the 4x4 grid (-1: the whole picture), and the replay's draw filter that tells it,
    /// draw by draw, which present the draw is behind (<see cref="GpuDumpFrameIndex"/>: the draws behind a present may
    /// be issued a present or two before it) and which surface that present shows. Null when the present is not in the
    /// dump.
    /// </summary>
    public static DrawProbe? Around(GpuDumpFrameIndex index, long present, int cell, out Func<long, long, bool> executeDraw)
    {
        executeDraw = (_, _) => true;
        if (index[present] is not { } shown || shown.Width == 0 || shown.Height == 0)
        {
            return null;
        }

        (uint x, uint y, uint w, uint h) = DrawAttribution.CellRegion(cell, shown.Width, shown.Height);
        var probe = new DrawProbe { X = x, Y = y, Width = w, Height = h };
        var ranges = new List<(long First, long Last, long Present, uint Address)>();
        for (long p = present - 1; p <= present + 1; p++)
        {
            if (index[p] is { } f)
            {
                (long first, long last) = f.Mapped ? (f.FirstDraw, f.LastDraw) : (f.IntervalFirst, f.IntervalLast);
                ranges.Add((first, last, p, f.Address));
            }
        }

        executeDraw = (draw, _) =>
        {
            probe.Present = -1;
            probe.Draw = draw;
            foreach ((long first, long last, long p, uint address) in ranges)
            {
                if (draw >= first && draw <= last)
                {
                    probe.Present = p;
                    probe.Address = address;
                    break;
                }
            }

            return true;
        };
        return probe;
    }
    private readonly List<DrawAttribution.Probed> _results = new();

    public List<DrawAttribution.Probed> Results
    {
        get
        {
            lock (_results)
            {
                return new List<DrawAttribution.Probed>(_results);
            }
        }
    }

    internal void Add(DrawAttribution.Probed probed)
    {
        lock (_results)
        {
            _results.Add(probed);
        }
    }
}

/// <summary>
/// The draw probe (<see cref="DrawProbe"/>): around every probed draw into the probed surface, the region is copied
/// out before and after the draw (and the depth and stencil under it before), outside the rendering; when the frame
/// retires the copies are compared: the pixels the draw changed. Probing ends and resumes the rendering around each
/// probed draw (a replay diagnostic only). Off (no probe set) it costs one field test per draw.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>The probe of a replay (GPU thread; <c>replay-gpu --probe</c>), null when off.</summary>
    public DrawProbe? Probe { get; set; }

    private readonly record struct ProbeCopy(Silk.NET.Vulkan.Buffer Buf, DeviceMemory Mem, nint Ptr, int Bytes);

    private void Draw(DrawCommand d)
    {
        if (Probe is { Present: >= 0 } probe && _inScene && _renderTarget is { } rt && rt.Address == probe.Address)
        {
            ProbedDraw(d, probe, rt);
            return;
        }

        DrawCore(d);
    }

    private void ProbedDraw(DrawCommand d, DrawProbe probe, RenderTarget rt)
    {
        uint x = Math.Min(probe.X, rt.Width - 1), y = Math.Min(probe.Y, rt.Height - 1);
        uint w = Math.Clamp(probe.Width, 1, rt.Width - x), h = Math.Clamp(probe.Height, 1, rt.Height - y);
        int bpp = HostBytesPerPixel(rt.Format);
        BeginFrameIfNeeded();
        ProbeCopy before = CopyProbeRegion(rt, x, y, w, h, bpp);
        (ProbeCopy Depth, ProbeCopy Stencil)? depth = CopyProbeDepth(rt, x, y, w, h);
        long present = probe.Present, draw = probe.Draw;
        string state = ProbeState(d);
        ulong vs = d.VertexProgram.Info?.Program.Hash ?? 0, fs = d.FragmentProgram.Info?.Program.Hash ?? 0;
        DrawCore(d);
        ProbeCopy after = CopyProbeRegion(rt, x, y, w, h, bpp);
        int pixels = (int)(w * h);
        F.Deferred.Add(() =>
        {
            try
            {
                var a = new ReadOnlySpan<byte>((void*)before.Ptr, before.Bytes);
                var b = new ReadOnlySpan<byte>((void*)after.Ptr, after.Bytes);
                int changed = 0;
                for (int i = 0; i + bpp <= a.Length; i += bpp)
                {
                    if (!a.Slice(i, bpp).SequenceEqual(b.Slice(i, bpp)))
                    {
                        changed++;
                    }
                }

                double meanDepth = -1, meanStencil = -1;
                if (depth is { } ds)
                {
                    var dv = new ReadOnlySpan<uint>((void*)ds.Depth.Ptr, ds.Depth.Bytes / 4);
                    double sum = 0;
                    foreach (uint v in dv)
                    {
                        sum += (v & 0xFFFFFF) / (double)0xFFFFFF;
                    }

                    meanDepth = dv.Length > 0 ? sum / dv.Length : -1;
                    var sv = new ReadOnlySpan<byte>((void*)ds.Stencil.Ptr, ds.Stencil.Bytes);
                    double stencilSum = 0;
                    foreach (byte v in sv)
                    {
                        stencilSum += v;
                    }

                    meanStencil = sv.Length > 0 ? stencilSum / sv.Length : -1;
                }

                probe.Add(new DrawAttribution.Probed(present, draw, rt.Address, vs, fs, changed, pixels, meanDepth, meanStencil, state));
            }
            finally
            {
                _core.DestroyBuffer(before.Buf, before.Mem);
                _core.DestroyBuffer(after.Buf, after.Mem);
                if (depth is { } ds)
                {
                    _core.DestroyBuffer(ds.Depth.Buf, ds.Depth.Mem);
                    _core.DestroyBuffer(ds.Stencil.Buf, ds.Stencil.Mem);
                }
            }
        });
    }

    /// <summary>The region of <paramref name="rt"/> (guest pixels) into a host buffer, outside the rendering.</summary>
    private ProbeCopy CopyProbeRegion(RenderTarget rt, uint x, uint y, uint w, uint h, int bpp)
    {
        int bytes = (int)(w * h) * bpp;
        (Silk.NET.Vulkan.Buffer buf, DeviceMemory mem, nint ptr) = _core.CreateHostBuffer((ulong)bytes, BufferUsageFlags.TransferDstBit, MemCat.Readback);
        rt.Dirty = true;
        SyncRenderTargetForRead(rt);
        using (BeginOutside(reads: rt))
        {
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageOffset = new Offset3D((int)x, (int)y, 0),
                ImageExtent = new Extent3D(w, h, 1),
            };
            _vk.CmdCopyImageToBuffer(Cmd, NativeRectOf(rt, x, y, w, h), ImageLayout.General, buf, 1, in region);
        }

        BufferBarrier(buf, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, AccessFlags.TransferWriteBit, AccessFlags.HostReadBit);
        return new ProbeCopy(buf, mem, ptr, bytes);
    }

    /// <summary>The depth (D24 in 32 bits) and stencil under the region before the draw, null without a D24S8 depth
    /// surface (the layouts as the draw trace's snapshots: VulkanRenderer.TraceTextures.cs).</summary>
    private (ProbeCopy Depth, ProbeCopy Stencil)? CopyProbeDepth(RenderTarget rt, uint x, uint y, uint w, uint h)
    {
        if (_depth is not { } dt || _core.DepthFormat != Format.D24UnormS8Uint)
        {
            return null;
        }

        float scale = rt.Scale;
        uint dx = (uint)Math.Min(ScaledLo(x, scale), dt.Width - 1), dy = (uint)Math.Min(ScaledLo(y, scale), dt.Height - 1);
        uint dw = (uint)Math.Clamp(ScaledHi(x + w, scale) - dx, 1, dt.Width - dx), dh = (uint)Math.Clamp(ScaledHi(y + h, scale) - dy, 1, dt.Height - dy);
        (Silk.NET.Vulkan.Buffer dbuf, DeviceMemory dmem, nint dptr) = _core.CreateHostBuffer(dw * dh * 4, BufferUsageFlags.TransferDstBit, MemCat.Readback);
        (Silk.NET.Vulkan.Buffer sbuf, DeviceMemory smem, nint sptr) = _core.CreateHostBuffer(dw * dh, BufferUsageFlags.TransferDstBit, MemCat.Readback);
        using (BeginOutside(reads: rt, readsDepth: true))
        {
            const ImageAspectFlags DepthStencilAspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
            Barrier(dt.Image, ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.TransferSrcOptimal, DepthStencilAspect, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.TransferBit, AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.TransferReadBit);
            var depthRegion = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, 0, 1), ImageOffset = new Offset3D((int)dx, (int)dy, 0), ImageExtent = new Extent3D(dw, dh, 1) };
            _vk.CmdCopyImageToBuffer(Cmd, dt.Image, ImageLayout.TransferSrcOptimal, dbuf, 1, in depthRegion);
            var stencilRegion = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.StencilBit, 0, 0, 1), ImageOffset = new Offset3D((int)dx, (int)dy, 0), ImageExtent = new Extent3D(dw, dh, 1) };
            _vk.CmdCopyImageToBuffer(Cmd, dt.Image, ImageLayout.TransferSrcOptimal, sbuf, 1, in stencilRegion);
            Barrier(dt.Image, ImageLayout.TransferSrcOptimal, ImageLayout.DepthStencilAttachmentOptimal, DepthStencilAspect, PipelineStageFlags.TransferBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit, AccessFlags.TransferReadBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
        }

        BufferBarrier(dbuf, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, AccessFlags.TransferWriteBit, AccessFlags.HostReadBit);
        BufferBarrier(sbuf, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, AccessFlags.TransferWriteBit, AccessFlags.HostReadBit);
        return (new ProbeCopy(dbuf, dmem, dptr, (int)(dw * dh * 4)), new ProbeCopy(sbuf, smem, sptr, (int)(dw * dh)));
    }

    /// <summary>The draw's state as the probe reports it: <c>key=value</c> fields (the draw trace's names).</summary>
    private static string ProbeState(DrawCommand d)
    {
        DrawState s = d.State;
        var sb = new StringBuilder();
        sb.Append($"depth={s.Front.DepthFunc}/{s.Front.DepthWrite} st={StencilText(s.Front.Stencil)}/{StencilText(s.Back.Stencil)}");
        sb.Append($" blend={(d.FragmentProgram.Blend is { } b ? $"{b.ColorMask:X}:{b.ColorFunc}/{b.ColorSrc}/{b.ColorDst}" : "none")}");
        sb.Append($" fragEn={s.Front.FragmentEnabled} cull={s.CullMode} clip={s.RegionClipMode}[{s.ClipMinX},{s.ClipMinY}-{s.ClipMaxX},{s.ClipMaxY}] n={d.IndexCount}x{d.InstanceCount}");
        if (d.FragmentProgram.Info is { } info)
        {
            for (int i = 0; i < 16; i++)
            {
                if ((info.TexturesUsed & (1u << i)) != 0)
                {
                    sb.Append($" t{i}=0x{d.FragmentTextures[i].DataAddress:X8}:{d.FragmentTextures[i].Format:X8}");
                }
            }
        }

        return sb.ToString();
    }
}
