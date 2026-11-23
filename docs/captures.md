<!-- pair: captures.pt-BR.md sha256=bea37c97906c9c5ed04e12ad4026b4236dc5a9a617d91b766e39f9a028df5299 -->
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

No licence, key, serial or password is written. No file of a plugin is copied, and no whole file of the game: the save
state holds the guest memory as the game had it, and `files/` holds only the bytes the game read from its own mounts
(app0, addcont0) during the recording, each distinct content once, up to 256 MB (64 MB on Android;
`VITASHARP_CAPTURE_FILES_MB=<MB>` or `--capture-files-mb=<MB>`; 0 keeps only the paths, positions and hashes).
That is what the re-run needs; `report.txt` says when a capture holds data of the game, so it is shared only where
that may go. The save data (savedata0) is the player's: only the paths, positions and hashes of its reads are kept,
unless the recording is made with `--capture-savedata` (or `VITASHARP_CAPTURE_SAVEDATA=1`); the re-run reads the
save of its own data folder.

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

`hle_calls.tsv` has one line per HLE call of the recording (guest time, thread, function, library, r0-r3, result,
return address); `hle_errors.txt` sums them by function with the error returns.

The files the game read during the recording are put back over its mounts for the re-run, so it reads the same
bytes without the game; the re-run's summary says whether it read anything the recording did not (`files:` line), and
`report.txt` lists what was read and whether it all left with the capture.

What still needs the game: reads past the `files/` limit (the re-run reads zeros there), firmware modules the game
loads for the first time during the recording (they come from the analyst's firmware), and captures of the system
shell with applications open (several processes; booting them from the state is not supported yet).

Details for developers: [`docs/notes/session-capture.md`](notes/session-capture.md).
