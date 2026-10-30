using System.Buffers.Binary;

namespace VitaSharp.Core.Gpu;

/// <summary>
/// Programmable blending without rasterization-order attachment access: every fragment of a GXM draw reads what the
/// earlier primitives of the same draw wrote, but a host GPU only orders input-attachment reads against the writes
/// of earlier draws (behind a barrier). Small draws are therefore drawn piece by piece with a barrier in between.
/// </summary>
public static class FetchSplit
{
    /// <summary>
    /// The pieces (first index, index count) of an indexed draw. Triangle lists: one triangle per piece. Triangle
    /// strips: two triangles per piece, starting at an even index so each keeps its winding (and provoking vertex);
    /// the two share an edge and lie on either side of it, so they do not overlap. Strips of particle quads joined by
    /// degenerate triangles (Soul Sacrifice's fire and smoke) would otherwise blend all their quads against the colour
    /// from before the draw, racing with each other in 8x8-pixel blocks on desktop GPUs. Pieces whose triangles are
    /// all degenerate (a repeated index) draw nothing and are left out.
    /// </summary>
    /// <returns>false (draw it whole): fewer than two pieces, or more than <paramref name="max"/></returns>
    public static bool Pieces(ReadOnlySpan<byte> indices, bool index32, bool strip, List<(uint First, uint Count)> pieces, int max)
    {
        pieces.Clear();
        int n = indices.Length / (index32 ? 4 : 2);
        if (!strip)
        {
            for (int t = 0; t + 2 < n; t += 3)
            {
                if (Degenerate(indices, index32, t)) continue;
                if (pieces.Count == max) return false;
                pieces.Add(((uint)t, 3));
            }
        }
        else
        {
            for (int s = 0; s + 2 < n; s += 2)
            {
                int count = Math.Min(4, n - s);
                if (Degenerate(indices, index32, s) && (count < 4 || Degenerate(indices, index32, s + 1))) continue;
                if (pieces.Count == max) return false;
                pieces.Add(((uint)s, (uint)count));
            }
        }
        return pieces.Count > 1;
    }

    static uint At(ReadOnlySpan<byte> s, bool index32, int i) => index32
        ? BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 4)..])
        : BinaryPrimitives.ReadUInt16LittleEndian(s[(i * 2)..]);

    /// <summary>The triangle of indices i, i+1, i+2 repeats a vertex (it covers no pixel).</summary>
    static bool Degenerate(ReadOnlySpan<byte> s, bool index32, int i)
    {
        uint a = At(s, index32, i), b = At(s, index32, i + 1), c = At(s, index32, i + 2);
        return a == b || b == c || a == c;
    }
}
