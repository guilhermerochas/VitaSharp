using System.Diagnostics;
using SkiaSharp;

namespace VitaSharp.Tests;

/// <summary>
/// The front-end (VitaSharp.Desktop) as a child process, from the test's own output folder: what needs a guest memory
/// of its own (one per process) runs there, a game run above all. The child loads the same assemblies as the tests, so
/// the coverage collector counts what it executes too. Always with <c>--data=</c> a test folder (never the user's)
/// and, for the renderer, the software Vulkan driver (<see cref="SoftwareVulkan"/>).
/// </summary>
public static class DesktopProcess
{
    public sealed record Result(int ExitCode, string Output);

    static string Exe => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "VitaSharp.Desktop.exe" : "VitaSharp.Desktop");

    /// <summary>Runs the front-end with <paramref name="args"/> and <c>--data=<paramref name="data"/></c>; the output
    /// is stdout and stderr together. Fails the test when it does not end in <paramref name="timeoutMs"/>.</summary>
    /// <param name="systemLoader">Windows: the software driver through the system Vulkan loader (its ICD manifest) instead of
    /// loaded directly; a window surface needs it (GLFW creates the surface through the loader).</param>
    public static Result Run(string data, IEnumerable<string> args, int timeoutMs = 120_000, IDictionary<string, string>? env = null, bool softwareVulkan = true, bool systemLoader = false)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = data,
        };
        psi.ArgumentList.Add("--data=" + data);
        foreach (var a in args) psi.ArgumentList.Add(a);
        // no window on screen, no audio device, no network, nothing from the user's own configuration
        psi.Environment[VitaSharp.Core.Util.EnvVars.HiddenWindow] = "1";
        psi.Environment.Remove(VitaSharp.Core.Util.EnvVars.Data);
        if (softwareVulkan)
            foreach (var (name, value) in SoftwareVulkan.ChildEnvironment(systemLoader)) psi.Environment[name] = value;
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var output = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            p.WaitForExit();
            Assert.Fail($"VitaSharp.Desktop {string.Join(' ', args)} did not end in {timeoutMs / 1000} s:\n{Tail(output)}");
        }
        p.WaitForExit(); // the redirected streams are drained
        lock (output) return new Result(p.ExitCode, output.ToString());
    }

    static string Tail(System.Text.StringBuilder sb)
    {
        lock (sb) { var s = sb.ToString(); return s.Length > 6000 ? s[^6000..] : s; }
    }

    /// <summary>The pixel at the centre of a PNG as R, G, B, and its size.</summary>
    public static ((byte R, byte G, byte B) Center, int Width, int Height) Probe(string png)
    {
        using var bmp = SKBitmap.Decode(png) ?? throw new InvalidDataException(png);
        var c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        return ((c.Red, c.Green, c.Blue), bmp.Width, bmp.Height);
    }
}
