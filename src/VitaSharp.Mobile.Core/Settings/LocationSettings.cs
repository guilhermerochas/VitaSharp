using VitaSharp.Core.Host;
using VitaSharp.Core.Localization;

namespace VitaSharp.Mobile.Core.Settings;

/// <summary>
/// "Localizacao" in Ajustes (<see cref="AppPreferences.Location"/>), the desktop launcher's option of the same name with
/// the desktop's values (<see cref="LocationSpec"/>): off (the default) or the phone's location service. A fixed
/// position typed on the desktop ("latitude,longitude") is kept and shown as such.
/// </summary>
public static class LocationSettings
{
    /// <summary>The picker's choices.</summary>
    public static IReadOnlyList<Choice> Choices =>
    [
        new("", Loc.Get("LocationOff")),
        new("system", Loc.Get("LocationSystem")),
    ];

    /// <summary>The row's text for a setting (never the position itself).</summary>
    public static string Label(string? value)
    {
        return LocationSpec.Parse(value).Kind switch
        {
            LocationSpecKind.System => Loc.Get("LocationSystem"),
            LocationSpecKind.Fixed => Loc.Get("LocationFixed"),
            _ => Loc.Get("LocationOff"),
        };
    }
}
