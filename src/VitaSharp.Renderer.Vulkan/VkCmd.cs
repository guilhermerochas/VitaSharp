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
    public void CmdBindPipeline(CommandBuffer commandBuffer, PipelineBindPoint pipelineBindPoint, Pipeline pipeline)
    {
        if (_direct)
        {
            _bindPipeline(commandBuffer, pipelineBindPoint, pipeline);
        }
        else
        {
            _vk.CmdBindPipeline(commandBuffer, pipelineBindPoint, pipeline);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdBindDescriptorSets(CommandBuffer commandBuffer, PipelineBindPoint pipelineBindPoint, PipelineLayout pipelineLayout, uint first, uint count, DescriptorSet* sets, uint dynCount, uint* dyn)
    {
        if (_direct)
        {
            _bindDescriptorSets(commandBuffer, pipelineBindPoint, pipelineLayout, first, count, sets, dynCount, dyn);
        }
        else
        {
            _vk.CmdBindDescriptorSets(commandBuffer, pipelineBindPoint, pipelineLayout, first, count, sets, dynCount, dyn);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdBindVertexBuffers(CommandBuffer commandBuffer, uint first, uint count, Buffer* b, ulong* o)
    {
        if (_direct)
        {
            _bindVertexBuffers(commandBuffer, first, count, b, o);
        }
        else
        {
            _vk.CmdBindVertexBuffers(commandBuffer, first, count, b, o);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdBindIndexBuffer(CommandBuffer commandBuffer, Buffer buffer, ulong o, IndexType indexType)
    {
        if (_direct)
        {
            _bindIndexBuffer(commandBuffer, buffer, o, indexType);
        }
        else
        {
            _vk.CmdBindIndexBuffer(commandBuffer, buffer, o, indexType);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdDrawIndexed(CommandBuffer commandBuffer, uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    {
        if (_direct)
        {
            _drawIndexed(commandBuffer, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
        }
        else
        {
            _vk.CmdDrawIndexed(commandBuffer, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdPushConstants(CommandBuffer commandBuffer, PipelineLayout pipelineLayout, ShaderStageFlags shaderStageFlags, uint offset, uint size, void* data)
    {
        if (_direct)
        {
            _pushConstants(commandBuffer, pipelineLayout, shaderStageFlags, offset, size, data);
        }
        else
        {
            _vk.CmdPushConstants(commandBuffer, pipelineLayout, shaderStageFlags, offset, size, data);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetViewport(CommandBuffer commandBuffer, uint first, uint count, in Viewport viewport)
    {
        if (_direct)
        {
            fixed (Viewport* p = &viewport)
            {
                _setViewport(commandBuffer, first, count, p);
            }
        }
        else
        {
            _vk.CmdSetViewport(commandBuffer, first, count, in viewport);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetScissor(CommandBuffer commandBuffer, uint first, uint count, in Rect2D r)
    {
        if (_direct)
        {
            fixed (Rect2D* p = &r)
            {
                _setScissor(commandBuffer, first, count, p);
            }
        }
        else
        {
            _vk.CmdSetScissor(commandBuffer, first, count, in r);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetPrimitiveTopology(CommandBuffer commandBuffer, PrimitiveTopology primitiveTopology)
    {
        if (_direct)
        {
            _setPrimitiveTopology(commandBuffer, primitiveTopology);
        }
        else
        {
            _vk.CmdSetPrimitiveTopology(commandBuffer, primitiveTopology);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetCullMode(CommandBuffer commandBuffer, CullModeFlags cullModeFlags)
    {
        if (_direct)
        {
            _setCullMode(commandBuffer, cullModeFlags);
        }
        else
        {
            _vk.CmdSetCullMode(commandBuffer, cullModeFlags);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetFrontFace(CommandBuffer commandBuffer, FrontFace frontFace)
    {
        if (_direct)
        {
            _setFrontFace(commandBuffer, frontFace);
        }
        else
        {
            _vk.CmdSetFrontFace(commandBuffer, frontFace);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetLineWidth(CommandBuffer commandBuffer, float w)
    {
        if (_direct)
        {
            _setLineWidth(commandBuffer, w);
        }
        else
        {
            _vk.CmdSetLineWidth(commandBuffer, w);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetDepthTestEnable(CommandBuffer commandBuffer, Bool32 e)
    {
        if (_direct)
        {
            _setDepthTestEnable(commandBuffer, e);
        }
        else
        {
            _vk.CmdSetDepthTestEnable(commandBuffer, e);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetDepthWriteEnable(CommandBuffer commandBuffer, Bool32 e)
    {
        if (_direct)
        {
            _setDepthWriteEnable(commandBuffer, e);
        }
        else
        {
            _vk.CmdSetDepthWriteEnable(commandBuffer, e);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetStencilTestEnable(CommandBuffer commandBuffer, Bool32 e)
    {
        if (_direct)
        {
            _setStencilTestEnable(commandBuffer, e);
        }
        else
        {
            _vk.CmdSetStencilTestEnable(commandBuffer, e);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetDepthBiasEnable(CommandBuffer commandBuffer, Bool32 e)
    {
        if (_direct)
        {
            _setDepthBiasEnable(commandBuffer, e);
        }
        else
        {
            _vk.CmdSetDepthBiasEnable(commandBuffer, e);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetDepthCompareOp(CommandBuffer commandBuffer, CompareOp op)
    {
        if (_direct)
        {
            _setDepthCompareOp(commandBuffer, op);
        }
        else
        {
            _vk.CmdSetDepthCompareOp(commandBuffer, op);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetDepthBias(CommandBuffer commandBuffer, float constant, float clamp, float slope)
    {
        if (_direct)
        {
            _setDepthBias(commandBuffer, constant, clamp, slope);
        }
        else
        {
            _vk.CmdSetDepthBias(commandBuffer, constant, clamp, slope);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetStencilOp(CommandBuffer commandBuffer, StencilFaceFlags stencilFaceFlags, StencilOp fail, StencilOp pass, StencilOp depthFail, CompareOp op)
    {
        if (_direct)
        {
            _setStencilOp(commandBuffer, stencilFaceFlags, fail, pass, depthFail, op);
        }
        else
        {
            _vk.CmdSetStencilOp(commandBuffer, stencilFaceFlags, fail, pass, depthFail, op);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetStencilCompareMask(CommandBuffer commandBuffer, StencilFaceFlags stencilFaceFlags, uint m)
    {
        if (_direct)
        {
            _setStencilCompareMask(commandBuffer, stencilFaceFlags, m);
        }
        else
        {
            _vk.CmdSetStencilCompareMask(commandBuffer, stencilFaceFlags, m);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetStencilWriteMask(CommandBuffer commandBuffer, StencilFaceFlags stencilFaceFlags, uint m)
    {
        if (_direct)
        {
            _setStencilWriteMask(commandBuffer, stencilFaceFlags, m);
        }
        else
        {
            _vk.CmdSetStencilWriteMask(commandBuffer, stencilFaceFlags, m);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CmdSetStencilReference(CommandBuffer commandBuffer, StencilFaceFlags stencilFaceFlags, uint r)
    {
        if (_direct)
        {
            _setStencilReference(commandBuffer, stencilFaceFlags, r);
        }
        else
        {
            _vk.CmdSetStencilReference(commandBuffer, stencilFaceFlags, r);
        }
    }
}
