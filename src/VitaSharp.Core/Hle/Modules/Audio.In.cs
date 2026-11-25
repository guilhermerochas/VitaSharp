using VitaSharp.Core.Host;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceAudioIn (os0:kd/audioin.skprx SceAudioin 3.74, read in the module, study only; vita-headers psp2/audioin.h): the
// microphone ports. sceAudioInOpenPort validates the type first (below 0x400; RAW is 2 or 0x12, 0x1F a 48 kHz port, any
// other multiple of 16 a VOICE-like port, the 0x20 bit the camera profile that reports route changes), then the rate and
// the grain (VOICE 256/512 samples at 16 kHz; RAW 256 at 16 kHz or 768 at 48 kHz; 0x1F 768 at 48 kHz) and only then the
// parameter (0 = S16 mono). Each process gets one port (its handle: 0x80 | a process byte); sceAudioInInput blocks
// until the capture thread has the next grain (the codec chip's ADC through Venezia, so one grain per grain/rate
// seconds) and copies it out. The mute flag (sceAudioInSetMute) is system-wide and silences every capture. The Vita
// always has its built-in microphone, so "no microphone" has no error of its own: here, without a host device or its
// permission, the port delivers silence at the same pace. The host capture comes from HostServices.Microphone
// (MicrophoneFeed resamples it to the port's rate and bridges the host's clock to the guest's).
public sealed partial class Audio
{
    const int IN_ERR_INVALID_SIZE = unchecked((int)0x80260102);
    const int IN_ERR_INVALID_SAMPLE_FREQ = unchecked((int)0x80260103);
    const int IN_ERR_INVALID_PORT_TYPE = unchecked((int)0x80260104);
    const int IN_ERR_INVALID_POINTER = unchecked((int)0x80260105);
    const int IN_ERR_INVALID_PORT_PARAM = unchecked((int)0x80260106);
    const int IN_ERR_PORT_FULL = unchecked((int)0x80260107);
    const int IN_ERR_NOT_OPENED = unchecked((int)0x80260109);
    const int IN_ERR_INVALID_PARAMETER = unchecked((int)0x8026010B);
    const int InPortId = 0x80;

    /// <summary>The type bit of the camera profile (SCE_AUDIO_IN_PORT_TYPE_VOICE | 0x20): its captures report route
    /// changes through sceAudioInInputWithInputDeviceState.</summary>
    const int InCameraProfile = 0x20;

    sealed partial class InPort
    {
        public int Type, Grain, Freq;
        public short MicGain = 0x1060;
        public double NextDeadline;
        public int RouteReports; // CAMERA profile: captures left that report a route change
        public readonly object Lock = new();
    }

    InPort? _inPort;
    bool _micMuted;
    int _inputRoute; // SCE_AUDIO_IN_INPUT_MODE_BUILTIN_MIC

    /// <summary>The host capture of the open port (not saved: a loaded state reopens it from the port's format).</summary>
    [SnapshotIgnore]
    MicrophoneFeed? _micFeed;

    [SnapshotIgnore]
    short[] _micGrain = [];

    /// <summary>The front-end's microphone; tests set their own.</summary>
    [SnapshotIgnore]
    internal IMicrophoneSource? MicrophoneOverride;

    IMicrophoneSource? HostMicrophone => MicrophoneOverride ?? Emu?.Host.Microphone;

    [Hle]
    int sceAudioInGetStatus(int select)
    {
        if (select != 1)
        {
            return IN_ERR_INVALID_PARAMETER;
        }

        return _micMuted ? 1 : 0;
    }

    /// <summary>The checks of the module's open, in its order: type, rate, grain, then the parameter.</summary>
    static int ValidateInPort(int type, int grain, int freq, int param)
    {
        if ((uint)type >> 10 != 0)
        {
            return IN_ERR_INVALID_PORT_TYPE;
        }

        if ((type & ~0x10) == 2)
        {
            // RAW: 256 samples at 16 kHz or 768 at 48 kHz
            bool valid = (grain == 768 && freq == 48000) || (grain == 256 && freq == 16000);
            if (!valid)
            {
                return freq is 16000 or 48000 ? IN_ERR_INVALID_SIZE : IN_ERR_INVALID_SAMPLE_FREQ;
            }
        }
        else if (type == 0x1F)
        {
            if (freq != 48000)
            {
                return IN_ERR_INVALID_SAMPLE_FREQ;
            }

            if (grain != 768)
            {
                return IN_ERR_INVALID_SIZE;
            }
        }
        else if ((type & 0xF) == 0)
        {
            // VOICE (and the camera profile): 256 or 512 samples at 16 kHz
            if (freq != 16000)
            {
                return IN_ERR_INVALID_SAMPLE_FREQ;
            }

            if (grain is not (256 or 512))
            {
                return IN_ERR_INVALID_SIZE;
            }
        }
        else
        {
            return IN_ERR_INVALID_PORT_TYPE;
        }

        return param != 0 ? IN_ERR_INVALID_PORT_PARAM : 0;
    }

    [Hle]
    int sceAudioInOpenPort(int type, int grain, int freq, int param)
    {
        int error = ValidateInPort(type, grain, freq, param);
        if (error != 0)
        {
            return error;
        }

        return OpenInPort(type, grain, freq);
    }

    /// <summary>Diagnostic entry (psp2/audioin.h): like sceAudioInOpenPort, and also accepts the DIAG port type (14)
    /// without validating grain/frequency.</summary>
    [Hle]
    int sceAudioInOpenPortForDiag(int type, int grain, int freq, int param)
    {
        if (type != 14)
        {
            return sceAudioInOpenPortThroughEntry(type, grain, freq, param);
        }

        if (param != 0)
        {
            return IN_ERR_INVALID_PORT_PARAM;
        }

        // unvalidated values still have to pace and size the capture
        return OpenInPort(type, grain is > 0 and <= 4096 ? grain : 256, freq > 0 ? freq : 16000);
    }

    int OpenInPort(int type, int grain, int freq)
    {
        lock (this)
        {
            if (_inPort != null)
            {
                return IN_ERR_PORT_FULL; // one port per process
            }

            _inPort = new InPort { Type = type, Grain = grain, Freq = freq, RouteReports = 2 };
        }

        OpenMicFeed(grain, freq);
        CallInPortCallback(InKernel._inOpenCallback, InOwnerPid);
        return InPortId;
    }

    InPort? InGet(int port)
    {
        lock (this)
        {
            return port == InPortId ? _inPort : null;
        }
    }

    [Hle]
    int sceAudioInReleasePort(int port)
    {
        lock (this)
        {
            if (port != InPortId || _inPort == null)
            {
                return IN_ERR_NOT_OPENED;
            }

            _inPort = null;
        }

        CloseMicFeed();
        CallInPortCallback(InKernel._inReleaseCallback, InOwnerPid);
        return 0;
    }

    [Hle]
    int sceAudioInInput(int port, uint dest) => Input(port, dest, 0);

    [Hle]
    int sceAudioInInputWithInputDeviceState(int port, uint dest, uint deviceState) => Input(port, dest, deviceState);

    /// <summary>Blocks until the next grain is due on the guest clock (grain/rate seconds after the previous one, as the
    /// capture hardware delivers them; turbo mode moves the clock there instead of waiting), then writes the grain the
    /// host captured (silence when muted, without a microphone, or while another process has the microphone).</summary>
    int Input(int port, uint dest, uint deviceState)
    {
        InPort? inPort = InGet(port);
        if (inPort == null)
        {
            return IN_ERR_NOT_OPENED;
        }

        if (dest == 0)
        {
            return IN_ERR_INVALID_POINTER;
        }

        lock (inPort.Lock)
        {
            WaitForInGrain(inPort);
            MicrophoneFeed feed = EnsureMicFeed(inPort);
            Span<short> grain = _micGrain.AsSpan(0, inPort.Grain);
            bool silent = _micMuted || InAdoptedPid != InOwnerPid;
            feed.Fill(grain, silent);
            grain.CopyTo(GuestMemory.Span<short>(dest, grain.Length));
            AudioDump.WriteMicrophone(grain, inPort.Freq);
            if ((inPort.Type & InCameraProfile) != 0 && deviceState != 0)
            {
                GuestMemory.W32(deviceState, inPort.RouteReports > 0 ? 1u : 0u);
                if (inPort.RouteReports > 0)
                {
                    inPort.RouteReports--;
                }
            }
        }

        return 0;
    }

    /// <summary>The grain pace on the guest clock: a port that was not read for a while (over 0.1 s late) starts again
    /// from now instead of returning the missed grains at once.</summary>
    static void WaitForInGrain(InPort inPort)
    {
        double now = GuestClock.Seconds;
        double period = (double)inPort.Grain / inPort.Freq;
        if (inPort.NextDeadline < now - 0.1)
        {
            inPort.NextDeadline = now;
        }

        inPort.NextDeadline += period;
        long deadline = GuestClock.FromSeconds(inPort.NextDeadline);
        if (GuestClock.Ticks >= deadline)
        {
            return;
        }

        // even a wait under a millisecond is waited (or, in turbo mode, the clock moved): the guest never sees its
        // grain before the hardware would have it
        using Scheduler.Blocked blocked = Scheduler.Block();
        GuestClock.WaitUntil(deadline);
    }

    /// <summary>Starts the host capture for a newly opened port (a permission prompt on Android shows now).</summary>
    void OpenMicFeed(int grain, int freq)
    {
        IMicrophoneStream? stream = null;
        try
        {
            stream = HostMicrophone?.Open(freq);
        }
        catch (Exception e)
        {
            Log.Warn($"Microphone: opening the host capture failed: {e.Message}");
        }

        var feed = new MicrophoneFeed(stream, freq, grain);
        if (_micGrain.Length < grain)
        {
            _micGrain = new short[grain];
        }

        SwapMicFeed(feed);
        AudioDiagnostics.MicrophoneOpened(feed);
        Log.Info($"Microphone: port open, {feed.Describe()}");
    }

    /// <summary>The port's capture, reopened when the port's format changed under it (a loaded state, a process exec).</summary>
    MicrophoneFeed EnsureMicFeed(InPort inPort)
    {
        MicrophoneFeed? feed;
        lock (this)
        {
            feed = _micFeed;
        }

        if (feed != null && feed.Grain == inPort.Grain && feed.PortRate == inPort.Freq)
        {
            return feed;
        }

        OpenMicFeed(inPort.Grain, inPort.Freq);
        lock (this)
        {
            return _micFeed!;
        }
    }

    void CloseMicFeed()
    {
        MicrophoneFeed? feed = SwapMicFeed(null);
        if (feed != null)
        {
            Log.Info($"Microphone: port released, {feed.Describe()}");
        }
    }

    /// <summary>Makes <paramref name="feed"/> the port's capture and closes the previous one (also the one a previous
    /// image of this process left registered with the host); returns the previous one.</summary>
    MicrophoneFeed? SwapMicFeed(MicrophoneFeed? feed)
    {
        MicrophoneFeed? previous;
        lock (this)
        {
            previous = _micFeed;
            _micFeed = feed;
        }

        if (previous != null)
        {
            AudioDiagnostics.MicrophoneClosed(previous);
        }

        try
        {
            if (Emu != null)
            {
                Emu.Host.ReplaceMicrophoneFeed(Emu, feed);
            }
            else if (previous != null && !ReferenceEquals(previous, feed))
            {
                previous.Dispose();
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Microphone: closing the host capture failed: {e.Message}");
        }

        return previous;
    }

    /// <summary>A process exec recreates the modules: the capture the previous image left open is closed.</summary>
    public override void Initialize()
    {
        Emu?.Host.ReplaceMicrophoneFeed(Emu, null);
    }

    /// <summary>A loaded state: the host capture follows the restored port (kept when the format is the same, reopened
    /// with the saved one, closed without a port).</summary>
    void AfterSnapshotLoad()
    {
        InPort? inPort;
        lock (this)
        {
            inPort = _inPort;
        }

        if (inPort == null)
        {
            CloseMicFeed();
        }
        else
        {
            EnsureMicFeed(inPort);
        }
    }

    /// <summary>sceAudioInGetAdopt(type): VOICE/RAW (0, 2), else 0x80260104; 1 when the caller's process is the
    /// adopted one (AppMgr's, else the foreground process) and has its port open.</summary>
    [Hle]
    int sceAudioInGetAdopt(int type)
    {
        if (type is not (0 or 2))
        {
            return IN_ERR_INVALID_PORT_TYPE;
        }

        return HasInPort() && InAdoptedPid == InOwnerPid ? 1 : 0;
    }

    [Hle]
    int sceAudioInGetInput() => _inputRoute;

    [Hle]
    int sceAudioInSelectInput(int mode)
    {
        if (_inputRoute == mode)
        {
            return 0;
        }

        _inputRoute = mode;
        if (_inPort is { } inPort)
        {
            inPort.RouteReports = 2;
        }

        return 0;
    }

    /// <summary>1 = MUTE, 2 = UNMUTE; other commands are ignored and returned unchanged.</summary>
    [Hle]
    int sceAudioInSetMute(int command)
    {
        if (command is not (1 or 2))
        {
            return command;
        }

        _micMuted = command == 1;
        return _micMuted ? 1 : 0;
    }

    [Hle]
    int sceAudioInSetMicGain(int port, int gain)
    {
        InPort? inPort = InGet(port);
        if (inPort == null)
        {
            return IN_ERR_INVALID_PORT_TYPE;
        }

        inPort.MicGain = (short)gain;
        return 0;
    }

    [Hle]
    int sceAudioInGetMicGain(int port) => InGet(port) is { } inPort ? inPort.MicGain : IN_ERR_INVALID_PORT_TYPE;
}
