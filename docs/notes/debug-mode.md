# Diagnostics: F10 capture, debug mode, watchdog, session logs

Branch `feat/diag-capture`. Goal: when a user sees a problem while playing normally, the evidence exists without
reproducing the scene. Everything here is generic (no title-specific code).

## Where things go

| What | Where |
|---|---|
| Current session log | `<DataRoot>/logs/last.log` |
| Previous sessions | `<DataRoot>/logs/<yyyyMMdd_HHmmss>_<TITLEID>.log` (start time + title of that session, newest 10 kept) |
| Diagnostic captures | `<DataRoot>/diagnostics/<TITLEID>_<yyyyMMdd_HHmmss>/` and the same name `.zip` next to it |

`--log=<file>` (test scenarios) bypasses the rotation. If another VitaSharp holds `last.log`, the session writes
`logs/<now>_<TITLEID>.log` instead. The first line of every session log is
`VitaSharp session <TITLEID> <yyyyMMdd_HHmmss> VitaSharp <version>+<commit>, built <date>`.

The log is buffered and flushed every 250 ms (warnings and errors immediately, and at process exit), so a crash or a
kill loses at most a quarter of a second. Unhandled exceptions (any thread) and unobserved task exceptions are logged
as `UNHANDLED EXCEPTION ...` / `Unobserved task exception` and flushed. A Vulkan device loss logs
`[gpu] VULKAN DEVICE LOST in <call> at present #N` with the renderer state and the last 20 frame summaries.
Above 256 M characters in one session, info lines are dropped (warnings/errors still written) so a runaway log cannot
fill the disk.

## F10: "Capturar diagnóstico"

Press F10 in the game window (also `--diag-at=FRAME[,FRAME...]` for tests, and automatically in debug mode, below).
The title bar shows `Capturando diagnóstico...` then `Diagnóstico salvo: <zip>` for 6 s; the log gets
`[diag] capture requested ...`, `[diag] capture armed at present #N ...`, `[diag] frame traced: ...` and
`[diag] saved: <dir> and <zip>`. Presses during a capture are ignored (`[diag] F10: ignored, a capture is in progress`).

The renderer arms the trace at the next present and traces the **whole following frame** (present #N to #N+1); the
frame is presented normally, then every render target is read back. The game stalls for the readbacks (typically
0.5-2 s for a 3D scene) and then continues; the trace mode is switched off again afterwards.

The traced frame is the one *after* the request, so a glitch that lasts a few frames is usually gone by then. In
debug mode the capture therefore also contains `replay/`: the last ~128 presented frames before the traced one (see
"Replay ring" below), written when the trace is armed. An automatic capture waits 30 more presents (~1 s) before it
arms, so `replay/` holds both sides of the frame that triggered it.

Folder contents:

| File | Content |
|---|---|
| `info.txt` | reason, title, guest frame, build (version+commit), runtime/OS, command line, CPU backend, debug mode, window size, GPU/driver/Vulkan version, MSAA mode, shader settings, shader cache state, GPU queue counters, every `VITASHARP_*` environment variable |
| `screenshot.png` | the presented frame at the end of the traced frame |
| `replay/` | debug mode only: `frame_<present>.png` for each of the last ~128 presented frames (480x272), `sheet_<first>-<last>.png` contact sheets of 30 frames (6 per row, oldest top left; red border = the trigger frame, yellow = a frame over a per-frame threshold), `replay.txt` (below) |
| `draws.txt` | every `[scene] begin` (render target, GXM MSAA mode, downscale, depth surface) and `[draw]` of the frame, in order |
| `shaders/` | GLSL of every program drawn, `<gxp hash>_<hint key>.vert/.frag` (shaders loaded from the shader pack are re-translated for this, nothing is recompiled) |
| `targets/` | every render target as `rt_<addr>_<w>x<h>.png` (replaced targets with `_<fmt>`), plus `targets.txt` |
| `textures/` | every texture sampled in the frame as PNG plus `textures.txt` |
| `frames.txt` | the last 120 presented frames (rolling summary, below) |
| `renderer.txt` | GPU/driver/settings, renderer state (frame slots, writebacks), texture cache and GPU time statistics so far |
| `state.txt` | guest threads (status, wait object, pc/lr/backtrace; last 16 HLE calls each in debug mode), held mutexes with waiters, GPU queue (submitted/completed, command executing), last 30 frames |
| `audio.txt` | audio device and buffering, open and released ports (outputs/s, gaps, underruns, silent grains, host queue), NGS systems and decoder streams (see Audio below) |
| `log.txt` | the session log up to the capture |

If no frame is presented within 15 s after the request (a hang), the GPU part is skipped and `info.txt` says
`NOTE: no frame was presented within 15 s (hang?)`; `state.txt`, `frames.txt` and the log are still written, which is
exactly what a hang needs.

### Reading draws.txt

```
[scene] begin present=N rt=0x65C78000 960x544 msaa=1 downscale=True ds=0x... load=... bgDepth=...
[draw] id=12 f=N rt=0x65C78000:0x0... ds=0x.../960x544 vs=<hash> fs=<hash> prim=... n=<indices>x<instances>
       blend=<mask>:<func>/<src>/<dst> fragEn cull twoSided depth=<func>/<write> bias st vp=(...) clip=...
       t0=0x<addr>:<fmt>:<w>x<h>:T<type>:S<stride>:G<gamma>:F<mag><min><mip>:B<lodbias>:L<lodmin>:M<mips>:A<u><v>:W0=...
       a[r<reg> s<stream>+<off> f<fmt>x<n>] st[<stride>/<indexsource>] v0@..=<raw vertex bytes> v1=...
       vu/fu (uniform sizes) fub<n>=<buffer>+<bytes> vuf=<first vertex uniforms> fuh=<first fragment uniforms as half>
       msaa=<off|ss2x|4x>  (how the scene is actually rendered: native MSAA samples, 2x supersampling, off)
```

To find the draw that produced a glitch: look at `screenshot.png`, find the target in `targets/` that shows it first
(the HDR scene, a quarter-res buffer, the post atlas...), then the draws whose `rt=` is that address; their `fs=` hash
names the GLSL in `shaders/`, their `tN=` the textures in `textures/`. `targets.txt` has numeric per-channel
min/max/mean and NaN/Inf counts, so NaN-propagation or a saturated channel shows up without looking at the PNGs.

## Rolling frame summaries (always on)

A ring of the last 120 presents (a few field writes per frame) goes into every capture as `frames.txt`, into hang and
device-lost reports:

```
p4301 f4301 33.4ms draws=812 scenes=9 newSh=0 newPipe=2 newRT=0 newTex=1 hitch=fence(3.1) fb=0x60000000 960x544
```

`intervalMs` = host time since the previous present; `newSh/newPipe/newRT/newTex` = shaders, pipelines, render targets,
textures first used in that frame; `hitch` = the biggest stall cause of the frame from the FrameLedger (shTrans,
shaderc, shPack, pipeline, texUpload, fence, writeback, guestGpuWait, fileIo, shWait) with its milliseconds.

## Hang / stall watchdog (always on)

Checked once per second. When no frame was presented for 5 s (after the first frame), or the GPU worker did not
complete a command for 5 s while commands are pending, it logs a warning

```
[watchdog] STALL: no frame presented for 5 s (frame 1275)
guest frame 1275; GPU queue: submitted 912345, completed 912300, executing DrawCommand
renderer: present #1275, GPU frames ..., in scene True, rt 0x..., draws this frame 431, device lost False, ...
  frame slot 0: recording True, submitted True (seq ...), retired False, writebacks 2, signals 1
thread 'main' uid=0x... Waiting wait=[sema ...] pc=... lr=... bt:...
    hle f1275 sceGxmNotificationWait(0x81A5C84C, ...) lr=0x... (not returned)
mutex 'X' held by 'Y' (Waiting) count 1, 2 waiting
last frames: ...
```

and repeats it at 30 s and 120 s of the same stall. `executing` is the GPU command the worker is inside (stuck in the
backend when it stays the same), `(not returned)` marks the HLE call a thread is blocked in (debug mode only; the last 6 calls per thread here, 16 in
a capture's state.txt).

## Debug mode ("Modo de depuração")

Launcher: Configurações > Geral > "Modo de depuração (gera logs detalhados para diagnóstico)", saved in
`launcher.json` (`DebugMode`) and passed as `--debug-mode`; also `VITASHARP_DEBUG_MODE=1`. (`--debug` is unrelated:
it sets the log level to Debug, which is far more verbose.) The log says `[dbg] debug mode on` at the start.
F9 in the game window toggles it at runtime (`[dbg] debug mode on|off (F9) at guest frame N`): turned on mid-game,
the first-use lines report every shader, pipeline, target and texture format used from that frame on, and the
per-thread HLE rings start filling then.

All lines carry the log timestamp; `[dbg]` lines also `f<guest frame> p<renderer present>`.

| Line | When |
|---|---|
| `[dbg] ... new vertex shader <hash> hint <key>: N attributes, M streams, translated / from the shader pack / FAILED` | first use of each vertex program + hint variant |
| `[dbg] ... new fragment shader <hash> hint <key>: output fmt 0x.. param <type>x<n> start <reg>, program msaa mode, raw colour bits, framebuffer fetch, writes depth, textures mask, rt 0x.. fmt 0x.. (<vk format>), scene msaa <path>` | first use of each fragment program + variant |
| `[dbg] ... new pipeline vs .. fs .. <topology> colour <vk format> samples <n> blend ... mask ... layout ...` | first use of each pipeline (incl. prewarmed ones) |
| `[dbg] ... new render target 0x.. WxH stride fmt 0x.. -> <vk format>; replaces ...; overlaps ...` | every new colour surface; `replaces` = same address with another size/format (reinterpretation), `overlaps` = other targets sharing memory (aliasing) |
| `[dbg] ... new scene setup: rt ... gxm msaa <mode> downscale <b> -> <native Nx / supersampled 2x / off>, depth ..., gamma` | first time each target/MSAA/depth combination is rendered |
| `[dbg] ... new texture format 0x.. (<base>, swizzle bits 0x..) type 0x.. first seen at 0x.. WxH mips gamma` | first texture of each format+swizzle and type |
| `[dbg] ... texture 0x.. fmt .. lies in N render targets, tried in this order: [...]` | a sampled address inside several colour targets (ResolveTexture uses the first compatible one in that order) |
| `[dbg] ... address 0x.. lies in N depth/stencil targets: [...]; picked 0x.. (most recently used)` | DepthTargetContaining with several candidates |
| `[dbg] f.. HLE-ERR [thread] func(a0, a1, a2, a3) = 0x80xxxxxx lr=0x..` | an HLE function returned an SCE error (0x80000000-0x80FFFFFF; user memory starts at 0x81000000, so pointers are not counted). First 3 per (function, code), then the 10th, 100th, ... with `(#n)` |
| `[dbg] f.. HLE-UNIMPL [thread] lib::func (NID ..) lr=.. (#n)` | unimplemented calls, same rate limit |
| `PERF periodic f..: fps, process cpu ms/frame, allocated KB/frame, GC counts, working set, GPU queue pending, threads` | every 60 s |
| `[dbg] WARN x<n>: <warning with numbers as #>`, `[dbg] HLE-ERR summary: ...`, `[dbg] HLE-UNIMPL summary: ...` | every 10 minutes and at the end of the session |
| `[frame-metrics] p.. mean white black magenta green blue` | every 300 presents |
| `[anomaly] p.. f.. <kind>: <details>` / `... cleared (after n frames)` | see below |

Warnings are deduplicated by their text with numbers masked (`#`): the first three of a kind are written, then the
10th, 100th, ... with `(repeated n times)`.

Texture inspection (the test harness's TEXTURE SUSPECT check: noise ~0.33 = random data, seams > 1.8 = broken block
decoding) runs on at most 2 texture uploads per frame.

### Replay ring, per-frame anomaly checks and automatic captures

While debug mode is on (from the start or after F9; F9 off frees it), **every** present blits the presented image,
downscaled to fit 480x272, into a small device-local staging image and copies it into the next slot of a ring of
128 host-visible (cached) staging slots, all with the frame's own command buffer: nothing waits for the GPU. When
the frame retires (non-blocking, `VulkanRenderer.Sync.cs`), its slot is measured on every 4th pixel of every 4th row
(~8K samples, a 120x68 grid of the 960x544 frame) and fed to the anomaly rules. The log says `[dbg] ... replay ring on: 128 frames at up to 480x272
(64 MB host memory)`.

Size: 128 slots = 4.3 s at 30 fps, 2.1 s at 60 fps; 480x272 RGBA8 is 510 KB per slot, 64 MB of system memory in
all (no VRAM besides one 510 KB staging image). Full 960x544 would be 255 MB for the same time span, and half
resolution is plenty to see what a glitch looks like (the PNGs in `targets/` of the traced frame are full size).

Per-frame metrics (same thresholds as the test harness): mean brightness, white (all channels > 235), black (all
< 10), magenta (r,b > 200, g < 80: missing/garbage textures), green (g > 180, r,b < 60) and blue (b > 180, r,g < 60:
garbage render target / effect).

| Kind | Rule | Auto capture |
|---|---|---|
| `magenta` | > 5% magenta in 2 of 3 consecutive frames | yes |
| `saturated` | > 8% pure green + pure blue in 2 of 3 consecutive frames (a 2-frame burst, or on/off/on) | yes |
| `flicker` | the mean brightness jumps by > 0.35 for 1-3 frames and then comes back to within 0.12 of the frame before (a transient flash or black frame; a cut or fade that stays is not one). The frames before and after must not be black | yes |
| `blown-out` | > 35% saturated white, or mean brightness > 0.85, for 60 frames | yes |
| `black` | > 97% black for 30 frames | no (loading screens) |
| `frozen` | identical image for 600 presents | no (pause menus) |

A single-frame spike is ignored. Lines: `[anomaly] p<present> f<guest frame> <kind>: <details>` when confirmed,
`... <kind> cleared` / `cleared after n frames` when it ends, `[frame-metrics] p.. mean white black magenta green
blue` every 300 presents.

A confirmed anomaly with "auto capture" triggers the same capture as F10 (`[diag] capture requested (auto: <kind> at
present #N - <details>)`), at most one every 5 minutes and 3 per session (`[diag] anomaly ...: skipped` otherwise).
The renderer keeps filling the ring for 30 more presents (~1 s) before it arms the trace, so `replay/` covers about
3 s before the spike and 1 s after it. F10 and `--diag-at` arm at the next present as before (their `replay/` ends
at the present before the traced frame).

At arm time the renderer retires the frames in flight (a wait of at most a frame's GPU work, during a capture that
stalls anyway), copies the measured slots out of the ring (~64 MB memcpy) and writes the PNGs on a worker thread
while the next frame is traced; the capture is zipped once both are done (`[diag] replay: N frames (presents
#a-#b), trigger #t` and `[diag] replay written: ...`; info.txt says `Replay: N presented frames ...`, or `off` when
debug mode was off).

`replay/replay.txt`:

```
# present guestFrame time(local) dt_to_trigger_ms size(src) mean white black magenta green blue hits
p2999 f2999 23:58:01.402 -33 480x272(960x544) 0.312 0.000 0.020 0.000 0.000 0.001 -
p3000 f3000 23:58:01.435 0 480x272(960x544) 0.512 0.000 0.010 0.000 0.500 0.001 S  <== trigger
...
# recent anomaly events (debug mode, newest last)
p3001 saturated [capture]: 50% pure green and 0% pure blue pixels (...), in 2 of the last 3 frames
```

`hits` = per-frame thresholds the frame is over (M magenta, S saturated, B blown-out, K black); the trigger is the
present the anomaly was confirmed on (auto) or the present when the request arrived (F10/--diag-at); `dt` is
relative to it.

Test switch: `VITASHARP_DEBUG_INJECT=<kind>@<present>[:<count>]` (kind = green, blue, magenta, black, white; count
default 4) paints the replay copies of those presents (green/blue/magenta over the top half, black/white over the
whole frame) before they are measured (`[anomaly] test injection (green) into present #N`), so both the detector and
the saved `replay/` see it; the rendered frame is untouched. `tests/scenarios/kz_replay.txt` with
`VITASHARP_DEBUG_INJECT=green@3000:2` checks a 2-frame burst end to end (auto capture with the green frames in
`replay/`) and the `--diag-at` path. The rules themselves are unit-tested (`FrameAnomalyDetectorTests`).

### NaN/Inf detection

Branch `feat/diag-nan`, `src/VitaSharp.Renderer.Vulkan/VulkanRenderer.NanCheck.cs`. Meant for glitches that look
like NaN/Inf spreading through post-processing (saturated blocky areas growing out of bloom/blur/exposure passes),
which the presented image alone does not explain.

**Layer 1, per-scene target scan (debug mode, follows F9).** At the end of every scene whose colour surface is a float
format (host F16x4: guest F16x4 HDR and U2F10F10F10; F11F11F10; F16/F16x2/F32/F32x2), a 16x16 compute shader reads
**every** pixel of the surface and counts the pixels with a NaN, an Inf, a huge finite value (>= 65000 for 16-bit and
packed formats, >= 1e30 for F32) or a negative value, plus the first NaN/Inf (and first huge) pixel in raster order.
It is recorded into the frame's own command buffer (barrier, dispatch, barrier); the 2 KB result buffer of the frame
slot is host-visible and read when the frame retires (non-blocking retire path, `VulkanRenderer.Sync.cs`), so the GPU
worker never waits for it. Up to 64 scans per submission. Surfaces written with raw register bits (packed data in an
F16x4 surface, see `RawOutputBits`) are skipped: their bits are not colours. A scene that does not render into a
float surface costs nothing.

A target is *bad* in a scan when it has NaN, Inf or huge values, or negative values in a guest format that cannot
hold them (U2F10F10F10, F11F11F10: the hardware stores 0 there, so a negative value in our F16 image is itself an
emulation difference). Per target (address, size, format) the renderer keeps a history:

```
[nan] f4321 p4321 rt 0x65C78000 960x544 F16x4: 1234 NaN, 56 Inf, 0 huge, 0 negative of 522240 px (first NaN/Inf at 312,140) after scene 3 (draws 120-410) - first time in this target
[nan] f4621 p4621 rt 0x65C78000 960x544 F16x4: still 980 NaN, ... (since present 4321)
[nan] f4700 p4700 rt 0x65C78000 960x544 F16x4: clean again (bad in presents 4321-4697, 377 bad presents in all so far)
```

`after scene N` = the N-th scene of that present (`draws a-b` = the draw ids of that scene, as in draws.txt).
Rate limits per target: a new episode is logged when it is the first one or at least 30 presents after the
previous line (shorter ones are counted and mentioned in the next line), an ongoing one repeats every 300 presents,
and a target is clean again after 3 presents without a bad scan.

A target going from clean to NaN/Inf (not huge/negative alone) is an anomaly of kind `nan`: `[anomaly] p.. f.. nan:
<the line above>`, listed in replay.txt's events, and an automatic capture under the usual limits (one every 5
minutes, 3 per session; the renderer itself raises at most one `nan` anomaly every 300 presents). Unlike the
presented-image anomalies it arms at the **next** present (no 1 s post-roll), so the traced frame, `targets/` and
`targets.txt` (NaN/Inf counts of F16x4 targets) are taken right after the NaN appeared, while `replay/` holds the
~4 s before. Every capture taken while scans ran has `nan.txt`: per float target the scans, bad presents, episodes
(present ranges), maxima and the last scan result; in draws mode also the program list below.

**Layer 2, "which draw produced it": `VITASHARP_DEBUG_NAN=draws`** (environment, chosen at launch; heavier, works with
or without debug mode, use both for captures). Every generated fragment shader gets, after its output (and after
the `VITASHARP_DEBUG_FS`/`DEBUG_TEX0` overrides):

```glsl
if (any(isnan(out_color)) || any(isinf(out_color))) { atomics into nan_hits[draw_id]: fragments, NaN/Inf flags, first pixel }
```

`draw_id` is the existing push constant (the draw id of draws.txt, per present); the table (4096 entries, ids above
share the last one) is set 0 binding 4, a 64 KB region of the frame slot's NaN buffer, read at retire. The draws of
each submission are remembered (programs, hints, render target, textures), and every hit is logged:

```
[nan-draw] f4321 p4321 draw id=211 scene 3: 4096 fragments NaN (first at 312,140) vs=... fs=94A7330124AABFB7 fsHint=... rt=0x65C78000 960x544 F16x4 prim=0 n=6x1 msaa=off t0=0x...:fmt:WxH ...
[nan-draw] p4321 3 draws with NaN/Inf output in this submission: id=211:fs=...(4096) ...
```

Per vs/fs/render-target combination, the presents 1-3, 10, 100, ... in which it produced NaN are logged with all
its draws (40 lines per submission at most); the per-submission summary at most every 30 presents. Notes:
- it counts *shaded* fragments: a fragment later rejected by the depth/stencil test, a colour mask of 0 or
  blending with a zero factor still counts (the atomics make the shader run late-Z). Compare with layer 1 to see
  whether the NaN lands in a target.
- raw-bit outputs (packed data) are not checked unless a debug override replaced them.
- the instrumented GLSL counts as a debug override (`ShaderGenerator.DebugOverridesActive`): `shaders.pack`,
  `programs.pack` and `pipelines.list` are bypassed exactly as for `VITASHARP_DEBUG_FS`, the SPIR-V is not written to
  the loose `.spv` cache and the driver pipeline cache file is loaded but not saved, so the normal caches never
  see an instrumented shader. The normal generator output is unchanged (no `GeneratorVersion` bump), and with the
  variable unset the set 0 layout keeps its 4 bindings.
- it is a launch switch rather than a runtime toggle (Shift+F9) because switching would need a second variant of
  every shader and pipeline at runtime.

**Test injection.** `VITASHARP_DEBUG_FS=<fragment program hash>:vec4(uintBitsToFloat(0x7FC00000u))` forces a NaN
output in one fragment program (all its draws). With `--debug-mode` the scan reports the target and auto-captures;
with `VITASHARP_DEBUG_NAN=draws` as well, the `[nan-draw]` lines name exactly that program's draws.

## Audio

`src/VitaSharp.Core/Util/AudioDiagnostics.cs` counts, for every sceAudioOut port, the guest outputs, the gaps
between them, silent (all-zero) grains and host underruns. Counting is always on (a few field writes per output); what
gets logged depends on debug mode, which is checked on every call, so F9 takes effect immediately.

**Host device line** (start of every session): `Audio: OpenAL Soft, OpenAL '<device>' mixer 48000 Hz, update 960
samples (20,0 ms)`. OpenAL Soft mixes in updates of that size and only gives back a buffer once it is fully played,
so each port's host queue holds `2 x update + 2 grains` (at least 4 grains) and playback starts or resumes once
`update + 1 grain` is queued. The per-stream part (`stream 48000 Hz grain 256: up to 10 grains queued (53,3 ms), starts
at 5, latency ~47 ms, underruns N`) appears in the summary, the snapshot and audio.txt.

**Always logged:**

| Line | When |
|---|---|
| `Audio: underrun burst #n on port 0 MAIN at f..: host queue ran dry, X ms since the previous output (grain Y ms), host queue N samples` | first underrun of a burst (underruns less than 1 s apart are one burst). With debug mode off: bursts 1-3, 10, 100, ... |
| `AUDIO summary: device ...` and `AUDIO summary: port <id> <MAIN/BGM/VOICE> grain G F Hz stereo volume l/r: N outputs in T s (R/s while active, expected E), gap avg/max, underruns n (n/min) in b bursts, silent s` | end of the session (window and test runs; test reports include them). `while active` = outputs over the time spent between outputs, so a port that is idle for a while is not penalised |

**Debug mode adds** (all `[dbg] f<frame> AUDIO ...`):

| Line | When |
|---|---|
| `AUDIO open port 0 MAIN grain 256 48000 Hz stereo volume 1,00/1,00, expected 187,5 outputs/s` / `AUDIO release ...: <totals>` / `AUDIO config ...` | sceAudioOutOpenPort / OpenExtPort, ReleasePort, SetConfig |
| `AUDIO open NGS system 0x..: granularity 256, 48000 Hz, 64 voices` | sceNgsSystemInit (and `release` at sceNgsSystemRelease) |
| `AUDIO open Audiodec decoder N: AAC 2 ch` / `Atrac handle N: ATRAC9 2 ch 48000 Hz, ... samples, loop ..` / `AvPlayer <file>: audio 2 ch 48000 Hz, video WxH fps, duration` | decoder streams created / deleted (Audiodec, Atrac, HLE SceAvPlayer). Games that link their own AvPlayer show up through Audiodec and their audio port instead |
| `AUDIO snapshot: device ...`, one line per open port (with host queue and totals so far), one per NGS system / decoder stream | debug mode switched on mid-game (F9, `--debug-mode-at`): the open events already happened, so their state is listed once |
| `AUDIO port 0 MAIN: R outputs/s (expected E), gap avg A ms max M ms (grain G ms), host queue Q samples, underruns u, silent s/N over T s` | every 10 s per open port while debug mode is on (stops when it is turned off) |
| `AUDIO underrun burst #n on <port> ended: k underruns over T ms` | a burst is over (no underrun for 1 s; checked with the periodic line) |

Reading them: `outputs/s` below `expected` with underruns = the guest produces too slowly (emulated CPU, a starved
audio thread, or host blocking); `expected` reached but underruns anyway = host buffering too small for the device
update; large `gap max` with no underruns = the queue absorbed a stall; a high `silent` share is normal in menus and
loading screens (CRI/NGS keep outputting zeros).

**F10 capture:** `audio.txt` = device and buffering, every open port (host queue, time since the last output, totals),
the ports released earlier in the session, NGS systems and decoder streams.

**Tests:** `--audio` uses the OpenAL device in headless runs. For a silent run that records the mixed output, set
`ALSOFT_CONF` to a file with `[general]` `drivers=wave`, `sample-type=int16` and `[wave]` `file=<path>.wav` (OpenAL
Soft's wave writer runs in real time, like a device). `--audio-dump=<dir>` instead writes exactly what the game sent to each port, one
WAV per opened port (`NN_portP_TYPE_RATE.wav`, after port volume, no device timing), the input for content analysis
(clipping, clicks, pitch, comparison with a reference decode). `--debug-mode-at=FRAME[,FRAME...]` toggles debug mode like F9
when the guest frame is reached (`[dbg] debug mode on (--debug-mode-at) at guest frame N`).

## Cost

Measured on Killzone, the kz_skin route (4500 frames, paced, GTX 1650, 2026-10-22):

| Build / mode | fps | process cpu ms/frame | GPU worker ms/frame | steady alloc KB/frame | shader cache |
|---|---|---|---|---|---|
| main 429721a | 28.7 | 31.4 | 4.83 | 482 | pack + 509 prewarmed pipelines, 0 compiled on the GPU thread |
| this branch, debug off (2 runs) | 28.9 / 28.8 | 28.5 / 30.7 | 4.10 / 4.67 | 486 / 485 | same |
| this branch, debug on | 28.8 | 31.6 | 5.02 | 490 | same |

Run-to-run noise is about 2-3 ms/frame of process cpu, so debug off is not measurable and debug on costs about 3%
(one more ms of process cpu, ~0.4 ms on the GPU worker). A debug-mode log of that route is ~340 KB for 2.5 minutes.
A capture stalls the game while it writes: 4.2 s for a 298-draw gameplay frame (209 textures and 15 targets as PNG;
the zip was 68 MB), 0.3 s for a menu frame.

- Debug mode off: per present a few counter reads and field writes (frame summary ring) and one null check for a
  pending capture; per HLE call one static flag read; per GPU command two field writes; the watchdog wakes once per
  second. The shader/pipeline cache is not touched (only `--trace-draws` still bypasses it, as before: it is chosen at
  launch and needs freshly generated GLSL for every shader).
- Debug mode on: per present a 960x544 -> 480x272 blit and a 510 KB copy into the replay ring (GPU, no wait) and, when
  the frame retires, an ~8K-sample scan plus the anomaly rules on the GPU worker; 64 MB of host memory for the ring; a
  16-entry HLE call ring per thread; the rate-limited lines above. Measured cost: see "Replay ring cost" below.

### Replay ring cost

Killzone kz_skin route (4500 frames, paced, `--shader-cache` in `args=`, GTX 1650, 2026-10-23), branch
`feat/diag-replay` against main 00ffb73. Other agents were running emulators on the same machine during these
runs, so the GPU time per frame in particular swings by 2-3 ms between identical runs; each line is one run.

| Build / mode | fps | process cpu ms/frame | GPU worker ms/frame | main thread ms/frame | GPU ms/frame |
|---|---|---|---|---|---|
| main, debug off | 28.4 / 28.4 | 33.6 / 33.8 | 4.59 / 5.06 | 13.2 / 12.7 | 3.07 / 5.25 |
| this branch, debug off | 27.4 / 28.5 | 31.9 / 33.4 | 5.01 / 4.96 | 11.2 / 12.6 | 4.35 / 5.34 |
| main, debug on (check every 30 presents) | 28.4 / 26.2 | 34.7 / 35.7 | 5.39 / 5.17 | 12.5 / 12.2 | 4.89 / 5.92 |
| this branch, debug on (final, 4x4 sampling) | 28.8 | 29.5 | 4.11 | 12.6 | 4.20 |
| this branch, debug on (first version, 2x2 sampling) | 28.2 / 27.6 / 28.7 | 35.1 / 34.7 / 31.9 | 5.39 / 5.47 / 4.93 | 12.3 / 11.9 / 12.5 | 6.55 / 6.06 / 3.02 |

The differences are inside the run-to-run noise, so the replay's own cost is measured directly instead: the
`PERF replay` line of the report (debug mode only) gives **15 us/frame** to record the blit + copy and **55-63
us/frame** for the metrics and rules when the frame retires (~190 us with 2x2 sampling, hence 4x4), i.e. ~0.07 ms of
GPU-worker time per frame (~1.5% of its ~4.6 ms). The GPU work is one 960x544 -> 480x272 linear blit and a 510 KB
image-to-buffer copy per present. Memory: 64 MB of host-visible system memory and a 510 KB device-local image while
debug mode is on, nothing while it is off. Debug off runs exactly the code of before plus one null check per present
(`_replay != null`, to free the ring after F9). A capture adds ~64 MB of temporary managed copies and about 1 s of
background PNG writing (127 frames + 5 sheets, ~40 MB; the kz_replay auto capture zip was 45 MB in all); kz_skin with debug on gave no false anomaly over
the whole route (only the expected `black` on the loading screen).

## Implementation map

- `src/VitaSharp.Renderer.Vulkan/VulkanRenderer.Diagnostics.cs`: capture arming/completion, frame ring, describe/state,
  device lost, debug first-use lines.
- `src/VitaSharp.Renderer.Vulkan/VulkanRenderer.Replay.cs`: replay ring (per-present blit + copy, per-frame metrics at
  retire, snapshot + replay/ writer), `VITASHARP_DEBUG_INJECT`.
- `src/VitaSharp.Renderer.Vulkan/VulkanRenderer.NanCheck.cs`: NaN/Inf scan of float targets (compute shader at scene end,
  results at retire, per-target history, `nan` anomaly, nan.txt) and the `VITASHARP_DEBUG_NAN=draws` draw table;
  the instrumentation itself is `ShaderGenerator.EmitNanDrawCheck`.
- `src/VitaSharp.Core/Util/FrameAnomalyDetector.cs`: `FrameMetrics` (colour metrics) and the per-frame anomaly rules
  (pure logic, unit-tested in `tests/VitaSharp.Tests/FrameAnomalyDetectorTests.cs`).
- `VulkanRenderer.cs` Present: `_traceFrame`/`_traceDumpFrame`/`_traceDir` replace the launch-time `TraceDrawsFrame`
  checks; `--trace-draws=N` sets both frames to N (its old behaviour, including the cache bypass and the skipped present).
- `src/VitaSharp.Desktop/Diagnostics.cs`: F10 / `--diag-at` / anomaly captures, info.txt, log copy, zip.
- `src/VitaSharp.Desktop/Watchdog.cs`: watchdog, periodic PERF and summaries, session log rotation (`LogFiles`).
- `src/VitaSharp.Core/Util/AudioDiagnostics.cs`: audio port/stream statistics, debug lines, summary, audio.txt; fed by `Hle/Modules/Audio.cs` (ports), Ngs, Audiodec, Atrac, AvPlayer (streams) and `Desktop/Host/OpenAlAudio.cs` (device description, underruns).
- `src/VitaSharp.Core/Util/DebugMode.cs`, `Log.cs` (dedupe, size cap, FilePath), `GuestThread.cs` (HLE ring/errors),
  `GpuQueue.Executing`.
