# Gravar tudo: one recording that is enough to fix a bug offline

Branch `feat/capture-all`. A player records a problem once, while playing, and sends one zip. A developer then
diagnoses it without playing the game again: what reached the screen and when (presentation flicker, stutter,
repeated or out-of-order frames, black screens), what the GPU drew (the GPU dump), what the guest read (the input
log), and how the game logic got there (a save state plus the input, re-run headless).

Code: `src/VitaSharp.Core/Capture/` (shared by the desktop and Android), the renderer's present tap
(`VulkanRendererOptions.PresentTap`, `VulkanRenderer.Replay.cs`), `src/VitaSharp.Desktop/SessionCaptureControl.cs`
(Ctrl+F8, `--capture-*`, `rerun`), the Android pause menu (`GameActivity.cs`, `EmulationSession.cs`), the analyzer
`vitasharp-perf capture` (`tools/VitaSharp.PerfTools/Commands/CaptureCommand.cs`, `Core/Capture/CaptureAnalyzer.cs`).
Tests: `tests/VitaSharp.Tests/SessionCaptureTests.cs`.

## Para o usuário

> **Gravar tudo** (quando aparecer qualquer problema: imagem piscando, tela preta, travada, câmera parada, som
> estranho):
> - **PC:** aperte **Ctrl+F8** durante o jogo. A barra de título mostra *"● REC | Gravando tudo (Ctrl+F8 para
>   parar)"*. Jogue até o problema aparecer e aperte **Ctrl+F8** de novo. A barra mostra *"Salvando a gravação..."* e
>   depois *"Gravação salva: <arquivo>.zip"*.
> - **Android:** no menu de pausa (botão PS), escolha **Gravar tudo**. O contador de FPS mostra *"● REC"*. Para
>   parar, abra o menu de novo e escolha **Parar gravação**.
> - **Pré-gravação** (para defeitos rápidos): com ela ligada, a gravação também inclui os segundos *antes* de você
>   apertar. No PC: Configurações > Geral > *"Pré-gravação"*, ou ligue o modo de depuração (F9). No Android: menu de
>   pausa > *"Pré-gravação: ligada"*. Ela guarda um estado salvo a cada 30 s, o que pausa o jogo por um instante.
> - Envie o arquivo `.zip` da pasta `captures` (dentro da pasta de dados do VitaSharp; no Android,
>   `Android/data/io.github.vitasharp/files/captures`).
>
> Outras mensagens: *"Aguarde: a gravação anterior ainda está sendo salva"*, *"Falha ao iniciar a gravação: ..."*,
> *"Falha ao salvar a gravação: ..."*. F8 (dump da GPU), F9 (modo de depuração) e F10 (diagnóstico) continuam iguais.

## What a capture holds

Folder `<data>/captures/<TITLEID>_<yyyyMMdd_HHmmss>_tudo/` and its zip:

| File | Content |
|---|---|
| `session.txt` | title, app path, platform, run options (reused by the re-run), start/stop (time, guest frame, guest clock), the states, the GPU dump result, frame counts and sizes, process CPU while recording, build, CPU and renderer description |
| `report.txt`, `findings/*.png` | the analyzer's report, and a strip of the 5 frames around each finding |
| `frames.vsfr` | every presented frame as shown (downscaled to fit 480x272 on the GPU), with the renderer present number, the guest flip number, the vblank count, the guest clock and the host times of the flip and of the present |
| `timeline.tsv` | one line per presented frame: those numbers plus frame interval, GPU ms, draws, scenes, new shaders/pipelines, the biggest stall cause (FrameLedger) and its ms, framebuffer address and size, pre-roll flag |
| `input.tsv` | every change of pad and touch the guest read, every motion sample, every `sceKernelGetRandomNumber` result, the emulator hotkeys and the capture's marks, with host ns, guest µs and guest flip |
| `state_start.vss` | save state taken at the start of the recording |
| `state_preroll.vss` | save state at the start of the pre-roll (when armed) |
| `gpu.vsgd` | GPU dump from the start (the F8 format; `replay-gpu`); up to 5400 frames on the desktop, 1800 on Android |
| `audio/*.wav`, `audio/files.txt`, `audio_mix.wav` | every guest audio port and every NGS source voice as a WAV (`AudioDump`), the time each opened, and their mix at 48 kHz as the device would play it |
| `threads_start.txt`, `threads_stop.txt` | guest threads (wait state, backtrace, recent HLE calls in debug mode), held mutexes, GPU queue |
| `hle_errors.txt` | HLE error returns and unimplemented calls by count (counted while recording, as debug mode does) |
| `log.txt` | the session log up to the stop |

### Pre-roll

Armed by `--capture-preroll[=SECONDS]` (launcher "Pré-gravação"), `VITASHARP_CAPTURE_PREROLL=SECONDS`, debug mode
on the desktop (F9 or `--debug-mode`, 10 s), or the Android pause menu. While armed, the presented frames, their
timing and the input go into memory rings, and a save state is taken every 30 s (`SessionCaptureOptions.
PreRollStateInterval`; at least the pre-roll time) into `<data>/captures/.preroll/` (the last two are kept). The
rings keep everything since the older of the two states, and at least the pre-roll time, within a memory limit
(192 MB of encoded frames on the desktop, 64 MB on Android). A recording starts with the ring (frames flagged
pre-roll) and copies the older state as `state_preroll.vss`, so a glitch seen before Ctrl+F8 is in the capture and
can be re-run.

### Frames file

`frames.vsfr` (`FramesFile` in `CaptureFormat.cs`): `"VSFRAMES" u32 version`, then one record per present (u32
length, the `PresentInfo` fields, flags, stall cause, picture). A key picture (plain QOI image) every 30 pictures and
after any gap; the others are `Brotli(QOI(picture - previous picture))`. Measured on FIFA 13 menus (480x272):
138 KB per QOI picture, 23 KB per QOI delta, 19 KB per frame on average with Brotli on the deltas. The encoder runs
on its own thread (~1 ms per frame); when it falls behind (`EncoderQueueFrames`, 120 on the desktop, 48 on Android)
the frame keeps its timing and loses its picture (flag 2).

## Sizes and cost

Measured on the desktop (GTX 1650 laptop, 4 logical CPUs given to the emulator, 05/11/2026):

| | |
|---|---|
| Presented frames | ~1.1 MB/s at 60 fps on FIFA 13 menus (~70 MB/min); moving gameplay more |
| GPU dump | the F8 numbers: FIFA 13 menus 3 MB/s, Killzone gameplay ~20 MB/s (docs/notes/gpu-dump.md) |
| States | 30-50 MB each (start, and pre-roll) |
| Audio | 11.5 MB/min per stereo 48 kHz port or voice WAV |
| Input, timeline, text | < 1 MB/min |
| CPU while recording | the GPU dump's (~0.2-7 ms/frame on the GPU worker + compression on other threads), the frame encoder (~1 ms/frame on its own thread), the per-frame downscale copy on the GPU (the debug-mode replay ring's), a compare per input poll |
| Start | one save state: the game stops 0.1-0.35 s |
| Pre-roll armed | the same frame encoding, the input rings, and a save state every 30 s (0.1-0.35 s stop each) |
| Off | nothing: no tap, no input wrapper, no thread; the HLE dispatch reads one static flag (`DebugMode.CollectHleErrors`) and the RNG one static field |

## Re-run

```
VitaSharp.Desktop rerun <capture folder> [--from=preroll|start] [--window] [--rerun-gpu-dump] [run options]
```

Boots the capture's game with the capture's run options (Jak 1's `--cpu-mhz=0` comes along), loads
`state_preroll.vss` (default when present) or `state_start.vss` after 120 frames, and replaces the host input with
`InputPlayback`: from the load on, the guest reads each recorded pad, touch and motion change at the same guest
clock time it read it while recording (a save state restores the guest clock exactly), and
`sceKernelGetRandomNumber` returns the recorded numbers in order. The re-run records itself as a capture into
`<capture>/reruns/` (no state, no GPU dump unless `--rerun-gpu-dump`), analyzes it, and pairs its frames with the
original's by guest time (`compare.txt`: which pictures match, the first that differs). It ends 2 s after the last
recorded event. Headless unless `--window`.

Not deterministic: guest threads run on host threads, so their interleaving, and with it every guest clock read,
differs from the recording. The guest clock reads themselves are not recorded (thousands per frame). A re-run of a
FIFA 13 menu capture from its pre-roll state matched 95% of the original's pictures (median difference 0) and showed
the same findings.

## Analyzer

```
vitasharp-perf capture <folder|zip> [--threshold 6] [--frames <dir>] [--rerun] [--compare <original>]
```

It also runs at the end of every recording (`report.txt` is in the zip). Findings:

| Kind | Rule |
|---|---|
| `black-flash` | 1-3 black frames (luma < 10, > 97% dark) between normal pictures |
| `black-transition` (info) | black under 1 s between pictures (a fade or a loading cut) |
| `black-screen` | black for 1 s or more, or from the first / to the last captured frame |
| `flash` | a frame that differs from both neighbours while they match each other, on the whole picture or one cell of a 4x4 grid (`FlickerDetector`, the rule of `vitasharp-perf flicker`) |
| `out-of-order` | a guest flip presented after a later one |
| `re-presented` | the same guest flip presented twice |
| `flips-skipped` | guest flips that never reached the screen between two consecutive presents |
| `picture-order` | swapping two consecutive pictures makes the sequence much smoother (decode or buffer order) |
| `frozen-picture` | the same picture for 1.5 s or more while the guest flips and reads input changes (`static-picture`, info, without input) |
| `stall` | a present interval above 3x the median and 50 ms over it, with the frame's stall cause, GPU ms, draws, new shaders/pipelines, and an emulator action (save state, hotkey) that happened then |

Each finding says where it is in the GPU dump (`replay-gpu --frame=K`), or that it happened in the pre-roll, before
the dump (re-run from `state_preroll.vss`), and the input the guest read in the 2 s before it. Audio: per file
level, clipping, silent gaps inside sound, clicks. Exit code 1 when a finding that is not (info) is reported.
