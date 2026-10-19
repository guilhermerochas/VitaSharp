# Especificações HLE pendentes

Este diretório descreve, função por função, tudo o que falta implementar no HLE do VitaSharp em relação ao VitaSDK
(user-mode). Foi escrito para que uma sessão futura (humana ou do Claude) pegue um arquivo, implemente as funções
e marque o checkbox, sem refazer a pesquisa.

Fonte da comparação (2026-10-18):

- VitaSharp: reflection sobre todo `HleModule` e seus atributos `[Hle]` (1.126 NIDs implementados).
- VitaSDK: `db/360` + `db/363` do [vita-headers](https://github.com/vitasdk/vita-headers) @ `e66ebe9`, só bibliotecas
  `kernel: false` (7.006 funções). É o mesmo banco que gera o [docs.vitasdk.org](https://docs.vitasdk.org/).
- Uso real: `VitaSharp.Desktop imports <TITLEID>` nos 7 jogos instalados e logs `Unimplemented HLE function`.
- Referência de comportamento: [Vita3K](https://github.com/Vita3K/Vita3K) @ `7059dc2` (`vita3k/modules/<Lib>/`).
  Use para entender semântica, layouts e códigos de erro; não copie código.

## Arquivos

| Arquivo | Escopo |
| --- | --- |
| [A-kernel.md](A-kernel.md) | SceLibKernel (exceto I/O), SceThreadmgr, SceSysmem, SceProcessmgr, SceModulemgr, SceSysmodule, SceRtc |
| [B-io.md](B-io.md) | `sceIo*` (sync e async), SceIofilemgr, overlays do SceFios2Kernel, `NID_023EAA62` / `tty0:` |
| [C-gxm-display.md](C-gxm-display.md) | SceGxm, SceDisplay, SceScreenShot e stubs de renderização |
| [D-dialogs-app.md](D-dialogs-app.md) | SceCommonDialog, SceAppUtil, SceAppMgrUser |
| [E1-net.md](E1-net.md) | SceHttp, SceNet, SceNetCtl, SceSsl, SceLibRudp, SceNetAdhocMatching |
| [E2-np.md](E2-np.md) | Todas as bibliotecas SceNp* |
| [F-devices-media.md](F-devices-media.md) | Touch, Ctrl, Motion, Audio, AudioIn, Audiodec, Videodec, Camera, Power, Face, Smart, Ngs |
| [G1-media-libs.md](G1-media-libs.md) | P2: Atrac, Sas, Voice, G729, Audioenc, Jpeg*, Videoenc, Mp4Recorder, Pgf, Pvf, Ime, Handwriting |
| [G2-support-libs.md](G2-support-libs.md) | P2: Ult, Json, Xml, Sqlite, utilitários de sistema, debug/perf |
| [data/](data/) | TSVs brutos: funções do VitaSDK com status, implementadas no VitaSharp, faltantes por jogo |

## Comece por aqui: achados que atravessam pacotes

Estes itens rendem mais do que qualquer função isolada. Faça-os antes do resto.

1. **Códigos de erro errados (P0).** ~30 constantes de `SceError` (`Kernel/KernelObjects.cs`) divergem de
   `psp2/kernel/error.h`: `INVALID_ARGUMENT` é `0x80020005` (não `0x80020003`, que é `NOSYS`), `ILLEGAL_ADDR` é
   `0x80020006`, além de Sema/Mutex/LwMutex/EventFlag/MsgPipe/Thread. Também há literais errados em `ThreadMgr.cs`,
   `Sync.cs`, `VmThreads.cs`, `SysMem.cs`, `sceSysmoduleIsLoaded`, `Online.cs` (rede/HTTP) e `Audio.cs`.
   Tabelas completas: [A-kernel.md](A-kernel.md) §0.1, [E1-net.md](E1-net.md), [F-devices-media.md](F-devices-media.md) Anexo A.
2. **`ThreadStatus.Waiting = 4`** (`Kernel/GuestThread.cs:9`); no header WAITING é 8 e 4 é STANDBY. Afeta
   `sceKernelGetThreadInfo`. Ver [A-kernel.md](A-kernel.md) §0.1.
3. **`sceSysmoduleLoadModule` carregando módulos via LLE.** Hoje é HLE puro. Fazê-lo carregar
   `app0:sce_module/<nome>.suprx` e, na falta, `vs0:sys/external/<nome>.suprx` (chamando `module_start`) resolve:
   SceFace e SceSmart do Invizimals (16 P0 + 48 P1; o jogo traz `libface.suprx`/`libsmart.suprx`), e torna viáveis
   Ult, Json, Xml, Sqlite, Sas, Pgf, Atrac e Pvf sem HLE. Ver [G2-support-libs.md](G2-support-libs.md) (topo),
   [F-devices-media.md](F-devices-media.md) e [G1-media-libs.md](G1-media-libs.md).
4. **Bugs em funções já implementadas** ([D-dialogs-app.md](D-dialogs-app.md), seção STUB):
   `sceAppMgrAppParamGetString` troca os IDs 8 e 9; `sceAppUtilSaveDataDataRemove` anda de 48 em 48 bytes (a struct
   tem 0x2C); `sceMsgDialogInit` lê o modo em 0x18 (é 0x50); `sceNetCheckDialogGetResult` grava código de erro no
   campo de resultado; `sceKernelGetSystemSwVersion` escreve `version` no offset errado ([A-kernel.md](A-kernel.md)).
5. **Esperas que viram busy loop:** `sceNetEpollWait` (timeout -1 dorme 1 ms) e `sceRudpPollWait` (volta na hora).
   `Wait.Until` precisa de uma sobrecarga com timeout por valor. Ver [E1-net.md](E1-net.md).
6. **Falsos positivos do comando `imports`:** os 34 imports `pthread`/`Il2CppAssemblies` do Devious Dungeon resolvem
   em runtime por LLE (`Media/Modules`); o comando não carrega esses módulos. Ver [A-kernel.md](A-kernel.md) §0.3.
7. **`NID_023EAA62`** (F1 2011, Uncharted) é a função de log de erro da libc antiga: registrar a string e retornar 0.
   O problema de verdade é `tty0:`/`tty1:` não mapeados no VFS. Ver [B-io.md](B-io.md).

Todos os pacotes marcam como "inferido" ou "a confirmar" o que não tem fonte (sem header no VitaSDK e sem
implementação no Vita3K), sempre com o método para confirmar. Confirme antes de implementar.

## Prioridades

- **P0:** importada por um jogo instalado e sem HLE. Jogos: F1 2011 (PCSE00002), Uncharted: Golden Abyss (PCSA00029),
  God of War Collection (PCSA00126), Invizimals: The Resistance (PCSA00552), Devious Dungeon (PCSB01240),
  Killzone Mercenary JP (PCSC00045), Sakatsuku 2013 (PCSG00198).
- **STUB:** já existe como `[Hle]`, mas o corpo é trivial e descarta comportamento que jogos notam.
- **P1:** função pública de uma biblioteca já parcialmente implementada.
- **P2:** biblioteca de jogo sem nenhuma função implementada; implementar quando um jogo importar.
- **FORA:** não é chamada por jogos (shell, VSH, debug interno). Fica documentada para não ser pesquisada de novo.

## Template de cada função

```markdown
### sceExemploFuncao
- [ ] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceLibKernel · **NID:** 0x12345678
- **Protótipo:** `int sceExemploFuncao(SceUID id, SceExemploInfo *pInfo);` (`psp2/kernel/threadmgr.h`)
- **Comportamento:** o que a função faz no hardware real, incluindo efeitos colaterais.
- **Retorno/erros:** valor de sucesso e cada código de erro relevante (nome + valor hex).
- **Estruturas:** layout com offsets, quando há struct.
- **Implementação no VitaSharp:** arquivo/classe onde entra, helpers e objetos existentes a reaproveitar.
- **Validação:** como confirmar (jogo, cenário em `tests/scenarios`, teste unitário).
```

Famílias de funções idênticas (atomics, `Init/GetStatus/GetResult/Term/Abort` de diálogos) têm uma seção comum e uma
tabela com o que muda por função.

## Como implementar

1. Método com `[Hle]` numa subclasse de `HleModule` em `src/VitaSharp.Core/Hle/Modules/`. O registro é automático
   (`Emulator.InitProcess`). O NID é resolvido pelo nome em `Hle/Data/nids.txt`; use `Nid = 0x...` se o nome não
   estiver lá e `Library = "..."` se for ambíguo.
2. Parâmetros seguem AAPCS-VFP pelo binder (`HleFramework.cs`): `int/uint/Ptr<T>/string` em r0–r3 e pilha,
   `long/ulong` em par alinhado, `float` em s0–s15, `VarArgs` para variádicas.
3. Erros vêm de `SceError` (`Kernel/KernelObjects.cs`); objetos do kernel via `K.Objects`.
4. Funções de espera ou I/O novas precisam cair na faixa certa de `HleFunction.ClassifyCost`.
5. Depois de implementar: `VitaSharp.Desktop imports <TITLEID>` deve mostrar o nome fora da lista de faltantes, e os
   cenários do jogo afetado devem rodar sem regressão.
