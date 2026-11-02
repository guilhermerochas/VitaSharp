using Silk.NET.Vulkan;
using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Set 0 (uniform data and mapped memory) with dynamic offsets: a draw's uniform data is bound by its offset in the
/// upload ring, so the set only depends on the buffers and on the data lengths (the shaders bound-check against
/// ub.length()). One set per distinct key and frame replaces a set allocation and two to four descriptor writes per
/// draw (F1 2011's race: 1,626 of its 2,164 sets per frame).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    readonly record struct UniformSetKey(ulong B0, ulong R0, ulong B1, ulong R1, ulong B2, ulong B3);

    readonly Dictionary<UniformSetKey, DescriptorSet> _uniformSets = new();
    long _uniformSetsSerial = -1;
    UniformSetKey _lastUniformKey;
    DescriptorSet _lastUniformSet;

    /// <summary>The set for these four bindings (0/1 dynamic: written at offset 0, bound with the offsets of
    /// <paramref name="infos"/>).</summary>
    DescriptorSet UniformSet(DescriptorBufferInfo* infos)
    {
        var key = new UniformSetKey(infos[0].Buffer.Handle, infos[0].Range, infos[1].Buffer.Handle, infos[1].Range, infos[2].Buffer.Handle, infos[3].Buffer.Handle);
        if (_uniformSetsSerial != _frameSerial) { _uniformSets.Clear(); _uniformSetsSerial = _frameSerial; _lastUniformSet = default; }
        else if (_lastUniformSet.Handle != 0 && key == _lastUniformKey) return _lastUniformSet;
        if (!_uniformSets.TryGetValue(key, out var set))
        {
            set = AllocSet(_uboLayout);
            var bi = stackalloc DescriptorBufferInfo[4];
            var writes = stackalloc WriteDescriptorSet[4];
            // the dummy buffer stands for a buffer the stage's shader does not declare (no uniforms, no mapped slots): not written
            uint nw = 0;
            for (int i = 0; i < 4; i++)
            {
                if (infos[i].Buffer.Handle == _dummyBuffer.Handle && !ShaderGenerator.NanDraws) continue;
                bi[i] = i < 2 ? new DescriptorBufferInfo(infos[i].Buffer, 0, infos[i].Range) : infos[i];
                writes[nw++] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)i, DescriptorCount = 1,
                    DescriptorType = i < 2 ? DescriptorType.StorageBufferDynamic : DescriptorType.StorageBuffer, PBufferInfo = &bi[i] };
            }
            if (nw > 0) _vk.UpdateDescriptorSets(_dev, nw, writes, 0, null);
            _statDescWrites += nw;
            if (ShaderGenerator.NanDraws) NanBindDrawBuffer(set);
            _uniformSets[key] = set;
        }
        _lastUniformKey = key;
        _lastUniformSet = set;
        return set;
    }
}
