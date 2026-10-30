# Mobile GPUs: capability and quirk layer (branch feat/adreno)

Preparation for the Android port: the Vulkan renderer no longer assumes a desktop GPU. Every optional feature it
uses is checked against one capabilities object built at device creation, known driver bugs of Qualcomm's Adreno
driver (and of the other mobile blobs) are worked around behind quirk flags, and tile-based GPUs get per-scene
load/store ops. None of this was run on a phone: there is no Adreno or Mali hardware here. The mobile paths were
exercised on the GTX 1650 with emulated device profiles (`--gpu-profile=adreno|mali`).

## Where things are

| File | |
|---|---|
| `src/VitaSharp.Core/Gpu/GpuQuirks.cs` | `GpuQuirkTable` (the table, the test profiles, the selection logic), `GpuCapabilities`, `GpuDeviceInfo`. Pure C#, unit-tested (`tests/VitaSharp.Tests/GpuQuirkTests.cs`) |
| `src/VitaSharp.Renderer.Vulkan/VkCore.cs` | gathers features, extensions, limits and `VkPhysicalDeviceDriverProperties`, builds `VkCore.Caps`, enables only what `Caps` allows, logs `Caps.Describe()` once at startup |
| `VulkanRenderer.Mobile.cs` | texture-unit cap for small descriptor limits, the "Mobile paths" summary (info.txt) |
| `VulkanRenderer.Tiler.cs` | tile-based load/store ops, load/store counters (`PERF gpu` line, info.txt) |
| `VulkanRenderer.StaticState.cs` | static fallback for broken extended dynamic state |
| `BcSoftware.cs` | CPU BC decode with a content-addressed cache |
| `ShaderGenerator.LowerDynamicIndexing` | switch lowering of dynamically indexed register words |
| `src/VitaSharp.Core/Gpu/CustomGpuDriver.cs` | custom driver packages (meta.json + zip), store, settings |
| `src/VitaSharp.Renderer.Vulkan/IVulkanDriverProvider.cs`, `VulkanLoader.DriverProvider` | hook that replaces the system Vulkan driver |
| `src/VitaSharp.Android/Gpu/AdrenoToolsDriverLoader.cs` | libadrenotools implementation of the hook (in the app, not run yet) |

Switches: `--gpu-profile=adreno|mali|desktop` (`run`, `test`, scenario `args=`, `replay-gpu`) or
`VITASHARP_GPU_PROFILE`; `VITASHARP_GPU_QUIRKS=+Quirk,-Quirk,-Feature` forces quirks on/off or removes features
(for example `-StaticExtendedDynamicState` on a phone to check whether the workaround is still needed, or
`+TileBased,-Timestamps` on the desktop).

## Capabilities and fallbacks

Every optional feature the renderer touches, what it is used for, and what happens without it. "Not used" features
are queried so the log and info.txt say what the device has, and are never enabled.

| Feature / limit | Used for | Without it |
|---|---|---|
| `textureCompressionBC` | GXM UBC1-UBC5, SBC4/SBC5 sampled compressed | CPU decode to RGBA8 (BC4: R001, BC5: RG01; SBC4/SBC5 to R8G8B8A8_SNORM, exact RGTC signed decode), cached by content hash (64 MB LRU). GXM has no BC6H/BC7. Android: libadrenotools' BCeNabler may enable BCn on Qualcomm drivers that hide it (`IVulkanDriverProvider.TryEnableBcn`). ASTC transcoding: hook point documented in `BcSoftware.cs`, not implemented |
| `shaderStorageImageWriteWithoutFormat` | not used | - |
| `sampleRateShading` | programmable blending in MSAA scenes per sample (`gl_SampleID`) | shaders read sample 0 (part of the shader cache key, as before) |
| framebuffer sample counts | GXM 2X/4X downscale MSAA | 2X with 4 samples when the device has 4 but not 2 (Mali, `Msaa2xAs4x`); no usable count: 2x2 supersampling (existing fallback) |
| `VK_EXT_rasterization_order_attachment_access` / `VK_ARM_...` | programmable blending in MSAA render passes | input attachment + by-region barrier per draw, small draws split per triangle (as before). With it: the subpass and pipelines declare rasterization-order colour access, no barriers, no splitting. Not present on the GTX 1650 / Iris Xe: **untested** |
| `VK_EXT_memory_budget` | eviction under memory pressure, VRAM logs | pressure = renderer bytes / 80% of the largest device heap; 50% on mobile/unified-memory devices (`ConservativeMemoryBudget`: Android kills apps before the heap is full) |
| `maxPushConstantsSize` | 32 bytes of push constants | 128 is the Vulkan minimum: always fits |
| per-stage samplers / sampled images, `maxDescriptorSetSamplers` | 16 vertex + 16 fragment texture units + 2 framebuffer-fetch samplers | fewer units bound (Mali Bifrost: 16 per stage = 16 vertex, 14 fragment); a draw whose program samples a missing unit is skipped with a warning |
| `maxBoundDescriptorSets` | 4 sets | 4 is the Vulkan minimum: always fits |
| `geometryShader`, `logicOp`, 16-bit storage, `shaderInt16` | not used (GXM has no logic op; the generator packs halves with `packHalf2x16`) | - |
| `depthClamp` | GXM does not clip at the near plane | the near plane clips (as before) |
| `fillModeNonSolid` | GXM line/point polygon modes | drawn filled (as before) |
| `wideLines` | GXM line width | 1-pixel lines (as before) |
| timestamps | `PERF gpu` GPU time | "no timestamps" (the load/store counters are still printed) |
| pipeline cache | driver's `VkPipelineCache` blob per title | header checked against vendor, device, driver version and UUID (as before); Qualcomm drivers (`PipelineCachePerDriverBuild`) get one file per driver build (hash of driver ID, name, info string and version) because custom drivers replace the system one without changing the IDs |

Also checked but not a quirk: shaders are compiled for the Vulkan 1.0 SPIR-V environment (SPIR-V 1.0), which every
mobile driver accepts. The renderer does require Vulkan 1.3 (dynamic rendering, synchronization2, extended dynamic
state as core features): Adreno 6xx on older drivers exposes only Vulkan 1.1 and is rejected at startup (see below).

## Quirk table

Selected by `GpuQuirkTable.Select` from the vendor ID, `VkPhysicalDeviceDriverProperties::driverID` and the driver
version (without driver properties: vendor plus Vita3K's rule, a Qualcomm major version below 100 is Turnip).
"All versions" means the references disable the feature on every version of that driver; no fixed version is known.

| Quirk | Applies to | Workaround | Source |
|---|---|---|---|
| `TileBased` | Qualcomm (blob and Turnip), ARM, Imagination, Apple, Broadcom | per-scene load/store ops (below) | architecture |
| `DynamicIndexLowering` | Qualcomm proprietary, all versions | USSE indexed operands (`r[i >> 2][i & 3]`, `OpVectorExtractDynamic`/`OpVectorInsertDynamic`) go through `dynR_<bank>`/`dynW_<bank>` switch helpers. VitaSharp has no sampler or UBO descriptor arrays (textures are separate bindings, uniforms one SSBO), so the register banks are the only dynamically indexed objects | Dolphin `BUG_BROKEN_DYNAMIC_SAMPLER_INDEXING` (lowered to a switch); yuzu `has_broken_spirv_subgroup_mask_vector_extract_dynamic` (Qualcomm) |
| `StaticExtendedDynamicState` | Qualcomm proprietary, all versions | cull mode, front face, topology, depth test/write/compare, stencil test/ops baked into the pipeline key and pipeline (`PipelineKey.Static`, 0 elsewhere); only Vulkan 1.0 dynamic state remains. Background pipeline prediction includes the static state; pipelines.list recording and prewarm are off (recipes do not record it) | yuzu: "Qualcomm drivers have broken VK_EXT_extended_dynamic_state" |
| `StaticDepthBiasEnable` | Qualcomm proprietary, all versions | depth bias always enabled in the pipeline, zero bias when the draw has none (exact) instead of `vkCmdSetDepthBiasEnable` | yuzu: VK_EXT_extended_dynamic_state2 reported but broken on newer Qualcomm drivers |
| `AvoidPushDescriptors` | Qualcomm, ARM, Imagination proprietary | none needed: the renderer allocates descriptor sets | yuzu (slow on Qualcomm), DuckStation ("broken on Adreno v502") |
| `AvoidClearAttachments` | Qualcomm, ARM proprietary | none needed: clears are load ops, or `vkCmdClearColorImage` outside renderings | precautionary; DuckStation avoids it in feedback passes / on NVIDIA |
| `DynamicRenderingSuspect` | Qualcomm, ARM, Imagination proprietary | **not worked around**: logged so device reports point at it. Single-sampled scenes use `VK_KHR_dynamic_rendering`; MSAA scenes already use render passes | DuckStation disables dynamic rendering on these blobs |
| `PipelineCachePerDriverBuild` | Qualcomm (blob and Turnip) | one driver-cache file per driver build | custom drivers via libadrenotools (Vita3K, yuzu) |
| `TurnipDriver` | Qualcomm on Mesa Turnip | none of the blob quirks | Vita3K: "say it is a turnip driver if the major driver version is less than 100" |
| `CustomDriverCapable` | Qualcomm | the Android UI may offer custom drivers | Vita3K `support_custom_drivers()` |
| `Msaa2xAs4x` | any device with 4 but not 2 framebuffer samples | 2X scenes render with 4 samples | capability |
| `ConservativeMemoryBudget` | tile-based or unified-memory device without `VK_EXT_memory_budget` | eviction at 50% of the heap | capability |

The proprietary-only quirks key off the driver, not the vendor: a Turnip driver on the same Adreno gets none of
them. Shader-generator changes are opt-in through `ShaderGenerator.LowerDynamicIndexing`, which is part of the shader
pack key; with it off the generated GLSL is byte-identical to before (`GeneratorVersion` stays 13).

## Tile-based load/store ops

On `TileBased` devices single-sampled scene renderings are recorded into a secondary command buffer and begun in
the primary only when they end, as multisampled renderings always were, so the store ops are chosen knowing whether
the scene goes on:

- colour: always LOAD and STORE (the surface is guest memory and the Vita loads it into the tiles at scene start);
- depth/stencil: CLEAR unless the scene force-loads it, else LOAD; STORE while the scene continues; at the scene end
  DONT_CARE unless `SCE_GXM_DEPTH_STENCIL_FORCE_STORE` is set or the surface was ever read back (depth/stencil
  texture or mapped read; kept conservatively, GXM itself stores only when forced);
- MSAA scenes (unchanged): samples start DONT_CARE (the scene-start copy is drawn into them), resolve inside the
  render pass, are discarded at the scene end; depth as above.

Desktop GPUs keep beginning renderings directly (no behaviour change). The ops of every scene rendering are counted
and printed in the `PERF gpu` line and info.txt, e.g.
`load/store ops (tile-based, 2704 deferred renderings): colour load 2704/clear 0/dont-care 0, store 2704/dont-care 0; depth load 899/clear 1805/dont-care 0, store 899/dont-care 1805` (skullgirls_boot, adreno profile).

## Custom drivers (Android)

`VulkanLoader.DriverProvider` (`IVulkanDriverProvider`) replaces the system loader: `Open()` returns a library
handle exporting `vkGetInstanceProcAddr`, wrapped in a Silk.NET `LamdaNativeContext`; 0 falls back to the system
driver. `TryEnableBcn` is called when the device reports no `textureCompressionBC`; true makes the renderer sample
BCn without enabling the feature (BCeNabler patches `vkGetPhysicalDeviceFormatProperties`).

`VitaSharp.Android/Gpu/AdrenoToolsDriverLoader.cs` implements it with libadrenotools (`adrenotools_open_libvulkan`
with `ADRENOTOOLS_DRIVER_CUSTOM` / `FILE_REDIRECT`, `adrenotools_get_bcn_type` + `adrenotools_patch_bcn`,
`adrenotools_set_turbo`). Driver packages are the community format (zip with `meta.json`: `schemaVersion` 1, `name`,
`vendor`, `driverVersion`, `minApi`, `libraryName`), installed by `CustomDriverStore` into
`<internal>/gpu_drivers/<id>/` (the app's internal files folder: the linker refuses libraries on shared storage;
`meta.json` at the top of the zip or inside its one folder; nested paths and unsafe names skipped; files made
read-only, as Android 14 requires of dynamically loaded code); the choice lives in `GpuDriverSettings`
(`gpu_driver.json`: driver id, file redirect, turbo, BCn patch). Store, settings and the "Driver da GPU" model are
unit-tested on the desktop (`GpuQuirkTests`, `AndroidMediaDriverTests`).

libadrenotools is built from source by `build/build-android-adrenotools.ps1` (or `.sh`): the pinned commit
(8fae8ce, with liblinkernsbypass aa39758) is fetched into `third_party/android-adrenotools/src` and compiled with
ndk-build (`build/android-adrenotools/jni`, the upstream CMake modules without needing CMake; arm64-v8a, libc++ static
and hidden, 16 KiB-page aligned) into `third_party/android-adrenotools/libs/arm64-v8a/`: `libadrenotools.so` (283 KB)
and the hooks `libhook_impl.so`, `libmain_hook.so`, `libfile_redirect_hook.so`, `libgsl_alloc_hook.so`. The app
packages them when they exist (about 140 KB compressed) and declares `android:extractNativeLibs="true"`, because
libadrenotools loads the hooks by path from the native library directory.

The game session installs `AdrenoToolsDriverLoader` before the renderer starts. It uses the system loader directly
when nothing needs libadrenotools (system driver, no option), when the GPU is not an Adreno (`/dev/kgsl-3d0` absent),
when libadrenotools or a hook is missing from the native library directory, or when the package needs a newer API.
Otherwise `adrenotools_open_libvulkan` gets `ADRENOTOOLS_DRIVER_CUSTOM` for a package and
`ADRENOTOOLS_DRIVER_FILE_REDIRECT` with `<internal>/gpu_driver_files/` for the redirect option; BCeNabler runs from
`TryEnableBcn` (`adrenotools_get_bcn_type`, then `adrenotools_patch_bcn`), turbo with `adrenotools_set_turbo`. A
process loads one driver, so the game process records its configuration in `<internal>/run/game.json`; after a change,
"Driver da GPU" offers to restart a game that runs with another one (the UI kills the `:game` process and starts the
game again). None of this has run on a device.

## What was tested (GTX 1650, emulated profiles)

All on the GTX 1650 (driver 546.17), Release build. Game runs are not deterministic (timing: animations, loading
dots, menu cursors), so rendering was compared on GPU dumps replayed with `replay-gpu --compare` (the same
commands every time; pixel-exact) and the game runs were checked for crashes, new warnings and visible differences.
Other agents were running games on the same PC at the time, so frame rates and GPU times below are noisy.

**Desktop default path (no profile) against the base commit (fd26516):**

- GLSL of all 124 programs of Killzone, Skullgirls and Knightin'+ (`shadertest --dump`) byte-identical: the
  generator change is opt-in, `GeneratorVersion` stays 13.
- Killzone dump (frames 4200-4300, 31843 draws, 4X MSAA, BC1/3/4 textures): 101/101 frames pixel-identical.
  Skullgirls dump (frames 1300-1400): 101/101 pixel-identical.
- kz_skin, knightin_boot and skullgirls_boot game runs: same frames apart from timing, same warnings, no new log
  lines except the capability block at startup. The PERF report gains only the load/store counters.

**Each workaround alone on the Killzone dump** (desktop GPU, `VITASHARP_GPU_QUIRKS`): `+TileBased`,
`+StaticExtendedDynamicState,+StaticDepthBiasEnable` and `+DynamicIndexLowering` are pixel-identical (101/101);
`-SampleRateShading` (sample 0) mean difference 0.010; `-TextureCompressionBC` (CPU decode) mean difference at most
0.79 of 255, max 17 (rounding: the hardware decodes BC4/BC5 at more than 8 bits and NVIDIA rounds its BC1
interpolation differently; the first decoder version, truncating, was 1.03 / max 82). The lowered GLSL compiles for
all 3504 programs of the 22 titles `shadertest` can scan (24 of them use dynamic indexing).

**Profiles:**

| | Killzone dump | Skullgirls dump | game runs |
|---|---|---|---|
| `--gpu-profile=adreno` | 99/101 frames differ, worst mean 0.79 (BC decode only) | worst mean 0.15 (BC decode only) | kz_skin, knightin_boot, skullgirls_boot: run to the end, frames as on desktop |
| `--gpu-profile=mali` | as adreno | worst mean 4.3 on one frame: without depthClamp the near plane clips one of the white HUD triangles of the known Skullgirls white-box frame (that frame is already wrong on desktop); otherwise as adreno | knightin_boot as on desktop |

Fallback activity (adreno profile): Killzone 749 pipelines instead of 247 (static state; first session all compiled
on the GPU thread, 2.7 s total, worst frame stall 714 ms; this run predates the change that lets background prediction carry the static key; with it, skullgirls_boot built
all 13 static-state pipelines on the workers, 0 on the GPU thread), GPU-worker CPU 4.8 -> 6.7 ms/frame (BC decode + pipelines), 4525 BC levels
decoded of which 1182 came from the decode cache; Skullgirls 149 decoded, 162 from the cache. Mali profile on
Skullgirls: 14 fragment texture units, no draw skipped; 2X scenes would render with 4 samples (none in these games).

**Tile-based load/store (counters, adreno profile):** depth/stencil stores discarded at scene end - Knightin'+
2225 of 2241 renderings, Skullgirls 1805 of 2704, Killzone 6997 of 97821 (Killzone breaks each scene into ~13
renderings for texture uploads and copies; only the last one can discard, see the roadmap item). Colour always
loads and stores.

**Frame rates** (noisy, shared PC): kz_skin 28.0 (branch) / 28.4 (base) / 27.2 fps (adreno profile), knightin_boot
29.3 / 29.3 / 29.4, skullgirls_boot 28.8 / 34.0 / 39.1. GPU time per frame within noise (kz 5.07 / 5.18 / 3.91 ms).

**F10 capture:** `--diag-at=1200` under the adreno profile writes the capability block, the "Mobile paths" line and
the load/store counters into info.txt (and renderer.txt).

## What needs a real device

- Everything Adreno/Mali-specific actually running: the profiles only remove features and pretend to be the vendor;
  they cannot reproduce the driver bugs themselves, so whether each quirk is still needed (or sufficient) has to be
  checked on devices, ideally old (512.5xx) and new (512.7xx+) Adreno blobs, Turnip, and Mali r3x/r4x. Use
  `VITASHARP_GPU_QUIRKS=-Quirk` to turn a workaround off.
- `VK_EXT/ARM_rasterization_order_attachment_access`: the code path (subpass flag, pipeline flag, no barriers or
  per-triangle splits) has never executed; neither desktop GPU here has the extension.
- Real tile-memory savings: the counters show which ops are chosen; bandwidth/power needs the device's profiler
  (Snapdragon Profiler, Arm Performance Studio).
- `DynamicRenderingSuspect`: if dynamic rendering misbehaves on a blob, single-sampled scenes need a render-pass path
  (the MSAA code already has one to generalise).
- Vulkan 1.1 devices (many Adreno 6xx on older drivers): rejected today; supporting them needs render passes instead
  of dynamic rendering, `VK_KHR_synchronization2` or classic barriers, and the static-state path above.
- libadrenotools: building it for arm64-v8a, packaging the hook libraries, verifying that Silk.NET resolves all
  commands through the returned handle, the "Driver da GPU" screen, and BCeNabler on real blobs.
- ASTC transcoding of BCn/PVRTC (memory: RGBA8 is 4-8x larger than the compressed data) and a disk cache of decoded
  textures.
