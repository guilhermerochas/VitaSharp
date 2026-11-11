using VitaSharp.Core;

using VitaSharp.Core.Hle.Modules;

using VitaSharp.Core.Kernel;

using VitaSharp.Core.Loader;

using VitaSharp.Core.Memory;

using static VitaSharp.Tests.HleHarness;



namespace VitaSharp.Tests;



/// <summary>

/// The kernel module manager (SceModulemgrForDriver/ForKernel): kernel and process module loads, starts, stops and

/// unloads, and the module records kernel code reads.

/// </summary>

[Collection("guest")]

public sealed class KernelModulemgrTests(GuestMemoryFixture fixture) : IDisposable

{

    private const uint Lib = 0x5EC5A120, Fn = 0x55555555, Other = 0x66666666;



    private readonly HleHarness _harness = new(fixture, "kernmgr");

    private ModulemgrKernel Modmgr => _harness.Module<ModulemgrKernel>();



    public void Dispose() => _harness.Dispose();



    private string Write(string file, byte[] elf)

    {

        string dir = Path.Combine(_harness.Root, "ur0", "tai");

        Directory.CreateDirectory(dir);

        File.WriteAllBytes(Path.Combine(dir, file), elf);

        return "ur0:tai/" + file;

    }



    /// <summary>A kernel module: function 0 returns 5 (exported as <see cref="Fn"/>), function 1 returns 6.</summary>

    private static byte[] Module(string name, byte start = 0)

    {

        SceModuleBuilder b = new SceModuleBuilder(name).Start(start);

        int five = b.ReturnConstant(5), six = b.ReturnConstant(6);

        return b.Export("TestLib", Lib, 0x0001, (Fn, five), (Other, six)).Build();

    }



    private LoadedModule ModuleOf(int uid) => _harness.Emu.Kernel.Modules[uid];



    [Fact]

    public void LoadStartStopUnload()

    {

        string path = Write("k.skprx", Module("KMod"));

        Assert.Equal(SceError.MODULEMGR_NOENT, Call(Modmgr, "ksceKernelLoadModule", "ur0:tai/none.skprx", 0, 0u));

        _harness.OnGuest(_ =>

        {

            int uid = Call(Modmgr, "ksceKernelLoadModule", path, 0, 0u);

            Assert.True(uid > 0);

            Assert.True(ModuleOf(uid).IsKernel);

            uint status = _harness.Word(0xFF);

            Assert.Equal(0, Call(Modmgr, "ksceKernelStartModule", uid, 0u, 0u, 0, 0u, status));

            Assert.Equal(0u, GuestMemory.R32(status));

            Assert.Equal(0, Call(Modmgr, "ksceKernelStopModule", uid, 0u, 0u, 0, 0u, status));

            Assert.Equal(0, Call(Modmgr, "ksceKernelUnloadModule", uid, 0, 0u));

            Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelUnloadModule", uid, 0, 0u));

            Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelStartModule", uid, 0u, 0u, 0, 0u, status));



            int uid2 = Call(Modmgr, "ksceKernelLoadStartModule", path, 0u, 0u, 0, 0u, status);

            Assert.True(uid2 > 0);

            Assert.Equal(0, Call(Modmgr, "ksceKernelStopUnloadModule", uid2, 0u, 0u, 0, 0u, status));

            Assert.False(_harness.Emu.Kernel.Modules.ContainsKey(uid2));



            // a module_start that fails leaves nothing loaded

            string failing = Write("fail.skprx", Module("KFail", start: 2));

            int uid3 = Call(Modmgr, "ksceKernelLoadStartModule", failing, 0u, 0u, 0, 0u, status);

            Assert.Equal(2u, GuestMemory.R32(status));

            Assert.False(_harness.Emu.Kernel.Modules.ContainsKey(uid3));

        });

    }



    [Fact]

    public void ForPidCallsLoadUserModules()

    {

        string path = Write("u.suprx", Module("UMod"));

        _harness.OnGuest(_ =>

        {

            uint status = _harness.Word(0xFF);

            int uid = Call(Modmgr, "ksceKernelLoadStartModuleForPid", _harness.Emu.Pid, path, 0u, 0u, 0, 0u, status);

            Assert.True(uid > 0);

            Assert.False(ModuleOf(uid).IsKernel);

            Assert.Equal(0u, GuestMemory.R32(status));

            Assert.Equal(0, Call(Modmgr, "ksceKernelStopModuleForPid", _harness.Emu.Pid, uid, 0u, 0u, 0, 0u, status));

            Assert.Equal(0, Call(Modmgr, "ksceKernelUnloadModuleForPid", _harness.Emu.Pid, uid, 0, 0u));

            Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelUnloadModuleForPid", _harness.Emu.Pid, uid, 0, 0u));

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

        LoadedModule k = _harness.Emu.LoadModule(Write("k.skprx", Module("KInfo")), kernel: true);

        LoadedModule u = _harness.Emu.LoadModule(Write("u.suprx", Module("UInfo")));

        uint info = _harness.Alloc(0x1B8);

        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleInfo", KernelSpace.KernelPid, k.Uid, info));

        Assert.Equal("KInfo", GuestMemory.ReadCString(info + 0x0C));

        Assert.Equal(k.Segments[0].Address, GuestMemory.R32(info + 0x154 + 8));

        // a process's call does not see the kernel's modules, nor the kernel the process's

        Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelGetModuleInfo", _harness.Emu.Pid, k.Uid, info));

        Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelGetModuleInfo", KernelSpace.KernelPid, u.Uid, info));

        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleInfo", 0, u.Uid, info));

        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Modmgr, "ksceKernelGetModuleInfo", 0, u.Uid, 0u));

        Assert.Equal(SceError.MODULEMGR_INVALID_PROCESS_UID, Call(Modmgr, "ksceKernelGetModuleInfo", 0x7FFFFF01, u.Uid, info));



        uint ids = _harness.Alloc(16 * 4), num = _harness.Word(16);

        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleList", KernelSpace.KernelPid, 0xFF, 1, ids, num));

        Assert.Equal(1u, GuestMemory.R32(num));

        Assert.Equal((uint)k.Uid, GuestMemory.R32(ids));

        GuestMemory.W32(num, 0);

        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleList", 0, 0xFF, 1, ids, num));

        Assert.Equal(0u, GuestMemory.R32(num)); // room for none

        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Modmgr, "ksceKernelGetModuleList", 0, 0xFF, 1, 0u, num));



        uint cbOut = _harness.Alloc(4);

        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleCB", k.Uid, cbOut));

        uint cb = GuestMemory.R32(cbOut);

        Assert.Equal((uint)k.Uid, GuestMemory.R32(cb + 0xC));

        Assert.Equal("KInfo", GuestMemory.ReadCString(GuestMemory.R32(cb + 0x1C)));

        Assert.Equal(k.ExportsStart, GuestMemory.R32(cb + 0x20));

        Assert.Equal(0x12345678u, GuestMemory.R32(cb + 0x30));

        Assert.Equal(SceError.MODULEMGR_NO_MOD, Call(Modmgr, "ksceKernelGetModuleCB", 0x7FFFFF01, cbOut));

    }



}

