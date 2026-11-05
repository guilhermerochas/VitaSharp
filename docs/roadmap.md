# Roadmap

Lista de itens futuros do VitaSharp. Marque `[x]` quando um item for concluído.

## Diagnóstico

- [ ] Teclas para alternar opções gráficas com o jogo rodando (MSAA real / antigo / desligado e outras)

## Save states

- [ ] Salvar e carregar o estado inteiro do emulador: CPU (registradores, NEON/VFP, monitor exclusivo), memória,
  objetos do kernel (threads, semáforos, mutexes, event flags, timers), estado dos módulos HLE (áudio, NGS, GXM,
  arquivos abertos, câmera, rede simulada) e estado da GPU (imagens renderizadas, texturas, cache). Decisão do
  usuário (01/11/2026): estado dos módulos salvo **por módulo** (`ISnapshotState` explícito em cada um), não por um
  serializador genérico com reflexão; pode ser gerado na compilação (source generator) a partir dos campos, com os
  casos especiais escritos à mão (vídeo/MediaCodec, saídas de áudio, arquivos, GXM). Desenho e estimativa em
  `docs/notes/dev-snapshots.md` (CPU/memória/threads ~2 dias; módulos de Uncharted/F1 3–5 dias; total 1–2 semanas);
  fica para depois da prioridade de desempenho no Android
- [ ] Formato versionado e tolerante a mudanças do emulador; aviso claro quando um estado antigo não puder ser carregado
- [ ] Interface: slots por jogo com miniatura, atalhos (salvar/carregar rápido) e menu de pausa
- [ ] Uso em diagnóstico: o jogador salva o estado no momento do problema e manda junto com a captura

## Desempenho (PC)

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
- [ ] Instalação de `.pkg` + `work.bin` muito lenta no build Native AOT do desktop (relato do usuário, 31/10/2026: a
  "transferência de arquivos" demora demais): medir a vazão em MB/s do `PkgInstaller` no pacote AOT contra o build
  JIT do mesmo commit, por etapa (leitura do `.pkg`, descriptografia AES-CTR com a klicensee, PFS/arquivos,
  gravação em `ux0:app`). O AES-CTR (`AesUtil.Ctr`) já cifra em lotes de 64 KB com o AES do sistema, mas monta os
  contadores bloco a bloco num laço e faz o XOR à parte (`XorInPlace`; ver se está vetorizado no AOT); o `Extract`
  lê em blocos de 1 MB sem sobrepor leitura, decifra e gravação. Corrigir com base na medição: contadores e XOR
  vetorizados (ou CTR inteiro com `Vector128`/AES-NI), leitura/decifra/gravação em paralelo, sem cópias extras e com
  barra de progresso; meta: perto da velocidade do disco
- [ ] Engasgos da primeira vez: montar os pipelines que faltam com `VK_EXT_graphics_pipeline_library` (ligação
  rápida, sem otimizar, no thread da GPU) e trocar pelo otimizado em segundo plano; prever também as variantes
  tile-word e de saída inteira. Pular o desenho foi analisado e descartado: quase nada do que trava pode ser pulado
  sem risco (Killzone 0 de 91 engasgos, Tearaway 1 de 14), ver `docs/notes/pipeline-skip.md`
- [ ] Prioridade das threads em CPU híbrida (núcleos E) e na bateria: medir `SetThreadSelectedCpuSets` e o
  power throttling num Intel de 12ª geração ou mais novo (as threads da CPU emulada e da GPU já sobem para acima do
  normal, `--host-prio`; ver `docs/notes/thread-priority.md`)
- [ ] Queda para 50-54 fps no menu do Sonic Mania

## Android

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

## Firmware e sistema

- [ ] Licença em zRIF na instalação de `.pkg`: hoje `PkgInstaller` só aceita o arquivo `.rif`/`work.bin` (lê a
  klicensee em 0x50). Aceitar também a licença no formato zRIF (o texto base64 com o `.rif` comprimido em zlib, com
  o dicionário fixo, que o NoNpDrm exporta das licenças do próprio Vita do usuário): decodificar, descomprimir, validar
  o content ID contra o `.pkg` e gravar o `.rif` em `ux0:license/<TITLEID>` e o `work.bin`; no CLI (`install <pkg>
  <rif|zRIF>`), no launcher (campo de texto na instalação) e no app Android. Core, CLI e app Android feitos
  (04/11/2026, `Package/Zrif.cs`, fix/user-bugs); falta o campo de zRIF no launcher do desktop
- [ ] Sistema do Vita de verdade (ver docs/notes/system-apps.md): o shell já mostra os jogos, roda vários processos e faz a
  configuração inicial; falta o app de Troféus/Configurações desenharem (composição da tela compartilhada), o botão PS
  no PC, o texto cortado no boot direto, o travamento do teclado na tela (IME) e as funções restantes (sensor de
  movimento, sockets do sistema)
- [ ] Validar a fundo TODOS os apps do sistema (04/11/2026, pedido do usuário): cada menu, aba, categoria, sub-página,
  botão, menu de opções e caminho de voltar/fechar de cada app, abrir e fechar duas vezes, alternar entre apps (botão PS,
  retomar, encerrar); nada pode travar ou crashar. Erros só por falta de rede/PSN/USB/aparelho ficam anotados como
  esperados. Primeiro caso: Música → Artistas mostra erro (log do usuário 18:55, `AVCH ... SQL Failed: DROP TRIGGER`,
  `IO: unmapped path ''`). Entregar checklist por app em `docs/notes/system-apps.md` e roteiros (.ps1/.sh) para rodar de
  novo como teste de regressão (branch fix/system-apps-4)
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

- [ ] Jak and Daxter Collection (PCSA00080): no menu da coleção, a faixa vertical do meio (a prévia do jogo
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
- [ ] Blind Men (PCSB01421; no Vita3K tela preta): roda e é jogável, sem funções faltando nem erros (31/10/2026, main
  13ca113, log `20261031_193014_PCSB01421`), mas: (1) demora para começar (carregamento inicial longo; comparar com o
  Vita real); (2) nas trocas de tela aparece por ~0,5 s uma imagem preta com pequenos problemas de textura (provável
  render target/textura nova usada antes de ser preenchida, ou conteúdo antigo de uma superfície reaproveitada; pegar
  um F10 ou dump de GPU numa troca de tela); (3) 19–30 fps, quase sempre 22–24 (Unity, alvo provável 30): medir com
  `--profile`/F10 se é CPU ou espera da GPU, como no The Deer God
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

- [ ] Sonic & All-Stars Racing Transformed (PCSE00056; no Vita3K também não abre): trava no quadro 24, antes dos
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
- [ ] MotorStorm RC (PCSA00034): trava na tela de termos (relato do usuário, 04/11/2026, main 5d4b998, log
  `logs61104_201525_PCSA00034.log`). Roda a ~40 fps até o quadro 756 e então nenhum quadro novo (watchdog STALL).
  No relatório do travamento a thread `main` está **Dormant com pc=0x00000000** (lr 0x8114669F): ela parou, não está
  esperando nada, logo depois de uma série de chamadas de rede/PSN que falham (sceHttpInit 0x80431020, sceNetCtlInit
  0x80412102, TUS/TSS/Commerce2, `sceNpSnsFacebookInit` sem implementação). Provável salto para um ponteiro nulo
  (callback não registrado) ou saída da thread no caminho de erro; as threads de jobs ficam em loop de
  `sceKernelWaitSema` com timeout
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
- [ ] FIFA 13 (PCSE00093): na partida (Botafogo x Fluminense, main 5d4b998), a faixa do campo depois da linha lateral
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

- [ ] Crazy Market (PCSE00291, joga normalmente; 30/10/2026, main 4dbe06c, log `20261030_204008_PCSE00291`): a
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

- [ ] Zero Time Dilemma (PCSE00781; no Vita3K trava no boot): 40 dos 105 shaders de fragmento leem o registrador global G16 (banco especial com o bit 0x40), que o tradutor USSE não conhece e troca por 0 (aviso `load from unsupported bank Global register 16`; 31/10/2026, branch fix/ztd, `shadertest PCSE00781`). As cenas parecem corretas, mas falta descobrir o que é o G16 (o Vita3K também não trata o banco GLOBAL). O jogo agora passa do boot (sceGxmColorSurfaceInit com data NULL) e roda as cenas da história a 30 fps; também aparecem `scene without colour surface` e um render target 1024x1024 amostrado enquanto é desenhado perto do quadro 6200
- [ ] Anexo da palavra extra do registrador de saída (TileWord1): falta nas cenas supersampled, no caminho tile-based (Android) e com VITASHARP_MSAA_FETCH=copy (lá a palavra continua perdida, com aviso no log; 29/10/2026: `TileWord1Unsupported` ainda recusa esses três casos)
- [ ] Fibers: `_sceFiberAttachContext*` exige status Init (o firmware aceita qualquer fiber ocioso sem área de contexto; 0/0 é switch simples) e o topo da pilha deveria ser `(addr+size-1)&~7` (conferido em 29/10/2026 no main c12927d: `Fiber.AttachContext` ainda devolve ERR_STATE fora de Init e R13 = `(addr+size)&~7`)
- [ ] MSAA real: leitura da imagem pelo próprio desenho (input attachment) é indefinida quando triângulos do mesmo desenho se sobrepõem (sem `VK_EXT_rasterization_order_attachment_access` na GTX 1650/Iris Xe); causa diferenças de ±1 entre execuções (29/10/2026: o log da GTX 1650 ainda mostra `GPU missing: ... RasterizationOrderColor`)
- [ ] Sonic Mania: ~18 estalos por segundo na saída de música (44,1 kHz); pode ser do próprio conteúdo, não investigado (29/10/2026, main c12927d, `mania_boot.txt` com `--audio-dump`: 522 estalos em 34,5 s de som, 15,1/s, sem relação com a fase do grain de 1024; sem underruns)
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
