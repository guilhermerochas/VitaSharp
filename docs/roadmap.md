# Roadmap

Lista de itens futuros do VitaSharp. Marque `[x]` quando um item for concluído.

## Diagnóstico

- [ ] Teclas para alternar opções gráficas com o jogo rodando (MSAA real / antigo / desligado e outras)

## Save states

- [ ] Salvar e carregar o estado inteiro do emulador: CPU (registradores, NEON/VFP, monitor exclusivo), memória,
  objetos do kernel (threads, semáforos, mutexes, event flags, timers), estado dos módulos HLE (áudio, NGS, GXM,
  arquivos abertos, câmera, rede simulada) e estado da GPU (imagens renderizadas, texturas, cache)
- [ ] Formato versionado e tolerante a mudanças do emulador; aviso claro quando um estado antigo não puder ser carregado
- [ ] Interface: slots por jogo com miniatura, atalhos (salvar/carregar rápido) e menu de pausa
- [ ] Uso em diagnóstico: o jogador salva o estado no momento do problema e manda junto com a captura

## Desempenho (PC)

- [ ] Engasgos da primeira vez: montar os pipelines que faltam com `VK_EXT_graphics_pipeline_library` (ligação
  rápida, sem otimizar, no thread da GPU) e trocar pelo otimizado em segundo plano; prever também as variantes
  tile-word e de saída inteira. Pular o desenho foi analisado e descartado: quase nada do que trava pode ser pulado
  sem risco (Killzone 0 de 91 engasgos, Tearaway 1 de 14), ver `docs/notes/pipeline-skip.md`
- [ ] Prioridade das threads em CPU híbrida (núcleos E) e na bateria: medir `SetThreadSelectedCpuSets` e o
  power throttling num Intel de 12ª geração ou mais novo (as threads da CPU emulada e da GPU já sobem para acima do
  normal, `--host-prio`; ver `docs/notes/thread-priority.md`)
- [ ] Queda para 50-54 fps no menu do Sonic Mania

## Android

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
- [ ] AAudio de baixa latência no Redmi Note 14: o stream sai "normal shared" (a saída do alto-falante não tem
  FastMixer, rajada informada de 4096 quadros, ~200 ms de latência); ver se outro caminho (MMAP, sem
  `setBufferCapacityInFrames`, AudioTrack com `PERFORMANCE_MODE_LOW_LATENCY`) consegue a saída rápida
- [ ] God of War Collection (PCSA00126) no celular: o Kratos aparece com problemas de textura (visto pelo usuário em
  31/10/2026 no Redmi Note 14, build de release do `perf/android-phone`); comparar com o desktop na mesma cena (perfis
  `desktop` e `mali`) para saber se é da Mali (conversão BC → ASTC/RGBA8, formato sem suporte, mipmaps) ou geral, e
  pegar o log/captura do celular
- [ ] Flicker no celular em todos os jogos testados (Knightin'+, Devious Dungeon, F1 2011, Uncharted, God of War
  Collection): sequências de 10+ quadros seguidos (`screencap`/`screenrecord`) comparadas com o desktop na mesma cena
  e zero quadro piscando; o que falta hoje: o quadro escuro do Uncharted (1 a cada volta do filme do título) e
  conferir o God of War
- [ ] GPU da Mali sobrecarregada (Uncharted no título: 33,7 ms de GPU por quadro na Mali-G57 contra 4,2 ms no PC,
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
  - [ ] Crash do driver Mali em `vkEndCommandBuffer` (GPU worker, ponteiro nulo em `libGLES_mali.so`): de vez em quando
    no F1 2011 entre o menu e a corrida, também nas versões anteriores; causa ainda não achada
  - [ ] Som dos filmes do F1 2011 no celular: um quadro AAC de silêncio a cada quatro (o player do jogo recebe o áudio
    devagar pelo SceMp4/MediaExtractor + SceAudiodec/MediaCodec; no PC, com FFmpeg, sem buracos); imagem dos filmes já
    corrigida (31/10: fila de quadros do MediaCodec, pts da unidade, sem flashes pretos)
  - [ ] Shaders: shaderc em segundo plano nos núcleos rápidos e reaproveitar o cache de shaders gerado no PC
  - [ ] GPU: conversão para ASTC com cache em disco sempre ligada na Mali (sem BC), MSAA nativo
- [x] Armazenamento: escolher a pasta dos jogos (Storage Access Framework), instalar VPK, saves e instalar o firmware
  (.PUP) pelo seletor (30/10/2026: pasta específica do app como raiz de dados, VPK/PUP/driver pelo seletor do sistema,
  lidos no lugar; o motivo de não usar uma pasta do SAF como raiz está em `docs/android/README.md`)

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

- [ ] Sistema do Vita de verdade (ver docs/notes/system-apps.md): o shell já mostra os jogos, roda vários processos e faz a
  configuração inicial; falta o app de Troféus/Configurações desenharem (composição da tela compartilhada), o botão PS
  no PC, o texto cortado no boot direto, o travamento do teclado na tela (IME) e as funções restantes (sensor de
  movimento, sockets do sistema)
- [ ] App de Câmera (NPXS10007) trava logo depois de abrir (30/10/2026, main 4dbe06c, log `20261030_204528_shell`):
  a câmera traseira abre (`Integrated Camera` 640x480 YUYV), o primeiro quadro é lido e a textura YUV420P3 aparece,
  e então o app para; não há mais leituras da câmera, a thread `ScePhotoCamMain` está Dormant, uma
  `SceCameraCoreCameraDeviceThread` espera em `condvar SceCameraCoreCameraDeviceLoopStatusCond` e a outra está Dormant.
  Funções que faltam no caminho: `ScePhotoExport::NID_16B1028E`, `SceLibLocation::NID_EFC9BD25` / `NID_2311B24A`,
  `sceAppMgrMmsMount`, `_vshAppMgrRegisterPath`, `sceKernelCheckModelCapability`, e um `IO: unmapped path ''` logo
  antes de criar as threads da câmera

## Código

- [ ] Refatorar o projeto para o guia de estilo C# do Google (https://google.github.io/styleguide/csharpguide.html), exceto as
  chaves (ficam na linha de baixo, padrão do C#) e a indentação (mantém 4 espaços e ~120 colunas): nomes, ordem dos
  membros, usings, comentários, `var`; regras num `.editorconfig` com analisadores. Cuidados: o GLSL gerado vem de
  strings literais do C# (o conteúdo delas tem que ficar idêntico, conferir com o shadertest) e fazer num momento sem
  outros branches abertos (o diff toca quase todos os arquivos)

## Bugs em aberto

- [ ] Croixleur Sigma (PCSG00537; no Vita3K trava ao gerar shaders): roda a ~30 fps, mas no início 3 cenas são
  puladas com `vkAllocateMemory(host buffer) failed: ErrorOutOfDeviceMemory` e "out of host memory for 0,0 MB" com a
  VRAM a 4% e o heap do host a 2% (31/10/2026, main e74a747, log `20261031_030841_PCSG00537`); a captura F10 também
  falhou pelo mesmo erro. Provavelmente um pedido de alocação com tamanho absurdo ou zero (uma superfície/buffer com
  tamanho calculado errado), não falta de memória de verdade; ver qual buffer é pedido no `EndSceneCommand` e o
  tamanho

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
  um agente compilando o APK naquela hora); se repetir com o PC livre, ver quem sinaliza essa espera

- [ ] Sonic & All-Stars Racing Transformed (PCSE00056; no Vita3K também não abre): trava no quadro 24, antes dos
  logos (watchdog `STALL` aos 6 s e 31 s; 30/10/2026, main 4603a03, log `20261030_225954_PCSE00056`). A
  `MainThread`, a `AchievementIssueThread`, a `SumoPlatform` e o `CRI Server Manager` ficam rodando (polling), o
  resto espera em semáforos/eventflags (`SlSemaphore_Platform`, `SlThreadGateBank`); a única função que falta é
  `ScePspnetAdhoc::sceNetAdhocInit` (NID 0x87805E13), chamada pela MainThread logo antes. Ver o que a MainThread e a
  `SumoPlatform` esperam (provavelmente rede/PSN ou conquistas) e a instrução `strex r1, r1, [r2]` (Rd = Rt) que
  derruba o Vita3K

- [ ] FIFA 13 (PCSE00093; no Vita3K nem abre): no treino da Arena, uma malha enorme preta e branca se estica da
  frente da câmera até o meio da tela e se mexe a cada quadro (provavelmente o jogador com os vértices "explodidos":
  skinning — matrizes de osso, formato/índice dos atributos de osso ou o buffer de uniformes —, ou um triângulo que
  cruza o plano próximo sem recorte). Captura F10 em `%APPDATA%\VitaSharp\diagnostics\PCSE00093_20261030_204939`
  (127 quadros em `replay/`, 109 desenhos em `draws.txt`; aviso de profundidade de cena MSAA lida como textura
  X8U24 960x1088) (30/10/2026, main 4dbe06c)

- [ ] Crazy Market (PCSE00291, joga normalmente; 30/10/2026, main 4dbe06c, log `20261030_204008_PCSE00291`): a
  biblioteca de som do jogo (SCREAM/SndStream 7.6.9) avisa 3456 vezes `sndsi_UpdateVoicePositionOfStream:
  locationDelta < 0, voiceLocation = 0x0, previous = 0x700…` — a posição de leitura que a voz NGS informa volta a 0
  em vez de avançar (estado do player em `sceNgsVoiceGetStateData`, `Ngs.cs`); conferir com o firmware/Vita3K qual
  campo é acumulado (pode causar música repetindo ou cortando em outros jogos com SndStream). Também:
  `sce_scream_api.cpp(1385): Invalid parameter` na inicialização do som e `sceNetInit` chamado duas vezes (o
  segundo recebe EBUSY, como no firmware; o jogo segue)

- [ ] Anexo da palavra extra do registrador de saída (TileWord1): falta nas cenas supersampled, no caminho tile-based (Android) e com VITASHARP_MSAA_FETCH=copy (lá a palavra continua perdida, com aviso no log; 29/10/2026: `TileWord1Unsupported` ainda recusa esses três casos)
- [ ] Fibers: `_sceFiberAttachContext*` exige status Init (o firmware aceita qualquer fiber ocioso sem área de contexto; 0/0 é switch simples) e o topo da pilha deveria ser `(addr+size-1)&~7` (conferido em 29/10/2026 no main c12927d: `Fiber.AttachContext` ainda devolve ERR_STATE fora de Init e R13 = `(addr+size)&~7`)
- [ ] MSAA real: leitura da imagem pelo próprio desenho (input attachment) é indefinida quando triângulos do mesmo desenho se sobrepõem (sem `VK_EXT_rasterization_order_attachment_access` na GTX 1650/Iris Xe); causa diferenças de ±1 entre execuções (29/10/2026: o log da GTX 1650 ainda mostra `GPU missing: ... RasterizationOrderColor`)
- [ ] Sonic Mania: ~18 estalos por segundo na saída de música (44,1 kHz); pode ser do próprio conteúdo, não investigado (29/10/2026, main c12927d, `mania_boot.txt` com `--audio-dump`: 522 estalos em 34,5 s de som, 15,1/s, sem relação com a fase do grain de 1024; sem underruns)
- [ ] ATRAC9: a saída do nosso decodificador tem a polaridade invertida em relação ao FFmpeg (inaudível; confirmar qual está certa; o decodificador não mudou desde então, e só dá para decidir comparando com o Vita real)
- [ ] `sceNpManagerGetCachedParam`: layout não confirmado, por isso responde "sem conta" mesmo com a conta PSN emulada conectada (o UnityNpToolkit do 99Vidas chama e segue em frente; 29/10/2026: `Np.cs` ainda devolve ERR_SIGNED_OUT)
