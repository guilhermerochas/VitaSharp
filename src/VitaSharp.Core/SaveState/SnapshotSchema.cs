namespace VitaSharp.Core.SaveState;

/// <summary>
/// Which save states a build can load. The generated part hashes every [GenerateSnapshot] layout, including the code
/// of the types' SaveExtra/LoadExtra hooks; the other hand-written layouts (kernel objects, memory, the container) are
/// covered by <see cref="ManualVersion"/>, which must be raised whenever one of them changes what it writes.
/// </summary>
public static partial class SnapshotSchema
{
    /// <summary>Version of the hand-written state layouts.</summary>
    public const int ManualVersion = 3;

    public static string Hash => $"{ManualVersion}-{GeneratedHash}";

    /// <summary>True for an HLE module type with instance state and no <see cref="ISnapshotState"/>: a save is
    /// refused once the game has used it.</summary>
    public static bool IsStateful(Type moduleType) => StatefulModules.Contains(moduleType);

    /// <summary>The stateful module types without save state support (diagnostics, tests).</summary>
    public static IReadOnlyCollection<Type> UnsupportedModules => StatefulModules;
}
