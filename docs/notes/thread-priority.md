# perf/thread-prio: host thread priorities

Goal (roadmap "Prioridade das threads da CPU emulada e da GPU (depois de medir)"): find out whether host thread
priorities, MMCSS, timer resolution or power-throttling settings improve fps, p99 or hitches, and keep only what
helps.

**Result.** One change is kept: the guest threads (the emulated cores) and the GPU worker run at
`THREAD_PRIORITY_ABOVE_NORMAL` (`Core/Util/HostThreads.cs`). On an idle machine it makes no difference, because
every scenario is already at its vsync cap. When other programs keep every logical CPU busy, it is the difference
between playable and not: Sonic Mania went from 19-20 fps with 334-445 frames over 50 ms to 31.6 fps with 19.
Nothing else beat the noise.

Settings: `--host-prio=on|off` (default on), `VITASHARP_HOST_PRIO=0` turns it off (the environment variable wins).

## Machine and method
- Intel i5-11300H (Tiger Lake H): 4 cores / 8 threads, **not hybrid**. `GetSystemCpuSetInformation` reports
  efficiency class 0 for all 8 logical processors. GTX 1650 + Iris Xe. On AC power, "Balanced" power plan.
- Headless test scenarios with `--profile` and a private pre-warmed shader cache (`--shader-cache=`). Mania, Uncharted
  and WRC 4 ran from a scratch data root whose game folders are junctions to `%APPDATA%\VitaSharp\fs\ux0\app` (saves
  copied; WRC 4 gets a fresh root every run, as its scenario requires). Killzone and Devious Dungeon ran from
  `.vitadata`.
- fps = `PROFILE frames` (whole run for mania_boot, dd_boot and uc_walk, boot and loads included; from frame 4150
  for kz_skin, from 4300 to 5400 for kzl_walk3 (the heavy stretch), from 3000 (the race) for wrc4_boot). p99 =
  frame interval p99. Hitches = frames over 50 ms.
- Shared machine: other agents ran 0-3 other emulators during most runs (the "others" count was logged before
  each run). That noise is large: the same build gave uc_walk 29-171 hitches and wrc4_boot 0-73.
- "Contention" = a separate process with N normal-priority busy-loop threads (4 or 8) running during the scenario.
  It stands in for whatever else the user runs: a browser, a compile, another game.
- All variants were one build switched by environment variables (removed afterwards), so "off" is the same binary.

## Variants
| Variant | What |
|---|---|
| off | as main: every thread normal, JIT workers below normal (unchanged), shader workers normal |
| gpu_above | GPU worker `ABOVE_NORMAL` |
| roles | GPU worker `ABOVE_NORMAL`, shader workers `BELOW_NORMAL` |
| **guest_above** (kept) | GPU worker and every guest thread `ABOVE_NORMAL` |
| audio_hi | guest threads that call `sceAudioOutOutput` `HIGHEST` |
| mmcss | GPU worker in MMCSS "Games", audio-output guest threads in MMCSS "Pro Audio" |
| power | `SetProcessInformation(ProcessPowerThrottling)`: execution-speed throttling (EcoQoS) and timer-resolution throttling off |

## Results: idle machine (2 runs each; fps / p99 ms / frames > 50 ms)
| Scenario | base (main) | off | gpu_above | roles | guest_above | audio_hi | mmcss | power |
|---|---|---|---|---|---|---|---|---|
| mania_boot | 45.8, 47.4 / 42, 22 / 13, 4 | 46.9, 46.3 / 22, 29 / 4, 10 | 46.3, 46.8 / 37, 22 / 13, 4 | 44.2, 44.7 / 62, 58 / 57, 40 | 46.1, 46.8 / 30, 23 / 8, 8 | 46.6, 47.0 / 22, 22 / 4, 4 | 46.0, 47.1 / 22, 22 / 4, 4 | 46.6, 47.0 / 22, 22 / 4, 4 |
| dd_boot | 51.8, 50.5 / 17, 37 / 5, 17 | 51.9 / 17 / 5 | 50.2, 51.8 / 38, 17 / 14, 5 | 49.9, 52.1 / 17, 17 / 5, 5 | 48.3, 49.8 / 17, 17 / 5, 5 | 51.3, 46.8 / 17, 17 / 5, 5 | 39.7, 51.7 / 66, 17 / 32, 5 | 51.7, 51.8 / 17, 17 / 5, 5 |
| uc_walk | 34.3, 35.4 / 61, 39 / 106, 45 | 33.7, 35.2 / 71, 34 / 171, 29 | 35.4, 35.2 / 40, 42 / 46, 53 | 35.4, 34.7 / 36, 56 / 14, 121 | 35.3, 35.1 / 38, 46 / 36, 58 | 34.9, 34.9 / 49, 53 / 72, 88 | 35.0, 35.2 / 34, 43 / 18, 44 | 34.8, 35.5 / 53, 34 / 83, 19 |
| wrc4_boot (race) | 30.0, 29.7 / 34, 47 / 0, 15 | 29.2, 27.8 / 62, 87 / 32, 73 | 29.8, 30.0 / 34, 34 / 9, 0 | 30.0, 30.0 / 34, 34 / 0, 0 | 29.8, 30.0 / 38, 34 / 4, 0 | 28.8, 30.0 / 75, 34 / 51, 1 | 29.9, 29.9 / 39, 34 / 2, 1 | 30.0, 29.1 / 34, 67 / 0, 41 |
| kz_skin (from 4150) | 29.8, 29.9 / 36, 34 / 1, 0 | | | | 29.9 / 34 / 0 (final build, 1 run) | | | |
| kzl_walk3 (4300-5400) | 29.9, 29.9 / 34, 34 / 0, 0 | | | | 30.0 / 34 / 0 (final build, 1 run) | | | |

Steady fps (loads excluded) was at the cap in every run: Mania 56.6-58.7, DD 59.0-60.0, Uncharted 36.5-36.9,
WRC 4 29.9-30.0, Killzone 29.9-30.0. Every bad run (`dd_boot` mmcss run 1, `wrc4_boot` off run 2, `roles` Mania,
...) was dominated by `gpuIdle` / `pacing` / `fileIo` hitch causes: guest-bound frames that happen in all variants,
including "off", depending on what else ran on the machine. The `roles` Mania hitches (57, 40) came from one
guest-bound stretch (f1200-1310) and did not repeat under contention (4, 8), so they are counted as noise. Still, shader
workers at below normal bring nothing (as the kz-stutter branch found), so they were not kept.

## Results: contention
4 busy threads (2 runs each; fps / p99 ms / hitches):
| Scenario | off | gpu_above | roles | guest_above | audio_hi | mmcss | power |
|---|---|---|---|---|---|---|---|
| mania_boot | 46.5, 32.2 / 22, 108 / 4, 252 | 42.6, 45.6 / 72, 23 / 58, 9 | 46.5, 45.1 / 22, 24 / 4, 8 | 46.4, 44.8 / 29, 26 / 5, 7 | 46.3, 41.9 / 22, 55 / 4, 31 | 46.4, 39.6 / 22, 86 / 4, 97 | 46.8, 45.0 / 22, 25 / 4, 4 |
| kzl_walk3 | 29.9, 29.1 / 41, 62 / 0, 23 | 30.0, 30.0 / 38, 37 / 0, 0 | 29.9, 17.5 / 38, 253 / 0, 367 | 29.9, 25.6 / 34, 74 / 1, 29 | 30.0, 29.9 / 37, 41 / 0, 3 | 30.0, 29.0 / 34, 61 / 0, 25 | 30.0, 24.8 / 35, 87 / 0, 128 |

With 4 of 8 logical CPUs left free, the results are mostly noise again. The bad kzl_walk3 runs (roles 367, power 128)
are the known Killzone slow stretches where the main thread polls in `sceKernelDelayThread` and the GPU is idle
(docs/notes/kz-mainthread.md, "Open"). They show up in some runs of every variant.

8 busy threads (every logical CPU contended; 1 run each, 1-3 other emulators also running):
| Scenario | off | gpu_above | guest_above | mmcss | power | **final build (default on)** |
|---|---|---|---|---|---|---|
| mania_boot fps (steady) | 19.9 (40.4), 19.0 (39.5) | 15.9 (33.7) | **31.6 (58.0)** | 14.5 (31.3) | 16.9 (37.1) | **32.1 (58.1)** |
| mania_boot p99 ms | 95, 90 | 135 | **33** | 116 | 117 | **35** |
| mania_boot frames > 50 ms | 445, 334 | 847 | **19** | 781 | 594 | **22** |
| kzl_walk3 fps / p99 / hitches | 9.6 / 1334 / 842 | | | | | not measured (run stopped) |

Under full contention Mania (guest-bound: its main thread needs most of a core) only keeps up when the guest
threads outrank the busy programs. Raising only the GPU worker (normally, or through MMCSS) does nothing for a
guest-bound game. EcoQoS does not apply to this foreground workload either.

## Re-check after merging main fb661f7 (333 MHz default, shell core pool), under the shared-machine rules
By then other agents shared the machine, and every emulator run had to be pinned to CPUs 0-3
(`ProcessorAffinity = 0x0F`) with a BelowNormal process priority class. That distorts any priority experiment: the
boost is now relative to a below-normal class, inside 4 logical CPUs that the other agents' processes also use. These
runs show only the **relative** effect of `--host-prio=on` vs `off` under those same conditions. Mania, whole run, the
pairs in run order:

| Pair | on: fps / p99 ms / hitches | off: fps / p99 ms / hitches | CPU load before (on, off) |
|---|---|---|---|
| 1 (on first) | 27.5 / 457 / 156 | 43.5 / 28 / 4 | 49%, 63% |
| 2 (off first) | 43.2 / 28 / 4 | 43.6 / 28 / 6 | 62%, 68% |
| 3 (on first) | 43.6 / 28 / 14 | 33.9 / 173 / 73 | 22%, 24% |

There was one bad run on each side, both guest-bound (`gpuIdle`/`pacing`). In pair 1 the bad "on" run also had more
JIT time (1.20 vs 0.11 ms/frame) and GPU waits (3.5 vs 0.8). That fits a short load spike from another process
more than a systematic cost, since pairs 2 and 3 do not repeat it. Steady fps was 54.8-55.3 in all six runs. So in
this configuration the setting is neutral within the noise. Its measured gain is the unpinned full-contention case
above.

## Not measured
- kzl_walk3 under full contention with the final build (stopped), and kz_skin under contention.
- The window mode (OpenAL audio, SDL input, swapchain present). Every run was headless.
- Hybrid CPUs, battery power, Linux and macOS (the macOS QoS call has not been run on a Mac).
- Interaction with `--cores` and the shell's system-core pool from main fb661f7: those threads are guest threads too,
  so they get the same boost.
## Kept
- `HostThreads.Apply(Role.Guest)` in `GuestThread.HostMain` and `HostThreads.Apply(Role.Gpu)` in `GpuQueue.Run`:
  `ThreadPriority.AboveNormal` on Windows.
- The GPU worker is raised together with the guest threads: with only the guest threads raised, Killzone's
  main and job threads, which spin in `sceKernelDelayThread` polls (docs/notes/kz-mainthread.md), would outrank the
  GPU worker they wait for.
- JIT workers, the texture-inspection thread and the shader prewarm stay below normal. Shader workers stay normal.
- macOS (not measured): the same two roles get `QOS_CLASS_USER_INTERACTIVE` (`pthread_set_qos_class_self_np`),
  which also keeps them on the performance cores of Apple Silicon. Linux: unchanged, because a negative nice value
  needs `CAP_SYS_NICE`, so an unprivileged process cannot raise a thread above the default.

## Rejected or not applicable
- **GPU worker alone above normal / MMCSS "Games"**: no gain idle or under contention (Mania under full contention
  was worse, within the noise of single runs).
- **Audio threads at highest / MMCSS "Pro Audio"**: no measurable effect. Headless runs have no audio device; the
  guest audio thread mostly sleeps on the output pacing. In the window mode, the thread that mixes and plays the audio belongs to
  OpenAL Soft, which sets its own priority, so this branch does not touch it.
- **Power throttling off** (`ProcessPowerThrottling`, execution speed + ignore timer resolution): no gain. The
  emulator is a foreground, CPU-heavy process that Windows does not put in EcoQoS. Re-measure on battery with the
  window hidden if a report shows that pattern.
- **timeBeginPeriod(1) / high-resolution waitable timers**: already in place (`Runner.Run`, `PreciseSleep`).
  The 0.5-0.7 ms late wake of the high-resolution timer is a Windows limit; see kz-mainthread.md for the spin/sleep
  trade-off.
- **Hybrid CPUs (`SetThreadSelectedCpuSets` off E-cores)**: this machine is not hybrid, so it could not be measured,
  and it was not added. On hybrid Intel CPUs, Thread Director already prefers P-cores for high-priority foreground
  threads, and the threads raised here are exactly those.

## Next steps
- Re-measure on a hybrid CPU (12th gen Intel or later) and on battery: E-core pinning and power throttling are the
  settings that could matter there.
- The Killzone `DelayThread` spin (4 threads spinning) is now above normal: on a busy 4-core laptop it competes with
  the user's other programs harder than before. A shared spinner thread (kz-mainthread.md) would limit that to one core.
