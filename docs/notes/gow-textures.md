# God of War Collection: Kratos' "texture" problems on the phone (31/10/2026)

**Symptom** (user report, Redmi Note 14, Mali-G57 r54): Kratos and the undead legionnaires showed yellow, purple and
blue patches and a blotchy tattoo in God of War I gameplay. The desktop (GTX 1650) was correct with the `desktop` and
the `mali` GPU profiles.

**Not the cause** (each tried on the phone, patches unchanged): BC to ASTC transcoding (`VITASHARP_ASTC=0`, CPU RGBA8
decode), the per-draw texture descriptor-set reuse, the vertex-stream rebase with `vertexOffset`, zero-initialised
attribute/secondary-attribute registers, an integer (uvec4) register file against NaN canonicalisation, the `desktop`
GPU profile on the phone. `VITASHARP_NATIVE_VERIFY=1`: no JIT mismatch (69 reported in one block of the render thread
are a race on a shared queue word, the interpreter re-reading a value another thread changed). A GPU dump recorded on
the phone (`VITASHARP_GPU_DUMP_AT`, new on Android) replayed correctly on the desktop: guest data fine, so the phone's
Vulkan driver read something differently.

**Cause:** the character meshes' vertex streams have a 19-byte stride with F16 attributes at offsets 3 and 11
(`a[r8 s1+3 f8x4]`, `a[r12 s1+11 f8x2]` in the draw trace). Vulkan requires a vertex attribute's address to be a
multiple of its component size; GXM does not. NVIDIA reads misaligned attributes anyway, Mali returns garbage for them
(texture coordinates and colours: the patches).

**Fix (generic, all GPUs):** an attribute whose offset or stream stride is not a multiple of its component size is
marked in the vertex shader hints (`ShaderGenerator.ByteFetchFlag`) and fetched as R8 unsigned bytes, which need no
alignment; the vertex shader rebuilds each component (F16, U16/S16 scaled or normalised, F32, untyped words). No CPU
copy and no extra memory; aligned attributes keep exactly the shaders they had (the GLSL and the shader cache of games
without misaligned attributes do not change). Checked on the phone (`phone2_shots/gow_bytefetch`) and on the desktop
(replay of the phone dump, `--compare` against the previous build).

Desktop: the bug never showed there (NVIDIA tolerates the misalignment), so the shared renderer change is the whole
fix; nothing is left for the desktop revalidation.
