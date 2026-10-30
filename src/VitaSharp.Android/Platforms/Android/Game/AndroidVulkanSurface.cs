using System.Runtime.InteropServices;
using Android.Runtime;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Android.Game;

/// <summary>
/// The game's SurfaceView as the renderer's presentation surface: ANativeWindow_fromSurface on the view's Surface, then
/// vkCreateAndroidSurfaceKHR (VK_KHR_android_surface) through the renderer's own Vulkan entry points, so a custom
/// driver loaded by libadrenotools creates it too. One instance per SurfaceHolder.surfaceCreated; the session detaches
/// the renderer from it in surfaceDestroyed before <see cref="Dispose"/> releases the window.
/// </summary>
public sealed unsafe partial class AndroidVulkanSurface : IPresentationSurface, IDisposable
{
    nint _window;

    public AndroidVulkanSurface(global::Android.Views.Surface surface)
    {
        _window = ANativeWindow_fromSurface(JNIEnv.Handle, surface.Handle);
        if (_window == 0) throw new InvalidOperationException("ANativeWindow_fromSurface returned null");
    }

    public string[] RequiredInstanceExtensions => [KhrSurface.ExtensionName, KhrAndroidSurface.ExtensionName];

    public SurfaceKHR CreateSurface(Instance instance) =>
        throw new NotSupportedException("the Android surface is created through the renderer's Vk (CreateSurface(Vk, Instance))");

    public SurfaceKHR CreateSurface(Vk vk, Instance instance)
    {
        if (!vk.TryGetInstanceExtension(instance, out KhrAndroidSurface ext))
            throw new InvalidOperationException("VK_KHR_android_surface is not available");
        var ci = new AndroidSurfaceCreateInfoKHR { SType = StructureType.AndroidSurfaceCreateInfoKhr, Window = (nint*)_window };
        ext.CreateAndroidSurface(instance, in ci, null, out var surface).Ok("vkCreateAndroidSurfaceKHR");
        return surface;
    }

    /// <summary>The window's buffer size in pixels (the SurfaceView's size, landscape).</summary>
    public (int Width, int Height) FramebufferSize => _window == 0 ? (0, 0) : (ANativeWindow_getWidth(_window), ANativeWindow_getHeight(_window));

    public void Dispose()
    {
        if (_window != 0) ANativeWindow_release(_window);
        _window = 0;
    }

    [LibraryImport("android")] private static partial nint ANativeWindow_fromSurface(nint env, nint surface);
    [LibraryImport("android")] private static partial void ANativeWindow_release(nint window);
    [LibraryImport("android")] private static partial int ANativeWindow_getWidth(nint window);
    [LibraryImport("android")] private static partial int ANativeWindow_getHeight(nint window);
}
