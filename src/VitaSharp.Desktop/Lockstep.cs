using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using UcArm = UnicornEngine.Const.Arm;

namespace VitaSharp.Desktop;

/// <summary>
/// Debug: differential execution of real game code against Unicorn. VITASHARP_LOCKSTEP=STARTPC:BLOCKS:FROMFRAME.
/// The first thread reaching STARTPC (after FROMFRAME) has its next BLOCKS blocks executed on Unicorn first (same
/// registers and shared memory, Unicorn stores undone) one instruction at a time, each compared with ours; a mismatch
/// is logged with the full input state (LOCKSTEP MISMATCH) and the rest of that block is not compared. JIT is disabled.
/// </summary>
static unsafe class Lockstep
{
    [DllImport("unicorn")] static extern int uc_open(int arch, int mode, out nint uc);
    [DllImport("unicorn")] static extern int uc_mem_map_ptr(nint uc, ulong address, nuint size, uint perms, void* ptr);
    [DllImport("unicorn")] static extern int uc_mem_map(nint uc, ulong address, nuint size, uint perms);
    [DllImport("unicorn")] static extern int uc_mem_write(nint uc, ulong address, void* bytes, nuint size);
    [DllImport("unicorn")] static extern int uc_reg_write(nint uc, int regid, void* value);
    [DllImport("unicorn")] static extern int uc_reg_read(nint uc, int regid, void* value);
    [DllImport("unicorn")] static extern int uc_emu_start(nint uc, ulong begin, ulong until, ulong timeout, nuint count);
    [DllImport("unicorn")] static extern int uc_emu_stop(nint uc);
    [DllImport("unicorn")] static extern int uc_hook_add(nint uc, out nint hh, int type, void* callback, void* userData, ulong begin, ulong end);

    static uint _start; static long _blocks, _fromFrame; static int _threadId = -1; static long _done, _mismatches;
    static nint _uc;
    static readonly List<(long Addr, int Size, byte[] Old, long Value)> _writes = new();
    static readonly uint[] _ucR = new uint[16]; static readonly ulong[] _ucD = new ulong[32]; static uint _ucCpsr, _ucFpscr;

    static readonly int[] RegIds =
    [
        UcArm.UC_ARM_REG_R0, UcArm.UC_ARM_REG_R1, UcArm.UC_ARM_REG_R2, UcArm.UC_ARM_REG_R3, UcArm.UC_ARM_REG_R4,
        UcArm.UC_ARM_REG_R5, UcArm.UC_ARM_REG_R6, UcArm.UC_ARM_REG_R7, UcArm.UC_ARM_REG_R8, UcArm.UC_ARM_REG_R9,
        UcArm.UC_ARM_REG_R10, UcArm.UC_ARM_REG_R11, UcArm.UC_ARM_REG_R12, UcArm.UC_ARM_REG_R13, UcArm.UC_ARM_REG_R14, UcArm.UC_ARM_REG_R15,
    ];

    public static void Install()
    {
        if (Environment.GetEnvironmentVariable("VITASHARP_LOCKSTEP") is not { } spec) return;
        var p = spec.Split(':');
        if (p[0] == "ALL") { InstallAll(long.Parse(p[1]), p.Length > 2 ? long.Parse(p[2]) : 0); return; }
        _start = Convert.ToUInt32(p[0], 16) & ~1u; _blocks = long.Parse(p[1]); _fromFrame = p.Length > 2 ? long.Parse(p[2]) : 0;
        Jit.Enabled = false;
        CpuCore.BlockHook = Hook;
        NewUc();
        Log.Info($"LOCKSTEP armed at {_start:X8} for {_blocks} blocks from frame {_fromFrame}");
    }

    static readonly object _allLock = new();
    static readonly Dictionary<uint, int> _perAddress = new();
    static readonly Dictionary<Op, long> _perOp = new();
    static bool _all;

    /// <summary>VITASHARP_LOCKSTEP=ALL:FRAMES:FROMFRAME: the backends run normally until FROMFRAME, then every block of
    /// every thread is compared with Unicorn (serialised; best with --cores=1) for FRAMES frames. Mismatches are logged
    /// at most 3 times per instruction address and summarised per opcode at the end.</summary>
    static void InstallAll(long frames, long fromFrame)
    {
        _all = true;
        NewUc();
        Log.Info($"LOCKSTEP ALL armed for frames {fromFrame}..{fromFrame + frames}");
        new Thread(() =>
        {
            while (VitaSharp.Core.Hle.Modules.Display.FramesPresented < fromFrame) Thread.Sleep(5);
            Log.Info($"LOCKSTEP ALL start at frame {VitaSharp.Core.Hle.Modules.Display.FramesPresented}");
            CpuCore.BlockHook = (core, s, b) => { lock (_allLock) { _done++; StepBlock(s, b); } return true; };
            while (VitaSharp.Core.Hle.Modules.Display.FramesPresented < fromFrame + frames) Thread.Sleep(5);
            CpuCore.BlockHook = null;
            lock (_allLock)
            {
                Log.Info($"LOCKSTEP ALL done at frame {VitaSharp.Core.Hle.Modules.Display.FramesPresented}: {_done} blocks, {_mismatches} mismatches at {_perAddress.Count} addresses");
                foreach (var (op, n) in _perOp.OrderByDescending(kv => kv.Value)) Log.Info($"LOCKSTEP ALL op {op}: {n}");
            }
        }) { IsBackground = true, Name = "lockstep" }.Start();
    }

    static void W(int reg, ulong v) => uc_reg_write(_uc, reg, &v);
    static ulong R(int reg) { ulong v = 0; uc_reg_read(_uc, reg, &v); return v; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnWrite(nint uc, int type, ulong address, int size, long value, void* user)
    {
        var old = new byte[size];
        new ReadOnlySpan<byte>(GuestMemory.Base + address, size).CopyTo(old);
        _writes.Add(((long)address, size, old, value));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnIntr(nint uc, uint intno, void* user) => uc_emu_stop(uc);

    static void NewUc()
    {
        int e = uc_open(1, 16, out _uc); // ARM, THUMB
        if (e != 0) throw new InvalidOperationException($"uc_open failed: {e}");
        // CDRAM and user memory are shared with Unicorn; its stores are undone after every step
        uc_mem_map_ptr(_uc, 0x60000000, 0x80000000, 7, GuestMemory.Base + 0x60000000L);
        uc_mem_map(_uc, 0x1000, 0x1000, 7);
        uint* init = stackalloc uint[] { 0xEE110F50, 0xE380060F, 0xEE010F50, 0xF57FF06F, 0xE3A00101, 0xEEE80A10 };
        uc_mem_write(_uc, 0x1000, init, 24);
        W(UcArm.UC_ARM_REG_CPSR, 0x1D3);
        uc_emu_start(_uc, 0x1000, 0x1000 + 24, 0, 0); // enable VFP/NEON (CPACR, FPEXC) from privileged mode
        uc_hook_add(_uc, out _, 1 << 11, (delegate* unmanaged[Cdecl]<nint, int, ulong, int, long, void*, void>)&OnWrite, null, 0x60000000, 0xDFFFFFFF);
        uc_hook_add(_uc, out _, 1, (delegate* unmanaged[Cdecl]<nint, uint, void*, void>)&OnIntr, null, 1, 0);
    }

    static bool Hook(CpuCore core, CpuState* s, Block b)
    {
        if (_threadId < 0)
        {
            if (b.Start != _start || VitaSharp.Core.Hle.Modules.Display.FramesPresented < _fromFrame) return false;
            _threadId = Environment.CurrentManagedThreadId;
            Log.Info($"LOCKSTEP start on {VitaSharp.Core.Kernel.GuestThread.Current?.Name} at frame {VitaSharp.Core.Hle.Modules.Display.FramesPresented}");
        }
        if (Environment.CurrentManagedThreadId != _threadId) return false;
        if (_done++ >= _blocks) { if (_done == _blocks + 1) Log.Info($"LOCKSTEP done: {_blocks} blocks, {_mismatches} mismatches"); return false; }
        StepBlock(s, b);
        return true;
    }

    static readonly Dictionary<long, byte> _ucBytes = new();

    static void LoadUc(CpuState* s)
    {
        for (int r = 0; r < 15; r++) W(RegIds[r], s->R[r]);
        W(UcArm.UC_ARM_REG_CPSR, (s->Cpsr & ~0x3Fu) | 0x10u | (s->Thumb != 0 ? 0x20u : 0));
        for (int r = 0; r < 32; r++) W(UcArm.UC_ARM_REG_D0 + r, s->D[r]);
        W(UcArm.UC_ARM_REG_FPSCR, s->Fpscr);
        W(UcArm.UC_ARM_REG_C13_C0_3, s->TpidrUro);
    }

    /// <summary>ARM ARM ConditionPassed() on the NZCV flags of a CPSR value.</summary>
    static bool CondPassed(uint cpsr, uint cond)
    {
        bool n = (cpsr & 0x80000000) != 0, z = (cpsr & 0x40000000) != 0, c = (cpsr & 0x20000000) != 0, v = (cpsr & 0x10000000) != 0;
        bool r = (cond >> 1) switch
        {
            0 => z, 1 => c, 2 => n, 3 => v, 4 => c && !z, 5 => n == v, 6 => n == v && !z, _ => true,
        };
        return (cond & 1) != 0 && cond != 15 ? !r : r;
    }

    /// <summary>Executes the block one instruction at a time on Unicorn and on our interpreter, comparing after each.</summary>
    static void StepBlock(CpuState* s, Block b)
    {
        if (_uc == 0) NewUc();
        LoadUc(s);
        bool sync = true;
        uint* preR = stackalloc uint[16];
        ulong* preD = stackalloc ulong[32];
        uint it = 0, nextIt = 0; // ITSTATE (firstcond:mask) of the current instruction, as in Block's decoder
        for (int i = 0; i < b.Insts.Length; i++)
        {
            var d = b.Insts[i];
            it = nextIt;
            nextIt = d.Op == Op.Thumb_It ? d.Raw & 0xFFu : (it & 7) == 0 ? 0 : (it & 0xE0) | ((it << 1) & 0x1F);
            bool svc = d.Op is Op.A32_Svc or Op.T16_Svc;
            uint pre15 = s->R[15];
            for (int r = 0; r < 16; r++) preR[r] = s->R[r];
            for (int r = 0; r < 32; r++) preD[r] = s->D[r];
            uint preFpscr = s->Fpscr, preCpsr = s->Cpsr;
            int e = 0;
            // Unicorn single steps do not survive IT blocks or failed conditions (ITSTATE is lost between steps, and a
            // skipped instruction does not count as a step, so it runs on into the next ones). Instructions whose
            // condition fails and IT itself change nothing here: they are not stepped on Unicorn; the others run
            // there unconditionally with ITSTATE cleared (a 16-bit flag-setting form then sets flags, so NZCV is
            // not compared inside IT blocks).
            bool conditional = (d.Flags & InstFlags.Conditional) != 0;
            bool ucStep = sync && !svc && d.Op != Op.Thumb_It && (!conditional || CondPassed(s->Cpsr, d.Cond));
            if (ucStep)
            {
                if (b.Thumb) W(UcArm.UC_ARM_REG_CPSR, (uint)R(UcArm.UC_ARM_REG_CPSR) & ~0x0600FC00u);
                _writes.Clear();
                e = uc_emu_start(_uc, d.Address | (b.Thumb ? 1u : 0), 0xFFFFFFFF, 0, 1);
                for (int r = 0; r < 16; r++) _ucR[r] = (uint)R(RegIds[r]);
                for (int r = 0; r < 32; r++) _ucD[r] = R(UcArm.UC_ARM_REG_D0 + r);
                _ucCpsr = (uint)R(UcArm.UC_ARM_REG_CPSR); _ucFpscr = (uint)R(UcArm.UC_ARM_REG_FPSCR);
                _ucBytes.Clear();
                foreach (var w in _writes) for (int k = 0; k < w.Size; k++) _ucBytes[w.Addr + k] = (byte)(w.Value >> (8 * k));
                for (int k = _writes.Count - 1; k >= 0; k--) _writes[k].Old.CopyTo(new Span<byte>(GuestMemory.Base + _writes[k].Addr, _writes[k].Size));
            }
            // ours: one instruction (its IT condition is already folded into the decoded form)
            var one = new Block { Start = d.Address, Thumb = b.Thumb, Insts = [d], End = d.Address + d.Size };
            CpuCore.Interpret(s, one);
            if (ucStep && e != 0 && _all) sync = false; // Unicorn faulted (e.g. memory it does not map): stop comparing this block
            if (ucStep && e == 0)
            {
                if (it != 0)
                {
                    _ucCpsr = (_ucCpsr & 0x07FFFFFF) | (s->Cpsr & 0xF8000000);
                    W(UcArm.UC_ARM_REG_CPSR, _ucCpsr);
                }
                var sb = new System.Text.StringBuilder();
                for (int r = 0; r < 15; r++) if (_ucR[r] != s->R[r]) sb.Append($" R{r}: uc={_ucR[r]:X8} us={s->R[r]:X8}");
                if ((_ucR[15] & ~1u) != s->R[15]) sb.Append($" PC: uc={_ucR[15]:X8} us={s->R[15]:X8}");
                if ((_ucCpsr & 0xF8000000) != (s->Cpsr & 0xF8000000)) sb.Append($" NZCVQ: uc={_ucCpsr >> 27:X} us={s->Cpsr >> 27:X}");
                for (int r = 0; r < 32; r++) if (_ucD[r] != s->D[r]) sb.Append($" D{r}: uc={_ucD[r]:X16} us={s->D[r]:X16}");
                if ((_ucFpscr & 0xF8000000) != (s->Fpscr & 0xF8000000)) sb.Append($" FPSCR: uc={_ucFpscr:X8} us={s->Fpscr:X8}");
                int memDiff = 0;
                foreach (var (a, v) in _ucBytes) if (GuestMemory.Base[a] != v && memDiff++ < 4) sb.Append($" MEM@{a:X8}: uc={v:X2} us={GuestMemory.Base[a]:X2}");
                if (sb.Length > 0)
                {
                    bool log;
                    if (_all)
                    {
                        _mismatches++;
                        _perOp[d.Op] = _perOp.GetValueOrDefault(d.Op) + 1;
                        int n = _perAddress.GetValueOrDefault(d.Address);
                        _perAddress[d.Address] = n + 1;
                        log = n < 3;
                    }
                    else log = _mismatches++ < 400;
                    if (log)
                    {
                        var rs = new System.Text.StringBuilder();
                        for (int r = 0; r < 16; r++) rs.Append($" {preR[r]:X}");
                        var ds = new System.Text.StringBuilder();
                        for (int r = 0; r < 32; r++) ds.Append($" {preD[r]:X}");
                        Log.Info($"LOCKSTEP MISMATCH {d.Address:X8} {d.Op} raw={d.Raw:X8} cpsr={preCpsr:X8} fpscr={preFpscr:X8} R=[{rs}] D=[{ds}]:{sb}");
                    }
                    sync = false; // resynchronise at the next block
                }
            }
            if (s->Exit != ExitReason.None || svc) return;
            if (i < b.Insts.Length - 1 && s->R[15] != b.Insts[i + 1].Address && !(d.Op == Op.Thumb_It)) return; // left the block (taken branch)
        }
    }
}
