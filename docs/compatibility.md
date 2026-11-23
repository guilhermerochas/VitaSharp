<!-- pair: compatibility.pt-BR.md sha256=80b457ef2610a02ce4181fab321fdb7ad7bd3f276188376bad8c472d5972b95c -->
# Compatibility

**English** | [Português](compatibility.pt-BR.md)

Games VitaSharp has been tested with, back to the [README](../README.md).

## More screenshots

<table>
<tr>
<td><img src="assets/screenshots/lbp.webp" alt="LittleBigPlanet PS Vita" width="300"><br><sub><b>LittleBigPlanet</b>, notebook intro</sub></td>
<td><img src="assets/screenshots/ninja-gaiden-sigma.webp" alt="Ninja Gaiden Sigma Plus" width="300"><br><sub><b>Ninja Gaiden Σ Plus</b>, gameplay</sub></td>
<td><img src="assets/screenshots/skullgirls.webp" alt="Skullgirls 2nd Encore" width="300"><br><sub><b>Skullgirls 2nd Encore</b>, gameplay</sub></td>
</tr>
<tr>
<td><img src="assets/screenshots/resistance.webp" alt="Resistance: Burning Skies" width="300"><br><sub><b>Resistance: Burning Skies</b>, first scene</sub></td>
<td><img src="assets/screenshots/ninja-gaiden-sigma-2.webp" alt="Ninja Gaiden Sigma 2 Plus" width="300"><br><sub><b>Ninja Gaiden Σ2 Plus</b>, chapter 1</sub></td>
<td><img src="assets/screenshots/final-fantasy-x.webp" alt="Final Fantasy X HD Remaster" width="300"><br><sub><b>Final Fantasy X HD Remaster</b>, opening</sub></td>
</tr>
<tr>
<td><img src="assets/screenshots/99vidas.webp" alt="99Vidas" width="300"><br><sub><b>99Vidas</b>, tutorial stage</sub></td>
<td><img src="assets/screenshots/blast-em-bunnies.webp" alt="Blast 'Em Bunnies" width="300"><br><sub><b>Blast 'Em Bunnies</b>, first level</sub></td>
<td><img src="assets/screenshots/knightin.webp" alt="Knightin'+" width="300"><br><sub><b>Knightin'+</b>, world map</sub></td>
</tr>
<tr>
<td><img src="assets/screenshots/verse-of-orpheus.webp" alt="Silverio Vendetta -Verse of Orpheus-" width="300"><br><sub><b>Silverio Vendetta -Verse of Orpheus-</b>, story</sub></td>
<td><img src="assets/screenshots/minecraft.webp" alt="Minecraft: PlayStation Vita Edition" width="300"><br><sub><b>Minecraft</b>, main menu</sub></td>
<td><img src="assets/screenshots/sengoku-musou-2-empires.webp" alt="Sengoku Musou 2 Empires HD" width="300"><br><sub><b>Sengoku Musou 2 Empires HD</b>, campaign map</sub></td>
</tr>
<tr>
<td><img src="assets/screenshots/invizimals.webp" alt="Invizimals: The Resistance" width="300"><br><sub><b>Invizimals: The Resistance</b>, main menu</sub></td>
<td><img src="assets/screenshots/silent-hill-2.webp" alt="Silent Hill: Book of Memories character creation" width="300"><br><sub><b>Silent Hill: Book of Memories</b>, character creation</sub></td>
<td><img src="assets/screenshots/call-of-duty.webp" alt="Call of Duty: Black Ops Declassified" width="300"><br><sub><b>Call of Duty: Black Ops Declassified</b>, title</sub></td>
</tr>
</table>

## Tested games

VitaSharp is tested against the games below through automated scenarios in [`tests/scenarios`](../tests/scenarios)
(see [Build and test](../README.md#build-and-test)). The table lists what has been observed so far; it is not a promise, and the furthest
point a scenario reaches is not the end of the game.

| Status | Meaning |
| --- | --- |
| **In-game** | Gameplay reached and rendered correctly in the tested part |
| **Menus** | Title screen, menus or the opening work; gameplay not reached by the scenario yet |
| **Hangs** | Stops before the first screen |
| **Does not run** | Needs something VitaSharp does not provide (see the notes) |

| Game | Title ID | Status | Notes | Scenario |
| --- | --- | --- | --- | --- |
| 99Vidas | PCSE00914 | Playable | Tutorial stage and first fights; Cross-Save through the local PSN services | `99vidas_boot` |
| Army Corps of Hell | PCSE00006 | Menus | Logos and the intro at 30 fps, no audio underruns | — |
| Blast 'Em Bunnies | PCSE00328 | Playable | First level and tutorial | `bunnies_boot` |
| Call of Duty: Black Ops Declassified | PCSB00232 | Menus | Logos and title screen | `cod_boot` |
| Crazy Market | PCSE00291 | Menus | Logos and the terms of service (16 fps on that screen, to confirm) | `crazymarket` |
| Croixleur Sigma | PCSG00537 | Playable | Help pages, opening, title menu, character select with the 3D model, story dialogue over the 3D scene at ~30 fps (Japanese release: Circle confirms) | `croixleur_boot` |
| Devious Dungeon | PCSB01240 | Playable | 60 fps; also starts from the real shell's LiveArea | `dd_boot` |
| F1 2011 | PCSE00002 | Playable | Races with HUD, menus and video (from an existing profile: with fresh data the scenario stops at the driver name keyboard) | `f1_drive` |
| FIFA 13 | PCSE00093 | Playable | Title screen, menus and Arena training with the players skinned correctly; the players' reflection cubes read with the right face spacing since 04/11/2026 (cyan/green blocks on the kits, to confirm in game) | `fifa13` |
| Final Fantasy X HD Remaster | PCSE00293 | Menus | Launcher menu, then the game's opening scene with credits | `ffx_boot` |
| Gal Gunvolt | PCSG00704 | Playable | Title, file select and the first stage at 60 fps; the playfield background shows since 04/11/2026 (RGBA surfaces sampled in memory order) | — |
| God of War Collection | PCSA00126 | Playable | Combat, cutscenes and audio in God of War I and II; on Mali phones since 31/10/2026 (misaligned vertex attributes) | `gow_game` |
| Gravity Rush | PCSF00024 | Playable | Opening and first gameplay; a tap on the "Tap the Apple" title does not always advance | `gr_boot` |
| Invizimals: The Resistance | PCSA00552 | Menus | Title and main menu; uses the webcam and motion sensors | `inv_boot` |
| Jak and Daxter Collection | PCSA00080 | Menus | Logos, opening movie and the collection menu at 30 fps; starting Jak 1 execs `Jak1.self` (not checked further); the menu's animated preview strip is garbled (MJPEG decode one frame ahead of the render thread, see the roadmap) | — |
| Killzone: Mercenary | PCSC00045 | Playable | Campaign at ~30 fps; open issues in the [roadmap](roadmap.md) (muzzle flash brightness, slowdowns in some areas) | `kzl_walk3`, `kz_skin` |
| Knightin'+ | PCSE01444 | Menus | Title, save select, world map | `knightin_boot` |
| LittleBigPlanet PS Vita | PCSA00017 | Playable | Notebook intro with Sackboy walking | `lbp_boot` |
| Minecraft: PlayStation Vita Edition | PCSE00491 | Menus | Terms of use, main menu, new world options | `minecraft_boot` |
| Shinobido 2: Revenge of Zen | PCSE00015 | Menus | Logos and the intro movie | `shinobido2` |
| Ninja Gaiden Σ Plus | PCSB00097 | Playable | Intro movie, menus, new game and the first fights | `ngs_boot` |
| Ninja Gaiden Σ2 Plus | PCSE00233 | Playable | Opening movie, title, new game, first fights of chapter 1 | `ngs2_boot` |
| Persona 4 Golden | PCSB00245 | Playable | New game up to the station scene (the scenario types a full name on the game's own letter grid); cannot start from the real shell yet (fixed-address executable) | `p4g_newgame` |
| Resistance: Burning Skies | PCSA00008 | Playable | New game, intro movie, first scene | `rbs_boot` |
| Sakatsuku (J.League Pro Soccer Club wo Tsukurou!) 2013 | PCSG00198 | Menus | Title, save flow, new-game creation | `st_boot`, `st_game2` |
| Sengoku Musou 2 Empires HD | PCSG00230 | Menus | Empire mode setup, army cutscene, first strategy turn | `sm2e_play` |
| Sonic & All-Stars Racing Transformed | PCSE00056 | Playable | Boots and plays (sceTouchPeek fix 63c125a; confirmed by the user 05/11/2026) | `sasrt` |
| Soul Sacrifice Delta | PCSA00152 | Playable | First area; slow at the fire and smoke (to confirm) | `soulsac_boot` |
| Silent Hill: Book of Memories | PCSE00011 | Playable | Character creation and intro cutscene | `shbom_boot` |
| Silverio Vendetta -Verse of Orpheus- | PCSG00845 | Playable | Visual novel: title, prologue and story text (needs the firmware fonts) | `verse_orpheus_boot` |
| Skullgirls 2nd Encore | PCSE00606 | Playable | Fights | `skullgirls_boot` |
| Sonic Mania (homebrew port) | MANIA0001 | Not confirmed | Needs the plugin framework: run it with the emulator option **Enable Kernel Plugins** (see [plugins](notes/plugins.md)); not yet confirmed with the option | — |
| The Deer God | PCSB01070 | Menus | Menus and the intro text; frames not paced (214-348 fps) | `deergod` |
| Tearaway (Trial & Unlock) | PCSF00476 | Playable | New profile up to the first papercraft tutorial | `tearaway_tutorial` |
| Unit 13 | PCSF00034 | Playable | First mission with HUD | `unit13_boot` |
| Uncharted: Golden Abyss | PCSA00029 | Playable | First level | `uc_walk` |
| WRC 4 FIA World Rally Championship | PCSE00411 | Playable | Fresh profile to a stage start and driving | `wrc4_boot` |
| Zero Escape: Zero Time Dilemma | PCSE00781 | Playable | Logos, title menu at 60 fps, New Game, opening and the first ~2.5 minutes of story cutscenes at 30 fps | `ztd_boot` |

Missing system functions are tracked one by one in [`docs/hle`](hle/README.md), with priorities based on what
the tested games actually import. Known bugs and future work are in the [roadmap](roadmap.md).
