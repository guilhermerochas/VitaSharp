using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceSblACMgr (FW 3.74 os0:kd/acmgr.skprx, read in the module, study only): the access control of the kernel. Every
// query reads the process's SELF auth info (ksceSysrootGetSelfAuthInfo: program authority ID at +0, capability bitmap
// at +0x10, attribute bitmap at +0x30, bits numbered from the most significant bit of each byte) and answers from it:
// a capability bit, the authority ID of a known system program (the shell 0x2800000000000001, the PSM runtime, the web
// browser...), the mount table (the 21 devices and their default modes) and the file attribute conversions the file
// systems ask for (exFAT, PFS). Each export is in SceSblACMgrForKernel and, most of them, again in
// SceSblACMgrForDriver at the same address. The auth info comes from what the emulator knows of the process
// (ProgramAccess: its authority ID and its capability, or the class of what it is); the attribute bitmap is not known
// and is empty (its bits only take rights away: none is taken). The checks that need a QA or manufacturing token (the
// sysroot handlers +0x36C and +0x370 of SceSblPostSsMgr) answer as on a retail unit without one.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblACMgr: the process (its auth info) does not exist.</summary>
    const int ACMGR_ERR_NO_PROCESS = unchecked((int)0x800F0903);

    /// <summary>SceSblACMgr: an invalid argument (NULL, a type it does not know).</summary>
    const int ACMGR_ERR_INVALID = unchecked((int)0x800F0916);

    /// <summary>SceSblACMgr: no entry (mount name, mode) matches.</summary>
    const int ACMGR_ERR_NOT_FOUND = unchecked((int)0x800F0902);

    /// <summary>SceSblACMgr: a NULL path or output to the media type query.</summary>
    const int ACMGR_ERR_MEDIA_INVALID = unchecked((int)0x800F090E);

    /// <summary>SceSblACMgr: the kind of attribute does not support the operation (types 6-8).</summary>
    const int ACMGR_ERR_UNSUPPORTED_TYPE = unchecked((int)0x800F0925);

    /// <summary>SceSblACMgr: the caller's access level does not allow an exFAT attribute.</summary>
    const int ACMGR_ERR_LEVEL = unchecked((int)0x800F0928);

    /// <summary>SceSblACMgr: a PFS attribute that does not grant the access asked for.</summary>
    const int ACMGR_ERR_PFS_ACCESS = unchecked((int)0x800F090D);

    /// <summary>The kernel's generic errors the module answers: permission denied, an invalid mode, not found.</summary>
    const int ACMGR_ERR_PERMISSION = unchecked((int)0x8001000D);
    const int ACMGR_ERR_MODE = unchecked((int)0x80010016);
    const int ACMGR_ERR_MODE_NOT_FOUND = unchecked((int)0x80010002);

    /// <summary>The kernel process's program authority ID (its own auth info).</summary>
    const ulong KernelAuthorityId = 0x2808000000000001UL;

    /// <summary>The access levels: the kernel, a system program, a game, a non-game user program.</summary>
    const uint LevelKernel = 0x80, LevelSystem = 0x40, LevelGame = 0x20, LevelUser = 0x10;

    /// <summary>The auth info of a process as SceSblACMgr reads it.</summary>
    readonly struct AcAuthInfo(ulong authorityId, ProgramAccess access, uint levelWord)
    {
        public ulong AuthorityId { get; } = authorityId;

        public uint High => (uint)(AuthorityId >> 32);

        public uint Low => (uint)AuthorityId;

        /// <summary>The first word of the capability field, read as the access level.</summary>
        public uint LevelWord { get; } = levelWord;

        /// <summary>Capability bit <paramref name="bit"/> (the module indexes the auth info from +0x10, so bits past
        /// the capability field read the attribute field, which is empty).</summary>
        public bool Capability(uint bit) => bit < ProgramAccess.CapabilitySize * 8 && access.HasCapabilityBit((int)bit);
    }

    /// <summary>The auth info of process <paramref name="pid"/> (0 the caller's), or false when there is none.</summary>
    bool AcAuth(int pid, out AcAuthInfo info)
    {
        if (pid == KernelSpace.KernelPid)
        {
            info = new AcAuthInfo(KernelAuthorityId, SysmemKernel.KernelAccess, LevelKernel);
            return true;
        }

        if (KernelSpace.ProcessOf(Emu, pid) is not { } process)
        {
            info = default;
            return false;
        }

        ProgramAccess access = process.Access;
        info = new AcAuthInfo(access.AuthorityId, access, access.LevelWord);
        return true;
    }

    /// <summary>The access level of the calling thread (ksceKernelSysrootGetThreadAccessLevel): a kernel thread 0x80;
    /// a thread of a process, the level of its program.</summary>
    uint ThreadAccessLevel()
    {
        if (GuestThread.Current is not { } thread || thread.KernelMode)
        {
            return LevelKernel;
        }

        return Emu.Access.LevelWord & 0xFF;
    }

    static bool IsKernelLevel(uint level) => level is LevelKernel or LevelSystem;

    static bool IsUserLevel(uint level) => level is LevelGame or LevelUser;

    /// <summary>The program-authority prefix (the top 12 bits) of a game or PSM application and of a fake SELF.</summary>
    const uint AuthorityApplication = 0x21000000, AuthorityFself = 0x2F000000, AuthorityPrefixMask = 0xFFF00000;

    /// <summary>Whether bytes of the guest string at <paramref name="address"/>, from <paramref name="offset"/>,
    /// start with <paramref name="prefix"/>.</summary>
    static bool GuestStartsWith(uint address, int offset, ReadOnlySpan<char> prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
        {
            if (GuestMemory.Read<byte>(address + (uint)(offset + i)) != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ capability bits

    /// <summary>ksceSblACMgrHasCapability (pid, bit): bit <paramref name="bit"/> of the process's capability; 0
    /// without the process.</summary>
    [Hle("ksceSblACMgrHasCapability2", Nid = 0x9EDAF856, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrHasCapability", Nid = 0xC2D1F2FC, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrHasCapability(int pid, uint bit) => AcCapability(pid, bit) ? 1 : 0;

    bool AcCapability(int pid, uint bit) => AcAuth(pid, out AcAuthInfo info) && info.Capability(bit);

    /// <summary>Attribute bit <paramref name="bit"/> of the process (the attribute field at +0x30); 0 without it.</summary>
    bool AcAttribute(int pid, uint bit) => false;

    /// <summary>(pid, bit): 0 when the process has capability bit 1 or 0x85 (the only two it accepts), 0x800F0902
    /// when it does not.</summary>
    [Hle("NID_8241AB5C", Nid = 0x8241AB5C, Library = "SceSblACMgrForKernel")]
    int SblAcMgrRequireCapability(int pid, uint bit)
    {
        if (bit != 1 && bit != 0x85)
        {
            return ACMGR_ERR_INVALID;
        }

        return AcCapability(pid, bit) ? 0 : ACMGR_ERR_NOT_FOUND;
    }

    /// <summary>ksceSblACMgrIsRootProgram (pid): capability bit 0.</summary>
    [Hle("ksceSblACMgrIsRootProgram2", Nid = 0x31C23B66, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsRootProgram", Nid = 0x0948F41C, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsRootProgram(int pid) => AcCapability(pid, 0) ? 1 : 0;

    /// <summary>ksceSblACMgrIsSystemProgram2 (pid): capability bit 1 (SceSblACMgrForDriver's twin is in
    /// <see cref="ProcessmgrKernel"/>).</summary>
    [Hle("ksceSblACMgrIsSystemProgram2", Nid = 0x930CD037, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    int ksceSblACMgrIsSystemProgram2(int pid) => AcCapability(pid, 1) ? 1 : 0;

    /// <summary>ksceSblACMgrIsNonGameOrGameProgram (pid): capability bit 2 or 3.</summary>
    [Hle("ksceSblACMgrIsNonGameOrGameProgram2", Nid = 0x570D6AD3, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsNonGameOrGameProgram", Nid = 0x8A54DF3A, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsNonGameOrGameProgram(int pid) => AcCapability(pid, 2) || AcCapability(pid, 3) ? 1 : 0;

    /// <summary>ksceSblACMgrIsNonGameProgram (pid): capability bit 3.</summary>
    [Hle("ksceSblACMgrIsNonGameProgram2", Nid = 0x3F99279F, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsNonGameProgram", Nid = 0x6C5AB07F, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsNonGameProgram(int pid) => AcCapability(pid, 3) ? 1 : 0;

    /// <summary>(pid): capability bit 0x1F.</summary>
    [Hle("NID_48F4D5EE", Nid = 0x48F4D5EE, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_84604EED", Nid = 0x84604EED, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrCapability1F(int pid) => AcCapability(pid, 0x1F) ? 1 : 0;

    /// <summary>(pid): capability bit 0x20.</summary>
    [Hle("NID_0E489631", Nid = 0x0E489631, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_AD717E7A", Nid = 0xAD717E7A, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrCapability20(int pid) => AcCapability(pid, 0x20) ? 1 : 0;

    /// <summary>(pid): capability bit 0x80 (the Marlin network: SceMarlinHci, SceAppMgr, SceVshBridge).</summary>
    [Hle("NID_04C0ED3F", Nid = 0x04C0ED3F, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_F5AE24AC", Nid = 0xF5AE24AC, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrCapability80(int pid) => AcCapability(pid, 0x80) ? 1 : 0;

    /// <summary>(pid): capability bit 0x81 (MagicGate: SceMgVideo, SceUsbstorMg).</summary>
    [Hle("NID_02422F1F", Nid = 0x02422F1F, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_A27E47A7", Nid = 0xA27E47A7, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrCapability81(int pid) => AcCapability(pid, 0x81) ? 1 : 0;

    /// <summary>(pid): capability bit 0x87.</summary>
    [Hle("NID_5E6BA11C", Nid = 0x5E6BA11C, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_A92CD636", Nid = 0xA92CD636, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrCapability87(int pid) => AcCapability(pid, 0x87) ? 1 : 0;

    /// <summary>(pid): attribute bit 0x81 (no attribute bit is set: 0).</summary>
    [Hle("NID_A50FDA27", Nid = 0xA50FDA27, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_B836CF13", Nid = 0xB836CF13, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrAttribute81(int pid) => AcAttribute(pid, 0x81) ? 1 : 0;

    /// <summary>(pid): capability bit 0x22, or a fake SELF on a unit with the QA flag (retail: none).</summary>
    [Hle("NID_30575458", Nid = 0x30575458, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    int SblAcMgrCapability22(int pid) => AcCapability(pid, 0x22) ? 1 : 0;

    /// <summary>ksceSblACMgrIsAllowUsbSerial (pid): capability bit 1 or 0x83 (a fake SELF also with the QA flag,
    /// which a retail unit does not have).</summary>
    [Hle("ksceSblACMgrIsAllowUsbSerial2", Nid = 0xDC49E160, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsAllowedUsbSerial", Nid = 0x062CAEB2, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsAllowedUsbSerial(int pid) => AcCapability(pid, 1) || AcCapability(pid, 0x83) ? 1 : 0;

    /// <summary>ksceSblACMgrIsAllowedVirtualMachine (pid): capability bit 0x84 (or a fake SELF with the QA flag).</summary>
    [Hle("ksceSblACMgrIsAllowedVirtualMachine2", Nid = 0x96403142, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsAllowedVirtualMachine", Nid = 0x2A29453C, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsAllowedVirtualMachine(int pid) => AcCapability(pid, 0x84) ? 1 : 0;

    /// <summary>(pid): capability bit 0x86 (or a fake SELF with the manufacturing flag).</summary>
    [Hle("NID_410357AF", Nid = 0x410357AF, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_AE1AF154", Nid = 0xAE1AF154, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrCapability86(int pid) => AcCapability(pid, 0x86) ? 1 : 0;

    /// <summary>ksceSblACMgrIsDevelopmentMode (): dip switch 0x9F (a development kit's; a retail unit's is clear).</summary>
    [Hle("ksceSblACMgrIsDevelopmentMode2", Nid = 0xBBA13D9C, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsDevelopmentMode", Nid = 0xE87D1777, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsDevelopmentMode() => ksceKernelCheckDipsw(0x9F);

    /// <summary>Two exports that answer 0.</summary>
    [Hle("NID_FBA1A256", Nid = 0xFBA1A256, Library = "SceSblACMgrForKernel")]
    [Hle("NID_1948E9DB", Nid = 0x1948E9DB, Library = "SceSblACMgrForKernel")]
    int SblAcMgrNoOperation() => 0;

    // ------------------------------------------------------------------ the program authority ID

    /// <summary>ksceSblACMgrGetProcessProgramAuthId (pid, SceUInt64 *authId): the process's program authority ID;
    /// NULL or no process 0x800F0916.</summary>
    [Hle("ksceSblACMgrGetProcessProgramAuthId2", Nid = 0x7C2AF978, Library = "SceSblACMgrForKernel")]
    [Hle("ksceSblACMgrGetProcessProgramAuthId", Nid = 0x96AF69BD, Library = "SceSblACMgrForDriver")]
    int ksceSblACMgrGetProcessProgramAuthId(int pid, uint authId)
    {
        if (authId == 0 || !AcAuth(pid, out AcAuthInfo info))
        {
            return ACMGR_ERR_INVALID;
        }

        GuestMemory.Write(authId, info.AuthorityId);
        return 0;
    }

    /// <summary>Whether process <paramref name="pid"/> exists and its authority ID passes <paramref name="test"/>.</summary>
    bool AcAuthority(int pid, Func<ulong, bool> test) => AcAuth(pid, out AcAuthInfo info) && test(info.AuthorityId);

    static bool IsGameAuthority(ulong id) =>
        ((uint)(id >> 32) & AuthorityPrefixMask) == AuthorityApplication || id == 0x2F00000000000001UL;

    static bool IsFselfAuthority(ulong id) => ((uint)(id >> 32) & AuthorityPrefixMask) == AuthorityFself;

    /// <summary>The authority IDs of the system's own applications (NPXS, 0x210000101CD20007..09).</summary>
    static bool IsSieAppAuthority(ulong id) => id - 0x210000101CD20007UL < 3;

    /// <summary>The PSM runtime (0x2800C0101CD2000B) and the PSM programs (0x210000101CD20007..0A).</summary>
    static bool IsPsmProgramAuthority(ulong id) => id == ProgramAccess.PsmRuntimeAuthorityId || id - 0x210000101CD20007UL < 4;

    /// <summary>The programs that are not sandboxed: the PSP emulator (0x2800000000007009, 0x13) and the system
    /// services 0x10, 0x2D, 0x2E, 0x22, 0x39 and the shell (1).</summary>
    static bool IsNotSandboxedAuthority(ulong id) =>
        id is 0x2800000000007009UL or 0x2800000000000013UL or 0x2800000000000010UL or 0x280000000000002DUL
            or 0x280000000000002EUL or 0x2800000000000022UL or 0x2800000000000039UL or 0x2800000000000001UL;

    /// <summary>ksceSblACMgrIsGameProgram (pid): an application authority (0x210...) or the default fake SELF one.</summary>
    [Hle("ksceSblACMgrIsGameProgram2", Nid = 0x05FDC646, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsGameProgram", Nid = 0x1298C647, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsGameProgram(int pid) => AcAuthority(pid, IsGameAuthority) ? 1 : 0;

    /// <summary>ksceSblACMgrIsFself (pid): a fake SELF authority (0x2F0...).</summary>
    [Hle("ksceSblACMgrIsFself2", Nid = 0x5AC59172, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsFself", Nid = 0x426A4E8C, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsFself(int pid) => AcAuthority(pid, IsFselfAuthority) ? 1 : 0;

    /// <summary>ksceSblACMgrIsSceShell (pid): the shell's authority, 0x2800000000000001.</summary>
    [Hle("ksceSblACMgrIsSceShell2", Nid = 0xF9FEF5F0, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsSceShell", Nid = 0x991FDC15, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsSceShell(int pid) => AcAuthority(pid, id => id == 0x2800000000000001UL) ? 1 : 0;

    /// <summary>ksceSblACMgrIsWebCoreOrWebKitProcess (pid): 0x2800000000008003 or 0x2800000000008005.</summary>
    [Hle("ksceSblACMgrIsWebCoreOrWebKitProcess2", Nid = 0xA7C3001D, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsWebCoreOrWebKitProcess", Nid = 0x0139FC20, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsWebCoreOrWebKitProcess(int pid) =>
        AcAuthority(pid, id => id is 0x2800000000008003UL or 0x2800000000008005UL) ? 1 : 0;

    /// <summary>(pid): 0x220000101CD2000C or 0x220000101CD2000D.</summary>
    [Hle("NID_E273ED8C", Nid = 0xE273ED8C, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_0924896F", Nid = 0x0924896F, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrIsAuthority22Cd2000C(int pid) => AcAuthority(pid, id => id - 0x220000101CD2000CUL < 2) ? 1 : 0;

    /// <summary>ksceSblACMgrIsUpdaterUISetupperOrPkgInstallerSpawn (pid): 0x2800800000000000..0F.</summary>
    [Hle("ksceSblACMgrIsUpdaterUISetupperOrPkgInstallerSpawn2", Nid = 0x98B28671, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsUpdaterUISetupperOrPkgInstallerSpawn", Nid = 0x962CD237, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsUpdaterUISetupperOrPkgInstallerSpawn(int pid) =>
        AcAuthority(pid, id => (id & 0xFFFFFFFFFFFFFFF0UL) == 0x2800800000000000UL) ? 1 : 0;

    /// <summary>(pid): an authority 0x2801xxxx........ (SceCtrl, SceHid).</summary>
    [Hle("NID_2E992B02", Nid = 0x2E992B02, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrIsAuthority2801(int pid) => AcAuthority(pid, id => (id >> 48) == 0x2801) ? 1 : 0;

    /// <summary>ksceSblACMgrIsPSMProgram (pid): the PSM runtime or a PSM program (or a fake SELF with the QA flag).</summary>
    [Hle("ksceSblACMgrIsPSMProgram2", Nid = 0xC90A9216, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsPSMProgram", Nid = 0xC98D82EE, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsPSMProgram(int pid) => AcAuthority(pid, IsPsmProgramAuthority) ? 1 : 0;

    /// <summary>ksceSblACMgrIsPSMRuntime (pid): 0x210000101CD2000A or the PSM runtime 0x2800C0101CD2000B.</summary>
    [Hle("ksceSblACMgrIsPSMRuntime2", Nid = 0x966B3738, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsPSMRuntime", Nid = 0x091F7247, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsPSMRuntime(int pid) =>
        AcAuthority(pid, id => id is 0x210000101CD2000AUL or ProgramAccess.PsmRuntimeAuthorityId) ? 1 : 0;

    /// <summary>(pid): 0x210000101CD2000A.</summary>
    [Hle("NID_06BE9F0F", Nid = 0x06BE9F0F, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_5C4BC352", Nid = 0x5C4BC352, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrIsAuthority1Cd2000A(int pid) => AcAuthority(pid, id => id == 0x210000101CD2000AUL) ? 1 : 0;

    /// <summary>ksceSblACMgrIsSIEApp (pid): 0x210000101CD20007..09.</summary>
    [Hle("ksceSblACMgrIsSIEApp2", Nid = 0x3388F595, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsSIEApp", Nid = 0xA67E8E5B, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsSIEApp(int pid) => AcAuthority(pid, IsSieAppAuthority) ? 1 : 0;

    /// <summary>ksceSblACMgrIsMiniSettingsForQA (pid): 0x220000101CC73883, 0x2800000000000010, else capability
    /// bit 0x82.</summary>
    [Hle("ksceSblACMgrIsMiniSettingsForQA2", Nid = 0xAF6F208E, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsMiniSettingsForQA", Nid = 0xD0E11C89, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsMiniSettingsForQA(int pid)
    {
        if (!AcAuth(pid, out AcAuthInfo info))
        {
            return 0;
        }

        if (info.AuthorityId is 0x220000101CC73883UL or 0x2800000000000010UL)
        {
            return 1;
        }

        return info.Capability(0x82) ? 1 : 0;
    }

    /// <summary>ksceSblACMgrIsAllowedExtendedMemory (pid): 0x280000000000001E, 0x220000101CC73883,
    /// 0x2800000000000010, 0x2800000000000031, else capability bit 0x82.</summary>
    [Hle("ksceSblACMgrIsAllowedExtendedMemory2", Nid = 0x384D20FD, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsAllowedExtendedMemory", Nid = 0x4DB7F512, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsAllowedExtendedMemory(int pid)
    {
        if (!AcAuth(pid, out AcAuthInfo info))
        {
            return 0;
        }

        if (info.AuthorityId is 0x280000000000001EUL or 0x220000101CC73883UL or 0x2800000000000010UL
            or 0x2800000000000031UL)
        {
            return 1;
        }

        return info.Capability(0x82) ? 1 : 0;
    }

    /// <summary>ksceSblACMgrIsNotSandboxed (pid): the PSP emulator, the shell and the system services that run
    /// outside the sandbox.</summary>
    [Hle("ksceSblACMgrIsNotSandboxed2", Nid = 0xF5AD56E4, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsNotSandboxed", Nid = 0x0B6E6CD7, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsNotSandboxed(int pid) => AcAuthority(pid, IsNotSandboxedAuthority) ? 1 : 0;

    /// <summary>(pid): a game program or one not sandboxed.</summary>
    [Hle("NID_7529E364", Nid = 0x7529E364, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_6D8A88B7", Nid = 0x6D8A88B7, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrIsGameOrNotSandboxed(int pid) =>
        AcAuthority(pid, id => IsGameAuthority(id) || IsNotSandboxedAuthority(id)) ? 1 : 0;

    /// <summary>(pid): capability bit 3, the system's applications, 0x2800000000000044 or 0x280000000000001E.</summary>
    [Hle("NID_4C4B7D6B", Nid = 0x4C4B7D6B, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_456DA7AC", Nid = 0x456DA7AC, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrIsNonGameOrSystemApplication(int pid)
    {
        if (!AcAuth(pid, out AcAuthInfo info))
        {
            return 0;
        }

        bool listed = IsSieAppAuthority(info.AuthorityId)
            || info.AuthorityId is 0x2800000000000044UL or 0x280000000000001EUL;
        return info.Capability(3) || listed ? 1 : 0;
    }

    /// <summary>ksceSblACMgrIsPspEmu (pid): the PSP emulator, 0x2800000000007009 or 0x2800000000000013.</summary>
    [Hle("ksceSblACMgrIsPspEmu2", Nid = 0x47B67F72, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("ksceSblACMgrIsPspEmu", Nid = 0xFD00C72A, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int ksceSblACMgrIsPspEmu(int pid) =>
        AcAuthority(pid, id => id is 0x2800000000007009UL or 0x2800000000000013UL) ? 1 : 0;

    /// <summary>(pid): whether the process may write a core dump: a system program (0x280...) when the QA flag
    /// allows system application debugging, an application when the QA flag or the sysroot allows it, otherwise
    /// unless its attribute bit 0x10 forbids it (no attribute bit is set: 1).</summary>
    [Hle("NID_49509A83", Nid = 0x49509A83, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_4CBD6156", Nid = 0x4CBD6156, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrIsCoredumpAllowed(int pid)
    {
        if (!AcAuth(pid, out _))
        {
            return 0;
        }

        return AcAttribute(pid, 0x10) ? 0 : 1;
    }

    // ------------------------------------------------------------------ paths and devices

    /// <summary>The media types of the paths (prefix, digits, suffix) ksceSblACMgrGetMediaType knows, in its order.</summary>
    static readonly (string Device, string Suffix, uint Type)[] MediaTypes =
    [
        ("sd", ":", 1), ("os", ":", 2), ("vs", ":", 3), ("vd", ":", 4), ("tm", ":", 5), ("ur", ":", 6),
        ("host", ":", 7), ("ud", ":", 0xB), ("ux", ":app", 0x17), ("ux", ":/app", 0x17), ("ux", ":patch", 0x18),
        ("ux", ":/patch", 0x18), ("ux", ":data", 0x19), ("ux", ":/data", 0x19), ("ux", ":user", 0),
        ("ux", ":/user", 0), ("ux", ":", 0xC), ("gro", ":app", 0xD), ("gro", ":/app", 0xD), ("grw", ":patch", 0xE),
        ("grw", ":/patch", 0xE), ("sa", ":", 0xF), ("mfa", ":", 0x10), ("mfb", ":", 0x11), ("lma", ":", 0x12),
        ("lmb", ":", 0x13), ("lmc", ":", 0x14), ("lmd", ":", 0x15), ("pd", ":", 0x16), ("uma", ":", 0x1B),
    ];

    /// <summary>ksceSblACMgrGetMediaType (const char *path, int *type): the media of the path (the device name, any
    /// unit digits, then the rest of the entry); NULL 0x800F090E, a path it does not know 0x800F0903 (the output is
    /// cleared first).</summary>
    [Hle("ksceSblACMgrGetMediaType2", Nid = 0xD442962E, Library = "SceSblACMgrForKernel")]
    [Hle("ksceSblACMgrGetMediaType", Nid = 0x4322F188, Library = "SceSblACMgrForDriver")]
    int ksceSblACMgrGetMediaType(uint path, uint type)
    {
        if (path == 0 || type == 0)
        {
            return ACMGR_ERR_MEDIA_INVALID;
        }

        GuestMemory.W32(type, 0);
        foreach ((string device, string rest, uint media) in MediaTypes)
        {
            if (!GuestStartsWith(path, 0, device))
            {
                continue;
            }

            int offset = device.Length;
            while (GuestMemory.Read<byte>(path + (uint)offset) is >= (byte)'0' and <= (byte)'9')
            {
                offset++;
            }

            if (GuestStartsWith(path, offset, rest))
            {
                GuestMemory.W32(type, media);
                return 0;
            }
        }

        return ACMGR_ERR_NO_PROCESS;
    }

    /// <summary>The devices of 7F294A09: a game reads its answer from its attribute bit 1 (0 or 1 column), on a retail
    /// unit (dip switch 0x9F clear) and on a development kit.</summary>
    static readonly (string Device, uint Retail0, uint Retail1, uint Devkit0, uint Devkit1)[] DeviceAccess =
    [
        ("ux0:", 0, 0, 1, 1), ("sd0:", 0, 0, 1, 1), ("pd0:", 0, 0, 0, 0), ("host0:", 0, 0, 1, 1),
    ];

    /// <summary>(pid, const char *path): whether the process may open the raw device of the path (ux0:, sd0:, pd0:,
    /// host0:): an application or a fake SELF by the table (none on a retail unit), any other program unless its
    /// attribute bit 1 is set.</summary>
    [Hle("NID_7F294A09", Nid = 0x7F294A09, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_5F9AF49C", Nid = 0x5F9AF49C, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrIsDeviceAllowed(int pid, uint path)
    {
        if (!AcAuth(pid, out AcAuthInfo info) || path == 0)
        {
            return 0;
        }

        uint prefix = info.High & AuthorityPrefixMask;
        bool attribute = AcAttribute(pid, 1);
        if (prefix != AuthorityApplication && prefix != AuthorityFself && !attribute)
        {
            return 1;
        }

        int column = attribute ? 1 : 0;
        bool devkit = ksceKernelCheckDipsw(0x9F) != 0;
        foreach ((string device, uint retail0, uint retail1, uint devkit0, uint devkit1) in DeviceAccess)
        {
            if (GuestStartsWith(path, 0, device))
            {
                uint value = devkit ? (column == 0 ? devkit0 : devkit1) : (column == 0 ? retail0 : retail1);
                return (int)value;
            }
        }

        return 0;
    }

    /// <summary>The paths of vs0: the programs whose attribute bit 0x81 restricts them may open (by authority).</summary>
    static readonly (ulong AuthorityId, string[] Paths)[] RestrictedPaths =
    [
        (0x2800000000008003UL, ["vs0:data/external/cert/", "vs0:data/external/webcore/"]),
        (0x2800000000008005UL, ["vs0:data/external/cert/", "vs0:data/external/webcore/"]),
        (ProgramAccess.PsmRuntimeAuthorityId, ["vs0:data/external", "vs0:sys/external"]),
        (0x210000101CD20007UL, ["vs0:data/external", "vs0:sys/external"]),
        (0x210000101CD20008UL, ["vs0:data/external", "vs0:sys/external"]),
        (0x210000101CD2000AUL, ["vs0:data/external", "vs0:sys/external"]),
        (0x210000101CD20009UL, ["vs0:data/external", "vs0:sys/external"]),
        (0x220000101CD2000CUL, ["vs0:data/external/cert/", "vs0:data/external", "vs0:sys/external"]),
        (0x220000101CD2000DUL, ["vs0:data/external/cert/", "vs0:data/external", "vs0:sys/external"]),
    ];

    /// <summary>(pid, const char *path): whether the process may open the path: any path unless its attribute bit
    /// 0x81 restricts it to the vs0: paths of its authority (the device, an optional '/', the path).</summary>
    [Hle("NID_165C3C7A", Nid = 0x165C3C7A, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_E79C7A8D", Nid = 0xE79C7A8D, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrIsPathAllowed(int pid, uint path)
    {
        if (!AcAttribute(pid, 0x81))
        {
            return 1;
        }

        if (!AcAuth(pid, out AcAuthInfo info) || path == 0)
        {
            return 0;
        }

        foreach ((ulong authority, string[] paths) in RestrictedPaths)
        {
            if (authority != info.AuthorityId)
            {
                continue;
            }

            foreach (string allowed in paths)
            {
                const int DeviceLength = 4; // "vs0:"
                if (!GuestStartsWith(path, 0, allowed.AsSpan(0, DeviceLength)))
                {
                    continue;
                }

                int offset = GuestMemory.Read<byte>(path + DeviceLength) == '/' ? DeviceLength + 1 : DeviceLength;
                if (GuestStartsWith(path, offset, allowed.AsSpan(DeviceLength)))
                {
                    return 1;
                }
            }
        }

        return 0;
    }

    /// <summary>The mount points SceSblACMgr knows and the default mode of each (0x1000000: lma0:, a raw device).</summary>
    static readonly (string Mount, uint Mode)[] MountModes =
    [
        ("sd0:", 0x186), ("os0:", 0x106), ("vs0:", 0x106), ("vd0:", 0x106), ("tm0:", 0x106), ("ur0:", 0x106),
        ("ud0:", 0x106), ("ux0:", 0x106), ("gro0:", 0x106), ("grw0:", 0x106), ("sa0:", 0x106), ("mfa0:", 0x106),
        ("mfb0:", 0x106), ("lma0:", 0x1000000), ("lmb0:", 0x106), ("lmc0:", 0x106), ("lmd0:", 0x106),
        ("pd0:", 0x106), ("imc0:", 0x106), ("xmc0:", 0x106), ("uma0:", 0x106),
    ];

    static int MountIndex(uint mount)
    {
        for (int i = 0; i < MountModes.Length; i++)
        {
            if (GuestStartsWith(mount, 0, MountModes[i].Mount))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>(pid, const char *mount, int *mode): the default mode of the mount point; NULL 0x800F0916, a mount
    /// point it does not know 0x800F0902.</summary>
    [Hle("NID_356B9139", Nid = 0x356B9139, Library = "SceSblACMgrForKernel")]
    [Hle("NID_FF7125DE", Nid = 0xFF7125DE, Library = "SceSblACMgrForDriver")]
    int SblAcMgrGetMountMode(int pid, uint mount, uint mode)
    {
        if (mount == 0 || mode == 0)
        {
            return ACMGR_ERR_INVALID;
        }

        int index = MountIndex(mount);
        if (index < 0)
        {
            return ACMGR_ERR_NOT_FOUND;
        }

        GuestMemory.W32(mode, MountModes[index].Mode);
        return 0;
    }

    /// <summary>(pid, const char *mount): whether the process may mount the device: a known mount point and a system
    /// program, a kernel or system thread, a non-game program on ux0:, or the two system services of the table
    /// (0x220000101CC73883 any, 0x220000101CC60019 on lma0:).</summary>
    [Hle("NID_FB2AC5B4", Nid = 0xFB2AC5B4, Library = "SceSblACMgrForKernel", ReturnsValue = true)]
    [Hle("NID_D7AD8471", Nid = 0xD7AD8471, Library = "SceSblACMgrForDriver", ReturnsValue = true)]
    int SblAcMgrIsMountAllowed(int pid, uint mount)
    {
        uint level = ThreadAccessLevel();
        if (!AcAuth(pid, out AcAuthInfo info) || mount == 0)
        {
            return 0;
        }

        int index = MountIndex(mount);
        if (index < 0)
        {
            return 0;
        }

        string name = MountModes[index].Mount;
        if (info.Capability(1) || level is LevelKernel or LevelSystem)
        {
            return 1;
        }

        if (info.Capability(3) && name == "ux0:")
        {
            return 1;
        }

        if (info.AuthorityId == 0x220000101CC73883UL)
        {
            return 1;
        }

        return info.AuthorityId == 0x220000101CC60019UL && name == "lma0:" ? 1 : 0;
    }

    // ------------------------------------------------------------------ file attributes (SceSblACMgrFsAttribute)

    // The file systems describe a file's attribute with { int type; void *value; int size; }: types 1-5 the FAT/exFAT
    // style mode (the low 3 bits rwx-like, in 1 byte for types 1-3, 2 bytes for 4-5), 6 the exFAT/PFS mode word, 7 and
    // 8 the PFS kinds (8: 6 bytes of per-program access nibbles).

    /// <summary>The kernel's modes and the attribute value of each (types 1-5, a kernel or system thread).</summary>
    static readonly (uint Mode, uint Value)[] KernelModeValues =
    [
        (0x10186, 0), (0x186, 0), (0x10106, 1), (0x106, 4), (0x10104, 5), (0x104, 5), (0x10006, 6), (0x6, 6),
        (0x10004, 7), (0x4, 7),
    ];

    /// <summary>A user thread's modes (read-write, read-only) and their values (types 1-5).</summary>
    static readonly (uint Mode, uint Value)[] UserModeValues = [(0x180, 0), (0x100, 1)];

    /// <summary>The mode of an attribute value (types 1-5).</summary>
    static ReadOnlySpan<uint> ValueModes => [0x186, 0x106, 0x186, 0x106, 0x106, 0x104, 0x6, 0x4];

    /// <summary>The access (bit 0 read, bit 1 write, bit 3 execute...) a value grants a user and a kernel thread.</summary>
    static ReadOnlySpan<uint> UserValueAccess => [0xB, 0x9, 0xB, 0x9, 0x1, 0x1, 0x0, 0x0];

    static ReadOnlySpan<uint> KernelValueAccess => [0xB, 0xB, 0xB, 0xB, 0xB, 0x9, 0xB, 0x9];

    /// <summary>The exFAT mode values (type 6) and the access each grants a kernel and a user thread.</summary>
    static readonly (uint Value, uint Kernel, uint User)[] ExfatAccess =
    [
        (0, 0xB, 0xB), (1, 0xB, 0x9), (4, 0xB, 0x1), (5, 0x9, 0x1), (6, 0xB, 0x0), (7, 0x9, 0x0),
    ];

    /// <summary>The exFAT modes (type 6) and their values, a user thread's and a kernel one's.</summary>
    static readonly (uint Mode, uint Value)[] ExfatUserModes = [(0x180, 0), (0x100, 1)];

    static readonly (uint Mode, uint Value)[] ExfatKernelModes =
    [
        (0x186, 0), (0x10106, 1), (0x106, 4), (0x104, 5), (0x6, 6), (0x4, 7),
    ];

    readonly record struct FsAttribute(uint Type, uint Value, uint Size);

    static FsAttribute ReadFsAttribute(uint attribute) =>
        new(GuestMemory.R32(attribute), GuestMemory.R32(attribute + 4), GuestMemory.R32(attribute + 8));

    /// <summary>The attribute's value (1 or 2 bytes by its size, 0 otherwise).</summary>
    static uint FsValue(FsAttribute attribute) => attribute.Size switch
    {
        1 => GuestMemory.Read<byte>(attribute.Value),
        2 => GuestMemory.Read<ushort>(attribute.Value),
        _ => 0,
    };

    /// <summary>Sets the low 3 bits of the value (the rest kept).</summary>
    static void FsSetLowBits(FsAttribute attribute, uint value)
    {
        if (attribute.Size is 1 or 2)
        {
            byte low = GuestMemory.Read<byte>(attribute.Value);
            GuestMemory.Write(attribute.Value, (byte)((value & 7) | (low & 0xF8u)));
        }
    }

    /// <summary>Stores the whole value (1 or 2 bytes).</summary>
    static void FsStore(FsAttribute attribute, uint value)
    {
        if (attribute.Size == 1)
        {
            GuestMemory.Write(attribute.Value, (byte)value);
        }
        else if (attribute.Size == 2)
        {
            GuestMemory.Write(attribute.Value, (ushort)value);
        }
    }

    /// <summary>Sets the low bits; a value with bit 2 (set now or before) needs a kernel or system thread.</summary>
    int FsSetChecked(FsAttribute attribute, uint value)
    {
        uint current = FsValue(attribute) & 7;
        if (((current | value) & 4) != 0 && !IsKernelLevel(ThreadAccessLevel()))
        {
            return ACMGR_ERR_PERMISSION;
        }

        FsSetLowBits(attribute, value);
        return 0;
    }

    /// <summary>Stores an exFAT value; bit 2 needs a program of kernel or system level.</summary>
    static int ExfatStore(AcAuthInfo info, FsAttribute attribute, uint value)
    {
        if (!IsKernelLevel(info.LevelWord) && (value & 4) != 0)
        {
            return ACMGR_ERR_PERMISSION;
        }

        FsStore(attribute, value);
        return 0;
    }

    static bool FindMode((uint Mode, uint Value)[] table, uint mode, out uint value)
    {
        foreach ((uint entry, uint entryValue) in table)
        {
            if (entry == mode)
            {
                value = entryValue;
                return true;
            }
        }

        value = 0;
        return false;
    }

    /// <summary>The auth info and the attribute of the conversions: no process 0x800F0903, a NULL attribute or
    /// value 0x800F0916.</summary>
    int FsBegin(int pid, uint attribute, out AcAuthInfo info, out FsAttribute fs)
    {
        fs = default;
        if (!AcAuth(pid, out info))
        {
            return ACMGR_ERR_NO_PROCESS;
        }

        if (attribute == 0)
        {
            return ACMGR_ERR_INVALID;
        }

        fs = ReadFsAttribute(attribute);
        return fs.Value == 0 ? ACMGR_ERR_INVALID : 0;
    }

    /// <summary>ksceSblACMgrConvertModeToFsAttribute (pid, SceSblACMgrFsAttribute *attribute, int mode): the
    /// attribute of a file opened with the mode, by the thread's level (types 1-5) or the program's (type 6).</summary>
    [Hle("ksceSblACMgrConvertModeToFsAttribute2", Nid = 0x2AE6CF27, Library = "SceSblACMgrForKernel")]
    [Hle("ksceSblACMgrConvertModeToFsAttribute", Nid = 0x6210D745, Library = "SceSblACMgrForDriver")]
    int ksceSblACMgrConvertModeToFsAttribute(int pid, uint attribute, uint mode)
    {
        int begin = FsBegin(pid, attribute, out AcAuthInfo info, out FsAttribute fs);
        if (begin != 0)
        {
            return begin;
        }

        switch (fs.Type)
        {
            case >= 1 and <= 5:
                return ModeToFatAttribute(fs, mode);
            case 6:
                return ModeToExfatAttribute(info, fs, mode);
            case 7:
            case 8:
                return ACMGR_ERR_UNSUPPORTED_TYPE;
            default:
                return ACMGR_ERR_INVALID;
        }
    }

    int ModeToFatAttribute(FsAttribute fs, uint mode)
    {
        if (mode == 0x1000000)
        {
            FsStore(fs, 0xF);
            return 0;
        }

        uint level = ThreadAccessLevel();
        bool found;
        uint value;
        if (IsKernelLevel(level))
        {
            found = FindMode(KernelModeValues, mode & 0x10186, out value);
        }
        else if (IsUserLevel(level))
        {
            found = FindMode(UserModeValues, mode & 0x180, out value);
        }
        else
        {
            return ACMGR_ERR_MODE;
        }

        return found ? FsSetChecked(fs, value) : ACMGR_ERR_MODE;
    }

    static int ModeToExfatAttribute(AcAuthInfo info, FsAttribute fs, uint mode)
    {
        uint level = info.LevelWord;
        if (!IsKernelLevel(level) && !IsUserLevel(level))
        {
            return ACMGR_ERR_LEVEL;
        }

        uint allowed = IsKernelLevel(level) ? 0xFF3FFFFFu : 0xFF0FFFFFu;
        if ((mode & allowed) != mode)
        {
            return ACMGR_ERR_MODE;
        }

        bool found = IsKernelLevel(level)
            ? FindMode(ExfatKernelModes, mode & 0x10186, out uint value)
            : FindMode(ExfatUserModes, mode & 0x180, out value);
        if (!found)
        {
            return ACMGR_ERR_NOT_FOUND;
        }

        if ((mode & 0x100000) != 0)
        {
            value |= 0x4000;
        }

        if ((mode & 0x200000) != 0)
        {
            value |= 0x2000;
        }

        return ExfatStore(info, fs, value);
    }

    /// <summary>(pid, SceSblACMgrFsAttribute *to, const SceSblACMgrFsAttribute *from): copies the value of one
    /// attribute to another (types 1-5 the low bits, type 6 the value).</summary>
    [Hle("NID_CCDBB74D", Nid = 0xCCDBB74D, Library = "SceSblACMgrForKernel")]
    [Hle("NID_B12CEAA8", Nid = 0xB12CEAA8, Library = "SceSblACMgrForDriver")]
    int SblAcMgrCopyFsAttribute(int pid, uint to, uint from)
    {
        int begin = FsBegin(pid, to, out AcAuthInfo info, out FsAttribute target);
        if (begin != 0)
        {
            return begin;
        }

        if (from == 0 || GuestMemory.R32(from + 4) == 0)
        {
            return ACMGR_ERR_INVALID;
        }

        FsAttribute source = ReadFsAttribute(from);
        switch (target.Type)
        {
            case >= 1 and <= 5:
                if (source.Type - 1 >= 5)
                {
                    return ACMGR_ERR_INVALID;
                }

                return FsSetChecked(target, FsValue(source) & 7);
            case 6:
                return ExfatStore(info, target, FsValue(source));
            case 7:
            case 8:
                return ACMGR_ERR_UNSUPPORTED_TYPE;
            default:
                return ACMGR_ERR_INVALID;
        }
    }

    /// <summary>(pid, SceSblACMgrFsAttribute *to, const SceSblACMgrFsAttribute *from, int flag): clears the target
    /// when the source is the raw-device value 0xF and the flag is 0x10000 (types 1-5).</summary>
    [Hle("NID_1B160234", Nid = 0x1B160234, Library = "SceSblACMgrForKernel")]
    [Hle("NID_48CFCEA2", Nid = 0x48CFCEA2, Library = "SceSblACMgrForDriver")]
    int SblAcMgrClearRawFsAttribute(int pid, uint to, uint from, uint flag)
    {
        int begin = FsBegin(pid, to, out _, out FsAttribute target);
        if (begin != 0)
        {
            return begin;
        }

        if (from == 0 || GuestMemory.R32(from + 4) == 0)
        {
            return ACMGR_ERR_INVALID;
        }

        switch (target.Type)
        {
            case >= 1 and <= 5:
                FsAttribute source = ReadFsAttribute(from);
                if (source.Type - 1 >= 5 || flag != 0x10000 || source.Size is not (1 or 2) || FsValue(source) != 0xF)
                {
                    return ACMGR_ERR_INVALID;
                }

                FsStore(target, 0);
                return 0;
            case >= 6 and <= 8:
                return ACMGR_ERR_UNSUPPORTED_TYPE;
            default:
                return ACMGR_ERR_INVALID;
        }
    }

    /// <summary>(pid, const SceSblACMgrFsAttribute *attribute, int access): 0 when the attribute grants the access
    /// (bit 0 read, bit 1 write, bit 3 execute) to the calling thread, 0x8001000D when it does not.</summary>
    [Hle("NID_75AAF981", Nid = 0x75AAF981, Library = "SceSblACMgrForKernel")]
    [Hle("NID_BE5667C5", Nid = 0xBE5667C5, Library = "SceSblACMgrForDriver")]
    int SblAcMgrCheckFsAccess(int pid, uint attribute, uint access)
    {
        int begin = FsBegin(pid, attribute, out AcAuthInfo info, out FsAttribute fs);
        if (begin != 0)
        {
            return begin;
        }

        switch (fs.Type)
        {
            case >= 1 and <= 5:
                return FatAccess(fs, access);
            case 6:
                return ExfatAccessCheck(fs, access);
            case 7:
                return ACMGR_ERR_UNSUPPORTED_TYPE;
            case 8:
                return PfsAccess(info, fs, access);
            default:
                return ACMGR_ERR_INVALID;
        }
    }

    int FatAccess(FsAttribute fs, uint access)
    {
        uint level = ThreadAccessLevel();
        bool kernel;
        if (IsUserLevel(level))
        {
            kernel = false;
        }
        else if (IsKernelLevel(level))
        {
            kernel = true;
        }
        else
        {
            return ACMGR_ERR_PERMISSION;
        }

        uint expectedSize = fs.Type <= 3 ? 1u : 2u;
        if (fs.Size != expectedSize)
        {
            return ACMGR_ERR_INVALID;
        }

        uint value = FsValue(fs) & 7;
        uint granted = kernel ? KernelValueAccess[(int)value] : UserValueAccess[(int)value];
        return (granted & access) == access ? 0 : ACMGR_ERR_PERMISSION;
    }

    int ExfatAccessCheck(FsAttribute fs, uint access)
    {
        uint level = ThreadAccessLevel();
        if (!IsKernelLevel(level) && !IsUserLevel(level))
        {
            return ACMGR_ERR_LEVEL;
        }

        uint value = FsValue(fs) & 7;
        foreach ((uint entry, uint kernelAccess, uint userAccess) in ExfatAccess)
        {
            if (entry == value)
            {
                uint granted = IsKernelLevel(level) ? kernelAccess : userAccess;
                return (granted & access) == access ? 0 : ACMGR_ERR_PERMISSION;
            }
        }

        return ACMGR_ERR_PERMISSION;
    }

    /// <summary>A PFS attribute (type 8, 6 bytes): the access 1-4 a program has, from the nibble of its class (the
    /// kernel all; the package installer and two system services the first low nibble; the 0x2801 programs and two
    /// others the second byte's; the other system programs the first high nibble).</summary>
    int PfsAccess(AcAuthInfo info, FsAttribute fs, uint access)
    {
        if (access is < 1 or > 4)
        {
            return ACMGR_ERR_INVALID;
        }

        if (fs.Size != 6)
        {
            return ACMGR_ERR_PFS_ACCESS;
        }

        uint level = ThreadAccessLevel();
        uint high = info.High;
        uint low = info.Low;
        uint granted;
        if (level == LevelKernel || (high & 0xFFF80000) == 0x28080000)
        {
            granted = 0xF;
        }
        else if ((high == 0x28008000 && (low & 0xFFFFFFF0) == 0) || (high == 0x28000000 && low is 0x7001 or 0x15))
        {
            granted = GuestMemory.Read<byte>(fs.Value) & 0xFu;
        }
        else if ((high & 0xFFFF0000) == 0x28010000 || (high == 0x28008000 && low is 0x22 or 0x18))
        {
            granted = GuestMemory.Read<byte>(fs.Value + 1) & 0xFu;
        }
        else if (high == 0x28000000 && low is not (0x8005 or 0x8003 or 0x25) && (low & 0xFFFF0000) == 0)
        {
            granted = (uint)GuestMemory.Read<byte>(fs.Value) >> 4;
        }
        else
        {
            return ACMGR_ERR_PFS_ACCESS;
        }

        return (granted & access) == access ? 0 : ACMGR_ERR_PFS_ACCESS;
    }

    /// <summary>(const SceSblACMgrFsAttribute *attribute, int *mode): the open mode of an attribute (types 1-5 by
    /// the low bits; type 6 with its exFAT flags).</summary>
    [Hle("NID_11C9158B", Nid = 0x11C9158B, Library = "SceSblACMgrForKernel")]
    [Hle("NID_3B356B98", Nid = 0x3B356B98, Library = "SceSblACMgrForDriver")]
    int SblAcMgrFsAttributeToMode(uint attribute, uint mode)
    {
        if (attribute == 0 || GuestMemory.R32(attribute + 4) == 0 || mode == 0)
        {
            return ACMGR_ERR_INVALID;
        }

        FsAttribute fs = ReadFsAttribute(attribute);
        switch (fs.Type)
        {
            case >= 1 and <= 5:
                GuestMemory.W32(mode, ValueModes[(int)(FsValue(fs) & 7)]);
                return 0;
            case 6:
                return ExfatToMode(fs, mode);
            case 7:
            case 8:
                return ACMGR_ERR_UNSUPPORTED_TYPE;
            default:
                return ACMGR_ERR_INVALID;
        }
    }

    static int ExfatToMode(FsAttribute fs, uint mode)
    {
        uint value = FsValue(fs);
        foreach ((uint entryMode, uint entryValue) in ExfatKernelModes)
        {
            if (entryValue != (value & 7))
            {
                continue;
            }

            uint result = entryMode & 0xFFFEFFFF;
            if ((value & 0x4000) != 0)
            {
                result |= 0x100000;
            }

            if ((value & 0x2000) != 0)
            {
                result |= 0x200000;
            }

            result |= (value & 0x8000) != 0 ? 0x1000u : 0x2000u;
            GuestMemory.W32(mode, result);
            return 0;
        }

        return ACMGR_ERR_INVALID;
    }

    /// <summary>(int mode, int level, SceSblACMgrFsAttribute *attribute): the attribute of the mode for an explicit
    /// access level (types 1-5); a level it does not know 0x800F0916, a mode 0x80010002.</summary>
    [Hle("NID_0606B87E", Nid = 0x0606B87E, Library = "SceSblACMgrForDriver")]
    int SblAcMgrModeToFsAttributeForLevel(uint mode, uint level, uint attribute)
    {
        if (attribute == 0 || GuestMemory.R32(attribute + 4) == 0)
        {
            return ACMGR_ERR_INVALID;
        }

        FsAttribute fs = ReadFsAttribute(attribute);
        if (fs.Type is >= 6 and <= 8)
        {
            return ACMGR_ERR_UNSUPPORTED_TYPE;
        }

        if (fs.Type is < 1 or > 8)
        {
            return ACMGR_ERR_INVALID;
        }

        (uint Mode, uint Value)[] table;
        if (IsKernelLevel(level))
        {
            table = KernelModeValues;
        }
        else if (IsUserLevel(level))
        {
            table = UserModeValues;
        }
        else
        {
            return ACMGR_ERR_INVALID;
        }

        if (!FindMode(table, mode, out uint value))
        {
            return ACMGR_ERR_MODE_NOT_FOUND;
        }

        FsSetLowBits(fs, value);
        return 0;
    }
}
