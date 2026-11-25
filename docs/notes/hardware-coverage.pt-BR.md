<!-- pair: hardware-coverage.md sha256=b5c0b764569353c25d46f43b40882d24287169e94a24ef119dbd9cd05b8ac459 -->
# Cobertura do hardware (SoC e console do PS Vita)

*English: [hardware-coverage.md](hardware-coverage.md).*

Inventário dos blocos de hardware do PS Vita (o SoC CXD5315GG do PCH-1000, os chips em volta e a variante PS TV).
Para cada bloco: o que o driver do FW 3.74 espera do hardware, como o VitaSharp atende hoje e o que falta. É uma nota
de estudo, sem código de produto. Fontes: os módulos do FW 3.74 decifrados e descompilados com as ferramentas de RE
locais (`%LOCALAPPDATA%\VitaSharpTools\fw374`, só estudo, nada copiado), o HLE em `src/VitaSharp.Core` (o comentário
de cabeçalho de cada arquivo de driver, que registra o que o módulo faz), o roadmap, `docs/notes/psp-compat.md`,
`docs/notes/online-features.md`, `docs/hle/F-devices-media.md` e estas páginas públicas: psdevwiki
([Hardware](https://psdevwiki.com/vita/Hardware), [Pinout](https://psdevwiki.com/vita/Pinout); o site recusa acesso
automático, então os pontos marcados "a confirmar" pedem leitura à mão), o
[PlayStation Vita architecture](https://www.copetti.org/writings/consoles/playstation-vita) de Rodrigo Copetti, a
página do [VVNZRunner](https://gamebrew.org/wiki/VVNZRunner_Vita) e o README do
[VitaLoader Redux](https://github.com/CreepNT/VitaLoaderRedux).

## Como o VitaSharp atende o hardware

Os drivers de kernel do firmware falam com registradores, barramentos e coprocessadores. O VitaSharp não tem SoC por
trás deles. Um driver é atendido de um de três jeitos:

- **HLE do driver:** as entradas `[Hle]` fazem o que o módulo faz, sobre estado emulado (a maior parte do `os0:kd` e
  da boot image). Onde existe um dispositivo, o estado vem de um dispositivo do host (áudio, webcam, controle,
  sockets).
- **Default documentado (`[Hle(Default = true)]`):** as checagens do próprio módulo e depois a resposta que o módulo
  dá sem o dispositivo (sem cabo, sem cartão, sem aparelho pareado, sem processador seguro). O `hle-coverage` conta
  esses à parte.
- **Emulação num nível acima:** o trabalho do hardware é feito uma camada acima (a GPU no nível dos comandos GXM, o
  codec engine no nível das bibliotecas de decodificação, o armazenamento no nível de arquivos).

O que separa o núcleo de compatibilidade (Allegrex) do resto: é um **coprocessador que roda o próprio código**, e
nenhuma camada acima pode substituí-lo, porque o código que roda nele (o kernel do PSP e os jogos de PSP) é o próprio
conteúdo. O levantamento abaixo procurou outros blocos desse tipo. Os coprocessadores do Vita que rodam firmware
próprio são: o Allegrex (`pcbc`/`pcff`), o Venezia (`os0:kd/vnzimg.skprx`), as unidades VIP
(`os0:kd/vipimg.skprx`), o processador seguro (`os0:sm/*.self`), o chip de Wi-Fi/Bluetooth
(`os0:kd/wlanbt_robin_img_ax.skprx`), o syscon e o sensor de movimento (o firmware desses dois está no pacote de
atualização, não é rodado pelo lado ARM). Só o Allegrex e o Venezia rodam código que não vem do firmware da Sony:
títulos de PSP no primeiro, código MeP de homebrew (VVNZRunner) no segundo.

## Resumo

Classes: **(a)** hardware ausente que bloqueia funcionalidade real; **(b)** parcial; **(c)** fora de escopo (limite
registrado, não é para emular); **ok** coberto.

| Bloco | Driver do FW 3.74 | VitaSharp hoje | Classe |
|---|---|---|---|
| ARM Cortex-A9 MPCore (4 núcleos, VFPv3, NEON) | o próprio kernel | LLE: interpretador, JIT IL e nativo para A32/T16/T32/VFP/NEON (`Cpu/`), modos de exceção | ok |
| GPU SGX543MP4+ | `gpu_es4.skprx`, `SceGpuEs4`, `libgpu_es4`/`libgxm_es4` | fluxos de comandos GXM desenhados em Vulkan (`Gpu/`, `Gxm/`); serviços do `SceGpuEs4ForDriver` como registros, nunca trava | ok (nível alto) |
| Processador seguro (Toshiba MeP, "F00D") | `os0:sm/*.self`, `sm_comm`, `ss_mgr`, `SceSblFwLoader` | decifração de SELF/PFS pelo código do próprio emulador; comandos seguros recusam (`0x80020004`) ou respondem status 0 onde não devolvem dados | (c) |
| Núcleo de compatibilidade PSP (Allegrex) | `SceCompat`, `os0:kd/pcbc.skprx`, `vs0:app/NPXS10028/pcff.skprx` | o ScePspemu real roda até o `sceCompatInitEx`, que recusa: sem núcleo | **(a)** (item do roadmap já existe) |
| Processador de mídia Venezia (núcleos MeP + IVC2) | o `SceCodecEngineWrapper` carrega o `vnzimg.skprx`; `SceAvcodec`, `SceAudioin`, `SceNgs` o usam | não emulado; as bibliotecas de decodificação são HLE no nível de usuário (ATRAC9 próprio, FFmpeg para AVC/MP3/AAC/MPEG-4/G.729); a contabilidade do wrapper é HLE, os RPCs respondem 0 | **(a)** para código MeP, **(b)** para codecs |
| Unidades de imagem VIP | `SceCodecEngineWrapper` ("SceVipDriverPu0/1") carrega o `vipimg.skprx` | não emulado; o trabalho de câmera e JPEG é feito pelas bibliotecas em HLE | ok (nível alto); a confirmar o que elas atendem |
| Controladores de DMA, DMA de cripto DMAC5 | `dmacmgr.skprx`, `ss_mgr` (DMAC5) | cópias em HLE; AES/DES/HMAC do DMAC5 com chave em memória implementados; slots de chave preenchidos pelo processador seguro recusam | ok; slots de chave (c) |
| Tela: planos IFTU, cabeças DSI, painel OLED / LCD | `display.skprx`, `lowio` (IFTU, DSI), `oled.skprx`, `lcd.skprx` | o HLE do SceDisplay compõe os planos; cabeça 0 = painel OLED (DDB id 0); o LCD responde "sem painel" (PCH-1000) | ok (PCH-1000) |
| Transmissor HDMI + CEC (PS TV) | `hdmi.skprx`, cabeça DSI 1, HDMI do `SceAVConfig` | "sem transmissor, sem monitor"; chamadas HDMI do AVConfig `0x803A0109` (não é PS TV) | **(b)**, parte do modelo PS TV |
| Câmeras (frontal/traseira), MIPI CSI-2, ISP | `SceCamera`, `lowio` (CSI) | webcam do host / imagem fixa / cinza por câmera (`Host/Camera.cs`, `CameraSetup.cs`); registradores do sensor guardados | ok |
| Tela de toque frontal, touchpad traseiro | `SceTouch` | mouse/toque do host nos dois painéis | ok |
| Movimento: acelerômetro, giroscópio | `SceMotionDev`, `SceMotion` | giroscópio/acelerômetro do host (controle SDL, sensores do celular) | ok |
| Movimento: magnetômetro | `SceMotionDev`, `SceMotion` | campo do host (magnetômetro do celular no Android, `heading@` roteirizado no PC): matriz NED e campo estável com o magnetômetro ligado | feito (era **(b)**); a calibração gravada como o 3.74; falta o anel de registros brutos |
| GPS / localização | `SceGps` (driver do modem), `liblocation`, `location_provider` | desligada por padrão ("aplicativo sem permissão"); posição fixa (PC) ou a do celular (Android) quando a opção dá uma | **(b)** |
| Modem 3G (PCH-1100) | `SceBbmc`, modem USB | ausente, como no modelo Wi-Fi que a unidade é (`0x80330003`) | (c) (fiel ao modelo; o serviço 3G acabou) |
| Wi-Fi infraestrutura | `SceWlanBt`, `SceNetPs`, `wlanbt_robin_img_ax.skprx` | sockets do host (`--net=real`); perfil offline senão | ok |
| Wi-Fi ad hoc | ad hoc do `SceNetPs`, `pspnet_adhoc`, `sceNetAdhocctl*`, Matching | perfil offline: a biblioteca funciona, nenhum par é achado | **(b)** |
| Bluetooth | `SceBt`, `SceHid` | nada pareado: sem fone, teclado ou mouse (`NoHardware.KernelServices.cs`) | **(b)** (teclado/mouse; controles no item PS TV) |
| Porta USB de dispositivo (conector multiuso) | `SceUdcd`, `usbstor*`, `SceUsbMtp`, `usbpspcm`, `usbdev_serial` | cabo sempre desconectado (`0x10`) | **(b)** (Content Manager, armazenamento USB) |
| Portas USB de host (PS TV) | `SceUsbd`, `SceUsbMass`, `SceUsbAudio`, `SceUsbEtherRtl/Smsc`, `SceUsbServ` | nada conectado | **(b)**, parte do modelo PS TV |
| Slot do cartucho | `sdif` host 1, `gcauthmgr` | dump do cartucho servido por arquivos (`gro0:`/`grw0:`); a autenticação recusa | ok; autenticação (c) |
| Cartão de memória, eMMC interno | `msif.skprx`, `sdif` host 0, `sdstor.skprx` | partições servidas por arquivos; leitura/escrita de setores falha (`0x803D0000`, `0x8032001A`) | **(b)** |
| Syscon, energia, bateria, RTC, botões | `syscon.skprx`, `ScePower`, `rtc.skprx` | HLE com bateria, carregador, eventos, timers de ociosidade, botão de energia, alarmes | ok |
| Saída de áudio, chip de codec, conector de fone | `SceAudio`, `SceCodec` (I2C 0x34), `SceHpremote` | dispositivo de áudio do host; ajustes do codec aceitos; nada no conector | ok |
| Microfone | `SceAudioin`, `SceCodec` | captura do host (OpenAL Soft no desktop, `AudioRecord` no Android) reamostrada para a taxa da porta no ritmo de grain do guest; silêncio sem dispositivo | feito (era **(b)**) |
| Controles além do embutido, vibração, light bar | `SceCtrl`, `SceDs3`, `SceHid`, `SceBt` | só a porta 0 (o controle embutido); `sceCtrlSetActuator`/`SetLightBar` ignorados; `sceCtrlIsMultiControllerSupported` = 0 | **(b)**, parte do modelo PS TV |
| Interrupções, timers, GPIO, I2C, gerador de clock, registradores pervasive | `intrmgr`, `systimer`, `lowio`, `SceClockgen` | modelos em HLE; os timers do sistema geram interrupções em tempo de guest; GPIO/I2C sem dispositivos atrás | ok |
| Modelo do console (PCH-1000 / PCH-1100 / PCH-2000 / VTE-1000 PS TV) | SceSblAIMgr, hardware info do syscon, `vs0` por modelo | PCH-1000 CEX fixo (`Hle/ConsoleIdentity.cs`) | **(b)** (PS TV; cartão interno do PCH-2000) |

## As lacunas em detalhe

### (a) Núcleo de compatibilidade Allegrex

Estudo, plano e fases em [psp-compat.pt-BR.md](psp-compat.pt-BR.md) e no item do roadmap "Núcleo de
compatibilidade PSP". Impacto: todo título de PSP e PSone, homebrew de PSP, Adrenaline. A maior peça é o GE do PSP.

### (a)/(b) Processador de mídia Venezia

- **O que o firmware faz:** o `SceCodecEngineWrapper` (boot image) monta as janelas de memória do engine, pede ao
  `SceSblFwLoader` que carregue nelas o `os0:kd/vnzimg.skprx` ("SceVeneziaImage"), liga os núcleos pelos
  registradores pervasive e espera o engine informar o estado 8. Os decodificadores (`SceAvcodec`: AVC, MPEG-4, MP3,
  AAC, ATRAC9, CELP, JPEG), o caminho do microfone (`SceAudioin`) e o `SceNgs` mandam RPCs ao firmware dele. O mesmo
  módulo carrega o `os0:kd/vipimg.skprx` nas duas unidades VIP ("SceVipDriverPu0/1"; a confirmar o que atendem).
- **Hoje:** a opção de modelo (`--model=pstv`, `VITASHARP_MODEL`, Configurações > Emulação > Modelo; `ConsoleModel`)
  faz o aparelho ser um PS TV: bit 0x200 do sub code (IsDolce / IsGenuineDolce = 1), `sceCtrlIsMultiControllerSupported`
  = 1, portas 1-4 = os controles do host um a um, `sceCtrlSetActuator` nos motores do controle (rumble do SDL). Falta:
  o `vs0` do PS TV, os dispositivos do PS TV (câmera/toque/movimento "dummy"), HDMI, USB host, barra de luz, o tipo de
  placa na informação de hardware, os controles no Android.
- **Parte (a):** código que manda os próprios programas MeP ao Venezia não tem onde rodar. O plugin de kernel
  homebrew VVNZRunner faz exatamente isso (uma API para rodar código Toshiba MeP em até 6 núcleos, 7 no modo
  estendido do `sceCodecEngineChangeNumWorkerCoresMax`), e o homebrew feito sobre ele não recebe resultado. Pela
  regra do projeto o plugin roda sem mudança (LLE), então o único caminho é emular o engine. Ao contrário do `pcbc`,
  o `vnzimg.skprx` sai do `self2elf` em claro (entropia medida 4,4-5,5 bits/byte, strings legíveis): nenhuma chave
  bloqueia. O coprocessador IVC2 (a unidade vetorial de cada núcleo) não tem documentação pública; o VitaLoader Redux
  o lista como não implementado.
- **Parte (b) (codecs):** o HLE não tem decodificador CELP (criar um responde o erro de tipo inválido, com aviso), e
  os codificadores CELP e G.729 produzem quadros zerados; o decodificador JPEG dividido é recusado. Usuários: chat de
  voz (`SceVoice`, Party), gravação de voz, jogos que gravam e tocam o microfone.
- **Tamanho:** interpretador do núcleo MeP [G]; IVC2 [G, ISA desconhecida]; rodar o `vnzimg` até o estado 8 e atender
  os RPCs [G]. Decodificador CELP pela ISO/IEC 14496-3 [G].
- **Primeira entrega testável:** interpretador do núcleo MeP-c5 (a ISA base está nos manuais MeP da Toshiba) numa
  thread de guest, com um programa sintético montado no teste (bytes nossos: aritmética, loads/stores, desvios, uma
  escrita de mailbox que o lado ARM lê). Para o CELP: a tabela de tamanho de quadro por modo de taxa conferida com o
  contrato do `SceAudiodec`, depois quadros decodificados comparados com valores calculados pela norma.

### (b) Modelo PS TV (controles, vibração, HDMI, USB host, Ethernet)

- **O que o firmware faz:** um PS TV (VTE-1000, "dolce") é reconhecido pela identidade do KBL (bit `0x200` do product
  sub code, tipo de placa `0x51`/`0x70`/`0x72` no hardware info), que o `ksceSblAimgrIsDolce` e os patches de `vs0`
  por modelo (target 2 do pacote de firmware) seguem. Nele o `SceDs3` e o `SceBt`/`SceHid`/`SceUsbd` atendem até
  quatro controles DS3/DS4 (portas 1-4, vibração pelo `sceCtrlSetActuator`, light bar), o transmissor HDMI e o CEC
  funcionam, o `SceCameraDummy`, o `SceTouchDummy` e o `SceMotionDevDummy` substituem os dispositivos do portátil, e
  as portas USB de host e o adaptador Ethernet USB funcionam.
- **Hoje:** a unidade é sempre um PCH-1000 (`Hle/ConsoleIdentity.cs`). Só a porta 0, vibração e light bar
  descartadas, HDMI e USB host respondem "não é PS TV" / "nada conectado".
- **Impacto:** multijogador local com vários controles (títulos que consultam `sceCtrlIsMultiControllerSupported` e
  leem as portas 1-4; quais títulos, a confirmar), vibração nos controles do host, o software de sistema do próprio
  PS TV e os ajustes dele (resolução HDMI, controles, Ethernet).
- **Tamanho:** [G] (opção de modelo, instalação do `vs0` do PS TV, os controles nos controles do host, destino da
  vibração, HDMI como "um monitor que aceita todo modo").
- **Primeira entrega testável:** opção de modelo do console (num lugar só, `ConsoleIdentity` a partir dela, a
  variável no `EnvVars.cs`). Teste: com o modelo PS TV o `ksceSblAimgrIsDolce` = 1, as chamadas HDMI do AVConfig
  deixam de responder `0x803A0109`, `sceCtrlIsMultiControllerSupported` = 1 e o `sceCtrlGetControllerPortInfo` lista
  um segundo controle do host (roteirizado) na porta 1, e o `sceCtrlSetActuator` nessa porta chega a um destino de
  vibração que grava; com o modelo padrão tudo responde como hoje.

### (b) Microfone

- **O que o firmware faz:** o `SceAudioin` lê o ADC do chip de codec pelo caminho de áudio do Venezia; portas de 16
  bits mono a 16 ou 48 kHz, uma por processo, adotadas pelo AppMgr.
- **Hoje (feito):** a porta entrega o microfone do host (`Host/Microphone.cs`, `Host/MicrophoneFeed.cs`,
  `Hle/Modules/Audio.In.cs`; captura do OpenAL Soft no desktop, `AudioRecord` no Android), reamostrado para a taxa da
  porta, um grain a cada grain/taxa segundos de tempo do guest; com mudo, sem dispositivo ou permissão, ou num processo
  para o qual o microfone não está adotado, silêncio. Antes: as amostras eram sempre zero
  ("virtual microphone (no host capture)").
- **Impacto:** chat de voz em jogos online e no Party, voz no Skype/Near, gravação de voz em apps, mecânicas de jogo
  que reagem a som (títulos a confirmar).
- **Tamanho:** [M]: uma interface de captura do host ao lado da da câmera (`Host/`), uma implementação por sistema
  (WASAPI / PulseAudio ou PipeWire / Core Audio / `AudioRecord` do Android) e uma fonte "nenhuma".
- **Primeira entrega testável:** a interface mais uma fonte roteirizada no teste (uma senoide): o `sceAudioInInput`
  devolve essas amostras no ritmo do grain, o mudo dá zeros e sem fonte dá zeros como hoje.

### (b) Wi-Fi ad hoc

- **Hoje:** o `pspnet_adhoc` e o Matching iniciam e respondem como sem nenhum par por perto
  (`Hle/Modules/NetAdhoc*.cs`).
- **Impacto:** multijogador local entre duas unidades (`docs/notes/online-features.md` lista Persona 4 Golden,
  Minecraft, Sakatsuku e Soul Sacrifice Delta entre os usuários); depois o ad hoc dos títulos de PSP pelo canal WLAN
  do ScePspemu.
- **Tamanho:** [G]. **Primeira entrega testável:** duas pilhas emuladas num teste sobre UDP de loopback do host: uma
  cria um grupo, a outra o acha com `sceNetAdhocctlScan`, entra, e um datagrama PDP vai e volta.

### (b) GPS / localização

- **Hoje:** desligada por padrão, o aplicativo "sem permissão" (`Hle/Modules/Location.cs`); com a opção de
  localização (`--location=latitude,longitude`, "Localização do celular" no Android) o aplicativo tem permissão e recebe
  essa posição (`Host/Location.cs`); os callbacks de localização a recebem da thread "SceLocationCallback" como no 3.74
  (`Hle/Modules/Location.Callbacks.cs`: a primeira posição e depois a cada `distance` metros). Falta: o serviço de
  localização do PC, os callbacks de direção (o serviço não tem bússola), o caminho do firmware (liblocation LLE contra o provedor do shell, `SceGps` de um modelo 3G).
- **Impacto:** Near, Mapas e os recursos de localização de apps e jogos.
- **Tamanho:** [M]: uma interface de localização do host (Geolocation do Windows, GeoClue, Core Location,
  `LocationManager` do Android) e uma opção de posição fixa. **Primeira entrega testável:** uma posição roteirizada;
  com o aplicativo permitido, o `sceLocationGetLocation` a devolve com o método e a precisão dados; sem fonte a
  resposta continua a de hoje.

### (b) Magnetômetro

- **Hoje:** a amostra de movimento do host traz o campo magnético (magnetômetro do celular, `heading@` no roteiro);
  com o magnetômetro ligado e o campo estável, o `sceMotionGetState` dá a matriz NED e STABLE
  (`Hle/Motion/MotionDevice.cs`). A calibração que o atualizador do shell grava fica guardada como no 3.74
  (`sceMotionDevUpdateMagnCalibData` / `GetCurrentMagnCalibData`: 12 palavras e um contador de atualizações; as
  leituras brutas conferem os argumentos como o 3.74). Falta: o anel de registros brutos do driver (o que
  `sceMotionDevRead`/`ReadForMagnCalib` copiam: nenhum ainda) e o campo no quaternion da fusão.
- **Impacto:** direção da bússola (Mapas, Near, títulos de RA que usam o norte), `magFieldStability` e a matriz NED.
- **Tamanho:** [M]. **Primeira entrega testável:** a leitura de movimento do host ganha campo magnético; um campo
  roteirizado dá a direção e um `magFieldStability` estável no `sceMotionGetSensorState`/`GetState`.

### (b) Teclado e mouse Bluetooth

- **Hoje:** com a opção (`--bt-keyboard-mouse=on`, Configurações > Emulação; Android: Ajustes) o teclado e o mouse
  do host são os dispositivos pareados: `sceHid{Keyboard,Mouse}Enumerate/Read/Peek` como o 3.74 (`Hle/Modules/Hid.cs`,
  `Host/Hid.cs`), com os callbacks de dica notificados; o SceBt os lista (`sceBtGetRegisteredInfo`) e enfileira as
  conexões e desconexões como os eventos 5 e 6 do `sceBtReadEvent` (`SystemServicesVsh.BluetoothEvents.cs`). Os
  controles de consumo (`sceHidConsumerControl*`) respondem como o 3.74 sem nenhum desses pareado; as chamadas de
  controle (`sceHidController*`) respondem 0x803B0004, como o 3.74. Falta: uma fonte do host para as teclas de
  consumo (teclas de mídia) e conferir à mão a tela de Bluetooth do shell.
- **Impacto:** digitar no navegador, no IME e em apps que leem o teclado direto; mouse no navegador.
- **Tamanho:** [M]. **Primeira entrega testável:** o teclado do host oferecido como um teclado pareado (uma opção):
  `sceHidKeyboardEnumerate` devolve um handle e `sceHidKeyboardRead` informa teclas roteirizadas.

### (b) Porta USB de dispositivo (Content Manager, armazenamento USB)

- **Hoje:** o cabo nunca está conectado.
- **Impacto:** Content Manager com PC ou PS3 (backup, transferência), modo de armazenamento USB, recursos PSP-PS3.
- **Tamanho:** [G] (um host emulado do outro lado do cabo). **Primeira entrega testável:** um evento "cabo
  conectado": o `SceUdcd` passa a `0x20`, a espera de estado retorna e um host roteirizado completa um pedido de
  controle.

### (b) Cartão de memória e eMMC como dispositivos de bloco

- **Hoje:** cada partição é uma pasta do host; o acesso a setores falha como um comando de cartão que falhou.
- **Impacto:** o que o firmware faz no nível de bloco: formatar o cartão de memória pelos Ajustes, a atualização do
  sistema pelos Ajustes gravando `os0`/`vs0`, homebrew que lê ou grava partições cruas (os drivers de armazenamento
  da cena).
- **Tamanho:** [G]. **Primeira entrega testável:** um log de todo acesso a setor por chamador (para listar os usuários
  reais), depois um arquivo de imagem como base de um dispositivo do `sdstor`: um setor gravado é lido de volta, fora
  do limite responde como o módulo.

## Fora de escopo (c)

- O processador seguro e as chaves por console: o VitaSharp não emula o processador seguro nem deriva chaves. O que
  depende dele (slots de chave do DMAC5, autenticação do cartucho, ativação de licença, decifrar a camada interna do
  `pcbc`) responde como uma unidade de varejo sem o token. É um limite, registrado aqui e no `psp-compat.md`.
- O modem 3G: a unidade emulada é o modelo Wi-Fi, e o serviço 3G não existe mais.
- Hardware de fábrica e de desenvolvimento (Deci4p, o console UART, os ganchos de teste).

## Ordem sugerida

1. Allegrex (item existente, fase 1).
2. Microfone (a menor mudança com o maior efeito).
3. Modelo PS TV (vários controles, vibração, HDMI, software do PS TV).
4. Wi-Fi ad hoc.
5. GPS/localização, magnetômetro, teclado Bluetooth.
6. Execução MeP do Venezia, CELP, armazenamento em bloco, porta USB de dispositivo.
