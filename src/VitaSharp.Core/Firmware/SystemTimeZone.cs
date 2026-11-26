namespace VitaSharp.Core.Firmware;

/// <summary>
/// The system time zone: /CONFIG/DATE/time_zone holds a time zone id of the firmware's own list (the initial setup's
/// table of (string id, time zone id) pairs, FW 3.60 initialsetup.self; 0 is "UTC-11:00 American Samoa", 12 the
/// template's "UTC-05:00 Eastern Time"), not an offset. A fresh system starts with the host's time zone (as the
/// language follows the host), and SceAppUtil's SCE_SYSTEM_PARAM_ID_TIME_ZONE (minutes from UTC) is the offset of the
/// zone the registry holds.
/// </summary>
public static class SystemTimeZone
{
    /// <summary>(Vita time zone id, standard offset in minutes, IANA zone of the city the firmware names).</summary>
    private static readonly (int Id, int Minutes, string Iana)[] s_zones =
    [
        (0, -660, "Pacific/Pago_Pago"), (1, -660, "Pacific/Midway"), (2, -600, "Pacific/Honolulu"), (3, -540, "America/Anchorage"),
        (4, -480, "America/Los_Angeles"), (5, -480, "America/Tijuana"), (6, -420, "America/Denver"), (110, -420, "America/Phoenix"),
        (7, -420, "America/Chihuahua"), (8, -360, "America/Costa_Rica"), (111, -360, "America/Guatemala"), (9, -360, "America/Chicago"),
        (112, -360, "America/Guadalajara"), (10, -360, "America/Mexico_City"), (113, -360, "America/Monterrey"), (11, -300, "America/Guayaquil"),
        (12, -300, "America/New_York"), (13, -300, "America/Panama"), (14, -300, "America/Bogota"), (15, -300, "America/Lima"),
        (16, -270, "America/Caracas"), (17, -240, "America/Santiago"), (114, -240, "America/Manaus"), (18, -240, "America/Halifax"),
        (115, -240, "America/Blanc-Sablon"), (19, -240, "America/Puerto_Rico"), (20, -240, "America/La_Paz"), (21, -210, "America/St_Johns"),
        (22, -180, "America/Sao_Paulo"), (116, -180, "America/Rio_de_Janeiro"), (23, -180, "America/Buenos_Aires"), (117, -120, "Atlantic/South_Georgia"),
        (118, -120, "Etc/GMT+2"), (24, -60, "Atlantic/Azores"), (25, 0, "Africa/Casablanca"), (26, 0, "Europe/Dublin"),
        (27, 0, "Europe/Lisbon"), (28, 0, "Atlantic/Reykjavik"), (29, 0, "Europe/London"), (30, 60, "Europe/Amsterdam"),
        (31, 60, "Africa/Algiers"), (32, 60, "Europe/Andorra"), (33, 60, "Europe/Vienna"), (34, 60, "Africa/Windhoek"),
        (35, 60, "Europe/Oslo"), (36, 60, "Europe/Copenhagen"), (37, 60, "Europe/Zagreb"), (38, 60, "Europe/Sarajevo"),
        (39, 60, "Europe/San_Marino"), (40, 60, "Europe/Skopje"), (41, 60, "Europe/Stockholm"), (42, 60, "Africa/Tunis"),
        (43, 60, "Europe/Tirane"), (44, 60, "Europe/Paris"), (45, 60, "Europe/Malta"), (46, 60, "Europe/Vaduz"),
        (47, 60, "Europe/Budapest"), (48, 60, "Europe/Bratislava"), (49, 60, "Europe/Prague"), (50, 60, "Europe/Brussels"),
        (51, 60, "Europe/Belgrade"), (52, 60, "Europe/Berlin"), (53, 60, "Europe/Zurich"), (54, 60, "Europe/Madrid"),
        (55, 60, "Europe/Monaco"), (56, 60, "Europe/Ljubljana"), (57, 60, "Europe/Luxembourg"), (58, 60, "Europe/Rome"),
        (59, 60, "Europe/Warsaw"), (60, 120, "Europe/Athens"), (61, 120, "Asia/Amman"), (62, 120, "Europe/Istanbul"),
        (63, 120, "Asia/Jerusalem"), (64, 120, "Africa/Cairo"), (65, 120, "Europe/Kiev"), (66, 120, "Europe/Sofia"),
        (67, 120, "Asia/Damascus"), (68, 120, "Europe/Tallinn"), (69, 120, "Asia/Nicosia"), (70, 120, "Europe/Vilnius"),
        (71, 120, "Europe/Bucharest"), (72, 120, "Asia/Beirut"), (73, 120, "Europe/Helsinki"), (75, 120, "Africa/Johannesburg"),
        (76, 120, "Europe/Riga"), (77, 180, "Asia/Kuwait"), (78, 180, "Asia/Baghdad"), (79, 180, "Asia/Bahrain"),
        (74, 180, "Europe/Minsk"), (81, 180, "Asia/Riyadh"), (80, 180, "Europe/Moscow"), (82, 210, "Asia/Tehran"),
        (83, 240, "Asia/Dubai"), (84, 240, "Asia/Yerevan"), (85, 240, "Asia/Tbilisi"), (86, 240, "Asia/Baku"),
        (87, 240, "Asia/Muscat"), (88, 270, "Asia/Kabul"), (89, 300, "Asia/Karachi"), (90, 300, "Asia/Tashkent"),
        (92, 330, "Asia/Calcutta"), (93, 345, "Asia/Katmandu"), (91, 360, "Asia/Bishkek"), (94, 360, "Asia/Almaty"),
        (95, 360, "Asia/Dhaka"), (96, 420, "Asia/Bangkok"), (97, 480, "Asia/Kuala_Lumpur"), (98, 480, "Asia/Singapore"),
        (99, 480, "Asia/Taipei"), (100, 480, "Australia/Perth"), (101, 480, "Asia/Shanghai"), (102, 480, "Asia/Hong_Kong"),
        (103, 540, "Asia/Seoul"), (104, 540, "Asia/Tokyo"), (105, 570, "Australia/Adelaide"), (119, 570, "Australia/Darwin"),
        (106, 600, "Australia/Sydney"), (120, 600, "Australia/Brisbane"), (121, 660, "Pacific/Noumea"), (107, 720, "Pacific/Auckland"),
        (108, 720, "Pacific/Fiji"), (109, 780, "Pacific/Apia"),
    ];

    /// <summary>The template's default (UTC-05:00 Eastern Time).</summary>
    public const int Default = 12;

    /// <summary>
    /// The host's own clock where the .NET runtime's time zone data may not be the system's: the platform's zone id and
    /// its offset from UTC at a given time (minutes, summer time included), as the platform itself shows the time
    /// (Android: java.util.TimeZone, set by the app at start). Null: .NET's TimeZoneInfo.Local. The runtime on a phone
    /// may carry its own copy of the zone rules, older than the phone's: with the rules of before 2019 São Paulo is in
    /// summer time from November, and the Vita clock showed an hour more than the phone.
    /// </summary>
    public static (string Id, Func<DateTime, int> UtcOffsetMinutes)? Platform { get; set; }

    /// <summary>Vita time zone id of the host's time zone.</summary>
    public static int Host
    {
        get
        {
            if (Platform is not { } platform)
            {
                return s_hostFromRuntime.Value;
            }

            DateTime now = DateTime.UtcNow;
            int standard = platform.UtcOffsetMinutes(now) - SummerMinutes(platform.UtcOffsetMinutes, now);
            return FromZone(platform.Id, standard);
        }
    }

    private static readonly Lazy<int> s_hostFromRuntime = new(() => FromTimeZone(TimeZoneInfo.Local));

    /// <summary>Whether the host is in summer time now (/CONFIG/DATE/summer_time of a fresh system, SceAppUtil's
    /// SCE_SYSTEM_PARAM_ID_DAYLIGHT_SAVINGS): its offset now past its standard offset (<see cref="SummerTime"/>), from
    /// the platform's clock when it gave one.</summary>
    public static int HostSummerTime
    {
        get
        {
            DateTime now = DateTime.UtcNow;
            if (Platform is { } platform)
            {
                return SummerMinutes(platform.UtcOffsetMinutes, now) > 0 ? 1 : 0;
            }

            TimeZoneInfo local = TimeZoneInfo.Local;
            return SummerTime((int)local.GetUtcOffset(now).TotalMinutes, (int)local.BaseUtcOffset.TotalMinutes);
        }
    }

    /// <summary>1 when a zone's offset now is past its standard offset (summer time moves the clock forward), else 0.</summary>
    public static int SummerTime(int currentOffsetMinutes, int standardOffsetMinutes) => currentOffsetMinutes > standardOffsetMinutes ? 1 : 0;

    /// <summary>The summer time part of a platform zone's offset at <paramref name="utc"/>: the offset then minus the
    /// smaller of the offsets half a year before and after (the standard one, for a zone with summer time).</summary>
    private static int SummerMinutes(Func<DateTime, int> offsetAt, DateTime utc)
    {
        int now = offsetAt(utc);
        int standard = Math.Min(now, Math.Min(offsetAt(utc.AddMonths(-6)), offsetAt(utc.AddMonths(6))));
        return now - standard;
    }

    /// <summary>The zone of the same city, else the zone CLDR names for the host zone's Windows zone (a city the tz
    /// database renamed, as Asia/Kolkata for the Asia/Calcutta of the table, or another city of that zone), else the
    /// first one with the same standard offset, else the one with the nearest offset. Only the zone's id (an IANA or a
    /// Windows id) and its standard offset are used, never the host's time zone database: a Linux without the tz
    /// database's old names (no America/Buenos_Aires file) maps its zones the same way.</summary>
    public static int FromTimeZone(TimeZoneInfo timeZoneInfo) => FromZone(timeZoneInfo.Id, (int)timeZoneInfo.BaseUtcOffset.TotalMinutes);

    /// <summary><see cref="FromTimeZone"/> by the zone's id and standard offset in minutes.</summary>
    public static int FromZone(string id, int standardMinutes)
    {
        if (City(id) is int same)
        {
            return same;
        }

        // a Windows id (Windows hosts): the IANA zone CLDR gives it
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out string? fromWindows) && City(fromWindows) is int windowsCity)
        {
            return windowsCity;
        }

        // an IANA id the table does not name (Linux and macOS hosts use the tz database's current names): the zone CLDR
        // gives its Windows zone
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out string? windowsId)
            && TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out string? cldrZone)
            && City(cldrZone) is int zoneCity)
        {
            return zoneCity;
        }

        return s_zones.MinBy(z => Math.Abs(z.Minutes - standardMinutes)).Id;
    }

    /// <summary>The Vita time zone of the city an IANA id names (the table's names are CLDR's), null when none.</summary>
    private static int? City(string iana)
    {
        foreach ((int Id, int Minutes, string Iana) z in s_zones)
        {
            if (string.Equals(z.Iana, iana, StringComparison.OrdinalIgnoreCase))
            {
                return z.Id;
            }
        }

        return null;
    }

    /// <summary>Standard offset in minutes of a Vita time zone id; null for an unknown id.</summary>
    public static int? OffsetMinutes(int id)
    {
        foreach ((int Id, int Minutes, string Iana) z in s_zones)
        {
            if (z.Id == id)
            {
                return z.Minutes;
            }
        }

        return null;
    }
}
