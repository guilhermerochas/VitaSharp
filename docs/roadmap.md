# Roadmap
  audio-check`): 245 e 157 estalos no celular
  Fps do shell main x f0e692f (agente C, 08/11/2026, boot-shell headless 60 s, 3 execuções de cada): f0e692f 60 fps
  cravados nas 3; a main (com as correções desta rodada) teve quedas (55,8/40,6; 29/5,7/30/42/46; 51/23/29/51) nas 3,
  mas o PC não estava livre: as quedas caem exatamente nos picos de 97-100% de CPU total, com csc de outro agente
  usando ~7 núcleos (470-727%) e outros VitaSharp/testhost rodando. Sem conclusão; repetir com o PC parado
  Filme do LBP com --video-decoder=hw x software (agente C, 08/11/2026, boot sem entrada, presents 2400-2620): o
  trecho é a tela de título (logo animado), não o filme movie.mp4 (nenhum quadro casa com ele). hw 12 achados
  (flashes A-B-A e 1 troca, bugsC3_data/captures/PCSA00017_20261108_154402_tudo), software 11 com quedas de fps
  (..._154608_tudo). Pista nova: com VITASHARP_DECODER_HISTORY=0 2 de 2 execuções a 30 fps cravados e 1-2
  achados (..._155052_tudo, ..._160414_tudo); com o histórico ligado 2 de 2 com quedas a 4-13 fps e 29-31 achados
  (..._155746_tudo, ..._160123_tudo), mas o jogo chegou em telas diferentes (889 x 507 cenas MSAA) e o PC estava
  carregado, então não dá para culpar o histórico ainda. Próximo passo: rerun de uma gravação (mesmo estado) com o
  histórico ligado x desligado. O uto continua em software
  Soccer Tsuku (rosto lento): não iniciado nesta rodada; continua precisando de uma gravação nova com a main
# Roadmap

Lista de itens futuros do VitaSharp. Marque `[x]` quando um item for concluído.

## Diagnóstico

- [x] Gravar tudo (F8) diz o contexto no próprio report.txt (pedido do usuário, 10/11/2026; agente snd1,
  fix/shell-audio): seção "Where" com onde o jogador está no começo e cada mudança durante a gravação (app/jogo na
  frente com título; no shell: tela de bloqueio, tela inicial ou a página de LiveArea de qual título, pela abertura
  dos arquivos `livearea/contents` do título e pelo botão PS; eventos `ctx` no input.tsv), os processos (na frente,
  suspensos), uma folha de contatos `frames.png` (12 quadros ao longo da gravação, com os tempos no relatório), e as
  últimas entradas antes da gravação (`input_before.tsv`, sempre guardadas: as últimas 32 mudanças de botão/toque).
  Áudio: cada WAV de porta com dono e papel (processo e thread que abriu: música do shell, sons da interface, jogo),
  tempo com som, a linha "no continuous audio stream (music)" / "continuous sound on <porta>", quem tem a porta BGM e
  os decodificadores; `context.txt` com tudo isso no começo e no fim. Testes em SessionCaptureTests

- [x] Teclas para alternar opções gráficas com o jogo rodando (MSAA real / antigo / desligado e outras)
  Feito em parte (07/11/2026, perf/desktop-11): `F1` / `Shift+F1` passam para a resolução interna seguinte / anterior
  (1x, 2x, 3x, 4x) e `F2` / `Shift+F2` para a taxa de quadros seguinte / anterior (nativa, fixa em 30, fixa em 60, sem
  limite), com aviso no canto da janela e na barra de título; valem só para a sessão (o perfil do jogo no launcher
  não muda). A resolução troca o renderizador com as threads do jogo seguras entre duas cenas da GPU
  (`SaveStates.ReplaceBackendBetweenScenes`, o mesmo caminho da carga de estado; 57-470 ms). Também como passos de
  script (`key@f2250:F1`). Validado no Sonic Mania em gameplay, sem janela e com janela oculta: 1x -> 2x -> 3x -> 2x
  (quadros gravados em 960x544, 1920x1088, 2880x1632, imagem certa logo depois de cada troca) e 30 fps entre os quadros
  2500 e 2600 no modo fixo em 30 (`perf11_out/hk_mania`, `hk_win`, `hk_sheet.png`). Falta: o MSAA (real /
  antigo / desligado) ainda é lido uma vez por processo (`VITASHARP_MSAA`, estático no renderizador); para trocar ao
  vivo ele precisa virar opção do renderizador como a escala
  Confirmado pelo usuário (08/11/2026)
- [ ] Killzone (PCSC00045) `kz_skin` a 1x morreu calado no quadro 3435 numa das 8 rodadas da perf/desktop-res
  (07/11/2026; sem exceção, `.err` vazio). Não reproduziu (perf/desktop-11): 6 rodadas seguidas, 3 com cache de
  shaders e do driver frios e 3 quentes, com outros emuladores disputando a CPU, todas até o quadro 4500 com código de
  saída 0 (23-29 fps, `perf11_out/kz_summary.txt`). O log de eventos do Windows não tem nenhum "Application
  Error"/WER do VitaSharp desde 06/11 15:14, então aquela rodada não caiu por exceção nativa nem do .NET (essas deixam
  evento, como os c0000374/c0000005 de 06/11): o processo foi encerrado de fora (TerminateProcess: um `Stop-Process`
  de outro agente, por exemplo) ou saiu sem passar pelo fim da rodada. O script daquela rodada lia `$p.ExitCode` de um
  `Start-Process -PassThru` sem guardar o handle (vem vazio), por isso não há código de saída. Agora toda saída
  normal do processo escreve `Process exit at guest frame N` no log: um log sem essa linha e sem `UNHANDLED` é um
  processo morto de fora ou em código nativo (ver o WER). Se repetir, rodar com `DOTNET_DbgEnableMiniDump=1` e
  guardar o código de saída (`perf11_tools/run.ps1` faz os dois)

## Save states

- [ ] Save state e runtime do Android (09/11/2026, laboratório): um estado salvo por um APK CoreCLR carrega em APKs
  Mono LLVM e NativeAOT do mesmo código, mas um APK mono-aot sem LLVM recusa ("salvo por outra versão", StateOtherVersion)
  embora o hash de SnapshotSchema seja igual na compilação: o hash em tempo de execução depende do runtime/AOT (ordem de
  reflexão ou trimming?). Deixar o hash estável entre runtimes, ou medir a compatibilidade no lugar do hash.
- [ ] Uncharted (PCSA00029) no Android congela depois de carregar um estado na tela de título (09/11/2026, main
  9f21653, APK CoreCLR do and_rt; estado do slot 1 salvo no gameplay do Prologue pelo mesmo APK): o log diz "Estado do
  slot 1 carregado" (0,8 s parado), mas o jogo nunca mais apresenta um quadro (frames presented parado em 1964, GPU
  submitted == completed, o áudio continua, tela parada no título, 0 fps). 2 de 2 (carga em 30 s e em 50 s). Logs no
  scratchpad do agente: `lab\uc_clr1\clr_0\last.log`, `lab\uc_clr2\clr_0\last.log`. Ainda não conferido no APK mono.
- [ ] F1 2011 (PCSE00002) no Android: carregar um estado durante os vídeos de abertura (30 s depois da primeira leitura
  do controle) falha com "thread 'AudioOutput' did not stop (in ); restart the game" (09/11/2026, main 9f21653, APK
  CoreCLR; log `lab\f1_clr1\clr_0\last.log` no scratchpad do agente); em 60 s carrega e roda. O roteiro de medida
  (`build/android-perf-run.sh`) carrega o F1 em 60 s por isso.
- [x] Save states mais visuais, no estilo do PCSX2 (pedido do usuário, 06/11/2026): uma tela de estados por cima do
  jogo pausado (desktop e Android), com uma grade/lista de slots, cada um com o print da tela no momento do
  salvamento, data e hora, tempo de jogo e a versão do VitaSharp; slot vazio identificado. Ações: salvar no slot
  escolhido (se já houver estado, pedir confirmação para **sobrescrever**, mostrando o print antigo e o atual),
  carregar (com o print grande antes de confirmar), apagar, e o salvamento/carregamento rápido de sempre. Atalhos no
  desktop (abrir a tela, anterior/próximo slot com o slot e o print aparecendo num aviso na tela, como o F2/F1/F3 do
  PCSX2) e navegação por controle, teclado e toque; estados de outra versão aparecem marcados como incompatíveis
  (não carregam). O launcher continua listando os estados do jogo com os mesmos prints. Base: os slots, prints e o
  menu de pausa do Android que já existem (`SaveStateControl`, `GameActivity.SaveStates`/`PauseMenu`)
  Parte feita (08/11/2026, feat/product-2, desktop): F6 abre o seletor por cima do jogo (`Host/SaveStateOverlay`:
  rápido + slots 1-9 com print, data e "Outra versão: não carrega"; abas Salvar/Carregar com Tab, L/R ou clique;
  setas/direcional, Enter/Cruz, F5 salva, F7 carrega, F6 próximo, Esc/Círculo fecha; substituir pede confirmação).
  F5/F7 rápidos e Shift+F5/F7 no slot escolhido continuam. Validado no Gravity Rush (janela oculta, roteiro: salvar
  no slot 2, substituir com confirmação, carregar o slot 2 pela aba Carregar, o jogo segue a 30 fps). Falta: pausar
  o jogo com a tela aberta, apagar, print grande ao carregar, tempo de jogo/versão no slot, e o Android
  Feito (08/11/2026, feat/product-3, desktop): o F6 pausa o jogo (pausa interna, sem o selo "Pausado") depois de
  tirar o print da tela atual e retoma ao fechar; Del/Backspace/Triângulo apaga o slot (pergunta antes); carregar pelo
  seletor mostra o print grande e pergunta; substituir mostra o print salvo e o atual lado a lado; cada slot mostra o
  tempo de jogo (segundos do relógio do jogo) e a versão. Os prints dos estados passaram a 480x272 (screenshot do
  renderer, o sink de 96x54 fica de reserva); o launcher mostra o tempo de jogo no card do estado. Validado no Sonic
  Mania (janela oculta, teclas postadas na janela): salvar, substituir, carregar (60 fps depois), apagar, F5/F7
  rápidos; frames +0 com o seletor aberto (`prod3_out/ss4/ov`, `prod3_out/ui/game_dark.png` na pasta de trabalho local). Falta: o
  Android (a tela de estados por cima do jogo no menu de pausa, validar no Killzone no celular)
  Confirmado pelo usuário (08/11/2026)

- [x] Salvar e carregar o estado inteiro do emulador: CPU (registradores, NEON/VFP, monitor exclusivo), memória,
  objetos do kernel, estado dos módulos HLE (por módulo, `ISnapshotState` gerado na compilação com as partes à mão,
  decisão do usuário de 01/11/2026) e a GPU (o renderer é recriado e reconstrói os caches da memória do jogo). Feito
  (8efb2d5..3b45916, desenho em `docs/notes/save-states.md`, inventário em `docs/notes/dev-snapshots.md`). Validado
  no desktop em 04/11/2026 com 16 jogos (tabela "Tested" em `save-states.md`) e no celular (Knightin'+, Uncharted,
  F1: `docs/notes/android-frame-anatomy.md`, main 1c0e516). 06/11/2026 (feat/savestate-tolerant): Tearaway salva e
  carrega (antes a carga era recusada com a cena da GPU aberta e o salvamento com uma command list sendo gravada),
  carregado duas vezes seguidas no modo de depuração, 30 fps e o boneco anda com o direcional depois das cargas, sem
  device lost (`ss1_tear5`); LoadExec depois de uma carga: estado no menu da Jak and Daxter Collection,
  carregado, Cross no Jak 1 e o Jak 1 chega ao PRESS START (`ss1_jak1`); God of War Collection: estado no
  menu, carregado, God of War 1 até o menu do jogo a 30 fps (`ss1_gow1`)
- [x] Estado salvo depois de um LoadExec carrega numa sessão nova (06/11/2026, fix/savestate-loadexec): o programa do
  estado é o primeiro módulo dele (`SaveStates.ProgramOf`); `--load-state` (launcher, `rerun` do Gravar tudo) boota esse
  self direto; F7/slot com outro self rodando faz o LoadExec antes e carrega após 120 quadros; os módulos são achados
  por arquivo e endereço e ficam com os UIDs salvos. Junto: `sceGxmDisplayQueueAddEntry` com a fila cheia não espera
  para sempre quando a thread é parada (o LoadExec pedido pelo host esperava 10 s e seguia com a thread antiga viva).
  Validado: Jak 1 e God of War 1 em gameplay salvos, carregados em sessão nova pelos dois caminhos, 20 e 30 fps como
  ao salvar (`ss2_jakB`, `ss2_jakC4`, `ss2_gowB`, `ss2_gowC6`); Uncharted (o eboot.bin dele também faz
  LoadExec) a 59 fps (`ss2_ucB`); o rerun da gravação do usuário `PCSA00080_20261106_002008_tudo` (build da época
  0d298b4 + estas mudanças, o esquema da main mudou depois) carrega e 98% dos quadros saem iguais
  (`ss2_cap/.../reruns`). Os argumentos do LoadExec não são repassados (a memória salva os substitui). Android
  (06/11/2026, perf/android-8, ba51978): o Carregar estado do menu de pausa faz o mesmo `SwitchProgramFor` (a emulação
  volta a rodar durante o LoadExec, o menu a tinha pausado). Validado no celular: estado salvo no GOW1 da coleção do
  God of War, jogo reaberto no menu da coleção, slot carregado pelo menu: o log mostra `LoadExec app0:GOW1.self` e a
  carga, o jogo segue do ponto salvo e responde (`and8_tools/shots/g4_loadpage.jpg`, `g6_loaded.jpg`,
  `g7_after.jpg`); Killzone (sem LoadExec) salvo e carregado no gameplay pelo mesmo caminho em todas as corridas de
  medida. O Jak and Daxter Collection não está no celular
- [x] Estados de outra versão: decisão do usuário (05/11/2026): estados de outra versão são recusados com aviso claro
  (não há formato tolerante; `SnapshotSchema.ManualVersion` + o hash dos layouts gerados decidem). Aviso em português
  ("Este estado foi salvo por outra versão do VitaSharp (1.0.0+57c57b1) e não pode ser carregado"), 15 s na barra de
  título, dica do slot no launcher e aviso no Android (agente C dia 6, `bugsC6_out/ss_old/log.txt`).
  Visto na janela (06/11/2026): o estado do Jak 1 da 57c57b1 com `--load-state` mostra o aviso na barra de título de
  8,2 s a 24,1 s e o jogo segue a 30 fps (`ss1_oldstate/titles.txt`, `window_0.png`)
- [x] Interface: F5/F7 (rápido), F6 escolhe o slot 1-9, Shift+F5/F7 no slot; o launcher lista os estados do jogo com
  miniatura e abre o jogo carregando um; Android: "Salvar estado"/"Carregar estado" no menu de pausa, 6 slots com
  miniatura e data (24a3c6f; carga e salvamento por passos no celular em 9c288d7)
- [x] Uso em diagnóstico: o Gravar tudo (F8) leva o estado do início (`state_start.vss`) e o da pré-gravação
  (`state_preroll.vss`), e `rerun` recomeça dele com a entrada gravada (FIFA 13: 95% dos quadros iguais, mediana da
  diferença 0; `docs/notes/session-capture.md`)
- [ ] Sem teste por falta de um jogo instalado que use: Atrac, Sas, Audioenc/Videoenc/Mp4Recorder, G729, Location,
  NetAdhocMatching/Rudp, TriggerUtil e o `sceIme` (teclado do jogo sem o diálogo do sistema); esses módulos recusam o
  salvamento só enquanto estão em uso. Sob o shell do Vita (vários processos) o salvamento é recusado
- [x] Não validado (06/11/2026): o device lost do Tearaway na janela (o relato de 04/11 não reproduziu em 3 cargas
  sem janela; não houve teste com janela); o teclado do sistema (ImeDialog) aberto num save: o Sakatsuku
  (`st_game2`, 7000 quadros) não chegou ao teclado, e o Invizimals não foi testado
  Confirmado pelo usuário (06/11/2026): ok
- [x] Jak and Daxter Collection, menu (antes de qualquer save): a GPU parou ~20 s num `FinishCommand` no quadro 1076
  (watchdog STALL, quadro 1 da GPU não retirado) e depois seguiu; com outros agentes usando a GPU ao mesmo tempo, causa
  não investigada (`ss1_jak1/stdout.txt`)
  Concluído por decisão do usuário (06/11/2026): Jak and Daxter fechado, exceto o save state no gameplay (item abaixo)

## Desempenho (PC)

- [x] Onde o código do guest roda (perf/il-1, 11/11/2026): linha nova `CPU tiers` no relatório do fim da execução
  (instruções executadas no código nativo, nas callouts, no backend IL e no interpretador, com o estado dos blocos
  interpretados) e os motivos de rejeição por backend. Desktop x64, cenários inteiros (boot incluído): Killzone 99,40%
  nativo, Uncharted 99,54%, F1 2011 99,88%, Sonic Mania 99,77%, Gravity Rush 99,61%, Silent Hill 4 98,26%, Persona 4
  Golden 99,43%; o backend IL roda 0,00% em todos e o resto é o interpretador em blocos frios (ainda abaixo do limiar
  de 2.000 instruções ou na fila de compilação); 0 blocos rejeitados pelo nativo (o único motivo possível é falta de
  slots de spill). No Android a cadeia é `native > interp` (logs do laboratório: 0 rejeições, 100% inline). **Descartado
  com esses números:** otimizar o IL emitido (cache de registradores em locais, flags preguiçosas, menos checagens de
  memória, constantes do PC): o backend IL não roda código nos jogos, então não há fps a ganhar. Fica o modo de dump
  (`VITASHARP_IL_DUMP`, docs/notes/il-jit.md) para quando o IL voltar a ser usado (`--cpu=il`, watchpoints).

- [ ] Avaliar o .NET 11 para desempenho (pedido do usuário, 09/11/2026), desktop e Android: ler as notas
  oficiais de desempenho do .NET 11 (blog "Performance Improvements in .NET" e release notes: JIT/RyuJIT ARM64 e x64,
  NativeAOT, GC, SIMD/Vector, interop) e listar o que toca o emulador (JIT do guest em C#, renderer, HLE, áudio).
  Depois medir de verdade, sem trocar a main: build numa branch com o SDK novo, desktop (Killzone, Uncharted, F1, Sonic
  Mania: entradas HLE/ms de CPU por quadro) e Android NativeAOT no celular (protocolo do laboratório:
  `build/android-perf-run.sh`, Killzone na escada, F1 corrida, fps de vazão real). Hoje: .NET 10.0.9, Android em
  NativeAOT (experimental no 10; ver se fica estável/maduro na versão nova e se as três correções de build do NativeAOT
  ainda são precisas). Só trocar se ganhar de forma estável nos dois
- [x] Resolução interna maior, no estilo do PCSX2 (pedido do usuário, 07/11/2026), no desktop: "Resolução interna" por
  jogo no painel do jogo do launcher (1x nativa padrão, 2x, 3x, 4x; `--res-scale=2|3|4`, `VITASHARP_RES_SCALE`),
  estendendo `VulkanRenderer.ResScale.cs` a fatores inteiros acima de 1. Acima do nativo, texturas de stencil e as
  linhas escaladas dos alvos reinterpretados (G-buffer do Uncharted) passam por buffers próprios (não cabem na memória do
  guest); writeback e cópias da CPU continuam na resolução nativa; dumps de quadro e prints saem na resolução interna.
  Validado (perf/desktop-res): `replay-gpu --compare` 1x idêntico ao main em 20 dumps (582 quadros), 2x sem erro em
  todos; gameplay em 2x de Killzone, Uncharted, Gravity Rush, Sonic Mania e FIFA 13 (dump de partida), 4x de Killzone e
  Sonic Mania. Varredura 1x x 2x de perf/desktop-14 (08/11/2026, cenários sem janela com o mesmo roteiro, e
  `replay-gpu` de uma partida do FIFA 13): sem defeito de escala novo em Uncharted (gameplay), Killzone (treino VR),
  FIFA 13 (partida), Injustice (luta), Croixleur Σ (seleção com modelo 3D), Soccer Tsuku (secretária 3D), Sonic Mania,
  LBP (cápsula), Jak 1 (menus), Tearaway (menus), Gravity Rush (título) e Minecraft (menus); os cenários do Jak,
  Tearaway, GR e Minecraft não chegam ao gameplay com os saves atuais.
  `gl_PointSize` escalado desde 07/11/2026 (perf/desktop-11): os programas de vértice que escrevem o
  tamanho do ponto multiplicam por `rinfo.res_multiplier` numa variante com chave própria (`ShaderHints.ScaledPointSize`),
  então só esses shaders são traduzidos de novo; o cache dos outros continua valendo (sem subir a versão do gerador).
  Junto: as contagens dos testes de visibilidade (INCREMENT) de cenas escaladas são divididas por k*k (vinham k*k vezes
  maiores). Conferido: `replay-gpu --compare` dos 18 dumps idêntico ao main em 1x e em 2x; o Uncharted desenha 334
  pontos por quadro num stencil de 720x408 (`perf11_cmp/vis_trace2`, não aparecem nos quadros gravados); o
  Killzone em 2x (`kzvis2`) dá contagens de visibilidade na mesma ordem das de 1x (`kzvis1`, 10-25 mil) com o sol e as
  luzes normais. Nenhum jogo instalado com sprites de ponto visíveis foi achado (The Deer God: 12 programas com
  tamanho de ponto, Killzone 0, pelo `shadertest`)
  Confirmado pelo usuário (07/11/2026)
- [ ] Resolução interna maior no Android (opção por jogo nos ajustes e aviso de custo de GPU), depois de resolver o
  desempenho do Android (ordem do usuário, 07/11/2026); o renderizador já aceita 2x-4x
- [x] Taxa de quadros por jogo (pedido do usuário, 06/11/2026): uma opção **por jogo** (não global) com **Nativo**
  (padrão: o jogo roda exatamente como hoje, nada muda), **Fixo em 30**, **Fixo em 60** e **Sem limite**. Exemplo: o
  Jak 1 roda a 20 fps no Vita (o jogo pede 3 vblanks por troca); com a opção, travar em 30, rodar a 60 ou liberar.
  Onde atua: no intervalo de vblank que o jogo pede (`sceDisplayWaitSetFrameBufMulti`/`WaitVblankStartMulti`,
  `sceDisplaySetFrameBuf` IMMEDIATE, a fila de display do GXM e o ritmo do vblank emulado), nunca no código do jogo.
  Cuidados: jogos cuja lógica anda por quadro ficam mais rápidos ou mais lentos (avisar na descrição da opção); o
  modo Sem limite também solta o ritmo do vblank; o áudio continua no tempo real. Guardar no perfil do jogo (como o
  modo do mouse), no launcher do desktop (página/ajustes do jogo) e no app Android (ajustes do jogo e menu de pausa),
  com o modo atual no overlay de FPS
  Confirmado pelo usuário (08/11/2026)

  Desktop feito (perf/desktop-res, 07/11/2026): "Taxa de quadros" no painel do jogo do launcher, `--frame-rate=native|30|60|unlimited`
  (`VITASHARP_FRAME_RATE`, `FrameRate` em Hle/Modules/Display.cs): os modos fixos limitam os vblanks que as esperas
  de display pedem e seguram a troca que vem antes do intervalo; Sem limite pula as esperas de display. Modo no título da
  janela ao lado do FPS. Medido: menu do Jak 30 -> 59 fps em Fixo 60, Sonic Mania 60 -> 30 em Fixo 30, Sem limite 121 fps
- [ ] Taxa de quadros por jogo no app Android (ajustes do jogo e menu de pausa, com o modo no overlay de FPS): o núcleo
  já tem `FrameRate`; falta a interface (Android parado por ordem do usuário, 07/11/2026)
- [ ] Reflexão o mínimo possível (desktop e Android; ajuda o Native AOT, o Mono AOT e o tempo de início). Hoje
  (31/10/2026): `GetMethod(s)`/`GetProperty(s)` em 3 arquivos, `GetField(s)` em 1, `GetCustomAttribute` em 2,
  `Enum.GetValues/GetNames/Parse` em 5, `Assembly.GetTypes/GetExecutingAssembly` em 1 e `Reflection.Emit` em 3 (o
  backend IL do JIT, que precisa dele). Análises e trocas:
  - [x] Auditar cada uso e trocar por geradores de código (source generators) ou tabelas fixas: atributos lidos em
    tempo de execução viram tabelas geradas na compilação; enums com `switch` gerado em vez de `Enum.ToString/Parse`
    Auditado (07/11/2026, perf/desktop-11): não sobrou reflexão de verdade fora do backend IL. As chamadas `[Hle]` já
    vêm do gerador (`VitaSharp.Core.Generators`); os `GetProperty` são do DOM do `System.Text.Json` (`JsonElement`),
    não reflexão; `Enum.GetValues<T>`/`Enum.Parse<T>`/`TryParse<T>` genéricos funcionam no Native AOT e só rodam em
    relatórios, scripts e na leitura de configurações; `GetCustomAttribute` só lê a versão do assembly;
    `Type.GetType("Mono.RuntimeStructs")` (constante, analisável) detecta o Mono; `typeof(T).GetFields()` do relatório
    de recursos do Vulkan tem `[DynamicallyAccessedMembers]`. Nada barato a trocar
  - [x] Ligar os analisadores de trimming/AOT (`IsAotCompatible`, `EnableTrimAnalyzer`, `EnableAotAnalyzer`) em todos
    os projetos com aviso tratado como erro, para nenhum uso novo entrar sem ser visto (IL2026/IL3050)
    Feito (07/11/2026): Core, Renderer.Vulkan e Android.Common já tinham `IsAotCompatible` e o Desktop os
    analisadores; o `Directory.Build.targets` agora transforma os avisos de trimming/AOT/arquivo único (IL2026, IL2046,
    IL2055-IL2095, IL2111, IL3000-IL3002, IL3050/IL3051) em erro nesses projetos. O app Android fica de fora (a análise
    dele só roda no build longo do APK)
  - [x] Conferir o JSON: todos os `JsonSerializer` com contexto gerado (sem o serializador por reflexão como reserva;
    `JsonSerializerIsReflectionEnabledByDefault=false`)
    Feito no desktop (07/11/2026): todas as chamadas já passavam contexto gerado ou `JsonTypeInfo`; o
    `VitaSharp.Desktop.csproj` desliga o serializador por reflexão (`runtimeconfig.json` com a chave). Conferido: o
    jogo abre e lê o `input.json` de perfis (teclado personalizado + 1 controle; `perf11_out/json_win`). No app Android falta pôr a mesma propriedade e testar no celular (Android parado)
  - [ ] Inicialização: medir o tempo de início (desktop JIT/AOT e app Android) e o que roda nos construtores estáticos
    (tabelas de HLE, NIDs, formatos) — gerar na compilação o que hoje é montado ao iniciar
  - [ ] Caminhos quentes sem custos escondidos: boxing, `params object[]`, interpolação de string em logs desligados
    (usar `[InterpolatedStringHandler]`/checar o nível antes), LINQ e lambdas que alocam por quadro, `ToString()` de
    enums em logs; medir com o contador de alocações (`PERF allocated`) e zerar o que for por quadro
  - [ ] Backend IL do JIT (único uso real de `Reflection.Emit`): manter só onde não há JIT nativo e documentar
- [x] Instalação de `.pkg` + `work.bin` muito lenta no build Native AOT do desktop (relato do usuário, 31/10/2026).
  Medido com o WRC 4 (1,5 GB, 195 arquivos PFS): o AOT não tinha caminho lento próprio (CPU 2,7-3,8 s no AOT, 4,7 s
  no JIT); o tempo era E/S. O pacote era extraído ainda cifrado em PFS e cada arquivo era relido e regravado no
  lugar setor a setor (32 KB por leitura/gravação com seek): 3 passadas no disco, 10-56 s no AOT conforme a carga e
  o cache (a fase PFS sozinha 8-30 s). Agora files.db/unicv.db são lidos antes, cada arquivo é casado com sua
  tabela pelo 1º setor e lido, decifrado (CTR + PFS) e gravado uma vez em blocos de 4 MB, com a gravação numa
  thread própria (pré-alocada, sem buffer) enquanto o próximo bloco é lido; threads abaixo do normal. AOT depois:
  1,7-6,8 s (leitura ~3,1 GB/s, AES-CTR ~4 GB/s, PFS ~8,5 GB/s; a gravação, 240-1160 MB/s, é o limite). Arquivos
  instalados idênticos (SHA-256 dos 207 arquivos). VPK no mesmo pipeline; progresso com MB/s e tempo restante no
  launcher e no Android, no máximo a cada 100 ms
- [ ] Engasgos da primeira vez: montar os pipelines que faltam com `VK_EXT_graphics_pipeline_library` (ligação
  rápida, sem otimizar, no thread da GPU) e trocar pelo otimizado em segundo plano; prever também as variantes
  tile-word e de saída inteira. Pular o desenho foi analisado e descartado: quase nada do que trava pode ser pulado
  sem risco (Killzone 0 de 91 engasgos, Tearaway 1 de 14), ver `docs/notes/pipeline-skip.md`
  Tentado em 07/11/2026 (perf/desktop-res) e descartado: no Killzone com cache frio o thread da GPU passou a montar
  mais pipelines (18,8-21,4 s contra 5,0-10,7 s; os mesmos ~90 quadros acima de 16,7 ms), porque no driver da NVIDIA
  a biblioteca de shader custa quase um pipeline inteiro e o Killzone tem tantos shaders quanto pipelines. O limite é a
  vazão de compilação (2 workers); ver `docs/notes/pipeline-skip.md`. Próximo passo, se voltar: mais workers ou
  pipelines.list pré-gerado por jogo
  Rodada de 08/11/2026 (perf/desktop-12; Injustice `inj_fight` novo cenário com luta no Treino, Killzone `kz_skin`,
  FIFA 13; GTX 1650, 4 CPUs fixadas, caches frios = `--shader-cache` e cache do driver NVIDIA novos). Antes: Injustice
  frio 61-83 desenhos travados (5,4-6,6 s) em 33-40 quadros, Killzone frio 375 desenhos (13,5 s), 95 quadros >= 16,7 ms.
  Onde está o tempo: o driver da NVIDIA leva 60-90 ms por pipeline (83 ms medidos no replay, sozinho, sem disputa) e
  quase cada pipeline é um par de shaders novo (Injustice 115 pipelines / 105 pares, Killzone 552 / 533); o shaderc em
  `zero` leva 2-12 ms por shader. O jogo pede o pipeline ~1 quadro depois do prefetch, então cada pipeline novo custa
  quase os seus 60-90 ms. Medido e descartado: `--shaderc-opt=perf` (driver 83 -> 59 ms por pipeline, mas shaderc 12 ->
  80 ms por shader: pior), sem `VkPipelineCache` (sem ganho; não serializa), 4 workers em 4 CPUs (mesmos engasgos, CPU
  cheia), normalizar a chave do pipeline (estado de blend desligado, sem cor, layout de vértice: 2 duplicatas em 115,
  3 em 552). Ubershader: não cabe (USSE arbitrário). Feito: shaderc e o tradutor aquecidos em segundo plano (o 1º shader
  novo de uma sessão com cache quente: ~20 ms em vez de ~150-190 ms; 08aede3), prewarm também nos workers (cache do driver
  frio: Killzone 21-22 s -> 11 s; 5ca2e0a) e o renderizador trocado (F1/F3/load state) passa a abrir os caches do jogo
  (7d73f91). O que sobra no cache frio é o custo do driver por pipeline novo; só cai pulando desenho (descartado) ou
  com menos SPIR-V por shader (gerar GLSL menor: o `-O` mostrou ~30% a menos no driver)
- [ ] Prioridade das threads em CPU híbrida (núcleos E) e na bateria: medir `SetThreadSelectedCpuSets` e o
  power throttling num Intel de 12ª geração ou mais novo (as threads da CPU emulada e da GPU já sobem para acima do
  normal, `--host-prio`; ver `docs/notes/thread-priority.md`)
- [ ] Queda para 50-54 fps no menu do Sonic Mania
  Medido (07/11/2026, perf/desktop-11, `mania_boot` com `--profile-from=1520 --frames=1660`, menu principal e seleção
  de save): 53,4 fps, quadro p50 17,5 ms. Não é a GPU nem o renderizador: a thread `pthread` (o renderizador por
  software do RSDKv5) executa 5,2 M ciclos emulados por quadro (1,07 ciclo/instrução, 720 K blocos) e fica 59% rodando
  + 24% no ritmo da CPU emulada + 17% esperando a `main`. O jogo não chama `scePowerSetArmClockFrequency` (nem
  importa ScePower), então roda nos 333 MHz padrão: 5,2 M ciclos = 15,7 ms de CPU por quadro, mais a espera, passa de
  16,7 ms. Prova: com `--cpu-mhz=444` 59,4 fps (p50 16,6 ms) e sem ritmo (`--cpu-mhz=0`) 59,6 fps cravados, mesmo
  número de ciclos. Os dados do usuário têm `pixWidth=480` e o mod UltrawideMania (mais pixels por quadro que os 424 do
  jogo). Nada mudado: acelerar só este caso seria mexer no modelo de custo (`BlockBuilder.CycleCost`) ou no relógio
  padrão de todos os jogos (o Silent Hill depende dos 333 MHz). Opções para o usuário decidir: uma opção por jogo de
  "Relógio da CPU" (333 / 444 / sem ritmo) como a taxa de quadros, ou conferir num Vita de verdade se este menu com o
  mod e 480 de largura fica a 60 a 333 MHz (se ficar, o custo do laço de pixels está alto no modelo)
- [ ] Opção por jogo "Relógio da CPU" (333 MHz / 444 MHz / sem ritmo), como a taxa de quadros por jogo: **pendente da
  decisão do usuário, não implementar antes** (anotado em 08/11/2026, perf/desktop-12). Motivo: o menu do Sonic Mania
  (item acima) e outros jogos presos na CPU emulada a 333 MHz; hoje só --cpu-mhz=/VITASHARP_CPU_MHZ mudam isso
- [ ] Sonic Mania: travadas de 3-5 s nos carregamentos (quadros ~1131 e ~1856 do `mania_boot`, 6-17 fps nesses 5 s)
  (perf/desktop-14, 08/11/2026). Reproduz com a CPU livre, em 1x e 2x e também sem ritmo de CPU (`--cpu-mhz=0`), então
  é tempo do host, não o relógio emulado. Precisa de `ux0:data/Mania` na pasta de dados (sem ele a tela fica preta).
  Perfil (dotnet-trace na travada): a `pthread` passa ~77% e a `main` ~22% do tempo dentro de uma mesma chamada HLE
  (provavelmente `sceKernelLockLwMutex`: 27 M lock/unlock no `mania_boot`), boa parte gastando CPU. Tentado: entregar o
  mutex direto ao primeiro da fila no unlock (como o `Mutex::serve` do Vita3K e o PPSSPP): piorou (98 s contra 70 s,
  mais travadas), revertido (patch na pasta de trabalho local `perf14_tools/mutex_handoff_wip.patch`). Próximo passo: contar as
  esperas com disputa por mutex/thread (nome, quem segura) na travada, e ver se o custo é o `Latency()` de 30 us
  por espera acordada ou o giro do `Monitor`
  Medido (perf/desktop-15, 08/11/2026): **não é o lwmutex**. Contando as esperas com disputa num `mania_boot` inteiro
  (contador temporário no `Mutex.Lock`): 2 locks com disputa em 585 mil; o perfil antigo (dotnet-trace) atribuía o
  tempo à função errada. O perfil do próprio emulador (`--profile-hle --profile-from=1050`) na travada do quadro 1131
  (6,2 s): 3,1 s de E/S de arquivo (a `main` em `sceIoRead`), o resto trabalho da própria `main` (ritmo da CPU a
  333 MHz 1,5 s, ~4.700 `sceClibMemcpy` e ~4.600 `sceKernelGetTLSAddr` por quadro) e a `pthread` esperando a `main`
  num semáforo. **Corrigido em parte** (perf/desktop-15, `IoTiming` com janela de leitura antecipada de 64 KB por
  arquivo): o modelo de tempo do cartão cobrava 0,5 ms de acesso em cada leitura, e o carregador lê o Data.rsdk de
  200 MB em milhares de pedaços pequenos (18.600 `sceIoRead`); agora a leitura que erra traz a janela e as seguintes
  dentro dela não custam tempo de dispositivo (leituras grandes e aleatórias cobradas como antes; o Vita3K não tem
  tempo de E/S nenhum). Travada do 1131: 6,2 s -> 4,2 s (E/S 3,1 s -> 0,1 s); `mania_boot` 69,0 s (3 rodadas) ->
  66,5-66,9 s (3 rodadas), igual a `--io-speed=0`; gameplay em Green Hill normal. A entrega direta do mutex (fila
  FIFO/prioridade como Vita3K/PPSSPP) com 20 us de giro antes de dormir não mudou nada (69,0 x 68,8 s), não entrou
  (patch guardado fora do repositório). Falta (próximo passo): o custo de host das
  chamadas HLE triviais (`sceClibMemcpy`/`sceKernelGetTLSAddr` como intrínsecos no JIT em vez de sair do código
  nativo, como os "HLE inline" do Ryujinx/dynarmic) e os 1,5 s de ritmo da CPU emulada, que um Vita também gastaria
  Feito em parte (perf/desktop-16, 08/11/2026): `sceKernelGetTLSAddr`, `sceKernelGetThreadId` e as cópias/preenchimentos
  de até 256 bytes de `sceClibMemcpy`/`_safe`/`Memmove`/`Memset` viraram código ARM do guest (`Hle/HleGuestCode`, como
  a tabela de substituição do PPSSPP e a leitura inline do TPIDRRO_EL0 do ARMeilleure), compilado pelos dois backends
  nativos; cópia com destino dentro da origem (memmove para trás) e as maiores continuam no C#. `mania_boot` (2 rodadas
  de cada, mesmo binário, `VITASHARP_HLE_GUEST_CODE=gxm` = antes): entradas no código nativo da `main` 6.340 -> 1.830-1.990
  por quadro, `sceClibMemcpy` em C# 7,37 M -> 0,90 M chamadas, CPU da `main` 2,62-2,79 -> 2,42-2,56 ms/quadro, rodada
  67,0/66,7 s -> 65,5/65,3 s. Medido e descartado: chamar o HLE de dentro do código nativo também no x64 (callout,
  como no ARM64): 65,6/65,3 s, igual. O que sobra na travada é o ritmo da CPU emulada (opção "Relógio da CPU", pendente)
  Também: FIFA 13 (tela de título; o roteiro não passa dela com os dados de teste) CPU do processo 12,0 -> 10,5 ms/quadro
  (6,4 M `sceKernelGetThreadId` saíram do C#), Injustice 26,2 -> 24,7. Depois, os setters/getters da palavra de controle
  de textura da libgxm (`sceGxmTextureSet{U,V}AddrMode`, `Set{Mag,Min,Mip}Filter`, `SetLodBias`, `SetLodMin`,
  `GetType`) e `sceGxmProgramGetDefaultUniformBufferSize` também em código do guest (`=notex` desliga, A/B): no boot do
  Jak 1 a `F3DPlaybackThread` cai de 28.700 para 14.900 entradas no código nativo por quadro e de ~5,1 para ~4,7 us de
  CPU por desenho (o jogo fica nos 20 fps dele). Um save state carregado mantém os stubs de SVC de quando foi salvo (o
  código do guest só entra num boot novo). O que ainda sai do código nativo em quantidade: lwmutex (objetos do host),
  `sceKernelGetProcessTimeWide` e os setters de estado do contexto da libgxm (estado em C#)
- [x] Rodadas 17-18 de desempenho (perf/desktop-17, 09/11/2026): HLE que vira código do guest (a chamada não sai do
  código nativo; no celular cada saída é uma transição nativo -> gerenciado). Medido headless no PC com a máquina a
  87-100% de CPU por builds do Android (fps e ms/quadro viraram ruído; o que vale é entradas no código nativo/quadro):
  (1) lwmutex com o estado só na área de trabalho do guest (`Kernel/Sync.cs`: +0 UID do dono com bit 31 = alguém
  dorme, +8 contagem, +16 UID, 0 depois de apagado; protocolo de futex), lock/trylock/unlock sem disputa e
  `sceKernelSignalLwCond(All)` sem ninguém esperando em código do guest (LDREX/STREX), o resto no C#;
  `VITASHARP_HLE_GUEST_CODE=nolw` desliga. Killzone `kzl_walk3`: `killzone_vita` 2.587-2.674 -> 1.886-2.075
  entradas/quadro (sem as ~640 + 640 chamadas de lwmutex), JobSched ~560 -> ~470; Sonic Mania `main` 1.863 ->
  1.112 (-40%), CPU da `main` 3,31 -> 3,17 ms/quadro; FIFA 13 e Uncharted quase não usam lwmutex. (2) Os setters do
  contexto da libgxm (programas, índices de visibilidade, depth func/write, fragment enable, two-sided, stencil
  func/ref, region clip, viewport: 17 funções) gravam no próprio contexto imediato (128 bytes do guest com uma marca
  em +11; `GxmContext.SyncGuestState`/`PublishGuestState`, o C# lê antes de desenhar/reservar/executar listas/salvar);
  contextos diferidos e contextos de save states antigos (64 bytes) continuam no C#; `=nogxmctx` desliga. Killzone:
  JobSched 513-570 -> 223-257 entradas/quadro (CPU 2,72 -> 2,03 ms/quadro por thread), `killzone_vita` 2.007 ->
  1.713. Uncharted: ~1.500 das 3.600 entradas/quadro da thread principal eram esses setters. (3) Também em código do
  guest: as consultas de parâmetro de programa GXP, `sceGxmPrecomputedDrawSetVertexStream` e
  `sceGxmSetUniformDataF` de arrays F32 de 2 ou 4 componentes (Uncharted mandava ~530/quadro ao C#). (4) Save state:
  ao carregar, `Linker.RebindHleStubs` + `HleGuestCode.Replace` religam os stubs de HLE às rotinas desta build (609
  stubs no Killzone); validado em gameplay do Killzone: salvar e carregar na mesma build e carregar com o código do
  guest ligado um estado salvo com `=nogxmctx`. Bug achado no caminho (corrigido antes do hash): as reservas do uniform
  buffer padrão liam o programa antigo (antes da sincronização), e os textos do Killzone saíam como blocos. Descartado: `sceKernelGetProcessTimeWide` como código do guest (o
  valor depende da posição emulada da thread e do máximo monotônico entre threads). Achado: o F1 2011 chama
  `sceKernelGetProcessTimeWide` ~2.200 vezes por quadro (51% das entradas da thread principal), em espera ativa
  (cresce com o tempo livre): no PC não custa fps; no celular é uma transição por chamada (candidato a intrínseco no
  JIT ARM64). Falta: medir no celular (APK mono-aot, `env.txt` com `VITASHARP_HLE_GUEST_CODE` vazio / `nolw` /
  `nogxmctx`, Killzone, Uncharted e F1: ms da thread do jogo e fps).
  (5) Arena de uniforms (`Gpu/UniformArena`): os blocos ficam zerados e quem escreve marca o que escreveu (grânulos de
  256 bytes); o bloco reciclado limpa só isso, em vez de a thread do jogo zerar a fatia inteira de cada draw (8 KB por
  buffer de tamanho desconhecido). F1 2011 na corrida (2 pares, `VITASHARP_UB_ZERO=full` = antes): `sceGxmDrawPrecomputed`
  7,6/4,6 -> 2,2/1,6 us por chamada, CPU dentro do HLE da thread principal 13,2/6,6 -> 4,9/3,1 ms por quadro.
  Achados do worker da GPU (perfil por unidade de textura, só desenvolvedor): na corrida do F1 o custo por draw está
  espalhado (gravação 1,0, geometria 0,7, uniforms 0,7, unidades de textura 0,8 ms por quadro para ~1.400 draws); um
  memo de unidades resolvidas acerta 74-83% das unidades (no desktop pouco ganho; entrou para o celular, onde a
  resolução de unidades era 17 de 49 ms do worker: `VITASHARP_UNIT_MEMO=0` desliga). (6) Worker da GPU: memo da
  entrada de shader por programa e formatos de textura (fase Shader 0,285 -> 0,241 ms no KZ desktop;
  `VITASHARP_SHADER_MEMO=0`), extensões de stream por programa, e cache de conteúdo de texturas: o Killzone passa
  texturas pela mesma memória (26.000 despejos "substituída por outra" em 6.500 quadros) e reenviava a textura antiga
  quando ela voltava (no celular, transcodificação ASTC na CPU); a imagem despejada (e a de um conteúdo que a textura
  deixa, da segunda troca em diante) fica guardada pelo conteúdo (48 MB) e volta sem upload: uploads 2,12 -> 0,96 por
  quadro, tempo de upload 1,44 -> 0,49 ms (`VITASHARP_TEX_CONTENT_CACHE=0`). Perfil do worker (só desenvolvedor):
  `VITASHARP_WORKER_PROF_FROM=N` começa a medir no quadro N; as unidades de textura são divididas em memória/RT/outras e
  o caminho de falta em lookup/checagem/hash/upload; a linha PERF texcache traz ms de upload e conteúdos repetidos.
  (7) GPU do Mali (rodada 19): sem `shaderClipDistance` todo fragment shader terminava com um `discard` do plano
  distante, que só repetia o recorte do hardware (depth clamp desligado desde 580f464) e no Mali tirava o early-ZS e o
  FPK de todo draw; agora o hardware recorta (`VITASHARP_GPU_QUIRKS=+FarPlaneDiscard` volta o discard). Bench do quadro
  do Killzone no Mali-G57: 48,4 -> 39,0 ms de GPU (-19%), mesma imagem; Killzone ao vivo 16 -> 22-24 fps. Cópias de
  região de RT: a cópia é reaproveitada enquanto nenhum draw tocou o retângulo dela (log dos últimos 32 bumps de versão
  por RT; `VITASHARP_REGION_COPY_REUSE=0` desliga): 7 de 32 cópias por quadro no atlas de pós-processo do Killzone
  (384x512). O resto do atlas (s4, ~12,8 ms no Mali: 17 passes de ~0,37 ms, 28 cópias de ~0,16 ms) são dependências
  reais, cada draw lê o que o anterior escreveu; só um redesenho (sem cópia e sem quebra de passe) baixaria isso.
- [x] Rodada 20 de desempenho (perf/desktop-20, 10/11/2026): **os chunks da arena de uniforms voltam quando o quadro
  se aposenta**, não quando o slot do quadro é gravado de novo (FramesInFlight submits depois: 3 no PC, 8 no celular).
  Os chunks ficavam presos esse tempo todo, o heap de uniforms de 48 MB enchia e os uniforms dos draws seguintes eram
  copiados para o anel de upload (`DrawUniformInfos`/`UniformInfo`, o maior memmove do worker no perfil). Agora as
  ações adiadas do quadro (destruição e referências dos chunks) rodam em `RetireFrame`, depois da cerca;
  `VITASHARP_RETIRE_DEFERRED=0` volta ao antigo. Killzone `kzl_walk3` (PC, a partir do quadro 4400): blocos copiados
  76 -> 0 por quadro (FIF 3) e 159 -> 0 (FIF 8, o do celular), fase Uniform do worker 0,43 -> 0,12 e 0,63 -> 0,12 ms,
  uploads 4,2 -> 1,2 MB/quadro (6,5 -> 1,2 com FIF 8), Execute do worker 6,7 -> 4,9 e 6,1 -> 5,0 ms/quadro;
  `replay-gpu --compare` nos 9 dumps (5.782 quadros) pixel-idênticos. Medido e descartado na rodada: o "kernel" alto
  do PERF cpu no Windows é artefato da contagem por tick (variou de 1,8 a 12,5 ms/quadro entre rodadas iguais); os
  ~170 `sceKernelDelayThread` curtos (~44 us) por quadro da thread principal do Killzone acordam ~0,76 ms atrasados no
  Windows (resolução mínima do timer de 0,5 ms; `VITASHARP_DELAY_SPIN=1` acerta o tempo, mas triplica a CPU da thread)
  e não limitam o fps no PC (28 ms/quadro esperando a fila de display); escalonador de núcleos emulados sem disputa
  (0 esperas/quadro); orçamento do cache de conteúdo de texturas 48 -> 128 MB sem ganho (uploads 0,81 vs 1,00/quadro,
  enche de novo). Novas linhas de diagnóstico: `PERF steady cpu`/`PERF steady thread` (CPU por thread a partir de
  `VITASHARP_WORKER_PROF_FROM`), `PERF delays` (delays por thread, quantos dormem no host e o atraso médio) e
  `PERF sched`, `PERF uniforms` (bytes que a arena zera ao reciclar e bytes copiados pelo guest). Medido depois:
  `UniformChunk.ClearWritten` zera exatamente o que o guest copiou (Killzone: 7,0 MB/quadro copiados = 7,0 MB zerados,
  0,83 ms/quadro no worker do PC; ~5% do worker no perfil); grânulo de 64 bytes em vez de 256 é pior (1,51 ms), não
  há desperdício de arredondamento. O custo de verdade são os ~7 MB/quadro de uniforms copiados (paletas de ossos do
  Killzone; inclui o memmove de `CopyLateUniforms`, ~3% do worker): só cai sem copiar, ligando a memória do guest
  direto (mudança grande, em aberto). Uncharted quase não usa o caminho de cópia (4 blocos/quadro): A/B igual
  (worker 3,23 vs 3,17 ms/quadro)
- [x] "Carregamento rápido" por jogo (feat/fast-loading, 10/11/2026): desde a ee53dd0 as leituras seguem a velocidade
  do cartão de memória do Vita (`IoTiming`, 7 MB/s), que alguns jogos precisam. A opção tira o limite (sem latência nem
  teto de vazão) só nos jogos escolhidos: na página do jogo (Padrão/Ligado/Desligado) sobre um padrão global em
  Configurações/Ajustes (desligado), nos dois launchers, com o aviso "Mais rápido que o Vita; alguns jogos podem ter
  erros de tempo". Linha de comando `--fast-loading[=on|off]` e `--io-speed=<MB/s>|vita|fast` (agora também com janela;
  antes só o headless lia o `--io-speed`), variável `VITASHARP_IO_SPEED`; o log e o relatório mostram a velocidade, as
  leituras e o tempo de dispositivo. Medido (`<scratchpad>\fastio_data\tests`, `kz_idle` headless, do toque que
  começa a missão em f2550 até a primeira imagem da cena): Killzone desligado 70,6 s e 75,7 s, ligado 20,1 s e 20,1 s
  (duas rodadas de cada, alternadas). Silent Hill: Book of Memories desligado passa logos, carregamento, criação do
  personagem e chega ao quarto (f2950, sem 0x805B0006 nem BKPT, `shbom_off`); ligado ele aborta no boot (0x805B0006 em
  f236 e BKPT na `main`, `shbom_on`), que é o caso que o aviso descreve. A mesma rodada corrigiu `--cpu-mhz` e
  `--frame-rate` (o "Clock da CPU" e a "Taxa de quadros" do launcher), que também só eram lidos no headless
- [x] Carregamento do Killzone desligado mais longo do que o tempo de dispositivo explica (feat/fast-loading,
  11/11/2026): **não é o modelo de E/S, é carga do PC durante as medições**. Os 70,6/75,7 s da primeira medida
  rodaram com outros emuladores e builds dos outros agentes nos mesmos núcleos (fixados em 0x0F ou sem fixar). Com o
  relatório novo por thread (`storage:` lista quem esperou o cartão, quanto, e quanto levou para pegar um núcleo de
  volta), toda a E/S do Killzone é da `SceFiosIO` (libfios2 do jogo, LLE): `sceIoPread` síncronos de 16-34 KB, nenhum
  pedido assíncrono, 22,8-29,8 s de espera na sessão inteira para 22-25 s de dispositivo, 0,0-1,0 s para pegar o núcleo de
  volta; o timer, o lock e a fila do dispositivo não somam tempo. Medidas do toque da dificuldade até a cena (headless,
  `<scratchpad>\fastio_data\tests`): rápido 20,1/20,1/21,1/23,0 s; 7 MB/s 35,4 e 40,0 s com o PC mais livre e 80,3 s
  com outro emulador e builds nos mesmos núcleos (`kz_p_off2`); 10 MB/s 37,6 s; 14 MB/s 32,3 s; 1000 MB/s 21,6 s (o
  caminho de bloqueio em si não custa nada). Os ~15-19 s a mais em 7 MB/s batem com os ~100 MB que a missão lê
  (~14 s a 7 MB/s mais 0,5 ms por leitura). Vita de verdade (longplay YouTube drriyW7Mn3M): depois da dificuldade a
  tela escura dura ~5 s e entra o briefing interativo "VAN-Guard" de ~4 min, durante o qual a fase carrega; no fim
  dele faltam ~7 s até a mesma cena. O padrão de 7 MB/s fica; SH:BoM continua precisando dele
- [ ] Killzone: o briefing "VAN-Guard" depois da escolha de dificuldade não aparece (11/11/2026, feat/fast-loading,
  `kz_fresh2`): no Vita ele entra ~5 s depois da tela escura; aqui, com o save do usuário, a tela escura com o ícone
  de carregamento fica até a fase abrir (20 s com carregamento rápido) e, com save novo, passou de 15 s sem briefing
  (fim da gravação). Falta ver o que o jogo espera para
  abrir o briefing (provavelmente um filme ou áudio em streaming)
- [x] Rodada 21 de desempenho (perf/desktop-21, 10/11/2026): **cópias de uniforms do worker da GPU**. Medido com a
  nova linha `PERF ubstats` (`VITASHARP_UB_STATS=1`, a partir de `VITASHARP_WORKER_PROF_FROM`): no Killzone (escada,
  quadro 4400+) o worker copiava 12,9 MB/quadro de uniforms (856 cópias, quase todas de slots de 16 KB: buffers que
  terminam em array, lidos até o próximo buffer), 8,5 MB deles repetidos no mesmo quadro (o mesmo endereço e conteúdo
  ligado por vários desenhos); no F1 2011 (corrida) 19,7 MB/quadro, 17 MB repetidos; Uncharted 0,4 MB; Mania 0;
  conteúdo igual ao do quadro anterior: ~0 (as paletas mudam todo quadro, então um cache entre quadros com
  rastreamento de escrita não ajudaria). Duas mudanças, sem mudar a imagem: (1) a arena de uniforms não zera mais os
  chunks reciclados; quem aluga a fatia escreve todos os bytes (cópia + zeros só no resto do slot, ~0,1 MB/quadro no
  Killzone contra 6,5 MB zerados antes); (2) os dois maiores buffers ligados (>= 4 KB) de cada estágio viram "slots
  separados" (`UniformBufferSlot.Split`): o shader os lê de bindings próprios (5/6 vértice, 7/8 fragmento, dinâmicos
  como 0/1, endereço com flag 0x10000000/0x20000000 no `fetchMemory`) e os desenhos de uma cena já encerrada que ligam
  o mesmo buffer leem uma cópia só (`Gxm.CopySplit`; o hardware lê os buffers quando executa a cena, então todos os
  desenhos dela veem a memória daquele momento). Dumps continuam com o layout inteiro (o gravador remonta, o leitor
  separa). `VITASHARP_UB_SHARE=0` volta aos slots no bloco por desenho; `VITASHARP_UB_ZERO=recycle` volta a zerar na
  reciclagem; `=check` preenche as fatias com um marcador e conta palavras não escritas (Killzone 0 de 1,57 milhão,
  F1 0 de 4,86 milhões; atenção: 0xDEADBEEF aparece de verdade na memória do Killzone, o marcador agora é um NaN).
  Resultado (PC, CPU do worker da GPU por quadro em regime, máquina dividida com outros agentes, pares A/B na mesma
  build): Killzone 9,20/8,27/8,23 contra 10,25/11,06/10,65 ms (~-2 ms, -20%); F1 corrida 6,39/7,19/6,62 contra
  9,28/10,12/7,93 ms (~-2,4 ms); copiados por quadro Killzone 6,5 -> 2,3 MB, F1 9,6 -> 0,9-1,3 MB; zerados 6,5/9,6 MB
  -> 0. `replay-gpu --compare` nos 9 dumps (5.782 quadros): todos pixel-idênticos. Estudado e não feito: ligar a
  memória do guest direto na GPU como o Vita3K (`map_memory` no `sceGxmMapMemory`, métodos ExternalHost
  (VK_EXT_external_memory_host), PageTable/NativeBuffer no Android e endereço de dispositivo por bloco) eliminaria
  até a cópia única, mas a GPU passaria a ler a memória no momento da execução (o worker hoje copia logo depois do fim
  da cena, e o quadro executa depois; jogos que reescrevem o buffer entre esses pontos mudariam), o Mali não garante
  o ExternalHost e o JIT usa a memória do guest num endereço fixo (aliasing de memória do Vulkan nesse endereço é
  inviável no Windows); com 2/3 das cópias já compartilhadas o ganho restante é ~2,3 MB/quadro no Killzone. Fases de
  ~1 fps e pausas de 30 s nas medidas do F1 apareceram com e sem a mudança (b-f1 em 2,5 fps, c-f1 com pausa de 31 s
  no boot): eram a máquina (disco C: a 98%, outros agentes, o Storage Sense apagando as junctions no meio das
  corridas), não o código
- [x] Rodada 22 de desempenho (perf/desktop-22, 11/11/2026): projeto de benchmark (item "Projeto de benchmark à
  parte") e os pontos quentes da rodada 21. **Ganho de bench**: swizzled -> linear por tabelas de coluna/linha (os bits intercalados de x e y não se
  sobrepõem) e tiled -> linear por linhas de tile (32 texels contíguos), também nos swizzles de tamanho arbitrário e nas
  grades de blocos comprimidos do decodificador: RGBA8 256x256 1032 -> 79 us e 1024x1024 13,6 -> 1,7 ms (13x); tiled
  424 -> 24 us e 6,5 -> 0,85 ms (18x); bit-idêntico (`TextureLayoutTests`), `VITASHARP_TEX_LAYOUT=legacy` = antes. No
  Killzone (escada) o tempo de upload por quadro não mudou (0,89-0,94 uploads/quadro, 0,15-0,19 ms/quadro nas duas
  versões): os uploads dele são BC e o custo é o resto do upload; o ganho vale para jogos que sobem texturas
  swizzled/tiled sem compressão. `replay-gpu --compare` nos 9 dumps (5.782 quadros) pixel-idêntico. Medido e
  descartado: (1) `VITASHARP_VK_DIRECT=1` no desktop: fase Record 1,81 vs 1,76 ms/quadro no Killzone (o custo do Record
  é o driver, não a transição do Silk.NET); (2) push constants só quando mudam (o número do draw só é lido pelos modos
  de depuração): 9 dumps idênticos, mas sem ganho medível (bench do worker 599 vs 531 ms por replay do kz_game, ruído
  de +-70 ms; no Killzone o Record ficou em 21-29% dos draws nas duas versões); (3) PVRTC com tabelas planas e sem
  Array.Clear por bloco: 4,7 vs 7,2 ms (256x256) e 83 vs 89 ms (1024x1024), ruído; o custo (~60-80 ns/texel) vem de
  desempacotar cada palavra 4 vezes e interpolar dois conjuntos de cor por bloco (um cache de modulação por palavra
  seria o próximo passo, e vale mais no celular, onde o PVRTC vira ASTC na CPU). A máquina estava dividida com outros
  agentes: o Execute do worker do Killzone variou de 6 a 35 ms/quadro entre rodadas iguais, então as fases só se
  comparam dentro da mesma rodada ou no benchmark. `DOTNET_JitDisasm` nos métodos quentes do worker: `Draw`,
  `BuildTextureSet` e `UniformSet` compilam direto em FullOpts (têm stackalloc, então ficam sem PGO dinâmico); em `Draw`,
  os getters `FetchPassMerge` e `DepthFeedback` fazem um switch de string por chamada; nada disso aparece acima do ruído
  (candidatos baratos anotados em perf_knowledge)

## Android

- [ ] Antes de voltar ao Android (08/11/2026): `third_party/android-shaderc` e `third_party/android-adrenotools` da
  checkout principal estão vazias desde 07/11 22:13 (provavelmente apagadas por uma limpeza através de junction); um APK
  compilado agora sai sem `libshaderc*.so`/adrenotools. Refazer com `build/build-android-shaderc.ps1|.sh` e
  `build/build-android-adrenotools.ps1|.sh` antes do próximo APK de teste
- [ ] Compatibilidade com o modo desempenho / modo jogo dos celulares (pedido do usuário, 07/11/2026): o app se declara
  jogo para o Android e para os otimizadores dos fabricantes (`android:appCategory="game"`/`isGame`, `game_mode_config`
  com suporte aos modos de desempenho e de bateria da API GameManager, `GameState` de carregando/jogando), para entrar
  nos painéis como o Game Turbo da Xiaomi e o Game Booster da Samsung e receber o perfil de desempenho; respeitar o
  modo escolhido (desempenho = sem limite de energia; bateria = ritmo econômico). Validar no Redmi com o Game Turbo e
  medir no Killzone com e sem o modo desempenho
  Declaração feita (conferido em 08/11/2026: 7b6a2ae, `isGame`/`appCategory="game"` e `game_mode_config` no
  AndroidManifest, modo de jogo lido no GameActivity); falta validar no Redmi com o Game Turbo e medir no Killzone
- [ ] Vibração dos botões na tela enquanto segura o analógico (pedido do usuário, 07/11/2026): opção nos controles por
  toque para os botões virtuais vibrarem ao serem tocados mesmo com o outro dedo no analógico (hoje o toque multiponto
  com o analógico pressionado não dá retorno tátil); intensidade ajustável e desligável, por perfil de controle

- [ ] Killzone no Android a 30 fps (pedido do usuário, 06/11/2026; perf/android-8): hoje 12-15 fps de mediana,
  68-83 ms por quadro, GPU 42-51 ms (`docs/notes/android-frame-anatomy.md`, seção Killzone). O rastro Perfetto mostra
  a GPU ocupada só 48% do tempo: a cadeia jogo -> worker -> GPU -> jogo é serial (`docs/notes/android-perf-references.md`).
  Próximos, por ganho esperado: (1) `VITASHARP_DISPLAY_EARLY_SIGNAL=1` levou a mediana de 12 a 15 fps em duas corridas
  (64,0/74,8 ms contra 77,5/78,9): repetir em 3 pares frios e no Uncharted/F1 antes de ligar; depois, notificações GXM
  liberadas no submit com a cerca real só onde o jogo lê a GPU (writebacks, visibilidade), como Dolphin/PCSX2;
  (2) opção de qualidade por jogo no Android (escala interna, MSAA), padrão nativo: 0,75 ou MSAA desligado levam a
  GPU a ~34,5 ms e o quadro a ~62 ms; (3) `VITASHARP_RENDER_AREA_TRIM=1` (db2d72d): -2 a -5 ms de GPU na cena de
  pós-processamento no bench, pixel-idêntico em 21 dumps; ligar quando uma série ao vivo mais longa confirmar.
  `VITASHARP_PRESENT_THREAD` piorou (85-86 ms) e `VITASHARP_TEXSET_PERSIST` não mudou o quadro: ficam desligados
  CPU do jogo no ARM64 (09/11/2026, perf/android-jit): o simpleperf da thread do jogo mostra IPC 0,49 e 60 faltas de
  L1I por mil instruções (código quente do JIT de 1,7 MB em 1.600 páginas): o tamanho do código é o gargalo. Feito:
  estado morto nas saídas (flags/registros que o bloco seguinte sobrescreve), R15 só no caminho frio, sem contadores
  de bloco, marca de página suja em 3 instruções e agrupada, DMB duplicado, FPSCR checado uma vez por bloco com
  operandos VFP em registros, tabela de despacho compacta (`VITASHARP_NATIVE_A64_NOOPT` desliga cada um;
  `docs/notes/native-jit.md`, "perf/android-jit"). Medido (50f7ddf, 3 pares): thread do jogo 24,2 contra 26,6 ms por
  quadro (-9%), fps igual porque o quadro estava preso no worker. Depois vieram superblocos (`chain`), cache SIMD
  de escrita (`vlazy`) e tabela de despacho de 2 vias (`assoc`).
  Depois (b760002, NativeAOT sobre ebfe72c, KZ slot 1, A B D D B A): com tudo ligado a thread do jogo cai de ~19,4
  para ~16,0 ms por quadro (-17%) e o tempo no código do JIT da thread do jogo de 23,3-24,6 para 21,4-21,9 ms; JobSched
  e Havok dentro do ruído (29-37 ms por quadro cada, A55). fps sem ganho: o quadro está preso na GPU (~43 ms) e nos
  quadros lentos de 6-8 vblanks (fase da transferência); na série o A teve mais quadros lentos que o D (A1 101 contra
  D1 34 quadros de 6-7 vblanks), confirmar com uma série D A D A antes de tirar conclusão sobre fps.
  Onde parei / próximos: (1) perfil de todas as threads com o native map (pedido ao laboratório, não feito) para ver os
  blocos quentes dos workers JobSched/Havok nos A55, que gastam ~30 ms de JIT por quadro cada para 0,3-0,6 M ciclos de
  guest; (2) ~800-900 voltas por quadro ao despachante na thread do jogo continuam com a tabela de 2 vias: ver se são
  saídas indiretas para blocos ainda não compilados ou destinos ausentes (contador por motivo); (3) cache de escrita
  para S ímpares (hoje só D e S pares vêm do cache); (4) VecLdSt pelo cache SIMD (hoje esvazia o cache)
  09/11/2026 (perf/android-gpu): **regressão desde 06/11 corrigida (23811d9)**: a 240a8de (Jak 1) fazia um `Finish` de
  todos os quadros a cada transferência de CPU sobre um writeback "em voo" e casava quadros que não estavam mais em
  voo; na main eram ~2.400 esperas por sessão (26-29 s) no Killzone. Agora só o quadro do writeback (gravando ou
  submetido) é esperado: Killzone 12 -> 15 fps de mediana (66,5 ms) na rota sem save state, esperas 6-8 por sessão;
  Jak 1 continua esperando no céu (0 quadros pretos em 3 gravações). Na escada do save state o Killzone ainda espera:
  `sceGxmTransferDownscale` do alvo 384x192 0x66332000 para os mips da textura 0x66362000 no quadro que está sendo
  gravado (~1.000 esperas por sessão, 12 fps); `VITASHARP_TRANSFER_DEFER=1` (8bf512d) roda a transferência quando o
  quadro aposenta, sem o worker esperar (mips um quadro atrasados): medir nessa rota. Sem ganho, ficam desligados:
  `VITASHARP_RT_FORMAT_LIST` (AFBC: os alvos RGBA8 já eram comprimidos; RGBA16F e MSAA o Mali não comprime),
  `VITASHARP_GPU_RELAXED_SYNC` (12 -> 15 fps mas a parede de vidro atrás da escada fica escura e borrada) e
  `VITASHARP_SHADER_FP16=half` (281 de 300 shaders relaxados, ainda não medido no Mali). O worker da GPU passou a ser
  o limite (47-50 ms no build mono-aot, GPU 42 ms, jogo 24 ms)
  Fim do dia 09/11 (NativeAOT + JIT + far plane + uniforms sem checagem, dev a622069): KZ 23,5-24 fps, GPU 26-28 ms,
  worker 22-24, jogo 11,5; nada bloqueia o worker, mas o jogo espera ~8,5 ms/quadro na fila de display e ~5 ms em
  notificações (os 4 writebacks de cena por quadro) e o worker fica 10 ms ocioso: o quadro é serial. Testado ao vivo:
  `VITASHARP_DISPLAY_EARLY_SIGNAL=1` KZ +12% (26,5-27,7 fps) mas F1 corrida -15% (29,7 -> 25,2; worker 22,7 -> 30,7 ms,
  jogo 12,3 -> 15,4: o jogo corre na frente e disputa os dois A76) e o Uncharted ficou mais lento em 04/11: continua só
  por env (sem padrão por jogo). Pista para o flicker: com EARLY os quadros pretos do vídeo de abertura do F1 caem
  de 32/69 para 15/13 (é timing). `GPU_RELAXED_SYNC` e `LAZY_WRITEBACK_SYNC` pioram o KZ; 4 quadros em voo = 8.
  Próximos: entender por que o EARLY deixa o worker do F1 mais lento (CPU/escalonamento) e tirar a espera da fila de
  display do KZ de outro jeito genérico; nos shaders, os vertex shaders da cena principal são limitados por load/store
  (`vitasharp-perf mali-cost`: ~79 leituras dinâmicas da paleta de ossos por vértice, ocupação 50%); ler 4 palavras
  com uma checagem (fetchMemory4) piorou no modelo; candidato: uniforms em UBO (registradores do Mali)
  Opções para o EARLY (decisão do usuário): (b, preferida, genérica) adaptativo: liberar o callback no submit só
  quando o quadro está preso na GPU (o jogo esperou a fila de display enquanto o worker ficou ocioso), e voltar ao
  callback na conclusão quando a CPU (worker + jogo nos dois A76) é o limite, como no F1; (a, alternativa) opção por
  jogo para o usuário no Android (como o MSAA), desligada por padrão
  Feito (b) em c41f784 (perf/android-early-adaptive): `VITASHARP_DISPLAY_EARLY_SIGNAL=auto` (Gpu/AdaptiveEarlySignal.cs)
  tenta o EARLY por uma janela de 60 quadros só quando o jogo perde a cadência (menos de 90% dos intervalos perto da
  mediana) e espera >= 3 ms/quadro na fila de display; mantém só se o quadro médio cair 5%, reconfere a cada 900
  quadros e recua (300 -> 7200 quadros) quando não ajuda. Só por env; ainda não medido no celular (KZ em medição;
  F1 sem estado compatível com o build). KZ escada (slot 5 salvo pelo build, parcial: 1 base, 2 auto): base 24,7 fps,
  auto 26,1/25,5 (+3-5%), p90 66,6 nos três, fila de display ~7,5 ms/quadro igual; o auto ficou em EARLY quase todo o
  tempo (uma reconferência voltou à conclusão e retentou). A 1ª tentativa compara com quadros de carga (63 ms): ganho
  inflado no começo. Faltam a 2ª corrida base e o F1 corrida (precisa de estado do mesmo build) antes de virar padrão

- [ ] Uncharted (PCSA00029) no Android: plantas piscando no começo do Prologue (Drake parado; APK LLVM perf/uc-cpu, dump
  `and_flicker/PCSA00029_plants075.vsgd`, presents 4000-4150, escala 0,75; pisca em 0,75 e 1,0, nenhuma
  chave do Android muda). Replay do mesmo dump no PC (agente C, 05/11/2026, build da main a19cdbe + a correção do
  DelayThread): **não pisca**, nem com o perfil nativo nem com `--gpu-profile=mali` (`tileflick.py`: 0 quadros com
  flash em 151; na faixa de baixo, a grama, a mudança n→n+1 é 1,0 e n→n+2 1,5, ou seja, movimento contínuo do vento, não
  alternância; `bugsC5_out/uc_plants_native`, `uc_plants_mali`, recorte `uc_grass_strip.png`). Como o dump guarda os
  comandos do jogo, a animação que o jogo manda é suave: o pisca vem da execução na GPU do celular (driver Mali-G57),
  não do jogo nem do caminho mobile do renderer emulado no PC. O tradutor não usa precisão reduzida (nenhum
  `RelaxedPrecision`/f16). Próximo, com o celular: o mesmo dump com `replay-gpu` no celular (android-gpu-dump) e
  `--draw-limit` nos desenhos da grama. Referência do Vita (vídeo `PGOXsCBMa9A`, 73 s): a grama balança devagar, sem
  pisca. Estranho, no PC: (1) com `--gpu-profile=mali` o Drake sai quebrado a partir do present ~4030 (só a mochila e
  partes do corpo; ~11 mil pixels diferentes do perfil nativo, `uc_native_mali_phone.png`), e no celular ele está
  certo. Causa: o perfil limita a 16 imagens amostradas por estágio (Mali Bifrost), o fragmento reserva 2 para o
  framebuffer fetch e fica com 14 unidades, e os programas do Drake que amostram as unidades 14 e 15 são pulados (log:
  `program 6A20E1F185A19E8F samples texture unit 15 but this device binds only 14: draw skipped`, idem 0CD0BE7FF59F2AF3).
  Nenhuma quirk muda isso (bisseção com `VITASHARP_GPU_QUIRKS=-<quirk>`: 11299 pixels diferentes em todas, `mali_q-*`),
  e tirar uma a uma as features do perfil no perfil desktop também não (0-2 pixels, `desk_f-*`). O Mali-G57 do
  celular (Valhall) desenha o Drake, ou seja, expõe mais que 16: o limite do perfil é pessimista para Valhall (conferir
  os limites reais no log do celular e ajustar o perfil). Não tem relação com o pisca das plantas (a grama não usa
  essas unidades e não pisca no replay). (2) Um de três replays com `--gpu-profile=desktop` deu `ErrorDeviceLost` no
  vkQueueSubmit; os outros dois passaram (PC dividido com outros agentes; não reproduz)
  Agente C dia 6 (05/11/2026, fix/bugs-games-6): (1) **corrigido** o perfil: agora usa os limites que o Mali-G57 r54
  reporta (8388608 por estágio, 7 sets, 256 B de push constants, 1/4/8 amostras); replay com `--gpu-profile=mali`: 0
  desenhos pulados (eram 3040), diferença média máxima contra o desktop 0,110 (era 2,339), Drake inteiro
  (`bugsC6_out/mali_drake_4037.png`: main mali / novo mali / desktop); desktop 151/151 quadros idênticos à
  main. (2) DeviceLost: 0 em 5 replays com `--gpu-profile=desktop` (2 builds, 3 com cache de shaders vazio,
  `bugsC6_out/plants_ref_desk`, `plants_new_desk`, `plants_dl3..5`); o da rodada anterior foi no 1º present, depois
  de 1335 ms de compilação de shaders no mesmo quadro, com a CPU dividida (provável TDR do driver); não reproduz
  Conferido (06/11/2026, perf/android-7): o replay com `--gpu-profile=mali` no build de baabc78 (fix/bugs-games-6) já
  mostra o Drake inteiro; o da main ainda o quebra (`and7_runs/drake_mali_cmp.png`, esquerda main, direita
  baabc78). A bisseção do pisca no celular não foi feita (testes no celular parados pelo usuário); preparado:
  `and7_tools/flick.sh` (screenrecord do aparelho, quadros distintos, `tileflick.py` por variante do env.txt)
  e o vídeo do Vita `and7_yt/uc_vita.mp4` (o Prologue começa por volta de 70 s)
- [x] Menu de pausa do Android (botão PS) no estilo do app (pedido do usuário, 05/11/2026): hoje é um `AlertDialog`
  padrão do Android (`GameActivity.cs` ~482/524, `GameActivity.SaveStates.cs` ~72 para os slots de estado). Trocar por
  uma tela própria com as cores, fontes, cantos e ícones das páginas do app (lista de jogos/configurações), por cima do
  jogo pausado: continuar, salvar/carregar estado (slots com miniatura), configurações do jogo, recentralizar,
  gravações de desenvolvedor, sair; navegável por toque e pelo controle físico
  Feito (05/11/2026, agente C, fix/bugs-games-5, commit 72b769a; falta validar no celular): `GameActivity.PauseMenu.cs`,
  um cartão sobre o jogo pausado com a paleta, as fontes Inter/Sora, os cantos e os ícones de `pausa.html` (traços
  SVG com `PathParser`), em duas colunas para paisagem: Continuar e Sair; Salvar/Carregar estado (página dos 6 slots
  com miniatura e data), Ajustes (toque traseiro, controles na tela, pré-gravação, recentralizar), Recentralizar,
  Gravar tudo/Parar gravação e, no build de desenvolvedor, Desenvolvedor (Gravar tempo/conteúdo). Toque e controle:
  todo botão recebe o foco (D-pad/analógico movem, A ou Enter aperta, B ou Voltar volta, Start ou PS fecha), o focado
  ganha o contorno azul; um toque fora do cartão continua. APK dev em `bugsC5_apk` (instalado no celular
  às 20:55 sem a correção dos acentos). O Redmi recusa `adb shell input` (INJECT_EVENTS): para dirigir o menu e as
  telas sem o usuário há agora `VITASHARP_ANDROID_UI_INPUT` (build dev, `Developer/UiScript.cs`, commit 96ac1d5).
  A validação no celular foi interrompida a pedido do usuário antes dos prints do menu
  Validado no celular (06/11/2026, perf/android-7, APK LLVM dev, por toque com `VITASHARP_ANDROID_UI_INPUT`):
  página principal, Salvar estado (salvou no slot 2 com miniatura), Carregar estado (carregou o slot 2 em 3 s),
  Ajustes, Desenvolvedor, Recentralizar (aviso "Movimento recentralizado"), Gravar tudo / Parar gravação, Sair do
  jogo (`and7_tools/shots/pm_main.png`, `pm_a.jpg`, `st_a.jpg`, `st_b.jpg`, `rec_a.jpg`). Achado e corrigido (79925c5, ainda
  não validado no aparelho): os interruptores de Ajustes eram um texto minúsculo "LIG/DESL" (Switch do Theme.Black),
  as páginas de slots passavam da altura da tela, a miniatura era uma pílula, o aviso de "Salvando a gravação" era um
  AlertDialog antigo. O gancho UI_INPUT por Instrumentation era recusado (INJECT_EVENTS) também na própria janela:
  agora despacha direto na activity (99911c2). Pelo controle: B volta/fecha; D-pad e A não fizeram nada no gancho
  (nenhuma view com foco em modo de toque) — a correção do gancho (sai do modo de toque) não foi testada no aparelho;
  falta também o PS de um controle real. Gravar tudo no Android: sair pelo menu durante a gravação espera o save com
  aviso e o zip fica completo (31 arquivos, 194 MB: estado, dump de GPU de 288 quadros, quadros, áudio, report;
  `captures/PCSA00029_20261105_232641_tudo.zip` no celular). Estranho: a gravação derruba o Uncharted de ~26 para
  12-14 fps (gravador 10,7 ms/quadro no worker + 13,3 ms de compressão); o session.txt diz "the GPU dump ended without
  a present" sempre que se para com o jogo pausado (o menu pausa antes do stop)
  Validado no celular (06/11/2026, perf/android-8, APK LLVM dev da main 4a8bb94, no Killzone e no God of War): as
  páginas de slots cabem na tela, a miniatura tem cantos retos, a página principal por toque
  (`and8_tools/shots/m1_main.jpg`, `m3_save_slots.jpg`, `g4_loadpage.jpg`). O foco por D-pad/A continua
  sem validação: as teclas injetadas pelo gancho não tiram a janela do modo de toque neste celular (nenhuma view
  recebe o foco, `dumpsys activity top`), e não havia controle físico (nem o botão PS). Corrigido sem validar
  (ae93ac8): o primeiro botão de cada página é o foco padrão (`FocusedByDefault`); sem isso, a primeira seta de um
  controle real focaria o primeiro focável da árvore, o próprio quadro do menu. Falta: validar com um controle
  Feito e validado no celular por toque (06/11/2026); foco pelo controle físico ainda sem teste (sem controle no aparelho)
- [x] Acentos quebrados no Android (relato do usuário, 05/11/2026): **causa achada e corrigida** (commit 239d126, falta
  o print do depois no celular): `Pages/LibraryView.cs` estava salvo com UTF-8 lido como Windows-1252 e gravado de
  novo ("JogÃ¡veis", "Todos Â· 8", "licenÃ§a", e os ícones ⚙ ⌕ ▶ › 🏆 como "âš™", "âŒ•", "â–¶", "â€º", "ðŸ†"),
  exatamente o que o celular mostra (`bugsC5_phone/before_library.png`). As fontes Inter e Sora têm os
  glifos (á à â ã ç é ê í ó ô õ ú, conferido no cmap). Varredura do repositório inteiro: só esse arquivo e três
  comentários do Core; `SourceEncodingTests` falha se a sequência voltar (src, tests, build). As outras telas,
  diálogos, o overlay de FPS/REC e o texto dos jogos (PVF) ainda não foram vistos no celular
  Validado no celular (06/11/2026, perf/android-7): Biblioteca ("Jogáveis", "Todos · 8", "Jogável"), Ajustes
  ("licença", "Padrão", "Automático"), diálogo da CPU, Desempenho, Controles, página do jogo, menu de pausa e
  overlay ("Pausado", "● REC") com os acentos certos (`and7_tools/shots/lib3.png`, `app_ajustes.png`, `app3.jpg`, `app3b.jpg`,
  `pm_main.png`). Texto dos jogos (PVF) não conferido
  Validado no celular (06/11/2026, perf/android-7): acentos certos em todas as telas do app
- [ ] Reescrita do JIT para ARM64 (pedido do usuário, 04/11/2026): o código gerado ainda tem ~6-12 palavras por
  instrução do Vita no caminho quente e os cortes pequenos (perf/jit-a64: juntar marcações de página suja, PC só nas
  saídas frias, ponto flutuante fora do caminho, contagem simplificada) não deram ganho mensurável no Devious Dungeon.
  Reescrever com representação intermediária e otimizações de verdade: registradores do Vita em registradores do
  celular por bloco e entre blocos ligados (alocação global), flags NZCV preguiçosas no NZCV do ARM64, propagação de
  constantes e eliminação de código morto, acesso à memória direto pela base fastmem, encadeamento de blocos sem
  volta ao despachante, superblocos e ponto flutuante/NEON mapeado direto. Meta: jogos limitados pela CPU (F1 2011,
  Devious Dungeon) com a thread do jogo pelo menos 1,5-2x mais rápida no celular; validar com o teste de corpus contra
  o interpretador e o `VITASHARP_NATIVE_VERIFY` no celular
- [ ] Comparar o desempenho com o Vita3K no mesmo celular (pedido do usuário, 04/11/2026): Minecraft (instalado nos
  dois, mas não roda no Vita3K do celular, segundo o usuário) e Uncharted quando o usuário instalar no Vita3K; gameplay no mesmo ponto, resolução nativa e 0,5, fps mediano
  e 1% mais baixo pelo contador do sistema, prints; só leitura, nada gravado na pasta do Vita3K
- [ ] Resolução interna reduzida (0.75 / 0.5, por jogo, desligada por padrão) para o Uncharted no Mali-G57 (GPU ~40 ms
  por quadro em a92710b): precisa de trabalho no espaço de coordenadas. As cópias de região, as cópias de profundidade,
  os writebacks e a reinterpretação do G-buffer pelo espelho da CDRAM (bytes no layout do convidado) usam pixels do
  convidado; o caminho de supersample (2x) só aumenta a escala (04/11/2026)
- [x] (bug) Instalar jogos no Android: o usuário não encontrou como instalar um VPK no app (04/11/2026). O código tem
  um botão flutuante "+ Instalar VPK" no canto inferior direito da Biblioteca (`LibraryView.cs`, seletor do sistema
  pelo SAF), mas ele pode estar escondido/fora da tela (sobre a grade, atrás da barra de navegação, ou some durante
  a instalação) — conferir no Redmi Note 14 e deixar a ação visível e fácil de achar (também no menu/Ajustes e na
  tela vazia da Biblioteca). Além do VPK, aceitar `.pkg` + `work.bin`/`.rif` e licença em zRIF (o desktop já instala
  `.pkg`; ver o item do zRIF em "Firmware e sistema") e mostrar progresso e erros claros. Feito (04/11/2026, branch
  fix/user-bugs, só compilado, falta o usuário testar no celular): "+ Instalar" no topo da Biblioteca, o botão
  flutuante virou "+ Instalar jogo", um botão na Biblioteca vazia e "Jogos > Instalar jogo" em Ajustes; cada um
  pergunta .vpk ou .pkg + licença (arquivo work.bin/.rif ou zRIF colado), com barra de progresso e o erro em
  diálogo. O zRIF é decodificado no Core (`Package/Zrif.cs`, dicionário do pkg2zip) e o `PkgInstaller` confere o
  content ID da licença contra o do `.pkg`; o `install <pkg> <rif|zRIF>` do CLI também aceita zRIF
- [ ] Sistema do Vita (shell) não roda no Android (relato do usuário, 01/11/2026; log do celular
  `20261101_204800_SYSTEM.log`, Redmi Note 14, firmware 3.74 instalado no app): a sessão abre, a GPU apresenta só
  2 superfícies (0x6043E000 e 0x60240000) e 91 quadros em ~4 s, com apenas 7 shaders e 4 pipelines (no PC o shell
  chega à tela inicial com bem mais); depois o usuário pausou e saiu. Avisos: `SceNetPsForSyscalls::sceNetSyscall*`
  (Control, Socket, Ioctl, Close, GetIfList, Sendto, IcmConnect) não implementadas, várias `SceNpCommon`/
  `SceNpManager` sem nome, `SceMotion::NID_A408BC69`, `SceNetCtl::NID_C61F3E96`, `SceHttp::NID_855D5254`; a thread
  `SceDbUtilDcnClient` gasta 68 ms de código nativo por quadro. Comparar com o boot do shell no PC (mesmo firmware) para
  ver onde o Android para (tela preta? qual processo/thread espera o quê) e se é algo do Android (sockets do sistema,
  caminhos/armazenamento, processos múltiplos, `MediaCodec`, permissões); pedir uma gravação "Gravar tudo" do usuário
  na tela travada
- [x] App .NET MAUI com interface declarativa em C# (MauiReactor), seguindo os mockups em `docs/android/` (biblioteca
  e troféus, ajustes, firmware, conta PSN e rede, driver e informações da GPU, Sistema do Vita) (30/10/2026: gera o
  APK, `src/VitaSharp.Android`; ainda não rodou num aparelho nem no emulador)
- [x] Primeira execução no emulador do Android (30/10/2026: AVD `VitaSharp_Test`, API 36 x86_64, GPU do host;
  Knightin'+ chega ao menu e Devious Dungeon ao load com o JIT nativo x64; corrigidos o crash do `GameActivity`, a
  swapchain recriada a cada quadro e o `SignExtend` que o JIT do Mono compilava errado; resultados em
  `docs/android/README.md`, "Emulator run")
- [x] Primeira execução num aparelho ARM64 (30/10/2026: Redmi Note 14, Helio G99, Mali-G57: JIT nativo ARM64 sob o
  SELinux, driver Mali sem `shaderClipDistance` (plano distante pelo fragment shader, `FarPlaneDiscard`), MediaCodec de
  hardware, AAudio; Devious Dungeon, Knightin'+, F1 2011, Persona 4 Golden, Killzone e Uncharted; resultados em
  `docs/android/README.md`, "Phone run")
- [ ] Aparelho Adreno: driver da Qualcomm, libadrenotools (driver customizado, BCeNabler), controle Bluetooth, giro de
  180° (o que falta da lista no fim de `docs/android/README.md`)
- [ ] Estimador de custo de GPU para Adreno, como o `vitasharp-perf mali-cost` da Mali: analisar os shaders traduzidos
  numa Adreno (6xx/7xx) sem aparelho — compilar o Mesa no PC e usar o compilador `ir3` do Turnip pela linha de comando
  (instruções, registradores, transbordo, ocupação por shader) ou o Adreno Offline Compiler da Qualcomm se for
  encontrado — e juntar com a contagem de fragmentos/vértices do replay e o tráfego de tiles (a Adreno também desenha
  em blocos, com GMEM), para estimar ms por desenho/quadro; o resultado é do Turnip (não do driver da Qualcomm), então
  calibrar quando houver um aparelho Adreno ou pelo Samsung Remote Test Lab / Firebase Test Lab
- [ ] AAudio de baixa latência no Redmi Note 14: o stream sai "normal shared" (a saída do alto-falante não tem
  FastMixer, rajada informada de 4096 quadros, ~200 ms de latência); ver se outro caminho (MMAP, sem
  `setBufferCapacityInFrames`, AudioTrack com `PERFORMANCE_MODE_LOW_LATENCY`) consegue a saída rápida
  (31/10/2026, perf/android-phone-2: sem pedir capacidade o AAudio dá rajada de 2052 quadros e ~96 ms, sem underrun;
  continua "normal shared": o alto-falante não tem caminho rápido nem MMAP neste celular)
- [x] God of War Collection (PCSA00126) no celular: o Kratos aparece com problemas de textura (31/10/2026, branch
  perf/android-phone-2: atributos de vértice desalinhados (stride 19, F16 nos offsets 3 e 11) que a NVIDIA aceita e a
  Mali lê errado; agora lidos como bytes e remontados no shader. Só no celular; `docs/notes/gow-textures.md`)
- [ ] Flicker no celular em todos os jogos testados (Knightin'+, Devious Dungeon, F1 2011, Uncharted, God of War
  Collection): sequências de 10+ quadros seguidos (`screencap`/`screenrecord`) comparadas com o desktop na mesma cena
  e zero quadro piscando; o que falta hoje: o quadro escuro do Uncharted (1 a cada volta do filme do título) e
  conferir o God of War
  31/10/2026, perf/android-phone-2: dumps do celular reproduzidos no PC (normal e `--gpu-profile=mali` com
  `VITASHARP_GPU_POISON=1`) e gravações de tela do celular dos cinco jogos sem nenhum quadro marcado pelo `flicker`;
  falta o quadro do filme do Uncharted a cada volta (não apareceu em 8 s de gravação)
- [ ] GPU da Mali sobrecarregada (Uncharted no título: 33,7 ms de GPU por quadro na Mali-G57 contra 4,2 ms no PC,
  31/10/2026, perf/android-phone-2: draws com blending programável seguidos dividindo um render pass de leitura
  (`VITASHARP_FETCH_PASS_MERGE=1`: 5,4 → 1,6 renderings por cena, 27 → 35 fps, 28 → 22 ms de GPU); os quadros sem a
  camada de luz no título aparecem também sem a junção (1 em 25 a 27 fps, main incluída; 1 em 7 com ela), nunca no
  replay do PC: defeito do lado do celular (Phone run 4); a junção fica desligada até achar a causa; o resto continua aberto.
  8x mais que a diferença de força esperada): medir com os contadores da Mali (Arm Performance Studio / contadores
  via adb) onde vai o tempo (cálculo, texturas, leitura/gravação de tiles, vértices) e reduzir: shaders em 16 bits
  onde o Vita usa `half` (mediump na Mali), menos load/store de tiles entre etapas, MSAA/superfícies extras no perfil
  Mali, shaders traduzidos mais enxutos e uma opção de resolução interna menor (75%/50%)
- [ ] Uncharted no celular: na tela de título, um quadro escuro e velho do filme de fundo a cada ~5 s (não é o caso
  "decodificador sem quadro"; 30/10/2026, Redmi Note 14); achar de onde vem o quadro velho (ring de buffers do
  player do jogo com a CPU lenta, ou o cache de texturas da imagem YUV)
  12/11/2026 (A1): provável a mesma causa do F1 (item "o vídeo de abertura do F1 2011 pisca"): o stream do filme
  recomeçado respondia as primeiras chamadas com a última picture da volta anterior (o fim escuro do filme); agora a
  primeira chamada de cada volta sai com a picture do IDR. Na tela de título (150 s, 1 execução de cada) o detector não
  marcou nada nem no código novo nem no antigo; falta conferir ao vivo
- [ ] F1 2011 no celular: quadros pretos nos filmes, porque o player do jogo pula o desenho do quadro quando o
  decodificador atrasa (CPU lenta: ~20 fps; reproduz no PC com `--cpu-mhz=120`); some com a velocidade nativa (item
  abaixo), conferir depois
  12/11/2026 (A1): com o código atual não há mais chamada sem picture no começo dos filmes (era 8 por filme) e o fps
  do filme não mudou (ver o item "o vídeo de abertura do F1 2011 pisca")
- [x] Teclado físico no jogo (setas/letras para os botões, como no desktop) (30/10/2026: `AndroidKeyboard` com o perfil
  de teclado do `input.json` e o mapeamento padrão do desktop, P e Home abrem o menu de pausa, teclas de inclinação
  no SceMotion pelo `VirtualMotion`, teclas soltas quando um diálogo pega o foco, texto do IME pelo teclado; falta
  testar num aparelho, item 5 da lista em `docs/android/README.md`)
- [x] Linhas de fim de sessão no Android (`AUDIO summary`, `PERF gpu`, `PERF mobile`, relatório do backend de CPU)
  (30/10/2026: `SessionReport` no Core, o mesmo texto do `Runner` do desktop; o `SessionStatistics` as escreve no fim
  do jogo e, só no arquivo de log, a cada 30 s e a cada pausa, porque o Android costuma matar o processo; falta ver
  num aparelho que as linhas aparecem e que os tempos por thread do Mono são lidos)
- [ ] Rodar sessões longas com `VITASHARP_NATIVE_VERIFY=1` (pelo `config/env.txt`) no Android para achar outros
  pontos em que o JIT do Mono dobra constantes de outro jeito que o CoreCLR (o backend IL e os blocos que sobem para
  ele)
- [x] shaderc para Android: compilar a `libshaderc_shared.so` (arm64-v8a e x86_64) com o NDK e empacotar no APK; sem
  ela o renderizador não compila shaders no celular (30/10/2026: `build/build-android-shaderc.ps1` (ou `.sh`), NDK r30)
- [x] Controles na tela (horizontal), mapeamento de controles Bluetooth e toque traseiro (30/10/2026: `OnScreenPad`,
  `AndroidGamepads` com os perfis de controle do desktop e o botão TRÁS; falta testar num aparelho e o editor de
  layout)
- [x] Sensores do celular alimentando o SceMotion (interface `IMotionSource` já existe no Core) e câmera do celular
  no SceCamera (30/10/2026: acelerômetro + giroscópio no SENSOR_DELAY_GAME com os eixos girados pela rotação da tela,
  ajuste "Movimento" (celular, controle no Android 12+, desligado), Recentralizar no menu de pausa; Camera2 em
  YUV_420_888, frontal/traseira para as câmeras 0/1, tamanho e fps pedidos pelo jogo, imagem em pé, permissão CAMERA
  pedida no primeiro uso (negada = quadros cinza); falta testar num aparelho, item 11 da lista em
  `docs/android/README.md`)
- [ ] Validar num aparelho Adreno a camada de quirks de GPU (na Mali-G57 nenhum contorno fez diferença visível, 30/10/2026;
  `docs/notes/mobile-gpu.md`): quais contornos
  continuam necessários, `VK_EXT_rasterization_order_attachment_access` (nunca executado), o ganho real dos
  load/store por cena no perfilador do aparelho, o caminho de render passes (Vulkan 1.1) num Adreno 6xx com driver
  antigo e a qualidade/velocidade da conversão para ASTC (item 10 da lista em `docs/android/README.md`)
- [x] Menos interrupções de cena no renderizador (30/10/2026: uploads e cópias que não leem a cena são gravados
  antes da renderização, que continua; `docs/notes/mobile-renderer.md`)
- [x] Texturas BC/PVRTC convertidas para ASTC 4x4 com cache em disco por jogo (30/10/2026: codificador em C#,
  RGBA8 quando não há ASTC; testado no Iris Xe; `docs/notes/mobile-renderer.md`)
- [x] Caminho para Vulkan 1.1 (30/10/2026: render passes no lugar de dynamic rendering, estado fixo nos pipelines,
  perfil `--gpu-profile=adreno-vk11`; comparado pixel a pixel na GTX 1650; falta rodar num aparelho)
- [x] Driver de GPU customizado: libadrenotools compilada com o NDK e empacotada, carregador (pacote Turnip/Adreno com
  meta.json, redirecionamento de arquivos, BCeNabler, turbo) e a tela "Driver da GPU" (instalar o zip pelo seletor,
  usar, reiniciar o jogo aberto) (30/10/2026: `build/build-android-adrenotools.ps1` (ou `.sh`); falta testar num
  aparelho Adreno, item 9 da lista em `docs/android/README.md`)
- [x] Vídeo pelo MediaCodec e áudio pelo AAudio (30/10/2026: H.264 sem superfície com o decodificador de software do
  Android como reserva, filmes do SceAvPlayer pelo MediaExtractor com AAC pelo MediaCodec, AAudio de baixa latência na
  taxa do aparelho com AudioTrack no Android 8.0, a mesma política de buffer do OpenAL e reabertura quando o aparelho de
  saída muda; falta testar num aparelho, itens 7 e 8 da lista em `docs/android/README.md`)
- [x] SceAudiodec MP3 no Android (30/10/2026: `AudioFrameDecoders.Mp3Backend`, o mesmo `MediaCodecAacDecoder`
  com "audio/mpeg" e o tamanho do quadro tirado do cabeçalho (1152/576 amostras, como o FFmpeg); nenhum jogo conhecido
  usa, falta testar num aparelho; AAC e SceMp4 já vão pelo MediaCodec e pelo MediaExtractor, verificados no Redmi Note 14)
- [ ] Velocidade nativa no celular (meta: o fps do jogo no Vita; medir Knightin'+, Devious Dungeon e F1 2011 no
  Redmi Note 14 (Helio G99, Mali-G57) antes e depois de cada passo), nesta ordem:
  - [x] Threads do jogo e da GPU nos núcleos rápidos (2× A76) e sessão da Performance Hint API (ADPF) (30/10/2026,
    branch feat/android-speed-1: clusters lidos do sysfs, thread principal e GPU nos núcleos rápidos, sessão ADPF com o
    tempo de trabalho por quadro, `VITASHARP_ANDROID_AFFINITY=off` para comparar; medido no Redmi Note 14 em 31/10:
    thread principal e GPU nos núcleos 6-7, ver "Phone run 2" em `docs/android/README.md`)
  - [x] Ganhos rápidos: sem logs de nível info no logcat durante o jogo, Swappy (ritmo de quadros, 90/120 Hz) e o
    controle de velocidade da CPU emulada sem esperas à toa (30/10/2026, mesmo branch: logcat só com avisos e erros a
    partir do primeiro quadro, FIFO com 3 imagens, `Surface.setFrameRate` com o fps do jogo, nenhuma espera girando
    no celular; o Swappy ficou de fora por ser biblioteca nativa, ver "Frame pacing" no README; medido em 31/10:
    Knightin'+ estável em 30 fps com 60 Hz na tela)
  - [x] Ajuste do JIT nativo ARM64 no aparelho: contagem de ciclos na saída dos blocos, comparação e desvio juntos,
    registradores NEON mantidos nos do processador, menos trocas do modo de ponto flutuante (30/10/2026: feito no
    branch `perf/arm64-jit`, cerca de 25% menos instruções por instrução do Vita nos blocos de teste, cada otimização
    com chave em `VITASHARP_NATIVE_A64_NOOPT`; 31/10/2026, branch perf/android-phone: 0 divergências com
    `VITASHARP_NATIVE_VERIFY=1`, mais saídas ligadas por B direto, chamadas HLE de dentro do código nativo, sem a
    transição de GC do Mono; Devious Dungeon de 27-30 para 52 fps; ver `docs/notes/native-jit.md`, "On the device")
  - [x] Custo do Mono: AOT do código gerenciado quente (HLE, renderizador, áudio) ou CoreCLR no Android (30/10/2026:
    Release com AOT completo do Mono, todos os métodos de todos os assemblies com o JIT mantido; troca por
    `-p:VitaSharpAndroidRuntime=mono-jit|mono-aot|mono-aot-llvm|coreclr`; APK de 44,2 para 66,9 MB; 31/10: medido
    no celular, `mono-aot-llvm` virou o padrão (corrida do F1 2011 35% mais rápida que `mono-aot`), ver "Runtime and AOT
    choice" em `docs/android/README.md`)
  - [ ] Custo por draw do worker da GPU no celular: F1 2011 na corrida ainda abaixo de 30 fps (cerca de 1.000 draws por
    quadro, o worker da GPU num núcleo A76 inteiro); dividir a preparação e a gravação dos comandos em duas threads,
    menos atualizações de descriptors por draw (31/10: conjuntos de texturas repetidos já são reaproveitados)
  - [x] Crash do driver Mali em `vkEndCommandBuffer` (GPU worker, ponteiro nulo em `libGLES_mali.so`) no F1 2011 entre o
    menu e a corrida: comandos gravados num command buffer ainda não iniciado depois de um submit antecipado (camada de
    validação no celular, `VITASHARP_VK_VALIDATION=1`/`sync`); o próximo quadro começa antes (31/10, perf/android-phone-3)
  - [ ] Som dos filmes do F1 2011 no celular: um quadro AAC de silêncio a cada quatro (o player do jogo recebe o áudio
    devagar pelo SceMp4/MediaExtractor + SceAudiodec/MediaCodec; no PC, com FFmpeg, sem buracos); imagem dos filmes já
    corrigida (31/10: fila de quadros do MediaCodec, pts da unidade, sem flashes pretos)
  - [ ] Shaders: shaderc em segundo plano nos núcleos rápidos e reaproveitar o cache de shaders gerado no PC
  - [ ] GPU: conversão para ASTC com cache em disco sempre ligada na Mali (sem BC), MSAA nativo
- [x] Armazenamento: escolher a pasta dos jogos (Storage Access Framework), instalar VPK, saves e instalar o firmware
  (.PUP) pelo seletor (30/10/2026: pasta específica do app como raiz de dados, VPK/PUP/driver pelo seletor do sistema,
  lidos no lugar; o motivo de não usar uma pasta do SAF como raiz está em `docs/android/README.md`)

- [ ] Ferramentas de desenvolvedor (`-p:VitaSharpDeveloper=true`, `tools/dev/README.md`): bench da GPU com fixtures e
  variantes em tempo de execução, teste de flicker no aparelho (replay e `VITASHARP_FLICKER_CHECK` no jogo), Perfetto,
  snapshots de desenvolvedor e o gravador de voo (últimos N s com um botão)
- [ ] Próxima fase, quando Uncharted e F1 estiverem sólidos no celular (fps nativo, sem flicker nem quadro escuro, som
  correto, sem crash) e o gravador de voo funcionar: testar os outros jogos instalados no celular, os mais pesados
  primeiro (ordem pelo ms/quadro de CPU+GPU no PC, dos logs de revalidação e `docs/compatibility.md`; provavelmente
  Killzone Mercenary, Soul Sacrifice Delta, Gravity Rush, Unit 13, Resistance, CoD BO Declassified, God of War
  Collection II, Tearaway, Persona 4, FFX HD, LBP, Ninja Gaiden Σ/Σ2). Para cada um: fps nativo, sem flicker, som
  correto, sem crash, imagem igual à do PC
  - [ ] Antes de cada rodada no celular, pegar no PC os erros que a NVIDIA tolera e a Mali não (como o do God of War):
    replay de um dump de cada jogo com a camada de validação e o perfil `mali-strict` (alinhamento estrito de atributos
    + poison); um teste automático (unitário ou de replay) que falha com uso inválido apontado pela validação
    (atributos desalinhados, formato sem o recurso, flags de uso, tipo de view errado como o samplerCube sobre view 2D
    do F1); corrigir de forma genérica e depois confirmar no celular com o bench de flicker e fps

## Controles

- [x] Botões do mouse mapeáveis (pedido do usuário, 06/11/2026): o botão esquerdo e o direito do mouse (e o do meio)
  podem ser mapeados como qualquer botão do Vita (ex.: R para atirar e L para mirar no Killzone, junto com o mouse como
  analógico do F4), no perfil de controle (`input.json`), por jogo, e na tela de controles do launcher; funcionando com
  o cursor capturado na janela
  Feito (08/11/2026, feat/product-2): os botões do mouse são teclas do perfil do teclado (`key:MouseLeft`,
  `key:MouseRight`, `key:MouseMiddle`, `key:Mouse4`, `key:Mouse5`); um botão mapeado aperta o controle do Vita (ou o
  atalho de pausa) em vez de tocar na tela, também com o cursor capturado; na tela de controles, enquanto espera a
  tecla, um clique do direito/meio/laterais (o esquerdo só sobre o desenho do Vita) grava o botão. Validado no 99Vidas
  (janela oculta, mensagens do Windows na janela): botão direito = Cruz respondeu NÃO ao diálogo, botão 4 = pausa.
  O perfil do teclado é um só (não por jogo)
  Confirmado pelo usuário (08/11/2026)
- [x] Mouse como analógico direito (desktop): o movimento do mouse vira a alavanca direita do Vita, para mirar e
  girar a câmera como num jogo de PC (ex.: Killzone, Uncharted). Captura do cursor na janela (com tecla para soltar),
  sensibilidade, inverter eixo Y, zona morta/curva de resposta e retorno ao centro quando o mouse para; opção no
  perfil de controle (`input.json`) e na tela de controles do launcher. Hoje o mouse só move o giroscópio
  (`HostInput.MouseLook`, com o botão do meio)
  - [ ] Modo do mouse escolhido no mapeamento: giroscópio, analógico direito, analógico esquerdo ou desligado
    (qualquer analógico pode ser o alvo), com um botão/tecla de atalho para alternar entre os modos com o jogo rodando
    (aviso na tela com o modo atual) e o modo salvo por jogo no perfil de controle
  Feito (05/11/2026, agente C, fix/bugs-games-5; falta validar a captura na janela): `MouseSettings` no perfil do
  teclado (`input.json`, `mouse`): modo (giroscópio = o mouse look de antes com o botão do meio, analógico direito,
  analógico esquerdo, desligado), sensibilidade, inverter Y, zona morta do jogo a pular, curva, tempo de retorno ao
  centro, tecla de soltar (Esc) e de alternar (F4), e o modo por jogo (`games`: title id → modo). `MouseStick`
  (Core/Input) faz cada pixel empurrar a alavanca e ela voltar ao centro com constante de tempo ReturnMs/3, então uma
  velocidade constante do mouse dá uma inclinação proporcional qualquer que seja a taxa de leitura. Na janela, num modo
  de analógico o cursor fica preso (Silk.NET `CursorMode.Raw`), Esc solta, um clique prende de novo; preso, os botões
  não tocam a tela. F4 alterna os quatro modos com o jogo rodando, guarda a escolha para aquele jogo e mostra o aviso
  no canto ("Mouse: analógico direito (Escape solta o cursor, F4 muda)") e na barra de título. Launcher: seção "Mouse
  como analógico" nas opções do teclado em Controles. Script: `mouse@T:dx:dy[:len][:l]`. Validado sem janela no
  Uncharted (`bugsC5_scn/uc_mouse.txt`, `mouse@f7000:1200:0:f90`): a câmera gira para a direita durante o movimento e
  para quando ele acaba (`bugsC5_out/uc_mouse_strip.png`, 6950/6990 parados, 7010-7090 girando); testes
  `MouseStickTests`. Falta: a captura do cursor e o F4 na janela (pedir ao lead/usuário)
  Confirmado pelo usuário (06/11/2026): mouse como analógico com F4 e o Gravar tudo no F8 funcionando

## Firmware e sistema

- [ ] **PRIORIDADE (11/11/2026)** Plugins também nos programas do sistema (achado ao revisar o loader, 11/11/2026).
  Hoje `Emulator.LoadPlugins` só roda quando o processo não é um programa do sistema (`if (!IsSystemProgram)` em
  `Emulator.cs`): os módulos `*KERNEL` só carregam quando um jogo abre (no console, o taiHEN carrega no boot, antes
  do shell), a seção `*main` (plugins do shell) não é reconhecida e `*ALL` e as seções por ID (`*NPXS10015`...) nunca
  valem para o shell e os apps do sistema. Corrigir de forma genérica: `*KERNEL` no boot do sistema, uma vez por
  sessão; `*main` para o shell; `*ALL` e `*<ID>` também para os programas do sistema, na ordem do taiHEN. Teste: um
  `.skprx` e um `.suprx` de teste (montados pelo `SceModuleBuilder`) carregados no boot do shell e num app do sistema.
  Regras exatas do taiHEN (revalidação de 12/11/2026, fontes: README do taiHEN; `taihen_internal.h`, `plugin.c`,
  `hen.c` e `taihen.c` do taiHEN; `lexer.c` e `parser.c` do taihen-parser): o `module_start` do taiHEN lê a
  configuração uma vez (`plugin_load_config`: `ux0:tai/config.txt`; só se esse não abrir, `ur0:tai/config.txt`, a
  "recovery config"; nunca os dois) e carrega os `*KERNEL` no boot (`plugin_load_all(KERNEL_PID, "KERNEL")`), a não
  ser com L segurado. Os plugins de usuário carregam num gancho de `sceKernelStartPreloadedModules`
  (SceKernelModulemgr, NID 0x432DCC7A): para **todo** processo, depois de iniciar os módulos pré-carregados dele, pega
  o ID com `ksceKernelGetProcessTitleId` (o SceShell é `main`, os apps do sistema têm o NPXS deles) e carrega cada
  caminho com `ksceKernelLoadStartModuleForPid(pid, path, 0, NULL, 0, NULL, &res)`, na ordem do arquivo; uma falha só
  vai para o log. L segurado pula os plugins daquele processo. Nomes de seção comparados exatos (strcmp, maiúsculas
  importam); `*ALL` vale para todo processo menos o kernel; `*!NOME` (halt): quando a seção bate, a leitura para na
  próxima seção. Arquivo com linha de 256+ caracteres, caminho antes da primeira seção ou seção sem nome é recusado
  inteiro (nenhum plugin). `taiReloadConfig` relê o arquivo (e pode carregar os `*KERNEL` de novo). O VitaSharp já lê
  o arquivo com essas regras (`Loader/PluginConfig.cs`, revalidação de 12/11/2026); falta só chamar nos processos do
  sistema (este item) e no boot para os `*KERNEL`.
- [x] PSM, depois do instalador (12/11/2026, teste do usuário; feito em feat/firmware-layout): UMA fonte do que o
  firmware guarda e onde, `src/VitaSharp.Core/Io/FirmwareLayout.cs` (jogos/apps em ux0:app, cartão gro0:app e
  grw0:patch/grw0:addcont, sistema vs0:app, PSP/PSone ux0:pspemu/PSP/GAME, SAVEDATA e LICENSE, PSM ux0:psm/<id>
  RO/RW, atualizações ux0:patch, DLC ux0:addcont, temas ux0:theme, licenças ux0:license/app|addcont|theme,
  metadados ux0:appmeta e ur0:appmeta, saves ux0:user/00/savedata e ur0:user/00/savedata, mídia ux0:music|video|
  picture, base ur0:shell/db/app.db); todo o código usa só ela (o teste `FirmwareLayoutTests` falha se alguém
  escrever essas pastas fora dela). Como o console faz (shell.self e dbrecovery_plugin do FW 3.74, só estudo): a
  reconstrução da base (dbrecovery_plugin, LLE) lista vs0:app, ux0:appmeta, ux0:pspemu/PSP/GAME e ux0:psm; um título
  de PSM é lido de ux0:psm/<id>/RO/Application/app.info, o shell monta ur0:appmeta/<id> (icon0.dds, pic0.png,
  LiveArea do modelo vs0:vsh/game/psm/contents) e grava categoria "mb", caminho ux0:psm/<id>; abrir a bolha roda
  ux0:app/PCSI00011/eboot.bin com o parâmetro "ux0:psm/<id>" (um título Unity, ux0:psm/<id>/runtime/eboot.bin).
  Correções: o instalador punha os itens "contents/" (o nome real) fora de RO (só aceitava "content/"): o shell não
  achava o app.info; o nome do jogo vem do app.info; licenças agora em ux0:license/app/<id> (as antigas em
  ux0:license/<id> migram sozinhas). Prova: boot do shell real headless com o NPPA00120 (cópia em layout novo): a
  bolha "Merum" com o ícone na página 3 e a entrada no app.db. A Biblioteca (Desktop e Android) lista o PSM com nome
  e ícone do app.info e abre pelo runtime com o mesmo parâmetro. Falta (item abaixo, PlayStation Mobile): o shell
  pede a atualização do runtime (vs0:vsh/game/psm/runtime_version.txt = 2.01; o PCSI00011 do usuário é 01.00: no
  console também pediria), e o próprio runtime (sceKernelAllocUnmapMemBlock, SceAppMgrUser 0x9D6D8058...).
- [ ] PlayStation Mobile (pedido do usuário, 11/11/2026; o runtime `PCSI00011` "PSM Runtime Package" 01.00, SDK
  2.00, já está instalado pelo usuário com a sua licença). 1) Rodar o runtime no VitaSharp sem janela e levantar o
  que o Mono dele usa e o que falta (HLE genérico do que faltar, nada específico do PSM). 2) Instalador de `.pkg` de
  PSM: feito (`PsmPkgInstaller`, feat/psm-install): `ux0:psm/<ID>/RO` (itens `content/`), `RO/License/<rif da
  conta>`, `RW/Documents|Temp|System/content_id|pm.dat`, licença de PSM de 1024 bytes obrigatória. Primeira execução
  sem janela do runtime (12/11/2026): `eboot.bin` (ScePsm) carrega libc/libfios2 e, por LLE, `libshacccg`,
  `libmono_bridge`, `libmono` e `libpsm` (heap `ScePsmGlobalHeap` de 16 MB, MJPEG); faltam
  `sceKernelAllocUnmapMemBlock` ("ScePsmDummyMemBlock", 0x2800000) e `SceAppMgrUser` NID `0x9D6D8058` (args -2,
  buffer, -4: devolve o ID do jogo de PSM a abrir?); sem ele o `sceAppMgrAppDataMountById(106, "")` falha
  (0x80802012), o runtime descarrega os módulos e aborta (`sceKernelCallAbortHandler`) antes de iniciar o Mono.
  Falta também como o shell inicia o runtime com o jogo (parâmetro/ID) e o registro de `ux0:psm` no app.db. 3) Validar com jogos de PSM do usuário e as licenças da conta dele. Nada de
  assinatura da Sony gerada, licença gerada ou remoção da proteção (PSSE): só a licença real do usuário.
- [ ] Plugins de usuário: hooks e injeção completos (pedido do usuário, 11/11/2026). Hoje os plugins `.suprx` do
  arquivo de configuração de plugins carregam no processo, e parte dos hooks/injeções já existe (ver Dead Space e
  libshacccg); completar todas as formas de hook (export/import/offset, cadeia de hooks com continuação, liberação) e
  de injeção de dados com restauração, com a mesma semântica e os mesmos códigos de erro do console, respeitados pelo
  JIT e pelo interpretador (invalidando blocos traduzidos). Validar com plugins de usuário reais e um teste por API.
- [ ] Plugins de kernel `.skprx` (pedido do usuário, 11/11/2026: "eu quero tudo, todos os plugins, quero que a emulação
  seja completa"), depois do item acima. Rodar o código do plugin no processador emulado num "modo kernel" do VitaSharp
  e implementar em HLE as exportações de kernel que os plugins importam (ksceIo*, memória, ksceKernel* de threads e
  módulos, SceSysmemForKernel, SceModulemgrForKernel e as de hook/injeção de kernel), com os hooks aplicados no
  código do jogo e dos módulos. Inclui os plugins que mexem no hardware, ligando a camada que eles tocam ao que o
  VitaSharp emula: clock -> clock emulado, controles Bluetooth/USB/HID -> controles do host, armazenamento USB/SD2Vita
  -> pastas do host, tela/brilho/áudio. O ambiente é genérico e não bloqueia plugin nenhum (decisão do usuário): roda
  o código que o usuário instalar, como o console. O projeto não implementa, não testa e não ajusta nada voltado a
  contornar DRM; os jogos comprados rodam pela licença real do usuário. Validar com uma lista dos plugins públicos mais
  usados (um teste por exportação e cada plugin rodando).
  Parte feita (11/11/2026, kern1): o ambiente de kernel (plugins `*KERNEL` com o código original, exportações de
  kernel do firmware em HLE, separação kernel/usuário no linker); o taiHEN real carrega e falha sozinho (hooks de DRM),
  então os plugins que dependem dele (kubridge, fd_fix) não iniciam. Detalhes, limites e o plano do resto no item "O emulador é genérico" abaixo e em `docs/notes/plugins.md`.
- [ ] Temas do Vita (pedido do usuário, 10/11/2026): instalar e usar temas como no aparelho. (1) Instalação: `.pkg` de
  tema da loja (content type de tema; o agente de PKG já detecta o tipo) com a licença, extraído para `ux0:theme/<id>`
  com o `theme.xml` e as imagens/sons, e registrado como o firmware faz (banco do shell / app.db, lista de temas das
  Configurações); também os temas personalizados que o Vita aceita. (2) Uso pelo shell de verdade: Configurações ->
  Tema e fundo lista os temas instalados, aplicar troca a tela inicial, a tela de bloqueio, os ícones do sistema, o
  fundo e a música de fundo, como no Vita (o shell.self/libpaf faz isso sozinho se o sistema embaixo responder certo:
  arquivos em ux0:theme, registro `/CONFIG/THEME`, notificação ao shell). (3) Launcher: instalar tema pelo mesmo
  diálogo de `.pkg` e mostrar o tipo. Validar com um tema oficial gratuito e um personalizado: aplicar, reiniciar o
  shell e conferir que continua aplicado.
- [ ] Jogos de PSP pelo emulador de PSP do próprio Vita (pedido do usuário, 10/11/2026: "eu quero que ele emule tudo o
  que seja possível do psvita"). O Vita roda PSP com o `pspemu` do firmware (código ARM que traduz o MIPS do PSP) e os
  serviços de compatibilidade (`SceCompat`, `ScePsp2Compat`: tela, áudio, arquivos, controles, memória compartilhada).
  Caminho: rodar o `pspemu` real em LLE dentro do VitaSharp, como o shell e os apps do sistema, e completar o HLE dos
  serviços que ele usa (não escrever um emulador de PSP próprio: não reproduzir o PPSSPP). Passos: (1) descobrir como
  o shell inicia um título de PSP (PBOOT/EBOOT.PBP, ISO/CSO, `ux0:pspemu`) e quais módulos do firmware carrega;
  (2) carregar o pspemu e seguir as funções que faltam pelos logs; (3) primeiro um homebrew de PSP simples, depois um
  jogo; (4) medir o desempenho (é emulação dentro de emulação: esperar lento no início, otimizar depois). Exige o
  firmware com os módulos do pspemu; nada do firmware vai para o repositório
  - Como o SceCompat move o núcleo de compatibilidade (estudo do bootimage SceCompat 3.74, lote 11, 10/11/2026): o
    pspemu (NPXS10028) pede uma janela de memória (`sceCompatAllocCdramWithHole`: 64 MB com um buraco em +32 MB, vista
    cached e uncached, mais um bloco de CDRAM do kernel); `sceCompatInitEx` carrega `vs0:app/NPXS10028/pcff.skprx` em
    CDRAM+0x3000000 e `os0:kd/pcbc.skprx` em CDRAM+0x600000 (SceSblFwLoader), sobe o clock (ScePower 0x14D) e reserva um
    núcleo ARM para eles (SceGrab); `sceCompatStart` liga esse núcleo. Daí em diante o PSP do lado do núcleo e o pspemu
    conversam por um bloco compartilhado: 16 canais de pedidos (`sceCompatWaitAndGetRequest`, fila de 16 por canal),
    interrupções nos dois sentidos (`sceCompatInterrupt` 1..15, `sceCompatWaitIntr` 0..2), registradores de 32 bits
    (`Read/WriteShared32`, `WriteSharedCtrl` com o controle), valores de retorno (`ReturnValueEx`), o bloco "dat" de
    0x1038 bytes e um aperto de mão em CDRAM+0x3D78000 (estados 1/2/3) para boot, suspensão e retomada. No HLE de hoje
    (`Compat.cs`) tudo o que não depende do núcleo segue o módulo; InitEx/Start estão marcados default (0x80027205) e
    NÃO contam como feitos. Para emular: rodar o pcff.skprx/pcbc.skprx num segundo núcleo ARM emulado (bare-metal, com
    o hardware que ele toca), ligado ao bloco compartilhado acima. Obs.: o `ScePsp2Compat` não é do pspemu: é a camada
    POSIX do WebKit (vs0:data/external/webcore).
  - Instalação de `.pkg` de PSP e PS1 sem licença (feat/pkg-psp-psx, 10/11/2026, pedido do usuário). `PkgReader` lê
    os pacotes de PSP/PSone da PS Store (formato PS3, tipo de chave 1: a tabela de itens e o conteúdo PSP, itens com
    byte 24 = 0x90, na chave PSP; o invólucro PS3 na chave PS3) e diz o tipo (`PkgKind`: jogo de Vita, atualização,
    DLC, tema, PSM; PSP, PSP Go 0xE, minis 0xF, NEOGEO 0x10, PC Engine (7 + categoria HG), DLC de PSP (7 + bloco 10
    com a pasta de instalação), tema de PSP 9, PSone 6; tabela do pkg2zip/psdevwiki). PSP/PS1 não pedem licença no
    launcher, no Android nem no CLI (`install <pkg>`); Vita continua exigindo (`LicenseRequiredException`).
    O que a instalação deixa para o pspemu (o layout de um título baixado da loja num Vita de verdade, onde o
    `dbrecovery_plugin` do firmware procura: ele lista `ux0:pspemu/PSP/GAME` e `ux0:pspemu/PSP/GAME/%s` na
    reconstrução do banco; a instalação apaga `ux0:id.dat` para o shell reconstruir):
    - Jogo de PSP: `ux0:pspemu/PSP/GAME/<DISC_ID>/EBOOT.PBP` (o eboot NPDRM como vendido, com PARAM.SFO/ICON0/PIC1
      dentro; a pasta leva o DISC_ID do PARAM.SFO do eboot, como o lusid1/pkg2zip), os outros arquivos de
      `USRDIR/CONTENT` (DOCUMENT.DAT, DOCINFO.EDAT, PSP-KEY.EDAT, CONTENT.DAT, EDATs) como estão, e `__sce_ebootpbp`
      (a assinatura do eboot, `PspEbootSignature`: o mesmo formato que o HLE de `_vshNpDrmEbootSigGenPsp` gera e que
      `_vshNpDrmEbootSigVerify`/`PspEbootVerify` aceitam; o shell.self importa essas quatro funções, o dbrecovery não).
      Nada do jogo é decifrado: só a camada do pacote (chaves fixas) sai; EBOOT.PBP e EDATs ficam NPDRM, como vendidos.
      (Até 11/11/2026 havia também a ISO decifrada e o KEYS.BIN derivado do eboot, como o pkg2zip faz; removidos por
      decisão do lead: decifrar o jogo sem a licença do usuário é contornar o DRM.)
    - PSone Classic: `ux0:pspemu/PSP/GAME/<DISC_ID>/EBOOT.PBP` (PSISOIMG/PSTITLEIMG, NPDRM), DOCUMENT.DAT e
      `__sce_ebootpbp` (tipo 2);
      `texture.enc` vai para `ux0:ps1emu/<DISC_ID>`. Multidisco (`__sce_discinfo`, tipo 3) ainda não é gerado.
    - DLC de PSP: os arquivos em `ux0:pspemu/PSP/GAME/<pasta do bloco 10>/`; tema: `ux0:pspemu/PSP/THEME`.
    - Licença: só a do próprio usuário (decisão do usuário, 11/11/2026: nada de licença gerada). O shell procura
      `ux0:pspemu/PSP/LICENSE/<content id do eboot>.rif` (o content id que o eboot leva em DATA.PSP + 0x560; no PSone
      de teste ele difere do content id do pacote). O instalador grava ali o .rif do console do usuário quando ele o
      dá (CLI: `install <pkg> <seu .rif> [--act=act.dat]`; launcher: o .rif e o act.dat soltos junto com o .pkg), e o
      act.dat em tm0:npdrm/act.dat; confere o content id do rif com o do título. Sem eles o jogo instala e o sistema
      mostra a mensagem normal de falta de licença (C0-11136-2). Falta: os campos opcionais no diálogo do launcher.
    Para o trabalho do pspemu: o pspemu de verdade (NPXS10028) abre o EBOOT.PBP NPDRM e pede a licença
    (`sceCompatSetRif` aponta para `ux0:pspemu/PSP/LICENSE/<content id>.rif`) e a ativação. Use o EBOOT.PBP (é o que
    o firmware roda) com a licença do usuário. Validado: testes `PspPkgInstallTests` (pacotes sintéticos montados no
    teste, `PspPkgFixture`: detecção de cada tipo, layout, assinatura, DLC, licença do usuário,
    nomes que saem da pasta recusados, Vita sem licença recusado, pacote PS1 no
    formato PS3). Com pacotes reais do usuário (só leitura, instalados numa pasta de dados de teste): um jogo de PSP
    (0,5 GB) instala em 36 s pelo CLI sem licença; o EBOOT.PBP instalado é o NPUMDIMG original; no `boot-shell` (shell de verdade, sem cabeça) a reconstrução do banco (dbrecovery) pôs a
    bolha do jogo na tela inicial com o ícone e o nome. Um PSone Classic real veio no formato PS3 (cabeçalho tipo 1,
    content type 1, arquivos em `<ID>/EBOOT.PBP`, tudo na chave PS3, sem PARAM.SFO próprio; lusid1/pkg2zip trata
    igual): `PkgReader` reconhece esse formato; instala em 2 s (EBOOT.PBP com PSISOIMG, DOCUMENT.DAT,
    `__sce_ebootpbp`). Com os dois instalados, o shell mostra as duas bolhas na mesma página (nomes certos).
    As duas diferenças vistas no shell (11/11/2026):
    - "Você deve atualizar o software de sistema para a versão - ou posterior" ao tocar na bolha do PSone: o shell
      pergunta a versão do PSP do emulador por SceCompatForVsh (NID 0x9C107CC9 = sceCompatGetPspSystemSoftwareVersion,
      6.61 = 0x295; o mesmo código do SceCompat no bootimage) e o HLE só exportava o nome do SceCompat. Corrigido
      (mais os outros dois exports do SceCompatForVsh: 0x4915DEE7 GetTitleList, 0x70997F92 IsPocketStationTitle).
      Agora a bolha abre a LiveArea do jogo (com "Iniciar") e "Iniciar" para em "Não é possível iniciar esse
      aplicativo... (C0-11136-2)": a checagem de licença do título, que não tem rif (o passo do pspemu abaixo).
    - A bolha do PSone sem ícone é o firmware: o shell monta o ícone da bolha (ICON0 sobre vs0:vsh/game/psp_icon_base.raw,
      gravado em ur0:appmeta/<ID>/icon0.dds) e, para a categoria ME (PSone), recusa um ICON0 com mais de 80 pixels de
      largura (erro 0x80101134; PSP: até 144); o ICON0 desse eboot é 144x80, então o firmware não gera o ícone. Não é
      um defeito do emulador nem da instalação (não mexemos nos arquivos do jogo). Ponto em aberto (o lead lembra que
      no Vita de verdade as bolhas de PSone têm ícone): ver se o caminho de instalação da loja (promoter,
      ux0:pspemu/temp/promote) grava outro ícone; a montagem do ícone no shell é uma só (FUN_81037978, chamada pelo
      dbrecovery e pela promoção).
  - Do toque em "Iniciar" até o pspemu de verdade (11/11/2026, com os dois jogos reais de teste, um de PSP e um PSone):
    1. Licença: o shell (FUN_8101bd12/FUN_8101f8b2) lê o content id do eboot (DATA.PSP + 0x560), abre
       `ux0:pspemu/PSP/LICENSE/<content id>.rif` e chama `_sceNpDrmGetRifInfo`; sem o arquivo: C0-11136-2 (a validação
       abaixo usou um rif de teste numa pasta de dados de teste, fora do repositório). A forma
       syscall do GetRifInfo é (rif, tamanho, checkSign, ponteiro para os outros 9 argumentos); o HLE agora lê o rif
       (content id, conta, versão) e devolve validade de 0 a 0x7FFFFFFFFFFFFFFF ("sem expiração"; com a ordem errada
       dos ponteiros o shell dizia "ainda não atingiu a data de início").
    2. O shell inicia `vs0:app/NPXS10028/eboot.bin` (sceAppMgrLaunchAppByPath4, com o título do jogo) com o parâmetro
       "originalpath=ux0:pspemu/PSP/GAME/<ID>&selfpath=.../EBOOT.PBP&discid=<ID>&parentallevel=3&gamedataid=&appver=00.00&bootable=1&category=ME"
       (para PSP, "EG"). O HLE não repassava esse parâmetro ao processo novo: o pspemu lia "" e saía com 0x80010016;
       corrigido (sceAppMgrGetAppParam devolve o parâmetro do launch).
    3. O pspemu de verdade sobe: escolhe 444 MHz, cria as threads (ScePspemuMain, TitleInfo, ErrDlg, PrepareSuspend,
       PromoteRequest), inicia o GXM (framebuffer 480x272), pega a janela de memória (`sceCompatAllocCdramWithHole`),
       ajusta o cache das duas metades (`sceCompatCache` 0x2000000 + 0x1000000), registra os semáforos de suspensão e
       para em `sceCompatInitEx(1)`, que o HLE recusa (0x80027205): o núcleo de compatibilidade não é emulado. O
       pspemu então mostra o erro e volta à LiveArea (que fica com "Continuar" e a tela de aviso do PSone).
    Onde para e o que falta: InitEx carrega `vs0:app/NPXS10028/pcff.skprx` e `os0:kd/pcbc.skprx` no CDRAM e liga um
    núcleo ARM. Decifrados com o self2elf: o pcff é o flash0 do PSP 6.61 (fontes, `kd/*.prx` com os módulos `kermit_*`
    que falam com o Vita), dados; o pcbc (SceSblPcbcBin, 0x5CE0 bytes) é o código do núcleo, ainda cifrado (entropia
    7,7 bits/byte; os 0x60 primeiros bytes cifrados, cabeçalho em 0x60, o resto cifrado até 0x5400): o
    SceSblFwLoader o decifra no processador seguro, com chaves que o projeto não tem e não vai procurar nem derivar
    (decisão do lead, 11/11/2026). O caminho do núcleo de compatibilidade PARA aqui até existir o arquivo:
    - O que precisa: o SceSblPcbcBin decifrado, isto é, o código que o SceSblFwLoader coloca em CDRAM+0x600000 quando o
      pspemu chama sceCompatInitEx (o conteúdo de os0:kd/pcbc.skprx depois da decifração do processador seguro),
      tirado pelo próprio usuário do console dele (como os arquivos do firmware): por exemplo um plugin no console do
      usuário que, depois do sceCompatInitEx do pspemu, copia essa região de CDRAM para um arquivo.
    - Onde pôr: `%APPDATA%\VitaSharp\fs\os0\kd\pcbc.bin` (o binário puro, como fica na memória do núcleo), ao lado do
      pcbc.skprx do firmware instalado. Nada disso vai para o repositório.
    - Com ele: (a) o InitEx do HLE carrega o pcbc.bin em CDRAM+0x600000 e o pcff (já decifrável pelo self2elf, é
      dado) em CDRAM+0x3000000; (b) emular esse núcleo ARM (bare-metal, CDRAM, o bloco compartilhado dos
      pedidos/interrupções do SceCompat descrito acima) e o lado PSP (Allegrex) que o pcff traz. Os dois jogos de teste param nesse ponto (o de PSP chama antes também `sceCompatFrameBufferInit`).
- [ ] Emular tudo o que for possível do Vita (diretriz do usuário, 10/11/2026): além dos jogos e do sistema, listar
  os recursos do aparelho ainda não emulados (ex.: PSP, PS Mobile/PSM, Remote Play/PS TV, Near, Party, Content
  Manager, câmera/AR, apps de vídeo/música/browser) e abrir um item por recurso com o caminho (LLE do módulo real +
  HLE do que falta); os grupos que o plano do VitaSDK marcou como "não se aplica" (Mono do PSM, Piglet, ShaccCg)
  entram aqui como recursos a emular, não como ignorados
- [ ] Idioma do sistema por jogo (pedido do usuário, 09/11/2026): opção "Idioma do sistema" nos ajustes de cada jogo
  (desktop e Android) que sobrepõe o idioma que o jogo recebe (SceSystemParamLang / `/CONFIG/SYSTEM/language`), para
  jogos sem o idioma do aparelho. Caso real: Killzone Mercenary PCSC00045 (lançamento asiático) só tem japonês
  (padrão) e inglês; com o sistema em pt-BR o jogo cai no japonês. Padrão: seguir o idioma do sistema/app
- [ ] HLE lote 10b (09/11/2026, feat/hle-batch-10b, parado no fim do dia): feito o SceLibMtpHttpWrapper (26), o
  SceLibMtpHttp (28, libhttp/libssl sobre o MTP numa instância própria e offline do Http/Ssl), o sceHttpSetProxy
  (SceHttp 53B722F2), o SceLibMtp (153: núcleo Init/Start/Stop/End, sem PC conectado = os erros da checagem de conexão
  da biblioteca, configurações locais, callbacks, Wi-Fi sem PC registrado) e o SceMtpClient (163: sem cliente IPC do
  SceMtpService no HLE, então NOTINIT; observadores, ouvintes e textos de erro funcionam). Tudo firmware-fallback
  (o shell continua com os módulos de verdade). Sistema 2.819 -> 3.129. Testes de unidade em HleBatch10bTests.
  Próximo passo: livespace_db (SceLsdb, 162; os getters são buscas no std::map do AppInfo: nó +0x10 chave, +4/+8
  filhos, +0x29 nil, +0x18 tipo (0 int64 em +0x20, 1 string em *(+0x20)); chaves como 0xA1541070 nome, 0x21CA76B1
  categoria), o email_engine (223: classes C++ com vtables do módulo, getters/setters, construtores e destrutores) e os
  helpers do ScePsp2Compat. Falta validar o shell headless com este build (só testes de unidade).
- [x] Diálogos comuns com interface de verdade (08/11/2026): o sceMsgDialog (e os outros diálogos comuns sem UI)
  respondia sozinho; hoje um SIM/NÃO responde SIM, menos logo depois de um NetCheckDialog cancelado, quando responde NÃO
  uma vez (282ec7d, 99Vidas sem rede). Como no Vita3K, mostrar o diálogo por cima do jogo e deixar o usuário escolher
  com os botões/toque, para não depender de respostas automáticas
  Feito (08/11/2026, agente de produto, feat/product-2): `IMessageDialogBackend` (`HostServices.MessageDialog`); o
  sceMsgDialog (mensagem do usuário com os botões do Vita3K: OK, SIM/NÃO, OK/CANCELAR, CANCELAR, 3 botões; mensagens
  do sistema; código de erro; barra de progresso) e as mensagens/confirmações do SaveDataDialog (usuário, sistema com
  os textos do Vita3K por dispType, código de erro, barra de progresso) esperam a escolha quando há janela; sem ela
  (headless, `--dialogs=auto`) ficam as respostas automáticas de antes. Desktop: `Host/MessageOverlay` no
  `SetCenterOverlay` (setas/Tab, Enter/Espaço, Esc = botão de cancelar, direcional/Cruz/Círculo, clique; Círculo
  confirma quando o botão de confirmar do sistema/jogo é o Círculo; fonte do sistema com glifos japoneses/chineses).
  Validado no 99Vidas (janela oculta, roteiro): "Deseja tentar novamente?" aparece com Sim/Não, NÃO segue até o
  gameplay; Soccer Tsuku (aviso da PSN em japonês, OK). Nenhum jogo da biblioteca mostrou um SaveDataDialog de
  mensagem em 8 tentativas (GR, Soccer Tsuku, RBS, Minecraft, NFS MW, Sonic Racing): essa parte só tem teste de unidade
  (`DialogHleTests`). `msg-preview` desenha o painel; `--overlay-dump=<pasta>` grava os painéis de um teste
- [x] Música de fundo do shell não toca (relato do usuário, 06/11/2026): no Vita real a tela inicial/LiveArea toca a
  música de fundo do sistema (e a dos temas); aqui o shell fica mudo. Ver se o shell abre a porta de áudio/BGM (SceAudio,
  `sceAppMgrSetBgmProxyApp`, o serviço de música do shell, arquivos de som em vs0:data/internal ou do tema) e o que falta
  para ela tocar; validar com gravação do áudio (F8) na tela inicial e comparar com um vídeo do Vita real
  Causa (07/11/2026, fix/system-apps-9): o shell toca `pd0:data/systembgm/home.at9` (registro /CONFIG/SHELL/bgm,
  player SceShellMusicCore na porta BGM), mas o pd0 é dado de fábrica que nenhuma atualização traz e a instalação do
  usuário (do PSVUPDAT.PUP) tem o pd0 vazio. Com o pd0 do Vita3K numa cópia dos dados a tela inicial toca: porta BGM
  48 kHz, 33 s em 33 s, 0 underruns; o espectrograma bate com o home.at9 decodificado pelo ffmpeg (correlação 0,98;
  trecho sem relação 0,80), mesmo nível (`fw9_out/bgm2_audio/03_port2_BGM_48000.wav`). Agora: a instalação
  do PUP copia o pd0 do Vita3K quando ele existe, `install-firmware --pd0[=<pasta>]` copia só o pd0 e o shell avisa no
  log quando falta. O usuário precisa rodar `install-firmware --pd0` uma vez (ou reinstalar o PUP). O dump de áudio
  abre um WAV novo quando a porta muda de taxa (o BGM abre a 44,1 kHz e passa a 48 kHz: o WAV dizia 44,1).
  Confirmado pelo usuário (07/11/2026)
  Música parando com um app na frente (relato do usuário 08/11/2026, 1834769): confirmado pelo usuário (08/11/2026)
- [x] Licença em zRIF na instalação de `.pkg`: hoje `PkgInstaller` só aceita o arquivo `.rif`/`work.bin` (lê a
  klicensee em 0x50). Aceitar também a licença no formato zRIF (o texto base64 com o `.rif` comprimido em zlib, com
  o dicionário fixo, que o NoNpDrm exporta das licenças do próprio Vita do usuário): decodificar, descomprimir, validar
  o content ID contra o `.pkg` e gravar o `.rif` em `ux0:license/<TITLEID>` e o `work.bin`; no CLI (`install <pkg>
  <rif|zRIF>`), no launcher (campo de texto na instalação) e no app Android. Core, CLI e app Android feitos
  (04/11/2026, `Package/Zrif.cs`, fix/user-bugs); falta o campo de zRIF no launcher do desktop. Campo feito (567e200:
  diálogo com o texto do zRIF ou o botão do arquivo). Validado 05/11/2026 só pela CLI com o `.pkg` real do usuário (WRC 4,
  Downloads) e um zRIF gerado do `.rif` dele (zlib com o dicionário, janela 1 KB: começa com `KO5ifR1dQ+`): instalou os
  1,5 GB numa pasta de teste e gravou um `.rif` idêntico ao do usuário (`fw_out/pkg_install/install_log.txt`). O diálogo
  do launcher ainda não foi clicado (precisa da tela)
  Validado no launcher novo (08/11/2026, feat/product-1): `ui-render --install-pkg=<pkg> --license=<zRIF>` abre o
  diálogo de licença de verdade (sem janela), digita o zRIF, aperta Instalar e a fila instala o `.pkg` numa pasta de
  dados de teste; o `.rif` em `ux0:license/<TITLEID>` e o `work.bin` saem iguais ao zRIF decodificado. Teste
  `PkgInstallTests` (um `.pkg` montado no teste, `PkgFixture`): instalação com zRIF e recusa de licença de outro jogo
  Confirmado pelo usuário (08/11/2026)
- [ ] Sistema do Vita de verdade (ver docs/notes/system-apps.md): o shell já mostra os jogos, roda vários processos e faz a
  configuração inicial; falta o app de Troféus/Configurações desenharem (composição da tela compartilhada), o botão PS
  no PC, o texto cortado no boot direto, o travamento do teclado na tela (IME) e as funções restantes (sensor de
  movimento, sockets do sistema)
  Achados (05/11/2026, fix/user-bugs-6): a configuração inicial completa nos três caminhos ("Não tenho conta",
  "Tenho conta" + "Definir depois" = configuração de uso experimental da PSN, "Usar" = tela de Sign-In; prints em
  `fw_out/setup_*` da pasta de trabalho local); o "Definir depois" travava em "Aguarde" (pedido de sistema do SceNpManager, 4fbab47)
  e depois mostrava "É preciso atualizar o software do sistema" (checagem de vínculo da conta, 82365bb).
  Teclado na tela (IME): não é um travamento, é a tela a ~1 fps sem teclado. Reproduzido na tela de Sign-In da
  configuração inicial (tocar no campo; `fw_out/ime_diag`, `ime_trace3`): o ime_plugin do shell abre e o **shell passa
  a apresentar quadros próprios** (sceGxmDisplayQueueAddEntry a cada quadro, antes só UpdateProcessBegin/End); o
  initialsetup NÃO é cliente do SceSharedFb (apresenta sozinho com sceDisplaySetFrameBufInternal), então no Vita o
  teclado é o **plano de sobreposição do shell** que o display compõe por cima do plano do app (mesmo mecanismo das
  notificações e do menu do botão PS sobre jogos). O VitaSharp descarta os quadros do shell quando ele não está em
  primeiro plano (`Display.sceDisplaySetFrameBuf`: só o processo da frente apresenta), por isso o teclado nunca aparece.
  A lentidão: a cada quadro a `SceShellMain` abre a cena (BeginScene), **depois** espera a superfície de display
  (`ScePafDisplaySurfaceCond`, ~400 ms) e só então termina a cena; o `sceGxmBeginScene` do setup espera a cena do shell
  fechar (arbitragem de cena entre processos no `GpuQueue`) e o worker da GPU segura os desenhos da cena aberta até o
  EndScene (gate dos uniform buffers, 250 ms de timeout): `VITASHARP_UB_SCENE_END=0` sobe de 1 para 7 fps, o resto é a
  arbitragem. Próximo passo: (1) compor o quadro do shell (A8B8G8R8 com alpha) por cima do quadro do processo da
  frente quando o shell apresenta sem estar na frente (um segundo `DisplayFrame` no `PresentCommand`, blit com blend
  no `VulkanRenderer.Present`); (2) não segurar a submissão do outro processo enquanto a cena aberta ainda não tem
  desenhos, ou fechar a arbitragem só no primeiro desenho.
  Rodada fix/system-apps-5 (05/11/2026): (1) feito (efecbd5, `VulkanRenderer.ShellOverlay.cs`): o teclado aparece por
  cima da tela de Sign-In com a página visível em volta (`fw5_out/ime_new/kb2.png`; antes sem teclado,
  `fw5_out/ime_base/kb2.png`). Continua a ~1 fps (0,6-1,0 fps medido; antes 0,8) e **as teclas não digitam**. Tentado
  sem ganho (revertido; patch `fw5_all_wip.patch` na pasta de trabalho local): (a) segurar o BeginScene de cada processo até o
  comando seguinte dele (a arbitragem começa no primeiro desenho): a `SceShellMain` desenha **antes** de esperar a
  `ScePafDisplaySurfaceCond`, então nada muda; (b) a espera da thread da display queue pela cerca contar como espera
  do guest (libera o gate dos uniforms para desenhos anteriores a ela): sem efeito; (c) rotear o toque para o shell
  enquanto a região de toque 2 dele está ativa (com o teclado aberto o ime_plugin faz `sceTouchSetRegion(0, 2,
  {0,0,1920,1088})` + `sceTouchActivateRegion(0, 2, 1, 4)` a cada quadro): o roteamento liga, mas durante os toques
  (10 s cada a 1 fps) nem o shell nem o setup chamam `sceTouchPeek*` com o dedo no painel; não validado.
  `VITASHARP_UB_SCENE_END=0` sobe para ~6 fps (`fw5_out/ime_ub0`). No fim de cada execução a `SceShellMain` espera a
  `ScePafDisplaySurfaceCond` e o `SceInitialSetupMain` está dentro do `sceGxmBeginScene` (lr 0x83C763F9: liberação do
  buffer de display ou arbitragem). Próximo passo: contar no worker da GPU, por processo, quanto tempo cada cena fica
  aberta e quem espera quem (gate, arbitragem, cerca da display queue) em vez de trace HLE (o trace deixa tudo lento
  demais para medir); depois escolher entre gravar as cenas de processos diferentes em render passes separados (sem
  arbitragem) ou liberar o gate quando outro processo espera a cena.
- [x] Apps do sistema rodados sozinhos (`run NPXS...`, como o launcher abre um app do sistema) caem (10/11/2026, agente
  B, fix/bugs-b-4; logs das rodadas sem cabeça do lote HLE 11 em `<scratchpad>\hle11_out\x1_*`): NPXS10015/10004/10002
  (prefetch abort na `ScePafJobDefault`), NPXS10014 -> NPXS10018 (`SceSignup`), NPXS10009 (`SceMBJobFast`) e o NPXS10026
  (sai na hora com `sceKernelExitProcess(0x80020583)` e o emulador fica 150 s sem quadro). Causa única: sem o shell não
  existe o servidor IPC do serviço do shell; o libshellsvc de verdade avisa "[SceShellSvc] *WARNING* IPC Server is not
  available. (0x80020583)" (o MTP do Content Manager/Music: "Connect failed: 0x80020583") e depois chama por um
  ponteiro de serviço NULL (pc 0 / 0x80000000, lr dentro do SceShellSvc ou do app). Dentro do `boot-shell` os mesmos apps
  abrem pelo LiveArea. Tentado (revertido; patch `<scratchpad>\bB4\item3_shell_launch_wip.patch`, com testes): `run` de
  um app do vs0 bootar o shell e pedir o app a ele: (a) pedido por URI `psgm:play?titleid=NPXS10015` (evento 6, como
  `sceAppMgrLaunchAppByUri`): o shell mostra "Ocorreu um erro (C2-12570-5)" e cancela (o applauncher_plugin recusa,
  erro 0x80103907); (b) pedido por nome (evento 6, como `sceAppMgrLaunchAppByName` de outro app): o shell cancela na
  hora (`sceAppMgrLaunchAppCancel`, FUN_8127ac1e do shell 3.74); com o +0x74 bit 0 no status dos serviços de fundo
  (NPXS10079) também; (c) criar o processo ao lado do shell como o `LaunchAppByPath4` faz e ativá-lo: Configurações
  abre e navega sem cair (`<scratchpad>\bB4\runs\seti`), mas o shell não lançou o app e continua compondo a tela de
  bloqueio/início por cima. Próximo passo: o launcher abrir os apps do sistema pelo shell de verdade (boot-shell e o
  toque no ícone, ou achar a resposta que o applauncher_plugin espera para um pedido de outro app), em vez do `run`
  sozinho
  Corrigido (10/11/2026, agente B, fix/bugs-b-5; provas em `<scratchpad>\bB5\runs\a_*\strip.png`): o `run` de um app do
  vs0 (o que o launcher usa) boota o shell de verdade e, quando ele está de pé (iniciou o primeiro serviço de fundo,
  NPXS10079, depois da tela de boot e da atualização do banco), pede o app como outro app pede
  (sceAppMgrLaunchAppByName com flags 0x20000, evento 6). No shell 3.74 o handler (0x8127D402 -> GetAppInfoJob ->
  0x812782CC) abre a LiveArea do título para todo pedido e só mantém o pedido (e inicia o app com o 2ndStage) quando ele
  tem 0x20000 (o que a Música usa para o Gerenciador de conteúdo); sem ele, e no `psgm:play?titleid=`, ele cancela e
  deixa a LiveArea aberta no portão: era o "cancela na hora" das tentativas (a)/(b). Pedido antes do shell estar de pé
  derruba o shell (salto para 0 em 0x812787E0: o applauncher_plugin ainda não foi carregado). O app abre atrás da tela
  de bloqueio, como no Vita; enquanto o shell a mostra (sceAppMgrSetSystemImposeState2(1), 0 ao desbloquear) os quadros
  do shell ficam por cima e os botões e toques são do shell (era o "continua compondo por cima" da tentativa (c): a
  entrada ia para o app). Validados (headless, desbloquear, abrir um item, voltar, botão PS -> LiveArea com Continuar,
  0 crash): Configurações (Atualização do sistema), Música (Conectar-se ao dispositivo), Vídeos, Navegador (sem rede:
  C2-14921-7, esperado), Troféus (lista e um jogo), Gerenciador de conteúdo (cartão de memória), Email (Adicionar
  conta). `--no-shell` roda o app sozinho como antes; `--launch-uri=` abre uma URI pelo shell
- [x] Validar a fundo TODOS os apps do sistema (04/11/2026, pedido do usuário): cada menu, aba, categoria, sub-página,
  botão, menu de opções e caminho de voltar/fechar de cada app, abrir e fechar duas vezes, alternar entre apps (botão PS,
  retomar, encerrar); nada pode travar ou crashar. Erros só por falta de rede/PSN/USB/aparelho ficam anotados como
  esperados. Primeiro caso: Música → Artistas mostra erro (log do usuário 18:55, `AVCH ... SQL Failed: DROP TRIGGER`,
  `IO: unmapped path ''`). Entregar checklist por app em `docs/notes/system-apps.md` e roteiros (.ps1/.sh) para rodar de
  novo como teste de regressão (branch fix/system-apps-4)
  Estado (05/11/2026, fix/user-bugs-6): runner `build/sysapps-sweep.ps1/.sh` + `tests/nav/sysapps/{settings,music}.nav`;
  checklist em docs/notes/system-apps.md. Validados: configuração inicial, Configurações (todas as páginas menos
  Sistema/Formatar/Economia de energia e a segunda abertura: o roteiro precisa de ajuste), Música
  (Artistas/Álbuns/Conectar; o `SQL Failed: DROP TRIGGER` sai em todo início antes de qualquer banco ser aberto e não
  é visível; `unmapped path ''` é o ini_file_processor removendo um temporário sem nome, inofensivo), PS Store (erro
  NW-2288-0 esperado), Navegador (favoritos; página fica em "Aguarde" sem rede), Troféus. **Aberto**: Amigos (NPXS10006)
  tela preta depois de Iniciar (`fw_out/x_friends/log.txt`), regressão do que estava corrigido; miniatura preta no
  LiveArea dos apps suspensos; e os apps que faltam varrer (Mensagens, Party, PS4 Link, Controle dos pais, Vídeos, Fotos
  + câmera, Email, Calendário, Gerenciador de conteúdo, near, PS3 Remote Play). Câmera: o 3.74 não tem `vs0:app/NPXS10007`
  (nem no Vita3K); a câmera é o modo do app Fotos (NPXS10004, `photocam_plugin.rco`); a webcam do PC funciona
  (`camera-test 0`: Integrated Camera 640x480). Texto cortado no boot direto: não testado nesta rodada
  Rodada fix/system-apps-5 (05/11/2026): roteiros para todos os apps que faltavam (`tests/nav/sysapps/*.nav`, um
  gerador só) e checklist em docs/notes/system-apps.md; 0 crash. Corrigidos: Amigos (ac4507b, safe memory), miniatura
  preta no LiveArea (44180e7), fechar pelo LiveArea levava 10 s (03cbb63). Validados: Mensagens, Party, PS4 Link,
  Controle dos pais, Vídeos, Fotos (câmera com `--camera=0` mostra a webcam), Email, Gerenciador de conteúdo, PS3 Remote
  Play, Configurações (segunda abertura). **Abertos**: tela de bloqueio depois que um app termina (evento 4 do shell,
  veja o checklist); near preto (SceLibLocation NID_EFC9BD25/2311B24A); Calendário cai na tela de bloqueio quando o
  Calendar BG termina; Navegador em "Aguarde" não é rede: não faz chamada de rede nenhuma e nunca carrega o motor web
  (`vs0:data/external/webcore`); barra de status ausente sobre Mensagens/Amigos/Party; Configurações > Sistema/
  Formatar/Economia de energia ainda não abertas (`settings_tail.nav` escrito, rodar de novo). Texto cortado no boot
  direto: não reproduzido (`run NPXS10015` mostra o texto inteiro). Botão PS no PC: não testado (precisa da janela)
  Rodada fix/system-apps-6 (05/11/2026, provas em `fw6_out/` da pasta de trabalho local): **tela de bloqueio** corrigida (0e92acf:
  evento 4 com o layout do kernel, +8 estado, +0xC/+0x18 -1, +0x20 title id; depois de fechar volta a tela inicial,
  `close1/after.png`; Calendário não cai mais na tela de bloqueio, mostra NW-2288-0 já no primeiro início, `cal1`).
  **Teclado (IME)**: 0,8 -> 7-8 fps (e928487, o gate dos uniforms agora deixa o backend submeter: a cerca da display
  queue do shell ficava presa 390 ms; `ime1` antes, `ime2` depois) e as teclas digitam (53447dc, toque vai ao shell com
  a região 2 ativa): "vita" digitado, Enter vai para a senha, o botão de esconder fecha (`ime_word2/sheet.png`). Ainda
  abaixo de 30 fps: o shell segura a cena ~33 ms (espera de vblank com a cena aberta) e a arbitragem segura o BeginScene
  do setup; toques de 8 quadros às vezes se perdem (`ime_word`: "ta" de "vita"); o círculo não fecha o teclado.
  **Navegador**: não é rede: ele espera o processo do motor web **NPXS10083 (SceWebKitProcess**, eboot em vs0:app, usa
  ScePsp2Compat/SceWebKit de vs0:data/external/webcore; NPXS10084 é o "mini") — `sceAppMgrGetStatusByName("NPXS10083")`
  falha e ninguém o inicia. O código que vigia e inicia está no eboot do Navegador perto de 0x810A8100-0x810A8950
  ("SceWebCoreProcessWatcher", referências a 0x810F5E8C); próximo passo: ver como ele pede o início (provavelmente pelo
  shell/SceShellSvc ou sceAppMgrLaunchAppByPath) e implementar; depois as 8 NIDs ScePsp2Compat que faltam.
  `sceGxmGetDisplayQueueThreadIdInternal` implementado (sem efeito visível). **near**: NID_EFC9BD25 devolve o ponteiro de
  contexto interno do liblocation real (global +0x4000) e NID_2311B24A um byte (0 até 5C7185D2); são internas entre
  liblocation e liblocation_internal/extension (LLE) e o stub já devolve 0: a tela preta tem outra causa (não
  investigada). Configurações > Sistema/Formatar/Economia: não rodado. A primeira descida da tela de bloqueio no boot às
  vezes falha (`br1`: ficou na tela de bloqueio o tempo todo; `br2` igual roteiro, passou).
  Rodada fix/system-apps-7 (06/11/2026, provas em `fw7_out/` da pasta de trabalho local): **Navegador** corrigido (f1d8488): ele
  mesmo inicia o motor web com `sceAppMgrLaunchAppByName2` quando `sceAppMgrGetStatusByName("NPXS10083")` falha com
  0x80802012 (o HLE devolvia 0x8080201C e ele desistia); mais NID_BEF71602 (funções exportadas por NID) e
  `sceKernelWaitEvent` em message pipes (o IPC entre os dois processos). Sem rede: "Cannot display the page
  (C2-14921-7)", OK, PS, continuar, fechar e reabrir funcionam (`br8/sheet.png`). As 8 NIDs de ScePsp2Compat eram
  só imports do eboot do NPXS10083, resolvidos quando ele carrega o módulo. **near** corrigido (064c1dc):
  liblocation_internal/extension só chamam funções internas do SceLibLocation, então o liblocation real (cliente
  IPC do shell) é carregado; mostra as páginas de introdução e "Sign up for PSN" (`near4/sheet.png`). Configurações >
  Sistema/Formatar/Economia de energia: rodado, OK (`st1/sheet.png`). Dica da tela inicial: não é bug, são dicas em
  sequência que avançam quando fechadas no X (o `press cross` dos roteiros não fecha). A descida da tela de bloqueio
  não falhou em ~12 boots desta rodada. **Teclado (IME): sem ganho, revertido**: medido no `GpuQueue` (por processo)
  com o teclado aberto: o shell abre ~53 cenas/s e cada uma fica aberta ~19 ms (espera o vblank com a cena aberta),
  98% do tempo; o setup espera 5 s a cada 5 s e faz ~13 cenas por quadro, cada uma esperando uma cena do shell (6-7
  fps). Tentado (1) revezamento (quem espera um BeginScene ganha a próxima cena): 6 fps, e numa execução o servidor
  IME do shell assertou (UNKNOWN_SEMA_ID); (2) dar os botões ao shell enquanto a região de toque 2 dele está ativa:
  o círculo continua sem fechar o teclado (o setup também lê os botões; o caminho do círculo não foi achado). O
  próximo passo continua sendo gravar as cenas de processos diferentes em render passes separados (suspender a cena
  aberta de um processo no backend quando outro submete), o que tira a arbitragem. Ainda aberto: barra de status do
  shell ausente sobre Mensagens/Amigos/Party/near (a faixa escura no topo; os atributos do param.sfo de Troféus, que
  tem a barra, e de Mensagens são iguais: não é isso). 07/11/2026 (fix/system-apps-10): hoje **nenhum** app do
  sistema mostra a barra, nem Troféus (`fw9_out/tro7/list.png`); só a tela inicial tem. A causa é comum
  e provavelmente a mesma do teclado antes do intercalamento: a barra é desenhada pelo shell por cima do app e o
  VitaSharp só apresenta o processo da frente (`Display.sceDisplaySetFrameBuf`) ou o quadro do cliente SceSharedFb.
  Próximo passo: ver se o shell desenha a barra num plano próprio enquanto um app está na frente (sceGxm do
  SceShellMain com o app aberto) e compor esse plano. Botão PS no PC e texto cortado no boot direto: não mexidos
  nesta rodada (o primeiro precisa da janela, o segundo não foi reproduzido na rodada 9).
  **Barra de status: resolvido** (07/11/2026, fix/system-apps-11, provas em `fw11_out/tro2/t2.png` e
  `apps1/sheet.png`): não era composição (o shell já desenha a barra dentro do quadro SceSharedFb que ele compõe). O
  shell lê a cada quadro, no SceAppMgrAppStatus do app da frente, +0x48/+0x49/+0x4A (visível, cor, transparência;
  shell 0x8127DF5E) e o HLE escrevia zero. Agora vêm do param.sfo (ATTRIBUTE 0x80 = barra, que todo app do sistema
  com tela tem e nenhum jogo tem; 0x100 = branca: Amigos, Email, Calendário; 0x20 = translúcida: Store e Navegador
  dentro do jogo) e do `sceAppMgrSetInfobarState`. Troféus e Mensagens com a barra preta, Amigos com a branca, a tela
  inicial igual. Texto cortado no boot direto: de novo não reproduzido (`run NPXS10015`: Configurações com o texto
  inteiro, `fw11_out/direct.png`); no `run NPXS10008` sem o shell, Troféus fica só no fundo depois de ~12 s (sem o
  servidor de troféus do shell, esperado).
  Confirmado pelo usuário (06/11/2026): os apps do sistema estão ok. Seguem abertos à parte: teclado na tela (fps),
  Câmera (salvar foto, gravar vídeo), app Troféus e o fundo do jogo na LiveArea (agente de firmware).
  Rodada fix/system-apps-8 (06/11/2026, provas em `fw8_out/` da pasta de trabalho local): **Teclado (IME) a 60 fps** (era 5-6):
  as cenas de processos diferentes não se esperam mais (60caec2, padrão em 6df044e; `VITASHARP_GPU_SCENE_INTERLEAVE=0`
  volta a arbitragem): o renderizador guarda a cena aberta de cada processo e a retoma (VulkanRenderer.SceneContexts.cs),
  o draw segurado pelo gate dos uniforms espera na fila do seu processo sem travar o worker, e os dumps de GPU gravam o
  processo de cada comando. Os dumps de GPU reproduzem idênticos à main (16 de 16 com quadros, de novo depois do merge
  com a main dce8f6b, `fw8_cmp/summary.txt`) e o fps dos jogos é o mesmo com e sem o intercalamento (`fw8_fps/summary.txt`:
  FIFA 13 60/60, Devious Dungeon 56-60/56-60, Uncharted 30/30 no gameplay); teclado, "vita" digitado
  (`ime_il6/typed.png`). Os botões vão ao shell enquanto o teclado está aberto (be22179): o círculo mostra o foco e
  confirma a predição, o d-pad e o X funcionam nas teclas (`ime_circ*`); **o círculo ainda não fecha o teclado** e um
  toque curto às vezes se perde ("via" em vez de "vita", 1 de 4 execuções): próximo passo, ver se o shell (1 núcleo)
  fica ocupado com a predição e perde o toque inteiro entre dois quadros dele. **Barra de status**: não investigada
  além do estático: Troféus importa só SceIpmi/SceAppMgr Safe Memory, os apps sem barra importam SceShellUtil
  BB54D049 (que é um relatório de erro, IPC 0xC000D, não a barra); num print antigo (`fw_out/x_trophies/main.png`)
  Troféus também está sem barra: conferir primeiro se alguma tela de app tem a barra hoje. **setup_no.nav**: é o
  roteiro, não o emulador: ele termina (`quit`) na nota dos pais sem tocar em OK, então `/CONFIG/SYSTEM/initialize`
  nunca é gravado e o próximo boot daquela pasta recomeça a configuração; com o OK a configuração termina e o shell vai
  à tela inicial (`setup1/sheet.png`, registro com initialize=1).
  Círculo no teclado (07/11/2026, fix/system-apps-9, só estático, não resolvido): o libpaf lê o botão de confirmar com
  sceRegMgrUtilityGetInt(0x229142 = /CONFIG/SYSTEM/button_assign) e guarda em settings[+0x7c] (1 = X confirma, o
  padrão do HLE), então o círculo chega ao ime_plugin como "voltar". O ime_plugin (vs0:vsh/shell/ime_plugin.suprx)
  traduz as teclas do paf numa tabela em 0x8100B8A0 (offset no ELF decriptado): a tecla 3 vira 0x2000 (o mesmo da
  tecla 0x14, provável "fechar") só se `paf Framework (F72FC59E)[+0x5c] == 0` ou `810030F4(obj[+0x9C]) != 0`; senão
  vira 4. O que se vê (o círculo só mostra o foco e confirma a predição) bate com o ramo 4. Próximo passo: rodar o
  roteiro do teclado (`fw9_ime.nav`, refeito do log de `fw8_out/ime_circ3`, numa pasta de dados nova como
  `fw9_ime_data`) lendo o Framework[+0x5c] e o retorno de 810030F4 no instante do círculo, e conferir num vídeo do
  Vita real se o círculo fecha mesmo com uma predição aberta ("vita" sublinhado).
  07/11/2026 (fix/system-apps-10, `fw10_out/ime2`, não resolvido): a pista acima estava errada. A função
  da "tabela de teclas" (ime_plugin 0x8100B836, base no shell 0x83210000) roda **uma vez por tecla do layout** quando
  o teclado é montado (r1 = definição da tecla, tipo 0xF na primeira), não a cada botão; com `--probe` nela e no ramo
  da tecla 3 nenhum aperto do círculo passou por ali. O caminho do botão círculo até o ime_plugin (evento de
  "voltar" do paf no processo do shell) segue por achar; próximo passo: `--trace-hle-threads=SceShellMain` no
  instante do `press circle` e procurar no ime_plugin quem trata o evento de cancelar do paf (o handler de
  `paf::ui::Event` da janela do teclado), e confirmar num vídeo do Vita real que o círculo fecha o teclado.
  07/11/2026 (fix/system-apps-11, `fw11_out/ime3`..`ime6`, não resolvido; a configuração inicial precisa
  de uma pasta de dados sem ur0, `fw11_reset_ime.ps1`): o que cada botão faz hoje no teclado aberto (só o shell lê os
  botões): círculo = primeiro mostra o foco, depois nada visível (com o foco no Enter, um círculo longo passou ao
  campo Senha, como o Enter); triângulo = confirma a predição; quadrado = apaga (backspace); Start = foco no Enter;
  Select = nada; o X digita. A tecla de fechar (chevron, canto inferior esquerdo) fecha. Os botões irem também para o
  app (o setup, por baixo) não muda nada (o paf do app ignora o círculo com o teclado aberto; experimento revertido).
  Nenhuma chamada HLE difere entre o círculo e o chevron (o pedido de fechar anda por memória/IPC do ime_plugin).
  Falta ainda a referência: num vídeo do Vita real, o que o círculo faz com o teclado aberto.
  **Círculo no teclado: resolvido** (08/11/2026, fix/system-apps-12, provas em `fw12_out/ime6/sheet.png`,
  `ime7/sheet.png`, `ime3/sheet.png`): o libpaf do shell já trocava círculo/X certo (tabela de botões em 0x81301094,
  círculo = tecla 0x20 "voltar" com /CONFIG/SYSTEM/button_assign = 1). Quem desfazia era o ime_plugin: ao abrir o
  teclado (0x81002B4C) ele lê `sceAppMgrAppParamGetInt(pid do app, 0x10)` (o botão de confirmar do processo) e, se for
  diferente do do libpaf, troca círculo e X no teclado; o HLE devolvia 0 (círculo) para todo parâmetro. Agora o 0x10
  devolve a configuração do sistema: círculo fecha o teclado mantendo o texto e, com o teclado fechado, volta no app;
  X digita a tecla com foco, quadrado apaga, triângulo confirma a predição. Com button_assign = 0 (modo japonês) o X
  fecha o teclado (`ime3`). No caminho: depois da barra de status (rodada 11) o shell deixa a região de toque 1 ativa
  sobre a barra, e qualquer região ativa dava ao shell o painel inteiro e os botões: a Galeria do Fotos (e qualquer app
  com a barra) não respondia a toque nem botão. Agora só os toques dentro de uma região ativa (x, y, largura, altura:
  barra 1920x1..64 no topo com attr 2, teclado (0, 594) 1920x494 com attr 4) vão ao shell, e os botões só com uma região do tamanho do
  teclado. O padrão do button_assign continua X (como o Vita3K); um Vita japonês/asiático de fábrica vem com círculo.
  Deste item seguem abertos o botão PS no PC (precisa da janela) e o texto cortado no boot direto (não reproduzido nas
  rodadas 9 e 11); nas execuções desta rodada (câmera, Fotos, configuração inicial, teclado) nenhuma função HLE
  faltou.
- [x] Câmera (modo câmera do Fotos, NPXS10004), relato do usuário 06/11/2026 (`captures/shell_20261106_020757_tudo` e
  `_020903_tudo`). **Foto (não resolvido, código não commitado)**: o libScePhotoExport do firmware é um cliente IPC do
  serviço SceAvMediaSrv, que é o app NPXS10036 (AvMediaService) iniciado pelo shell quando alguém pede a URI
  "avmedia_service:". Tentativa (patch `fw8_photo_wip.patch` na pasta de trabalho local, fora do git por não salvar a foto): sistema
  carrega o ScePhotoExport real; `sceAppMgrLaunchAppByUri/2` viram pedido ao shell (evento 6 com a URI, flags |
  0x100000, como o kernel 3.74) e o shell inicia o NPXS10036 com flags 0x600000 (servidor IPC no status +0x74; não abre
  um segundo); semáforo/event flag apagado com handles abertos por nome vira zumbi até o último close (o servidor
  assertava UNKNOWN_SEMA_ID); `sceKernelGetMsgPipeCreatorId` aceita o nome do pipe (libipmi) e devolve o pid. Com isso o
  cliente conecta, manda o pedido tipo 8 (método 0x41560004?) e recebe a resposta tipo 5 toda zerada, desconecta e tenta
  de novo; a câmera mostra "Could not save the image (C1-11213-8)" e chama sceErrorHistoryPostError (`cam8`, `cam9`
  com a correção do IO assíncrono abaixo: igual). Próximo passo: ver no NPXS10036 (eboot 91 KB) o despacho do servidor e
  por que responde zero (estado de inicialização? evento que falta?). As pastas padrão do Vita (ux0:picture, ux0:video;
  photo0 hoje é `<fs>/photo0`, não ux0:picture) ainda não foram tratadas. 06/11/2026 (fix/bugs-games-12): das duas
  correções de kernel do patch, o zumbi de semáforo/event flag já estava na main (e9e70ca) e o
  `sceKernelGetMsgPipeCreatorId` por nome entrou (validado no PS4 Link: SceShellSvc e SceDbUtilDcn resolvem o pid do
  shell, mais o `sceAppMgrIsPidShellAndCrashed` que o cliente passa a consultar); o resto do patch (ScePhotoExport real,
  LaunchAppByUri como pedido ao shell, flags de serviço) e o despacho do NPXS10036 não foram mexidos (orçamento).
  06/11/2026 (fix/bugs-games-13): o `sceAppMgrLaunchAppByUri/2` como pedido ao shell e as flags de serviço entraram (o
  "psnreg:" do PS4 Link abre o Sign Up; "avmedia_service:" inicia o NPXS10036 com 0x600000, `bugsC13_out/cam1`). Com o
  ScePhotoExport real (patch `bugsC13_photoexport_wip.patch`, fora do git) a câmera continua com "Could not
  save the image": o NPXS10036 carrega libpaf, sqlite, dbutil, dbrecovery_utility e meta_gen e fica no laço do
  SceAvMediaServiceMain sem nenhuma chamada HLE com erro; o despacho do servidor segue não investigado (orçamento). fix/bugs-games-14: não retomado (orçamento semanal no fim).
  Despacho do NPXS10036 (07/11/2026, fix/system-apps-9, só estático): o servidor libipmi é criado em eboot 0x81000604
  (SceIpmi 391B02AA/2B646CEA, objeto de despacho no próprio serviço) e **atendido no laço da thread principal**
  (0x81000BA4 chama o vtable[1] do servidor a cada volta; se ele devolver != 0 o serviço se encerra com
  sceAppMgrDestroyAppByAppId(-2); um contador de ócio em +0x1C fecha o serviço depois de 0x753 voltas sem trabalho).
  Métodos síncronos 0x41560000-13 em 0x8100A77E (atende 0, 3, 4, 5, 6, 9, A, D, E, 11, 12, 13), assíncronos em
  0x8100A92A (1, 7, B, F); o servidor manda aos clientes 0x41560002/8/C/10. O cliente libScePhotoExport usa
  0x41560000-6, F e 10. Próximo passo: com o patch `bugsC13_photoexport_wip.patch` (aplica limpo na main 176f5d6),
  ver qual método a câmera manda e se o laço principal chega a atendê-lo (a thread SceAvMediaServiceMain fica
  "Running" dentro do libpaf no dump de `fw8_out/cam9`: talvez nunca volte ao laço). Vídeo (NID_9A5E7B2F, vtable
  nula, sem encoder H.264): não mexido nesta rodada.
  **Vídeo**: o crash do emulador
  (`ArgumentException` no free do CodecEngine) saiu (269b672): exceções .NET comuns numa função HLE viram erro para o
  guest em vez de derrubar o processo; o CodecEngine devolve 0 quando não há espaço (a câmera testa 0) e recusa free
  repetido; o Videoenc aceita as assinaturas que a câmera usa e o encode diz "sem quadro". A gravação ainda derruba o
  app **no guest** (salto para 0 em ScePhotoCamJobQueue1, lr eboot+0xD6FDB: chamada por vtable nula, `vid3`), logo
  depois de SceCodecEngineUser NID_9A5E7B2F (não implementada); não há encoder H.264 no emulador, então mesmo sem o
  crash o MP4 sairia vazio (`photo0/temp/photocam/movie/*.mp4` com 0 bytes). Próximo passo: o que NID_9A5E7B2F devolve
  (provável objeto/ponteiro que vira a vtable) e decidir o encoder (FFmpeg com h264_mf/openh264).
  **Foto: resolvido** (07/11/2026, fix/system-apps-10, provas em `fw10_out/cam9`, `cam10`): eram duas
  falhas. (1) O libpaf grava a foto com IO assíncrono e lê o fd do open no `SceIoAsyncParam` (último argumento de
  todo `sceIoXxxAsync`, resultado de 64 bits no +0) depois do `sceKernelWaitEvent`; o HLE ignorava esse argumento, o
  paf escrevia no fd 0 e o JPEG falhava com 0x80AF5021 (a thread ScePafJobDefault da câmera, não as JobQueue). (2) O
  photo0: era `<fs>/photo0` e o NPXS10036 (outro processo, que tinha montado photo0 = ux0:picture) não achava a foto
  para registrar (0x80101A0C). Agora photo0/music0/video0 são ux0:picture/music/video em todo processo (as pastas
  antigas migram), e o sistema usa o libScePhotoExport do firmware (cliente IPC do NPXS10036). A câmera salva em
  `ux0:picture/CAMERA/<xx>/<data>.jpg`, o NPXS10036 registra no `ux0:mms/photo/AVContent.db`, a miniatura aparece na
  câmera e a foto em Fotos > Imagens da câmera e abre (`cam10/pics.png`, `pic1.png`; a webcam deste PC dá quadro
  preto, brilho médio 8/255, por isso a foto é preta).
  **Vídeo (não resolvido, 07/11/2026, fix/system-apps-10, `fw10_out/vid2`)**: a gravação não cai mais (o
  salto para a vtable nula sumiu junto com a correção do IO assíncrono) e as duas funções que faltavam do
  SceCodecEngineUser (9A5E7B2F ao começar, 9AD7E66F ao parar; sem argumentos, retorno ignorado, provável monitor de
  desempenho) devolvem 0. Fluxo medido: QueryMemSize/QueryEncoderMemSize (tipo 0x1002), bloco unmap de 6 MiB com 3
  alocações (0x40000, 0x200000 = provável buffer de stream, 0x40000), InitLibraryWithUnmapMem, CreateEncoder
  640x480, SetEncoderParameter, ~30 `sceAvcencEncode(ctrl, input, output)`/s na thread ScePhotoAvSrcVideoSrcMain,
  EncodeStop, DeleteEncoder. O libFflMp4 (LLE) grava o MP4 em `ux0:picture/CAMERA/<xx>/<data>.mp4` com o áudio AAC
  certo (93 pacotes em 2 s) e a trilha H.264 640x480 vazia; a câmera então diz "Não foi possível salvar o vídeo". O
  que falta: (1) um encoder H.264 (o mais simples honesto seria só macroblocos I_PCM em CAVLC, ~460 KB por quadro,
  ou intra 16x16 DC com resíduo CAVLC); (2) os layouts que a câmera usa e ninguém confirmou: a imagem de entrada do
  Encode (+0x? endereços Y/UV e pitch), a imagem de saída (+0 != 0 quando há quadro, número de NALs em +0x34), o
  `SceAvcencNalUnit` (+0 endereço, +4 tamanho, +8 tipo) e onde os bytes dos NALs moram (provavelmente o buffer de
  0x200000 do bloco unmap). Próximo passo: com `--probe` no retorno do Encode, ler a entrada (0x8414CEE4) e a saída
  (0x8414CEA0) e seguir no eboot da câmera (fw10_cam.elf, base +0x02F10000) o que ela faz com os NALs até o muxer.
  07/11/2026 (fix/system-apps-11, `fw11_out/vid2`..`vid5`): **o MP4 agora tem vídeo de verdade** (116
  quadros H.264 640x480 em 3,9 s, Constrained Baseline, AAC junto; `ffprobe` e um quadro decodificado, `vid2/frame60.png`).
  Layouts lidos no eboot da câmera (0x83FE8EE6) e confirmados com `--probe`, escritos no Videoenc.cs: entrada de 0x30
  bytes (+8 pitch, +0xC largura, +0x10 altura, +0x24 a imagem YUV420 planar da SceCamera, +0x2C flags), saída (+0 != 0
  com quadro, +4 número de infos, +8 lista de ponteiros para infos de 0x28 bytes: dois tempos em (segundos,
  numerador, denominador), buffer de stream +0x1C/+0x20 que a câmera dá (0x64000 bytes, no heap dela, não no bloco
  unmap), +0x24 número de NALs, +0x25 tipo 4 = IDR), `SceAvcencNalUnit` (+0 endereço depois do start code, +4
  tamanho, +8 nal_unit_type): a câmera troca cada start code de 4 bytes pelo tamanho big-endian (amostra MP4). O
  encoder é o do FFmpeg (libopenh264, BSD, já vem no FFmpeg LGPL do build; I_PCM não caberia no buffer de 400 KB);
  sem FFmpeg (Android) não sai quadro, como antes. **Ainda não resolvido**: a câmera continua com "Não foi possível
  salvar o vídeo (C2-10836-8)". Não é o arquivo: o NPXS10036 move o MP4 para `photo0:CAMERA/<xx>/`, carrega
  MsvPromoter + libFflMp4 + libvideoprofiler, lê o arquivo inteiro e o promote devolve 0x80103F2A (probe em eboot
  0x866B3886, r4), que o serviço traduz em 0x80101A0C (0x866B3940: 0x80103F20 -> A0B, 3F17 -> A04, 1AF1 -> A0E,
  1AF0 -> A0A, o resto -> A0C). O 0x80103F2A não aparece como constante no MsvPromoter, libvideoprofiler nem
  libFflMp4 (montado em tempo de execução); próximo passo: `--probe` nos retornos do libvideoprofiler (base do
  processo 0x881C0000) para achar qual verificação do MP4 falha (perfil/nível do SPS, fps 29,97 do muxer, duração
  vídeo 3,87 s x áudio 4,29 s, miniatura `.m4t` ausente).
  **Vídeo: resolvido** (08/11/2026, fix/system-apps-12, provas em `fw12_out/vid10/sheet.png` e
  `vid6/play1.png`): o 0x80103F2A sai do MsvPromoter (0x81003952: nenhum vídeo achado depois do perfil) porque o
  libvideoprofiler não achava a trilha de vídeo (0x80103C04, lista de trilhas vazia): o avcC do MP4 tinha SPS e PPS
  vazios. A câmera lê SPS/PPS do bloco de controle do encoder logo depois do `sceAvcencSetEncoderParameter` (+0x14/+0x18
  um buffer de 0x400 bytes dela; de volta +0x1C/+0x20 SPS, +0x28/+0x2C PPS) e monta o avcC com o cabeçalho fixo
  01 42 E0 1E. Agora o encoder FFmpeg abre com cabeçalho global e o extradata dá os dois; o tamanho da imagem vem do
  parâmetro (+0x18/+0x20; a consulta da criação dizia 640x640). Gravar 4 s: a câmera salva sem erro, a miniatura com
  o ícone de vídeo aparece, Fotos > Imagens da câmera mostra o vídeo "00:04", abre e toca até o fim (123-125 quadros
  decodificados, 0 perdidos); `SceMp4` NID C05DFF01 (fim da reprodução) devolve 0. A webcam deste PC não está ligada
  ao emulador (quadros cinza), então o conteúdo não dá para conferir aqui.
- [x] Troféus (NPXS10008): os troféus ganhos só aparecem para jogos já rodados **pelo sistema** (shell). Validado com
  uma cópia dos dados: Minecraft (NPWR06859, 5 troféus no JSON do HLE) aberto pelo shell, a ponte do servidor
  (TrophyServerBridge) instala o título e dá os 5 troféus, e Troféus mostra "Minecraft 7%, 5 bronzes" (`tro7/t2.png`).
  Jogos abertos direto pelo launcher nunca passam pelo servidor do shell, então o app mostra "There are no trophies"
  para eles (o trophy_local.db do usuário tem tbl_trophy_title vazio). Falta a migração dos títulos que só estão no
  JSON: instalar cada um no servidor sem abrir o jogo (a ponte precisa de um processo do título por causa do
  sceAppMgrTrophyMount(300, pid) que lê o TROPHY.TRP). A falha do relato (`[SceIpmi] INTERNAL ASSERTION FAILURE
  0x8002058e`, BKPT em SceTrophy+394CE: o método síncrono +0x10 do cliente IPMI devolveu 0x8002058E na segunda
  abertura do app, depois de near/Email/Calendário) não se repetiu em 9 aberturas (`tro2`-`tro5`: abrir/fechar 6 vezes,
  com o Email suspenso); a gravação do usuário começa depois do crash.
  Plano da migração (06/11/2026, fix/bugs-games-12, não implementado por orçamento): fazer a ponte rodar no processo
  do app Troféus (NPXS10008, cliente do mesmo servidor) quando ele abre: para cada `ux0:user/00/trophy/<NPCOMMID>.json`
  sem título no servidor, achar o app em `ux0:app/*/sce_sys/trophy/<NPCOMMID>`, fazer o
  `sceAppMgrTrophyMount(300, pid do Troféus)` apontar para ele enquanto a ponte cria o contexto (sem assinatura, como os
  jogos que passam NULL), instala e dá os troféus do JSON. A dúvida a testar primeiro: se o servidor aceita um contexto
  de outro título vindo de um programa do sistema (ele pode amarrar o contexto ao title id do pid). Validar com uma
  cópia do `ux0:user` e do `ur0` (como em `bugsC12_data`), nunca no %APPDATA%.
  Migração feita (06/11/2026, fix/bugs-games-13): quando um programa do sistema que não é o shell abre o pipe
  `SceNpTrophyServerMP` (o Troféus), a ponte roda no processo dele antes: para cada JSON sem
  `ur0:user/00/trophy/data/<NPCOMMID>/TRPTITLE.DAT`, com o app em `ux0:app` e com a assinatura guardada, o
  `sceAppMgrTrophyMount(300, pid)` aponta para o app, a ponte cria o contexto, instala, dá os troféus do JSON, apaga o
  contexto e no fim fecha a própria conexão (com ela aberta o Troféus mostrava "Could not restore the trophy
  information (NP-10219-3)": o servidor recusa a segunda conexão do mesmo processo). Resposta da dúvida: o servidor
  aceita o contexto de outro título vindo do Troféus, mas **confere a assinatura** (SceNpCommunicationSignature, 0xA0
  bytes, magic 0xB9DDE13B; sem ela o np_trophy dá 0x80551604, com zeros o servidor dá 0x8055160B). Ela não pode ser
  inventada: o HLE agora guarda a que o jogo passa (sceNpTrophyCreateContext ou sceNpInit) em
  `ux0:user/00/trophy/<NPCOMMID>.sig`, então os jogos jogados antes desta versão precisam abrir uma vez (pelo launcher
  serve) para aparecer. Validado numa cópia (`bugsC13_out/mc1` grava a assinatura do Minecraft rodando direto,
  `tro14/t2.png` Troféus com "Minecraft 7%, 0 0 0 5" e `tro15/sheet.png` lista, detalhes e reabertura sem reinstalar).
  Estranho: durante a migração o shell mostra o aviso "You have earned a trophy!" para cada troféu dado (5 no Minecraft).
  **Aviso na migração (aberto, fix/bugs-games-14)**: reproduzido numa cópia (`bugsC14_out/tro1/t1.png`,
  "Montando / Você conquistou um troféu!" ao abrir Troféus). O servidor (shell.self, SceNpTrophyServerJob) faz por
  desbloqueio, dentro da própria chamada síncrona da ponte: `sceAppMgrAppParamGetInt(pid, 1/5/0xD)` e
  `sceAppMgrGetStatusById(pid)` (lr 812CB04D, wrapper 812CB034, a3 = NPCOMMID). O callback de fim do UnlockTrophyJob
  (814287F4) chama 81229A58, que só segue se 812CB034 (o GetStatusById) der >= 0; tentativa: devolver "não achado" a
  essas consultas durante os desbloqueios da ponte (patch `bugsC14_trophy_silent_wip.patch`, fora do git):
  as 5 consultas falharam e o aviso apareceu igual (`tro4/t1.png`), então o aviso não sai desse caminho. Próximo passo:
  achar quem empurra o aviso (strings "JobPushNotification"/"notificationPush"/"sys-notify" do shell; talvez o cliente
  np_trophy do processo ou a UI do shell ao receber o evento do servidor) e ver se há um modo de "sync" sem aviso
  (o servidor tem DebugUnlockTrophyJob ao lado de UnlockTrophyJob).
  **Tentativa DebugUnlock (fix/bugs-games-16, não resolvida)**: o cliente do IPC 0x90036 é o export np_trophy
  `C1406504(handle, pedido, ctx, {tipo, id}, out)` (8100A258 -> 8100EB04 -> stub 8101010C; tipo 3 = troféu 0..127, 2 =
  grupo, 1 = todos). O servidor (812C74FC) busca o handle (tabela +0x14, falta = 0x80551608), o contexto (+0x64, falta =
  0x80551609) e o pedido (+0x84, falta = 0x8055169B): o "pedido" é o id que D70DAFA7 devolve (0x6000x); com ele a chamada
  passa das três buscas e falha com 0x80551609 em `812D7E20` (exige contexto[+0x20] == 1). Criar o contexto com
  options=1 não muda nada; falta achar quem põe contexto[+0x20] = 1 (talvez um contexto de depuração/sistema).
  Rodadas em `bugsC16_out/tro2..tro5` (log "silent unlock"). `SceNpManager::NID_A775A452` (pergunta do
  servidor antes de sincronizar com a rede) agora devolve 0 (sem sincronizar): `tro5/log.txt` sem funções faltando.
- [x] LiveArea dos jogos com o fundo padrão do sistema em vez do template do jogo
  (`captures/shell_20261106_021501_tudo`): ao abrir a LiveArea de um jogo o shell copia icon0.png e pic0.png do appmeta
  (am110:) para ur0:temp/appmeta; o pic0 (maior) passa pelo leitor assíncrono do libpaf, que espera cada sceIoReadAsync
  com sceKernelWaitEvent(opid, 1); isso devolvia UNKNOWN_EVENT_ID, a cópia falhava (0x80AF5001 no job da LiveArea,
  callback shell 0x811B6F18) e o shell usava a LiveArea padrão. O opid agora é um objeto de evento (9255af8): FIFA 13,
  Uncharted e Gravity Rush mostram fundo, portão e frames (`la6/la2.png`, `la_uc/la2.png`, `la_gr/la2.png`). O
  `#_livearea_path` "ur0:appmeta/<id>/livearea/contents" do app.db é o que o dbrecovery 3.74 grava, não é a causa.
  Confirmado pelo usuário (06/11/2026): as imagens da LiveArea aparecem e o app Troféus funciona (os troféus antigos
  de jogos não reabertos e os avisos da migração seguem no item de troféus antigos)
- [x] App de Câmera (NPXS10007) trava logo depois de abrir (30/10/2026, main 4dbe06c, log `20261030_204528_shell`):
  a câmera traseira abre (`Integrated Camera` 640x480 YUYV), o primeiro quadro é lido e a textura YUV420P3 aparece,
  e então o app para; não há mais leituras da câmera, a thread `ScePhotoCamMain` está Dormant, uma
  `SceCameraCoreCameraDeviceThread` espera em `condvar SceCameraCoreCameraDeviceLoopStatusCond` e a outra está Dormant.
  Funções que faltam no caminho: `ScePhotoExport::NID_16B1028E`, `SceLibLocation::NID_EFC9BD25` / `NID_2311B24A`,
  `sceAppMgrMmsMount`, `_vshAppMgrRegisterPath`, `sceKernelCheckModelCapability`, e um `IO: unmapped path ''` logo
  antes de criar as threads da câmera. (04/11/2026: não deu para reproduzir — a instalação atual do firmware 3.74
  em `%APPDATA%\VitaSharp\fs` não tem `vs0:app/NPXS10007`; precisa reinstalar/conferir o firmware e rodar com
  `--camera=<webcam>`)
  Superado (08/11/2026, fix/system-apps-12): o firmware 3.74 não tem NPXS10007; a câmera é o modo câmera do Fotos
  (NPXS10004), que abre a webcam, tira foto e grava vídeo, e os dois aparecem e abrem em Fotos (item da Câmera acima).

## Código

- [ ] Refatoração, o que ficou para depois (11/11/2026, decisão do usuário: Hle, Desktop e Android outro dia). Em
  branches locais, não juntadas: estilo do Hle (`refactor/style-hle-3`, pronto, falta a suíte); nomes do Desktop
  (`refactor/naming-7`, WIP: falta build, format-check e IL incluindo `-p:Lockstep=true`); nomes do Android
  (`refactor/naming-9`, WIP: faltam os builds Release e o IL do VitaSharp.Android.dll); passada de nomes curtos nos
  testes (`refactor/naming-4` e `refactor/naming-8`, WIP); `format-check` cobrindo os projetos fora da solução
  (`refactor/format-extra`, WIP: o build/IconGen precisa das chaves antes). Depois: os nomes do Hle, os métodos de
  save state do Hle (com o hash do esquema independente da formatação) e religar o job "Code style" do CI. Cada
  entrega de nomes confere também o build da versão de desenvolvedor (`-p:VitaSharpDeveloper=true`). Um script que
  recalcula as linhas de par dos READMEs depois de cada merge (quebraram três vezes em 11/11).
  Braces: em 11/11 ainda havia corpos de if/for/foreach sem chaves onde a format-check não chega: ~375 no
  `src/VitaSharp.Android` (fora do .sln), 30 no `src/VitaSharp.Desktop/Lockstep.cs` (só com `-p:Lockstep=true`), 2
  no `build/IconGen`, além dos ~311 do Hle. Os WIPs do Android, do Desktop e da format-check corrigem; só considerar
  feito com uma busca no repositório inteiro que dê zero fora do Hle, e com a format-check cobrindo esses projetos.
- [ ] CI do Windows e da cobertura (pausado em 11/11/2026): `fix/ci-green-2` (SwiftShader registrado para o carregador
  como administrador, `--gpu-dump-at` armado antes do jogo, limite de 3 apresentações pendentes). Antes de juntar:
  medição A/B do F1 2011 e do Uncharted com a máquina quieta e conferência de travadas nos quadros gravados.
- [ ] **PRIORIDADE (11/11/2026)** Builds do GitHub Actions quebrando na main (relato do usuário, 11/11/2026, página de
  commits da main). Ver em cada workflow (ci.yml: Windows/Ubuntu/macOS, publish, "Coverage gate" novo de 11/11;
  release.yml) qual job falha desde quando e por quê (suspeitos do dia: o job de cobertura e o build-coverage-gate.txt,
  testes que dependem de rede ou de GPU de software no runner, o par .ps1/.sh, o build do Desktop no macOS/Linux com o
  teste que referencia o Desktop, NativeAOT), corrigir a causa (não desligar job nem pular teste) e deixar todos os
  commits verdes; a checagem local do lead passa a rodar também o que o CI roda que ela não roda.
  Causas (11/11/2026, R1, logs do `gh run view --log-failed`; vermelho desde 049c8a1): (1) macOS "Build and test": os
  3 testes de decodificação do `VideodecHleTests` sem FFmpeg no runner (o macOS usa o FFmpeg do sistema) -> pulados
  sem FFmpeg (`[FFmpegFact]`, como os outros testes de FFmpeg); e desde a268652 o
  `LauncherHeadlessTests.EveryPageRenders` travava 20 min na página Configurações: a enumeração AVFoundation do
  FlashCap pede acesso à câmera e espera a resposta sem prazo (o mesmo travaria o launcher de um usuário de macOS) ->
  `FlashCapCamera.ListDevices` espera no máximo 3 s (a enumeração segue em segundo plano; a chamada seguinte pega a
  lista). (2) "Coverage gate": todos os testes passam; os mínimos vieram de uma medida local com firmware instalado
  (os testes que precisam dele são pulados no runner): CI mede Overall 64,4% e Core 70,0% contra mínimos 65,3/71,7.
  Rebase dos mínimos pela medida do CI aprovado pelo usuário; aplicado pelo lead (o classificador de permissões do
  agente bloqueia baixar mínimos do gate). Achado à parte: no Linux **com** lavapipe (WSL Debian),
  `RendererTextureTests.EveryTextureFormatAndLayoutSamplesAndTheDumpReplaysTheSame` derruba o testhost com SIGSEGV
  (exit 139) logo depois de "Readback buffers" / o primeiro present; o runner ubuntu não tem lavapipe e pula. A
  investigar (driver lavapipe ou leitura de volta do renderizador).
- [ ] **PRIORIDADE (11/11/2026, dia de refatoração)** O emulador é genérico (regra do usuário: nada específico a
  não ser a camada Vulkan; ver `docs/agents/regras.md`). (1) Auditoria arquivo por arquivo de src/ procurando qualquer
  comportamento por jogo, app, homebrew ou módulo de terceiro (comparação de ID/nome, listas fixas, caminhos fixos,
  HLE de módulo que não é do firmware) e lista com o arquivo:linha de cada um. (2) Trocar cada um pelo caminho
  genérico, já encontrados: `Hle/Modules/Taihen.cs` e `Hle/Modules/Kubridge.cs` (módulos de kernel de homebrew em
  HLE -> rodar o `.skprx` original no ambiente de kernel genérico; enquanto ele não existe, deixar registrado como
  dívida e não acrescentar nada específico), `Hle/LleLibraries.cs` (lista fixa e caminhos de homebrew -> carregar
  qualquer módulo importado pelos caminhos do carregador do Vita), `Shell/ShellAssets.cs` `HomeSystemApps` (lista de
  NPXS -> app.db do firmware), "é app do sistema" pelo prefixo `NPXS` em `Capture/CaptureContext.cs` e
  `Desktop/Runner.cs` (-> categoria do param.sfo / origem vs0:), região pela letra do ID em
  `Desktop/Launcher/GameLibrary.cs` (-> param.sfo). (3) Teste que falha se aparecer comparação com ID de título
  literal no código (exceto testes e comentários). Validar: suíte completa, replay-gpu --compare idêntico, shell + apps
  do sistema e os jogos da varredura sem regressão.
  **Auditoria (11/11/2026, R1, refactor/generic-1)**, todo o `src/` (busca por IDs de título, prefixos, nomes de
  módulo/plugin/homebrew, caminhos fixos, endereços de jogo). Trocado pelo caminho genérico:
  - `Capture/CaptureContext.cs:30,79` (prefixo `NPXS` = "app do sistema") e `Desktop/Runner.cs:446` (sem tela de
    início para `NPXS`) -> `Emulator.IsSystemProgram` (o programa roda de `vs0:`).
  - `Desktop/Launcher/GameLibrary.cs:25` (região pela 4ª letra do ID `PCSx`) -> letra de região do `CONTENT_ID` do
    `param.sfo` (U/E/J/H-K; sem content ID = homebrew).
  - `Shell/ShellAssets.cs:175` (`HomeSystemApps`, lista fixa de 4 NPXS) -> as bolhas do banco do próprio shell
    (`ur0:shell/db/app.db`, `tbl_appinfo_icon` por página/posição) que são pastas de `vs0:app`; sem o banco (shell
    nunca rodou), todo `vs0:app` com ícone.
  - `Hle/Modules/VshBridge.Homebrew.cs:27` (`name == "kubridge"`) -> `HleModule.KernelModuleName` (o nome fica só em
    `Kubridge.cs`).
  - `Hle/LleLibraries.cs` (lista fixa que mudava o carregador: `FromFirmware` para o SceLsdb, e
    `ur0:data/libshacccg.suprx`) -> o carregador decide por `HleModule.FirmwareForEveryImporter` (declarado pelo
    próprio módulo HLE, hoje o `Lsdb`); a lista ficou só como tabela do relatório de cobertura (SceSmart/SceFace do
    `app0:sce_module` do jogo, SceLsdb do firmware) e sem o caminho de homebrew.
  - `Hle/Modules/Piglet.Shaders.cs:23` (`ur0:data/libshacccg.suprx`, caminho do vitaGL) -> o compilador que o
    programa já carregou, senão `app0:sce_module` e `vs0:sys/external` (regras do carregador).
  - `Hle/Modules/SystemMisc.cs:262` (aviso só para `libshacccg.suprx`/vitaGL) -> aviso genérico para qualquer módulo
    fora de `vs0:`/`app0:` que falta.
  - `Loader/TitlePatches.cs` (tabela `Builtin` de patches por título, vazia) -> removida; ficam só os patches do
    usuário (`<data>/patches/<TITLEID>.txt`).
  - Teste `GenericCodeTests`: falha com qualquer literal de ID de título ou prefixo (`NPXS`, `PCSx`, `NPxx`,
    `XXXX00000`) em `src/` fora de comentários.
  Em troca (pedido do usuário, 11/11/2026: tudo trocado pelo caminho genérico, nada fica como dívida):
  - feito: `Emulator.LoadPlugins` (era `LoadTaiPlugins`) é o carregador genérico da configuração de plugins
    (`ux0:tai/config.txt`, senão `ur0:tai/config.txt`): módulos de kernel (`*KERNEL`, `.skprx`) e de usuário (`*ALL`,
    `*TITLEID`) pelo mesmo caminho do carregador, os de kernel primeiro; como o gerenciador de módulos do firmware, um
    módulo que importa uma biblioteca que ninguém fornece (nenhum módulo carregado e nenhuma HLE) não é iniciado, e o
    log diz quais (`PluginLoaderTests`, com os módulos reais quando instalados).
  - feito: `Hle/LleLibraries.cs` sem tabela fixa: conta como LLE o que um módulo instalado realmente exporta, achado
    varrendo os `app0:sce_module/*.suprx` dos jogos instalados e os módulos do firmware que o carregador dá a todo
    importador (`HleModule.FirmwareForEveryImporter`); a tabela do `docs/hle/progress.md` sai do TSV medido.
  - feito (11/11/2026, kern1, feat/kernel-env): ambiente de kernel genérico para os `.skprx` reais (carregados e
    iniciados com o código original; ver `docs/notes/plugins.md`). Importações lidas das tabelas dos módulos: taihen.skprx 60,
    kubridge.skprx 33, fd_fix.skprx 2. (M1) o carregador acima. (M2) HLE das exportações de kernel do firmware que eles
    importam, com a semântica e os códigos de erro do firmware (tabela de erros do SDK; SceSysmem 3.74 decompilado só
    para estudo): `SysmemKernel` (memblocks de kernel e de usuário com vbase/alinhamento/pid, heaps, classes e objetos
    GUID/PUID, cópias usuário/kernel com a checagem de permissão, strncpy, ForPid, cópia para código de usuário, caches,
    interrupções e spinlocks, sysroot, kernel panic), `ProcessmgrKernel` (armazenamento local por processo, heap em
    outro processo, handlers de eventos de processo, cadeia de handlers de exceção, threads de kernel, RunWithStack,
    ACMgr, clock), `ModulemgrKernel` (load/start/stop/unload de kernel e ForPid, info/lista/bloco de controle dos
    módulos) e os apelidos de kernel das funções de usuário (mutex, threads, Sysclib, Io, Ctrl); um teste por exportação
    (`KernelExportsTests`, `KernelModulemgrTests`). Região de kernel: os módulos de kernel vão para 0x40000000-0x50000000
    (mapeada em todo espaço de endereço), donos do processo do kernel (0x10005); threads de kernel no escalonador comum
    (`GuestThread.KernelMode`: o que alocam é do kernel, `ksceKernelGetProcessId` devolve o kernel). Interrupções:
    só estado (não há controlador de interrupções); exceções: os handlers ficam registrados, nenhuma é entregue (a falha
    do guest é a queda do emulador). (M3) o linker separa kernel e usuário: um programa de usuário só se liga às
    bibliotecas exportadas como syscall (atributo 0x4000) de um módulo de kernel e um módulo de kernel só a exportações de
    kernel (`ExportScope`, `KernelModuleLoaderTests` com módulos sintéticos); as consultas de módulo do usuário não veem
    os módulos de kernel. (M4) código que se modifica: as manutenções de cache de instrução do ARMv7 que o código do
    guest executa (`MCR p15 c7`: ICIMVAU derruba a linha de 32 bytes, ICIALLU/ICIALLUIS tudo) invalidam os blocos
    traduzidos no interpretador e no backend nativo (este chama o interpretador para essas instruções), como os
    recompiladores de referência (Dynarmic, Ryujinx) fazem; as escritas sozinhas não derrubam código (no hardware o lado
    de instrução só vê o código novo depois da manutenção), e os caminhos de HLE que escrevem código já invalidam
    (`ksceKernelCpuIcache*`, `sceKernelSyncVMDomain`, cópia para código de usuário, carga de módulo). Teste: `SelfModifyingCodeTests` (patch sintético + execução, interpretador e nativo). Detecção de escrita em
    página de código (proteção de página, estilo Dolphin) não feita: custo em todo store e nenhum caso conhecido que
    precise; plano se aparecer um: marcar as páginas com código traduzido e usar o byte de página suja que todo store já
    escreve (`DirtyPages`) para derrubar os blocos dessas páginas na primeira escrita. (M5) o taiHEN de verdade NÃO roda: o `module_start` dele (fonte aberta, `hen.c`
    `hen_add_patches`) só dá certo depois de 11 hooks nos módulos do firmware, vários deles contorno de DRM/assinatura
    (SceNpDrm rif_check, SceSblAIMgr package_check, SceSblAuthMgr parse/decrypt), que não fazemos funcionar nem fingimos
    (decisão do lead). **Decisão final do usuário (11/11/2026): emulador totalmente genérico, nenhuma HLE de módulo de
    terceiro.** `Hle/Modules/Kubridge.cs` e `Hle/Modules/Taihen.cs` (+ `Taihen.Snapshot.cs`) saíram; o `Security`
    (SceSblDmac5Mgr/RNG, do firmware) que morava no `Taihen.cs` foi para `Security.cs`. O taihen.skprx real continua
    reconhecido e carregado pelo caminho genérico (config.txt, região de kernel, importações ligadas às exportações de
    kernel do firmware, o `module_start` dele roda) e termina com o próprio START_FAILED, logado como
    `module_start of taihen returned 0x2, unloaded` (`PluginLoaderTests.RealTaihenRunsItsModuleStartAndFailsOnItsOwn`,
    pulado sem o arquivo). Consequência aceita: tudo o que precisa do taiHEN não roda, como num Vita sem ele: kubridge e
    fd_fix ficam em `not started: no module provides taihenForKernel`, e com isso **o Dead Space (kubridge) e o Sonic
    Mania (API de usuário do taiHEN) não rodam mais** (a lista de compatibilidade marca o Sonic Mania como "não roda").
    Validado antes da remoção do taiHEN em HLE: o kubridge real sobre o ambiente de kernel levava o Dead Space ao
    gameplay (`<scratchpad>/kern1/data/tests/deadspace_boot/sheet.png`), o que mostra que o ambiente de kernel serve o
    plugin; volta a rodar se um dia o taiHEN puder iniciar sem os hooks de DRM.
    Falta (próximos passos, plano):
    - kernel único do sistema: hoje os módulos de kernel vivem no emulador do processo para o qual foram carregados e saem
      com ele (sem eventos de processo entregues); o certo é um processo de kernel do `VitaSystem` que carrega o
      `*KERNEL` uma vez no boot e manda create/exit/kill aos handlers de `ksceKernelRegisterProcEventHandler`.
    - **[ ] aberto (exportações de kernel genéricas):** quando o kubridge.skprx real roda (hoje não roda: precisa do
      taiHEN), ele atende `kuKernelMemProtect`/`kuKernelMemReserve`/`kuKernelMemCommit`/`kuKernelMemDecommit` pulando para
      ponteiros internos do SceSysmem 3.60/3.65 que ele acha por deslocamento (`module_get_offset` no SceSysmem) e que não
      existem no VitaSharp: um homebrew que use essas funções para de rodar (o Dead Space não usa; o HLE antigo do
      kubridge as atendia).
      Caminho genérico a estudar: o SceSysmem do firmware em LLE ou um "SceSysmem" com as funções internas que o kubridge
      chama (AddressSpaceFindMemBlockCBByAddr, MapVirPageCore...) servidas por HLE nos deslocamentos do firmware
      instalado; validar com um homebrew que chame kuKernelMemProtect.
    - entrega de exceções aos handlers de kernel (data/prefetch abort, instrução indefinida) a partir da CPU emulada.
    - heaps de kernel: cada alocação ocupa páginas inteiras (correto, mas gasta memória com muitos objetos pequenos).
  Listas de nomes de módulos **do firmware** (conhecimento do firmware, não de um programa; ficam):
  `Emulator.cs:538,637` (preloads do processo pelos bits do process param), `Emulator.cs:668` `OnRequestModules`
  (libpaf só sob pedido), `Emulator.cs:675` `FirmwareServiceClients` (ScePhotoExport é cliente IPC do serviço),
  `Hle/Modules/SystemMisc.cs:66-100` (IDs de sysmodule -> módulos), `:141` `HleImplemented` e `:148` `HleFallback`
  (política HLE x LLE por módulo do firmware), `Hle/Modules/Rtc.cs:44` (threads `SceNpTrophyServer*` do shell levam a
  data do troféu da ponte do servidor de troféus). A camada Vulkan (`GpuQuirks`) é a exceção da regra.
- [x] Projeto de benchmark à parte (pedido do usuário, 11/11/2026; perf/desktop-22): `tests/VitaSharp.Benchmarks`
  com BenchmarkDotNet 0.15.2 (`MemoryDiagnoser` + `DisassemblyDiagnoser` de dois níveis, uma tabela Markdown juntada),
  fora da solução, da suíte e do CI. Benchmarks: `GpuWorkerBenchmarks` (Execute do renderizador sobre um dump inteiro,
  sem janela, na GPU do PC, caches quentes; `-Variants 'K=V|K2=V2'` mede chaves A/B no mesmo build),
  `ShaderTranslationBenchmarks` (USSE -> GLSL de todos os programas do dump), `UniformArenaBenchmarks`,
  `CpuTranslatorBenchmarks` (decodificação e compilação IL e x64 de um bloco; `-Corpus` = corpus do
  `VITASHARP_NATIVE_CORPUS`, para o agente do IL), `CpuDispatchBenchmarks` (CpuCore.Run em interp/il/native),
  `HleSyncBenchmarks` (lwmutex, mutex, semáforo sem disputa), `Atrac9Benchmarks` (superframe; `-At9` ou superframes
  gerados com cabeçalhos válidos), `TextureBenchmarks`. `build/bench.ps1`/`.sh`: filtro, resultado em
  `<out>/<nome>.md` com o commit, `-Compare` imprime as razões contra um resultado anterior (tools/dev/README.md).
  O projeto gerado compila com `-m:2` sem servidor de compilação, o plano de energia do Windows não é trocado e os
  processos rodam abaixo do normal (o BenchmarkDotNet os põe em High). Primeira rodada (PC, `--job short`, máquina
  dividida): lwmutex lock+unlock 102 ns, mutex 113 ns, semáforo 143 ns; aluguel+slot+liberação de uniforms 57/279/565 ns
  (256 B/4 KB/16 KB); decodificar um bloco ARM 0,18 us, compilar em IL 2,9 ms, em x64 13 us; laço de 4.096 voltas com
  chamada: interp 229 us, IL 134 us, nativo 30 us; USSE do kz_game inteiro 37 ms (83 MB alocados); ATRAC9 39 us por
  superframe (21,3 ms de áudio); PVRTC 4 bpp 256x256 4,7 ms e 1024x1024 83 ms (~60-80 ns/texel); replay do kz_game
  (7 quadros) 0,6-0,8 s com 137 MB alocados por replay (a maior parte é o leitor do dump, que copia cada registro).
- [x] Scripts `.sh` no Google Shell Style Guide (pedido do usuário, 10/11/2026):
  https://google.github.io/styleguide/shellguide.html, junto com a regra de que todo `.sh` roda em Linux e macOS
  (bash 3.2, sem ferramentas só do GNU sem alternativa; ver `docs/agents/regras.md`). Passos: (1) adequar os 17
  scripts de `build/`: `#!/usr/bin/env bash` + `set -euo pipefail` onde couber, cabeçalho com o que o script faz,
  funções com comentário e `local`, `main "$@"` no fim dos scripts com funções, variáveis entre aspas e com `${}`,
  `[[ ]]` e `$(...)`, nomes em minúsculas_com_sublinhado, constantes em MAIÚSCULAS com `readonly`, erros no stderr,
  indentação de 2 espaços e linhas até 80 colunas (o guia pede; conferir com o par `.ps1`); (2) ShellCheck sem avisos
  em todos (`shellcheck build/*.sh`); (3) no CI: um passo de ShellCheck e a execução rápida dos scripts (`--help` ou
  modo de teste) também no `macos-latest`, para pegar o que só funciona no Linux. O par `.ps1` continua fazendo a mesma
  coisa (check-script-parity).
  Feito (11/11/2026, refactor/style-2): os 19 `.sh` de `build/` no guia (cabeçalho, funções comentadas com `local`,
  `main "$@"`, `${}`/`[[ ]]`, constantes `readonly`, erros no stderr, `--help` em todos), `shellcheck --severity=style`
  sem avisos; CI: jobs "ShellCheck" e "Scripts on macOS" (`/bin/bash` 3.2 roda `build/check-scripts-help.sh`: todo
  `.sh` faz parse e responde `--help`; o par `.ps1` só faz parse dos `.ps1`). Corrigido no caminho: a linha de erro do
  `android-perf-run.sh` tinha tab/quebra literais e o cabeçalho e a linha normal do results.tsv tinham uma coluna a
  menos que os dados (app_fps). Falta: linhas de 120 colunas como o resto do repositório (o guia pede 80; decidir com
  o usuário). O awk do `android-perf-run.sh` (era do GNU: `asort`, `match` com vetor) passou a POSIX (`match` com
  RSTART/RLENGTH, `split`, `sort -n`), mesma saída em dados de teste e aceito pelo `gawk --posix`.
- [ ] Cobertura de código mínima de 70% em tudo (pedido do usuário, 10/11/2026). Base de hoje (`build/coverage.ps1` /
  `.sh`, coverlet + ReportGenerator, `Category!=Slow`): geral 59,4% linhas / 42,5% ramos; VitaSharp.Core 65,8%;
  Mobile.Core 85,4%; Renderer.Vulkan 1,3%. Meta: cada projeto e cada namespace com pelo menos 70% de linhas, e manter
  assim. Passos: (1) testes onde falta mais, começando pelo que tem bug ativo: Hle.Modules.ThreadMgr (9,2%), Gxm (14,9%),
  Ngs (14,2%), Videodec (8,8%), SystemGesture (5,1%), AppMgrShell (3,8%), AvPlayer (0,5%), Paf, NpCommerce2,
  UsseTranslator/UsseConstants, Core.Emulator, Capture.SessionCapture; (2) renderizador: testes que rodam com um
  dispositivo Vulkan de software (lavapipe/SwiftShader) e o replay dos dumps da GPU (`replay-gpu --compare`) contando na
  cobertura; (3) Desktop e Android: tirar a lógica da UI para classes testáveis e incluir os projetos na medição;
  (4) portão no CI: o build falha se a cobertura de algum projeto cair abaixo de 70% (ou abaixo da última medida,
  enquanto não chega lá). Os testes têm que checar comportamento do Vita real (firmware, documentação), não só passar
  pelas linhas.
- [ ] Core libretro do VitaSharp (pedido do usuário, 08/11/2026; fazer depois do Android em velocidade nativa, porque
  depende do mesmo trabalho de runtime): projeto novo `VitaSharp.Libretro` que expõe a API C do libretro (`retro_init`,
  `retro_load_game`, `retro_run`, `retro_serialize`...) via NativeAOT (`[UnmanagedCallersOnly]`, DLL no Windows e `.so`
  no Linux/Android). Vídeo pela interface Vulkan de hardware do libretro (RETRO_HW_CONTEXT_VULKAN: o renderizador
  desenha nas imagens que o frontend dá), entrada pelo RetroPad + ponteiro (toque), áudio pelo `audio_sample_batch`,
  estados salvos do VitaSharp no `retro_serialize` (rewind), firmware na pasta `system` do frontend. Sincronizar o
  emulador multi-thread com o `retro_run` por quadro sem perder desempenho. Validar no RetroArch (Windows, Linux e
  Android)
- [x] Sugestão de layout novo para o launcher do desktop (pedido do usuário, 07/11/2026), como foi feito para o app
  Android: proposta de um visual moderno e próprio do VitaSharp (o usuário citou o Ryujinx só como exemplo de visual
  moderno, não como modelo a copiar): biblioteca de jogos com capas grandes, busca e filtros, página do jogo com
  LiveArea/capturas/estados salvos/troféus/ajustes por jogo, configurações organizadas por seção, tema claro e escuro e
  as mesmas cores, fontes e ícones do app Android. Primeiro mostrar a proposta ao usuário (prints ou protótipo) e só
  implementar depois da aprovação. Proposta aprovada e implementada (07/11/2026, etapa 2, `docs/notes/desktop-launcher-design.md`):
  barra lateral com o Sistema do Vita em um clique (Ctrl+H), biblioteca em capas/lista/tela inicial, página do jogo,
  troféus, capturas, fila de instalação, configurações por seção, tema claro/escuro, tempo jogado. Validado sem janela
  (`ui-render`: todas as páginas com dados reais; Killzone e o Sistema do Vita iniciados pelo launcher com `--headless`,
  saída 0 e tempo jogado gravado); falta o usuário usar com janela. A atualização automática só tem os ajustes.
  Confirmado pelo usuário (08/11/2026)

- [x] Textos da interface em arquivos de idioma, inglês e português (pedido do usuário, 06/11/2026): hoje os textos do
  launcher, do app Android, dos avisos na janela/barra de título, do menu de pausa e das mensagens do emulador estão
  espalhados no código e misturados entre inglês e português. Juntar tudo em arquivos de recurso por idioma (ex.: `.resx`
  ou JSON com chaves, um para `en` e um para `pt-BR`, compartilhados pelo desktop e pelo Android via Core), escolher o
  idioma pelo sistema (com opção nas configurações) e cair para inglês quando faltar uma chave; um teste que falha se um
  texto de interface ficar fora dos arquivos ou se faltar uma chave em algum idioma. Também resolve a regra 16 do guia
  Ryubing (sem caracteres não ASCII no código). Os logs continuam em inglês
  Feito no desktop (08/11/2026, feat/product-1): `src/VitaSharp.Core/Localization/Texts.resx` (inglês, também o padrão
  quando falta uma chave) e `Texts.pt-BR.resx`, lidos por `Loc` (Core, para o desktop e o Android). O idioma segue o
  sistema, com Configurações > Geral > Aparência > Idioma (Sistema / English / Português); o launcher passa a escolha
  para o jogo (`VITASHARP_LANGUAGE`). Migrados: todas as páginas do launcher (`Strings.cs` lê a chave do próprio nome),
  controles, firmware, importação de troféus, tela inicial recriada, avisos do jogo (barra de título, troféu, teclado
  na tela, pausa, estados salvos, resolução/taxa por tecla, Gravar tudo, dump da GPU, diagnóstico) e as mensagens do
  Core que aparecem para o usuário (estado incompatível, firmware, modo do mouse). `LocalizationTests`: mesmas chaves
  e marcadores nos dois idiomas, toda chave usada no código existe, e os .cs do desktop são só ASCII (regra 16 do
  Ryubing: um texto em português no código falha o teste). 550 textos. Validado com `ui-render --lang=en|pt-BR`.
  Falta: o app Android (`VitaSharp.Android`, páginas e menu de pausa) e o `VitaSharp.Mobile.Core` ainda têm os
  textos em português no código; migrar para as mesmas chaves e estender o teste de ASCII a eles
  Android feito (08/11/2026, feat/product-2): as páginas (biblioteca, jogo, controles, desempenho, ajustes, firmware,
  PSN, GPU, troféus, desenvolvedor), o menu de pausa, os avisos do jogo e os textos do `VitaSharp.Mobile.Core` (rótulos
  das escolhas, driver/peculiaridades da GPU, troféus, datas, firmware) passaram para chaves `M*` no Texts.resx /
  Texts.pt-BR.resx (~330 textos, escritos em inglês e português). O APK compila (Debug); não rodou no celular. Falta:
  estender o teste de ASCII ao Android e ao Mobile.Core (os símbolos ✕ ○ › ▶ e os comentários em português ainda
  estão no código) e uma escolha de idioma no app (hoje segue o idioma do aparelho)
  Feito (08/11/2026, feat/product-3): `LocalizationTests` confere também `VitaSharp.Android` e `VitaSharp.Mobile.Core`
  (só ASCII: símbolos como \u, textos das telas de desenvolvedor no .resx, comentários em ASCII); Ajustes > Aparência >
  Idioma no app (Sistema / English / Português, `config/app.json` `Language`, aplicado nos dois processos ao iniciar;
  trocar oferece reiniciar o app). O APK compila (Debug); não rodou no celular
  Confirmado pelo usuário (08/11/2026)
- [x] README e documentação externa em dois idiomas (pedido do usuário, 07/11/2026): inglês como padrão
  (`README.md`, `docs/compatibility.md` e o que for para quem usa o emulador: instalação, firmware, controles,
  releases) e uma cópia em português do Brasil ao lado (`README.pt-BR.md`, `docs/compatibility.pt-BR.md`...), com um
  link "English | Português" no topo de cada um. Só o que é externo: coisas internas (este roadmap, `docs/agents/`,
  `docs/notes/`, `docs/hle/`) continuam num idioma só, como estão. Manter os dois em dia a cada mudança (um teste que
  avisa quando um arquivo externo muda sem o par)
  Feito (08/11/2026, feat/product-1): `README.md` em inglês e `README.pt-BR.md`, `docs/compatibility.md` e
  `docs/compatibility.pt-BR.md`, com "English | Português" no topo (atualizados para o launcher novo: Instalar pkg /
  vpk, firmware na página Sistema do Vita, idioma, atualização automática). `ExternalDocPairsTests`: os externos
  exigidos e todo `*.pt-BR.md` têm o par e o link, e a primeira linha de cada um (`<!-- pair: <outro> sha256=... -->`)
  guarda o hash do outro idioma: mudar um sem o outro falha o teste, que mostra as duas linhas novas. O
  `docs/android/README.md` (compilar e rodar o app) e o cabeçalho das notas de release ficam só em inglês por enquanto
- [x] Release automática a cada build: hoje o `release.yml` só publica com tag `v*` ou pelo botão da aba Actions.
  Fazer o CI, a cada push na `main` com testes e pacotes ok, publicar sozinho uma release (prerelease) na aba
  "Releases" do GitHub com os pacotes Native AOT (win-x64, linux-x64, osx-arm64; depois o APK android-arm64), o
  `SHA256SUMS` e a trava de firmware, nomeada pela hash do commit (ex.: tag `build-<hash curta>`, título "VitaSharp
  <versão>+<hash>", corpo com a lista de commits desde a anterior). Manter só as últimas N releases automáticas (ou
  uma release "nightly" sempre substituída) para não lotar a aba; as releases `v*` continuam como estão
  Feito (05/11/2026, agente C, fix/bugs-games-5; falta validar no GitHub, depende do push): o `release.yml` também
  roda em `workflow_run` do CI na `main` (evento push, conclusão success) e publica os pacotes daquela execução do CI
  (baixados por `run-id`, sem compilar de novo, renomeados de `VitaSharp-ci-<n>-*` para
  `VitaSharp-<último v* ou 0.0.0>-build.g<hash>-*`) como prerelease "VitaSharp <versão>+<hash>", tag `build-<hash>`,
  com a mesma checagem de firmware, `SHA256SUMS.txt` e notas com os commits desde a release anterior (build-* ou v*);
  nunca vira "latest" e só as 10 últimas ficam (`KEEP_BUILDS`; as antigas e suas tags são apagadas com `gh release
  delete --cleanup-tag`). README, seção Releases, atualizado. O APK continua de fora até o CI ter o job do Android
  Feito: release automática build-<hash> a cada push verde na main (6b85695), já publicando (build-eb950c1)
- [x] Atualização automática do emulador pelas releases do GitHub, no estilo do PCSX2 (pedido do usuário, 07/11/2026):
  ao abrir (e por um botão "Procurar atualizações" nas Configurações), consultar a API de releases do GitHub do
  projeto, comparar com a versão/hash do build em uso e, se houver uma mais nova, mostrar um diálogo com a versão, a
  data e as notas (lista de commits), com Atualizar / Pular esta versão / Lembrar depois. Atualizar baixa o pacote da
  plataforma certa (win-x64, linux-x64, osx-arm64), confere o `SHA256SUMS.txt`, troca os arquivos do emulador (um
  pequeno atualizador ou renomear-e-substituir, já que o exe em uso não pode ser sobrescrito no Windows) e reabre.
  Opções: canal (builds automáticas `build-*` ou só versões `v*`), checar ao iniciar liga/desliga. Nunca toca nos
  dados do usuário (pasta de dados, firmware, saves). Android: avisar e abrir o APK novo (instalação pelo sistema),
  depois que o CI publicar o APK
  Feito no desktop (08/11/2026, feat/product-1): `Core/Update` (releases da API, canal builds = build-* e v*, canal
  releases = só v*; mais nova = publicada depois da release do commit em uso, ou depois da hora do build quando o
  commit não está na lista; pacote win-x64.zip/-setup.exe/linux .tar.gz/.AppImage/osx .app.zip; `SHA256SUMS.txt`),
  `Updater.cs` (o exe do pacote novo roda `apply-update`: espera o processo antigo sair, copia só os arquivos do pacote
  por cima, nunca apaga nada e recusa se a pasta de dados estiver dentro da instalação, e reabre; instalação pelo setup
  roda o instalador; AppImage é renomeado por cima), diálogo no launcher (ao abrir e "Procurar agora"). O repositório
  vem do `GITHUB_REPOSITORY` do CI (`VitaSharpUpdateRepository`): build compilado do código não se atualiza.
  `VITASHARP_UPDATE_FEED` troca o feed. Validado com um feed local (pasta com `releases.json` e URLs file://): build
  1.0.0+7f2e54a achou a build-2222222 (9.9.9), `update --apply` baixou o zip de 158 MB, conferiu o SHA-256, o exe novo
  copiou 121 arquivos e reabriu (`version` = 9.9.9, `update` = em dia); diálogo em `ui-render --update-feed`;
  `UpdateFeedTests` com um servidor HTTP local (inclui pacote adulterado recusado). Falta: rodar contra o GitHub de
  verdade depois da primeira release com o repositório embutido, o caminho do setup.exe/AppImage/macOS, e o Android
  GitHub de verdade (08/11/2026, feat/product-3): a build-d99089e é a primeira release com o repositório embutido (a
  build-3cae19d e as anteriores não têm o atualizador). O pacote win-x64 dela, numa cópia, acha o próprio commit na API
  e responde "em dia"; o `version` mostrava "built 1600-12-31" (o pacote AOT não tem VitaSharp.Core.dll): agora o CI
  embute `VitaSharpBuildTime`
  Falta (pendente do próximo push da main, que gera uma build-* mais nova que a build-d99089e): a troca de verdade.
  Como testar, só leitura e numa cópia: (1) baixar `VitaSharp-0.0.0-build.gd99089e-win-x64.zip` da release
  build-d99089e para uma pasta nova na pasta de trabalho local, conferir o SHA-256 com o `SHA256SUMS.txt` e extrair em `inst`;
  (2) `inst\VitaSharp\VitaSharp.Desktop.exe --data=<pasta de teste vazia> update` deve listar a build nova;
  (3) o diálogo: `... --data=<pasta de teste> ui-render <saída> --update-feed=https://api.github.com/repos/<dono>/VitaSharp/releases?per_page=30`
  grava o PNG do diálogo com a versão, a data e as notas; (4) a troca: `... --data=<pasta de teste> update --apply
  --restart-arg=--data=<pasta de teste> --restart-arg=version` (sem `--restart-arg` ele reabre o launcher com janela),
  com o processo escondido; conferir no fim que `inst\VitaSharp\VitaSharp.Desktop.exe version` mostra o commit novo e
  a hora do build certa, e que `update` responde em dia. Nunca rodar na instalação nem na pasta de dados do usuário
- [x] Renomear `VitaSharp.Android.Common` (a lógica do app de celular que não depende do Android e é testada no PC:
  mapeamento de entrada, sensores, câmera, núcleos rápidos, ritmo de quadros, preferências) para
  `VitaSharp.Mobile.Core` (escolhido pelo usuário; o app continua `VitaSharp.Android`, um futuro iOS usaria o mesmo
  projeto); fazer quando não houver branches abertas (namespaces,
  `VitaSharp.sln`, `VitaSharp.Android.sln`, testes, docs)
  Feito (08/11/2026, refactor/mobile-core): pasta, projeto, namespaces `VitaSharp.Mobile.Core.*`, referências, as duas
  soluções, `Directory.Build.targets`, testes e docs; só mecânico, sem mudança de comportamento
- [ ] Refatorar o projeto para o guia de estilo de código do Ryujinx/Ryubing (pedido do usuário, 06/11/2026,
  substitui o guia do Google). Cópia local do guia e do `.editorconfig` dele (277 linhas, ponto de partida do nosso):
  `%USERPROFILE%/Ryubing/docs/coding-guidelines/coding-style.md` e `%USERPROFILE%/Ryubing/.editorconfig`. Regras
  ("padrões do Visual Studio"): chaves Allman; 4 espaços; campos privados/internos `_camelCase`, estáticos `s_`,
  thread-static `t_`, `static readonly` nessa ordem, `readonly` sempre que der, campos públicos em PascalCase e raros;
  sem `this.`; visibilidade sempre explícita e como primeiro modificador; `using` no topo, fora do namespace; no máximo
  uma linha em branco; sem espaços sobrando; `var` só com o tipo escrito à direita (`new`/cast) e `new()` só com o tipo
  à esquerda; palavras-chave (`int`, `int.Parse`); constantes e métodos (inclusive funções locais) em PascalCase;
  `nameof`; campos no topo do tipo; labels de `goto` um nível a menos; `if` de uma instrução nunca na mesma linha, chaves
  em todos os blocos se algum tiver; tipos privados/internos `static` ou `sealed`; XML docs em interfaces e no que for
  complexo; números mágicos viram constantes nomeadas. A regra 16 (caracteres não ASCII como escape Unicode) conflita
  com os textos em português da interface (launcher, Android, mensagens): decidir com o usuário antes (proposta: vale
  para o código, os textos de interface ficam em arquivos de recurso/strings). Cuidados: o GLSL gerado vem de strings
  literais do C# (o conteúdo delas tem que ficar idêntico, conferir com o shadertest e `replay-gpu --compare`), a regra
  9 deixa arquivos com estilo próprio como estão até serem convertidos, e fazer num momento sem outros branches abertos
  (o diff toca quase todos os arquivos); `dotnet format` + analisadores no CI para não voltar

## Bugs em aberto

- [ ] PSM: o shell não enxerga a atualização 2.01 do runtime (relato do usuário, 12/11/2026, captura
  `shell_20261112_030858_tudo`). Instalada em `ux0:patch/PCSI00011` (`runtime_version.txt` 2.01; o `ux0:app`
  continua 1.00), mas ao tocar no Cave Squid (NPOA00239, PSM) o shell ainda pede a atualização do runtime e falha
  no download (NW-8941-2, offline). Estudar como o firmware obtém a versão instalada do runtime (entrada do app.db
  atualizada pelo promote/reconstrução ou leitura de arquivo; sobreposição de ux0:patch nas leituras de ux0:app) e
  como o console instala uma atualização do PCSI00011 (ux0:patch ou substituição em ux0:app); corrigir com teste e
  confirmar que o Cave Squid passa a abrir o runtime.
- [ ] Desktop (Windows): em tela cheia, depois de tirar o foco do jogo (ir para outro aplicativo), não dá para
  voltar: o Windows toca o som de erro e a janela do jogo não volta; só fechando o jogo (relato do usuário,
  12/11/2026, "ainda está acontecendo"; ver o item de 04/11 do F11). Estudar o comportamento do GLFW em tela cheia
  ao perder o foco (auto-iconify), as regras de ativação do Windows (janela desabilitada ou dona) e a perda do
  swapchain; corrigir para Windows, Linux e macOS, com teste que falha antes.
  Causa (fix/fullscreen-focus, reproduzido neste PC com o título sintético): o GLFW minimiza a janela em tela cheia
  quando ela perde o foco (GLFW_AUTO_ICONIFY, ligado por padrão) e só a restaura no WM_SIZE/SIZE_RESTORED; o Alt+Tab
  (e o SwitchToThisWindow que o Windows usa) ativou a janela minimizada **sem restaurá-la**: ficou IsIconic e em
  primeiro plano, swapchain 0x0. Nesse estado o clique na barra de tarefas (na janela ativa) só minimiza de novo e
  as teclas vão para a janela minimizada como WM_SYSKEYDOWN/WM_SYSCHAR, que o DefWindowProc responde com o bipe
  padrão (o "ding"). Não era janela desabilitada nem dona (o launcher é outro processo; IsWindowEnabled verdadeiro,
  sem dono) nem a swapchain (ela volta a 1920x1080 assim que a janela é restaurada). Correção: o laço da janela
  (100 Hz) restaura a janela em tela cheia que o Windows ativou minimizada (`WindowHost.FullscreenNeedsRestore`);
  X11/Wayland/macOS não têm o estado "ativa e minimizada" (ativar mapeia/desminiaturiza). O log agora mostra
  estado/foco/framebuffer da janela e os resultados do acquire/present. Correção feita e conferida no repro
  (SwitchToThisWindow e Alt+Tab de verdade voltam à tela cheia, swapchain 1920x1080 recriada, foco de volta); falta a
  confirmação do usuário no jogo.
- [ ] Trocar entre o modo claro e o modo escuro demora muito (relato do usuário, 11/11/2026). Medir quanto leva a
  troca e onde o tempo vai (recriar a interface inteira, recarregar recursos/ícones/capas, refazer o layout da
  biblioteca) e fazer a troca só reaplicar as cores. Conferir no launcher do Desktop e no app Android.
- [x] **REGRESSÃO (11/11/2026, relato do usuário: "não estava assim")** Killzone Mercenary (PCSC00045): o HUD piscava,
  e F1 2011 (PCSE00002) mostrava quadros pretos isolados, no build 2e7789b2 (capturas F8 `PCSC00045_20261111_222844_tudo`
  e `PCSE00002_20261111_223250_tudo`). Causa: 758ad0a9 (a espera do sync object de fragmento no `sceGxmBeginScene`
  ligada por padrão; A/B ao vivo no F1 com `VITASHARP_DISPLAY_SYNC=0`: 0 presents no meio de uma cena contra 50). A
  espera segura no worker da GPU a cena seguinte no mesmo buffer até a fila de exibição trocar de buffer, mas o flip
  (o `PresentCommand` que o callback da fila envia pelo `sceDisplaySetFrameBuf`) ia para o fim da fila da GPU, atrás
  dos draws dessa cena: o worker liberava a cena, desenhava parte dela e só então apresentava o buffer (limpo ou pela
  metade). No console o controlador de vídeo não faz parte do fluxo de comandos da GPU. Correção: os flips da fila de
  exibição (`GpuQueue.SubmitFlip`, só na thread `SceGxmDisplayQueue`) rodam antes do próximo comando do worker e
  dentro da espera do sync object; os flips de threads do jogo continuam na ordem da fila. `VITASHARP_DISPLAY_FLIP_ORDER=queue`
  volta à ordem antiga (A/B). Teste `GxmFlowTests.DisplayQueueFlipComesBeforeALaterSceneIntoTheSameBuffer` (falha sem a
  correção: a cena do quadro 2 vinha antes do flip do quadro 0). Ao vivo (estado das capturas, sem janela): F1 26
  quadros pretos isolados em 501 -> 0, 50 presents no meio da cena -> 0; Killzone (rerun da captura com a entrada
  gravada) 7 e 11 [flash] -> 0 e 0; fps igual (F1 59,9/52,9/42 -> 59,6/54,0/42; Killzone 30 -> 30).
- [ ] Achados do jogo de teste VitaSDK (repositório local `VitaSharp-testgame`, 11/11/2026; 182 OK / 3 FAIL / 2 SKIP
  no VitaSharp sem janela). 1) e 2) corrigidos (revalidação de 12/11/2026, `fix/revalidate-2`): o vita-headers
  (`gxm.h`) define `SCE_GXM_TEXTURE_BASE_FORMAT_ETC1 = 0x84000000` ("Requires SCE_GXM_INITIALIZE_FLAG_EXTENDED_FORMAT",
  único formato `ETC1_1BGR`), agora decodificado na CPU com o formato da Khronos (`Etc1.cs`); e
  `SCE_GXM_COLOR_FORMAT_U2U10U10U10_ABGR` é "32-bit unsigned A2B10G10R10" (vermelho nos bits baixos): textura e
  superfície de cor passam a usar `A2B10G10R10UnormPack32`. Falta rodar de novo o jogo de teste para confirmar os dois
  casos. 3) A primeira espera de vblank depois do boot às vezes demora (+51 vblanks em 861 ms, esperado 30 em ~500 ms)
  enquanto os primeiros quadros compilam shaders e pipelines. Análise (12/11/2026): o relógio de vblank é o relógio do
  convidado (`VBlank.Count` = tempo do convidado / 16,68 ms) e 51 vblanks em 861 ms é a taxa certa (59,2 Hz); o que
  falta são os despertares: o laço chama `sceDisplayWaitVblankStart` 30 vezes e várias voltas acordaram depois do
  vblank seguinte (cada atraso maior que um período perde um vblank). No console a thread de prioridade alta acorda no
  vblank; aqui ela espera o host (`GuestClock.WaitUntil`) e um núcleo emulado livre (`Scheduler.Block`), os dois
  disputados pela compilação de shaders (e por qualquer outra carga no PC durante o teste). Não é erro de semântica:
  próximo passo é medir o atraso do despertar (FrameLedger) com a máquina livre; se repetir, olhar se a compilação
  roda numa thread que segura um núcleo emulado. Também falta cobrir PVRTC, YUV e cube no jogo de teste.
- [ ] Teste instável sob carga: `ThreadMgrHleTests.TimerEventsFireAndCancel` (11/11/2026) falhou 3 de 5 vezes
  isolado com o processador em 100% (7 agentes). Ele usa um timer de 20 ms e espera que um poll logo depois do wait
  não veja o evento: com a máquina carregada passa mais de um período entre as duas chamadas. Não veio da refatoração
  (IL idêntico, teste intocado). Deixar o teste independente do tempo real (intervalo maior ou relógio do processo
  controlado), sem afrouxar o que ele confere.
- [ ] Wipeout 2048 (PCSA00015): depois do loading do primeiro evento (Empire Climb), o loading e a corrida ficam
  travados em ~7,5 fps (60/8), com o cronômetro do jogo andando ~70 s em ~2 min reais; a CPU das threads é baixa
  (main 3,3 ms/quadro, GPU 6,6 ms/quadro), então parece espera de tempo/vblank/sincronização, não falta de CPU
  (agente hlegxm, 11/11/2026, headless; antes do loading o jogo fica a 60 fps). O jogo chega à corrida desde a
  função SceGxm NID_8FF68274 (2e9919e).
- [ ] Texturas de inteiro de 32 bits (U32, S32, U32U32) amostradas por um sampler float (agente T2, test/apps-2,
  11/11/2026): o renderer criava views R32_UINT/R32_SINT, mas o gerador de shader lê toda textura por um sampler float
  e guarda a palavra como bits F32; no Vulkan isso é indefinido (camada de validação: VUID-vkCmdDrawIndexed-format-07753;
  provável causa do crash no lavapipe). Agora são imagens R32(G32)_SFLOAT com os mesmos bits. `replay-gpu --compare` dos
  8 dumps: todos os quadros idênticos (média 0,0000). O teste de texturas passa a desenhar cubemaps por um samplerCube
  (antes um sampler2D, VUID-07752, erro do teste). **Passo em aberto:** repetir no lavapipe (WSL) os testes de renderer e
  a camada de validação; espera a decisão do usuário sobre `maxCrashDumpCount` no `.wslconfig`, porque `ulimit -c 0` não
  impede os dumps de ~6 GB que o WSL grava em `%TEMP%\wsl-crashes` (o core_pattern é um pipe e ignora o RLIMIT_CORE)

- [x] Superfícies de cor que não são de 32 bits (F16x4, formatos de 16 bits) começavam pretas em vez de com o que a
  CPU escreveu na memória (agente T2, test/apps-2, 11/11/2026): `UploadSurfaceFromGuest` só carregava superfícies de
  4 bytes por pixel; as outras eram limpas. Achado porque a varredura de NaN/Inf do modo de depuração não acusava uma
  superfície F16x4 cheia de NaN escrita pela CPU. Agora toda superfície cujo formato no host guarda os bytes do guest
  como estão (o mesmo layout cru que o writeback copia de volta) é carregada. Teste: `RendererPathsTests` (a anomalia
  "nan" com a superfície de NaN, nenhuma com uma limpa). Correção feita, ainda não validada com um jogo rodando

- [x] Música da LiveArea / tela inicial para "do nada" (relato do usuário, 10/11/2026, captura
  `shell_20261110_223155_tudo`, na LiveArea do LittleBigPlanet): o que o Vita toca ali é a música de fundo do sistema,
  `pd0:data/systembgm/home.at9` (registro `/CONFIG/SHELL/bgm` = 1; 162 s, em laço), pelo player do shell
  (SceShellMusicCore, porta BGM), na tela inicial e nas páginas de LiveArea; os sons curtos da interface saem na porta
  MAIN (SceWaveMain). Não é regressão de um commit: o main e o 1834769 (08/11) tocam igual numa sessão simples (boot,
  tela inicial 4 min com o laço, app de sistema e jogo com o botão PS: a música volta). Reproduzido (agente snd1,
  fix/shell-audio, `boot-shell` sem janela, nav `snd1_gw2.nav`): jogo (Batman PCSE00269) aberto e suspenso com o botão
  PS, depois o Navegador aberto da LiveArea: no instante em que o Navegador vem para a frente o player do shell chama
  `sceAppMgrReleaseBgmPort`, abaixa o volume da porta BGM até 0 e a libera (`sceAudioOutReleasePort`), e não pede a
  porta de novo nunca mais (tela inicial, LiveArea, outros apps: silêncio até reiniciar). Sem o jogo suspenso (só apps
  de sistema) isso não acontece. Achados do shell 3.74 (estudo): o pedido do player fica num campo (+0x94 do objeto de
  saída, -1 = não quer a porta) que o Controller do music core muda; a função 0x811E872A pede/solta a porta conforme
  esse campo; a resposta de `sceAppMgrGetCurrentBgmState2` usada é +8 (prioridade de quem tem a porta, comparada com a
  pedida). O jogo chama `sceAppMgrAcquireBgmPort` no boot (aqui sem efeito). Tentativas: (1) `sceAppMgrReleaseBgmPort`
  passa a liberar a porta de verdade (feito, não basta: o player não volta a pedir); (2) o jogo virar dono da porta
  ao chamar `sceAppMgrAcquireBgmPort` (revertido: a música do shell tocou por cima do jogo e a liberação no Navegador
  continuou). **Corrigido** (11/11/2026, agente snd1, decompilação do shell 3.74 e do appmgr do kernel, só estudo):
  (a) causa: o status de aplicação (`sceAppMgrGetStatusByName`) trazia em +0x50 o próprio title id; no kernel é o nome
  da aplicação que toca o BGM dela (`sceAppMgrSetBgmProxyApp`, appmgr 0x810070D4), vazio sem proxy. O PluginMain do
  music core (0x811CF8E2) segue esse nome para escolher qual dos seus players vai com a aplicação da frente; com o
  title id ali, abrir o Navegador desligou o player da música da tela inicial (mensagem 0x101) e nada o religou: o
  Controller ficou sem player ativo e pediu -1 (soltar a porta). (b) a porta BGM agora é roteada como no kernel
  (appmgr 0x81005EF8): cada processo tem uma prioridade de BGM (`sceAppMgrAcquireBgmPort` 0x100, `...ForMusicPlayer`
  0x82, `...WithPriority(p)` p, `sceAppMgrReleaseBgmPort` 0); entre os não suspensos ganha a maior positiva, sem
  nenhuma vai para o processo da frente (o shell na tela inicial e na LiveArea); as portas BGM dos outros ficam mudas;
  `sceAppMgrGetCurrentBgmState2` devolve +0 pid, +4 prioridade (0xFFFF = o da frente), +0xC app id. Validado
  (`boot-shell` sem janela, Batman → PS → Navegador → PS → tela inicial → LiveArea, dump por porta): a música para
  durante o jogo (o Batman pede a porta: 0x100) e durante o Navegador, volta depois de cada PS e continua na tela
  inicial e na LiveArea; o player não solta mais a porta. Gravar tudo na LiveArea depois do Navegador: "continuous
  sound on 01_port2_BGM_48000.wav: 12.4 s of 14.0 s", "BGM routed to shell ... PCSE00269 priority 0x100 (suspended:
  not counted)". O aviso "underrun burst" da porta MAIN do shell era alarme falso
  (porta parada entre sons curtos): corrigido (abaixo)
- [x] "underrun burst" em porta que só fica parada entre sons curtos (os sons da interface do shell: 5 s sem saída e a
  fila do host esvaziou) era alarme falso: uma saída depois de uma pausa maior que 4 grains (mínimo 250 ms) conta como
  "começo depois de pausa", sem aviso, e aparece no resumo da porta (agente snd1, fix/shell-audio)

- [ ] Metal Gear Solid HD Collection (PCSE00020), novo relato (11/11/2026, build 2e7789b2): "várias texturas zuadas,
  a tela do PC fica na frente do personagem, texturas popando". Captura `PCSE00020_20261111_224104_tudo` (dump da GPU
  de 383 quadros; log: alvo 0x60E0CA00 amostrado enquanto é desenhado, cena sem superfície de cor, texturas P8;
  findings [flash] e [picture-order]). Três sintomas: texturas decodificadas erradas, profundidade/ordem (tela do PC
  na frente do personagem) e texturas aparecendo tarde ou mudando de um quadro para o outro. Continua o item abaixo.
  - **Tela do PC na frente do personagem: correção feita (fix/mgs-gfx), validada no replay e no jogo rodando.** Os
    monitores (e vidros/hélices) são desenhados na cena 4X 360x224 com teste GREATER_EQUAL contra a profundidade que
    ela carrega da cena de 1 amostra 768x480; `SeedLoadedDepth` só copiava entre imagens com o mesmo número de
    amostras. Agora os texels são empacotados nas amostras (`PackDepthSamples`, um desenho por amostra com máscara).
    Replay do dump 224104: cabeças na frente dos monitores como no vídeo do Vita (GaPPb3zatq0, "Arm main parachute");
    jogo rodando a partir do `state_start.vss`: idem. Teste `RendererSurfaceReuseTests` (falha sem a correção).
  - **Fantasmas/efeitos atrasados ("texturas popando"): correção feita, validada no replay e no jogo rodando.** O
    atlas 1024x512 de bloom/DOF em 0x60E0CA00 substitui a cada quadro o alvo 360x224 no mesmo endereço e começava da
    memória do guest (um quadro ou mais atrasada): depois de um corte de câmera aparecia o capacete do plano anterior
    por cima, e o texto do monitor ficava fantasma sobre a cabeça. Agora o alvo novo copia os pixels do substituído
    (mesmo stride/formato/escala, CPU não escreveu depois). Teste no mesmo arquivo (falha sem a correção).
  - **Ainda aberto: cunhas vermelhas/azuis de um quadro ([flash]).** Não são ordem de flip: com 5ce57aa6 (fix/flicker)
    continuam (uma execução com 45 flashes e 3 picture-order, todas cunhas; sem ele 10; a contagem varia de 2 a 45
    entre execuções iguais). No dump 224104 a cunha é determinística: quadro 3259, desenho 201 (vs 42E7C2B1B81ED0DF,
    12 índices, mesmos streams dos desenhos 200 e 202, que saem certos) cobre meia tela de vermelho puro; a textura P8
    decodifica certo. `VITASHARP_UB_SCENE_END=audit` (262 mil desenhos): nenhum uniform buffer reescrito depois do
    desenho, e nenhum portão de cena liberado cedo; `=frame` não muda nada claramente (3 flashes). Próximo: ler o
    índice/vértices do desenho 201 no dump (endereço do índice no registro Draw) e ver se o dado gravado já veio
    errado da memória (CPU ainda escrevendo os vértices quando o worker leu) ou se é decodificação (formato de índice
    32 bits `st[16/1]`, atributos U8x4/F16x4).
- [ ] Metal Gear Solid HD Collection (PCSE00020), abertura do MGS3 dentro do avião (relato do usuário, 10/11/2026;
  `captures\PCSE00020_20261110_214209_tudo`, `_214218_tudo` com dump da GPU, `_214309_tudo` com a trava). Agente B
  (fix/bugs-b-7). Referência do Vita de verdade: YouTube GaPPb3zatq0 (longplay, 1:45-4:00).
  - **(2) cunhas vermelhas/azuis e borrão no interior: correção feita, validada só no replay** (2ae181d). A composição
    final lê a profundidade 0x60C80000 como textura X8U24 720x448. Ordem do quadro: cena de 1 amostra 768x480 que grava
    a profundidade, depois uma cena 4X com downscale 360x224 (efeitos, hélices, vidros) que carrega essa profundidade e
    não a grava. O renderizador pegava o alvo mais recente (o 4X, amostras não gravadas) e caía na memória do guest
    (nunca escrita); agora usa o último alvo cuja profundidade está gravada, como a memória do hardware. Replay do dump
    214218 (343 quadros): quadros com cunha vermelha 47 -> 1, personagens visíveis e imagem nítida (antes a névoa/DOF
    borrava tudo). As texturas P8 do quadro (trace) decodificam certo. **No jogo rodando** (estado da captura, 75 s)
    ainda aparecem cunhas de um quadro (5 de 111 quadros amostrados; main: 1 de 56), com o formato dos "flashes" do
    relatório: triângulos finos saindo de um ponto e regiões chapadas vermelhas/azuis que mudam a cada quadro, o que
    aponta para dados de vértice/uniform lidos fora de hora (ver FIFA 13, `UbLate`), não para a profundidade.
    Próximo: `--gpu-dump-at` num trecho com cunha no jogo rodando e `--trace-draws` no quadro ruim.
    `replay-gpu --compare` contra a main nos 24 dumps de `captures` e `gpudumps`: só os dois do PCSE00020 mudam; no
    PCSE00333_20261108_194141 29 de 1080 quadros diferem em até 0,05% dos pixels (ruído do replay: o dump não tem cena
    MSAA nem leitura de profundidade, o caminho alterado não roda).
  - **(1) vidro da cabine (vista externa "Flying over Pakistan"): causa achada, falta corrigir.** Os vidros e o borrão
    das hélices são desenhados na cena 4X 360x224 com teste de profundidade (função GREATER_EQUAL) contra a
    profundidade que ela carrega (load=True) da cena de 1 amostra. `SeedLoadedDepth` só copia de alvos com o mesmo
    número de amostras, então a cena 4X testa contra a própria profundidade do quadro anterior (ou zero): vidros da
    direita saem com a textura pontilhada azul opaca e o da esquerda sem o reflexo claro do Vita. Correção proposta:
    ao carregar a profundidade numa cena 4X (2X) cujo conteúdo mais novo está num alvo de 1 amostra no mesmo endereço,
    empacotar os texels nas amostras (amostra (x&1, y&1) do pixel (x/2, y/2) = texel (x, y); o inverso de
    `UnpackedDepthTexture`), com um passe de tela cheia por amostra.
  - Ainda aparecem triângulos soltos de um quadro (faixas azuladas finas, ex. quadros 2725 e 2898 do replay).
  - **(3) trava: reproduz, causa no protocolo do jogo, sem correção ainda.** Reproduz no boot carregando o
    `state_start.vss` da captura 214218 (o boot vai direto ao MGS3.self): 4 de 5 boots param entre os quadros 4 e 36
    com `main` e `End Frame Thread` esperando o mesmo event flag "End Frame Event". Protocolo (trace HLE):
    main `Wait(2)`, `Set(1)`, `Clear(~2)`; End Frame `Wait(1, clear pat)`, trabalho, `Set(2)`. Trava quando o End Frame
    faz o ciclo inteiro e `Set(2)` antes de a main chegar ao `Clear(~2)` logo depois do `Set(1)` (a main então apaga o
    "pronto" e as duas esperam para sempre). No Vita o `Set`→`Clear` da main leva ~1 us; aqui o relógio emulado da main
    pula ~5,7 ms entre as duas chamadas (ela perde o núcleo/thread do host). A trava do usuário no quadro 3416 tem o
    mesmo formato (End Frame esperando o flag; a main aparecia "Running" num callback, o que b86dc3b corrige no dump).
    Tentativas medidas (5 boots cada, base 4/5 travando): liberar os waiters na hora do `Set` como o kernel (2/5),
    CPU sem ritmo `--cpu-mhz=0` (2/5), ceder o núcleo a uma thread de prioridade maior em todo retorno de HLE (3/5):
    nenhuma resolve, todas revertidas. Próximo passo: instrumentar o Scheduler (quem segura os 3 núcleos quando a main
    espera núcleo depois do `Set`, e por quanto tempo) e o tempo de host da main entre as duas chamadas.
  - **MGS2 a partir do menu da coleção: trava logo depois de abrir** (boot limpo, menu MGS3|MGS2, direita + X no
    quadro 260): `LoadExec app0:MGS2.self` ok, módulos, NGS, troféus e BPE Render Thread criados, e no quadro 523 a
    main espera o fim da thread 'BackgroundLoadSDX', que espera uma operação do FIOS (condvar SceFiosOpWait) que nunca
    termina; as threads do FIOS (SceFiosIO, SceFiosCallback...) dormem em SceFiosSchedulerWakeup. Nenhum erro de HLE
    ou arquivo no log. Uma tentativa só; próximo: `--trace-hle-only=sceIo+sceFios+sceKernelSignalCond` nesse boot para
    ver se a operação chegou ao sceIo e quem deveria acordar o escalonador do FIOS (pode ser o mesmo tipo de corrida
    da trava do MGS3).
- [ ] Android, controle conectado no celular (relato do usuário, 10/11/2026): (1) o L2 não funciona (conferir o
  mapeamento do eixo/botão do gatilho: no Android o L2 pode chegar como AXIS_LTRIGGER/AXIS_BRAKE ou KEYCODE_BUTTON_L2,
  conforme o controle; o Vita não tem L2, então conferir para o que o VitaSharp o mapeia (toque traseiro/atalho) e se
  isso funciona); (2) com controle de PS4, o touchpad do controle vira mouse no Android e esse ponteiro se mistura com o
  toque na tela: o app deve tratar o touchpad do DualShock 4 como entrada do controle (SOURCE_TOUCHPAD/MOUSE do
  dispositivo do controle, separado do SOURCE_TOUCHSCREEN do aparelho; ou capturar o ponteiro) e mapear para o toque
  frontal/traseiro do Vita sem mexer no toque da tela. Validar com o controle real no celular.
- [x] Tradutor de shaders: GLSL que não compila em dois casos (agente de testes, 11/11/2026; vai para o dia de melhorias:
  mexe em todo shader, precisa subir a versão do gerador e replay-gpu --compare em todos os dumps). Achado pelo
  ShaderFuzzTests (os 32 opcodes USSE com operandos pseudoaleatórios, vertex e fragment, compilados com shaderc): 35 de
  768 programas. (1) dFdx/dFdy emitidos num vertex shader ("no matching overloaded function"): vs 0x0DBA30DF384131F5,
  0x0D3182821B81332C, 0x11946D4F2B33455D, 0x10149DB94DB5476E, 0x141042446720CD6D. Conferir no hardware o que o USSE
  faz com derivada num vertex program (provavelmente 0) e emitir isso. (2) índice constante além do array de
  registradores declarado ("array index out of range"): 0x0868574A028E3EB4 (índice 19), 0x1A3B247670E1028A (5),
  0x31AC7E864902C658 (143), 0x3C5942B26370B3DD (27), 0x42CA9BAA3693FE7A (51), em vs e fs. O array deve ter o tamanho do
  maior índice que alguma instrução pode endereçar, incluindo as formas indexadas/relativas; hoje o draw inteiro cai.
  Teste: ShaderFuzzTests.EveryTranslationCompiles (Skip até a correção; tirar o Skip junto). **Corrigido** (agente de
  testes T1, 11/11/2026, gerador v26): (1) derivada num vertex program vira 0 (o USSE deriva pelo quad 2x2 de pixels, que
  um vértice não tem; o compilador de shaders nunca a emite ali); (2) os arrays têm o tamanho do maior índice constante
  usado (o teto de 64 registradores fazia o GLSL não compilar; fi também é rastreado). A rodada sem Skip achou mais dois
  casos, corrigidos juntos: registrador de índice fora de 1/2 (i.l/i.h; escrita sem efeito, leitura 0) e DEPTHF num
  vertex program (sem profundidade de fragmento: ignorado). Testes: EveryTranslationCompiles (0 de 768 falham) e
  StageAndRegisterLimitsFollowTheHardware.
- [x] Tempo de processo menor que um delay em volta dele (agente de testes, 10/11/2026): dois sceKernelGetProcessTimeWide
  em volta de sceKernelDelayThread(2000) num thread guest mediram 1839 us; no Vita é sempre >= 2 ms. Causa:
  GuestClock.Observed é monotônico entre threads (_lastObserved) e um thread com ritmo de CPU (que corre até ~1 ms à
  frente do tempo real) deixa o relógio adiantado; a leitura antes do delay já sai adiantada e o delay contava do tempo
  real. **Corrigido** (agente de testes T1, 11/11/2026): o delay começa no maior entre a posição do thread e o último
  tempo que algum thread leu (`GuestClock.ObservedFloor`, limitado a 5 ms à frente do relógio). Teste sem Skip:
  ThreadMgrHleTests.ProcessTimeAroundADelayIsAtLeastTheDelay (agora simula a vantagem de 1 ms de outro thread a cada
  medida). Correção feita, ainda não validada com jogo (limitadores de quadro que medem o próprio sleep).
- [x] MsgPipe: envio FULL e tamanho do buffer como o threadmgr (agente de testes T1, 11/11/2026; decompilação do
  threadmgr 3.74, só estudo: envio 0x81013DFC, TrySend 0x4CF1BE58, criação 0x81015D10). (1) Uma mensagem maior que o
  buffer é recusada com 0x8002000B (ILLEGAL_SIZE) nos dois modos. (2) FULL só entra no buffer inteira: o Try falha com
  MPP_FULL sem mandar nada (antes mandava a parte que cabia e devolvia MPP_FULL) e o bloqueante espera caber tudo. (3) O
  buffer é o tamanho pedido arredondado para páginas de 4 KB (o bloco de memória que o threadmgr aloca; o campo de
  capacidade guarda o arredondado). Testes: ThreadMgrHleTests.MsgPipeSendReceiveModes,
  MsgPipeFullSendWaitsForRoomForTheWholeMessage. Correção feita, ainda não validada com jogo.
- [ ] MsgPipe: DONT_WAIT (0x10) nas formas bloqueantes (sceKernelSendMsgPipe/ReceiveMsgPipe e Vector) não é "falhar em
  vez de esperar" no firmware: o threadmgr copia o vetor, enfileira o pedido para os threads "SceKernelMsgPipeAsync"
  (até 0x100 pedidos por processo, depois 0x80028225) e volta na hora; o envio/recebimento acontece depois nesse thread
  (decompilação 0x81015004 / 0x8101533C, só estudo). O HLE trata 0x10 como Try. Também não há entrega direta a um
  receptor que espera (o firmware passa os dados ao receptor antes do buffer) nem pipe sem buffer (tamanho 0). Conferir
  com um jogo que use 0x10 antes de mudar.
- [x] Dragon's Crown (PCSE00019): flicker e texturas erradas (relato do usuário, 10/11/2026, capturas
  `PCSE00019_20261110_214605_tudo` e `..._214648_tudo`). Agente A4 (fix/bugs-a-4), duas causas:
  (1) **corrigido em bf2f3b9**: o jogo lê o back buffer (960x544, pitch 1024) como uma textura 1024x1024 que também
  alcança o segundo back buffer 3 MB adiante; com outro alvo dentro da memória da textura o renderizador lia a memória
  do guest, que não tem nada de um alvo nunca gravado de volta: um bloco magenta (memória velha) em quadros alternados
  no logo da Atlus e na criação de personagem. Replay da captura: magenta em 261/263/265, nenhum depois; rerun da
  captura B (headless): 0 quadros magenta.
  (2) a ordem dos quadros: sem esperar o display soltar o buffer, o jogo renderiza de novo um back buffer ainda na fila
  e o display mostra o quadro novo antes e o velho depois (achados "picture-order", fades em pares A B A B). Com o
  `VITASHARP_DISPLAY_SYNC` (a espera do fragment sync object no BeginScene, como no hardware) o rerun da captura B
  caiu de 26 flashes + 5 trocas e 10 + 1 para 0 + 0 e 1 + 0 (dois reruns de cada), com fps mediano 38-39 contra 32-34.
  Ligado por padrão em 758ad0a (`=0` desliga). A/B de 60 s headless (mediana de fps com/sem): Killzone 29,9/29,9,
  Uncharted 59,5/19,1 (a rodada sem pegou o PC carregado), Persona 4 29,8/29,8, Jak 20,1/20,1, Sonic Mania
  51,3/32,3, Gravity Rush 22,4/29,9 numa rodada com o PC carregado e um "still displayed after 250 ms", 29,9 cravados
  numa segunda rodada com `=trace` (esperas de 0,1-27 ms, nenhuma de 250 ms). Falta: validar no celular (Killzone) e
  numa janela; uma única espera de 250 ms apareceu no Killzone (no fim da execução) e no Gravity Rush (PC carregado).
  Sobra 1 quadro preto isolado na criação de personagem (também sem a correção)
  Concluído (11/11/2026, dia de refatoração): as duas causas corrigidas na main (bf2f3b9, 758ad0a); o que sobra
  (validação no celular, o quadro preto isolado) fica nos itens do Killzone/celular e do renderizador
- [x] Dead Space (port Android, DEADSPACE, so_loader + vitaGL): não rodava sem o kubridge (08/11/2026). Feito (agente C,
  fix/bugs-c-24): kubridge em HLE (aabf2d6), VFP short vectors no CPU (7501ec8: o motor do jogo usa FPSCR LEN=8),
  trampolim do hook por offset em endereço de meia palavra (907b204), hooks de import/export e
  plugins do config.txt (805af91). Validado headless até o gameplay (logo da EA, menu de dificuldade, cinemática,
  Isaac no corredor). **Relatos do usuário de 09/11 (00:2x)**: (1) "o andar para frente do teclado não funciona": o
  port transforma o analógico esquerdo num toque no joystick da tela, e um analógico que pula do centro para a borda
  numa amostra não anda (nav `lstick 0 -1`: Isaac parado; o mesmo empurrão em 6 quadros, `lstick 0 -1 120 6`: anda até
  a sala seguinte). Correção: o analógico do teclado percorre o curso em 100 ms (KeyStickSlew); falta o usuário
  confirmar com o teclado. (2) "o som sumiu de repente": a porta BGM continuou mandando áudio não silencioso, sem
  underrun, mas só ~661 s "ativos" dos 837 s e a gravação F8 das 00:21 não tem nenhum WAV: o fluxo de áudio do jogo
  parou de sair perto das 00:19:50, quando o controle DS4 (que tem saída de fone própria) conectou/desconectou várias
  vezes. Hipótese: o OpenAL parou de consumir buffers e o sceAudioOutOutput ficou bloqueado. Correção feita, **não
  validada** (não reproduzi a troca de dispositivo): OpenAlAudio verifica ALC_CONNECTED e o padrão do sistema a cada
  1 s e move o dispositivo com alcReopenDeviceSOFT; nunca segura a thread de áudio do jogo mais de 1 s. Próximo passo:
  reproduzir conectando/desconectando o controle com o jogo rodando e conferir o log "Audio: ... now on"
- [x] Silent Hill: Book of Memories (PCSE00011): a lanterna e as luzes não iluminavam (relato do usuário, 08/11/2026,
  `captures\PCSE00011_20261108_225425_tudo`). Causa: o buffer de luz (superfície 0x41200000, U2F10F10F10 num F16x4 do
  host) guarda no alfa a profundidade de cada pixel e cada luz (stencil volume + programmable blending) reconstrói a
  posição a partir dele; o gerador prendia o alfa em 0..1, então nenhuma luz (lanterna incluída) somava nada. Dentro da
  cena o hardware lê de volta o registrador de saída F16 do tile. Correção (ShaderGenerator v24): o alfa não é mais
  preso. Replay: o spot da lanterna (draw 81) passa de 0 a 23 mil pixels acesos, o personagem e o chão à frente
  iluminados (`<scratchpad>\c24\rp\bomfull\frame_007538.png`, antes `cmp\base\...\frame_007560.png`);
  `replay-gpu --compare` dos 19 dumps/gravações: só o PCSE00011 muda. Real Vita (YouTube -fQbhws3LyI, zona 1) ainda
  parece mais claro ao redor do jogador: conferir no jogo rodando. Falta validar ao vivo
  **Validado ao vivo** (agente A, 10/11/2026, main bfae931, headless com o save do usuário copiado): Continuar >
  Seleção da Área > Área 1 (Fogo) > Sim; na ponte do começo o jogador e o chão à frente dele saem iluminados pela luz
  dele, como no vídeo do Vita real (`<scratchpad>\bA\proofs\bom_live_flashlight.png`, `..._flashlight2.png`). O save
  state da gravação do usuário não carrega mais (formato de outra versão), por isso não deu para repetir o ponto exato
  dela; falta o usuário confirmar no ponto dele
- [ ] Salt and Sanctuary (PCSE01023): logo da Ska Studios pisca na introdução (relato do usuário, 08/11/2026,
  `captures\PCSE01023_20261108_220030_tudo`). Achados (não resolvido): o replay reproduz (quadros no 0x66600000 claros,
  ~3,3x os do 0x66900000); os dois quadros têm os mesmos 5 desenhos (quad preto em tela cheia, um mask update em tela
  cheia com stencil ALWAYS, e três sprites aditivos com alfa crescendo suave), mesmas texturas (md5 iguais). O desenho
  2 é um programa de mask update, que o VitaSharp ignora (o Vita3K implementa a máscara GXM: imagem de máscara por
  alvo, iniciada pela background mask da superfície de profundidade, ALWAYS=1/NEVER=0, desenhos rejeitados onde a
  máscara é 0). Próximo passo: ver no replay qual dos desenhos difere entre os dois buffers (`--draw-limit=N@17/18`) e
  se a máscara GXM explica (implementar como o Vita3K: vulkan/context.cpp, scene.cpp, state_set.cpp)
  Agente A (10/11/2026, fix/bugs-a-1): **não é a máscara**. O mask update usa stencil ALWAYS, que no Vita3K grava 1 em
  toda a área (máscara 1 = nada rejeitado), igual a ignorar. O que difere é o viewport: nos quadros escuros os desenhos
  4 e 5 (brilho do logo e anéis) saem com `sceGxmSetViewport(0, 0, 0, -0, 0.5, 0.5)` (trace do replay:
  `vp=True(0,0,0,-0,...)`), nos claros com (480, 272, 480, -272). Ao vivo (headless, `--probe`/`--watch`): o motor do
  jogo guarda o retângulo do viewport em duas estruturas por quadro (0x6880 bytes, índice em +0x5C do device, trocado
  no Present em 0x81D8DCE2); o setter do jogo (0x81D8E0B2) só grava na estrutura do índice atual, e as duas chamadas
  dele (início do jogo, 31 ms uma da outra, a segunda por um delegate em 0x8122DBE6) acontecem antes do primeiro
  Present, as duas com índice 0. A estrutura 1 fica zerada (memset na criação) e o Clear (0x81D8D08A, depois do mask
  update) reaplica o viewport da estrutura do quadro: nos quadros ímpares, zero. O `sceGxmSetViewport` do libgxm real
  (0x8100AA2C) só guarda os seis floats e marca o estado (0x100 em ctx+0x1C0), copiados crus para o fluxo do hardware;
  nada trata escala 0. Vídeo do Vita real (YouTube 5JSM07SCA1s, 2-4 s): logo branco e estável em todos os quadros (o
  quadro claro do VitaSharp tem a mesma luma do logo: 155 contra 138). Logo no Vita real alguma chamada do setter cai
  com índice 1 (a 2ª chamada vem de um evento; no Vita real ela deve chegar depois do primeiro Present). Hipótese em
  aberto: um evento do sistema/janela que o VitaSharp entrega cedo demais (ou uma vez só). Próximo passo: descobrir o
  que dispara o delegate de 0x8102A788 (pilha 81000104 < 81000A0C < 81487FC8 < 8122DBE6 < 8103D404 < 82021B36 <
  8102A78C) e comparar com o Vita3K
  Rodada 2 (agente A, fix/bugs-a-2, `--trace-hle` do quadro 0 com probes no setter e no Present): entre as duas
  chamadas do setter o jogo abre a primeira cena (BeginScene + o Clear) e lê `savedata0:/config.ini`
  (`sceIoGetstat` -> não existe; stdout "Missing config: /savedata0/config.ini", "Read config failed"); a 2ª chamada
  é a aplicação dos padrões depois dessa falha, ainda antes do 1º Present (índice 0). O save do usuário (`dat0.slv`,
  `SlotParam_0.bin`) não tem config.ini e o jogo não grava um em 90 s de menus. O ritmo é do próprio jogo: laço de
  passo fixo com `sceKernelDelayThread` calculado e um vblank por troca (a fila de exibição faz `SetFrameBuf` +
  `WaitVblankStart(1)`), 30 fps, então no Vita a alternância também seria visível a 15 Hz. Hipótese que sobra: com um
  `config.ini` existente (o jogador do vídeo já tinha jogado) a aplicação da configuração acontece depois do 1º
  Present e cai na estrutura 1. Próximo passo: descobrir o formato do config.ini (o jogo lê com o FIOS) e testar com
  um; se for isso, o defeito também existe num Vita sem config e não é do emulador
  Rodada 3 (agente A4, 10/11/2026, fix/bugs-a-4): **o config.ini não é a causa**. O jogo grava o config.ini ao sair de
  Opções > Áudio ("Config: save done"); com ele presente ("Read config done") a 2ª chamada do setter continua antes do
  1º Present e a alternância continua igual (luma 18,5 / 6,1 em quadros alternados, `<scratchpad>/bA4/out/s3/log.txt`). O motor
  (eboot decifrado, só estudo): dois blocos por quadro de 0x6880 bytes (malloc + memset 0 em 0x81D8D8F0), índice em
  device+0x5C trocado no Present (0x81D8DC30); o setter de viewport (0x81D8E0B2, chamado por uma tabela em 0x82207384)
  só grava o bloco do índice atual; o scissor (0x81D8E0DA) e o rasterizer state (0x81D8DDD4) são chamados todo quadro,
  e o "aplicar scissor" (0x81D8D08A) marca +0x158 para reaplicar o viewport do bloco: no bloco 1 os campos são zero, e
  os desenhos seguintes (brilho e anéis do logo, a árvore da tela "o jogo salva...") saem com sceGxmSetViewport(0,0,0,
  -0,...). Do quadro 1 ao 1384 (logos, aviso de salvamento, carga) o setter não é chamado nenhuma vez; do menu em diante
  é chamado várias vezes por quadro. O libgxm do firmware (decompilado: sceGxmSetViewport 0x8100AA2C só guarda os seis
  floats; sceGxmBeginScene chama sceGxmSetDefaultRegionClipAndViewport) não trata escala 0, e o Vita3K também não.
  Vídeo do Vita real a 60 fps (5JSM07SCA1s, formato 298): logo branco sem nenhuma alternância quadro a quadro. Ou seja,
  com o mesmo código o Vita desenha esses quadros com o viewport cheio: falta descobrir o que no hardware (ou no
  libgxm, no caminho de emissão `FUN_8100e9b4` / estado em ctx+0x1C0) faz um viewport de escala zero não colapsar a
  geometria. Próximo passo: ler a emissão do estado do viewport no libgxm até os registradores (flags 0x100 e o
  ViewportEnable em ctx+0x214 bit 0x8000) e procurar no SDK/PowerVR o significado de escala 0
  Lentidão de ~70 s ao sair da criação (quadros 1934-1977 da varredura): **não reproduz** (agente A4, 10/11/2026). Com o
  roteiro da varredura (cross a cada 150 quadros; o START do sweep.ps1 nunca disparava: 400+150k nunca é múltiplo
  de 600) e cache de shaders quente ou frio, 180 s a 28-30 fps, só uma queda curta a 15 fps na entrada da criação
  (`<scratchpad>/bA4/out/s5`); com navegação guiada, Aventurar-se > Sim chega ao gameplay em ~40 s a 30 fps. A rodada
  da varredura foi feita com o disco a 99% e o Storage Sense apagando arquivos do build (seção "Não rodados" do
  documento da varredura): a lentidão era do PC, não do jogo
- [x] Silent Hill 4 (SILH00004), visual (câmera; o relato não era de controles): não iniciado nesta rodada. Preparado: `<scratchpad>\c24\sh4.nav` e
  `ux0:data/sh4/env.txt` com `SH4_AUDIT_PAD=1` (o port loga `[audit-pad]` com os eixos e `[audit-pad-player]` com a
  rotação). Atenção: nos scripts NAV, `lstick x y` usa -1..1 (para frente = `lstick 0 -1`); agora aceita rampa
  (`lstick x y quadros rampa`)
  Agente A (10/11/2026, main bfae931, headless com `SH4_AUDIT_PAD=1`): **nada de errado achado no lado do emulador**.
  Título, Options (cima/baixo/círculo), dificuldade, abertura e o quarto em primeira pessoa respondem; o `[audit-pad]`
  do port recebe os eixos crus certos dos dois analógicos (esquerdo Y -32767/+32767 para frente/trás, esquerdo X
  +32767, direito X +32767) e a câmera anda e vira. O mapeamento é do port: o direito X vira `axes[0]` (girar) e o
  esquerdo X não entra em nenhum eixo em primeira pessoa (`axes=0,0,...`), escolha do port e não do VitaSharp. Achado à
  parte: com o `sh4.sav` do usuário copiado (694288 bytes, lido inteiro duas vezes no boot) o Load mostra as 10 vagas
  "No game data" e o Continue fica cinza. Falta o usuário dizer o que nos controles parece errado (botão, eixo, menu
  ou gameplay)
  Agente A (10/11/2026, fix/bugs-a-2): o "No game data" **está certo**: o `sh4.sav` do usuário só tem a área de
  sistema/opções do port (4 bytes em 0 e 1508 em 0x7C, o resto zero), nenhuma vaga de jogo. O port o criou no primeiro
  boot (log do usuário de 08/11 17:25: CreateFileA falhou, depois "[save] staged replacement complete" duas vezes, a
  segunda sobre o arquivo existente, então gravar e renomear funcionam); a cópia do Vita3K do usuário é idêntica
  (mesmo md5) e nenhuma sessão depois gravou nele. O usuário nunca salvou um jogo, em nenhum dos dois emuladores.
  **Correção do usuário (10/11): o relato é visual, não de controle**: "quando mexo a câmera parece que o cenário se
  mexe, não o personagem". Achados (gravação `captures\SILH00004_20261108_211441_tudo`, a única que existe):
  girando para a direita (analógico direito X=255), o centro da câmera tirado da matriz WVP de cada desenho de cenário
  fica parado em (417, -300, -335) nos quadros 1812-1844 (todos os 19 desenhos estáticos; os com skin usam as mesmas
  linhas de vista), então a vista é um giro no lugar (yaw +0,0667 rad/quadro, o valor do próprio jogo no
  `SH4_CAMERA_TRACE=1` do port), sem matriz velha nem desenho atrasado. A vista inicial de um New Game sai igual pixel a
  pixel no backend IL e no nativo (diferença média 0,4/255). O que difere do vídeo do Vita real (YouTube i1CuZULiQ6w,
  mesmo quarto) é a abertura: aqui HFOV ~70°/VFOV ~44° a 640x362 e a câmera colada nas paredes; um FOV estreito faz o
  giro parecer o cenário deslizando. A projeção vem das constantes do próprio jogo (código x86 do port sobre o newlib
  dele, sem libc/libm do sistema; o port não tem opção de FOV). Falta uma referência no mesmo estado (o port no
  Vita3K do usuário com `SH4_CAMERA_TRACE=1`, ou um print logo depois de "He couldn't leave Room 302..." num New Game;
  o nosso: `<scratchpad>\bA\proofs\sh4_newgame_start.png`)
  Referência do Vita3K (rodada do usuário com `SH4_CAMERA_TRACE=1` e `SH4_CAMERA_SNAP=1`, 10/11): a estrutura da
  câmera do jogo é a mesma nos dois emuladores (no nosso dump achada em 0x848E5B70, no snap do Vita3K em +0x11B70):
  olho a 300 unidades acima da base (Vita3K -15/-315, aqui -18/-318: o Vita3K tinha andado, x/z também diferem),
  vetores de direção unitários, o par de parâmetros da câmera (-3.721, 100.0) idêntico, e o giro igual (0,0667
  rad/quadro na mesma sensibilidade 3/10). Ou seja, a altura, a inclinação e a abertura saem da mesma conta do port
  nos dois: a vista estreita é do próprio port, não do VitaSharp. Diferenças pequenas que sobram: o 4º componente dos
  vec4 de posição é 1.0 aqui e 0.0 no Vita3K (não entra na matriz WVP que medimos), e o yaw inicial de um New Game sem
  entrada é -1,395092 aqui contra -1,370750 no Vita3K (o usuário mexeu no analógico antes). O vídeo do Vita real
  (i1CuZULiQ6w) parece mais aberto; pode ser outra versão do port. ~~Fechado como comportamento do port~~ **Reaberto** (usuário, 10/11: o Vita3K não é
  referência; vale só o vídeo do Vita real `kHpX5jbI5DE`, enviado em 30/10, mesmo dia do v1.0.1 = o eboot do usuário,
  md5 idêntico). Comparação lado a lado com a gravação nova `captures\SILH00004_20261110_155931_tudo` e um New Game
  headless: no vídeo o texto da introdução ("Pero algo extraño...") aparece **sobre a cena 3D** (Henry na cama olhando
  o ventilador do teto, com letterbox) e depois vem um despertar roteirizado de ~8 s (teto, vistas inclinadas); aqui o
  mesmo texto sai sobre um **cinza chapado** e o despertar fica escondido no mesmo cinza, até a vista parada na parede
  (`<scratchpad>\bA\yt\cmp_intro.png`). Dump da GPU da introdução (`<scratchpad>\bA\runs\sh4intro.vsgd`, quadros
  2350-2420; trace de 2400): o alvo da cena 0x60FBE800 recebe 23 desenhos de cenário (vs A6BA1847, fs 63EB500A) e
  depois `mask update` NEVER/ALWAYS (o vitaGL usa a máscara GXM, que o VitaSharp ignora) e um quad sem fragmento; no fim
  do quadro o alvo está cinza com o texto. Suspeitas: a máscara GXM ignorada (o mesmo do Salt and Sanctuary, mas aqui com
  NEVER, que zera a máscara e deveria rejeitar desenhos) ou a névoa (fog) saindo total. Os quadros intermediários de
  60 fps (interpolação do port, qgl.c) estão coerentes: com a cena parada, quadros seguidos iguais, com e sem
  `SH4_INTERP=0`. Próximo passo: implementar a máscara GXM (Vita3K: imagem por alvo, iniciada pela background mask,
  mask update grava 1 com stencil ALWAYS e 0 com NEVER, desenhos rejeitados onde é 0) e repetir o replay do dump
  **Resolvido** (agente A, 10/11/2026, fix/bugs-a-3): **não era a máscara nem a névoa do jogo, era o tradutor de
  shaders**. O programa de vértice com névoa do vitaGL (vs A6BA1847) calcula o w do clip com `VTST.F32` de produto
  escalar (`fi1.x = dot(fi0, fi1)`, o teste `== 0` antes do 1/w, que também entra no fator da névoa); o tradutor só
  carregava o canal testado e gerava `fi0.x * fi1.x`. Com o w errado a cena saía fora da tela (introdução: só a cor
  de limpeza 15/255 com o ruído por cima = o cinza chapado) e, no jogo, a perspectiva saía errada (a "abertura
  estreita"/câmera colada nas paredes do relato de 08/11: era isso, não o port). Correção genérica: teste com VDP
  reduz o vetor inteiro (gerador v25). Replay de `sh4intro.vsgd`: ventilador do teto sob o texto; ao vivo (headless,
  `<scratchpad>\bA3\nav\sh4i`) a sequência igual ao vídeo kHp_start: texto sobre a cena, despertar no teto com a
  porta e vistas inclinadas, depois a vista de pé na escrivaninha com o abajur, mesmo enquadramento
  (`<scratchpad>\bA3\cmp_intro_fix.png`, vídeo à esquerda). Brilho nos mesmos pontos: luma média 24,3/20,8/24,5 no
  vídeo contra 22,1/20,3/23,4 aqui (diferença da compressão do vídeo). A máscara GXM continua ignorada: nos quadros
  do SH4 o mask update NEVER é seguido de um ALWAYS da tela inteira (o scissor do vitaGL desligado; com ele ligado o
  vitaGL também põe o region clip no retângulo), então nenhum jogo testado depende dela; fica para quando um jogo
  precisar. O Salt and Sanctuary não muda com esta correção (replay idêntico; lá é o viewport zerado, item próprio)
- [ ] Comparação com o Vita3K (pedido do usuário): lista de lacunas em `<scratchpad>\c24\vita3k_gap.tsv` (agente de
  tarefas rápidas). Do lado do usuário só ~20 funções reais faltavam; feitas: aliases sceKernelLock/Unlock/TryLock
  LwMutex_0/_16XX, _sceKernelSuspend/ResumeThreadForVM, _sceKernelGetRandomNumber,
  sceFiosKernelOverlayResolveWithRangeSync02. Faltam: sceKernelWaitMultipleEvents(CB), _sceKernelGetTimer5Reg,
  sceShellSvcGetSvcObj, IPMI::Client::create; o resto é do lado do kernel (o Vita3K roda .skprx em LLE; aqui o
  kubridge é HLE). Lista de LLE automático do Vita3K (HTTP, SSL, PGF, FIOS2, MP4, AVPLAYER...) a comparar com jogos
  Feitas (10/11/2026, agente B, fix/bugs-b-2), pelo threadmgr/processmgr/libkernel do 3.74 desmontados (o Vita3K tem
  as duas primeiras como UNIMPLEMENTED no kernel): sceKernelWaitMultipleEvents(CB) e _sceKernelWaitMultipleEvents(CB)
  (eventos {uid, padrão}, até 64, modo OR 1/AND 2; cada evento é tomado quando bate, com o auto-reset limpando na hora,
  e entra nos resultados {uid, resultado, padrão, 0, userData} na ordem em que disparou; uid desconhecido entra com
  UNKNOWN_EVENT_ID/0x10; devolve o número de resultados, ou WAIT_TIMEOUT; serve para eventos simples, timers, msgpipes
  e I/O assíncrono) e _sceKernelGetTimer5Reg (o endereço de uma página com o contador de microssegundos de 64 bits
  que o libkernel lê direto para o sceKernelGetProcessTime*, mantida por uma thread do host a cada 1 ms). Testes em
  `KernelGapTests`. Dos 47 jogos instalados, só o Batman: Arkham Origins Blackgate (PCSE00269) importa
  WaitMultipleEvents (FEvent::WaitForMultiple do motor, OR com resultados e AND sem); no boot até o gameplay
  (`bat_game.nav`, `<scratchpad>\bB2\runs\bat\g20.png`) ele não chama, então a correção não foi vista num jogo.
  Nenhum jogo importa o Timer5Reg (só o libkernel do firmware, que aqui é HLE), sceShellSvcGetSvcObj ou as funções
  SceIpmi: nos programas do sistema elas vêm dos módulos de verdade (libshellsvc, libipmi), e num jogo elas só
  existiriam se o jogo importasse esses módulos (nenhum importa). Próximo passo sugerido para o lead: os jogos não
  passam por `LoadFirmwareDependencies` (só os programas do sistema), então uma biblioteca do firmware que o HLE não
  conhece fica "não implementada" num jogo em vez de vir do módulo real
  Feito (10/11/2026, agente B, fix/bugs-b-3): o sceSysmoduleLoadModule de um id público (tabela de ids do
  psp2/sysmodule.h e módulos como no sysmodule_paths do Vita3K) carrega o módulo do firmware quando o HLE não tem
  nenhuma função de nenhuma biblioteca que ele exporta (HleRegistry.KnowsLibrary, que conta também os HLE de reserva
  como libc e FIOS2); o que o HLE emula continua no HLE. As dependências de um módulo carregado assim (e dos de
  `LleSysmodules`, como o libult do jogo) agora também vêm do firmware num jogo, mas só as bibliotecas que o HLE não
  conhece. Pelo índice do firmware 3.74 isso vale para AACENC (libnaac), BG_APP_UTIL, IPMI, MARLIN (3),
  TELEPHONY_UTIL, DTCP_IP, VIDEO_SEARCH_EMPR, BEISOBMF, BEMP2SYS, LOCATION_EXTENSION, MAIL_API, TELEPORT_SERVER,
  APPUTIL_EXT e AVCDEC. Nenhum dos 47 jogos instalados importa essas bibliotecas (o `imports` de todos só acusa o
  SceShaccCg do Dead Space e do SH4, que é do runtime do PSM e não do firmware), então não houve jogo para provar a
  carga; sem regressão no NGS2 Plus (libult LLE, gameplay, `<scratchpad>\bB2\runs\v3_ngs2_boot`), SH: BoM
  (`v3_shbom_boot`) e nos jogos da revalidação abaixo
- [x] Câmera (Fotos, NPXS10004): um vídeo de 7 s gravado pela câmera mostra 1:09 de duração no player (relato do
  usuário, 08/11/2026, gravação `captures\shell_20261108_170201_tudo`). O MP4 gravado está certo (ffprobe 7,70 s;
  `mvhd` 90000/693120). **Corrigido** (feat/product-4): o player da câmera (avPlayer LLE) pega a duração do arquivo no
  `sceMp4StartFileStreaming` (HLE), que devolvia ticks de 90 kHz; o libscemp4 do firmware 3.74 devolve em 1/10000 s
  (duração do `mvhd` x 10000 / escala, 0x8100ccee; o `sceMp4TimeToPTS` dele usa a mesma unidade: tempo x 90000 /
  10000), daí 693120 / 10000 = 1:09. Validado com o próprio vídeo do usuário no modo câmera (headless): 00:05/00:07
  com a barra proporcional (antes 00:05/01:09; um clipe de 3,9 s mostrava 00:35). Ainda diferente do firmware, sem
  mexer (os jogos funcionam assim): o `sceMp4PTSToTime` real é pts / 90000 e o `sceMp4TimeToPTS` real é tempo x 9 (o
  HLE usa microssegundos); o `D5B26179` (layout compacto) vai para os mesmos offsets +0x28/+0x30 do layout de 0x158
  (o F1 2011 lê a duração em +0x30; o HLE escreve a do vídeo em +0x28). No headless, o modo vídeo da câmera só troca
  com os dados do `fw10_base` (com os dados do usuário o toque em 0.94, 0.58 não faz nada).

- [x] Ninja Gaiden Sigma (PCSB00097): alguns trechos ficam embaçados (relato do usuário, 08/11/2026, main f8ddeca,
  gravação `captures\PCSB00097_20261108_032607_tudo`, 322 quadros de dump da GPU, 30 fps). O usuário trocou a resolução
  interna com F1 (1x -> 2x -> 3x -> 4x) na mesma sessão: conferir com replay-gpu em 1x e na escala usada se o borrão é
  da escala (alvo reinterpretado/desfoque lido no tamanho errado, como o planeta do LBP e a luz do Resistance) ou do
  jogo (efeito de profundidade de campo); comparar com vídeo do Vita real. Validar
  **Corrigido** (perf/desktop-13; falta o usuário confirmar): não era da escala (a gravação toda está em 1x; o F1 voltou
  a 1x no quadro 317, a gravação começa no 1972) nem só o efeito do jogo. A profundidade de campo das cenas (abertura
  do capítulo 1) lê a profundidade da cena 960x544 MSAA 2X (force store) como uma textura F32 960x1088 (as duas
  amostras de cada pixel uma em cima da outra) e mistura a cópia borrada pelo fator de foco. Só o 4X tinha as amostras
  desempacotadas (`UnpackedDepthTexture`); no 2X a leitura via a memória do convidado, o fator saía "longe" em tudo e
  o personagem em foco ficava tão borrado quanto o fundo. Agora 2X (e 2X em imagem de 4 amostras, Msaa2xAs4x) também
  é desempacotado. Comparado com vídeo do Vita real (YouTube J7LRdOTELtg, 46-56 s): Ryu nítido e fundo borrado, igual.
  Replay 1x-4x iguais; rerun ao vivo de uma cópia da gravação em 1x e 2x com o Ryu nítido. Efeito colateral: os brilhos
  dos refletores do FIFA 13 (partículas que leem a profundidade 2X da própria cena) mudam um pouco atrás do menu de pausa.
  Confirmado pelo usuário (08/11/2026)
- [x] Resistance: Burning Skies (PCSA00008): "parece problema de como a resolução interna tá funcionando" (relato do
  usuário, 08/11/2026, gravação `captures\PCSA00008_20261108_014949_tudo`, F1 1x->4x->1x duas vezes).
  (1) **Corrigido** (perf/desktop-12, 7d73f91; falta o usuário confirmar): cada F1 congelava 0,6-0,9 s recompilando
  ~100 shaders e ~50 pipelines, porque o renderizador novo não abria os caches do jogo (só o primeiro recebia
  `BeginTitle`); agora F1/F3/load state passam o título adiante e o novo faz o prewarm (cenário `res_f1`: 0 shaders
  traduzidos e 0 quadros travados depois de cada troca). (2) **Aberto**: em 2x/3x/4x a luz da cena sai errada (listras
  verticais finas em 2x, tudo azulado em 3x/4x; replay do dump da gravação em cada escala, `perf12_out/
  res_m3900.png`). O G-buffer 0x64800000 (720x408 F16F16F16F16, escrito com bits crus) sai igual nas escalas; o erro é
  na leitura: o passe de luz amostra esse alvo como **uma** textura 1440x408 U8U8U8U8 com o uv do pixel da tela, o que
  cai exatamente na fronteira entre os dois texels (metades) de cada pixel (u*1440 = 2x+1). No nativo o arredondamento
  sempre pega a 2ª metade; na escala (textura reinterpretada 1440k de largura, `ScaledReinterpretGeometry`) o mesmo
  ponto de fronteira arredonda para um lado ou outro conforme a escala (listras esparsas em 2x, metade errada em quase
  tudo em 3x/4x). Ler pela cópia nativa (sem a reinterpretação escalada) piora (listras a cada pixel). Opções a decidir:
  um texelFetch com arredondamento fixo para texturas que reinterpretam alvos escalados (o gerador não sabe disso hoje),
  ou desenhar esses alvos em nativo. **Corrigido** (perf/desktop-13; falta o usuário confirmar; o trabalho
  parou em 08/11/2026 a pedido do usuário e foi retomado no mesmo dia). Causa exata: o jogo soma -0,5/1440 ao uv (centro da 1ª metade do pixel
  F16x4 no nativo: u*1440 = 2x+0,5); na escala k o mesmo deslocamento fica fixo e u*1440k = 2X+1-0,5k (borda em 2x,
  a outra metade em 3x, borda de novo em 4x). Correção genérica como o sample_depth do PCSX2 (texelFetch do texel
  nativo vezes a escala): texturas que reinterpretam um alvo com texel menor, acima do nativo, levam bits na entrada
  de formato das hints (`ShaderGenerator.RtReinterpretFlag`, só esses shaders ganham chave nova) e são lidas por
  `rtfetch_*`: o uv volta ao centro do pixel nativo do fragmento (derivadas de tela), pega o texel nativo e lê a
  cópia dele no alias escalado na sub-posição do fragmento. Replay do dump da gravação, quadro 3900: 1x/2x/3x/4x com
  a mesma luz (`perf13_out/fb_m3900.png`; antes `fa_m3900.png`), 1x idêntico ao main. `replay-gpu
  --compare` 1x e 2x: 7 dumps pequenos sem diferença até a parada (lista em `perf13_cmp/c1.txt`).
  **Validado** (08/11/2026): `--compare` 1x e 2x em todos os 18 dumps e na gravação: só a gravação do Resistance muda
  em 2x (a luz corrigida) e os brilhos dos refletores do FIFA 13 (efeito da correção do 2X MSAA do Ninja Gaiden). No
  jogo (a gravação não serve para rerun: o save state é de outro formato): cenário `rbs_continue` até a sala dos
  bombeiros e olhando para a direita, em `--res-scale` 1/2/3/4: luz igual à de 1x em todas; main em 4x no mesmo ponto
  sai com o machado e as mãos azulados. `--env=KEY=VALUE` e `--res-scale=` agora valem igual em
  run/test/boot-shell/rerun e replay-gpu (perf/desktop-14; antes `--env` só existia no replay-gpu).
  Os prefetches dos workers não conhecem os bits (calculam a chave sem eles): esses poucos shaders compilam no
  primeiro uso. (3) Os dois "flashes" depois de 4x->1x são uma variação de 0,3 de luma numa célula,
  antes da troca; não reproduzidos
  Confirmado pelo usuário (08/11/2026)
- [x] Resistance: Burning Skies (PCSA00008): às vezes trava na primeira tela de carregamento (a caveira gira para
  sempre) logo depois do boot (achado do agente, 08/11/2026). Intermitente e já existe no main (main: 1 de 8 boots;
  perf/desktop-13: 3 de 15). O jogo cria a thread 'Autoload' (quadro ~61) e nunca chega à 'FMOD nonblocking
  thread (0)' (quadro ~155 numa execução boa); nada de 3D é desenhado. Não depende da escala. Próximo passo: captura
  `--diag-at` (threads e esperas) numa execução travada e comparar com uma boa
  perf/desktop-15 (08/11/2026): **não reproduz como trava**. Com o critério antigo ("a thread 'FMOD nonblocking' não
  nasceu até o quadro 320") 1 de 12 boots frios "travou", mas com a CPU do PC a 96% (outros agentes): o carregamento só
  estava lento (329 quadros em 64 s; no fim a fila de callbacks da FIOS vazia, as threads da FIOS ociosas e a `main`
  rodando código do jogo). Com um critério de tempo (até 900 quadros/200 s, cenário com `--frames=900`): 23 boots
  frios sem nenhuma trava (13 no main, carregamento pronto entre os quadros 149 e 302; 10 com as correções desta
  rodada, quadros 137-144). O predicado de 0x820B9672 é o laço da SceFiosCallback (0x820B95EE): espera no cond
  0x820D3700 quando a fila de callbacks (anel em +0x6C do agendador 0x820D36D0, contagem em +0x1070) está vazia ou o
  contador global 0x820D48D4 > 0; com o índice de varredura igual à contagem faz uma espera de 50 ms e varre de novo.
  O monitor exclusivo foi conferido (interpretador e JIT x64: geração por grânulo de 8 bytes, STREX falha se outro
  núcleo gravou com STREX desde o LDREX, valor comparado contra gravações comuns; ABA só com gravações comuns, que não
  perde atualização de contador) e não tem defeito; os contadores da FIOS (espera de operação em op+0x70, fim em
  op+0x6C) usam LDREX/STREX com o mutex 0x820D3680 do jeito certo. Achado no caminho e corrigido: o printf a partir de
  `va_list` (sceClibVprintf e afins) lia todo `%lld`/`%llx`/double uma palavra fora quando a lista começava em 4 mod 8
  (o rastro da própria FIOS saía com "(null)" em tudo). Para diagnosticar: `--poke=820D2514:15@1` liga o rastro da
  FIOS2 (api/op/io; 2 = sched e 8 = chunk são muito verbosos). Se voltar a travar com a CPU livre, olhar a última
  operação "Accept" sem "COMPLETE" nesse rastro
  Fechado (08/11/2026, perf/desktop-15): não era trava, era carregamento lento com a CPU disputada; 23 boots frios sem travar
- [x] Ninja Gaiden Sigma 2 Plus (PCSE00233): o vídeo "trava" e a música do menu sai "com ruído" (relato do usuário,
  perf/desktop-14: 0 de 30 boots com o cache de shaders quente (10 a 1x, 10 a 1x em paralelo, 10 a 2x com dumps de
  quadro); 3 de 15 com cache frio (`--shader-cache` e cache do driver novos). Nas travadas a main fica num
  laço de espera do carregador (`DelayThread(16 ms)` em 0x8112BAEA/0x8112C680, prioridade baixada para 0xBD) que
  espera um contador de operações pendentes da FIOS (libfios2 LLE) chegar a 0, e todas as threads da FIOS
  (SceFiosCallback/IO/Libdeflt0) dormem em `sceKernelWaitLwCondCB` nos seus SceFiosSchedulerWakeup: uma operação na
  fila sem ninguém acordado (wakeup perdido). Numa delas a trava foi antes do primeiro quadro (quadro 0), na outra com
  a LoadScr ainda desenhando. O traço das chamadas de sincronização (`--trace-hle-only=` lwcond/sema/evf) mostra
  cada Signal com o waiter já em espera, menos o último: o SceFiosLibdeflt0 sinaliza o 0x820D3700 enquanto a
  SceFiosCallback está entre o seu Signal(0x820D007C) e o próximo Wait(0x820D3700). Lw mutexes e conds conferidos
  (Sync.cs: Wait/Signal atômicos sob o lock do cond; IO assíncrono é síncrono). Com o traço dos lw mutexes também
  (1 travada em 10 boots frios, `rbs_h3`): o SceFiosLibdeflt0 faz Lock(0x820D36D8), Signal(0x820D3700),
  Unlock(0x820D36D8) e dorme em 0x820D007C; só depois a SceFiosCallback faz Lock(0x820D36D8), confere o estado e mesmo
  assim dorme em Wait(0x820D3700); a main fica em SceFiosOpWait (0x820D36A8) esperando a operação. Ou seja, o sinal
  não se perdeu no cond: a Callback leu o estado depois do aviso e não viu o trabalho pronto. Suspeita seguinte: o
  estado é um contador da FIOS atualizado com LDREX/STREX fora desse mutex (perda de atualização no monitor exclusivo
  entre núcleos, `X64Emitter.StoreExclusive`/`Arm.StoreExclusive32`), ou a cópia do valor que a Callback leu; próximo
  passo: desmontar o predicado da SceFiosCallback em 0x820B9672 e vigiar (`--watch`) o endereço que ela lê
- [x] Ninja Gaiden Sigma 2 Plus (PCSE00233): o vídeo "trava" e a música do menu sai "com ruído" (relato do usuário,
  07/11/2026; gravações `captures\shell_20261107_214909_tudo` e `..._214953_tudo`, jogo aberto pelo shell).
  Vídeo (agente C, fix/bugs-games-17): o filme da abertura (player de vídeo do jogo, SceMp4 + sceAvcdec) com d3d11va
  tem 44 flashes e 4 pares de quadros trocados em 23 s; em software (o novo padrão `auto`, db00236) 10 flashes e 2 pares
  (`bugsC3_data/captures/PCSE00233_20261107_215142_tudo` x `..._215323_tudo`, 30 fps nos dois). Melhorou, mas sobram
  flashes de célula e 2 pares trocados: conferir se são do próprio filme (cenas rápidas) ou ordem de quadros do player.
  Música: a voz NGS 0x822A5F40 (player PCM estéreo 48 kHz em dois buffers de 512 quadros, um tick cada, em pingue-pongue)
  toca buffers que o jogo entrega zerados: ~40% dos grãos são silêncio exato (padrão `_#_##`), o que o usuário ouve
  como ruído. Trace (`VITASHARP_NGS_TRACE=1`, `bugsC17_out/ngm2`): o NGS lê o buffer certo a cada tick e o callback de
  troca chega a cada tick; o conteúdo é que vem zerado (ex. ticks 1511 e 1514). O produtor parece ser o
  "ATStreamEngine"/"ATStreamPlayer" do jogo (prio 64, núcleo 1, junto com ArmSpurs e TFW workers), que não acompanha.
  Tentativa sem efeito claro (revertida, patch `bugsC17_ngs_eagerswap_wip.patch`): trocar de buffer no
  mesmo tick em que ele acaba (o hardware avisa um tick antes): 39% contra 43% de silêncio, dentro da variação.
  Próximo passo: medir quanto tempo de CPU o ATStreamEngine recebe por segundo (o decoder de música do jogo pode estar
  sem tempo de CPU no núcleo 1, ou esperando um evento/semáforo que chega tarde) e comparar com o Vita3K
  Música corrigida (07/11/2026, agente C, fix/bugs-games-18, 352d66f): não era o produtor. O jogo mistura a música
  dentro do callback de troca de buffer de cada tick do NGS (thread de áudio sem nome, prio 64): a cópia vem de um
  buffer de mistura de 512 quadros (0x822EB800) que o mixer do jogo enche a cada tick. O OpenAL Soft devolve os buffers
  por atualização de mixagem (960 quadros = 2 grãos de 512), então o `sceAudioOutOutput` do jogo voltava duas vezes
  seguidas, dois ticks rodavam sem intervalo e o segundo não tinha dado novo (padrão `_#_#_##`). O headless não
  reproduz (o ritmo é pelo relógio); com o OpenAL de verdade sem som (driver `wave` do OpenAL Soft via `ALSOFT_CONF`) dá
  34%. Correção como o `audio_output` do Vita3K: quando um grão chega menos de um grão depois do anterior, espera
  metade do resto (`VITASHARP_AUDIO_SPACING`, 0 desliga). Mesmo binário, 75 s: desligado 29,6% de grãos zerados,
  65,6 saídas/s e 112 underruns; ligado 0,5%, 93,6/s e 9 (`bugsC18_out/ab_off_audio`, `ab_on_audio`). Falta o vídeo
  Vídeo corrigido (2f7518c): o filme da abertura (`databin/MOVIE/NINJA_VI.MP4`, H.264 com B-frames) saía fora de
  ordem. O decodificador FFmpeg era aberto com `AV_CODEC_FLAG_LOW_DELAY`, que entrega cada quadro assim que decodifica
  (ordem de decodificação), e o jogo mostra os quadros do sceAvcdec na ordem em que chegam: a cada três quadros um
  voltava dois. Agora sai na ordem de exibição, como o Vita3K (sem o flag; `VITASHARP_VIDEO_LOW_DELAY=1` volta ao
  antigo). Medido contra o filme decodificado pelo ffmpeg de linha de comando (cada present casado com o quadro do
  filme, `bugsC18_order.py`), 700 presents a partir do 300: low-delay 62 voltas para trás; sem ele 1 (no
  início) e 431 passos de +1 (`bugsC3_data/captures/PCSE00233_20261107_234018_tudo` x `..._234117_tudo`). Os
  "flashes" que restam no analisador são relâmpagos do próprio filme
  Pista do usuário (08/11/2026): a abertura do Ninja Gaiden Sigma 1 (PCSB00097) vai fluida e a do Sigma 2 engasga, no mesmo build. Comparar os dois caminhos lado a lado (player usado: avPlayer x sceAvcdec, fps do filme x vblank, buffers/anel, ritmo de entrega dos quadros e de troca do framebuffer, áudio do filme como relógio) e achar o que o Sigma 2 faz diferente; os agentes não acharam defeito olhando só o Sigma 2
  Vídeo corrigido de verdade (08/11/2026, agente C, fix/bugs-games-20): casando cada present com o NINJA_VI.MP4
  decodificado pelo ffmpeg (miniaturas 120x68, média por bloco, sem a média de brilho), a gravação do usuário
  (captures\PCSE00233_20261108_015357_tudo, build cb284cf, já com o 2f7518c) mostra o filme inteiro na ordem
  n+1, n+2, n (103 voltas de -2 em 411 presents), e não só nos B-frames. O decodificador estava certo (um quadro por
  chamada, pts crescentes, anel de 3 buffers); o defeito era o histórico de quadros do decodificador
  (Gpu/DecoderOutputs.cs, da 57c57b1 do Jak): o primeiro quadro do buffer 0x90500000 nunca é amostrado pelo jogo,
  fica marcado como não usado, e daí em diante cada upload desse buffer pegava o quadro anterior (o 'mais antigo não
  usado'), um ciclo atrasado para sempre. Com VITASHARP_DECODER_HISTORY=0 some (264/264 passos de +1). Correção: um
  quadro decodificado antes de outro do mesmo tamanho que um upload já pegou (em qualquer endereço) foi pulado pelo
  jogo e não é mais mostrado depois. Medido com boot ao vivo sem entrada (--capture-at=250:860), 5 execuções do build
  final: 0 voltas para trás (325/325, 324/324, 304 com o PC carregado), contra 85-105 sem a correção
  (bugsC3_data/captures/PCSE00233_20261108_1342* a ..._134743_tudo); teste DecoderOutputsTests. A diferença para o
  Sigma 1: ele não deixa quadro sem amostrar no anel
  Confirmado pelo usuário (08/11/2026)

  08/11/2026 (agente C, fix/bugs-games-19, só achados): nova gravação do usuário com o 2f7518c
  (`captures\PCSE00233_20261108_015357_tudo`, 30 fps cravados, 3 "trocas" e ~10 flashes). O `rerun` dela derruba o
  emulador logo depois de carregar o estado, também com o build exato do usuário (cb284cf): AccessViolation em
  `T16.LdStSp` num thread guest (bug de load-state à parte, aberto). Reproduzido ao vivo (boot sem entrada,
  `bugsC3_data/captures/PCSE00233_20261108_022130_tudo`: mesmas trocas 364/365, 381/382, 519/520). O caminho do vídeo
  está em ordem: um sceAvcdecDecode por quadro, pts de saída crescentes de 3003 em 3003, anel de 3 buffers, e a textura
  que cada present amostra (trace-draws do dump nos presents 360-368, 379-383, 517-521) é o quadro N-244 do
  NINJA_VI.MP4 decodificado pelo ffmpeg (erro 2-10 contra 50-70 dos vizinhos); os framebuffers alternam em ordem.
  As trocas/flashes do analisador caem nos relâmpagos do próprio filme (quadros-fonte vizinhos diferem 50-70):
  falso positivo. Filme de 29,97 fps a 1 quadro por vblank de 30 Hz. Falta o usuário apontar o trecho que engasga
  (ou comparar com um vídeo do Vita real)

  08/11/2026 (agente C, fix/bugs-games-20, interrompido pelo usuário antes de começar): o crash do `rerun` dessa
  gravação depois de carregar o estado (AccessViolation em `T16.LdStSp`) segue aberto, sem investigação nova. Próximo
  passo: rodar o `rerun` (cópia em `bugsC3_data`) com o log do thread que cai, comparar registradores/pilha do thread
  com `threads_start.txt` e ver se o código traduzido (cache do JIT) ou um mapeamento de memória não foi refeito na
  carga. Fila da rodada 20 ainda não iniciada: NFS Most Wanted no diálogo Origin, fps do shell main x f0e692f com o PC
  livre, filme do LBP com `--video-decoder=hw`, rosto lento do Soccer Tsuku (PCSG00198)
  Crash do load-state corrigido (08/11/2026, agente C): o thread 'main' do Sigma 2 roda fibers (SceFiber HLE) e o
  contexto de fiber restaurado de um save state vinha sem a parte do host do CpuState (MemBase nulo); o sceFiberRun /
  Switch copiava o contexto inteiro por cima do estado do thread e o primeiro acesso à memória caía (AccessViolation em
  T16.LdStSp, MemBase 0x0 no log de diagnóstico). Agora a troca de fiber só troca os registradores do guest. Sem a
  correção 3/3 reruns caem; com ela 0 em 5 (reruns de bugsC3_data/captures/PCSE00233_20261108_142546_tudo 2x e
  ..._150404_tudo, e --load-state direto). Junto: o decodificador H.264 recriado na carga recebe de novo o SPS/PPS
  do filme (antes: 
on-existing PPS 0 e nenhum quadro). Ainda aberto: depois da carga no meio do filme da abertura
  a imagem fica parada ~3,5 s e o filme pula para o fim (..._142721_tudo); o player do jogo não se recupera do
  demuxer reaberto no quadro-chave. Agente B (08/11/2026, fix/bugs-b-1, não validado): o sceAvcdec guarda no estado
  as unidades desde o último IDR e as decodifica de novo no decodificador recriado (descartando por pts as imagens já
  mostradas), e o SceMp4 volta logo depois da última unidade lida em vez do quadro-chave. Do estado salvo no meio do
  filme: antes a imagem parava ~3,5 s; com isso o filme segue certo ~1,4 s (casando quadro a quadro com a execução sem
  carga) e depois o 'avPlayer VideoDec' do jogo para de decodificar (f168) enquanto o demux lê só áudio, imagem parada ~2,4 s
  e salto. No NFS (filme de abertura, mesmo tipo de player) a imagem fica parada depois da carga com ou sem isso.
  Falta achar o que o player do jogo espera depois da carga (provável: relógio/estado de áudio do filme)
  99Vidas (PCSE00914, pedido do lead): o travamento depois de Load state: GPU
  backend replaced não reproduziu em 9 cargas com este build (headless, janela oculta, 6 cargas seguidas); o build
  do agente de produto (desk) recusa o save em f4000 porque um diálogo Msg do sistema está aberto, então o
  estado que travou foi salvo com esse diálogo aberto
- [ ] FIFA 13 (PCSE00093): "algumas coisas parecem estranhas" (relato do usuário, 07/11/2026, main 90b55bc; três
  gravações: `captures\PCSE00093_20261107_030833_tudo` (4,3 s), `..._031028_tudo` (5,2 s) e `..._032507_tudo` (8,4 s),
  todas a 60 fps). O analisador só acha paradas de 112-212 ms (a primeira de cada é o estado salvo do início da gravação)
  e nenhum flash ou tela preta, então o defeito é algo que ele não mede (textura, modelo, animação, cor, som). Analisar
  quadro a quadro os frames ao vivo (`vitasharp-perf capture --frames`) e o áudio de cada gravação contra um vídeo do
  Vita real do mesmo trecho, e perguntar ao usuário o que viu em cada uma antes de corrigir
  O que o usuário viu em cada uma: 030833 = o áudio da torcida; 031028 = um problema visual que impediu de cobrar o
  escanteio: algo na frente tapava a visão da cobrança (um objeto/camada desenhado onde não devia: placa, torcida, HUD,
  máscara ou camada de efeito na frente da câmera do escanteio; achar o desenho com `--draw-limit` no dump da gravação e
  comparar com um vídeo do Vita da mesma câmera); 032507 = o vídeo de abertura (não sabe se é
  assim no Vita: comparar com um vídeo do Vita real). Validar assim que o limite semanal renovar
  Achados (07/11/2026, agente C, fix/bugs-games-17): 032507 = a abertura (bola da EA Sports, jogador de bicicleta,
  bola na rede) bate com o vídeo do Vita real (`NfnIFRme17c`, `bugsC17_yt/fifa_sheet.png`) e não tem
  flash nem quadro trocado a 60 fps (`bugsC17_fifa/sheet032507.png`). 031028 = os 295 quadros ao vivo são só a
  comemoração do gol ("22' Goal 7 DANILINHO", câmera atrás do escanteio, `bugsC17_fifa/sheet031028.png`): não há
  cobrança de escanteio na gravação e nada fora do normal além da faixa do gol; precisa de uma gravação do momento da
  cobrança (ou o usuário dizer o que tapava). 030833 = as vozes da torcida não têm cliques, lacunas nem grãos repetidos
  (`vitasharp-perf audio-check`); a voz 03 (0x89A424A0) bate em 0 dBFS (0,03% de clipping) e todas cortam acima de
  ~12 kHz; sem referência do Vita no mesmo trecho não dá para dizer o que soa estranho
  Gravação nova da torcida (`captures\PCSE00093_20261107_223859_tudo`, agente C, fix/bugs-games-18): rerun com o
  OpenAL de verdade (driver wave) sem e com o 352d66f: o port 1 tinha 2 buracos (97 ms) sem o espaçamento e nenhum com
  ele; vozes ATRAC9 com 0 superframes velhos. A voz 08 (0x89A44060, ATRAC9 mono em anel) é um tom forte de ~3 kHz
  (provavelmente o apito): os "saltos" são a própria onda (amplitude ~30000), não descontinuidade. No fim de cada clipe
  o jogo grava superframes zerados no anel; o LibAtrac9 (o nosso decodificador) e o FFmpeg (o do Vita3K) rejeitam o
  segundo frame (FirstInSuperframe) e o Vita3K também chama DECODE_ERROR: o mesmo que fazemos desde o 7732c69 (toca
  silêncio, conta os bytes; o jogo mata a voz no fim do clipe). Sem mudança
  Áudio da torcida confirmado ok pelo usuário (07/11/2026, depois do espaçamento dos grãos 352d66f); segue aberto o escanteio
- [x] Mais ~1000 funções HLE implementadas de verdade (pedido do usuário, 07/11/2026; começar na próxima rodada de
  agentes, além das que já estão na lista das não implementadas). Hoje: ~2.840 funções com comportamento no VitaSharp
  contra ~2.626 no Vita3K (das 7.380 que ele declara, 4.630 respondem UNIMPLEMENTED). Cada função com o comportamento
  do Vita: lógica de verdade quando a função faz algo, e retorno vazio/de erro só quando é isso que o console faz
  (sem rede/PSN/hardware ausente = o erro que o Vita devolve nessas condições); nunca stub genérico que devolve 0 sem
  pensar. Fonte da lista: `docs/hle/data/vitasdk-user-functions.tsv` (7.006 funções user-mode do VitaSDK) menos as
  implementadas, nesta ordem: (1) as importadas pelos jogos instalados (`VitaSharp.Desktop imports <TITLEID>`) e as que
  aparecem nos logs; (2) as que o Vita3K implementa de verdade e nós não; (3) as bibliotecas que os jogos de Vita mais usam
  (Gxm, Ngs, AppUtil, CommonDialog, Np, Net, Audio, Ctrl/Touch/Motion, Fios2, Sas, Atrac, Avplayer...); (4) o resto
  por biblioteca. Referências: firmware 3.74 desmontado (agente B deixou ELFs e scripts) e o Vita3K. Cada lote com teste
  unitário quando der, um jogo/app que chama a função seguindo sem regressão, e o item marcado com a contagem; meta:
  ~3.840 com comportamento real
  - Lote 3 (feat/hle-batch-3, 07/11/2026): 3.173 -> 3.512 NIDs com implementação (3.017 -> 3.356 das 7.006 do VitaSDK;
    contagem pelo novo comando `VitaSharp.Desktop hle-implemented --vitasdk=docs/hle/data/vitasdk-user-functions.tsv`,
    que gera `docs/hle/data/vitasharp-implemented.tsv`; o "~2.840" acima vinha de outra contagem). Feitas: os syscalls
    `_sceKernel*`/`_sceIo*`/`_sceRtc*`/`_sceDisplay*`/módulos por trás dos wrappers do libkernel/driver_us 3.74 (argumentos
    empacotados no opt como o wrapper passa), SceRtabi (divisão por zero saturando como o libkernel), SceDmacmgr,
    sceKernelGetEventInfo/CancelEventWithSetPattern, SceNgsInternal (o libngs.suprx repassa com os mesmos argumentos;
    IsFixForBugzilla89940 pela versão do SDK), syscalls do SceAvcodec (decodificadores de áudio/vídeo, encoder de áudio,
    MJPEG) e as formas Internal/Nongameapp do Videodec, ScePspnetAdhoc (ctlInit/Term/GetEtherAddr e sockets PDP sem
    rede ad hoc, erros do pspnet_adhoc.suprx), sceNpCommerce2GetShortfallOf*Pool, sceCoredumpWriteUserData,
    sceAppUtilCache*, as funções cujo corpo no firmware 3.74 é um retorno constante (SceHid controle, SceBt OPP/HFP,
    SceMotionDev formas 2/3, SceBbmc debug, sceSslInternalIsInitalized) e os syscalls que o driver_us só repassa
    (SceAppMgr, SceError, overlays do SceFios2Kernel02). Validado sem regressão (boot e menu, 75-90 s headless, sem
    "Unimplemented" nem crash) em PCSA00152, PCSE00491 (ad hoc), PCSA00034 (NpCommerce2) e PCSF00476 (coredump); as
    telas de multijogador ad hoc e loja não foram exercitadas. Método que rende: os ELFs do firmware 3.74 em
    `os0:kd`/`os0:us`/`vs0:sys/external` (self2elf) mostram o corpo de cada NID; os wrappers que só repassam
    (`push; blx; pop`) e os corpos que só devolvem constante viram implementação exata. Falta (os jogos importam):
    SceNearUtil NID_46604AA3/79A4283C/CCC99AF1/D22CB2DE/8E47CFDC/9044B509/DDEB6C8C (libSceNearUtil.suprx: estado do
    serviço Near; o NearUtil HLE de hoje devolve códigos genéricos, não os 0x801049xx do firmware), SceMp4 NID_C05DFF01
    (recebe o ponteiro do objeto do libscemp4, que o HLE troca por número) e SceNpManager NID_FFBFC540 (o tratador
    0xB0025 não apareceu como constante no shell.self; o despacho do servidor IPMI fica por tabela)
  Lote 3 (+339) confirmado pelo usuário (07/11/2026); a meta de ~1000 continua (faltam ~660): próximo lote nas funções que o shell, os apps do sistema e os jogos importam (VshBridge, AppMgr, SceShellSvc, NearUtil, Mp4, NpManager...), mesmo as do lado do sistema
  - Lote 4 (feat/hle-batch-4, 08/11/2026): 3.514 -> 5.125 NIDs com implementação (+1.611; 3.356 -> 4.150 das 7.006 do
    VitaSDK; mesma contagem `hle-implemented`). Novo comando `VitaSharp.Desktop hle-coverage [--out=<tsv>]`: lê os 359
    módulos do firmware instalado (os0/vs0, inclusive os de dentro do bootimage.skprx) e os imports dos jogos
    instalados e dá implementadas/total de tudo o que o firmware exporta para o código de usuário. Antes -> depois:
    geral 3.465/19.406 (17,9%) -> 5.076 (26,2%); importadas pelos jogos 1.151/1.518 (75,8%) -> 1.469 (96,8%); pelo
    sistema 1.172/7.539 (15,5%) -> 1.323 (17,5%); o resto 1.142/10.349 (11,0%) -> 2.284 (22,1%). Feitas: SceLibc
    inteira (350: strings, ctype, strto*/_Sto*, heap/mspace, stdio sobre os descritores sceIo, printf/scanf
    estreito/largo/Annex K, multibyte UTF-8/UTF-16, time, exit/atexit, guardas C++, setjmp/longjmp, TLS de módulo)
    com os detalhes do libc.suprx 3.74 (errno no TLS 0x88, textos do strerror, tratador de restrição que imprime e
    devolve ERANGE, o rand LCG de 64 bits); SceLibm inteira (201, ABI hard-float); SceFios2 (160: handles com a
    codificação do módulo, ops que terminam ao serem emitidas, PSARC zlib, códigos da tabela do libfios2.suprx);
    42 do SceLibstdcxx (new/delete, std::_Mutex, atômicos, type_info, what(), _Syserror_map); SceNearUtil com os
    códigos 0x801049xx do firmware e os 7 NIDs sem nome; 845 funções cujo corpo no firmware é um retorno constante,
    vazio (bx lr) ou o endereço de um texto constante (paf, WebKit, VideoProfiler...; os que devolvem ponteiro para
    dados do módulo ficaram de fora). Os módulos que só substituem código do firmware (Libc, Libm, Fios2,
    FirmwareConstants) têm `HleModule.FirmwareFallbackOnly`: não contam para `ImplementsLibrary`, então os programas
    do sistema continuam pegando essas bibliotecas do firmware como antes (shell: mesmos módulos e dependências LLE).
    `VITASHARP_HLE_MODULES=libc,libfios2` agora também deixa no HLE os preloads que o jogo traz. Validado: shell e 4
    jogos (PCSE00491, PCSA00152, PCSF00476, PCSB00245) sem regressão contra a base (mesmos módulos, 0
    "Unimplemented", 0 crash, quadros iguais dentro do ruído); Minecraft (PCSE00491) com libc e FIOS2 do HLE
    inicializa o FIOS2, lê os dados (2.608 sceFiosFHReadSync), cria o Gxm e apresenta o 1º quadro, e para no
    iostream do C++ (ios_base/_Winit precisam dos dados do libstdc++ do módulo). Falta: o resto do SceLibstdcxx
    (exceções, iostreams, locale: precisa das vtables/dados do módulo), SceMp4 NID_C05DFF01 e SceNpManager
    NID_FFBFC540 (IPC 0xB0025 de 64 bytes ao servidor NP do shell, sem ponte no VitaSharp), o código de falha do
    sceNearInitialize depois das checagens (precisa do app Near), e os syscalls do lado do sistema (SceAppMgr 108,
    SceVshBridge 92, SceAvcodec 82, SceBbmc 48... do bootimage)
  - Lote 5 (feat/hle-batch-5, 08/11/2026). `hle-coverage` antes -> depois: geral 5.076/19.406 (26,2%) -> 5.383 (27,7%);
    importadas pelos jogos 1.469/1.518 (96,8%) -> 1.514 (99,7%); pelo sistema 1.323/7.539 (17,5%) -> 1.398 (18,5%); o
    resto 2.284/10.349 (22,1%) -> 2.471 (23,9%). Feito: os 6 syscalls `_sceIo{Remove,Mkdir,Rmdir,Rename,Getstat,Sync}Async`
    recebem o SceIoAsyncParam no registrador depois dos argumentos (o libkernel 3.74 repassa; antes ia 0); as exceções
    C++ do libc.suprx 3.74 para processos com a libc do HLE (unwinder ARM EHABI dos módulos carregados, pr0/1/2 com
    descritores ARM, `__snc_personality_v0` com LSDA no formato GCC, `__cxa_*` com o cabeçalho de 0x80 bytes do
    módulo, casamento de tipos, classes de exceção com vtables, `_X*`), as 418 RTTI que o SceLibstdcxx exporta como
    variáveis (tabela gerada dos dados de exportação do módulo, `Libc.Rtti.g.cs`), `ios_base::_Init/clear/~ios_base/
    _Addstd`, manipuladores, `ios_base::failure` com a categoria iostream, `_Fiopen`, atômicos de 1/2/8 bytes e
    genéricos, pedaços de `std::string`; SceLibGcc (`_Unwind_*` do libkernel) sobre o mesmo unwinder; 37 syscalls
    `_sceAppMgr*` e 36 do SceAvcodec (Avcenc/Videoenc/Jpeg) atrás dos wrappers do driver_us/avcodec_us (struct de
    rascunho no último argumento); `VITASHARP_HLE_CALLS=1` lista toda função HLE chamada. Validado: Minecraft
    (PCSE00491) com `VITASHARP_HLE_MODULES=libc` 90 s chamando `ios_base::_Init`/`~ios_base` 1.737x, `clear` 2.270x,
    0 crash; modo normal sem regressão em PCSA00080, PCSB01073, PCSE00606, PCSE00781 e no shell (custo por quadro igual, 5,8 ms; Skullgirls A/B contra
    main: 54,1/53,9 vs 54,0/53,2 fps). Os syscalls `_sceAppMgr*`/SceAvcodec/IO só têm teste de unidade: nenhum
    programa instalado os chama (os wrappers rodam no HLE).
  Superado (08/11/2026): lotes 3-9 somaram mais de 3.600 funções; a meta atual é o sistema em 3.000 (hle-coverage)
- [x] HLE lote 6: iostreams e locale clássico do libc.suprx 3.74 (os 4 imports de jogo que faltam: `ios_base::Init`
  C1/D1 e `_Winit` C1/D1). O Init (0x8102BD34 no libc.suprx 3.74, contador em 0x8105355C sob o lock 0) constrói, na
  primeira chamada, 3 `basic_filebuf<char>` de 0x90 bytes sobre stdin/stdout/stderr (vtable de basic_streambuf em
  0x8104F554, de basic_filebuf em 0x8104F5C4: overflow, underflow, uflow, pbackfail, xsputn, xsgetn, sync, setbuf,
  seekoff/seekpos, imbue, showmanyc) e cin/cout/cerr/clog (istream/ostream com basic_ios, `_Addstd`, `tie`); o locale
  clássico (`_Locimp`, `_Locinfo`, facetas ctype/numpunct/num_put/num_get/codecvt com vtables) começa em 0x81038DD8;
  `_Winit` (0x81031BA0) faz o mesmo para wchar_t; os D1 (0x8102C35A / 0x810321AC) dão flush nos 3 streams na última
  referência. Já existe: a tabela RTTI e o construtor de vtables com slots H (funções não exportadas) em
  `Libc.Exceptions.cs`, os helpers de `std::string`, os campos de `ios_base` em `Libc.Ios.cs`. Validar com Minecraft
  em modo libc do HLE chegando ao menu e ao jogo; também os `_Mtx_*/_Cnd_*/_Thrd_*` (31, xthreads sobre lwmutex/lwcond)
  e os syscalls do SceVshBridge/SceBbmc (corpos no bootimage.skprx).
  - Passo 1 (feat/hle-batch-6, 08/11/2026): os jogos chegam a 1518/1518. O SceLibstdcxx do libc.suprx 3.74 é a biblioteca "abridged" da Dinkumware: não tem std::locale (basic_ios sem _Ploc; as facetas são _ctype<E> e _codecvt<E, char, _Mbstatet> embutidas), então não há locale clássico a reverter (0x81038DD8 é ~basic_ios). Feito (Libc.Streams/Iostreams/Strstream.cs): basic_streambuf e basic_filebuf<char/wchar_t> (overflow/underflow/uflow/pbackfail/xsgetn/xsputn/seek/setbuf/sync/_Endwrite, conversão UTF-8 do wchar_t pelo codecvt), _ctype/_codecvt, ios_base (_Findarr, register_callback, _Callfns, _Tidy, copyfmt), basic_ios/istream/ostream/iostream (destrutores, thunks, ostream(sb, isstd)), ios_base::Init e _Winit (filebufs sobre stdin/stdout/stderr, cin/cout/cerr/clog com tie e unitbuf, contagens -1 como variáveis), strstreambuf/istrstream/ostrstream/strstream, e as variáveis de dados que os jogos importam no modo libc do HLE (cin..wclog, _Ctype, _Tolotab, _Touptab, _Stdin/_Stdout/_Stderr, _BADOFF, _Fpz). O filebuf não aponta para o buffer do FILE (o FILE do HLE não tem buffer): cada caractere passa por overflow/uflow. Validado: Minecraft (PCSE00491) com VITASHARP_HLE_MODULES=libc chama ios_base::Init/_Winit 1189x, chega ao menu principal, termos, aviso de salvamento, diálogo PSN e placares, 0 "Unimplemented", 0 crash, 0 variáveis sem resolver (antes 8). Próximo: sistema (xthreads _Mtx/_Cnd/_Thrd e _Pad já escritos em rascunho, não commitados), depois VitaSDK e o resto.
  Feito (08/11/2026): jogos instalados em 100% das importações (lote 6, be7af8e)
- [x] LittleBigPlanet (PCSA00017): o filme de abertura sai "zuado" (relato do usuário, 07/11/2026, main 90b55bc, gravação
  - Lote 7 (feat/hle-batch-7-clean, 08/11/2026): o lado do sistema, escrito à mão a partir do firmware 3.74 e dos headers do VitaSDK. `hle-coverage --vitasdk=...` antes -> depois: geral 5.540/19.406 (28,5%) -> 6.038 (31,1%); jogos 1.518/1.518 (100%); sistema 1.399/7.539 (18,6%) -> 1.842 (24,4%); o resto 2.623/10.349 (25,3%) -> 2.678 (25,9%); VitaSDK 4.599/7.006 (65,6%) -> 5.088 (72,6%). Feito: o resto do SceVshBridge (92, com as checagens de cópia/tamanho do bridge: flags QA, console id, NVS, modo IDU, CAF dos relatórios de crash, assinaturas de eboot, relógios seguro/de rede, viewport, montagens) e do SceAppMgr (72 + 28 wrappers do driver_us: QuitApp/DestroyOtherApp, listas de pid/app id do shell, pedidos de notificação na fila do shell, eventos de sistema na fila do app, SendParam, dono exclusivo do som/btrm, arquivos de dados do sistema, montagens que o emulador não precisa com a resposta do kernel); SceAVConfig (25: HDMI/volume mestre = 0x803A0109 no portátil, volume do sistema no registro), SceBbmc (48: sem modem 3G, 0x80330003), Videodec/M4vdec (68: decodificação dividida, NAL AU, modos, CSC, MPEG-4 pelo FFmpeg), syscalls do SceNetPs (27, blocos como o libnet.suprx empacota), ScePafStdc (72, regras do libpaf), xthreads do libc.suprx (_Mtx/_Cnd/_Thrd/_Pad, rascunho do lote 6 terminado), formas syscall/Internal de funções que o HLE já tinha. Uma tentativa de traduzir mecanicamente o código Thumb do firmware para C# foi descartada (cópia derivada do código da Sony num repositório público, e não é implementação): nada gerado do firmware entra. Validado: shell (Amigos, Troféus, Câmera) e 10 apps do sistema abertos direto, mesmos resultados da base (0 "Unimplemented", mesmas quedas dos apps abertos sem o shell); chamadas ao vivo: sceAppMgrReceiveNotificationRequestForShell, sceAVConfigGetSystemVol, vshSysconIsIduMode; as outras novas só têm teste de unidade (HleBatch7Tests; nenhum fluxo exercitado as chama). Falta (sistema): SceSblGcAuthMgr (59, cripto do cartão de jogo), SceCompat (36, emulador de PSP), SceSblSsUpdateMgr (24), NGS Internal (12), Threadmgr (9), Motion Ext (8), DrmBridge (9), libpaf/WebKit/email (NIDs sem nome, C++ com dados do módulo), NIDs sem nome do NetCtl/NpManager/NpCommerce2 (IPC ao shell). A meta de ~1000 por lote não foi atingida neste (+498 no geral).
  - Lote 8 (feat/hle-batch-8, 08/11/2026), parte 1: as classes do libpaf que se sustentam sozinhas, escritas à mão a partir do comportamento do libpaf.suprx 3.74 (layouts e regras lidos na desmontagem) e dos nomes da lista de NIDs do projeto comunitário ScePaf-RE (nomes C++ dos programas de teste dos protótipos; NID = SHA-1 do nome + "PAF"; só os nomes entram, nenhum código: 3.994 nomes novos no `nids.txt`). `hle-coverage --vitasdk=...` antes -> depois: geral 6.114/19.406 (31,5%) -> 6.677 (34,4%); jogos 1.552/1.552 (100%); sistema 1.834/7.516 (24,4%) -> 2.134 (28,4%); o resto 2.728/10.338 (26,4%) -> 2.991 (28,9%); VitaSDK 5.168/7.006 (73,8%) -> 5.227 (74,6%). Feito (módulo `Paf`, só reserva do firmware: um programa do sistema carrega o libpaf de verdade): paf::math::rect (centro/tamanho, bordas, hit/contém), paf::rtc e paf::datetime::DateTime (ticks sobre o SceRtc, o parser de datas do SQLite com os erros 0x8025108x, RFC 3339, dia da semana), paf::common (UTF-8/UTF-16 em paf::string, FromString, FormatString, path Split/SplitExt/Join, RefObj, common::String), LocalFile/MemFile/Dir (tabela virtual de HLE com a ordem da interface File, async imediato com WaitAsync, Dir recursivo), paf::thread (Mutex/RMutex/Cond/Semaphore/RWLock/Thread/CallQueue/SyncCall/ThreadIDCache sobre o kernel do HLE), SHA-1 (RFC 3174 com o contexto na memória do guest), zlib (sceZlibDecompress, compress::Decompress), HeapAllocator (mspace do SceClib) e os buffers (Malloc/HeapAlloc/Sub/SubConst/UnmanagedConst em SharedPtr), cxml (Document/Element/Attribute/File e o util::*), Url, as curvas de Timer::DoFunc*, o estado do inputdevice (pad/touch/motion), os acessores de membro de image::Image/JobItem/Framework/ScrollViewBase/ListView/SurfaceBase e os nomes sem `private` da ScePafStdc. Validação: testes de unidade (HleBatch8Tests: rect, datas, strings/caminhos, SHA-1 dos vetores do RFC, zlib, heap, LocalFile/Dir num ux0 de teste, cxml montado à mão, Url, curvas); nada disso é chamado ao vivo (o libpaf é LLE nos apps do sistema); shell (Amigos, Troféus, Câmera) e 10 apps do sistema sem cabeça: mesmos resultados da base do lote 7 (0 "Unimplemented", mesmas quedas e o mesmo timeout do NPXS10026). Não feito neste lote: o lado NP (SceNpCommerce2/NpManager/NetCtl/NpCommonPs4: as exportações sem nome são clientes IPC do servidor do shell, sem fonte de nomes; o NID_FFBFC540 continua sem ponte), SceSqliteVsh/dbutil (NIDs da vsh sem nome; a comparação de código com o SceSqlite nomeado não deu pares confiáveis), e o grosso do libpaf (ui::Widget e família, graph, image, Plugin, Framework, job, cache: dependem do motor de widgets/GPU do próprio libpaf). Meta de 3.000 no sistema não atingida (+300). Próximo: com o Ghidra + VitaLoader instalados (só estudo), ler as exportações sem nome do NP/sqlite e o motor de widgets do libpaf.
- [x] LittleBigPlanet (PCSA00017): o filme de abertura sai "zuado" (relato do usuário, 07/11/2026, main 90b55bc, gravação
  - Lote 9 (feat/hle-batch-9, 08/11/2026): o lado NP do software do sistema, o SQLite da vsh e mais membros do libpaf, com os módulos do firmware 3.74 descompilados no Ghidra + VitaLoader (só estudo; o contrato de cada exportação lido no módulo e escrito à mão em C#, nada do código descompilado no repositório). `hle-coverage --vitasdk=...` antes -> depois (com os 155 analisadores do NpCommerce2 do agente auxiliar hle9b, já na main): geral 6.677/19.406 (34,4%) -> 7.389 (38,1%); jogos 1.559/1.559 (100%); sistema 2.130/7.512 (28,4%) -> 2.820 (37,5%; +535 deste ramo, +155 do hle9b); o resto 2.988 -> 3.010; VitaSDK 5.227/7.006 (74,6%). Feito: `ShellSvcIpc` (o pedido que libnetctl/np_manager fazem ao serviço do shell pelo objeto de serviço do libshellsvc: método síncrono, entradas {ptr, tamanho}, saídas {ptr, tamanho, escrito}, palavra de resultado; o HLE repassa o mesmo pedido ao serviço de verdade do shell; sem libshellsvc no processo, "não inicializado"); SceNetCtl (62: os pedidos 0x200xx com as checagens da biblioteca e a tabela de tratadores do shell, mais duas listas de callbacks do sistema, o tick de rede guardado, o nome de autenticação ad hoc e o contexto do cliente de autenticação); SceNpManager (18: os pedidos 0xA00xx/0xB00xx; o NID_FFBFC540 = método 0xB0025, 64 bytes, agora com ponte; a configuração de comunicação que o sceNpInit guarda; o término do lado do sistema); SceNpCommerce2 (206: criação de pedido por tipo, id = contexto << 16 | n, oito por contexto; as checagens de contexto/pedido de toda operação da loja, a troca com o servidor, os pedidos do serviço de loja do shell, ticket, cópia de sessão; a loja continua inalcançável, como nas outras funções da loja do HLE); SceNpCommon (22: escape de URL/XML, o primeiro caractere XML, Base64, a família strtol, comparação de online id, o bloco de configuração e os padrões das opções de depuração num console de varejo); SceNpCommonPs4 (35, só reserva do firmware: rand, atômicos, acessores, tempo, travas lwmutex, heaps e alocadores, o cabeçalho Bearer); SceSqliteVsh (89: o SQLite 3.7.6.3 da vsh, cada exportação casada com a função da API pelo que faz no módulo; roda sobre o SQLite de verdade (e_sqlite3 pelo SQLitePCLRaw, domínio público / Apache-2.0, nos créditos do README) nos arquivos do host para onde os caminhos do guest apontam; bancos/statements/blobs/valores/contextos como handles, texto e blobs no heap do módulo até o próximo step/reset/finalize, callbacks de exec/colação/função/ganchos no guest; só reserva do firmware, `VITASHARP_HLE_MODULES=SceSqliteVsh` liga); libpaf (~120: membros de Widget/TextBox/ScrollView/RichText/BusyIndicator/ListItem, superfícies (filtros, wrap, páginas, refcount), estado de blend/depth/cull do ARL, TextLayout/TextStyle, Image (extensão, recorte), PropertyList, parâmetros de Plugin/Page, Timer, gestos, std::map, http::Transaction sobre o SceHttp). Um importador vs0 que antes pegava uma biblioteca toda do firmware não passa a uma HLE parcial (conferido por módulo; NID_650D111B e NID_CC8E6AC6 ficaram de fora: o NPXS10031 e o activity_db importam só eles, e o activity_db tirava o np_common do firmware, cujas exportações o resto do shell usava). Validação: testes de unidade (HleBatch9Tests, inclusive SQL de verdade num banco :memory:); shell (Amigos, Troféus, Câmera) e 10 apps do sistema sem cabeça: mesmos resultados da base (0 "Unimplemented", as mesmas quedas e o mesmo timeout do NPXS10026). Chamadas ao vivo: com `VITASHARP_HLE_MODULES=SceSqliteVsh,sqlite` o shell roda as consultas do próprio banco no SQLite do HLE (open_v2 13x, prepare_v2 35x, step 68x, column_text/int/count, exec 8x, create_function 4x, ganchos de commit/rollback/update, mprintf) e o NPXS10014 abre o seu banco (open_v2, busy_timeout, ganchos, prepare_v2); mas o livespace_db do shell para numa asserção do libpaf (BKPT, pela pilha SceLsdb -> SceDbutil, um ponteiro de texto NULL num paf::string) logo depois de abrir os bancos, então o HLE do SQLite fica desligado por padrão (o firmware continua com o seu). Próximo passo: achar a resposta que difere (o log agora mostra cada open e cada prepare que falha). Os outros pedidos novos (NetCtl, NpManager, NpCommerce2, NpCommon, libpaf) só têm teste de unidade: nenhum fluxo exercitado sem cabeça os chama (a loja só chama sceNpCommerce2Init sem rede). Não feito: os 27 pedidos do np_manager pela conexão libipmi própria (o HLE não abre essa conexão; o SceIpmi do libshellsvc está descompilado), os dois do cliente de autenticação ad hoc do libnetctl, o callout do np_common, o NpUtility (estado interno de HTTP), o resto do NpCommonPs4 (classes com vtables do módulo), o sqlite3_busy_handler/sqlite3_config (o SQLitePCLRaw não os expõe), o grosso do ui::Widget (motor de widgets/GPU). Meta de 3.000 no sistema não atingida (2.820).
  - Lote 10 (feat/hle-batch-10, 09/11/2026, WIP: o usuário encerrou o dia antes da validação). `hle-coverage --vitasdk=...` antes -> depois: geral 7.391/19.406 (38,1%) -> 7.585 (39,1%); sistema 2.819/7.511 (37,5%) -> 2.993 (39,8%); VitaSDK 5.229/7.006 (74,6%) -> 5.374 (76,7%). Feito: a conexão libipmi própria do np_manager (`NpManager.Ipmi.cs`: aberta no primeiro pedido depois do sceNpInit/NID_C89E0B94 pelas mesmas funções SceIpmi do libshellsvc, com a Config que o módulo monta; os 27 pedidos assíncronos 0xB00xx (pegar o id, enviar M, esperar, receber M+1, devolver o id) e os síncronos 0xB0048/0xB0049, com os mapeamentos de erro do módulo; sem libshellsvc, 0x80550502; o evento 0x33 do shell não é registrado); libnetctl NID_9CB6FEE1 (mapeamento UPnP: sem gateway, 0x80414187) e NID_1172C5FC (servidor de autenticação ad hoc: sem rede ad hoc, 0x80412108); serviços do kernel lidos nos módulos 3.74 (`SystemServicesVsh.Batch10.cs`): SceSblSsUpdateMgr (24: consultas da 3.74 em modo normal, avisos aceitos, operações de pacote de atualização 0x80020004), SceBt (14, pilha ociosa sem aparelho), SceUsbPspcm (12, 0x8024450D sem host USB), SceUsbstorVStor, Wlan/Usbd/Ctrl/Hid, Dipsw, DebugLed, região (CnvRegionStr), tempo de outro processo, RNG do kernel, avisos de instalação do SceNpDrmPackage (12), formas Ext de SceMotion (com porta; NID_D87E2B64 = GetState por porta) e SceMotionDev (registros vazios de calibração), FIOS2 kernel 02, internos do NGS, notificação de progresso, depuração do FIOS2; libpaf: sce_paf_exit (parada fatal), operator new no HeapAllocator, towlower/fflush privados; SceSqliteVsh sqlite3_config/sqlite3_busy_handler; módulo `SystemFallback` (só reserva do firmware): SceTeleportClient (14), SceSulpha (9), ScePromoterUtil (8, pedidos 0x13xxxx ao shell), eventos do SceShellSvc (4). O partial2 só aponta bibliotecas de módulos só-reserva (SceShellSvc/ScePromoterUtil/SceSulpha), que não tiram nada do firmware. Não validado: o build compila com 0 avisos, mas os testes (HleBatch10Tests) e as execuções do shell e dos 10 apps sem cabeça (base x novo; a worktree base fica no commit 5d97301) não rodaram. Próximo: rodar `hle10_seq.sh` (testes + execuções), conferir 0 "Unimplemented" e as mesmas quedas da base; faltam 7 para 3.000 (candidatos: SceNpDrm _sceNpDrmGetRifName e afins (10 importadores, sem semântica confirmada), sceTeleportServerSendInitialInfo, o resto do SceSulpha/ScePromoterUtil, NID_8D4F9F2E/D943CE15 do SceShellSvc). Não feito: o callout do np_common (precisa de uma thread do HLE que chame o guest), o parser de ticket NID_85503FA6, o NpUtility (estado do módulo; NID_31423835 tiraria o activity_db do firmware), o IniFileProcessor (motor C++ inteiro), o ui::Widget (motor de widgets).
  - Coluna VitaSDK (feat/hle-vitasdk-1, 10/11/2026), PARADO no começo (só um agente de HLE por dia, pedido do usuário): base 5.229/7.006. Feito (WIP, não compilado nem testado): `HleAttribute.Default` (bool) e `HleBinding.Default` (o gerador HleBinderGenerator emite `, true` quando `[Hle(Default = true)]`). Falta da infraestrutura: `HleImplementedNids` (Program.cs) devolver o Default (um NID é default só se todas as entradas dele forem default), `hle-coverage` imprimir real/default por grupo, `HleStatusReport` (colunas default/games_default/system_default no hle-status.tsv, `vitasdk_default=` no cabeçalho, badge "x% (n/total; d default)", colunas Real/Default no progress.md, dica no mapa), testes HleStatusReportTests, regenerar com build/hle-status.ps1/.sh e explicar "default" na nota do README. Plano (contagens): implementar ~906 (SceLibXml 180 sobre o XML do .NET, SceSqlite 164 sobre SQLitePCLRaw como o SceSqliteVsh, SceVshBridge 66, SceNpDrm 28, SceLibc 27, SceSmart/SceFace por LLE do módulo do jogo; SceLsdb 197 e SceLibMtp 47 ficaram com o agente A do sistema); default ~364 (Bt 42, SceCompat 34 (agente A), SblGcAuthMgr 34, Pamgr 31, Bbmc 27, SblPostSsMgr 24, SblUpdateMgr 24, Sulpha 23, Usbd 22, PspnetAdhoc 17...) com o retorno documentado do firmware; não se aplica ~509 (SceLibMonoBridge/Mono, libScePiglet, SceShaccCg: perguntar ao lead). Ordem: infraestrutura do default + grupo default, depois o grupo implementar por número de importadores.
  - Lote 11 (feat/hle-batch-11 / wip/hle11-*, 10/11/2026, agente único de HLE): lotes 10 e 10b validados e na main (1fb7766); depois a separação real/default e o grupo default. `[Hle(Default = true)]` marca uma função que precisa de hardware ou serviço que o emulador não tem e devolve o resultado do firmware para esse caso; `hle-coverage` imprime "real N, default N" por grupo, o hle-status.tsv ganhou colunas default, os badges dizem "n default", o progress.md tem colunas Real/Default. `hle-coverage --vitasdk=...` 1fb7766 -> agora: geral 7.963 -> 8.448/19.406 (41,0% -> 43,5%; real 8.089, default 359); jogos 1.566/1.566; sistema 3.303 -> 3.401/7.511 (44,0% -> 45,3%; default 174); VitaSDK 5.421 -> 5.906/7.006 (77,4% -> 84,3%; real 5.547, default 359). Feito: SceSqlite (164, o SQLite do SDK no mesmo motor do SceSqliteVsh, com UTF-16, backup, get_table, mutexes, lista de VFS, status, authorizer/trace/profile, agregados), SceCompat (34, ver o item do PSP), os 400 nomes VitaSDK que faltavam no nids.txt (SceLsdb, libScePiglet...), SceVshBridge (64), ScePspnetAdhoc PTP/poll/stats/ctl (17), Fios2 (4); defaults: SceBt 28, SceBbmc 27, SceUsbd 21, SceUsbAudioIn 8, SceUsbSerial 7, SceUsbServ 2, SceSblGcAuthMgr 34, SceSbl{Lic,Pm,Rtc,Ut,Qaf,Dmac5}Mgr 34, SceMotionDev 6, SceSulpha 14, SceNgsInternal 5, SceNpDrm/PsmDrm/Package 16, e as entradas sem hardware de lotes anteriores (Bt, Bbmc, UsbPspcm, UsbstorVStor, SblSsUpdateMgr) remarcadas default. Próximos, por tamanho: SceLibMonoBridge 293 (runtime do PSM), SceLsdb 197 (+162 sistema: o livespace_db sobre o app.db; os exports são envoltórios de um motor de banco próprio), libScePiglet 182 (o GLES2 do PSM sobre o GXM), SceLibXml 180 (sce::Xml: objetos com ponteiro para a implementação, alocador do app por vtable, String {char*, size} por valor, NodeId de 64 bits; decompilado em <scratchpad>\hle11_kdecomp), SceSmart 44/SceFace 23 (módulos do jogo), SceShaccCg 33, SceLibstdcxx 25, ScePamgr 31 (só devkit), ScePromoterUtil 8, SceRegMgr 5, SceShellUtil 5; no sistema: ScePafWidget 608, ScePsp2Compat 518 (a camada POSIX do WebKit, NIDs sem nome), SceEmailEngine 223, SceNpCommonPs4 210, ScePafGraphics 174, SceLsdb 162, SceDbutil 100.
  - Lote 12 (feat/hle-batch-12, 10/11/2026, agente único de HLE). `hle-coverage --vitasdk=...` main 566c872 -> agora: geral 8.453 -> 8.907/19.406 (43,6% -> 45,9%; real 8.540, default 367); jogos 1.566/1.566; sistema 3.405 -> 3.620/7.511 (45,3% -> 48,2%; default 178); VitaSDK 5.911 -> 6.240/7.006 (84,4% -> 89,1%; real 5.845, default 395). Feito: SceLibXml (180, sce::Xml inteiro: Initializer/MemAllocator, String/SimpleData, Attr, AttributeList, NodeList, Dom::Document/DocumentBuilder/Node, Sax::Parser com o DocumentHandler do jogo, VarAllocBuffer, strResult/bXResultToResultType; parser incremental e serializador nossos, conferidos contra o libSceXml.suprx 3.74 de verdade pelo XmlHleTests, que roda os mesmos cenários no módulo do firmware e no HLE e exige o mesmo log: tipos de nó (4 elemento lido, 5 criado, 7 CDATA, 8 texto, 0x17 PI, 0 documento), entidades (sem resolveEntity ficam como escritas; com ela cada uma é um texto à parte), skippedText 5/7/9/11 com o texto até o fechamento, insertNode depois do nó de referência, getParent da raiz = 0, os erros do módulo (atributo duplicado e sem '=' = 0x80850008, marcação fora da raiz 0x8085000B), Node::getChildNodes/getAttributes vazios e a falha do serializador do módulo (o irmão não-elemento depois de um elemento vazio ganha o fechamento dele), DocumentBuilder ignora texto só de espaços por padrão); SceLsdb (LSDB, os getters do AppInfo (std::map do módulo: chave em +0x10, tipo em +0x18, valor em +0x20, nil em +0x29), título/título curto pela língua do sistema, bits do atributo (versão do LiveArea, atividade, prévia, modo de memória estendida), categorias, ParseVersion, faixa de horário, idioma/país/ambiente NP e região em paf::string, MapLanguageIndex, AppDatabaseInit/End); ScePamgr (31: PMU e timebase como o ScePerf, o resto default: sem o hardware de trace do Razor) e SceDTrace (6 default: sem /dev/dtrace); libScePromoterUtil (8: DeletePkg, GetSize, PromoteImport, PromotePkg(WithRif), RemoveSavedata, UpdateLiveArea, UpdateUpgradableStatus, pedidos 0x13xxxx ao serviço do shell com a espera de estado do módulo); libc.suprx (17: _Call_onceEx, _Do_call, textos do <future>, _Random_device_entropy, destrutores de regex_error/future_error, _Rng_abort, _Fiopen com nome largo, _FCbuild, _XLgamma d/e/f (Lanczos do módulo), _MP_Add/Get/Mul/Rem); SceEmailEngine (98: os acessores de membro e os liberadores das classes do motor de e-mail, offsets lidos no módulo); libgxm (6: _sceGxmBeginScene antigo, sceGxmInitializedInternal, CheckMemoryInternal, RenderingContextIsWithinSceneInternal, SetCallbackInternal, SetInitializeParamInternal); formas do libkernel/syscall (sceSblGcAuthMgr* do libkernel como default, _sceKernelWaitException(CB), _sceKernelExitProcessForUser, _sceDisplaySetFrameBufForCompat, sceKernelGetPMUSERENR); sceTeleportServerSendInitialInfo (0x8010840A na unidade com a capacidade 10); parte 2: ScePsp2Compat (~60: acessores de membro, constantes de tamanho de tela, pthread sobre lwmutex/lwcond "SceWebKitPosixPthread", usleep, ids, as entradas de malloc/free/safe memory/NetCtl que ele reexporta), sce_paf_private_setjmp/longjmp, (_)sceKernelPMonThreadGetCounter. Validação: testes de unidade (XmlHleTests com o módulo do firmware, HleBatch12Tests), Category!=Slow completo (1.476 ok; os NativeJitTests ficaram de fora por um travamento conhecido do testhost que outro agente corrige), shell e 10 apps do sistema sem cabeça: 0 "Unimplemented" e as mesmas quedas da base (o NPXS10026 com timeout na base e no novo). Obs.: o Storage Sense do Windows apagou partes do scratchpad no meio das execuções (junctions e cópias de dados de teste); as execuções afetadas foram refeitas. Não feito (próximos): SceLibMonoBridge 293 / libScePiglet 182 (runtime do PSM e o GLES2 dele), o lado de banco do SceLsdb (~140: passa pelo SceActivityDb do shell e por std::map/shared_ptr do módulo; o app.db tem tbl_appinfo(titleId, key, val) com as mesmas chaves dos getters), SceSmart 44 / SceFace 23 / SceShaccCg 33 (módulos que o jogo traz), o resto do SceEmailEngine (classes com estado do módulo), SceShellUtil (cliente assíncrono com thread própria), Location extension (histórico de Wi-Fi).
  - Lote 13 (feat/hle-batch-13, 10/11/2026, agente de HLE do lado VitaSDK; 13b faz o lado do sistema e 13p o libScePiglet). `hle-coverage --vitasdk=...` main ebec399 -> agora: geral 8.907 -> 9.074/19.406 (45,9% -> 46,8%; real 8.571, default 367, LLE 136); sistema 3.616 -> 3.734/7.507 (48,2% -> 49,7%; LLE 112, o SceLsdb); VitaSDK 6.240 -> 6.800/7.006 (89,1% -> 97,1%; real 6.166, default 400, LLE 234). Categoria nova "LLE" (como "default"): funções de bibliotecas que o emulador fornece carregando e rodando o módulo de verdade (`Hle/LleLibraries.cs`): SceSmart/SceFace (libsmart/libface que os jogos trazem em app0:sce_module), SceShaccCg (ur0:data/libshacccg.suprx que o usuário extrai do console) e SceLsdb (vs0:vsh/shell/livespace_db.suprx, que agora o LoadFirmwareDependencies carrega para qualquer importador, não só o shell); só contam os NIDs que uma cópia instalada do módulo exporta de fato (as versões do libsmart dos jogos exportam 37 dos 44); LleLibrariesTests carrega cada módulo e roda um export, e um importador sintético que não é o shell recebe o livespace_db com os imports ligados; colunas/badges/progress.md/README mostram "n LLE". Variáveis (exports de dados) que o HLE entrega passam a contar (environ do SceLibMonoBridge, std::adopt_lock/defer_lock/try_to_lock, os do libc). Feito: SceLibMonoBridge inteiro (o runtime do PSM: eglib `monoeg_*` com os layouts do eglib na memória do guest — GList/GSList/GString/GArray/GPtrArray/GQueue/GHashTable com o rehash e o iterador do eglib, GError, Unicode UTF-8/16/UCS-4 com os erros do GLib, g_convert, caminhos, arquivos/diretórios, o parser de markup com callbacks no guest, log/assert, GTimer; pthreads sobre lwmutex/lwcond/threads do kernel com chaves, cleanup e join; `pss_*` sobre sceIo/sceNet/kernel/VM blocks com as raízes /Application /Documents /Temp do PSM (protótipos inferidos: o runtime do PSM não está instalado); os helpers do libgcc), mono_security_enable_core_clr, `__aeabi_unwind_cpp_pr0/1/2`, Set/PulseEventWithNotifyCallback (+ syscalls), histórico de Wi-Fi do Location (negado como as outras medições), AppUtil Book/ExtVideo mount e AddcontForceAddcontMount, sceBgAppUtilStartBgApp (a sequência do módulo no libshellsvc), sceLiveAreaEnableBgAppMode, sceHttpGetCookieStats, sceSslInternalWrite (SSL_write do módulo), a arena de replay do libgxm, sceGpuSignalWait/WaitLockup e (Un)RegisterSalvage sobre o contador de sinais da fila da GPU, sceRegMgrSrvCnvRegionInt e GetInitVals (os padrões do template do registro), _Xregex_error, _Rethrow_future_exception, __set_exidx_main. Restam no VitaSDK: libScePiglet 182 (13p), SceShellUtil 6 (13b), PVRSRVOpen/Release/BridgeDispatchKM (precisa de um modelo dos serviços PowerVR), SceRegMgr GetRegVersion/ResetRegistryLv/DbBackup/DbRestore (o formato e os níveis do registry.db0 do SceRegistryMgr), SceAppMgr Dump/EnableCoredumpForTest/QuitForNonSuspendableApp/SuspendUntilActivated (checagens do SceSblACMgr ainda não identificadas), sceGxmShaderPatcherCreateFragmentProgramForGles/GetTopContextInternal, _Throw_future_error, sceKernelAllocUnmapMemBlock, _sceKernelExitCallback, ScePaf 2.
  - Lote 13, continuação (11/11/2026, agente único de HLE depois do 13b). Parte 2: sceAppMgrDump e sceAppMgrQuitForNonSuspendableApp (lidos no SceAppMgr do bootimage). SceDbutil (22 exports sem vtable do módulo: a conexão { +8 sqlite3*, +0x1C bytes de flag, +0x44, +0x54/+0x58 callback de erro, +0x98 RMutex } com interrupt/changes/total_changes sob a trava e o mapeador de erro 0x80103200 | código, o objeto de resultado { +0x10 RMutex, +0x30 "pronto" }, os contadores sob mutex, as linhas de 0x18 bytes, os registros zerados), conferidos contra o dbutil.suprx 3.74 do firmware em LLE pelo DbutilHleTests (mesmo resultado e mesmos bytes do objeto em cada cenário). Com a main (13b incluído) -> agora: geral 9.425/19.406 (48,6%), sistema 3.916/7.507 (52,2%), VitaSDK 6.808/7.006 (97,2%). A reclassificação em massa como LLE foi descartada (o usuário prefere HLE de verdade, por ordem de utilidade). Próximo: o resto do SceDbutil (as classes com vtable do módulo), SceNpCommonPs4 classe a classe, pedaços do SceEmailEngine e o resto do ScePsp2Compat; depois os motores grandes (ScePafWidget/Graphics) aos poucos.
  - Lote 13b (feat/hle-batch-13b, 10-11/11/2026, auxiliar do lote 13, lado do sistema fora da coluna VitaSDK). `hle-coverage --vitasdk=...` base 884d58c -> 872e98b: sistema 3.635 -> 3.781/7.507 (48,4% -> 50,4%), geral 8.962 -> 9.228 (46,2% -> 47,6%), VitaSDK 6.240 -> 6.246 (os 6 NIDs SceShellUtil/SceShellUtilLaunchApp); depois do rebase na main: sistema 3.785. Feito (tudo lido nos módulos 3.74 descompilados, só estudo; módulos só-reserva do firmware, exceto o SceMotion): SceShellUtil (40: o cliente do serviço "shell util" do libshellsvc, InitEvents/TermEvents, quatro vagas de pedido, o poll que entrega ao tratador (resultado, tipo, parâmetro, dados), Lock/Unlock com a contagem do 0x800 e os 20 outros tipos de pedido, a thread de eventos do impose, a área de transferência de texto UTF-16 de 0x1FFE caracteres; sem shell os pedidos terminam com 0 e os bits de trava ficam em `ShellUtil.LockedBits`; os que esperam o shell agir (lançar app, consultas síncronas) são default) e SceShellUtilLaunchApp; SceShellSvc (6: as flags de evento do shell, registro de tratador, espera de estado como timeout 0x80100904 default); IPMI::Client::Config::estimateClientMemorySize; ScePsp2Compat (~65: a camada POSIX do WebKit: open/close/read/write/lseek/ftruncate/unlink/mkdir/rmdir/stat, diretórios, sockets AF_INET, pipes sobre message pipes, chaves pthread (64, destrutor), pthread_once, mutex lock/trylock, cond_timedwait, a pilha da thread, mmap sobre blocos de memória (RW e o RX do JIT), gmtime_r/localtime_r, a tabela de ambiente, números do heap, objetos worker, png_error/png_read_data/png_write_data/png_process_data_skip); libpaf: paf::math quaternion/matriz (33, conferidos contra o libpaf.suprx do firmware em LLE pelo PafMathHleTests: Ortho/Frustum, inversas geral/rígida/3x3, transposta, isCoordSys/getCoordSysError/fixedCoordSys, Euler nas 6 ordens com trava de cardã, Slerp, exp/log, toMatrix/de matriz; o FromEuler do módulo escreve a metade dos ângulos na cópia do argumento), IsInherit das 53 classes (a hierarquia lida no módulo), os getters/setters de layout do TextLayout e as consultas do TextObj pelo layout (+0x50), 55 membros de uma instrução; formas libkernel do sceSblGcAuthMgr (22 default); SceMotion NID_ACEFA121/BC5C86C5/0D9FA7EA/D88B731D (os interruptores de amostragem dos outros clientes do driver_us; o servidor de localização do shell chamava o ACEFA121 sem HLE: agora 0 "Unimplemented" no shell e no NPXS10004, chamada ao vivo 1x); np_common_ps4/dbutil de uma instrução (34). Validação: HleBatch13bTests e PafMathHleTests (firmware) ok; shell + 10 apps do sistema sem cabeça base x novo idênticos (0 "Unimplemented" fora o ACEFA121, sem quedas nem stalls); Category!=Slow depois do rebase na main 3aa90d2: 1.619 ok, 0 falhas. As funções novas, fora o SceMotion, não são chamadas ao vivo (os módulos do firmware servem os programas do sistema). Sobrou (para o agente do lote 13): ScePafWidget/ScePafGraphics/ScePafMisc (~1.200 no sistema: o motor de widgets e os atributos de estilo do TextLayout, que passam pelo AttributedString +0x38), SceNpCommonPs4 (~210, classes com vtables do módulo), SceDbutil (~95, classes C++ sobre o SQLite da vsh com vtables do módulo), SceEmailEngine (~135), o resto do ScePsp2Compat (~410: libpng/libjpeg e o contexto gráfico do WebKit, que dependem do estado do módulo), SceIpmi (o resto do cliente/servidor IPC), SceSslInternal (68), SceFt2 (16, FreeType), a entrada "grande texto" do TextLayout (o atributo 0x13 multiplica por 1,35 quando o ScePafMisc_C51EF81D liga o bit 0; o HLE não tem esse estado). Obs.: o hle4_fw (ELFs decifrados) foi apagado na limpeza do disco; o hle13 regenera em hle13_fw.
  - Lote 13p (feat/hle-batch-13p, 10/11/2026, agente do Piglet): libScePiglet inteiro (182 NIDs do VitaSDK: EGL 1.4 + OpenGL ES 2.0 do runtime do PSM). `hle-coverage --vitasdk=...`: VitaSDK 6.240 -> 6.422/7.006 (89,1% -> 91,7%); geral/jogos/sistema sem mudança (o Piglet não está no firmware). Feito (Hle/Modules/Piglet*.cs): o estado do GLES2 em C# e os objetos na memória do guest, e tudo vira as mesmas chamadas GXM de um jogo (Gxm.PigletApi): uma tela (janela 960x544 com 3 buffers na fila de exibição do GXM, intervalo de troca), pbuffers (também como textura: eglBindTexImage), 5 configurações, contextos com objetos compartilhados, erros do EGL por thread; buffers (dados novos quando a GPU ainda lê o antigo, escritas parciais na ordem da GPU), texturas 2D/cubo nos layouts do GXM que o renderizador lê (lineares com linhas de 8 texels e mips em potência de 2; DXT1/3/5, ETC1 e PVRTC 1/2 swizzled), glGenerateMipmap/glCopyTex(Sub)Image2D/glReadPixels na ordem da GPU (a cena é gravada de volta antes), renderbuffers e FBOs (completude do GLES 2.0 + o que o GXM não faz: depth e stencil separados, cor em cubo), cenas (uma por alvo; depth carregado quando uma cena anterior gravou), glClear como um quadrado com o shader de limpeza (tesoura e máscaras valem; sem compilador, preenchimento por transferência), cull/depth/stencil/blend/máscara de cor/viewport/tesoura/polygon offset, atributos (VBO no lugar, arrays do cliente copiados, FIXED convertido, valor constante com stride 0, divisor por instância), índices (bytes convertidos, line strip/loop como listas), uniforms nos buffers padrão pelo sceGxmSetUniformDataF e samplers pela tabela de parâmetros do GXP; shaders Cg compilados pelo libshacccg.suprx de verdade (LLE: o do app, ou ur0:data como no vitaGL), glShaderBinary com GXP e glPigletGetShaderBinarySCE. Validação: PigletHleTests (9: EGL, erros/estado, buffers, texturas, FBOs, shaders/programas, e dois de ponta a ponta com o renderizador Vulkan que leem os pixels: triângulo + quadrado texturizado, FBO com depth/tesoura/cull e a textura dele na tela); nenhum app com Piglet testado (não há homebrew público com o Piglet). Não feito: GLSL ES (o compilador só aceita Cg, como no PSM), cor constante de blend (o GXM não tem), MSAA, save state com o Piglet em uso (módulo sem snapshot).
- [ ] LittleBigPlanet (PCSA00017): o filme de abertura sai "zuado" (relato do usuário, 07/11/2026, main 90b55bc, gravação
- [x] LittleBigPlanet (PCSA00017): o filme de abertura sai "zuado" (relato do usuário, 07/11/2026, main 90b55bc, gravação
  `captures\PCSA00017_20261107_011815_tudo`, 30 fps): o analisador acha 10+ flashes em 6,7 s — quadros do filme com brilho que pula entre
  vizinhos (o logo e as partículas aparecem e somem de um quadro para o outro, ex. presents 2532/2537/2538) e quadros
  pretos isolados (2432, 2460). O filme é tocado pelo AvPlayer com H.264 pelo `ffmpeg-d3d11va` (decodificação na GPU
  do Windows). Suspeitas: ordem de apresentação dos quadros com B-frames (o histórico de quadros do decodificador
  corrigido para o Jak), um quadro entregue antes de terminar de decodificar, ou a cópia da imagem da d3d11va. Comparar
  com a decodificação em software (sem d3d11va) e com um vídeo do Vita; corrigir de forma genérica
  Correção feita (07/11/2026, agente C, fix/bugs-games-17): o `auto` do `--video-decoder` agora decodifica em software
  (`hw` ou o nome do dispositivo ainda pede o decodificador da GPU). Mesmo trecho do filme (boot sem entrada, presents
  2400-2620, dados de teste): d3d11va 12 flashes e 1 par de quadros trocados, 6,0 ms por chamada e até 470 ms
  (`bugsC3_data/captures/PCSA00017_20261107_211552_tudo`); software 0 flashes do filme, 2,8-4,7 ms por chamada e até
  19 ms (`..._211757_tudo`, `..._212638_tudo` com o build novo). O FFmpeg de linha de comando decodifica o mesmo filme
  (`bugsC17_lbp/movie.mp4`) igual bit a bit com e sem d3d11va (900 quadros, com e sem low_delay), então o defeito vem
  do caminho da GPU dentro do emulador (lentidão/concorrência), não do fluxo. Falta o usuário confirmar no jogo
  d3d11va (agente C, fix/bugs-games-18): o filme do LBP também tem B-frames, e o FFmpeg do emulador abria com
  low-delay (corrigido no 2f7518c, ver Ninja Gaiden). Com d3d11va, mesmo build, frames 2400-2620 sem entrada: com
  low-delay 4 flashes (pares A-B-A em 2403/2404 e 2440/2441, `bugsC3_data/captures/PCSA00017_20261107_234538_tudo`);
  sem low-delay a janela caiu noutro trecho do filme (o boot variou) e deu 16 flashes de células
  (`..._235336_tudo`), e o software no mesmo build 2 (`..._235636_tudo`). Inconclusivo: comparar o mesmo trecho
  (rerun de uma gravação com estado no meio do filme, `--video-decoder=hw` x software x `VITASHARP_VIDEO_LOW_DELAY=1`)
  antes de voltar o `auto` para a GPU
  Áudio "estranho" (gravações `captures\PCSA00017_20261107_230046_tudo` e `..._230127_tudo`, agente C,
  fix/bugs-games-18): as vozes NGS saem limpas, mas o port 1 (thread "FMOD NGS output thread", grão de 256) repete grãos
  idênticos a 4 grãos antes (anel de 4 saídas do FMOD): 199 de 1689 (12%) na gravação do usuário. Mesma causa do Ninja
  Gaiden (o OpenAL devolve ~4 grãos de 256 de uma vez). Rerun da 230046 com o OpenAL de verdade: sem o espaçamento 970
  de 2303 grãos repetidos (42%), com o 352d66f 0 (`bugsC18_out/lbpr_off_audio`, `lbpr_on_audio`).
  Planeta "embaçado" (mesmas gravações, com `--res-scale=2`): o desfoque de profundidade lê a profundidade da cena 4X
  (store forçado) desempacotada como textura 1440x816; acima do nativo a imagem multisample é escalada, mas o
  desempacotamento era no tamanho do guest, então o texel (x, y) lia o pixel (x/2, y/2) da imagem escalada (só o quarto
  superior esquerdo) e o planeta inteiro ficava desfocado. Corrigido (e66ac42): desempacota no tamanho escalado. Replay
  do dump da gravação: nativo nítido, 2x antes desfocado, 2x depois igual ao nativo (`bugsC18_rp/lbp_r1`, `lbp_r2`,
  `lbp_r2fix`, `bugsC18_yt/lbpfix.png`)
  Confirmado pelo usuário (08/11/2026)
  Confirmado pelo usuário (08/11/2026): filme e áudio corrigidos (software decode + espaçamento de grãos)
- [x] Injustice (PCSE00271): elementos do HUD faltando/ruins (relato do usuário, 07/11/2026, gravação
  `captures\PCSE00271_20261107_223633_tudo`). Nos 478 quadros ao vivo as barras de vida, os nomes e o tempo nunca
  aparecem (o HUD de baixo aparece); o replay do mesmo dump mostra tudo e bate com o Vita real (vídeo `XerzDSm7Z9U`:
  barras prateadas). Causa: a cena do HUD (960x544) carrega (FORCE_LOAD) o depth/stencil 0x60600000 que a cena 3D usa
  em 720x408; com outro tamanho ela ganhava uma imagem nova com conteúdo indefinido, a memória reciclada do driver
  trazia o bit 7 do stencil ligado e as máscaras do HUD (EQUAL 0 nos 8 bits depois de limpar só os 7 de baixo) falhavam.
  Correção (cf3cf9d + 8b6787d): a imagem nova recebe a sobreposição da imagem mais recente da mesma superfície e o resto
  fica como memória zerada (`VITASHARP_DEPTH_SEED=0` desliga; `VITASHARP_GPU_POISON=1` agora envenena imagens de
  profundidade novas). Prova no replay com poison: sem a correção o HUD de cima some como no usuário, com ela aparece
  (`bugsC18_yt/p01.png`, `p2.png`). Falta o usuário confirmar no jogo
  Voltou ao vivo (relato do usuário, 08/11/2026, main 65bcf95, `captures\PCSE00271_20261108_163244_tudo`): a 1x desde o
  boot o HUD de cima some e só aparece depois do F1. Causa: a semeadura só valia para uma imagem de profundidade criada
  na hora; numa sessão de verdade a imagem 960x544 da superfície já existia dos menus e a cena do HUD de cada luta
  carregava o stencil velho dos menus. Corrigido (agente C, fix/bugs-games-21, e26e5bd): uma cena que carrega a
  superfície copia a sobreposição da imagem mais nova da mesma superfície sempre que ela renderizou depois. Prova ao
  vivo do boot pelos menus até Luta Única, sem troca de escala: main sem barras/nomes/tempo
  (`bugsC3_data/tests/inj_nav3`), com a correção completos (`inj_nav4`; `inj_nav5` com `VITASHARP_GPU_POISON=1`)
  Confirmado pelo usuário (08/11/2026)
- [x] Ninja Gaiden Sigma 2 Plus (PCSE00233): barra de vida "zuada", sobrepondo o resto da tela (relato do usuário,
  08/11/2026, `captures\PCSE00233_20261108_162212_tudo`). Corrigido (agente C, 4c5f352): o jogo corta os pedaços da barra
  com planos de recorte do usuário (saídas CLIP0-3 do vertex program, vo1 bits 0-7), que o tradutor ignorava; a parte
  vermelha ia até a borda. Agora viram gl_ClipDistance[1..7] (gerador v23). Replay do dump igual ao vídeo do Vita real
  (`l2I_NsWWFEI`); falta o usuário confirmar
  Confirmado pelo usuário (08/11/2026)
- [x] 99Vidas (PCSE00914): o logo do parceiro na intro e textos de menus somem (relato do usuário, 08/11/2026,
  `captures\PCSE00914_20261108_164513_tudo` e `..._165037_tudo`). Corrigido (agente C, 343ad70): o Unity renderiza
  superfícies 8x8 no começo e depois carrega o logo, o atlas da fonte e sprites na mesma memória; a checagem de
  "sobrescrito depois de renderizar" só olhava páginas inteiras dentro da superfície (uma 8x8 não tem nenhuma) e a
  textura vinha da superfície velha. Prova ao vivo: logo "99 VIDAS" e os textos da seleção de personagem aparecem
  (`bugsC3_data/tests/v99_fix`, `v99p_fix` x `v99_main`, `v99p_main`); o menu de pausa em jogo não foi alcançado por
  script, falta o usuário confirmar
  Confirmado pelo usuário (08/11/2026)
- [x] Need for Speed Most Wanted (PCSE00089): "não vira o carro" (relato do usuário, 08/11/2026,
  `captures\PCSE00089_20261108_162657_tudo`, já em gameplay). Não reproduzido (agente C): do estado da gravação, R +
  analógico à direita por script faz o carro girar com a main e com a correção do Peek (b3c16ca: sceCtrlPeek/Read
  preenchem todas as amostras pedidas e devolvem a contagem, como o ctrl_get do Vita3K; o jogo lê 2 amostras). Falta
  uma gravação com o carro em velocidade sem virar
  Confirmado pelo usuário (08/11/2026)
  Causa (agente B, 08/11/2026): o jogo lê 2 amostras num buffer de 2 entradas zerado (lr 0x8100AB95), junta os botões
  das duas e tira os analógicos só da 2ª; com 1 amostra escrita os analógicos ficavam em 0 (cima/esquerda inteiros), o
  que também prendia o foco do diálogo do Origin (X em CONTINUE não fazia nada). Com o b3c16ca, do boot sem estado: X
  passa o diálogo do Origin e A/D do teclado viram o carro na corrida (`scratchpad/bugsB1/nfs_b1_sheet.png`,
  `nfs_steer_proof.png`)
- [x] Need for Speed Most Wanted (PCSE00089): depois de "trocar de carro" a tela fica preta (relato do usuário,
  08/11/2026, `captures\PCSE00089_20261108_183918_tudo`, build 3261a98). Achados (agente B): reproduz no replay-gpu (388
  quadros pretos). A cena é desenhada (HDR F16 e o tonemap em 0x60A99800 com conteúdo); o último passe (fs
  3C7FBEE743D02522, LUT 16x2048) recebe da CPU escala 0,0795 / viés -0,0049 contra 0,9708 / 0,0135 numa corrida normal:
  o próprio jogo segura um fade para o preto. Nesse momento um avPlayer novo (f7380) tem o 'avPlayer Demux' já Dormant
  e o 'avPlayer VideoDec' esperando a condvar decbuff para sempre: o filme da troca de carro não entrega quadro e o fade
  não volta. Antes disso o usuário trocou 1x->2x e 30->60 fps por hotkey. Rodando do estado da gravação com o build
  3261a98: o filme é `UI/MOVIES/1689052.MP4` (prévia do carro, 348 KB, só vídeo, sem trilha de áudio, como todos os
  1689xxx/1473xxx), o SceMp4 já tinha chegado ao fim (última unidade nula) e em 260 quadros nenhuma chamada
  SceMp4/sceAvcdec acontece; a tela segue escura. Próximo: chegar à troca de carro sem estado (dirigir até um ponto de
  troca) com trace do SceMp4/sceAvcdec desde a abertura do filme e ver o que o player do jogo (entry 0x816361B5) espera
  de um filme sem áudio (fim de stream, sceMp4Reset para repetir, relógio de áudio)
  Rodada 2 (agente B, Ghidra no libscemp4 do FW 3.74 e no eboot, só estudo): o sceMp4GetNextUnit de verdade devolve 0
  (unidade), 1 (fim do stream) ou -1 (erro); o HLE devolvia 0x80630010. O player do jogo (o mesmo do F1 2011) passa
  o valor adiante: negativo vai para o caminho de erro (o Demux para sem avisar o VideoDec), 1 para o de fim (repete,
  esvazia, avisa). Corrigido em 64a15c4 (GetNextUnit devolve 1 no fim); no boot a prévia 1689283 chega ao fim com 1 e
  o jogo fecha o player normalmente. **Falta validar** na troca de carro: não cheguei lá (a corrida do prólogo de
  1,6 mi; com o novo `hold` do --nav (3be9b2c) dá para acelerar e virar juntos, mas guiar às cegas por prints levou
  mais de 30 passos e não terminou a corrida)
- [x] Batman: Arkham Origins Blackgate (PCSE00269): som "estourado" e depois o jogo trava (fps 0) (relato do usuário,
  08/11/2026, `captures\PCSE00269_20261108_185032_tudo`, build 3261a98). Travamento corrigido (agente B, 0d19271):
  o Wwise (FUN_814f02cc, Ghidra no eboot, só estudo) lê o estado do player NGS e libera os buffers do stream do índice
  dele até o "buffer atual" (+4), andando de 1 em 1 módulo 3; quando a cadeia de buffers acabava (next = -1), o HLE
  gravava -1 em +4 e esse laço nunca terminava (AK::EventManager girando no ReleaseBuffer, main esperando
  AkManualEvent, IO thread parado). Agora um next inválido deixa o último buffer como atual (os válidos seguem como
  no Vita3K). Do boot sem estado: intro, luta no telhado, cena da Mulher-Gato, gancho no letreiro ROYAL e adiante,
  23209 quadros a 30 fps (`scratchpad/bugsB2/bat_l1/sheet6-8.png`); o build 3261a98 trava do estado da gravação.
- [x] Batman: Arkham Origins Blackgate (PCSE00269): som "estourado" (agente B, 0caefdf). A música e os sons em stream
  do Wwise são vozes NGS em HEVAG estéreo; o primeiro buffer pula o cabeçalho do arquivo (52 bytes), então toda
  fronteira de buffer seguinte corta um quadro de 16 bytes. O HLE jogava fora a sobra do buffer e decodificava o
  próximo desde o byte 0: tudo dali em diante saía 4 bytes fora de quadro, como ruído. Agora o quadro partido espera o
  começo do buffer seguinte (como no Vita3K). Espectro da porta 1 no gameplay: antes 37-41% da energia abaixo de 1 kHz
  e 12-27% em 4-8 kHz; depois 86-95% abaixo de 1 kHz e 0,1-3% em 4-8 kHz, como um longplay do Vita de verdade
  (75-95% / 0,2-7%) (`scratchpad/bugsB2/bat_g1/audio`, `bat_l1/audio`, `yt/longplay.wav`)
  Corrigido (08/11/2026, 64a15c4, merge f4097ef, agente B): o `sceMp4GetNextUnit` devolvia um erro negativo inventado
  no fim do stream; o libscemp4 3.74 devolve 1. O player do jogo tratava o negativo como erro de leitura, o demuxer
  parava e o decodificador de vídeo esperava para sempre (tela preta depois da prévia do carro)
- [x] Batman: Arkham Origins Blackgate (PCSE00269): som "estourado" e depois o jogo trava (fps 0) (relato do usuário,
  08/11/2026, `captures\PCSE00269_20261108_185032_tudo`, build 3261a98). Achados (agente B): reproduz do estado da
  gravação com o build 3261a98 (~250 quadros depois do estado): 'main' espera a eventflag AkManualEvent, o
  'AK::EventManager' (Wwise) gira LockLwMutex/UnlockLwMutex2 em 0x81513A2C esperando um buffer de stream ([obj+0x9C] >
  0) e o 'AK::IOThread' espera a eventflag CAkIOThread.Events (0xD, OR) sem nunca acordar; depois da carga a porta 1
  só recebe silêncio (`scratchpad/bugsB1/bat1/audio`). O audio/ vazio da gravação combina com isso (o thread do Wwise
  já não chamava sceAudioOutOutput quando o F8 começou). Protocolo (disassembly do eboot, só estudo): o IO thread do
  Wwise espera 0xD em OR sem limpar; o bit 0x8 é ligado quando a contagem de pedidos pendentes (+0x40) passa de 0 para
  1 sem pausa (+0x44) e desligado quando volta a 0 ou ao pausar (sceKernelClearEventFlag com o complemento, que o HLE
  trata certo: Pattern &= bits); o bit 0x4 segue a contagem de +0x3C; 0x2 os slots livres (+0x48 contra +0x4). No
  travamento a contagem de pendentes está em 0 (IO dormindo) e o EventManager espera dados de um stream: um pedido
  sumiu ou nunca foi feito. As semânticas de eventflag/lwmutex do HLE conferem; falta seguir quem chama 0x81517354
  (enfileira) e 0x815173CC/0x815173AE (pausa/retoma) perto do travamento. Causa ainda aberta
  Corrigido (08/11/2026, 0d19271, merge f4097ef, agente B): quando a corrente de buffers de um player do NGS (ou
  ATRAC9) termina (próximo índice -1), o buffer atual (+4) guardava o -1; o Wwise lê +4 como slot do anel e o
  AK::EventManager girava no ReleaseBuffer para sempre. Agora o último buffer fica como atual (como o Vita3K)
- [x] SILH00004 (homebrew, port de Silent Hill 4 com vitaGL): não rodava (relato do usuário, 08/11/2026). Feito
  Fechado (08/11/2026): roda até o gameplay (9bc0d66); a imagem "zuada" e os controles seguem num item próprio se o usuário detalhar
- [x] SILH00004 (homebrew, port de Silent Hill 4 com vitaGL): não rodava (relato do usuário, 08/11/2026). Feito
  (agente C): módulo ausente devolve o erro do Vita em vez de exceção do host (bf378ec, aviso no log de como extrair o
  libshacccg.suprx com o ShaRKF00D); injeção de dados (relativa e absoluta) e hook por offset em Thumb
  (26a9556): o SceShaccCgExt aplica os patches no libshacccg. **Aberto**: a tela continua preta em 900 quadros; o log do
  próprio port (`ux0:data/sh4/sh4log.txt`) mostra o heap newlib de 24 MB (tamanho escolhido pelo port) cheio
  ("malloc 1024 KB failed ... texture skipped") e o vitaGL sobre esse heap. Falta referência do Vita real
  Corrigido (08/11/2026, agente C, fix/bugs-c-22): o vitaGL chama `sceAppMgrGetBudgetInfo` para saber se é app de
  sistema; o HLE devolvia 0 para todos, o vitaGL entrava no modo de sistema, não criava os pools de CDRAM/RAM/phycont
  e punha todas as texturas no heap newlib de 24 MB. Agora só programas do sistema recebem o orçamento (o Vita3K
  responde "not system mode" para apps). Com isso: pools `cdram_mempool` 112 MB, `ram_mempool` 32 MB,
  `phycont_mempool` 26 MB, heap newlib em ~8 MB, 30 fps (antes 15) e o jogo mostra logo da Konami, aviso, título,
  menu, tela de Load, dificuldade, texto de abertura e o quarto em primeira pessoa com o menu de pausa. No Vita3K do usuário o mesmo port também cria os pools (log do port: "after vitaGL: cdram 0 KB") e toca o
  filme m_00. Diferença que sobra: o Vita3K informa 232 MB de memória de usuário livre no início e o VitaSharp 341 MB
  (o port pede 188 vs 200 MB para a memória x86); o `SH4_AUTOSHOT` do port (glReadPixels) sai preto nos dois
  emuladores. O código de erro exato do Vita real para apps não está confirmado (o vitaGL só testa != 0)
- [x] Need for Speed Most Wanted (PCSE00089): no começo fica muito tempo na tela de aviso de salvamento ("This icon
  indicates the game is saving..." + copyright da EA) — 27 s inteiros na gravação, quase preta e a 30 fps — e aparece
  um erro de rede (relato do usuário, 07/11/2026, main 90b55bc, gravação `captures\PCSE00089_20261107_011440_tudo`). No log/HLE da gravação: o jogo
  tenta a rede da EA: `sceNetResolverStartNtoa` falha 3x (0x804101E1, DNS), `sceNetRecvfrom` 419x com EAGAIN, `sceNetSendto`
  falha, `sceNpInit` 0x80550001, `sceNetCtlInit` já iniciado; `SceNpManager::NID_FFBFC540` sem implementação (o mesmo do
  FIFA 13, outro jogo EA); `sceSysmoduleIsLoaded` respondendo "não carregado" 27973x (polling). Provável: o jogo espera
  tempos de rede/DNS antes de seguir. Ver o que um Vita sem rede responde (ex.: sceNetCtl desconectado faz o jogo pular o
  online na hora) e se o erro de rede deve ou não aparecer, comparar a duração da tela com um vídeo do Vita, e implementar
  o NID_FFBFC540 (registro de 64 bytes do SceNpManagerIpc; notas no item das funções HLE)
  Achados (07/11/2026, agente C, fix/bugs-games-17): o problema visível não é a rede. No Vita real (vídeo do YouTube
  `8puPotAAmVc`, `bugsC17_yt/nfs_sheet.png`) o boot mostra o logo da EA, o vídeo da cidade (Criterion),
  a tela branca "MOST WANTED" com o aviso (~6 s), a faixa de carregamento e só então o diálogo de PSN. Aqui a tela fica
  preta de 5 s a 50 s (`bugsC3_data/captures/PCSE00089_20261107_212738_tudo`, boot sem entrada): os filmes (SceMp4 +
  sceAvcdec, 720x400) decodificam certo (textura YUV420P2 0x8591AC00 com o logo da Criterion no `--trace-draws=300`,
  `bugsC17_nfs/rp/trace_300`), mas o jogo desenha o quadro do filme (draw de 6 índices, vs 6C022B03953DA3AA, thread
  "GXM Dispatch Thread", lr 0x8154E667) com o multiplicador de cor do shader (uniforms 20-23 do buffer padrão) em
  (0,0,0,0): o fade do filme fica em zero o tempo todo, por lógica do próprio jogo (o player de vídeo é código do jogo,
  entry 0x816361B5). Hipótese a conferir: o fade espera a posição do áudio do filme (NGS) ou outro estado do player.
  Próximo passo: achar quem grava esse multiplicador (backtrace `VITASHARP_BT_DRAW=6` já dá o lr) e o que ele lê
  08/11/2026 (agente C, fix/bugs-games-18, só achados): com o 2f7518c (vídeo sem low-delay) o boot continua preto
  (`bugsC18_out/nfs_new_frames`); o sceAvcdec decodifica 184 quadros sem erro. O thread "EAAudioCore Dac" acorda a
  cada 16 ms (grão de 768) como deve. Visto no caminho: depois do quadro ~1120 (~35 s) o jogo cai para 2-5 fps
  ("Hawaii_Main_Thread" com 4,95 M ciclos/quadro cobrados de HLE), provavelmente as chamadas de rede/PSN; medir
  com `--trace-hle-threads=Hawaii_Main_Thread` nesse trecho
  Corrigido (08/11/2026, agente C, fix/bugs-games-19): a causa era a CPU, não o player. O multiplicador de cor do
  desenho sai de uma cor empacotada (RGBA8) que o jogo abre em floats com `rev; vmov d1, r1, r1; vzip.8 d1, d1;
  vzip.16 q0, q0; vcvt.f32.u32 q0, q0, #32` (0x81158462). VZIP com Vd == Vm é UNKNOWN no ARM ARM, mas o Cortex-A9 fica
  com a segunda metade (o resultado gravado em Vm por último); o interpretador e os dois JITs (x64 e arm64) gravavam
  Vd por último, a primeira metade vinha do d0 antigo (zero) e toda cor assim virava 0: o filme, o fundo branco e o
  logo desenhavam pretos. Agora VZIP/VUZP gravam Vd e depois Vm (VTRN igual a antes); teste
  `VzipWithItselfKeepsTheSecondHalf`. Jogo rodando (boot sem entrada, `bugsC19_out/nfs_fix/frames/sheet.png`):
  logo da EA, vídeo da Criterion, tela branca MOST WANTED com o aviso e "TOUCH TO START", como no vídeo do Vita real;
  30 fps cravados; a queda para 2-5 fps não reproduz em 110 s headless (perfil: o Hawaii_Main_Thread gira em
  sceKernelGetProcessTimeWide ~25 mil vezes por quadro, um limitador de quadro do jogo, sem custo visível). Falta o
  usuário confirmar
  Visto depois do toque (achado, `bugsC19_out/nfs_play`, `nfs_cont`): o jogo abre o diálogo do Origin "You must be
  signed in to PlayStation Network and the EA servers..." (Error Code 80210000) com um spinner que não para; X em
  CONTINUE não faz nada em 60 s, O volta para o título e o diálogo volta. Sequência: o emulador responde PSN conectado
  (sceNetCtlInetGetState 3, IP 192.168.0.10, sceNpGetServiceState conectado, ticket do sceNpAuth), o jogo chama
  NID_FFBFC540 (sem implementação), resolve `gosredirector.ea.com` (falha na hora, 0x804101E1) e manda um UDP a cada
  15 s (sceNetSendto 0x80410133) com sceNetRecvfrom em EAGAIN a cada quadro. O Vita3K por padrão responde PSN
  desconectado, e aí o jogo pula o online. Decisão para o lead/usuário: responder "sem rede" para jogos que só tentam
  servidores reais (ou uma opção por jogo), ou descobrir o que libera o CONTINUE (provavelmente o fim do pedido do
  Origin, que fica esperando resposta do UDP)
  Decisão do usuário (08/11/2026): "sem rede para todos", como o Vita3K. Feito (agente C, fix/bugs-games-19): conta PSN
  desconectada por padrão (core, launcher, Android; "Simular conta PSN conectada"/--psn=on ainda liga) e, sem conta e
  sem internet real, sceNetCtlInetGetState DISCONNECTED e sceNetCtlInetGetInfo NOT_CONNECTED (só device e MAC). O
  diálogo de mensagem sem UI respondia SIM a tudo; um SIM/NÃO logo depois de um NetCheckDialog cancelado ("conecte-se à
  PSN... tentar de novo?") agora responde NÃO, como o usuário de um console sem rede: o 99Vidas, que repetia a
  pergunta para sempre, chega ao tutorial com as lutas desconectado (`bugsC19_out/v99b/frames/sheet_3.png`); o NFS não
  tenta mais DNS/UDP (só o NetCheckDialog, cancelado). **Aberto**: o NFS continua parado no diálogo do Origin depois do
  toque, com e sem conta (X, toque em CONTINUE, START não fazem nada; O volta ao título); o jogo lê o controle e não faz
  IO nesse estado, então não é rede: falta achar o que o diálogo espera (o spinner continua)
  08/11/2026 (agente C, fix/bugs-games-20, só achados, timebox): reproduz com a main (`bugsC20_out/nfs1`, `nfs8`):
  depois do toque o jogo carrega 26 BNDL da UI/mundo (f659-f803, último UI/SCREENS2/669547_TRANSITIONS.BNDL) e para
  de ler; o spinner continua, X (8 e 30 quadros), toque em CONTINUE (0.25, 0.30), START e O não mudam nada em 10 s
  (o O voltava ao título no relato anterior, depois de mais tempo). O X chega ao jogo: ele grava SafeMemory 4 vezes
  (f977-f981) logo depois. O Hawaii_Main_Thread só faz por quadro sceNpGetServiceState (SIGNED_OUT),
  sceNpManagerGetNpId (SIGNED_OUT), sceNpCheckCallback, sceNpBasicCheckCallback, _sceAppMgrGetAppState; os threads de
  job giram normalmente. Testado sem efeito: NetCheckDialog terminando OK (como o Vita3K) e sceNpManagerGetNpId
  devolvendo o id mesmo deslogado (como o Vita3K) — nenhum dos dois libera. Sem erro HLE novo (só IsLoaded(5),
  SignalSema OVF, NgsVoiceGetOutputPatch). No Vita3K o jogo chega ao gameplay (issue de compatibilidade #209), sem
  menção ao Origin. Próximo passo: achar no código do jogo o que o frontend espera depois do último BNDL (backtrace
  do Hawaii_Main_Thread em --bt, ou comparar com um log do Vita3K com --log-imports)
  Boot (logo EA, vídeo, título) confirmado pelo usuário (08/11/2026); seguem abertos o diálogo do Origin e os controles
  que não respondem
  Agente A (10/11/2026, main bfae931, headless do boot, sem estado): **resolvido no main**. Aviso de salvamento (tela
  branca MOST WANTED) por ~4-5 s e logo "TOUCH TO START" (Vita real ~6 s); depois do toque o diálogo do Origin aparece
  e o X em CONTINUE passa (a leitura de 2 amostras do b3c16ca), vem o filme de abertura e a corrida do prólogo (1,6 mi,
  Connors Bridge Road) responde a acelerador, ré e direção (`<scratchpad>\bA\proofs\nfs_boot_sheet.png`,
  `nfs_origin.png`, `nfs_prologue.png`). A troca de carro (item do `sceMp4GetNextUnit`, 64a15c4) segue sem validação ao
  vivo: guiar a corrida do prólogo às cegas por prints de novo não chegou ao fim (timebox); precisa de uma gravação do
  usuário depois do prólogo ou de um save com carros liberados
- [x] Soccer Tsuku (PCSG00198): o rosto na tela de criação demora muito para aparecer (relato do usuário, 06/11/2026,
  build de release 99396f5; log `20261106_221928_PCSG00198`, gravação `captures\PCSG00198_20261106_222037_tudo`). O jogo
  roda a 30 fps cravados e o log não tem erro; a gravação tem 2,5 s com uma parada de 131 ms (fileIo). Medir quanto o
  rosto leva aqui e no Vita real (vídeo), ver se é leitura de arquivo (FIOS/IO assíncrono), decodificação de textura ou
  geração na CPU, e corrigir se for o emulador. Conferir antes com o build da main atual (o usuário usava o release
  99396f5, mais antigo)
  07/11/2026 (agente C): a gravação mostra o FACE VIEWER vazio os 2,5 s inteiros; o estado dela é do 99396f5 e a main não
  carrega ("salvo por outra versão"), e chegar à tela de criação por roteiro não coube no tempo. Precisa de uma gravação
  nova com a main (estado carregável) para medir
  Agente A (10/11/2026, main bfae931, headless do boot, 28-30 fps): **não reproduz no main**. NEW GAME > secretária >
  passaporte do técnico (o rosto padrão já aparece no FACE) > ○ no FACE: o FACE VIEWER abre com o rosto montado já no
  primeiro print, 30 quadros (~1 s) depois do botão, e igual nos prints de 2, 4, 8, 16 e 30 s, com as três vagas do SAVE
  FACE preenchidas (`<scratchpad>\bA\proofs\soccertsuku_face_viewer.png`, `soccertsuku_passport.png`). O build do
  relato (release 99396f5) era bem mais antigo; falta o usuário confirmar no main
- [x] 99Vidas (PCSE00914): gravação do usuário no boot sem nenhum quadro (06/11/2026, release 99396f5,
  `captures\PCSE00914_20261106_221719_tudo`; AsyncReadManager do Unity rodando, quadro 0). A main tem o 1dfb147
  (sceNpManagerGetCachedParam, com o qual o jogo chega à abertura aqui): conferir com o build novo antes de investigar
  Conferido (07/11/2026, agente C, main 176f5d6): 	ests/scenarios/99vidas_boot.txt chega ao tutorial com as lutas a
  60 fps (quadro 3988, bugsC3_data/tests/99vidas_boot/sheet_3.png): o boot sem quadros era do release antigo
  Confirmado pelo usuário (08/11/2026)
- [x] Mudar o mapeamento de controle não vale com o jogo aberto (relato do usuário, 06/11/2026): o novo mapeamento só
  entra depois de sair e abrir o jogo de novo; o mesmo no Sistema do Vita (shell). Recarregar o perfil (`input.json`) na
  hora quando ele muda (launcher, tela de controles, F4), no desktop e no Android, sem reiniciar a sessão; validar trocando
  um botão com o jogo e com o shell abertos
  Correção feita (07/11/2026, agente C, afdb095): o input.json é relido quando muda (data do arquivo olhada a cada 500 ms) e
  cada Put/Remove muda uma revisão; o HostInput do desktop e o teclado e os controles do Android recompilam os perfis na
  hora (os controles do Android nem pegavam o perfil salvo antes de reconectar). Jogo rodando (Jak 1, janela oculta):
  "Input: input.json changed, mapping reloaded" logo depois de trocar o arquivo (bugsC17_out/input1/log.txt); o efeito
  nos botões está no teste ARemappingAppliesWithTheGameRunning. Falta o usuário trocar um botão com o jogo e o shell abertos
  Confirmado pelo usuário (07/11/2026)
- [x] Troféus antigos não aparecem no app Troféus (relato do usuário, 06/11/2026, main 90b55bc): o troféu só apareceu
  depois de abrir o app Troféus, e os troféus já salvos de outros jogos (ex.: Minecraft) não foram mapeados. Causa provável:
  a ponte só instala um título no servidor quando tem a assinatura de comunicação NP dele (`ux0:user/00/trophy/<id>.sig`),
  gravada só quando o jogo abre com esta versão; os jogos jogados antes não têm. Corrigir para não depender de abrir cada
  jogo: obter a assinatura de outro jeito (ela fica no próprio jogo: procurar no eboot/módulos de cada jogo instalado a
  estrutura SceNpCommunicationSignature que ele passa ao sceNpTrophyCreateContext, ou no pacote de troféus), migrar todos
  de uma vez sem avisos de "troféu conquistado" e validar com uma CÓPIA dos dados do usuário (todos os jogos com JSON
  aparecendo no app com as datas)
  Feito (07/11/2026, fix/system-apps-9, provas em `fw9_out/`): (a) a assinatura sai do próprio jogo: o eboot
  (e os módulos em sce_module) é decriptado com a licença e a constante de 0xA0 bytes com o magic 0xB9DDE13B mais perto
  do id "NPWRxxxxx" é a assinatura (a do Minecraft é idêntica à que o jogo passou; `Hle/Np/TrophySignature.cs`); é
  guardada no `.sig` na primeira migração. (b) sem avisos: o DebugUnlock (IPC 0x90036, np_trophy C1406504) exige um
  contexto do tipo 1 (contexto[+0x20], que o servidor grava a partir do 3.º argumento do IPC de criação); o np_trophy
  C80794CF cria esse contexto só com o NPCOMMID (sem assinatura), e a ponte dá os troféus por ele no handle de
  instalação. (c) datas: durante cada DebugUnlock as threads SceNpTrophyServer* do shell leem a data do JSON em
  sceRtcGetCurrentTick/_vshRtcGetCurrentSecureTick. (d) uma conexão por título: o servidor monta o app do cliente
  (sceAppMgrTrophyMount 300) uma vez por conexão, então o 2.º título lia a pasta do 1.º (0x80551619). Validado numa
  cópia dos dados do usuário: os 6 jogos com JSON e app instalado (Croixleur, Killzone, Silent Hill, Gravity Rush,
  Minecraft, FIFA 13) aparecem com CoD (`tro6/sheet.png`, `tro7/list2.png`), sem aviso durante a migração
  (`tro4/open*.png`), Minecraft "Inventariando" conquistado 2026/10/23 como no JSON (`tro7/mc_t1.png`). NPWR04671 e
  NPWR19711 não têm app instalado e ficam de fora.
  Confirmado pelo usuário (07/11/2026)
- [ ] Android: corrigir o flicker de textura nos jogos (relato do usuário, 06/11/2026; prioridade de bug do Android).
  Texturas que piscam no celular e não no desktop (ex.: plantas do Uncharted no Prologue, item acima na seção Android,
  dump `and_flicker`). O replay dos dumps no PC, inclusive com `--gpu-profile=mali`, não pisca, então a causa está no
  caminho da Mali real: bissecar no aparelho com chaves de runtime (MSAA, framebuffer fetch, formatos e mips de
  textura, compressão BC/ASTC decodificada, precisão dos shaders, profundidade, ordem de barreiras e uploads de
  textura, reaproveitamento de descritores), gravar tela e contar quadros com `flick.sh`/`tileflick.py` contra um
  vídeo do Vita real e corrigir de forma genérica. Validação de verdade no Killzone em gameplay (regra do Android) e
  depois Uncharted, F1 2011, God of War e Devious Dungeon sem nenhum quadro piscando. Com o agente de Android
- [x] Carregar estado duas vezes seguidas derruba o emulador (relato do usuário, 06/11/2026 02:45, PCSF00476, main
  Não reproduz no build da main (06/11/2026, perf/android-8, APK LLVM dev de 4a8bb94): Uncharted no início do
  Prologue (Drake parado, a cena das plantas), 12 s de gravação de tela: 0 quadros com flash (`tileflick.py`, blocos de
  16 px) em 196 quadros distintos a 0,75 e 1 em 179 a 1,0 (no canto dos controles na tela); as maiores mudanças de
  bloco são a animação do Drake, a grama muda continuamente. Killzone no gameplay (escadas do início), parado e
  andando: nenhuma alternância (o brilho da região só muda com o movimento da câmera). Provas:
  `and8_runs/flick_base.mp4`, `flick_s100.mp4`, `flick_kz_base.mp4`, `flick_kz_walk2.mp4`. Pausado a pedido
  do usuário (desempenho primeiro): falta saber de qual jogo/cena/APK é o relato para bissecar
- [ ] Android: um segundo load state logo depois do primeiro congela o jogo (06/11/2026, perf/android-8, APK LLVM dev
  db2d72d, Killzone PCSC00045, `VITASHARP_RES_SCALE=0.75;VITASHARP_RENDER_AREA_TRIM=1`). O roteiro mandou um só
  `loadstate@0:1` (`VITASHARP_ANDROID_INPUT_LIVE`); o log tem duas cargas: 15:57:41 "Estado do slot 1 carregado" e,
  11 s depois, "Não foi possível carregar o estado: thread 'PadThread' did not stop (in ); restart the game"; daí em
  diante 0 fps, o processo vivo (`and8_runs/log_RS75b.txt`, print `and8_tools/shots/kz_RS75b.jpg`). Duas
  coisas: (1) por que o passo do roteiro rodou duas vezes (o ScriptedPad relido depois da carga?); (2) uma carga que
  falha no meio deixa o jogo parado em vez de voltar ao estado de antes. Em 25 corridas com uma carga só, nenhuma
  falhou. Parecido com o item abaixo (desktop). Com o agente de bugs
- [ ] Android: o vídeo de abertura do F1 2011 (PCSE00002) pisca (relato do usuário, 09/11/2026, APK com todos os
  ganhos, perf/android-all 577bce4): quadros pretos (a imagem inteira com luma 0 entre pictures de luma 16) e pictures
  fora de ordem. A captura do usuário (`PCSE00002_20261109_185836_tudo`, 3º filme, jogo a 20 fps) tem 13 piscadas
  pretas e 7 pictures fora de ordem em 8 s. Perguntar ao lab se o gameplay também pisca. **Restrição do usuário: a
  correção não pode afetar a performance** (medir no celular, sem perda de fps).
  - O replay da captura no PC reproduz o defeito com qualquer build e qualquer chave do renderizador, inclusive o
    perfil do desktop e a main da manhã de 09/11. As pictures pretas ou velhas já estão na memória do guest quando o
    worker da GPU grava o quadro: o defeito é do lado do guest/HLE, não do renderizador.
  - A/B no celular (lab), abertura do F1 desde o boot a frio, 35 s, `VITASHARP_FLICKER_CHECK=1`, quadros marcados
    por execução (2 execuções):

    | Variante | Piscadas |
    |---|---|
    | NativeAOT f010c45 | 40 / 36 |
    | + `VITASHARP_DECODER_HISTORY=0` | 110 / 78 (pior) |
    | + `VITASHARP_TEX_CONTENT_CACHE=0` | 99 / 47 |
    | Mono LLVM do mesmo código (901f294) | 44 / 19 |
    | APK11 (mono-aot de ead401c, base ebfe72c) | 14 / 8 |

    Nenhuma das chaves `VITASHARP_UNIT_MEMO=0`, `VITASHARP_UB_ZERO=full`, `VITASHARP_HLE_GUEST_CODE=0`/`nogxmctx`,
    `DECODER_HISTORY` ou `TEX_CONTENT_CACHE` chega perto do APK11. O runtime não é a causa: o Mono pisca tanto quanto o
    NativeAOT. As linhas do sceAvcdec são iguais em todas as execuções: 1º decodificador com 150 chamadas, 0 pictures
    repetidas, 0 descartadas e 7-8 chamadas sem picture; 2º com 241 chamadas, 0 sem picture e 5-8 respondidas com a
    última picture do decodificador anterior.
  - **Hipótese de tempo:** o APK11 decodificava mais devagar (MediaCodec c2.android.avc em software, 16 ms por chamada
    contra 10-12 ms), então provavelmente é uma corrida que já existe há tempo e ficou mais visível com o jogo mais
    rápido.
  - **Hipótese a verificar (posse dos buffers):** o worker lê o buffer de saída do decodificador depois que o jogo já o
    reutilizou. O jogo teria limpado o buffer (luma 0) ou decodificado a próxima picture nele, enquanto o quadro na tela
    ainda o amostra. Ou nós escrevemos num buffer que o jogo ainda não devolveu, ou publicamos a picture antes dos
    pixels. A comparar com o Vita3K: quando o avPlayer/sceAvcdec devolve um frame buffer ao jogo, e se o
    `Videodec.Output` (fila `Ready`, `repeatIfLate`, `Recycle`) escreve num buffer que o jogo ainda usa. Também o
    sinal que o jogo espera antes de reutilizar um buffer (notificações e fila de display), que pode chegar cedo demais
    no nosso caminho.
  - Correção candidata, a medir antes: fotografar, no thread do jogo, a picture que está no endereço quando o draw que
    a amostra é submetido (o que a GPU do Vita leria logo depois) e fazer o worker usar essa foto quando a memória já
    mudou, no lugar das heurísticas do `DecoderOutputs.EarlierFor`. Só custa quando há histórico de decodificador.
    Medir o fps no celular antes e depois (restrição acima). Com o agente de bugs
  - Do item duplicado do laboratório (juntado aqui em 11/11/2026): com `VITASHARP_DISPLAY_EARLY_SIGNAL=1` (a622069) a
    contagem cai para 15/13, contra 32/69 sem ela (o tempo do flip/apresentação pesa); o APK mono-aot decodifica a
    16 ms por chamada contra 10-11 ms nos outros, então a contagem menor dele pode ser efeito de tempo. Próximo passo
    do lab: um mono-aot da main com o mesmo detector, depois bisectar (logs `lab\flicker\{A..J}_{0,1}.log`, roteiro
    `lab\flicker\intro_ab.sh` no scratchpad do agente).
  - 12/11/2026 (agente A1, fix/android-video, correção feita e medida no celular; falta o usuário confirmar). Como o
    Vita faz: `sceAvcdecDecode` é síncrona (vita-headers; o Vita3K manda a unidade e recebe a picture na mesma chamada,
    `numOfOutput` só sobe quando sai uma), e a primeira chamada de um stream já responde com a picture do IDR. Causa no
    Android: o decodificador de software (libavc no `c2.android.avc.decoder`) segura as primeiras pictures até chegarem
    mais unidades (o atraso de exibição dele vem do DPB quando o SPS não traz `bitstream_restriction`: o clipe sintético
    de 64x64 só soltou a primeira picture na chamada 18; os filmes do F1 têm B-frames e limite próprio de 2) e a
    inicialização do codec leva 0,1-0,7 s; o HLE respondia essas chamadas vazias com nada (buffer não escrito: os
    quadros pretos de luma 0/16) ou com a última picture do filme anterior (o quadro velho). Correção, no mecanismo do
    MediaCodec (todos os jogos com SceAvcdec/SceAvPlayer): (1) a primeira unidade IDR de cada stream (criação, Stop,
    Flush, volta do filme) sai na própria chamada: fim de stream para o decodificador soltar o que segura, flush, a mesma
    unidade de novo como referência (a segunda cópia é descartada pelo pts) — 13-55 ms no celular, contra 0,6 s +
    8 chamadas vazias antes; (2) SPS sem limite de reordenação recebe `max_num_reorder_frames = 0`
    (`H264ReorderLimit`; desfeito com replay do GOP se aparecer slice B depois do limite); (3) depois da primeira, as
    pictures saem como antes (sem esperar cada uma: esperar todas custou 7% de fps, `VITASHARP_MEDIACODEC_SYNC=all`);
    (4) AAC em ADTS no Android saía mudo (MediaCodec com `KEY_IS_ADTS`: 25 de 25 quadros em silêncio no clipe de
    teste): agora vai como AAC cru com o AudioSpecificConfig do cabeçalho. Medido no Redmi Note 14 (APK dev nativeaot,
    abertura do F1 desde o boot, 35 s, 3 rodadas alternadas, `VITASHARP_MEDIACODEC_SYNC=0` = o código antigo):
    chamadas sem picture 0/0/0 contra 8/8/8 no 1º filme, respostas com a picture do filme anterior 0 contra 5-6 no
    2º, fps 22,0/22,3/21,2 contra 21,5/21,8/22,0 (sem perda), duração dos filmes igual (5,0-5,1 s e 7,8-8,1 s). O
    detector de flicker ainda marca 10-14 quadros por execução no novo contra 3-8 no antigo, quase todos mudanças
    pequenas de um bloco 4x4 nos primeiros 3 s de cada filme (repetições do primeiro quadro enquanto o libavc enche
    o pipeline); nenhum quadro inteiro preto nas 9 execuções de hoje, nem no antigo (o pisca de 17 quadros
    alternando luma 97/16 no início do 3º filme apareceu 1 vez em 4 no código antigo). Falta: comparar com um vídeo do
    Vita de verdade o começo de cada filme e confirmar com o usuário. Teste: `MediaParityTests`,
    `H264ReorderLimitTests`, `AndroidMediaDriverTests` (SynchronousOutput); paridade no celular:
    `build/android-media-parity.sh` (vídeo do Android idêntico e na mesma ordem que o FFmpeg do PC, primeira picture
    na chamada 0; o código antigo dava a primeira na chamada 18). Logs: `<scratchpad>\a1\runs\f1_v5`.
- [ ] Android: crash do GC do Mono logo depois de um load state (06/11/2026, perf/android-8, Killzone): SIGSEGV
  (SEGV_ACCERR) na thread `SGen worker` (libmonosgen-2.0.so) 1 s depois de "Estado do slot 1 carregado"
  (`and8_runs/crash_B1_sgen.txt`, log `log_B1.txt`); de novo às 16:01 (`VITASHARP_MSAA=off`), 1 s depois da
  carga, abort na thread `Shader worker 1`: `* Assertion at sgen-los.c:381, condition '!section->free_chunk_map [i]'
  not met` (`crash_M2_shader.txt`). Duas vezes em ~28 cargas, sempre logo depois de carregar: o heap de objetos
  grandes do GC corrompido, provável escrita fora do lugar durante/depois da carga (buffers do renderer recriado,
  memória do guest, spans sobre arrays). Com o agente de bugs
- [x] Carregar estado duas vezes seguidas derruba o emulador (relato do usuário, 06/11/2026 02:45, PCSF00476, main
  1ae237f, log `20261106_024439_PCSF00476`): o 1º F7 carrega; o 2º também ('GPU backend replaced', relógio do guest
  voltando 16 s), o jogo desenha 3 cenas MSAA e o log para; o Windows registra TDR do driver da GPU (LiveKernelEvent 141)
  e o processo cai com c0000005 em coreclr.dll. Corrigir o renderer recriado e garantir que uma falha da GPU nunca
  derrube o processo; validar 10 loads seguidos. Com o agente de bugs C
  **Corrigido** (agente C dia 10, 06/11/2026, fix/bugs-games-9; validado aqui, falta o usuário confirmar): cada load
  destruía e recriava a instância Vulkan, o device, a surface e o swapchain da janela, numa thread de trabalho. No
  driver NVIDIA isso corrompia o heap nativo (c0000374 no vkCreateInstance seguinte, 2 de 2 execuções no 1º load) e,
  com a destruição na thread da janela, ainda dava c0000005 em coreclr.dll dentro do vkQueuePresentKHR logo depois de
  um load (1 em ~25); o TDR (LiveKernelEvent 141) vem **depois** da queda, do processo morrendo com trabalho na GPU.
  Agora o renderer novo herda instância, device, surface e swapchain (o swapchain é recriado a partir do antigo, como
  num resize; `VulkanRendererOptions.KeepDevice`, só na janela), a troca roda na thread da janela, e um device
  perdido vira aviso no título (`A GPU parou de responder...`) e um device novo no próximo load. Com janela oculta
  (chave de teste nova `VITASHARP_HIDDEN_WINDOW=1`, sem som): Tearaway 12 loads, FIFA 13 10, Uncharted 10 (+1 LoadExec),
  sem queda nem TDR, 30/60 fps depois (`bugsC9_out/tear_w7`, `fifa_w2`, `uc_w2`; prints em
  `bugsC9_proof`). O relógio do guest voltando é o tempo do estado salvo (esperado). Limite: os loads foram
  em menus/cenas (sem gameplay roteirizado); device lost não foi provocado de verdade
  Confirmado pelo usuário (06/11/2026): ok
- [x] NGS: módulo de envelope (agente C dia 9, fix/bugs-games-9, commit WIP "NGS envelope"; regressão completada no dia 10).
  O envelope era passthrough e `sceNgsVoiceKeyOff` parava a voz no update seguinte; o SDK diz que depois do key off
  "o processamento continua até a voz terminar" (release). O Jak 1 usa racks Scream com envelope (pontos 0→1 em 0 ms,
  sustain 1 ou 0,5625, release de 23 ms a 12 s) e dá key off com o som ainda alto: cada som era cortado no meio da onda.
  Feito: pontos, sustain no último ponto, loop, release linear (curva CURVED aproximada como linear) e a voz termina com
  o release; `VITASHARP_NGS_ENVELOPE=0` volta ao passthrough. Jak 1 gameplay (`jak_g2`): cortes bruscos (som alto→0) nas
  vozes 152 → 2, 143 vozes terminam pelo release, 20 fps iguais, 0 HLE-ERR (`bugsC9_out/jak_a2` x `jak_e1`,
  WAVs por voz em `audio/`; `bugsC9_tools/asum.py`). Regressão de ritmo feita: DD 59,9/59,9, F1 59,9/59,9, UC 59,8/59,9,
  KZ 29,9 (ref. não rodou); **faltam KZ ref, FIFA e GoW** (`bugsC9_tools/regress.ps1`, com --audio-dump: conferir que
  nenhum som some por voz presa em Finalizing durante o release). Os "clicks" do analisador de captura no Jak (~41 por
  ocorrência) são outra coisa: um som de ruído agudo (energia em 10-14 kHz, idêntico em todas as vozes e execuções,
  provável passo/impacto do próprio banco de sons); o decodificador ADPCM bate com o do Vita3K e os bancos decodificados
  fora não têm saltos (`adec.py`); não identifiquei o banco exato (o dump por endereço de buffer reaproveitado ficou
  velho). O clipping de 0,12% da gravação do usuário não reproduz aqui (0,002% no gameplay do `jak_g2`)
  Agente C dia 10 (06/11/2026): **regressão completa, fica ligado por padrão** (mesmo build, `VITASHARP_NGS_ENVELOPE=0`
  como referência, `bugsC9_tools/regress.ps1`): Killzone 29,9/29,9 fps (p99 35,0/36,7 ms), FIFA 13 59,6/59,5, God of War
  59,8/59,8 (`bugsC9_out/r_kz_on_nd`, `r_kz_off_nd`, `r_fifa_on`/`off`, `r_gow_on`/`off`). Nenhum som
  some: a mistura gravada (`--audio-dump`) tem o mesmo RMS (KZ -23,4/-23,5, FIFA -23,1/-23,1, GoW -25,1/-25,2 dBFS) e, em
  FIFA e GoW, 0-1 de 133-150 meios-segundos diferem mais de 6 dB; com o modo de depuração, nenhum erro novo de NGS
  (`sceNgsVoicePlay` numa voz ainda em release seria o sintoma; `bugsC9_tools/onoff.py`). Não ouvi (sem som no PC
  do usuário); a medida é por RMS
- [x] Jak and Daxter Collection (PCSA00080): validar o save state no gameplay do Jak 1 (salvar, fechar, abrir e
  carregar com F7/lista do launcher; estados salvos depois do LoadExec) quando o usuário puder jogar com a câmera
  corrigida (f3259f6, DelayThreadCB). Pedido do usuário (06/11/2026): é o único item do Jak que fica aberto
  Confirmado pelo usuário (06/11/2026): save state, câmera e tela travada do Jak 1 corrigidos. Aberto à parte: o fundo
  piscando quando abre o menu de pausa (gravação `PCSA00080_20261106_183708_tudo`, com o agente de bugs C)
- [x] Injustice: Gods Among Us Ultimate Edition (PCSE00271) não roda (relato do usuário, 06/11/2026, main 0d298b4,
  prioridade máxima; gravação `captures\PCSE00271_20261106_020056_tudo`): nenhum quadro apresentado (watchdog STALL
  no frame 1). As threads `AsyncIOSystem` e `NRSMovieFileAsyncServer` chamam `sceKernelWaitEvent(0x0 / 0xFFFFFFFF,
  0x1000000, ...)` (lr 0x8107DF21) em laço e recebem 0x80028201 (UNKNOWN_EVENT_ID) 692.895 vezes em 15 s; a `main`
  fica esperando o IO assíncrono. Provável: a criação do objeto de evento antes falhou ou não existe no emulador;
  a `Scaleform Render Thread` nunca iniciou (Dormant, pc 0). Com o agente de bugs C
  **Corrigido** (agente C dia 9, 06/11/2026, fix/bugs-games-9; validado aqui, falta o usuário confirmar): (1) o jogo cria
  os eventos do pool de threads e do IO assíncrono do motor NRS com `sceKernelCreateSimpleEvent("InName", 0x900)` e
  o emulador recusava o bit 0x800 (`SCE_KERNEL_ATTR_NOTIFY_CB_WAKEUP_ONLY`) com ILLEGAL_ATTR: o uid guardado era o
  código de erro e as threads giravam no `sceKernelWaitEvent`; agora é aceito (como no Vita3K). (2) Em seguida a
  `RenderingThread` morria numa exceção do host em `sceKernelGetThreadInfo` (tamanho da estrutura acima de 0x80, ao
  esperar o fim da `Scaleform Render Thread`, que roda e termina normalmente); agora devolve NO_MEMORY como os outros
  `Get*Info` e o Vita3K. O jogo passa pelos avisos, abertura, menu principal, pergunta do tutorial e a luta do
  tutorial (Batman x Superman, andar e golpes respondem) a 55-60 fps (`bugsC3_data/tests/inj_menu/sheet.png`,
  `inj_fight/sheet_2.png`, logs `bugsC9_out/inj_m2`, `inj_f1`; 0 HLE-ERR)
  Confirmado no jogo pelo usuário (06/11/2026, main d6c1fb7): roda
  Confirmado pelo usuário (06/11/2026): ok
- [x] Implementar todas as funções HLE que aparecem como não implementadas nos logs do usuário (pedido do usuário,
  06/11/2026; levantadas de 21 logs e gravações em `%APPDATA%\VitaSharp\logs` e `captures`, 90 funções). Cada uma com o
  comportamento do Vita (Vita3K e o firmware como referência), não um stub que devolve 0 sem pensar; as que só fazem
  sentido com rede/PSN/hardware ausente devolvem o erro que o Vita devolve nesse caso.
  Nova varredura (10/11/2026, agente B, fix/bugs-b-2): os 11 logs e as 13 gravações que estão hoje na pasta do usuário
  (08-09/11) têm só 5 chamadas não implementadas, todas de uma sessão do Dead Space de 08/11 21:23, antes do kubridge
  em HLE: kubridge kuKernelRegisterExceptionHandler, kuKernelAllocMemBlock (NID_2EF7C290, 3x), kuKernelCpuUnrestrictedMemcpy
  (NID_91D9CABC, 4x), SceVshBridge _vshSblGetSystemSwVersion e _vshKernelSearchModuleByName (1x cada). As cinco já estão
  na main (Kubridge.cs, VshBridge.Homebrew.cs) e as sessões seguintes do Dead Space (09/11 00:06 e a gravação 00:21) não
  têm nenhuma; os `hle_errors.txt` das gravações só têm retornos de erro normais (timeouts de espera, módulo não
  carregado). O FFBFC540 abaixo está feito (NpManager.Batch9.cs, IPC 0xB0025) e o 16B1028E do PhotoExport vem do
  módulo do firmware nos programas do sistema (Emulator.FirmwareServiceClients). Falso positivo achado: o
  `sceGxmTextureGetType` aparece como "erro" 547x no `hle_errors.txt` do SH: BoM porque devolve o tipo
  SCE_GXM_TEXTURE_TILED (0x80000000), que cai na faixa dos códigos de erro; não mexi (o agente HLE B está mexendo no
  HleFramework). Jogos:
  - SceNpManager::NID_FFBFC540 (0xFFBFC540) (PCSE00093)
    Achados (06/11/2026): no np_manager.suprx do firmware é um getter igual ao sceNpManagerGetNpId (NULL ->
    0x80550503; senão IPC 0xB0025 ao serviço SceNpManagerIpc do shell com 64 bytes de saída). O FIFA 13 chama logo
    depois do GetNpId (objeto+0x28) com objeto+0x4C; retorno != 0 leva ao estado "-npo" (NP offline), 0 ao "+npo".
    O conteúdo dos 64 bytes não foi identificado (o tratador 0xB0025 fica no shell, atrás do servidor IPMI em C++);
    hoje o "não implementado" devolve 0 com o buffer intocado, que é o caminho que o jogo já usa
  - ScePspnetAdhoc::sceNetAdhocInit (0x87805E13) (PCSE00056) — feita (feat/hle-unimplemented)
  Sistema do Vita (shell e apps):
  - SceAppMgr::sceAppMgrIsDevelopmentMode (0x7D4F8F83) — feita (feat/hle-unimplemented)
  - SceAppMgr::sceAppMgrIsGameBudgetAppPresent (0xE134F5A0) — feita (feat/hle-unimplemented)
  - SceAppMgr::sceAppMgrSetRecommendedScreenOrientationActivated (0x75876896) — feita (feat/hle-unimplemented-2)
  - SceAppMgrUser::sceAppMgrCheckRifGD (0x6E5CBCA2) — feita (feat/hle-unimplemented-2)
  - SceAppMgrUser::sceAppMgrMmsMount (0xF4730BA8) — feita (feat/hle-unimplemented-2)
  - SceAppMgrUser::sceAppMgrSetBackRenderPortOwner (0x7149016E) — feita (feat/hle-unimplemented-2)
  - SceCodecEngineUser::NID_9A5E7B2F (0x9A5E7B2F) — feita (15dc989, 07/11/2026; conferido em 08/11/2026)
  - SceCodecEngineUser::NID_9AD7E66F (0x9AD7E66F) — feita (15dc989, 07/11/2026; conferido em 08/11/2026)
  - SceErrorUser::sceErrorHistoryPostError (0xBC416CFA) — feita (feat/hle-unimplemented-2)
  - SceHttp::NID_855D5254 (0x855D5254) — feita (feat/hle-unimplemented-2)
  - SceModulemgr::sceKernelInhibitLoadingModule (0x6CED1F63) — feita (feat/hle-unimplemented-2)
  - SceMotion::NID_A408BC69 (0xA408BC69) — feita (feat/hle-unimplemented-2)
  - SceMotionDev::sceMotionDevGetCurrentMagnCalibData (0xAAB4B41F) — feita (feat/hle-unimplemented-2)
  - SceMotionDev::sceMotionDevGetDeviceInfo (0x1F1EFEFB) — feita (feat/hle-unimplemented-2)
  - SceMotionDev::sceMotionDevGetGyroCalibData (0x74555D91) — feita (feat/hle-unimplemented-2)
  - SceMotionDev::sceMotionDevGetMeasMode (0xEA95D3C9) — feita (feat/hle-unimplemented-2)
  - SceMotionDev::sceMotionDevReadForMagnCalib (0xCC4FDA6A) — feita (feat/hle-unimplemented-2)
  - SceNetCtl::NID_777FE4C2 (0x777FE4C2) — feita (feat/hle-unimplemented-2)
  - SceNetCtl::NID_AC326DB7 (0xAC326DB7) — feita (feat/hle-unimplemented-2)
  - SceNetCtl::NID_C33D7374 (0xC33D7374) — feita (feat/hle-unimplemented-2)
  - SceNetCtl::NID_C61F3E96 (0xC61F3E96) — feita (feat/hle-unimplemented-2)
  - SceNetInternal::sceNetInternalIcmConnect (0x93F2FF08) — feita (feat/hle-unimplemented-2)
  - SceNetPsForSyscalls::sceNetSyscallClose (0x1EBC2E28) — feita (feat/hle-unimplemented-2)
  - SceNetPsForSyscalls::sceNetSyscallControl (0xEA0C1B71) — feita (feat/hle-unimplemented-2)
  - SceNetPsForSyscalls::sceNetSyscallGetIfList (0x878274CE) — feita (feat/hle-unimplemented-2)
  - SceNetPsForSyscalls::sceNetSyscallIcmConnect (0x8C3FBC87) — feita (feat/hle-unimplemented-2)
  - SceNetPsForSyscalls::sceNetSyscallIoctl (0x310F0725) — feita (feat/hle-unimplemented-2)
  - SceNetPsForSyscalls::sceNetSyscallSendto (0x39796C01) — feita (feat/hle-unimplemented-2)
  - SceNetPsForSyscalls::sceNetSyscallSocket (0x81A120BE) — feita (feat/hle-unimplemented-2)
  - SceNpBasic::NID_6F1B58F9 (0x6F1B58F9) — feita (feat/hle-unimplemented-2)
  - SceNpBasic::NID_B414E768 (0xB414E768) — feita (feat/hle-unimplemented-2)
  - SceNpDrm::_sceNpDrmGetFixedRifName (0xE935B0FC) — feita (feat/hle-unimplemented-2)
  - SceNpManager::NID_77DE17D4 (0x77DE17D4) — feita (feat/hle-unimplemented-2)
  - SceNpManager::NID_B24FC028 (0xB24FC028) — feita (feat/hle-unimplemented-2)
  - SceNpManager::NID_C89E0B94 (0xC89E0B94) — feita (feat/hle-unimplemented-2)
  - SceNpManager::NID_CFAEB050 (0xCFAEB050) — feita (feat/hle-unimplemented-2)
  - SceNpManager::NID_DDF30B0E (0xDDF30B0E) — feita (feat/hle-unimplemented-2)
  - SceNpManager::NID_E45E4003 (0xE45E4003) — feita (feat/hle-unimplemented-2)
  - SceNpWebApi::NID_4699EF17 (0x4699EF17) — feita (feat/hle-unimplemented-2)
  - SceNpWebApi::NID_E83CE7F1 (0xE83CE7F1) — feita (feat/hle-unimplemented-2)
  - SceNpWebApi::NID_E8FBA9B8 (0xE8FBA9B8) — feita (feat/hle-unimplemented-2)
  - ScePhotoExport::NID_16B1028E (0x16B1028E)
  - SceSblQafMgr::sceSblQafMgrIsAllowNpFullTest (0x72168C6E) — feita (feat/hle-unimplemented)
  - SceSslInternal::sceSslInternalGetCertificateAuthority (0x064DFC99) — feita (feat/hle-unimplemented-2)
  - SceSysmem::sceKernelCheckModelCapability (0x0144FBD9) — feita (feat/hle-unimplemented)
  - SceTriggerUtil::NID_28EF51DA (0x28EF51DA) — feita (feat/hle-unimplemented-2)
  - SceVshBridge::_vshAppMgrRegisterPath (0x179965E7) — feita (feat/hle-unimplemented-2)
  - SceVshBridge::_vshSblSsGenerateAppKey (0x82F2FBA8) — feita (feat/hle-unimplemented-2)
  - SceVshBridge::vshSblQafMgrIsAllowLiblocationChangeModel (0x58E1DE33) — feita (feat/hle-unimplemented)
  - SceVshBridge::vshSblQafMgrIsAllowNearTest (0xD0D4EB67) — feita (feat/hle-unimplemented)
  - SceWlan::sceWlanGetConfiguration (0x70AF9BD6) — feita (feat/hle-unimplemented-2)
  Mais 37 achadas depois (06/11/2026, mesmos logs, sessões novas do Sistema do Vita):
  - SceAppMgrUser::sceAppMgrSetBgmProxyApp (0xC42F41E9) — feita (feat/hle-unimplemented-2)
  - SceBt::sceBtGetRegisteredInfo (0xB6C9A094) — feita (feat/hle-unimplemented-2)
  - SceBt::sceBtSetConfiguration (0x9B1F2964) — feita (feat/hle-unimplemented-2)
  - SceHid::sceHidKeyboardSetIntercept (0x5D727742) — feita (feat/hle-unimplemented-2)
  - SceHid::sceHidKeyboardUnregisterEnumHintCallback (0x8068D61E) — feita (feat/hle-unimplemented-2)
  - SceHid::sceHidKeyboardUnregisterReadHintCallback (0xF73C5FBA) — feita (feat/hle-unimplemented-2)
  - SceHttp::NID_F30073EC (0xF30073EC) — feita (feat/hle-unimplemented-2)
  - SceNetCtl::NID_0CC36330 (0x0CC36330) — feita (feat/hle-unimplemented-2)
  - SceNetCtl::NID_147B054C (0x147B054C) — feita (feat/hle-unimplemented-2)
  - SceNetCtl::NID_6A56FC3A (0x6A56FC3A) — feita (feat/hle-unimplemented-2)
  - SceNetCtl::NID_713394C0 (0x713394C0) — feita (feat/hle-unimplemented-2)
  - SceNetCtl::NID_AB1462C5 (0xAB1462C5) — feita (feat/hle-unimplemented-2)
  - SceNetCtl::NID_BC0563A0 (0xBC0563A0) — feita (feat/hle-unimplemented-2)
  - SceNetCtl::NID_E8E8DF18 (0xE8E8DF18) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_25C58D33 (0x25C58D33) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_45ACE466 (0x45ACE466) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_4C406B2A (0x4C406B2A) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_5AB115CE (0x5AB115CE) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_5D8B5B59 (0x5D8B5B59) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_749C8571 (0x749C8571) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_86405E79 (0x86405E79) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_8ECAE666 (0x8ECAE666) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_AA090F5E (0xAA090F5E) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_BC89C4D3 (0xBC89C4D3) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_BFDC7B9F (0xBFDC7B9F) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_C7B04A45 (0xC7B04A45) — feita (feat/hle-unimplemented-2)
  - SceNpCommerce2::NID_D09A38DA (0xD09A38DA) — feita (feat/hle-unimplemented-2)
  - SceNpCommon::sceNpCommonGetNpEnviroment (0xAD6403E0) — feita (feat/hle-unimplemented-2)
  - SceNpManager::NID_29C9B8A5 (0x29C9B8A5) — feita (feat/hle-unimplemented-2)
  - SceNpManager::NID_ED12AF16 (0xED12AF16) — feita (feat/hle-unimplemented-2)
  - SceSblQafMgr::sceSblQafMgrIsAllowAllDebugMenuDisplay (0x66843305) — feita (feat/hle-unimplemented)
  - SceSblQafMgr::sceSblQafMgrIsAllowLimitedDebugMenuDisplay (0xC456212D) — feita (feat/hle-unimplemented)
  - SceSblQafMgr::sceSblQafMgrIsAllowMinimumDebugMenuDisplay (0xA156BBD2) — feita (feat/hle-unimplemented)
  - SceSblQafMgr::sceSblQafMgrIsAllowNonQAPup (0xB5621615) — feita (feat/hle-unimplemented)
  - SceUdcd::NID_F1A3690B (0xF1A3690B) — feita (feat/hle-unimplemented-2)
  - SceVshBridge::_vshSblAimgrGetPscode (0x1AF2CFC9) — feita (feat/hle-unimplemented-2)
  - SceWlan::sceWlanSetConfiguration (0xDBE8AEF2) — feita (feat/hle-unimplemented-2)
  Achadas na rodada 2 (06/11/2026, feat/hle-unimplemented-2):
  - SceNetCtl::NID_719DD1DA (0x719DD1DA) (configuração inicial) — feita (feat/hle-unimplemented-2)
  - SceAppMgrUser::sceAppMgrSetRecommendedScreenOrientationForShell (0x219043FF) (o shell chega nela agora que o
    NID_A408BC69 devolve a orientação) — feita (feat/hle-unimplemented-2)
  - SceVshBridge::vshSblQafMgrIsAllowSystemAppDebug (0x31AACDD2) (Content Manager) — feita (feat/hle-unimplemented-2)
  - SceAppMgr::sceAppMgrDeclareSystemChatApp (0x77EE891D), sceAppMgrEnableDuckingOnSystemChat (0x5AB230DE) e
    sceAppMgrSetInfobarStateForCommonDialog (0xD6758B4E) (varredura dos apps do sistema) — feitas (feat/hle-unimplemented-2)
  - SceVshBridge::vshSysconIsShowMode (0xFF244636) — feita (feat/hle-unimplemented-2)
  - SceNpManager::NID_351A74C2, NID_997CD5A7; SceNpWebApi::NID_159396F0, NID_19062C5A, NID_1BDB76C0, NID_83E94928,
    NID_A11FBA1A (Messages), NID_BED99F66; SceProcessmgr::sceKernelCDialogSessionClose (0xDB4CC1D0) (varredura) —
    feitas (fix/bugs-games-12; comportamento do np_manager/np_webapi FW 3.74: tabela de 4 callbacks com 0x80550504,
    argumentos 0x80552902, ids locais; o CDialogSessionClose roda no diálogo "Sign up" do PS4 Link)
  - Alcançadas agora que a rede do kernel e o pscode respondem: SceNetInternal::NID_235DE96C, NID_7493FB0C,
    NID_DE8DD55E (PS3 Remote Play, PS4 Link); SceRegMgrService::sceRegMgrSrvCnvRegionPsCode (0x94D008D2) e 15 NIDs de
    SceNpCommerce2 (145137BE, 1A53A505, 1BFF9C73, 2DC2A0F9, 34194245, 4329A980, 499732CA, 4D00AA32, 624B7A50, 64E27331,
    71ADF5A6, 7E3C9196, 9B444B7C, C1542070, C737A929) (PS4 Link) — feitas (fix/bugs-games-12). SceNetInternal do
    libnet: 235DE96C = a interface IfNames[i] (lo0, eth0..., wlan0...) existe? (só lo0; -1 = a lista do kernel),
    DE8DD55E/7493FB0C = ler/gravar a configuração 0x18 da pilha (controle 0x10000018/0x20000018); validadas no PS3
    Remote Play (pergunta eth0/wlan0, chega à tela de registro) e no PS4 Link (`bugsC12_out/s_ps3rp`,
    `s_ps4link2`, 0 não implementadas). CnvRegionPsCode = código de produto por região (0x103 J1, 0x104 US ... 0x10D
    CN; só o shell, os outros ENOSYS), como o SceRegistryMgr. Os 15 do SceNpCommerce2 seguem os wrappers do
    np_commerce2 (argumentos 0x80550F03; pedidos ao servidor = servidor inalcançável; leitores de resultado rejeitam,
    não há resultado). CnvRegionPsCode e os 15 do Commerce2 não foram alcançados nesta rodada (o "Sign Up" do PS4 Link
    não abre o NPXS10018: sceAppMgrLaunchAppByUri ainda não lança, ver Câmera)
  - SceCoredump::sceCoredumpRegisterCoredumpHandler (0x031DC61E) (PCSF00476) — feita (fix/bugs-games-12; NULL
    0x800A0000, segundo registro 0x800A0002; o Tearaway registra no Init e chega ao menu, `bugsC12_out/s_tear/t2.png`)
  - SceNpSnsFacebook::sceNpSnsFacebookInit (0x80557AA0) (PCSF00476) — feita (fix/bugs-games-12, com Term e os
    pedidos: sem conta do Facebook os tokens nunca existem)
  - SceNpCommon::NID_0624AE44 (PS4 Link, módulo de atividade) — feita (fix/bugs-games-12): a versão do sistema em
    texto ("3.74", snprintf "%d.%02d" do BCD de sceKernelGetSystemSwVersion), devolve o tamanho
  - SceAppMgr::sceAppMgrIsPidShellAndCrashed (0xC648EA9B) — feita (fix/bugs-games-12): o cliente libipmi do
    SceDbUtilDcn consulta a cada 0,5 s com o pid do servidor (agora que sceKernelGetMsgPipeCreatorId aceita nome); 0
  - Estranho (06/11/2026, não é destas funções: igual no build da main `user_test_build`): no Mensagens, o Sign Up
    (NPXS10018) abre com "An error has occurred. (NW-8941-2)" em vez de "Create / Use an Existing Account" que o
    checklist do `docs/notes/system-apps.md` espera (`bugsC12_out/base_messages/signup.png` e `s_messages/signup.png`);
    o NID_86405E79 do Commerce2 devolve 0x80431063 (servidor inalcançável) logo antes
    (06/11/2026, fix/bugs-games-13: o mesmo NW-8941-2 aparece pelo PS4 Link agora que o "psnreg:" abre o Sign Up,
    `bugsC13_out/signup1/sheet.png`. No traço HLE do SceSignup a única chamada com erro antes do aviso é esse
    NID_86405E79 = 0x80431063: o emulador diz rede conectada e conta local, e o servidor não responde, que é o que o
    Vita mostra com rede mas sem os servidores. "Create / Use an Existing Account" só viria com o servidor respondendo
    (ou num Vita sem rede, que pararia antes na checagem da conexão); não mudei, é decisão de modelo de rede, não defeito)
  Kernel (rodada 2): com a rede do kernel respondendo, o Settings passou a abrir e fechar sua conexão IPMI com o
  SceMtpService do shell, e o servidor caía em `[SceIpmi] INTERNAL ASSERTION FAILURE (0x80028101)`: o cliente apaga seus
  semáforos antes de o servidor fechar os handles que abriu por nome, e aqui o Delete tirava o objeto da tabela na hora
  (3 de 3 varreduras). Agora semáforo, event flag e mutex apagados (ou do processo que terminou) com handles abertos
  por outro processo ficam (sem nome) até o último Close, como os msgpipes já ficavam. A mesma asserção apareceu uma vez
  no SceImeServerDispatch na configuração inicial com a main (provavelmente a mesma causa)
  Notas da rodada 2 (referência: libnetctl, libnet, np_manager, np_basic, np_webapi, np_commerce2, np_common,
  libhttp, libssl, trigger_util, driver_us e os módulos do bootimage.skprx do 3.74, desmontados): o NID_A408BC69 é
  uma função pura do driver_us (orientação básica com histerese) portada; os sceNetSyscall* usam a mesma tabela de
  sockets do SceNet com um kernel só com lo0 (o shell configura lo0, rota e lê as estatísticas). Aproximações
  conscientes (sem o layout/servidor de verdade): os pedidos de conta do SceNpManager do sistema (B24FC028,
  CFAEB050, E45E4003, DDF30B0E, ED12AF16) devolvem SIGNED_OUT (o sistema não tem conta; o servidor IPC fica no shell
  em C++, como no FFBFC540); o SceNpCommerce2 do Sign Up aceita os ajustes e falha nos pedidos à loja; o
  sceSslInternalGetCertificateAuthority devolve NOT_FOUND (o libssl de verdade serve vs0:data/external/cert/CA_LIST.cer);
  SceMotionDev devolve dados de calibração vazios e "magnetômetro não calibrado"; o _vshSblSsGenerateAppKey deriva a
  chave por SHA-256 do pedido (sem os segredos do console); MmsMount 401/402 (música/vídeo) supostos pela ordem
  das pastas. Abertos de propósito: CodecEngine e PhotoExport (agente do firmware) e o FFBFC540 do FIFA 13

- [x] Jak and Daxter Collection (PCSA00080): no menu da coleção, a faixa vertical do meio (a prévia do jogo
  selecionado) aparece como listras horizontais embaralhadas, enquanto o resto do menu está certo (relato do usuário,
  04/11/2026, main d388b61; captura F10 `diagnostics\PCSA00080_20261104_162655`, dump de GPU
  `gpudumps\PCSA00080_20261104_162658.vsgd`). Parece passo (stride/pitch) ou layout errado de uma textura — muito
  provavelmente o quadro do vídeo de prévia (o SceMp4/decoder passou a funcionar nesse jogo há pouco, e o decoder agora
  completa as linhas até múltiplo de 16 por causa do Shinobido 2): conferir largura/altura/pitch do quadro decodificado
  contra a textura YUV que o jogo amostra
  Achados (04/11/2026, fix/user-bugs-3): não é o vídeo nem o preenchimento até múltiplo de 16 do Avcdec (o filme de
  fundo é 960x544 RGBA e sai certo). A faixa é uma animação MJPEG: a cada quadro a `main` decodifica com
  `sceJpegDecodeMJpeg` um JPEG de tamanho diferente (192..336 x 512..544, recortado no Jak) no mesmo buffer
  0x63FBC400 e manda uma mensagem (msgpipe) para a `F3DPlaybackThread`, que monta a textura linear com o tamanho
  que veio na mensagem. Aqui a `main` fica uma mensagem à frente: decodifica o JPEG N+1 antes de a thread de render
  enviar o desenho do quadro N (o `sceGxmWaitEvent` da `main` acorda com a conclusão da cena anterior da GPU, que
  chega depois de ela já ter mandado a mensagem N). Na hora do upload, 215 texturas tinham o tamanho de um quadro e
  o conteúdo do seguinte (passo errado = listras). O dump F8 do usuário reproduz CERTO no `replay-gpu` (o gravador
  pegou o conteúdo coerente), então é só temporização ao vivo. Testado sem efeito: esperar a GPU antes do decode,
  6 ms de latência no decode, `VITASHARP_TEXDIRTY=0`, e o `sceGxmWaitEvent` esperando a GPU esvaziar antes do sinal
  (a semântica do `libgxm_es4` já é "acorda no próximo sinal da GPU", ignorando os argumentos). No Vita a thread de
  render está parada no `sceKernelReceiveMsgPipe` quando a `main` envia, e a cena dela termina antes do próximo
  decode. Próximo passo: um modelo de tempo da thread de render/GPU (latência do `sceGxmFinish`/fila de display) ou
  atrasar a saída do decodificador até a GPU consumir o que já foi enviado — mudança de temporização, não um ajuste local
  Achados (04/11/2026, fix/system-apps-4), medidos com um log de cada decode e de cada upload da textura 0x63FBC400:
  o upload sempre tem o **tamanho do decode anterior e o conteúdo do decode atual** (1 upload por decode; quando o
  tamanho muda, listras; quando não muda, a imagem é a do quadro seguinte, invisível). Causa no jogo: a `main`
  (prio 160) monta o quadro k, manda a mensagem k e espera (laço de `sceGxmWaitEvent` em 0x81007176-0x81007192) só
  até a notificação `*(*(ctx+0x38)+0x24)` chegar a k-1 (escrita pelo EndScene da cena de display da
  `F3DPlaybackThread`); aí decodifica o JPEG k+1 no mesmo buffer. Ou seja, o jogo foi feito para ficar uma mensagem à
  frente e conta com a `F3DPlaybackThread` (prio 129) + a GPU consumirem a mensagem k no intervalo entre "notificação
  k-1 escrita" e "fim do próximo decode". No Vita esse intervalo é longo: a notificação só sai quando a GPU termina a
  cena de verdade (ms depois do EndScene) e o `sceJpegDecodeMJpeg` roda no codec (ms). Aqui a `F3DPlaybackThread`
  recebe a mensagem k-1 depois que a `main` já decodificou k (o `ReceiveMsgPipe` dela volta com uma mensagem que já
  estava no pipe: está uma mensagem atrás). Testado sem efeito (2 tentativas, timebox): esperar o GPU worker ficar em
  dia antes de o decoder escrever (42/171 quadros com listras contra 39/171), e fila de display mais funda
  (VS_DQ_CAP=3: 41/171). Medição: `tools/stripes.py` da pasta de trabalho local (razão entre gradiente vertical e horizontal na faixa
  do meio > 2). Próximo passo: medir onde a `F3DPlaybackThread` perde o quadro (o intervalo entre o AddEntry e o
  ReceiveMsgPipe varia de 0,4 a 25 ms conforme o trace), e modelar a latência da notificação de fim de cena (escrever
  a notificação um tempo de GPU do Vita depois do EndScene) ou a duração do decode do codec, nessa ordem
  Mais medições (04/11/2026, fix/system-apps-4, log de cada `sceGxmSetFragmentTexture` da textura 0x63FBC400, de cada
  decode e das chamadas de msgpipe/AddEntry): a `F3DPlaybackThread` faz ~27 ms por quadro **dentro de
  `sceGxmDisplayQueueAddEntry`** (a fila de display está sempre cheia: o callback do jogo faz SetFrameBuf +
  `sceDisplayWaitSetFrameBufMulti(2)`, 30 Hz) e só chama `ReceiveMsgPipe` a cada dois quadros, recebendo duas mensagens
  de 12 bytes de uma vez (o pipe é um fluxo de bytes, modo ASAP); desenha a primeira num quadro e a segunda no seguinte,
  sem novo Receive. Na segunda, a `main` já decodificou o JPEG seguinte: tamanho de k, conteúdo de k+1. A `main` só
  espera a notificação do quadro anterior ao que acabou de mandar (0x81007176), então o jogo permite essa folga de uma
  mensagem. Testado sem efeito (as listras ficam em 32-42 de 123-171 quadros): fila de display com 3 ou 64 lugares
  (VS_DQ_CAP), latência do decoder de 50 e 120 ns por pixel (9 e 22 ms para 336x544, antes de escrever a saída), esperar
  o GPU worker antes do decode. Ou seja, nem o tempo da `main` nem a profundidade da fila mudam a fase; falta descobrir o
  que no Vita impede a segunda mensagem de ficar para o quadro seguinte (o tempo do `sceGxmFinish` dentro da cena da
  F3D, que espera a GPU inteira, é o próximo suspeito: no Vita ele não espera a cena de display anterior)
  Achados (04/11/2026, fix/user-bugs-5; trace `VITASHARP_TRACE_TEX=63FBC400` = decode JPEG e leitura da textura no
  renderer com horário, `--vulkan` obrigatório no `run --headless` para haver renderer): a `F3DPlaybackThread` faz
  Receive a cada dois quadros desde o boot (mensagens de 24 bytes do logo, f0-f3), não só no menu: a `main` manda a
  mensagem 0 e logo a 1 (a espera da notificação k-1 já está satisfeita), e a 3 chega 12 ms depois do EndScene da
  cena 1, enquanto a F3D ainda está no AddEntry (o `displayQueueMaxPendingCount` do jogo é **1**). Com o atraso de
  uma mensagem estabelecido no começo, ele nunca se desfaz (as duas threads andam a 30 Hz). Testado sem efeito (42/171
  e 42/172): (a) a espera do sync object de fragmento no BeginScene, do lado da GPU (a cena só roda depois que o
  display soltou o buffer, como no Vita3K; `VITASHARP_DISPLAY_SYNC=1`, as esperas são de ~31 ms) e (b) a fila de
  display com no máximo `pendingCount` entradas em voo (`VITASHARP_DISPLAY_QUEUE_STRICT=1`; antes `pending=1` deixava
  passar 2). Os dois ficam como chaves A/B desligadas. O que falta para separar as hipóteses: o tempo real de GPU do
  Vita (aqui a notificação da cena 1 sai em <1 ms) e do decoder; um vídeo do Vita do menu confirmaria se a faixa está
  limpa no console
  Concluído por decisão do usuário (06/11/2026): Jak and Daxter fechado, exceto o save state no gameplay (item abaixo)
- [x] Army Corps of Hell (PCSE00006; no Vita3K trava na intro; aqui roda a 30 fps estáveis, 31/10/2026, main 13ca113,
  log `20261031_193541_PCSE00006`): implementar `SceDebugLed::sceKernelGetGPI` (NID 0x14F582CF), lida pela `main`
  em 0x81064351 — num Vita de varejo devolve 0 (as chaves de depuração só existem em kits); também ver os 4
  underruns de áudio da sessão. Feito (04/11/2026, fix/user-bugs): `sceKernelGetGPI` devolve 0; 40 s a 30 fps
  sem nenhum underrun (os 4 de antes não se repetiram com o PC livre)
- [x] Jak and Daxter Collection (PCSA00080; no Vita3K trava no boot): não carrega, fica no quadro 0 (nenhuma imagem)
  por mais de 1 minuto (31/10/2026, main 13ca113, log `20261031_193411_PCSA00080`). A thread `avPlayer Controller` (o
  player do vídeo de abertura) chama `SceMp4::NID_D5B26179`, que não está implementada (o `Mp4.cs` já tem stubs para
  outros NIDs desconhecidos), logo depois de um `sceGxmWaitEvent` da `main`; o jogo provavelmente espera o filme
  começar/terminar. Descobrir o que a `D5B26179` faz (assinatura no `libSceMp4`/firmware, Vita3K) e implementar, e
  ver se o player do jogo precisa de mais funções do SceMp4/SceAvPlayer; também havia 2 underruns de áudio sem
  nenhum quadro. Corrigido (04/11/2026, fix/user-bugs), três causas: (1) `D5B26179` é o GetStreamInfo no layout
  interno do `libscemp4` (0x140 bytes: vídeo +0x10/+0x14 perfil/nível AVC, +0x20 tamanho máximo da unidade, +0x28
  duração; áudio +8 taxa, +0xC bits por amostra, +0x1C canais; idioma +0x38), que o `sceMp4GetStreamInfo` só converte
  para o de 0x158; (2) `sceGxmWaitEvent` do `libgxm_es4` é um `sceGpuSignalWait(…, 100 ms)` e não volta na hora (a
  `main` chamava 67 milhões de vezes em 50 s); (3) o modo das msgpipes estava trocado (PSP: 0 = tudo; Vita: 0 = ASAP,
  1 = FULL, 0x10 = não esperar), e a thread de render do jogo recebia com ASAP num buffer de 0x600 e esperava para
  sempre mensagens de 12 bytes. Logos, filme de abertura, menu da coleção a 30 fps e o LoadExec do Jak 1; o Ninja
  Gaiden Σ2 agora também mostra o filme de abertura (antes o player falhava e pulava)
- [x] Blind Men (PCSB01421; no Vita3K tela preta): roda e é jogável, sem funções faltando nem erros (31/10/2026, main
  13ca113, log `20261031_193014_PCSB01421`), mas: (1) demora para começar (carregamento inicial longo; comparar com o
  Vita real); (2) nas trocas de tela aparece por ~0,5 s uma imagem preta com pequenos problemas de textura (provável
  render target/textura nova usada antes de ser preenchida, ou conteúdo antigo de uma superfície reaproveitada; pegar
  um F10 ou dump de GPU numa troca de tela); (3) 19–30 fps, quase sempre 22–24 (Unity, alvo provável 30): medir com
  `--profile`/F10 se é CPU ou espera da GPU, como no The Deer God
  Correção do item (3), falta o usuário medir na janela (05/11/2026, agente C, fix/bugs-games-5; achados do agente B
  na fix/bugs-games-4: é Ren'Py, e `saveThreadProc`/`SDL_TimerThread` giram em `DelayThread(1000)`): o
  `sceKernelDelayThread` dormia quase todo o prazo e **girava** (SpinWait) os últimos ~0,5 ms para acordar na hora, e
  num laço de 1 ms isso é meio núcleo do host por thread. Agora só os atrasos de 5 ms ou mais giram no fim (limitadores
  de quadro); os curtos só dormem no timer de alta resolução do Windows (atraso medido aqui: mediana 0,3 ms, p99
  0,7 ms), e a thread retoma no prazo em tempo de guest como antes (`CpuCore.WokeAt`). `VITASHARP_DELAY_SPIN=1` volta
  ao antigo (todos giram), `=0` nenhum gira. Medido sem janela em 2 núcleos (0x30, 1800 quadros, mesmo build,
  `bugsC5_out/blind_nospin` x `blind_spin`): CPU do processo 35,7 → 25,0 ms/quadro, `saveThreadProc`
  7,57 → 0,43 ms/quadro, `SDL_TimerThread` 5,63 → 0,42; `main` igual (13,5 x 13,1). O fps sem janela já era 30 nos
  trechos de jogo nos dois (com quedas nos carregamentos nos dois, o PC estava dividido com outros agentes). Ritmo sem
  regressão (mesmo build, `--profile`, novo x `DELAY_SPIN=1`): Devious Dungeon p50 16,68/16,68 ms, >25 ms 10 x 23;
  F1 2011 (menus, 25-60 fps) p50 38,9/38,4, p99 64/76; Uncharted (menu) 59,9/59,3 fps, p90 17,6/18,4, >25 ms 3 x 25
  (`bugsC5_out/dd_boot_*`, `f1_menu_*`, `uc_menu_*`). Ainda abertos: (1) o boot (~5 s até o 1º quadro, ~20 s em 2
  núcleos) e (2) a troca de tela (f560-780, `bugsB3_out/blind_tr`)
  Agente C dia 6 (05/11/2026): (2) a troca de tela é a transição do Ren'Py: o menu some de uma vez (602 → 603), 16
  quadros pretos (~0,5 s) e um fade-in da cena nova (619-640, sem defeito de textura sem janela,
  `bugsC6_out/blind_tr_4.png`). O preto é o fade-out que deveria mostrar o menu escurecendo: no dump
  `bugsC6_out/blind_fade2.vsgd` (presents 598-604, trace `blind_fade2_tr/trace_603`) o jogo desenha uma textura 32x32
  (0x612656F0/0x612666F0, preta com alfa médio 0,14) em dois alvos 960x540 (0x62CE6950, 0x62EE4950) e depois compõe os
  dois (fs 1EB8007BE57CDB61, blend 1/1/5): os dois saem pretos, e o menu (que está em 0x60E6C680) não entra na
  composição. Falta ver de onde a textura 32x32 deveria vir (cópia do menu? textura enviada pela CPU?). (1) Boot: 5 s
  até o 1º quadro com o PC ocioso (`bugsC6_out/blind_fade2/log.txt`; ~2,2 s em `main` entre o `Preload` e o thread
  `preloader`, o init do Python do Ren'Py); com a CPU dividida passa de 30 s (watchdog STALL em `blind_fade`); falta
  o tempo do Vita real para comparar
  Agente C dia 8 (06/11/2026, fix/bugs-games-8): **(2) não é defeito, o Vita real faz igual.** No vídeo do Vita
  (`9vleIQnJ8dI`, "Blind Men (PS Vita Gameplay)", Start no menu aos ~82 s; `bugsC8_yt/trans.png`, 10 qps) o
  menu some de uma vez, fica ~0,7 s preto e a cena entra em fade, como aqui. As duas texturas 32x32 são sólidos
  pretos opacos (todos os texels 0,0,0,255 em `trace_603/textures`, não alfa 0,14): o Ren'Py faz o dissolve entre
  dois quadros só com o fundo preto (no present 601 o mesmo tipo de 32x32, 0x60E6B680, é o fundo do menu, com as
  imagens 1280x720 por cima; `bugsC8_out/blind_tr601/trace_601`). Resta o (1), o boot
  Confirmado no jogo pelo usuário (06/11/2026): ok (a troca de tela preta é igual à do Vita)
  Agente C dia 10 (06/11/2026), o preto do boot: o Vita mostra o `sce_sys/pic0.png` do jogo (idêntico ao quadro do
  vídeo `9vleIQnJ8dI`) até o logo da Ratalaika; aqui o jogo apresenta um 1º quadro preto com 1 s (uma textura
  960x540 U8U8U8U8 preta em tela cheia: o SDL do Ren'Py limpando a tela; não há presplash nos arquivos do jogo) e o
  2º só 19 s depois (PC dividido; ~5 s ocioso). Feito e **não commitado**: mostrar o pic0 na janela até o 1º present
  do jogo (`bugsC9_proof/bootsplash_pic0.patch`; validado só por log: `Boot picture: 960x544 shown`,
  `shown for 1,0 s`), mas isso só cobre 1 s: o Vita segue com o pic0 depois desse quadro preto. Falta saber a regra
  do sistema (o pic0 fica até o 1º quadro não preto? até o jogo terminar de carregar? é o SceShell que o desenha?) com
  o firmware LLE ou outro vídeo de jogo cujo 1º quadro seja preto
  **Feito** (agente C dia 11, 06/11/2026, fix/bugs-games-10; validado por log, falta o usuário ver na janela): no
  vídeo do Vita (`9vleIQnJ8dI`, 0:18-0:22) o pic0 fica ~4 s e passa direto para o logo, sem preto (ffmpeg
  blackdetect: nenhum trecho preto ali), embora o jogo apresente quadros pretos antes (aqui os presents 1-83 são todos
  0,0,0 e o logo entra com fade a partir do 84). Regra adotada: o pic0 fica até o 1º quadro do jogo que não é preto
  (algum canal > 16), no máximo 60 s; enquanto isso cada quadro do jogo é lido de volta e conferido e a janela mostra o
  pic0 no lugar dele (`VulkanRenderer.BootSplash.cs`; só na janela, apps do sistema NPXS não têm). Blind Men: `shown for
  21,7 s, until the game's first frame that is not black (present 84)`; Devious Dungeon 6,6 s (present 2), Killzone
  4,1 s (present 11, os 10 primeiros são pretos), Tearaway 1,9 s (present 1) (`bugsC10_out/blind3`,
  `splash_PCSB01240`, `splash_PCSC00045`, `splash_PCSF00476`). Não conferido em vídeo do Vita se outros jogos com
  quadros pretos no início (Killzone) também seguram o pic0 por cima deles
  Confirmado pelo usuário (06/11/2026): ok
- [x] MLB 14 The Show (PCSA00115; no Vita3K aborta na intro): roda a 53–60 fps e depois de ~70 s trava no quadro
  4116 (31/10/2026, main 13ca113, log `20261031_191246_PCSA00115`, captura F10 `diagnostics\PCSA00115_20261031_191416`
  de antes do travamento). Watchdog `STALL` aos 6 s e 30 s; a fila da GPU está vazia (966383 comandos concluídos). A
  thread `main` espera no `mutex Render 1`; a thread `Display Engine` fica rodando sem parar (pc 0x8102F2A0, lr
  0x00002E42, que parece inválido) logo depois de um `sceGxmWaitEvent(1, 0x2CC7, 0x1021, 0)` — provavelmente ela
  segura o mutex e fica em loop esperando um evento/notificação da GPU que o emulador não entrega (ou o lr
  quebrado indica um retorno errado do HLE). Ver o que o `sceGxmWaitEvent` espera e o que a Display Engine lê no loop.
  Provavelmente corrigido (04/11/2026, fix/user-bugs, falta o usuário confirmar jogando): o `sceGxmWaitEvent` do
  `libgxm_es4` é um `sceGpuSignalWait` de até 100 ms e o HLE voltava na hora, então a Display Engine girava sem
  parar segurando o `Render 1`; agora dorme até uma notificação da GPU. Duas sessões de 8000 quadros (a 60 fps, logos,
  aviso de dificuldade e a tela do título) passaram do quadro 4116 sem watchdog; no fim a Display Engine espera no
  seu condvar
  Confirmado no jogo pelo usuário (04/11/2026): não trava mais
- [x] God Wars: Future Past (PCSB01073; no Vita3K trava no carregamento): o campo de batalha 3D não aparece, só o HUD
  sobre preto (31/10/2026, main 13ca113, captura F10 `diagnostics\PCSB01073_20261031_191141`, ~30 fps, 134 desenhos em
  6 cenas). O jogo (Unity, IL2CPP) desenha o 3D numa superfície fora da tela em 0x61CFF8E0 (960x544, endereço sem
  alinhamento de página), reduz para 240x136 e 120x68 (brilho) e depois compõe no framebuffer 0x60400000. O log
  avisa `transfer reads render target 0x604…/0x608…/0x600… while a scene is open: not written back` (uma cópia lê o
  framebuffer antes de a cena ser gravada na memória) e há 6 imports de variável não resolvidos de
  `Il2CppAssemblies` em `UnityPlayer_536_Release`. Ver se a composição lê a superfície 3D certa (endereço
  desalinhado / região), se a cópia precisa forçar a gravação da cena e se os imports faltando importam.
  Achados (04/11/2026, só pela captura; o jogo precisa de um save numa batalha para reproduzir): o 3D é desenhado
  (o clear do Unity, draw 5, pinta 0.10/0.22/0.20 e ~80 desenhos seguem), e o downscale (draw 99, scene 33653) lê o
  `0x61CFF8E0` como textura 960x544 e sai com conteúdo (média ~0.39 no 240x136); mas a composição final (draw 103,
  outra cena, mesmo `t0=0x61CFF8E0`) sai preta, e o dump do próprio alvo em `targets.txt` tem estatísticas lixo
  (médias ~1e-315, como se a leitura não fosse do formato certo). Ou seja, a segunda leitura do alvo desalinhado já
  não acha a imagem renderizada (cai na memória do guest, que nunca recebeu o writeback). Próximo passo: com um save
  na batalha, `--trace-draws` + log de qual imagem o `ResolveTexture` escolhe para `0x61CFF8E0` em cada desenho
  (área do renderer que os agentes de desempenho estão mexendo: Draw/Memory)
  Mais achados (04/11/2026, fix/user-bugs-2, só pela mesma captura): o alvo 3D é lido três vezes no quadro, e as
  três pelo mesmo caminho (draw ring `Krt@0x61CFF8E0`, sem scene-start): o downscale (99, sai certo), a composição com
  o bloom (103) e um passe de tela cheia (113, fs 53F1421EAC9F9053, `out = tex4x4 * sa0.x + cena`, com `sa0.x` = 0,
  substitui o RGB do framebuffer). Então o preto final vem da leitura da cena em 103/113, não do vertex shader (os UVs
  passam direto; o `sa[4].x` = 0x178814A0 é o `_TexelSize` em F16 e só decide a inversão do v). A diferença entre 99
  e 103/113 é só o alvo de destino (240x136 sem profundidade x o framebuffer 0x60400000 com ds 0x60C00000). Sem um
  roteiro de entrada até a batalha não deu para reproduzir; próximo passo continua sendo um F8 (dump de GPU) do
  usuário numa batalha, e então `replay-gpu --draw-limit` em 99/103/113 com `--debug-fs=9154143BA21F0658:t3`. Pista forte: no
  `targets.txt` o alvo 3D termina o quadro todo zerado (min = max = 0; as médias ~1e-315 são só ruído do cálculo), ou
  seja, a imagem é apagada depois do desenho 99. O único caminho que recarrega um alvo inteiro da memória do guest no meio
  do quadro é `InvalidateTextures` (`CallbackCommand` com `WriteSize`: uma transferência na CPU que escreve na faixa
  do alvo → `UploadSurfaceFromGuest`, com a memória nunca gravada de volta = preto). O log mostra transferências lendo os
  framebuffers com uma cena aberta (`transfer reads render target ... while a scene is open`) logo antes de a textura
  0x61CFF8E0 aparecer: conferir com `VITASHARP_TRACE_VIS`/um log temporário em `InvalidateTextures` se o destino de uma
  transferência cai em [0x61CFF8E0, 0x61EFD8E0); se cair, a transferência tem de ir para a imagem na GPU (ou o alvo ser
  gravado de volta antes), não recarregar o alvo inteiro da memória
  Confirmado com o dump F8 do usuário numa batalha (`PCSB01073_20261104_135953`): em todo quadro uma transferência
  escreve 0x61CFF8E0 inteiro (2088960 bytes) com a cena do framebuffer aberta, logo depois da composição (desenho 110) e
  antes do passe final (118, que lê o alvo e substitui o RGB do framebuffer); o `InvalidateTextures` recarrega o alvo
  da memória e o 118 lê lixo. A transferência lê o framebuffer cuja cena ainda está aberta, e `BeforeCallback` pulava a
  gravação de volta de qualquer alvo enquanto houvesse cena aberta. Corrigido (fix/user-bugs-2): a renderização é pausada
  (`PauseRendering`) e o alvo é gravado de volta antes da cópia, como entre cenas. Falta conferir ao vivo numa batalha
  (o replay não reexecuta transferências; o roteiro `Load Game` com o save no slot 0 só chega ao mapa)
  Confirmado no jogo pelo usuário (04/11/2026): a batalha 3D aparece
- [x] Gal Gunvolt (PCSG00704; no Vita3K trava ao iniciar): roda a 60 fps estáveis, mas "parece com problemas" (usuário,
  31/10/2026, main 13ca113, captura F10 `diagnostics\PCSG00704_20261031_175803`). O jogo desenha num framebuffer de
  480x272 (pitch 512) que o sistema amplia para a tela; na captura (tela "How to play") a moldura lateral e os sprites
  aparecem, mas a área de jogo no centro está toda preta, sem o cenário/blocos da fase (24 desenhos em 3 cenas por
  quadro, nenhum aviso além de formatos de textura novos). Confirmar com o usuário o sintoma exato e comparar com o
  Vita real (se a fase deveria ter fundo), e conferir a ampliação 480x272 → 960x544 (filtro, proporção)
  Achados (04/11/2026, fix/user-bugs-2, pela captura): o fundo da fase existe — é o alvo 0x8E449BC0 512x256
  (`targets/`), desenhado no 960x544 pelo desenho 9 (fs 4C0DA2DA0578DC65), que descarta o texel se `.w < 1`
  (alpha test) e devolve `(w, z, y, x)`. O alvo é uma superfície U8U8U8U8 com swizzle RGBA (0x00200000) e é lido como
  textura RGBA (0x0C002000). No hardware essa ida e volta é identidade (memória A,B,G,R; a textura RGBA lê do byte
  mais alto), mas o renderer grava a saída do programa em ordem lógica numa imagem R8G8B8A8 (só ARGB usa B8G8R8A8,
  que já é a ordem da memória) e aplica o swizzle da textura (definido sobre a memória) direto nela: o `.w` lido é o R
  (máx. 0,66) e o alpha test descarta a fase inteira. Testado compor o swizzle da textura com a ordem de memória da
  superfície em `RtSwizzle`: a fase volta, mas as cores do jogo todo invertem (vermelho no lugar de azul), porque a
  apresentação (blit cru da imagem) e o resto do caminho contam com o erro oposto. A correção certa é tratar a imagem
  de superfícies RGBA/BGRA de forma consistente em todos os pontos (amostragem, apresentação, writeback, regiões, cópias),
  ou gravar na ordem da memória; patch do experimento em `bugs2_gunvolt_swizzle.patch`
  Corrigido (04/11/2026, fix/user-bugs-3): as imagens de superfícies U8U8U8U8 RGBA/BGRA continuam em ordem lógica
  (o blending precisa do alfa no canal A), e todos os consumidores passaram a vê-las na ordem da memória: amostragem
  (`RtSwizzle` compõe o swizzle da textura com `Formats.SurfaceMemoryOrder`, também nas regiões e cadeias de mips),
  gravação de volta na memória (`WritebackConvert.ReverseBytes`/`LogicalToBgra`), recarga da memória
  (`UploadSurfaceFromGuest`) e apresentação (o framebuffer RGBA 480x272 do Gunvolt passa por uma cópia em ordem de
  memória, `VulkanRenderer.PresentOrder.cs`, antes do blit e do dump do quadro). A fase do "How to play" aparece com
  as cores certas. Ainda leem os bytes crus da imagem: o espelho de CDRAM das uniformes mapeadas e as texturas que
  reinterpretam um alvo com outro tamanho de texel (`ReinterpretRenderTarget`), e a composição de alvos dentro de
  um framebuffer da CPU
- [x] FIFA 13: estalos no som da torcida (relato do usuário, 05/11/2026 03:36, main 5a3bcb4, log
  `logs61105_033621_PCSE00093.log`, dump F8 `gpudumps\PCSE00093_20261105_034029.vsgd`). Sem underruns (1 em 261 s).
  Suspeita forte: a correção da narração (7732c69) toca os superframes ATRAC9 zerados como silêncio, e o agente B já
  tinha visto que isso põe um buraco de 0,27 s num anel da torcida; buracos/emendas sem suavização no loop dão estalo.
  Comparar com `VITASHARP_AT9_ERROR_SILENCE=0`, e tratar o silêncio só nas vozes que terminam (narração) ou suavizar
  as emendas do loop.
  Achados (05/11/2026, fix/bugs-audio-2, sem causa no emulador; o log citado não existe, a sessão é o `logs\last.log`):
  três partidas de 10 min (`tests/nav/fifa13_match.nav`, `VITASHARP_NGS_TRACE=1` + `--audio-dump`), duas com o padrão
  e uma com `VITASHARP_AT9_ERROR_SILENCE=0`. As vozes de torcida (anéis ATRAC9 de 6720 bytes, superframes de 40 bytes
  = 1 quadro de 256 amostras, mono) não têm buraco nem degrau: o fim de cada clipe já chega em silêncio antes dos 3-7
  superframes zerados (16-37 ms) e do kill+play do próximo clipe (0 quedas de >30 dB em 2,5 ms nos WAVs das vozes nem
  no mix, nenhuma emenda na fase de 256/512/1024 amostras; os 0,27 s de buraco são de um clipe no fim da partida, já
  em silêncio). O decodificador bate com o FFmpeg também na configuração da torcida (E00470FE: 102,8 dB). Com
  `=0` os estalos do mix não caem (0,4/s contra 0,2-0,4/s; são transientes do próprio conteúdo, o YouTube do Vita
  real `R3WhJ9u_MB4` dá 0,5/s pelo mesmo critério) e as reproduções com dado velho voltam (plays com >2 superframes
  velhos: 62 contra 4 e 9), então a 7732c69 fica. A saída do host (OpenAL) não descarta grãos (Submit bloqueia).
  Falta ouvir: um F10 com áudio no momento do estalo diria se ele está no que o jogo gera (dump) ou depois (host).
  Ferramentas na pasta de trabalho local `bugsB2_tools` (`drops.py`, `phase.py`, `gaps.py`, `clickhist.py`, `ffsnr.py`)
  Confirmado pelo usuário (06/11/2026, main 0d298b4): todos os problemas do FIFA 13 corrigidos
- [x] FIFA 13: "a voz da torcida está estranha" (relato do usuário, 05/11/2026 22:22, main da8372d, gravação
  `captures\PCSE00093_20261105_222244_tudo`, 10 s de partida). Achados (fix/bugs-games-7), sem causa no emulador:
  com o `state_start.vss` (60 s, `VITASHARP_NGS_TRACE=1` + `--audio-dump`, `bugsB7_out/state0`) a torcida
  são 4 vozes ATRAC9 estéreo (E00974FE: um laço fixo de 3,77 s em 0x89A41DB0 e três anéis de 13440 bytes que o jogo
  regrava) e 2 mono (E00470FE, ronco grave); a narração são 2 vozes mono (E00270FE/E00370FE). O decodificador bate
  com o FFmpeg também em estéreo (107,6 dB), **mas com a polaridade invertida** nos dois canais (inaudível sozinho;
  só importaria somado a um som correlacionado que não venha do ATRAC9). Rota (log novo do trace: cada play mostra
  as saídas até o master): Atrac9 -> Mixer -> (Mixer) -> Master, só saída 0, sem reverb; EQ/Envelope/Distortion/
  Generator com parâmetros zerados (o jogo não os usa), Mixer com 1,0/1,0; volumes e matriz iguais ao Vita3K. Nenhum
  superframe velho, nenhum corte (os 39 erros de decodificação são o enchimento zerado do fim das falas), nenhum
  clipping (pico do mix -5,5 dBFS). Diferença medida contra o Vita real (YouTube `bMYfYTF_BHc` 300-420 s, além do
  `R3WhJ9u_MB4`): no Vita a torcida quase não tem graves (abaixo de ~400 Hz fica 30-40 dB abaixo da faixa 600-2000 Hz);
  aqui o grave vem forte das próprias vozes (as mono E00470FE têm o máximo abaixo de 200 Hz, e o anel 0x89A416C0
  -12 dB). Como o jogo não configura EQ, a hipótese que sobra é o que o hardware faz com um módulo EQ/filtro de
  parâmetros zerados e não contornado (passa direto aqui e no Vita3K). Próximo: o usuário dizer o que soa estranho
  (tom, eco, grave, repetição) ou um vídeo do mesmo momento; comparar a mesma cena do Vita3K não dá (não abre o jogo)
  Confirmado pelo usuário (06/11/2026, main 0d298b4): todos os problemas do FIFA 13 corrigidos
- [x] FIFA 13: as cunhas/pontos escuros na grama continuam depois da cópia dos uniforms na execução (318be32; relato
  do usuário 05/11/2026 03:40, dump F8 `gpudumps\PCSE00093_20261105_034029.vsgd`: o `specks.py` acha pontos em 5 de
  266 quadros, ex. 13543 com 21 px em (338,71)). Então a causa não é (só) o uniform copiado cedo; olhar de novo a malha
  de sombra projetada (vs DCBB621AC8070CB0) nesses quadros com `--draw-limit`.
  Achados (05/11/2026, fix/bugs-audio-2): **o dump novo não tem cunha.** Os 5 quadros do `specks.py` são falsos
  positivos: 13543/13547 estão na vinheta de transição do replay (o "ponto" é a sombra normal do jogador sob a
  vinheta) e 13711 tem 1 px; com o critério de cunha (>= 8 blocos) dá 0 de 266. No dump antigo (205631) a cunha do
  present 11182 é o desenho 12 de `--draw-limit=N@11180` (atenção: `--draw-limit=N@F`/`--trace-draws=F` mexem no
  present F+2 neste jogo). Com `VITASHARP_TRACE_UNIFORMS` (agora grava também índices e streams) e um modelo do
  vs DCBB621AC8070CB0 na CPU (`bugsB2_tools/shadowvs.py`): a paleta de ossos (buffer 1) daquele desenho só estava
  escrita até o osso 61; os ossos 62-147 vinham zerados, e todo vértice deles caiu na origem do mundo projetada
  (647,262), exatamente a ponta da cunha. Ou seja, o jogo ainda estava escrevendo a paleta quando ela foi copiada.
  A 318be32 passou a cópia para o worker, mas o worker executa o desenho assim que ele entra na fila, muitas vezes
  antes do sceGxmEndScene (o hardware só lê na execução da cena). **Correção** (falta o usuário conferir): o worker
  segura o desenho com cópia tardia até o fim da cena (sceGxmEndScene ou sceGxmMidSceneFlush; `SceneGate`), a não
  ser que uma thread do jogo espere por ele (WaitFor) ou passem 250 ms; `VITASHARP_UB_SCENE_END=0` volta ao antigo,
  `=check` conta os desenhos segurados cuja memória mudou na espera. Aqui, com o PC dividido entre agentes, o worker
  fica atrás do jogo: só 8192 desenhos foram segurados numa partida e nenhum teve a paleta reescrita, por isso as
  cunhas não reproduzem aqui; na máquina livre do usuário o worker anda junto com o jogo
  Achados (05/11/2026, fix/bugs-games-4): o usuário ainda vê cunhas com a main 2f47304 (que tem o SceneGate), e o
  dump novo dele (`PCSE00093_20261105_122101.vsgd`) não as tem. Hipótese testada: o jogo escreve a paleta DEPOIS do
  `sceGxmEndScene`. Instrumentos novos: `VITASHARP_UB_SCENE_END=frame` (os gates das cenas só abrem no
  `sceGxmDisplayQueueAddEntry` do quadro; experimento, desligado) e `=audit` (no lado do guest, independente do
  worker: 1 em 4 desenhos tem os buffers de uniforms de vértice hasheados no desenho, no fim da cena, na troca e na
  troca seguinte). Partidas aqui (`bugsB3_out/fifa_g_check`, `fifa_g_frame`, 760-820 mil amostras): o hash muda
  entre o fim da cena e a troca em 11% (modo normal) a 17% (`=frame`) das amostras, 36-52% nos programas de pele
  (47F553B335AE94C0, 6F43641D442F7C86, DCBB621AC8070CB0). Mas esses números NÃO provam escrita atrasada da paleta: o
  bloco hasheado é o que a cópia lê, e uma paleta que termina em array é lida até 16 KB (até o próximo buffer), o que
  cobre as paletas dos desenhos SEGUINTES do mesmo anel (100% das amostras desses programas "mudam" entre o desenho
  e o fim da cena). O worker aqui fica sempre atrás do jogo, então nenhum modo mostra cunha (`specks.py` 0 de 1501
  quadros nos dois). Achado ao lado (Croixleur, abaixo): o corte da cópia "no próximo buffer ligado" olhava também
  índices que o programa não lê (endereços velhos de desenhos anteriores) e zerava o resto do bloco; numa paleta de
  16 KB num anel isso zera ossos — o mesmo sintoma das cunhas. Corrigido junto com o Croixleur; o audit conta quantas
  cópias o corte antigo encurtava: numa partida do FIFA
  com a correção, 147.600 cortes evitados (contados em cada cálculo de tamanho, inclusive o do audit) e 200 MB a mais lidos (`bugsB3_out/fifa_clamp`; a
  partida e a sombra seguem certas no t05). Próximo: o usuário jogar uma partida com a correção; se
  ainda houver cunhas, gravar tudo (F8) num triângulo e testar `VITASHARP_UB_SCENE_END=frame`
  Relato novo (main a19cdbe, com o 479b54f): "retângulos pretos abaixo dos jogadores", gravação completa
  `PCSE00093_20261105_211112_tudo`. Os 442 quadros ao vivo (frames.vsfr, extraídos com `vitasharp-perf capture
  --frames`) são iguais ao replay do gpu.vsgd nos 389 presents em comum (pixels 50+ níveis mais escuros ao vivo: 0-1
  por quadro), e nenhum dos dois tem retângulo (specks.py ao vivo: 1 quadro com >= 8 blocos, sombras normais). Ou o
  defeito não caiu nessa janela, ou ele não aparece na imagem lida para a gravação; falta um print ou o horário do
  retângulo
  05/11/2026 22:22 (main da8372d, com a b6949d7): o usuário não viu mais os triângulos na partida. Parece resolvido,
  falta mais uma partida
  Confirmado pelo usuário (06/11/2026, main 0d298b4): todos os problemas do FIFA 13 corrigidos
- [ ] Achados do agente de bug C (05/11/2026; detalhes na branch fix/bugs-games, commits 749d738..7330147):
  Jak 1: com o save state do usuário (carregado depois do LoadExec da coleção, cópias de ux0/user/00 e np) há quadros
  isolados com o céu preto e o mar azul-claro (7 em 623 quadros, `tileflick.py`), e a câmera segue o Jak; os dumps do
  usuário 015823/020221 não têm esse flicker e mostram a câmera fixa (intermitente). Ferramenta: depois de LoadExec ou
  de carregar um estado o renderer novo recomeça a contagem de presents em 0, então `--gpu-dump-at`/`--dump-from`/
  `--trace-draws` pegam os quadros errados (sugestão: `ro.FirstPresent = Display.FramesPresented`). A tela de título do
  Jak 1 fica a 0,4-2 fps nos testes dos agentes (sem janela e na janela, CPU sem limite, 8 núcleos), e o usuário vê só
  ~10 s lentos e depois 20 fps; causa não achada. Save state do Jak 1: em 2 de 4 carregamentos o jogo trava (main em
  DMAPut, GOAL em DMADone, F3DPlaybackThread no msgpipe, fila da GPU vazia). Croixleur: o ícone do SKIP é ligado e
  desligado pela lógica do jogo a cada quadro (cor do uniform 1/0), a fila da tela pede 2 vblanks por troca e isso é
  respeitado; próximo passo é logar `sceKernelGetProcessTimeWide` por quadro e `--watch` na origem da cor. Akiba's
  Beat: as telas pretas longas são carregamentos (no Vita 8-18 s, com ícone animado; aqui a imagem congela ~10 s)
- [ ] Achados do agente de bug C, dia 2 (05/11/2026, branch fix/bugs-games-2; dados em
  `bugsC2_*`). Corrigidos: (1) o renderer criado depois de um LoadExec ou de carregar um estado segue a
  contagem de presents do jogo (`FirstPresent = Display.FramesPresented`): o `--gpu-dump-at=1740:2340` do Jak 1
  depois do LoadExec e do estado gravou os presents 1740-2340 e os frame_N.png mantêm os números; (2) o travamento
  ao carregar estado do Jak 1 (main em DMAPut, GOAL em DMADone): as threads salvas dentro de uma espera reemitem a
  espera antes das outras rodarem (uma variável de condição não guarda sinal, e uma thread que rodava sinalizava antes
  do waiter voltar à fila). 9 de 9 cargas do estado do usuário numa rodada voltaram ao gameplay (antes 2 de 4
  travavam). Ainda aberto:
  Jak 1, flicker de um quadro: com o estado do usuário aparece num quadro (live 1930) uma faixa escura translúcida
  sobre tudo abaixo de uma linha horizontal (~y 75, largura toda); 5 quadros com flash em 618 (`tileflick.py`). O dump
  `bugsC2_out\jak_flash.vsgd` reproduz no replay, mas em outros quadros (1926/1928/1930: depende de tempo) e igual com
  `VITASHARP_UB_LATE=0` (não é a cópia tardia dos uniforms). O `--draw-limit` não muda nada nesse dump (nem com 0
  desenhos, em 1928-2000): a imagem apresentada não vem dos desenhos reproduzidos, então a bisseção por desenho não
  funciona aqui; ver por que (escrita de volta/upload da memória do framebuffer?) antes de continuar. Depois de cada
  carga do estado há ~20-40 quadros pretos antes da imagem voltar (talvez ligado à tela preta no começo do gameplay).
  A câmera fixa não foi reproduzida.
  Explicado e corrigido (05/11/2026, fix/bugs-games-3): o `--draw-limit` mudava, sim, a imagem — mas a do present F+2,
  e o `limits.ps1` da rodada só gravava os quadros F e F+1. O registro de present fica na fila onde o renderer o
  executou (depois do flip da fila de display), e um jogo que anda à frente da tela (3 buffers, 2 flips pendentes:
  Jak, FIFA 13) só apresenta um buffer 1-2 presents depois de desenhá-lo. Agora o replay indexa o dump
  (`GpuDumpFrameIndex`, `frames_index.txt`): cada present é mapeado para a última cena que desenhou no buffer mostrado,
  e `--draw-limit=N@F`/`--trace-draws=F` agem sobre os desenhos por trás de `frame_F.png` (o trace cobre esses
  desenhos mesmo quando atravessam um present; os ids do `draws.txt` são os N do `--draw-limit`). Prova:
  `--draw-limit=0@1930` no `jak_flash.vsgd` muda só o `frame_001930.png` (antes: `0@1928` mudava o 1930);
  `--draw-limit=0@13605` no dump do FIFA `PCSE00093_20261105_034029` muda só o 13605 (atraso 1 nesse dump, 2 em 10
  presents); saídas em `bugsC3_out/new_lim0_1930`, `fifa_lim0` (compare.txt)
  Tearaway (`tearaway_boot` com `--save-state=f1800 --load-state-at=f2100 --validation`): sem device lost, com e sem
  a correção (2 rodadas), imagem certa depois da carga. Atenção: o `--validation` não faz nada neste PC, a camada
  `VK_LAYER_KHRONOS_validation` não está instalada (só há os binários Android em `vvl_dl`), e o código ignora isso
  sem aviso. Corrigido (05/11/2026, fix/bugs-games-4): aviso no log e no console ("running WITHOUT validation") com
  a instrução de instalação por plataforma (LunarG SDK, pacote da distro ou release do Vulkan-ValidationLayers +
  `VK_ADD_LAYER_PATH`); README atualizado. Conferido com `replay-gpu --validation` neste PC.
  Croixleur: o pisca do SKIP reproduz sem janela: o símbolo do quadrado dentro do botão aparece num quadro e some no
  seguinte, alternando exatamente a cada quadro apresentado (2700-2783). Não é o relógio: o
  `sceKernelGetProcessTimeWide` da main avança ~33,4 ms por quadro, e um quadro de 109 ms (2739) não mudou a fase.
  As chamadas da main (ctrl/touch peek, tempo) têm os mesmos argumentos nos quadros com e sem o símbolo. Próximo:
  achar o desenho do símbolo com `--trace-draws` em dois quadros seguidos e `--watch` na origem da cor/visibilidade.
  O trace `--trace-hle-frames=A:B:all` mostra os valores de retorno (linhas `->`).
  Achados (05/11/2026, fix/bugs-games-4): o símbolo é o desenho 14 de cada quadro (textura 28x28 UBC3 0x608B1600, vs
  7C9AC5F71551EBCC) e a ÚNICA diferença entre dois quadros seguidos (traces 2711-2715 do dump do usuário
  `PCSG00537_20261104_200307.vsgd`, campos normalizados) é a cor nos uniforms de vértice: (1,1,1,1) num quadro e
  (0,0,0,0) no outro. O jogo usa dois anéis de uniforms alternados por quadro (0x8D926000 / 0x8DAF6000; o desenho 14
  lê `vub0=` 0x8D926570 / 0x8DAF6570, cor em +0x40) e a cor segue a paridade do anel: ímpar = 1, par = 0, em 2711-2715.
  Hipótese A: o jogo só escreve a cor num dos anéis (dado velho no outro); hipótese B: a lógica alterna de propósito
  (50% por cintilação a 60 Hz). Vídeo do Vita real (`fNZ7oTLZivM`, 60 fps, diálogo em 83-84 s, recorte do SKIP em
  `bugsB3_croix/yt/d_skip.png`): o símbolo fica aceso nos 60 quadros, sem piscar (os quadros do vídeo vêm em pares
  idênticos: 30 fps de origem). `--watch` na cor do anel par (8DAF65B0:10, quadros 2700-2740) não viu NENHUMA escrita
  da CPU, e as cópias por `sceClibMemcpy` (HLE) não passavam pelo watch — corrigido (o watch agora vê cópias/fills
  da HLE); repetir o watch nos dois anéis desde o quadro 2200 é o próximo passo (fila `bugsB3_queue1b`).
  Corrigido (05/11/2026, fix/bugs-games-4): os endereços do dump do usuário não valem numa rodada local (o anel ímpar
  aqui é 0x8DAF7570). Com `--trace-draws` local em 2691/2692 e `--watch` + trace HLE da main, a cor (1,1,1,1) é escrita
  ANTES do `sceGxmDraw` nos dois anéis, mas a cópia do quadro par saía (0,0,0,0) mesmo com `VITASHARP_UB_LATE=0`. A
  causa é o corte da cópia de um buffer de uniforms "no próximo buffer ligado" (`SlotCopySize`): ele olhava os 15
  índices, inclusive os que o programa não lê e que guardam endereços de desenhos anteriores; num dos anéis um desses
  endereços caía dentro dos 80 bytes do bloco e a cópia parava antes da cor (o resto fica zerado). Agora só os buffers
  que o próprio programa lê limitam a cópia; `VITASHARP_UB_CLAMP=all` volta ao antigo. Prova (`skipsym.py`, recorte do
  símbolo em 2700-2763): com a correção 63 de 63 quadros acesos (`bugsB3_out/croix_fix`), com `=all` 32 de 64
  (`croix_clampall`), como antes (`croix_def`); `=frame` e `UB_LATE=0` não mudavam nada (`croix_frame`, `croix_late0`).
  O audit conta 365 cópias que o corte antigo encurtava nessa rodada
  Sonic & All-Stars Racing Transformed: trava em 3 de 3 boots (quadros 23-25, main 9482b0f); com
  `--trace-hle-frames=18:40:all` (mais lento) passou do ponto. Na trava a `AchievementRegisterThread` já terminou,
  enquanto na rodada boa ela ainda fazia 51 `sceNpTrophyGetTrophyInfo` no quadro 22: comparar a ordem
  registro de conquistas x `MainThread`. Blind Men, Zero Time Dilemma, The Deer God e Silent Hill: não testados
  nesta rodada.
- [x] Jak 1 (Jak and Daxter Collection, PCSA00080) no gameplay (teste do usuário na janela, 05/11/2026, main 57c57b1):
  a câmera fica fixa num trecho e não se move, e a tela fica preta no começo do gameplay; 20 fps (Vita: 30), uma
  queda de ~10 s para 0-1 fps; `sceKernelWaitLwCond` com timeout 18461 vezes. Dumps F8
  `gpudumps\PCSA00080_20261105_015823.vsgd` e `..._020221.vsgd`, log `logs61105_015542_PCSA00080.log`
  Concluído por decisão do usuário (06/11/2026): Jak and Daxter fechado, exceto o save state no gameplay (item abaixo)
- [x] Jak and Daxter Collection (PCSA00080), teste do usuário 05/11/2026 (main 84a12b1): ao escolher o Jak 1,
  `LoadExec app0:Jak1.self` e então `F3DPlaybackThread` cai com `Undefined instruction 0xEE002000 at 0x81256204 (ARM)`
  e o jogo trava (watchdog STALL, `main` segura o mutex FluxRndr); log `logs61105_003217_PCSA00080.log`.
  Corrigido na main pela 5378b1a (o LoadExec renova o espaço de endereços; a thread saltava para código HLE antigo).
  Prova: a sessão seguinte do usuário (`logs61105_015542_PCSA00080.log`, main 57c57b1) passa do LoadExec e chega ao
  gameplay do Jak 1; as 3 execuções do agente C dia 3 (`bugsC3_out/jak1_live_main/_fix/_final.log`, uma
  com o build da main) fazem o `LoadExec app0:Jak1.self` a partir do menu, a F3DPlaybackThread roda e não há
  `Undefined instruction` nem STALL; idem nos 10 logs com LoadExec de `bugsC_jak` (dia 1)
- [x] Jak and Daxter Collection (PCSA00080), menu (main 84a12b1, teste do usuário 05/11/2026): a prévia (MJPEG,
  textura 0x63FBC400) ainda pisca quando o Jak olha para a direita (provável: dois quadros seguidos com o mesmo
  tamanho, então a escolha pelo tamanho não distingue o quadro k do k+1). A 57c57b1 (quadros tirados na ordem de
  decodificação, 0/506 idas e voltas) é posterior ao relato; com o build do agente C dia 3 (`jak_menu`): 1 ida-e-volta
  em 527 quadros (1351→1352→1353, `bugsC3_out/menu1352.png`), contra 7/504 antes da 57c57b1 — falta o
  usuário confirmar olhando para a direita na main atual
  Concluído por decisão do usuário (06/11/2026): Jak and Daxter fechado, exceto o save state no gameplay (item abaixo)
- [ ] Achados do agente de bug C, dia 3 (05/11/2026, branch fix/bugs-games-3; dados em `bugsC3_*`).
  Corrigido: **Jak 1, flash de um quadro (faixa escura translúcida abaixo do horizonte)**. Com a ferramenta do item
  acima, o `--draw-limit` no `jak_flash.vsgd` (present 1928, faixa; 1927, sem) isola os desenhos 874-875: o 874 é um
  volume de sombra por stencil (z-fail: faces da frente INCR_WRAP, de trás DECR_WRAP, 4008 índices, sem cor) e o 875 um
  quadrado de tela cheia que multiplica o framebuffer onde o stencil != 0. Os vértices do volume são gravados pelo
  desenho 873 (programa de vértice com VLDST, `vsStore ub1@0x60606400`) e lidos como stream pelo 874; o renderer lê
  esses streams do espelho da CDRAM (`_gpuWritten`), mas descartava a faixa gravada se a CPU tivesse escrito em
  qualquer página da **janela de 1 MB** do slot desde a gravação — e a entrada do volume (ub0, 0x60605D40) fica na
  mesma página que a saída, reescrita pela CPU para o quadro k+2 enquanto o worker ainda está no k: a faixa caía
  para a cópia do guest (que nunca recebe o que a GPU grava), o volume saía de lixo e o stencil desbalanceado
  escurecia tudo abaixo do horizonte (traço `[mirror] ... written by the CPU since: back to guest memory (before draw
  id=875)` em `bugsC3_out/tr1928/trace_1928/draws.txt`). Correção (`ExpireGpuWritten`): só contam as páginas da parte
  lida e, para streams de 3+ páginas, só as páginas internas (as da ponta são compartilhadas com os dados vizinhos);
  streams pequenos e leituras mapeadas continuam caindo para o guest a qualquer escrita da CPU nas suas páginas
  (uma primeira tentativa só com "páginas internas" para tudo deixava malhas escritas pela CPU dentro da janela
  lendo o espelho velho: triângulos pretos no mar, `bugsC3_out/diff1954.png`). Prova: dump gravado com a correção
  (`bugsC3_out/jak_fix.vsgd`, 601 presents) reproduz sem a faixa (`tileflick.py`: 0 quadros com a faixa; o dump antigo
  dá a faixa em quadros alternados 1926-1930 em todo replay), o traço de 1928 mostra `[stream] id=866
  s0=0x60605800+16044: read from the mirror`, e 3 execuções ao vivo do estado do usuário (`jak1_live_main/_fix/_final`,
  619-643 quadros cada) sem a faixa (ela já era intermitente ao vivo: 5/618 na rodada anterior, 0 nas 3 de hoje, por
  isso a prova fica no dump). Novas linhas de traço (`--trace-draws`/F10): `st=` com ops/ref/máscaras do stencil,
  `vsStore ub<i>@addr` nos programas que gravam memória, `[stream]`/`[mirror]` com a origem de cada stream lido do
  espelho e as faixas descartadas. `replay-gpu --compare` dos 16 dumps do usuário + `jak_fix` contra a main
  (`bugsC3_cmp`, a cada 10 quadros): 14 idênticos; só mudam os do Jak 1 com sombras (`PCSA00080_20261105_015823` 24/71 quadros,
  `_020221` 1/54, `jak_fix` 41/61), todos pela correção: some a faixa/cunha escura nas rochas e aparece a sombra
  do Jak (`diff_001760.png`, `diff_001850.png`, `jak_fix_new/frame_002120.png`).
  Ainda aberto (Jak 1): (a) **céu preto por um quadro** (1 em 643 ao vivo, `jak1_live_final` quadro 1950; reproduz no
  replay de `jak_final.vsgd`): os desenhos do céu (24-40, vs 79BC61BE4C9F8178, 3-4 vértices em RAM, 4 buffers em
  rodízio 0x8DAC4080/0x8DC44080/0x8DDC4080/0x8DF44080) têm os mesmos uniforms e texturas, mas os vértices lidos no
  1950 (x=2510) são os do uso seguinte do mesmo buffer (1954; a sequência é -338, 152, ?, 1135, 1621 por quadro): a
  CPU (GOAL) já tinha reescrito o anel de vértices quando o worker leu o stream. Os cenários dos agentes rodam com
  `--cpu-mhz=0` (CPU sem ritmo), o que deixa o GOAL muito à frente; conferir com o ritmo padrão e, se persistir, ver
  o que o jogo espera para reusar o anel (notificação/`sceGxmNotificationWait`) — é a mesma classe do `ub-late`, mas
  para streams em RAM; (b) **quadros pretos depois de carregar o estado**: 30-48 presents pretos (`METRICS black`
  1782..1811 na main, 1735..1782 no build de hoje) entre o `Load state: GPU backend replaced` e a primeira imagem.
  Não é o renderer: no replay de `jak_final.vsgd` (`frames_index.txt`) cada um desses presents tem a cena inteira
  (~1450 desenhos, 1752-1782) e o último desenho é um quadrado de tela cheia opaco sem textura (vs 96859A639870B7F2,
  fs 687E44699E752BD2) que cobre tudo de preto; `--draw-limit=1462@1770` (sem ele) mostra o Jak e o cenário certos
  (`bugsC3_out/blk_lim1462/frame_001770.png`), e no 1783 o jogo para de emiti-lo (corte seco, sem fade).
  É o "blackout" do próprio jogo depois de carregar a área; falta saber se a duração é a do Vita (ver um vídeo do
  Vita/Vita3K) ou se o salto do relógio na carga (`guest clock +15832 ms` no resume) a estende; (c) **mar com buracos pretos por um quadro**
  (replay `jak_fix.vsgd` present 2112, `bugsC3_out/flash2112.png`): nesse quadro o jogo emitiu 52 desenhos a menos
  (1302 x 1354 no 2111), então vem do lado do jogo/CPU, não do renderer; (d) **20 fps** reproduz nos 3 testes (20,0
  fps, `--cpu-mhz=0`, 4 núcleos): CPU do jogo (`process cpu 70 ms/quadro` no log do usuário), fora do escopo deste
  item — desempenho; (e) **câmera fixa**: não reproduzida (a câmera segue o Jak nos 3 testes e no replay). Menu da
  coleção (prévia MJPEG): `jak_menu` com o build de hoje: 1 ida-e-volta em 527 quadros (1351→1352→1353), contra 0/506
  da 57c57b1 e 7/504 antes dela — dentro do ruído; falta o usuário confirmar olhando para a direita.
- [x] Jak 1, cena inteira escura em quadros alternados por segundos (gravação do usuário 06/11/2026 18:37, build
  19c2375: Daxter parado antes do menu de pausa, luma ~24 x ~36 quadro sim/quadro não, presents 5859-5889; agente C,
  fix/bugs-games-15, `bugsC15_jak`). Reproduz no replay do `gpu.vsgd` (5871, 5873, 5875 escuros). Causa:
  o jogo extruda os volumes de sombra com um programa que grava na memória, em duas janelas de 1 MB vizinhas que
  alterna por quadro (0x60501400 e 0x60601400); as faixas gravadas pela GPU dos dois quadros se juntam numa só, e
  quando a janela do desenho seguinte (a outra metade) achava uma página escrita pela CPU depois (dados do quadro
  seguinte), a faixa inteira era descartada, inclusive o volume que o quadro acabara de extrudar: o desenho do
  volume (`[stream] id=455`) lia a cópia velha da memória do guest, o stencil ficava desbalanceado e o quad de
  sombra (st=5) escurecia a tela. Corrigido: só as páginas que a CPU escreveu saem da faixa (o resto continua lido do
  espelho), e antes de juntar faixas antigas a uma gravação nova (que renova o carimbo) as escritas da CPU desde as
  antigas são conferidas. Replay: 5873/5875 claros (o 5871 continua escuro porque mostra um buffer importado do
  estado gravado). `replay-gpu --compare` contra a main: os 18 dumps do usuário idênticos; no da gravação só mudam
  5873/5875 (claros) e um canto de sombra no 5893. Jogo rodando (`rerun` da gravação a partir do estado, cópia em
  `bugsC15_jak/cap_*`): main 25 quadros seguidos alternando (presents 154-178), com a correção 5 (169-173, logo depois
  de carregar o estado) e depois estável. **Os 5 quadros iniciais (fix/bugs-games-16)**: corrida, não o estado: logo
  depois de carregar, o worker da GPU fica vários quadros atrás e a CPU já reescreveu (dados de quadros seguintes)
  páginas da faixa gravada pela GPU antes de o worker chegar ao desenho do volume; como a conferência via "escrito
  desde a gravação" sem limite, essas escritas futuras derrubavam a faixa (7-9 quadros alternando por rodada, número
  variando; VITASHARP_DISPLAY_QUEUE_STRICT=1 só diminuía). Corrigido: quando há programa que grava na memória, o guest
  faz uma varredura das páginas sujas ao fechar cada cena (`SceneGate.SweepEpoch`) e o worker só conta as escritas da
  CPU até o fim da cena do desenho que lê (`DirtyPages.WrittenBetween`); a faixa gravada leva o carimbo do fim da cena
  que gravou. Replay não muda (os comandos do dump não têm cena). `rerun` da gravação: antes 9 e 7 quadros alternando
  (`bugsC16_jak/rr1`, `rr2`), com a correção 0 e 0 (`rf1`, `rf2`; só um achado de 1 célula no 209, o mesmo
  tipo do 220/237 de antes), 20 fps iguais.
  Achado à parte (gravação 18:34, cutscene, preto no present 2155, prioridade menor): não é o espelho (nenhum
  programa grava na memória nessa cena); os desenhos do céu e do cenário saem pretos porque a textura 32x32 em
  0x630F2B40 (amostrada pelo céu, vs 79BC61BE4C9F8178) foi lida já reescrita pela CPU ((255,0,0) contra (255,3,33)
  no 2154): a mesma classe do "céu preto por um quadro" acima (a CPU à frente do worker reescreve dados que o quadro
  ainda vai ler). `--draw-limit=222@2155` mostra só alguns objetos (`bugsC15_jak/b222`).
  Tentativa (07/11/2026, agente C, fix/bugs-games-17, sem commit; patch em `bugsC17_texsnapshot_wip.patch`):
  cópia das texturas pequenas no fim da cena (o guest guarda os bytes das texturas de até 32x32 e depois 128x128 que
  os desenhos da cena amostram, reaproveitando a cópia enquanto as páginas não mudam; o worker usa essa cópia,
  `VITASHARP_TEX_SNAPSHOT=0` desliga). Funciona no mecanismo: no `rerun` com `VITASHARP_TRACE_TEX=630F2B40` o worker
  usou a cópia em 44 leituras em que a memória já tinha o quadro seguinte. Mas o `rerun` da gravação continua com
  quadros inteiros escuros (luma 1-9): desligado 6, ligado 6/0/0/5 em quatro rodadas (`bugsC17_jak/reruns_*`), então a
  textura do céu não é a única (ou não é a) causa; o próximo passo é `--trace-draws` num quadro escuro do `rerun` (não
  do replay) e comparar todas as leituras (texturas maiores, vértices, uniforms) com o quadro anterior.
  Causa achada e corrigida (08/11/2026, agente C, fix/bugs-games-19): o céu não é escrito pela CPU, vem de 7
  sceGxmTransferCopy por quadro (thread F3DPlaybackThread, logo depois do EndScene) de uma superfície 512x512 em
  0x6312BB40 (os "tiles" do céu/sol) para as texturas 0x630F2B40, 0x630F9B40 etc. A mesma memória é uma superfície
  360x204 desenhada antes no quadro. As duas são copiadas para a memória no fim da cena (writeback), mas só quando o
  quadro do Vulkan termina; a transferência roda no worker logo depois e, se o quadro ainda estava em voo, lia o 360x204
  (preto) do quadro anterior: as texturas do céu saíam zeradas e a cena inteira (iluminada por elas) ficava escura num
  quadro. Prova no dump da gravação nova do início do Jak 1 (`bugsC3_data/captures/PCSA00080_20261108_010555_tudo`,
  intro depois de New Game): no 1398 as duas texturas são (0,0,0,0) e com `--draw-limit` o quadro só fica escuro a
  partir do desenho 10 (o céu). Correção: o BeforeCallback também espera (Finish) quando um writeback ainda pendente
  cai na faixa que a transferência lê. Sem carga: base 1 de 2 gravações com 14 quadros escuros (a outra 0), com a
  correção 0 em 3 gravações (`..._013041_tudo`, `..._014457_tudo`, `..._023505_tudo`); fps igual (13-14 com o PC
  carregado por outros agentes). Validação parcial (o erro depende da carga); replay não muda (os callbacks do dump
  não leem superfícies)
- [x] Jak 1, agente C dia 5 (05/11/2026, fix/bugs-games-5; dados em `bugsC5_*`). (a) **Céu preto com o ritmo
  padrão da CPU** (sem `--cpu-mhz=0`, `bugsC5_scn/jak1_paced.txt`, build `bugsC3_bin`): 0 quadros com o céu preto em 619
  (`tileflick.py`: 3 achados de 1 tile cada, partículas), contra 1 em 1881 nas 3 rodadas sem ritmo do dia 3; raro demais
  para concluir com uma rodada, e o fps é o mesmo (20). (b) **Blackout depois de carregar o estado**: 31 presents
  pretos com ritmo (1806-1836, ~9-20 fps logo depois da carga) contra 47-48 sem ritmo: a duração acompanha o tempo, não
  a contagem de quadros, ou seja, é um temporizador do jogo. O `guest clock +15832 ms` do log não é um salto: é o
  relógio salvo no estado sendo restaurado (`GuestClock.RestoreMicroseconds` em `SaveStates.cs`; com ritmo deu
  `-2329 ms`, o relógio voltou para os 73,8 s do estado), então do ponto de vista do jogo o tempo é contínuo e não estica
  o blackout. Não há referência no Vita para um save state; no boot do Vita real (vídeo `B2hItpwydrQ`, Vita slim) o
  "Loading" some num fade e há ~1,5 s de preto antes do logo, a mesma ordem. (c) **Novo: o logo da Naughty Dog não
  aparece.** No Vita real, depois do "Sony Computer Entertainment presents / Loading", o Daxter corre pela tela e o
  logo da Naughty Dog entra com o Jak (~8 s), e só então vem o título. Aqui (`bugsC5_out/boot_strip.png`,
  `bugsC5_scn/jak1_boot.txt`, main a19cdbe) o Daxter aparece parado (1844-1904, quadros idênticos), depois ~110
  presents pretos e o título direto, sem o logo, e esse trecho rodou a 20 fps (não é a lentidão). Em seguida a tela de
  título cai para 0,4-2 fps com 25-55 MB alocados por quadro (`ALLOC` no log) e GC de geração 2 quase todo quadro: a
  lentidão da tela de título conhecida tem esse sinal, a investigar (quem aloca). (d) **Câmera fixa**: no replay do dump
  do usuário `PCSA00080_20261105_015823` (`bugsC5_out/cam015823.png`, a cada 60 presents) a câmera fica no mesmo lugar
  enquanto o Jak anda até sair do quadro, nos dois dumps; no estado do usuário com o lstick dos testes a câmera segue o
  Jak (`bugsC5_out/paced2.png`, mesmo lugar do mapa). A diferença é a entrada: o usuário jogou com um DualShock 4
  (touchpad e giroscópio mapeados, log `20261105_015542`). Próximo: repetir com `rstick`/`btap` segurado e com
  `tilt` para ver qual entrada trava a câmera. (e) Estranho: o estado `jak1_quick.vss` (gravado na 57c57b1) não carrega
  mais na main a19cdbe (`the save state was written by an incompatible VitaSharp build`: o esquema mudou, provavelmente
  com a 9482b0f), então o save state do usuário também não carrega na main atual
  (f) **479b54f não deixou o Jak mais lento** (pedido do lead): o estado do usuário com o build da fix/bugs-games-3
  (`bugsC3_bin`, com a 479b54f) e o mesmo build com ela revertida (`bugsC5_rev`, worktree `bugsC5rev`), 2 rodadas
  alternadas cada, `--profile` 1900-2350 (`bugsC5_out/jak_ab_fix1/2`, `jak_ab_rev1/2`): 20,0 fps nas quatro, intervalo
  p50 50,05 ms em todas, GPU worker 4,87/5,41 ms/quadro com e 4,80/4,64 sem (ruído; CPU total 27,7/30,3 x 26,2/27,7).
  Os 2 fps do usuário vieram da CPU estourada (o build do APK do agente C sem limite). (g) **Os 20 fps são o jogo
  pedindo 3 vblanks por troca**: o callback da fila de display chama `sceDisplayWaitSetFrameBufMulti(3, ...)` em todo
  quadro (`--trace-hle-frames=2000:2003`, `bugsC5_out/jak1_tr.log`) e o intervalo é 50,0 ms cravado; as threads quase
  só esperam (GOAL `wait:condvar DMADone` 70%, F3DPlaybackThread bloqueada em `sceGxmDisplayQueueAddEntry` 61-77%,
  main em `sceGxmWaitEvent`; GOAL 2,5 ms e GPU worker 5 ms de CPU por quadro). O jogo escolhe o intervalo: o dado do
  `AddEntry` (pilha 0x81FC0864) é um ponteiro (0x86A92F00, `--watch=81FC0864:4:2000:2001`, `bugsC5_out/jak1_watch.log`)
  e o callback (lr 0x810411B3) lê o 3 de outro lugar; provavelmente o tempo medido do quadro, que inclui a espera na
  fila cheia, então uma vez em 3 se mantém. No Vita o Jak 1 roda a 30. Próximo: `--watch-read` no callback para achar
  a variável do intervalo e quem a escreve
  Concluído por decisão do usuário (06/11/2026): Jak and Daxter fechado, exceto o save state no gameplay (item abaixo)
- [x] Jak 1, agente C dia 6 (05/11/2026, fix/bugs-games-6; `bugsC6_*`, cenários `bugsC6_scn/jak1_*.txt`).
  (a) **Tela de título lenta: corrigido o grosso** (0,4-2,5 fps → 10-12 fps, alocação 19-55 MB → 0,2 MB/quadro). Cada
  quadro tem ~6300 desenhos com 16 KB de uniforms de vértice (27 MB/quadro): passava do heap de uniforms (48 MB), o
  `UniformArena` não guardava nenhum bloco gerenciado de reserva (o limite de 16 contava os 48 do heap) e alocava 1 MB
  novo por bloco (GC gen 2 quase todo quadro); o anel de upload de 64 MB estourava e cada upload seguinte criava um
  buffer próprio (vkAllocateMemory), e os anéis de vértices de 16 KB+ reescritos pela CPU ganhavam um buffer novo por
  reescrita (dotnet-trace no replay: CreateHostBuffer 752 ms e DestroyBuffer 994 ms de 3,4 s). Agora: blocos de
  overflow de 16 MB, faixas voláteis pelo anel, reservas gerenciadas contadas à parte (`bugsC6_out/jak1_2x.log` x
  `jak1_2x_new.log` x `jak1_2x_new2.log`). O que sobra (10-12 fps, `jak1_prof_new.log`): `pacing` 58 ms/quadro, a CPU
  emulada no modelo de ciclos: F3DPlaybackThread 9,1 M ciclos de guest + 11,8 M de HLE cobrados por quadro (9568
  `sceGxmSetVertexStream`, 5177 `sceGxmDraw` e `ReserveVertexDefaultUniformBuffer`), mais que um quadro a 30 fps;
  ver se o custo cobrado por chamada de HLE do GXM é realista (modelo de tempo, fora do escopo). (b) **Logo da
  Naughty Dog não aparece**: não é a entrada (sem o 3º Cross, f1600, é igual: `bugsC3_data/tests/jak1_2x/sheet_2.png`,
  `sheet_3.png`); no dump do trecho preto (`bugsC6_out/jak_black.vsgd`, presents 1940-1944, trace em `jak_black_tr`)
  o jogo emite 19 desenhos por quadro, incluindo malhas 3D (5901, 14568, 3300 vértices) com texturas pequenas (4x4 a
  256x64), mas o alvo já está todo preto depois do desenho 9 (`--draw-limit=2/9/16@1942`, `jak_black_l*`): a cena
  sai preta, provavelmente um fade/cor do próprio jogo que não avança, como o Daxter parado (quadros idênticos
  1840-1904). Nos quadros do Daxter parado o ISO só faz `DelayThread(1000)` (nenhuma leitura de arquivo,
  `bugsC3_data/tests/jak1_hle/log.txt`). Próximo: ver de onde o GOAL tira o tempo das animações (relógio/vblank) nesse
  trecho. (c) **De onde vem o 3**: o callback da fila de display (0x81041126 no Jak1.self) lê o intervalo da palavra +4
  dos dados do callback (`ldr r0,[r4,#4]`, compara com 2 e 3) e chama `sceDisplayWaitSetFrameBufMulti(r0)`: o valor
  vem do jogo em cada `sceGxmDisplayQueueAddEntry`. Não achei quem o escreve (o endereço do dia 5, 0x86A92F00, não
  é o desta sessão; o trace de HLE não mostrou o AddEntry). Provável: o jogo escolhe 3 vblanks porque o quadro dele
  passa de 33 ms (item a: pacing)
  Concluído por decisão do usuário (06/11/2026): Jak and Daxter fechado, exceto o save state no gameplay (item abaixo)
- [x] Jak 1, agente C dia 8 (06/11/2026, fix/bugs-games-8; `bugsC8_*`, cenários `bugsC8_scn/jak_*.txt`).
  (a) **Tela de título: custo de HLE corrigido** (commit "HLE cost: libgxm accessors..."): 14,4 → 18,8 fps, F3D
  11,7 → 3,8 M ciclos de HLE/quadro (`bugsC8_out/jak_t_ref` x `jak_t_new`). (b) **Os 20 fps do gameplay são o
  nativo**: a palavra +4 dos dados do `AddEntry` é o campo [obj+0x98] do device F3D, gravado uma vez na inicialização
  a partir da config (Jak1.self 0x8103DD82, = 2); o callback da fila (0x81041126), com 3 buffers, chama
  `sceDisplayWaitSetFrameBufMulti(2+1)` antes do `SetFrameBuf` (igual ao Vita3K: último setbuf + n). No vídeo de
  captura direta Vita x PS3 (`TyUuSltNINs`, 60-100 s, `bugsC8_yt/jakcmp.*`) a metade do Vita tem o padrão "2 quadros
  novos a cada 3" (20 fps) o tempo todo e a do PS3 30. A gravação do usuário `PCSA00080_20261106_002008_tudo` também:
  vblank +3 em todo quadro, GPU 3,6 ms. (c) **Corrigido: callbacks em `sceKernelDelayThreadCB`.** O Jak registra um
  callback de vblank numa thread que fica em `DelayThreadCB(0xFFFFFFFF)`; o callback chama o handler de vsync do GOAL
  (0x8100C4B2), que conta os vblanks do jogo (0x816B3B88). O nosso DelayThreadCB só rodava callbacks na volta, ou seja
  nunca: o contador ficava em 0xFFFFFFFF (`--peek`, `bugsC8_out/jak_cb3`) e o que anda por vsync parava: o Daxter
  parado e o logo da Naughty Dog que não aparecia (agora f1900-2190 o Daxter anda e o logo entra com o Jak, e vem a
  abertura do título: `bugsC3_data/tests/jak_boot/sheet*.png`; contador +90 em 30 quadros). Gameplay com a correção:
  20,0 fps, câmera segue o Jak (`bugsC3_data/tests/jak_g2/sheet_2.png`). (d) Gravação do usuário: o rerun não roda
  (estado depois do LoadExec: `module (app0:Jak1.self) is not loaded the same way`, com ou sem `--self=Jak1.self`;
  passado ao agente de save state). No `gpu.vsgd` (replay igual ao ao vivo) os 116 quadros pretos são um quad de tela
  cheia com cor (0,0,0,0) e blend ADD 1-srcA/srcA (desenho 1450 do present 9600) que some no quadro seguinte ao X
  (present 9646, pad 0x4000 em 9645), e depois a câmera fica parada com o Jak andando: os dois combinam com o relógio
  de vsync parado do item (c); falta o usuário confirmar no mesmo trecho com o build novo
  Concluído por decisão do usuário (06/11/2026): Jak and Daxter fechado, exceto o save state no gameplay (item abaixo)
  Confirmado pelo usuário (06/11/2026): ok
- [x] Flicker no desktop, Croixleur Sigma (PCSG00537; 31/10/2026, main 7918e46, captura F10
  `diagnostics\PCSG00537_20261031_170452`): nos diálogos da história a imagem alterna entre dois quadros diferentes,
  um sim e outro não (diferença n→n+1 constante, n→n+2 zero em 105 quadros). Só muda a faixa decorativa de cima (os
  ~40 px do topo, largura toda, com o botão SKIP): ela aparece num quadro e some no seguinte. Provavelmente o jogo
  desenha essa faixa só uma vez em cada buffer e depois só redesenha o resto, contando que o conteúdo do buffer
  continua lá; ver se o emulador perde o conteúdo anterior do framebuffer (load op / imagem nova por quadro, troca de
  buffer, escrita de volta na memória) quando a cena não cobre a tela inteira.
  Achados (04/11/2026, fix/user-bugs): reproduz com o `croixleur_boot` (save do usuário) até f2700 e
  `--gpu-dump-at=2600:2606`; no `replay-gpu` a faixa está nos quadros 2600-2601 (vêm dos alvos restaurados no começo
  do dump) e some de 2602 em diante. Os 88 desenhos são os mesmos em todo quadro e nenhum desenha a faixa: ela fica
  na memória de cada um dos 3 framebuffers (0x60280000/0x60480000/0x60680000) e sobrevive porque o desenho 7 (fs
  A96C3889E4BBF59E, tela cheia) lê o próprio framebuffer como `t1` e o recompõe. Ou seja, o conteúdo de um
  framebuffer tem que continuar o mesmo depois de 3 quadros (2 apresentações de outros buffers no meio); aqui ele se
  perde (provável: a imagem do alvo é descartada/recriada a partir da memória do guest, que nunca recebeu o
  writeback). Fica no renderer (Memory/Draw, área dos agentes de desempenho); o dump acima serve de teste
  (`replay-gpu --compare`)
  Achados (04/11/2026, fix/user-bugs-2): a hipótese acima estava errada. A faixa é desenhada em todo quadro (desenho 12,
  textura 0x621E8000 960x40 UBC3 swizzled-arbitrary; a de baixo é o desenho 15, 0x615C0000). Num bloco comprimido
  swizzled-arbitrary os blocos seguem a ordem morton na grade potência de dois (240x10 blocos → 256x16), então a
  textura ocupa 61120 bytes, mas `ComputeLayout` contava 38400: o cache de texturas e o dump de GPU cobriam só os
  primeiros 2/3, e no replay o terço direito das faixas (e a ponta esquerda da caixa de diálogo, 720x216) saía preto.
  Corrigido o tamanho (replay do `croixleur_boot` com as faixas inteiras de 2600 a 2606; replays de PCSF00476
  idênticos). O pisca-pisca ao vivo (a faixa inteira some em quadros alternados) NÃO reproduziu headless, nem com as
  opções do usuário (`--async-shaders=on --shaderc-opt=zero`, modo de depuração): falta o usuário confirmar na
  janela com esta versão; se continuar, gravar um F8 (dump de GPU) durante o pisca-pisca
  Ainda pisca (relato do usuário, 04/11/2026, depois da correção do tamanho da textura): o botão SKIP continua
  piscando; validar ao vivo com o detector de flicker e uma captura F8 do usuário no momento
  Confirmado pelo usuário (06/11/2026): o SKIP não pisca mais (b6949d7)
- [x] Croixleur Sigma (PCSG00537; no Vita3K trava ao gerar shaders): roda a ~30 fps, mas no início 3 cenas são
  puladas com `vkAllocateMemory(host buffer) failed: ErrorOutOfDeviceMemory` e "out of host memory for 0,0 MB" com a
  VRAM a 4% e o heap do host a 2% (31/10/2026, main e74a747, log `20261031_030841_PCSG00537`); a captura F10 também
  falhou pelo mesmo erro. Causa: o jogo chama `sceGxmColorSurfaceInit` com data NULL para os framebuffers (a memória
  vem depois com `sceGxmColorSurfaceSetData`); a HLE recusava (0x805B0004) e a superfície ficava zerada, com 0x0
  pixels. O renderer criava um render target 0x0, e os buffers de 0 bytes do writeback, da leitura do quadro e da
  captura F10 falhavam no driver como falta de memória (era assim a cada cena, não só nas 3 primeiras: 993 falhas na
  sessão). O `a662f3b` (fix/ztd) já aceita data NULL; agora uma superfície sem tamanho também vira cena sem cor em
  `ReadColorSurface` em vez de chegar ao renderer (31/10/2026, branch fix/croixleur): 0 falhas de alocação, avisos,
  logos, ajuda, abertura e menu do título a 30 fps. Mais dois defeitos achados no caminho, também corrigidos: (1) o jogo
  faz o skinning na GPU com programas de vértice que gravam os vértices transformados na memória (VLDST store, 4
  programas; provavelmente o que derruba o Vita3K ao gerar shaders) e o tradutor descartava essas gravações: os modelos
  3D não apareciam e cada desenho de skinning copiava 1 MB para o espelho da CDRAM (≈60 MB por quadro, o jogo caía
  para 4–6 fps nos diálogos). Agora `storeMemory` grava no espelho (`VulkanRenderer.Mapped.cs`), as faixas gravadas
  não são mais copiadas da memória do guest e os streams de vértices dentro delas são lidos do espelho. (2) O bloom
  encadeia passes por regiões de um alvo 1024x512 com o clip de região do alvo inteiro, então cada passe lia o
  conteúdo do início da cena (o quadro anterior) e o resultado acumulava até ficar branco (fundo branco no título, na
  seleção de personagem e nos diálogos); a área do desenho agora é recortada pelo viewport. Seleção de personagem,
  diálogos e o cenário 3D a ~30 fps (`croixleur_boot`)
- [ ] Memória gravada por programas de shader (VLDST store): só funciona para endereços na CDRAM e para palavras de 32
  bits; o que a GPU grava não volta para a memória do guest (a CPU que ler essa memória vê o conteúdo antigo), e uma
  escrita da CPU em qualquer página da faixa devolve a faixa inteira à cópia do guest. Ver se algum jogo lê esses
  dados na CPU ou grava fora da CDRAM (aviso `a program stores to memory at ..., outside CDRAM`) (31/10/2026, branch
  fix/croixleur)
  Conferido (10/11/2026, agente B, fix/bugs-b-2): nenhum dos 11 logs e 13 gravações do usuário nem as execuções dos
  agentes no `<scratchpad>` têm o aviso `a program stores to memory at ..., outside CDRAM`; o único jogo conhecido com
  VLDST store é o Croixleur (CDRAM). Sem jogo que precise, não mexi (fica aberto até aparecer o aviso)

- [x] The Deer God (PCSB01070; no Vita3K trava no boot): engasgos no gameplay (30/10/2026, main 4603a03, logs
  `20261030_230111_PCSB01070` e `20261030_230208_PCSB01070`). Roda a 60 fps, mas cai de repente para 12–16 fps por
  ~5 s (quadros ~1500 e ~2200) sem nenhuma linha no log; uma troca de cena (thread `UnityPreload`) deixou a tela parada
  por ~15 s (watchdog `STALL`, quadro 3813); e no fim da segunda sessão o fps caiu aos poucos (49 → 32 → 15 → 8 em
  25 s). Áudio sem underruns. Captura F10 durante uma queda (`diagnostics\PCSB01070_20261030_230505`, quadros
  1648–1676): cada quadro leva 45–127 ms e quase todo esse tempo é `guestGpuWait` (60–115 ms; num quadro normal são
  ~14 ms de vsync), com os mesmos ~390 desenhos, nenhum shader/pipeline/textura novo e a GPU do PC ocupada só
  ~1,1 ms por quadro (esperas de fence 0,18 ms/quadro). Ou seja, o jogo espera um sinal da "GPU" (fila de exibição,
  notificação ou sync do GXM) que o emulador entrega atrasado, e não é a GPU que está lenta. A
  `UnityGfxDeviceWorker` estava rodando. Conferir se não é só a CPU do PC disputada por um build em paralelo (havia
  um agente compilando o APK naquela hora); se repetir com o PC livre, ver quem sinaliza essa espera. Achados
  (04/11/2026, fix/user-bugs): o jogo não espera vblank nenhum — o callback da fila de exibição chama
  `sceDisplaySetFrameBuf(…, 0)` (IMMEDIATE) e nada chama `sceDisplayWait*`; o único freio são os 2
  `sceGxmNotificationWait` por quadro da `UnityGfxDeviceWorker`. Sem janela (teste) ele roda a 160-620 fps; com
  janela, o ritmo vem de quando a apresentação do host libera o quadro e as notificações. Ou seja, os engasgos com
  `guestGpuWait` alto são a espera das notificações atrás da apresentação do host. Ideia genérica a decidir com os
  agentes de desempenho (mexe no ritmo de todos os jogos): no modo IMMEDIATE, no máximo uma troca de buffer por vblank
  no `SetFrameBuf` (o painel não mostra mais que isso), em vez de depender da apresentação do host
  Implementado atrás de chave (05/11/2026, fix/bugs-games-4): `VITASHARP_DISPLAY_IMMEDIATE_PACE=1` faz um
  `sceDisplaySetFrameBuf` IMMEDIATE esperar o próximo vblank quando já houve uma troca neste (contador "IMMEDIATE
  flips held" no resumo da sessão). Medido sem janela (`deergod` 1800 quadros, `bugsB3_out/deer_pace` x
  `deer_base`): sem a chave 162-170 fps; com a chave 60,0 fps cravados (1805 de 1855 trocas seguradas; as quedas no
  fim da rodada foram outros emuladores entrando na CPU). Falta o teste que importa, na janela (engasgos com
  `guestGpuWait` alto), que precisa da tela do usuário: rodar o jogo com a chave e comparar o F10/FrameLedger com o
  de antes. Regressão com a chave ligada: Devious Dungeon (59,9 fps), F1 2011 (25 fps, como antes) e Uncharted (60 fps
  no boot) usam NEXTFRAME, a chave não entra (contador 0) — rodadas `bugsB3_out/dd_pace`, `f1_pace`, `uc_pace`.
  Medido de novo (08/11/2026, perf/desktop-11, main 8984522 + perf/desktop-11, janela oculta, 4 rodadas de 150 s
  alternando sem/com `VITASHARP_DISPLAY_IMMEDIATE_PACE=1`, `perf11_out/deer*`): com o PC livre o gameplay
  fica em 55-61 fps nos dois modos (janelas de 5 s: 58-61, uma de 55); as quedas para 0-25 fps só apareceram nas
  rodadas em que outros agentes compilavam (CPU do PC a 90-100%: até os timers do processo atrasavam 5-9 s, e os
  quadros mostram `guestGpuWait` de 100-245 ms), então os engasgos com `guestGpuWait` alto são a CPU do PC disputada,
  não um defeito achado do emulador; a chave não muda isso. A tela parada de ~12-15 s é sempre no quadro 2752 (a
  troca de cena depois do menu): é carga de verdade do jogo. Com `--profile` (sem janela, com a chave para ter o ritmo
  da janela; `deer_prof_pace`) a thread `UnityPreload` faz 212 M ciclos/s de código + 61 M de HLE (40% rodando, 30% no
  ritmo da CPU emulada a 333 MHz, 18% em `sceIoPread`), enquanto a `Unity_main_thread` dorme em `sceKernelDelayThread`;
  o quadro 2753 leva 11,8 s (fileIo 4,2 s, ritmo da CPU 3,7 s). Sem a chave e sem janela o jogo corre a ~280 fps, as
  entradas por número de quadro caem em outros momentos e essa carga nem acontece na rodada. Nada mudado: a carga
  segue o relógio emulado de 333 MHz e o tempo de leitura emulado; falta comparar o tempo dessa carga com um vídeo do
  Vita de verdade antes de mexer no ritmo de leitura ou da CPU
  Desligada por padrão até o usuário confirmar na janela.
  Agente A (10/11/2026, main bfae931, janela oculta `VITASHARP_HIDDEN_WINDOW=1`, cenário `deergod` com `--data=`): sem
  medida útil. A janela oculta não segura o ritmo (122-650 fps no menu e no gameplay), as entradas por número de quadro
  caem fora de hora, e a CPU do PC ficou em 87-100% de 66 s a 144 s da rodada (outros agentes); as duas paradas de 5-6 s
  (quadros 11255 e 23978) são as trocas de cena já conhecidas. Medir de novo só com o PC livre e na janela visível do
  usuário (pedir ao lead), ou com uma gravação F8 do usuário durante um engasgo
  Medido na janela oculta (agente C dia 11, 06/11/2026, fix/bugs-games-10, `VITASHARP_HIDDEN_WINDOW=1`, 150 s, Cross a
  cada 200 quadros; `bugsC10_out/deer_off`, `deer_on`): sem a chave, amostras de 5 s a 60-63 fps com duas
  quedas (41, 46) e a parada da troca de cena (watchdog `STALL` 5 s, quadro 2752); com a chave, 59-60 fps, uma queda
  (45) e a mesma parada (6 s, quadro 2754). Os engasgos do relato (12-16 fps por ~5 s) não aparecem em nenhum dos dois
  com o PC ocioso, então não há ganho medido para ligar por padrão. Com a chave na janela, Devious Dungeon 60, F1 2011
  25 no menu / 60 depois, Uncharted 60 e Killzone 30 (title) como antes (`reg_dd/f1/uc/kz`, com a main eb950c1).
  Mantida desligada; falta a janela de verdade do usuário (vsync do monitor)
  Ligada por padrão (10/11/2026, agente B, fix/bugs-b-4): no Vita um flip IMMEDIATE só troca o ponteiro de varredura e
  o painel mostra no máximo um quadro por vblank; quem freia um jogo que vira IMMEDIATE sem esperar vblank (Unity "don't
  sync": The Deer God, Akiba's Beat) é a GPU dele, que aqui nunca freia, e ele desenhava 140-650 quadros por segundo
  que ninguém vê (CPU do PC jogada fora). O Vita3K trata IMMEDIATE como NEXTFRAME sem esperar nada (o ritmo vem do vsync
  do host), por isso não serve de referência aqui. Um jogo mais lento que o painel nunca tem dois flips no mesmo vblank
  e não muda; o modo "sem limite" da taxa de quadros desliga a regra; `VITASHARP_DISPLAY_IMMEDIATE_PACE=0` volta ao
  antigo. Medido sem janela (`<scratchpad>\bB4\runs\deer1`, cenário `deergod` com uma raiz sem saves): menu e gameplay
  56-60 fps (antes 142-147), 2966 flips segurados; a parada de ~5 s no quadro 2752 é a troca de cena já conhecida (carga
  do jogo). Os engasgos do relato de 30/10 já tinham sido atribuídos à CPU do PC dividida; não reapareceram

- [x] Sonic & All-Stars Racing Transformed (PCSE00056; no Vita3K também não abre): trava no quadro 24, antes dos
  logos (watchdog `STALL` aos 6 s e 31 s; 30/10/2026, main 4603a03, log `20261030_225954_PCSE00056`). A
  `MainThread`, a `AchievementIssueThread`, a `SumoPlatform` e o `CRI Server Manager` ficam rodando (polling), o
  resto espera em semáforos/eventflags (`SlSemaphore_Platform`, `SlThreadGateBank`); a única função que falta é
  `ScePspnetAdhoc::sceNetAdhocInit` (NID 0x87805E13), chamada pela MainThread logo antes. Ver o que a MainThread e a
  `SumoPlatform` esperam (provavelmente rede/PSN ou conquistas) e a instrução `strex r1, r1, [r2]` (Rd = Rt) que
  derruba o Vita3K. Achados (04/11/2026, fix/user-bugs): não é determinístico — numa rodada passou do quadro 24 e
  seguiu a 30 fps (1029 quadros, 11750 desenhos, mas as imagens pretas), nas outras parou no 25/26. Parado, a
  `MainThread` só faz `sceTouchPeek`+`sceTouchRead` (lr 0x81118615/0x81118AFD) e trava/destrava o lwmutex 0x81896328 a
  cada ~16 ms, sem nenhuma outra função desde o quadro 23: espera em memória algo que outra thread deveria
  marcar; a `SumoPlatform` faz `sceKernelDelayThread(1 s)` em loop no mesmo lwmutex e a `AchievementIssueThread`
  também gira. Próximo: `--watch` na variável que a `MainThread` testa no loop (perto de 0x81118AFD) para ver quem
  a escreveria, e comparar uma rodada que passa com uma que para
  Corrigido (05/11/2026, fix/bugs-games-4): não era uma variável. O loop em 0x81118608-0x81118AEE é a leitura do
  painel de toque: `while (sceTouchPeek(porta, &d, 1) > 0) { se o timestamp mudou, sceTouchRead(porta, buf, 16);
  processa; }`, e só sai quando o Peek devolve 0 — o nosso devolvia sempre 1 (o Vita3K também, por isso trava lá).
  Agora o Peek devolve 0 quando não há amostra mais nova que o último `sceTouchRead` da porta (o buffer continua
  preenchido com a amostra mais nova; portas que o jogo nunca lê seguem como antes). `VITASHARP_TOUCH_PEEK_UNREAD=0`
  volta ao antigo. Prova: 3 de 3 boots passam do quadro 25 (logos SEGA/Sumo/CRIWARE e a tela legal; 400 quadros a
  30 fps, `bugsB3_out/sonic_fix1..3`), e com a chave desligada o mesmo build trava no quadro 23
  (`bugsB3_out/sonic_ab0`); na rodada boa de antes a thread nem entrava nesse loop (ordem das threads). O cenário
  `sasrt` inteiro (2400 quadros) agora passa pelos logos, o filme de abertura, o título "PRESS START", o aviso de
  autosave e a criação do save (`bugsB3_out/sonic_full/sheet.png`); falta o usuário jogar uma corrida
  O usuário ainda travava com a main sem o 63c125a e mandou dois "Gravar tudo" incompletos (`PCSE00056_20261105_204517`
  e `_204848`, estado no quadro 23-25 já travado). Com o build da branch o `state_start.vss` dele destrava a partir do
  estado preso (382 quadros a 30 fps depois da carga, `bugsB3_out/sonic_st1`; com `VITASHARP_TOUCH_PEEK_UNREAD=0`
  continua parado, `sonic_st0`), e o boot limpo passa em 3 de 3 (`sonic_boot1..3`, 351-378 quadros a 30 fps). As
  gravações saíam incompletas por causa do Gravar tudo (corrigido, ver docs/notes/session-capture.md)
  Confirmado no jogo pelo usuário (05/11/2026, main da8372d): abre e funciona

- [x] FIFA 13 (PCSE00093): depois da correção da malha esticada (ossos lidos até 16 KB), as texturas dos jogadores,
  principalmente do Messi, aparecem erradas (relato do usuário, 04/11/2026; captura F10
  `diagnostics\PCSE00093_20261104_145125` e dump de GPU `gpudumps\PCSE00093_20261104_145113.vsgd`). Conferir no dump
  quais texturas e formatos o corpo/rosto/uniforme usam e comparar com o esperado; ver se é o mesmo problema de ordem
  dos canais RGBA/BGRA das superfícies do Gal Gunvolt, um formato/layout de textura (swizzle, compressão) ou o
  conteúdo de uma superfície renderizada reaproveitada como textura
  Achados e correção (04/11/2026, fix/user-bugs-3): os brilhos ciano/verdes no uniforme vêm dos dois cubos de
  reflexo 128x128 UBC3 de um nível (`t3`/`t4` do corpo, 0x6284E700/0x6280C700, contagem crua de mips 0): as faces
  1-5 saíam com blocos de lixo. A memória do dump mostra 16 KB de dados e 6 KB de intervalo repetidos, ou seja,
  faces a 22528 bytes (a cadeia 64x64..1x1 reservada e alinhamento de 2 KB, a regra do Vita3K); o
  `TextureLayout.CubeFaceStride` empacotava todo cubo de um nível (regra criada para os cubos 32x32 do Unit 13 e do
  Silent Hill). Agora só faces de um nível de até 32x32 ficam empacotadas; as maiores seguem a regra do Vita3K. O
  dump do usuário tem só um quadro e guarda apenas a memória que o renderer antigo leu, então o replay não mostra a
  diferença. Confirmado no jogo pelo usuário (04/11/2026): os uniformes estão certos
- [x] Desktop: F11 entra em tela cheia mas não volta para a janela (relato do usuário, 04/11/2026, de novo depois da
  correção que restaura tamanho e posição em `WindowHost.ToggleFullscreen`). Corrigido (fix/system-apps-4): o modo é
  guardado no `WindowHost` (não se lê mais o `WindowState` do Silk.NET), a saída da tela cheia chama o GLFW direto
  (`glfwSetWindowMonitor(janela, NULL, x, y, w, h, GLFW_DONT_CARE)`) e a repetição de tecla não alterna duas vezes.
  Conferido com Sonic Mania e `--input=key@f200:F11,...` (F11 agora é tecla de host nos scripts): aberto em janela e
  com `--fullscreen`, quatro alternâncias cada, o log mostra "monitor attached: False" ao sair e a swapchain volta a
  960x544 a 60 fps
  Confirmado pelo usuário (05/11/2026)
- [x] MotorStorm RC (PCSA00034): trava na tela de termos (relato do usuário, 04/11/2026, main 5d4b998, log
  `logs61104_201525_PCSA00034.log`). Roda a ~40 fps até o quadro 756 e então nenhum quadro novo (watchdog STALL).
  No relatório do travamento a thread `main` está **Dormant com pc=0x00000000** (lr 0x8114669F): ela parou, não está
  esperando nada, logo depois de uma série de chamadas de rede/PSN que falham (sceHttpInit 0x80431020, sceNetCtlInit
  0x80412102, TUS/TSS/Commerce2, `sceNpSnsFacebookInit` sem implementação). Provável salto para um ponteiro nulo
  (callback não registrado) ou saída da thread no caminho de erro; as threads de jobs ficam em loop de
  `sceKernelWaitSema` com timeout
  Corrigido (05/11/2026: `sceNpCommerce2CreateCtx` com 2 argumentos e o key off do NGS terminando a voz na próxima
  atualização, com os callbacks fora da trava) e confirmado no jogo pelo usuário
  **Regressão** achada na varredura de compatibilidade (agente A, 10/11/2026, main 5843bbd): o jogo parava no quadro 1
  (180 s, "Run finished: 1 frames"; a `main` girando num laço de lock/unlock do lwmutex 0x877E50D8 e a `async_2`
  esperando para sempre na lwcond). Com `VITASHARP_HLE_GUEST_CODE=nolw` rodava. Causa: o caminho rápido do
  `sceKernelSignalLwCond` em código guest (Hle/HleGuestCode) lê a contagem de espera em +8 da área de trabalho que
  recebe, mas o jogo usa **cópias** da área da lwcond: criada em 0x877E5978, espera por uma cópia na pilha da thread
  (0x8A3A5E98) e sinaliza por outra (0x877E5330); só a original recebe a contagem, a cópia tinha 0 e o sinal sumia sem
  entrar no kernel. Correção genérica: a criação grava em +12 o próprio endereço da área (`CondVar.LwSelfOffset`) e o
  caminho rápido manda para o kernel qualquer área cujo +12 não seja ela mesma (uma cópia), como o kernel de verdade,
  que acha a lwcond pelo UID. Depois: 4865 quadros em 180 s, logo da Havok, o aviso de salvamento e o tutorial de
  condução (`<scratchpad>\bA\proofs\motorstorm_fix_sheet.png`); teste `LwCondSignalWithoutWaitersStaysInGuestCode`
  com o caso da cópia
- [x] Unity: as 10 "importações de variável sem resolver" do `Il2CppAssemblies` (NIDs 338B0BD1, 3A451B91, 550DFE23,
  56354517, 5EF67445, 6ACA1D57, 71A78083, 79F9DB75, AA0340C3, F9A1DE92, importadas pelo `UnityPlayer_536/537_Release`;
  Akiba's Beat e God Wars, 04/11/2026, pedido do usuário). O aviso sai no boot (`Emulator.cs` ~522), antes de o jogo
  carregar `app0:/Media/Modules/Il2CppAssemblies.suprx` (~0,6 s depois); o `ModuleLoader.RegisterExports` aplica as
  relocações pendentes quando o módulo exporta a variável. Confirmar que o Il2CppAssemblies exporta as 10 e que as
  relocações foram aplicadas; se faltar alguma, implementar; e só avisar das que continuarem sem resolver depois que
  o módulo de origem carregar (hoje o aviso aparece em todo jogo Unity mesmo quando tudo é resolvido)
  Feito no 40e2fb5 (05/11/2026) e conferido em 06/11/2026 (fix/bugs-games-11): o Akiba's Beat loga `10 variable imports
  wait for a module exporting Il2CppAssemblies` no boot e, 0,3 s depois, `Il2CppAssemblies: 10 waiting variable imports
  bound`, sem aviso; o jogo roda até a primeira batalha (log `bugsC11_out/akiba4/log.txt`)
- [x] Akiba's Beat (PCSG00952, Unity): validar o jogo todo (pedido do usuário, 04/11/2026; sessão de 2,5 min, log
  `logs61104_201229_PCSG00952.log`, main 5d4b998). No log: 60 fps quase o tempo todo, mas a imagem fica parada
  ~10 s (quadro 4171, 20:14:05-20:14:16, `frozen: identical image for 600 presents`) e ~4 s de tela preta em 20:13:46
  (talvez carregamento); 2,6 s de engasgo de shaders na primeira vez (91 desenhos em 14 quadros); 10 importações de
  variável sem resolver do `Il2CppAssemblies` (também no God Wars); 1 underrun de áudio do CRI Atom. A captura
  automática `PCSG00952_20261104_201246` é só a tela da Unity (falso positivo). Conferir com vídeos do Vita de
  verdade se as paradas são carregamento normal ou travamento, e jogar além do começo
  Agente C dia 11 (06/11/2026, fix/bugs-games-10, sem janela com `VITASHARP_DISPLAY_IMMEDIATE_PACE=1`, senão a Unity
  roda a 400-600 fps e o roteiro em quadros fica rápido demais): boot, logos (Acquire, Unity, CRIWARE, Live2D), aviso
  de auto-save, filme de abertura, título e a tela New Game com o diálogo de confirmação, como no vídeo do Vita
  `ju9W3ZiWABU` (lá também há ~10 s de preto antes do filme e um carregamento preto longo depois do New Game, então as
  telas pretas do relato podem ser normais). 300 s a 58 fps, 0 underruns, nenhuma imagem parada, 2-3 desenhos
  esperando shader (80 ms) só na 1ª vez. Não passou do diálogo: a versão japonesa confirma com Círculo e o "はい" fica
  acima de "戻る" (selecionado); o Start do roteiro chega cedo ou tarde conforme o boot (roteiros `bugsC10_scn/akiba*.txt`;
  usar `--nav` esperando a tela). Falta o jogo em si
  Agente C dia 12 (06/11/2026, fix/bugs-games-11): roteiro `tests/nav/akibas_beat_start.nav` (com
  `VITASHARP_DISPLAY_IMMEDIATE_PACE=1`; o `--nav` ganhou `lstick`/`rstick`): título, New Game, "はい", o filme de evento
  pulado (Start, "はい") e o primeiro calabouço: andar, virar, câmera, os ataques, o encontro com o primeiro inimigo
  ("Encounter"), a batalha com a 1ª caixa de tutorial e o passo "aproxime-se e afaste-se" (analógico à esquerda e à
  direita), pausa e volta. Os ataques ainda não aparecem: o tutorial pede o movimento antes (próximo passo do roteiro). 60 fps estáveis, 0 underruns, nenhuma imagem parada; avisos só de
  formatos de textura vistos pela 1ª vez e um `scene without colour surface`. Provas: `bugsC11_out/akiba5_sheet.png`,
  `akiba4_sheet.png`. Sem o pace, a Unity (flips IMMEDIATE) roda a ~460 fps sem janela: esperas em quadros não servem
  Confirmado pelo usuário (06/11/2026): ok
- [x] FIFA 13 (PCSE00093): na partida (Botafogo x Fluminense, main 5d4b998), a faixa do campo depois da linha lateral
  direita sai cinza-clara e granulada em vez de grama, e o áudio da torcida está "zuado" (relato do usuário,
  04/11/2026; captura F10 `diagnostics\PCSE00093_20261104_190454` com áudio, replay de 127 quadros e draw ring, dump
  `gpudumps\PCSE00093_20261104_190442.vsgd`). Pistas: `TEXTURE SUSPECT 0x60E8F6C0 UBC3 32x64 SUSPECT-SEAMS`, a
  profundidade 4X de uma cena multisample lida como textura (`samples are not stored`), formatos vistos pela primeira
  vez (UBC2 128x128 8 mips tipo 0x40000000, U8U8U8U8 32x32 tipo 0x60000000). Áudio: NGS 24 vozes, granularidade 512,
  48 kHz, sem underruns, então o problema é no conteúdo das vozes (mixagem/efeitos da torcida), não falta de buffer.
  **Áudio corrigido** (fix/system-apps-4): o EAAudioCore cria a rota (`sceNgsPatchCreateRouting`) de uma voz só
  quando `sceNgsVoiceGetOutputPatch` falha (eboot 0x8140F8D4 / 0x8140C8E3); o HLE devolvia OK com handle 0, então
  quatro vozes (uma submix na inicialização e as de stream do jogo) nunca chegavam ao mixer. Agora devolve
  `SCE_NGS_ERROR_PATCH_NOT_AVAIL` (0x804A000E) com handle nulo, como o Vita3K; as rotas são criadas e os
  `sceNgsPatchGetInfo(0)`/`SetVolumesMatrix(0)` com erro sumiram. Falta o usuário ouvir uma partida.
  **Faixa cinza: sem causa no emulador encontrada.** No dump (quadro 13426, uma cena de 717 desenhos), `--draw-limit`
  isola os desenhos 309-330 (vs 64BA7B40B8B8F6AA, fs F97686423E6187DF): é a malha do piso de concreto do estádio, com a
  textura 0x638A8100 UBC1 256x256 (concreto com papel picado, decodificada limpa: noise 0,017) e o lightmap
  0x63B9D400; o gramado (desenhado antes do 300) termina ali de verdade e nenhum desenho foi pulado. O granulado é o
  papel picado da própria textura. O SUSPECT 0x60E8F6C0 é um elemento do HUD (desenho 700) e a profundidade lida
  (desenhos 615/616, partículas aditivas) não muda a faixa. Sem referência do Vita (o Vita3K não abre o jogo) não dá
  para dizer se a cor/iluminação está errada; precisa de uma foto/vídeo do console no mesmo estádio
  Reaberto (04/11/2026, usuário em fe012ec): o áudio continua errado (narração repete o que acabou de dizer e picota;
  torcida não está boa) e há "pontos" no gramado. Achados (fix/user-bugs-5, `VITASHARP_NGS_TRACE=1` + `--audio-dump`:
  um WAV por voz NGS, linhas `[ngs]` com play/kill (com quem chamou), callbacks, tabela de buffers e hash do anel a cada
  volta): as vozes de torcida/narração são ATRAC9 tocando um anel em loop infinito (6720 ou 13440 bytes) que o
  EAAudioCore reabastece pela posição informada; o fim de cada anel tem superframes zerados (900 de 900 erros de decode
  são zeros), que o HLE pulava sem gastar tempo nem contar em bytes consumidos. Em várias voltas o jogo não reescreve
  nada do anel (hash igual) e a voz repete o mesmo conteúdo (repetições a cada 0,90 s / 2,99 s nos WAVs,
  `tools/audrep.py` da pasta de trabalho local), e há kill+play seguidos a cada 4-8 ticks (192 bytes) pela mesma função do jogo
  (lr 0x8140D0E5). Tocar o superframe ruim como silêncio contando bytes (como o Vita3K soma os contadores) não mudou a
  contagem de voltas sem reescrita; fica em `VITASHARP_AT9_ERROR_SILENCE=1` (A/B). Os módulos EQ/envelope vêm todos
  zerados, o Mixer da voz vem com ganho 1,0/1,0 (o HLE ignora o Mixer). O espectro do nosso mix tem ~20 dB a mais
  abaixo de 250 Hz que dois vídeos do Vita no YouTube (bMYfYTF_BHc, PZYkqAFhvFI), mas eles podem ter sido gravados do
  alto-falante. **Pontos no gramado**: no dump F8 do usuário (`PCSE00093_20261104_205631.vsgd`) há cunhas/triângulos
  escuros de 1 quadro saindo de um jogador (presents 11158, 11182, 11190), desenhados pela malha de sombra projetada
  dos jogadores (vs DCBB621AC8070CB0, 3285 vértices, paleta de ossos no buffer 1 de 16 KB): um vértice vai longe. O
  replay reproduz (o dado ruim está nos uniforms copiados na hora do draw). Com `VITASHARP_UB_LATE=1` (o worker copia
  os uniform buffers de novo antes de executar o draw, como o hardware que lê na execução) 31% dos draws do FIFA têm
  buffers alterados depois do draw, inclusive o da sombra. Em 2500 quadros de partida aqui as cunhas não apareceram
  nem com nem sem a chave, então não está validado; a chave custa ~25% de fps (cópia dupla)
  **Narração corrigida** (05/11/2026, fix/bugs-audio, falta o usuário ouvir): eram dois defeitos no HLE do NGS.
  (1) `sceNgsVoicePlay` zerava também os totais do estado (+16 amostras, +20 bytes); o EAAudioCore mede cada clipe
  como bytes totais menos uma base que ele guarda por stream (eboot 0x8140D02A-0x8140D0E4), então o total zerado
  parecia clipe terminado e a voz era morta 4-8 ticks depois de começar (29% das paradas da narração; agora 0-3%).
  Como o Vita3K, só posição, buffer e contadores "desde o key on" voltam a 0. (2) O fim de cada clipe no anel é
  preenchido com superframes zerados (não decodificam) e o jogo mata a voz quando os bytes consumidos chegam ao
  tamanho do clipe, enchimento incluído; pulando-os na hora sem contar bytes, a voz tocava a volta anterior do anel
  (a repetição). Agora um superframe que não decodifica toca como silêncio e conta bytes e amostras
  (`VITASHARP_AT9_ERROR_SILENCE=0` pula, contando bytes). Medida (contador de superframes "stale" do
  `VITASHARP_NGS_TRACE`: decodificados de novo de um lugar do anel que o jogo não reescreveu): reproduções da narração
  com dado velho 33 → 2 em duas partidas de 8 min lado a lado (as 2 são o próprio jogo tocando de novo um clipe curto
  que não reescreveu); `audrep.py` sem trechos repetidos nos WAVs da narração. O decodificador ATRAC9 bate com o
  atrac9 do FFmpeg nos mesmos superframes (dump `.at9raw` do trace; SNR 106 dB, só a polaridade invertida).
  **Abertura EA SPORTS**: o filme é `data/movies/bootflowintro_MARKER.vp6` (8 s, áudio do próprio jogo, sem voz NGS);
  no nosso WAV ele é contínuo e na mesma velocidade do vídeo do Vita real `ZBF54pOUQ1k` (janelas de 0,1 s com o mesmo
  deslocamento, sem repetição nem salto) e os logs do usuário não têm underrun; o "robótico"/engasgo não reproduz
  aqui, falta o usuário ouvir de novo. **Torcida**: as vozes de torcida não têm dado velho nem buraco; sem defeito
  medido (a diferença de graves para os vídeos do YouTube continua sem referência confiável).
  **Cunhas das sombras** (05/11/2026, fix/bugs-audio, falta o usuário conferir): as duas sessões do usuário com
  `VITASHARP_UB_LATE=1` (logs 20261104_235321/235540) não tiveram cunha em 1042 quadros dos dumps. Agora os uniform
  buffers ligados pelo jogo (0-13) são copiados uma vez só, pelo worker da GPU logo antes de executar o draw; o
  buffer padrão (14, anel reaproveitado) continua copiado na hora do draw; `VITASHARP_UB_LATE=0` volta ao antigo.
  Sem custo medido (partida de 18,6 mil quadros lado a lado, metades trocadas: worker 4,95 x 5,28 ms/quadro, main
  5,29 x 5,98). Nas janelas de 800 quadros gravadas aqui (`--vulkan --gpu-dump-at`) não há cunha nem com nem sem a
  mudança (`specks.py`: 0 quadros com >= 8 blocos), então a prova continua sendo a partida do usuário
  Confirmado pelo usuário (06/11/2026, main 0d298b4): todos os problemas do FIFA 13 corrigidos
- [x] FIFA 13 (PCSE00093; no Vita3K nem abre): no treino da Arena, uma malha enorme preta e branca se estica da
  frente da câmera até o meio da tela e se mexe a cada quadro (provavelmente o jogador com os vértices "explodidos":
  skinning — matrizes de osso, formato/índice dos atributos de osso ou o buffer de uniformes —, ou um triângulo que
  cruza o plano próximo sem recorte). Captura F10 em `%APPDATA%\VitaSharp\diagnostics\PCSE00093_20261030_204939`
  (127 quadros em `replay/`, 109 desenhos em `draws.txt`; aviso de profundidade de cena MSAA lida como textura
  X8U24 960x1088) (30/10/2026, main 4dbe06c). Reproduzido e isolado (04/11/2026, fix/user-bugs): com o save do
  usuário, `--input=start@f1900:f8,cross@f2300:f8,cross@f2700:f8,lstick@f3100:0:1:f400,lstick@f3500:-1:0:f400` e
  `--gpu-dump-at=3600:3603` o `replay-gpu` mostra a malha; `--draw-limit=79@3601` x `80@3601` dá o desenho 80 (corpo
  do Messi: vs ACBE979868E05D6E, fs 4BBB4BC975F36384, 16524 índices, skinning com o buffer 1 de 8 KB de matrizes
  3x4). Os índices dos ossos são U8 em +32 (`a[r20 f0x4]`, ex. 60/103) e os pesos U8N em +36 (`a[r16 f4x4]`, 0xAF+0x50
  = 1,0), então não passam do buffer (aumentar o `MaxUbInFloat` para 16 KB só sumiu com os jogadores: descartado). O
  shader multiplica os índices (`pa[5]`) por `sa[0].z` = 9,155553e-05 = 6/65535, o que sugere que o hardware entrega
  esse atributo U8 cru em outro formato (16 bits normalizado?) e o endereço do osso sai errado; o desenho 81 (mesmo
  layout) fica certo. Próximo: conferir no GXP o tipo do parâmetro `r20` (regformat/untyped) e como o Vita3K converte
  esse atributo antes da multiplicação.
  Corrigido (04/11/2026, fix/user-bugs-2): os índices e o fator 6/65535 estavam certos (VPCK F32→S16N dá 3·índice,
  48 bytes por osso). O GXP declara `Joint[180]` (60 ossos, buffer 1 de 2880 bytes), mas a malha do corpo usa ossos
  até 255 (12 KB); um buffer que termina num array era lido só até 8 KB (`MaxUbInFloat`), então os ossos 171-255
  liam o buffer 7 ou nada. Agora o limite desses buffers é 16 KB (`MaxUbArrayInFloat`, `GeneratorVersion` 20; a
  tentativa anterior de 16 KB provavelmente sumiu com os jogadores por usar o cache de shaders antigo, com outros
  offsets). Messi inteiro no treino; replays de Croixleur e PCSF00476 idênticos

- [x] Crazy Market (PCSE00291, joga normalmente; 30/10/2026, main 4dbe06c, log `20261030_204008_PCSE00291`): a
  biblioteca de som do jogo (SCREAM/SndStream 7.6.9) avisa 3456 vezes `sndsi_UpdateVoicePositionOfStream:
  locationDelta < 0, voiceLocation = 0x0, previous = 0x700…` — a posição de leitura que a voz NGS informa volta a 0
  em vez de avançar (estado do player em `sceNgsVoiceGetStateData`, `Ngs.cs`); conferir com o firmware/Vita3K qual
  campo é acumulado (pode causar música repetindo ou cortando em outros jogos com SndStream). Também:
  `sce_scream_api.cpp(1385): Invalid parameter` na inicialização do som e `sceNetInit` chamado duas vezes (o
  segundo recebe EBUSY, como no firmware; o jogo segue). Achados (04/11/2026): o campo que o SndStream acumula é o
  +8 do estado do player ("amostras geradas desde o key on", `Ngs.cs`), e ele só volta a 0 no `Transition`
  Available→Active (novo key on) ou no `sceNgsVoiceInit` com flag 2 — então o aviso aparece quando a voz do stream
  termina (buffer sem próximo → Finalizing → Available) e é ligada de novo, enquanto o SndStream acha que é a
  mesma reprodução. Próximo: logar as transições da voz do stream de música e ver se ela acaba por falta de buffer
  (fila vazia por atraso nosso) — aí o defeito é o fim da voz, não o contador
  Achados (04/11/2026, fix/user-bugs-3, sem correção ainda): reproduz com o save do usuário, aceitando os termos por
  toque (`tap@f1100:0.196:0.713,tap@f1300:0.69:0.90` e depois cross a cada 150 quadros): 4264 avisos em 100 s. A
  hipótese de fim de voz por buffer inválido na troca não se confirma: o único END_OF_DATA logado no swap é de um som
  de um buffer só (`next -1`, legítimo). Os avisos alternam leituras: o contador cresce (0x700, 0xB00, 0xF00..., passos
  de 0x400) e a leitura seguinte dá 0, ou seja, o SndStream lê a posição em dois lugares diferentes e um deles fica
  sempre zerado. As primeiras leituras de `sceNgsVoiceGetStateData` antes do primeiro aviso são da voz 0x81759310,
  módulo 1, 2048 bytes, tudo zero (+0/+4/+8 = 0) com a voz Active: provavelmente o SndStream lê o estado de um
  módulo (ou de uma segunda voz do stream) cujo `GuestState` nunca é preenchido por nós. Próximo passo: logar
  voz/módulo/tipo de módulo de todas as leituras com +8 = 0 junto com as não-zero, e ver qual módulo o firmware
  preenche (Vita3K: estado do player x estado do módulo de saída)
  Corrigido (05/11/2026, fix/bugs-audio): a voz do stream é ATRAC9 (rack ScreamAtrac9, superframe de 4 quadros = 1024
  amostras) e o módulo somava o superframe inteiro em "amostras geradas" (+8/+16) ao decodificá-lo; com ticks de 512
  amostras o contador ficava parado em ticks alternados, e o SndStream (eboot 0x8109EB18-0x8109EBB8: amostras menos o
  valor anterior, se > 0) devolvia posição 0 nesses ticks. Agora o contador soma o que cada tick entrega, como no
  módulo player. 6200 quadros com o save do usuário: 6283 avisos antes, 0 depois

- [x] Zero Time Dilemma (PCSE00781; no Vita3K trava no boot): 40 dos 105 shaders de fragmento leem o registrador global G16 (banco especial com o bit 0x40), que o tradutor USSE não conhece e troca por 0 (aviso `load from unsupported bank Global register 16`; 31/10/2026, branch fix/ztd, `shadertest PCSE00781`). As cenas parecem corretas, mas falta descobrir o que é o G16 (o Vita3K também não trata o banco GLOBAL). O jogo agora passa do boot (sceGxmColorSurfaceInit com data NULL) e roda as cenas da história a 30 fps; também aparecem `scene without colour surface` e um render target 1024x1024 amostrado enquanto é desenhado perto do quadro 6200
  Achados (05/11/2026, fix/bugs-games-4, `shadertest <app> --warnings` lista os avisos por programa): os 40 são as 10
  variantes (m/o/msaa/...) de 4 materiais, `HemiRimBump`, `HemiRimBumpB`, `HemiRimBumpHDR` e `toon` (iluminação de
  borda/toon dos personagens); nenhum outro shader tem aviso. Próximo: ver no `--dump` em que conta o G16 entra (se
  multiplica a borda, o efeito de rim light some) e comparar com um vídeo do Vita real
  Agente C dia 6 (05/11/2026): **menu do título sem regressão**: 59,8 fps, p50 16,68 ms, 6 de 299 intervalos acima de
  17,5 ms com o PC ocioso (`bugsC6_out/ztd_menu.log`, `bugsC3_data/tests/ztd_menu/sheet.png`); os 47-57 eram
  CPU dividida. G16: `shadertest --warnings` ainda dá 40x `load from unsupported bank Global register 16`; em todos os
  40 o G16 só aparece num `VTST` contra os 16 bits de baixo de um uniform (`p0 = G16 != sa[8].y & 0xFFFF`) e o
  predicado guarda uma cópia de registradores (`VMOV r1 = pa0`) antes da iluminação (`07a17b44508ff793.frag`): com
  G16 = 0 o jogo sempre segue um dos lados. Próximo: um dump de GPU numa cena com esses shaders e forçar outros valores
  para ver qual lado é o certo
  Agente C dia 8 (06/11/2026): o lado do predicado é **iluminação de dois lados**: `pa0 = -r1` (normal negada) e
  `if (!p0) r1 = pa0`, ou seja com G16 = 0 a normal é sempre invertida; o G16 parece ser "face da frente" (não zero na
  frente). Feita a chave de depuração `VITASHARP_G16=front|back` (padrão continua 0; GLSL idêntico sem ela;
  `shadertest` com `front`: nenhum aviso de G16, 105/105 compilam). Não validado: a cena de abertura gravada não usa esses programas (dump `bugsC8_out/ztd_scene.vsgd`, f2180-2182: replays com
  front/back/zero idênticos, 9 shaders, nenhum com G16). Falta uma cena com eles (provável: salas de fuga 3D) para
  comparar com o vídeo do Vita (`zwPJR5er054`, `bugsC8_yt/ztd.webm`, 10 primeiros minutos)
  Agente C dia 9: não feito (o dia foi para o Injustice, o áudio do Jak e o crash de load state). Cenário pronto
  `bugsC9_scn/ztd_scout.txt` (Cross a cada 60 quadros de f2250 a f4200, quadros a cada 50): no vídeo, depois
  do "Coincide" (f2180 aqui) vem a cena das celas com os personagens 3D, que deve usar os shaders toon/HemiRimBump
  **Corrigido** (agente C dia 10, 06/11/2026, fix/bugs-games-9; validado aqui, falta o usuário confirmar): o G16 agora
  é `gl_FrontFacing` por padrão (`VITASHARP_G16=zero|back` para depurar; `GeneratorVersion` 21, os caches se
  refazem). Dump da cena das celas (`bugsC9_out/ztd_cells.vsgd`, presents 3150-3152, com os avisos de G16)
  reproduzido com zero/front/back e comparado ao vídeo do Vita (1:23): zero e back são iguais, personagens claros e
  chapados (camisa do Sigma RGB 134/128/127, cabelo 203/186/172); front dá a sombra do Vita (camisa 100/95/95, cabelo
  165/141/128; Vita 110/104/99 e 155/137/122). No jogo com o padrão novo (`bugsC9_proof/ztd_g16front_sheet.png`
  x `ztd_g16zero_sheet.png`): sombreado como no vídeo e a máscara do Zero com a textura suja do Vita (antes branca)
  `replay-gpu --compare` nos 18 dumps: só os 2 do Tearaway mudam (ele também lê o G16: as bordas das folhas de papel
  de dois lados ficam escuras e somem uns pontos verdes na borda; média até 2,5/255, `bugsC9_proof/tearaway_g16_before_after_diff.png`);
  os outros 16 idênticos. Falta conferir o Tearaway com um vídeo do Vita
  Conferido (agente C dia 11, 06/11/2026, fix/bugs-games-10): com o G16 = face da frente em todo desenho, o Tearaway
  **regrediu**: na abertura (o sol de papel, dump `PCSF00476_20261030_112014`, quadro 3690) os raios ganham um contorno
  verde fino, e no vídeo do Vita (`etxceok4M44`, 3:06) as bordas rasgadas dos raios são marrom-escuras, como com G16 = 0.
  Os dois shaders do papel (`10FF2379F841F16E`, `E3209025AAF95916`) testam `G16 != 0` (literal) e com 0 pegam o segundo
  par de cores (TexCoord6/7); os desenhos do papel são cull=0 (sem culling), os do ZTD cull=1 (CW). Agora o G16 é a face
  da frente só em desenhos com culling e 0 sem culling (push constant `face_cull`; `GeneratorVersion` 22;
  `VITASHARP_G16=front-always` volta ao comportamento anterior). Tearaway = G16 0 de antes (61 quadros idênticos), ZTD
  igual ao 288f6ed (3 quadros idênticos, diferente do zero). Prova `bugsC10_proof/tearaway_g16_vita_vs_main_vs_new.png`.
  A regra do hardware é uma hipótese que bate com os dois jogos (o que é G16 com cull CCW não foi visto). Sobra: no
  close-up do quadro 3795 o G16 = 0 deixa uns pontos verdes na borda de uma folha (como antes do 11f18ce), sem vídeo do
  Vita do mesmo ângulo (a câmera do Tearaway segue o giroscópio)
  Confirmado pelo usuário (06/11/2026): ok
- [ ] Anexo da palavra extra do registrador de saída (TileWord1): falta nas cenas supersampled, no caminho tile-based (Android) e com VITASHARP_MSAA_FETCH=copy (lá a palavra continua perdida, com aviso no log; 29/10/2026: `TileWord1Unsupported` ainda recusa esses três casos)
  Conferido (10/11/2026, agente B): os três casos são caminhos de reserva no desktop (supersample só quando o MSAA
  nativo não serve o formato; cópia só com VITASHARP_MSAA_FETCH=copy) e o tile-based é do Android. Nenhum log ou
  gravação do usuário nem execução no `<scratchpad>` tem o aviso `... which the scene cannot keep (...)`; sem jogo
  que precise, não mexi
- [x] Fibers: `_sceFiberAttachContext*` exige status Init (o firmware aceita qualquer fiber ocioso sem área de contexto; 0/0 é switch simples) e o topo da pilha deveria ser `(addr+size-1)&~7` (conferido em 29/10/2026 no main c12927d: `Fiber.AttachContext` ainda devolve ERR_STATE fora de Init e R13 = `(addr+size)&~7`)
  Feito (10/11/2026, agente B, fix/bugs-b-2), pelo libfiber.suprx 3.74 desmontado: as checagens do
  _sceFiberAttachContextAndRun/AndSwitch seguem a ordem do firmware (NULL; fiber ou contexto desalinhado; Run já dentro
  de um fiber ou Switch fora de um = PERMISSION; tamanho < 512 = RANGE; endereço NULL, tamanho não múltiplo de 8 ou
  fiber que já tem área = INVALID; fiber rodando = STATE); qualquer fiber ocioso sem área recebe a área e começa de novo
  na entrada sobre a pilha nova; 0/0 é um run/switch simples; o topo da pilha é `(addr+size-1)&~7`. Achado no mesmo
  desmonte e corrigido: quando a função de entrada de um fiber retorna, o firmware volta para a thread logo depois do
  sceFiberRun, que devolve 0x80590006 com *argOnReturn = o fiber (que fica ocioso e recomeça na entrada); aqui a thread
  inteira terminava (LR = ThreadExitTrap). Testes em `FiberHleTests` (3). No jogo: Ninja Gaiden Sigma 2 Plus
  (`ngs2_boot` até o gameplay em Sky City Tokyo, 40273 sceFiberSwitch e 2604 sceFiberRun,
  `<scratchpad>\bB2\runs\ngs2\frames\sheet.png`) e Soul Sacrifice Delta (`soulsac_boot` até a fogueira da primeira
  área, 16610 sceFiberSwitch, `<scratchpad>\bB2\runs\soulsac_boot\frames\sheet_3.png`). Nenhum jogo rodado chama o AttachContext (só o libult importa; NGS2,
  Unit 13 e Soul Sacrifice Delta não criam ulthreads no caminho testado): essa parte só tem os testes
- [ ] MSAA real: leitura da imagem pelo próprio desenho (input attachment) é indefinida quando triângulos do mesmo desenho se sobrepõem (sem `VK_EXT_rasterization_order_attachment_access` na GTX 1650/Iris Xe); causa diferenças de ±1 entre execuções (29/10/2026: o log da GTX 1650 ainda mostra `GPU missing: ... RasterizationOrderColor`)
  Estudado (10/11/2026, agente B, fix/bugs-b-3): o Vita3K (vulkan/renderer.cpp, context.cpp) usa o
  rasterization-order quando existe e, sem ele, só no modo "alta precisão" o `VK_EXT_fragment_shader_interlock`
  (fragmentShaderSampleInterlock + fragmentStoresAndAtomics): a cor vira uma storage image (visão UNORM separada
  da sRGB, VK_KHR_maintenance2), lida e escrita pelo shader dentro da seção crítica, num render pass próprio; fora
  disso ele também fica indefinido. A correção exata
  aqui seria esse mesmo caminho (variante do gerador com begin/endInvocationInterlock e imageLoad/imageStore da cor,
  storage image multisample para o MSAA nativo), uma mudança grande no renderizador para diferenças de ±1; não feita
  nesta rodada. Primeiro passo se for fazer: conferir na GTX 1650 e na Iris Xe se `fragmentShaderSampleInterlock` e
  `shaderStorageImageMultisample` existem (logar no início, como o `GPU missing`)
- [ ] Sonic Mania: ~18 estalos por segundo na saída de música (44,1 kHz); pode ser do próprio conteúdo, não investigado (29/10/2026, main c12927d, `mania_boot.txt` com `--audio-dump`: 522 estalos em 34,5 s de som, 15,1/s, sem relação com a fase do grain de 1024; sem underruns)
  Não reproduz (05/11/2026, fix/bugs-audio, os mesmos `mania_boot` e `--audio-dump`): porta BGM 44,1 kHz grain 1024, 62 s (34 s com som), `audrep.py` 0 estalos e um detector de segunda diferença mais sensível 0,1/s; sem underruns. A conversão 44,1→48 kHz da saída é do OpenAL (fora do dump); falta o usuário ouvir no PC.
  Agente C dia 11 (06/11/2026, fix/bugs-games-10, `mania_boot` com `--audio-dump`, 62 s, 35,6 s com som): nas emendas
  dos grains de 1024 amostras não há salto além do conteúdo (9 de 1506 emendas com salto > 8x a mediana em volta,
  contra 2 no meio dos grains; nenhum grain repetido, 1 grain mudo depois de som); um detector de segunda diferença
  marca 10/s, espalhados e não alinhados aos grains: são ataques da própria música (síntese com bordas retas). Depois
  de passar o dump por Opus 128k (como o YouTube) caem para 2,5/s; o vídeo do Vita `w5qGSkE4IqM` dá 0,8/s, mas em
  outro trecho com outra música, então não compara. Conclusão: não é do emulador na saída do jogo; fica a conversão
  44,1→48 kHz do OpenAL, fora do dump, para o usuário ouvir
  Conferido de novo (05/11/2026, fix/bugs-audio-2, no mesmo dump): 0 quedas de >30 dB em 2,5 ms (`drops.py`) e nenhuma
  emenda na fase de 256/512/1024 amostras (`phase.py`, pico 1,16x da mediana, o mesmo do FIFA sem defeito); os 8
  eventos de segunda diferença são transientes da música entre 32 e 38 s. Sem defeito medível no que o jogo gera
- [x] ATRAC9: a saída do nosso decodificador tem a polaridade invertida em relação ao FFmpeg (inaudível; confirmar qual está certa; o decodificador não mudou desde então, e só dá para decidir comparando com o Vita real)
  Conferido (10/11/2026, agente B): o nosso é o LibAtrac9/VGAudio (Alex Barney, engenharia reversa do decodificador da
  Sony); o do FFmpeg (2018) foi escrito sem comparar com o binário da Sony ("confiança de que está perto do binário",
  na lista do FFmpeg) e usa o MDCT genérico do FFmpeg com escala positiva (outros codecs do FFmpeg, como o Vorbis,
  usam escala negativa para acertar o sinal). O Vita3K usa o FFmpeg. Indício a favor do nosso, sem prova: decidir só
  com uma gravação digital do Vita/PSTV de um som isolado (logo) correlacionada com o nosso decodificado
  **Decidido (10/11/2026, agente B): o nosso está certo, o FFmpeg tem a polaridade invertida.** Áudio de dois vídeos
  de PS TV do YouTube (captura HDMI; só o áudio, baixado com yt-dlp para o scratchpad e apagado depois), em mono a
  8 kHz, correlacionado por FFT com as faixas .at9 dos próprios jogos decodificadas pelo FFmpeg: Unit 13
  (`TudV1nnrZLk`, "Unit 13: HD Gameplay (PS TV)") bate com 6 sons diferentes, todos com correlação negativa
  (post_mission_complete -0,96, combat_set1_fl_11 -0,92, menu_splash -0,92, combat_set1_fl_12 -0,91, fl_05 -0,91,
  hud_logo -0,86); Zero Time Dilemma (`9CpD-ZeIR5E`, "Part 42 PS Vita / PS TV") bate com o bgm049 a -0,80. O nosso
  decodificador no mesmo bgm049 dá exatamente o FFmpeg com o sinal trocado (correlação -1,000000 no atraso 0, 106,2 dB
  de SNR com a inversão), ou seja, o mesmo sinal do Vita de verdade. Nada a mudar
- [x] `sceNpManagerGetCachedParam`: layout não confirmado, por isso responde "sem conta" mesmo com a conta PSN emulada conectada (o UnityNpToolkit do 99Vidas chama e segue em frente; 29/10/2026: `Np.cs` ainda devolve ERR_SIGNED_OUT)
  Feito (06/11/2026, fix/bugs-games-11): desmontado do np_manager.suprx 3.74, copia do registro de 0x148 bytes do
  NID_E45E4003 o SceNpId (+0, 0x24) e a URL do avatar (+0x38, 0x80) para um SceNpManagerCacheParam de 0xA4 bytes e
  devolve 0; o HLE dá o NpId da conta emulada (sem URL) ou tudo zero sem conta. O 99Vidas chama 4 vezes no boot e
  recebe "VitaSharp" (`--peek`, log `bugsC11_out/np_99vidas_peek/log.txt`) e segue para a abertura
- [ ] Revalidação dos jogos do PC (31/10/2026, branch perf/android-phone-2; folhas e resumo em
  `phone2_shots/pc`, `review.md`): The Deer God (PCSB01070) sem limite de quadros (214-348 fps; antes 60);
  Shinobido 2 (PCSE00015) com uma linha verde embaixo do filme de abertura e `sceKernelGetCpuId` sem implementação
  (31.410 chamadas) — corrigidos em 04/11/2026 (fix/user-bugs): o filme é 960x540 e o jogo amostra o buffer de 544
  linhas; o decodificador do Vita escreve macroblocos inteiros e agora as linhas até o múltiplo de 16 repetem a última
  (antes ficavam YUV 0,0,0 = verde), e `sceKernelGetCpuId` devolve o núcleo da afinidade (ou um fixo 0–2 por thread);
  Call of Duty (PCSB00232) com o título em russo; Zero Time Dilemma com o menu do título a 47-57 fps
  (era 60); lentidões a confirmar com o PC ocioso (as rodadas dividiram a CPU com builds do APK): Soul Sacrifice no
  fogo/fumaça, Silent Hill na seleção de zona (15-22 fps), Skullgirls na abertura da luta, Crazy Market nos termos de
  uso (16 fps), Tearaway num menu parado (com o PC ocioso: confirmados Silent Hill e Crazy Market; Soul Sacrifice e
  Skullgirls eram carregamentos ou CPU dividida); NIDs sem implementação vistos uma vez: `SceNpManager` FFBFC540 (FIFA 13),
  `SceMp4` D5B26179 (Ninja Gaiden Σ2), `sceNpSnsFacebookInit` e `sceCoredumpRegisterCoredumpHandler` (Tearaway). Os
  cenários de jogo novo precisam de uma raiz de dados sem saves (com save eles seguem pelo Continuar). O título em
  russo do Call of Duty não é defeito (04/11/2026): o PCSB00232 instalado é a edição `DECLASSIFIEDEU02` (russo e
  polonês; o `TITLE` padrão do param.sfo é "(рус)" e só há "(Polski)" como alternativa); o jogo lê o idioma 1 (inglês)
  pelo `sceAppUtilSystemParamGetInt` e, sem inglês nessa edição, fica no russo, como faria no Vita
  Revalidado (10/11/2026, agente B, fix/bugs-b-3 sobre a main 2cfe1ec, headless, cenários de `tests/scenarios` com uma
  raiz de dados sem saves; folhas em `<scratchpad>\bB2\runs\rv_*\frames`; fps das janelas de 5 s do log):
  - Zero Time Dilemma: menu do título 57-60 fps (era 47-57) e 60 até a abertura, 30 nas cenas (a taxa do jogo): ok.
  - Call of Duty: logos e título a 30 fps estáveis; o russo continua sendo da edição (não é defeito): ok.
  - Shinobido 2: boot e filme 15-20 fps até ~f960, depois 28-30 fps até f3954; 0 não implementadas: ok depois do filme
    (os 15 fps do começo são o filme de abertura, a conferir com o PC ocioso).
  - Tearaway: 30 fps no menu de perfil, carregamento e intro (o menu parado lento não aparece mais): ok.
  - Skullgirls: na primeira rodada a abertura da luta caiu para 5-16 fps; repetida com a CPU entre 3 e 22%, a luta
    ficou em 43-60 fps (`rv2_skullgirls`): era a CPU dividida, não o jogo.
  - The Deer God: continua sem limite de quadros (142-147 fps nas janelas do log, 4045 quadros em ~19 s). O jogo vira
    os buffers com SCE_DISPLAY_SETBUF_IMMEDIATE e não espera vblank; o `VITASHARP_DISPLAY_IMMEDIATE_PACE=1` (um flip
    por vblank) continua sendo só um experimento. Fica aberto (jogo do agente A).
  - Silent Hill: Book of Memories (`shbom_boot`, não chega à seleção de zona): criação do personagem a ~16 fps
    (f1328-2064) e ~1 fps enquanto o teclado na tela abre (f1700-1740); quarto inicial a 60. Fica aberto (jogo do
    agente A).
    Agente B (10/11/2026, fix/bugs-b-4, `<scratchpad>\bB4\runs\sh1`..`sh4`): o ~1 fps com o teclado não se repete sem
    a gravação contínua ligada (coincidia com o pre-roll do "Gravar tudo" da raiz de dados daquela rodada, que gravava o
    estado em até 5,5 s); sem ela a tela do nome fica nos mesmos ~15 fps do resto da criação. Os 16 fps da criação: a thread
    `SwiftUpdate` (a interface) faz ~614 pares sceKernelLockMutex/UnlockMutex por quadro, e cada chamada é cobrada
    como uma passagem pelo escalonador (12.000 ciclos, `HleFunction.Cost`): 14,7 M ciclos/quadro = 44 ms no relógio de
    333 MHz, então a thread fica 80% do tempo no freio ("throttle") e o quadro sai a cada 60 ms. Cobrar o lock/unlock sem
    disputa como uma entrada no kernel (2.000) tiraria o freio, mas nas duas rodadas com essa mudança (`sh3`, `sh4`) o
    jogo parou no carregamento (~f880, BKPT do próprio jogo depois de `sceGxmDraw` fora de cena 0x805B0006 da
    `jobQueueWorker1` e da `main`, que não aparecem com o custo antigo): o jogo trava um mutex não recursivo de novo
    (0x80028142, 1097 vezes, igual nos dois custos) e o desbloqueio interno solta o externo, então o ritmo mais rápido
    deixa duas threads desenharem no mesmo contexto. Mudança revertida; falta ver se o Vita de verdade aceita esse lock
    (atributo do mutex 0x40010075 na criação) antes de baratear o custo
    Agente B (10/11/2026, fix/bugs-b-5, `<scratchpad>\bB5\runs\m1`, `m2`, `s1`..`s6`; só achados, nada mudou no
    código): (1) o mutex 0x40010075 é o lock do heap do jogo (o malloc em 0x81450342), criado com atributo 0 (não
    recursivo); a função em ~0x8144FF00 trava o lock e chama o malloc, que trava de novo (backtrace `814500C6 8144F2D4`).
    (2) No firmware a semântica é a nossa: o SceLibKernel 3.74 passa direto para o syscall do threadmgr
    (NID 0x1D8D7945 -> SceThreadmgr_7FA945AD) e o threadmgr devolve 0x80028142 sem mudar a contagem quando o dono
    trava de novo um mutex sem o bit 0x2 (attr+0x20), e só soma a contagem com o bit; o desbloqueio de dentro solta o
    de fora no Vita também. Então os 1097 erros são do próprio jogo e não são a causa do BKPT. (3) A causa do BKPT com
    o custo 2.000 é uma corrida na troca de quem desenha no início do carregamento: a thread sem nome (0x400101E3, o
    laço de 30 fps da tela: Begin/EndScene, sceKernelDelayThreadCB(33.333 us), sem nenhum lock) continua desenhando
    depois que a `main` faz a cena dela (lr 0x8127421F) e a `jobQueueWorker1` assume; com o custo antigo a thread sem
    nome para na f625 e a `main` faz a cena 87 ms depois (sem corrida, `s4`); com 2.000 a `main` chega na cena dela na
    f342/f378 com a thread sem nome ainda no laço (`s3`, `s5`), os dois contextos se cruzam (0x805B0006) e o jogo aborta
    (assert do SceLibc, `s1`). Falta achar como a `main` manda a thread sem nome parar (não é mutex nem semáforo:
    provavelmente uma variável lida no topo do laço) e por que no Vita ela para antes; até lá o custo de 12.000 fica (achado da rodada 5)
    Agente B (10/11/2026, fix/bugs-b-6, `<scratchpad>\bB6\runs\a2000`, `b_io7`, `c_full`): **corrigido**. Não existe
    sinal da `main` para a thread sem nome: ela é a sequência de logos (0x81196FAB: aviso de salvamento, Konami,
    WayForward, CRI/Wwise, ~18 s de fades em tempo fixo), que no fim abre a tela de carregamento (0x81196551, o livro no
    canto). O jogo serializa quem desenha com uma trava gráfica contada (0x81224BC8, sobre um mutex) que só fica ligada
    enquanto os logos ou a tela de carregamento rodam; a `main`, ao terminar de carregar, chama o "fecha a tela de
    carregamento" (0x8119714E), que só espera a tela de carregamento e desliga a trava. Se a `main` chega antes do fim
    dos logos, a trava some com os logos ainda desenhando e os jobs de render (`jobQueueWorker1`) desenham no mesmo
    contexto: 0x805B0006 e o assert. No Vita isso nunca acontece porque o carregamento é mais lento que os logos: em
    vídeo de um Vita (YouTube BXrVtH4HP2w, ~2:07) os logos duram ~18 s e a tela de carregamento depois deles ~9 s.
    Aqui, com o mutex a 2.000 a `main` acabava em 14,8 s (antes dos logos acabarem); a `main` lê 139 MB no boot e o
    modelo de leitura estava a 25 MB/s (5,5 s). O cartão de memória do Vita lê a 6,3-7,6 MB/s (medição da Digital
    Foundry dos cartões oficiais, 2012): com `IoTiming` a 7 MB/s e o lock/unlock de mutex cobrado como entrada no kernel
    (2.000; o `sceKernelLockMutex` do firmware é um syscall direto ao threadmgr, o escalonador só entra quando a thread
    bloqueia), os logos duram 18,8 s e a tela de carregamento depois deles 10 s, como no console; 0 erros 0x805B0006,
    sem BKPT, e o cenário inteiro chega ao quarto (f2950). A criação do personagem foi de ~15 para 57-59 fps (no Vita
    ~30: vídeo ruh4O2ZO7aA em 60 fps troca de imagem a cada 2 quadros; o jogo apresenta a 60). Efeito colateral
    esperado: todo carregamento fica mais lento (como no Vita); os cenários com entrada por número de quadro podem
    precisar de mais quadros. Outros jogos (build com as duas mudanças contra a main, mesma raiz de dados, `kz_idle`, `uc_menu`,
    `f1_menu`): Killzone 29-30 fps no gameplay nos dois (o gameplay chega igual com 7 MB/s), Uncharted 59-60 nos menus,
    F1 25 nos filmes e 59-60 nos menus; as quedas isoladas de uma rodada de cada lado coincidiram com outro emulador
    ou a suíte de testes rodando junto e não se repetiram
  - Crazy Market (PCSE00291) e Soul Sacrifice Delta: o Crazy Market não está mais instalado; o Soul Sacrifice chegou ao
    fogo e à fumaça da primeira área na rodada do agente B de hoje (`<scratchpad>\bB2\runs\soulsac_boot`).
  - NIDs que faltavam (FFBFC540, D5B26179, sceNpSnsFacebookInit, sceCoredumpRegisterCoredumpHandler): todos implementados;
    0 "Unimplemented" nas sete execuções
- [x] Suíte `Category!=Slow`: 3 falhas do NativeJitTests só na suíte completa (a classe sozinha passava 37/37; agente B,
  10/11/2026, fix/bugs-b-6). Causa: o construtor do `Emulator` escreve o `Jit.Enabled`/`Jit.Mode` do processo e os testes
  de HLE criam emuladores com `EnableJit = false` sem devolver; os testes que rodavam depois (Invalidation, TierUp,
  Dispatcher) nunca compilavam um bloco. Agora o emulador devolve o estado no `Dispose` e os testes que precisam de
  blocos compilados ligam o JIT na duração deles (`JitTestState.Enable`). Reproduzido na main (`<scratchpad>\bB6\runs\t1`:
  1558/1562, 3 falhas) e verde depois em 3 suítes completas seguidas
- [ ] Filmes no celular ainda mais lentos que no PC (31/10/2026, perf/android-phone-2): o primeiro filme do F1 2011 leva
  5,6 s contra 4,8 s no PC (45 grãos de silêncio no som do filme; Uncharted 40 por volta do filme do título), já com o
  decodificador H.264 do próprio Android (o da MediaTek segurava quadros: 6,25 s, 75 lacunas). Os decodificadores não
  são o gargalo (AAC 5 ms, H.264 12,8 ms por chamada, MediaExtractor 0,4 ms); falta ver as threads do player do jogo
  (simpleperf). Comparar também os dumps de áudio de F1 (menu e corrida) e God of War com o PC (`vitasharp-perf
  audio-check`): 245 e 157 estalos no celular
  12/11/2026 (A1, Redmi Note 14, abertura do F1): 1º filme 4,9-5,1 s e 2º 7,8-8,3 s nos dois códigos (novo e antigo),
  contra 5,0 s e 8,0 s do conteúdo: já em tempo real. A paridade de áudio (`build/android-media-parity.sh`) mostrou
  que o AAC do MediaCodec sai um quadro (1024 amostras) depois do FFmpeg, com os 2 primeiros em silêncio, e o MP3
  623 amostras deslocado (o decodificador do Android descarta 529 amostras de atraso): diferença de contrato a
  corrigir, não de velocidade
- **Silent Hill 4 port (SILH00004), "a visualização tá zuada" (08/11/2026, capture SILH00004_20261108_211441, not
  solved):** checked so far: the depth-clamp change (580f464) does not touch it (replay with and without it:
  pixel-identical); the post pass (scene 640x362 -> 0x60F8CC00 -> back) does not blur or darken it (the scene target
  before it looks the same); the BC1/BC2 swizzled-arbitrary atlases decode to plausible (dark, rusty) art; the
  stencil-masked effect target 0x613EF000 stays empty because no draw in that frame writes a non-zero stencil (so
  the composite is a no-op, which may be right). Real Vita footage (YouTube i1CuZULiQ6w) shows a much brighter,
  grey-green apartment, but not the same spot. Next: a Vita3K picture at the same save (its Vulkan window comes out
  white with PrintWindow: capture it another way), and ask the user what exactly looks wrong. The title menu's
  Continue stays grey with the user's sh4.sav copied (the save is read whole, 694288 bytes); Load is enabled.
