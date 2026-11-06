# Roadmap

Lista de itens futuros do VitaSharp. Marque `[x]` quando um item for concluído.

## Diagnóstico

- [ ] Teclas para alternar opções gráficas com o jogo rodando (MSAA real / antigo / desligado e outras)

## Save states

- [x] Salvar e carregar o estado inteiro do emulador: CPU (registradores, NEON/VFP, monitor exclusivo), memória,
  objetos do kernel, estado dos módulos HLE (por módulo, `ISnapshotState` gerado na compilação com as partes à mão,
  decisão do usuário de 01/11/2026) e a GPU (o renderer é recriado e reconstrói os caches da memória do jogo). Feito
  (8efb2d5..3b45916, desenho em `docs/notes/save-states.md`, inventário em `docs/notes/dev-snapshots.md`). Validado
  no desktop em 04/11/2026 com 16 jogos (tabela "Tested" em `save-states.md`) e no celular (Knightin'+, Uncharted,
  F1: `docs/notes/android-frame-anatomy.md`, main 1c0e516). 06/11/2026 (feat/savestate-tolerant): Tearaway salva e
  carrega (antes a carga era recusada com a cena da GPU aberta e o salvamento com uma command list sendo gravada),
  carregado duas vezes seguidas no modo de depuração, 30 fps e o boneco anda com o direcional depois das cargas, sem
  device lost (`scratchpad/ss1_tear5`); LoadExec depois de uma carga: estado no menu da Jak and Daxter Collection,
  carregado, Cross no Jak 1 e o Jak 1 chega ao PRESS START (`scratchpad/ss1_jak1`); God of War Collection: estado no
  menu, carregado, God of War 1 até o menu do jogo a 30 fps (`scratchpad/ss1_gow1`)
- [x] Estado salvo depois de um LoadExec carrega numa sessão nova (06/11/2026, fix/savestate-loadexec): o programa do
  estado é o primeiro módulo dele (`SaveStates.ProgramOf`); `--load-state` (launcher, `rerun` do Gravar tudo) boota esse
  self direto; F7/slot com outro self rodando faz o LoadExec antes e carrega após 120 quadros; os módulos são achados
  por arquivo e endereço e ficam com os UIDs salvos. Junto: `sceGxmDisplayQueueAddEntry` com a fila cheia não espera
  para sempre quando a thread é parada (o LoadExec pedido pelo host esperava 10 s e seguia com a thread antiga viva).
  Validado: Jak 1 e God of War 1 em gameplay salvos, carregados em sessão nova pelos dois caminhos, 20 e 30 fps como
  ao salvar (`scratchpad/ss2_jakB`, `ss2_jakC4`, `ss2_gowB`, `ss2_gowC6`); Uncharted (o eboot.bin dele também faz
  LoadExec) a 59 fps (`ss2_ucB`); o rerun da gravação do usuário `PCSA00080_20261106_002008_tudo` (build da época
  0d298b4 + estas mudanças, o esquema da main mudou depois) carrega e 98% dos quadros saem iguais
  (`ss2_cap/.../reruns`). Os argumentos do LoadExec não são repassados (a memória salva os substitui); o Android ainda
  não faz o passo do LoadExec ao carregar um estado de outro self
- [x] Estados de outra versão: decisão do usuário (05/11/2026): estados de outra versão são recusados com aviso claro
  (não há formato tolerante; `SnapshotSchema.ManualVersion` + o hash dos layouts gerados decidem). Aviso em português
  ("Este estado foi salvo por outra versão do VitaSharp (1.0.0+57c57b1) e não pode ser carregado"), 15 s na barra de
  título, dica do slot no launcher e aviso no Android (agente C dia 6, `scratchpad/bugsC6_out/ss_old/log.txt`).
  Visto na janela (06/11/2026): o estado do Jak 1 da 57c57b1 com `--load-state` mostra o aviso na barra de título de
  8,2 s a 24,1 s e o jogo segue a 30 fps (`scratchpad/ss1_oldstate/titles.txt`, `window_0.png`)
- [x] Interface: F5/F7 (rápido), F6 escolhe o slot 1-9, Shift+F5/F7 no slot; o launcher lista os estados do jogo com
  miniatura e abre o jogo carregando um; Android: "Salvar estado"/"Carregar estado" no menu de pausa, 6 slots com
  miniatura e data (24a3c6f; carga e salvamento por passos no celular em 9c288d7)
- [x] Uso em diagnóstico: o Gravar tudo (F8) leva o estado do início (`state_start.vss`) e o da pré-gravação
  (`state_preroll.vss`), e `rerun` recomeça dele com a entrada gravada (FIFA 13: 95% dos quadros iguais, mediana da
  diferença 0; `docs/notes/session-capture.md`)
- [ ] Sem teste por falta de um jogo instalado que use: Atrac, Sas, Audioenc/Videoenc/Mp4Recorder, G729, Location,
  NetAdhocMatching/Rudp, TriggerUtil e o `sceIme` (teclado do jogo sem o diálogo do sistema); esses módulos recusam o
  salvamento só enquanto estão em uso. Sob o shell do Vita (vários processos) o salvamento é recusado
- [ ] Não validado (06/11/2026): o device lost do Tearaway na janela (o relato de 04/11 não reproduziu em 3 cargas
  sem janela; não houve teste com janela); o teclado do sistema (ImeDialog) aberto num save: o Sakatsuku
  (`st_game2`, 7000 quadros) não chegou ao teclado, e o Invizimals não foi testado
- [x] Jak and Daxter Collection, menu (antes de qualquer save): a GPU parou ~20 s num `FinishCommand` no quadro 1076
  (watchdog STALL, quadro 1 da GPU não retirado) e depois seguiu; com outros agentes usando a GPU ao mesmo tempo, causa
  não investigada (`scratchpad/ss1_jak1/stdout.txt`)
  Concluído por decisão do usuário (06/11/2026): Jak and Daxter fechado, exceto o save state no gameplay (item abaixo)

## Desempenho (PC)

- [ ] Taxa de quadros por jogo (pedido do usuário, 06/11/2026): uma opção **por jogo** (não global) com **Nativo**
  (padrão: o jogo roda exatamente como hoje, nada muda), **Fixo em 30**, **Fixo em 60** e **Sem limite**. Exemplo: o
  Jak 1 roda a 20 fps no Vita (o jogo pede 3 vblanks por troca); com a opção, travar em 30, rodar a 60 ou liberar.
  Onde atua: no intervalo de vblank que o jogo pede (`sceDisplayWaitSetFrameBufMulti`/`WaitVblankStartMulti`,
  `sceDisplaySetFrameBuf` IMMEDIATE, a fila de display do GXM e o ritmo do vblank emulado), nunca no código do jogo.
  Cuidados: jogos cuja lógica anda por quadro ficam mais rápidos ou mais lentos (avisar na descrição da opção); o
  modo Sem limite também solta o ritmo do vblank; o áudio continua no tempo real. Guardar no perfil do jogo (como o
  modo do mouse), no launcher do desktop (página/ajustes do jogo) e no app Android (ajustes do jogo e menu de pausa),
  com o modo atual no overlay de FPS

- [ ] Reflexão o mínimo possível (desktop e Android; ajuda o Native AOT, o Mono AOT e o tempo de início). Hoje
  (31/10/2026): `GetMethod(s)`/`GetProperty(s)` em 3 arquivos, `GetField(s)` em 1, `GetCustomAttribute` em 2,
  `Enum.GetValues/GetNames/Parse` em 5, `Assembly.GetTypes/GetExecutingAssembly` em 1 e `Reflection.Emit` em 3 (o
  backend IL do JIT, que precisa dele). Análises e trocas:
  - [ ] Auditar cada uso e trocar por geradores de código (source generators) ou tabelas fixas: atributos lidos em
    tempo de execução viram tabelas geradas na compilação; enums com `switch` gerado em vez de `Enum.ToString/Parse`
  - [ ] Ligar os analisadores de trimming/AOT (`IsAotCompatible`, `EnableTrimAnalyzer`, `EnableAotAnalyzer`) em todos
    os projetos com aviso tratado como erro, para nenhum uso novo entrar sem ser visto (IL2026/IL3050)
  - [ ] Conferir o JSON: todos os `JsonSerializer` com contexto gerado (sem o serializador por reflexão como reserva;
    `JsonSerializerIsReflectionEnabledByDefault=false`)
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
- [ ] Prioridade das threads em CPU híbrida (núcleos E) e na bateria: medir `SetThreadSelectedCpuSets` e o
  power throttling num Intel de 12ª geração ou mais novo (as threads da CPU emulada e da GPU já sobem para acima do
  normal, `--host-prio`; ver `docs/notes/thread-priority.md`)
- [ ] Queda para 50-54 fps no menu do Sonic Mania

## Android

- [ ] Uncharted (PCSA00029) no Android: plantas piscando no começo do Prologue (Drake parado; APK LLVM perf/uc-cpu, dump
  `scratchpad/and_flicker/PCSA00029_plants075.vsgd`, presents 4000-4150, escala 0,75; pisca em 0,75 e 1,0, nenhuma
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
  (`scratchpad/bugsC6_out/mali_drake_4037.png`: main mali / novo mali / desktop); desktop 151/151 quadros idênticos à
  main. (2) DeviceLost: 0 em 5 replays com `--gpu-profile=desktop` (2 builds, 3 com cache de shaders vazio,
  `bugsC6_out/plants_ref_desk`, `plants_new_desk`, `plants_dl3..5`); o da rodada anterior foi no 1º present, depois
  de 1335 ms de compilação de shaders no mesmo quadro, com a CPU dividida (provável TDR do driver); não reproduz
  Conferido (06/11/2026, perf/android-7): o replay com `--gpu-profile=mali` no build de baabc78 (fix/bugs-games-6) já
  mostra o Drake inteiro; o da main ainda o quebra (`scratchpad/and7_runs/drake_mali_cmp.png`, esquerda main, direita
  baabc78). A bisseção do pisca no celular não foi feita (testes no celular parados pelo usuário); preparado:
  `scratchpad/and7_tools/flick.sh` (screenrecord do aparelho, quadros distintos, `tileflick.py` por variante do env.txt)
  e o vídeo do Vita `scratchpad/and7_yt/uc_vita.mp4` (o Prologue começa por volta de 70 s)
- [ ] Menu de pausa do Android (botão PS) no estilo do app (pedido do usuário, 05/11/2026): hoje é um `AlertDialog`
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
  ganha o contorno azul; um toque fora do cartão continua. APK dev em `scratchpad/bugsC5_apk` (instalado no celular
  às 20:55 sem a correção dos acentos). O Redmi recusa `adb shell input` (INJECT_EVENTS): para dirigir o menu e as
  telas sem o usuário há agora `VITASHARP_ANDROID_UI_INPUT` (build dev, `Developer/UiScript.cs`, commit 96ac1d5).
  A validação no celular foi interrompida a pedido do usuário antes dos prints do menu
  Validado no celular (06/11/2026, perf/android-7, APK LLVM dev, por toque com `VITASHARP_ANDROID_UI_INPUT`):
  página principal, Salvar estado (salvou no slot 2 com miniatura), Carregar estado (carregou o slot 2 em 3 s),
  Ajustes, Desenvolvedor, Recentralizar (aviso "Movimento recentralizado"), Gravar tudo / Parar gravação, Sair do
  jogo (`scratchpad/and7_tools/shots/pm_main.png`, `pm_a.jpg`, `st_a.jpg`, `st_b.jpg`, `rec_a.jpg`). Achado e corrigido (79925c5, ainda
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
- [ ] Acentos quebrados no Android (relato do usuário, 05/11/2026): **causa achada e corrigida** (commit 239d126, falta
  o print do depois no celular): `Pages/LibraryView.cs` estava salvo com UTF-8 lido como Windows-1252 e gravado de
  novo ("JogÃ¡veis", "Todos Â· 8", "licenÃ§a", e os ícones ⚙ ⌕ ▶ › 🏆 como "âš™", "âŒ•", "â–¶", "â€º", "ðŸ†"),
  exatamente o que o celular mostra (`scratchpad/bugsC5_phone/before_library.png`). As fontes Inter e Sora têm os
  glifos (á à â ã ç é ê í ó ô õ ú, conferido no cmap). Varredura do repositório inteiro: só esse arquivo e três
  comentários do Core; `SourceEncodingTests` falha se a sequência voltar (src, tests, build). As outras telas,
  diálogos, o overlay de FPS/REC e o texto dos jogos (PVF) ainda não foram vistos no celular
  Validado no celular (06/11/2026, perf/android-7): Biblioteca ("Jogáveis", "Todos · 8", "Jogável"), Ajustes
  ("licença", "Padrão", "Automático"), diálogo da CPU, Desempenho, Controles, página do jogo, menu de pausa e
  overlay ("Pausado", "● REC") com os acentos certos (`scratchpad/and7_tools/shots/lib3.png`, `app_ajustes.png`, `app3.jpg`, `app3b.jpg`,
  `pm_main.png`). Texto dos jogos (PVF) não conferido
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
- [ ] F1 2011 no celular: quadros pretos nos filmes, porque o player do jogo pula o desenho do quadro quando o
  decodificador atrasa (CPU lenta: ~20 fps; reproduz no PC com `--cpu-mhz=120`); some com a velocidade nativa (item
  abaixo), conferir depois
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

- [ ] Mouse como analógico direito (desktop): o movimento do mouse vira a alavanca direita do Vita, para mirar e
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
  para quando ele acaba (`scratchpad/bugsC5_out/uc_mouse_strip.png`, 6950/6990 parados, 7010-7090 girando); testes
  `MouseStickTests`. Falta: a captura do cursor e o F4 na janela (pedir ao lead/usuário)

## Firmware e sistema

- [ ] Licença em zRIF na instalação de `.pkg`: hoje `PkgInstaller` só aceita o arquivo `.rif`/`work.bin` (lê a
  klicensee em 0x50). Aceitar também a licença no formato zRIF (o texto base64 com o `.rif` comprimido em zlib, com
  o dicionário fixo, que o NoNpDrm exporta das licenças do próprio Vita do usuário): decodificar, descomprimir, validar
  o content ID contra o `.pkg` e gravar o `.rif` em `ux0:license/<TITLEID>` e o `work.bin`; no CLI (`install <pkg>
  <rif|zRIF>`), no launcher (campo de texto na instalação) e no app Android. Core, CLI e app Android feitos
  (04/11/2026, `Package/Zrif.cs`, fix/user-bugs); falta o campo de zRIF no launcher do desktop. Campo feito (567e200:
  diálogo com o texto do zRIF ou o botão do arquivo). Validado 05/11/2026 só pela CLI com o `.pkg` real do usuário (WRC 4,
  Downloads) e um zRIF gerado do `.rif` dele (zlib com o dicionário, janela 1 KB: começa com `KO5ifR1dQ+`): instalou os
  1,5 GB numa pasta de teste e gravou um `.rif` idêntico ao do usuário (`fw_out/pkg_install/install_log.txt`). O diálogo
  do launcher ainda não foi clicado (precisa da tela)
- [ ] Sistema do Vita de verdade (ver docs/notes/system-apps.md): o shell já mostra os jogos, roda vários processos e faz a
  configuração inicial; falta o app de Troféus/Configurações desenharem (composição da tela compartilhada), o botão PS
  no PC, o texto cortado no boot direto, o travamento do teclado na tela (IME) e as funções restantes (sensor de
  movimento, sockets do sistema)
  Achados (05/11/2026, fix/user-bugs-6): a configuração inicial completa nos três caminhos ("Não tenho conta",
  "Tenho conta" + "Definir depois" = configuração de uso experimental da PSN, "Usar" = tela de Sign-In; prints em
  `fw_out/setup_*` do scratchpad); o "Definir depois" travava em "Aguarde" (pedido de sistema do SceNpManager, 4fbab47)
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
  sem ganho (revertido; patch `fw5_all_wip.patch` no scratchpad): (a) segurar o BeginScene de cada processo até o
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
- [ ] Validar a fundo TODOS os apps do sistema (04/11/2026, pedido do usuário): cada menu, aba, categoria, sub-página,
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
  Rodada fix/system-apps-6 (05/11/2026, provas em `fw6_out/` do scratchpad): **tela de bloqueio** corrigida (0e92acf:
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
  Rodada fix/system-apps-7 (06/11/2026, provas em `fw7_out/` do scratchpad): **Navegador** corrigido (f1d8488): ele
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
  tem a barra, e de Mensagens são iguais: não é isso).
- [ ] App de Câmera (NPXS10007) trava logo depois de abrir (30/10/2026, main 4dbe06c, log `20261030_204528_shell`):
  a câmera traseira abre (`Integrated Camera` 640x480 YUYV), o primeiro quadro é lido e a textura YUV420P3 aparece,
  e então o app para; não há mais leituras da câmera, a thread `ScePhotoCamMain` está Dormant, uma
  `SceCameraCoreCameraDeviceThread` espera em `condvar SceCameraCoreCameraDeviceLoopStatusCond` e a outra está Dormant.
  Funções que faltam no caminho: `ScePhotoExport::NID_16B1028E`, `SceLibLocation::NID_EFC9BD25` / `NID_2311B24A`,
  `sceAppMgrMmsMount`, `_vshAppMgrRegisterPath`, `sceKernelCheckModelCapability`, e um `IO: unmapped path ''` logo
  antes de criar as threads da câmera. (04/11/2026: não deu para reproduzir — a instalação atual do firmware 3.74
  em `%APPDATA%\VitaSharp\fs` não tem `vs0:app/NPXS10007`; precisa reinstalar/conferir o firmware e rodar com
  `--camera=<webcam>`)

## Código

- [ ] Release automática a cada build: hoje o `release.yml` só publica com tag `v*` ou pelo botão da aba Actions.
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
- [ ] Renomear `VitaSharp.Android.Common` (a lógica do app de celular que não depende do Android e é testada no PC:
  mapeamento de entrada, sensores, câmera, núcleos rápidos, ritmo de quadros, preferências) para
  `VitaSharp.Mobile.Core` (escolhido pelo usuário; o app continua `VitaSharp.Android`, um futuro iOS usaria o mesmo
  projeto); fazer quando não houver branches abertas (namespaces,
  `VitaSharp.sln`, `VitaSharp.Android.sln`, testes, docs)
- [ ] Refatorar o projeto para o guia de estilo C# do Google (https://google.github.io/styleguide/csharpguide.html), exceto as
  chaves (ficam na linha de baixo, padrão do C#) e a indentação (mantém 4 espaços e ~120 colunas): nomes, ordem dos
  membros, usings, comentários, `var`; regras num `.editorconfig` com analisadores. Cuidados: o GLSL gerado vem de
  strings literais do C# (o conteúdo delas tem que ficar idêntico, conferir com o shadertest) e fazer num momento sem
  outros branches abertos (o diff toca quase todos os arquivos)

## Bugs em aberto

- [ ] Jak and Daxter Collection (PCSA00080): validar o save state no gameplay do Jak 1 (salvar, fechar, abrir e
  carregar com F7/lista do launcher; estados salvos depois do LoadExec) quando o usuário puder jogar com a câmera
  corrigida (f3259f6, DelayThreadCB). Pedido do usuário (06/11/2026): é o único item do Jak que fica aberto
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
  tutorial (Batman x Superman, andar e golpes respondem) a 55-60 fps (`scratchpad/bugsC3_data/tests/inj_menu/sheet.png`,
  `inj_fight/sheet_2.png`, logs `bugsC9_out/inj_m2`, `inj_f1`; 0 HLE-ERR)
  Confirmado no jogo pelo usuário (06/11/2026, main d6c1fb7): roda
- [ ] Implementar todas as funções HLE que aparecem como não implementadas nos logs do usuário (pedido do usuário,
  06/11/2026; levantadas de 21 logs e gravações em `%APPDATA%\VitaSharp\logs` e `captures`, 53 funções). Cada uma com o
  comportamento do Vita (Vita3K e o firmware como referência), não um stub que devolve 0 sem pensar; as que só fazem
  sentido com rede/PSN/hardware ausente devolvem o erro que o Vita devolve nesse caso. Jogos:
  - SceNpManager::NID_FFBFC540 (0xFFBFC540) (PCSE00093)
  - ScePspnetAdhoc::sceNetAdhocInit (0x87805E13) (PCSE00056)
  Sistema do Vita (shell e apps):
  - SceAppMgr::sceAppMgrIsDevelopmentMode (0x7D4F8F83)
  - SceAppMgr::sceAppMgrIsGameBudgetAppPresent (0xE134F5A0)
  - SceAppMgr::sceAppMgrSetRecommendedScreenOrientationActivated (0x75876896)
  - SceAppMgrUser::sceAppMgrCheckRifGD (0x6E5CBCA2)
  - SceAppMgrUser::sceAppMgrMmsMount (0xF4730BA8)
  - SceAppMgrUser::sceAppMgrSetBackRenderPortOwner (0x7149016E)
  - SceCodecEngineUser::NID_9A5E7B2F (0x9A5E7B2F)
  - SceCodecEngineUser::NID_9AD7E66F (0x9AD7E66F)
  - SceErrorUser::sceErrorHistoryPostError (0xBC416CFA)
  - SceHttp::NID_855D5254 (0x855D5254)
  - SceModulemgr::sceKernelInhibitLoadingModule (0x6CED1F63)
  - SceMotion::NID_A408BC69 (0xA408BC69)
  - SceMotionDev::sceMotionDevGetCurrentMagnCalibData (0xAAB4B41F)
  - SceMotionDev::sceMotionDevGetDeviceInfo (0x1F1EFEFB)
  - SceMotionDev::sceMotionDevGetGyroCalibData (0x74555D91)
  - SceMotionDev::sceMotionDevGetMeasMode (0xEA95D3C9)
  - SceMotionDev::sceMotionDevReadForMagnCalib (0xCC4FDA6A)
  - SceNetCtl::NID_777FE4C2 (0x777FE4C2)
  - SceNetCtl::NID_AC326DB7 (0xAC326DB7)
  - SceNetCtl::NID_C33D7374 (0xC33D7374)
  - SceNetCtl::NID_C61F3E96 (0xC61F3E96)
  - SceNetInternal::sceNetInternalIcmConnect (0x93F2FF08)
  - SceNetPsForSyscalls::sceNetSyscallClose (0x1EBC2E28)
  - SceNetPsForSyscalls::sceNetSyscallControl (0xEA0C1B71)
  - SceNetPsForSyscalls::sceNetSyscallGetIfList (0x878274CE)
  - SceNetPsForSyscalls::sceNetSyscallIcmConnect (0x8C3FBC87)
  - SceNetPsForSyscalls::sceNetSyscallIoctl (0x310F0725)
  - SceNetPsForSyscalls::sceNetSyscallSendto (0x39796C01)
  - SceNetPsForSyscalls::sceNetSyscallSocket (0x81A120BE)
  - SceNpBasic::NID_6F1B58F9 (0x6F1B58F9)
  - SceNpBasic::NID_B414E768 (0xB414E768)
  - SceNpDrm::_sceNpDrmGetFixedRifName (0xE935B0FC)
  - SceNpManager::NID_77DE17D4 (0x77DE17D4)
  - SceNpManager::NID_B24FC028 (0xB24FC028)
  - SceNpManager::NID_C89E0B94 (0xC89E0B94)
  - SceNpManager::NID_CFAEB050 (0xCFAEB050)
  - SceNpManager::NID_DDF30B0E (0xDDF30B0E)
  - SceNpManager::NID_E45E4003 (0xE45E4003)
  - SceNpWebApi::NID_4699EF17 (0x4699EF17)
  - SceNpWebApi::NID_E83CE7F1 (0xE83CE7F1)
  - SceNpWebApi::NID_E8FBA9B8 (0xE8FBA9B8)
  - ScePhotoExport::NID_16B1028E (0x16B1028E)
  - SceSblQafMgr::sceSblQafMgrIsAllowNpFullTest (0x72168C6E)
  - SceSslInternal::sceSslInternalGetCertificateAuthority (0x064DFC99)
  - SceSysmem::sceKernelCheckModelCapability (0x0144FBD9)
  - SceTriggerUtil::NID_28EF51DA (0x28EF51DA)
  - SceVshBridge::_vshAppMgrRegisterPath (0x179965E7)
  - SceVshBridge::_vshSblSsGenerateAppKey (0x82F2FBA8)
  - SceVshBridge::vshSblQafMgrIsAllowLiblocationChangeModel (0x58E1DE33)
  - SceVshBridge::vshSblQafMgrIsAllowNearTest (0xD0D4EB67)
  - SceWlan::sceWlanGetConfiguration (0x70AF9BD6)

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
  (VS_DQ_CAP=3: 41/171). Medição: `tools/stripes.py` do scratchpad (razão entre gradiente vertical e horizontal na faixa
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
- [ ] Silent Hill: Book of Memories (PCSE00011): a lanterna e algumas partes do jogo não funcionam direito (relato do
  usuário; o áudio já foi corrigido antes). Falta uma captura: F9 (modo de depuração) + F10 na cena com a lanterna,
  para comparar a luz/sombra com o esperado e achar o desenho ou shader errado
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
  `scratchpad/bugsC5_out/blind_nospin` x `blind_spin`): CPU do processo 35,7 → 25,0 ms/quadro, `saveThreadProc`
  7,57 → 0,43 ms/quadro, `SDL_TimerThread` 5,63 → 0,42; `main` igual (13,5 x 13,1). O fps sem janela já era 30 nos
  trechos de jogo nos dois (com quedas nos carregamentos nos dois, o PC estava dividido com outros agentes). Ritmo sem
  regressão (mesmo build, `--profile`, novo x `DELAY_SPIN=1`): Devious Dungeon p50 16,68/16,68 ms, >25 ms 10 x 23;
  F1 2011 (menus, 25-60 fps) p50 38,9/38,4, p99 64/76; Uncharted (menu) 59,9/59,3 fps, p90 17,6/18,4, >25 ms 3 x 25
  (`bugsC5_out/dd_boot_*`, `f1_menu_*`, `uc_menu_*`). Ainda abertos: (1) o boot (~5 s até o 1º quadro, ~20 s em 2
  núcleos) e (2) a troca de tela (f560-780, `bugsB3_out/blind_tr`)
  Agente C dia 6 (05/11/2026): (2) a troca de tela é a transição do Ren'Py: o menu some de uma vez (602 → 603), 16
  quadros pretos (~0,5 s) e um fade-in da cena nova (619-640, sem defeito de textura sem janela,
  `scratchpad/bugsC6_out/blind_tr_4.png`). O preto é o fade-out que deveria mostrar o menu escurecendo: no dump
  `bugsC6_out/blind_fade2.vsgd` (presents 598-604, trace `blind_fade2_tr/trace_603`) o jogo desenha uma textura 32x32
  (0x612656F0/0x612666F0, preta com alfa médio 0,14) em dois alvos 960x540 (0x62CE6950, 0x62EE4950) e depois compõe os
  dois (fs 1EB8007BE57CDB61, blend 1/1/5): os dois saem pretos, e o menu (que está em 0x60E6C680) não entra na
  composição. Falta ver de onde a textura 32x32 deveria vir (cópia do menu? textura enviada pela CPU?). (1) Boot: 5 s
  até o 1º quadro com o PC ocioso (`bugsC6_out/blind_fade2/log.txt`; ~2,2 s em `main` entre o `Preload` e o thread
  `preloader`, o init do Python do Ren'Py); com a CPU dividida passa de 30 s (watchdog STALL em `blind_fade`); falta
  o tempo do Vita real para comparar
  Agente C dia 8 (06/11/2026, fix/bugs-games-8): **(2) não é defeito, o Vita real faz igual.** No vídeo do Vita
  (`9vleIQnJ8dI`, "Blind Men (PS Vita Gameplay)", Start no menu aos ~82 s; `scratchpad/bugsC8_yt/trans.png`, 10 qps) o
  menu some de uma vez, fica ~0,7 s preto e a cena entra em fade, como aqui. As duas texturas 32x32 são sólidos
  pretos opacos (todos os texels 0,0,0,255 em `trace_603/textures`, não alfa 0,14): o Ren'Py faz o dissolve entre
  dois quadros só com o fundo preto (no present 601 o mesmo tipo de 32x32, 0x60E6B680, é o fundo do menu, com as
  imagens 1280x720 por cima; `bugsC8_out/blind_tr601/trace_601`). Resta o (1), o boot
  Confirmado no jogo pelo usuário (06/11/2026): ok (a troca de tela preta é igual à do Vita)
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
  ou gravar na ordem da memória; patch do experimento em `scratchpad/bugs2_gunvolt_swizzle.patch`
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
  Ferramentas no scratchpad `bugsB2_tools` (`drops.py`, `phase.py`, `gaps.py`, `clickhist.py`, `ffsnr.py`)
  Confirmado pelo usuário (06/11/2026, main 0d298b4): todos os problemas do FIFA 13 corrigidos
- [x] FIFA 13: "a voz da torcida está estranha" (relato do usuário, 05/11/2026 22:22, main da8372d, gravação
  `captures\PCSE00093_20261105_222244_tudo`, 10 s de partida). Achados (fix/bugs-games-7), sem causa no emulador:
  com o `state_start.vss` (60 s, `VITASHARP_NGS_TRACE=1` + `--audio-dump`, `scratchpad/bugsB7_out/state0`) a torcida
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
  `scratchpad/bugsC2_*`). Corrigidos: (1) o renderer criado depois de um LoadExec ou de carregar um estado segue a
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
  presents); saídas em `scratchpad/bugsC3_out/new_lim0_1930`, `fifa_lim0` (compare.txt)
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
  gameplay do Jak 1; as 3 execuções do agente C dia 3 (`scratchpad/bugsC3_out/jak1_live_main/_fix/_final.log`, uma
  com o build da main) fazem o `LoadExec app0:Jak1.self` a partir do menu, a F3DPlaybackThread roda e não há
  `Undefined instruction` nem STALL; idem nos 10 logs com LoadExec de `scratchpad/bugsC_jak` (dia 1)
- [x] Jak and Daxter Collection (PCSA00080), menu (main 84a12b1, teste do usuário 05/11/2026): a prévia (MJPEG,
  textura 0x63FBC400) ainda pisca quando o Jak olha para a direita (provável: dois quadros seguidos com o mesmo
  tamanho, então a escolha pelo tamanho não distingue o quadro k do k+1). A 57c57b1 (quadros tirados na ordem de
  decodificação, 0/506 idas e voltas) é posterior ao relato; com o build do agente C dia 3 (`jak_menu`): 1 ida-e-volta
  em 527 quadros (1351→1352→1353, `scratchpad/bugsC3_out/menu1352.png`), contra 7/504 antes da 57c57b1 — falta o
  usuário confirmar olhando para a direita na main atual
  Concluído por decisão do usuário (06/11/2026): Jak and Daxter fechado, exceto o save state no gameplay (item abaixo)
- [ ] Achados do agente de bug C, dia 3 (05/11/2026, branch fix/bugs-games-3; dados em `scratchpad/bugsC3_*`).
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
  (`scratchpad/bugsC3_cmp`, a cada 10 quadros): 14 idênticos; só mudam os do Jak 1 com sombras (`PCSA00080_20261105_015823` 24/71 quadros,
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
  (`scratchpad/bugsC3_out/blk_lim1462/frame_001770.png`), e no 1783 o jogo para de emiti-lo (corte seco, sem fade).
  É o "blackout" do próprio jogo depois de carregar a área; falta saber se a duração é a do Vita (ver um vídeo do
  Vita/Vita3K) ou se o salto do relógio na carga (`guest clock +15832 ms` no resume) a estende; (c) **mar com buracos pretos por um quadro**
  (replay `jak_fix.vsgd` present 2112, `bugsC3_out/flash2112.png`): nesse quadro o jogo emitiu 52 desenhos a menos
  (1302 x 1354 no 2111), então vem do lado do jogo/CPU, não do renderer; (d) **20 fps** reproduz nos 3 testes (20,0
  fps, `--cpu-mhz=0`, 4 núcleos): CPU do jogo (`process cpu 70 ms/quadro` no log do usuário), fora do escopo deste
  item — desempenho; (e) **câmera fixa**: não reproduzida (a câmera segue o Jak nos 3 testes e no replay). Menu da
  coleção (prévia MJPEG): `jak_menu` com o build de hoje: 1 ida-e-volta em 527 quadros (1351→1352→1353), contra 0/506
  da 57c57b1 e 7/504 antes dela — dentro do ruído; falta o usuário confirmar olhando para a direita.
- [x] Jak 1, agente C dia 5 (05/11/2026, fix/bugs-games-5; dados em `scratchpad/bugsC5_*`). (a) **Céu preto com o ritmo
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
- [x] Jak 1, agente C dia 6 (05/11/2026, fix/bugs-games-6; `scratchpad/bugsC6_*`, cenários `bugsC6_scn/jak1_*.txt`).
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
- [x] Jak 1, agente C dia 8 (06/11/2026, fix/bugs-games-8; `scratchpad/bugsC8_*`, cenários `bugsC8_scn/jak_*.txt`).
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
- [ ] Flicker no desktop, Croixleur Sigma (PCSG00537; 31/10/2026, main 7918e46, captura F10
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

- [ ] The Deer God (PCSB01070; no Vita3K trava no boot): engasgos no gameplay (30/10/2026, main 4603a03, logs
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
  Desligada por padrão até o usuário confirmar na janela.

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
  30 fps, `scratchpad/bugsB3_out/sonic_fix1..3`), e com a chave desligada o mesmo build trava no quadro 23
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
- [ ] Unity: as 10 "importações de variável sem resolver" do `Il2CppAssemblies` (NIDs 338B0BD1, 3A451B91, 550DFE23,
  56354517, 5EF67445, 6ACA1D57, 71A78083, 79F9DB75, AA0340C3, F9A1DE92, importadas pelo `UnityPlayer_536/537_Release`;
  Akiba's Beat e God Wars, 04/11/2026, pedido do usuário). O aviso sai no boot (`Emulator.cs` ~522), antes de o jogo
  carregar `app0:/Media/Modules/Il2CppAssemblies.suprx` (~0,6 s depois); o `ModuleLoader.RegisterExports` aplica as
  relocações pendentes quando o módulo exporta a variável. Confirmar que o Il2CppAssemblies exporta as 10 e que as
  relocações foram aplicadas; se faltar alguma, implementar; e só avisar das que continuarem sem resolver depois que
  o módulo de origem carregar (hoje o aviso aparece em todo jogo Unity mesmo quando tudo é resolvido)
- [ ] Akiba's Beat (PCSG00952, Unity): validar o jogo todo (pedido do usuário, 04/11/2026; sessão de 2,5 min, log
  `logs61104_201229_PCSG00952.log`, main 5d4b998). No log: 60 fps quase o tempo todo, mas a imagem fica parada
  ~10 s (quadro 4171, 20:14:05-20:14:16, `frozen: identical image for 600 presents`) e ~4 s de tela preta em 20:13:46
  (talvez carregamento); 2,6 s de engasgo de shaders na primeira vez (91 desenhos em 14 quadros); 10 importações de
  variável sem resolver do `Il2CppAssemblies` (também no God Wars); 1 underrun de áudio do CRI Atom. A captura
  automática `PCSG00952_20261104_201246` é só a tela da Unity (falso positivo). Conferir com vídeos do Vita de
  verdade se as paradas são carregamento normal ou travamento, e jogar além do começo
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
  `tools/audrep.py` do scratchpad), e há kill+play seguidos a cada 4-8 ticks (192 bytes) pela mesma função do jogo
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

- [ ] Zero Time Dilemma (PCSE00781; no Vita3K trava no boot): 40 dos 105 shaders de fragmento leem o registrador global G16 (banco especial com o bit 0x40), que o tradutor USSE não conhece e troca por 0 (aviso `load from unsupported bank Global register 16`; 31/10/2026, branch fix/ztd, `shadertest PCSE00781`). As cenas parecem corretas, mas falta descobrir o que é o G16 (o Vita3K também não trata o banco GLOBAL). O jogo agora passa do boot (sceGxmColorSurfaceInit com data NULL) e roda as cenas da história a 30 fps; também aparecem `scene without colour surface` e um render target 1024x1024 amostrado enquanto é desenhado perto do quadro 6200
  Achados (05/11/2026, fix/bugs-games-4, `shadertest <app> --warnings` lista os avisos por programa): os 40 são as 10
  variantes (m/o/msaa/...) de 4 materiais, `HemiRimBump`, `HemiRimBumpB`, `HemiRimBumpHDR` e `toon` (iluminação de
  borda/toon dos personagens); nenhum outro shader tem aviso. Próximo: ver no `--dump` em que conta o G16 entra (se
  multiplica a borda, o efeito de rim light some) e comparar com um vídeo do Vita real
  Agente C dia 6 (05/11/2026): **menu do título sem regressão**: 59,8 fps, p50 16,68 ms, 6 de 299 intervalos acima de
  17,5 ms com o PC ocioso (`scratchpad/bugsC6_out/ztd_menu.log`, `bugsC3_data/tests/ztd_menu/sheet.png`); os 47-57 eram
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
  comparar com o vídeo do Vita (`zwPJR5er054`, `scratchpad/bugsC8_yt/ztd.webm`, 10 primeiros minutos)
- [ ] Anexo da palavra extra do registrador de saída (TileWord1): falta nas cenas supersampled, no caminho tile-based (Android) e com VITASHARP_MSAA_FETCH=copy (lá a palavra continua perdida, com aviso no log; 29/10/2026: `TileWord1Unsupported` ainda recusa esses três casos)
- [ ] Fibers: `_sceFiberAttachContext*` exige status Init (o firmware aceita qualquer fiber ocioso sem área de contexto; 0/0 é switch simples) e o topo da pilha deveria ser `(addr+size-1)&~7` (conferido em 29/10/2026 no main c12927d: `Fiber.AttachContext` ainda devolve ERR_STATE fora de Init e R13 = `(addr+size)&~7`)
- [ ] MSAA real: leitura da imagem pelo próprio desenho (input attachment) é indefinida quando triângulos do mesmo desenho se sobrepõem (sem `VK_EXT_rasterization_order_attachment_access` na GTX 1650/Iris Xe); causa diferenças de ±1 entre execuções (29/10/2026: o log da GTX 1650 ainda mostra `GPU missing: ... RasterizationOrderColor`)
- [ ] Sonic Mania: ~18 estalos por segundo na saída de música (44,1 kHz); pode ser do próprio conteúdo, não investigado (29/10/2026, main c12927d, `mania_boot.txt` com `--audio-dump`: 522 estalos em 34,5 s de som, 15,1/s, sem relação com a fase do grain de 1024; sem underruns)
  Não reproduz (05/11/2026, fix/bugs-audio, os mesmos `mania_boot` e `--audio-dump`): porta BGM 44,1 kHz grain 1024, 62 s (34 s com som), `audrep.py` 0 estalos e um detector de segunda diferença mais sensível 0,1/s; sem underruns. A conversão 44,1→48 kHz da saída é do OpenAL (fora do dump); falta o usuário ouvir no PC.
  Conferido de novo (05/11/2026, fix/bugs-audio-2, no mesmo dump): 0 quedas de >30 dB em 2,5 ms (`drops.py`) e nenhuma
  emenda na fase de 256/512/1024 amostras (`phase.py`, pico 1,16x da mediana, o mesmo do FIFA sem defeito); os 8
  eventos de segunda diferença são transientes da música entre 32 e 38 s. Sem defeito medível no que o jogo gera
- [ ] ATRAC9: a saída do nosso decodificador tem a polaridade invertida em relação ao FFmpeg (inaudível; confirmar qual está certa; o decodificador não mudou desde então, e só dá para decidir comparando com o Vita real)
- [ ] `sceNpManagerGetCachedParam`: layout não confirmado, por isso responde "sem conta" mesmo com a conta PSN emulada conectada (o UnityNpToolkit do 99Vidas chama e segue em frente; 29/10/2026: `Np.cs` ainda devolve ERR_SIGNED_OUT)
- [ ] Revalidação dos jogos do PC (31/10/2026, branch perf/android-phone-2; folhas e resumo em
  `scratchpad/phone2_shots/pc`, `review.md`): The Deer God (PCSB01070) sem limite de quadros (214-348 fps; antes 60);
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
- [ ] Filmes no celular ainda mais lentos que no PC (31/10/2026, perf/android-phone-2): o primeiro filme do F1 2011 leva
  5,6 s contra 4,8 s no PC (45 grãos de silêncio no som do filme; Uncharted 40 por volta do filme do título), já com o
  decodificador H.264 do próprio Android (o da MediaTek segurava quadros: 6,25 s, 75 lacunas). Os decodificadores não
  são o gargalo (AAC 5 ms, H.264 12,8 ms por chamada, MediaExtractor 0,4 ms); falta ver as threads do player do jogo
  (simpleperf). Comparar também os dumps de áudio de F1 (menu e corrida) e God of War com o PC (`vitasharp-perf
  audio-check`): 245 e 157 estalos no celular
