using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceSblAIMgrForDriver (SceSysmem 3.74, read in the module, study only): the unit's identity queries. At boot the
// module copies the product code block and the hardware info word from the boot loader's parameters; every query
// is a test of those words (ConsoleIdentity), except IsDolce, which also honours the debug switch 0x98 (a handheld
// run as a PS TV), and GetSMI, which reads the secure module information word.
public sealed partial class SystemServicesVsh
{
    /// <summary>ksceSblAimgrGetSMI's answer without a place to write to.</summary>
    const int AIMGR_ERR_INVALID_ARGUMENT = unchecked((int)0x800F0C16);

    /// <summary>The debug switch that makes the unit report itself as a PS TV.</summary>
    const uint DipswRunAsDolce = 0x98;

    /// <summary>The product codes of the kinds of unit: test, tool (development kit), DEX (testing kit), and the first
    /// CEX (retail) code; the CEX codes run to 0x111.</summary>
    const ushort ProductTest = 0x100, ProductTool = 0x101, ProductDex = 0x102, ProductFirstCex = 0x103;

    /// <summary>ksceSblAimgrGetProductCode (0x14345161)(): the product code.</summary>
    [Hle(Nid = 0x14345161, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrGetProductCode()
    {
        return ConsoleIdentity.ProductCode;
    }

    /// <summary>ksceSblAimgrGetProductSubCode (0xB33CEC8F)(): the product sub code.</summary>
    [Hle(Nid = 0xB33CEC8F, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrGetProductSubCode()
    {
        return ConsoleIdentity.ProductSubCode;
    }

    /// <summary>ksceSblAimgrGetSMI (0x47D9CF13)(uint *smi): the secure module information word of the boot
    /// parameters; NULL is 0x800F0C16.</summary>
    [Hle(Nid = 0x47D9CF13, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrGetSMI(uint smi)
    {
        if (smi == 0)
        {
            return AIMGR_ERR_INVALID_ARGUMENT;
        }

        GuestMemory.W32(smi, ConsoleIdentity.SecureModuleInfo);
        return 0;
    }

    /// <summary>ksceSblAimgrIsTest (0x3B638885)(): a test unit (product 0x100).</summary>
    [Hle(Nid = 0x3B638885, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsTest()
    {
        return ConsoleIdentity.ProductCode == ProductTest ? 1 : 0;
    }

    /// <summary>ksceSblAimgrIsTool (0x274663A0)(): a development kit (product 0x101).</summary>
    [Hle(Nid = 0x274663A0, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsTool()
    {
        return ConsoleIdentity.ProductCode == ProductTool ? 1 : 0;
    }

    /// <summary>ksceSblAimgrIsDEX (0xF4B98F66)(): a testing kit (product 0x102).</summary>
    [Hle(Nid = 0xF4B98F66, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsDEX()
    {
        return ConsoleIdentity.ProductCode == ProductDex ? 1 : 0;
    }

    /// <summary>ksceSblAimgrIsJapaneseFat (0x6D5A3FC9)(): a first Japanese handheld of one factory (product 0x103,
    /// sub code 0x10, factory code 0x24).</summary>
    [Hle(Nid = 0x6D5A3FC9, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsJapaneseFat()
    {
        return AimgrIsJapaneseFat() ? 1 : 0;
    }

    static bool AimgrIsJapaneseFat()
    {
        return ConsoleIdentity.ProductCode == ProductFirstCex && ConsoleIdentity.ProductSubCode == 0x10
            && ConsoleIdentity.FactoryCode == 0x24;
    }

    /// <summary>ksceSblAimgrIsCEX (0xD78B04A2)(): a retail unit (product 0x103..0x111) that is not a JapaneseFat
    /// one.</summary>
    [Hle(Nid = 0xD78B04A2, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsCEX()
    {
        bool retail = (ushort)(ConsoleIdentity.ProductCode - ProductFirstCex) < 0xF;
        return retail && !AimgrIsJapaneseFat() ? 1 : 0;
    }

    /// <summary>Whether the hardware info word names one of the PS TV boards (0x51, 0x70, 0x72 in bits 16..23).</summary>
    static bool AimgrIsDolceBoard()
    {
        uint board = ConsoleIdentity.HardwareInfo & 0xFF0000;
        return board is 0x700000 or 0x720000 or 0x510000;
    }

    static bool AimgrIsGenuineDolce()
    {
        bool toolDolce = ConsoleIdentity.ProductCode == ProductTool && ConsoleIdentity.ProductSubCode == 0x408;
        if (toolDolce || (ConsoleIdentity.ProductSubCode & 0x200) != 0)
        {
            return true;
        }

        return AimgrIsJapaneseFat() && AimgrIsDolceBoard();
    }

    /// <summary>ksceSblAimgrIsGenuineDolce (0xC6E83F34)(): a PS TV by its codes (sub code bit 0x200, or the tool
    /// unit 0x408).</summary>
    [Hle(Nid = 0xC6E83F34, Library = "SceSblAIMgrForDriver")]
    internal int ksceSblAimgrIsGenuineDolce()
    {
        return AimgrIsGenuineDolce() ? 1 : 0;
    }

    static bool AimgrIsGenuineVita()
    {
        bool known = (ushort)(ConsoleIdentity.ProductCode - ProductTest) <= 0x11;
        if (known && !AimgrIsGenuineDolce())
        {
            return true;
        }

        return AimgrIsJapaneseFat() && !AimgrIsDolceBoard();
    }

    /// <summary>ksceSblAimgrIsGenuineVITA (0x963CA644)(): a handheld (a known product code that is not a PS TV).</summary>
    [Hle(Nid = 0x963CA644, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsGenuineVITA()
    {
        return AimgrIsGenuineVita() ? 1 : 0;
    }

    /// <summary>ksceSblAimgrIsVITA (0x4273B97B)(): the same test as IsGenuineVITA.</summary>
    [Hle(Nid = 0x4273B97B, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsVITA()
    {
        return AimgrIsGenuineVita() ? 1 : 0;
    }

    /// <summary>ksceSblAimgrIsDolce (0x71608CA3)(): a PS TV, or a handheld with the debug switch 0x98 set.</summary>
    [Hle(Nid = 0x71608CA3, Library = "SceSblAIMgrForDriver")]
    internal int ksceSblAimgrIsDolce()
    {
        return AimgrIsGenuineDolce() || ksceKernelCheckDipswThroughEntry(DipswRunAsDolce) != 0 ? 1 : 0;
    }

    /// <summary>A tool unit (0x101) of the given revision (its sub code).</summary>
    static int AimgrIsToolRevision(ushort revision)
    {
        return ConsoleIdentity.ProductCode == ProductTool && ConsoleIdentity.ProductSubCode == revision ? 1 : 0;
    }

    /// <summary>A prototype retail unit (0x103) of the given revision (its sub code).</summary>
    static int AimgrIsPrototypeRevision(ushort revision)
    {
        return ConsoleIdentity.ProductCode == ProductFirstCex && ConsoleIdentity.ProductSubCode == revision ? 1 : 0;
    }

    /// <summary>ksceSblAimgrIsToolRev3 (0xBB9D146B)().</summary>
    [Hle(Nid = 0xBB9D146B, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsToolRev3()
    {
        return AimgrIsToolRevision(3);
    }

    /// <summary>ksceSblAimgrIsToolRev4 (0x37A79140)().</summary>
    [Hle(Nid = 0x37A79140, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsToolRev4()
    {
        return AimgrIsToolRevision(4);
    }

    /// <summary>ksceSblAimgrIsToolRev5 (0xE5E47FF7)().</summary>
    [Hle(Nid = 0xE5E47FF7, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsToolRev5()
    {
        return AimgrIsToolRevision(5);
    }

    /// <summary>ksceSblAimgrIsPrototypeRev2 (0xFF5784B9)().</summary>
    [Hle(Nid = 0xFF5784B9, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsPrototypeRev2()
    {
        return AimgrIsPrototypeRevision(2);
    }

    /// <summary>ksceSblAimgrIsPrototypeRev7 (0x05F79D4A)().</summary>
    [Hle(Nid = 0x05F79D4A, Library = "SceSblAIMgrForDriver")]
    int ksceSblAimgrIsPrototypeRev7()
    {
        return AimgrIsPrototypeRevision(7);
    }
}
