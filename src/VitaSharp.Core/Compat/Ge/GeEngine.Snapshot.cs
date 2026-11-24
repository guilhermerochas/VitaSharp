using System.Numerics;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Compat.Ge;

/// <summary>
/// Save states (docs/notes/save-states.md): the GE's registers as the core sees them (the command registers, the
/// matrices with their upload indices, the EDRAM controller, the list being run: its address, stall address, the call
/// stack, the branch flag, the interrupt status and enable mask with the line), the CLUT the last LOADCLUT filled and
/// the texture coordinates and normal vertices carry over. A list stops only at END, its stall address or a FINISH,
/// and the core runs between two of those stops at a safe point, so the list in progress is these registers.
/// Before the save, what the rasterizer still holds on the GPU goes to memory (the memory is saved after the
/// modules); after a load the rasterizer's copies are dropped (the loaded memory is newer) and the texture cache is
/// emptied. The scratch arrays of the vertex, patch and draw stages, the rasterizer and the callbacks are the
/// session's.
/// </summary>
[GenerateSnapshot, SnapshotIgnoreFields("_executing", "Rasterizer", "RasterizerFactory", "InterruptLevel", "_clipVertices",
    "_transformer", "_assembled", "_rasterizerAsked", "_rasterizerOwned", "Batches", "_textures", "_executionEpoch",
    "_nextTextureId", "_controlPoints", "_patchVertices", "_patchIndices", "_patchColumns")]
public sealed partial class GeEngine
{
    /// <summary>The PSP's physical space: a load drops the rasterizer's copies of all of it.</summary>
    private const int PhysicalSpaceBytes = 0x20000000;

    void SaveExtra(SnapshotWriter w)
    {
        Rasterizer?.Flush(_memory);
        (float u, float v, Vector3 normal) = _transformer?.Carry ?? default;
        w.Write(u);
        w.Write(v);
        w.Write(normal.X);
        w.Write(normal.Y);
        w.Write(normal.Z);
    }

    void LoadExtra(SnapshotReader r)
    {
        float u = r.ReadSingle();
        float v = r.ReadSingle();
        Vector3 normal = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        _transformer ??= new GeVertexTransformer(this);
        _transformer.Carry = (u, v, normal);
        Rasterizer?.Invalidate(0, PhysicalSpaceBytes);
        _textures.Clear();
        _executionEpoch++;
    }
}
