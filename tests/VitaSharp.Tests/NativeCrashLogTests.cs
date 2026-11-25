using System.Runtime.InteropServices;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// A native crash leaves a FATAL line in the log and a clear exit code (<see cref="NativeCrashLog"/>). Headless runs from
/// a state died with nothing in the log (native heap corruption from a driver's thread-exit callback, 25/11/2026): only
/// the Windows Application Error event told what happened. The front-end runs as a child process that crashes on
/// purpose (<see cref="EnvVars.DebugNativeCrash"/>) right after its log opens.
/// </summary>
public sealed class NativeCrashLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-crashlog-" + Guid.NewGuid().ToString("N"));

    public NativeCrashLogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private (DesktopProcess.Result Result, string Log) CrashWith(string kind)
    {
        string log = Path.Combine(_root, kind + ".log");
        var env = new Dictionary<string, string> { [EnvVars.DebugNativeCrash] = kind };
        DesktopProcess.Result r = DesktopProcess.Run(_root, ["run", _root, "--headless", "--frames=1", $"--log={log}"], timeoutMs: 120_000, env: env, softwareVulkan: false);
        return (r, File.Exists(log) ? ReadShared(log) : "");
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Native heap corruption (ntdll raises 0xC0000374 where it detects it, here in RtlFreeHeap like the
    /// driver's callback did): the handler's line with the code, then the detail with the module and the thread's
    /// name, and the process ends with that code.</summary>
    [WindowsX64Fact]
    public void HeapCorruptionLeavesAFatalLineAndItsExitCode()
    {
        (DesktopProcess.Result r, string log) = CrashWith("heap");
        Assert.Contains("ending the process with a native crash", log);
        Assert.Contains("FATAL native exception 0xC0000374 (native heap corruption)", log);
        Assert.Contains("exit code 0xC0000374", log);
        Assert.Matches(@"FATAL native exception detail: ntdll\.dll\+0x[0-9A-F]+ on thread \d+ 'crash self-test'", log);
        Assert.DoesNotContain("did not crash", log);
        Assert.Equal(unchecked((int)0xC0000374), r.ExitCode);
    }

    [Fact]
    public void TheLineNamesTheCodeTheModuleAndTheThread()
    {
        string line = NativeCrashLog.FormatLine(0xC0000374, 0x7FFA80812000 + 0x6C, @"C:\drivers\igc64.dll", 0x7FFA80812000, 0x49A0, "save-state steps");
        Assert.Equal("FATAL native exception 0xC0000374 (native heap corruption) at igc64.dll+0x6C on thread 18848 'save-state steps': the process ends with exit code 0xC0000374 (Windows Error Reporting records it as an Application Error)", line);
        Assert.Equal("access violation", NativeCrashLog.Describe(0xC0000005));
        Assert.Equal("stack overflow", NativeCrashLog.Describe(0xC00000FD));
        Assert.Equal("native exception", NativeCrashLog.Describe(0xE0000001));
    }

    /// <summary>Off Windows x64 nothing is installed (the runtime owns the fault signals there); the Windows x64 case
    /// is the child-process test above (the test host itself keeps its own handlers).</summary>
    [Fact]
    public void NothingIsInstalledOffWindowsX64()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return;
        }

        Assert.False(NativeCrashLog.Install());
        Assert.False(NativeCrashLog.Installed);
    }
}

/// <summary>[Fact] for Windows x64 only (<see cref="NativeCrashLog"/>'s filters are x64 machine code and Windows APIs).</summary>
public sealed class WindowsX64FactAttribute : FactAttribute
{
    public WindowsX64FactAttribute()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Skip = "native crash filters exist on Windows x64 only";
        }
    }
}
