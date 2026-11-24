using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using VitaSharp.Core.Compat.Ge;
using VitaSharp.Core.Util;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The PSP GE's rasterizer on Vulkan (docs/notes/psp-compat.md, "The GE"), separate from the GXM renderer: each batch
/// the GE hands over is drawn into an RGBA8 colour image and a 16-bit depth image holding the PSP framebuffer region
/// (uploaded from the GE's target, read back into it after the draw), with the GE's per-fragment state: the texture
/// function (modulate, decal, blend, replace, add, colour doubling; the texture sampled at U/Q, V/Q), the secondary
/// (separate specular) colour added after it, fog (FOGCOLOR by the vertices' factor), the alpha test, blending (the GE's factors and
/// equations; a fixed factor is the blend constant), the depth test and write, the scissor, the RGB write masks and
/// clear mode (colour, alpha and depth written as they are). The framebuffer's alpha is the PSP's stencil: normal
/// draws leave it (the stencil operations are later work), clear mode writes it.
/// <para>
/// One submission per batch, waited for: correct and simple; keeping the target on the GPU between batches is the
/// performance step after this one. Not thread-safe (the GE runs one list at a time).
/// </para>
/// </summary>
public sealed unsafe class GeVulkanRasterizer : IGeRasterizer, IDisposable
{
    private const Format ColorFormat = Format.R8G8B8A8Unorm;
    private const Format DepthFormat = Format.D16Unorm;
    /// <summary>The vertex buffer is the GeVertex array as it is: its size and field offsets give the layout.</summary>
    private static readonly int s_vertexStride = Marshal.SizeOf<GeVertex>();
    private static readonly uint s_positionOffset = (uint)Marshal.OffsetOf<GeVertex>(nameof(GeVertex.X));
    private static readonly uint s_uvOffset = (uint)Marshal.OffsetOf<GeVertex>(nameof(GeVertex.U));
    private static readonly uint s_colorOffset = (uint)Marshal.OffsetOf<GeVertex>(nameof(GeVertex.Color));
    private static readonly uint s_color1Offset = (uint)Marshal.OffsetOf<GeVertex>(nameof(GeVertex.Color1));
    private static readonly uint s_fogOffset = (uint)Marshal.OffsetOf<GeVertex>(nameof(GeVertex.Fog));
    private static readonly uint s_qOffset = (uint)Marshal.OffsetOf<GeVertex>(nameof(GeVertex.Q));
    private const int PushConstantSize = 64;

    private const string VertexShader = """
        #version 450
        layout(location = 0) in vec4 inPosition;
        layout(location = 1) in vec2 inUv;
        layout(location = 2) in vec4 inColor;
        layout(location = 3) in vec4 inColor1;
        layout(location = 4) in float inFog;
        layout(location = 5) in float inQ;
        layout(push_constant) uniform Constants { vec2 size; int flags; int textureFunction; vec4 environment; ivec4 alpha; vec4 fog; } pc;
        layout(location = 0) out vec4 vColor;
        layout(location = 1) out vec3 vUvq;
        layout(location = 2) out vec4 vColor1;
        layout(location = 3) out float vFog;
        void main()
        {
            float w = inPosition.w;
            vec2 ndc = inPosition.xy / pc.size * 2.0 - 1.0;
            gl_Position = vec4(ndc * w, inPosition.z * w, w);
            gl_PointSize = 1.0;
            vColor = inColor;
            vUvq = vec3(inUv, inQ);
            vColor1 = inColor1;
            vFog = inFog;
        }
        """;

    private const string FragmentShader = """
        #version 450
        layout(location = 0) in vec4 vColor;
        layout(location = 1) in vec3 vUvq;
        layout(location = 2) in vec4 vColor1;
        layout(location = 3) in float vFog;
        layout(set = 0, binding = 0) uniform sampler2D textureSampler;
        layout(push_constant) uniform Constants { vec2 size; int flags; int textureFunction; vec4 environment; ivec4 alpha; vec4 fog; } pc;
        layout(location = 0) out vec4 outColor;
        bool Compare(int function, int value, int reference)
        {
            switch (function)
            {
                case 0: return false;
                case 1: return true;
                case 2: return value == reference;
                case 3: return value != reference;
                case 4: return value < reference;
                case 5: return value <= reference;
                case 6: return value > reference;
                default: return value >= reference;
            }
        }
        void main()
        {
            vec4 color = vColor;
            if ((pc.flags & 1) != 0)
            {
                float q = vUvq.z == 0.0 ? 1.0 : vUvq.z;
                vec4 texel = texture(textureSampler, vUvq.xy / q);
                bool textureAlpha = (pc.flags & 2) != 0;
                float alpha = textureAlpha ? color.a * texel.a : color.a;
                if (pc.textureFunction == 0)
                {
                    color = vec4(color.rgb * texel.rgb, alpha);
                }
                else if (pc.textureFunction == 1)
                {
                    color = vec4(textureAlpha ? mix(color.rgb, texel.rgb, texel.a) : texel.rgb, color.a);
                }
                else if (pc.textureFunction == 2)
                {
                    color = vec4(mix(color.rgb, pc.environment.rgb, texel.rgb), alpha);
                }
                else if (pc.textureFunction == 3)
                {
                    color = vec4(texel.rgb, textureAlpha ? texel.a : color.a);
                }
                else
                {
                    color = vec4(color.rgb + texel.rgb, alpha);
                }

                if ((pc.flags & 4) != 0)
                {
                    color.rgb *= 2.0;
                }

                color = clamp(color, 0.0, 1.0);
            }

            if ((pc.flags & 32) != 0)
            {
                color.rgb = clamp(color.rgb + vColor1.rgb, 0.0, 1.0);
            }

            if ((pc.flags & 16) != 0)
            {
                color.rgb = mix(pc.fog.rgb, color.rgb, clamp(vFog, 0.0, 1.0));
            }

            if ((pc.flags & 8) != 0)
            {
                int value = int(color.a * 255.0 + 0.5) & pc.alpha.z;
                if (!Compare(pc.alpha.x, value, pc.alpha.y & pc.alpha.z))
                {
                    discard;
                }
            }

            outColor = color;
        }
        """;

    private readonly VkCore _core;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer _commandBuffer;
    private readonly Fence _fence;
    private readonly RenderPass _renderPass;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _descriptorPool;
    private readonly DescriptorSet _descriptorSet;
    private readonly PipelineLayout _pipelineLayout;
    private readonly ShaderModule _vertexModule;
    private readonly ShaderModule _fragmentModule;
    private readonly Sampler[] _samplers = new Sampler[8];
    private readonly Dictionary<ulong, Pipeline> _pipelines = [];

    private int _targetWidth;
    private int _targetHeight;
    private Image _colorImage;
    private DeviceMemory _colorMemory;
    private ImageView _colorView;
    private Image _depthImage;
    private DeviceMemory _depthMemory;
    private ImageView _depthView;
    private Framebuffer _framebuffer;

    private int _textureWidth;
    private int _textureHeight;
    private Image _textureImage;
    private DeviceMemory _textureMemory;
    private ImageView _textureView;
    private bool _textureReadable;

    private Buffer _staging;
    private DeviceMemory _stagingMemory;
    private nint _stagingMapped;
    private ulong _stagingSize;
    private Buffer _vertexBuffer;
    private DeviceMemory _vertexMemory;
    private nint _vertexMapped;
    private ulong _vertexSize;

    public GeVulkanRasterizer(VkCore core, ShaderCompiler compiler)
    {
        _core = core;
        _vk = core.Vk;
        _device = core.Device;

        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = core.QueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        _vk.CreateCommandPool(_device, in poolInfo, null, out _commandPool).Ok("GE command pool");
        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        _vk.AllocateCommandBuffers(_device, in allocateInfo, out _commandBuffer).Ok("GE command buffer");
        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        _vk.CreateFence(_device, in fenceInfo, null, out _fence).Ok("GE fence");

        _renderPass = CreateRenderPass();
        (_setLayout, _descriptorPool, _descriptorSet) = CreateDescriptors();
        _pipelineLayout = CreatePipelineLayout();
        _vertexModule = CreateModule(compiler, VertexShader, vertex: true);
        _fragmentModule = CreateModule(compiler, FragmentShader, vertex: false);
        for (int i = 0; i < _samplers.Length; i++)
        {
            _samplers[i] = CreateSampler((i & 1) != 0, (i & 2) != 0, (i & 4) != 0);
        }

        EnsureTexture(1, 1);
    }

    /// <summary>The batches drawn.</summary>
    public long Draws { get; private set; }

    public void Draw(GeTopology topology, ReadOnlySpan<GeVertex> vertices, GeRenderState state, GeTarget target)
    {
        if (vertices.Length == 0 || target.Width <= 0 || target.Height <= 0)
        {
            return;
        }

        bool usesDepth = target.Depth != null;
        bool textured = state.Texture && !state.ClearMode && state.TexturePixels.Length >= state.TextureWidth * state.TextureHeight;
        EnsureTarget(target.Width, target.Height);
        if (textured)
        {
            EnsureTexture(state.TextureWidth, state.TextureHeight);
        }

        ulong colorBytes = (ulong)target.Width * (ulong)target.Height * 4;
        ulong depthOffset = colorBytes;
        ulong depthBytes = ((ulong)target.Width * (ulong)target.Height * 2 + 3) & ~3UL;
        ulong textureOffset = depthOffset + depthBytes;
        ulong textureBytes = textured ? (ulong)state.TextureWidth * (ulong)state.TextureHeight * 4 : 0;
        EnsureStaging(textureOffset + textureBytes);
        EnsureVertexBuffer((ulong)(vertices.Length * s_vertexStride));

        target.Color.AsSpan(0, target.Width * target.Height).CopyTo(new Span<uint>((void*)_stagingMapped, target.Width * target.Height));
        if (usesDepth)
        {
            target.Depth!.AsSpan(0, target.Width * target.Height).CopyTo(new Span<ushort>((void*)(_stagingMapped + (nint)depthOffset), target.Width * target.Height));
        }

        if (textured)
        {
            state.TexturePixels.AsSpan(0, state.TextureWidth * state.TextureHeight).CopyTo(new Span<uint>((void*)(_stagingMapped + (nint)textureOffset), state.TextureWidth * state.TextureHeight));
        }

        MemoryMarshal.AsBytes(vertices).CopyTo(new Span<byte>((void*)_vertexMapped, vertices.Length * s_vertexStride));

        Pipeline pipeline = PipelineFor(topology, state, usesDepth);
        UpdateDescriptor(state, textured);
        Record(target, usesDepth, textured, depthOffset, textureOffset, pipeline, state, vertices.Length);
        Submit();

        new Span<uint>((void*)_stagingMapped, target.Width * target.Height).CopyTo(target.Color);
        if (usesDepth)
        {
            new Span<ushort>((void*)(_stagingMapped + (nint)depthOffset), target.Width * target.Height).CopyTo(target.Depth);
        }

        Draws++;
    }

    // ---- recording

    private void Record(GeTarget target, bool usesDepth, bool textured, ulong depthOffset, ulong textureOffset, Pipeline pipeline,
        GeRenderState state, int vertexCount)
    {
        CommandBuffer cb = _commandBuffer;
        _vk.ResetCommandBuffer(cb, 0).Ok("GE reset command buffer");
        CommandBufferBeginInfo begin = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _vk.BeginCommandBuffer(cb, in begin).Ok("GE begin");

        uint width = (uint)target.Width;
        uint height = (uint)target.Height;
        Transition(cb, _colorImage, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
        Copy(cb, _colorImage, ImageAspectFlags.ColorBit, 0, width, height, toImage: true);
        Transition(cb, _colorImage, ImageAspectFlags.ColorBit, ImageLayout.TransferDstOptimal, ImageLayout.ColorAttachmentOptimal);
        if (usesDepth)
        {
            Transition(cb, _depthImage, ImageAspectFlags.DepthBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            Copy(cb, _depthImage, ImageAspectFlags.DepthBit, depthOffset, width, height, toImage: true);
            Transition(cb, _depthImage, ImageAspectFlags.DepthBit, ImageLayout.TransferDstOptimal, ImageLayout.DepthStencilAttachmentOptimal);
        }
        else
        {
            Transition(cb, _depthImage, ImageAspectFlags.DepthBit, ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal);
        }

        if (textured)
        {
            Transition(cb, _textureImage, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            Copy(cb, _textureImage, ImageAspectFlags.ColorBit, textureOffset, (uint)_textureWidth, (uint)_textureHeight, toImage: true);
            Transition(cb, _textureImage, ImageAspectFlags.ColorBit, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            _textureReadable = true;
        }
        else if (!_textureReadable)
        {
            Transition(cb, _textureImage, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal);
            _textureReadable = true;
        }

        RenderPassBeginInfo pass = new()
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height)),
        };
        _vk.CmdBeginRenderPass(cb, in pass, SubpassContents.Inline);
        Viewport viewport = new(0, 0, width, height, 0, 1);
        _vk.CmdSetViewport(cb, 0, 1, in viewport);
        int left = Math.Clamp(state.ScissorLeft, 0, target.Width);
        int top = Math.Clamp(state.ScissorTop, 0, target.Height);
        int right = Math.Clamp(state.ScissorRight + 1, left, target.Width);
        int bottom = Math.Clamp(state.ScissorBottom + 1, top, target.Height);
        Rect2D scissor = new(new Offset2D(left, top), new Extent2D((uint)(right - left), (uint)(bottom - top)));
        _vk.CmdSetScissor(cb, 0, 1, in scissor);
        uint constantColor = BlendConstant(state);
        float* constants = stackalloc float[4];
        constants[0] = (constantColor & 0xFF) / 255f;
        constants[1] = ((constantColor >> 8) & 0xFF) / 255f;
        constants[2] = ((constantColor >> 16) & 0xFF) / 255f;
        constants[3] = 1;
        _vk.CmdSetBlendConstants(cb, constants);
        _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        DescriptorSet set = _descriptorSet;
        _vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, in set, 0, null);
        PushConstants(cb, target, state, textured);
        Buffer vertexBuffer = _vertexBuffer;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(cb, 0, 1, in vertexBuffer, in offset);
        _vk.CmdDraw(cb, (uint)vertexCount, 1, 0, 0);
        _vk.CmdEndRenderPass(cb);

        Copy(cb, _colorImage, ImageAspectFlags.ColorBit, 0, width, height, toImage: false);
        if (usesDepth)
        {
            Copy(cb, _depthImage, ImageAspectFlags.DepthBit, depthOffset, width, height, toImage: false);
        }

        MemoryBarrier hostRead = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.HostReadBit,
        };
        _vk.CmdPipelineBarrier(cb, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 1, in hostRead, 0, null, 0, null);
        _vk.EndCommandBuffer(cb).Ok("GE end");
    }

    private void PushConstants(CommandBuffer cb, GeTarget target, GeRenderState state, bool textured)
    {
        byte* data = stackalloc byte[PushConstantSize];
        float* floats = (float*)data;
        int* ints = (int*)data;
        floats[0] = target.Width;
        floats[1] = target.Height;
        int flags = 0;
        if (textured)
        {
            flags |= 1;
        }

        if (state.TextureAlpha)
        {
            flags |= 2;
        }

        if (state.ColorDouble)
        {
            flags |= 4;
        }

        if (state.AlphaTest && !state.ClearMode)
        {
            flags |= 8;
        }

        if (!state.ClearMode)
        {
            flags |= 32; // the secondary (specular) colour: 0 when the vertices have none
        }

        if (state.Fog && !state.ClearMode)
        {
            flags |= 16;
        }

        ints[2] = flags;
        ints[3] = state.TextureFunction;
        floats[4] = (state.TextureEnvironment & 0xFF) / 255f;
        floats[5] = ((state.TextureEnvironment >> 8) & 0xFF) / 255f;
        floats[6] = ((state.TextureEnvironment >> 16) & 0xFF) / 255f;
        floats[7] = 1;
        ints[8] = state.AlphaFunction;
        ints[9] = state.AlphaReference;
        ints[10] = state.AlphaMask;
        ints[11] = 0;
        floats[12] = (state.FogColor & 0xFF) / 255f;
        floats[13] = ((state.FogColor >> 8) & 0xFF) / 255f;
        floats[14] = ((state.FogColor >> 16) & 0xFF) / 255f;
        floats[15] = 1;
        _vk.CmdPushConstants(cb, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushConstantSize, data);
    }

    private void Transition(CommandBuffer cb, Image image, ImageAspectFlags aspect, ImageLayout from, ImageLayout to)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
            SrcAccessMask = AccessFlags.TransferWriteBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit | AccessFlags.ColorAttachmentReadBit
                | AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit
                | AccessFlags.ShaderReadBit,
        };
        _vk.CmdPipelineBarrier(cb, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, in barrier);
    }

    private void Copy(CommandBuffer cb, Image image, ImageAspectFlags aspect, ulong offset, uint width, uint height, bool toImage)
    {
        BufferImageCopy region = new()
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(aspect, 0, 0, 1),
            ImageExtent = new Extent3D(width, height, 1),
        };
        if (toImage)
        {
            _vk.CmdCopyBufferToImage(cb, _staging, image, ImageLayout.TransferDstOptimal, 1, in region);
        }
        else
        {
            _vk.CmdCopyImageToBuffer(cb, image, ImageLayout.TransferSrcOptimal, _staging, 1, in region);
        }
    }

    private void Submit()
    {
        CommandBuffer cb = _commandBuffer;
        SubmitInfo submit = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
        };
        Fence fence = _fence;
        _vk.ResetFences(_device, 1, in fence).Ok("GE reset fence");
        _vk.QueueSubmit(_core.Queue, 1, in submit, _fence).Ok("GE submit");
        _vk.WaitForFences(_device, 1, in fence, true, ulong.MaxValue).Ok("GE wait");
    }

    // ---- state to Vulkan

    private static BlendFactor SourceFactor(int factor, uint fixedColor)
    {
        return factor switch
        {
            0 => BlendFactor.DstColor,
            1 => BlendFactor.OneMinusDstColor,
            2 or 6 => BlendFactor.SrcAlpha,
            3 or 7 => BlendFactor.OneMinusSrcAlpha,
            4 or 8 => BlendFactor.DstAlpha,
            5 or 9 => BlendFactor.OneMinusDstAlpha,
            _ => FixedFactor(fixedColor),
        };
    }

    private static BlendFactor DestinationFactor(int factor, uint fixedColor)
    {
        return factor switch
        {
            0 => BlendFactor.SrcColor,
            1 => BlendFactor.OneMinusSrcColor,
            2 or 6 => BlendFactor.SrcAlpha,
            3 or 7 => BlendFactor.OneMinusSrcAlpha,
            4 or 8 => BlendFactor.DstAlpha,
            5 or 9 => BlendFactor.OneMinusDstAlpha,
            _ => FixedFactor(fixedColor),
        };
    }

    /// <summary>A fixed blend colour: white and black are One and Zero; another is the blend constant.</summary>
    private static BlendFactor FixedFactor(uint fixedColor)
    {
        return fixedColor switch
        {
            0xFFFFFF => BlendFactor.One,
            0 => BlendFactor.Zero,
            _ => BlendFactor.ConstantColor,
        };
    }

    /// <summary>The blend constant: the source's fixed colour when it needs one, else the destination's (one
    /// constant: when both sides need different ones, the destination's is approximated).</summary>
    private static uint BlendConstant(GeRenderState state)
    {
        bool sourceConstant = state.BlendSource >= 10 && FixedFactor(state.BlendFixedA) == BlendFactor.ConstantColor;
        if (sourceConstant)
        {
            if (state.BlendDestination >= 10 && FixedFactor(state.BlendFixedB) == BlendFactor.ConstantColor && state.BlendFixedB != state.BlendFixedA)
            {
                Log.WarnOnce("ge-blend-two-constants", "GE: blending with two different fixed colours is approximated");
            }

            return state.BlendFixedA;
        }

        return state.BlendFixedB;
    }

    private static BlendOp Equation(int equation)
    {
        switch (equation)
        {
            case 1:
                return BlendOp.Subtract;
            case 2:
                return BlendOp.ReverseSubtract;
            case 3:
                return BlendOp.Min;
            case 4:
                return BlendOp.Max;
            case 5:
                Log.WarnOnce("ge-blend-absdiff", "GE: the absolute-difference blend equation is approximated by add");
                return BlendOp.Add;
            default:
                return BlendOp.Add;
        }
    }

    private static CompareOp DepthCompare(int function)
    {
        return function switch
        {
            0 => CompareOp.Never,
            1 => CompareOp.Always,
            2 => CompareOp.Equal,
            3 => CompareOp.NotEqual,
            4 => CompareOp.Less,
            5 => CompareOp.LessOrEqual,
            6 => CompareOp.Greater,
            _ => CompareOp.GreaterOrEqual,
        };
    }

    /// <summary>The RGB channels the draw writes: a channel whose 8 mask bits are all set is kept (a partial mask is
    /// taken as no mask).</summary>
    private static ColorComponentFlags ColorMask(GeRenderState state)
    {
        if (state.ClearMode)
        {
            ColorComponentFlags clear = state.ClearColor ? ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit : 0;
            return state.ClearAlpha ? clear | ColorComponentFlags.ABit : clear;
        }

        ColorComponentFlags mask = 0;
        uint rgb = state.WriteMaskRgb;
        if ((rgb & 0xFF) != 0xFF)
        {
            mask |= ColorComponentFlags.RBit;
        }

        if (((rgb >> 8) & 0xFF) != 0xFF)
        {
            mask |= ColorComponentFlags.GBit;
        }

        if (((rgb >> 16) & 0xFF) != 0xFF)
        {
            mask |= ColorComponentFlags.BBit;
        }

        if ((rgb & 0xFF) is not (0 or 0xFF) || ((rgb >> 8) & 0xFF) is not (0 or 0xFF) || ((rgb >> 16) & 0xFF) is not (0 or 0xFF))
        {
            Log.WarnOnce("ge-partial-mask", "GE: partial colour bit masks are not applied yet");
        }

        return mask;
    }

    private Pipeline PipelineFor(GeTopology topology, GeRenderState state, bool usesDepth)
    {
        bool clear = state.ClearMode;
        bool blend = state.Blend && !clear;
        BlendFactor source = blend ? SourceFactor(state.BlendSource, state.BlendFixedA) : BlendFactor.One;
        BlendFactor destination = blend ? DestinationFactor(state.BlendDestination, state.BlendFixedB) : BlendFactor.Zero;
        BlendOp operation = blend ? Equation(state.BlendEquation) : BlendOp.Add;
        bool depthTest = usesDepth && (clear ? state.ClearDepth : state.DepthTest);
        CompareOp compare = clear ? CompareOp.Always : DepthCompare(state.DepthFunction);
        bool depthWrite = usesDepth && (clear ? state.ClearDepth : state.DepthWrite);
        ColorComponentFlags mask = ColorMask(state);

        ulong key = (ulong)topology
            | (blend ? 1UL << 2 : 0)
            | ((ulong)source << 3)
            | ((ulong)destination << 8)
            | ((ulong)operation << 13)
            | (depthTest ? 1UL << 17 : 0)
            | ((ulong)compare << 18)
            | (depthWrite ? 1UL << 21 : 0)
            | ((ulong)mask << 22);
        if (_pipelines.TryGetValue(key, out Pipeline existing))
        {
            return existing;
        }

        Pipeline created = CreatePipeline(topology, blend, source, destination, operation, depthTest, compare, depthWrite, mask);
        _pipelines[key] = created;
        return created;
    }

    private Pipeline CreatePipeline(GeTopology topology, bool blend, BlendFactor source, BlendFactor destination, BlendOp operation,
        bool depthTest, CompareOp compare, bool depthWrite, ColorComponentFlags mask)
    {
        byte* entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = _vertexModule, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = _fragmentModule, PName = entry };

            VertexInputBindingDescription binding = new(0, (uint)s_vertexStride, VertexInputRate.Vertex);
            VertexInputAttributeDescription* attributes = stackalloc VertexInputAttributeDescription[6];
            attributes[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32A32Sfloat, s_positionOffset);
            attributes[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32Sfloat, s_uvOffset);
            attributes[2] = new VertexInputAttributeDescription(2, 0, Format.R8G8B8A8Unorm, s_colorOffset);
            attributes[3] = new VertexInputAttributeDescription(3, 0, Format.R8G8B8A8Unorm, s_color1Offset);
            attributes[4] = new VertexInputAttributeDescription(4, 0, Format.R32Sfloat, s_fogOffset);
            attributes[5] = new VertexInputAttributeDescription(5, 0, Format.R32Sfloat, s_qOffset);
            PipelineVertexInputStateCreateInfo vertexInput = new()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 6,
                PVertexAttributeDescriptions = attributes,
            };
            PipelineInputAssemblyStateCreateInfo assembly = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = topology switch
                {
                    GeTopology.Points => PrimitiveTopology.PointList,
                    GeTopology.Lines => PrimitiveTopology.LineList,
                    _ => PrimitiveTopology.TriangleList,
                },
            };
            PipelineViewportStateCreateInfo viewport = new() { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            PipelineRasterizationStateCreateInfo rasterization = new()
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1,
            };
            PipelineMultisampleStateCreateInfo multisample = new() { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            PipelineDepthStencilStateCreateInfo depth = new()
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = depthTest,
                DepthWriteEnable = depthWrite,
                DepthCompareOp = depthTest ? compare : CompareOp.Always,
            };
            PipelineColorBlendAttachmentState attachment = new()
            {
                BlendEnable = blend,
                SrcColorBlendFactor = source,
                DstColorBlendFactor = destination,
                ColorBlendOp = operation,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.Zero,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = mask,
            };
            PipelineColorBlendStateCreateInfo colorBlend = new()
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &attachment,
            };
            DynamicState* dynamics = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor, DynamicState.BlendConstants };
            PipelineDynamicStateCreateInfo dynamic = new() { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 3, PDynamicStates = dynamics };
            GraphicsPipelineCreateInfo info = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &assembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depth,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamic,
                Layout = _pipelineLayout,
                RenderPass = _renderPass,
            };
            _vk.CreateGraphicsPipelines(_device, default, 1, in info, null, out Pipeline pipeline).Ok("GE pipeline");
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
        }
    }

    // ---- resources

    private RenderPass CreateRenderPass()
    {
        AttachmentDescription* attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = ColorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ColorAttachmentOptimal,
            FinalLayout = ImageLayout.TransferSrcOptimal,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = DepthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
            FinalLayout = ImageLayout.TransferSrcOptimal,
        };
        AttachmentReference color = new(0, ImageLayout.ColorAttachmentOptimal);
        AttachmentReference depth = new(1, ImageLayout.DepthStencilAttachmentOptimal);
        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &color,
            PDepthStencilAttachment = &depth,
        };
        SubpassDependency* dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.AllCommandsBit,
            DstStageMask = PipelineStageFlags.AllCommandsBit,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit
                | AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.AllCommandsBit,
            DstStageMask = PipelineStageFlags.AllCommandsBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
        };
        RenderPassCreateInfo info = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };
        _vk.CreateRenderPass(_device, in info, null, out RenderPass renderPass).Ok("GE render pass");
        return renderPass;
    }

    private (DescriptorSetLayout, DescriptorPool, DescriptorSet) CreateDescriptors()
    {
        DescriptorSetLayoutBinding binding = new()
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        DescriptorSetLayoutCreateInfo layoutInfo = new() { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding };
        _vk.CreateDescriptorSetLayout(_device, in layoutInfo, null, out DescriptorSetLayout layout).Ok("GE set layout");
        DescriptorPoolSize size = new(DescriptorType.CombinedImageSampler, 1);
        DescriptorPoolCreateInfo poolInfo = new() { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &size };
        _vk.CreateDescriptorPool(_device, in poolInfo, null, out DescriptorPool pool).Ok("GE descriptor pool");
        DescriptorSetAllocateInfo allocateInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        _vk.AllocateDescriptorSets(_device, in allocateInfo, out DescriptorSet set).Ok("GE descriptor set");
        return (layout, pool, set);
    }

    private PipelineLayout CreatePipelineLayout()
    {
        PushConstantRange range = new(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushConstantSize);
        DescriptorSetLayout setLayout = _setLayout;
        PipelineLayoutCreateInfo info = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range,
        };
        _vk.CreatePipelineLayout(_device, in info, null, out PipelineLayout layout).Ok("GE pipeline layout");
        return layout;
    }

    private ShaderModule CreateModule(ShaderCompiler compiler, string glsl, bool vertex)
    {
        byte[] spirv = compiler.Compile(glsl, vertex, out string? error) ?? throw new InvalidOperationException($"GE shader: {error}");
        fixed (byte* code = spirv)
        {
            ShaderModuleCreateInfo info = new() { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            _vk.CreateShaderModule(_device, in info, null, out ShaderModule module).Ok("GE shader module");
            return module;
        }
    }

    private Sampler CreateSampler(bool linear, bool clampU, bool clampV)
    {
        Filter filter = linear ? Filter.Linear : Filter.Nearest;
        SamplerCreateInfo info = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = clampU ? SamplerAddressMode.ClampToEdge : SamplerAddressMode.Repeat,
            AddressModeV = clampV ? SamplerAddressMode.ClampToEdge : SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0,
        };
        _vk.CreateSampler(_device, in info, null, out Sampler sampler).Ok("GE sampler");
        return sampler;
    }

    private void UpdateDescriptor(GeRenderState state, bool textured)
    {
        int samplerIndex = textured ? (state.TextureLinear ? 1 : 0) | (state.ClampU ? 2 : 0) | (state.ClampV ? 4 : 0) : 0;
        DescriptorImageInfo image = new(_samplers[samplerIndex], _textureView, ImageLayout.ShaderReadOnlyOptimal);
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &image,
        };
        _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
    }

    private (Image, DeviceMemory, ImageView) CreateImage(int width, int height, Format format, ImageUsageFlags usage, ImageAspectFlags aspect)
    {
        ImageCreateInfo info = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        _vk.CreateImage(_device, in info, null, out Image image).Ok("GE image");
        _vk.GetImageMemoryRequirements(_device, image, out MemoryRequirements requirements);
        DeviceMemory memory = _core.AllocateMemory(requirements.Size, _core.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            MemCat.RenderTarget, "vkAllocateMemory(GE image)");
        _vk.BindImageMemory(_device, image, memory, 0).Ok("GE bind image");
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        _vk.CreateImageView(_device, in viewInfo, null, out ImageView view).Ok("GE image view");
        return (image, memory, view);
    }

    private void DestroyImage(ref Image image, ref DeviceMemory memory, ref ImageView view)
    {
        if (image.Handle == 0)
        {
            return;
        }

        _vk.DestroyImageView(_device, view, null);
        _vk.DestroyImage(_device, image, null);
        _core.FreeMemory(memory);
        image = default;
        memory = default;
        view = default;
    }

    private void EnsureTarget(int width, int height)
    {
        if (width == _targetWidth && height == _targetHeight)
        {
            return;
        }

        if (_framebuffer.Handle != 0)
        {
            _vk.DestroyFramebuffer(_device, _framebuffer, null);
        }

        DestroyImage(ref _colorImage, ref _colorMemory, ref _colorView);
        DestroyImage(ref _depthImage, ref _depthMemory, ref _depthView);
        (_colorImage, _colorMemory, _colorView) = CreateImage(width, height, ColorFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        (_depthImage, _depthMemory, _depthView) = CreateImage(width, height, DepthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.DepthBit);
        ImageView* views = stackalloc ImageView[2];
        views[0] = _colorView;
        views[1] = _depthView;
        FramebufferCreateInfo info = new()
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 2,
            PAttachments = views,
            Width = (uint)width,
            Height = (uint)height,
            Layers = 1,
        };
        _vk.CreateFramebuffer(_device, in info, null, out _framebuffer).Ok("GE framebuffer");
        _targetWidth = width;
        _targetHeight = height;
    }

    private void EnsureTexture(int width, int height)
    {
        if (width == _textureWidth && height == _textureHeight)
        {
            return;
        }

        DestroyImage(ref _textureImage, ref _textureMemory, ref _textureView);
        (_textureImage, _textureMemory, _textureView) = CreateImage(width, height, ColorFormat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        _textureWidth = width;
        _textureHeight = height;
        _textureReadable = false;
    }

    private void EnsureStaging(ulong size)
    {
        if (size <= _stagingSize)
        {
            return;
        }

        if (_staging.Handle != 0)
        {
            _core.DestroyBuffer(_staging, _stagingMemory);
        }

        ulong rounded = Math.Max(size, _stagingSize * 2);
        (_staging, _stagingMemory, _stagingMapped) = _core.CreateHostBuffer(rounded, BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
            MemCat.Readback);
        _stagingSize = rounded;
    }

    private void EnsureVertexBuffer(ulong size)
    {
        if (size <= _vertexSize)
        {
            return;
        }

        if (_vertexBuffer.Handle != 0)
        {
            _core.DestroyBuffer(_vertexBuffer, _vertexMemory);
        }

        ulong rounded = Math.Max(size, Math.Max(_vertexSize * 2, 4096));
        (_vertexBuffer, _vertexMemory, _vertexMapped) = _core.CreateHostBuffer(rounded, BufferUsageFlags.VertexBufferBit, MemCat.UploadRing);
        _vertexSize = rounded;
    }

    public void Dispose()
    {
        _vk.DeviceWaitIdle(_device);
        foreach (Pipeline pipeline in _pipelines.Values)
        {
            _vk.DestroyPipeline(_device, pipeline, null);
        }

        foreach (Sampler sampler in _samplers)
        {
            _vk.DestroySampler(_device, sampler, null);
        }

        if (_framebuffer.Handle != 0)
        {
            _vk.DestroyFramebuffer(_device, _framebuffer, null);
        }

        DestroyImage(ref _colorImage, ref _colorMemory, ref _colorView);
        DestroyImage(ref _depthImage, ref _depthMemory, ref _depthView);
        DestroyImage(ref _textureImage, ref _textureMemory, ref _textureView);
        if (_staging.Handle != 0)
        {
            _core.DestroyBuffer(_staging, _stagingMemory);
        }

        if (_vertexBuffer.Handle != 0)
        {
            _core.DestroyBuffer(_vertexBuffer, _vertexMemory);
        }

        _vk.DestroyShaderModule(_device, _vertexModule, null);
        _vk.DestroyShaderModule(_device, _fragmentModule, null);
        _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
        _vk.DestroyRenderPass(_device, _renderPass, null);
        _vk.DestroyFence(_device, _fence, null);
        _vk.DestroyCommandPool(_device, _commandPool, null);
    }
}
