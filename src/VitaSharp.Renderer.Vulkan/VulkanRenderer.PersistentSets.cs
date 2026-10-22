using Silk.NET.Vulkan;

namespace VitaSharp.Renderer.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    // Descriptor sets that live as long as the renderer (cached in fields) must not come from the per-frame pools:
    // those are reset when their frame slot is reused, which frees every set allocated from them. A cached set from
    // a frame pool is then a dangling handle: NVIDIA's driver happens to tolerate it, Mesa (lavapipe, RADV, ANV)
    // crashes inside vkCmdBindDescriptorSets / the draw.
    DescriptorPool _persistentPool;

    DescriptorSet AllocPersistentSet(DescriptorSetLayout layout)
    {
        if (_persistentPool.Handle == 0)
        {
            var sizes = stackalloc DescriptorPoolSize[2];
            sizes[0] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 64);
            sizes[1] = new DescriptorPoolSize(DescriptorType.InputAttachment, 16); // framebuffer-fetch sets (set 3 binding 2)
            var pci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 16, PoolSizeCount = 2, PPoolSizes = sizes };
            _vk.CreateDescriptorPool(_dev, in pci, null, out _persistentPool).Ok("persistent descriptor pool");
        }
        var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _persistentPool, DescriptorSetCount = 1, PSetLayouts = &layout };
        _vk.AllocateDescriptorSets(_dev, in ai, out var set).Ok("persistent descriptor set");
        return set;
    }

    void DestroyPersistentSets()
    {
        if (_persistentPool.Handle != 0) _vk.DestroyDescriptorPool(_dev, _persistentPool, null);
        _persistentPool = default;
    }
}
