using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The rest of SceProcessmgrForKernel/ForDriver of FW 3.74 processmgr (decompiled, study only) and the module
/// manager's library database: the checks of creating, starting and ending a process, the debugging pairs, the
/// coredump handler, the budget objects and the library and stub lists built from the loaded modules.
/// </summary>
[Collection("guest")]
public sealed class KernelProcessLifecycleTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80020005), KernelProcess = unchecked((int)0x80029002),
        StateError = unchecked((int)0x80029004), IllegalOptionSize = unchecked((int)0x80020009),
        NotAllowed = unchecked((int)0x80029008), CoredumpRegistered = unchecked((int)0x800A0002),
        CoredumpNotRegistered = unchecked((int)0x800A0001), IllegalSize = unchecked((int)0x8002000B),
        NoSys = unchecked((int)0x80010058), BootfsMounted = unchecked((int)0x8002D021);

    private readonly HleHarness _harness = new(fixture, "kproclife");

    public void Dispose() => _harness.Dispose();

    private ProcessmgrKernel Processmgr => _harness.Module<ProcessmgrKernel>();

    private ModulemgrKernel Modulemgr => _harness.Module<ModulemgrKernel>();

    [Fact]
    public void CreatingAProcessChecksItsArguments()
    {
        uint title = _harness.Str("PCSX00000");
        uint path = _harness.Str("ux0:app/PCSX00000/eboot.bin");
        uint option = _harness.Alloc(0x40);
        Assert.Equal(InvalidArgument, Call(Processmgr, "ksceKernelCreateProcess", 0u, 0x1000000u, path, 0u));
        Assert.Equal(KernelProcess, Call(Processmgr, "ksceKernelCreateProcess", title, 0x3000000u, path, 0u));
        GuestMemory.W32(option, 0x20);
        Assert.Equal(IllegalOptionSize, Call(Processmgr, "ksceKernelCreateProcess", title, 0x1000000u, path, option));
        Assert.Equal(SceError.MODULEMGR_NOENT, Call(Processmgr, "ksceKernelCreateProcess", title, 0x1000000u, path, 0u));
    }

    /// <summary>NID_1E5B38E5 / NID_5A57766C launch a process (processmgr 3.74 0x81004b48): the creation's checks
    /// with the type's low 31 bits (bit 31 is the start's), so a title ID of 0, a kernel type, a short option and a
    /// missing executable give the creation's errors. They were taken for an exit by process ID (a title ID pointer
    /// as a process: 0x80020005 or a process error for every call).</summary>
    [Theory]
    [InlineData("NID_1E5B38E5")]
    [InlineData("NID_5A57766C")]
    public void TheLaunchCreatesAndStartsWithTheCreationsChecks(string entry)
    {
        uint title = _harness.Str("PCSX00000");
        uint path = _harness.Str("ux0:app/PCSX00000/eboot.bin");
        uint option = _harness.Alloc(0x40);
        GuestMemory.W32(option, 0x20);
        Assert.Equal(InvalidArgument, Call(Processmgr, entry, 0u, 0x1000000u, path, 0u, 0u, 0u, 0u));
        Assert.Equal(KernelProcess, Call(Processmgr, entry, title, 0x3000000u, path, 0u, 0u, 0u, 0u));
        Assert.Equal(IllegalOptionSize, Call(Processmgr, entry, title, 0x1000000u, path, 0u, 0u, option, 0u));
        Assert.Equal(SceError.MODULEMGR_NOENT, Call(Processmgr, entry, title, 0x1000000u, path, 0u, 0u, 0u, 0u));
        // bit 31 of the type goes to the start, not to the creation's type check
        Assert.Equal(SceError.MODULEMGR_NOENT, Call(Processmgr, entry, title, 0x81000000u, path, 0u, 0u, 0u, 0u));
        GuestMemory.W32(option, 0x40);
        Assert.Equal(SceError.MODULEMGR_NOENT, Call(Processmgr, entry, title, 0x1000000u, path, 0u, 0u, option, 0u));
    }

    [Fact]
    public void ARunningProcessIsNotStartedAgainAndTheKernelsIsNotEnded()
    {
        int pid = _harness.Emu.Pid;
        Assert.Equal(StateError, Call(Processmgr, "NID_5137B783", pid, 0u, 0u, 0u));
        Assert.Equal(KernelProcess, Call(Processmgr, "NID_4232517A", KernelSpace.KernelPid));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_1400A1F8", pid, 4u, 0u));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_0F23A5ED", pid, 3, new Ptr<uint>(0)));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_BC110987", pid, 0x10));
        // the process list lock is taken and given back
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Processmgr, "NID_8321B827", 0u));
            Assert.Equal(0, Call(Processmgr, "NID_D5DF895D"));
        });
    }

    [Fact]
    public void TheDebuggingPairsNeedTheDebuggingConsole()
    {
        int pid = _harness.Emu.Pid;
        Assert.Equal(NotAllowed, Call(Processmgr, "NID_597E6D2C", pid, 0u, 1u, 2u));
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        Invoke(services, "ksceKernelSetDipsw", 0xE4u);
        try
        {
            Assert.Equal(InvalidArgument, Call(Processmgr, "NID_597E6D2C", pid, 5u, 1u, 2u));
            Assert.Equal(0, Call(Processmgr, "NID_597E6D2C", pid, 2u, 0x11u, 0x22u));
            uint first = _harness.Alloc(4);
            uint second = _harness.Alloc(4);
            Assert.Equal(0, Call(Processmgr, "NID_060DA466", pid, 2u, first, second));
            Assert.Equal(0x11u, GuestMemory.R32(first));
            Assert.Equal(0x22u, GuestMemory.R32(second));
            Assert.Equal(0, Call(Processmgr, "NID_B2421F93", pid, 1u, 0x33u, 0x40u));
            Assert.Equal(0, Call(Processmgr, "NID_9DF739C7", pid, 1u));
            Assert.Equal(0, Call(Processmgr, "NID_6898F7A7", pid, 1u, first, second));
            Assert.Equal(0x41u, GuestMemory.R32(second));
            Assert.Equal(0, Call(Processmgr, "NID_A2EE1B7F", pid, 1u));
            Assert.Equal(0, Call(Processmgr, "NID_6898F7A7", pid, 1u, first, second));
            Assert.Equal(0x40u, GuestMemory.R32(second));
        }
        finally
        {
            Invoke(services, "ksceKernelClearDipsw", 0xE4u);
        }
    }

    [Fact]
    public void TheCoredumpHandlerIsRegisteredOnce()
    {
        _harness.OnGuest(_ =>
        {
            Assert.Equal(IllegalSize, Call(Processmgr, "NID_21A6F0EC", 0x81000001u, 0x800u, 0u));
            Assert.Equal(CoredumpNotRegistered, Call(Processmgr, "NID_33B3D026"));
            Assert.Equal(0, Call(Processmgr, "NID_21A6F0EC", 0x81000001u, 0x1000u, 0u));
            Assert.Equal(CoredumpRegistered, Call(Processmgr, "NID_21A6F0EC", 0x81000001u, 0x1000u, 0u));
            Assert.Equal(0, Call(Processmgr, "NID_33B3D026"));
        });
    }

    [Fact]
    public void BudgetObjectsNeedTheKernelsPermissions()
    {
        uint name = _harness.Str("kbudget");
        uint info = _harness.Alloc(0x480);
        GuestMemory.W32(info, 0x18);
        Assert.Equal(NoSys, Call(Processmgr, "NID_DC18FCEF", name, 0x1000000u, info));
        GuestMemory.W32(info, 0x28);
        Assert.Equal(0, Call(Processmgr, "NID_C5CF15ED", 3u, info));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_C5CF15ED", 0x20u, info));
        Assert.Equal(0u, (uint)Invoke(Processmgr, "NID_923AF3CC", 9u)!);
    }

    [Fact]
    public void TheLibraryDatabaseListsTheLoadedModulesLibraries()
    {
        int pid = KernelSpace.KernelPid;
        uint count = _harness.Alloc(4);
        int libraries = Call(Modulemgr, "ksceKernelGetLibraryList", pid, 0u, count);
        if (libraries <= 0)
        {
            return; // no kernel module without an installed firmware
        }

        uint list = _harness.Alloc((uint)libraries * 4);
        GuestMemory.W32(count, (uint)libraries);
        Assert.Equal(0, Call(Modulemgr, "ksceKernelGetLibraryList", pid, list, count));
        Assert.Equal((uint)libraries, GuestMemory.R32(count));
        int first = (int)GuestMemory.R32(list);
        // the same UID the second time
        Assert.Equal(0, Call(Modulemgr, "ksceKernelGetLibraryList", pid, list, count));
        Assert.Equal(first, (int)GuestMemory.R32(list));
        uint info = _harness.Alloc(0x120);
        Assert.Equal(0, Call(Modulemgr, "ksceKernelGetLibraryInfoForDebugger", pid, first, info));
        Assert.Equal((uint)first, GuestMemory.R32(info + 4));
        Assert.Equal(unchecked((int)0x8002D003), Call(Modulemgr, "ksceKernelGetLibraryInfoForDebugger", pid, 0x7FFF0001, info));
    }

    [Fact]
    public void TheBootFileSystemMountsOnce()
    {
        Assert.Equal(0, Call(Modulemgr, "ksceKernelMountBootfs", 0u));
        Assert.Equal(BootfsMounted, Call(Modulemgr, "ksceKernelMountBootfs", 0u));
        Assert.Equal(0, Call(Modulemgr, "ksceKernelUmountBootfs"));
        Assert.Equal(0, Call(Modulemgr, "ksceKernelMountBootfs", 0u));
        Assert.Equal(0, Call(Modulemgr, "ksceKernelFinalizeKbl"));
        Assert.Equal(InvalidArgument, Call(Modulemgr, "NID_353821B2", 0));
    }
}
