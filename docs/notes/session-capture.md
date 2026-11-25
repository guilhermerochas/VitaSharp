# Gravar tudo: one recording that is enough to fix a bug offline

Branch `feat/capture-all`. A player records a problem once, while playing, and sends one zip. A developer then
diagnoses it without playing the game again: what reached the screen and when (presentation flicker, stutter,
repeated or out-of-order frames, black screens), what the GPU drew (the GPU dump), what the guest read (the input
log), and how the game logic got there (a save state plus the input, re-run headless).

Code: `src/VitaSharp.Core/Capture/` (shared by the desktop and Android), the renderer's present tap
(`VulkanRendererOptions.PresentTap`, `VulkanRenderer.Replay.cs`), `src/VitaSharp.Desktop/SessionCaptureControl.cs`
(F8, `--capture-*`, `rerun`), the Android pause menu (`GameActivity.cs`, `EmulationSession.cs`), the analyzer
`vitasharp-perf capture` (`tools/VitaSharp.PerfTools/Commands/CaptureCommand.cs`, `Core/Capture/CaptureAnalyzer.cs`).
Tests: `tests/VitaSharp.Tests/SessionCaptureTests.cs`, `SessionCapturePrivacyTests.cs`, `SaveStateBootFromStateTests.cs`,
`SessionCaptureRerunTests.cs`.

## Para o usuário

> **Gravar tudo** (quando aparecer qualquer problema: imagem piscando, tela preta, travada, câmera parada, som
> estranho):
> - **PC:** aperte **F8** durante o jogo. No canto superior esquerdo do jogo aparece *"● REC 00:12"* (o tempo
>   gravado; também na barra de título). Jogue até o problema aparecer e aperte **F8** de novo. O canto mostra
>   *"Salvando a gravação: 42%"* e depois a barra mostra *"Gravação salva: <arquivo>.zip"*. A gravação para sozinha
>   depois de **60 s** (ou 1 GB de imagens), com o aviso *"A gravação chegou ao limite (60 s) e parou sozinha"*: grave
>   só o trecho do problema.
> - **Android:** no menu de pausa (botão PS), escolha **Gravar tudo**. O contador de FPS mostra *"● REC"*. Para
>   parar, abra o menu de novo e escolha **Parar gravação**.
> - **Pré-gravação** (para defeitos rápidos): com ela ligada, a gravação também inclui os segundos *antes* de você
>   apertar. No PC: Configurações > Geral > *"Pré-gravação"*, ou ligue o modo de depuração (F9). No Android: menu de
>   pausa > *"Pré-gravação: ligada"*. Ela guarda um estado salvo na memória a cada 5 s (no PC, só o que mudou desde
>   o anterior: o jogo para alguns milissegundos) ou 10 s (quando o estado para o jogo um quadro ou mais).
> - **Marcar o problema:** durante a gravação, aperte **Shift+F8** (no Android: menu de pausa > *"Marcar o problema"*)
>   no instante em que ele aparece: a gravação ganha uma marca e um estado salvo daquele momento.
> - Envie o arquivo `.zip` da pasta `captures` (dentro da pasta de dados do VitaSharp; no Android,
>   `.vitasharp/captures` na raiz do armazenamento, ou `Android/data/io.github.vitasharp/files/captures` com root ou
>   sem a permissão "Acesso a todos os arquivos").
>
> Outras mensagens: *"Aguarde: a gravação anterior ainda está sendo salva"*, *"Falha ao iniciar a gravação: ..."*,
> *"Falha ao salvar a gravação: ..."*. Ctrl+F8 grava só o dump da GPU; F9 (modo de depuração) e F10 (diagnóstico) continuam iguais.
>
> A gravação termina sempre, mesmo com o jogo travado. Fechar a janela (X, Alt+F4, *Stop* no launcher) ou sair do
> jogo no Android com uma gravação ligada ou ainda sendo salva não perde nada: a janela fica aberta com *"Salvando a
> gravação: NN%"* (no Android, um aviso na tela) e fecha sozinha quando o `.zip` está pronto (a análise
> `report.txt` é pulada nesse caso; `vitasharp-perf capture <pasta>` a faz depois). No
> launcher, um segundo clique em *"Forçar parada"* encerra o jogo na hora (aí a gravação se perde).

## Length limit, progress and F8 (08/11/2026)

A 6-minute recording of The Amazing Spider-Man (14,603 frames: 2 GB of pictures, a 500 MB GPU dump, a 2.9 GB zip)
took 135 s to save after its "saved" log line: the analysis (102 s: every picture decoded twice and the whole GPU dump
decompressed) and the zip (32 s, deflating the already Brotli-compressed pictures). The window was closed during it
and stayed open ~75 s with only "Salvando a gravação..." in its title. Now:

- **Limit:** a recording stops and saves by itself after `SessionCaptureOptions.MaxSeconds` (60 s; `--capture-max=S`
  or `VITASHARP_CAPTURE_MAX_SECONDS`, 0 = none) or `MaxBytes` of pictures (1 GB), with the `CaptureLimit` notice and
  a `note:` in `session.txt`. `--capture-at` and re-runs record exactly their range (`Start(limit: false)`).
- **Progress:** `SavePercent` / `SaveStep` (frames 0-10%, GPU dump, files 15-20%, analysis 20-60%, zip 60-100%); the
  desktop shows it in the top-left badge (renderer status overlay, never in the captured frames) and the title bar,
  and the badge shows *"● REC mm:ss"* while recording (fullscreen has no title bar).
- **Closing while saving** calls `Hurry()`: the analysis is skipped (or stopped where it is) and the close waits only
  for the files and the zip, printing the progress; the log says how long it waited. `frames.vsfr` is stored in the
  zip, not deflated.
- **F8:** every press is logged (`Window: F8 (Gravar tudo) pressed`, then `[capture] F8 pressed: stop accepted` or
  why it was refused). A stop pressed while the recording is still starting (its save state) is kept and done when the
  start completes instead of being dropped; keys held when the window loses the focus are released, so the next F8
  is not taken for key repeat. The window thread logs any stop of 1 s or more (`Window: the window thread did not run
  for N ms`) and its longest stop at the end of the run.

## Stopping never waits on the game

The save (`SessionCapture.StopCore`) depends on nothing of the guest: the GPU dump ends at the game's next present
if one comes within 2 s, else the GPU worker completes the file from the commands it already received (a note in
`session.txt`). Every file is its own step: one that fails is logged and becomes an `error:` line of `session.txt`
(written last, before the zip), and the rest is still written. A game hung before the recording (no present at all
while recording) still gives the state, both thread lists, `log.txt`, `hle_errors.txt`, `input.tsv`, the timeline,
the audio and the zip. Before 05/11/2026 the dump waited up to 180 s for a present while the session's end waited
120 s, so a hung game's recording stopped after `threads_stop.txt` (no `session.txt`, log or zip).

The start's save state pauses the game for a moment with an internal pause (`EmulationPause.Pause(..., internal:
true)`): the window shows *"salvando estado..."* instead of the *"Pausado"* badge, which is only for the player's own
pause, and the pause is always released, also when the state cannot be taken (a game hung outside a safe point).

## What a capture holds

Folder `<data>/captures/<TITLEID>_<yyyyMMdd_HHmmss>_tudo/` and its zip:

| File | Content |
|---|---|
| `session.txt` | title, the app's guest folder (`app-guest-path`), its version (`app-version`: the update's APP_VER and the application's own), category, content id, firmware, kernel plugins on/off, the `VITASHARP_*` variables set, every loaded module with its image hash, platform, run options (reused by the re-run), start/stop (time, guest frame, guest clock), the states, the GPU dump result, frame counts and sizes, process CPU while recording, build, CPU and renderer description |
| `report.txt`, `findings/*.png` | the analyzer's report, and a strip of the 5 frames around each finding |
| `frames.vsfr` | every presented frame as shown (downscaled to fit 480x272 on the GPU), with the renderer present number, the guest flip number, the vblank count, the guest clock and the host times of the flip and of the present |
| `timeline.tsv` | one line per presented frame: those numbers plus frame interval, GPU ms, draws, scenes, new shaders/pipelines, the biggest stall cause (FrameLedger) and its ms, framebuffer address and size, pre-roll flag |
| `input.tsv` | every change of pad and touch the guest read, every motion sample, every `sceKernelGetRandomNumber` result, the emulator hotkeys and the capture's marks, with host ns, guest µs and guest flip |
| `state_start.vss` | save state taken at the start of the recording |
| `state_preroll.vss` | save state at the start of the pre-roll (when armed): the newest of the pre-roll's in-memory states taken at or before the pre-roll's start |
| `state_mark_N.vss` | save state of the moment the player marked the problem (Shift+F8, Android pause menu "Marcar o problema"); session.txt `mark-state-N` |
| `gpu.vsgd` | GPU dump from the start (the Ctrl+F8 format; `replay-gpu`); up to 5400 frames on the desktop, 1800 on Android |
| `audio/*.wav`, `audio/files.txt`, `audio_mix.wav` | every guest audio port and every NGS source voice as a WAV (`AudioDump`), the time each opened, and their mix at 48 kHz as the device would play it; the microphone port only with `--capture-mic-audio` / `VITASHARP_CAPTURE_MIC_AUDIO=1` (by default `audio.txt` has only its format, source and pace) |
| `threads_start.txt`, `threads_stop.txt` | guest threads (wait state, backtrace, recent HLE calls in debug mode), held mutexes, GPU queue |
| `hle_errors.txt` | HLE error returns and unimplemented calls by count (counted while recording, as debug mode does), and the calls of `hle_calls.tsv` by function |
| `hle_calls.tsv` | every HLE call of the recording: guest µs, thread, function, library, r0-r3, result (`-`: still inside it at the stop), return address (`HleCallTrace`: per-thread chunks of 4096 calls, nothing allocated per call, at most 1,000,000 calls kept); `#state` lines say how many calls each thread had made when a save state was taken (the re-run, whose trace starts when its state is put back, is compared from there; a thread blocked in a call at the state makes it again in the re-run; every thread of the state has its line, also one with no call yet, and `in-call` when it was inside a call: one blocked since before the recording began makes in the re-run a call the capture's trace does not hold) |
| `files/files.tsv`, `files/blobs.bin` | what the guest touched on its title's own mounts (app0, savedata0, addcont0, a PFS or game data mount; `Vfs.IsTitleDevice`; never ux0, os0, vs0): every file opened or whose status was read (its size), every folder listed (its entries), every read from the start of the recording (or of the pre-roll) on (path, offset, length, SHA-256, guest µs) and the decrypted image of every module loaded from them; `blobs.bin` holds each distinct content once, up to `VITASHARP_CAPTURE_FILES_MB` / `--capture-files-mb` (default 0: no bytes, every read listed with its hash only; a capture goes to public reports), the rest is listed with its hash (`CaptureFileRecorder`); the save data mounts (`Vfs.IsSaveDataDevice`) only with their hashes (`hashdir`/`hashfile`/`hashread` lines, never put back by a re-run) unless `--capture-savedata` / `VITASHARP_CAPTURE_SAVEDATA=1`; `report.txt` notes when the capture holds the game's data |
| `log.txt` | the session log up to the stop |

### Pre-roll

Armed by `--capture-preroll[=SECONDS]` (launcher "Pré-gravação"), `VITASHARP_CAPTURE_PREROLL=SECONDS`, debug mode
on the desktop (F9 or `--debug-mode`, 10 s), or the Android pause menu. While armed, the presented frames, their
timing and the input go into memory rings, and a save state is taken into memory (`SaveStates.Save(emu, Stream)`:
nothing is written to disk while armed) every 5 s (`SessionCaptureOptions.PreRollStateInterval`) while the last three
stopped the game less than a frame (`PreRollStateStopBudgetMs`, 16 ms), else every 10 s (`PreRollSlowStateInterval`,
and until three are measured); `VITASHARP_CAPTURE_STATE_INTERVAL` sets both. On the desktop the states are
incremental (`IncrementalStates`, `SnapshotPages`, `GuestMemory.SnapshotPages.cs`): the capture sweeps the dirty-page
tracker (`DirtyPages`, every guest, HLE and GPU write marks its page; the sweep is exact with every guest thread
stopped) and reads only the pages written since the previous state, the others are the previous state's image, and
the memory sections are built after the game goes on; every 12th state is whole (`FullStateEvery`). Measured
(`SaveStateIncrementalTests`, synthetic title with 48 MB of non-zero memory, the game held): a whole state 18.5 ms,
the first incremental one 24 ms, the next ones 1.8-3.7 ms (260 pages read of 12,837); the image costs about the
non-zero guest memory in host memory, so Android takes whole states (every 10 s there). The states are kept from the newest one taken at or before the pre-roll's start on
(it covers the whole pre-roll; the older ones go), within `PreRollStateMaxBytes` (384 MB on the desktop, 128 MB on
Android; the oldest go first, the newest always stays): with the default 10 s pre-roll that is three or four
states. The rings keep everything since the oldest kept state, and at least the pre-roll time, within a memory
limit (192 MB of encoded frames on the desktop, 64 MB on Android). A recording starts with the ring (frames flagged
pre-roll) and writes the oldest kept state as `state_preroll.vss`, so a glitch seen before F8 is in the capture and
can be re-run from a state at most one interval before the pre-roll (before 25/11/2026: one every 30 s into
`<data>/captures/.preroll/`, so the state could be up to a minute before the glitch).

### Marking the problem

While recording, Shift+F8 (`WindowHost.BugMarked`; `--input` key `Shift+F8`) or the Android pause menu's "Marcar o
problema" calls `SessionCapture.MarkBug`: a `bug N <reason>` mark goes into `input.tsv` at once, and a save state of
that moment is taken on a thread of its own into `state_mark_N.vss` (the game stops 0.1-0.35 s, as for any state).
The stop waits for it; `session.txt` lists it as `mark-state-N: state_mark_N.vss guest-us ... flip ... ns ...`, the
report has a "marked by the player at +S s" line, and `rerun <capture> --from=markN` re-runs from it
(`CaptureStates`: the names, files and session keys of preroll, start and markN). Outside a recording the mark is
refused with a notice.

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
| GPU dump | the Ctrl+F8 numbers: FIFA 13 menus 3 MB/s, Killzone gameplay ~20 MB/s (docs/notes/gpu-dump.md) |
| States | 30-50 MB each (start, and pre-roll) |
| Audio | 11.5 MB/min per stereo 48 kHz port or voice WAV |
| Input, timeline, text | < 1 MB/min |
| Files read (`files/`) | the index (path, offset, length, hash per read); the distinct bytes only up to `--capture-files-mb` (default 0); a SHA-256 per `sceIoRead` (off: one static field read) |
| CPU while recording | the GPU dump's (~0.2-7 ms/frame on the GPU worker + compression on other threads), the frame encoder (~1 ms/frame on its own thread), the per-frame downscale copy on the GPU (the debug-mode replay ring's), a compare per input poll |
| Start | one save state: the game stops 0.1-0.35 s |
| Pre-roll armed | the same frame encoding, the input rings, and a save state into memory every 5 s (incremental, desktop: a few ms of stop) or 10 s (whole: 0.1-0.35 s stop each); 30-50 MB each, two to four held, plus the incremental image (~ the non-zero guest memory) |
| Off | nothing: no tap, no input wrapper, no thread; the HLE dispatch reads one static flag (`DebugMode.CollectHleErrors`) and the RNG one static field |

## Privacy

A capture is meant to be posted (a GitHub issue). After every file is written and before the zip, `CapturePrivacy`
rewrites every text file of the folder (`.txt`, `.tsv`, `.log`...), and the GPU dump's header is redacted when the
dump starts: the data folder becomes `<data>`, the home folder and the user name `<user>`, the computer name `<pc>`,
the online id (unless it is the default `VitaSharp`) and the `--psn-id` / `--rpcn-user` / `--rpcn-token` values
`<psn-id>`, MAC addresses `<mac>`, e-mail addresses `<email>`. Secrets in `VITASHARP_*` variables (`PASSWORD`,
`TOKEN`) are written as `<secret>`. `SessionCapturePrivacyTests` records a synthetic capture with a made-up person's
paths and names in the log, the options and the notes and scans every file of the folder and of the zip.

## Re-run

```
VitaSharp.Desktop rerun <capture folder> [--from=preroll|start] [--window] [--rerun-gpu-dump] [--boot=title] [run options]
```

Brings the capture's program up from the capture's own state (`--boot-state=<state>`, `SaveStates.BootFromState`):
every save state has a `boot` section (`StateBootImage`: title, executable, the app's guest folder, param.sfo, the
program's authority, and every module of the process and the kernel in load order with its guest path, segments and
module_info address). The modules' code and tables are in the state's memory, so after the memory is put back each
module is rebuilt from there (`ModuleLoader.FromMemory`: module_info, exports and imports read from guest memory and
registered with the linker, as a load from the file does), the firmware kernel modules' entries and stubs too
(`FirmwareKernelModules.AdoptFromState`). Nothing runs until `SaveStates.Load` brings the threads, objects and HLE
state. No file of the game, its update, the plugins or the firmware is read, so the re-run is the same whether they
are installed or not; app0 is the title's folder of the data folder given (`--data`), never a path recorded in the
capture (it may not exist). The files the recording read (`files/`) are put back first (`CapturedFiles.Materialize`
into `<capture>/reruns/.files/`: each file at its recorded size, sparse, with the recorded bytes where they were read)
and go over the title's mounts as a layer read before the device's own folder (`Vfs.AddReplayLayer`; a file of the
layer opened for writing is moved to the device's folder first), so the game reads the same bytes without being
installed; a module it loads from them is the recorded decrypted image (no licence leaves with the capture). A read the
recording did not make is logged with its path and counted ("files:" line of the re-run's summary), and so is a read
whose bytes the recording kept as a hash only (the default: the re-run reads zeros there). A received
capture is untrusted: a line whose path would leave `.files/` (a drive or root path, `..`, a device that is not a
plain name) or does not parse is skipped. States of several
processes (the shell with applications) are refused. `--boot=title` boots the installed game instead and loads the
state after 120 frames (states of earlier builds, without the boot section, need it).

With the capture's run options (Jak 1's `--cpu-mhz=0` comes along), from `state_preroll.vss` (default when present)
or `state_start.vss`, the re-run replaces the host input with
`InputPlayback`: from the load on, the guest's read that first saw each pad and touch change while recording sees
it again: the controller and touch drivers number the guest's reads of each port and panel (`IGuestReadInput`; the
count is part of the save state), `input.tsv` notes the read (`read=N`) and the re-run hands the change to that
read, however fast the host runs it (the guest clock follows the host: under load a change matched by guest time
landed a frame early or late). Motion samples, and captures without read numbers, play back by guest clock time (a
save state restores the guest clock exactly); `sceKernelGetRandomNumber` returns the recorded numbers in order. The
re-run records itself as a capture into `<capture>/reruns/` (no state, no GPU dump unless `--rerun-gpu-dump`),
analyzes it, and pairs its frames with the original's by guest frame from the state (the re-run's first frame is the
recording's first after the state's guest time; `SaveStates.FramesPresentedAtLastLoad`), by guest time for a
re-run that does not say where it started (`compare.txt`: which pictures match, the first that differs, and the
first HLE call that differs thread by thread: another function, or another result). It ends 2 s after the last
recorded event. Headless unless `--window`.

Not deterministic: guest threads run on host threads, so their interleaving, and with it every guest clock read,
differs from the recording. The guest clock reads themselves are not recorded (thousands per frame). A re-run of a
FIFA 13 menu capture from its pre-roll state matched 95% of the original's pictures (median difference 0) and showed
the same findings.

## Validation (desktop, headless, GTX 1650 laptop, 05/11/2026)

| Game | Capture | What the analyzer found from the capture alone |
|---|---|---|
| FIFA 13 (PCSE00093), menus | `--capture-preroll=10 --capture-at=1500:1900`: 1359 frames, 959 of them pre-roll (16.8 s before the start) | the menu fade (black 0.34 s), a 88 ms `fileIo` stall on Cross, the stalls of the pre-roll save states (labelled as emulator actions); audio: port + NGS voice + mix. Re-run from the pre-roll state: 95% of the frames the same, median difference 0, same findings |
| Jak 1 (PCSA00080), user's F5 state loaded after the collection's LoadExec, `--cpu-mhz=0` | `--capture-preroll=10 --capture-at=2150:2300`: 1400 frames, 1250 pre-roll (the pre-roll state is 49.5 s before the start, before the LoadExec) | `black-screen` for 5.7 s around the state load (found in the pre-roll, 25 s before the recording started), the LoadExec black transition, the two renderer replacements, guest-waits-for-GPU stalls in gameplay |

**Validated by the user in a window (06/11/2026, build 0d298b4):** F8 on a game that never presented a frame
(Injustice, PCSE00271, hung at boot) gave a complete capture: `session.txt`, `state_start.vss`, threads at start and
stop, `hle_errors.txt` (it named the cause: `sceKernelWaitEvent` = 0x80028201 692,895 times), log, input, timeline,
an empty frames file and a 0-frame GPU dump, and the zip. F8 on Jak 1 gameplay (35 s) gave the full set with a
682-frame GPU dump and 53 audio files. The hung-game and window-close finalization (d6cd59c) counts as done.

Not covered yet: the black start of a *new* Jak 1 game (headless, Jak 1 stays at 0.4-2 fps on its title screen after
the LoadExec, with or without a capture: an open Jak bug), the Croixleur Sigma SKIP flicker (only in a window), the
Android build on a phone. The user's F5 state of Jak 1 hangs intermittently right after the load (main waits on
`DMAPut`, F3DPlaybackThread on `F3DRecordingMessagePipe`): 2 of 3 runs with the pre-roll, 1 of 4 without in the bug
agent's runs; the hang starts before the pre-roll's next save state.

## Verify (is the problem still there?)

`VitaSharp.Desktop verify <capture>` (`src/VitaSharp.Desktop/VerifyCapture.cs`, `Core/Capture/CaptureVerify.cs`):
the capture's findings (the report's rules over `frames.vsfr` through `FrameFindings`, and the `[anomaly] ... nan:`
lines of `log.txt`) against those of a replay of `gpu.vsgd` by the running build. The replay runs in a child process
(`replay-gpu --findings`: the renderer's switches are static, read once per process) with `FrameFindings` as its
present tap, so it sees each frame as a recording does (the debug-mode replay ring's downscale), and debug mode on for
the NaN/Inf scan; it writes `findings.txt`. Findings are matched by kind, present (one either way: the replay numbers
its presents from the dump's `first-present`, as the recording did) and cell. `--rerun` adds a `rerun` of the logic
and compares the two `report.txt` kind by kind. `FrameFindings` is the API every tool that measures a flicker from
frames uses (one set of rules: the analyzer's). Tests: `CaptureVerifyTests` (rules, matching, files) and
`CaptureVerifyRenderTests` (a synthetic scene with a HUD that disappears for one frame, recorded into a GPU dump and
replayed on the software Vulkan device).

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
