using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// Every function the installed system software lets user code call, and who calls it: the export tables of every
/// firmware module (user-mode .suprx/.self, and the syscall libraries of the kernel .skprx), the import tables of the
/// system programs (shell.self, the vs0 applications, the vs0/os0 user modules) and of the installed games
/// (ux0:app/*, eboot.bin and their own modules). Used by <c>hle-coverage</c> to measure how much of the real system
/// the HLE implements.
/// </summary>
public sealed class FunctionCensus
{
    /// <summary>Syscall export: a kernel module library user code can call.</summary>
    const ushort SyscallExport = 0x4000;
    static ReadOnlySpan<byte> ElfMagic => [0x7F, (byte)'E', (byte)'L', (byte)'F'];

    /// <summary>Exported functions: NID → (library, module path).</summary>
    public Dictionary<uint, (string Library, string Module)> Exported { get; } = new();
    /// <summary>NIDs imported by the system programs and modules (with one importer each, for reports).</summary>
    public Dictionary<uint, string> SystemImports { get; } = new();
    /// <summary>NIDs imported by the installed games (eboot.bin and their modules) → one importing title.</summary>
    public Dictionary<uint, string> GameImports { get; } = new();
    public int FirmwareModules, Games;
    /// <summary>Firmware files that did not decrypt or parse.</summary>
    public List<string> Failed { get; } = new();

    public static FunctionCensus Scan(string fsRoot)
    {
        var c = new FunctionCensus();
        foreach (var dev in new[] { "os0", "vs0" })
        {
            string root = Path.Combine(fsRoot, dev);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".suprx" or ".skprx" or ".self" or ".prx") && !Path.GetFileName(file).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string vita = dev + ":" + Path.GetRelativePath(root, file).Replace('\\', '/');
                if (vita.StartsWith("os0:sm/", StringComparison.Ordinal))
                {
                    continue; // secure-world modules: no user exports
                }

                byte[] elf;
                try
                {
                    elf = SelfDecryptor.ToElf(File.ReadAllBytes(file), null);
                }
                catch (Exception e) when (e is not OutOfMemoryException) { c.Failed.Add(vita); Log.Debug($"census: {vita}: {e.Message}"); continue; }
                bool kernel = ext == ".skprx";
                // a kernel image (bootimage.skprx) carries more modules as whole ELFs after its own
                var starts = new List<int> { 0 };
                if (kernel)
                {
                    for (int i = elf.AsSpan(4).IndexOf(ElfMagic); i >= 0;)
                    {
                        starts.Add(i + 4);
                        int next = elf.AsSpan(i + 8).IndexOf(ElfMagic);
                        i = next < 0 ? -1 : i + 4 + next;
                    }
                }

                for (int k = 0; k < starts.Count; k++)
                {
                    int end = k + 1 < starts.Count ? starts[k + 1] : elf.Length;
                    ModuleImage img;
                    try
                    {
                        img = ModuleImage.Read(k == 0 && starts.Count == 1 ? elf : elf[starts[k]..end]);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { if (starts.Count == 1) { c.Failed.Add(vita); Log.Debug($"census: {vita}: {e.Message}"); } continue; }
                    c.FirmwareModules++;
                    string where = k == 0 ? vita : $"{vita}/{img.Name}";
                    foreach (var (lib, _, attr, funcs, _) in img.Exports)
                    {
                        if (lib.Length == 0 || (kernel && (attr & SyscallExport) == 0))
                        {
                            continue;
                        }

                        foreach (var nid in funcs)
                        {
                            c.Exported.TryAdd(nid, (lib, where));
                        }
                    }
                    if (!kernel)
                    {
                        foreach (var (_, _, funcs) in img.Imports)
                        {
                            foreach (var nid in funcs)
                            {
                                c.SystemImports.TryAdd(nid, where);
                            }
                        }
                    }
                }
            }
        }
        string apps = Path.Combine(fsRoot, "ux0", "app");
        if (Directory.Exists(apps))
        {
            foreach (var appDir in Directory.EnumerateDirectories(apps).Order(StringComparer.OrdinalIgnoreCase))
            {
                string title = Path.GetFileName(appDir);
                byte[]? klic = TrophySignature.Klicensee(fsRoot, appDir);
                var imports = new List<uint>();
                bool any = false;
                foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                {
                    string rel = Path.GetRelativePath(appDir, file).Replace('\\', '/');
                    if (rel.StartsWith("sce_sys/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!rel.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) && !rel.EndsWith(".suprx", StringComparison.OrdinalIgnoreCase)
                        && !rel.EndsWith(".prx", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var img = ModuleImage.Read(SelfDecryptor.ToElf(File.ReadAllBytes(file), klic));
                        any = true;
                        foreach (var (_, _, funcs) in img.Imports)
                        {
                            imports.AddRange(funcs);
                        }
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { Log.Debug($"census: {title}/{rel}: {e.Message}"); }
                }
                if (any)
                {
                    c.Games++;
                }

                foreach (var nid in imports)
                {
                    c.GameImports.TryAdd(nid, title);
                }
            }
        }

        return c;
    }
}
