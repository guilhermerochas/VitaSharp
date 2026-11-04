using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the device settings and timing; a started camera reopens its host capture on load.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_dev", "HostOverride")]
public sealed partial class Camera
{
    [GenerateSnapshot, SnapshotIgnoreFields("Stream", "HostRgba", "Scaled", "ScaledSeq")]
    sealed partial class Device;

    void SaveExtra(SnapshotWriter w)
    {
        lock (_lock) foreach (var d in _dev) w.Write((ISnapshotState)d);
    }

    void LoadExtra(SnapshotReader r)
    {
        lock (_lock)
            for (int i = 0; i < _dev.Length; i++)
            {
                var d = _dev[i];
                StopDevice(d);
                r.ReadInto(d);
                d.ScaledSeq = -1;
                if (d.Started) d.Stream = OpenHostStream(i, d.Width, d.Height, (int)Math.Ceiling(FrameRate(d.Framerate)));
            }
    }
}
