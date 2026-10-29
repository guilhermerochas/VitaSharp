using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Loader;

/// <summary>Loaded segment info used to resolve SCE relocations.</summary>
public readonly record struct SegmentInfo(uint Address, uint OriginalVaddr, uint Size);

/// <summary>
/// SCE relocation stream decoder (PT_SCE_RELA, formats 0-9). Ported from Vita3K (kernel/src/relocation.cpp).
/// </summary>
public static unsafe class Relocation
{
    const uint None = 0, Abs32 = 2, Rel32 = 3, Abs8 = 8, ThumbCall = 10, Call = 28, Jump24 = 29, Target1 = 38,
        V4BX = 40, Target2 = 41, Prel31 = 42, MovwAbsNc = 43, MovtAbs = 44, ThumbMovwAbsNc = 47, ThumbMovtAbs = 48, RBase = 255;

    /// <summary>When set (<see cref="RelocateImage"/>), relocations patch this host buffer instead of guest memory.</summary>
    [ThreadStatic] static byte* _imageBase;

    static byte* At(uint p) => _imageBase != null ? _imageBase + p : GuestMemory.Ptr(p);

    /// <summary>Applies a relocation stream to a module image in a host buffer (segments laid out at the offsets in
    /// <paramref name="segs"/>), for reading a module's tables without loading it.</summary>
    public static void RelocateImage(ReadOnlySpan<byte> data, IReadOnlyDictionary<int, SegmentInfo> segs, byte* image)
    {
        _imageBase = image;
        try { Relocate(data, segs); }
        finally { _imageBase = null; }
    }

    static void Apply(uint p, uint code, uint s, uint a)
    {
        byte* ptr = At(p);
        switch (code)
        {
            case None: case V4BX: case RBase: return;
            case Abs32: case Target1: *(uint*)ptr = s + a; return;
            case Abs8: *ptr = (byte)(s + a); return;
            case Rel32: case Target2: *(uint*)ptr = s + a - p; return;
            case Prel31: *(uint*)ptr = (s + a - p) & 0x7FFFFFFF; return;
            case ThumbCall:
            {
                uint v = s + a - p;
                ushort* hw = (ushort*)ptr;
                uint sign = (v >> 24) & 1;
                uint j1 = sign ^ ((~v >> 23) & 1);
                uint j2 = sign ^ ((~v >> 22) & 1);
                hw[0] = (ushort)(((uint)hw[0] & 0xF800u) | (sign << 10) | ((v >> 12) & 0x3FF));
                hw[1] = (ushort)(((uint)hw[1] & 0xD000u) | (j1 << 13) | (j2 << 11) | ((v >> 1) & 0x7FF));
                return;
            }
            case Call: case Jump24:
            {
                uint v = (s + a - p) >> 2;
                *(uint*)ptr = (*(uint*)ptr & 0xFF000000) | (v & 0xFFFFFF);
                return;
            }
            case MovwAbsNc: WriteMovAbs(ptr, (ushort)(s + a)); return;
            case MovtAbs: WriteMovAbs(ptr, (ushort)((s + a) >> 16)); return;
            case ThumbMovwAbsNc: WriteThumbMovAbs(ptr, (ushort)(s + a)); return;
            case ThumbMovtAbs: WriteThumbMovAbs(ptr, (ushort)((s + a) >> 16)); return;
        }
    }

    static void WriteMovAbs(byte* p, ushort v)
    {
        uint ins = *(uint*)p;
        ins = (ins & 0xFFF0F000) | (v & 0xFFFu) | ((uint)(v >> 12) << 16);
        *(uint*)p = ins;
    }

    static void WriteThumbMovAbs(byte* p, ushort v)
    {
        ushort* hw = (ushort*)p;
        hw[0] = (ushort)((hw[0] & 0xFBF0) | ((v >> 12) & 0xF) | (((v >> 11) & 1) << 10));
        hw[1] = (ushort)((hw[1] & 0x8F00) | (v & 0xFF) | (((v >> 8) & 7) << 12));
    }

    /// <summary>
    /// Applies a relocation stream. For variable/function-reference imports (<paramref name="isVarImport"/>),
    /// formats 1 and 2 use their alternate layouts and the symbol value is <paramref name="explicitSymval"/>.
    /// </summary>
    public static void Relocate(ReadOnlySpan<byte> data, IReadOnlyDictionary<int, SegmentInfo> segs, bool isVarImport = false, uint explicitSymval = 0)
    {
        uint gAddr = 0, gOffset = 0, gPatchSeg = 0, gSaddr = 0, gAddend = 0, gType = 0, gType2 = 0;
        int pos = 0;
        uint SegAddr(uint idx) => segs.TryGetValue((int)idx, out var si) ? si.Address : 0;

        while (pos + 4 <= data.Length)
        {
            uint w0 = BitConverter.ToUInt32(data[pos..]);
            uint format = w0 & 0xF;
            switch (format)
            {
                case 0:
                {
                    uint symSeg = (w0 >> 4) & 0xF, code = (w0 >> 8) & 0xFF, patchSeg = (w0 >> 16) & 0xF, code2 = (w0 >> 20) & 0xFF, dist2 = (w0 >> 28) & 0xF;
                    uint addend = BitConverter.ToUInt32(data[(pos + 4)..]), offset = BitConverter.ToUInt32(data[(pos + 8)..]);
                    uint s = symSeg == 0xF ? 0 : SegAddr(symSeg);
                    uint p = SegAddr(patchSeg) + offset;
                    Apply(p, code, s, addend);
                    if (code2 != 0) Apply(p + dist2 * 2, code2, s, addend);
                    gAddr = SegAddr(patchSeg); gOffset = offset; gPatchSeg = patchSeg; gSaddr = s; gAddend = addend; gType = code; gType2 = code2;
                    pos += 12;
                    break;
                }
                case 1:
                {
                    uint w1 = BitConverter.ToUInt32(data[(pos + 4)..]);
                    if (!isVarImport)
                    {
                        uint symSeg = (w0 >> 4) & 0xF, code = (w0 >> 8) & 0xFF, patchSeg = (w0 >> 16) & 0xF;
                        uint offset = ((w0 >> 20) & 0xFFF) | ((w1 & 0x3FF) << 12);
                        uint addend = w1 >> 10;
                        uint s = symSeg == 0xF ? 0 : SegAddr(symSeg);
                        uint p = SegAddr(patchSeg) + offset;
                        Apply(p, code, s, addend);
                        gAddr = SegAddr(patchSeg); gOffset = offset; gPatchSeg = patchSeg; gSaddr = s; gAddend = addend; gType = code; gType2 = 0;
                    }
                    else
                    {
                        uint patchSeg = (w0 >> 4) & 0xF, code = (w0 >> 8) & 0xFF, addend = w0 >> 16;
                        uint offset = w1;
                        if (segs.ContainsKey((int)patchSeg))
                        {
                            uint p = SegAddr(patchSeg) + offset;
                            Apply(p, code, explicitSymval, addend);
                            gAddr = SegAddr(patchSeg); gOffset = offset; gPatchSeg = patchSeg; gSaddr = explicitSymval; gAddend = addend; gType = code; gType2 = 0;
                        }
                    }
                    pos += 8;
                    break;
                }
                case 2:
                {
                    if (!isVarImport)
                    {
                        uint symSeg = (w0 >> 4) & 0xF, code = (w0 >> 8) & 0xFF, offset = w0 >> 16;
                        uint addend = BitConverter.ToUInt32(data[(pos + 4)..]);
                        gOffset += offset;
                        gSaddr = symSeg == 0xF ? 0 : SegAddr(symSeg);
                        gAddend = addend;
                        gType = code;
                        Apply(gAddr + gOffset, gType, gSaddr, gAddend);
                        gType2 = 0;
                        pos += 8;
                    }
                    else
                    {
                        uint patchSeg = (w0 >> 4) & 0xF, code = (w0 >> 8) & 0xFF;
                        uint offset = BitConverter.ToUInt32(data[(pos + 4)..]), addend = BitConverter.ToUInt32(data[(pos + 8)..]);
                        if (segs.ContainsKey((int)patchSeg))
                        {
                            uint p = SegAddr(patchSeg) + offset;
                            Apply(p, code, explicitSymval, addend);
                            gAddr = SegAddr(patchSeg); gOffset = offset; gPatchSeg = patchSeg; gSaddr = explicitSymval; gAddend = addend; gType = code; gType2 = 0;
                        }
                        pos += 12;
                    }
                    break;
                }
                case 3:
                {
                    uint w1 = BitConverter.ToUInt32(data[(pos + 4)..]);
                    uint symSeg = (w0 >> 4) & 0xF, mode = (w0 >> 8) & 1, offset = (w0 >> 9) & 0x3FFFF, dist2 = (w0 >> 27) & 0x1F;
                    uint addend = w1 & 0x3FFFFF;
                    uint s = symSeg == 0xF ? 0 : SegAddr(symSeg);
                    gType = mode == 1 ? ThumbMovwAbsNc : MovwAbsNc;
                    gType2 = mode == 1 ? ThumbMovtAbs : MovtAbs;
                    gOffset += offset;
                    gSaddr = s;
                    gAddend = addend;
                    uint p = gAddr + gOffset;
                    Apply(p, gType, s, addend);
                    Apply(p + dist2, gType2, s, addend);
                    pos += 8;
                    break;
                }
                case 4:
                {
                    uint offset = (w0 >> 4) & 0x7FFFFF, dist2 = (w0 >> 27) & 0x1F;
                    gOffset += offset;
                    uint p = gAddr + gOffset;
                    Apply(p, gType, gSaddr, gAddend);
                    Apply(p + dist2, gType2, gSaddr, gAddend);
                    pos += 4;
                    break;
                }
                case 5:
                {
                    uint dist1 = (w0 >> 4) & 0x1FF, dist2 = (w0 >> 13) & 0x1F, dist3 = (w0 >> 18) & 0x1FF, dist4 = (w0 >> 27) & 0x1F;
                    gOffset += dist1;
                    uint p = gAddr + gOffset;
                    Apply(p, gType, gSaddr, gAddend);
                    Apply(p + dist2, gType2, gSaddr, gAddend);
                    gOffset += dist3;
                    uint p2 = gAddr + gOffset;
                    Apply(p2, gType, gSaddr, gAddend);
                    Apply(p2 + dist4, gType2, gSaddr, gAddend);
                    pos += 4;
                    break;
                }
                case 6:
                {
                    gOffset += w0 >> 4;
                    RelocateAbsFromOriginal(segs, gPatchSeg, gAddr, gOffset, ref gSaddr);
                    gType = Abs32; gType2 = 0;
                    pos += 4;
                    break;
                }
                case 7: case 8: case 9:
                {
                    uint offsets = w0 >> 4;
                    int bitsize = format == 7 ? 7 : format == 8 ? 4 : 2;
                    uint mask = format == 7 ? 0x7Fu : format == 8 ? 0xFu : 0x3u;
                    do
                    {
                        gOffset += (offsets & mask) * 4;
                        RelocateAbsFromOriginal(segs, gPatchSeg, gAddr, gOffset, ref gSaddr);
                    } while ((offsets >>= bitsize) != 0);
                    gType = Abs32; gType2 = 0;
                    pos += 4;
                    break;
                }
                default:
                    throw new InvalidDataException($"Unknown SCE relocation format {format}");
            }
        }
    }

    /// <summary>Formats 6-9: the patched word holds an unrelocated address; rebase it into whichever segment it pointed to.</summary>
    static void RelocateAbsFromOriginal(IReadOnlyDictionary<int, SegmentInfo> segs, uint patchSeg, uint gAddr, uint gOffset, ref uint gSaddr)
    {
        uint patchStart = segs.TryGetValue((int)patchSeg, out var ps) ? ps.Address : 0;
        uint orig = *(uint*)At(patchStart + gOffset);
        uint segbase = 0;
        foreach (var seg in segs.Values)
        {
            if (orig >= seg.OriginalVaddr && orig < seg.OriginalVaddr + seg.Size)
            {
                segbase = seg.OriginalVaddr;
                gSaddr = seg.Address;
            }
        }
        Apply(gAddr + gOffset, Abs32, gSaddr, orig - segbase);
    }
}
