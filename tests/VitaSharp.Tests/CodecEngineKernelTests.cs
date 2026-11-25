using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceCodecEngineWrapperForDriver (SceCodecEngineWrapper 3.74) with no codec engine processor: the module's
/// bookkeeping over the user side's unmap memblocks, transfer lists and DMA; and the user side's unmap memblock checks
/// as the module makes them.
/// </summary>
[Collection("guest")]
public sealed class CodecEngineKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotFound = unchecked((int)0x80600000);
    private const int BadSize = unchecked((int)0x80600001);
    private const int BadAddress = unchecked((int)0x80600002);
    private const int SlotsFull = unchecked((int)0x80600007);
    private const int InvalidValue = unchecked((int)0x80600009);
    private const int InstanceValue = unchecked((int)0x80620101);
    private const int NoInstance = unchecked((int)0x80620108);
    private const int NotDecoderMode = unchecked((int)0x8001005F);
    private const int DmaArgument = unchecked((int)0x80010016);
    private const uint BlockBase = 0x90000000;

    private readonly HleHarness _harness = new(fixture, "kcew");

    public void Dispose() => _harness.Dispose();

    private CodecEngine Engine => _harness.Module<CodecEngine>();

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

    // ------------------------------------------------------------------ SceCodecEngineWrapperForDriver

    private static readonly uint[] s_engine =
    [
        0xCB8703D8, 0x091B0231, 0x7492DE05, 0x391BD4FD, 0x0D3BB164, 0x2DD83BB0, 0x3C5E9CC3, 0x91535820, 0x3A79A935,
        0x0F235F44, 0x26FAF6EC, 0xF9495589, 0xC7C150AE, 0x47AED6FF, 0x07C53B4E, 0x03EA6C2A, 0x1C2BC9F7, 0xD1F94D9D,
        0x6A1184EA, 0x2B7CC150, 0x86861877, 0x62996F94, 0x54F39D1D, 0x18197103, 0x2B74706E, 0x8CD35820, 0x9D3B1425,
        0xB280BA55, 0x6A3638A8, 0x8DEF9631, 0xBB02EB22, 0xB420ABAC, 0x30F6AFD4, 0xBB388700, 0x972F0D0F, 0xB48B02A3,
        0x9EB3D63E, 0x0D140628, 0x16506603, 0x8FE370E6, 0xFAF590A6, 0xEA88ABE5, 0x0E38B133, 0xFBD4853E, 0xC5478DD5,
        0xF3F71CEF, 0x6D41287F, 0xBCDCEA13, 0x82E378BD, 0x744D4C3F,
    ];

    [Fact]
    public void EveryCodecEngineExportHasAnEntry() => AssertEveryExportBound("SceCodecEngineWrapperForDriver", s_engine, 50);

    [Fact]
    public void UseCountGatesSuspend()
    {
        int pid = ProcessScope.CurrentPid;
        Assert.Equal(1, Call(Engine, "ksceCodecEngineCanSuspend", 0));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineUseBegin"));
        Assert.Equal(1, Engine.UseCount(pid));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineCanSuspend", pid));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineUseEnd"));
        Assert.Equal(1, Call(Engine, "ksceCodecEngineCanSuspend", pid));
    }

    [Fact]
    public void InstanceSettingsFollowTheModulesChecks()
    {
        Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineSetLevel", 0u));
        Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineSetLevel", 7u));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineSetLevel", 6u));
        // outside decoder mode only 0x1000000 can be set, and the work size answers 0x8001005F
        Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineSetFlags", 0x20000u, 0x1060000u));
        Assert.Equal(NotDecoderMode, Call(Engine, "ksceCodecEngineSetWorkSize", 0x800u));
        // mode 2 = decoder mode, 1 = back; 0, 3 and other bits refused; a zero second argument answers 0x8001005F
        Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineSetMode", 3u, 1, 0));
        Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineSetMode", 4u, 1, 0));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineSetMode", 2u, 1, 0));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineSetFlags", 0x1020000u, 0x1060000u));
        Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineSetWorkSize", 0x804u));
        Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineSetWorkSize", 0x1008u));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineSetWorkSize", 0x1000u));
        Assert.Equal((1u, 6u, 0x1020000u, 0x1000u), Engine.InstanceSettings(0));
        Assert.Equal(NotDecoderMode, Call(Engine, "ksceCodecEngineSetMode", 1u, 0, 0));
        Assert.Equal(0u, Engine.InstanceSettings(0).Mode);
        // the ops table needs the engine
        Assert.Equal(NoInstance, Call(Engine, "ksceCodecEngineInstanceOp"));
    }

    [Fact]
    public void WindowBasesAndCallbacks()
    {
        Assert.Equal(0, Call(Engine, "ksceCodecEngineSetWindowBase", 0x2345_6789u));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineSetWindowBase", 0x8123_4567u));
        Assert.Equal((0x23400000u, 0x81200000u), Engine.WindowBases);
        Assert.Equal(0u, (uint)Invoke(Engine, "ksceCodecEngineWindowTranslate", 0x23400000u)!);
        for (uint i = 0; i < 4; i++)
        {
            Assert.Equal(0, Call(Engine, "ksceCodecEngineRegisterCallbackA", 0x81000001u + i));
        }

        Assert.Equal(SlotsFull, Call(Engine, "ksceCodecEngineRegisterCallbackA", 0x81000009u));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineRegisterCallbackB", 0x81000009u)); // its own four slots
    }

    [Fact]
    public void UnmapMemBlockQueriesUseTheUserSidesTable()
    {
        int uid = Call(Engine, "sceCodecEngineOpenUnmapMemBlock", BlockBase, 0x100000u);
        Assert.True(uid > 0);
        uint allocation = (uint)Invoke(Engine, "sceCodecEngineAllocMemoryFromUnmapMemBlock", uid, 0x1000u, 0u)!;
        Assert.Equal(BlockBase, allocation);
        Assert.Equal(1, Call(Engine, "ksceCodecEngineIsInUnmapMemBlock", 0, BlockBase + 0x100, 0x1000u));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineIsInUnmapMemBlock", 0, BlockBase + 0xFF000, 0x2000u)); // runs past it
        Assert.Equal(0, Call(Engine, "ksceCodecEngineIsInUnmapMemBlock", 0, 0x81000000u, 0x100u));
        uint output = _harness.Alloc(4);
        Assert.Equal(0, Call(Engine, "ksceCodecEngineTranslateUnmapAddress", 0, BlockBase + 0x40, output));
        Assert.Equal(BlockBase + 0x40, GuestMemory.R32(output)); // flat memory: every view is the address
        Assert.Equal(NotFound, Call(Engine, "ksceCodecEngineTranslateUnmapAddress", 0, 0u, output));
        Assert.Equal(NotFound, Call(Engine, "ksceCodecEngineTranslateUnmapAddress", 0, 0x81000000u, 0u));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineCheckUnmapAllocation", 0, allocation, 0x1000u));
        Assert.Equal(NotFound, Call(Engine, "ksceCodecEngineCheckUnmapAllocation", 0, allocation + 0x100, 0x100u));
        Assert.Equal(0, Call(Engine, "sceCodecEngineCloseUnmapMemBlock", uid));
        Assert.Equal(NotFound, Call(Engine, "ksceCodecEngineTranslateUnmapAddress", 0, BlockBase + 0x40, output));
    }

    [Fact]
    public void TransferListEntries()
    {
        uint list = _harness.Alloc(0xC + 4 * 0x20);
        GuestMemory.Fill(list, 0, 0xC + 4 * 0x20);
        uint buffer = _harness.Alloc(0x100);
        uint options = _harness.Alloc(8);
        // in place: physical address kept, no bounce
        Assert.Equal(0, Call(Engine, "ksceCodecEngineListAdd", list, buffer, 0x100u, 0u));
        // options bit 0: through a bounce buffer, filled
        GuestMemory.W32(options, 1);
        GuestMemory.W32(options + 4, 0x200);
        Assert.Equal(0, Call(Engine, "ksceCodecEngineListAdd", list, buffer, 0x100u, options));
        // bounce with bit 0x10: never copied back
        GuestMemory.W32(options, 0x11);
        Assert.Equal(0, Call(Engine, "ksceCodecEngineListAdd", list, buffer, 0x40u, options));
        Assert.Equal(3u, GuestMemory.R32(list + 4));
        uint first = list + 0xC, second = first + 0x20, third = second + 0x20;
        Assert.Equal(0u, GuestMemory.R32(first + 8));
        Assert.Equal(buffer, GuestMemory.R32(first + 0x10));
        Assert.Equal(1u, GuestMemory.R32(second));
        Assert.Equal(1u, GuestMemory.R32(second + 8));
        Assert.Equal(0x200u, GuestMemory.R32(second + 0x18));
        Assert.Equal(0x100u, GuestMemory.R32(second + 0x14));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineListMarkCopyBack", list));
        Assert.Equal(0u, GuestMemory.R32(first));
        Assert.Equal(1u, GuestMemory.R32(second));
        Assert.Equal(0u, GuestMemory.R32(third));
        // no engine memory: the bounce buffers translate to nothing
        GuestMemory.W32(second + 0x10, 0xDEADBEEF);
        Assert.Equal(0, Call(Engine, "ksceCodecEngineListAllocateBounce", list));
        Assert.Equal(0u, GuestMemory.R32(second + 0x10));
        Assert.Equal(buffer, GuestMemory.R32(first + 0x10));
    }

    [Fact]
    public void DmaCopyRefusesEmptyAndOverlappingRanges()
    {
        uint source = _harness.Alloc(0x40), destination = _harness.Alloc(0x40);
        Span<byte> bytes = GuestMemory.Span(source, 0x40);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i + 1);
        }

        Assert.Equal(DmaArgument, Call(Engine, "ksceCodecEngineDmaCopy", destination, source, 0u));
        Assert.Equal(DmaArgument, Call(Engine, "ksceCodecEngineDmaCopy", source + 0x10, source, 0x20u));
        Assert.Equal(DmaArgument, Call(Engine, "ksceCodecEngineDmaCopy", source, source + 0x10, 0x20u));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineDmaCopy", destination, source, 0x40u));
        Assert.True(GuestMemory.ReadSpan(destination, 0x40).SequenceEqual(GuestMemory.ReadSpan(source, 0x40)));
    }

    // ------------------------------------------------------------------ the user side, revalidated

    [Fact]
    public void OpenUnmapMemBlockChecksAsTheModule()
    {
        Assert.Equal(NotFound, Call(Engine, "sceCodecEngineOpenUnmapMemBlock", 0u, 0x1000u));
        Assert.Equal(BadAddress, Call(Engine, "sceCodecEngineOpenUnmapMemBlock", BlockBase + 0x80, 0x1000u));
        Assert.Equal(BadSize, Call(Engine, "sceCodecEngineOpenUnmapMemBlock", BlockBase, 0x1080u));
        Assert.Equal(BadSize, Call(Engine, "sceCodecEngineOpenUnmapMemBlock", BlockBase, 0u));
    }

    [Fact]
    public void AllocationAlignmentFollowsTheModule()
    {
        int uid = Call(Engine, "sceCodecEngineOpenUnmapMemBlock", BlockBase, 0x200000u);
        // not a power of two, or above 1 MiB: nothing
        Assert.Equal(0u, (uint)Invoke(Engine, "sceCodecEngineAllocMemoryFromUnmapMemBlock", uid, 0x100u, 0x300u)!);
        Assert.Equal(0u, (uint)Invoke(Engine, "sceCodecEngineAllocMemoryFromUnmapMemBlock", uid, 0x100u, 0x200000u)!);
        // below 256 it is 256 (a small odd value included)
        Assert.Equal(BlockBase, (uint)Invoke(Engine, "sceCodecEngineAllocMemoryFromUnmapMemBlock", uid, 0x10u, 3u)!);
        Assert.Equal(BlockBase + 0x100, (uint)Invoke(Engine, "sceCodecEngineAllocMemoryFromUnmapMemBlock", uid, 0x10u, 0x80u)!);
        // freeing outside the block is 0x80600000; inside it but never handed out 0x80600009
        Assert.Equal(NotFound, Call(Engine, "sceCodecEngineFreeMemoryFromUnmapMemBlock", uid, BlockBase + 0x200000));
        Assert.Equal(InvalidValue, Call(Engine, "sceCodecEngineFreeMemoryFromUnmapMemBlock", uid, BlockBase + 0x800));
        Assert.Equal(0, Call(Engine, "sceCodecEngineCloseUnmapMemBlock", uid));
    }

    /// <summary>The decoder memory query (op 1, SceCodecEngineWrapper 3.74 0x8100cf70): the tiers by frame size, the
    /// work area and the streams; out of range 0x80620101, NULL 0x80620102. Mode 0 takes one stream and no work area,
    /// mode 1 up to four with the instance's work size.</summary>
    [Fact]
    public void TheDecoderMemorySizeFollowsTheModulesTiers()
    {
        uint parameters = _harness.Alloc(0x10);
        uint total = _harness.Alloc(4);
        int Size(uint width, uint height, uint references, uint streams)
        {
            GuestMemory.W32(parameters, width);
            GuestMemory.W32(parameters + 4, height);
            GuestMemory.W32(parameters + 8, references);
            GuestMemory.W32(parameters + 0xC, streams);
            GuestMemory.W32(total, 0xDEADBEEF);
            return Call(Engine, "ksceCodecEngineDecoderMemorySize", parameters, total);
        }

        Assert.Equal(0, Call(Engine, "ksceCodecEngineSetMode", 1u, 1, 0));
        // 480x272: the small tier, 0x140000 + 0x80000 + the 0x80000 base work area
        Assert.Equal(0, Size(480, 272, 0, 1));
        Assert.Equal(0x140000u + 0x80000u + 0x80000u, GuestMemory.R32(total));
        // 960x544 with 2 references: the middle tier
        Assert.Equal(0, Size(960, 544, 2, 1));
        Assert.Equal(0x400000u + 0x100000u + 0x80000u, GuestMemory.R32(total));
        // 1920x1088: the large tier; with 5 references the frames pass 0xC00000
        Assert.Equal(0, Size(1920, 1088, 1, 1));
        Assert.Equal(0x700000u + 0x200000u + 0x80000u, GuestMemory.R32(total));
        Assert.Equal(InstanceValue, Size(1920, 1088, 5, 1));
        // no stream: nothing
        Assert.Equal(0, Size(480, 272, 1, 0));
        Assert.Equal(0u, GuestMemory.R32(total));
        Assert.Equal(InstanceValue, Size(480, 272, 1, 2));
        Assert.Equal(InstanceValue, Size(63, 272, 1, 1));
        Assert.Equal(InstanceValue, Size(1921, 272, 1, 1));
        Assert.Equal(InstanceValue, Size(480, 1089, 1, 1));
        Assert.Equal(InstanceValue, Size(480, 272, 17, 1));
        Assert.Equal(unchecked((int)0x80620102), Call(Engine, "ksceCodecEngineDecoderMemorySize", 0u, total));
        Assert.Equal(unchecked((int)0x80620102), Call(Engine, "ksceCodecEngineDecoderMemorySize", parameters, 0u));

        // mode 1: up to four streams, each with the work size's area ((0x1000 * 0x29 + 0x10C0) to 256 KiB, + 0x80000)
        Assert.Equal(0, Call(Engine, "ksceCodecEngineSetMode", 2u, 1, 0));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineSetWorkSize", 0x1000u));
        Assert.Equal(0, Size(480, 272, 1, 4));
        uint workArea = ((0x1000u * 0x29 + 0x10C0 + 0x3FFFF) & 0xFFFC0000) + 0x80000;
        Assert.Equal(4 * (0x140000u + 0x80000u + workArea), GuestMemory.R32(total));
        Assert.Equal(InstanceValue, Size(480, 272, 1, 5));
        Assert.Equal(0, Call(Engine, "ksceCodecEngineSetMode", 1u, 1, 0));
    }

    /// <summary>The end-of-stream mark (op 12) and the notification callbacks (op 14) with no decoder open: mode 0
    /// takes only decoder 1 and keeps up to six callbacks; mode 1 keeps them per open decoder.</summary>
    [Fact]
    public void TheNotificationCallbacksTakeSixSlotsInMode0()
    {
        Assert.Equal(0, Call(Engine, "ksceCodecEngineSetMode", 1u, 1, 0));
        Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineDecoderEndOfStream", 2u));
        Assert.Equal(unchecked((int)0x80620105), Call(Engine, "ksceCodecEngineDecoderEndOfStream", 1u));
        _harness.OnGuest(_ =>
        {
            ThreadMgr threads = _harness.Module<ThreadMgr>();
            int callback = Call(threads, "sceKernelCreateCallback", "codec", 0u, 0x81000001u, 0u);
            int semaphore = Call(threads, "sceKernelCreateSema", "codec", 0u, 0, 1, 0u);
            Assert.True(callback > 0 && semaphore > 0);
            Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineDecoderAddNotification", 2u, callback, 0u));
            Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineDecoderAddNotification", 1u, callback, 0x1u));
            Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineDecoderAddNotification", 1u, semaphore, 0u));
            Assert.True(Call(Engine, "ksceCodecEngineDecoderAddNotification", 1u, 0x7FFF0001, 0u) < 0);
            for (int i = 0; i < 6; i++)
            {
                Assert.Equal(0, Call(Engine, "ksceCodecEngineDecoderAddNotification", 1u, callback, 0x3B06u));
            }

            Assert.Equal(unchecked((int)0x80620106), Call(Engine, "ksceCodecEngineDecoderAddNotification", 1u, callback, 0u));
            Assert.Equal(6, Engine.Notifications(0).Count);
            Assert.All(Engine.Notifications(0), n => Assert.Equal(((uint)_harness.Emu.Pid, (uint)callback, 0x3B06u), n));

            // mode 1: no decoder open
            Assert.Equal(0, Call(Engine, "ksceCodecEngineSetMode", 2u, 1, 0));
            Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineDecoderAddNotification", 1u, callback, 0u));
            Assert.Equal(InstanceValue, Call(Engine, "ksceCodecEngineDecoderEndOfStream", 1u));
            Assert.Equal(0, Call(Engine, "ksceCodecEngineSetMode", 1u, 1, 0));
        });
    }
}
