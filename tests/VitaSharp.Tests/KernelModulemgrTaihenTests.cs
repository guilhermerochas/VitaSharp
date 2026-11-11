using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The kernel module manager (SceModulemgrForDriver/ForKernel) and taiHEN's kernel API (taihenForKernel,
/// taihenModuleUtils, served by the HLE because taihen.skprx itself is not run): module lookups by process, offsets,
/// exports of loaded modules and of HLE firmware modules, hooks and injections in kernel modules.
/// </summary>
[Collection("guest")]
public sealed class KernelModulemgrTaihenTests(GuestMemoryFixture fx) : IDisposable
{
    const int NOT_FOUND = unchecked((int)0x90010002), INVALID_ARGS = unchecked((int)0x90010003);
    const uint AnyLibrary = 0xFFFFFFFF, Lib = 0x5EC5A120, Fn = 0x55555555, Other = 0x66666666;

    readonly HleHarness _h = new(fx, "kernmgr");
    ModulemgrKernel Modmgr => _h.Module<ModulemgrKernel>();
    Taihen Tai => _h.Module<Taihen>();

    public void Dispose() => _h.Dispose();

    string Write(string file, byte[] elf)
    {
        string dir = Path.Combine(_h.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), elf);
        return "ur0:tai/" + file;
    }

    /// <summary>A kernel module: function 0 returns 5 (exported as <see cref="Fn"/>), function 1 returns 6.</summary>
    static byte[] Module(string name, byte start = 0)
    {
        var b = new SceModuleBuilder(name).Start(start);
        int five = b.ReturnConstant(5), six = b.ReturnConstant(6);
        return b.Export("TestLib", Lib, 0x0001, (Fn, five), (Other, six)).Build();
    }

    LoadedModule ModuleOf(int uid) => _h.Emu.Kernel.Modules[uid];

    [Fact]
    public void LoadStartStopUnload()
    {
        string path = Write("k.skprx", Module("KMod"));
        Assert.Equal(SceError.MODULEMGR_NOENT, Call(Modmgr, "ksceKernelLoadModule", "ur0:tai/none.skprx", 0, 0u));
        _h.OnGuest(_ =>
        {
            int uid = Call(Modmgr, "ksceKernelLoadModule", path, 0, 0u);
            Assert.True(uid > 0);
            Assert.True(ModuleOf(uid).IsKernel);
            uint status = _h.Word(0xFF);
            Assert.Equal(0, Call(Modmgr, "ksceKernelStartModule", uid, 0u, 0u, 0, 0u, status));
            Assert.Equal(0u, GuestMemory.R32(status));
            Assert.Equal(0, Call(Modmgr, "ksceKernelStopModule", uid, 0u, 0u, 0, 0u, status));
            Assert.Equal(0, Call(Modmgr, "ksceKernelUnloadModule", uid, 0, 0u));
            Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelUnloadModule", uid, 0, 0u));
            Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelStartModule", uid, 0u, 0u, 0, 0u, status));

            int uid2 = Call(Modmgr, "ksceKernelLoadStartModule", path, 0u, 0u, 0, 0u, status);
            Assert.True(uid2 > 0);
            Assert.Equal(0, Call(Modmgr, "ksceKernelStopUnloadModule", uid2, 0u, 0u, 0, 0u, status));
            Assert.False(_h.Emu.Kernel.Modules.ContainsKey(uid2));

            // a module_start that fails leaves nothing loaded
            string failing = Write("fail.skprx", Module("KFail", start: 2));
            int uid3 = Call(Modmgr, "ksceKernelLoadStartModule", failing, 0u, 0u, 0, 0u, status);
            Assert.Equal(2u, GuestMemory.R32(status));
            Assert.False(_h.Emu.Kernel.Modules.ContainsKey(uid3));
        });
    }

    [Fact]
    public void ForPidCallsLoadUserModules()
    {
        string path = Write("u.suprx", Module("UMod"));
        _h.OnGuest(_ =>
        {
            uint status = _h.Word(0xFF);
            int uid = Call(Modmgr, "ksceKernelLoadStartModuleForPid", _h.Emu.Pid, path, 0u, 0u, 0, 0u, status);
            Assert.True(uid > 0);
            Assert.False(ModuleOf(uid).IsKernel);
            Assert.Equal(0u, GuestMemory.R32(status));
            Assert.Equal(0, Call(Modmgr, "ksceKernelStopModuleForPid", _h.Emu.Pid, uid, 0u, 0u, 0, 0u, status));
            Assert.Equal(0, Call(Modmgr, "ksceKernelUnloadModuleForPid", _h.Emu.Pid, uid, 0, 0u));
            Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelUnloadModuleForPid", _h.Emu.Pid, uid, 0, 0u));
            int uid2 = Call(Modmgr, "ksceKernelLoadStartModuleForPid", 0, path, 0u, 0u, 0, 0u, 0u);
            Assert.Equal(0, Call(Modmgr, "ksceKernelStopUnloadModuleForPid", 0, uid2, 0u, 0u, 0, 0u, status));
            Assert.Equal(SceError.MODULEMGR_INVALID_PROCESS_UID, Call(Modmgr, "ksceKernelLoadStartModuleForPid", 0x7FFFFF01, path, 0u, 0u, 0, 0u, 0u));
            Assert.Equal(SceError.MODULEMGR_NOENT, Call(Modmgr, "ksceKernelLoadStartModuleForPid", 0, "ur0:tai/none.suprx", 0u, 0u, 0, 0u, 0u));
            // the kernel pid loads a kernel module
            int k = Call(Modmgr, "ksceKernelLoadStartModuleForPid", KernelSpace.KernelPid, path, 0u, 0u, 0, 0u, 0u);
            Assert.True(ModuleOf(k).IsKernel);
            Assert.Equal(0, Call(Modmgr, "ksceKernelStopUnloadModuleForPid", KernelSpace.KernelPid, k, 0u, 0u, 0, 0u, 0u));
        });
    }

    [Fact]
    public void ModuleInfoListAndControlBlock()
    {
        var k = _h.Emu.LoadModule(Write("k.skprx", Module("KInfo")), kernel: true);
        var u = _h.Emu.LoadModule(Write("u.suprx", Module("UInfo")));
        uint info = _h.Alloc(0x1B8);
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleInfo", KernelSpace.KernelPid, k.Uid, info));
        Assert.Equal("KInfo", GuestMemory.ReadCString(info + 0x0C));
        Assert.Equal(k.Segments[0].Address, GuestMemory.R32(info + 0x154 + 8));
        // a process's call does not see the kernel's modules, nor the kernel the process's
        Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelGetModuleInfo", _h.Emu.Pid, k.Uid, info));
        Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelGetModuleInfo", KernelSpace.KernelPid, u.Uid, info));
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleInfo", 0, u.Uid, info));
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Modmgr, "ksceKernelGetModuleInfo", 0, u.Uid, 0u));
        Assert.Equal(SceError.MODULEMGR_INVALID_PROCESS_UID, Call(Modmgr, "ksceKernelGetModuleInfo", 0x7FFFFF01, u.Uid, info));

        uint ids = _h.Alloc(16 * 4), num = _h.Word(16);
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleList", KernelSpace.KernelPid, 0xFF, 1, ids, num));
        Assert.Equal(1u, GuestMemory.R32(num));
        Assert.Equal((uint)k.Uid, GuestMemory.R32(ids));
        GuestMemory.W32(num, 0);
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleList", 0, 0xFF, 1, ids, num));
        Assert.Equal(0u, GuestMemory.R32(num)); // room for none
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Modmgr, "ksceKernelGetModuleList", 0, 0xFF, 1, 0u, num));

        uint cbOut = _h.Alloc(4);
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleCB", k.Uid, cbOut));
        uint cb = GuestMemory.R32(cbOut);
        Assert.Equal((uint)k.Uid, GuestMemory.R32(cb + 0xC));
        Assert.Equal("KInfo", GuestMemory.ReadCString(GuestMemory.R32(cb + 0x1C)));
        Assert.Equal(k.ExportsStart, GuestMemory.R32(cb + 0x20));
        Assert.Equal(0x12345678u, GuestMemory.R32(cb + 0x30));
        Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelGetModuleCB", 0x7FFFFF01, cbOut));
    }

    [Fact]
    public void ModuleLookupsAndOffsets()
    {
        var k = _h.Emu.LoadModule(Write("k.skprx", Module("KTai")), kernel: true);
        uint info = _h.Alloc(0x38), addr = _h.Alloc(4);
        GuestMemory.W32(info, 0x38);
        Assert.Equal(0, Call(Tai, "taiGetModuleInfoForKernel", KernelSpace.KernelPid, _h.Str("KTai"), info));
        Assert.Equal((uint)k.Uid, GuestMemory.R32(info + 4));
        Assert.Equal(0x12345678u, GuestMemory.R32(info + 8));
        Assert.Equal(NOT_FOUND, Call(Tai, "taiGetModuleInfoForKernel", _h.Emu.Pid, _h.Str("KTai"), info)); // not a user module
        Assert.Equal(0, Call(Tai, "taiGetModuleInfoForKernel", KernelSpace.KernelPid, 0u, info)); // the newest
        Assert.Equal(0, Call(Tai, "module_get_by_name_nid", KernelSpace.KernelPid, 0u, 0x12345678u, info));
        Assert.Equal(unchecked((int)0x9001000B), Call(Tai, "module_get_by_name_nid", KernelSpace.KernelPid, 0u, 0xFFFFFFFFu, info));
        GuestMemory.W32(info, 0x10);
        Assert.Equal(unchecked((int)0x90010000), Call(Tai, "taiGetModuleInfoForKernel", KernelSpace.KernelPid, _h.Str("KTai"), info));

        Assert.Equal(0, Call(Tai, "module_get_offset", KernelSpace.KernelPid, k.Uid, 0, 0x10u, addr));
        Assert.Equal(k.Segments[0].Address + 0x10, GuestMemory.R32(addr));
        Assert.Equal(INVALID_ARGS, Call(Tai, "module_get_offset", KernelSpace.KernelPid, k.Uid, 4, 0x10u, addr));
        Assert.Equal(INVALID_ARGS, Call(Tai, "module_get_offset", KernelSpace.KernelPid, k.Uid, 0, 0x100000u, addr));
        Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Tai, "module_get_offset", _h.Emu.Pid, k.Uid, 0, 0x10u, addr));

        // exports of a loaded module, by library or any
        Assert.Equal(0, Call(Tai, "module_get_export_func", KernelSpace.KernelPid, _h.Str("KTai"), Lib, Fn, addr));
        Assert.Equal(SceModuleBuilder.FunctionAddress(k.Segments[0].Address, 1), GuestMemory.R32(addr));
        Assert.Equal(0, Call(Tai, "module_get_export_func", KernelSpace.KernelPid, _h.Str("KTai"), AnyLibrary, Other, addr));
        Assert.Equal(NOT_FOUND, Call(Tai, "module_get_export_func", KernelSpace.KernelPid, _h.Str("KTai"), 0x1u, Fn, addr));
        Assert.Equal(INVALID_ARGS, Call(Tai, "module_get_export_func", KernelSpace.KernelPid, 0u, Lib, Fn, addr));

        // a firmware module VitaSharp implements: a stub that reaches the HLE (SceSysmem's icache invalidation)
        Assert.Equal(0, Call(Tai, "module_get_export_func", KernelSpace.KernelPid, _h.Str("SceSysmem"), AnyLibrary, 0xF4C7F578u, addr));
        uint stub = GuestMemory.R32(addr);
        Assert.True(KernelSpace.IsKernelOwned(stub));
        Assert.Equal(0xEFu, GuestMemory.R32(stub) >> 24);
        Assert.Equal(0xF4C7F578u, GuestMemory.R32(stub + 8));
        Assert.Equal(NOT_FOUND, Call(Tai, "module_get_export_func", KernelSpace.KernelPid, _h.Str("SceNoSuch"), AnyLibrary, 0xF4C7F578u, addr));
        _h.OnGuest(t => t.CallGuest(stub, stub, 4)); // callable
    }

    [Fact]
    public void ImportsHooksAndInjections()
    {
        var k = _h.Emu.LoadModule(Write("k.skprx", Module("KHooked")), kernel: true);
        // a client whose function 0 calls KHooked's Fn
        var c = new SceModuleBuilder("KClient").Import("TestLib", Lib, Fn);
        int call = c.CallImport(0, 0);
        var client = _h.Emu.LoadModule(Write("c.skprx", c.Build()), kernel: true);
        uint callFn = SceModuleBuilder.FunctionAddress(client.Segments[0].Address, call);
        uint addr = _h.Alloc(4);
        Assert.Equal(0, Call(Tai, "module_get_import_func", KernelSpace.KernelPid, _h.Str("KClient"), Lib, Fn, addr));
        uint importStub = GuestMemory.R32(addr);
        Assert.Equal(NOT_FOUND, Call(Tai, "module_get_import_func", KernelSpace.KernelPid, _h.Str("KClient"), Lib, Other, addr));

        // hook function code: returns 9
        uint hookFn = _h.Alloc(16);
        GuestMemory.Write(hookFn, (ushort)0x2009);
        GuestMemory.Write(hookFn + 2, (ushort)0x4770);
        _h.OnGuest(t =>
        {
            Assert.Equal(5u, t.CallGuest(callFn));
            uint hook = _h.Alloc(4);
            int importHook = Call(Tai, "taiHookFunctionImportForKernel", KernelSpace.KernelPid, hook, _h.Str("KClient"), Lib, Fn, hookFn | 1);
            Assert.True(importHook > 0, $"0x{importHook:X8}");
            Assert.Equal(9u, t.CallGuest(callFn));
            // TAI_CONTINUE: the hook reference's "old" (+8) reaches the original
            uint old = GuestMemory.R32(GuestMemory.R32(hook) + 8);
            Assert.Equal(5u, t.CallGuest(old));
            Assert.Equal(0, Call(Tai, "taiHookReleaseForKernel", importHook, GuestMemory.R32(hook)));
            Assert.Equal(5u, t.CallGuest(callFn));
            Assert.Equal(importStub, _h.Emu.Linker.BoundStubs(Fn, Lib, kernel: true).Single());

            // an export hook patches the function itself (Thumb, in place)
            int exportHook = Call(Tai, "taiHookFunctionExportForKernel", KernelSpace.KernelPid, hook, _h.Str("KHooked"), Lib, Fn, hookFn | 1);
            Assert.True(exportHook > 0, $"0x{exportHook:X8}");
            Assert.Equal(9u, t.CallGuest(callFn));
            Assert.Equal(0, Call(Tai, "taiHookReleaseForKernel", exportHook, GuestMemory.R32(hook)));
            Assert.Equal(5u, t.CallGuest(callFn));
            Assert.Equal(NOT_FOUND, Call(Tai, "taiHookFunctionExportForKernel", KernelSpace.KernelPid, hook, _h.Str("KHooked"), Lib, 0x7777u, hookFn | 1));

            // by offset (function 0 at segment offset 0x10)
            int offsetHook = Call(Tai, "taiHookFunctionOffsetForKernel", KernelSpace.KernelPid, hook, k.Uid, 0, 0x10u, 1, hookFn | 1);
            Assert.True(offsetHook > 0);
            Assert.Equal(9u, t.CallGuest(callFn));
            Assert.Equal(0, Call(Tai, "taiHookReleaseForKernel", offsetHook, GuestMemory.R32(hook)));
            Assert.Equal(INVALID_ARGS, Call(Tai, "taiHookFunctionAbs", KernelSpace.KernelPid, 0u, hookFn, hookFn | 1));

            // an HLE firmware function through its kernel stub
            Assert.Equal(0, Call(Tai, "module_get_export_func", KernelSpace.KernelPid, _h.Str("SceSysmem"), AnyLibrary, 0x6BA2E51Cu, addr));
            uint fw = GuestMemory.R32(addr);
            int fwHook = Call(Tai, "taiHookFunctionAbs", KernelSpace.KernelPid, hook, fw, hookFn | 1);
            Assert.True(fwHook > 0);
            Assert.Equal(9u, t.CallGuest(fw));
            Assert.Equal(0, Call(Tai, "taiHookReleaseForKernel", fwHook, GuestMemory.R32(hook)));
        });

        // injections into a module's segment, released in reverse order
        uint data = _h.Alloc(4);
        GuestMemory.W32(data, 0xA5A5A5A5);
        uint target = k.Segments[0].Address + 0x300;
        int inj = Call(Tai, "taiInjectDataForKernel", KernelSpace.KernelPid, k.Uid, 0, 0x300u, data, 4u);
        Assert.True(inj > 0);
        Assert.Equal(0xA5A5A5A5u, GuestMemory.R32(target));
        Assert.Equal(INVALID_ARGS, Call(Tai, "taiInjectDataForKernel", KernelSpace.KernelPid, k.Uid, 0, 0x100000u, data, 4u));
        Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Tai, "taiInjectDataForKernel", KernelSpace.KernelPid, 0x7FFFFF01, 0, 0u, data, 4u));
        GuestMemory.W32(data, 0x5A5A5A5A);
        int abs = Call(Tai, "taiInjectAbsForKernel", KernelSpace.KernelPid, target, data, 2u);
        Assert.Equal(0xA5A55A5Au, GuestMemory.R32(target));
        Assert.Equal(0, Call(Tai, "taiInjectReleaseForKernel", abs));
        Assert.Equal(0, Call(Tai, "taiInjectReleaseForKernel", inj));
        Assert.Equal(0u, GuestMemory.R32(target));
        Assert.Equal(NOT_FOUND, Call(Tai, "taiInjectReleaseForKernel", inj));
        Assert.Equal(INVALID_ARGS, Call(Tai, "taiInjectAbsForKernel", KernelSpace.KernelPid, target, data, 0u));
    }

    [Fact]
    public void PluginLoadsAskedByKernelCode()
    {
        string user = Write("u.suprx", Module("UPlugin"));
        string kernel = Write("k.skprx", Module("KPlugin"));
        File.WriteAllText(Path.Combine(_h.Root, "ur0", "tai", "config.txt"), $"*KERNEL\n{kernel}\n*ALL\n{user}\n");
        Assert.Equal(0, Call(Tai, "taiReloadConfigForKernel", 0, 1));
        Assert.Contains(_h.Emu.Kernel.Modules.Values, m => m.IsKernel && m.Name == "KPlugin");
        Assert.Equal(0, Call(Tai, "taiReloadConfigForKernel", 0, 1)); // already loaded: once
        Assert.Single(_h.Emu.Kernel.Modules.Values, m => m.Name == "KPlugin");
        Assert.Equal(0, Call(Tai, "taiLoadPluginsForTitleForKernel", 0, "ANYTITLE1", 0));
        var plugin = _h.Emu.Kernel.Modules.Values.Single(m => m.Name == "UPlugin");
        Assert.False(plugin.IsKernel);
        Assert.True(plugin.Started);
        Assert.Equal(NOT_FOUND, Call(Tai, "taiLoadPluginsForTitleForKernel", 0x7FFFFF01, "ANYTITLE1", 0));
    }

    [Fact]
    public void UserApiLoadsKernelModulesThroughTheKernelLoader()
    {
        string path = Write("k.skprx", Module("KUser"));
        Assert.Equal(SceError.MODULEMGR_NOENT, Call(Tai, "taiLoadKernelModule", "ur0:tai/none.skprx", 0, 0u));
        _h.OnGuest(_ =>
        {
            int uid = Call(Tai, "taiLoadKernelModule", path, 0, 0u);
            Assert.True(ModuleOf(uid).IsKernel);
            uint res = _h.Word(9);
            Assert.Equal(0, Call(Tai, "taiStartKernelModuleForUser", uid, 0u, 0u, new Ptr<int>(res)));
            Assert.Equal(0u, GuestMemory.R32(res));
            Assert.Equal(0, Call(Tai, "taiStopKernelModuleForUser", uid, 0u, 0u, new Ptr<int>(res)));
            Assert.Equal(0, Call(Tai, "taiUnloadKernelModule", uid, 0, 0u));
            Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Tai, "taiUnloadKernelModule", uid, 0, 0u));
            int uid2 = Call(Tai, "taiLoadStartKernelModuleForUser", path, 0u);
            Assert.True(uid2 > 0);
            Assert.Equal(0, Call(Tai, "taiStopUnloadKernelModuleForUser", uid2, 0u, 0u, new Ptr<int>(res)));

            // user modules of a process
            string user = Write("u.suprx", Module("UUser"));
            int u = Call(Tai, "taiLoadStartModuleForPidForUser", user, 0u);
            Assert.False(ModuleOf(u).IsKernel);
            Assert.Equal(0, Call(Tai, "taiStopModuleForPidForUser", 0, u, 0u, new Ptr<int>(res)));
            Assert.Equal(0, Call(Tai, "taiUnloadModuleForPid", 0, u, 0u, new Ptr<int>(res)));
            int u2 = Call(Tai, "taiLoadStartModuleForPidForUser", user, 0u);
            Assert.Equal(0, Call(Tai, "taiStopUnloadModuleForPidForUser", 0, u2, 0u, new Ptr<int>(res)));
            Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Tai, "taiUnloadModuleForPid", 0, u2, 0u, new Ptr<int>(res)));
        });

        // the user/kernel copies follow ksceKernelMemcpy*: kernel memory is not a user range
        var sysmem = _h.Module<SysmemKernel>();
        int block = Call(sysmem, "ksceKernelAllocMemBlock", _h.Str("k"), 0x1020D006u, 0x1000u, 0u);
        uint user = _h.Alloc(8), basePtr = _h.Alloc(4);
        Assert.Equal(0, Call(sysmem, "ksceKernelGetMemBlockBase", block, basePtr));
        uint kernel = GuestMemory.R32(basePtr);
        Assert.Equal(0, Call(Tai, "taiMemcpyUserToKernel", kernel, user, 4u));
        Assert.Equal(0, Call(Tai, "taiMemcpyKernelToUser", user, kernel, 4u));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Tai, "taiMemcpyKernelToUser", kernel + 8, kernel, 4u));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Tai, "taiMemcpyUserToKernel", kernel + 8, kernel, 4u));
    }
}
