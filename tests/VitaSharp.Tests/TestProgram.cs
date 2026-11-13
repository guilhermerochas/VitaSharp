using System.Diagnostics;

namespace VitaSharp.Tests;

/// <summary>
/// The test assembly run as a program (<c>dotnet VitaSharp.Tests.dll &lt;mode&gt;</c>): what a test must see in a
/// process of its own, its exit above all. The test host never calls it.
/// </summary>
public static class TestProgram
{
    /// <summary>Mode that loads shaderc the way the tests do (<see cref="ShadercAvailability"/>) and exits.</summary>
    public const string ShadercProbeMode = "shaderc-probe";

    public static int Main(string[] args)
    {
        if (args is [ShadercProbeMode])
        {
            Console.WriteLine(ShadercAvailability.Probe() ?? "shaderc loaded");
            return 0;
        }

        Console.Error.WriteLine($"usage: dotnet VitaSharp.Tests.dll {ShadercProbeMode}");
        return 2;
    }

    public sealed record Result(int ExitCode, string Output);

    /// <summary>Runs this assembly as a program with <paramref name="mode"/>; the output is stdout and stderr.</summary>
    public static Result Run(string mode)
    {
        // the dotnet that runs the tests (set by the dotnet CLI for the processes it starts)
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path ? path : "dotnet";
        var psi = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(typeof(TestProgram).Assembly.Location);
        psi.ArgumentList.Add(mode);
        // a crash of the child must not write a dump (Linux: see build/prepush-check.sh)
        psi.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using Process process = Process.Start(psi)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new Result(process.ExitCode, output + error.Result);
    }
}
