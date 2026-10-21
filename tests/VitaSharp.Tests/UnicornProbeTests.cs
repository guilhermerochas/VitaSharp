using UnicornEngine;
using UnicornEngine.Const;
using Xunit;
using Xunit.Abstractions;
using UcArm = UnicornEngine.Const.Arm;

namespace VitaSharp.Tests;

public sealed class UnicornProbeTests(ITestOutputHelper output)
{
    [UnicornFact]
    public void RegisterRoundTrip()
    {
        var uc = new Unicorn(Common.UC_ARCH_ARM, Common.UC_MODE_ARM);
        int[] ids = [UcArm.UC_ARM_REG_R0, UcArm.UC_ARM_REG_R1, UcArm.UC_ARM_REG_R8, UcArm.UC_ARM_REG_R9, UcArm.UC_ARM_REG_R10, UcArm.UC_ARM_REG_R12, UcArm.UC_ARM_REG_R13, UcArm.UC_ARM_REG_R14, UcArm.UC_ARM_REG_D0, UcArm.UC_ARM_REG_D15, UcArm.UC_ARM_REG_CPSR, UcArm.UC_ARM_REG_FPSCR];
        foreach (var id in ids)
        {
            uc.RegWrite(id, 0x1234_5600 + id);
            output.WriteLine($"id {id}: wrote {0x1234_5600 + id:X} read {uc.RegRead(id):X}");
        }
        output.WriteLine($"R0={UcArm.UC_ARM_REG_R0} R9={UcArm.UC_ARM_REG_R9} SB={UcArm.UC_ARM_REG_SB} D0={UcArm.UC_ARM_REG_D0} CPSR={UcArm.UC_ARM_REG_CPSR}");
    }
}
