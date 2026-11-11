using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// The ARM EHABI unwinder and personality routines of libc.suprx 3.74 (its __gnu_Unwind_* copy, the C++ personality
// it shares between __aeabi_unwind_cpp_pr0/1/2 and __snc_personality_v0, and __cxa type matching), for processes
// that run the HLE C library. Exception tables are the ones of the loaded modules (.ARM.exidx/.ARM.extab from their
// module info). The register set travels as a C# Vrs; a guest _Unwind_Context (a personality routine in guest code,
// the _Unwind_VRS_* exports) is libkernel's phase VRS layout in guest memory: +0 demand-save flags, +4 r0-r15,
// +0x44 prev_sp (_Unwind_GetCFA), +0x48 d0-d31.
public sealed partial class Libc
{
    /// <summary>The virtual register set of one frame: core registers and the 32 VFP double registers.</summary>
    internal sealed class Vrs
    {
        public readonly uint[] R = new uint[16];
        public readonly ulong[] D = new ulong[32];
        public Vrs Clone() { var v = new Vrs { PrevSp = PrevSp }; R.CopyTo(v.R, 0); D.CopyTo(v.D, 0); return v; }
        public uint PrevSp;
        public uint Sp { get => R[13]; set => R[13] = value; }
        public uint Pc { get => R[15]; set => R[15] = value; }

        internal const uint GuestSize = 0x148, GuestCore = 4, GuestPrevSp = 0x44, GuestVfp = 0x48;

        public void Store(uint ctx)
        {
            GuestMemory.W32(ctx, 0);
            for (int i = 0; i < 16; i++) GuestMemory.W32(ctx + GuestCore + (uint)i * 4, R[i]);
            GuestMemory.W32(ctx + GuestPrevSp, PrevSp);
            for (int i = 0; i < 32; i++) GuestMemory.Write(ctx + GuestVfp + (uint)i * 8, D[i]);
        }

        public static Vrs Load(uint ctx)
        {
            var v = new Vrs { PrevSp = GuestMemory.R32(ctx + GuestPrevSp) };
            for (int i = 0; i < 16; i++) v.R[i] = GuestMemory.R32(ctx + GuestCore + (uint)i * 4);
            for (int i = 0; i < 32; i++) v.D[i] = GuestMemory.Read<ulong>(ctx + GuestVfp + (uint)i * 8);
            return v;
        }

        /// <summary>The registers at the call of an unwinder entry point: r0-r14 and d0-d31 as they are, pc = lr (the
        /// return address into the caller), as the module's assembly entries save them.</summary>
        public static unsafe Vrs AtCall(HleContext ctx)
        {
            var v = new Vrs();
            for (int i = 0; i < 15; i++) v.R[i] = ctx.Cpu->R[i];
            v.R[15] = ctx.Cpu->R[14];
            for (int i = 0; i < 32; i++) v.D[i] = ctx.Cpu->D[i];
            return v;
        }

        /// <summary>Resumes the thread with these registers when the HLE call returns: the stub's "bx lr" goes to pc
        /// (lr is not part of an installed context: landing pads never rely on it, it was clobbered by the call that
        /// threw).</summary>
        public unsafe void Install(HleContext ctx)
        {
            for (int i = 0; i < 14; i++) ctx.Cpu->R[i] = R[i];
            ctx.Cpu->R[14] = R[15];
            for (int i = 0; i < 32; i++) ctx.Cpu->D[i] = D[i];
        }
    }

    // _Unwind_Reason_Code and _Unwind_State
    internal const int URC_OK = 0, URC_FOREIGN_EXCEPTION_CAUGHT = 1, URC_END_OF_STACK = 5, URC_HANDLER_FOUND = 6,
        URC_INSTALL_CONTEXT = 7, URC_CONTINUE_UNWIND = 8, URC_FAILURE = 9;
    internal const int US_VIRTUAL_UNWIND_FRAME = 0, US_UNWIND_FRAME_STARTING = 1, US_UNWIND_FRAME_RESUME = 2, US_FORCE_UNWIND = 8;

    // _Unwind_Control_Block (0x58 bytes): unwinder_cache 0x0C-0x1F (reserved1 = forced-unwind stop function,
    // reserved2 = personality routine, reserved3 = saved call-site address, reserved4 = stop argument), barrier_cache
    // 0x20-0x37, cleanup_cache 0x38-0x47, pr_cache 0x48-0x57 (fnstart, ehtp, additional).
    internal const uint UcbCleanup = 0x08, UcbStopFn = 0x0C, UcbPr = 0x10, UcbCallSite = 0x14, UcbStopArg = 0x18,
        UcbBarrierSp = 0x20, UcbBarrier1 = 0x24, UcbBarrier2 = 0x28, UcbBarrier3 = 0x2C, UcbBarrier4 = 0x30, UcbBarrier5 = 0x34,
        UcbCleanupCache = 0x38, UcbFnStart = 0x48, UcbEhtp = 0x4C, UcbAdditional = 0x50, UcbSize = 0x58;

    /// <summary>Personality routine values in <see cref="UcbPr"/> for the compact models (the guest never calls them).</summary>
    const uint PrCompact0 = 0xFFFFFFF0;

    const uint NidSncPersonality = 0xAE42C1D5;

    static uint Prel31(uint addr)
    {
        uint w = GuestMemory.R32(addr);
        int off = (int)(w << 1) >> 1;
        return addr + (uint)off;
    }

    // ------------------------------------------------------------------ exception table lookup

    /// <summary>
    /// Finds the exception table entry of the function containing <paramref name="returnAddress"/> - 2 (inside the
    /// call instruction) in the loaded modules and fills the UCB's pr_cache and personality. EXIDX_CANTUNWIND is
    /// END_OF_STACK; no module or no entry is FAILURE.
    /// </summary>
    int GetEitEntry(uint ucb, uint returnAddress)
    {
        uint ra = returnAddress - 2;
        GuestMemory.W32(ucb + UcbPr, 0);
        var m = K.Modules.Values.FirstOrDefault(x => (x.ExidxTop != 0 || _exidxOverride.ContainsKey(x)) && x.Segments.Values.Any(s => ra - s.Address < s.Size));
        if (m == null) return URC_FAILURE;
        var (top, end) = _exidxOverride.TryGetValue(m, out var o) ? o : (m.ExidxTop, m.ExidxEnd);
        uint lo = 0, n = (end - top) / 8;
        if (n == 0) return URC_FAILURE;
        uint hi = n;
        // the last entry whose function start is <= ra
        if (Prel31(top) > ra) return URC_FAILURE;
        while (hi - lo > 1)
        {
            uint mid = (lo + hi) / 2;
            if (Prel31(top + mid * 8) <= ra) lo = mid; else hi = mid;
        }
        uint e = top + lo * 8;
        GuestMemory.W32(ucb + UcbFnStart, Prel31(e));
        uint content = GuestMemory.R32(e + 4);
        if (content == 1) return URC_END_OF_STACK; // EXIDX_CANTUNWIND
        uint ehtp;
        if ((content & 0x80000000) != 0)
        {
            ehtp = e + 4;
            GuestMemory.W32(ucb + UcbAdditional, 1);
        }
        else
        {
            ehtp = Prel31(e + 4);
            GuestMemory.W32(ucb + UcbAdditional, 0);
        }
        GuestMemory.W32(ucb + UcbEhtp, ehtp);
        uint h = GuestMemory.R32(ehtp);
        if ((h & 0x80000000) != 0)
        {
            uint idx = (h >> 24) & 0xF;
            if (idx > 2) return URC_FAILURE;
            GuestMemory.W32(ucb + UcbPr, PrCompact0 + idx);
        }
        else GuestMemory.W32(ucb + UcbPr, Prel31(ehtp));
        return URC_OK;
    }

    /// <summary>The NID of the HLE stub at <paramref name="addr"/> (svc; bx lr; nid), 0 for guest code.</summary>
    static uint StubNid(uint addr)
    {
        addr &= ~1u;
        if (!GuestMemory.IsMapped(addr) || (GuestMemory.R32(addr) & 0xFF000000) != 0xEF000000 || GuestMemory.R32(addr + 4) != 0xE12FFF1E) return 0;
        return GuestMemory.R32(addr + 8);
    }

    /// <summary>Calls the frame's personality routine (UCB reserved2).</summary>
    int CallPersonality(int state, uint ucb, Vrs vrs)
    {
        uint pr = GuestMemory.R32(ucb + UcbPr);
        if (pr - PrCompact0 <= 2) return CommonPersonality(state, ucb, vrs, (int)(pr - PrCompact0));
        if (StubNid(pr) == NidSncPersonality) return CommonPersonality(state, ucb, vrs, 4);
        // a personality routine in guest code gets a guest _Unwind_Context
        var t = GuestThread.Current;
        if (t == null || pr == 0) return URC_FAILURE;
        uint ctx = HeapAlloc(Vrs.GuestSize, HeapAlign);
        if (ctx == 0) return URC_FAILURE;
        try
        {
            vrs.Store(ctx);
            int r = (int)t.CallGuest(pr, (uint)state, ucb, ctx);
            var v = Vrs.Load(ctx);
            v.R.CopyTo(vrs.R, 0);
            v.D.CopyTo(vrs.D, 0);
            return r;
        }
        finally { free(ctx); }
    }

    internal int FindFrame(uint ucb, uint returnAddress) => GetEitEntry(ucb, returnAddress);
    internal int FramePersonality(int state, uint ucb, Vrs vrs) => CallPersonality(state, ucb, vrs);
    internal uint AllocScratch(uint size) { uint p = HeapAlloc(size, HeapAlign); if (p != 0) GuestMemory.Fill(p, 0, size); return p; }
    internal void FreeScratch(uint p) => free(p);
    internal void AbortProcess() => abort();

    // ------------------------------------------------------------------ unwind instructions

    /// <summary>Reads the frame's unwind instruction bytes (EHABI 10.2/10.3) from the exception table entry.</summary>
    sealed class OpReader
    {
        uint _data, _next;
        int _bytesLeft, _wordsLeft;

        public OpReader(uint data, uint next, int bytesLeft, int wordsLeft)
        {
            _data = data; _next = next; _bytesLeft = bytesLeft; _wordsLeft = wordsLeft;
        }

        /// <summary>The next byte; 0xB0 (finish) once the instructions are used up.</summary>
        public uint Next()
        {
            if (_bytesLeft == 0)
            {
                if (_wordsLeft == 0) return 0xB0;
                _data = GuestMemory.R32(_next);
                _next += 4;
                _wordsLeft--;
                _bytesLeft = 4;
            }
            _bytesLeft--;
            uint b = _data >> 24;
            _data <<= 8;
            return b;
        }
    }

    /// <summary>
    /// Executes one frame's unwind instructions on <paramref name="vrs"/>. Returns false where the module returns
    /// _URC_FAILURE: refuse-to-unwind (0x8000), the spare/reserved encodings, iWMMX pops, and a finish (0xB0) with
    /// neither pc nor lr popped. <paramref name="pcFromLr"/> tells that pc was taken from lr at the finish.
    /// </summary>
    static bool ExecuteUnwind(Vrs vrs, OpReader ops, out bool pcFromLr)
    {
        bool pcPopped = false, lrPopped = false;
        pcFromLr = false;
        while (true)
        {
            uint op = ops.Next();
            if (op == 0xB0)
            {
                if (pcPopped) return true;
                if (!lrPopped) return false;
                vrs.Pc = vrs.R[14];
                pcFromLr = true;
                return true;
            }
            if (op <= 0x3F) { vrs.Sp += ((op & 0x3F) << 2) + 4; continue; }
            if (op <= 0x7F) { vrs.Sp -= ((op & 0x3F) << 2) + 4; continue; }
            if (op <= 0x8F)
            {
                uint mask = ((op & 0xF) << 12 | ops.Next() << 4);
                if (mask == 0) return false; // refuse to unwind
                PopCore(vrs, mask);
                if ((mask & 0x8000) != 0) pcPopped = true;
                if ((mask & 0x4000) != 0) lrPopped = true;
                continue;
            }
            if (op <= 0x9F)
            {
                uint r = op & 0xF;
                if (r == 13 || r == 15) return false;
                vrs.Sp = vrs.R[r];
                continue;
            }
            if (op <= 0xAF)
            {
                uint mask = ((1u << (int)((op & 7) + 1)) - 1) << 4;
                if ((op & 8) != 0) { mask |= 0x4000; lrPopped = true; }
                PopCore(vrs, mask);
                continue;
            }
            switch (op)
            {
                case 0xB1:
                {
                    uint mask = ops.Next();
                    if (mask == 0 || (mask & 0xF0) != 0) return false;
                    PopCore(vrs, mask);
                    continue;
                }
                case 0xB2:
                {
                    uint v = 0;
                    for (int shift = 0; ; shift += 7)
                    {
                        uint b = ops.Next();
                        v |= (b & 0x7F) << shift;
                        if ((b & 0x80) == 0) break;
                    }
                    vrs.Sp += 0x204 + (v << 2);
                    continue;
                }
                case 0xB3:
                {
                    uint b = ops.Next();
                    PopVfp(vrs, (int)(b >> 4), (int)(b & 0xF) + 1, true);
                    continue;
                }
                case 0xC8:
                case 0xC9:
                {
                    uint b = ops.Next();
                    PopVfp(vrs, (int)(b >> 4) + (op == 0xC8 ? 16 : 0), (int)(b & 0xF) + 1, false);
                    continue;
                }
            }
            if (op >= 0xB8 && op <= 0xBF) { PopVfp(vrs, 8, (int)(op & 7) + 1, true); continue; }
            if (op >= 0xD0 && op <= 0xD7) { PopVfp(vrs, 8, (int)(op & 7) + 1, false); continue; }
            return false; // B4-B7, iWMMX C0-C7, CA-CF, D8-FF
        }
    }

    /// <summary>Pops the core registers of <paramref name="mask"/> (bit n = rn) from vsp upwards; sp, when popped,
    /// becomes the loaded value.</summary>
    static void PopCore(Vrs vrs, uint mask)
    {
        uint vsp = vrs.Sp;
        bool spPopped = false;
        uint newSp = 0;
        for (int i = 0; i < 16; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            uint v = GuestMemory.R32(vsp);
            vsp += 4;
            if (i == 13) { spPopped = true; newSp = v; } else vrs.R[i] = v;
        }
        vrs.Sp = spPopped ? newSp : vsp;
    }

    /// <summary>Pops <paramref name="count"/> double registers from d<paramref name="first"/>; FSTMFDX format
    /// (<paramref name="fstmx"/>) has a padding word after them.</summary>
    static void PopVfp(Vrs vrs, int first, int count, bool fstmx)
    {
        uint vsp = vrs.Sp;
        for (int i = 0; i < count && first + i < 32; i++, vsp += 8) vrs.D[first + i] = GuestMemory.Read<ulong>(vsp);
        vrs.Sp = vsp + (fstmx ? 4u : 0u);
    }

    /// <summary>The unwind instructions of the frame described by the UCB's pr_cache, for its personality model.</summary>
    static OpReader FrameOps(uint ucb, int idx)
    {
        uint ehtp = GuestMemory.R32(ucb + UcbEhtp);
        if (idx == 4)
        {
            uint w = GuestMemory.R32(ehtp + 4);
            return new OpReader(w << 8, ehtp + 8, 3, (int)(w >> 24));
        }
        uint h = GuestMemory.R32(ehtp);
        return idx == 0 ? new OpReader(h << 8, ehtp + 4, 3, 0) : new OpReader(h << 16, ehtp + 4, 2, (int)((h >> 16) & 0xFF));
    }

    // ------------------------------------------------------------------ the personality routine

    /// <summary>
    /// The module's common personality routine (state, ucb, context, model): 0-2 the compact models with ARM-defined
    /// descriptors (__aeabi_unwind_cpp_pr0/1/2), 4 __snc_personality_v0 (an LSDA in the GCC format after the unwind
    /// instructions: lpstart and ttype encoding bytes, the type table offset when the ttype encoding is 0, the call-site
    /// encoding byte, then call sites of four ULEB128s and the action table). Returns HANDLER_FOUND (phase 1),
    /// INSTALL_CONTEXT (phase 2: landing pad in pc, the UCB in r0 and the selector in r1), CONTINUE_UNWIND after
    /// unwinding the frame, or FAILURE.
    /// </summary>
    int CommonPersonality(int state, uint ucb, Vrs vrs, int idx)
    {
        if (state is not (US_VIRTUAL_UNWIND_FRAME or US_UNWIND_FRAME_STARTING or US_UNWIND_FRAME_RESUME)) return URC_FAILURE;
        bool callUnexpected = false;
        if (idx == 4)
        {
            int r = SncFrame(state, ucb, vrs);
            if (r != URC_CONTINUE_UNWIND) return r;
        }
        else if ((GuestMemory.R32(ucb + UcbAdditional) & 1) == 0)
        {
            int r = ArmDescriptors(state, ucb, vrs, idx, ref callUnexpected);
            if (r != URC_CONTINUE_UNWIND) return r;
        }
        vrs.PrevSp = vrs.Sp;
        if (!ExecuteUnwind(vrs, FrameOps(ucb, idx), out bool pcFromLr)) return URC_FAILURE;
        if (!callUnexpected) return URC_CONTINUE_UNWIND;
        // a violated exception specification without a landing pad: __cxa_call_unexpected(ucb) in this frame's caller
        vrs.R[0] = ucb;
        if (!pcFromLr) vrs.R[14] = vrs.Pc;
        vrs.Pc = CxaCallUnexpectedStub;
        return URC_INSTALL_CONTEXT;
    }

    static uint Uleb(ref uint p)
    {
        uint v = 0;
        for (int shift = 0; ; shift += 7)
        {
            uint b = GuestMemory.Read<byte>(p++);
            if (shift < 32) v |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
        }
    }

    static int Sleb(ref uint p)
    {
        uint v = 0;
        int shift = 0;
        uint b;
        do
        {
            b = GuestMemory.Read<byte>(p++);
            if (shift < 32) v |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        if (shift < 32 && (b & 0x40) != 0) v |= ~0u << shift;
        return (int)v;
    }

    /// <summary>Phase 2 installs a landing pad: pc, r0 = UCB, r1 = selector; the catch parameter in barrier_cache[1].</summary>
    static int Install(uint ucb, Vrs vrs, uint landing, int selector, uint adjusted)
    {
        GuestMemory.W32(ucb + UcbBarrier1, adjusted);
        vrs.R[0] = ucb;
        vrs.Pc = landing | (vrs.Pc & 1);
        vrs.R[1] = (uint)selector;
        return URC_INSTALL_CONTEXT;
    }

    /// <summary>__snc_personality_v0's search of the frame's call-site table (see <see cref="CommonPersonality"/>):
    /// CONTINUE_UNWIND when the frame has nothing to run for this exception.</summary>
    int SncFrame(int state, uint ucb, Vrs vrs)
    {
        uint ehtp = GuestMemory.R32(ucb + UcbEhtp);
        uint lsda = ehtp + 8 + 4 * (GuestMemory.R32(ehtp + 4) >> 24);
        uint p = lsda + 2, ttypeBase = 0;
        if (GuestMemory.Read<byte>(lsda + 1) == 0) { uint off = Uleb(ref p); ttypeBase = p + off; }
        p++; // call-site encoding
        uint csLen = Uleb(ref p), csEnd = p + csLen;
        uint fnStart = GuestMemory.R32(ucb + UcbFnStart), pc = vrs.Pc - 2;
        while (p < csEnd)
        {
            uint start = Uleb(ref p), len = Uleb(ref p), lp = Uleb(ref p), action = Uleb(ref p);
            if (pc < fnStart + start) break;
            if (pc >= fnStart + start + len) continue;
            if (lp == 0) return URC_CONTINUE_UNWIND;
            uint landing = (fnStart & ~1u) + lp;
            if (action == 0)
                return state == US_VIRTUAL_UNWIND_FRAME ? URC_CONTINUE_UNWIND : Install(ucb, vrs, landing, 0, 0);
            uint a = csEnd + action - 1;
            bool cleanup = false;
            while (true)
            {
                uint rec = a;
                int filter = Sleb(ref rec);
                uint dispAt = rec;
                int disp = Sleb(ref rec);
                if (filter == 0) cleanup = true;
                else if (filter > 0)
                {
                    uint entry = ttypeBase - (uint)filter * 4, v = GuestMemory.R32(entry);
                    uint adjusted = ucb + UcbSize; // catch (...): the thrown object
                    int m = v == 0 ? 1 : TypeMatch(ucb, entry + v, false, out adjusted);
                    if (m == 2 && state == US_UNWIND_FRAME_STARTING)
                    {
                        GuestMemory.W32(ucb + UcbBarrier5, adjusted);
                        adjusted = ucb + UcbBarrier5;
                    }
                    if (m != 0)
                        return state == US_VIRTUAL_UNWIND_FRAME ? URC_HANDLER_FOUND : Install(ucb, vrs, landing, filter, adjusted);
                }
                else
                {
                    // exception specification: a list of types (self-relative words, 0-terminated); thrown outside it
                    uint list = ttypeBase + (uint)(-filter) * 4 - 4, e = list;
                    uint count = 0;
                    bool allowed = false;
                    for (; GuestMemory.R32(e) is var v && v != 0; e += 4, count++)
                        if (TypeMatch(ucb, e + v, false, out _) != 0) { allowed = true; break; }
                    if (!allowed)
                    {
                        if (state != US_UNWIND_FRAME_STARTING) return URC_HANDLER_FOUND;
                        GuestMemory.W32(ucb + UcbBarrier2, count);
                        GuestMemory.W32(ucb + UcbBarrier3, 0);
                        GuestMemory.W32(ucb + UcbBarrier4, 4);
                        GuestMemory.W32(ucb + UcbBarrier5, list);
                        return Install(ucb, vrs, landing, filter, 0);
                    }
                }
                if (disp == 0) break;
                a = dispAt + (uint)disp;
            }
            if (cleanup && state != US_VIRTUAL_UNWIND_FRAME) return Install(ucb, vrs, landing, 0, 0);
            return URC_CONTINUE_UNWIND;
        }
        // no call site covers pc: std::terminate (the module calls __cxa_call_terminate)
        CallTerminate(ucb);
        return URC_FAILURE;
    }

    /// <summary>The ARM-defined descriptors after a compact entry's unwind instructions (EHABI 9.2): 16-bit (model
    /// 1) or 32-bit (model 2) length/offset pairs whose low bits give cleanup, catch or exception specification.</summary>
    int ArmDescriptors(int state, uint ucb, Vrs vrs, int idx, ref bool callUnexpected)
    {
        uint ehtp = GuestMemory.R32(ucb + UcbEhtp);
        uint d = state == US_UNWIND_FRAME_RESUME ? GuestMemory.R32(ucb + UcbCleanupCache)
            : ehtp + 4 + (idx == 0 ? 0 : 4 * ((GuestMemory.R32(ehtp) >> 16) & 0xFF));
        uint fnStart = GuestMemory.R32(ucb + UcbFnStart), pc = vrs.Pc;
        while (true)
        {
            uint len, off;
            if (idx == 2)
            {
                len = GuestMemory.R32(d);
                if (len == 0) return URC_CONTINUE_UNWIND;
                off = GuestMemory.R32(d + 4);
                d += 8;
            }
            else
            {
                len = GuestMemory.Read<ushort>(d);
                if (len == 0) return URC_CONTINUE_UNWIND;
                off = GuestMemory.Read<ushort>(d + 2);
                d += 4;
            }
            switch ((len & 1) | ((off & 1) << 1))
            {
                case 0: // cleanup: landing pad
                {
                    uint start = fnStart + off;
                    if (state != US_VIRTUAL_UNWIND_FRAME && start <= pc && pc < start + len)
                    {
                        GuestMemory.W32(ucb + UcbCleanupCache, d + 4);
                        if (!BeginCleanup(ucb)) return URC_FAILURE;
                        vrs.Pc = Prel31(d) | (vrs.Pc & 1);
                        return URC_INSTALL_CONTEXT;
                    }
                    d += 4;
                    break;
                }
                case 1: // catch: landing pad (bit 31: by reference), type
                {
                    uint start = fnStart + off;
                    bool inRange = start <= pc && pc < start + len - 1;
                    if (state == US_VIRTUAL_UNWIND_FRAME)
                    {
                        if (inRange)
                        {
                            uint rtti = GuestMemory.R32(d + 4);
                            if (rtti == 0xFFFFFFFE) return URC_FAILURE;
                            uint adjusted = ucb + UcbSize;
                            int m = rtti == 0xFFFFFFFF ? 1 : TypeMatch(ucb, rtti, (GuestMemory.R32(d) & 0x80000000) != 0, out adjusted);
                            if (m != 0)
                            {
                                if (m == 2) { GuestMemory.W32(ucb + UcbBarrier2, adjusted); adjusted = ucb + UcbBarrier2; }
                                GuestMemory.W32(ucb + UcbBarrierSp, vrs.Sp);
                                GuestMemory.W32(ucb + UcbBarrier1, adjusted);
                                GuestMemory.W32(ucb + UcbBarrier3, d);
                                return URC_HANDLER_FOUND;
                            }
                        }
                    }
                    else if (GuestMemory.R32(ucb + UcbBarrierSp) == vrs.Sp && GuestMemory.R32(ucb + UcbBarrier3) == d)
                    {
                        vrs.Pc = Prel31(d) | (vrs.Pc & 1);
                        vrs.R[0] = ucb;
                        return URC_INSTALL_CONTEXT;
                    }
                    d += 8;
                    break;
                }
                case 2: // exception specification: count (bit 31: landing pad follows), types[, landing pad]
                {
                    uint w = GuestMemory.R32(d), count = w & 0x7FFFFFFF;
                    uint start = fnStart + off - 1;
                    if (state == US_VIRTUAL_UNWIND_FRAME)
                    {
                        if (start <= pc && pc < start + len)
                        {
                            uint i = 0;
                            for (; i < count; i++)
                                if (TypeMatch(ucb, GuestMemory.R32(d + 4 + i * 4), false, out _) != 0) break;
                            if (i == count)
                            {
                                GuestMemory.W32(ucb + UcbBarrierSp, vrs.Sp);
                                GuestMemory.W32(ucb + UcbBarrier1, 0);
                                GuestMemory.W32(ucb + UcbBarrier3, d);
                                return URC_HANDLER_FOUND;
                            }
                        }
                    }
                    else if (GuestMemory.R32(ucb + UcbBarrierSp) == vrs.Sp && GuestMemory.R32(ucb + UcbBarrier3) == d)
                    {
                        GuestMemory.W32(ucb + UcbBarrier2, count);
                        GuestMemory.W32(ucb + UcbBarrier3, 0);
                        GuestMemory.W32(ucb + UcbBarrier4, 4);
                        GuestMemory.W32(ucb + UcbBarrier5, d + 4);
                        if ((w & 0x80000000) == 0) callUnexpected = true;
                        else
                        {
                            vrs.R[0] = ucb;
                            vrs.Pc = Prel31(d + 4 + count * 4) | (vrs.Pc & 1);
                            return URC_INSTALL_CONTEXT;
                        }
                    }
                    d += 4 + count * 4 + ((w & 0x80000000) != 0 ? 4u : 0u);
                    break;
                }
                default:
                    return URC_FAILURE;
            }
        }
    }

    // ------------------------------------------------------------------ phases 1 and 2

    /// <summary>
    /// _Unwind_RaiseException: phase 1 searches the frames above <paramref name="entry"/> for a handler (on a copy of
    /// the registers), phase 2 runs the cleanups down to it and installs its landing pad. Returns only on failure
    /// (no handler, an unwinding error): FAILURE / END_OF_STACK.
    /// </summary>
    internal int RaiseException(HleContext ctx, uint ucb, Vrs entry)
    {
        var p1 = entry.Clone();
        int r;
        do
        {
            r = GetEitEntry(ucb, p1.Pc);
            if (r != URC_OK) break;
            r = CallPersonality(US_VIRTUAL_UNWIND_FRAME, ucb, p1);
        } while (r == URC_CONTINUE_UNWIND);
        if (r != URC_HANDLER_FOUND) return URC_FAILURE;
        return UnwindPhase2(ctx, ucb, entry, false);
    }

    /// <summary>Phase 2 from <paramref name="vrs"/>: each frame's personality with UNWIND_FRAME_STARTING (or RESUME for
    /// the frame <see cref="ResumeUnwind"/> continues) until one installs a landing pad.</summary>
    int UnwindPhase2(HleContext ctx, uint ucb, Vrs vrs, bool resume)
    {
        while (true)
        {
            int r;
            if (!resume)
            {
                if (GetEitEntry(ucb, vrs.Pc) != URC_OK) return URC_FAILURE;
                GuestMemory.W32(ucb + UcbCallSite, vrs.Pc);
                r = CallPersonality(US_UNWIND_FRAME_STARTING, ucb, vrs);
            }
            else r = CallPersonality(US_UNWIND_FRAME_RESUME, ucb, vrs);
            resume = false;
            if (r == URC_CONTINUE_UNWIND) continue;
            if (r != URC_INSTALL_CONTEXT) return URC_FAILURE;
            vrs.Install(ctx);
            return URC_INSTALL_CONTEXT;
        }
    }

    /// <summary>Phase 2 of a forced unwind: every frame is unwound with US_FORCE_UNWIND, the stop function
    /// (UCB reserved1) is asked before each, cleanups run as usual.</summary>
    int UnwindPhase2Forced(HleContext ctx, uint ucb, Vrs vrs, bool resume)
    {
        uint stop = GuestMemory.R32(ucb + UcbStopFn), arg = GuestMemory.R32(ucb + UcbStopArg);
        var t = GuestThread.Current;
        if (t == null) return URC_FAILURE;
        uint gctx = HeapAlloc(Vrs.GuestSize, HeapAlign);
        try
        {
            while (true)
            {
                int er = resume ? URC_OK : GetEitEntry(ucb, vrs.Pc);
                int action = US_FORCE_UNWIND | (resume ? US_UNWIND_FRAME_RESUME : US_UNWIND_FRAME_STARTING);
                if (er != URC_OK) action |= 0x10; // _US_END_OF_STACK
                if (!resume) GuestMemory.W32(ucb + UcbCallSite, vrs.Pc);
                vrs.Store(gctx);
                // stop(version 1, actions, exception class, ucb, context, stop argument)
                int sr = (int)t.CallGuest(stop, 1, (uint)action, GuestMemory.R32(ucb), GuestMemory.R32(ucb + 4), ucb, gctx, arg);
                if (sr != URC_OK || er != URC_OK) return URC_FAILURE;
                int r = CallPersonality(action & ~0x10, ucb, vrs);
                resume = false;
                if (r == URC_CONTINUE_UNWIND) continue;
                if (r != URC_INSTALL_CONTEXT) return URC_FAILURE;
                vrs.Install(ctx);
                return URC_INSTALL_CONTEXT;
            }
        }
        finally { free(gctx); }
    }

    /// <summary>_Unwind_Resume (from a cleanup landing pad): the frame that ran the cleanup is finished by its
    /// personality (RESUME), then phase 2 goes on; a forced unwind stays forced.</summary>
    internal int ResumeUnwind(HleContext ctx, uint ucb, Vrs entry)
    {
        GuestMemory.W32(ucb + UcbCallSite, 0);
        return GuestMemory.R32(ucb + UcbStopFn) != 0 ? UnwindPhase2Forced(ctx, ucb, entry, true) : UnwindPhase2(ctx, ucb, entry, true);
    }

    internal int ForcedUnwind(HleContext ctx, uint ucb, uint stop, uint arg, Vrs entry)
    {
        GuestMemory.W32(ucb + UcbStopFn, stop);
        GuestMemory.W32(ucb + UcbStopArg, arg);
        return UnwindPhase2Forced(ctx, ucb, entry, false);
    }

    // ------------------------------------------------------------------ type matching

    static ReadOnlySpan<byte> ArmCxxClass => "ARM\0C++\0"u8;
    static ReadOnlySpan<byte> ArmCxxDependentClass => "ARM\0C++\u0001"u8;

    static bool ClassIs(uint ucb, ReadOnlySpan<byte> cls, int n)
    {
        for (int i = 0; i < n; i++) if (GuestMemory.Read<byte>(ucb + (uint)i) != cls[i]) return false;
        return true;
    }

    internal static bool IsNativeException(uint ucb) => ClassIs(ucb, ArmCxxClass, 7);
    static bool IsDependentException(uint ucb) => ClassIs(ucb, ArmCxxDependentClass, 8);

    /// <summary>The exception header (0x80 bytes before the thrown object) of a native UCB; a dependent exception's
    /// first word is its primary object.</summary>
    static uint HeaderOf(uint ucb) => IsDependentException(ucb) ? GuestMemory.R32(ucb - 0x28) - 0x80 : ucb - 0x28;

    /// <summary>type_info equality as the module compares: the name pointers.</summary>
    static bool TiEq(uint a, uint b) => a != 0 && b != 0 && GuestMemory.R32(a + 4) == GuestMemory.R32(b + 4);

    /// <summary>The type_info of a type_info object's dynamic type (its vtable's slot -1).</summary>
    static uint TiKind(uint ti) => ti == 0 ? 0 : GuestMemory.R32(GuestMemory.R32(ti) - 4);

    /// <summary>
    /// __cxa type matching of the module (UCB, catch type, catch by reference): 0 no match, 1 a match with the
    /// thrown object's address in <paramref name="adjusted"/>, 2 a match through a pointer whose value had to be
    /// adjusted to a base class (<paramref name="adjusted"/> is then the adjusted pointer value). Foreign exceptions
    /// match nothing. Pointers compare their cv-qualification level by level (a qualifier added below a level that
    /// is not const fails); nullptr_t matches any pointer or pointer to member with the null value of its kind;
    /// catch (void*) takes any object pointer; classes match a unique public base.
    /// </summary>
    internal int TypeMatch(uint ucb, uint catchTi, bool byRef, out uint adjusted)
    {
        adjusted = 0;
        if (!IsNativeException(ucb)) return 0;
        uint h = HeaderOf(ucb);
        uint thrown = GuestMemory.R32(h + 8), obj = h + 0x80, cur = obj;
        int depth = 0;
        bool allConst = true;
        var r = Rtti;
        while (true)
        {
            if (TiEq(thrown, catchTi)) { adjusted = obj; return 1; }
            uint tk = TiKind(thrown), ck = TiKind(catchTi);
            bool cPtr = TiEq(ck, r.PointerTi), cPtm = TiEq(ck, r.PtmTi);
            if ((cPtr || cPtm) && TiEq(thrown, r.NullptrTi))
            {
                uint nullValue = r.NullPtr;
                if (cPtm && !TiEq(TiKind(GuestMemory.R32(catchTi + 0xC)), r.FunctionTi)) nullValue = r.NullPtm;
                adjusted = nullValue;
                return 1;
            }
            bool tPtr = TiEq(tk, r.PointerTi);
            if ((tPtr || cPtr) && !TiEq(tk, ck)) return 0;
            if (tPtr)
            {
                depth++;
                uint tq = GuestMemory.R32(thrown + 8) & 3, cflags = GuestMemory.R32(catchTi + 8), cq = cflags & 3;
                if ((tq & ~cq) != 0) return 0;
                if ((cq & ~tq) != 0 && !allConst) return 0;
                if ((cflags & 1) == 0) allConst = false;
                thrown = GuestMemory.R32(thrown + 0xC);
                catchTi = GuestMemory.R32(catchTi + 0xC);
                if (cur != 0) cur = GuestMemory.R32(cur);
                continue;
            }
            if (depth == 1 && TiEq(catchTi, r.VoidTi))
            {
                if (byRef) return 0;
                adjusted = obj;
                return 1;
            }
            if (depth > 1 || (depth == 1 && byRef)) return 0;
            if (!TiEq(tk, r.SiTi) && !TiEq(tk, r.VmiTi)) return 0;
            if (!FindPublicBase(thrown, catchTi, cur, out uint sub)) return 0;
            adjusted = sub;
            return depth != 0 ? 2 : 1;
        }
    }

    /// <summary>The address of the unique public base subobject of type <paramref name="target"/> in an object of
    /// type <paramref name="type"/> at <paramref name="obj"/> (0: only the type relation is checked).</summary>
    bool FindPublicBase(uint type, uint target, uint obj, out uint sub)
    {
        var found = new HashSet<uint>();
        bool any = false;
        void Walk(uint t, uint at, bool pub, int level)
        {
            if (level > 64) return;
            if (TiEq(t, target))
            {
                if (pub) { found.Add(at); any = true; }
                return;
            }
            uint k = TiKind(t);
            if (TiEq(k, Rtti.SiTi)) { Walk(GuestMemory.R32(t + 8), at, pub, level + 1); return; }
            if (!TiEq(k, Rtti.VmiTi)) return;
            uint n = GuestMemory.R32(t + 0xC);
            for (uint i = 0; i < n; i++)
            {
                uint bt = GuestMemory.R32(t + 0x10 + i * 8), of = GuestMemory.R32(t + 0x14 + i * 8);
                int offset = (int)of >> 8;
                uint baseAt = 0;
                if (at != 0)
                {
                    if ((of & 1) != 0) { uint vptr = GuestMemory.R32(at); baseAt = at + GuestMemory.R32(vptr + (uint)offset); }
                    else baseAt = at + (uint)offset;
                }
                Walk(bt, baseAt, pub && (of & 2) != 0, level + 1);
            }
        }
        Walk(type, obj, true, 0);
        sub = found.FirstOrDefault();
        return any && found.Count == 1;
    }
}
