using VitaSharp.Core;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using VitaSharp.Core.Util;
using Xunit;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// The libraries the coverage counts as "LLE" (<see cref="LleLibraries"/>): the real module the program brings loads
/// under VitaSharp, exports the library's functions and one of them runs. The modules come from an installed data
/// folder named by VITASHARP_TEST_INSTALLED_DATA (read only, never the user's own data folder by default): a game that
/// ships libsmart/libface (decrypted with its work.bin or licence) and the firmware's livespace_db, which can also come
/// from the test firmware (<see cref="JsonFirmware"/>); without them the tests are skipped and say what is missing.
/// </summary>
[Collection("guest")]
public sealed class LleLibrariesTests(GuestMemoryFixture fixture, ITestOutputHelper output)
{
    /// <summary>Where the modules are looked for, for the messages: the installed data folder, else the test firmware.</summary>
    private static string Sources => TestDataIsolation.InstalledFsRoot ?? FirmwareFsRoot ?? "(none)";

    /// <summary>The file-system root (the folder holding vs0/) of the test firmware, or null.</summary>
    internal static string? FirmwareFsRoot
    {
        get
        {
            // <root>/vs0/sys/external/libSceJson.suprx
            string? external = JsonFirmware.ModulePath is { } json ? Path.GetDirectoryName(json) : null;
            DirectoryInfo? vs0 = external != null ? Directory.GetParent(external)?.Parent : null;
            return vs0 != null && vs0.Name.Equals("vs0", StringComparison.OrdinalIgnoreCase) ? vs0.Parent?.FullName : null;
        }
    }

    /// <summary>The module file and the key to decrypt it (null for a module that needs none): the first installed
    /// module that really exports <paramref name="library"/> (a game's app0:sce_module copy or the firmware module of
    /// the installed data folder, else the test firmware's), found the way the coverage scan finds them.</summary>
    internal static (string Path, string VitaPath, byte[]? Klicensee)? Find(string library)
    {
        IEnumerable<LleLibraries.InstalledModule> modules = [];
        if (TestDataIsolation.InstalledFsRoot is { } fsRoot)
        {
            modules = LleLibraries.GameModules(fsRoot).Concat(LleLibraries.DataModules(fsRoot)).Concat(LleLibraries.FirmwareModules(fsRoot, l => l == library));
        }

        if (FirmwareFsRoot is { } firmware)
        {
            modules = modules.Concat(LleLibraries.FirmwareModules(firmware, l => l == library));
        }

        foreach ((LleLibraries.InstalledModule? m, string _, IReadOnlyList<uint> _) in LleLibraries.Exports(modules, (_, l) => l == library))
        {
            return (m.HostPath, m.VitaPath, m.Klicensee);
        }

        return null;
    }

    [InstalledDataTheory]
    [InlineData("SceSmart", "sceSmartRelease")]
    [InlineData("SceFace", "sceFaceDetectionGetWorkingMemorySize")]
    [InlineData("SceShaccCg", "sceShaccCgGetVersionString")]
    [InlineData("SceLsdb", "sceLsdbAppDatabaseInit")]
    public void RealModuleLoadsAndRuns(string library, string export)
    {
        (string Path, string VitaPath, byte[]? Klicensee)? found = Find(library);
        if (found == null)
        {
            output.WriteLine($"{library}: no installed module exporting it under {Sources}; nothing to check");
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-lle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var emu = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
            byte[] elf = SelfDecryptor.ToElf(File.ReadAllBytes(found.Value.Path), found.Value.Klicensee);
            LoadedModule m = ModuleLoader.Load(elf, found.Value.VitaPath, emu.Memory, emu.Linker, 0);
            emu.Code.Invalidate(0, uint.MaxValue);
            // every NID of the library the module exports is bound by the linker
            var nids = NidDatabase.All.Where(n => n.Library == library).ToList();
            int exported = nids.Count(n => emu.Linker.TryGetFunctionExport(n.Nid, out _));
            output.WriteLine($"{library}: {m.Name} exports {exported}/{nids.Count} of the library's NIDs");
            Assert.True(exported > 0, $"{m.Name} exports none of {library}");
            uint nid = NidDatabase.LookupName(export).Single(n => n.Library == library).Nid;
            Assert.True(emu.Linker.TryGetFunctionExport(nid, out uint addr), $"{export} is not exported");
            uint buf = emu.Memory.Alloc(0x1000, "lle test buffer");
            GuestMemory.Fill(buf, 0, 0x1000);
            uint result = 0;
            Exception? failure = null;
            GuestThread t = emu.Kernel.CreateThread("lle-test", 0, 0x10000100, 0x10000, 0, 0x70000);
            t.HostBody = th =>
            {
                try
                {
                    result = th.CallGuest(addr, buf);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            };
            t.Start(0, 0);
            Assert.True(t.WaitForExit(60_000), $"{export} did not return");
            emu.Kernel.DeleteThread(t);
            Assert.Null(failure);
            output.WriteLine($"{export} -> 0x{result:X8}");
            if (export == "sceShaccCgGetVersionString")
            {
                Assert.False(string.IsNullOrEmpty(GuestMemory.ReadCString(result)));
            }

            if (export == "sceLsdbAppDatabaseInit")
            {
                Assert.Equal(buf, result);
                Assert.NotEqual(0u, GuestMemory.R32(buf));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }

    /// <summary>A program that is not the shell importing SceLsdb gets the firmware's livespace_db.suprx through the
    /// firmware dependency resolution (although the HLE has some of the library), and its imports bind to the module.</summary>
    [InstalledFirmwareFact]
    public void FirmwareLleLibraryLoadsForAnyImporter()
    {
        (string Path, string VitaPath, byte[]? Klicensee)? found = Find("SceLsdb");
        if (found == null)
        {
            output.WriteLine($"livespace_db.suprx not installed under {Sources}; nothing to check");
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-lle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "vs0", "vsh", "shell"));
        File.Copy(found.Value.Path, Path.Combine(root, "vs0", "vsh", "shell", "livespace_db.suprx"));
        try
        {
            using var emu = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
            FirmwareLibraries.Module? lib = VitaSharp.Core.Firmware.FirmwareLibraries.For(root).Provider("SceLsdb");
            Assert.NotNull(lib);
            uint libNid = lib!.Libraries.Single(l => l.Library == "SceLsdb").LibNid;
            uint[] nids = new[] { "sceLsdbAppDatabaseInit", "sceLsdbGetDatabaseLanguageIndex", "sceLsdbOpen" }
                .Select(n => NidDatabase.LookupName(n).Single(i => i.Library == "SceLsdb").Nid).ToArray();
            uint seg = emu.Memory.Alloc(0x100, "lle importer");
            var importer = new LoadedModule { Name = "homebrew", Path = "app0:eboot.bin", Segments = { [0] = new SegmentInfo(seg, 0, 0x100) } };
            importer.Imports.Add(("SceLsdb", libNid, nids));
            for (int i = 0; i < nids.Length; i++)
            {
                emu.Linker.BindFunctionImport(nids[i], libNid, "SceLsdb", seg + (uint)i * 0x10, importer.Segments);
            }

            List<LoadedModule> loaded = emu.LoadFirmwareDependencies(importer);
            Assert.Contains(loaded, m => m.Path.Equals("vs0:vsh/shell/livespace_db.suprx", StringComparison.OrdinalIgnoreCase));
            foreach (uint nid in nids)
            {
                Assert.True(emu.Linker.TryGetFunctionExport(nid, out _));
            }
            // the importer's stub now jumps into the module instead of trapping into the HLE
            Assert.NotEqual(0xEF000000u, GuestMemory.R32(seg) & 0xFF000000u);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }
}

/// <summary>Skipped without <see cref="EnvVars.TestInstalledData"/>: the tests of modules that games ship need an
/// installed data folder, which the suite never takes from the user's own.</summary>
public sealed class InstalledDataTheoryAttribute : TheoryAttribute
{
    public InstalledDataTheoryAttribute()
    {
        if (TestDataIsolation.InstalledData == null)
        {
            Skip = $"no installed data folder (set {EnvVars.TestInstalledData} to a VitaSharp data folder with installed games, read only)";
        }
    }
}

/// <summary>Skipped without an installed data folder and without the test firmware.</summary>
public sealed class InstalledFirmwareFactAttribute : FactAttribute
{
    public InstalledFirmwareFactAttribute()
    {
        if (TestDataIsolation.InstalledData == null && LleLibrariesTests.FirmwareFsRoot == null)
        {
            Skip = $"no firmware (set {EnvVars.TestInstalledData} to a VitaSharp data folder, or {EnvVars.FirmwareDir} to a Vita3K data folder or vs0, or {EnvVars.FirmwarePup})";
        }
    }
}
