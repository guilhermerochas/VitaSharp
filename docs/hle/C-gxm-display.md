# Pacote C — SceGxm, SceDisplay, SceDisplayUser, SceScreenShot

Especificação das funções de gráficos e display que faltam (ou que existem só como stub) no HLE do VitaSharp.
Leia antes o [README](README.md) (prioridades, template, como registrar um `[Hle]`).

**Escopo:** `SceGxm` (Gxm.cs), `SceDisplay` e `SceDisplayUser` (Display.cs), `SceScreenShot` (biblioteca nova, sem
nenhuma função no VitaSharp hoje), mais os stubs triviais de `Gxm`/`Display` listados em
[data/vitasharp-implemented.tsv](data/vitasharp-implemented.tsv) (IL ≤ 3) que descartam comportamento relevante.

**Fontes:** protótipos de `vita-headers` (`psp2/gxm.h`, `psp2/display.h`, `psp2common/display.h`, `psp2/screenshot.h`),
NIDs de `db/360` (`SceGxm.yml`, `SceDisplay.yml`, `SceDriverUser.yml` → `SceDisplayUser`, `SceScreenShot.yml`),
semântica do Vita3K (`modules/SceGxm`, `modules/SceDisplay`, `display/src/display.cpp`). Todos os NIDs abaixo já estão em
`src/VitaSharp.Core/Hle/Data/nids.txt`, então `[Hle]` sem `Nid =` resolve pelo nome.

## Contagem por prioridade

| Prioridade | Qtde | Funções |
| --- | --- | --- |
| P0 | 9 | `sceGxmPushUserMarker`, `sceGxmPopUserMarker` (God of War, PCSA00126); `sceGxmSetWarningEnabled`, `sceGxmSetWBufferEnable`, `sceScreenShotEnable`, `sceScreenShotDisable`, `sceScreenShotSetOverlayImage` (Sakatsuku 2013, PCSG00198); `sceScreenShotSetParam` (PCSG00198, PCSA00552); `sceScreenShotCapture` (Invizimals, PCSA00552) |
| STUB | 8 | `sceDisplayRegisterVblankStartCallback`, `sceDisplayUnregisterVblankStartCallback`, `sceGxmColorSurfaceSetClip`, `sceGxmColorSurfaceGetClip`, `sceGxmSetWClampEnable`, `sceGxmSetWClampValue`, `sceGxmColorSurfaceSetDitherMode`, `sceGxmColorSurfaceGetDitherMode` |
| P1 | 37 | 4 de display interno, 30 de SceGxm (11 deles de deferred context / command list), 3 de SceScreenShot |
| FORA | 3 | `sceGxmAddRazorGpuCaptureBuffer`, `sceGxmRemoveRazorGpuCaptureBuffer`, `sceGxmPadTriggerGpuPaTrace` (Razor; listados como P1 no TSV, reclassificados) |

Stubs triviais revisados e **mantidos** como estão (comportamento já correto ou irrelevante para jogos):
`sceDisplayGetPrimaryHead` (retorna 0 = `SCE_DISPLAY_HEAD_MAIN_LCD_OLED`, correto), `sceGxmIsDebugVersion` (0 = release),
`sceGxmSetValidationEnable` (validação de debug), `sceGxmPadHeartbeat` (Razor/heartbeat; no máximo validar ponteiros
nulos → `SCE_GXM_ERROR_INVALID_POINTER`, como no Vita3K), `sceGxmUnmapVertexUsseMemory`/`sceGxmUnmapFragmentUsseMemory`
(o map não guarda nada; idem, só validar `base != 0`), `sceGxmShaderPatcherGet{Host,Buffer,VertexUsse,FragmentUsse}MemAllocated`
(estatística de memória do patcher; 0 é aceitável porque o VitaSharp não aloca nada nos heaps do jogo).

## Estado da implementação (branch feat/hle-p0)

Todos os P0, STUB e P1 deste arquivo estão implementados no HLE (`Gxm.cs`, `Display.cs`, `ScreenShot.cs`). Pendências
que dependem do renderer (`VitaSharp.Renderer.Vulkan`/`ShaderGenerator`, fora do escopo desta passada) — o estado já é
guardado e só falta consumi-lo:

- `sceGxmColorSurfaceSetClip`: o clip fica na struct do guest (w0 bits 8-31, +0x1C bits 8-31), mas o scissor do
  Vulkan ainda não é intersectado com ele (`ColorSurfaceInfo` não ganhou os campos).
- `sceGxmSetWClampEnable/Value`, `sceGxmSetWBufferEnable`, `sceGxmSet*LineFillLastPixelEnable`: campos em `DrawState`/
  `SideState`, ignorados pelo renderer.
- `sceGxmSetYuvProfile`: guardado em `GxmContext.YuvProfile`; o decodificador YUV ainda usa as matrizes fixas.
- `sceGxmColorSurfaceSetDitherMode`: só estado (w0 bit 4); Vulkan não tem dither fixo.
- `sceScreenShotCapture`: fase 1 (loga os argumentos, não grava imagem). Os argumentos reais continuam a confirmar
  no primeiro jogo que chamar (Invizimals).
- Deferred contexts: gravação/execução por receita (`DrawRecipe`); custo de VDM por draw = 64 bytes (a confirmar).

## Ordem sugerida de implementação

1. **P0 triviais (uma sessão curta):** `sceGxmSetWarningEnabled`, família de user markers
   (`Push/Pop/SetUserMarker`), `sceGxmSetWBufferEnable` (guarda estado). Destrava Sakatsuku e God of War sem mexer no
   renderer.
2. **Biblioteca SceScreenShot inteira** (arquivo novo `ScreenShot.cs`, 8 funções; só `Capture` toca o renderer, e pode
   começar como no-op logado).
3. **STUB de display:** callbacks de vblank (thread host notificadora + ajuste nos `Wait*CB`).
4. **STUB de GXM com efeito visual:** `sceGxmColorSurfaceSetClip/GetClip` → scissor no Vulkan; depois W clamp e dither.
5. **P1 baratos de GXM:** getters/setters de textura, `ProgramIsEquivalent`, `ProgramGetFragmentProgramInputs`,
   `FragmentProgramGetPassType`, `PrecomputedXStateSetAllUniformBuffers`, `GetContextType`, `VertexFence`,
   `LineFillLastPixel`, `SetYuvProfile`, `GetParameterBufferThreshold`, `WaitEvent`.
6. **Display interno** (`sceDisplay*Internal`).
7. **Deferred contexts / command lists** (seção de arquitetura + 11 funções). É o maior bloco; nenhum dos 7 jogos
   instalados importa, então fica por último.
8. FORA (no-ops de 1 linha, se algum jogo importar).

## Convenções deste arquivo

- Offsets de structs em bytes, little-endian, ponteiros de 32 bits.
- "Layout VitaSharp" = layout próprio que o Gxm.cs grava na memória do jogo (as structs GXM são opacas para o jogo;
  o VitaSharp escolhe o que guarda nelas). Mudanças de layout precisam manter todos os leitores em Gxm.cs coerentes.
- Constantes de erro a acrescentar em `Gxm`:
  `ERR_UNSUPPORTED = 0x805B0009`, `ERR_RESERVE_FAILED = 0x805B000B` (vita-headers),
  `ERR_WITHIN_COMMAND_LIST = 0x805B002C`, `ERR_NOT_WITHIN_COMMAND_LIST = 0x805B002D` (só no Vita3K `gxm/types.h`;
  ausentes do vita-headers).
- "a confirmar" = valor ou comportamento não verificável com as fontes disponíveis; cada ocorrência diz como confirmar.

---

# Parte 1 — P0

## User markers (família)

Marcadores de depuração do Razor GPU (agrupam draws com um nome numa captura). Não têm efeito na imagem. As três
funções compartilham a mesma implementação; o Vita3K deixa todas como `UNIMPLEMENTED()`.

| Função | NID | Prioridade | Efeito |
| --- | --- | --- | --- |
| `sceGxmPushUserMarker` | 0x3276C475 | P0 (PCSA00126) | abre um grupo com `tag` |
| `sceGxmPopUserMarker` | 0x4FA073A6 | P0 (PCSA00126) | fecha o grupo aberto mais recente |
| `sceGxmSetUserMarker` | 0xC7A8CB77 | P1 | marcador pontual |

Implementação comum (Gxm.cs, seção "state setters"): validar `ctx` com `Ctx(ctx)` (nulo → `ERR_INVALID_POINTER`,
a confirmar se o hardware valida) e retornar 0. Manter um contador de profundidade em `GxmContext` (`int MarkerDepth`)
só para log de debug (`Log.Debug` com a tag em Push/Set; Pop com profundidade 0 apenas loga, não retorna erro — a
confirmar). Opcional, só quando houver um modo de trace no renderer: submeter um `DebugMarkerCommand { Kind, Tag }`
(novo `GpuCommand`) que o `VulkanRenderer` traduz em `vkCmdBeginDebugUtilsLabelEXT`/`End`/`Insert` se a extensão
`VK_EXT_debug_utils` estiver ativa — útil para RenderDoc. Não fazer isso por padrão (God of War chama por draw group).

### sceGxmPushUserMarker
- [x] **Prioridade:** P0 (God of War Collection, PCSA00126) · **Biblioteca:** SceGxm · **NID:** 0x3276C475
- **Protótipo:** `int sceGxmPushUserMarker(SceGxmContext *context, const char *tag);` (`psp2/gxm.h`)
- **Comportamento:** empilha um marcador de debug nomeado no stream de comandos do contexto. Sem efeito visual.
- **Retorno/erros:** 0. `SCE_GXM_ERROR_INVALID_POINTER` (0x805B0004) se `context` for nulo (a confirmar; tag nula
  provavelmente aceita).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `[Hle] int sceGxmPushUserMarker(uint ctx, string tag)` em `Gxm`; ver família acima.
  Usar `uint tag` em vez de `string` se houver risco de tag nula (o binder de `string` com ponteiro 0 deve ser
  verificado em `HleFramework.cs`).
- **Validação:** `VitaSharp.Desktop imports PCSA00126` sem o nome na lista de faltantes; `test tests/scenarios/gow_boot.txt`
  e `gow_game.txt` sem `Unimplemented HLE function` e sem regressão nos frames.

### sceGxmPopUserMarker
- [x] **Prioridade:** P0 (God of War Collection, PCSA00126) · **Biblioteca:** SceGxm · **NID:** 0x4FA073A6
- **Protótipo:** `int sceGxmPopUserMarker(SceGxmContext *context);` (`psp2/gxm.h`)
- **Comportamento:** desempilha o marcador aberto por `sceGxmPushUserMarker`.
- **Retorno/erros:** 0; `SCE_GXM_ERROR_INVALID_POINTER` com contexto nulo (a confirmar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `[Hle] int sceGxmPopUserMarker(uint ctx)`; decrementa `MarkerDepth` (sem ficar < 0).
- **Validação:** igual a `sceGxmPushUserMarker`.

### sceGxmSetWarningEnabled
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceGxm · **NID:** 0x12D18B3D
- **Protótipo (inferido; ausente do vita-headers):** `int sceGxmSetWarningEnabled(SceGxmWarning warning, SceBool enable);`
  — liga/desliga uma classe de aviso da libgxm de debug. Nomes/valores de `SceGxmWarning` a confirmar (não constam em
  nenhuma fonte disponível); para o HLE basta tratar os dois argumentos como `uint`.
- **Comportamento:** só afeta mensagens de aviso que a biblioteca de debug imprime no TTY. Na libgxm de release é no-op.
  Vita3K: `UNIMPLEMENTED()`.
- **Retorno/erros:** 0 (a confirmar se retorna `int` ou `void`; retornar 0 em r0 é inofensivo nos dois casos).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `[Hle] int sceGxmSetWarningEnabled(uint warning, uint enable) => 0;` junto de
  `sceGxmSetValidationEnable` (Gxm.cs:144).
- **Validação:** `imports PCSG00198`; `test tests/scenarios/st_boot.txt` sem o aviso de função não implementada.

### sceGxmSetWBufferEnable
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceGxm · **NID:** 0xEED86975
- **Protótipo:** `void sceGxmSetWBufferEnable(SceGxmContext *context, SceGxmWBufferMode enable);` (`psp2/gxm.h`)
  `SCE_GXM_WBUFFER_DISABLED = 0`, `SCE_GXM_WBUFFER_ENABLED = 0x00004000`.
- **Comportamento:** com W-buffer ligado, o teste/escrita de profundidade usa o W do vértice (profundidade em espaço de
  vista) em vez do Z/W pós-projeção. Afeta todos os draws seguintes do contexto. Vita3K: `UNIMPLEMENTED()`.
- **Retorno/erros:** void.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:**
  1. Fase 1 (suficiente para o P0): acrescentar `public bool WBuffer;` em `DrawState` (`Gpu/GxmScene.cs`) e gravar
     `c.State.WBuffer = enable != 0` (o enum vem como `0x4000`; não compare com 1). O renderer ignora o campo. Justificativa:
     com projeção perspectiva, Z/W e W são ambos monótonos na profundidade de vista, então a **ordem** dos testes
     `LESS/GREATER` é preservada; só diferem valores absolutos.
  2. Logar `Log.WarnOnce("wbuffer", ...)` na primeira ativação, para identificar jogos onde a diferença aparece.
  3. Casos que a fase 1 não cobre (implementar só se um jogo mostrar artefato): depth buffer amostrado como textura
     (sombras/pós-processo leem W em vez de Z), fragment program com depth replace escrevendo valores em unidades de W,
     ou `backgroundDepth` > 1 (hoje é clampado para [0,1] em `BeginRendering`). Uma emulação fiel exige escrever W
     normalizado em `gl_FragDepth` (custo: desliga early-Z) e conhecer a escala do D32F do SGX — a confirmar.
- **Validação:** `imports PCSG00198`; `st_boot.txt`/`st_game.txt` sem regressão de profundidade (campo de futebol,
  jogadores ocluídos corretamente). Comparar frames com/sem a chamada.

## SceScreenShot (biblioteca nova)

O VitaSharp não tem nenhuma função dessa biblioteca. Criar `src/VitaSharp.Core/Hle/Modules/ScreenShot.cs` com
`public sealed class ScreenShot : HleModule`. Estado (instância, thread-safe com `lock`):

```text
bool   Enabled = true          // captura pelo sistema (PS + START) permitida
bool   Notification = false    // app quer receber o app event de screenshot
string? PhotoTitle, GameTitle, GameComment   // de sceScreenShotSetParam (UTF-32 → string)
string? OverlayPath; int OverlayX, OverlayY  // de sceScreenShotSetOverlayImage
```

Erros (`psp2/screenshot.h`): `SCE_SCREENSHOT_ERROR_INVALID_ARGUMENT = 0x80102F01`, `NO_MEMORY = 0x80102F02`,
`FILE_NOT_FOUND = 0x80102F03`, `NOT_SUPPORTED_FORMAT = 0x80102F04`, `MEDIA_FULL = 0x80102F05`, `INTERNAL = 0x80102F06`.

O Vita3K deixa toda a biblioteca como `UNIMPLEMENTED()`; o comportamento abaixo vem do header e do papel da
biblioteca no sistema (o jogo controla se o usuário pode tirar screenshot e com que metadados/marca d'água).

`SceScreenShotParam` (0x10 bytes):

| Offset | Campo | Tipo |
| --- | --- | --- |
| 0x00 | `photoTitle` | `const SceWChar32 *` (UTF-32, até 64 caracteres, buffer de 256 bytes com NUL) |
| 0x04 | `gameTitle` | `const SceWChar32 *` (idem) |
| 0x08 | `gameComment` | `const SceWChar32 *` (até 128 caracteres, buffer de 512 bytes) |
| 0x0C | `reserved` | `void *`, deve ser NULL |

### sceScreenShotEnable
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceScreenShot · **NID:** 0x76E674D1
- **Protótipo:** `int sceScreenShotEnable(void);` (`psp2/screenshot.h`)
- **Comportamento:** volta a permitir que o usuário capture a tela pelo sistema (PS + START). Jogos alternam com
  `sceScreenShotDisable` para bloquear capturas em cenas com spoiler/conteúdo licenciado.
- **Retorno/erros:** 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `[Hle] int sceScreenShotEnable() { Enabled = true; return 0; }` em `ScreenShot`.
  Expor `public bool Enabled` para um futuro atalho de screenshot no host (`VitaSharp.Desktop`) respeitar o bloqueio.
- **Validação:** `imports PCSG00198`; `st_game.txt` sem aviso de função não implementada.

### sceScreenShotDisable
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceScreenShot · **NID:** 0x50AE9FF9
- **Protótipo:** `int sceScreenShotDisable(void);` (`psp2/screenshot.h`)
- **Comportamento:** bloqueia capturas pelo sistema até `sceScreenShotEnable`.
- **Retorno/erros:** 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Enabled = false; return 0;`.
- **Validação:** igual a `sceScreenShotEnable`.

### sceScreenShotSetParam
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198; Invizimals, PCSA00552) · **Biblioteca:** SceScreenShot · **NID:** 0x05DB59C7
- **Protótipo:** `int sceScreenShotSetParam(const SceScreenShotParam *param);` (`psp2/screenshot.h`)
- **Comportamento:** define título da foto, título do jogo e comentário gravados nos metadados das próximas capturas.
  Ponteiros de string nulos = campo não definido (mantém o padrão do sistema).
- **Retorno/erros:** 0. `SCE_SCREENSHOT_ERROR_INVALID_ARGUMENT` (0x80102F01) se `param` for nulo ou `reserved != NULL`.
  String maior que o limite (`SCE_SCREENSHOT_MAX_PHOTO_TITLE_LEN`/`GAME_TITLE_LEN` = 64, `GAME_COMMENT_LEN` = 128
  caracteres): provavelmente `INVALID_ARGUMENT` — a confirmar; enquanto isso, truncar e aceitar.
- **Estruturas:** `SceScreenShotParam` (acima).
- **Implementação no VitaSharp:** `[Hle] int sceScreenShotSetParam(uint param)`. Ler as três strings como UTF-32 LE até
  o NUL (helper local: ler `uint` a `uint` com limite de caracteres; não há `ReadCString` para UTF-32 em `GuestMemory`,
  conferir antes de criar). Guardar como `string`. Logar em `Debug`.
- **Validação:** `imports PCSG00198` e `imports PCSA00552`; cenários `st_*` sem erro. Teste unitário opcional: param com
  `reserved != 0` → 0x80102F01.

### sceScreenShotSetOverlayImage
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceScreenShot · **NID:** 0x7061665B
- **Protótipo:** `int sceScreenShotSetOverlayImage(const char *filepath, int offsetX, int offsetY);` (`psp2/screenshot.h`)
- **Comportamento:** registra uma imagem (PNG, normalmente em `app0:`) desenhada por cima das capturas na posição
  `(offsetX, offsetY)` — marca d'água/logo. `filepath` nulo remove o overlay (a confirmar).
- **Retorno/erros:** 0. `INVALID_ARGUMENT` (0x80102F01) para argumentos inválidos (offset negativo/fora de 960×544 —
  a confirmar); `FILE_NOT_FOUND` (0x80102F03) se o arquivo não existir; `NOT_SUPPORTED_FORMAT` (0x80102F04) se não for
  PNG (a confirmar se a checagem é feita aqui ou só na captura).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `[Hle] int sceScreenShotSetOverlayImage(uint pathPtr, int x, int y)`. Se `pathPtr == 0`,
  limpar o overlay e retornar 0. Senão ler a string, resolver com `Emu.Vfs.Resolve(path)` (como `IoFileMgr.HostPath`,
  incluindo o redirecionamento do `Fios2User` se aplicável); se o host path não existir, logar e retornar
  `FILE_NOT_FOUND`. Guardar caminho host + offsets. Não decodificar o PNG aqui. **Cuidado:** se o Sakatsuku tratar o
  erro como fatal, preferir retornar 0 e só logar — verificar no cenário.
- **Validação:** `st_boot.txt`/`st_game.txt`: conferir no log o caminho recebido e que o arquivo resolve.

### sceScreenShotCapture
- [x] **Prioridade:** P0 (Invizimals, PCSA00552) · **Biblioteca:** SceScreenShot · **NID:** 0xD509FB08
- **Protótipo (inferido; ausente do vita-headers e sem implementação no Vita3K):** argumentos desconhecidos. Hipótese:
  captura programática da tela atual para a galeria (Fotos), usando o param/overlay configurados; Invizimals usa para
  "foto" de criaturas em realidade aumentada. **Como confirmar:** rodar Invizimals com
  `--trace-hle-only sceScreenShotCapture` (Runner.cs) depois de registrar a função com 4 argumentos `uint`, ou achar o
  stub com `VitaSharp.Desktop stub` e o chamador com `VitaSharp.Desktop disasm PCSA00552 <addr>` para ver r0–r3 e
  se o retorno é testado/alguma memória é lida depois.
- **Comportamento:** grava o framebuffer exibido (com overlay) como JPEG em `ux0:picture/SCREENSHOT/<TITLEID>/...`
  (caminho a confirmar). É assíncrona ou síncrona — a confirmar.
- **Retorno/erros:** 0 em sucesso; `MEDIA_FULL` (0x80102F05), `NO_MEMORY`, `INTERNAL` possíveis. Se `Enabled == false`,
  provavelmente ainda permite (o bloqueio é para o atalho do sistema) — a confirmar.
- **Estruturas:** desconhecidas.
- **Implementação no VitaSharp:**
  1. Fase 1: `[Hle] int sceScreenShotCapture(uint a0, uint a1, uint a2, uint a3)`, `Log.WarnOnce` com os 4 valores,
     retorna 0. Não escreve memória.
  2. Fase 2 (opcional): salvar um PNG no host. O conteúdo do framebuffer **não** está na memória do guest (o
     `VulkanRenderer` só faz writeback de render targets até `WritebackMaxPixels` = 512×512), então é preciso um
     comando novo: `CaptureCommand : GpuCommand { DisplayFrame Frame; Action<byte[], int, int> Done }`, submetido via
     `Emu.Gpu.Submit` com o framebuffer atual do `Display` (expor `_base/_pitch/_width/_height` por uma propriedade
     `CurrentFrame`). No renderer: `FindRenderTargetByAddress` + o mesmo caminho de readback de `RecordReadback`
     (VulkanRenderer.cs:608) e `Png.Write` (TextureDecoder.cs:285) para `<data>/screenshots/<TITLEID>_<n>.png`.
     Compor o overlay é opcional.
- **Validação:** `imports PCSA00552`; criar `tests/scenarios/iz_boot.txt` (title=PCSA00552) e chegar ao ponto da
  captura; conferir o log dos argumentos.

---

# Parte 2 — STUB

## Callbacks de vblank

Hoje `sceDisplayRegisterVblankStartCallback`/`Unregister` (Display.cs:102-103) retornam 0 e nada acontece: o callback
do jogo nunca roda. No hardware o kernel chama `sceKernelNotifyCallback` em cada início de vblank para todo callback
registrado; o callback executa quando a thread dona entra numa espera `*CB` ou chama `sceKernelCheckCallback`.

Infra existente: `Kernel.Callback` (Kernel/Sync.cs:634) com `Notify(int arg)` thread-safe (incrementa `NotifyCount`,
marca `Owner.PendingCallbacks` e acorda waits); `ThreadMgr.ProcessCallbacks` executa os pendentes; funções `[Hle]` cujo
nome termina em `CB` já rodam callbacks pendentes **antes** de entrar (GuestThread.cs:259) e `Sync` os roda durante
esperas em objetos do kernel (Sync.cs:50). O relógio de vblank é calculado (`VBlank.Count`, Display.cs:11), **não há
tick periódico**.

Desenho:

1. Em `Display`: `readonly ConcurrentDictionary<int, Callback> _vblankCallbacks = new(); Thread? _vblankNotifier;`.
2. Thread **host** (não guest) criada no primeiro registro, `IsBackground = true`, nome `"VBlank notifier"`. Loop:
   `long last = VBlank.Count; while (Emu.Running && !_stop) { Thread.Sleep(1); long now = VBlank.Count; if (now == last) continue; int n = (int)Math.Min(now - last, 4); last = now; foreach (var (uid, cb) in _vblankCallbacks) { if (K.Objects.Get<Callback>(uid) == null) { _vblankCallbacks.TryRemove(uid, out _); continue; } for (i < n) cb.Notify(0); } }`.
   **Não** usar `GuestClock.WaitUntil` nem `VBlank.WaitFor` nessa thread: em modo turbo `WaitUntil` *avança* o relógio
   do guest (`GuestClock.AdvanceTo`), e uma thread host esperando sozinha faria o tempo do jogo disparar. O polling de
   1 ms em tempo real só observa o relógio.
3. Argumento do `Notify`: o Vita3K passa o "notifier id" do callback; o valor real a confirmar (logar o `arg` que o
   callback do jogo recebe não ajuda — confirmar em hardware/decompilação do SceDisplay). Usar 0.
4. Encerrar a thread em `Emulator` stop/LoadExec (flag `_stop` + `Join` curto). Conferir se `HleModule` tem hook de
   shutdown; senão checar `Emu.Running` no loop basta.
5. Ajuste nos waits CB: `sceDisplayWaitVblankStartCB`, `sceDisplayWaitVblankStartMultiCB`, `sceDisplayWaitSetFrameBufCB`,
   `sceDisplayWaitSetFrameBufMultiCB` hoje só esperam o vblank; o callback notificado **durante** a espera só rodaria
   na próxima chamada CB. Depois de `VBlank.WaitFor(...)`, se `GuestThread.Current is { CallbackWait: true } t &&
   t.PendingCallbacks > 0`, chamar `GuestThread.CallbackProcessor?.Invoke(t)`. (No hardware os callbacks rodam dentro
   da espera, antes do retorno.)

### sceDisplayRegisterVblankStartCallback
- [x] **Prioridade:** STUB · **Biblioteca:** SceDisplay · **NID:** 0x6BDF4C4D
- **Protótipo:** `int sceDisplayRegisterVblankStartCallback(SceUID uid);` (`psp2/display.h`)
- **Comportamento:** registra o callback `uid` (criado com `sceKernelCreateCallback`) para ser notificado em cada início
  de vblank (~59,94 Hz). Registrar o mesmo uid duas vezes não duplica (Vita3K usa um map por uid).
- **Retorno/erros:** 0. `SCE_DISPLAY_ERROR_INVALID_VALUE` (0x80290001) se o uid não for um callback válido (Vita3K).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Display.cs`, trocar a linha 102. `var cb = K.Objects.Get<Callback>(uid); if (cb == null)
  return ERR_INVALID_VALUE; _vblankCallbacks[uid] = cb; EnsureNotifier(); return 0;`. Ver desenho acima.
- **Validação:** nenhum dos 7 jogos importa hoje com efeito visível conhecido; teste unitário/homebrew: criar callback,
  registrar, `sceDisplayWaitVblankStartCB` 10× → callback chamado ~10 vezes (contador na memória). Rodar `st_boot.txt`,
  `gow_boot.txt`, `kz_boot.txt` para garantir que a thread nova não muda o ritmo (turbo incluso).

### sceDisplayUnregisterVblankStartCallback
- [x] **Prioridade:** STUB · **Biblioteca:** SceDisplay · **NID:** 0x98436A80
- **Protótipo:** `int sceDisplayUnregisterVblankStartCallback(SceUID uid);` (`psp2/display.h`)
- **Comportamento:** remove o registro; notificações já pendentes no callback continuam pendentes.
- **Retorno/erros:** 0; `SCE_DISPLAY_ERROR_INVALID_VALUE` (0x80290001) se `uid` não estiver registrado (Vita3K).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `return _vblankCallbacks.TryRemove(uid, out _) ? 0 : ERR_INVALID_VALUE;` (linha 103).
  A thread notificadora pode continuar viva ociosa.
- **Validação:** mesmo teste: depois de desregistrar, o contador para de subir.

## Clip da color surface

`sceGxmColorSurfaceSetClip` (Gxm.cs:259) é ignorado e `sceGxmColorSurfaceGetClip` (Gxm.cs:260) devolve sempre a
superfície inteira. No hardware o clip fica nas palavras do PBE (pixel back end) dentro da `SceGxmColorSurface` e
descarta escritas de cor fora do retângulo em **todas** as cenas que usarem essa superfície (independente do region
clip do contexto). Jogos usam para renderizar só uma faixa da surface (split-screen, atualização parcial de HUD).

Onde guardar: na própria struct do guest (é copiada por valor pelos jogos; um dicionário no host por endereço
quebraria). O layout VitaSharp de 48 bytes está todo ocupado, mas há bits livres:

| Palavra | Uso atual | Novo uso |
| --- | --- | --- |
| `+0x00` (w0) | bit0 disabled, bit1 downscale, bits2-3 gamma | bit4 dither (ver abaixo); bits 8-19 `xMax`; bits 20-31 `yMax` |
| `+0x1C` | `outputRegisterSize` (0/1) | bit0 mantém o valor; bits 8-19 `xMin`; bits 20-31 `yMin` |

12 bits bastam (largura/altura ≤ 4096 → coordenadas 0..4095). `sceGxmColorSurfaceInit` passa a gravar o clip padrão
`(0, 0, width-1, height-1)`; `sceGxmColorSurfaceInitDisabled` zera tudo (já faz). Conferir que nenhum leitor usa
`R32(surface + 0x1C)` inteiro (hoje só `ColorSurfaceInit` escreve) e que `SetGammaMode`/`SetScaleMode` continuam
preservando os outros bits (preservam: usam máscara).

Como aplicar no Vulkan:

1. `ColorSurfaceInfo` (Gpu/GxmScene.cs:10) ganha `int ClipMinX = 0, int ClipMinY = 0, int ClipMaxX = int.MaxValue,
   int ClipMaxY = int.MaxValue` como parâmetros opcionais no fim (o `new ColorSurfaceInfo(...)` posicional em
   VulkanRenderer.cs:556 continua compilando). `ReadColorSurface` (Gxm.cs:287) desempacota. A chave do cache de RT
   (`(Data, Width, Height, ColorFormat)`, VulkanRenderer.cs:325) não muda.
2. Em `VulkanRenderer.SetDynamicState` (VulkanRenderer.Draw.cs:531-539), depois de calcular o `scissor` do region
   clip, intersectar com o clip de `_scene?.Color` quando ele não for a superfície inteira:
   `x0 = max(x0, ClipMinX)`, `y0 = max(y0, ClipMinY)`, `x1 = min(x1, ClipMaxX + 1)`, `y1 = min(y1, ClipMaxY + 1)`;
   extensão negativa → 0 (scissor de extensão 0 é válido e descarta tudo). `_scene` já está disponível ali.
3. Diferença conhecida: o scissor também bloqueia escrita de profundidade/stencil, enquanto o clip do PBE só afeta a
   cor (a confirmar se o ZLS respeita o clip). Aceitável; documentar no código.
4. Fora do escopo mas relacionado: `RegionClipMode == 3` (`SCE_GXM_REGION_CLIP_INSIDE`) não é tratado em
   `SetDynamicState` (só 1 e 2).

### sceGxmColorSurfaceSetClip
- [x] **Prioridade:** STUB · **Biblioteca:** SceGxm · **NID:** 0x86456F7B
- **Protótipo:** `void sceGxmColorSurfaceSetClip(SceGxmColorSurface *surface, unsigned int xMin, unsigned int yMin, unsigned int xMax, unsigned int yMax);` (`psp2/gxm.h`)
- **Comportamento:** define o retângulo inclusivo `[xMin..xMax] × [yMin..yMax]` fora do qual a cor não é escrita.
  Vita3K: `UNIMPLEMENTED()`.
- **Retorno/erros:** void. Valores fora da superfície: clampar para `width-1`/`height-1` (a confirmar).
- **Estruturas:** layout VitaSharp acima.
- **Implementação no VitaSharp:** Gxm.cs:259; gravar os 4 campos com máscara, sem tocar nos bits baixos. Renderer
  conforme "Como aplicar no Vulkan".
- **Validação:** teste unitário em `VitaSharp.Core` (se houver projeto de testes) Init → SetClip → GetClip devolve os
  valores; visual: nenhum dos cenários atuais depende disso — rodar `f1_menu.txt`, `gow_menu.txt`, `st_game.txt`,
  `kz_gameplay.txt` e comparar frames (nada deve mudar quando o clip é o padrão). Procurar com `--trace-hle-only
  sceGxmColorSurfaceSetClip` quais jogos chamam.

### sceGxmColorSurfaceGetClip
- [x] **Prioridade:** STUB · **Biblioteca:** SceGxm · **NID:** 0x07DFEE4B
- **Protótipo:** `void sceGxmColorSurfaceGetClip(const SceGxmColorSurface *surface, unsigned int *xMin, unsigned int *yMin, unsigned int *xMax, unsigned int *yMax);` (`psp2/gxm.h`)
- **Comportamento:** devolve o clip guardado (padrão = superfície inteira).
- **Retorno/erros:** void; ponteiros de saída nulos são ignorados (já usa `TrySet`).
- **Estruturas:** layout VitaSharp acima.
- **Implementação no VitaSharp:** Gxm.cs:260; desempacotar de `+0x00`/`+0x1C` em vez de derivar de width/height.
- **Validação:** junto com `SetClip`.

## W clamp

### sceGxmSetWClampEnable
- [x] **Prioridade:** STUB · **Biblioteca:** SceGxm · **NID:** 0x1BF8B853
- **Protótipo:** `void sceGxmSetWClampEnable(SceGxmContext *context, SceGxmWClampMode enable);` (`psp2/gxm.h`)
  `SCE_GXM_WCLAMP_MODE_DISABLED = 0`, `SCE_GXM_WCLAMP_MODE_ENABLED = 0x00008000`.
- **Comportamento:** liga o clamp de W dos vértices transformados ao valor de `sceGxmSetWClampValue` (evita W ≤ 0 ou
  minúsculo antes da divisão perspectiva em primitivas que cruzam o plano próximo). Semântica exata (`max(w, v)`? só
  quando o viewport está habilitado?) e o estado padrão do contexto (ligado/desligado, valor) — **a confirmar**; não
  constam em nenhuma fonte disponível (Vita3K: `UNIMPLEMENTED()`). Confirmar comparando um jogo que chama a função
  (achar com `--trace-hle-only sceGxmSetWClampEnable+sceGxmSetWClampValue`) contra captura de hardware.
- **Retorno/erros:** void. A assinatura atual em Gxm.cs:141 é `int` — trocar para `void` (inofensivo, mas coerente).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:**
  1. `DrawState` ganha `public bool WClampEnabled; public float WClampValue;`; setter grava `enable != 0`.
  2. Renderer: `PushConstants` (VulkanRenderer.Draw.cs:21) ganha `float WClamp` (0 = desligado) preenchido em
     `SetDynamicState`; o bloco GLSL `RenderInfo` em `ShaderGenerator.PushConstants` (ShaderGenerator.cs:61-70) ganha
     `float w_clamp;` **na mesma posição**. No ramo `viewport_flag >= 0.5` (ShaderGenerator.cs:557-559), antes de usar
     `vpos`: `if (rinfo.w_clamp > 0.0) vpos.w = max(vpos.w, rinfo.w_clamp);`.
  3. Atenção: `ShaderGenerator.cs` tem modificação não commitada no branch atual — coordenar antes de editar.
  4. Se a semântica não for confirmada, implementar só o item 1 (guardar estado) e deixar o shader como está: o
     clipping do Vulkan já trata W ≤ 0, então o efeito visual de não ter clamp é pequeno.
- **Validação:** cenários F1/GoW/KZ sem regressão; procurar artefatos de triângulos "explodindo" perto da câmera
  no jogo que usa a função.

### sceGxmSetWClampValue
- [x] **Prioridade:** STUB · **Biblioteca:** SceGxm · **NID:** 0xD096336E
- **Protótipo:** `void sceGxmSetWClampValue(SceGxmContext *context, float clampValue);` (`psp2/gxm.h`)
- **Comportamento:** define o valor usado pelo W clamp. `clampValue` chega em `s0` (AAPCS-VFP), como já declarado em
  Gxm.cs:142.
- **Retorno/erros:** void.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `if (Ctx(ctx) is { } c) c.State.WClampValue = value;`. O push constant usa
  `WClampEnabled ? WClampValue : 0`.
- **Validação:** junto com `sceGxmSetWClampEnable`.

## Dither da color surface

### sceGxmColorSurfaceSetDitherMode
- [x] **Prioridade:** STUB · **Biblioteca:** SceGxm · **NID:** 0x45027BAB
- **Protótipo:** `int sceGxmColorSurfaceSetDitherMode(SceGxmColorSurface *surface, SceGxmColorSurfaceDitherMode ditherMode);` (`psp2/gxm.h`)
  `SCE_GXM_COLOR_SURFACE_DITHER_DISABLED = 0`, `SCE_GXM_COLOR_SURFACE_DITHER_ENABLED = 0x00000008`.
- **Comportamento:** liga dithering ao escrever em formatos de poucos bits (RGB565, ARGB4444…). Hoje o stub descarta o
  valor, e `GetDitherMode` devolve sempre 0, então um jogo que lê o modo de volta vê o valor errado.
- **Retorno/erros:** 0; `SCE_GXM_ERROR_INVALID_POINTER` (0x805B0004) com `surface` nulo (Vita3K).
- **Estruturas:** bit 4 de w0 no layout VitaSharp (não usar o bit 3 do enum: bits 2-3 já são o gamma).
- **Implementação no VitaSharp:** `SetBits`-like em w0 bit 4 (`mode != 0`). Efeito visual opcional: o Vulkan não tem
  dither fixo; ignorar (diferença imperceptível em 8 bits; em 565 o hardware real teria dither — a confirmar se algum
  jogo usa 565 com dither).
- **Validação:** teste unitário Set/Get.

### sceGxmColorSurfaceGetDitherMode
- [x] **Prioridade:** STUB · **Biblioteca:** SceGxm · **NID:** 0x200A96E1
- **Protótipo:** `SceGxmColorSurfaceDitherMode sceGxmColorSurfaceGetDitherMode(const SceGxmColorSurface *surface);` (`psp2/gxm.h`)
- **Comportamento:** devolve o modo guardado.
- **Retorno/erros:** `0` ou `0x8`.
- **Estruturas:** bit 4 de w0.
- **Implementação no VitaSharp:** `((GuestMemory.R32(surface) >> 4) & 1) != 0 ? 8u : 0u`.
- **Validação:** junto com `Set`.

---

# Parte 3 — P1 de SceGxm (exceto deferred)

### sceGxmFragmentProgramGetPassType
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xCE0B0A76
- **Protótipo (inferido; ausente do vita-headers, assinatura do Vita3K):** `SceGxmPassType sceGxmFragmentProgramGetPassType(const SceGxmFragmentProgram *fragmentProgram);`
  `SceGxmPassType` está em `psp2/gxm.h`: `OPAQUE = 0`, `TRANSLUCENT = 0x02000000`, `DISCARD = 0x04000000`,
  `MASK_UPDATE = 0x06000000`, `DEPTH_REPLACE = 0x0A000000`.
- **Comportamento:** o patcher classifica o fragment program pelo tipo de passe do ISP (hidden surface removal):
  mask update, depth replace, discard, translúcido (com blend) ou opaco. O Vita3K retorna sempre `OPAQUE`.
- **Retorno/erros:** o enum; programa inválido → comportamento indefinido no hardware.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `[Hle] uint sceGxmFragmentProgramGetPassType(uint fp)`. Com `_fragmentPrograms[fp]`:
  `IsMaskUpdate → 0x06000000`; senão `Info.Program.IsDepthReplaceUsed → 0x0A000000`; `IsDiscardUsed → 0x04000000`;
  blend ativo (mesma regra de `blendOn` em VulkanRenderer.Draw.cs: `Blend != null` e não é `src=ONE,dst=ZERO`) →
  `0x02000000`; senão 0. A precedência entre depth replace/discard/translucent é **a confirmar** (decompilar
  `sceGxmShaderPatcherCreateFragmentProgram` ou comparar com hardware); a ordem acima é a mais restritiva primeiro.
- **Validação:** teste unitário com programas dos jogos (F1/GoW têm programas com discard); nenhum jogo instalado importa.

### sceGxmGetContextType
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x2A1BCDDB
- **Protótipo (inferido; Vita3K):** `int sceGxmGetContextType(const SceGxmContext *context, SceGxmContextType *type);`
  `SceGxmContextType`: `IMMEDIATE = 0`, `DEFERRED = 1` (Vita3K `gxm/types.h`).
- **Comportamento:** informa se o contexto é imediato ou deferred.
- **Retorno/erros:** 0; `SCE_GXM_ERROR_INVALID_POINTER` com `context` ou `type` nulos (ou contexto desconhecido).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** depende de `GxmContext.Deferred` (ver Parte 5). `type.Value = c.Deferred ? 1u : 0u`.
- **Validação:** teste unitário com os dois tipos de contexto.

### sceGxmGetParameterBufferThreshold
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x6ABF3F76
- **Protótipo (inferido; Vita3K):** `int sceGxmGetParameterBufferThreshold(unsigned int *parameterBufferSize);`
- **Comportamento:** devolve o limiar de ocupação do parameter buffer (memória de geometria binada do SGX) a partir do
  qual o driver força partial renders. O VitaSharp não tem parameter buffer.
- **Retorno/erros:** 0; `SCE_GXM_ERROR_INVALID_POINTER` com ponteiro nulo (Vita3K).
- **Estruturas:** `SceGxmInitializeParams.parameterBufferSize` em `+0x10` (`psp2/gxm.h`).
- **Implementação no VitaSharp:** guardar `parameterBufferSize` em `sceGxmInitialize` (`GuestMemory.R32(pParams + 16)`)
  e devolvê-lo. Valor real (fração do tamanho? constante?) **a confirmar** por decompilação da libgxm; qualquer valor
  plausível serve porque o HLE nunca faz partial render.
- **Validação:** teste unitário.

### sceGxmPrecomputedVertexStateSetAllUniformBuffers
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x0389861D
- **Protótipo:** `int sceGxmPrecomputedVertexStateSetAllUniformBuffers(SceGxmPrecomputedVertexState *precomputedState, const void * const *bufferDataArray);` (`psp2/gxm.h`)
- **Comportamento:** copia, de uma vez, os ponteiros de todos os uniform buffers usados pelo programa para o estado
  pré-computado (equivale a `SetUniformBuffer(i, array[i])` para cada índice).
- **Retorno/erros:** 0; `SCE_GXM_ERROR_INVALID_POINTER` se `precomputedState`, o bloco de buffers ou `bufferDataArray`
  forem nulos.
- **Estruturas:** layout VitaSharp do estado pré-computado (Gxm.cs:729): `program@0`, `textures@4`,
  `textureCount@8 (u16)`, `bufferCount@10 (u16)`, `uniformBuffers@12`.
- **Implementação no VitaSharp:** helper `PrecomputedSetAllUniformBuffers(state, array)` ao lado de
  `PrecomputedSetUniformBuffer` (Gxm.cs:837): para `i < Math.Min(bufferCount, 14)` copiar `R32(array + i*4)` para
  `R32(state+12) + i*4`. O slot 14 (default uniform buffer) **não** é coberto pelo array (o array tem os 14 buffers
  "normais"; a confirmar — o Vita3K copia `buffer_count` entradas a partir do seu offset interno).
- **Validação:** nenhum jogo instalado importa; teste unitário com `sceGxmPrecomputedVertexStateInit` + `SetAll` +
  leitura dos slots.

### sceGxmPrecomputedFragmentStateSetAllUniformBuffers
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x5A783DC3
- **Protótipo:** `int sceGxmPrecomputedFragmentStateSetAllUniformBuffers(SceGxmPrecomputedFragmentState *precomputedState, const void * const *bufferDataArray);` (`psp2/gxm.h`)
- **Comportamento/Retorno/Estruturas:** idênticos à versão vertex.
- **Implementação no VitaSharp:** mesmo helper.
- **Validação:** idem.

### sceGxmProgramGetFragmentProgramInputs
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xC6924709
- **Protótipo (inferido; Vita3K):** `SceGxmFragmentProgramInputs sceGxmProgramGetFragmentProgramInputs(const SceGxmProgram *program);`
  Bits: `POSITION = 1<<0`, `FOG = 1<<1`, `COLOR0 = 1<<2`, `COLOR1 = 1<<3`, `TEXCOORD0..9 = 1<<4..1<<13`,
  `SPRITECOORD = 1<<14` (Vita3K `gxm/types.h`).
- **Comportamento:** máscara dos varyings que um fragment program lê. Para vertex program retorna 0.
- **Retorno/erros:** a máscara.
- **Estruturas:** descritores de atributo de fragmento do GXP (`GxpProgram.FragmentAttributeDescriptors()`,
  Gxp.cs:128), campo `AttributeInfo`. Para cada descritor (são `VaryingsCount` entradas): o nibble baixo
  (`info & 0xF`) e o nibble `(info >> 12) & 0xF` identificam o recurso; `0xF` = não usado. Nibble baixo: bit `0x400`
  → SPRITECOORD, senão valor 0..9 → TEXCOORDn. Nibble alto: bit `0x40000000` → SPRITECOORD, senão 0..9 → TEXCOORDn,
  0xA → COLOR0, 0xB → COLOR1, 0xC → FOG, 0xD → POSITION. (Regra derivada da decompilação usada pelo Vita3K em
  `gxm/src/gxp.cpp`; reimplementar a partir desta descrição.)
- **Implementação no VitaSharp:** método `FragmentInputs()` em `GxpProgram` + `[Hle] uint sceGxmProgramGetFragmentProgramInputs(uint program)
  => GxmProgramInfo.Get(program).Program.IsFragment ? ... : 0`.
- **Validação:** teste unitário com programas de fragmento extraídos de F1/GoW (comparar com os inputs que o
  `ShaderGenerator` já decodifica, ShaderGenerator.cs:411).

### sceGxmProgramIsEquivalent
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xE23C838C
- **Protótipo (inferido; Vita3K):** `SceBool sceGxmProgramIsEquivalent(const SceGxmProgram *programA, const SceGxmProgram *programB);`
- **Comportamento:** verdadeiro se os dois blobs GXP são idênticos (mesmo tamanho em `+0x08` e mesmos bytes).
- **Retorno/erros:** 1/0; ponteiro nulo → 0 (Vita3K loga `INVALID_POINTER` e retorna false).
- **Estruturas:** cabeçalho GXP: tamanho total em `+0x08`.
- **Implementação no VitaSharp:** `[Hle] bool sceGxmProgramIsEquivalent(uint a, uint b)`; `a == b` → true;
  comparar `R32(a+8) == R32(b+8)` e `GuestMemory.Span(a, size).SequenceEqual(GuestMemory.Span(b, size))`.
- **Validação:** teste unitário.

### sceGxmSetFrontLineFillLastPixelEnable
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x5765DE9F
- **Protótipo:** `void sceGxmSetFrontLineFillLastPixelEnable(SceGxmContext *context, SceGxmLineFillLastPixelMode enable);` (`psp2/gxm.h`)
  `DISABLED = 0`, `ENABLED = 0x00080000`.
- **Comportamento:** em polígonos desenhados em modo linha (`SCE_GXM_POLYGON_MODE_LINE`/primitiva de linha), controla
  se o último pixel de cada segmento é rasterizado (face frontal).
- **Retorno/erros:** void.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** guardar `bool LineFillLastPixel` em `SideState` (Gpu/GxmScene.cs). O Vulkan não
  expõe controle do último pixel (nem `VK_EXT_line_rasterization`); o renderer ignora. Diferença: 1 pixel por linha.
- **Validação:** nenhum jogo instalado importa; só compilar e rodar um cenário.

### sceGxmSetBackLineFillLastPixelEnable
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xC88EB702
- **Protótipo:** `void sceGxmSetBackLineFillLastPixelEnable(SceGxmContext *context, SceGxmLineFillLastPixelMode enable);` (`psp2/gxm.h`)
- **Comportamento/Implementação:** igual à versão front, em `State.Back`.
- **Retorno/erros:** void.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** idem.
- **Validação:** idem.

### sceGxmSetUserMarker
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xC7A8CB77
- **Protótipo:** `int sceGxmSetUserMarker(SceGxmContext *context, const char *tag);` (`psp2/gxm.h`)
- **Comportamento:** marcador pontual de debug (Razor). Ver família "User markers" na Parte 1.
- **Retorno/erros:** 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** igual a `sceGxmPushUserMarker` sem mexer na profundidade.
- **Validação:** compilar; nenhum jogo instalado importa.

### sceGxmSetYuvProfile
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xB32917F0
- **Protótipo:** `int sceGxmSetYuvProfile(SceGxmContext *context, unsigned int index, SceGxmYuvProfile profile);` (`psp2/gxm.h`)
  `BT601_STANDARD = 0`, `BT709_STANDARD = 1`, `BT601_FULL_RANGE = 2`, `BT709_FULL_RANGE = 3`.
- **Comportamento:** define a matriz de conversão YUV→RGB dos dois "CSC" do hardware (`index` 0 ou 1). Texturas
  YUV420/YUV422 escolhem CSC0 ou CSC1 pelo bit de swizzle.
- **Retorno/erros:** 0; `SCE_GXM_ERROR_INVALID_VALUE` para `index > 1` ou `profile > 3` (a confirmar);
  `INVALID_POINTER` com contexto nulo.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `GxmContext` ganha `byte[] YuvProfile = [0, 2]` (padrão que reproduz o decodificador
  atual: CSC0 = BT.601 estúdio, CSC1 = BT.601 full — TextureDecoder.cs:220; padrão real a confirmar). Copiar para o
  `DrawCommand` (campo novo `YuvProfiles`, 2 bytes). No renderer, `TextureDecoder.YuvToRgb` recebe o perfil
  (coeficientes BT.601 vs BT.709 e range) e o perfil entra na **chave do cache de textura** — senão uma textura YUV já
  decodificada não muda quando o perfil muda.
- **Validação:** vídeos (`gow_vid*.txt`, `f1_movie.txt`) sem mudança de cor quando o perfil não é chamado.

### sceGxmTextureGetGammaMode
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xF23FCE81
- **Protótipo:** `SceGxmTextureGammaMode sceGxmTextureGetGammaMode(const SceGxmTexture *texture);` (`psp2/gxm.h`)
- **Comportamento:** devolve o modo de gamma (bits 27-28 da palavra 0 da textura, já em posição de enum).
- **Retorno/erros:** `((w0 >> 27) & 3) << 27`.
- **Estruturas:** `SceGxmTexture` palavra 0, bits 27-28 (`GxmTextureDesc.GammaMode`).
- **Implementação no VitaSharp:** ao lado de `sceGxmTextureSetGammaMode` (Gxm.cs:1283).
- **Validação:** teste unitário Set/Get.

### sceGxmTextureGetMipmapCountUnsafe
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x4CC42929
- **Protótipo (inferido; Vita3K):** `unsigned int sceGxmTextureGetMipmapCountUnsafe(const SceGxmTexture *texture);`
- **Comportamento:** versão sem checagem de tipo: `(mipCountRaw + 1) & 0xF`, mesmo para textura linear strided (onde
  esses bits fazem parte do stride).
- **Retorno/erros:** o valor.
- **Estruturas:** palavra 0, bits 17-20 (`GxmTextureDesc.MipCountRaw`).
- **Implementação no VitaSharp:** `(GxmTextureDesc.Read(tex).MipCountRaw + 1) & 0xF`.
- **Validação:** teste unitário.

### sceGxmTextureGetNormalizeMode
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x512BB86C
- **Protótipo (inferido; Vita3K):** `SceGxmTextureNormalizeMode sceGxmTextureGetNormalizeMode(const SceGxmTexture *texture);`
  (`SceGxmTextureNormalizeMode` em `psp2/gxm.h`: `DISABLED = 0`, `ENABLED = 0x80000000`.)
- **Comportamento:** devolve o bit 31 da palavra 3.
- **Retorno/erros:** `GuestMemory.R32(tex + 12) & 0x80000000`.
- **Estruturas:** palavra 3, bit 31.
- **Implementação no VitaSharp:** ao lado de `sceGxmTextureSetNormalizeMode` (Gxm.cs:1285).
- **Validação:** teste unitário Set/Get.

### sceGxmTextureGetUAddrModeSafe
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xC037DA83
- **Protótipo (inferido; Vita3K):** `SceGxmTextureAddrMode sceGxmTextureGetUAddrModeSafe(const SceGxmTexture *texture);`
- **Comportamento:** como `GetUAddrMode`, mas textura linear strided (que não suporta modos de endereço) devolve
  `SCE_GXM_TEXTURE_ADDR_CLAMP` (2).
- **Retorno/erros:** o modo.
- **Estruturas:** palavra 0 bits 6-8.
- **Implementação no VitaSharp:** `IsStrided(tex) ? 2u : (R32(tex) >> 6) & 7`.
- **Validação:** teste unitário.

### sceGxmTextureGetVAddrModeSafe
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xD2F0D9C1
- **Protótipo (inferido; Vita3K):** `SceGxmTextureAddrMode sceGxmTextureGetVAddrModeSafe(const SceGxmTexture *texture);`
- **Comportamento:** idem para V (bits 3-5).
- **Retorno/erros:** `IsStrided(tex) ? 2u : (R32(tex) >> 3) & 7`.
- **Estruturas:** palavra 0 bits 3-5.
- **Implementação no VitaSharp:** idem.
- **Validação:** idem. Obs.: os *setters* `Safe` existentes (Gxm.cs:1278-1279) também deveriam ignorar texturas strided
  — conferir.

### sceGxmTextureSetStride
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x58D0EB0A
- **Protótipo:** `int sceGxmTextureSetStride(SceGxmTexture *texture, unsigned int byteStride);` (`psp2/gxm.h`)
- **Comportamento:** muda o stride de uma textura linear strided já inicializada.
- **Retorno/erros:** 0; `INVALID_POINTER` (textura nula); `SCE_GXM_ERROR_UNSUPPORTED` (0x805B0009) se o tipo não for
  `LINEAR_STRIDED`; `INVALID_ALIGNMENT` se `byteStride & 3`; `INVALID_VALUE` se `byteStride < 4` ou `> 131072`
  (mesmas checagens do Vita3K e de `sceGxmTextureInitLinearStrided`).
- **Estruturas:** stride codificado como `s = byteStride/4 - 1` espalhado na palavra 0: bit 9 ← `s & 1`,
  bits 10-11 ← `(s >> 1) & 3`, bits 17-20 ← `(s >> 3) & 0xF`, bits 21-26 ← `(s >> 7) & 0x3F` (mesma codificação de
  `sceGxmTextureInitLinearStrided`, Gxm.cs:1250, e lida por `GxmTextureDesc.StrideBytes`).
- **Implementação no VitaSharp:** extrair a codificação de `InitLinearStrided` num helper `EncodeStride(w0, byteStride)`
  que limpa e regrava esses bits, preservando o resto da palavra 0.
- **Validação:** teste unitário: InitLinearStrided(stride A) → SetStride(B) → `sceGxmTextureGetStride == B`.

### sceGxmVertexFence
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xE05277D6
- **Protótipo (inferido; Vita3K):** `int sceGxmVertexFence(SceGxmContext *immediateContext);`
- **Comportamento:** barreira de vértice no meio da cena; o Vita3K implementa como
  `sceGxmMidSceneFlush(ctx, 0, NULL, NULL)`.
- **Retorno/erros:** 0; `INVALID_POINTER` (contexto nulo/desconhecido); `INVALID_VALUE` se o contexto for deferred;
  `NOT_WITHIN_SCENE` (0x805B0006) fora de cena.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** como o VitaSharp executa a fila em ordem, basta validar e retornar 0 (o
  `sceGxmMidSceneFlush` atual também não faz nada sem notificação).
- **Validação:** compilar; nenhum jogo instalado importa.

### sceGxmWaitEvent
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x8BD94593
- **Protótipo (inferido; ausente do vita-headers, Vita3K sem assinatura):** `int sceGxmWaitEvent(void);` — argumentos
  desconhecidos.
- **Comportamento:** espera interna por um evento do driver GPU (usada pela própria libgxm/GLES). Semântica a
  confirmar por decompilação; nenhum dos 7 jogos importa.
- **Retorno/erros:** 0.
- **Estruturas:** desconhecidas.
- **Implementação no VitaSharp:** no-op `=> 0` com `Log.WarnOnce` mostrando r0–r3 (declarar 4 `uint`), para descobrir os
  argumentos quando algum jogo importar. Se o jogo travar esperando, trocar para `GpuFinish()`.
- **Validação:** só quando um jogo importar.

---

# Parte 4 — P1 de display interno

Estas funções são as variantes "Internal" (cabeça de display + slot de framebuffer). As versões em `SceDisplayUser`
(stub `SceDriverUser`) recebem os argumentos diretos; as `_sceDisplay*` em `SceDisplay` recebem uma struct `opt`
(`psp2/display.h`). Os protótipos de `SceDisplayUser` abaixo são **inferidos** removendo o `opt` dos protótipos
`_sceDisplay*Internal` do header (mesma relação que existe entre `sceDisplayGetFrameBuf` e `_sceDisplayGetFrameBuf`);
confirmar com `--trace-hle` se algum jogo importar. Para o VitaSharp só existe a cabeça 0 (LCD/OLED) e o slot 0 (jogo).

Constantes (`psp2common/display.h`): `SCE_DISPLAY_ERROR_INVALID_HEAD = 0x80290000`, `INVALID_VALUE = 0x80290001`,
`INVALID_ADDR = 0x80290002`, `INVALID_PIXELFORMAT = 0x80290003`, `INVALID_PITCH = 0x80290004`,
`INVALID_RESOLUTION = 0x80290005`, `INVALID_UPDATETIMING = 0x80290006`, `NO_FRAME_BUFFER = 0x80290007`.
`SceDisplayHead`: `MAIN_LCD_OLED = 0`, `HDMI = 1`, `SUB_LCD = 2`. `SceDisplayFrameBufType`: `GAME_APP = 0`, `LIVEAREA = 1`.

### sceDisplayGetVcountInternal
- [x] **Prioridade:** P1 · **Biblioteca:** SceDisplay · **NID:** 0x9686859E
- **Protótipo:** `int sceDisplayGetVcountInternal(int display);` (`psp2/display.h`)
- **Comportamento:** número de vblanks da cabeça `display`.
- **Retorno/erros:** para `display == 0`, o mesmo que `sceDisplayGetVcount`. Outras cabeças: o Vita não tem HDMI;
  `SCE_DISPLAY_ERROR_INVALID_HEAD` ou 0 — a confirmar. Obs.: o Vita3K mascara `sceDisplayGetVcount` com `& 0xFFFF`
  (o VitaSharp não mascara); confirmar a largura real antes de mudar qualquer uma das duas.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Display.cs`: `[Hle] int sceDisplayGetVcountInternal(int display) => display == 0 ?
  (int)VBlank.Count : unchecked((int)0x80290000);`.
- **Validação:** nenhum jogo instalado importa; teste unitário.

### sceDisplayGetFrameBufInternal
- [x] **Prioridade:** P1 · **Biblioteca:** SceDisplayUser · **NID:** 0x5213F74B
- **Protótipo (inferido):** `int sceDisplayGetFrameBufInternal(SceDisplayHead head, SceDisplayFrameBufType fb_idx, SceDisplayFrameBuf *pParam, SceDisplaySetBufSync sync);`
  (base: `_sceDisplayGetFrameBufInternal(head, fb_idx, frame_buf, opt)` em `psp2/display.h`).
- **Comportamento:** lê o framebuffer configurado num slot de uma cabeça.
- **Retorno/erros:** 0; `INVALID_VALUE` se `pParam->size` não for 0x18 nem 0x1C; `INVALID_UPDATETIMING` se `sync` não
  for 0/1; `INVALID_HEAD` para cabeça ≠ 0 (a confirmar); `INVALID_ADDR` com `pParam` nulo.
- **Estruturas:** `SceDisplayFrameBuf` (0x18): `size@0`, `base@4`, `pitch@8`, `pixelformat@0xC`, `width@0x10`,
  `height@0x14`; `SceDisplayFrameBufExt` (0x1C) acrescenta `flags@0x18`.
- **Implementação no VitaSharp:** `head 0, idx 0` → reutilizar `GetFrameBuf`; para struct de 0x1C escrever também
  `flags = 0x00100000` (bilinear, que o driver liga para structs de 0x18 — `psp2common/display.h`). `idx 1` (LiveArea)
  → zerar `base/pitch/width/height` (sem overlay do sistema no VitaSharp). Hoje `GetFrameBuf` não valida `size`;
  acrescentar a mesma validação ali.
- **Validação:** teste unitário.

### sceDisplayGetResolutionInfoInternal
- [x] **Prioridade:** P1 · **Biblioteca:** SceDisplayUser · **NID:** 0x0BFB78C6
- **Protótipo (inferido):** `int sceDisplayGetResolutionInfoInternal(SceDisplayHead head, SceDisplayResolutionInfo *info);`
  (base: `_sceDisplayGetResolutionInfoInternal(head, info, opt)`).
- **Comportamento:** resolução e modo de saída da cabeça.
- **Retorno/erros:** 0; `INVALID_VALUE` se `info->size != 0x1C`; `INVALID_HEAD` para cabeça ≠ 0 (a confirmar).
- **Estruturas:** `SceDisplayResolutionInfo` (0x1C): `size@0`, `screen_mode@4`, `width@8`, `height@0xC`,
  `output_format@0x10`, `scan_mode@0x14`, `fps@0x18 (float)`.
- **Implementação no VitaSharp:** cabeça 0: `screen_mode = 0` (`SCE_DISPLAY_SCREENMODE_INTERNAL_960X544`),
  `width = 960`, `height = 544`, `output_format = 0`, `scan_mode = 0` (progressivo), `fps = 59.94005f` (mesmo valor de
  `sceDisplayGetRefreshRate`; o header cita ≈59,940056).
- **Validação:** teste unitário.

### sceDisplaySetFrameBufInternal
- [x] **Prioridade:** P1 · **Biblioteca:** SceDisplayUser · **NID:** 0x89F86A1E
- **Protótipo (inferido):** `int sceDisplaySetFrameBufInternal(SceDisplayHead head, SceDisplayFrameBufType fb_idx, const SceDisplayFrameBuf *pParam, SceDisplaySetBufSync sync);`
  (base: `_sceDisplaySetFrameBufInternal(head, fb_idx, frame_buf, opt)`).
- **Comportamento:** define o framebuffer de um slot; `pParam` nulo desliga a varredura do slot.
- **Retorno/erros:** como `sceDisplaySetFrameBuf` (ver checagens do Vita3K: `INVALID_VALUE` para size, `INVALID_ADDR`
  para base nula, `INVALID_PITCH` se `pitch < width`, `INVALID_PIXELFORMAT`, `INVALID_UPDATETIMING`,
  `INVALID_RESOLUTION` se menor que 480×272) + `INVALID_HEAD`.
- **Estruturas:** `SceDisplayFrameBuf`/`Ext` (acima).
- **Implementação no VitaSharp:** `head 0, idx 0` → `SetFrameBuf(pParam, sync)`. `idx 1` → aceitar e ignorar (overlay
  do sistema). Aceitar struct de 0x1C também em `SetFrameBuf` (hoje só 24 bytes; Display.cs:47).
- **Validação:** teste unitário; os cenários de todos os jogos continuam apresentando frames.

---

# Parte 5 — Deferred contexts e command lists

## Arquitetura

### Como funciona no hardware

Um **deferred context** grava draws num command list sem estar dentro de uma cena, possivelmente noutra thread. Não
tem `BeginScene/EndScene`, notificações nem sync objects. O command list resultante (`SceGxmCommandList`, 32 bytes
opacos) é reproduzido por `sceGxmExecuteCommandList` num **immediate context que está dentro de uma cena**, como se os
draws tivessem sido emitidos ali. Um mesmo list pode ser executado várias vezes (listas estáticas reaproveitadas a cada
frame). Memória:

- **VDM buffer:** onde ficam os comandos gravados. Vem de `vdmRingBufferMem` ou do `vdmCallback`.
- **Vertex/fragment buffers:** de onde saem os default uniform buffers reservados durante a gravação
  (`sceGxmReserve*DefaultUniformBuffer`). Vêm dos params ou de `vertexCallback`/`fragmentCallback`.
- Callback: `void *cb(void *callbackData, SceSize requestedSize, SceSize *size)` — devolve um bloco de memória e
  escreve o tamanho real em `*size`; NULL = sem memória (`SCE_GXM_ERROR_RESERVE_FAILED`).
- Um list continua válido enquanto a memória do VDM que ele ocupa não for reutilizada; o Vita3K libera os lists cujas
  faixas de VDM são sobrescritas por uma gravação nova.

Comportamento observado no Vita3K que vale reproduzir:

- `Begin`: zera o uso dos rings vertex/fragment; pede rings aos callbacks se não houver (tamanho pedido 4096); pede VDM
  se não houver (pedido 1024).
- Draw num deferred fora de `Begin/End` → `SCE_GXM_ERROR_NOT_WITHIN_COMMAND_LIST`.
- `Execute` só em immediate dentro de cena; depois de executar, o estado do immediate é **restaurado** — os setters do
  deferred não vazam para o immediate.
- Viewport e region clip **não** fazem parte do estado que o deferred impõe: o Vita3K re-sincroniza viewport/clip do
  immediate só depois do `Execute` e não os sincroniza no `Begin` do deferred. Ou seja, um draw gravado sem que o
  deferred tenha chamado `sceGxmSetViewport`/`SetRegionClip` usa o viewport/clip da cena onde é executado. (Mais
  fiel a confirmar com hardware, mas é a única forma de listas gravadas antes da cena funcionarem.)

### Por que o desenho do VitaSharp facilita

Em Gxm.cs cada draw já captura **todo** o estado necessário: `Draw(...)` (Gxm.cs:660) copia `c.State` (struct
`DrawState`), programas, texturas (descritores de 16 bytes lidos no `Set*Texture`), streams, endereços dos uniform
buffers e, então, copia os **dados** dos uniforms (`CopyUniforms`) e submete um `DrawCommand` à `GpuQueue`. Não há
estado incremental no renderer. Então gravar = guardar a "receita" do draw; reproduzir = transformar a receita em
`DrawCommand` e submeter.

### Mudanças propostas

1. **Separar receita de submissão.** Novo tipo em `Gxm/GxmObjects.cs`:

   ```text
   sealed class DrawRecipe {
     GxmVertexProgram Vp; GxmFragmentProgram Fp; DrawState State;
     uint[15] VUbs, FUbs;              // endereços (não os dados)
     GxmTextureDesc[16] VTex, FTex; uint[16] Streams;
     byte Prim; bool Index32; uint IndexAddr, IndexCount, InstanceCount;
     uint VisAddress; bool VisIncrement;
     bool ViewportSet, RegionClipSet;  // só para deferred
   }
   ```

   Refatorar `Draw(...)` em `BuildRecipe(c, ...)` (validações + captura) e `SubmitRecipe(recipe, DrawState? sceneState)`
   (rent `DrawCommand`, `CopyUniforms` a partir de `recipe.VUbs/FUbs`, `Gpu.Submit`). O caminho imediato chama os dois
   em sequência, sem alocar (reusar uma receita scratch por contexto, como `StreamScratch`). A contabilidade de
   `VertexDefaultReserved/FragmentDefaultReserved` continua no momento da gravação.

2. **Contexto deferred.** `GxmContext` ganha:
   `bool Deferred; bool InCommandList; uint VdmCallback, VertexCallback, FragmentCallback, CallbackData;`
   `uint VdmBuffer, VdmBufferSize, VdmUsed, VdmListStart; uint SizeScratch;` (4 bytes de memória guest alocados com
   `Emu.Memory.Alloc` para o `*size` dos callbacks) `List<DrawRecipe>? Recording; bool ViewportSet, RegionClipSet;`.
   `sceGxmSetViewport`, `sceGxmSetViewportEnable`, `sceGxmSetDefaultRegionClipAndViewport` marcam `ViewportSet`;
   `sceGxmSetRegionClip`/`SetDefaultRegionClipAndViewport` marcam `RegionClipSet` (só tem efeito em deferred). `Begin`
   zera os dois.

3. **Gravação.** Em `Draw`/`DrawPrecomputed`/`DrawInstanced`: se `c.Deferred`, a checagem `!c.InScene` vira
   `!c.InCommandList → ERR_NOT_WITHIN_COMMAND_LIST`; a receita (cópia nova, não a scratch) vai para `c.Recording`, com
   `ViewportSet/RegionClipSet` do contexto. Consumir VDM: `EnsureVdm(VdmCostPerDraw)` — avança `VdmUsed`; se não couber,
   fecha a faixa atual (ver 5) e pede bloco novo ao `vdmCallback` (pedido 1024 bytes, como o Vita3K). Valor de
   `VdmCostPerDraw` **a confirmar** (bytes reais por draw no VDM do SGX); usar constante 64 ajustável. Serve só para o
   jogo observar consumo e callbacks parecidos com o hardware e para o tempo de vida dos lists.

4. **Uniform buffers no deferred.** `sceGxmReserveVertex/FragmentDefaultUniformBuffer` (Gxm.cs:523/539): quando o ring
   não comporta `size` e o contexto é deferred, chamar o callback correspondente
   (`GuestThread.Current!.CallGuest(cb, CallbackData, Math.Max(size, 4096), SizeScratch)`), trocar `VertexRing` /
   `VertexRingSize = R32(SizeScratch)`, `VertexRingUsed = 0`; retorno 0 → `ERR_RESERVE_FAILED`. Immediate mantém o
   comportamento atual (volta ao início do ring).

5. **Fim de gravação e handle.** `End` grava no `SceGxmCommandList` (32 bytes): `words[0] = 0x4C435356` ("VSCL"),
   `words[1] = id` (contador global), `words[2] = handle do deferred`, resto 0. O host guarda
   `Dictionary<uint, RecordedList>` em `Gxm` (`RecordedList { DrawRecipe[] Draws; uint Owner; List<(uint Start, uint End)> VdmRanges; }`).
   Tempo de vida: ao fechar uma faixa de VDM nova, remover os lists **do mesmo contexto** cujas faixas se sobrepõem
   (a memória foi reutilizada pelo jogo, como no hardware); `DestroyDeferredContext` remove os lists do contexto;
   teto de segurança (ex.: 8192 lists) descarta os mais antigos com `Log.WarnOnce`.

6. **Execução.** `Execute(immediate, list)`: para cada receita, `DrawState st = recipe.State`; se `!recipe.ViewportSet`
   copiar `ViewportEnabled, VpOff*, VpScale*` de `immediate.State`; se `!recipe.RegionClipSet` copiar
   `RegionClipMode, ClipMin*/ClipMax*`. Depois `SubmitRecipe(recipe, st)` — os **dados** de uniform são lidos da memória
   do guest **agora** (no hardware a GPU lê os uniform buffers por ponteiro na execução; listas estáticas com buffers
   atualizados por frame dependem disso). O estado do immediate não é tocado (equivale ao "restore" do Vita3K).
   `DrawCommand` sai de `Gpu.RentDraw()` a cada execução, então o pool da `GpuQueue` continua correto mesmo executando
   o mesmo list várias vezes.

7. **Threads.** Deferred contexts são gravados em outras threads guest. `_contexts` já é `ConcurrentDictionary`; o
   dicionário de lists precisa de `lock`. `Gpu.Submit` é thread-safe, mas a **ordem** dos draws executados vem da thread
   que chama `Execute` (a do immediate), que é a correta.

8. Funções de cena chamadas com deferred (`BeginScene`, `EndScene`, `MidSceneFlush`, `VertexFence`, `Execute` como
   destino) → `ERR_INVALID_VALUE` (Vita3K faz isso no `Execute`/`MidSceneFlush`; para `BeginScene` a confirmar).

## Funções

`SceGxmDeferredContextParams` (0x30, `psp2/gxm.h`):

| Offset | Campo |
| --- | --- |
| 0x00 | `hostMem` |
| 0x04 | `hostMemSize` |
| 0x08 | `vdmCallback` |
| 0x0C | `vertexCallback` |
| 0x10 | `fragmentCallback` |
| 0x14 | `callbackData` |
| 0x18 | `vdmRingBufferMem` |
| 0x1C | `vdmRingBufferMemSize` |
| 0x20 | `vertexRingBufferMem` |
| 0x24 | `vertexRingBufferMemSize` |
| 0x28 | `fragmentRingBufferMem` |
| 0x2C | `fragmentRingBufferMemSize` |

### sceGxmCreateDeferredContext
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x249D5B00
- **Protótipo:** `int sceGxmCreateDeferredContext(const SceGxmDeferredContextParams *params, SceGxmContext **context);` (`psp2/gxm.h`)
- **Comportamento:** cria um contexto deferred dentro de `hostMem` (o contexto mora na memória do jogo). Guarda
  callbacks e buffers iniciais; nenhum é obrigatório se o callback correspondente existir.
- **Retorno/erros:** 0; `INVALID_POINTER` se `params` ou `context` forem nulos; `INVALID_VALUE` se `hostMemSize` for
  menor que o mínimo (o Vita3K usa `sizeof(SceGxmContext)` interno; o mínimo real — provavelmente
  `SCE_GXM_MINIMUM_CONTEXT_HOST_MEM_SIZE` = 2 KiB ou uma constante própria de deferred — a confirmar). `UNINITIALIZED`
  antes de `sceGxmInitialize` (a confirmar).
- **Estruturas:** tabela acima.
- **Implementação no VitaSharp:** handle = `hostMem` alinhado a 8 (como Vita3K e hardware); se `hostMem == 0`, usar
  `AllocObj(64, "SceGxmDeferredContext")`. Registrar `new GxmContext { Handle, Deferred = true, ... }` em `_contexts`
  com `VdmBuffer/VertexRing/FragmentRing` dos params e `SizeScratch` alocado.
- **Validação:** nenhum jogo instalado usa; homebrew de teste do VitaSDK com deferred context (gravar um triângulo,
  executar em cena) ou teste unitário que chama as funções HLE diretamente e confere os `DrawCommand` na `GpuQueue`
  sem backend.

### sceGxmDestroyDeferredContext
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xD6A2FF2F
- **Protótipo:** `int sceGxmDestroyDeferredContext(SceGxmContext *context);` (`psp2/gxm.h`)
- **Comportamento:** destrói o contexto; os command lists gravados por ele deixam de ser válidos.
- **Retorno/erros:** 0; `INVALID_POINTER` se o contexto não existir; `INVALID_VALUE` se for immediate;
  `ERR_WITHIN_COMMAND_LIST` (0x805B002C) se estiver gravando.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** remover de `_contexts`, remover os `RecordedList` com `Owner == handle`, liberar
  `SizeScratch`. Não precisa de `Gpu.WaitIdle()`: draws já submetidos têm cópia própria.
- **Validação:** idem Create.

### sceGxmBeginCommandList
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x944D3F83
- **Protótipo:** `int sceGxmBeginCommandList(SceGxmContext *context);` (`psp2/gxm.h`)
- **Comportamento:** começa a gravar. Zera uso dos rings vertex/fragment; obtém rings/VDM pelos callbacks se faltarem.
  O estado do deferred (programas, texturas, depth…) persiste entre lists (Vita3K) — a confirmar se o hardware zera.
- **Retorno/erros:** 0; `INVALID_POINTER` (nulo/desconhecido); `INVALID_VALUE` se immediate;
  `ERR_WITHIN_COMMAND_LIST` se já gravando; `ERR_RESERVE_FAILED` (0x805B000B) se um callback devolver NULL.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `InCommandList = true; Recording = new(); ViewportSet = RegionClipSet = false;
  VertexRingUsed = FragmentRingUsed = 0; VdmListStart = VdmBuffer + VdmUsed;` + chamadas de callback necessárias
  (arquitetura, itens 3-4).
- **Validação:** idem Create.

### sceGxmEndCommandList
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x36D85916
- **Protótipo:** `int sceGxmEndCommandList(SceGxmContext *context, SceGxmCommandList *list);` (`psp2/gxm.h`)
- **Comportamento:** termina a gravação e preenche `list` (32 bytes opacos).
- **Retorno/erros:** 0; `INVALID_POINTER` (contexto ou `list` nulos); `INVALID_VALUE` se immediate;
  `ERR_NOT_WITHIN_COMMAND_LIST` (0x805B002D) se não estava gravando.
- **Estruturas:** `SceGxmCommandList { uint32_t words[8]; }` — encoding VitaSharp: `words[0] = 0x4C435356`,
  `words[1] = id`, `words[2] = handle do deferred`, demais 0 (arquitetura, item 5).
- **Implementação no VitaSharp:** fechar faixa de VDM, criar `RecordedList`, aplicar a política de invalidação por
  sobreposição, `InCommandList = false; Recording = null`.
- **Validação:** idem Create.

### sceGxmExecuteCommandList
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xE8E139DD
- **Protótipo:** `int sceGxmExecuteCommandList(SceGxmContext *context, SceGxmCommandList *list);` (`psp2/gxm.h`)
- **Comportamento:** reproduz os draws do list no immediate `context`, dentro da cena atual. O estado do immediate
  fica como estava.
- **Retorno/erros:** 0; `INVALID_POINTER` (contexto nulo, `list` nulo, magic/id desconhecido); `INVALID_VALUE` se
  `context` for deferred; `NOT_WITHIN_SCENE` (0x805B0006) fora de cena.
- **Estruturas:** `SceGxmCommandList` (acima).
- **Implementação no VitaSharp:** arquitetura, item 6. Respeitar o mesmo controle de fila de `sceGxmEndScene`
  (`Gpu.Pending`) se o list for muito grande.
- **Validação:** idem Create; conferir com `--trace-draws=<frame>` (Runner.cs:101, `TraceDrawsFrame` do renderer) que os draws do list
  aparecem na cena certa com viewport do immediate.

### sceGxmGetDeferredContextVdmBuffer
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xAD8C2EBA
- **Protótipo (inferido; Vita3K):** `int sceGxmGetDeferredContextVdmBuffer(const SceGxmContext *deferredContext, void **mem);`
- **Comportamento:** devolve o VDM buffer atual do deferred.
- **Retorno/erros:** 0; `INVALID_POINTER` (nulos); `INVALID_VALUE` se immediate; `ERR_WITHIN_COMMAND_LIST` se gravando.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `mem.Value = c.VdmBuffer`.
- **Validação:** teste unitário.

### sceGxmSetDeferredContextVdmBuffer
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x0B9D13CE
- **Protótipo (inferido; Vita3K):** `int sceGxmSetDeferredContextVdmBuffer(SceGxmContext *deferredContext, void *mem, SceSize size);`
- **Comportamento:** troca o VDM buffer; o próximo `Begin` usa o novo (Vita3K força isso esgotando o espaço atual).
- **Retorno/erros:** 0; `INVALID_POINTER`; `INVALID_VALUE` se immediate; `ERR_WITHIN_COMMAND_LIST` se gravando.
  (O Vita3K não aplica o mínimo de 1024 aqui, só nos de vertex/fragment.)
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `VdmBuffer = mem; VdmBufferSize = size; VdmUsed = 0;`.
- **Validação:** teste unitário.

### sceGxmGetDeferredContextVertexBuffer
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x87632B9C
- **Protótipo (inferido; Vita3K):** `int sceGxmGetDeferredContextVertexBuffer(const SceGxmContext *deferredContext, void **mem);`
- **Comportamento/erros:** como a versão VDM, devolvendo `VertexRing`.
- **Retorno/erros:** idem `GetDeferredContextVdmBuffer`.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `mem.Value = c.VertexRing`.
- **Validação:** teste unitário.

### sceGxmSetDeferredContextVertexBuffer
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0x27CAD127
- **Protótipo (inferido; Vita3K):** `int sceGxmSetDeferredContextVertexBuffer(SceGxmContext *deferredContext, void *mem, SceSize size);`
- **Comportamento:** troca o ring de vertex (default uniform buffers de vértice).
- **Retorno/erros:** 0; `INVALID_POINTER`; `INVALID_VALUE` se immediate, se `size != 0 && size < 1024`
  (`SCE_GXM_DEFERRED_CONTEXT_MINIMUM_BUFFER_SIZE`, valor do Vita3K, a confirmar) ou se `mem != 0 && size == 0`;
  `ERR_WITHIN_COMMAND_LIST` se gravando.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `VertexRing = mem; VertexRingSize = size; VertexRingUsed = 0;`.
- **Validação:** teste unitário.

### sceGxmGetDeferredContextFragmentBuffer
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xFA081D05
- **Protótipo (inferido; Vita3K):** `int sceGxmGetDeferredContextFragmentBuffer(const SceGxmContext *deferredContext, void **mem);`
- **Comportamento/erros:** como a versão VDM, devolvendo `FragmentRing`.
- **Retorno/erros:** idem.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `mem.Value = c.FragmentRing`.
- **Validação:** teste unitário.

### sceGxmSetDeferredContextFragmentBuffer
- [x] **Prioridade:** P1 · **Biblioteca:** SceGxm · **NID:** 0xC6B3FCD0
- **Protótipo (inferido; Vita3K):** `int sceGxmSetDeferredContextFragmentBuffer(SceGxmContext *deferredContext, void *mem, SceSize size);`
- **Comportamento/erros:** iguais a `SetDeferredContextVertexBuffer`, para o ring de fragment.
- **Retorno/erros:** idem.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `FragmentRing = mem; FragmentRingSize = size; FragmentRingUsed = 0;`.
- **Validação:** teste unitário.

---

# Parte 6 — P1 de SceScreenShot

### sceScreenShotGetParam
- [x] **Prioridade:** P1 · **Biblioteca:** SceScreenShot · **NID:** 0xBAE9E534
- **Protótipo (inferido; ausente do header):** `int sceScreenShotGetParam(SceScreenShotParam *param);`
- **Comportamento:** devolve os metadados configurados por `SetParam`. Como a struct só tem ponteiros, o mais provável
  é que o chamador forneça os buffers (`photoTitle`/`gameTitle` com `SCE_SCREENSHOT_MAX_*_SIZE` bytes) e a função
  copie as strings para eles — a confirmar (alternativa: devolver ponteiros internos).
- **Retorno/erros:** 0; `INVALID_ARGUMENT` (0x80102F01) com `param` nulo.
- **Estruturas:** `SceScreenShotParam` (Parte 1).
- **Implementação no VitaSharp:** para cada ponteiro não nulo em `param`, escrever a string guardada em UTF-32 com NUL,
  limitada ao tamanho máximo do campo (256/256/512 bytes); string não definida → string vazia.
- **Validação:** teste unitário Set → Get.

### sceScreenShotEnableNotification
- [x] **Prioridade:** P1 · **Biblioteca:** SceScreenShot · **NID:** 0x816633FE
- **Protótipo (inferido):** `int sceScreenShotEnableNotification(void);`
- **Comportamento:** pede ao sistema que avise o app quando o usuário tirar uma screenshot (o app recebe um app event,
  lido com `sceAppUtilAppEventParseScreenShotNotification` — SceAppUtil, NID 0x2AF42D6A).
- **Retorno/erros:** 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Notification = true; return 0;`. Só gera evento quando existir captura pelo host;
  então integrar com o pacote D (SceAppUtil app events).
- **Validação:** compilar; nenhum jogo instalado importa.

### sceScreenShotDisableNotification
- [x] **Prioridade:** P1 · **Biblioteca:** SceScreenShot · **NID:** 0xDA80A228
- **Protótipo (inferido):** `int sceScreenShotDisableNotification(void);`
- **Comportamento:** desliga o aviso acima.
- **Retorno/erros:** 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Notification = false; return 0;`.
- **Validação:** compilar.

---

# Parte 7 — FORA

Funções do Razor GPU capture/trace (ferramenta de profiling do devkit). Não afetam a imagem; jogos de varejo só as
chamam em builds de debug. Se algum jogo importar, implementar como no-op `=> 0` (validar ponteiro nulo quando houver
argumento) e mover o checkbox.

### sceGxmAddRazorGpuCaptureBuffer
- [ ] **Prioridade:** FORA (P1 no TSV; Razor) · **Biblioteca:** SceGxm · **NID:** 0xE9E81073
- **Protótipo (inferido):** `int sceGxmAddRazorGpuCaptureBuffer(void *base, SceSize size);`
- **Comportamento:** registra memória para o Razor gravar capturas de GPU. Vita3K: `UNIMPLEMENTED()`.
- **Retorno/erros:** 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** no-op `[Hle] int sceGxmAddRazorGpuCaptureBuffer(uint b, uint s) => 0;`.
- **Validação:** só se importada.

### sceGxmRemoveRazorGpuCaptureBuffer
- [ ] **Prioridade:** FORA (P1 no TSV; Razor) · **Biblioteca:** SceGxm · **NID:** 0x0468E3F2
- **Protótipo (inferido):** `int sceGxmRemoveRazorGpuCaptureBuffer(void *base);`
- **Comportamento:** desfaz o registro acima.
- **Retorno/erros:** 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** no-op.
- **Validação:** só se importada.

### sceGxmPadTriggerGpuPaTrace
- [ ] **Prioridade:** FORA (P1 no TSV; Razor) · **Biblioteca:** SceGxm · **NID:** 0x47E06984
- **Protótipo:** `int sceGxmPadTriggerGpuPaTrace();` (`psp2/gxm.h`)
- **Comportamento:** dispara um trace do parameter buffer para o Razor. Vita3K: `UNIMPLEMENTED()`.
- **Retorno/erros:** 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** no-op.
- **Validação:** só se importada.

### NID_1625D348 (sceGxmPrecomputedVertexStateSetTexture, NID antigo)
- [x] **Prioridade:** P1 (Uncharted) · **Biblioteca:** SceGxm · **NID:** 0x1625D348
- **Protótipo:** `int sceGxmPrecomputedVertexStateSetTexture(SceGxmPrecomputedVertexState *state, unsigned int textureIndex, const SceGxmTexture *texture);`
- **Identificação:** ausente do vita-headers e do Vita3K. O `uncharted.self` (SDK antigo) importa este NID e não importa
  0x6A29EB06; os dois pontos de chamada (0x8123221A, 0x8123329A) escolhem entre ele e
  `sceGxmPrecomputedFragmentStateSetTexture` (0x29118BF1) pelo tipo do estado pré-computado (1 = fragment), com os
  mesmos argumentos `(state, index, texture)`. Logo é o SetTexture do estado de vértice.
- **Implementação no VitaSharp:** segundo `[Hle(Nid = 0x1625D348)]` em `sceGxmPrecomputedVertexStateSetTexture` (`Gxm.cs`).
