# perf/kz-mainthread: Killzone Mercenary main thread profile

Goal: find out why Killzone (PCSC00045) ran at 26-28 fps in the heavier stretches of `kzl_walk3`, and whether the
main guest thread `killzone_vita` (host CPU, HLE cost, waits) or the CPU pacing model limits it.

**Conclusion.** Neither the main thread nor the pacing model limits fps.
- **The sub-30 stretch at gameplay start** (`kzl_walk3` frames 4300-5400) was GPU-worker bound. Half of the GPU
  worker's time went into test-only texture inspection (`InspectUploadedTexture`, active whenever a test run dumps
  frames). Fixed: on an otherwise idle machine, `kzl_walk3` on the branch held a flat 30 fps in all 3 runs
  (`sw1`-`sw3`, built on main 0836830).
- **The main thread's host CPU** (26-32 ms/frame) is mostly spin-waiting inside `sceKernelDelayThread(1)` polling
  loops. It costs CPU but does not limit fps.
- **Slow stretches that remain** (5-10 fps for 20-40 s, in some runs only): the game itself is waiting. The main
  thread polls in `DelayThread`, the job threads are idle and the GPU worker is idle. They happen with and without
  the branch, and in more runs since main gained the KZ combat fix. See "Open" below.

## Tools added
- `--profile`:
  - names host waits after the HLE function the thread is in (`host-block:sceKernelDelayThread`,
    `host-block:sceGxmDisplayQueueAddEntry`, ...; `GuestThread.CurrentHle`);
  - logs a `PROFILE slow window` report for every 5 s window below 20 fps: each thread that was not mostly asleep
    in a kernel wait, its guest + charged-HLE cycle rate (M cycles/s; near 444 means the pacing model limits), its
    state mix and its hot PCs.
- `--profile-hle` (opt-in, it slows HLE-heavy games) also times every HLE call per thread. `PROFILE host <thread>`
  gives the thread's host CPU in the window, wall and CPU time inside HLE, the top functions by host CPU (ms/frame,
  calls/frame, us/call) and the functions where the thread waited. CPU comes from `QueryThreadCycleTime` (Windows,
  `NativeThread.CycleTime`).
- `PERF texcache` reports the test inspection cost and queue (inspected, repeated contents skipped, over the frame
  budget, still queued, changed before their turn).
- `VITASHARP_SYSCALL_LATENCY_US=N` overrides `Wait.SyscallLatencyUs` (default 30), for timing bisection.

## Measurements (Windows x64, 8 logical CPUs, shared machine, private pre-warmed shader cache)
Heavy stretch = `kzl_walk3` cut at frame 5400 (`--profile --profile-from=4300`, ~1100 gameplay frames), main 5eb90aa.

| Run (heavy stretch) | fps | frame interval p50 / p90 / p99 ms |
|---|---|---|
| main, inspection on (as the scenario runs) | 22.4 | 33.5 / 68.9 / 101.8 |
| same, `--cpu-mhz=0` (unpaced) | 23.4 | 33.4 / 66.6 / 86.0 |
| same, short delays sleep instead of spin (experiment) | 19.6 | 40.4 / 79.3 / 173.0 |
| main, `--no-inspect` | 30.0 | 33.4 / 33.4 / 33.7 |
| main, `--no-inspect`, unpaced / latency 0 / no spin | 30.0 / 30.0 / 30.0 | p99 33.7 / 33.6 / 33.8 |
| branch (inspection budget), inspection on | 29.9 | 33.4 / 33.5 / 37.9 |

- **What limited the heavy stretch:** the GPU worker. A `dotnet-trace` sample (`dotnet-sampled-thread-time`, 20 s
  at frame 4500, `cpurep` report filtered to the GPU thread) put 53% of the GPU worker in `InspectUploadedTexture`:
  30% in `Inspect`, ~20% in the BC decode done only for the inspection, the rest in `TextureDecoder`. With
  `--no-inspect` the stretch holds 30 fps and the GPU worker is idle 17 ms/frame.
- **Pacing is not the limit.** Unpaced gains 1 fps while GPU-bound and nothing at the vsync cap. Pacing sleeps are
  about 1 ms/frame. In the slow stretches the main thread runs 25 M guest + 195 M charged-HLE cycles/s, far below
  444 M.
- **Main thread host CPU** (`--no-inspect`, heavy stretch): 32.5 ms/frame, of which 26 ms is spinning inside
  `sceKernelDelayThread`:
  - 455 calls/frame at 57 us each, all from one game helper (`DelayThread(1)` at 8100D692) that the fiber job
    system uses to poll for job completion;
  - guest code and the rest of the HLE take ~6 ms;
  - the thread is in a host wait 70% of the samples, running 28%, and never waits for a core;
  - each `JobSched_00x` thread takes 5 ms/frame, 3.4 ms of it in the same spin;
  - all other HLE is cheap (lwmutex 0.6 us/call, `sceKernelGetTLSAddr` 0.2 us, Gxm state setters 0.2-0.4 us,
    `sceGxmDrawPrecomputed` ~4 us on the job threads), with no allocation, lock or lookup hot spot. The native JIT
    has 100% of the static instructions inline (only 625 K NEON callouts per run).
- **Why 57 us per `DelayThread(1)`:** 12,000 charged cycles (27 us at 444 MHz, the "syscall + scheduler" class)
  plus the 30 us `SyscallLatencyUs` floor. The sleep starts at the thread's emulated position, so the charge becomes
  wall time. Unpaced, a call takes 32 us; with the latency at 0, 27.6 us. The main thread's "charged HLE 4.9 M
  cycles/frame vs 1.9 M guest" is almost all these polls.
- **Spinning:** Windows' high-resolution waitable timer wakes 0.5-0.7 ms late at best (measured 500-730 us for any
  request from 10 to 200 us), so a delay shorter than ~0.6 ms can only spin. Sleeping instead cut the main thread
  from 32 to 11 ms CPU/frame and still held 30 fps at the vsync cap. When the GPU worker was the bottleneck, though,
  it cost 13% fps (22.4 to 19.6): the coarse wake-ups delay the job and GPU completion polls. Kept as is.

## Change: test-run texture inspection (VulkanRenderer.TraceTextures.cs, VulkanRenderer.Textures.cs)
Test runs that dump frames inspect every texture upload on the GPU thread: decode to RGBA8 (BC/PVRTC included) and
a noise/seam scan for the TEXTURE SUSPECT warnings. In `kzl_walk3` 70% of those uploads repeated contents already
inspected (41,239 of 55,174 in one run: streaming re-uploads of the same data). Now:
- each distinct content (hash, format, size) is inspected once;
- inline inspection is limited to 1.5 ms per frame. Uploads over that are queued (up to 4,096) and inspected on a
  below-normal background thread (`Texture inspection`). That thread hashes the texel data before and after the
  decode and drops the result if the guest rewrote it. Palette textures are not queued.
- Two earlier versions caused Uncharted hitches:
  - draining the queue at the start of the next frame moved a loading screen's inspections into the first gameplay
    frames;
  - draining it in the GPU worker's idle time let one large decode delay the next frame's commands.

`kzl_walk3`, gameplay frames 4300-10500, inspection on:

| Build | runs | fps | p50 / p90 / p99 ms | frames > 50 ms |
|---|---|---|---|---|
| main 5eb90aa | 2 | 28.5, 28.3 | 33.4 / 33.5 / 72.6; 33.4 / 33.6 / 75.6 | 292; 328 |
| branch on 5eb90aa, idle machine | 1 | 30.0 | 33.4 / 33.4 / 33.9 | 0 |
| main 0836830 | 2 | 29.0, 25.0 | p99 74.9; 128.2 | 170; 637 |
| branch on 0836830 | 2 | 29.5, 28.0 | p99 45.7; 118.5 | 50; 169 |
| branch on 0836830, idle machine | 3 | 30.0, 30.0, 30.0 | p99 33.6, 33.6, 33.8 | 2, 0, 0 |
| main 0c994cb (KZ combat fix) | 2 | 25.4, 22.6 | p99 101.1; 210.7 | 893; 822 |
| branch on 0c994cb | 2 | 26.2, 28.1 | p99 125.5; 78.7 | 359; 229 |

Inspection now costs 0.11-0.22 ms/frame on the GPU thread. The low values in the 0c994cb rows come from the
remaining slow stretches (below), which hit both builds.


## Final regressions (branch 1784a57 + tracking commit, on main d6e072a, vs main 51895a1; idle machine)
Each run used a private pre-warmed shader cache and `--profile`. fps covers the whole run for uc_walk and mania_boot
(boot and loads included) and the profile window for the Killzone scenarios.

| Scenario | main fps (p99 ms, frames > 50 ms) | branch fps (p99 ms, frames > 50 ms) |
|---|---|---|
| Killzone heavy stretch (kzl_walk3 to f5400, from 4300) | 19.7 (185.2, 404) | 30.0 (33.5, 0); an earlier run 29.9 (33.5, 0) |
| kz_skin (from 4150) | 30.0 (35.3, 0) | 30.0 (34.2, 0) |
| uc_walk, 2 runs each | 35.3, 35.3 (33.8, 33.7; 23, 23) | 35.5, 35.3 (33.8, 33.6; 17, 14) |
| mania_boot | 46.0 (22.2, 4) | 44.8 (23.6, 4); earlier pairs 46.6 vs 46.8, 46.4 vs 46.6 |

- Earlier pairs on mains 0836830 and 0c994cb also covered gow_play and dd_boot. Branch vs main: gow_play 59.4 / 57.5 vs 53.8 / 59.4 fps, dd_boot 51.8 / 51.5 vs 46.6 / 51.5 fps. The differences are noise from other emulators running on the machine.
- Auto exposure is unchanged: `kz_skin` frame brightness from f4150 to f4500 matches main to within ±0.006.
- No Killzone run died. METRICS shows black frames only in boot frames 10-300.
- The unit tests pass.

## Tried and not kept: lower `DelayThread` charge
Charging the `sceKernelDelay*` calls 2,000 cycles (kernel entry only; the 30 us floor already models the reschedule)
cut each poll from 57 to 30 us. On `kz_skin` it cut the main thread's charged HLE from 5.9 to 1.7 M cycles/frame.
It brought no fps: every measured stretch is at the 30 fps vsync cap once the test inspection is fixed. It does
move emulated time, which the game measures, so it was not kept. Both `kzl_walk3` runs with it took a different
route (a staircase instead of the usual room) and slowed down there. Later runs without the change took the same
route and slowed down the same way, so the route and the slowdown come from run-to-run variation, not from the
charge. The charge classes (12,000 cycles for any blocking syscall, plus the latency floor for delays and woken
waits) need a hardware reference before they move.

## Open: slow stretches with everything waiting
In some `kzl_walk3` runs (more often on busy machines, and in most runs on main 0c994cb and later) the game drops
to 5-10 fps for 20-40 s in certain areas (staircase, wall breach), then recovers.

`PROFILE slow window` during such a stretch (branch on 0c994cb, f9333-9617, 8 fps):
- `killzone_vita`: 85-88% `host-block:sceKernelDelayThread`, ~11% running, 25 M guest + 195 M HLE cycles/s;
- `JobSched_00x`: 55-70% waiting on their event flag, 25-50% in the delay spin;
- the GPU worker is idle (hitch cause `gpuIdle`), pacing sleeps are negligible, and file I/O is under 10% of the
  hitch time.

So the main thread polls for something that neither the job threads, the GPU nor the CPU budget is producing.
`ErrorOutOfDeviceMemory` (texture page allocation) shows up in some of these runs, but after the stretch, so it is
not the trigger.

Next steps:
1. Find what the `DelayThread(1)` loop at 8100D692's caller waits for during the stretch. Trace the caller with
   `--bt=sceKernelDelayThread` limited to the slow frames, or probe the flag it reads.
2. Check whether it is a GPU-side completion that arrives late: a notification, a visibility query or a deferred
   `SignalCommand` retired only by `PollRetire`.

## Other next steps
- POSIX hosts: `nanosleep` is precise to tens of microseconds, so `PreciseSleep.Until` could sleep most of a short
  delay and spin only the last ~50 us. That would cut the delay-spin CPU on Linux/Android, where battery matters.
- On Windows laptops, the delay spin could move to one shared spinner thread that wakes delayed threads by event:
  about 5-10 us more wake latency per poll, in exchange for spinning on one core instead of four.
- The test harness still writes frame-dump PNGs on the GPU thread (`WriteDump`/`Png.Write`, ~7% of the GPU worker
  in `kzl_walk3`), which could move to a background thread.
