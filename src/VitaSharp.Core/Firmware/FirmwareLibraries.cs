using System.Collections.Concurrent;
using System.Text;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// Which user-mode firmware module exports which library: every .suprx in the system module folders of vs0
/// (sys/external, vsh/common, vsh/shell) read with <see cref="ModuleImage"/>. The kernel's module manager uses this
/// to load the modules a system program depends on (libpaf, libshellsvc, ...) before it starts, the way the Vita's
/// modulemgr resolves a system executable's libraries against the firmware.
/// The index is cached in &lt;FsRoot&gt;/firmware-libraries.txt (one line per module: path, size, mtime, libraries)
/// and rebuilt for files that changed.
/// </summary>
public sealed class FirmwareLibraries
{
    /// <summary>vs0 folders holding the user-mode modules of the system software, searched in this order.</summary>
    public static readonly string[] ModuleFolders = ["sys/external", "vsh/common", "vsh/shell"];

    public sealed record Module(string VitaPath, string Name, IReadOnlyList<(string Library, uint LibNid)> Libraries);

    readonly Dictionary<string, Module> _byLibrary = new(StringComparer.Ordinal);
    public IReadOnlyList<Module> Modules { get; }

    static readonly ConcurrentDictionary<string, FirmwareLibraries> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The index of the firmware installed in <paramref name="fsRoot"/> (empty when there is none).</summary>
    public static FirmwareLibraries For(string fsRoot) => Cache.GetOrAdd(Path.GetFullPath(fsRoot), r => new FirmwareLibraries(r));

    /// <summary>The module exporting <paramref name="library"/> (first one in <see cref="ModuleFolders"/> order).</summary>
    public Module? Provider(string library) => _byLibrary.GetValueOrDefault(library);

    FirmwareLibraries(string fsRoot)
    {
        string cacheFile = Path.Combine(fsRoot, "firmware-libraries.txt");
        var cached = ReadCache(cacheFile);
        var modules = new List<Module>();
        var lines = new List<string>();
        bool changed = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var folder in ModuleFolders)
        {
            string dir = Path.Combine(fsRoot, "vs0", folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.suprx").Order(StringComparer.OrdinalIgnoreCase))
            {
                string vita = $"vs0:{folder}/{Path.GetFileName(file)}";
                var fi = new FileInfo(file);
                string stamp = $"{fi.Length}\t{fi.LastWriteTimeUtc.Ticks}";
                Module? m = null;
                if (cached.TryGetValue(vita, out var c) && c.Stamp == stamp) m = c.Module;
                else
                {
                    changed = true;
                    try
                    {
                        var img = ModuleImage.Read(SelfDecryptor.ToElf(File.ReadAllBytes(file), null));
                        m = new Module(vita, img.Name, img.Exports.Where(x => x.Library.Length > 0).Select(x => (x.Library, x.LibNid)).ToArray());
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        Log.Debug($"firmware libraries: {vita}: {e.Message}");
                        m = new Module(vita, "", []);
                    }
                }
                modules.Add(m);
                lines.Add($"{vita}\t{stamp}\t{m.Name}\t{string.Join(',', m.Libraries.Select(l => $"{l.Library}:{l.LibNid:X8}"))}");
                foreach (var (lib, _) in m.Libraries) _byLibrary.TryAdd(lib, m);
            }
        }
        Modules = modules;
        if (changed || cached.Count != modules.Count)
        {
            Log.Info($"firmware libraries: indexed {modules.Count} modules, {_byLibrary.Count} libraries in {sw.ElapsedMilliseconds} ms");
            try { File.WriteAllLines(cacheFile, lines, new UTF8Encoding(false)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Debug($"firmware libraries: cannot write {cacheFile}: {e.Message}"); }
        }
    }

    static Dictionary<string, (string Stamp, Module Module)> ReadCache(string file)
    {
        var d = new Dictionary<string, (string, Module)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file)) return d;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var f = line.Split('\t');
                if (f.Length != 5) continue;
                var libs = f[4].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x =>
                {
                    int c = x.LastIndexOf(':');
                    return (x[..c], Convert.ToUInt32(x[(c + 1)..], 16));
                }).ToArray();
                d[f[0]] = ($"{f[1]}\t{f[2]}", new Module(f[0], f[3], libs));
            }
        }
        catch (Exception e) when (e is IOException or FormatException or ArgumentException) { d.Clear(); }
        return d;
    }
}
