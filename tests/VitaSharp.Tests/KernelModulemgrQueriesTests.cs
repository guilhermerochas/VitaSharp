using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceModulemgrForKernel queries and small entries as FW 3.74 modulemgr has them (decompiled, study only): a module's
/// path, entry and fingerprint, the module holding an address (UID, control block, the minimal info, the shared bit),
/// the process's main module, loading and starting a module in a process (their pid and flag checks), the syscall
/// table and the owner and hook words.
/// </summary>
[Collection("guest")]
public sealed class KernelModulemgrQueriesTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int LibraryDbNoMod = unchecked((int)0x8002D082), InvalidProcessUid = unchecked((int)0x8002D017),
        InvalidFlags = unchecked((int)0x8002000A), NoProcessModules = unchecked((int)0x8002D080);

    private const uint Lib = 0x5EC5A121, Fn = 0x55555556;

    private readonly HleHarness _harness = new(fixture, "kmodq");

    public void Dispose() => _harness.Dispose();

    private ModulemgrKernel Modmgr => _harness.Module<ModulemgrKernel>();

    private string Write(string file, byte[] elf)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), elf);
        return "ur0:tai/" + file;
    }

    private static byte[] Module(string name, byte start = 0)
    {
        SceModuleBuilder builder = new SceModuleBuilder(name).Start(start);
        int five = builder.ReturnConstant(5);
        return builder.Export("QueryLib", Lib, 0x0001, (Fn, five)).Build();
    }

    [Fact]
    public void PathEntryAndFingerprintComeFromTheModule()
    {
        string path = Write("q.suprx", Module("QMod"));
        LoadedModule module = _harness.Emu.LoadModule(path);
        uint text = _harness.Alloc(0x100);
        GuestMemory.Fill(text, 0xEE, 0x100);
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModulePath", module.Uid, text, 0x100u));
        Assert.Equal(module.Path, GuestMemory.ReadCString(text));
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModulePath", module.Uid, text, 4u)); // strncpy: no terminator
        Assert.Equal(LibraryDbNoMod, Call(Modmgr, "ksceKernelGetModulePath", 0x7FFF0001, text, 0x100u));
        Assert.Equal(module.StartEntry, (uint)Invoke(Modmgr, "ksceKernelGetModuleEntryPoint", module.Uid)!);
        Assert.Equal(0u, (uint)Invoke(Modmgr, "ksceKernelGetModuleEntryPoint", 0x7FFF0001)!);
        uint fingerprint = _harness.Alloc(4);
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleFingerprint", module.Uid, fingerprint));
        Assert.Equal(module.ModuleNid, GuestMemory.R32(fingerprint));
        Assert.Equal(LibraryDbNoMod, Call(Modmgr, "ksceKernelGetModuleFingerprint", 0x7FFF0001, fingerprint));
    }

    [Fact]
    public void TheModuleHoldingAnAddressIsFoundByItsSegments()
    {
        LoadedModule module = _harness.Emu.LoadModule(Write("a.suprx", Module("AddrMod")));
        uint inside = module.Segments[0].Address + 4;
        int pid = _harness.Emu.Pid;
        Assert.Equal(module.Uid, Call(Modmgr, "ksceKernelGetModuleIdByAddrForDebugger", pid, inside));
        Assert.Equal(LibraryDbNoMod, Call(Modmgr, "ksceKernelGetModuleIdByAddrForDebugger", pid, 0x10u));
        uint cb = _harness.Alloc(4);
        uint direct = _harness.Alloc(4);
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleCBByAddr", pid, inside, cb));
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleCB", module.Uid, direct));
        Assert.Equal(GuestMemory.R32(direct), GuestMemory.R32(cb));
        Assert.Equal(LibraryDbNoMod, Call(Modmgr, "ksceKernelGetModuleCBByAddr", pid, 0x10u, cb));
        Assert.Equal(NoProcessModules, Call(Modmgr, "ksceKernelGetModuleCBByAddr", 0x7FFF0001, inside, cb));
        uint modid = _harness.Alloc(4);
        uint baseOut = _harness.Alloc(4);
        uint name = _harness.Alloc(0x20);
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleInfoMinByAddr", pid, inside, modid, baseOut, name));
        Assert.Equal((uint)module.Uid, GuestMemory.R32(modid));
        Assert.Equal(module.Segments[0].Address, GuestMemory.R32(baseOut));
        Assert.Equal("AddrMod", GuestMemory.ReadCString(name));
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleInfoMinByAddr", pid, inside, 0u, 0u, 0u));
        Assert.Equal(LibraryDbNoMod, Call(Modmgr, "ksceKernelGetModuleInfoMinByAddr", 0x7FFF0001, inside, modid, baseOut, name));
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleIsSharedByAddr", pid, inside));
        Assert.Equal(0, Call(Modmgr, "ksceKernelGetModuleIsSharedByAddr", pid, 0x10u));
        Assert.Equal(LibraryDbNoMod, Call(Modmgr, "ksceKernelGetModuleIdByPid", KernelSpace.KernelPid));
    }

    [Fact]
    public void LoadAndStartForPidCheckThePidAndTheFlags()
    {
        string path = Write("p.suprx", Module("PidMod"));
        string failing = Write("f.suprx", Module("PidFail", start: 2));
        int pid = _harness.Emu.Pid;
        Assert.Equal(InvalidProcessUid, Call(Modmgr, "ksceKernelLoadModuleForPid", 0, path, 0u, 0u));
        Assert.Equal(InvalidFlags, Call(Modmgr, "ksceKernelLoadModuleForPid", pid, path, 0x1u, 0u));
        Assert.Equal(InvalidProcessUid, Call(Modmgr, "ksceKernelLoadModuleForPid", 0x7FFF0001, path, 0u, 0u));
        _harness.OnGuest(_ =>
        {
            int uid = Call(Modmgr, "ksceKernelLoadModuleForPid", pid, path, 0u, 0u);
            Assert.True(uid > 0);
            Assert.False(_harness.Emu.Kernel.Modules[uid].Started);
            uint status = _harness.Word(0xFF);
            Assert.Equal(InvalidProcessUid, Call(Modmgr, "ksceKernelStartModuleForPid", 0, uid, 0u, 0u, 0, 0u, status));
            Assert.Equal(InvalidFlags, Call(Modmgr, "ksceKernelStartModuleForPid", pid, uid, 0u, 0u, 1, 0u, status));
            Assert.Equal(0, Call(Modmgr, "ksceKernelStartModuleForPid", pid, uid, 0u, 0u, 0, 0u, status));
            Assert.Equal(0u, GuestMemory.R32(status));
            Assert.True(_harness.Emu.Kernel.Modules[uid].Started);
            int bad = Call(Modmgr, "ksceKernelLoadModuleForPid", pid, failing, 0u, 0u);
            Assert.Equal(0, Call(Modmgr, "ksceKernelStartModuleForPid", pid, bad, 0u, 0u, 0, 0u, status));
            Assert.Equal(2u, GuestMemory.R32(status));
            Assert.False(_harness.Emu.Kernel.Modules.ContainsKey(bad));
        });
    }

    /// <summary>
    /// The other *ForPid exports refuse pid 0 as the FW 3.74 modulemgr does (0x8002D017: it names no process, not the
    /// caller's) and check their flags first. A kernel module that passes the shell's pid while no shell runs (0) got
    /// its user module loaded into the calling application instead (the Adrenaline bubble opened directly, 23/11/2026).
    /// </summary>
    [Fact]
    public void TheOtherForPidExportsRefusePidZeroAndForeignFlags()
    {
        string path = Write("z.suprx", Module("PidZero"));
        int pid = _harness.Emu.Pid;
        _harness.OnGuest(_ =>
        {
            int modules = _harness.Emu.Kernel.Modules.Count;
            Assert.Equal(InvalidProcessUid, Call(Modmgr, "ksceKernelLoadStartModuleForPid", 0, path, 0u, 0u, 0, 0u, 0u));
            Assert.Equal(modules, _harness.Emu.Kernel.Modules.Count); // nothing loaded into the caller
            Assert.Equal(InvalidFlags, Call(Modmgr, "ksceKernelLoadStartModuleForPid", pid, path, 0u, 0u, 0x1, 0u, 0u));
            int uid = Call(Modmgr, "ksceKernelLoadStartModuleForPid", pid, path, 0u, 0u, 0, 0u, 0u);
            Assert.True(uid > 0);

            Assert.Equal(InvalidProcessUid, Call(Modmgr, "ksceKernelStopModuleForPid", 0, uid, 0u, 0u, 0, 0u, 0u));
            Assert.Equal(InvalidFlags, Call(Modmgr, "ksceKernelStopModuleForPid", pid, uid, 0u, 0u, 1, 0u, 0u));
            Assert.Equal(InvalidProcessUid, Call(Modmgr, "ksceKernelUnloadModuleForPid", 0, uid, 0, 0u));
            Assert.Equal(InvalidFlags, Call(Modmgr, "ksceKernelUnloadModuleForPid", pid, uid, 1, 0u));
            Assert.Equal(InvalidProcessUid, Call(Modmgr, "ksceKernelStopUnloadModuleForPid", 0, uid, 0u, 0u, 0, 0u, 0u));
            Assert.Equal(InvalidFlags, Call(Modmgr, "ksceKernelStopUnloadModuleForPid", pid, uid, 0u, 0u, 1, 0u, 0u));
            Assert.True(_harness.Emu.Kernel.Modules.ContainsKey(uid));

            // the force flag is the one the unloads accept
            Assert.Equal(0, Call(Modmgr, "ksceKernelStopUnloadModuleForPid", pid, uid, 0u, 0u, 0x40000000, 0u, 0u));
            Assert.False(_harness.Emu.Kernel.Modules.ContainsKey(uid));
        });
    }

    [Fact]
    public void TheSyscallTableAndTheOwnerAndHookWordsAreKept()
    {
        Invoke(Modmgr, "ksceKernelRegisterSyscall", 0x123u, 0x81001001u);
        Invoke(Modmgr, "ksceKernelRegisterSyscall", 0x1000u, 0x81001003u);
        Assert.Equal(0x81001001u, (uint)Invoke(Modmgr, "Syscall", 0x123u)!);
        Assert.Equal(0u, (uint)Invoke(Modmgr, "Syscall", 0x1000u)!);
        Assert.Equal(unchecked((int)0x8002D01C), Call(Modmgr, "NID_ACBC97C9", 7));
        Assert.Equal(0, Call(Modmgr, "NID_74A4E0EE", 7));
        Assert.Equal(0, Call(Modmgr, "NID_74A4E0EE", 7));
        Assert.Equal(unchecked((int)0x8002D01D), Call(Modmgr, "NID_74A4E0EE", 8));
        Assert.Equal(unchecked((int)0x8002D01C), Call(Modmgr, "NID_ACBC97C9", 8));
        Assert.Equal(0, Call(Modmgr, "NID_ACBC97C9", 7));
        Assert.Equal(0, Call(Modmgr, "NID_74A4E0EE", 8));
        Invoke(Modmgr, "NID_B23286B8", 0x11u, 0x22u);
        Assert.Equal((0x11u, 0x22u), ((uint, uint))Invoke(Modmgr, "get_HookWords")!);
    }
}
