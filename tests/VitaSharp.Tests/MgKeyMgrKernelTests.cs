using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblMgKeyMgrForDriver (mgkeymgr 3.74): the module's NULL checks (0x800F1616) before the secure processor, which
/// the emulated unit lacks (0x80020004, defaults), and NID_B6AC3AE1's own answer (0x800F1625).
/// </summary>
[Collection("guest")]
public sealed class MgKeyMgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x800F1616), Unsupported = unchecked((int)0x800F1625);
    private const int NoSecure = unchecked((int)0x80020004);

    private readonly HleHarness _harness = new(fixture, "mgkeyk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    public static TheoryData<uint, string, int> Secure => new()
    {
        { 0x20B66ADAu, "MgKeyDecryptWithKey", 3 },
        { 0x5FECCE03u, "MgKeyEncryptWithKey", 3 },
        { 0xEA534495u, "MgKeyCombineEncrypt", 4 },
        { 0xDF3B808Du, "MgKeyCombineDecrypt", 4 },
        { 0xBC7D86EEu, "MgKeyBc7d", 3 },
        { 0x4C82A466u, "MgKey4c82", 3 },
        { 0xD37C3DAEu, "MgKeyD37c", 3 },
        { 0x8CFCAA52u, "MgKey8cfc", 3 },
        { 0xF8F856FCu, "MgKeyF8f8", 3 },
        { 0x8ECFFD7Au, "MgKey8ecf", 3 },
        { 0x71FA16B0u, "MgKey71fa", 2 },
        { 0x1C5388D0u, "MgKey1c53", 2 },
        { 0x1263FB04u, "MgKey1263", 2 },
    };

    [Theory]
    [MemberData(nameof(Secure))]
    public void EachSecureEntryChecksItsPointersFirst(uint nid, string name, int arguments)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceSblMgKeyMgrForDriver").ToList();
        Assert.Single(bindings);
        Assert.True(bindings[0].Default);

        uint buffer = _harness.Alloc(16);
        for (int nulled = 0; nulled < arguments; nulled++)
        {
            object[] args = Enumerable.Repeat<object>(buffer, arguments).ToArray();
            args[nulled] = 0u;
            Assert.Equal(Invalid, Call(Services, name, args));
        }

        Assert.Equal(NoSecure, Call(Services, name, Enumerable.Repeat<object>(buffer, arguments).ToArray()));
    }

    [Fact]
    public void TheModuleAnswersB6ac3ae1Itself()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == 0xB6AC3AE1u && b.Library == "SceSblMgKeyMgrForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
        uint buffer = _harness.Alloc(16);
        Assert.Equal(Invalid, Call(Services, "MgKeyUnsupported", 0u, buffer));
        Assert.Equal(Invalid, Call(Services, "MgKeyUnsupported", buffer, 0u));
        Assert.Equal(Unsupported, Call(Services, "MgKeyUnsupported", buffer, buffer));
    }
}
