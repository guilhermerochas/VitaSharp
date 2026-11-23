using VitaSharp.Core.Cpu;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// A block decoded while another thread rewrites its code (a plugin's hook or injection, a module load) and added to the
/// process's <see cref="BlockCache"/> after that thread's <see cref="BlockCache.Invalidate"/> looked for it must not
/// stay in the cache with the code from before the rewrite: the next run would execute the old instructions, as if the
/// patch had never been made. The order of the two threads is forced with the cache's decode test point.
/// </summary>
[Collection("guest")]
public sealed class BlockCacheInvalidationRaceTests(GuestMemoryFixture fixture)
{
    private const uint AddOne = 0xE2800001; // add r0, r0, #1
    private const uint AddTwo = 0xE2800002; // add r0, r0, #2
    private const uint ReturnToCaller = 0xE12FFF1E; // bx lr

    [Fact]
    public void BlockDecodedBeforeAPatchAndAddedAfterItsInvalidationIsDecodedAgain()
    {
        uint code = fixture.Memory.Alloc(0x100, "invalidate race");
        GuestMemory.W32(code, AddOne);
        GuestMemory.W32(code + 4, ReturnToCaller);
        var cache = new BlockCache();
        bool patched = false;
        cache.AfterDecode = _ =>
        {
            if (!patched)
            {
                // the other thread's patch lands between the decode and the insert
                patched = true;
                GuestMemory.W32(code, AddTwo);
                cache.Invalidate(code, 4);
            }
        };

        Block block = cache.Get(code, false);

        Assert.Equal(AddTwo, block.Insts[0].Raw);
        Assert.True(cache.TryGet(code, false, out Block? cached));
        Assert.Same(block, cached);
        Assert.Equal(AddTwo, cached!.Insts[0].Raw);
    }

    [Fact]
    public void BlockDecodedBeforeAnUnrelatedInvalidationIsKept()
    {
        uint code = fixture.Memory.Alloc(0x100, "invalidate race unrelated");
        GuestMemory.W32(code, AddOne);
        GuestMemory.W32(code + 4, ReturnToCaller);
        GuestMemory.W32(code + 0x80, AddOne);
        var cache = new BlockCache();
        int decodes = 0;
        cache.AfterDecode = _ =>
        {
            decodes++;
            if (decodes == 1)
            {
                // other code is rewritten meanwhile: the decoded block is still right and goes in as it is
                GuestMemory.W32(code + 0x80, AddTwo);
                cache.Invalidate(code + 0x80, 4);
            }
        };

        Block block = cache.Get(code, false);

        Assert.Equal(1, decodes);
        Assert.Equal(AddOne, block.Insts[0].Raw);
        Assert.False(block.Invalidated);
        Assert.Same(block, cache.Get(code, false));
    }

    [Fact]
    public void DecodeOnAWorkerThreadRacingAPatchOnTheGuestThreadEndsWithThePatchedCode()
    {
        uint code = fixture.Memory.Alloc(0x100, "invalidate race threads");
        GuestMemory.W32(code, AddOne);
        GuestMemory.W32(code + 4, ReturnToCaller);
        var cache = new BlockCache();
        using var decoded = new ManualResetEventSlim();
        using var patchDone = new ManualResetEventSlim();
        cache.AfterDecode = _ =>
        {
            if (!decoded.IsSet)
            {
                decoded.Set();
                patchDone.Wait();
            }
        };
        Block? workerBlock = null;
        var worker = new Thread(() => workerBlock = cache.GetFromAnyThread(code, false));
        worker.Start();

        // the worker decoded the old code and has not added it yet: the guest thread patches and invalidates now
        decoded.Wait();
        GuestMemory.W32(code, AddTwo);
        cache.Invalidate(code, 4);
        patchDone.Set();
        worker.Join();

        Assert.NotNull(workerBlock);
        Assert.Equal(AddTwo, workerBlock!.Insts[0].Raw);
        cache.AfterDecode = null;
        Assert.Equal(AddTwo, cache.Get(code, false).Insts[0].Raw);
    }
}
