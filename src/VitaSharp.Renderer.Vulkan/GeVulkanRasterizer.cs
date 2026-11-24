using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using VitaSharp.Core.Compat.Ge;
using VitaSharp.Core.Util;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;
/// <summary>
/// The PSP GE's rasterizer on Vulkan (docs/notes/psp-compat.md, "The GE"), separate from the GXM renderer, with the
/// GE's per-fragment state: the texture function (modulate, decal, blend, replace, add, colour doubling; sampled at
/// U/Q, V/Q), the secondary (separate specular) colour added after it, fog (FOGCOLOR by the vertices' factor), the
/// alpha test, blending (the GE's factors and equations; a fixed factor is the blend constant), the depth test and
/// write, the scissor, the RGB write masks and clear mode (colour, alpha and depth written as they are). The
/// framebuffer's alpha is the PSP's stencil: normal draws leave it, clear mode writes it.
/// <para>
/// The framebuffers and depth buffers stay on the GPU (RGBA8 / D16 images per address, stride and format) between
/// draws and between lists, as the GXM renderer keeps its surfaces: a surface is loaded from memory at its first use
/// and again only when memory changed under it (its hash, checked once between write-backs: the PSP's CPU or a block
/// transfer wrote it), and written back at <see cref="Flush"/> (the GE's list stopped, or memory a surface holds is
/// about to be read). The draws of a list go in one command buffer, one submission per flush; consecutive draws with
/// the same pipeline, texture, constants and scissor become one vkCmdDraw. Textures are uploaded once per decoded
/// content (the GE's texture id). Not thread-safe: the GE runs one list at a time, on the core's thread.
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
    private const int PushConstantSize = 96;

    private const string VertexShader = """
        #version 450
        layout(location = 0) in vec4 inPosition;
        layout(location = 1) in vec2 inUv;
        layout(location = 2) in vec4 inColor;
        layout(location = 3) in vec4 inColor1;
        layout(location = 4) in float inFog;
        layout(location = 5) in float inQ;
        layout(push_constant) uniform Constants { vec2 size; int flags; int textureFunction; vec4 environment; ivec4 alpha; vec4 fog; ivec4 colorTest; ivec4 dither; } pc;
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
        layout(push_constant) uniform Constants { vec2 size; int flags; int textureFunction; vec4 environment; ivec4 alpha; vec4 fog; ivec4 colorTest; ivec4 dither; } pc;
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
                vec2 uv = vUvq.xy / q;
                vec4 texel = pc.colorTest.w == 1 ? textureLod(textureSampler, uv, pc.environment.w) : texture(textureSampler, uv, pc.environment.w);
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

            if ((pc.flags & 64) != 0)
            {
                ivec3 channels = ivec3(color.rgb * 255.0 + 0.5);
                int packed = (channels.r | (channels.g << 8) | (channels.b << 16)) & pc.colorTest.z;
                int reference = pc.colorTest.y & pc.colorTest.z;
                bool pass = pc.colorTest.x == 1 || (pc.colorTest.x == 2 && packed == reference) || (pc.colorTest.x == 3 && packed != reference);
                if (!pass)
                {
                    discard;
                }
            }

            if ((pc.flags & 128) != 0)
            {
                ivec2 cell = ivec2(gl_FragCoord.xy) & 3;
                int offset = (pc.dither[cell.y] >> (cell.x * 4)) & 15;
                offset = offset >= 8 ? offset - 16 : offset;
                color.rgb = clamp(color.rgb + float(offset) / 255.0, 0.0, 1.0);
            }

            outColor = color;
        }
        """;

    /// <summary>A framebuffer or depth buffer kept on the GPU: its image, whether the GPU holds changes memory does not
    /// have yet, and the hash of its memory when they last agreed.</summary>
    private sealed class Surface
    {
        public GeSurface Key;
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public ImageLayout Layout = ImageLayout.Undefined;
        public bool Loaded;          // the image has the memory's pixels (or newer)
        public bool GpuDirty;        // drawn into since the last write-back
        public bool Checked;         // compared with memory since the last write-back
        public ulong MemoryHash;
        public Buffer Staging;       // the upload and read-back buffer (the surface's pixels as the image holds them)
        public DeviceMemory StagingMemory;
        public nint StagingMapped;
    }

    private sealed class CachedTexture
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public int Width;
        public int Height;
        public int Levels = 1;
        public long LastUse;
    }

    /// <summary>The draw being gathered: consecutive draws with the same pipeline, texture, constants, scissor and
    /// blend constant whose vertices follow each other become one vkCmdDraw.</summary>
    private struct PendingDraw
    {
        public bool Active;
        public Pipeline Pipeline;
        public DescriptorSet Set;
        public Rect2D Scissor;
        public uint BlendConstant;
        public uint FirstVertex;
        public uint VertexCount;
    }

    private const int TextureCacheLimit = 128;
    private const uint DescriptorSetsPerBatch = 256;
    private const ulong UploadRingSize = 8 << 20;

    private readonly VkCore _core;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer _commandBuffer;
    private readonly Fence _fence;
    private readonly RenderPass _renderPass;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _descriptorPool;
    private readonly PipelineLayout _pipelineLayout;
    private readonly ShaderModule _vertexModule;
    private readonly ShaderModule _fragmentModule;
    private readonly Sampler[] _samplers = new Sampler[64]; // made at first use: see SamplerIndex
    private readonly Dictionary<ulong, Pipeline> _pipelines = [];
    private readonly Dictionary<(uint, int, int), Surface> _surfaces = [];
    private readonly Dictionary<(ulong, ulong), Framebuffer> _framebuffers = [];
    private readonly Dictionary<(int, int), Surface> _scratchDepth = [];
    private readonly Dictionary<long, CachedTexture> _textures = [];
    private readonly Dictionary<(long, int), DescriptorSet> _sets = [];
    private readonly byte[] _pushConstants = new byte[PushConstantSize];
    private readonly byte[] _pendingConstants = new byte[PushConstantSize];

    private readonly CachedTexture _dummyTexture;
    private bool _recording;
    private Surface? _passColor;
    private Surface? _passDepth;
    private PendingDraw _pending;
    private uint _setsUsed;
    private long _batch;

    private readonly Buffer _uploadRing;
    private readonly DeviceMemory _uploadRingMemory;
    private readonly nint _uploadRingMapped;
    private ulong _uploadRingUsed;
    private Buffer _vertexBuffer;
    private DeviceMemory _vertexMemory;
    private nint _vertexMapped;
    private ulong _vertexSize;
    private uint _verticesUsed;

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
        _setLayout = CreateSetLayout();
        _descriptorPool = CreateDescriptorPool();
        _pipelineLayout = CreatePipelineLayout();
        _vertexModule = CreateModule(compiler, VertexShader, vertex: true);
        _fragmentModule = CreateModule(compiler, FragmentShader, vertex: false);

        (_uploadRing, _uploadRingMemory, _uploadRingMapped) = _core.CreateHostBuffer(UploadRingSize, BufferUsageFlags.TransferSrcBit, MemCat.UploadRing);
        EnsureVertexBuffer(1UL << 20); // before anything is recorded: growing it later submits the batch first
        _dummyTexture = CreateTexture(1, 1);
    }

    /// <summary>The device takes the GE's logic ops (VkCore.LogicOp).</summary>
    public bool SupportsLogicOp => _core.LogicOp;

    /// <summary>The batches given (each PRIM).</summary>
    public long Draws { get; private set; }

    /// <summary>The vkCmdDraw calls recorded (batches with the same state merge into one).</summary>
    public long RecordedDraws { get; private set; }

    /// <summary>The submissions to the queue (one per flush, more when a batch outgrows its rings).</summary>
    public long Submissions { get; private set; }

    /// <summary>Surfaces loaded from memory / written back to it.</summary>
    public long SurfaceUploads { get; private set; }

    public long SurfaceReadbacks { get; private set; }

    public void Draw(GeTopology topology, ReadOnlySpan<GeVertex> vertices, GeRenderState state, GeSurface color, GeSurface? depth, IGeMemory memory)
    {
        if (vertices.Length == 0 || color.Stride <= 0 || color.Height <= 0)
        {
            return;
        }

        Draws++;
        if (_verticesUsed + (uint)vertices.Length > _vertexSize / (ulong)s_vertexStride || _setsUsed >= DescriptorSetsPerBatch)
        {
            SubmitAndWait(readBack: false, memory);
        }

        EnsureVertexBuffer((ulong)(_verticesUsed + (uint)vertices.Length) * (ulong)s_vertexStride);
        Surface colorSurface = SurfaceFor(color, memory);
        Surface depthSurface = depth is { } depthKey ? SurfaceFor(depthKey, memory) : ScratchDepth(color.Stride, color.Height);
        bool textured = state.Texture && !state.ClearMode && state.TexturePixels.Length >= state.TextureWidth * state.TextureHeight;
        CachedTexture texture = textured ? TextureFor(state) : _dummyTexture;
        BeginRecording();
        EnsureLoaded(colorSurface, memory);
        if (depth != null)
        {
            EnsureLoaded(depthSurface, memory);
        }
        else
        {
            EnsureAttachment(depthSurface, ImageAspectFlags.DepthBit, ImageLayout.DepthStencilAttachmentOptimal);
        }

        Pipeline pipeline = PipelineFor(topology, state, depth != null);
        DescriptorSet set = SetFor(texture, state, textured);
        int left = Math.Clamp(state.ScissorLeft, 0, color.Stride);
        int top = Math.Clamp(state.ScissorTop, 0, color.Height);
        int right = Math.Clamp(state.ScissorRight + 1, left, color.Stride);
        int bottom = Math.Clamp(state.ScissorBottom + 1, top, color.Height);
        Rect2D scissor = new(new Offset2D(left, top), new Extent2D((uint)(right - left), (uint)(bottom - top)));
        uint blendConstant = BlendConstant(state);
        FillPushConstants(_pushConstants, color.Stride, color.Height, state, textured);

        uint first = _verticesUsed;
        MemoryMarshal.AsBytes(vertices).CopyTo(new Span<byte>((void*)(_vertexMapped + (nint)(first * (uint)s_vertexStride)), vertices.Length * s_vertexStride));
        _verticesUsed += (uint)vertices.Length;

        BeginPass(colorSurface, depthSurface);
        bool merges = _pending.Active && _pending.Pipeline.Handle == pipeline.Handle && _pending.Set.Handle == set.Handle
            && _pending.Scissor.Offset.X == scissor.Offset.X && _pending.Scissor.Offset.Y == scissor.Offset.Y
            && _pending.Scissor.Extent.Width == scissor.Extent.Width && _pending.Scissor.Extent.Height == scissor.Extent.Height
            && _pending.BlendConstant == blendConstant && _pending.FirstVertex + _pending.VertexCount == first
            && _pendingConstants.AsSpan().SequenceEqual(_pushConstants);
        if (merges)
        {
            _pending.VertexCount += (uint)vertices.Length;
        }
        else
        {
            RecordPending();
            _pending = new PendingDraw
            {
                Active = true,
                Pipeline = pipeline,
                Set = set,
                Scissor = scissor,
                BlendConstant = blendConstant,
                FirstVertex = first,
                VertexCount = (uint)vertices.Length,
            };
            _pushConstants.CopyTo(_pendingConstants, 0);
        }

        if (ColorMask(state) != 0)
        {
            colorSurface.GpuDirty = true;
        }

        if (depth != null && (state.ClearMode ? state.ClearDepth : state.DepthWrite))
        {
            depthSurface.GpuDirty = true;
        }
    }

    public void Flush(IGeMemory memory)
    {
        bool dirty = false;
        foreach (Surface surface in _surfaces.Values)
        {
            dirty |= surface.GpuDirty;
        }

        if (_recording || dirty)
        {
            SubmitAndWait(readBack: true, memory);
        }

        foreach (Surface surface in _surfaces.Values)
        {
            surface.Checked = false;
        }
    }

    public bool Holds(uint address, int length)
    {
        foreach (Surface surface in _surfaces.Values)
        {
            if (surface.GpuDirty && surface.Key.Overlaps(address, length))
            {
                return true;
            }
        }

        return false;
    }

    public void Invalidate(uint address, int length)
    {
        foreach (Surface surface in _surfaces.Values)
        {
            if (surface.Key.Overlaps(address, length))
            {
                surface.Loaded = false;
                surface.Checked = false;
            }
        }
    }

    // ---- surfaces

    /// <summary>The GPU side of a surface; one of the same address, stride and format that is too short is replaced (its
    /// changes written back first).</summary>
    private Surface SurfaceFor(GeSurface key, IGeMemory memory)
    {
        (uint, int, int) id = (key.Address, key.Stride, key.Format);
        if (_surfaces.TryGetValue(id, out Surface? existing))
        {
            if (existing.Key.Height >= key.Height)
            {
                return existing;
            }

            if (existing.GpuDirty || _recording)
            {
                SubmitAndWait(readBack: true, memory);
            }

            DestroySurface(existing);
            _surfaces.Remove(id);
        }

        bool depth = key.Format == GeSurface.DepthFormat;
        Surface surface = new() { Key = key };
        (surface.Image, surface.Memory, surface.View) = CreateImage(key.Stride, key.Height, depth ? DepthFormat : ColorFormat,
            (depth ? ImageUsageFlags.DepthStencilAttachmentBit : ImageUsageFlags.ColorAttachmentBit) | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            depth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit);
        ulong stagingBytes = (ulong)key.Stride * (ulong)key.Height * (depth ? 2UL : 4UL);
        (surface.Staging, surface.StagingMemory, surface.StagingMapped) = _core.CreateHostBuffer(stagingBytes,
            BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit, MemCat.Readback);
        _surfaces[id] = surface;
        return surface;
    }

    /// <summary>A depth image for draws without a depth buffer (the render pass has one): never loaded nor read back.</summary>
    private Surface ScratchDepth(int width, int height)
    {
        if (_scratchDepth.TryGetValue((width, height), out Surface? existing))
        {
            return existing;
        }

        Surface surface = new() { Key = new GeSurface(0, width, height, GeSurface.DepthFormat) };
        (surface.Image, surface.Memory, surface.View) = CreateImage(width, height, DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);
        _scratchDepth[(width, height)] = surface;
        return surface;
    }

    /// <summary>Loads a surface from memory when the GPU does not have its current pixels: first use, or memory changed
    /// since the last write-back (the CPU, a block transfer). Checked once between write-backs.</summary>
    private void EnsureLoaded(Surface surface, IGeMemory memory)
    {
        bool depth = surface.Key.Format == GeSurface.DepthFormat;
        ImageLayout attachment = depth ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.ColorAttachmentOptimal;
        ImageAspectFlags aspect = depth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
        if (surface.Checked || surface.GpuDirty)
        {
            EnsureAttachment(surface, aspect, attachment);
            return;
        }

        surface.Checked = true;
        Span<byte> source = memory.Span(surface.Key.Address, surface.Key.Bytes);
        ulong hash = source.Length == surface.Key.Bytes ? System.IO.Hashing.XxHash3.HashToUInt64(source) : 0;
        if (surface.Loaded && hash == surface.MemoryHash)
        {
            EnsureAttachment(surface, aspect, attachment);
            return;
        }

        int pixels = surface.Key.Stride * surface.Key.Height;
        if (source.Length == surface.Key.Bytes)
        {
            if (depth)
            {
                source.CopyTo(new Span<byte>((void*)surface.StagingMapped, pixels * 2));
            }
            else
            {
                GePixels.ToRgba(source, surface.Key.Format, new Span<uint>((void*)surface.StagingMapped, pixels));
            }
        }

        EndPass();
        Transition(_commandBuffer, surface.Image, aspect, surface.Layout, ImageLayout.TransferDstOptimal);
        CopyBuffer(surface.Staging, 0, surface.Image, aspect, (uint)surface.Key.Stride, (uint)surface.Key.Height, toImage: true);
        Transition(_commandBuffer, surface.Image, aspect, ImageLayout.TransferDstOptimal, attachment);
        surface.Layout = attachment;
        surface.Loaded = true;
        surface.MemoryHash = hash;
        SurfaceUploads++;
    }

    private void EnsureAttachment(Surface surface, ImageAspectFlags aspect, ImageLayout attachment)
    {
        if (surface.Layout == attachment)
        {
            return;
        }

        EndPass();
        Transition(_commandBuffer, surface.Image, aspect, surface.Layout, attachment);
        surface.Layout = attachment;
    }

    private void DestroySurface(Surface surface)
    {
        foreach ((ulong, ulong) key in _framebuffers.Keys.ToArray())
        {
            if (key.Item1 == surface.View.Handle || key.Item2 == surface.View.Handle)
            {
                _vk.DestroyFramebuffer(_device, _framebuffers[key], null);
                _framebuffers.Remove(key);
            }
        }

        DestroyImage(ref surface.Image, ref surface.Memory, ref surface.View);
        if (surface.Staging.Handle != 0)
        {
            _core.DestroyBuffer(surface.Staging, surface.StagingMemory);
            surface.Staging = default;
        }
    }

    // ---- textures

    private CachedTexture CreateTexture(int width, int height)
    {
        CachedTexture texture = new() { Width = width, Height = height };
        (texture.Image, texture.Memory, texture.View) = CreateImage(width, height, ColorFormat, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        BeginRecording();
        Transition(_commandBuffer, texture.Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal);
        return texture;
    }

    /// <summary>The image of a decoded texture by its id (uploaded once; the oldest go when the cache is full).</summary>
    private CachedTexture TextureFor(GeRenderState state)
    {
        int levels = Math.Max(state.TextureLevels, 1);
        if (_textures.TryGetValue(state.TextureId, out CachedTexture? cached) && cached.Width == state.TextureWidth
            && cached.Height == state.TextureHeight && cached.Levels == levels)
        {
            cached.LastUse = _batch;
            return cached;
        }

        int pixels = 0;
        for (int level = 0; level < levels; level++)
        {
            pixels += Math.Max(state.TextureWidth >> level, 1) * Math.Max(state.TextureHeight >> level, 1);
        }

        ulong bytes = (ulong)pixels * 4;
        if (_uploadRingUsed + bytes > UploadRingSize)
        {
            SubmitAndWait(readBack: false, null);
        }

        if (_textures.Count >= TextureCacheLimit)
        {
            EvictTextures();
        }

        CachedTexture texture = new() { Width = state.TextureWidth, Height = state.TextureHeight, Levels = levels, LastUse = _batch };
        (texture.Image, texture.Memory, texture.View) = CreateImage(texture.Width, texture.Height, ColorFormat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit, levels);
        ulong offset = _uploadRingUsed;
        state.TexturePixels.AsSpan(0, pixels).CopyTo(new Span<uint>((void*)(_uploadRingMapped + (nint)offset), pixels));
        _uploadRingUsed = (offset + bytes + 15) & ~15UL;
        BeginRecording();
        EndPass();
        Transition(_commandBuffer, texture.Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, levels);
        ulong levelOffset = offset;
        for (int level = 0; level < levels; level++)
        {
            uint levelWidth = (uint)Math.Max(texture.Width >> level, 1);
            uint levelHeight = (uint)Math.Max(texture.Height >> level, 1);
            BufferImageCopy region = new()
            {
                BufferOffset = levelOffset,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level, 0, 1),
                ImageExtent = new Extent3D(levelWidth, levelHeight, 1),
            };
            _vk.CmdCopyBufferToImage(_commandBuffer, _uploadRing, texture.Image, ImageLayout.TransferDstOptimal, 1, in region);
            levelOffset += levelWidth * levelHeight * 4;
        }

        Transition(_commandBuffer, texture.Image, ImageAspectFlags.ColorBit, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, levels);
        if (cached != null)
        {
            RetireTexture(state.TextureId, cached);
        }

        _textures[state.TextureId] = texture;
        return texture;
    }

    /// <summary>Half the cache, the least recently used (after the work that may sample them is done).</summary>
    private void EvictTextures()
    {
        SubmitAndWait(readBack: false, null);
        long[] oldest = _textures.OrderBy(entry => entry.Value.LastUse).Take(_textures.Count / 2).Select(entry => entry.Key).ToArray();
        foreach (long id in oldest)
        {
            CachedTexture texture = _textures[id];
            _textures.Remove(id);
            DestroyImage(ref texture.Image, ref texture.Memory, ref texture.View);
        }
    }

    private readonly List<CachedTexture> _retired = [];

    /// <summary>A texture replaced by a new upload of the same id: destroyed once the batch that may sample it is done.</summary>
    private void RetireTexture(long id, CachedTexture texture)
    {
        _textures.Remove(id);
        _retired.Add(texture);
    }

    private DescriptorSet SetFor(CachedTexture texture, GeRenderState state, bool textured)
    {
        int samplerIndex = textured ? SamplerIndex(state) : 0;
        long textureKey = (long)texture.View.Handle;
        if (_sets.TryGetValue((textureKey, samplerIndex), out DescriptorSet existing))
        {
            return existing;
        }

        DescriptorSetLayout layout = _setLayout;
        DescriptorSetAllocateInfo allocateInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        _vk.AllocateDescriptorSets(_device, in allocateInfo, out DescriptorSet set).Ok("GE descriptor set");
        _setsUsed++;
        DescriptorImageInfo image = new(SamplerAt(samplerIndex), texture.View, ImageLayout.ShaderReadOnlyOptimal);
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &image,
        };
        _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
        _sets[(textureKey, samplerIndex)] = set;
        return set;
    }

    // ---- recording

    private void BeginRecording()
    {
        if (_recording)
        {
            return;
        }

        _vk.ResetCommandBuffer(_commandBuffer, 0).Ok("GE reset command buffer");
        CommandBufferBeginInfo begin = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _vk.BeginCommandBuffer(_commandBuffer, in begin).Ok("GE begin");
        _recording = true;
    }

    private void BeginPass(Surface color, Surface depth)
    {
        if (_passColor == color && _passDepth == depth)
        {
            return;
        }

        EndPass();
        Framebuffer framebuffer = FramebufferFor(color, depth);
        RenderPassBeginInfo pass = new()
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)color.Key.Stride, (uint)color.Key.Height)),
        };
        _vk.CmdBeginRenderPass(_commandBuffer, in pass, SubpassContents.Inline);
        Viewport viewport = new(0, 0, color.Key.Stride, color.Key.Height, 0, 1);
        _vk.CmdSetViewport(_commandBuffer, 0, 1, in viewport);
        Buffer vertexBuffer = _vertexBuffer;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(_commandBuffer, 0, 1, in vertexBuffer, in offset);
        _passColor = color;
        _passDepth = depth;
    }

    private void EndPass()
    {
        if (_passColor == null)
        {
            return;
        }

        RecordPending();
        _vk.CmdEndRenderPass(_commandBuffer);
        _passColor = null;
        _passDepth = null;
    }

    private void RecordPending()
    {
        if (!_pending.Active)
        {
            return;
        }

        CommandBuffer cb = _commandBuffer;
        Rect2D scissor = _pending.Scissor;
        _vk.CmdSetScissor(cb, 0, 1, in scissor);
        float* constants = stackalloc float[4];
        constants[0] = (_pending.BlendConstant & 0xFF) / 255f;
        constants[1] = ((_pending.BlendConstant >> 8) & 0xFF) / 255f;
        constants[2] = ((_pending.BlendConstant >> 16) & 0xFF) / 255f;
        constants[3] = 1;
        _vk.CmdSetBlendConstants(cb, constants);
        _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pending.Pipeline);
        DescriptorSet set = _pending.Set;
        _vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, in set, 0, null);
        fixed (byte* data = _pendingConstants)
        {
            _vk.CmdPushConstants(cb, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushConstantSize, data);
        }

        _vk.CmdDraw(cb, _pending.VertexCount, 1, _pending.FirstVertex, 0);
        RecordedDraws++;
        _pending.Active = false;
    }

    private Framebuffer FramebufferFor(Surface color, Surface depth)
    {
        (ulong, ulong) key = (color.View.Handle, depth.View.Handle);
        if (_framebuffers.TryGetValue(key, out Framebuffer existing))
        {
            return existing;
        }

        ImageView* views = stackalloc ImageView[2];
        views[0] = color.View;
        views[1] = depth.View;
        FramebufferCreateInfo info = new()
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 2,
            PAttachments = views,
            Width = (uint)color.Key.Stride,
            Height = (uint)Math.Min(color.Key.Height, depth.Key.Height),
            Layers = 1,
        };
        _vk.CreateFramebuffer(_device, in info, null, out Framebuffer framebuffer).Ok("GE framebuffer");
        _framebuffers[key] = framebuffer;
        return framebuffer;
    }

    /// <summary>Ends the batch: the draws submitted and waited for; with <paramref name="readBack"/>, every surface the
    /// GPU changed is copied back to memory (and becomes clean).</summary>
    private void SubmitAndWait(bool readBack, IGeMemory? memory)
    {
        List<Surface> written = [];
        if (readBack)
        {
            foreach (Surface surface in _surfaces.Values)
            {
                if (surface.GpuDirty)
                {
                    written.Add(surface);
                }
            }
        }

        if (!_recording && written.Count == 0)
        {
            return;
        }

        BeginRecording();
        EndPass();
        foreach (Surface surface in written)
        {
            bool depth = surface.Key.Format == GeSurface.DepthFormat;
            ImageAspectFlags aspect = depth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
            ImageLayout attachment = depth ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.ColorAttachmentOptimal;
            Transition(_commandBuffer, surface.Image, aspect, surface.Layout, ImageLayout.TransferSrcOptimal);
            CopyBuffer(surface.Staging, 0, surface.Image, aspect, (uint)surface.Key.Stride, (uint)surface.Key.Height, toImage: false);
            Transition(_commandBuffer, surface.Image, aspect, ImageLayout.TransferSrcOptimal, attachment);
            surface.Layout = attachment;
        }

        MemoryBarrier hostRead = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.HostReadBit,
        };
        _vk.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 1, in hostRead, 0, null, 0, null);
        _vk.EndCommandBuffer(_commandBuffer).Ok("GE end");
        CommandBuffer cb = _commandBuffer;
        SubmitInfo submit = new() { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cb };
        Fence fence = _fence;
        _vk.ResetFences(_device, 1, in fence).Ok("GE reset fence");
        _vk.QueueSubmit(_core.Queue, 1, in submit, _fence).Ok("GE submit");
        _vk.WaitForFences(_device, 1, in fence, true, ulong.MaxValue).Ok("GE wait");
        Submissions++;
        _recording = false;
        _batch++;

        foreach (Surface surface in written)
        {
            WriteBack(surface, memory!);
        }

        _vk.ResetDescriptorPool(_device, _descriptorPool, 0).Ok("GE reset descriptor pool");
        _sets.Clear();
        _setsUsed = 0;
        _uploadRingUsed = 0;
        _verticesUsed = 0;
        foreach (CachedTexture texture in _retired)
        {
            CachedTexture retired = texture;
            DestroyImage(ref retired.Image, ref retired.Memory, ref retired.View);
        }

        _retired.Clear();
    }

    private void WriteBack(Surface surface, IGeMemory memory)
    {
        int pixels = surface.Key.Stride * surface.Key.Height;
        Span<byte> destination = memory.Span(surface.Key.Address, surface.Key.Bytes);
        if (destination.Length == surface.Key.Bytes)
        {
            if (surface.Key.Format == GeSurface.DepthFormat)
            {
                new Span<byte>((void*)surface.StagingMapped, pixels * 2).CopyTo(destination);
            }
            else
            {
                GePixels.FromRgba(new Span<uint>((void*)surface.StagingMapped, pixels), surface.Key.Format, destination);
            }

            surface.MemoryHash = System.IO.Hashing.XxHash3.HashToUInt64(destination);
        }

        surface.GpuDirty = false;
        surface.Loaded = true;
        SurfaceReadbacks++;
    }

    private void CopyBuffer(Buffer buffer, ulong offset, Image image, ImageAspectFlags aspect, uint width, uint height, bool toImage)
    {
        BufferImageCopy region = new()
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(aspect, 0, 0, 1),
            ImageExtent = new Extent3D(width, height, 1),
        };
        if (toImage)
        {
            _vk.CmdCopyBufferToImage(_commandBuffer, buffer, image, ImageLayout.TransferDstOptimal, 1, in region);
        }
        else
        {
            _vk.CmdCopyImageToBuffer(_commandBuffer, image, ImageLayout.TransferSrcOptimal, buffer, 1, in region);
        }
    }

    private void Transition(CommandBuffer cb, Image image, ImageAspectFlags aspect, ImageLayout from, ImageLayout to, int levels = 1)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, (uint)levels, 0, 1),
            SrcAccessMask = AccessFlags.TransferWriteBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit | AccessFlags.ColorAttachmentReadBit
                | AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit
                | AccessFlags.ShaderReadBit,
        };
        _vk.CmdPipelineBarrier(cb, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, in barrier);
    }

    private static void FillPushConstants(Span<byte> data, int width, int height, GeRenderState state, bool textured)
    {
        Span<float> floats = MemoryMarshal.Cast<byte, float>(data);
        Span<int> ints = MemoryMarshal.Cast<byte, int>(data);
        floats[0] = width;
        floats[1] = height;
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

        if (state.Fog && !state.ClearMode)
        {
            flags |= 16;
        }

        if (!state.ClearMode)
        {
            flags |= 32; // the secondary (specular) colour: 0 when the vertices have none
        }

        if (state.ColorTest && !state.ClearMode)
        {
            flags |= 64;
        }

        if (state.Dither && !state.ClearMode)
        {
            flags |= 128;
        }

        ints[2] = flags;
        ints[3] = state.TextureFunction;
        floats[4] = (state.TextureEnvironment & 0xFF) / 255f;
        floats[5] = ((state.TextureEnvironment >> 8) & 0xFF) / 255f;
        floats[6] = ((state.TextureEnvironment >> 16) & 0xFF) / 255f;
        floats[7] = state.LodBias; // the texture level bias (TEXLEVEL), with the mode in ints[19]
        ints[8] = state.AlphaFunction;
        ints[9] = state.AlphaReference;
        ints[10] = state.AlphaMask;
        ints[11] = 0;
        floats[12] = (state.FogColor & 0xFF) / 255f;
        floats[13] = ((state.FogColor >> 8) & 0xFF) / 255f;
        floats[14] = ((state.FogColor >> 16) & 0xFF) / 255f;
        floats[15] = 1;
        ints[16] = state.ColorTestFunction;
        ints[17] = (int)state.ColorReference;
        ints[18] = (int)state.ColorTestMask;
        ints[19] = state.LodMode;
        for (int row = 0; row < 4; row++)
        {
            ints[20 + row] = state.DitherRows[row];
        }
    }

    private DescriptorSetLayout CreateSetLayout()
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
        return layout;
    }

    private DescriptorPool CreateDescriptorPool()
    {
        DescriptorPoolSize size = new(DescriptorType.CombinedImageSampler, DescriptorSetsPerBatch);
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = DescriptorSetsPerBatch,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };
        _vk.CreateDescriptorPool(_device, in poolInfo, null, out DescriptorPool pool).Ok("GE descriptor pool");
        return pool;
    }

    private void EnsureVertexBuffer(ulong size)
    {
        if (size <= _vertexSize)
        {
            return;
        }

        // a larger ring: the batch so far is submitted first (its draws read the old one)
        SubmitAndWait(readBack: false, null);
        if (_vertexBuffer.Handle != 0)
        {
            _core.DestroyBuffer(_vertexBuffer, _vertexMemory);
        }

        ulong rounded = Math.Max(size, Math.Max(_vertexSize * 2, 1UL << 20));
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

        foreach (Framebuffer framebuffer in _framebuffers.Values)
        {
            _vk.DestroyFramebuffer(_device, framebuffer, null);
        }

        _framebuffers.Clear();
        foreach (Surface surface in _surfaces.Values.Concat(_scratchDepth.Values))
        {
            DestroySurface(surface);
        }

        foreach (CachedTexture texture in _textures.Values.Concat(_retired).Append(_dummyTexture))
        {
            CachedTexture owned = texture;
            DestroyImage(ref owned.Image, ref owned.Memory, ref owned.View);
        }

        foreach (Sampler sampler in _samplers)
        {
            if (sampler.Handle != 0)
            {
                _vk.DestroySampler(_device, sampler, null);
            }
        }

        _core.DestroyBuffer(_uploadRing, _uploadRingMemory);
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
        if (state.LogicOp && !_core.LogicOp)
        {
            Log.WarnOnce("ge-logic-op", "GE: the device has no logic ops: drawn without the logic op");
        }

        int logicOp = state.LogicOp && _core.LogicOp ? state.LogicOpCode : -1;
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
            | ((ulong)mask << 22)
            | ((ulong)(logicOp + 1) << 26);
        if (_pipelines.TryGetValue(key, out Pipeline existing))
        {
            return existing;
        }

        Pipeline created = CreatePipeline(topology, blend, source, destination, operation, depthTest, compare, depthWrite, mask, logicOp);
        _pipelines[key] = created;
        return created;
    }

    private Pipeline CreatePipeline(GeTopology topology, bool blend, BlendFactor source, BlendFactor destination, BlendOp operation,
        bool depthTest, CompareOp compare, bool depthWrite, ColorComponentFlags mask, int logicOp)
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
                LogicOpEnable = logicOp >= 0,
                LogicOp = logicOp >= 0 ? (LogicOp)logicOp : LogicOp.Copy,
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
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
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
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
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
            DstAccessMask = AccessFlags.TransferReadBit | AccessFlags.ColorAttachmentReadBit | AccessFlags.DepthStencilAttachmentReadBit,
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

    /// <summary>A sampler's index: bit 0 linear minification, 1 linear magnification, 2..3 the mipmap filter (0 none,
    /// 1 nearest level, 2 linear between levels), 4 clamp on U, 5 clamp on V.</summary>
    private static int SamplerIndex(GeRenderState state)
    {
        int index = state.MinLinear ? 1 : 0;
        index |= state.MagLinear ? 2 : 0;
        index |= (state.TextureLevels > 1 ? state.MipFilter : 0) << 2;
        index |= state.ClampU ? 16 : 0;
        index |= state.ClampV ? 32 : 0;
        return index;
    }

    private Sampler SamplerAt(int index)
    {
        if (_samplers[index].Handle == 0)
        {
            _samplers[index] = CreateSampler(index);
        }

        return _samplers[index];
    }

    private Sampler CreateSampler(int index)
    {
        int mipFilter = (index >> 2) & 3;
        SamplerCreateInfo info = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MinFilter = (index & 1) != 0 ? Filter.Linear : Filter.Nearest,
            MagFilter = (index & 2) != 0 ? Filter.Linear : Filter.Nearest,
            MipmapMode = mipFilter == 2 ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
            AddressModeU = (index & 16) != 0 ? SamplerAddressMode.ClampToEdge : SamplerAddressMode.Repeat,
            AddressModeV = (index & 32) != 0 ? SamplerAddressMode.ClampToEdge : SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = mipFilter == 0 ? 0 : 16,
        };
        _vk.CreateSampler(_device, in info, null, out Sampler sampler).Ok("GE sampler");
        return sampler;
    }


    private (Image, DeviceMemory, ImageView) CreateImage(int width, int height, Format format, ImageUsageFlags usage, ImageAspectFlags aspect, int levels = 1)
    {
        ImageCreateInfo info = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = (uint)levels,
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
            SubresourceRange = new ImageSubresourceRange(aspect, 0, (uint)levels, 0, 1),
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
}
