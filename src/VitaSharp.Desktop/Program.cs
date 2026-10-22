using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Package;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop;

public static class Program
{
    public static string DataRoot => Environment.GetEnvironmentVariable("VITASHARP_DATA")
        ?? DataPaths.DefaultRoot();

    public static string FsRoot => Path.Combine(DataRoot, "fs");
    public static string Ux0 => Path.Combine(FsRoot, "ux0");

    /// <summary>True when --data=&lt;folder&gt; was given (it then also wins over a test scenario's data=).</summary>
    public static bool DataFromCommandLine { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // --data=<root> overrides the data folder for any command (same as VITASHARP_DATA)
        var data = args.FirstOrDefault(a => a.StartsWith("--data="));
        if (data != null)
        {
            Environment.SetEnvironmentVariable("VITASHARP_DATA", Path.GetFullPath(data["--data=".Length..]));
            args = args.Where(a => a != data).ToArray();
            DataFromCommandLine = true;
        }
        if (OperatingSystem.IsMacOS() && MacVulkanLoader.RelaunchIfNeeded(args) is { } rc) return rc;

        if (args.Length == 0 || args[0] == "gui") return Launcher.LauncherApp.Run(args);

        switch (args[0])
        {
            case "install" when args.Length >= 2 && IsArchive(args[1]):
                return InstallVpkCli(args[1], args.Skip(2).ToArray());
            case "install" when args.Length >= 3:
            {
                var t = PkgInstaller.Install(args[1], args[2], Ux0, Console.WriteLine);
                Console.WriteLine($"Installed {t.TitleId} \"{t.Title}\" ({t.Type}) -> {t.Path}");
                return 0;
            }
            case "run" when args.Length >= 2 && IsArchive(args[1]) && File.Exists(args[1]):
            {
                var app = InstallVpkForRun(args[1], args.Skip(2).ToArray());
                if (app == null) return 1;
                return Runner.Run(app, args.Skip(2).Where(a => a != "--reinstall" && !a.StartsWith("--license=")).ToArray());
            }
            case "self2elf" when args.Length >= 3:
            {
                byte[]? klic = args.Length >= 4 ? PkgInstaller.ReadKlicensee(args[3]) : null;
                var elf = VitaSharp.Core.Loader.Self.SelfDecryptor.ToElf(File.ReadAllBytes(args[1]), klic);
                File.WriteAllBytes(args[2], elf);
                Console.WriteLine($"Wrote {elf.Length} bytes");
                return 0;
            }
            case "ui-snapshot":
                return Launcher.UiSnapshot.Run(args.Skip(1).ToArray());
            case "pad-test":
                return Input.PadTest.Run(args.Skip(1).ToArray());
            case "camera-test":
                return CameraTest.Run(args.Skip(1).ToArray());
            case "imports" when args.Length >= 2:
                return ListImports(args[1], args.Length > 2 ? args[2] : "eboot.bin");
            case "shadertest" when args.Length >= 2:
                return ShaderTest.Run(ResolveApp(args[1]), args.Skip(2).ToArray());
            case "disasm" when args.Length >= 3:
            {
                Log.Level = LogLevel.Warn;
                using var emu = new Emulator(new EmulatorOptions { FsRoot = FsRoot, EnableJit = false });
                emu.LoadTitle(ResolveApp(args[1]));
                uint addr = Convert.ToUInt32(args[2], 16) & ~1u;
                int count = args.Length > 3 ? int.Parse(args[3]) : 40;
                for (int i = 0; i < count; i++)
                {
                    uint hw = VitaSharp.Core.Memory.GuestMemory.Read<ushort>(addr);
                    VitaSharp.Core.Cpu.DecodedInst d;
                    if ((hw & 0xE000) == 0xE000 && (hw & 0x1800) != 0)
                        d = VitaSharp.Core.Cpu.Decoder.DecodeThumb32((hw << 16) | VitaSharp.Core.Memory.GuestMemory.Read<ushort>(addr + 2), addr, false, 0);
                    else d = VitaSharp.Core.Cpu.Decoder.DecodeThumb16(hw, addr, false, 0);
                    Console.WriteLine($"{addr:X8}: {(d.Size == 4 ? $"{d.Raw:X8}" : $"    {d.Raw & 0xFFFF:X4}")}  {d.Op}");
                    addr += d.Size;
                }
                return 0;
            }
            case "stub" when args.Length >= 3:
            {
                Log.Level = LogLevel.Warn;
                using var emu = new Emulator(new EmulatorOptions { FsRoot = FsRoot, EnableJit = false });
                emu.LoadTitle(ResolveApp(args[1]));
                uint addr = Convert.ToUInt32(args[2], 16) & ~3u;
                for (uint a = addr; a < addr + 16; a += 4) Console.WriteLine($"{a:X8}: {VitaSharp.Core.Memory.GuestMemory.R32(a):X8}");
                uint nid = VitaSharp.Core.Memory.GuestMemory.R32(addr + 8);
                Console.WriteLine($"NID {nid:X8} = {NidDatabase.NameOf(nid)}; svc index -> {emu.Hle.GetFunction(VitaSharp.Core.Memory.GuestMemory.R32(addr) & 0xFFFFFF).Name}");
                return 0;
            }
            case "memdiff" when args.Length >= 3:
                return MemDiff.Run(args[1], args[2], args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 10f);
            case "test" when args.Length >= 2:
                return TestScenario.Run(args[1], args.Skip(2).ToArray());
            case "run" when args.Length >= 2:
                return Runner.Run(ResolveApp(args[1]), args.Skip(2).ToArray());
            default:
                PrintUsage();
                return 1;
        }
    }

    public static string ResolveApp(string arg)
    {
        if (Directory.Exists(arg)) return arg;
        var p = Path.Combine(Ux0, "app", arg);
        if (Directory.Exists(p)) return p;
        throw new DirectoryNotFoundException($"Title {arg} not found (looked in {p})");
    }

    static bool IsArchive(string path) =>
        path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    static string? OptValue(string[] options, string name) =>
        options.FirstOrDefault(o => o.StartsWith(name + "="))?[(name.Length + 1)..];

    /// <summary>install &lt;file.vpk|.zip&gt; [--license=&lt;work.bin|.rif&gt;] [--force]</summary>
    static int InstallVpkCli(string archive, string[] options)
    {
        bool force = options.Contains("--force") || options.Contains("-y");
        int lastPercent = -1;
        try
        {
            var results = VpkInstaller.Install(archive, Ux0, new VpkInstallOptions
            {
                Log = Console.WriteLine,
                LicensePath = OptValue(options, "--license"),
                ConfirmOverwrite = c => force || AskYesNo($"{c.TitleId} ({c.Kind}) is already installed at {c.DestinationPath}. Replace it?"),
                Progress = p =>
                {
                    int pct = (int)(p.Fraction * 100);
                    if (pct / 10 == lastPercent / 10 && pct != 100) return;
                    lastPercent = pct;
                    Console.WriteLine($"  {pct,3}%");
                },
            });
            foreach (var r in results)
                Console.WriteLine(r.Installed
                    ? $"{(r.Replaced ? "Updated" : "Installed")} {r.TitleId} \"{r.Content.Title}\" ({r.Content.Kind}, category {r.Content.Category}) -> {r.InstallPath}"
                    : $"Skipped {r.TitleId} ({r.Content.Kind}): already installed (use --force to replace)");
            return 0;
        }
        catch (Exception e) when (e is InvalidDataException or VpkLicenseException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Install failed: " + e.Message);
            return 1;
        }
    }

    /// <summary>run &lt;file.vpk&gt;: installs the archive (unless its app is already installed) and returns the app folder.</summary>
    static string? InstallVpkForRun(string archive, string[] options)
    {
        try
        {
            var contents = VpkInstaller.Inspect(archive, Ux0);
            var app = contents.FirstOrDefault(c => c.Kind == VpkContentKind.App);
            bool reinstall = options.Contains("--reinstall");
            if (app != null && app.AlreadyInstalled && !reinstall)
            {
                Console.WriteLine($"{app.TitleId} is already installed; booting {app.DestinationPath} (use --reinstall to install the VPK again)");
                return app.DestinationPath;
            }
            Console.WriteLine($"Installing {Path.GetFileName(archive)}…");
            VpkInstaller.Install(archive, Ux0, new VpkInstallOptions
            {
                Log = Console.WriteLine,
                LicensePath = OptValue(options, "--license"),
                ConfirmOverwrite = c => reinstall || c.Kind != VpkContentKind.App,
            });
            if (app != null) return app.DestinationPath;
            // a patch/add-on archive: boot the base game it belongs to, when installed
            var baseApp = Path.Combine(Ux0, "app", contents[0].TitleId);
            if (Directory.Exists(baseApp)) return baseApp;
            Console.Error.WriteLine($"Installed {contents[0].TitleId} ({contents[0].Kind}), but its base game is not installed, so there is nothing to boot.");
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or VpkLicenseException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Install failed: " + e.Message);
            return null;
        }
    }

    static bool AskYesNo(string question)
    {
        if (Console.IsInputRedirected) return false;
        Console.Write(question + " [y/N] ");
        var answer = Console.ReadLine()?.Trim();
        return answer != null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    static int ListImports(string titleArg, string exe)
    {
        Log.Level = LogLevel.Warn;
        using var emu = new Emulator(new EmulatorOptions { FsRoot = FsRoot, EnableJit = false });
        string appDir = ResolveApp(titleArg);
        emu.LoadTitle(appDir, exe);
        // Modules the game loads at run time (sceKernelLoadStartModule of Media/Modules/*.suprx, sysmodule LLE of
        // sce_module/*.suprx): load them too, without module_start, so imports they export count as LLE.
        foreach (var file in Directory.EnumerateFiles(appDir, "*.suprx", SearchOption.AllDirectories).Order())
        {
            string rel = "app0:" + Path.GetRelativePath(appDir, file).Replace('\\', '/');
            if (rel.StartsWith("app0:sce_sys/", StringComparison.OrdinalIgnoreCase)) continue; // system plugins (manual, about)
            if (emu.Kernel.Modules.Values.Any(m => string.Equals(m.Path, rel, StringComparison.OrdinalIgnoreCase))) continue;
            try { emu.LoadModule(rel); }
            catch (Exception e) { Console.WriteLine($"(could not load {rel}: {e.Message})"); }
        }
        int total = 0, hle = 0, lle = 0;
        foreach (var m in emu.Kernel.Modules.Values.OrderBy(m => m.Uid))
        {
            Console.WriteLine($"== {m.Name} ({m.Path})");
            foreach (var (lib, _, nids) in m.Imports.OrderBy(i => i.Library))
            {
                var missing = new List<string>();
                foreach (var nid in nids)
                {
                    total++;
                    if (emu.Linker.TryGetFunctionExport(nid, out _)) lle++;
                    else if (emu.Hle.IsImplemented(nid)) hle++;
                    else missing.Add(NidDatabase.NameOf(nid));
                }
                Console.WriteLine($"  {lib,-28} {nids.Length,4} imports, {missing.Count,4} missing");
                foreach (var n in missing) Console.WriteLine($"      - {n}");
            }
        }
        Console.WriteLine($"Total {total}: LLE {lle}, HLE {hle}, missing {total - lle - hle}");
        return 0;
    }

    static void PrintUsage()
    {
        Console.WriteLine("VitaSharp - PS Vita emulator");
        Console.WriteLine("  (no arguments)                        open the game library window");
        Console.WriteLine("  install <game.pkg> <work.bin|.rif>   install a package into the virtual ux0");
        Console.WriteLine("  install <game.vpk|.zip> [--license=<work.bin|.rif>] [--force]");
        Console.WriteLine("                                       install a VPK/content zip (app, patch or add-on, by param.sfo category)");
        Console.WriteLine("  run <TITLEID|folder> [options]       boot a title");
        Console.WriteLine("  run <game.vpk> [--reinstall] [options]  install the VPK if needed, then boot it");
        Console.WriteLine("  --data=<folder>                      use another data folder (default: " + DataPaths.DefaultRoot() + ")");
        Console.WriteLine("  test <scenario.txt> [options]        run an automated scenario (frames, contact sheet, report); run options given here win over the scenario's args=");
        Console.WriteLine("  imports <TITLEID|folder> [x.self]    list the system functions a title (or one of its executables) needs");
        Console.WriteLine("  self2elf <in> <out> [license]        decrypt a SELF");
        Console.WriteLine("  camera-test [device] [out-prefix]    grab one webcam frame (SceCamera host path) as PNG");
        Console.WriteLine("  pad-test [--seconds=N] [--virtual]   list controllers (SDL2) and print their mapped Vita input");
        Console.WriteLine("  ui-snapshot <out-dir>                render Settings > Controles driven by a simulated DualSense (use a scratch --data)");
        Console.WriteLine("  run options: --camera=<name|index|none> webcam for the Vita cameras (default: first)");
        Console.WriteLine("               --video-decoder=auto|sw|d3d11va|vaapi|videotoolbox  H.264 decoding (default auto: hardware, software fallback)");
        Console.WriteLine("               --shaderc-opt=zero|size|perf  shader optimisation (default zero: fast compiles, less stutter)");
        Console.WriteLine("               --async-shaders=on|off  compile new shaders and pipelines on worker threads (default on)");
        Console.WriteLine("               --async-pipelines=on|off  background pipelines only (default on), --shader-workers=N (default auto)");
    }
}
