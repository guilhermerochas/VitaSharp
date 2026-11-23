using System.Collections.Concurrent;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// The user modules of the installed firmware with their export tables: the os0:us modules every process has
/// preloaded (libkernel, driver_us, libgxm_es4, libgpu_es4, avcodec_us) and the vs0 modules a process loads
/// (<see cref="FirmwareLibraries"/>: libc, libfios2, libsysmodule's modules...). Each is read with
/// <see cref="ModuleImage"/> when first asked for: its name, module NID, segments and export tables, which is what the
/// module manager shows of the module in a process's module list. Read once per firmware folder.
/// </summary>
public sealed class FirmwareUserImage
{
    /// <summary>One user module of the firmware: where it is and its tables.</summary>
    public sealed record Module(string VitaPath, ModuleImage Image);

    private static readonly ConcurrentDictionary<string, FirmwareUserImage> s_cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _fsRoot;
    private readonly object _lock = new();
    private readonly Dictionary<string, Module?> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private List<Module>? _preloaded;

    private FirmwareUserImage(string fsRoot)
    {
        _fsRoot = fsRoot;
    }

    /// <summary>The user modules of the firmware installed under <paramref name="fsRoot"/> (none without one).</summary>
    public static FirmwareUserImage For(string fsRoot) => s_cache.GetOrAdd(Path.GetFullPath(fsRoot), root => new FirmwareUserImage(root));

    /// <summary>Forgets what was read under <paramref name="fsRoot"/> (tests that install a firmware of their own).</summary>
    internal static void Forget(string fsRoot) => s_cache.TryRemove(Path.GetFullPath(fsRoot), out _);

    /// <summary>
    /// The firmware user module that exports library <paramref name="libNid"/> named <paramref name="library"/>: an
    /// os0:us module first (they are in every process), else the vs0 module the firmware's library index names for it,
    /// when its export tables really have the library. Null when no user module of the firmware exports it (a kernel
    /// module's syscall library, or no firmware installed).
    /// </summary>
    public Module? Exporting(string library, uint libNid)
    {
        lock (_lock)
        {
            foreach (Module module in Preloaded())
            {
                if (Exports(module.Image, libNid))
                {
                    return module;
                }
            }

            if (FirmwareLibraries.For(_fsRoot).Provider(library) is { } provider && Read(provider.VitaPath, Vs0Host(provider.VitaPath)) is { } read
                && Exports(read.Image, libNid))
            {
                return read;
            }

            return null;
        }
    }

    private static bool Exports(ModuleImage image, uint libNid)
    {
        foreach ((string _, uint exportLibNid, ushort _, uint[] funcNids, uint[] _) in image.Exports)
        {
            if (exportLibNid == libNid && funcNids.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private List<Module> Preloaded()
    {
        if (_preloaded != null)
        {
            return _preloaded;
        }

        var modules = new List<Module>();
        string folder = Io.FirmwareLayout.UserModules.Host(_fsRoot);
        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.suprx").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Read(Io.FirmwareLayout.UserModules.Guest(Path.GetFileName(file)), file) is { } module)
                {
                    modules.Add(module);
                }
            }
        }

        _preloaded = modules;
        return modules;
    }

    private string Vs0Host(string vitaPath) => Path.Combine(_fsRoot, "vs0", vitaPath["vs0:".Length..].Replace('/', Path.DirectorySeparatorChar));

    private Module? Read(string vitaPath, string host)
    {
        if (_byPath.TryGetValue(vitaPath, out Module? known))
        {
            return known;
        }

        Module? module = null;
        try
        {
            var image = ModuleImage.Read(SelfDecryptor.ToElf(File.ReadAllBytes(host), null));
            if (image.Name.Length > 0)
            {
                module = new Module(vitaPath, image);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
            or IndexOutOfRangeException or KeyNotFoundException or NotSupportedException or System.Security.Cryptography.CryptographicException)
        {
            Log.Debug($"Firmware user module {vitaPath} not readable ({e.Message})");
        }

        _byPath[vitaPath] = module;
        return module;
    }
}
