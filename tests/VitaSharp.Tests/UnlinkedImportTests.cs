using VitaSharp.Core.Hle;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// An import no loaded module exports and FW 3.74 does not have: on a Vita 3.74 the module manager leaves its stub
/// "not linked" (sub r12, pc, #8; mov pc, #0) and a call jumps to 0. VitaSharp keeps the contract of returning 0 (its
/// NID list may miss a real function), but the first call is logged as an import without an exporter and listed in
/// the HLE summary that the F8 capture (hle_errors.txt) and the session report carry, so a report names the cause.
/// A firmware function the HLE does not implement is not such an import.
/// </summary>
[Collection("guest")]
public sealed class UnlinkedImportTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint MissingLibrary = 0x0BADF00D, MissingFunction = 0x0BADC0DE;

    private readonly HleHarness _harness = new(fixture, "unlinked");

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ACallOfAnImportWithoutAnExporterIsNamedAndStillReturnsZero()
    {
        Assert.Null(NidDatabase.Lookup(MissingFunction)); // not a function of the firmware
        SceModuleBuilder builder = new SceModuleBuilder("UnlinkedCaller").Import("SceMissingLibrary", MissingLibrary, MissingFunction);
        int caller = builder.CallImport(0, 0);
        string dir = Path.Combine(_harness.Root, "ux0", "data");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "unlinked.suprx"), builder.Build());
        LoadedModule module = _harness.Emu.LoadModule("ux0:data/unlinked.suprx");
        uint call = SceModuleBuilder.FunctionAddress(module.Segments[0].Address, caller);

        _harness.OnGuest(t => Assert.Equal(0u, t.CallGuest(call)));

        Assert.Equal("SceMissingLibrary", DebugMode.UnlinkedImports[MissingFunction]);
        string line = Assert.Single(DebugMode.HleSummary(), l => l.Contains("UNLINKED import: NID 0x0BADC0DE", StringComparison.Ordinal));
        Assert.Contains("library SceMissingLibrary", line);
        Assert.Contains("sub r12, pc, #8; mov pc, #0", line);
        Assert.False(DebugMode.UnlinkedImport(MissingFunction, "SceMissingLibrary", 0)); // once per NID
    }
}
