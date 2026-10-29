using System.Collections.Concurrent;
using VitaSharp.Core.Host;
using VitaSharp.Core.Shell;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Host;

namespace VitaSharp.Desktop.Launcher.Shell;

/// <summary>
/// The home screen's sound: the system BGM (an .at9 looped on a background thread) and the system sound effects,
/// through OpenAL. Both are optional (off by default) and the device is only opened when one is first used.
/// </summary>
public sealed class ShellAudio : IDisposable
{
    readonly ShellAssets _assets;
    OpenAlAudio? _audio;
    bool _audioFailed;
    readonly object _lock = new();

    Thread? _bgm;
    volatile bool _bgmStop;
    string? _bgmPath;

    Thread? _sfx;
    readonly BlockingCollection<PcmSound> _sfxQueue = new(4);

    public ShellAudio(ShellAssets assets) { _assets = assets; }

    OpenAlAudio? Device()
    {
        lock (_lock)
        {
            if (_audio == null && !_audioFailed)
            {
                _audio = OpenAlAudio.TryCreate();
                _audioFailed = _audio == null;
            }
            return _audio;
        }
    }

    /// <summary>Plays <paramref name="path"/> in a loop (null or a different file stops the current one first).</summary>
    public void PlayBgm(string? path)
    {
        if (path == _bgmPath && _bgm != null) return;
        StopBgm();
        if (path == null || !File.Exists(path)) return;
        At9Reader reader;
        try { reader = At9Reader.Open(path); }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            Log.Warn($"home BGM: {path}: {e.Message}");
            return;
        }
        var dev = Device();
        if (dev == null) return;
        _bgmPath = path;
        _bgmStop = false;
        _bgm = new Thread(() => BgmLoop(dev, reader)) { IsBackground = true, Name = "Home BGM" };
        _bgm.Start();
    }

    void BgmLoop(IAudioBackend dev, At9Reader reader)
    {
        using var stream = dev.OpenStream(reader.SampleRate, reader.Channels, 1024);
        stream.Volume = 0.6f;
        var buf = new List<short>(8192);
        int fails = 0;
        while (!_bgmStop)
        {
            buf.Clear();
            bool ok;
            try { ok = reader.Read(buf, out _); }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                if (++fails > 3) { Log.Warn($"home BGM: decoding failed: {e.Message}"); return; }
                ok = false;
            }
            if (!ok) { reader.Rewind(); continue; }
            stream.Submit(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf));
        }
    }

    public void StopBgm()
    {
        _bgmStop = true;
        _bgm?.Join(500);
        _bgm = null;
        _bgmPath = null;
    }

    /// <summary>Plays a system sound effect without waiting (dropped when several are already pending).</summary>
    public void Play(ShellSoundEffect effect)
    {
        var pcm = _assets.Sound(effect);
        if (pcm == null) return;
        var dev = Device();
        if (dev == null) return;
        if (_sfx == null)
        {
            _sfx = new Thread(() => SfxLoop(dev)) { IsBackground = true, Name = "Home sound effects" };
            _sfx.Start();
        }
        _sfxQueue.TryAdd(pcm);
    }

    void SfxLoop(IAudioBackend dev)
    {
        IAudioStream? stream = null;
        (int, int) format = default;
        try
        {
            foreach (var pcm in _sfxQueue.GetConsumingEnumerable())
            {
                if (stream == null || format != (pcm.SampleRate, pcm.Channels))
                {
                    stream?.Dispose();
                    stream = dev.OpenStream(pcm.SampleRate, pcm.Channels, 256);
                    format = (pcm.SampleRate, pcm.Channels);
                }
                stream.Submit(pcm.Samples);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { }
        finally { stream?.Dispose(); }
    }

    public void Dispose()
    {
        StopBgm();
        _sfxQueue.CompleteAdding();
        _sfx?.Join(500);
        lock (_lock) { _audio?.Dispose(); _audio = null; }
    }
}
