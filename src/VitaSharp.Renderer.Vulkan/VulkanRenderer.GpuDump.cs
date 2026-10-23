using System.Buffers.Binary;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// GPU dump support (docs/notes/gpu-dump.md): render targets live on the GPU, so a dump that starts mid-game also
/// carries the contents of every colour target at that moment (<see cref="ExportDumpState"/>), and the replay
/// recreates them before the first command (<see cref="ImportDumpState"/>). Depth/stencil surfaces are not carried:
/// games clear or re-render them every frame.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    const uint DumpStateVersion = 1;

    /// <summary>Reads back every colour render target (GPU worker, at a frame boundary; waits for the GPU).</summary>
    public byte[]? ExportDumpState()
    {
        if (F.Recording) { SubmitFrame(false, 0); _drawsSinceSubmit = 0; }
        _vk.DeviceWaitIdle(_dev);
        var targets = _renderTargets.Values.OrderBy(t => t.LastScene).ThenBy(t => t.LastUsedFrame).ToList();
        var w = new DumpBuffer(1 << 20);
        w.U32(DumpStateVersion);
        w.U32((uint)targets.Count);
        var pci = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _core.QueueFamily };
        _vk.CreateCommandPool(_dev, in pci, null, out var pool);
        try
        {
            foreach (var rt in targets)
            {
                uint bpp = (uint)HostBytesPerPixel(rt.Format), bytes = rt.Width * rt.Height * bpp;
                w.U32(rt.Address); w.U32(rt.Width); w.U32(rt.Height); w.U32(rt.ColorFormat); w.U32(rt.Stride);
                w.I32((int)rt.Format); w.U32(bpp);
                var (buf, mem, ptr) = _core.CreateHostBuffer(bytes, BufferUsageFlags.TransferDstBit);
                var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
                _vk.AllocateCommandBuffers(_dev, in ai, out var cmd);
                var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
                _vk.BeginCommandBuffer(cmd, in bi);
                var region = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D(rt.Width, rt.Height, 1) };
                _vk.CmdCopyImageToBuffer(cmd, rt.Image, ImageLayout.General, buf, 1, in region);
                _vk.EndCommandBuffer(cmd);
                var si = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
                _vk.QueueSubmit(_core.Queue, 1, in si, default);
                _vk.QueueWaitIdle(_core.Queue);
                w.Bytes(new ReadOnlySpan<byte>((void*)ptr, (int)bytes));
                _core.DestroyBuffer(buf, mem);
                _vk.FreeCommandBuffers(_dev, pool, 1, in cmd);
            }
        }
        finally { _vk.DestroyCommandPool(_dev, pool, null); }
        Log.Info($"[gpudump] exported {targets.Count} render targets ({w.Length / 1048576.0:F1} MB)");
        return w.Span.ToArray();
    }

    /// <summary>Recreates the recorded render targets with their contents (replay, before the first command).</summary>
    public void ImportDumpState(ReadOnlySpan<byte> state)
    {
        var buffer = state.ToArray();
        var r = new DumpCursor();
        r.Reset(buffer, buffer.Length);
        uint version = r.U32();
        if (version != DumpStateVersion) { Log.Warn($"[replay] renderer state v{version} not supported: render targets start from guest memory"); return; }
        int count = (int)r.U32(), imported = 0;
        for (int i = 0; i < count; i++)
        {
            uint addr = r.U32(), w = r.U32(), h = r.U32(), colorFormat = r.U32(), stride = r.U32();
            var format = (Format)r.I32();
            uint bpp = r.U32();
            int offset = r.Skip((int)(w * h * bpp));
            var rt = GetRenderTarget(new ColorSurfaceInfo(addr, w, h, stride, colorFormat, 0, false, 0));
            rt.LastScene = ++_sceneSerial; // the recorded order: the newest target wins where targets overlap
            rt.LastUsedFrame = FrameCount;
            if (rt.Format != format || (uint)HostBytesPerPixel(rt.Format) != bpp)
            {
                Log.Warn($"[replay] render target 0x{addr:X8} {w}x{h} was {format} when recorded, {rt.Format} here: left as in guest memory");
                continue;
            }
            BeginFrameIfNeeded();
            EndRendering();
            var (buf, off) = UploadSpan(buffer.AsSpan(offset, (int)(w * h * bpp)), 16);
            Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, AccessFlags.MemoryWriteBit, AccessFlags.TransferWriteBit);
            var region = new BufferImageCopy
            {
                BufferOffset = off,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(w, h, 1),
            };
            _vk.CmdCopyBufferToImage(Cmd, buf, rt.Image, ImageLayout.General, 1, in region);
            Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
                AccessFlags.TransferWriteBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
            rt.Version++;
            rt.Dirty = true;
            imported++;
        }
        Log.Info($"[replay] imported {imported} of {count} render targets");
    }
}
