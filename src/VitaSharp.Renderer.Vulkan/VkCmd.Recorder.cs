using System.Diagnostics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using VitaSharp.Core.Util;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// VITASHARP_REC_THREAD=1: the draw path's vkCmd* calls into a tiled scene rendering's secondary command buffer
/// (VulkanRenderer.Tiler.cs) are queued as small records and replayed by a recorder thread, so the GPU worker goes on
/// translating the next draws (textures, uploads, descriptor sets) while the driver encodes the previous ones. On the
/// phone the worker spent about 6 ms of its 24 ms per frame (Uncharted) inside those calls, on one core, while the
/// small cores sat idle.
/// <para>
/// The recorder owns the deferred secondary exclusively from its begin to its end: such secondaries come from a
/// command pool of their own per frame (Vulkan pools are externally synchronised), the worker calls <see cref="Drain"/>
/// before anything else touches one directly (executing it in the primary, recording into it outside this class,
/// submitting, resetting the pool), and a call that does not fit a record (more than 4 sets, 2 dynamic offsets or 128
/// bytes of push constants) drains and goes direct. Off by default.
/// </para>
/// </summary>
internal sealed unsafe partial class VkCmd
{
    internal static readonly bool RecorderRequested = Environment.GetEnvironmentVariable(EnvVars.RecThread)?.Trim() == "1";

    enum OpKind : byte
    {
        BindPipeline, BindSets, BindVertex, BindIndex, DrawIndexed, Push, Viewport, Scissor, Topology, Cull, FrontFace,
        LineWidth, DepthTest, DepthWrite, StencilTest, DepthBiasEnable, DepthCompare, DepthBias, StencilOp, CompareMask,
        WriteMask, Reference, BeginQuery, EndQuery, ByRegionBarrier, Begin, End,
    }

    struct Op
    {
        public OpKind Kind;
        public nint Cb;
        public uint U0, U1, U2, U3, U4, U5;
        public int I0;
        public float F0, F1, F2;
        public ulong H0, H1;
        public fixed byte Data[128];
    }

    const int RingSize = 8192;
    Op[]? _ring;
    long _head, _tail; // producer writes _head, the recorder _tail
    nint _deferHandle;
    Thread? _thread;
    readonly SemaphoreSlim _wake = new(0);
    volatile bool _sleeping;
    Exception? _error;
    long _statOps, _statDrains, _statDrainWaits, _statDrainTicks, _statFull;

    public bool RecorderOn => _ring != null;

    /// <summary>Starts the recorder thread (once).</summary>
    public void StartRecorder()
    {
        if (_ring != null) return;
        _ring = new Op[RingSize];
        _thread = new Thread(RecorderLoop) { IsBackground = true, Name = "GPU recorder" };
        _thread.Start();
        Log.Info("[gpu] VITASHARP_REC_THREAD: draw commands of tiled renderings are recorded on a second thread");
    }

    /// <summary>From now on, calls on <paramref name="c"/> are queued (default: none).</summary>
    public void DeferTo(CommandBuffer c) => _deferHandle = c.Handle;

    public bool Deferring => _deferHandle != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ref Op Push(OpKind kind, CommandBuffer c)
    {
        var ring = _ring!;
        long head = _head;
        if (head - Volatile.Read(ref _tail) >= RingSize)
        {
            _statFull++;
            var sw = new SpinWait();
            while (head - Volatile.Read(ref _tail) >= RingSize) sw.SpinOnce();
        }
        ref var o = ref ring[head & (RingSize - 1)];
        o.Kind = kind;
        o.Cb = c.Handle;
        return ref o;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Commit()
    {
        Volatile.Write(ref _head, _head + 1);
        _statOps++;
        if (_sleeping) { _sleeping = false; _wake.Release(); }
    }

    /// <summary>Waits until the recorder replayed everything queued (the deferred buffers may then be used directly).</summary>
    public void Drain()
    {
        if (_ring == null) return;
        _statDrains++;
        if (Volatile.Read(ref _tail) != _head)
        {
            _statDrainWaits++;
            long t0 = Stopwatch.GetTimestamp();
            var sw = new SpinWait();
            while (Volatile.Read(ref _tail) != _head)
            {
                if (_sleeping) { _sleeping = false; _wake.Release(); }
                sw.SpinOnce(-1);
            }
            _statDrainTicks += Stopwatch.GetTimestamp() - t0;
        }
        if (_error is { } e) { _error = null; throw new InvalidOperationException("GPU recorder thread failed", e); }
    }

    void RecorderLoop()
    {
        var ring = _ring!;
        long tail = 0;
        while (true)
        {
            int spins = 0;
            while (Volatile.Read(ref _head) == tail)
            {
                if (++spins < 64) { Thread.SpinWait(32); continue; }
                _sleeping = true;
                if (Volatile.Read(ref _head) != tail) { _sleeping = false; break; }
                _wake.Wait(2);
                spins = 0;
            }
            try { Exec(ref ring[tail & (RingSize - 1)]); }
            catch (Exception e) { _error ??= e; }
            tail++;
            Volatile.Write(ref _tail, tail);
        }
    }

    public string RecorderReport() => _ring == null ? "" :
        $"; recorder thread: {_statOps} commands, {_statDrains} drains ({_statDrainWaits} waited, {_statDrainTicks * 1000.0 / Stopwatch.Frequency:F0} ms), {_statFull} ring-full waits";

    // ------------------------------------------------------------------ queued calls

    void DeferCmdBindPipeline(CommandBuffer c, PipelineBindPoint bp, Pipeline p)
    { ref var o = ref Push(OpKind.BindPipeline, c); o.U0 = (uint)bp; o.H0 = p.Handle; Commit(); }

    void DeferCmdBindDescriptorSets(CommandBuffer c, PipelineBindPoint bp, PipelineLayout l, uint first, uint count, DescriptorSet* sets, uint dynCount, uint* dyn)
    {
        if (count > 4 || dynCount > 2) { Drain(); if (_direct) _bindDescriptorSets(c, bp, l, first, count, sets, dynCount, dyn); else _vk.CmdBindDescriptorSets(c, bp, l, first, count, sets, dynCount, dyn); return; }
        ref var o = ref Push(OpKind.BindSets, c);
        o.U0 = (uint)bp; o.H0 = l.Handle; o.U1 = first; o.U2 = count; o.U3 = dynCount;
        fixed (byte* d = o.Data)
        {
            var s = (ulong*)d;
            for (int i = 0; i < count; i++) s[i] = sets[i].Handle;
            var dy = (uint*)(d + 32);
            for (int i = 0; i < dynCount; i++) dy[i] = dyn[i];
        }
        Commit();
    }

    void DeferCmdBindVertexBuffers(CommandBuffer c, uint first, uint count, Buffer* b, ulong* off)
    {
        if (count > 8) { Drain(); if (_direct) _bindVertexBuffers(c, first, count, b, off); else _vk.CmdBindVertexBuffers(c, first, count, b, off); return; }
        ref var o = ref Push(OpKind.BindVertex, c);
        o.U0 = first; o.U1 = count;
        fixed (byte* d = o.Data)
        {
            var bb = (ulong*)d; var oo = (ulong*)(d + 64);
            for (int i = 0; i < count; i++) { bb[i] = b[i].Handle; oo[i] = off[i]; }
        }
        Commit();
    }

    void DeferCmdBindIndexBuffer(CommandBuffer c, Buffer b, ulong off, IndexType t)
    { ref var o = ref Push(OpKind.BindIndex, c); o.H0 = b.Handle; o.H1 = off; o.U0 = (uint)t; Commit(); }

    void DeferCmdDrawIndexed(CommandBuffer c, uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    { ref var o = ref Push(OpKind.DrawIndexed, c); o.U0 = indexCount; o.U1 = instanceCount; o.U2 = firstIndex; o.I0 = vertexOffset; o.U3 = firstInstance; Commit(); }

    void DeferCmdPushConstants(CommandBuffer c, PipelineLayout l, ShaderStageFlags s, uint offset, uint size, void* data)
    {
        if (size > 128) { Drain(); if (_direct) _pushConstants(c, l, s, offset, size, data); else _vk.CmdPushConstants(c, l, s, offset, size, data); return; }
        ref var o = ref Push(OpKind.Push, c);
        o.H0 = l.Handle; o.U0 = (uint)s; o.U1 = offset; o.U2 = size;
        fixed (byte* d = o.Data) System.Buffer.MemoryCopy(data, d, 128, size);
        Commit();
    }

    void DeferCmdSetViewport(CommandBuffer c, uint first, uint count, in Viewport v)
    {
        ref var o = ref Push(OpKind.Viewport, c);
        o.U0 = first; o.U1 = count;
        fixed (byte* d = o.Data) *(Viewport*)d = v;
        Commit();
    }

    void DeferCmdSetScissor(CommandBuffer c, uint first, uint count, in Rect2D r)
    {
        ref var o = ref Push(OpKind.Scissor, c);
        o.U0 = first; o.U1 = count;
        fixed (byte* d = o.Data) *(Rect2D*)d = r;
        Commit();
    }

    void DeferCmdSetPrimitiveTopology(CommandBuffer c, PrimitiveTopology t) { ref var o = ref Push(OpKind.Topology, c); o.U0 = (uint)t; Commit(); }
    void DeferCmdSetCullMode(CommandBuffer c, CullModeFlags m) { ref var o = ref Push(OpKind.Cull, c); o.U0 = (uint)m; Commit(); }
    void DeferCmdSetFrontFace(CommandBuffer c, FrontFace f) { ref var o = ref Push(OpKind.FrontFace, c); o.U0 = (uint)f; Commit(); }
    void DeferCmdSetLineWidth(CommandBuffer c, float w) { ref var o = ref Push(OpKind.LineWidth, c); o.F0 = w; Commit(); }
    void DeferCmdSetDepthTestEnable(CommandBuffer c, Silk.NET.Core.Bool32 e) { ref var o = ref Push(OpKind.DepthTest, c); o.U0 = e ? 1u : 0u; Commit(); }
    void DeferCmdSetDepthWriteEnable(CommandBuffer c, Silk.NET.Core.Bool32 e) { ref var o = ref Push(OpKind.DepthWrite, c); o.U0 = e ? 1u : 0u; Commit(); }
    void DeferCmdSetStencilTestEnable(CommandBuffer c, Silk.NET.Core.Bool32 e) { ref var o = ref Push(OpKind.StencilTest, c); o.U0 = e ? 1u : 0u; Commit(); }
    void DeferCmdSetDepthBiasEnable(CommandBuffer c, Silk.NET.Core.Bool32 e) { ref var o = ref Push(OpKind.DepthBiasEnable, c); o.U0 = e ? 1u : 0u; Commit(); }
    void DeferCmdSetDepthCompareOp(CommandBuffer c, CompareOp op) { ref var o = ref Push(OpKind.DepthCompare, c); o.U0 = (uint)op; Commit(); }
    void DeferCmdSetDepthBias(CommandBuffer c, float constant, float clamp, float slope) { ref var o = ref Push(OpKind.DepthBias, c); o.F0 = constant; o.F1 = clamp; o.F2 = slope; Commit(); }

    void DeferCmdSetStencilOp(CommandBuffer c, StencilFaceFlags f, StencilOp fail, StencilOp pass, StencilOp depthFail, CompareOp op)
    { ref var o = ref Push(OpKind.StencilOp, c); o.U0 = (uint)f; o.U1 = (uint)fail; o.U2 = (uint)pass; o.U3 = (uint)depthFail; o.U4 = (uint)op; Commit(); }

    void DeferCmdSetStencilCompareMask(CommandBuffer c, StencilFaceFlags f, uint m) { ref var o = ref Push(OpKind.CompareMask, c); o.U0 = (uint)f; o.U1 = m; Commit(); }
    void DeferCmdSetStencilWriteMask(CommandBuffer c, StencilFaceFlags f, uint m) { ref var o = ref Push(OpKind.WriteMask, c); o.U0 = (uint)f; o.U1 = m; Commit(); }
    void DeferCmdSetStencilReference(CommandBuffer c, StencilFaceFlags f, uint r) { ref var o = ref Push(OpKind.Reference, c); o.U0 = (uint)f; o.U1 = r; Commit(); }

    // ------------------------------------------------------------------ calls the draw path made through Silk.NET directly

    public void CmdBeginQuery(CommandBuffer c, QueryPool pool, uint query, QueryControlFlags flags)
    {
        if (c.Handle == _deferHandle) { ref var o = ref Push(OpKind.BeginQuery, c); o.H0 = pool.Handle; o.U0 = query; o.U1 = (uint)flags; Commit(); return; }
        _vk.CmdBeginQuery(c, pool, query, flags);
    }

    public void CmdEndQuery(CommandBuffer c, QueryPool pool, uint query)
    {
        if (c.Handle == _deferHandle) { ref var o = ref Push(OpKind.EndQuery, c); o.H0 = pool.Handle; o.U0 = query; Commit(); return; }
        _vk.CmdEndQuery(c, pool, query);
    }

    /// <summary>A by-region colour-write to input-attachment-read memory barrier (programmable blending inside a pass).</summary>
    public void CmdInputFetchBarrier(CommandBuffer c)
    {
        if (c.Handle == _deferHandle) { Push(OpKind.ByRegionBarrier, c); Commit(); return; }
        InputFetchBarrierNow(c);
    }

    void InputFetchBarrierNow(CommandBuffer c)
    {
        var mb = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ColorAttachmentWriteBit, DstAccessMask = AccessFlags.InputAttachmentReadBit };
        _vk.CmdPipelineBarrier(c, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit, DependencyFlags.ByRegionBit, 1, in mb, 0, null, 0, null);
    }

    /// <summary>Queues vkBeginCommandBuffer of a render-pass-continue secondary and defers the calls on it from now on.
    /// <paramref name="renderPass"/> set: it inherits that pass (subpass 0); else dynamic rendering with these formats.</summary>
    public void BeginDeferredSecondary(CommandBuffer c, RenderPass renderPass, uint colorCount, Format color0, Format color1, Format depth)
    {
        ref var o = ref Push(OpKind.Begin, c);
        o.H0 = renderPass.Handle; o.U0 = colorCount; o.U1 = (uint)color0; o.U2 = (uint)color1; o.U3 = (uint)depth;
        Commit();
        _deferHandle = c.Handle;
    }

    /// <summary>Queues vkEndCommandBuffer of the deferred secondary and stops deferring.</summary>
    public void EndDeferredSecondary(CommandBuffer c)
    {
        Push(OpKind.End, c);
        Commit();
        _deferHandle = 0;
    }

    // ------------------------------------------------------------------ replay (recorder thread)

    void Exec(ref Op o)
    {
        var c = new CommandBuffer(o.Cb);
        switch (o.Kind)
        {
            case OpKind.BindPipeline:
                var p = new Pipeline(o.H0);
                if (_direct) _bindPipeline(c, (PipelineBindPoint)o.U0, p); else _vk.CmdBindPipeline(c, (PipelineBindPoint)o.U0, p);
                break;
            case OpKind.BindSets:
                fixed (byte* d = o.Data)
                {
                    var l = new PipelineLayout(o.H0);
                    if (_direct) _bindDescriptorSets(c, (PipelineBindPoint)o.U0, l, o.U1, o.U2, (DescriptorSet*)d, o.U3, (uint*)(d + 32));
                    else _vk.CmdBindDescriptorSets(c, (PipelineBindPoint)o.U0, l, o.U1, o.U2, (DescriptorSet*)d, o.U3, (uint*)(d + 32));
                }
                break;
            case OpKind.BindVertex:
                fixed (byte* d = o.Data)
                {
                    if (_direct) _bindVertexBuffers(c, o.U0, o.U1, (Buffer*)d, (ulong*)(d + 64));
                    else _vk.CmdBindVertexBuffers(c, o.U0, o.U1, (Buffer*)d, (ulong*)(d + 64));
                }
                break;
            case OpKind.BindIndex:
                if (_direct) _bindIndexBuffer(c, new Buffer(o.H0), o.H1, (IndexType)o.U0); else _vk.CmdBindIndexBuffer(c, new Buffer(o.H0), o.H1, (IndexType)o.U0);
                break;
            case OpKind.DrawIndexed:
                if (_direct) _drawIndexed(c, o.U0, o.U1, o.U2, o.I0, o.U3); else _vk.CmdDrawIndexed(c, o.U0, o.U1, o.U2, o.I0, o.U3);
                break;
            case OpKind.Push:
                fixed (byte* d = o.Data)
                {
                    if (_direct) _pushConstants(c, new PipelineLayout(o.H0), (ShaderStageFlags)o.U0, o.U1, o.U2, d);
                    else _vk.CmdPushConstants(c, new PipelineLayout(o.H0), (ShaderStageFlags)o.U0, o.U1, o.U2, d);
                }
                break;
            case OpKind.Viewport:
                fixed (byte* d = o.Data) { if (_direct) _setViewport(c, o.U0, o.U1, (Viewport*)d); else _vk.CmdSetViewport(c, o.U0, o.U1, (Viewport*)d); }
                break;
            case OpKind.Scissor:
                fixed (byte* d = o.Data) { if (_direct) _setScissor(c, o.U0, o.U1, (Rect2D*)d); else _vk.CmdSetScissor(c, o.U0, o.U1, (Rect2D*)d); }
                break;
            case OpKind.Topology: if (_direct) _setPrimitiveTopology(c, (PrimitiveTopology)o.U0); else _vk.CmdSetPrimitiveTopology(c, (PrimitiveTopology)o.U0); break;
            case OpKind.Cull: if (_direct) _setCullMode(c, (CullModeFlags)o.U0); else _vk.CmdSetCullMode(c, (CullModeFlags)o.U0); break;
            case OpKind.FrontFace: if (_direct) _setFrontFace(c, (FrontFace)o.U0); else _vk.CmdSetFrontFace(c, (FrontFace)o.U0); break;
            case OpKind.LineWidth: if (_direct) _setLineWidth(c, o.F0); else _vk.CmdSetLineWidth(c, o.F0); break;
            case OpKind.DepthTest: if (_direct) _setDepthTestEnable(c, o.U0 != 0); else _vk.CmdSetDepthTestEnable(c, o.U0 != 0); break;
            case OpKind.DepthWrite: if (_direct) _setDepthWriteEnable(c, o.U0 != 0); else _vk.CmdSetDepthWriteEnable(c, o.U0 != 0); break;
            case OpKind.StencilTest: if (_direct) _setStencilTestEnable(c, o.U0 != 0); else _vk.CmdSetStencilTestEnable(c, o.U0 != 0); break;
            case OpKind.DepthBiasEnable: if (_direct) _setDepthBiasEnable(c, o.U0 != 0); else _vk.CmdSetDepthBiasEnable(c, o.U0 != 0); break;
            case OpKind.DepthCompare: if (_direct) _setDepthCompareOp(c, (CompareOp)o.U0); else _vk.CmdSetDepthCompareOp(c, (CompareOp)o.U0); break;
            case OpKind.DepthBias: if (_direct) _setDepthBias(c, o.F0, o.F1, o.F2); else _vk.CmdSetDepthBias(c, o.F0, o.F1, o.F2); break;
            case OpKind.StencilOp:
                if (_direct) _setStencilOp(c, (StencilFaceFlags)o.U0, (StencilOp)o.U1, (StencilOp)o.U2, (StencilOp)o.U3, (CompareOp)o.U4);
                else _vk.CmdSetStencilOp(c, (StencilFaceFlags)o.U0, (StencilOp)o.U1, (StencilOp)o.U2, (StencilOp)o.U3, (CompareOp)o.U4);
                break;
            case OpKind.CompareMask: if (_direct) _setStencilCompareMask(c, (StencilFaceFlags)o.U0, o.U1); else _vk.CmdSetStencilCompareMask(c, (StencilFaceFlags)o.U0, o.U1); break;
            case OpKind.WriteMask: if (_direct) _setStencilWriteMask(c, (StencilFaceFlags)o.U0, o.U1); else _vk.CmdSetStencilWriteMask(c, (StencilFaceFlags)o.U0, o.U1); break;
            case OpKind.Reference: if (_direct) _setStencilReference(c, (StencilFaceFlags)o.U0, o.U1); else _vk.CmdSetStencilReference(c, (StencilFaceFlags)o.U0, o.U1); break;
            case OpKind.BeginQuery: _vk.CmdBeginQuery(c, new QueryPool(o.H0), o.U0, (QueryControlFlags)o.U1); break;
            case OpKind.EndQuery: _vk.CmdEndQuery(c, new QueryPool(o.H0), o.U0); break;
            case OpKind.ByRegionBarrier: InputFetchBarrierNow(c); break;
            case OpKind.Begin:
            {
                var fmt = stackalloc Format[2] { (Format)o.U1, (Format)o.U2 };
                var rii = new CommandBufferInheritanceRenderingInfo
                {
                    SType = StructureType.CommandBufferInheritanceRenderingInfo, ColorAttachmentCount = o.U0, PColorAttachmentFormats = fmt,
                    DepthAttachmentFormat = (Format)o.U3, StencilAttachmentFormat = (Format)o.U3, RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var ii = o.H0 != 0
                    ? new CommandBufferInheritanceInfo { SType = StructureType.CommandBufferInheritanceInfo, RenderPass = new RenderPass(o.H0), Subpass = 0 }
                    : new CommandBufferInheritanceInfo { SType = StructureType.CommandBufferInheritanceInfo, PNext = &rii };
                var bi = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.OneTimeSubmitBit,
                    PInheritanceInfo = &ii,
                };
                _vk.BeginCommandBuffer(c, in bi).Ok("vkBeginCommandBuffer(recorded secondary)");
                break;
            }
            case OpKind.End: _vk.EndCommandBuffer(c).Ok("vkEndCommandBuffer(recorded secondary)"); break;
        }
    }
}
