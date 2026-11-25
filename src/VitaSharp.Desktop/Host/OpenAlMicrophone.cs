using Silk.NET.OpenAL;
using System.Runtime.InteropServices;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// The Vita microphone from a host capture device through OpenAL Soft's capture API (the library the emulator already
/// ships for its output: WASAPI on Windows, PipeWire/PulseAudio/ALSA on Linux, Core Audio on macOS). The device opens
/// when the guest opens its microphone port, at the port's rate (OpenAL Soft converts from the device's own), 16-bit
/// mono, with a quarter of a second of buffer; the guest's reads take what was captured (never blocking: the port's
/// pace is the guest clock's). A failed open (no device, macOS microphone permission refused) leaves the port silent.
/// </summary>
public sealed unsafe class OpenAlMicrophone : IMicrophoneSource
{
    const int ALC_CAPTURE_DEVICE_SPECIFIER = 0x310, ALC_CAPTURE_DEFAULT_DEVICE_SPECIFIER = 0x311, ALC_CAPTURE_SAMPLES = 0x312;
    const int AL_FORMAT_MONO16 = 0x1101;

    readonly MicrophoneSpec _spec;
    readonly delegate* unmanaged[Cdecl]<Device*, int, byte*> _getString;
    readonly delegate* unmanaged[Cdecl]<byte*, uint, int, int, Device*> _open;
    readonly delegate* unmanaged[Cdecl]<Device*, byte> _close;
    readonly delegate* unmanaged[Cdecl]<Device*, void> _start;
    readonly delegate* unmanaged[Cdecl]<Device*, void> _stop;
    readonly delegate* unmanaged[Cdecl]<Device*, void*, int, void> _samples;
    readonly delegate* unmanaged[Cdecl]<Device*, int, int, int*, void> _getInteger;

    OpenAlMicrophone(MicrophoneSpec spec, ALContext alc)
    {
        _spec = spec;
        _getString = (delegate* unmanaged[Cdecl]<Device*, int, byte*>)alc.Context.GetProcAddress("alcGetString");
        _open = (delegate* unmanaged[Cdecl]<byte*, uint, int, int, Device*>)alc.Context.GetProcAddress("alcCaptureOpenDevice");
        _close = (delegate* unmanaged[Cdecl]<Device*, byte>)alc.Context.GetProcAddress("alcCaptureCloseDevice");
        _start = (delegate* unmanaged[Cdecl]<Device*, void>)alc.Context.GetProcAddress("alcCaptureStart");
        _stop = (delegate* unmanaged[Cdecl]<Device*, void>)alc.Context.GetProcAddress("alcCaptureStop");
        _samples = (delegate* unmanaged[Cdecl]<Device*, void*, int, void>)alc.Context.GetProcAddress("alcCaptureSamples");
        _getInteger = (delegate* unmanaged[Cdecl]<Device*, int, int, int*, void>)alc.Context.GetProcAddress("alcGetIntegerv");
    }

    bool Usable => _getString != null && _open != null && _close != null && _start != null && _stop != null && _samples != null && _getInteger != null;

    /// <summary>The microphone for a setting: null when OpenAL Soft or its capture API is missing (the port stays silent).
    /// The device itself is only opened when a guest opens its port.</summary>
    public static OpenAlMicrophone? TryCreate(MicrophoneSpec spec)
    {
        try
        {
            var microphone = new OpenAlMicrophone(spec, ALContext.GetApi(true));
            if (!microphone.Usable)
            {
                Log.Warn("Microphone: this OpenAL has no capture API; the Vita microphone is silent");
                return null;
            }

            return microphone;
        }
        catch (Exception e)
        {
            Log.Warn($"Microphone: OpenAL unavailable ({e.Message}); the Vita microphone is silent");
            return null;
        }
    }

    public IReadOnlyList<string> DeviceNames => ListDevices(out _);

    /// <summary>The capture devices OpenAL Soft offers and the system's default one.</summary>
    public List<string> ListDevices(out string? defaultName)
    {
        var names = new List<string>();
        defaultName = null;
        byte* p = _getString(null, ALC_CAPTURE_DEVICE_SPECIFIER);
        while (p != null && *p != 0)
        {
            string name = Marshal.PtrToStringUTF8((nint)p)!;
            names.Add(name);
            p += System.Text.Encoding.UTF8.GetByteCount(name) + 1;
        }

        byte* d = _getString(null, ALC_CAPTURE_DEFAULT_DEVICE_SPECIFIER);
        if (d != null)
        {
            defaultName = Marshal.PtrToStringUTF8((nint)d);
        }

        return names;
    }

    public IMicrophoneStream? Open(int sampleRate)
    {
        List<string> names = ListDevices(out string? defaultName);
        int index = _spec.Kind == MicrophoneSpecKind.Default ? -1 : _spec.SelectDevice(names);
        if (_spec.Kind == MicrophoneSpecKind.Device && index < 0)
        {
            Log.Warn($"Microphone: no capture device matches the setting ({names.Count} devices); the Vita microphone is silent");
            return null;
        }

        string? name = index >= 0 ? names[index] : null;
        int shownIndex = index >= 0 ? index : defaultName != null ? names.IndexOf(defaultName) : -1;
        // the port's rate first (OpenAL Soft converts); then the rates every device has (the core resamples)
        foreach (int rate in new[] { sampleRate, 48000, 44100 }.Distinct())
        {
            Device* device = OpenDevice(name, rate);
            if (device == null)
            {
                continue;
            }

            _start(device);
            // the device name stays out of the description (it can carry a person's name: "X's headset"); logs and
            // capture reports only say which entry of the list it is
            string description = $"host capture device {(shownIndex >= 0 ? $"#{shownIndex}" : "default")} of {names.Count} at {rate} Hz";
            Log.Info($"Microphone: OpenAL capture open, {description}");
            return new CaptureStream(this, device, rate, description);
        }

        Log.Warn($"Microphone: no capture device could be opened ({names.Count} listed; on macOS check the microphone permission); the Vita microphone is silent");
        return null;
    }

    Device* OpenDevice(string? name, int rate)
    {
        int bufferSamples = Math.Max(rate / 4, 1024);
        if (name == null)
        {
            return _open(null, (uint)rate, AL_FORMAT_MONO16, bufferSamples);
        }

        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* namePointer = utf8)
        {
            return _open(namePointer, (uint)rate, AL_FORMAT_MONO16, bufferSamples);
        }
    }

    public void Dispose()
    {
    }

    sealed class CaptureStream : IMicrophoneStream
    {
        readonly OpenAlMicrophone _owner;
        readonly object _lock = new();
        Device* _device;

        public CaptureStream(OpenAlMicrophone owner, Device* device, int rate, string description)
        {
            _owner = owner;
            _device = device;
            SampleRate = rate;
            Description = description;
        }

        public int SampleRate { get; }

        public string Description { get; }

        public int Available
        {
            get
            {
                lock (_lock)
                {
                    return CapturedLocked();
                }
            }
        }

        int CapturedLocked()
        {
            if (_device == null)
            {
                return 0;
            }

            int count = 0;
            _owner._getInteger(_device, ALC_CAPTURE_SAMPLES, 1, &count);
            return Math.Max(0, count);
        }

        public int Read(Span<short> dest)
        {
            lock (_lock)
            {
                int count = Math.Min(CapturedLocked(), dest.Length);
                if (count <= 0)
                {
                    return 0;
                }

                fixed (short* destination = dest)
                {
                    _owner._samples(_device, destination, count);
                }

                return count;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_device == null)
                {
                    return;
                }

                _owner._stop(_device);
                _owner._close(_device);
                _device = null;
            }

            Log.Info("Microphone: OpenAL capture closed");
        }
    }
}
