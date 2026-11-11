using System.Buffers.Binary;
using System.Text;

namespace VitaSharp.Tests;

/// <summary>
/// Builds a small relocatable SCE module (an ELF of type ET_SCE_RELEXEC with one segment and a PT_SCE_RELA stream) for
/// loader tests: Thumb functions that return a constant or call an import, exported in named libraries with chosen
/// attributes, and imported libraries with their stubs. Everything is position independent except the table pointers,
/// which carry relocations, so the module loads anywhere (the kernel loader relocates its modules).
/// </summary>
public sealed class SceModuleBuilder(string name)
{
    private const uint NidModuleStart = 0x935CD196;
    private const int CodeAt = 0x0, InfoAt = 0x400, ExportsAt = 0x500, ImportsAt = 0x700, StubsAt = 0x900, DataAt = 0xA00, Size = 0x1000;

    private readonly List<(string Name, uint Nid, ushort Attr, List<(uint Nid, int Function)> Functions)> _exports = [];
    private readonly List<(string Name, uint Nid, uint[] Functions)> _imports = [];
    private readonly List<ushort[]> _code = [];
    private int _startFunction = -1;

    /// <summary>A Thumb function returning <paramref name="value"/> (0..255); returns its index.</summary>
    public int ReturnConstant(byte value)
    {
        _code.Add([(ushort)(0x2000 | value), 0x4770]); // movs r0, #value; bx lr
        return _code.Count - 1;
    }

    /// <summary>A Thumb function that calls import <paramref name="importIndex"/>'s function
    /// <paramref name="functionIndex"/> with its own arguments and returns its result; returns its index.</summary>
    public int CallImport(int importIndex, int functionIndex)
    {
        // push {r4, lr}; ldr r3, [pc, #4]; blx r3; pop {r4, pc}; then the stub address (a relocated literal)
        _code.Add([0xB510, 0x4B01, 0x4798, 0xBD10, (ushort)(importIndex << 8 | functionIndex), 0xFFFF]);
        return _code.Count - 1;
    }

    /// <summary>module_start: the function returns this value (0 = SCE_KERNEL_START_SUCCESS).</summary>
    public SceModuleBuilder Start(byte status)
    {
        _startFunction = ReturnConstant(status);
        return this;
    }

    public SceModuleBuilder Export(string library, uint libNid, ushort attr, params (uint Nid, int Function)[] functions)
    {
        _exports.Add((library, libNid, attr, functions.ToList()));
        return this;
    }

    public SceModuleBuilder Import(string library, uint libNid, params uint[] functions)
    {
        _imports.Add((library, libNid, functions));
        return this;
    }

    public byte[] Build()
    {
        byte[] seg = new byte[Size];
        var relocs = new List<(uint Offset, uint Value)>();
        void W16(int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(seg.AsSpan(at), v);
        void W32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(seg.AsSpan(at), v);
        void Ptr(int at, uint target)
        {
            W32(at, target);
            relocs.Add(((uint)at, target));
        }

        int data = DataAt;
        uint Str(string s)
        {
            int at = data;
            Encoding.ASCII.GetBytes(s).CopyTo(seg, at);
            data += s.Length + 1;
            return (uint)at;
        }

        // code: each function at a 16-byte slot
        var functionAt = new List<int>();
        for (int i = 0; i < _code.Count; i++)
        {
            int at = CodeAt + i * 0x10;
            functionAt.Add(at);
            ushort[] words = _code[i];
            bool call = words.Length == 6;
            for (int k = 0; k < (call ? 4 : words.Length); k++)
            {
                W16(at + k * 2, words[k]);
            }

            if (call)
            {
                int imp = words[4] >> 8, fn = words[4] & 0xFF;
                int stubIndex = _imports.Take(imp).Sum(x => x.Functions.Length) + fn;
                Ptr(at + 8, (uint)(StubsAt + stubIndex * 0x10));
            }
        }

        // exports: the module's own (module_start) library first
        var libs = new List<(string? Name, uint Nid, ushort Attr, List<(uint Nid, uint Addr)> F)>();
        if (_startFunction >= 0)
        {
            libs.Add((null, 0, 0x8000, [(NidModuleStart, (uint)functionAt[_startFunction] | 1)]));
        }

        foreach ((string Name, uint Nid, ushort Attr, List<(uint Nid, int Function)> Functions) e in _exports)
        {
            libs.Add((e.Name, e.Nid, e.Attr, e.Functions.Select(f => (f.Nid, (uint)functionAt[f.Function] | 1)).ToList()));
        }

        int exp = ExportsAt;
        foreach ((string? Name, uint Nid, ushort Attr, List<(uint Nid, uint Addr)> F) lib in libs)
        {
            uint nids = (uint)data;
            data += lib.F.Count * 4;
            uint entries = (uint)data;
            data += lib.F.Count * 4;
            for (int i = 0; i < lib.F.Count; i++)
            {
                W32((int)nids + i * 4, lib.F[i].Nid);
                Ptr((int)entries + i * 4, lib.F[i].Addr);
            }

            W16(exp, 0x20);
            W16(exp + 2, 1);
            W16(exp + 4, lib.Attr);
            W16(exp + 6, (ushort)lib.F.Count);
            W32(exp + 0x10, lib.Nid);
            if (lib.Name != null)
            {
                Ptr(exp + 0x14, Str(lib.Name));
            }

            Ptr(exp + 0x18, nids);
            Ptr(exp + 0x1C, entries);
            exp += 0x20;
        }

        // imports (0x34-byte entries) and their stubs (16 bytes each, rewritten by the linker)
        int imp0 = ImportsAt, stub = StubsAt;
        foreach ((string Name, uint Nid, uint[] Functions) lib in _imports)
        {
            uint nids = (uint)data;
            data += lib.Functions.Length * 4;
            uint entries = (uint)data;
            data += lib.Functions.Length * 4;
            for (int i = 0; i < lib.Functions.Length; i++)
            {
                W32((int)nids + i * 4, lib.Functions[i]);
                Ptr((int)entries + i * 4, (uint)stub);
                stub += 0x10;
            }

            W16(imp0, 0x34);
            W16(imp0 + 2, 1);
            W16(imp0 + 6, (ushort)lib.Functions.Length);
            W32(imp0 + 0x10, lib.Nid);
            Ptr(imp0 + 0x14, Str(lib.Name));
            Ptr(imp0 + 0x1C, nids);
            Ptr(imp0 + 0x20, entries);
            imp0 += 0x34;
        }

        // module_info: offsets of the tables from the segment start
        W16(InfoAt, 0);
        W16(InfoAt + 2, 0x0101);
        Encoding.ASCII.GetBytes(name).CopyTo(seg, InfoAt + 4);
        W32(InfoAt + 0x24, ExportsAt);
        W32(InfoAt + 0x28, (uint)exp);
        W32(InfoAt + 0x2C, ImportsAt);
        W32(InfoAt + 0x30, (uint)imp0);
        W32(InfoAt + 0x34, 0x12345678);
        W32(InfoAt + 0x44, _startFunction >= 0 ? (uint)functionAt[_startFunction] | 1 : 0);
        W32(InfoAt + 0x48, 0xFFFFFFFF);

        // the ELF: header, 2 program headers (PT_LOAD, PT_SCE_RELA), the segment, the relocations (format 0, R_ARM_ABS32)
        byte[] rela = new byte[relocs.Count * 12];
        for (int i = 0; i < relocs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rela.AsSpan(i * 12), 2u << 8);
            BinaryPrimitives.WriteUInt32LittleEndian(rela.AsSpan(i * 12 + 4), relocs[i].Value);
            BinaryPrimitives.WriteUInt32LittleEndian(rela.AsSpan(i * 12 + 8), relocs[i].Offset);
        }

        const int SegOffset = 0x100;
        byte[] elf = new byte[SegOffset + Size + rela.Length];
        Span<byte> h = elf;
        h[0] = 0x7F;
        h[1] = (byte)'E';
        h[2] = (byte)'L';
        h[3] = (byte)'F';
        h[4] = 1;
        h[5] = 1;
        h[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(h[16..], 0xFE04);
        BinaryPrimitives.WriteUInt16LittleEndian(h[18..], 0x28);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], InfoAt);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], 0x34);
        BinaryPrimitives.WriteUInt16LittleEndian(h[40..], 0x34);
        BinaryPrimitives.WriteUInt16LittleEndian(h[42..], 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(h[44..], 2);
        static void Ph(Span<byte> h, int at, uint type, uint offset, uint size, uint flags)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[at..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 4)..], offset);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 16)..], size);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 20)..], size);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 24)..], flags);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 28)..], 0x10);
        }

        Ph(h, 0x34, 1, SegOffset, Size, 7);
        Ph(h, 0x54, 0x60000000, (uint)(SegOffset + Size), (uint)rela.Length, 0);
        seg.CopyTo(elf, SegOffset);
        rela.CopyTo(elf, SegOffset + Size);
        return elf;
    }

    /// <summary>The address of function <paramref name="index"/> (Thumb bit set) in a module loaded at
    /// <paramref name="segmentBase"/>.</summary>
    public static uint FunctionAddress(uint segmentBase, int index) => segmentBase + (uint)(CodeAt + index * 0x10) + 1;
}
