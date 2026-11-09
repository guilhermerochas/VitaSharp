using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle;

/// <summary>
/// HLE functions implemented as guest ARM code instead of an SVC into C#. On the Vita these are user-mode library code
/// (libgxm's precomputed-state packing: a few loads and stores on memory whose layout is ours; SceLibKernel's TLS lookups
/// and small Clib copies and fills). As guest code they are
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

    /// <summary>The A32 modified-immediate field (rotation and 8 bits) of <paramref name="v"/>.</summary>
    static uint Imm(uint v)
    {
        for (int rot = 0; rot < 16; rot++)
        {
            uint r = rot == 0 ? v : (v << (2 * rot)) | (v >> (32 - 2 * rot));
            if (r <= 0xFF) return ((uint)rot << 8) | r;
        }
        throw new ArgumentException($"0x{v:X} is not an A32 modified immediate");
    }

    /// <summary>Linear strided texture (word 1's type field is SCE_GXM_TEXTURE_LINEAR_STRIDED): return 0 (Gxm.IsStrided).</summary>
    static readonly uint[] SkipStrided =
    [
        0xE5902004, // ldr r2, [r0, #4]
        0xE1A02EA2, // lsr r2, r2, #29
        0xE3520006, // cmp r2, #6
        0x03A00000, // moveq r0, #0
        0x012FFF1E, // bxeq lr
    ];

    /// <summary>sceGxmTextureSet*(texture, value) = Gxm.SetBits(texture, shift, bits, value) on word 0, after the linear
    /// strided check when <paramref name="strided"/>; <paramref name="boolean"/>: the value is value != 0.</summary>
    static uint[] SetTextureField(int shift, int bits, bool strided, bool boolean = false)
    {
        uint low = (1u << bits) - 1;
        return
        [
            .. (strided ? SkipStrided : []),
            0xE3500000, .. ReturnGxmError(EQ, ErrInvalidPointer), // cmp r0, #0
            0xE5902000,                                  // ldr r2, [r0]
            0xE3C22000 | Imm(low << shift),              // bic r2, r2, #mask
            .. (boolean ? (uint[])[0xE3510000, 0x13A01001] : []), // cmp r1, #0 ; movne r1, #1
            0xE2011000 | Imm(low),                       // and r1, r1, #low
            0xE1822001 | ((uint)shift << 7),             // orr r2, r2, r1, lsl #shift
            0xE5802000,                                  // str r2, [r0]
            0xE3A00000,                                  // mov r0, #0
            BxLr,
        ];
    }

    /// <summary>sceGxmTextureSetLodMin(texture, lodMin) (Gxm.cs): bits 0-1 of word 2 take lodMin >> 2, bits 26-27 of word 3
    /// take lodMin &amp; 3, each a SetBits with its own NULL check of the word's address.</summary>
    static readonly uint[] SetLodMin =
    [
        .. SkipStrided,
        0xE2903008,                // adds r3, r0, #8
        Branch(EQ, 6, 22),         // beq error
        0xE5932000,                // ldr r2, [r3]
        0xE3C22003,                // bic r2, r2, #3
        0xE1A0C121,                // lsr r12, r1, #2
        0xE20CC003,                // and r12, r12, #3
        0xE182200C,                // orr r2, r2, r12
        0xE5832000,                // str r2, [r3]
        0xE290300C,                // adds r3, r0, #12
        Branch(EQ, 14, 22),        // beq error
        0xE5932000,                // ldr r2, [r3]
        0xE3C22303,                // bic r2, r2, #0x0C000000
        0xE201C003,                // and r12, r1, #3
        0xE1822D0C,                // orr r2, r2, r12, lsl #26
        0xE5832000,                // str r2, [r3]
        0xE3A00000,                // mov r0, #0
        BxLr,                      // 21
        .. ReturnGxmError(0xE, ErrInvalidPointer), // 22 error
    ];

    /// <summary>sceGxmTextureGetType(texture): word 1 &amp; 0xE0000000.</summary>
    static readonly uint[] GetTextureType = [0xE5900004, 0xE2000000 | Imm(0xE0000000), BxLr]; // ldr r0, [r0, #4] ; and r0, r0, #0xE0000000

    /// <summary>sceGxmProgramGetDefaultUniformBufferSize(program): the word at +0x64, times 4.</summary>
    static readonly uint[] GetDefaultUniformBufferSize = [0xE5900064, 0xE1A00100, BxLr]; // ldr r0, [r0, #0x64] ; lsl r0, r0, #2

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

    // ------------------------------------------------------------------ SceLibKernel's hot leaf functions
    //
    // A game's own code calls these thousands of times per frame (Sonic Mania's loader ~4,700 sceClibMemcpy and ~4,600
    // sceKernelGetTLSAddr per frame): through an SVC each one left native code for the C# dispatcher and came back,
    // which cost far more than the work. On the Vita they are user-mode SceLibKernel code too (the TLS lookups read
    // TPIDRURO, Clib's memcpy is a plain copy loop). Like PPSSPP's replacement table (Replace_memcpy & co. charge
    // ~10 + bytes / 4 cycles) and the inline TPIDRRO_EL0 read of Ryujinx's ARMeilleure, the common case never leaves
    // compiled code. Copies and fills longer than SmallCopy (where the host copy wins over the guest loop) and
    // overlapping copies whose destination is above the source (memmove's backward case) take the C# version.

    /// <summary>The longest copy or fill the guest loops below do themselves (bytes; above it the C# version runs).</summary>
    const uint SmallCopy = 256;
    const uint SmallCopyImm = 0xC01; // the A32 modified immediate of SmallCopy (0x01 ror 24)

    /// <summary>A32 B/BL-style branch from word <paramref name="from"/> to word <paramref name="to"/> of the same routine.</summary>
    static uint Branch(uint cond, int from, int to) => (cond << 28) | 0x0A000000 | ((uint)(to - from - 2) & 0xFFFFFF);

    const uint HI = 0x8, MI = 0x4, PL = 0x5;

    /// <summary>sceKernelGetThreadId(): the thread UID from the kernel TLS word TlsThreadId (Kernel/GuestThread.InitTls),
    /// which sits 0x800 - 4 bytes below TPIDRURO (the user TLS: GuestThread.Start).</summary>
    static readonly uint[] GetThreadId =
    [
        0xEE1D0F70, // mrc p15, 0, r0, c13, c0, 3   TPIDRURO
        0xE51007FC, // ldr r0, [r0, #-0x7FC]        kernel TLS word 1
        BxLr,
    ];

    /// <summary>sceKernelGetTLSAddr(key) (ThreadMgr.cs): kernel TLS base + key * 4 for key 0..0xFF, else NULL.</summary>
    static readonly uint[] GetTlsAddr =
    [
        0xE3500C01, // cmp r0, #0x100   (unsigned: a negative key is out of range too)
        0x23A00000, // movhs r0, #0
        0x212FFF1E, // bxhs lr
        0xEE1D1F70, // mrc p15, 0, r1, c13, c0, 3
        0xE2411B02, // sub r1, r1, #0x800
        0xE0810100, // add r0, r1, r0, lsl #2
        BxLr,
    ];

    /// <summary>sceClibMemcpy / _safe / Memmove(dst, src, len) (ProcessMgr.cs: memmove semantics, returns dst): a forward
    /// copy, 16 bytes per loop, then words, then bytes (unaligned word accesses are allowed on ARMv7). A copy whose
    /// destination lies in [src, src + len) needs the backward order: it falls back, like any copy over SmallCopy.</summary>
    static readonly uint[] Memcpy =
    [
        0xE3520000 | SmallCopyImm, //  0 cmp r2, #SmallCopy
        Branch(HI, 1, 31),         //  1 bhi fallback
        0xE040C001,                //  2 sub r12, r0, r1
        0xE15C0002,                //  3 cmp r12, r2         dst - src < len: dst inside the source
        Branch(LO, 4, 31),         //  4 blo fallback
        0xE1A0C000,                //  5 mov r12, r0         destination cursor (r0 is returned)
        0xE2522010,                //  6 subs r2, r2, #16
        Branch(LO, 7, 18),         //  7 blo words
        0xE4913004,                //  8 l16: ldr r3, [r1], #4
        0xE48C3004,                //  9 str r3, [r12], #4
        0xE4913004, 0xE48C3004,    // 10, 11
        0xE4913004, 0xE48C3004,    // 12, 13
        0xE4913004, 0xE48C3004,    // 14, 15
        0xE2522010,                // 16 subs r2, r2, #16
        Branch(HS, 17, 8),         // 17 bhs l16
        0xE292200C,                // 18 words: adds r2, r2, #12   (r2 = left - 4)
        Branch(MI, 19, 24),        // 19 bmi bytes
        0xE4913004,                // 20 w4: ldr r3, [r1], #4
        0xE48C3004,                // 21 str r3, [r12], #4
        0xE2522004,                // 22 subs r2, r2, #4
        Branch(PL, 23, 20),        // 23 bpl w4
        0xE2922004,                // 24 bytes: adds r2, r2, #4   (r2 = left, 0..3)
        0x012FFF1E,                // 25 bxeq lr
        0xE4D13001,                // 26 b1: ldrb r3, [r1], #1
        0xE4CC3001,                // 27 strb r3, [r12], #1
        0xE2522001,                // 28 subs r2, r2, #1
        Branch(NE, 29, 26),        // 29 bne b1
        BxLr,                      // 30
        FallbackSvc,               // 31 fallback: the C# version (r0-r2 untouched)
        BxLr,                      // 32
    ];

    /// <summary>sceClibMemset(dst, ch, len) (ProcessMgr.cs: the low byte of ch, returns dst), in the same shape as
    /// <see cref="Memcpy"/>.</summary>
    static readonly uint[] Memset =
    [
        0xE3520000 | SmallCopyImm, //  0 cmp r2, #SmallCopy
        Branch(HI, 1, 25),         //  1 bhi fallback
        0xE20110FF,                //  2 and r1, r1, #0xFF
        0xE1811401,                //  3 orr r1, r1, r1, lsl #8
        0xE1811801,                //  4 orr r1, r1, r1, lsl #16
        0xE1A0C000,                //  5 mov r12, r0
        0xE2522010,                //  6 subs r2, r2, #16
        Branch(LO, 7, 14),         //  7 blo words
        0xE48C1004,                //  8 l16: str r1, [r12], #4
        0xE48C1004, 0xE48C1004, 0xE48C1004, //  9-11
        0xE2522010,                // 12 subs r2, r2, #16
        Branch(HS, 13, 8),         // 13 bhs l16
        0xE292200C,                // 14 words: adds r2, r2, #12
        Branch(MI, 15, 19),        // 15 bmi bytes
        0xE48C1004,                // 16 w4: str r1, [r12], #4
        0xE2522004,                // 17 subs r2, r2, #4
        Branch(PL, 18, 16),        // 18 bpl w4
        0xE2922004,                // 19 bytes: adds r2, r2, #4
        0x012FFF1E,                // 20 bxeq lr
        0xE4CC1001,                // 21 b1: strb r1, [r12], #1
        0xE2522001,                // 22 subs r2, r2, #1
        Branch(NE, 23, 21),        // 23 bne b1
        BxLr,                      // 24
        FallbackSvc,               // 25 fallback
        BxLr,                      // 26
    ];

    /// <summary>VITASHARP_HLE_GUEST_CODE=precomputed: only the precomputed-state routines (not the context's cull and
    /// polygon-mode setters); =gxm: only libgxm's routines (not SceLibKernel's).</summary>
    static readonly string? Mode = Environment.GetEnvironmentVariable(EnvVars.HleGuestCode);
    static readonly bool ContextModes = Mode != "precomputed";
    static readonly bool KernelRoutines = Mode is not ("precomputed" or "gxm");
    /// <summary>=notex: without the texture-control-word and program-header routines (A/B).</summary>
    static readonly bool TextureRoutines = Mode is not ("precomputed" or "notex");

    const string Gxm = "SceGxm", LibKernel = "SceLibKernel";

    static readonly (string Name, string Library, uint[] Code)[] ContextModeRoutines =
    [
        ("sceGxmSetCullMode", Gxm, SetContextByte(VitaSharp.Core.Gxm.GxmContext.CullModeOffset)),
        ("sceGxmSetFrontPolygonMode", Gxm, SetContextByteShift15(VitaSharp.Core.Gxm.GxmContext.FrontPolygonModeOffset)),
        ("sceGxmSetBackPolygonMode", Gxm, SetContextByteShift15(VitaSharp.Core.Gxm.GxmContext.BackPolygonModeOffset)),
    ];

    static readonly (string Name, string Library, uint[] Code)[] KernelLeafRoutines =
    [
        ("sceKernelGetThreadId", LibKernel, GetThreadId),
        ("sceKernelGetTLSAddr", LibKernel, GetTlsAddr),
        ("sceClibMemcpy", LibKernel, Memcpy),
        ("sceClibMemcpy_safe", LibKernel, Memcpy),
        ("sceClibMemmove", LibKernel, Memcpy),
        ("sceClibMemset", LibKernel, Memset),
    ];

    static readonly (string Name, string Library, uint[] Code)[] TextureAndProgramRoutines =
    [
        ("sceGxmTextureGetType", Gxm, GetTextureType),
        ("sceGxmTextureSetUAddrMode", Gxm, SetTextureField(6, 3, strided: false)),
        ("sceGxmTextureSetVAddrMode", Gxm, SetTextureField(3, 3, strided: false)),
        ("sceGxmTextureSetMagFilter", Gxm, SetTextureField(12, 2, strided: false)),
        ("sceGxmTextureSetMinFilter", Gxm, SetTextureField(10, 2, strided: true)),
        ("sceGxmTextureSetMipFilter", Gxm, SetTextureField(9, 1, strided: true, boolean: true)),
        ("sceGxmTextureSetLodBias", Gxm, SetTextureField(21, 6, strided: true)),
        ("sceGxmTextureSetLodMin", Gxm, SetLodMin),
        ("sceGxmProgramGetDefaultUniformBufferSize", Gxm, GetDefaultUniformBufferSize),
    ];

    static readonly (string Name, string Library, uint[] Code)[] Routines =
    [
        ("sceGxmPrecomputedVertexStateSetUniformBuffer", Gxm, SetUniformBuffer),
        ("sceGxmPrecomputedFragmentStateSetUniformBuffer", Gxm, SetUniformBuffer),
        ("sceGxmPrecomputedVertexStateGetDefaultUniformBuffer", Gxm, GetDefaultUniformBuffer),
        ("sceGxmPrecomputedFragmentStateGetDefaultUniformBuffer", Gxm, GetDefaultUniformBuffer),
        ("sceGxmSetPrecomputedVertexState", Gxm, SetContextWord(VitaSharp.Core.Gxm.GxmContext.PrecomputedVertexStateOffset)),
        ("sceGxmSetPrecomputedFragmentState", Gxm, SetContextWord(VitaSharp.Core.Gxm.GxmContext.PrecomputedFragmentStateOffset)),
        ("sceGxmTextureSetUAddrModeSafe", Gxm, SetAddrModeSafe(6)),
        ("sceGxmTextureSetVAddrModeSafe", Gxm, SetAddrModeSafe(3)),
        ("sceGxmSetUniformDataF", Gxm, SetUniformDataF),
        .. (TextureRoutines ? TextureAndProgramRoutines : []),
        .. (ContextModes ? ContextModeRoutines : []),
        .. (KernelRoutines ? KernelLeafRoutines : []),
    ];

    static readonly Dictionary<uint, int> RoutineByNid = BuildNidTable();

    static Dictionary<uint, int> BuildNidTable()
    {
        var d = new Dictionary<uint, int>();
        for (int i = 0; i < Routines.Length; i++)
            foreach (var n in NidDatabase.LookupName(Routines[i].Name))
                if (n.Library == Routines[i].Library) d[n.Nid] = i;
        return d;
    }

    /// <summary>True when <paramref name="nid"/> has a guest-code implementation.</summary>
    public static bool Has(uint nid) => Enabled && RoutineByNid.ContainsKey(nid);

    // the routines of each address space use (AddressSpace.Id) and HLE registry (the SVC numbers of the fallbacks are the
    // registry's: processes sharing one space, or tests, each get their own copy): written once, on the first import
    static readonly Dictionary<(AddressSpace, int, object), uint[]> Placed = new();

    static uint NidOf(int routine) => NidDatabase.LookupName(Routines[routine].Name).First(n => n.Library == Routines[routine].Library).Nid;

    /// <summary>Guest address (ARM) of the routine for <paramref name="nid"/> in the calling thread's address space, or 0.
    /// <paramref name="svcIndexOf"/> gives the SVC number of a NID's C# version (for <see cref="FallbackSvc"/>) in the registry
    /// <paramref name="owner"/> (null: the delegate itself names it).</summary>
    public static uint AddressOf(uint nid, GuestMemory memory, Func<uint, uint> svcIndexOf, object? owner = null)
    {
        if (!Enabled || !RoutineByNid.TryGetValue(nid, out int index) || AddressSpace.Current is not { } space) return 0;
        lock (Placed)
        {
            var key = (space, space.Id, owner ?? svcIndexOf);
            if (!Placed.TryGetValue(key, out var addrs))
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
                        GuestMemory.W32(at, w == FallbackSvc ? 0xEF000000 | (svcIndexOf(NidOf(i)) & 0xFFFFFF) : w);
                        at += 4;
                    }
                }
                Placed[key] = addrs;
            }
            return addrs[index];
        }
    }
}
