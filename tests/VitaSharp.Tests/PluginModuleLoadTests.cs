using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The plugin framework's module calls for user programs (taiLoadStartKernelModuleForUser and the rest of its unsafe
/// library, <see cref="PluginFramework"/>), with the option "Enable Kernel Plugins" on, against a synthetic kernel
/// module whose module_start and module_stop record what they receive: the module loads into the kernel, module_start
/// runs with the copied arguments, the UID comes back, module_stop gets its arguments and *res its status, and the
/// module goes on unload; the framework's checks (system-class caller, the 0x14-byte argument record, 256 bytes of
/// arguments, the path length, opt) answer its error codes. A homebrew eboot calls taiLoadStartKernelModuleForUser to
/// load its own kernel module (Adrenaline's PSPEMUCFW did, and closed at once when the call was missing).
/// </summary>
[Collection("guest")]
public sealed class PluginModuleLoadTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int ErrorInvalidArgs = unchecked((int)0x90010003);
    private const int ErrorUserMemory = unchecked((int)0x90010008);
    private const int ErrorNotAllowed = unchecked((int)0x90010009);

    /// <summary>An unsafe homebrew SELF (vita-make-fself's default authority): the system class.</summary>
    private const ulong UnsafeHomebrew = 0x2F00000000000001UL;

    /// <summary>A safe homebrew SELF (vita-make-fself -s): the game class.</summary>
    private const ulong SafeHomebrew = 0x2F00000000000002UL;

    private const byte StopStatus = 3;

    private HleHarness? _harness;

    public void Dispose() => _harness?.Dispose();

    private HleHarness Harness(bool enabled = true, ulong authority = UnsafeHomebrew)
    {
        _harness = new HleHarness(fixture, "taimod", enabled);
        // a retail application (0x21... authority) is an NPDRM SELF; the rest are homebrew fake SELFs
        bool retail = authority >> 56 == 0x21;
        _harness.Emu.Access = ProgramAccess.Of(authority, fromSystemSoftware: false, retailApplication: retail);
        return _harness;
    }

    /// <summary>
    /// A32 code that records r0 (args), r1 (argp) and the first word at argp in three words after it and returns
    /// <paramref name="status"/>: ldr r2,[r1]; str r0,[pc,#12]; str r1,[pc,#12]; str r2,[pc,#12]; mov r0,#status;
    /// bx lr; then the three words.
    /// </summary>
    private static uint[] Recorder(byte status) =>
        [0xE5912000, 0xE58F000C, 0xE58F100C, 0xE58F200C, 0xE3A00000u | status, 0xE12FFF1E, 0, 0, 0];

    /// <summary>
    /// A32 code that records r0 (args) and r1 (argp) in the buffer whose address is the first word at argp, and returns
    /// <paramref name="status"/>: ldr r2,[r1]; str r0,[r2]; str r1,[r2,#4]; mov r0,#status; bx lr. For a call that also
    /// unloads the module: what it saw must outlive the module's memory, which the unload frees (freed kernel memory
    /// reads zeros on Linux and Android, where the pages are punched out of the shared memory file).
    /// </summary>
    private static uint[] RecorderToArgpBuffer(byte status) =>
        [0xE5912000, 0xE5820000, 0xE5821004, 0xE3A00000u | status, 0xE12FFF1E];

    private sealed record Probe(string Path, SceModuleBuilder Builder, int StartFunction, int StopFunction);

    /// <summary>The synthetic kernel module, written to ux0:app/TAIMOD000/kmod.skprx (an application's own folder);
    /// <paramref name="stopRecordsToArgpBuffer"/>: module_stop is <see cref="RecorderToArgpBuffer"/>.</summary>
    private static Probe WriteProbe(HleHarness harness, string file = "kmod.skprx", byte startStatus = 0, bool stopRecordsToArgpBuffer = false)
    {
        var builder = new SceModuleBuilder("TaiProbeKernel");
        int start = builder.Arm(Recorder(startStatus));
        int stop = builder.Arm(stopRecordsToArgpBuffer ? RecorderToArgpBuffer(StopStatus) : Recorder(StopStatus));
        builder.StartWith(start).StopWith(stop);
        string dir = Path.Combine(harness.Root, "ux0", "app", "TAIMOD000");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), builder.Build());
        return new Probe("ux0:app/TAIMOD000/" + file, builder, start, stop);
    }

    /// <summary>What a recorder of the module saw: (args, argp, first word at argp).</summary>
    private static (uint Args, uint Argp, uint FirstWord) Recorded(LoadedModule module, Probe probe, int function)
    {
        uint slots = probe.Builder.AddressOf(module.Segments[0].Address, function) + 24;
        return (GuestMemory.R32(slots), GuestMemory.R32(slots + 4), GuestMemory.R32(slots + 8));
    }

    /// <summary>tai_module_args_t {size, pid, args, argp, flags} in user memory, argp holding <paramref name="word"/>.</summary>
    private static uint ModuleArgs(HleHarness harness, uint length, uint word, int pid = 0, uint size = 0x14)
    {
        uint argp = harness.Alloc(Math.Max(length, 4u));
        GuestMemory.W32(argp, word);
        uint record = harness.Alloc(0x14);
        GuestMemory.W32(record, size);
        GuestMemory.W32(record + 4, (uint)pid);
        GuestMemory.W32(record + 8, length);
        GuestMemory.W32(record + 0xC, argp);
        GuestMemory.W32(record + 0x10, 0);
        return record;
    }

    private static LoadedModule? KernelModule(HleHarness harness, int uid) =>
        HleHarness.KernelModules(harness.Emu).TryGetValue(uid, out LoadedModule? module) && module.IsKernel ? module : null;

    [Fact]
    public void LoadStartKernelModuleForUser_runs_module_start_with_the_copied_arguments_and_returns_the_uid()
    {
        HleHarness harness = Harness();
        PluginFramework framework = harness.Module<PluginFramework>();
        Probe probe = WriteProbe(harness, stopRecordsToArgpBuffer: true);
        harness.OnGuest(_ =>
        {
            uint args = ModuleArgs(harness, 8, 0xCAFEF00D);
            int uid = Call(framework, "LoadStartKernelModuleForUser", harness.Str(probe.Path), args);
            Assert.True(uid > 0, $"0x{uid:X8}");
            LoadedModule module = KernelModule(harness, uid) ?? throw new Xunit.Sdk.XunitException("no kernel module");
            Assert.True(module.Started);
            (uint length, uint argp, uint word) = Recorded(module, probe, probe.StartFunction);
            Assert.Equal(8u, length);
            Assert.Equal(0xCAFEF00Du, word);
            // module_start reads a kernel copy of argp, not the caller's buffer
            Assert.NotEqual(GuestMemory.R32(args + 0xC), argp);
            Assert.False(KernelSpace.IsUserRange(argp, 8));

            // module_stop gets its own arguments (their first word: where it records them, since the module's memory
            // goes with the unload), *res its status, and the module goes
            uint res = harness.Word(0xFFFFFFFF);
            uint stopRecord = harness.Alloc(8);
            GuestMemory.W32(stopRecord, 0);
            GuestMemory.W32(stopRecord + 4, 0);
            uint stopArgs = ModuleArgs(harness, 4, stopRecord);
            Assert.Equal(0, Call(framework, "StopUnloadKernelModuleForUser", uid, stopArgs, 0u, res));
            Assert.Equal((uint)StopStatus, GuestMemory.R32(res));
            uint stopLength = GuestMemory.R32(stopRecord), stopArgp = GuestMemory.R32(stopRecord + 4);
            Assert.Equal(4u, stopLength);
            Assert.NotEqual(GuestMemory.R32(stopArgs + 0xC), stopArgp);
            Assert.False(KernelSpace.IsUserRange(stopArgp, 4));
            Assert.Null(KernelModule(harness, uid));
        });
    }

    [Fact]
    public void Load_start_stop_and_unload_one_step_at_a_time()
    {
        HleHarness harness = Harness();
        PluginFramework framework = harness.Module<PluginFramework>();
        Probe probe = WriteProbe(harness);
        harness.OnGuest(_ =>
        {
            int uid = Call(framework, "LoadKernelModule", harness.Str(probe.Path), 0, 0u);
            Assert.True(uid > 0, $"0x{uid:X8}");
            LoadedModule module = KernelModule(harness, uid) ?? throw new Xunit.Sdk.XunitException("no kernel module");
            Assert.False(module.Started);

            uint res = harness.Word(0xFFFFFFFF);
            Assert.Equal(0, Call(framework, "StartKernelModuleForUser", uid, ModuleArgs(harness, 16, 0x1234), 0u, res));
            Assert.Equal(0u, GuestMemory.R32(res));
            Assert.Equal((16u, 0x1234u), (Recorded(module, probe, probe.StartFunction).Args, Recorded(module, probe, probe.StartFunction).FirstWord));

            Assert.Equal(0, Call(framework, "StopKernelModuleForUser", uid, ModuleArgs(harness, 0, 0), 0u, res));
            Assert.Equal((uint)StopStatus, GuestMemory.R32(res));
            Assert.NotNull(KernelModule(harness, uid));
            Assert.Equal(0, Call(framework, "UnloadKernelModule", uid, 0, 0u));
            Assert.Null(KernelModule(harness, uid));
            // the handle went with the module
            Assert.Equal(SceError.INVALID_UID, Call(framework, "UnloadKernelModule", uid, 0, 0u));
        });
    }

    [Fact]
    public void A_module_start_that_fails_leaves_nothing_loaded()
    {
        HleHarness harness = Harness();
        PluginFramework framework = harness.Module<PluginFramework>();
        Probe probe = WriteProbe(harness, "fail.skprx", startStatus: 2);
        harness.OnGuest(_ =>
        {
            int uid = Call(framework, "LoadStartKernelModuleForUser", harness.Str(probe.Path), ModuleArgs(harness, 0, 0));
            Assert.True(uid > 0, $"0x{uid:X8}");
            Assert.Null(KernelModule(harness, uid));
        });
    }

    [Fact]
    public void Process_modules_load_and_unload_in_the_process_named_by_the_arguments()
    {
        HleHarness harness = Harness();
        PluginFramework framework = harness.Module<PluginFramework>();
        Probe probe = WriteProbe(harness, "umod.suprx");
        harness.OnGuest(_ =>
        {
            int pid = harness.Emu.Pid;
            int uid = Call(framework, "LoadStartModuleForPidForUser", harness.Str(probe.Path), ModuleArgs(harness, 4, 0xABCD, pid));
            Assert.True(uid > 0, $"0x{uid:X8}");
            Assert.True(harness.Emu.Kernel.Modules.TryGetValue(uid, out LoadedModule? module));
            Assert.False(module!.IsKernel);
            Assert.Equal((4u, 0xABCDu), (Recorded(module, probe, probe.StartFunction).Args, Recorded(module, probe, probe.StartFunction).FirstWord));

            uint res = harness.Word(0xFFFFFFFF);
            Assert.Equal(0, Call(framework, "StopModuleForPidForUser", uid, ModuleArgs(harness, 0, 0, pid), 0u, res));
            Assert.Equal((uint)StopStatus, GuestMemory.R32(res));
            Assert.Equal(0, Call(framework, "UnloadModuleForPid", pid, uid, 0, 0u));
            Assert.False(harness.Emu.Kernel.Modules.ContainsKey(uid));

            int again = Call(framework, "LoadStartModuleForPidForUser", harness.Str(probe.Path), ModuleArgs(harness, 0, 0, pid));
            Assert.Equal(0, Call(framework, "StopUnloadModuleForPidForUser", again, ModuleArgs(harness, 0, 0, pid), 0u, res));
            Assert.False(harness.Emu.Kernel.Modules.ContainsKey(again));
        });
    }

    [Fact]
    public void The_framework_checks_answer_its_error_codes()
    {
        HleHarness harness = Harness();
        PluginFramework framework = harness.Module<PluginFramework>();
        Probe probe = WriteProbe(harness);
        harness.OnGuest(_ =>
        {
            uint path = harness.Str(probe.Path);
            // the argument record: a wrong size, more than 256 bytes of arguments, a record the caller cannot read
            Assert.Equal(ErrorUserMemory, Call(framework, "LoadStartKernelModuleForUser", path, ModuleArgs(harness, 0, 0, size: 0x10)));
            Assert.Equal(ErrorUserMemory, Call(framework, "LoadStartKernelModuleForUser", path, 0u));
            Assert.Equal(ErrorInvalidArgs, Call(framework, "LoadStartKernelModuleForUser", path, ModuleArgs(harness, 257, 0)));
            Assert.Equal(ErrorUserMemory, Call(framework, "LoadStartModuleForPidForUser", path, ModuleArgs(harness, 0, 0, size: 0x18)));
            // a path of 256 characters or more; a file that is not there is the module manager's error
            Assert.Equal(ErrorUserMemory, Call(framework, "LoadStartKernelModuleForUser", harness.Str("ux0:" + new string('a', 300)), ModuleArgs(harness, 0, 0)));
            Assert.Equal(ErrorUserMemory, Call(framework, "LoadKernelModule", harness.Str("ux0:" + new string('a', 300)), 0, 0u));
            Assert.Equal(SceError.MODULEMGR_NOENT, Call(framework, "LoadStartKernelModuleForUser", harness.Str("ux0:app/TAIMOD000/none.skprx"), ModuleArgs(harness, 0, 0)));
            Assert.Equal(ErrorInvalidArgs, Call(framework, "LoadKernelModule", path, 0, 0x100u));

            int uid = Call(framework, "LoadKernelModule", path, 0, 0u);
            uint res = harness.Word(0);
            // taiStartKernelModuleForUser answers a wrong size with INVALID_ARGS and the argument size or opt with
            // USER_MEMORY; the stop calls the other way round
            Assert.Equal(ErrorInvalidArgs, Call(framework, "StartKernelModuleForUser", uid, ModuleArgs(harness, 0, 0, size: 0), 0u, res));
            Assert.Equal(ErrorUserMemory, Call(framework, "StartKernelModuleForUser", uid, ModuleArgs(harness, 257, 0), 0u, res));
            Assert.Equal(ErrorUserMemory, Call(framework, "StartKernelModuleForUser", uid, ModuleArgs(harness, 0, 0), 4u, res));
            Assert.Equal(ErrorUserMemory, Call(framework, "StopKernelModuleForUser", uid, ModuleArgs(harness, 0, 0, size: 0), 0u, res));
            Assert.Equal(ErrorInvalidArgs, Call(framework, "StopKernelModuleForUser", uid, ModuleArgs(harness, 257, 0), 0u, res));
            Assert.Equal(ErrorInvalidArgs, Call(framework, "StopUnloadKernelModuleForUser", uid, ModuleArgs(harness, 0, 0), 4u, res));
            Assert.Equal(ErrorInvalidArgs, Call(framework, "UnloadKernelModule", uid, 0, 4u));
            Assert.Equal(ErrorInvalidArgs, Call(framework, "UnloadModuleForPid", 0, uid, 0, 4u));
            // a handle the caller does not have
            Assert.Equal(SceError.INVALID_UID, Call(framework, "StopKernelModuleForUser", 0x7FFFFF01, ModuleArgs(harness, 0, 0), 0u, res));
            Assert.Equal(SceError.INVALID_UID, Call(framework, "UnloadKernelModule", 0x7FFFFF01, 0, 0u));
            Assert.Equal(0, Call(framework, "UnloadKernelModule", uid, 0, 0u));
        });
    }

    /// <summary>Only a program with the system class may use them (ksceSblACMgrIsShell): safe homebrew and retail
    /// games get 0x90010009 and nothing loads.</summary>
    [Theory]
    [InlineData(SafeHomebrew)]
    [InlineData(0x2100000000000000UL)]
    public void A_program_without_the_system_class_is_not_allowed(ulong authority)
    {
        HleHarness harness = Harness(authority: authority);
        PluginFramework framework = harness.Module<PluginFramework>();
        Probe probe = WriteProbe(harness);
        int modules = HleHarness.KernelModules(harness.Emu).Count;
        uint path = harness.Str(probe.Path);
        uint args = ModuleArgs(harness, 0, 0);
        uint res = harness.Word(0);
        Assert.Equal(ErrorNotAllowed, Call(framework, "LoadStartKernelModuleForUser", path, args));
        Assert.Equal(ErrorNotAllowed, Call(framework, "LoadStartModuleForPidForUser", path, args));
        Assert.Equal(ErrorNotAllowed, Call(framework, "LoadKernelModule", path, 0, 0u));
        Assert.Equal(ErrorNotAllowed, Call(framework, "StartKernelModuleForUser", 1, args, 0u, res));
        Assert.Equal(ErrorNotAllowed, Call(framework, "StopKernelModuleForUser", 1, args, 0u, res));
        Assert.Equal(ErrorNotAllowed, Call(framework, "StopUnloadKernelModuleForUser", 1, args, 0u, res));
        Assert.Equal(ErrorNotAllowed, Call(framework, "StopModuleForPidForUser", 1, args, 0u, res));
        Assert.Equal(ErrorNotAllowed, Call(framework, "StopUnloadModuleForPidForUser", 1, args, 0u, res));
        Assert.Equal(ErrorNotAllowed, Call(framework, "UnloadKernelModule", 1, 0, 0u));
        Assert.Equal(ErrorNotAllowed, Call(framework, "UnloadModuleForPid", 0, 1, 0, 0u));
        Assert.Equal(ErrorNotAllowed, Call(framework, "MemcpyUserToKernel", 0u, 0u, 0u));
        Assert.Equal(ErrorNotAllowed, Call(framework, "MemcpyKernelToUser", 0u, 0u, 0u));
        Assert.Equal(modules, HleHarness.KernelModules(harness.Emu).Count);
    }

    [Fact]
    public void User_kernel_copies_go_through_the_kernel_copy_checks()
    {
        HleHarness harness = Harness();
        PluginFramework framework = harness.Module<PluginFramework>();
        uint kernel;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            kernel = harness.Emu.Memory.Alloc(0x10, "taimod:kernel", MemoryRegion.Kernel);
        }

        uint user = harness.Word(0x11223344);
        uint back = harness.Word(0);
        Assert.Equal(0, Call(framework, "MemcpyUserToKernel", kernel, user, 4u));
        Assert.Equal(0x11223344u, GuestMemory.R32(kernel));
        Assert.Equal(0, Call(framework, "MemcpyKernelToUser", back, kernel, 4u));
        Assert.Equal(0x11223344u, GuestMemory.R32(back));
        // the user side must be the caller's memory
        Assert.Equal(KernelSpace.UserCopyFault, Call(framework, "MemcpyUserToKernel", back, kernel, 4u));
        harness.Emu.Memory.Free(kernel);
    }

    /// <summary>With the option off the pure firmware runs: the calls do nothing, as imports nothing provides.</summary>
    [Fact]
    public void With_the_option_off_nothing_loads()
    {
        HleHarness harness = Harness(enabled: false);
        PluginFramework framework = harness.Module<PluginFramework>();
        Probe probe = WriteProbe(harness);
        int modules = HleHarness.KernelModules(harness.Emu).Count;
        Assert.Equal(0, Call(framework, "LoadStartKernelModuleForUser", harness.Str(probe.Path), ModuleArgs(harness, 0, 0)));
        Assert.Equal(modules, HleHarness.KernelModules(harness.Emu).Count);
    }

    /// <summary>ksceSblACMgrIsShell (SceSblACMgrForDriver's ksceSblACMgrIsSystemProgram) answers capability bit 1 as
    /// its SceSblACMgrForKernel twin does: unsafe homebrew has the system class, safe homebrew does not.</summary>
    [Theory]
    [InlineData(UnsafeHomebrew, 1)]
    [InlineData(SafeHomebrew, 0)]
    public void IsShell_answers_the_program_class_as_its_kernel_twin(ulong authority, int expected)
    {
        HleHarness harness = Harness(authority: authority);
        Assert.Equal(expected, Call(harness.Module<ProcessmgrKernel>(), "ksceSblACMgrIsSystemProgram", 0));
        Assert.Equal(expected, Call(harness.Module<SystemServicesVsh>(), "ksceSblACMgrIsSystemProgram2", 0));
    }
}
