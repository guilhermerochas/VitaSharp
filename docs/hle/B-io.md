# B — I/O: `sceIo*`, SceIofilemgr, overlays do SceFios2Kernel, `NID_023EAA62` / `tty0:`

Escopo: funções `sceIo*` da SceLibKernel (síncronas e Async) e da SceIofilemgr, os `_sceFiosKernelOverlay*` que faltam
no SceFios2Kernel (importados pela `libfios2.suprx` LLE de F1 2011 e Uncharted), o NID desconhecido `NID_023EAA62`
chamado pela `libc.suprx` LLE, e os stubs triviais de I/O que já existem no VitaSharp.

Fontes usadas (2026-10-18): lista alvo `pkg/B.tsv`; `docs/hle/data/*.tsv`; vita-headers (`psp2/io/*.h`,
`psp2common/kernel/iofilemgr.h`, `psp2/fios2kernel.h`, `psp2/fios2kernel02.h`, `psp2common/fios2.h`,
`psp2/kernel/error.h`, `psp2/kernel/libkernel.h`); Vita3K `modules/SceIofilemgr`, `modules/SceLibKernel`,
`modules/SceFios2Kernel`, `io/src/io.cpp`; código atual `Hle/Modules/IoFileMgr.cs`, `Fios2User.cs`, `ThreadMgr.cs`,
`Kernel/Sync.cs`, `Io/Vfs.cs`; log `%APPDATA%\VitaSharp\logs\last.log` (F1 2011) e desmontagem do call site em
`libc.suprx` com `VitaSharp.Desktop disasm/stub PCSE00002 <addr>`.

## Contagem por prioridade

| Prioridade | Qtde | Funções |
| --- | --- | --- |
| P0 | 11 | 10 × `_sceFiosKernelOverlay*` + `NID_023EAA62` (com mapeamento `tty0:`/`tty1:`) |
| STUB | 6 | `sceIoChstat`, `sceIoChstatByFd`, `sceIoSync`, `sceIoIoctl`, `_sceFiosKernelOverlayThreadSetDisabled`, `_sceFiosKernelOverlayThreadIsDisabled` |
| P1 | 34 | 22 variantes Async, `sceIoComplete`, `sceIoCompleteMultiple`, `sceIoCancel`, 6 de prioridade de I/O, `sceIoRead2`, `sceIoWrite2`, `sceIoClose2` |
| FORA | 5 | `sceIoFlockForSystem` e 4 `*PriorityForSystem` |
| **Total** | **56** | |

Nenhuma variante Async nem as funções de prioridade são importadas pelos 7 jogos instalados
(`missing-by-game.tsv`). Todas as P0 são importadas por F1 2011 (PCSE00002) e Uncharted (PCSA00029); das P0, só
`NID_023EAA62` é chamada em runtime no log atual. As DH*/GetList/GetInfo/Modify são só importadas até agora.

## Ordem sugerida de implementação

1. **`NID_023EAA62` + dispositivos `tty0:`/`tty1:`** (P0, ~40 linhas). Elimina o `openStdio : failed` da libc LLE
   e faz a saída `printf` do jogo chegar ao log.
2. **`_sceFiosKernelOverlayGetInfo`, `GetList`, `Modify`, `ModifyForProcess`** (P0). Reaproveitam a tabela de
   overlays que já existe em `Fios2User`.
3. **Família `_sceFiosKernelOverlayDH*`** (P0). Primeiro o modo fiel ao FW 3.60 ("não suportado"); a listagem mesclada
   fica para quando algum jogo precisar.
4. **STUBs**: `sceIoChstat`/`ChstatByFd`, `sceIoSync`, `ThreadSetDisabled`/`IsDisabled`, `sceIoIoctl` (só validação e
   log).
5. **Prioridades de I/O** (P1, triviais).
6. **Infraestrutura Async** (seção comum abaixo) → `sceIoComplete`/`CompleteMultiple`/`Cancel` → as 22 variantes.
   Depende de `sceKernelWaitEvent`/`PollEvent` (pacote A, [A-kernel.md](A-kernel.md)).
7. **`sceIoRead2`/`Write2`/`Close2`** só depois de confirmar os protótipos.

## Notas gerais (valem para o pacote todo)

- **Códigos de erro de I/O** seguem `0x80010000 | errno` com os valores de errno da newlib. Já confirmados (Vita3K
  `io/io.h` e `SceError`): `ENOENT 0x80010002`, `EBADF 0x80010009`, `EACCES 0x8001000D`, `EEXIST 0x80010011`,
  `ENOTDIR 0x80010014`, `EISDIR 0x80010015`, `EINVAL 0x80010016`, `EMFILE 0x80010018`, `ENOSPC 0x8001001C`,
  `EBADFD 0x80010051`, `ENOTEMPTY 0x8001005A`, `EOPNOTSUPP 0x8001005F`. Derivados da newlib, **a confirmar** no
  hardware: `EAGAIN 0x8001000B`, `EBUSY 0x80010010`, `ENODEV 0x80010013`, `ENOTTY 0x80010019`, `ESPIPE 0x8001001D`,
  `EROFS 0x8001001E`, `ENOSYS 0x80010058`, `EALREADY 0x80010078`, `ECANCELED 0x8001008C`. Para confirmar: comparar
  com o `SceError` de um jogo que trate o código, ou com a tabela de errno da `libc.suprx` (a string de `strerror`
  fica ao lado do valor).
- O VitaSharp devolve `IO_EBADF 0x80010009` para fd inválido; o Vita3K usa `EBADFD 0x80010051`. Qual o firmware
  devolve **está a confirmar** (teste: `sceIoClose(0x7FFFFFFF)` num homebrew no console). Mantenha `IO_EBADF` até lá,
  para ficar coerente com o código atual.
- **Divergência encontrada fora do escopo** (avisar o pacote A): em `Kernel/KernelObjects.cs`,
  `SceError.INVALID_ARGUMENT = 0x80020003` e `ILLEGAL_ADDR = 0x8002000C`, mas `psp2/kernel/error.h` e o Vita3K dão
  `SCE_KERNEL_ERROR_NOSYS = 0x80020003`, `UNSUP = 0x80020004`, `INVALID_ARGUMENT = 0x80020005` e
  `ILLEGAL_ADDR = 0x80020006`. As funções novas deste pacote que precisarem desses códigos devem usar os valores do
  header (crie `SceError.UNSUP`/`NOSYS` e corrija os existentes num commit separado, rodando os cenários).
- **Timestamps de `SceIoStat`**: `IoFileMgr.WriteStat` grava hora local (`CreationTime`, sem `Utc`); o Vita3K grava
  UTC. Use o mesmo critério do `WriteStat` nas funções novas (Chstat/Getstat async) e anote: **a confirmar** com
  `sceIoGetstat` + `sceRtcGetCurrentClockLocalTime` no console.
- **Custo**: tudo que começa com `sceIo` já cai em 12.000 instruções em `HleFunction.ClassifyCost`. `_sceFios*` e
  `NID_023EAA62` caem no padrão de 2.000, o que serve.
- Todos os nomes da lista alvo existem e são únicos em `Hle/Data/nids.txt`; basta `[Hle]`. A exceção é
  `NID_023EAA62`, que precisa de `[Hle("…", Nid = 0x023EAA62, Library = "SceLibKernel")]`. Veja a entrada para o
  nome do método.

---

## 1. `NID_023EAA62` e os dispositivos `tty0:`/`tty1:`

### NID_023EAA62
- [x] **Prioridade:** P0 (F1 2011 PCSE00002, Uncharted PCSA00029) · **Biblioteca:** SceLibKernel · **NID:** 0x023EAA62
- **Protótipo:** não existe em nenhum header do VitaSDK, nem em `nids.txt` (Vita3K). Inferido pela desmontagem:
  `void/int NID_023EAA62(const char *msg /*, r1..r3 = lixo */);`
- **O que a desmontagem mostrou** (F1, `libc.suprx` carregada em 0x81340000+, função `openStdio` em 0x813405E4):
  ```
  r0="tty0:" (0x81374908), r1=3 (SCE_O_RDWR), r2=0 → sceIoOpen          (stub 0x8136D840, NID 0x6C60AC61)
  str r0,[0x813822FC]                        ; fd de stdin
  if (r0 < 0) { r0="stdin : open failed" (0x81374910); blx 0x8136D7D0 ; ← NID_023EAA62, LR=0x8134063D
                r7 = -1 }
  r0="tty0:", r1=3 → sceIoOpen; str [0x81382300]   ; stdout
  if (<0) { r0="stdout : open failed" (0x81374924); blx 0x8136D7D0; r7=-1 }
  r0="tty1:" (0x8137493C), r1=3 → sceIoOpen; str [0x81382304]   ; stderr
  if (<0) { r0="stderr : open failed" (0x81374944); blx 0x8136D7D0; r7=-1 }
  if (r7 < 0) sceClibPrintf("openStdio : failed\n")  (stub 0x8136D8E0, NID 0xFA26BC62)
  ```
  O stub em 0x8136D7D0 é `svc 0x190 / bx lr / .word 0x023EAA62`. O único argumento carregado é `r0` = mensagem
  **sem `\n`**. O valor de retorno é **ignorado**: a libc define `r7=-1` de qualquer forma. Só há um aviso
  "Unimplemented" no log por causa do `WarnOnce`; na prática a função é chamada 3 vezes.
- **Comportamento (inferido):** imprime uma mensagem de diagnóstico no console de debug, do tipo `puts`/print de
  kernel sem formatação. Como a única chamada observada ignora o retorno e passa uma string literal sem `%`,
  qualquer implementação que registre a string e devolva 0 é correta para este caller. Isso vale seja qual for o
  nome real.
- **Retorno/erros:** inferido `0` (SCE_OK). Não observado.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** em `Hle/Modules/Clib.cs` (perto de `sceClibPrintf`), ou em `ProcessMgr.cs`:
  ```csharp
  [Hle("NID_023EAA62", Nid = 0x023EAA62, Library = "SceLibKernel")]
  int Nid023EAA62(string msg) { Log.Info("[guest] " + msg.TrimEnd('\n')); return 0; }
  ```
  Não use `CFormat` (não está confirmado que seja printf). Se quiser investigar, registre `r1..r3` em `Log.Debug`
  (o binder aceita parâmetros extras `uint a1, uint a2, uint a3`).
- **Plano de investigação para descobrir o nome real** (opcional; a implementação acima já resolve):
  1. Confirmar que Uncharted usa a mesma `libc.suprx`: rodar
     `VitaSharp.Desktop run PCSA00029 --seconds=20 --bt NID_023EAA62 --bt-depth 8` (depois de implementar,
     `BacktraceHle` só dispara para funções registradas) e conferir se o LR cai no mesmo offset relativo
     (`openStdio` + 0x59). Também dá para usar `--trace-hle-only NID_023EAA62+sceIoOpen` para ver a sequência com
     argumentos.
  2. Procurar outros call sites na libc: desmontar o `.text` da `libc.suprx` com `VitaSharp.Desktop disasm` e
     calcular os alvos de cada `BLX imm` (o comando hoje só imprime o opcode; os alvos acima foram calculados à mão).
     Se houver call site com argumento formatado (`%`) ou com `r1` útil, isso decide entre `puts` e `printf`.
  3. Pegar o `libkernel.suprx` do firmware (`os0:us/libkernel.suprx`, de um PUP 3.60 ou de um dump), localizar
     0x023EAA62 na tabela de exports e desmontar o export. Ele mostra qual syscall/função do kernel é chamada
     (provavelmente o caminho de `sceKernelPrintf`/debug-print do SceSysmem). Esse é o método definitivo.
     `self2elf` do VitaSharp só decripta NPDRM; módulos de sistema precisam de outra ferramenta.
  4. Candidatos, por ordem de probabilidade: (a) uma variante `puts` de debug da SceLibKernel do SDK 0.9x/1.0x
     (`sceClibPuts`/`sceKernelDebugPuts`-like), removida dos headers depois; (b) um `sceKernelPrintf` sem varargs. As
     hipóteses "variante de `sceIoOpen`", "`sceKernelGetStdin`" e "ioctl de tty" **estão descartadas**: a função
     recebe só a string de erro e é chamada **depois** da falha do open, com o resultado ignorado.
- **Validação:** `VitaSharp.Desktop run PCSE00002 --seconds=20` (ou o cenário `tests/scenarios/f1_boot.txt`). O log
  não pode mais ter `Unimplemented HLE function SceLibKernel::NID_023EAA62`. Com o mapeamento de tty abaixo, também
  não podem aparecer `IO: unmapped path 'tty0:'` nem `[guest] openStdio : failed`. O mesmo vale para
  `run PCSA00029`.

### Mapeamento de `tty0:`/`tty1:` (parte do P0 acima)
- [x] **Prioridade:** P0 (F1 2011, Uncharted) · **Biblioteca:** SceIofilemgr (`sceIoOpen`/`Read`/`Write`/`Close`) ·
  **NID:** n/a (comportamento de `sceIoOpen` 0x6C60AC61)
- **Comportamento:** no Vita, `tty0:`..`tty3:` são dispositivos de caractere do console de debug (deci/UART). A
  libc LLE abre `tty0:` como stdin e stdout e `tty1:` como stderr, todos com `SCE_O_RDWR` (3), e guarda os fds em
  globais. Depois, todo `printf`/`fwrite(stdout)` do jogo vira `sceIoWrite(fd_tty, ...)`. Em retail, o open dá
  certo e a saída é descartada. O Vita3K aceita `tty0..3`, cria um fd de TTY (IN se `O_RDONLY`, OUT se `O_WRONLY`)
  e loga as escritas.
- **Retorno/erros:** `sceIoOpen("ttyN:")` devolve um fd válido (>0). `sceIoWrite` devolve `size`. `sceIoRead` em
  tty: o VitaSharp não tem entrada de console, então deve devolver `0` (EOF). `sceIoLseek` em tty: `ESPIPE`
  **a confirmar**; devolver 0 também é aceitável. `sceIoClose`: 0.
- **Implementação no VitaSharp:**
  - `Io/Vfs.cs`: acrescentar `public static bool IsTty(string vitaPath, out int index)`. Retorna true quando
    `Split(path)` dá device `tty0`..`tty3` (sem diferenciar maiúsculas) e `rel` vazio. **Não** monte tty em
    `_mounts`, porque `Mount` cria um diretório no host.
  - `Hle/Modules/IoFileMgr.cs`:
    - `OpenFile` ganha `public int Tty = -1;` (com `Stream = null`).
    - `sceIoOpen`: antes de `HostPath`, `if (Vfs.IsTty(path, out int n)) { int fd = Interlocked.Increment(ref _nextFd); _files[fd] = new OpenFile { VitaPath = path, Tty = n }; return fd; }`.
      Faça isso antes da resolução FIOS, para não poluir o log com "unmapped path".
    - `sceIoWrite`: `if (f.Tty >= 0) { Log.Info((f.Tty == 1 ? "[guest stderr] " : "[guest stdout] ") + texto); return (int)size; }`.
      Hoje a escrita só é aceita para os fds fixos 0x11/0x12; mantenha esses dois, porque `sceKernelGetStdout`
      (ProcessMgr.cs:29) devolve 0x11. Opcional: bufferizar por fd até `\n`, porque a libc às vezes escreve linhas
      em pedaços.
    - `sceIoRead`: `if (f.Tty >= 0) return 0;`. Também trate os fds 0x10..0x12 (hoje devolvem EBADF).
    - `sceIoClose`: já funciona (`TryRemove`, `Stream?.Dispose()`).
    - `sceIoLseek`/`Pread`/`Pwrite`/`GetstatByFd` em tty: hoje caem em `f.Stream == null → EBADF`. Aceitável; se
      algum jogo reclamar, devolva 0.
- **Validação:** mesma da entrada anterior. Além disso, as linhas `[guest stdout]` que a libc imprimir devem aparecer
  no `last.log` durante o boot de F1.

---

## 2. Overlays do SceFios2Kernel (P0)

Contexto: as `libfios2.suprx` LLE de F1 e Uncharted (SDK anterior ao 1.80) usam a ABI "syscall" antiga
`_sceFiosKernelOverlay*`, em que os argumentos extras vão numa struct de opções. A tabela de overlays já existe em
`Fios2User` (`_overlays`, `Resolve`, `sceFiosOverlayAddForProcess02` etc.) e as `_sceFiosKernelOverlay*` atuais
apenas delegam para as funções `…02`. Implemente as que faltam no mesmo arquivo `Hle/Modules/Fios2User.cs`, na seção
`SceFios2Kernel`, com o atributo `[Hle("_sceFiosKernelOverlayXxx")]`, igual às existentes.

Layout de `SceFiosOverlay` (`psp2common/fios2.h`, 0x258 bytes), que é o `SceFiosKernelOverlay` dos protótipos
antigos (o header declara a struct só por forward declaration; o uso atual em `Fios2User` mostra que os dois
layouts são iguais):

| Offset | Tipo | Campo |
| --- | --- | --- |
| 0x00 | u8 | `type` (0 OPAQUE, 1 TRANSLUCENT, 2 NEWER, 3 WRITABLE) |
| 0x01 | u8 | `order` (0x00–0x7F app, 0x80–0xFF privilegiado) |
| 0x02 | u16 | `dst_len` (preenchido pelo FIOS) |
| 0x04 | u16 | `src_len` (preenchido pelo FIOS) |
| 0x06 | u16 | `unk2` |
| 0x08 | SceUID | `pid` |
| 0x0C | s32 | `id` (atribuído pelo FIOS) |
| 0x10 | char[292] | `dst` |
| 0x134 | char[292] | `src` |

Erro "id desconhecido / parâmetro inválido": o código atual usa `0x80820001` (prefixo da facility FIOS). **A
confirmar**: o valor exato que o firmware 3.60 devolve não está em nenhum header. Para descobrir, chame
`_sceFiosKernelOverlayGetInfo(0x7FFF, buf)` num homebrew no console, ou veja como a `libfios2` testa o retorno
(desmontar o caller).

Lacunas que já existem no código e que valem para todas as entradas abaixo: `Resolve()` ignora `type` (trata tudo
como OPAQUE) e o parâmetro `resolve_for_write`; e `GetInfoForProcess02` não preenche `dst_len`, `src_len`, `pid` e
`id`. Corrija junto com `GetInfo`.

### _sceFiosKernelOverlayGetInfo
- [x] **Prioridade:** P0 (F1 2011, Uncharted) · **Biblioteca:** SceFios2Kernel · **NID:** 0xF44F3505
- **Protótipo:** `int _sceFiosKernelOverlayGetInfo(SceFiosKernelOverlayID id, SceFiosKernelOverlay *out_overlay);` (`psp2/fios2kernel.h`)
- **Comportamento:** copia para `out_overlay` o overlay `id` do processo atual. É o mesmo que
  `_sceFiosKernelOverlayGetInfoForProcess(pid_atual, id, out)`.
- **Retorno/erros:** 0; id inexistente → erro FIOS (o código atual usa `0x80820001`, a confirmar); `out_overlay` NULL
  → `SCE_KERNEL_ERROR_ILLEGAL_ADDR` (0x80020006 pelo header, a confirmar).
- **Estruturas:** `SceFiosOverlay` (tabela acima).
- **Implementação no VitaSharp:** `[Hle("_sceFiosKernelOverlayGetInfo")] int …(int id, uint outOverlay) => sceFiosOverlayGetInfoForProcess02(0, id, outOverlay);`.
  Aproveite para completar `GetInfoForProcess02`: `dst_len`=`Dst.Length`, `src_len`=`Src.Length`, `pid` (0 ou o UID
  do processo, se `ProcessMgr` tiver um), `id`.
- **Validação:** sem chamada em runtime ainda. Teste unitário: Add → GetInfo → comparar campos. Em jogo:
  `imports PCSE00002` não deve listá-la como faltante.

### _sceFiosKernelOverlayGetList
- [x] **Prioridade:** P0 (F1 2011, Uncharted) · **Biblioteca:** SceFios2Kernel · **NID:** 0x9379E2D5
- **Protótipo:** `int _sceFiosKernelOverlayGetList(SceUID pid, SceUInt8 min_order, SceUInt8 max_order, SceFiosGetListSyscallArgs *args);` (`psp2/fios2kernel.h`)
- **Comportamento:** lista os IDs dos overlays de `pid` com `min_order <= order <= max_order`, na ordem de resolução
  (ordem crescente de `order`; dentro do mesmo order, ordem de inserção). Escreve no máximo `max_ids` IDs e informa o
  total encontrado, que pode ser maior que `max_ids`.
- **Retorno/erros:** 0; `args` NULL → erro de argumento (a confirmar); `max_ids > 128` ou buffer `> 0x200` bytes →
  inválido (limites documentados para a versão `02` no FW 3.60; para a ABI antiga, a confirmar).
- **Estruturas:** `SceFiosGetListSyscallArgs` (0x18). O header só nomeia `out_ids`. O resto foi **inferido** do
  `sceFiosKernelOverlayGetList02_opt` (`psp2/fios2kernel02.h`), que tem o mesmo tamanho:

  | Offset | Campo (inferido) |
  | --- | --- |
  | 0x00 | `SceFiosOverlayID *out_ids` |
  | 0x04 | `SceSize max_ids` |
  | 0x08 | `SceSize *actual_ids` (opcional) |
  | 0x0C | `SceSize out_ids_buffer_size` (bytes) |
  | 0x10 | reservado[2] |

  Para confirmar: com `--trace-hle-only _sceFiosKernelOverlayGetList`, faça dump de `args` (0x18 bytes) na primeira
  chamada e confira se +0x0C == +0x04 × 4.
- **Implementação no VitaSharp:** ler `outIds = R32(args)`, `maxIds = R32(args+4)`, `actual = R32(args+8)` e chamar
  a lógica de `sceFiosOverlayGetList02(pid, min, max, outIds, maxIds, actual)`. Limite também pelo
  `out_ids_buffer_size / 4`.
- **Validação:** teste unitário com 3 overlays de orders diferentes e faixas parciais.

### _sceFiosKernelOverlayModify
- [x] **Prioridade:** P0 (F1 2011, Uncharted) · **Biblioteca:** SceFios2Kernel · **NID:** 0x6D6CDE05
- **Protótipo:** `int _sceFiosKernelOverlayModify(SceFiosKernelOverlayID id, const SceFiosKernelOverlay *new_value);` (`psp2/fios2kernel.h`)
- **Comportamento:** substitui `type`, `order`, `dst` e `src` do overlay `id` do processo atual pelos de
  `new_value`. Normaliza os caminhos como no Add e mantém o `id`. Se o `order` mudar, o overlay volta para a posição
  certa na lista.
- **Retorno/erros:** 0; id inexistente → mesmo erro do GetInfo; `new_value` NULL → ILLEGAL_ADDR (a confirmar);
  `order >= 0x80` vindo de app → erro de permissão (a confirmar; o comentário do header diz que 0x80–0xFF são
  privilegiados).
- **Estruturas:** `SceFiosOverlay`.
- **Implementação no VitaSharp:** `Fios2User`: como o `record Overlay` é imutável, remova o antigo e insira
  `new Overlay(id, type, order, Standardize(dst), Standardize(src))` na posição de order, com `lock (_overlays)`.
  Implemente também o `sceFiosOverlayModifyForProcess02`, que hoje é stub e devolve 0, chamando a mesma rotina.
  Log `Info` igual ao do Add.
- **Validação:** teste unitário Add → Modify(src novo) → `Resolve` devolve o src novo.

### _sceFiosKernelOverlayModifyForProcess
- [x] **Prioridade:** P0 (F1 2011, Uncharted) · **Biblioteca:** SceFios2Kernel · **NID:** 0x6DF2FC05
- **Protótipo:** `int _sceFiosKernelOverlayModifyForProcess(SceUID target_process, SceFiosKernelOverlayID id, const SceFiosKernelOverlay *new_value);` (`psp2/fios2kernel.h`)
- **Comportamento:** igual ao `_sceFiosKernelOverlayModify`, para o processo `target_process`. O VitaSharp tem um só
  processo, então ignore `pid` (como fazem as demais `…ForProcess`).
- **Retorno/erros:** como Modify.
- **Estruturas:** `SceFiosOverlay`.
- **Implementação no VitaSharp:** delega para a rotina comum do Modify.
- **Validação:** como Modify.

### Família `_sceFiosKernelOverlayDH*` (directory handles mesclados)

O "DH" do FIOS é um handle de diretório que **mescla** as entradas do caminho pedido com as dos overlays
aplicáveis (por exemplo, `app0:` e um patch). A `libfios2` usa isso em `sceFiosDHOpenSync`/`sceFiosDHReadSync`.

**Fato importante do header:** `SceFiosDHOpenSyncSyscallArgs.to_order` traz a nota "On FW 3.60, the function
returns an unsupported-operation error before using this value". Ou seja, no firmware em que os jogos rodam hoje,
`_sceFiosKernelOverlayDHOpenSync` **sempre falha** com "não suportado", e a `libfios2` precisa ter um fallback
(provavelmente `sceIoDopen` comum). Daí duas opções:

- **Modo A (recomendado, fiel ao 3.60):** `DHOpenSync` devolve o erro "não suportado" e não escreve `*out_dh`. As
  demais DH* recebem um `dh` que nunca foi criado e devolvem `EBADF` (o código exato é a confirmar; o 3.60
  provavelmente também devolve "não suportado" sem nem olhar o dh).
- **Modo B (só se algum jogo exigir):** implementar a mesclagem: tabela `Dictionary<int, MergedDir>` em `Fios2User`,
  com a união das entradas de `Resolve(path)` para cada overlay com order em `[from_order, to_order]` mais o
  diretório original, sem nomes duplicados (vence o de menor order).

Código de "não suportado": **a confirmar**. Candidatos: `SCE_KERNEL_ERROR_UNSUP 0x80020004` (o mais provável para
"unsupported-operation" vindo do kernel) ou `EOPNOTSUPP 0x8001005F`. Para confirmar: desmontar na `libfios2.suprx`
o caller do stub de 0x5D6A1CCE e ver contra qual constante o retorno é comparado antes de cair no fallback. Outra
via é implementar o Modo A com `0x80020004`, rodar F1/Uncharted com `--trace-hle-only _sceFiosKernelOverlayDHOpenSync+sceIoDopen`
e conferir se aparece um `sceIoDopen` logo depois (sinal de fallback).

> **Implementado (2026-10-19): Modo A** com `SCE_KERNEL_ERROR_UNSUP 0x80020004` em todas as DH*. F1 (cenário de boot de 90 s) e Uncharted (1500 quadros) não chamaram `DHOpenSync`; o `WarnOnce` avisa se algum jogo chamar.

Structs ausentes no header (`SceFiosNativeStat`, `SceFiosNativeDirEntry`): **inferido** que "Native" é o tipo de
plataforma, ou seja, `SceIoStat` (0x58) e `SceIoDirent` (0x160). Só importa no Modo B. Confirmar pelo tamanho que a
`libfios2` reserva na pilha antes de chamar `DHReadSync`.

### _sceFiosKernelOverlayDHOpenSync
- [x] **Prioridade:** P0 (F1 2011, Uncharted) · **Biblioteca:** SceFios2Kernel · **NID:** 0x5D6A1CCE
- **Protótipo:** `int _sceFiosKernelOverlayDHOpenSync(SceFiosKernelOverlayDH *out_dh, const char *path, SceUInt8 from_order, SceFiosDHOpenSyncSyscallArgs *args);` (`psp2/fios2kernel.h`)
- **Comportamento:** FW 3.60: devolve "não suportado" antes de ler `args->to_order`. Modo B: abre o diretório
  mesclado e escreve o handle em `*out_dh`.
- **Retorno/erros:** Modo A: erro "não suportado" (a confirmar, ver acima). Modo B: 0; `ENOENT` se nem o caminho
  nem nenhum overlay existir; `ENOTDIR` se for arquivo.
- **Estruturas:** `SceFiosDHOpenSyncSyscallArgs` (0xC): +0x00 `u8 to_order`, +0x04 `int padding[2]`.
- **Implementação no VitaSharp:** `Fios2User`, `[Hle("_sceFiosKernelOverlayDHOpenSync")] int …(Ptr<int> outDh, string path, int fromOrder, uint args)`.
  `from_order` é u8 em r2; leia como `int` e faça `& 0xFF`. Log `WarnOnce` com o caminho, para saber se algum
  jogo chega a chamar.
- **Validação:** `imports PCSE00002`/`PCSA00029` sem a função na lista de faltantes; boot de F1 (`f1_boot.txt`) e
  Uncharted sem regressão. Se a chamada aparecer no log, confirme se o fallback funciona (o diretório é listado via
  `sceIoDopen`).

### _sceFiosKernelOverlayDHReadSync
- [x] **Prioridade:** P0 (F1 2011, Uncharted) · **Biblioteca:** SceFios2Kernel · **NID:** 0x2F06ADC6
- **Protótipo:** `int _sceFiosKernelOverlayDHReadSync(SceFiosKernelOverlayDH dh, SceFiosNativeDirEntry *out_entry);` (`psp2/fios2kernel.h`)
- **Comportamento:** lê a próxima entrada do DH mesclado. Modo A: `dh` nunca é válido.
- **Retorno/erros:** Modo A: `EBADF` (ou "não suportado", a confirmar). Modo B: valor > 0 se escreveu uma entrada, 0
  no fim (mesma convenção de `sceIoDread`, inferida).
- **Estruturas:** `SceFiosNativeDirEntry` = `SceIoDirent` (0x160, inferido): `d_stat` +0x00 (0x58), `d_name` +0x58
  (256), `d_private` +0x158, `dummy` +0x15C.
- **Implementação no VitaSharp:** Modo B reaproveita `IoFileMgr.WriteStat`, que hoje é `static` privado. Torne-o
  `internal static` para o `Fios2User` usar.
- **Validação:** como DHOpenSync.

### _sceFiosKernelOverlayDHStatSync
- [x] **Prioridade:** P0 (F1 2011, Uncharted) · **Biblioteca:** SceFios2Kernel · **NID:** 0x759EBEE6
- **Protótipo:** `int _sceFiosKernelOverlayDHStatSync(SceFiosKernelOverlayDH dh, SceFiosNativeStat *out_stat);` (`psp2/fios2kernel.h`)
- **Comportamento:** stat do diretório aberto pelo DH.
- **Retorno/erros:** Modo A: `EBADF`/"não suportado". Modo B: 0 e `SceIoStat` do diretório resolvido.
- **Estruturas:** `SceFiosNativeStat` = `SceIoStat` (0x58, inferido).
- **Implementação no VitaSharp:** `Fios2User`. Modo B: `WriteStat(out, hostDir)`.
- **Validação:** como DHOpenSync.

### _sceFiosKernelOverlayDHChstatSync
- [x] **Prioridade:** P0 (F1 2011, Uncharted) · **Biblioteca:** SceFios2Kernel · **NID:** 0xF6A3E335
- **Protótipo:** `int _sceFiosKernelOverlayDHChstatSync(SceFiosKernelOverlayDH dh, const SceFiosNativeStat *new_stat, unsigned int cbit);` (`psp2/fios2kernel.h`)
- **Comportamento:** chstat do diretório do DH, com os bits `SCE_CST_*` de `cbit` (ver `sceIoChstat`).
- **Retorno/erros:** Modo A: `EBADF`/"não suportado". Modo B: como `sceIoChstat`.
- **Estruturas:** `SceIoStat`; `cbit` usa `SCE_CST_MODE 0x1`, `SIZE 0x4`, `CT 0x8`, `AT 0x10`, `MT 0x20`.
- **Implementação no VitaSharp:** Modo B chama o helper de chstat de `IoFileMgr` (ver entrada `sceIoChstat`).
- **Validação:** como DHOpenSync.

### _sceFiosKernelOverlayDHSyncSync
- [x] **Prioridade:** P0 (F1 2011, Uncharted) · **Biblioteca:** SceFios2Kernel · **NID:** 0x2A9724C9
- **Protótipo:** `int _sceFiosKernelOverlayDHSyncSync(SceFiosKernelOverlayDH dh, int flag);` (`psp2/fios2kernel.h`)
- **Comportamento:** sync do diretório (o equivalente a `sceIoSyncByFd` para o DH).
- **Retorno/erros:** Modo A: `EBADF`/"não suportado". Modo B: 0 (nada a fazer no host).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Fios2User`.
- **Validação:** como DHOpenSync.

### _sceFiosKernelOverlayDHCloseSync
- [x] **Prioridade:** P0 (F1 2011, Uncharted) · **Biblioteca:** SceFios2Kernel · **NID:** 0x021B4AF7
- **Protótipo:** `int _sceFiosKernelOverlayDHCloseSync(SceFiosKernelOverlayDH dh);` (`psp2/fios2kernel.h`)
- **Comportamento:** fecha o DH.
- **Retorno/erros:** Modo A: `EBADF`/"não suportado". Modo B: 0; dh desconhecido → `EBADF`.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Fios2User`. Modo B: `_dhs.TryRemove`.
- **Validação:** como DHOpenSync.

### _sceFiosKernelOverlayThreadSetDisabled
- [x] **Prioridade:** STUB · **Biblioteca:** SceFios2Kernel · **NID:** 0x3E9172EA
- **Protótipo:** `int _sceFiosKernelOverlayThreadSetDisabled(SceInt32 disabled);` (`psp2/fios2kernel.h`)
- **Comportamento:** liga/desliga os overlays de **aplicativo** (order < 0x80) para a thread atual. Segundo o
  header, com overlays desligados, `ResolveWithRangeSync` troca o `min_order` pedido por 0x80, ou seja, só valem os
  overlays privilegiados. A `libfios2` usa isso para acessar o caminho "cru" (por exemplo, para montar o próprio
  overlay sem recursão).
- **Retorno/erros:** 0 (inferido: talvez devolva o estado anterior; a confirmar com o retorno observado num trace).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** hoje devolve 0 sem efeito (IL 2). Guarde a flag por thread:
  `[ThreadStatic] static bool _appOverlaysDisabled` não serve, porque a mesma GuestThread pode trocar de thread
  host. Use `ConditionalWeakTable<GuestThread, StrongBox<bool>>` ou um campo novo em `GuestThread`. Aplique em
  `Fios2User.Resolve` (min efetivo `= Math.Max(minOrder, 0x80)` quando a flag da `GuestThread.Current` estiver
  ligada), o que cobre `ResolveSync`, `ResolveWithRangeSync` e a resolução automática de `IoFileMgr.HostPath`.
  `sceFiosOverlayThreadSetDisabled02` deve usar a mesma rotina.
- **Validação:** teste unitário: overlay order 0 `app0:` → `ux0:x`; com a flag ligada, `Resolve("app0:a")` devolve
  `app0:a`. Em jogo: `--trace-hle-only _sceFiosKernelOverlayThreadSetDisabled` em F1 mostra se e quando é usado.

> **Implementado (2026-10-19):** flag por `GuestThread` em `Fios2User` (`ConditionalWeakTable`), aplicada em `Resolve`. Trace de F1: as threads `SceFiosIO`/`SceFiosCallback` chamam `SetDisabled(1)` ao iniciar e nunca religam (elas recebem caminhos já resolvidos); a `main` alterna 1/0 em volta de cada resolução. Retorno 0 (o r1 da chamada traz lixo, não um ponteiro de estado anterior).

### _sceFiosKernelOverlayThreadIsDisabled
- [x] **Prioridade:** STUB · **Biblioteca:** SceFios2Kernel · **NID:** 0x629F4FE4
- **Protótipo:** `int _sceFiosKernelOverlayThreadIsDisabled(void);` (`psp2/fios2kernel.h`)
- **Comportamento:** devolve 1 se os overlays de app estão desligados na thread atual, senão 0.
- **Retorno/erros:** 0/1.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** lê a flag da entrada anterior (o mesmo vale para `sceFiosOverlayThreadIsDisabled02`).
- **Validação:** teste unitário Set(1) → IsDisabled()==1 → Set(0) → 0.

---

## 3. Stubs síncronos de I/O (STUB)

Critério: a tabela `vitasharp-implemented.tsv` mostra IL ≤ 3 para `sceIoChstat`, `sceIoChstatByFd`, `sceIoIoctl`
e `sceIoSync`. As demais funções de I/O implementadas (`sceIoDevctl` IL 73, `sceIoSyncByFd` IL 43 etc.) têm corpo
real e ficam fora desta lista.

### sceIoChstat
- [x] **Prioridade:** STUB · **Biblioteca:** SceLibKernel · **NID:** 0x29482F7F
- **Protótipo:** `int sceIoChstat(const char *file, SceIoStat *stat, int bits);` (`psp2/io/stat.h`). A syscall por
  baixo é `_sceIoChstat(name, stat, bits, sceIoChstatOpt*)` (`psp2/io/fcntl.h`).
- **Comportamento:** altera os atributos de `file` conforme `bits`:
  `SCE_CST_MODE 0x01` → `st_mode` (no VitaSharp, só o bit de escrita `SCE_S_IWUSR 0200` importa);
  `SCE_CST_SIZE 0x04` → trunca/estende para `st_size` (suporte no firmware **a confirmar**; o exFAT do ux0
  provavelmente aceita);
  `SCE_CST_CT 0x08`, `SCE_CST_AT 0x10`, `SCE_CST_MT 0x20` → criação/acesso/modificação a partir dos `SceDateTime`.
  Bits não reconhecidos são ignorados. Jogos usam isso em saves (ajustar mtime, tirar read-only antes de
  sobrescrever). O stub atual devolve 0 sem aplicar nada, e um `Getstat` posterior mostra os valores antigos.
- **Retorno/erros:** 0; `ENOENT 0x80010002` se não existir; `EROFS 0x8001001E` (a confirmar) em montagens só-leitura
  (`app0:`, `vs0:`, `os0:`); `stat` NULL → `EINVAL 0x80010016` (a confirmar).
- **Estruturas:** `SceIoStat` (0x58): +0x00 `st_mode`, +0x04 `st_attr`, +0x08 `st_size` (s64), +0x10 `st_ctime`,
  +0x20 `st_atime`, +0x30 `st_mtime` (`SceDateTime` de 16 bytes: u16 year, month, day, hour, minute, second; u32
  microsecond), +0x40 `st_private[6]`.
- **Implementação no VitaSharp:** `IoFileMgr.cs`. Crie `static DateTime ReadDateTime(uint p)`, o inverso de
  `WriteDateTime`, com o mesmo critério local/UTC. Crie também um helper `int ApplyChstat(string host, uint stat, int bits, FileStream? fs)`:
  `File.SetCreationTime`/`SetLastAccessTime`/`SetLastWriteTime` (ou as variantes de `Directory`);
  `FileInfo.IsReadOnly = (mode & 0x80) == 0` para MODE; `fs?.SetLength` ou `using var f = File.Open(host, FileMode.Open); f.SetLength(size)` para SIZE.
  Resolva o caminho com `HostPath(path, write: true)`. Rejeite com EROFS os devices só-leitura, com uma lista no
  próprio `IoFileMgr`.
- **Validação:** teste unitário: criar arquivo em `ux0:`, Chstat MT=2010-01-01 → Getstat devolve 2010-01-01. Em
  jogo: `--trace-hle-only sceIoChstat` durante um save (GoW, F1) para ver quais bits são usados.

### sceIoChstatByFd
- [x] **Prioridade:** STUB · **Biblioteca:** SceLibKernel · **NID:** 0x6E903AB2
- **Protótipo:** `int sceIoChstatByFd(SceUID fd, const SceIoStat *buf, unsigned int cbit);` (`psp2/io/stat.h`)
- **Comportamento:** como `sceIoChstat`, sobre o arquivo aberto em `fd`.
- **Retorno/erros:** 0; fd inválido → `IO_EBADF`; o resto como Chstat.
- **Estruturas:** `SceIoStat`.
- **Implementação no VitaSharp:** `IoFileMgr.cs`: `_files[fd].Stream.Name` é o caminho no host. Chame `ApplyChstat`
  passando o próprio `FileStream` para SIZE. Antes de mudar tempos, faça `Flush`, senão o fechamento do stream
  sobrescreve o mtime.
- **Validação:** como Chstat, usando fd.

### sceIoSync
- [x] **Prioridade:** STUB · **Biblioteca:** SceLibKernel · **NID:** 0x98ACED6D
- **Protótipo:** `int sceIoSync(const char *device, unsigned int flags);` (`psp2/io/fcntl.h`). A syscall é
  `_sceIoSync(device, flags, sceIoSyncOpt*)`.
- **Comportamento:** grava em disco os dados em cache de todos os arquivos abertos no dispositivo (`"savedata0:"`,
  `"ux0:"`…). Jogos chamam depois de gravar o save, antes de mostrar "salvo". O stub atual devolve 0 sem fazer
  flush. O efeito real é pequeno (o SO do host acaba gravando), mas um crash do emulador logo depois do save pode
  perder dados.
- **Retorno/erros:** 0; device desconhecido → `ENODEV 0x80010013` (a confirmar); `device` NULL → `EINVAL`.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `IoFileMgr.cs`: `var (dev, _) = Vfs.Split(device)`; para cada `_files` com
  `Vfs.Split(f.VitaPath).device` igual (sem diferenciar maiúsculas) e `Stream != null`, faça `lock (f) f.Stream.Flush(true)`.
  O `flags` é ignorado. `sceIoSyncByFd` deveria usar `Flush(true)` também (hoje usa `Flush()`, que não força o
  disco).
- **Validação:** `--trace-hle-only sceIoSync` num save; teste unitário de que não lança exceção com o arquivo aberto.

### sceIoIoctl
- [x] **Prioridade:** STUB · **Biblioteca:** SceLibKernel · **NID:** 0x54ABACFA
- **Protótipo:** `int sceIoIoctl(SceUID fd, unsigned int cmd, void *indata, int inlen, void *outdata, int outlen);` (`psp2/io/devctl.h`)
- **Comportamento:** comando específico do driver sobre um fd aberto. Não há comandos documentados para arquivos
  comuns no exFAT/PFS. O stub atual devolve 0 **sem validar o fd e sem registrar nada**, o que esconde chamadas que
  podem importar (por exemplo, ioctl em `tty`, ou consulta de setor/tamanho em `gro0:`).
- **Retorno/erros:** 0 para comandos aceitos; fd inválido → `IO_EBADF`; comando desconhecido → no firmware,
  provavelmente `EINVAL`/`EOPNOTSUPP` (**a confirmar**).
- **Estruturas:** dependem do comando.
- **Implementação no VitaSharp:** `IoFileMgr.cs`: valide o fd (`_files`, ou fds de tty/0x10–0x12). Faça
  `Log.WarnOnce($"ioctl{cmd:X}", $"sceIoIoctl fd={fd:X} cmd=0x{cmd:X} in={inlen} out={outlen} path={f.VitaPath}")`.
  Se `outdata != 0`, zere `outdata` (`outlen` bytes) e devolva 0. Mantenha o 0 até algum jogo mostrar um comando
  concreto.
- **Validação:** rodar os 7 jogos com o log e anotar os `cmd` que aparecerem. Documente cada um aqui quando
  aparecer.

---

## 4. I/O assíncrono — arquitetura comum (P1)

### O que se sabe e o que é inferido

- **Fato:** o VitaSDK não tem protótipo para nenhuma variante Async, exceto `sceIoIoctlAsync` (`psp2/io/devctl.h`),
  e `sceIoCancel(SceUID fd)` (`psp2/io/fcntl.h`). O Vita3K deixa todas como `UNIMPLEMENTED`. Também **não existem**
  `sceIoWaitAsync`, `sceIoPollAsync`, `sceIoGetAsyncStat` nem `sceIoSetAsyncCallback` no Vita: nenhum desses nomes
  está no banco do VitaSDK (`vitasdk-user-functions.tsv`). O modelo do PSP não se aplica.
- **Fato:** as funções de controle que existem são `sceIoComplete` (SceIofilemgr), `sceIoCompleteMultiple`
  (SceLibKernel, com a syscall `_sceIoCompleteMultiple`) e `sceIoCancel`. Para esperar, o Vita usa o mecanismo
  genérico de eventos do ThreadMgr: `sceKernelWaitEvent`/`WaitEventCB`/`PollEvent`/`WaitMultipleEvents`/
  `CancelEvent` (protótipo de `sceKernelWaitEvent` em `psp2/kernel/libkernel.h`). Todos estão faltando no
  VitaSharp e pertencem ao pacote A.
- **Inferido (a confirmar):**
  1. Toda `sceIoXxxAsync` devolve um **SceUID de operação** (`opid`, > 0) ou um erro < 0 de validação imediata. A
     operação roda em segundo plano.
  2. O `opid` é um objeto de evento: `sceKernelWaitEvent(opid, pattern, &res, &userData, &timeout)` bloqueia até o
     fim. Valor do bit de conclusão: **a confirmar** (candidato 0x1).
  3. `sceIoComplete(opid)` devolve o resultado final da operação, o mesmo que a versão síncrona devolveria (fd para
     Open, bytes para Read/Write, 0 para Close…), e libera o `opid`. Para Lseek (resultado de 64 bits), como o
     valor volta é a confirmar.
  4. Os callbacks chegam pelos `…CB` do mecanismo de eventos (`sceKernelWaitEventCB`, notificação via
     `sceKernelSetEventWithNotifyCallback`). Não há callback próprio de I/O.
  5. As variantes Async de SceLibKernel são wrappers das syscalls `_sceIoXxxAsync(…, opt)`, do mesmo jeito que
     `sceIoLseek` → `_sceIoLseek(fd, opt)`. As de SceIofilemgr (`sceIoReadAsync` etc.) já são as syscalls. Em ambos
     os casos, os argumentos públicos são os mesmos da versão síncrona.

  **Como confirmar:** nenhum dos 7 jogos importa Async. Quando aparecer um, rode com
  `--trace-hle-only sceIo+sceKernelWaitEvent+sceKernelPollEvent` e registre a sequência
  (Async → Wait/Poll(uid, pattern) → Complete(uid)), o pattern usado e o retorno esperado de Complete. Ajuste
  então os itens 2–3 e atualize esta seção. Antes disso, um homebrew de teste no console resolve as dúvidas (não
  existe exemplo no VitaSDK).

### Desenho para o VitaSharp

- **Arquivo:** transforme `IoFileMgr` em `partial` e crie `Hle/Modules/IoFileMgr.Async.cs`. Antes, refatore cada
  função síncrona em um helper interno sem `[Hle]` (`int DoOpen(string path, int flags, int mode)`,
  `int DoRead(int fd, uint buf, uint size)`, `long DoLseek(...)` etc.). O `[Hle]` síncrono só chama o helper, e o
  Async também.
- **Objeto de kernel:** `sealed class IoAsyncOp : KernelObject` em `Kernel/Sync.cs`, com campos `int Fd`,
  `long Result`, `bool Done`, `bool Cancelled`, `uint Pattern` (bits de evento) e `ulong UserData`. Registre com
  `K.Objects.Register` (UIDs separados dos fds, que vêm de `_nextFd` a partir de 0x100). Para que
  `sceKernelWaitEvent`/`PollEvent`/`WaitMultipleEvents` do pacote A funcionem sobre ele, defina uma interface comum,
  por exemplo `interface IKernelEvent { object Sync { get; } uint Pattern { get; } ulong UserData { get; } }`,
  implementada também pelo `SimpleEvent` existente. A espera usa `Wait.Until(op, s => (s.Pattern & bits) != 0, timeout, …)`,
  que já trata timeout, callbacks (`CallbackWait`) e liberação de core. Combine a interface com quem implementar o
  pacote A.
- **Execução, fase 1 (simples e determinística):** execute a operação **na hora**, no thread chamador, chamando o
  helper síncrono. Grave `Result`, faça `Done = true`, `Pattern |= bitConclusão`, `Monitor.PulseAll(op)`, e devolva o
  `opid`. A semântica observável fica correta (o resultado só é lido via Complete) e não há concorrência nova.
  Desvantagem: o chamador paga a latência de `IoTiming.Read`.
- **Execução, fase 2 (só se algum jogo depender do paralelismo):** uma fila por fd (`Channel<Func<long>>`)
  consumida por uma `Task` do host. A ordem é FIFO por fd, como no firmware (o `sceIoSetPriority` afeta só a ordem
  entre fds). `GuestMemory` é compartilhada, então escrever o buffer a partir do thread do host é seguro, desde que o
  jogo não mexa no buffer antes do Complete. Na conclusão, mesmo tratamento da fase 1, sob `lock (op)`.
- **Erros comuns a todas as Async:** o argumento inválido detectável sem I/O (fd desconhecido, ponteiro NULL)
  provavelmente volta direto do `XxxAsync` como valor < 0, sem criar `opid` (**a confirmar**). Erros de I/O (ENOENT
  no Open etc.) voltam pelo `sceIoComplete`. Na fase 1, adote: fd inexistente → retorno imediato `IO_EBADF`; todo o
  resto → `opid` com `Result` = erro.
- **Limite:** o firmware tem um número máximo de operações pendentes por processo (**a confirmar**). Não limite na
  fase 1.

> **Implementado (2026-10-19), fase 1:** `Hle/Modules/IoFileMgr.Async.cs`. `IoAsyncOp : KernelObject` (UID = opid, `Pattern` recebe o bit 0x1 na conclusão e o objeto recebe `PulseAll`, pronto para o `sceKernelWaitEvent` do pacote A); cada `XxxAsync` executa a versão síncrona na hora e devolve o opid; fd desconhecido → `IO_EBADF` imediato. `sceIoComplete` devolve o resultado (32 bits baixos no Lseek) e libera o opid (`UNKNOWN_EVENT_ID` se desconhecido). `sceIoCancel` aceita opid ou fd e devolve 0 (nada fica pendente na fase 1). `sceIoCompleteMultiple` continua sem registro até o layout ser confirmado. Prioridades: guardadas e devolvidas; padrão do processo 0 e faixa (só negativos rejeitados) a confirmar.

### Tabela das variantes Async

Todas as linhas seguem o mesmo template. Protótipo **inferido**, a não ser quando houver header indicado.
Retorno: `opid` (> 0) ou erro de validação imediata (< 0). O resultado final vem por `sceIoComplete`, com os mesmos
códigos da versão síncrona. Implementação: `IoFileMgr.Async.cs`, `[Hle]` + `Submit(fd, () => DoXxx(...))`.
Validação: teste unitário Async → `sceKernelWaitEvent` → `sceIoComplete` tem de dar o mesmo resultado da versão
síncrona. Em jogo, só quando algum título importar a função.

### sceIoOpenAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x6A7EA9FD
- **Protótipo:** `SceUID sceIoOpenAsync(const char *file, int flags, SceMode mode);` (sem header; inferido de `sceIoOpen`)
- **Comportamento:** abre o arquivo de forma assíncrona; o fd novo é o resultado do Complete. Se o fd já existe
  quando Async retorna (como no PSP) ou só passa a existir no Complete: **a confirmar**.
- **Retorno/erros:** `opid`; Complete → fd ou ENOENT/EEXIST/EACCES/EISDIR.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(-1, () => DoOpen(path, flags, mode))`. Copie `path` para uma `string`
  **antes** de enfileirar (o guest pode reutilizar o buffer).
- **Validação:** Open síncrono e Async no mesmo arquivo dão fds válidos e o mesmo conteúdo lido.

### sceIoCloseAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0x8EA3616A
- **Protótipo:** `int sceIoCloseAsync(SceUID fd);` (inferido)
- **Comportamento:** fecha `fd` depois que as operações pendentes nele terminarem.
- **Retorno/erros:** `opid` ou `IO_EBADF` imediato; Complete → 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(fd, () => sceIoClose(fd))`.
- **Validação:** Close async seguido de `sceIoRead(fd)` → EBADF depois do Complete.

### sceIoReadAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0x773EBD45
- **Protótipo:** `int sceIoReadAsync(SceUID fd, void *buf, SceSize nbyte);` (inferido de `sceIoRead`)
- **Comportamento:** lê `nbyte` bytes da posição atual e avança a posição.
- **Retorno/erros:** `opid`; Complete → bytes lidos.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(fd, () => DoRead(fd, buf, size))`. Contabiliza `BytesRead` e
  `IoTiming.Read`, como a versão síncrona.
- **Validação:** ler um arquivo conhecido do `app0:` e comparar o hash.

### sceIoWriteAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0xE0D63D2A
- **Protótipo:** `int sceIoWriteAsync(SceUID fd, const void *buf, SceSize nbyte);` (inferido)
- **Comportamento:** escreve na posição atual. Os fds de tty/stdout também são aceitos.
- **Retorno/erros:** `opid`; Complete → bytes escritos.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(fd, () => DoWrite(fd, buf, size))`. Na fase 2, copie o buffer do guest na
  submissão, porque o jogo pode reutilizá-lo depois de um Complete fora de ordem (a confirmar).
- **Validação:** escrever em `ux0:data/x`, Complete, ler de volta.

### sceIoPreadAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xA010141E
- **Protótipo:** `int sceIoPreadAsync(SceUID fd, void *buf, SceSize nbyte, SceOff offset);` (inferido de `sceIoPread`)
- **Comportamento:** lê em `offset` sem mover a posição do arquivo.
- **Retorno/erros:** `opid`; Complete → bytes lidos.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** parâmetro `long offset` (o binder põe em r2:r3). `Submit(fd, () => DoPread(...))`
  com `RandomAccess.Read`, como hoje. É a variante que um streamer de FIOS usaria; é a mais provável de aparecer.
- **Validação:** como ReadAsync, com offsets aleatórios.

### sceIoPwriteAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xED25BEEF
- **Protótipo:** `int sceIoPwriteAsync(SceUID fd, const void *buf, SceSize nbyte, SceOff offset);` (inferido)
- **Comportamento:** escreve em `offset` sem mover a posição.
- **Retorno/erros:** `opid`; Complete → bytes escritos.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(fd, () => DoPwrite(...))`.
- **Validação:** como WriteAsync.

### sceIoLseekAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xCAC5D672
- **Protótipo:** `int sceIoLseekAsync(SceUID fd, SceOff offset, int whence);` (inferido; syscall `_sceIoLseekAsync(fd, _sceIoLseekOpt*)`,
  com `_sceIoLseekOpt { SceOff offset; int whence; u32 unk; }`, layout do Vita3K `SceIofilemgr.h`)
- **Comportamento:** move a posição; o resultado é a nova posição (64 bits).
- **Retorno/erros:** `opid`; Complete → posição. Como os 64 bits voltam (`sceIoComplete` devolve `int`; talvez a
  ABI devolva r0:r1) é **a confirmar**. Na fase 1, guarde `long Result` e devolva os 32 bits baixos em Complete.
- **Estruturas:** `_sceIoLseekOpt` (0x10).
- **Implementação no VitaSharp:** `Submit(fd, () => sceIoLseek(fd, offset, whence))`.
- **Validação:** Lseek async SEEK_END → Complete == tamanho do arquivo.

### sceIoSyncAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xF7C7FBFE
- **Protótipo:** `int sceIoSyncAsync(const char *device, unsigned int flags);` (inferido de `sceIoSync`)
- **Comportamento:** `sceIoSync` assíncrono.
- **Retorno/erros:** `opid`; Complete → 0 / ENODEV.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(-1, () => DoSync(dev, flags))` (depende da entrada STUB `sceIoSync`).
- **Validação:** como `sceIoSync`.

### sceIoSyncByFdAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0x7E1367CB
- **Protótipo:** `int sceIoSyncByFdAsync(SceUID fd, int flag);` (inferido)
- **Comportamento:** `sceIoSyncByFd` assíncrono.
- **Retorno/erros:** `opid` / `IO_EBADF`; Complete → 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(fd, () => sceIoSyncByFd(fd, flag))`.
- **Validação:** unitário.

### sceIoGetstatAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x82B20B41
- **Protótipo:** `int sceIoGetstatAsync(const char *file, SceIoStat *stat);` (inferido)
- **Comportamento:** `sceIoGetstat` assíncrono; `stat` é preenchido antes da conclusão.
- **Retorno/erros:** `opid`; Complete → 0 / ENOENT.
- **Estruturas:** `SceIoStat` (0x58).
- **Implementação no VitaSharp:** `Submit(-1, () => sceIoGetstat(path, stat))`.
- **Validação:** unitário comparando com a versão síncrona.

### sceIoGetstatByFdAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0x5167AC1E
- **Protótipo:** `int sceIoGetstatByFdAsync(SceUID fd, SceIoStat *stat);` (inferido)
- **Comportamento:** `sceIoGetstatByFd` assíncrono.
- **Retorno/erros:** `opid` / `IO_EBADF`; Complete → 0.
- **Estruturas:** `SceIoStat`.
- **Implementação no VitaSharp:** `Submit(fd, () => sceIoGetstatByFd(fd, stat))`.
- **Validação:** unitário.

### sceIoChstatAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x9739A5E2
- **Protótipo:** `int sceIoChstatAsync(const char *file, SceIoStat *stat, int bits);` (inferido; syscall `_sceIoChstatAsync`)
- **Comportamento:** `sceIoChstat` assíncrono.
- **Retorno/erros:** `opid`; Complete → como Chstat.
- **Estruturas:** `SceIoStat`, bits `SCE_CST_*`.
- **Implementação no VitaSharp:** depende do STUB `sceIoChstat`. Copie a `SceIoStat` (0x58 bytes) na submissão.
- **Validação:** unitário.

### sceIoChstatByFdAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0xA9F89275
- **Protótipo:** `int sceIoChstatByFdAsync(SceUID fd, const SceIoStat *buf, unsigned int cbit);` (inferido)
- **Comportamento:** `sceIoChstatByFd` assíncrono.
- **Retorno/erros:** `opid` / `IO_EBADF`; Complete → como ChstatByFd.
- **Estruturas:** `SceIoStat`.
- **Implementação no VitaSharp:** depende do STUB `sceIoChstatByFd`.
- **Validação:** unitário.

### sceIoMkdirAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x8E5FCBB1
- **Protótipo:** `int sceIoMkdirAsync(const char *dir, SceMode mode);` (inferido)
- **Comportamento:** `sceIoMkdir` assíncrono.
- **Retorno/erros:** `opid`; Complete → 0 / EEXIST / ENOENT.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(-1, () => sceIoMkdir(path, mode))`.
- **Validação:** unitário.

### sceIoRmdirAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x9694D00F
- **Protótipo:** `int sceIoRmdirAsync(const char *path);` (inferido)
- **Comportamento:** `sceIoRmdir` assíncrono.
- **Retorno/erros:** `opid`; Complete → 0 / ENOENT / ENOTEMPTY.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(-1, () => sceIoRmdir(path))`.
- **Validação:** unitário.

### sceIoRemoveAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x446A60AC
- **Protótipo:** `int sceIoRemoveAsync(const char *file);` (inferido)
- **Comportamento:** `sceIoRemove` assíncrono.
- **Retorno/erros:** `opid`; Complete → 0 / ENOENT.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(-1, () => sceIoRemove(path))`.
- **Validação:** unitário.

### sceIoRenameAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xEE9857CD
- **Protótipo:** `int sceIoRenameAsync(const char *oldname, const char *newname);` (inferido)
- **Comportamento:** `sceIoRename` assíncrono.
- **Retorno/erros:** `opid`; Complete → 0 / ENOENT.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(-1, () => sceIoRename(a, b))`.
- **Validação:** unitário.

### sceIoDopenAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0xC49C312F
- **Protótipo:** `SceUID sceIoDopenAsync(const char *dirname);` (inferido)
- **Comportamento:** `sceIoDopen` assíncrono; o dfd é o resultado do Complete.
- **Retorno/erros:** `opid`; Complete → dfd / ENOENT.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(-1, () => sceIoDopen(path))`.
- **Validação:** unitário.

### sceIoDreadAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0xF59F37B0
- **Protótipo:** `int sceIoDreadAsync(SceUID fd, SceIoDirent *dir);` (inferido)
- **Comportamento:** `sceIoDread` assíncrono.
- **Retorno/erros:** `opid` / `IO_EBADF`; Complete → >0 entrada lida, 0 fim.
- **Estruturas:** `SceIoDirent` (0x160).
- **Implementação no VitaSharp:** `Submit(fd, () => sceIoDread(fd, dirent))`. As filas por fd precisam aceitar dfds
  (de `_dirs`).
- **Validação:** unitário.

### sceIoDcloseAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0xDC2D7D38
- **Protótipo:** `int sceIoDcloseAsync(SceUID fd);` (inferido)
- **Comportamento:** `sceIoDclose` assíncrono.
- **Retorno/erros:** `opid` / `IO_EBADF`; Complete → 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Submit(fd, () => sceIoDclose(fd))`.
- **Validação:** unitário.

### sceIoDevctlAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x950F78EB
- **Protótipo:** `int sceIoDevctlAsync(const char *dev, unsigned int cmd, void *indata, int inlen, void *outdata, int outlen);`
  (inferido de `sceIoDevctl`; syscall `_sceIoDevctlAsync(dev, cmd, arg, SceIoDevctlOpt*)`, com `SceIoDevctlOpt` 0x18:
  +0 `arglen`, +4 `bufp`, +8 `buflen`, +0xC reservado[3], conforme `psp2/io/fcntl.h`)
- **Comportamento:** `sceIoDevctl` assíncrono (por exemplo, o 0x3001, espaço livre).
- **Retorno/erros:** `opid`; Complete → como Devctl.
- **Estruturas:** `SceIoDevInfo` (0x18) para o 0x3001: +0 `max_size` s64, +8 `free_size` s64, +0x10 `cluster_size`.
  Repare que o `sceIoDevctl` atual escreve só 16 bytes e não grava `cluster_size`. Corrija junto (o Vita3K usa
  4096).
- **Implementação no VitaSharp:** `Submit(-1, () => sceIoDevctl(...))`. A pilha de 6 argumentos é tratada pelo
  binder.
- **Validação:** unitário.

### sceIoIoctlAsync
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x099C54B9
- **Protótipo:** `int sceIoIoctlAsync(SceUID fd, unsigned int cmd, void *indata, int inlen, void *outdata, int outlen);` (`psp2/io/devctl.h`)
- **Comportamento:** `sceIoIoctl` assíncrono.
- **Retorno/erros:** `opid` / `IO_EBADF`; Complete → como Ioctl.
- **Estruturas:** dependem do comando.
- **Implementação no VitaSharp:** `Submit(fd, () => sceIoIoctl(...))`, depois do STUB `sceIoIoctl`.
- **Validação:** unitário.

### Funções de controle do I/O assíncrono

### sceIoComplete
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0xD1C49D2F
- **Protótipo:** `int sceIoComplete(SceUID opid);` (sem header; inferido. O argumento pode ser o fd em vez do `opid`:
  **a confirmar**)
- **Comportamento:** finaliza a operação: se ainda estiver pendente, espera (inferido; pode também devolver
  "ainda não terminou", a confirmar). Depois devolve o resultado e destrói o objeto `opid`.
- **Retorno/erros:** resultado da operação; UID desconhecido → `SCE_KERNEL_ERROR_UNKNOWN_EVENT_ID 0x80028201`
  (a confirmar; pode ser o código de UID de I/O); operação cancelada → `ECANCELED 0x8001008C` (a confirmar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `IoFileMgr.Async.cs`: `var op = K.Objects.Get<IoAsyncOp>(uid)`; se
  `!op.Done`, fazer `lock (op) Wait.Until(op, o => o.Done, default, …)`; `K.Objects.Remove(uid)`; devolver
  `(int)op.Result`.
- **Validação:** unitário; ver "Como confirmar" na arquitetura.

### sceIoCompleteMultiple
- [ ] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xA792C404
- **Status (p1):** continua não registrada: vita-headers só lista o NID (`db/360/SceLibKernel.yml`) e a syscall `_sceIoCompleteMultiple` 0x9111D004, sem protótipo nem struct; no Vita3K ambas são `UNIMPLEMENTED()` sem argumentos.
- **Protótipo:** sem header. Inferido: `int sceIoCompleteMultiple(SceIoCompleteMultipleParam *params, int count);`,
  um array de `{ SceUID opid; int result; … }` (**a confirmar**; a syscall `_sceIoCompleteMultiple` 0x9111D004 é o
  backend).
- **Comportamento:** aplica `sceIoComplete` a vários `opid` e escreve o resultado de cada um no array.
- **Retorno/erros:** 0 ou número de concluídos (a confirmar); erros por elemento ficam no array.
- **Estruturas:** a confirmar. Para descobrir, peça o tamanho do elemento a um jogo/homebrew que use a função
  (stride do loop no caller), ou desmonte o export da SceLibKernel do firmware.
- **Implementação no VitaSharp:** só depois de confirmar o layout. Até lá, não registre a função (melhor o aviso
  "Unimplemented" que um layout errado).
- **Validação:** a definir quando o layout for confirmado.

### sceIoCancel
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0xCEF48835
- **Protótipo:** `int sceIoCancel(SceUID fd);` (`psp2/io/fcntl.h`: "Cancel an asynchronous operation on a file
  descriptor")
- **Comportamento:** cancela as operações assíncronas pendentes. O header diz `fd`; se for o fd ou o `opid` é **a
  confirmar** (aceite os dois: procure primeiro um `IoAsyncOp` com esse UID, depois as operações com `Fd == fd`). As
  operações canceladas concluem com `Result = ECANCELED` e o evento é sinalizado.
- **Retorno/erros:** 0; nada pendente → erro (a confirmar; candidato `EALREADY`/`EINVAL`); UID desconhecido →
  `IO_EBADF`.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** na fase 1 tudo já está concluído na submissão, então Cancel só valida o
  argumento e devolve o erro de "nada pendente" (a confirmar) ou 0. Na fase 2, remova da fila os itens não
  iniciados.
- **Validação:** unitário (fase 2).

---

## 5. Prioridade de I/O (P1) e variantes ForSystem (FORA)

Semântica comum: o iofilemgr do Vita agenda as requisições pendentes por prioridade (mesma escala de prioridade de
thread, menor = mais urgente). A prioridade efetiva de uma operação vem do fd (`sceIoSetPriority`); se o fd não
tiver uma, vem da thread (`SetThreadDefault`) e depois do processo (`SetProcessDefault`). O VitaSharp não tem fila
de I/O (operações síncronas no host), então **guardar e devolver os valores basta**. Na fase 2 do Async, a
prioridade pode ordenar a fila. Faixa válida e valor padrão: **a confirmar**. Candidatos: faixa 1–126 como
threads; padrão do processo 0x10000100 ("SCE_KERNEL_DEFAULT_PRIORITY_USER") ou o valor numérico do jogo. Para
descobrir, chame `sceIoGetProcessDefaultPriority()` no console, ou veja o que o jogo passa (`--trace-hle-only sceIoSet`).

Implementação comum: em `IoFileMgr`, guarde `int Priority` em `OpenFile` (‑1 = não definida),
`int _processDefaultPriority` e um `ConditionalWeakTable<GuestThread, StrongBox<int>>` para o valor por thread.

### sceIoGetPriority
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0xF2A472A1
- **Protótipo:** `int sceIoGetPriority(SceUID fd);` (`psp2/io/fcntl.h`)
- **Comportamento:** devolve a prioridade de I/O do fd (a definida, ou a herdada no open, a confirmar).
- **Retorno/erros:** prioridade (≥ 0); fd inválido → `IO_EBADF`.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `_files[fd].Priority`; se ‑1, devolver o padrão da thread/processo.
- **Validação:** Set → Get devolve o valor.

### sceIoSetPriority
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0x14B2D56C
- **Protótipo:** `int sceIoSetPriority(SceUID fd, int priority);` (`psp2/io/fcntl.h`)
- **Comportamento:** define a prioridade de I/O do fd.
- **Retorno/erros:** 0; fd inválido → `IO_EBADF`; prioridade fora da faixa → `SCE_KERNEL_ERROR_ILLEGAL_PRIORITY
  0x8002801B` (a confirmar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** grava `Priority`.
- **Validação:** unitário.

### sceIoGetThreadDefaultPriority
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0xA176CD03
- **Protótipo:** `int sceIoGetThreadDefaultPriority(void);` (`psp2/io/fcntl.h`)
- **Comportamento:** prioridade padrão dos fds abertos pela thread atual.
- **Retorno/erros:** prioridade; se não definida, a do processo (inferido).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** tabela por `GuestThread.Current`.
- **Validação:** unitário.

### sceIoSetThreadDefaultPriority
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0x49312108
- **Protótipo:** `int sceIoSetThreadDefaultPriority(int priority);` (`psp2/io/fcntl.h`)
- **Comportamento:** define a prioridade padrão de I/O da thread atual.
- **Retorno/erros:** 0; fora da faixa → ILLEGAL_PRIORITY (a confirmar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** tabela por thread.
- **Validação:** unitário.

### sceIoGetProcessDefaultPriority
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0x0DC4F1BB
- **Protótipo:** `int sceIoGetProcessDefaultPriority(void);` (`psp2/io/fcntl.h`)
- **Comportamento:** prioridade padrão de I/O do processo.
- **Retorno/erros:** prioridade (valor inicial a confirmar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `_processDefaultPriority`.
- **Validação:** unitário.

### sceIoSetProcessDefaultPriority
- [x] **Prioridade:** P1 · **Biblioteca:** SceIofilemgr · **NID:** 0x7F2ABBAF
- **Protótipo:** `int sceIoSetProcessDefaultPriority(int priority);` (`psp2/io/fcntl.h`)
- **Comportamento:** define a prioridade padrão de I/O do processo.
- **Retorno/erros:** 0; fora da faixa → ILLEGAL_PRIORITY (a confirmar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `_processDefaultPriority = priority`.
- **Validação:** unitário.

### sceIoGetPriorityForSystem
- [ ] **Prioridade:** FORA · **Biblioteca:** SceIofilemgr · **NID:** 0xEF5432ED
- Variante privilegiada de `sceIoGetPriority`, usada por módulos de sistema (shell, NP); jogos usam a versão comum.

### sceIoSetPriorityForSystem
- [ ] **Prioridade:** FORA · **Biblioteca:** SceIofilemgr · **NID:** 0x27373135
- Variante privilegiada de `sceIoSetPriority` (aceita prioridades de sistema); não é chamada por jogos.

### sceIoGetThreadDefaultPriorityForSystem
- [ ] **Prioridade:** FORA · **Biblioteca:** SceIofilemgr · **NID:** 0xFCBCEAED
- Variante de sistema de `sceIoGetThreadDefaultPriority`; só módulos de sistema.

### sceIoSetThreadDefaultPriorityForSystem
- [ ] **Prioridade:** FORA · **Biblioteca:** SceIofilemgr · **NID:** 0xB9C9C9CF
- Variante de sistema de `sceIoSetThreadDefaultPriority`; só módulos de sistema.

### sceIoFlockForSystem
- [ ] **Prioridade:** FORA · **Biblioteca:** SceIofilemgr · **NID:** 0x3E98E422
- Lock de arquivo para processos de sistema (sem protótipo público); jogos usam `SCE_O_RDLOCK`/`WRLOCK` no open,
  quando usam.

---

## 6. `sceIoRead2`, `sceIoWrite2`, `sceIoClose2` (P1)

Nenhum header, o Vita3K não implementa e nenhum dos 7 jogos importa. Os nomes indicam variantes "v2" exportadas
pela SceLibKernel (wrappers em user mode). Hipóteses:
(a) a mesma assinatura do original, com retorno/erros diferentes (por exemplo, `SceSSize` de 64 bits ou um
`opt`/flags extra);
(b) wrappers que tratam fds de stdio da libc (0x10–0x12) de forma especial.
**Não implemente às cegas.** Para confirmar: desmonte os exports 0x713523E1, 0x11FED231 e 0xF5C6F098 no
`libkernel.suprx` do firmware 3.60 e veja qual syscall cada um chama e quantos argumentos repassa. Outra fonte é
qualquer módulo que os importe (`imports` de outros títulos), lendo os argumentos no call site, como foi feito
com `NID_023EAA62`.

### sceIoRead2
- [ ] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x713523E1
- **Status (p1):** continua não registrada: só o NID em `db/360/SceLibKernel.yml`; sem header e Vita3K `UNIMPLEMENTED()`.
- **Protótipo:** sem header. Inferido: `SceSSize sceIoRead2(SceUID fd, void *buf, SceSize nbyte /*, ? */);` (a
  confirmar)
- **Comportamento:** provavelmente igual a `sceIoRead`. A confirmar pelo método acima.
- **Retorno/erros:** como `sceIoRead` (a confirmar).
- **Estruturas:** a confirmar.
- **Implementação no VitaSharp:** depois de confirmar, `[Hle] int sceIoRead2(int fd, uint buf, uint size) => sceIoRead(fd, buf, size);`
  em `IoFileMgr.cs`.
- **Validação:** um título que importe a função; não há nenhum hoje.

### sceIoWrite2
- [ ] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0x11FED231
- **Status (p1):** continua não registrada: só o NID em `db/360/SceLibKernel.yml`; sem header e Vita3K `UNIMPLEMENTED()`.
- **Protótipo:** sem header. Inferido: `SceSSize sceIoWrite2(SceUID fd, const void *buf, SceSize nbyte /*, ? */);` (a
  confirmar)
- **Comportamento:** provavelmente igual a `sceIoWrite`.
- **Retorno/erros:** como `sceIoWrite` (a confirmar).
- **Estruturas:** a confirmar.
- **Implementação no VitaSharp:** delegar para `sceIoWrite` depois de confirmar.
- **Validação:** idem.

### sceIoClose2
- [ ] **Prioridade:** P1 · **Biblioteca:** SceLibKernel · **NID:** 0xF5C6F098
- **Status (p1):** continua não registrada: só o NID em `db/360/SceLibKernel.yml`; sem header e Vita3K `UNIMPLEMENTED()`.
- **Protótipo:** sem header. Inferido: `int sceIoClose2(SceUID fd);` (a confirmar)
- **Comportamento:** provavelmente igual a `sceIoClose`.
- **Retorno/erros:** como `sceIoClose` (a confirmar).
- **Estruturas:** nenhuma (a confirmar).
- **Implementação no VitaSharp:** delegar para `sceIoClose` depois de confirmar.
- **Validação:** idem.
