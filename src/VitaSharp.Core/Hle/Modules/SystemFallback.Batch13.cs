using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// bgapputil.suprx 3.74 (SceBgAppUtil): sceBgAppUtilStartBgApp(int mode) starts the application's background
/// application (sce_sys's bgapp) through the shell's service, the module's own sequence: mode must be 0 (else
/// 0x80106501); the flag word and bit of shell event 5 (sceShellSvc_A0B067AC), the asynchronous request 0x180000 on
/// the service object (vtable +0x1C: method, no input, the request handle out, the event to signal), then
/// sceShellSvc_8D4F9F2E(5, 0) and the request's completion (vtable +0x24: 0x180001 with the handle, the result out);
/// the result is the server's. The calls go to the process's libshellsvc; without it (an application started without
/// the system software) there is no shell to start the background application: 0x80106502, the code after the module's
/// invalid-argument one (the firmware module always has the shell, so it has no code of its own for this).
/// </summary>
public sealed partial class SystemFallback
{
    const int BGAPP_INVALID = unchecked((int)0x80106501), BGAPP_NO_SHELL = unchecked((int)0x80106502);

    [Hle("sceBgAppUtilStartBgApp", Library = "SceBgAppUtil")]
    unsafe int BgAppUtilStartBgApp(int mode)
    {
        if (mode != 0) return BGAPP_INVALID;
        var t = GuestThread.Current;
        if (t == null || !Emu.Linker.TryGetFunctionExport(ShellSvcIpc.NidGetSvcObj, out uint getObj)
            || !Emu.Linker.TryGetFunctionExport(0xA0B067AC, out uint eventBit) || !Emu.Linker.TryGetFunctionExport(0x8D4F9F2E, out uint eventArm))
            return BGAPP_NO_SHELL;
        uint sp = t.Core.State->R[13];
        uint frame = (sp - 0x80) & ~7u, ev = frame, handle = frame + 8, result = frame + 12;
        GuestMemory.Fill(frame, 0, 16);
        int r = (int)t.CallGuestOnStack(eventBit, frame, ev, 5);
        if (r != 0) return r;
        uint obj = t.CallGuestOnStack(getObj, frame);
        if (obj == 0) return BGAPP_NO_SHELL;
        r = (int)t.CallGuestOnStack(GuestMemory.R32(GuestMemory.R32(obj) + 0x1C), frame, obj, 0x180000, 0, 0, handle, ev);
        if (r != 0) return r;
        r = (int)t.CallGuestOnStack(eventArm, frame, 5, 0);
        if (r != 0) return r;
        obj = t.CallGuestOnStack(getObj, frame);
        r = (int)t.CallGuestOnStack(GuestMemory.R32(GuestMemory.R32(obj) + 0x24), frame, obj, 0x180001, GuestMemory.R32(handle), result, 0, 0);
        return r != 0 ? r : (int)GuestMemory.R32(result);
    }
}
