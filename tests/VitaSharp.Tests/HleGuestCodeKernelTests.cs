using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The guest-code versions of SceLibKernel's hot leaf functions (Hle/HleGuestCode): sceClibMemcpy/Memmove/Memset do
/// exactly what the C# versions (memmove semantics, the low byte of the fill value, dst returned) do for every length,
/// alignment and overlap they keep, and leave the rest (long copies, a destination inside the source) to the C# version
/// with the arguments untouched; the TLS lookups return what ThreadMgr's do. Interpreted and on the native backend.
/// </summary>
[Collection("guest")]
public sealed unsafe class HleGuestCodeKernelTests(GuestMemoryFixture fx)
{
    const uint FallbackIndex = 0x321;

    static uint Nid(string name, string library = "SceLibKernel") => NidDatabase.LookupName(name).First(n => n.Library == library).Nid;

    /// <summary>Runs the routine of <paramref name="name"/> with r0-r2 until it returns to an SVC #0 trap or falls back.</summary>
    sealed class Runner : IDisposable
    {
        readonly CpuCore _core = new(new BlockCache());
        readonly uint _trap, _code, _stack;
        public CpuState* S => _core.State;

        public Runner(GuestMemoryFixture fx, string name, string library = "SceLibKernel")
        {
            _trap = fx.Memory.Alloc(0x10, "gk trap");
            GuestMemory.W32(_trap, 0xEF000000);
            _stack = fx.Memory.Alloc(0x100, "gk stack");
            _code = HleGuestCode.AddressOf(Nid(name, library), fx.Memory, _ => FallbackIndex);
            Assert.NotEqual(0u, _code);
        }

        /// <summary>Returns r0, or null when the routine fell back to its C# version (r0-r2 must then be the arguments).</summary>
        public uint? Call(uint a0, uint a1 = 0, uint a2 = 0, uint tpidruro = 0)
        {
            var s = S;
            s->R[0] = a0; s->R[1] = a1; s->R[2] = a2; s->R[3] = 0x33333333; s->R[12] = 0xCCCCCCCC;
            s->R[13] = _stack + 0x80; s->R[14] = _trap; s->R[15] = _code; s->Thumb = 0; s->TpidrUro = tpidruro;
            Assert.Equal(ExitReason.Svc, _core.Run());
            Assert.Equal(_stack + 0x80, s->R[13]);
            if (s->SvcPc != _trap)
            {
                Assert.Equal(FallbackIndex, s->SvcImm);
                Assert.Equal([a0, a1, a2], [s->R[0], s->R[1], s->R[2]]);
                return null;
            }
            Assert.Equal(0u, s->SvcImm);
            return s->R[0];
        }

        public void Dispose() => _core.Dispose();
    }

    static IEnumerable<string> Backends() => NativeX64Compiler.Instance.IsSupported ? ["interp", "native"] : ["interp"];

    /// <summary>Runs <paramref name="body"/> once per backend; the native backend compiles every block on first sight.</summary>
    static void ForEachBackend(Action<string> body)
    {
        long oldRate = CpuCore.InstructionsPerSecond, oldHot = NativeX64Compiler.Instance.HotInstructions;
        var oldMode = Jit.Mode;
        CpuCore.InstructionsPerSecond = 0;
        NativeX64Compiler.Instance.HotInstructions = 1;
        try
        {
            foreach (var backend in Backends())
            {
                Assert.True(Jit.Select(backend));
                body(backend);
            }
        }
        finally
        {
            CpuCore.InstructionsPerSecond = oldRate;
            NativeX64Compiler.Instance.HotInstructions = oldHot;
            Jit.Select(oldMode);
        }
    }

    [Theory]
    [InlineData("sceClibMemcpy")]
    [InlineData("sceClibMemmove")]
    [InlineData("sceClibMemcpy_safe")]
    public void CopiesLikeMemmove(string name)
    {
        if (!HleGuestCode.Enabled) return;
        const int Size = 0x400;
        uint buf = fx.Memory.Alloc(Size, "gk copy");
        var rng = new Random(5);
        var init = new byte[Size];
        rng.NextBytes(init);
        ForEachBackend(backend =>
        {
            using var r = new Runner(fx, name);
            // passes: the native backend compiles the routine's blocks in the background while the first ones run
            for (int pass = 0; pass < 2; pass++)
                foreach (int len in (int[])[0, 1, 2, 3, 4, 5, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 100, 255, 256, 257, 300])
                    foreach (var (d, s) in new (int, int)[] { (0, 0x200), (1, 0x203), (2, 0x201), (3, 0x202), (0x200, 0), (0x201, 0x100), (0, 2), (0, 1), (2, 0), (1, 0), (5, 5), (0x40, 0x10) })
                    {
                        init.CopyTo(GuestMemory.Span(buf, Size));
                        uint dst = buf + (uint)d, src = buf + (uint)s;
                        uint? ret = r.Call(dst, src, (uint)len);
                        var expect = (byte[])init.Clone();
                        Buffer.BlockCopy(init, s, expect, d, len); // memmove: BlockCopy handles overlap like Buffer.MemoryCopy
                        bool overlapsAbove = (uint)(d - s) < (uint)len;
                        if (len > 256 || overlapsAbove)
                        {
                            Assert.Null(ret);
                            Assert.True(GuestMemory.ReadSpan(buf, Size).SequenceEqual(init), $"{backend}: a fallback wrote memory");
                            continue;
                        }
                        Assert.Equal(dst, ret);
                        Assert.True(GuestMemory.ReadSpan(buf, Size).SequenceEqual(expect), $"{backend}: {name}(+{d:X}, +{s:X}, {len}) copied wrong");
                    }
            if (backend == "native") Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
        });
    }

    [Fact]
    public void FillsLikeMemset()
    {
        if (!HleGuestCode.Enabled) return;
        const int Size = 0x200;
        uint buf = fx.Memory.Alloc(Size, "gk fill");
        ForEachBackend(backend =>
        {
            using var r = new Runner(fx, "sceClibMemset");
            for (int pass = 0; pass < 2; pass++)
                foreach (int len in (int[])[0, 1, 3, 4, 5, 12, 15, 16, 17, 33, 64, 129, 256, 257, 400])
                    foreach (int off in (int[])[0, 1, 2, 3])
                        foreach (uint ch in (uint[])[0, 0xA5, 0x1234_56FF, 0xFFFF_FF80])
                        {
                            GuestMemory.Fill(buf, 0x5A, Size);
                            uint? ret = r.Call(buf + (uint)off, ch, (uint)len);
                            if (len > 256) { Assert.Null(ret); continue; }
                            Assert.Equal(buf + (uint)off, ret);
                            var mem = GuestMemory.ReadSpan(buf, Size);
                            for (int i = 0; i < Size; i++)
                                Assert.True(mem[i] == (i >= off && i < off + len ? (byte)ch : 0x5A), $"{backend}: memset(+{off}, {ch:X}, {len}) byte {i}");
                        }
        });
    }

    [Fact]
    public void TlsLookupsMatchThreadMgr()
    {
        if (!HleGuestCode.Enabled) return;
        uint tls = fx.Memory.Alloc(0x1000, "gk tls");
        GuestMemory.W32(tls + 4, 0x40010023); // kernel TLS word 1: the thread UID (GuestThread.InitTls)
        ForEachBackend(_ =>
        {
            using var id = new Runner(fx, "sceKernelGetThreadId");
            using var addr = new Runner(fx, "sceKernelGetTLSAddr");
            for (int pass = 0; pass < 2; pass++)
            {
                Assert.Equal(0x40010023u, id.Call(0, tpidruro: tls + 0x800));
                foreach (uint key in (uint[])[0, 1, 0x40, 0xFF])
                    Assert.Equal(tls + key * 4, addr.Call(key, tpidruro: tls + 0x800));
                foreach (uint key in (uint[])[0x100, 0x1000, 0xFFFFFFFF, 0x80000000])
                    Assert.Equal(0u, addr.Call(key, tpidruro: tls + 0x800));
            }
        });
    }
    /// <summary>libgxm's texture-control-word setters/getters and the program-header getter as guest code store and return
    /// exactly what Gxm.cs does, for linear strided and other textures, every value bit and a NULL texture.</summary>
    [Theory]
    [InlineData("sceGxmTextureSetUAddrMode")]
    [InlineData("sceGxmTextureSetVAddrMode")]
    [InlineData("sceGxmTextureSetMagFilter")]
    [InlineData("sceGxmTextureSetMinFilter")]
    [InlineData("sceGxmTextureSetMipFilter")]
    [InlineData("sceGxmTextureSetLodBias")]
    [InlineData("sceGxmTextureSetLodMin")]
    [InlineData("sceGxmTextureGetType")]
    [InlineData("sceGxmProgramGetDefaultUniformBufferSize")]
    public void GxmTextureRoutinesMatchHle(string name)
    {
        if (!HleGuestCode.Enabled) return;
        var gxm = new VitaSharp.Core.Hle.Modules.Gxm();
        var method = typeof(VitaSharp.Core.Hle.Modules.Gxm).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        bool getter = method.GetParameters().Length == 1;
        Assert.NotNull(method);
        uint tex = fx.Memory.Alloc(0x100, "gk tex");
        var rng = new Random(name.Length * 31);
        ForEachBackend(backend =>
        {
            using var r = new Runner(fx, name, "SceGxm");
            for (int i = 0; i < 300; i++)
            {
                var words = new uint[26];
                for (int k = 0; k < words.Length; k++) words[k] = (uint)rng.NextInt64();
                if (i % 3 == 0) words[1] = (words[1] & 0x1FFFFFFF) | 0xC0000000; // SCE_GXM_TEXTURE_LINEAR_STRIDED
                uint value = i % 5 == 0 ? (uint)rng.NextInt64() : (uint)rng.Next(0, 80);
                // a NULL texture: only where the C# version checks it before reading the texture (the others read word 1)
                uint arg = i == 7 && name is "sceGxmTextureSetUAddrMode" or "sceGxmTextureSetVAddrMode" or "sceGxmTextureSetMagFilter" ? 0u : tex;
                void Load() { for (int k = 0; k < words.Length; k++) GuestMemory.W32(tex + (uint)k * 4, words[k]); }
                Load();
                object? expected = getter ? method.Invoke(gxm, [arg]) : method.Invoke(gxm, [arg, value]);
                var after = Enumerable.Range(0, words.Length).Select(k => GuestMemory.R32(tex + (uint)k * 4)).ToArray();
                Load();
                uint? got = r.Call(arg, value);
                Assert.NotNull(got);
                Assert.Equal(Convert.ToUInt32(expected is int e ? (uint)e : expected), got);
                Assert.Equal(after, Enumerable.Range(0, words.Length).Select(k => GuestMemory.R32(tex + (uint)k * 4)).ToArray());
            }
        });
    }
}