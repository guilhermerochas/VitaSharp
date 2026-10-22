using System.Runtime.InteropServices;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Audio;

/// <summary>
/// SKELETON (not built or tested yet: there is no Android front-end). Audio output through the NDK AAudio API
/// (libaaudio.so, API 26+) behind the core's <see cref="IAudioBackend"/>, the interface the desktop's OpenAL backend
/// implements. One AAudio stream per guest audio port, signed 16-bit interleaved, blocking writes so the guest is
/// paced by the device like on the desktop. The Android front-end installs it with
/// <c>emu.Host.Audio = AAudioBackend.TryCreate();</c>.
///
/// Remaining work:
/// <list type="bullet">
/// <item>Error/disconnect callback (AAudioStreamBuilder_setErrorCallback): reopen the stream on another device when
///   headphones/Bluetooth change; the callback must not reopen on its own thread.</item>
/// <item>Activity lifecycle: pause (requestPause) on onPause, resume on onResume, close on destroy.</item>
/// <item>Buffer sizing: setBufferSizeInFrames to about two guest grains (the Vita output is double-buffered) and
///   expose the real queue depth through <see cref="IAudioStream.PendingSamples"/> (done here from the frame counters).</item>
/// <item>Sample-rate conversion when the device refuses 48 kHz in exclusive mode (AAudio resamples in shared mode).</item>
/// <item>Fallback to OpenSL ES (API &lt; 26) or Oboe if needed; validation on devices (latency, underruns).</item>
/// </list>
/// </summary>
public sealed class AAudioBackend : IAudioBackend
{
    AAudioBackend() { }

    public static AAudioBackend? TryCreate()
    {
        try
        {
            if (AAudio.AAudio_createStreamBuilder(out nint b) != 0) return null;
            AAudio.AAudioStreamBuilder_delete(b);
            Log.Info("Audio: AAudio");
            return new AAudioBackend();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"AAudio unavailable: {e.Message}");
            return null;
        }
    }

    public IAudioStream OpenStream(int sampleRate, int channels, int grainSamples) => new Stream(sampleRate, channels, grainSamples);

    public void Dispose() { }

    sealed unsafe class Stream : IAudioStream
    {
        nint _stream;
        readonly int _channels;
        short[] _scaled = [];

        public Stream(int rate, int channels, int grain)
        {
            _channels = channels;
            if (AAudio.AAudio_createStreamBuilder(out nint b) != 0) throw new InvalidOperationException("AAudio: no stream builder");
            AAudio.AAudioStreamBuilder_setFormat(b, AAudio.FormatPcmI16);
            AAudio.AAudioStreamBuilder_setChannelCount(b, channels);
            AAudio.AAudioStreamBuilder_setSampleRate(b, rate);
            AAudio.AAudioStreamBuilder_setPerformanceMode(b, AAudio.PerformanceModeLowLatency);
            AAudio.AAudioStreamBuilder_setBufferCapacityInFrames(b, grain * 4);
            int r = AAudio.AAudioStreamBuilder_openStream(b, out _stream);
            AAudio.AAudioStreamBuilder_delete(b);
            if (r != 0) throw new InvalidOperationException($"AAudio: openStream failed ({r})");
            AAudio.AAudioStream_setBufferSizeInFrames(_stream, grain * 2);
            AAudio.AAudioStream_requestStart(_stream);
        }

        /// <summary>AAudio streams have no gain: the volume is applied to the samples.</summary>
        public float Volume { get; set; } = 1;

        public int PendingSamples => _stream == 0 ? 0
            : (int)Math.Max(0, AAudio.AAudioStream_getFramesWritten(_stream) - AAudio.AAudioStream_getFramesRead(_stream));

        public void Submit(ReadOnlySpan<short> interleaved)
        {
            if (_stream == 0 || interleaved.IsEmpty) return;
            ReadOnlySpan<short> data = interleaved;
            if (Volume != 1)
            {
                if (_scaled.Length < interleaved.Length) _scaled = new short[interleaved.Length];
                for (int i = 0; i < interleaved.Length; i++) _scaled[i] = (short)Math.Clamp((int)(interleaved[i] * Volume), short.MinValue, short.MaxValue);
                data = _scaled.AsSpan(0, interleaved.Length);
            }
            int frames = data.Length / _channels;
            fixed (short* p = data)
            {
                // blocks until the device has room (1 s timeout): this paces the guest like the hardware does
                int done = 0;
                while (done < frames)
                {
                    int n = AAudio.AAudioStream_write(_stream, p + done * _channels, frames - done, 1_000_000_000L);
                    if (n <= 0) { Log.WarnOnce("aaudio-write", $"AAudio: write failed ({n})"); return; }
                    done += n;
                }
            }
        }

        public void Dispose()
        {
            if (_stream == 0) return;
            AAudio.AAudioStream_requestStop(_stream);
            AAudio.AAudioStream_close(_stream);
            _stream = 0;
        }
    }
}

/// <summary>NDK AAudio API (aaudio/AAudio.h).</summary>
internal static unsafe partial class AAudio
{
    const string Lib = "libaaudio.so";
    public const int FormatPcmI16 = 1, PerformanceModeLowLatency = 12;

    [LibraryImport(Lib)] public static partial int AAudio_createStreamBuilder(out nint builder);
    [LibraryImport(Lib)] public static partial int AAudioStreamBuilder_delete(nint builder);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setFormat(nint builder, int format);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setChannelCount(nint builder, int channels);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setSampleRate(nint builder, int rate);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setPerformanceMode(nint builder, int mode);
    [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setBufferCapacityInFrames(nint builder, int frames);
    [LibraryImport(Lib)] public static partial int AAudioStreamBuilder_openStream(nint builder, out nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_setBufferSizeInFrames(nint stream, int frames);
    [LibraryImport(Lib)] public static partial int AAudioStream_requestStart(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_requestStop(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_close(nint stream);
    [LibraryImport(Lib)] public static partial int AAudioStream_write(nint stream, void* buffer, int numFrames, long timeoutNanoseconds);
    [LibraryImport(Lib)] public static partial long AAudioStream_getFramesWritten(nint stream);
    [LibraryImport(Lib)] public static partial long AAudioStream_getFramesRead(nint stream);
}
