# Varredura de compatibilidade (10/11/2026)

Agente A, branch fix/bugs-a-2, build do main 5843bbd. Cada jogo instalado no ux0 do usuário (47, por junction numa pasta
de dados de teste, saves copiados) rodou headless com Vulkan por 180 s, um por vez, fixado em 0xF0, com uma entrada
roteirizada (cross a cada 150 quadros, START a cada 600). Colunas: até onde chegou (pelos prints a cada 300 quadros),
quadros apresentados, fps (média da rodada / mediana e mínimo das janelas de 5 s), funções HLE sem implementação, linhas
`[Error]`. O fps headless não é o da janela (sem vsync e com outro emulador às vezes rodando ao mesmo tempo).

Achado e corrigido: **MotorStorm RC (PCSA00034)** travava no quadro 1 (o caminho rápido em código guest do
`sceKernelSignalLwCond` perdia sinais dados por uma cópia da área da lwcond); correção genérica em be76b00.

Nenhum outro jogo travou, deu exceção ou chamou função sem implementação nesses 180 s. Os que pararam em
"touch to start"/"press start" esperam toque ou um START mais longo que o roteiro dá (não são defeitos).

## Resultados

| ID | Jogo | Chegou a | Quadros (180 s) | fps médio / mediana / mín | Unimpl | Erros | Notas |
|---|---|---|---|---|---|---|---|
| DEADSPACE | Dead Space | menu principal | 10380 | 57.6 / 59.9 / 59.8 | 0 | 0 | 60 fps |
| MANIA0001 | Sonic Mania | gameplay (Green Hill) | 9773 | 54.3 / 59.8 / 10.6 | 0 | 0 | 60 fps |
| PCSA00008 | Resistance: Burning Skies™ | título (Touch here to start) | 5279 | 29.3 / 29.9 / 28.3 | 0 | 0 | precisa de toque |
| PCSA00017 | LittleBigPlanet™ PlayStation®Vita | menu (pod) | 4452 | 24.7 / 29.8 / 4.2 | 0 | 0 | aviso: caminho 'memory:/output' não mapeado (inofensivo) |
| PCSA00029 | Uncharted: Golden Abyss™ | título (Touch to start) | 6312 | 35.1 / 58.1 / 0.4 | 0 | 0 | 1 STALL de 5 s da GPU no 1º boot (cache de shaders frio) |
| PCSA00034 | MOTORSTORM® RC | quadro 1 (travado) | 1 | 0.0 / 0.0 / 0.0 | 0 | 0 | **corrigido em be76b00** (lwcond por cópia da área); depois: tutorial de condução |
| PCSA00048 | Table Top Tanks | título (Touch the screen) | 2883 | 16.0 / 22.7 / 1.0 | 0 | 0 | carregamento lento (1-4 fps), dirs '/asset_00/...' não mapeados (FIOS LLE) |
| PCSA00080 | Jak and Daxter Collection | título (Press Start) | 3290 | 18.3 / 19.9 / 11.7 | 0 | 0 | 18-20 fps (nativo 30); DisplayQueue 27 ms/quadro |
| PCSA00115 | MLB® 14 The Show™ | título (Swipe) | 9684 | 50.8 / 59.9 / 6.7 | 0 | 4 | 4 erros = sondagem de memória do jogo (AllocMemBlock OOM esperado) |
| PCSA00126 | God of War® Collection | menu (Credits) | 5875 | 32.6 / 33.5 / 2.7 | 0 | 0 |  |
| PCSA00152 | Soul Sacrifice™ Delta | filme/intro | 3988 | 22.1 / 29.7 / 3.0 | 0 | 0 |  |
| PCSA00552 | Invizimals™: The Resistance | menu principal | 4475 | 24.4 / 29.8 / 3.6 | 0 | 0 |  |
| PCSB00097 | NINJA GAIDEN Σ PLUS | gameplay | 4951 | 27.5 / 30.0 / 18.9 | 0 | 0 | 30 fps |
| PCSB00232 | Call of Duty® Black Ops: Declassified (рус) | título (Press START) | 5130 | 28.5 / 30.0 / 15.8 | 0 | 0 | START de 8 quadros não passa; de 30 quadros passa (teste à parte) |
| PCSB00245 | Persona 4 GOLDEN | gameplay (Shopping District) | 5004 | 26.4 / 29.9 / 8.3 | 0 | 0 |  |
| PCSB01070 | The Deer God | gameplay | 19952 | 110.8 / 72.2 / 8.1 | 0 | 0 | IMMEDIATE sem ritmo no headless (110 fps) |
| PCSB01073 | GOD WARS Future Past | cena de abertura | 8696 | 48.3 / 59.8 / 2.8 | 0 | 0 |  |
| PCSB01240 | Devious Dungeon | gameplay | 9935 | 55.2 / 59.8 / 21.5 | 0 | 0 | 60 fps |
| PCSC00045 | Killzone®: Mercenary | cartão Valor (diálogo de toque) | 3584 | 19.9 / 29.9 / 0.7 | 0 | 0 | precisa de toque |
| PCSE00002 | F1 2011 | corrida | 8917 | 49.5 / 58.6 / 25.1 | 0 | 0 | ~58 fps |
| PCSE00006 | ARMY CORPS OF HELL | Formation (menu) | 5033 | 27.9 / 30.0 / 15.2 | 0 | 0 |  |
| PCSE00011 | Silent Hill: Book of Memories | seleção de área | 2906 | 16.1 / 17.2 / 2.6 | 0 | 0 | 17 fps de mediana nesta rodada (outro emulador junto) |
| PCSE00015 | Shinobido 2: Revenge of Zen | gameplay | 4504 | 25.0 / 29.8 / 13.0 | 0 | 2 | 2 erros: AllocMemBlock(gxm, CDRAM, 256 KB) sem memória; o jogo segue |
| PCSE00056 | Sonic & All-Stars Racing Transformed™ | menu | 5301 | 28.9 / 29.9 / 20.9 | 0 | 0 |  |
| PCSE00089 | Need For Speed™ Most Wanted | diálogo do Origin | 4277 | 23.7 / 29.8 / 2.1 | 0 | 0 | X de 8 quadros não passa (rodada guiada passa) |

## Segunda rodada (os 22 que faltavam)

Agente A3, branch fix/bugs-a-3 (main b80568a + a correção do VTST com produto escalar), mesmo roteiro (180 s, cross a
cada 150 quadros, START a cada 600, headless Vulkan, 0xF0). Nenhum travou, deu exceção ou chamou função sem
implementação; 0 linhas `[Error]` em todos.

| ID | Jogo | Chegou a | Quadros (180 s) | fps médio / mediana / mín | Unimpl | Erros | Notas |
|---|---|---|---|---|---|---|---|
| PCSE00093 | FIFA 13 | título (Press START) | 7120 | 39.5 / 58.9 / 0.0 | 0 | 0 | 60 fps no título |
| PCSE00213 | Jetpack Joyride | título (Tap rear touch pad) | 9897 | 54.9 / 59.7 / 7.7 | 0 | 0 | precisa do touch traseiro |
| PCSE00233 | NINJA GAIDEN Σ2 PLUS | gameplay (primeira fase) | 4660 | 24.2 / 29.6 / 11.1 | 0 | 0 | 30 fps |
| PCSE00269 | Batman: Arkham Origins Blackgate | cena de abertura | 3807 | 21.1 / 29.2 / 3.1 | 0 | 0 |  |
| PCSE00271 | Injustice: Gods Among Us | cena de abertura | 5703 | 31.7 / 30.0 / 26.2 | 0 | 0 | 30 fps |
| PCSE00293 | FINAL FANTASY X HD Remaster | cena de abertura | 5028 | 27.9 / 29.9 / 4.1 | 0 | 0 | 30 fps |
| PCSE00333 | The Amazing Spider-Man | título | 10379 | 57.6 / 59.9 / 29.1 | 0 | 0 | 60 fps |
| PCSE00491 | Minecraft | menu principal | 9755 | 54.2 / 55.9 / 48.0 | 0 | 0 | o cross não sai do menu (cursor parado) |
| PCSE00589 | Severed | gameplay | 10249 | 56.8 / 59.9 / 42.6 | 0 | 0 | 60 fps |
| PCSE00606 | Skullgirls 2nd Encore | modo história (cenas) | 7522 | 37.1 / 52.5 / 7.8 | 0 | 0 |  |
| PCSE00781 | Zero Time Dilemma | diálogo "Load save?" | 6986 | 38.8 / 51.3 / 3.9 | 0 | 0 | o cross não confirma (talvez círculo ou toque) |
| PCSE00914 | 99Vidas | gameplay | 9813 | 54.5 / 59.8 / 22.9 | 0 | 0 | aviso de rede do próprio jogo no boot |
| PCSE01023 | Salt and Sanctuary | criação de personagem / carregamento | 3133 | 17.4 / 26.3 / 0.3 | 0 | 0 | ~70 s a 0,3-1,5 fps ao sair da criação (quadros 1934-1977, 1 STALL de 5 s) |
| PCSE01235 | Stardew Valley | título (New/Load) | 10481 | 57.5 / 59.9 / 55.0 | 0 | 0 | menu por toque |
| PCSF00024 | GRAVITY RUSH | título | 5254 | 29.2 / 30.0 / 29.2 | 0 | 0 | precisa de toque |
| PCSF00034 | Unit 13 | título | 5330 | 29.6 / 30.0 / 29.6 | 0 | 0 |  |
| PCSF00476 | Tearaway | menu (PLAY) | 5254 | 29.2 / 30.0 / 18.9 | 0 | 0 | menu por toque |
| PCSG00198 | Sakatsuku | título (Press START) | 5245 | 29.1 / 30.0 / 25.6 | 0 | 0 | START de 8 quadros não passa |
| PCSG00537 | Croixleur Sigma | abertura | 5352 | 29.7 / 30.0 / 28.8 | 0 | 0 |  |
| PCSG00845 | Silverio Vendetta | menu principal | 8567 | 47.6 / 59.2 / 5.4 | 0 | 0 |  |
| PCSG00952 | Akiba's Beat | filme de abertura | 9966 | 55.3 / 59.9 / 39.4 | 0 | 0 |  |
| SILH00004 | Silent Hill 4 (port) | Load (sem jogo salvo) | 5338 | 29.5 / 30.0 / 29.5 | 0 | 0 | 30 fps; o roteiro entra no Load |
## Não rodados na primeira rodada (feitos na segunda, acima)

PCSE00093, PCSE00213, PCSE00233, PCSE00269, PCSE00271, PCSE00293, PCSE00333, PCSE00491, PCSE00589, PCSE00606,
PCSE00781, PCSE00914, PCSE01023, PCSE01235, PCSF00024, PCSF00034, PCSF00476, PCSG00198, PCSG00537, PCSG00845,
PCSG00952, SILH00004: a limpeza de arquivos temporários do Windows (Storage Sense, disco a 99%) apagou as DLLs do build
da varredura às 15:37 e às 16:17, e essas rodadas não iniciaram (o "crash" do Gravity Rush era a StbTrueTypeSharp.dll
apagada). Ficam para a próxima rodada.

## Terceira rodada: paradas por entrada (agente A4, fix/bugs-a-4)

Os jogos que a varredura deixou parados numa tela, rodados de novo com navegação guiada (`--nav`, headless Vulkan).
Nenhum era defeito de entrada ou de diálogo do emulador; o botão de confirmar do sistema (cross, o padrão de um console
ocidental, ou o `button_assign` gravado no registro) chega certo aos jogos que o leem (o Minecraft mostra "X Selecionar").

| ID | Jogo | O que parava | Causa | Roteiro |
|---|---|---|---|---|
| PCSE00781 | Zero Time Dilemma | "Load save?" | diálogo do próprio jogo com NO pré-selecionado: o cross escolhia NO | `tests/nav/ztd_load.nav` (esquerda + cross): história |
| PCSE00491 | Minecraft | menu principal | o cross entra em Jogar; no aviso "não conectado", "Conectar-se à PSN" volta ao menu (NetCheckDialog sem rede = cancelado) | `tests/nav/minecraft_offline.nav` (baixo + cross = jogar offline): gameplay |
| PCSG00198 | Sakatsuku | título | o START de 8 quadros passa; no menu o jogo (japonês) confirma com círculo e não lê o parâmetro do sistema | `tests/nav/sakatsuku_newgame.nav`: seleção da secretária |
| PCSE01023 | Salt and Sanctuary | lentidão ao sair da criação | não reproduz (180 s a 28-30 fps, cache quente e frio); a rodada anterior teve o disco a 99% | |

Observação: o roteiro da varredura (`sweep.ps1`) nunca apertava START (os quadros 400+150k nunca são múltiplos de 600):
era só cross.

### Paradas por toque (180 s cada, `--nav` com `tap`/`btap`)

O roteiro de navegação ganhou `btap X Y [quadros]` (toque no painel traseiro; `tap` já tocava o da frente). Rodadas de
180 s headless depois das telas de toque, com a build desta branch:

| ID | Jogo | Roteiro | Chegou a | fps | Notas |
|---|---|---|---|---|---|
| PCSE00213 | Jetpack Joyride | `tests/nav/jetpack_rear.nav` (`btap` no título, toques longos para voar) | uma corrida (169 m) e a tela de resultado | 54,9 médio | a "foto" da morte na tela de resultado sai preta: conferir |
| PCSE01235 | Stardew Valley | `tests/nav/stardew_new.nav` | criação de personagem (New) | 57-60 | não é parada por toque: a versão do Vita só lê o painel como botões extras nos cantos; o menu anda pelo direcional. O save do usuário não tem jogo salvo (Load: "No Saved Games Found") |
| PCSF00024 | Gravity Rush | `tests/nav/gravityrush_continue.nav` (`tap` em CONTINUE) | gameplay (túnel do começo), andando | 28,0 médio | |
| PCSF00476 | Tearaway | `tests/nav/tearaway_continue.nav` (`tap` em Continue) | The Standing Stones, andando | 28,5 médio | numa rodada com a suíte de testes rodando junto caiu a 0,3 fps (PC sem CPU); sozinho, 30 fps. Uma vez (build anterior desta branch, cache de shaders compartilhado) deu VULKAN DEVICE LOST ao entrar no gameplay; não repetiu em 4 rodadas |
