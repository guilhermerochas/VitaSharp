namespace VitaSharp.Core.Hle.Modules;

public sealed partial class Paf
{
    /// <summary>The placement allocation operators of ScePafStdc (operator new / new[] (size, HeapAllocator *),
    /// FW 3.74): NULL without an allocator, else the allocator's sce_paf_malloc.</summary>
    [Hle("NID_D00387E7", Nid = 0xD00387E7, Library = "ScePafStdc")] uint PafNewOnHeap(uint size, uint heap) => heap == 0 ? 0 : HeapMalloc(heap, size);
    [Hle("NID_3CF19248", Nid = 0x3CF19248, Library = "ScePafStdc")] uint PafNewArrayOnHeap(uint size, uint heap) => heap == 0 ? 0 : HeapMalloc(heap, size);
}
