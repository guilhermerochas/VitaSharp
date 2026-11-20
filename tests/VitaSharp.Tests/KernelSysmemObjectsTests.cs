using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// SceSysmemForKernel (FW 3.74): the classes sysmem defines and the class lookups (by name, by index, the parent
/// chain), the field readers on records kernel code holds, the GUID entries (owner pid, find by name or body, open,
/// attribute), memory block information and type, the realloc of a heap block, the main memory window, the handler
/// words and the empty entries of this build.
/// </summary>
[Collection("guest")]
public sealed class KernelSysmemObjectsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint InvalidArgument = 0x80020005, NotAGuid = 0x80024501;

    private readonly HleHarness _harness = new(fixture, "ksysmemk");

    public void Dispose() => _harness.Dispose();

    private unsafe uint R0(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        for (int i = 0; i < 4; i++)
        {
            thread.Core.State->R[i] = i < args.Length ? args[i] : 0xA5A5A5A5;
        }

        uint stack = thread.Core.State->R[13];
        thread.Core.State->R[13] = stack - 0x40;
        for (int i = 4; i < args.Length; i++)
        {
            GuestMemory.W32(stack - 0x40 + (uint)(i - 4) * 4, args[i]);
        }

        implementation(thread.Hle);
        thread.Core.State->R[13] = stack;
        return thread.Core.State->R[0];
    }

    [Fact]
    public void SysmemsClassesAreFoundByNameAndIndexUnderTheRoot()
    {
        _harness.OnGuest(t =>
        {
            uint memBlockClass = R0(t, 0x86681B64);
            Assert.NotEqual(0u, memBlockClass);
            Assert.Equal(memBlockClass, R0(t, 0x86681B64));
            uint heapClass = R0(t, 0x7C878E94);
            uint dlinkClass = R0(t, 0xE4193EB6);
            Assert.NotEqual(heapClass, dlinkClass);
            Assert.Equal("SceUIDHeapClass", GuestMemory.ReadCString(GuestMemory.R32(heapClass + 0xC)));

            uint found = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x7D87F706, _harness.Str("SceUIDMemBlockClass"), found));
            Assert.Equal(memBlockClass, GuestMemory.R32(found));
            Assert.Equal(0x8002450Fu, R0(t, 0x7D87F706, _harness.Str("SceNoSuchClass"), found));
            Assert.Equal(InvalidArgument, R0(t, 0x7D87F706, 0, found));

            uint root = R0(t, 0x85336A1C); // ksceKernelGetUIDClass
            Assert.Equal(0u, R0(t, 0x1DE4953F, 1, found));
            Assert.Equal(root, GuestMemory.R32(found));
            uint index = GuestMemory.Read<byte>(heapClass + 0x14);
            Assert.Equal(0u, R0(t, 0x1DE4953F, index, found));
            Assert.Equal(heapClass, GuestMemory.R32(found));
            Assert.Equal(0x8002450Fu, R0(t, 0x1DE4953F, 0xFE, found));

            Assert.Equal(root, R0(t, 0x03A2CAF0, heapClass)); // its parent
            Assert.Equal(1u, R0(t, 0x7F7D5C4A, heapClass, root));
            Assert.Equal(1u, R0(t, 0x7F7D5C4A, heapClass, heapClass));
            Assert.Equal(0u, R0(t, 0x7F7D5C4A, heapClass, dlinkClass));
        });
    }

    [Fact]
    public void TheFieldReadersReadTheRecordTheyAreGiven()
    {
        _harness.OnGuest(t =>
        {
            uint record = _harness.Alloc(0xA0);
            GuestMemory.W32(record + 8, 0x11111111);
            GuestMemory.Write(record + 0xD, (byte)0x22);
            GuestMemory.Write(record + 0xE, (ushort)0x3333);
            GuestMemory.W32(record + 0x1C, 0x44444444);
            Assert.Equal(0x11111111u, R0(t, 0x17F12DCE, record));
            Assert.Equal(0x22u, R0(t, 0xF3DB2D71, record));
            Assert.Equal(0x3333u, R0(t, 0x797C858B, record));
            Assert.Equal(0x44444444u, R0(t, 0x5FD15C6E, record));
            Assert.Equal(0x44444444u, R0(t, 0x1F84FE44, record, 0));
            Assert.Equal(0u, R0(t, 0x811C452B, record, 0x55));
            Assert.Equal(0x55u, GuestMemory.R32(record + 0x9C));
            Assert.Equal(1u, R0(t, 0x6D563733, record));
            Assert.Equal(2u, R0(t, 0x6D563733, record));

            GuestMemory.W32(record + 0x18, 0x81000000);
            GuestMemory.W32(record + 0x30, 0x20);
            uint end = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x39D5A3B7, record, end));
            Assert.Equal(0x81002000u, GuestMemory.R32(end));

            uint table = _harness.Alloc(0x40);
            uint entries = _harness.Alloc(0x24);
            GuestMemory.W32(table + 8, 2);
            GuestMemory.W32(table + 0xC, entries);
            GuestMemory.W32(entries, 0xA);
            GuestMemory.W32(entries + 4, 0xB);
            GuestMemory.W32(entries + 0xC, 0xC);
            GuestMemory.W32(entries + 0x10, 0xD);
            uint output = _harness.Alloc(200);
            Assert.Equal(InvalidArgument, R0(t, 0xDBEACE2C, table, output));
            GuestMemory.W32(output, 200);
            Assert.Equal(0u, R0(t, 0xDBEACE2C, table, output));
            Assert.Equal(2u, GuestMemory.R32(output + 4));
            Assert.Equal(0xDu, GuestMemory.R32(output + 0x18));
        });
    }

    [Fact]
    public void GuidEntriesKnowTheirOwnerNameAndBody()
    {
        _harness.OnGuest(t =>
        {
            uint uid = R0(t, 0xC94850C9, _harness.Str("SceGuidEntry"), 0x1020D006, 0x1000, 0);
            Assert.True((int)uid > 0);
            Assert.Equal((uint)KernelSpace.KernelPid, R0(t, 0xBD42A0FC, uid));
            Assert.Equal(NotAGuid, R0(t, 0xBD42A0FC, 2));
            Assert.Equal(0u, R0(t, 0x46DF8C8A, uid, (uint)t.Pid));
            Assert.Equal((uint)t.Pid, R0(t, 0xBD42A0FC, uid));
            Assert.Equal(0u, R0(t, 0x46DF8C8A, uid, 0x12345)); // a process's object keeps its owner
            Assert.Equal((uint)t.Pid, R0(t, 0xBD42A0FC, uid));

            Assert.Equal(uid, R0(t, 0xF49C0BE8, _harness.Str("SceGuidEntry")));
            Assert.Equal(InvalidArgument, R0(t, 0xF49C0BE8, 0));
            Assert.Equal(uid, R0(t, 0x89D33DF0, uid));
            Assert.Equal(NotAGuid, R0(t, 0x89D33DF0, 4));
            Assert.Equal(0u, R0(t, 0xE8CC2166, uid, 3));

            uint body = R0(t, 0xFA6C098C, uid); // the object's body
            Assert.Equal(uid, R0(t, 0xEC90AA11, body));
            uint objOut = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x0E43E113, uid, 0, objOut));
            Assert.Equal(body, GuestMemory.R32(objOut));
            Assert.Equal(NotAGuid, R0(t, 0x0E43E113, 6, 0, objOut));

            uint attr = _harness.Word(0xFFFF);
            Assert.Equal(InvalidArgument, R0(t, 0x96BD47D1, (uint)KernelSpace.KernelPid, uid, attr));
            Assert.Equal(0u, R0(t, 0x96BD47D1, (uint)t.Pid, uid, attr));
            Assert.Equal(NotAGuid, R0(t, 0x96BD47D1, (uint)t.Pid, 8, attr));
            Assert.Equal(0u, GuestMemory.R32(attr));
        });
    }

    [Fact]
    public void BlockInformationTypeAndTheAddressLookupAgree()
    {
        _harness.OnGuest(t =>
        {
            uint uid = R0(t, 0xC94850C9, _harness.Str("SceBlockInfo"), 0x1020D006, 0x2000, 0);
            uint type = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0xD44FE44B, uid, type));
            Assert.Equal(0x1020D006u, GuestMemory.R32(type));
            Assert.Equal(InvalidArgument, R0(t, 0xD44FE44B, uid, 0));
            Assert.Equal(6u, R0(t, 0x48D2E408, 0x1020D006)); // the type's access byte

            uint info = _harness.Alloc(0x30);
            GuestMemory.W32(info, 0x2C);
            Assert.Equal(0x80020009u, R0(t, 0xBA441FD0, uid, info));
            GuestMemory.W32(info, 0x30);
            Assert.Equal(0u, R0(t, 0xBA441FD0, uid, info));
            Assert.Equal(uid, GuestMemory.R32(info + 8));
            uint blockBase = GuestMemory.R32(info + 0x10);

            uint full = _harness.Alloc(0xB8);
            GuestMemory.W32(full, 0xB8);
            Assert.Equal(0u, R0(t, 0xC3F9607E, 0, blockBase + 0x1800, 7, full));
            Assert.Equal(uid, GuestMemory.R32(full + 8));
            Assert.Equal(0x80024A03u, R0(t, 0xC3F9607E, 0, 0x10, 7, full));
        });
    }

    [Fact]
    public void ReallocKeepsTheContentsAndGivesTheOldBlockBack()
    {
        _harness.OnGuest(t =>
        {
            uint heap = R0(t, 0x9328E0E8, _harness.Str("SceReallocHeap"), 0x1000, 0);
            uint block = R0(t, 0x7B4CB60A, heap, 0x10);
            GuestMemory.W32(block, 0xCAFEBABE);
            uint moved = R0(t, 0x8EE8B917, heap, block, 0x100);
            Assert.NotEqual(0u, moved);
            Assert.Equal(0xCAFEBABEu, GuestMemory.R32(moved));
            Assert.Equal(1u, R0(t, 0xC50A9C0D, heap, 0)); // only the new block is in use
            Assert.Equal(0u, R0(t, 0x8EE8B917, heap, block, 0x10)); // the old pointer is gone
            Assert.NotEqual(0u, R0(t, 0x8EE8B917, heap, 0, 0x10)); // NULL allocates
        });
    }

    [Fact]
    public void TheSmallEntriesAnswerAsTheFirmware()
    {
        _harness.OnGuest(t =>
        {
            foreach (uint nid in new uint[] { 0x2C7B8D65, 0x31FA81B5, 0x320E907F, 0x42834422, 0x50980298, 0x666C878F, 0x6DBDA03B, 0x8F0B5536, 0x93D0A183, 0xF57A97A0 })
            {
                Assert.Equal(0u, R0(t, nid));
            }

            Assert.Equal(1u, R0(t, 0x8450BFF7));
            uint address = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x2CFD8C9A, 0x10002, 0x1000, address));
            Assert.Equal(0x40001000u, GuestMemory.R32(address));
            Assert.Equal(0x80024C02u, R0(t, 0x2CFD8C9A, 0x10001, 0x1000, address));
            Assert.Equal(0u, R0(t, 0xB591E49F, 5, 0x1234));
            Assert.Equal(InvalidArgument, R0(t, 0xB591E49F, 6, 0x1234));
            R0(t, 0x3753C584, 0x77);
            Assert.Equal(0x77u, R0(t, 0x62800A6B));
            Assert.Equal(InvalidArgument, R0(t, 0x73C38695, 0, 0, 0, 0x100000, 0x1000));
            Assert.Equal(0u, R0(t, 0x73C38695, 0, 0, 0, 0x100000, 0x200000));
        });
    }
}
