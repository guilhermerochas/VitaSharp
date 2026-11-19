using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceTouchForDriver (os0:kd/bootimage.skprx SceTouch 3.74, read in the module, study only), the entries that only keep
// a value or that the module leaves empty: ksceTouchRegisterVirtualTouchDriver and ksceTouchSetTouchEmulationData
// answer 0 without doing anything on 3.74; the CtrlP callback (NULL: 0x80350001) and NID_384F6C68's per-port word
// (port 0 front, 1 back, else 0x80350001) are kept for the module's sampling.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceTouch: a NULL argument or a port above 1 (0x80350001).</summary>
    const int TOUCH_ERR_INVALID_ARG = unchecked((int)0x80350001);

    [SnapshotMissing] // states of builds before it: nothing set
    uint _touchCtrlpCallback;

    [SnapshotMissing]
    readonly uint[] _touchPortWord = new uint[2];

    /// <summary>The CtrlP callback ksceTouchSetCtrlpCallback keeps, and NID_384F6C68's word of a port.</summary>
    internal (uint Callback, uint Front, uint Back) TouchKernelState => (_touchCtrlpCallback, _touchPortWord[0], _touchPortWord[1]);

    /// <summary>ksceTouchRegisterVirtualTouchDriver(driver): empty on 3.74; 0.</summary>
    [Hle(Nid = 0x03C3AC7C, Library = "SceTouchForDriver")]
    int ksceTouchRegisterVirtualTouchDriver(uint driver)
    {
        return 0;
    }

    /// <summary>ksceTouchSetTouchEmulationData(data): empty on 3.74; 0.</summary>
    [Hle(Nid = 0xA32529B8, Library = "SceTouchForDriver")]
    int ksceTouchSetTouchEmulationData(uint data)
    {
        return 0;
    }

    /// <summary>ksceTouchSetCtrlpCallback(callback): kept; NULL is 0x80350001.</summary>
    [Hle(Nid = 0x628D9324, Library = "SceTouchForDriver")]
    int ksceTouchSetCtrlpCallback(uint callback)
    {
        if (callback == 0)
        {
            return TOUCH_ERR_INVALID_ARG;
        }

        _touchCtrlpCallback = callback;
        return 0;
    }

    /// <summary>NID_384F6C68 (SceTouchForDriver)(uint port, uint value): a word kept per port.</summary>
    [Hle("NID_384F6C68", Nid = 0x384F6C68, Library = "SceTouchForDriver")]
    int TouchSetPortWord(uint port, uint value)
    {
        if (port >= 2)
        {
            return TOUCH_ERR_INVALID_ARG;
        }

        _touchPortWord[port] = value;
        return 0;
    }

    /// <summary>The words NID_3F3FBF69's table holds.</summary>
    const int TouchTableWords = 16;

    [SnapshotMissing] // states of builds before it: nothing set
    readonly ushort[] _touchPortBytes = new ushort[2];

    [SnapshotMissing]
    readonly uint[] _touchTable = new uint[TouchTableWords];

    [SnapshotMissing]
    uint _touchTableCount;

    /// <summary>NID_9C8CCB66's bytes of a port (first | second &lt;&lt; 8) and NID_3F3FBF69's count, for the tests.</summary>
    internal (ushort Front, ushort Back, uint TableCount, uint FirstWord) TouchKernelTable =>
        (_touchPortBytes[0], _touchPortBytes[1], _touchTableCount, _touchTable[0]);

    /// <summary>NID_9C8CCB66 (SceTouchForDriver)(uint port, uint8 first, uint8 second): two bytes kept per port (port
    /// 0 front, 1 back, else 0x80350001); 0.</summary>
    [Hle("NID_9C8CCB66", Nid = 0x9C8CCB66, Library = "SceTouchForDriver")]
    int TouchSetPortBytes(uint port, uint first, uint second)
    {
        if (port >= 2)
        {
            return TOUCH_ERR_INVALID_ARG;
        }

        _touchPortBytes[port] = (ushort)((first & 0xFF) | ((second & 0xFF) << 8));
        return 0;
    }

    /// <summary>NID_3F3FBF69 (SceTouchForDriver)(const uint *table): the count (table[0]) and its words (table[1..]),
    /// kept; NULL is 0x80350001.</summary>
    [Hle("NID_3F3FBF69", Nid = 0x3F3FBF69, Library = "SceTouchForDriver")]
    int TouchSetTable(uint table)
    {
        if (table == 0)
        {
            return TOUCH_ERR_INVALID_ARG;
        }

        uint count = GuestMemory.R32(table);
        _touchTableCount = count;
        uint kept = Math.Min(count, TouchTableWords);
        for (uint word = 0; word < kept; word++)
        {
            _touchTable[word] = GuestMemory.R32(table + 4 + word * 4);
        }

        return 0;
    }
}
