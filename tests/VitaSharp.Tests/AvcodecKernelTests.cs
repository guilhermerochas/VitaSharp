using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceAvcodecForDriver (SceAvcodec 3.74): the decoder entries run the user decoders' HLE on the same state (the
/// syscalls pass the same arguments on), and the application manager's one resource callback.
/// </summary>
[Collection("guest")]
public sealed class AvcodecKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int CallbackTaken = unchecked((int)0x80620808);

    private readonly HleHarness _harness = new(fixture, "kavc");

    public void Dispose() => _harness.Dispose();

    private static List<HleBinding> BindingsOf(string library, uint nid) => HleBindings.Modules
        .SelectMany(m => m.Bindings()).Where(b => b.Nid == nid && b.Library == library).ToList();

    private static void AssertEveryExportBound(string library, uint[] nids, int expected)
    {
        Assert.Equal(expected, nids.Distinct().Count());
        foreach (uint nid in nids)
        {
            int count = BindingsOf(library, nid).Count;
            Assert.True(count == 1, $"{library} 0x{nid:X8}: {count} entries");
        }
    }

    // ------------------------------------------------------------------ SceAvcodecForDriver

    /// <summary>The 82 the HLE did not have (the encoder entries were twins already).</summary>
    private static readonly uint[] s_avcodec =
    [
        0x01387A93, 0x04080710, 0x04F734CC, 0x0CC84413, 0x12DAE04A, 0x13C530E4, 0x16B501C9, 0x20892154, 0x20C83C00,
        0x22D7B653, 0x2641888C, 0x27211864, 0x2A0AF392, 0x2C7A9841, 0x301CCA25, 0x32DEE3C5, 0x361238E2, 0x3CB7661A,
        0x3F146942, 0x427914D5, 0x43985AA8, 0x43A9CD35, 0x47D8E150, 0x48247A20, 0x48DECA56, 0x4AE6B437, 0x4DD3826D,
        0x536C8296, 0x58F1E353, 0x663B3195, 0x6DC39EDC, 0x6E250A5F, 0x6F1CF77F, 0x73401748, 0x77529FD2, 0x78BA8E98,
        0x7E8CC4E5, 0x80F63723, 0x823E187E, 0x864FC454, 0x866B49BF, 0x86B1ACC5, 0x90F394E6, 0x91E94B76, 0x95E8AFCA,
        0xA2085B6B, 0xA6CAE217, 0xB6882013, 0xB8A9632A, 0xBC244D21, 0xBC55D10F, 0xBCB769EB, 0xC114C499, 0xC363B31A,
        0xC518A109, 0xC5C10C8B, 0xC8366BB8, 0xC84AFFC4, 0xC93DA0C7, 0xC99242F5, 0xC9EF7057, 0xCD7FE8D0, 0xD2ACCE22,
        0xD46B8463, 0xD5A34236, 0xD7CD7F82, 0xD9FCDF3E, 0xDFC0AAC6, 0xE355CAAE, 0xE4F668C5, 0xE782E735, 0xE7E14DE4,
        0xEA07A3B7, 0xEAAE7737, 0xEAEB16D5, 0xF305E3B3, 0xF67F93DC, 0xF714DB9A, 0xF8BCBE20, 0xF9E271DE, 0xFACCD284,
        0xFD805BF1,
    ];

    [Fact]
    public void EveryAvcodecExportHasAnEntry() => AssertEveryExportBound("SceAvcodecForDriver", s_avcodec, 82);

    private uint RunNid(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        unsafe
        {
            for (int i = 0; i < 4; i++)
            {
                thread.Core.State->R[i] = i < args.Length ? args[i] : 0;
            }

            implementation(thread.Hle);
            return thread.Core.State->R[0];
        }
    }

    [Fact]
    public void AvcodecKernelEntriesRunOnTheUserDecodersState()
    {
        Jpeg jpeg = _harness.Module<Jpeg>();
        uint param = _harness.Alloc(0xC);
        GuestMemory.W32(param, 0xC);
        GuestMemory.W32(param + 4, 5);
        GuestMemory.W32(param + 8, 0);
        _harness.OnGuest(t =>
        {
            // ksceJpegInitMJpeg (0x48247A20) sets the library the user side reads
            Assert.Equal(0u, RunNid(t, 0x48247A20, 3));
            Assert.True(jpeg.Initialized);
            Assert.Equal(3, jpeg.MaxSplitDecoders);
            // its finish (0x04080710) ends it for the user side too
            Assert.Equal(0u, RunNid(t, 0x04080710));
            Assert.False(jpeg.Initialized);
            // the init with a parameter block (0x6F1CF77F): +4 the decoder count
            Assert.Equal(0u, RunNid(t, 0x6F1CF77F, param));
            Assert.Equal(5, jpeg.MaxSplitDecoders);
            Assert.Equal(0x80650010u, RunNid(t, 0x6F1CF77F, 0)); // the user side's NULL-pointer answer
        });
    }

    [Fact]
    public void AvcodecTakesOneResourceCallback()
    {
        Videodec videodec = _harness.Module<Videodec>();
        Assert.Equal(0, Call(videodec, "ksceAvcodecRegisterResourceCallback", 0x81000101u));
        Assert.Equal(CallbackTaken, Call(videodec, "ksceAvcodecRegisterResourceCallback", 0x81000201u));
        Assert.Equal(0x81000101u, videodec.ResourceCallback);
    }
}
