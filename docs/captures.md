<!-- pair: captures.pt-BR.md sha256=b485ae75167a5a448261bf24d517e24fd4e6dbe9b001a0f53a2a4597e66c0c8c -->
# Analysing a Record everything capture without the game

**English** | [Português](captures.pt-BR.md)

A **Record everything** capture (F8 on the PC, pause menu on Android) is made to be sent: someone posts the `.zip` on
GitHub and whoever fixes the problem works from it alone, on a computer that has neither the game, nor its update,
nor the plugins of the person who recorded it.

## What leaves with the capture

Nothing personal. Every text file of the capture (`session.txt`, `log.txt`, `report.txt`, `context.txt`, the thread
lists, `hle_calls.tsv`, `audio/files.txt`) and the GPU dump's header go through a last pass that replaces:

| What | Placeholder |
|---|---|
| the VitaSharp data folder | `<data>` |
| the home folder and the user name | `<user>` |
| the computer's name | `<pc>` |
| the PSN / RPCN online id and account options (`--psn-id`, `--rpcn-user`, `--rpcn-token`) | `<psn-id>` |
| MAC addresses | `<mac>` |
| e-mail addresses | `<email>` |

No licence, key, serial or password is written. No file of the game or of a plugin is copied: the save state holds the
guest memory as the game had it, and `files/` lists what the game read from its own mounts (app0, addcont0) during
the recording, by path, position, length and hash only. The bytes go with it only when the recording is made with
`--capture-files-mb=<MB>` (or `VITASHARP_CAPTURE_FILES_MB=<MB>`), each distinct content once up to that size, and
`report.txt` then says the capture holds data of the game, so it is shared only where that may go. The save data
(savedata0) is the player's: only its hashes, unless the recording is made with `--capture-savedata` (or
`VITASHARP_CAPTURE_SAVEDATA=1`); the re-run reads the save of its own data folder.
The microphone is what the player and the people near them say: `audio.txt` gives only the microphone port's
format, source and pace, and its sound goes in (`audio/NN_microphone_RATE.wav`) only with `--capture-mic-audio` (or
`VITASHARP_CAPTURE_MIC_AUDIO=1`).
The position is where the player is: while a capture is armed or recording, the Vita's location service answers
"position undetermined" (so no position enters the guest memory the capture keeps) and `session.txt` keeps
`--location=<location>`; only `--capture-location` (or `VITASHARP_CAPTURE_LOCATION=1`) lets it answer and keeps the
option's value.

## What the capture says about the setup

`session.txt` names the title and its version without the game: `title-id`, `app-guest-path` (`ux0:app/<id>`),
`app-version` (the APP_VER the system reads; with an installed update, the application's own one too), `category`,
`content-id`, the `firmware` version, `kernel-plugins` on or off, the system language, the emulator options in effect
(`options:` and every `VITASHARP_*` variable set, `env:`), the build, the renderer and GPU, and every loaded module
(`module:` lines: name, guest path, address and image hash, the game's executable, the firmware modules and the
plugins), so two captures of the same version have the same hashes.

## Analysing it

| Analysis | Command | Needs |
|---|---|---|
| Report (frames, stalls, audio, findings) | `vitasharp-perf capture <folder or zip>` | nothing |
| GPU (what was drawn) | `VitaSharp.Desktop replay-gpu <folder>/gpu.vsgd --out=<dir>` | a Vulkan device (any; SwiftShader / lavapipe work) |
| CPU, HLE, audio, logic (the game runs again) | `VitaSharp.Desktop rerun <folder>` | a Vulkan device; no game, no plugins, no firmware |
| The system's side | read `hle_calls.tsv` and `hle_errors.txt` | nothing |

`rerun` brings the program up from the capture's own save state: every module the process and the kernel had loaded
(the game, the firmware modules, the plugins) is rebuilt from the state's memory, then the recorded input is played
back and the re-run is recorded and compared with the original (`reruns/<...>/compare.txt`: the pictures, and the
first HLE call that differs). A path written in the capture is never opened: the re-run uses the data folder it is
given (`--data=<folder>`). `--boot=title` boots the installed game instead and loads the state into it.
`--from=preroll` (the default with a pre-roll: a state at most 5-10 s before it), `--from=start`, or `--from=markN`: the
state of the moment the player marked the problem (Shift+F8 while recording, or the Android pause menu's "Mark the
problem"; `state_mark_N.vss`, listed in `session.txt` and in the report).

`hle_calls.tsv` has one line per HLE call of the recording (guest time, thread, function, library, r0-r3, result,
return address); `hle_errors.txt` sums them by function with the error returns.

The files the game read during the recording are put back over its mounts for the re-run, at their size and, when
the recording kept them, with the bytes it read, so it reads the same bytes without the game; the re-run's summary
(`files:` lines) says whether it read anything the recording did not, and which reads it could not put back (kept
as hashes only: the re-run reads zeros there). `report.txt` lists what was read and whether its bytes left with the
capture.

What still needs the game: the bytes of a recording made without `--capture-files-mb`, firmware modules the game
loads for the first time during the recording (they come from the analyst's firmware), and captures of the system
shell with applications open (several processes; booting them from the state is not supported yet).

## Checking a fix

```
VitaSharp.Desktop verify <capture folder or zip> [--out=<dir>] [--rerun] [replay options]
```

Says whether the problem the capture recorded is still there with the build that runs it, without the game and
without anyone playing. It replays the capture's GPU dump with this build (`replay-gpu --findings`, in a process of
its own), runs the report's picture rules over the replayed frames (the same frames a recording sees, at most
480x272: black flashes and screens, `[flash]` of the whole picture or of one cell of the 4x4 grid, pictures out of
order) and the NaN/Inf scan of the float render targets, and sets every finding of the recording inside the dump
against them:

```
  [flash] present 1587 cell 1,1: ainda presente (replay: present 1587 cell 1,1: ...)
  [black-flash] present 1600: corrigido (replay: nada nos presents 1599-1601)
  por tipo (gravação -> replay):
    flash: 20 -> 0 (corrigido)
resultado: corrigido
```

*corrigido* (fixed): every finding of the recording is gone and nothing new appeared; *ainda presente* (still
there): a finding is at the same present (one either way) and cell; *mudou* (changed): it moved, or the replay shows
a finding the recording did not have (*novo*). Findings before the dump (the pre-roll) or after it are listed as not
checked. Exit code 0 fixed, 1 still there, 2 changed, 3 nothing to check, 4 the replay failed. Everything goes into
`--out` (default `<capture>/verify`: `verify.txt`, the replay's `log.txt` and `findings.txt`); the capture itself is
not changed. Replay options (`--res-scale=2`, `--env=KEY=VALUE`, `--msaa=...`) go to the replay; a dump recorded at
a resolution scale carries its render targets at that scale, so its replay at the same scale starts from exactly
the pixels the player saw.

The replay executes the recorded commands: a finding still there in it is drawn by the renderer from those
commands, or is already in them (the cause is before the GPU). `--rerun` also runs the game logic again from the
capture's state (`rerun`) and compares the two reports kind by kind (stalls, presentation order, audio gaps and
clicks); a re-run is not deterministic, so only the counts are compared.

### Which draw

For every flash still there (at most three), `verify` names the draw responsible, without bisecting by hand: one more
replay with the draw probe (`replay-gpu <dump> --probe=PRESENT:CELL`, also usable alone) copies the finding's region
of the surface shown at that present and at its two neighbours before and after every draw into it, with the depth
and stencil under it before the draw. The draws of the flashing frame are lined up with the neighbour's (same
programs, in order) and the first that differs is named, with its programs (vs/fs hashes), its state and why:

```
  draw responsável por [flash] present 1587 cell 1,1:
    draw responsável: draw 41823 vs=... fs=... rt=0x66600000 depth=0/True st=7/7 blend=none ... t0=0x81200000:0C000000
      por quê: não escreveu na região no quadro do pisca (no vizinho: 412 px); depth 0/True (vizinho 7/True), profundidade antes 0.2500 (vizinho 1.0000)
```

The reasons: *falta no quadro do pisca* (the neighbour has the draw, the flashing frame does not), *só no quadro do
pisca*, *não escreveu na região* (the same draw wrote nothing there: rejected by the depth, stencil or alpha test,
clipped or moved; the state fields and the depth/stencil under it that differ are listed), *estado diferente*. When
no draw into the shown surface differs, the cause is a surface drawn earlier and sampled, or which buffer was
presented. The files: `probe_P/probe_P.txt` (every probed draw: present, draw number, target, programs, pixels of
the region it changed, depth and stencil before, state) and `probe_P/attribution_P.txt`.

### Which switch (A/B)

`verify --ab` replays the dump once more for each of the renderer's switches that change how a picture is drawn
(each in its own process, up to two presents after the last finding) and compares the findings with the
switch-free replay's: *o achado some* (the finding goes away: the switch names the class of the bug), *muda*, or
*igual*. The switches (`--ab=NAME,...` picks some; `--ab-env=KEY=VALUE` adds any other, repeatable):

| Name | Variable | What it changes |
|---|---|---|
| `depth-seed-off` | `VITASHARP_DEPTH_SEED=0` | a loaded depth/stencil starts undefined instead of the surface's earlier image |
| `msaa-off` / `msaa-ss` / `msaa-native` | `VITASHARP_MSAA` | MSAA scenes single-sampled, as 2x supersampling, multisampled |
| `store-writeback-off` | `VITASHARP_GPU_STORE_WRITEBACK=0` | shader stores stay in the renderer's mirror |
| `scene-breaks-strict` | `VITASHARP_SCENE_BREAKS=strict` | every upload or copy in a scene ends the rendering |
| `fetch-pass-merge-*`, `fetch-large-*`, `fetch-pass-join-*`, `depth-feedback-*` | `VITASHARP_FETCH_PASS_MERGE`, `_FETCH_LARGE`, `_FETCH_PASS_JOIN`, `_DEPTH_FEEDBACK` (0 and 1) | the fetch passes of programmable blending and depth sampling |
| `depth-ops-none-off` | `VITASHARP_DEPTH_OPS_NONE=0` | unused depth/stencil is still loaded and stored |
| `gpu-profile-mali` | `VITASHARP_GPU_PROFILE=mali` | the tile-based (phone) renderer paths |
| `gpu-poison` | `VITASHARP_GPU_POISON=1` | undefined contents get loud values: a read of something never drawn shows |
| `res-scale-1` | `VITASHARP_RES_SCALE=1` | native resolution |

```
A/B dos interruptores do renderizador (18; o replay de cada um contra o replay sem eles):
  depth-seed-off (VITASHARP_DEPTH_SEED=0: ...): igual
  msaa-off (VITASHARP_MSAA=off: MSAA scenes drawn single-sampled): o achado some
  ...
  o achado some com: msaa-off (a classe do bug: MSAA scenes drawn single-sampled)
```

The replays go into `<out>/ab/<name>/`.

Details for developers: [`docs/notes/session-capture.md`](notes/session-capture.md).
