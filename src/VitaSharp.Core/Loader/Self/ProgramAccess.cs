namespace VitaSharp.Core.Loader.Self;

/// <summary>
/// What SceSblACMgr answers about a program (FW 3.74 acmgr.skprx, study of its exports): it reads the process's SELF
/// auth info, which the secure processor fills when it authenticates the SELF; its capability field is a bitmap
/// numbered from the most significant bit of its first byte.
/// <list type="bullet">
/// <item>ksceSblACMgrIsSystemProgram: capability bit 1.</item>
/// <item>ksceSblACMgrIsNonGameProgram: capability bit 3.</item>
/// <item>ksceSblACMgrIsPSMProgram: the program authority ID is the PSM runtime's (0x2800C0101CD2000B) or one of
/// 0x210000101CD20007-0x210000101CD2000A.</item>
/// <item>ksceSblACMgrIsDevelopmentMode: a development kit; a retail console (the one emulated) is not.</item>
/// </list>
/// The emulator does not run the secure processor, so where the capability is not given it is answered from what the
/// program is (<see cref="Of"/>): the system software (vs0:) is a system and non-game program; a retail application
/// (an NPDRM SELF: games, the PSM runtime) is neither; homebrew gets what HENkaku's fake-SELF support gives it: a "safe"
/// one (vita-make-fself -s/-ss, authority 0x2F00000000000002/3) the game class, an "unsafe" one (the default authority
/// 0x2F00000000000001, or a plain ELF) the system class.
/// </summary>
public sealed class ProgramAccess
{
    /// <summary>The capability bitmap's size in SceSelfAuthInfo.</summary>
    public const int CapabilitySize = 0x20;

    /// <summary>The program authority ID of the PSM runtime package (PCSI00011 eboot.bin, 1.00 and 2.01).</summary>
    public const ulong PsmRuntimeAuthorityId = 0x2800C0101CD2000BUL;

    private const int SystemProgramBit = 1;
    private const int NonGameProgramBit = 3;
    private const ulong PsmProgramsFirst = 0x210000101CD20007UL;
    private const ulong PsmProgramsLast = 0x210000101CD2000AUL;
    private const ulong SafeHomebrewAuthorityId = 0x2F00000000000002UL;
    private const ulong SecretSafeHomebrewAuthorityId = 0x2F00000000000003UL;

    private readonly byte[]? _capability;
    private readonly bool _privileged;

    /// <summary>A program with the capability its auth info has (<paramref name="capability"/>, <see cref="CapabilitySize"/>
    /// bytes), or, when that is null, with the system class when <paramref name="privileged"/>.</summary>
    public ProgramAccess(ulong authorityId, byte[]? capability, bool privileged)
    {
        AuthorityId = authorityId;
        _capability = capability is { Length: >= CapabilitySize } ? capability : null;
        _privileged = privileged;
    }

    /// <summary>A program of unknown origin (no SELF read yet): no privilege.</summary>
    public static ProgramAccess None { get; } = new(0, null, false);

    /// <summary>
    /// The access of a program by what it is, when its auth info's capability is not known: the system software is
    /// privileged, a retail application (<paramref name="retailApplication"/>: an NPDRM SELF with encrypted segments)
    /// is not, and homebrew is privileged unless its fake SELF is a safe one.
    /// </summary>
    public static ProgramAccess Of(ulong authorityId, bool fromSystemSoftware, bool retailApplication)
    {
        if (fromSystemSoftware)
        {
            return new ProgramAccess(authorityId, null, true);
        }

        if (retailApplication)
        {
            return new ProgramAccess(authorityId, null, false);
        }

        bool safeHomebrew = authorityId is SafeHomebrewAuthorityId or SecretSafeHomebrewAuthorityId;
        return new ProgramAccess(authorityId, null, !safeHomebrew);
    }

    public ulong AuthorityId { get; }

    /// <summary>Whether the capability came from the auth info (not from what the program is).</summary>
    public bool HasCapability => _capability != null;

    public bool IsSystemProgram => _capability != null ? Bit(SystemProgramBit) : _privileged;

    public bool IsNonGameProgram => _capability != null ? Bit(NonGameProgramBit) : _privileged;

    public bool IsPsmProgram => AuthorityId == PsmRuntimeAuthorityId || AuthorityId is >= PsmProgramsFirst and <= PsmProgramsLast;

    /// <summary>The emulated console is a retail unit.</summary>
    public bool IsDevelopmentMode => false;

    /// <summary>
    /// Capability bit <paramref name="bit"/> of the process (SceSblACMgr's check of one bit, counted from the most
    /// significant bit of the first byte: ksceSblACMgrIsSystemProgram is bit 1, SceSblACMgrForDriver_A27E47A7 bit 0x81,
    /// SceSblACMgrForDriver_F5AE24AC bit 0x80). Without the auth info a privileged program has every bit.
    /// </summary>
    public bool HasCapabilityBit(int bit) => _capability != null ? (bit >> 3) < _capability.Length && Bit(bit) : _privileged;

    /// <summary>
    /// The first word of the capability field (little-endian), which SceSblACMgr also reads as the program's access
    /// level (0x80 kernel, 0x40 system, 0x20 game, 0x10 non-game user program: the values of the threads' access
    /// level). Without the auth info's capability it is the level of what the program is: a system (privileged)
    /// program 0x40, any other 0x20.
    /// </summary>
    public uint LevelWord
    {
        get
        {
            if (_capability != null)
            {
                return BitConverter.ToUInt32(_capability, 0);
            }

            return _privileged ? 0x40u : 0x20u;
        }
    }

    private bool Bit(int bit) => ((_capability![bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
}
