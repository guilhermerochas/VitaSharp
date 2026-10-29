# E2: PSN (SceNp*)

Especificação HLE de todas as bibliotecas `SceNp*` que ainda faltam no VitaSharp (exceto SceNpTrophy e SceNpMatching2,
que já estão implementadas em `NpTrophy.cs` e `Multiplayer.cs`). O escopo é a lista `E.tsv` filtrada por bibliotecas
`SceNp*` (296 funções), mais os stubs triviais da classe `Np` (`Online.cs`) que descartam comportamento relevante.

Bibliotecas: SceNpCommon, SceNpManager, SceNpScore, SceNpTus (inclui `sceNpTss*`), SceNpBasic, SceNpMessage,
SceNpActivity, SceNpPartyGameUtil, SceNpUtility (Lookup, WordFilter, BandwidthTest), SceNpSignaling, SceNpWebApi,
SceNpWebApiCommonDialog, SceNpCommerce2, SceNpDrmPackage, SceNpSnsFacebook.

## Contagem por prioridade

| Prioridade | Funções | Onde |
| --- | ---: | --- |
| P0 | 38 | Score (9) e Tus (13) para Sakatsuku PCSG00198; Common (4); Manager (1); Basic (3); Message (4); Activity (3); Party (1) para Invizimals PCSA00552 |
| STUB | 4 | `sceNpInit`, `sceNpAuthCreateStartRequest`, `sceNpLookupUserProfileAsync`, `sceNpLookupPollAsync` |
| P1 | 245 | restante das bibliotecas acima |
| FORA | 13 | SnsFacebook (7), DrmPackage `*Ongoing` (4), `sceNpCommonMalloc/FreeNpServerName` (2) |
| **Total** | **300** | 296 da lista + 4 STUB |

No `E.tsv` as 13 funções FORA aparecem como P1. Elas foram reclassificadas aqui, com justificativa em cada seção.

**Estado (2026-10-19, branch `feat/hle-p0-e2`):** todos os P0 e STUB implementados (checkboxes abaixo). As tabelas P1
(sem checkbox) também foram implementadas conforme o contrato offline, em `Hle/Modules/Np.cs` (`NpManager`, `NpIdUtil`,
`NpSocial`), `NpCommunity.cs` (`NpCommunity`, `NpScore`, `NpTus`, `NpUtility`) e `NpServices.cs` (`NpSignaling`,
`NpWebApi`, `NpWebApiCommonDialog`, `NpCommerce2`, `NpDrmPackage`), exceto as de protótipo não confirmado:
`sceNpCommonBase64Encode`, `sceNpCommonGetNpEnviroment`, `sceNpCommonGetSystemSwVersion`,
`sceNpCommerce2GetShortfallOfLibhttpPool`/`LibsslPool`, e as FORA. A classe `Np` de `Online.cs` mantém só Basic,
Party e `sceNpMessageDialog*`. Pendentes de confirmação (§0.6 não achou call sites diretos no eboot do Invizimals): o
valor de `SCE_NP_PARTY_STATE_NOT_IN_PARTY` (hoje 0) e a forma de `sceNpGetPlatformType` (tipo em r0).

## Ordem sugerida de implementação

1. **Helper `NpCommunity` (§0.4)** e **SceNpScore/SceNpTus P0** (§4, §5): destrava Sakatsuku, cujos 22 imports
   ainda faltam. Rodar `st_boot`/`st_game` depois.
2. **SceNpCommon P0** (§2): `sceNpCmpNpId` (Sakatsuku), `sceNpGet/SetPlatformType` e `sceNpAuthAbortRequest`.
3. **Invizimals P0** (§3, §6–§9): `sceNpManagerGetAccountRegion`, Basic (3), Message (4), Activity (3) e
   `sceNpPartyGetState`. Antes, confirmar os protótipos inferidos com o procedimento do §0.6.
4. **STUB** (§1): migrar o Lookup para o helper `NpCommunity` e ajustar `sceNpInit` e `sceNpAuthCreateStartRequest`.
5. **P1 do Score/Tus** (variantes síncronas, VUser, Tss): sai quase de graça com o helper.
6. P1 de Common, Manager, Basic, Message e Party, que são pequenas.
7. SceNpUtility (Lookup, WordFilter, BandwidthTest), SceNpSignaling, SceNpWebApi, SceNpWebApiCommonDialog.
8. SceNpCommerce2.
9. FORA só quando algum jogo importar.

---

## 0. Decisão de design: PSN "offline / sem conta"

### 0.00 Serviços PSN locais (2026-10-29, branch `feat/psn-local`): padrão = ligados

Com a conta conectada, os serviços que funcionam para um jogador só são atendidos por um "servidor" local
(`Hle/Np/LocalPsn.cs`, dados em `<DataRoot>/np`, por título e por ID online). `--psn-local=off` (launcher: "Serviços PSN
locais") volta ao comportamento da seção 0.0, em que tudo falha como servidor inacessível. Nada sai do PC.

- **TUS / Cross-Save** (`NpTus`, `TusStore`): variáveis e dados por slot em `np/tus/<npCommId>/<dono>/`
  (`variables.json`, `data_<slot>.bin` + `.json`). Set/Get/Delete MultiSlot, MultiUser, Friends (só o próprio usuário),
  AddAndGet, TryAndSet (condição `compareValue ?? valor <op> atual`), SetData/GetData em partes na mesma requisição
  (o resultado do GetData é o número de bytes copiados), DataStatus. Slot vazio: `hasData` 0, e GetData responde
  `USER_STORAGE_DATA_NOT_FOUND` (0x8055084A). TSS não tem dados publicados: `USER_STORAGE_TITLE_MASTER_NOT_FOUND`.
- **Score** (`NpScore`, `ScoreStore`): `np/score/<npCommId>/board_<id>.json`, melhor pontuação por ID online e
  personagem, ordem decrescente (o `SortMode` do arquivo pode ser trocado para 1, crescente, em placares de tempo),
  empates dividem a posição. RecordScore (tmpRank, `NOT_BEST_SCORE`), GetRankingByRange/NpId/NpIdPcId/Friends,
  BoardInfo, game data, comentários. Sem entradas: `GAME_RANKING_NOT_FOUND`.
- Estruturas no layout do PS3 (RPCS3 `sceNpTus.h`/`sceNp.h`): SceNpTusVariable 120, SceNpTusDataStatus 488,
  SceNpScoreRankData 128, SceNpScorePlayerRankData 136; o tamanho de cada elemento vem do tamanho do vetor passado pelo
  jogo (o layout do Vita não está confirmado em header público). Requisições assíncronas terminam na hora: Poll/Wait
  devolvem 0 com o resultado (nunca "rodando").
- **Matching2** (`Multiplayer.cs`): ContextStart chama o callback de contexto com START (0x6F02, causa
  CONTEXT_ACTION) a partir de uma thread da biblioteca (`HleCallbackThread`); ContextStop manda STOP. Um servidor
  (id 1, AVAILABLE) e um mundo; SearchRoom e GetLobbyInfoList respondem 0 salas; CreateJoinRoom falha no callback com
  `SERVICE_UNAVAILABLE`, JoinRoom e o resto de sala com `NO_SUCH_ROOM`. Toda requisição recebe resposta pelo callback
  (`void (ctxId, reqId, event, errorCode, const void *data, arg)`: o Vita não tem sceNpMatching2GetEventData).
  Códigos 0x80550Cxx/0x80550Dxx com o byte baixo do PS3 (inferidos).
- **Auth** (`NpTicket.cs`): ticket local no formato 2.1 do PS3 (o que o RPCN emite), entregue no callback com o
  tamanho; GetTicketParam, GetEntitlementById/ByIdPrefix/IdList. O DLC instalado em `ux0:addcont/<TITLEID>` aparece
  como direito comprado (id = CONTENT_ID sem o prefixo do serviço).
- **Social**: lista de amigos vazia; presença e atividade aceitas (ninguém vê). Lookup: nenhum outro usuário.
- **Commerce2 continua falhando como servidor inacessível**: o Vita usa CreateSessionStart/GetResult com requisições,
  mas o protótipo e se há eventos no handler não estão confirmados; uma sessão "aceita" sem o evento esperado pode
  travar o jogo. A loja aparece como indisponível (para o jogador, vazia); o DLC instalado vale pelo ticket.

### 0.0 Conta PSN emulada (2026-10-28, branch `fix/psn-signin`): padrão = conectada

O perfil "sem conta" das seções 0.1 e 0.2 continua existindo, mas deixou de ser o padrão. A conta é escolhida por
`PsnAccount` (`Hle/PsnAccount.cs`), vinda de `EmulatorOptions.Psn`, das opções `--psn=on|off` e `--psn-id=<online id>`
e, no launcher, de Configurações > Geral > Rede e PSN ("Simular conta PSN conectada (offline)" e "ID online", padrão
`VitaSharp`). Nada é enviado aos servidores da Sony.

- **Conectada (padrão, como o "PSN status: signed in" do Vita3K):** `sceNpGetServiceState`/`sceNpCheckCallback` dizem
  `SIGNED_IN` (2); `sceNpManagerGetNpId` devolve o ID online configurado (plataforma `psp2`, `isIdValid` = 1, como o
  Vita3K); `sceNpManagerGetAccountRegion` devolve `us` e o idioma do sistema; os callbacks de NetCtl (infraestrutura)
  recebem `IPOBTAINED` (3) em vez de `DISCONNECTED`; o NetCheckDialog nos modos `PSN` (2) e `PSN_ONLINE` (3) termina com
  `SCE_COMMON_DIALOG_RESULT_OK` e `psnModeSucceeded` = 1 (o Vita3K também devolve OK). Tudo o que iria a um servidor
  PSN (tickets de auth e OAuth, Score/Tus/Tss/Lookup/WordFilter, Matching2, Signaling, WebApi, Commerce2,
  presença/mensagens/atividade) falha como servidor inacessível, com `SCE_HTTP_ERROR_NETWORK` (0x80431063,
  `psp2/net/http.h`): o mesmo erro que o `sceHttpSendRequest` já devolve. Os modos ad hoc e PS3_CONNECT continuam
  cancelados (não há outro console).
- **Desconectada (`--psn=off`):** exatamente o comportamento anterior, descrito em 0.1 e 0.2.

Por que conectada por padrão: vários jogos não passam da tela inicial sem conta. 99Vidas (PCSE00914, UnityNpToolkit)
pergunta se o jogador quer ativar o Cross-Save, roda o NetCheckDialog em modo `PSN_ONLINE` e, se ele é cancelado, mostra
"You must sign in to PlayStation Network ... Do you want to retry?" para sempre: o MsgDialog sem interface responde YES.
Num Vita real o jogador poderia responder NO, mas o emulador não tem essa interface, e o Vita3K resolve com a conta
emulada. Um console conectado mas sem acesso ao servidor também é um estado real (servidor fora do ar), que os jogos
precisam tratar.

### 0.1 Perfil emulado sem conta (`--psn=off`)

- A rede local está ativa. `sceNetCtlInetGetState` retorna 3 (IP obtido), mas sockets, resolver e HTTP falham como
  se o servidor estivesse inacessível.
- Não há conta PSN. `sceNpGetServiceState` retorna `SCE_NP_SERVICE_STATE_SIGNED_OUT` (1), `sceNpCheckCallback` chama
  cada callback de estado uma vez com `SIGNED_OUT`, e `sceNpManagerGetNpId` retorna `SCE_NP_ERROR_SIGNED_OUT`.
- O mesmo perfil vale para NpMatching2/Rudp (`Multiplayer.cs`): contextos são criados e qualquer requisição que iria ao
  servidor falha com `SCE_NP_ERROR_SIGNED_OUT`.

### 0.2 Contrato offline por biblioteca

Regra geral: **inicialização e criação de objetos locais funcionam; operações que dependem do servidor falham com
um código negativo; nada fica "rodando" para sempre.** Toda operação assíncrona termina no instante em que começa, e
`Poll`/`Wait` retornam "terminado" com o erro em `*result`. Assim o jogo segue pelo caminho de "falha de rede", que
todo título certificado precisa tratar.

| Biblioteca | Sucesso (local) | Falha offline | Código offline | Assíncrono |
| --- | --- | --- | --- | --- |
| SceNpCommon | funções puras (`Cmp*`, `Base64`, `Get/SetPlatformType`), `Auth{Init,Term,Abort,Destroy}` | ticket e entitlement | `SCE_NP_ERROR_SIGNED_OUT` | `CreateStartRequest` falha na hora (ver STUB) |
| SceNpManager | `Init/Term`, callbacks, flags de restrição | NpId, região da conta, OAuth, cache | `SCE_NP_ERROR_SIGNED_OUT` | — |
| SceNpScore / SceNpTus / Tss | `Init`, `CreateTitleCtx`, `CreateRequest`, `Delete*`, `Abort`, `SetTimeout` | toda transação | `SCE_NP_COMMUNITY_ERROR_NO_LOGIN` (§0.3) | `*Async` retorna 0; `Poll/Wait` retornam 0 com `*result` = erro |
| SceNpUtility (Lookup, WordFilter) | igual ao Score | toda transação | `SCE_NP_COMMUNITY_ERROR_NO_LOGIN` | igual ao Score |
| SceNpUtility (BandwidthTest) | `Abort`, `Shutdown`, `GetStatus` | `InitStart` | `SCE_NP_ERROR_SIGNED_OUT` | sem estado "rodando" |
| SceNpBasic | `Init/Term`, handlers, listas vazias (amigos, bloqueados, pedidos) | presença, consulta por amigo | `SCE_NP_ERROR_SIGNED_OUT` | sem eventos |
| SceNpMessage | `Init/Term`, `Abort` | qualquer leitura de mensagem | `SCE_NP_ERROR_SIGNED_OUT` | — |
| SceNpActivity | `Init/Term` | posts | `SCE_NP_ERROR_SIGNED_OUT` | — |
| SceNpPartyGameUtil | `Init/Term`, handler, `GetState` = fora de party | dados de party | `0x80553402` (já usado no `Online.cs`) | sem eventos |
| SceNpSignaling | `Init/Term`, `Create/DestroyCtx`, opções | conexões, net info | `SCE_NP_ERROR_SIGNED_OUT` | sem eventos |
| SceNpWebApi | `Initialize`, handles, requests, filtros e callbacks de push | `Send*`, leitura de resposta | `SCE_NP_ERROR_SIGNED_OUT` (§12) | callbacks nunca disparam |
| SceNpWebApiCommonDialog | `Init` → status FINISHED | — | resultado `ABORTED` | igual aos diálogos do `Online.cs` |
| SceNpCommerce2 | `Init/Term`, `CreateCtx`, `Create*Req`, `Destroy*`, ícone da loja | `*Start` (sessão, categoria, produto) | `SCE_NP_ERROR_SIGNED_OUT` | `Start` falha na hora |

### 0.3 Códigos de erro

| Nome | Valor | Fonte / confiança |
| --- | --- | --- |
| `SCE_NP_ERROR_ALREADY_INITIALIZED` | 0x80550001 | Vita3K `SceNpManager.cpp` |
| `SCE_NP_ERROR_NOT_INITIALIZED` | 0x80550002 | Vita3K |
| `SCE_NP_ERROR_INVALID_ARGUMENT` | 0x80550003 | Vita3K; `Online.cs` já usa em `sceNpGetServiceState` |
| `SCE_NP_ERROR_UNKNOWN_PLATFORM_TYPE` | 0x80550004 | Vita3K |
| `SCE_NP_ERROR_SIGNED_OUT` | 0x80550006 | `Online.cs`/`Multiplayer.cs` (`ERR_SIGNED_OUT`); o PS4 usa o mesmo valor |
| `SCE_NP_MANAGER_ERROR_NOT_INITIALIZED` | 0x80550502 | Vita3K |
| `SCE_NP_MANAGER_ERROR_INVALID_ARGUMENT` | 0x80550503 | Vita3K |
| `SCE_NP_MANAGER_ERROR_ID_NOT_AVAIL` | 0x80550509 | Vita3K |
| `SCE_NP_UTIL_ERROR_INVALID_ARGUMENT` | 0x80550601 | Vita3K `SceNpCommon.cpp` |
| `SCE_NP_UTIL_ERROR_INVALID_NP_ID` | 0x80550605 | Vita3K |
| `SCE_NP_UTIL_ERROR_NOT_MATCH` | 0x80550609 | Vita3K |
| `SCE_NP_COMMUNITY_ERROR_ALREADY_INITIALIZED` | 0x80550701 | Vita3K `SceNpTus.cpp` |
| `SCE_NP_COMMUNITY_ERROR_NOT_INITIALIZED` | 0x80550702 | Vita3K |
| `SCE_NP_COMMUNITY_ERROR_INVALID_ARGUMENT` | 0x80550704 | Vita3K |
| `SCE_NP_COMMUNITY_ERROR_NO_LOGIN` | 0x80550705 | Vita3K (definido, não usado) |
| `SCE_NP_COMMUNITY_ERROR_TOO_MANY_OBJECTS` | 0x80550706 | Vita3K |
| `SCE_NP_COMMUNITY_ERROR_ABORTED` | 0x80550707 | Vita3K |
| `SCE_NP_COMMUNITY_ERROR_INSUFFICIENT_ARGUMENT` | 0x8055070C | Vita3K |
| `SCE_NP_COMMUNITY_ERROR_INVALID_ID` | 0x8055070E | Vita3K |
| `SCE_NP_COMMUNITY_ERROR_TRANSACTION_ALREADY_END` | 0x80550712 | Vita3K |
| `SCE_NP_COMMUNITY_ERROR_NO_RESOURCE` | 0x80550716 | Vita3K (é o que ele devolve para TSS offline) |
| `SCE_NP_COMMUNITY_SERVER_ERROR_*` | 0x805508xx | Vita3K; não usar offline |
| `SCE_NP_BASIC_ERROR_INVALID_ARGUMENT` | 0x80551D02 | Vita3K `SceNpBasic.cpp` |
| erro de party "sem party" | 0x80553402 | `Online.cs` (`sceNpPartyGetMembers`); **nome a confirmar** |

**Erro offline das bibliotecas community (Score/Tus/Lookup/WordFilter):** usar `SCE_NP_COMMUNITY_ERROR_NO_LOGIN`
(0x80550705), que é o equivalente da família community a "sem login". O código exato que o firmware devolve sem
conta está **a confirmar**: o Vita3K devolve `NO_RESOURCE` só para o TSS. Por isso o valor fica numa única constante
(`NpCommunity.OfflineResult`), fácil de trocar se algum jogo testar um código específico (procure no disassembly
comparações com `0x8055070x` logo após o `PollAsync`).

### 0.4 Arquitetura comum das bibliotecas community (Score, Tus/Tss, Lookup, WordFilter)

Todas seguem o mesmo modelo: **title context → request → transação (sync ou async) → poll/wait/abort → delete.**

Estado, uma instância por biblioteca (`NpCommunity`, classe auxiliar não-`HleModule` em
`Hle/Modules/NpCommunity.cs`, usada pelos módulos `NpScore`, `NpTus`, `NpLookup` e `NpWordFilter`):

```
bool Inited; int NextId = 1;                     // um contador por biblioteca: ctx e request no mesmo espaço
Dictionary<int, Ctx> TitleCtx;                   // máx. 32 (SCE_NP_TUS_MAX_CTX_NUM no Vita3K; usar 32 para todos)
Dictionary<int, Req> Requests;                   // máx. 32; Req { int Ctx; int? Result; }
```

O espaço de IDs é compartilhado porque `Set*Timeout(id, ...)` aceita tanto um title ctx quanto uma request.
Mantenha `lock` (o padrão de `Multiplayer.cs`).

| Operação | Regra |
| --- | --- |
| `Init(prio, affinity, opt)` | se `Inited` → `ALREADY_INITIALIZED`; senão `Inited = true`, limpa as tabelas, retorna 0. `opt` é ignorado |
| `Term()` | se `!Inited` → `NOT_INITIALIZED`; limpa as tabelas, `Inited = false`, retorna 0 |
| `CreateTitleCtx(commId, pass, selfNpId)` | `!Inited` → `NOT_INITIALIZED`; `commId == 0` → `INSUFFICIENT_ARGUMENT`; 32 contextos → `TOO_MANY_OBJECTS`; senão retorna um ID novo (> 0). **Não** validar `selfNpId`: offline ele vem zerado (o `GetNpId` falhou). Logar o commId (`ReadCString(commId, 9)` + `_` + byte em +10, como em `NpTrophy.cs`) |
| `DeleteTitleCtx(id)` | `!Inited` → `NOT_INITIALIZED`; ID inexistente → `INVALID_ID`; remove o contexto e as requests dele; 0 |
| `CreateRequest(ctxId)` | `!Inited` → `NOT_INITIALIZED`; contexto inexistente → `INVALID_ID`; 32 requests → `TOO_MANY_OBJECTS`; senão novo ID (> 0) |
| `DeleteRequest(id)` | `INVALID_ID` se não existe; remove; 0 |
| `AbortRequest(id)` | `INVALID_ID` se não existe; se `Result == null`, `Result = ABORTED`; 0 |
| `SetTimeout(id, ...)` | `INVALID_ID` se o ID não for nem contexto nem request; 0 (os valores são ignorados) |
| transação **síncrona** `X(reqId, ...)` | valida a request (`NOT_INITIALIZED`/`INVALID_ID`); se `Result != null` → `TRANSACTION_ALREADY_END`; `Result = OfflineResult`; **retorna `OfflineResult`**. Não escreve nos buffers de saída |
| transação **assíncrona** `XAsync(reqId, ...)` | mesma validação; `Result = OfflineResult`; **retorna 0** (iniciada) |
| `PollAsync(reqId, int *result)` | `NOT_INITIALIZED`/`INVALID_ID`; `result == 0` ou nenhuma transação iniciada (`Result == null`) → `INVALID_ARGUMENT` (comportamento do Vita3K); senão `*result = Result` e **retorna 0** (= FINISHED; 1 seria RUNNING, como no PS3/PS4. O valor de RUNNING no Vita está a confirmar, mas nunca é usado aqui) |
| `WaitAsync(reqId, int *result)` | igual ao `PollAsync`; nunca bloqueia, então não precisa entrar no `ClassifyCost` |

Notas:

- `TRANSACTION_ALREADY_END`: no PS3 uma request serve para uma única transação, e o Vita3K mantém essa regra. Se
  algum jogo reutilizar a request (o log mostrará `0x80550712`), relaxe a regra e sobrescreva `Result`.
- Os ponteiros de saída (rankArray, dataStatus, variableArray...) não são tocados: o resultado é erro, então o jogo
  não deve lê-los.
- Validação de argumentos além do ID da request é opcional. Os jogos não dependem dela offline, e cada verificação a
  mais é chance de rejeitar uma chamada legítima com protótipo inferido.
- **Binder:** declare no método C# só os parâmetros usados (normalmente `int reqId`, às vezes `Ptr<int> result`). Os
  demais ficam em r1–r3 ou na pilha e podem ser ignorados. Só quando um parâmetro posterior for usado é que tipos
  64 bits anteriores (`SceInt64 score` em `RecordScore`, `SceInt64 variable` no Tus) precisam ser declarados como
  `long`, para o binder alinhar o par de registradores.

### 0.5 Onde implementar

| Biblioteca | Arquivo / classe |
| --- | --- |
| Common, Manager, Basic, Message, Activity, Party | `Hle/Modules/Online.cs`, classe `Np` (já contém as irmãs) |
| Score, Tus/Tss, Lookup, WordFilter, BandwidthTest | novo `Hle/Modules/NpCommunity.cs`: helper `NpCommunity` + módulos `NpScore`, `NpTus`, `NpUtility` (mover os `sceNpLookup*` de `Np` para cá, ver STUB) |
| Signaling | `Hle/Modules/Multiplayer.cs`, nova classe `NpSignaling` (mesmo padrão de `NpMatching2`) |
| WebApi, WebApiCommonDialog, Commerce2, SnsFacebook, DrmPackage | novo `Hle/Modules/NpServices.cs` (classes `NpWebApi`, `NpCommerce2`, ...) |

Todos os nomes estão em `Hle/Data/nids.txt` com biblioteca única, **exceto `sceNpCommerce2GetDrmContentInfoCreateReq`**,
que precisa de `[Hle(Nid = 0x9DF3CBCC)]`.

### 0.6 Como confirmar protótipos inferidos (P0 do Invizimals)

O vita-headers só tem protótipos de `sceNpInit`, `sceNpTerm`, `sceNpManagerGetNpId`, `sceNpManagerGetAccountRegion`
(`psp2/np/mgr.h`), das estruturas de `psp2/np/common.h` e do SceNpDrmPackage (`psp2/npdrmpackage.h`). O resto foi
inferido do SDK do PS3/PS4 (mesma família de API) e do Vita3K, e está marcado como **inferido**. Para confirmar:

1. Implemente a função provisoriamente com `uint r0, uint r1, uint r2, uint r3` e logue os argumentos e o LR
   (`Log.Info`).
2. Rode o jogo (`VitaSharp.Desktop run PCSA00552`) até a chamada.
3. `VitaSharp.Desktop disasm PCSA00552 <LR-0x30> 40` mostra como os argumentos foram montados e como o retorno (r0)
   e os buffers de saída são usados depois: comparação com constantes, leitura de `[sp+x]` etc.

---

## 1. STUB: funções já registradas que descartam comportamento

### sceNpInit
- [x] **Prioridade:** STUB · **Biblioteca:** SceNpManager · **NID:** `0x04D9F484`
- **Protótipo:** `int sceNpInit(const SceNpCommunicationConfig *config, SceNpOptParam *opt);` (`psp2/np/mgr.h`)
- **Comportamento:** inicializa o NP do processo e guarda o `SceNpCommunicationId` padrão. Hoje retorna 0 sempre, não
  detecta chamada dupla e não guarda o commId, que as bibliotecas filhas (Basic, Message, WebApi) usariam como padrão
  e que permitiria a elas retornar `NOT_INITIALIZED` antes do `sceNpInit`.
- **Retorno/erros:** 0; `SCE_NP_ERROR_ALREADY_INITIALIZED` (0x80550001) na segunda chamada sem `sceNpTerm`.
- **Estruturas:** `SceNpCommunicationConfig` (0xC): +0 `npCommId*`, +4 `npCommPass*`, +8 `npCommSign*`.
  `SceNpCommunicationId` (0xC): `data[9]`, `term`, `num` (+0xA), `dummy`.
- **Implementação no VitaSharp:** `Np.sceNpInit` em `Online.cs`. Adicionar `bool _npInited` e `string? _commId`;
  `sceNpTerm` zera os dois. Expor `internal bool NpInited` para as outras classes NP consultarem via
  `Emu.Hle.Modules.OfType<Np>()` (padrão de `CommonDialog` → `AppUtil`).
- **Validação:** cenários `st_boot` (Sakatsuku) e `kz_boot` (Killzone usa NP) sem regressão; log com o commId.

### sceNpAuthCreateStartRequest
- [x] **Prioridade:** STUB · **Biblioteca:** SceNpCommon · **NID:** `0xED42079F`
- **Protótipo:** `int sceNpAuthCreateStartRequest(const SceNpAuthRequestParameter *param);` (inferido; layout no Vita3K)
- **Comportamento:** cria uma requisição de ticket NP e, ao terminar, chama `param->ticketCb(reqId, result, cbArg)`
  a partir do `sceNpCheckCallback`. Hoje retorna `SCE_NP_ERROR_SIGNED_OUT` na hora, sem criar requisição. Sakatsuku
  importa `sceNpAuthAbortRequest`, sinal de que trata requisições em andamento. Um jogo que só trata falha no
  callback, ou que guarda o ID e chama `Abort/Destroy` depois, fica num caminho inesperado.
- **Retorno/erros:** contrato offline proposto: `param == 0` ou `param->size` pequeno → `SCE_NP_ERROR_INVALID_ARGUMENT`
  (0x80550003). Senão retorna um `reqId` > 0, guarda `(ticketCb, cbArg)` e, no próximo `sceNpCheckCallback`, chama
  `ticketCb(reqId, SCE_NP_ERROR_SIGNED_OUT, cbArg)` uma vez. O código exato do callback sem conta está **a confirmar**.
  Alternativa conservadora: manter o retorno imediato de erro se Sakatsuku não chamar `sceNpCheckCallback` nesse
  fluxo.
- **Estruturas:** `SceNpAuthRequestParameter` (Vita3K): +0 `size`, +4 `version`, +8 `serviceId*`, +0xC `cookie*`,
  +0x10 `cookieSize`, +0x14 `entitlementId*`, +0x18 `consumedCount`, +0x1C `ticketCb`, +0x20 `cbArg`.
- **Implementação no VitaSharp:** `Np` em `Online.cs`: `Dictionary<int,(uint Cb,uint Arg,bool Done,bool Aborted)>
  _authReqs`. `sceNpCheckCallback` passa a percorrer `_authReqs` (fora do lock, como já faz com `_npCallbacks`) e
  usa `Kernel.GuestThread.Current?.CallGuest(cb, reqId, result, arg)`. `sceNpAuthDestroyRequest` remove;
  `sceNpAuthAbortRequest` (§2) marca abortada (callback com o código de abort, a confirmar, ou nenhum). O ticket nunca
  existe: `sceNpAuthGetTicket` continua devolvendo `SIGNED_OUT`.
- **Validação:** `st_game` (Sakatsuku) com log das chamadas `sceNpAuth*`; conferir que o jogo destrói a requisição.

### sceNpLookupUserProfileAsync
- [x] **Prioridade:** STUB · **Biblioteca:** SceNpUtility · **NID:** `0xE5285E0F`
- **Protótipo:** `int sceNpLookupUserProfileAsync(SceInt32 reqId, const SceNpId *targetNpId, SceNpUserInfo *userInfo, SceNpAboutMe *aboutMe, SceNpMyLanguages *languages, SceNpCountryCode *countryCode, void *avatarImage, void *option);` (inferido do PS3; a ordem exata não importa offline)
- **Comportamento:** hoje falha na hora com `SIGNED_OUT`, sem validar a request e sem marcar a transação, o que é
  incoerente com o `sceNpLookupPollAsync`. Deve seguir o modelo community (§0.4).
- **Retorno/erros:** 0 (iniciada) e `Result = OfflineResult`; `INVALID_ID` (0x8055070E) para request desconhecida.
- **Estruturas:** nenhuma é escrita.
- **Implementação no VitaSharp:** mover todos os `sceNpLookup*` de `Np` (`Online.cs`) para o módulo `NpUtility`
  (`NpCommunity.cs`) com uma instância própria de `NpCommunity`. `CreateTitleCtx`/`CreateRequest`/`Delete*`/`Abort`
  passam a validar IDs. Sem o mover, a mesma função ficaria registrada duas vezes.
- **Validação:** teste unitário do helper (criar ctx → request → async → poll = 0 com `result = 0x80550705`); `kz_boot`.

### sceNpLookupPollAsync
- [x] **Prioridade:** STUB · **Biblioteca:** SceNpUtility · **NID:** `0xFCDBA234`
- **Protótipo:** `int sceNpLookupPollAsync(SceInt32 reqId, SceInt32 *result);` (inferido)
- **Comportamento:** hoje retorna 0 e escreve `SIGNED_OUT` para qualquer ID, inclusive inexistente. Deve seguir o §0.4.
- **Retorno/erros:** 0 com `*result`; `INVALID_ID`; `INVALID_ARGUMENT` sem transação iniciada.
- **Estruturas:** —
- **Implementação no VitaSharp:** `NpUtility` → `NpCommunity.Poll`.
- **Validação:** idem acima.

Revisados e **mantidos** (corpo trivial, mas coerente com o perfil offline): `sceNpTerm`, `sceNpAuthInit/Term/
DestroyRequest/GetTicket/GetEntitlementById`, todos os `sceNpBasic*` atuais, `sceNpManagerGetNpId` (SIGNED_OUT),
`sceNpManagerGetChatRestrictionFlag`, `sceNpManagerGetContentRatingFlag`, `sceNpParty{Init,Term,CheckCallback,
RegisterHandler,GetMembers,GetMemberInfo,GetMemberVoiceInfo}`. Os `sceNpMessageDialog*` pertencem a SceCommonDialog
(pacote D).

---

## 2. SceNpCommon

### sceNpCmpNpId
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpCommon · **NID:** `0xFB8D82E5`
- **Protótipo:** `int sceNpCmpNpId(const SceNpId *npid1, const SceNpId *npid2);` (inferido; Vita3K usa essa assinatura)
- **Comportamento:** função pura que compara dois NpIds.
  1. `npid1 == 0 || npid2 == 0` → `SCE_NP_UTIL_ERROR_INVALID_ARGUMENT`.
  2. `byte[+0x1C] != 1` em qualquer um → `SCE_NP_UTIL_ERROR_INVALID_NP_ID`. O primeiro byte de `reserved` funciona
     como flag "ID válido": o Vita3K chama de `isIdValid` e o grava como 1 em `GetNpId`; o RPCS3 (PS3) testa
     `reserved[0] != 1`. O Vita3K testa `!= 0`; usar `!= 1` só se um jogo mostrar o contrário.
  3. `strncmp(onlineId.data, 16)` diferente → `NOT_MATCH`.
  4. `opt[0..3]` (+0x14) diferente (memcmp de 4 bytes) → `NOT_MATCH`.
  5. Plataforma `opt[4..7]` (+0x18): se igual → 0. Se diferente: regra do PS3 (RPCS3), "se qualquer um for zero, casa",
     ou seja, `NOT_MATCH` só quando `opt[4] != 0` nos dois. O Vita3K trata "um zero e outro não" como `NOT_MATCH`. Qual
     regra o firmware do Vita usa está **a confirmar**; adotar a do PS3, que tem engenharia reversa do firmware.
  6. Retorna 0.
- **Retorno/erros:** 0 (iguais); `SCE_NP_UTIL_ERROR_INVALID_ARGUMENT` 0x80550601; `SCE_NP_UTIL_ERROR_INVALID_NP_ID`
  0x80550605; `SCE_NP_UTIL_ERROR_NOT_MATCH` 0x80550609.
- **Estruturas:** `SceNpId` (0x24): +0x00 `onlineId.data[16]`, +0x10 `term`, +0x11 `dummy[3]`, +0x14 `opt[8]`
  (opt[4..7] = plataforma, ex. `"psp2"`), +0x1C `reserved[8]` (`reserved[0]` = 1 quando válido). O vita-headers
  chama +0x14 de `npId[8]` e +0x1C de `unk[8]`.
- **Implementação no VitaSharp:** `Np` (`Online.cs`). Criar um helper estático `NpIdUtil.Compare(uint a, uint b)`,
  reaproveitado por `sceNpCmpNpIdInOrder`. Leitura via `GuestMemory.Span(addr, 0x24)`.
- **Validação:** teste unitário com NpIds montados em memória (iguais; online ID diferente; plataforma
  `"psp2"` vs zero; reserved[0]=0). Em Sakatsuku (`st_game`), a chamada não pode gerar `Unimplemented`.

### sceNpGetPlatformType
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpCommon · **NID:** `0xE9A003DE`
- **Protótipo:** `int sceNpGetPlatformType(const SceNpId *npId);` (**inferido**; retorna o tipo em r0. Confirmar pelo
  §0.6 se não há um segundo argumento `SceNpPlatformType *type`. Se o jogo passar um ponteiro em r1 e ler `[r1]` depois
  da chamada, a forma é `(npId, *type)` com retorno 0.)
- **Comportamento:** função pura. Lê `opt[4..7]` (+0x18) e mapeia: os 4 bytes zerados → `NONE` (0); `"ps3\0"` → PS3 (1);
  `"psp2"` → VITA (2); `"ps4\0"` → PS4 (3); qualquer outro → `SCE_NP_ERROR_UNKNOWN_PLATFORM_TYPE`.
- **Retorno/erros:** tipo ≥ 0; `SCE_NP_ERROR_INVALID_ARGUMENT` 0x80550003 (`npId == 0`);
  `SCE_NP_ERROR_UNKNOWN_PLATFORM_TYPE` 0x80550004.
- **Estruturas:** `SceNpId` como em `sceNpCmpNpId`. Os valores do enum (NONE=0, PS3=1, VITA=2, PS4=3) vêm do SDK do
  PS4. `"psp2"` está confirmado pelo `sceNpManagerGetNpId` do Vita3K. `"ps3\0"` vem do RPCS3. `"ps4\0"` está
  **a confirmar**.
- **Implementação no VitaSharp:** `Np` (`Online.cs`), tabela estática `byte[4]` ↔ tipo, compartilhada com
  `sceNpSetPlatformType`.
- **Validação:** teste unitário ida e volta (Set → Get). Em Invizimals, log do retorno; o jogo não deve tratar
  falha nesse ponto.

### sceNpSetPlatformType
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpCommon · **NID:** `0xE807D0BC`
- **Protótipo:** `int sceNpSetPlatformType(SceNpId *npId, SceNpPlatformType platformType);` (inferido do PS4; confirmar pelo §0.6)
- **Comportamento:** função pura. Grava os 4 bytes de plataforma em `opt[4..7]` (+0x18): NONE → `00 00 00 00`; PS3 →
  `"ps3\0"`; VITA → `"psp2"`; PS4 → `"ps4\0"`. Não altera mais nada.
- **Retorno/erros:** 0; `SCE_NP_ERROR_INVALID_ARGUMENT` (`npId == 0`); `SCE_NP_ERROR_UNKNOWN_PLATFORM_TYPE` (tipo fora
  de 0..3).
- **Estruturas:** idem `sceNpGetPlatformType`.
- **Implementação no VitaSharp:** `Np` (`Online.cs`).
- **Validação:** idem `sceNpGetPlatformType`.

### sceNpAuthAbortRequest
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpCommon · **NID:** `0xE2582575`
- **Protótipo:** `int sceNpAuthAbortRequest(SceNpAuthRequestId reqId);` (inferido; `SceNpAuthRequestId` = `SceInt32`)
- **Comportamento:** cancela uma requisição de ticket em andamento; o callback recebe o resultado de abort. Offline,
  sem o STUB de `sceNpAuthCreateStartRequest`, nenhuma requisição existe e o jogo só chama isto no caminho de limpeza.
- **Retorno/erros:** ID conhecido em `_authReqs` → marca abortado e retorna 0. ID desconhecido → **retornar 0** (limpeza
  idempotente). O firmware provavelmente devolve um `SCE_NP_AUTH_ERROR_*` de "request not found", com valor
  **a confirmar** (não está no Vita3K). Logar com `Log.Debug`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Np` (`Online.cs`), junto do STUB de `sceNpAuthCreateStartRequest`.
- **Validação:** `st_game`; o import deixa de aparecer em `VitaSharp.Desktop imports PCSG00198`.

### Demais funções SceNpCommon (P1 / FORA)

| Função | NID | Protótipo (inferido salvo indicação) | Contrato offline |
| --- | --- | --- | --- |
| `sceNpCmpNpIdInOrder` | `0x6BC8150A` | `int (const SceNpId *a, const SceNpId *b, SceInt32 *order)` | Pura. Argumento nulo (incl. `order`) → 0x80550601; `reserved[0] != 1` → 0x80550605. `order = sign(strncmp(onlineId,16))`; se 0, `sign(memcmp(opt[0..3]))`; se 0: plataformas ambas zero → 0, ambas não-zero → `sign(memcmp(opt[4..7]))`, só uma zero → 0 (mesma regra "zero casa" do `CmpNpId`, a confirmar). Retorna 0 |
| `sceNpCmpOnlineId` | `0x8327C26F` | `int (const SceNpOnlineId *a, const SceNpOnlineId *b)` | Pura. Nulo → 0x80550601; `strncmp(data,16) != 0` → 0x80550609; senão 0 |
| `sceNpCommonBase64Encode` | `0x5601E9A2` | **inferido:** `int (char *dst, SceSize dstSize, const void *src, SceSize srcSize)`; ordem e retorno (tamanho escrito ou 0) **a confirmar** por disassembly de um chamador | Pura. Base64 RFC 4648, alfabeto padrão `A–Z a–z 0–9 + /`, padding `=`, terminador `\0`. Saída = `4*ceil(n/3)` + 1; `dstSize` menor → 0x80550601 (código a confirmar). Usar `Convert.ToBase64String` |
| `sceNpAuthGetEntitlementByIdPrefix` | `0xC8E51CCA` | `int (const void *ticket, SceSize ticketSize, const char *prefix, SceNpEntitlement *ent, SceSize num)` | Sem ticket: `SCE_NP_ERROR_SIGNED_OUT` (igual ao `GetEntitlementById` atual) |
| `sceNpAuthGetEntitlementIdList` | `0x3377CD37` | `int (const void *ticket, SceSize ticketSize, SceNpEntitlementId *list, SceSize num)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpAuthGetTicketParam` | `0xC1E23E01` | `int (const void *ticket, SceSize ticketSize, SceInt32 paramId, SceNpTicketParam *param)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpCommonGetNpEnviroment` | `0xAD6403E0` | **inferido:** `int (char *env)`; tamanho do buffer a confirmar | Escrever `"np"` (ambiente de produção) com `\0`, retornar 0. Nulo → 0x80550003 |
| `sceNpCommonGetSystemSwVersion` | `0x775D0177` | **inferido**; forma (retorno direto ou struct) a confirmar | Retornar a mesma versão que o VitaSharp reporta em `sceKernelGetSystemSwVersion` (pacote A). Se for struct, copiar o layout `SceKernelFwInfo` daquele pacote |
| `sceNpCommonMallocNpServerName` | `0x95EFA15C` | interno | **FORA**: helper usado pelos módulos NP do sistema para montar hostnames. Com HLE dessas bibliotecas, nenhum jogo chama. Se aparecer, retornar 0x80550003 |
| `sceNpCommonFreeNpServerName` | `0x1D81E445` | interno | **FORA**: par do anterior. Retornar 0 |

---

## 3. SceNpManager

### sceNpManagerGetAccountRegion
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpManager · **NID:** `0xFE835967`
- **Protótipo:** `int sceNpManagerGetAccountRegion(SceNpCountryCode *country_code, int *language_code);` (`psp2/np/mgr.h`)
- **Comportamento:** devolve o país (2 letras + `\0`, ex. `"us"`) e o idioma da conta PSN. Sem conta não há região.
- **Retorno/erros:** `country_code == 0 || language_code == 0` → `SCE_NP_MANAGER_ERROR_INVALID_ARGUMENT` (0x80550503);
  senão `SCE_NP_ERROR_SIGNED_OUT` (0x80550006), sem escrever nada, coerente com `sceNpManagerGetNpId`.
  **Plano B:** se Invizimals tratar a falha como fatal (trava ou diálogo em loop na abertura), retornar 0 com
  `country = "us"` e `language_code = ` o idioma do sistema (`SCE_SYSTEM_PARAM_LANG_*` que o `AppUtil` já reporta).
  O Vita real guarda a região da conta localmente, então esse comportamento também é plausível.
- **Estruturas:** `SceNpCountryCode` (4): `data[2]` + `term` + `padding` (o vita-headers declara `data[4]`).
- **Implementação no VitaSharp:** `Np` (`Online.cs`), uma linha.
- **Validação:** Invizimals (não há cenário ainda; criar `tests/scenarios/inv_boot.txt` com `title=PCSA00552`,
  `args=--seconds=60 --dump-every=60` ao implementar). O boot deve passar do menu inicial.

### Demais funções SceNpManager (P1)

| Função | NID | Protótipo (inferido) | Contrato offline |
| --- | --- | --- | --- |
| `sceNpManagerGetCachedParam` | `0x43DC48A1` | `int (SceNpManagerCacheParam *param)`; layout a confirmar | Sem conta: `SCE_NP_ERROR_SIGNED_OUT`; nulo → 0x80550503 |
| `sceNpAuthCreateOAuthRequest` | `0x555EB58C` | `int (void)` → reqId | Retorna ID > 0 (tabela local) |
| `sceNpAuthDeleteOAuthRequest` | `0x8C94C7A5` | `int (SceInt32 reqId)` | Remove; 0 (ID desconhecido → 0x80550503) |
| `sceNpAuthAbortOAuthRequest` | `0xECBC7614` | `int (SceInt32 reqId)` | 0 |
| `sceNpAuthGetAuthorizationCode` | `0x65DFB208` | `int (SceInt32 reqId, const SceNpAuthGetAuthorizationCodeParameter *param, SceNpAuthorizationCode *code, SceInt32 *issuerId)` | `SCE_NP_ERROR_SIGNED_OUT` (bloqueante no real; aqui retorna na hora) |

---

## 4. SceNpScore

Modelo: §0.4. Módulo `NpScore` em `NpCommunity.cs`. Tipos: `SceNpScoreBoardId` = `SceUInt32`, `SceNpScorePcId` =
`SceInt32`, `SceNpScoreValue` = `SceInt64`, `SceNpScoreRankNumber` = `SceUInt32`. Os protótipos são **inferidos** do SDK
do PS3/PS4 (no Vita o `Async` tem os mesmos argumentos do síncrono, sem argumento extra de prioridade).

### sceNpScoreInit
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpScore · **NID:** `0x0433069F`
- **Protótipo:** `int sceNpScoreInit(SceInt32 threadPriority, SceInt32 cpuAffinityMask, void *option);` (inferido)
- **Comportamento:** inicializa a biblioteca de ranking (o real cria uma thread de trabalho; aqui nada).
- **Retorno/erros:** 0; `SCE_NP_COMMUNITY_ERROR_ALREADY_INITIALIZED` 0x80550701.
- **Estruturas:** —
- **Implementação no VitaSharp:** `NpScore.sceNpScoreInit` → `_lib.Init()`.
- **Validação:** `st_boot`/`st_game`; `imports PCSG00198` sem SceNpScore faltando.

### sceNpScoreCreateTitleCtx
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpScore · **NID:** `0x5685F225`
- **Protótipo:** `int sceNpScoreCreateTitleCtx(const SceNpCommunicationId *communicationId, const SceNpCommunicationPassphrase *passphrase, const SceNpId *selfNpId);` (inferido; mesma assinatura do `sceNpTusCreateTitleCtx` do Vita3K)
- **Comportamento:** cria o contexto do título. Offline funciona, porque é local.
- **Retorno/erros:** ID > 0; `NOT_INITIALIZED` 0x80550702; `INSUFFICIENT_ARGUMENT` 0x8055070C (`communicationId == 0`);
  `TOO_MANY_OBJECTS` 0x80550706.
- **Estruturas:** `SceNpCommunicationId` (0xC), `SceNpCommunicationPassphrase` (0x80), `SceNpId` (0x24): ver `psp2/np/common.h`.
- **Implementação no VitaSharp:** `_lib.CreateTitleCtx(commId, passphrase)`; logar o commId.
- **Validação:** idem.

### sceNpScoreDeleteTitleCtx
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpScore · **NID:** `0xF52EA88A`
- **Protótipo:** `int sceNpScoreDeleteTitleCtx(SceInt32 titleCtxId);` (inferido)
- **Comportamento:** destrói o contexto e as requests dele.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_ID` 0x8055070E.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_lib.DeleteTitleCtx`.
- **Validação:** idem.

### sceNpScoreCreateRequest
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpScore · **NID:** `0xD30D1993`
- **Protótipo:** `int sceNpScoreCreateRequest(SceInt32 titleCtxId);` (inferido)
- **Comportamento:** cria um ID de transação ligado ao contexto.
- **Retorno/erros:** ID > 0; `NOT_INITIALIZED`; `INVALID_ID`; `TOO_MANY_OBJECTS`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_lib.CreateRequest`.
- **Validação:** idem.

### sceNpScoreDeleteRequest
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpScore · **NID:** `0xFFF24BB1`
- **Protótipo:** `int sceNpScoreDeleteRequest(SceInt32 reqId);` (inferido)
- **Comportamento:** libera a request (aborta se estiver em andamento).
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_ID`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_lib.DeleteRequest`.
- **Validação:** idem.

### sceNpScoreRecordScoreAsync
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpScore · **NID:** `0x24B09634`
- **Protótipo:** `int sceNpScoreRecordScoreAsync(SceInt32 reqId, SceNpScoreBoardId boardId, SceNpScoreValue score, const SceNpScoreComment *scoreComment, const SceNpScoreGameInfo *gameInfo, SceNpScoreRankNumber *tmpRank, const SceRtcTick *compareDate, void *option);` (inferido; `score` 64 bits em r2:r3, o resto na pilha)
- **Comportamento:** envia uma pontuação ao placar. Offline, a transação termina na hora com erro.
- **Retorno/erros:** 0 (iniciada); `NOT_INITIALIZED`; `INVALID_ID`; `TRANSACTION_ALREADY_END` 0x80550712. O
  `PollAsync` devolve `OfflineResult` (0x80550705). `*tmpRank` não é escrito.
- **Estruturas:** nenhuma é escrita.
- **Implementação no VitaSharp:** `[Hle] int sceNpScoreRecordScoreAsync(int reqId) => _lib.StartAsync(reqId);`.
- **Validação:** `st_game`. Fluxo esperado no log: CreateRequest → RecordScoreAsync → PollAsync (result 0x80550705)
  → DeleteRequest, e o jogo continua.

### sceNpScoreGetRankingByRangeAsync
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpScore · **NID:** `0xC45E3FCD`
- **Protótipo:** `int sceNpScoreGetRankingByRangeAsync(SceInt32 reqId, SceNpScoreBoardId boardId, SceNpScoreRankNumber startSerialRank, SceNpScoreRankData *rankArray, SceSize rankArraySize, SceNpScoreComment *commentArray, SceSize commentArraySize, SceNpScoreGameInfo *infoArray, SceSize infoArraySize, SceSize arrayNum, SceRtcTick *lastSortDate, SceNpScoreRankNumber *totalRecord, void *option);` (inferido)
- **Comportamento:** lê um intervalo do ranking. Offline, termina com erro e não preenche nada.
- **Retorno/erros:** como `RecordScoreAsync`.
- **Estruturas:** nenhuma é escrita.
- **Implementação no VitaSharp:** `_lib.StartAsync(reqId)`.
- **Validação:** `st_game` (tela de ranking online deve mostrar erro de conexão e voltar).

### sceNpScoreGetRankingByNpIdPcIdAsync
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpScore · **NID:** `0xCE3A9544`
- **Protótipo:** `int sceNpScoreGetRankingByNpIdPcIdAsync(SceInt32 reqId, SceNpScoreBoardId boardId, const SceNpScoreNpIdPcId *idArray, SceSize idArraySize, SceNpScorePlayerRankData *rankArray, SceSize rankArraySize, SceNpScoreComment *commentArray, SceSize commentArraySize, SceNpScoreGameInfo *infoArray, SceSize infoArraySize, SceSize arrayNum, SceRtcTick *lastSortDate, SceNpScoreRankNumber *totalRecord, void *option);` (inferido)
- **Comportamento:** ranking de jogadores específicos (NpId + id de personagem). Offline: erro.
- **Retorno/erros:** como `RecordScoreAsync`.
- **Estruturas:** nenhuma é escrita.
- **Implementação no VitaSharp:** `_lib.StartAsync(reqId)`.
- **Validação:** `st_game`.

### sceNpScorePollAsync
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpScore · **NID:** `0x9F2A7AC9`
- **Protótipo:** `int sceNpScorePollAsync(SceInt32 reqId, SceInt32 *result);` (inferido)
- **Comportamento:** consulta sem bloquear. Retorna 0 (FINISHED) com `*result` = resultado da transação, ou 1
  (RUNNING). Aqui nunca há RUNNING.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_ID`; `INVALID_ARGUMENT` 0x80550704 (`result == 0` ou sem
  transação).
- **Estruturas:** `*result` (`SceInt32`).
- **Implementação no VitaSharp:** `_lib.Poll(reqId, result)`. Para confirmar FINISHED=0/RUNNING=1, o laço de polling
  no disassembly de Sakatsuku deve comparar r0 com 1 (ou testar ≠ 0) antes de ler `*result`.
- **Validação:** `st_game`; o jogo não pode ficar preso num laço de polling (ver o perfil de chamadas).

### Demais funções SceNpScore (P1)

Todas usam o helper do §0.4: as transações síncronas retornam `OfflineResult` e as `Async` retornam 0 com `Result`
marcado.

| Função | NID | Protótipo resumido (inferido) | Retorno offline |
| --- | --- | --- | --- |
| `sceNpScoreTerm` | `0x2050F98F` | `int (void)` | `_lib.Term()` |
| `sceNpScoreAbortRequest` | `0xA7E93CE1` | `int (SceInt32 reqId)` | `_lib.Abort` |
| `sceNpScoreWaitAsync` | `0x31733BF3` | `int (SceInt32 reqId, SceInt32 *result)` | `_lib.Poll` (não bloqueia) |
| `sceNpScoreSetTimeout` | `0x5EF44841` | `int (SceInt32 id, SceInt32 resolveRetry, SceUInt32 resolveTimeout, SceUInt32 connTimeout, SceUInt32 sendTimeout, SceUInt32 recvTimeout)` | `_lib.SetTimeout(id)` → 0 |
| `sceNpScoreSetPlayerCharacterId` | `0x53D77883` | `int (SceInt32 id, SceNpScorePcId pcId)` | valida o ID como `SetTimeout`; 0 |
| `sceNpScoreChangeModeForOtherSaveDataOwners` | `0x7C68D91B` | `int (void)` | 0 (`NOT_INITIALIZED` se não iniciado) |
| `sceNpScoreGetBoardInfo` | `0x00F90E7B` | `int (reqId, boardId, SceNpScoreBoardInfo *info, void *opt)` | `OfflineResult` |
| `sceNpScoreGetBoardInfoAsync` | `0x3CD9974E` | idem | 0 / Poll → erro |
| `sceNpScoreRecordScore` | `0x320C0277` | como o `RecordScoreAsync` | `OfflineResult` |
| `sceNpScoreRecordGameData` | `0xC2862B67` | `int (reqId, boardId, SceNpScoreValue score, SceSize totalSize, SceSize sendSize, const void *data, void *opt)` | `OfflineResult` |
| `sceNpScoreRecordGameDataAsync` | `0x40573917` | idem | 0 / Poll → erro |
| `sceNpScoreGetGameData` | `0xDFAD64D3` | `int (reqId, boardId, const SceNpId *npId, SceSize *totalSize, SceSize recvSize, void *data, void *opt)` | `OfflineResult` |
| `sceNpScoreGetGameDataAsync` | `0xCE416993` | idem | 0 / Poll → erro |
| `sceNpScoreGetRankingByNpId` | `0xBAE55B34` | `int (reqId, boardId, const SceNpId *npIdArray, SceSize npIdArraySize, rankArray, size, commentArray, size, infoArray, size, arrayNum, lastSortDate*, totalRecord*, opt)` | `OfflineResult` |
| `sceNpScoreGetRankingByNpIdAsync` | `0x45CD1D00` | idem | 0 / Poll → erro |
| `sceNpScoreGetRankingByNpIdPcId` | `0x871F28AA` | como a variante Async | `OfflineResult` |
| `sceNpScoreGetRankingByRange` | `0x427D3412` | como a variante Async | `OfflineResult` |
| `sceNpScoreGetFriendsRanking` | `0x4F795659` | `int (reqId, boardId, SceInt32 includeSelf, rankArray, size, commentArray, size, infoArray, size, arrayNum, lastSortDate*, totalRecord*, opt)` | `OfflineResult` |
| `sceNpScoreGetFriendsRankingAsync` | `0x7090ED11` | idem | 0 / Poll → erro |
| `sceNpScoreCensorComment` | `0xA0C94D46` | `int (reqId, const char *comment, void *opt)` | `OfflineResult` |
| `sceNpScoreCensorCommentAsync` | `0xAA0BBF8E` | idem | 0 / Poll → erro |
| `sceNpScoreSanitizeComment` | `0x6FD2041A` | `int (reqId, const char *comment, char *sanitized, void *opt)` | `OfflineResult` (não copia) |
| `sceNpScoreSanitizeCommentAsync` | `0x15981858` | idem | 0 / Poll → erro |

---

## 5. SceNpTus (TUS + TSS)

Modelo: §0.4, com uma única instância `NpCommunity` para `sceNpTus*` e `sceNpTss*`, que compartilham requests (como
no Vita3K). Módulo `NpTus` em `NpCommunity.cs`. Tipos: `SceNpTusSlotId` = `SceInt32`, `SceNpTssSlotId` = `SceInt32`
(0..15 segundo o Vita3K), variáveis `SceInt64`. As variantes **VUser** trocam `const SceNpId *targetNpId` por
`const SceNpTusVirtualUserId *targetVirtualUserId`; offline o contrato é o mesmo. Protótipos **inferidos** do SDK
PS4/PS Vita análogo e do Vita3K (`sceNpTss*`).

### sceNpTusInit
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0xB2141F8D`
- **Protótipo:** `int sceNpTusInit(SceInt32 threadPriority, SceInt32 cpuAffinityMask, const void *option);` (Vita3K)
- **Comportamento:** inicializa o TUS/TSS. O Vita3K rejeita `option != NULL` com `INVALID_ARGUMENT`; aqui ignorar o
  `option`, porque rejeitar não traz benefício.
- **Retorno/erros:** 0; `ALREADY_INITIALIZED` 0x80550701.
- **Estruturas:** —
- **Implementação no VitaSharp:** `NpTus.sceNpTusInit` → `_lib.Init()`.
- **Validação:** `st_boot`; `imports PCSG00198` sem SceNpTus faltando.

### sceNpTusTerm
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0x7EDC33B3`
- **Protótipo:** `int sceNpTusTerm(void);` (Vita3K)
- **Comportamento:** encerra a biblioteca e descarta contextos e requests.
- **Retorno/erros:** 0; `NOT_INITIALIZED` 0x80550702.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_lib.Term()`.
- **Validação:** idem.

### sceNpTusCreateTitleCtx
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0xF1D9529E`
- **Protótipo:** `int sceNpTusCreateTitleCtx(const SceNpCommunicationId *communicationId, const SceNpCommunicationPassphrase *passphrase, const SceNpId *selfNpId);` (Vita3K)
- **Comportamento:** cria o contexto do título (local).
- **Retorno/erros:** ID > 0; `NOT_INITIALIZED`; `INSUFFICIENT_ARGUMENT` 0x8055070C; `TOO_MANY_OBJECTS` 0x80550706 (32).
- **Estruturas:** ver `sceNpScoreCreateTitleCtx`.
- **Implementação no VitaSharp:** `_lib.CreateTitleCtx`.
- **Validação:** idem.

### sceNpTusDeleteTitleCtx
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0xD53D3692`
- **Protótipo:** `int sceNpTusDeleteTitleCtx(SceInt32 titleCtxId);` (Vita3K)
- **Comportamento:** remove o contexto e as requests dele.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_ID` 0x8055070E.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_lib.DeleteTitleCtx`.
- **Validação:** idem.

### sceNpTusCreateRequest
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0x99DC7420`
- **Protótipo:** `int sceNpTusCreateRequest(SceInt32 titleCtxId);` (Vita3K)
- **Comportamento:** cria uma request (vale para TUS e TSS).
- **Retorno/erros:** ID > 0; `NOT_INITIALIZED`; `INVALID_ID`; `TOO_MANY_OBJECTS`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_lib.CreateRequest`.
- **Validação:** idem.

### sceNpTusDeleteRequest
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0xB46B5D7F`
- **Protótipo:** `int sceNpTusDeleteRequest(SceInt32 requestId);` (Vita3K)
- **Comportamento:** libera a request.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_ID`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_lib.DeleteRequest`.
- **Validação:** idem.

### sceNpTusAbortRequest
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0x2C5F1162`
- **Protótipo:** `int sceNpTusAbortRequest(SceInt32 requestId);` (Vita3K)
- **Comportamento:** aborta a transação em andamento. Se a request ainda não tem resultado, ele passa a ser
  `ABORTED`; se já terminou, nada muda.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_ID`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_lib.Abort`.
- **Validação:** idem.

### sceNpTusPollAsync
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0xC003C588`
- **Protótipo:** `int sceNpTusPollAsync(SceInt32 requestId, SceInt32 *result);` (Vita3K)
- **Comportamento:** igual a `sceNpScorePollAsync`.
- **Retorno/erros:** 0 com `*result`; `NOT_INITIALIZED`; `INVALID_ID`; `INVALID_ARGUMENT` (sem transação ou `result == 0`).
- **Estruturas:** `*result`.
- **Implementação no VitaSharp:** `_lib.Poll`.
- **Validação:** idem.

### sceNpTusGetDataAsync
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0x883AC44A`
- **Protótipo:** `int sceNpTusGetDataAsync(SceInt32 reqId, const SceNpId *targetNpId, SceNpTusSlotId slotId, SceNpTusDataStatus *dataStatus, SceSize dataStatusSize, void *data, SceSize recvSize, void *option);` (inferido)
- **Comportamento:** baixa o blob de dados de um slot do usuário. Offline: erro na conclusão. O Vita3K retorna
  `NO_RESOURCE` sem iniciar a transação; aqui ela é iniciada, para o jogo seguir o caminho de polling.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_ID`; `TRANSACTION_ALREADY_END`. Poll → `OfflineResult`.
- **Estruturas:** `dataStatus` não é escrito.
- **Implementação no VitaSharp:** `_lib.StartAsync(reqId)`.
- **Validação:** `st_game` (dados online de Sakatsuku: o jogo deve mostrar falha de conexão e continuar).

### sceNpTusSetDataAsync
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0x9FABA1E5`
- **Protótipo:** `int sceNpTusSetDataAsync(SceInt32 reqId, const SceNpId *targetNpId, SceNpTusSlotId slotId, SceSize totalSize, SceSize sendSize, const void *data, const SceNpTusDataInfo *info, SceSize infoStructSize, void *option);` (inferido)
- **Comportamento:** envia um blob para o slot. Offline: erro.
- **Retorno/erros:** como `GetDataAsync`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_lib.StartAsync(reqId)`.
- **Validação:** idem.

### sceNpTusGetMultiSlotDataStatusAsync
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0x520975C3`
- **Protótipo:** `int sceNpTusGetMultiSlotDataStatusAsync(SceInt32 reqId, const SceNpId *targetNpId, const SceNpTusSlotId *slotIdArray, SceNpTusDataStatus *statusArray, SceSize statusArraySize, SceSize arrayNum, void *option);` (inferido)
- **Comportamento:** lê o status (existência, data de alteração, tamanho) de vários slots. Offline: erro.
- **Retorno/erros:** como `GetDataAsync`.
- **Estruturas:** `statusArray` não é escrito.
- **Implementação no VitaSharp:** `_lib.StartAsync(reqId)`.
- **Validação:** idem.

### sceNpTusDeleteMultiSlotDataAsync
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0x0CFAC523`
- **Protótipo:** `int sceNpTusDeleteMultiSlotDataAsync(SceInt32 reqId, const SceNpId *targetNpId, const SceNpTusSlotId *slotIdArray, SceSize arrayNum, void *option);` (inferido)
- **Comportamento:** apaga blobs de vários slots. Offline: erro.
- **Retorno/erros:** como `GetDataAsync`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `_lib.StartAsync(reqId)`.
- **Validação:** idem.

### sceNpTssGetSmallStorageAsync
- [x] **Prioridade:** P0 (Sakatsuku) · **Biblioteca:** SceNpTus · **NID:** `0xEA588F23`
- **Protótipo:** `int sceNpTssGetSmallStorageAsync(SceInt32 requestId, void *data, SceSize maxSize, SceSize *contentLength, const void *option);` (Vita3K)
- **Comportamento:** baixa o "small storage" do título (TSS, até 64 KiB, dados que a Sony hospeda por título, como
  notícias e configurações). Offline: erro. O Vita3K zera `*contentLength`; fazer o mesmo, que é inofensivo.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `INVALID_ID`; `INSUFFICIENT_ARGUMENT` (`data == 0 || contentLength == 0`);
  `TRANSACTION_ALREADY_END`. Poll → `OfflineResult`. (O Vita3K também rejeita `option != 0` e `maxSize > 65536` com
  `INVALID_ARGUMENT`; não é necessário.)
- **Estruturas:** `*contentLength` (`SceSize`) = 0.
- **Implementação no VitaSharp:** `if (contentLength != 0) GuestMemory.W32(contentLength, 0); return _lib.StartAsync(reqId);`
- **Validação:** idem.

### Demais funções SceNpTus (P1)

Regra: o sufixo `Async` retorna 0 e marca `Result`; sem o sufixo, retorna `OfflineResult`. Nenhuma escreve na saída.

| Função | NID | Protótipo resumido (inferido) | Retorno offline |
| --- | --- | --- | --- |
| `sceNpTusWaitAsync` | `0x3907A8BC` | `int (SceInt32 reqId, SceInt32 *result)` | `_lib.Poll` |
| `sceNpTusSetTimeout` | `0xC4BB64A7` | `int (SceInt32 id, SceInt32 resolveRetry, SceUInt32 resolveTimeout, SceUInt32 connTimeout, SceUInt32 sendTimeout, SceUInt32 recvTimeout)`; forma a confirmar, só `id` é usado | `_lib.SetTimeout(id)` |
| `sceNpTusChangeModeForOtherSaveDataOwners` | `0x88A2D551` | `int (void)` | 0 |
| `sceNpTusSetMultiSlotVariable` | `0xA3F00EE6` | `int (reqId, const SceNpId *target, const SceNpTusSlotId *slotIdArray, const SceInt64 *variableArray, SceSize arrayNum, void *opt)` | `OfflineResult` |
| `sceNpTusSetMultiSlotVariableAsync` | `0xA61AEDB0` | idem | 0 / Poll → erro |
| `sceNpTusSetMultiSlotVariableVUser` | `0x9A1E4472` | idem, VUser | `OfflineResult` |
| `sceNpTusSetMultiSlotVariableVUserAsync` | `0x2E9A7FD0` | idem | 0 / Poll → erro |
| `sceNpTusGetMultiSlotVariable` | `0x5EB05C7A` | `int (reqId, target, slotIdArray, SceNpTusVariable *variableArray, SceSize variableArraySize, SceSize arrayNum, void *opt)` | `OfflineResult` |
| `sceNpTusGetMultiSlotVariableAsync` | `0xD26B9F4C` | idem | 0 / Poll → erro |
| `sceNpTusGetMultiSlotVariableVUser` | `0xFE92D6CB` | idem, VUser | `OfflineResult` |
| `sceNpTusGetMultiSlotVariableVUserAsync` | `0xF91887EC` | idem | 0 / Poll → erro |
| `sceNpTusGetMultiUserVariable` | `0xC5656132` | `int (reqId, const SceNpId *targetArray, SceNpTusSlotId slotId, SceNpTusVariable *variableArray, SceSize variableArraySize, SceSize arrayNum, void *opt)` | `OfflineResult` |
| `sceNpTusGetMultiUserVariableAsync` | `0xBBEFCD26` | idem | 0 / Poll → erro |
| `sceNpTusGetMultiUserVariableVUser` | `0x0374C127` | idem, VUser | `OfflineResult` |
| `sceNpTusGetMultiUserVariableVUserAsync` | `0xD51B39ED` | idem | 0 / Poll → erro |
| `sceNpTusGetFriendsVariable` | `0x47E1716A` | `int (reqId, SceNpTusSlotId slotId, SceInt32 includeSelf, SceInt32 sortType, SceNpTusVariable *variableArray, SceSize variableArraySize, SceSize arrayNum, void *opt)` | `OfflineResult` |
| `sceNpTusGetFriendsVariableAsync` | `0xDFC8F03D` | idem | 0 / Poll → erro |
| `sceNpTusAddAndGetVariable` | `0x4099FC56` | `int (reqId, target, SceNpTusSlotId slotId, SceInt64 inVariable, SceNpTusVariable *outVariable, SceSize outVariableSize, void *opt)` (`inVariable` em r2:r3) | `OfflineResult` |
| `sceNpTusAddAndGetVariableAsync` | `0x0A5D05F1` | idem | 0 / Poll → erro |
| `sceNpTusAddAndGetVariableVUser` | `0xE1D6D4CF` | idem, VUser | `OfflineResult` |
| `sceNpTusAddAndGetVariableVUserAsync` | `0xBB5E5E29` | idem | 0 / Poll → erro |
| `sceNpTusTryAndSetVariable` | `0xB0A6F839` | `int (reqId, target, slotId, SceInt32 opeType, SceInt64 variable, SceNpTusVariable *resultVariable, SceSize resultVariableSize, void *opt)` | `OfflineResult` |
| `sceNpTusTryAndSetVariableAsync` | `0x44947D07` | idem | 0 / Poll → erro |
| `sceNpTusTryAndSetVariableVUser` | `0x6B2A54EB` | idem, VUser | `OfflineResult` |
| `sceNpTusTryAndSetVariableVUserAsync` | `0x518A64B9` | idem | 0 / Poll → erro |
| `sceNpTusDeleteMultiSlotVariable` | `0x246DB508` | `int (reqId, target, const SceNpTusSlotId *slotIdArray, SceSize arrayNum, void *opt)` | `OfflineResult` |
| `sceNpTusDeleteMultiSlotVariableAsync` | `0xFC0C5159` | idem | 0 / Poll → erro |
| `sceNpTusDeleteMultiSlotVariableVUser` | `0x420350F9` | idem, VUser | `OfflineResult` |
| `sceNpTusDeleteMultiSlotVariableVUserAsync` | `0x3B44D0E4` | idem | 0 / Poll → erro |
| `sceNpTusSetData` | `0x33E29819` | como o `SetDataAsync` | `OfflineResult` |
| `sceNpTusSetDataVUser` | `0x6A175F75` | idem, VUser | `OfflineResult` |
| `sceNpTusSetDataVUserAsync` | `0x1989CC51` | idem | 0 / Poll → erro |
| `sceNpTusGetData` | `0x9677BDD6` | como o `GetDataAsync` | `OfflineResult` |
| `sceNpTusGetDataVUser` | `0xCCB9E791` | idem, VUser | `OfflineResult` |
| `sceNpTusGetDataVUserAsync` | `0x9E8E1CF8` | idem | 0 / Poll → erro |
| `sceNpTusGetMultiSlotDataStatus` | `0xD97E51EC` | como o `GetMultiSlotDataStatusAsync` | `OfflineResult` |
| `sceNpTusGetMultiSlotDataStatusVUser` | `0x50FC680F` | idem, VUser | `OfflineResult` |
| `sceNpTusGetMultiSlotDataStatusVUserAsync` | `0xD62D2A0B` | idem | 0 / Poll → erro |
| `sceNpTusGetMultiUserDataStatus` | `0xF4D40B19` | `int (reqId, const SceNpId *targetArray, SceNpTusSlotId slotId, SceNpTusDataStatus *statusArray, SceSize statusArraySize, SceSize arrayNum, void *opt)` | `OfflineResult` |
| `sceNpTusGetMultiUserDataStatusAsync` | `0xC08A1E5E` | idem | 0 / Poll → erro |
| `sceNpTusGetMultiUserDataStatusVUser` | `0xAF7148A6` | idem, VUser | `OfflineResult` |
| `sceNpTusGetMultiUserDataStatusVUserAsync` | `0x7593D595` | idem | 0 / Poll → erro |
| `sceNpTusGetFriendsDataStatus` | `0x27FB4EFD` | `int (reqId, slotId, SceInt32 includeSelf, SceInt32 sortType, SceNpTusDataStatus *statusArray, SceSize statusArraySize, SceSize arrayNum, void *opt)` | `OfflineResult` |
| `sceNpTusGetFriendsDataStatusAsync` | `0x32064D64` | idem | 0 / Poll → erro |
| `sceNpTusDeleteMultiSlotData` | `0xF6D24DF7` | como o `DeleteMultiSlotDataAsync` | `OfflineResult` |
| `sceNpTusDeleteMultiSlotDataVUser` | `0x41FBED79` | idem, VUser | `OfflineResult` |
| `sceNpTusDeleteMultiSlotDataVUserAsync` | `0x9BA9148C` | idem | 0 / Poll → erro |
| `sceNpTssGetData` | `0xD8D5CABA` | `int (SceInt32 reqId, SceNpTssSlotId slotId, SceNpTssDataStatus *dataStatus, SceSize dataStatusSize, void *data, SceSize recvSize, const void *opt)` (Vita3K) | `dataStatus == 0` → `INSUFFICIENT_ARGUMENT`; `dataStatusSize != 16` ou slot fora de 0..15 → `INVALID_ARGUMENT`; zera os 16 bytes de `dataStatus`; `OfflineResult` |
| `sceNpTssGetDataAsync` | `0x1C8D73A8` | idem (Vita3K) | mesmas validações; 0 / Poll → erro |
| `sceNpTssGetDataNoLimit` | `0x987D1018` | **inferido:** como o `GetData`, sem limite de tamanho | `OfflineResult` |
| `sceNpTssGetDataNoLimitAsync` | `0x7A101394` | idem | 0 / Poll → erro |
| `sceNpTssGetSmallStorage` | `0x8CA469F4` | como o `GetSmallStorageAsync` (Vita3K) | `*contentLength = 0`; `OfflineResult` |
| `sceNpTssGetStorage` | `0xFC300E7E` | **inferido:** `int (reqId, ... , void *data, SceSize maxSize, SceSize *contentLength, ...)`; forma a confirmar | `OfflineResult` (só `reqId`) |
| `sceNpTssGetStorageAsync` | `0x8972DF8F` | idem | 0 / Poll → erro |

`SceNpTssDataStatus` (0x10, Vita3K): +0 `SceRtcTick lastModified`, +8 `statusCodeType`, +0xC `contentLength`.

---

## 6. SceNpBasic

O offline já implementado mantém listas vazias de amigos e bloqueados e devolve `SIGNED_OUT` para presença. As
funções abaixo seguem o mesmo padrão em `Np` (`Online.cs`). Protótipos **inferidos** do SDK PS Vita/PS4 análogo.

### sceNpBasicGetFriendOnlineStatus
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpBasic · **NID:** `0x5183A4B5`
- **Protótipo:** `int sceNpBasicGetFriendOnlineStatus(const SceNpId *friendId, SceNpBasicFriendOnlineStatus *status);` (inferido)
- **Comportamento:** status online de um amigo. A lista de amigos está vazia (`sceNpBasicGetFriendListEntryCount` = 0),
  então o jogo não tem NpId de amigo para consultar. Se consultar mesmo assim, a resposta é "sem dados".
- **Retorno/erros:** argumento nulo → `SCE_NP_BASIC_ERROR_INVALID_ARGUMENT` 0x80551D02; senão
  `SCE_NP_ERROR_SIGNED_OUT` 0x80550006, sem escrever `*status`. Erro de "não é amigo" do firmware: **a confirmar**.
- **Estruturas:** `SceNpBasicFriendOnlineStatus` = enum de 4 bytes (valores a confirmar; não são escritos).
- **Implementação no VitaSharp:** `Np` (`Online.cs`), uma linha.
- **Validação:** cenário Invizimals (§3); a tela de amigos/menu online deve mostrar lista vazia.

### sceNpBasicGetFriendContextState
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpBasic · **NID:** `0x9A9E2021`
- **Protótipo:** `int sceNpBasicGetFriendContextState(const SceNpId *friendId, SceNpBasicFriendContextState *state);` (inferido)
- **Comportamento:** diz se o amigo está no mesmo jogo/contexto. Offline não há amigos.
- **Retorno/erros:** igual a `GetFriendOnlineStatus`.
- **Estruturas:** enum de 4 bytes (não escrito).
- **Implementação no VitaSharp:** `Np` (`Online.cs`).
- **Validação:** idem.

### sceNpBasicGetGamePresenceOfFriend
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpBasic · **NID:** `0xEF8A91BC`
- **Protótipo:** `int sceNpBasicGetGamePresenceOfFriend(const SceNpId *friendId, SceNpBasicGamePresence *presence);` (inferido)
- **Comportamento:** lê a presença in-game (status, dados) de um amigo. Offline não há amigos.
- **Retorno/erros:** igual a `GetFriendOnlineStatus`.
- **Estruturas:** `SceNpBasicGamePresence`: layout a confirmar (não é escrito).
- **Implementação no VitaSharp:** `Np` (`Online.cs`).
- **Validação:** idem.

### Demais funções SceNpBasic (P1)

| Função | NID | Protótipo (inferido) | Contrato offline |
| --- | --- | --- | --- |
| `sceNpBasicCheckIfPlayerIsBlocked` | `0xF51545D8` | `int (const SceNpId *player, SceInt32 *playerIsBlocked)` | Lista de bloqueio vazia: `*playerIsBlocked = 0`, retorna 0; nulo → 0x80551D02 |
| `sceNpBasicGetFriendRequestEntryCount` | `0xFE98E45A` | `int (SceUInt32 *count)` | `count.TrySet(0)`; 0 (como `GetFriendListEntryCount`) |
| `sceNpBasicGetFriendRequestEntries` | `0x91BF63CB` | `int (SceUInt32 startIndex, SceNpBasicFriendRequestEntry *entries, SceUInt32 num, SceUInt32 *retrieved)` | `retrieved.TrySet(0)`; 0 |
| `sceNpBasicGetRequestedFriendRequestEntryCount` | `0xB9041420` | idem ao Count | 0 entradas |
| `sceNpBasicGetRequestedFriendRequestEntries` | `0x3700E00C` | idem ao Entries | 0 entradas |
| `sceNpBasicGetGameJoiningPresence` | `0x863249CB` | `int (const SceNpId *friendId, SceNpBasicGameJoiningPresence *presence)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpBasicGetPlaySessionLogSize` | `0xFB0F7FDF` | `int (SceNpBasicPlaySessionLogType type, SceUInt32 *size)` | `*size = 0`; 0. O `RecordPlaySessionLog` atual não grava nada, coerente |
| `sceNpBasicGetPlaySessionLog` | `0x364531A8` | `int (SceNpBasicPlaySessionLogType type, SceUInt32 index, SceNpBasicPlaySessionLog *log)` | Log vazio: índice sempre fora do intervalo → 0x80551D02 (o código de "índice inválido" do firmware está a confirmar) |
| `sceNpBasicUnsetInGamePresence` | `0xD20C2370` | `int (void)` | `SCE_NP_ERROR_SIGNED_OUT` (simétrico ao `SetInGamePresence` atual) |

---

## 7. SceNpMessage

Mensagens PSN com dados anexos ("in-game messages" lidas pelo app Mensagens). O jogo só chama `GetMessage`/
`GetAttachedData` depois de receber um app event de mensagem (`sceAppUtilAppEventParseNpAppDataMessage`, pacote D),
que o VitaSharp nunca gera. Offline, então: Init/Term funcionam e toda leitura falha. **Nenhum protótipo está no
vita-headers ou no Vita3K; todos são inferidos.** Como os P0 só usam o valor de retorno, ignorar os argumentos é
seguro.

### sceNpMessageInit
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpMessage · **NID:** `0x258DA4AC`
- **Protótipo:** `int sceNpMessageInit(void);` (**inferido**; pode receber prioridade/afinidade de thread, que são ignoradas)
- **Comportamento:** inicializa a biblioteca de mensagens.
- **Retorno/erros:** 0. Chamada dupla: o firmware provavelmente devolve um `ALREADY_INITIALIZED` da família da
  biblioteca (valor a confirmar). Aqui retornar 0 sempre e guardar `bool _msgInited`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Np` (`Online.cs`).
- **Validação:** cenário Invizimals; sem `Unimplemented` no boot.

### sceNpMessageTerm
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpMessage · **NID:** `0x380230A1`
- **Protótipo:** `int sceNpMessageTerm(void);` (inferido)
- **Comportamento:** encerra a biblioteca.
- **Retorno/erros:** 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Np`; `_msgInited = false`.
- **Validação:** idem.

### sceNpMessageGetMessage
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpMessage · **NID:** `0x514A4320`
- **Protótipo:** `int sceNpMessageGetMessage(SceNpMessageId msgId, ...);` (**inferido**; `SceNpMessageId` provavelmente
  64 bits, em r0:r1, e os buffers de saída nos registradores seguintes. Confirmar pelo §0.6 se algum dia for chamado)
- **Comportamento:** lê o cabeçalho e o corpo de uma mensagem recebida. Offline não existe mensagem.
- **Retorno/erros:** `SCE_NP_ERROR_SIGNED_OUT` (0x80550006), sem escrever nada. Código de "mensagem não encontrada":
  a confirmar.
- **Estruturas:** não escritas.
- **Implementação no VitaSharp:** `Np` (`Online.cs`), sem parâmetros declarados; logar r0–r3 com `Log.Warn` na
  primeira chamada, porque só pode acontecer se o jogo tiver recebido um app event inesperado.
- **Validação:** idem; esperar que nunca seja chamada.

### sceNpMessageGetAttachedData
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpMessage · **NID:** `0x7E697F98`
- **Protótipo:** `int sceNpMessageGetAttachedData(SceNpMessageAttachedDataId id, SceNpId *from, void *data, SceSize *size);` (**inferido**)
- **Comportamento:** copia os dados anexados (app data) de uma mensagem para o jogo. Offline não existe mensagem.
- **Retorno/erros:** `SCE_NP_ERROR_SIGNED_OUT`, sem escrever nada.
- **Estruturas:** não escritas.
- **Implementação no VitaSharp:** como `GetMessage`.
- **Validação:** idem.

### Demais funções SceNpMessage (P1)

| Função | NID | Protótipo | Contrato offline |
| --- | --- | --- | --- |
| `sceNpMessageInitWithParam` | `0x26AF5306` | inferido: `int (const SceNpMessageInitParam *param)` | Igual ao `Init`; 0 |
| `sceNpMessageAbort` | `0x707B26C2` | inferido: `int (void)` ou `(SceInt32 reqId)` | 0 (nada em andamento) |
| `sceNpMessageGetMessageEntryCount` | `0x1DBD26D2` | inferido; argumentos a confirmar | `SCE_NP_ERROR_SIGNED_OUT`. Com o protótipo confirmado, preferir escrever 0 e retornar 0 |
| `sceNpMessageGetMessageEntries` | `0xC86B6DA7` | inferido | `SCE_NP_ERROR_SIGNED_OUT` (idem) |
| `sceNpMessageGetMessageEntry` | `0x70116381` | inferido | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpMessageSetAttachedDataUsedFlag` | `0xF533A73A` | inferido: `int (SceNpMessageAttachedDataId id, ...)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpMessageSyncMessage` | `0x35BE21C5` | inferido | `SCE_NP_ERROR_SIGNED_OUT` (sincronizar exige servidor) |

---

## 8. SceNpActivity

Posts no feed de atividades da PSN. **Sem protótipos públicos; todos inferidos.** Só o retorno importa.

### sceNpActivityInit
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpActivity · **NID:** `0xE0FFEE97`
- **Protótipo:** `int sceNpActivityInit(void);` (inferido; argumentos ignorados)
- **Comportamento:** inicializa a biblioteca.
- **Retorno/erros:** 0; guardar `bool _activityInited`.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Np` (`Online.cs`).
- **Validação:** cenário Invizimals.

### sceNpActivityTerm
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpActivity · **NID:** `0x9EA4901F`
- **Protótipo:** `int sceNpActivityTerm(void);` (inferido)
- **Comportamento:** encerra.
- **Retorno/erros:** 0.
- **Estruturas:** —
- **Implementação no VitaSharp:** `Np`.
- **Validação:** idem.

### sceNpActivityPostStatus
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpActivity · **NID:** `0xBC7FDC77`
- **Protótipo:** `int sceNpActivityPostStatus(const SceNpActivityPostStatusParam *param);` (inferido)
- **Comportamento:** publica um evento ("capturou o Invizimal X") no feed. Sem conta, não há onde publicar.
- **Retorno/erros:** `SCE_NP_ERROR_SIGNED_OUT`, coerente com `sceNpBasicSetInGamePresence`. **Plano B:** se o jogo
  mostrar diálogo de erro a cada post, retornar 0 (descartar o post em silêncio) e logar uma vez.
- **Estruturas:** não lidas (logar a string de texto, se o layout for descoberto).
- **Implementação no VitaSharp:** `Np` (`Online.cs`).
- **Validação:** jogar Invizimals até um evento que gere post e confirmar que não aparece diálogo.

| Função | NID | Protótipo | Contrato offline |
| --- | --- | --- | --- |
| `sceNpActivityPostAppStartupStatus` | `0x6BE90416` | inferido: `int (void)` ou `(const param*)` | Igual ao `PostStatus` (P1) |

---

## 9. SceNpPartyGameUtil

### sceNpPartyGetState
- [x] **Prioridade:** P0 (Invizimals) · **Biblioteca:** SceNpPartyGameUtil · **NID:** `0x9F99ADF7`
- **Protótipo:** `int sceNpPartyGetState(SceNpPartyState *state);` (**inferido**)
- **Comportamento:** estado do usuário no app Party (fora de party / em party). Offline: nunca em party.
- **Retorno/erros:** `state == 0` → `SCE_NP_ERROR_INVALID_ARGUMENT` 0x80550003 (código da família party a confirmar);
  senão `*state = SCE_NP_PARTY_STATE_NOT_IN_PARTY` e retorna 0. **O valor numérico de NOT_IN_PARTY está a
  confirmar.** Para descobrir, siga o §0.6: o código do Invizimals logo após a chamada compara `[state]` com as
  constantes do enum, e a comparação que leva ao caminho "sem party" (não tenta `sceNpPartyGetMembers`) dá o valor.
  Até lá, escrever 0 e logar. Alternativa se o jogo não usa ponteiro: retornar `0x80553402`, o código "sem party" que
  `GetMembers` já usa.
- **Estruturas:** `SceNpPartyState`: enum de 4 bytes.
- **Implementação no VitaSharp:** `Np` (`Online.cs`), ao lado dos `sceNpParty*` existentes.
- **Validação:** cenário Invizimals. O jogo não pode chamar `sceNpPartyGetMembers` em loop nem tentar voice chat.

| Função | NID | Protótipo (inferido) | Contrato offline |
| --- | --- | --- | --- |
| `sceNpPartyGetId` | `0x27AFCE24` | `int (SceNpPartyId *id)` | `0x80553402` (sem party; mesmo código de `GetMembers`) |
| `sceNpPartyGetMemberSessionInfo` | `0xF01585A3` | `int (SceNpPartyRoomMemberId id, SceNpPartyMemberSessionInfo *info)` | `0x80553402` |
| `sceNpPartyGetGameSessionReadyState` | `0xE9173FE6` | `int (SceNpPartyGameSessionReadyState *state)` | `0x80553402` |

---

## 10. SceNpUtility (Lookup, WordFilter, BandwidthTest)

Lookup e WordFilter são bibliotecas community: usam o §0.4, com uma instância de `NpCommunity` para cada, no módulo
`NpUtility` (`NpCommunity.cs`). Os `sceNpLookup*` já registrados em `Np` precisam migrar para lá (STUB §1). Os não
listados aqui (`Init`, `Term`, `CreateTitleCtx`, `CreateRequest`, `Delete*`, `AbortRequest`, `PollAsync`,
`UserProfileAsync`) já existem e só mudam de classe.

| Função | NID | Protótipo resumido (inferido do PS3) | Retorno offline |
| --- | --- | --- | --- |
| `sceNpLookupSetTimeout` | `0xB0C9DC45` | `int (SceInt32 id, SceInt32 resolveRetry, SceUInt32 resolveTimeout, SceUInt32 connTimeout, SceUInt32 sendTimeout, SceUInt32 recvTimeout)` | `SetTimeout(id)` → 0 |
| `sceNpLookupWaitAsync` | `0xCF956F23` | `int (SceInt32 reqId, SceInt32 *result)` | `Poll` |
| `sceNpLookupNpId` | `0xB1A14879` | `int (SceInt32 reqId, const SceNpOnlineId *onlineId, SceNpId *npId, void *opt)` | `OfflineResult` |
| `sceNpLookupNpIdAsync` | `0x5387BABB` | idem | 0 / Poll → erro |
| `sceNpLookupUserProfile` | `0x6A1BF429` | como o `UserProfileAsync` (§1) | `OfflineResult` |
| `sceNpLookupAvatarImage` | `0xFDB0AE47` | `int (SceInt32 reqId, const SceNpAvatarUrl *url, SceNpAvatarImage *image, void *opt)` | `OfflineResult` |
| `sceNpLookupAvatarImageAsync` | `0x282BD43C` | idem | 0 / Poll → erro |
| `sceNpWordFilterInit` | `0x01284C6E` | `int (SceInt32 threadPriority, SceInt32 cpuAffinityMask, void *opt)` | `Init` |
| `sceNpWordFilterTerm` | `0x7D140F70` | `int (void)` | `Term` |
| `sceNpWordFilterCreateTitleCtx` | `0x1075589C` | `int (const SceNpCommunicationId*, const SceNpCommunicationPassphrase*, const SceNpId*)` | `CreateTitleCtx` |
| `sceNpWordFilterDeleteTitleCtx` | `0xB4E5A189` | `int (SceInt32 ctxId)` | `DeleteTitleCtx` |
| `sceNpWordFilterCreateRequest` | `0x7183047C` | `int (SceInt32 ctxId)` | `CreateRequest` |
| `sceNpWordFilterDeleteRequest` | `0x6690D8E2` | `int (SceInt32 reqId)` | `DeleteRequest` |
| `sceNpWordFilterAbortRequest` | `0x36DA662E` | `int (SceInt32 reqId)` | `Abort` |
| `sceNpWordFilterSetTimeout` | `0x9F44CE45` | como o Lookup | `SetTimeout` |
| `sceNpWordFilterPollAsync` | `0xBAE4CD1F` | `int (SceInt32 reqId, SceInt32 *result)` | `Poll` |
| `sceNpWordFilterWaitAsync` | `0x31B308AA` | idem | `Poll` |
| `sceNpWordFilterCensorComment` | `0xA30E4E17` | `int (SceInt32 reqId, const char *comment, SceInt32 *result?, void *opt)` (forma a confirmar) | `OfflineResult` |
| `sceNpWordFilterCensorCommentAsync` | `0xE956080F` | idem | 0 / Poll → erro |
| `sceNpWordFilterSanitizeComment` | `0x30606C85` | `int (SceInt32 reqId, const char *comment, char *sanitized, void *opt)` | `OfflineResult`. Alternativa, se um jogo travar ao exibir texto do usuário: copiar `comment` para `sanitized` sem alteração e retornar 0 |
| `sceNpWordFilterSanitizeCommentAsync` | `0x40EC7271` | idem | 0 / Poll → erro |

BandwidthTest mede a banda até o servidor da Sony. Offline, `InitStart` falha na hora e as demais funções são
inofensivas:

| Função | NID | Protótipo (inferido do PS3) | Contrato offline |
| --- | --- | --- | --- |
| `sceNpBandwidthTestInitStart` | `0x081FA13C` | `int (SceInt32 threadPriority, SceSize stackSize)` ou `(const param*)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpBandwidthTestGetStatus` | `0xE0EBFBF6` | `int (void)` → status | 0 (NONE; valor a confirmar) |
| `sceNpBandwidthTestShutdown` | `0x58D92EFD` | `int (SceNpBandwidthTestResult *result)` | 0, sem escrever (o teste nunca começou) |
| `sceNpBandwidthTestAbort` | `0x32B068C4` | `int (void)` | 0 |

---

## 11. SceNpSignaling

Sinalização P2P (NAT traversal entre NpIds), usada por jogos com voz/P2P fora do NpMatching2. Offline nenhum par
conecta, e o contrato espelha `sceNpMatching2Signaling*` de `Multiplayer.cs`. Nova classe `NpSignaling` em
`Multiplayer.cs` com `bool _inited`, `HashSet<int> _ctx` e `Dictionary<(int ctx,int opt),int> _opts`. Protótipos
**inferidos** do `libnp_signaling` do PS3 (mesma API). Os códigos de erro da família Signaling no Vita estão
**a confirmar**: por isso use `SCE_NP_ERROR_NOT_INITIALIZED` 0x80550002 / `SCE_NP_ERROR_INVALID_ARGUMENT` 0x80550003
para validação e `SCE_NP_ERROR_SIGNED_OUT` para operações de rede.

| Função | NID | Protótipo (inferido) | Contrato offline |
| --- | --- | --- | --- |
| `sceNpSignalingInit` | `0x4B6ACF47` | `int (SceSize poolSize, SceInt32 threadPriority, SceInt32 cpuAffinityMask, SceSize threadStackSize)` | `_inited = true`; 0 |
| `sceNpSignalingTerm` | `0xBC892D18` | `int (void)` | limpa; 0 |
| `sceNpSignalingCreateCtx` | `0xF77EF683` | `int (const SceNpId *npId, SceNpSignalingHandler handler, void *arg, SceInt32 *ctxId)` | `!_inited` → 0x80550002; `ctxId == 0` → 0x80550003; novo ID em `*ctxId`; 0. O handler nunca é chamado |
| `sceNpSignalingDestroyCtx` | `0xEAA4B1F3` | `int (SceInt32 ctxId)` | remove; ID desconhecido → 0x80550003 |
| `sceNpSignalingSetCtxOpt` | `0x0B48FADB` | `int (SceInt32 ctxId, SceInt32 optname, SceInt32 optval)` | guarda; 0 |
| `sceNpSignalingGetCtxOpt` | `0x9D1C5303` | `int (SceInt32 ctxId, SceInt32 optname, SceInt32 *optval)` | valor guardado ou 0; 0 |
| `sceNpSignalingActivateConnection` | `0x92FFBDE3` | `int (SceInt32 ctxId, const SceNpId *npId, SceInt32 *connId)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpSignalingDeactivateConnection` | `0x3B56E6C2` | `int (SceInt32 ctxId, SceInt32 connId)` | 0 (nada ativo) |
| `sceNpSignalingTerminateConnection` | `0xA413F8C2` | `int (SceInt32 ctxId, SceInt32 connId)` | 0 |
| `sceNpSignalingGetConnectionStatus` | `0x83B3DA0A` | `int (SceInt32 ctxId, SceInt32 connId, SceInt32 *connStatus, SceNetInAddr *peerAddr, SceNetInPort *peerPort)` | `*connStatus = 0` (INACTIVE, como em `Multiplayer.cs`); retorna `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpSignalingGetConnectionInfo` | `0x51883EAE` | `int (SceInt32 ctxId, SceInt32 connId, SceInt32 code, SceNpSignalingConnectionInfo *info)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpSignalingGetConnectionFromNpId` | `0x3EC1F4AA` | `int (SceInt32 ctxId, const SceNpId *npId, SceInt32 *connId)` | `SCE_NP_ERROR_SIGNED_OUT` (conexão inexistente) |
| `sceNpSignalingGetConnectionFromPeerAddress` | `0x6B9E6E3F` | `int (SceInt32 ctxId, SceNetInAddr addr, SceNetInPort port, SceInt32 *connId)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpSignalingGetLocalNetInfo` | `0x5861E7BA` | `int (SceInt32 ctxId, SceNpSignalingNetInfo *info)` | `SCE_NP_ERROR_SIGNED_OUT` (igual a `sceNpMatching2SignalingGetLocalNetInfo`) |
| `sceNpSignalingGetPeerNetInfo` | `0x5D660C45` | `int (SceInt32 ctxId, const SceNpId *npId, SceInt32 *reqId)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpSignalingCancelPeerNetInfo` | `0x5048E147` | `int (SceInt32 ctxId, SceInt32 reqId)` | 0 |
| `sceNpSignalingGetPeerNetInfoResult` | `0xDE8536F0` | `int (SceInt32 ctxId, SceInt32 reqId, SceNpSignalingNetInfo *info)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpSignalingGetMemoryInfo` | `0x995A5A74` | `int (SceNpSignalingMemoryInfo *info)` | zera 12 bytes (como `sceNpMatching2GetMemoryInfo`); 0 |

---

## 12. SceNpWebApi

Cliente REST da PSN (amigos, sessões, atividades, convites). Offline, a criação local funciona e o envio falha.
Nova classe `NpWebApi` em `NpServices.cs` com tabelas de libCtx, handles, requests (`long` ID), filtros e callbacks.
Protótipos **inferidos** do SDK do PS4 adaptado ao Vita (sem `userCtxId`, já que o Vita tem um usuário só); a forma
exata está a confirmar e, salvo os IDs, os argumentos não são usados. Erros: os `SCE_NP_WEBAPI_ERROR_*` do Vita são
desconhecidos (no PS4 a família é 0x805529xx; os valores ficam a confirmar e não devem ser usados). Use
`SCE_NP_ERROR_INVALID_ARGUMENT`/`NOT_INITIALIZED` para validação e `SCE_NP_ERROR_SIGNED_OUT` no envio.

| Função | NID | Protótipo (inferido) | Contrato offline |
| --- | --- | --- | --- |
| `sceNpWebApiInitialize` | `0x8519EB34` | `int (SceInt32 libHttpCtxId, SceSize poolSize)` → libCtxId | ID > 0 |
| `sceNpWebApiTerminate` | `0x001254C2` | `int (SceInt32 libCtxId)` | limpa; 0 |
| `sceNpWebApiSetNpTitleId` | `0x9AE6BC46` | `int (const SceNpTitleId *titleId, const SceNpTitleSecret *secret)` | guarda os 16 bytes de `titleId` (layout a confirmar: `char id[12+1]` + pad); 0 |
| `sceNpWebApiGetNpTitleId` | `0xB65DF857` | `int (SceNpTitleId *titleId)` | copia o valor guardado (zeros se nunca setado); 0 |
| `sceNpWebApiCreateHandle` | `0x40341759` | `int (SceInt32 libCtxId)` → handleId | ID > 0 |
| `sceNpWebApiDeleteHandle` | `0xB575A09C` | `int (SceInt32 libCtxId, SceInt32 handleId)` | 0 |
| `sceNpWebApiAbortHandle` | `0xD462D519` | `int (SceInt32 libCtxId, SceInt32 handleId)` | 0 |
| `sceNpWebApiCreateRequest` | `0x6FC7357E` | `int (SceInt32 libCtxId, const char *apiGroup, const char *path, SceNpWebApiHttpMethod method, const SceNpWebApiContentParameter *content, SceInt64 *requestId)` | grava ID > 0 em `*requestId` (64 bits); 0. Logar `apiGroup` + `path` |
| `sceNpWebApiCreateMultipartRequest` | `0x55435037` | `int (SceInt32 libCtxId, const char *apiGroup, const char *path, SceNpWebApiHttpMethod method, SceInt64 *requestId)` | idem |
| `sceNpWebApiDeleteRequest` | `0x57B2826E` | `int (SceInt64 requestId)` (r0:r1) | 0 |
| `sceNpWebApiAbortRequest` | `0xEB3D14CE` | `int (SceInt64 requestId)` | 0 |
| `sceNpWebApiAddHttpRequestHeader` | `0x8DCD6D19` | `int (SceInt64 requestId, const char *name, const char *value)` | 0 |
| `sceNpWebApiAddMultipartPart` | `0xC4B24435` | `int (SceInt64 requestId, const SceNpWebApiMultipartPartParameter *param, SceInt32 *index)` | `*index` = contador; 0 |
| `sceNpWebApiSetMultipartContentType` | `0x8E870BE4` | `int (SceInt64 requestId, const char *type, const char *boundary)` | 0 |
| `sceNpWebApiSendRequest` | `0x51EA899B` | `int (SceInt64 requestId, const void *data, SceSize size)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpWebApiSendRequest2` | `0x1E37862A` | `int (SceInt64 requestId, const void *data, SceSize size, SceNpWebApiResponseInformationOption *respInfo)` | `SCE_NP_ERROR_SIGNED_OUT`; se `respInfo` não for nulo, zerar `httpStatus` (layout a confirmar; não escrever enquanto não confirmado) |
| `sceNpWebApiSendMultipartRequest` | `0xBDEC2650` | `int (SceInt64 requestId, SceInt32 partIndex, const void *data, SceSize size)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpWebApiSendMultipartRequest2` | `0xC8E042B8` | idem + `respInfo` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpWebApiReadData` | `0xBA75BF6E` | `int (SceInt64 requestId, void *data, SceSize size)` | `SCE_NP_ERROR_SIGNED_OUT` (não houve resposta) |
| `sceNpWebApiGetHttpStatusCode` | `0x67A3BC79` | `int (SceInt64 requestId, SceInt32 *status)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpWebApiGetHttpResponseHeaderValueLength` | `0xD585FDEA` | `int (SceInt64 requestId, const char *name, SceSize *len)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpWebApiGetHttpResponseHeaderValue` | `0xB0500FF4` | `int (SceInt64 requestId, const char *name, char *value, SceSize len)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpWebApiGetErrorCode` | `0x00234C88` | `int (SceInt64 requestId, SceNpWebApiErrorCode *err)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpWebApiGetMemoryPoolStats` | `0x9DF7EE1E` | `int (SceInt32 libCtxId, SceNpWebApiMemoryPoolStats *stats)` | zera 16 bytes (como `sceHttpGetMemoryPoolStats`); 0 |
| `sceNpWebApiCheckCallback` | `0xEDA9C359` | `int (SceInt32 libCtxId)` | 0 (nenhum evento de push) |
| `sceNpWebApiCreatePushEventFilter` | `0x18CB54B5` | `int (SceInt32 libCtxId, const SceNpWebApiPushEventFilterParameter *p, SceSize n)` → filterId | ID > 0 |
| `sceNpWebApiDeletePushEventFilter` | `0x6099B918` | `int (SceInt32 libCtxId, SceInt32 filterId)` | 0 |
| `sceNpWebApiCreateServicePushEventFilter` | `0x2B61B0C7` | idem, com `handleId`, `serviceName`, `serviceLabel` | ID > 0 |
| `sceNpWebApiDeleteServicePushEventFilter` | `0x828F657B` | `int (SceInt32 libCtxId, SceInt32 filterId)` | 0 |
| `sceNpWebApiCreateExtdPushEventFilter` | `0x6AB53577` | idem (extended) | ID > 0 |
| `sceNpWebApiDeleteExtdPushEventFilter` | `0x4980D88D` | `int (SceInt32 libCtxId, SceInt32 filterId)` | 0 |
| `sceNpWebApiRegisterPushEventCallback` | `0xDB04ECD6` | `int (SceInt32 libCtxId, SceInt32 filterId, cb, void *arg)` → cbId | ID > 0 (nunca chamado) |
| `sceNpWebApiUnregisterPushEventCallback` | `0xC9056736` | `int (SceInt32 libCtxId, SceInt32 cbId)` | 0 |
| `sceNpWebApiRegisterServicePushEventCallback` | `0xF6B6C994` | idem | ID > 0 |
| `sceNpWebApiUnregisterServicePushEventCallback` | `0x06D28ADA` | idem | 0 |
| `sceNpWebApiRegisterExtdPushEventCallback` | `0xE75796DD` | idem | ID > 0 |
| `sceNpWebApiUnregisterExtdPushEventCallback` | `0x2D272ACA` | idem | 0 |
| `sceNpWebApiUtilityParseNpId` | `0x98CBE11F` | `int (const char *jsonNpId, SceNpId *npId)` | Pura, mas só faz sentido com JSON vindo do servidor. O campo `npId` da WebAPI é Base64 de `"<onlineId>@<sufixo>"`: decodificar, copiar a parte antes de `@` (≤ 16 bytes) para `onlineId.data`, zerar o resto e pôr `reserved[0] = 1`. O mapeamento do sufixo para `opt[8]` está a confirmar. Nulo → 0x80550003 |

---

## 13. SceNpWebApiCommonDialog

Diálogos de sistema baseados na WebAPI: convite para sessão (`sceInvitationDialog*`) e envio de dados customizados do
jogo (`sceGameCustomDataDialog*`). Sem conta, o diálogo não abre conteúdo útil. O contrato segue o padrão de diálogo
"termina na hora" de `Net.sceNetCheckDialog*` (`Online.cs`), com resultado `SCE_COMMON_DIALOG_RESULT_ABORTED` (2,
`psp2/common_dialog.h`). O layout dos structs de resultado está a confirmar: escrever **apenas o primeiro campo**
`SceInt32 result` (+0) e zerar até `sizeof` só depois de confirmar. Nova classe em `NpServices.cs` com `int _status`
por diálogo (`NONE`=0, `RUNNING`=1, `FINISHED`=2).

| Função | NID | Protótipo (inferido) | Contrato offline |
| --- | --- | --- | --- |
| `sceInvitationDialogInit` | `0x8723A5F2` | `int (const SceInvitationDialogParam *param)` | `param == 0` → 0x80020401 (`SCE_COMMON_DIALOG_ERROR_PARAM`, já usado em `CommonDialog`); diálogo já aberto → 0x80020404 (BUSY); `_status = FINISHED`; 0 |
| `sceInvitationDialogGetStatus` | `0x5B735F60` | `SceCommonDialogStatus (void)` | `_status` |
| `sceInvitationDialogGetResult` | `0xEA14FB1E` | `int (SceInvitationDialogResult *result)` | nulo → 0x80020401; `W32(result, 2)` (ABORTED); 0 |
| `sceInvitationDialogAbort` | `0x853E5D3B` | `int (void)` | `_status = FINISHED`; 0 |
| `sceInvitationDialogTerm` | `0x4136382C` | `int (void)` | `_status = NONE`; 0 |
| `sceGameCustomDataDialogInit` | `0x23C9840E` | `int (const SceGameCustomDataDialogParam *param)` | idem ao Invitation |
| `sceGameCustomDataDialogGetStatus` | `0x114F831A` | `SceCommonDialogStatus (void)` | idem |
| `sceGameCustomDataDialogGetResult` | `0xC2B39806` | `int (SceGameCustomDataDialogResult *result)` | idem |
| `sceGameCustomDataDialogAbort` | `0xF5A1FA42` | `int (void)` | idem |
| `sceGameCustomDataDialogTerm` | `0xE183B9F3` | `int (void)` | idem |

---

## 14. SceNpCommerce2

Loja PSN in-game (listar produtos/DLC, abrir checkout). Sem conta, nenhuma sessão é criada. Protótipos **inferidos**
do `libnpcommerce2` do PS3 (mesma API). No Vita, o fluxo é `Init → CreateCtx → Create*Req → *Start → *GetResult →
Init*Result → Get*Info → Destroy*Result → DestroyReq`. Offline, o primeiro `*Start` falha na hora com
`SCE_NP_ERROR_SIGNED_OUT`, e por isso nenhum buffer de resultado chega a existir. As funções de *parse* (`Init*Result`,
`Get*Info`, `GetPrice`) são puras sobre um buffer vindo do servidor; offline não são alcançáveis e devem só validar
ponteiros. Erros da família Commerce2 no Vita: **a confirmar**. Use `SCE_NP_ERROR_NOT_INITIALIZED`/
`INVALID_ARGUMENT`/`SIGNED_OUT`. Classe `NpCommerce2` em `NpServices.cs` com `_inited`, `HashSet<uint> _ctx` e
`Dictionary<uint, uint ctx> _reqs`. `sceNpCommerce2GetDrmContentInfoCreateReq` exige `[Hle(Nid = 0x9DF3CBCC)]`.

| Função | NID | Protótipo (inferido) | Contrato offline |
| --- | --- | --- | --- |
| `sceNpCommerce2Init` | `0xC73F209A` | `int (void)` | `_inited = true`; 0 |
| `sceNpCommerce2Term` | `0xB99958AE` | `int (void)` | limpa; 0 |
| `sceNpCommerce2CreateCtx` | `0x123E55F4` | `int (SceUInt32 version, const SceNpId *npId, SceNpCommerce2Handler handler, void *arg, SceUInt32 *ctxId)` | `!_inited` → 0x80550002; `ctxId == 0` → 0x80550003; novo ID; 0. Handler nunca chamado |
| `sceNpCommerce2DestroyCtx` | `0x6CD27BD0` | `int (SceUInt32 ctxId)` | remove o ctx e as reqs dele; 0 |
| `sceNpCommerce2CreateSessionCreateReq` | `0xFDB39774` | `int (SceUInt32 ctxId, SceUInt32 *reqId)` | ctx válido → novo ID em `*reqId`; 0 |
| `sceNpCommerce2CreateSessionStart` | `0xBBDDF866` | `int (SceUInt32 reqId)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpCommerce2CreateSessionGetResult` | `0xAEE8D3DF` | `int (SceUInt32 reqId, SceNpCommerce2SessionInfo *info)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpCommerce2GetSessionInfo` | `0xF6F23623` | `int (SceUInt32 ctxId, SceNpCommerce2SessionInfo *info)` | `SCE_NP_ERROR_SIGNED_OUT` (sem sessão) |
| `sceNpCommerce2AbortReq` | `0xE7486A60` | `int (SceUInt32 reqId)` | 0 |
| `sceNpCommerce2DestroyReq` | `0xA4464754` | `int (SceUInt32 reqId)` | remove; 0 |
| `sceNpCommerce2GetCategoryContentsCreateReq` | `0x5CF665E0` | `int (SceUInt32 ctxId, SceUInt32 *reqId)` | novo ID; 0 |
| `sceNpCommerce2GetCategoryContentsStart` | `0x774F88D0` | `int (SceUInt32 reqId, const char *categoryId, SceUInt32 startPosition, SceUInt32 maxCountOfResults)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpCommerce2GetCategoryContentsGetResult` | `0xC47CDB59` | `int (SceUInt32 reqId, void *buf, SceSize bufSize, SceSize *fillSize)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpCommerce2InitGetCategoryContentsResult` | `0x5135EA31` | `int (SceNpCommerce2GetCategoryContentsResult *result, const void *data, SceSize dataSize)` | inalcançável; nulo/`dataSize == 0` → 0x80550003, senão 0x80550003 também (sem parser de dados reais) |
| `sceNpCommerce2DestroyGetCategoryContentsResult` | `0x9CD463F8` | `int (SceNpCommerce2GetCategoryContentsResult *result)` | 0 |
| `sceNpCommerce2GetCategoryInfo` | `0xB88DB7C2` | `int (const SceNpCommerce2GetCategoryContentsResult *result, SceNpCommerce2CategoryInfo *info)` | 0x80550003 |
| `sceNpCommerce2GetContentInfo` | `0xDE45F84F` | `int (const ...Result *result, SceUInt32 index, SceNpCommerce2ContentInfo *info)` | 0x80550003 |
| `sceNpCommerce2GetCategoryInfoFromContentInfo` | `0x56B80FAF` | `int (const SceNpCommerce2ContentInfo*, SceNpCommerce2CategoryInfo*)` | 0x80550003 |
| `sceNpCommerce2GetGameProductInfoFromContentInfo` | `0x021DC797` | `int (const SceNpCommerce2ContentInfo*, SceNpCommerce2GameProductInfo*)` | 0x80550003 |
| `sceNpCommerce2GetProductInfoCreateReq` | `0xB0AFF6C9` | `int (SceUInt32 ctxId, SceUInt32 *reqId)` | novo ID; 0 |
| `sceNpCommerce2GetProductInfoStart` | `0xC594ADC2` | `int (SceUInt32 reqId, const char *categoryId, const char *productId)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpCommerce2GetProductInfoGetResult` | `0x363922BA` | `int (SceUInt32 reqId, void *buf, SceSize bufSize, SceSize *fillSize)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpCommerce2InitGetProductInfoResult` | `0x118E1C5E` | `int (SceNpCommerce2GetProductInfoResult *result, const void *data, SceSize size)` | 0x80550003 |
| `sceNpCommerce2DestroyGetProductInfoResult` | `0xADF3BD5B` | `int (SceNpCommerce2GetProductInfoResult *result)` | 0 |
| `sceNpCommerce2GetGameProductInfo` | `0xA308B496` | `int (const ...ProductInfoResult *result, SceNpCommerce2GameProductInfo *info)` | 0x80550003 |
| `sceNpCommerce2GetProductInfoListCreateReq` | `0xE81B8BAD` | `int (SceUInt32 ctxId, SceUInt32 *reqId)` | novo ID; 0 |
| `sceNpCommerce2GetProductInfoListStart` | `0x18BDC4BD` | `int (SceUInt32 reqId, const char **productIds, SceUInt32 productNum)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpCommerce2GetProductInfoListGetResult` | `0x4DFBCDD6` | `int (SceUInt32 reqId, void *buf, SceSize bufSize, SceSize *fillSize)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpCommerce2InitGetProductInfoListResult` | `0x29186E27` | `int (SceNpCommerce2GetProductInfoListResult *result, const void *data, SceSize size)` | 0x80550003 |
| `sceNpCommerce2DestroyGetProductInfoListResult` | `0x24D3FE34` | `int (SceNpCommerce2GetProductInfoListResult *result)` | 0 |
| `sceNpCommerce2GetGameProductInfoFromGetProductInfoListResult` | `0x0550C016` | `int (const ...ListResult *result, SceUInt32 index, SceNpCommerce2GameProductInfo *info)` | 0x80550003 |
| `sceNpCommerce2GetGameSkuInfoFromGameProductInfo` | `0x3B7C81A5` | `int (const SceNpCommerce2GameProductInfo*, SceUInt32 index, SceNpCommerce2GameSkuInfo*)` | 0x80550003 |
| `sceNpCommerce2GetContentRatingInfoFromGameProductInfo` | `0x7894DCDF` | `int (const SceNpCommerce2GameProductInfo*, SceNpCommerce2ContentRatingInfo*)` | 0x80550003 |
| `sceNpCommerce2GetContentRatingInfoFromCategoryInfo` | `0xD6A5B200` | `int (const SceNpCommerce2CategoryInfo*, SceNpCommerce2ContentRatingInfo*)` | 0x80550003 |
| `sceNpCommerce2GetContentRatingDescriptor` | `0x315B9BD0` | `int (const SceNpCommerce2ContentRatingInfo*, SceUInt32 index, SceNpCommerce2ContentRatingDescriptor*)` | 0x80550003 |
| `sceNpCommerce2GetDrmContentInfoCreateReq` | `0x9DF3CBCC` | **inferido:** `int (SceUInt32 ctxId, SceUInt32 *reqId)`, que cria request de informação DRM. Não há `Start` correspondente na lista, então provavelmente reaproveita `GetProductInfoStart` | novo ID; 0 (falha no `Start`) |
| `sceNpCommerce2GetPrice` | `0x88D236DF` | `int (SceUInt32 ctxId, char *buf, SceSize bufLen, SceUInt32 price)` | Formata o preço com a moeda da sessão, que offline não existe: `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpCommerce2GetShortfallOfLibhttpPool` | `0x586B0599` | **inferido:** `int (SceSize *shortfall)` ou retorno direto | Retornar 0 (nenhuma falta); se for ponteiro, `TrySet(0)` só após confirmar a forma |
| `sceNpCommerce2GetShortfallOfLibsslPool` | `0xBA5A5784` | idem | idem |
| `sceNpCommerce2ShowPsStoreIcon` | `0x84E56A6D` | `int (SceInt32 pos)` | Ícone da PS Store no canto da tela (overlay do sistema). No-op; 0 |
| `sceNpCommerce2HidePsStoreIcon` | `0xFFC8B18B` | `int (void)` | 0 |
| `sceNpCommerce2StartEmptyStoreCheck` | `0x7132EAA5` | **inferido**: verifica se a loja do título está vazia | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpCommerce2StopEmptyStoreCheck` | `0x84C336C6` | `int (void)` | 0 |

---

## 15. SceNpDrmPackage

Protótipos no vita-headers (`psp2/npdrmpackage.h`). Segundo o header, no FW 3.60 todas as funções da biblioteca,
exceto `sceNpDrmPackageIsGameExist`, são restritas a programas de sistema (instalador de pacotes).

| Função | NID | Protótipo | Contrato |
| --- | --- | --- | --- |
| `sceNpDrmPackageIsGameExist` | `0xB9337914` | `SceBool sceNpDrmPackageIsGameExist(void);` | **P1.** "Diferente de zero quando existe conteúdo de jogo do pacote". O título em execução foi instalado por PKG, então retornar 1. Semântica exata a confirmar (pode se referir a outro título ou ao `gd`) |
| `sceNpDrmPackageInstallOngoing` | `0xED0471FE` | `int (int identifier, SceUInt32 progress);` | **FORA**: restrita ao instalador do sistema. Se algum jogo importar, retornar 0 (relatório de progresso sem efeito) |
| `sceNpDrmPackageUninstallOngoing` | `0xF1FF6193` | `int (int identifier, SceUInt32 progress);` | **FORA**, idem |
| `sceNpDrmSaveDataFormatOngoing` | `0x200D2DE4` | `int (int identifier, SceUInt32 progress);` | **FORA**, idem |
| `sceNpDrmSaveDataInstallOngoing` | `0xA5E0F38C` | `int (int identifier, SceUInt32 progress);` | **FORA**, idem |

Implementação: classe pequena em `NpServices.cs` (ou junto do NpDrm existente, se houver; o pacote A cobre
`_sceNpDrm*`).

---

## 16. SceNpSnsFacebook (FORA)

A integração com Facebook da PSN foi desativada pela Sony e nenhum dos 7 jogos alvo importa a biblioteca. Se algum
jogo importar, implementar como no-op com erro: `Init`/`Term` → 0, `CreateRequest` → ID > 0, `Delete`/`Abort` → 0,
`GetAccessToken`/`GetLongAccessToken` → `SCE_NP_ERROR_SIGNED_OUT`. Protótipos não documentados; são inferidos e só os
IDs importam.

| Função | NID | Protótipo (inferido) | Contrato |
| --- | --- | --- | --- |
| `sceNpSnsFacebookInit` | `0x80557AA0` | `int (const SceNpSnsFacebookInitParam *param)` | 0 |
| `sceNpSnsFacebookTerm` | `0xA71E6847` | `int (void)` | 0 |
| `sceNpSnsFacebookCreateRequest` | `0x9CF1BC76` | `int (void)` → reqId | ID > 0 |
| `sceNpSnsFacebookDeleteRequest` | `0xA537FF90` | `int (SceInt32 reqId)` | 0 |
| `sceNpSnsFacebookAbortRequest` | `0x32FB2721` | `int (SceInt32 reqId)` | 0 |
| `sceNpSnsFacebookGetAccessToken` | `0x9A87EEE9` | `int (SceInt32 reqId, const SceNpSnsFacebookAccessTokenParam *param, SceNpSnsFacebookAccessTokenResult *result)` | `SCE_NP_ERROR_SIGNED_OUT` |
| `sceNpSnsFacebookGetLongAccessToken` | `0xA3C9041D` | idem | `SCE_NP_ERROR_SIGNED_OUT` |
