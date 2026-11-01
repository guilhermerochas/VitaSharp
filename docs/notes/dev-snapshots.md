# Developer snapshots and the flight recorder (design, 01/11/2026)

**Goal:** save the exact scene once, load it in 1-2 s, and run the next N seconds with real timing as an isolated test
scenario: flicker, fps and audio checks from that point (`bench snapshot=FILE seconds=N`). Unlike a GPU dump, a
snapshot keeps the guest CPU running, so timing-dependent bugs reproduce. Uncharted's dark title frames are one: a
race between game writes and the renderer's dirty-page checks, invisible in any dump replay. Developer builds only
(`VITASHARP_DEVELOPER`). This is not the user-facing save-state feature of the roadmap. It has no compatibility
promise across emulator versions: a snapshot is only valid for the build that wrote it.

## What a snapshot must hold

| Part | Where it lives | How |
|---|---|---|
| CPU registers, VFP/NEON, flags, FPSCR, TPIDR | `CpuState` per `GuestThread` (blittable struct) | raw bytes |
| Exclusive monitor | `CpuState.ExclusiveAddr/Value/Valid/Gen` | raw bytes; clearing it on load is also correct (a spurious STREX failure is allowed by the architecture) |
| Guest memory | `GuestMemory` (4 GiB reservation) + the mapped ranges of `SysMem`/`AddressSpace` | the committed ranges only, zstd/Brotli; 50-300 MB per game |
| Kernel objects | `KernelObjects`, `Sync` (semaphores, event flags, mutexes, cond vars, message pipes, timers, callbacks) and their wait queues | per-type serializers |
| Threads | `GuestThread` (one host thread each) with status, priority, affinity, wait object and timeout | see "safe point" |
| HLE module state | 72 module classes in `Hle/Modules` (GXM contexts and sync objects, display, ctrl, audio ports, NGS systems/voices/racks, AT9/HEVAG decoder state, file handles, AppUtil, savedata, trophies, net stubs) | one serializer per module, or reflection over their fields |
| GPU | renderer caches (textures, render targets, pipelines) | not saved: rebuilt from guest memory on load, as after a LoadExec. Render targets the game reads back before redrawing them would be black for a frame; the dump code's `ExportDumpState` (colour targets) can be reused for those |
| Audio | output ring, port timing, NGS voice positions | ports and NGS as module state; the host output starts empty |
| Files | open guest file handles (path, offset, mode) | reopened by path and seeked on load |
| Clocks | `GuestClock` base, vblank count, timers' next fire | rebased so guest time continues from the saved value |

## Safe point

All guest threads must stop where their whole state is guest state:
- **Running threads** stop at a block boundary (`SuspendForVm` already does this for the VM suspend calls).
- **Threads blocked inside an HLE wait** (`Wait`/`Sync` block the host thread with `Monitor.Wait`) carry host stack
  state. They are saved as "about to issue the call": PC = `SvcPc`, registers as at the call, and the wait is
  repeated on load. Waits restart their timeout, which a developer snapshot can accept.
- **Threads in a callback, or in an HLE call that calls back into the guest** (`CallGuest` nested frames) have nested
  host frames and cannot be saved. The capture waits until none is in that state (retry for up to 1 s, otherwise
  report which thread blocks it).
- The GPU worker is drained (`Finish`) and the audio output paused first.

## Feasibility

- **CPU, memory, threads and kernel objects:** about 2 days. The structures are explicit and few.
- **HLE module state is the blocker.** There are 72 module classes with ad-hoc C# fields, host handles (MediaCodec
  decoders, AAudio streams, file streams) and cross-references. Two ways to do it:
  - A reflection serializer over the module graph, with an allow-list of host-handle types that are rebuilt rather
    than copied. Fast to write, fragile, and only acceptable because the snapshot is tied to one build.
  - Hand-written `ISnapshotState` per module: robust, roughly 3-5 days for the modules Uncharted and F1 use (GXM,
    display, ctrl, audio, NGS, AT9, avplayer/MP4 movie state, fios/io, appmgr), more for full coverage.
- **Movies are the worst case.** Uncharted's title runs a looping movie through MediaCodec. A decoder cannot be
  snapshotted; it can only be reopened and seeked to the saved pts, with a few pictures of drift.
- **Estimate:** 1-2 weeks for "works on the Uncharted title and the F1 race start", including the bench mode and
  checks. That is beyond the timebox of this task, so it was not implemented here.

## Smallest version that gets most of the benefit

Most of the iteration cost is the boot and menu navigation: 40-60 s per run, 90 s or more for F1's race. The
following get most of the benefit without full snapshots:
1. **GPU work:** GPU dump fixtures with the bench (done: `vitasharp-perf bench`, `build/android-bench.*`). This covers
   Mali tile traffic, shader and render-state experiments in seconds.
2. **Timing races:** short in-game probes with the on-device check (`VITASHARP_FLICKER_CHECK=1`, developer APK). Done:
   it found Uncharted's dark frames in about two runs once the replay had ruled the renderer out.
3. **Next step towards snapshots:** CPU, memory, threads and kernel objects, plus the reflection serializer for HLE
   modules, behind a "snapshot unsupported: module X holds host handle Y" report. Run it first on Knightin'+ (no
   movies) to prove the loader, then extend it module by module to Uncharted and F1.

## Flight recorder (built on snapshots)

While the player plays, the recorder keeps:
- A periodic snapshot every 10-20 s (configurable, off by default; 1-2 kept, the second as a page delta against the
  first, with the dirty-page table giving the changed pages for free).
- The input log since that snapshot: buttons, sticks, touch and motion with frame and time stamps
  (`ScriptedInput`'s format).
- A log of nondeterministic guest inputs: clock reads, file read results, thread wake order where cheap.
- The GPU dump of the last N frames (the draw ring and the recorder).
- The guest port and output audio of the window, the logs and frame thumbnails, in the F10 diagnostics bundle format.

One button (the pause menu on Android, a key on desktop) writes the bundle. The bench loads the snapshot, replays the
input log with real timing, and runs the flicker, fps and audio checks.

**Cost:** a 10-20 s snapshot cadence on the phone means copying 50-300 MB of guest memory, about 100-300 ms at the
A76's memcpy rate. It must go through a copy-on-write delta (the dirty pages since the last snapshot), or the
player sees a hitch every 15 s. To measure once the snapshot exists.

**Smallest useful recorder before snapshots:** the existing F10 bundle plus a rolling input log and the last N
frames' GPU dump (`VITASHARP_GPU_DUMP_AT`'s recorder in ring mode), triggered by one button. It reproduces the
GPU-side picture of a report but not CPU timing.
