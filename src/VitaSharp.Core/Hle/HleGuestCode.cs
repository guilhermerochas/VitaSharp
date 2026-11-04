using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle;

/// <summary>
/// HLE functions implemented as guest ARM code instead of an SVC into C#. On the Vita these are user-mode library code
/// (libgxm's precomputed-state packing: a few loads and stores on memory whose layout is ours). As guest code they are
/// compiled with the caller's blocks and linked like any other call, so a call costs a few native instructions instead
/// of a round trip through the HLE dispatcher (on the phone, a native-to-managed transition per call; F1 2011 makes
/// thousands per frame). Each routine returns exactly what its C# twin in Gxm.cs returns, checks in the same order.
/// VITASHARP_HLE_GUEST_CODE=0 binds the C# versions again.
/// </summary>
public static class HleGuestCode
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable(EnvVars.HleGuestCode) is not ("0" or "off");

    // A32 encodings used below (condition in bits 31..28: E always, 0 EQ, 1 NE, 2 HS, 3 LO)
    const uint BxLr = 0xE12FFF1E;

    /// <summary>r0 = 0x805B0000 | low, when the condition holds, then return (MOVW, MOVT, BX LR under <paramref name="cond"/>).</summary>
    static uint[] ReturnGxmError(uint cond, uint low) =>
        [(cond << 28) | 0x03000000 | low, (cond << 28) | 0x0348005B, (cond << 28) | 0x012FFF1E];

    const uint ErrInvalidValue = 3, ErrInvalidPointer = 4; // SCE_GXM_ERROR_INVALID_VALUE / _POINTER low halves
    const uint EQ = 0x0, NE = 0x1, HS = 0x2, LO = 0x3;

    /// <summary>sceGxmPrecomputed{Vertex,Fragment}StateSetUniformBuffer(state, index, data) (Gxm.PrecomputedSetUniformBuffer).</summary>
    static readonly uint[] SetUniformBuffer =
    [
        0xE3500000, .. ReturnGxmError(EQ, ErrInvalidPointer), // cmp r0, #0 ; state == NULL
        0xE351000E, .. ReturnGxmError(HS, ErrInvalidValue),   // cmp r1, #14 ; index >= 14
        0xE3520000, .. ReturnGxmError(EQ, ErrInvalidPointer), // cmp r2, #0 ; data == NULL
        0xE1D030BA,                                           // ldrh r3, [r0, #10]   buffer count
        0xE1510003,                                           // cmp r1, r3
        0x3590300C,                                           // ldrlo r3, [r0, #12]  buffer array
        0x37832101,                                           // strlo r2, [r3, r1, lsl #2]
        0xE3A00000,                                           // mov r0, #0
        BxLr,
    ];

    /// <summary>sceGxmPrecomputed{Vertex,Fragment}StateGetDefaultUniformBuffer(state): buffer 14 when the state has 15 slots.</summary>
    static readonly uint[] GetDefaultUniformBuffer =
    [
        0xE1D010BA, // ldrh r1, [r0, #10]
        0xE351000F, // cmp r1, #15
        0x33A00000, // movlo r0, #0
        0x2590100C, // ldrhs r1, [r0, #12]
        0x25910038, // ldrhs r0, [r1, #56]
        BxLr,
    ];

    /// <summary>sceGxmSetPrecomputed{Vertex,Fragment}State(context, state): the context's guest word
    /// (<see cref="VitaSharp.Core.Gxm.GxmContext.PrecomputedVertexStateOffset"/>); a NULL context is ignored.</summary>
    static uint[] SetContextWord(uint offset) =>
    [
        0xE3500000,               // cmp r0, #0
        0x15801000 | offset,      // strne r1, [r0, #offset]
        BxLr,
    ];

    /// <summary>sceGxmSetCullMode(context, mode): the mode's low byte into the context's cull byte.</summary>
    static uint[] SetContextByte(uint offset) =>
    [
        0xE3500000,               // cmp r0, #0
        0x15C01000 | offset,      // strbne r1, [r0, #offset]
        BxLr,
    ];

    /// <summary>sceGxmSet{Front,Back}PolygonMode(context, mode): (byte)(mode >> 15) into the context's byte.</summary>
    static uint[] SetContextByteShift15(uint offset) =>
    [
        0xE3500000,               // cmp r0, #0
        0x11A017A1,               // lsrne r1, r1, #15
        0x15C01000 | offset,      // strbne r1, [r0, #offset]
        BxLr,
    ];

    /// <summary>VITASHARP_HLE_GUEST_CODE=precomputed: only the precomputed-state routines (not the context's cull and
    /// polygon-mode setters).</summary>
    static readonly bool ContextModes = Environment.GetEnvironmentVariable(EnvVars.HleGuestCode) != "precomputed";

    static readonly (string Name, uint[] Code)[] ContextModeRoutines =
    [
        ("sceGxmSetCullMode", SetContextByte(VitaSharp.Core.Gxm.GxmContext.CullModeOffset)),
        ("sceGxmSetFrontPolygonMode", SetContextByteShift15(VitaSharp.Core.Gxm.GxmContext.FrontPolygonModeOffset)),
        ("sceGxmSetBackPolygonMode", SetContextByteShift15(VitaSharp.Core.Gxm.GxmContext.BackPolygonModeOffset)),
    ];

    static readonly (string Name, uint[] Code)[] Routines =
    [
        ("sceGxmPrecomputedVertexStateSetUniformBuffer", SetUniformBuffer),
        ("sceGxmPrecomputedFragmentStateSetUniformBuffer", SetUniformBuffer),
        ("sceGxmPrecomputedVertexStateGetDefaultUniformBuffer", GetDefaultUniformBuffer),
        ("sceGxmPrecomputedFragmentStateGetDefaultUniformBuffer", GetDefaultUniformBuffer),
        ("sceGxmSetPrecomputedVertexState", SetContextWord(VitaSharp.Core.Gxm.GxmContext.PrecomputedVertexStateOffset)),
        ("sceGxmSetPrecomputedFragmentState", SetContextWord(VitaSharp.Core.Gxm.GxmContext.PrecomputedFragmentStateOffset)),
        .. (ContextModes ? ContextModeRoutines : Array.Empty<(string, uint[])>()),
    ];

    static readonly Dictionary<uint, int> RoutineByNid = BuildNidTable();

    static Dictionary<uint, int> BuildNidTable()
    {
        var d = new Dictionary<uint, int>();
        for (int i = 0; i < Routines.Length; i++)
            foreach (var n in NidDatabase.LookupName(Routines[i].Name))
                if (n.Library == "SceGxm") d[n.Nid] = i;
        return d;
    }

    /// <summary>True when <paramref name="nid"/> has a guest-code implementation.</summary>
    public static bool Has(uint nid) => Enabled && RoutineByNid.ContainsKey(nid);

    // the routines of each address space use (AddressSpace.Id): written once, on the first import that needs them
    static readonly Dictionary<(AddressSpace, int), uint[]> Placed = new();

    /// <summary>Guest address (ARM) of the routine for <paramref name="nid"/> in the calling thread's address space, or 0.</summary>
    public static uint AddressOf(uint nid, GuestMemory memory)
    {
        if (!Enabled || !RoutineByNid.TryGetValue(nid, out int index) || AddressSpace.Current is not { } space) return 0;
        lock (Placed)
        {
            if (!Placed.TryGetValue((space, space.Id), out var addrs))
            {
                uint bytes = 0;
                foreach (var r in Routines) bytes += (uint)r.Code.Length * 4;
                uint at = memory.Alloc(bytes, "HLE guest code", MemoryRegion.User, 16);
                addrs = new uint[Routines.Length];
                for (int i = 0; i < Routines.Length; i++)
                {
                    addrs[i] = at;
                    foreach (uint w in Routines[i].Code) { GuestMemory.W32(at, w); at += 4; }
                }
                Placed[(space, space.Id)] = addrs;
            }
            return addrs[index];
        }
    }
}
