using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>The system side (SceVshBridge, SceAppMgr, SceAVConfig, SceBbmc, Videodec, SceNetPs syscalls,
/// libpaf's C library, the xthreads of libc.suprx).</summary>
[Collection("guest")]
public sealed class VshBridgeSystemSideTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hle7-" + Guid.NewGuid().ToString("N"));
    private Emulator? _emu;

    private Emulator Emu()
    {
        if (_emu != null)
        {
            return _emu;
        }

        Directory.CreateDirectory(_root);
        return _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    private T Module<T>() => Emu().Hle.Modules.OfType<T>().Single();

    private static object? Invoke(object module, string name, params object?[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    private static int Call(object module, string name, params object?[] args) => Convert.ToInt32(Invoke(module, name, args));

    private uint Alloc(uint size)
    {
        uint p = fixture.Memory.Alloc(size, "hle7");
        GuestMemory.Fill(p, 0, size);
        return p;
    }

    private uint Str(string s)
    {
        uint p = Alloc((uint)s.Length + 1);
        GuestMemory.WriteCString(p, s);
        return p;
    }

    public void Dispose()
    {
        _emu?.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void NvsDataRoundTripsAndChecksTheSize()
    {
        VshBridge vsh = Module<VshBridge>();
        uint buf = Alloc(0x20), opt = Alloc(8), back = Alloc(0x20);
        for (uint i = 0; i < 0x10; i++)
        {
            GuestMemory.Write(buf + i, (byte)(0xA0 + i));
        }

        GuestMemory.W32(opt, 0x10);
        Assert.Equal(0, Call(vsh, "_vshSblSsSetNvsData", 0x480, buf, 0, opt));
        Assert.Equal(0, Call(vsh, "_vshSblSsGetNvsData", 0x480, back, 0, opt));
        Assert.True(GuestMemory.ReadSpan(back, 0x10).SequenceEqual(GuestMemory.ReadSpan(buf, 0x10)));
        Assert.Equal(0, Call(vsh, "_vshSblSsGetNvsData", 0x500, back, 0, opt)); // never written: zeros
        Assert.True(GuestMemory.ReadSpan(back, 0x10).ToArray().All(b => b == 0));
        GuestMemory.W32(opt, 0x21);
        Assert.Equal(unchecked((int)0x8002710B), Call(vsh, "_vshSblSsGetNvsData", 0x480, back, 0, opt));
    }

    [Fact]
    public void CrashArchiveSegmentsRoundTrip()
    {
        VshBridge vsh = Module<VshBridge>();
        int ctx = Call(vsh, "vshCoredumpCafContextCreate");
        uint opt = Alloc(0x18), src = Alloc(0x40), enc = Alloc(0x40), dec = Alloc(0x40), iv = Alloc(0x10), digA = Alloc(0x20), digB = Alloc(0x20), o8 = Alloc(8);
        for (uint i = 0; i < 0x40; i++)
        {
            GuestMemory.Write(src + i, (byte)i);
        }

        GuestMemory.W32(opt + 8, iv);
        GuestMemory.W32(opt + 0xC, 0x10);
        Assert.Equal(0, Call(vsh, "_vshCoredumpCafSegmentInit", ctx, 1u, 1, 2u, opt));
        GuestMemory.W32(o8, 0x40);
        Assert.Equal(0, Call(vsh, "_vshCoredumpCafSegmentTransform", ctx, src, enc, o8));
        Assert.False(GuestMemory.ReadSpan(enc, 0x40).SequenceEqual(GuestMemory.ReadSpan(src, 0x40)));
        GuestMemory.W32(o8, 0x20);
        Assert.Equal(0, Call(vsh, "_vshCoredumpCafSegmentFinal", ctx, digA, o8));
        Assert.Equal(0, Call(vsh, "_vshCoredumpCafSegmentInit", ctx, 1u, 2, 2u, opt));
        GuestMemory.W32(o8, 0x40);
        Assert.Equal(0, Call(vsh, "_vshCoredumpCafSegmentTransform", ctx, enc, dec, o8));
        GuestMemory.W32(o8, 0x20);
        Assert.Equal(0, Call(vsh, "_vshCoredumpCafSegmentFinal", ctx, digB, o8));
        Assert.True(GuestMemory.ReadSpan(dec, 0x40).SequenceEqual(GuestMemory.ReadSpan(src, 0x40)));
        Assert.True(GuestMemory.ReadSpan(digA, 0x20).SequenceEqual(GuestMemory.ReadSpan(digB, 0x20))); // the HMAC covers the plain text
        Assert.Equal(0, Call(vsh, "vshCoredumpCafContextDestroy", ctx));
        Assert.Equal(VitaSharp.Core.Kernel.SceError.UNKNOWN_UID, Call(vsh, "vshCoredumpCafContextDestroy", ctx));
    }

    /// <summary>The user eboot signatures go through the kernel's (SceVshBridge 3.74 copies the option block in and
    /// calls ksceNpDrmEbootSigGen*/Verify): the signature is the emulated unit's integrity signature, the one the
    /// package installer writes; the check answers 0 or 0x80870005; the option block is input (its first word the
    /// software version), never written.</summary>
    [Fact]
    public void EbootSignaturesVerifyOnlyTheirEboot()
    {
        VshBridge vsh = Module<VshBridge>();
        const string Eboot = "ux0:pspemu/PSP/GAME/NPUZ00001/EBOOT.PBP";
        string host = Emu().Vfs.Resolve(Eboot, forWrite: true)!;
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllBytes(host, [0x00, 0x50, 0x42, 0x50]);
        uint path = Str(Eboot), sha = Alloc(0x20), sig = Alloc(0x200), opt = Alloc(0x10);
        GuestMemory.W32(opt, 0x03600000);
        byte[] hash = new byte[0x20];
        for (uint i = 0; i < 0x20; i++)
        {
            hash[i] = (byte)(i * 7);
            GuestMemory.Write(sha + i, hash[i]);
        }

        Assert.Equal(VitaSharp.Core.Kernel.SceError.ILLEGAL_ADDR, Call(vsh, "_vshNpDrmEbootSigGenPsp", path, sha, sig, 0u));
        Assert.Equal(0, Call(vsh, "_vshNpDrmEbootSigGenPsp", path, sha, sig, opt));
        Assert.Equal(0x03600000u, GuestMemory.R32(opt));
        Assert.Equal(PspEbootSignature.Create("EBOOT.PBP", hash, PspEbootSignature.KindPsp), GuestMemory.ReadSpan(sig, 0x200).ToArray());
        Assert.Equal(0, Call(vsh, "_vshNpDrmEbootSigVerify", path, sig, opt));
        Assert.Equal(0, Call(vsh, "_vshNpDrmPspEbootVerify", path, sig, opt));
        const int NpDrmInvalid = unchecked((int)0x80870001), NpDrmBadData = unchecked((int)0x80870005);
        Assert.Equal(NpDrmBadData, Call(vsh, "_vshNpDrmEbootSigVerify", Str("ux0:other/EBOOT2.PBP"), sig, opt));
        Assert.Equal(NpDrmBadData, Call(vsh, "_vshNpDrmPspEbootVerify", Str("ux0:other/EBOOT2.PBP"), sig, opt));
        Assert.Equal(NpDrmInvalid, Call(vsh, "_vshNpDrmEbootSigVerify", path, 0u, opt));
        // an eboot that is not there: the kernel's stat answers
        Assert.Equal(unchecked((int)0x80010002), Call(vsh, "_vshNpDrmEbootSigGenPs1", Str("ux0:pspemu/none/EBOOT.PBP"), sha, sig, opt));
        Assert.Equal(new byte[0x200], GuestMemory.ReadSpan(sig, 0x200).ToArray());
        Assert.Equal(0, Call(vsh, "_vshNpDrmEbootSigGenPs1", path, sha, sig, opt));
        Assert.Equal(0, Call(vsh, "_vshNpDrmEbootSigVerify", path, sig, opt));
        GuestMemory.Write(sig, (byte)'X');
        Assert.Equal(NpDrmBadData, Call(vsh, "_vshNpDrmEbootSigVerify", path, sig, opt));
        Assert.Equal(NpDrmBadData, Call(vsh, "_vshNpDrmPspEbootVerify", path, sig, opt));
    }

    /// <summary>The signature the package installer writes for an eboot passes the kernel's check the shell and the
    /// PSP emulator make (one signature model for both).</summary>
    [Fact]
    public void The_installer_signature_passes_the_kernel_check()
    {
        SystemServicesVsh services = Module<SystemServicesVsh>();
        byte[] hash = new byte[0x20];
        hash[3] = 9;
        uint sig = Alloc(0x200);
        PspEbootSignature.Create("EBOOT.PBP", hash, PspEbootSignature.KindPs1).CopyTo(GuestMemory.Span(sig, 0x200));
        uint path = Str("ux0:pspemu/PSP/GAME/SLUS00001/EBOOT.PBP");
        Assert.Equal(0, Call(services, "ksceNpDrmEbootSigVerify", path, sig));
        hash[3] = 8;
        PspEbootSignature.Create("EBOOT.PBP", hash, PspEbootSignature.KindPs1).AsSpan(0x30, 0x20).CopyTo(GuestMemory.Span(sig + 0x30, 0x20));
        Assert.Equal(unchecked((int)0x80870005), Call(services, "ksceNpDrmEbootSigVerify", path, sig));
    }

    [Fact]
    public void IduModeAndClocks()
    {
        VshBridge vsh = Module<VshBridge>();
        Assert.Equal(0, Call(vsh, "vshSysconIsIduMode"));
        Call(vsh, "vshSysconIduModeSet");
        Assert.Equal(1, Call(vsh, "vshSysconIsIduMode"));
        Call(vsh, "vshSysconIduModeClear");
        Assert.Equal(0, Call(vsh, "vshSysconIsIduMode"));
        Assert.Equal(1, Call(vsh, "vshSblAimgrIsVITA"));
        uint tick = Alloc(8);
        GuestMemory.Write(tick, 1UL); // an alarm long passed
        Assert.Equal(0, Call(vsh, "_vshRtcSetSecureAlarmTick", tick));
        Assert.Equal(1, Call(vsh, "vshRtcIsSecureAlarmed"));
    }

    [Fact]
    public void NotificationRequestsReachTheShellInOrder()
    {
        AppMgrShell am = Module<AppMgrShell>();
        uint req = Alloc(0x40), out1 = Alloc(0x40);
        Assert.Equal(unchecked((int)0x80802013), Call(am, "sceAppMgrReceiveNotificationRequestForShell", out1));
        GuestMemory.W32(req, 0x1234);
        Assert.Equal(0, Call(am, "SendNotificationRequest", req));
        GuestMemory.W32(req, 0x5678);
        Assert.Equal(0, Call(am, "SendNotificationRequest", req));
        Assert.Equal(0, Call(am, "sceAppMgrReceiveNotificationRequestForShell", out1));
        Assert.Equal(0x1234u, GuestMemory.R32(out1));
        Assert.Equal(0, Call(am, "sceAppMgrReceiveNotificationRequestForShell", out1));
        Assert.Equal(0x5678u, GuestMemory.R32(out1));
        Assert.Equal(unchecked((int)0x80802013), Call(am, "sceAppMgrReceiveNotificationRequestForShell", out1));
    }

    [Fact]
    public void SystemEventsQueueForTheApplication()
    {
        SystemMisc sys = Module<SystemMisc>();
        uint ev = Alloc(0x40);
        Assert.Equal(unchecked((int)0x80801000), Call(sys, "sceAppMgrReceiveSystemEvent", ev));
        Invoke(sys, "PostSystemEvent", 0x10000002, "ABCDEFGHIJKL");
        Assert.Equal(0, Call(sys, "sceAppMgrReceiveSystemEvent", ev));
        Assert.Equal(0x10000002u, GuestMemory.R32(ev));
        Assert.Equal("ABCDEFGHIJKL", GuestMemory.ReadCString(ev + 4, 0xD));
    }

    [Fact]
    public void HandheldAnswersOfAvConfigAndBbmc()
    {
        VshBridge vsh = Module<VshBridge>();
        SystemServicesVsh svc = Module<SystemServicesVsh>();
        // the test process is not a system program
        Assert.Equal(unchecked((int)0x80010058), Call(vsh, "sceAVConfigHdmiCecEnable"));
        Assert.Equal(unchecked((int)0x80010058), Call(svc, "sceBbmcNoModem"));
        Assert.Equal(unchecked((int)0x80330003), Call(svc, "sceBbmcUsbExtIf", 0));
        Assert.Equal(1, Call(vsh, "sceAVConfigGetShutterVol"));
    }

    [Fact]
    public void NalUnitsBecomeAnnexB()
    {
        byte[] avcc = [0, 0, 0, 2, 0x65, 0x88, 0, 0, 0, 1, 0x41];
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65, 0x88, 0, 0, 0, 1, 0x41 }, Videodec.ToAnnexB(avcc));
        byte[] already = [0, 0, 1, 0x67, 0x42];
        Assert.Equal(already, Videodec.ToAnnexB(already));
    }

    [Fact]
    public void NetSyscallsSysctlAndDescriptors()
    {
        Net net = Module<Net>();
        uint block = Alloc(0x18), name = Alloc(8);
        GuestMemory.W32(block, name);
        GuestMemory.W32(block + 4, 2);
        Assert.Equal(-2, Call(net, "sceNetSyscallSysctl", block));
        GuestMemory.W32(block + 4, 25);
        Assert.Equal(-22, Call(net, "sceNetSyscallSysctl", block));
        int d = Call(net, "sceNetSyscallDescriptorCreate", "x", 0);
        Assert.Equal(0, Call(net, "sceNetSyscallDescriptorCtl", d, 1, 0u, 0));
        Assert.Equal(0, Call(net, "sceNetSyscallDescriptorClose", d));
        Assert.Equal(-9, Call(net, "sceNetSyscallDescriptorClose", d));
    }

    [Fact]
    public void PafStringFunctionsFollowLibpaf()
    {
        Libc libc = Module<Libc>();
        uint a = Str("abc"), b = Str("abd"), dst = Alloc(8);
        Assert.Equal(0, Call(libc, "sce_paf_private_strcmp", 0u, 0u));
        Assert.Equal(-1, Call(libc, "sce_paf_private_strcmp", 0u, a));
        Assert.Equal(1, Call(libc, "sce_paf_private_strcmp", a, 0u));
        Assert.Equal('c' - 'd', Call(libc, "sce_paf_private_strcmp", a, b));
        Assert.Equal(0, Call(libc, "sce_paf_private_strncmp", a, b, 2));
        Assert.Equal(3, Call(libc, "sce_paf_private_strlcpy", dst, a, 3u));
        Assert.Equal("ab", GuestMemory.ReadCString(dst));
        Assert.Equal(0, Call(libc, "sce_paf_private_strlen", 0u));
        Assert.Equal(0, Call(libc, "sce_paf_private_wcslen", 0u));
        Assert.Equal('A', Call(libc, "sce_paf_private_toupper", (int)'a'));
        Assert.Equal(0xE1, Call(libc, "sce_paf_private_toupper", 0xE1)); // only 7-bit letters
        Invoke(libc, "sce_paf_private_srand", 1u);
        Assert.Equal((int)(((1u * 0x41C64E6D + 12345) >> 16) & 0x7FFF), Call(libc, "sce_paf_private_rand"));
        Assert.Equal(0u, (uint)Call(libc, "sce_paf_private_strcpy", 0u, a));
    }

    [Fact]
    public void XthreadsMutexOnAGuestThread()
    {
        Emulator emu = Emu();
        Libc libc = Module<Libc>();
        Exception? failure = null;
        var results = new List<int>();
        GuestThread th = emu.Kernel.CreateThread("hle7-xthreads", 0, 0x10000100, 0x10000, 0, 0x70000);
        th.HostBody = t =>
        {
            try
            {
                uint mtx = Alloc(4), xt = Alloc(8);
                results.Add(Call(libc, "_Mtx_init", mtx, 0x101u)); // recursive
                results.Add(Call(libc, "_Mtx_lock", mtx));
                results.Add(Call(libc, "_Mtx_current_owns", mtx));
                results.Add(Call(libc, "_Mtx_trylock", mtx));
                results.Add(Call(libc, "_Mtx_unlock", mtx));
                results.Add(Call(libc, "_Mtx_unlock", mtx));
                results.Add(Call(libc, "_Mtx_current_owns", mtx));
                Invoke(libc, "_Mtx_destroy", mtx);
                results.Add((int)GuestMemory.R32(mtx));
                results.Add(Call(libc, "xtime_get", xt, 1));
                results.Add(GuestMemory.R32(xt) > 1_600_000_000 ? 1 : 0);
                results.Add(Call(libc, "_Thrd_equal", 5, 5));
                results.Add(Call(libc, "_Thrd_current") == t.Uid ? 1 : 0);
            }
            catch (Exception e)
            {
                failure = e;
            }
        };
        th.Start(0, 0);
        Assert.True(th.WaitForExit(60_000), "test thread did not finish");
        emu.Kernel.DeleteThread(th);
        if (failure != null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        Assert.Equal([0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 1, 1], results);
    }
}
