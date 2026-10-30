# Mobile renderer: fewer scene breaks, ASTC transcoding, the Vulkan 1.1 path (branch feat/mobile-renderer)

Three renderer changes for the Android port, all developed and checked on the PC (GTX 1650, Iris Xe) without a phone
or the Android emulator. Background: [mobile GPU notes](mobile-gpu.md) (quirk layer, `--gpu-profile`, tile-based
load/store counters).

## 1. Fewer scene breaks (`VulkanRenderer.SceneBreaks.cs`)

A Vita scene is one pass over the tiles. The renderer ended its Vulkan rendering for every texture upload, render
target region copy, depth-texture copy or barrier recorded in the middle of a scene and began a new one afterwards; on
a tile-based GPU each break stores the tiles and loads them again.

- **Deferred renderings everywhere.** Multisampled renderings and, on tile-based GPUs, single-sampled ones were
  already recorded into secondary command buffers and begun in the primary only when they end. Desktop GPUs now do
  the same (depth/stencil still stored, as before); `VITASHARP_SCENE_BREAKS=strict` restores the previous behaviour
  (direct renderings on desktop, every upload/copy ends the rendering) in the same build, which is how the "before"
  numbers below were taken.
- **Hoisting.** While a deferred rendering is open the primary has not begun it, so work that must run outside a
  rendering can be recorded into the primary directly (`BeginOutside` ... dispose; `Cmd` returns the primary while
  `_hoisting`): it lands before the rendering's begin and the rendering goes on. This is exact when the work reads
  only guest memory or another surface (not the scene's colour surface, samples, depth/stencil or tile word, which
  keep the strict order) and writes no image that a draw already recorded in the open rendering samples. Every view
  a draw binds is stamped with the serial of the rendering it lands in (`NoteSampled`; the next serial when no
  rendering is open, so a draw whose rendering ends while its textures are being resolved is covered too); a write
  into a stamped view ends the rendering as before. Everything already in the primary stays before the hoisted work,
  so no other order changes. Hoisted: texture uploads (`UploadTexture`), render target region copies of other
  surfaces (`RenderTargetRegion`, and its creation clear) and of parts of the scene's own surface that no draw of the
  open rendering touched (the region clips of its draws are recorded, `NoteDrawn`; plain single-sampled scenes only:
  Killzone's post-processing passes chain through regions of one surface), barriers of other surfaces read as
  textures (`SyncRenderTargetForRead`), depth textures copied from another scene's depth (`DepthTexture`), mip chains
  whose level 0 is another surface (`RenderTargetMipmapped`).
- **Merging.** A single-sampled rendering that ends mid-scene is not begun in the primary right away: if the next
  rendering of the scene uses the same attachments and nothing was recorded into the primary in between (every
  primary access goes through `Cmd`, which flushes them first), both secondaries are executed inside one
  rendering/render pass (`FlushChain`). Hoisting treats such a chain as one open rendering.
- **Strict order kept**: framebuffer-fetch copies of the scene's own surface, scene-start captures, mid-scene
  flushes, mapped/CPU reads, tile-word activation, integer-view switches, eviction, the forced-store 4X depth unpack,
  stencil textures and render target reinterpretation (CDRAM mirror) still end the rendering.
- **Counters** (`PERF gpu`): renderings and scenes (renderings per scene), hoisted operations by caller, the remaining
  mid-scene breaks by caller, besides the existing load/store counters.

### Measurements

GPU dumps recorded with the base build (main 653299b) under `--gpu-profile=adreno` (61 frames each: kz_skin
f4200-4260 gameplay, lbp_boot f2200-2260 pod, tearaway_tutorial f9400-9460, shbom_boot f2600-2660 intro,
skullgirls_boot f1300-1360 fight), replayed with `replay-gpu --shader-cache=<scratch>` by the base build and by this
branch, on the desktop path and with `--gpu-profile=adreno` (tile-based ops). "Renderings" counts the Vulkan
renderings / render passes that run a scene's draws (not the empty MSAA resolve renderings).

Renderings per scene (the `VITASHARP_SCENE_BREAKS=strict` replay of this branch gives exactly the base build's
load/store counters, so it is the "before"):

| dump (61 frames) | scenes | before | after (desktop) | after (adreno) | hoisted | remaining breaks (adreno) |
|---|---|---|---|---|---|---|
| Killzone | 381 | 2574 (6.76) | 1601 (4.20) | 1601 (4.20) | 1839 | RenderTargetRegion 945 (region of the scene's own surface where it already drew), BuildFramebufferFetchSet 210, MirrorDepthTarget 62 |
| LittleBigPlanet | 305 | 1186 (3.89) | 976 (3.20) | 915 (3.00) | 301 | RenderTargetRegion 244, BuildFramebufferFetchSet 244, UnpackedDepthTexture 61, MirrorRenderTarget 60 (desktop: +61 ActivateTileWord1) |
| Tearaway | 122 | 252 (2.07) | 179 (1.47) | 179 (1.47) | 77 | SubmitFrameCore 57 (mid-scene submits) |
| Silent Hill | 305 | 319 (1.05) | 306 (1.00) | 306 (1.00) | 16 | Present 1 |
| Skullgirls | 61 | 131 (2.15) | 61 (1.00) | 61 (1.00) | 76 | none |

Killzone's MSAA scenes went from 713 render passes for 127 scenes to one each (the uploads that split them are
hoisted). Load/store ops with the adreno profile (tile-based), before → after:

| dump | colour loads | colour stores | depth loads | depth stores |
|---|---|---|---|---|
| Killzone | 2447 → 1474 | 2447 → 1474 | 2256 → 1283 | 2382 → 1409 |
| LittleBigPlanet | 1065 → 793 | 1065 → 793 | 882 → 671 | 882 → 671 |
| Tearaway | 130 → 57 | 130 → 57 | 130 → 57 | 252 → 179 |
| Silent Hill | 319 → 306 | 319 → 306 | 319 → 306 | 319 → 306 |
| Skullgirls | 131 → 61 | 131 → 61 | 70 → 0 (61 clears) | 70 → 0 (61 discards) |

No rendering was merged with the previous one in these dumps (`0 more merged`): every break that remains records
work into the primary (a copy of what the scene drew), so the chains are always flushed; the merge only catches
breaks that turn out to record nothing.

Pixels (`--compare` against the base build's replay of the same dump, same options): Killzone, Silent Hill and
Skullgirls 61/61 frames identical on the desktop path, with `--gpu-profile=adreno`, with `+RenderPasses` and with
`--gpu-profile=adreno-vk11`. LittleBigPlanet and Tearaway are not deterministic even with the base build (two base
replays of the same dump: LBP desktop 0/61 identical, 98.2% identical pixels, mean difference 0.041; adreno 23/61;
Tearaway 3-4/61, mean 0.0006 — the LBP hologram noise and Tearaway's paper particles); this branch is inside that
noise in every mode (LBP desktop mean 0.043 / 98.2%, adreno 34/61, adreno-vk11 36/61; Tearaway 2-6/61, mean
0.0007).

Whole scenarios (this branch, `--gpu-profile=adreno`, `VITASHARP_SCENE_BREAKS=strict` for "before"; game runs are
not deterministic, so scene counts differ a little; GPU time and fps were measured on a shared PC and are noisy):

| scenario | renderings per scene | colour loads / stores | depth loads / stores | GPU ms per frame | fps |
|---|---|---|---|---|---|
| kz_skin (4500 f) | 5.65 → 4.08 | 99407 / 99412 → 70973 / 70975 | 92197 / 94903 → 63787 / 66476 | 5.77 → 4.56 | 27.1 → 23.9 |
| lbp_boot (4000 f) | 3.67 → 3.12 | 55025 / 55025 → 42402 / 42402 | 45121 / 45121 → 35540 / 35540 | 8.57 → 8.26 | 24.9 → 28.2 |
| tearaway_tutorial (10250 f) | 1.82 → 1.40 | 14672 / 14678 → 7077 / 7080 | 14672 / 32564 → 7077 / 24967 | 10.49 → 10.57 | 28.8 → 29.6 |

Killzone's MSAA renderings: 16552 for 2705 scenes → 2733 for 2683 (6.1 → 1.02 per MSAA scene); hoisted in kz_skin:
46330 operations (RenderTargetRegion 19384, UploadTexture 13849, texture barriers 7745, RT mip chains 2676,
depth textures 2675). What still breaks Killzone's scenes: programmable-blending copies of the scene's own
surface (33808) and regions of it the scene has drawn into (19352).

Tearaway's remaining breaks are mid-scene submissions (`SubmitFrameCore`: the frame is submitted while a scene is
open, which always ends the rendering); its depth-texture copies (7638) are now hoisted.

**On the PC** (kz_skin, desktop path on the GTX 1650, strict → default): 5.71 → 4.08 renderings per scene, MSAA
renderings 17281 → 2750, GPU time 6.24 → 5.08 ms per presented frame (-19%); fps 20.9 → 28.6, but other agents
used the machine during these runs, so the fps numbers are only indicative.

## 2. ASTC transcoding with a disk cache (`Core/Gpu/Astc.cs`, `AstcTranscoder.cs`, `Core/Gpu/TranscodeDiskCache.cs`)

Before: without `textureCompressionBC` (`--gpu-profile=adreno|mali`, most phones) BC levels were decoded to RGBA8 on
the CPU (64 MB in-memory cache) and PVRTC always was. RGBA8 is 4x the memory of BC2/BC3 and 8x BC1/BC4.

Now, when the device has `textureCompressionASTC_LDR` (enabled at device creation only then) and samples the
ASTC 4x4 UNORM/sRGB formats:

- **What is transcoded** (`AstcTranscoder.ChannelsFor`): UBC1-UBC3 and PVRTC (2/4 bpp, I and II) as colour (+alpha),
  UBC4 as luminance blocks (sampled through an (R, 0, 0, 1) view swizzle composed with the texture's own swizzle),
  UBC5 as luminance+alpha blocks holding R and G ((R, A, 0, 1)). SBC4/SBC5 (signed: ASTC LDR is unsigned) and sRGB
  BC4/BC5 (ASTC applies sRGB to L only) stay on the RGBA8 path, as does everything when the device has no ASTC or
  `VITASHARP_ASTC=0`. Desktop GPUs with BC keep their PVRTC on RGBA8 (unchanged).
- **Encoder** (`Core/Gpu/Astc.cs`, pure C#, no native library): the source is decoded to RGBA8 (the existing BC and
  PVRTC decoders) and every 4x4 block is encoded as one partition with a 4x4 weight grid, trying a few fixed
  configurations in order and stopping at the first whose error is below ~46 dB: uniform blocks as void-extent
  blocks (exact); opaque colour as RGB direct with 8 weight levels (8-bit endpoints), 16 levels (192-level
  endpoints) or 4 levels; colour with alpha as RGBA direct with 8 levels (192-level endpoints), two planes with alpha
  on the second (4 levels each, 48-level endpoints: BC3's independent alpha indices) or 4 levels (8-bit endpoints);
  BC4 as luminance with 16 levels; BC5 as luminance+alpha with two planes (4 levels), one plane (8 levels) or two
  planes with 8 levels. Endpoints: principal axis of the block (min/max projection), then one least-squares
  refinement for the chosen weights; quantized with the bounded integer sequence encoding the decoder derives from
  the bits left (binary, trit and quint ranges), RGB endpoints ordered so the decoder never applies blue
  contraction. Large levels are encoded by up to 4 threads (rows of blocks). A direct BC-to-ASTC block mapping was
  considered: BC1 four-colour blocks map onto RGB with 4 weight levels almost exactly, but BC1's punch-through and
  BC3's alpha indices do not, and going through RGBA8 with the error check gives the same result for the BC1 case.
  6x6 blocks (0.89 bits per texel instead of 2, for low-detail data) were not done: nothing tells the texture cache
  which data is low-detail (the BC source has the same 4x4 detail everywhere), and a wrong guess shows as blur.
- **Software decoder** (`AstcDecoder`): decodes what the encoder writes (void extent; single partition, 4x4 grid,
  one or two planes, L/LA/RGB/RGBA direct endpoints) for the unit tests' PSNR checks and the texture inspection of
  test runs.
- **Caches**: in memory (content-addressed, 32 MB LRU) and on disk per title (`TranscodeDiskCache`:
  `<shader cache>/titles/<TITLEID>/astc/<key>.astc`, key = xxHash3 of the source data + format + size + encoder
  version; 24-byte header checked on read, torn files deleted; least recently used entries (file write time,
  refreshed on hits) deleted down to 90% once the folder passes its limit, 512 MB by default,
  `VITASHARP_ASTC_CACHE_MB`). A texture is therefore encoded once per title, not once per run.
- **Reports**: the `PERF mobile` line and info.txt get the ASTC summary (levels encoded, M texels, encode time and
  rate, memory and disk hits, disk cache size); `VITASHARP_GPU_DEVICE=<index or name part>` picks a physical device
  (the Iris Xe on this laptop) for testing.

### Measurements

**On a real ASTC GPU.** The Iris Xe of this laptop (Vulkan 1.3, BC and ASTC LDR) was selected with
`VITASHARP_GPU_DEVICE=Intel`, and BC removed with `VITASHARP_GPU_QUIRKS=-TextureCompressionBC` (the transcoder is
then on; `VITASHARP_ASTC=0` gives the RGBA8 fallback). The same dumps, compared with the Iris Xe sampling BC natively:

| dump | path | frames identical | mean difference (avg / worst frame) | pixels off by > 8 | worst channel | textures in VRAM |
|---|---|---|---|---|---|---|
| Killzone | native BC | - | - | - | - | 100 MB (570) |
| | RGBA8 (CPU decode) | 0/61 | 0.146 / 0.293 | 0.006% | 19 | 506 MB |
| | ASTC 4x4 | 0/61 | 0.715 / 1.122 | 0.57% | 49 | 142 MB |
| Skullgirls | native BC | - | - | - | - | 64 MB |
| | RGBA8 | 2/61 | worst 0.013 | | | 73 MB |
| | ASTC 4x4 | 2/61 | worst 0.623 | | | 64 MB |
| LittleBigPlanet | native BC | - | - | - | - | 8 MB |
| | RGBA8 | 0/61 | worst 0.026 | | | 40 MB |
| | ASTC 4x4 | 0/61 | worst 0.623 | | | 13 MB |

The ASTC frames look like the BC ones (no block artefacts, sprites and HUD intact); the difference is low-level
banding in smooth gradients (1-3 levels, from the weight quantization) and isolated texels on lit surfaces (Killzone's
worst 49). The RGBA8 numbers are the existing CPU decode's rounding against Intel's BC decoder.

**Speed.** Unit test (`AstcTests.EncoderSpeed`, one thread, 256x256, i5-11300H): BC1-like blocks
6.7 M texels/s, smooth RGB 2.4, RGBA with independent alpha 1.5, BC4 16, BC5 2.9 M texels/s. In the replays (2 encoder
threads: the process is pinned to 4 logical CPUs): Killzone 1655 levels, 67.5 M texels in 5.8 s (11.6 M texels/s);
LBP 412 levels, 8.5 M texels in 0.65 s (13.1 M texels/s); Skullgirls 7 levels, 3.2 M texels in 0.38 s. The cold
first run pays this on the GPU worker (a 1024x1024 level is about 0.1 s); the second replay of the Killzone dump
encoded nothing (3217 levels from the disk cache, 1931 from memory, 64 MB on disk) and its frames match the first
one.

**Quality** (`AstcTests.RoundTripQuality`, software decoder, PSNR): BC1-like blocks 49.4 dB, smooth gradient 46.2,
textured RGB 35.0, textured RGBA 35.2, BC3-like blocks with independent alpha 36.7, BC4-like 53.6, BC5-like 41.0 dB;
uniform blocks are exact (void extent).

## 3. The Vulkan 1.1 path (`VulkanRenderer.RenderPasses.cs`)

Older Adreno 6xx drivers expose only Vulkan 1.1: no dynamic rendering, no synchronization2, no extended dynamic
state. The minimum is now Vulkan 1.1 plus `shaderClipDistance` (`VK_KHR_sampler_mirror_clamp_to_edge` is used when
present, as on 1.2+ devices the core feature).

- **Selection** (`GpuQuirk.RenderPasses`, `GpuQuirkTable.Select`): when the device lacks `GpuFeature.DynamicRendering`
  (Vulkan 1.3 `dynamicRendering` + `synchronization2`; a 1.2 device with `VK_KHR_dynamic_rendering` is not wired up,
  the extension's commands have KHR names), on `DynamicRenderingSuspect` drivers (Qualcomm/ARM/Imagination
  proprietary, as DuckStation does), or forced with `VITASHARP_GPU_QUIRKS=+RenderPasses`. `-RenderPasses` turns it off
  only where dynamic rendering exists. Without `GpuFeature.ExtendedDynamicState` (Vulkan 1.3) the static pipeline
  state quirks (`StaticExtendedDynamicState`, `StaticDepthBiasEnable`) are forced too.
- **Device creation** (`VkCore`): physical devices of Vulkan 1.1 and later are accepted (1.3 ones preferred within a
  device type); the 1.2/1.3 feature structs are queried and chained only on devices of that version, `dynamicRendering`
  is enabled only when it is used. The new profile `--gpu-profile=adreno-vk11` (`GpuProfile.AdrenoVulkan11`) is the
  Adreno profile without dynamic rendering and extended dynamic state, and creates the device with no 1.2/1.3 feature
  struct at all, which is how a 1.1 driver would see the renderer.
- **Renderings**: single-sampled scene renderings are always deferred (as in item 1); their secondaries inherit a
  compatible render pass (`SingleCompatPass`: colour format, tile word, depth/stencil format) and the pass with the
  real load/store ops is begun with a framebuffer of the rendering's views when the rendering ends. Attachments:
  colour 0 (GENERAL, LOAD/STORE), tile word 1 when the scene has one, depth/stencil last with the same ops as the
  dynamic-rendering path (so tile-based devices keep the per-scene DONT_CARE stores). Framebuffers are created per
  rendering and destroyed when the frame retires (a few dozen per frame; no cache that would have to be invalidated
  when render targets and their views die). Multisampled scenes already used render passes; the empty resolve
  rendering of `ResolveMsaa` becomes an MSAA render pass with only a resolve attachment, and the forced-store 4X depth
  unpack a depth-only pass. Pipelines of single-sampled scenes are created against the compatible pass instead of
  `VkPipelineRenderingCreateInfo`.

### Measurements

All on the GTX 1650 (no Vulkan validation layers are installed here), against the base build's replays of the five
dumps: `VITASHARP_GPU_QUIRKS=+RenderPasses` (desktop, render passes only) against the desktop base,
`--gpu-profile=adreno` (render passes through `DynamicRenderingSuspect`, tile-based ops) and
`--gpu-profile=adreno-vk11` (device created without 1.2/1.3 features, static state, render passes) against the adreno
base: Killzone, Silent Hill and Skullgirls 61/61 frames identical in all three; LBP and Tearaway within their
base-vs-base noise (see item 1). The replay logs show `render passes (no dynamic rendering): 7 single-sampled
passes, 1474 framebuffers` for the Killzone dump. Not tested: a real Vulkan 1.1 driver (no such device here), the
raw-integer MSAA resolve through a render pass (no dump has a raw MSAA scene).
