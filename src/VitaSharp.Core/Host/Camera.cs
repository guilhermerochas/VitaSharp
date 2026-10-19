namespace VitaSharp.Core.Host;

/// <summary>Which Vita camera the guest opened (SCE_CAMERA_DEVICE_FRONT / BACK).</summary>
public enum CameraFacing { Front = 0, Back = 1 }

/// <summary>
/// Host camera provider (desktop webcam, Android camera...). The core asks for a stream when the guest starts a
/// camera and releases it when the guest stops it. Without a provider (or when <see cref="Open"/> returns null) the
/// emulated camera still works and delivers neutral grey frames.
/// </summary>
public interface ICameraSource : IDisposable
{
    /// <summary>Human-readable names of the capture devices the host offers (for logs and settings).</summary>
    IReadOnlyList<string> DeviceNames { get; }

    /// <summary>Starts capturing for the given Vita camera at about the requested size and rate. Must return quickly
    /// (the device may finish opening in the background) and never throw; null = no camera available.</summary>
    ICameraStream? Open(CameraFacing facing, int width, int height, int fps);
}

/// <summary>A running host capture. Disposing it stops the capture.</summary>
public interface ICameraStream : IDisposable
{
    /// <summary>Most recent captured frame (written by the capture thread).</summary>
    CameraFrameBuffer Frames { get; }
}

/// <summary>
/// Thread-safe holder of the most recent camera frame, stored as RGBA8 (R, G, B, A bytes, top-down rows).
/// Producers overwrite it; consumers copy it out. Only the latest frame is kept (no queue).
/// </summary>
public sealed class CameraFrameBuffer
{
    readonly object _lock = new();
    byte[] _rgba = [];
    int _width, _height;
    long _sequence;

    /// <summary>Number of frames published so far (0 = none yet).</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>Publishes a frame produced by <paramref name="fill"/>, which writes width*height*4 RGBA bytes.</summary>
    public void Publish(int width, int height, CameraFill fill)
    {
        lock (_lock)
        {
            int n = width * height * 4;
            if (_rgba.Length != n) _rgba = new byte[n];
            fill(_rgba);
            _width = width;
            _height = height;
            Interlocked.Increment(ref _sequence);
        }
    }

    /// <summary>Publishes an RGBA frame by copy.</summary>
    public void Publish(ReadOnlySpan<byte> rgba, int width, int height)
    {
        lock (_lock)
        {
            int n = width * height * 4;
            if (_rgba.Length != n) _rgba = new byte[n];
            rgba[..n].CopyTo(_rgba);
            _width = width;
            _height = height;
            Interlocked.Increment(ref _sequence);
        }
    }

    /// <summary>Copies the latest frame into <paramref name="dst"/> (resized as needed). Returns false if none yet.</summary>
    public bool CopyLatest(ref byte[] dst, out int width, out int height, out long sequence)
    {
        lock (_lock)
        {
            width = _width; height = _height; sequence = _sequence;
            if (sequence == 0) return false;
            if (dst.Length != _rgba.Length) dst = new byte[_rgba.Length];
            _rgba.CopyTo(dst, 0);
            return true;
        }
    }
}

public delegate void CameraFill(Span<byte> rgba);
