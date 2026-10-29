using System.Runtime.InteropServices;
using static VitaSharp.Core.Util.BinaryHelpers;

namespace VitaSharp.Core.Loader;

/// <summary>
/// The tables of a module (name, exported and imported libraries) read from its ELF without loading it into guest
/// memory: the segments are laid out in a host buffer at their link addresses and relocated there, so the pointers in
/// module_info and the export/import entries can be followed. Used to find which firmware module provides a library.
/// </summary>
public sealed class ModuleImage
{
    public string Name { get; private init; } = "";
    public ushort Attributes { get; private init; }
    public List<(string Library, uint LibNid, ushort Attributes, uint[] FuncNids, uint[] VarNids)> Exports { get; } = new();
    public List<(string Library, uint LibNid, uint[] FuncNids)> Imports { get; } = new();

    const uint PT_LOAD = 1, PT_SCE_RELA = 0x60000000;

    public static unsafe ModuleImage Read(byte[] elf)
    {
        ReadOnlySpan<byte> e = elf;
        if (U32LE(e, 0) != 0x464C457F) throw new InvalidDataException("not an ELF");
        uint entry = U32LE(e, 24), phoff = U32LE(e, 28);
        ushort phnum = U16LE(e, 44);

        // layout: each loadable segment at (vaddr - lowest vaddr); relocatable modules link at 0
        var loads = new List<(int Index, uint Off, uint Vaddr, uint FileSz, uint MemSz)>();
        var relas = new List<(uint Off, uint Size)>();
        for (int i = 0; i < phnum; i++)
        {
            var ph = e.Slice((int)phoff + i * 32, 32);
            uint type = U32LE(ph, 0);
            if (type == PT_LOAD && U32LE(ph, 20) != 0) loads.Add((i, U32LE(ph, 4), U32LE(ph, 8), U32LE(ph, 16), U32LE(ph, 20)));
            else if (type == PT_SCE_RELA) relas.Add((U32LE(ph, 4), U32LE(ph, 16)));
        }
        if (loads.Count == 0) throw new InvalidDataException("no loadable segment");
        uint low = loads.Min(l => l.Vaddr);
        // segments of relocatable modules may all sit at 0: give each its own slot after the previous one
        var segs = new Dictionary<int, SegmentInfo>();
        uint cursor = 0;
        bool overlapping = loads.Select(l => l.Vaddr).Distinct().Count() != loads.Count;
        foreach (var l in loads)
        {
            uint at = overlapping ? cursor : l.Vaddr - low;
            segs[l.Index] = new SegmentInfo(at, l.Vaddr, l.MemSz);
            cursor = Math.Max(cursor, (at + l.MemSz + 0xFFF) & ~0xFFFu);
        }
        uint total = segs.Values.Max(s => s.Address + s.Size) + 16;
        if (total > 256u << 20) throw new InvalidDataException("module image too large");
        byte[] image = new byte[total];
        foreach (var l in loads)
            e.Slice((int)l.Off, (int)l.FileSz).CopyTo(image.AsSpan((int)segs[l.Index].Address));

        fixed (byte* img = image)
        {
            foreach (var (off, size) in relas) Relocation.RelocateImage(e.Slice((int)off, (int)size), segs, img);
        }

        ReadOnlySpan<byte> m = image;
        int infoSeg = (int)(entry >> 30);
        uint infoBase = segs[infoSeg].Address;
        int mi = (int)(infoBase + (entry & 0x3FFFFFFF));
        var result = new ModuleImage { Name = CString(m.Slice(mi + 4, 27)), Attributes = U16LE(m, mi) };
        uint exportTop = U32LE(m, mi + 0x24), exportEnd = U32LE(m, mi + 0x28);
        uint importTop = U32LE(m, mi + 0x2C), importEnd = U32LE(m, mi + 0x30);

        string Str(uint p) => p != 0 && p < image.Length ? CString(image.AsSpan((int)p)) : "";
        uint[] Nids(uint p, int n) => n > 0 && p + (uint)n * 4 <= image.Length ? MemoryMarshal.Cast<byte, uint>(image.AsSpan((int)p, n * 4)).ToArray() : [];

        for (uint p = infoBase + exportTop; p + 0x20 <= infoBase + exportEnd;)
        {
            ushort size = U16LE(m, (int)p);
            ushort attr = U16LE(m, (int)p + 4), nfunc = U16LE(m, (int)p + 6);
            uint nvar = U32LE(m, (int)p + 8);
            uint libNid = U32LE(m, (int)p + 0x10), libName = U32LE(m, (int)p + 0x14), nidTable = U32LE(m, (int)p + 0x18);
            var nids = Nids(nidTable, nfunc + (int)nvar);
            if (nids.Length == nfunc + (int)nvar)
                result.Exports.Add((Str(libName), libNid, attr, nids[..nfunc], nids[nfunc..]));
            if (size == 0) break;
            p += size;
        }
        for (uint p = infoBase + importTop; p + 0x24 <= infoBase + importEnd;)
        {
            ushort size = U16LE(m, (int)p);
            ushort nfunc = U16LE(m, (int)p + 6);
            uint libNid, libName, funcNids;
            if (size == 0x24) { libNid = U32LE(m, (int)p + 0x0C); libName = U32LE(m, (int)p + 0x10); funcNids = U32LE(m, (int)p + 0x14); }
            else { libNid = U32LE(m, (int)p + 0x10); libName = U32LE(m, (int)p + 0x14); funcNids = U32LE(m, (int)p + 0x1C); }
            result.Imports.Add((Str(libName), libNid, Nids(funcNids, nfunc)));
            if (size == 0) break;
            p += size;
        }
        return result;
    }
}
