using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblDmac5Mgr's hash commands checked against the published SHA-1/SHA-256 and HMAC test vectors (FIPS 180,
/// RFC 2202, RFC 4231), plus sceKernelGetRandomNumber's 64-byte limit.
/// </summary>
[Collection("guest")]
public sealed class SecurityTests(GuestMemoryFixture fx) : IDisposable
{
    const int SBL_INVALID = unchecked((int)0x800F0016), RNG_INVALID = unchecked((int)0x810C0000);

    readonly HleHarness _h = new(fx, "security");
    Security S => _h.Module<Security>();

    public void Dispose() => _h.Dispose();

    uint Bytes(byte[] b)
    {
        uint p = _h.Alloc((uint)b.Length + 4);
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }

    string Hash(uint command, byte[] data, byte[]? key = null)
    {
        uint param = _h.Alloc(24), dst = _h.Alloc(32);
        GuestMemory.W32(param, Bytes(data));
        GuestMemory.W32(param + 4, dst);
        GuestMemory.W32(param + 8, (uint)data.Length);
        if (key != null)
        {
            GuestMemory.W32(param + 12, Bytes(key));
            GuestMemory.W32(param + 16, (uint)key.Length);
        }
        Assert.Equal(0, Call(S, "sceSblDmac5HashTransform", param, command, 0u));
        return Convert.ToHexString(GuestMemory.ReadSpan(dst, (command & 0xF0) is 0x10 or 0x30 ? 32 : 20)).ToLowerInvariant();
    }

    [Fact]
    public void Dmac5HashesMatchTheStandardVectors()
    {
        byte[] abc = Encoding.ASCII.GetBytes("abc");
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", Hash(0x03, abc));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Hash(0x13, abc));
        // RFC 2202 test case 2 / RFC 4231 test case 2: key "Jefe", "what do ya want for nothing?"
        byte[] jefe = Encoding.ASCII.GetBytes("Jefe"), msg = Encoding.ASCII.GetBytes("what do ya want for nothing?");
        Assert.Equal("effcdf6ae5eb2fa2d27416d5f184df9c259a7c79", Hash(0x23, msg, jefe));
        Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", Hash(0x33, msg, jefe));
        // the empty message
        uint param = _h.Alloc(24), dst = _h.Alloc(32);
        GuestMemory.W32(param + 4, dst);
        Assert.Equal(0, Call(S, "sceSblDmac5HashTransform", param, 0x03u, 0u));
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", Convert.ToHexString(GuestMemory.ReadSpan(dst, 20)).ToLowerInvariant());
        Assert.Equal(SBL_INVALID, Call(S, "sceSblDmac5HashTransform", param, 0x99u, 0u));
        Assert.Equal(SBL_INVALID, Call(S, "sceSblDmac5HashTransform", 0u, 0x03u, 0u));
        GuestMemory.W32(param + 4, 0);
        Assert.Equal(SBL_INVALID, Call(S, "sceSblDmac5HashTransform", param, 0x03u, 0u)); // no destination
    }

    [Fact]
    public void RandomNumbersUpTo64Bytes()
    {
        uint buf = _h.Alloc(0x48);
        Assert.Equal(RNG_INVALID, Call(S, "sceKernelGetRandomNumber", buf, 0x41u));
        Assert.Equal(RNG_INVALID, Call(S, "sceKernelGetRandomNumber", 0u, 4u));
        Assert.Equal(0, Call(S, "sceKernelGetRandomNumber", 0u, 0u));
        Assert.Equal(0, Call(S, "sceKernelGetRandomNumber", buf, 0x40u));
        Assert.Equal(0, Call(S, "_sceKernelGetRandomNumber", buf + 0x40, 8u));
        Assert.NotEqual(new byte[0x40], GuestMemory.ReadSpan(buf, 0x40).ToArray()); // 512 random bits are not all zero
    }
}
