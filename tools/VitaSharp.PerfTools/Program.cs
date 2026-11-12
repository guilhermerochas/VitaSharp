using VitaSharp.PerfTools;
using VitaSharp.PerfTools.Commands;

// vitasharp-perf <command> [arguments]: one IPerfCommand per file in Commands/, registered here.
IPerfCommand[] commands = [new MaliCostCommand(), new FlickerCommand(), new AudioCheckCommand(), new BenchCommand(), new CaptureCommand()];


if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("vitasharp-perf <command> [arguments]   VitaSharp performance tools (not shipped with the emulator)");
    foreach (IPerfCommand perfCommand in commands)
    {
        Console.WriteLine($"  {perfCommand.Name,-12} {perfCommand.Summary}");
    }

    Console.WriteLine("vitasharp-perf <command> --help        the command's options");
    return 0;
}
IPerfCommand? cmd = commands.FirstOrDefault(c => c.Name == args[0]);
if (cmd == null)
{
    Console.Error.WriteLine($"unknown command '{args[0]}' (vitasharp-perf --help)");
    return 1;
}
var a = new PerfArgs(args[1..]);
if (a.Has("--help") || a.Has("-h"))
{
    cmd.PrintUsage();
    return 0;
}
try
{
    return cmd.Run(a);
}
catch (PerfArgsException e)
{
    Console.Error.WriteLine(e.Message);
    cmd.PrintUsage();
    return 1;
}
