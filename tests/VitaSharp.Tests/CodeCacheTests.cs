using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// "Precompilar código ARM" (<see cref="CodeCache"/>, <see cref="CodePrecompiler"/>): a module's code is translated
/// before the process runs, from the code cache of earlier sessions (per module image, valid at any load address) and
/// from the static analysis of its entry points. The precompiled code must give what the interpreter gives, the game
/// must then compile nothing for that code, rewritten code must still be dropped, and a cache file of another
/// translator configuration (an older build), image or a corrupted one must be ignored.
/// </summary>
[Collection("guest")]
public sealed unsafe class CodeCacheTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vitasharp-codecache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static bool NativeHost => NativeX64Compiler.Instance.IsSupported || NativeArm64Compiler.Instance.IsSupported;

    /// <summary>
    /// A loop calling a function: main (C+0) sets r0 = 0, r1 = 100; loop (C+8) calls func (C+0x20, r0 += 3) and counts r1
    /// down; then SVC #2. r0 ends at 300. Blocks: C+0, C+8 (the BL), C+0xC (the return point), C+0x14 (SVC), C+0x20.
    /// </summary>
    private static void WriteProgram(uint code, uint addend = 3)
    {
        GuestMemory.W32(code + 0x00, 0xE3A00000); // mov r0, #0
        GuestMemory.W32(code + 0x04, 0xE3A01064); // mov r1, #100
        GuestMemory.W32(code + 0x08, 0xEB000004); // loop: bl func
        GuestMemory.W32(code + 0x0C, 0xE2511001); //       subs r1, r1, #1
        GuestMemory.W32(code + 0x10, 0x1AFFFFFC); //       bne loop
        GuestMemory.W32(code + 0x14, 0xEF000002); //       svc #2
        GuestMemory.W32(code + 0x18, 0xE12FFF1E); //       bx lr (never reached)
        GuestMemory.W32(code + 0x20, 0xE2800000 | addend); // func: add r0, r0, #addend
        GuestMemory.W32(code + 0x24, 0xE12FFF1E); //       bx lr
    }

    private static LoadedModule Module(uint code, ulong imageHash) => new()
    {
        Name = "codecache test",
        ImageHash = imageHash,
        StartEntry = code, // ARM (bit 0 clear)
        Segments = { [0] = new SegmentInfo(code, 0x81000000, 0x100) },
        SegmentFlags = { [0] = (5, 0x100, 0x100) }, // R X
    };

    private static uint Run(CpuCore core, uint entry)
    {
        CpuState* s = core.State;
        s->R[0] = 0;
        s->R[15] = entry;
        s->Thumb = 0;
        Assert.Equal(ExitReason.Svc, core.Run());
        return s->R[0];
    }

    /// <summary>Blocks the JIT compiled while the program ran (not precompiled).</summary>
    private static HashSet<uint> Compiled(BlockCache cache) =>
        cache.Blocks.Where(b => b.JitState == 2 && !b.Ahead && !b.Invalidated).Select(b => b.Start).ToHashSet();

    /// <summary>Blocks that have compiled code, run or not (precompiled ones wait unpublished until first reached).</summary>
    private static HashSet<uint> Translated(BlockCache cache) =>
        cache.Blocks.Where(b => b.JitState is 2 or Jit.AheadPending && !b.Invalidated).Select(b => b.Start).ToHashSet();
    /// <summary>Runs <paramref name="body"/> with the native backend selected (fast pacing, every block hot at once).</summary>
    private static void WithNative(Action body)
    {
        long oldX64 = NativeX64Compiler.Instance.HotInstructions;
        long oldArm64 = NativeArm64Compiler.Instance.HotInstructions;
        string oldMode = Jit.Mode;
        bool oldTurbo = GuestClock.Turbo;
        using JitTestState.Scope jitOn = JitTestState.Enable();
        try
        {
            NativeX64Compiler.Instance.HotInstructions = 1;
            NativeArm64Compiler.Instance.HotInstructions = 1;
            GuestClock.Turbo = true;
            Assert.True(Jit.Select("native"));
            body();
        }
        finally
        {
            NativeX64Compiler.Instance.HotInstructions = oldX64;
            NativeArm64Compiler.Instance.HotInstructions = oldArm64;
            GuestClock.Turbo = oldTurbo;
            Jit.Select(oldMode);
        }
    }

    [Fact]
    public void FileRoundTripsAndRejectsOtherBuildsImagesAndCorruption()
    {
        ulong key = CodeCacheFile.KeyOf("native", Architecture.X64);
        CodeCacheEntry[] entries = [new(0, 0x10, true), new(1, 0x2000, false), new(0, 0xFFFC, true)];
        byte[] data = CodeCacheFile.Serialize(0xABCDEF, key, entries);
        Assert.Equal(entries, CodeCacheFile.Deserialize(data, 0xABCDEF, key));

        // a file of an older translator (block splitting rules), another backend or host: rejected
        Assert.Null(CodeCacheFile.Deserialize(data, 0xABCDEF, CodeCacheFile.KeyOf("native", Architecture.X64, CodeCacheFile.TranslatorVersion + 1)));
        Assert.Null(CodeCacheFile.Deserialize(CodeCacheFile.Serialize(0xABCDEF, CodeCacheFile.KeyOf("native", Architecture.X64, CodeCacheFile.TranslatorVersion - 1), entries), 0xABCDEF, key));
        Assert.Null(CodeCacheFile.Deserialize(data, 0xABCDEF, CodeCacheFile.KeyOf("native", Architecture.Arm64)));
        Assert.Null(CodeCacheFile.Deserialize(data, 0xABCDEF, CodeCacheFile.KeyOf("il", Architecture.X64)));
        // another module image
        Assert.Null(CodeCacheFile.Deserialize(data, 0xABCDEE, key));
        // a flipped bit anywhere, a truncated file, an empty one
        for (int i = 0; i < data.Length; i++)
        {
            byte[] corrupt = (byte[])data.Clone();
            corrupt[i] ^= 0x10;
            Assert.Null(CodeCacheFile.Deserialize(corrupt, 0xABCDEF, key));
        }

        Assert.Null(CodeCacheFile.Deserialize(data.AsSpan(0, data.Length - 1), 0xABCDEF, key));
        Assert.Null(CodeCacheFile.Deserialize([], 0xABCDEF, key));

        // on disk, through the module's file name
        CodeCacheFile.Write(_directory, 0xABCDEF, key, entries);
        Assert.Equal(entries, CodeCacheFile.Read(_directory, 0xABCDEF, key));
        Assert.Null(CodeCacheFile.Read(_directory, 0xABCDEE, key));
    }

    [Fact]
    public void StaticAnalysisTranslatesTheModuleBeforeItRunsAndTheGameCompilesNothing()
    {
        if (!NativeHost)
        {
            return; // native code runs in this dispatcher only on an x64 or ARM64 host
        }

        uint code = fixture.Memory.Alloc(0x1000, "codecache analysis");
        WriteProgram(code);
        uint interpreted;
        string oldMode = Jit.Mode;
        try
        {
            Assert.True(Jit.Select("interp"));
            using var interpreterCore = new CpuCore(new BlockCache());
            interpreted = Run(interpreterCore, code);
        }
        finally
        {
            Jit.Select(oldMode);
        }

        Assert.Equal(300u, interpreted);
        WithNative(() =>
        {
            // without the precompilation the game compiles its blocks while it runs
            var control = new BlockCache();
            using (var controlCore = new CpuCore(control))
            {
                Assert.Empty(Compiled(control));
                Assert.Equal(interpreted, Run(controlCore, code));
                Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
                Assert.NotEmpty(Compiled(control));
            }

            var cache = new BlockCache();
            var codeCache = new CodeCache(_directory, cache);
            codeCache.ModuleLoaded(Module(code, 1));
            // a first session: the boot step waits for the code reached from the module's entry point
            CodePrecompiler.Result result = codeCache.PrecompilePending(null, CancellationToken.None, workers: 2);
            HashSet<uint> before = Translated(cache);
            // every block the program runs: the call target, the return point after the call (no static exit names it)
            // and both ways of the loop's branch
            Assert.Superset(new HashSet<uint> { code, code + 0x08, code + 0x0C, code + 0x14, code + 0x20 }, before);
            Assert.True(result.Translated >= 5 && result.Found >= 4, result.ToString());
            Assert.All(before, start => Assert.InRange(start, code, code + 0xFF));
            // precompiled code is published when the guest first reaches it
            Assert.All(cache.Blocks.Where(b => b.Ahead), b => Assert.Equal(0, b.NativeEntry));

            using (var core = new CpuCore(cache))
            {
                Assert.Equal(interpreted, Run(core, code));
            }

            Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            Assert.Empty(Compiled(cache)); // nothing compiled while the program ran
            Assert.NotEqual(0, cache.Get(code + 0x20, false).NativeEntry);

            // the cache records what ran, in the order it first ran; not the precompiled code that never ran
            codeCache.Save();
            List<CodeCacheEntry>? recorded = CodeCacheFile.Read(_directory, 1, CodeCacheFile.ConfigurationKey);
            Assert.NotNull(recorded);
            Assert.Equal([0x00u, 0x20, 0x0C, 0x08, 0x14], recorded.Select(e => e.Offset));
            codeCache.Stop();
        });
    }

    [Fact]
    public void BackgroundPrecompilationDoesNotWaitAndStillFillsIn()
    {
        if (!NativeHost)
        {
            return;
        }

        uint code = fixture.Memory.Alloc(0x1000, "codecache background");
        WriteProgram(code);
        WithNative(() =>
        {
            var cache = new BlockCache();
            var codeCache = new CodeCache(null, cache);
            codeCache.ModuleLoaded(Module(code, 3));
            codeCache.PrecompilePending(null, CancellationToken.None, wait: false);
            CodePrecompiler run = Assert.Single(codeCache.Runs);
            Assert.True(run.WaitFinished(TimeSpan.FromSeconds(60)));
            Assert.Superset(new HashSet<uint> { code, code + 0x08, code + 0x0C, code + 0x14, code + 0x20 }, Translated(cache));
            using var core = new CpuCore(cache);
            Assert.Equal(300u, Run(core, code));
            Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            Assert.Empty(Compiled(cache));

            // a module loaded while the process runs is analysed in the background too; a stopped cache starts nothing
            uint late = fixture.Memory.Alloc(0x1000, "codecache late module");
            WriteProgram(late);
            codeCache.ModuleLoaded(Module(late, 4));
            Assert.Equal(2, codeCache.Runs.Count);
            Assert.True(codeCache.Runs[1].WaitFinished(TimeSpan.FromSeconds(60)));
            Assert.Contains(late + 0x20, Translated(cache));
            codeCache.Stop();
            codeCache.ModuleLoaded(Module(fixture.Memory.Alloc(0x1000, "codecache after stop"), 5));
            Assert.Equal(2, codeCache.Runs.Count);
        });
    }
    [Fact]
    public void RecordedBlocksPrecompileInTheNextSessionAtAnotherLoadAddress()
    {
        if (!NativeHost)
        {
            return;
        }

        uint first = fixture.Memory.Alloc(0x1000, "codecache session 1");
        uint second = fixture.Memory.Alloc(0x1000, "codecache session 2");
        Assert.NotEqual(first, second);
        WriteProgram(first);
        WriteProgram(second);
        WithNative(() =>
        {
            // session 1: the JIT compiles the hot blocks; the session end records them
            var cache1 = new BlockCache();
            var codeCache1 = new CodeCache(_directory, cache1);
            codeCache1.ModuleLoaded(Module(first, 0x5EED));
            codeCache1.Analyze = false;
            codeCache1.PrecompilePending(null, CancellationToken.None, wait: false);
            using (var core1 = new CpuCore(cache1))
            {
                Assert.Equal(300u, Run(core1, first));
                Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            }

            var recorded = Compiled(cache1).Select(start => start - first).ToHashSet();
            Assert.Empty(Translated(cache1).Except(Compiled(cache1)));
            Assert.NotEmpty(recorded);
            codeCache1.Save();
            Assert.True(File.Exists(CodeCacheFile.PathFor(_directory, 0x5EED)));

            // session 2: the same module image somewhere else; the boot step waits for the recorded blocks only
            var cache2 = new BlockCache();
            var codeCache2 = new CodeCache(_directory, cache2) { Analyze = false };
            codeCache2.ModuleLoaded(Module(second, 0x5EED));
            Assert.Equal(recorded.Count, codeCache2.PendingCount);
            CodePrecompiler.Result result = codeCache2.PrecompilePending(null, CancellationToken.None, workers: 2);
            Assert.Equal(recorded.Count, result.Translated);
            HashSet<uint> before = Translated(cache2);
            Assert.Equal(recorded, before.Select(start => start - second).ToHashSet());
            using var core2 = new CpuCore(cache2);
            Assert.Equal(300u, Run(core2, second));
            Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            Assert.Empty(Compiled(cache2));
            Assert.Equal(before, Translated(cache2));
            codeCache1.Stop();
            codeCache2.Stop();

            // another image (an updated module) finds nothing; a file of an older build is ignored
            var cache3 = new BlockCache();
            var otherImage = new CodeCache(_directory, cache3);
            otherImage.ModuleLoaded(Module(second, 0x5EEE));
            Assert.Equal(0, otherImage.PendingCount);
            var olderBuild = new CodeCache(_directory, cache3, CodeCacheFile.KeyOf(Jit.Mode, RuntimeInformation.ProcessArchitecture, CodeCacheFile.TranslatorVersion + 1));
            olderBuild.ModuleLoaded(Module(second, 0x5EED));
            Assert.Equal(0, olderBuild.PendingCount);
        });
    }

    [Fact]
    public void IndexFunctionsAreNotTranslatedWholesaleButTheFrontierFillsInWhatRuns()
    {
        if (!NativeHost)
        {
            return;
        }

        uint code = fixture.Memory.Alloc(0x1000, "codecache frontier");
        GuestMemory.W32(code + 0x00, 0xE3A00000); // mov r0, #0
        GuestMemory.W32(code + 0x04, 0xE1A00000); // nop (r2 = F, set by the caller: a function reached only through a register)
        GuestMemory.W32(code + 0x08, 0xE12FFF32); // blx r2
        GuestMemory.W32(code + 0x0C, 0xEF000002); // svc #2
        GuestMemory.W32(code + 0x10, 0xE12FFF1E); // bx lr (never reached: ends the flow after the SVC)
        GuestMemory.W32(code + 0x400, 0xE3500000); // F: cmp r0, #0
        GuestMemory.W32(code + 0x404, 0x1A000001); //    bne rare
        GuestMemory.W32(code + 0x408, 0xE2800005); //    add r0, r0, #5
        GuestMemory.W32(code + 0x40C, 0xE12FFF1E); //    bx lr
        GuestMemory.W32(code + 0x410, 0xE2800009); // rare: add r0, r0, #9 (never runs)
        GuestMemory.W32(code + 0x414, 0xE12FFF1E); //    bx lr
        GuestMemory.W32(code + 0x800, unchecked((uint)(0x400 - 0x800)) & 0x7FFFFFFF); // .ARM.exidx: F
        GuestMemory.W32(code + 0x804, 1);
        LoadedModule module = Module(code, 6);
        module.Segments[0] = new SegmentInfo(code, 0x81000000, 0x1000);
        module.SegmentFlags[0] = (5, 0x1000, 0x1000);
        module.ExidxTop = code + 0x800;
        module.ExidxEnd = code + 0x808;
        WithNative(() =>
        {
            var cache = new BlockCache();
            var codeCache = new CodeCache(null, cache);
            codeCache.ModuleLoaded(module);
            codeCache.PrecompilePending(null, CancellationToken.None, workers: 2);
            CodePrecompiler run = Assert.Single(codeCache.Runs);
            Assert.True(run.WaitFinished(TimeSpan.FromSeconds(60)));
            HashSet<uint> boot = Translated(cache);
            Assert.Contains(code, boot);
            Assert.Contains(code + 0x0C, boot); // the return point after the call through the register
            // the index only names F: its code is not translated before anything runs it
            Assert.DoesNotContain(code + 0x400, boot);
            Assert.DoesNotContain(code + 0x410, boot);

            // F runs until the JIT compiles it: the frontier then translates the rest of F, the branch it never takes too
            using var core = new CpuCore(cache);
            core.State->R[2] = code + 0x400;
            for (int i = 0; i < 2 * Jit.MinExecutions; i++)
            {
                Assert.Equal(5u, Run(core, code));
            }

            Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            Assert.True(run.WaitFinished(TimeSpan.FromSeconds(60)));
            Block rare = cache.Get(code + 0x410, false);
            Assert.True(rare.Ahead);
            Assert.Equal(Jit.AheadPending, rare.JitState);
            codeCache.Stop();
        });
    }
    [Fact]
    public void TheFrontierTranslatesTheWholeFunctionAndTheFunctionsItPointsTo()
    {
        if (!NativeHost)
        {
            return;
        }

        uint code = fixture.Memory.Alloc(0x1000, "codecache whole function");
        GuestMemory.W32(code + 0x00, 0xE3A00000); // mov r0, #0
        GuestMemory.W32(code + 0x04, 0xE1A00000); // nop (r2 = F)
        GuestMemory.W32(code + 0x08, 0xE12FFF32); // blx r2
        GuestMemory.W32(code + 0x0C, 0xEF000002); // svc #2
        GuestMemory.W32(code + 0x10, 0xE12FFF1E); // bx lr
        // F: the path that runs is its first block; the others are a chain of branches never taken, deeper than
        // MaxFrontierDepth blocks from it
        GuestMemory.W32(code + 0x400, 0xE3500000); // F: cmp r0, #0
        GuestMemory.W32(code + 0x404, 0x1A000005); //    bne 0x420
        GuestMemory.W32(code + 0x408, 0xE2800005); //    add r0, r0, #5
        GuestMemory.W32(code + 0x40C, 0xE12FFF1E); //    bx lr
        for (uint level = 0; level < 4; level++)
        {
            uint at = code + 0x420 + level * 0x10;
            GuestMemory.W32(at, 0xE3500001 + level); //  cmp r0, #level + 1
            GuestMemory.W32(at + 4, 0x1A000001); //       bne next level (at + 0x10)
            GuestMemory.W32(at + 8, 0xE12FFF1E); //       bx lr
        }

        GuestMemory.W32(code + 0x460, 0xE2800007); //    deepest: add r0, r0, #7
        GuestMemory.W32(code + 0x464, 0xE12FFF1E); //    bx lr
        GuestMemory.W32(code + 0x470, code + 0x600); //  F's literal pool: the address of G (a relocation)
        GuestMemory.W32(code + 0x600, 0xE92D4010); // G: push {r4, lr}
        GuestMemory.W32(code + 0x604, 0xE8BD8010); //    pop {r4, pc}
        GuestMemory.W32(code + 0x800, unchecked((uint)(0x400 - 0x800)) & 0x7FFFFFFF); // .ARM.exidx: F, G
        GuestMemory.W32(code + 0x804, 1);
        GuestMemory.W32(code + 0x808, unchecked((uint)(0x600 - 0x808)) & 0x7FFFFFFF);
        GuestMemory.W32(code + 0x80C, 1);
        LoadedModule module = Module(code, 7);
        module.Segments[0] = new SegmentInfo(code, 0x81000000, 0x1000);
        module.SegmentFlags[0] = (5, 0x1000, 0x1000);
        module.ExidxTop = code + 0x800;
        module.ExidxEnd = code + 0x810;
        module.AbsoluteRelocations.Add((code + 0x470, code + 0x600));
        WithNative(() =>
        {
            var cache = new BlockCache();
            var codeCache = new CodeCache(null, cache);
            codeCache.ModuleLoaded(module);
            codeCache.PrecompilePending(null, CancellationToken.None, workers: 2);
            CodePrecompiler run = Assert.Single(codeCache.Runs);
            Assert.True(run.WaitFinished(TimeSpan.FromSeconds(60)));
            Assert.DoesNotContain(code + 0x600, Translated(cache)); // G: only a pointer names it

            using var core = new CpuCore(cache);
            core.State->R[2] = code + 0x400;
            for (int i = 0; i < 2 * Jit.MinExecutions; i++)
            {
                Assert.Equal(5u, Run(core, code));
            }

            Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            Assert.True(run.WaitFinished(TimeSpan.FromSeconds(60)));
            HashSet<uint> translated = Translated(cache);
            // the whole of F, however deep the branch that never ran
            Assert.Contains(code + 0x450, translated);
            Assert.Contains(code + 0x460, translated);
            // the function F points to, before anything calls it
            Block pointed = cache.Get(code + 0x600, false);
            Assert.True(pointed.Ahead);
            Assert.Equal(Jit.AheadPending, pointed.JitState);
            // the rest of the running function comes first (Running); the functions it points to, after (Frontier)
            Assert.Equal((byte)CodePrecompiler.Priority.Running, cache.Get(code + 0x460, false).AheadPriority);
            Assert.Equal((byte)CodePrecompiler.Priority.Frontier, pointed.AheadPriority);
            codeCache.Stop();
        });
    }

    /// <summary>Writes an exception index naming <paramref name="functions"/> (offsets from <paramref name="code"/>) at
    /// code + 0x800 and returns a module with that code (segment 0, 4 KiB) and <paramref name="data"/> (segment 1).</summary>
    private static LoadedModule ModuleWithData(uint code, uint data, ulong imageHash, params uint[] functions)
    {
        for (int i = 0; i < functions.Length; i++)
        {
            uint at = code + 0x800 + 8 * (uint)i;
            GuestMemory.W32(at, (code + functions[i] - at) & 0x7FFFFFFF);
            GuestMemory.W32(at + 4, 1);
        }

        LoadedModule module = Module(code, imageHash);
        module.Segments[0] = new SegmentInfo(code, 0x81000000, 0x1000);
        module.SegmentFlags[0] = (5, 0x1000, 0x1000);
        module.Segments[1] = new SegmentInfo(data, 0x81001000, 0x1000);
        module.SegmentFlags[1] = (6, 0x1000, 0x1000); // R W: data
        module.ExidxTop = code + 0x800;
        module.ExidxEnd = code + 0x800 + 8 * (uint)functions.Length;
        return module;
    }

    /// <summary>ARM MOVW (<paramref name="top"/> false) or MOVT of the low or high half of <paramref name="value"/> into <paramref name="register"/>.</summary>
    private static uint ArmMoveWide(bool top, uint register, uint value)
    {
        uint half = top ? value >> 16 : value & 0xFFFF;
        return (top ? 0xE3400000u : 0xE3000000u) | ((half >> 12) << 16) | (register << 12) | (half & 0xFFF);
    }

    [Fact]
    public void TheFrontierFollowsTheTablesAFixedAddressExecutableBuildsWithMovwMovt()
    {
        if (!NativeHost)
        {
            return;
        }

        // an executable at a fixed address has no relocations: the vtable F stores is only an address F builds
        uint code = fixture.Memory.Alloc(0x1000, "codecache fixed tables code");
        uint data = fixture.Memory.Alloc(0x1000, "codecache fixed tables data");
        GuestMemory.W32(code + 0x00, 0xE3A00000); // mov r0, #0
        GuestMemory.W32(code + 0x04, 0xE1A00000); // nop (r2 = F)
        GuestMemory.W32(code + 0x08, 0xE12FFF32); // blx r2
        GuestMemory.W32(code + 0x0C, 0xEF000002); // svc #2
        GuestMemory.W32(code + 0x10, 0xE12FFF1E); // bx lr
        GuestMemory.W32(code + 0x400, ArmMoveWide(false, 3, data + 0x20)); // F: movw r3, #:lower16:table
        GuestMemory.W32(code + 0x404, ArmMoveWide(true, 3, data + 0x20)); //     movt r3, #:upper16:table
        GuestMemory.W32(code + 0x408, 0xE2800005); //                          add r0, r0, #5
        GuestMemory.W32(code + 0x40C, 0xE12FFF1E); //                          bx lr
        GuestMemory.W32(code + 0x600, 0xE92D4010); // G: push {r4, lr} (only the table names it)
        GuestMemory.W32(code + 0x604, 0xE8BD8010); //    pop {r4, pc}
        GuestMemory.W32(data + 0x20, code + 0x600); // the table: G, then something that is not a function
        GuestMemory.W32(data + 0x24, 0x12345678);
        LoadedModule module = ModuleWithData(code, data, 11, 0x000, 0x400, 0x600);
        Assert.Empty(module.AbsoluteRelocations);
        WithNative(() =>
        {
            var cache = new BlockCache();
            var codeCache = new CodeCache(null, cache);
            codeCache.ModuleLoaded(module);
            codeCache.PrecompilePending(null, CancellationToken.None, workers: 2);
            CodePrecompiler run = Assert.Single(codeCache.Runs);
            Assert.True(run.WaitFinished(TimeSpan.FromSeconds(60)));
            Assert.DoesNotContain(code + 0x600, Translated(cache));

            using var core = new CpuCore(cache);
            core.State->R[2] = code + 0x400;
            for (int i = 0; i < 2 * Jit.MinExecutions; i++)
            {
                Assert.Equal(5u, Run(core, code));
            }

            Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            Assert.True(run.WaitFinished(TimeSpan.FromSeconds(60)));
            // once F runs, the function of the table it builds, before anything calls it
            Block pointed = cache.Get(code + 0x600, false);
            Assert.True(pointed.Ahead);
            Assert.Equal(Jit.AheadPending, pointed.JitState);
            Assert.Equal((byte)CodePrecompiler.Priority.Frontier, pointed.AheadPriority);
            codeCache.Stop();
        });
    }

    [Fact]
    public void TheFlowTranslatesTheCasesOfASwitch()
    {
        if (!NativeHost)
        {
            return;
        }

        uint code = fixture.Memory.Alloc(0x1000, "codecache switch");
        GuestMemory.Write<ushort>(code + 0x100, 0x2802); // cmp r0, #2
        GuestMemory.Write<ushort>(code + 0x102, 0xD806); // bhi default (0x112)
        GuestMemory.Write<ushort>(code + 0x104, 0xE8DF); // tbb [pc, r0]
        GuestMemory.Write<ushort>(code + 0x106, 0xF000);
        GuestMemory.Write<byte>(code + 0x108, 2); // cases 0x10C, 0x10E, 0x110
        GuestMemory.Write<byte>(code + 0x109, 3);
        GuestMemory.Write<byte>(code + 0x10A, 4);
        GuestMemory.Write<byte>(code + 0x10B, 0);
        for (uint at = 0x10C; at <= 0x112; at += 2)
        {
            GuestMemory.Write<ushort>(code + at, 0x4770); // bx lr
        }

        LoadedModule module = Module(code, 8);
        module.StartEntry = code + 0x101; // Thumb
        module.Segments[0] = new SegmentInfo(code, 0x81000000, 0x1000);
        module.SegmentFlags[0] = (5, 0x1000, 0x1000);
        WithNative(() =>
        {
            var cache = new BlockCache();
            var codeCache = new CodeCache(null, cache);
            codeCache.ModuleLoaded(module);
            codeCache.PrecompilePending(null, CancellationToken.None, workers: 2);
            HashSet<uint> boot = Translated(cache);
            Assert.Superset(new HashSet<uint> { code + 0x100, code + 0x104, code + 0x10C, code + 0x10E, code + 0x110, code + 0x112 }, boot);
            codeCache.Stop();
        });
    }

    [Fact]
    public void CodeOutsideTheModulesIsRecordedByItsBytesAndPrecompiledNextSession()
    {
        if (!NativeHost)
        {
            return;
        }

        // code the emulator writes into the process (the HLE routines written as guest code): no module, no ELF
        uint first = fixture.Memory.Alloc(0x1000, "codecache region 1");
        uint second = fixture.Memory.Alloc(0x1000, "codecache region 2");
        WriteProgram(first);
        WriteProgram(second);
        WithNative(() =>
        {
            var cache1 = new BlockCache();
            var codeCache1 = new CodeCache(_directory, cache1) { Analyze = false };
            LoadedModule region1 = codeCache1.CodeRegionPlaced("routines", first, 0x100);
            codeCache1.PrecompilePending(null, CancellationToken.None, wait: false);
            using (var core1 = new CpuCore(cache1))
            {
                Assert.Equal(300u, Run(core1, first));
                Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            }

            var ran = Compiled(cache1).Select(start => start - first).ToHashSet();
            Assert.NotEmpty(ran);
            codeCache1.Save();
            Assert.True(File.Exists(CodeCacheFile.PathFor(_directory, region1.ImageHash)));
            codeCache1.Stop();

            // the same bytes elsewhere in the next session: what ran is translated before it runs, and nothing compiles
            var cache2 = new BlockCache();
            var codeCache2 = new CodeCache(_directory, cache2) { Analyze = false };
            LoadedModule region2 = codeCache2.CodeRegionPlaced("routines", second, 0x100);
            Assert.Equal(region1.ImageHash, region2.ImageHash);
            Assert.Equal(ran.Count, codeCache2.PendingCount);
            codeCache2.PrecompilePending(null, CancellationToken.None, workers: 2);
            using (var core2 = new CpuCore(cache2))
            {
                Assert.Equal(300u, Run(core2, second));
                Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)));
            }

            Assert.Empty(Compiled(cache2));
            codeCache2.Stop();

            // other bytes (another build's routines): another hash, nothing recorded for them
            WriteProgram(second, addend: 4);
            var codeCache3 = new CodeCache(_directory, new BlockCache());
            Assert.NotEqual(region1.ImageHash, codeCache3.CodeRegionPlaced("routines", second, 0x100).ImageHash);
            Assert.Equal(0, codeCache3.PendingCount);
        });
    }

    [Fact]
    public void PrecompiledCodeThatTheGameRewritesIsDropped()
    {
        if (!NativeHost)
        {
            return;
        }

        uint code = fixture.Memory.Alloc(0x1000, "codecache rewrite");
        WriteProgram(code);
        WithNative(() =>
        {
            var cache = new BlockCache();
            var codeCache = new CodeCache(null, cache);
            codeCache.ModuleLoaded(Module(code, 2));
            codeCache.PrecompilePending(null, CancellationToken.None, workers: 1);
            Block func = cache.Get(code + 0x20, false);
            Assert.Equal(Jit.AheadPending, func.JitState);

            // the game writes another addend into func (the emulator drops the translations of the written range)
            GuestMemory.W32(code + 0x20, 0xE2800007);
            cache.Invalidate(code + 0x20, 4);
            Assert.True(func.Invalidated);
            using var core = new CpuCore(cache);
            Assert.Equal(700u, Run(core, code));
            codeCache.Stop();
        });
    }

    [Fact]
    public void EntryPointsComeFromTheModuleInfoExportsAndExceptionIndex()
    {
        uint code = fixture.Memory.Alloc(0x1000, "codecache entries");
        var module = new LoadedModule
        {
            StartEntry = code + 0x101, // Thumb
            StopEntry = code + 0x201,
            ExidxTop = code + 0x800,
            ExidxEnd = code + 0x818,
            Segments = { [0] = new SegmentInfo(code, code, 0x1000) },
            SegmentFlags = { [0] = (5, 0x1000, 0x1000) },
        };
        module.ExportedFunctionEntries.Add(code + 0x301);
        // .ARM.exidx: prel31 offsets from each entry to its function, then the unwind word
        GuestMemory.W32(code + 0x800, unchecked((uint)(0x400 - 0x800)) & 0x7FFFFFFF);
        GuestMemory.W32(code + 0x804, 1); // EXIDX_CANTUNWIND
        GuestMemory.W32(code + 0x808, unchecked((uint)(0x500 - 0x808)) & 0x7FFFFFFF);
        GuestMemory.W32(code + 0x80C, 1);
        GuestMemory.W32(code + 0x810, 0x80000000); // not a function offset
        GuestMemory.W32(code + 0x814, 1);
        List<((uint Pc, bool Thumb) Target, CodePrecompiler.Priority Priority)> entries = CodePrecompiler.ModuleEntryPoints(module);
        Assert.Equal(
            [(code + 0x100, true), (code + 0x200, true), (code + 0x300, true), (code + 0x400, true), (code + 0x500, true)],
            entries.Select(e => e.Target));
        // the module's own entry points are followed first; the exception index only names functions
        Assert.Equal(
            [CodePrecompiler.Priority.Flow, CodePrecompiler.Priority.Flow, CodePrecompiler.Priority.Flow, CodePrecompiler.Priority.Index, CodePrecompiler.Priority.Index],
            entries.Select(e => e.Priority));
        Assert.True(CodePrecompiler.InCode(module, code + 0xFFF));
        Assert.False(CodePrecompiler.InCode(module, code + 0x1000));
        module.SegmentFlags[0] = (6, 0x1000, 0x1000); // R W: data
        Assert.False(CodePrecompiler.InCode(module, code + 0x100));
    }
}
