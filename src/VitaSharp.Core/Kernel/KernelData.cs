namespace VitaSharp.Core.Kernel;

/// <summary>
/// A kernel object the HLE of the firmware's kernel libraries keeps for kernel code: a kernel heap, an object of a
/// class kernel code registered (<c>ksceGUIDKernelCreateWithOpt</c>), a process event handler. What it holds is in
/// guest memory at <see cref="Address"/>; <see cref="Aux"/> is kind-specific (the class of an object).
/// </summary>
public sealed class KernelData : KernelObject
{
    public const string Heap = "heap", ClassObject = "object", ProcEventHandler = "procevent";

    /// <summary>What the object is (<see cref="Heap"/>, <see cref="ClassObject"/>, <see cref="ProcEventHandler"/>).</summary>
    public string Kind { get; init; } = "";

    /// <summary>Its guest memory (the object's body, the heap's first block, the handler table).</summary>
    public uint Address;

    /// <summary>The size of <see cref="Address"/> (a heap: the size it was created with).</summary>
    public uint Size;

    /// <summary>Kind-specific: the SceClass of an object, the attributes of a heap.</summary>
    public uint Aux;

    /// <summary>References taken with ksceGUIDReferObject and not released yet.</summary>
    public int References;
}
