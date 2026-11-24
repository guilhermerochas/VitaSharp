namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>What stopped an instruction and was offered to <see cref="AllegrexCore.TrapHandler"/>.</summary>
public enum AllegrexTrapKind
{
    /// <summary><c>syscall</c>: the code is the instruction's 20-bit code field.</summary>
    Syscall,

    /// <summary><c>break</c>: the code is the instruction's 20-bit code field.</summary>
    Break,
}

/// <summary>
/// Offered a syscall or break before the core acts on it. Returning true means the host handled it: the core goes on
/// with the next instruction. Returning false lets the core do what the hardware does (the syscall or breakpoint
/// exception through EBase).
/// </summary>
public delegate bool AllegrexTrapHandler(AllegrexCore core, AllegrexTrapKind kind, uint code, uint instructionAddress);

/// <summary>The MIPS exception codes (Cause bits 2..6) the Allegrex raises.</summary>
public enum AllegrexExceptionCode : uint
{
    Interrupt = 0,
    AddressErrorLoad = 4,
    AddressErrorStore = 5,
    Syscall = 8,
    Breakpoint = 9,
    ReservedInstruction = 10,
    CoprocessorUnusable = 11,
    Overflow = 12,
    Trap = 13,
    FloatingPoint = 15,
}
