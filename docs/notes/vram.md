# GPU memory: VRAM visibility and out-of-memory handling

Branch fix/vram-oom (2026-10-23). It started from one `ErrorOutOfDeviceMemory` (image page) in a Killzone session
on a GTX 1650 with 4 GB, at about f4540. That draw was dropped and the replay ring could not be allocated after F9.

## What is logged

- **Accounting.** `VkCore` counts every Vulkan allocation by `MemCat`:
  - render targets, MSAA/supersample images, depth/stencil, textures;
  - regions/reinterpret/mip views (RT regions, aliases, RT mip chains, depth/stencil textures, scene-start copies,
    fetch copies);
  - pipeline buffers (the CDRAM mirror), upload rings, readback/writeback, debug rings (replay, NaN scan), GPU dump.

  `ImageMemoryPool` counts what is in use in each of its pages (sub-allocations), and buffers are counted when they
  are allocated. A device-local or host split is kept for each category.
- **Budget.** `VK_EXT_memory_budget` is enabled when the device has it. Every heap's budget and this process's usage
  appear in each summary. The budget drops when other processes use the GPU.
- **Where it shows up:**
  - a `[vram]` line at startup;
  - `[vram] pN` every 600 presents in debug mode;
  - `PERF periodic fN VRAM ...` from the watchdog, every 60 s in debug mode;
  - `PERF vram` at the end of a run, with eviction and failure counters;
  - the `VRAM:` line of renderer.txt / info.txt in captures;
  - every out-of-memory message;
  - in normal play, a line each time device-local usage crosses 75% or 90% of the budget.

## Out of memory

`VkCore.AllocateMemory` (buffers) and `ImageMemoryPool.Allocate` (image pages) react to
`ErrorOutOfDeviceMemory` and `ErrorOutOfHostMemory` in steps. `VulkanRenderer.Reclaim` runs at each level:

1. **Level 0.**
   - Wait for every submitted frame, then run their deferred destroys and free their upload overflow buffers.
   - Evict least-recently-used caches not used for 2 GPU frames, up to the request plus 128 MB. These are textures,
     RT views, and render targets idle for 120 frames whose content is in guest memory.
   - Destroy them at once, return empty pool pages to the driver, then retry: first the free space, then a full
     128 MB page, then a 16 MB page.
2. **Level 1.** The same, but everything the recording frame does not use goes. That includes render targets and
   depth targets idle for 600 frames even if their content would be lost.
3. **Still failing.** `VkOutOfMemoryException` is thrown, carrying the budget summary:
   - in `BuildTextureSet`, the texture binds the dummy texture and the draw still runs;
   - in `Execute`, the command is skipped. A scene without a target drops its draws.

   Warnings are rate-limited: the first 3 are logged in full, then one every 10 s with a count of the suppressed
   ones.

Every evictable object records the GPU frame it was last used in (`LastUsedFrame` / `LastTouchedFrame`; `FrameCount`
is the frame being recorded). The current scene's target and depth are never evicted. Destroys outside an emergency
go through `DestroyAfterGpu`, which uses the recording frame's deferred list, or between frames waits for the newest
submitted frame. The old code put destroys in `F.Deferred` even between frames, so they could run while a newer
frame still used the object.

**Proactive eviction:**

- **Expiry.** Every 256 presents, caches unused for 6000 GPU frames are evicted.
- **Overlap.** When a texture is created, cached textures over the same memory that were unused for 8 frames are
  dropped.
- **Pressure.** Every 4 presents, if usage is at or above 90% of the budget or of the test cap, LRU caches are
  evicted until the pool has a free reserve of 8% of the budget (at least 48 MB), and empty pages are returned.

**Test switches:**

- `VITASHARP_VRAM_BUDGET_MB=N` caps the renderer's device-local bytes. An allocation past the cap fails like a real
  OOM.
- `VITASHARP_VRAM_EVICT=0` turns off expiry, overlap and pressure eviction. Use it to measure the old behaviour.

## Leaks found

- **The texture cache never freed anything.** Killzone streams textures into reused memory with other
  sizes/formats, so new cache keys appeared all the time: kzl_walk3 created about 44,000 textures (3.2 GB) in 10,500
  frames. Overlap and expiry eviction now hold the cache at 80-120 MB.
- **RT regions and RT mip textures were keyed by the `RenderTarget` object.** When a surface was replaced (same
  address, new size or format), its regions and mip images stayed forever, as did the replaced target's `RtAlias`
  source reference. They are now released with the target (`ForgetRenderTargetViews`).
- **Depth/stencil textures were keyed by `DepthTarget`.** They are released with the depth target. Before, depth
  targets were never destroyed at all.
- **GPU dump export.** A failure between creating a buffer and destroying it leaked the buffer. It is now released in
  a finally block.
- **Replay ring.** One failed allocation turned it off for the whole session. It is now tried again the next time
  debug mode is turned on.
- **Draw ring, NaN scan buffers, replay ring under F9 toggling.** No growth: the replay ring is freed after its
  frame, the draw ring is managed memory released at the next present, and the NaN buffers (2 KB) are reused per
  frame slot.
- **Async shader/pipeline prefetch.** Allocates no device memory.
