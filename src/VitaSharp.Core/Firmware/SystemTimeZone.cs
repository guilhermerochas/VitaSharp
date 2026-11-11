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

    /// <summary>Vita time zone id of the host's time zone, read once.</summary>
    public static int Host { get; } = FromTimeZone(TimeZoneInfo.Local);

    /// <summary>The zone of the same city (IANA id, also from a Windows id), else the first one with the same
    /// standard offset, else the one with the nearest offset.</summary>
    public static int FromTimeZone(TimeZoneInfo tz)
    {
        string iana = tz.HasIanaId ? tz.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out string? i) ? i : "";
        foreach ((int Id, int Minutes, string Iana) z in s_zones)
        {
            if (string.Equals(z.Iana, iana, StringComparison.OrdinalIgnoreCase))
            {
                return z.Id;
            }
        }

        int minutes = (int)tz.BaseUtcOffset.TotalMinutes;
        return s_zones.MinBy(z => Math.Abs(z.Minutes - minutes)).Id;
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
