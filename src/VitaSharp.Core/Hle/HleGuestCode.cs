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

    /// <summary>sceGxmTextureSet{U,V}AddrModeSafe(texture, mode) (Gxm.cs): a NULL texture is INVALID_POINTER, a linear
    /// strided one is left alone (0), otherwise the 3-bit field at <paramref name="shift"/> of word 0 takes the mode.</summary>
    static uint[] SetAddrModeSafe(int shift) =>
    [
        0xE3500000, .. ReturnGxmError(EQ, ErrInvalidPointer), // cmp r0, #0
        0xE5902004,                                  // ldr r2, [r0, #4]
        0xE1A02EA2,                                  // lsr r2, r2, #29   texture type
        0xE3520006,                                  // cmp r2, #6        SCE_GXM_TEXTURE_LINEAR_STRIDED >> 29
        0x03A00000,                                  // moveq r0, #0
        0x012FFF1E,                                  // bxeq lr
        0xE5902000,                                  // ldr r2, [r0]
        shift == 6 ? 0xE3C22D07u : 0xE3C22038u,      // bic r2, r2, #(7 << shift)
        0xE2011007,                                  // and r1, r1, #7
        0xE1822001u | ((uint)shift << 7),            // orr r2, r2, r1, lsl #shift
        0xE5802000,                                  // str r2, [r0]
        0xE3A00000,                                  // mov r0, #0
        BxLr,
    ];

    /// <summary>Placeholder for an SVC into the routine's own C# version (<see cref="AddressOf"/> fills the index in):
    /// the cases a routine leaves to C#, entered with the caller's registers and stack.</summary>
    const uint FallbackSvc = 0xEF00FFFF;

    /// <summary>sceGxmSetUniformDataF(buffer, param, componentOffset, componentCount, source): the F32 parameters that
    /// are a scalar array or a single vector (no per-element padding) copy componentCount words to
    /// buffer + resourceIndex * 4 + componentOffset * 4; every other case (and the errors after the NULL checks'
    /// INVALID_VALUE) is the C# version's.</summary>
    static readonly uint[] SetUniformDataF =
    [
        0xE92D0030,                                  // push {r4, r5}
        0xE59DC008,                                  // ldr r12, [sp, #8]   source (5th argument)
        0xE3500000,                                  // cmp r0, #0
        0x13510000,                                  // cmpne r1, #0
        0x135C0000,                                  // cmpne r12, #0
        0x08BD0030,                                  // popeq {r4, r5}
        .. ReturnGxmError(EQ, ErrInvalidPointer),
        0xE5D14004,                                  // ldrb r4, [r1, #4]
        0xE204500F,                                  // and r5, r4, #15
        0xE3550001,                                  // cmp r5, #1          a uniform
        0x18BD0030,                                  // popne {r4, r5}
        .. ReturnGxmError(NE, ErrInvalidValue),
        0xE1B04224,                                  // lsrs r4, r4, #4     type: F32 is 0
        0x1A00000F,                                  // bne fallback
        0xE5915008,                                  // ldr r5, [r1, #8]    array size
        0xE3550001,                                  // cmp r5, #1
        0x15D15005,                                  // ldrbne r5, [r1, #5]
        0x1205500F,                                  // andne r5, r5, #15   component count
        0x13550001,                                  // cmpne r5, #1
        0x1A000009,                                  // bne fallback
        0xE591500C,                                  // ldr r5, [r1, #12]   resource index
        0xE0800105,                                  // add r0, r0, r5, lsl #2
        0xE0800102,                                  // add r0, r0, r2, lsl #2
        0xE2533001,                                  // loop: subs r3, r3, #1
        0x249C5004,                                  // ldrhs r5, [r12], #4
        0x24805004,                                  // strhs r5, [r0], #4
        0x2AFFFFFB,                                  // bhs loop
        0xE3A00000,                                  // mov r0, #0
        0xE8BD0030,                                  // pop {r4, r5}
        BxLr,
        0xE8BD0030,                                  // fallback: pop {r4, r5} (r0-r3 and sp are the caller's)
        FallbackSvc,                                 // svc: the C# version
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
        ("sceGxmTextureSetUAddrModeSafe", SetAddrModeSafe(6)),
        ("sceGxmTextureSetVAddrModeSafe", SetAddrModeSafe(3)),
        ("sceGxmSetUniformDataF", SetUniformDataF),
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

    static uint NidOf(string name) => NidDatabase.LookupName(name).First(n => n.Library == "SceGxm").Nid;

    /// <summary>Guest address (ARM) of the routine for <paramref name="nid"/> in the calling thread's address space, or 0.
    /// <paramref name="svcIndexOf"/> gives the SVC number of a NID's C# version (for <see cref="FallbackSvc"/>).</summary>
    public static uint AddressOf(uint nid, GuestMemory memory, Func<uint, uint> svcIndexOf)
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
                    foreach (uint w in Routines[i].Code)
                    {
                        GuestMemory.W32(at, w == FallbackSvc ? 0xEF000000 | (svcIndexOf(NidOf(Routines[i].Name)) & 0xFFFFFF) : w);
                        at += 4;
                    }
                }
                Placed[(space, space.Id)] = addrs;
            }
            return addrs[index];
        }
    }
}
