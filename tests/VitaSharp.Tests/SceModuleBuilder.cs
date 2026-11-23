using System.Buffers.Binary;
using System.Text;

namespace VitaSharp.Tests;

/// <summary>
/// Builds a small relocatable SCE module (an ELF of type ET_SCE_RELEXEC with one segment and a PT_SCE_RELA stream) for
/// loader tests: Thumb functions that return a constant or call an import, A32 functions of any code, exported in
/// named libraries with chosen attributes, and imported libraries with their stubs. Everything is position independent except the table pointers,
/// which carry relocations, so the module loads anywhere (the kernel loader relocates its modules).
/// </summary>
public sealed class SceModuleBuilder(string name)
{
    private const uint NidModuleStart = 0x935CD196, NidModuleStop = 0x79F8E492;
    private const int CodeAt = 0x0, InfoAt = 0x400, ExportsAt = 0x500, ImportsAt = 0x700, StubsAt = 0x900, DataAt = 0xA00, ArmAt = 0xC00, Size = 0x1000;

    private readonly List<(string Name, uint Nid, ushort Attr, List<(uint Nid, int Function)> Functions)> _exports = [];
    private readonly List<(string Name, uint Nid, ushort Attr, uint[] Functions)> _imports = [];
    private readonly List<ushort[]> _code = [];
    private readonly Dictionary<int, (int Offset, uint[] Words)> _arm = [];
    private int _armUsed;
    private int _startFunction = -1;
    private int _stopFunction = -1;
    private uint _codeMemSize = Size;
    private uint _dataMemSize;
    private uint _moduleNid = 0x12345678;
    private readonly List<(int Offset, byte[] Bytes)> _payloads = [];

    /// <summary>module_info's module NID (+0x34; a firmware module's names its firmware build).</summary>
    public SceModuleBuilder ModuleNid(uint nid)
    {
        _moduleNid = nid;
        return this;
    }

    /// <summary>The module's segments as a firmware module has them: the code segment <paramref name="codeMemSize"/>
    /// bytes in memory (its file part stays the builder's), then a data segment of <paramref name="dataMemSize"/>
    /// zeroed bytes (none: 0).</summary>
    public SceModuleBuilder SegmentSizes(uint codeMemSize, uint dataMemSize)
    {
        _codeMemSize = Math.Max(codeMemSize, Size);
        _dataMemSize = dataMemSize;
        return this;
    }

    /// <summary>Data at <paramref name="segmentOffset"/> of the code segment, past the builder's own area (from 0x1000):
    /// the segment's file part grows to hold it, as a firmware module keeps its keys in its first segment.</summary>
    public SceModuleBuilder Payload(int segmentOffset, byte[] bytes)
    {
        if (segmentOffset < Size)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentOffset), "the builder's own area ends at 0x1000");
        }

        _payloads.Add((segmentOffset, bytes));
        return this;
    }

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

    /// <summary>An A32 function of <paramref name="words"/> (its literals included), placed in the module's A32 code
    /// area; returns its index (<see cref="AddressOf"/> gives its address).</summary>
    public int Arm(params uint[] words)
    {
        _code.Add([]);
        _arm[_code.Count - 1] = (ArmAt + _armUsed, words);
        _armUsed += words.Length * 4;
        if (ArmAt + _armUsed > Size)
        {
            throw new InvalidOperationException("A32 code area full");
        }

        return _code.Count - 1;
    }

    /// <summary>The address of function <paramref name="index"/> (Thumb bit set for a Thumb one) in a module loaded at
    /// <paramref name="segmentBase"/>.</summary>
    public uint AddressOf(uint segmentBase, int index) =>
        _arm.TryGetValue(index, out (int Offset, uint[] Words) arm) ? segmentBase + (uint)arm.Offset : FunctionAddress(segmentBase, index);

    /// <summary>module_start: the function returns this value (0 = SCE_KERNEL_START_SUCCESS).</summary>
    public SceModuleBuilder Start(byte status)
    {
        _startFunction = ReturnConstant(status);
        return this;
    }

    /// <summary>module_stop: function <paramref name="function"/> of the module (exported in the module's own library).</summary>
    public SceModuleBuilder Stop(int function)
    {
        _stopFunction = function;
        return this;
    }

    public SceModuleBuilder Export(string library, uint libNid, ushort attr, params (uint Nid, int Function)[] functions)
    {
        _exports.Add((library, libNid, attr, functions.ToList()));
        return this;
    }

    public SceModuleBuilder Import(string library, uint libNid, params uint[] functions)
    {
        _imports.Add((library, libNid, 0, functions));
        return this;
    }

    /// <summary>An imported library the module does without (SCE_LIBRARY_ATTR_WEAK_IMPORT, 0x0008).</summary>
    public SceModuleBuilder WeakImport(string library, uint libNid, params uint[] functions)
    {
        _imports.Add((library, libNid, 0x0008, functions));
        return this;
    }

    public byte[] Build()
    {
        int segmentSize = Size;
        foreach ((int Offset, byte[] Bytes) payload in _payloads)
        {
            segmentSize = Math.Max(segmentSize, (payload.Offset + payload.Bytes.Length + 0xFFF) & ~0xFFF);
        }

        byte[] seg = new byte[segmentSize];
        foreach ((int Offset, byte[] Bytes) payload in _payloads)
        {
            payload.Bytes.CopyTo(seg, payload.Offset);
        }

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
        var entryOf = new List<uint>();
        for (int i = 0; i < _code.Count; i++)
        {
            if (_arm.TryGetValue(i, out (int Offset, uint[] Words) arm))
            {
                functionAt.Add(arm.Offset);
                entryOf.Add((uint)arm.Offset);
                for (int k = 0; k < arm.Words.Length; k++)
                {
                    W32(arm.Offset + k * 4, arm.Words[k]);
                }

                continue;
            }

            int at = CodeAt + i * 0x10;
            functionAt.Add(at);
            entryOf.Add((uint)at | 1);
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
        if (_startFunction >= 0 || _stopFunction >= 0)
        {
            var own = new List<(uint Nid, uint Addr)>();
            if (_startFunction >= 0)
            {
                own.Add((NidModuleStart, entryOf[_startFunction]));
            }

            if (_stopFunction >= 0)
            {
                own.Add((NidModuleStop, entryOf[_stopFunction]));
            }

            libs.Add((null, 0, 0x8000, own));
        }

        foreach ((string Name, uint Nid, ushort Attr, List<(uint Nid, int Function)> Functions) e in _exports)
        {
            libs.Add((e.Name, e.Nid, e.Attr, e.Functions.Select(f => (f.Nid, entryOf[f.Function])).ToList()));
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
        foreach ((string Name, uint Nid, ushort Attr, uint[] Functions) lib in _imports)
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
            W16(imp0 + 4, lib.Attr);
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
        W32(InfoAt + 0x34, _moduleNid);
        W32(InfoAt + 0x44, _startFunction >= 0 ? entryOf[_startFunction] : 0);
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
        byte[] elf = new byte[SegOffset + segmentSize + rela.Length];
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
        BinaryPrimitives.WriteUInt16LittleEndian(h[44..], (ushort)(_dataMemSize != 0 ? 3 : 2));
        static void Ph(Span<byte> h, int at, uint type, uint offset, uint fileSize, uint memSize, uint flags)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[at..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 4)..], offset);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 16)..], fileSize);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 20)..], memSize);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 24)..], flags);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(at + 28)..], 0x10);
        }

        Ph(h, 0x34, 1, SegOffset, (uint)segmentSize, Math.Max(_codeMemSize, (uint)segmentSize), 7);
        int relaHeader = 0x54;
        if (_dataMemSize != 0)
        {
            Ph(h, 0x54, 1, (uint)(SegOffset + segmentSize), 0, _dataMemSize, 6);
            relaHeader = 0x74;
        }

        Ph(h, relaHeader, 0x60000000, (uint)(SegOffset + segmentSize), (uint)rela.Length, (uint)rela.Length, 0);

        seg.CopyTo(elf, SegOffset);
        rela.CopyTo(elf, SegOffset + segmentSize);
        return elf;
    }

    /// <summary>The address of function <paramref name="index"/> (Thumb bit set) in a module loaded at
    /// <paramref name="segmentBase"/>.</summary>
    public static uint FunctionAddress(uint segmentBase, int index) => segmentBase + (uint)(CodeAt + index * 0x10) + 1;
}
