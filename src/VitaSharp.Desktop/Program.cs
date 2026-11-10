using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Package;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop;

public static class Program
{
    public static string DataRoot => Environment.GetEnvironmentVariable(EnvVars.Data)
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
            Environment.SetEnvironmentVariable(EnvVars.Data, Path.GetFullPath(data["--data=".Length..]));
            args = args.Where(a => a != data).ToArray();
            DataFromCommandLine = true;
        }
        // the update swap runs from the downloaded package and touches nothing else (no Vulkan, no window)
        if (args.Length > 0 && args[0] == "apply-update") return Updater.ApplyCli(args.Skip(1).ToArray());
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
            case "install-firmware" when args.Length >= 2:
                return FirmwareCli.Install(args[1], args.Skip(2).ToArray());
            case "firmware-info":
                return FirmwareCli.Info();
            case "firmware-verify":
                return FirmwareCli.Verify(args.Skip(1).ToArray());
            case "import-trophies" when args.Length >= 2:
                return ImportTrophies(args[1]);
            case "self2elf" when args.Length >= 3:
            {
                byte[]? klic = args.Length >= 4 ? PkgInstaller.ReadKlicensee(args[3]) : null;
                var elf = VitaSharp.Core.Loader.Self.SelfDecryptor.ToElf(File.ReadAllBytes(args[1]), klic);
                File.WriteAllBytes(args[2], elf);
                Console.WriteLine($"Wrote {elf.Length} bytes");
                return 0;
            }
            case "rco-dump" when args.Length >= 2:
                return RcoDump(args[1], args.Skip(2).FirstOrDefault(a => a.StartsWith("--extract="))?["--extract=".Length..]);
            case "registry":
            {
                // registry [prefix] [path=value ...]: list the system registry (firmware template + written values);
                // path=value writes an int key (or a string key, quoted value) first
                var reg = VitaSharp.Core.Firmware.Registry.For(FsRoot);
                string prefix = args.Skip(1).FirstOrDefault(a => !a.Contains('=')) ?? "/";
                foreach (var set in args.Skip(1).Where(a => a.Contains('=')))
                {
                    int eq = set.IndexOf('='), slash = set.LastIndexOf('/', eq) + 1;
                    string val = set[(eq + 1)..];
                    bool ok = int.TryParse(val, out int iv)
                        ? reg.SetInt(set[..slash], set[slash..eq], iv)
                        : reg.Set(set[..slash], set[slash..eq], System.Text.Encoding.UTF8.GetBytes(val.Trim('"') + "\0"));
                    Console.WriteLine(ok ? $"set {set[..eq]}" : $"no such key {set[..eq]}");
                }
                foreach (var k in reg.Keys.Where(k => k.Path.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k.Path, StringComparer.Ordinal))
                {
                    var v = reg.Get(k.Path) ?? [];
                    string shown = k.Type == VitaSharp.Core.Firmware.Registry.KeyType.Int ? BitConverter.ToInt32(v.Concat(new byte[4]).Take(4).ToArray()).ToString()
                        : k.Type == VitaSharp.Core.Firmware.Registry.KeyType.Str ? "\"" + System.Text.Encoding.UTF8.GetString(v).TrimEnd('\0') + "\"" : Convert.ToHexString(v);
                    Console.WriteLine($"{k.Path} ({k.Type}, {k.Size}) = {shown}");
                }
                return 0;
            }
            case "update":
                return Updater.Cli(args.Skip(1).ToArray());
            case "version":
                Console.WriteLine(Diagnostics.BuildVersion());
                return 0;
            case "ui-snapshot":
                return Launcher.UiSnapshot.Run(args.Skip(1).ToArray());
            case "ui-render":
                return Launcher.UiRender.Run(args.Skip(1).ToArray());
            case "toast-preview" when args.Length >= 2:
            {
                // toast-preview <out.png> [TITLEID]: draws the in-game trophy notification (first trophy of the title's set)
                VitaSharp.Core.Hle.Np.TrophyInfo t = new(1, VitaSharp.Core.Hle.Np.TrophyGrade.Gold, false, 0, -1, "A trophy with quite a long name to shorten", "");
                byte[]? icon = null;
                if (args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) is { } toastTitle
                    && VitaSharp.Core.Hle.Np.TrophyPackage.FindSets(ResolveApp(toastTitle)).FirstOrDefault() is { TrpPath: not null } set
                    && VitaSharp.Core.Hle.Np.TrophyPackage.Load(set.TrpPath, set.CommId) is { } pkg && pkg.Trophies.Count > 1)
                {
                    t = pkg.Trophies[1];
                    icon = pkg.TrophyIcon(t.Id);
                }
                var (bgra, w, h) = Host.TrophyToast.Render(new VitaSharp.Core.Hle.Np.TrophyUnlockedEvent("", "", t, icon), 2f);
                WriteBgraPng(args[1], bgra, w, h);
                // --sound=<out.wav>: also writes the trophy sound the game window plays (firmware chime or built-in)
                if (args.FirstOrDefault(a => a.StartsWith("--sound="))?["--sound=".Length..] is { } wav)
                {
                    var (pcm, source) = VitaSharp.Core.Shell.TrophySound.Load(FsRoot);
                    WriteWav(wav, pcm);
                    Console.WriteLine($"Trophy sound: {source}, {pcm.Seconds:F2} s, {pcm.SampleRate} Hz, {pcm.Channels} ch -> {wav}");
                }
                return 0;
            }
            case "ime-preview" when args.Length >= 2:
            {
                // ime-preview <out.png> [title] [text] [--max=N] [--multiline] [--password]: draws the IME text entry
                // overlay of the game window (sceImeDialog / SceIme) as it looks with that text typed
                var rest = args.Skip(2).Where(a => !a.StartsWith("--")).ToArray();
                int max = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--max="))?["--max=".Length..], out var m) ? m : 16;
                var entry = new VitaSharp.Core.Host.TextEntry(rest.ElementAtOrDefault(0) ?? "Nome", rest.ElementAtOrDefault(1) ?? "", max, 0,
                    args.Contains("--multiline"), args.Contains("--password"));
                var (bgra, w, h) = Host.ImeOverlay.Render(entry, 2f);
                WriteBgraPng(args[1], bgra, w, h);
                return 0;
            }
            case "msg-preview" when args.Length >= 2:
            {
                // msg-preview <out.png> [message] [--buttons=Yes,No] [--selected=N] [--progress=N]: draws the message
                // dialog panel of the game window (sceMsgDialog / save data messages)
                string msg = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "Message";
                string[] buttons = args.FirstOrDefault(a => a.StartsWith("--buttons="))?["--buttons=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [VitaSharp.Core.Localization.Loc.Get("DlgOk")];
                int sel = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--selected="))?["--selected=".Length..], out var sv) ? sv : 0;
                int? progress = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--progress="))?["--progress=".Length..], out var pv) ? pv : null;
                var panel = Host.MessageOverlay.Render(msg.Replace("\\n", "\n"), buttons, sel, progress, 2f);
                WriteBgraPng(args[1], panel.Bgra, panel.Width, panel.Height);
                return 0;
            }
            case "pad-test":
                return Input.PadTest.Run(args.Skip(1).ToArray());
            case "camera-test":
                return CameraTest.Run(args.Skip(1).ToArray());
            case "audio-devices":
            {
                // audio-devices [--audio-device=<name>]: the outputs OpenAL lists, the system default and the one a game
                // would open now (silent: no sound is played)
                var (outs, def) = Host.OpenAlAudio.ListOutputs();
                foreach (var o in outs) Console.WriteLine($"{(o == def ? "* " : "  ")}{o}");
                using var a = Host.OpenAlAudio.TryCreate(OptValue(args, "--audio-device"));
                Console.WriteLine(a == null ? "no output opened" : $"opened: {a.DeviceName}");
                return a == null ? 1 : 0;
            }
            case "hle-implemented":
                // hle-implemented [--vitasdk=<vitasdk-user-functions.tsv>]: every NID with an HLE implementation, as
                // docs/hle/data/vitasharp-implemented.tsv (nid, name, module, library, "vh" when the VitaSDK lists it).
                return ListHleImplemented(args.Skip(1).FirstOrDefault(a => a.StartsWith("--vitasdk="))?["--vitasdk=".Length..]);
            case "hle-coverage":
                return HleCoverage(args.Skip(1).FirstOrDefault(a => a.StartsWith("--out="))?["--out=".Length..],
                    args.Skip(1).FirstOrDefault(a => a.StartsWith("--vitasdk="))?["--vitasdk=".Length..],
                    args.Skip(1).FirstOrDefault(a => a.StartsWith("--status="))?["--status=".Length..]);
            case "imports" when args.Length >= 2:
            {
                // imports <title> [exe] [--names=SceNp,SceNet]: also list every imported function name of the
                // libraries whose name starts with one of the prefixes (online-feature survey).
                var names = args.Skip(2).FirstOrDefault(a => a.StartsWith("--names="))?["--names=".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
                var exeArg = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "eboot.bin";
                return ListImports(args[1], exeArg, names);
            }
            case "shadertest" when args.Length >= 2:
                return ShaderTest.Run(ResolveApp(args[1]), args.Skip(2).ToArray());
            case "disasm" when args.Length >= 3:
            {
                Log.Level = LogLevel.Warn;
                using var emu = new Emulator(new EmulatorOptions { FsRoot = FsRoot, EnableJit = false });
                var selfArg = args.FirstOrDefault(a => a.StartsWith("--self="))?["--self=".Length..];
                if (selfArg != null) emu.LoadTitle(ResolveApp(args[1]), selfArg); else emu.LoadTitle(ResolveApp(args[1]));
                // --load=vs0:sys/external/libpaf.suprx,...: also load these modules (firmware code a system program
                // loads at run time), each printed with its base so addresses like ScePaf+1E011E can be found
                foreach (var mod in args.FirstOrDefault(a => a.StartsWith("--load="))?["--load=".Length..].Split(',') ?? [])
                    Console.WriteLine($"{mod}: base 0x{emu.LoadModule(mod).Segments.Values.Min(s => s.Address):X8}");
                // --raw=<file>: write COUNT bytes from ADDR of the loaded image instead (for an external disassembler)
                var rawOut = args.FirstOrDefault(a => a.StartsWith("--raw="))?["--raw=".Length..];
                args = args.Where(a => !a.StartsWith("--")).ToArray();
                uint addr = Convert.ToUInt32(args[2], 16) & ~1u;
                int count = args.Length > 3 ? int.Parse(args[3]) : 40;
                if (rawOut != null)
                {
                    var bytes = new byte[count];
                    for (int i = 0; i < count; i++) bytes[i] = VitaSharp.Core.Memory.GuestMemory.Read<byte>(addr + (uint)i);
                    File.WriteAllBytes(rawOut, bytes);
                    return 0;
                }
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
            case "png-diff" when args.Length >= 3:
                ReplayGpu.Compare(args[1], args[2], args.Length > 3 ? args[3] : "*.png");
                return 0;
            case "replay-gpu" when args.Length >= 2:
                return ReplayGpu.Run(args[1], args.Skip(2).ToArray());
            case "rerun" when args.Length >= 2:
                return SessionCaptureControl.Rerun(args[1], args.Skip(2).ToArray());
            case "boot-shell":
                // the PS Vita system software itself: vs0:vsh/shell/shell.self (home screen, LiveArea, system apps)
                return Runner.Run(ResolveApp("vs0:vsh/shell"), args.Skip(1).Append("--self=shell.self").ToArray());
            case "test" when args.Length >= 2:
                return TestScenario.Run(args[1], args.Skip(2).ToArray());
            case "run" when args.Length >= 2:
            {
                string app = ResolveApp(args[1]);
                var opts = args.Skip(2).ToArray();
                // a system application (vs0:app/NPXS*) runs under the shell, as on the Vita: the shell boots and opens it
                // (as when an application asks it to); --no-shell runs it alone (its libshellsvc then finds no shell to talk to)
                if (SystemAppTitle(app) is { } tid && !opts.Contains("--no-shell") && Runner.Opt(opts, "--self") == null && Runner.Opt(opts, "--load-state") == null)
                    return Runner.Run(ResolveApp("vs0:vsh/shell"), opts.Append("--self=shell.self").Append($"--launch-app={tid}").ToArray());
                return Runner.Run(app, opts);
            }
            default:
                PrintUsage();
                return 1;
        }
    }

    /// <summary>The title id of a system application folder of the installed firmware (vs0:app/NPXS*), else null.</summary>
    internal static string? SystemAppTitle(string appDir)
    {
        string sys = Path.GetFullPath(Path.Combine(FsRoot, "vs0", "app"));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDir));
        return string.Equals(Path.GetDirectoryName(full), sys, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(full) : null;
    }

    public static string ResolveApp(string arg)
    {
        if (Directory.Exists(arg)) return arg;
        // a folder of the virtual file system: vs0:vsh/shell (the shell, with --self=shell.self)
        if (arg.Length > 4 && arg.IndexOf(':') is > 1 and var colon && Path.Combine(FsRoot, arg[..colon], arg[(colon + 1)..].TrimStart('/')) is var dev && Directory.Exists(dev))
            return dev;
        var p = Path.Combine(Ux0, "app", arg);
        if (Directory.Exists(p)) return p;
        // system applications of the installed firmware (vs0:app/NPXS*)
        var sys = Path.Combine(FsRoot, "vs0", "app", arg);
        if (Directory.Exists(sys)) return sys;
        throw new DirectoryNotFoundException($"Title {arg} not found (looked in {p} and {sys})");
    }

    internal static void WriteBgraPng(string path, byte[] bgra, int w, int h)
    {
        using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bmp.GetPixels(), bgra.Length);
        using var f = File.Create(path);
        bmp.Encode(f, SkiaSharp.SKEncodedImageFormat.Png, 100);
    }

    /// <summary>A 16-bit PCM WAV file.</summary>
    static void WriteWav(string path, VitaSharp.Core.Shell.PcmSound pcm)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = pcm.Samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)pcm.Channels);
        w.Write(pcm.SampleRate); w.Write(pcm.SampleRate * pcm.Channels * 2); w.Write((short)(pcm.Channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in pcm.Samples) w.Write(s);
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
            Console.WriteLine($"Installing {Path.GetFileName(archive)}\u2026");
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

    /// <summary>import-trophies &lt;folder|--vita3k&gt;: a PS Vita's ur0:user/00/trophy (decrypted copy) or Vita3K's trophies.</summary>
    static int ImportTrophies(string source)
    {
        if (source == "--vita3k")
        {
            var v3k = VitaSharp.Core.Hle.Np.TrophyImport.FindVita3K();
            if (v3k == null) { Console.Error.WriteLine("No Vita3K trophy data found."); return 1; }
            source = v3k;
        }
        if (!Directory.Exists(source)) { Console.Error.WriteLine($"{source}: no such folder"); return 1; }
        try
        {
            var r = VitaSharp.Core.Hle.Np.TrophyImport.Import(source, FsRoot);
            foreach (var t in r.Titles)
                Console.WriteLine(r.Kind == VitaSharp.Core.Hle.Np.TrophyImport.SourceKind.Vita3K ? $"  {t.CommId}: {t.Total} unlocked, {t.Imported} new" : $"  {t.CommId}");
            Console.WriteLine(r.Message);
            return r.Kind is VitaSharp.Core.Hle.Np.TrophyImport.SourceKind.Vita3K or VitaSharp.Core.Hle.Np.TrophyImport.SourceKind.VitaDecrypted ? 0 : 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Import failed: " + e.Message);
            return 1;
        }
    }

    static bool AskYesNo(string question)
    {
        if (Console.IsInputRedirected) return false;
        Console.Write(question + " [y/N] ");
        var answer = Console.ReadLine()?.Trim();
        return answer != null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    static int ListImports(string titleArg, string exe, string[] namePrefixes)
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
                if (namePrefixes.Any(p => lib.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    foreach (var nid in nids)
                        if (!missing.Contains(NidDatabase.NameOf(nid))) Console.WriteLine($"      . {NidDatabase.NameOf(nid)}");
            }
        }
        Console.WriteLine($"Total {total}: LLE {lle}, HLE {hle}, missing {total - lle - hle}");
        return 0;
    }

    /// <summary>Every NID with an HLE implementation -> (name, HLE module). Same resolution as
    /// HleRegistry.RegisterModule: explicit NID, else every NID of the name (optionally per library); a later
    /// registration of a NID replaces an earlier one.</summary>
    /// <summary>Every NID with an [Hle] entry point; Default when every entry point of it is a documented firmware
    /// default (<see cref="HleAttribute.Default"/>), not a real implementation.</summary>
    static SortedDictionary<uint, (string Name, string Module, bool Default)> HleImplementedNids()
    {
        var byNid = new SortedDictionary<uint, (string Name, string Module, bool Default)>();
        foreach (var m in HleBindings.Modules)
            foreach (var b in m.Bindings())
            {
                string name = b.Name ?? b.Method;
                var nids = b.Nid != 0 ? [b.Nid]
                    : NidDatabase.LookupName(name).Where(i => b.Library == null || i.Library == b.Library).Select(i => i.Nid).ToArray();
                foreach (var nid in nids)
                    byNid[nid] = byNid.TryGetValue(nid, out var had) ? had with { Default = had.Default && b.Default }
                        : (NidDatabase.Lookup(nid)?.Name ?? name, m.Type.Name, b.Default);
            }
        return byNid;
    }

    /// <summary>hle-coverage [--out=&lt;tsv&gt;]: how much of the real system the HLE implements. Total = every function the
    /// installed firmware exports to user code (user modules, syscall libraries of the kernel modules), split into the
    /// ones the installed games import, the ones the system programs/modules import (not already counted for games) and
    /// the rest; --out writes one line per firmware function (nid, name, library, module, group, implemented, one
    /// importer). --status=&lt;repository root&gt; writes the README status (docs/hle/data/hle-status.tsv, the badges and
    /// progress map in docs/assets/status, docs/hle/progress.md; see HleStatusReport).</summary>
    static int HleCoverage(string? outTsv, string? vitasdkTsv = null, string? statusRoot = null)
    {
        Log.Level = LogLevel.Warn;
        var impl = HleImplementedNids();
        var c = VitaSharp.Core.Firmware.FunctionCensus.Scan(FsRoot);
        string Importer(uint nid) => c.GameImports.TryGetValue(nid, out var g) ? g : c.SystemImports.GetValueOrDefault(nid, "-");
        string Group(uint nid) => c.GameImports.ContainsKey(nid) ? "games" : c.SystemImports.ContainsKey(nid) ? "system" : "general";
        var rows = c.Exported.Select(e => (Nid: e.Key, e.Value.Library, e.Value.Module, Group: Group(e.Key), Impl: impl.ContainsKey(e.Key),
            Default: impl.TryGetValue(e.Key, out var iv) && iv.Default)).ToList();
        Console.WriteLine($"{c.FirmwareModules} firmware modules read, {c.Games} games; not read: {string.Join(", ", c.Failed)}");
        void Line(string label, IEnumerable<(uint Nid, string Library, string Module, string Group, bool Impl, bool Default)> set)
        {
            var l = set.ToList();
            int n = l.Count(r => r.Impl), d = l.Count(r => r.Default);
            Console.WriteLine($"{label,-10} {n,6} / {l.Count,6}  {(l.Count == 0 ? 0 : 100.0 * n / l.Count),6:F1}%  (real {n - d}, default {d})");
        }
        Line("overall", rows);
        foreach (var g in new[] { "games", "system", "general" }) Line(g, rows.Where(r => r.Group == g));
        // the VitaSDK column: every user function of the VitaSDK NID list (docs/hle/data/vitasdk-user-functions.tsv), exported
        // by the installed firmware or not
        vitasdkTsv ??= Path.Combine(AppContext.BaseDirectory, "vitasdk-user-functions.tsv") is var d && File.Exists(d) ? d : null;
        int sdkTotal = 0, sdkImpl = 0, sdkDefault = 0;
        if (vitasdkTsv != null && File.Exists(vitasdkTsv))
        {
            var sdk = File.ReadLines(vitasdkTsv).Where(l => l.StartsWith("0x"))
                .Select(l => uint.Parse(l.AsSpan(2, 8), System.Globalization.NumberStyles.HexNumber)).Distinct().ToList();
            int n = sdk.Count(impl.ContainsKey), nd = sdk.Count(x => impl.TryGetValue(x, out var v) && v.Default);
            (sdkTotal, sdkImpl, sdkDefault) = (sdk.Count, n, nd);
            Console.WriteLine($"{"vitasdk",-10} {n,6} / {sdk.Count,6}  {(sdk.Count == 0 ? 0 : 100.0 * n / sdk.Count),6:F1}%  (real {n - nd}, default {nd})");
        }
        if (statusRoot != null)
        {
            if (sdkTotal == 0) { Console.Error.WriteLine("--status needs --vitasdk=<vitasdk-user-functions.tsv>"); return 1; }
            var status = VitaSharp.Core.Firmware.HleStatusReport.Build(VitaSharp.Core.Firmware.FirmwareInfo.Read(FsRoot)?.Version ?? "?", c.Games, sdkTotal, sdkImpl,
                rows.Select(r => (r.Library, r.Module, r.Group, r.Impl, r.Default)), sdkDefault);
            var files = new Dictionary<string, string>(VitaSharp.Core.Firmware.HleStatusReport.Render(status))
            {
                [VitaSharp.Core.Firmware.HleStatusReport.TsvPath] = VitaSharp.Core.Firmware.HleStatusReport.ToTsv(status),
            };
            foreach (var (path, text) in files)
            {
                string full = Path.Combine(statusRoot, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            Console.WriteLine($"status: {files.Count} files written under {statusRoot}");
        }
        // games/system imports that no installed firmware module exports (HLE-only libraries, removed ones)
        int extra = c.GameImports.Keys.Concat(c.SystemImports.Keys).Distinct().Count(n => !c.Exported.ContainsKey(n));
        Console.WriteLine($"({extra} imported NIDs are not exported by any installed firmware module; not counted)");
        if (outTsv != null)
            File.WriteAllLines(outTsv, rows.OrderBy(r => r.Library, StringComparer.Ordinal).ThenBy(r => NidDatabase.NameOf(r.Nid), StringComparer.Ordinal)
                .Select(r => $"0x{r.Nid:X8}	{NidDatabase.NameOf(r.Nid)}	{r.Library}	{r.Module}	{r.Group}	{(r.Default ? "default" : r.Impl ? "impl" : "-")}	{Importer(r.Nid)}")
                .Concat(c.GameImports.Concat(c.SystemImports).Where(e => !c.Exported.ContainsKey(e.Key)).DistinctBy(e => e.Key)
                    .Select(e => $"0x{e.Key:X8}	{NidDatabase.NameOf(e.Key)}	{NidDatabase.Lookup(e.Key)?.Library ?? "-"}	{e.Value}	not-exported	{(impl.TryGetValue(e.Key, out var v) ? v.Default ? "default" : "impl" : "-")}	{Importer(e.Key)}")));
        return 0;
    }

    static int ListHleImplemented(string? vitasdkTsv)
    {
        var vitasdk = new HashSet<uint>();
        if (vitasdkTsv != null)
            foreach (var line in File.ReadLines(vitasdkTsv))
                if (line.StartsWith("0x") && uint.TryParse(line.AsSpan(2, 8), System.Globalization.NumberStyles.HexNumber, null, out var n)) vitasdk.Add(n);
        var byNid = HleImplementedNids();
        foreach (var (nid, (name, module, isDefault)) in byNid.OrderBy(e => NidDatabase.Lookup(e.Key)?.Library ?? "~", StringComparer.Ordinal).ThenBy(e => e.Value.Name, StringComparer.Ordinal))
            Console.WriteLine($"0x{nid:X8}	{name}	{module}	{NidDatabase.Lookup(nid)?.Library ?? "-"}	{(vitasdk.Contains(nid) ? "vh" : "-")}{(isDefault ? "	default" : "")}");
        Console.Error.WriteLine($"{byNid.Count} implemented NIDs ({byNid.Keys.Count(vitasdk.Contains)} in the VitaSDK list, {byNid.Values.Count(v => v.Default)} default)");
        return 0;
    }

    /// <summary>rco-dump &lt;file.rco&gt; [--extract=&lt;dir&gt;]: the element tree of a system RCO and, optionally, its files.</summary>
    static int RcoDump(string path, string? extractDir)
    {
        var rco = VitaSharp.Core.Shell.Rco.Load(path);
        Console.Write(rco.Dump());
        if (extractDir == null) return 0;
        Directory.CreateDirectory(extractDir);
        int n = 0;
        foreach (var f in rco.Files())
        {
            string name = string.Concat((f.Id.Length > 0 ? f.Id : $"{f.Table}_{n}").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            File.WriteAllBytes(Path.Combine(extractDir, name + VitaSharp.Core.Shell.RcoResources.Extension(f)), f.Data);
            n++;
            if (!VitaSharp.Core.Shell.Gim.IsGim(f.Data)) continue;
            try
            {
                var img = VitaSharp.Core.Shell.Gim.Decode(f.Data);
                using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(img.Width, img.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
                System.Runtime.InteropServices.Marshal.Copy(img.Pixels, 0, bmp.GetPixels(), img.Pixels.Length);
                using var png = File.Create(Path.Combine(extractDir, name + ".png"));
                bmp.Encode(png, SkiaSharp.SKEncodedImageFormat.Png, 100);
            }
            catch (InvalidDataException e) { Console.WriteLine($"  {name}: {e.Message}"); }
        }
        Console.WriteLine($"extracted {n} file(s) to {extractDir}");
        return 0;
    }

    static void PrintUsage()
    {
        Console.WriteLine("VitaSharp - PS Vita emulator");
        Console.WriteLine("  (no arguments)                        open the game library window");
        Console.WriteLine("  install <game.pkg> <work.bin|.rif>   install a package into the virtual ux0");
        Console.WriteLine("  install <game.vpk|.zip> [--license=<work.bin|.rif>] [--force]");
        Console.WriteLine("                                       install a VPK/content zip (app, patch or add-on, by param.sfo category)");
        Console.WriteLine("  install-firmware <PSVUPDAT.PUP>       install the official system software (os0/vs0/sa0/pd0) into the data folder");
        Console.WriteLine("  install-firmware <folder>|--vita3k   copy a firmware already installed elsewhere (e.g. Vita3K's)");
        Console.WriteLine("  install-firmware --pd0[=<folder>]     copy only pd0 (factory data: the home screen music), from Vita3K by default");
        Console.WriteLine("  firmware-info                        show the installed firmware version");
        Console.WriteLine("  firmware-verify [other fs root]      check the key modules decrypt; compare files with another installation");
        Console.WriteLine("  import-trophies <folder>|--vita3k    import a PS Vita's ur0:user/00/trophy (decrypted copy) or Vita3K's trophies");
        Console.WriteLine("  run <TITLEID|folder> [options]       boot a title (a system app such as NPXS10008 boots the shell, which opens it; --no-shell: alone; a vs0: folder with --self=x.self)");
        Console.WriteLine("  run options: --launch-app=<TITLEID> | --launch-uri=<uri> [--launch-flags=N] [--launch-after=<frames>]  with boot-shell: once the shell is up, ask it to start the app (flags 0x20000) / open the URI (as an application asks it)");
        Console.WriteLine("  boot-shell [options]                 boot the installed firmware's shell (vs0:vsh/shell/shell.self)");
        Console.WriteLine("  registry [prefix] [path=value]       list / set the system registry (firmware template + written values)");
        Console.WriteLine("  run <game.vpk> [--reinstall] [options]  install the VPK if needed, then boot it");
        Console.WriteLine("  --data=<folder>                      use another data folder (default: " + DataPaths.DefaultRoot() + ")");
        Console.WriteLine("  test <scenario.txt> [options]        run an automated scenario (frames, contact sheet, report); run options given here win over the scenario's args=");
        ReplayGpu.PrintUsage();
        Console.WriteLine("  run options: --gpu-dump-at=F1:F2[:file]  record frames F1..F2 as a GPU dump (Ctrl+F8 in the window starts/stops one)");
        Console.WriteLine("  run options: --capture-preroll[=S] --capture-at=A:B  Gravar tudo (F8): keep the last S s / record guest frames A..B");
        Console.WriteLine("  rerun <capture folder> [--from=preroll|start] [--window] [--rerun-gpu-dump]  replay a Gravar tudo capture's input from its save state");
        Console.WriteLine("  run options: --nav=<file> [--nav-live] [--nav-out=<dir>]  screen-driven input script (NavScript: tap/press/stable/shot/quit)");
        Console.WriteLine("  hle-implemented [--vitasdk=<tsv>]   list every NID the HLE implements (docs/hle/data/vitasharp-implemented.tsv)");
        Console.WriteLine("  audio-devices [--audio-device=<name>]  list the audio outputs and show the one a game opens");
        Console.WriteLine("  hle-coverage [--out=<tsv>] [--vitasdk=<tsv>] [--status=<repo>]  HLE coverage of every function the installed firmware exports (games/system/general)");
        Console.WriteLine("  imports <TITLEID|folder> [x.self]   list the system functions a title (or one of its executables) needs");
        Console.WriteLine("        [--names=SceNp,SceNet]          ...and every imported function name of the libraries with these prefixes");
        Console.WriteLine("  self2elf <in> <out> [license]        decrypt a SELF");
        Console.WriteLine("  rco-dump <file.rco> [--extract=<dir>]  print a system RCO resource tree; write out its textures, sounds and other files");
        Console.WriteLine("  camera-test [device] [out-prefix]    grab one webcam frame (SceCamera host path) as PNG");
        Console.WriteLine("  pad-test [--seconds=N] [--virtual]   list controllers (SDL2) and print their mapped Vita input");
        Console.WriteLine("  version                              print the version and commit of this build");
        Console.WriteLine("  update [--channel=builds|releases] [--feed=URL|folder] [--apply]  check the GitHub releases for a newer emulator; --apply installs it");
        Console.WriteLine("  ui-snapshot <out-dir>               render Settings > Controles driven by a simulated DualSense (use a scratch --data)");
        Console.WriteLine("  ui-render <out-dir> [--title=ID] [--launch=ID|shell] [--install-pkg=P --license=zRIF]  render every launcher page to PNGs without a window (use a scratch --data)");
        Console.WriteLine("  toast-preview <out.png> [TITLEID] [--sound=<out.wav>]  draw the trophy notification; write the trophy sound");
        Console.WriteLine("  ime-preview <out.png> [title] [text] [--max=N] [--multiline] [--password]  draw the IME text entry overlay");
        Console.WriteLine("  msg-preview <out.png> [message] [--buttons=A,B] [--selected=N] [--progress=N]  draw the message dialog overlay");
        Console.WriteLine("  run options: --camera=<name|index|none|picture.png/.jpg> source of both Vita cameras");
        Console.WriteLine("               --camera-front=<...> / --camera-back=<...> one camera (default: front = first webcam,");
        Console.WriteLine("               back = the next other webcam, else the same one)");
        Console.WriteLine("               --ime-text=<text>  answer every IME dialog / in-game keyboard with this text and Enter, no UI");
        Console.WriteLine("               --dialogs=auto     answer message / save data dialogs at once (no panel over the game)");
        Console.WriteLine("               --overlay-dump=<dir>  also write every panel drawn over the game (dialogs, save state picker) as PNG");
        Console.WriteLine("               --start-paused  pause the emulation right after the boot (P, or the key set in Controles, resumes)");
        Console.WriteLine("               --trophy-sound=on|off  chime with the trophy notification in the window (default on)");
        Console.WriteLine("               --video-decoder=auto|sw|hw|d3d11va|vaapi|videotoolbox  H.264 decoding (default auto: software; hw: the platform's hardware decoder)");
        Console.WriteLine("               --shaderc-opt=zero|size|perf  shader optimisation (default zero: fast compiles, less stutter)");
        Console.WriteLine("               --async-shaders=on|off  compile new shaders and pipelines on worker threads (default on)");
        Console.WriteLine("               --async-pipelines=on|off  background pipelines only (default on), --shader-workers=N (default auto)");
        Console.WriteLine("               --host-prio=on|off  guest threads and GPU worker above normal host priority (default on)");
        Console.WriteLine("               --gpu-profile=adreno|mali|desktop  emulate a mobile GPU class: its missing features, limits and driver quirks");
        Console.WriteLine("               --res-scale=0.75|0.5|2|3|4  internal resolution: below native (faster on GPU-bound games, softer) or 2x-4x (sharper, costs GPU)");
        Console.WriteLine("               --env=KEY=VALUE  set an environment switch for this run (repeatable; e.g. --env=VITASHARP_RES_SCALE=2)");
        Console.WriteLine("               --msaa=native|ss|off  MSAA scenes: real multisampling (default), 2x supersampling or off (F3 cycles them in game)");
        Console.WriteLine("               --frame-rate=native|30|60|unlimited  vblanks per flip: the game's own (default), fixed 30/60, or no display waits");
        Console.WriteLine("               --psn=on|off  emulated PSN account signed in (default off: no network, NP signed out)");
        Console.WriteLine("               --psn-id=<online id>  online ID of the emulated account (default " + VitaSharp.Core.Hle.PsnAccount.DefaultOnlineId + ")");
        Console.WriteLine("               --psn-local=on|off  local PSN services: TUS/Cross-Save, rankings, tickets, matching in <data>/np");
        Console.WriteLine("                             (default on; off = every PSN server request fails as unreachable)");
        Console.WriteLine("               --net=off|real  real internet for game sockets, DNS and HTTP (default off; Sony's servers stay blocked)");
        Console.WriteLine("               --net-allow=host,...  with --net=real, only these hosts/addresses are reachable (localhost = loopback)");
        Console.WriteLine("               --rpcn=host[:port] --rpcn-user=<name> [--rpcn-token=]  Score/TUS on an RPCN server (needs --net=real;");
        Console.WriteLine("                             password in " + EnvVars.RpcnPassword + ")");
        Console.WriteLine("               --lle-modules=libsas,libatrac  load these from the installed firmware instead of HLE");
        Console.WriteLine("               --hle-modules=json,...    keep these on HLE even when the game or the firmware has the module");
    }
}
