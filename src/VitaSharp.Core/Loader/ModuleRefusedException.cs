namespace VitaSharp.Core.Loader;

/// <summary>The module manager refuses a module it could read (FW 3.74 checks of its tables): <see cref="Error"/> is
/// what the load call returns.</summary>
public sealed class ModuleRefusedException(int error, string message) : Exception(message)
{
    public int Error { get; } = error;
}
