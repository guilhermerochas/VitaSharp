using System.Numerics;

namespace VitaSharp.Core.Compat.Ge;

/// <summary>A vertex as read (or made by a patch), before the transform.</summary>
public struct GeDecodedVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public float U;
    public float V;
    public uint Color;
    public bool HasColor;
}

/// <summary>A vertex after the transform and before primitive assembly: the rasterizer's vertex and its clip coordinates.</summary>
public struct GeClipVertex
{
    public GeVertex Vertex;
    public float ClipX;
    public float ClipY;
    public float ClipZ;
    public float ClipW;

    /// <summary>The screen position fell outside the GE's 4096-pixel range (or the position is invalid): a primitive
    /// with this vertex is not drawn (unless the near-plane clip removes the vertex first).</summary>
    public bool OutsideRange;
}

/// <summary>
/// The GE's vertex stage: VTYPE decoding (positions, normals and texture coordinates in 8/16-bit fixed point or
/// float, the four colour formats, up to 8 skinning weights and 8 morph targets, 8/16/32-bit indices), then either
/// "through" screen coordinates or the transform: skinning with the bone matrices, world, view and projection, the
/// viewport and screen offset, fog, the texture coordinate generation (UV scale/offset, the texture matrix projection
/// of position, UV or normal, environment mapping from two lights) and the PSP's lighting model (four directional,
/// point or spot lights, ambient/diffuse/specular, attenuation, powered diffuse, separate specular, the material
/// colours or the vertex colour per MATERIALUPDATE). A format without UVs or normals keeps the last ones read, from
/// any draw. Behaviour after PPSSPP's software renderer and its hardware probes (GPU/Software/TransformUnit.cpp,
/// Lighting.cpp, Clipper.cpp, studied only), in float arithmetic where the GE uses its 24-bit floats.
/// </summary>
public sealed class GeVertexTransformer
{
    private readonly GeEngine _engine;
    private readonly IGeMemory _memory;
    private readonly float[] _morphWeights = new float[8];
    private readonly float[] _weights = new float[8];

    private float _carryU;
    private float _carryV;
    private Vector3 _carryNormal;

    // per draw
    private GeVertexFormat _format;
    private bool _lighting;
    private bool _fog;
    private int _uvMode;
    private int _projectionSource;
    private float _fogEnd;
    private float _fogSlope;
    private Vector3 _viewDirection;
    private readonly GeLight[] _lights = new GeLight[4];
    private readonly float[] _clipMatrix = new float[16];

    public GeVertexTransformer(GeEngine engine)
    {
        _engine = engine;
        _memory = engine.Memory;
    }

    private struct GeLight
    {
        public bool Enabled;
        public bool Directional;
        public bool Spot;
        public bool Specular;
        public bool PoweredDiffuse;
        public Vector3 Position;
        public Vector3 Direction;
        public Vector3 Attenuation;
        public float SpotCutoff;
        public float SpotExponent;
        public uint Ambient;
        public uint Diffuse;
        public uint SpecularColor;
    }

    private uint Command(int command)
    {
        return _engine.Command(command);
    }

    /// <summary>A command's 24-bit float argument (the float's top 24 bits).</summary>
    public static float Float24(uint word)
    {
        return BitConverter.UInt32BitsToSingle((word & 0xFFFFFF) << 8);
    }

    /// <summary>Decodes and transforms <paramref name="count"/> vertices of the batch at VADDR (through IADDR when indexed).</summary>
    public void Transform(int count, GeVertexFormat format, Span<GeClipVertex> output)
    {
        PrepareState(format);
        uint vertexAddress = _engine.VertexAddress;
        uint indexAddress = _engine.IndexAddress;
        for (int i = 0; i < count; i++)
        {
            int index = format.IndexSize switch
            {
                1 => (int)ReadByte(indexAddress + (uint)i),
                2 => (int)ReadHalf(indexAddress + (uint)(i * 2)),
                4 => (int)_memory.Read32(indexAddress + (uint)(i * 4)),
                _ => i,
            };
            output[i] = TransformVertex(vertexAddress + (uint)(index * format.Size));
        }
    }

    /// <summary>The per-draw state: the morph weights, the combined matrix, lights and texture generation.</summary>
    internal void PrepareState(GeVertexFormat format)
    {
        _format = format;
        for (int m = 0; m < format.MorphCount; m++)
        {
            _morphWeights[m] = format.MorphCount == 1 ? 1 : Float24(Command(GeCommand.MorphWeight0 + m));
        }

        GeFloat24.CombineMatrices(_engine.WorldMatrix, _engine.ViewMatrix, _engine.ProjectionMatrix, _clipMatrix);
        _lighting = (Command(GeCommand.LightingEnable) & 1) != 0 && !_format.Through;
        _fog = (Command(GeCommand.FogEnable) & 1) != 0 && !_format.Through;
        _fogEnd = Float24(Command(GeCommand.Fog1));
        _fogSlope = Float24(Command(GeCommand.Fog2));
        uint mapMode = Command(GeCommand.TextureMapMode);
        _uvMode = (int)(mapMode & 3);
        if (_uvMode == 3)
        {
            _uvMode = 0;
        }

        _projectionSource = (int)((mapMode >> 8) & 3);
        ReadOnlySpan<float> view = _engine.ViewMatrix;
        Vector3 direction = new(view[2], view[5], view[8]);
        _viewDirection = direction.LengthSquared() > 0 ? Vector3.Normalize(direction) : Vector3.UnitZ;
        for (int light = 0; light < 4; light++)
        {
            uint type = Command(GeCommand.LightType0 + light);
            GeLight state = new()
            {
                Enabled = (Command(GeCommand.LightEnable0 + light) & 1) != 0,
                Specular = (type & 3) == 1,
                PoweredDiffuse = (type & 3) == 2,
                Directional = ((type >> 8) & 3) == 0,
                Spot = ((type >> 8) & 3) >= 2,
                Position = LightVector(GeCommand.LightPosition0, light),
                Direction = LightVector(GeCommand.LightDirection0, light),
                Attenuation = LightVector(GeCommand.LightAttenuation0, light),
                SpotExponent = LightExponent(Float24(Command(GeCommand.LightSpotExponent0 + light))),
                SpotCutoff = Float24(Command(GeCommand.LightSpotCutoff0 + light)),
                Ambient = Command(GeCommand.LightColor0 + (light * 3)) & 0xFFFFFF,
                Diffuse = Command(GeCommand.LightColor0 + (light * 3) + 1) & 0xFFFFFF,
                SpecularColor = Command(GeCommand.LightColor0 + (light * 3) + 2) & 0xFFFFFF,
            };
            if (state.Directional && state.Position.LengthSquared() > 0)
            {
                state.Position = Vector3.Normalize(state.Position);
            }

            if (float.IsNaN(state.SpotCutoff) && float.IsNegative(state.SpotCutoff))
            {
                state.SpotCutoff = 0;
            }

            _lights[light] = state;
        }
    }

    private Vector3 LightVector(int firstCommand, int light)
    {
        int command = firstCommand + (light * 3);
        return new Vector3(Float24(Command(command)), Float24(Command(command + 1)), Float24(Command(command + 2)));
    }

    // ---- decoding

    private uint ReadByte(uint address)
    {
        return (_memory.Read32(address & ~3u) >> (int)((address & 3) * 8)) & 0xFF;
    }

    private uint ReadHalf(uint address)
    {
        return (_memory.Read32(address & ~3u) >> (int)((address & 2) * 8)) & 0xFFFF;
    }

    private float ReadFloat(uint address)
    {
        return BitConverter.UInt32BitsToSingle(_memory.Read32(address));
    }

    /// <summary>A signed position or normal component: s8 / 128, s16 / 32768, or a float.</summary>
    private float ReadSigned(uint address, int componentFormat, int component)
    {
        switch (componentFormat)
        {
            case 1:
                return (sbyte)ReadByte(address + (uint)component) / 128f;
            case 2:
                return (short)ReadHalf(address + (uint)(component * 2)) / 32768f;
            case 3:
                return ReadFloat(address + (uint)(component * 4));
            default:
                return 0;
        }
    }

    /// <summary>An unsigned texture coordinate or weight: u8 / 128, u16 / 32768, or a float.</summary>
    private float ReadUnsigned(uint address, int componentFormat, int component)
    {
        switch (componentFormat)
        {
            case 1:
                return ReadByte(address + (uint)component) / 128f;
            case 2:
                return ReadHalf(address + (uint)(component * 2)) / 32768f;
            case 3:
                return ReadFloat(address + (uint)(component * 4));
            default:
                return 0;
        }
    }

    /// <summary>A vertex colour as RGBA8 (R in the low byte); the 16-bit formats expand their channels.</summary>
    private uint ReadColor(uint address)
    {
        switch (_format.ColorFormat)
        {
            case 4:
                // no alpha bits: opaque
                return GePixels.From565((ushort)ReadHalf(address)) | 0xFF000000;
            case 5:
                return GePixels.From5551((ushort)ReadHalf(address));
            case 6:
                return GePixels.From4444((ushort)ReadHalf(address));
            default:
                return _memory.Read32(address);
        }
    }

    /// <summary>A morphed component: each target's value times its weight, summed in target order.</summary>
    private float Morph(uint vertex, int offset, int component, bool unsigned, int componentFormat)
    {
        float sum = 0;
        for (int m = 0; m < _format.MorphCount; m++)
        {
            uint target = vertex + (uint)(m * _format.Stride) + (uint)offset;
            float value = unsigned ? ReadUnsigned(target, componentFormat, component) : ReadSigned(target, componentFormat, component);
            sum += _format.MorphCount == 1 ? value : value * _morphWeights[m];
        }

        return sum;
    }

    private uint MorphColor(uint vertex)
    {
        if (_format.MorphCount == 1)
        {
            return ReadColor(vertex + (uint)_format.ColorOffset);
        }

        Span<float> channels = stackalloc float[4];
        channels.Clear();
        for (int m = 0; m < _format.MorphCount; m++)
        {
            uint color = ReadColor(vertex + (uint)(m * _format.Stride) + (uint)_format.ColorOffset);
            for (int c = 0; c < 4; c++)
            {
                channels[c] += ((color >> (c * 8)) & 0xFF) * _morphWeights[m];
            }
        }

        uint result = 0;
        for (int c = 0; c < 4; c++)
        {
            result |= (uint)Math.Clamp((int)MathF.Floor(channels[c]), 0, 255) << (c * 8);
        }

        return result;
    }

    private GeClipVertex TransformVertex(uint address)
    {
        return Finish(Decode(address));
    }

    /// <summary>
    /// One vertex as read: its position (morphed; skinned in transform mode), the UV and normal it has or the last
    /// ones read, its colour or the material ambient.
    /// </summary>
    internal GeDecodedVertex Decode(uint address)
    {
        GeVertexFormat format = _format;
        GeDecodedVertex result = default;
        if (format.Through)
        {
            result.Position = ThroughPosition(address);
        }
        else if (format.PositionInvalid)
        {
            result.Position = new Vector3(float.PositiveInfinity);
        }
        else
        {
            result.Position = new Vector3(
                Morph(address, format.PositionOffset, 0, false, format.PositionFormat),
                Morph(address, format.PositionOffset, 1, false, format.PositionFormat),
                Morph(address, format.PositionOffset, 2, false, format.PositionFormat));
        }

        if (format.TextureOffset >= 0)
        {
            _carryU = Morph(address, format.TextureOffset, 0, true, format.TextureFormat);
            _carryV = Morph(address, format.TextureOffset, 1, true, format.TextureFormat);
            if (format.Through && format.TextureFormat >= 2)
            {
                // through mode: 16-bit and float coordinates are texels (8-bit ones keep their / 128)
                _carryU = ThroughTexel(address, 0);
                _carryV = ThroughTexel(address, 1);
            }
        }

        if (format.NormalOffset >= 0)
        {
            _carryNormal = new Vector3(
                Morph(address, format.NormalOffset, 0, false, format.NormalFormat),
                Morph(address, format.NormalOffset, 1, false, format.NormalFormat),
                Morph(address, format.NormalOffset, 2, false, format.NormalFormat));
        }

        result.U = _carryU;
        result.V = _carryV;
        result.Normal = _carryNormal;
        result.HasColor = format.ColorOffset >= 0;
        result.Color = result.HasColor ? MorphColor(address) : MaterialAmbient();
        if (!format.Through && format.WeightCount > 0)
        {
            Skin(address, ref result.Position, ref result.Normal);
        }

        return result;
    }

    /// <summary>The position of a vertex without touching the carried UV and normal (the bounding box test).</summary>
    internal Vector3 DecodePosition(uint address)
    {
        float u = _carryU;
        float v = _carryV;
        Vector3 normal = _carryNormal;
        Vector3 position = Decode(address).Position;
        _carryU = u;
        _carryV = v;
        _carryNormal = normal;
        return position;
    }

    /// <summary>Only the clip coordinates of a model position (the bounding box test).</summary>
    internal GeClipVertex ClipPosition(Vector3 position)
    {
        GeClipVertex result = default;
        result.ClipX = GeFloat24.ClipComponent(position, _clipMatrix, 0);
        result.ClipY = GeFloat24.ClipComponent(position, _clipMatrix, 1);
        result.ClipZ = GeFloat24.ClipComponent(position, _clipMatrix, 2);
        result.ClipW = GeFloat24.ClipComponent(position, _clipMatrix, 3);
        return result;
    }

    /// <summary>The rest of the vertex stage, for a vertex read or made by a patch.</summary>
    internal GeClipVertex Finish(in GeDecodedVertex decoded)
    {
        GeClipVertex result = default;
        result.Vertex = new GeVertex { Color = decoded.Color };
        Vector3 position = decoded.Position;
        if (_format.Through)
        {
            result.Vertex.X = position.X;
            result.Vertex.Y = position.Y;
            result.Vertex.Z = position.Z / 65535f;
            result.Vertex.W = 1;
            result.Vertex.U = decoded.U / Math.Max(1, TextureWidth());
            result.Vertex.V = decoded.V / Math.Max(1, TextureHeight());
            result.ClipW = 1;
            result.OutsideRange = _format.PositionInvalid;
            return result;
        }

        Vector3 normal = decoded.Normal;
        if ((Command(GeCommand.ReverseNormal) & 1) != 0)
        {
            normal = -normal;
        }

        ReadOnlySpan<float> world = _engine.WorldMatrix;
        Vector3 worldPosition = Apply43(world, position, true);
        Vector3 viewPosition = Apply43(_engine.ViewMatrix, worldPosition, true);

        // the clip coordinates as the GE sums the combined matrix's rows (24-bit operands, see GeFloat24)
        result.ClipX = GeFloat24.ClipComponent(position, _clipMatrix, 0);
        result.ClipY = GeFloat24.ClipComponent(position, _clipMatrix, 1);
        result.ClipZ = GeFloat24.ClipComponent(position, _clipMatrix, 2);
        result.ClipW = GeFloat24.ClipComponent(position, _clipMatrix, 3);
        ToScreen(ref result);
        result.Vertex.Fog = _fog ? Math.Clamp((viewPosition.Z + _fogEnd) * _fogSlope, 0, 1) : 1;

        Vector3 worldNormal = Apply43(world, normal, false);
        GenerateTextureCoordinates(ref result.Vertex, position, normal, worldNormal, worldPosition, decoded.U, decoded.V);
        if (_lighting)
        {
            Light(ref result.Vertex, worldPosition, worldNormal, decoded.HasColor);
        }

        return result;
    }

    private Vector3 ThroughPosition(uint address)
    {
        uint at = address + (uint)_format.PositionOffset;
        switch (_format.PositionFormat)
        {
            case 2:
                return new Vector3((short)ReadHalf(at), (short)ReadHalf(at + 2), ReadHalf(at + 4));
            case 3:
            {
                // the depth is an integer: truncated and clamped to 16 bits (NaN gives 0)
                float z = ReadFloat(at + 8);
                float depth = z >= 65535f ? 65535f : (z > 0 ? MathF.Truncate(z) : 0);
                return new Vector3(ReadFloat(at), ReadFloat(at + 4), depth);
            }

            default:
                // 8-bit (and invalid) positions decode to 0 in through mode, depth included
                return Vector3.Zero;
        }
    }

    private float ThroughTexel(uint address, int component)
    {
        uint at = address + (uint)_format.TextureOffset;
        return _format.TextureFormat == 2 ? ReadHalf(at + (uint)(component * 2)) : ReadFloat(at + (uint)(component * 4));
    }

    /// <summary>A 4x3 matrix (column after column) times (v, 1) or, without the translation, times v.</summary>
    private static Vector3 Apply43(ReadOnlySpan<float> matrix, Vector3 v, bool translate)
    {
        float tx = translate ? matrix[9] : 0;
        float ty = translate ? matrix[10] : 0;
        float tz = translate ? matrix[11] : 0;
        return new Vector3(
            (v.X * matrix[0]) + (v.Y * matrix[3]) + (v.Z * matrix[6]) + tx,
            (v.X * matrix[1]) + (v.Y * matrix[4]) + (v.Z * matrix[7]) + ty,
            (v.X * matrix[2]) + (v.Y * matrix[5]) + (v.Z * matrix[8]) + tz);
    }

    /// <summary>Skinning: the weighted sum of the bone matrices applied to the position (and, without the translation, the normal); the weights morph too.</summary>
    private void Skin(uint address, ref Vector3 position, ref Vector3 normal)
    {
        int count = _format.WeightCount;
        for (int b = 0; b < count; b++)
        {
            _weights[b] = Morph(address, _format.WeightOffset, b, true, _format.WeightFormat);
        }

        ReadOnlySpan<float> bones = _engine.BoneMatrices;
        Vector3 skinnedPosition = Vector3.Zero;
        Vector3 skinnedNormal = Vector3.Zero;
        for (int b = 0; b < count; b++)
        {
            float weight = _weights[b];
            if (weight == 0)
            {
                continue;
            }

            ReadOnlySpan<float> bone = bones.Slice(b * 12, 12);
            skinnedPosition += Apply43(bone, position, true) * weight;
            skinnedNormal += Apply43(bone, normal, false) * weight;
        }

        position = skinnedPosition;
        normal = skinnedNormal;
    }

    /// <summary>
    /// The viewport (x / w * scale + center), the screen position in 1/16 pixels floored, minus OFFSETX/Y; the depth
    /// floored, clamped to 0..65535 with DEPTHCLAMP (else out of range). Outside 0..4096 the vertex is out of range,
    /// except, with DEPTHCLAMP, one the near plane removes anyway.
    /// </summary>
    private void ToScreen(ref GeClipVertex vertex)
    {
        float w = vertex.ClipW;
        float x = GeFloat24.Viewport(vertex.ClipX, w, Float24(Command(GeCommand.ViewportXScale)), Float24(Command(GeCommand.ViewportXCenter)));
        float y = GeFloat24.Viewport(vertex.ClipY, w, Float24(Command(GeCommand.ViewportYScale)), Float24(Command(GeCommand.ViewportYCenter)));
        float z = MathF.Floor(GeFloat24.Viewport(vertex.ClipZ, w, Float24(Command(GeCommand.ViewportZScale)), Float24(Command(GeCommand.ViewportZCenter))));
        const float ScreenBound = 4095f + (15.5f / 16f);
        bool depthClamp = (Command(GeCommand.DepthClampEnable) & 1) != 0;
        bool inside = x >= 0 && y >= 0 && x < ScreenBound && y < ScreenBound;
        if (depthClamp)
        {
            if (!inside && !(vertex.ClipZ < -vertex.ClipW))
            {
                vertex.OutsideRange = true;
            }

            z = Math.Clamp(z, 0, 65535);
        }
        else if (!(inside && z >= 0 && z < 65536))
        {
            vertex.OutsideRange = true;
        }

        if (_format.PositionInvalid || float.IsNaN(x) || float.IsNaN(y))
        {
            vertex.OutsideRange = true;
        }

        int screenX = (int)MathF.Floor(x * 16) - (int)(Command(GeCommand.OffsetX) & 0xFFFF);
        int screenY = (int)MathF.Floor(y * 16) - (int)(Command(GeCommand.OffsetY) & 0xFFFF);
        vertex.Vertex.X = screenX / 16f;
        vertex.Vertex.Y = screenY / 16f;
        vertex.Vertex.Z = Math.Clamp(z, 0, 65535) / 65535f;
        vertex.Vertex.W = w;
    }

    /// <summary>Re-projects a clip-space vertex made by the near-plane clip.</summary>
    internal void Reproject(ref GeClipVertex vertex)
    {
        vertex.OutsideRange = false;
        ToScreen(ref vertex);
    }

    private int TextureWidth()
    {
        return 1 << (int)(Command(GeCommand.TextureSize0) & 0xF);
    }

    private int TextureHeight()
    {
        return 1 << (int)((Command(GeCommand.TextureSize0) >> 8) & 0xF);
    }

    private uint MaterialAmbient()
    {
        return (Command(GeCommand.MaterialAmbient) & 0xFFFFFF) | ((Command(GeCommand.MaterialAlpha) & 0xFF) << 24);
    }

    /// <summary>
    /// TEXMAPMODE: 0 the UVs times TEXSCALE plus TEXOFFSET; 1 the texture matrix applied to the position, the UV,
    /// the normalised normal or the normal (bits 8-9), giving s, t and q; 2 environment mapping, s and t from the
    /// lights TEXSHADELS names.
    /// </summary>
    private void GenerateTextureCoordinates(ref GeVertex vertex, Vector3 position, Vector3 normal, Vector3 worldNormal, Vector3 worldPosition, float u, float v)
    {
        switch (_uvMode)
        {
            case 1:
            {
                Vector3 source;
                float sourceW = 1;
                switch (_projectionSource)
                {
                    case 0:
                        source = position;
                        break;
                    case 1:
                        source = new Vector3(u, v, 0);
                        break;
                    case 2:
                        source = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : normal;
                        break;
                    default:
                        source = normal;
                        break;
                }

                Vector3 stq = Apply43(_engine.TexGenMatrix, source, sourceW != 0);
                vertex.U = stq.X;
                vertex.V = stq.Y;
                vertex.Q = stq.Z;
                break;
            }

            case 2:
            {
                uint lights = Command(GeCommand.TextureShadeLights);
                vertex.U = ShadeMapCoordinate((int)(lights & 3), worldPosition, worldNormal);
                vertex.V = ShadeMapCoordinate((int)((lights >> 8) & 3), worldPosition, worldNormal);
                vertex.Q = 1;
                break;
            }

            default:
                vertex.U = (u * Float24(Command(GeCommand.TexScaleU))) + Float24(Command(GeCommand.TexOffsetU));
                vertex.V = (v * Float24(Command(GeCommand.TexScaleV))) + Float24(Command(GeCommand.TexOffsetV));
                vertex.Q = 1;
                break;
        }
    }

    /// <summary>The direction from the vertex to a light: the light's own for a directional one, normalised; (0, 0, 1) at the light.</summary>
    private Vector3 ToLight(in GeLight light, Vector3 worldPosition, out float distance)
    {
        if (light.Directional)
        {
            distance = 1;
            return light.Position;
        }

        Vector3 toLight = light.Position - worldPosition;
        distance = toLight.Length();
        return distance == 0 ? Vector3.UnitZ : toLight / distance;
    }

    private float NormalDot(Vector3 v, Vector3 worldNormal)
    {
        float length = worldNormal.Length();
        return length > 0 && float.IsFinite(length) ? Vector3.Dot(v, worldNormal) / length : 0;
    }

    private float ShadeMapCoordinate(int index, Vector3 worldPosition, Vector3 worldNormal)
    {
        GeLight light = _lights[index];
        Vector3 toLight = ToLight(light, worldPosition, out _);
        if (light.Specular)
        {
            Vector3 half = toLight + _viewDirection;
            toLight = half.LengthSquared() > 0 ? Vector3.Normalize(half) : half;
        }

        return (NormalDot(toLight, worldNormal) + 1) * 0.5f;
    }

    /// <summary>The lighting's colour factor: 2c + 1 per channel (the GE's half-offset rounding).</summary>
    private static int Factor(uint color, int channel)
    {
        return (int)(((color >> (channel * 8)) & 0xFF) * 2) + 1;
    }

    /// <summary>Scales an 8-bit light product by a factor in [0, 1]: s = floor(256 f), ((2x + 1)(2s + 1)) >> 10.</summary>
    private static int Scale(int value, float factor)
    {
        int s = Math.Clamp((int)(256f * factor), 0, 256);
        return ((value + value + 1) * ((2 * s) + 1)) >> 10;
    }

    /// <summary>The GE's power for the specular and spot exponents: an exponent at most 496, low bits dropped.</summary>
    public static float LightExponent(float exponent)
    {
        if (float.IsNaN(exponent))
        {
            return float.IsNegative(exponent) ? 0 : 496;
        }

        if (exponent >= 512)
        {
            return 496;
        }

        uint bits = BitConverter.SingleToUInt32Bits(exponent) & 0xFFF80000;
        float result = BitConverter.UInt32BitsToSingle(bits);
        return result <= 0 ? 0 : result;
    }

    /// <summary>
    /// v^e as the GE computes it (PPSSPP's GELightPow, measured): on the float's bits, the exponent times the log in
    /// units of 16 of the mantissa, truncated; a non-positive v is returned as it is, e &lt;= 0 gives 1.
    /// </summary>
    public static float LightPower(float value, float exponent)
    {
        if (exponent <= 0)
        {
            return 1;
        }

        if (!(value > 0))
        {
            return value;
        }

        int bits = BitConverter.SingleToInt32Bits(value);
        double t = (Math.Truncate((double)exponent * (bits - 0x3F800000) * (1.0 / 16.0)) * 16.0) + 1065353216.0;
        t = t >= 0 ? (t < 2139095039.0 ? t : 2139095039.0) : 0;
        return BitConverter.Int32BitsToSingle((int)t);
    }

    /// <summary>
    /// The PSP's lighting: emissive + material ambient x global ambient, then per enabled light its ambient, its
    /// diffuse (N.L, raised to the specular power when powered) and its specular (N.H to that power) times the
    /// material's (or, per MATERIALUPDATE, the vertex colour), each scaled by attenuation and the spot factor, in the
    /// GE's 8-bit colour arithmetic. The specular goes to Color1 with LIGHTMODE bit 0, else into the colour.
    /// </summary>
    private void Light(ref GeVertex vertex, Vector3 worldPosition, Vector3 worldNormal, bool hasColor)
    {
        uint vertexColor = vertex.Color;
        int update = hasColor ? (int)(Command(GeCommand.MaterialUpdate) & 7) : 0;
        uint materialAmbient = (update & 1) != 0 ? vertexColor : MaterialAmbient();
        uint materialDiffuse = (update & 2) != 0 ? vertexColor : Command(GeCommand.MaterialDiffuse) & 0xFFFFFF;
        uint materialSpecular = (update & 4) != 0 ? vertexColor : Command(GeCommand.MaterialSpecular) & 0xFFFFFF;
        uint emissive = Command(GeCommand.MaterialEmissive) & 0xFFFFFF;
        uint globalAmbient = (Command(GeCommand.AmbientColor) & 0xFFFFFF) | ((Command(GeCommand.AmbientAlpha) & 0xFF) << 24);
        float specularExponent = LightExponent(Float24(Command(GeCommand.MaterialSpecularCoefficient)));
        Span<int> color = stackalloc int[4];
        Span<int> specular = stackalloc int[4];
        for (int c = 0; c < 4; c++)
        {
            color[c] = (int)((emissive >> (c * 8)) & 0xFF) + ((Factor(materialAmbient, c) * Factor(globalAmbient, c)) >> 10);
            specular[c] = 0;
        }

        bool anySpecular = false;
        for (int index = 0; index < 4; index++)
        {
            GeLight light = _lights[index];
            if (!light.Enabled)
            {
                continue;
            }

            Vector3 toLight = ToLight(light, worldPosition, out float distance);
            float attenuation = 1;
            float spot = 1;
            if (!light.Directional)
            {
                float denominator = Vector3.Dot(light.Attenuation, new Vector3(1, distance, distance * distance));
                attenuation = denominator > 0 ? Math.Clamp(1 / denominator, 0, 1) : 0;
            }

            if (light.Spot)
            {
                float directionLength = light.Direction.Length();
                float rawSpot = directionLength > 0 ? Vector3.Dot(light.Direction, toLight) / directionLength : 0;
                if (float.IsNaN(rawSpot))
                {
                    rawSpot = float.IsNegative(rawSpot) ? 0 : 1;
                }

                spot = rawSpot >= light.SpotCutoff ? LightPower(rawSpot, light.SpotExponent) : 0;
                if (float.IsNaN(spot))
                {
                    spot = 0;
                }
            }

            float diffuseFactor = NormalDot(toLight, worldNormal);
            if (light.PoweredDiffuse)
            {
                diffuseFactor = LightPower(diffuseFactor, specularExponent);
            }

            float specularFactor = 0;
            if (light.Specular && diffuseFactor >= 0)
            {
                Vector3 half = toLight + _viewDirection;
                half = half.LengthSquared() > 0 ? Vector3.Normalize(half) : Vector3.UnitZ;
                specularFactor = LightPower(NormalDot(half, worldNormal), specularExponent);
            }

            for (int c = 0; c < 4; c++)
            {
                int ambient = (Factor(light.Ambient, c) * Factor(materialAmbient, c)) >> 10;
                int diffuse = Scale((Factor(light.Diffuse, c) * Factor(materialDiffuse, c)) >> 10, diffuseFactor);
                color[c] += AttenuateAndSpot(ambient, attenuation, spot) + AttenuateAndSpot(diffuse, attenuation, spot);
                if (specularFactor > 0)
                {
                    int product = Scale((Factor(light.SpecularColor, c) * Factor(materialSpecular, c)) >> 10, specularFactor);
                    specular[c] += AttenuateAndSpot(product, attenuation, spot);
                    anySpecular = true;
                }
            }
        }

        bool separate = (Command(GeCommand.LightMode) & 1) != 0 && anySpecular;
        uint result = 0;
        uint secondary = 0;
        for (int c = 0; c < 4; c++)
        {
            int total = separate ? color[c] : color[c] + specular[c];
            result |= (uint)Math.Clamp(total, 0, 255) << (c * 8);
            if (c < 3)
            {
                secondary |= (uint)Math.Clamp(specular[c], 0, 255) << (c * 8);
            }
        }

        vertex.Color = result;
        vertex.Color1 = separate ? secondary : 0;
    }

    private static int AttenuateAndSpot(int value, float attenuation, float spot)
    {
        if (attenuation < 1)
        {
            value = Scale(value, attenuation);
        }

        if (spot < 1)
        {
            value = Scale(value, spot);
        }

        return value;
    }
}
