using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceAudioForDriver (the audio output driver of the FW 3.74 bootimage's SceAudio, decompiled, study only): the
// per-process port settings the application manager changes (each process has three port kinds: 0 main, 1 BGM, 2
// voice; another kind 0xC026000A), the monitor output (the sound sent to the system's capture: channel mode 1 or 2,
// rate 8000 or 48000), the master gain (up to 0x8000), the I2S routing and the mixers' inputs and rates, and the
// capture of the I2S0 transmit stream. The mixing itself is VitaSharp's host audio, so the settings are kept as the
// driver keeps them and the capture/monitor streams carry nothing (the host does not loop its output back).
public sealed partial class Audio
{
    const int OutDriverBadPortKind = unchecked((int)0xC026000A);
    const int OutDriverBadChannelMode = unchecked((int)0x80260007);
    const int OutDriverBadSampleRate = unchecked((int)0x80260008);
    const int OutDriverBadGain = unchecked((int)0x80260009);
    const int OutDriverBadCaptureBuffer = unchecked((int)0x80260102);

    /// <summary>The port volume of each process's port kind (pid * 4 + kind) once set (0x8000 before).</summary>
    [SnapshotMissing] // states of builds before it: none had been set
    readonly Dictionary<int, int> _outDriverVolumes = new();

    /// <summary>The adopt state of each process's port kind (pid * 4 + kind).</summary>
    [SnapshotMissing]
    readonly Dictionary<int, int> _outDriverAdopt = new();

    [SnapshotMissing]
    uint _outDriverOpenCallback, _outDriverReleaseCallback, _outDriverCaptureBuffer, _outDriverCaptureSize;

    [SnapshotMissing(0x8000u)] // states of builds before it: the boot gain
    uint _outDriverMasterGain = 0x8000;

    [SnapshotMissing(true)]
    bool _outDriverMonitorStereo = true;

    [SnapshotMissing]
    bool _outDriverMonitorNarrowband;

    [SnapshotMissing]
    int _outDriverI2sOutputIndex, _outDriverMix2I2sIndex;

    /// <summary>ksceAudioOutGetPortVolumeForPid (0xB05D5B28)(pid, kind): the volume of a port kind of the process (0x8000
    /// until set); another kind 0xC026000A.</summary>
    [Hle(Nid = 0xB05D5B28, Library = "SceAudioForDriver")]
    int ksceAudioOutGetPortVolumeForPid(int pid, int kind)
    {
        if (kind is < 0 or > 2)
        {
            return OutDriverBadPortKind;
        }

        lock (_outDriverVolumes)
        {
            return _outDriverVolumes.TryGetValue(pid * 4 + kind, out int volume) ? volume : 0x8000;
        }
    }

    /// <summary>ksceAudioOutSetPortVolumeForPid (0x629261CD)(pid, kinds, volume, ?): sets the volume (up to 0x100, else
    /// -1) of the port kinds in the mask (bit 1 main, bit 2 BGM, bit 3 voice); an empty mask 0.</summary>
    [Hle(Nid = 0x629261CD, Library = "SceAudioForDriver")]
    int ksceAudioOutSetPortVolumeForPid(int pid, uint kinds, uint volume, uint unused)
    {
        if (kinds == 0)
        {
            return 0;
        }

        if (volume > 0x100)
        {
            return -1;
        }

        lock (_outDriverVolumes)
        {
            for (int kind = 0; kind < 3; kind++)
            {
                if ((kinds & (2u << kind)) != 0)
                {
                    _outDriverVolumes[pid * 4 + kind] = (int)volume;
                }
            }
        }

        return 0;
    }

    /// <summary>ksceAudioOutSetAdoptForPid (0x3440C1CA)(pid, kind, adopt, ?, ?): whether the process's ports of the
    /// kind reach the output; another kind 0xC026000A.</summary>
    [Hle(Nid = 0x3440C1CA, Library = "SceAudioForDriver")]
    int ksceAudioOutSetAdoptForPid(int pid, int kind, int adopt, uint unused1, uint unused2)
    {
        if (kind is < 0 or > 2)
        {
            return OutDriverBadPortKind;
        }

        lock (_outDriverVolumes)
        {
            _outDriverAdopt[pid * 4 + kind] = adopt;
        }

        return 0;
    }

    /// <summary>ksceAudioOutSetPortOpenCallback (0xF07D34C2)(callback); 0.</summary>
    [Hle(Nid = 0xF07D34C2, Library = "SceAudioForDriver")]
    int ksceAudioOutSetPortOpenCallback(uint callback)
    {
        _outDriverOpenCallback = callback;
        return 0;
    }

    /// <summary>ksceAudioOutSetPortReleaseCallback (0xE87902DF)(callback); 0.</summary>
    [Hle(Nid = 0xE87902DF, Library = "SceAudioForDriver")]
    int ksceAudioOutSetPortReleaseCallback(uint callback)
    {
        _outDriverReleaseCallback = callback;
        return 0;
    }

    /// <summary>ksceAudioOutSetMasterGain (0x15FB30DF)(gain): up to 0x8000 (else 0x80260009); 0.</summary>
    [Hle(Nid = 0x15FB30DF, Library = "SceAudioForDriver")]
    int ksceAudioOutSetMasterGain(uint gain)
    {
        if (gain > 0x8000)
        {
            return OutDriverBadGain;
        }

        _outDriverMasterGain = gain;
        return 0;
    }

    /// <summary>ksceAudioOutMonitorSetChannelMode (0xFC375BAC)(mode): 1 stereo or 2 mono (else 0x80260007).</summary>
    [Hle(Nid = 0xFC375BAC, Library = "SceAudioForDriver")]
    int ksceAudioOutMonitorSetChannelMode(int mode)
    {
        if (mode is not (1 or 2))
        {
            return OutDriverBadChannelMode;
        }

        _outDriverMonitorStereo = mode == 1;
        return 0;
    }

    /// <summary>ksceAudioOutMonitorSetSampleRate (0x134C96C1)(rate): 8000 or 48000 (else 0x80260008).</summary>
    [Hle(Nid = 0x134C96C1, Library = "SceAudioForDriver")]
    int ksceAudioOutMonitorSetSampleRate(int rate)
    {
        if (rate is not (8000 or 48000))
        {
            return OutDriverBadSampleRate;
        }

        _outDriverMonitorNarrowband = rate == 8000;
        return 0;
    }

    /// <summary>ksceAudioOutMonitorSetPathMode (0xAC9383D0)(mode): the monitor's source; 0.</summary>
    [Hle(Nid = 0xAC9383D0, Library = "SceAudioForDriver")]
    int ksceAudioOutMonitorSetPathMode(uint mode) => 0;

    /// <summary>ksceAudioOutMonitorRead (0x81EB0AE5)(buffer, ...): the monitor stream; the host output is not looped
    /// back, so nothing is read: 0.</summary>
    [Hle(Nid = 0x81EB0AE5, Library = "SceAudioForDriver")]
    int ksceAudioOutMonitorRead(uint buffer) => 0;

    /// <summary>ksceAudioOutSetI2sOutputIndex (0xA8C38998)(index): 3..7 (else -1); 0.</summary>
    [Hle(Nid = 0xA8C38998, Library = "SceAudioForDriver")]
    int ksceAudioOutSetI2sOutputIndex(int index)
    {
        if ((uint)(index - 3) >= 5)
        {
            return -1;
        }

        _outDriverI2sOutputIndex = index;
        return 0;
    }

    /// <summary>ksceAudioSrcMix2SetInputI2sIndex (0x7E0E1331)(index): below 3 (else -1); 0.</summary>
    [Hle(Nid = 0x7E0E1331, Library = "SceAudioForDriver")]
    int ksceAudioSrcMix2SetInputI2sIndex(uint index)
    {
        if (index >= 3)
        {
            return -1;
        }

        _outDriverMix2I2sIndex = (int)index;
        return 0;
    }

    /// <summary>ksceAudioSrcMix2SetInput0SourceMode (0x85987B88)(mode): the second mixer's input 0 source; 0.</summary>
    [Hle(Nid = 0x85987B88, Library = "SceAudioForDriver")]
    int ksceAudioSrcMix2SetInput0SourceMode(uint mode) => 0;

    /// <summary>ksceAudioSrcMix2SubmitInput0Pcm (0x6F613D36)(pcm, ...): PCM for the second mixer's input 0 (mixed by
    /// the host output); 0.</summary>
    [Hle(Nid = 0x6F613D36, Library = "SceAudioForDriver")]
    int ksceAudioSrcMix2SubmitInput0Pcm(uint pcm) => 0;

    /// <summary>ksceAudioSrcMix2CaptureOutput (0x75915003)(buffer, ...): the second mixer's output for the microphone
    /// driver (nothing looped back); 0.</summary>
    [Hle(Nid = 0x75915003, Library = "SceAudioForDriver")]
    int ksceAudioSrcMix2CaptureOutput(uint buffer) => 0;

    /// <summary>ksceAudioSrcMixSetSampleRates (0x28B3AF10)(mixer, ?, inputRate, outputRate): the sample rate converter's
    /// rates (the host output resamples); 0.</summary>
    [Hle(Nid = 0x28B3AF10, Library = "SceAudioForDriver")]
    int ksceAudioSrcMixSetSampleRates(uint mixer, uint unused, int inputRate, int outputRate) => 0;

    /// <summary>ksceAudioI2s0CaptureTx (0x37517CC9)(buffer, size): captures the I2S0 transmit stream into a 128-byte
    /// aligned buffer of at least 0x40 bytes in 0x20 steps (else 0x80260102; NULL stops): kept, nothing looped
    /// back; 0.</summary>
    [Hle(Nid = 0x37517CC9, Library = "SceAudioForDriver")]
    int ksceAudioI2s0CaptureTx(uint buffer, uint size)
    {
        if ((buffer & 0x7F) != 0 || size < 0x40 || (size & 0x1F) != 0)
        {
            return OutDriverBadCaptureBuffer;
        }

        _outDriverCaptureBuffer = buffer;
        _outDriverCaptureSize = size;
        return 0;
    }
}
