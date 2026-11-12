using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Driver workaround for broken extended dynamic state (<see cref="GpuQuirk.StaticExtendedDynamicState"/>,
/// <see cref="GpuQuirk.StaticDepthBiasEnable"/>; yuzu removes both extensions on Qualcomm's proprietary driver).
/// Cull mode, front face, primitive topology, depth test/write/compare and stencil test/ops are then part of the
/// pipeline key (<see cref="PipelineKey.Static"/>, 0 on drivers without the quirk, whose keys and pipelines are
/// unchanged) and baked into the pipeline; depth bias stays enabled in every pipeline and a draw without bias sets
/// zero bias (depth + 0 is exact). The renderer keeps only Vulkan 1.0 dynamic state then: viewport, scissor, line
/// width, depth bias values, stencil masks and reference. Background pipeline prediction carries the static key of the
/// draw it predicts from; pipelines.list recipes do not record it, so recording and prewarm are off with the quirk.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private bool StaticEds => _core.Caps.Has(GpuQuirk.StaticExtendedDynamicState);
    private bool StaticBias => _core.Caps.Has(GpuQuirk.StaticDepthBiasEnable);
    private long _statPipelinesStatic;

    /// <summary>Set in every non-zero static state key.</summary>
    private const ulong StaticPresent = 1ul << 63;

    /// <summary>VK_PIPELINE_COLOR_BLEND_STATE_CREATE_RASTERIZATION_ORDER_ATTACHMENT_ACCESS_BIT_EXT (0x1; not named in Silk.NET 2.23).</summary>
    private const PipelineColorBlendStateCreateFlags RoaaBlendFlag = (PipelineColorBlendStateCreateFlags)1;

    /// <summary>The draw's fixed-function state that a static-state pipeline bakes in (0 without the quirk).</summary>
    private ulong StaticStateKey(in DrawState drawState, PrimitiveTopology topology)
    {
        if (!StaticEds)
        {
            return 0;
        }

        SideState front = drawState.Front;
        SideState back = drawState.TwoSided ? drawState.Back : drawState.Front;
        ulong k = StaticPresent | (ulong)(drawState.CullMode & 3u) | ((ulong)(uint)topology & 15) << 2;
        bool depthTest = !(front.DepthFunc == 7 && !front.DepthWrite);
        if (depthTest)
        {
            k |= 1ul << 6;
        }

        if (front.DepthWrite)
        {
            k |= 1ul << 7;
        }

        k |= (ulong)(front.DepthFunc & 7u) << 8;
        if (!(IsTrivial(front.Stencil) && IsTrivial(back.Stencil)))
        {
            k |= 1ul << 11 | StencilBits(front.Stencil) << 12 | StencilBits(back.Stencil) << 24;
        }

        return k;
    }

    private static ulong StencilBits(in StencilState stencilState) =>
        (stencilState.StencilFail & 7u) | (stencilState.DepthPass & 7u) << 3 | (stencilState.DepthFail & 7u) << 6 | (stencilState.Func & 7u) << 9;

    private static StencilOpState StencilFrom(ulong bits) => new()
    {
        FailOp = (StencilOp)(bits & 7),
        PassOp = (StencilOp)((bits >> 3) & 7),
        DepthFailOp = (StencilOp)((bits >> 6) & 7),
        CompareOp = (CompareOp)((bits >> 9) & 7),
        CompareMask = 0xFF,
        WriteMask = 0xFF,
        Reference = 0, // masks/reference stay dynamic
    };

    /// <summary>Bakes a static state key into the pipeline create infos.</summary>
    private static void ApplyStaticState(ulong k, ref PipelineInputAssemblyStateCreateInfo pipelineInputAssemblyStateCreateInfo, ref PipelineRasterizationStateCreateInfo rs, ref PipelineDepthStencilStateCreateInfo pipelineDepthStencilStateCreateInfo)
    {
        if (k == 0)
        {
            return;
        }

        rs.CullMode = (k & 3) switch
        {
            1 => CullModeFlags.BackBit,
            2 => CullModeFlags.FrontBit,
            _ => CullModeFlags.None
        };
        rs.FrontFace = FrontFace.CounterClockwise;
        pipelineInputAssemblyStateCreateInfo.Topology = (PrimitiveTopology)((k >> 2) & 15);
        pipelineDepthStencilStateCreateInfo.DepthTestEnable = (k & (1ul << 6)) != 0;
        pipelineDepthStencilStateCreateInfo.DepthWriteEnable = (k & (1ul << 7)) != 0;
        pipelineDepthStencilStateCreateInfo.DepthCompareOp = (CompareOp)((k >> 8) & 7);
        pipelineDepthStencilStateCreateInfo.StencilTestEnable = (k & (1ul << 11)) != 0;
        if (pipelineDepthStencilStateCreateInfo.StencilTestEnable)
        {
            pipelineDepthStencilStateCreateInfo.Front = StencilFrom(k >> 12);
            pipelineDepthStencilStateCreateInfo.Back = StencilFrom(k >> 24);
        }
    }

    /// <summary>Dynamic states of a draw pipeline: all of core 1.3's unless a quirk bakes them in.</summary>
    private int DrawDynamicStates(DynamicState* dyn, bool staticState)
    {
        int n = 0;
        dyn[n++] = DynamicState.Viewport;
        dyn[n++] = DynamicState.Scissor;
        dyn[n++] = DynamicState.LineWidth;
        dyn[n++] = DynamicState.DepthBias;
        dyn[n++] = DynamicState.StencilCompareMask;
        dyn[n++] = DynamicState.StencilWriteMask;
        dyn[n++] = DynamicState.StencilReference;
        if (!staticState)
        {
            dyn[n++] = DynamicState.CullMode;
            dyn[n++] = DynamicState.FrontFace;
            dyn[n++] = DynamicState.PrimitiveTopology;
            dyn[n++] = DynamicState.DepthTestEnable;
            dyn[n++] = DynamicState.DepthWriteEnable;
            dyn[n++] = DynamicState.DepthCompareOp;
            dyn[n++] = DynamicState.StencilTestEnable;
            dyn[n++] = DynamicState.StencilOp;
        }
        if (!StaticBias)
        {
            dyn[n++] = DynamicState.DepthBiasEnable;
        }

        return n;
    }
}
