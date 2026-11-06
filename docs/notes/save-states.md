# Save states

Saving and loading the whole emulated machine of a game run directly (one process, not under the Vita shell).
Code: `src/VitaSharp.Core/SaveState`, the `*.Snapshot.cs` parts next to each class, the generator
`src/VitaSharp.Core.Generators/SnapshotGenerator.cs`. Module coverage: the inventory in `dev-snapshots.md`.

## Use

- **Desktop window:** F5 quick save, F7 quick load, F6 selects slot 1-9, Shift+F5 / Shift+F7 save / load that slot.
  The title bar shows the result. The launcher lists a game's states with their pictures; clicking one starts the
  game and loads it (`--load-state=PATH`, after 120 presented frames).
- **Android:** pause menu "Salvar estado" / "Carregar estado", slots 1-6 with picture and date.
- **Headless tests:** `--save-state=fN[,fN...]` (to `test_fN.vss`), `--load-state-at=fN[,...]` (the last state this
  run saved), `--load-state=PATH[@fN]`.
- Files: `<data>/savestates/<TITLEID>/quick.vss`, `slotN.vss`.

## File

`"VSSTATE\0"`, format version, uncompressed header (emulator version, layout hash, title id/name, app version,
time, guest seconds, PNG picture), then Brotli sections: `system`, `hle.functions`, `kernel.objects`, `hle:<Module>`,
`memory.blocks`, `mem:<address>` (a bitmap of the non-zero pages, then those pages). A state loads only when the
format version, the title and the layout hash match (`SnapshotSchema.Hash` = `ManualVersion` + a hash the generator
computes over every generated layout); otherwise the load is refused with the reason. Raise
`SnapshotSchema.ManualVersion` whenever a hand-written layout changes.

## Safe point

1. Every guest thread is *held*: it parks at its next block boundary at call depth 0 while the emulation and the guest
   clock still run, so threads inside guest callbacks finish them and module service threads (the GXM display queue,
   whose callback waits for vblanks) go idle. The thread with a GPU scene open runs on until the scene ends.
2. The emulation is paused (clock frozen), the GPU queue drained, and every thread checked:
   - parked at depth 0: saved as is;
   - blocked in an HLE wait at depth 0 (`Wait.Until`, `Scheduler.Block`): saved as "about to issue the call" (PC =
     the SVC, registers as at the call); on load the call is issued again and blocks again, with its timeout
     restarted. A reissued condition wait does not release its mutex again (another thread may own it by
     now): it waits for the signal and then takes back the lock count it had released, which is saved;
   - anything else (a nested callback, host work, a condition wait taking its mutex back, a frame in the GXM display
     queue): "not at a safe point", the save resumes for a moment and retries (3 s at most).
3. The whole state is copied to memory (~0.1-0.2 s with the game stopped), the game resumes, the file is compressed
   and written in the background. A per-thread HLE sequence number checks that no thread woke while it was taken.

## Load

The file is decompressed while the game still runs. Then the threads are held, the service threads idle, the
emulation paused, every guest thread stopped, and: GPU backend replaced (its caches rebuild from guest memory, as
after a LoadExec; render targets the game reads back before drawing are black for a frame), translated code dropped,
memory blocks and contents restored, HLE function table, kernel objects (new threads with their saved registers;
module service threads kept and given their saved UIDs), modules, guest clock (exactly the saved time), then the
threads start. The loaded modules must be the saved ones at the same addresses: a game that loads modules late
refuses a state from a different point with a message (restart the game and load again).

## Host resources

Recreated on load: audio ports, AT9/MP3/AAC and H.264 decoders (empty; movies restart on the key frame before the
saved position, a few pictures of drift), MP4 demuxers and AvPlayer media (reopened and seeked), open files
(reopened at their offset), camera captures, system fonts. Dropped: network sockets (the game sees a disconnect).
Not saved, the save is refused while one exists: see "refused in use" in the inventory. Saved data, trophies and
registry values the game wrote are the user's (on disk) and are not rolled back.

## Tested (desktop, 04/11/2026)

Save, load ~10 s later, play on (headless runs with the scenario inputs). "Pass" = saved, loaded, no crash, the
picture continues from the saved point, frame rate as before the load.

| Game | Family | Result |
|---|---|---|
| Knightin'+ | Construct 2, no video | pass (save select, then the dungeon entered after the load) |
| Uncharted: Golden Abyss | gameplay, NGS, fibers | pass (title and gameplay) |
| F1 2011 | movies, NGS, race | pass (race; intro movie reopened by Mp4) |
| Persona 4 Golden | movies | pass (title) |
| LittleBigPlanet, Devious Dungeon | NGS audio | pass |
| Killzone: Mercenary | AT9, touch gestures | pass (after SystemGesture state) |
| Invizimals | camera / motion | pass (menu) |
| The Deer God, Stardew Valley | Unity | pass |
| Sonic Mania, Blast 'Em Bunnies, Jak and Daxter Collection | misc., AvPlayer (LLE) | pass |
| Tearaway | deferred contexts | pass (06/11/2026: loaded twice in a row in debug mode, Gravar tudo pre-roll states around the loads, moves with the stick after them; no device loss in 3 headless loads) |
| Minecraft, Gravity Rush | NP matching / voice ports, touch / motion, PVF game fonts | pass |
| Jak and Daxter Collection, God of War Collection | LoadExec after a load | pass (06/11/2026: state in the collection menu, loaded, then Jak 1 to PRESS START / God of War 1 to its menu at 30 fps) |

Not exercised by an installed game: Atrac (ATRAC streams), Sas, Audioenc/Videoenc/Mp4Recorder, G729, Location,
NetAdhocMatching/Rudp (online), TriggerUtil, Ime (in-game keyboard), the Vita shell (refused).
