using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// What translating a block ahead of time allocates on the managed heap (<see cref="CodePrecompiler"/>): a large game's
/// first session translates ~700k blocks in the background, and every byte per translation is GC work that pauses the
/// game's threads.
/// </summary>
[Collection("guest")]
public sealed unsafe class PrecompileAllocationTests(GuestMemoryFixture fixture, ITestOutputHelper output)
{
    private const int Blocks = 512;

    /// <summary>The most a block translated ahead may allocate, decoding and compiling (bytes).</summary>
    public const int MaxBytesPerBlock = 1000;

    /// <summary>A chain of <see cref="Blocks"/> ARM blocks shaped like compiled game code: loads, ALU, a store, a
    /// compare and a conditional branch to the next block.</summary>
    private uint WriteChain()
    {
        uint code = fixture.Memory.Alloc(Blocks * 32 + 16, "precompile alloc chain");
        for (uint i = 0; i < Blocks; i++)
        {
            uint at = code + i * 32;
            GuestMemory.W32(at + 0x00, 0xE5910000); // ldr r0, [r1]
            GuestMemory.W32(at + 0x04, 0xE0800002); // add r0, r0, r2
            GuestMemory.W32(at + 0x08, 0xE1A03100); // lsl r3, r0, #2
            GuestMemory.W32(at + 0x0C, 0xE0234002); // eor r4, r3, r2
            GuestMemory.W32(at + 0x10, 0xE5814004); // str r4, [r1, #4]
            GuestMemory.W32(at + 0x14, 0xE3540010); // cmp r4, #16
            GuestMemory.W32(at + 0x18, 0x1A000000); // bne next (at + 0x20)
            GuestMemory.W32(at + 0x1C, 0xE2811001); // add r1, r1, #1
        }

        GuestMemory.W32(code + Blocks * 32, 0xEF000002); // svc #2
        return code;
    }

    [Fact]
    public void TranslatingABlockAheadAllocatesLittle()
    {
        if (!NativeX64Compiler.Instance.IsSupported && !NativeArm64Compiler.Instance.IsSupported)
        {
            return;
        }

        uint code = WriteChain();
        using JitTestState.Scope jitOn = JitTestState.Enable();
        string oldMode = Jit.Mode;
        try
        {
            Assert.True(Jit.Select("native"));
            var cache = new BlockCache();
            var exits = new List<(uint Pc, bool Thumb)>();
            Jit.StaticExits = exits;
            // warm up: the per-thread frontend, emitter and the backends' first-use state
            for (uint i = 0; i < 16; i++)
            {
                exits.Clear();
                Assert.True(Jit.CompileAheadOne(cache.Get(code + i * 32, false), 1, speculative: false));
            }

            long decode = 0;
            long compile = 0;
            for (uint i = 16; i < Blocks; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Block block = cache.Get(code + i * 32, false);
                long decoded = GC.GetAllocatedBytesForCurrentThread();
                exits.Clear();
                Assert.True(Jit.CompileAheadOne(block, 1, speculative: false));
                long compiled = GC.GetAllocatedBytesForCurrentThread();
                decode += decoded - before;
                compile += compiled - decoded;
            }

            int count = Blocks - 16;
            long perBlock = (decode + compile) / count;
            output.WriteLine($"per block: decode {decode / count} B, compile {compile / count} B");
            // measured on x64: 2,396 B per block before (a decode list, the code array, the encoder closure, a closure
            // per lowered instruction from the flag lambdas), 692 B after: what remains is what the block keeps (the
            // block, its instructions, its compiled code record) and its share of the runtime's tables
            Assert.True(perBlock <= MaxBytesPerBlock, $"{perBlock} B per block translated ahead (decode {decode / count}, compile {compile / count})");
        }
        finally
        {
            Jit.StaticExits = null;
            Jit.Select(oldMode);
        }
    }
}
