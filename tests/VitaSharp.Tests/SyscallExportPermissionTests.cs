using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// FW 3.74 modulemgr checks a module's export table before linking it: a syscall library (attribute 0x4000, the
/// functions user code reaches through the kernel) is the kernel's to export. A user module that exports one is
/// refused with 0x8002D01E and nothing of it is linked; the same library in a kernel module loads. Before, VitaSharp
/// loaded the user module and served its "syscall" library.
/// </summary>
[Collection("guest")]
public sealed class SyscallExportPermissionTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalExport = unchecked((int)0x8002D01E);
    private const uint LibraryNid = 0x5CA11AB1, FunctionNid = 0x5CA11F01;
    private const ushort SyscallLibrary = 0x4001;

    private readonly HleHarness _harness = new(fixture, "sysexport");

    public void Dispose() => _harness.Dispose();

    private string Write(string folder, string file, SceModuleBuilder builder)
    {
        string dir = Path.Combine(_harness.Root, "ux0", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), builder.Build());
        return $"ux0:{folder}/{file}";
    }

    private static SceModuleBuilder Exporter(string name)
    {
        var builder = new SceModuleBuilder(name);
        int function = builder.ReturnConstant(7);
        return builder.Start(0).Export("SceSyscallLib", LibraryNid, SyscallLibrary, (FunctionNid, function));
    }

    [Fact]
    public void AUserModuleCannotExportASyscallLibrary()
    {
        string path = Write("data", "user.suprx", Exporter("UserSyscalls"));
        Assert.Equal(IllegalExport, Call(_harness.Module<SystemMisc>(), "sceKernelLoadModule", path, 0u, 0u));
        Assert.False(_harness.Emu.Linker.TryGetFunctionExport(FunctionNid, out _));

        string kernelPath = Write("tai", "kernel.skprx", Exporter("KernelSyscalls"));
        LoadedModule module = _harness.Emu.LoadModule(kernelPath, kernel: true);
        Assert.Contains(module.ExportLibraries, l => l.LibNid == LibraryNid);
    }
}
