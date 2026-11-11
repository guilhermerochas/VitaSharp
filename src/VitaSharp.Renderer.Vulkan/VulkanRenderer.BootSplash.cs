using Silk.NET.Vulkan;
using System.Diagnostics;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The application's start-up picture: the Vita shows the app's sce_sys/pic0.png while the process starts and keeps it
/// until the game shows a frame with something on it (Blind Men on a real Vita: ~4 s of pic0, then the publisher logo
/// with no black in between, although the game presents a black frame first; here the window stayed black ~20 s).
/// The front end hands the picture over with <see cref="SetBootSplash"/>; it is shown while the GPU worker is idle (on
/// a resize and about twice a second) and in place of the guest's frames, each of which is read back and checked,
/// until one is not black (or after <see cref="SplashMaxSeconds"/>), then released.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Longest the picture stays once the guest presents frames, all of them black.</summary>
    const double SplashMaxSeconds = 60;
    /// <summary>A channel above this (0-255) anywhere in a presented frame ends the picture.</summary>
    const int SplashBlackMax = 16;

    (byte[] Rgba, int Width, int Height)? _splashPixels;
    RenderTarget? _splashRt;
    long _splashAt, _splashSince, _splashFirstGuest;
    volatile bool _splashDone;

    /// <summary>RGBA pixels shown until the guest presents a frame that is not black (null: none). Any thread, before
    /// the first present.</summary>
    public void SetBootSplash(byte[]? rgba, int width, int height) =>
        _splashPixels = rgba != null && width > 0 && height > 0 && rgba.Length >= width * height * 4 ? (rgba, width, height) : null;

    /// <summary>The picture is still up: guest frames are read back and checked instead of shown.</summary>
    bool SplashWaiting
    {
        get
        {
            if (_splashDone || _splashPixels == null)
            {
                return false;
            }

            long now = Stopwatch.GetTimestamp();
            if (_splashFirstGuest == 0)
            {
                _splashFirstGuest = now;
            }
            else if (Stopwatch.GetElapsedTime(_splashFirstGuest, now).TotalSeconds > SplashMaxSeconds)
            {
                EndSplash($"after {SplashMaxSeconds:F0} s of black frames");
                return false;
            }
            return true;
        }
    }

    /// <summary>GPU worker (readback of a frame presented under the picture): ends the picture when the frame has content.</summary>
    void CheckSplashFrame(int present, nint pixels, uint w, uint h)
    {
        if (_splashDone)
        {
            return;
        }

        byte* p = (byte*)pixels;
        long n = (long)w * h * 4;
        for (long i = 0; i < n; i += 4)
        {
            if (p[i] > SplashBlackMax || p[i + 1] > SplashBlackMax || p[i + 2] > SplashBlackMax)
            {
                EndSplash($"until the game's first frame that is not black (present {present})");
                return;
            }
        }
    }

    void EndSplash(string why)
    {
        if (_splashDone)
        {
            return;
        }

        _splashDone = true;
        if (_splashSince != 0)
        {
            Core.Util.Log.Info($"Boot picture: shown for {Stopwatch.GetElapsedTime(_splashSince).TotalSeconds:F1} s, {why}");
        }

        _splashPixels = null;
        if (_splashRt != null)
        {
            DestroyLater(_splashRt);
            _splashRt = null;
        }
    }

    /// <summary>The picture's image, made and uploaded in the current command buffer the first time.</summary>
    (RenderTarget Rt, DisplayFrame Frame)? SplashImage()
    {
        if (_splashPixels is not { } px)
        {
            return null;
        }

        if (_splashRt == null)
        {
            uint w = (uint)px.Width, h = (uint)px.Height;
            var (img, view, mem) = CreateImage(MemCat.RenderTarget, w, h, Format.R8G8B8A8Unorm,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
            _splashSince = Stopwatch.GetTimestamp();
            Core.Util.Log.Info($"Boot picture: {w}x{h} shown until the game's first frame that is not black");
            _splashRt = new RenderTarget { Width = w, Height = h, Stride = w, Image = img, View = view, Mem = mem, Format = Format.R8G8B8A8Unorm };
            Barrier(img, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
            var (buf, off) = UploadSpan(px.Rgba.AsSpan(0, px.Width * px.Height * 4), 16);
            var region = new BufferImageCopy
            {
                BufferOffset = off,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(w, h, 1),
            };
            _vk.CmdCopyBufferToImage(Cmd, buf, img, ImageLayout.General, 1, in region);
            Barrier(img, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        }
        return (_splashRt, new DisplayFrame(0, _splashRt.Width, _splashRt.Width, _splashRt.Height, DisplayPixelFormat.A8B8G8R8));
    }

    /// <summary>Idle GPU worker: shows the picture (again on a resize or about twice a second) while it is up.</summary>
    void PresentBootSplash()
    {
        if (_splashDone || _splashPixels == null || _swapchain.Handle == 0 || _deviceLost || _inScene)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_splashRt != null && !_swapchainDirty && now - _splashAt < Stopwatch.Frequency / 2)
        {
            return;
        }

        _splashAt = now;
        BeginFrameIfNeeded();
        EndRendering();
        if (SplashImage() is { } s)
        {
            PresentOut(s.Rt, s.Frame);
        }
    }
}
