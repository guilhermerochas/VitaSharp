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

## Element fixtures, picture check and flicker mode

- **Selecting elements.** `bench cut DUMP --frame I --name N --select "scenes=0,3;draws=A-B;vs=HEX|HEX;fs=HEX"` stores
  a selection in the fixture. Only those draws of the target frame run, so the time is that element's. The same keys
  work as run parameters.
  - The scenes that are not selected still begin and end: their load/store traffic stays in the frame.
  - `fragment=discard` replaces every fragment shader with a discard, which leaves the selection's vertex-side cost.
    Compare it with the normal run to split vertex from fragment cost.
- **Picture check.** Every run saves the target frame's picture (`results/pictures/FIXTURE_VARIANT.png`). The table's
  `picture` column says `same` or `DIFFERS (mean channel difference)` against the base variant. A variant that changes
  the picture is not a valid optimisation, whatever its time: `depthload=dontcare` is the example, 32% faster on the
  Uncharted title only because it shades the wrong fragments.
- **Flicker mode.** `mode=flicker;loops=3;speed=real|max` replays every frame of a whole dump in a loop, reads each
  presented frame back small and runs the flicker/dark-frame detector (`FlickerDetector`, the rule of
  `vitasharp-perf flicker` on the whole picture and on a 4x4 grid).
  - Flagged frames are saved with their neighbours under `results/RUN_flicker/`.
  - `skipscene=K` and `drawlimit=N` bisect.
  - In the game itself (developer APK), `VITASHARP_FLICKER_CHECK=1` in `env.txt` runs the same check on every
    presented frame and writes FLICKER lines to the log. Desktop test scenarios run it by default (`--flicker-check`).

| Fixture | From | Measures |
|---|---|---|
| `uc_title` | Uncharted, title (phone3 `uc3.vsgd`, frame 3) | the title's 8 scenes: lighting buffer s0 (draws 1-217) and fetch-pass scene s3 (220-264) |
| `uc3` (whole dump) | Uncharted, title, 301 frames | flicker mode (a dark movie picture at frame 277 is in the dump itself) |
| `f1_race` | F1 2011 race start (phone2 dump, frame 1) | 8 scenes, ~1,630 draws |

The fixtures stay out of git. They are cut again from the scratch dumps with the commands above.

## Captures from the pause menu (developer APK)

**Recording, for the player:**
1. In a game, press PS to open the pause menu and choose **Gravar tudo**.
2. Play the scene. "● REC" shows next to the fps counter while it records.
3. Open the menu again and choose **Parar gravação**. Write a line about what happened (or edit `scene:` in
   `info.txt`).

| Option | Records |
|---|---|
| **Gravar tudo** | <ul><li>The first 5 s are timing only: fps, the renderer's PERF lines and the trace markers, with no dump slowing the game. They are written to `info.txt` as the "timing part".</li><li>Then a GPU dump of up to 600 frames: commands, shaders, and every guest memory range the GPU read, so the textures as uploaded.</li><li>The guest audio ports as WAVs (`audio/`), from the start.</li><li>`log.txt` and `info.txt`.</li></ul> |
| **Gravar tempo** | the timing part only, for the whole recording |
| **Gravar conteúdo** | the dump and audio only |

- Output: `files/captures/TITLE_DATE_KIND/`.
- Scripted: `VITASHARP_CAPTURE=tudo@START:STOP` (seconds after the game starts) in `env.txt`.
- Not in a capture yet:
  - The device-side output audio (the guest ports are recorded).
  - A texture-path bench (detile/convert/upload timings). The data is in the dump.
  - simpleperf, which cannot run from inside the app. Use `adb shell simpleperf record --app io.github.vitasharp` on a
    `<profileable>` build (docs/android/README.md, Phone run 2).

**Analysing a capture:**
```
build/android-capture-analyze.sh [CAPTURE_NAME|LOCAL_FOLDER]      # no argument: the newest capture on the phone
build/android-capture-analyze.ps1 [-Capture NAME|FOLDER] [-Desktop VitaSharp.Desktop.exe]
```
The script pulls the capture to `./captures/` and prints:
- `info.txt`: the timing part, and the PERF lines at the stop;
- the fps windows and capture lines of the log, and the HLE/thread lines;
- `vitasharp-perf audio-check` over the port WAVs;
- the flicker check over every frame of the dump, replayed on the PC;
- the GPU ms per scene of the middle frame (PC GPU, so relative costs);
- with `DESKTOP` / `-Desktop`, a Vulkan validation replay with synchronization validation.

For phone GPU ms, cut fixtures from the dump (`bench cut`, element selections) and run `build/android-bench.*`.
