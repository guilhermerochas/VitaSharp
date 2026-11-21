using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// ScePsmDrmForDriver and the user library's way to it, as on the console: the scePsmDrm* syscalls call the kernel
/// exports kscePsmDrm*, so a kernel plugin that hooks an export sees the user's call (here a synthetic firmware
/// SceNpDrm exporting ScePsmDrmForDriver and a plugin whose code is written over the export entry); without a hook
/// the result is the firmware's. The kernel functions read the installed license as the PSM support does and refuse
/// what the secure processor decides (a signed license's key set, the activation data).
/// </summary>
[Collection("guest")]
public sealed class PsmDrmKernelTests : IDisposable
{
    private const uint PsmDrmForDriver = 0x9F4924F2, GetRifKey = 0x8C8CFD01, GetRifInfo = 0x984F9017;
    private const uint LdrPcMinus4 = 0xE51FF004;
    private const int Unsupported = unchecked((int)0x80020004), Invalid = unchecked((int)0x808F0101);
    private const int NotActivated = unchecked((int)0x808F0106), NotPsmRuntime = unchecked((int)0x80010058);
    private const uint LdrR0SpThenBxLr = 0xE59D0000, BxLr = 0xE12FFF1E;

    private readonly HleHarness _harness;

    public PsmDrmKernelTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "psmdrmk", kernelPlugins: true);
        SceModuleBuilder npdrm = new SceModuleBuilder("SceNpDrm").SegmentSizes(0x1000, 0x100);
        int body = npdrm.ReturnConstant(0);
        npdrm.Export("ScePsmDrmForDriver", PsmDrmForDriver, 0x0001, (GetRifKey, body), (GetRifInfo, body));
        string kd = Path.Combine(_harness.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "npdrm.skprx"), npdrm.Build());
        FirmwareKernelImage.Forget(_harness.Root);
    }

    public void Dispose()
    {
        FirmwareKernelImage.Forget(_harness.Root);
        _harness.Dispose();
    }

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    /// <summary>Loads a kernel plugin with one function answering <paramref name="answer"/>; returns its address.</summary>
    private uint LoadHookPlugin(byte answer) => LoadHookPlugin(b => b.ReturnConstant(answer));

    /// <summary>Loads a kernel plugin with the function <paramref name="function"/> adds; returns its address.</summary>
    private uint LoadHookPlugin(Func<SceModuleBuilder, int> function)
    {
        SceModuleBuilder hooker = new SceModuleBuilder("PsmHookPlugin").Start(0);
        int hook = function(hooker);
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "psmhook.skprx"), hooker.Build());
        File.WriteAllText(Path.Combine(dir, "config.txt"), "*KERNEL\nur0:tai/psmhook.skprx\n");
        List<string> results = _harness.Emu.LoadPlugins();
        Assert.Contains(": loaded and started (", Assert.Single(results));
        LoadedModule loaded = HleHarness.KernelModules(_harness.Emu).Values.Single(m => m.Path == "ur0:tai/psmhook.skprx");
        return hooker.AddressOf(loaded.Segments[0].Address, hook);
    }

    /// <summary>Writes a jump to <paramref name="hook"/> over an export entry, as kernel code does.</summary>
    private void WriteJump(uint at, uint hook)
    {
        SysmemKernel sysmem = _harness.Module<SysmemKernel>();
        uint patch = _harness.Alloc(8);
        GuestMemory.W32(patch, LdrPcMinus4);
        GuestMemory.W32(patch + 4, hook);
        Assert.Equal(0, Call(sysmem, "ksceKernelCpuUnrestrictedMemcpy", at, patch, 8u));
        Invoke(sysmem, "ksceKernelCpuDcacheWritebackInvalidateRange", at, 8u);
        Invoke(sysmem, "ksceKernelCpuIcacheAndL2WritebackInvalidateRange", at, 8u);
    }

    private (uint License, uint Keys, uint Times) SignedLicense()
    {
        uint license = _harness.Alloc(PsmLicense.Size);
        uint keys = _harness.Alloc(PsmLicense.KeySetSize);
        uint times = _harness.Alloc(0x10);
        GuestMemory.W32(times, _harness.Alloc(8));
        GuestMemory.W32(times + 4, _harness.Alloc(8));
        // a license whose signature area is not empty: the store's, its key set encrypted for the account
        GuestMemory.Fill(license + (uint)PsmLicense.Size - 0x100, 0x5A, 0x100);
        return (license, keys, times);
    }

    /// <summary>The caller is the PSM runtime (the only program scePsmDrmGetRifKey serves).</summary>
    private void AsPsmRuntime() =>
        _harness.Emu.Access = new ProgramAccess(ProgramAccess.PsmRuntimeAuthorityId, new byte[ProgramAccess.CapabilitySize], false);

    private int UserCallOnGuest(string name, params object[] args)
    {
        int result = 0;
        _harness.OnGuest(_ => result = Call(Services, name, args));
        return result;
    }

    [Fact]
    public void Without_a_hook_the_user_call_gets_the_firmware_answer()
    {
        LoadHookPlugin(0x55);
        (uint license, uint keys, uint times) = SignedLicense();
        Assert.False(_harness.Emu.FirmwareKernel.IsPatched(PsmDrmForDriver, GetRifKey));
        Assert.Equal(NotPsmRuntime, UserCallOnGuest("PsmDrmGetRifKey", license, keys, 0u, times, 0u));
        AsPsmRuntime();
        Assert.Equal(NotActivated, UserCallOnGuest("PsmDrmGetRifKey", license, keys, 0u, times, 0u));
    }

    [Fact]
    public void A_kernel_plugin_hook_on_the_export_sees_the_user_call()
    {
        uint hook = LoadHookPlugin(0x55);
        WriteJump(_harness.Emu.FirmwareKernel.EntryOf(PsmDrmForDriver, GetRifKey), hook);
        Assert.True(_harness.Emu.FirmwareKernel.IsPatched(PsmDrmForDriver, GetRifKey));
        (uint license, uint keys, uint times) = SignedLicense();
        AsPsmRuntime();
        // the hook decides, before the firmware's activation check (none here)
        Assert.Equal(0x55, UserCallOnGuest("PsmDrmGetRifKey", license, keys, 0u, times, 0u));
        // the other export is not hooked: the firmware's answer (this synthetic module has no license key)
        uint content = _harness.Alloc(0x30);
        Assert.Equal(Unsupported, UserCallOnGuest("PsmDrmGetRifInfo", license, content, 0u, 0u, 0u));
    }

    [Fact]
    public void The_kernel_functions_follow_the_firmware()
    {
        (uint license, uint keys, uint times) = SignedLicense();
        Assert.Equal(Invalid, Call(Services, "kscePsmDrmGetRifKey", 0u, keys, 0u, times, 0u));
        Assert.Equal(Invalid, Call(Services, "kscePsmDrmGetRifKey", license, 0u, 0u, times, 0u));
        Assert.Equal(NotActivated, Call(Services, "kscePsmDrmGetRifKey", license, keys, 0u, times, 0u));
        Assert.Equal(new byte[PsmLicense.KeySetSize], GuestMemory.ReadSpan(keys, PsmLicense.KeySetSize).ToArray());
        Assert.Equal(Invalid, Call(Services, "kscePsmDrmGetRifInfo", 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(NotActivated, Call(Services, "kscePsmDrmGetActInfo", _harness.Alloc(8)));
        Assert.Equal(NotActivated, Call(Services, "kscePsmDrmReadActData"));
        Assert.Equal(Invalid, Call(Services, "kscePsmDrmWriteActData", 0u, 0u));
        Assert.Equal(Unsupported, Call(Services, "kscePsmDrmWriteActData", _harness.Alloc(0x400), 0u));
        uint account = _harness.Word(0xFFFF);
        Assert.Equal(0, Call(Services, "kscePsmDrmRemoveActData", account));
        Assert.Equal(0u, GuestMemory.R32(account));
        // the user call of the removal goes to the kernel export (no twin of it forwards back)
        Assert.Equal(0, UserCallOnGuest("PsmDrmRemoveActData", account));
    }

    /// <summary>The syscalls pass all five arguments to the export (the fifth on the stack): a hook that answers its
    /// fifth argument sees the expiration time pointer of both calls.</summary>
    [Fact]
    public void The_fifth_argument_reaches_the_hooked_export()
    {
        uint hook = LoadHookPlugin(b => b.Arm(LdrR0SpThenBxLr, BxLr));
        WriteJump(_harness.Emu.FirmwareKernel.EntryOf(PsmDrmForDriver, GetRifKey), hook);
        WriteJump(_harness.Emu.FirmwareKernel.EntryOf(PsmDrmForDriver, GetRifInfo), hook);
        AsPsmRuntime();
        (uint license, uint keys, uint times) = SignedLicense();
        uint expiration = _harness.Alloc(8);
        Assert.Equal((int)expiration, UserCallOnGuest("PsmDrmGetRifKey", license, keys, 0u, times, expiration));
        Assert.Equal((int)expiration, UserCallOnGuest("PsmDrmGetRifInfo", license, 0u, 0u, times, expiration));
    }
}
