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
    /// <summary>Per-thread buffers (the compile workers allocate nothing per block once warm).</summary>
    sealed class Scratch
    {
        public int[] Def = new int[256], Last = new int[256], Loc = new int[256], CalloutsBefore = new int[257], SlotFreeAfter = [];
        public readonly List<int> FreeRegs = new(16), Active = new(16);
    }

    [ThreadStatic] static Scratch? _scratch;

    static int[] Ensure(ref int[] a, int n) => a.Length >= n ? a : a = new int[Math.Max(n, a.Length * 2)];

    /// <summary>
    /// Returns the location of every temp (&gt;= 0 host register, &lt;= -2 spill slot -(loc+2), -1 none), or null when
    /// the block needs more than <paramref name="spillSlots"/> slots. With <paramref name="reuse"/> the returned array
    /// is a per-thread buffer (valid until the thread's next allocation; it may be longer than the temp count).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static int[]? Allocate(IrBlock ir, int[] callerSaved, int[] calleeSaved, int spillSlots, int maxTempRegisters, bool verify, bool reuse = false)
    {
        var sc = reuse ? _scratch ??= new Scratch() : new Scratch();
        int n = ir.TempCount;
        var def = Ensure(ref sc.Def, n);
        var last = Ensure(ref sc.Last, n);
        Array.Fill(def, -1, 0, n);
        Array.Fill(last, -1, 0, n);
        var insts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ir.Insts);
        int count = insts.Length;
        var calloutsBefore = Ensure(ref sc.CalloutsBefore, count + 1);
        calloutsBefore[0] = 0;
        for (int i = 0; i < count; i++)
        {
            ref readonly var x = ref insts[i];
            calloutsBefore[i + 1] = calloutsBefore[i] + (IsCallSite(x) ? 1 : 0);
            if (x.D >= 0) def[x.D] = i;
            if (x.D2 >= 0) def[x.D2] = i;
            if (x.Fn >= 0) def[x.Fn] = i;
            if (x.Fz >= 0) def[x.Fz] = i;
            if (x.Fc >= 0) def[x.Fc] = i;
            if (x.Fv >= 0) def[x.Fv] = i;
            if (x.A >= 0) last[x.A] = i;
            if (x.B >= 0) last[x.B] = i;
            if (x.C >= 0) last[x.C] = i;
            if (x.E >= 0) last[x.E] = i;
        }
        var loc = reuse ? Ensure(ref sc.Loc, n) : new int[n];
        Array.Fill(loc, -1, 0, n);
        var freeRegs = sc.FreeRegs;
        freeRegs.Clear();
        int limit = maxTempRegisters;
        foreach (var r in callerSaved) if (freeRegs.Count < limit) freeRegs.Add(r);
        foreach (var r in calleeSaved) if (freeRegs.Count < limit) freeRegs.Add(r);
        uint calleeMask = 0;
        foreach (var r in calleeSaved) calleeMask |= 1u << r;
        var active = sc.Active; // temps currently holding registers
        active.Clear();
        // a slot is free for a whole live range [from, until] when its previous occupant died before "from"
        // (spill victims are spilled retroactively from their definition, not from the current instruction)
        var slotFreeAfter = Ensure(ref sc.SlotFreeAfter, spillSlots);
        Array.Fill(slotFreeAfter, -1, 0, spillSlots);
        int TakeSlot(int from, int until)
        {
            for (int s = 0; s < spillSlots; s++)
                if (slotFreeAfter[s] < from) { slotFreeAfter[s] = until; return s; }
            return -1;
        }

        bool Crosses(int t) => last[t] > def[t] && calloutsBefore[last[t]] - calloutsBefore[def[t] + 1] > 0;
        bool IsCallee(int r) => (calleeMask >> r & 1) != 0;
        var isConst = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ir.IsConst);

        for (int i = 0; i < count; i++)
        {
            // release temps whose last use is before this instruction
            for (int k = active.Count - 1; k >= 0; k--)
                if (last[active[k]] < i) { freeRegs.Add(loc[active[k]]); active.RemoveAt(k); }

            ref readonly var x = ref insts[i];
            foreach (int t in (ReadOnlySpan<int>)[x.D, x.D2, x.Fn, x.Fz, x.Fc, x.Fv])
            {
                if (t < 0 || isConst[t]) continue;
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
