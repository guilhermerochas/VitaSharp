# GPU dump: record the GPU work of a few seconds, replay it offline

Branch `feat/gpu-dump`. Like PCSX2's GS dumps: a user records the GPU work of the last seconds of play, sends the
file, and the frame is reproduced draw by draw on a developer machine without the game, its CPU emulation or its
data. The replay runs the same Vulkan renderer, so renderer options (MSAA mode, shader debug overrides...) can be
changed to bisect a glitch.

## Recording

| How | What |
|---|---|
| **F8** in the game window | starts a recording at the next present; F8 again stops it at the next present (it also stops by itself after 1800 frames, ~60 s at 30 fps). Works with or without debug mode |
| `--gpu-dump-at=F1:F2[:file]` (`run`, `test`, scenario `args=`) or `VITASHARP_GPU_DUMP_AT=F1:F2[:file]` | records frames F1..F2 exactly (frame N = the frame ending with renderer present N, the number in `frame_N.png`) |

Files go to `<DataRoot>/gpudumps/<TITLEID>_<yyyyMMdd_HHmmss>.vsgd` unless a file is given. The log gets
`[gpudump] armed ...`, `[gpudump] exported N render targets`, `[gpudump] recording to ...` and, once the file is
complete, `[gpudump] saved <file>: <frames, sizes, cost>`. An F10 capture's `info.txt` says whether a GPU dump is
being recorded.

### Para o usuário (texto das mensagens)

> **Gravar um dump da GPU** (quando aparecer um defeito gráfico que dá para reproduzir):
> 1. Chegue perto do ponto onde o defeito aparece.
> 2. Aperte **F8**. A barra de título mostra *"Gravando dump da GPU (F8 para parar)"*.
> 3. Deixe o defeito aparecer na tela (alguns segundos bastam; o jogo pode ficar um pouco mais lento).
> 4. Aperte **F8** de novo. A barra de título mostra *"Dump da GPU salvo: <arquivo> (N quadros, M MB)"*.
> 5. Envie o arquivo `.vsgd` da pasta `gpudumps` (dentro da pasta de dados do VitaSharp).
>
> Outras mensagens: *"Salvando dump da GPU..."* (depois do segundo F8), *"Falha ao gravar o dump da GPU: ..."*,
> *"Já existe uma gravação do dump da GPU em andamento"*. Com o modo de depuração ligado (F9), a mensagem lembra:
> *"Modo de depuração ligado (F9 desliga, F10 captura, F8 grava dump da GPU)"*.

Why an explicit start instead of an always-on "last N seconds" ring in debug mode: Killzone gameplay produces
~0.7 MB of compressed dump per frame and the recorder needs ~30 ms of CPU per frame (mostly compression on
background threads) plus a shadow copy of every page the GPU read (~60 MB). That is fine for a few seconds of
recording but too much to keep running for a whole debug session.

## Replaying

```
VitaSharp.Desktop replay-gpu <dump.vsgd> [options]
```

| Option | |
|---|---|
| `--out=<dir>` | output folder (default `<dump>_replay` next to the dump): `frame_N.png`, `sheet*.png`, `metrics.csv`, `log.txt` |
| `--frame=N` | stop after frame N |
| `--dump-every=K --dump-from=F` | which frames become PNGs (default: every frame) |
| `--trace-draws=N` | full capture of frame N into `<out>/trace_N/` with the F10 writer: `draws.txt`, `shaders/`, `targets/` (+ `targets.txt`), `textures/`, `screenshot.png`. Needs frame N-1 in the dump |
| `--draw-limit=N[@F]` | execute only the first N draws of frame F (default `--frame`); the rest of that frame's draws are skipped. Combine with `--trace-draws=F` to see the targets at that point (the presented image keeps older content where the skipped passes would have drawn) |
| `--msaa=native\|ss\|off` | `VITASHARP_MSAA` |
| `--debug-fs=<hash>:<expr>`, `--debug-tex0=<mode>` | `VITASHARP_DEBUG_FS`, `VITASHARP_DEBUG_TEX0` |
| `--env=KEY=VALUE` | any other environment switch (repeatable) |
| `--no-recorded-env` | by default the `VITASHARP_*` variables of the recording session are set again (unless set in the replay's own environment or by the options above); this skips them |
| `--compare=<dir>` | compares the written frames with the same-named `frame_N.png` of another run: `compare.txt` (identical %, % off by more than 8, mean and max channel difference) and `diff_N.png` (difference x8) |
| `--window [--hold] [--speed=X \| --fast]` | shows the replay in a window at the recorded pace; `--hold` keeps it open at the end |
| `--shader-cache=<dir>` | shader cache (default `<DataRoot>/shadercache`; the title's programs pack is used) |
| `--async-shaders=on` | background shader compilation (default off in the replay) |
| `--no-idle` | do not reproduce the recorded idle points (earlier submission of work) |
| `--no-targets` | do not restore the render targets recorded at the start (to see what depends on them) |
| `--info` | header, record statistics, size per frame, guest memory read per MB of address space |
| `--recompress=<out> [--quality=0-11]` | rewrites the dump with another Brotli quality (default 9: ~7% smaller than the recording, slow) |

`png-diff <dirA> <dirB> [pattern]` compares same-named PNGs of two folders (two traces' `targets/`, for example)
the same way.

Bisecting a glitch: replay with `--compare` against the original frames; then `--msaa=off`/`ss`, a debug override,
or `--draw-limit` with `--trace-draws` until the glitch disappears.

## What a dump contains

The recorder sits on the GPU worker: it records the commands in the order the backend executes them (the same order
the guest submitted them), and the guest memory the backend actually read.

- **Commands:** BeginScene (surfaces, MSAA mode, depth surface), Draw (programs, full fixed-function state, the
  uniform data copied at submission, uniform buffer addresses, texture descriptors, streams, indices, visibility),
  EndScene (notification addresses), MidSceneFlush, Finish, Signal, Callback (only its written range: GPU transfers
  run on the CPU, their result reaches the dump through later reads), Present (display frame plus host time,
  guest frame and renderer present number), and the points where the queue ran empty (Idle).
- **Programs:** the GXP bytes of each program and the patched vertex/fragment program parameters, once per dump.
- **Guest memory:** `GuestMemory.ReadSpan` calls the recorder while a recording runs (one flag check otherwise) and
  only reads made on the GPU worker count. The recorder keeps a shadow copy of every page the backend has read. The
  first read of a page stores the whole page; later reads compare the page with the shadow and store only the bytes
  that changed. A page whose DirtyPages byte has stayed clear since it was last compared (same sweep epoch) is not
  compared again. Updates are stored before the command that read them; the replay writes them into an empty
  guest address space (marking the pages written, so its texture cache sees them) before executing that command.
  At the start every page is marked dirty, so the renderer re-reads (and the dump gets) the textures it had cached.
- **Render targets:** they live only on the GPU, so at the start the renderer reads back every colour target
  (`IGpuBackend.ExportDumpState`, `VulkanRenderer.GpuDump.cs`) and the replay recreates them with their contents
  before the first command. Without this, Killzone's menu text (glyphs rendered once into a target) was missing.
- **Header:** title, build and commit, OS, CPU backend, GPU, renderer description (driver, MSAA mode, shader
  settings, cache), debug mode, command line, every `VITASHARP_*` variable, first present.

## File format (`.vsgd`, version 1)

```
file    = "VSGPUDMP" u32 version u32 headerBytes header chunk*
header  = UTF-8 "key: value\n" lines
chunk   = u8 type u32 rawBytes u32 storedBytes u8 codec payload     codec 0 = stored, 1 = Brotli
          type 1 = records (one chunk per frame, or 32 MB), type 2 = end summary ("key: value" lines)
record  = u8 kind ...                                                records never span chunks
```

Integers are little endian, `var` is unsigned LEB128. Record kinds (`GpuDumpRecord`): 1 Memory (u32 address, var
length, bytes), 2 ProgramInfo, 3 VertexProgram, 4 FragmentProgram, 5 BackendState (var length, opaque renderer
blob), 0x10 BeginScene, 0x11 Draw, 0x12 EndScene, 0x13 Finish, 0x14 MidSceneFlush, 0x15 Present, 0x16 Signal,
0x17 Callback, 0x20 Idle. Draw uniform blocks are stored inline, as a reference to an identical block among the last
4096, or as a delta (changed 8-byte-word runs) against the previous block of the same program and stage.
A dump cut short (crash) is readable up to its last complete chunk; the reader skips unknown chunk types. Chunks
are compressed with Brotli quality 3 on up to 4 background threads (quality 5 compressed at ~30 MB/s and stalled
the GPU worker). Code: `src/VitaSharp.Core/Gpu/GpuDumpFormat.cs` (codec), `GpuDumpRecorder.cs`, `GpuDumpReader.cs`,
`src/VitaSharp.Desktop/GpuDumpControl.cs` (F8, `--gpu-dump-at`, header), `ReplayGpu.cs`; tests in
`tests/VitaSharp.Tests/GpuDumpTests.cs`.

## Verification (GTX 1650 laptop, 2026-10-23)

| Dump | Frames | Replay vs the recording run's own frames |
|---|---|---|
| Killzone menu (kz_skin route, frames 600-650) | 51 | 51/51 pixel-identical |
| Killzone gameplay (kz_skin, frames 4150-4449, walking) | 300 (60 compared) | 55/60 pixel-identical; the others differ in 0.01-0.8% of pixels (mean difference <= 0.03/255) |
| Uncharted tutorial overlay (uc_walk, 6700-6849) | 150 (30 compared) | 30/30 pixel-identical |
| Uncharted gameplay (uc_walk, 7100-7249, walking) | 150 (30 compared) | 30/30 pixel-identical |

The Killzone differences are not a dump problem: replaying the same dump twice gives the same small differences
between the two replays, and only with native MSAA's programmable blending through input attachments. With
`--msaa=off`, `--msaa=ss` or `VITASHARP_MSAA_FETCH=copy` two replays are pixel-identical. The first target that
differs is the 4x MSAA 384x192 scene at 0x66332000 (values off by 1), which then propagates. Reading the colour
attachment as an input attachment while overlapping fragments of the same draw write it is a feedback loop that
Vulkan leaves undefined without a rasterization-order guarantee. This is a renderer issue worth a separate look
(it may be related to rare one-frame colour glitches).

## Size and cost

| Dump | On disk | First frame (target contents + every texture used) | Then |
|---|---|---|---|
| Killzone gameplay, 300 frames | 237 MB (220 MB after `--recompress`) | 37 MB | 0.67 MB/frame = ~19-20 MB per second of play |
| Uncharted gameplay, 150 frames | 31 MB | 27.5 MB | 0.02 MB/frame |
| Killzone menu, 51 frames | 3 MB | 2.8 MB | 0.01 MB/frame |

Killzone's size is mostly per-draw vertex uniform data (~10 KB per skinned draw, changing every frame: 1.25 GB raw
for 300 frames, 618 MB after references and deltas, then Brotli) and ~1 MB/frame of rewritten vertex/uniform
memory.

Cost while recording (Killzone kz_skin, frames 4150-4449, paced, GTX 1650; the same route without recording runs at
29.8-30.0 fps there):

- 300 frames recorded in 10.6 s = **28.3 fps** (5-s FPS lines 28.1 / 30.0 / 27.9).
- GPU worker: 6.8 ms/frame of recorder work (3.1 ms of it comparing the 5.4 MB/frame the renderer reads).
- Compression: 22 ms/frame of CPU on background threads; the worker waited 0.02 ms/frame for them.
- Process CPU over the whole run: 149.2 s against 140.6 s without recording, all of it in those 10.6 s (~0.8 of a core).
- Start: one synchronous readback of every render target (15 targets, 21 MB in Killzone), and the first frame
  re-hashes every texture it uses; the FPS line covering the start was 28.1.
- Memory: 66 MB of shadow pages plus up to 16 chunks waiting for compression.
- Uncharted gameplay: 1.5 ms/frame on the worker, no fps change.

Off: no recorder object; `GpuQueue` reads one static field per command and `ReadSpan` checks one static flag.

## Limitations

- Depth/stencil surface contents and multisampled images are not carried from the recording start: a frame that
  reads depth written in an earlier frame will differ in the first replayed frame. Games tested clear or re-render
  them every frame.
- GPU results consumed by the game's CPU code (auto exposure reading back luminance, visibility results, notifications)
  are not fed back: the replay runs the commands exactly as they were submitted, which is the point.
- The replay re-executes on the developer's GPU/driver and current build; the dump records the recording machine's
  GPU and settings in its header. A different GPU may give different results with the same commands.
- Memory written by the CPU concurrently with a GPU read can race: the dump holds what the recorder saw, which may
  differ from what the renderer copied a few microseconds later (not observed in the tests).
- F8 recordings stop by themselves after 1800 frames; large dumps (Killzone: ~20 MB/s) need disk space.
- The format version must be bumped when records change; readers reject newer versions.
