# A — Kernel (SceLibKernel, SceThreadmgr, SceSysmem, SceProcessmgr, SceModulemgr, SceSysmodule, SceRtc)

Escopo: todas as funções user-mode do kernel que o VitaSharp ainda não tem (lista `pkg/A.tsv`, 200 NIDs), mais os stubs
já registrados em `ThreadMgr`, `SysMem`, `SystemMisc` e `ProcessMgr` que descartam comportamento que jogos notam.
I/O (`sceIo*`) fica no [B-io.md](B-io.md). Referência de semântica: headers do VitaSDK (`psp2/kernel/*.h`,
`psp2common/kernel/*.h`, `psp2kern/kernel/*.h` quando a versão user não documenta) e Vita3K
(`vita3k/modules/SceLibKernel`, `SceKernelThreadMgr`, `SceSysmem`, `SceProcessmgr`, `SceKernelModulemgr`,
`SceSysmodule`, `SceDriverUser/SceRtcUser.cpp`, `vita3k/kernel/src/sync_primitives.cpp`). Na seção 0 há um
pré-requisito transversal (códigos de erro errados em `SceError`) que afeta quase todas as funções deste arquivo.

## Contagem

| Prioridade | Funções | Observação |
| --- | --- | --- |
| P0 | 1 | `sceKernelGetMutexInfo` (Invizimals). O caso Devious Dungeon (`pthread`/`Il2CppAssemblies`) é LLE e está na seção 0.3. |
| STUB | 22 | Já existem como `[Hle]`, mas com corpo trivial ou semântica errada. |
| P1 | 179 | Inclui os 72 `sceKernelAtomic*`. |
| FORA | 20 | Segurança (`sceSblGcAuthMgr*`), PMU, shell, outros processos. |
| P0 transversal | — | Correções em `SceError` (seção 0.1); não é uma função, mas vem antes de tudo. |

## Ordem sugerida de implementação

1. **0.1 Correções em `SceError`** (P0): corrigir os valores em `Kernel/KernelObjects.cs` e os literais espalhados. Uma passada, rodar
   todos os cenários de `tests/scenarios`.
2. **0.2 helpers** (`WriteInfo`, `OpenByName`, `UidClass`, contador de Open) e a classe `ThreadEventHandler`
   (libera o nome `SimpleEvent` para os eventos de verdade).
3. **P0 `sceKernelGetMutexInfo`** (Invizimals) e os STUB `Close*`/`Open*`/`GetThreadmgrUIDClass`.
4. **Atomics** (72 funções, custo baixo, um helper genérico).
5. **Eventos simples + eventos de timer** (seções 1.3 e 1.4): uma classe nova em `Kernel/Sync.cs` serve
   `Set/Pulse/Clear/Wait/Poll/Cancel` e os timers reusam a mesma fila de espera.
6. **Info/Cancel** de Cond, LwCond, MsgPipe, Callback, RWLock, Mutex; MsgPipe vetorial.
7. **Thread misc** (signals, `CheckThreadStack`, `GetThreadStackFreeSize`, `ForVM`, `WaitException`).
8. **Módulos e sysmodule** (`StopModule`, `UnloadModule`, correções de `sceSysmodule*`, `GetSystemSwVersion`).
9. **Sysmem VM, Processmgr, RTC**.
10. **Ferramenta `imports`** reconhecendo módulos carregados em runtime (seção 0.3), para tirar o falso "missing" do
    Devious Dungeon.

---

## 0. Pré-requisitos transversais

### 0.1 Correções em SceError

- [x] **Prioridade:** P0 (afeta todas as funções que retornam esses códigos, em todos os jogos) · **Arquivo:** `src/VitaSharp.Core/Kernel/KernelObjects.cs`
- **Problema:** quase todas as constantes de threadmgr em `SceError` não batem com `psp2/kernel/error.h` (VitaSDK) nem
  com `vita3k/kernel/include/kernel/types.h`, que concordam entre si. Vários valores atuais são o código de **outro**
  erro (ex.: `UNKNOWN_SEMA_ID` atual = `UNKNOWN_LW_COND_ID` real). Jogos que comparam o retorno com a constante do SDK
  (padrão comum: `if (sceKernelTryLockLwMutex(...) == SCE_KERNEL_ERROR_LW_MUTEX_FAILED_TO_OWN)`) seguem o caminho
  errado hoje.

| Constante VitaSharp | Atual | Correto | Observação (header) |
| --- | --- | --- | --- |
| `INVALID_ARGUMENT` | 0x80020003 | **0x80020005** | 0x80020003 é `NOSYS` |
| `ILLEGAL_ADDR` | 0x8002000C | **0x80020006** | 0x8002000C é `ILLEGAL_TYPE` |
| `ILLEGAL_SIZE` | 0x80020015 | **0x8002000B** | |
| `ILLEGAL_TYPE` | 0x8002000A | **0x8002000C** | 0x8002000A é `INVALID_FLAGS` |
| `NO_MEMORY` | 0x80028004 | **0x8002710B** | 0x80028004 é `CAN_NOT_WAIT` |
| `UNKNOWN_UID` | 0x80028002 | **0x80028001** | 0x80028002 é `DIFFERENT_UID_CLASS`; em sysmem o certo é `INVALID_UID` 0x80024501 |
| `ILLEGAL_THREAD_ID` | 0x80028040 | **0x80028022** | |
| `UNKNOWN_THREAD_ID` | 0x80028041 | **0x80028021** | |
| `ILLEGAL_PRIORITY` | 0x8002801B | **0x80028023** | |
| `DORMANT` | 0x80028044 | **0x80028027** | |
| `NOT_DORMANT` | 0x80028045 | **0x80028028** | |
| `WAIT_TIMEOUT` / `WAIT_DELETE` / `WAIT_CANCEL` | 0x80028005/6/7 | ok | |
| `UNKNOWN_SEMA_ID` | 0x800281C1 | **0x80028101** | |
| `SEMA_ZERO` | 0x800281C3 | **0x80028102** | |
| `SEMA_OVF` | 0x800281C4 | **0x80028103** | |
| `UNKNOWN_EVF_ID` | 0x80028181 | **0x800280E1** | 0x80028181 é `UNKNOWN_LW_MUTEX_ID` |
| `EVF_MULTI` | 0x80028184 | **0x800280E2** | |
| `EVF_COND` | 0x80028183 | **0x800280E3** | |
| `UNKNOWN_MUTEX_ID` | 0x800281D1 | **0x80028141** | |
| `MUTEX_RECURSIVE` | 0x800281D4 | **0x80028142** | |
| `MUTEX_LOCK_OVF` | 0x800281D5 | **0x80028143** | |
| `MUTEX_UNLOCK_UDF` | 0x800281D6 | **0x80028144** | |
| `MUTEX_FAILED_TO_OWN` | 0x800281D7 | **0x80028145** | |
| `MUTEX_NOT_OWNED` | 0x800281D8 | **0x80028146** | |
| `LW_MUTEX_RECURSIVE` | 0x80028224 | **0x80028182** | |
| `LW_MUTEX_UNLOCK_UDF` | 0x80028226 | **0x80028184** | |
| `LW_MUTEX_FAILED_TO_OWN` | 0x80028227 | **0x80028185** | |
| `LW_MUTEX_NOT_OWNED` | 0x80028228 | **0x80028186** | |
| `UNKNOWN_COND_ID` | 0x800281E1 | **0x800281A1** | 0x800281E1 é `UNKNOWN_RW_LOCK_ID` |
| `UNKNOWN_CALLBACK_ID` | 0x800280B1 | **0x800280A1** | |
| `UNKNOWN_MSG_PIPE_ID` | 0x80028261 | **0x80028221** | |
| `MPP_FULL` | 0x80028262 | **0x80028222** | `MSG_PIPE_FULL` |
| `MPP_EMPTY` | 0x80028263 | **0x80028223** | `MSG_PIPE_EMPTY` |
| `UNKNOWN_RW_LOCK_ID` | 0x80028281 | **0x800281E1** | |
| `UNKNOWN_TIMER_ID` | 0x80028241 | ok | |
| `NOT_FOUND` | 0x80028080 | remover ou trocar | 0x80028080 é `KERNEL_TLS_ERROR`; para "não achou por nome" use `UID_CANNOT_FIND_BY_NAME` 0x8002450A |

Literais fora de `SceError` que também estão errados:

| Onde | Literal atual | Correto |
| --- | --- | --- |
| `ThreadMgr.cs` (LwMutex desconhecido: Delete/Lock/TryLock/GetLwMutexInfo/CreateLwCond) | 0x80028221 | `UNKNOWN_LW_MUTEX_ID` 0x80028181 |
| `ThreadMgr.cs` (LwCond desconhecido) | 0x80028241 | `UNKNOWN_LW_COND_ID` 0x800281C1 |
| `Sync.cs` `RwLock.TryLock*` | 0x80028283 | `RW_LOCK_FAILED_TO_LOCK` 0x800281E6 |
| `Sync.cs` `RwLock.UnlockRead` sem leitores | 0x80028285 | `RW_LOCK_UNLOCK_UDF` 0x800281E5 (a confirmar: pode ser `RW_LOCK_NOT_OWNED`; testar no Vita3K/hardware) |
| `Sync.cs` `RwLock.UnlockWrite` sem posse | 0x80028286 | `RW_LOCK_NOT_OWNED` 0x800281E4 |
| `VmThreads.cs` `ERR_NOT_SUSPENDED` | 0x80028047 | `NOT_SUSPENDED` 0x8002802E |
| `VmThreads.cs` `ERR_ILLEGAL_SIZE` | 0x80020015 | `ILLEGAL_SIZE` 0x8002000B |
| `SysMem.cs` `FindMemBlockByAddr` / `GetMemBlockInfoByAddr` | 0x80020009 | Vita3K usa `BLOCK_ERROR` 0x80024700 em Find; para InfoByAddr a confirmar |
| `SysMem.cs` `FreeMemBlock` / `GetMemBlockBase` UID desconhecido | `UNKNOWN_UID` | `INVALID_UID` 0x80024501 |
| `SystemMisc.cs` `sceSysmoduleIsLoaded*` | 0x805A1002 | `SCE_SYSMODULE_ERROR_UNLOADED` 0x805A1001 |

Constantes novas que este arquivo usa (adicionar em `SceError`): `ILLEGAL_PERMISSION` 0x80020008,
`ILLEGAL_PATTERN` 0x8002000D, `ILLEGAL_ATTR` 0x8002000E, `ILLEGAL_COUNT` 0x8002000F, `ILLEGAL_MODE` 0x80020010,
`UID_NAME_TOO_LONG` 0x80024002, `INVALID_UID` 0x80024501, `UID_CANNOT_FIND_BY_NAME` 0x8002450A,
`ILLEGAL_STACK_SIZE` 0x80028024, `ILLEGAL_CPU_AFFINITY_MASK` 0x80028025, `NOT_SUSPENDED` 0x8002802E,
`UNKNOWN_THREAD_EVENT_ID` 0x80028061, `ALREADY_SENT` 0x80028121, `UNKNOWN_LW_MUTEX_ID` 0x80028181,
`UNKNOWN_LW_COND_ID` 0x800281C1, `RW_LOCK_*` 0x800281E2..E7, `UNKNOWN_EVENT_ID` 0x80028201, `EVENT_COND` 0x80028202,
`MODULEMGR_NO_MOD` 0x8002D011, `MODULEMGR_NOENT` 0x8002D01C, `LIBRARYDB_NO_MOD` 0x8002D082,
`MEMBLOCK_RANGE_ERROR` 0x80024B09, `ILLEGAL_MEMBLOCK_SIZE` 0x80024B05, `MEMBLOCK_OVERFLOW` 0x80024B0B,
`ILLEGAL_BLOCK_ID` 0x80024701 (só no Vita3K, não no header; a confirmar), `ERRNO_ENOSYS` 0x80010058.

Outra divergência relacionada: `ThreadStatus` (`Kernel/GuestThread.cs`) usa `Waiting = 4`, `Suspended = 8`, mas o
header (`psp2common/kernel/threadmgr.h`, `SceThreadStatus`) define RUNNING 1, READY 2, STANDBY 4, WAITING 8,
DORMANT 16, DELETED 32, DEAD 64, STAGNANT 128, SUSPENDED 256. `sceKernelGetThreadInfo` escreve `(uint)t.Status`, então
uma thread esperando aparece como STANDBY. Corrigir o mapeamento ao escrever o campo `status` (não precisa mudar o
enum interno).

- **Implementação:** trocar os valores em `SceError`; os nomes continuam iguais, então nada mais precisa mudar além
  dos literais da segunda tabela. Os `IO_*` da mesma classe foram conferidos: seguem `0x80010000 + errno` do newlib (ENOENT 2, EBADF 9, EACCES 0xD, EEXIST 0x11, ENOTDIR 0x14, EISDIR 0x15, EINVAL 0x16, EMFILE 0x18, ENOSPC 0x1C, ENOTEMPTY 0x5A) e batem com o Vita3K (`SCE_ERROR_ERRNO_*`); `OK`, `ERROR` 0x80020001 e `NOT_IMPLEMENTED` 0x80020002 também estão certos. Uso de I/O fica no [B-io.md](B-io.md).
- **Validação:** `dotnet test`; rodar todos os cenários de `tests/scenarios` (F1, GoW, Killzone, Devious Dungeon) e
  comparar logs `Unimplemented`/travamentos antes e depois. Atenção especial a laços `TryLock` e `PollSema`, que agora
  vão reconhecer o erro.

### 0.2 Helpers comuns (usar em todas as seções abaixo)

- [x] **Prioridade:** pré-requisito · **Arquivo:** `Hle/Modules/ThreadMgr.cs` (helpers privados) e `Kernel/KernelObjects.cs`
- **`WriteInfo(uint info, ReadOnlySpan<byte> full)`** (semântica Vita3K `write_info`, confirmada pelo comentário do
  header de `sceKernelGetTimerInfo`: "Its first field, size, sets the exact number of bytes copied"):
  `info == 0` → `ILLEGAL_ADDR`; `size = R32(info)`; `size > full.Length` → `NO_MEMORY` (0x8002710B, como o Vita3K);
  copia `size` bytes de `full` para `info` e reescreve `size` no offset 0. Montar `full` num `stackalloc byte[]` com
  `BinaryPrimitives`. Hoje `sceKernelGetSemaInfo/GetEventFlagInfo/GetRWLockInfo/GetLwMutexInfo` escrevem campos fixos
  sem olhar `size`: migrar junto.
- **`UidClass` em `KernelObject`**: `public virtual int UidClass => 0;` com override por classe. Valores (Vita3K
  `kernel_object.h`, "the class reported by sceKernelGetThreadmgrUIDClass"): thread 1, semaphore 2, event_flag 3,
  mutex 4, cond 5, timer 6, msg_pipe 7, callback 8, thread_event 9, lw_mutex 10, lw_cond 11, rw_lock 12,
  simple_event 13. O enum `SceKernelIdListType` do header é herdado do PSP (4 = Mbox, 5 = Vpl...) e diverge; seguir o
  Vita3K e marcar "a confirmar" (confirmar com um homebrew que cria um objeto de cada tipo e imprime o retorno).
  `Mutex` é usado para mutex e lw mutex: retornar 10 quando `IsLight`. `CondVar` com `WorkArea != 0` → 11.
- **`OpenByName<T>(string? name, Func<T,bool>? filter)`**: `name == null` → `ILLEGAL_ADDR`; `UTF8 > 31 bytes` →
  `UID_NAME_TOO_LONG`; procura `K.Objects.FindByName<T>` (com filtro opcional, ex. lw vs heavy) → senão
  `UID_CANNOT_FIND_BY_NAME`. No hardware o Open devolve um UID novo que referencia o mesmo objeto; aqui basta devolver
  o mesmo UID e incrementar `KernelObject.OpenCount` (campo novo, `Interlocked`).
- **`CloseHandle<T>(int uid, int unknownErr)`**: `K.Objects.Get<T>(uid)` nulo → `unknownErr`; decrementa `OpenCount`
  se > 0; nunca apaga o objeto (Delete é que apaga); retorna 0.
- **`ThreadEventHandler : KernelObject`** (novo): hoje `sceKernelRegisterThreadEventHandler` registra um
  `SimpleEvent`, o que faria `sceKernelWaitEvent` aceitar o UID de um handler. Criar a classe (campos `ThreadMask`,
  `Mask`, `Handler`, `Common`, `UidClass = 9`) e reservar `SimpleEvent` para os eventos reais (seção 1.3).
- **Faixa de custo:** helpers não mudam `HleFunction.ClassifyCost`; cada seção diz a faixa da função.

### 0.3 Caso especial P0: Devious Dungeon (PCSB01240, Unity) — `pthread` e `Il2CppAssemblies` via LLE

- [x] **Prioridade:** P0 (Devious Dungeon) · **Não é HLE.**
- **Situação:** `VitaSharp.Desktop imports PCSB01240` lista no `eboot.bin` (UnityPlayer_201721_Release) 30 imports da
  biblioteca `pthread` (NID_0394D3B4 … NID_F67A82D8) e 4 de `Il2CppAssemblies` (NID_2016A56B, NID_46F3ED2C,
  NID_A7C286AB, NID_F2FAAAB6) como "missing". Esses NIDs são exportados pelos módulos do próprio jogo:

  ```
  ux0:app/PCSB01240/
    eboot.bin, archive.psarc
    Media/Modules/  il2CppAssemblies.suprx, pthread.suprx
    Media/Plugins/  SavedGames.suprx, SavedGames.suprx.b.suprx, SavedGames_stub.a,
                    UnityNpToolkit.suprx, UnityNpToolkit.suprx.b.suprx, UnityNpToolkit_stub.a
    Media/Metadata/global-metadata.dat, Media/*.assets*, Media/*.xml, Media/boot.config, ...
    sce_module/     libc.suprx, libface.suprx, libfios2.suprx, libsmart.suprx, libult.suprx
    sce_sys/        param.sfo, icon0.png, livearea/, manual/, trophy/NPWR15539_00, package/{work,temp,digs}.bin, ...
  ```

- **O que já funciona (log `.vitadata/tests/dd_boot/log.txt`):** a thread `Unity_Startup` chama
  `sceKernelLoadStartModule("app0:/Media/Modules/pthread.suprx")` → `PTHREAD_PRX` carregado (45 imports); depois
  `Unity_main_thread` carrega `app0:/Media/Modules/Il2CppAssemblies.suprx` → `LIBIL2CPP_PRX` (222 imports); as threads
  Unity nascem com `entry=0x82812098` (dentro do `pthread.suprx`). Ou seja, o `Linker` reescreve os stubs do eboot
  quando o módulo exporta os NIDs (`Linker.RegisterExports`) e o "missing" do `imports` é falso positivo: a ferramenta
  só carrega o eboot e os `sce_module/*` pré-carregados.
- **O que verificar/endurecer em `sceKernelLoadStartModule` / `Loader/ModuleLoader.cs`:**
  1. **Religação por biblioteca:** `RegisterExports` só reescreve um stub quando `blib == libNid` (NID da biblioteca no
     import igual ao do export). Conferir com log temporário que os 30+4 stubs do eboot foram reescritos (contar
     `WriteJumpStub` por NID). Se algum import usar um libNid diferente (versões diferentes da stub lib), acrescentar
     fallback por nome de biblioteca (`Imports[].Library` vs `Exports[].Library`) ou só por NID quando o NID é único.
  2. **Variáveis:** o boot loga 17 `Unresolved variable import NID ...` (338B0BD1, 3A451B91, 550DFE23, 56354517,
     5EF67445, 6ACA1D57, 71A78083, 79F9DB75, AA0340C3, F9A1DE92, 36878958, 3CE6109D, 5D8C1282, 66B7406C, D662E07C,
     DDF9BB09, BFEF56B5) antes de os módulos carregarem. `RegisterExports` religa `_varBindings` depois; confirmar que
     cada um desses NIDs aparece em `Exports[].VarNids` do `pthread.suprx` ou `Il2CppAssemblies.suprx` e que a
     relocação roda (log ao relocar). Os que não forem exportados por nenhum módulo do jogo são HLE de outro pacote.
  3. **Chamada antes do carregamento:** se o eboot chamar um import `pthread` antes do `LoadStartModule`, cai no stub
     de log (`Unimplemented HLE function ... NID_...`). Procurar esse aviso no log com os NIDs acima; hoje não aparece.
  4. **Caminho:** o jogo pede `Il2CppAssemblies.suprx` e o arquivo é `il2CppAssemblies.suprx`. No Windows passa;
     garantir que `Vfs.Resolve` faça busca case-insensitive (para Linux/macOS) e aceite `app0:/` com barra extra.
  5. **TLS por módulo:** `Emulator.LoadTitle` só usa `TlsInit/TlsAreaSize` do módulo principal. Confirmar
     (`LoadedModule.TlsAreaSize`) que `pthread.suprx` e `Il2CppAssemblies.suprx` têm TLS 0; se não, falta suporte a TLS
     de módulo carregado em runtime.
  6. **`module_start`:** `sceKernelLoadStartModule` roda `module_start` na thread chamadora (`CallGuest`). O hardware
     (e o Vita3K) roda numa thread nova; manter como está, mas tratar retorno: `module_start` devolvendo
     `SCE_KERNEL_START_NO_RESIDENT` (1) ou `SCE_KERNEL_START_FAILED` (2) deveria descarregar o módulo e retornar
     `MODULEMGR_START_FAILED` 0x8002D000 (constantes em `psp2common/kernel/modulemgr.h`: `START_SUCCESS` 0, `START_NO_RESIDENT` 1, `START_FAILED` 2; o código de erro devolvido nesse caso é a confirmar).
  7. **Ferramenta `imports`** (`src/VitaSharp.Desktop/Program.cs`, `ListImports`): depois de `LoadTitle`, carregar
     também `app0:Media/Modules/*.suprx` e `app0:Media/Plugins/*.suprx` (só para análise, sem rodar `module_start`)
     ou marcar como "LLE (módulo do jogo)" os imports cuja biblioteca é exportada por algum `.suprx` dentro de `app0:`.
     Isso tira `pthread`/`Il2CppAssemblies` da lista de faltantes e evita retrabalho.
- **Validação:** cenário `tests/scenarios/dd_boot.txt` (60 s, `cross@10..40`): o log deve mostrar os dois módulos,
  nenhum `Unimplemented HLE function` com NIDs `pthread`/`Il2Cpp`, e a ferramenta `imports PCSB01240` não deve mais
  listar essas bibliotecas.

---
## 1. SceLibKernel

### 1.1 Família `sceKernelAtomic*` (72 funções)

- **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NIDs:** tabela abaixo
- **Protótipo:** `psp2/kernel/cpu.h`. Formas (W = 8/16/32/64, `T` = `SceInt8/16/32/64`):
  - `T sceKernelAtomic<Op>W(T *store, T value);` para AddAndGet, SubAndGet, AndAndGet, OrAndGet, XorAndGet,
    ClearAndGet, GetAndAdd, GetAndSub, GetAndAnd, GetAndOr, GetAndXor, GetAndClear, GetAndSet.
  - `void sceKernelAtomicSetW(T *store, T value);` e `void sceKernelAtomicClearMaskW(T *store, T value);`
  - `T sceKernelAtomicCompareAndSetW(T *store, T value, T new_value);`
  - `SceBool sceKernelAtomicAddUnlessW(T *store, T value, T cmpv);`
  - `T sceKernelAtomicDecIfPositiveW(T *store);`
- **Comportamento:** operação read-modify-write atômica em memória do guest (no hardware, laço LDREX/STREX dentro da
  própria SceLibKernel; não é syscall). "XAndGet" devolve o valor **novo**, "GetAndX" devolve o valor **antigo**.
  - **CompareAndSet:** se `*store == value`, grava `new_value`; devolve sempre o valor antigo (o chamador compara com
    `value` para saber se trocou).
  - **AddUnless** (semântica observada pelo Vita3K, comentário "Despite the name, that's what a Vita does"): calcula
    `sum = old + value` (em 32 bits para W=8/16, 64 bits para W=64); se `sum >= cmpv` grava `cmpv` e devolve 1, senão
    grava `sum` e devolve 0. O nome e o parâmetro `cmpv` do header sugerem a semântica Linux ("soma a menos que
    `*store == cmpv`"): a confirmar em hardware; seguir o Vita3K até lá.
  - **DecIfPositive:** se `old > 0` (W=32/64) ou `old >= 0` (W=8/16, segundo o Vita3K) grava `old - 1`; devolve `old`
    em qualquer caso.
  - **ClearAndGet/GetAndClear/ClearMask:** `*store &= ~value`.
- **Retorno/erros:** sem códigos de erro; ponteiro inválido = falta de página no guest (não tratar).
  Retornos de 8/16 bits estendidos com sinal em r0; 64 bits em r0:r1.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** nova classe `Hle/Modules/Atomic.cs` (`public sealed class Atomic : HleModule`).
  Para W=32/64 usar `Interlocked.*`/`Interlocked.CompareExchange` sobre `ref GuestMemory.Ref<int/long>(addr)`
  (compatível com o STREX emulado, que é CAS por valor em `Cpu/A32.Integer.cs` `ExclusiveCore`). Para W=8/16 não há
  `Interlocked` em .NET: laço CAS sobre a palavra de 32 bits alinhada que contém o byte/halfword (`addr & ~3`),
  trocando só a fatia. Escrever um helper genérico `Rmw32(addr, Func<int,int>)`/`Rmw64`/`RmwNarrow(addr, width, f)` e
  gerar as 72 funções com `[Hle]` de uma linha cada. Binder: parâmetro `T *store` como `uint`, `value` como
  `int`/`long` (o binder põe `long` no par r2:r3 e o 3º argumento de 64 bits na pilha alinhada a 8), retorno `int`
  (já estendido) ou `long`, `void` para Set/ClearMask. Custo: `ClassifyCost` já tem `sceKernelAtomic` → 200.
- **Validação:** teste unitário em `tests/VitaSharp.Tests` chamando cada função com valores de borda (overflow em 8 bits,
  `CompareAndSet` falhando, `DecIfPositive` em 0); conferir que `imports` deixa de listar `sceKernelAtomic*`.

| Feito | Função | NID | W | Operação | Retorna |
| --- | --- | --- | --- | --- | --- |
| [x] | `sceKernelAtomicAddAndGet16` | 0x495C52EC | 16 | `*p += v` | novo |
| [x] | `sceKernelAtomicAddAndGet32` | 0x2E84A93B | 32 | `*p += v` | novo |
| [x] | `sceKernelAtomicAddAndGet64` | 0xB6CE9B9A | 64 | `*p += v` | novo |
| [x] | `sceKernelAtomicAddAndGet8` | 0x8F7BD940 | 8 | `*p += v` | novo |
| [x] | `sceKernelAtomicAddUnless16` | 0x1CF4AA4B | 16 | ver nota AddUnless (args `v`, `limit`) | `SceBool` |
| [x] | `sceKernelAtomicAddUnless32` | 0x4B33FD3C | 32 | ver nota AddUnless (args `v`, `limit`) | `SceBool` |
| [x] | `sceKernelAtomicAddUnless64` | 0xFFCE7438 | 64 | ver nota AddUnless (args `v`, `limit`) | `SceBool` |
| [x] | `sceKernelAtomicAddUnless8` | 0x7FD94393 | 8 | ver nota AddUnless (args `v`, `limit`) | `SceBool` |
| [x] | `sceKernelAtomicAndAndGet16` | 0xF9890F7E | 16 | `*p &= v` | novo |
| [x] | `sceKernelAtomicAndAndGet32` | 0x6709D30C | 32 | `*p &= v` | novo |
| [x] | `sceKernelAtomicAndAndGet64` | 0xAED2B370 | 64 | `*p &= v` | novo |
| [x] | `sceKernelAtomicAndAndGet8` | 0x86B9170F | 8 | `*p &= v` | novo |
| [x] | `sceKernelAtomicClearAndGet16` | 0xCC36B775 | 16 | `*p &= ~v` | novo |
| [x] | `sceKernelAtomicClearAndGet32` | 0x9DAA0DE8 | 32 | `*p &= ~v` | novo |
| [x] | `sceKernelAtomicClearAndGet64` | 0x264209F3 | 64 | `*p &= ~v` | novo |
| [x] | `sceKernelAtomicClearAndGet8` | 0x17B40010 | 8 | `*p &= ~v` | novo |
| [x] | `sceKernelAtomicClearMask16` | 0x5FE7DFF8 | 16 | `*p &= ~v` | `void` |
| [x] | `sceKernelAtomicClearMask32` | 0xE3DF0CB3 | 32 | `*p &= ~v` | `void` |
| [x] | `sceKernelAtomicClearMask64` | 0x953D118A | 64 | `*p &= ~v` | `void` |
| [x] | `sceKernelAtomicClearMask8` | 0xD7D49E36 | 8 | `*p &= ~v` | `void` |
| [x] | `sceKernelAtomicCompareAndSet16` | 0xE8C01236 | 16 | se `*p == v` então `*p = nv` (3º arg) | antigo |
| [x] | `sceKernelAtomicCompareAndSet32` | 0x1124A1D4 | 32 | se `*p == v` então `*p = nv` (3º arg) | antigo |
| [x] | `sceKernelAtomicCompareAndSet64` | 0x1EBDFCCD | 64 | se `*p == v` então `*p = nv` (3º arg) | antigo |
| [x] | `sceKernelAtomicCompareAndSet8` | 0x327DB4C0 | 8 | se `*p == v` então `*p = nv` (3º arg) | antigo |
| [x] | `sceKernelAtomicDecIfPositive16` | 0x323718FB | 16 | ver nota DecIfPositive (sem `v`) | antigo |
| [x] | `sceKernelAtomicDecIfPositive32` | 0xCA3294F1 | 32 | ver nota DecIfPositive (sem `v`) | antigo |
| [x] | `sceKernelAtomicDecIfPositive64` | 0x8BE2A007 | 64 | ver nota DecIfPositive (sem `v`) | antigo |
| [x] | `sceKernelAtomicDecIfPositive8` | 0x9DABE6C3 | 8 | ver nota DecIfPositive (sem `v`) | antigo |
| [x] | `sceKernelAtomicGetAndAdd16` | 0x5674DB0C | 16 | `*p += v` | antigo |
| [x] | `sceKernelAtomicGetAndAdd32` | 0x2611CB0B | 32 | `*p += v` | antigo |
| [x] | `sceKernelAtomicGetAndAdd64` | 0x63DAF37D | 64 | `*p += v` | antigo |
| [x] | `sceKernelAtomicGetAndAdd8` | 0x27A2AAFA | 8 | `*p += v` | antigo |
| [x] | `sceKernelAtomicGetAndAnd16` | 0x7A0CB056 | 16 | `*p &= v` | antigo |
| [x] | `sceKernelAtomicGetAndAnd32` | 0x08266595 | 32 | `*p &= v` | antigo |
| [x] | `sceKernelAtomicGetAndAnd64` | 0x4828BC43 | 64 | `*p &= v` | antigo |
| [x] | `sceKernelAtomicGetAndAnd8` | 0x53DCA02B | 8 | `*p &= v` | antigo |
| [x] | `sceKernelAtomicGetAndClear16` | 0x57CD7E7D | 16 | `*p &= ~v` | antigo |
| [x] | `sceKernelAtomicGetAndClear32` | 0x0226A017 | 32 | `*p &= ~v` | antigo |
| [x] | `sceKernelAtomicGetAndClear64` | 0x436AA0B5 | 64 | `*p &= ~v` | antigo |
| [x] | `sceKernelAtomicGetAndClear8` | 0xA10873A4 | 8 | `*p &= ~v` | antigo |
| [x] | `sceKernelAtomicGetAndOr16` | 0x31E49E73 | 16 | `*p |= v` | antigo |
| [x] | `sceKernelAtomicGetAndOr32` | 0x984AD276 | 32 | `*p |= v` | antigo |
| [x] | `sceKernelAtomicGetAndOr64` | 0xC39186CD | 64 | `*p |= v` | antigo |
| [x] | `sceKernelAtomicGetAndOr8` | 0x107A68DF | 8 | `*p |= v` | antigo |
| [x] | `sceKernelAtomicGetAndSet16` | 0xF7B9ADFC | 16 | `*p = v` | antigo |
| [x] | `sceKernelAtomicGetAndSet32` | 0x9B43E667 | 32 | `*p = v` | antigo |
| [x] | `sceKernelAtomicGetAndSet64` | 0xD82B3A7A | 64 | `*p = v` | antigo |
| [x] | `sceKernelAtomicGetAndSet8` | 0x2672EA0B | 8 | `*p = v` | antigo |
| [x] | `sceKernelAtomicGetAndSub16` | 0xAC51979C | 16 | `*p -= v` | antigo |
| [x] | `sceKernelAtomicGetAndSub32` | 0x115C516F | 32 | `*p -= v` | antigo |
| [x] | `sceKernelAtomicGetAndSub64` | 0x4AE9C8E6 | 64 | `*p -= v` | antigo |
| [x] | `sceKernelAtomicGetAndSub8` | 0xCDF5DF67 | 8 | `*p -= v` | antigo |
| [x] | `sceKernelAtomicGetAndXor16` | 0x6E2D0B9E | 16 | `*p ^= v` | antigo |
| [x] | `sceKernelAtomicGetAndXor32` | 0x38739E2F | 32 | `*p ^= v` | antigo |
| [x] | `sceKernelAtomicGetAndXor64` | 0x6A19BBE9 | 64 | `*p ^= v` | antigo |
| [x] | `sceKernelAtomicGetAndXor8` | 0x7350B2DF | 8 | `*p ^= v` | antigo |
| [x] | `sceKernelAtomicOrAndGet16` | 0x8E248EBD | 16 | `*p |= v` | novo |
| [x] | `sceKernelAtomicOrAndGet32` | 0xC3B2F7F8 | 32 | `*p |= v` | novo |
| [x] | `sceKernelAtomicOrAndGet64` | 0x809BBC7D | 64 | `*p |= v` | novo |
| [x] | `sceKernelAtomicOrAndGet8` | 0x51693931 | 8 | `*p |= v` | novo |
| [x] | `sceKernelAtomicSet16` | 0x450BFECF | 16 | `*p = v` | `void` |
| [x] | `sceKernelAtomicSet32` | 0xB69DA09B | 32 | `*p = v` | `void` |
| [x] | `sceKernelAtomicSet64` | 0xC8A4339C | 64 | `*p = v` | `void` |
| [x] | `sceKernelAtomicSet8` | 0xE960FDA2 | 8 | `*p = v` | `void` |
| [x] | `sceKernelAtomicSubAndGet16` | 0xC26BBBB1 | 16 | `*p -= v` | novo |
| [x] | `sceKernelAtomicSubAndGet32` | 0x01C9CD92 | 32 | `*p -= v` | novo |
| [x] | `sceKernelAtomicSubAndGet64` | 0x9BB4A94B | 64 | `*p -= v` | novo |
| [x] | `sceKernelAtomicSubAndGet8` | 0x99E1796E | 8 | `*p -= v` | novo |
| [x] | `sceKernelAtomicXorAndGet16` | 0x6F524195 | 16 | `*p ^= v` | novo |
| [x] | `sceKernelAtomicXorAndGet32` | 0x46940704 | 32 | `*p ^= v` | novo |
| [x] | `sceKernelAtomicXorAndGet64` | 0xDDC6866E | 64 | `*p ^= v` | novo |
| [x] | `sceKernelAtomicXorAndGet8` | 0x634AF062 | 8 | `*p ^= v` | novo |

### 1.2 Mutex

#### sceKernelGetMutexInfo
- [x] **Prioridade:** P0 (Invizimals, PCSA00552) · **Biblioteca:** SceLibKernel · **NID:** 0x9A6C43CA
- **Protótipo:** `int sceKernelGetMutexInfo(SceUID mutexid, SceKernelMutexInfo *info);` (`psp2/kernel/threadmgr/mutex.h`)
- **Comportamento:** copia o estado do mutex (não lightweight) para `info`, respeitando `info->size` (ver `WriteInfo`).
  Sem efeitos colaterais.
- **Retorno/erros:** 0; `info == NULL` → `INVALID_ARGUMENT` 0x80020005 (Vita3K testa o ponteiro antes do UID);
  `info->size > 0x3C` → `NO_MEMORY` 0x8002710B; UID inexistente ou que é lw mutex → `UNKNOWN_MUTEX_ID` 0x80028141.
- **Estruturas:** `SceKernelMutexInfo` (`psp2common/kernel/threadmgr.h`), 0x3C bytes: +0x00 `size`, +0x04 `mutexId`,
  +0x08 `name[32]`, +0x28 `attr`, +0x2C `initCount`, +0x30 `currentCount`, +0x34 `currentOwnerId` (UID da thread ou 0),
  +0x38 `numWaitThreads`.
- **Implementação no VitaSharp:** `ThreadMgr.cs`, ao lado de `sceKernelCreateMutex`. `Obj<Mutex>(uid)` com
  `!m.IsLight`; campos `Name`, `Attr`, `InitCount`, `LockCount`, `Owner?.Uid`, `Waiters` já existem em `Kernel/Sync.cs`
  (ler sob `lock (m)`). Usar `WriteInfo`. Custo 2_000 (padrão).
- **Validação:** Invizimals (PCSA00552): `imports` sem a função; rodar boot e conferir que não aparece
  `Unimplemented ... sceKernelGetMutexInfo`. Teste unitário: criar mutex com `initCount=1`, conferir `currentOwnerId`.

#### sceKernelCancelMutex
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x2144890D
- **Status:** feito: `Mutex.Cancel` (contador de geração `_cancelled` no abort do `Wait.Until` de `Lock`, sem custo no
  caminho sem espera); só mutex normal (lw mutex → `UNKNOWN_MUTEX_ID`). Regras de contagem conferidas com o Vita3K
  (`Mutex::cancel` em `kernel/src/sync_primitives.cpp`). Teste: `KernelModuleHleTests`.
- **Protótipo:** `int sceKernelCancelMutex(SceUID mutexid, int newCount, int *numWaitThreads);` (`psp2/kernel/threadmgr/mutex.h`)
- **Comportamento:** acorda todas as threads esperando no mutex com `WAIT_CANCEL`; `newCount == -1` restaura
  `initCount`; grava `LockCount = newCount` e dono = chamador se `newCount > 0`, senão sem dono.
- **Retorno/erros:** 0; UID → `UNKNOWN_MUTEX_ID`; `newCount < 0` (após -1) ou `newCount > 1` sem
  `SCE_KERNEL_MUTEX_ATTR_RECURSIVE` (0x2) → `ILLEGAL_COUNT` 0x8002000F. `numWaitThreads` opcional.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** método `Mutex.Cancel(GuestThread self, int newCount, Ptr<int> numWait)` em
  `Kernel/Sync.cs` no padrão de `Semaphore.Cancel` (contador `_cancelled` + abort `WAIT_CANCEL` no `Wait.Until` de
  `Lock`). `UpdateWorkArea()` no fim. Custo 2_000.
- **Validação:** teste unitário com uma thread bloqueada em `Lock` recebendo `WAIT_CANCEL`.

#### sceKernelOpenMutex_089 / sceKernelCloseMutex_089
- [x] **Prioridade:** P1 · **Biblioteca:** SceThreadmgr · **NID:** 0x2928D2EC (Open), 0xA35427EE (Close)
- **Protótipo:** sem protótipo no header. Inferido (NIDs de firmware 0.89x, mesmas assinaturas das versões atuais):
  `SceUID sceKernelOpenMutex_089(const char *name);` e `int sceKernelCloseMutex_089(SceUID mutexid);`
- **Comportamento/erros:** idênticos a `sceKernelOpenMutex`/`sceKernelCloseMutex` (seção 2.1).
- **Implementação no VitaSharp:** `[Hle("sceKernelOpenMutex_089")]` / `[Hle("sceKernelCloseMutex_089")]` nos mesmos
  métodos de `ThreadMgr.cs` (o nome está em `nids.txt`? se não, usar `Nid = 0x...`). Custo 2_000.
- **Validação:** `imports` sem os nomes.

### 1.3 Eventos simples (SceLibKernel + SceThreadmgr)

Seção comum para `sceKernelCreateSimpleEvent`, `Delete/Open/CloseSimpleEvent`, `Set/Pulse/ClearEvent`,
`Wait/WaitCB/Poll/Cancel/CancelEventWithSetPattern`, `GetEventPattern`, `GetEventInfo`, `WaitMultipleEvents(CB)` e
o registro de callbacks em eventos.

- **Protótipos:** só `sceKernelWaitEvent` está no header user (`psp2/kernel/libkernel.h`); `ksceKernelClearEvent` e
  `ksceKernelWaitEvent` em `psp2kern/kernel/threadmgr.h` documentam a mesma semântica. Os demais são inferidos das
  exportações do Vita3K (`SceLibKernel.cpp`, `SceThreadmgr.cpp`), marcados na tabela.
- **Comportamento (Vita3K `SimpleEvent` em `kernel/src/sync_primitives.cpp` + headers):**
  - Objeto com `pattern` (32 bits), `lastUserData` (u64) e fila de espera. Atributos aceitos:
    `SCE_KERNEL_EVENT_ATTR_AUTO_RESET` 0x100, `SCE_KERNEL_ATTR_TH_PRIO` 0x2000 (fila por prioridade),
    `SCE_KERNEL_ATTR_OPENABLE` 0x80. Bits do sistema: `EVENT_CREATE` 0x4 (ligado na criação), `EVENT_DELETE` 0x8,
    `EVENT_ERROR` 0x10, `EVENT_TIMER` 0x8000; bits do usuário: `EVENT_USER_DEFINED_MASK` 0xFF000000.
  - **Create:** `pattern = initPattern | 0x4`.
  - **Wait/Poll(waitPattern):** sucesso se `pattern & waitPattern != 0`; `resultPattern = pattern & waitPattern`;
    `userData = lastUserData`; com AUTO_RESET zera `pattern &= ~waitPattern`. Wait que satisfaz na hora não mexe em
    `timeout`. Wait que bloqueia: ao terminar escreve o tempo restante; erro `WAIT_TIMEOUT` deixa `resultPattern = 0`;
    `WAIT_DELETE`/`WAIT_CANCEL` escrevem `resultPattern = EVENT_ERROR` (0x10) e `userData = lastUserData`. Quem espera
    com o bit `EVENT_DELETE` em `waitPattern` sai com **sucesso** quando o evento é apagado
    (`resultPattern = (pattern | 0x8) & waitPattern`). Poll que falha: `EVENT_COND` e `resultPattern = 0`.
  - **Set(setPattern, userData):** `pattern |= setPattern`, `lastUserData = userData`, acorda em ordem da fila cada
    waiter cujo `waitPattern & pattern != 0` (aplicando AUTO_RESET por waiter). **Pulse:** igual, mas depois restaura
    `pattern = old & ~setPattern` e o `userData` antigo (só quem já esperava vê o pulso).
  - **Clear(clearPattern):** `pattern &= clearPattern` (os bits passados são os que **ficam**).
  - **Cancel:** acorda todos com `WAIT_CANCEL`, devolve o número em `*pNumWaitThreads`.
  - Wait/Poll/Clear/Cancel também aceitam o UID de um **timer** (seção 1.4): resultado `EVENT_TIMER`.
- **Retorno/erros comuns:** 0; UID que não é evento nem timer → `UNKNOWN_EVENT_ID` 0x80028201; `waitPattern == 0` →
  `INVALID_ARGUMENT`; bits fora de 0xFF000000 em `initPattern`/`setPattern`/`clearPattern` → `ILLEGAL_PATTERN`
  0x8002000D (para Clear o header kernel diz que só o bit 0x00010000 é rejeitado: a confirmar, seguir o Vita3K);
  `attr` fora de 0x2180 → `ILLEGAL_ATTR` 0x8002000E; nome NULL → `ILLEGAL_ADDR`; nome > 31 com OPENABLE →
  `UID_NAME_TOO_LONG`; poll sem bits → `EVENT_COND` 0x80028202.
- **Estruturas:** `SceKernelSimpleEventOptParam { SceSize size; }` (ignorado).
- **Implementação no VitaSharp:** nova classe `SimpleEvent` em `Kernel/Sync.cs` (renomear a atual para
  `ThreadEventHandler`, seção 0.2), no estilo de `EventFlag`: `lock(this)` + `Wait.Until(this, state, ready, timeout,
  abort, WaitDescription)`. Para "acorda na ordem da fila com AUTO_RESET" manter uma `LinkedList<Waiter>` (pattern,
  ponteiros de saída, flag `Done`, resultado) e decidir quem acorda dentro de `Set`/`Pulse`, não no predicado (senão o
  pulso some antes de o waiter rodar). Ordenar por prioridade só com TH_PRIO. As funções vão em `ThreadMgr.cs`;
  `[Hle("...CB")]` reaproveita o mesmo método (o dispatcher já liga `CallbackWait`). Custo: `sceKernelWaitEvent*`,
  `sceKernelWaitMultipleEvents*` caem em `sceKernelWait` → 12_000; acrescentar `sceKernelSetEvent`,
  `sceKernelPulseEvent`, `sceKernelPollEvent`, `sceKernelClearEvent` à faixa 12_000 (são syscalls como
  `sceKernelSetEventFlag`).
- **Validação:** teste unitário (thread A espera 0x01000000, B faz Pulse; segunda espera depois do pulso deve dar
  timeout; AUTO_RESET consome só os bits pedidos); `imports` dos 7 jogos sem esses nomes.

| Feito | Função | Lib | NID | Protótipo (inferido = sem header) | Notas |
| --- | --- | --- | --- | --- | --- |
| [x] | `sceKernelCreateSimpleEvent` | SceLibKernel | 0xE6DB2494 | inferido: `SceUID (const char *name, SceUInt32 attr, SceUInt32 initPattern, const SceKernelSimpleEventOptParam *opt)` | Retorna UID; `UidClass` 13 |
| [x] | `sceKernelDeleteSimpleEvent` | SceThreadmgr | 0x208CFE28 | inferido: `int (SceUID eventId)` | Acorda waiters (regra do bit DELETE), remove do `K.Objects`; UID errado → `UNKNOWN_EVENT_ID` |
| [x] | `sceKernelOpenSimpleEvent` | SceThreadmgr | 0x4E1E4DF8 | inferido: `SceUID (const char *name)` | `OpenByName`, só encontra eventos com `ATTR_OPENABLE` |
| [x] | `sceKernelCloseSimpleEvent` | SceThreadmgr | 0xFEF4CA53 | inferido: `int (SceUID eventId)` | `CloseHandle<SimpleEvent>`, erro `UNKNOWN_EVENT_ID` |
| [x] | `sceKernelSetEvent` | SceThreadmgr | 0x324218CD | inferido: `SceInt32 (SceUID id, SceUInt32 setPattern, SceUInt64 userData)` | `userData` em r2:r3; só SimpleEvent (timer → `UNKNOWN_EVENT_ID`) |
| [x] | `sceKernelPulseEvent` | SceThreadmgr | 0x8D27BAD6 | inferido: igual a SetEvent | Pulso |
| [x] | `sceKernelClearEvent` | SceThreadmgr | 0x7B2A4B28 | `int ksceKernelClearEvent(SceUID, SceUInt32 clear_pattern)` (`psp2kern/kernel/threadmgr.h`) | Timer: zera o "event set" |
| [x] | `sceKernelWaitEvent` | SceLibKernel | 0x120F03AF | `SceInt32 (SceUID, SceUInt32 wait_pattern, SceUInt32 *result_pattern, SceUInt64 *user_data, SceUInt32 *timeout)` (`psp2/kernel/libkernel.h`) | Sem callbacks |
| [x] | `sceKernelWaitEventCB` | SceLibKernel | 0xA0490795 | inferido: igual a WaitEvent | Processa callbacks antes e durante a espera |
| [x] | `sceKernelPollEvent` | SceLibKernel | 0x241F3634 | inferido: `SceInt32 (SceUID, SceUInt32 pattern, SceUInt32 *result, SceUInt64 *user_data)` | `EVENT_COND` se nada |
| [x] | `sceKernelCancelEvent` | SceLibKernel | 0x603AB770 | inferido: `SceInt32 (SceUID, SceUInt32 *pNumWaitThreads)` | Timer: também desarma o evento do timer |
| [ ] | `sceKernelCancelEventWithSetPattern` | SceLibKernel | 0xB01387D5 | **a confirmar**; provável `SceInt32 (SceUID, SceUInt32 setPattern, SceUInt64 userData, SceUInt32 *pNumWaitThreads)` | Vita3K não implementa. Confirmar desmontando o export em SceLibKernel (Ghidra + VitaLoader) e vendo os registradores repassados à syscall `_sceKernelCancelEventWithSetPattern` |
| [x] | `sceKernelGetEventPattern` | SceLibKernel | 0xE5A2AC66 | inferido: `SceInt32 (SceUID, SceUInt32 *pattern)` | `pattern == NULL` → `ILLEGAL_ADDR`; só SimpleEvent |
| [ ] | `sceKernelGetEventInfo` | SceLibKernel | 0xE35F19D6 | **a confirmar**: `int (SceUID, SceKernelEventInfo *info)` | Estrutura não documentada no VitaSDK nem no Vita3K. Provável (padrão das outras Info): size, id, name[32], attr, currentPattern, userData (u64), numWaitThreads. Confirmar com desmontagem antes de implementar; enquanto isso, não registrar |
| [ ] | `sceKernelWaitMultipleEvents` | SceLibKernel | 0x10586418 | **a confirmar** | Espera em vários eventos de uma vez; protótipo e struct de entrada desconhecidos (Vita3K: `UNIMPLEMENTED`). Confirmar por desmontagem; implementar como espera com predicado "algum evento satisfeito" sobre várias instâncias |
| [ ] | `sceKernelWaitMultipleEventsCB` | SceLibKernel | 0x4263DBC9 | **a confirmar** | Versão CB |
| [ ] | `sceKernelSetEventWithNotifyCallback` | SceLibKernel | 0x5FF3EE92 | **a confirmar**; provável `(SceUID, SceUInt32 setPattern, SceUInt64 userData, SceInt32 notifyArg)` | Set + `Notify` em cada callback registrado (abaixo) |
| [ ] | `sceKernelPulseEventWithNotifyCallback` | SceLibKernel | 0x92F162F3 | **a confirmar**; igual ao anterior | Pulse + notify |
| [ ] | `sceKernelRegisterCallbackToEvent` | SceThreadmgr | 0x76FB37E9 | inferido: `int (SceUID eventId, SceUID callbackId)` | Guarda o callback numa lista do evento. Com attr `NOTIFY_CB_WAKEUP_ONLY` 0x800 o callback só é notificado quando o set acorda alguém (a confirmar). Erros: `UNKNOWN_EVENT_ID`, `UNKNOWN_CALLBACK_ID`, `ALREADY_REGISTERED` 0x80028003 |
| [ ] | `sceKernelUnregisterCallbackFromEvent` | SceThreadmgr | 0x18462B11 | inferido: `int (SceUID eventId, SceUID callbackId)` | Remove um; ausente → `CALLBACK_NOT_REGISTERED` 0x800280A3 (a confirmar) |
| [ ] | `sceKernelUnregisterCallbackFromEventAll` | SceThreadmgr | 0x888A7361 | inferido: `int (SceUID eventId)` | Limpa a lista |

> Segunda passada (p1): os 9 itens abertos acima continuam sem protótipo confirmado. vita-headers (include/ e db/)
> não declara nenhum deles (nem as versões `ksce`), e no Vita3K todos (`SceLibKernel.cpp`, `SceThreadmgr.cpp`,
> `SceThreadmgrForDriver.cpp`, inclusive as syscalls `_sceKernel...`) são `UNIMPLEMENTED()` sem argumentos. Ficam
> não registrados até uma desmontagem do export confirmar argumentos/structs.

### 1.4 Timers (SceLibKernel + SceThreadmgr)

Seção comum para `CancelTimer`, `GetTimerBase(Wide)`, `GetTimerEventRemainingTime`, `GetTimerInfo`,
`SetTimerEvent`, `SetTimerTime`, `Open/CloseTimer` e as correções dos timers existentes (STUB, seção 2.2).

- **Comportamento (headers `psp2/kernel/libkernel.h`, `psp2kern/kernel/threadmgr.h` + Vita3K `Timer`):** contador em
  µs de tempo de processo. Estado: `started`, `base` (tempo de processo em que o contador valeria 0 enquanto roda),
  `stoppedCount`; `count = started ? now - base : stoppedCount`. Evento armado: `type` (0 = SET_EVENT,
  1 = PULSE_EVENT), `interval`, `repeat`, `nextEvent` (tempo de processo), `eventSet`. Quando `now >= nextEvent`:
  SET deixa `eventSet = true` (e consome com AUTO_RESET 0x100 no attr do timer), PULSE só acorda quem já espera;
  `repeat` reagenda `nextEvent += k*interval`, senão desarma. `sceKernelWaitEvent/Poll/Clear/Cancel` com UID de timer
  usam isso e devolvem `resultPattern = EVENT_TIMER` 0x8000 e `userData = 0`.
- **Implementação no VitaSharp:** estender `TimerObject` (`Kernel/Sync.cs`): hoje é `GuestStopwatch Clock` +
  `BaseUs`. Trocar por campos baseados em `K.ProcessTimeUs` (mesma base de `sceKernelGetProcessTime`), mais os campos
  de evento e um contador de waiters/`_cancelled` como em `EventFlag`. A espera de timer é
  `Wait.Until(this, ..., ready: now >= nextEvent || eventSet, timeout = min(timeout, nextEvent-now))`; como
  `Wait.Until` só acorda por `Monitor.Pulse` ou fatias de 50 ms, limitar a fatia ao tempo até `nextEvent` (passar um
  timeout interno calculado). Funções em `ThreadMgr.cs`, bloco "timers". Custo: padrão 2_000 (as esperas passam por
  `sceKernelWaitEvent`).
- **Validação:** teste unitário com `GuestClock` em modo turbo: timer com evento periódico de 1 ms, `WaitEvent`
  devolve `EVENT_TIMER` repetidas vezes; `CancelTimer` acorda waiter com `WAIT_CANCEL`.

| Feito | Função | Lib | NID | Protótipo | Comportamento / erros |
| --- | --- | --- | --- | --- | --- |
| [x] | `sceKernelCancelTimer` | SceLibKernel | 0x9CCF768C | `int (SceUID timer_id, SceUInt32 *num_wait_threads)` (`libkernel.h`) | Desarma o evento (`interval = 0`, sem repeat) e acorda waiters com `WAIT_CANCEL`; UID → `UNKNOWN_TIMER_ID` 0x80028241 |
| [x] | `sceKernelGetTimerBase` | SceLibKernel | 0x1F59E04D | `int (SceUID, SceKernelSysClock *base_time)` (`libkernel.h`) | 0 se parado, senão `base` (tempo de processo); `base_time == NULL` → `ILLEGAL_ADDR` |
| [x] | `sceKernelGetTimerBaseWide` | SceThreadmgr | 0x5DBC1960 | `SceUInt64 (SceUID)` (`ksceKernelGetTimerBaseWide`, `psp2kern/kernel/threadmgr.h`) | Igual, erro → `UINT64_MAX` (0xFFFFFFFFFFFFFFFF) |
| [x] | `sceKernelGetTimerEventRemainingTime` | SceLibKernel | 0x75B1EB3E | `int (SceUID, SceKernelSysClock *remaining_time)` (`libkernel.h`) | `max(0, nextEvent - now)`; sem evento armado: provavelmente `EVENT_NOT_SET` 0x80028242 (a confirmar); `NULL` → `ILLEGAL_ADDR` |
| [x] | `sceKernelGetTimerInfo` | SceLibKernel | 0x7E35E10A | `int (SceUID, SceKernelTimerInfo *info)` (`libkernel.h`) | `WriteInfo` com struct de 0x60 bytes (abaixo) |
| [x] | `sceKernelSetTimerEvent` | SceLibKernel | 0x621D293B | `int (SceUID, SceKernelTimerType type, const SceKernelSysClock *interval, SceBool repeat)` (`libkernel.h`) | `type` ∉ {0,1} → `ILLEGAL_TYPE` 0x8002000C; `interval == NULL` → `ILLEGAL_ADDR`; se rodando, `nextEvent = now + interval` e acorda o 1º waiter para reavaliar |
| [x] | `sceKernelSetTimerTime` | SceLibKernel | 0xFFAD717F | `int (SceUID, SceKernelSysClock *timer_time)` (`libkernel.h`) | Entrada/saída: grava o novo `count` e devolve o antigo no mesmo ponteiro (SDK ≥ 2.00; assumir sempre); `NULL` → `ILLEGAL_ADDR` |
| [x] | `sceKernelOpenTimer` | SceThreadmgr | 0xB6E286E7 | inferido: `SceUID (const char *name)` | `OpenByName<TimerObject>` |
| [x] | `sceKernelCloseTimer` | SceThreadmgr | 0xACE60E4A | inferido: `int (SceUID)` | `CloseHandle<TimerObject>(UNKNOWN_TIMER_ID)` |

`SceKernelTimerInfo` (`psp2common/kernel/threadmgr.h`, 0x60 bytes): +0x00 `size`, +0x04 `timer_id`, +0x08 `name[32]`,
+0x28 `attr`, +0x2C `active` (SceBool), +0x30 `base_time` (u64; rodando = base, parado = `count` armazenado),
+0x38 `current_time` (u64), +0x40 `schedule` (u64, próximo evento em tempo do timer ou 0), +0x48 `interval` (u64),
+0x50 `type`, +0x54 `repeat`, +0x58 `num_wait_threads`, +0x5C `reserved[1]` (0).

### 1.5 Message pipes

- **Comportamento comum:** pipe de bytes com buffer circular; `waitMode` bit 0 = `ASAP` (transfere o que der) vs
  `FULL` (só termina com tudo); bit `SCE_KERNEL_MSG_PIPE_MODE_DONT_WAIT` nas versões Try. A versão vetorial recebe
  `count` pedaços `{ void *base; SceSize size; }` (8 bytes cada, `MsgPipeVector` no Vita3K) e os trata como um único
  buffer contíguo na ordem.
- **Retorno/erros comuns:** UID → `UNKNOWN_MSG_PIPE_ID` 0x80028221; vetor NULL ou algum `base` NULL (mesmo com
  `size == 0`) → `ILLEGAL_ADDR`; `count == 0` → `ILLEGAL_COUNT` 0x8002000F; soma dos tamanhos 0 → retorna 0 na hora
  com `*pResult = 0` (diferente da versão simples ASAP de 0 bytes); espera que termina por timeout/delete/cancel escreve
  `*pResult = 0`; versões Try escrevem `*pResult = 0` em qualquer falha; buffer cheio/vazio na Try →
  `MSG_PIPE_FULL` 0x80028222 / `MSG_PIPE_EMPTY` 0x80028223.
- **Implementação no VitaSharp:** `Kernel/Sync.cs` `MsgPipe`: refatorar `Send`/`Receive` para receber
  `ReadOnlySpan<(uint addr, uint size)>` em vez de `(uint buf, int size)`, mantendo as funções existentes como caso de
  1 pedaço. Funções novas em `ThreadMgr.cs`, bloco "message pipes". Custo: `sceKernelSendMsgPipe*`/
  `sceKernelReceiveMsgPipe*` já são 12_000 (pegam os Vector por prefixo); Try* ficam em 2_000.
- **Validação:** teste unitário: enviar 3 pedaços de 4, 0, 8 bytes e receber em 2 pedaços de 6 bytes; conferir ordem.

| Feito | Função | Lib | NID | Protótipo | Notas |
| --- | --- | --- | --- | --- | --- |
| [x] | `sceKernelSendMsgPipeVector` | SceLibKernel | 0x94D506F7 | inferido (Vita3K): `SceInt32 (SceUID, const MsgPipeVector *v, SceUInt32 count, SceUInt32 waitMode, SceSize *pResult, SceUInt32 *pTimeout)` | |
| [x] | `sceKernelSendMsgPipeVectorCB` | SceLibKernel | 0x9C6F7F79 | igual | Com callbacks |
| [x] | `sceKernelTrySendMsgPipeVector` | SceLibKernel | 0x60DB346F | inferido: sem `pTimeout` | `DONT_WAIT` |
| [x] | `sceKernelReceiveMsgPipeVector` | SceLibKernel | 0x9F899087 | igual ao Send | |
| [x] | `sceKernelReceiveMsgPipeVectorCB` | SceLibKernel | 0xBE5B3E27 | igual | Com callbacks |
| [x] | `sceKernelTryReceiveMsgPipeVector` | SceLibKernel | 0x86ECC0FF | inferido: sem `pTimeout` | `DONT_WAIT` |
| [x] | `sceKernelGetMsgPipeInfo` | SceLibKernel | 0x4046D16B | `int (SceUID uid, SceKernelMppInfo *info)` (`psp2/kernel/threadmgr/msgpipe.h`) | `WriteInfo`; struct 0x3C: +0 size, +4 mppId, +8 name[32], +0x28 attr, +0x2C bufSize, +0x30 freeSize, +0x34 numSendWaitThreads, +0x38 numReceiveWaitThreads. `MsgPipe` já tem `BufferSize`, `_data.Count`, `SendWaiters`, `RecvWaiters` |
| [x] | `sceKernelCreateMsgPipeWithLR` | SceLibKernel | 0xD7E10BB1 | **inferido**: mesmos argumentos de `sceKernelCreateMsgPipe` (`name, type, attr, bufSize, opt`); o "LR" é o endereço de retorno do chamador que o kernel guarda para depuração (como `caller_address` em `sceKernelCreateThreadForUser_opt`) | Delegar para `sceKernelCreateMsgPipe`. Se a desmontagem mostrar um 6º argumento (LR), ignorá-lo |
| [x] | `sceKernelGetMsgPipeCreatorId` | SceThreadmgr | 0x70E2A6D2 | **inferido**: `SceUID (SceUID msgPipeId)` | Devolve o UID da thread que criou o pipe (a confirmar: pode ser o PID). Guardar `CreatorThreadUid` em `MsgPipe` no Create. UID → `UNKNOWN_MSG_PIPE_ID` |
| [x] | `sceKernelOpenMsgPipe` | SceThreadmgr | 0x0E1CB9F6 | inferido: `SceUID (const char *name)` | `OpenByName<MsgPipe>` |
| [x] | `sceKernelCloseMsgPipe` | SceThreadmgr | 0x1305A065 | inferido: `int (SceUID)` | `CloseHandle<MsgPipe>(UNKNOWN_MSG_PIPE_ID)` |

### 1.6 Cond, LwCond, RWLock

#### sceKernelGetCondInfo
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x6864DCE2
- **Protótipo:** sem protótipo user; inferido `int sceKernelGetCondInfo(SceUID condId, SceKernelCondInfo *info);`
  (struct em `psp2common/kernel/threadmgr.h`, que referencia a função).
- **Comportamento:** copia estado da cond (não lw). **Retorno/erros:** 0; UID → `UNKNOWN_COND_ID` 0x800281A1 (o
  Vita3K devolve `UNKNOWN_EVF_ID` por engano; não copiar); `info == NULL` → `ILLEGAL_ADDR`; `size > 0x34` → `NO_MEMORY`.
- **Estruturas:** `SceKernelCondInfo` 0x34: +0 size, +4 condId, +8 name[32], +0x28 attr, +0x2C mutexId,
  +0x30 numWaitThreads.
- **Implementação no VitaSharp:** `ThreadMgr.cs`; `Obj<CondVar>(uid)` com `WorkArea == 0`; `c.Mutex.Uid`, `c.Waiters`.
  `WriteInfo`. Custo 2_000.
- **Validação:** teste unitário.

#### sceKernelGetLwCondInfo / sceKernelGetLwCondInfoById
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xE4DF36A0 (por work area), 0x971F1DE8 (por UID)
- **Protótipo:** sem protótipo user. Inferido, por simetria com `sceKernelGetLwMutexInfo(ById)`:
  `int sceKernelGetLwCondInfo(SceKernelLwCondWork *work, SceKernelLwCondInfo *info);`
  `int sceKernelGetLwCondInfoById(SceUID lwCondId, SceKernelLwCondInfo *info);` (struct de
  `psp2kern/kernel/threadmgr/lw_cond.h`).
- **Comportamento:** a versão por work lê o UID em `work+0` e chama a ById.
- **Retorno/erros:** 0; UID/work inválido → `UNKNOWN_LW_COND_ID` 0x800281C1; `info == NULL` → `ILLEGAL_ADDR`;
  `size > 0x38` → `NO_MEMORY`.
- **Estruturas:** `SceKernelLwCondInfo` 0x38: +0 size, +4 lwcond_id, +8 name[32], +0x28 attr, +0x2C work (ponteiro
  guest da work area), +0x30 lwmutex (ponteiro da work area do lw mutex), +0x34 num_wait_threads.
- **Implementação no VitaSharp:** `ThreadMgr.cs`, junto de `LwCondOf`; `CondVar.WorkArea`, `CondVar.Mutex.WorkArea`,
  `Waiters`. Custo 2_000.
- **Validação:** teste unitário com `sceKernelCreateLwCond`.

#### sceKernelCancelRWLock
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x190CA94B
- **Protótipo:** sem header; Vita3K: `SceInt32 sceKernelCancelRWLock(SceUID rwLockId, SceUInt32 *pNumReadWaitThreads, SceUInt32 *pNumWriteWaitThreads, SceInt32 flag);`
- **Comportamento:** acorda todos os waiters (leitura e escrita) com `WAIT_CANCEL`, conta cada tipo; zera o lock; com
  `flag & SCE_KERNEL_RW_LOCK_CANCEL_WITH_WRITE_LOCK` (0x1) o chamador fica com o lock de escrita (count 1).
- **Retorno/erros:** 0; UID → `UNKNOWN_RW_LOCK_ID` 0x800281E1; ponteiros opcionais.
- **Implementação no VitaSharp:** `RwLock.Cancel(...)` em `Kernel/Sync.cs` com `_cancelled` (abort `WAIT_CANCEL` em
  `LockRead`/`LockWrite`); `ReadWaiters`/`WriteWaiters` já existem. Custo 2_000.
- **Validação:** teste unitário.

| Feito | Função | Lib | NID | Protótipo | Notas |
| --- | --- | --- | --- | --- | --- |
| [x] | `sceKernelOpenCond` | SceThreadmgr | 0x76BDA02F | `int sceKernelOpenCond(const char *name);` (`psp2/kernel/threadmgr/cond.h`) | `OpenByName<CondVar>` filtrando `WorkArea == 0` |
| [x] | `sceKernelCloseCond` | SceThreadmgr | 0x15C690E0 | `int sceKernelCloseCond(SceUID condId);` (`cond.h`) | `CloseHandle<CondVar>(UNKNOWN_COND_ID)` |
| [x] | `sceKernelOpenRWLock` | SceThreadmgr | 0xCE510196 | `SceUID sceKernelOpenRWLock(const char *name);` (`rwlock.h`) | `OpenByName<RwLock>` |
| [x] | `sceKernelCloseRWLock` | SceThreadmgr | 0xFD5BD5C1 | `int sceKernelCloseRWLock(SceUID rwlock_id);` (`rwlock.h`) | `CloseHandle<RwLock>(UNKNOWN_RW_LOCK_ID)` |

Custo de todos: 2_000. Validação: teste unitário Open→Close→Delete.

### 1.7 Callbacks

#### sceKernelGetCallbackInfo
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x88DD1BC8
- **Protótipo:** `int sceKernelGetCallbackInfo(SceUID cb, SceKernelCallbackInfo *infop);` (`psp2/kernel/threadmgr/callback.h`)
- **Comportamento:** copia o estado do callback (`WriteInfo`). Sem efeitos colaterais.
- **Retorno/erros:** 0; UID → `UNKNOWN_CALLBACK_ID` 0x800280A1; `NULL` → `ILLEGAL_ADDR`; size maior que a struct →
  `NO_MEMORY`.
- **Estruturas:** **layout a confirmar.** Header (marcado "Needs confirmation"): +0 size, +4 callbackId, +8 name[32],
  +0x28 threadId, +0x2C callback, +0x30 common, +0x34 notifyCount, +0x38 notifyArg (0x3C). Vita3K: +0 size,
  +4 callbackId, +8 name[32], +0x28 attr, +0x2C threadId, +0x30 callbackFunc, +0x34 notifyId, +0x38 notifyCount,
  +0x3C notifyArg, +0x40 pCommon (0x44). Usar o layout do Vita3K (mais recente e com `attr`, como as demais Info) e
  confirmar com um homebrew que imprime a struct.
- **Implementação no VitaSharp:** `ThreadMgr.cs`; `Callback.Owner.Uid`, `Function`, `Arg`, `NotifyCount`,
  `NotifyArg`, `Attr`; `notifyId` = UID de quem notificou (guardar em `Callback.Notify`). Custo 2_000.
- **Validação:** teste unitário.

#### sceKernelCancelCallback
- [x] **Prioridade:** P1 · **Biblioteca:** SceThreadmgr · **NID:** 0x30741EF2
- **Protótipo:** `int sceKernelCancelCallback(SceUID cb);` (`callback.h`)
- **Comportamento:** descarta notificações pendentes (`NotifyCount = 0`, `NotifyArg = 0`) sem chamar a função.
- **Retorno/erros:** 0; UID → `UNKNOWN_CALLBACK_ID`.
- **Implementação no VitaSharp:** `ThreadMgr.cs`, `lock (cb)` e zerar; decrementar `Owner.PendingCallbacks` (não deixar
  negativo). Custo 2_000.
- **Validação:** notificar, cancelar, `sceKernelCheckCallback` deve devolver 0.

### 1.8 Threads: diversos

#### sceKernelChangeCurrentThreadAttr
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x36833F93
- **Protótipo:** `int sceKernelChangeCurrentThreadAttr(SceUInt clearAttr, SceUInt setAttr);` (`psp2/kernel/threadmgr/thread.h`)
- **Comportamento:** só aceita `SCE_KERNEL_THREAD_ATTR_NOUSE_VFP` 0x01000000 e `USER` 0x80000000; no hardware nada
  visível muda (Vita3K: "thread info doesn't show a change").
- **Retorno/erros:** 0; outro bit em `clearAttr | setAttr` → `ILLEGAL_ATTR` 0x8002000E.
- **Implementação no VitaSharp:** `ThreadMgr.cs`, sem estado. Custo 2_000. **Validação:** `imports`.

#### sceKernelCheckThreadStack
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x84092FD0
- **Protótipo:** `int sceKernelCheckThreadStack(void);` (`thread.h`, retorno "Unknown")
- **Comportamento (Vita3K):** devolve os bytes livres da pilha da thread atual: `sp - stackBase`; 0 se `sp` estiver
  fora da pilha. Usado por código que verifica estouro de pilha.
- **Retorno/erros:** inteiro ≥ 0.
- **Implementação no VitaSharp:** `ThreadMgr.cs`: `Self.Core.State->R[13] - Self.StackAddr` (método `unsafe`, como
  `ProcTime`). Custo 2_000. **Validação:** teste unitário.

#### sceKernelGetCurrentThreadVfpException
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xA37A6057
- **Protótipo:** sem header; inferido `int sceKernelGetCurrentThreadVfpException(void);`
- **Comportamento:** devolve a máscara de exceções VFP da thread (palavra 4 do TLS do kernel,
  `GuestThread.TlsVfpException`), mantida por `sceKernelChangeThreadVfpException` (STUB 2.2).
- **Implementação no VitaSharp:** `ThreadMgr.cs`: `GuestMemory.Ref<int>(Self.TlsBase + 4*TlsVfpException)`. Custo 2_000.
- **Validação:** Change(0, 0x2) → Get devolve 0x2.

#### sceKernelGetThreadEventInfo
- [ ] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xE48476FE
- **Protótipo:** **a confirmar**; provável `int sceKernelGetThreadEventInfo(SceUID handlerId, SceKernelThreadEventInfo *info);`
- **Comportamento:** informações de um handler criado por `sceKernelRegisterThreadEventHandler`. Struct não
  documentada (nem VitaSDK nem Vita3K). Provável: size, id, name[32], thread mask, event mask, handler, common.
  Confirmar desmontando o export antes de implementar.
- **Implementação no VitaSharp:** depende da classe `ThreadEventHandler` (0.2). Custo 2_000.
- **Validação:** só depois de confirmar o layout.

#### sceKernelGetThreadRunStatus
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xD6B01013
- **Protótipo:** `int sceKernelGetThreadRunStatus(SceUID thid, SceKernelThreadRunStatus *status);` (`thread.h`)
- **Comportamento:** para cada uma das 4 CPUs, qual processo/thread/prioridade está rodando. O papel de `thid` não é
  claro (a confirmar); validar como UID de thread (0 = atual).
- **Retorno/erros:** 0; thread desconhecida → `UNKNOWN_THREAD_ID`; `NULL` → `ILLEGAL_ADDR`.
- **Estruturas:** `SceKernelThreadRunStatus` 0x34: +0 size, depois `cpuInfo[4]` de 12 bytes:
  {+0 processId, +4 threadId, +8 priority}.
- **Implementação no VitaSharp:** `ThreadMgr.cs`; percorrer `K.Threads` com `HasCore == true` (o `Scheduler` dá os
  núcleos) e preencher até 3 entradas (núcleos de usuário), a 4ª zerada; `processId` = 0x40010003 (mesmo valor de
  `sceKernelGetProcessId`). Custo 2_000. **Validação:** teste unitário.

#### sceKernelGetSystemInfo
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xE0241FAA
- **Protótipo:** `int sceKernelGetSystemInfo(SceKernelSystemInfo *info);` (`thread.h`)
- **Comportamento:** contadores do escalonador por CPU.
- **Retorno/erros:** 0; `NULL` → `ILLEGAL_ADDR`; size maior que 0x48 → `NO_MEMORY` (padrão Info, a confirmar).
- **Estruturas:** 0x48: +0 size, +4 activeCpuMask, +8 `cpuInfo[4]` de 16 bytes {+0 idleClock (u64), +8
  comesOutOfIdleCount, +0xC threadSwitchCount}.
- **Implementação no VitaSharp:** `ThreadMgr.cs`; `activeCpuMask = 0x7` (3 núcleos de usuário; a confirmar se o
  hardware reporta 0xF); contadores 0 ou, se o `Scheduler` tiver estatísticas, trocas de contexto. `WriteInfo`.
  Custo 2_000. **Validação:** teste unitário.

#### sceKernelGetThreadContextForVM / sceKernelSetThreadContextForVM
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x22C9595E (Get), 0x27E6DEDE (Set)
- **Status:** aliases registrados; o limite superior de tamanho (`NO_MEMORY`) e a checagem de `THREAD_ATTR_VM` não foram aplicados porque o método é compartilhado com as variantes `ForMono` usadas pelo Devious Dungeon (a confirmar o tamanho que ele passa).
- **Protótipo:** sem header; Vita3K: `int (SceUID threadId, SceKernelThreadCpuRegisterInfo *cpu, SceKernelThreadVfpRegisterInfo *vfp);`
- **Comportamento:** wrappers da SceLibKernel para as syscalls `_sceKernelGetThreadContextForVM`/`_Set...` que o
  VitaSharp já implementa em `Hle/Modules/VmThreads.cs` (`GetContext`/`SetContext`). Ler/gravar os registradores de
  uma thread suspensa por `sceKernelSuspendThreadForVM`.
- **Retorno/erros (Vita3K):** 0; alvo = thread atual → `ILLEGAL_THREAD_ID`; UID → `UNKNOWN_THREAD_ID`; thread sem
  `THREAD_ATTR_VM` 0x04000000 → `ILLEGAL_PERMISSION` 0x80020008; não suspensa → `NOT_SUSPENDED` 0x8002802E;
  `cpu->size` < 0x58 → `ILLEGAL_SIZE`, > 0x58 → `NO_MEMORY`; `vfp->size` < 0x108 → `ILLEGAL_SIZE`, > 0x108 →
  `NO_MEMORY`.
- **Estruturas:** CPU 0x58: +0 size, +4 cpsr, +8 reg[16], +0x48 tpidrurw, +0x4C teehbr, +0x50 sb (topo da pilha),
  +0x54 st (base). VFP 0x108: +0 size, +4 fpscr, +8 reg[64] (floats = d0..d31).
- **Implementação no VitaSharp:** acrescentar `[Hle("sceKernelGetThreadContextForVM")]` e
  `[Hle("sceKernelSetThreadContextForVM")]` aos métodos existentes de `VmThreads`; ajustar as validações de tamanho
  (hoje só `< 0x58`) e a checagem `THREAD_ATTR_VM` (verificar antes se algum jogo Mono cria a thread sem esse bit; se
  sim, não exigir). Custo 2_000.
- **Validação:** jogo PSM/Unity que use GC com suspensão; nenhum dos 7 instalados importa hoje.

#### sceKernelWaitException / sceKernelWaitExceptionCB
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x11E263A5, 0xAB4B5485
- **Protótipo:** sem header, Vita3K sem argumentos. **A confirmar** (provavelmente recebe ponteiro para info da
  exceção). Equivalente ao `sceKernelWaitExceptionForMono` já tratado em `VmThreads.WaitException`.
- **Comportamento:** a thread de tratamento de exceções da VM bloqueia até o kernel entregar uma exceção de outra
  thread. O VitaSharp não gera essas exceções: bloquear para sempre (respondendo a stop/callbacks na versão CB).
- **Implementação no VitaSharp:** `[Hle("sceKernelWaitException")]` e `[Hle("sceKernelWaitExceptionCB")]` no método
  existente de `VmThreads.cs`. Custo 12_000 (prefixo `sceKernelWait`).
- **Validação:** thread fica `Waiting` sem consumir CPU.

#### Sinais: sceKernelWaitSignal / sceKernelWaitSignalCB / sceKernelSendSignal
- [x] **Prioridade:** P1 · **Bibliotecas/NIDs:** `sceKernelWaitSignal` SceLibKernel 0xADCA94E5,
  `sceKernelWaitSignalCB` SceLibKernel 0x24460BB3, `sceKernelSendSignal` SceThreadmgr 0xD4C367B2
- **Protótipo:** `int sceKernelWaitSignal(SceUInt32 unk0, SceUInt32 delay, SceUInt32 *timeout);` e
  `int sceKernelSendSignal(SceUID thid);` (`psp2/kernel/threadmgr/signal.h`); CB inferido igual ao Wait.
- **Comportamento:** cada thread tem um flag "sinal pendente" (semáforo binário). Wait: se pendente, consome e
  retorna 0 sem tocar em `timeout`; senão bloqueia até Send/timeout. `delay` é documentado como "atraso antes de
  esperar"; o Vita3K ignora (a confirmar). Send: marca o flag da thread alvo e acorda.
- **Retorno/erros:** Wait: 0, `WAIT_TIMEOUT`. Send: 0; flag já ligado → `ALREADY_SENT` 0x80028121; `thid == 0` →
  `ILLEGAL_THREAD_ID` (Vita3K; o header diz que pode sinalizar a própria thread pelo UID real); UID → `UNKNOWN_THREAD_ID`.
- **Implementação no VitaSharp:** campo `bool SignalPending` em `GuestThread`; Wait usa
  `Wait.Until(Self.WaitLock, () => SignalPending, timeout, ...)` e zera; Send faz `lock (t.WaitLock)` +
  `Monitor.PulseAll`. Métodos em `ThreadMgr.cs`. Custo: Wait = 12_000 (prefixo); acrescentar `sceKernelSendSignal` à
  faixa 12_000.
- **Validação:** teste unitário (Send antes do Wait → Wait retorna 0 imediato; segundo Send → `ALREADY_SENT`).

#### sceKernelCallWithChangeStack
- [ ] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xAFE2B300
- **Protótipo:** **a confirmar** (sem header, Vita3K `UNIMPLEMENTED`). Pelo nome: chama uma função numa pilha
  fornecida pelo chamador, algo como `int (void *stackTop, SceSize stackSize, int (*func)(void *), void *arg)`.
- **Comportamento:** troca `sp` para a pilha nova, chama `func(arg)`, restaura `sp`, devolve o retorno de `func`.
- **Implementação no VitaSharp:** `ThreadMgr.cs`; `Self.CallGuest` com `R[13]` ajustado (hoje `CallGuest` deriva `sp`
  do atual: acrescentar sobrecarga com `sp` explícito; `Fiber.cs` já troca pilhas e serve de exemplo). Só implementar
  depois de confirmar a ordem dos argumentos por desmontagem.
- **Validação:** depois da confirmação, teste unitário com uma função guest que devolve `sp`.

#### sceKernelStackChkFail
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xEC287338
- **Protótipo:** sem header; inferido `void sceKernelStackChkFail(void);` (mesmo papel de `__stack_chk_fail`,
  declarado em `psp2/kernel/libkernel.h`).
- **Comportamento:** termina a thread/processo por corrupção de pilha.
- **Implementação no VitaSharp:** `[Hle("sceKernelStackChkFail")]` no método `__stack_chk_fail` de `ProcessMgr.cs`
  (loga, `DumpState`, `GuestThreadExitException`). Custo 2_000. **Validação:** `imports`.

#### sceKernelBacktrace / sceKernelPrintBacktrace
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x894037E8 (Backtrace), 0x1C02A434 (Print)
- **Protótipo:** `SceInt32 sceKernelBacktrace(SceUID thread_id, SceKernelCallFrame *buf, SceSize num_bytes_buffer, SceUInt32 *num_frames, SceInt32 mode);`
  e `SceInt32 sceKernelPrintBacktrace(const SceKernelCallFrame *call_frame, SceUInt32 num_frames);` (`psp2/kernel/libkernel.h`)
- **Comportamento:** Backtrace preenche `{sp, pc}` (8 bytes, `psp2common/kernel/backtrace.h`) da pilha de chamadas;
  `thread_id = 0` = atual; `mode`: USER 0, KERNEL 1, DONT_EXCEED 2. Com DONT_EXCEED retorna 0; senão retorna a
  profundidade total mesmo se o buffer for menor. Print imprime os quadros no TTY.
- **Retorno/erros:** buffer não NULL menor que 1 quadro → `INVALID_ARGUMENT` (a confirmar); thread → `UNKNOWN_THREAD_ID`.
- **Implementação no VitaSharp:** `ProcessMgr.cs`, ao lado de `sceKernelBacktraceSelf` (hoje devolve 0 quadros).
  Reaproveitar a heurística de `GuestThread.Backtrace` (palavras da pilha que apontam logo após BL/BLX) devolvendo
  pares (endereço da palavra na pilha, retorno). Print: `Log.Info` com os pcs. Custo 2_000.
- **Validação:** chamar a partir de um teste e conferir que os pcs caem em segmentos de módulos carregados.

### 1.9 Módulos (exportados por SceLibKernel)

#### sceKernelStopModule
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x3B2CBA09
- **Protótipo:** `int sceKernelStopModule(SceUID modid, SceSize args, void *argp, int flags, void *option, int *status);` (`psp2/kernel/modulemgr.h`)
- **Comportamento:** chama `module_stop(args, argp)` do módulo; grava o retorno em `*status`; o módulo continua
  carregado. `SCE_KERNEL_STOP_SUCCESS` 0, `STOP_FAIL` 1.
- **Retorno/erros:** 0; UID → `MODULEMGR_NO_MOD` 0x8002D011.
- **Implementação no VitaSharp:** `SystemMisc.cs`; extrair de `sceKernelStopUnloadModule` (que hoje só para) um helper
  `StopModule(m, args, argp, status)`. Custo 2_000. **Validação:** teste com módulo de exemplo ou jogo que descarrega
  plugins.

#### sceKernelUnloadModule
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x1987920E (+ syscall `_sceKernelUnloadModule` 0x8E4A7716)
- **Status:** feito: `Linker.UnregisterModule` (remove exports e bindings do próprio módulo; stubs de outros módulos
  que saltavam para ele vão para o próximo provedor do NID ou de volta ao stub HLE; imports de variável são
  re-relocados para uma variável HLE quando existe) + `Emulator.UnloadModule` (invalida o JIT e libera os segmentos,
  remove de `K.Modules`/`K.Objects`). Sem checagem de "ainda iniciado" (o Vita3K também não faz).
  `sceKernelStopUnloadModule` agora faz Stop + Unload. O executável principal não pode ser descarregado
  (`MODULEMGR_NO_MOD`). Teste: `KernelModuleHleTests.UnregisterModuleRebindsImports`.
- **Protótipo:** `int sceKernelUnloadModule(SceUID modid, int flags, SceKernelULMOption *option);` (`modulemgr.h`)
- **Comportamento:** remove um módulo parado: libera segmentos, tira as exportações do linker (imports que apontavam
  para ele voltam a cair no HLE/stub de log) e invalida o código JIT da faixa.
- **Retorno/erros:** 0; UID → `MODULEMGR_NO_MOD`; módulo ainda iniciado → `MODULEMGR_NOT_STOPPED` 0x8002D016 (a
  confirmar se o hardware exige).
- **Implementação no VitaSharp:** `SystemMisc.cs` + `Linker` (método novo `UnregisterExports(LoadedModule)` que
  reescreve com `WriteHleStub` os stubs que apontavam para o módulo); `K.Modules.TryRemove`, `K.Objects.Remove`,
  `Mem.Free` dos segmentos, `Emu.Code.Invalidate(seg)`. `sceKernelStopUnloadModule` passa a chamar Stop + Unload.
  Custo 2_000. **Validação:** carregar/descarregar `UnityNpToolkit.suprx` num teste do Devious Dungeon e conferir que
  nada aponta para memória liberada.

#### sceKernelOpenModule / sceKernelCloseModule
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x4F2D8B15 (Open), 0x657FA50E (Close)
- **Status:** feito com os 4 argumentos confirmados da syscall (`path/modid, args, argp, flags`), registrados também
  nas syscalls `_sceKernelOpenModule` 0x9D674F45 / `_sceKernelCloseModule` 0x849E78BE. Os argumentos 5/6 do wrapper
  (opt/status, inferidos) não são lidos nem gravados, para não escrever por um ponteiro que talvez não exista. Open de
  um caminho já carregado devolve o mesmo UID e conta referência; Close da última referência roda `module_stop` e
  descarrega. O Vita3K não implementa nenhuma das duas.
- **Protótipo:** só a syscall está no header: `SceUID _sceKernelOpenModule(const char *module_filename, SceSize args, const void *argp, SceUInt32 flags);`
  e `SceUID _sceKernelCloseModule(SceUID modid, SceSize args, const void *argp, SceUInt32 flags);` (`psp2/kernel/modulemgr.h`).
  Wrapper user inferido: `SceUID sceKernelOpenModule(const char *path, SceSize args, void *argp, int flags, void *opt, int *status)`
  e `int sceKernelCloseModule(SceUID modid, SceSize args, void *argp, int flags, void *opt, int *status)` (mesmo
  padrão de LoadStart/StopUnload; a confirmar).
- **Comportamento (inferido):** Open = LoadStart compartilhado (se o mesmo caminho já está carregado, incrementa
  referência e devolve o UID); Close = decrementa e, ao chegar a 0, Stop + Unload.
- **Implementação no VitaSharp:** `SystemMisc.cs`; contador de referência por `LoadedModule.Path`; reaproveitar
  `sceKernelLoadStartModule` e o Unload acima. Custo 2_000. **Validação:** teste com dois Open do mesmo caminho.

### 1.10 Outras de SceLibKernel

#### sceSblACMgrIsGameProgram
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x963F4A99
- **Protótipo:** `SceInt32 sceSblACMgrIsGameProgram(SceBool *result);` (`psp2/sblacmgr.h`)
- **Comportamento:** `*result = 1` (processo de jogo). **Erros:** `result == NULL` → `ILLEGAL_ADDR` (a confirmar).
- **Implementação no VitaSharp:** `SystemMisc.cs` (ao lado de `sceAppMgrIsGameProgram`). Custo 2_000.
- **Validação:** `imports`.

#### FORA (SceLibKernel)

| Feito | Função | NID | Justificativa |
| --- | --- | --- | --- |
| FORA | `sceKernelGetPMUSERENR` | 0xF8A99FDF | Lê o registrador de permissão da PMU (perfilamento). Se aparecer em jogo, devolver 0. |
| FORA | `sceKernelPMonThreadGetCounter` | 0x99B2BF15 | Contadores de PMU por thread; só ferramentas de profiling. Se aparecer, devolver `PMON_ERROR` 0x80028280. |
| FORA | `sceSblGcAuthMgrAdhocBB160Shutdown` | 0x5F9F0D61 | Criptografia de adhoc interna dos módulos de rede do sistema. |
| FORA | `sceSblGcAuthMgrAdhocBB224Shutdown` | 0xE633BAD1 | Idem. |
| FORA | `sceSblGcAuthMgrGetMediaIdType01` | 0xF7027E6A | Autenticação de cartão de jogo. |
| FORA | `sceSblGcAuthMgrMsSaveBBCipherFinal` | 0xEB02F15D | Cifra de saves do emulador de PSP (sistema). |
| FORA | `sceSblGcAuthMgrMsSaveBBMacUpdate` | 0xEE2D40F7 | Idem. |
| FORA | `sceSblGcAuthMgrPcactActivation` | 0x17C0CEF4 | Ativação de conteúdo por PC (shell). |
| FORA | `sceSblGcAuthMgrPcactGetChallenge` | 0x9557D15C | Idem. |
| FORA | `sceSblGcAuthMgrPkgVry` | 0x12C7CD2B | Verificação de assinatura de pacote (instalador). |

---

## 2. SceThreadmgr

As funções de SceThreadmgr ligadas a eventos, timers, message pipes, cond/rwlock, callbacks e sinais estão nas seções
1.3–1.8 (com a biblioteca indicada em cada linha). Aqui ficam as demais e os STUB.

### 2.1 Novas

#### sceKernelCreateThreadForUser
- [x] **Prioridade:** P1 · **Biblioteca:** SceThreadmgr · **NID:** 0xC0FAF6A3
- **Protótipo:** `SceUID sceKernelCreateThreadForUser(const char *name, SceKernelThreadEntry entry, SceInt32 init_priority, const sceKernelCreateThreadForUser_opt *opt);` (`psp2/kernel/threadmgr.h`)
- **Comportamento:** syscall que o wrapper `sceKernelCreateThread` da SceLibKernel chama; jogos só a importam se
  contornam a libkernel. Mesma semântica de `sceKernelCreateThread`.
- **Retorno/erros (Vita3K):** UID; `name == NULL` → `INVALID_MEMORY_ACCESS` 0x80022005; `entry == 0` → `ILLEGAL_ADDR`;
  prioridade fora de 64..191 e fora da faixa relativa a `SCE_KERNEL_DEFAULT_PRIORITY` → `ILLEGAL_PRIORITY`;
  `stack_size <= 0` → `ILLEGAL_STACK_SIZE` 0x80028024; afinidade fora de `SCE_KERNEL_CPU_MASK_USER_ALL` (0x70000) →
  `ILLEGAL_CPU_AFFINITY_MASK` 0x80028025; attr fora de 0x2000|0x01000000|0x04000000|0x80000000 → `ILLEGAL_ATTR`.
  `init_priority == SCE_KERNEL_CURRENT_THREAD_PRIORITY` (0, a confirmar) herda a do chamador.
- **Estruturas:** `sceKernelCreateThreadForUser_opt` 0x18: +0 size (ignorado, wrapper põe 0x18), +4 stack_size,
  +8 attr, +0xC cpu_affinity_mask, +0x10 opt_param, +0x14 caller_address.
- **Implementação no VitaSharp:** `ThreadMgr.cs`: ler `opt` e chamar o mesmo caminho de `sceKernelCreateThread`
  (`K.CreateThread`, `ResolvePriority`). Aproveitar para aplicar as mesmas validações em `sceKernelCreateThread`
  (hoje só valida `entry`). Custo 2_000.
- **Validação:** teste unitário criando thread pelos dois caminhos.

#### sceKernelDelayThreadCB200
- [x] **Prioridade:** P1 · **Biblioteca:** SceThreadmgr · **NID:** 0x34938242
- **Protótipo:** sem header; inferido `int sceKernelDelayThreadCB200(SceUInt delay);`
- **Comportamento:** como `sceKernelDelayThreadCB`, com atraso mínimo de 201 µs (Vita3K usa `< 201 → 201`; o
  VitaSharp usa 200 em `sceKernelDelayThread200`: alinhar os dois em 201, a confirmar).
- **Implementação no VitaSharp:** `[Hle("sceKernelDelayThreadCB200")]` no método `sceKernelDelayThread200` de
  `ThreadMgr.cs` (o sufixo `200` não termina em "CB", então `IsCallbackWait` não liga: criar método separado que
  processa callbacks via `ProcessCallbacks(Self)` antes e depois, ou estender `IsCallbackWait` para nomes que contêm
  "CB200"). Custo 12_000 (prefixo `sceKernelDelay`). **Validação:** `imports`.

#### sceKernelGetThreadTLSAddr
- [x] **Prioridade:** P1 · **Biblioteca:** SceThreadmgr · **NID:** 0xBACA6891
- **Protótipo:** `void *sceKernelGetThreadTLSAddr(SceUID thid, int key);` (`psp2/kernel/threadmgr/thread.h`)
- **Comportamento:** como `sceKernelGetTLSAddr`, mas da thread `thid` (0 = atual): endereço da palavra `key` (0..0xFF)
  do TLS do usuário.
- **Retorno/erros:** endereço; `key` fora da faixa ou thread inexistente → 0 (NULL).
- **Implementação no VitaSharp:** `ThreadMgr.cs`: `K.GetThread(thid)?.TlsBase + key*4` (mesma conta de
  `sceKernelGetTLSAddr`). Custo 200: acrescentar `sceKernelGetThreadTLSAddr` à faixa barata (o prefixo
  `sceKernelGetTLSAddr` não pega). **Validação:** teste unitário.

#### sceKernelWaitThreadEnd_089 / sceKernelWaitThreadEndCB_089
- [x] **Prioridade:** P1 · **Biblioteca:** SceThreadmgr · **NID:** 0xF3489EF4, 0x0373C5E3
- **Protótipo:** sem header; inferido igual a `int sceKernelWaitThreadEnd(SceUID thid, int *stat, SceUInt *timeout);`
  (NIDs da firmware 0.89x).
- **Implementação no VitaSharp:** `[Hle("sceKernelWaitThreadEnd_089")]`/`[Hle("sceKernelWaitThreadEndCB_089")]` no
  método existente (o nome CB termina em `_089`, não em `CB`: tratar callbacks explicitamente como no item acima).
  Custo 12_000. **Validação:** `imports`.

#### FORA (SceThreadmgr)

| Feito | Função | NID | Justificativa |
| --- | --- | --- | --- |
| FORA | `sceKernelChangeActiveCpuMask` | 0x001173F8 | Muda os núcleos ativos do processo; usado pelo shell/sistema. Se aparecer, devolver 0. |

### 2.2 STUB (já existem, precisam de comportamento real)

#### sceKernelGetThreadmgrUIDClass
- [x] **Prioridade:** STUB · **Biblioteca:** SceThreadmgr · **NID:** 0xC9678F7F · `ThreadMgr.cs:110` (sempre 0)
- **Protótipo:** `SceKernelIdListType sceKernelGetThreadmgrUIDClass(SceUID uid);` (`psp2/kernel/threadmgr/thread.h`)
- **Comportamento:** classe do objeto de threadmgr dono do UID (tabela em 0.2: thread 1 … simple_event 13). Bibliotecas
  usam para validar handles ("isto é um semáforo?") e ramificar.
- **Retorno/erros:** classe > 0; UID desconhecido → `INVALID_UID` 0x80024501 (Vita3K). Objetos que não são de
  threadmgr (MemBlock, módulo) → a confirmar (`INVALID_UID_CLASS` 0x80024508 é o candidato).
- **Implementação no VitaSharp:** `K.GetThread(uid)` (aceita 0 = atual) → 1; senão `K.Objects.Get<KernelObject>(uid)`
  e `UidClass` (0.2); `UidClass == 0` → erro. Custo 2_000.
- **Validação:** teste unitário com um objeto de cada tipo.

#### sceKernelOpenSema / sceKernelCloseSema
- [x] **Prioridade:** STUB · **Biblioteca:** SceThreadmgr · **NID:** 0xCBE235C7 (Open), 0xA2D81F9E (Close) · `ThreadMgr.cs:226-227`
- **Protótipo:** `SceUID sceKernelOpenSema(const char *name);`, `int sceKernelCloseSema(SceUID semaid);` (`psp2/kernel/threadmgr/semaphore.h`)
- **Comportamento:** Open devolve um handle para o semáforo de mesmo nome; Close solta esse handle (não apaga o objeto).
  Hoje Close não valida o UID (sempre 0) e Open devolve `UNKNOWN_SEMA_ID` quando não acha.
- **Retorno/erros:** Open: UID; `UID_CANNOT_FIND_BY_NAME` 0x8002450A; `UID_NAME_TOO_LONG` 0x80024002; `ILLEGAL_ADDR`.
  Close: 0; UID que não é semáforo → `UNKNOWN_SEMA_ID` 0x80028101.
- **Implementação no VitaSharp:** `OpenByName<Semaphore>` / `CloseHandle<Semaphore>` (0.2). Custo 2_000.
- **Validação:** teste unitário: Close de UID inválido devolve erro; Close não impede `sceKernelSignalSema` pelo UID
  do criador.

#### sceKernelOpenEventFlag / sceKernelCloseEventFlag
- [x] **Prioridade:** STUB · **Biblioteca:** SceThreadmgr · **NID:** 0xBC19F8A1 (Open), 0x9A68F547 (Close) · `ThreadMgr.cs:268-269`
- **Protótipo:** inferido `SceUID sceKernelOpenEventFlag(const char *name);`, `int sceKernelCloseEventFlag(SceUID evfId);`
  (`psp2/kernel/threadmgr/eventflag.h`)
- **Comportamento/erros:** como semáforo; erro de Close → `UNKNOWN_EVF_ID` 0x800280E1.
- **Implementação no VitaSharp:** `OpenByName<EventFlag>` / `CloseHandle<EventFlag>`. Custo 2_000.
- **Validação:** idem.

#### sceKernelOpenMutex / sceKernelCloseMutex
- [x] **Prioridade:** STUB · **Biblioteca:** SceThreadmgr · **NID:** 0x52E17182 (Open), 0x03E23AF6 (Close) · `ThreadMgr.cs:307-308`
- **Protótipo:** `int sceKernelOpenMutex(const char *name);`, `int sceKernelCloseMutex(SceUID mutexid);` (`psp2/kernel/threadmgr/mutex.h`)
- **Comportamento/erros:** como semáforo; Open só encontra mutex **não** lightweight (`!IsLight`; hoje `FindByName`
  pode devolver um lw mutex de mesmo nome); erro de Close → `UNKNOWN_MUTEX_ID` 0x80028141.
- **Implementação no VitaSharp:** `OpenByName<Mutex>(name, m => !m.IsLight)` / `CloseHandle<Mutex>`; as variantes
  `_089` (1.2) usam os mesmos métodos. Custo 2_000.
- **Validação:** idem.

#### sceKernelChangeThreadVfpException
- [x] **Prioridade:** STUB · **Biblioteca:** SceThreadmgr · **NID:** 0xCC18FBAE · `ThreadMgr.cs:108` (sempre 0)
- **Protótipo:** `int sceKernelChangeThreadVfpException(SceUInt32 clear, SceUInt32 set);` (`psp2/kernel/threadmgr/vfp.h`)
- **Comportamento:** `mask = set | (mask & ~clear)` na palavra `TlsVfpException` (4) do TLS do kernel da thread
  atual. Bits válidos: `SCE_KERNEL_VFP_EXCP_*` IOC 0x1, DZC 0x2, OFC 0x4, UFC 0x8, IXC 0x10, IDC 0x80, QC 0x08000000
  (`psp2common/kernel/threadmgr.h`). O VitaSharp não precisa gerar traps VFP; só manter o valor para
  `sceKernelGetCurrentThreadVfpException`.
- **Retorno/erros:** header diz `SCE_OK`; o Vita3K devolve a máscara antiga (a confirmar; manter 0). Bits fora de
  `~0xF7FFFF60` ou `clear & set != 0` → `INVALID_ARGUMENT`.
- **Implementação no VitaSharp:** `ThreadMgr.cs`, `GuestMemory.Ref<int>(Self.TlsBase + 16)`. Custo 2_000.
- **Validação:** teste com Get (1.8).

#### sceKernelGetThreadStackFreeSize
- [x] **Prioridade:** STUB · **Biblioteca:** SceThreadmgr · **NID:** 0x4F8A3DA0 · `ThreadMgr.cs:157` (`StackSize/2`)
- **Protótipo:** `int sceKernelGetThreadStackFreeSize(SceUID thid);` (`thread.h`)
- **Comportamento (Vita3K):** thread dormente → `StackSize`; senão `sp - StackAddr` da thread. Alternativa mais fiel
  (é o que o hardware faz, a confirmar): contar bytes ainda com o padrão de preenchimento desde a base — o VitaSharp já
  preenche a pilha com 0xCC em `GuestThread`, então dá para medir o pico de uso.
- **Retorno/erros:** tamanho; thread → `UNKNOWN_THREAD_ID` (hoje devolve 0).
- **Implementação no VitaSharp:** `ThreadMgr.cs`; para outra thread, ler `t.Core.State->R[13]` (ou varrer 0xCC).
  Custo 2_000. **Validação:** teste unitário.

#### sceKernelSignalCondTo / sceKernelSignalLwCondTo
- [x] **Prioridade:** STUB · **Biblioteca:** SceThreadmgr (`SignalCondTo` 0x1269F4EC), SceLibKernel (`SignalLwCondTo` 0xFC1A48EB) · `ThreadMgr.cs:390,413`
- **Protótipo:** `int sceKernelSignalCondTo(SceUID condId, SceUID threadId);` (`cond.h`), lw análogo com work area.
- **Comportamento:** acorda **a thread indicada** se ela espera na cond; hoje acorda uma qualquer. Thread que não espera
  → erro (a confirmar o código; o Vita3K valida só a existência da thread → `UNKNOWN_THREAD_ID`).
- **Implementação no VitaSharp:** em `CondVar` (`Kernel/Sync.cs`) trocar o contador `_signals` por um conjunto de
  waiters (`HashSet<GuestThread>` de "liberados"); `Signal()` libera o primeiro da fila, `SignalTo(t)` libera `t`.
  Custo 12_000 (prefixo `sceKernelSignal`). **Validação:** teste com duas threads esperando; `SignalTo` na segunda.

#### Timers existentes: sceKernelStartTimer / sceKernelStopTimer / sceKernelGetTimerTime / sceKernelGetTimerTimeWide
- [x] **Prioridade:** STUB · **Biblioteca:** SceThreadmgr (Start 0x48091E0C, Stop 0x869E9F20, TimeWide 0x3EFD3165), SceLibKernel (GetTimerTime 0x381DC300) · `ThreadMgr.cs:524-555`
- **Protótipo:** `int ksceKernelStartTimer(SceUID)`, `int ksceKernelStopTimer(SceUID)`, `SceUInt64 GetTimerTimeWide(SceUID)`
  (`psp2kern/kernel/threadmgr.h`), `int sceKernelGetTimerTime(SceUID, SceKernelSysClock *)` (`psp2/kernel/libkernel.h`).
- **Comportamento a corrigir:** Start devolve **1** se já estava rodando (0 se iniciou); Stop devolve **1** se parou e
  **0** se já estava parado; GetTimerTimeWide com UID inválido devolve `UINT64_MAX` (hoje 0); GetTimerTime com UID
  inválido devolve `UNKNOWN_TIMER_ID` e `NULL` → `ILLEGAL_ADDR` (hoje sempre 0). `sceKernelDeleteTimer` deve validar o
  UID (`UNKNOWN_TIMER_ID`) e acordar waiters com `WAIT_DELETE`.
- **Implementação no VitaSharp:** junto com a reescrita de `TimerObject` (1.4). Custo 2_000.
- **Validação:** testes da seção 1.4.

#### sceKernelRegisterThreadEventHandler
- [x] **Prioridade:** STUB · **Biblioteca:** SceLibKernel · **NID:** 0x6D8C0F13 · `ThreadMgr.cs:174`
- **Protótipo:** `SceUID sceKernelRegisterThreadEventHandler(const char *name, SceUID thid, SceInt32 eventMask, SceKernelThreadEventHandler handler, void *common);` (`thread.h`)
- **Comportamento:** registra um handler chamado em eventos de thread (START 0x4, EXIT 0x8 em `SceKernelThreadEventType`);
  `thid = 0x10027` = todas as threads do processo. Hoje cria um `SimpleEvent` e nunca chama o handler.
- **Implementação no VitaSharp:** criar `ThreadEventHandler` (0.2). Chamar o handler é opcional (nenhum jogo instalado
  depende, a confirmar); o mínimo é não ser confundido com evento simples e `sceKernelUnregisterThreadEventHandler`
  validar o UID (`UNKNOWN_THREAD_EVENT_ID` 0x80028061). Se for chamar: na saída/início de `GuestThread`, `CallGuest`
  com a assinatura de `SceKernelThreadEventHandler` (a confirmar em `psp2common/kernel/threadmgr.h`).
- **Validação:** `sceKernelGetThreadmgrUIDClass` do UID devolve 9.

---

## 3. SceSysmem

### 3.1 Novas

#### sceKernelOpenVMDomain / sceKernelCloseVMDomain / sceKernelSyncVMDomain
- [x] **Prioridade:** P1 · **Biblioteca:** SceSysmem · **NID:** 0x9CA3EB2B (Open), 0xD6CA56CA (Close), 0x19D2A81A (Sync)
- **Protótipo:** `int sceKernelOpenVMDomain(void);`, `int sceKernelCloseVMDomain(void);`,
  `int sceKernelSyncVMDomain(SceUID uid, void *data, SceSize size);` (`psp2/kernel/sysmem.h`)
- **Comportamento:** usados por JITs (Mono/PSM) junto com `sceKernelAllocMemBlockForVM`. Open torna os blocos VM
  graváveis (o header diz "executáveis"), Close volta ao estado anterior; Sync faz flush de cache de instrução da faixa
  depois de escrever código.
- **Retorno/erros (Vita3K):** Open com domínio já aberto / Close com domínio fechado → `ERRNO_ENOSYS` 0x80010058.
  Sync: UID que não é bloco → `ILLEGAL_BLOCK_ID` 0x80024701 (a confirmar, não está no header); faixa fora do bloco →
  `MEMBLOCK_RANGE_ERROR` 0x80024B09.
- **Implementação no VitaSharp:** `SysMem.cs`: flag `_vmDomainOpen`. Sync: validar e chamar
  `Emu.Code.Invalidate(data, size)` (o mesmo usado em `Emulator.LoadModule`), essencial para não executar JIT velho.
  Custo 2_000. **Validação:** jogo com JIT (nenhum dos 7); teste unitário de Sync invalidando o cache.

#### sceKernelFreeMemBlockForVM
- [x] **Prioridade:** P1 · **Biblioteca:** SceSysmem · **NID:** 0x4EA13FEA
- **Protótipo:** `int sceKernelFreeMemBlockForVM(SceUID uid);` (`sysmem.h`)
- **Comportamento:** libera um bloco criado por `sceKernelAllocMemBlockForVM`.
- **Retorno/erros:** 0; UID que não é bloco VM → `INVALID_ARGUMENT` (Vita3K).
- **Implementação no VitaSharp:** `SysMem.cs`: marcar os blocos VM (`MemBlock.IsVm`) em `sceKernelAllocMemBlockForVM`
  e aplicar também as regras do Vita3K ali (tamanho múltiplo de 1 MiB senão `ILLEGAL_MEMBLOCK_SIZE` 0x80024B05; soma
  dos blocos VM ≤ 16 MiB senão `MEMBLOCK_OVERFLOW` 0x80024B0B); delegar para `sceKernelFreeMemBlock`. Custo 2_000.
- **Validação:** teste unitário.

#### FORA (SceSysmem)

| Feito | Função | NID | Justificativa |
| --- | --- | --- | --- |
| FORA | `sceKernelAllocUnmapMemBlock` | 0xEC636BCB | Só disponível com "user remapping" habilitado (header); aplicativos do sistema. |
| FORA | `sceKernelCheckModelCapability` | 0x0144FBD9 | Só índices 7 e 10 (Teleport/Remote Play, header). Se aparecer: 7/10 → 0, outro → `INVALID_ARGUMENT`. |

### 3.2 STUB

#### sceKernelCloseMemBlock
- [x] **Prioridade:** STUB · **Biblioteca:** SceSysmem · **NID:** 0xB680E3A0 · `SysMem.cs` (sempre 0)
- **Protótipo:** inferido `int sceKernelCloseMemBlock(SceUID uid);` (par de `sceKernelOpenMemBlock(const char *name, int flags)`).
- **Comportamento:** solta o handle aberto por `sceKernelOpenMemBlock`; não libera a memória.
- **Retorno/erros:** 0; UID → `INVALID_UID` 0x80024501. `sceKernelOpenMemBlock` deve devolver
  `UID_CANNOT_FIND_BY_NAME` 0x8002450A quando não acha (Vita3K; hoje `UNKNOWN_UID`).
- **Implementação no VitaSharp:** `CloseHandle<MemBlock>(INVALID_UID)`. Custo 2_000. **Validação:** teste unitário.

#### sceKernelGetSubbudgetInfo
- [x] **Prioridade:** STUB · **Biblioteca:** SceSysmem · **NID:** 0x832B4A65 · `SysMem.cs` (`(uint p) => 0`, não escreve nada)
- **Protótipo:** do header: `int sceKernelGetSubbudgetInfo(SceKernelSubbudgetId subbudget, SceKernelSubbudgetInfo *info);`
  (`psp2/kernel/sysmem.h`; o binding atual tem um argumento só, está errado).
- **Comportamento:** total e livre do subbudget MAIN (0) ou CDLG (1, memória de common dialog). O header avisa que na
  3.60 exige o DIP switch 159: em console de varejo provavelmente retorna erro (a confirmar o código). Como algum jogo
  instalado importa a função, preencher é mais seguro do que deixar lixo: MAIN = mesmo cálculo de
  `sceKernelGetFreeMemorySize` (`UserBudget`), CDLG = valor a confirmar.
- **Estruturas:** `SceKernelSubbudgetInfo` 0xC: +0 size, +4 total_size, +8 free_size.
- **Retorno/erros:** 0; id fora de {0,1} → `INVALID_ARGUMENT`; `info == NULL` → `ILLEGAL_ADDR`.
- **Implementação no VitaSharp:** `SysMem.cs`; corrigir a assinatura para `(int id, uint info)`. Custo 2_000.
- **Validação:** descobrir pelo `imports` qual jogo importa e rodar o cenário dele.

---

## 4. SceProcessmgr

#### sceKernelGetProcessTimeCore / sceKernelGetProcessTimeLowCore / sceKernelGetProcessTimeWideCore
- [ ] **Prioridade:** P1 · **Biblioteca:** SceProcessmgr · **NID:** 0xD37A8437 (Core), 0xF5D0D4C6 (LowCore), 0x89DA0967 (WideCore)
- **Status:** não feito nesta passada: fontes de tempo estão sendo alteradas em outro branch (fix/kz-limbo).
- **Protótipo:** `int sceKernelGetProcessTimeCore(SceKernelSysClock *time);`, `SceUInt32 sceKernelGetProcessTimeLowCore(void);`,
  `SceKernelSysClock sceKernelGetProcessTimeWideCore(void);` (`psp2/kernel/processmgr.h`)
- **Comportamento:** syscalls por trás de `sceKernelGetProcessTime*` da SceLibKernel: tempo de processo em µs.
- **Retorno/erros:** Core: 0; `time == NULL` → `ILLEGAL_ADDR` (a confirmar; o header diz "Non-NULL").
- **Implementação no VitaSharp:** `[Hle(...)]` extras nos métodos `sceKernelGetProcessTime`, `...Low`, `...Wide` de
  `ThreadMgr.cs` (usar `ProcTime()` nas três, hoje `sceKernelGetProcessTime` usa `K.ProcessTimeUs` sem a escala de
  depuração). Custo 200 (prefixo `sceKernelGetProcessTime`). **Validação:** `imports`.

#### sceKernelGetProcessName
- [x] **Prioridade:** P1 · **Biblioteca:** SceProcessmgr · **NID:** 0x10C52C95
- **Protótipo:** sem header; Vita3K `int sceKernelGetProcessName(char *name, SceUInt32 len);`
- **Comportamento:** copia o nome do processo (`process_name` do SceProcessParam, offset 0x20 do `sceProcessParam`;
  se 0, o title id) com no máximo `len` bytes.
- **Retorno/erros:** 0; `name == NULL`, `len == 0` ou `len > 32` → `INVALID_ARGUMENT` (Vita3K; mesma regra de
  `sceKernelGetProcessTitleId`).
- **Implementação no VitaSharp:** `ProcessMgr.cs`; `Emu.Linker.ProcessParam` + `GuestMemory.R32(pp + 0x20)`
  (`ProcessParamString` em `Emulator` faz a mesma leitura para 0x10). Aproveitar para pôr as mesmas validações em
  `sceKernelGetProcessTitleId`. Custo 2_000. **Validação:** teste unitário.

#### FORA (SceProcessmgr)

| Feito | Função | NID | Justificativa |
| --- | --- | --- | --- |
| FORA | `sceKernelCDialogSessionClose` | 0xDB4CC1D0 | Usada pelos módulos de common dialog do sistema, que o VitaSharp substitui por HLE. |
| FORA | `sceKernelCDialogSetLeaseLimit` | 0xEC8DDAAD | Idem. |
| FORA | `sceKernelGetExtraTty` | 0x2D635A00 | TTY extra de depuração (DevKit). Se aparecer, devolver o mesmo fd de `sceKernelGetStderr`. |
| FORA | `sceKernelGetRemoteProcessTime` | 0xE6E9FCA3 | Tempo de outro processo; só há um processo. Se aparecer: pid próprio → igual a `GetProcessTime`, outro → `INVALID_UID`. |
| FORA | `sceKernelRegisterProcessTerminationCallback` | 0x5EC77870 | Avisa quando **outro** processo termina (monitoramento de processos, shell). |
| FORA | `sceKernelUnregisterProcessTerminationCallback` | 0x973A4527 | Idem. |

---

## 5. SceModulemgr

#### sceKernelGetAllowedSdkVersionOnSystem
- [x] **Prioridade:** P1 · **Biblioteca:** SceModulemgr · **NID:** 0x4397FC4E
- **Protótipo:** `SceUInt32 sceKernelGetAllowedSdkVersionOnSystem(void);` (`psp2/kernel/modulemgr.h`)
- **Comportamento:** maior versão de SDK que o firmware aceita (formato 0xMMmmpppp, ex. 0x03740011). Bibliotecas
  comparam com a versão do SDK do jogo.
- **Retorno/erros:** versão; sem erros.
- **Implementação no VitaSharp:** `SystemMisc.cs`; devolver o mesmo valor que `sceKernelGetSystemSwVersion` grava em
  `version` (3.74 → 0x03740011, constante compartilhada). Valor exato do hardware a confirmar. Custo 2_000.
- **Validação:** `imports`.

#### sceKernelGetLibraryInfoByNID
- [x] **Prioridade:** P1 · **Biblioteca:** SceModulemgr · **NID:** 0xEAEB1312
- **Protótipo:** `int sceKernelGetLibraryInfoByNID(SceUID modid, SceNID libnid, SceKernelLibraryInfo *info);`
  (`psp2/kernel/modulemgr.h`; a struct `SceKernelLibraryInfo` está declarada sem corpo: "missing structs").
- **Comportamento:** informações da biblioteca `libnid` exportada pelo módulo `modid`.
- **Estruturas:** **a confirmar.** A struct mais próxima documentada é `SceKernelModuleLibraryInfo` (0x120,
  `psp2common/kernel/modulemgr.h`): +0 size, +4 library_id, +8 libnid, +0xC version (u16), +0xE flags (u16),
  +0x10 entry_num_function (u16), +0x12 entry_num_variable (u16), +0x14/+0x16 desconhecidos, +0x18 library_name[0x100],
  +0x118 client_count, +0x11C owner_module_id. Usar como base e confirmar.
- **Retorno/erros:** 0; módulo → `MODULEMGR_NO_MOD` 0x8002D011; biblioteca não exportada → `MODULEMGR_NO_LIB`
  0x8002D003 (a confirmar).
- **Implementação no VitaSharp:** `SystemMisc.cs`; `LoadedModule.Exports` já guarda nome, libNid, NIDs de função e de
  variável. Custo 2_000. **Validação:** teste unitário com `libc.suprx`.

#### sceKernelIsCalledFromSysModule
- [x] **Prioridade:** P1 · **Biblioteca:** SceModulemgr · **NID:** 0x85E6D2BB
- **Protótipo:** `int sceKernelIsCalledFromSysModule(void *lr);` (`modulemgr.h`)
- **Comportamento:** 1 se `lr` está dentro de um módulo de sistema (carregado de `vs0:`/`os0:`), 0 se dentro de
  módulo do jogo. Módulos de sistema carregados como LLE (`sce_module/libc.suprx` etc. vêm de `app0:`, então contam
  como do jogo).
- **Implementação no VitaSharp:** `SystemMisc.cs`: achar o módulo por endereço (mesmo laço de
  `sceKernelGetModuleIdByAddr`) e devolver 1 se `Path` começa com `vs0:`/`os0:`, senão 0. Custo 2_000.
- **Validação:** teste unitário.

#### FORA (SceModulemgr)

| Feito | Função | NID | Justificativa |
| --- | --- | --- | --- |
| FORA | `sceKernelInhibitLoadingModule` | 0x6CED1F63 | Bloqueia carga de módulos no processo; usada pelo shell/sistema. Se aparecer, devolver 0. |

#### STUB: sceKernelGetSystemSwVersion
- [x] **Prioridade:** STUB · **Biblioteca:** SceModulemgr · **NID:** 0x5182E212 · `SystemMisc.cs` (última linha)
- **Protótipo:** `int sceKernelGetSystemSwVersion(SceKernelSystemSwVersion *version);` (`psp2/kernel/modulemgr.h`)
- **Problema:** grava `version` em +0x14, mas a struct (`psp2common/kernel/modulemgr.h`, 0x28 bytes) é +0 size,
  +4 `versionString[0x1C]`, **+0x20 `version`**, +0x24 `unk_24`. Quem lê `version` recebe lixo/zero.
- **Comportamento:** preencher `versionString = "3.74"` (zerar o resto dos 0x1C), `version = 0x03740011`, `unk_24 = 0`
  (a confirmar), respeitando `size`.
- **Implementação no VitaSharp:** corrigir o offset; usar a constante compartilhada com
  `sceKernelGetAllowedSdkVersionOnSystem`. Custo 2_000. **Validação:** teste unitário lendo +0x20.

---

## 6. SceSysmodule

#### sceSysmoduleUnloadModuleInternalWithArg
- [x] **Prioridade:** P1 · **Biblioteca:** SceSysmodule · **NID:** 0xA2F40C4C
- **Protótipo:** `int sceSysmoduleUnloadModuleInternalWithArg(SceSysmoduleInternalModuleId id, SceSize args, void *argp, const SceSysmoduleOpt *option);` (`psp2/sysmodule.h`)
- **Comportamento:** descarrega um módulo interno passando argumentos ao `module_stop`; no VitaSharp os módulos são
  HLE, então só remove de `_loadedSysmodules` e, se `option && option->result`, grava 0 em `*option->result`.
- **Retorno/erros:** 0; módulo não carregado → `SCE_SYSMODULE_ERROR_UNLOADED` 0x805A1001 (a confirmar; hoje
  `sceSysmoduleUnloadModule` devolve sempre 0).
- **Estruturas:** `SceSysmoduleOpt` 0x10: +0 flags, +4 `int *result`, +8 unused[2].
- **Implementação no VitaSharp:** `SystemMisc.cs`, novo método (as outras assinaturas têm só `id`). Custo 2_000.
- **Validação:** `imports`.

#### STUB: sceSysmoduleIsLoaded / sceSysmoduleIsLoadedInternal
- [x] **Prioridade:** STUB · **Biblioteca:** SceSysmodule · **NID:** 0x53099B7A, 0x09A4AF1D · `SystemMisc.cs`
- **Protótipo:** `int sceSysmoduleIsLoaded(SceSysmoduleModuleId id);` (`psp2/sysmodule.h`)
- **Problema:** devolve 0x805A1002 quando não carregado; o header define `SCE_SYSMODULE_ERROR_UNLOADED = 0x805A1001`
  (0x805A1002 não existe). Jogos que fazem `if (IsLoaded(x) == SCE_SYSMODULE_ERROR_UNLOADED) LoadModule(x)` nunca
  carregam.
- **Implementação no VitaSharp:** trocar a constante. Ids fora da faixa → `SCE_SYSMODULE_ERROR_INVALID_VALUE`
  0x805A1000. Custo 2_000. **Validação:** cenários de todos os jogos (afeta inicialização de bibliotecas).

#### STUB: sceSysmoduleLoadModuleInternalWithArg
- [x] **Prioridade:** STUB · **Biblioteca:** SceSysmodule · **NID:** 0xC3C26339 · `SystemMisc.cs` (mesmo método de `sceSysmoduleLoadModule(uint id)`)
- **Protótipo:** `int sceSysmoduleLoadModuleInternalWithArg(SceSysmoduleInternalModuleId id, SceSize args, void *argp, const SceSysmoduleOpt *option);`
- **Problema:** ignora `option`; quem passa `option->result` lê lixo como retorno do `module_start`.
- **Implementação no VitaSharp:** método próprio que marca como carregado e, se `option != 0 && R32(option+4) != 0`,
  grava 0 (`SCE_KERNEL_START_SUCCESS`) no ponteiro. Custo 2_000. **Validação:** teste unitário.

---

## 7. SceRtc / SceRtcUser

Ticks em µs desde 0001-01-01 00:00:00 UTC (`Rtc.cs`, `NowTick`). `SceDateTime` (`psp2common/types.h`, 0x10):
+0 year, +2 month, +4 day, +6 hour, +8 minute, +0xA second (u16 cada), +0xC microsecond (u32). Erros
(`psp2common/kernel/rtc.h`): `INVALID_VALUE` 0x80251000, `INVALID_POINTER` 0x80251001, `INVALID_YEAR` 0x80251081,
`INVALID_MONTH` 0x80251082, `INVALID_DAY` 0x80251083. Todas em `Hle/Modules/Rtc.cs`, custo 200 (prefixo `sceRtc`).
Aproveitar para conferir que `sceRtcCheckValid` devolve 0x80251081..87 conforme o campo (hoje 0x80251002, que é
`NOT_INITIALIZED`).

#### sceRtcGetDayOfYear
- [x] **Prioridade:** P1 · **Biblioteca:** SceRtcUser · **NID:** 0xB5C4E95F
- **Protótipo:** sem header; inferido `int sceRtcGetDayOfYear(int year, int month, int day);` (par de
  `sceRtcGetDayOfWeek(int, int, int)`).
- **Comportamento:** dia do ano; base 1 (1º de janeiro = 1) a confirmar (PSP devolve 1..366).
- **Retorno/erros:** dia; validações iguais às de `sceRtcGetDayOfWeek` no Vita3K (`INVALID_MONTH`, `INVALID_YEAR` se
  `year < 0`, `INVALID_DAY`).
- **Implementação:** `new DateTime(y,m,d).DayOfYear` dentro de validação explícita (não `try/catch`). **Validação:**
  teste unitário (29/02/2024 → 60).

#### sceRtcGetDosTime / sceRtcSetDosTime
- [x] **Prioridade:** P1 · **Biblioteca:** SceRtcUser · **NID:** 0x92ABEBAF (Get), 0xF8B22B07 (Set)
- **Protótipo:** `int sceRtcGetDosTime(const SceDateTime *time, unsigned int *puiDosTime);`,
  `int sceRtcSetDosTime(SceDateTime *time, unsigned int uiDosTime);` (`psp2/rtc.h`)
- **Comportamento:** formato FAT: `((year-1980)&0x7F)<<25 | (month&0xF)<<21 | (day&0x1F)<<16 | (hour&0x1F)<<11 |
  (minute&0x3F)<<5 | (second>>1)&0x1F`. Get: ano < 1980 → `*dos = 0` e `INVALID_VALUE`; ano ≥ 2108 →
  `*dos = 0xFF9FBF7D` e `INVALID_VALUE` (Vita3K/PPSSPP). Set: decodifica (segundos ×2, microsecond 0).
- **Retorno/erros:** 0; ponteiro NULL → `INVALID_POINTER`.
- **Implementação:** `Rtc.cs`, usar `ReadDateTime`/`WriteDateTime` só para os campos (sem `DateTime` para não
  normalizar valores inválidos). **Validação:** ida e volta de uma data.

#### sceRtcTickAddTicks / sceRtcTickAddWeeks / sceRtcTickAddMonths / sceRtcTickAddYears
- [x] **Prioridade:** P1 · **Biblioteca:** SceRtcUser · **NID:** 0x4559E2DB (Ticks), 0xE713C640 (Weeks), 0x6321B4AA (Months), 0xDF6C3E1B (Years)
- **Protótipo:** `int sceRtcTickAddTicks(SceRtcTick *pTick0, const SceRtcTick *pTick1, SceLong64 lAdd);` e
  `int sceRtcTickAdd{Weeks,Months,Years}(SceRtcTick *pTick0, const SceRtcTick *pTick1, int lAdd);` (`psp2/rtc.h`)
- **Comportamento:** `*pTick0 = *pTick1 + delta`. Ticks: delta em µs. Weeks: `lAdd * 604800 * 10^6`. Months/Years:
  converte para data, soma meses/anos e **satura o dia** no último dia do mês resultante (31/01 + 1 mês → 28/29/02);
  ano resultante 0 → `INVALID_YEAR`; data de origem com dia 0 → `INVALID_VALUE`.
- **Retorno/erros:** 0; ponteiro NULL → `INVALID_POINTER`.
- **Implementação:** `Rtc.cs`, mesmo estilo de `sceRtcTickAddDays` (`Ptr<ulong>`; `lAdd` de Ticks como `long` no par
  r2:r3). Months/Years com `DateTime.AddMonths/AddYears` (já saturam o dia). **Validação:** teste unitário com 31/01.

#### sceRtcGetCurrentAdNetworkTick / sceRtcGetCurrentDebugNetworkTick / sceRtcGetCurrentGpsTick / sceRtcGetCurrentRetainedNetworkTick
- [x] **Prioridade:** P1 · **Biblioteca:** SceRtcUser · **NID:** 0x5612AF12 (Ad), 0x4ED6BA6C (Debug), 0xBB92534F (Gps), 0xA086AE18 (Retained)
- **Protótipo:** `int sceRtcGetCurrent{AdNetwork,DebugNetwork,Gps,RetainedNetwork}Tick(SceRtcTick *tick);` (`psp2/rtc.h`)
- **Comportamento:** relógios alternativos (ad hoc, depuração, GPS do 3G, rede retida). Sem rede/GPS reais, devolver o
  mesmo que `sceRtcGetCurrentNetworkTick` (`NowTick`). GPS em console sem 3G deveria falhar (código a confirmar;
  `NOT_INITIALIZED` 0x80251002 é candidato).
- **Retorno/erros:** 0; NULL → `INVALID_POINTER`.
- **Implementação:** `[Hle(...)]` extras em `sceRtcGetCurrentNetworkTick` de `Rtc.cs` (com checagem de NULL).
- **Validação:** `imports`.

#### sceRtcGetLastAdjustedTick / sceRtcGetLastReincarnatedTick
- [x] **Prioridade:** P1 · **Biblioteca:** SceRtcUser · **NID:** 0x8148E95C (Adjusted), 0xA8C16615 (Reincarnated)
- **Protótipo:** `int sceRtcGetLastAdjustedTick(SceRtcTick *tick);`, `int sceRtcGetLastReincarnatedTick(SceRtcTick *tick);` (`psp2/rtc.h`)
- **Comportamento:** momento do último ajuste do relógio pelo usuário / da última "reencarnação" (reset do RTC). Usado
  por jogos para detectar troca de hora (anti-trapaça de eventos diários). Devolver um tick fixo no passado: o
  `BootTick` do `Rtc` (horário do boot do emulador) para os dois.
- **Retorno/erros:** 0; NULL → `INVALID_POINTER`.
- **Implementação:** `Rtc.cs`. **Validação:** `imports`.

#### sceRtcGetAccumulativeTime
- [x] **Prioridade:** P1 · **Biblioteca:** SceRtc · **NID:** 0x258BE8EC
- **Protótipo:** `SceULong64 sceRtcGetAccumulativeTime(void);` (`psp2/rtc.h`)
- **Comportamento:** contador monotônico acumulado (não afetado por ajuste de hora). O Vita3K (marcado STUBBED) devolve
  o tick atual; unidade a confirmar. Devolver `NowTick` mantém coerência com os outros relógios.
- **Implementação:** `Rtc.cs`, retorno `ulong` (r0:r1). **Validação:** `imports`.
