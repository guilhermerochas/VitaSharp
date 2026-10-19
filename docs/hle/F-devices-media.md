# F — Dispositivos e mídia

Escopo: SceTouch, SceCtrl, SceMotion, SceAudio (AudioOut), SceAudioIn, SceAudiodecUser, SceVideodecUser, SceCamera,
ScePower, SceFace, SceSmart, SceNgs (só Sulpha). Lista-alvo: 224 funções do VitaSDK sem HLE (P0/P1) e mais 41 stubs
existentes que descartam comportamento (STUB). Formato e convenções: ver [README.md](README.md).

## Contagem

| Biblioteca | P0 | STUB | P1 | FORA | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| SceTouch | 0 | 4 | 17 | 3 | 24 |
| SceCtrl | 0 | 2 | 3 | 5 | 10 |
| SceMotion | 1 | 10 | 6 | 0 | 17 |
| SceAudio | 0 | 0 | 6 | 0 | 6 |
| SceAudioIn | 1 | 3 | 7 | 1 | 12 |
| SceAudiodecUser | 0 | 0 | 4 | 0 | 4 |
| SceVideodecUser | 0 | 0 | 19 | 34 | 53 |
| SceCamera | 0 | 21 | 24 | 0 | 45 |
| ScePower | 0 | 0 | 21 | 0 | 21 |
| SceFace | 6 | 0 | 17 | 0 | 23 |
| SceSmart | 10 | 0 | 34 | 0 | 44 |
| SceNgs (Sulpha) | 0 | 0 | 5 | 0 | 5 |
| SceSysmodule (pré-requisito de Face/Smart) | 0 | 1 | 0 | 0 | 1 |
| **Total** | **18** | **41** | **163** | **43** | **265** |

Das 224 funções da lista, 43 foram rebaixadas de P1 para FORA (variantes `Internal`/`Nongameapp` do Videodec, funções
de shell/diagnóstico de Touch, Ctrl e AudioIn), cada uma com justificativa na sua seção.

## Achados que mudam a estratégia

1. **SceFace e SceSmart não precisam de HLE: são módulos que o jogo traz.** Invizimals (e também PCSA00126, PCSB01240,
   PCSC00045, PCSG00198) traz `app0:sce_module/libface.suprx` e `app0:sce_module/libsmart.suprx`. Carregados pelo
   `Emulator.LoadModule` do VitaSharp (teste feito com `VitaSharp.Core` em 2026-10-18), os dois resolvem todos os seus
   imports: libc/libm/libstdc++ (LLE, `app0:sce_module/libc.suprx`) e, no SceLibKernel, só `__stack_chk_fail`,
   `__sce_aeabi_idiv0`, `__sce_aeabi_ldiv0`, `sceKernelCreate/Delete/Lock/UnlockLwMutex` e
   `sceKernelGetProcessTimeWide`, todos já em HLE. **Zero imports faltando.** O jogo chama
   `sceSysmoduleLoadModule(0x38)` (SCE_SYSMODULE_FACE) e `sceSysmoduleLoadModule(0x39)` (SCE_SYSMODULE_SMART) logo antes
   de usar as bibliotecas (endereços `0x814D21DA` e `0x814FEE98` do eboot). O Vita3K faz o mesmo: FACE e SMART estão em
   `auto_lle_modules` (`vita3k/module/src/load_module.cpp`). Basta que `sceSysmoduleLoadModule` carregue esses `.suprx`
   (entrada [sceSysmoduleLoadModule](#scesysmoduleloadmodule-pré-requisito-de-facesmart)). Isso resolve os 16 P0 de
   Face/Smart e as 48 P1 exportadas por esses módulos. As specs HLE abaixo ficam como fallback, para quando o `.suprx`
   não existir.
2. **"AR sem câmera" depende da câmera, não do Face/Smart.** Com LLE, libface e libsmart processam qualquer imagem que
   o jogo entregar. Hoje `sceCameraOpen/Start/Read` falham com `0x802E000B` (que é `SCE_CAMERA_ERROR_EXCLUSIVE`, não
   "câmera ausente"). A proposta é uma câmera virtual que entrega quadros neutros (cinza), descrita em
   [SceCamera](#scecamera). Num quadro sem textura, o detector de rostos devolve 0 rostos e o scene mapping não acha
   pontos de inicialização. Esse é o resultado "nada detectado / tracking perdido" pedido, vindo do próprio código da
   Sony.
3. **Códigos de erro errados em funções já implementadas** (corrigir junto, ver
   [Anexo A](#anexo-a--correções-em-funções-já-implementadas)): `Audio.cs` usa `0x80260007` como INVALID_PORT (o
   correto é `0x80260003`); AudioIn e Camera devolvem códigos de outras condições; `Ctrl.Peek` devolve
   PRIV_REQUIRED para ponteiro nulo.

## Ordem sugerida de implementação

1. `sceSysmoduleLoadModule` carregando `libface`/`libsmart` (resolve 16 P0 de uma vez). Validar com Invizimals.
2. `sceMotionGetSensorState` (P0) junto com o estado de sampling de SceMotion (STUB Start/Stop e Get/Set de
   tilt/deadband).
3. `sceAudioInGetStatus` (P0) junto com o microfone virtual (STUB OpenPort/Input/ReleasePort).
4. Câmera virtual (STUB Open/Start/Read/IsActive/Stop/Close e setters), seguida dos getters P1 de SceCamera.
5. `sceTouchSetSamplingState`/`GetSamplingState` e força do touch (STUB): seis dos sete jogos chamam.
6. Rapid fire do SceCtrl (STUB).
7. P1 baratos e com protótipo conhecido: ScePower (telemetria fixa), SceMotion P1, SceAudio P1, Sulpha, Touch
   Peek2/Read2/PixelDensity.
8. P1 de Audiodec (`DecodeNStreams`, `PartlyDecode`) quando um jogo/libatrac pedir.
9. Videodec P1 (M4V e variantes de AVC): só quando um jogo importar; os protótipos exigem dump do módulo do firmware.
10. Fallback HLE de Face/Smart: só se aparecer um jogo sem os `.suprx` em `sce_module`.

## Convenções deste arquivo

- "Inferido" = sem protótipo no vita-headers; deduzido do Vita3K, da chamada no jogo ou da desmontagem do módulo.
- Desmontagens: `libface.suprx`/`libsmart.suprx` de PCSA00552, decriptados com `VitaSharp.Desktop self2elf` e
  desmontados com Capstone. Os offsets são relativos ao segmento 0 do módulo (Thumb, bit 0 = 1).
- Arquivos: Touch/Ctrl em `src/VitaSharp.Core/Hle/Modules/Display.cs` (classes `Touch`, `Ctrl`); Motion/Camera em
  `Online.cs` (classe `Sensors`); AudioOut/AudioIn em `Audio.cs`; Audiodec em `Audiodec.cs`; Videodec em `Videodec.cs`;
  Power e Sysmodule em `SystemMisc.cs`; Sulpha em `Ngs.cs`. Para fallback de Face/Smart, criar `Face.cs`/`Smart.cs`.
- Entrada do host: `Host/HostServices.cs` (`IInputBackend`: `GetController`, `GetTouches`); implementação em
  `VitaSharp.Desktop/Host/WindowHost.cs`. Não há fonte de movimento, câmera nem microfone no host.

---

## SceTouch

Estado novo da classe `Touch`: `SceTouchSamplingState[2] _sampling` (padrão STOP/STOP, ver STUB abaixo) e
`bool[2] _force` (padrão `false`). `Peek` (Display.cs) passa a consultar os dois.

### sceTouchSetSamplingState
- [ ] **Prioridade:** STUB (chamada por Invizimals, Devious Dungeon, God of War, Killzone, F1) · **Biblioteca:** SceTouch · **NID:** 0x1B9C5D14
- **Protótipo:** `int sceTouchSetSamplingState(SceUInt32 port, SceTouchSamplingState state);` (`psp2/touch.h`)
- **Comportamento:** liga ou desliga a amostragem do painel. Com STOP, `sceTouchRead/Peek` continuam funcionando, mas
  devolvem `reportNum = 0` (Vita3K `touch/src/touch.cpp`, `if (!touch_mode[port]) data->reportNum = 0`). Hoje
  (Display.cs:270) a função não faz nada e o painel responde mesmo desligado.
- **Retorno/erros:** 0. `SCE_TOUCH_ERROR_INVALID_ARG` (0x80350001) se `port > 1` ou `state` não for 0/1.
- **Estruturas:** `SCE_TOUCH_SAMPLING_STATE_STOP = 0`, `START = 1`.
- **Implementação no VitaSharp:** guardar em `_sampling[port]`. Em `Peek`, quando `_sampling[port] == STOP`, escrever o
  cabeçalho (timestamp, `reportNum = 0`) e devolver 1. O estado inicial de hardware é STOP para os dois painéis (é o que
  o Vita3K usa). **Risco:** um jogo que lê o touch sem nunca chamar `SetSamplingState` perderia o touch. Dos jogos
  instalados, todos os que importam SceTouch também importam `SetSamplingState`. Para Uncharted (PCSA00029) a lista de
  imports não foi verificada (a carga dele falha na ferramenta). Confirmar com `VitaSharp.Desktop imports PCSA00029`.
- **Validação:** `tests/scenarios/gow_menu.txt`, `kz_gameplay.txt`, `dd_boot.txt` e `f1_menu.txt` sem regressão
  (taps continuam funcionando). Log Debug da chamada mostra `state=1` no boot desses jogos.

### sceTouchGetSamplingState
- [ ] **Prioridade:** STUB · **Biblioteca:** SceTouch · **NID:** 0x26531526
- **Protótipo:** `int sceTouchGetSamplingState(SceUInt32 port, SceTouchSamplingState *pState);` (`psp2/touch.h`)
- **Comportamento:** devolve o estado gravado por `SetSamplingState`. Hoje devolve sempre 1.
- **Retorno/erros:** 0; `0x80350001` se `port > 1` ou `pState == NULL`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `state.Value = _sampling[port]`.
- **Validação:** teste unitário: Set(0, STOP) seguido de Get deve dar 0.

### sceTouchEnableTouchForce / sceTouchDisableTouchForce
- [ ] **Prioridade:** STUB · **Biblioteca:** SceTouch · **NID:** 0xB18370C2 (Enable) / 0x41194411 (Disable)
- **Protótipo:** `int sceTouchEnableTouchForce(SceUInt32 port);` / `int sceTouchDisableTouchForce(SceUInt32 port);` (`psp2/touch.h`)
- **Comportamento:** liga ou desliga o campo `force` dos relatórios. Desligado, `force = 0`; ligado, `force` vem dentro de
  `minForce..maxForce` (1..128). Hoje o Peek escreve sempre 128.
- **Retorno/erros:** 0; `0x80350001` se `port > 1`.
- **Estruturas:** `SceTouchReport.force` (offset +1 de cada relatório de 16 bytes).
- **Implementação no VitaSharp:** `_force[port] = true/false`; em `Peek`, `force = _force[port] ? 128 : 0` (o host não
  tem pressão, como no Vita3K).
- **Validação:** Invizimals e F1 chamam `EnableTouchForce`; verificar no log que os relatórios saem com 128.

### sceTouchPeek2 / sceTouchRead2
- [ ] **Prioridade:** P1 · **Biblioteca:** SceTouch · **NID:** 0x3AD3D0A1 (Peek2) / 0x39401BEA (Read2)
- **Protótipo:** inferido (Vita3K): `int sceTouchPeek2(SceUInt32 port, SceTouchData *pData, SceUInt32 nBufs);`, idem `sceTouchRead2`.
- **Comportamento:** mesmo contrato de `sceTouchPeek`/`sceTouchRead`. O Vita3K trata as duas como aliases. Diferença
  real em relação às versões sem "2": a confirmar (provavelmente o tratamento de `SCE_TOUCH_REPORT_INFO_HIDE_UPPER_LAYER`).
  Para confirmar, desmontar o SceTouch do firmware 3.60 e comparar os dois caminhos.
- **Retorno/erros:** número de buffers (1..nBufs); `0x80350001` para porta inválida, `pData == NULL` ou `nBufs > 64`.
- **Estruturas:** `SceTouchData` (0x90 bytes, ver `psp2/touch.h`).
- **Implementação no VitaSharp:** `[Hle("sceTouchPeek2")]` e `[Hle("sceTouchRead2")]` nos métodos existentes. Validar
  `nBufs > 64` em todos.
- **Validação:** teste unitário de alias; nenhum jogo instalado importa.

### sceTouchGetPixelDensity / sceTouchGetPixelDensity2
- [ ] **Prioridade:** P1 · **Biblioteca:** SceTouch · **NID:** 0xF0704CF3 / 0xE0C1DB92
- **Protótipo:** inferido (Vita3K): `int sceTouchGetPixelDensity(float *pFront, float *pBack);` e
  `int sceTouchGetPixelDensity2(float *p1, float *p2, float *p3, float *p4);`
- **Comportamento:** densidade do painel. O Vita3K devolve 22.0 em todos os campos quando o "hardware info" vale
  0x30/0x31, e 17.54 nos outros modelos. Qual modelo (OLED/LCD) corresponde a qual valor: a confirmar (pela NID
  0xCBD6D8BC, GetHardwareInfo, citada no Vita3K). O significado dos 4 campos da versão 2 também está a confirmar.
- **Retorno/erros:** 0; `0x80350001` se algum ponteiro for NULL.
- **Estruturas:** —
- **Implementação no VitaSharp:** escrever 17.54f em todos os ponteiros. Isso equivale a 1920 unidades em ~109 mm
  (largura física da tela de 5"); o modelo correspondente ainda está a confirmar.
- **Validação:** teste unitário.

### Família: regiões e variantes Ext do SceTouch

Sem protótipo no vita-headers e não implementadas no Vita3K, exceto `PeekRegion`. São usadas para dividir o painel
entre processos/overlays. Comportamento comum: aceitar e ignorar a região (`region <= 0xFF`, senão `0x80350001`, como no
Vita3K) e delegar para a função base. Protótipos inferidos: `*Region(port, pData, nBufs, region)`; as variantes `Ext`
recebem os mesmos argumentos da base. Para confirmar, desmontar o SceTouch do FW 3.60.

| [ ] | Função | NID | Prioridade | Protótipo (inferido) | Implementação |
| --- | --- | --- | --- | --- | --- |
| [ ] | sceTouchPeekRegion | 0x04440622 | P1 | `int (SceUInt32 port, SceTouchData*, SceUInt32 nBufs, int region)` | `region > 0xFF` → 0x80350001; senão `Peek` |
| [ ] | sceTouchReadRegion | 0x93B5635B | P1 | idem | idem com `Read` |
| [ ] | sceTouchPeekRegionExt | 0x2CF6D7E2 | P1 | idem + args extras a confirmar | `PeekRegion` |
| [ ] | sceTouchReadRegionExt | 0x9F0ACAF9 | P1 | idem | `ReadRegion` |
| [ ] | sceTouchSetRegion | 0x6134DFBE | P1 | a confirmar | 0 (no-op) |
| [ ] | sceTouchSetRegionAttr | 0x08DD4C7C | P1 | a confirmar | 0 |
| [ ] | sceTouchActivateRegion | 0xBD59D652 | P1 | a confirmar | 0 |
| [ ] | sceTouchClearRegion | 0x2D94EAEA | P1 | a confirmar | 0 |
| [ ] | sceTouchGetSamplingStateExt | 0xDC8671EA | P1 | `int (SceUInt32 port, SceTouchSamplingState*)` | igual a `GetSamplingState` |
| [ ] | sceTouchSetSamplingStateExt | 0x13CDFC43 | P1 | `int (SceUInt32 port, SceTouchSamplingState)` | igual a `SetSamplingState` |
| [ ] | sceTouchEnableTouchForceExt | 0xA3FE877B | P1 | `int (SceUInt32 port)` | igual a `EnableTouchForce` |
| [ ] | sceTouchDisableTouchForceExt | 0xED25DFDC | P1 | `int (SceUInt32 port)` | igual a `DisableTouchForce` |
| [ ] | sceTouchEnableIdleTimerCancelSetting | 0x012EAE3A | P1 | `int (SceUInt32 port, SceBool enable)` (a confirmar) | 0 (o VitaSharp não tem idle timer) |

Validação: teste unitário de cada alias; nenhum jogo instalado importa.

### FORA: sceTouchGetDeviceInfo, sceTouchGetProcessInfo, sceTouchSetProcessPrivilege
- [ ] **Prioridade:** FORA · **Biblioteca:** SceTouch · **NID:** 0xD7889B91 / 0xAB364C23 / 0x609EA489
- **Protótipo:** `int sceTouchGetDeviceInfo(SceUInt32 port_type, SceTouchDeviceInfo *info);` (`psp2/touch.h`); as
  outras duas sem protótipo.
- **Comportamento:** só programas de sistema. Para um jogo, `GetDeviceInfo` devolve `SCE_TOUCH_ERROR_PRIV_REQUIRED`
  (0x80350002), segundo o header.
- **Retorno/erros:** se algum jogo importar, devolver 0x80350002 nas três.
- **Estruturas:** `SceTouchDeviceInfo` (0xC bytes, `psp2common/touch.h`).
- **Implementação no VitaSharp:** nenhuma.
- **Validação:** —

---

## SceCtrl

### sceCtrlSetRapidFire
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCtrl · **NID:** 0xE9CB69C8
- **Protótipo:** `int sceCtrlSetRapidFire(int port, int idx, const SceCtrlRapidFireRule *pRule);` (`psp2/ctrl.h`)
- **Comportamento:** grava a regra de tiro rápido `idx` (0..15) da porta. Depois disso, as leituras do buffer aplicam a
  regra: quando `(buttons & Mask) == Trigger`, os bits de `Target` alternam. Ficam `Delay` amostras sem mudança, depois
  `Make` amostras ligados e `Break` amostras desligados, em ciclo enquanto o gatilho continuar satisfeito. Essa
  semântica vem do sceCtrl do PSP e dos nomes dos campos; a ordem exata (atraso antes do primeiro "make", efeito sobre
  `Target` fora do ciclo) está a confirmar desmontando o SceCtrl do FW 3.60. Hoje (Display.cs:171) a regra é
  descartada.
- **Retorno/erros:** 0. `SCE_CTRL_ERROR_INVALID_ARG` (0x80340001) se `idx` estiver fora de 0..15, `pRule == NULL` ou a
  porta for inválida (o header diz "use 0").
- **Estruturas:** `SceCtrlRapidFireRule` (0x18): +0x00 Mask, +0x04 Trigger, +0x08 Target, +0x0C Delay, +0x10 Make,
  +0x14 Break (todos `uint32`).
- **Implementação no VitaSharp:** em `Ctrl`, `RapidFireRule?[16] _rapid` e um contador de amostras por regra. Aplicar em
  `WriteData` sobre `s.Buttons` antes do `negative`. O contador avança por amostra (vblank), não por chamada: usar
  `VBlank.Count` para que Peek repetido no mesmo quadro não avance o ciclo.
- **Validação:** teste unitário com regra `{Mask=Cross, Trigger=Cross, Target=Cross, Delay=0, Make=2, Break=2}` e Cross
  pressionado: 4 vblanks devem dar o padrão 1,1,0,0.

### sceCtrlClearRapidFire
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCtrl · **NID:** 0xD8294C9C
- **Protótipo:** `int sceCtrlClearRapidFire(int port, int idx);` (`psp2/ctrl.h`)
- **Comportamento:** remove a regra `idx`.
- **Retorno/erros:** 0; `0x80340001` para `idx` fora de 0..15 ou porta inválida.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_rapid[idx] = null`.
- **Validação:** junto com o teste de `SetRapidFire`.

### sceCtrlSetAnalogStickCheckTarget / sceCtrlGetAnalogStickCheckTarget / sceCtrlGetAnalogStickCheckMode
- [ ] **Prioridade:** P1 · **Biblioteca:** SceCtrl · **NID:** 0xFB12A0A7 (SetTarget) / 0xD01DE63A (GetTarget) / 0x35BCD3BB (GetMode)
- **Protótipo:** inferido, pareando com o stub existente `sceCtrlSetAnalogStickCheckMode(uint mode)`:
  `int sceCtrlSetAnalogStickCheckTarget(int target);`, `int sceCtrlGetAnalogStickCheckTarget(int *pTarget);`,
  `int sceCtrlGetAnalogStickCheckMode(int *pMode);`. Nenhuma tem protótipo no vita-headers e o Vita3K não implementa.
- **Comportamento:** configuram uma verificação do analógico (provavelmente a calibração/zona morta usada pelo sistema).
  Semântica a confirmar desmontando o SceCtrl do FW 3.60.
- **Retorno/erros:** 0; NULL nos Get → 0x80340001 (a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** guardar `_stickCheckMode` (gravado também pelo `SetAnalogStickCheckMode` existente) e
  `_stickCheckTarget`, e devolver nos Get. Sem efeito sobre a leitura.
- **Validação:** teste unitário Set/Get.

### FORA: funções de shell do SceCtrl

| [ ] | Função | NID | Motivo |
| --- | --- | --- | --- |
| [ ] | sceCtrlDisconnect | 0x16D26DC7 | Desconecta um controle pareado (PSTV/configurações); sem protótipo; jogos não chamam. Se aparecer: `int (int port)` → 0. |
| [ ] | sceCtrlGetProcessStatus | 0xA2A56685 | Estado de entrada por processo (shell). Sem protótipo. |
| [ ] | sceCtrlRegisterBdRMCCallback | 0x0A06B7E4 | Callback do controle remoto BD (sistema). Se aparecer: devolver 0 sem registrar nada. |
| [ ] | sceCtrlUnregisterBdRMCCallback | 0x103EB2B0 | Par do anterior. |
| [ ] | sceCtrlSetButtonRemappingInfo | 0x8A187F98 | Remapeamento de botões da acessibilidade do sistema. |

---

## SceMotion

Estado novo da classe `Sensors` (Online.cs), com padrões tirados de `psp2/motion.h` e do Vita3K
(`motion/include/motion/motion_input.h`):

| Campo | Padrão | Fonte |
| --- | --- | --- |
| `_sampling` | false | Start/Stop |
| `_tiltCorrection` | 1 | header: "Enabled by default" |
| `_deadband` | 1 | header: "Enabled by default" |
| `_gyroBiasCorrection` | 1 | header: "Enabled by default" |
| `_angleThreshold` | 20.0f graus | Vita3K `angle_threshold = 20.0f`; faixa 0..45 |
| `_magnetometer` | false | On/Off |
| `_counter` | 0 | contador de amostras do SensorState |

Sem fonte de movimento no host: o aparelho fica parado, deitado com a tela para cima. Isso dá aceleração (0, 0, -1) g,
giroscópio (0, 0, 0) e `basicOrientation` (0, 0, 1), os mesmos valores que o `sceMotionGetState` atual já escreve.
Códigos de erro de `psp2/motion.h`: NOT_SAMPLING `0x80360206`, ALREADY_SAMPLING `0x80360207`, OUT_OF_BOUNDS
`0x80360205`. Do Vita3K: NULL_PARAMETER `0x80360209` e ANGLE_OUT_OF_RANGE `0x8036020A`.

### sceMotionGetSensorState
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceMotion · **NID:** 0x47D679EA
- **Protótipo:** `int sceMotionGetSensorState(SceMotionSensorState *sensorState, int numRecords);` (`psp2/motion.h`)
- **Comportamento:** copia as últimas `numRecords` amostras cruas do acelerômetro e do giroscópio, sem fusão (diferente
  de `GetState`). Invizimals chama em `0x814BCA04` com `numRecords = 64` e copia em seguida `0x1000` bytes
  (64 × 0x40) para o seu estado, junto com os 0xF8 de `sceMotionGetState`. **Por isso 64 precisa ser aceito.** O
  Vita3K rejeita `numRecords >= 64` (`SCE_MOTION_MAX_NUM_STATES`), o que quebraria o Invizimals. O jogo ignora o
  retorno. Preencher todos os registros: cada um com aceleração (0,0,-1), giro (0,0,0), `counter` crescente e
  timestamps crescentes com o índice. A ordem (mais antigo primeiro ou mais novo primeiro) está a confirmar; com dados
  estáticos, só `counter`/`timestamp` mudam. Intervalo entre amostras no hardware: a confirmar. Até lá, usar o vblank
  (16 667 µs), como já fazem Ctrl e Touch.
- **Retorno/erros:** 0. `0x80360206` (NOT_SAMPLING) se a amostragem não estiver ligada (só depois de implementar o STUB
  Start/Stop). `0x80360209` se `sensorState == NULL`. `0x80360205` se `numRecords <= 0` ou `numRecords > 64` (o limite
  superior está a confirmar; 64 é aceito com certeza).
- **Estruturas:** `SceMotionSensorState` (0x40): +0x00 `accelerometer` (3 float), +0x0C `gyro` (3 float), +0x18
  reservado[12], +0x24 `timestamp` (u32, µs), +0x28 `counter` (u32), +0x2C reservado[4], +0x30 `hostTimestamp` (u64,
  µs), +0x38 `dataInfo` (u8; bit 0 = parada forçada), +0x39 reservado[7].
- **Implementação no VitaSharp:** em `Sensors` (Online.cs). `now = Emu.Kernel.ProcessTimeUs`. Para `i` em
  `0..numRecords-1`, registro em `p = sensorState + i*0x40`: zerar 0x40; `W(p+8, -1.0f)`;
  `ts = now - (numRecords-1-i)*16667`; `W32(p+0x24, (uint)ts)`; `W32(p+0x28, _counter - (numRecords-1-i))`;
  `W64(p+0x30, ts)`. Depois, `_counter++`.
- **Validação:** `VitaSharp.Desktop imports PCSA00552` deixa de listar a função. Criar `tests/scenarios/inv_boot.txt`
  (`title=PCSA00552`, `data=C:\Users\guisa\AppData\Roaming\VitaSharp`, `args=--seconds=90 --dump-every=60`) e conferir
  que o modo AR não trava na leitura de sensores.

### Família STUB: estado de amostragem e filtros do SceMotion

Hoje todas devolvem 0 sem guardar nada, e os Get devolvem 0 mesmo com o recurso habilitado por padrão.
Protótipos em `psp2/motion.h`. Implementação: campos da tabela acima, em `Sensors`. Validação: Devious Dungeon chama
`GetDeadband`/`GetTiltCorrection`/`GetMagnetometerState` e Invizimals chama `SetDeadband`/`SetTiltCorrection`; ver no
log Debug que os Get devolvem o que foi gravado.

| [ ] | Função | NID | Protótipo | Comportamento / retorno |
| --- | --- | --- | --- | --- |
| [ ] | sceMotionStartSampling | 0x28034AC9 | `int (void)` | Se já estiver amostrando → 0x80360207; senão `_sampling = true`, 0. |
| [ ] | sceMotionStopSampling | 0xAF32CB1D | `int (void)` | Se não estiver amostrando → 0x80360206; senão `false`, 0. |
| [ ] | sceMotionGetTiltCorrection | 0xC1652201 | `int (void)` | Devolve `_tiltCorrection` (padrão 1). |
| [ ] | sceMotionSetTiltCorrection | 0xAF09FCDB | `int (int setValue)` | Grava (0/1), 0. |
| [ ] | sceMotionGetDeadband | 0x112E0EAE | `int (void)` | Devolve `_deadband` (padrão 1). |
| [ ] | sceMotionSetDeadband | 0x917EA390 | `int (int setValue)` | Grava, 0. |
| [ ] | sceMotionSetAngleThreshold | 0xDACB2A41 | `int (float angle)` | NaN ou fora de 0..45 → 0x8036020A (Vita3K); senão grava, 0. |
| [ ] | sceMotionMagnetometerOn | 0x122A79F8 | `int (void)` | `_magnetometer = true`, 0. |
| [ ] | sceMotionMagnetometerOff | 0xC1A7395A | `int (void)` | `false`, 0. |
| [ ] | sceMotionGetMagnetometerState | 0x3D4813AE | `int (void)` | Devolve `_magnetometer` (1/0). |

Risco: `sceMotionGetState` passaria a exigir `_sampling` (o Vita3K devolve NOT_SAMPLING). Os 5 jogos que importam
SceMotion também importam `StartSampling`, mas manter `GetState` permissivo até validar GoW e Killzone.
`sceMotionReset` (0x0FD2CDA2) continua no-op: com orientação estática não há o que zerar.

### sceMotionGetAngleThreshold
- [ ] **Prioridade:** P1 · **Biblioteca:** SceMotion · **NID:** 0x499B6C87
- **Protótipo:** `float sceMotionGetAngleThreshold(void);` (`psp2/motion.h`)
- **Comportamento:** devolve o limiar em graus usado pela orientação básica.
- **Retorno/erros:** `_angleThreshold` (padrão 20.0f) em s0.
- **Estruturas:** —
- **Implementação no VitaSharp:** `[Hle] float sceMotionGetAngleThreshold() => _angleThreshold;` (o binder já devolve
  `float`, como em `Gxm.cs:324`).
- **Validação:** teste unitário Set(30)/Get.

### sceMotionGetBasicOrientation
- [ ] **Prioridade:** P1 · **Biblioteca:** SceMotion · **NID:** 0x4F28BFE0
- **Protótipo:** `int sceMotionGetBasicOrientation(SceFVector3 *basicOrientation);` (`psp2/motion.h`)
- **Comportamento:** vetor com componentes -1/0/1 indicando qual eixo aponta contra a gravidade. Aparelho parado e
  deitado: (0, 0, 1).
- **Retorno/erros:** 0; 0x80360206 sem amostragem; 0x80360209 se NULL.
- **Estruturas:** `SceFVector3` (3 float, 12 bytes).
- **Implementação no VitaSharp:** escrever (0,0,1), a mesma constante que `sceMotionGetState` grava em +188.
- **Validação:** teste unitário.

### sceMotionGetDeviceLocation
- [ ] **Prioridade:** P1 · **Biblioteca:** SceMotion · **NID:** 0x047F060A
- **Protótipo:** `int sceMotionGetDeviceLocation(SceMotionDeviceLocation *deviceLocation);` (`psp2/motion.h`)
- **Comportamento:** posição (mm) do acelerômetro e do giroscópio em relação ao centro do aparelho.
- **Retorno/erros:** 0; 0x80360209 se NULL.
- **Estruturas:** `SceMotionDeviceLocation` (0x30): +0x00 accelerometer (3 float), +0x0C gyro (3 float), +0x18
  reservado[24]. Valores documentados no header: OLED (PCH-1000) accel (-32, 12, -8), gyro (-33, 6, -8); LCD
  (PCH-2000) accel e gyro (13, 19, -8).
- **Implementação no VitaSharp:** usar os valores OLED (o modelo que o VitaSharp emula em `sceTouchGetPanelInfo`) e
  zerar o reservado.
- **Validação:** teste unitário.

### sceMotionGetGyroBiasCorrection / sceMotionSetGyroBiasCorrection
- [ ] **Prioridade:** P1 · **Biblioteca:** SceMotion · **NID:** 0xE8A28DA5 (Get) / 0xA4A7E6E2 (Set)
- **Protótipo:** `int sceMotionGetGyroBiasCorrection(void);` / `int sceMotionSetGyroBiasCorrection(int setValue);` (`psp2/motion.h`)
- **Comportamento:** liga/desliga a correção de bias do giroscópio (padrão ligado). Sem efeito nos dados estáticos.
- **Retorno/erros:** Get devolve 1/0; Set devolve 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** campo `_gyroBiasCorrection`.
- **Validação:** teste unitário.

### sceMotionRotateYaw
- [ ] **Prioridade:** P1 · **Biblioteca:** SceMotion · **NID:** 0x20F00078
- **Protótipo:** `int sceMotionRotateYaw(float radians);` (`psp2/motion.h`)
- **Comportamento:** gira a referência de yaw da orientação fundida.
- **Retorno/erros:** 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** guardar `_yawOffset += radians`. Com orientação estática, o efeito é girar o
  quaternion/matrizes de `GetState` em torno de Z; implementar só quando houver fonte de movimento no host (até lá, 0
  e no-op).
- **Validação:** —

---

## SceAudio (AudioOut)

Códigos de `psp2common/audioout.h`: NOT_OPENED 0x80260001, BUSY 0x80260002, INVALID_PORT 0x80260003,
INVALID_POINTER 0x80260004, PORT_FULL 0x80260005, INVALID_SIZE 0x80260006, INVALID_FORMAT 0x80260007,
INVALID_SAMPLE_FREQ 0x80260008, INVALID_VOLUME 0x80260009, INVALID_PORT_TYPE 0x8026000A, INVALID_CONF_TYPE 0x8026000C.
O `Audio.cs` atual usa valores trocados (Anexo A).

### sceAudioOutOpenExtPort
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudio · **NID:** 0x6184B939
- **Protótipo:** `int sceAudioOutOpenExtPort(int len, SceAudioOutMode mode);` (`psp2/audioout.h`)
- **Comportamento:** abre a porta MAIN estendida do processo, a 48 kHz. É a nona porta MAIN, além das 8 normais.
- **Retorno/erros:** handle **8**. INVALID_SIZE se `len` não for múltiplo de 64 ou passar de 65472; INVALID_FORMAT se
  `mode` não for 0/1; PORT_FULL se a porta 8 já estiver aberta.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Audio.cs`: reaproveitar `sceAudioOutOpenPort(0, len, 48000, mode)`, mas registrando
  em `_ports[8]`. O alocador atual (`_nextPort++`) precisa reservar 8. Os handles normais no hardware ficam em 0..7?
  A confirmar, pela saída de `sceAudioOutOpenPort` num jogo rodando no Vita3K/hardware. Até lá, alocar o menor livre em
  0..7 e nunca o 8.
- **Validação:** teste unitário: abrir 8 portas normais e a estendida.

### sceAudioOutSetCompress
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudio · **NID:** 0x3601D375
- **Protótipo:** `int sceAudioOutSetCompress(int port, SceBool enable);` (`psp2/audioout.h`)
- **Comportamento:** escolhe o perfil comprimido de codec externo para porta BGM ou VOICE. Sem efeito audível no host.
- **Retorno/erros:** 0; INVALID_PORT se a porta não existir. Para porta MAIN: o erro está a confirmar (provavelmente
  INVALID_PORT_TYPE 0x8026000A).
- **Estruturas:** —
- **Implementação no VitaSharp:** validar a porta (é preciso guardar `Type` em `Port`, que já existe) e devolver 0.
- **Validação:** teste unitário.

### sceAudioOutSetAdoptMode
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudio · **NID:** 0xDCC90EDB
- **Protótipo:** `int sceAudioOutSetAdoptMode(SceAudioOutAdoptMode mode);` (`psp2/audioout.h`)
- **Comportamento:** define se portas BGM/VOICE novas são "adotadas" automaticamente (0) ou não (≠0).
- **Retorno/erros:** sempre 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** guardar `_adoptMode`; nada mais (o VitaSharp não arbitra áudio entre processos).
- **Validação:** —

### sceAudioOutSetAdopt_forUser
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudio · **NID:** 0x26C82AF2
- **Protótipo:** `int sceAudioOutSetAdopt_forUser(SceAudioOutPortType type, SceBool adopt, int ramp_length, SceBool wait_for_completion);` (`psp2/audioout.h`)
- **Comportamento:** adota ou libera o perfil `type` com rampa de `ramp_length` ms (limitada a 1..4096).
- **Retorno/erros:** 0; `type` fora de MAIN/BGM/VOICE → INVALID_PORT_TYPE 0x8026000A (a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** `_adopted[type] = adopt`. `sceAudioOutGetAdopt` (hoje sempre 1) passa a devolver esse
  valor, com padrão 1.
- **Validação:** Invizimals e Sakatsuku chamam `GetAdopt`: o retorno 1 deve continuar igual.

### sceAudioOutSetPortVolume_forUser / sceAudioOutGetPortVolume_forUser
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudio · **NID:** 0x65840F3D (Set) / 0xFF635DA5 (Get)
- **Protótipo:** `int sceAudioOutSetPortVolume_forUser(int port_mask, SceUInt32 volume, int ramp_length);` /
  `int sceAudioOutGetPortVolume_forUser(SceAudioOutPortType type);` (`psp2/audioout.h`)
- **Comportamento:** ganho privado 0..256 por perfil (máscara MAIN=2, BGM=4, VOICE=8), separado do volume Q15 por
  canal. Máscara 0 não faz nada; bits desconhecidos são ignorados. Get devolve o ganho alvo do perfil.
- **Retorno/erros:** Set: 0; volume > 256 → INVALID_VOLUME 0x80260009 (a confirmar). Get: 0..256, padrão 256.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_profileGain[3]`. No `sceAudioOutOutput`, multiplicar por `gain/256` além de
  `p.Volume` (a rampa pode ser ignorada).
- **Validação:** teste unitário: Set(2, 128, 0) e depois Get(0) == 128.

---

## SceAudioIn

Não há microfone no host. Proposta: **microfone virtual** que abre a porta e devolve silêncio no ritmo certo. A
porta abre, `Input` bloqueia `grain/freq` segundos e preenche zeros, e `GetStatus(MUTE)` diz "não mudo". Hoje
`OpenPort` falha com 0x80260106 (INVALID_PORT_PARAM) e `Input` com 0x80260107 (PORT_FULL); os dois códigos são
inadequados. Códigos de `psp2common/audioin.h`.

Estado novo em `Audio.cs` (ou classe `AudioIn` separada): `Dictionary<int, InPort>` com `{Type, Grain, Freq,
NextDeadline, MicGain = 0x1060}`, `bool _muted = false` e `int _inputRoute = 0` (BUILTIN_MIC).

### sceAudioInGetStatus
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceAudioIn · **NID:** 0x2F940377
- **Protótipo:** `int sceAudioInGetStatus(int select);` (`psp2/audioin.h`)
- **Comportamento:** com `select = SCE_AUDIO_IN_GETSTATUS_MUTE` (1), diz se o microfone do sistema está mudo.
  Invizimals chama em `0x814FF94A` com `select = 1` e trata `== 1` como mudo (a função dele devolve bool). Não depende
  de porta aberta.
- **Retorno/erros:** 1 = mudo, 0 = não mudo. Seletor diferente de 1: `SCE_AUDIO_IN_ERROR_INVALID_PARAMETER`
  (0x8026010B) para processos com SDK ≥ 1.80 e `SCE_AUDIO_IN_ERROR_INVALID_PORT_PARAM` (0x80260106) para SDK < 1.80. Usar
  0x8026010B; os 7 jogos instalados são de SDK ≥ 1.80, a confirmar lendo `sdk_version` no `SceProcessParam`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Audio.cs`: `[Hle] int sceAudioInGetStatus(int select) => select == 1 ? (_muted ? 1 : 0) : unchecked((int)0x8026010B);`
  Devolver 0 (não mudo) é coerente com o microfone virtual. Se o projeto preferir anunciar "sem microfone", devolver 1
  e manter `OpenPort` falhando; nesse caso, ver no Invizimals qual caminho ele toma.
- **Validação:** `imports PCSA00552` sem a função; cenário `inv_boot.txt` sem `Unimplemented HLE function`.

### sceAudioInOpenPort
- [ ] **Prioridade:** STUB (Invizimals, Devious Dungeon) · **Biblioteca:** SceAudioIn · **NID:** 0x39B50DC1
- **Protótipo:** `int sceAudioInOpenPort(SceAudioInPortType portType, int grain, int freq, SceAudioInParam param);` (`psp2/audioin.h`)
- **Comportamento:** abre a porta de captura. Regras do FW 3.60 (header): VOICE (0) aceita 256 ou 512 amostras a
  16000 Hz; RAW (2) aceita 256 a 16000 Hz ou 768 a 48000 Hz; CAMERA (0x20) aceita 256/512 a 16000 Hz. `param` precisa
  ser 0 (S16 mono). Uma porta por processo, quatro no total.
- **Retorno/erros:** handle entre 0x80 e 0xFF. INVALID_PORT_TYPE 0x80260104; INVALID_SAMPLE_FREQ 0x80260103;
  INVALID_SIZE 0x80260102 (grain inválido; o Vita3K usa INVALID_PARAMETER, mas o header define INVALID_SIZE para isso);
  INVALID_PORT_PARAM 0x80260106 (`param ≠ 0`); PORT_FULL 0x80260107 (o processo já tem porta).
- **Estruturas:** —
- **Implementação no VitaSharp:** validar como acima e devolver `0x80`. Não abrir dispositivo no host.
- **Validação:** `dd_boot.txt` sem regressão; ver no log o handle 0x80.

### sceAudioInInput
- [ ] **Prioridade:** STUB · **Biblioteca:** SceAudioIn · **NID:** 0x638ADD2D
- **Protótipo:** `int sceAudioInInput(int port, void *destPtr);` (`psp2/audioin.h`)
- **Comportamento:** bloqueia até haver `grain` amostras e escreve `grain*2` bytes de PCM S16 mono.
- **Retorno/erros:** 0; NOT_OPENED 0x80260109 se a porta não estiver aberta; INVALID_POINTER 0x80260105 se `destPtr`
  for NULL.
- **Estruturas:** —
- **Implementação no VitaSharp:** zerar `grain*2` bytes e controlar o ritmo como o caminho "sem dispositivo" do
  `sceAudioOutOutput` (`NextDeadline += grain/freq`, `Scheduler.Block()` + `PreciseSleep`; sem espera em
  `GuestClock.Turbo`). Classificar como espera em `HleFunction.ClassifyCost` (README, passo 4).
- **Validação:** cronometrar 100 chamadas com grain 256 @16 kHz: ~1,6 s.

### sceAudioInReleasePort
- [ ] **Prioridade:** STUB · **Biblioteca:** SceAudioIn · **NID:** 0x3A61B8C4
- **Protótipo:** `int sceAudioInReleasePort(int port);` (`psp2/audioin.h`)
- **Comportamento:** libera a porta.
- **Retorno/erros:** 0; NOT_OPENED 0x80260109 se não estiver aberta.
- **Estruturas:** —
- **Implementação no VitaSharp:** remover do dicionário.
- **Validação:** Open → Release → Open devolve 0x80 de novo.

### sceAudioInInputWithInputDeviceState
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudioIn · **NID:** 0x343E8251
- **Protótipo:** `int sceAudioInInputWithInputDeviceState(int port, void *dest_ptr, int *input_device_state);` (`psp2/audioin.h`)
- **Comportamento:** igual a `Input`. Em portas com relato de dispositivo (perfil CAMERA), escreve 1 nas duas primeiras
  capturas depois de uma troca de rota e 0 depois disso. Em outros perfis não toca no ponteiro.
- **Retorno/erros:** os de `Input`.
- **Estruturas:** —
- **Implementação no VitaSharp:** chamar o caminho de `Input`. Se `Type == 0x20`, escrever 1 nas duas primeiras
  capturas após `OpenPort`/`SelectInput` e 0 depois.
- **Validação:** teste unitário.

### sceAudioInGetAdopt
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudioIn · **NID:** 0x566AC433
- **Protótipo:** `int sceAudioInGetAdopt(SceAudioInPortType portType);` (`psp2/audioin.h`)
- **Comportamento:** 1 se o processo tem porta e é o processo de entrada adotado. Só aceita VOICE e RAW.
- **Retorno/erros:** 1/0; INVALID_PORT_TYPE 0x80260104 para outros tipos.
- **Estruturas:** —
- **Implementação no VitaSharp:** `portType is 0 or 2 ? (porta aberta ? 1 : 0) : 0x80260104`.
- **Validação:** teste unitário.

### sceAudioInGetInput / sceAudioInSelectInput
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudioIn · **NID:** 0x08105392 (Get) / 0xA0EB852F (Select)
- **Protótipo:** `int sceAudioInGetInput(void);` / `int sceAudioInSelectInput(SceAudioInInputMode input_mode);` (`psp2/audioin.h`)
- **Comportamento:** lê/escolhe a rota de entrada (-1 NONE, 0 BUILTIN_MIC, 1 HEADSET_MIC, 2 CODEC_2, 0x10 BLUETOOTH,
  0x20 VITA_TV). `Select` não rejeita valores fora do enum.
- **Retorno/erros:** Get devolve `_inputRoute`. Select devolve 0 se a rota já estiver selecionada; senão, no FW, o
  resultado da notificação ao worker. Devolver 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** campo `_inputRoute` (padrão 0). Select grava e reinicia o contador de relato do perfil
  CAMERA.
- **Validação:** teste unitário.

### sceAudioInSetMute
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudioIn · **NID:** 0x1DFE7698
- **Protótipo:** `int sceAudioInSetMute(SceAudioInMuteCommand command);` (`psp2/audioin.h`)
- **Comportamento:** 1 = MUTE, 2 = UNMUTE. Outros valores são ignorados.
- **Retorno/erros:** para comando reconhecido, as flags internas atualizadas (layout no FW a confirmar; devolver
  `_muted ? 1 : 0`); comando desconhecido devolve o próprio `command`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_muted = command == 1` (para 1 ou 2). Reflete em `GetStatus`.
- **Validação:** SetMute(1) e depois GetStatus(1) == 1.

### sceAudioInSetMicGain / sceAudioInGetMicGain
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudioIn · **NID:** 0x0F34DD73 (Set) / 0x86118097 (Get)
- **Protótipo:** `int sceAudioInSetMicGain(int port, int gain);` / `int sceAudioInGetMicGain(int port);` (`psp2/audioin.h`)
- **Comportamento:** guarda os 16 bits baixos de `gain` na porta (valor inicial 0x1060). O FW não usa o valor para nada.
- **Retorno/erros:** Set: 0. Get: o valor com extensão de sinal de 16 bits. Porta inválida ou de outro processo:
  INVALID_PORT_TYPE 0x80260104.
- **Estruturas:** —
- **Implementação no VitaSharp:** `InPort.MicGain = (short)gain`.
- **Validação:** teste unitário: Get logo após Open == 0x1060.

### FORA: sceAudioInOpenPortForDiag
- [ ] **Prioridade:** FORA · **Biblioteca:** SceAudioIn · **NID:** 0xC6962E84
- **Protótipo:** `int sceAudioInOpenPortForDiag(SceAudioInPortType port_type, int grain, int freq, SceAudioInParam param);` (`psp2/audioin.h`)
- **Comportamento:** entrada de diagnóstico: igual a `OpenPort` e também aceita DIAG (14) sem validar grain/freq. Jogos
  não usam.
- **Retorno/erros:** se aparecer, delegar a `OpenPort`, aceitando o tipo 14.
- **Estruturas:** —
- **Implementação no VitaSharp:** nenhuma.
- **Validação:** —

---

## SceAudiodecUser

Tudo em `Audiodec.cs`. Códigos de `psp2/audiodec.h` (0x807F0000..0x807F0013).

### sceAudiodecDecodeNStreams
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudiodecUser · **NID:** 0x68F4A9CB
- **Protótipo:** `SceInt32 sceAudiodecDecodeNStreams(SceAudiodecCtrl *pCtrls[], SceUInt32 nStreams);` (`psp2/audiodec.h`)
- **Comportamento:** decodifica um quadro em cada um dos `nStreams` decodificadores, numa chamada só (uso: várias trilhas
  sincronizadas). Atenção: o argumento é um array de **ponteiros**. O Vita3K trata como ponteiro único e não implementa.
- **Retorno/erros:** 0. `SCE_AUDIODEC_ERROR_INVALID_PTR` (0x807F0008) se o array ou algum elemento for NULL;
  `INVALID_NSTREAMS` (0x807F0010) se `nStreams == 0` (limite máximo a confirmar); `DIFFERENT_TYPES` (0x807F0011) se os
  codecs diferirem; `SAME_HANDLES` (0x807F0012) se dois ctrls tiverem o mesmo handle. Erro de um stream: devolver o
  primeiro erro (a confirmar se o FW para ou continua).
- **Estruturas:** `SceAudiodecCtrl` (0x28; layout no cabeçalho de `Audiodec.cs`).
- **Implementação no VitaSharp:** ler `nStreams` ponteiros de `pCtrls`, validar com `Find`, comparar `Decoder.Type` e
  handles, e chamar `DecodeFrames(ctrl, 1)` em cada um.
- **Validação:** teste unitário com dois decoders AT9 do mesmo arquivo.

### sceAudiodecPartlyDecode
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudiodecUser · **NID:** 0x2742E642
- **Protótipo:** inferido (Vita3K): `SceInt32 sceAudiodecPartlyDecode(SceAudiodecCtrl *pCtrl, SceUInt32 samplesOffset, SceUInt32 samplesToDecode);`
- **Comportamento:** usada pela libatrac (LLE) para decodificar parte de um superquadro ATRAC9. Decodifica quadros a
  partir de `pEs` até ter `samplesOffset + samplesToDecode` amostras e grava em `pPcm` só a faixa
  `[samplesOffset, samplesOffset + samplesToDecode)`. O Vita3K escreve essa faixa na posição
  `pPcm + samplesOffset*bytesPorAmostra`, não no início. Confirmar qual dos dois o FW faz desmontando a libatrac
  (`app0:sce_module` de um jogo que use `SCE_SYSMODULE_ATRAC`) no ponto de chamada. Atualiza `inputEsSize` (+12) com os
  bytes consumidos e `outputPcmSize` (+24).
- **Retorno/erros:** 0; INVALID_PTR / INVALID_SIZE (`ctrl->size != 0x28`); API_FAIL (0x807F0000) em erro de quadro.
- **Estruturas:** `SceAudiodecCtrl`.
- **Implementação no VitaSharp:** generalizar `DecodeFrames` com um buffer temporário de PCM e copiar a faixa. Só AT9;
  para outros codecs, `Log.WarnOnce`, como faz o Vita3K.
- **Validação:** jogo com libatrac; nenhum dos 7 instalados importa.

### sceAudiodecCreateDecoderResident / sceAudiodecDeleteDecoderResident
- [ ] **Prioridade:** P1 · **Biblioteca:** SceAudiodecUser · **NID:** 0x98B44E1B (Create) / 0x164DAEDA (Delete)
- **Protótipo:** inferido: `SceInt32 sceAudiodecCreateDecoderResident(SceAudiodecCtrl *pCtrl, SceUInt32 codecType);` e
  `SceInt32 sceAudiodecDeleteDecoderResident(SceAudiodecCtrl *pCtrl);`. Sem protótipo no vita-headers e não
  implementadas no Vita3K: a confirmar pela desmontagem do SceAudiodecUser do FW.
- **Comportamento:** cria/remove um decodificador em memória residente do sistema (sem `InitLibrary`). Do lado do host,
  é igual a `CreateDecoderExternal`.
- **Retorno/erros:** os de `CreateDecoder`/`DeleteDecoder`.
- **Estruturas:** `SceAudiodecCtrl`.
- **Implementação no VitaSharp:** `Create(ctrl, codecType, needsLibrary: false)` e `sceAudiodecDeleteDecoder(ctrl)`.
- **Validação:** só quando algum jogo importar (confirmar os argumentos no ponto de chamada).

---

## SceVideodecUser

Nenhum dos 7 jogos chama as 53 funções desta lista (F1 usa só as 9 já implementadas). Seção comum:

- Arquivo: `Videodec.cs`. Os erros de AVC (`0x80620001..0x8062000E`) e de Videodec (`0x80620801..0x8062080E`) estão em
  `psp2/videodec.h`. O código atual usa os de AVC.
- Nenhuma das funções abaixo tem protótipo no vita-headers, e o Vita3K não implementa nenhuma (as `M4vdecQuery*` devolvem
  o número fixo 53). Os protótipos são **inferidos** por analogia com a API AVC documentada e precisam ser confirmados
  com um dump do FW 3.60 (módulo SceVideodecUser, não disponível nesta máquina: `fs/vs0` e `fs/os0` estão vazios) ou no
  ponto de chamada do primeiro jogo que importar.
- **M4V (MPEG-4 Part 2):** decodificar com FFmpeg `AV_CODEC_ID_MPEG4`, criando um `H264Decoder` genérico ou um novo
  `M4vDecoder` em `Media/`. Saída pelo mesmo `WritePicture`. O valor de `codecType` para M4V (presumivelmente
  `SCE_VIDEODEC_TYPE_HW_M4VDEC`) está a confirmar e não está no header (só `HW_AVCDEC = 0x1001`).
- **Variantes `Internal` e `Nongameapp`:** FORA. São as entradas usadas por módulos do sistema (avplayer do VSH, apps
  não-jogo como Video/Browser). Um jogo importa a versão pública. Se uma aparecer, criar alias para a pública
  correspondente.

### Funções P1 (API de jogo)

| [ ] | Função | NID | Protótipo (inferido) | Implementação proposta |
| --- | --- | --- | --- | --- |
| [ ] | sceVideodecSetConfig | 0x9B871779 | `int (SceVideodecType codec, const void *config)` (a confirmar) | Validar `codec`; ignorar config; 0. |
| [ ] | sceAvcdecSetDecodeMode | 0x75803C83 | `int (SceAvcdecCtrl *ctrl, int mode)` (a confirmar) | Guardar em `Decoder`; 0. |
| [ ] | sceAvcdecSetInterlacedStreamMode | 0x2DD70FC8 | `int (SceVideodecType codec, int mode)` (a confirmar) | 0 (o FFmpeg lida com entrelaçado). |
| [ ] | sceAvcdecSetRecoveryPointSEIMode | 0x58C620EC | `int (SceVideodecType codec, int mode)` (a confirmar) | 0. |
| [ ] | sceAvcdecCsc | 0xB2A428DB | `int (const SceAvcdecCtrl*, const SceAvcdecFrame *src, SceAvcdecFrame *dst)` (a confirmar) | Converter `src` YUV para o `pixelType` de `dst` com `ToRgb`. |
| [ ] | sceAvcdecDecodeNalAu | 0x6C68A38F | `int (const SceAvcdecCtrl*, const SceAvcdecAu*, SceAvcdecArrayPicture*)` (a confirmar) | Igual a `sceAvcdecDecode`. Se o AU vier com prefixo de tamanho NAL em vez de start code, converter para Annex B antes do `Send`. |
| [ ] | sceAvcdecDecodeWithWorkPicture | 0xBA5A009B | `int (ctrl, au, SceAvcdecArrayPicture *work, SceAvcdecArrayPicture *out)` (a confirmar) | `sceAvcdecDecode` gravando em `out`; `work` ignorado. |
| [ ] | sceAvcdecDecodeNalAuWithWorkPicture | 0xDDF41798 | idem | Combinação das duas anteriores. |
| [ ] | sceAvcdecDecodeStopWithWorkPicture | 0x67AD42C3 | `int (ctrl, SceAvcdecArrayPicture *work, SceAvcdecArrayPicture *out)` (a confirmar) | `sceAvcdecDecodeStop(ctrl, out)`. |
| [ ] | sceM4vdecQueryDecoderMemSize | 0xC67C1A80 | `int (SceVideodecType codec, const SceM4vdecQueryDecoderInfo*, SceM4vdecDecoderInfo*)` (a confirmar) | Mesmo cálculo de `sceAvcdecQueryDecoderMemSize`. |
| [ ] | sceM4vdecCreateDecoder | 0x17C6AC9E | `int (SceVideodecType codec, SceM4vdecCtrl*, const SceM4vdecQueryDecoderInfo*)` (a confirmar) | Como `sceAvcdecCreateDecoder` com MPEG-4. |
| [ ] | sceM4vdecDecode | 0x624664DB | `int (const SceM4vdecCtrl*, const SceM4vdecAu*, SceM4vdecArrayPicture*)` (a confirmar) | Como `sceAvcdecDecode`. |
| [ ] | sceM4vdecDecodeWithWorkPicture | 0x8F36574D | ver AVC | ver AVC |
| [ ] | sceM4vdecDecodeStop | 0x87CFD23B | `int (ctrl, array)` | Como `sceAvcdecDecodeStop`. |
| [ ] | sceM4vdecDecodeStopWithWorkPicture | 0xC1CF351F | ver AVC | ver AVC |
| [ ] | sceM4vdecDecodeFlush | 0x7C460D75 | `int (ctrl)` | `Codec.Reset()`. |
| [ ] | sceM4vdecDecodeAvailableSize | 0xA8CF1942 | `int (ctrl)` | `MaxWidth*MaxHeight*3/2`. |
| [ ] | sceM4vdecDeleteDecoder | 0x0EB2E4E7 | `int (ctrl)` | Como `sceAvcdecDeleteDecoder`. |
| [ ] | sceM4vdecCsc | 0xB4BC325B | ver `sceAvcdecCsc` | ver `sceAvcdecCsc` |

Validação: por enquanto só teste unitário com um `.m4v` curto. Com um jogo que importe, cenário de vídeo análogo a
`f1_movie.txt`.

### FORA: variantes Internal / Nongameapp

Motivo comum: entradas de módulos do sistema e de apps não-jogo; nenhum jogo instalado importa. Se aparecer, criar alias
para a função pública indicada.

| [ ] | Função | NID | Alias público |
| --- | --- | --- | --- |
| [ ] | sceVideodecInitLibraryInternal | 0x0FD286E0 | sceVideodecInitLibrary |
| [ ] | sceVideodecInitLibraryNongameapp | 0x26F3C35D | sceVideodecInitLibrary |
| [ ] | sceVideodecInitLibraryWithUnmapMemInternal | 0x36ADE1E5 | sceVideodecInitLibraryWithUnmapMem |
| [ ] | sceVideodecInitLibraryWithUnmapMemNongameapp | 0x108C1FEC | sceVideodecInitLibraryWithUnmapMem |
| [ ] | sceVideodecQueryMemSizeInternal | 0xEB48554F | sceVideodecQueryMemSize |
| [ ] | sceVideodecQueryMemSizeNongameapp | 0x2D9AE981 | sceVideodecQueryMemSize |
| [ ] | sceVideodecQueryInstanceNongameapp | 0xEF03E31B | — (sem equivalente público; a confirmar) |
| [ ] | sceVideodecSetConfigInternal | 0x4A9CB138 | sceVideodecSetConfig |
| [ ] | sceAvcdecCreateDecoderInternal | 0x808FECBD | sceAvcdecCreateDecoder |
| [ ] | sceAvcdecCreateDecoderNongameapp | 0x3102196C | sceAvcdecCreateDecoder |
| [ ] | sceAvcdecQueryDecoderMemSizeInternal | 0x7BC8DFEA | sceAvcdecQueryDecoderMemSize |
| [ ] | sceAvcdecQueryDecoderMemSizeNongameapp | 0xFA072F66 | sceAvcdecQueryDecoderMemSize |
| [ ] | sceAvcdecCscInternal | 0xC300BF46 | sceAvcdecCsc |
| [ ] | sceAvcdecDecodeAuInternal | 0x2385A82B | sceAvcdecDecode (decodificação em duas fases AU/GetPicture) |
| [ ] | sceAvcdecDecodeAuNongameapp | 0x867C75AD | idem |
| [ ] | sceAvcdecDecodeAuNalAuInternal | 0x68628BC4 | sceAvcdecDecodeNalAu |
| [ ] | sceAvcdecDecodeAuNalAuNongameapp | 0x0E4A6EE3 | sceAvcdecDecodeNalAu |
| [ ] | sceAvcdecDecodeGetPictureInternal | 0x63237A8F | segunda fase de DecodeAu (`Output`) |
| [ ] | sceAvcdecDecodeGetPictureNongameapp | 0xCF3CFC70 | idem |
| [ ] | sceAvcdecDecodeGetPictureWithWorkPictureInternal | 0x66F65155 | idem com work picture |
| [ ] | sceAvcdecDecodeSetTrickModeNongameapp | 0x7AEB56AB | — (trick play do player de vídeo) |
| [ ] | sceAvcdecDecodeSetUserDataSei1FieldMemSizeNongameapp | 0xD037206A | — (SEI de user data) |
| [ ] | sceAvcdecGetSeiPictureTimingInternal | 0x8EB07E2F | — |
| [ ] | sceAvcdecGetSeiUserDataNongameapp | 0xD6B26396 | — |
| [ ] | sceAvcdecRegisterCallbackInternal | 0x8FA94451 | — (callbacks de decodificação do sistema) |
| [ ] | sceAvcdecRegisterCallbackNongameapp | 0x497F7EC7 | — |
| [ ] | sceAvcdecUnregisterCallbackInternal | 0x24E905BF | — |
| [ ] | sceAvcdecUnregisterCallbackNongameapp | 0xE07A91B5 | — |
| [ ] | sceAvcdecUnregisterCallbackWithCbidInternal | 0x001069FB | — |
| [ ] | sceAvcdecUnregisterCallbackWithCbidNongameapp | 0x7761522A | — |
| [ ] | sceAvcdecSetDecodeModeInternal | 0xF57CC2BC | sceAvcdecSetDecodeMode |
| [ ] | sceAvcdecSetLowDelayModeNongameapp | 0xD57F82C1 | — |
| [ ] | sceM4vdecCreateDecoderInternal | 0x4D4CA34E | sceM4vdecCreateDecoder |
| [ ] | sceM4vdecQueryDecoderMemSizeInternal | 0x84DC8101 | sceM4vdecQueryDecoderMemSize |

---

## SceCamera

### Seção comum: câmera virtual

Objetivo: o jogo vê uma câmera funcionando que entrega quadros neutros. Assim o modo AR do Invizimals avança até o ponto
em que libface/libsmart (LLE) analisam a imagem e devolvem "nada detectado". Devious Dungeon (PCSB01240) também abre a
câmera e usa 15 setters.

- Arquivo: `Online.cs`, classe `Sensors` (ou um novo `Camera.cs`). Estado por dispositivo (`devnum` 0 = frente,
  1 = trás): `Opened`, `Started`, cópia de `SceCameraInfo`, `FrameIndex`, `NextFrameUs` e um dicionário de atributos.
- Validações comuns: `devnum` fora de 0/1 → `SCE_CAMERA_ERROR_PARAM` (0x802E0000). Ponteiro de saída NULL →
  0x802E0000. Códigos de `psp2/camera.h`. **Nunca** devolver 0x802E000B (EXCLUSIVE) como "não encontrada".
- Getters/setters de atributo exigem câmera aberta e iniciada: senão NOT_OPEN 0x802E0004 ou NOT_START 0x802E0006 (Vita3K
  `camera_get_attribute`). **Exceção:** `SetGain`/`GetGain` devolvem sempre ATTRIBUTE_UNKNOWN 0x802E000C depois de
  validar `devnum`/ponteiro ("code from disasm" no Vita3K: ganho não pode ser lido nem escrito no Vita).
- Quadro neutro: Y = 128, U = V = 128 (cinza médio) nos formatos YUV; ARGB/ABGR = 0xFF808080; RAW8 = 128. Cinza
  uniforme não tem bordas nem cantos, então o detector de rostos e o mapeador de cena não acham nada. Opcional para
  depuração: uma imagem estática configurável, como o `CameraType::Image` do Vita3K.
- Ritmo: um quadro novo a cada `1/framerate` s. `SCE_CAMERA_FRAMERATE_3_FPS` significa 3,75 fps e `7` significa 7,5 fps.
- Plataforma: o VitaSharp emula o portátil, então não há o caminho `NOT_MOUNTED` (0x802E0010) do PSTV.

Valores-padrão dos atributos (Vita3K `camera/src/camera.cpp` `Camera::get_attribute`):

| Atributo | Padrão | Faixa/enum (`psp2/camera.h`) |
| --- | --- | --- |
| Saturation | 10 (`SCE_CAMERA_SATURATION_10`) | 0,5,10,20,30,40 |
| Brightness | 128 | 0..255 |
| Contrast | 128 | 0..255 |
| Sharpness | 1 (`SHARPNESS_100`) | 1..4 |
| Reverse | 0 | 0..3 |
| Effect | 0 | 0..6 |
| EV | 0 | -20..20 |
| Zoom | 10 | 10..40 |
| AntiFlicker | 1 (AUTO) | 1..3 |
| ISO | 1 (AUTO) | 1,100,200,400 |
| WhiteBalance | 0 (AUTO) | 0,1,2,4 |
| Backlight | 0 | 0/1 |
| Nightmode | 0 | 0..3 |
| ExposureCeiling | 0 | a confirmar |
| AutoControlHold | 0 | a confirmar |
| ImageQuality | 0 | a confirmar |
| NoiseReduction | 0 | a confirmar |
| SharpnessOff | 0 | a confirmar |

Os setters gravam o valor no dicionário, e o getter correspondente o devolve. Faixa de cada atributo: o Vita3K não
valida (devolve 0 sempre). Validar pelos enums da tabela e devolver PARAM em caso de valor inválido é a opção mais fiel
(a confirmar com o FW).

### sceCameraOpen
- [ ] **Prioridade:** STUB (Invizimals, Devious Dungeon) · **Biblioteca:** SceCamera · **NID:** 0xA462F801
- **Protótipo:** `int sceCameraOpen(int devnum, SceCameraInfo *pInfo);` (`psp2/camera.h`)
- **Comportamento:** valida e guarda a configuração, e preenche `width`/`height` a partir de `resolution`. Hoje devolve
  sempre 0x802E000B.
- **Retorno/erros:** 0. PARAM 0x802E0000 para: `pInfo == NULL`; `size` fora de 0x2C..0x30; `priority` ∉ {0,1};
  `buffer` ∉ {0 = SETBYOPEN, 1 = SETBYREAD}; e, em SETBYOPEN, `pIBase == NULL` ou `sizeIBase == 0` (e U/V nos formatos
  planares 1 e 3). FORMAT_UNKNOWN 0x802E0007 se `format == 0` ou `> 6`. BAD_FRAMERATE 0x802E0009 fora de
  {3,5,7,10,15,20,30,60,120}. DATA_RANGE_UNKNOWN 0x802E0011 se `range` ∉ {0 = FULL, 1 = BT601}. RESOLUTION_UNKNOWN
  0x802E0008 fora de {1..6, 8}. ALREADY_OPEN 0x802E0003. Regras do Vita3K `SceCamera.cpp`, comentadas como "from disasm".
- **Estruturas:** `SceCameraInfo` (0x30): +0x00 size, +0x04 priority (u16), +0x06 format (u16), +0x08 resolution
  (u16), +0x0A framerate (u16), +0x0C width (u16, saída), +0x0E height (u16, saída), +0x10 range (u16), +0x12 pad,
  +0x14 sizeIBase, +0x18 sizeUBase, +0x1C sizeVBase, +0x20 pIBase, +0x24 pUBase, +0x28 pVBase, +0x2C pitch (u16),
  +0x2E buffer (u16). Resoluções: 1 = 640×480, 2 = 320×240, 3 = 160×120, 4 = 352×288, 5 = 176×144, 6 = 480×272,
  8 = 640×360.
- **Implementação no VitaSharp:** classe `CameraDevice` em `Sensors`; copiar `min(size, 0x30)` bytes.
- **Validação:** Invizimals/DD não devem mais logar erro de câmera; o cenário `inv_boot.txt` entra no AR.

### sceCameraStart / sceCameraStop
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCamera · **NID:** 0xA8FEAE35 (Start) / 0x1DD9C9CE (Stop)
- **Protótipo:** `int sceCameraStart(int devnum);` / `int sceCameraStop(int devnum);` (`psp2/camera.h`)
- **Comportamento:** começa/para a captura. Start zera `FrameIndex` e agenda o primeiro quadro.
- **Retorno/erros:** 0; NOT_OPEN 0x802E0004; Start já iniciado → ALREADY_START 0x802E0005; Stop não iniciado →
  NOT_START 0x802E0006.
- **Estruturas:** —
- **Implementação no VitaSharp:** flags em `CameraDevice`.
- **Validação:** sequência Open/Start/Stop/Close do Invizimals sem erro.

### sceCameraRead
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCamera · **NID:** 0x79B5C2DE
- **Protótipo:** `int sceCameraRead(int devnum, SceCameraRead *pRead);` (`psp2/camera.h`)
- **Comportamento:** entrega o quadro atual nos buffers (os do Open em SETBYOPEN, os do `pRead` em SETBYREAD). Com
  `mode = 0` (WAIT_NEXTFRAME_ON) bloqueia até o próximo quadro. Com `mode = 1` (OFF), se ainda não houver quadro novo,
  devolve 0 com `status = 1` (ALREADY_READ) e o `frame`/`timestamp` anteriores. Preenche `frame` (contador) e
  `timestamp` (µs).
- **Retorno/erros:** 0. PARAM se `pRead == NULL`, `size <= 24` ou `size > 0x38`; NOT_OPEN; NOT_START; PARAM em
  SETBYREAD com buffers nulos.
- **Estruturas:** `SceCameraRead` (0x38): +0x00 size, +0x04 mode, +0x08 pad, +0x0C status (0 ACTIVE, 1 ALREADY_READ,
  -1 NOT_ACTIVE), +0x10 frame (u64), +0x18 timestamp (u64), +0x20 sizeIBase, +0x24 sizeUBase, +0x28 sizeVBase,
  +0x2C pIBase, +0x30 pUBase, +0x34 pVBase.
- **Implementação no VitaSharp:** preencher o quadro neutro só na primeira vez e quando o formato mudar (a imagem não
  muda). Esperar com `Scheduler.Block()` + `PreciseSleep` até `NextFrameUs` (sem esperar em turbo). Classificar como
  espera em `ClassifyCost`.
- **Validação:** um log Debug do primeiro `Read` mostra formato/resolução pedidos pelo Invizimals; o dump de quadro do
  cenário mostra o fundo cinza da câmera na tela de AR.

### sceCameraIsActive
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCamera · **NID:** 0x103A75B8
- **Protótipo:** `int sceCameraIsActive(int devnum);` (`psp2/camera.h`)
- **Comportamento:** 1 se aberta e iniciada.
- **Retorno/erros:** 1; NOT_OPEN; NOT_START (Vita3K devolve os erros em vez de 0).
- **Estruturas:** —
- **Implementação no VitaSharp:** ver Comportamento.
- **Validação:** junto com Start.

### sceCameraClose
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCamera · **NID:** 0xCD6E1CFC
- **Protótipo:** `int sceCameraClose(int devnum);` (`psp2/camera.h`)
- **Comportamento:** fecha. Se estiver iniciada, para antes (a confirmar se o FW exige Stop antes).
- **Retorno/erros:** 0; NOT_OPEN.
- **Estruturas:** —
- **Implementação no VitaSharp:** resetar `CameraDevice`, mantendo os atributos (a confirmar se o FW os reinicia).
- **Validação:** Open → Close → Open sem ALREADY_OPEN.

### Família STUB: setters existentes de SceCamera

Hoje devolvem 0 e descartam o valor (Online.cs). Passam a gravar o atributo, e o getter P1 correspondente o devolve.
Protótipo comum: `int sceCameraSetX(int devnum, int value);` (`psp2/camera.h`). Erros comuns da seção.

| [ ] | Função | NID | Atributo |
| --- | --- | --- | --- |
| [ ] | sceCameraSetAntiFlicker | 0xE312958A | AntiFlicker |
| [ ] | sceCameraSetAutoControlHold | 0x3A0DABBD | AutoControlHold (Invizimals chama) |
| [ ] | sceCameraSetBacklight | 0xAE071044 | Backlight |
| [ ] | sceCameraSetBrightness | 0x98D71588 | Brightness |
| [ ] | sceCameraSetContrast | 0x06FB2900 | Contrast |
| [ ] | sceCameraSetEV | 0x62AFF0B8 | EV |
| [ ] | sceCameraSetEffect | 0xE9D2CFB1 | Effect |
| [ ] | sceCameraSetExposureCeiling | 0x04F34BEE | ExposureCeiling |
| [ ] | sceCameraSetISO | 0x3CF630A1 | ISO |
| [ ] | sceCameraSetNightmode | 0x3F26233E | Nightmode |
| [ ] | sceCameraSetReverse | 0x1175F477 | Reverse |
| [ ] | sceCameraSetSaturation | 0xF9F7CA3D | Saturation |
| [ ] | sceCameraSetSharpness | 0xD1A5BB0B | Sharpness |
| [ ] | sceCameraSetWhiteBalance | 0x4D4514AC | WhiteBalance |
| [ ] | sceCameraSetZoom | 0xF7464216 | Zoom |

Validação: Devious Dungeon chama 13 desses setters em sequência; nenhum deve falhar depois do Open/Start virtual.
Atenção: hoje eles "funcionam" porque devolvem 0 mesmo com a câmera fechada. Com a câmera virtual, um setter chamado
antes do Start devolve NOT_START, como no Vita3K. Ver no log se o DD chama algum antes do Start.

### Funções P1 de SceCamera (getters e setters novos)

Protótipo comum dos getters: `int sceCameraGetX(int devnum, int *pValue);`. Dos setters:
`int sceCameraSetX(int devnum, int value);` (`psp2/camera.h`). Comportamento: devolver o atributo (padrões na tabela
da seção comum). Erros comuns da seção. Validação comum: teste unitário Set→Get depois de Open/Start.

| [ ] | Função | NID | Observação |
| --- | --- | --- | --- |
| [ ] | sceCameraGetAntiFlicker | 0x9FDACB99 | |
| [ ] | sceCameraGetAutoControlHold | 0x06A21BBB | |
| [ ] | sceCameraGetBacklight | 0x8DD1292B | |
| [ ] | sceCameraGetBrightness | 0x85D5951D | |
| [ ] | sceCameraGetContrast | 0x8FBE84BE | |
| [ ] | sceCameraGetEffect | 0x7E8EF3B2 | |
| [ ] | sceCameraGetEV | 0x8B5E6147 | |
| [ ] | sceCameraGetExposureCeiling | 0x5FA5B1BB | |
| [ ] | sceCameraGetImageQuality | 0xE2AC7BCE | `int (int devnum, int* pLevel)` |
| [ ] | sceCameraGetISO | 0x4EBD5C68 | |
| [ ] | sceCameraGetNightmode | 0x12B6FF26 | |
| [ ] | sceCameraGetNoiseReduction | 0xFEB99ACC | `int (int devnum, int* pLevel)` |
| [ ] | sceCameraGetReverse | 0x44F6043F | |
| [ ] | sceCameraGetSaturation | 0x624F7653 | |
| [ ] | sceCameraGetSharpness | 0xAA72C3DC | |
| [ ] | sceCameraGetSharpnessOff | 0x34CCAF85 | `int (int devnum, int* pLevel)` |
| [ ] | sceCameraGetWhiteBalance | 0xDBFFA1DA | |
| [ ] | sceCameraGetZoom | 0x06D3816C | |
| [ ] | sceCameraGetGain | 0x2C36D6F3 | Sempre ATTRIBUTE_UNKNOWN 0x802E000C depois de validar `devnum`/`pValue` (sem exigir Open). |
| [ ] | sceCameraSetGain | 0xE65CFE86 | Sempre 0x802E000C depois de validar `devnum`. |
| [ ] | sceCameraSetImageQuality | 0x75C4300B | |
| [ ] | sceCameraSetNoiseReduction | 0xF9B79556 | |
| [ ] | sceCameraSetSharpnessOff | 0x4B5405C8 | |
| [ ] | sceCameraGetDeviceLocation | 0x274EF751 | `int (int devnum, SceFVector3 *pLocation)`. Posição física da câmera (mm). O Vita3K zera e marca não implementado. Valores reais a confirmar (um homebrew no hardware). Até lá, escrever (0,0,0) e devolver 0. Não exige Open. |

---

## ScePower

### Seção comum: bateria virtual fixa

Arquivo: `SystemMisc.cs` (seção ScePower). O VitaSharp já anuncia: AC conectado (`IsPowerOnline = 1`), 100 %,
2200 mAh, 300 min. Para a telemetria nova, valores fixos **escolhidos** (não medidos) e coerentes com uma bateria
Li-ion cheia de 3,7 V nominais, na tomada:

| Grandeza | Valor | Unidade | Fonte da unidade |
| --- | --- | --- | --- |
| Temperatura da bateria | 3000 | °C × 100 (30,00 °C) | `psp2/power.h` |
| Tensão | 4150 | mV | `psp2/power.h` |
| Saúde (SOH) | 100 | % | `psp2/power.h` |
| Ciclos | 10 | contagem | `psp2/power.h` |
| Nível restante | 4 | enum: 75–100 % | Vita3K `ScePowerBatteryRemainLevel` |
| Nível máximo | 4 | idem | Vita3K |
| Corrente ("Elec") | 0 | a confirmar (provavelmente mA, com sinal) | inferido do nome |
| Temperatura do gabinete | 3000 | a confirmar (supõe-se °C × 100, como a da bateria) | inferido |

Ajuste de coerência opcional nos stubs existentes: com 100 % e AC, `scePowerIsBatteryCharging` (hoje 1) deveria
devolver 0 (cheia). Nenhum jogo instalado depende disso. Códigos (`psp2common/power.h`): INVALID_VALUE 0x802B0000.

### Telemetria (P1)

Protótipo e retorno de cada linha. Sem erros (sempre o valor). Implementação: `[Hle] int nome() => valor;` em
`SystemMisc`. Validação: teste unitário.

| [ ] | Função | NID | Protótipo | Retorno |
| --- | --- | --- | --- | --- |
| [ ] | scePowerGetBatteryTemp | 0x28E12023 | `int (void)` (`psp2/power.h`) | 3000 |
| [ ] | scePowerGetBatteryVolt | 0x483CE86B | `int (void)` (`psp2/power.h`) | 4150 |
| [ ] | scePowerGetBatterySOH | 0xA88A2B65 | `int (void)` (`psp2/power.h`) | 100 |
| [ ] | scePowerGetBatteryCycleCount | 0x008A54B9 | `int (void)` (`psp2/power.h`) | 10 |
| [ ] | scePowerGetBatteryRemainLevel | 0xEA3E3715 | `int (void)` (inferido, Vita3K) | 4 |
| [ ] | scePowerGetBatteryRemainMaxLevel | 0x1DB00F10 | `int (void)` (inferido, Vita3K) | 4 |
| [ ] | scePowerGetBatteryElec | 0x862AE1A6 | `int (void)` (inferido) | 0 (bateria cheia, sem corrente de carga) |
| [ ] | scePowerGetCaseTemp | 0x525592E4 | `int (void)` (inferido) | 3000 |
| [ ] | scePowerGetBatteryChargingStatus | 0xB4432BC8 | `int (void)` (inferido) | 0. Enum desconhecido, a confirmar: desmontar o ScePower do FW ou ler num homebrew no hardware com o carregador ligado e desligado. |
| [ ] | scePowerIsLowBatteryInhibitUpdateDownload | 0xE3E45636 | `SceBool (void)` (inferido) | 0 |
| [ ] | scePowerIsLowBatteryInhibitUpdateReboot | 0x9AA05A48 | `SceBool (void)` (inferido) | 0 |
| [ ] | scePowerBatteryUpdateInfo | 0x27F3292C | `int (void)` (inferido) | 0 (pede releitura da bateria; nada a fazer) |

### scePowerSetUsingWireless / scePowerGetUsingWireless
- [ ] **Prioridade:** P1 · **Biblioteca:** ScePower · **NID:** 0x4D695C1F (Set) / 0xD541FF03 (Get)
- **Protótipo:** `int scePowerSetUsingWireless(SceBool enabled);` / `int scePowerGetUsingWireless(void);` (`psp2/power.h`)
- **Comportamento:** o jogo avisa se usa Wi-Fi (o perfil de energia muda). Get devolve o último valor.
- **Retorno/erros:** Set: 0; valor ≠ 0/1 → INVALID_VALUE 0x802B0000 (Vita3K). Get: 0/1. O padrão de hardware está a
  confirmar; usar 0 até que algum jogo chame Set.
- **Estruturas:** —
- **Implementação no VitaSharp:** campo `_usingWireless` em `SystemMisc`.
- **Validação:** teste unitário.

### Pedidos de energia (P1, no-op)

O VitaSharp não suspende nem reinicia. Cada pedido vira um `Log.Info` (`Log.Warn` para ColdReset) e devolve 0.
Implementação em `SystemMisc`. Validação: teste unitário.

| [ ] | Função | NID | Protótipo | Retorno |
| --- | --- | --- | --- | --- |
| [ ] | scePowerRequestSuspend | 0xAC32C9CC | `int (void)` (`psp2/power.h`, "always 0") | 0 (no-op) |
| [ ] | scePowerRequestStandby | 0x2B7C7CF4 | `int (void)` (`psp2/power.h`, "always 0") | 0 (no-op) |
| [ ] | scePowerRequestColdReset | 0x0442D852 | `int (void)` (`psp2/power.h`, "always 0") | 0 (no-op + Warn). No hardware o processo morre; se um jogo depender disso, tratar como `sceKernelExitProcess`. |
| [ ] | scePowerIsSuspendRequired | 0x78A1A796 | `SceBool (void)` (`psp2/power.h`) | 0 |
| [ ] | scePowerIsRequest | 0x7FA406DD | `SceBool (void)` (inferido) | 0 (nenhum pedido pendente) |
| [ ] | scePowerCancelRequest | 0xDB62C9CF | `int (void)` (inferido) | 0 |
| [ ] | scePowerSetIdleTimerCount | 0xB11EF149 | `int (int count)` (inferido) | 0 (sem timer de inatividade) |

---

## SceFace

### Seção comum: LLE via `libface.suprx`

- Módulo: `app0:sce_module/libface.suprx`, nome `SceFace`, biblioteca SceFace (NID de biblioteca 0xF5683CF7),
  23 exports. Cobre **todas** as 23 funções SceFace da lista. Imports: SceLibc (12), SceLibm (8), SceLibstdcxx (2),
  SceLibKernel (`__stack_chk_fail`, `__sce_aeabi_idiv0`, `__sce_aeabi_ldiv0`). Todos resolvidos hoje.
- Pré-requisito: [sceSysmoduleLoadModule](#scesysmoduleloadmodule-pré-requisito-de-facesmart). Com ele, as entradas
  abaixo ficam resolvidas sem código novo. Marcar o checkbox depois de validar com Invizimals.
- Fallback HLE (só se o `.suprx` não existir): criar `Face.cs`. Objetivo: "nenhum rosto": tamanhos de memória > 0,
  parâmetros default reais e contagem de resultados 0.
- Erros: base 0x808B0000. `sceFaceDetectionGetDefaultParam(NULL)` devolve **0x808B0002** (visto na desmontagem); nome
  inferido `SCE_FACE_ERROR_INVALID_PARAM`. As funções `*GetWorkingMemorySize` não devolvem erro: devolvem **0** para
  argumentos inválidos.
- Fluxo no Invizimals (desmontagem do eboot, `0x814D21D4..0x814D2720`):
  `sceSysmoduleLoadModule(0x38)` → `sceFaceDetectionGetWorkingMemorySize(160,120,160,dict)` → alocação do tamanho
  devolvido → se `[obj+0x4C] == 1`, `sceFaceAllPartsGetWorkingMemorySize(160,120,160,partsDict)` → alocação →
  `sceFaceDetectionGetDefaultParam(&param)` → o jogo sobrescreve `param+0x2C = 0.35f`, `+0x30 = 1`, `+0x34 = 1` → por
  quadro, `sceFaceDetectionEx` (busca global) ou `sceFaceDetectionLocal` (busca local, perto do último rosto) e, com
  rosto achado, `sceFaceAllParts`. Imagem: luminância 160×120, `rowstride` 160. Os dicionários vêm dos dados do jogo.
- Offsets dos exports (segmento 0 do libface, Thumb): AgeRangeEstimate +0x0001, AgeRangeIntegrate +0x0371, AllParts
  +0x0925, Attribute +0x1A91, DetectionGetDefaultParam +0x2351, Detection +0x23A1, DetectionEx +0x27B7, DetectionLocal
  +0x2D7D, IdentifyGetFeature +0x3F55, IdentifySimilarity +0x42AF, DetectionGetWorkingMemorySize +0x4519,
  PartsGetWorkingMemorySize +0x45C9, AllPartsGetWorkingMemorySize +0x4659, AttributeGetWorkingMemorySize +0x46E9,
  AgeGetWorkingMemorySize +0x4765, IdentifyGetWorkingMemorySize +0x47D1, Parts +0x483D, PartsEx +0x4C85,
  PartsResultCheck +0x5B8B, EstimatePoseRegion +0x5DD5, ShapeGetWorkingMemorySize +0x61EF, ShapeFit +0x6861,
  ShapeTrack +0x6CBB.

### sceFaceDetectionGetWorkingMemorySize
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceFace · **NID:** 0xF24B851D
- **Protótipo:** inferido (desmontagem + chamada): `SceInt32 sceFaceDetectionGetWorkingMemorySize(SceInt32 width, SceInt32 height, SceInt32 rowstride, const void *detectDict);`
- **Comportamento:** tamanho da memória de trabalho da detecção para uma imagem `width×height`. O código valida e
  devolve **0** se `rowstride < width`, `width <= 0`, `height <= 0`, `rowstride <= 0`, `detectDict == NULL` ou o
  dicionário for rejeitado por uma rotina interna. Senão devolve `f(width, height, rowstride, dict) + 0x3000` (com
  saturação: resultados acima de `INT_MAX` viram 0). Invizimals chama com (160, 120, 160, `[obj+0x9C]`) e aloca o valor.
- **Retorno/erros:** tamanho em bytes, ou 0.
- **Estruturas:** dicionário opaco (dado do jogo).
- **Implementação no VitaSharp:** LLE (seção comum). Fallback HLE: devolver 0 para os argumentos inválidos acima e, nos
  demais casos, um tamanho fixo. O valor real para (160,120,160) está a confirmar: com o LLE ligado, logar o retorno
  uma vez (`TraceHle` ou breakpoint no retorno da chamada em `0x814D21EA`) e usar esse número no fallback.
- **Validação:** `imports PCSA00552` lista SceFace como LLE; cenário `inv_boot.txt` sem `Unimplemented HLE function`.

### sceFaceAllPartsGetWorkingMemorySize
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceFace · **NID:** 0xCF07E1C4
- **Protótipo:** inferido: `SceInt32 sceFaceAllPartsGetWorkingMemorySize(SceInt32 width, SceInt32 height, SceInt32 rowstride, const void *partsDict);`
- **Comportamento:** como a anterior, para a detecção de partes. Valida os mesmos argumentos e verifica que a primeira
  palavra de `partsDict` é igual a uma constante mágica do módulo. Devolve `g(...) + 0xA50` ou 0 se inválido.
  `sceFacePartsGetWorkingMemorySize` (P1) é igual, com outra constante mágica e `+0x1800`. Invizimals chama com
  (160,120,160,`[obj+0xF4]`) só quando `[obj+0x4C] == 1`.
- **Retorno/erros:** bytes, ou 0.
- **Estruturas:** dicionário opaco com palavra mágica no offset 0.
- **Implementação no VitaSharp:** LLE. Fallback: tamanho fixo, obtido como descrito na entrada anterior.
- **Validação:** idem.

### sceFaceDetectionGetDefaultParam
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceFace · **NID:** 0xE5689BD1
- **Protótipo:** inferido: `SceInt32 sceFaceDetectionGetDefaultParam(SceFaceDetectionParam *param);`
- **Comportamento:** preenche os parâmetros-padrão da detecção. Valores lidos do código (libface +0x2350):
- **Retorno/erros:** 0; `param == NULL` → 0x808B0002.
- **Estruturas:** `SceFaceDetectionParam` (0x38 bytes). Os nomes dos campos são desconhecidos; os valores são os do
  módulo:

  | Offset | Tipo | Default | Observação |
  | --- | --- | --- | --- |
  | +0x00 | int | 2 | |
  | +0x04 | int | 0x38 (56) | igual ao tamanho da struct (significado a confirmar) |
  | +0x08 | float | 0.5 | |
  | +0x0C | float | 0.8409 (0x3F574BC7 ≈ 2^-0,25) | provável passo de escala da pirâmide |
  | +0x10 | int | 0 | |
  | +0x14 | int | 0 | |
  | +0x18 | int | 0 | |
  | +0x1C | int | 2 | |
  | +0x20 | int | 2 | |
  | +0x24 | float | 0.5 | |
  | +0x28 | float | 0.5 | |
  | +0x2C | float | 0.5 | Invizimals troca por 0.35 |
  | +0x30 | int | 1 | Invizimals grava 1 |
  | +0x34 | int | 0 | Invizimals troca por 1 |
- **Implementação no VitaSharp:** LLE. Fallback: escrever exatamente a tabela.
- **Validação:** teste unitário do fallback comparando com a tabela.

### sceFaceDetectionEx
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceFace · **NID:** 0xD9603C69
- **Protótipo:** inferido da chamada em `0x814D2640` (4 registradores e 7 palavras na pilha):
  `SceInt32 sceFaceDetectionEx(const SceUChar8 *img, SceInt32 width, SceInt32 height, SceInt32 rowstride, const void *detectDict, const SceFaceDetectionParam *param, SceFaceDetectionResult *results, SceInt32 maxResults, SceInt32 *numResults, void *workMem, SceInt32 workMemSize);`
  O Invizimals passa 160, 120, 160, `maxResults = 1`. Os nomes dos argumentos da pilha estão a confirmar; as posições
  vêm da chamada.
- **Comportamento:** detecta rostos na imagem inteira e grava até `maxResults` regiões, com o total em `*numResults`.
- **Retorno/erros:** 0 em sucesso. Códigos de erro 0x808B00xx (a confirmar pela desmontagem de libface +0x27B6).
- **Estruturas:** `SceFaceDetectionResult`: 8 floats (0x20 bytes) por rosto. O jogo copia 8 floats do resultado da
  busca local; o layout é o mesmo (presumivelmente região x/y/largura/altura, ângulos e score, a confirmar).
- **Implementação no VitaSharp:** LLE. Fallback "nada detectado": `*numResults = 0`, devolver 0, não tocar em `results`.
- **Validação:** com câmera virtual cinza, o jogo nunca chama `sceFaceAllParts` (ver log com `TraceHle`).

### sceFaceDetectionLocal
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceFace · **NID:** 0x7D71725D
- **Protótipo:** inferido da chamada em `0x814D25D8`: 4 registradores (img, 160, 120, 160), **4 floats em s0–s3**
  (0.8409, 1.5, 1.3, 0.1 no Invizimals) e 10 palavras na pilha: `[0]` dict, `[4]` 2, `[8]` 1, `[0xC]` região anterior
  (entrada), `[0x10]` 1, `[0x14]` resultado (saída, 0x20 bytes), `[0x18]` 1 (máximo de resultados?), `[0x1C]`
  `*numResults`, `[0x20]` workMem, `[0x24]` workMemSize.
  `SceInt32 sceFaceDetectionLocal(const SceUChar8 *img, SceInt32 w, SceInt32 h, SceInt32 rowstride, const void *dict, SceInt32 a5, SceFloat32 f0, SceFloat32 f1, SceFloat32 f2, SceFloat32 f3, SceInt32 a6, const SceFaceDetectionResult *prevRegion, SceInt32 a8, SceFaceDetectionResult *result, SceInt32 maxResults, SceInt32 *numResults, void *workMem, SceInt32 workMemSize);`
  (ordem C dos floats em relação aos ints da pilha: a confirmar. No AAPCS-VFP os floats vão para s0–s3, qualquer que
  seja a posição.)
- **Comportamento:** procura o rosto perto da região anterior (tracking).
- **Retorno/erros:** 0; erros 0x808B00xx a confirmar.
- **Estruturas:** como em `DetectionEx`.
- **Implementação no VitaSharp:** LLE. Fallback: `*numResults = 0`, 0. No binder, declarar os 4 `float` e os 14
  argumentos inteiros na ordem de pilha (README, passo 2).
- **Validação:** idem `DetectionEx`.

### sceFaceAllParts
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceFace · **NID:** 0xE521EB6F
- **Protótipo:** inferido da chamada em `0x814D26F0`: `SceInt32 sceFaceAllParts(const SceUChar8 *img, SceInt32 w, SceInt32 h, SceInt32 rowstride, const void *partsDict, SceInt32 a5 /*0*/, SceInt32 a6 /*10*/, SceInt32 a7 /*10*/, const SceFaceDetectionResult *face, SceFacePartsResult *parts, SceInt32 maxParts /*55*/, SceInt32 *numParts, void *workMem, SceInt32 workMemSize);`
- **Comportamento:** localiza as partes do rosto (olhos, boca etc.) dentro da região de um rosto detectado. O jogo
  percorre o resultado em registros de 0x30 bytes, procura os IDs de parte 0x9A e 0x9D e exige score ≥ 3.0.
- **Retorno/erros:** 0; erros a confirmar.
- **Estruturas:** `SceFacePartsResult`: 0x30 bytes por parte (ID, posição, score em offsets a confirmar).
- **Implementação no VitaSharp:** LLE. Fallback: `*numParts = 0`, 0. Sem rosto, o jogo nem chega aqui.
- **Validação:** idem.

### Funções P1 de SceFace (cobertas pelo LLE)

Todas exportadas por `libface.suprx` (offsets na seção comum). Nenhum jogo instalado importa. Sem protótipo público.
Fallback HLE, se um dia for preciso: `*GetWorkingMemorySize` devolve um tamanho fixo obtido do LLE; as funções de
análise gravam contagem 0 ou devolvem erro de "nada encontrado" (valores a confirmar desmontando cada export). Validação
comum: jogo que importe; até lá, nada.

| [ ] | Função | NID | Observação |
| --- | --- | --- | --- |
| [ ] | sceFaceDetection | 0xF3045394 | Versão simples de `DetectionEx` (sem struct de parâmetros). |
| [ ] | sceFaceParts | 0x87550392 | Partes de um rosto (subconjunto de `AllParts`). |
| [ ] | sceFacePartsEx | 0x70C9CF95 | |
| [ ] | sceFacePartsGetWorkingMemorySize | 0x707B9A1D | Mesmos argumentos de `AllPartsGetWorkingMemorySize`; `+0x1800`; outra palavra mágica. |
| [ ] | sceFacePartsResultCheck | 0xCC98B939 | Valida um resultado de partes. |
| [ ] | sceFaceAttribute | 0x67F0585A | Atributos (sorriso, olhos fechados...). |
| [ ] | sceFaceAttributeGetWorkingMemorySize | 0xA905A467 | |
| [ ] | sceFaceAgeGetWorkingMemorySize | 0x73E9791D | |
| [ ] | sceFaceAgeRangeEstimate | 0x17F3DC79 | |
| [ ] | sceFaceAgeRangeIntegrate | 0xB794C6CB | |
| [ ] | sceFaceIdentifyGetWorkingMemorySize | 0x4468B054 | |
| [ ] | sceFaceIdentifyGetFeature | 0xF852E35D | |
| [ ] | sceFaceIdentifySimilarity | 0x8F8E9FB1 | |
| [ ] | sceFaceEstimatePoseRegion | 0xF7E4EC1F | |
| [ ] | sceFaceShapeGetWorkingMemorySize | 0xC0812127 | |
| [ ] | sceFaceShapeFit | 0x64F45021 | |
| [ ] | sceFaceShapeTrack | 0x37704DE9 | |

---

## SceSmart

### Seção comum: LLE via `libsmart.suprx`

- Módulo: `app0:sce_module/libsmart.suprx`, nome `SceSmart`, biblioteca SceSmart (NID 0xC2D4E61A), 41 exports. Cobre
  41 das 44 funções da lista. **Faltam nesta versão** (não exportadas pelo libsmart do Invizimals):
  `sceSmartCreateInstantImageTarget`, `sceSmartSceneMappingSetCustomListener` e `sceSmartSceneMappingSetDispatchMode`,
  que vieram em SDKs posteriores. Um jogo que as importe trará um libsmart mais novo, que as exporta; logo, LLE também.
- Imports: SceLibc (28), SceLibm (25), SceLibstdcxx (10), SceLibKernel (`sceKernelCreate/Delete/Lock/UnlockLwMutex`,
  `sceKernelGetProcessTimeWide`, `__stack_chk_fail`, `__sce_aeabi_idiv0/ldiv0`). Todos resolvidos. O módulo **não cria
  threads**: o processamento pesado roda na thread do jogo que chama `sceSmartSceneMappingRunCore`.
- Pré-requisito: [sceSysmoduleLoadModule](#scesysmoduleloadmodule-pré-requisito-de-facesmart).
- Erros: base 0x808C0000 (nomes inferidos): `0x808C0000` não inicializado (`Release` sem `Init`), `0x808C0001` já
  inicializado, `0x808C0002` sem memória, `0x808C0004` falha ao liberar recurso interno, `0x808C0006` falha na
  inicialização interna (desmontagem de libsmart +0x0194 e +0x0280).
- Fluxo no Invizimals (`0x814FEE80..0x814FF0D0`): `sceSysmoduleLoadModule(0x39)` →
  `sceSmartInit(&{NULL, alloc, free})` → `sceSmartSceneMappingStart(5)` → por quadro,
  `sceSmartSceneMappingDispatchAndQuery(out, &in)`, `GetInitializationPointInfo(buf, 64)`,
  `GetLandmarkInfo(buf, 512)` → numa thread de trabalho do jogo: em troca de modo, `Stop()` e `Start(tabela[modo])` com
  a tabela {5,2,3,3,4} em `0x81921BE8`, ou `Reset()`; depois `RunCore()` → `PropagateResult(...)` (com o pose
  propagado pelo sensor de movimento) → no fim, `Stop()` e `Release()`. Os resultados ficam em dois buffers alternados
  de 0x2F58 bytes: consulta em +0x2048, número de pontos de inicialização em +0x2090, pontos (64 × 0x1C) em +0x2094 e
  landmarks (512 × 0x14) em +0x2798. Tamanhos por registro deduzidos dos offsets, a confirmar.
- Fallback HLE (`Smart.cs`, só sem o `.suprx`): "tracking perdido": Start/Stop/Reset/RunCore devolvem 0;
  DispatchAndQuery zera a saída; as consultas de pontos e landmarks devolvem 0. O valor de "estado = perdido" no
  resultado da consulta está a confirmar. O LLE é bem mais seguro aqui.
- Offsets dos exports (segmento 0, Thumb): Release +0x0195, Init +0x0281, CreateLearnedImageTarget +0x059F,
  DestroyTarget +0x07BD, GetTargetInfo +0x0939, SMStart +0x1347, SMStop +0x1707, SMEnableMask +0x182D,
  SMGetNumLandmarks +0x1873, SMGetNumNodePoints +0x18A9, SMGetLandmarkInfo +0x18DF, SMGetNodePointInfo +0x19C9,
  SMSaveMap +0x1A77, SMForceLocalize +0x1B27, SMSetDenseMapMode +0x1B8F, SMFixMap +0x1C13, SMLoadMap +0x1CA1, SMRun
  +0x1D51, SMDispatchAndQuery +0x1F5B, SMDispatchAndQueryWithMask +0x1FFB, SMRunCore +0x209B, SMPropagateResult
  +0x219B, SMRegisterTarget +0x257F, SMUnregisterTarget +0x27E7, SMReset +0x29AF, SMGetNumInitializationPoints
  +0x2B37, SMGetInitializationPointInfo +0x2B83, SMRemoveLandmark +0x2C89, TTStart +0x33FB, TTStop +0x3B27,
  TTRegisterTarget +0x3C7B, TTUnregisterTarget +0x4165, TTRun +0x433B, TTRun2 +0x4401, TTDispatchAndQuery +0x448F,
  TTRunWorker +0x46E1, TTGetResults +0x48E9, TTQuery +0x4C63, TTReset +0x4CB5, TTSetSearchPolicy +0x4D73, TTQuery2
  +0x4DEB (SM = SceneMapping, TT = TargetTracking).

### sceSmartInit
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceSmart · **NID:** 0x58979C84
- **Protótipo:** inferido: `SceInt32 sceSmartInit(const SceSmartInitParam *param);`
- **Comportamento:** cria o contexto global (0xC8 bytes alocados pelo alocador do usuário). `param` pode ser NULL. Se
  `param` e os dois ponteiros de função forem não nulos, usa o alocador do usuário; senão usa o padrão interno
  (libc). O Invizimals passa `{0, 0x814FF0F1, 0x814FF121}`.
- **Retorno/erros:** 0; `0x808C0001` já inicializado; `0x808C0006` falha da inicialização interna; `0x808C0002` sem
  memória.
- **Estruturas:** `SceSmartInitParam` (0xC): +0x00 `void *userData`, +0x04 `void *(*alloc)(void *userData, SceSize
  size)`, +0x08 `void (*free)(void *userData, void *ptr)` (assinaturas deduzidas: alloc recebe `(userData, 0xC8)`,
  free recebe `(userData, ptr)`).
- **Implementação no VitaSharp:** LLE. Fallback: flag global e 0. Não chamar o alocador do jogo.
- **Validação:** `imports PCSA00552` mostra SceSmart como LLE; cenário `inv_boot.txt`.

### sceSmartRelease
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceSmart · **NID:** 0x43DF6D9C
- **Protótipo:** inferido: `SceInt32 sceSmartRelease(void);`
- **Comportamento:** para scene mapping e target tracking se estiverem ativos, libera o contexto pelo `free` do usuário
  e volta ao estado não inicializado.
- **Retorno/erros:** 0; `0x808C0000` se não inicializado; `0x808C0004` se um recurso interno não puder ser liberado.
- **Estruturas:** —
- **Implementação no VitaSharp:** LLE. Fallback: limpar a flag.
- **Validação:** sair do modo AR e entrar de novo sem `0x808C0001` no Init.

### sceSmartSceneMappingStart
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceSmart · **NID:** 0xA3A82B99
- **Protótipo:** inferido: `SceInt32 sceSmartSceneMappingStart(SceInt32 mode);`
- **Comportamento:** inicia o mapeamento de cena (SLAM monocular) no modo `mode`. O Invizimals usa 5 na entrada e 2/3/4
  conforme a fase (tabela {5,2,3,3,4}). O significado dos modos está a confirmar.
- **Retorno/erros:** 0; erros 0x808C00xx a confirmar (provavelmente "não inicializado" e "já iniciado").
- **Estruturas:** —
- **Implementação no VitaSharp:** LLE. Fallback: 0.
- **Validação:** idem.

### sceSmartSceneMappingStop
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceSmart · **NID:** 0x52EB57E4
- **Protótipo:** inferido: `SceInt32 sceSmartSceneMappingStop(void);`
- **Comportamento:** para o mapeamento.
- **Retorno/erros:** 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** LLE. Fallback: 0.
- **Validação:** idem.

### sceSmartSceneMappingReset
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceSmart · **NID:** 0xC1AD43BC
- **Protótipo:** inferido: `SceInt32 sceSmartSceneMappingReset(void);`
- **Comportamento:** descarta o mapa e volta à fase de inicialização sem parar.
- **Retorno/erros:** 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** LLE. Fallback: 0.
- **Validação:** idem.

### sceSmartSceneMappingRunCore
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceSmart · **NID:** 0xCB30BD4D
- **Protótipo:** inferido: `SceInt32 sceSmartSceneMappingRunCore(void);` (nenhum argumento preparado na chamada em
  `0x814FF092`).
- **Comportamento:** executa uma iteração do processamento (extração de features, rastreamento, atualização do mapa)
  sobre o último quadro despachado. É CPU pesada e roda na thread de trabalho do jogo. Com quadro cinza, não acha
  features e sai rápido.
- **Retorno/erros:** 0 (a confirmar se devolve o estado).
- **Estruturas:** —
- **Implementação no VitaSharp:** LLE. Fallback: 0 sem trabalho. Se for preciso, ceder a thread (`Kernel.Wait.Sleep`
  de 1 ms) para não ocupar 100 % de CPU num loop vazio.
- **Validação:** perfil de CPU do cenário `inv_boot.txt` no modo AR.

### sceSmartSceneMappingDispatchAndQuery
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceSmart · **NID:** 0xF10FDDD1
- **Protótipo:** inferido da chamada em `0x814FEFCC`: `SceInt32 sceSmartSceneMappingDispatchAndQuery(SceSmartSceneMappingResult *result, const SceSmartSceneMappingInput *input);`
- **Comportamento:** entrega o quadro atual (e dados associados) ao mapeador e devolve o resultado mais recente (pose
  da câmera e estado do tracking) em `result`.
- **Retorno/erros:** 0; erros a confirmar.
- **Estruturas:** `input`, montado na pilha pelo Invizimals: +0x00 = 0x40 (formato/flags, a confirmar), +0x04 = ponteiro
  da imagem, +0x08/+0x0C = duas palavras do quadro (dimensões ou timestamp u64, a confirmar), +0x10 = um campo do quadro,
  +0x18 = ponteiro/contexto. `result`: até 0x48 bytes (o próximo campo do jogo começa em +0x48), layout a confirmar.
- **Implementação no VitaSharp:** LLE. Fallback: zerar 0x48 bytes de `result` e devolver 0. A confirmar que o estado 0
  significa "não inicializado/perdido": rodar com LLE e câmera cinza, despejar `result` e copiar o padrão.
- **Validação:** idem.

### sceSmartSceneMappingGetInitializationPointInfo
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceSmart · **NID:** 0x9A578A29
- **Protótipo:** inferido: `SceInt32 sceSmartSceneMappingGetInitializationPointInfo(SceSmartSceneMappingInitPointInfo *buf, SceInt32 maxNum);`
- **Comportamento:** copia os pontos candidatos à inicialização do mapa. O Invizimals passa `maxNum = 64` e guarda o
  retorno como contagem.
- **Retorno/erros:** número de pontos gravados (≥ 0) ou erro negativo.
- **Estruturas:** registro de 0x1C bytes (deduzido do layout do jogo, a confirmar).
- **Implementação no VitaSharp:** LLE. Fallback: 0.
- **Validação:** idem.

### sceSmartSceneMappingGetLandmarkInfo
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceSmart · **NID:** 0xB17057EA
- **Protótipo:** inferido: `SceInt32 sceSmartSceneMappingGetLandmarkInfo(SceSmartSceneMappingLandmarkInfo *buf, SceInt32 maxNum);`
- **Comportamento:** copia os landmarks do mapa. O Invizimals passa `maxNum = 512`.
- **Retorno/erros:** contagem (a confirmar se é o retorno, como na anterior) ou erro.
- **Estruturas:** registro de 0x14 bytes (deduzido, a confirmar).
- **Implementação no VitaSharp:** LLE. Fallback: 0.
- **Validação:** idem.

### sceSmartSceneMappingPropagateResult
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceSmart · **NID:** 0x5A9EC64D
- **Protótipo:** inferido da chamada em `0x814FEB36` (4 registradores e 1 palavra na pilha): `SceInt32 sceSmartSceneMappingPropagateResult(const SceSmartSceneMappingResult *in, a1, a2, a3, SceSmartSceneMappingResult *out);`
  `a1..a3` são dados do sensor de movimento/intervalo de tempo (a confirmar).
- **Comportamento:** propaga o último pose estimado para o instante atual usando o giroscópio. Isso suaviza o AR entre
  as iterações de `RunCore`.
- **Retorno/erros:** 0; erros a confirmar.
- **Estruturas:** ver `DispatchAndQuery`.
- **Implementação no VitaSharp:** LLE. Fallback: copiar `in` para `out` (pose inalterado) e devolver 0, mantendo o estado
  "perdido".
- **Validação:** idem.

### Funções P1 de SceSmart

Exportadas pelo libsmart (offsets na seção comum), exceto as três marcadas. Nenhum jogo instalado importa. Fallback
comum: 0 / contagem 0 / "alvo não encontrado". Validação: jogo que importe.

| [ ] | Função | NID | Observação |
| --- | --- | --- | --- |
| [ ] | sceSmartCreateLearnedImageTarget | 0x5A31446E | Cria alvo a partir de dados aprendidos. |
| [ ] | sceSmartCreateInstantImageTarget | 0x57C17649 | **Não exportada por esta versão do libsmart.** LLE só com um libsmart mais novo; fallback HLE: devolver erro de "sem memória" ou criar um alvo fictício (a confirmar). |
| [ ] | sceSmartDestroyTarget | 0x0C6A1DA8 | |
| [ ] | sceSmartGetTargetInfo | 0x23EDFA9A | |
| [ ] | sceSmartSceneMappingDispatchAndQueryWithMask | 0x4D9EA817 | `DispatchAndQuery` com máscara de região. |
| [ ] | sceSmartSceneMappingEnableMask | 0x7C4731E2 | |
| [ ] | sceSmartSceneMappingFixMap | 0xA96B997B | |
| [ ] | sceSmartSceneMappingForceLocalize | 0x6D4081D3 | |
| [ ] | sceSmartSceneMappingGetNodePointInfo | 0x607AACB5 | |
| [ ] | sceSmartSceneMappingGetNumInitializationPoints | 0xDF900B29 | Fallback: 0. |
| [ ] | sceSmartSceneMappingGetNumLandmarks | 0x2260E8BB | Fallback: 0. |
| [ ] | sceSmartSceneMappingGetNumNodePoints | 0x3159D38C | Fallback: 0. |
| [ ] | sceSmartSceneMappingLoadMap | 0x961367D3 | |
| [ ] | sceSmartSceneMappingSaveMap | 0x4907021F | |
| [ ] | sceSmartSceneMappingRegisterTarget | 0x55C37F31 | |
| [ ] | sceSmartSceneMappingUnregisterTarget | 0x6807CA97 | |
| [ ] | sceSmartSceneMappingRemoveLandmark | 0x9F30C12A | |
| [ ] | sceSmartSceneMappingRun | 0xC7EBD753 | Provavelmente `DispatchAndQuery` + `RunCore` numa chamada. |
| [ ] | sceSmartSceneMappingSetCustomListener | 0x4A4F20EF | **Não exportada por esta versão.** |
| [ ] | sceSmartSceneMappingSetDenseMapMode | 0xE7867558 | |
| [ ] | sceSmartSceneMappingSetDispatchMode | 0xA4EC7755 | **Não exportada por esta versão.** |
| [ ] | sceSmartTargetTrackingStart | 0x5A121F6C | |
| [ ] | sceSmartTargetTrackingStop | 0xC4C607C0 | |
| [ ] | sceSmartTargetTrackingReset | 0xA4A155B5 | |
| [ ] | sceSmartTargetTrackingRegisterTarget | 0x8ACADD54 | |
| [ ] | sceSmartTargetTrackingUnregisterTarget | 0x6EE07829 | |
| [ ] | sceSmartTargetTrackingRun | 0xBFD87F61 | |
| [ ] | sceSmartTargetTrackingRun2 | 0x245EC28A | |
| [ ] | sceSmartTargetTrackingRunWorker | 0x8AE7354A | |
| [ ] | sceSmartTargetTrackingDispatchAndQuery | 0x7FE71305 | Fallback: nenhum alvo. |
| [ ] | sceSmartTargetTrackingQuery | 0x2808C8CB | Fallback: nenhum alvo. |
| [ ] | sceSmartTargetTrackingQuery2 | 0xD17EF0B2 | idem |
| [ ] | sceSmartTargetTrackingGetResults | 0x648C7E15 | Fallback: contagem 0. |
| [ ] | sceSmartTargetTrackingSetSearchPolicy | 0x35344A8E | |

---

## sceSysmoduleLoadModule (pré-requisito de Face/Smart)

### sceSysmoduleLoadModule
- [ ] **Prioridade:** STUB (Invizimals; sobrepõe o pacote A) · **Biblioteca:** SceSysmodule · **NID:** 0x79A0160A
- **Protótipo:** `int sceSysmoduleLoadModule(SceSysmoduleModuleId id);` (`psp2/sysmodule.h`)
- **Comportamento:** carrega o módulo do sistema `id`. Alguns módulos não estão no firmware e vêm em
  `app0:sce_module`; para esses, o sistema carrega o `.suprx` do jogo. Hoje (SystemMisc.cs:17) a função só anota o ID,
  e os imports do eboot para SceFace/SceSmart ficam como "Unimplemented HLE function".
- **Retorno/erros:** 0; se o arquivo existir e a carga falhar, devolver o erro do `module_start` ou de carga (o código
  exato do FW está a confirmar; `SCE_SYSMODULE_ERROR_FATAL` provável).
- **Estruturas:** IDs: `SCE_SYSMODULE_FACE = 0x38` → `libface`, `SCE_SYSMODULE_SMART = 0x39` → `libsmart`
  (`psp2/sysmodule.h`; nomes de arquivo do Vita3K `load_module.cpp`).
- **Implementação no VitaSharp:** em `SystemMisc`, uma tabela `id → nome` para módulos trazidos pelo jogo (começar
  por 0x38 e 0x39; o pacote A pode estender a outros). Na primeira carga de um `id`, se
  `Emu.Vfs.Resolve($"app0:sce_module/{nome}.suprx")` existir: `var m = Emu.LoadModule(path)` (já invalida o cache de
  código e, via `Linker.RegisterExports`, reescreve os stubs HLE já ligados do eboot em jump stubs para o módulo);
  depois `GuestThread.Current!.CallGuest(m.StartEntry, 0, 0)` se `StartEntry != 0`, como em `sceKernelLoadStartModule`.
  Guardar `id → LoadedModule` para não carregar de novo. `sceSysmoduleUnloadModule` só tira o ID de
  `_loadedSysmodules` e mantém o código residente: os stubs do eboot continuam apontando para ele, e uma nova carga o
  reaproveita. Se o arquivo não existir, manter o comportamento atual (fallback HLE).
- **Validação:** `VitaSharp.Desktop imports PCSA00552`: o comando atual só lista módulos carregados no boot. Validar no
  log de execução (`Loaded module SceFace (app0:sce_module/libface.suprx)`) e pela ausência de `Unimplemented HLE
  function` para `sceFace*`/`sceSmart*` no cenário `inv_boot.txt`. Regressão: `gow_boot.txt`, `kz_boot.txt`,
  `dd_boot.txt` e `st_boot.txt` (esses jogos também trazem os `.suprx`, mas não carregam 0x38/0x39).

---

## SceNgs (Sulpha)

Sulpha é o agente de trace do NGS para a ferramenta de PC; no VitaSharp nada está conectado. Os 4 stubs existentes
(`SetRackName`, `SetSynthName`, `SetVoiceName`, `Trace`, Ngs.cs:70-73) estão corretos como no-op. Os códigos de erro
Sulpha não estão no header (a confirmar); validar só ponteiros nulos. Arquivo: `Ngs.cs`.

### sceSulphaNgsGetDefaultConfig
- [ ] **Prioridade:** P1 · **Biblioteca:** SceNgs · **NID:** 0x5FD8AEDB
- **Protótipo:** `SceInt32 sceSulphaNgsGetDefaultConfig(SceSulphaNgsConfig *config);` (`psp2/ngs.h`)
- **Comportamento:** preenche os defaults do FW 3.60: 1024 amostras nomeáveis e buffer de trace de 4096 bytes.
- **Retorno/erros:** 0; NULL → erro Sulpha (a confirmar; usar `SCE_NGS_ERROR_INVALID_ARG`, já usado em `Ngs.cs`, até
  confirmar).
- **Estruturas:** `SceSulphaNgsConfig` (0x8): +0x00 `max_named_samples` = 1024, +0x04 `api_trace_buffer_size` = 4096.
- **Implementação no VitaSharp:** escrever os dois campos.
- **Validação:** teste unitário.

### sceSulphaNgsGetNeededMemory
- [ ] **Prioridade:** P1 · **Biblioteca:** SceNgs · **NID:** 0x793E3E8C
- **Protótipo:** `SceInt32 sceSulphaNgsGetNeededMemory(const SceSulphaNgsConfig *config, SceSize *size_in_bytes);` (`psp2/ngs.h`)
- **Comportamento:** quanto o jogo deve alocar para o agente. O valor real do FW está a confirmar (desmontar o
  SceNgsUser). Como o HLE não usa o buffer, qualquer valor > 0 serve. Proposta, marcada como valor do HLE e não do FW:
  `16 + max_named_samples*16 + api_trace_buffer_size` (20 496 com os defaults).
- **Retorno/erros:** 0; ponteiros NULL → erro (ver acima).
- **Estruturas:** idem.
- **Implementação no VitaSharp:** escrever o tamanho.
- **Validação:** teste unitário.

### sceSulphaNgsInit / sceSulphaNgsShutdown
- [ ] **Prioridade:** P1 · **Biblioteca:** SceNgs · **NID:** 0xAFCD824F (Init) / 0xD124BFB1 (Shutdown)
- **Protótipo:** `SceInt32 sceSulphaNgsInit(const SceSulphaNgsConfig *config, void *buffer, SceSize size_in_bytes);` /
  `SceInt32 sceSulphaNgsShutdown(void);` (`psp2/ngs.h`)
- **Comportamento:** registra/desregistra o agente (compartilhado pelo processo). O buffer precisa continuar válido até
  o Shutdown.
- **Retorno/erros:** 0. Init com `config`/`buffer` NULL → erro; Init duplo e Shutdown sem Init → erros a confirmar
  (sugestão: devolver 0 nos dois casos, já que não há agente).
- **Estruturas:** —
- **Implementação no VitaSharp:** flag `_sulpha`.
- **Validação:** teste unitário.

### sceSulphaNgsSetSampleName
- [ ] **Prioridade:** P1 · **Biblioteca:** SceNgs · **NID:** 0x54EC5B8D
- **Protótipo:** `SceInt32 sceSulphaNgsSetSampleName(const void *location, SceSize length, const char *name);` (`psp2/ngs.h`)
- **Comportamento:** associa um nome de diagnóstico a uma faixa de memória de amostras. `location` não nulo e
  `length > 0`.
- **Retorno/erros:** 0; `location == NULL` ou `length == 0` → erro (a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** validar e devolver 0.
- **Validação:** teste unitário.

---

## Anexo A — correções em funções já implementadas

Fora da lista de pendências, mas achadas durante a pesquisa e no caminho das funções acima. Corrigir junto.

| Arquivo | Função | Problema | Correção |
| --- | --- | --- | --- |
| `Audio.cs` | constantes | `ERR_INVALID_PORT = 0x80260007` (é INVALID_FORMAT), `ERR_PORT_FULL = 0x80260109` (é AudioIn NOT_OPENED), `ERR_INVALID_SIZE = 0x80260004` (é INVALID_POINTER), `ERR_INVALID_FORMAT = 0x80260108` (é AudioIn OUT_OF_MEMORY) | INVALID_PORT 0x80260003, PORT_FULL 0x80260005, INVALID_SIZE 0x80260006, INVALID_FORMAT 0x80260007 (`psp2common/audioout.h`) |
| `Audio.cs` | `sceAudioOutSetVolume` | `vols == 0` devolve 0x80260003 (INVALID_PORT) | INVALID_POINTER 0x80260004 |
| `Audio.cs` | `sceAudioOutGetConfig` | tipo inválido devolve 0x80260003 | INVALID_CONF_TYPE 0x8026000C |
| `Audio.cs` | `sceAudioOutOpenPort` | não valida `type` nem `freq` (MAIN só aceita 48000) | INVALID_PORT_TYPE 0x8026000A / INVALID_SAMPLE_FREQ 0x80260008 |
| `Display.cs` | `Ctrl.Peek` | `data == 0` devolve 0x80340002 (PRIV_REQUIRED) | INVALID_ARG 0x80340001 |
| `Display.cs` | `sceTouchGetPanelInfo` | zera 0x20 bytes; a struct tem 0x30 | `Fill(info, 0, 0x30)` |
| `Online.cs` | `sceCamera*` | `ERR_CAMERA_NOT_FOUND = 0x802E000B` é EXCLUSIVE | substituído pela câmera virtual (seção SceCamera) |
| `SystemMisc.cs` | `scePowerGetConfigurationMode` | log no boot de todos os jogos: "no NID known for SystemMisc.scePowerGetConfigurationMode"; o nome não está em `Hle/Data/nids.txt` | adicionar `Nid = ...` ao atributo quando o NID for confirmado (não está no db do vita-headers; a confirmar) |

## Anexo B — stubs triviais revisados e mantidos

Stubs com IL ≤ 3 de `vitasharp-implemented.tsv`, nas classes deste pacote, que **não** descartam comportamento que os
jogos notem:

| Função | Motivo para manter |
| --- | --- |
| sceCtrlSetActuator, sceCtrlSetLightBar, sceCtrlResetLightBar | Só valem para controles externos (PSTV/DS4). No portátil o FW devolve NOT_SUPPORTED 0x80340021 (Vita3K); 0 é inofensivo. |
| sceCtrlIsMultiControllerSupported (0), sceCtrlSingleControllerMode | Coerente com o portátil. |
| sceCtrlSetAnalogStickCheckMode, sceCtrlSetButtonIntercept | Sem efeito visível; o primeiro passa a gravar o valor (ver P1 AnalogStickCheck). |
| sceAudioOutGetAdopt (1), sceAudioOutSetAlcMode, sceAudioOutSetEffectType | Normalizador/EQ do BGM; não implementados no host. GetAdopt ganha estado em `SetAdopt_forUser`. |
| sceNgsRackSetParamErrorCallback, sceNgsSystemSetParamErrorCallback, sceNgsSystemSetFlags, sceNgsVoiceGetParamsOutOfRange | Diagnóstico de parâmetros do NGS; sem efeito no áudio produzido. |
| sceSulphaNgsSetRackName/SetSynthName/SetVoiceName/Trace | Sulpha desconectado. |
| sceVideodecTermLibrary | Nada a liberar no host. |
| scePowerRegisterCallback/UnregisterCallback | O VitaSharp não gera eventos de energia. |
| scePowerRequestDisplayOn/Off, scePowerSet*ClockFrequency, scePowerSetConfigurationMode | Sem efeito no host. |
| scePowerIsBatteryExist (1), scePowerIsLowBattery (0), scePowerIsPowerOnline (1), scePowerGetBatteryLifePercent (100) | Fazem parte da bateria virtual. Ver a nota sobre `IsBatteryCharging` em ScePower. |
| sceMotionReset | Orientação estática; nada a zerar. |
| sceShutterSoundPlay | Som de obturador; sem efeito no jogo. |
