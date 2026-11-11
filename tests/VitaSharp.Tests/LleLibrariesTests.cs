using VitaSharp.Core;
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
/// under VitaSharp, exports the library's functions and one of them runs. The modules come from the installed data
/// (read only: VITASHARP_DATA or %APPDATA%\VitaSharp): a game that ships libsmart/libface (decrypted with its
/// work.bin or licence) and the firmware's livespace_db; without them the test is skipped and says what is missing.
/// </summary>
[Collection("guest")]
public sealed class LleLibrariesTests(GuestMemoryFixture fx, ITestOutputHelper output)
{
    static string FsRoot => Path.Combine(Environment.GetEnvironmentVariable(EnvVars.Data) is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VitaSharp"), "fs");

    /// <summary>The module file and the key to decrypt it (null for a module that needs none).</summary>
    /// <summary>The first installed module that really exports <paramref name="library"/> (a game's app0:sce_module copy
    /// or the firmware module), found the way the coverage scan finds them.</summary>
    internal static (string Path, string VitaPath, byte[]? Klicensee)? Find(string library)
    {
        var modules = LleLibraries.GameModules(FsRoot).Concat(LleLibraries.DataModules(FsRoot)).Concat(LleLibraries.FirmwareModules(FsRoot, l => l == library));
        foreach (var (m, _, _) in LleLibraries.Exports(modules, (_, l) => l == library))
        {
            return (m.HostPath, m.VitaPath, m.Klicensee);
        }

        return null;
    }

    [Theory]
    [InlineData("SceSmart", "sceSmartRelease")]
    [InlineData("SceFace", "sceFaceDetectionGetWorkingMemorySize")]
    [InlineData("SceShaccCg", "sceShaccCgGetVersionString")]
    [InlineData("SceLsdb", "sceLsdbAppDatabaseInit")]
    public void RealModuleLoadsAndRuns(string library, string export)
    {
        var found = Find(library);
        if (found == null)
        {
            output.WriteLine($"{library}: no installed module exporting it under {FsRoot}; nothing to check");
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-lle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var emu = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
            var elf = SelfDecryptor.ToElf(File.ReadAllBytes(found.Value.Path), found.Value.Klicensee);
            var m = ModuleLoader.Load(elf, found.Value.VitaPath, emu.Memory, emu.Linker, 0);
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
            var t = emu.Kernel.CreateThread("lle-test", 0, 0x10000100, 0x10000, 0, 0x70000);
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
    [Fact]
    public void FirmwareLleLibraryLoadsForAnyImporter()
    {
        var found = Find("SceLsdb");
        if (found == null)
        {
            output.WriteLine($"livespace_db.suprx not installed under {FsRoot}; nothing to check");
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-lle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "vs0", "vsh", "shell"));
        File.Copy(found.Value.Path, Path.Combine(root, "vs0", "vsh", "shell", "livespace_db.suprx"));
        try
        {
            using var emu = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
            var lib = VitaSharp.Core.Firmware.FirmwareLibraries.For(root).Provider("SceLsdb");
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

            var loaded = emu.LoadFirmwareDependencies(importer);
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
