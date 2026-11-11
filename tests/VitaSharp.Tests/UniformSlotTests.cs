using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>A uniform slot is written whole (Gxm.WriteSlot): the buffer's bytes, then zeros for the rest of the slot,
/// whatever an earlier slice left in the arena's memory (chunks are not cleared when recycled).</summary>
[Collection("guest")]
public sealed class UniformSlotTests(GuestMemoryFixture fx)
{
    [Fact]
    public void SlotIsCopiedThenZeroedOverGarbage()
    {
        uint src = fx.Memory.Alloc(0x1000, "ubslot");
        for (uint i = 0; i < 0x1000; i += 4)
        {
            GuestMemory.W32(src + i, 0x11000000 + i);
        }

        var chunk = UniformArena.Rent(1024, out int off, clear: false);
        try
        {
            var data = chunk.Span.Slice(off, 1024);
            var slot = new UniformBufferSlot(0, 256, 512);
            foreach (int copy in new[] { 512, 100, 0 })
            {
                data.Fill(0xCD); // an earlier slice's bytes
                Gxm.WriteSlot(slot.ByteOffset, slot.ByteSize, copy == 0 ? 0 : src, copy, data, chunk, off);
                for (int i = 0; i < 1024; i++)
                {
                    byte want = i < 256 || i >= 768 ? (byte)0xCD // outside the slot: untouched
                        : i - 256 < copy ? GuestMemory.Read<byte>(src + (uint)(i - 256)) : (byte)0;
                    Assert.True(data[i] == want, $"copy {copy}: byte {i} is {data[i]:X2}, want {want:X2}");
                }
            }
            // a slot running past the data is cut at its end
            data.Fill(0xCD);
            Gxm.WriteSlot(1000, 64, src, 64, data, chunk, off);
            for (int i = 1000; i < 1024; i++)
            {
                Assert.Equal(GuestMemory.Read<byte>(src + (uint)(i - 1000)), data[i]);
            }
        }
        finally
        {
            UniformArena.Release(chunk);
        }
    }
}
