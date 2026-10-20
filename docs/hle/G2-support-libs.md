# G2 — Bibliotecas de apoio e de sistema (P2)

Escopo: bibliotecas sem nenhuma função implementada no VitaSharp e que **nenhum jogo instalado importa hoje** (todas
P2). São de três tipos: bibliotecas de código puro que a Sony distribui como módulos de firmware (Ult, Json, Xml,
Sqlite), utilitários de sistema que conversam com o SceShell (LiveArea, notificação, trigger, export, localização,
Near...) e bibliotecas de debug/perf que só fazem algo num devkit.

Fontes usadas: TSV `G.tsv` (lista alvo), vita-headers `e66ebe9` (`include/psp2`, `db/360`), Vita3K `7059dc2`
(`vita3k/modules`, `vita3k/module/src/load_module.cpp`, `vita3k/regmgr`), código atual do VitaSharp. Quando um
protótipo não existe no vita-headers ele está marcado **(inferido)**; valores não verificados estão marcados
**a confirmar**, junto com a forma de confirmar.

## Contagem por biblioteca

| Biblioteca | Funções | NID da biblioteca | Módulo (db/360) | Abordagem recomendada | Seção |
| --- | ---: | --- | --- | --- | --- |
| SceUlt | 62 | 0x220668A5 | SceUlt (`libult.suprx`) | LLE (fallback HLE descrito) | [2](#2-sceult) |
| SceLibJson | 179 | 0x0854B69C | SceLibJson (`libSceJson.suprx`) | LLE | [3.1](#31-scelibjson-179-funções) |
| SceLibXml | 186 | 0xC2B22E99 | SceLibXml (`libSceXml.suprx`) | LLE | [3.2](#32-scelibxml-186-funções) |
| SceSqlite | 171 | 0x67F02E1D | SceSqlite (`libSceSqlite.suprx`) | LLE | [3.3](#33-scesqlite-171-funções) |
| SceRegMgrForGame | 7 | 0x0B351269 | SceRegistryMgr | HLE | [4.1](#41-sceregmgrforgame) |
| SceLiveAreaUtil | 14 | 0x5303CB07 | SceLiveAreaUtil | HLE | [4.2](#42-sceliveareautil) |
| SceNotificationUtil | 2 | 0xE19097C3 | SceNotificationUtil | HLE | [4.3](#43-scenotificationutil) |
| SceIncomingDialog | 6 | 0x5058F4AB | SceIncomingDialog | HLE | [4.4](#44-sceincomingdialog) |
| SceClipboard | 2 | 0x6B346AA8 | SceClipboard | HLE | [4.5](#45-sceclipboard) |
| SceGameUpdate | 4 | 0x2DD9A62D | SceGameUpdate | HLE | [4.6](#46-scegameupdate) |
| SceTriggerUtil | 11 | 0x1B0302AD | SceTriggerUtil | HLE | [4.7](#47-scetriggerutil) |
| ScePhotoExport | 4 | 0x79166BD9 | ScePhotoExport | HLE | [4.8](#48-scephotoexport-scevideoexport-scemusicexport) |
| SceVideoExport | 1 | 0xF0812A7C | SceVideoExport | HLE | [4.8](#48-scephotoexport-scevideoexport-scemusicexport) |
| SceMusicExport | 1 | 0xF2E30EED | SceMusicExport | HLE | [4.8](#48-scephotoexport-scevideoexport-scemusicexport) |
| SceLibLocation | 22 | 0x924D86D7 | SceLibLocation | HLE | [5.1](#51-sceliblocation) |
| SceGps | 11 | 0x0B6F33FE | SceGps | HLE | [5.2](#52-scegps) |
| SceNearUtil | 29 | 0x560D1608 | SceNearUtil | HLE | [5.3](#53-scenearutil-e-sceneardialogutil) |
| SceNearDialogUtil | 33 | 0x65408075 | SceNearDialogUtil | HLE | [5.3](#53-scenearutil-e-sceneardialogutil) |
| ScePerf | 27 | 0x447F047D | ScePerf | HLE no-op | [6.1](#61-sceperf) |
| SceRazorCapture | 10 | 0x7B2A3F18 | SceRazorCapture | HLE no-op | [6.2](#62-scerazorcapture-e-scerazorhud) |
| SceRazorHud | 17 | 0x250371C2 | SceRazorHud | HLE no-op | [6.2](#62-scerazorcapture-e-scerazorhud) |
| SceDeci4pUserp | 10 | 0x797E39C0 | SceDeci4pUserp | HLE no-op | [6.3](#63-scedeci4puserp) |
| SceBacktrace | 1 | 0xB07B6A3F | SceKernelModulemgr | HLE trivial | [6.4](#64-scebacktrace) |
| SceLibSsp | 1 | 0x8FA98EF1 | SceLibKernel | HLE (alias) | [6.5](#65-scelibssp) |
| SceLibRng | 1 | 0xF9AC7CF8 | SceLibKernel | HLE | [6.6](#66-scelibrng) |
| **Total** | **812** | | | | |

## Ordem sugerida de implementação

1. **SceLibSsp, SceLibRng, ScePerf, SceRazorCapture, SceRazorHud** — custo quase zero e são as que middleware
   (Havok, Unity, motores com profiler) importa "sem querer". Uma tarde de trabalho cobre as 56 funções.
2. **Infra de LLE de sysmodule (seção 1)** — destrava Json, Xml, Sqlite e Ult (598 funções) de uma vez, sem
   escrever HLE para elas.
3. **SceRegMgrForGame** — 7 funções, lidas por jogos para idioma/formato de data em vez de SceAppUtil.
4. **SceLiveAreaUtil, SceNotificationUtil, SceIncomingDialog, SceTriggerUtil, SceClipboard, SceGameUpdate** —
   utilitários de shell; comportamento "aceita e não faz nada visível".
5. **ScePhotoExport / SceVideoExport / SceMusicExport** — cópia de arquivo para `photo0:`/`video0:`/`music0:`.
6. **SceLibLocation, SceGps, SceNearUtil, SceNearDialogUtil** — "serviço indisponível" coerente.
7. **SceDeci4pUserp, SceBacktrace** — só se algum jogo de debug/demo importar.
8. **Fallback HLE do SceUlt** — só se aparecer jogo que importe SceUlt sem trazer `libult.suprx` e sem firmware.

---

## 1. Decisão de arquitetura: LLE de módulos de firmware vs. HLE

### Situação atual

- `sceSysmoduleLoadModule` (`src/VitaSharp.Core/Hle/Modules/SystemMisc.cs`) é HLE puro: só guarda o ID em
  `_loadedSysmodules` e retorna 0. `sceSysmoduleIsLoaded` responde por esse conjunto (erro `0x805A1002` se ausente).
- LLE só acontece em dois caminhos:
  - `Emulator.LoadTitle` (`src/VitaSharp.Core/Emulator.cs`, ~linha 157) pré-carrega
    `app0:sce_module/{libc,libfios2,libSceFt2,libpvf}.suprx` conforme os bits do `SceProcessParam` (+0x24).
  - `sceKernelLoadStartModule` / `sceKernelLoadModule` (`SystemMisc.cs`) chamam `Emu.LoadModule(path)` para
    qualquer caminho que o jogo passar.
- `Emulator.LoadModule` resolve o caminho pelo `Vfs`, decripta com `SelfDecryptor.ToElf` (aceita ELF, fake SELF,
  SELF NPDRM e **SELF de sistema** — `SelfKeys.Generated.cs` tem as chaves METADATA `SceType.SELF`/`SelfType.USER`
  de várias revisões), carrega com `ModuleLoader.Load`, registra `ModuleObject` e invalida o cache de código.
- `Linker` (`src/VitaSharp.Core/Loader/ModuleLoader.cs`) já faz **late binding**: imports sem export viram stub SVC
  (`WriteHleStub`); quando um módulo que exporta o NID é carregado depois, `RegisterExports` percorre
  `_funcBindings` e reescreve cada stub com `WriteJumpStub` para o endereço LLE (casando `(libNid, nid)`), e as
  variáveis são re-relocadas. Ou seja: carregar um módulo de firmware **depois** do jogo já funciona sem mudanças
  no Linker.
- O `Vfs` já monta `vs0:` em `<FsRoot>/vs0` (`src/VitaSharp.Core/Io/Vfs.cs`, linha 17). No Desktop,
  `FsRoot = <DataRoot>/fs`, então hoje seria `C:\Users\guisa\VitaSharp\.vitadata\fs\vs0\`.

### Opção (a) — LLE a partir de dump de firmware do usuário

O que muda:

1. **`SystemMisc.sceSysmoduleLoadModule`** passa a consultar uma tabela `id → arquivos` (nomes da firmware, de
   `vita3k/module/src/load_module.cpp`):

   | ID | Constante | Arquivos | LLE? |
   | --- | --- | --- | --- |
   | 0x0007 | `SCE_SYSMODULE_ULT` | `libfiber` (manter HLE do `Fiber.cs`), `libult` | sim (só `libult`) |
   | 0x0017 | `SCE_SYSMODULE_XML` | `libSceXml` | sim |
   | 0x001F | `SCE_SYSMODULE_SQLITE` | `libSceSqlite` | sim |
   | 0x0055 | `SCE_SYSMODULE_JSON` | `libSceJson` | sim |
   | 0x0005 | `SCE_SYSMODULE_PERF` | `libperf` | não (HLE no-op) |
   | 0x0009 / 0x000A | `RAZOR_CAPTURE` / `RAZOR_HUD` | `librazorcapture_es4` / `librazorhud_es4` | não |
   | 0x0014 | `SCE_SYSMODULE_LOCATION` | `liblocation` | não |
   | 0x0016 | `SCE_SYSMODULE_PHOTO_EXPORT` | `libScePhotoExport` | não |
   | 0x0020 | `SCE_SYSMODULE_TRIGGER_UTIL` | `trigger_util` | não |
   | 0x0023 | `SCE_SYSMODULE_LIVEAREA` | `livearea_util` | não |
   | 0x0028 | `SCE_SYSMODULE_CLIPBOARD` | `libclipboard` | não |
   | 0x002B | `SCE_SYSMODULE_NEAR_UTIL` | `libSceNearUtil` (Vita3K grafa `libScenNearUtil`; a confirmar listando `vs0:sys/external`) | não |
   | 0x0032 | `SCE_SYSMODULE_VIDEO_EXPORT` | `libSceVideoExport` | não |
   | 0x0033 | `SCE_SYSMODULE_NOTIFICATION_UTIL` | `notification_util` | não |
   | 0x0035 | `SCE_SYSMODULE_INCOMING_DIALOG` | `incoming_dialog` | não |
   | 0x0049 | `SCE_SYSMODULE_MUSIC_EXPORT` | `libSceMusicExport` | não |
   | 0x004A | `SCE_SYSMODULE_NEAR_DIALOG_UTIL` | `libSceNearDialogUtil` | não |
   | 0x004D | `SCE_SYSMODULE_GAME_UPDATE` | a confirmar (Vita3K não mapeia; listar `vs0:sys/external`) | não |

   Os utilitários marcados "não" falam com o SceShell por IPC (SceIpmi/SceAppMgr); em LLE eles falhariam do mesmo
   jeito, então ficam HLE.

   Algoritmo por arquivo LLE:
   1. Candidatos, em ordem: `app0:sce_module/<nome>.suprx` (jogos com Ult costumam trazer `libult.suprx`; o
      Vita3K tenta app0 primeiro para Ult) e `vs0:sys/external/<nome>.suprx`.
   2. Se já existe em `K.Modules` um `LoadedModule` com o mesmo `Path`, reaproveita (não carregar duas vezes).
   3. Senão `Emu.LoadModule(path)`; se `StartEntry != 0`, `GuestThread.Current!.CallGuest(m.StartEntry, 0, 0)`
      (igual a `sceKernelLoadStartModule`). `module_start` < 0 → retornar o erro e não marcar como carregado.
   4. Guardar `id → List<uid>` para `sceSysmoduleIsLoaded`/`Unload`.
   5. Nenhum candidato existe → manter o comportamento atual (retorna 0, marca como carregado) e logar
      `Log.WarnOnce` com o caminho esperado, para o usuário saber o que copiar. Os imports continuam caindo no stub
      "Unimplemented HLE function".
   - `sceSysmoduleLoadModuleInternalWithArg(id, args, argp, opt)` passa `args/argp` para `module_start`.
   - `sceSysmoduleUnloadModule`: chamar `module_stop` se quiser, mas **não descarregar** (o Linker não sabe
     desfazer bindings; deixar residente é seguro). Só remover de `_loadedSysmodules`.
2. **`ModuleLoader` / `Linker`**: nenhuma mudança obrigatória. Pontos a verificar ao primeiro teste:
   - módulos de firmware importam `SceLibKernel`, `SceSysmem`, `SceThreadmgr`, `SceLibc`/`SceClib`, `SceIofilemgr`
     (Sqlite usa sceIo* como VFS) — tudo resolve contra o HLE; funções ainda não implementadas aparecerão como
     `Unimplemented HLE function` no log;
   - `Linker.BindFunctionImport` casa `(libNid, nid)`; os NIDs de biblioteca dos imports do jogo têm de bater com os
     da tabela acima (a confirmar no primeiro jogo; se não bater, o fallback por NID puro `_funcExports` cobre).
3. **Onde o usuário coloca o vs0**: `<FsRoot>/vs0/sys/external/*.suprx` (Desktop:
   `.vitadata\fs\vs0\sys\external\`). Aceitar o SELF original da firmware (decriptado por `SelfDecryptor` com
   chaves de sistema) ou ELF já decriptado (`VitaSharp.Desktop self2elf in out`). O VitaSharp não instala PUP; o
   usuário copia o `vs0:` do próprio console (ex.: VitaShell) ou reaproveita o `vs0/` instalado por outro emulador.
   Documentar isso no README do projeto quando implementar.
4. **Opção de configuração**: `EmulatorOptions.LleSysmodules` (padrão `{ULT, XML, SQLITE, JSON}`) para poder
   desligar por módulo durante debug.

Prós: comportamento exato (parser, serialização, SQL, escalonador de ulthreads), custo de implementação pequeno
(~100 linhas), reaproveita o late binding que já existe. Contras: depende de o usuário ter firmware; módulos de
firmware podem importar funções do kernel ainda não implementadas.

### Opção (b) — HLE

- **Json/Xml**: 365 métodos C++ com ABI Itanium/ARM exata: objetos alocados pelo jogo na pilha/heap com layout
  fixo (`sce::Json::Value` tem tipo + união + ponteiro para Array/Object), iteradores passados por valor
  (`Array::iterator::operator!=(iterator)`), `MemAllocator` com vtable que o jogo **herda** (a lib chama
  `allocate/deallocate/notifyError` virtuais no código do jogo), construtores C1/C2 e destrutores D0/D1/D2. Seria
  preciso reproduzir layouts não documentados e chamar código guest a partir do HLE para cada alocação. Muito caro e
  frágil.
- **Sqlite**: reimplementar o motor SQL é inviável; usar um SQLite do host exigiria traduzir `sqlite3*`,
  `sqlite3_stmt*`, callbacks (`exec`, `create_function`, `busy_handler`, collation), alocador customizado
  (`sceSqliteConfigMallocMethods`) e VFS sobre `sceIo` — mais trabalho que o LLE e menos fiel.
- **Ult**: ver seção 2.

### Recomendação

**LLE (opção a) para Json, Xml, Sqlite e Ult.** As tabelas das seções 2 e 3 ficam como referência (nome demangled,
NID, assinatura) para depurar o LLE — por exemplo, reconhecer no log qual método C++ chamou um import faltante — e,
no caso do Ult, como especificação de fallback.

**Validação da infra LLE**: como nenhum jogo instalado importa essas bibliotecas, testar com homebrew
(`ux0:app/<id>`) que use `SCE_SYSMODULE_JSON` / `SQLITE` (ex.: amostras do VitaSDK compiladas contra os stubs
`SceLibJson_stub`/`SceSqlite_stub`) e verificar no log `Loaded module libSceJson (vs0:sys/external/...)`, a
reescrita dos stubs e a ausência de `Unimplemented HLE function` para NIDs da biblioteca. Criar cenário
`tests/scenarios/hb_json.txt` quando houver o homebrew.

---

## 2. SceUlt

User-level threads (ulthreads) cooperativas executadas sobre um pool de worker threads do kernel, mais primitivas
de sincronização que bloqueiam só a ulthread (não a worker). O vita-headers **não tem header** para SceUlt
(nenhum protótipo público) e o Vita3K não implementa nada em HLE (todas as 62 funções são `UNIMPLEMENTED()`; ele
depende de LLE, tentando `app0:sce_module/libult.suprx` e depois `vs0:sys/external/libult.suprx`).

### Avaliação HLE vs. LLE

- **LLE (recomendado)**: `libult` é código user-mode sobre SceFiber (troca de contexto) e primitivas do kernel
  (`sceKernelCreateSema`, LwMutex/LwCond, threads). O VitaSharp já tem HLE de SceFiber (`Hle/Modules/Fiber.cs`) e
  de todas as primitivas do kernel (`Kernel/Sync.cs`), então o `libult.suprx` LLE roda em cima delas sem código
  novo. Muitos jogos que usam Ult trazem o `libult.suprx` em `app0:sce_module/` — nesse caso nem é preciso firmware.
  A confirmar ao primeiro jogo: lista de imports do `libult.suprx` (todas as funções de SceFiber usadas precisam
  existir em `Fiber.cs`; hoje existem Initialize/Finalize/Run/Switch/ReturnToThread/GetSelf/GetInfo/
  OptParamInitialize/RenameSelf/AttachContext*).
- **HLE (fallback)**: viável mapeando cada objeto Ult para um objeto do kernel do VitaSharp e cada **ulthread para
  uma `GuestThread`** (o VitaSharp não precisa multiplexar ulthreads sobre `numWorkerThread` workers; basta que a
  semântica observável — bloqueio, join, yield — seja preservada). Problemas: layouts e tamanhos das structs
  (`SceUltMutex`, `SceUltQueue`, ...) e dos parâmetros opcionais são desconhecidos; o último argumento "build
  version" das funções `_sceUlt*` não é confirmado; códigos de erro não documentados. Só vale fazer se aparecer um
  jogo que importe SceUlt sem trazer `libult.suprx` e o usuário não tiver firmware.

Por isso a especificação abaixo é compacta (tabela), com protótipos **inferidos** a partir da libult do PS4 (mesma
API, mesma Sony; nomes idênticos) — confirmar por desmontagem do `libult.suprx` (`VitaSharp.Desktop self2elf` + Ghidra)
antes de implementar.

### Seção comum do fallback HLE

- [ ] **Prioridade:** P2 · **Biblioteca:** SceUlt (NID lib 0x220668A5)
- **Protótipos:** **(inferidos)**, ver tabela. Todos retornam `int32_t` (0 = sucesso, < 0 = erro). Objetos
  (`SceUltMutex*`, `SceUltQueue*` etc.) são áreas de memória do jogo, opacas; as funções `_sceUlt*Create` e
  `_sceUlt*OptParamInitialize` provavelmente recebem um argumento extra final com a versão do SDK (a confirmar).
- **Comportamento:** ver tabela; recursos ("waiting queue resource pool", "queue data resource pool") são pools de
  memória que o jogo fornece em `workArea`; os `GetWorkAreaSize` dizem quanto alocar.
- **Retorno/erros (a confirmar)**: na libult do PS4 os erros são `0x80810001` NULL, `0x80810002` ALIGNMENT,
  `0x80810003` RANGE, `0x80810004` INVALID, `0x80810005` PERMISSION, `0x80810006` STATE, `0x80810007` BUSY,
  `0x80810008` AGAIN, `0x80810009` FATAL. No Vita o prefixo pode ser outro: confirmar pelas constantes no
  `libult.suprx` (procurar imediatos `0x80xx00xx` nos caminhos de erro de `sceUltMutexLock`).
- **Estruturas:** tamanhos a confirmar (no PS4 quase todos os objetos têm 256 bytes e os OptParam 128 bytes; no
  Vita, 32-bit, provavelmente menores). No HLE: escrever nos 4 primeiros bytes do objeto um magic (`'ULT?'`) e nos
  4 seguintes um UID de `K.Objects`; validar o magic em toda chamada (objeto inválido → erro INVALID).
- **Implementação no VitaSharp:** novo `Hle/Modules/Ult.cs` (`sealed class Ult : HleModule`).
  - Ulthread → `K.CreateThread(name, entry, prioridade da thread chamadora, sizeContext, ...)` usando o
    `context`/`sizeContext` do jogo como pilha se o loader permitir; `Join` → espera do fim da thread (mesmo
    mecanismo de `sceKernelWaitThreadEnd`); `Exit` → `GuestThreadExitException(status)`; `Yield` →
    `sceKernelDelayThread(0)`-equivalente.
  - Mutex → `Kernel.Mutex` (não recursivo); CondVar → `Kernel.CondVar`; Semaphore → `Kernel.Semaphore`
    (`Acquire(n)`/`Release(n)`); RwLock → `Kernel.RwLock`; Queue → `Kernel.MsgPipe` com tamanho
    `dataSize * numData` ou fila própria com dois semáforos (slots livres/ocupados) e cópia de `dataSize` bytes.
  - Pools: validar parâmetros e devolver 0; o VitaSharp não precisa da memória.
  - Nomes que começam com `sceUlt` precisam entrar na faixa de custo de espera em `HleFunction.ClassifyCost`
    (`Lock`, `Acquire`, `Wait`, `Pop`, `Push`, `Join`).
- **Validação:** jogo/homebrew com Ult; comparar com execução LLE do `libult.suprx` (mesma sequência de retornos).

| NID | Função | Protótipo **(inferido)** | Comportamento (fallback HLE) |
| --- | --- | --- | --- |
| 0xEF094E35 | `_sceUltWaitingQueueResourcePoolOptParamInitialize` | `int (SceUltWaitingQueueResourcePoolOptParam *opt)` | zera o OptParam; NULL → erro NULL |
| 0x644DA029 | `sceUltWaitingQueueResourcePoolGetWorkAreaSize` | `size_t (uint32_t numThreads, uint32_t numSyncObjects)` | tamanho de work area; retornar valor > 0 coerente (ex.: `numThreads*32 + numSyncObjects*32`, a confirmar) |
| 0x62F9493E | `_sceUltWaitingQueueResourcePoolCreate` | `int (SceUltWaitingQueueResourcePool *pool, const char *name, uint32_t numThreads, uint32_t numSyncObjects, void *workArea, const SceUltWaitingQueueResourcePoolOptParam *opt)` | valida, grava magic/UID; capacidades usadas só para checagem |
| 0xC9E96714 | `sceUltWaitingQueueResourcePoolDestroy` | `int (SceUltWaitingQueueResourcePool *pool)` | invalida; erro BUSY se ainda houver objetos ligados |
| 0x04DC59F2 | `sceUltGetWaitingQueueResourcePoolInfo` | `int (SceUltWaitingQueueResourcePool *pool, SceUltWaitingQueueResourcePoolInfo *info)` | preenche nome/capacidades (layout a confirmar) |
| 0x8A4F88A2 | `_sceUltQueueDataResourcePoolOptParamInitialize` | `int (SceUltQueueDataResourcePoolOptParam *opt)` | zera o OptParam |
| 0xECDA7FEE | `sceUltQueueDataResourcePoolGetWorkAreaSize` | `size_t (uint32_t numData, size_t dataSize, uint32_t numQueueObjects)` | tamanho ≥ `numData*dataSize` |
| 0x40856827 | `_sceUltQueueDataResourcePoolCreate` | `int (SceUltQueueDataResourcePool *pool, const char *name, uint32_t numData, size_t dataSize, uint32_t numQueueObjects, SceUltWaitingQueueResourcePool *waitingQueuePool, void *workArea, const SceUltQueueDataResourcePoolOptParam *opt)` | valida, grava magic/UID |
| 0x2B8D33F1 | `sceUltQueueDataResourcePoolDestroy` | `int (SceUltQueueDataResourcePool *pool)` | invalida |
| 0x750E7962 | `sceUltGetQueueDataResourcePoolInfo` | `int (SceUltQueueDataResourcePool *pool, SceUltQueueDataResourcePoolInfo *info)` | preenche info |
| 0xF7A83023 | `_sceUltQueueOptParamInitialize` | `int (SceUltQueueOptParam *opt)` | zera o OptParam |
| 0x14DA1BB4 | `_sceUltQueueCreate` | `int (SceUltQueue *queue, const char *name, size_t dataSize, SceUltWaitingQueueResourcePool *wqPool, SceUltQueueDataResourcePool *dataPool, const SceUltQueueOptParam *opt)` | cria fila FIFO de elementos de `dataSize` bytes, capacidade = `numData` do pool |
| 0xF37862DE | `sceUltQueueDestroy` | `int (SceUltQueue *queue)` | destrói; acorda esperas com erro |
| 0xA7E78FF9 | `sceUltQueuePush` | `int (SceUltQueue *queue, const void *data)` | copia `dataSize` bytes; bloqueia se cheia |
| 0x6D356B29 | `sceUltQueueTryPush` | `int (SceUltQueue *queue, const void *data)` | idem sem bloquear; cheia → erro AGAIN/BUSY |
| 0x1AD58A53 | `sceUltQueuePop` | `int (SceUltQueue *queue, void *data)` | retira o mais antigo; bloqueia se vazia |
| 0x2A1A8EA6 | `sceUltQueueTryPop` | `int (SceUltQueue *queue, void *data)` | idem sem bloquear; vazia → erro AGAIN/BUSY |
| 0x1470318D | `sceUltGetQueueInfo` | `int (SceUltQueue *queue, SceUltQueueInfo *info)` | preenche nome, dataSize, nº de elementos |
| 0x24D87E05 | `_sceUltMutexOptParamInitialize` | `int (SceUltMutexOptParam *opt)` | zera o OptParam |
| 0x5AFEC7A1 | `_sceUltMutexCreate` | `int (SceUltMutex *mutex, const char *name, SceUltWaitingQueueResourcePool *wqPool, const SceUltMutexOptParam *opt)` | cria mutex não recursivo (atributo recursivo no opt, a confirmar) |
| 0xEEBD9052 | `sceUltMutexDestroy` | `int (SceUltMutex *mutex)` | destrói |
| 0x001EAC8A | `sceUltMutexLock` | `int (SceUltMutex *mutex)` | trava; bloqueia a ulthread |
| 0xE5936A69 | `sceUltMutexTryLock` | `int (SceUltMutex *mutex)` | trava sem bloquear; ocupado → erro BUSY |
| 0x897C9097 | `sceUltMutexUnlock` | `int (SceUltMutex *mutex)` | destrava; não dono → erro PERMISSION |
| 0x58D8DDE6 | `sceUltGetMutexInfo` | `int (SceUltMutex *mutex, SceUltMutexInfo *info)` | preenche info |
| 0x0603FCC1 | `_sceUltConditionVariableOptParamInitialize` | `int (SceUltConditionVariableOptParam *opt)` | zera o OptParam |
| 0xD76A156C | `_sceUltConditionVariableCreate` | `int (SceUltConditionVariable *cv, const char *name, const SceUltConditionVariableOptParam *opt)` | cria condvar |
| 0x53420ED2 | `sceUltConditionVariableDestroy` | `int (SceUltConditionVariable *cv)` | destrói |
| 0x2CD0F57C | `sceUltConditionVariableWait` | `int (SceUltConditionVariable *cv, SceUltMutex *mutex)` | libera o mutex, espera sinal, retrava |
| 0x9FE7CB9F | `sceUltConditionVariableSignal` | `int (SceUltConditionVariable *cv)` | acorda um |
| 0xEBB6FC1E | `sceUltConditionVariableSignalAll` | `int (SceUltConditionVariable *cv)` | acorda todos |
| 0xC7AA4779 | `sceUltGetConditionVariableInfo` | `int (SceUltConditionVariable *cv, SceUltConditionVariableInfo *info)` | preenche info |
| 0x8E31B9FE | `_sceUltSemaphoreOptParamInitialize` | `int (SceUltSemaphoreOptParam *opt)` | zera o OptParam |
| 0xDD59562C | `_sceUltSemaphoreCreate` | `int (SceUltSemaphore *sema, const char *name, int32_t numInitialResource, SceUltWaitingQueueResourcePool *wqPool, const SceUltSemaphoreOptParam *opt)` | cria semáforo de contagem |
| 0x8EC57420 | `sceUltSemaphoreDestroy` | `int (SceUltSemaphore *sema)` | destrói |
| 0xF220D3AE | `sceUltSemaphoreAcquire` | `int (SceUltSemaphore *sema, int32_t numResource)` | espera ter `numResource` e subtrai |
| 0xAF15606D | `sceUltSemaphoreTryAcquire` | `int (SceUltSemaphore *sema, int32_t numResource)` | idem sem bloquear; insuficiente → erro BUSY |
| 0x65376E2D | `sceUltSemaphoreRelease` | `int (SceUltSemaphore *sema, int32_t numResource)` | soma e acorda esperas |
| 0x25FE84F3 | `sceUltGetSemaphoreInfo` | `int (SceUltSemaphore *sema, SceUltSemaphoreInfo *info)` | preenche contagem atual |
| 0xD8334A1F | `_sceUltReaderWriterLockOptParamInitialize` | `int (SceUltReaderWriterLockOptParam *opt)` | zera o OptParam |
| 0x2FB0EB32 | `_sceUltReaderWriterLockCreate` | `int (SceUltReaderWriterLock *rw, const char *name, SceUltWaitingQueueResourcePool *wqPool, const SceUltReaderWriterLockOptParam *opt)` | cria rwlock |
| 0xB1FEB79B | `sceUltReaderWriterLockDestroy` | `int (SceUltReaderWriterLock *rw)` | destrói |
| 0x9AD07630 | `sceUltReaderWriterLockLockRead` | `int (SceUltReaderWriterLock *rw)` | trava leitura (compartilhada) |
| 0xF5F63E2C | `sceUltReaderWriterLockLockWrite` | `int (SceUltReaderWriterLock *rw)` | trava escrita (exclusiva) |
| 0x2629C055 | `sceUltReaderWriterLockTryLockRead` | `int (SceUltReaderWriterLock *rw)` | sem bloquear; erro BUSY |
| 0x944FB222 | `sceUltReaderWriterLockTryLockWrite` | `int (SceUltReaderWriterLock *rw)` | sem bloquear; erro BUSY |
| 0x218D4743 | `sceUltReaderWriterLockUnlockRead` | `int (SceUltReaderWriterLock *rw)` | libera leitura |
| 0x2A5741F5 | `sceUltReaderWriterLockUnlockWrite` | `int (SceUltReaderWriterLock *rw)` | libera escrita |
| 0x10B3FB76 | `sceUltGetReaderWriterLockInfo` | `int (SceUltReaderWriterLock *rw, SceUltReaderWriterLockInfo *info)` | preenche info |
| 0x8486DDE6 | `_sceUltUlthreadRuntimeOptParamInitialize` | `int (SceUltUlthreadRuntimeOptParam *opt)` | zera o OptParam (contém prioridade/afinidade/stack das workers, a confirmar) |
| 0x5435C586 | `sceUltUlthreadRuntimeGetWorkAreaSize` | `size_t (uint32_t numMaxUlthread, uint32_t numWorkerThread)` | tamanho de work area |
| 0x86DDA3AE | `_sceUltUlthreadRuntimeCreate` | `int (SceUltUlthreadRuntime *rt, const char *name, uint32_t numMaxUlthread, uint32_t numWorkerThread, void *workArea, const SceUltUlthreadRuntimeOptParam *opt)` | cria runtime; HLE só guarda limites |
| 0x4E9A745C | `sceUltUlthreadRuntimeDestroy` | `int (SceUltUlthreadRuntime *rt)` | erro BUSY se houver ulthreads vivas |
| 0x4F6A7DA6 | `sceUltGetUlthreadRuntimeInfo` | `int (SceUltUlthreadRuntime *rt, SceUltUlthreadRuntimeInfo *info)` | preenche info |
| 0x7F373376 | `_sceUltUlthreadOptParamInitialize` | `int (SceUltUlthreadOptParam *opt)` | zera o OptParam |
| 0xB1290375 | `_sceUltUlthreadCreate` | `int (SceUltUlthread *ut, const char *name, SceUltUlthreadEntry entry /* int32_t (*)(uint32_t arg) */, uint32_t arg, void *context, size_t sizeContext, SceUltUlthreadRuntime *rt, const SceUltUlthreadOptParam *opt)` | cria e põe para rodar uma ulthread (HLE: `GuestThread`); `numMaxUlthread` excedido → erro |
| 0xCAD57BAD | `sceUltUlthreadYield` | `int (void)` | cede a vez |
| 0x1E401DF8 | `sceUltUlthreadExit` | `int (int32_t status)` | termina a ulthread atual com `status` (não retorna) |
| 0x63483381 | `sceUltUlthreadJoin` | `int (SceUltUlthread *ut, int32_t *status)` | espera terminar; grava status |
| 0xB4CF88AC | `sceUltUlthreadTryJoin` | `int (SceUltUlthread *ut, int32_t *status)` | sem bloquear; viva → erro BUSY |
| 0xA798C5D7 | `sceUltUlthreadGetSelf` | `int (SceUltUlthread **ut)` | ponteiro do objeto da ulthread atual; fora de ulthread → erro PERMISSION |
| 0xB5895640 | `sceUltGetUlthreadInfo` | `int (SceUltUlthread *ut, SceUltUlthreadInfo *info)` | preenche info |

---

## 3. SceLibJson, SceLibXml, SceSqlite — referência para o LLE

Recomendação: **LLE** (seção 1). As tabelas listam todas as funções do alvo para depuração. Nomes C++ foram
demangled com um demangler Itanium próprio (verificar casos duvidosos com `c++filt`). Convenção de chamada
ARM: `this` em r0; construtores retornam `this`; `[C1 complete]`/`[C2 base]`/`[D0 deleting]`/`[D1 complete]`/
`[D2 base]` indicam a variante Itanium (na prática C1≡C2 e D1≡D2 no binário). `NodeId(u64)` é o identificador
de nó de 64 bits do DOM do SceLibXml (`unsigned long long`, passa em par de registradores alinhado).

Itens com interesse especial se algum dia for preciso HLE parcial ou depurar:

- `sce::Json::Initializer::initialize(const InitParameter*)` recebe o alocador (`MemAllocator*` com vtable do
  jogo), userData e tamanho de buffer de leitura (layout de `InitParameter` a confirmar desmontando o jogo).
- `sce::Json::Parser::parse(Value&, int (*)(char&, void*), void*)` chama de volta código do jogo por caractere;
  `Value::serialize(String&, int (*)(String&, void*), void*)` idem por bloco.
- `sce::Xml::Dom::Document` endereça nós por `NodeId` (u64), não por ponteiro.
- `sceSqliteConfigMallocMethods(SceSqliteMallocMethods*)` (`psp2/sqlite.h`; struct `{xMalloc, xRealloc, xFree}`,
  0xC bytes) é o único símbolo não padrão do SQLite; o resto é a API C do SQLite 3.6.x. A ausência das funções
  `sqlite3_wal_*`, `sqlite3_create_function_v2` e a presença de `sqlite3_sourceid`/`backup_*`/`stmt_status`
  indicam versão entre 3.6.18 e 3.6.23 (a confirmar chamando `sqlite3_libversion` no LLE).

### 3.1 SceLibJson (179 funções)

| NID | Assinatura demangled (namespace `sce::Json` omitido) | Comportamento |
| --- | --- | --- |
| 0xA3EA44E9 | `Initializer::initialize(const InitParameter*)` | inicializa o objeto (aloca estruturas internas pelo Initializer/alocador) |
| 0xABA14718 | `Initializer::setAllocatorInfoCallBack(void (*)(int, ValueType, void*), void*)` | registra callback chamado a cada alocação/liberação (tipo de valor, ponteiro) |
| 0x30AD8C6A | `Initializer::terminate()` | libera os recursos internos; objeto volta ao estado não inicializado |
| 0x5405EE04 | `Initializer::Initializer()` [C1 complete] | construtor padrão |
| 0x7ECFC5DE | `Initializer::Initializer()` [C2 base] | construtor padrão |
| 0xA19AA8B4 | `Initializer::~Initializer()` [D1 complete] | destrutor |
| 0xCBA596D3 | `Initializer::~Initializer()` [D2 base] | destrutor |
| 0x38C51D0C | `MemAllocator::notifyError(int, unsigned int, void*)` | virtual: chamado pela lib ao falhar alocação (código, tamanho, userData); padrão não faz nada |
| 0x2F9610D7 | `MemAllocator::MemAllocator()` [C1 complete] | construtor padrão |
| 0x5EC4F6CB | `MemAllocator::MemAllocator()` [C2 base] | construtor padrão |
| 0x71AA3947 | `MemAllocator::~MemAllocator()` [D0 deleting] | destrutor |
| 0x1666CAFA | `MemAllocator::~MemAllocator()` [D1 complete] | destrutor |
| 0x47A56DF5 | `MemAllocator::~MemAllocator()` [D2 base] | destrutor |
| 0x102FE221 | `Array::clear()` | esvazia o conteúdo mantendo o objeto utilizável |
| 0xF9BE5DF7 | `Array::erase(const Array::iterator&)` | remove o elemento indicado |
| 0xCA3426FF | `Array::insert(const Array::iterator&, const Value&)` | insere elemento (antes do iterador / par chave-valor) |
| 0x6E9F5468 | `Array::iterator::advance(unsigned int)` | avança o iterador N posições |
| 0x20E1D03F | `Array::iterator::operator=(const Array::iterator&)` | atribuição (cópia profunda) |
| 0xF471D774 | `Array::iterator::iterator(const Array::iterator&)` [C1 complete] | construtor de cópia |
| 0x21974253 | `Array::iterator::iterator()` [C1 complete] | construtor padrão |
| 0x8F200495 | `Array::iterator::iterator(const Array::iterator&)` [C2 base] | construtor de cópia |
| 0x2BA3DFE6 | `Array::iterator::iterator()` [C2 base] | construtor padrão |
| 0x866C0131 | `Array::iterator::~iterator()` [D1 complete] | destrutor |
| 0x25A5F935 | `Array::iterator::~iterator()` [D2 base] | destrutor |
| 0x883F0120 | `Array::iterator::operator++(int)` | incremento do iterador |
| 0x24413A1E | `Array::iterator::operator++()` | incremento do iterador |
| 0x99422AB5 | `Array::pop_back()` | remove o último elemento |
| 0x21AE1E00 | `Array::push_back(const Value&)` | acrescenta valor ao fim do array |
| 0xBFE9F993 | `Array::operator=(const Array&)` | atribuição (cópia profunda) |
| 0xA1131B26 | `Array::Array(const Array&)` [C1 complete] | construtor de cópia |
| 0x8090B2D1 | `Array::Array()` [C1 complete] | construtor padrão |
| 0x4CAF859A | `Array::Array(const Array&)` [C2 base] | construtor de cópia |
| 0x871FE141 | `Array::Array()` [C2 base] | construtor padrão |
| 0xAA0C1A25 | `Array::~Array()` [D1 complete] | destrutor |
| 0xF4C27A09 | `Array::~Array()` [D2 base] | destrutor |
| 0xCC68D950 | `Value::referArray()` | retorna ponteiro mutável para o Array interno (converte o tipo se necessário) |
| 0x8A817B50 | `Value::referValue(unsigned int)` | retorna ponteiro mutável para o elemento (índice ou chave) |
| 0x97ABD9CF | `Value::referValue(const String&)` | retorna ponteiro mutável para o elemento (índice ou chave) |
| 0xB1556E63 | `Value::referObject()` | retorna ponteiro mutável para o Object interno |
| 0xE2B03959 | `Value::referString()` | retorna ponteiro mutável para a String interna |
| 0xF7F9550C | `Value::referBoolean()` | retorna ponteiro mutável para o bool interno |
| 0x9F0BD96B | `Value::referInteger()` | retorna ponteiro mutável para o int64 interno |
| 0xD1C592D6 | `Value::referUInteger()` | retorna ponteiro mutável para o uint64 interno |
| 0x349F2D89 | `Value::setNullAccessCallback(const Value& (*)(ValueType, const Value*, void*), void*)` | registra callback devolvendo o Value usado quando se acessa chave/índice inexistente |
| 0xAA76F905 | `Value::set(bool)` | substitui o conteúdo do Value pelo argumento (muda o tipo) |
| 0x870C0A3D | `Value::set(double)` | substitui o conteúdo do Value pelo argumento (muda o tipo) |
| 0x925E89F8 | `Value::set(ValueType)` | substitui o conteúdo do Value pelo argumento (muda o tipo) |
| 0x49389A02 | `Value::set(const Array&)` | substitui o conteúdo do Value pelo argumento (muda o tipo) |
| 0x8725FD85 | `Value::set(const Object&)` | substitui o conteúdo do Value pelo argumento (muda o tipo) |
| 0x2923CA00 | `Value::set(const String&)` | substitui o conteúdo do Value pelo argumento (muda o tipo) |
| 0xA25972A1 | `Value::set(const Value&)` | substitui o conteúdo do Value pelo argumento (muda o tipo) |
| 0x5423BA15 | `Value::set(long long)` | substitui o conteúdo do Value pelo argumento (muda o tipo) |
| 0xCB972985 | `Value::set(NodeId(u64))` | substitui o conteúdo do Value pelo argumento (muda o tipo) |
| 0xAD14CD26 | `Value::swap(Value&)` | troca o conteúdo de dois Values |
| 0xC9813AC7 | `Value::clear()` | esvazia o conteúdo mantendo o objeto utilizável |
| 0xB33FF9D6 | `Value::referReal()` | retorna ponteiro mutável para o double interno |
| 0xCFC4D75F | `Value::serialize(String&)` | serializa para texto (JSON/XML) na String de saída; variante com callback entrega em blocos |
| 0xBB3A0C22 | `Value::serialize(String&, int (*)(String&, void*), void*)` | serializa para texto (JSON/XML) na String de saída; variante com callback entrega em blocos |
| 0x705ED2BB | `Value::operator=(const Value&)` | atribuição (cópia profunda) |
| 0x7A6FE666 | `Value::Value(bool)` [C1 complete] | construtor a partir do argumento |
| 0xAD01DFC1 | `Value::Value(double)` [C1 complete] | construtor a partir do argumento |
| 0xB7D13E29 | `Value::Value(ValueType)` [C1 complete] | construtor a partir do argumento |
| 0x0E917571 | `Value::Value(const Array&)` [C1 complete] | construtor a partir do argumento |
| 0xE1DFAB01 | `Value::Value(const Object&)` [C1 complete] | construtor a partir do argumento |
| 0xB7582884 | `Value::Value(const String&)` [C1 complete] | construtor a partir do argumento |
| 0xCC201CD2 | `Value::Value(const Value&)` [C1 complete] | construtor de cópia |
| 0x7A9DECC0 | `Value::Value()` [C1 complete] | construtor padrão |
| 0x1B23D989 | `Value::Value(long long)` [C1 complete] | construtor a partir do argumento |
| 0xF785608C | `Value::Value(NodeId(u64))` [C1 complete] | construtor a partir do argumento |
| 0xB9AA1D08 | `Value::Value(bool)` [C2 base] | construtor a partir do argumento |
| 0x33A75C2B | `Value::Value(double)` [C2 base] | construtor a partir do argumento |
| 0x471A687D | `Value::Value(ValueType)` [C2 base] | construtor a partir do argumento |
| 0xBCBE2CD9 | `Value::Value(const Array&)` [C2 base] | construtor a partir do argumento |
| 0x13FD685F | `Value::Value(const Object&)` [C2 base] | construtor a partir do argumento |
| 0x58C4E096 | `Value::Value(const String&)` [C2 base] | construtor a partir do argumento |
| 0x2C83C840 | `Value::Value(const Value&)` [C2 base] | construtor de cópia |
| 0x87A356B9 | `Value::Value()` [C2 base] | construtor padrão |
| 0x6304C0BE | `Value::Value(long long)` [C2 base] | construtor a partir do argumento |
| 0x22B3F3F1 | `Value::Value(NodeId(u64))` [C2 base] | construtor a partir do argumento |
| 0xE07648EC | `Value::~Value()` [D1 complete] | destrutor |
| 0xAE2322F7 | `Value::~Value()` [D2 base] | destrutor |
| 0x5C6A0FAA | `Object::Pair::Pair(const String&, const Value&)` [C1 complete] | construtor a partir do argumento |
| 0xA912F22B | `Object::Pair::Pair()` [C1 complete] | construtor padrão |
| 0x5001AC95 | `Object::Pair::Pair(const String&, const Value&)` [C2 base] | construtor a partir do argumento |
| 0x51BE3283 | `Object::Pair::Pair()` [C2 base] | construtor padrão |
| 0xB91E619A | `Object::Pair::~Pair()` [D1 complete] | destrutor |
| 0x50EF2E1C | `Object::Pair::~Pair()` [D2 base] | destrutor |
| 0xE0C9D64E | `Object::clear()` | esvazia o conteúdo mantendo o objeto utilizável |
| 0xBDFAC9E3 | `Object::erase(const String&)` | remove o elemento indicado |
| 0x5EBE53AB | `Object::insert(const Object::Pair&)` | insere elemento (antes do iterador / par chave-valor) |
| 0xF4E33C1B | `Object::iterator::advance(unsigned int)` | avança o iterador N posições |
| 0x52D081C4 | `Object::iterator::operator=(const Object::iterator&)` | atribuição (cópia profunda) |
| 0xE9B2D4AB | `Object::iterator::iterator(const Object::iterator&)` [C1 complete] | construtor de cópia |
| 0x2E89608C | `Object::iterator::iterator()` [C1 complete] | construtor padrão |
| 0x0EC42227 | `Object::iterator::iterator(const Object::iterator&)` [C2 base] | construtor de cópia |
| 0x02A3CFE2 | `Object::iterator::iterator()` [C2 base] | construtor padrão |
| 0x2A47D045 | `Object::iterator::~iterator()` [D1 complete] | destrutor |
| 0x6708662C | `Object::iterator::~iterator()` [D2 base] | destrutor |
| 0xD48C8891 | `Object::iterator::operator++(int)` | incremento do iterador |
| 0xAEC3AD59 | `Object::iterator::operator++()` | incremento do iterador |
| 0x63E9610C | `Object::operator=(const Object&)` | atribuição (cópia profunda) |
| 0xD7070313 | `Object::Object(const Object&)` [C1 complete] | construtor de cópia |
| 0xB669382C | `Object::Object()` [C1 complete] | construtor padrão |
| 0x3AD421A0 | `Object::Object(const Object&)` [C2 base] | construtor de cópia |
| 0x5F9DB320 | `Object::Object()` [C2 base] | construtor padrão |
| 0x3CF6AF04 | `Object::~Object()` [D1 complete] | destrutor |
| 0xDB941A5D | `Object::~Object()` [D2 base] | destrutor |
| 0x8F2E1720 | `Object::operator[](const String&)` | acesso por índice/chave |
| 0xDD6F3526 | `Parser::parse(Value&, int (*)(char&, void*), void*)` | faz parse do texto (buffer, buffer+tamanho ou callback de leitura) para a estrutura de saída |
| 0x0E4211FC | `Parser::parse(Value&, const char*)` | faz parse do texto (buffer, buffer+tamanho ou callback de leitura) para a estrutura de saída |
| 0x59B1A5B8 | `Parser::parse(Value&, const char*, unsigned int)` | faz parse do texto (buffer, buffer+tamanho ou callback de leitura) para a estrutura de saída |
| 0x617479FB | `String::clear()` | esvazia o conteúdo mantendo o objeto utilizável |
| 0x4DBF6086 | `String::append(const char*)` | concatena texto à String |
| 0xECE95143 | `String::append(const char*, unsigned int)` | concatena texto à String |
| 0x0700528F | `String::append(const String&)` | concatena texto à String |
| 0x7055787A | `String::resize(unsigned int)` | redimensiona a String |
| 0xC3D22030 | `String::operator=(const String&)` | atribuição (cópia profunda) |
| 0x70703FC7 | `String::String(const char*)` [C1 complete] | construtor a partir do argumento |
| 0x40C6576A | `String::String(const String&)` [C1 complete] | construtor de cópia |
| 0x692125B6 | `String::String()` [C1 complete] | construtor padrão |
| 0x1666DA2C | `String::String(const char*)` [C2 base] | construtor a partir do argumento |
| 0x0E0AA31C | `String::String(const String&)` [C2 base] | construtor de cópia |
| 0xFEAB587D | `String::String()` [C2 base] | construtor padrão |
| 0xB70F12A8 | `String::~String()` [D1 complete] | destrutor |
| 0xB039FD7A | `String::~String()` [D2 base] | destrutor |
| 0x895CAEBF | `String::operator+=(unsigned char)` | concatena |
| 0xED322AC3 | `String::operator+=(const char*)` | concatena |
| 0x0F533297 | `Array::end() const` | iterador de fim |
| 0xAF380743 | `Array::back() const` | último elemento |
| 0x9DC07B95 | `Array::size() const` | número de elementos/caracteres |
| 0x82074D93 | `Array::begin() const` | iterador de início |
| 0x7E275CE0 | `Array::empty() const` | true se vazio |
| 0x81EF83DC | `Array::iterator::operator*() const` | desreferencia o iterador |
| 0x5467A38D | `Array::iterator::operator!=(Array::iterator) const` | desigualdade |
| 0x33BE3C5D | `Array::iterator::operator->() const` | acesso a membro via iterador |
| 0x7DDFCAE2 | `Value::getBoolean() const` | retorna o bool (tipo errado → valor padrão) |
| 0x6861C3CC | `Value::getInteger() const` | retorna o int64 |
| 0x35EAAEDA | `Value::getUInteger() const` | retorna o uint64 |
| 0xF14E07A2 | `Value::count() const` | número de elementos (array/objeto) |
| 0xFC0C699A | `Value::getReal() const` | retorna o double |
| 0x691BE883 | `Value::getRoot() const` | retorna o nó/Value raiz |
| 0xA95EF0EC | `Value::getType() const` | retorna o ValueType (null, boolean, integer, uinteger, real, string, array, object) |
| 0x000926DB | `Value::getArray() const` | retorna referência const ao Array |
| 0xC1FE4E8D | `Value::getValue(unsigned int) const` | retorna o elemento (índice/chave) ou o Value nulo |
| 0xAA94ACDD | `Value::getValue(const String&) const` | retorna o elemento (índice/chave) ou o Value nulo |
| 0x39748AF8 | `Value::toString(String&) const` | converte o valor escalar em texto |
| 0xF793E30D | `Value::getObject() const` | retorna referência const ao Object |
| 0xBC3391BE | `Value::getString() const` | retorna referência const à String |
| 0xA5B36078 | `Value::operator bool() const` | true se o Value não é nulo |
| 0x3CE5DEC7 | `Value::operator[](unsigned int) const` | acesso por índice/chave |
| 0xD5B3E509 | `Value::operator[](const char*) const` | acesso por índice/chave |
| 0x910E18C0 | `Value::operator[](const String&) const` | acesso por índice/chave |
| 0xCB50C919 | `Object::end() const` | iterador de fim |
| 0x396D0910 | `Object::find(const String&) const` | procura chave/substring; retorna iterador ou posição (npos se não achar) |
| 0x5630DAD9 | `Object::size() const` | número de elementos/caracteres |
| 0x192FE2BF | `Object::begin() const` | iterador de início |
| 0x168162C2 | `Object::empty() const` | true se vazio |
| 0x0D832C40 | `Object::iterator::operator*() const` | desreferencia o iterador |
| 0xAD643D14 | `Object::iterator::operator==(Object::iterator) const` | igualdade |
| 0x7B74D109 | `Object::iterator::operator!=(Object::iterator) const` | desigualdade |
| 0x8FA78056 | `Object::iterator::operator->() const` | acesso a membro via iterador |
| 0x5B667A69 | `String::at(unsigned int) const` | caractere na posição |
| 0x28991492 | `String::find(char, unsigned int) const` | procura chave/substring; retorna iterador ou posição (npos se não achar) |
| 0x469FDB89 | `String::find(const char*, unsigned int) const` | procura chave/substring; retorna iterador ou posição (npos se não achar) |
| 0x20C5C51E | `String::find(const char*, unsigned int, unsigned int) const` | procura chave/substring; retorna iterador ou posição (npos se não achar) |
| 0xBC59E809 | `String::find(const String&, unsigned int) const` | procura chave/substring; retorna iterador ou posição (npos se não achar) |
| 0xA15F63C1 | `String::size() const` | número de elementos/caracteres |
| 0x327D74CE | `String::c_str() const` | ponteiro para o texto terminado em NUL |
| 0xAE9ECB11 | `String::empty() const` | true se vazio |
| 0x190B0BDA | `String::rfind(char, unsigned int) const` | procura substring/caractere a partir do fim |
| 0x01440503 | `String::rfind(const char*, unsigned int) const` | procura substring/caractere a partir do fim |
| 0x6A0DB914 | `String::rfind(const char*, unsigned int, unsigned int) const` | procura substring/caractere a partir do fim |
| 0x11F2CB3E | `String::rfind(const String&, unsigned int) const` | procura substring/caractere a partir do fim |
| 0x35F974C1 | `String::length() const` | número de caracteres |
| 0xF2F81B2D | `String::substr(unsigned int, unsigned int) const` | nova String com o trecho [pos, pos+len) |
| 0x267A86A3 | `String::compare(const char*) const` | compara (estilo strcmp) |
| 0x55241DBB | `String::compare(const String&) const` | compara (estilo strcmp) |
| 0x5B665588 | `String::operator==(const char*) const` | igualdade |
| 0xCCCD4BA9 | `String::operator==(const String&) const` | igualdade |

### 3.2 SceLibXml (186 funções)

| NID | Assinatura demangled (namespace `sce::Xml` omitido) | Comportamento |
| --- | --- | --- |
| 0x57400A1A | `SimpleData::SimpleData(const char*, unsigned int)` [C1 complete] | construtor a partir do argumento |
| 0x7E582075 | `SimpleData::SimpleData()` [C1 complete] | construtor padrão |
| 0x4CF0656B | `SimpleData::SimpleData(const char*, unsigned int)` [C2 base] | construtor a partir do argumento |
| 0x95077028 | `SimpleData::SimpleData()` [C2 base] | construtor padrão |
| 0xECFA6A2A | `Initializer::initialize(const InitParameter*)` | inicializa o objeto (aloca estruturas internas pelo Initializer/alocador) |
| 0x29824CD5 | `Initializer::terminate()` | libera os recursos internos; objeto volta ao estado não inicializado |
| 0xBF13FDE6 | `Initializer::Initializer()` [C1 complete] | construtor padrão |
| 0x94AAA71D | `Initializer::Initializer()` [C2 base] | construtor padrão |
| 0xB4547C88 | `Initializer::~Initializer()` [D1 complete] | destrutor |
| 0xAAA08FA8 | `Initializer::~Initializer()` [D2 base] | destrutor |
| 0x8D387E01 | `MemAllocator::MemAllocator()` [C1 complete] | construtor padrão |
| 0xE982E681 | `MemAllocator::MemAllocator()` [C2 base] | construtor padrão |
| 0x90B82579 | `MemAllocator::~MemAllocator()` [D0 deleting] | destrutor |
| 0x56002B9D | `MemAllocator::~MemAllocator()` [D1 complete] | destrutor |
| 0x1BE022EA | `MemAllocator::~MemAllocator()` [D2 base] | destrutor |
| 0x89AA847E | `AttributeList::initialize(const Initializer*)` | inicializa o objeto (aloca estruturas internas pelo Initializer/alocador) |
| 0xD08EE434 | `AttributeList::addAttribute(const String*, const String*)` | acrescenta par nome/valor à lista |
| 0xCCEE4E7C | `AttributeList::clear()` | esvazia o conteúdo mantendo o objeto utilizável |
| 0x11FE5A65 | `AttributeList::terminate()` | libera os recursos internos; objeto volta ao estado não inicializado |
| 0x9CBD82D4 | `AttributeList::AttributeList(const AttributeList&)` [C1 complete] | construtor de cópia |
| 0x542076D8 | `AttributeList::AttributeList()` [C1 complete] | construtor padrão |
| 0x87C89447 | `AttributeList::AttributeList(const AttributeList&)` [C2 base] | construtor de cópia |
| 0x5D49542A | `AttributeList::AttributeList()` [C2 base] | construtor padrão |
| 0x38861841 | `AttributeList::~AttributeList()` [D1 complete] | destrutor |
| 0x1B0B3976 | `AttributeList::~AttributeList()` [D2 base] | destrutor |
| 0x30520B78 | `VarAllocBuffer::copy(const unsigned char*, unsigned int, bool)` | copia bytes para o buffer interno (crescendo se preciso) |
| 0x7D5A0041 | `VarAllocBuffer::clear()` | esvazia o conteúdo mantendo o objeto utilizável |
| 0xD95D3824 | `VarAllocBuffer::copyStr(const char*, unsigned int)` | copia string para o buffer interno |
| 0xFE99676E | `VarAllocBuffer::copyStr(const String&)` | copia string para o buffer interno |
| 0x82747F92 | `VarAllocBuffer::reserve(unsigned int)` | garante capacidade mínima do buffer |
| 0xE93EACFC | `VarAllocBuffer::terminate()` | libera os recursos internos; objeto volta ao estado não inicializado |
| 0x8045D9C2 | `VarAllocBuffer::VarAllocBuffer(const Initializer*)` [C1 complete] | construtor a partir do argumento |
| 0xEF4FA027 | `VarAllocBuffer::VarAllocBuffer(const Initializer*)` [C2 base] | construtor a partir do argumento |
| 0xD61CAAFC | `VarAllocBuffer::~VarAllocBuffer()` [D0 deleting] | destrutor |
| 0xD9217FC8 | `VarAllocBuffer::~VarAllocBuffer()` [D1 complete] | destrutor |
| 0x8A4B9379 | `VarAllocBuffer::~VarAllocBuffer()` [D2 base] | destrutor |
| 0xB7770E5E | `SerializeParameter::SerializeParameter()` [C1 complete] | construtor padrão |
| 0xF65270FC | `SerializeParameter::SerializeParameter()` [C2 base] | construtor padrão |
| 0x2CB61A7C | `bXResultToResultType(int)` | converte código interno do parser em código de erro público |
| 0x59C5E9B2 | `getMemManagerDebugLevel()` | retorna o nível de debug do gerenciador de memória |
| 0xBA8B7374 | `setMemManagerDebugLevel(int)` | define o nível de debug do gerenciador de memória |
| 0xBBACFE87 | `Dom::DocumentBuilder::initialize(const Initializer*)` | inicializa o objeto (aloca estruturas internas pelo Initializer/alocador) |
| 0x1A29526B | `Dom::DocumentBuilder::getDocument()` | retorna o Document resultante do último parse |
| 0xA2431C2B | `Dom::DocumentBuilder::setResolveEntity(bool)` | liga/desliga resolução de entidades (&amp; etc.) |
| 0xB8C4D13C | `Dom::DocumentBuilder::setSkipIgnorableText(bool)` | liga/desliga descarte de texto ignorável |
| 0xF351D753 | `Dom::DocumentBuilder::setSkipIgnorableWhiteSpace(bool)` | liga/desliga descarte de espaços ignoráveis |
| 0x7744DD14 | `Dom::DocumentBuilder::parse(const String*, bool)` | faz parse do texto (buffer, buffer+tamanho ou callback de leitura) para a estrutura de saída |
| 0x42D59053 | `Dom::DocumentBuilder::terminate()` | libera os recursos internos; objeto volta ao estado não inicializado |
| 0x702492EA | `Dom::DocumentBuilder::DocumentBuilder()` [C1 complete] | construtor padrão |
| 0x36F6BDF2 | `Dom::DocumentBuilder::DocumentBuilder()` [C2 base] | construtor padrão |
| 0x19D0E024 | `Dom::DocumentBuilder::~DocumentBuilder()` [D1 complete] | destrutor |
| 0x99C58389 | `Dom::DocumentBuilder::~DocumentBuilder()` [D2 base] | destrutor |
| 0x243E6731 | `Dom::Node::appendChild(NodeId(u64))` | acrescenta filho ao nó |
| 0x3CA958D3 | `Dom::Node::removeChild(NodeId(u64))` | remove filho do nó |
| 0x1B98BDBE | `Dom::Node::insertBefore(NodeId(u64), NodeId(u64))` | insere nó antes do nó de referência |
| 0x088C100E | `Dom::Node::Node(NodeId(u64))` [C1 complete] | construtor a partir do argumento |
| 0x44CAF9E1 | `Dom::Node::Node(NodeId(u64))` [C2 base] | construtor a partir do argumento |
| 0x8F2EB967 | `Dom::Node::~Node()` [D1 complete] | destrutor |
| 0x241EFC0E | `Dom::Node::~Node()` [D2 base] | destrutor |
| 0x6A16C2FF | `Dom::Document::importNode(NodeId(u64), NodeId(u64), const Dom::Document*, NodeId(u64))` | copia nó (e subárvore) de outro Document |
| 0xB4A33B78 | `Dom::Document::initialize(const Initializer*)` | inicializa o objeto (aloca estruturas internas pelo Initializer/alocador) |
| 0x18686B94 | `Dom::Document::insertNode(NodeId(u64), NodeId(u64), NodeId(u64))` | insere nó sob o pai, antes do irmão dado |
| 0x49263CE5 | `Dom::Document::removeChild(NodeId(u64), NodeId(u64))` | remove filho do nó |
| 0xD945184A | `Dom::Document::resetStatus()` | zera o status de erro do Document |
| 0x7B0A8F6C | `Dom::Document::setWritable()` | torna o Document editável |
| 0x0CBC1C3F | `Dom::Document::importParent(const Dom::Document*, NodeId(u64))` | importa a cadeia de pais de um nó de outro Document |
| 0xEA19C7CF | `Dom::Document::setAttribute(NodeId(u64), const String*, const String*)` | cria/atualiza atributo do elemento |
| 0x016A9ADB | `Dom::Document::setAttrValue(NodeId(u64), const String*, const String*)` | altera valor de atributo existente |
| 0x35C50B8B | `Dom::Document::createElement(const String*, const AttributeList*, const String*)` | cria elemento (nome, atributos, texto) e retorna o NodeId |
| 0xBCA5E62A | `Dom::Document::recurseDelete(NodeId(u64))` | apaga o nó e toda a subárvore |
| 0x8D19723F | `Dom::Document::createTextNode(const String*)` | cria nó de texto e retorna o NodeId |
| 0x6220E98B | `Dom::Document::addElementChild(NodeId(u64), const String*, const AttributeList*, const String*)` | cria elemento como filho do nó dado |
| 0xF1DB18B1 | `Dom::Document::removeAttribute(NodeId(u64), const String*)` | remove atributo pelo nome |
| 0x779036AB | `Dom::Document::removeAttributes(NodeId(u64))` | remove todos os atributos do elemento |
| 0x0667B08D | `Dom::Document::setAttributeList(NodeId(u64), const AttributeList*)` | substitui os atributos do elemento |
| 0xDEFEAFD2 | `Dom::Document::setText(NodeId(u64), const String*)` | define o texto do nó |
| 0x87F8B4DA | `Dom::Document::serialize(const SerializeParameter*, String*)` | serializa para texto (JSON/XML) na String de saída; variante com callback entrega em blocos |
| 0x4B7321FB | `Dom::Document::terminate()` | libera os recursos internos; objeto volta ao estado não inicializado |
| 0xD622A7FE | `Dom::Document::operator=(const Dom::Document&)` | atribuição (cópia profunda) |
| 0x1DD41C7A | `Dom::Document::Document(const Dom::Document&)` [C1 complete] | construtor de cópia |
| 0x7B7107AD | `Dom::Document::Document()` [C1 complete] | construtor padrão |
| 0xF399F763 | `Dom::Document::Document(const Dom::Document&)` [C2 base] | construtor de cópia |
| 0xE6BA9C73 | `Dom::Document::Document()` [C2 base] | construtor padrão |
| 0xFB207925 | `Dom::Document::~Document()` [D1 complete] | destrutor |
| 0x11A5F0A3 | `Dom::Document::~Document()` [D2 base] | destrutor |
| 0x860CC706 | `Dom::NodeList::initialize(const Initializer*)` | inicializa o objeto (aloca estruturas internas pelo Initializer/alocador) |
| 0x7A889374 | `Dom::NodeList::insertLast(NodeId(u64))` | acrescenta NodeId ao fim da lista |
| 0xE9995F58 | `Dom::NodeList::removeItem(NodeId(u64))` | remove NodeId da lista |
| 0xFA921C6E | `Dom::NodeList::insertFirst(NodeId(u64))` | insere NodeId no início da lista |
| 0x508E9150 | `Dom::NodeList::clear()` | esvazia o conteúdo mantendo o objeto utilizável |
| 0x32B396AD | `Dom::NodeList::terminate()` | libera os recursos internos; objeto volta ao estado não inicializado |
| 0xB1CA0E34 | `Dom::NodeList::NodeList(const Dom::NodeList&)` [C1 complete] | construtor de cópia |
| 0x0580C02E | `Dom::NodeList::NodeList()` [C1 complete] | construtor padrão |
| 0xB97BF737 | `Dom::NodeList::NodeList(const Dom::NodeList&)` [C2 base] | construtor de cópia |
| 0x684E57B9 | `Dom::NodeList::NodeList()` [C2 base] | construtor padrão |
| 0x92EBC9F8 | `Dom::NodeList::~NodeList()` [D1 complete] | destrutor |
| 0x2DF80037 | `Dom::NodeList::~NodeList()` [D2 base] | destrutor |
| 0x874C8331 | `Sax::Parser::initialize(const Initializer*)` | inicializa o objeto (aloca estruturas internas pelo Initializer/alocador) |
| 0x4DB998E6 | `Sax::Parser::setUserData(void*)` | guarda ponteiro de usuário passado aos callbacks SAX |
| 0xB77BF8A0 | `Sax::Parser::setResolveEntity(bool)` | liga/desliga resolução de entidades (&amp; etc.) |
| 0x1B2442A0 | `Sax::Parser::setDocumentHandler(Sax::DocumentHandler*)` | registra o DocumentHandler (callbacks SAX) |
| 0xCE1DAE23 | `Sax::Parser::setSkipIgnorableWhiteSpace(bool)` | liga/desliga descarte de espaços ignoráveis |
| 0x70D9FC8E | `Sax::Parser::parse(const String*, bool)` | faz parse do texto (buffer, buffer+tamanho ou callback de leitura) para a estrutura de saída |
| 0xA2B40FA7 | `Sax::Parser::reset()` | reinicia o parser para novo documento |
| 0xF2C8950D | `Sax::Parser::terminate()` | libera os recursos internos; objeto volta ao estado não inicializado |
| 0x60BF9988 | `Sax::Parser::Parser()` [C1 complete] | construtor padrão |
| 0x56390CA0 | `Sax::Parser::Parser()` [C2 base] | construtor padrão |
| 0xA11C2AED | `Sax::Parser::~Parser()` [D1 complete] | destrutor |
| 0x02E8F7FA | `Sax::Parser::~Parser()` [D2 base] | destrutor |
| 0xE5314387 | `Attr::initialize(const Initializer*)` | inicializa o objeto (aloca estruturas internas pelo Initializer/alocador) |
| 0x66D1B605 | `Attr::setName(const String*)` | define o nome |
| 0x7DD3059D | `Attr::setValue(const String*)` | define o valor |
| 0x67E0DF2B | `Attr::terminate()` | libera os recursos internos; objeto volta ao estado não inicializado |
| 0xA5B902D4 | `Attr::operator=(const Attr&)` | atribuição (cópia profunda) |
| 0xC09ABF87 | `Attr::Attr(const Attr&)` [C1 complete] | construtor de cópia |
| 0xD016F1BC | `Attr::Attr()` [C1 complete] | construtor padrão |
| 0xB4851BEC | `Attr::Attr(const Attr&)` [C2 base] | construtor de cópia |
| 0x0B3AE81B | `Attr::Attr()` [C2 base] | construtor padrão |
| 0x58E349A5 | `Attr::~Attr()` [D1 complete] | destrutor |
| 0xB9E6F81A | `Attr::~Attr()` [D2 base] | destrutor |
| 0xA7E983E2 | `Util::strResult(int)` | texto descritivo de um código de erro |
| 0x18758863 | `String::operator=(const String&)` | atribuição (cópia profunda) |
| 0x035F013B | `String::String(const char*)` [C1 complete] | construtor a partir do argumento |
| 0x0B5461E0 | `String::String(const char*, unsigned int)` [C1 complete] | construtor a partir do argumento |
| 0x67191CC6 | `String::String(const String&)` [C1 complete] | construtor de cópia |
| 0xA17502C1 | `String::String()` [C1 complete] | construtor padrão |
| 0xECC1F1A4 | `String::String(const char*)` [C2 base] | construtor a partir do argumento |
| 0x457CCE55 | `String::String(const char*, unsigned int)` [C2 base] | construtor a partir do argumento |
| 0xD785BA85 | `String::String(const String&)` [C2 base] | construtor de cópia |
| 0x8816F7EF | `String::String()` [C2 base] | construtor padrão |
| 0x58854322 | `AttributeList::isAvailable() const` | true se o objeto foi inicializado/é válido |
| 0x5ED0B2F9 | `AttributeList::getAttribute(unsigned int) const` | retorna atributo por índice ou nome |
| 0x4F30F0CC | `AttributeList::getAttribute(const String*) const` | retorna atributo por índice ou nome |
| 0x38AEB52E | `AttributeList::getLength() const` | número de itens |
| 0xEC96BFC6 | `Dom::DocumentDebug::getStructSize() const` | debug: tamanho total das estruturas do Document |
| 0xE1100FC0 | `Dom::DocumentDebug::getAttrTableSize() const` | debug: tamanho da tabela de atributos |
| 0x6E1F1FFB | `Dom::DocumentDebug::getCharTableSize() const` | debug: tamanho da tabela de caracteres |
| 0x8F9CEE10 | `Dom::DocumentDebug::getElementTableSize() const` | debug: tamanho da tabela de elementos |
| 0xE1269956 | `Dom::Node::getNodeName() const` | nome do nó |
| 0xCED5E0FF | `Dom::Node::getNodeType() const` | tipo do nó (elemento, texto, ...) |
| 0x0D119AB3 | `Dom::Node::isAvailable() const` | true se o objeto foi inicializado/é válido |
| 0x3FD63FB8 | `Dom::Node::getLastChild() const` | último filho |
| 0x4F2D5541 | `Dom::Node::getNodeValue() const` | valor/texto do nó |
| 0xB405A149 | `Dom::Node::getAttributes() const` | atributos do nó |
| 0x117BEA8A | `Dom::Node::getChildNodes() const` | filhos do nó |
| 0x639D219C | `Dom::Node::getFirstChild() const` | primeiro filho |
| 0x9BD3722B | `Dom::Node::getParentNode() const` | pai do nó |
| 0x26BA1E6E | `Dom::Node::hasAttributes() const` | true se o nó tem atributos |
| 0x6C1913E9 | `Dom::Node::hasChildNodes() const` | true se o nó tem filhos |
| 0x1A46C0E1 | `Dom::Node::getNextSibling() const` | próximo irmão |
| 0x3E8122AB | `Dom::Node::getOwnerDocument() const` | Document dono do nó |
| 0x22DBB221 | `Dom::Document::getDocRoot() const` | nó documento (raiz lógica) |
| 0xE3D0A78A | `Dom::Document::getSibling(NodeId(u64)) const` | próximo irmão |
| 0x2D370226 | `Dom::Document::getXmlMeta() const` | NodeId da declaração <?xml ...?> |
| 0xA4D99D40 | `Dom::Document::isReadOnly() const` | true se o Document é somente leitura |
| 0xCD65B91F | `Dom::Document::getAttrName(NodeId(u64)) const` | nome do atributo |
| 0x883E1BFC | `Dom::Document::getNextAttr(NodeId(u64)) const` | próximo atributo |
| 0x471A22E8 | `Dom::Document::getNodeName(NodeId(u64)) const` | nome do nó |
| 0x62D3CB44 | `Dom::Document::getNodeType(NodeId(u64)) const` | tipo do nó (elemento, texto, ...) |
| 0x28FD79E3 | `Dom::Document::isAvailable() const` | true se o objeto foi inicializado/é válido |
| 0x9531C3CD | `Dom::Document::getAttribute(NodeId(u64), const String*) const` | retorna atributo por índice ou nome |
| 0x7C6A03FD | `Dom::Document::getAttrValue(NodeId(u64)) const` | valor do atributo |
| 0xEC856072 | `Dom::Document::getFirstAttr(NodeId(u64)) const` | primeiro atributo do elemento |
| 0xFBCF0D3E | `Dom::Document::getLastChild(NodeId(u64)) const` | último filho |
| 0xCDEC3F43 | `Dom::Document::getAttributes(NodeId(u64), Dom::NodeList*) const` | atributos do nó |
| 0xFC61FDF1 | `Dom::Document::getChildNodes(NodeId(u64), Dom::NodeList*) const` | filhos do nó |
| 0xDAC75E49 | `Dom::Document::getEntityType(NodeId(u64)) const` | tipo de entidade do nó |
| 0x3028E05D | `Dom::Document::getFirstChild(NodeId(u64)) const` | primeiro filho |
| 0xEA805296 | `Dom::Document::hasAttributes(NodeId(u64)) const` | true se o nó tem atributos |
| 0xC5E7431A | `Dom::Document::hasChildNodes(NodeId(u64)) const` | true se o nó tem filhos |
| 0x0C1DDEC5 | `Dom::Document::getSkippedText(NodeId(u64)) const` | texto descartado (ignorável) junto ao nó |
| 0xD2BFBC47 | `Dom::Document::getElementsByTagName(NodeId(u64), const String*, Dom::NodeList*) const` | preenche NodeList com descendentes de mesmo nome |
| 0xB34D9672 | `Dom::Document::getRoot() const` | retorna o nó/Value raiz |
| 0x36ACFF5E | `Dom::Document::getText(NodeId(u64)) const` | texto do nó |
| 0x161BA85E | `Dom::Document::getEntity(NodeId(u64)) const` | entidade associada ao nó |
| 0xA98B5758 | `Dom::Document::getParent(NodeId(u64)) const` | pai do nó |
| 0xD428753A | `Dom::Document::getStatus() const` | último código de status |
| 0x10530611 | `Dom::NodeList::isAvailable() const` | true se o objeto foi inicializado/é válido |
| 0xCDD1D418 | `Dom::NodeList::item(unsigned int) const` | NodeId na posição |
| 0xA41ED241 | `Dom::NodeList::findItem(const String*) const` | procura NodeId/nome na lista |
| 0xE1AB441D | `Dom::NodeList::findItem(NodeId(u64)) const` | procura NodeId/nome na lista |
| 0xFB9EDBF9 | `Dom::NodeList::getLength() const` | número de itens |
| 0xBAD4AAFA | `Dom::NodeList::operator[](unsigned int) const` | acesso por índice/chave |
| 0x1633846D | `Attr::isAvailable() const` | true se o objeto foi inicializado/é válido |
| 0x35134B85 | `Attr::getName() const` | nome do atributo |
| 0x7834A2F7 | `Attr::getValue() const` | valor do atributo |

### 3.3 SceSqlite (171 funções)

| NID | Função | Protótipo (SQLite 3.6.x, `sqlite3.h`) | Comportamento |
| --- | --- | --- | --- |
| 0x2034E5B7 | `sceSqliteConfigMallocMethods` | `int (SceSqliteMallocMethods *methods)` | wrapper Sony de sqlite3_config(SQLITE_CONFIG_MALLOC) com xMalloc/xRealloc/xFree do jogo (`psp2/sqlite.h`) |
| 0xAA8BE477 | `sqlite3_aggregate_context` | `void *(sqlite3_context*, int nBytes)` | memória por agregado, zerada na 1ª chamada |
| 0x6AB02532 | `sqlite3_aggregate_count` | `int (sqlite3_context*)` | obsoleta: nº de linhas do agregado |
| 0x24738263 | `sqlite3_auto_extension` | `int (void (*xEntryPoint)(void))` | registra extensão carregada em toda conexão nova |
| 0x93A6B7EF | `sqlite3_backup_finish` | `int (sqlite3_backup*)` | encerra backup e libera o handle |
| 0x91DDB12A | `sqlite3_backup_init` | `sqlite3_backup *(sqlite3 *pDest, const char *zDestName, sqlite3 *pSource, const char *zSourceName)` | inicia backup online entre bancos |
| 0x20D054CF | `sqlite3_backup_pagecount` | `int (sqlite3_backup*)` | total de páginas do banco de origem |
| 0x9962540B | `sqlite3_backup_remaining` | `int (sqlite3_backup*)` | páginas ainda por copiar |
| 0x2A15E081 | `sqlite3_backup_step` | `int (sqlite3_backup*, int nPage)` | copia até nPage páginas |
| 0x3F225D62 | `sqlite3_bind_blob` | `int (sqlite3_stmt*, int, const void*, int n, void(*)(void*))` | liga BLOB ao parâmetro |
| 0xDE007F1B | `sqlite3_bind_double` | `int (sqlite3_stmt*, int, double)` | liga double ao parâmetro |
| 0x14ABCBCC | `sqlite3_bind_int` | `int (sqlite3_stmt*, int, int)` | liga int ao parâmetro |
| 0x43D967EF | `sqlite3_bind_int64` | `int (sqlite3_stmt*, int, sqlite3_int64)` | liga int64 ao parâmetro (par de registradores alinhado) |
| 0xFF8A9974 | `sqlite3_bind_null` | `int (sqlite3_stmt*, int)` | liga NULL ao parâmetro |
| 0x17D4F00B | `sqlite3_bind_parameter_count` | `int (sqlite3_stmt*)` | nº de parâmetros SQL |
| 0xD4D2A5D8 | `sqlite3_bind_parameter_index` | `int (sqlite3_stmt*, const char *zName)` | índice do parâmetro nomeado |
| 0x96D3B5F9 | `sqlite3_bind_parameter_name` | `const char *(sqlite3_stmt*, int)` | nome do parâmetro |
| 0x613AB709 | `sqlite3_bind_text` | `int (sqlite3_stmt*, int, const char*, int n, void(*)(void*))` | liga texto UTF-8 |
| 0x9D0FEAEF | `sqlite3_bind_text16` | `int (sqlite3_stmt*, int, const void*, int, void(*)(void*))` | liga texto UTF-16 |
| 0x8A667D2A | `sqlite3_bind_value` | `int (sqlite3_stmt*, int, const sqlite3_value*)` | liga cópia de sqlite3_value |
| 0x78FBA2D0 | `sqlite3_bind_zeroblob` | `int (sqlite3_stmt*, int, int n)` | liga BLOB de n zeros |
| 0xA07AEEE3 | `sqlite3_blob_bytes` | `int (sqlite3_blob*)` | tamanho do BLOB aberto |
| 0xC085A15D | `sqlite3_blob_close` | `int (sqlite3_blob*)` | fecha handle de BLOB incremental |
| 0xD35B3E55 | `sqlite3_blob_open` | `int (sqlite3*, const char *zDb, const char *zTable, const char *zColumn, sqlite3_int64 iRow, int flags, sqlite3_blob **ppBlob)` | abre BLOB para I/O incremental |
| 0x71393AA4 | `sqlite3_blob_read` | `int (sqlite3_blob*, void *Z, int N, int iOffset)` | lê trecho do BLOB |
| 0xBDB46BCF | `sqlite3_blob_write` | `int (sqlite3_blob*, const void *z, int n, int iOffset)` | escreve trecho do BLOB |
| 0xB5B5D287 | `sqlite3_busy_handler` | `int (sqlite3*, int(*)(void*,int), void*)` | callback chamado quando a tabela está travada |
| 0xAE8E3630 | `sqlite3_busy_timeout` | `int (sqlite3*, int ms)` | espera até ms quando travado |
| 0xF206FBA1 | `sqlite3_changes` | `int (sqlite3*)` | linhas alteradas pelo último statement |
| 0x690947E2 | `sqlite3_clear_bindings` | `int (sqlite3_stmt*)` | zera todos os parâmetros para NULL |
| 0x7DF94B79 | `sqlite3_close` | `int (sqlite3*)` | fecha a conexão (SQLITE_BUSY se houver stmts vivos) |
| 0x836C99A3 | `sqlite3_collation_needed` | `int (sqlite3*, void*, void(*)(void*,sqlite3*,int eTextRep,const char*))` | callback para collation desconhecida |
| 0x537066CE | `sqlite3_collation_needed16` | `int (sqlite3*, void*, void(*)(void*,sqlite3*,int eTextRep,const void*))` | idem, nome UTF-16 |
| 0xFE237ED7 | `sqlite3_column_blob` | `const void *(sqlite3_stmt*, int iCol)` | coluna como BLOB |
| 0x36013FE4 | `sqlite3_column_bytes` | `int (sqlite3_stmt*, int iCol)` | tamanho em bytes (UTF-8/BLOB) |
| 0x439F160B | `sqlite3_column_bytes16` | `int (sqlite3_stmt*, int iCol)` | tamanho em bytes (UTF-16) |
| 0x8567A8DE | `sqlite3_column_count` | `int (sqlite3_stmt*)` | nº de colunas do resultado |
| 0x5AE92D67 | `sqlite3_column_decltype` | `const char *(sqlite3_stmt*, int)` | tipo declarado da coluna |
| 0xE058DE60 | `sqlite3_column_decltype16` | `const void *(sqlite3_stmt*, int)` | idem, UTF-16 |
| 0xC4866097 | `sqlite3_column_double` | `double (sqlite3_stmt*, int iCol)` | coluna como double (retorno em d0) |
| 0xE5B6BA01 | `sqlite3_column_int` | `int (sqlite3_stmt*, int iCol)` | coluna como int |
| 0x90BA0B88 | `sqlite3_column_int64` | `sqlite3_int64 (sqlite3_stmt*, int iCol)` | coluna como int64 (r0:r1) |
| 0xBC422DF6 | `sqlite3_column_name` | `const char *(sqlite3_stmt*, int N)` | nome da coluna |
| 0x6EF9A642 | `sqlite3_column_name16` | `const void *(sqlite3_stmt*, int N)` | idem, UTF-16 |
| 0x8E68D270 | `sqlite3_column_text` | `const unsigned char *(sqlite3_stmt*, int iCol)` | coluna como texto UTF-8 |
| 0xD7BD6B76 | `sqlite3_column_text16` | `const void *(sqlite3_stmt*, int iCol)` | coluna como texto UTF-16 |
| 0xDBB25C43 | `sqlite3_column_type` | `int (sqlite3_stmt*, int iCol)` | tipo dinâmico (INTEGER/FLOAT/TEXT/BLOB/NULL) |
| 0x2227F21D | `sqlite3_column_value` | `sqlite3_value *(sqlite3_stmt*, int iCol)` | coluna como sqlite3_value |
| 0x4BAE6E3B | `sqlite3_commit_hook` | `void *(sqlite3*, int(*)(void*), void*)` | callback antes de COMMIT |
| 0x2E28B2A7 | `sqlite3_complete` | `int (const char *sql)` | true se o SQL termina com statement completo |
| 0x4EAB317B | `sqlite3_complete16` | `int (const void *sql)` | idem, UTF-16 |
| 0x96C5D388 | `sqlite3_config` | `int (int op, ...)` | configuração global (variádica, antes de initialize) |
| 0x74259C09 | `sqlite3_context_db_handle` | `sqlite3 *(sqlite3_context*)` | conexão da função SQL em execução |
| 0xC61B63FB | `sqlite3_create_collation` | `int (sqlite3*, const char *zName, int eTextRep, void*, int(*xCompare)(void*,int,const void*,int,const void*))` | registra collation |
| 0x4B110AF2 | `sqlite3_create_collation_v2` | `int (sqlite3*, const char*, int, void*, int(*)(void*,int,const void*,int,const void*), void(*xDestroy)(void*))` | idem com destrutor |
| 0xF7FE99C8 | `sqlite3_create_collation16` | `int (sqlite3*, const void *zName, int eTextRep, void*, int(*)(void*,int,const void*,int,const void*))` | idem, nome UTF-16 |
| 0xB0543897 | `sqlite3_create_function` | `int (sqlite3*, const char *zFunctionName, int nArg, int eTextRep, void*, void(*xFunc)(sqlite3_context*,int,sqlite3_value**), void(*xStep)(...), void(*xFinal)(sqlite3_context*))` | registra função SQL/agregado |
| 0x7655FA45 | `sqlite3_create_function16` | `int (sqlite3*, const void *zFunctionName, int nArg, int eTextRep, void*, xFunc, xStep, xFinal)` | idem, nome UTF-16 |
| 0x8970C45F | `sqlite3_create_module` | `int (sqlite3*, const char *zName, const sqlite3_module*, void *pClientData)` | registra módulo de tabela virtual |
| 0x1AA3BC1A | `sqlite3_create_module_v2` | `int (sqlite3*, const char*, const sqlite3_module*, void*, void(*xDestroy)(void*))` | idem com destrutor |
| 0x61911935 | `sqlite3_data_count` | `int (sqlite3_stmt*)` | nº de colunas da linha atual |
| 0xADFB25C0 | `sqlite3_db_config` | `int (sqlite3*, int op, ...)` | configuração por conexão (lookaside) |
| 0x2C62429E | `sqlite3_db_handle` | `sqlite3 *(sqlite3_stmt*)` | conexão dona do statement |
| 0xA8E53D26 | `sqlite3_db_mutex` | `sqlite3_mutex *(sqlite3*)` | mutex da conexão |
| 0xB5DFAF6A | `sqlite3_db_status` | `int (sqlite3*, int op, int *pCur, int *pHiwtr, int resetFlg)` | estatística da conexão |
| 0x7E2A5E8F | `sqlite3_declare_vtab` | `int (sqlite3*, const char *zSQL)` | declara esquema da tabela virtual |
| 0x9BFC6F07 | `sqlite3_enable_load_extension` | `int (sqlite3*, int onoff)` | liga carga de extensões (inútil no Vita) |
| 0xF0094BED | `sqlite3_enable_shared_cache` | `int (int)` | liga cache compartilhado |
| 0xA7AAE2E7 | `sqlite3_errcode` | `int (sqlite3*)` | último código de erro |
| 0xABFB8B6E | `sqlite3_errmsg` | `const char *(sqlite3*)` | mensagem do último erro |
| 0xF0DE1A97 | `sqlite3_errmsg16` | `const void *(sqlite3*)` | idem, UTF-16 |
| 0x2371E86A | `sqlite3_exec` | `int (sqlite3*, const char *sql, int(*callback)(void*,int,char**,char**), void*, char **errmsg)` | executa SQL chamando callback por linha |
| 0xF8AA518B | `sqlite3_expired` | `int (sqlite3_stmt*)` | obsoleta: stmt expirado |
| 0x91187282 | `sqlite3_extended_errcode` | `int (sqlite3*)` | código de erro estendido |
| 0x3892C4B8 | `sqlite3_extended_result_codes` | `int (sqlite3*, int onoff)` | liga códigos estendidos |
| 0xBB096FBD | `sqlite3_file_control` | `int (sqlite3*, const char *zDbName, int op, void*)` | repassa controle ao VFS |
| 0xB656B7E2 | `sqlite3_finalize` | `int (sqlite3_stmt*)` | destrói o statement |
| 0xCBF0CA8A | `sqlite3_free` | `void (void*)` | libera memória alocada pelo SQLite |
| 0x1FEC6959 | `sqlite3_free_table` | `void (char **result)` | libera resultado de get_table |
| 0x0910C3CB | `sqlite3_get_autocommit` | `int (sqlite3*)` | true se em modo autocommit |
| 0x394FC1CB | `sqlite3_get_auxdata` | `void *(sqlite3_context*, int N)` | metadado auxiliar do argumento N |
| 0xF2AB9C89 | `sqlite3_get_table` | `int (sqlite3*, const char *zSql, char ***pazResult, int *pnRow, int *pnColumn, char **pzErrmsg)` | executa e devolve tabela de strings |
| 0xF48E021B | `sqlite3_global_recover` | `int (void)` | obsoleta: no-op |
| 0xC22AF627 | `sqlite3_initialize` | `int (void)` | inicializa a biblioteca |
| 0x3CB771AC | `sqlite3_interrupt` | `void (sqlite3*)` | aborta operação em curso |
| 0x301851A1 | `sqlite3_last_insert_rowid` | `sqlite3_int64 (sqlite3*)` | rowid do último INSERT |
| 0x26E46324 | `sqlite3_libversion` | `const char *(void)` | versão em texto (identifica a versão do firmware) |
| 0x5982F404 | `sqlite3_libversion_number` | `int (void)` | versão numérica |
| 0xDED2D517 | `sqlite3_limit` | `int (sqlite3*, int id, int newVal)` | lê/ajusta limite de runtime |
| 0xC9EA8E1F | `sqlite3_load_extension` | `int (sqlite3*, const char *zFile, const char *zProc, char **pzErrMsg)` | carrega extensão (deve falhar no Vita) |
| 0xF01DEB95 | `sqlite3_malloc` | `void *(int)` | aloca pelo alocador configurado |
| 0x56EDF517 | `sqlite3_memory_alarm` | `int (void(*)(void*,sqlite3_int64,int), void*, sqlite3_int64)` | obsoleta: alarme de memória |
| 0x2F33DAD6 | `sqlite3_memory_highwater` | `sqlite3_int64 (int resetFlag)` | pico de memória usada |
| 0x8E4F6ED5 | `sqlite3_memory_used` | `sqlite3_int64 (void)` | memória em uso |
| 0xE630216C | `sqlite3_mprintf` | `char *(const char*, ...)` | printf para string alocada (variádica) |
| 0xEEB7839F | `sqlite3_mutex_alloc` | `sqlite3_mutex *(int)` | aloca mutex |
| 0x60DB89C0 | `sqlite3_mutex_enter` | `void (sqlite3_mutex*)` | trava mutex |
| 0x38E933E2 | `sqlite3_mutex_free` | `void (sqlite3_mutex*)` | libera mutex |
| 0x545ABDDB | `sqlite3_mutex_leave` | `void (sqlite3_mutex*)` | destrava mutex |
| 0x218D700E | `sqlite3_mutex_try` | `int (sqlite3_mutex*)` | tenta travar |
| 0xD257592A | `sqlite3_next_stmt` | `sqlite3_stmt *(sqlite3 *pDb, sqlite3_stmt *pStmt)` | itera statements da conexão |
| 0x8E506859 | `sqlite3_open` | `int (const char *filename, sqlite3 **ppDb)` | abre banco (caminho Vita, ex. savedata0:) |
| 0xA1E98A41 | `sqlite3_open_v2` | `int (const char *filename, sqlite3 **ppDb, int flags, const char *zVfs)` | abre banco com flags/VFS |
| 0x881EEDD8 | `sqlite3_open16` | `int (const void *filename, sqlite3 **ppDb)` | idem, caminho UTF-16 |
| 0x9CE7C4C3 | `sqlite3_os_end` | `int (void)` | finaliza camada de SO |
| 0xBD304836 | `sqlite3_os_init` | `int (void)` | inicializa camada de SO (registra VFS padrão) |
| 0xAF680D40 | `sqlite3_overload_function` | `int (sqlite3*, const char *zFuncName, int nArg)` | garante função para tabela virtual |
| 0x0C1B5509 | `sqlite3_prepare` | `int (sqlite3 *db, const char *zSql, int nByte, sqlite3_stmt **ppStmt, const char **pzTail)` | compila SQL (legado) |
| 0xBC4BDCF4 | `sqlite3_prepare_v2` | `int (sqlite3 *db, const char *zSql, int nByte, sqlite3_stmt **ppStmt, const char **pzTail)` | compila SQL |
| 0xC657CFB8 | `sqlite3_prepare16` | `int (sqlite3*, const void *zSql, int nByte, sqlite3_stmt**, const void **pzTail)` | compila SQL UTF-16 (legado) |
| 0x426D81D2 | `sqlite3_prepare16_v2` | `int (sqlite3*, const void *zSql, int nByte, sqlite3_stmt**, const void **pzTail)` | compila SQL UTF-16 |
| 0x48B789A1 | `sqlite3_profile` | `void *(sqlite3*, void(*)(void*,const char*,sqlite3_uint64), void*)` | callback de profiling |
| 0x19165D04 | `sqlite3_progress_handler` | `void (sqlite3*, int, int(*)(void*), void*)` | callback a cada N opcodes |
| 0x5A2590BF | `sqlite3_randomness` | `void (int N, void *P)` | N bytes pseudo-aleatórios |
| 0xD1CF5631 | `sqlite3_realloc` | `void *(void*, int)` | realoca pelo alocador configurado |
| 0x8F99FBE5 | `sqlite3_release_memory` | `int (int)` | tenta liberar memória |
| 0xA6ECC214 | `sqlite3_reset` | `int (sqlite3_stmt*)` | reinicia o statement |
| 0xC4296FFD | `sqlite3_reset_auto_extension` | `void (void)` | remove extensões automáticas |
| 0x90CDF8C1 | `sqlite3_result_blob` | `void (sqlite3_context*, const void*, int, void(*)(void*))` | resultado BLOB |
| 0xC2A5C2F8 | `sqlite3_result_double` | `void (sqlite3_context*, double)` | resultado double |
| 0x063BFACA | `sqlite3_result_error` | `void (sqlite3_context*, const char*, int)` | resultado erro (mensagem) |
| 0x1165223C | `sqlite3_result_error_code` | `void (sqlite3_context*, int)` | resultado erro (código) |
| 0x944E747A | `sqlite3_result_error_nomem` | `void (sqlite3_context*)` | resultado SQLITE_NOMEM |
| 0xAB9EFF96 | `sqlite3_result_error_toobig` | `void (sqlite3_context*)` | resultado SQLITE_TOOBIG |
| 0xAB2AEB4A | `sqlite3_result_error16` | `void (sqlite3_context*, const void*, int)` | erro, mensagem UTF-16 |
| 0x5C9CD9D4 | `sqlite3_result_int` | `void (sqlite3_context*, int)` | resultado int |
| 0x0EF1AA07 | `sqlite3_result_int64` | `void (sqlite3_context*, sqlite3_int64)` | resultado int64 |
| 0x6DE09482 | `sqlite3_result_null` | `void (sqlite3_context*)` | resultado NULL |
| 0x696B5E6A | `sqlite3_result_text` | `void (sqlite3_context*, const char*, int, void(*)(void*))` | resultado texto UTF-8 |
| 0x3AF5D206 | `sqlite3_result_text16` | `void (sqlite3_context*, const void*, int, void(*)(void*))` | resultado UTF-16 nativo |
| 0xEE3E906A | `sqlite3_result_text16be` | `void (sqlite3_context*, const void*, int, void(*)(void*))` | resultado UTF-16BE |
| 0x845B4FC2 | `sqlite3_result_text16le` | `void (sqlite3_context*, const void*, int, void(*)(void*))` | resultado UTF-16LE |
| 0x09664492 | `sqlite3_result_value` | `void (sqlite3_context*, sqlite3_value*)` | resultado cópia de value |
| 0x3D463CF7 | `sqlite3_result_zeroblob` | `void (sqlite3_context*, int n)` | resultado BLOB de zeros |
| 0x67F53D6B | `sqlite3_rollback_hook` | `void *(sqlite3*, void(*)(void*), void*)` | callback em ROLLBACK |
| 0x77FB3458 | `sqlite3_set_authorizer` | `int (sqlite3*, int(*xAuth)(void*,int,const char*,const char*,const char*,const char*), void*)` | callback de autorização na compilação |
| 0x129E01C9 | `sqlite3_set_auxdata` | `void (sqlite3_context*, int N, void*, void(*)(void*))` | guarda metadado do argumento N |
| 0x99B5A4A3 | `sqlite3_shutdown` | `int (void)` | desinicializa a biblioteca |
| 0x6B88D1D4 | `sqlite3_sleep` | `int (int ms)` | dorme ms (via VFS → sceKernelDelayThread) |
| 0xCC189941 | `sqlite3_snprintf` | `char *(int, char*, const char*, ...)` | snprintf (variádica; ordem de args do SQLite) |
| 0xD1458BA7 | `sqlite3_soft_heap_limit` | `void (int)` | limite suave de heap |
| 0x4CCB58A2 | `sqlite3_sourceid` | `const char *(void)` | id do código-fonte (data + hash) |
| 0x082C36D4 | `sqlite3_sql` | `const char *(sqlite3_stmt*)` | SQL original do statement |
| 0xD8C435AA | `sqlite3_status` | `int (int op, int *pCurrent, int *pHighwater, int resetFlag)` | estatística global |
| 0xCA8755B7 | `sqlite3_step` | `int (sqlite3_stmt*)` | executa até a próxima linha (SQLITE_ROW/DONE) |
| 0xF7ABF5FA | `sqlite3_stmt_status` | `int (sqlite3_stmt*, int op, int resetFlg)` | contador do statement |
| 0x12E2FC18 | `sqlite3_strnicmp` | `int (const char*, const char*, int)` | comparação case-insensitive |
| 0x324D4EFD | `sqlite3_test_control` | `int (int op, ...)` | controle de testes internos |
| 0x173C9C0B | `sqlite3_thread_cleanup` | `void (void)` | obsoleta: no-op |
| 0xA3B818DA | `sqlite3_threadsafe` | `int (void)` | modo de thread-safety compilado |
| 0x02ADA92D | `sqlite3_total_changes` | `int (sqlite3*)` | linhas alteradas desde a abertura |
| 0xFC127A83 | `sqlite3_trace` | `void *(sqlite3*, void(*xTrace)(void*,const char*), void*)` | callback de trace do SQL |
| 0x6EC012E5 | `sqlite3_transfer_bindings` | `int (sqlite3_stmt*, sqlite3_stmt*)` | obsoleta: move bindings |
| 0xEB05FE87 | `sqlite3_update_hook` | `void *(sqlite3*, void(*)(void*,int,char const*,char const*,sqlite3_int64), void*)` | callback em INSERT/UPDATE/DELETE |
| 0x78FF81FB | `sqlite3_user_data` | `void *(sqlite3_context*)` | pUserData da função SQL |
| 0xC9962B31 | `sqlite3_value_blob` | `const void *(sqlite3_value*)` | value como BLOB |
| 0x5368EF1F | `sqlite3_value_bytes` | `int (sqlite3_value*)` | tamanho (UTF-8/BLOB) |
| 0x4D10900D | `sqlite3_value_bytes16` | `int (sqlite3_value*)` | tamanho (UTF-16) |
| 0xF1F2C9BE | `sqlite3_value_double` | `double (sqlite3_value*)` | value como double |
| 0x4809A520 | `sqlite3_value_int` | `int (sqlite3_value*)` | value como int |
| 0xA6581C04 | `sqlite3_value_int64` | `sqlite3_int64 (sqlite3_value*)` | value como int64 |
| 0x81B7D43D | `sqlite3_value_numeric_type` | `int (sqlite3_value*)` | tipo após afinidade numérica |
| 0x7EB97356 | `sqlite3_value_text` | `const unsigned char *(sqlite3_value*)` | value como UTF-8 |
| 0x5BBE38C2 | `sqlite3_value_text16` | `const void *(sqlite3_value*)` | value como UTF-16 nativo |
| 0x3B89AA8D | `sqlite3_value_text16be` | `const void *(sqlite3_value*)` | value como UTF-16BE |
| 0x014863A6 | `sqlite3_value_text16le` | `const void *(sqlite3_value*)` | value como UTF-16LE |
| 0xC5EEBB5D | `sqlite3_value_type` | `int (sqlite3_value*)` | tipo do value |
| 0x0C6DD8C3 | `sqlite3_vfs_find` | `sqlite3_vfs *(const char *zVfsName)` | procura VFS registrado |
| 0x65F53B9C | `sqlite3_vfs_register` | `int (sqlite3_vfs*, int makeDflt)` | registra VFS |
| 0x69CF4171 | `sqlite3_vfs_unregister` | `int (sqlite3_vfs*)` | remove VFS |
| 0xC6372184 | `sqlite3_vmprintf` | `char *(const char*, va_list)` | mprintf com va_list |

---

## 4. Utilitários de sistema (HLE)

Todos estes falam com o SceShell no hardware real. No VitaSharp não há shell: a regra é **aceitar a chamada,
validar argumentos como o firmware, registrar no log e devolver o estado "nada aconteceu / indisponível"** de
forma coerente entre as funções da mesma biblioteca.

Arquivo sugerido: `src/VitaSharp.Core/Hle/Modules/SystemUtil.cs` (`sealed class SystemUtil : HleModule`) para
4.1–4.7 e `src/VitaSharp.Core/Hle/Modules/MediaExport.cs` para 4.8. Strings UTF-16 do guest: ler com um helper
`ReadUtf16(uint addr, int maxChars)` (criar em `GuestMemory` se ainda não existir).

### 4.1 SceRegMgrForGame

Estado compartilhado: um dicionário `id → valor` com os padrões abaixo, carregado de / salvo em
`<FsRoot>/vd0/registry/vitasharp-regmgr.json` (sugestão; criar o diretório). Tipos e IDs de
`psp2/registrymgr.h`. Valores coerentes com o que `AppUtil.sceAppUtilSystemParamGetInt` já devolve
(`Hle/Modules/AppUtil.cs`), para o jogo não ver configurações contraditórias.

| ID | Chave | Tipo | Padrão | Gravável | Observação |
| --- | --- | --- | --- | --- | --- |
| 0x00037502 | /CONFIG/SYSTEM/language | int | 1 (inglês EUA) | não | mesma enumeração de `SCE_SYSTEM_PARAM_LANG_*` (a confirmar); idealmente derivar de `EmulatorOptions.Language` com a mesma tabela usada pelo AppUtil |
| 0x00229142 | /CONFIG/SYSTEM/button_assign | int | 1 (X confirma) | não | AppUtil devolve 1 para ENTER_BUTTON; semântica 0=O/1=X a confirmar |
| 0x00088776 | /CONFIG/DATE/date_format | int | 0 (AAAAMMDD) | não | igual ao AppUtil |
| 0x00668503 | /CONFIG/DATE/time_format | int | 0 (12 h) | não | igual ao AppUtil |
| 0x00100591 | /CONFIG/DATE/time_zone | int | a confirmar | não | o registro pode guardar um índice de fuso, não minutos; até confirmar, devolver o mesmo que o AppUtil (minutos do host) |
| 0x00760538 | /CONFIG/DATE/summer_time | int | horário de verão do host (0/1) | não | igual ao AppUtil |
| 0x007F9315 | /CONFIG/DATE/is_summer_time | int | idem | não | |
| 0x00598438 | /CONFIG/SYSTEM/username | str | `"VitaSharp"` | não | mesmo nome do `sceAppUtilSystemParamGetString(3)` |
| 0x00186122 | /CONFIG/SECURITY/PARENTAL/passcode | str | `"0000"` | não | padrão de fábrica do Vita |
| 0x00450F32 | /CONFIG/NP/account_id | bin (8) | zeros | não | sem conta PSN |
| 0x008F94F9 | /CONFIG/NP/country | str | `"us"` (a confirmar formato) | não | |
| 0x00033818 | /CONFIG/NP/env | str | `"np"` (a confirmar) | não | |
| 0x00023FC2, 0x00611DC9, 0x008A2AD7 | /CONFIG/ACCESSIBILITY/large_text, bold_text, contrast | int | 0 | não | |
| 0x0089C9CF, 0x00711659 | /CONFIG/SECURITY/PARENTAL/store_start_control, content_start_control | int | 0 | não | controle parental desligado |
| 0x0081649F, 0x00068303, 0x001B2292, 0x00872621 | /CONFIG/BROWSER/ADDIN/TRENDMICRO/* | int | 0 | **sim** | |
| 0x00612B3E | /CONFIG/BROWSER/web_security_status | int | 0 | **sim** | |
| 0x005F6737, 0x0091F34F | /CONFIG/NP2/TWITTER/access_token, access_token_secret | str | `""` | **sim** | |
| 0x008D89EB, 0x004E7A16, 0x00987180, 0x000504E4 | /CONFIG/NP2/TELEPORT/wol_target_mac_address, target_name, initial_target, passcode_client | str/bin | vazio | **sim** | tipo exato a confirmar |
| 0x004F7E60, 0x0094E320 | /CONFIG/PS4LINK/counter, keys | int / bin | 0 / vazio | **sim** | |
| 0x008C3860 | /CONFIG/BROWSER/DEBUG/net_dbg_config | int | 0 | **sim** | |
| demais IDs do enum `SceRegMgrSystemParam` | /CONFIG/GAME/*, /CONFIG/NP*/*, /DEVENV/*, ... | int/str | 0 / `""` | não | flags de debug (fake_*, show_debug_info) sempre 0 |

Tipos exatos e padrões reais: a confirmar lendo `os0:kd/registry.db0` de um dump (XOR com a chave de 16 bytes usada
pelo Vita3K em `vita3k/regmgr/src/regmgr.cpp`, cabeçalho de 138 bytes; seção `[REG-BAS` traz `tipo:tamanho:padrão`).

#### sceRegMgrSystemParamGetInt
- [ ] **Prioridade:** P2 · **Biblioteca:** SceRegMgrForGame · **NID:** 0x347C1BDB
- **Protótipo:** `int sceRegMgrSystemParamGetInt(const int id, int *buf);` (`psp2/registrymgr.h`)
- **Comportamento:** procura `id` na tabela fixa e escreve o inteiro em `*buf`.
- **Retorno/erros:** 0; ID desconhecido ou `buf` NULL → `0x800D0004`; ID de tipo não-int → a confirmar (sugestão
  `0x800D0004`).
- **Estruturas:** —
- **Implementação no VitaSharp:** `SystemUtil.cs`, dicionário compartilhado com as outras 5 funções.
- **Validação:** homebrew que leia `0x00037502` e compare com `sceAppUtilSystemParamGetInt(1)`.

#### sceRegMgrSystemParamGetStr
- [ ] **Prioridade:** P2 · **Biblioteca:** SceRegMgrForGame · **NID:** 0x877ADB3F
- **Protótipo:** `int sceRegMgrSystemParamGetStr(const int id, char *buf, const int size);` (`psp2/registrymgr.h`)
- **Comportamento:** copia a string da chave para `buf`, até `size` bytes, terminada em NUL.
- **Retorno/erros:** 0; ID desconhecido ou `buf` NULL → `0x800D0004`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `GuestMemory.WriteCString(buf, valor, size)`.
- **Validação:** ler `0x00598438` → `"VitaSharp"`.

#### sceRegMgrSystemParamGetBin
- [ ] **Prioridade:** P2 · **Biblioteca:** SceRegMgrForGame · **NID:** 0x7FFE2CDF
- **Protótipo:** `SceInt32 sceRegMgrSystemParamGetBin(SceRegMgrSystemParam id, void *buf, SceSize buf_size);`
- **Comportamento:** copia exatamente `min(buf_size, 0x800)` bytes do valor (completar com zeros se o valor
  armazenado for menor).
- **Retorno/erros:** 0; ID desconhecido ou `buf` NULL → `0x800D0004`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `GuestMemory.Span(buf, n)`.
- **Validação:** ler `0x00450F32` com 8 bytes → zeros.

#### sceRegMgrSystemParamSetInt
- [ ] **Prioridade:** P2 · **Biblioteca:** SceRegMgrForGame · **NID:** 0xC8F73311
- **Protótipo:** `SceInt32 sceRegMgrSystemParamSetInt(SceRegMgrSystemParam id, SceInt32 value);`
- **Comportamento:** grava o inteiro se o ID for gravável e de tipo int; persiste no JSON.
- **Retorno/erros:** 0; ID desconhecido → `0x800D0004`; ID conhecido somente leitura → `0x800D0015`.
- **Estruturas:** —
- **Implementação no VitaSharp:** conjunto `Writable` com os IDs marcados "sim" na tabela.
- **Validação:** `SetInt(0x00612B3E, 1)` seguido de `GetInt` → 1; `SetInt(0x00037502, 0)` → `0x800D0015`.

#### sceRegMgrSystemParamSetStr
- [ ] **Prioridade:** P2 · **Biblioteca:** SceRegMgrForGame · **NID:** 0xCB3246E3
- **Protótipo:** `SceInt32 sceRegMgrSystemParamSetStr(SceRegMgrSystemParam id, const char *buf, SceSize buf_size);`
- **Comportamento:** copia exatamente `buf_size` bytes (sem acrescentar NUL) para a chave gravável de tipo string.
- **Retorno/erros:** 0; `buf` NULL ou `buf_size > 0x7C0` → `0x800D0004` (código exato a confirmar; o header só diz
  que é exigido); ID desconhecido → `0x800D0004`; somente leitura → `0x800D0015`.
- **Estruturas:** —
- **Implementação no VitaSharp:** guardar `byte[]` cru.
- **Validação:** gravar e reler `0x005F6737`.

#### sceRegMgrSystemParamSetBin
- [ ] **Prioridade:** P2 · **Biblioteca:** SceRegMgrForGame · **NID:** 0xD5A73557
- **Protótipo:** `SceInt32 sceRegMgrSystemParamSetBin(SceRegMgrSystemParam id, const void *buf, SceSize buf_size);`
- **Comportamento:** igual a SetStr, para chaves binárias.
- **Retorno/erros:** 0; `buf` NULL ou `buf_size > 0x7C0` → erro (a confirmar, sugestão `0x800D0004`); desconhecido
  `0x800D0004`; somente leitura `0x800D0015`.
- **Estruturas:** —
- **Implementação no VitaSharp:** idem.
- **Validação:** gravar e reler `0x0094E320`.

#### sceRegMgrSystemIsBlueScreen
- [ ] **Prioridade:** P2 · **Biblioteca:** SceRegMgrForGame · **NID:** 0x169A0D1D
- **Protótipo:** `int sceRegMgrSystemIsBlueScreen(void);` **(inferido; não há protótipo no vita-headers)**
- **Comportamento:** informa se o sistema está em modo "blue screen" (tela de demonstração/loja; a confirmar).
- **Retorno/erros:** retornar 0 (não está). Se desmontagem mostrar ponteiro de saída, escrever 0 nele.
- **Estruturas:** —
- **Implementação no VitaSharp:** `[Hle] int sceRegMgrSystemIsBlueScreen() => 0;`
- **Validação:** confirmar assinatura no `SceRegistryMgr` (stub user) antes de confiar.

### 4.2 SceLiveAreaUtil

Sem shell: as operações "dão certo" e não têm efeito. Estado: `pendingResult` (int?) para o modelo
"uma operação assíncrona pendente por processo" e, opcionalmente, um dicionário `frameId → (revision, userData)`
alimentado pelos updates (parse leve do XML: atributo `id` de `<frame>` e conteúdo de `<user-data>` — opcional).

Erros comuns (todos de `psp2/livearea.h`): `0x80104001` operação pendente / nenhuma pendente,
`0x80104002` argumento inválido, `0x80104003` caminho não resolvido, `0x80104004` XML inválido,
`0x80104009` XML > 10239 bytes, `0x8010400A` objeto/frame indisponível, `0x8010400B` caminho não permitido.

#### sceLiveAreaGetStatus
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0x7FE5B83F
- **Protótipo:** `int sceLiveAreaGetStatus(void);` (`psp2/livearea.h`)
- **Comportamento:** consulta a operação assíncrona pendente; o resultado final é devolvido uma única vez e a
  pendência é limpa.
- **Retorno/erros:** 1 se ainda pendente; 0 (ou o erro final) uma vez; depois `0x80104001`.
- **Estruturas:** —
- **Implementação no VitaSharp:** como as operações HLE terminam na hora, a primeira chamada após um `*Async`
  devolve o resultado guardado e zera `pendingResult`; sem pendência → `0x80104001`.
- **Validação:** `ReplaceAllAsync` → `GetStatus` = 0 → `GetStatus` = `0x80104001`.

#### sceLiveAreaReplaceAllSync
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0x29C65C0B
- **Protótipo:** `int sceLiveAreaReplaceAllSync(const char *contents_path);`
- **Comportamento:** substitui toda a LiveArea do título atual pelo conteúdo do diretório.
- **Retorno/erros:** 0; `contents_path` NULL ou > 255 caracteres → `0x80104002`; caminho que não começa com
  `app0:` ou `savedata0:` → `0x8010400B`; diretório inexistente no `Vfs` → `0x80104003`.
- **Estruturas:** —
- **Implementação no VitaSharp:** validar, `Emu.Vfs.Resolve` + `Directory.Exists`, logar e retornar 0.
- **Validação:** teste unitário do validador de caminho.

#### sceLiveAreaReplaceAllAsync
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0x18066BA9
- **Protótipo:** `int sceLiveAreaReplaceAllAsync(const char *contents_path);`
- **Comportamento:** versão assíncrona; resultado via `sceLiveAreaGetStatus`.
- **Retorno/erros:** 0 submetido; `0x80104001` se já há pendência; `0x80104002` argumento inválido. Erros de
  caminho/permissão vão para `pendingResult`.
- **Estruturas:** —
- **Implementação no VitaSharp:** executar a lógica do Sync e guardar o resultado em `pendingResult`.
- **Validação:** ver `sceLiveAreaGetStatus`.

#### sceLiveAreaReplaceAllForTitleSync
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0xB0571F88
- **Protótipo:** `int sceLiveAreaReplaceAllForTitleSync(const char *contents_path, const char *title_id, const SceRtcTick *last_modified);`
- **Comportamento:** como ReplaceAllSync para um título explícito; não aplica a lista de prefixos `app0:`/`savedata0:`.
- **Retorno/erros:** 0; `0x80104002`; `0x80104003`.
- **Estruturas:** `SceRtcTick` = u64.
- **Implementação no VitaSharp:** mesmo validador sem a regra de prefixo.
- **Validação:** idem.

#### sceLiveAreaReplaceAllForTitleAsync
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0xEF2A3C3D
- **Protótipo:** `int sceLiveAreaReplaceAllForTitleAsync(const char *contents_path, const char *title_id, const SceRtcTick *last_modified);`
- **Comportamento:** assíncrona da anterior.
- **Retorno/erros:** 0; `0x80104001`; `0x80104002`.
- **Estruturas:** —
- **Implementação no VitaSharp:** idem padrão Async.
- **Validação:** idem.

#### sceLiveAreaUpdateFrameSync
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0xCFCEDE95
- **Protótipo:** `int sceLiveAreaUpdateFrameSync(const char *format_version, const char *frame_xml, SceInt32 frame_xml_length, const char *contents_path, SceLiveAreaTargetType target_type);`
- **Comportamento:** atualiza um frame (id lido do XML) do título atual.
- **Retorno/erros:** 0; `format_version != "01.00"`, ponteiros NULL, caminho > 255 ou `target_type` ∉ {0,1} →
  `0x80104002`; tamanho (negativo = `strlen`) > 10239 → `0x80104009`; XML sem `<frame` → `0x80104004` (parse
  mínimo); prefixo não permitido → `0x8010400B`.
- **Estruturas:** `SceLiveAreaTargetType`: 0 NORMAL, 1 RETAIL.
- **Implementação no VitaSharp:** validar; opcionalmente incrementar revisão do frame no dicionário.
- **Validação:** teste unitário com XML válido/ inválido.

#### sceLiveAreaUpdateFrameAsync
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0xD330285D
- **Protótipo:** `int sceLiveAreaUpdateFrameAsync(const char *format_version, const char *frame_xml, SceInt32 frame_xml_length, const char *contents_path, SceLiveAreaTargetType target_type);`
- **Comportamento:** assíncrona de UpdateFrameSync.
- **Retorno/erros:** 0; `0x80104001`; `0x80104002`; `0x80104009` (síncronos); parse/permissão → `pendingResult`.
- **Estruturas:** —
- **Implementação no VitaSharp:** padrão Async.
- **Validação:** idem.

#### sceLiveAreaUpdateFrameForTitleSync
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0x009CD8E2
- **Protótipo:** `int sceLiveAreaUpdateFrameForTitleSync(const char *format_version, const char *frame_xml, SceInt32 frame_xml_length, const char *contents_path, SceLiveAreaTargetType target_type, const char *title_id);`
- **Comportamento:** UpdateFrameSync para título explícito, sem regra de prefixo.
- **Retorno/erros:** 0; `0x80104002`; `0x80104004`; `0x80104009`.
- **Estruturas:** —
- **Implementação no VitaSharp:** idem (6º argumento vem da pilha; o binder do `HleFramework` cuida).
- **Validação:** idem.

#### sceLiveAreaUpdateFrameForTitleAsync
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0xC1F7A931
- **Protótipo:** `int sceLiveAreaUpdateFrameForTitleAsync(const char *format_version, const char *frame_xml, SceInt32 frame_xml_length, const char *contents_path, SceLiveAreaTargetType target_type, const char *title_id);`
- **Comportamento:** assíncrona da anterior.
- **Retorno/erros:** 0; `0x80104001`; `0x80104002`; `0x80104009`.
- **Estruturas:** —
- **Implementação no VitaSharp:** padrão Async.
- **Validação:** idem.

#### sceLiveAreaUpdateFramesForTitleSync
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0x31A5E6AB
- **Protótipo:** `int sceLiveAreaUpdateFramesForTitleSync(const char *format_version, const char *const *frame_xml_array, SceUInt32 frame_count, const char *contents_path, SceLiveAreaTargetType target_type, const char *title_id);`
- **Comportamento:** atualiza vários frames (strings NUL-terminated, sem limite individual).
- **Retorno/erros:** 0; `frame_count == 0`, entrada NULL, versão/alvo inválidos → `0x80104002`; algum XML sem
  `<frame` → `0x80104004`; requisição serializada (cada XML arredondado a 16 bytes + tabela) > 0x3F00 → `0x80020588`.
- **Estruturas:** —
- **Implementação no VitaSharp:** calcular o tamanho como o firmware para devolver `0x80020588` (fórmula exata da
  tabela de descritores a confirmar; aproximar por `Σ align16(len+1) + 8*frame_count`).
- **Validação:** teste unitário.

#### sceLiveAreaUpdateFramesForTitleAsync
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0x876A649B
- **Protótipo:** `int sceLiveAreaUpdateFramesForTitleAsync(const char *format_version, const char *const *frame_xml_array, SceUInt32 frame_count, const char *contents_path, SceLiveAreaTargetType target_type, const char *title_id);`
- **Comportamento:** assíncrona da anterior.
- **Retorno/erros:** 0; `0x80104001`; `0x80104002`; `0x80020588`.
- **Estruturas:** —
- **Implementação no VitaSharp:** padrão Async.
- **Validação:** idem.

#### sceLiveAreaGetRevision
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0x833F5D0A
- **Protótipo:** `int sceLiveAreaGetRevision(SceUInt64 *revision);`
- **Comportamento:** revisão global da LiveArea do título.
- **Retorno/erros:** 0 com `*revision = 0` (ou contador incrementado a cada Replace/Update); `revision` NULL →
  `0x80104002`.
- **Estruturas:** —
- **Implementação no VitaSharp:** contador `ulong` no módulo.
- **Validação:** —

#### sceLiveAreaGetFrameRevision
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0xD6EE2214
- **Protótipo:** `int sceLiveAreaGetFrameRevision(const char *frame_id, SceUInt64 *revision);`
- **Comportamento:** revisão de um frame (retail tem precedência).
- **Retorno/erros:** 0 se o frame está no dicionário; `frame_id` NULL/vazio ou `revision` NULL → `0x80104002`;
  frame desconhecido → `0x8010400A`.
- **Estruturas:** —
- **Implementação no VitaSharp:** dicionário opcional; sem ele, sempre `0x8010400A`.
- **Validação:** —

#### sceLiveAreaGetFrameUserData
- [ ] **Prioridade:** P2 · **Biblioteca:** SceLiveAreaUtil · **NID:** 0xD6FD367F
- **Protótipo:** `int sceLiveAreaGetFrameUserData(const char *frame_id, char *buffer, SceSize buffer_size);`
- **Comportamento:** devolve a string de user data do frame, até `min(buffer_size, 1024) - 1` bytes + NUL.
- **Retorno/erros:** 0; `frame_id`/`buffer` NULL ou `buffer_size == 0` → `0x80104002`; sem frame → `0x8010400A`.
- **Estruturas:** —
- **Implementação no VitaSharp:** dicionário opcional; sem ele, `0x8010400A`.
- **Validação:** —

### 4.3 SceNotificationUtil

#### sceNotificationUtilSendNotification
- [ ] **Prioridade:** P2 · **Biblioteca:** SceNotificationUtil · **NID:** 0xDE6F33F4
- **Protótipo:** `SceInt32 sceNotificationUtilSendNotification(const SceWChar16 *text);` (`psp2/notificationutil.h`)
- **Comportamento:** mostra uma notificação do sistema com o texto (buffer de 0x410 bytes) e a guarda no
  histórico do app.
- **Retorno/erros:** 0; `text` NULL → `0x80106300` (`SCE_NOTIFICATIONUTIL_ERROR_INTERNAL`, único código conhecido;
  a confirmar).
- **Estruturas:** —
- **Implementação no VitaSharp:** ler UTF-16 até NUL (máx. 0x208 caracteres), `Log.Info($"Notification: {texto}")`;
  opcional: repassar ao front-end via `Emu.Host` como toast.
- **Validação:** homebrew que envie notificação → linha no log.

#### sceNotificationUtilCleanHistory
- [ ] **Prioridade:** P2 · **Biblioteca:** SceNotificationUtil · **NID:** 0xB0FFFB7B
- **Protótipo:** `SceInt32 sceNotificationUtilCleanHistory(void);`
- **Comportamento:** apaga as notificações do app no histórico.
- **Retorno/erros:** 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** `=> 0`.
- **Validação:** —

### 4.4 SceIncomingDialog

Estado: `initialized` (bool) e `status` (`SceIncomingDialogStatus`: 0 NOT_RUNNING, 1 ACCEPTED, 2 RUNNING,
3 REJECTED, 4 CLOSED, 5 BUSY, 6 TIMEOUT). Sem shell, ninguém aceita o diálogo: ao abrir, ele expira na hora.

#### sceIncomingDialogInitialize
- [ ] **Prioridade:** P2 · **Biblioteca:** SceIncomingDialog · **NID:** 0x18AF99EB
- **Protótipo:** `SceInt32 sceIncomingDialogInitialize(int init_type);` (`psp2/incoming_dialog.h`)
- **Comportamento:** inicializa a biblioteca; `init_type` deve ser 1.
- **Retorno/erros:** 0; `init_type != 1` → `0x80106201` (`SCE_INCOMINGDIALOG_ERROR_INVALID_ARG`); já inicializada →
  a confirmar (sugestão: 0).
- **Estruturas:** —
- **Implementação no VitaSharp:** `initialized = true; status = 0`.
- **Validação:** —

#### sceIncomingDialogOpen
- [ ] **Prioridade:** P2 · **Biblioteca:** SceIncomingDialog · **NID:** 0x2BEDC1A0
- **Protótipo:** `SceInt32 sceIncomingDialogOpen(SceIncomingDialogParam *dialogParam);`
- **Comportamento:** mostra diálogo de "chamada recebida" com botões aceitar/recusar por `dialogTimer` segundos.
- **Retorno/erros:** 0; `dialogParam` NULL ou não inicializado → `0x80106201` (a confirmar o segundo caso); já
  aberto → a confirmar (sugestão: status BUSY e retorno 0).
- **Estruturas:** `SceIncomingDialogParam` (0x25C, soma dos campos confere com o assert): +0x00 sdkVersion,
  +0x04 audioPath[0x80] (.mp3/.at9/.m4a, pode ser vazio), +0x84 titleid[0x10] (app aberto ao aceitar),
  +0x94 `unk_BC` (o nome não é o offset), +0x98 dialogTimer (u32, segundos), +0x9C reserved1[0x3E],
  +0xDA buttonRightText (UTF-16[0x1F], "aceitar"), +0x118 separator0, +0x11A buttonLeftText (UTF-16[0x1F],
  "recusar"; vazio = só um botão), +0x158 separator1, +0x15A dialogText (UTF-16[0x80]), +0x25A separator2.
- **Implementação no VitaSharp:** logar o texto; `status = 6` (TIMEOUT). Alternativa mais fiel: `status = 2` e
  mudar para 6 após `dialogTimer` segundos de tempo de processo (`K.ProcessTimeUs`).
- **Validação:** `Open` → `GetStatus` = 6.

#### sceIncomingDialogGetStatus
- [ ] **Prioridade:** P2 · **Biblioteca:** SceIncomingDialog · **NID:** 0x839DE14C
- **Protótipo:** `SceInt32 sceIncomingDialogGetStatus(void);`
- **Comportamento:** devolve o status atual (valor do enum, não erro).
- **Retorno/erros:** `status`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `=> status`.
- **Validação:** —

#### sceIncomingDialogSwitchToDialog
- [ ] **Prioridade:** P2 · **Biblioteca:** SceIncomingDialog · **NID:** 0x0123B83A
- **Protótipo:** `SceInt32 sceIncomingDialogSwitchToDialog(void);`
- **Comportamento:** força a volta à LiveArea mostrando o diálogo.
- **Retorno/erros:** 0 (no-op).
- **Estruturas:** —
- **Implementação no VitaSharp:** `=> 0`.
- **Validação:** —

#### sceIncomingDialogClose
- [ ] **Prioridade:** P2 · **Biblioteca:** SceIncomingDialog · **NID:** 0x126BD15E
- **Protótipo:** `SceInt32 sceIncomingDialogClose(void);`
- **Comportamento:** fecha o diálogo aberto.
- **Retorno/erros:** 0; `status = 4` (CLOSED).
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceIncomingDialogFinish
- [ ] **Prioridade:** P2 · **Biblioteca:** SceIncomingDialog · **NID:** 0x860B1885
- **Protótipo:** `SceInt32 sceIncomingDialogFinish(void);`
- **Comportamento:** finaliza a biblioteca.
- **Retorno/erros:** 0; `initialized = false; status = 0`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

### 4.5 SceClipboard

Sem header no vita-headers. Estado: uma string em memória (opcional: área de transferência do host via `Emu.Host`).

#### sceClipboardSetText
- [ ] **Prioridade:** P2 · **Biblioteca:** SceClipboard · **NID:** 0x43A94D3E
- **Protótipo:** `int sceClipboardSetText(const char *text, SceSize length);` **(inferido)**
- **Comportamento:** grava texto UTF-8 na área de transferência do sistema.
- **Retorno/erros:** 0; `text` NULL → erro (código a confirmar); limite de tamanho a confirmar (desmontar
  `libclipboard.suprx`).
- **Estruturas:** —
- **Implementação no VitaSharp:** guardar `length` bytes (ou até NUL).
- **Validação:** Set seguido de Get devolve o mesmo texto.

#### sceClipboardGetText
- [ ] **Prioridade:** P2 · **Biblioteca:** SceClipboard · **NID:** 0x5BB10CC2
- **Protótipo:** `int sceClipboardGetText(char *text, SceSize length);` **(inferido)**
- **Comportamento:** copia o texto atual para `text` (até `length - 1` + NUL).
- **Retorno/erros:** 0 (ou nº de bytes — a confirmar); vazio → string vazia.
- **Estruturas:** —
- **Implementação no VitaSharp:** `GuestMemory.WriteCString(text, atual, length)`.
- **Validação:** idem.

### 4.6 SceGameUpdate

Sem header e sem implementação no Vita3K. A biblioteca verifica/baixa patch do jogo pela PSN. Comportamento
mínimo: "nenhuma atualização disponível". Protótipos **inferidos**; confirmar desmontando um jogo que a importe.

#### sceGameUpdateInit
- [ ] **Prioridade:** P2 · **Biblioteca:** SceGameUpdate · **NID:** 0x2F401247
- **Protótipo:** `int sceGameUpdateInit(void *param);` **(inferido)**
- **Comportamento:** inicializa a biblioteca.
- **Retorno/erros:** 0.
- **Estruturas:** a confirmar.
- **Implementação no VitaSharp:** `initialized = true`.
- **Validação:** —

#### sceGameUpdateRun
- [ ] **Prioridade:** P2 · **Biblioteca:** SceGameUpdate · **NID:** 0x3C616238
- **Protótipo:** `int sceGameUpdateRun(void *param);` **(inferido)**
- **Comportamento:** executa a verificação de update (no hardware, diálogo do sistema).
- **Retorno/erros:** sem rede → erro de "não conectado" (código a confirmar). Até confirmar, logar e retornar 0
  sem escrever nada; se o jogo esperar resultado por ponteiro, escrever "sem update" após confirmar o layout.
- **Estruturas:** a confirmar.
- **Implementação no VitaSharp:** —
- **Validação:** primeiro jogo que importar.

#### sceGameUpdateAbort
- [ ] **Prioridade:** P2 · **Biblioteca:** SceGameUpdate · **NID:** 0x14116007
- **Protótipo:** `int sceGameUpdateAbort(void);` **(inferido)**
- **Comportamento:** cancela a verificação em curso.
- **Retorno/erros:** 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** `=> 0`.
- **Validação:** —

#### sceGameUpdateTerm
- [ ] **Prioridade:** P2 · **Biblioteca:** SceGameUpdate · **NID:** 0x48D879BA
- **Protótipo:** `int sceGameUpdateTerm(void);` **(inferido)**
- **Comportamento:** finaliza a biblioteca.
- **Retorno/erros:** 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** `initialized = false`.
- **Validação:** —

### 4.7 SceTriggerUtil

Agenda o início automático do app (diário ou uma vez). O header avisa que os dois últimos parâmetros nunca são
usados e que cada app tem no máximo 6 eventos. Estado: dicionário `eventId → (tipo, parâmetros)` em memória
(opcional: persistir em `<FsRoot>/ux0/vitasharp/trigger/<TITLEID>.json`). Erros (`psp2/triggerutil.h`):
`0x80103600` BUSY, `0x80103611` NOT_FOUND_USER, `0x80103614` NOT_FOUND_SYSTEM, `0x80103621` NOT_REGISTERED,
`0x80103624` EVENT_TYPE_MISMATCH, `0x80103660` INVALID_ARG.

#### sceTriggerUtilRegisterDailyEvent
- [ ] **Prioridade:** P2 · **Biblioteca:** SceTriggerUtil · **NID:** 0xA69102DE
- **Protótipo:** `int sceTriggerUtilRegisterDailyEvent(const char *titleid, const SceTriggerUtilEventParamDaily *param, int eventId, int a4, int a5);`
- **Comportamento:** registra evento diário (`repeatDays` = máscara `SCE_TRIGGER_UTIL_SUNDAY..SATURDAY`).
- **Retorno/erros:** 0; `param` NULL ou máscara 0 → `0x80103660`; 7º evento → erro (código a confirmar; sugestão
  `0x80103660`).
- **Estruturas:** `SceTriggerUtilEventParamDaily` (0x50): +0x00 ver, +0x04 extraParam1 (1), +0x06 extraParam2 (0),
  +0x08 triggerTime (POSIX), +0x0C repeatDays (u16), +0x0E reserved.
- **Implementação no VitaSharp:** copiar os 0x50 bytes para o dicionário.
- **Validação:** Register → GetDailyEventInfo devolve os mesmos bytes.

#### sceTriggerUtilRegisterOneTimeEvent
- [ ] **Prioridade:** P2 · **Biblioteca:** SceTriggerUtil · **NID:** 0x04218D35
- **Protótipo:** `int sceTriggerUtilRegisterOneTimeEvent(const char *titleid, const SceTriggerUtilEventParamOneTime *param, int eventId, int a4, int a5);`
- **Comportamento:** registra evento único.
- **Retorno/erros:** 0; `param` NULL → `0x80103660`.
- **Estruturas:** `SceTriggerUtilEventParamOneTime` (0x58; o comentário diz 0x54 mas o assert diz 0x58): +0x00 ver,
  +0x08 triggerTime (`SceRtcTick` UTC, alinhado a 8), +0x10 extraParam1 (1), +0x11 extraParam2 (0), reserved.
- **Implementação no VitaSharp:** idem.
- **Validação:** Register → GetOneTimeEventInfo.

#### sceTriggerUtilUnregisterDailyEvent
- [ ] **Prioridade:** P2 · **Biblioteca:** SceTriggerUtil · **NID:** 0x58BF700F
- **Protótipo:** `int sceTriggerUtilUnregisterDailyEvent(int eventId, int a2, int a3);`
- **Comportamento:** remove evento diário.
- **Retorno/erros:** 0; inexistente → `0x80103621`; é one-time → `0x80103624`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceTriggerUtilUnregisterOneTimeEvent
- [ ] **Prioridade:** P2 · **Biblioteca:** SceTriggerUtil · **NID:** 0xAF73E0A8
- **Protótipo:** `int sceTriggerUtilUnregisterOneTimeEvent(int eventId, int a2, int a3);`
- **Comportamento:** remove evento único.
- **Retorno/erros:** 0; `0x80103621`; `0x80103624`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceTriggerUtilGetDailyEventInfo
- [ ] **Prioridade:** P2 · **Biblioteca:** SceTriggerUtil · **NID:** 0xDF3ED08E
- **Protótipo:** `int sceTriggerUtilGetDailyEventInfo(int eventId, SceTriggerUtilEventParamDaily *param, int a5, int a6);`
- **Comportamento:** devolve os parâmetros registrados.
- **Retorno/erros:** 0; `0x80103621`; `0x80103624`; `param` NULL → `0x80103660`.
- **Estruturas:** ver Register.
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceTriggerUtilGetOneTimeEventInfo
- [ ] **Prioridade:** P2 · **Biblioteca:** SceTriggerUtil · **NID:** 0x912434E0
- **Protótipo:** `int sceTriggerUtilGetOneTimeEventInfo(int eventId, SceRtcTick *triggerTime, int a4, int a5);`
- **Comportamento:** devolve o instante do evento único.
- **Retorno/erros:** 0; `0x80103621`; `0x80103624`; ponteiro NULL → `0x80103660`.
- **Estruturas:** u64.
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceTriggerUtilGetAutoStartStatus
- [ ] **Prioridade:** P2 · **Biblioteca:** SceTriggerUtil · **NID:** 0xDD2CA8BB
- **Protótipo:** `int sceTriggerUtilGetAutoStartStatus(int *status, int a2, int a3);`
- **Comportamento:** informa se o app foi iniciado por um trigger (semântica exata do valor a confirmar).
- **Retorno/erros:** 0 com `*status = 0` (não foi auto-iniciado); NULL → `0x80103660`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceTriggerUtilGetUserAppInfo
- [ ] **Prioridade:** P2 · **Biblioteca:** SceTriggerUtil · **NID:** 0x0ACE6552
- **Protótipo:** `int sceTriggerUtilGetUserAppInfo(const char *titleid, SceTriggerUtilUserAppInfo *appInfo, int a4, int a5);`
- **Comportamento:** nome e ícone de um app de usuário instalado.
- **Retorno/erros:** 0 se `titleid == Emu.TitleId` (ou existe `ux0:app/<titleid>`); senão `0x80103611`.
- **Estruturas:** `SceTriggerUtilUserAppInfo` (0x46A): +0x000 name (UTF-16[0x34]), +0x068 iconPath[0x400],
  +0x468 unk.
- **Implementação no VitaSharp:** nome de `Emu.ParamSfo.Title` (ou do `param.sfo` do outro app), ícone
  `"ux0:app/<titleid>/sce_sys/icon0.png"` (formato do caminho a confirmar).
- **Validação:** —

#### sceTriggerUtilGetSystemAppInfo
- [ ] **Prioridade:** P2 · **Biblioteca:** SceTriggerUtil · **NID:** 0xD659CCB0
- **Protótipo:** `int sceTriggerUtilGetSystemAppInfo(const char *titleid, SceTriggerUtilSystemAppInfo *appInfo, int a4, int a5);`
- **Comportamento:** idem para apps de sistema (NPXS...).
- **Retorno/erros:** sempre `0x80103614` (não há apps de sistema).
- **Estruturas:** `SceTriggerUtilSystemAppInfo` (0x602).
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceTriggerUtilGetRegisteredUserTitleIdList
- [ ] **Prioridade:** P2 · **Biblioteca:** SceTriggerUtil · **NID:** 0xE2B2D2D0
- **Protótipo:** `int sceTriggerUtilGetRegisteredUserTitleIdList(char *titleIdBuffer, int numOfIds);`
- **Comportamento:** lista títulos de usuário com eventos registrados.
- **Retorno/erros:** nº de IDs escritos (a confirmar se é retorno ou só sucesso); com eventos do jogo atual, escrever
  `Emu.TitleId` (10 bytes por entrada? largura a confirmar); sem eventos → 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceTriggerUtilGetRegisteredSystemTitleIdList
- [ ] **Prioridade:** P2 · **Biblioteca:** SceTriggerUtil · **NID:** 0x2A67EF8C
- **Protótipo:** `int sceTriggerUtilGetRegisteredSystemTitleIdList(char *buffer, int numOfIds);`
- **Comportamento:** idem para apps de sistema.
- **Retorno/erros:** 0 (lista vazia).
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

### 4.8 ScePhotoExport, SceVideoExport, SceMusicExport

Exportam um arquivo do jogo para a biblioteca de mídia do sistema. HLE: copiar para o dispositivo de mídia
correspondente já montado pelo `Vfs` (`photo0:`, `video0:`, `music0:` em `<FsRoot>/photo0` etc.) e devolver o
caminho de destino. Subpastas reais do sistema (ex.: `photo0:/<TITLEID>/` ou `ux0:picture/...`) a confirmar num
console; até lá usar `<dispositivo>:/<TITLEID>/<nome original>`, acrescentando sufixo numérico se já existir.
`cancelCb`/`progress` podem ser ignorados (operação síncrona instantânea); chamar `progress(user, 100)` só se for
simples chamar guest a partir do HLE (`GuestThread.Current!.CallGuest`). Códigos de erro não documentados: usar
`SceError.INVALID_ARGUMENT` (0x80020003) para NULL e `0x80010002` (ENOENT) para arquivo de origem inexistente até
confirmar.

#### scePhotoExportFromData
- [ ] **Prioridade:** P2 · **Biblioteca:** ScePhotoExport · **NID:** 0x70512321
- **Protótipo:** `int scePhotoExportFromData(const void *data, SceSize size, const PhotoExportParam *param, void *workingMemory, void *cancelCb, void *user, char *outPath, SceSize outPathSize);` (`psp2/photoexport.h`)
- **Comportamento:** grava a imagem (JPEG/PNG em memória) como nova foto.
- **Retorno/erros:** 0; `data`/`param` NULL → erro; formato não suportado → erro (a confirmar).
- **Estruturas:** `PhotoExportParam` (0x30): +0x00 version, +0x04 photoTitle (`SceWChar32*`), +0x08 gameTitle,
  +0x0C gameComment, +0x10 reserved[8].
- **Implementação no VitaSharp:** detectar extensão pelos magic bytes (`FF D8` → .jpg, `89 50 4E 47` → .png),
  gravar em `photo0:/<TITLEID>/<timestamp>.<ext>`, escrever caminho Vita em `outPath` (até `outPathSize`).
- **Validação:** arquivo aparece em `<FsRoot>/photo0`.

#### scePhotoExportFromFile
- [ ] **Prioridade:** P2 · **Biblioteca:** ScePhotoExport · **NID:** 0x84FD9FC5
- **Protótipo:** `int scePhotoExportFromFile(const char *path, const PhotoExportParam *param, void *workingMemory, void *cancelCb, void *user, char *outPath, SceSize outPathSize);`
- **Comportamento:** idem a partir de um arquivo Vita.
- **Retorno/erros:** 0; origem inexistente → erro.
- **Estruturas:** idem.
- **Implementação no VitaSharp:** `Emu.Vfs.Resolve(path)` + `File.Copy`.
- **Validação:** idem.

#### scePhotoExportIsAvailableFromData
- [ ] **Prioridade:** P2 · **Biblioteca:** ScePhotoExport · **NID:** 0xE6E6AFD5
- **Protótipo:** `int scePhotoExportIsAvailableFromData(const void *data, SceSize size);` **(inferido)**
- **Comportamento:** verifica se os dados podem ser exportados (formato suportado/espaço).
- **Retorno/erros:** 0 se JPEG/PNG reconhecido (ou 1 = disponível — a confirmar a convenção de retorno), erro
  caso contrário.
- **Estruturas:** —
- **Implementação no VitaSharp:** mesma detecção de magic.
- **Validação:** —

#### scePhotoExportIsAvailableFromFile
- [ ] **Prioridade:** P2 · **Biblioteca:** ScePhotoExport · **NID:** 0x5655F971
- **Protótipo:** `int scePhotoExportIsAvailableFromFile(const char *path);` **(inferido)**
- **Comportamento:** idem para arquivo.
- **Retorno/erros:** como acima; inexistente → erro.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceVideoExportFromFile
- [ ] **Prioridade:** P2 · **Biblioteca:** SceVideoExport · **NID:** 0x4F60A3C8
- **Protótipo:** `int sceVideoExportFromFile(const VideoExportInputParam *in_param, int unk, void *workingMemory, void *cancelCb, void (*progress)(void*, int), void *user, int unk2, VideoExportOutputParam *out_param);` (`psp2/videoexport.h`)
- **Comportamento:** copia o vídeo (MP4) para a biblioteca de vídeos.
- **Retorno/erros:** 0; `in_param`/`out_param` NULL → erro; origem inexistente → erro.
- **Estruturas:** `VideoExportInputParam` (0x440): path[1024] + reserved[64]; `VideoExportOutputParam` (0x408):
  path[1024] + reserved[8].
- **Implementação no VitaSharp:** copiar para `video0:/<TITLEID>/`; escrever caminho em `out_param->path`.
- **Validação:** arquivo em `<FsRoot>/video0`.

#### sceMusicExportFromFile
- [ ] **Prioridade:** P2 · **Biblioteca:** SceMusicExport · **NID:** 0x08000579
- **Protótipo:** `int sceMusicExportFromFile(const char *path, const MusicExportParam *param, void *workingMemory, void *cancelCb, void (*progress)(void*, int), void *user, char *outPath, SceSize outPathSize);` (`psp2/musicexport.h`)
- **Comportamento:** copia a música para a biblioteca de músicas.
- **Retorno/erros:** 0; origem inexistente → erro.
- **Estruturas:** `MusicExportParam` (0x80, reservado).
- **Implementação no VitaSharp:** copiar para `music0:/<TITLEID>/`; escrever `outPath`.
- **Validação:** arquivo em `<FsRoot>/music0`.

---

## 5. Localização e proximidade (HLE, "serviço indisponível")

Implementado em `src/VitaSharp.Core/Hle/Modules/Location.cs` (`Location` = SceLibLocation, `Gps` = SceGps) e
`src/VitaSharp.Core/Hle/Modules/Near.cs` (`NearUtil` = SceNearUtil + SceNearDialogUtil). Testes:
`tests/VitaSharp.Tests/LocationPerfHleTests.cs`. Os timestamps gravados usam o mesmo relógio de `sceRtcGetCurrentTick`
(`Rtc.NowTick`).

### 5.1 SceLibLocation

Modelo coerente escolhido: o sistema tem localização ligada, mas **o app não tem permissão** (estado que um usuário
real pode produzir negando o diálogo). Assim:

- `sceLocationGetPermission` → `parentalstatus = ALLOW(1)`, `mainstatus = ALLOW(1)`,
  `applicationstatus = DENY(2)`.
- Diálogo de confirmação termina na hora com `SCE_LOCATION_DIALOG_RESULT_DISABLE(1)`.
- Medições (`GetLocation`, `GetHeading`, callbacks) falham com `SCE_LOCATION_ERROR_DISABLE_APPLICATION`
  (`0x8010120D`). Código real neste estado a confirmar num console (alternativa plausível:
  `SCE_LOCATION_INFO_DENIED_BY_USER` `0x80101203`).
- Handles: inteiros a partir de 1 num dicionário `handle → (lmethod, hmethod, confirmStatus, confirmResult)`.
  Limite de handles (`0x80101207`) a confirmar; não impor limite até lá.

Erros usados (de `psp2/location.h`): `0x80101204` INVALID_ADDRESS (ponteiro NULL), `0x80101205` INVALID_HANDLE,
`0x80101208` INVALID_LOCATION_METHOD (> 5), `0x80101209` INVALID_HEADING_METHOD (> 4), `0x8010120B`
NOT_RUNNING_CALLBACK, `0x8010120C` DIALOG_RESULT_NONE, `0x8010120D` DISABLE_APPLICATION, `0x8010120E`
MULTIPLE_CONFIRM.

#### sceLocationOpen
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0xDD271661
- **Protótipo:** `SceInt32 sceLocationOpen(SceLocationHandle *handle, SceLocationLocationMethod locateMethod, SceLocationHeadingMethod headingMethod);` (`psp2/location.h`)
- **Comportamento:** cria um handle com os métodos de medição.
- **Retorno/erros:** 0; `handle` NULL → `0x80101204`; método inválido → `0x80101208`/`0x80101209`.
- **Estruturas:** `SceLocationHandle` = u32.
- **Implementação no VitaSharp:** alocar handle, gravar em `*handle`.
- **Validação:** homebrew de localização: Open → GetLocation retorna `0x8010120D`.

#### sceLocationClose
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x14FE76E8
- **Protótipo:** `SceInt32 sceLocationClose(SceLocationHandle handle);`
- **Comportamento:** fecha o handle e para callbacks.
- **Retorno/erros:** 0; desconhecido → `0x80101205`.
- **Estruturas:** —
- **Implementação no VitaSharp:** remover do dicionário.
- **Validação:** Close duplo → `0x80101205`.

#### sceLocationReopen
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0xB1F55065
- **Protótipo:** `SceInt32 sceLocationReopen(SceLocationHandle handle, SceLocationLocationMethod locateMethod, SceLocationHeadingMethod headingMethod);`
- **Comportamento:** troca os métodos de um handle aberto.
- **Retorno/erros:** 0; `0x80101205`; `0x80101208`; `0x80101209`.
- **Estruturas:** —
- **Implementação no VitaSharp:** atualizar a entrada.
- **Validação:** Reopen → GetMethod.

#### sceLocationGetMethod
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x188CE004
- **Protótipo:** `SceInt32 sceLocationGetMethod(SceLocationHandle handle, SceLocationLocationMethod *locateMethod, SceLocationHeadingMethod *headingMethod);`
- **Comportamento:** devolve os métodos do handle.
- **Retorno/erros:** 0; `0x80101205`; ponteiro NULL → `0x80101204`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceLocationGetLocation
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x15BC27C8
- **Protótipo:** `SceInt32 sceLocationGetLocation(SceLocationHandle handle, SceLocationLocationInfo *locationInfo);`
- **Comportamento:** medição bloqueante da posição.
- **Retorno/erros:** `0x8010120D`; `0x80101205`; `0x80101204`. Antes de retornar, preencher `locationInfo` com
  `SCE_LOCATION_DATA_INVALID` (-9999.0) em latitude/longitude/altitude/accuracy/direction/speed e timestamp atual
  (comportamento do preenchimento em erro a confirmar; inofensivo).
- **Estruturas:** `SceLocationLocationInfo` (0x30): +0x00 latitude (double), +0x08 longitude, +0x10 altitude,
  +0x18 accuracy (float), +0x1C reserve, +0x20 direction, +0x24 speed, +0x28 timestamp (`SceRtcTick` µs UTC).
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceLocationGetLocationWithTimeout
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x16F41ED0
- **Protótipo:** `SceInt32 sceLocationGetLocationWithTimeout(SceLocationHandle handle, SceLocationLocationInfo *locationInfo, SceUInt64 timeout);` **(inferido; tipo/unidade do timeout a confirmar)**
- **Comportamento:** como GetLocation com tempo limite.
- **Retorno/erros:** igual a GetLocation (`0x8010120D`); nunca espera.
- **Estruturas:** idem.
- **Implementação no VitaSharp:** reutilizar GetLocation; ignorar o timeout.
- **Validação:** —

#### sceLocationCancelGetLocation
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x71503251
- **Protótipo:** `SceInt32 sceLocationCancelGetLocation(SceLocationHandle handle);`
- **Comportamento:** cancela um GetLocation em espera (que retornaria `0x80101202`).
- **Retorno/erros:** 0; `0x80101205`.
- **Estruturas:** —
- **Implementação no VitaSharp:** no-op (não há espera).
- **Validação:** —

#### sceLocationStartLocationCallback
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x12D1F0EA
- **Protótipo:** `SceInt32 sceLocationStartLocationCallback(SceLocationHandle handle, SceUInt32 distance, SceLocationLocationInfoCallback callback, void *userdata);`
- **Comportamento:** chama `callback` a cada deslocamento de `distance` metros.
- **Retorno/erros:** `0x8010120D` (sem permissão; nenhum callback registrado); `0x80101205`; `callback` NULL →
  `0x80101204`.
- **Estruturas:** `void (*)(SceInt32 result, SceLocationHandle, const SceLocationLocationInfo*, void *userdata)`.
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceLocationStopLocationCallback
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0xED378700
- **Protótipo:** `SceInt32 sceLocationStopLocationCallback(SceLocationHandle handle);`
- **Comportamento:** para o callback de posição.
- **Retorno/erros:** como nunca há callback ativo → `0x8010120B`; `0x80101205`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceLocationGetHeading
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x4E9E5ED9
- **Protótipo:** `SceInt32 sceLocationGetHeading(SceLocationHandle handle, SceLocationHeadingInfo *headingInfo);`
- **Comportamento:** direção (bússola).
- **Retorno/erros:** `0x8010120D`; `0x80101205`; `0x80101204`.
- **Estruturas:** `SceLocationHeadingInfo` (0x20): +0x00 trueHeading, +0x04..+0x0C headingVectorX/Y/Z,
  +0x10 reserve, +0x14 reserve2, +0x18 timestamp (u64).
- **Implementação no VitaSharp:** preencher -9999.0 como em GetLocation.
- **Validação:** —

#### sceLocationStartHeadingCallback
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x07D4DFE0
- **Protótipo:** `SceInt32 sceLocationStartHeadingCallback(SceLocationHandle handle, SceUInt32 difference, SceLocationHeadingInfoCallback callback, void *userdata);`
- **Comportamento:** callback de mudança de direção.
- **Retorno/erros:** `0x8010120D`; `0x80101205`; `0x80101204`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceLocationStopHeadingCallback
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x92E53F94
- **Protótipo:** `SceInt32 sceLocationStopHeadingCallback(SceLocationHandle handle);`
- **Comportamento:** para o callback de direção.
- **Retorno/erros:** `0x8010120B`; `0x80101205`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceLocationConfirm
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0xC895E567
- **Protótipo:** `SceInt32 sceLocationConfirm(SceLocationHandle handle);`
- **Comportamento:** abre o diálogo de permissão de localização.
- **Retorno/erros:** 0; `0x80101205`; diálogo já rodando → `0x8010120E` (nunca ocorre aqui).
- **Estruturas:** —
- **Implementação no VitaSharp:** `confirmStatus = FINISHED(2)`, `confirmResult = DISABLE(1)`.
- **Validação:** Confirm → ConfirmGetStatus = 2 → ConfirmGetResult = 1.

#### sceLocationConfirmGetStatus
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x730FF842
- **Protótipo:** `SceInt32 sceLocationConfirmGetStatus(SceLocationHandle handle, SceLocationDialogStatus *status);`
- **Comportamento:** estado do diálogo (0 IDLE, 1 RUNNING, 2 FINISHED).
- **Retorno/erros:** 0; `0x80101205`; `0x80101204`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceLocationConfirmGetResult
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0xFF016C13
- **Protótipo:** `SceInt32 sceLocationConfirmGetResult(SceLocationHandle handle, SceLocationDialogResult *result);`
- **Comportamento:** resultado do diálogo (0 NONE, 1 DISABLE, 2 ENABLE).
- **Retorno/erros:** 0; sem diálogo concluído → `0x8010120C`; `0x80101205`; `0x80101204`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceLocationConfirmAbort
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0xE3CBF875
- **Protótipo:** `SceInt32 sceLocationConfirmAbort(SceLocationHandle handle);`
- **Comportamento:** aborta o diálogo em curso.
- **Retorno/erros:** 0; `0x80101205`.
- **Estruturas:** —
- **Implementação no VitaSharp:** no-op (já terminou).
- **Validação:** —

#### sceLocationGetPermission
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x482622C6
- **Protótipo:** `SceInt32 sceLocationGetPermission(SceLocationHandle handle, SceLocationPermissionInfo *info);`
- **Comportamento:** estado de permissão.
- **Retorno/erros:** 0; `0x80101205`; `0x80101204`.
- **Estruturas:** `SceLocationPermissionInfo` (0x14): +0x00 parentalstatus = 1, +0x04 mainstatus = 1,
  +0x08 applicationstatus = 2, +0x0C/+0x10 = 0.
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceLocationDenyApplication
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x8AAF3FBD
- **Protótipo:** `SceInt32 sceLocationDenyApplication(void);` **(inferido)**
- **Comportamento:** marca o app como sem permissão.
- **Retorno/erros:** 0 (estado já é DENY).
- **Estruturas:** —
- **Implementação no VitaSharp:** `=> 0`.
- **Validação:** —

#### sceLocationSetGpsEmulationFile
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0xDE0A9EA4
- **Protótipo:** `SceInt32 sceLocationSetGpsEmulationFile(unsigned char *filename);`
- **Comportamento:** (devkit) usa arquivo de trilha GPS simulada.
- **Retorno/erros:** 0 e logar o nome (não usar o arquivo).
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### sceLocationInit
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x09C4F674
- **Protótipo:** `SceInt32 sceLocationInit(void);` **(inferido)**
- **Comportamento:** inicialização interna da biblioteca (normalmente chamada pelo `module_start`/sistema).
- **Retorno/erros:** 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** `=> 0`.
- **Validação:** —

#### sceLocationTerm
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0x1E80199A
- **Protótipo:** `SceInt32 sceLocationTerm(void);` **(inferido)**
- **Comportamento:** finalização interna; fecha handles.
- **Retorno/erros:** 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** limpar o dicionário.
- **Validação:** —

#### sceLocationSetThreadParameter
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibLocation · **NID:** 0xAA026B53
- **Protótipo:** `SceInt32 sceLocationSetThreadParameter(const void *threadParam);` **(inferido; struct com prioridade/afinidade da thread interna, a confirmar)**
- **Comportamento:** configura a thread interna da biblioteca.
- **Retorno/erros:** 0.
- **Estruturas:** a confirmar.
- **Implementação no VitaSharp:** `=> 0`.
- **Validação:** —

### 5.2 SceGps

Interface de baixo nível do receptor GPS (só modelos 3G). O VitaSharp emula um modelo Wi-Fi: **sem dispositivo**.
Structs (`SceGpsDeviceInfo`, `SceGpsPositionData`, `SceGpsSatelliteData`, `SceGpsStatus`) não têm layout no
vita-headers ("missing structs") — não escrever nelas. Erro "sem dispositivo": usar `0x80010013`
(`SCE_ERROR_ERRNO_ENODEV`) até confirmar o código real (desmontar `SceGps` user stub ou testar num PS Vita Wi-Fi).

#### _sceGpsIsDevice
- [x] **Prioridade:** P2 · **Biblioteca:** SceGps · **NID:** 0x0EE1BC7B
- **Protótipo:** `int _sceGpsIsDevice(void);` (`psp2/gps.h`)
- **Comportamento:** informa se há receptor GPS.
- **Retorno/erros:** 0 (não há). Convenção (bool vs. erro) a confirmar.
- **Estruturas:** —
- **Implementação no VitaSharp:** `=> 0`.
- **Validação:** —

#### _sceGpsOpen
- [x] **Prioridade:** P2 · **Biblioteca:** SceGps · **NID:** 0x6EAC2C6D
- **Protótipo:** `int _sceGpsOpen(SceUID cbid);`
- **Comportamento:** abre o dispositivo, registrando callback de eventos.
- **Retorno/erros:** `0x80010013` (sem dispositivo).
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### _sceGpsClose
- [x] **Prioridade:** P2 · **Biblioteca:** SceGps · **NID:** 0x73EE0ED1
- **Protótipo:** `int _sceGpsClose(void);`
- **Comportamento:** fecha o dispositivo.
- **Retorno/erros:** `0x80010013`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### _sceGpsStart
- [x] **Prioridade:** P2 · **Biblioteca:** SceGps · **NID:** 0x2914F243
- **Protótipo:** `int _sceGpsStart(unsigned int mode);`
- **Comportamento:** inicia aquisição.
- **Retorno/erros:** `0x80010013`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### _sceGpsStop
- [x] **Prioridade:** P2 · **Biblioteca:** SceGps · **NID:** 0xE1ABB52D
- **Protótipo:** `int _sceGpsStop(void);`
- **Comportamento:** para aquisição.
- **Retorno/erros:** `0x80010013`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### _sceGpsGetData
- [x] **Prioridade:** P2 · **Biblioteca:** SceGps · **NID:** 0x9BAF9579
- **Protótipo:** `int _sceGpsGetData(SceGpsPositionData *pos, SceGpsSatelliteData *sat);`
- **Comportamento:** lê posição e satélites.
- **Retorno/erros:** `0x80010013`.
- **Estruturas:** layouts desconhecidos.
- **Implementação no VitaSharp:** —
- **Validação:** —

#### _sceGpsGetDeviceInfo
- [x] **Prioridade:** P2 · **Biblioteca:** SceGps · **NID:** 0x7F1DFADB
- **Protótipo:** `int _sceGpsGetDeviceInfo(SceGpsDeviceInfo *dev_info);`
- **Comportamento:** informações do receptor.
- **Retorno/erros:** `0x80010013`.
- **Estruturas:** desconhecida.
- **Implementação no VitaSharp:** —
- **Validação:** —

#### _sceGpsGetState
- [x] **Prioridade:** P2 · **Biblioteca:** SceGps · **NID:** 0xB8001499
- **Protótipo:** `int _sceGpsGetState(SceGpsStatus *state);`
- **Comportamento:** estado do receptor.
- **Retorno/erros:** `0x80010013`.
- **Estruturas:** desconhecida.
- **Implementação no VitaSharp:** —
- **Validação:** —

#### _sceGpsIoctl
- [x] **Prioridade:** P2 · **Biblioteca:** SceGps · **NID:** 0xB11E674D
- **Protótipo:** `int _sceGpsIoctl(SceUInt32 ioctl_command, SceVoid *arg, SceSize arg_size, SceSize *a4);`
- **Comportamento:** comando específico do driver.
- **Retorno/erros:** `0x80010013`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### _sceGpsSelectDevice
- [x] **Prioridade:** P2 · **Biblioteca:** SceGps · **NID:** 0x2D4D8788
- **Protótipo:** `int _sceGpsSelectDevice(SceUInt32 device_type);`
- **Comportamento:** escolhe o dispositivo (interno/externo).
- **Retorno/erros:** `0x80010013`.
- **Estruturas:** —
- **Implementação no VitaSharp:** —
- **Validação:** —

#### _sceGpsResumeCallback
- [x] **Prioridade:** P2 · **Biblioteca:** SceGps · **NID:** 0x0A4882B3
- **Protótipo:** `int _sceGpsResumeCallback(void);`
- **Comportamento:** usado pelo sistema após suspensão.
- **Retorno/erros:** 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** `=> 0`.
- **Validação:** —

### 5.3 SceNearUtil e SceNearDialogUtil

"Near" troca presentes/descobertas entre consoles próximos via o app de sistema Near (descontinuado). Não há
header no vita-headers nem implementação no Vita3K; os protótipos abaixo são **inferidos** só na forma
(`int f(...)`): a implementação mínima não lê argumentos, então os layouts não são necessários.

#### Seção comum (família `sceNear*` / `sceNearUtility*`)
- [x] **Prioridade:** P2 · **Biblioteca:** SceNearUtil / SceNearDialogUtil · **NID:** ver tabela
- **Protótipo:** não público; `int f(...)` **(inferido)**. `sceNearUtility*` (SceNearDialogUtil) são as mesmas
  operações de `sceNear*` (SceNearUtil) com nome alternativo (mesma lista, 1 para 1, mais as 5 funções de diálogo).
- **Comportamento:** serviço Near indisponível. `sceNearInitialize`/`sceNearUtilityInitialize` falham; todas as
  outras retornam "não inicializado"; `Finalize` retorna 0. Isso faz o jogo esconder a funcionalidade, como num
  console sem conta/sem o app Near.
- **Retorno/erros:** códigos Near não documentados (a confirmar: desmontar `libSceNearUtil.suprx` e procurar
  constantes `0x80xxxxxx` nos retornos de `sceNearInitialize`). Até lá: Initialize → `0x80020405`
  (`SCE_COMMON_DIALOG_ERROR_NOT_SUPPORTED`, reaproveitado por ser genérico e conhecido pelos jogos) e demais →
  `0x80020404` (`NOT_RUNNING`). Trocar pelos códigos reais quando confirmados.
- **Estruturas:** desconhecidas; não escrever em ponteiros de saída.
- **Implementação no VitaSharp:** `Location.cs`, um método por NID com `[Hle]`; dá para usar dois helpers
  `NotSupported()`/`NotInit()`.
- **Validação:** primeiro jogo que importar: verificar que o menu "Near"/"presentes" some sem travar.

Diálogo Near (`sceNearDialog*`): segue o padrão de common dialog do VitaSharp (`Hle/Modules/Online.cs`,
`sceNetCheckDialog*`): `Init(param)` → 0; `GetStatus()` → 2 (`SCE_COMMON_DIALOG_STATUS_FINISHED`);
`GetResult(result)` → escreve só `result = 2` (`SCE_COMMON_DIALOG_RESULT_ABORTED`) no primeiro campo (o tamanho
0x2C da struct não está confirmado, então o resto não é zerado para não arriscar sobrescrever a pilha do jogo);
`Abort` → 0; `Term` → 0 e volta ao estado NONE. `GetStatus` sem `Init` → 0 (NONE); `GetResult` sem `Init` →
`0x80020404`.

#### NIDs SceNearUtil de SDK antigo (Uncharted: Golden Abyss, PCSA00029)
- [x] **Prioridade:** P2 · **Biblioteca:** SceNearUtil · **NID:** 0x1AA394BA 0x1AE3EC1C 0x49A97D5F 0x69EE6FB3
  0x76C4807C 0x8095FDEF 0x88540CEC 0x9B3F2DCE 0x9FB9277B 0xA412E9CA 0xBF2BBE1F 0xCA742B97 0xE608000B
- **Origem:** o `uncharted.self` (título de lançamento) importa 13 NIDs de SceNearUtil que não existem no db do
  vita-headers nem no Vita3K (nenhum coincide com a tabela acima: são NIDs de um SDK pré-lançamento).
- **Análise dos call sites** (`0x81282084`..`0x812838C0` do `uncharted.self`; o jogo carrega
  `SCE_SYSMODULE_NEAR_UTIL` 0x2B antes):
  - `0xA412E9CA(work, {buf, 0x40000}*, 0)`: o jogo aloca 256 KiB, chama, e se o retorno ≠ 0 libera o buffer e grava
    o estado "falhou" (que permite nova tentativa a partir do menu) → é o `Initialize` antigo.
  - `0xE608000B(work)`: mesmo ponteiro de trabalho, chamado no desligamento antes de liberar o buffer → `Finalize`.
  - `0x69EE6FB3(&n, &ptr)`: o valor de retorno é usado diretamente como contagem sem sinal de um laço (chamada duas
    vezes, a segunda com `&contagem`). Um código de erro negativo seria lido como ~2 bilhões de entradas → **não pode
    retornar erro**. `0x49A97D5F` é testado com `<= 0 → falha`. Os demais (`1AA394BA`, `1AE3EC1C`, `76C4807C`,
    `8095FDEF`, `88540CEC`, `9B3F2DCE`, `9FB9277B`, `BF2BBE1F`, `CA742B97`) leem/consultam entradas desses laços.
  - A thread de trabalho Near do jogo chama essas funções mesmo depois de o `Initialize` falhar (não testa o
    estado), então o comportamento seguro é "nada encontrado".
- **Comportamento:** `0xA412E9CA` → `0x80020405` (indisponível, como `sceNearInitialize`); `0xE608000B` → 0;
  os outros 11 → 0 sem escrever em ponteiro nenhum (contagem 0 / nada). Registrados com NID explícito em `Near.cs`
  com nomes `sceNearUtil_<NID>`.

| NID | Biblioteca | Função | Comportamento mínimo |
| --- | --- | --- | --- |
| 0xBBCA5813 | SceNearUtil | `sceNearInitialize` | `0x80020405` (indisponível) |
| 0xFF3BC581 | SceNearUtil | `sceNearFinalize` | 0 |
| 0x3F3F6D92 | SceNearUtil | `sceNearFinalizeAndLaunchNearApp` | 0; logar (não há app Near para abrir) |
| 0x2ED36EE2 | SceNearUtil | `sceNearRefresh` | `0x80020404` |
| 0xAEF3022A | SceNearUtil | `sceNearGetMyStatus` | `0x80020404` |
| 0xAD264F5F | SceNearUtil | `sceNearGetNeighbors` | `0x80020404` |
| 0x6C2E610F | SceNearUtil | `sceNearGetNewNeighbors` | `0x80020404` |
| 0xE75A5A9B | SceNearUtil | `sceNearGetRecentNeighbors` | `0x80020404` |
| 0xD393578E | SceNearUtil | `sceNearGetLastNeighborFoundDateTime` | `0x80020404` |
| 0x146BE236 | SceNearUtil | `sceNearSetGift` | `0x80020404` |
| 0x4CCE6A14 | SceNearUtil | `sceNearSetGift2` | `0x80020404` |
| 0xDB8BAC35 | SceNearUtil | `sceNearGetGift` | `0x80020404` |
| 0x8F3CC01C | SceNearUtil | `sceNearGetGiftStatus` | `0x80020404` |
| 0x52021026 | SceNearUtil | `sceNearDeleteGift` | `0x80020404` |
| 0xDE6F0859 | SceNearUtil | `sceNearGetDiscoveredGifts` | `0x80020404` |
| 0x773ABEA9 | SceNearUtil | `sceNearGetDiscoveredGiftInfo` | `0x80020404` |
| 0xEFE6F060 | SceNearUtil | `sceNearGetDiscoveredGiftSender` | `0x80020404` |
| 0xA64D9D38 | SceNearUtil | `sceNearGetDiscoveredGiftStatus` | `0x80020404` |
| 0x80D6C552 | SceNearUtil | `sceNearConvertDiscoveredGiftParam` | `0x80020404` |
| 0x8CBEB2DA | SceNearUtil | `sceNearDeleteDiscoveredGift` | `0x80020404` |
| 0x360D4A19 | SceNearUtil | `sceNearIgnoreDiscoveredGift` | `0x80020404` |
| 0xF8C15008 | SceNearUtil | `sceNearOpenDiscoveredGiftImage` | `0x80020404` |
| 0x2364D6BD | SceNearUtil | `sceNearReadDiscoveredGiftImage` | `0x80020404` |
| 0x88A68D20 | SceNearUtil | `sceNearCloseDiscoveredGiftImage` | `0x80020404` |
| 0x06807CA3 | SceNearUtil | `sceNearOpenReceivedGiftData` | `0x80020404` |
| 0xB14145DD | SceNearUtil | `sceNearReadReceivedGiftData` | `0x80020404` |
| 0x2F35C196 | SceNearUtil | `sceNearCloseReceivedGiftData` | `0x80020404` |
| 0xB22CB01D | SceNearUtil | `sceNearLaunchNearAppForDownload` | `0x80020404`; logar |
| 0xF3398774 | SceNearUtil | `sceNearLaunchNearAppForUpdate` | `0x80020404`; logar |
| 0xE493D3B8 | SceNearDialogUtil | `sceNearDialogInit` | 0 (padrão common dialog acima) |
| 0x1341672D | SceNearDialogUtil | `sceNearDialogGetStatus` | 2 FINISHED (0 sem Init) |
| 0xD46E5257 | SceNearDialogUtil | `sceNearDialogGetResult` | 0, result = 2 ABORTED |
| 0x7790BDB4 | SceNearDialogUtil | `sceNearDialogAbort` | 0 |
| 0xA3D52935 | SceNearDialogUtil | `sceNearDialogTerm` | 0 |
| 0xECA38E35 | SceNearDialogUtil | `sceNearUtilityInitialize` | `0x80020405` |
| 0x52BBF4C5 | SceNearDialogUtil | `sceNearUtilityFinalize` | 0 |
| 0x74E55A3B | SceNearDialogUtil | `sceNearUtilityRefresh` | `0x80020404` |
| 0x1265745D | SceNearDialogUtil | `sceNearUtilityGetMyStatus` | `0x80020404` |
| 0x61A68242 | SceNearDialogUtil | `sceNearUtilityGetNeighbors` | `0x80020404` |
| 0x44A0F2B7 | SceNearDialogUtil | `sceNearUtilityGetNewNeighbors` | `0x80020404` |
| 0xDA56F2F1 | SceNearDialogUtil | `sceNearUtilityGetRecentNeighbors` | `0x80020404` |
| 0x9D6A7AD4 | SceNearDialogUtil | `sceNearUtilityGetLastNeighborFoundDateTime` | `0x80020404` |
| 0x294693CB | SceNearDialogUtil | `sceNearUtilitySetGift` | `0x80020404` |
| 0xDBEFB12E | SceNearDialogUtil | `sceNearUtilitySetGift2` | `0x80020404` |
| 0x8CBD3BF9 | SceNearDialogUtil | `sceNearUtilityGetGift` | `0x80020404` |
| 0xCCB5FF48 | SceNearDialogUtil | `sceNearUtilityGetGiftStatus` | `0x80020404` |
| 0x722E8B53 | SceNearDialogUtil | `sceNearUtilityDeleteGift` | `0x80020404` |
| 0xF684C554 | SceNearDialogUtil | `sceNearUtilityGetDiscoveredGifts` | `0x80020404` |
| 0xAF4740D1 | SceNearDialogUtil | `sceNearUtilityGetDiscoveredGiftInfo` | `0x80020404` |
| 0xE2A90639 | SceNearDialogUtil | `sceNearUtilityGetDiscoveredGiftSender` | `0x80020404` |
| 0xD6FFC962 | SceNearDialogUtil | `sceNearUtilityGetDiscoveredGiftStatus` | `0x80020404` |
| 0xF3FBFFE9 | SceNearDialogUtil | `sceNearUtilityConvertDiscoveredGiftParam` | `0x80020404` |
| 0x44EA7FA3 | SceNearDialogUtil | `sceNearUtilityDeleteDiscoveredGift` | `0x80020404` |
| 0xB04B9676 | SceNearDialogUtil | `sceNearUtilityIgnoreDiscoveredGift` | `0x80020404` |
| 0xBF1B4BB9 | SceNearDialogUtil | `sceNearUtilityOpenDiscoveredGiftImage` | `0x80020404` |
| 0x200D6143 | SceNearDialogUtil | `sceNearUtilityReadDiscoveredGiftImage` | `0x80020404` |
| 0x3289BF4A | SceNearDialogUtil | `sceNearUtilityCloseDiscoveredGiftImage` | `0x80020404` |
| 0x486BEBDB | SceNearDialogUtil | `sceNearUtilityOpenReceivedGiftData` | `0x80020404` |
| 0x30C8B7A3 | SceNearDialogUtil | `sceNearUtilityReadReceivedGiftData` | `0x80020404` |
| 0x28394CD7 | SceNearDialogUtil | `sceNearUtilityCloseReceivedGiftData` | `0x80020404` |
| 0x6DBC14B8 | SceNearDialogUtil | `sceNearUtilityLaunchNearAppForDownload` | `0x80020404`; logar |
| 0x8D32729E | SceNearDialogUtil | `sceNearUtilityLaunchNearAppForUpdate` | `0x80020404`; logar |

---

## 6. Debug, perf e segurança (HLE trivial)

Implementado: `src/VitaSharp.Core/Hle/Modules/DebugPerf.cs` (`DebugPerf`) para 6.1–6.3 e 6.7;
`_sceKernelBacktrace` e SceLibSsp em `ProcessMgr.cs`, ao lado de `__stack_chk_fail`; SceLibRng já estava em
`Taihen.cs` (`Security`). Testes: `tests/VitaSharp.Tests/LocationPerfHleTests.cs`.
Num console retail essas bibliotecas de devkit existem mas não fazem nada útil; o objetivo é só não quebrar o jogo
que deixou chamadas de profiler no build final.

### 6.1 ScePerf

#### Seção comum
- [x] **Prioridade:** P2 · **Biblioteca:** ScePerf · **NID:** ver tabela
- **Protótipo:** PMON e timebase em `psp2/perf.h`; Razor CPU **(inferidos)**.
- **Comportamento:** contadores de performance ARM (PMU) e marcadores do Razor CPU profiler. Sem profiler: tudo
  retorna sucesso sem efeito; consultas devolvem "não capturando"/zero.
- **Retorno/erros:** 0, salvo indicado na tabela. Códigos de erro reais (thread inválida, contador > 5) a confirmar;
  não validar.
- **Estruturas:** —
- **Implementação no VitaSharp:** `DebugPerf.cs`. `_pLibPerfCaptureFlagPtr` é **variável** exportada (o Vita3K a
  trata como `VAR_EXPORT`, o db do vita-headers a lista como função): registrar com
  `Emu.Hle.RegisterVariable(0x936A5F31u, ...)` alocando 8 bytes em `MemoryRegion.Kernel`: palavra 0 = endereço da
  palavra 1, palavra 1 = 0 (flag "capturando" = falso). O jogo lê `*_pLibPerfCaptureFlagPtr` e testa o flag. Também
  manter um `[Hle]` que retorna 0 para o caso de o import vir como função.
- **Validação:** jogo com build de profiler (Unity/Havok costumam importar `sceRazorCpuPushMarker`).

| NID | Função | Protótipo | Comportamento |
| --- | --- | --- | --- |
| 0x936A5F31 | `_pLibPerfCaptureFlagPtr` | variável `uint32_t *_pLibPerfCaptureFlagPtr` | ponteiro para flag = 0 (ver acima) |
| 0x7AD6AC30 | `_sceRazorCpuInit` | `int (const void *param)` **(inferido)** | 0 |
| 0x8F7B522C | `_sceCpuRazorPushFiberUserMarker` | `int (const char *name, ...)` **(inferido)** | 0 (chamada pela libfiber LLE) |
| 0x453AED06 | `_sceCpuRazorPopFiberUserMarker` | `int (void)` **(inferido)** | 0 |
| 0x409D966A | `_sceRazorCpuWriteFiberUltPkt` | `int (...)` **(inferido)** | 0 (chamada pela libult LLE) |
| 0x35151735 | `scePerfArmPmonReset` | `int (SceUID thid)` | 0 |
| 0x63CBEA8B | `scePerfArmPmonSelectEvent` | `int (SceUID thid, SceUInt32 counter, SceUInt8 event_code)` | 0 |
| 0xC9D969D5 | `scePerfArmPmonStart` | `int (SceUID thid)` | 0 |
| 0xD1A40F54 | `scePerfArmPmonStop` | `int (SceUID thid)` | 0 |
| 0x6132A497 | `scePerfArmPmonGetCounterValue` | `int (SceUID thid, SceUInt32 counter, SceUInt32 *value)` | `*value = 0` (se não NULL); 0 |
| 0x12F6C708 | `scePerfArmPmonSetCounterValue` | `int (SceUID thid, SceUInt32 counter, SceUInt32 value)` | 0 |
| 0x4264B4E7 | `scePerfArmPmonSoftwareIncrement` | `int (SceUInt32 mask)` | 0 |
| 0xBD9615E5 | `scePerfGetTimebaseValue` | `SceUInt64 (void)` | contador monotônico; usar `K.ProcessTimeUs` (r0:r1) |
| 0x78EA4FFB | `scePerfGetTimebaseFrequency` | `SceUInt32 (void)` | frequência coerente com o valor acima: 1 000 000 enquanto a frequência real não for confirmada (comparar com um console; não misturar unidades) |
| 0xC3DE4C0A | `sceRazorCpuPushMarker` | `int (const char *label)` **(inferido)** | 0 |
| 0x5CDC09EA | `sceRazorCpuPushMarkerWithHud` | `int (const char *label, SceUInt32 color, SceInt32 flags)` **(inferido)** | 0 |
| 0xDC3224C3 | `sceRazorCpuPopMarker` | `int (void)` | 0 |
| 0x8DA87B35 | `sceRazorCpuIsCapturing` | `SceBool (void)` **(inferido)** | 0 (falso) |
| 0xDAB63FC3 | `sceRazorCpuStartCapture` | `int (...)` **(inferido)** | 0 |
| 0x9C2C05C2 | `sceRazorCpuStopCapture` | `int (void)` **(inferido)** | 0 |
| 0x4F1385E3 | `sceRazorCpuSync` | `int (void)` **(inferido)** | 0 |
| 0x3F29E937 | `sceRazorCpuStartActivityMonitor` | `int (...)` **(inferido)** | 0 |
| 0x9D5B9621 | `sceRazorCpuStopActivityMonitor` | `int (void)` **(inferido)** | 0 |
| 0xF7F8ADCE | `sceRazorCpuGetActivityMonitorTraceBuffer` | `int (void **buf, SceSize *size)` **(inferido)** | 0; não escreve nos ponteiros (assinatura não confirmada) |
| 0xAED36A40 | `sceRazorCpuStartUserMarkerTrace` | `int (void *buffer, SceSize size)` **(inferido)** | 0 |
| 0x4B47E144 | `sceRazorCpuStopUserMarkerTrace` | `int (void)` **(inferido)** | 0 |
| 0x0DE231D4 | `sceRazorCpuGetUserMarkerTraceBuffer` | `int (void **buf, SceSize *size)` **(inferido)** | 0; idem (não escreve) |

### 6.2 SceRazorCapture e SceRazorHud

#### Seção comum
- [x] **Prioridade:** P2 · **Biblioteca:** SceRazorCapture / SceRazorHud · **NID:** ver tabela
- **Protótipo:** os que estão em `psp2/razor_capture.h` e `psp2/razor_hud.h` estão indicados; o resto
  **(inferido)**.
- **Comportamento:** captura de frame da GPU (Razor GPU) e HUD de métricas ao vivo. Sem profiler: no-op.
- **Retorno/erros:** 0; funções `void` não retornam nada (o HLE pode declarar `void`); consultas "em andamento" → 0.
- **Estruturas:** `SceRazorGpuLiveResultInfo` (0x28) em `psp2/razor_hud.h`: em `sceRazorGpuLiveSetBuffer`,
  se `results` não for NULL, zerar os 0x28 bytes e preencher `buffer_size` (+0x0C) e `result_data` (+0x00) com os
  argumentos, para o jogo ver "0 entradas" se ler.
- **Implementação no VitaSharp:** `DebugPerf.cs`.
- **Validação:** —

| NID | Biblioteca | Função | Protótipo | Comportamento |
| --- | --- | --- | --- | --- |
| 0x911E0AA0 | SceRazorCapture | `sceRazorCaptureIsInProgress` | `SceBool (void)` **(inferido)** | 0 |
| 0xE916B538 | SceRazorCapture | `sceRazorCaptureSetTrigger` | `int (SceUInt32 frameIndex, const char *filename)` **(inferido)** | 0 |
| 0x3D4B7E68 | SceRazorCapture | `sceRazorCaptureSetTriggerNextFrame` | `int (const char *filename)` **(inferido)** | 0 |
| 0xD2358BFB | SceRazorCapture | `sceRazorGpuCaptureIsInProgress` | `SceBool (void)` **(inferido)** | 0 |
| 0x472492DA | SceRazorCapture | `sceRazorGpuCaptureSetTrigger` | `int (SceUInt32 frameIndex, const char *filename)` **(inferido)** | 0 |
| 0x9DAA7E9C | SceRazorCapture | `sceRazorGpuCaptureSetTriggerNextFrame` | `void (const char *filename)` | nada |
| 0xDFBDCC4C | SceRazorCapture | `sceRazorGpuCaptureEnableSalvage` | `void (const char *filename)` | nada |
| 0x3F0913CF | SceRazorCapture | `sceRazorGpuCaptureStartSalvageMode` | `int (void)` **(inferido)** | 0 |
| 0x6A09822D | SceRazorCapture | `sceRazorGpuCaptureSetCaptureAllMemory` | `int (SceBool enable)` **(inferido)** | 0 |
| 0x8EE74EB7 | SceRazorCapture | `sceRazorGpuCaptureSetCaptureBeforeKick` | `int (SceBool enable)` **(inferido)** | 0 |
| 0x29CEFDAA | SceRazorHud | `sceRazorGpuLiveSetBuffer` | `int (void *buffer, SceSize buf_size, SceRazorGpuLiveResultInfo *results)` | 0; preencher `results` (ver acima) |
| 0x6FD64DE6 | SceRazorHud | `sceRazorGpuLiveSetMetricsGroup` | `int (uint32_t metrics)` | 0 (valor ≥ 4 → erro real a confirmar; aceitar) |
| 0xB46382FC | SceRazorHud | `sceRazorGpuLiveStart` | `int (void)` | 0 |
| 0xBD360A69 | SceRazorHud | `sceRazorGpuLiveStop` | `int (void)` | 0 |
| 0xBCB790E9 | SceRazorHud | `sceRazorGpuLiveTriggerDebugDump` | `int (void)` **(inferido)** | 0 |
| 0xE3709B44 | SceRazorHud | `sceRazorGpuPerfGetMode` | `int (void)` **(inferido)** | 0 (modo desligado) |
| 0x73F83357 | SceRazorHud | `sceRazorGpuTraceRingBufferSize` | `SceSize (void)` **(inferido)** | 0 |
| 0x7A51F1A0 | SceRazorHud | `sceRazorGpuTraceRingBufferUsed` | `SceSize (void)` **(inferido)** | 0 |
| 0xF867EFE1 | SceRazorHud | `sceRazorGpuTraceSetFilename` | `int (const char *filename, ...)` **(inferido)** | 0 |
| 0xBFEADB77 | SceRazorHud | `sceRazorGpuTraceSetMetricsGroup` | `int (uint32_t metrics)` **(inferido)** | 0 |
| 0x528AA4C7 | SceRazorHud | `sceRazorGpuTraceTrigger` | `int (void)` **(inferido)** | 0 |
| 0xECE363EF | SceRazorHud | `sceRazorCpuHudSetUserMarkerTraceBuffer` | `int (void *buffer, SceSize size)` **(inferido)** | 0 |
| 0xFD290A17 | SceRazorHud | `sceRazorCpuHudUserMarkerEnableAllThreads` | `int (void)` **(inferido)** | 0 |
| 0x6DFBAC39 | SceRazorHud | `sceRazorCpuHudUserMarkerEnableThreadId` | `int (SceUID thid)` **(inferido)** | 0 |
| 0xDFD00BAE | SceRazorHud | `sceRazorHudReloadSettings` | `int (void)` **(inferido)** | 0 |
| 0x77F2F606 | SceRazorHud | `sceRazorHudSetDisplayEnabled` | `int (SceBool enable)` **(inferido)** | 0 |
| 0xCC390860 | SceRazorHud | `sceRazorHudSetDisplayFrameCount` | `int (SceUInt32 count)` **(inferido)** | 0 |

### 6.3 SceDeci4pUserp

Canal DECI4P com o PC host (Target Manager) — só existe em devkit. Num retail não há host: abrir canal falha.

#### Seção comum
- [x] **Prioridade:** P2 · **Biblioteca:** SceDeci4pUserp · **NID:** ver tabela
- **Protótipo:** `psp2/deci4p/user.h` (os de CreateHostProcess* **inferidos**).
- **Comportamento:** sem host conectado.
- **Retorno/erros:** códigos reais a confirmar (testar num retail ou desmontar `SceDeci4pUserp`). Provisório:
  `SceError.ERROR` (`0x80020001`) para Open/CreateHostProcess*, `SceError.UNKNOWN_UID` (`0x80028001`; o
  `0x80028002` citado antes é `DIFFERENT_UID_CLASS` em `psp2/kernel/error.h`) para operações sobre socket (nunca há
  socket válido).
- **Estruturas:** —
- **Implementação no VitaSharp:** `DebugPerf.cs`.
- **Validação:** —

| NID | Função | Protótipo | Comportamento |
| --- | --- | --- | --- |
| 0x28578FE8 | `sceKernelDeci4pOpen` | `SceUID (const char *protoname, SceUInt32 protonum, SceSize bufsize)` | `0x80020001` (sem host); logar `protoname` |
| 0x63B0C50F | `sceKernelDeci4pClose` | `SceInt32 (SceUID socketid)` | `0x80028001` |
| 0x971E1C66 | `sceKernelDeci4pRead` | `SceInt32 (SceUID socketid, void *buffer, SceSize size, SceUInt32 reserved)` | `0x80028001` |
| 0xCDA3AAAC | `sceKernelDeci4pWrite` | `SceInt32 (SceUID socketid, const void *buffer, SceSize size, SceUInt32 reserved)` | `0x80028001` |
| 0x73371F35 | `sceKernelDeci4pRegisterCallback` | `SceInt32 (SceUID socketid, SceUID cbid)` | `0x80028001` |
| 0xC4E1D86D | `sceKernelDeci4pIsProcessAttached` | `SceInt32 (void)` | 0 (nenhum debugger) |
| 0x3BC66BD8 | `sceKernelDeci4pEnableWatchpoint` | `SceInt32 (void)` | 0 |
| 0x5A4CDF97 | `sceKernelDeci4pDisableWatchpoint` | `SceInt32 (void)` | 0 |
| 0x4719A799 | `sceKernelDeci4pCreateHostProcess` | `SceInt32 (const char *cmdline, ...)` **(inferido)** | `0x80020001` |
| 0x6A09F63A | `sceKernelDeci4pCreateHostProcessAndWait` | `SceInt32 (const char *cmdline, ..., SceInt32 *exitStatus)` **(inferido)** | `0x80020001` |

### 6.4 SceBacktrace

#### _sceKernelBacktrace
- [x] **Prioridade:** P2 · **Biblioteca:** SceBacktrace (módulo SceKernelModulemgr) · **NID:** 0xBF371A98
- **Protótipo:** `SceInt32 _sceKernelBacktrace(SceUID thread_id, SceKernelCallFrame *call_frame_buffer, SceSize num_bytes_buffer, const SceBacktraceArgs *args);` (`psp2/kernel/backtrace.h`)
- **Comportamento:** desempilha a pilha da thread (`thread_id` ou `SCE_KERNEL_BACKTRACE_CONTEXT_CURRENT`) gravando
  pares `{sp, pc}`. No FW 3.60 exige modo de desenvolvimento.
- **Retorno/erros:** com `SCE_KERNEL_BACKTRACE_MODE_DONT_EXCEED` (2) em `args->mode`: 0 em sucesso; sem o flag: nº
  total de frames da pilha (inclusive os que não couberam); < 0 em erro. `args` NULL → erro; buffer não NULL menor
  que 8 bytes → erro (códigos a confirmar; usa `SceError.INVALID_ARGUMENT` = `0x80020005`; o `0x80020003` citado
  antes é `SCE_KERNEL_ERROR_NOSYS`). Modo kernel (`SCE_KERNEL_BACKTRACE_MODE_KERNEL` = 1, bit 0) → erro de
  permissão (a confirmar; usa `0x80020005`).
- **Estruturas:** `SceBacktraceArgs` (0x10): +0x00 `num_frames` (`SceUInt32*`, pode ser NULL), +0x04 `mode`,
  +0x08 reserved[2]. `SceKernelCallFrame` (8): +0x00 sp, +0x04 pc.
- **Implementação no VitaSharp:** `ProcessMgr._sceKernelBacktrace` valida `args`/modo/buffer e delega ao
  `sceKernelBacktrace` existente (frame 0 = `sp`/`lr` da thread atual, depois palavras da pilha que apontam logo após
  um BL/BLX Thumb, até 64 frames; `*num_frames` = frames gravados). `DONT_EXCEED` agora é testado como bit
  (`mode & 2`), aceitando também o bit 0x8.
- **Validação:** homebrew que imprime backtrace; comparar com `DumpState`.

### 6.5 SceLibSsp

#### sceLibSspStackChkFail
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibSsp (módulo SceLibKernel) · **NID:** 0x39AD080B
- **Protótipo:** `__attribute__((__noreturn__)) void sceLibSspStackChkFail(void);` (`psp2/kernel/ssp.h`)
- **Comportamento:** alias de `__stack_chk_fail` (NID 0x37691BF8, mesma entrada no FW 3.60): executa `bkpt 0x81`
  e não retorna — o processo é derrubado (coredump), não só a thread.
- **Retorno/erros:** não retorna.
- **Estruturas:** —
- **Implementação no VitaSharp:** o método `__stack_chk_fail` de `ProcessMgr.cs` agora tem
  `[Hle("__stack_chk_fail", Library = "SceLibKernel")]` (0x37691BF8 — antes só estava ligado a
  `sceKernelStackChkFail` 0xEC287338, por isso GoW/Invizimals/Killzone/Sakatsuku e as libc/libult LLE o listavam como
  ausente), `[Hle("sceLibSspStackChkFail")]` (0x39AD080B) e `[Hle("sceKernelStackChkFail")]`. Comportamento: log de
  erro com nome/UID da thread + `DumpState` + `Emu.Stop()` + `GuestThreadExitException(-1)` (mesmo término de processo
  de `sceKernelExitProcess`; o Vita3K faz `assert(false)`). As variáveis `sceLibSspStackChkGuard` (0x1EA375E6) e
  `SceKernelStackChkGuard` (0x4458BCF3) usam o mesmo factory idempotente de `__stack_chk_guard` (0x93B8AA67), logo
  apontam para a mesma palavra de guard.
- **Validação:** homebrew compilado com `-fstack-protector` que estoura um buffer → log
  `__stack_chk_fail: stack smashing detected`.

### 6.6 SceLibRng

#### sceKernelGetRandomNumber
- [x] **Prioridade:** P2 · **Biblioteca:** SceLibRng (módulo SceLibKernel) · **NID:** 0xB2700165
- **Protótipo:** `int sceKernelGetRandomNumber(void *output, SceSize size);` (`psp2/kernel/rng.h`)
- **Comportamento:** preenche `output` com `size` bytes aleatórios (wrapper de `_sceKernelGetRandomNumber`
  do SceSblSsMgr com `dst_size == length`). Máximo 64 bytes por chamada.
- **Retorno/erros:** 0; `size > 64` → `0x810C0000` (valor usado pelo Vita3K como `SCE_RNG_ERROR_INVALID_ARGUMENT`;
  a confirmar no hardware); `output` NULL com `size > 0` → erro (código a confirmar; usar o mesmo `0x810C0000`).
  `size == 0` → 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** já existia em `Taihen.cs` (`Security.sceKernelGetRandomNumber`, com
  `RandomNumberGenerator.Fill`); os códigos de erro foram alinhados a este spec (`0x810C0000` em vez de
  `ILLEGAL_SIZE`/`ILLEGAL_ADDR`) e `size == 0` retorna 0 mesmo com `output` NULL.
  Não usar semente fixa (jogos usam para IDs de sessão/sal); se for preciso determinismo em cenários de teste,
  usar uma opção explícita (`EmulatorOptions.DeterministicRng`).
- **Validação:** teste unitário: 64 bytes → 0 e buffer alterado; 65 → `0x810C0000`.

### 6.7 SceUlobjMgr e SceUlobjDbg (suporte à libult LLE)

#### Seção comum
- [x] **Prioridade:** P2 · **Biblioteca:** SceUlobjMgr / SceUlobjDbg · **NID:** ver tabela
- **Origem:** importadas pelo `libult.suprx` que os jogos trazem (GoW, Invizimals, Killzone, Sakatsuku, DD), junto
  com `_sceRazorCpuWriteFiberUltPkt` (6.1). Registram a revisão do protocolo da libult e objetos de nível de usuário
  para o debugger; sem debugger não fazem nada.
- **Comportamento:** 0, sem efeito (o Vita3K também só retorna). Implementado em `DebugPerf.cs`.

| NID | Biblioteca | Função | Comportamento |
| --- | --- | --- | --- |
| 0x50F2F2AA | SceUlobjMgr | `_sceUlobjMgrRegisterLibultProtocolRevision` | 0 |
| 0x08769991 | SceUlobjMgr | `_sceUlobjMgrStartSupportingUserlevelObject` | 0 |
| 0xEF09284B | SceUlobjMgr | `_sceUlobjMgrStopSupportingUserlevelObject` | 0 |
| 0xD7F0F610 | SceUlobjDbg | `SceUlobjDbg_D7F0F610` (sem nome público) | 0 |
| 0xF9C0F5DA | SceUlobjDbg | `SceUlobjDbg_F9C0F5DA` (sem nome público) | 0 |
