# Mobile GPUs: capability and quirk layer (branch feat/adreno)

Preparation for the Android port: the Vulkan renderer no longer assumes a desktop GPU. Every optional feature it
uses is checked against one capabilities object built at device creation, known driver bugs of Qualcomm's Adreno
driver (and of the other mobile blobs) are worked around behind quirk flags, and tile-based GPUs get per-scene
load/store ops. The mobile paths were first exercised on the GTX 1650 with emulated device profiles
(`--gpu-profile=adreno|mali`); since 30/10/2026 they have also run on a real Mali phone ([below](#real-device-mali-g57-mc2-30102026)).
No Adreno has run them yet.

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

Switches: `--gpu-profile=adreno|adreno-vk11|mali|desktop` (`run`, `test`, scenario `args=`, `replay-gpu`) or
`VITASHARP_GPU_PROFILE`; `VITASHARP_GPU_QUIRKS=+Quirk,-Quirk,-Feature` forces quirks on/off or removes features
(for example `-StaticExtendedDynamicState` on a phone to check whether the workaround is still needed, or
`+TileBased,-Timestamps` on the desktop).

## Capabilities and fallbacks

Every optional feature the renderer touches, what it is used for, and what happens without it. "Not used" features
are queried so the log and info.txt say what the device has, and are never enabled.

| Feature / limit | Used for | Without it |
|---|---|---|
| `textureCompressionBC` | GXM UBC1-UBC5, SBC4/SBC5 sampled compressed | with `textureCompressionASTC_LDR`: transcoded to ASTC 4x4 (UBC1-3 RGB/RGBA, UBC4 luminance, UBC5 luminance+alpha behind a view swizzle), with a per-title disk cache ([mobile renderer notes](mobile-renderer.md#2-astc-transcoding-with-a-disk-cache-coregpuastccs-astctranscodercs-coregputranscodediskcachecs)); otherwise, and for SBC4/SBC5, CPU decode to RGBA8 (BC4: R001, BC5: RG01; SBC4/SBC5 to R8G8B8A8_SNORM, exact RGTC signed decode), cached by content hash (64 MB LRU). GXM has no BC6H/BC7. Android: libadrenotools' BCeNabler may enable BCn on Qualcomm drivers that hide it (`IVulkanDriverProvider.TryEnableBcn`) |
| `textureCompressionASTC_LDR` | BC/PVRTC transcoded to ASTC 4x4 on devices without BC (enabled only then) | RGBA8 (as before); `VITASHARP_ASTC=0` forces that |
| dynamic rendering (Vulkan 1.3) | single-sampled scene renderings | render passes and framebuffers (`RenderPasses`, the Vulkan 1.1 path: [mobile renderer notes](mobile-renderer.md#3-the-vulkan-11-path-vulkanrendererrenderpassescs)) |
| extended dynamic state 1/2 (Vulkan 1.3) | cull mode, topology, depth/stencil state, depth bias enable | baked into the pipelines (`StaticExtendedDynamicState`, `StaticDepthBiasEnable`) |
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
| `shaderClipDistance` | the GXM far plane (`gl_ClipDistance[0] = w - z`): depth clamp also turns off the hardware's far clip | with depth clamp: `FarPlaneDiscard` (w - z as a varying at location 14, `discard` where it is negative, after the rest of the fragment shader); without depth clamp: nothing, the hardware clips the far plane. Mali Bifrost/Valhall has no clip distances (`maxClipDistances` 0) |
| colour formats that render but do not blend (`COLOR_ATTACHMENT` without `COLOR_ATTACHMENT_BLEND`; Mali: 32-bit float such as R32G32_SFLOAT) | GXM colour surfaces in their own format | the surface keeps its format (`RenderableFormat`); a blended draw into it is drawn unblended with a warning. Until fix/mali-g57 such surfaces fell back to RGBA8: half the bytes per pixel, raw 32-bit outputs skipped, no writeback (Uncharted's title background black) |
| `fillModeNonSolid` | GXM line/point polygon modes | drawn filled (as before) |
| `wideLines` | GXM line width | 1-pixel lines (as before) |
| timestamps | `PERF gpu` GPU time | "no timestamps" (the load/store counters are still printed) |
| pipeline cache | driver's `VkPipelineCache` blob per title | header checked against vendor, device, driver version and UUID (as before); Qualcomm drivers (`PipelineCachePerDriverBuild`) get one file per driver build (hash of driver ID, name, info string and version) because custom drivers replace the system one without changing the IDs |

Also checked but not a quirk: shaders are compiled for the Vulkan 1.0 SPIR-V environment (SPIR-V 1.0), which every
mobile driver accepts. The minimum is Vulkan 1.1 (`VK_KHR_sampler_mirror_clamp_to_edge` when present;
`shaderClipDistance` was required until branch fix/mali-g57): devices without the 1.3 features render with render passes and static pipeline state (Adreno 6xx on
older drivers exposes only Vulkan 1.1). Until branch feat/mobile-renderer the renderer required Vulkan 1.3.

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
| `DynamicRenderingSuspect` | Qualcomm, ARM, Imagination proprietary | selects `RenderPasses` (overridable with `-RenderPasses` where the driver has dynamic rendering) | DuckStation disables dynamic rendering on these blobs |
| `RenderPasses` | no Vulkan 1.3 dynamic rendering, `DynamicRenderingSuspect` drivers, `+RenderPasses` | every rendering is a render pass + framebuffer ([mobile renderer notes](mobile-renderer.md#3-the-vulkan-11-path-vulkanrendererrenderpassescs)) | Vulkan 1.1 devices; DuckStation |
| `PipelineCachePerDriverBuild` | Qualcomm (blob and Turnip) | one driver-cache file per driver build | custom drivers via libadrenotools (Vita3K, yuzu) |
| `TurnipDriver` | Qualcomm on Mesa Turnip | none of the blob quirks | Vita3K: "say it is a turnip driver if the major driver version is less than 100" |
| `CustomDriverCapable` | Qualcomm | the Android UI may offer custom drivers | Vita3K `support_custom_drivers()` |
| `Msaa2xAs4x` | any device with 4 but not 2 framebuffer samples | 2X scenes render with 4 samples | capability |
| `ConservativeMemoryBudget` | tile-based or unified-memory device without `VK_EXT_memory_budget` | eviction at 50% of the heap | capability |
| `FarPlaneDiscard` | no `shaderClipDistance` but `depthClamp` (Mali) | far plane as a fragment `discard` (`ShaderGenerator.FarPlane`, part of the shader pack key; the desktop GLSL is unchanged). `-FarPlaneDiscard`: depth clamp off instead, the hardware clips both planes (keeps early depth tests, but the near plane clips) | capability; measured on Mali-G57 |

Measured on real devices (the startup log's `Vulkan 1.x features missing`, `Vulkan limits`, `Vulkan formats` and
`Vulkan device extensions` lines, `VulkanCapabilityReport`, written before the device is created):

| Device | Driver | Missing (of what the renderer uses) | Quirks selected | Result |
|---|---|---|---|---|
| Mali-G57 MC2 (Redmi Note 14, MediaTek Helio G99), Vulkan 1.3, Android 16 | ArmProprietary 54.1.0 | `shaderClipDistance` (maxClipDistances 0), `textureCompressionBC`, `fillModeNonSolid`, `VK_EXT_memory_budget`; has depth clamp, sample-rate shading, geometry shaders, logic op, wide lines, 16-bit storage, `VK_ARM`/`VK_EXT_rasterization_order_attachment_access`, push descriptors, custom border colour, ASTC LDR (BC goes to ASTC 4x4), ETC2; D24S8; framebuffer samples 1/4/8; per-stage descriptor limits 8388608, 7 sets, 256 B push constants | `TileBased AvoidPushDescriptors AvoidClearAttachments Msaa2xAs4x DynamicRenderingSuspect ConservativeMemoryBudget RenderPasses FarPlaneDiscard` | before branch fix/mali-g57: no device ("lacks shaderClipDistance"). Now: Devious Dungeon, Knightin'+, F1 2011, Persona 4 Golden, Killzone Mercenary (into gameplay) and Uncharted render correctly ([device run](../android/README.md#phone-run-redmi-note-14-mali-g57-30102026)); no Mali driver bug seen. `-TileBased`, `-RenderPasses` (dynamic rendering), `-RasterizationOrderColor`, `-FarPlaneDiscard` and the `desktop` profile all ran F1 2011 the same |

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

Since feat/mobile-renderer desktop GPUs defer their single-sampled renderings the same way (still storing depth), so
uploads and copies that do not read the scene are recorded before the rendering instead of ending it
([mobile renderer notes](mobile-renderer.md)); `VITASHARP_SCENE_BREAKS=strict` restores the old behaviour. The ops of every scene rendering are counted
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

## Real device: Mali-G57 MC2 (30/10/2026)

Redmi Note 14 (24117RN76L), MediaTek MT6789 (Helio G99: 2x Cortex-A76 2.2 GHz + 6x A55 2.0 GHz), Mali-G57 MC2 on the
r54 proprietary driver (`ArmProprietary 54.1.0`), Vulkan 1.3, Android 16. Capabilities and quirks: the table above.

- The far plane: `FarPlaneDiscard` alone on the Killzone dump (frames 4200-4300, desktop GPU,
  `VITASHARP_GPU_QUIRKS=+FarPlaneDiscard`) is 101/101 pixel-identical to the clip-distance path; the updated `mali`
  profile (no BCn, fill modes, memory budget, clip distance; depth clamp) differs only by the BC decode (worst mean
  0.79, as before). The desktop default path stays pixel-identical (101/101 against the base commit).
- The renderer paths the device selects were active: render passes (the blob is `DynamicRenderingSuspect`), tile-based
  load/store, BC to ASTC 4x4 (Killzone: UBC1-4, PVRTC-II), 2X MSAA as 4X (no 2X scene in these games), rasterization-order attachment access enabled (`VK_ARM_...`; whether a
  multisampled framebuffer-fetch scene used it was not checked), no draw skipped for texture units. Turning each workaround off
  (`-TileBased`, `-RenderPasses`, `-RasterizationOrderColor`, `-FarPlaneDiscard`, the `desktop` profile) changed
  nothing visible in F1 2011: none of them is known to be needed on this driver, none hurt.
- Black frames in F1 2011's movies on the phone are not a GPU problem: a GPU dump replayed on the desktop shows the
  same frames, and in each the game issues no draw of its movie picture. Its movie player drops a frame whenever its
  decoder thread is late, which happens when the emulated CPU cannot keep up (the phone runs F1 at about 20 fps); the
  desktop does the same with `--cpu-mhz=120`.

- Misaligned vertex attributes (31/10/2026, God of War Collection): GXM streams may place 16- and 32-bit attributes
  at addresses that are not multiples of their component size (a 19-byte stride with F16 at offsets 3 and 11); Vulkan
  forbids it, NVIDIA reads them anyway, the Mali-G57 returns garbage. Such attributes are fetched as R8 bytes and
  rebuilt in the vertex shader on every GPU (`ShaderGenerator.ByteFetchFlag`, [notes](gow-textures.md)).
- One-sample programmable blending keeps one fetch render pass per draw: sharing it between consecutive draws
  (`VITASHARP_FETCH_PASS_MERGE=1`, with `VK_ARM_rasterization_order_attachment_access`) cut Uncharted's renderings per
  scene from 5.4 to 1.6 but darkened about one frame in seven on the Mali-G57 (cause open).

## What needs a real device

- Everything Adreno/Mali-specific actually running: the profiles only remove features and pretend to be the vendor;
  they cannot reproduce the driver bugs themselves, so whether each quirk is still needed (or sufficient) has to be
  checked on devices, ideally old (512.5xx) and new (512.7xx+) Adreno blobs, Turnip, and older Mali (r3x/r4x;
  a Mali-G57 on r54 ran, see above). Use
  `VITASHARP_GPU_QUIRKS=-Quirk` to turn a workaround off.
- `VK_EXT/ARM_rasterization_order_attachment_access`: the code path (subpass flag, pipeline flag, no barriers or
  per-triangle splits) has never executed; neither desktop GPU here has the extension.
- Real tile-memory savings: the counters show which ops are chosen; bandwidth/power needs the device's profiler
  (Snapdragon Profiler, Arm Performance Studio).
- `DynamicRenderingSuspect` now selects the render-pass path on the proprietary blobs; whether a blob's dynamic
  rendering actually works (`VITASHARP_GPU_QUIRKS=-RenderPasses`) needs the device.
- Vulkan 1.1 devices (many Adreno 6xx on older drivers): the render-pass path and static state are implemented and
  were checked on the desktop with `--gpu-profile=adreno-vk11` (device created without any 1.2/1.3 feature struct);
  a real 1.1 driver has not run it.
- libadrenotools: building it for arm64-v8a, packaging the hook libraries, verifying that Silk.NET resolves all
  commands through the returned handle, the "Driver da GPU" screen, and BCeNabler on real blobs.
- ASTC transcoding of BCn/PVRTC with a disk cache: implemented and checked on the Iris Xe; the speed and quality on a
  phone CPU/GPU are unmeasured ([mobile renderer notes](mobile-renderer.md)).
