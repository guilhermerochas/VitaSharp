using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The library database of the kernel module manager (modulemgr 3.74): a module's export entries marked AUTO_EXPORT
/// are registered when it loads and the others are not; ksceKernelRegisterLibary / ksceKernelReleaseLibary add or take
/// out one entry (named by its address in the module's export table), and the importers follow (bound to the library
/// while it is registered, back to the HLE when it goes). A library a module imports without the weak attribute
/// cannot go.
/// </summary>
[Collection("guest")]
public sealed class KernelLibraryRegistrationTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint AutoLib = 0x5EC5B001, ManualLib = 0x5EC5B002, ImporterLib = 0x5EC5B003;
    private const uint AutoFn = 0x51111111, ManualFn = 0x52222222, ImporterFn = 0x53333333;
    private const int NotAnEntry = unchecked((int)0x8002D009);
    private const int AlreadyRegistered = unchecked((int)0x8002D013);
    private const int NotRegistered = unchecked((int)0x8002D081);
    private const int InUse = unchecked((int)0x8002D002);
    private const int NoModule = unchecked((int)0x8002D082);

    private readonly HleHarness _harness = new(fixture, "kernlib");

    private ModulemgrKernel Modmgr => _harness.Module<ModulemgrKernel>();

    public void Dispose() => _harness.Dispose();

    private string Write(string file, byte[] elf)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), elf);
        return "ur0:tai/" + file;
    }

    /// <summary>The provider: entry 0 its main entry, entry 1 an AUTO_EXPORT library, entry 2 a library without
    /// it.</summary>
    private static byte[] Provider()
    {
        SceModuleBuilder builder = new SceModuleBuilder("KLibProvider").Start(0);
        int five = builder.ReturnConstant(5);
        int seven = builder.ReturnConstant(7);
        return builder
            .Export("AutoLib", AutoLib, 0x0001, (AutoFn, five))
            .Export("ManualLib", ManualLib, 0x0000, (ManualFn, seven))
            .Build();
    }

    /// <summary>The importer: the AUTO_EXPORT library imported as usual, the other one weak (or not).</summary>
    private static byte[] Importer(bool weak)
    {
        SceModuleBuilder builder = new SceModuleBuilder(weak ? "KLibWeak" : "KLibStrong").Start(0);
        builder.Import("AutoLib", AutoLib, AutoFn);
        if (weak)
        {
            builder.WeakImport("ManualLib", ManualLib, ManualFn);
        }
        else
        {
            builder.Import("ManualLib", ManualLib, ManualFn);
        }

        int call = builder.CallImport(1, 0);
        return builder.Export("ImporterLib", ImporterLib, 0x0001, (ImporterFn, call)).Build();
    }

    private LoadedModule Load(string path)
    {
        uint status = _harness.Word(0xFF);
        int uid = Call(Modmgr, "ksceKernelLoadStartModule", path, 0u, 0u, 0, 0u, status);
        Assert.True(uid > 0, $"load {path}: 0x{uid:X8}");
        return KernelModules(_harness.Emu)[uid];
    }

    private Linker Linker => KernelLinker(_harness.Emu);

    /// <summary>The address the single import stub of <paramref name="nid"/> jumps to (0 = an HLE stub).</summary>
    private uint StubTarget(uint nid, uint libNid)
    {
        List<uint> stubs = Linker.BoundStubs(nid, libNid, true);
        Assert.Single(stubs);
        uint w0 = GuestMemory.R32(stubs[0]);
        uint w1 = GuestMemory.R32(stubs[0] + 4);
        if ((w0 & 0xFFF0F000) != 0xE300C000 || (w1 & 0xFFF0F000) != 0xE340C000)
        {
            return 0;
        }

        return ((w0 >> 4) & 0xF000) | (w0 & 0xFFF) | ((((w1 >> 4) & 0xF000) | (w1 & 0xFFF)) << 16);
    }

    [Fact]
    public void OnlyAutoExportLibrariesAreRegisteredAtLoad()
    {
        string path = Write("klibprov.skprx", Provider());
        _harness.OnGuest(guestThread =>
        {
            LoadedModule provider = Load(path);
            Assert.True(Linker.TryGetFunctionExport(AutoLib, AutoFn, out _));
            Assert.False(Linker.TryGetFunctionExport(ManualLib, ManualFn, out _));
            Assert.Equal(3, provider.ExportLibraries.Count);
            Assert.False(provider.ExportLibraries[0].Registered); // the main entry is no library
            Assert.True(provider.ExportLibraries[1].Registered);
            Assert.False(provider.ExportLibraries[2].Registered);
        });
    }

    [Fact]
    public void TheEntryIsNamedByItsAddressInTheExportTable()
    {
        string path = Write("klibprov.skprx", Provider());
        _harness.OnGuest(guestThread =>
        {
            LoadedModule provider = Load(path);
            uint table = provider.ExportsStart;
            Assert.Equal(NoModule, Call(Modmgr, "ksceKernelRegisterLibary", _harness.Alloc(0x20)));
            Assert.Equal(NoModule, Call(Modmgr, "ksceKernelReleaseLibary", 0u));
            Assert.Equal(NotAnEntry, Call(Modmgr, "ksceKernelRegisterLibary", table + 4));
            Assert.Equal(NotAnEntry, Call(Modmgr, "ksceKernelRegisterLibary", table - 0x20));
            Assert.Equal(NotAnEntry, Call(Modmgr, "ksceKernelReleaseLibary", provider.ExportsEnd));

            // The main entry: no library, both calls answer 0 and change nothing.
            Assert.Equal(0, Call(Modmgr, "ksceKernelRegisterLibary", table));
            Assert.Equal(0, Call(Modmgr, "ksceKernelReleaseLibary", table));
            Assert.False(provider.ExportLibraries[0].Registered);

            // A library not in the database cannot be released; one in it cannot be registered again.
            Assert.Equal(NotRegistered, Call(Modmgr, "ksceKernelReleaseLibary", table + 0x40));
            Assert.Equal(AlreadyRegistered, Call(Modmgr, "ksceKernelRegisterLibary", table + 0x20));
            Assert.Equal(0, Call(Modmgr, "ksceKernelRegisterLibary", table + 0x40));
            Assert.True(Linker.TryGetFunctionExport(ManualLib, ManualFn, out _));
            Assert.Equal(AlreadyRegistered, Call(Modmgr, "ksceKernelRegisterLibary", table + 0x40));
            Assert.Equal(0, Call(Modmgr, "ksceKernelReleaseLibary", table + 0x40));
            Assert.False(Linker.TryGetFunctionExport(ManualLib, ManualFn, out _));
            Assert.Equal(NotRegistered, Call(Modmgr, "ksceKernelReleaseLibary", table + 0x40));
        });
    }

    [Fact]
    public void ImportersFollowTheLibraryAndStrongImportersKeepIt()
    {
        string providerPath = Write("klibprov.skprx", Provider());
        string weakPath = Write("klibweak.skprx", Importer(weak: true));
        _harness.OnGuest(thread =>
        {
            LoadedModule provider = Load(providerPath);
            uint table = provider.ExportsStart;
            Load(weakPath);

            // Not registered: the weak import is the HLE's.
            Assert.Equal(0u, StubTarget(ManualFn, ManualLib));
            Assert.Equal(0, Call(Modmgr, "ksceKernelRegisterLibary", table + 0x40));
            Assert.True(Linker.TryGetFunctionExport(ManualLib, ManualFn, out uint manual));
            Assert.Equal(manual, StubTarget(ManualFn, ManualLib));
            Assert.True(Linker.TryGetFunctionExport(ImporterLib, ImporterFn, out uint importer));
            Assert.Equal(7u, thread.CallGuest(importer));

            // The importer takes AutoLib without the weak attribute: it stays. ManualLib (weak) goes, and the stub
            // falls back to the HLE.
            Assert.Equal(InUse, Call(Modmgr, "ksceKernelReleaseLibary", table + 0x20));
            Assert.True(Linker.TryGetFunctionExport(AutoLib, AutoFn, out _));
            Assert.Equal(0, Call(Modmgr, "ksceKernelReleaseLibary", table + 0x40));
            Assert.Equal(0u, StubTarget(ManualFn, ManualLib));

            Assert.Equal(0, Call(Modmgr, "ksceKernelRegisterLibary", table + 0x40));
            Assert.Equal(manual, StubTarget(ManualFn, ManualLib));
        });
    }

    [Fact]
    public void AStrongImporterKeepsEvenTheLibraryRegisteredLater()
    {
        string providerPath = Write("klibprov.skprx", Provider());
        string strongPath = Write("klibstrong.skprx", Importer(weak: false));
        _harness.OnGuest(guestThread =>
        {
            LoadedModule provider = Load(providerPath);
            uint table = provider.ExportsStart;
            Load(strongPath);
            Assert.Equal(0, Call(Modmgr, "ksceKernelRegisterLibary", table + 0x40));
            Assert.Equal(InUse, Call(Modmgr, "ksceKernelReleaseLibary", table + 0x40));
            Assert.True(Linker.TryGetFunctionExport(ManualLib, ManualFn, out _));
        });
    }
}
