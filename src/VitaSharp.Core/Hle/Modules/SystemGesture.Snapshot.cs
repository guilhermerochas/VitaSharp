using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: touch primitives and gesture recognizers with their events and tracking (scratch lists
/// excluded).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_live", "_mine")]
public sealed partial class SystemGesture
{
    [GenerateSnapshot]
    sealed partial class Primitive;

    [GenerateSnapshot]
    sealed partial class Recognizer;
}
