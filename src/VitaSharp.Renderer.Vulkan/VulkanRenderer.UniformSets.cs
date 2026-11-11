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
    readonly record struct UniformSetKey(ulong B0, ulong R0, ulong B1, ulong R1, ulong B2, ulong B3, ulong S0, ulong S1, ulong S2, ulong S3);

    /// <summary>Dynamic storage buffers of set 0: the uniform data of both stages, and the four split slots when on.</summary>
    static int DynamicUniformBindings => ShaderGenerator.SplitUniformSlots ? 6 : 2;

    /// <summary>Buffer handle and range of a split slot binding in one key word (ranges are slot sizes, below 2^20).</summary>
    static ulong SplitKey(in DescriptorBufferInfo b) => b.Buffer.Handle * 0x100000 ^ b.Range;

    readonly Dictionary<UniformSetKey, DescriptorSet> _uniformSets = new();
    long _uniformSetsSerial = -1;
    UniformSetKey _lastUniformKey;
    DescriptorSet _lastUniformSet;

    /// <summary>The set for these four bindings (0/1 dynamic: written at offset 0, bound with the offsets of
    /// <paramref name="infos"/>).</summary>
    DescriptorSet UniformSet(DescriptorBufferInfo* infos)
    {
        bool split = ShaderGenerator.SplitUniformSlots;
        var key = new UniformSetKey(infos[0].Buffer.Handle, infos[0].Range, infos[1].Buffer.Handle, infos[1].Range, infos[2].Buffer.Handle, infos[3].Buffer.Handle,
            split ? SplitKey(infos[4]) : 0, split ? SplitKey(infos[5]) : 0, split ? SplitKey(infos[6]) : 0, split ? SplitKey(infos[7]) : 0);
        if (_uniformSetsSerial != _frameSerial) { _uniformSets.Clear(); _uniformSetsSerial = _frameSerial; _lastUniformSet = default; }
        else if (_lastUniformSet.Handle != 0 && key == _lastUniformKey) return _lastUniformSet;
        if (!_uniformSets.TryGetValue(key, out var set))
        {
            set = AllocSet(_uboLayout);
            var bi = stackalloc DescriptorBufferInfo[8];
            var writes = stackalloc WriteDescriptorSet[8];
            // the dummy buffer stands for a buffer the stage's shader does not declare (no uniforms, no mapped slots): not written
            uint nw = 0;
            for (int i = 0; i < (split ? 8 : 4); i++)
            {
                if (infos[i].Buffer.Handle == _dummyBuffer.Handle && !ShaderGenerator.NanDraws) continue;
                bool dynamic = i < 2 || i >= 4; // 0/1 and the split slots: bound with dynamic offsets
                bi[i] = dynamic ? new DescriptorBufferInfo(infos[i].Buffer, 0, infos[i].Range) : infos[i];
                uint binding = i < 4 ? (uint)i : (uint)ShaderGenerator.SplitBinding(i < 6, (i - 4) & 1);
                writes[nw++] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = binding, DescriptorCount = 1,
                    DescriptorType = dynamic ? DescriptorType.StorageBufferDynamic : DescriptorType.StorageBuffer, PBufferInfo = &bi[i]
                };
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
