using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The uniform arena's chunks live in one host-visible buffer of the renderer: a draw's uniform blocks are bound where
/// the guest side wrote them (dynamic offsets), instead of being copied into the upload ring on the GPU worker (most
/// of F1 2011's ~15 MB of uploads per race frame). A recorded frame holds a reference on each chunk it reads until
/// its fence retires; with the heap full, chunks are managed memory again and their blocks are copied as before.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    const int UniformHeapChunks = 48;

    sealed class UniformHeap(Buffer buffer, DeviceMemory memory, nint ptr) : IUniformHeap
    {
        public readonly Buffer Buffer = buffer;
        public readonly DeviceMemory Memory = memory;
        int _next;

        public nint Alloc(out int id)
        {
            id = _next;
            if (_next >= UniformHeapChunks)
            {
                return 0;
            }

            _next++;
            return ptr + (nint)id * UniformArena.ChunkSize;
        }
    }

    UniformHeap? _uniformHeap;
    /// <summary>Statistics (PERF sync): draws whose uniform blocks were bound in the heap / copied into the ring.</summary>
    long _statUniformInHeap, _statUniformCopied;

    void CreateUniformHeap()
    {
        var (b, m, p) = _core.CreateHostBuffer((ulong)UniformHeapChunks * UniformArena.ChunkSize, BufferUsageFlags.StorageBufferBit, MemCat.UploadRing);
        _uniformHeap = new UniformHeap(b, m, p);
        UniformArena.Attach(_uniformHeap, (int)Math.Max(_core.Properties.Limits.MinStorageBufferOffsetAlignment, 16));
    }

    void DestroyUniformHeap()
    {
        if (_uniformHeap is not { } h)
        {
            return;
        }

        UniformArena.Detach(h);
        _core.DestroyBuffer(h.Buffer, h.Memory);
        _uniformHeap = null;
    }

    /// <summary>The draw's uniform bindings 0/1: in place in the uniform heap, else copied into the upload ring.</summary>
    void DrawUniformInfos(DrawCommand d, DescriptorBufferInfo* infos)
    {
        if (ShaderGenerator.SplitUniformSlots)
        {
            SplitInfos(d, infos + 4);
        }

        if (d.UniformChunk is { } c && _uniformHeap is { } h && ReferenceEquals(c.Heap, h))
        {
            HoldForFrame(c);
            _statUniformInHeap++;
            ulong chunkOff = (ulong)c.HeapId * UniformArena.ChunkSize;
            infos[0] = d.VertexUniformsLength == 0 ? new DescriptorBufferInfo(_dummyBuffer, 0, 256) : new DescriptorBufferInfo(h.Buffer, chunkOff + (ulong)d.VertexUniformsOffset, (ulong)d.VertexUniformsLength);
            infos[1] = d.FragmentUniformsLength == 0 ? new DescriptorBufferInfo(_dummyBuffer, 0, 256) : new DescriptorBufferInfo(h.Buffer, chunkOff + (ulong)d.FragmentUniformsOffset, (ulong)d.FragmentUniformsLength);
            return;
        }
        if (d.VertexUniformsLength + d.FragmentUniformsLength > 0)
        {
            _statUniformCopied++; // (only split slots: nothing to bind here)
        }

        infos[0] = UniformInfo(d.VertexUniformSpan);
        infos[1] = UniformInfo(d.FragmentUniformSpan);
    }

    /// <summary>The frame reads the heap chunk until its fence retires (Deferred runs then).</summary>
    void HoldForFrame(UniformChunk c)
    {
        if (c.BackendTag == _frameSerial)
        {
            return;
        }

        c.BackendTag = _frameSerial;
        UniformArena.AddRef(c);
        F.Deferred.Add(() => UniformArena.Release(c));
    }

    /// <summary>Bindings 5-8 (<paramref name="infos"/> 0-3): the draw's split uniform slots, in place in the heap (several
    /// draws may share one), else copied into the upload ring; the dummy buffer where the stage has none.</summary>
    void SplitInfos(DrawCommand d, DescriptorBufferInfo* infos)
    {
        for (int i = 0; i < 4; i++)
        {
            if (d.SplitChunks[i] is not { } c)
            {
                infos[i] = new DescriptorBufferInfo(_dummyBuffer, 0, 256);
                continue;
            }
            if (_uniformHeap is { } h && ReferenceEquals(c.Heap, h))
            {
                HoldForFrame(c);
                infos[i] = new DescriptorBufferInfo(h.Buffer, (ulong)c.HeapId * UniformArena.ChunkSize + (ulong)d.SplitOffsets[i], (ulong)d.SplitSizes[i]);
            }
            else
            {
                infos[i] = UniformInfo(d.SplitSpan(i));
            }
        }
    }
}
