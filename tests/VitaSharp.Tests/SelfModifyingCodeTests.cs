using VitaSharp.Core;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// Code that rewrites code (kernel plugins patching guest functions): after the ARMv7 instruction cache maintenance
/// (ICIMVAU / ICIALLU, MCR p15 c7) the rewritten code runs, in the interpreter and in the native backend; a store alone
/// leaves the old translation, as the hardware's instruction side keeps the old line until it is invalidated.
/// </summary>
[Collection("guest")]
public sealed class SelfModifyingCodeTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-smc-" + Guid.NewGuid().ToString("N"));
    private Emulator? _emu;

    public void Dispose()
    {
        _emu?.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Writes, in a fresh block: F at +0 (movs r0, #1; bx lr); P at +0x20: str r1, [r0] then, when
    /// <paramref name="invalidate"/>, mcr p15, 0, r0, c7, c5, 1 (ICIMVAU of r0's line); ICIALLU at +0x40 (mcr p15, 0, r0,
    /// c7, c5, 0); all Thumb. Returns the block's address.
    /// </summary>
    private static uint WriteCode(GuestMemory memory)
    {
        uint code = memory.Alloc(0x1000, "smc test");
        GuestMemory.Fill(code, 0, 0x1000);
        ushort[] f = [0x2001, 0x4770];
        ushort[] patchLine = [0x6001, 0xEE07, 0x0F35, 0x4770];
        ushort[] patchOnly = [0x6001, 0x4770];
        ushort[] invalidateAll = [0xEE07, 0x0F15, 0x4770];
        static void Put(uint at, ushort[] hw)
        {
            for (int i = 0; i < hw.Length; i++)
            {
                GuestMemory.Write(at + (uint)i * 2, hw[i]);
            }
        }

        Put(code, f);
        Put(code + 0x20, patchLine);
        Put(code + 0x30, patchOnly);
        Put(code + 0x40, invalidateAll);
        return code;
    }

    private Emulator NewEmulator(string backend)
    {
        Directory.CreateDirectory(_root);
        _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fixture.Memory, EnableJit = backend != "interp", CpuBackend = backend });
        return _emu;
    }

    /// <summary>movs r0, #value; bx lr, as the word P stores.</summary>
    private static uint ReturnWord(byte value) => 0x4770_2000u | value;

    [Theory]
    [InlineData("interp")]
    [InlineData("native")]
    public void RewrittenCodeRunsAfterTheInstructionCacheIsInvalidated(string backend)
    {
        Emulator emu = NewEmulator(backend);
        var h = new HleRunner(emu);
        uint code = WriteCode(fixture.Memory);
        uint f = code | 1;
        h.Run(t =>
        {
            // hot enough for the native backend to compile F (the interpreter has it decoded from the first call)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                Assert.Equal(1u, t.CallGuest(f));
            }
            while (backend == "native" && !(emu.Code.TryGet(code, true, out Block? b) && b!.NativeEntry != 0) && sw.ElapsedMilliseconds < 20_000);

            // a store alone: the old translation keeps running
            t.CallGuest((code + 0x30) | 1, code, ReturnWord(2));
            Assert.Equal(1u, t.CallGuest(f));
            // store + ICIMVAU of the line: the new code runs
            t.CallGuest((code + 0x20) | 1, code, ReturnWord(3));
            Assert.Equal(3u, t.CallGuest(f));
            // store, then ICIALLU (everything)
            t.CallGuest((code + 0x30) | 1, code, ReturnWord(4));
            t.CallGuest((code + 0x40) | 1);
            Assert.Equal(4u, t.CallGuest(f));
        });
        fixture.Memory.Free(code);
    }

    /// <summary>Runs a body on a guest thread of an emulator and rethrows its failure.</summary>
    private sealed class HleRunner(Emulator emu)
    {
        public void Run(Action<VitaSharp.Core.Kernel.GuestThread> body)
        {
            Exception? failure = null;
            GuestThread t = emu.Kernel.CreateThread("smc", 0, 0x10000100, 0x10000, 0, 0x70000);
            t.HostBody = th =>
            {
                try
                {
                    body(th);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            };
            Assert.Equal(0, t.Start(0, 0));
            Assert.True(t.WaitForExit(60_000), "the guest thread did not finish");
            if (failure != null)
            {
                throw new Xunit.Sdk.XunitException(failure.ToString());
            }
        }
    }
}
