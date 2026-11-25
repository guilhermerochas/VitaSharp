using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// FW 3.74 modulemgr checks a user module's import entries before it links each library it finds (FUN_81008aa0 ->
/// FUN_81005100): every function stub lies whole in a segment with permissions R+X (5) and every variable entry in a
/// segment without write permission; otherwise the load fails with 0x8002D01E. A library it does not find gets the
/// check made and ignored, and a kernel module (the kernel process, 0x10005) is not checked. Before, VitaSharp linked
/// stubs wherever they were.
/// </summary>
[Collection("guest")]
public sealed class ImportEntryCheckTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalExport = unchecked((int)0x8002D01E);

    /// <summary>A firmware library and one of its functions (SceLibKernel sceIoOpen).</summary>
    private const uint SceLibKernelNid = 0xCAE9ACE6, IoOpenNid = 0x6C60AC61;

    private const uint ReadWrite = 6, ReadWriteExecute = 7;

    private readonly HleHarness _harness = new(fixture, "importcheck");

    public void Dispose() => _harness.Dispose();

    private string Write(string folder, string file, SceModuleBuilder builder)
    {
        string dir = Path.Combine(_harness.Root, "ux0", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), builder.Build());
        return $"ux0:{folder}/{file}";
    }

    private static SceModuleBuilder Importer(string name, uint codeFlags, string library = "SceLibKernel", uint libNid = SceLibKernelNid)
    {
        var builder = new SceModuleBuilder(name);
        builder.ReturnConstant(0);
        return builder.Start(0).CodeSegmentFlags(codeFlags).Import(library, libNid, IoOpenNid);
    }

    private int LoadUser(string path) => Call(_harness.Module<SystemMisc>(), "sceKernelLoadModule", path, 0u, 0u);

    [Fact]
    public void AUserModulesImportStubsMustBeInAReadExecuteSegment()
    {
        // the text as a module has it (R+X): loads
        Assert.True(LoadUser(Write("data", "rx.suprx", Importer("ImportRx", 5))) > 0);

        // stubs in a writable segment, or in one that is also writable (RWX): refused
        Assert.Equal(IllegalExport, LoadUser(Write("data", "rw.suprx", Importer("ImportRw", ReadWrite))));
        Assert.Equal(IllegalExport, LoadUser(Write("data", "rwx.suprx", Importer("ImportRwx", ReadWriteExecute))));
    }

    /// <summary>
    /// The read-only census of installed modules (VitaSharp.Desktop import-check) answers as the loader: over a data
    /// folder with a title (eboot.bin R+X, a module with its stubs in a writable segment), an update and a user plugin,
    /// it counts each module, passes the ones the loader loads and names the one it refuses, with the stub.
    /// </summary>
    [Fact]
    public void TheCensusOfInstalledModulesAnswersAsTheLoader()
    {
        string fs = Path.Combine(_harness.Root, "census-fs");
        string app = Path.Combine(fs, "ux0", "app", "TEST00001");
        Directory.CreateDirectory(Path.Combine(app, "sce_module"));
        File.WriteAllBytes(Path.Combine(app, "eboot.bin"), Importer("CensusMain", 5).Build());
        File.WriteAllBytes(Path.Combine(app, "sce_module", "bad.suprx"), Importer("CensusBad", ReadWrite).Build());
        string patch = Path.Combine(fs, "ux0", "patch", "TEST00001");
        Directory.CreateDirectory(patch);
        File.WriteAllBytes(Path.Combine(patch, "eboot.bin"), Importer("CensusPatch", 5).Build());
        string tai = Path.Combine(fs, "ur0", "tai");
        Directory.CreateDirectory(tai);
        File.WriteAllBytes(Path.Combine(tai, "plugin.suprx"), Importer("CensusPlugin", 5).Build());
        File.WriteAllBytes(Path.Combine(tai, "unknown.suprx"), Importer("CensusUnknown", ReadWrite, "NoSuchLibrary", 0x0BADC0DE).Build());

        var census = Core.Firmware.ImportEntryCensus.Scan(fs);

        Assert.Equal(5, census.Modules);
        Assert.Equal(4, census.Passed);
        (string module, string problem) = Assert.Single(census.Refused);
        Assert.Equal("ux0/app/TEST00001/sce_module/bad.suprx", module);
        Assert.Contains("SceLibKernel", problem);
        Assert.Empty(census.Unreadable);

        // the loader refuses the same module
        string loaded = Write("data", "census-bad.suprx", Importer("CensusBad2", ReadWrite));
        Assert.Equal(IllegalExport, LoadUser(loaded));
    }

    [Fact]
    public void ALibraryNotFoundAndAKernelModuleAreNotChecked()
    {
        // no module exports it and the firmware has no such library: the check is ignored
        Assert.True(LoadUser(Write("data", "unknown.suprx", Importer("ImportUnknown", ReadWrite, "NoSuchLibrary", 0x0BADC0DE))) > 0);

        // a kernel module: the kernel process's modules are not checked
        LoadedModule module = _harness.Emu.LoadModule(Write("tai", "kernel.skprx", Importer("ImportKernel", ReadWrite)), kernel: true);
        Assert.NotNull(module);
    }
}
