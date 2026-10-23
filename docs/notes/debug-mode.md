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

Folder contents:

| File | Content |
|---|---|
| `info.txt` | reason, title, guest frame, build (version+commit), runtime/OS, command line, CPU backend, debug mode, window size, GPU/driver/Vulkan version, MSAA mode, shader settings, shader cache state, GPU queue counters, every `VITASHARP_*` environment variable |
| `screenshot.png` | the presented frame at the end of the traced frame |
| `draws.txt` | every `[scene] begin` (render target, GXM MSAA mode, downscale, depth surface) and `[draw]` of the frame, in order |
| `shaders/` | GLSL of every program drawn, `<gxp hash>_<hint key>.vert/.frag` (shaders loaded from the shader pack are re-translated for this, nothing is recompiled) |
| `targets/` | every render target as `rt_<addr>_<w>x<h>.png` (replaced targets with `_<fmt>`), plus `targets.txt` |
| `textures/` | every texture sampled in the frame as PNG plus `textures.txt` |
| `frames.txt` | the last 120 presented frames (rolling summary, below) |
| `renderer.txt` | GPU/driver/settings, renderer state (frame slots, writebacks), texture cache and GPU time statistics so far |
| `state.txt` | guest threads (status, wait object, pc/lr/backtrace; last 16 HLE calls each in debug mode), held mutexes with waiters, GPU queue (submitted/completed, command executing), last 30 frames |
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
| `[frame-metrics] p.. mean white black magenta green blue` | every 10th anomaly check (~every 300 presents) |
| `[anomaly] p.. f.. <kind>: <details>` / `... cleared after n checks` | see below |

Warnings are deduplicated by their text with numbers masked (`#`): the first three of a kind are written, then the
10th, 100th, ... with `(repeated n times)`.

Texture inspection (the test harness's TEXTURE SUSPECT check: noise ~0.33 = random data, seams > 1.8 = broken block
decoding) runs on at most 2 texture uploads per frame.

### Anomaly checks and automatic captures

Every 30 presents the presented image is copied to the CPU with the frame's own command buffer (no stall) and checked
on every 2nd pixel of every 2nd row when the frame retires:

| Kind | Rule | Confirmed after | Auto capture |
|---|---|---|---|
| `magenta` | > 5% pixels r>200, b>200, g<80 (missing/garbage textures) | 2 checks in a row | yes |
| `saturated` | > 8% pure green (g>180, r,b<60) or pure blue (b>180, r,g<60) pixels (garbage target/effect) | 2 | yes |
| `blown-out` | > 35% saturated white, or mean brightness > 0.85 | 2 | yes |
| `flicker` | mean brightness changes by > 0.35 between two checks (30 presents apart), neither black | 1 | yes |
| `black` | > 97% black | 1 | no (loading screens) |
| `frozen` | identical image for 20 checks (~600 presents) | 1 | no (pause menus) |

A confirmed anomaly with "auto capture" triggers the same capture as F10 (`[diag] capture requested (auto: <kind> -
<details>)`), at most one every 5 minutes and 3 per session (`[diag] anomaly ...: skipped` otherwise).

Test switch: `VITASHARP_DEBUG_INJECT=magenta@<present>` (or `green@<present>`) makes the check see that colour over
half the image for 4 checks from that present on (`[anomaly] test injection ...`); the rendered frame is untouched.

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
- Debug mode on: one 2 MB image copy and a ~65K-pixel scan every 30 presents, a 16-entry HLE call ring per thread, the
  rate-limited lines above.

## Implementation map

- `src/VitaSharp.Renderer.Vulkan/VulkanRenderer.Diagnostics.cs`: capture arming/completion, frame ring, describe/state,
  device lost, debug first-use lines, anomaly sampler.
- `VulkanRenderer.cs` Present: `_traceFrame`/`_traceDumpFrame`/`_traceDir` replace the launch-time `TraceDrawsFrame`
  checks; `--trace-draws=N` sets both frames to N (its old behaviour, including the cache bypass and the skipped present).
- `src/VitaSharp.Desktop/Diagnostics.cs`: F10 / `--diag-at` / anomaly captures, info.txt, log copy, zip.
- `src/VitaSharp.Desktop/Watchdog.cs`: watchdog, periodic PERF and summaries, session log rotation (`LogFiles`).
- `src/VitaSharp.Core/Util/DebugMode.cs`, `Log.cs` (dedupe, size cap, FilePath), `GuestThread.cs` (HLE ring/errors),
  `GpuQueue.Executing`.
