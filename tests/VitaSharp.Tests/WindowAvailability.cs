using System.Runtime.InteropServices;
using VitaSharp.Desktop.Host;

namespace VitaSharp.Tests;

/// <summary>
/// A desktop window (GLFW through Silk.NET.Windowing): Windows always has one; Linux needs a display, X11 or Wayland (a
/// headless machine can use a virtual one: <c>xvfb-run dotnet test ...</c>); macOS only on the process's main thread,
/// which a test never runs on. Tests that drive a <see cref="WindowHost"/> are skipped with the reason when no window
/// can be created.
/// </summary>
public static partial class WindowAvailability
{
    private static readonly Lazy<string?> s_reason = new(Probe);

    /// <summary>Null when a window can be created, otherwise why not and how to get one.</summary>
    public static string? SkipReason => s_reason.Value;

    /// <summary>macOS (libSystem): 1 on the process's main thread.</summary>
    [LibraryImport("libSystem.dylib", EntryPoint = "pthread_main_np")]
    private static partial int PthreadMainNp();

    /// <summary>macOS: whether this is the process's main thread (false when libSystem cannot be asked).</summary>
    private static bool OnMainThread()
    {
        try
        {
            return PthreadMainNp() == 1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static string? Probe()
    {
        // AppKit (under GLFW) creates windows and runs glfwInit only on the process's main thread; elsewhere it
        // aborts the whole process with an Objective-C exception, which no catch below sees (the test host crashed on
        // the macOS CI at the first overlay test). xUnit runs tests and discovery on worker threads.
        if (OperatingSystem.IsMacOS() && !OnMainThread())
        {
            return "macOS creates windows only on the process's main thread (AppKit; GLFW aborts the process "
                + "elsewhere) and xUnit runs the tests on worker threads: the game overlay tests run on Windows and Linux";
        }

        try
        {
            using var window = new WindowHost("window probe");
            return null;
        }
        catch (Exception e)
        {
            return $"no desktop window can be created ({e.GetType().Name}: {e.Message}). Linux needs an X11 or Wayland "
                + "display; on a headless machine run the tests under a virtual one: xvfb-run dotnet test tests/VitaSharp.Tests "
                + "(Debian/Ubuntu: sudo apt install xvfb)";
        }
    }
}

/// <summary>[Fact] that is skipped when no desktop window can be created.</summary>
public sealed class WindowFactAttribute : FactAttribute
{
    public WindowFactAttribute()
    {
        if (WindowAvailability.SkipReason is { } r)
        {
            Skip = r;
        }
    }
}
