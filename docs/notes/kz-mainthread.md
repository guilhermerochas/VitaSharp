# perf/kz-mainthread: Killzone Mercenary main thread profile

Goal: find out why Killzone (PCSC00045) ran at 26-28 fps in the heavier stretches of `kzl_walk3`, and whether the
main guest thread `killzone_vita` (host CPU, HLE cost, waits) or the CPU pacing model limits it.

Short answer: neither. The sub-30 stretch of `kzl_walk3` (gameplay frames 4300-5400) was limited by the GPU worker
thread, and half of the GPU worker's time went into test-only texture inspection (`InspectUploadedTexture`, active
whenever a test run dumps frames). With that made cheap, `kzl_walk3` runs at a flat 30 fps. Most of the main thread's
host CPU is spin-waiting inside `sceKernelDelayThread(1)` polling loops. It costs CPU but does not limit fps.

## Tools added
- `--profile` now also times every HLE call (only once the profile window starts): `PROFILE host <thread>` gives the
  thread's host CPU in the window, the wall time and CPU time spent inside HLE, and the calls per frame. Below it come
  the top functions by host CPU (`cpu`/`wall` ms/frame, calls/frame, us/call) and the functions where the thread
  mostly waited (`wait` = wall - cpu). CPU comes from `QueryThreadCycleTime` (Windows; `NativeThread.CycleTime`),
  converted with a TSC calibration. Cost when on: two timestamps, two cycle-counter reads and an uncontended lock per
  call.
- `PERF texcache` reports the test inspection cost: `test inspection X ms/frame (N inspected, M repeated contents
  skipped, K over the frame budget)`.
- `VITASHARP_SYSCALL_LATENCY_US=N` overrides `Wait.SyscallLatencyUs` (default 30), to bisect timing.

## Measurements (Windows x64, 8 logical CPUs, shared machine, private pre-warmed shader cache)
Heavy stretch = `kzl_walk3` cut at frame 5400 (`--profile --profile-from=4300`, ~1100 gameplay frames).

| Run (heavy stretch) | fps | frame interval p50 / p90 / p99 ms |
|---|---|---|
| main 5eb90aa, inspection on (as the scenario runs) | 22.4 | 33.5 / 68.9 / 101.8 |
| same, `--cpu-mhz=0` (unpaced) | 23.4 | 33.4 / 66.6 / 86.0 |
| same, short delays sleep instead of spin (experiment) | 19.6 | 40.4 / 79.3 / 173.0 |
| main, `--no-inspect` | 30.0 | 33.4 / 33.4 / 33.7 |
| main, `--no-inspect`, unpaced / latency 0 / no spin | 30.0 / 30.0 / 30.0 | p99 33.7 / 33.6 / 33.8 |
| branch (inspection budget), inspection on | 29.9 | 33.4 / 33.5 / 37.9 |

- **What limits fps in the heavy stretch:** the GPU worker. A `dotnet-trace` sample (`dotnet-sampled-thread-time`,
  20 s at frame 4500, report from the `cpurep` tool filtered to the GPU thread) put 53% of the GPU worker in
  `InspectUploadedTexture`: 30% in `Inspect`, ~20% in the BC decode done only for the inspection, and the rest in
  `TextureDecoder`. With `--no-inspect` the same stretch holds 30 fps with the GPU worker idle 17 ms/frame. Pacing
  is not the limit: unpaced gains 1 fps while GPU-bound and nothing at the vsync cap, and pacing sleeps are about
  1 ms/frame.
- **Main thread host CPU** (`--no-inspect`, heavy stretch): 32.5 ms/frame. Of that, 26 ms is spinning inside
  `sceKernelDelayThread` (455 calls/frame at 57 us each, all from one game helper, `DelayThread(1)` at 8100D692,
  which the fiber job system uses to poll for job completion). Guest code and the remaining HLE take ~6 ms. The
  thread is in a host wait 70% of the samples, running 28%, and never waits for a core. The job threads (`JobSched_00x`)
  take 5 ms/frame each, 3.4 ms of it spinning in the same helper. All other HLE is cheap: lwmutex 0.6 us/call,
  `sceKernelGetTLSAddr` 0.2 us, Gxm state setters 0.2-0.4 us, `sceGxmDrawPrecomputed` ~4 us (on the job threads).
  No HLE function stands out for allocations, locks or lookups.
- **Why 57 us per `DelayThread(1)`:** 12,000 charged cycles (27 us at 444 MHz, the "syscall + scheduler" class) plus
  the 30 us `SyscallLatencyUs` floor. The sleep starts at the thread's emulated position, so the charge turns into
  wall time. Unpaced it is 32 us; with the latency set to 0, 27.6 us. This is also where the "charged HLE 4.9 M vs
  guest 1.9 M cycles/frame" comes from: on the main thread that figure is almost all `DelayThread` polls.
- **Spinning:** the spin is there for latency. Windows' high-resolution waitable timer wakes after 0.5-0.7 ms at
  best (measured 500-730 us for any request from 10 to 200 us), so a delay shorter than ~0.6 ms can only spin.
  Sleeping instead cut the main thread from 32 to 11 ms CPU/frame and still held 30 fps at the vsync cap. When the
  GPU worker was the bottleneck, though, it cost 13% fps (22.4 to 19.6), because the coarse wake-ups delay the job
  and GPU completion polls. Kept as is.
- **`kz_skin`** (`--no-inspect`, gameplay frames 4150-4500): 29.9 fps before and after (p99 33.7 ms). Its main
  thread has the same profile: 74% host wait, ~28 ms/frame host CPU in the window, mostly the delay spin.

## Change: test-run texture inspection (VulkanRenderer.TraceTextures.cs)
Test runs that dump frames inspect every texture upload on the GPU thread: decode to RGBA8 (BC/PVRTC included) and
a noise/seam scan for the TEXTURE SUSPECT warnings. In `kzl_walk3` 70% of those uploads repeated contents that had
already been inspected: 41,239 of 55,174 in one run, from streaming re-uploads of the same data. Now:
- each distinct content (hash, format, size) is inspected once;
- at most 1.5 ms of inspection per frame; uploads over the budget queue (up to 4,096) and are inspected in a later
  frame if their memory still hashes to the uploaded data. Palette textures are not queued.

Results (`kzl_walk3`, gameplay frames 4300-10500, inspection on):

| Build | fps | p50 / p90 / p99 / max ms | frames > 50 ms |
|---|---|---|---|
| main 5eb90aa (2 runs) | 28.5, 28.3 | 33.4 / 33.5 / 72.6 / 110; 33.4 / 33.6 / 75.6 / 254 | 292; 328 |
| branch (2 runs) | 30.0, see final table | 33.4 / 33.4 / 33.9 / 40.6 | 0 |

Inspection now costs 0.16-0.18 ms/frame on the GPU thread. TEXTURE SUSPECT detection still runs on every distinct
upload that fits the budget or survives in memory until a later frame.

## Tried and reverted: lower `DelayThread` charge
Charging the `sceKernelDelay*` calls 2,000 cycles (kernel entry only, because the 30 us floor already models the
reschedule) cut each poll from 57 to 30 us and the main thread's charged HLE from 5.9 to 1.7 M cycles/frame
(`kz_skin`). It brought no fps, because every measured stretch is already at the 30 fps vsync cap once the test
inspection is fixed. It does move emulated time, which the game measures, so it was not kept. Both `kzl_walk3` runs with
it took a different route: the player ended on a staircase instead of the usual room, and that area ran at ~6 fps
for ~600 frames (one run also logged `ErrorOutOfDeviceMemory`). A later run of the build without the change (only the
inspection fix) took the same staircase route and slowed down the same way, so the route changes from run to run.
It is not caused by the charge. The slow staircase stretch is analysed below.
The charge classes (12,000 cycles for any blocking syscall, plus the latency floor for delays and woken waits) need
a hardware reference before they move.

## Next steps
- POSIX hosts: `nanosleep` is precise to tens of microseconds, so `PreciseSleep.Until` could sleep most of a short
  delay there and spin only the last ~50 us. That would cut the delay-spin CPU on Linux/Android, where battery matters.
- If the delay-spin CPU matters on Windows (laptops), one option is a shared spinner thread that wakes delayed
  threads by event. That trades about 5-10 us of wake latency per poll for spinning on one core instead of four.
