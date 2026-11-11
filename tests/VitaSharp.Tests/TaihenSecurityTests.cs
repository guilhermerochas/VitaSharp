using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// taiHEN's user API as homebrew uses it (injections that put the original bytes back on release, kernel plugins that
/// are accepted without running, the user/kernel copies, TAI_ERROR_* 0x9001000x) and SceSblDmac5Mgr's hash commands
/// checked against the published SHA-1/SHA-256 and HMAC test vectors (FIPS 180, RFC 2202, RFC 4231), plus
/// sceKernelGetRandomNumber's 64-byte limit.
/// </summary>
[Collection("guest")]
public sealed class TaihenSecurityTests(GuestMemoryFixture fx) : IDisposable
{
    const int NOT_FOUND = unchecked((int)0x90010002), INVALID_ARGS = unchecked((int)0x90010003),
        SBL_INVALID = unchecked((int)0x800F0016), RNG_INVALID = unchecked((int)0x810C0000), NO_MOD_FILE = unchecked((int)0x8002D01C);

    readonly HleHarness _h = new(fx, "taihen");
    Taihen T => _h.Module<Taihen>();
    Security S => _h.Module<Security>();

    public void Dispose() => _h.Dispose();

    uint Bytes(byte[] b) { uint p = _h.Alloc((uint)b.Length + 4); b.CopyTo(GuestMemory.Span(p, b.Length)); return p; }

    [Fact]
    public void InjectionsRestoreTheOriginalBytes()
    {
        uint target = Bytes([1, 2, 3, 4, 5, 6, 7, 8]), patch = Bytes([0xAA, 0xBB, 0xCC]);
        Assert.Equal(INVALID_ARGS, Call(T, "taiInjectAbs", 0u, patch, 3u));
        Assert.Equal(INVALID_ARGS, Call(T, "taiInjectAbs", target, patch, 0u));
        Assert.Equal(INVALID_ARGS, Call(T, "taiInjectAbs", target, patch, 0x100001u));
        int a = Call(T, "taiInjectAbs", target + 2, patch, 3u);
        Assert.True(a > 0);
        Assert.Equal(new byte[] { 1, 2, 0xAA, 0xBB, 0xCC, 6, 7, 8 }, GuestMemory.ReadSpan(target, 8).ToArray());
        // a second injection over the first, released in reverse order, gives the bytes back step by step
        int b = Call(T, "taiInjectAbs", target + 3, Bytes([0x11]), 1u);
        Assert.Equal(new byte[] { 1, 2, 0xAA, 0x11, 0xCC, 6, 7, 8 }, GuestMemory.ReadSpan(target, 8).ToArray());
        Assert.Equal(0, Call(T, "taiInjectRelease", b));
        Assert.Equal(0, Call(T, "taiInjectRelease", a));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, GuestMemory.ReadSpan(target, 8).ToArray());
        Assert.Equal(NOT_FOUND, Call(T, "taiInjectRelease", a));
        Assert.Equal(NOT_FOUND, Call(T, "taiHookRelease", a, 0u));

        // module-relative injections need a known module and segment, and must fit in it
        uint args = _h.Alloc(0x1C);
        Assert.Equal(INVALID_ARGS, Call(T, "taiInjectDataForUser", 0u));
        GuestMemory.W32(args, 0x1C); GuestMemory.W32(args + 4, 0x7FFF0001); GuestMemory.W32(args + 20, patch); GuestMemory.W32(args + 24, 3);
        Assert.Equal(NOT_FOUND, Call(T, "taiInjectDataForUser", args));
        GuestMemory.W32(args + 24, 0);
        Assert.Equal(INVALID_ARGS, Call(T, "taiInjectDataForUser", args));

        // the main module (NULL name) when none is loaded, and the info struct size check
        uint info = _h.Alloc(0x38);
        GuestMemory.W32(info, 0x20);
        Assert.Equal(INVALID_ARGS, Call(T, "taiGetModuleInfo", 0u, info));
        GuestMemory.W32(info, 0x38);
        Assert.Equal(NOT_FOUND, Call(T, "taiGetModuleInfo", _h.Str("NoSuchModule"), info));
        Assert.Equal(NOT_FOUND, Call(T, "taiGetModuleExportFunc", _h.Str("NoSuchModule"), 0u, 0u, new Ptr<uint>(_h.Word(0))));

        uint dst = _h.Alloc(8), src = Bytes([9, 8, 7, 6]);
        Assert.Equal(0, Call(T, "taiMemcpyUserToKernel", dst, src, 4u));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, GuestMemory.ReadSpan(dst, 4).ToArray());
        Assert.Equal(0, Call(T, "taiMemcpyKernelToUser", dst + 4, src, 0u));
        Assert.Equal(INVALID_ARGS, Call(T, "taiMemcpyKernelToUser", 0u, src, 4u));
        Assert.Equal(0, Call(T, "taiReloadConfig"));
    }

    [Fact]
    public void KernelPluginsAreAcceptedWithoutRunning()
    {
        Assert.Equal(NO_MOD_FILE, Call(T, "taiLoadKernelModule", "ur0:tai/missing.skprx", 0, 0u));
        Assert.Equal(NO_MOD_FILE, Call(T, "taiLoadStartKernelModuleForUser", "ur0:tai/missing.skprx", 0u));
        Directory.CreateDirectory(Path.Combine(_h.Root, "ur0", "tai"));
        File.WriteAllBytes(Path.Combine(_h.Root, "ur0", "tai", "plugin.skprx"), [0x7F, (byte)'E', (byte)'L', (byte)'F']);
        int uid = Call(T, "taiLoadKernelModule", "ur0:tai/plugin.skprx", 0, 0u);
        Assert.True(uid > 0);
        uint res = _h.Word(9);
        Assert.Equal(0, Call(T, "taiStartKernelModuleForUser", uid, 0u, 0u, new Ptr<int>(res)));
        Assert.Equal(0u, GuestMemory.R32(res)); // SCE_KERNEL_START_SUCCESS
        Assert.Equal(0, Call(T, "taiStopKernelModuleForUser", uid, 0u, 0u, new Ptr<int>(res)));
        Assert.Equal(0, Call(T, "taiUnloadKernelModule", uid, 0, 0u));
        Assert.Equal(SceError.INVALID_UID, Call(T, "taiUnloadKernelModule", uid, 0, 0u));
        Assert.Equal(SceError.INVALID_UID, Call(T, "taiStartKernelModuleForUser", uid, 0u, 0u, new Ptr<int>(res)));
        Assert.Equal(SceError.INVALID_UID, Call(T, "taiStopKernelModuleForUser", uid, 0u, 0u, new Ptr<int>(res)));
        int uid2 = Call(T, "taiLoadStartKernelModuleForUser", "ur0:tai/plugin.skprx", 0u);
        Assert.Equal(0, Call(T, "taiStopUnloadKernelModuleForUser", uid2, 0u, 0u, new Ptr<int>(res)));
        Assert.Equal(SceError.INVALID_UID, Call(T, "taiStopUnloadKernelModuleForUser", uid2, 0u, 0u, new Ptr<int>(res)));
        _h.OnGuest(_ => Assert.Equal(NO_MOD_FILE, Call(T, "taiLoadStartModuleForPidForUser", "ux0:data/missing.suprx", 0u)));
    }

    string Hash(uint command, byte[] data, byte[]? key = null)
    {
        uint param = _h.Alloc(24), dst = _h.Alloc(32);
        GuestMemory.W32(param, Bytes(data)); GuestMemory.W32(param + 4, dst); GuestMemory.W32(param + 8, (uint)data.Length);
        if (key != null) { GuestMemory.W32(param + 12, Bytes(key)); GuestMemory.W32(param + 16, (uint)key.Length); }
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
