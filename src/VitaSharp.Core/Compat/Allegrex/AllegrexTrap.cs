namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>What stopped an instruction and was offered to <see cref="AllegrexCore.TrapHandler"/>.</summary>
public enum AllegrexTrapKind
{
    /// <summary><c>syscall</c>: the code is the instruction's 20-bit code field.</summary>
    Syscall,

    /// <summary><c>break</c>: the code is the instruction's 20-bit code field.</summary>
    Break,

    /// <summary>An instruction of a unit not emulated yet (FPU, VFPU): the code is the instruction word.</summary>
    Unimplemented,
}

/// <summary>
/// Offered a syscall, break or not-yet-emulated instruction before the core acts on it. Returning true means the host
/// handled it: the core goes on with the next instruction. Returning false lets the core do what the hardware does
/// (the syscall or breakpoint exception through EBase; for an unimplemented unit, an
/// <see cref="AllegrexUnimplementedException"/>).
/// </summary>
public delegate bool AllegrexTrapHandler(AllegrexCore core, AllegrexTrapKind kind, uint code, uint instructionAddress);

/// <summary>An instruction of a unit the Allegrex interpreter does not emulate yet (phase 2: FPU and VFPU).</summary>
public sealed class AllegrexUnimplementedException : Exception
{
    public AllegrexUnimplementedException(uint address, uint instruction, string unit)
        : base($"Allegrex: {unit} instruction 0x{instruction:X8} at 0x{address:X8} is not emulated yet")
    {
        Address = address;
        Instruction = instruction;
        Unit = unit;
    }

    public uint Address { get; }

    public uint Instruction { get; }

    public string Unit { get; }
}

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
