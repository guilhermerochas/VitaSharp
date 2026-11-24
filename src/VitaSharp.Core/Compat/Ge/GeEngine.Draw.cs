using VitaSharp.Core.Util;

namespace VitaSharp.Core.Compat.Ge;

/// <summary>
/// The GE's drawing: PRIM's vertices decoded by VTYPE, transformed (world, view, projection, viewport, the screen
/// offset) or taken as framebuffer coordinates ("through" mode), the primitive expanded to points, lines or
/// triangles (strips, fans and rectangles become lists; flat shading takes the last vertex's colour), the state read
/// from the command registers, the texture and CLUT decoded (once while their memory stays the same), and the batch
/// handed to the rasterizer with its framebuffer and depth surfaces, which it may keep until the list stops. LOADCLUT
/// and the block transfer (TRANSFERSTART) work on memory
/// here. Not drawn yet: lighting, skinning, the stencil test, patches, texture
/// mipmaps (level 0 only); each logs once when a list uses it.
/// </summary>
public sealed partial class GeEngine
{
    /// <summary>The VRAM base (physical): framebuffer and depth pointers are offsets in it.</summary>
    public const uint VramBase = 0x04000000;

    private readonly uint[] _clut = new uint[256 * 4 / 4]; // 1 KB of CLUT entries (as 32-bit words)
    private readonly GeRenderState _state = new();
    private GeVertex[] _vertices = new GeVertex[256];
    private GeVertex[] _assembled = new GeVertex[768];

    /// <summary>The rasterizer the batches go to (none: the batches are decoded and dropped). Unset, the first batch
    /// takes one from <see cref="RasterizerFactory"/>.</summary>
    public IGeRasterizer? Rasterizer { get; set; }

    /// <summary>
    /// Makes the rasterizer of a GE that has none, at its first batch (so a core that never draws costs nothing): the
    /// front-end sets <see cref="DefaultRasterizerFactory"/> once (the Vulkan renderer: GeVulkanRasterizerDevice.Create). Null
    /// or a null result: no rasterizer, the batches are decoded and dropped.
    /// </summary>
    public Func<IGeRasterizer?>? RasterizerFactory { get; set; } = DefaultRasterizerFactory;

    /// <summary>The <see cref="RasterizerFactory"/> every new GE starts with (set by the front-end at start-up).</summary>
    public static Func<IGeRasterizer?>? DefaultRasterizerFactory { get; set; }

    private bool _rasterizerAsked;
    private bool _rasterizerOwned;     // made by the factory: the GE ends it

    /// <summary>Ends the rasterizer the GE made (its Vulkan device): the core stopped or its process ended. A later
    /// batch makes a new one. A rasterizer set from outside is its setter's.</summary>
    public void ReleaseRasterizer()
    {
        IGeRasterizer? rasterizer = Rasterizer;
        if (_rasterizerOwned)
        {
            Rasterizer = null;
            _rasterizerOwned = false;
            (rasterizer as IDisposable)?.Dispose();
        }

        _rasterizerAsked = false;
    }

    public void Dispose()
    {
        ReleaseRasterizer();
    }

    /// <summary>The batches drawn so far.</summary>
    public long Batches { get; private set; }

    partial void OnStateCommand(int command, uint argument)
    {
        switch (command)
        {
            case GeCommand.LoadClut:
                LoadClut(argument);
                break;
            case GeCommand.TransferStart:
                BlockTransfer(argument);
                break;
        }
    }

    partial void DrawBatch(int primitive, int count, GeVertexFormat format)
    {
        if (count == 0)
        {
            return;
        }

        if (Enabled(GeCommand.LightingEnable) && !format.Through)
        {
            Log.WarnOnce("ge-lighting", "GE: lighting is not applied yet");
        }

        if (format.WeightCount > 0 && !format.Through)
        {
            Log.WarnOnce("ge-skinning", "GE: skinning is not applied yet");
        }

        DecodeVertices(count, format);
        (GeTopology topology, int assembled) = Assemble(primitive, count, format);
        if (assembled == 0)
        {
            return;
        }

        if (Rasterizer == null && !_rasterizerAsked)
        {
            _rasterizerAsked = true;
            Rasterizer = RasterizerFactory?.Invoke();
            _rasterizerOwned = Rasterizer != null;
        }

        ReadState(format);
        Rasterizer?.Draw(topology, _assembled.AsSpan(0, assembled), _state, ColorSurface(), DepthSurface(), _memory);
        Batches++;
    }

    // ---- vertices

    private void DecodeVertices(int count, GeVertexFormat format)
    {
        if (_vertices.Length < count)
        {
            _vertices = new GeVertex[Math.Max(count, _vertices.Length * 2)];
        }

        Span<float> morphWeights = stackalloc float[8];
        for (int m = 0; m < format.MorphCount; m++)
        {
            morphWeights[m] = format.MorphCount == 1 ? 1 : Fixed24(Command(GeCommand.MorphWeight0 + m));
        }

        for (int i = 0; i < count; i++)
        {
            int index = format.IndexSize switch
            {
                1 => ReadByte(_indexAddress + (uint)i),
                2 => ReadHalf(_indexAddress + (uint)(i * 2)),
                4 => (int)_memory.Read32(_indexAddress + (uint)(i * 4)),
                _ => i,
            };
            uint vertex = format.IndexSize != 0 ? _vertexAddress + (uint)(index * format.Size) : _vertexAddress + (uint)(i * format.Size);
            _vertices[i] = DecodeVertex(vertex, format, morphWeights);
        }
    }

    private GeVertex DecodeVertex(uint address, GeVertexFormat format, ReadOnlySpan<float> morphWeights)
    {
        float x = 0;
        float y = 0;
        float z = 0;
        float u = 0;
        float v = 0;
        for (int m = 0; m < format.MorphCount; m++)
        {
            uint target = address + (uint)(m * format.Stride);
            float weight = morphWeights[m];
            (float px, float py, float pz) = ReadComponent3(target + (uint)format.PositionOffset, format.PositionFormat, format.Through);
            x += px * weight;
            y += py * weight;
            z += pz * weight;
            if (format.TextureOffset >= 0)
            {
                (float tu, float tv) = ReadTexture(target + (uint)format.TextureOffset, format.TextureFormat, format.Through);
                u += tu * weight;
                v += tv * weight;
            }
        }

        uint color = format.ColorOffset >= 0 ? ReadColor(address + (uint)format.ColorOffset, format.ColorFormat) : MaterialColor();
        GeVertex result = new() { Color = color };
        if (format.Through)
        {
            result.X = x;
            result.Y = y;
            result.Z = Math.Clamp(z / 65535f, 0, 1);
            result.W = 1;
            int width = Math.Max(1, TextureWidth());
            int height = Math.Max(1, TextureHeight());
            result.U = u / width;
            result.V = v / height;
            return result;
        }

        Transform(x, y, z, ref result);
        result.U = u * Fixed24(Command(GeCommand.TexScaleU)) + Fixed24(Command(GeCommand.TexOffsetU));
        result.V = v * Fixed24(Command(GeCommand.TexScaleV)) + Fixed24(Command(GeCommand.TexOffsetV));
        return result;
    }

    /// <summary>World, view and projection, then the viewport and the screen offset (OFFSETX/Y in 1/16 pixel).</summary>
    private void Transform(float x, float y, float z, ref GeVertex result)
    {
        ReadOnlySpan<float> world = _world;
        ReadOnlySpan<float> view = _view;
        ReadOnlySpan<float> projection = _projection;
        float wx = x * world[0] + y * world[3] + z * world[6] + world[9];
        float wy = x * world[1] + y * world[4] + z * world[7] + world[10];
        float wz = x * world[2] + y * world[5] + z * world[8] + world[11];
        float vx = wx * view[0] + wy * view[3] + wz * view[6] + view[9];
        float vy = wx * view[1] + wy * view[4] + wz * view[7] + view[10];
        float vz = wx * view[2] + wy * view[5] + wz * view[8] + view[11];
        float cx = vx * projection[0] + vy * projection[4] + vz * projection[8] + projection[12];
        float cy = vx * projection[1] + vy * projection[5] + vz * projection[9] + projection[13];
        float cz = vx * projection[2] + vy * projection[6] + vz * projection[10] + projection[14];
        float cw = vx * projection[3] + vy * projection[7] + vz * projection[11] + projection[15];
        if (cw == 0)
        {
            cw = 1e-6f;
        }

        float offsetX = (Command(GeCommand.OffsetX) & 0xFFFF) / 16f;
        float offsetY = (Command(GeCommand.OffsetY) & 0xFFFF) / 16f;
        result.X = Fixed24(Command(GeCommand.ViewportXScale)) * (cx / cw) + Fixed24(Command(GeCommand.ViewportXCenter)) - offsetX;
        result.Y = Fixed24(Command(GeCommand.ViewportYScale)) * (cy / cw) + Fixed24(Command(GeCommand.ViewportYCenter)) - offsetY;
        float depth = Fixed24(Command(GeCommand.ViewportZScale)) * (cz / cw) + Fixed24(Command(GeCommand.ViewportZCenter));
        result.Z = Math.Clamp(depth / 65535f, 0, 1);
        result.W = cw;
    }

    private (float, float, float) ReadComponent3(uint address, int componentFormat, bool through)
    {
        switch (componentFormat)
        {
            case 1:
            {
                float scale = through ? 1 : 1 / 128f;
                return ((sbyte)ReadByte(address) * scale, (sbyte)ReadByte(address + 1) * scale, (sbyte)ReadByte(address + 2) * scale);
            }

            case 2:
            {
                float scale = through ? 1 : 1 / 32768f;
                float zValue = through ? (ushort)ReadHalf(address + 4) : (short)ReadHalf(address + 4) * scale;
                return ((short)ReadHalf(address) * scale, (short)ReadHalf(address + 2) * scale, zValue);
            }

            case 3:
                return (ReadFloat(address), ReadFloat(address + 4), ReadFloat(address + 8));
            default:
                return (0, 0, 0);
        }
    }

    private (float, float) ReadTexture(uint address, int componentFormat, bool through)
    {
        switch (componentFormat)
        {
            case 1:
            {
                float scale = through ? 1 : 1 / 128f;
                return (ReadByte(address) * scale, ReadByte(address + 1) * scale);
            }

            case 2:
            {
                float scale = through ? 1 : 1 / 32768f;
                return ((ushort)ReadHalf(address) * scale, (ushort)ReadHalf(address + 2) * scale);
            }

            case 3:
                return (ReadFloat(address), ReadFloat(address + 4));
            default:
                return (0, 0);
        }
    }

    private uint ReadColor(uint address, int colorFormat)
    {
        switch (colorFormat)
        {
            case 4:
                return GePixels.From565((ushort)ReadHalf(address));
            case 5:
                return GePixels.From5551((ushort)ReadHalf(address));
            case 6:
                return GePixels.From4444((ushort)ReadHalf(address));
            case 7:
                return _memory.Read32(address);
            default:
                return MaterialColor();
        }
    }

    /// <summary>The colour of vertices without one: the material's ambient colour and alpha.</summary>
    private uint MaterialColor()
    {
        return (Command(GeCommand.MaterialAmbient) & 0xFFFFFF) | ((Command(GeCommand.MaterialAlpha) & 0xFF) << 24);
    }

    // ---- primitives

    private (GeTopology, int) Assemble(int primitive, int count, GeVertexFormat format)
    {
        bool flat = (Command(GeCommand.ShadeMode) & 1) == 0;
        EnsureAssembled(count * 3 + 6);
        ReadOnlySpan<GeVertex> input = _vertices.AsSpan(0, count);
        int written = 0;
        switch (primitive)
        {
            case GeCommand.PrimitivePoints:
                input.CopyTo(_assembled);
                return (GeTopology.Points, count);
            case GeCommand.PrimitiveLines:
                for (int i = 0; i + 1 < count; i += 2)
                {
                    AddLine(input[i], input[i + 1], flat, ref written);
                }

                return (GeTopology.Lines, written);
            case GeCommand.PrimitiveLineStrip:
                for (int i = 0; i + 1 < count; i++)
                {
                    AddLine(input[i], input[i + 1], flat, ref written);
                }

                return (GeTopology.Lines, written);
            case GeCommand.PrimitiveTriangles:
                for (int i = 0; i + 2 < count; i += 3)
                {
                    AddTriangle(input[i], input[i + 1], input[i + 2], flat, format.Through, ref written);
                }

                return (GeTopology.Triangles, written);
            case GeCommand.PrimitiveTriangleStrip:
                for (int i = 0; i + 2 < count; i++)
                {
                    if ((i & 1) == 0)
                    {
                        AddTriangle(input[i], input[i + 1], input[i + 2], flat, format.Through, ref written);
                    }
                    else
                    {
                        AddTriangle(input[i + 1], input[i], input[i + 2], flat, format.Through, ref written);
                    }
                }

                return (GeTopology.Triangles, written);
            case GeCommand.PrimitiveTriangleFan:
                for (int i = 1; i + 1 < count; i++)
                {
                    AddTriangle(input[0], input[i], input[i + 1], flat, format.Through, ref written);
                }

                return (GeTopology.Triangles, written);
            case GeCommand.PrimitiveRectangles:
                for (int i = 0; i + 1 < count; i += 2)
                {
                    AddRectangle(input[i], input[i + 1], ref written);
                }

                return (GeTopology.Triangles, written);
            default:
                return (GeTopology.Points, 0);
        }
    }

    private void EnsureAssembled(int needed)
    {
        if (_assembled.Length < needed)
        {
            _assembled = new GeVertex[Math.Max(needed, _assembled.Length * 2)];
        }
    }

    private void AddLine(GeVertex first, GeVertex second, bool flat, ref int written)
    {
        if (flat)
        {
            first.Color = second.Color;
        }

        _assembled[written++] = first;
        _assembled[written++] = second;
    }

    /// <summary>A triangle, unless culled: with CULLFACEENABLE (transformed vertices only), CULL bit 0 is the front
    /// face that is kept (0 clockwise, 1 counter-clockwise, as seen on the screen).</summary>
    private void AddTriangle(GeVertex first, GeVertex second, GeVertex third, bool flat, bool through, ref int written)
    {
        if (!through && (Command(GeCommand.CullFaceEnable) & 1) != 0)
        {
            float area = (second.X - first.X) * (third.Y - first.Y) - (third.X - first.X) * (second.Y - first.Y);
            bool counterClockwise = area < 0; // y grows downwards on screen
            bool keepCounterClockwise = (Command(GeCommand.Cull) & 1) != 0;
            if (area == 0 || counterClockwise != keepCounterClockwise)
            {
                return;
            }
        }

        if (flat)
        {
            first.Color = third.Color;
            second.Color = third.Color;
        }

        _assembled[written++] = first;
        _assembled[written++] = second;
        _assembled[written++] = third;
    }

    /// <summary>RECTANGLES: two corners make an axis-aligned rectangle with the second corner's colour and depth; the
    /// texture coordinates run from the first corner's to the second's.</summary>
    private void AddRectangle(GeVertex first, GeVertex second, ref int written)
    {
        GeVertex topLeft = second;
        topLeft.X = first.X;
        topLeft.Y = first.Y;
        topLeft.U = first.U;
        topLeft.V = first.V;
        GeVertex topRight = second;
        topRight.Y = first.Y;
        topRight.V = first.V;
        GeVertex bottomLeft = second;
        bottomLeft.X = first.X;
        bottomLeft.U = first.U;
        _assembled[written++] = topLeft;
        _assembled[written++] = topRight;
        _assembled[written++] = bottomLeft;
        _assembled[written++] = topRight;
        _assembled[written++] = second;
        _assembled[written++] = bottomLeft;
    }

    // ---- state

    private void ReadState(GeVertexFormat format)
    {
        GeRenderState state = _state;
        uint clear = Command(GeCommand.ClearMode);
        state.ClearMode = (clear & 1) != 0;
        state.ClearColor = (clear & 0x100) != 0;
        state.ClearAlpha = (clear & 0x200) != 0;
        state.ClearDepth = (clear & 0x400) != 0;
        uint scissor1 = Command(GeCommand.Scissor1);
        uint scissor2 = Command(GeCommand.Scissor2);
        state.ScissorLeft = (int)(scissor1 & 0x3FF);
        state.ScissorTop = (int)((scissor1 >> 10) & 0x3FF);
        state.ScissorRight = (int)(scissor2 & 0x3FF);
        state.ScissorBottom = (int)((scissor2 >> 10) & 0x3FF);
        state.DepthTest = Enabled(GeCommand.DepthTestEnable);
        state.DepthFunction = (int)(Command(GeCommand.DepthTest) & 7);
        state.DepthWrite = state.DepthTest && (Command(GeCommand.DepthWriteDisable) & 1) == 0; // no depth test: no depth writes either
        state.AlphaTest = Enabled(GeCommand.AlphaTestEnable);
        uint alpha = Command(GeCommand.AlphaTest);
        state.AlphaFunction = (int)(alpha & 7);
        state.AlphaReference = (int)((alpha >> 8) & 0xFF);
        state.AlphaMask = (int)((alpha >> 16) & 0xFF);
        state.Blend = Enabled(GeCommand.AlphaBlendEnable);
        uint blend = Command(GeCommand.BlendMode);
        state.BlendSource = (int)(blend & 0xF);
        state.BlendDestination = (int)((blend >> 4) & 0xF);
        state.BlendEquation = (int)((blend >> 8) & 0xF);
        state.BlendFixedA = Command(GeCommand.BlendFixedA) & 0xFFFFFF;
        state.BlendFixedB = Command(GeCommand.BlendFixedB) & 0xFFFFFF;
        state.WriteMaskRgb = Command(GeCommand.MaskRgb) & 0xFFFFFF;
        state.WriteMaskAlpha = Command(GeCommand.MaskAlpha) & 0xFF;
        uint function = Command(GeCommand.TextureFunction);
        state.TextureFunction = (int)(function & 7);
        state.TextureAlpha = (function & 0x100) != 0;
        state.ColorDouble = (function & 0x10000) != 0;
        state.TextureEnvironment = Command(GeCommand.TextureEnvironmentColor) & 0xFFFFFF;
        uint filter = Command(GeCommand.TextureFilter);
        state.TextureLinear = (filter & 1) != 0 || ((filter >> 8) & 1) != 0;
        int minFilter = (int)(filter & 7);
        state.MinLinear = (minFilter & 1) != 0;
        state.MagLinear = ((filter >> 8) & 1) != 0;
        state.MipFilter = minFilter >= 6 ? 2 : minFilter >= 4 ? 1 : 0;
        uint level = Command(GeCommand.TextureLevel);
        state.LodMode = (int)(level & 3);
        state.LodBias = (sbyte)((level >> 16) & 0xFF) / 16f;
        uint wrap = Command(GeCommand.TextureWrap);
        state.ClampU = (wrap & 1) != 0;
        state.ClampV = (wrap & 0x100) != 0;
        state.Texture = Enabled(GeCommand.TextureMapEnable) && !state.ClearMode && format.TextureOffset >= 0 && DecodeTexture();
        if (Enabled(GeCommand.StencilTestEnable) && !state.ClearMode)
        {
            Log.WarnOnce("ge-stencil", "GE: the stencil test is not applied yet");
        }

        state.Fog = Enabled(GeCommand.FogEnable) && !format.Through && !state.ClearMode;
        state.FogColor = Command(GeCommand.FogColor) & 0xFFFFFF;
        state.ColorTest = Enabled(GeCommand.ColorTestEnable) && !state.ClearMode;
        state.ColorTestFunction = (int)(Command(GeCommand.ColorTest) & 3);
        state.ColorReference = Command(GeCommand.ColorReference) & 0xFFFFFF;
        state.ColorTestMask = Command(GeCommand.ColorTestMask) & 0xFFFFFF;
        state.Dither = Enabled(GeCommand.DitherEnable) && !state.ClearMode && (Command(GeCommand.FrameBufferPixelFormat) & 3) != GePixels.Format8888;
        for (int row = 0; row < 4; row++)
        {
            state.DitherRows[row] = (int)(Command(GeCommand.Dither0 + row) & 0xFFFF);
        }

        state.LogicOp = Enabled(GeCommand.LogicOpEnable) && !state.ClearMode;
        state.LogicOpCode = (int)(Command(GeCommand.LogicOp) & 0xF);
    }

    private bool Enabled(int command)
    {
        return (Command(command) & 1) != 0;
    }

    private int TextureWidth()
    {
        return 1 << (int)(Command(GeCommand.TextureSize0) & 0xF);
    }

    private int TextureHeight()
    {
        return 1 << (int)((Command(GeCommand.TextureSize0) >> 8) & 0xF);
    }

    // ---- framebuffer

    /// <summary>The framebuffer (FBP's low 24 bits in VRAM, FBW, FRAMEBUFPIXFORMAT: 0 565, 1 5551, 2 4444, 3 8888) as far
    /// down as the scissor reaches.</summary>
    private GeSurface ColorSurface()
    {
        uint address = VramBase + (Command(GeCommand.FrameBufferPointer) & 0x1FFFF0);
        int stride = Math.Max((int)(Command(GeCommand.FrameBufferWidth) & 0x7FC), 4);
        int height = Math.Clamp(_state.ScissorBottom + 1, 1, 1024);
        return new GeSurface(address, stride, height, (int)(Command(GeCommand.FrameBufferPixelFormat) & 3));
    }

    /// <summary>The depth buffer (ZBP, ZBW), when the draw tests or writes depth.</summary>
    private GeSurface? DepthSurface()
    {
        bool usesDepth = _state.ClearMode ? _state.ClearDepth : _state.DepthTest || _state.DepthWrite;
        if (!usesDepth)
        {
            return null;
        }

        uint address = VramBase + (Command(GeCommand.DepthBufferPointer) & 0x1FFFF0);
        int stride = Math.Max((int)(Command(GeCommand.DepthBufferWidth) & 0x7FC), 4);
        int height = Math.Clamp(_state.ScissorBottom + 1, 1, 1024);
        return new GeSurface(address, stride, height, GeSurface.DepthFormat);
    }

    /// <summary>Memory the GE's surfaces may hold changes for is about to be read outside the GE (the LCD DMA reading
    /// the picture, the CPU): the rasterizer writes them back first. On the thread that runs the GE (the core's).</summary>
    public void SynchronizeMemory(uint address, int length)
    {
        IGeRasterizer? rasterizer = Rasterizer;
        if (rasterizer != null && rasterizer.Holds(address, length))
        {
            rasterizer.Flush(_memory);
        }
    }

    /// <summary>The list stopped (END, the stall address): the rasterizer finishes and memory has its pixels.</summary>
    partial void OnExecutionEnd()
    {
        Rasterizer?.Flush(_memory);
    }

    partial void OnExecutionStart()
    {
        _executionEpoch++;
    }

    // ---- textures

    /// <summary>A texture decoded once: the memory it came from, checked again once per list run.</summary>
    private sealed class CachedTexture
    {
        public uint[] Pixels = [];
        public ulong Hash;
        public long Id;
        public long CheckedEpoch = -1;
    }

    private const int TextureCacheLimit = 256;

    private readonly Dictionary<(uint Address, int Format, int Width, int Height, int BufferWidth, bool Swizzled, ulong Clut), CachedTexture> _textures = [];
    private long _executionEpoch;
    private long _nextTextureId = 1;

    /// <summary>The texture (TPSM, TMODE swizzle bit; level n at TEXADDRn/TBWn/TSIZEn) decoded to RGBA8 into the state:
    /// level 0, and with a mipmap filter the levels TMODE's bits 16..18 name, as long as each is half the one before;
    /// false when it cannot be read. A texture is decoded once and reused while its memory (checked once per list
    /// run) and its CLUT stay the same; one the rasterizer drew into is written back first.</summary>
    private bool DecodeTexture()
    {
        int width = TextureWidth();
        int height = TextureHeight();
        int format = (int)(Command(GeCommand.TextureFormat) & 0xF);
        bool swizzled = (Command(GeCommand.TextureMode) & 1) != 0;
        if (format > GeTextureDecoder.FormatDxt5)
        {
            Log.WarnOnce("ge-texture-format", $"GE: texture format {format} does not exist");
            return false;
        }

        int levels = _state.MipFilter != 0 ? 1 + Math.Min((int)((Command(GeCommand.TextureMode) >> 16) & 7), 7) : 1;
        Span<uint> addresses = stackalloc uint[8];
        Span<int> bufferWidths = stackalloc int[8];
        Span<int> sizes = stackalloc int[8];
        int totalPixels = 0;
        ulong hash = format is >= 4 and <= 7 ? ClutHash() : 0;
        bool changed = false;
        int usable = 0;
        for (int level = 0; level < levels; level++)
        {
            uint sizeWord = Command(GeCommand.TextureSize0 + level);
            int levelWidth = 1 << (int)(sizeWord & 0xF);
            int levelHeight = 1 << (int)((sizeWord >> 8) & 0xF);
            if (level > 0 && (levelWidth != Math.Max(width >> level, 1) || levelHeight != Math.Max(height >> level, 1)))
            {
                break;
            }

            addresses[level] = (Command(GeCommand.TextureAddress0 + level) & 0xFFFFF0) | ((Command(GeCommand.TextureBufferWidth0 + level) & 0x0F0000) << 8);
            bufferWidths[level] = Math.Max((int)(Command(GeCommand.TextureBufferWidth0 + level) & 0x7FF), 1);
            sizes[level] = GeTextureDecoder.SourceBytes(bufferWidths[level], levelHeight, format, swizzled);
            totalPixels += levelWidth * levelHeight;
            usable++;
        }

        (uint Address, int Format, int Width, int Height, int BufferWidth, bool Swizzled, ulong Clut) key = (addresses[0], format | (usable << 8), width, height, bufferWidths[0], swizzled, hash);
        if (!_textures.TryGetValue(key, out CachedTexture? cached))
        {
            if (_textures.Count >= TextureCacheLimit)
            {
                _textures.Clear();
            }

            cached = new CachedTexture { Pixels = new uint[totalPixels] };
            _textures[key] = cached;
        }

        if (cached.CheckedEpoch != _executionEpoch)
        {
            ulong contentHash = 0;
            for (int level = 0; level < usable; level++)
            {
                SynchronizeMemory(addresses[level], sizes[level]);
                Span<byte> source = _memory.Span(addresses[level], sizes[level]);
                if (source.Length != sizes[level])
                {
                    return false;
                }

                contentHash = contentHash * 31 + System.IO.Hashing.XxHash3.HashToUInt64(source);
            }

            changed = contentHash != cached.Hash || cached.Id == 0;
            if (changed)
            {
                int offset = 0;
                for (int level = 0; level < usable; level++)
                {
                    int levelWidth = Math.Max(width >> level, 1);
                    int levelHeight = Math.Max(height >> level, 1);
                    Span<uint> target = cached.Pixels.AsSpan(offset, levelWidth * levelHeight);
                    if (!GeTextureDecoder.Decode(_memory, addresses[level], bufferWidths[level], levelWidth, levelHeight, format, swizzled, _clut, Command(GeCommand.ClutFormat), target))
                    {
                        return false;
                    }

                    offset += levelWidth * levelHeight;
                }

                cached.Hash = contentHash;
                cached.Id = _nextTextureId++;
            }

            cached.CheckedEpoch = _executionEpoch;
        }

        _state.TexturePixels = cached.Pixels;
        _state.TextureWidth = width;
        _state.TextureHeight = height;
        _state.TextureLevels = usable;
        _state.TextureId = cached.Id;
        return true;
    }

    /// <summary>The loaded CLUT and CLUTFORMAT, for the key of an indexed texture.</summary>
    private ulong ClutHash()
    {
        ulong hash = System.IO.Hashing.XxHash3.HashToUInt64(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_clut.AsSpan()));
        return hash ^ Command(GeCommand.ClutFormat);
    }

    /// <summary>LOADCLUT: (argument & 0x3F) blocks of 32 bytes from CBP/CBPH into the CLUT.</summary>
    private void LoadClut(uint argument)
    {
        uint address = (Command(GeCommand.ClutAddress) & 0xFFFFF0) | ((Command(GeCommand.ClutAddressUpper) & 0x0F0000) << 8);
        int words = Math.Min((int)(argument & 0x3F) * 8, _clut.Length);
        SynchronizeMemory(address, words * 4);
        for (int i = 0; i < words; i++)
        {
            _clut[i] = _memory.Read32(address + (uint)(i * 4));
        }
    }

    /// <summary>TRANSFERSTART (bit 0: 32-bit pixels, else 16-bit): a rectangle copied from the source buffer (TRXSBP,
    /// TRXSBW, TRXPOS) to the destination (TRXDBP, TRXDBW, TRXDPOS), TRXSIZE (width - 1, height - 1). Surfaces the
    /// rasterizer holds in either buffer are written back first; the destination's are read again after.</summary>
    private void BlockTransfer(uint argument)
    {
        int bytesPerPixel = (argument & 1) != 0 ? 4 : 2;
        uint source = (Command(GeCommand.TransferSource) & 0xFFFFF0) | ((Command(GeCommand.TransferSourceWidth) & 0x0F0000) << 8);
        uint destination = (Command(GeCommand.TransferDestination) & 0xFFFFF0) | ((Command(GeCommand.TransferDestinationWidth) & 0x0F0000) << 8);
        int sourceWidth = (int)(Command(GeCommand.TransferSourceWidth) & 0x7F8);
        int destinationWidth = (int)(Command(GeCommand.TransferDestinationWidth) & 0x7F8);
        uint sourcePosition = Command(GeCommand.TransferSourcePosition);
        uint destinationPosition = Command(GeCommand.TransferDestinationPosition);
        uint size = Command(GeCommand.TransferSize);
        int width = (int)(size & 0x3FF) + 1;
        int height = (int)((size >> 10) & 0x3FF) + 1;
        int sourceX = (int)(sourcePosition & 0x3FF);
        int sourceY = (int)((sourcePosition >> 10) & 0x3FF);
        int destinationX = (int)(destinationPosition & 0x3FF);
        int destinationY = (int)((destinationPosition >> 10) & 0x3FF);
        uint sourceStart = source + (uint)((sourceY * sourceWidth + sourceX) * bytesPerPixel);
        uint destinationStart = destination + (uint)((destinationY * destinationWidth + destinationX) * bytesPerPixel);
        int sourceSpan = ((height - 1) * sourceWidth + width) * bytesPerPixel;
        int destinationSpan = ((height - 1) * destinationWidth + width) * bytesPerPixel;
        SynchronizeMemory(sourceStart, sourceSpan);
        SynchronizeMemory(destinationStart, destinationSpan);
        for (int row = 0; row < height; row++)
        {
            uint from = source + (uint)(((sourceY + row) * sourceWidth + sourceX) * bytesPerPixel);
            uint to = destination + (uint)(((destinationY + row) * destinationWidth + destinationX) * bytesPerPixel);
            Span<byte> fromSpan = _memory.Span(from, width * bytesPerPixel);
            Span<byte> toSpan = _memory.Span(to, width * bytesPerPixel);
            if (fromSpan.Length == width * bytesPerPixel && toSpan.Length == width * bytesPerPixel)
            {
                fromSpan.CopyTo(toSpan);
            }
        }

        Rasterizer?.Invalidate(destinationStart, destinationSpan);
    }

    // ---- memory

    private int ReadByte(uint address)
    {
        return (int)((_memory.Read32(address & ~3u) >> (int)((address & 3) * 8)) & 0xFF);
    }

    private int ReadHalf(uint address)
    {
        return (int)((_memory.Read32(address & ~3u) >> (int)((address & 2) * 8)) & 0xFFFF);
    }

    private float ReadFloat(uint address)
    {
        return BitConverter.UInt32BitsToSingle(_memory.Read32(address));
    }

    /// <summary>A command's 24-bit float argument (the float's top 24 bits).</summary>
    private static float Fixed24(uint word)
    {
        return BitConverter.UInt32BitsToSingle((word & 0xFFFFFF) << 8);
    }
}
