namespace VitaSharp.Core;

/// <summary>A launched application's executable could not be loaded (<see cref="Emulator.SpawnProcess"/>):
/// <see cref="Error"/> is the error the console's launch call returns (the module manager's).</summary>
public sealed class ProcessLoadException(int error, Exception cause) : Exception(cause.Message, cause)
{
    public int Error { get; } = error;
}
