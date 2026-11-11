using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// SceLibstdcxx C++ exceptions on the HLE C library: small Thumb functions with ARM EHABI unwind tables (a compact
/// pr0 frame and __snc_personality_v0 frames with GCC-format LSDAs, as SNC emits them) throw and catch through the
/// HLE __cxa_* entry points on a guest thread; the type matching is checked directly on exception headers.
/// </summary>
[Collection("guest")]
public sealed class CxxExceptionHleTests(GuestMemoryFixture fx)
{
    /// <summary>A minimal Thumb assembler for the test functions.</summary>
    sealed class Code(uint at)
    {
        public uint Pc = at;
        public void H(ushort v)
        {
            GuestMemory.Write(Pc, v);
            Pc += 2;
        }
        public void Push() => H(0xB510);            // push {r4, lr}
        public void Pop() => H(0xBD10);             // pop {r4, pc}
        public void MovR4R0() => H(0x0004);         // movs r4, r0
        public void MovR0R4() => H(0x0020);         // movs r0, r4
        public void Imm(int rd, uint v)             // movw/movt rd, #v
        {
            Mov(0xF240, rd, v & 0xFFFF);
            Mov(0xF2C0, rd, v >> 16);
        }
        void Mov(ushort op, int rd, uint imm16)
        {
            H((ushort)(op | ((imm16 >> 11) & 1) << 10 | (imm16 >> 12)));
            H((ushort)(((imm16 >> 8) & 7) << 12 | (uint)rd << 8 | (imm16 & 0xFF)));
        }
        public void Call(uint target)
        {
            Imm(3, target);
            H(0x4798);
        } // blx r3
        public void MovsR0(byte v) => H((ushort)(0x2000 | v));
        public void MovsR0Imm(int rd, byte v) => H((ushort)(0x2000 | rd << 8 | v));
        public void LdrR4R0() => H(0x6804);         // ldr r4, [r0]
        public void LdrR0R4() => H(0x6820);         // ldr r0, [r4]
        public void StrR4R0() => H(0x6004);         // str r4, [r0]
    }

    sealed class World : IDisposable
    {
        public readonly Emulator Emu;
        public readonly Libc Libc;
        readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-cxx-" + Guid.NewGuid().ToString("N"));
        public uint Base, Stubs;
        uint _nextStub;
        readonly Dictionary<uint, uint> _stubs = [];
        readonly LoadedModule _module = new() { Name = "cxx-test", Uid = 0x7777 };
        readonly List<(uint Fn, uint Entry, bool Inline)> _exidx = [];
        private uint _extab;
        private readonly uint _counter;

        public uint Counter => _counter;

        public World(GuestMemoryFixture fx)
        {
            Directory.CreateDirectory(_root);
            Emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
            Libc = Emu.Hle.Modules.OfType<Libc>().Single();
            Base = Emu.Memory.Alloc(0x4000, "cxx test code");
            GuestMemory.Fill(Base, 0, 0x4000);
            Stubs = Emu.Memory.Alloc(0x1000, "cxx test stubs");
            _nextStub = Stubs;
            _extab = Base + 0x2000;
            _counter = Base + 0x3F00;
            _module.Segments[0] = new SegmentInfo(Base, Base, 0x4000);
            Emu.Kernel.Modules[_module.Uid] = _module;
        }

        public uint Stub(string name, string library = "SceLibstdcxx")
        {
            uint nid = Core.Hle.NidDatabase.LookupName(name).Single(n => n.Library == library).Nid;
            if (_stubs.TryGetValue(nid, out uint a))
            {
                return a;
            }

            a = _nextStub;
            _nextStub += 16;
            Linker.WriteHleStub(a, Emu.Hle.GetHleFunctionIndex(nid, 0, library), nid);
            return _stubs[nid] = a;
        }

        static void Prel31(uint at, uint target) => GuestMemory.W32(at, (target - at) & 0x7FFFFFFF);

        /// <summary>A compact pr0 entry: the three unwind instruction bytes inline.</summary>
        public void Compact(uint fn, uint ops) => _exidx.Add((fn, 0x80000000 | ops, true));

        /// <summary>A __snc_personality_v0 entry: unwind instructions "pop {r4, lr}; finish", then the LSDA with call
        /// sites (start, length, landing pad, action) and catch types (filter n = types[n - 1]).</summary>
        public void Snc(uint fn, (uint Start, uint Len, uint Lp, uint Action)[] sites, byte[] actions, uint[] types)
        {
            uint e = _extab;
            Prel31(e, Stub("__snc_personality_v0"));
            GuestMemory.W32(e + 4, 0x00A8B0B0);
            var lsda = new List<byte> { 0xFF, 0x00 };
            var cs = new List<byte>();
            foreach (var s in sites)
            {
                cs.Add((byte)s.Start);
                cs.Add((byte)s.Len);
                cs.Add((byte)s.Lp);
                cs.Add((byte)s.Action);
            }
            // ttype offset (one byte, filled below), call-site encoding, length, sites, actions
            int offAt = lsda.Count;
            lsda.Add(0);
            int afterOff = lsda.Count;
            lsda.Add(0x01);
            lsda.Add((byte)cs.Count);
            lsda.AddRange(cs);
            lsda.AddRange(actions);
            uint lsdaAddr = e + 8;
            while ((lsdaAddr + (uint)lsda.Count) % 4 != 0)
            {
                lsda.Add(0);
            }

            uint typesAt = lsdaAddr + (uint)lsda.Count, ttypeBase = typesAt + (uint)types.Length * 4;
            lsda[offAt] = (byte)(ttypeBase - (lsdaAddr + (uint)afterOff));
            for (int i = 0; i < lsda.Count; i++)
            {
                GuestMemory.Write(lsdaAddr + (uint)i, lsda[i]);
            }
            // filter n is at ttypeBase - 4n, self-relative
            for (int n = 1; n <= types.Length; n++)
            {
                uint at = ttypeBase - (uint)n * 4;
                GuestMemory.W32(at, types[n - 1] == 0 ? 0 : types[n - 1] - at);
            }
            _extab = (ttypeBase + 15) & ~15u;
            _exidx.Add((fn, e, false));
        }

        public void Finish()
        {
            uint t = Base + 0x3000;
            _module.ExidxTop = t;
            foreach (var (fn, entry, inline) in _exidx.OrderBy(x => x.Fn))
            {
                Prel31(t, fn);
                if (inline)
                {
                    GuestMemory.W32(t + 4, entry);
                }
                else
                {
                    Prel31(t + 4, entry);
                }

                t += 8;
            }
            _module.ExidxEnd = t;
            Emu.Code.Invalidate(0, uint.MaxValue);
        }

        public uint Run(uint fn, params uint[] args)
        {
            uint result = 0;
            Exception? failure = null;
            var th = Emu.Kernel.CreateThread("cxx-test", 0, 0x10000100, 0x10000, 0, 0x70000);
            th.HostBody = t =>
            {
                try
                {
                    result = t.CallGuest(fn | 1, args);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            };
            th.Start(0, 0);
            Assert.True(th.WaitForExit(60_000), "test thread did not finish");
            Emu.Kernel.DeleteThread(th);
            if (failure != null)
            {
                throw new Xunit.Sdk.XunitException(failure.ToString());
            }

            return result;
        }

        public void Dispose()
        {
            Emu.Kernel.Modules.TryRemove(_module.Uid, out _);
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException) { }
        }
    }

    const uint TiInt = 0xA58E84A3, TiException = 0x1E8C6100, VtBadAlloc = 0xA27EFBA3;

    /// <summary>thrower(v): throws the int v (a compact pr0 frame: push {r4, lr}).</summary>
    static uint EmitThrower(World w, Code c, uint intTi)
    {
        uint fn = c.Pc;
        c.Push();
        c.MovR4R0();
        c.MovsR0(4);
        c.Call(w.Stub("__cxa_allocate_exception"));
        c.StrR4R0();
        c.Imm(1, intTi);
        c.H(0x2200);
        c.Call(w.Stub("__cxa_throw"));
        c.Pop();
        w.Compact(fn, 0xA8B0B0);
        return fn;
    }

    /// <summary>catcher(call): calls fn and returns 0, or for an exception caught by type filter 1, what
    /// <paramref name="onCatch"/> leaves in r0 (r4 = the caught object).</summary>
    static uint EmitCatcher(World w, Code c, uint callee, uint catchTi, Action<Code> onCatch)
    {
        uint fn = c.Pc;
        c.Push();                       // +0
        c.Call(callee | 1);             // +2: movw/movt r3 (8 bytes) + blx r3 at +10, returns to +12
        c.MovsR0(0);
        c.Pop();           // +12
        uint lp = c.Pc - fn;            // +16: landing pad
        c.Call(w.Stub("__cxa_begin_catch"));
        c.MovR4R0();
        onCatch(c);
        c.H(0x0001);                    // movs r1, r0 (keeps the result)
        c.H(0x000C);                    // movs r4, r1
        c.Call(w.Stub("__cxa_end_catch"));
        c.MovR0R4();
        c.Pop();
        w.Snc(fn, [(10, 4, lp, 1)], [0x01, 0x00], [catchTi]);
        return fn;
    }

    [Fact]
    public void ThrownIntIsCaughtByItsType()
    {
        using var w = new World(fx);
        var c = new Code(w.Base);
        uint ti = w.Libc.Rtti.Ti(TiInt);
        uint thrower = EmitThrower(w, c, ti);
        uint catcher = EmitCatcher(w, c, thrower, ti, c => c.LdrR0R4());
        w.Finish();
        Assert.Equal(42u, w.Run(catcher, 42));
        Assert.Equal(0, w.Libc._ZSt18uncaught_exceptionv());
    }

    [Fact]
    public void CleanupLandingPadRunsAndUnwindingResumes()
    {
        using var w = new World(fx);
        var c = new Code(w.Base);
        uint ti = w.Libc.Rtti.Ti(TiInt);
        uint thrower = EmitThrower(w, c, ti);
        // middle(v): calls thrower; its cleanup adds 1 to the counter word, then _Unwind_Resume(ucb)
        uint middle = c.Pc;
        c.Push();
        c.Call(thrower | 1);
        c.MovsR0(0);
        c.Pop();
        uint lp = c.Pc - middle;
        c.MovR4R0();
        c.Imm(0, w.Counter);
        c.H(0x6801);
        c.H(0x3101);
        c.H(0x6001); // ldr r1,[r0]; adds r1,#1; str r1,[r0]
        c.MovR0R4();
        c.Call(w.Stub("_Unwind_Resume"));
        w.Snc(middle, [(10, 4, lp, 0), (lp, c.Pc - middle - lp, 0, 0)], [], []);
        uint catcher = EmitCatcher(w, c, middle, ti, c => c.LdrR0R4());
        w.Finish();
        GuestMemory.W32(w.Counter, 0);
        Assert.Equal(7u, w.Run(catcher, 7));
        Assert.Equal(1u, GuestMemory.R32(w.Counter));
    }

    [Fact]
    public void BadAllocIsCaughtAsStdException()
    {
        using var w = new World(fx);
        var c = new Code(w.Base);
        // thrower: std::_Xbad_alloc()
        uint thrower = c.Pc;
        c.Push();
        c.Call(w.Stub("_ZSt11_Xbad_allocv"));
        c.Pop();
        w.Compact(thrower, 0xA8B0B0);
        uint catcher = EmitCatcher(w, c, thrower, w.Libc.Rtti.Ti(TiException), c => c.LdrR0R4()); // the caught object's vptr
        w.Finish();
        Assert.Equal(w.Libc.Rtti.Vptr(VtBadAlloc), w.Run(catcher, 0));
    }

    [Fact]
    public void UnmatchedTypeFallsToTheCatchAll()
    {
        using var w = new World(fx);
        var c = new Code(w.Base);
        uint ti = w.Libc.Rtti.Ti(TiInt);
        uint thrower = EmitThrower(w, c, ti);
        // catch (std::exception&) first (filter 2, does not match an int), then catch (...) (filter 1)
        uint fn = c.Pc;
        c.Push();
        c.Call(thrower | 1);
        c.MovsR0(0);
        c.Pop();
        uint lp = c.Pc - fn;
        c.Call(w.Stub("__cxa_begin_catch"));
        c.MovR4R0();
        c.LdrR0R4();
        c.H(0x0001);
        c.H(0x000C);
        c.Call(w.Stub("__cxa_end_catch"));
        c.MovR0R4();
        c.Pop();
        // action record 1: filter 2, next +1 -> record 2: filter 1, end
        w.Snc(fn, [(10, 4, lp, 1)], [0x02, 0x01, 0x01, 0x00], [0, w.Libc.Rtti.Ti(TiException)]);
        w.Finish();
        Assert.Equal(5u, w.Run(fn, 5));
    }

    [Fact]
    public void IosClearThrowsFailureWhenTheExceptionMaskSaysSo()
    {
        using var w = new World(fx);
        var c = new Code(w.Base);
        uint ios = w.Emu.Memory.Alloc(0x40, "ios_base");
        GuestMemory.Fill(ios, 0, 0x40);
        w.Libc._ZNSt8ios_base5_InitEv(null!, ios);
        Assert.Equal(0x201u, GuestMemory.R32(ios + 0x10));
        Assert.Equal(6u, GuestMemory.R32(ios + 0x14));
        GuestMemory.W32(ios + 0xC, 4); // exceptions(badbit)
        // thrower: ios.clear(badbit | failbit, false)
        uint thrower = c.Pc;
        c.Push();
        c.Imm(0, ios);
        c.MovsR0Imm(1, 6);
        c.H(0x2200);
        c.Call(w.Stub("_ZNSt8ios_base5clearENSt5_IosbIiE8_IostateEb"));
        c.Pop();
        w.Compact(thrower, 0xA8B0B0);
        uint catcher = EmitCatcher(w, c, thrower, w.Libc.Rtti.Ti(TiException), c => c.LdrR0R4());
        w.Finish();
        Assert.Equal(w.Libc.Rtti.Vptr(0x648213CC), w.Run(catcher, 0)); // ios_base::failure
        Assert.Equal(6u, GuestMemory.R32(ios + 8));
    }

    [Fact]
    public void ManipulatorsSetTheStreamFields()
    {
        using var w = new World(fx);
        uint ios = w.Emu.Memory.Alloc(0x40, "ios_base"), m = w.Emu.Memory.Alloc(8, "manip");
        GuestMemory.Fill(ios, 0, 0x40);
        w.Libc._ZNSt8ios_base5_InitEv(null!, ios);
        uint Apply()
        {
            uint fn = GuestMemory.R32(m), arg = GuestMemory.R32(m + 4);
            var c = new Code(w.Base);
            uint f = c.Pc;
            c.Push();
            c.Imm(0, ios);
            c.Imm(1, arg);
            c.Call(fn);
            c.Pop();
            w.Finish();
            return w.Run(f);
        }
        w.Libc._ZSt4setwi(m, 12);
        Apply();
        Assert.Equal(12u, GuestMemory.R32(ios + 0x18));
        w.Libc._ZSt12setprecisioni(m, 3);
        Apply();
        Assert.Equal(3u, GuestMemory.R32(ios + 0x14));
        w.Libc._ZSt7setbasei(m, 16);
        Apply();
        Assert.Equal(0x801u, GuestMemory.R32(ios + 0x10));
        w.Libc._ZSt13resetiosflagsNSt5_IosbIiE9_FmtflagsE(m, 1);
        Apply();
        Assert.Equal(0x800u, GuestMemory.R32(ios + 0x10));
        w.Libc._ZSt11setiosflagsNSt5_IosbIiE9_FmtflagsE(m, 0x4001);
        Apply();
        Assert.Equal(0x4801u, GuestMemory.R32(ios + 0x10));
    }

    /// <summary>__cxa type matching on an exception header (TypeMatch): pointer qualification, void*, nullptr and
    /// public bases.</summary>
    [Fact]
    public void TypeMatchingFollowsTheModulesRules()
    {
        using var w = new World(fx);
        var libc = w.Libc;
        var r = libc.Rtti;
        uint obj = 0;
        int Match(uint thrownNid, uint catchNid, out uint adjusted, uint value = 0)
        {
            uint h = w.Emu.Memory.Alloc(0x100, "cxx header");
            GuestMemory.Fill(h, 0, 0x100);
            "ARM\0C++\0"u8.ToArray().CopyTo(GuestMemory.Span(h + 0x28, 8));
            GuestMemory.W32(h + 8, r.Ti(thrownNid));
            GuestMemory.W32(h + 0x80, value);
            obj = h + 0x80;
            return libc.TypeMatch(h + 0x28, r.Ti(catchNid), false, out adjusted);
        }
        const uint Pi = 0x15C21CC8, PKi = 0x33DAF949, Pv = 0x4722F2AE, Dn = 0x64F462B0, BadAlloc = 0x35E135A0;
        Assert.Equal(1, Match(TiInt, TiInt, out uint a));
        Assert.Equal(obj, a);
        Assert.Equal(1, Match(Pi, PKi, out _));        // adding const to the pointee
        Assert.Equal(0, Match(PKi, Pi, out _));        // dropping it
        Assert.Equal(1, Match(Pi, Pv, out _));         // any object pointer to void*
        Assert.Equal(1, Match(Dn, Pi, out a));
        Assert.Equal(r.NullPtr, a);
        Assert.Equal(1, Match(BadAlloc, TiException, out a));
        Assert.Equal(obj, a);
        Assert.Equal(0, Match(TiException, BadAlloc, out _));
        Assert.Equal(0, Match(TiInt, TiException, out _));
    }
}
