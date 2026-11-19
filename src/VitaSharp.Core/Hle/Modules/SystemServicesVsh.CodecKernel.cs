namespace VitaSharp.Core.Hle.Modules;

// SceCodecForDriver (os0:kd/bootimage.skprx SceCodec 3.74, read in the module, study only): the audio codec chip's
// driver (SceAudio, SceAudioin, SceAVConfig and SceHpremote use it). Each entry calls the chip backend's function in
// the module's ops table (picked at module start; none: every entry answers 0), which programs the chip over I2C (bus
// 0, device 0x34). The emulated unit's sound goes to the host's audio device (SceAudio HLE), with no codec chip to
// program: the entries answer as the chip accepting each setting (0), a register reads 0 and no microphone is
// detected on the headset jack (0); they are defaults.
public sealed partial class SystemServicesVsh
{
    /// <summary>ksceCodecSetAudioOutMode(int mode): the chip's output mode; 0.</summary>
    [Hle(Nid = 0xCCDE1B04, Library = "SceCodecForDriver", Default = true)]
    int ksceCodecSetAudioOutMode(int mode)
    {
        return 0;
    }

    /// <summary>ksceCodecGetOutputState(): the chip's output state; the host device is not the chip: 0.</summary>
    [Hle(Nid = 0x41CFFE48, Library = "SceCodecForDriver", Default = true)]
    int ksceCodecGetOutputState()
    {
        return 0;
    }

    /// <summary>ksceCodecSetAudioInputMode(int mode): the chip's input path (SceAudioin's route); 0.</summary>
    [Hle(Nid = 0xC1DB7BAF, Library = "SceCodecForDriver", Default = true)]
    int ksceCodecSetAudioInputMode(int mode)
    {
        return 0;
    }

    /// <summary>ksceCodecGetMicrophoneDetectStatus(): no microphone on the headset jack (0).</summary>
    [Hle(Nid = 0xCF398D8A, Library = "SceCodecForDriver", Default = true)]
    int ksceCodecGetMicrophoneDetectStatus()
    {
        return 0;
    }

    /// <summary>ksceCodecSetOutputRoute(int route): the chip's output route; 0.</summary>
    [Hle(Nid = 0x762BF716, Library = "SceCodecForDriver", Default = true)]
    int ksceCodecSetOutputRoute(int route)
    {
        return 0;
    }

    /// <summary>ksceCodecReadRegister(uint reg): a chip register; 0.</summary>
    [Hle(Nid = 0x4FA36937, Library = "SceCodecForDriver", Default = true)]
    int ksceCodecReadRegister(uint register)
    {
        return 0;
    }

    /// <summary>ksceCodecSetVolume(int volume): the chip's volume (SceAVConfig); 0.</summary>
    [Hle(Nid = 0x8932F886, Library = "SceCodecForDriver", Default = true)]
    int ksceCodecSetVolume(int volume)
    {
        return 0;
    }

    /// <summary>ksceCodecSetMicrophoneDetectPower(int on): the jack's microphone detection; 0.</summary>
    [Hle(Nid = 0xE483B663, Library = "SceCodecForDriver", Default = true)]
    int ksceCodecSetMicrophoneDetectPower(int on)
    {
        return 0;
    }

    /// <summary>ksceCodecWriteRegister(uint reg, uint value): a chip register; 0.</summary>
    [Hle(Nid = 0x23BF8884, Library = "SceCodecForDriver", Default = true)]
    int ksceCodecWriteRegister(uint register, uint value)
    {
        return 0;
    }
}
