using Android;
using Android.App;
using Android.Content.PM;
using Android.Media;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Game;

/// <summary>
/// The phone's microphone for SceAudioIn through AudioRecord: 16-bit mono at the port's rate (16 kHz VOICE ports use
/// the VOICE_COMMUNICATION source, with the phone's echo cancellation and noise suppression, as the Vita's VOICE path
/// processes the voice; 48 kHz RAW ports the plain MIC source). The RECORD_AUDIO permission is asked the first time a
/// game opens its microphone port, never before; denied, the port delivers silence (as with no microphone). A capture
/// thread reads the device into a <see cref="MicrophoneRing"/> the guest's reads take from; the device is released
/// when the port is.
/// </summary>
public sealed class AndroidMicrophone : IMicrophoneSource
{
    /// <summary>The requestCode of the RECORD_AUDIO permission request (GameActivity.OnRequestPermissionsResult).</summary>
    public const int PermissionRequestCode = 0x5CA1;

    readonly Activity _activity;
    readonly object _lock = new();
    TaskCompletionSource<bool>? _permission;
    bool _denied;

    public AndroidMicrophone(Activity activity)
    {
        _activity = activity;
    }

    public IReadOnlyList<string> DeviceNames => ["phone microphone"];

    public IMicrophoneStream? Open(int sampleRate)
    {
        lock (_lock)
        {
            if (_denied)
            {
                Log.Info("Microphone: permission denied, the Vita microphone is silent");
                return null;
            }
        }

        return new Capture(this, sampleRate);
    }

    public void Dispose()
    {
    }

    /// <summary>True once RECORD_AUDIO is granted; asks for it (once per game process) otherwise.</summary>
    Task<bool> EnsurePermissionAsync()
    {
        if (_activity.CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted)
        {
            return Task.FromResult(true);
        }

        lock (_lock)
        {
            if (_denied)
            {
                return Task.FromResult(false);
            }

            if (_permission != null)
            {
                return _permission.Task;
            }

            _permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Log.Info("Microphone: asking for the RECORD_AUDIO permission");
        _activity.RunOnUiThread(() =>
        {
            try
            {
                _activity.RequestPermissions([Manifest.Permission.RecordAudio], PermissionRequestCode);
            }
            catch (Exception e)
            {
                Log.Warn($"Microphone: permission request failed: {e.Message}");
                OnPermissionResult(false);
            }
        });
        return _permission.Task;
    }

    /// <summary>GameActivity.OnRequestPermissionsResult; false when the request was not this one.</summary>
    public bool OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (requestCode != PermissionRequestCode)
        {
            return false;
        }

        OnPermissionResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
        return true;
    }

    void OnPermissionResult(bool granted)
    {
        TaskCompletionSource<bool>? completion;
        lock (_lock)
        {
            completion = _permission;
            _permission = null;
            if (!granted)
            {
                _denied = true;
            }
        }

        Log.Info(granted ? "Microphone: permission granted" : "Microphone: permission denied, the Vita microphone is silent");
        completion?.TrySetResult(granted);
    }

    /// <summary>One open port's capture: waits for the permission, then reads the device on its own thread.</summary>
    sealed class Capture : IMicrophoneStream
    {
        readonly AndroidMicrophone _owner;
        readonly MicrophoneRing _ring;
        readonly CancellationTokenSource _stop = new();

        public Capture(AndroidMicrophone owner, int sampleRate)
        {
            _owner = owner;
            SampleRate = sampleRate;
            // a quarter of a second: what the guest has not taken by then is old speech
            _ring = new MicrophoneRing(Math.Max(sampleRate / 4, 1024));
            Description = $"phone microphone at {sampleRate} Hz";
            _ = Task.Run(RunAsync);
        }

        public int SampleRate { get; }

        public string Description { get; }

        public int Available => _ring.Available;

        public int Read(Span<short> dest) => _ring.Read(dest);

        async Task RunAsync()
        {
            try
            {
                if (!await _owner.EnsurePermissionAsync().ConfigureAwait(false) || _stop.IsCancellationRequested)
                {
                    return;
                }

                var thread = new Thread(Record) { IsBackground = true, Name = "VitaSharp microphone" };
                thread.Start();
            }
            catch (Exception e)
            {
                Log.Warn($"Microphone: {e.Message}");
            }
        }

        void Record()
        {
            AudioRecord? record = null;
            try
            {
                AudioSource source = SampleRate <= 16000 ? AudioSource.VoiceCommunication : AudioSource.Mic;
                int minimum = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
                int bytes = Math.Max(minimum, SampleRate / 10 * 2);
                record = new AudioRecord(source, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, bytes);
                if (record.State != State.Initialized)
                {
                    Log.Warn($"Microphone: AudioRecord at {SampleRate} Hz did not initialise; the Vita microphone is silent");
                    return;
                }

                record.StartRecording();
                Log.Info($"Microphone: recording at {SampleRate} Hz ({source})");
                // one 10 ms chunk per read: the guest takes grains of 16 ms
                short[] chunk = new short[Math.Max(160, SampleRate / 100)];
                while (!_stop.IsCancellationRequested)
                {
                    int read = record.Read(chunk, 0, chunk.Length);
                    if (read < 0)
                    {
                        Log.Warn($"Microphone: AudioRecord read failed ({read})");
                        return;
                    }

                    _ring.Write(chunk.AsSpan(0, read));
                }
            }
            catch (Exception e)
            {
                Log.Warn($"Microphone: {e.Message}");
            }
            finally
            {
                try
                {
                    record?.Stop();
                }
                catch (Exception)
                {
                    // not recording (it failed to start)
                }

                record?.Release();
                Log.Info("Microphone: recording stopped");
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
        }
    }
}
