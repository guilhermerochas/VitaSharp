using System.Reflection;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// "Can this native library be loaded?" without ever unloading it. A library that registers a process-exit handler
/// (C++ static objects, atexit) while it is loaded leaves that handler behind when it is unloaded (dlclose), and on
/// Linux exit() then jumps into whatever is mapped at that address later (shaderc did, a9b03f1b: the renderer keeps it
/// loaded since, but the availability probes added later loaded and freed it again). So a probed library stays
/// loaded until the process ends, which costs nothing (the code that uses it loads it again anyway, and gets the same
/// handle).
/// </summary>
public static class NativeProbe
{
    /// <summary>Loads <paramref name="libraryName"/> (a name or a path, as <see cref="NativeLibrary.TryLoad(string, out nint)"/>)
    /// and keeps it loaded; false when it cannot be loaded.</summary>
    public static bool CanLoad(string libraryName) => NativeLibrary.TryLoad(libraryName, out _);

    /// <summary>The same, resolved the way <paramref name="assembly"/>'s [DllImport] resolves it (deps.json
    /// runtimes/&lt;rid&gt;/native first).</summary>
    public static bool CanLoad(string libraryName, Assembly assembly) => NativeLibrary.TryLoad(libraryName, assembly, null, out _);

    /// <summary>Loads <paramref name="path"/> and keeps it loaded; throws as <see cref="NativeLibrary.Load(string)"/>
    /// when it cannot be loaded (the message says why).</summary>
    public static void Load(string path) => NativeLibrary.Load(path);
}
