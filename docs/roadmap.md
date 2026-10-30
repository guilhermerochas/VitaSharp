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
- [ ] JIT: multiplicações Thumb (SMULxy/SMLAxy/SMULW/SMMUL) e somas/subtrações paralelas (Mania, Devious Dungeon)
- [ ] JIT: VLD2-4/VST2-4, VSHL.reg.64, VMULL, VCLS inline
- [ ] JIT: registradores NEON presos em registradores XMM do PC e comparação + desvio juntos (medir antes)
- [ ] JIT: compilação mais barata por bloco
- [ ] Queda para 50-54 fps no menu do Sonic Mania

## Android

- [x] App .NET MAUI com interface declarativa em C# (MauiReactor), seguindo os mockups em `docs/android/` (biblioteca
  e troféus, ajustes, firmware, conta PSN e rede, driver e informações da GPU, Sistema do Vita) (30/10/2026: gera o
  APK, `src/VitaSharp.Android`; ainda não rodou num aparelho nem no emulador)
- [ ] Primeira execução no emulador do Android e num aparelho: a lista do que verificar está no fim de
  `docs/android/README.md` (superfície Vulkan, ciclo de vida, JIT nativo, entrada, logcat, SAF)
- [ ] shaderc para Android: compilar a `libshaderc_shared.so` (arm64-v8a e x86_64) com o NDK e empacotar no APK; sem
  ela o renderizador não compila shaders no celular
- [x] Controles na tela (horizontal), mapeamento de controles Bluetooth e toque traseiro (30/10/2026: `OnScreenPad`,
  `AndroidGamepads` com os perfis de controle do desktop e o botão TRÁS; falta testar num aparelho e o editor de
  layout)
- [ ] Sensores do celular alimentando o SceMotion (interface `IMotionSource` já existe no Core) e câmera do celular
  no SceCamera
- [ ] Validar num aparelho Adreno e Mali a camada de quirks de GPU (`docs/notes/mobile-gpu.md`): quais contornos
  continuam necessários, `VK_EXT_rasterization_order_attachment_access` (nunca executado) e o ganho real dos
  load/store por cena no perfilador do aparelho
- [ ] Menos interrupções de cena no renderizador (no Killzone cada cena vira ~13 renderizações por uploads de
  textura e cópias; em GPU tile-based cada uma custa um store e um load da memória do tile)
- [ ] Texturas BC/PVRTC convertidas para ASTC (hoje BC vira RGBA8 na CPU, com cache em memória) e cache em disco
- [ ] Caminho para Vulkan 1.1 (Adreno 6xx com drivers antigos): render passes no lugar de dynamic rendering
- [ ] Driver de GPU customizado: compilar e empacotar a libadrenotools, desenhar a tela "Driver da GPU" e testar num
  aparelho (a tela "Driver da GPU" e o carregador já estão no app, `src/VitaSharp.Android`)
- [ ] Vídeo pelo MediaCodec e áudio pelo AAudio/Oboe (compilados no app, `src/VitaSharp.Android/Audio` e `Media`; falta testar num aparelho e o FFmpeg para os vídeos)
- [ ] Energia e telas: Performance Hint API (ADPF), Swappy (90/120 Hz), threads nos núcleos rápidos
- [x] Armazenamento: escolher a pasta dos jogos (Storage Access Framework), instalar VPK, saves e instalar o firmware
  (.PUP) pelo seletor (30/10/2026: pasta específica do app como raiz de dados, VPK/PUP/driver pelo seletor do sistema,
  lidos no lugar; o motivo de não usar uma pasta do SAF como raiz está em `docs/android/README.md`)

## Firmware e sistema

- [ ] Sistema do Vita de verdade (ver docs/notes/system-apps.md): o shell já mostra os jogos, roda vários processos e faz a
  configuração inicial; falta o app de Troféus/Configurações desenharem (composição da tela compartilhada), o botão PS
  no PC, o texto cortado no boot direto, o travamento do teclado na tela (IME) e as funções restantes (sensor de
  movimento, sockets do sistema)

## Código

- [ ] Refatorar o projeto para o guia de estilo C# do Google (https://google.github.io/styleguide/csharpguide.html), exceto as
  chaves (ficam na linha de baixo, padrão do C#) e a indentação (mantém 4 espaços e ~120 colunas): nomes, ordem dos
  membros, usings, comentários, `var`; regras num `.editorconfig` com analisadores. Cuidados: o GLSL gerado vem de
  strings literais do C# (o conteúdo delas tem que ficar idêntico, conferir com o shadertest) e fazer num momento sem
  outros branches abertos (o diff toca quase todos os arquivos)

## Bugs em aberto

- [ ] Gravity Rush: na tela de título "Tap the Apple", um toque registra (a maçã balança e o aviso some por ~180 quadros) mas não pega a maçã nem avança; igual em 0,46/0,48 e no centro, nos quadros 1000 e 1400 (29/10/2026, main c12927d, `gr_boot.txt` só com o toque); a diferença antiga "0,46/0,48 falha, centro funciona" não se repete. Falta saber se o Vita exige outro gesto (segurar, tocar na maçã de outro jeito)
- [ ] Killzone: depois da correção dos blocos, cada tiro clareia a cena por 1–3 quadros e às vezes deixa a tela inteira com um brilho forte (a luz do disparo agora é aplicada); confirmar a intensidade com o Vita real (não dá para conferir aqui)
- [ ] Killzone: congelamento raro com comandos da GPU pendentes (visto uma vez no quadro 1275; 29/10/2026, main c12927d: não se repetiu no `kz_boot.txt` sem `--turbo`, 3000 quadros, nem no `kzl_walk3`, 10500 quadros)
- [ ] Killzone: `kz_boot.txt` (com `--turbo`) para no quadro 6 em 2 de 2 execuções (29/10/2026, main c12927d, `.vitadata`): a thread `killzone_vita` espera em `condvar SceFiosOpWait`, as threads `SceFiosIO`/`SceFiosCallback` esperam em `SceFiosSchedulerWakeup` e a GPU está ociosa (29/29 comandos concluídos); o watchdog acusa `STALL` desde 6 s. Sem `--turbo`, o mesmo cenário chega aos 3000 quadros. Parece uma notificação perdida do FIOS quando o relógio do convidado avança direto (`GuestClock.Turbo`)
- [ ] Killzone: em algumas áreas do kzl_walk3 o jogo cai para 5–10 fps por 20–40 s; a thread principal fica em polling com `sceKernelDelayThread` (loop em 8100D692) esperando algo que ninguém produz (jobs e GPU ociosos); ver docs/notes/kz-mainthread.md (29/10/2026, main c12927d: 1 execução completa do kzl_walk3 com `--profile --profile-from=4300` ficou em 29–30 fps do quadro 3300 ao 10500, sem nenhum `PROFILE slow window`; como só acontecia em algumas execuções, ainda não dá para dar como corrigido)
- [ ] Anexo da palavra extra do registrador de saída (TileWord1): falta nas cenas supersampled, no caminho tile-based (Android) e com VITASHARP_MSAA_FETCH=copy (lá a palavra continua perdida, com aviso no log; 29/10/2026: `TileWord1Unsupported` ainda recusa esses três casos)
- [ ] Fibers: `_sceFiberAttachContext*` exige status Init (o firmware aceita qualquer fiber ocioso sem área de contexto; 0/0 é switch simples) e o topo da pilha deveria ser `(addr+size-1)&~7` (conferido em 29/10/2026 no main c12927d: `Fiber.AttachContext` ainda devolve ERR_STATE fora de Init e R13 = `(addr+size)&~7`)
- [ ] MSAA real: leitura da imagem pelo próprio desenho (input attachment) é indefinida quando triângulos do mesmo desenho se sobrepõem (sem `VK_EXT_rasterization_order_attachment_access` na GTX 1650/Iris Xe); causa diferenças de ±1 entre execuções (29/10/2026: o log da GTX 1650 ainda mostra `GPU missing: ... RasterizationOrderColor`)
- [ ] Sonic Mania: ~18 estalos por segundo na saída de música (44,1 kHz); pode ser do próprio conteúdo, não investigado (29/10/2026, main c12927d, `mania_boot.txt` com `--audio-dump`: 522 estalos em 34,5 s de som, 15,1/s, sem relação com a fase do grain de 1024; sem underruns)
- [ ] ATRAC9: a saída do nosso decodificador tem a polaridade invertida em relação ao FFmpeg (inaudível; confirmar qual está certa; o decodificador não mudou desde então, e só dá para decidir comparando com o Vita real)
- [ ] Killzone: commit 3dc7d41 (sincronização U4U4U4U4) segurado porque deixava o rodapé do EULA em itálico (29/10/2026: continua só no branch `fix/kz-text`, fora do main)
- [ ] Persona 4: digitar o nome no começo do jogo exige o teclado na tela (IME); os testes automáticos só conseguem digitar "A" (29/10/2026: o `--ime-text` citado em `Ime.cs` e `CommonDialog.cs` não existe na linha de comando; `CommonDialog.ImeDefaultText` nunca recebe valor)
- [ ] `sceNpManagerGetCachedParam`: layout não confirmado, por isso responde "sem conta" mesmo com a conta PSN emulada conectada (o UnityNpToolkit do 99Vidas chama e segue em frente; 29/10/2026: `Np.cs` ainda devolve ERR_SIGNED_OUT)
