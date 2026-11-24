using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;
using AndroidLocationFix = Android.Locations.Location;
using AndroidLocationManager = Android.Locations.LocationManager;

namespace VitaSharp.Android.Game;

/// <summary>
/// The phone's location service as the Vita's position (SceLibLocation, the "Localizacao" setting = "Localizacao do
/// celular"). The location permission (ACCESS_FINE_LOCATION, or COARSE when only that is granted) is asked the first time
/// a game asks for its position, never before; denied, the position stays unknown (the Vita's service answers
/// "undetermined"). Updates come from the network and GPS providers while the game runs; the latest fix is what the
/// guest reads. Nothing is logged or saved of the position.
/// </summary>
public sealed class AndroidLocation : Java.Lang.Object, ILocationSource, global::Android.Locations.ILocationListener
{
    /// <summary>The requestCode of the location permission request (GameActivity.OnRequestPermissionsResult).</summary>
    public const int PermissionRequestCode = 0x10CA;

    /// <summary>Time between updates the providers are asked for.</summary>
    const long UpdateMilliseconds = 2000;

    readonly Activity _activity;
    readonly object _lock = new();
    LocationFix? _fix;
    bool _asked, _listening, _denied, _disposed;

    public AndroidLocation(Activity activity)
    {
        _activity = activity;
    }

    public LocationFix? Current
    {
        get
        {
            Start();
            lock (_lock)
            {
                return _fix;
            }
        }
    }

    /// <summary>Asks for the permission on the first read, then listens to the providers.</summary>
    void Start()
    {
        lock (_lock)
        {
            if (_listening || _denied || _disposed)
            {
                return;
            }
        }

        if (Granted())
        {
            Listen();
            return;
        }

        lock (_lock)
        {
            if (_asked)
            {
                return;
            }

            _asked = true;
        }

        Log.Info("Location: asking for the location permission");
        _activity.RunOnUiThread(() =>
        {
            try
            {
                _activity.RequestPermissions([Manifest.Permission.AccessFineLocation, Manifest.Permission.AccessCoarseLocation], PermissionRequestCode);
            }
            catch (Exception e)
            {
                Log.Warn($"Location: permission request failed: {e.Message}");
                OnPermissionResult(false);
            }
        });
    }

    bool Granted() =>
        _activity.CheckSelfPermission(Manifest.Permission.AccessFineLocation) == Permission.Granted
        || _activity.CheckSelfPermission(Manifest.Permission.AccessCoarseLocation) == Permission.Granted;

    /// <summary>GameActivity.OnRequestPermissionsResult; false when the request was not this one.</summary>
    public bool OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (requestCode != PermissionRequestCode)
        {
            return false;
        }

        OnPermissionResult(grantResults.Any(r => r == Permission.Granted));
        return true;
    }

    void OnPermissionResult(bool granted)
    {
        Log.Info(granted ? "Location: permission granted" : "Location: permission denied, the Vita's position stays unknown");
        if (granted)
        {
            Listen();
            return;
        }

        lock (_lock)
        {
            _denied = true;
        }
    }

    /// <summary>Takes the last known fix and asks every enabled provider for updates (on the main looper).</summary>
    void Listen()
    {
        _activity.RunOnUiThread(() =>
        {
            lock (_lock)
            {
                if (_listening || _disposed)
                {
                    return;
                }

                _listening = true;
            }

            try
            {
                if (_activity.GetSystemService(Context.LocationService) is not AndroidLocationManager manager)
                {
                    Log.Warn("Location: the phone has no location service");
                    return;
                }

                foreach (string provider in manager.GetProviders(true))
                {
                    if (manager.GetLastKnownLocation(provider) is { } last)
                    {
                        Take(last);
                    }

                    manager.RequestLocationUpdates(provider, UpdateMilliseconds, 0f, this, Looper.MainLooper);
                }
            }
            catch (Java.Lang.SecurityException e)
            {
                Log.Warn($"Location: {e.Message}");
            }
        });
    }

    /// <summary>Keeps <paramref name="location"/> as the position the guest reads (the latest of any provider).</summary>
    void Take(AndroidLocationFix location)
    {
        var fix = new LocationFix(
            location.Latitude,
            location.Longitude,
            location.HasAltitude ? location.Altitude : double.NaN,
            location.HasAccuracy ? location.Accuracy : float.NaN,
            location.HasBearing ? location.Bearing : float.NaN,
            location.HasSpeed ? location.Speed : float.NaN);
        lock (_lock)
        {
            _fix = fix;
        }
    }

    public void OnLocationChanged(AndroidLocationFix location)
    {
        Take(location);
    }

    public void OnProviderDisabled(string provider)
    {
    }

    public void OnProviderEnabled(string provider)
    {
    }

    public void OnStatusChanged(string? provider, global::Android.Locations.Availability status, Bundle? extras)
    {
    }

    protected override void Dispose(bool disposing)
    {
        bool listening;
        lock (_lock)
        {
            listening = _listening;
            _listening = false;
            _disposed = true;
        }

        if (listening && disposing)
        {
            _activity.RunOnUiThread(() =>
            {
                try
                {
                    (_activity.GetSystemService(Context.LocationService) as AndroidLocationManager)?.RemoveUpdates(this);
                }
                catch (Exception e)
                {
                    Log.Warn($"Location: {e.Message}");
                }
            });
        }

        base.Dispose(disposing);
    }
}
