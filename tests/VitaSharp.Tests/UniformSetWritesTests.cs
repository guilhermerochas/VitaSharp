using Silk.NET.Vulkan;
using VitaSharp.Renderer.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Tests;

/// <summary>
/// Set 0 of a draw (VulkanRenderer.UniformSets.cs) writes every binding, the dummy buffer where the draw has no data.
/// Metal Gear Solid HD Collection's vertex program 9078D650 keeps all its uniform slots in split slots, so the draw has
/// no vertex uniform data, yet the shader declares UniformData (binding 0): the set left it unwritten
/// (VUID-vkCmdDrawIndexed-None-08114, a descriptor from whatever the reset pool held).
/// </summary>
public sealed unsafe class UniformSetWritesTests
{
    private static readonly Buffer s_dummy = new(0x1111);
    private static readonly Buffer s_ring = new(0x2222);

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 8)]
    public void EveryBindingIsWrittenAlsoWhereTheDrawHasNoData(bool split, int expected)
    {
        DescriptorBufferInfo* infos = stackalloc DescriptorBufferInfo[8];
        for (int i = 0; i < 8; i++)
        {
            infos[i] = new DescriptorBufferInfo(s_dummy, 0, 256);
        }

        // only the split slots of the vertex stage hold data (binding 0, the vertex uniform data, is the dummy)
        infos[4] = new DescriptorBufferInfo(s_ring, 4096, 16384);
        infos[5] = new DescriptorBufferInfo(s_ring, 20480, 16384);
        DescriptorBufferInfo* bufferInfos = stackalloc DescriptorBufferInfo[8];
        uint* bindings = stackalloc uint[8];
        DescriptorType* types = stackalloc DescriptorType[8];

        int count = VulkanRenderer.UniformSetWrites(infos, split, bufferInfos, bindings, types);

        Assert.Equal(expected, count);
        uint[] written = new ReadOnlySpan<uint>(bindings, count).ToArray();
        Assert.Contains(0u, written);
        Assert.Contains(1u, written);
        Assert.Contains(2u, written);
        Assert.Contains(3u, written);
        int vertexUniforms = Array.IndexOf(written, 0u);
        Assert.Equal(s_dummy.Handle, bufferInfos[vertexUniforms].Buffer.Handle);
        Assert.Equal(DescriptorType.StorageBufferDynamic, types[vertexUniforms]);
    }

    [Fact]
    public void DynamicBindingsAreWrittenAtOffsetZeroAndStaticOnesAtTheirOffset()
    {
        DescriptorBufferInfo* infos = stackalloc DescriptorBufferInfo[8];
        infos[0] = new DescriptorBufferInfo(s_ring, 512, 64);
        infos[1] = new DescriptorBufferInfo(s_ring, 1024, 32);
        infos[2] = new DescriptorBufferInfo(s_ring, 2048, 128);
        infos[3] = new DescriptorBufferInfo(s_dummy, 0, 256);
        for (int i = 4; i < 8; i++)
        {
            infos[i] = new DescriptorBufferInfo(s_ring, 8192, 16384);
        }

        DescriptorBufferInfo* bufferInfos = stackalloc DescriptorBufferInfo[8];
        uint* bindings = stackalloc uint[8];
        DescriptorType* types = stackalloc DescriptorType[8];

        int count = VulkanRenderer.UniformSetWrites(infos, true, bufferInfos, bindings, types);

        for (int i = 0; i < count; i++)
        {
            bool dynamic = bindings[i] is 0 or 1 || bindings[i] >= 5;
            Assert.Equal(dynamic ? DescriptorType.StorageBufferDynamic : DescriptorType.StorageBuffer, types[i]);
            Assert.Equal(dynamic ? 0ul : infos[i].Offset, bufferInfos[i].Offset);
            Assert.Equal(infos[i].Range, bufferInfos[i].Range);
        }

        Assert.Equal(new uint[] { 0, 1, 2, 3, 5, 6, 7, 8 }, new ReadOnlySpan<uint>(bindings, count).ToArray());
    }
}
