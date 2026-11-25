using Silk.NET.Vulkan;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Memory written by shader programs (USSE VLDST stores, <see cref="Core.Gxm.Shader.GeneratedShader.WritesMemory"/>)
/// reaches guest memory. On the Vita the USSE stores straight into the memory the buffer is mapped at, so once the
/// scene's GPU work is done the CPU reads what the program stored. Here the stores land in the renderer's mirror
/// (VulkanRenderer.Mapped.cs); each scene's stored ranges are copied out of the mirror at the scene's end and written
/// into guest memory when the frame retires, like the render-target writebacks published lazily (they do not hold
/// back the scene's notifications: no change to the guest's pacing).
///
/// A stored range is the program's whole mapped window (up to 1 MB from the slot's address), where the CPU writes
/// other data in the same frame. Only what the program changed is written: the words where the mirror differs from
/// guest memory, on the pages the CPU did not write since the store (the mirror equals guest memory there, apart from
/// the stores: a page the CPU wrote earlier was copied into the mirror again by the storing draw, see
/// <see cref="ExpireGpuWritten"/>). A page the CPU wrote since keeps the CPU's data. The writes do not mark the pages
/// written: they are the GPU's own data, which the mirror already has, and a mark would send the stored range back to
/// a guest copy that is older than newer stores still in flight.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary><see cref="EnvVars.GpuStoreWriteback"/>: "0" keeps the stores in the mirror only (A/B).</summary>
    private static readonly bool s_storeWriteback = Environment.GetEnvironmentVariable(EnvVars.GpuStoreWriteback) != "0";

    /// <summary>Ranges programs stored to in the current scene: guest start, end, mirror placement
    /// (<see cref="NoteGpuWritten"/>'s delta) and the dirty-page stamp of the store.</summary>
    private readonly List<(uint Start, uint End, uint Delta, uint Stamp)> _sceneStores = new();

    private long _storeWbBytes;

    /// <summary>Records [<paramref name="start"/>, <paramref name="end"/>) as stored by a draw of this scene (merged
    /// with the ranges of the same placement it overlaps or touches; the older stamp wins).</summary>
    private void NoteSceneStore(uint start, uint end, uint delta, uint stamp)
    {
        if (!s_storeWriteback || end <= start)
        {
            return;
        }

        for (int i = _sceneStores.Count - 1; i >= 0; i--)
        {
            (uint s, uint e, uint d, uint st) = _sceneStores[i];
            if (d != delta || e < start || s > end)
            {
                continue;
            }

            start = Math.Min(start, s);
            end = Math.Max(end, e);
            stamp = Math.Min(stamp, st);
            _sceneStores.RemoveAt(i);
        }
        _sceneStores.Add((start, end, delta, stamp));
    }

    /// <summary>The store area outside CDRAM starts over: the scene's ranges placed there no longer hold their stores.</summary>
    private void DropSceneStoresInStoreArea()
    {
        for (int i = _sceneStores.Count - 1; i >= 0; i--)
        {
            if (_sceneStores[i].Delta != CdramDelta)
            {
                _sceneStores.RemoveAt(i);
            }
        }
    }

    /// <summary>At the end of a scene: copies the ranges its programs stored to out of the mirror (written into guest
    /// memory by <see cref="CompleteStoreWritebacks"/> when the frame retires).</summary>
    private void ScheduleStoreWritebacks()
    {
        if (_sceneStores.Count == 0)
        {
            return;
        }

        if (_mirror.Handle == 0)
        {
            _sceneStores.Clear();
            return;
        }

        EndRendering();
        BufferBarrier(_mirror, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        foreach ((uint start, uint end, uint delta, uint stamp) in _sceneStores)
        {
            ulong offset = unchecked(start + delta);
            uint bytes = end - start;
            if (offset + bytes > MirrorBytes)
            {
                continue;
            }

            // buffer sizes rounded to 64 KB: the windows move every draw, the pool keeps a few sizes
            ulong size = ((ulong)bytes + 0xFFFF) & ~0xFFFFUL;
            if (!_writebackPool.TryGetValue(size, out Stack<RtWriteback>? pool))
            {
                _writebackPool[size] = pool = new();
            }

            _writebackPoolUsed[size] = FrameCount;
            RtWriteback? stored = pool.Count > 0 ? pool.Pop() : null;
            if (stored == null)
            {
                stored = new RtWriteback { Size = size };
                (stored.Buffer, stored.Memory, stored.Ptr) = _core.CreateReadbackBuffer(size, BufferUsageFlags.TransferDstBit, MemCat.Readback, out stored.Coherent);
            }
            stored.Address = start;
            stored.Width = bytes;
            stored.Rows = 1;
            stored.HostStride = bytes;
            stored.GuestStride = bytes;
            stored.GuestBpp = 1;
            stored.Convert = WritebackConvert.None;
            stored.Stamp = stamp;
            stored.Space = AddressSpace.Current;
            var region = new BufferCopy(offset, 0, bytes);
            _vk.CmdCopyBuffer(Cmd, _mirror, stored.Buffer, 1, in region);
            F.StoreWritebacks.Add(stored);
        }
        _sceneStores.Clear();
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferReadBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
    }

    /// <summary>Runs after the frame's fence signalled: writes what its programs stored into guest memory.</summary>
    private void CompleteStoreWritebacks(Frame f)
    {
        if (f.StoreWritebacks.Count == 0)
        {
            return;
        }

        SweepDirtyPages();
        foreach (RtWriteback stored in f.StoreWritebacks)
        {
            using (AddressSpace.Use(stored.Space))
            {
                if (!stored.Coherent)
                {
                    _core.InvalidateMapped(stored.Memory);
                }

                _storeWbBytes += PublishStored(new ReadOnlySpan<byte>((void*)stored.Ptr, (int)stored.Width), stored.Address, stored.Stamp);
            }
            _writebackPool[stored.Size].Push(stored);
        }
        f.StoreWritebacks.Clear();
    }

    /// <summary>
    /// Writes <paramref name="stored"/> (the mirror's copy of [<paramref name="addr"/>, + length) after the stores)
    /// into the calling thread's guest memory: the words that differ, on the pages the CPU did not write since
    /// <paramref name="stamp"/> (swept or still marked), without marking them. Returns the bytes written.
    /// </summary>
    public static int PublishStored(ReadOnlySpan<byte> stored, uint addr, uint stamp)
    {
        const uint Page = 1u << DirtyPages.PageShift;
        int written = 0;
        uint end = addr + (uint)stored.Length;
        for (uint at = addr; at < end;)
        {
            uint next = Math.Min((at & ~(Page - 1)) + Page, end);
            uint length = next - at;
            if (GuestMemory.IsMapped(at) && !DirtyPages.WrittenSince(at, length, stamp) && !DirtyPages.MarkedNow(at, length))
            {
                written += CopyChangedWords(stored.Slice((int)(at - addr), (int)length), new Span<byte>(GuestMemory.Ptr(at), (int)length));
            }

            at = next;
        }
        return written;
    }

    /// <summary>Copies the 4-byte words (and the bytes of a short tail) of <paramref name="stored"/> that differ from
    /// <paramref name="guest"/>, leaving equal words untouched (a CPU store racing into one of them stays); returns the
    /// bytes copied.</summary>
    public static int CopyChangedWords(ReadOnlySpan<byte> stored, Span<byte> guest)
    {
        const int Chunk = 64;
        int copied = 0;
        int i = 0;
        for (; i + Chunk <= stored.Length; i += Chunk)
        {
            if (stored.Slice(i, Chunk).SequenceEqual(guest.Slice(i, Chunk)))
            {
                continue;
            }

            ReadOnlySpan<uint> source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(stored.Slice(i, Chunk));
            Span<uint> target = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(guest.Slice(i, Chunk));
            for (int w = 0; w < source.Length; w++)
            {
                if (source[w] != target[w])
                {
                    target[w] = source[w];
                    copied += 4;
                }
            }
        }
        for (; i < stored.Length; i++)
        {
            if (stored[i] != guest[i])
            {
                guest[i] = stored[i];
                copied++;
            }
        }
        return copied;
    }
}
