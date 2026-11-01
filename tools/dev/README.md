# Developer flavour (`-p:VitaSharpDeveloper=true`)

Tooling for performance work that iterates in seconds: no APK rebuild and no game boot per experiment.

```
dotnet publish src/VitaSharp.Android -c Release -r android-arm64 -p:VitaSharpDeveloper=true [-p:VitaSharpAndroidRuntime=mono-aot] -m:2
dotnet build tools/VitaSharp.PerfTools -c Release -p:VitaSharpDeveloper=true -m:2     # vitasharp-perf bench, ...
```

- The property defines `VITASHARP_DEVELOPER` in every project (`Directory.Build.props`). Optimisation, AOT mode and
  trimming are those of the normal Release build.
- The tooling lives in `Developer/` folders (namespaces `*.Developer`) and behind `#if VITASHARP_DEVELOPER` hooks.
  A normal build has none of it: `DeveloperGatingTests` checks the assemblies, and `build/check-dev-gating.{ps1,sh}
  APK|FOLDER` checks a package (`--expect-dev` / `-ExpectDev` for the positive check on a developer APK).
- Nothing runs until a bench or trace is started. The hooks test static switches that are off by default.
- The developer APK is called **VitaSharp Dev** (launcher label, and "VitaSharp Dev" in the log header). It keeps the
  package id `io.github.vitasharp`, so `adb install -r` keeps the user's data.
- Fixtures contain game data, so they never go into git. On the phone they live in
  `/sdcard/Android/data/io.github.vitasharp/files/bench/`; on the PC, in any folder with the same layout:

```
bench/fixtures/NAME.vsgd           GPU fixtures (vitasharp-perf bench cut)
bench/variants/NAME/               a variant: replacement shaders + params.txt (render state), no rebuild
bench/results/RUN.tsv              one row per timed repetition (fixture, variant, config, rep, frame ms, scene ms)
bench/shadercache/                 the bench's own shader cache
```

## 1. GPU bench

**Fixtures.** A fixture is a GPU dump (`VITASHARP_GPU_DUMP_AT`, `build/android-gpu-dump.*`) cut after its target
frame. The frames before the target are its state: guest memory, shader definitions, and the render targets the dump
carries. The bench replays that state once, then replays the target frame N times with a timestamp pair around every
scene. A scene is a render pass, and Mali tile traffic is per pass.

```
vitasharp-perf bench cut DUMP.vsgd                        # lists the frames (scenes, draws, offset in the file)
vitasharp-perf bench cut DUMP.vsgd --frame 3 --name uc_title --root bench
```

**Single draws.** `draw=K` (the draw's 1-based serial in the target frame; the run prints each scene's draw range)
works in one of two modes:
- `drawmode=without` alternates the whole frame with the frame without draw K. The delta is the draw's marginal cost
  in context.
- `drawmode=only` alternates K's scene with only draw K against the scene with no draws. The delta is the draw on its
  own, with load/store and clear excluded.

Timestamps around single draws inside a pass are meaningless on a tiler, so draws are measured by difference.

**Variants** (`bench/variants/NAME/`, all read at run time):
- Shaders: `HASH_HINT.frag` / `.vert` (GLSL), or the same name + `.spv` (SPIR-V), replaces the translated shader with
  that program hash and hint key. `HASH.frag` replaces the shader for every hint of the program. The translator's
  interface (bindings, outputs) stays as it is. `vitasharp-perf bench shaders --root bench --fixture NAME` writes the
  fixture's translated GLSL to `variants/_translated/NAME/`, ready to copy and edit.
- `params.txt`: `key=value` lines, the same keys as the bench parameters:
  - `depthload=clear|dontcare`: depth load op of the renderings that do not load depth.
  - `depthstore=store|dontcare`: depth store op of the tiled renderings.
  - `barrier=full`: a full memory barrier after every tiled rendering.
  - `msaa=...`: `VITASHARP_MSAA`.
  - `fetchmerge=0|1`: `VITASHARP_FETCH_PASS_MERGE`, the fetch-pass merge.
  - `fetchsplit`, `msaafetch`, `scenebreaks`, `gpuquirks`.
  - `gpuprofile=mali|...`.
  - `env.VITASHARP_X=...`: any other renderer switch.

  The renderer reads its environment switches once per process, so every run is a process of its own. The renderer
  has no resolution-scale setting; `msaa` selects the 1x/2x render paths.

**Desktop:**
```
vitasharp-perf bench run --root bench --fixtures uc_title,f1_race --variants base,v1 --rounds 4 --reps 3 [--param gpuprofile=mali] [--scenes 3]
```
- `bench run` starts one child per (fixture, variant, round). Variants are interleaved A/B, B/A. Each child runs with
  affinity 0x0F at below-normal priority.
- `bench table results/RUN.tsv --base base` prints the table again.

**Phone** (developer APK installed):
```
build/android-bench.sh uc_title,f1_race base,v1 [ROUNDS=4] [REPS=3]      # BENCH_DIR=bench, PARAM='k=v;...'
build/android-bench.ps1 -Fixtures uc_title -Variants base,v1 -Rounds 4 -Reps 3
```
- The script pushes fixtures (only when their size changed) and variants. Each run is
  `am start -n io.github.vitasharp/io.github.vitasharp.BenchActivity --es bench 'fixture=..;variant=..;reps=..;run=..'`:
  a `:bench` process that ends when its run is done.
- The script pulls `results/RUN.tsv` and prints the table.
- The same extra on the launcher activity (`--es bench ...`) forwards to the bench.
- BENCH lines also go to logcat and to `logs/bench.log`.
- On the phone itself: Ajustes > Depuração > **Opções de desenvolvedor** lists the fixtures, runs one against every
  variant (3 interleaved rounds) and shows the table.

**Reading the table:**
- `GPU ms` is the median over every timed repetition of every round.
- `spread` is max - min.
- `delta` is against the base variant (the first one). Draw benches compare against their own reference config.
- Decide only when the delta is clearly larger than the spread of both rows.
- A scene's time runs from the end of the work before it to its own end: bottom-of-pipe timestamps at both ends.

## 2. Perfetto tracing on the phone

```
build/android-trace.sh PCSE00002 10 40 [INPUT]       # 10 s traced after 40 s of play; OUT=android-trace
build/android-trace.ps1 PCSE00002 -Seconds 10 -Delay 40
```

The script writes `VITASHARP_TRACE=1` (and `VITASHARP_TRACE_HLE=N`) to `files/config/env.txt`, starts the game and
records `tools/dev/perfetto-vitasharp.pbtx` with `perfetto --txt`. It then pulls `TITLE.perfetto-trace` and
`last.log`, removes `env.txt` and the trace from the phone, and force-stops the app.

The trace records:
- CPU scheduling (`sched_switch`/`sched_waking`), CPU frequency and idle, GPU frequency (`power/gpu_frequency`) and
  GPU memory.
- GPU counters and render stages, when the driver exposes them. Mali's `gpu.counters` producer is vendor-dependent:
  the track is simply missing when the driver has none.
- The app's markers. They are compiled only into the developer APK, and each costs one static readonly test when
  tracing is off (`Core/Util/Developer/DevTrace.cs`, ATrace through `libandroid.so`):

| Marker | Thread | Meaning |
|---|---|---|
| `guest frame` (counter) + `guest flip` | the guest's display thread | one `sceDisplaySetFrameBuf`: the guest's frame rate is the counter's slope |
| `gpu record frame` | GPU worker | from the first command after a present to the present: the CPU cost of recording one frame |
| `gpu commands/frame` (counter) | GPU worker | commands in that frame |
| `vkQueueSubmit` | GPU worker | the submit call itself (driver CPU time) |
| `hle NAME` | guest threads | one HLE call in `VITASHARP_TRACE_HLE` (default 64), so a hot function shows up in proportion to its call count |
| `jit compile` | guest threads | a native ARM64 block compile |
| `audio grain` | audio output thread | one write of mixed output to AAudio/AudioTrack |

### Reading a trace (ui.perfetto.dev)

- **Is it the CPU or the GPU?** Pin the GPU worker, the guest main thread and the `gpu_frequency` track.
  - The GPU is the limit when `gpu record frame` slices are short but the worker waits (sleeping in the fence wait
    between frames), with `gpu_frequency` at maximum.
  - The CPU is the limit when the worker or the main thread is Running end to end at the frame rate.
- **Big or little core?** Colour by CPU: the slices of the `sched` track show which core each thread ran on. On the Redmi
  Note 14 (2x A76 + 6x A55), the A76s are the two CPUs with the highest `cpu_frequency`.
 A hot thread on an A55 is a scheduling
  problem (`VITASHARP_ANDROID_AFFINITY`), not a code problem.
- **Frequency drops.** The `cpu_frequency` tracks of CPUs 6/7 and `gpu_frequency`: a sawtooth is thermal or DVFS. A
  bench decision should not rest on a thermally throttled run.
- **HLE hot spots.** Use Query (SQL):
  `select name, count(*) * 64 as calls, sum(dur)/1e6 * 64 as est_ms from slice where name like 'hle %' group by name order by est_ms desc limit 20`
  (64 = `VITASHARP_TRACE_HLE`).
- **Hitches.** A long `guest flip` gap that lines up with a `jit compile` burst or a `vkQueueSubmit` spike names the
  cause.
- **Audio.** `audio grain` slices should be evenly spaced. A gap longer than the device buffer is an underrun, and a
  click.
