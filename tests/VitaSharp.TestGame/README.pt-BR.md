# VitaSharp test game

*[English](README.md)*

Um homebrew de PS Vita que exercita as partes do sistema que um emulador precisa acertar, mostra OK / FAIL de cada
verificação na tela e grava os resultados num arquivo legível por máquina. Foi feito para testar o emulador VitaSharp
automaticamente e roda do mesmo jeito num PS Vita de verdade com HENkaku / h-encore (instale o `.vpk` pelo
VitaShell).

É um homebrew comum, feito com o [VitaSDK](https://vitasdk.org) de código aberto: nada de SDK da Sony, cabeçalhos da
Sony ou código descompilado, nenhuma assinatura, licença ou DRM. O pacote é a saída normal do `vita-make-fself` /
`vita-pack-vpk` do VitaSDK (title ID `VSTEST001`, homebrew seguro).

## O que ele verifica

Ao abrir, o app roda todas as verificações uma vez (uns 30 s, mais até 20 s da janela de entrada) e depois mostra um
menu onde cada área pode ser rodada de novo, os valores da entrada podem ser vistos ao vivo e os resultados navegados.

| Área | Verificações |
| --- | --- |
| `gfx` | `sceGxm` direto, desenhado fora da tela e lido pela CPU depois do `sceGxmFinish`: clear, triângulos, strips, fans, linhas, interpolação de cor por vértice, culling CW/CCW; 28 casos de textura (U8U8U8U8 ABGR/ARGB/X8, U8U8U8, 565 RGB/BGR, 4444, 1555, 2-10-10-10, U8, U8U8, F16x4, paletas P8 e P4, UBC1/2/3, ETC1) nos layouts linear, linear com stride, swizzled e tiled; PVRTC 4 bpp (os quatro pesos de modulação entre as duas cores do bloco); YUV420 em dois e três planos, U ou V primeiro, pelo conversor CSC0 (BT.601 padrão); samplers (point, linear, repeat / mirror / clamp, mipmaps); blending alfa, aditivo e multiplicativo, máscaras de cor; depth less / greater / sem escrita; stencil replace + equal; render to texture; bordas com MSAA 4x |
| `input` | `sceCtrl` peek e modo de amostragem, analógicos em repouso; todos os botões, os dois analógicos, o toque da frente e de trás e uma inclinação vistos numa janela de 20 s (acaba antes quando tudo foi visto; START+SELECT para); info dos painéis de toque; estado do sensor de movimento e 1 g em repouso; uma tela com todos os valores ao vivo |
| `audio` | portas MAIN 48 kHz e BGM 44,1 kHz do `sceAudioOut` tocando um tom de 1 kHz e outro de 1,5 kHz ao mesmo tempo em duas threads, o período de bloco de cada uma (nunca mais rápido do que a porta toca; o quanto mais lento é informado, porque depende da carga da máquina), chamadas de volume e config, erro de taxa inválida, amostras restantes, porta VOICE mono |
| `video` | `sceAvPlayer`: ativo desde o init (antes da fonte), um filme H.264 de 4 s com quadros B e AAC na fila e sem leitura por 2 s (o decodificador corre na frente com a fila cheia), depois tocado até o fim: todas as imagens em ordem até a última, quadros de áudio, `sceAvPlayerIsActive` 0 no fim; uma segunda rodada pula o filme no meio (`sceAvPlayerStop` retorna, o player fica inativo logo depois, `sceAvPlayerClose` retorna); ritmo e pulos como a libsceavplayer 3.74: um `GetVideoData` por vblank recebe todas as imagens, com os timestamps só andando para frente (`paced_*`), quem chama atrasado recebe a próxima imagem a cada chamada, sem pular nenhuma (`late_one_picture_per_call`), e um laço que pula de volta para 1,5 s sempre que o `sceAvPlayerCurrentTime` passa de 3 s vê o tempo do pulo no `CurrentTime` logo depois do pulo, um segundo pulo na hora recusado com 0x806A0002, nada de antes de 1,5 s depois de um pulo e um pulo por volta, com as imagens animando (`jump_*`) |
| `files` | `sceIo` mkdir, gravação / leitura de 64 KB, `getstat` tamanho / tipo / data, `lseek` set / end / cur, append, `dopen` / `dread`, rename, remove, rmdir, erro de arquivo inexistente; leituras de `app0:` (e que ele é só leitura); gravação / leitura em `savedata0:`, criar slot de save / ler parâmetros; remoção de save como o apputil 3.74 (lista vazia ou item inválido — `sce_sys`, prefixo `savedata0:`, `..` — recusados sem apagar nada, itens válidos apagados com caminho inexistente aceito, o caminho vazio esvazia o `savedata0:` mas mantém a raiz, o `sce_sys` e os parâmetros dos slots); o diálogo de save do sistema |
| `threads` | criar / iniciar thread / status de saída, mutex (duas threads, try-lock, recursivo), mutex leve, semáforos (produtor / consumidor, poll, info, timeout), event flags (espera AND, poll OR + clear, set / clear), message pipe (32 mensagens em ordem, try-receive vazio; um pipe de tamanho 0 sem buffer: Try send sozinho não move nada, um send vai direto a um receptor esperando, um receive com poll e um bloqueante pegam em ordem os dados de um emissor esperando), variável de condição, `sceKernelDelayThread` de 1 / 10 / 100 ms (nunca volta antes do pedido; o atraso é informado, porque depende da carga da máquina), RTC contra o relógio do processo, tempo do processo monotônico |
| `system` | hora local do RTC, tick, resolução, anos bissextos, dias do mês, dia da semana, ida e volta do tick; parâmetros do sistema (idioma, botão de confirmar, formato de data / hora, fuso, horário de verão, tamanho do nome de usuário); bateria e clocks; os callbacks de energia (avisados na hora do registro só com os bits que um jogo pode ver, os erros do ScePower para um segundo registro, um UID que não é callback e um que não está registrado); taxa de atualização da tela e 30 vblanks (nos relógios do console: toda espera num vblank novo, alguma no seguinte (até 300 esperas com a máquina ocupada), o tempo do processo batendo com os vblanks vistos, lidos em pares sem preempção; quantas esperas atrasaram é informado); memória livre e modelo; módulo de rede, estado do `sceNetCtl` (sem tráfego); as syscalls do registro (`sceRegMgrGetRegVersion`, as de callback, e o reset / backup / restore `sceRegMgrResetRegistryLv`, `sceRegMgrDbBackup`, `sceRegMgrDbRestore`) recusadas a um jogo (0x80010058); o compilador de shaders do sistema (`sceSysmoduleLoadModule` 0x3E) recusado a um jogo (0xC05A1000); as chamadas do SceAppMgr só de programas do sistema recusadas ao app (`sceAppMgrSuspendUntilActivated`, `sceAppMgrEnableCoredumpForTest`); as syscalls do vídeo MagicGate e do Marlin (SceMgVideo, SceMarlinHci: abrir e liberar, lock e unlock do mutex, tipo de chave, objeto de chave, estado) recusadas a um jogo (0x80010058); diálogo de mensagem; diálogo do IME |
| `perf` | uma cena GXM pesada (uns 6000 triângulos girando com depth test, quatro camadas alfa de tela cheia, texto) com contador de fps / tempo de quadro: fps médio (OK a partir de 29), pior quadro, quadros acima de 34 ms. 5 s na rodada automática, onde são só medidos (SKIP com o valor: o fps de um emulador depende do host e da carga dele) e a checagem é a cena desenhar; 60 s pelo menu, que aprova ou reprova |

Não coberto (registrado como SKIP): texturas cube. Amostrar uma precisa de um fragment program com `samplerCUBE`, e
o jogo de teste só tem os programas pré-compilados do vita2d: o VitaSDK não tem compilador de shader, e os que existem
(o `psp2cgc` da Sony, o `libshacccg` do sistema) não podem ser redistribuídos, então nenhum programa de cube pode ir no
VPK. A amostragem de cube do renderizador é coberta pelos testes de renderizador da suíte. YUV pelo CSC1 também não
é checado: a conversão padrão dele (a libgxm 3.74 começa com BT.709 padrão) ainda é uma questão aberta do
renderizador. Um caminho com vitaGL ficou de fora: o caminho GXM direto já cobre o que o vitaGL desenharia, e o
compilador de shaders do vitaGL precisa de um módulo do sistema que o homebrew tem de extrair do console.

### Arquivo de resultados

`ux0:data/VSTEST/results.txt` é regravado depois de cada área. Uma linha por verificação, separada por tab:

```
área<TAB>verificação<TAB>OK|FAIL|SKIP<TAB>valor medido
```

Linhas começando com `#` são comentários; uma rodada completa termina com `# done ok=N fail=N skip=N`. Toda linha
também vai para o log do sistema (`sceClibPrintf`) com o prefixo `VSTEST`, então o log de um emulador também as mostra.
O nome do usuário nunca é gravado (só o tamanho dele).

SKIP quer dizer que a verificação não pôde ser decidida sem o usuário (itens da entrada quando a janela é parada com
START+SELECT, valores de bateria que um console sem bateria informa como 0, `app0:` gravável quando instalado como
inseguro) ou é uma medida que depende do host (o fps da rodada automática do `perf`).

### Convenções de layout que o teste assume

- Texturas swizzled: ordem de Morton com y no bit 0 e x no bit 1 (formatos comprimidos em blocos fazem o swizzle de
  blocos 4x4 inteiros).
- Texturas tiled: tiles de 32x32 texels guardados linha a linha, texels dentro do tile linha a linha.
- Texturas P4: o texel par no nibble de baixo.

Isso segue como o Vita3K decodifica esses formatos; uma rodada num console de verdade confirma ou desmente.

## Build

O build precisa do VitaSDK (`arm-vita-eabi-gcc`, `vita-elf-create`, `vita-make-fself`, `vita-mksfoex`,
`vita-pack-vpk`), `make`, `git`, `curl` e `bash`. Não precisa de CMake. A saída é `build/VitaSharpTest.vpk`.

### Linux

```sh
sudo apt install git curl make python3     # Debian / Ubuntu (Fedora: dnf, Arch: pacman)
./build.sh --install-sdk                   # primeira vez: instala o VitaSDK em ~/vitasdk com o bootstrap oficial do vdpm
./build.sh                                 # builds seguintes
```

### macOS

```sh
xcode-select --install                     # git, make, curl
./build.sh --install-sdk
./build.sh
```

### Windows (WSL)

O VitaSDK roda dentro do WSL 2 (Debian ou Ubuntu; `wsl --install -d Debian`). Dentro da distribuição:
`sudo apt install git curl make python3`. Depois, no PowerShell, nesta pasta (`tests/VitaSharp.TestGame`):

```powershell
.\build.ps1 -InstallSdk                    # primeira vez: instala o VitaSDK em ~/vitasdk dentro do WSL
.\build.ps1                                # builds seguintes (-Clean recompila tudo, -Distro escolhe outra distribuição)
```

O `build.ps1` roda o `build.sh` dentro do WSL. Defina `VITASDK` para usar um VitaSDK instalado em outro lugar.

## Rodar

- **PS Vita de verdade** (HENkaku / h-encore, em qualquer firmware que eles suportem): copie
  `build/VitaSharpTest.vpk` para o console, instale pelo VitaShell e abra o *VitaSharp Test*. Resultados:
  `ux0:data/VSTEST/results.txt`.
- **VitaSharp**: `VitaSharp.Desktop run build/VitaSharpTest.vpk` instala e abre numa janela. Para uma rodada
  automática sem janela e sem mexer na sua pasta de dados:

  ```sh
  VitaSharp.Desktop run build/VitaSharpTest.vpk --reinstall --data=<pasta de teste> --vulkan --seconds=75 \
      --dialogs=auto --ime-text=VitaSharp --dump-frames=<pasta dos quadros> --dump-every=120 --log=<arquivo de log> \
      --input=cross@f60:10,circle@f75:10,square@f90:10,triangle@f105:10,up@f120:10,down@f135:10,left@f150:10,right@f165:10,ltrigger@f180:10,rtrigger@f195:10,start@f210:10,select@f225:10,lstick@f240:1:0:15,rstick@f260:0:-1:15,tap@f280:0.5:0.5:15,btap@f300:0.3:0.6:15,tilt@f320:40:20:60
  ```

  O `--vulkan` é necessário sem janela (sem renderizador nada é desenhado e as verificações com leitura de pixels
  falham). O arquivo de resultados fica em `<pasta de teste>/fs/ux0/data/VSTEST/results.txt` e o log tem todas as
  linhas `VSTEST`. A janela de entrada abre poucos quadros depois dos diálogos automáticos (o log diz em que quadro:
  `VSTEST input window opens at frame N`), então os passos acima, contados em quadros apresentados, caem dentro dela.
  O firmware é opcional: com as pastas `os0`, `vs0` e `sa0` de um firmware instalado na pasta de teste (junctions
  ou cópias) a rodada é a mesma.
- **Sair depois da rodada**: com um arquivo `ux0:data/VSTEST/exit_after_run` (qualquer conteúdo; no VitaSharp
  `<pasta de teste>/fs/ux0/data/VSTEST/exit_after_run`) o app chama `sceKernelExitProcess(0)` logo depois da rodada
  automática, como o "Sair" de um jogo. Um Vita de verdade volta ao LiveArea; o VitaSharp sem o shell encerra a
  sessão: a janela fecha, uma rodada headless termina bem antes do `--seconds` e o log tem `VSTEST exit after the
  run` seguido de `the game exited (0x0)`. Uma rodada que segue até o `--seconds` é a regressão.

## Organização da pasta

```
src/                  o app (C11, VitaSDK)
  main.c              boot, rodada automática, menu
  app.c, app.h        controle, quadros de progresso, listas de resultado, laço dos diálogos, entradas dos testes
  gfx.c, gfx.h        renderizador sceGxm direto: tela, shader patcher, alvos fora da tela + leitura, retângulos, texto
  results.c, .h       tabela de resultados, linhas de log, results.txt
  test_*.c            um arquivo por área (graphics, input, audio, video, files, threads, system, perf)
  savedata_dialog.h   declarações do sceSaveDataDialog (o VitaSDK tem os stubs, mas não o cabeçalho)
  shaders.S           embute os shaders pré-compilados
stubs/firmware_stubs.yml  stubs de importação por NID das bibliotecas do firmware que o VitaSDK não tem (o
                      Makefile gera com o vita-libs-gen em build/stubs)
third_party/
  vita2d-shaders/     shaders GXP pré-compilados (+ fontes Cg) do libvita2d, MIT
  font8x8/            fonte bitmap 8x8, domínio público
assets/icon0.png      ícone da LiveArea (gerado, 128x128 indexado)
assets/vstest.mp4     o filme da área video (make_movie.sh / make_movie.ps1, FFmpeg com libx264)
Makefile              regras de build (sem CMake)
build.sh, build.ps1   scripts de build (Linux / macOS; Windows pelo WSL)
```

## Dependências e licenças

| Dependência | Usada para | Licença |
| --- | --- | --- |
| Toolchain do [VitaSDK](https://github.com/vitasdk), newlib e bibliotecas de stubs | compilador, biblioteca C, stubs das chamadas do sistema, ferramentas de empacotamento | MIT / GPL (ferramentas) / licenças da newlib, como cada pacote do VitaSDK informa; nada disso é copiado para este repositório |
| Shaders do [libvita2d](https://github.com/xerpi/libvita2d) (`third_party/vita2d-shaders`) | os quatro pares de shaders GXP pré-compilados (clear, cor, textura, textura com tinta) | MIT (`third_party/vita2d-shaders/LICENSE`) |
| [font8x8](https://github.com/dhepper/font8x8) (`third_party/font8x8`) | o texto na tela | domínio público |

Os binários dos shaders foram extraídos dos objetos pré-compilados do libvita2d (`libvita2d/shader/compiled/*_gxp.o`);
as fontes Cg estão ao lado deles.

## Licença

Faz parte do repositório do VitaSharp, sob a licença dele (GNU GPL v3, veja o `LICENSE` na raiz do repositório).
Os arquivos de terceiros mantêm as próprias licenças listadas acima.
