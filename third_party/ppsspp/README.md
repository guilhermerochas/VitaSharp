# PPSSPP (VFPU interpolator tables)

VitaSharp's Allegrex core (the PSP CPU of the Vita's compatibility hardware, `src/VitaSharp.Core/Compat/Allegrex`)
evaluates the VFPU's `vrcp`, `vrsq`, `vsqrt`, `vsin`/`vcos`, `vasin`, `vexp2` and `vlog2` the way the PSP does: one
quadratic interpolator over 128 segments per function. The segment coefficients were fitted to the hardware's output by
the PPSSPP project (issue [#16946](https://github.com/hrydgard/ppsspp/issues/16946)).

| What | Where in VitaSharp | Source |
| --- | --- | --- |
| The seven coefficient tables (data only) | `src/VitaSharp.Core/Compat/Allegrex/AllegrexVfpuTables.cs` | [`Core/MIPS/MIPSVFPUUtils.cpp`](https://github.com/hrydgard/ppsspp/blob/58b84a89a7cb4ea0c61407945f58cd4637da82a1/Core/MIPS/MIPSVFPUUtils.cpp) at commit `58b84a89` |

Only the tables are taken; the code that evaluates them, and the rest of the core, is VitaSharp's own. PPSSPP is
Copyright (c) 2012- PPSSPP Project and its contributors and is licensed under the GNU General Public License version
2.0 or later, which allows its use in VitaSharp under the GPL version 3 ([LICENSE](../../LICENSE)).

The tests compare the results with a small subset of the outputs recorded on a PSP by
[pspautotests](https://github.com/hrydgard/pspautotests) (`tests/cpu/vfpu/exact` and `tests/cpu/vfpu/vrnd`), embedded
as measurement data in `tests/VitaSharp.Tests/AllegrexVfpuHardwareTests.cs`.
