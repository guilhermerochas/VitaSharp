# Recursos online por jogo (levantamento de 2026-10-29)

Jogos instalados em `%APPDATA%\VitaSharp\fs\ux0\app`, importações listadas com
`VitaSharp.Desktop imports <TITLEID> --names=SceNp,SceNet,SceHttp,SceSsl` (os números são as funções importadas de
cada biblioteca). "Agora local" é o que os serviços PSN locais (`feat/psn-local`, `--psn-local=on`, padrão; ver
`docs/hle/E2-np.md` §0.00) atendem sem servidor; "Precisa de servidor" é o que só funcionaria com a PSN de verdade (ou
outro console na rede).

Comum a todos os jogos com troféus: `sceNpTrophy*` desbloqueia e grava em `ux0:user/00/trophy/<NPCOMMID>.json`, a
notificação aparece no canto da janela e a lista aparece no launcher. A conta PSN emulada passa nas verificações de
login (NetCheckDialog, `sceNpGetServiceState`).

Legenda das funções que importam: **TUS** = `sceNpTus*` (Cross-Save, dados por usuário), **Score** = placares,
**M2** = `sceNpMatching2*` (salas online), **Auth** = tickets e direitos de DLC, **Commerce2** = loja dentro do jogo,
**WebApi** = REST da PSN (atividade, sessões), **Adhoc** = `sceNetAdhocMatching*` (multijogador local entre consoles).

| Jogo | Importações de rede/NP | Agora local | Precisa de servidor |
| --- | --- | --- | --- |
| MANIA0001 Sonic Mania (homebrew) | SceNet 9, SceNpTrophy 6 | troféus | — |
| PCSA00008 Resistance: Burning Skies | Net 29, NetCtl 11, Http 1, Ssl 1, NpManager 7, Common 5, Basic 6, Activity 3, Party 7, M2 20, Score 9, TUS 10, Trophy 7 | TUS SetData/GetData, placares (RecordScore, ByRange/ByNpId), ticket com direitos, contexto M2 inicia | partidas online (CreateJoinRoom/SearchRoom não encontram ninguém), party |
| PCSA00017 LittleBigPlanet | Http 26, Net 30, NetCtl 9, Ssl 2, NpManager 10, Common 7, Basic 10, Activity 3, Message 3, Commerce2 25, Utility 20, Trophy 8 | ticket, Lookup (sem outros usuários), presença | níveis da comunidade (servidores próprios via HTTP), loja (Commerce2 falha como servidor fora do ar), mensagens |
| PCSA00029 Uncharted: Golden Abyss | — (só NP do sistema) | troféus | — |
| PCSA00126 God of War Collection | — | troféus (2 conjuntos) | — |
| PCSA00552 Invizimals: The Resistance | Net 26, NetCtl 11, Http 2, Ssl 2, NpManager 10, Common 2, Basic 13, Activity 3, Message 4, Party 7, M2 31, Utility 2, Trophy 7 | contexto M2 inicia, busca sem salas | batalhas online, party, mensagens |
| PCSB00097 Ninja Gaiden Σ Plus | NpManager 3, Score 12, Trophy 10 | placares e game data (RecordGameData/GetGameData) | placares de outros jogadores |
| PCSB00232 Call of Duty: Black Ops Declassified | Net 29, NetCtl 11, NpManager 8, Common 5, Basic 7, Activity 3, Party 7, M2 22, Score 9, TUS 10, Trophy 7 | TUS, placares, ticket com direitos, contexto M2 | multijogador online, party |
| PCSB00245 Persona 4 Golden | Net 1, NetCtl 2, Adhoc 6, NpManager 6, Activity 3, M2 17, Score 11, Trophy 5 | placares, contexto M2 | "Voice of Persona" e ajuda online, ad hoc |
| PCSB01240 Devious Dungeon (Unity) | Http 27+2, Net 35+, Ssl 9+2, NpManager 11, Common 10, Basic 9, Message 5, M2 29, Score 17, TUS 15, Utility 26, WebApi 19, WebApiCommonDialog 8, Commerce2 30, Trophy 13 (UnityNpToolkit) | TUS/Cross-Save, placares, ticket, M2, Lookup | WebApi, loja, mensagens |
| PCSC00045 Killzone: Mercenary | Http 18, Net 27, NetCtl 10, Ssl 3, NpManager 7, Common 6, Basic 14, Party 7, M2 23, Utility 9, Trophy 9 | ticket com direitos, contexto M2 inicia | multijogador online (salas), Valor Card online, party |
| PCSE00002 F1 2011 | Net 11, NetCtl 9, Http 2, Ssl 2, NpManager 8, Basic 7, M2 22, Trophy 7 | contexto M2 inicia, busca sem salas | corridas online |
| PCSE00233 Ninja Gaiden Σ2 Plus | NpManager 3, Trophy 8 | troféus | — |
| PCSE00293 Final Fantasy X HD | — (launcher; o jogo é outro executável) | troféus | — |
| PCSE00328 Blast 'Em Bunnies | Net 2, NetCtl 6, Http 2, Ssl 2, NpManager 8, Common 1, Basic 2, Activity 3, Score 5, TUS 8, WebApi 8, Trophy 7 | TUS, placares | WebApi |
| PCSE00411 WRC 4 | Net 24, NetCtl 4, NpManager 8, Common 6, Basic 15, Message 6, M2 28, Score 14, TUS 13, Utility 10, Trophy 8 | TUS (Set/Get Data, AddAndGet, MultiSlot), placares e game data (fantasmas), TSS vazio, M2 | corridas online, fantasmas de outros jogadores, mensagens |
| PCSE00491 Minecraft | Http 25, Net 8, NetCtl 14, Ssl 3, Adhoc 10, NpManager 14, Common 10, Basic 17, Message 5, M2 31, Score 15, TUS 15, Utility 19, SnsFacebook 5, Commerce2 30, Trophy 13 | TUS, placares, ticket com o DLC instalado | mundos online, loja de pacotes, ad hoc, Facebook |
| PCSE00606 Skullgirls 2nd Encore | Http 1, Net 16, NetCtl 1, NpManager 7, Basic 2, M2 25, Score 11, Utility 1, WebApi 5, WebApiCommonDialog 4, Trophy 7 | placares (inclui amigos = só o usuário), contexto M2 | lutas online (salas, signaling), convites WebApi |
| PCSE00914 99Vidas (Unity) | Http 26+2, Net 35+, Ssl 10+2, NpManager 14, Common 10, Basic 9, Message 5, M2 29, Score 17, TUS 15, Utility 26, WebApi 19, WebApiCommonDialog 8, Commerce2 30, Trophy 13 (UnityNpToolkit) | **Cross-Save via TUS confirmado**: no 1º boot GetData = sem dados, o jogo grava 16 KB em `np/tus/NPWR11170_00/<id>/data_1.bin`; nos boots seguintes lê de volta, sem o diálogo de erro de antes | modo online (salas), WebApi, loja |
| PCSE01444 Knightin'+ | Http 1, Net 1, NetCtl 1, Ssl 1, NpManager 5, Trophy 9 | troféus | — |
| PCSF00024 Gravity Rush | Http 27, Net 22, NetCtl 7, Ssl 2, NpManager 6, Basic 5, Activity 3, Score 11, Trophy 8 | placares (desafios) | tesouros/missões online (HTTP próprio) |
| PCSF00476 Tearaway | Http 20, Net 24, NetCtl 8, Ssl 4, NpManager 10, Common 6, Activity 3, Commerce2 2, SnsFacebook 5, Utility 2, Trophy 8 | ticket (as consultas de direito respondem com o DLC instalado) | site de papercraft, Facebook, loja |
| PCSG00198 Sakatsuku | Http 19, Net 9, NetCtl 9, Ssl 2, Adhoc 10, NpManager 9, Common 8, Basic 5, M2 19, Score 9, TUS 13, Trophy 7 | TUS (contexto com id NULL agora usa o do sceNpInit), placares, ticket (11 consultas de DLC: nenhum instalado), M2 | partidas online, ad hoc |
| PCSG00230 Sengoku Musou 2 Empires | Net 29, NetCtl 13, NpManager 7, Common 1, Basic 11, Message 5, M2 27, Score 14, TUS 21, Utility 2, Trophy 8 | TUS (MultiUser, TryAndSet, DeleteMultiSlot), placares e game data, M2 | online, mensagens |
| PCSG00845 Silverio Vendetta | Http 1, Net 1, NetCtl 1, Ssl 1, NpManager 1, Trophy 7 | troféus | — |

## O que foi exercitado nos testes (2026-10-29, dados de rascunho)

- **99Vidas** (`99vidas_boot.txt`, 2 execuções): passa pela pergunta do Cross-Save, faz upload e depois download do save
  pelo TUS local, chega ao tutorial igual ao main. Também usa Score (contexto criado) e o trophy setup.
- **WRC 4** (`wrc4_boot.txt`): sem regressão (com `--psn-local=off` e `on` o resultado é o mesmo; o roteiro de entrada
  termina na tela de nome do piloto em dados novos nos dois casos). Não chama TUS/Score no boot.
- **Sakatsuku** (`st_boot.txt`): chega à tela de título; o contexto TUS agora é criado (antes falhava com
  INSUFFICIENT_ARGUMENT porque o jogo passa NULL); `sceNpAuthGetEntitlementById` responde "não encontrado" (nenhum DLC).

## O que falta

- Internet de verdade (opcional, opt-in): falar com um servidor compatível (ex.: RPCN) para placares, TUS e
  Matching2 com outros jogadores; hoje tudo fica no PC.
- Ad hoc na LAN (`sceNetAdhoc*`, `sceNetAdhocMatching*`) entre duas instâncias do emulador.
- Commerce2 com loja vazia "de verdade": falta confirmar o protótipo/eventos do Vita; hoje a loja falha como
  servidor fora do ar.
- WebApi, mensagens, party, signaling entre jogadores.
