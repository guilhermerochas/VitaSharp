using VitaSharp.Desktop.Host;

namespace VitaSharp.Tests;

/// <summary>
/// A desktop window (GLFW through Silk.NET.Windowing): Windows and macOS always have one; Linux needs a display, X11 or
/// Wayland (a headless machine can use a virtual one: <c>xvfb-run dotnet test ...</c>). Tests that drive a
/// <see cref="WindowHost"/> are skipped with the reason when no window can be created.
/// </summary>
public static class WindowAvailability
{
    static readonly Lazy<string?> Reason = new(Probe);

    /// <summary>Null when a window can be created, otherwise why not and how to get one.</summary>
    public static string? SkipReason => Reason.Value;

    static string? Probe()
    {
        try
        {
            using WindowHost window = new WindowHost("window probe");
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
