namespace VitaSharp.Core.Compat.Ge;

/// <summary>
/// The layout of a vertex as the GE's VTYPE command (0x12) describes it: bits 0..1 texture coordinates (none, u8,
/// u16, float; two of them), 2..4 colour (none, -, -, -, 565, 5551, 4444, 8888), 5..6 normal (none, s8, s16, float;
/// three), 7..8 position (as the normal), 9..10 weights (none, u8, u16, float; 1 + bits 14..16 of them), 11..12 the
/// index buffer (none, u8, u16, u32), 18..20 the morph targets - 1, bit 23 "through" (screen coordinates, no
/// transform). In memory a vertex is its weights, texture coordinates, colour, normal and position in that order,
/// each aligned to its component size, the whole aligned to the largest component; a morphing vertex is its
/// targets one after the other.
/// </summary>
public readonly struct GeVertexFormat
{
    private static readonly int[] s_componentSizes = [0, 1, 2, 4];
    private static readonly int[] s_colorSizes = [0, 0, 0, 0, 2, 2, 2, 4];

    public GeVertexFormat(uint vertexType)
    {
        Raw = vertexType & 0xFFFFFF;
        int offset = 0;
        int alignment = 1;

        WeightCount = WeightFormat == 0 ? 0 : (int)((Raw >> 14) & 7) + 1;
        WeightOffset = Place(ref offset, ref alignment, s_componentSizes[WeightFormat], WeightCount);
        TextureOffset = Place(ref offset, ref alignment, s_componentSizes[TextureFormat], 2);
        ColorOffset = Place(ref offset, ref alignment, s_colorSizes[ColorFormat], 1);
        NormalOffset = Place(ref offset, ref alignment, s_componentSizes[NormalFormat], 3);
        PositionOffset = Place(ref offset, ref alignment, s_componentSizes[PositionFormat], 3);
        Alignment = alignment;
        Stride = (offset + alignment - 1) & ~(alignment - 1);
        MorphCount = (int)((Raw >> 18) & 7) + 1;
        Size = Stride * MorphCount;
    }

    /// <summary>The VTYPE argument.</summary>
    public uint Raw { get; }

    public int TextureFormat => (int)(Raw & 3);

    public int ColorFormat => (int)((Raw >> 2) & 7);

    public int NormalFormat => (int)((Raw >> 5) & 3);

    public int PositionFormat => (int)((Raw >> 7) & 3);

    public int WeightFormat => (int)((Raw >> 9) & 3);

    public int IndexFormat => (int)((Raw >> 11) & 3);

    /// <summary>The bytes of one index (0: not indexed).</summary>
    public int IndexSize => s_componentSizes[IndexFormat];

    public bool Through => (Raw & (1u << 23)) != 0;

    public int WeightCount { get; }

    public int MorphCount { get; }

    /// <summary>Offsets of each attribute in one morph target (-1 when absent).</summary>
    public int WeightOffset { get; }

    public int TextureOffset { get; }

    public int ColorOffset { get; }

    public int NormalOffset { get; }

    public int PositionOffset { get; }

    public int Alignment { get; }

    /// <summary>The bytes of one morph target.</summary>
    public int Stride { get; }

    /// <summary>The bytes of one vertex (every morph target).</summary>
    public int Size { get; }

    private static int Place(ref int offset, ref int alignment, int componentSize, int count)
    {
        if (componentSize == 0 || count == 0)
        {
            return -1;
        }

        offset = (offset + componentSize - 1) & ~(componentSize - 1);
        int placed = offset;
        offset += componentSize * count;
        alignment = Math.Max(alignment, componentSize);
        return placed;
    }
}
