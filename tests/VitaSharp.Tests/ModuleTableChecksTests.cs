using VitaSharp.Core.Hle.Modules;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// FW 3.74 modulemgr checks the sizes of a module's library tables before linking it: every export entry is 0x20
/// bytes (else 0x8002D009) and the import entries are all 0x24 or all 0x34 bytes, the two formats (else 0x8002D00A).
/// Before, VitaSharp walked a table of any entry size.
/// </summary>
[Collection("guest")]
public sealed class ModuleTableChecksTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalExportTable = unchecked((int)0x8002D009), IllegalImportTable = unchecked((int)0x8002D00A);

    /// <summary>Where SceModuleBuilder puts the first export and import entries in its ELF file (segment at 0x100).</summary>
    private const int FirstExport = 0x100 + 0x500, FirstImport = 0x100 + 0x700;

    private readonly HleHarness _harness = new(fixture, "modtables");

    public void Dispose() => _harness.Dispose();

    private int Load(string file, byte[] elf)
    {
        string dir = Path.Combine(_harness.Root, "ux0", "data");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), elf);
        return Call(_harness.Module<SystemMisc>(), "sceKernelLoadModule", "ux0:data/" + file, 0u, 0u);
    }

    private static byte[] Module(string name)
    {
        var builder = new SceModuleBuilder(name);
        int function = builder.ReturnConstant(1);
        return builder.Start(0).Export("SceTableLib", 0x7AB1E001, 0x0001, (0x7AB1EF01, function))
            .Import("SceTableImports", 0x7AB1E002, 0x7AB1EF02).Build();
    }

    [Fact]
    public void ExportAndImportEntriesHaveTheFirmwaresSizes()
    {
        Assert.True(Load("good.suprx", Module("TablesGood")) > 0);

        byte[] badExport = Module("TablesBadExport");
        Assert.Equal(0x20, badExport[FirstExport]);
        badExport[FirstExport] = 0x1C;
        Assert.Equal(IllegalExportTable, Load("badexport.suprx", badExport));

        byte[] badImport = Module("TablesBadImport");
        Assert.True(badImport[FirstImport] is 0x24 or 0x34);
        badImport[FirstImport] = 0x28;
        Assert.Equal(IllegalImportTable, Load("badimport.suprx", badImport));
    }
}
