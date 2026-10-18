using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Package;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop;

public static class Program
{
    public static string DataRoot => Environment.GetEnvironmentVariable("VITASHARP_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VitaSharp");

    public static string FsRoot => Path.Combine(DataRoot, "fs");
    public static string Ux0 => Path.Combine(FsRoot, "ux0");

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "gui") return Launcher.LauncherApp.Run(args);

        switch (args[0])
        {
            case "install" when args.Length >= 3:
            {
                var t = PkgInstaller.Install(args[1], args[2], Ux0, Console.WriteLine);
                Console.WriteLine($"Installed {t.TitleId} \"{t.Title}\" ({t.Type}) -> {t.Path}");
                return 0;
            }
            case "self2elf" when args.Length >= 3:
            {
                byte[]? klic = args.Length >= 4 ? PkgInstaller.ReadKlicensee(args[3]) : null;
                var elf = VitaSharp.Core.Loader.Self.SelfDecryptor.ToElf(File.ReadAllBytes(args[1]), klic);
                File.WriteAllBytes(args[2], elf);
                Console.WriteLine($"Wrote {elf.Length} bytes");
                return 0;
            }
            case "imports" when args.Length >= 2:
                return ListImports(args[1]);
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
                return TestScenario.Run(args[1]);
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

    static int ListImports(string titleArg)
    {
        Log.Level = LogLevel.Warn;
        using var emu = new Emulator(new EmulatorOptions { FsRoot = FsRoot, EnableJit = false });
        emu.LoadTitle(ResolveApp(titleArg));
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
        Console.WriteLine("  run <TITLEID|folder> [options]       boot a title");
        Console.WriteLine("  test <scenario.txt>                  run an automated scenario (frames, contact sheet, report)");
        Console.WriteLine("  imports <TITLEID|folder>             list the system functions a title needs");
        Console.WriteLine("  self2elf <in> <out> [license]        decrypt a SELF");
    }
}
