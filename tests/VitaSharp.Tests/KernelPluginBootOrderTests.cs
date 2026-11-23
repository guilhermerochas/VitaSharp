using System.Buffers.Binary;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using VitaSharp.Core.Package.Pfs;
using VitaSharp.Core.Util;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The order of the system's boot and an application's launch (FW 3.74 SceAppMgr, study only; docs/notes/plugins.md):
/// the kernel and the plugin configuration's <c>*KERNEL</c> modules are up at the system's boot, before any
/// application, whatever the title; the application manager then launches the title: it finds the licence, reads it
/// with the kernel's I/O (ksceIoOpen, ksceIoRead, ksceIoClose), takes the title key (SceNpDrm, the secure processor)
/// and mounts app0 through ScePfsMgr. The emulator stopped a PSM title's launch at app0's key (0x80020004) before any
/// kernel plugin had loaded: the log showed no plugin line at all (23/11/2026). Without a secure processor the launch
/// still fails with 0x80020004 and its message; a kernel hook on the launch's path is called and changes nothing of it.
/// Synthetic kernel modules (stand-ins named as two common kernel plugin files) and a synthetic PFS title.
/// </summary>
[Collection("guest")]
public sealed class KernelPluginBootOrderTests : IDisposable
{
    private const string PsmTitle = "PFSB00001", RuntimeId = "PFSQ00001";
    private const uint IofilemgrForDriver = 0x40FD29C7, KernelIoOpen = 0x75192972, KernelIoRead = 0xE17EFC03, KernelIoClose = 0xF99DD8A3;
    private static readonly byte[] s_imageKey = Encoding.ASCII.GetBytes("boot-order-key!!");
    private readonly GuestMemoryFixture _fixture;
    private HleHarness _harness = null!;
    private Emulator _emu = null!;
    private readonly CollectingSink _sink = new();
    private readonly ILogSink? _savedSink;

    private sealed class CollectingSink : ILogSink
    {
        private readonly List<string> _messages = [];

        public void Write(LogLevel level, string thread, string message)
        {
            lock (_messages)
            {
                _messages.Add(message);
            }
        }

        public List<string> Messages()
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }

        /// <summary>The index of the first message containing <paramref name="part"/> (-1: none).</summary>
        public int IndexOf(string part) => Messages().FindIndex(m => m.Contains(part, StringComparison.Ordinal));
    }

    public KernelPluginBootOrderTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _fixture = fixture;
        _savedSink = Log.Sink;
        Log.Sink = _sink;
    }

    /// <summary>The system of the test, with the emulator option "Enable Kernel Plugins" as given.</summary>
    private void Boot(bool kernelPlugins = true)
    {
        _harness = new HleHarness(_fixture, "kpboot", kernelPlugins: kernelPlugins);
        _emu = _harness.Emu;
    }

    public void Dispose()
    {
        Log.Sink = _savedSink;
        if (_harness == null)
        {
            return;
        }

        _emu.Stop();
        FirmwareKernelImage.Forget(_harness.Root);
        _harness.Dispose();
        string views = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_harness.Root)))!, "cache", "pfs");
        if (!Directory.Exists(views))
        {
            return;
        }

        foreach (string view in Directory.GetDirectories(views))
        {
            if (Path.GetFileName(view).StartsWith(RuntimeId + "-", StringComparison.Ordinal))
            {
                Directory.Delete(view, true);
            }
        }
    }

    /// <summary>The plugin configuration in the emulator's plugin folder, laid out as a user's: two kernel modules and a
    /// third one in <c>*KERNEL</c>, then user modules for the shell and for the title (which only a started process
    /// loads).</summary>
    private void InstallPlugins(SceModuleBuilder? third = null)
    {
        string tai = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        File.WriteAllBytes(Path.Combine(tai, "kubridge.skprx"), new SceModuleBuilder("VsBootBridge").Start(0).Build());
        File.WriteAllBytes(Path.Combine(tai, "fd_fix.skprx"), new SceModuleBuilder("VsBootFdFix").Start(0).Build());
        File.WriteAllBytes(Path.Combine(tai, "bootorder.skprx"), (third ?? new SceModuleBuilder("VsBootHook").Start(0)).Build());
        Directory.CreateDirectory(_emu.KernelPluginsFolder);
        File.WriteAllText(Path.Combine(_emu.KernelPluginsFolder, "config.txt"),
            "*KERNEL\nur0:tai/kubridge.skprx\nur0:tai/fd_fix.skprx\nur0:tai/bootorder.skprx\n" +
            $"*main\nur0:tai/shell_user.suprx\n*{RuntimeId}\nur0:tai/title_user.suprx\n");
    }

    /// <summary>A PSM title whose own runtime is still a PFS image, with a free licence where the promote stores it.</summary>
    private string InstallPfsTitle()
    {
        string title = Path.Combine(_harness.Root, "ux0", "psm", PsmTitle);
        PfsImageBuilder.Write(Path.Combine(title, "runtime"), s_imageKey,
        [
            new("eboot.bin", SyntheticTitle.Elf(), Encrypted: true),
            new("sce_sys/param.sfo", SyntheticTitle.ParamSfo(RuntimeId), Encrypted: false),
        ]);
        string licenses = Path.Combine(_harness.Root, "ux0", "license", "app", PsmTitle);
        Directory.CreateDirectory(licenses);
        byte[] license = new byte[LicenseTitleKey.LicenseSize];
        BinaryPrimitives.WriteUInt16BigEndian(license, 1);
        BinaryPrimitives.WriteUInt16BigEndian(license.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(license.AsSpan(6), 0xD);
        Encoding.ASCII.GetBytes("IP9100-PFSQ00001_00-BOOTORDER0000000").CopyTo(license, 0x10);
        File.WriteAllBytes(Path.Combine(licenses, "6488b73b912a753a492e2714e9b38bc7.rif"), license);
        return title;
    }

    private bool KernelModuleStarted(string name) =>
        KernelModules(_emu).Values.Any(m => m.IsKernel && m.Name == name && m.Started);

    private List<string> LoadLines() => PluginFramework.HookContextLines(_emu).Where(l => l.StartsWith("plugins: module ", StringComparison.Ordinal)).ToList();

    [Fact]
    public void KernelPluginsLoadAtSystemBootBeforeALaunchWhoseApp0DoesNotMount()
    {
        Boot();
        InstallPlugins();
        string title = InstallPfsTitle();

        TitleKeyException e = Assert.Throws<TitleKeyException>(() => _emu.LoadTitle(title));

        // the launch still fails as the console's does without a secure processor
        Assert.Equal(unchecked((int)0x80020004), e.Error);
        Assert.Contains("0x80020004", e.UserMessage);
        Assert.Null(_emu.MainModule);
        // the kernel plugins were up first, all three, started
        Assert.True(KernelModuleStarted("VsBootBridge"), "kubridge.skprx is not loaded after the failed launch");
        Assert.True(KernelModuleStarted("VsBootFdFix"), "fd_fix.skprx is not loaded after the failed launch");
        Assert.True(KernelModuleStarted("VsBootHook"));
        int bridge = _sink.IndexOf("kernel plugin ur0:tai/kubridge.skprx: loaded and started (VsBootBridge)");
        int fdFix = _sink.IndexOf("kernel plugin ur0:tai/fd_fix.skprx: loaded and started (VsBootFdFix)");
        int boot = _sink.IndexOf("kernel plugins: 3 of 3 loaded and started at system boot, before any application is launched");
        int failure = _sink.IndexOf($"{RuntimeId}: the launch stopped at app0's mount, step 3 of 4 (title key");
        Assert.True(bridge >= 0 && fdFix > bridge && boot > fdFix, string.Join("\n", _sink.Messages()));
        Assert.True(failure > boot, string.Join("\n", _sink.Messages()));
        // the recording's plugin lines (the F8 capture's "Plugins") name each kernel plugin and when it loaded
        List<string> lines = LoadLines();
        Assert.Contains(lines, l => l.Contains("ur0:tai/kubridge.skprx\tkernel\tVsBootBridge", StringComparison.Ordinal) && l.EndsWith("\tsystem boot", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("ur0:tai/fd_fix.skprx\tkernel\tVsBootFdFix", StringComparison.Ordinal) && l.EndsWith("\tsystem boot", StringComparison.Ordinal));
        // the title's process never existed: its user plugins never loaded
        Assert.DoesNotContain(lines, l => l.Contains("title_user.suprx", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("shell_user.suprx", StringComparison.Ordinal));
    }

    /// <summary>The boot's kernel plugins do not depend on the title: one that cannot even start (no PSM runtime
    /// installed) and one that runs both find them loaded, before the executable is.</summary>
    [Fact]
    public void KernelPluginsLoadAtBootWhateverTheTitle()
    {
        Boot();
        InstallPlugins();
        string psm = Path.Combine(_harness.Root, "ux0", "psm", PsmTitle);
        Directory.CreateDirectory(Path.Combine(psm, "RO"));
        File.WriteAllBytes(Path.Combine(psm, "RO", "Application"), []);

        Assert.ThrowsAny<Exception>(() => _emu.LoadTitle(psm));
        Assert.True(KernelModuleStarted("VsBootBridge"));
        Assert.True(KernelModuleStarted("VsBootFdFix"));

        _emu.LoadTitle(SyntheticTitle.Install(_harness.Root));
        Assert.NotNull(_emu.MainModule);
        // loaded once, at the boot: the launch that works did not load them again
        Assert.Single(KernelModules(_emu).Values, m => m.IsKernel && m.Name == "VsBootBridge");
        Assert.True(_sink.IndexOf("kernel plugin ur0:tai/kubridge.skprx: loaded and started") < _sink.IndexOf("Loading VSTS00001"));
    }

    /// <summary>A kernel hook on the kernel's file open (an export of SceIofilemgrForDriver the HLE serves) is called by
    /// the launch's licence read, continues to the original, and the launch fails with the same error and message.</summary>
    [Fact]
    public void AKernelHookOnTheLaunchsLicenceReadIsCalledAndTheErrorStaysTheSame()
    {
        Boot();
        var iofilemgr = new SceModuleBuilder("SceIofilemgr");
        int body = iofilemgr.ReturnConstant(0);
        iofilemgr.Export("SceIofilemgrForDriver", IofilemgrForDriver, 0x0001, (KernelIoOpen, body), (KernelIoRead, body), (KernelIoClose, body));
        string kd = Path.Combine(_harness.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "iofilemgr.skprx"), iofilemgr.Build());
        FirmwareKernelImage.Forget(_harness.Root);
        string title = InstallPfsTitle();
        string expected = new TitleKeyException(RuntimeId, LicenseTitleKey.SecureProcessorUnavailable).UserMessage;

        uint hookRef = KernelWord();
        SceModuleBuilder hooks = new SceModuleBuilder("VsBootHook").Start(0);
        int continuing = hooks.Arm(PluginHookContinuation(hookRef));
        InstallPlugins(hooks);
        Assert.Equal(3, _emu.BootKernelPlugins().Count);
        Emulator kernel = KernelSpace.ProcessOf(_emu, KernelSpace.KernelPid)!;
        uint hookFunction = hooks.AddressOf(KernelModules(_emu).Values.Single(m => m.Name == "VsBootHook").Segments[0].Address, continuing);
        int uid = Call(kernel.Hle.Modules.OfType<PluginFramework>().Single(), "HookFunctionExportForKernel", KernelSpace.KernelPid, hookRef,
            _harness.Str("SceIofilemgr"), IofilemgrForDriver, KernelIoOpen, hookFunction);
        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        Assert.True(kernel.FirmwareKernel.IsPatched(IofilemgrForDriver, KernelIoOpen));

        TitleKeyException e = Assert.Throws<TitleKeyException>(() => _emu.LoadTitle(title));

        Assert.Equal(unchecked((int)0x80020004), e.Error);
        Assert.Equal(expected, e.UserMessage);
        Assert.True(_sink.IndexOf("plugin hook 1 called: ") >= 0, string.Join("\n", _sink.Messages()));
        Assert.True(_sink.IndexOf("plugin hook 1 continued to the original: ") >= 0, string.Join("\n", _sink.Messages()));
        Assert.True(_sink.IndexOf("plugin hook 1 called: ") < _sink.IndexOf($"{RuntimeId}: the launch stopped at app0's mount, step 3 of 4"));
    }

    /// <summary>With the option off, the pure firmware: no plugin at all, and the launch fails the same way.</summary>
    [Fact]
    public void WithTheOptionOffNoPluginLoadsAndTheLaunchFailsTheSame()
    {
        Boot(kernelPlugins: false);
        InstallPlugins();
        string title = InstallPfsTitle();

        TitleKeyException e = Assert.Throws<TitleKeyException>(() => _emu.LoadTitle(title));

        Assert.Equal(unchecked((int)0x80020004), e.Error);
        Assert.Contains("0x80020004", e.UserMessage);
        Assert.Equal(-1, _sink.IndexOf("kernel plugin "));
        Assert.Null(_emu.Vita.KernelProcess);
        Assert.True(_sink.IndexOf($"{RuntimeId}: the launch stopped at app0's mount, step 3 of 4 (title key") >= 0);
    }

    private uint KernelWord()
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uint word = _emu.Memory.Alloc(16, "test:hook ref", MemoryRegion.Kernel, 16);
            GuestMemory.W32(word, 0);
            return word;
        }
    }

    /// <summary>A hook that continues as the framework's continuation macro does: the next hook's function, or the
    /// record's old after the last one (A32, r1 kept on the stack, r12 scratch).</summary>
    private static uint[] PluginHookContinuation(uint hookRef) =>
    [
        0xE300C000 | ((hookRef & 0xF000) << 4) | (hookRef & 0xFFF),         // movw ip, #lo(hookRef)
        0xE340C000 | ((hookRef >> 12) & 0xF0000) | ((hookRef >> 16) & 0xFFF), // movt ip, #hi(hookRef)
        0xE59CC000,                                                            // ldr ip, [ip]        the record
        0xE52D1004,                                                            // push {r1}
        0xE59C1000,                                                            // ldr r1, [ip]        next
        0xE3510000,                                                            // cmp r1, #0
        0x1591C004,                                                            // ldrne ip, [r1, #4]  next->func
        0x059CC008,                                                            // ldreq ip, [ip, #8]  old
        0xE49D1004,                                                            // pop {r1}
        0xE12FFF1C,                                                            // bx ip
    ];
}
