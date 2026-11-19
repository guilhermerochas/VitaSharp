using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceAudioInForDriver (os0:kd/audioin.skprx SceAudioin 3.74, read in the module, study only): the kernel side of the
// microphone ports. The module keeps one port per process (four slots, each with its owner's pid), the pid AppMgr
// adopts (the one whose captures reach the microphone, -2 = none at boot; the others' ports are flagged not adopted),
// a callback called with the owner's pid after each open and each release, and a Bluetooth PCM ring that the
// Bluetooth headset driver fills while the input route is Bluetooth (0x10; any other route: -1). Here each process's
// Audio module holds its own port, so "the slots" are the ports of every process; the system-wide values (adopted
// pid, callbacks) live in the kernel process's Audio module (the caller's when there is none yet). Until AppMgr
// adopts a pid, the foreground process is the adopted one: the firmware's AppMgr adopts the application it brings to
// the foreground, which the emulator's own foreground switch stands for.
public sealed partial class Audio
{
    /// <summary>SceAudioIn: the input route of the Bluetooth headset (sceAudioInSelectInput 0x10).</summary>
    const int InRouteBluetooth = 0x10;

    /// <summary>The slots of the module's port table.</summary>
    const int InPortSlots = 4;

    [SnapshotMissing] // states of builds before it: no callback set
    uint _inOpenCallback;

    [SnapshotMissing]
    uint _inReleaseCallback;

    [SnapshotMissing] // states of builds before it: AppMgr adopted no pid (the foreground process is)
    bool _inAdoptSet;

    [SnapshotMissing]
    int _inAdoptedPid;

    [SnapshotMissing]
    int _inAdoptFlag;

    [SnapshotMissing] // states of builds before it: no Bluetooth PCM submitted
    long _inBluetoothSamples;

    /// <summary>The Audio module holding the system-wide audio-in values: the kernel process's, else this one's (also a
    /// module made without a process).</summary>
    Audio InKernel
    {
        get
        {
            if (Emu?.Vita.KernelProcess is not { } kernel)
            {
                return this;
            }

            return kernel.Hle.Modules.OfType<Audio>().FirstOrDefault() ?? this;
        }
    }

    /// <summary>The pid of this module's process (0 for a module made without one).</summary>
    int InOwnerPid => Emu?.Pid ?? 0;

    /// <summary>The Audio modules of every process, the kernel process's included (the module's port slots).</summary>
    IEnumerable<Audio> InEveryProcess
    {
        get
        {
            if (Emu == null)
            {
                yield return this;
                yield break;
            }

            foreach (Emulator process in Emu.Vita.ProcessesAndKernel)
            {
                foreach (Audio audio in process.Hle.Modules.OfType<Audio>())
                {
                    yield return audio;
                }
            }
        }
    }

    /// <summary>The pid whose captures reach the microphone: the one AppMgr adopted, else the foreground process.</summary>
    internal int InAdoptedPid
    {
        get
        {
            Audio kernel = InKernel;
            if (kernel._inAdoptSet)
            {
                return kernel._inAdoptedPid;
            }

            return Emu == null ? 0 : (Emu.Vita.Foreground ?? Emu).Pid;
        }
    }

    /// <summary>The adopt flag (AppMgr's 4th argument) and the Bluetooth samples submitted, for the tests.</summary>
    internal (int Flag, long BluetoothSamples) InKernelValues => (InKernel._inAdoptFlag, InKernel._inBluetoothSamples);

    bool HasInPort()
    {
        lock (this)
        {
            return _inPort != null;
        }
    }

    /// <summary>The open/release callback (kernel code) called with <paramref name="pid"/>, after the port's lock.</summary>
    static void CallInPortCallback(uint callback, int pid)
    {
        if (callback != 0 && GuestThread.Current is { } thread)
        {
            thread.CallGuest(callback, (uint)pid);
        }
    }

    /// <summary>ksceAudioInIsAnyPortOpen(): 1 while any process has its microphone port open.</summary>
    [Hle(Nid = 0x005A2A31, Library = "SceAudioInForDriver")]
    int ksceAudioInIsAnyPortOpen()
    {
        foreach (Audio audio in InEveryProcess)
        {
            if (audio.HasInPort())
            {
                return 1;
            }
        }

        return 0;
    }

    /// <summary>ksceAudioInIsProcessPortOwner(int pid): 1 when the process owns a port. The module compares the pid
    /// with each slot's owner, a free slot's being 0: pid 0 answers 1 while a slot is free.</summary>
    [Hle(Nid = 0xFB46FA10, Library = "SceAudioInForDriver")]
    int ksceAudioInIsProcessPortOwner(int pid)
    {
        int open = 0;
        foreach (Audio audio in InEveryProcess)
        {
            if (audio.HasInPort())
            {
                open++;
                if (audio.InOwnerPid == pid)
                {
                    return 1;
                }
            }
        }

        return pid == 0 && open < InPortSlots ? 1 : 0;
    }

    /// <summary>ksceAudioInSetAdoptForPid(int pid, int, int, int flag): the process whose captures reach the
    /// microphone; a non-zero flag marks even its port not adopted (kept: the captures are silent either way). 0.</summary>
    [Hle(Nid = 0xE4042F6F, Library = "SceAudioInForDriver")]
    int ksceAudioInSetAdoptForPid(int pid, uint unused1, uint unused2, int flag)
    {
        Audio kernel = InKernel;
        lock (kernel)
        {
            kernel._inAdoptSet = true;
            kernel._inAdoptedPid = pid;
            kernel._inAdoptFlag = flag;
        }

        return 0;
    }

    /// <summary>ksceAudioInSetPortOpenCallback(callback): called with the owner's pid after each open (NULL: none).
    /// 0.</summary>
    [Hle(Nid = 0x3EC79053, Library = "SceAudioInForDriver")]
    int ksceAudioInSetPortOpenCallback(uint callback)
    {
        InKernel._inOpenCallback = callback;
        return 0;
    }

    /// <summary>ksceAudioInSetPortReleaseCallback(callback): called with the owner's pid after each release (NULL:
    /// none). 0.</summary>
    [Hle(Nid = 0x197CAA1B, Library = "SceAudioInForDriver")]
    int ksceAudioInSetPortReleaseCallback(uint callback)
    {
        InKernel._inReleaseCallback = callback;
        return 0;
    }

    /// <summary>ksceAudioInSubmitBluetoothPcm(const short *pcm, unsigned samples): the Bluetooth headset's samples, taken
    /// while some process's input route is Bluetooth (0x10), else -1. The emulated microphone captures silence, so the
    /// samples are counted, not played back.</summary>
    [Hle(Nid = 0x3D164387, Library = "SceAudioInForDriver")]
    int ksceAudioInSubmitBluetoothPcm(uint pcm, uint samples)
    {
        bool bluetooth = false;
        foreach (Audio audio in InEveryProcess)
        {
            if (audio._inputRoute == InRouteBluetooth)
            {
                bluetooth = true;
                break;
            }
        }

        if (!bluetooth)
        {
            return -1;
        }

        Audio kernel = InKernel;
        lock (kernel)
        {
            kernel._inBluetoothSamples += samples;
        }

        return 0;
    }
}
