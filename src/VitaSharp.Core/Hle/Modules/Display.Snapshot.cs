using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot, SnapshotIgnoreFields("_vblankNotifier")]
public sealed partial class Display
{
    /// <summary>Restored vblank callbacks need the notifier thread, which this session may not have started yet.</summary>
    void AfterSnapshotLoad()
    {
        lock (_vblankCallbacks)
        {
            if (_vblankCallbacks.IsEmpty || _vblankNotifier != null) return;
            _vblankNotifier = new Thread(VblankNotifierLoop) { IsBackground = true, Name = "VBlank notifier" };
            _vblankNotifier.Start();
        }
    }
}

[GenerateSnapshot]
public sealed partial class Ctrl
{
    [GenerateSnapshot]
    sealed partial class RapidFireRule;
}

[GenerateSnapshot]
public sealed partial class Touch
{
}
