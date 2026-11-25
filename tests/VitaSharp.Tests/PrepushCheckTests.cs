using System.Diagnostics;

namespace VitaSharp.Tests;

/// <summary>
/// build/prepush-check.sh and build/prepush-check.ps1 (the CI's checks run locally before a push) in a scratch git
/// repository that holds the real scripts, a stub format-check and fake dotnet, shellcheck and wsl commands first on
/// the PATH: each script must fail when one check fails (code style, a script without its twin, a 0-warning build, a
/// failing test, a run the test host aborted, fewer tests run than listed) and pass when everything is clean, with the
/// same verdict from both. The aborted run exits 0 on purpose: the WSL leg once printed a summary and stopped at 1,216
/// of 1,951 tests when its test host crashed, so the exit code alone is not the check.
/// </summary>
public abstract class PrepushCheckTests : IDisposable
{
    private int _runs;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-prepush-test-" + Guid.NewGuid().ToString("N"));

    protected PrepushCheckTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            // git marks its object files read-only
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Every case, for both scripts.</summary>
    public static TheoryData<string> Cases => new()
    {
        "clean", "format", "parity", "warning", "failing-test", "aborted-run", "fewer-tests", "no-summary", "tests-only",
    };

    /// <summary>Runs one case with <paramref name="script"/> ("sh" or "ps1") and checks its verdict.</summary>
    protected void Check(string script, string testCase)
    {
        switch (testCase)
        {
            case "clean":
            {
                Result result = Run(script, Scenario.Pass);
                Assert.True(result.ExitCode == 0, result.Output);
                Assert.Contains("prepush-check: tests: 3 tests ran (3 listed)", result.Output);
                Assert.Contains("prepush-check: ok", result.Output);
                break;
            }
            case "format":
            {
                Result result = Run(script, Scenario.Pass, repo => File.WriteAllText(Path.Combine(repo, "src", "Bad.cs"), "// FORMAT-BROKEN\n"));
                AssertFailed(result, "prepush-check: format-check");
                Assert.DoesNotContain("prepush-check: build", result.Output);
                break;
            }
            case "parity":
            {
                Result result = Run(script, Scenario.Pass, repo => File.WriteAllText(Path.Combine(repo, "build", "lonely.sh"), "#!/usr/bin/env bash\necho lonely\n"));
                AssertFailed(result, "prepush-check: script parity");
                Assert.Contains("missing: build/lonely.ps1", result.Output);
                break;
            }
            case "warning":
                AssertFailed(Run(script, Scenario.Warning), "prepush-check: build has warnings");
                break;
            case "failing-test":
                AssertFailed(Run(script, Scenario.Fail), "prepush-check: tests: dotnet test exited with 1");
                break;
            case "aborted-run":
                // the fake exits 0 after the abort: the exit code alone is not the check
                AssertFailed(Run(script, Scenario.Abort), "prepush-check: tests: the test run was aborted");
                break;
            case "fewer-tests":
                AssertFailed(Run(script, Scenario.Short), "prepush-check: tests: 2 tests ran, 3 expected");
                break;
            case "no-summary":
                AssertFailed(Run(script, Scenario.Silent), "prepush-check: tests: no test summary");
                break;
            case "tests-only":
            {
                // the suite alone (what the WSL leg runs inside Linux) checks the run the same way
                Result clean = Run(script, Scenario.Pass, testsOnly: true);
                Assert.True(clean.ExitCode == 0, clean.Output);
                Assert.Contains("prepush-check: build of the tests", clean.Output);
                Assert.DoesNotContain("prepush-check: script parity", clean.Output);
                AssertFailed(Run(script, Scenario.Abort, testsOnly: true), "prepush-check: tests: the test run was aborted");
                AssertFailed(Run(script, Scenario.Short, testsOnly: true), "prepush-check: tests: 2 tests ran, 3 expected");
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null);
        }
    }

    private static void AssertFailed(Result result, string message)
    {
        Assert.True(result.ExitCode != 0, $"exit code 0, expected a failure with \"{message}\":\n{result.Output}");
        Assert.Contains(message, result.Output);
        Assert.DoesNotContain("prepush-check: ok", result.Output);
    }

    protected enum Scenario
    {
        Pass,
        Warning,
        Fail,
        Abort,
        Short,
        Silent,
    }

    protected sealed record Result(int ExitCode, string Output);

    /// <summary>The fake dotnet: the scenario file next to it picks what build and test print and return.</summary>
    private const string FakeDotnet = """
        #!/usr/bin/env bash
        # fake dotnet of PrepushCheckTests: the scenario file next to this script picks the outcome
        scenario="$(cat "$(dirname "$0")/scenario.txt")"
        if [[ "$1" == "build" ]]; then
          if [[ "${scenario}" == "warning" ]]; then
            echo "Bad.cs(1,1): warning CS0168: The variable is declared but never used"
            echo "    1 Warning(s)"
          else
            echo "    0 Warning(s)"
          fi
          echo "    0 Error(s)"
          exit 0
        fi
        for arg in "$@"; do
          if [[ "${arg}" == "--list-tests" ]]; then
            printf 'The following Tests are available:\n    T.A\n    T.B\n    T.C\n'
            exit 0
          fi
        done
        case "${scenario}" in
          fail)
            echo "Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3"
            exit 1
            ;;
          abort)
            echo "The active test run was aborted. Reason: Test host process crashed" >&2
            echo "Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3"
            echo "Test Run Aborted."
            exit 0
            ;;
          short)
            echo "Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2"
            exit 0
            ;;
          silent)
            exit 0
            ;;
          *)
            echo "Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3"
            exit 0
            ;;
        esac

        """;

    private const string FormatStubSh = """
        #!/usr/bin/env bash
        # Stub of build/format-check.sh for PrepushCheckTests: fails when a tracked file outside build/ says FORMAT-BROKEN.
        set -euo pipefail
        if [[ "${1:-}" == "--help" ]]; then
          echo "format-check stub"
          exit 0
        fi
        cd "$(dirname "${BASH_SOURCE[0]}")/.."
        if git grep -q FORMAT-BROKEN -- src; then
          echo "format: src/Bad.cs differs"
          exit 2
        fi
        echo "format: ok"

        """;

    private const string FormatStubPs1 = """
        # Stub of build/format-check.ps1 for PrepushCheckTests: fails when a tracked file outside build/ says FORMAT-BROKEN.
        Set-Location (Join-Path $PSScriptRoot '..')
        git grep -q FORMAT-BROKEN -- src
        if ($LASTEXITCODE -eq 0)
        {
            'format: src/Bad.cs differs'
            exit 2
        }

        'format: ok'

        """;

    private static readonly string[] s_realScripts =
    [
        "build/prepush-check.sh", "build/prepush-check.ps1", "build/check-script-parity.sh", "build/check-script-parity.ps1",
        "build/check-scripts-help.sh", "build/check-scripts-help.ps1",
    ];

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("VitaSharp.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Git Bash on Windows (next to git, never WSL's bash.exe), bash elsewhere.</summary>
    internal static string? Bash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "bash";
        }

        // git --exec-path: <Git>/mingw64/libexec/git-core
        string? execPath = Capture("git", ["--exec-path"])?.Trim();
        if (string.IsNullOrEmpty(execPath))
        {
            return null;
        }

        string git = Path.GetFullPath(Path.Combine(execPath, "..", "..", ".."));
        string bash = Path.Combine(git, "bin", "bash.exe");
        return File.Exists(bash) ? bash : null;
    }

    /// <summary>powershell.exe on Windows, pwsh elsewhere (null when it is not installed).</summary>
    internal static string? PowerShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return "powershell";
        }

        return Capture("pwsh", ["-NoProfile", "-Command", "$PSVersionTable.PSVersion.Major"]) != null ? "pwsh" : null;
    }

    private static string? Capture(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi)!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = error.Result;
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void Git(string repo, params string[] args)
    {
        string[] all = ["-C", repo, "-c", "user.name=prepush-test", "-c", "user.email=prepush-test@example.invalid", "-c", "core.autocrlf=false", .. args];
        Assert.NotNull(Capture("git", all));
    }

    private static void WriteScript(string path, string text)
    {
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>The fake commands, first on the PATH of the script run: dotnet (by scenario), shellcheck (passes), wsl
    /// (no .NET SDK there). Windows: bash scripts for Git Bash and .cmd wrappers for PowerShell, in separate folders.</summary>
    private string FakeTools(Scenario scenario, string bash, bool forPowerShell)
    {
        string lib = Path.Combine(_root, "fakelib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "scenario.txt"), scenario.ToString().ToLowerInvariant() + "\n");
        WriteScript(Path.Combine(lib, "dotnet"), FakeDotnet);
        WriteScript(Path.Combine(lib, "shellcheck"), "#!/usr/bin/env bash\nexit 0\n");
        WriteScript(Path.Combine(lib, "wsl"), "#!/usr/bin/env bash\nexit 0\n");
        if (!OperatingSystem.IsWindows() || !forPowerShell)
        {
            return lib;
        }

        string bin = Path.Combine(_root, "fakecmd");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "dotnet.cmd"), $"@\"{bash}\" \"{Path.Combine(lib, "dotnet")}\" %*\r\n");
        File.WriteAllText(Path.Combine(bin, "shellcheck.cmd"), "@exit /b 0\r\n");
        File.WriteAllText(Path.Combine(bin, "wsl.cmd"), "@exit /b 0\r\n");
        return bin;
    }

    /// <summary>A scratch repository with the real check scripts and a stub format-check, <paramref name="change"/>
    /// applied and committed, then the script run on it.</summary>
    private Result Run(string script, Scenario scenario, Action<string>? change = null, bool testsOnly = false)
    {
        string bash = Bash() ?? throw new InvalidOperationException("no bash");
        string shell = script == "sh" ? bash : PowerShell() ?? throw new InvalidOperationException("no PowerShell");

        string repo = Path.Combine(_root, "repo" + _runs++);
        Directory.CreateDirectory(Path.Combine(repo, "build"));
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        string source = RepoRoot();
        File.Copy(Path.Combine(source, ".gitattributes"), Path.Combine(repo, ".gitattributes"));
        foreach (string file in s_realScripts)
        {
            File.WriteAllText(Path.Combine(repo, file), File.ReadAllText(Path.Combine(source, file)));
        }

        WriteScript(Path.Combine(repo, "build", "format-check.sh"), FormatStubSh);
        File.WriteAllText(Path.Combine(repo, "build", "format-check.ps1"), FormatStubPs1.ReplaceLineEndings("\r\n"));
        File.WriteAllText(Path.Combine(repo, "src", "Good.cs"), "namespace Scratch;\n");
        change?.Invoke(repo);
        Git(repo, "init", "-q");
        Git(repo, "add", "-A");
        Git(repo, "add", "--chmod=+x", "--", "*.sh");
        Git(repo, "commit", "-q", "-m", "scratch");

        var psi = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repo,
        };
        if (script == "sh")
        {
            psi.ArgumentList.Add(Path.Combine(repo, "build", "prepush-check.sh"));
            if (testsOnly)
            {
                psi.ArgumentList.Add("--tests-only");
            }
        }
        else
        {
            foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(repo, "build", "prepush-check.ps1") })
            {
                psi.ArgumentList.Add(arg);
            }

            if (testsOnly)
            {
                psi.ArgumentList.Add("-TestsOnly");
            }
        }

        string fakes = FakeTools(scenario, bash, script == "ps1");
        psi.Environment["PATH"] = fakes + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using Process process = Process.Start(psi)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new Result(process.ExitCode, (output + error.Result).ReplaceLineEndings("\n"));
    }
}

/// <summary>build/prepush-check.sh (Git Bash on Windows).</summary>
public sealed class PrepushCheckShTests : PrepushCheckTests
{
    [PrepushTheory("sh")]
    [MemberData(nameof(Cases))]
    public void Sh(string testCase)
    {
        Check("sh", testCase);
    }
}

/// <summary>build/prepush-check.ps1 (Windows PowerShell on Windows, pwsh on Linux and macOS).</summary>
public sealed class PrepushCheckPs1Tests : PrepushCheckTests
{
    [PrepushTheory("ps1")]
    [MemberData(nameof(Cases))]
    public void Ps1(string testCase)
    {
        Check("ps1", testCase);
    }
}

/// <summary>[Theory] skipped when the shell of the script is missing (bash: Git Bash on Windows; pwsh on Linux/macOS).</summary>
public sealed class PrepushTheoryAttribute : TheoryAttribute
{
    public PrepushTheoryAttribute(string script)
    {
        if (PrepushCheckTests.Bash() == null)
        {
            Skip = "no bash: install Git for Windows (Git Bash runs the .sh scripts and the fake commands)";
        }
        else if (script == "ps1" && PrepushCheckTests.PowerShell() == null)
        {
            Skip = "no PowerShell: install pwsh (https://learn.microsoft.com/powershell/scripting/install/installing-powershell)";
        }
    }
}
