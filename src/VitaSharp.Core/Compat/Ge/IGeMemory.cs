namespace VitaSharp.Core.Compat.Ge;

/// <summary>
/// The memory the GE reads its lists, vertices, textures and CLUTs from and draws into: PSP physical addresses (the
/// segment bits removed), main RAM and the VRAM. <see cref="Span"/> gives a contiguous view for the bulk work
/// (textures, framebuffers, block transfers); empty when the range is not mapped memory in one piece.
/// </summary>
public interface IGeMemory
{
    uint Read32(uint physical);

    void Write32(uint physical, uint value);

    Span<byte> Span(uint physical, int length);
}
