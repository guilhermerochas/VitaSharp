using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceHpremoteForDriver (os0:kd/bootimage.skprx SceHpremote 3.74, read in the module, study only): the headset jack
// and its remote. The module's thread keeps what the jack reports (headphone, remote, microphone, the audio-out dock;
// a negative value while unknown reads as 0), sixteen callback slots (a seventeenth -1; removing one not there -1),
// the connect callback, and answers reset and shutdown by setting its thread's event bits (0). Nothing is plugged into
// the emulated unit's jack: every presence reads 0. The internal state entry hands out the module's own context; the
// emulated module has none to hand out (NULL), its state word 0.
public sealed partial class SystemServicesVsh
{
    const int HprmCallbackSlots = 16;

    [SnapshotMissing] // states of builds before it: no callback
    readonly int[] _hprmCallbacks = new int[HprmCallbackSlots];

    [SnapshotMissing]
    int _hprmCallbackCount;

    [SnapshotMissing]
    uint _hprmConnectCallback;

    [SnapshotMissing]
    uint _hprmConnectArgument;

    /// <summary>The registered callbacks and the connect callback, for the tests.</summary>
    internal (int[] Callbacks, uint Connect, uint Argument) HprmKernelValues =>
        (_hprmCallbacks[.._hprmCallbackCount], _hprmConnectCallback, _hprmConnectArgument);

    /// <summary>ksceHprmGetHpDetect(): the jack's detection: nothing plugged in (0).</summary>
    [Hle(Nid = 0x71B5FB67, Library = "SceHpremoteForDriver")]
    int ksceHprmGetHpDetect()
    {
        return 0;
    }

    /// <summary>ksceHprmIsAudioOutDockExist(): no audio-out dock (0).</summary>
    [Hle(Nid = 0x2229EF51, Library = "SceHpremoteForDriver")]
    int ksceHprmIsAudioOutDockExist()
    {
        return 0;
    }

    /// <summary>ksceHprmIsHeadphoneExist(): no headphone (0).</summary>
    [Hle(Nid = 0x7E69EDA4, Library = "SceHpremoteForDriver")]
    int ksceHprmIsHeadphoneExist()
    {
        return 0;
    }

    /// <summary>ksceHprmIsRemoteExist(): no remote (0).</summary>
    [Hle(Nid = 0x208DB1BD, Library = "SceHpremoteForDriver")]
    int ksceHprmIsRemoteExist()
    {
        return 0;
    }

    /// <summary>ksceHprmIsMicrophoneExist(): no headset microphone (0).</summary>
    [Hle(Nid = 0x219C58F1, Library = "SceHpremoteForDriver")]
    int ksceHprmIsMicrophoneExist()
    {
        return 0;
    }

    /// <summary>ksceHprmSetConnectCallback(callback, argument): kept; 0.</summary>
    [Hle(Nid = 0xD22913DB, Library = "SceHpremoteForDriver")]
    int ksceHprmSetConnectCallback(uint callback, uint argument)
    {
        _hprmConnectCallback = callback;
        _hprmConnectArgument = argument;
        return 0;
    }

    /// <summary>ksceHprmRegisterCallback(SceUID cb): the first free of sixteen slots; all taken: -1.</summary>
    [Hle(Nid = 0xC7154136, Library = "SceHpremoteForDriver")]
    int ksceHprmRegisterCallback(int callback)
    {
        if (_hprmCallbackCount >= HprmCallbackSlots)
        {
            return -1;
        }

        _hprmCallbacks[_hprmCallbackCount] = callback;
        _hprmCallbackCount++;
        return 0;
    }

    /// <summary>ksceHprmUnregisterCallback(SceUID cb): frees its slot; not registered: -1.</summary>
    [Hle(Nid = 0x68D64A0F, Library = "SceHpremoteForDriver")]
    int ksceHprmUnregisterCallback(int callback)
    {
        int at = Array.IndexOf(_hprmCallbacks, callback, 0, _hprmCallbackCount);
        if (at < 0)
        {
            return -1;
        }

        Array.Copy(_hprmCallbacks, at + 1, _hprmCallbacks, at, _hprmCallbackCount - at - 1);
        _hprmCallbackCount--;
        _hprmCallbacks[_hprmCallbackCount] = 0;
        return 0;
    }

    /// <summary>ksceHprmShutdown(): asks the module's thread to stop the jack; 0.</summary>
    [Hle(Nid = 0x7735C403, Library = "SceHpremoteForDriver")]
    int ksceHprmShutdown()
    {
        return 0;
    }

    /// <summary>ksceHprmReset(): asks the module's thread to probe the jack again; 0.</summary>
    [Hle(Nid = 0x4D1E622C, Library = "SceHpremoteForDriver")]
    int ksceHprmReset()
    {
        return 0;
    }

    /// <summary>ksceHprmGetInternalState(void **context): the module's context (none here: NULL) and its state word (0).</summary>
    [Hle(Nid = 0x7B038374, Library = "SceHpremoteForDriver", Default = true)]
    int ksceHprmGetInternalState(uint context)
    {
        if (context != 0)
        {
            GuestMemory.W32(context, 0);
        }

        return 0;
    }
}
