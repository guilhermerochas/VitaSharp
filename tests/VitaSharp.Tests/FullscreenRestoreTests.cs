using VitaSharp.Desktop.Host;

namespace VitaSharp.Tests;

/// <summary>
/// Returning to the fullscreen game on Windows (user report 12/11/2026: after switching to another application the
/// game window never came back, Windows played the "ding"). GLFW minimizes a fullscreen window when it loses the focus
/// (GLFW_AUTO_ICONIFY) and Alt+Tab / the taskbar could then make the minimized window the active one without restoring
/// it (reproduced: SwitchToThisWindow left it IsIconic and foreground, the swapchain stayed 0x0). The window loop now
/// restores a fullscreen window found in that state.
/// </summary>
public class FullscreenRestoreTests
{
    [Fact]
    public void AFullscreenWindowActivatedWhileMinimizedIsRestored()
        => Assert.True(WindowHost.FullscreenNeedsRestore(fullscreenMode: true, iconified: true, active: true));

    [Theory]
    [InlineData(true, true, false)]   // minimized in the background (the user is in another application): stays minimized
    [InlineData(true, false, true)]   // the normal fullscreen game
    [InlineData(false, true, true)]   // a window the user minimized from windowed mode: Windows restores it itself
    [InlineData(false, false, false)]
    public void EveryOtherStateIsLeftAlone(bool fullscreenMode, bool iconified, bool active)
        => Assert.False(WindowHost.FullscreenNeedsRestore(fullscreenMode, iconified, active));
}
