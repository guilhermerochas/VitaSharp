using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblPostSsMgrForDriver as SceSblPostSsMgr 3.74 answers on a retail unit: the argument checks of every secure
/// service before "not supported" (0x80020004, never a key or a valid signature), the clear utoken flags, the licence
/// that does not expire, no communication processor, and the signed param.sfo read into a kernel block.
/// </summary>
[Collection("guest")]
public sealed class PostSsMgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Unsupported = unchecked((int)0x80020004), Invalid = unchecked((int)0x800F0016);
    private const int Magic = unchecked((int)0x800F0024), Version = unchecked((int)0x800F0037);
    private const int SsInvalid = unchecked((int)0x800F1716), NotInitialized = unchecked((int)0x800F1727);

    private readonly HleHarness _harness = new(fixture, "postssk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private uint Bytes(ReadOnlySpan<byte> data, uint size = 0x100)
    {
        uint address = _harness.Alloc(size);
        data.CopyTo(GuestMemory.Span(address, data.Length));
        return address;
    }

    [Fact]
    public void Sealed_keys_check_their_header_then_need_the_secure_processor()
    {
        uint output = _harness.Alloc(0x20);
        Assert.Equal(Invalid, Call(Services, "ksceSblPostSsMgrEncryptSealedkey", 0u));
        Assert.Equal(Unsupported, Call(Services, "ksceSblPostSsMgrEncryptSealedkey", output));
        Assert.Equal(Invalid, Call(Services, "ksceSblPostSsMgrDecryptSealedkey", 0u, output));
        Assert.Equal(Magic, Call(Services, "ksceSblPostSsMgrDecryptSealedkey", Bytes("pfsXXKey"u8), output));
        Assert.Equal(Version, Call(Services, "ksceSblPostSsMgrDecryptSealedkey", Bytes("pfsSKKey\x03\x00"u8), output));
        Assert.Equal(Unsupported, Call(Services, "ksceSblPostSsMgrDecryptSealedkey", Bytes("pfsSKKey\x02\x00"u8), output));
        Assert.Equal(new byte[0x20], GuestMemory.ReadSpan(output, 0x20).ToArray());
        Assert.Equal(Version, Call(Services, "ksceSblPostSsMgrDebugDecryptKeystone", Bytes("keystone\x02\x00"u8), output));
        Assert.Equal(Unsupported, Call(Services, "ksceSblPostSsMgrDebugDecryptKeystone", Bytes("keystone\x01\x00"u8), output));
        uint keystone = Bytes("keystone\x01\x00\x05\x00"u8);
        Assert.Equal(unchecked((int)0x800F0002), Call(Services, "ksceSblPostSsMgrVerifyKeystone", keystone, 4u));
        Assert.Equal(Unsupported, Call(Services, "ksceSblPostSsMgrVerifyKeystone", keystone, 5u));
        Assert.Equal(Invalid, Call(Services, "ksceSblPostSsMgrVerifyKeystoneWithPasscode", keystone, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceSblPostSsMgrGenerateAppKey", _harness.Word(0x18), output));
        Assert.Equal(Unsupported, Call(Services, "ksceSblPostSsMgrGenerateAppKey", _harness.Word(0x20), output));
    }

    [Fact]
    public void Coredump_and_cloud_keys_are_never_handed_out()
    {
        uint key = _harness.Alloc(0x20);
        Assert.Equal(Unsupported, Call(Services, "ksceSblCoredumpKeyStoreInitialize"));
        Assert.Equal(SsInvalid, Call(Services, "ksceSblCoredumpGetHmacKey", 4u, 0x20u, key));
        Assert.Equal(SsInvalid, Call(Services, "ksceSblCoredumpGetHmacKey", 1u, 0x10u, key));
        Assert.Equal(NotInitialized, Call(Services, "ksceSblCoredumpGetHmacKey", 3u, 0x20u, key));
        Assert.Equal(NotInitialized, Call(Services, "ksceSblCoredumpGetAesKey", 0x10000002u, 0x10u, key));
        Assert.Equal(SsInvalid, Call(Services, "ksceSblCoredumpGetAesKey", 0x10000003u, 0x10u, key));
        Assert.Equal(new byte[0x20], GuestMemory.ReadSpan(key, 0x20).ToArray());
        Assert.Equal(0, Call(Services, "ksceSblCoredumpKeyStoreFinalize"));
        Assert.Equal(SsInvalid, Call(Services, "ksceSblSsMgrCloudDataGetSignCryptHandle", 2u, 0u, key));
        Assert.Equal(Unsupported, Call(Services, "ksceSblSsMgrCloudDataGetSignCryptHandle", 1u, 1u, key));
        Assert.Equal(0, Call(Services, "ksceSblSsMgrCloudDataStop"));
    }

    [Fact]
    public void A_retail_unit_has_no_utoken_and_no_cp()
    {
        Assert.Equal(0, Call(Services, "ksceSblUtMgrHasFlag"));
        Assert.Equal(0, Call(Services, "ksceSblUtMgrIsAllowComTest", 0));
        Assert.Equal(unchecked((int)0x800F1A02), Call(Services, "ksceSblUtMgrGetTrilithiumBuffer", _harness.Alloc(0x58)));
        Assert.Equal(unchecked((int)0x800F1A16), Call(Services, "ksceSblUtMgrVerifyAndStoreUtoken", _harness.Alloc(0x800), 0x7FFu));
        Assert.Equal(Unsupported, Call(Services, "ksceSblUtMgrVerifyAndStoreUtoken", _harness.Alloc(0x800), 0x800u));
        Assert.Equal(unchecked((int)0x800F1002), Call(Services, "ksceSblRtcMgrGetCpRtcPhysical", _harness.Alloc(8)));
        uint date = _harness.Word(0);
        Assert.Equal(0, Call(Services, "ksceSblPostSsMgrGetExpireDate", date, 1u));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(date));
        Assert.Equal(unchecked((int)0x800F1325), Call(Services, "ksceSblPostSsMgrActivate", _harness.Str("ux0:data/activate/")));
        Assert.Equal(unchecked((int)0x800F0A16), Call(Services, "ksceSblPmMgrSetSdModeOff", 1u));
    }

    [Fact]
    public void Signed_param_sfo_is_read_but_never_verified()
    {
        string host = _harness.Emu.Vfs.Resolve("ux0:data/spsfo_test.sfo", forWrite: true)!;
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllBytes(host, [0x00, 0x50, 0x53, 0x46, 1, 2, 3]);
        try
        {
            uint handle = _harness.Alloc(12);
            Assert.Equal(unchecked((int)0x800F1B16), Call(Services, "ksceSblSpsfoMgrOpen", _harness.Str("vs0:x.sfo"), handle));
            Assert.Equal(unchecked((int)0x80010002), Call(Services, "ksceSblSpsfoMgrOpen", _harness.Str("ux0:data/none.sfo"), handle));
            Assert.Equal(0, Call(Services, "ksceSblSpsfoMgrOpen", _harness.Str("ux0:data/spsfo_test.sfo"), handle));
            uint data = GuestMemory.R32(handle + 4);
            Assert.Equal(7u, GuestMemory.R32(handle + 8));
            Assert.Equal(new byte[] { 0x00, 0x50, 0x53, 0x46, 1, 2, 3 }, GuestMemory.ReadSpan(data, 7).ToArray());
            Assert.Equal(Unsupported, Call(Services, "ksceSblSpsfoMgrVerify", handle, _harness.Alloc(8), _harness.Alloc(8)));
            Assert.Equal(0, Call(Services, "ksceSblSpsfoMgrClose", handle));
            Assert.Equal(unchecked((int)0x800F1B16), Call(Services, "ksceSblSpsfoMgrClose", 0u));
        }
        finally
        {
            File.Delete(host);
        }
    }
}
