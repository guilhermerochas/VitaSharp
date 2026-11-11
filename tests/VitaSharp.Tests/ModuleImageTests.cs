using System.Buffers.Binary;
using VitaSharp.Core.Loader;

namespace VitaSharp.Tests;

/// <summary>The library tables of a module read from its ELF without loading it (ModuleImage): the module_info name,
/// the exported libraries (functions and variables) and the imported ones, from a relocatable module linked at 0 and
/// from a fixed-address executable (ET_SCE_EXEC, linked at 0x81000000, whose pointers are link addresses).</summary>
public sealed class ModuleImageTests
{
    /// <summary>One PT_LOAD segment at <paramref name="vaddr"/>: module_info at +0, an export entry at +0x40, an import
    /// entry (0x34 bytes) at +0x60, the library names and NID tables after them.</summary>
    private static byte[] Elf(ushort type, uint vaddr)
    {
        byte[] d = new byte[0x200];
        void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o), v);
        void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        W32(0, 0x464C457F);
        W16(16, type);
        W16(18, 0x28);
        W32(24, 0); // module_info: segment 0, offset 0
        W32(28, 52);
        W16(42, 32);
        W16(44, 1);
        W32(52, 1);
        W32(56, 0x100);
        W32(60, vaddr);
        W32(64, vaddr);
        W32(68, 0x100);
        W32(72, 0x100);
        const int SegmentOffset = 0x100;
        W16(SegmentOffset, 0x0007);
        "SceTestModule"u8.CopyTo(d.AsSpan(SegmentOffset + 4));
        W32(SegmentOffset + 0x24, 0x40);
        W32(SegmentOffset + 0x28, 0x60);
        W32(SegmentOffset + 0x2C, 0x60);
        W32(SegmentOffset + 0x30, 0x94);
        // export: 2 functions, 1 variable
        W16(SegmentOffset + 0x40, 0x20);
        W16(SegmentOffset + 0x44, 0x4001);
        W16(SegmentOffset + 0x46, 2);
        W32(SegmentOffset + 0x48, 1);
        W32(SegmentOffset + 0x50, 0xAABBCCDD);
        W32(SegmentOffset + 0x54, vaddr + 0xA0);
        W32(SegmentOffset + 0x58, vaddr + 0xC0);
        // import (the 0x34-byte form): 2 functions
        W16(SegmentOffset + 0x60, 0x34);
        W16(SegmentOffset + 0x66, 2);
        W32(SegmentOffset + 0x70, 0x11223344);
        W32(SegmentOffset + 0x74, vaddr + 0xB0);
        W32(SegmentOffset + 0x7C, vaddr + 0xD0);
        "SceTestLib"u8.CopyTo(d.AsSpan(SegmentOffset + 0xA0));
        "SceImported"u8.CopyTo(d.AsSpan(SegmentOffset + 0xB0));
        W32(SegmentOffset + 0xC0, 0x1001);
        W32(SegmentOffset + 0xC4, 0x1002);
        W32(SegmentOffset + 0xC8, 0x2001);
        W32(SegmentOffset + 0xD0, 0x3001);
        W32(SegmentOffset + 0xD4, 0x3002);
        return d;
    }

    [Theory]
    [InlineData((ushort)0xFFA5, 0u)]          // ET_SCE_RELEXEC linked at 0
    [InlineData((ushort)0xFE00, 0x81000000u)] // ET_SCE_EXEC at its fixed address
    public void ReadsTheLibraryTables(ushort type, uint vaddr)
    {
        var m = ModuleImage.Read(Elf(type, vaddr));
        Assert.Equal("SceTestModule", m.Name);
        Assert.Equal(7, m.Attributes);
        (string Library, uint LibNid, ushort Attributes, uint[] FuncNids, uint[] VarNids) x = Assert.Single(m.Exports);
        Assert.Equal("SceTestLib", x.Library);
        Assert.Equal(0xAABBCCDDu, x.LibNid);
        Assert.Equal((ushort)0x4001, x.Attributes);
        Assert.Equal(new uint[] { 0x1001, 0x1002 }, x.FuncNids);
        Assert.Equal(new uint[] { 0x2001 }, x.VarNids);
        (string Library, uint LibNid, uint[] FuncNids) i = Assert.Single(m.Imports);
        Assert.Equal("SceImported", i.Library);
        Assert.Equal(0x11223344u, i.LibNid);
        Assert.Equal(new uint[] { 0x3001, 0x3002 }, i.FuncNids);
    }
}
