using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The clocks SceRtc keeps (FW 3.74 rtc.skprx, read in the module, study only): nine clocks, each valid or not and
/// running as the system time plus its own offset (the main clock, the secure one, the retained network one, the
/// manufacturing one, the GPS one, the network one and three more), the time zone and daylight saving setting
/// (ksceRtcSetConf), the alarm the syscon chip keeps (its tick and the callback notified when it rings) and the
/// secure alarm (a kernel timer on the secure clock). The clocks start valid (a set-up unit) at the host's UTC time;
/// the time zone starts at the host's. One per system (<see cref="VitaSystem.RtcClocks"/>), shared by the user-mode
/// SceRtc calls.
/// </summary>
[GenerateSnapshot]
public sealed partial class RtcClocks
{
    public const int ClockCount = 9;

    /// <summary>The clocks by their place in the module's table.</summary>
    public const int Main = 0, Secure = 1, RetainedNetwork = 2, Manufacturing = 3, Gps = 4, Network = 5;

    /// <summary>No callback registered.</summary>
    public const int NoCallback = -1;

    /// <summary>Each clock is set (else its read answers 0x80251002).</summary>
    [SnapshotMissing]
    public readonly bool[] Valid = [true, true, true, true, true, true, false, false, false];

    /// <summary>Each clock's offset from the system time (µs, ticks are µs since 0001-01-01).</summary>
    [SnapshotMissing]
    public readonly long[] OffsetUs = new long[ClockCount];

    /// <summary>ksceRtcSetConf: two modes, the time zone and the daylight saving offset (minutes).</summary>
    [SnapshotMissing]
    public uint ConfMode;

    [SnapshotMissing]
    public uint ConfFlag;

    [SnapshotMissing]
    public int TimeZoneMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes;

    [SnapshotMissing]
    public int DaylightMinutes;

    /// <summary>The alarm (0: none), its flag, and the callback notified when it rings.</summary>
    [SnapshotMissing]
    public ulong AlarmTick;

    [SnapshotMissing]
    public uint AlarmFlag;

    [SnapshotMissing(NoCallback)]
    public int AlarmCallback = NoCallback;

    /// <summary>The secure alarm (0: none) and its callback.</summary>
    [SnapshotMissing]
    public ulong SecureAlarmTick;

    [SnapshotMissing(NoCallback)]
    public int SecureAlarmCallback = NoCallback;

    /// <summary>The local time's offset from UTC, minutes (zone and daylight saving).</summary>
    public int LocalOffsetMinutes => TimeZoneMinutes + DaylightMinutes;
}
