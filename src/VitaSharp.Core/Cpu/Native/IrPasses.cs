namespace VitaSharp.Core.Cpu.Native;

/// <summary>Backend-neutral IR clean-up run before emission.</summary>
public static class IrPasses
{
    /// <summary>
    /// Backward liveness: removes pure instructions whose results are unused and drops unused optional outputs (flag
    /// results superseded before the next flush, the high half of an unused product, ...). This is what makes flags
    /// lazy: a CMP whose N/C/V are overwritten before anything reads or stores them only produces Z.
    /// </summary>
    public static void DeadCode(IrBlock b)
    {
        var used = new bool[b.TempCount];
        var insts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(b.Insts);
        for (int i = insts.Length - 1; i >= 0; i--)
        {
            ref var x = ref insts[i];
            if (x.Op == IrOp.Nop) continue;
            static int Keep(int t, bool[] u) => t >= 0 && u[t] ? t : -1;
            x.D = Keep(x.D, used); x.D2 = Keep(x.D2, used);
            x.Fn = Keep(x.Fn, used); x.Fz = Keep(x.Fz, used); x.Fc = Keep(x.Fc, used); x.Fv = Keep(x.Fv, used);
            bool anyOut = x.D >= 0 || x.D2 >= 0 || x.Fn >= 0 || x.Fz >= 0 || x.Fc >= 0 || x.Fv >= 0;
            if (!anyOut && !x.HasSideEffects) { x = new IrInst(IrOp.Nop); continue; }
            // a shift's carry-in only feeds its carry-out
            if (x.Op == IrOp.ShiftReg && x.Fc < 0) x.C = -1;
            if (x.A >= 0) used[x.A] = true;
            if (x.B >= 0) used[x.B] = true;
            if (x.C >= 0) used[x.C] = true;
            if (x.E >= 0) used[x.E] = true;
        }
    }

    /// <summary>
    /// Checks the structural rules emitters rely on: every temp is defined once before use, and no temp defined inside
    /// a conditional region is used after the region's label. Throws on violation (frontend bug).
    /// </summary>
    public static void Validate(IrBlock b)
    {
        var defAt = new int[b.TempCount];
        Array.Fill(defAt, -1);
        var regionOfLabel = new Dictionary<uint, int>();
        int openStart = -1; uint openLabel = 0;
        var regions = new List<(int Start, int End)>();
        var insts = b.Insts;
        for (int i = 0; i < insts.Count; i++)
        {
            var x = insts[i];
            if (x.Op is IrOp.CondBranch or IrOp.BranchIf) { openStart = i; openLabel = x.Imm2; }
            if (x.Op == IrOp.Label && openStart >= 0 && x.Imm == openLabel) { regions.Add((openStart, i)); openStart = -1; }
        }
        int RegionOf(int idx) { for (int r = 0; r < regions.Count; r++) if (idx > regions[r].Start && idx < regions[r].End) return r; return -1; }
        for (int i = 0; i < insts.Count; i++)
        {
            var x = insts[i];
            foreach (int u in new[] { x.A, x.B, x.C, x.E })
            {
                if (u < 0 || b.IsConst[u]) continue;
                if (defAt[u] < 0) throw new InvalidOperationException($"IR: t{u} used at {i} before definition\n{b}");
                int r = RegionOf(defAt[u]);
                if (r >= 0 && i >= regions[r].End) throw new InvalidOperationException($"IR: t{u} defined in region {r} used after it at {i}\n{b}");
            }
            foreach (int o in new[] { x.D, x.D2, x.Fn, x.Fz, x.Fc, x.Fv })
            {
                if (o < 0) continue;
                if (defAt[o] >= 0) throw new InvalidOperationException($"IR: t{o} defined twice\n{b}");
                defAt[o] = i;
            }
        }
    }
}
