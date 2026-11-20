using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// What ScePower (FW 3.74 power.skprx in the boot image, read in the module, study only) keeps for the whole
/// system, shared by its kernel exports (ScePowerForDriver) and the user-mode ScePower calls (syscalls into the same
/// module): the clocks it set, the power switch mode, the button push times, the display brightness limits, the
/// charging permissions, the battery fake status, the idle timers and its flags. The battery values themselves come
/// from the syscon chip (<see cref="SysconChip.Battery"/>). One per system (<see cref="VitaSystem.PowerState"/>).
/// </summary>
[GenerateSnapshot]
public sealed partial class ScePowerState
{
    /// <summary>The idle timers ScePower runs (8 slots).</summary>
    public const int IdleTimerCount = 8;

    /// <summary>The clocks in MHz: the bus, the GPU crossbar, the GPU core pair (ScePowerForDriver's "internal" two
    /// values; the module starts the first at 0x6F), the DMAC5, the camera bus, the Venezia (codec engine), the VIP
    /// and the compat (PSP emulation) clocks. The bus, crossbar and GPU start at the application defaults; the others
    /// at 0 until a driver sets them (the module's memset; their boot values are not known).</summary>
    [SnapshotMissing(222)]
    public int BusClock = 222;

    [SnapshotMissing(166)]
    public int GpuXbarClock = 166;

    [SnapshotMissing(222)]
    public int GpuClock = 222;

    [SnapshotMissing(0x6F)]
    public int GpuClockInternal = 0x6F;

    [SnapshotMissing]
    public int GpuClockInternal2;

    [SnapshotMissing]
    public int Dmac5Clock;

    [SnapshotMissing]
    public int CameraBusClock;

    [SnapshotMissing]
    public int VeneziaClock;

    [SnapshotMissing]
    public int VipClock;

    [SnapshotMissing]
    public int CompatClock;

    /// <summary>The clocks a process asked for (the ...Proc exports: ARM, bus, crossbar, GPU), by process id; the
    /// system runs at the highest one.</summary>
    [SnapshotMissing]
    public readonly Dictionary<int, int> ArmClockByProcess = new();

    [SnapshotMissing]
    public readonly Dictionary<int, int> BusClockByProcess = new();

    [SnapshotMissing]
    public readonly Dictionary<int, int> GpuXbarClockByProcess = new();

    [SnapshotMissing]
    public readonly Dictionary<int, int> GpuClockByProcess = new();

    /// <summary>The power switch modes (kscePowerSetPowerSwMode and its second form; the reported mode is both
    /// or'ed): the module starts the first at 2 (an ordinary system boot).</summary>
    [SnapshotMissing(2u)]
    public uint PowerSwMode = 2;

    [SnapshotMissing]
    public uint PowerSwMode2;

    /// <summary>The PS button push time (µs, the module's 1 s).</summary>
    [SnapshotMissing(1_000_000u)]
    public uint PsButtonPushTimeUs = 1_000_000;

    /// <summary>The flag word the module starts at 2 (NID_F535D928 sets and clears its bits).</summary>
    [SnapshotMissing(2u)]
    public uint Flags = 2;

    /// <summary>The display brightness (16.16) and the two maxima (on the battery, on the AC adapter).</summary>
    [SnapshotMissing(0x10000u)]
    public uint DisplayBrightness = 0x10000;

    [SnapshotMissing(0x10000u)]
    public uint DisplayMaxBrightness = 0x10000;

    [SnapshotMissing(0x10000u)]
    public uint DisplayMaxBrightnessAc = 0x10000;

    /// <summary>The wireless (WLAN) is active (kscePowerWlanActivate).</summary>
    [SnapshotMissing]
    public bool WlanActive;

    /// <summary>The callback state (kscePowerSetCallbackState).</summary>
    [SnapshotMissing]
    public uint CallbackState;

    /// <summary>A resume was requested (kscePowerRequestResume).</summary>
    [SnapshotMissing]
    public bool ResumeRequested;

    /// <summary>How many times the system resumed, and what woke it last.</summary>
    [SnapshotMissing]
    public uint ResumeCount;

    [SnapshotMissing]
    public uint WakeupFactor;

    /// <summary>Charging forbidden: a count (kscePowerBatteryForbidCharging / PermitCharging).</summary>
    [SnapshotMissing]
    public int ChargingForbidden;

    /// <summary>USB charging enabled (kscePowerBatteryEnableUsbCharging) and the USB status the driver reported.</summary>
    [SnapshotMissing]
    public bool UsbCharging;

    [SnapshotMissing]
    public uint UsbStatus;

    /// <summary>The battery fake status (kscePowerSetBatteryFakeStatus, the system's battery test): when on, the
    /// fake remaining/full capacity and the fake time left replace the chip's values.</summary>
    [SnapshotMissing]
    public bool FakeBattery;

    [SnapshotMissing]
    public uint FakeMode;

    [SnapshotMissing]
    public uint FakeRemainCapacity;

    [SnapshotMissing]
    public uint FakeFullCapacity;

    [SnapshotMissing]
    public uint FakeLifeTimeUs;

    /// <summary>The guest time (µs) the fake status was set: its time left counts down from there.</summary>
    [SnapshotMissing]
    public long FakeSetUs;

    /// <summary>The idle timers: start time (µs), timeout (µs), callback, argument, flags, the slots that share its
    /// time (mask), and whether it fired.</summary>
    [SnapshotMissing]
    public readonly long[] IdleStartUs = new long[IdleTimerCount];

    [SnapshotMissing]
    public readonly long[] IdleTimeoutUs = new long[IdleTimerCount];

    [SnapshotMissing]
    public readonly uint[] IdleCallback = new uint[IdleTimerCount];

    [SnapshotMissing]
    public readonly uint[] IdleArgument = new uint[IdleTimerCount];

    [SnapshotMissing]
    public readonly uint[] IdleFlags = new uint[IdleTimerCount];

    [SnapshotMissing]
    public readonly uint[] IdleShareMask = new uint[IdleTimerCount];

    /// <summary>The process idle callback (kscePowerSetProcessIdleCallback) and its argument.</summary>
    [SnapshotMissing]
    public uint ProcessIdleCallback;

    [SnapshotMissing]
    public uint ProcessIdleArgument;

    /// <summary>The battery temperature callback (NID_C62B6164: callback, argument, threshold).</summary>
    [SnapshotMissing]
    public uint TemperatureCallback;

    [SnapshotMissing]
    public uint TemperatureArgument;

    [SnapshotMissing]
    public uint TemperatureThreshold;

    /// <summary>The words other setters keep (NID_FE35B73D's pair, NID_711C68DF's four, NID_B24263F0's two tables of
    /// four, NID_38415146's value, the shutdown request's argument).</summary>
    [SnapshotMissing]
    public readonly uint[] Words = new uint[16];

    /// <summary>The power flags a process set (NID_4A69163A bit 0, NID_661D670D bit 1), by process id.</summary>
    [SnapshotMissing]
    public readonly Dictionary<int, uint> ProcessFlags = new();

    /// <summary>The idle time (�s) a process set (NID_FB5C3C75), by process id.</summary>
    [SnapshotMissing]
    public readonly Dictionary<int, long> ProcessIdleTimeUs = new();

    [SnapshotMissing]
    public readonly uint[] DeviceTableA = new uint[4];

    [SnapshotMissing]
    public readonly uint[] DeviceTableB = new uint[4];
}
