using VitaSharp.Core.Loader;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// The FW 3.74 import entry check (<see cref="ModuleLoader.ImportEntryProblem"/>) over every installed user module,
/// read only: each title's eboot.bin and modules (ux0:app, its update in ux0:patch, the game card's) and the user
/// plugins (ux0:tai, ur0:tai), decrypted in memory with the title's licence and laid out with
/// <see cref="ModuleImage"/>, never loaded or run. A library counts as found as the loader counts it: a module of the
/// same title exports it, or it is a firmware library (the NID database). Nothing is written.
/// </summary>
public sealed class ImportEntryCensus
{
    public int Modules { get; private set; }

    public int Passed { get; private set; }

    /// <summary>Modules that did not decrypt or parse (not checked).</summary>
    public List<(string Module, string Error)> Unreadable { get; } = new();

    /// <summary>Modules the check refuses: the module and what fails.</summary>
    public List<(string Module, string Problem)> Refused { get; } = new();

    public static ImportEntryCensus Scan(string fsRoot)
    {
        var census = new ImportEntryCensus();
        var libraries = new HashSet<string>(StringComparer.Ordinal);
        foreach (Hle.NidInfo info in Hle.NidDatabase.All)
        {
            libraries.Add(info.Library);
        }

        foreach (Io.FirmwareFolder folder in new[] { Io.FirmwareLayout.Applications, Io.FirmwareLayout.Patches, Io.FirmwareLayout.GameCardApplications, Io.FirmwareLayout.GameCardPatches })
        {
            string root = folder.Host(fsRoot);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string appDir in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                byte[]? klicensee = Hle.Np.TrophySignature.Klicensee(fsRoot, appDir);
                List<string> files = [];
                foreach (string file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                {
                    string relative = Path.GetRelativePath(appDir, file).Replace('\\', '/');
                    if (relative.StartsWith("sce_sys/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (relative.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)
                        || relative.EndsWith(".suprx", StringComparison.OrdinalIgnoreCase)
                        || relative.EndsWith(".prx", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(file);
                    }
                }

                census.CheckGroup(fsRoot, files, klicensee, libraries);
            }
        }

        foreach (string device in new[] { "ux0", "ur0" })
        {
            string tai = Path.Combine(fsRoot, device, "tai");
            if (Directory.Exists(tai))
            {
                var plugins = Directory.EnumerateFiles(tai, "*.suprx", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList();
                census.CheckGroup(fsRoot, plugins, null, libraries);
            }
        }

        return census;
    }

    /// <summary>One title's modules (or the plugins of one folder): the libraries one of them exports count as found
    /// for all of them, as they do once the title has loaded them.</summary>
    private void CheckGroup(string fsRoot, List<string> files, byte[]? klicensee, HashSet<string> firmwareLibraries)
    {
        var images = new List<(string Name, ModuleImage Image)>();
        foreach (string file in files)
        {
            string name = Path.GetRelativePath(fsRoot, file).Replace('\\', '/');
            try
            {
                images.Add((name, ModuleImage.Read(SelfDecryptor.ToElf(File.ReadAllBytes(file), klicensee))));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Unreadable.Add((name, e.Message));
            }
        }

        var exported = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string _, ModuleImage image) in images)
        {
            foreach ((string library, uint _, ushort _, uint[] _, uint[] _) in image.Exports)
            {
                exported.Add(library);
            }
        }

        foreach ((string name, ModuleImage image) in images)
        {
            Modules++;
            string? problem = null;
            foreach ((string library, uint _, uint[] functions, uint[] variables) in image.ImportEntries)
            {
                if (!exported.Contains(library) && !firmwareLibraries.Contains(library))
                {
                    continue; // not found: the check's result is ignored
                }

                problem = ModuleLoader.ImportEntryProblem(image.SegmentLayout, library, functions, variables);
                if (problem != null)
                {
                    break;
                }
            }

            if (problem == null)
            {
                Passed++;
            }
            else
            {
                Refused.Add((name, problem));
                Log.Debug($"import check: {name}: {problem}");
            }
        }
    }
}
