using System.Globalization;

namespace VitaSharp.Core.Host;

/// <summary>A position the host's location source gives SceLibLocation: degrees (WGS 84), metres and metres per second.
/// <see cref="float.NaN"/> for what the source does not know (the guest then gets the SDK's "invalid" value).</summary>
public readonly record struct LocationFix(double Latitude, double Longitude, double Altitude, float Accuracy, float Direction, float Speed);

/// <summary>
/// Where the emulated unit's position comes from (SceLibLocation): a fixed test position, or the host system's location
/// service (Android's LocationManager). Off by default: the position is where the player is.
/// </summary>
public interface ILocationSource : IDisposable
{
    /// <summary>The latest position, or null while the source has none (no fix yet, permission refused).</summary>
    LocationFix? Current { get; }
}

/// <summary>A fixed position (scripted tests, and a player who wants one place everywhere).</summary>
public sealed class FixedLocationSource(LocationFix fix) : ILocationSource
{
    public LocationFix? Current => fix;

    public void Dispose()
    {
    }
}

/// <summary>What <see cref="LocationSpec"/> names.</summary>
public enum LocationSpecKind
{
    /// <summary>No location: the application is not allowed to use it, as today (the default).</summary>
    None,

    /// <summary>A fixed position given in the setting.</summary>
    Fixed,

    /// <summary>The host system's location service.</summary>
    System,
}

/// <summary>
/// The "Localização" setting and --location= value: "" or "none" = off (the default), "system" = the host system's
/// location service, "latitude,longitude[,altitude[,accuracy]]" (also after "fixed:") = that position (decimal degrees
/// with a point, metres; accuracy 10 m when not given).
/// </summary>
public sealed record LocationSpec(LocationSpecKind Kind, LocationFix Fix)
{
    /// <summary>The accuracy of a fixed position that gives none, in metres.</summary>
    public const float DefaultAccuracy = 10f;

    public static readonly LocationSpec None = new(LocationSpecKind.None, default);

    /// <summary>Parses a setting; a value that is not one of the forms is off (logged, without the value: it may be a
    /// place).</summary>
    public static LocationSpec Parse(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        if (text.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return new LocationSpec(LocationSpecKind.System, default);
        }

        if (text.StartsWith("fixed:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["fixed:".Length..];
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 4 || !TryNumber(parts[0], out double latitude) || !TryNumber(parts[1], out double longitude)
            || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            Util.Log.Warn("Location: the setting is not \"system\" nor \"latitude,longitude[,altitude[,accuracy]]\"; location stays off");
            return None;
        }

        double altitude = double.NaN;
        float accuracy = DefaultAccuracy;
        if (parts.Length > 2 && !TryNumber(parts[2], out altitude))
        {
            return None;
        }

        if (parts.Length > 3)
        {
            if (!TryNumber(parts[3], out double givenAccuracy) || givenAccuracy <= 0)
            {
                return None;
            }

            accuracy = (float)givenAccuracy;
        }

        return new LocationSpec(LocationSpecKind.Fixed, new LocationFix(latitude, longitude, altitude, accuracy, float.NaN, float.NaN));
    }

    private static bool TryNumber(string text, out double number) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    /// <summary>The source the setting names: <paramref name="system"/> makes the host system's (null where the host has
    /// none: location off).</summary>
    public ILocationSource? CreateSource(Func<ILocationSource?> system)
    {
        return Kind switch
        {
            LocationSpecKind.Fixed => new FixedLocationSource(Fix),
            LocationSpecKind.System => system(),
            _ => null,
        };
    }
}
