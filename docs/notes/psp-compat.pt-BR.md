<!-- pair: psp-compat.md sha256=2c4da33b23ac1c5a5f9db4adc87f19798a83dfc2e466a7b6c7d9c33468c56a25 -->
# Núcleo de compatibilidade PSP (SceCompat, ScePspemu, Allegrex)

*English: [psp-compat.md](psp-compat.md).*

Nota de estudo e desenho para rodar títulos de PSP e PSone pelo emulador de PSP do próprio Vita (`vs0:app/NPXS10028`,
ScePspemu) em cima do núcleo de compatibilidade de verdade, como num Vita real.
Código de produto até agora: o interpretador do Allegrex (abaixo). Fontes: o
`SceCompat` do FW 3.74 (bootimage), o `NPXS10028/eboot.bin`, o `pcbc.skprx` e o `pcff.skprx` descompilados ou
inspecionados com as ferramentas locais de RE (`%LOCALAPPDATA%\VitaSharpTools\fw374`, só estudo, nada copiado), o
código do Adrenaline (GPL-3), as notas do VitaMips (Hykem) e o HLE atual (`src/VitaSharp.Core/Hle/Modules/Compat.cs`).
As páginas da psdevwiki recusam acesso automático; leia à mão quando um ponto abaixo diz "a confirmar".

## Como o Vita faz

O SoC do Vita tem um núcleo MIPS compatível com o Allegrex do PSP. Ele roda o código do PSP nativamente: um código de
boot pequeno (`pcbc`) e um kernel de PSP 6.6x (`pcff`, uma imagem do flash0 cujos drivers são os módulos `kermit_*`).
Tudo o que o PSP faria pelo próprio hardware (Memory Stick, flash, áudio, os codecs do Media Engine, Wi-Fi, USB,
energia, OSK, diálogos) os drivers kermit mandam como **pedidos** para o lado ARM, onde o ScePspemu (código ARM de
usuário, que já roda em LLE no VitaSharp) atende com as APIs do Vita. O `SceCompat` (kernel ARM) é a ponte: é dono da
memória que o núcleo MIPS enxerga, carrega o firmware, liga e desliga o núcleo e passa pedidos, respostas e
interrupções por uma SRAM compartilhada e uma caixa de correio (mailbox). Títulos de PSone seguem o mesmo caminho (o
emulador `pops` do lado PSP, com ajuda do ScePspemu na tela e no áudio); o PocketStation é emulado no lado ARM pelo
ScePspemu.

Provas de que o núcleo de compatibilidade é MIPS (a nota do roadmap de 10/11 dizia "um núcleo ARM", o que está
errado):

- O Adrenaline, depois que o núcleo avisa que subiu, troca a palavra no endereço de usuário `0x70602D70` (janela +
  `0x602D70`, dentro da área onde o `pcbc` é carregado) por `0x0E3F0000`, a codificação MIPS de `jal 0x88FC0000`.
- O VitaMips põe o próprio código de boot em `0x88600000` do PSP (janela + `0x600000`, o lugar do `pcbc`) e o inicia
  com `sceCompatStart`.
- O ScePspemu não tem threads de emulação de GE nem de CPU; as threads dele são os servidores "Remote"
  (`ScePspemuRemoteMsfs`, `...Flashfs`, `...Me`, `...AudioOut...`, `...Net`, `...Usb`, `...Osk`, `...Dmacplus`) dos
  pedidos kermit.

## 1. O que o SceCompat 3.74 faz

### Memória

| Item | Valor | Observações |
|---|---|---|
| Janela (`sceCompatAllocCdramWithHole`) | 64 MB, vistos pelo PSP em `0x88000000` | três blocos de 32/16/16 MB, cada um alocado cached, uncached e como CDRAM do kernel; o do meio fica sem mapeamento (o "buraco", PSP `0x8A000000`-`0x8B000000`); o último tem de ficar em base + `0x3000000` (senão `0x80010078`) |
| Janelas de endereço do Allegrex | `SceGrabForDriver_E9C25A28(i, paddr)` para i = 0..3, com o bit 0 de paddr ligado | 0: base, 1 e 2: base + `0x1000000`, 3: base + `0x3000000` (o Adrenaline reescreve a janela 2 para dar ao PSP os 16 MB extras do buraco) |
| Código de boot `pcbc` | CDRAM + `0x600000` (PSP `0x88600000`), até `0xA00000` | `os0:kd/pcbc.skprx`, `SceSblPcbcBin`, `0x5CE0` bytes |
| Pacote do flash0 `pcff` | CDRAM + `0x3000000` (PSP `0x8B000000`), até `0x2000000` | `vs0:app/NPXS10028/pcff.skprx`, `SceSblPcffBin`, `0x920000` bytes: uma tabela {nome, buffer, tamanho} com ponteiros do PSP, depois os arquivos |
| Semente aleatória | CDRAM + `0x3FFF00` (PSP `0x883FFF00`), 4 bytes | de `SceSblSsMgrForDriver_4F9BFBE5` (número aleatório) quando a janela é alocada |
| Palavra do aperto de mão de boot/suspensão | CDRAM + `0x3D78000` (PSP `0x8BD78000`) | estados 1 (núcleo pronto), 2 (ARM confirmou), 3 (núcleo rodando), 0 (parado) |
| Área de depuração | CDRAM + `0x3D80000`, `0x40000` | mapeada para o Deci2p no start (unidades de desenvolvimento) |
| Scratchpad / VRAM do PSP | PSP `0x8BD00000` / `0x8BE00000` | nomes do Adrenaline para o topo da janela 3 |
| `SceCompatMailbox` | físico ARM `0xE5070000`, `0x1000` | campainhas: escrever `0xFFFFFFFF` em `+0x80 + n*4` e depois em `+n*4` sinaliza a linha n ao núcleo e a confirma |
| `SceCompatSharedSram` | físico ARM `0xE8100000`, `0x1000`; PSP `0xBFC00000` | o bloco compartilhado abaixo |
| `SceCompatLCDDMA` | físico ARM `0xE5071000`, `0x1000` | o DMA do LCD que copia o framebuffer do PSP (480x272, passo de `0x800` bytes) para o bloco de framebuffer do Vita (`0x88000` bytes) |
| Bloco "dat" | `0x1038` bytes no SceCompat | preenchido no start por `SceNpDrmForDriver_D91C3BCE`; `0x1038` é o tamanho do `act.dat` (a ativação que o npdrm do lado PSP lê por `sceCompatDatRead`) |

Deslocamentos na SRAM compartilhada (visão ARM `+x` = PSP `0xBFC00000 + x`):

| Deslocamento | Uso |
|---|---|
| `0x100`, `0x104`, `0x108`, `0x170`..`0x188`, `0x10C`..`0x114` | registradores compartilhados de 32 bits: `sceCompatWriteShared32(0x80..0x88)` escreve `0x100/0x104/0x108/0x170/0x174/0x178/0x17C/0x180/0x184`; `sceCompatReadShared32(0..7, exceto 3)` lê `0x100/0x170/0x17C/-/0x10C/0x110/0x114/0x188` |
| `0x120`..`0x128`, `0x140` | `sceCompatWriteSharedCtrl`: as três palavras do controle, depois a primeira de novo em `0x140` (escrita por último, a marca de "dado novo") |
| `0x800 + i*8` | entradas de comando que o núcleo posta (IRQ `0x44+i`): palavra 0, 16 bits altos = canal (< 16), palavra 1 = o endereço do pedido no PSP |
| `0x804 + k*8` | a entrada seguinte, relida quando a fila de um canal estourou |
| `0x840 + c*0x20` | respostas (`sceCompatReturnValueEx(c < 3, value[2])`): o valor, as palavras de semáforo/resposta do pedido, depois a campainha `c + 7` |
| `0x8C0 + n*8` | interrupções virtuais (`sceCompatInterrupt(n = 1..15)`): o SceCompat espera até 1 s o lugar ficar 0, escreve 1, toca a campainha `n + 0x10` |

### Interrupções e threads (`module_start`)

- IRQ `0x44`-`0x46` ("SceCompat0c".."2c"): o núcleo postou um pedido. O tratador lê a entrada da SRAM compartilhada,
  põe o endereço do pedido no anel do canal (16 canais x 16 entradas; anel cheio: marca de estouro) e confirma a
  campainha. Dezesseis semáforos "SceCompatHandler%d"/"SceCompatVirtualHandler" acordam quem espera em cada canal.
- IRQ `0x47`-`0x49` ("SceCompat0r".."2r"): o núcleo respondeu; sinalizam os semáforos em que `sceCompatWaitIntr(0..2)`
  espera.
- IRQ `0x4A` ("SceCompatLcdc"): o vsync do controlador do LCD; `sceCompatLCDCSync` espera por ele (pela linha de
  varredura da tela: linhas `0x1E6`/`0x226`/`1000`/`0x28A` conforme a saída).
- Um tratador de suspensão (`SceKernelSuspendForDriver_04C05D10`) e um de eventos de processo (o fim do processo
  para o núcleo).

### Exports (SceCompat; toda entrada confere que quem chama é programa do sistema: `0x80010058`)

| Export | O que faz |
|---|---|
| `sceCompatAllocCdramWithHole(out[2])` | aloca a janela acima (uma vez: `0x80010078`), guarda a thread dona (os outros: `0x8002F001`) |
| `sceCompatCache(op 1/2/3, addr, size)` | invalida / limpa / limpa+invalida o cache na janela, linhas de 64 bytes |
| `sceCompatInitEx(bootParam)` | limpa os anéis de pedidos; solta as janelas do Grab e as mapeia de novo; o `SceSblFwLoader` carrega o `pcff` em CDRAM + `0x3000000` e o `pcbc` em CDRAM + `0x600000` (decifrados pelo processador seguro), grava o cache; escreve a semente; liga o clock do núcleo (`ScePowerForDriver_FFC84E69(0x14D)`, 333 MHz); `SceGrabForDriver_072B8D93(bootParam)` pega o núcleo; comando seguro `0x10006` (abaixo) |
| `sceCompatStart()` | mapeia o Deci2p; preenche o bloco "dat" (ativação; o erro dela volta, salvo em unidade DEX); confere o rif de `sceCompatSetRif` (`SceNpDrmForDriver_DACB71F4`, numa pilha do kernel), inválido e não DEX: `0x8001000D`; `SceGrabForDriver_379130B2` tira o núcleo do reset; programa o DMA do LCD quando há bloco de framebuffer; estado 2 (rodando). Se falha, roda o caminho de parada |
| `sceCompatWaitSpecialRequest(1)` | aperto de mão de boot: lê a palavra em `0x8BD78000` até ser 1 (5 s, senão `0x80028005`); comando seguro `0x30006`; desmascara as campainhas `0x40`-`0x5F` exceto `0x41` e `0x4E`; escreve 2 e espera 3 (5 s), depois 0. `2`/`3`: apertos de mão de suspensão/retomada (30 s) |
| `sceCompatWaitAndGetRequest(canal < 16, tipo < 3)` | espera o semáforo do canal e tira do anel; devolve o endereço do pedido (>> 1; os 26 bits baixos localizam o pedido de 0x80 bytes na janela, invalidado antes de voltar); vazio: `0x80010016` |
| `sceCompatReturnValueEx(c < 3, value[2])` | escreve a resposta (SRAM compartilhada `0x840 + c*0x20`) e toca a campainha `c + 7`; núcleo não iniciado: `0x80027205` |
| `sceCompatInterrupt(1..15)` | interrupção virtual para o núcleo (lugar `0x8C0`, campainha `n + 0x10`); sem núcleo: `0x80010086` |
| `sceCompatWaitIntr(0..2)` | espera uma interrupção de resposta (IRQ `0x47`-`0x49`) |
| `sceCompatRead/WriteShared32`, `sceCompatWriteSharedCtrl` | os registradores compartilhados acima |
| `sceCompatStop`, `sceCompatUninit`, `sceCompatGetStatus` | parar (trava de 5 s, `SceGrabForDriver_F13C63DD`, clock 0, estado 1); soltar o framebuffer; estado 0/1/2 |
| `sceCompatSuspendResume(1)` | retoma um núcleo suspenso: Grab, clock, comando seguro `0x10006` com o token guardado, start, aperto de mão 1-2-3; nada para retomar: `0x8001008D` |
| `sceCompatFrameBufferInit`, `sceCompatSetDisplayConfig`, `sceCompatLCDCSync`, `sceCompatGetPrimaryHead`, `sceCompatAvailableColorSpaceSetting` | a tela: o bloco do framebuffer, `ksceDisplaySetFrameBuf` 480x272 passo 512 (wide: `0x100000`), o vsync do PSP |
| `sceCompatDatRead`, `sceCompatSetRif`, `sceCompatIdStorageLookup`, `sceCompatGetDevInf`, `sceCompatGetCurrentSecureTick`, `sceCompatGet/SetUpdateState`, `sceCompatGetPeripheralState`, `sceCompatGetPspSystemSoftwareVersion` (`0x295`), `sceCompatGetTitleList`, `sceCompatSetSuspendSema`, `sceCompatCheckPocketStation`, `sceCompatIsPocketStationTitle` | serviços que não dependem do núcleo (o `Compat.cs` já segue) |

### Processador seguro (`os0:sm/compat_sm.self`)

O SceCompat carrega o `compat_sm` para cada comando (`SceSblSmCommForKernel_7863A0CC`, com um parâmetro de `0x90`
bytes do módulo), manda um comando e o para de novo:

- `0x10006` (argumento: 0 no InitEx, o token de retomada na retomada): logo depois de pegar o núcleo. Nome
  desconhecido (a confirmar na psdevwiki); pela posição, prepara a memória protegida do núcleo para o boot.
- `0x30006`: o Adrenaline chama essa função de `sceCompatSecSetSSRAMAcl`: define o controle de acesso da SRAM
  compartilhada quando o núcleo avisa "pronto" (estado 1 do aperto de mão), antes de o lado ARM responder.

Nenhum dos dois devolve dados ao SceCompat além de uma palavra de status; no VitaSharp os dois viram "status 0" na
emulação do serviço do SM, sem criptografia.

### Protocolo com o ScePspemu

O ScePspemu cria uma thread por canal kermit e repete `sceCompatWaitAndGetRequest(modo)` → converte o endereço →
atende → `sceCompatReturnValueEx`. Um pedido tem 0x80 bytes: `cmd`, o id do semáforo do PSP, o endereço no PSP da
resposta de 64 bits, depois 14 argumentos de 64 bits. Canais (`KermitModes` do Adrenaline): 3 msfs (Memory Stick),
4 flashfs, 5 saída de áudio, 6 ME (os codecs do Media Engine rodam no lado ARM), 7 lowio, 8 PocketStation / USB
PSPCM, 9 periféricos, 10 WLAN, 11 entrada de áudio, 12 USB, 13 utilitários, 14-15 extras (o Adrenaline usa o 15).
Interrupções virtuais para o núcleo (`sceCompatInterrupt`): 1-3 áudio, 4-6 DMA do ME, 7-8 WLAN, 9 impose, 10 energia,
12 GPS USB, 13 USB PSPCM. Controle: o ScePspemu escreve o pad a cada quadro com `sceCompatWriteSharedCtrl`. Tela: o
PSP desenha na VRAM dele, o DMA do LCD copia o framebuffer para o `ScePspemuFrameBuffer` e o ScePspemu mostra com
`sceDisplaySetFrameBufForCompat`/`sceCompatSetDisplayConfig`. Áudio: o `kermit_audio` posta PCM no canal 5 e o
ScePspemu toca com `sceAudioOut`.

### Ordem de boot vista no VitaSharp

O ScePspemu de verdade já roda até `sceCompatInitEx(1)` (log: `SceCompat: InitEx(1): the PSP compat core is not
emulated`): escolheu 444 MHz, criou as threads, iniciou o GXM, pegou a janela, limpou as duas metades
(`sceCompatCache`), registrou os semáforos de suspensão e o framebuffer. Depois do InitEx ele chama `sceCompatStart`,
depois `sceCompatWaitSpecialRequest(1)`, e inicia as threads de pedidos.

## 2. O que o lado Allegrex precisa

- **CPU:** a ISA do Allegrex: núcleo inteiro MIPS32 da família R4000 sem as instruções de 64 bits, mais os acréscimos
  do Allegrex (`clz/clo`, `min/max`, `madd/msub(u)`, `ext/ins`, `seb/seh`, `wsbh/wsbw`, `bitrev`, `movz/movn`, `rotr`,
  `halt`, `mfic/mtic`), a FPU de precisão simples (COP1), o COP0 (status, cause, EPC, exceções, interrupções,
  instruções de cache) e a **VFPU** (COP2: 128 registradores como matrizes 4x4, prefixos, as instruções
  transcendentais e de conversão; resultado exato bit a bit importa para os jogos). Little endian, endereçamento
  físico de 32 bits por segmentos fixos (espelhos `0x0`/`0x4`/`0x8`/`0xA`), sem TLB.
- **Media Engine:** não precisa. O firmware kermit manda o trabalho do ME (`kermit_me_wrapper`) para o ScePspemu no
  lado ARM.
- **GE (gráficos):** hardware de compatibilidade do SoC, programado pelo próprio `ge.prx` do PSP; veja "O GE" abaixo.
  O VitaSharp precisa de uma implementação do GE (listas de exibição, decodificação de vértices, rasterização em
  Vulkan, VRAM na janela + `0x3E00000`). É a maior parte do trabalho.
- **Outro hardware do lado PSP que o kernel kermit toca:** controlador de interrupções, timer do sistema, controlador
  de DMA (`dmacman`), a mailbox e a SRAM compartilhada acima, o controlador do LCD, controle de cache; e o serviço de
  criptografia KIRK (o memlmd decifra cada módulo `~PSP` do flash0 e dos jogos). O conjunto exato de registradores
  sai do trace do boot (um log de "MMIO desconhecido" na fase 3 abaixo), não de palpite.
- **O que roda nele:** o `pcbc` (o código de boot em `0x88600000`), que inicia o kernel do PSP a partir do pacote
  `pcff` em `0x8B000000` (`loadcore`, `sysmem`, os drivers `kermit_*`, `init`, depois a VSH ou o jogo pelo
  `loadexec`). Com o Adrenaline, os patches dele (rodando como código de terceiro, sem mudança) trocam pelo flash0
  6.61 que o instalador dele extraiu do `661.PBP` (do lado PSP, com o `libpsardumper`) em `ux0:app/PSPEMUCFW/flash0`,
  e acrescentam `systemctrl`/`inferno`/`popcorn`.

### O interpretador do Allegrex no VitaSharp

`src/VitaSharp.Core/Compat/Allegrex` (`AllegrexCore` sobre um `IAllegrexBus` que recebe endereços físicos do PSP,
`endereço & 0x1FFFFFFF`): a ISA inteira com os acréscimos do Allegrex, delay slots e branch-likely, as exceções por
EBase (`0xBFC00200` com Status.BEV ligado), o COP0 (Status, Cause, EPC, ErrorEPC, EBase, BadVAddr, Count, o código do
syscall, o id da CPU, os registradores de controle de cfc0/ctc0), as linhas de interrupção Cause.IP0..IP7 (o
controlador de interrupções do PSP é a IP2) tomadas com Status.IE ou `mtic`, o halt, e syscall/break oferecidos
primeiro ao host. Os resultados que o MIPS32 deixa imprevisíveis seguem as notas do PPSSPP conferidas no hardware
(pspautotests): divisão por zero, `0x80000000 / -1`, a decodificação parcial de rotr/rotrv e do grupo
seb/seh/wsbh/wsbw/bitrev, `ins` com msb < pos, o estado de reset. Testes: `tests/VitaSharp.Tests/Allegrex*Tests.cs`.

Três pontos não têm teste de hardware conhecido; o VitaSharp segue a arquitetura MIPS32, que o Allegrex implementa:

- `add`, `addi` e `sub` disparam a exceção de overflow (o PPSSPP e o Jpcsp tratam como `addu`/`subu`).
- `di`/`ei` (`mfmc0`) funcionam como no MIPS32 release 2: rt = Status, depois Status.IE = o bit sc da instrução.
- Uma interrupção pode ser tomada com um delay slot a seguir: EPC = o branch, Cause.BD ligado (o PPSSPP só confere
  fora do slot).

### O GE (motor gráfico)

Como o FW 3.74 o atende (fontes: as strings e importações do `NPXS10028/eboot.bin` e o decompilado do `SceCompat`,
só estudo; o `include/common/hardware.h` do uofw para os registradores do GE do PSP; o `adrenaline_compat.h` do
Adrenaline):

- **Ninguém no lado ARM executa listas de exibição.** O ScePspemu não tem thread de GE nem servidor de pedidos do GE:
  os servidores "Remote" dele são saída/entrada de áudio, ME (codecs, Atrac, Sas2), Msfs, Flashfs, Net, Usb, Osk,
  Peripheral, Pocs, Pspcm, entrada AVC e Dmacplus. O pcff traz o `ge.prx`/`ge_2.prx` do próprio PSP, que programam os
  registradores do GE em `0xBD400000` diretamente (não há driver kermit do GE); então as listas de exibição são
  executadas por hardware de GE no bloco de compatibilidade do SoC, como num PSP.
- **Registradores** (uofw): `0xBD400000` reset, `+0x008` o tamanho da EDRAM (em KB, 16 bits baixos), `+0x100` EXEC (o
  bit 0 roda a lista e lê 1 enquanto ela roda; bit 1 a condição de desvio; bits 8/9 a profundidade de CALL), `+0x108` o
  endereço da lista, `+0x10C` o endereço de parada (0: nenhum; o GE espera enquanto o endereço da lista for igual a
  ele), `+0x110`/`+0x114` os endereços de retorno do CALL, `+0x118`/`+0x11C` os endereços de vértices/índices,
  `+0x120`..`+0x128` a origem e as duas cópias salvas, `+0x200` o relógio de geometria, `+0x304` o estado das
  interrupções (1 SIGNAL, 2 END, 4 FINISH, 8 erro), `+0x308` as interrupções aceitas, `+0x30C`/`+0x310` ligar/confirmar,
  `+0x400` o tamanho de EDRAM em uso, `+0x800 + cmd * 4` o último valor de cada comando, `+0xC00`.. as matrizes de
  ossos, mundo, visão, projeção e texgen; o controlador da EDRAM em `0xBD500000`. O `ge.prx` espera o bit 0 do EXEC
  zerar depois de SIGNAL+END e FINISH+END e lê as duas palavras antes do endereço da lista para saber que comando o
  parou.
- **Memória em que desenha:** a VRAM do PSP (físico `0x04000000`, 2 MB), que no Vita é o topo da janela 3: janela +
  `0x3E00000` (PSP `0x8BE00000`, o `SCE_PSPEMU_VRAM` do Adrenaline); o scratchpad é a janela + `0x3D00000`.
- **Para onde vai a imagem:** o driver de display do PSP aponta o controlador do LCD para um framebuffer na VRAM; o
  `SceCompatLCDDMA` (ARM `0xE5071000`, programado pelo `sceCompatStart`: 480x272, pitch de destino `0x800`, o bloco de
  framebuffer de `0x88000` bytes do `sceCompatFrameBufferInit`) o copia para o `ScePspemuFrameBuffer`, e o ScePspemu
  mostra esse bloco com `sceCompatSetDisplayConfig` / `sceDisplaySetFrameBufForCompat`. O ScePspemu usa o GXM só para
  o próprio desenho (diálogos, PocketStation, escala), não para o do PSP.

No VitaSharp o GE é nosso: `src/VitaSharp.Core/Compat/Ge` executa as listas (o bloco de registradores acima, o estado
dos comandos, a decodificação de vértices) e entrega cada lote de primitivas a um rasterizador; o de Vulkan desenha no
framebuffer do PSP na memória do PSP e escreve os pixels de volta, separado do renderizador do GXM.

### O que exige chaves e o que não exige

| Item | Quem decifra num Vita | VitaSharp |
|---|---|---|
| Camada SELF do `pcff.skprx` | o processador seguro (SceSblFwLoader) | a decifração de SELF que já existe (`self2elf`) já dá o pacote em claro (nomes e ponteiros do PSP legíveis): nenhuma chave nova |
| Camada interna do `pcbc.skprx` | o processador seguro (SceSblFwLoader / `compat_sm`) | depois do `self2elf` os `0x5CE0` bytes continuam cifrados (7,99 bits/byte). Chaves que o projeto não tem e não vai procurar nem derivar (decisão do lead, 11/11/2026). Único caminho: um `pcbc.bin` tirado pelo usuário do próprio console (CDRAM + `0x600000` depois do InitEx), nunca no repositório |
| Comandos `0x10006`/`0x30006` do `compat_sm` | o processador seguro | nada a decifrar: emulados como status 0 |
| Módulos `~PSP` dentro do flash0 e EBOOT/PRX dos jogos | o kernel do PSP (memlmd) pelo KIRK | o KIRK tem de ser emulado. Onde o KIRK fica no Vita (hardware de compatibilidade ou `compat_sm`) está a confirmar. As chaves do KIRK são chaves do PSP publicadas em emuladores de código aberto (libkirk do PPSSPP, Jpcsp): usá-las é **decisão do lead** (não tomada aqui; nenhuma chave está escrita nesta nota) |
| `661.PBP` (Adrenaline) | o lado PSP (PSAR pelo KIRK) | vem com o KIRK |
| rif / act.dat / NPDRM | NpDrm do Vita (HLE) + npdrm do PSP (KIRK) | HLE existente do lado Vita; lado PSP vem com o KIRK |

## 3. Referências

| Referência | O que estudar | Licença | Reuso |
|---|---|---|---|
| PPSSPP | interpretador e IR JIT do Allegrex, VFPU (prefixos, tabelas de matemática exata), GE (listas de exibição, decodificador de vértices, backend Vulkan), KIRK (`ext/libkirk`) | GPL-2.0 ou posterior | compatível com a GPL-3 do VitaSharp; pela regra do projeto estudamos e escrevemos nosso C#; tabelas de opcodes e constantes da VFPU podem ser portadas com atribuição se o lead decidir |
| Jpcsp | o único LLE aberto do hardware do PSP: boot pre-IPL/IPL, módulos reais do kernel, controlador de interrupções, timers, DMAC, KIRK como hardware | GPL-3.0 | como acima; a melhor referência para a fase 3 |
| Adrenaline (TheOfficialFloW, e o fork "Adrenaline 8" que o usuário usa) | layout dos pedidos/interrupções kermit, pacote do flash0, layout da janela/Grab, internos do ScePspemu | GPL-3.0 | estudo; roda sem mudança no VitaSharp como código de terceiro |
| VitaMips (Hykem) | código de boot próprio em `0x88600000` iniciado com `sceCompatStart`, KIRK emulado em software | a conferir | desenho do código de boot sintético dos testes |
| psdevwiki (páginas SceCompat, compat_sm, Kermit, ScePspemu) e vita-headers | nomes dos comandos seguros, Grab, mailbox | wiki / MIT | só fatos |
| pspautotests | testes de comportamento de CPU/VFPU/GE | a conferir | ideias para os nossos testes sintéticos (nenhum binário de PSP na suíte) |

## 4. Plano

Fases (tamanhos como no roadmap: `[M]` horas, `[G]` um dia ou mais):

1. **Primeira entrega mínima testável [G, ~2-3 dias].** Um interpretador inteiro do Allegrex (sem FPU/VFPU) numa
   thread de guest própria; a visão de endereços do PSP sobre a janela do SceCompat (as quatro janelas do Grab, o
   buraco); a SRAM compartilhada, as campainhas da mailbox e o caminho das IRQ `0x44`-`0x4A` no SceCompat, escritos
   como o módulo faz; `sceCompatInitEx` carrega o código de boot pelo HLE do SceSblFwLoader (um fixture nos testes);
   `sceCompatStart` o executa. Teste: um código de boot MIPS sintético montado no teste (bytes nossos, sem firmware)
   que põe 1 na palavra do aperto de mão, espera 2, escreve 3, posta um pedido no canal 3, espera a resposta e trata
   uma interrupção virtual. Confere: `sceCompatWaitSpecialRequest(1)` = 0, `sceCompatWaitAndGetRequest(3, 0)` devolve
   o pedido, o núcleo vê o valor de `sceCompatReturnValueEx` e as palavras de `sceCompatWriteSharedCtrl`,
   `sceCompatInterrupt(9)` chega ao tratador, `sceCompatStop` para. Falha hoje (o InitEx responde `0x80027205`).
2. **Allegrex completo [G, 1-2 semanas].** FPU, VFPU, exceções/interrupções do COP0, instruções de cache, contagem de
   ciclos para o clock do núcleo; testes de ISA por grupo de instruções, VFPU conferida contra valores calculados à mão
   pela semântica documentada.
3. **Hardware do lado PSP [G, 2-4 semanas, o GE domina].** Boot do `pcbc` (o `pcbc.bin` do usuário) + `pcff` com log
   de "MMIO desconhecido"; implementar o que ele toca: controlador de interrupções, timer, DMAC, DMA do LCD para o
   framebuffer do ScePspemu, KIRK (depois da decisão do lead sobre as chaves), GE em Vulkan. Meta: o núcleo chega ao
   estado 1 do aperto de mão e o ScePspemu de verdade atende os primeiros pedidos msfs/flashfs.
4. **ScePspemu de ponta a ponta [G, ~1 semana].** Seguir os logs: as lacunas de HLE que o ScePspemu encontra (áudio,
   codecs do ME, msfs, energia, tela), um homebrew de PSP, depois um jogo de PSP e um de PSone; suspensão/retomada
   (`0x10006` com token, estados 2/3) e save states do núcleo.
5. **Adrenaline 6.61 [M-G].** Os hooks do próprio Adrenaline e o flash0 dele (nenhum código para o Adrenaline no
   VitaSharp); o patch dele no `sceCompatSecSetSSRAMAcl` tem de achar o `pcbc` decifrado em `0x88602D70`.
6. **Desempenho [G].** JIT do Allegrex (IR JIT do PPSSPP, Dolphin e o JIT ARM existente como referências), lotes do
   GE; medido em gameplay.

Riscos e bloqueios:

- **Cifra interna do `pcbc`** bloqueia da fase 3 em diante com o código de boot real até o usuário fornecer o
  `pcbc.bin`. As fases 1-2 não precisam dele.
- **Chaves do KIRK:** sem a decisão do lead nenhum módulo `~PSP` carrega (fase 3).
- **Hardware do GE** no Vita não é documentado; o GE do PSP (PPSSPP/Jpcsp) é o modelo, com o risco de o hardware de
  compatibilidade ser diferente.
- **Registradores de compatibilidade desconhecidos** (Grab, bancos da mailbox, bits do DMA do LCD): inferidos só do
  SceCompat; o trace do boot os fixa.
- **Custo:** emulação dentro de emulação; o interpretador vai ser lento até a fase 6.
