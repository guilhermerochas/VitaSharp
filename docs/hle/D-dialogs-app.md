# D — Diálogos e app: SceCommonDialog, SceAppUtil, SceAppMgrUser

Especificação HLE do pacote D (ver [README](README.md) para prioridades, template e convenções).

**Escopo:** biblioteca `SceCommonDialog` (todas as famílias de diálogo, inclusive as já implementadas em
`CommonDialog`, `Net` e `Np` quando o stub descarta comportamento), `SceAppUtil` e `SceAppMgrUser` (variante exportada
por `SceDriverUser`, NIDs diferentes dos `_sceAppMgr*` de `SceAppMgr`).

**Fontes usadas:** vita-headers `db/360` (`SceCommonDialog.yml`, `SceAppUtil.yml`, `SceDriverUser.yml`), headers
`psp2/common_dialog.h`, `ime_dialog.h`, `message_dialog.h`, `netcheck_dialog.h`, `photo_import_dialog.h`,
`photo_review_dialog.h`, `apputil.h`, `appmgr.h`, `psp2common/appmgr.h`; Vita3K
`modules/SceCommonDialog/SceCommonDialog.cpp`, `modules/SceAppUtil/SceAppUtil.cpp`, `emuenv/app_util.h`,
`dialog/types.h`, `dialog/state.h`, `modules/SceAppMgr/SceAppMgr.cpp`, `modules/SceDriverUser/SceAppMgrUser.cpp`,
`gui-qt/src/ime_keyboard_filter.cpp`; código VitaSharp (`AppUtil.cs`, `Online.cs`, `NpTrophy.cs`, `SystemMisc.cs`,
`Io/Vfs.cs`, `Emulator.cs`); análise estática do eboot de Killzone Mercenary JP (seção [NIDs sem nome](#nids-sem-nome-do-killzone-mercenary-jp-pcsc00045)).

## Contagem

| Prioridade | SceCommonDialog | SceAppUtil | SceAppMgrUser | Total |
| --- | ---: | ---: | ---: | ---: |
| P0 | 14 (9 nomeadas + 5 NIDs sem nome do Killzone) | 2 | 0 | **16** |
| STUB | 15 | 10 | 1 | **26** |
| P1 | 71 | 16 | 54 | **141** |
| FORA | 4 (`scePspSaveDataDialog*`) | 0 | 66 | **70** |
| **Total** | 104 | 28 | 121 | **253** |

Jogos P0: Invizimals: The Resistance (PCSA00552), Sakatsuku 2013 (PCSG00198), Killzone Mercenary JP (PCSC00045).

## Ordem sugerida de implementação

1. **Estado global de diálogo** ([seção comum](#seção-comum-máquina-de-estados-dos-diálogos)): classe
   `CommonDialogState` com um único slot ativo, códigos de erro corretos e `sceCommonDialogIsRunning`. Corrige de
   passagem os STUBs de MsgDialog, SaveDataDialog, NetCheckDialog, NpMessageDialog e NpTrophySetupDialog.
2. **ImeDialog** (P0 Invizimals e Sakatsuku): versão mínima com auto-aceite + interface `IDialogBackend` para o
   Desktop.
3. **Família desconhecida do Killzone** (5 NIDs P0): registrar com `Nid = 0x...` usando a mesma máquina de estados,
   terminando como "cancelado".
4. **NpFriendListDialog** e **`sceNetCheckDialogGetPS3ConnectInfo`** (P0 Invizimals/Sakatsuku).
5. **`sceAppUtilAppEventParseNpAppDataMessage`** e **`sceAppUtilStoreBrowse`** (P0).
6. STUBs de SceAppUtil com efeito em save (`sceAppUtilSaveDataDataRemove` com stride errado,
   `sceAppUtilSaveDataGetQuota`, `sceAppUtilAppParamGetInt`) e `sceAppMgrAppParamGetString` (IDs 8/9 trocados).
7. P1 de diálogos (todos seguem o padrão "termina cancelado"), depois P1 de SceAppUtil, por fim SceAppMgrUser.

## Índice

- [Seção comum: máquina de estados dos diálogos](#seção-comum-máquina-de-estados-dos-diálogos)
- [Funções globais da SceCommonDialog](#funções-globais-da-scecommondialog)
- [ImeDialog](#imedialog)
- [NIDs sem nome do Killzone Mercenary JP](#nids-sem-nome-do-killzone-mercenary-jp-pcsc00045)
- [NetCheckDialog](#netcheckdialog)
- [NpFriendListDialog e NpFriendList2Dialog](#npfriendlistdialog-e-npfriendlist2dialog)
- [MsgDialog](#msgdialog)
- [SaveDataDialog, NpMessageDialog, NpTrophySetupDialog (STUB)](#savedatadialog-npmessagedialog-nptrophysetupdialog-stub)
- [Demais famílias de diálogo (P1)](#demais-famílias-de-diálogo-p1)
- [SceAppUtil](#sceapputil)
- [SceAppMgrUser](#sceappmgruser)

---

## Seção comum: máquina de estados dos diálogos

### Modelo do hardware

- O sistema só tem **um** diálogo comum ativo por processo. Chamar `*Init` de qualquer família enquanto outro
  diálogo (de qualquer família) não passou por `*Term` retorna `SCE_COMMON_DIALOG_ERROR_BUSY`. O Vita3K modela isso com
  um único `DialogState { type, status, result }` (`dialog/state.h`); o VitaSharp hoje tem um campo de status por
  família (`_msg`, `_save` em `CommonDialog`, nenhum em `Net`/`Np`/`NpTrophy`), o que permite dois diálogos ao mesmo
  tempo e faz `GetStatus` de famílias não iniciadas devolver `FINISHED`.
- Estados (`SceCommonDialogStatus`): `NONE = 0`, `RUNNING = 1`, `FINISHED = 2`.
- Resultado (`SceCommonDialogResult`, primeiro campo de quase todo `*Result`): `OK = 0`, `USER_CANCELED = 1`,
  `ABORTED = 2`.
- Ciclo: `Init` (NONE → RUNNING) → o jogo chama `sceCommonDialogUpdate` a cada frame (o sistema desenha o diálogo sobre
  o render target do jogo) e faz polling em `GetStatus` → usuário confirma/cancela (RUNNING → FINISHED) ou o jogo chama
  `Abort` (→ FINISHED, `result = ABORTED`) → `GetResult` → `Term` (FINISHED → NONE).
- Parâmetro comum `SceCommonDialogParam` (0x4C bytes), embutido logo após `sdkVersion` em todo `*Param`:

  | Offset | Campo | Notas |
  | --- | --- | --- |
  | 0x00 | `SceCommonDialogInfobarParam *infobarParam` | pode ser NULL |
  | 0x04 | `SceCommonDialogColor *bgColor` | pode ser NULL |
  | 0x08 | `SceCommonDialogColor *dimmerColor` | pode ser NULL |
  | 0x0C | `reserved[60]` | |
  | 0x48 | `SceUInt32 magic` | `0xC0D1A109 + endereço_guest(&commonParam)` (`_sceCommonDialogSetMagicNumber`) |

  Em todos os `*Param` com `sdkVersion` em 0x00, o `commonParam` fica em 0x04 e o magic em `param + 0x4C`, e o
  primeiro campo específico da família começa em **0x50**.

### Erros comuns (`SceCommonDialogErrorCode`, `psp2/common_dialog.h`)

| Nome | Valor | Quando |
| --- | --- | --- |
| `SCE_COMMON_DIALOG_ERROR_BUSY` | 0x80020401 | `Init` com outro diálogo ativo (status != NONE) |
| `SCE_COMMON_DIALOG_ERROR_NULL` | 0x80020402 | ponteiro de `Param`/`Result` NULL |
| `SCE_COMMON_DIALOG_ERROR_INVALID_ARGUMENT` | 0x80020403 | campo fora de faixa / magic inválido (a confirmar qual dos dois o firmware usa para magic) |
| `SCE_COMMON_DIALOG_ERROR_NOT_RUNNING` | 0x80020404 | `Close`/`Abort`/`Continue` sem diálogo RUNNING |
| `SCE_COMMON_DIALOG_ERROR_NOT_SUPPORTED` | 0x80020405 | operação não suportada no modo (ex.: `ProgressBar*` fora do modo barra) |
| `SCE_COMMON_DIALOG_ERROR_NOT_CONFIGURED` | 0x80020407 | a confirmar (provável: `SetConfigParam` não chamado quando exigido) |
| `SCE_COMMON_DIALOG_ERROR_NOT_AVAILABLE` | 0x80020408 | informação indisponível no estado atual |
| `SCE_COMMON_DIALOG_ERROR_NOT_FINISHED` | 0x80020410 | `GetResult`/`Term` antes de FINISHED |
| `SCE_COMMON_DIALOG_ERROR_NOT_IN_USE` | 0x80020411 | `Term`/`Abort`/`GetResult` de família que não é a ativa |
| `SCE_COMMON_DIALOG_ERROR_WITHIN_SCENE` | 0x80020424 | `sceCommonDialogUpdate` dentro de `sceGxmBeginScene/EndScene` |
| `SCE_COMMON_DIALOG_ERROR_IME_IN_USE` | 0x80020430 | `sceImeDialogInit` com `sceImeOpen` (libime) aberto |
| `SCE_COMMON_DIALOG_ERROR_GXM_IS_UNINITIALIZED` | 0x80020436 | `Init`/`Update` sem `sceGxmInitialize` (não emular: o VitaSharp não precisa da GXM para "desenhar") |

Regras por função (aplicam-se a toda família salvo indicação):

| Função | Pré-condição | Erro se violada | Efeito |
| --- | --- | --- | --- |
| `Init(param)` | slot livre | `BUSY`; `NULL` se `param == 0` | valida campos, guarda cópia, `type = família`, `status = RUNNING`, `result = OK` |
| `GetStatus()` | — | — | `type == família ? status : NONE` (nunca erro) |
| `GetResult(res)` | `type == família` e `status == FINISHED` | `NOT_FINISHED` (Vita3K usa NOT_FINISHED também para outra família; o firmware pode usar `NOT_IN_USE` — a confirmar) ; `NULL` se `res == 0` | escreve a struct `*Result` (campo 0 = `SceCommonDialogResult`) |
| `Abort()` | `type == família` | `NOT_IN_USE` (se já FINISHED: retorna 0 sem mudar `result`, a confirmar) | `status = FINISHED`, `result = ABORTED` |
| `Term()` | `type == família` e `status == FINISHED` | `NOT_IN_USE` / `NOT_FINISHED` | libera o slot (`type = nenhum`, `status = NONE`) |

### Duas estratégias de implementação

1. **Mínima ("FINISHED com resultado cancelado")**: `Init` valida e já deixa `status = FINISHED` com
   `result = USER_CANCELED` (ou o resultado padrão descrito por família). É o que o VitaSharp já faz para MsgDialog,
   NetCheck e TrophySetup. Funciona para diálogos de serviços online (o console emulado está offline e deslogado da
   PSN) e é o fallback do modo headless (`--headless`, cenários `tests/scenarios`).
   Atenção: alguns jogos só esperam ver `RUNNING` pelo menos uma vez antes de `FINISHED`; se um jogo travar em loop,
   devolver `RUNNING` na primeira chamada de `GetStatus` após `Init` e `FINISHED` a partir da segunda (contador no
   estado global) — comportamento seguro em ambos os casos.
2. **Integrada à UI do Desktop**: necessária para ImeDialog (texto digitado pelo usuário) e opcional para MsgDialog/
   SaveDataDialog. Proposta:
   - Em `src/VitaSharp.Core/Host/HostServices.cs` adicionar `public IDialogBackend? Dialogs { get; set; }` com
     `interface IDialogBackend { void ShowText(ImeDialogRequest req, Action<ImeDialogResponse> done); void ShowMessage(MsgDialogRequest req, Action<int /*buttonId*/> done); }`
     (requests com título/texto em `string`, `maxLength`, `multiline`, `password`, `cancelable`; response com
     `string? text` e `bool cancelled`).
   - `Init` chama o backend e deixa `status = RUNNING`; o callback (thread da UI) grava o resultado sob `lock` e muda
     para `FINISHED`. `GetStatus` só lê o estado (o jogo continua renderizando; o VitaSharp não desenha o diálogo sobre
     o framebuffer do jogo — o diálogo é uma janela/overlay do host).
   - `src/VitaSharp.Desktop/Host/WindowHost.cs` implementa o backend (janela modal ou caixa de texto sobre a janela
     Silk.NET; enquanto aberto, não repassar teclas ao `IInputBackend` do jogo).
   - `ScriptedInput` (cenários) e `--headless` implementam o backend respondendo de imediato (texto de
     `--ime-text=<texto>`, botão padrão), mantendo os cenários determinísticos.

### Onde fica no VitaSharp

- Criar `src/VitaSharp.Core/Hle/Modules/CommonDialogState.cs` (classe simples, não `HleModule`) com
  `enum DialogType { None, Msg, SaveData, Ime, NetCheck, NpMessage, NpTrophySetup, NpFriendList, ..., KzUnknown }`,
  `int Status`, `int Result`, `object? Data`, `int Busy(DialogType t)`, helpers `Begin(t)`, `Finish(result)`,
  `TermCheck(t)`, `ResultCheck(t)` e `lock`. Uma instância por `Emulator` (ex.: propriedade em `CommonDialog` acessada
  pelos outros módulos via `Emu.Hle.Modules.OfType<CommonDialog>().First()`, como `CommonDialog` já faz com `AppUtil`).
- Mover os diálogos que hoje estão em `Net` (`sceNetCheckDialog*`), `Np` (`sceNpMessageDialog*`) e `NpTrophy`
  (`sceNpTrophySetupDialog*`) para a classe `CommonDialog` (ou fazê-los usar o estado global). São todos da biblioteca
  `SceCommonDialog`.
- Constantes de erro: adicionar `static class CommonDialogError` em `CommonDialogState.cs`.
- Helpers de UTF-16 que não existem em `GuestMemory`: `string ReadUtf16(uint addr, int maxChars)` e
  `void WriteUtf16(uint addr, string s, int maxCharsIncludingNul)` em `src/VitaSharp.Core/Memory/GuestMemory.cs`.

---

## Funções globais da SceCommonDialog

### sceCommonDialogIsRunning
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0x043A353E
- **Protótipo:** `SceBool sceCommonDialogIsRunning(void);` (inferido; sem protótipo em `common_dialog.h`)
- **Comportamento:** verdadeiro enquanto há um diálogo comum ativo (status RUNNING; a confirmar se FINISHED-sem-Term
  também conta). Jogos usam para não abrir um segundo diálogo e para pausar input.
- **Retorno/erros:** 1/0, sem erros.
- **Estruturas:** —
- **Implementação no VitaSharp:** hoje `CommonDialog.sceCommonDialogIsRunning() => false` (`Online.cs`). Passar a
  retornar `state.Type != None && state.Status == RUNNING`. Só faz diferença depois que algum diálogo ficar RUNNING
  (ImeDialog integrado, SaveDataDialog modo barra).
- **Validação:** com ImeDialog integrado, abrir o teclado no Sakatsuku (`st_*`) e checar no `--trace-hle-only=sceCommonDialog` que o retorno é 1 enquanto a janela está aberta.

### sceCommonDialogUpdate
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0x90530F2F
- **Protótipo:** `int sceCommonDialogUpdate(const SceCommonDialogUpdateParam *updateParam);` (`psp2/common_dialog.h`)
- **Comportamento:** chamada uma vez por frame, depois de `sceGxmEndScene` e antes do flip; o sistema desenha o
  diálogo ativo no render target indicado e avança a animação/estado. Sem diálogo ativo, não faz nada.
- **Retorno/erros:** 0; `SCE_COMMON_DIALOG_ERROR_NULL` (0x80020402) se `updateParam == NULL`;
  `INVALID_COLOR_FORMAT`/`INVALID_SURFACE_*` (0x80020420–0x80020423) para render target inválido (não emular);
  `WITHIN_SCENE` (0x80020424) se chamada entre `BeginScene/EndScene` (não emular).
- **Estruturas:** `SceCommonDialogUpdateParam` (0x60): `renderTarget` (`SceCommonDialogRenderTargetInfo`, 0x3C:
  0x00 `depthSurfaceData`, 0x04 `colorSurfaceData`, 0x08 `surfaceType`, 0x0C `colorFormat`, 0x10 `width`,
  0x14 `height`, 0x18 `strideInPixels`, 0x1C reserved[32]) e 0x3C `displaySyncObject`, 0x40 reserved[32].
- **Implementação no VitaSharp:** hoje retorna 0 sem olhar nada (aceitável). Mudanças: retornar `NULL` para ponteiro
  zero; usar a chamada como "tick" do diálogo (ex.: contador de frames para a estratégia RUNNING→FINISHED e para
  `sceNpTrophySetupDialog` com opção de 3 s como o Vita3K). Não desenhar no framebuffer.
- **Validação:** Killzone (`kz_boot`) e Sakatsuku chamam por frame; checar ausência de regressão.

### sceCommonDialogSetConfigParam
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0xBECD35C8
- **Protótipo:** `int sceCommonDialogSetConfigParam(const SceCommonDialogConfigParam *configParam);` (`psp2/common_dialog.h`)
- **Comportamento:** define idioma e botão de confirmação usados pelos diálogos do sistema. Valores
  `SCE_SYSTEM_PARAM_*_MAX_VALUE` (0xFFFFFFFF) significam "usar o do sistema".
- **Retorno/erros:** 0; `NULL` (0x80020402) se ponteiro zero; `INVALID_LANGUAGE` (0x80020431),
  `INVALID_ENTER_BUTTON_ASSIGN` (0x80020432) para valores fora de faixa.
- **Estruturas:** `SceCommonDialogConfigParam` (0x2C): 0x00 `sdkVersion`, 0x04 `language`, 0x08 `enterButtonAssign`,
  0x0C reserved[32].
- **Implementação no VitaSharp:** guardar `language`/`enterButtonAssign` no estado global e repassar ao
  `IDialogBackend` (rótulos OK/Cancelar, troca círculo/xis). Hoje descarta.
- **Validação:** teste unitário simples; nenhum jogo depende do efeito offline.

### sceCommonDialogGetWorkerThreadId
- [ ] **Prioridade:** P1 · **Biblioteca:** SceCommonDialog · **NID:** 0xD4C37375
- **Protótipo:** `SceUID sceCommonDialogGetWorkerThreadId(void);` (inferido)
- **Comportamento:** retorna o UID da thread de trabalho interna da biblioteca de diálogos (usado para ajuste de
  prioridade/afinidade).
- **Retorno/erros:** UID > 0; erro a confirmar quando a biblioteca não está inicializada.
- **Estruturas:** —
- **Implementação no VitaSharp:** em `CommonDialog`; não há thread real. Retornar o UID de uma thread "fantasma"
  criada uma vez (ou o da thread principal) — se o jogo chamar `sceKernelChangeThreadPriority` nesse UID, precisa ser um
  UID válido em `K.Objects`. Alternativa mais simples: devolver o UID da thread chamadora (a confirmar efeito).
- **Validação:** nenhum jogo instalado importa; testar com homebrew se surgir.

---

## ImeDialog

Teclado de tela do sistema. P0 em Invizimals (PCSA00552) e Sakatsuku 2013 (PCSG00198): nome de time/jogador.
Hoje nenhuma das 5 funções existe, então `Init` cai no "Unimplemented HLE function" (retorna 0) e `GetStatus` retorna 0
(NONE) para sempre: o jogo espera um diálogo que nunca termina ou trata NONE como erro.

### Estruturas (`psp2/ime_dialog.h`, `psp2/libime.h`)

`SceImeDialogParam` (0xA8):

| Offset | Campo | Notas |
| --- | --- | --- |
| 0x00 | `SceUInt32 sdkVersion` | |
| 0x04 | `SceUInt32 inputMethod` | 0 = padrão |
| 0x08 | `SceUInt64 supportedLanguages` | máscara `SceImeLanguage`; 0 = todas |
| 0x10 | `SceBool languagesForced` | |
| 0x14 | `SceUInt32 type` | `SceImeType`: DEFAULT 0, BASIC_LATIN 1, NUMBER 2, EXTENDED_NUMBER 3, URL 4, MAIL 5 (`libime.h`) |
| 0x18 | `SceUInt32 option` | `SCE_IME_OPTION_MULTILINE` 0x01, `NO_AUTO_CAPITALIZATION` 0x02, `NO_ASSISTANCE` 0x04 |
| 0x1C | `SceImeTextFilter filter` | callback guest opcional `SceInt32 (*)(SceWChar16 *outText, SceUInt32 *outTextLength, const SceWChar16 *srcText, SceUInt32 srcTextLength)` (`libime.h`); o mínimo é ignorar; com UI, chamar via `CallGuest` a cada edição (a confirmar quando o firmware chama) |
| 0x20 | `SceUInt32 dialogMode` | `DEFAULT` 0, `WITH_CANCEL` 1 |
| 0x24 | `SceUInt32 textBoxMode` | `DEFAULT` 0, `PASSWORD` 1, `WITH_CLEAR` 2 |
| 0x28 | `const SceWChar16 *title` | UTF-16LE terminado em 0; até `SCE_IME_DIALOG_MAX_TITLE_LENGTH` = 128 caracteres |
| 0x2C | `SceUInt32 maxTextLength` | em unidades UTF-16, 1..`SCE_IME_DIALOG_MAX_TEXT_LENGTH` (2048) |
| 0x30 | `SceWChar16 *initialText` | pode ser NULL |
| 0x34 | `SceWChar16 *inputTextBuffer` | buffer de saída, capacidade `maxTextLength + 1` unidades |
| 0x38 | `SceCommonDialogParam commonParam` | magic em 0x80 (= 0xC0D1A109 + param + 0x38) |
| 0x84 | `SceUChar8 enterLabel` | rótulo do botão Enter (`SCE_IME_ENTER_LABEL_*`: DEFAULT 0, SEND 1, SEARCH 2, GO 3, `libime.h`) |
| 0x85 | `reserved[35]` | |

Atenção: aqui o `commonParam` **não** fica em 0x04 (diferente das outras famílias).

`SceImeDialogResult` (0x24): 0x00 `SceInt32 result` (`SceCommonDialogResult`), 0x04 `SceInt32 button`
(`SCE_IME_DIALOG_BUTTON_NONE` 0, `CLOSE` 1, `ENTER` 2), 0x08 reserved[28].

Erros próprios (`SceImeDialogErrorCode`): `PARAM` 0x80102001, `INTERNAL` 0x80102002, `INVALID_DIALOG_MODE` 0x80102003,
`INVALID_TEXT_BOX_MODE` 0x80102004, `INVALID_TITLE` 0x80102005.

### Semântica do buffer de saída

- O texto final é escrito em `inputTextBuffer` como UTF-16LE, truncado a `maxTextLength` unidades, seguido de um
  terminador 0 (o Vita3K faz `copy_len = min(len, max_length); memcpy; result[copy_len] = 0`).
- Só é escrito quando o usuário confirma (botão ENTER). Em cancelamento o conteúdo fica como estava (a confirmar se o
  firmware restaura `initialText`).
- Pares substitutos UTF-16 contam como 2 unidades; não cortar no meio de um par (ao truncar, se a última unidade for um
  high surrogate, descartá-la).
- Converter `\r\n` do host para `\n` e remover quebras de linha se `option & MULTILINE == 0`.
- `textBoxMode == PASSWORD`: o backend deve mascarar a entrada; o texto devolvido é o real.
- `type == NUMBER`/`EXTENDED_NUMBER`: o backend deve filtrar caracteres não numéricos (o fallback headless só copia).

### Resultado por desfecho

| Desfecho | `status` | `result` | `button` | buffer |
| --- | --- | --- | --- | --- |
| Usuário confirma | FINISHED | OK (0) | ENTER (2) | texto escrito |
| Usuário cancela, `dialogMode == WITH_CANCEL` | FINISHED | USER_CANCELED (1) | CLOSE (1) | intacto |
| Cancelamento com `dialogMode == DEFAULT` | não permitido: o diálogo só fecha com ENTER (Vita3K) | | | |
| `sceImeDialogAbort` | FINISHED | ABORTED (2) | NONE (0) | intacto |

### sceImeDialogInit
- [ ] **Prioridade:** P0 (Invizimals PCSA00552, Sakatsuku PCSG00198) · **Biblioteca:** SceCommonDialog · **NID:** 0x1E7043BF
- **Protótipo:** `SceInt32 sceImeDialogInit(const SceImeDialogParam *param);` (`psp2/ime_dialog.h`)
- **Comportamento:** valida o parâmetro, ocupa o slot global de diálogo, lê título e texto inicial, abre o teclado e
  deixa o diálogo RUNNING. Copia o que precisa da struct (o jogo pode reutilizar a memória do `param` depois do
  retorno, mas `inputTextBuffer` precisa continuar válido até o fim).
- **Retorno/erros:** 0;
  `SCE_COMMON_DIALOG_ERROR_BUSY` 0x80020401 (outro diálogo ativo);
  `SCE_COMMON_DIALOG_ERROR_NULL` 0x80020402 (`param == NULL`; a confirmar se o firmware usa `SCE_IME_DIALOG_ERROR_PARAM`);
  `SCE_COMMON_DIALOG_ERROR_IME_IN_USE` 0x80020430 (`sceImeOpen` ativo — só se o módulo libime do VitaSharp expuser esse estado);
  `SCE_IME_DIALOG_ERROR_PARAM` 0x80102001 (`maxTextLength == 0` ou > 2048, `inputTextBuffer == NULL`);
  `SCE_IME_DIALOG_ERROR_INVALID_DIALOG_MODE` 0x80102003 (`dialogMode > 1`);
  `SCE_IME_DIALOG_ERROR_INVALID_TEXT_BOX_MODE` 0x80102004 (`textBoxMode > 2`);
  `SCE_IME_DIALOG_ERROR_INVALID_TITLE` 0x80102005 (título com mais de 128 caracteres).
  Os critérios exatos de cada erro são inferidos dos nomes; confirmar só se um jogo depender deles. Magic inválido:
  apenas logar (não falhar) até haver confirmação.
- **Estruturas:** `SceImeDialogParam`, acima.
- **Implementação no VitaSharp:** novo método em `CommonDialog` (`Online.cs`, ou novo `CommonDialog.Ime.cs` como
  `partial`). Passos: `state.Busy` → ler `title`/`initialText` com `GuestMemory.ReadUtf16` → guardar
  `ImeData { InputBuffer, MaxLength, Multiline, Password, Cancelable, Type }` → `state.Begin(Ime)`.
  Estratégia mínima (headless e enquanto não houver `IDialogBackend`): concluir com ENTER e texto =
  `--ime-text` (opção nova em `EmulatorOptions`, ex. `ImeDefaultText`) ou, se ausente, `initialText`; se ambos vazios
  usar `"Player"` (heurística: muitos jogos recusam nome vazio e reabrem o teclado em loop). Escrever o buffer na hora
  da conclusão, não no `Init`. Logar título e texto escolhido (`Log.Info`).
  Estratégia integrada: `Emu.Host.Dialogs?.ShowText(...)`, status RUNNING até o callback.
- **Validação:** Sakatsuku: cenário `st_*` até a tela de nome do clube (adicionar `--trace-hle-only=sceImeDialog`);
  conferir que o nome aparece na UI do jogo. Invizimals: criar `tests/scenarios/inv_ime.txt` até a criação de perfil.
  `VitaSharp.Desktop imports PCSA00552` / `PCSG00198` não devem mais listar `sceImeDialog*`.

### sceImeDialogGetStatus
- [ ] **Prioridade:** P0 (Invizimals PCSA00552, Sakatsuku PCSG00198) · **Biblioteca:** SceCommonDialog · **NID:** 0xCF0431FD
- **Protótipo:** `SceCommonDialogStatus sceImeDialogGetStatus(void);` (`psp2/ime_dialog.h`)
- **Comportamento:** `type == Ime ? status : NONE`. Estratégia mínima: considerar devolver RUNNING na primeira chamada
  depois do `Init` (ver seção comum).
- **Retorno/erros:** 0/1/2; nunca erro.
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`, leitura sob `lock` do estado global.
- **Validação:** idem `sceImeDialogInit`.

### sceImeDialogGetResult
- [ ] **Prioridade:** P0 (Invizimals PCSA00552, Sakatsuku PCSG00198) · **Biblioteca:** SceCommonDialog · **NID:** 0x2EB3D046
- **Protótipo:** `SceInt32 sceImeDialogGetResult(SceImeDialogResult *result);` (`psp2/ime_dialog.h`)
- **Comportamento:** preenche `result` com o desfecho (tabela acima). Não escreve o texto (já está em `inputTextBuffer`).
- **Retorno/erros:** 0; `SCE_COMMON_DIALOG_ERROR_NOT_FINISHED` 0x80020410 se `type != Ime` ou `status != FINISHED`
  (Vita3K); `SCE_COMMON_DIALOG_ERROR_NULL` 0x80020402 se `result == NULL`.
- **Estruturas:** `SceImeDialogResult` (0x24). Zerar os 0x24 bytes e escrever `result` (0x00) e `button` (0x04).
- **Implementação no VitaSharp:** `CommonDialog`.
- **Validação:** idem; o jogo deve aceitar o nome (button == 2).

### sceImeDialogTerm
- [ ] **Prioridade:** P0 (Invizimals PCSA00552, Sakatsuku PCSG00198) · **Biblioteca:** SceCommonDialog · **NID:** 0x838A3AF4
- **Protótipo:** `SceInt32 sceImeDialogTerm(void);` (`psp2/ime_dialog.h`)
- **Comportamento:** fecha o diálogo e libera o slot.
- **Retorno/erros:** 0; `NOT_IN_USE` 0x80020411 se `type != Ime`; `NOT_FINISHED` 0x80020410 se ainda RUNNING.
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`; se houver janela do backend aberta (não deveria), fechá-la.
- **Validação:** após o nome, abrir outro diálogo (save) não pode retornar BUSY.

### sceImeDialogAbort
- [ ] **Prioridade:** P1 · **Biblioteca:** SceCommonDialog · **NID:** 0x594A220E
- **Protótipo:** `SceInt32 sceImeDialogAbort(void);` (`psp2/ime_dialog.h`)
- **Comportamento:** fecha o teclado à força: FINISHED, `result = ABORTED`, `button = NONE`, buffer intacto.
- **Retorno/erros:** 0; `NOT_IN_USE` 0x80020411 se `type != Ime`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`; pedir ao backend para fechar a janela.
- **Validação:** teste unitário (Init → Abort → GetResult → Term).

---

## NIDs sem nome do Killzone Mercenary JP (PCSC00045)

Os cinco NIDs não estão no vita-headers (`db/360`, `0.931`, `0.990`) nem em `Hle/Data/nids.txt`; o Vita3K só conhece
`sceCommonDialog_B7D4C911` (stub que retorna 2). Nenhum log em `.vitadata/logs` registra chamadas a eles nos cenários
existentes (o caminho de código é o de modos online/convite).

### Evidência (análise estática feita para este documento)

Eboot descriptografado com `VitaSharp.Desktop self2elf <eboot.bin> <out.elf> <rif>` e varredura da tabela de imports e
de todos os `BL/BLX` Thumb para os stubs de `SceCommonDialog` (ferramenta descartável, fora do repositório: parse
do `sceModuleInfo` via `e_entry`, tabela de imports de 0x34 bytes, decodificação de BL/BLX T32). Stubs: `33626E2C` @0x818C2A08, `B55CF0E3` @0x818C2B28, `B7D4C911`
@0x818C2B38, `BEBAEF25` @0x818C2B68, `D1858A78` @0x818C2B98. Chamadores (todos no gerenciador de diálogos do jogo,
string `KZMSystemNGPDialogMutex`, mesma região que chama NetCheckDialog @0x815C3F2C e MsgDialog @0x815C4304):

| NID | Chamada(s) | O que o código faz | Papel deduzido |
| --- | --- | --- | --- |
| 0x33626E2C | 0x815C4154 | `r0 = sp` (struct de param na pilha); antes copia 12 bytes `"NPWR02882\0\0\0"` ou `"NPWR05168\0\0\0"` (escolha por região) para `param + 0x50` e grava um valor de `this+0x348` em `param + 0x5C`; ponteiros em `param + 0x08`/`+0x0C` (cores do `commonParam`). `r0 >= 0` → sucesso | **Init** — param com `SceNpCommunicationId` em 0x50 e um campo de 32 bits em 0x5C |
| 0xB7D4C911 | 0x815C417C | sem argumentos; `cmp r0, #2; bne` | **GetStatus** (2 = FINISHED; bate com o stub do Vita3K) |
| 0xD1858A78 | 0x815C4188 | `r0 = sp` (frame de 0x2C bytes); se `>= 0` lê `[sp+4]` e faz switch nos valores 0, 1, 2, 3 (<0 vai para erro) | **GetResult** — struct ≤ 0x2C bytes; campo +0x04 é um enum 0..3 |
| 0xBEBAEF25 | 0x815C4278 | sem argumentos, depois de tratar o resultado; `< 0` → estado de erro | **Term** |
| 0xB55CF0E3 | 0x815C3E20, 0x815C3E4C | sem argumentos, só quando o tipo de diálogo corrente do gerenciador (`[r5+0x2C]`) é 4, em dois ramos de eventos do sistema; em seguida marca um flag | **Abort** |

Conclusão: é uma família `Init/GetStatus/GetResult/Term/Abort` de um diálogo NP que recebe um NP Communication ID
(provavelmente ligado a convites/sessão: o eboot tem `StartInviteDialog`, `StopInviteDialog`,
`appdir:ICONS_NGP/INVITE/ICON0.PNG`, `MENU_SCREEN_ACCEPT_INVITE_*`). O nome oficial **não** foi identificado.

### Comportamento proposto (comum aos cinco)

Usar a máquina de estados comum com `DialogType.KzUnknown`, estratégia mínima "cancelado" (o console está offline):
`Init` → FINISHED com `result = USER_CANCELED`. Como o tamanho real do result é
desconhecido (≤ 0x2C), `GetResult` escreve só `+0x00 = 1` (USER_CANCELED) e `+0x04 = 0`, sem tocar no resto. O valor de `+0x04` que leva o
jogo ao caminho "cancelado" está **a confirmar** (ver plano). Registrar cada função com
`[Hle(Nid = 0x..., Library = "SceCommonDialog")]` e nome provisório `sceCommonDialog_<NID>`.

### Plano de investigação para nomear e fechar os valores

1. Confirmar que chegam a ser chamados: rodar o jogo até o menu online
   (`VitaSharp.Desktop run PCSC00045 --log <arq>`, entrar em "Multijogador"/aceitar convite) e procurar
   `Unimplemented HLE function SceCommonDialog::NID_33626E2C ... called from 0x815C4158` no log (o `WarnOnce` de
   `GuestThread.cs` imprime o LR). Depois de registrar os NIDs, usar `--trace-hle-only=sceCommonDialog_` para ver
   argumentos e retornos.
2. Ler a struct de parâmetro no `Init`: no handler provisório, logar `GuestMemory.Span(r0, 0x100)` e verificar
   `R32(r0+0x4C) == 0xC0D1A109 + r0 + 4` (confirma `commonParam` em 0x04 e struct começando em `sdkVersion`). O
   `memset` inline do `*ParamInit` antes de 0x815C4110 dá o `sizeof` do param (procurar `movs r2, #<tam>` + `bl memset`
   com `r0 = sp` com `VitaSharp.Desktop disasm PCSC00045 815C40A0 60`).
3. Ler o switch em 0x815C419C–0x815C4250 (`disasm PCSC00045 815C419C 80`) para mapear qual valor de `[result+4]` leva ao
   ramo "cancelado"/"erro" sem efeitos colaterais (caso 1 grava `1` em `this+0x3C`).
4. Comparar `sizeof(param)`, offsets (commId em 0x50, campo em 0x5C) e tamanho do result com SDKs/documentação de
   diálogos NP da época (candidatos: diálogo de convite de sessão NP, diálogo de comércio/assinatura NP). Quando o nome
   for confirmado, adicioná-lo a `Hle/Data/nids.txt` e abrir PR no vita-headers.
5. `sceCommonDialog_B7D4C911`: manter retorno coerente com o estado (2 após `Init` mínimo). Sem `Init` prévio, devolver
   0 (NONE) — **não** copiar o "retorna 2 sempre" do Vita3K, que faria o jogo chamar `GetResult` sem diálogo.

### sceCommonDialog_33626E2C (Init)
- [ ] **Prioridade:** P0 (Killzone Mercenary JP PCSC00045) · **Biblioteca:** SceCommonDialog · **NID:** 0x33626E2C
- **Protótipo:** `SceInt32 NID_33626E2C(const void *param);` (inferido do call site)
- **Comportamento:** inicia o diálogo desconhecido; ver tabela acima. Mínimo: valida `param != 0`, ocupa o slot, termina
  como cancelado. Logar `commId` (0x50, 12 bytes) e `R32(param+0x5C)`.
- **Retorno/erros:** 0; `BUSY` 0x80020401; `NULL` 0x80020402.
- **Estruturas:** param: 0x00 `sdkVersion`, 0x04 `SceCommonDialogParam`, 0x50 `SceNpCommunicationId` (12 bytes:
  `data[9]`, `term`, `num`, `dummy`), 0x5C `uint32` desconhecido; tamanho total a confirmar.
- **Implementação no VitaSharp:** `CommonDialog`, `[Hle(Nid = 0x33626E2C, Library = "SceCommonDialog")]`.
- **Validação:** `imports PCSC00045` sem faltantes em SceCommonDialog; cenário novo `kz_online.txt` entrando no
  multijogador não pode travar.

### sceCommonDialog_B7D4C911 (GetStatus)
- [ ] **Prioridade:** P0 (Killzone Mercenary JP PCSC00045) · **Biblioteca:** SceCommonDialog · **NID:** 0xB7D4C911
- **Protótipo:** `SceCommonDialogStatus sceCommonDialog_B7D4C911(void);` (inferido; nome do Vita3K)
- **Comportamento:** status do diálogo desconhecido (`type == KzUnknown ? status : NONE`).
- **Retorno/erros:** 0/1/2.
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`; o nome já está em `nids.txt` (`sceCommonDialog_B7D4C911`), então
  basta `[Hle]` com esse nome.
- **Validação:** idem Init.

### sceCommonDialog_D1858A78 (GetResult)
- [ ] **Prioridade:** P0 (Killzone Mercenary JP PCSC00045) · **Biblioteca:** SceCommonDialog · **NID:** 0xD1858A78
- **Protótipo:** `SceInt32 NID_D1858A78(void *result);` (inferido)
- **Comportamento:** escreve `+0x00 = result` (`SceCommonDialogResult`) e `+0x04 = <enum 0..3>`; valor de `+0x04` para
  "cancelado" a confirmar (passo 3 do plano). Não escrever além de 0x08 enquanto o tamanho não for conhecido.
- **Retorno/erros:** 0; `NOT_FINISHED` 0x80020410; `NULL` 0x80020402.
- **Estruturas:** result ≤ 0x2C bytes (frame do chamador); campos além de 0x08 desconhecidos.
- **Implementação no VitaSharp:** `CommonDialog`, `Nid = 0xD1858A78`.
- **Validação:** idem Init; conferir no trace que o jogo segue para o estado "fechado" sem mensagem de erro.

### sceCommonDialog_BEBAEF25 (Term)
- [ ] **Prioridade:** P0 (Killzone Mercenary JP PCSC00045) · **Biblioteca:** SceCommonDialog · **NID:** 0xBEBAEF25
- **Protótipo:** `SceInt32 NID_BEBAEF25(void);` (inferido)
- **Comportamento:** libera o slot.
- **Retorno/erros:** 0; `NOT_IN_USE` 0x80020411; `NOT_FINISHED` 0x80020410.
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`, `Nid = 0xBEBAEF25`.
- **Validação:** depois do Term, `sceMsgDialogInit` do jogo não pode receber BUSY.

### sceCommonDialog_B55CF0E3 (Abort)
- [ ] **Prioridade:** P0 (Killzone Mercenary JP PCSC00045) · **Biblioteca:** SceCommonDialog · **NID:** 0xB55CF0E3
- **Protótipo:** `SceInt32 NID_B55CF0E3(void);` (inferido)
- **Comportamento:** aborta o diálogo desconhecido (FINISHED, `result = ABORTED`).
- **Retorno/erros:** 0; `NOT_IN_USE` 0x80020411 (o jogo ignora o retorno).
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`, `Nid = 0xB55CF0E3`.
- **Validação:** teste unitário.

---

## NetCheckDialog

Verificação de rede/PSN antes de modos online. O VitaSharp emula um console deslogado da PSN e sem rota para a
Internet (`Online.cs`), então o desfecho padrão é "o usuário cancelou a verificação".

### Estruturas (`psp2/netcheck_dialog.h`)

`SceNetCheckDialogParam` (0xE0): 0x00 `sdkVersion`, 0x04 `commonParam`, 0x50 `mode` (`SceNetCheckDialogMode`:
INVALID 0, ADHOC_CONN 1, PSN 2, PSN_ONLINE 3, PS3_CONNECT 4, PSP_ADHOC_CONN 5, PSP_ADHOC_CREATE 6, PSP_ADHOC_JOIN 7),
0x54 `SceNpCommunicationId npCommunicationId` (12), 0x60 `SceNetCheckDialogPS3ConnectParam *ps3ConnectParam`,
0x64 `SceNetAdhocctlGroupName *groupName`, 0x68 `timeoutUs`, 0x6C `SceInt8 defaultAgeRestriction`, 0x70
`ageRestrictionCount`, 0x74 `const SceNetCheckDialogAgeRestriction *ageRestriction`, 0x78 reserved[104].

`SceNetCheckDialogPS3ConnectParam` (0x70): 0x00 `action` (ENTER 0, LEAVE 1), 0x04 `ssid[33]`, 0x25 `wpaKey[65]`,
0x66 `titleId[10]`.

`SceNetCheckDialogResult` (0x84): 0x00 `result`, 0x04 `SceBool psnModeSucceeded`, 0x08 reserved[124].

`SceNetCheckDialogPS3ConnectInfo` (0x90): 0x00 `SceNetInAddr inaddr`, 0x04 `nickname[128]`, 0x84 `macAddress[6]`,
0x8A reserved[6].

Erros próprios: `PARAM` 0x80100C01, `INVALID_MODE` 0x80100C02, `LACK_OF_LIBHTTP_POOL_SIZE` 0x80100C03 (pool HTTP < 36 KiB
nos modos PSN), `LACK_OF_LIBSSL_POOL_SIZE` 0x80100C04 (pool SSL < 96 KiB), `LATEST_PATCH_PKG_EXIST` 0x80100C05,
`SIGN_OUT` 0x80100C06, `INVALID_PSPADHOC_PARAM` 0x80100C07, `INVALID_TIMEOUT_PARAM` 0x80100C08,
`PSN_AGE_RESTRICTION` 0x80100C09. Os quatro últimos aparecem também como valor de `result.result` (a confirmar).

### sceNetCheckDialogGetPS3ConnectInfo
- [ ] **Prioridade:** P0 (Invizimals PCSA00552, Sakatsuku PCSG00198) · **Biblioteca:** SceCommonDialog · **NID:** 0x39467634
- **Protótipo:** `SceInt32 sceNetCheckDialogGetPS3ConnectInfo(SceNetCheckDialogPS3ConnectInfo *info);` (`psp2/netcheck_dialog.h`)
- **Comportamento:** depois de um NetCheckDialog em modo `PS3_CONNECT` concluído com sucesso, devolve o endereço, apelido
  e MAC do PS3 ao qual o Vita se conectou (cross-play/companion). Offline nunca há PS3 conectado.
- **Retorno/erros:** 0 com `info` preenchido; caso contrário erro. Proposta (a confirmar):
  `NULL` 0x80020402 se `info == 0`; `NOT_IN_USE` 0x80020411 se o diálogo ativo não é NetCheck;
  `NOT_FINISHED` 0x80020410 se não terminou; `NOT_AVAILABLE` 0x80020408 se `mode != PS3_CONNECT` ou `result != OK`
  (sempre o caso no VitaSharp).
- **Estruturas:** `SceNetCheckDialogPS3ConnectInfo` (0x90), acima. Não escrever em caso de erro.
- **Implementação no VitaSharp:** junto do NetCheckDialog no estado global (`CommonDialog`). Como o diálogo nunca
  termina com sucesso em `PS3_CONNECT`, a função sempre devolve erro.
- **Validação:** `imports PCSA00552`/`PCSG00198`; com `--trace-hle-only=sceNetCheckDialog` confirmar que o jogo só a
  chama depois de `GetResult` e trata o erro (volta ao menu).

### sceNetCheckDialogInit
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0xA38A4A0D
- **Protótipo:** `SceInt32 sceNetCheckDialogInit(SceNetCheckDialogParam *param);` (`psp2/netcheck_dialog.h`)
- **Comportamento:** inicia a verificação no `mode` pedido. Offline: concluir com `result = USER_CANCELED` (o
  usuário vê "não conectado à PSN" e volta). Modo `ADHOC_CONN`: o Vita3K deixa RUNNING e dispara o adhoc; no VitaSharp
  (sem adhoc) também cancelar.
- **Retorno/erros:** 0; `BUSY`; `NULL`; `SCE_NETCHECK_DIALOG_ERROR_INVALID_MODE` 0x80100C02 se `mode` fora de 1..7.
- **Estruturas:** ver acima.
- **Implementação no VitaSharp:** hoje em `Net` (`Online.cs`): `=> 0`, sem estado. Mover para o estado global; guardar
  `mode` para `GetPS3ConnectInfo`.
- **Validação:** Killzone (importa NetCheck) e Sakatsuku/Invizimals: entrar no modo online e verificar que voltam ao menu.

### sceNetCheckDialogGetStatus
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0x8027292A
- **Protótipo:** `SceCommonDialogStatus sceNetCheckDialogGetStatus(void);` (`psp2/netcheck_dialog.h`)
- **Comportamento:** `type == NetCheck ? status : NONE`.
- **Retorno/erros:** 0/1/2.
- **Estruturas:** —
- **Implementação no VitaSharp:** hoje `=> 2` sempre, mesmo sem `Init` (um jogo que consulta o status antes de abrir o
  diálogo conclui que há um diálogo terminado pendente). Ler do estado global.
- **Validação:** idem Init.

### sceNetCheckDialogGetResult
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0xB05FCE9E
- **Protótipo:** `SceInt32 sceNetCheckDialogGetResult(SceNetCheckDialogResult *result);` (`psp2/netcheck_dialog.h`)
- **Comportamento:** preenche `result` (0x84): `result = USER_CANCELED` (1), `psnModeSucceeded = 0`.
- **Retorno/erros:** 0; `NOT_IN_USE`/`NOT_FINISHED`; `NULL`.
- **Estruturas:** `SceNetCheckDialogResult`.
- **Implementação no VitaSharp:** hoje zera 0x2C bytes (a struct tem 0x84) e grava `0x80020401` (código de erro BUSY)
  em `result.result`; jogos que comparam com `SCE_COMMON_DIALOG_RESULT_*` caem em ramo indefinido. Corrigir para zerar
  0x84 bytes e gravar 1.
- **Validação:** trace de Sakatsuku/Invizimals no menu online.

### sceNetCheckDialogTerm
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0x8BE51C15
- **Protótipo:** `SceInt32 sceNetCheckDialogTerm(void);`
- **Comportamento/erros:** regra comum (`NOT_IN_USE`, `NOT_FINISHED`); libera o slot.
- **Estruturas:** —
- **Implementação no VitaSharp:** hoje `=> 0` sem estado; usar estado global.
- **Validação:** teste unitário Init→GetResult→Term→Init.

### sceNetCheckDialogAbort
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0x2D8EDF09
- **Protótipo:** `SceInt32 sceNetCheckDialogAbort(void);`
- **Comportamento/erros:** regra comum (`result = ABORTED`).
- **Estruturas:** —
- **Implementação no VitaSharp:** hoje `=> 0`; usar estado global.
- **Validação:** teste unitário.

---

## NpFriendListDialog e NpFriendList2Dialog

Lista de amigos da PSN para escolher destinatários (convites, presentes). Sem header no vita-headers e sem
implementação no Vita3K. O console emulado está deslogado: o desfecho padrão é "cancelado".

**Estruturas (a confirmar):** `SceNpFriendListDialogParam` provavelmente segue o padrão (`sdkVersion` 0x00,
`commonParam` 0x04, campos próprios a partir de 0x50: modo, número máximo de seleção, ponteiro para lista de
`SceNpId`…). `SceNpFriendListDialogResult` começa com `SceInt32 result` em 0x00; o resto (número de selecionados e lista
de `SceNpId`) é desconhecido. Como confirmar: no Invizimals, logar `Span(param, 0x100)` no `Init` e, no call site de
`GetResult` (`--trace-hle-only=sceNpFriendList`), medir o frame do chamador com `disasm`. Até lá, `GetResult` escreve
apenas `+0x00 = USER_CANCELED` e, se existir contador de selecionados em `+0x04` (a confirmar), 0.

### sceNpFriendListDialogInit
- [ ] **Prioridade:** P0 (Invizimals PCSA00552) · **Biblioteca:** SceCommonDialog · **NID:** 0x93FCFEC6
- **Protótipo:** `SceInt32 sceNpFriendListDialogInit(const SceNpFriendListDialogParam *param);` (inferido)
- **Comportamento:** abre a lista de amigos. Deslogado: o firmware mostraria "sign in" e o usuário cancela → mínimo:
  FINISHED com `USER_CANCELED`.
- **Retorno/erros:** 0; `BUSY`; `NULL`. (Um erro de NP do tipo "signed out" no próprio `Init` é possível no hardware —
  a confirmar; preferir 0 + cancelado, que é o caminho que todo jogo trata.)
- **Estruturas:** a confirmar (ver acima).
- **Implementação no VitaSharp:** `CommonDialog`, tipo `NpFriendList`.
- **Validação:** Invizimals: criar `tests/scenarios/inv_friends.txt` acionando o menu de amigos/convite; o jogo deve
  voltar ao menu. `imports PCSA00552` sem `sceNpFriendListDialog*`.

### sceNpFriendListDialogGetStatus
- [ ] **Prioridade:** P0 (Invizimals PCSA00552) · **Biblioteca:** SceCommonDialog · **NID:** 0x1FD5D373
- **Protótipo:** `SceCommonDialogStatus sceNpFriendListDialogGetStatus(void);` (inferido)
- **Comportamento/erros:** regra comum.
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`.
- **Validação:** idem Init.

### sceNpFriendListDialogGetResult
- [ ] **Prioridade:** P0 (Invizimals PCSA00552) · **Biblioteca:** SceCommonDialog · **NID:** 0xD29FE607
- **Protótipo:** `SceInt32 sceNpFriendListDialogGetResult(SceNpFriendListDialogResult *result);` (inferido)
- **Comportamento:** escreve `result = USER_CANCELED` (e zero selecionados, quando o offset for conhecido).
- **Retorno/erros:** 0; `NOT_FINISHED`; `NULL`.
- **Estruturas:** a confirmar.
- **Implementação no VitaSharp:** `CommonDialog`.
- **Validação:** idem Init.

### sceNpFriendListDialogTerm
- [ ] **Prioridade:** P0 (Invizimals PCSA00552) · **Biblioteca:** SceCommonDialog · **NID:** 0x4A880C6A
- **Protótipo:** `SceInt32 sceNpFriendListDialogTerm(void);` (inferido)
- **Comportamento/erros:** regra comum.
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`.
- **Validação:** idem Init.

### sceNpFriendListDialogAbort
- [ ] **Prioridade:** P1 · **Biblioteca:** SceCommonDialog · **NID:** 0x58FA2062
- **Protótipo:** `SceInt32 sceNpFriendListDialogAbort(void);` (inferido)
- **Comportamento/erros:** regra comum (`ABORTED`).
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`.
- **Validação:** teste unitário.

### NpFriendList2Dialog (P1)

Versão posterior (SDK 3.x) da mesma lista. Mesmo comportamento mínimo (cancelado), structs a confirmar.

- [ ] `sceNpFriendList2DialogInit` · NID 0xBBCDF66D · P1 — `SceInt32 (const void *param)` (inferido); regra comum de `Init`; FINISHED/`USER_CANCELED`. Implementação em `CommonDialog`, tipo `NpFriendList2`. Validação: teste unitário.
- [ ] `sceNpFriendList2DialogGetStatus` · NID 0xAC735AF9 · P1 — `SceCommonDialogStatus (void)`; regra comum.
- [ ] `sceNpFriendList2DialogGetResult` · NID 0xC0CF1ED4 · P1 — `SceInt32 (void *result)`; escreve só `+0x00 = USER_CANCELED`.
- [ ] `sceNpFriendList2DialogTerm` · NID 0x05B18EA3 · P1 — `SceInt32 (void)`; regra comum.
- [ ] `sceNpFriendList2DialogAbort` · NID 0xFC67B971 · P1 — `SceInt32 (void)`; regra comum (`ABORTED`).

---

## MsgDialog

Implementada em `CommonDialog` (`Online.cs`), termina na hora com `buttonId = 1` (OK/YES).

### Estruturas (`psp2/message_dialog.h`)

`SceMsgDialogParam` (0x88): 0x00 `sdkVersion`, 0x04 `commonParam`,
**0x50 `mode`** (USER_MSG 1, SYSTEM_MSG 2, ERROR_CODE 3, PROGRESS_BAR 4), 0x54 `userMsgParam*`, 0x58 `sysMsgParam*`,
0x5C `errorCodeParam*`, 0x60 `progBarParam*`, 0x64 `flag`, 0x68 reserved[32].
`SceMsgDialogUserMessageParam`: 0x00 `buttonType` (OK 0, YESNO 1, NONE 2, OK_CANCEL 3, CANCEL 4, 3BUTTONS 5),
0x04 `const SceChar8 *msg` (UTF-8), 0x08 `buttonParam*`.
`SceMsgDialogProgressBarParam` (0x50): 0x00 `barType`, 0x04 `sysMsgParam` (0x28), 0x2C `msg*`, 0x30 reserved[8].
`SceMsgDialogResult` (0x2C): 0x00 `mode`, 0x04 `result`, 0x08 `buttonId` (INVALID 0, OK/YES/BUTTON1 1, NO/BUTTON2 2,
RETRY/BUTTON3 3).

### sceMsgDialogInit
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0x755FF270
- **Protótipo:** `int sceMsgDialogInit(const SceMsgDialogParam *param);` (`psp2/message_dialog.h`)
- **Comportamento:** abre a mensagem. Modos USER_MSG/SYSTEM_MSG/ERROR_CODE: mínimo = FINISHED com o botão padrão
  (OK para OK/OK_CANCEL, YES para YESNO, BUTTON1 para 3BUTTONS, `INVALID` + `USER_CANCELED` para CANCEL; para
  `buttonType == NONE` e SYSTEM_MSG WAIT/WAIT_SMALL/WAIT_CANCEL o diálogo **fica RUNNING** até `sceMsgDialogClose`).
  Modo PROGRESS_BAR: fica RUNNING até `Close`.
- **Retorno/erros:** 0; `BUSY` 0x80020401; `NULL` 0x80020402; `INVALID_ARGUMENT` 0x80020403 para `mode` inválido.
- **Estruturas:** acima.
- **Implementação no VitaSharp:** hoje lê `mode` em `param + 0x18` e `userMsgParam` em `+0x1C` (offsets errados: o
  correto é 0x50/0x54), não checa BUSY e sempre termina na hora — inclusive barra de progresso e "aguarde", que o jogo
  espera fechar ele mesmo com `Close` (se o jogo testar `GetStatus == RUNNING` antes de atualizar a barra, entra em
  caminho de erro). Corrigir offsets, usar estado global, guardar `mode` e `buttonId`.
- **Validação:** Sakatsuku `st_apputil.txt` já traça `sceMsgDialog`: as mensagens de save devem aparecer no log com o
  texto correto (UTF-8 japonês) e o fluxo não pode mudar.

### sceMsgDialogGetResult
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0xBB3BFC89
- **Protótipo:** `int sceMsgDialogGetResult(SceMsgDialogResult *result);`
- **Comportamento:** preenche `mode`, `result` e `buttonId` reais.
- **Retorno/erros:** 0; `NOT_FINISHED` 0x80020410; `NULL` 0x80020402 (hoje devolve 0x80020401 = BUSY).
- **Estruturas:** `SceMsgDialogResult` (0x2C).
- **Implementação no VitaSharp:** hoje grava só `buttonId = 1`, com `mode = 0` e sem checar estado. Gravar o `mode` do
  Init e `result` (ABORTED depois de `Abort`).
- **Validação:** idem Init.

### sceMsgDialogAbort
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0x0CC66115
- **Protótipo:** `int sceMsgDialogAbort(void);`
- **Comportamento:** FINISHED com `result = ABORTED` (`buttonId` inalterado/INVALID).
- **Retorno/erros:** 0; `NOT_IN_USE` 0x80020411.
- **Estruturas:** —
- **Implementação no VitaSharp:** hoje só muda status; gravar `result = ABORTED`. `sceMsgDialogClose` (0xC296D396):
  exigir RUNNING (`NOT_RUNNING` 0x80020404), `result = OK`. `sceMsgDialogTerm` (0x81ACF695): exigir FINISHED
  (`NOT_FINISHED`). Ambos já existem; ajustar junto.
- **Validação:** teste unitário.

### sceMsgDialogProgressBarInc
- [ ] **Prioridade:** P1 · **Biblioteca:** SceCommonDialog · **NID:** 0x7BE0E08B
- **Protótipo:** `int sceMsgDialogProgressBarInc(SceMsgDialogProgressBarTarget target, SceUInt32 delta);` (`psp2/message_dialog.h`)
- **Comportamento:** soma `delta` (em %) à barra, saturando em 100.
- **Retorno/erros:** 0; `NOT_RUNNING` 0x80020404 se não há MsgDialog; `NOT_SUPPORTED` 0x80020405 se `mode != PROGRESS_BAR`;
  `INVALID_ARGUMENT` se `target != 0` (a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`, campo `BarPercent` no `MsgData`; repassar ao backend se houver UI.
- **Validação:** teste unitário.

### sceMsgDialogProgressBarSetValue
- [ ] **Prioridade:** P1 · **Biblioteca:** SceCommonDialog · **NID:** 0x9CDA5E0D
- **Protótipo:** `int sceMsgDialogProgressBarSetValue(SceMsgDialogProgressBarTarget target, SceUInt32 rate);`
- **Comportamento:** define a barra em `min(rate, 100)`.
- **Retorno/erros:** como `ProgressBarInc`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`.
- **Validação:** teste unitário.

### sceMsgDialogProgressBarSetMsg
- [ ] **Prioridade:** P1 · **Biblioteca:** SceCommonDialog · **NID:** 0x0150A451
- **Protótipo:** `int sceMsgDialogProgressBarSetMsg(SceMsgDialogProgressBarTarget target, const SceChar8 *barMsg);`
- **Comportamento:** troca o texto da barra (UTF-8).
- **Retorno/erros:** como `ProgressBarInc`; `NULL` se `barMsg == 0` (a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** `CommonDialog`; logar o texto.
- **Validação:** teste unitário.

---

## SaveDataDialog, NpMessageDialog, NpTrophySetupDialog (STUB)

### sceSaveDataDialogInit
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0xBF5248FA
- **Protótipo:** `int sceSaveDataDialogInit(const SceSaveDataDialogParam *param);` (struct no Vita3K `dialog/types.h`)
- **Comportamento:** a lógica atual (RUNNING + substatus FINISHED, modo barra fica RUNNING) está correta e não deve
  mudar. Só os códigos de erro e o estado compartilhado estão errados.
- **Retorno/erros:** `NULL` deve ser 0x80020402 (hoje 0x80020401); `BUSY` deve ser 0x80020401 (hoje 0x80020404, que é
  `NOT_RUNNING`); BUSY também quando outro tipo de diálogo está ativo. `sceSaveDataDialogContinue`/`Finish`/`Abort`
  sem diálogo RUNNING devem devolver `NOT_RUNNING` 0x80020404 (hoje 0x80020405 = `NOT_SUPPORTED`).
  `sceSaveDataDialogGetResult` com `result == 0` deve devolver `NULL` 0x80020402.
- **Estruturas:** `SceSaveDataDialogParam` (offsets já usados no código: `mode` 0x50, `dispType` 0x54, `fixedParam` 0x58,
  `listParam` 0x5C, `userMsgParam` 0x60, `sysMsgParam` 0x64, `errorCodeParam` 0x68, `progressBarParam` 0x6C,
  `slotConfParam` 0x70, `flag` 0x74, `userdata` 0x78).
- **Implementação no VitaSharp:** `CommonDialog` (`Online.cs`); trocar `_save` pelo estado global e as constantes de
  erro. Não alterar o fluxo de slots.
- **Validação:** cenários `st_save*.txt` (Sakatsuku) sem regressão; Killzone `kz_boot`.

### sceNpMessageDialogGetStatus
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0x2A0D060F
- **Protótipo:** `SceCommonDialogStatus sceNpMessageDialogGetStatus(void);` (inferido)
- **Comportamento:** regra comum. Killzone importa toda a família (envio de convite/mensagem PSN, chamada em
  0x815CA036).
- **Retorno/erros:** 0/1/2.
- **Estruturas:** `SceNpMessageDialogParam`/`Result` sem header (a confirmar; o `Result` começa com `SceInt32 result`).
- **Implementação no VitaSharp:** hoje em `Np` (`Online.cs`) `=> 2` sem `Init`; `sceNpMessageDialogInit` (0x4535A358)
  `=> 0` sem estado; `Term`/`Abort` `=> 0`. Migrar a família para o estado global.
- **Validação:** Killzone: menu de convite (`StartInviteDialog`); o jogo não pode achar que a mensagem foi enviada.

### sceNpMessageDialogGetResult
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0x7EC95C61
- **Protótipo:** `SceInt32 sceNpMessageDialogGetResult(void *result);` (inferido)
- **Comportamento:** offline/deslogado: `result = USER_CANCELED` (hoje grava 0 = OK, ou seja "mensagem enviada").
- **Retorno/erros:** 0; `NOT_FINISHED`; `NULL`.
- **Estruturas:** só `+0x00` conhecido.
- **Implementação no VitaSharp:** `Np` → estado global.
- **Validação:** idem acima.

### sceNpTrophySetupDialogGetStatus
- [ ] **Prioridade:** STUB · **Biblioteca:** SceCommonDialog · **NID:** 0xC3A59547
- **Protótipo:** `SceCommonDialogStatus sceNpTrophySetupDialogGetStatus(void);` (inferido)
- **Comportamento:** regra comum; o Vita3K mantém RUNNING por 3 s se `options & 1` (param 0x50 `context`, 0x54
  `options`), depois FINISHED com OK.
- **Retorno/erros:** 0/1/2.
- **Estruturas:** `SceNpTrophySetupDialogParam`: 0x00 `sdkVersion`, 0x04 `commonParam`, 0x50 `context`, 0x54 `options`,
  0x58 reserved[128]. `Result`: 0x00 `result`, 0x04 reserved[128] (0x84 bytes).
- **Implementação no VitaSharp:** hoje em `NpTrophy` (`NpTrophy.cs:342-346`), `=> 2` sem `Init`, sem BUSY. Usar estado
  global; resultado OK (troféus funcionam offline). Atraso de 3 s é opcional.
- **Validação:** Killzone `kz_boot` (chama Init/GetStatus/GetResult/Term em 0x81375AAC–0x81375AF6) sem regressão.

---

## Demais famílias de diálogo (P1)

Todas seguem a [seção comum](#seção-comum-máquina-de-estados-dos-diálogos) com estratégia mínima. Nenhuma tem
implementação no Vita3K. Quando a struct não está no vita-headers, `GetResult` escreve apenas `+0x00` (result) e deixa
o resto intacto. Implementação: `CommonDialog`, um `DialogType` por família. Validação comum: teste unitário
Init → GetStatus(2) → GetResult → Term → Init de outra família sem BUSY; conferir com `imports` quando um jogo importar.

### PhotoImportDialog (`psp2/photo_import_dialog.h`)

Escolher uma foto da galeria. Param (0x80): 0x00 `sdkVersion`, 0x04 `commonParam`, 0x50 `unk` (deve ser 0), 0x54
`unk` (bits 0..2, não zero), 0x58 `unk`, 0x5C `void *` (provável `ScePhotoImportDialogImportInfo *` de saída, 0x530:
`path[0x400]` …), 0x60 reserved. Result (0x28): 0x00 `result`, 0x04 `unk` (provável número de itens), 0x08 reserved.
Desfecho mínimo: `USER_CANCELED`, `+0x04 = 0`. Integração futura: abrir seletor de arquivo do host em `ux0:picture`.

- [ ] `scePhotoImportDialogInit` · NID 0x73EE7C9C · P1 — `int (ScePhotoImportDialogParam *param)`; erros comuns.
- [ ] `scePhotoImportDialogGetStatus` · NID 0x032206D8 · P1 — `int (void)`.
- [ ] `scePhotoImportDialogGetResult` · NID 0xD855414C · P1 — `int (ScePhotoImportDialogResult *result)`; zera 0x28 bytes e grava `result = 1`.
- [ ] `scePhotoImportDialogTerm` · NID 0x7FE5BD77 · P1 — `int (void)`.
- [ ] `scePhotoImportDialogAbort` · NID 0x4B125581 · P1 — `int (void)`.

### PhotoReviewDialog (`psp2/photo_review_dialog.h`)

Mostrar uma foto (`path` em 0x54, `char[0x400]`; 0x50 `unk` = 1; 0x454 `internal_memory`, 0x458 `internal_memory_size`).
Result (0x24): `result`, `unk`. Mínimo: FINISHED com `OK` (só exibe) — ou `USER_CANCELED`, indiferente; usar OK.

- [ ] `scePhotoReviewDialogInit` · NID 0xCD990375 · P1 — `int (ScePhotoReviewDialogParam *param)`; logar `path`.
- [ ] `scePhotoReviewDialogGetStatus` · NID 0xF4F600CA · P1 — `int (void)`.
- [ ] `scePhotoReviewDialogGetResult` · NID 0xFFA35858 · P1 — `int (ScePhotoReviewDialogResult *result)`; zera 0x24 bytes.
- [ ] `scePhotoReviewDialogTerm` · NID 0xC700B2DF · P1 — `int (void)`.
- [ ] `scePhotoReviewDialogAbort` · NID 0x74FF2A8B · P1 — `int (void)`.

### CameraImportDialog / VideoImportDialog

Capturar foto pela câmera / escolher vídeo. Sem header; câmera ausente no VitaSharp (`sceCameraOpen` falha). Mínimo:
`USER_CANCELED`.

- [ ] `sceCameraImportDialogInit` · NID 0xE525BDB0 · P1 — `int (const void *param)` (inferido).
- [ ] `sceCameraImportDialogGetStatus` · NID 0x86AE7314 · P1 — `int (void)`.
- [ ] `sceCameraImportDialogGetResult` · NID 0x7B339AA2 · P1 — `int (void *result)`; só `+0x00`.
- [ ] `sceCameraImportDialogTerm` · NID 0x8ED0C83C · P1 — `int (void)`.
- [ ] `sceCameraImportDialogAbort` · NID 0x8852B9A4 · P1 — `int (void)`.
- [ ] `sceVideoImportDialogInit` · NID 0xF0CC86F9 · P1 — `int (const void *param)` (inferido).
- [ ] `sceVideoImportDialogGetStatus` · NID 0xBE7C198A · P1 — `int (void)`.
- [ ] `sceVideoImportDialogGetResult` · NID 0x7759D5AD · P1 — `int (void *result)`; só `+0x00`.
- [ ] `sceVideoImportDialogTerm` · NID 0x52E1CF8F · P1 — `int (void)`.
- [ ] `sceVideoImportDialogAbort` · NID 0xECA699FD · P1 — `int (void)`.

### CompanionUtilDialog / CrossControllerDialog

Pareamento com app companion (smartphone) e com PS4 (cross-controller, SDK 3.x). Sem header; sem dispositivo:
`USER_CANCELED`.

- [ ] `sceCompanionUtilDialogInit` · NID 0x0F68448E · P1 — `int (const void *param)` (inferido).
- [ ] `sceCompanionUtilDialogGetStatus` · NID 0xB981474F · P1 — `int (void)`.
- [ ] `sceCompanionUtilDialogGetResult` · NID 0x4A101FA7 · P1 — `int (void *result)`; só `+0x00`.
- [ ] `sceCompanionUtilDialogTerm` · NID 0xC3E6B1EC · P1 — `int (void)`.
- [ ] `sceCompanionUtilDialogAbort` · NID 0x686C27EB · P1 — `int (void)`.
- [ ] `sceCrossControllerDialogInit` · NID 0x2339FBD5 · P1 — `int (const void *param)` (inferido).
- [ ] `sceCrossControllerDialogGetStatus` · NID 0x44B9E931 · P1 — `int (void)`.
- [ ] `sceCrossControllerDialogGetResult` · NID 0x8E35EA7B · P1 — `int (void *result)`; só `+0x00`.
- [ ] `sceCrossControllerDialogTerm` · NID 0x6E572EBF · P1 — `int (void)`.
- [ ] `sceCrossControllerDialogAbort` · NID 0xDDC52A46 · P1 — `int (void)`.

### RemoteOSKDialog

Teclado remoto (digitar no PS3/PS4). Sem header. Mínimo: `USER_CANCELED`. Se um jogo usar como entrada de texto
principal, reaproveitar o backend do ImeDialog (struct a confirmar; provavelmente parecida com `SceImeDialogParam`).

- [ ] `sceRemoteOSKDialogInit` · NID 0x24EB7636 · P1 — `int (const void *param)` (inferido).
- [ ] `sceRemoteOSKDialogGetStatus` · NID 0x356F222A · P1 — `int (void)`.
- [ ] `sceRemoteOSKDialogGetResult` · NID 0xF22D7884 · P1 — `int (void *result)`; só `+0x00`.
- [ ] `sceRemoteOSKDialogTerm` · NID 0xA15121F7 · P1 — `int (void)`.
- [ ] `sceRemoteOSKDialogAbort` · NID 0x2314D273 · P1 — `int (void)`.

### StoreCheckoutDialog

Compra de DLC na PS Store dentro do jogo. Sem header. Offline: `USER_CANCELED` (nenhuma compra). Não gerar evento de
compra (`SCE_APPMGR_SYSTEMEVENT_ON_STORE_PURCHASE`).

- [ ] `sceStoreCheckoutDialogInit` · NID 0x52ECD8A5 · P1 — `int (const void *param)` (inferido); logar IDs de produto se o layout for descoberto.
- [ ] `sceStoreCheckoutDialogGetStatus` · NID 0x7004BB2E · P1 — `int (void)`.
- [ ] `sceStoreCheckoutDialogGetResult` · NID 0x07ED1E26 · P1 — `int (void *result)`; só `+0x00`.
- [ ] `sceStoreCheckoutDialogTerm` · NID 0xB787F4B0 · P1 — `int (void)`.
- [ ] `sceStoreCheckoutDialogAbort` · NID 0xD699D9B4 · P1 — `int (void)`.

### NpProfileDialog

Mostra o perfil PSN de um `SceNpId`. Deslogado: `USER_CANCELED`.

- [ ] `sceNpProfileDialogInit` · NID 0x5E0AFDF8 · P1 — `int (const void *param)` (inferido).
- [ ] `sceNpProfileDialogGetStatus` · NID 0xFDFE6042 · P1 — `int (void)`.
- [ ] `sceNpProfileDialogGetResult` · NID 0x749CAFFC · P1 — `int (void *result)`; só `+0x00`.
- [ ] `sceNpProfileDialogTerm` · NID 0x04B63D6F · P1 — `int (void)`.
- [ ] `sceNpProfileDialogAbort` · NID 0x2B02BE3F · P1 — `int (void)`.

### NpSnsFacebookDialog / TwDialog / TwLoginDialog

Login/postagem em Facebook e Twitter (serviços desativados). Sempre `USER_CANCELED`; `GetResultLongToken` sem token.
`sceTwLoginDialogInit` não tem NID conhecido no db (a família importada por um jogo revelaria o NID: procurar
`NID_` faltante em `imports` próximo dos outros `sceTwLoginDialog*`).

- [ ] `sceNpSnsFacebookDialogInit` · NID 0x6821F09B · P1 — `int (const void *param)` (inferido).
- [ ] `sceNpSnsFacebookDialogGetStatus` · NID 0x147650E8 · P1 — `int (void)`.
- [ ] `sceNpSnsFacebookDialogGetResult` · NID 0x36C5E9A5 · P1 — `int (void *result)`; só `+0x00`.
- [ ] `sceNpSnsFacebookDialogGetResultLongToken` · NID 0xA8682304 · P1 — `int (void *result)` (inferido); `NOT_AVAILABLE` 0x80020408 (a confirmar) porque não houve login; não escrever.
- [ ] `sceNpSnsFacebookDialogTerm` · NID 0xDE1F3928 · P1 — `int (void)`.
- [ ] `sceNpSnsFacebookDialogAbort` · NID 0x7E22AD33 · P1 — `int (void)`.
- [ ] `sceTwDialogInit` · NID 0x93E51B04 · P1 — `int (const void *param)` (inferido).
- [ ] `sceTwDialogGetStatus` · NID 0xD5A6B473 · P1 — `int (void)`.
- [ ] `sceTwDialogGetResult` · NID 0x87F3F43E · P1 — `int (void *result)`; só `+0x00`.
- [ ] `sceTwDialogTerm` · NID 0x2702905B · P1 — `int (void)`.
- [ ] `sceTwDialogAbort` · NID 0x9BEBB77B · P1 — `int (void)`.
- [ ] `sceTwLoginDialogGetStatus` · NID 0x8ACC1F0B · P1 — `int (void)`; sem `Init` conhecido, sempre NONE até o NID do Init aparecer.
- [ ] `sceTwLoginDialogGetResult` · NID 0x5F7F4149 · P1 — `int (void *result)`; `NOT_FINISHED` se não iniciado.
- [ ] `sceTwLoginDialogTerm` · NID 0xD6387E24 · P1 — `int (void)`; `NOT_IN_USE` se não iniciado.
- [ ] `sceTwLoginDialogAbort` · NID 0xB5ED4A32 · P1 — `int (void)`; `NOT_IN_USE` se não iniciado.

### PspSaveDataDialog (FORA)

Diálogo de save do emulador de PSP (`ScePspemu`), usado pelo processo do PSP emulado e não por jogos Vita.

- [ ] `scePspSaveDataDialogInit` · NID 0x4458B053 · FORA — uso exclusivo do PspEmu.
- [ ] `scePspSaveDataDialogContinue` · NID 0x4A40C37F · FORA — idem.
- [ ] `scePspSaveDataDialogGetResult` · NID 0x61C45E12 · FORA — idem.
- [ ] `scePspSaveDataDialogTerm` · NID 0xB8E37F7C · FORA — idem.

