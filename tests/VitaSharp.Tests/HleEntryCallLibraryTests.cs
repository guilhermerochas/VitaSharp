using System.Reflection;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// One internal path per migrated library: an HLE function that, as the firmware module does, calls another exported
/// function of the firmware, and a hook on that function's entry that sees the call. On the Vita a hook patches the
/// function's code, so the module's own call runs it; before the generated wrappers the HLE called the C# method and
/// the hook never ran. Kernel functions are hooked as a kernel plugin does, on their entry in the firmware kernel
/// modules (a synthetic os0:kd module exporting the function under its library); user library functions as a user
/// plugin does, in the process (the hook's thunk takes the function). The hook records the call and returns a
/// sentinel without running the original, so the caller's result shows the hook's answer reached it.
/// </summary>
[Collection("guest")]
public sealed class HleEntryCallLibraryTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint Sentinel = 0x0000_7A11;

    private readonly HleHarness _h = new(fixture, "hle-entry-lib");

    public void Dispose() => _h.Dispose();

    public enum Route
    {
        /// <summary>A kernel plugin's patch of the function's entry in the firmware kernel modules.</summary>
        KernelEntry,

        /// <summary>A user plugin's hook on the function in the calling process.</summary>
        UserProcess,
    }

    /// <summary>(library, caller module, caller, its arguments, callee module, callee, route).</summary>
    public static TheoryData<string, string, string, object[], string, string, Route> Paths => new()
    {
        { "SceSysmemForDriver", nameof(SysmemKernel), "ksceKernelVAtoPABySW", [0x8100_0000u, 0u], nameof(SysmemKernel), "ksceKernelVAtoPA", Route.KernelEntry },
        { "SceThreadmgrForDriver", nameof(ThreadmgrKernel), "NID_DA1544D1", [0, 0x40], nameof(ThreadmgrKernel), "ksceKernelChangeThreadPriority", Route.KernelEntry },
        { "SceModulemgrForKernel", nameof(ModulemgrKernel), "ksceKernelLoadProcessImage", [0x10005, 0u, 0u, 0u, 0u, 0u], nameof(ModulemgrKernel), "ksceKernelGetModuleIdByPid", Route.KernelEntry },
        { "SceProcessmgrForDriver", nameof(ProcessmgrKernel), "NID_5468892A", [0x100u, 0u], nameof(ProcessmgrKernel), "ksceKernelAllocRemoteProcessHeap", Route.KernelEntry },
        { "SceIofilemgr", nameof(IoFileMgr), "ksceIoSyncByFd2", [3, 0], nameof(IoFileMgr), "sceIoSyncByFd", Route.KernelEntry },
        { "SceNpDrm", nameof(SystemServicesVsh), "PsmDrmGetRifKey", [0u, 0u, 0u, 0u, 0u], nameof(SystemServicesVsh), "ksceSblACMgrIsPSMRuntime", Route.KernelEntry },
        { "SceThreadmgr", nameof(ThreadMgr), "sceKernelWaitThreadEnd_089", [0x1234, default(Ptr<int>), default(Ptr<uint>)], nameof(ThreadMgr), "sceKernelWaitThreadEnd", Route.KernelEntry },
        { "SceAppMgrUser", nameof(AppMgrShell), "sceAppMgrAcquireSoundOutExclusive3", [0, 0, 0, 0, 0], nameof(AppMgrShell), "sceAppMgrAcquireSoundOutExclusive2", Route.UserProcess },
        { "SceLibc", nameof(Libc), "strcoll", [0u, 0u], nameof(Libc), "strcmp", Route.UserProcess },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public void AHookOnTheFunctionSeesTheModulesOwnCall(string library, string callerModule, string caller, object[] args, string calleeModule, string callee, Route route)
    {
        HleModule callerInstance = ModuleNamed(callerModule);
        HleModule calleeInstance = ModuleNamed(calleeModule);
        (uint Nid, string? Library)[] exports = ExportOf(calleeInstance, callee).Resolved;
        Assert.NotEmpty(exports);
        uint nid = exports[0].Nid;
        var seen = new List<uint>();
        uint hook = _h.Emu.Memory.Alloc(16, "entry library test hook");
        Linker.WriteHleStub(hook, _h.Emu.Hle.RegisterHostFunction("entrylibtest:hook", context =>
        {
            seen.Add(context.Gpr(0));
            context.Return(Sentinel);
        }), 0);
        _h.Emu.Code.Invalidate(hook, 16);
        Core.Firmware.FirmwareKernelImage.Forget(_h.Root);
        try
        {
            uint entry = 0;
            if (route == Route.KernelEntry)
            {
                const uint TestLibNid = 0x7E57_2001;
                var module = new SceModuleBuilder("SceEntryLibraryProbe");
                int body = module.ReturnConstant(0);
                module.Export(exports[0].Library ?? library, TestLibNid, 0x0001, (nid, body));
                string kd = Path.Combine(_h.Root, "os0", "kd");
                Directory.CreateDirectory(kd);
                File.WriteAllBytes(Path.Combine(kd, "entrylibrary.skprx"), module.Build());
                Assert.True(_h.Emu.FirmwareKernel.Load() > 0);
                entry = _h.Emu.FirmwareKernel.EntryOf(TestLibNid, nid);
                Assert.NotEqual(0u, entry);
            }

            _h.OnGuest(_ =>
            {
                if (route == Route.KernelEntry)
                {
                    Linker.WriteJumpStub(entry, hook);
                    _h.Emu.InvalidateCode(entry, 16);
                }
                else
                {
                    _h.Emu.Hle.RedirectInProcess(0x7E57_2002, nid, hook);
                }

                HleHarness.Invoke(callerInstance, caller, args);
                Assert.True(seen.Count >= 1, $"{library}: {caller} called {callee} without its hook");
            });
        }
        finally
        {
            Core.Firmware.FirmwareKernelImage.Forget(_h.Root);
        }
    }

    private HleModule ModuleNamed(string name)
    {
        return _h.Emu.Hle.Modules.Single(m => m.GetType().Name == name);
    }

    private static HleExportRef ExportOf(HleModule module, string method)
    {
        FieldInfo field = module.GetType().GetField("s_hleEntry_" + method, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"{module.GetType().Name}.{method} has no wrapper");
        return (HleExportRef)field.GetValue(null)!;
    }
}
