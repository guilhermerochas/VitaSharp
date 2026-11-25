using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Location callbacks (sceLocationStartLocationCallback). FW 3.74's liblocation.suprx (study only) runs them on a
/// thread of its own, "SceLocationCallback" (sceKernelCreateThread with the library's thread parameters, a 0x1000
/// stack): the start keeps the callback and the userdata in the handle and asks the location service (the shell's
/// liblocation_provider) to report the position every <c>distance</c> metres; for each report the thread reads the
/// position into a SceLocationLocationInfo of its own and calls <c>callback(result, handle, &amp;info, userdata)</c>,
/// until the callback is stopped (or the handle closed). Here the service is the host's location source: the thread
/// looks at it about once a second and reports the first position it has, then each one at least <c>distance</c>
/// metres (great-circle) from the last one reported (any move for 0). A source without a position reports nothing,
/// as the service without a fix.
/// </summary>
public sealed partial class Location : IServiceThreadOwner
{
    /// <summary>The callback thread's name (fixed by the firmware: its identity in a save state).</summary>
    internal const string CallbackThreadName = "SceLocationCallback";

    /// <summary>How often (ms of host time) the thread looks at the source, as the service's periodic reports.</summary>
    private const int CallbackPollMs = 1000;

    /// <summary>A running location callback: its function, userdata and distance, and the last position reported.</summary>
    [GenerateSnapshot]
    sealed partial class LocationCallback
    {
        public uint Callback, Userdata, Distance;
        public bool Reported;
        public double LastLatitude, LastLongitude;
    }

    [SnapshotMissing] // states of builds before it: their callbacks never reported (no function kept)
    readonly Dictionary<uint, LocationCallback> _callbackInfo = new();

    [SnapshotIgnore]
    private GuestThread? _callbackThread;

    /// <summary>Passes the thread made over the callbacks (tests wait on it instead of on the host's clock).</summary>
    [SnapshotIgnore]
    private int _callbackPasses;

    [SnapshotIgnore]
    private readonly List<(uint Handle, uint Callback, uint Userdata, Host.LocationFix Fix)> _due = new();

    internal int LocationCallbackPasses
    {
        get
        {
            lock (_lock)
            {
                return _callbackPasses;
            }
        }
    }

    internal bool LocationCallbackThreadRunning
    {
        get
        {
            lock (_lock)
            {
                return _callbackThread is { IsLive: true };
            }
        }
    }

    /// <summary>Makes the thread look at the source now (a new position from the service; tests).</summary>
    internal void WakeLocationCallbacks()
    {
        lock (_lock)
        {
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Keeps a started callback and makes sure the thread runs (caller holds <see cref="_lock"/>).</summary>
    private void AddLocationCallback(uint handle, uint distance, uint callback, uint userdata)
    {
        _callbackInfo[handle] = new LocationCallback { Callback = callback, Userdata = userdata, Distance = distance };
        if (_callbackThread is not { IsLive: true })
        {
            GuestThread thread = Emu.Kernel.CreateThread(CallbackThreadName, 0, DefaultCallbackPriority, 0x1000, 0, 0);
            thread.HostBody = LocationCallbackThread;
            _callbackThread = thread;
            thread.Start(0, 0);
        }

        Monitor.PulseAll(_lock);
    }

    /// <summary>The priority of the library's threads without sceLocationSetThreadParameter (a user thread's default).</summary>
    private const int DefaultCallbackPriority = 0x10000100;

    /// <summary>Drops the callback of <paramref name="handle"/> (caller holds <see cref="_lock"/>).</summary>
    private void RemoveLocationCallback(uint handle)
    {
        _callbackInfo.Remove(handle);
        Monitor.PulseAll(_lock);
    }

    private void LocationCallbackThread(GuestThread self)
    {
        uint info = Emu.Memory.Alloc(0x30, CallbackThreadName, MemoryRegion.User);
        try
        {
            while (!self.Core.StopRequested)
            {
                CollectDueCallbacks();
                foreach ((uint handle, uint callback, uint userdata, Host.LocationFix fix) in _due)
                {
                    WriteLocation(info, fix);
                    self.CallGuest(callback, 0, handle, info, userdata);
                }

                lock (_lock)
                {
                    _callbackPasses++;
                    Monitor.PulseAll(_lock);
                    if (_callbackInfo.Count == 0 || !ReferenceEquals(_callbackThread, self))
                    {
                        if (ReferenceEquals(_callbackThread, self))
                        {
                            _callbackThread = null;
                        }

                        break;
                    }

                    using (Scheduler.Block()) // waiting for the next report: do not hold an emulated core
                    {
                        Monitor.Wait(_lock, CallbackPollMs);
                    }
                }
            }
        }
        catch (GuestThreadExitException)
        {
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_callbackThread, self))
                {
                    _callbackThread = null;
                }
            }

            Emu.Memory.Free(info);
        }
    }

    /// <summary>The callbacks the source's position is due for now (marked reported).</summary>
    private void CollectDueCallbacks()
    {
        _due.Clear();
        if (CurrentFix is not { } fix || !Allowed)
        {
            return;
        }

        lock (_lock)
        {
            foreach ((uint handle, LocationCallback callback) in _callbackInfo)
            {
                if (callback.Reported)
                {
                    double moved = DistanceMetres(callback.LastLatitude, callback.LastLongitude, fix.Latitude, fix.Longitude);
                    if (callback.Distance == 0 ? moved <= 0 : moved < callback.Distance)
                    {
                        continue;
                    }
                }

                callback.Reported = true;
                callback.LastLatitude = fix.Latitude;
                callback.LastLongitude = fix.Longitude;
                _due.Add((handle, callback.Callback, callback.Userdata, fix));
            }
        }
    }

    /// <summary>The great-circle distance in metres between two positions (haversine, mean Earth radius).</summary>
    internal static double DistanceMetres(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double EarthRadius = 6_371_000.0;
        double toRadians = Math.PI / 180.0;
        double deltaLatitude = (latitude2 - latitude1) * toRadians;
        double deltaLongitude = (longitude2 - longitude1) * toRadians;
        double a = Math.Sin(deltaLatitude / 2) * Math.Sin(deltaLatitude / 2)
                   + Math.Cos(latitude1 * toRadians) * Math.Cos(latitude2 * toRadians) * Math.Sin(deltaLongitude / 2) * Math.Sin(deltaLongitude / 2);
        return 2 * EarthRadius * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// <summary>Save states: the callback thread of a state with running callbacks, while this session runs none, is
    /// the module's again with its saved UID.</summary>
    bool IServiceThreadOwner.AdoptServiceThread(GuestThread thread)
    {
        if (!ReferenceEquals(thread.Process, Emu) || thread.Name != CallbackThreadName)
        {
            return false;
        }

        lock (_lock)
        {
            if (_callbackInfo.Count == 0 || (_callbackThread is { IsLive: true } running && !ReferenceEquals(running, thread)))
            {
                return false;
            }

            thread.HostBody = LocationCallbackThread;
            _callbackThread = thread;
        }

        return true;
    }

    /// <summary>Save states: the callback thread made after the save is let go (it ends at its next pass).</summary>
    bool IServiceThreadOwner.ReleaseServiceThread(GuestThread thread)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(thread, _callbackThread))
            {
                return false;
            }

            _callbackThread = null;
            Monitor.PulseAll(_lock);
        }

        return true;
    }
}
