namespace VitaSharp.Core.Hle.Audio;

/// <summary>
/// Sony ADPCM (VAG) 16-byte blocks: byte 0 = (predictor &lt;&lt; 4) | shift, byte 1 = flags (low nibble; for HEVAG the
/// high nibble extends the predictor to 7 bits), bytes 2..15 = 28 nibbles, low nibble first. The HEVAG predictor table
/// is a superset of the classic PS-ADPCM one (its first 5 entries are the VAG filters scaled by 128), so one routine
/// decodes both.
/// </summary>
public static class Adpcm
{
    public const int SamplesPerBlock = 28, BlockBytes = 16;
    public const int FlagLoopEnd = 1, FlagRepeat = 2, FlagLoopStart = 4, FlagEndOfStream = 7;

    /// <summary>Block flags (low nibble of byte 1).</summary>
    public static int Flags(ReadOnlySpan<byte> block) => block[1] & 0xF;

    /// <summary>
    /// Decodes one block into 28 samples. <paramref name="hist"/> holds the previous outputs (newest first, 4 entries for
    /// HEVAG, the classic filters only use 2) and is updated. Outputs (and history) are clamped to s16 as on the SPU.
    /// </summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<int> hist, Span<short> output)
    {
        int b0 = block[0], b1 = block[1];
        int coef = (b0 >> 4) | (b1 & 0xF0);
        if (coef > 127) coef = 0;
        int shift = 20 - (b0 & 0xF);
        int c0 = HevagTables.Coefs[coef, 0], c1 = HevagTables.Coefs[coef, 1], c2 = HevagTables.Coefs[coef, 2], c3 = HevagTables.Coefs[coef, 3];
        int h1 = hist[0], h2 = hist[1], h3 = hist[2], h4 = hist[3];
        for (int i = 0; i < SamplesPerBlock; i++)
        {
            int nib = block[2 + i / 2];
            nib = (i & 1) != 0 ? nib >> 4 : nib & 0xF;
            int s = shift >= 0 ? HevagTables.Nibbles[nib] << shift : HevagTables.Nibbles[nib] >> -shift;
            s += (h1 * c0 + h2 * c1 + h3 * c2 + h4 * c3) >> 5;
            s = Math.Clamp(s >> 8, short.MinValue, short.MaxValue);
            output[i] = (short)s;
            h4 = h3; h3 = h2; h2 = h1; h1 = s;
        }
        hist[0] = h1; hist[1] = h2; hist[2] = h3; hist[3] = h4;
    }
}
