using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The parts of the FW 3.74 SceSyscon kernel module (syscon.skprx; decompiled, study only) that do not talk to the
/// syscon chip: the event handler registrations (a table of (callback, argument) per event number that the module's
/// interrupt handler calls when the chip reports the event: low battery, thermal alert, alarm, ...), the debug
/// handlers and the module's flags. The handlers are called when the chip changes (SysconKernel.Events.cs).
/// </summary>
public sealed partial class SysconKernel : HleModule
{
    /// <summary>The events the module's table has room for (indices 0..38).</summary>
    const int EventCount = 39;

    /// <summary>Each event's (callback, argument), as registered.</summary>
    [SnapshotMissing] // states of builds before it: nothing registered
    readonly uint[] _eventHandlers = new uint[EventCount * 2];

    /// <summary>The words the module keeps for its other setters: [0] the debug handlers, [1] NID_901D6CD4's flag,
    /// [2] NID_0D58E297's flag.</summary>
    [SnapshotMissing]
    readonly uint[] _words = new uint[3];

    /// <summary>The registered handler of an event: its callback and argument.</summary>
    internal (uint Callback, uint Argument) EventHandler(int index)
    {
        lock (_eventHandlers)
        {
            return (_eventHandlers[index * 2], _eventHandlers[index * 2 + 1]);
        }
    }

    int SetEventHandler(int index, uint callback, uint argument)
    {
        lock (_eventHandlers)
        {
            _eventHandlers[index * 2] = callback;
            _eventHandlers[index * 2 + 1] = argument;
        }

        StartChipEvents();
        return 0;
    }

    /// <summary>ksceSysconSetDebugHandlers(handlers): keeps the debug handlers; 0.</summary>
    [Hle(Nid = 0xF245CD6F, Library = "SceSysconForDriver")]
    int ksceSysconSetDebugHandlers(uint handlers)
    {
        _words[0] = handlers;
        return 0;
    }

    /// <summary>NID_901D6CD4(flag): keeps the flag the controller and power modules set; 0.</summary>
    [Hle(Nid = 0x901D6CD4, Library = "SceSysconForDriver")]
    int NID_901D6CD4(uint flag)
    {
        _words[1] = flag;
        return 0;
    }

    /// <summary>NID_0D58E297(flag): keeps the flag (the firmware also changes the priority of the syscon interrupt,
    /// which VitaSharp does not have); 0.</summary>
    [Hle(Nid = 0x0D58E297, Library = "SceSysconForDriver")]
    int NID_0D58E297(uint flag)
    {
        _words[2] = flag;
        return 0;
    }

    /// <summary>ksceSysconWaitInitialized(): waits for the module's start to finish talking to the chip; the module is
    /// up once the emulated system is, so it returns 0 at once.</summary>
    [Hle(Nid = 0x55DF1C9B, Library = "SceSysconForDriver")]
    int ksceSysconWaitInitialized() => 0;

    /// <summary>NID_18A6F4D9(callback, argument): registers the handler of syscon event 0 (0 removes it); 0.</summary>
    [Hle(Nid = 0x18A6F4D9, Library = "SceSysconForDriver")]
    int NID_18A6F4D9(uint callback, uint argument) => SetEventHandler(0, callback, argument);

    /// <summary>NID_DE613081(callback, argument): registers the handler of syscon event 1 (0 removes it); 0.</summary>
    [Hle(Nid = 0xDE613081, Library = "SceSysconForDriver")]
    int NID_DE613081(uint callback, uint argument) => SetEventHandler(1, callback, argument);

    /// <summary>NID_229A07C2(callback, argument): registers the handler of syscon event 2 (0 removes it); 0.</summary>
    [Hle(Nid = 0x229A07C2, Library = "SceSysconForDriver")]
    int NID_229A07C2(uint callback, uint argument) => SetEventHandler(2, callback, argument);

    /// <summary>NID_8351526D(callback, argument): registers the handler of syscon event 3 (0 removes it); 0.</summary>
    [Hle(Nid = 0x8351526D, Library = "SceSysconForDriver")]
    int NID_8351526D(uint callback, uint argument) => SetEventHandler(3, callback, argument);

    /// <summary>NID_9F8340FF(callback, argument): registers the handler of syscon event 4 (0 removes it); 0.</summary>
    [Hle(Nid = 0x9F8340FF, Library = "SceSysconForDriver")]
    int NID_9F8340FF(uint callback, uint argument) => SetEventHandler(4, callback, argument);

    /// <summary>NID_35E1689F(callback, argument): registers the handler of syscon event 5 (0 removes it); 0.</summary>
    [Hle(Nid = 0x35E1689F, Library = "SceSysconForDriver")]
    int NID_35E1689F(uint callback, uint argument) => SetEventHandler(5, callback, argument);

    /// <summary>NID_474A9EA7(callback, argument): registers the handler of syscon event 6 (0 removes it); 0.</summary>
    [Hle(Nid = 0x474A9EA7, Library = "SceSysconForDriver")]
    int NID_474A9EA7(uint callback, uint argument) => SetEventHandler(6, callback, argument);

    /// <summary>NID_4E88B4D9(callback, argument): registers the handler of syscon event 8 (0 removes it); 0.</summary>
    [Hle(Nid = 0x4E88B4D9, Library = "SceSysconForDriver")]
    int NID_4E88B4D9(uint callback, uint argument) => SetEventHandler(8, callback, argument);

    /// <summary>NID_376CCCB8(callback, argument): registers the handler of syscon event 9 (0 removes it); 0.</summary>
    [Hle(Nid = 0x376CCCB8, Library = "SceSysconForDriver")]
    int NID_376CCCB8(uint callback, uint argument) => SetEventHandler(9, callback, argument);

    /// <summary>NID_3BAAC8A9(callback, argument): registers the handler of syscon event 10 (0 removes it); 0.</summary>
    [Hle(Nid = 0x3BAAC8A9, Library = "SceSysconForDriver")]
    int NID_3BAAC8A9(uint callback, uint argument) => SetEventHandler(10, callback, argument);

    /// <summary>NID_4A42712F(callback, argument): registers the handler of syscon event 15 (0 removes it); 0.</summary>
    [Hle(Nid = 0x4A42712F, Library = "SceSysconForDriver")]
    int NID_4A42712F(uint callback, uint argument) => SetEventHandler(15, callback, argument);

    /// <summary>ksceSysconSetLowBatteryCallback(callback, argument): registers the handler of syscon event 16 (0 removes it); 0.</summary>
    [Hle(Nid = 0x3F0DB7C0, Library = "SceSysconForDriver")]
    int ksceSysconSetLowBatteryCallback(uint callback, uint argument) => SetEventHandler(16, callback, argument);

    /// <summary>NID_80D6E061(callback, argument): registers the handler of syscon event 17 (0 removes it); 0.</summary>
    [Hle(Nid = 0x80D6E061, Library = "SceSysconForDriver")]
    int NID_80D6E061(uint callback, uint argument) => SetEventHandler(17, callback, argument);

    /// <summary>NID_7682FE69(callback, argument): registers the handler of syscon event 18 (0 removes it); 0.</summary>
    [Hle(Nid = 0x7682FE69, Library = "SceSysconForDriver")]
    int NID_7682FE69(uint callback, uint argument) => SetEventHandler(18, callback, argument);

    /// <summary>NID_E0D52DF0(callback, argument): registers the handler of syscon event 19 (0 removes it); 0.</summary>
    [Hle(Nid = 0xE0D52DF0, Library = "SceSysconForDriver")]
    int NID_E0D52DF0(uint callback, uint argument) => SetEventHandler(19, callback, argument);

    /// <summary>NID_2D471528(callback, argument): registers the handler of syscon event 20 (0 removes it); 0.</summary>
    [Hle(Nid = 0x2D471528, Library = "SceSysconForDriver")]
    int NID_2D471528(uint callback, uint argument) => SetEventHandler(20, callback, argument);

    /// <summary>NID_129EA022(callback, argument): registers the handler of syscon event 21 (0 removes it); 0.</summary>
    [Hle(Nid = 0x129EA022, Library = "SceSysconForDriver")]
    int NID_129EA022(uint callback, uint argument) => SetEventHandler(21, callback, argument);

    /// <summary>NID_85E5DEBF(callback, argument): registers the handler of syscon event 22 (0 removes it); 0.</summary>
    [Hle(Nid = 0x85E5DEBF, Library = "SceSysconForDriver")]
    int NID_85E5DEBF(uint callback, uint argument) => SetEventHandler(22, callback, argument);

    /// <summary>ksceSysconSetThermalAlertCallback(callback, argument): registers the handler of syscon event 23 (0 removes it); 0.</summary>
    [Hle(Nid = 0x773B8126, Library = "SceSysconForDriver")]
    int ksceSysconSetThermalAlertCallback(uint callback, uint argument) => SetEventHandler(23, callback, argument);

    /// <summary>NID_2E4BA4B8(callback, argument): registers the handler of syscon event 25 (0 removes it); 0.</summary>
    [Hle(Nid = 0x2E4BA4B8, Library = "SceSysconForDriver")]
    int NID_2E4BA4B8(uint callback, uint argument) => SetEventHandler(25, callback, argument);

    /// <summary>NID_C442D0BE(callback, argument): registers the handler of syscon event 26 (0 removes it); 0.</summary>
    [Hle(Nid = 0xC442D0BE, Library = "SceSysconForDriver")]
    int NID_C442D0BE(uint callback, uint argument) => SetEventHandler(26, callback, argument);

    /// <summary>NID_61AE3970(callback, argument): registers the handler of syscon event 27 (0 removes it); 0.</summary>
    [Hle(Nid = 0x61AE3970, Library = "SceSysconForDriver")]
    int NID_61AE3970(uint callback, uint argument) => SetEventHandler(27, callback, argument);

    /// <summary>NID_94678881(callback, argument): registers the handler of syscon event 28 (0 removes it); 0.</summary>
    [Hle(Nid = 0x94678881, Library = "SceSysconForDriver")]
    int NID_94678881(uint callback, uint argument) => SetEventHandler(28, callback, argument);

    /// <summary>NID_7AA00C01(callback, argument): registers the handler of syscon event 29 (0 removes it); 0.</summary>
    [Hle(Nid = 0x7AA00C01, Library = "SceSysconForDriver")]
    int NID_7AA00C01(uint callback, uint argument) => SetEventHandler(29, callback, argument);

    /// <summary>NID_423D0C58(callback, argument): registers the handler of syscon event 32 (0 removes it); 0.</summary>
    [Hle(Nid = 0x423D0C58, Library = "SceSysconForDriver")]
    int NID_423D0C58(uint callback, uint argument) => SetEventHandler(32, callback, argument);

    /// <summary>NID_154676F1(callback, argument): registers the handler of syscon event 33 (0 removes it); 0.</summary>
    [Hle(Nid = 0x154676F1, Library = "SceSysconForDriver")]
    int NID_154676F1(uint callback, uint argument) => SetEventHandler(33, callback, argument);

    /// <summary>NID_63352A39(callback, argument): registers the handler of syscon event 34 (0 removes it); 0.</summary>
    [Hle(Nid = 0x63352A39, Library = "SceSysconForDriver")]
    int NID_63352A39(uint callback, uint argument) => SetEventHandler(34, callback, argument);

    /// <summary>NID_14730196(callback, argument): registers the handler of syscon event 35 (0 removes it); 0.</summary>
    [Hle(Nid = 0x14730196, Library = "SceSysconForDriver")]
    int NID_14730196(uint callback, uint argument) => SetEventHandler(35, callback, argument);

    /// <summary>ksceSysconSetAlarmCallback(callback, argument): registers the handler of syscon event 36 (0 removes it); 0.</summary>
    [Hle(Nid = 0x32418370, Library = "SceSysconForDriver")]
    int ksceSysconSetAlarmCallback(uint callback, uint argument) => SetEventHandler(36, callback, argument);

    /// <summary>NID_A26586B2(callback, argument): registers the handler of syscon event 37 (0 removes it); 0.</summary>
    [Hle(Nid = 0xA26586B2, Library = "SceSysconForDriver")]
    int NID_A26586B2(uint callback, uint argument) => SetEventHandler(37, callback, argument);

    /// <summary>NID_67A4CB9F(callback, argument): registers the handler of syscon event 38 (0 removes it); 0.</summary>
    [Hle(Nid = 0x67A4CB9F, Library = "SceSysconForDriver")]
    int NID_67A4CB9F(uint callback, uint argument) => SetEventHandler(38, callback, argument);
}
