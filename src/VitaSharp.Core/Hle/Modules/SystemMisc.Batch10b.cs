namespace VitaSharp.Core.Hle.Modules;

public sealed partial class SystemMisc
{
    /// <summary>sceSysmoduleLoadModule / sceSysmoduleUnloadModule for HLE libraries that load a sysmodule themselves
    /// (libmtphttp_wrapper loads HTTP or HTTPS before handing out their functions).</summary>
    internal int LoadSysmoduleFor(uint id) => sceSysmoduleLoadModule(id);
    internal int UnloadSysmoduleFor(uint id) => sceSysmoduleUnloadModule(id);
}
