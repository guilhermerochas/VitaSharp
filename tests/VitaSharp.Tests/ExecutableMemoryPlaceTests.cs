using VitaSharp.Core.Cpu.Native;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="ExecutableMemory.Place(Func{ulong, byte[]})"/>: the code is encoded for its address outside the arena
/// lock (the JIT workers and the precompilation threads compile at the same time and must not wait for each other's
/// encoding), and whatever the interleaving every block ends up encoded for the address it really occupies, in a range
/// no other block shares.
/// </summary>
public sealed unsafe class ExecutableMemoryPlaceTests
{
    /// <summary>Code of <paramref name="length"/> bytes (a multiple of 8) that names the address it was encoded for.</summary>
    private static byte[] EncodeFor(ulong address, int length)
    {
        byte[] code = new byte[length];
        for (int at = 0; at + 8 <= length; at += 8)
        {
            BitConverter.TryWriteBytes(code.AsSpan(at), address);
        }

        return code;
    }

    private static void AssertEncodedForItsAddress(byte* placed, int length)
    {
        for (int at = 0; at + 8 <= length; at += 8)
        {
            Assert.Equal((ulong)placed, *(ulong*)(placed + at));
        }
    }

    [ExecutableMemoryFact]
    public void AnotherThreadPlacesWhileOneIsEncoding()
    {
        using var memory = new ExecutableMemory();
        byte* other = null;
        bool otherFinished = false;
        int calls = 0;
        byte* first = memory.Place(address =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // while this encoding runs another thread places its code: it must not wait for this one
                var thread = new Thread(() => other = memory.Place(otherAddress => EncodeFor(otherAddress, 32)));
                thread.Start();
                otherFinished = thread.Join(TimeSpan.FromSeconds(30)); // a hang guard only: done means it never waited
            }

            return EncodeFor(address, 48);
        });

        Assert.True(otherFinished, "the second Place waited for the first one's encoding (encoded under the arena lock)");
        Assert.Equal(2, calls); // the guessed address was taken: encoded again for the reserved range
        AssertEncodedForItsAddress(first, 48);
        AssertEncodedForItsAddress(other, 32);
        Assert.True(first >= other + 32 || other >= first + 48, "the two blocks overlap");
        Assert.Equal(1, memory.PlaceRetries);
    }

    [ExecutableMemoryFact]
    public void AnEncodingThatGrowsAtItsNewAddressGetsANewRange()
    {
        using var memory = new ExecutableMemory();
        byte* other = null;
        bool otherFinished = false;
        int calls = 0;
        int length = 0;
        byte* placed = memory.Place(address =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                var thread = new Thread(() => other = memory.Place(otherAddress => EncodeFor(otherAddress, 16)));
                thread.Start();
                otherFinished = thread.Join(TimeSpan.FromSeconds(30)); // a hang guard only: done means it never waited
            }

            // the length depends on the address (x64: short or near branches); here it grows past the slack once
            length = call == 2 ? 32 + ExecutableMemory.PlaceSlack + 64 : 32;
            return EncodeFor(address, length);
        });

        Assert.True(otherFinished, "the second Place waited for the first one's encoding (encoded under the arena lock)");
        Assert.Equal(3, calls);
        AssertEncodedForItsAddress(placed, length);
        AssertEncodedForItsAddress(other, 16);
        Assert.True(placed >= other + 16 || other >= placed + length, "the two blocks overlap");
        Assert.True(memory.Used >= placed - memory.ExecBase + length);
    }

    [ExecutableMemoryFact]
    public void ConcurrentPlacesNeverShareARange()
    {
        using var memory = new ExecutableMemory();
        const int Threads = 6;
        const int PerThread = 400;
        var blocks = new (ulong Start, int Length)[Threads * PerThread];
        using var go = new Barrier(Threads);
        var threads = new Thread[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int thread = t;
            threads[t] = new Thread(() =>
            {
                go.SignalAndWait();
                for (int i = 0; i < PerThread; i++)
                {
                    int length = 8 * (1 + ((thread + i) % 9));
                    // the length also depends on the address, like an x64 encoding
                    byte* placed = memory.Place(address => EncodeFor(address, length + (int)((address >> 4) & 1) * 8));
                    int real = length + (int)(((ulong)placed >> 4) & 1) * 8;
                    blocks[thread * PerThread + i] = ((ulong)placed, real);
                }
            });
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        Array.Sort(blocks, static (a, b) => a.Start.CompareTo(b.Start));
        for (int i = 0; i < blocks.Length; i++)
        {
            AssertEncodedForItsAddress((byte*)blocks[i].Start, blocks[i].Length);
            if (i > 0)
            {
                Assert.True(blocks[i - 1].Start + (ulong)blocks[i - 1].Length <= blocks[i].Start, $"blocks at {blocks[i - 1].Start:X} and {blocks[i].Start:X} overlap");
            }
        }
    }

    [ExecutableMemoryFact]
    public void CodeThatDoesNotDependOnItsAddressIsCopiedIn()
    {
        using var memory = new ExecutableMemory();
        byte[] code = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        byte* first = memory.Place(code);
        byte* second = memory.Place(code);
        Assert.Equal(code, new ReadOnlySpan<byte>(first, code.Length).ToArray());
        Assert.Equal(0, (long)first & 15);
        Assert.True(second >= first + code.Length);
    }
}

/// <summary>[Fact] that needs an executable code arena on this host (<see cref="ExecutableMemory.UnsupportedReason"/>).</summary>
public sealed class ExecutableMemoryFactAttribute : FactAttribute
{
    public ExecutableMemoryFactAttribute()
    {
        if (ExecutableMemory.UnsupportedReason is { } reason)
        {
            Skip = reason;
        }
    }
}
