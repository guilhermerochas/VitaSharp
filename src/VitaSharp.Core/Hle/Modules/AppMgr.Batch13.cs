using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceAppMgr syscalls of HLE batch 13, read in the SceAppMgr module of bootimage.skprx 3.74.</summary>
public sealed partial class AppMgr
{
    /// <summary>sceAppMgrDump(): the kernel prints every application slot that holds an application (the debug
    /// console's table) and answers 0; here the table goes to the log.</summary>
    [Hle("sceAppMgrDump")]
    int sceAppMgrDump()
    {
        foreach (var p in Emu.Vita.Processes)
            Log.Info($"[appmgr] app 0x{p.AppId:X} {p.Describe()}{(p.IsShell ? " shell" : "")}{(p.IsSystemProgram ? " system" : "")}");
        return 0;
    }

    /// <summary>sceAppMgrQuitForNonSuspendableApp(): a non-game program asks to end its own application when that
    /// application was started as non-suspendable (a flag of its slot, bit 1 of +0x13C); a game program is refused
    /// (0x8080201F), an application without the flag gets 0x8080201C. The emulator starts no application with that
    /// flag, so a system program gets 0x8080201C.</summary>
    [Hle("sceAppMgrQuitForNonSuspendableApp")]
    int sceAppMgrQuitForNonSuspendableApp() => !Emu.IsSystemProgram ? unchecked((int)0x8080201F) : unchecked((int)0x8080201C);
}
