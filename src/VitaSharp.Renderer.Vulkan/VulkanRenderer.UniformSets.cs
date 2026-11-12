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
    private readonly record struct UniformSetKey(ulong B0, ulong R0, ulong B1, ulong R1, ulong B2, ulong B3, ulong S0, ulong S1, ulong S2, ulong S3);

    /// <summary>Dynamic storage buffers of set 0: the uniform data of both stages, and the four split slots when on.</summary>
    private static int DynamicUniformBindings => ShaderGenerator.SplitUniformSlots ? 6 : 2;

    /// <summary>Buffer handle and range of a split slot binding in one key word (ranges are slot sizes, below 2^20).</summary>
    private static ulong SplitKey(in DescriptorBufferInfo descriptorBufferInfo) => descriptorBufferInfo.Buffer.Handle * 0x100000 ^ descriptorBufferInfo.Range;

    private readonly Dictionary<UniformSetKey, DescriptorSet> _uniformSets = new();
    private long _uniformSetsSerial = -1;
    private UniformSetKey _lastUniformKey;
    private DescriptorSet _lastUniformSet;

    /// <summary>The set for these four bindings (0/1 dynamic: written at offset 0, bound with the offsets of
    /// <paramref name="infos"/>).</summary>
    private DescriptorSet UniformSet(DescriptorBufferInfo* infos)
    {
        bool split = ShaderGenerator.SplitUniformSlots;
        var key = new UniformSetKey(infos[0].Buffer.Handle, infos[0].Range, infos[1].Buffer.Handle, infos[1].Range, infos[2].Buffer.Handle, infos[3].Buffer.Handle,
            split ? SplitKey(infos[4]) : 0, split ? SplitKey(infos[5]) : 0, split ? SplitKey(infos[6]) : 0, split ? SplitKey(infos[7]) : 0);
        if (_uniformSetsSerial != _frameSerial)
        {
            _uniformSets.Clear();
            _uniformSetsSerial = _frameSerial;
            _lastUniformSet = default;
        }
        else if (_lastUniformSet.Handle != 0 && key == _lastUniformKey)
        {
            return _lastUniformSet;
        }

        if (!_uniformSets.TryGetValue(key, out DescriptorSet set))
        {
            set = AllocSet(_uboLayout);
            DescriptorBufferInfo* bufferInfos = stackalloc DescriptorBufferInfo[8];
            uint* bindings = stackalloc uint[8];
            DescriptorType* types = stackalloc DescriptorType[8];
            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[8];
            int writeCount = UniformSetWrites(infos, split, bufferInfos, bindings, types);
            for (int i = 0; i < writeCount; i++)
            {
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = bindings[i],
                    DescriptorCount = 1,
                    DescriptorType = types[i],
                    PBufferInfo = &bufferInfos[i]
                };
            }
            _vk.UpdateDescriptorSets(_dev, (uint)writeCount, writes, 0, null);
            _statDescWrites += writeCount;
            if (ShaderGenerator.NanDraws)
            {
                NanBindDrawBuffer(set);
            }

            _uniformSets[key] = set;
        }
        _lastUniformKey = key;
        _lastUniformSet = set;
        return set;
    }

    /// <summary>
    /// The descriptor writes of a new set 0 (<paramref name="infos"/>: bindings 0-3, then the split slots 5-8 when
    /// <paramref name="split"/>), into <paramref name="bufferInfos"/>/<paramref name="bindings"/>/<paramref name="types"/>;
    /// returns their count. Every binding is written, the dummy buffer where the draw has no data: a program whose
    /// uniform slots are all split slots still declares UniformData (its fetchMemory reads it for other addresses),
    /// and a binding a pipeline's shader declares must hold a valid descriptor when the draw executes
    /// (VUID-vkCmdDrawIndexed-None-08114, Metal Gear Solid HD Collection's vertex program 9078D650 with no vertex
    /// uniform data); a set from a reset pool otherwise keeps whatever descriptor was in its memory. Dynamic bindings
    /// (0/1 and the split slots) are written at offset 0 and bound with the offsets of <paramref name="infos"/>.
    /// </summary>
    public static int UniformSetWrites(DescriptorBufferInfo* infos, bool split, DescriptorBufferInfo* bufferInfos, uint* bindings, DescriptorType* types)
    {
        int count = split ? 8 : 4;
        for (int i = 0; i < count; i++)
        {
            bool dynamic = i < 2 || i >= 4;
            bufferInfos[i] = dynamic ? new DescriptorBufferInfo(infos[i].Buffer, 0, infos[i].Range) : infos[i];
            bindings[i] = i < 4 ? (uint)i : (uint)ShaderGenerator.SplitBinding(i < 6, (i - 4) & 1);
            types[i] = dynamic ? DescriptorType.StorageBufferDynamic : DescriptorType.StorageBuffer;
        }

        return count;
    }
}
