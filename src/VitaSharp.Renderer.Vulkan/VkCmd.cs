using Silk.NET.Core;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using VitaSharp.Core.Util;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The command-recording calls of the draw path. With VITASHARP_VK_DIRECT=1 they go straight to the device-level
/// functions, loaded once with vkGetDeviceProcAddr, as <c>delegate* unmanaged[SuppressGCTransition]</c>: no Silk.NET
/// dispatch, no loader trampoline and no Mono GC transition per call (the vkCmd* calls are short and never block).
/// Otherwise (the default) they are Silk.NET's. Setup code always uses Silk.NET. Same signatures as the Silk.NET
/// overloads the draw path uses, with Silk.NET's blittable structs.
/// </summary>
internal sealed unsafe class VkCmd
{
    /// <summary>VITASHARP_VK_DIRECT=1: the direct function pointers (A/B against Silk.NET on the phone).</summary>
    internal static readonly bool s_directRequested = Environment.GetEnvironmentVariable(EnvVars.VkDirect)?.Trim() == "1";

    private readonly Vk _vk;
    private readonly bool _direct;

    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, PipelineBindPoint, Pipeline, void> _bindPipeline;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, PipelineBindPoint, PipelineLayout, uint, uint, DescriptorSet*, uint, uint*, void> _bindDescriptorSets;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, uint, uint, Buffer*, ulong*, void> _bindVertexBuffers;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, Buffer, ulong, IndexType, void> _bindIndexBuffer;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, uint, uint, uint, int, uint, void> _drawIndexed;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, PipelineLayout, ShaderStageFlags, uint, uint, void*, void> _pushConstants;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, uint, uint, Viewport*, void> _setViewport;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, uint, uint, Rect2D*, void> _setScissor;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, PrimitiveTopology, void> _setPrimitiveTopology;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, CullModeFlags, void> _setCullMode;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, FrontFace, void> _setFrontFace;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, float, void> _setLineWidth;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, Bool32, void> _setDepthTestEnable, _setDepthWriteEnable, _setStencilTestEnable, _setDepthBiasEnable;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, CompareOp, void> _setDepthCompareOp;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, float, float, float, void> _setDepthBias;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, StencilFaceFlags, StencilOp, StencilOp, StencilOp, CompareOp, void> _setStencilOp;
    private readonly delegate* unmanaged[SuppressGCTransition]<CommandBuffer, StencilFaceFlags, uint, void> _setStencilCompareMask, _setStencilWriteMask, _setStencilReference;

    public VkCmd(Vk vk, Device dev)
    {
        _vk = vk;
        if (!s_directRequested)
        {
            return;
        }

        nint P(string name) => (nint)vk.GetDeviceProcAddr(dev, name).Handle;
        nint[] all =
        [
            (nint)(_bindPipeline = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, PipelineBindPoint, Pipeline, void>)P("vkCmdBindPipeline")),
            (nint)(_bindDescriptorSets = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, PipelineBindPoint, PipelineLayout, uint, uint, DescriptorSet*, uint, uint*, void>)P("vkCmdBindDescriptorSets")),
            (nint)(_bindVertexBuffers = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, uint, uint, Buffer*, ulong*, void>)P("vkCmdBindVertexBuffers")),
            (nint)(_bindIndexBuffer = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, Buffer, ulong, IndexType, void>)P("vkCmdBindIndexBuffer")),
            (nint)(_drawIndexed = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, uint, uint, uint, int, uint, void>)P("vkCmdDrawIndexed")),
            (nint)(_pushConstants = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, PipelineLayout, ShaderStageFlags, uint, uint, void*, void>)P("vkCmdPushConstants")),
            (nint)(_setViewport = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, uint, uint, Viewport*, void>)P("vkCmdSetViewport")),
            (nint)(_setScissor = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, uint, uint, Rect2D*, void>)P("vkCmdSetScissor")),
            (nint)(_setLineWidth = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, float, void>)P("vkCmdSetLineWidth")),
            (nint)(_setDepthBias = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, float, float, float, void>)P("vkCmdSetDepthBias")),
            (nint)(_setStencilCompareMask = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, StencilFaceFlags, uint, void>)P("vkCmdSetStencilCompareMask")),
            (nint)(_setStencilWriteMask = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, StencilFaceFlags, uint, void>)P("vkCmdSetStencilWriteMask")),
            (nint)(_setStencilReference = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, StencilFaceFlags, uint, void>)P("vkCmdSetStencilReference")),
        ];
        // Vulkan 1.3 extended dynamic state: missing on older devices, where the draw path never calls them
        _setPrimitiveTopology = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, PrimitiveTopology, void>)P("vkCmdSetPrimitiveTopology");
        _setCullMode = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, CullModeFlags, void>)P("vkCmdSetCullMode");
        _setFrontFace = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, FrontFace, void>)P("vkCmdSetFrontFace");
        _setDepthTestEnable = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, Bool32, void>)P("vkCmdSetDepthTestEnable");
        _setDepthWriteEnable = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, Bool32, void>)P("vkCmdSetDepthWriteEnable");
        _setStencilTestEnable = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, Bool32, void>)P("vkCmdSetStencilTestEnable");
        _setDepthBiasEnable = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, Bool32, void>)P("vkCmdSetDepthBiasEnable");
        _setDepthCompareOp = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, CompareOp, void>)P("vkCmdSetDepthCompareOp");
        _setStencilOp = (delegate* unmanaged[SuppressGCTransition]<CommandBuffer, StencilFaceFlags, StencilOp, StencilOp, StencilOp, CompareOp, void>)P("vkCmdSetStencilOp");
        _direct = Array.TrueForAll(all, p => p != 0) && _setPrimitiveTopology != null && _setCullMode != null && _setFrontFace != null
            && _setDepthTestEnable != null && _setDepthWriteEnable != null && _setStencilTestEnable != null && _setDepthBiasEnable != null
            && _setDepthCompareOp != null && _setStencilOp != null;
        Log.Info(_direct ? "[gpu] VITASHARP_VK_DIRECT: draw-path vkCmd* calls through direct device function pointers"
                         : "[gpu] VITASHARP_VK_DIRECT ignored: a device function is missing, Silk.NET is used");
    }

    public bool Direct => _direct;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdBindPipeline(CommandBuffer c, PipelineBindPoint bp, Pipeline p)
    {
        if (_direct)
        {
            _bindPipeline(c, bp, p);
        }
        else
        {
            _vk.CmdBindPipeline(c, bp, p);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdBindDescriptorSets(CommandBuffer c, PipelineBindPoint bp, PipelineLayout l, uint first, uint count, DescriptorSet* sets, uint dynCount, uint* dyn)
    {
        if (_direct)
        {
            _bindDescriptorSets(c, bp, l, first, count, sets, dynCount, dyn);
        }
        else
        {
            _vk.CmdBindDescriptorSets(c, bp, l, first, count, sets, dynCount, dyn);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdBindVertexBuffers(CommandBuffer c, uint first, uint count, Buffer* b, ulong* o)
    {
        if (_direct)
        {
            _bindVertexBuffers(c, first, count, b, o);
        }
        else
        {
            _vk.CmdBindVertexBuffers(c, first, count, b, o);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdBindIndexBuffer(CommandBuffer c, Buffer b, ulong o, IndexType t)
    {
        if (_direct)
        {
            _bindIndexBuffer(c, b, o, t);
        }
        else
        {
            _vk.CmdBindIndexBuffer(c, b, o, t);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdDrawIndexed(CommandBuffer c, uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    {
        if (_direct)
        {
            _drawIndexed(c, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
        }
        else
        {
            _vk.CmdDrawIndexed(c, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdPushConstants(CommandBuffer c, PipelineLayout l, ShaderStageFlags s, uint offset, uint size, void* data)
    {
        if (_direct)
        {
            _pushConstants(c, l, s, offset, size, data);
        }
        else
        {
            _vk.CmdPushConstants(c, l, s, offset, size, data);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetViewport(CommandBuffer c, uint first, uint count, in Viewport v)
    {
        if (_direct)
        {
            fixed (Viewport* p = &v)
            {
                _setViewport(c, first, count, p);
            }
        }
        else
        {
            _vk.CmdSetViewport(c, first, count, in v);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetScissor(CommandBuffer c, uint first, uint count, in Rect2D r)
    {
        if (_direct)
        {
            fixed (Rect2D* p = &r)
            {
                _setScissor(c, first, count, p);
            }
        }
        else
        {
            _vk.CmdSetScissor(c, first, count, in r);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetPrimitiveTopology(CommandBuffer c, PrimitiveTopology t)
    {
        if (_direct)
        {
            _setPrimitiveTopology(c, t);
        }
        else
        {
            _vk.CmdSetPrimitiveTopology(c, t);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetCullMode(CommandBuffer c, CullModeFlags m)
    {
        if (_direct)
        {
            _setCullMode(c, m);
        }
        else
        {
            _vk.CmdSetCullMode(c, m);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetFrontFace(CommandBuffer c, FrontFace f)
    {
        if (_direct)
        {
            _setFrontFace(c, f);
        }
        else
        {
            _vk.CmdSetFrontFace(c, f);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetLineWidth(CommandBuffer c, float w)
    {
        if (_direct)
        {
            _setLineWidth(c, w);
        }
        else
        {
            _vk.CmdSetLineWidth(c, w);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetDepthTestEnable(CommandBuffer c, Bool32 e)
    {
        if (_direct)
        {
            _setDepthTestEnable(c, e);
        }
        else
        {
            _vk.CmdSetDepthTestEnable(c, e);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetDepthWriteEnable(CommandBuffer c, Bool32 e)
    {
        if (_direct)
        {
            _setDepthWriteEnable(c, e);
        }
        else
        {
            _vk.CmdSetDepthWriteEnable(c, e);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetStencilTestEnable(CommandBuffer c, Bool32 e)
    {
        if (_direct)
        {
            _setStencilTestEnable(c, e);
        }
        else
        {
            _vk.CmdSetStencilTestEnable(c, e);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetDepthBiasEnable(CommandBuffer c, Bool32 e)
    {
        if (_direct)
        {
            _setDepthBiasEnable(c, e);
        }
        else
        {
            _vk.CmdSetDepthBiasEnable(c, e);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetDepthCompareOp(CommandBuffer c, CompareOp op)
    {
        if (_direct)
        {
            _setDepthCompareOp(c, op);
        }
        else
        {
            _vk.CmdSetDepthCompareOp(c, op);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetDepthBias(CommandBuffer c, float constant, float clamp, float slope)
    {
        if (_direct)
        {
            _setDepthBias(c, constant, clamp, slope);
        }
        else
        {
            _vk.CmdSetDepthBias(c, constant, clamp, slope);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetStencilOp(CommandBuffer c, StencilFaceFlags f, StencilOp fail, StencilOp pass, StencilOp depthFail, CompareOp op)
    {
        if (_direct)
        {
            _setStencilOp(c, f, fail, pass, depthFail, op);
        }
        else
        {
            _vk.CmdSetStencilOp(c, f, fail, pass, depthFail, op);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetStencilCompareMask(CommandBuffer c, StencilFaceFlags f, uint m)
    {
        if (_direct)
        {
            _setStencilCompareMask(c, f, m);
        }
        else
        {
            _vk.CmdSetStencilCompareMask(c, f, m);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetStencilWriteMask(CommandBuffer c, StencilFaceFlags f, uint m)
    {
        if (_direct)
        {
            _setStencilWriteMask(c, f, m);
        }
        else
        {
            _vk.CmdSetStencilWriteMask(c, f, m);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetStencilReference(CommandBuffer c, StencilFaceFlags f, uint r)
    {
        if (_direct)
        {
            _setStencilReference(c, f, r);
        }
        else
        {
            _vk.CmdSetStencilReference(c, f, r);
        }
    }
}
