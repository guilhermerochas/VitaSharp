namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceAppUtilPsm (apputil.suprx, FW 3.74): the PSM runtime's (libpsm) entry to appmgr's PSM safe memory.</summary>
public sealed partial class AppUtil
{
    const int ERR_NO_PERMISSION = unchecked((int)0x80100605);

    AppMgr AppMgrModule => Emu.Hle.Modules.OfType<AppMgr>().First();

    /// <summary>
    /// (const char *psmTitleId): sceAppMgrInitSafeMemoryById for the PSM runtime, after sceAppUtilInit (else
    /// 0x80100601). apputil.suprx turns the kernel's answers into its own: a NULL or malformed name (0x80801001,
    /// 0x80800001) is 0x80100600, "already named" (0x8080004A) 0x80100603, "not a PSM runtime" (0x80800009)
    /// 0x80100605; the module also prints its "SceAppUtil Parameter Error" line for the first two. Anything else
    /// (0 included) passes through.
    /// </summary>
    [Hle("NID_DEB51A38", Nid = 0xDEB51A38, Library = "SceAppUtilPsm")]
    internal int PsmInitSafeMemory(uint psmTitleId)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        int r = AppMgrModule._sceAppMgrInitSafeMemoryByIdThroughEntry(psmTitleId, 0);
        return r switch
        {
            unchecked((int)0x80801001) or unchecked((int)0x80800001) => ERR_PARAMETER,
            unchecked((int)0x8080004A) => ERR_BUSY,
            unchecked((int)0x80800009) => ERR_NO_PERMISSION,
            _ => r,
        };
    }
}
