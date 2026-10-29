namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Linear-scan register allocation over the (forward-only, linear) IR, shared by the host emitters. Temps live across a
/// call site (callout, FpOp, slow-path VecOp) only get callee-saved registers or spill slots; spill slots are free for a
/// whole live range (victims are spilled retroactively from their definition).
/// </summary>
public static class LinearScan
{
    /// <summary>
    /// Returns the location of every temp (&gt;= 0 host register, &lt;= -2 spill slot -(loc+2), -1 none), or null when
    /// the block needs more than <paramref name="spillSlots"/> slots.
    /// </summary>
    public static int[]? Allocate(IrBlock ir, int[] callerSaved, int[] calleeSaved, int spillSlots, int maxTempRegisters, bool verify)
    {
        int n = ir.TempCount;
        var def = new int[n];
        var last = new int[n];
        Array.Fill(def, -1);
        Array.Fill(last, -1);
        var insts = ir.Insts;
        int count = insts.Count;
        var calloutsBefore = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            var x = insts[i];
            calloutsBefore[i + 1] = calloutsBefore[i] + (IsCallSite(x) ? 1 : 0);
            void Out(int t, int idx) { if (t >= 0) def[t] = idx; }
            void In(int t, int idx) { if (t >= 0) last[t] = idx; }
            Out(x.D, i); Out(x.D2, i); Out(x.Fn, i); Out(x.Fz, i); Out(x.Fc, i); Out(x.Fv, i);
            In(x.A, i); In(x.B, i); In(x.C, i); In(x.E, i);
        }
        var loc = new int[n];
        Array.Fill(loc, -1);
        var freeRegs = new List<int>();
        int limit = maxTempRegisters;
        foreach (var r in callerSaved) if (freeRegs.Count < limit) freeRegs.Add(r);
        foreach (var r in calleeSaved) if (freeRegs.Count < limit) freeRegs.Add(r);
        var active = new List<int>(); // temps currently holding registers
        // a slot is free for a whole live range [from, until] when its previous occupant died before "from"
        // (spill victims are spilled retroactively from their definition, not from the current instruction)
        var slotFreeAfter = new int[spillSlots];
        Array.Fill(slotFreeAfter, -1);
        int TakeSlot(int from, int until)
        {
            for (int s = 0; s < spillSlots; s++)
                if (slotFreeAfter[s] < from) { slotFreeAfter[s] = until; return s; }
            return -1;
        }

        bool Crosses(int t) => last[t] > def[t] && calloutsBefore[last[t]] - calloutsBefore[def[t] + 1] > 0;
        bool IsCallee(int r) => Array.IndexOf(calleeSaved, r) >= 0;

        for (int i = 0; i < count; i++)
        {
            // release temps whose last use is before this instruction
            for (int k = active.Count - 1; k >= 0; k--)
                if (last[active[k]] < i) { freeRegs.Add(loc[active[k]]); active.RemoveAt(k); }

            var x = insts[i];
            foreach (int t in (ReadOnlySpan<int>)[x.D, x.D2, x.Fn, x.Fz, x.Fc, x.Fv])
            {
                if (t < 0 || ir.IsConst[t]) continue;
                if (last[t] < 0) { last[t] = i; } // defined but unused (kept for side effects): short interval
                bool crosses = Crosses(t);
                int reg = -1;
                // pick: crossing temps need callee-saved; others prefer caller-saved
                for (int k = 0; k < freeRegs.Count; k++)
                {
                    int r = freeRegs[k];
                    if (crosses && !IsCallee(r)) continue;
                    if (reg < 0 || (!crosses && IsCallee(reg) && !IsCallee(r))) reg = r;
                }
                if (reg >= 0)
                {
                    freeRegs.Remove(reg);
                    loc[t] = reg;
                    active.Add(t);
                    continue;
                }
                // spill the compatible active temp that lives longest (or this one)
                int victim = -1;
                foreach (int a in active)
                {
                    if (crosses && !IsCallee(loc[a])) continue;
                    if (victim < 0 || last[a] > last[victim]) victim = a;
                }
                if (victim >= 0 && last[victim] > last[t])
                {
                    int slot = TakeSlot(def[victim], last[victim]);
                    if (slot < 0) return null;
                    loc[t] = loc[victim];
                    active.Remove(victim);
                    active.Add(t);
                    loc[victim] = -2 - slot;
                }
                else
                {
                    int slot = TakeSlot(i, last[t]);
                    if (slot < 0) return null;
                    loc[t] = -2 - slot;
                }
            }
        }
        if (verify)
        {
            // no two temps whose live ranges overlap may share a register or a spill slot
            var temps = Enumerable.Range(0, n).Where(t => loc[t] != -1).ToArray();
            foreach (int x in temps)
                foreach (int y in temps)
                    if (x < y && loc[x] == loc[y] && def[x] <= last[y] && def[y] <= last[x])
                        throw new InvalidOperationException($"allocation: t{x} [{def[x]},{last[x]}] and t{y} [{def[y]},{last[y]}] share {loc[x]}\n{ir}");
        }
        return loc;
    }

    /// <summary>FpOp / slow-path VecOp may call the interpreter handler, like a callout.</summary>
    public static bool IsCallSite(in IrInst x) =>
        x.Op is IrOp.Callout or IrOp.FpOp || (x.Op == IrOp.VecOp && (x.Imm & (uint)VecFlags.SlowPath) != 0);
}
