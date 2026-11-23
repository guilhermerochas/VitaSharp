using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceAVConfigForDriver (os0:kd/bootimage.skprx SceAVConfig, FW 3.74, read in the module, study only): the kernel side
// of the same module whose user entries the bridge serves (AvConfigBbmc.cs), so its state lives in the same object.
// Two switches the worker follows (NID_0E60701A: 0..1, NID_5662E799: 0..2) and the output request table SceAppMgr
// drives: 16 slots, a lower slot outranks a higher one, and the output the worker applies is the request of the
// highest-ranked active slot (none: the module's default). On the console each change is posted to the module's
// worker thread and the caller waits for it in 40 ms steps; here the change is applied at once.
public sealed partial class VshBridge
{
    /// <summary>SceAVConfigForDriver: a slot, switch or mode out of range, or a slot that holds no request.</summary>
    const int AVC_ERR_INVALID_ARGUMENT = unchecked((int)0x803A0101);

    /// <summary>The request slots of the output table.</summary>
    internal const int AvcOutputSlots = 16;

    [SnapshotMissing] // states of builds before it: no request was made
    readonly int[] _avcRequestActive = new int[AvcOutputSlots];

    [SnapshotMissing]
    readonly int[] _avcRequestEnable = new int[AvcOutputSlots];

    [SnapshotMissing]
    readonly int[] _avcRequestMode = new int[AvcOutputSlots];

    [SnapshotMissing]
    readonly int[] _avcRequestParam = new int[AvcOutputSlots];

    [SnapshotMissing]
    int _avcRequestCount;

    [SnapshotMissing]
    int _avcAppliedSlot = -1;

    [SnapshotMissing]
    int _avcKernelSwitch;

    [SnapshotMissing]
    int _avcKernelMode;

    /// <summary>The slot whose request the worker applies (-1: none, the module's default output).</summary>
    internal int AvcAppliedOutputSlot => _avcAppliedSlot;

    /// <summary>The switch NID_0E60701A sets (0..1).</summary>
    internal int AvcKernelSwitch => _avcKernelSwitch;

    /// <summary>The mode NID_5662E799 sets (0..2).</summary>
    internal int AvcKernelMode => _avcKernelMode;

    /// <summary>NID_0E60701A (SceAVConfigForDriver)(uint value): a switch 0..1 the worker follows (event 0x1000);
    /// 0x803A0101 above 1.</summary>
    [Hle("NID_0E60701A", Nid = 0x0E60701A, Library = "SceAVConfigForDriver")]
    int AvcKernelSetSwitch(uint value)
    {
        if (value >= 2)
        {
            return AVC_ERR_INVALID_ARGUMENT;
        }

        _avcKernelSwitch = (int)value;
        return 0;
    }

    /// <summary>NID_5662E799 (SceAVConfigForDriver)(uint mode): a mode 0..2 the worker follows (event 0x10000);
    /// 0x803A0101 above 2.</summary>
    [Hle("NID_5662E799", Nid = 0x5662E799, Library = "SceAVConfigForDriver")]
    int AvcKernelSetMode(uint mode)
    {
        if (mode >= 3)
        {
            return AVC_ERR_INVALID_ARGUMENT;
        }

        _avcKernelMode = (int)mode;
        return 0;
    }

    /// <summary>NID_2A2E9042 (SceAVConfigForDriver)(uint slot, uint enable, uint mode, int param): makes an output
    /// request in a slot (0..15; enable 0..1, mode 0..4, else 0x803A0101). Under a higher-ranked active slot the
    /// request is only kept; as the highest-ranked new request it is applied. A slot that already leads keeps its
    /// first request.</summary>
    [Hle("NID_2A2E9042", Nid = 0x2A2E9042, Library = "SceAVConfigForDriver")]
    int AvcKernelRequestOutput(uint slot, uint enable, uint mode, int param)
    {
        if (slot >= AvcOutputSlots || enable >= 2 || mode >= 5)
        {
            return AVC_ERR_INVALID_ARGUMENT;
        }

        int index = (int)slot;
        if (AvcHigherSlotActive(index))
        {
            if (_avcRequestActive[index] != 1)
            {
                _avcRequestActive[index] = 1;
                _avcRequestCount++;
            }

            AvcKeepRequest(index, enable, mode, param);
            return 0;
        }

        if (_avcRequestActive[index] != 1)
        {
            _avcRequestActive[index] = 1;
            _avcRequestCount++;
            AvcKeepRequest(index, enable, mode, param);
            _avcAppliedSlot = index;
        }

        return 0;
    }

    /// <summary>NID_3817CDA1 (SceAVConfigForDriver)(...): the module's other entry to NID_2A2E9042, with its
    /// arguments.</summary>
    [Hle("NID_3817CDA1", Nid = 0x3817CDA1, Library = "SceAVConfigForDriver")]
    int AvcKernelRequestOutputAlias(uint slot, uint enable, uint mode, int param)
    {
        return AvcKernelRequestOutputThroughEntry(slot, enable, mode, param);
    }

    /// <summary>NID_91792D6F (SceAVConfigForDriver)(uint slot): drops a slot's request (0x803A0101 when it holds
    /// none). The last request gone, the module's default output returns; under a higher-ranked active slot nothing
    /// else changes; otherwise the next active slot's request is applied.</summary>
    [Hle("NID_91792D6F", Nid = 0x91792D6F, Library = "SceAVConfigForDriver")]
    int AvcKernelReleaseOutput(uint slot)
    {
        if (slot >= AvcOutputSlots || _avcRequestActive[slot] != 1)
        {
            return AVC_ERR_INVALID_ARGUMENT;
        }

        int index = (int)slot;
        _avcRequestActive[index] = 0;
        _avcRequestCount--;
        if (_avcRequestCount == 0)
        {
            _avcAppliedSlot = -1;
            return 0;
        }

        if (AvcHigherSlotActive(index))
        {
            return 0;
        }

        for (int next = index + 1; next < AvcOutputSlots; next++)
        {
            if (_avcRequestActive[next] == 1)
            {
                _avcAppliedSlot = next;
                return 0;
            }
        }

        return AVC_ERR_INVALID_ARGUMENT;
    }

    /// <summary>NID_D8B7AE3D (SceAVConfigForDriver)(uint slot, int param): changes the parameter of a slot's request
    /// (0x803A0101 when it holds none); applied when the slot leads.</summary>
    [Hle("NID_D8B7AE3D", Nid = 0xD8B7AE3D, Library = "SceAVConfigForDriver")]
    int AvcKernelChangeOutputParam(uint slot, int param)
    {
        if (slot >= AvcOutputSlots || _avcRequestActive[slot] != 1)
        {
            return AVC_ERR_INVALID_ARGUMENT;
        }

        _avcRequestParam[slot] = param;
        if (!AvcHigherSlotActive((int)slot))
        {
            _avcAppliedSlot = (int)slot;
        }

        return 0;
    }

    /// <summary>The applied output: whether a request leads, and its enable, mode and parameter.</summary>
    internal (bool Requested, int Enable, int Mode, int Param) AvcAppliedOutput()
    {
        if (_avcAppliedSlot < 0)
        {
            return (false, 0, 0, 0);
        }

        int slot = _avcAppliedSlot;
        return (true, _avcRequestEnable[slot], _avcRequestMode[slot], _avcRequestParam[slot]);
    }

    bool AvcHigherSlotActive(int slot)
    {
        for (int higher = 0; higher < slot; higher++)
        {
            if (_avcRequestActive[higher] == 1)
            {
                return true;
            }
        }

        return false;
    }

    void AvcKeepRequest(int slot, uint enable, uint mode, int param)
    {
        _avcRequestEnable[slot] = (int)enable;
        _avcRequestMode[slot] = (int)mode;
        _avcRequestParam[slot] = param;
    }
}
