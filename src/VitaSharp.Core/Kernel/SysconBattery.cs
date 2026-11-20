using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The battery the syscon chip reports (commands 0x800 and 0x900..0x98C, which ScePower polls in turn: status,
/// remaining capacity, levels, full capacity, state of health, cycle count, time left, temperature, current,
/// voltage). The emulated unit has no battery to measure, so it is the default of the rules: a PCH-1000 with its
/// battery in, charged, unplugged and not charging. The values are what such a unit's ScePower shows (full: the
/// remaining capacity is the full one, level 4 of 4) and stay fixed until a test or the host changes them.
/// </summary>
[GenerateSnapshot]
public sealed partial class SysconBattery
{
    /// <summary>A battery is in (status word 0x40).</summary>
    [SnapshotMissing(true)]
    public bool Present = true;

    /// <summary>The battery charges (status word bit 1): only with the AC adapter plugged.</summary>
    [SnapshotMissing]
    public bool Charging;

    /// <summary>mAh left and the pack's full capacity (the PCH-1000's 2200 mAh class pack).</summary>
    [SnapshotMissing(2200)]
    public int RemainCapacity = 2200;

    [SnapshotMissing(2200)]
    public int FullCapacity = 2200;

    /// <summary>The level (0..4) and its maximum.</summary>
    [SnapshotMissing(4)]
    public int Level = 4;

    [SnapshotMissing(4)]
    public int MaxLevel = 4;

    /// <summary>The voltage in mV, a charged 3.7 V Li-ion cell.</summary>
    [SnapshotMissing(4150)]
    public int Voltage = 4150;

    /// <summary>The temperature, in the units ScePower passes on (hundredths of a degree C).</summary>
    [SnapshotMissing(3000)]
    public int Temperature = 3000;

    /// <summary>The current, signed (ScePower's "elec"); 0: no load is measured.</summary>
    [SnapshotMissing]
    public int Current;

    /// <summary>The state of health, %.</summary>
    [SnapshotMissing(100)]
    public int StateOfHealth = 100;

    [SnapshotMissing(10)]
    public int CycleCount = 10;

    /// <summary>The time left on the battery, minutes (the chip reports it only off the AC adapter).</summary>
    [SnapshotMissing(300)]
    public int LifeTimeMinutes = 300;

    /// <summary>The life left, % (ScePower: remaining * 100 / full).</summary>
    public int LifePercent => FullCapacity <= 0 ? 0 : RemainCapacity * 100 / FullCapacity;
}
