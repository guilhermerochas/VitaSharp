# E1 — Rede: SceHttp, SceNet, SceNetCtl, SceSsl, SceLibRudp, SceNetAdhocMatching

Especificação HLE das bibliotecas de rede "não-PSN". As bibliotecas `SceNp*` estão em [E2-np.md](E2-np.md).
Template, prioridades e convenções: [README.md](README.md).

Este documento descreve o perfil offline (padrão). A internet real opcional (`--net=real`: sockets, epoll, resolver,
NetCtl e HTTP pelo host, com os servidores da Sony bloqueados) está em [E2-np.md §0.000](E2-np.md).

## Escopo e contagem

Fonte da lista: `pkg/E.tsv` filtrado pelas seis bibliotecas acima (106 funções faltantes) + revisão dos stubs triviais
(IL ≤ 3) das classes `Net` (`Online.cs`) e `Rudp` (`Multiplayer.cs`) em `data/vitasharp-implemented.tsv`.

| Prioridade | Qtde | Conteúdo |
| --- | --- | --- |
| P0 | 12 | `sceHttpUriEscape` (Sakatsuku), `sceNetCtlAdhocGetResult` (Invizimals), 10 funções de `SceNetAdhocMatching` (Sakatsuku) |
| STUB | 38 | 6 Rudp (poll + MSS), 10 SceNet (init, sockets, epoll), 1 NetCtl, 19 HTTP (estado, setters, parsers), 2 SSL |
| P1 | 94 | SceHttp 61, SceNet 21, SceNetAdhocMatching 4, SceNetCtl 3, SceLibRudp 4, SceSsl 1 |
| **Total** | **144** | |

Por biblioteca (P0+P1 da lista): SceHttp 62, SceNet 21, SceNetAdhocMatching 14, SceNetCtl 4, SceLibRudp 4, SceSsl 1.

Uso pelos jogos instalados (`imports/<TITLEID>.txt`): SceNet/SceNetCtl/SceHttp/SceSsl em todos; **SceLibRudp em Killzone
(PCSC00045, 21 imports) e Sakatsuku (PCSG00198, 14 imports)**; SceNetAdhocMatching só em Sakatsuku.

## Ordem sugerida de implementação

1. **Funções puras de URI/HTTP** (testáveis por unidade, sem estado): `sceHttpUriEscape` (P0), `sceHttpUriUnescape`,
   `sceHttpUriBuild`, `sceHttpUriMerge`, `sceHttpUriSweepPath`, `sceHttpParseStatusLine` e os STUBs `sceHttpUriParse`,
   `sceHttpParseResponseHeader`.
2. **`sceNetCtlAdhocGetResult`** (P0, trivial) junto com o flag de init do NetCtl e a correção dos códigos de erro.
3. **SceNetAdhocMatching completa** no modelo offline (P0 + as 4 P1 da mesma biblioteca).
4. **Poll do Rudp** (`sceRudpPollWait` em busy loop afeta Killzone e Sakatsuku) + 4 P1 do Rudp.
5. **SceNet**: tabela de sockets/epoll, errno, STUBs de bloqueio (`sceNetRecv*`, `sceNetEpollWait`), depois as P1.
6. **Modelo de objetos HTTP** (template/conexão/requisição) + STUBs de setters + getters/setters P1.
7. Restante: cookies, epoll do HTTP, HTTPS/SSL, Dump/Emulation do SceNet.

---

## Decisão de design: rede "offline"

O VitaSharp emula um console **conectado a uma LAN, sem rota para a internet e deslogado da PSN** (comentários em
`Online.cs:6-8` e `Online.cs:112-113`; mesmo perfil do Vita3K):

- `sceNetCtlInetGetState` = 3 (CONNECTED), `sceNetCtlInetGetInfo` devolve IP 192.168.0.10; o callback de NetCtl recebe
  `DISCONNECTED` uma vez (`sceNetCtlCheckCallback`).
- Ad hoc: estado `DISCONNECTED` (0), nenhum par visível.
- Resolução DNS falha com `SCE_NET_ERROR_RESOLVER_ENODNS` (0x804101E1); `sceHttpSendRequest` falha com
  `SCE_HTTP_ERROR_NETWORK` (0x80431063).

Regras para toda função deste pacote no modo offline:

1. **Inicialização/criação sempre funciona** (Init, Create, Bind, Start…) e valida argumentos como o hardware.
2. **Operações que precisam de servidor ou par falham com o erro que o jogo trata como "rede indisponível"/"nenhum
   par"** — nunca com um código inventado, nunca com sucesso falso que leve o jogo a esperar dados que não chegam.
3. **Esperas respeitam o timeout e são canceláveis** (Abort/Cancel/Destroy/Term acordam quem espera). Nada de busy loop
   (retornar 0 na hora quando o jogo pediu para bloquear) nem de bloqueio sem saída.
4. **Setters guardam o valor** para que o getter correspondente seja coerente e para que a implementação de rede real
   reaproveite o estado.
5. Erros do SceNet também escrevem o errno (ver "errno" abaixo).

### Rede real (resumo, para quando fizer sentido)

- **Sockets**: `System.Net.Sockets.Socket` por socket guest; `SceNetSockaddrIn` (16 bytes, porta em big-endian,
  `sin_vport` em +8) ↔ `IPEndPoint`. Sockets P2P (`SCE_NET_SOCK_DGRAM_P2P` = 6, `STREAM_P2P` = 10) usam `vport`:
  encapsular em UDP com cabeçalho de vport (abordagem do Vita3K). `SO_NBIO` (0x1100) → `Socket.Blocking`.
- **Epoll**: `Socket.Select`/`Poll` numa thread de host, acordando o objeto de espera do guest.
- **HTTP/HTTPS**: `HttpClient` (ou socket + `SslStream`) dirigido pelo modelo de objetos descrito abaixo; os setters de
  timeout/redirect/gzip mapeiam para `HttpClientHandler`.
- **Ad hoc / Matching / Rudp**: UDP broadcast na LAN do host (ou um servidor de relay), com o protocolo do
  SceNetAdhocMatching (hello/request/accept/data) e do Rudp reimplementados em C#. Exige a mesma versão do protocolo
  entre duas instâncias do VitaSharp; interoperar com um Vita real exigiria o formato de pacote exato (a confirmar por
  captura de tráfego entre dois consoles).

### Problemas transversais encontrados no código atual (corrigir junto)

| Onde | Problema | Correção |
| --- | --- | --- |
| `Online.cs:12` | `ERR_NET_NOT_CONNECTED = 0x80410179` não existe em `psp2common/net.h` | `sceNetConnect`: `SCE_NET_ERROR_ENETUNREACH` 0x80410133 (ou `EHOSTUNREACH` 0x80410141); `sceNetSend`/`Accept`: `ENOTCONN` 0x80410139 |
| `Online.cs:114-120,145,152` | ponteiro nulo devolve 0x80412102 (`NOT_TERMINATED`) | `SCE_NET_CTL_ERROR_INVALID_ADDR` 0x80412107 |
| `Online.cs:155` | limite de callbacks devolve 0x80412104 (`ID_NOT_FOUND`) | `SCE_NET_CTL_ERROR_CALLBACK_MAX` 0x80412103 |
| `Online.cs:210-213` | `0x80431005` não existe em `http.h` | antes de enviar: `SCE_HTTP_ERROR_BEFORE_SEND` 0x80431065; após falha de envio: `SCE_HTTP_ERROR_NETWORK` 0x80431063 |
| `Online.cs:19` | `sceNetErrnoLoc` aloca o slot mas nada escreve nele | ver "errno" |
| `Multiplayer.cs:98-100` | códigos de erro do Rudp divergem dos do PS3 (ver seção Rudp) | a confirmar |
| `Multiplayer.cs:164` | `sceRudpNetFlush` não existe no Vita (não há NID em `nids.txt` nem no `nids.inc` do Vita3K; o log mostra "no NID known for Rudp.sceRudpNetFlush") | remover o método |
| `Kernel/Sync.cs` | `Wait.Until` só aceita timeout por `Ptr<uint>` (ponteiro guest) | adicionar sobrecarga com `long? timeoutUs` por valor (Rudp, epoll e HTTP passam timeout por valor) |
| `HleFramework.cs:115` | `ClassifyCost` não conhece as esperas de rede | incluir `sceNetEpollWait`, `sceNetRecv`, `sceRudpPollWait`, `sceHttpWaitRequest` na faixa de 12 000 |

### errno do SceNet

`SCE_NET_ERROR_X = 0x80410100 | errno` (ex.: `EAGAIN` 35 = 0x23 → 0x80410123; `RESOLVER_ENODNS` 225 → 0x804101E1).
Toda função `sceNet*` que falha deve, além de retornar o código negativo, gravar `code & 0xFF` como `int` no endereço
devolvido por `sceNetErrnoLoc` (Vita3K faz o mesmo em `ret_net_errno`). Helper sugerido em `Net`:
`int NetErr(int code) { GuestMemory.W32(ErrnoAddr, (uint)(code & 0xFF)); return code; }`. O errno é por thread no
hardware? **A confirmar** (testar com duas threads num homebrew); por ora um slot global como hoje.

### Como confirmar o que está marcado "a confirmar"

- **Homebrew de teste no Vita real** (VitaSDK): chamar a função com entradas controladas e imprimir saída/retorno.
  Caminho mais barato para algoritmos puros (escape, build, merge, parse).
- **Desmontagem do módulo do sistema**: `vs0:sys/external/libhttp.suprx`, `libnetctl`, `librudp.suprx`,
  `libnet_adhoc_matching.suprx` (nomes a confirmar no `vs0:sys/external/`). Procurar tabelas de classes de caractere e
  constantes de erro.
- **Call sites no jogo**: procurar no eboot descriptografado as constantes de erro imediatas (`0x8077xxxx`,
  `0x804131xx`) perto das chamadas, e o tamanho do buffer de pilha passado (para structs de layout desconhecido).
- **Vita3K**: referência de semântica; muitas funções aqui estão `UNIMPLEMENTED()` lá (Rudp e AdhocMatching inteiros).

---

## Infraestrutura comum

### Modelo de objetos HTTP

Hoje `sceHttpCreateTemplate/Connection/Request*` devolvem `_nextId++` (compartilhado com sockets e resolvers) e nada é
guardado. Proposta (nova classe `public sealed class Http : HleModule` em `Hle/Modules/Http.cs`, movendo todos os
`sceHttp*`/`sceHttps*` de `Net` — não deixar `[Hle]` duplicado):

```text
HttpState { bool Inited; uint PoolSize; Dictionary<int, HttpObj> Objs; int NextId = 1;
            uint SslFlags (global, sceHttpsEnable/DisableOption); bool DefaultGzip; CookieJar Cookies; }
HttpObj   { int Id; Kind (Template|Connection|Request); int ParentId;
            string? UserAgent, Url, Host, Scheme; ushort Port; int Method; string MethodStr; ulong ContentLength;
            HttpSettings S; List<(string Name, string Value)> Headers; bool Aborted; int LastErrno;
            uint HeadersGuestBuf (alocado sob demanda, liberado no Delete); epoll binding }
HttpSettings (copiado do pai na criação — template → conexão → requisição; a confirmar se é cópia ou herança viva):
  ResolveTimeoutUs = 1 000 000, ResolveRetry = 5, ConnectTimeoutUs = 30 000 000, SendTimeoutUs = 120 000 000,
  RecvTimeoutUs = 120 000 000, ResponseHeaderMax = 5000, Nonblock = 0, AutoRedirect = 1, AuthEnabled = 1,
  CookieEnabled = 1 (a confirmar), AcceptGzip = DefaultGzip (a confirmar), InflateGzip = 1 (a confirmar),
  IcmOption = 0 (a confirmar), callbacks (auth, redirect, cookieRecv, cookieSend, ssl) = (0, 0), SslFlags (Option2)
```

Os defaults numéricos vêm dos `#define SCE_HTTP_DEFAULT_*` de `psp2/net/http.h`. IDs: inteiros positivos, um espaço só
para os três tipos (`SCE_HTTP_INVALID_ID` = 0).

Erros comuns a todo o SceHttp:

| Situação | Código |
| --- | --- |
| antes de `sceHttpInit` | `SCE_HTTP_ERROR_BEFORE_INIT` 0x80431001 |
| ID inexistente ou de tipo errado (ex.: setter de requisição com ID de template) | `SCE_HTTP_ERROR_INVALID_ID` 0x80431100 |
| ponteiro de saída nulo / valor fora da faixa | `SCE_HTTP_ERROR_INVALID_VALUE` 0x804311FE |
| buffer de saída pequeno nas funções de URI | `SCE_HTTP_ERROR_OUT_OF_MEMORY` 0x80431022 (Vita3K usa este em `sceHttpUriParse`; a confirmar vs `OUT_OF_SIZE` 0x80431104) |
| requisição abortada | `SCE_HTTP_ERROR_ABORTED` 0x80431080 |

### Tabela de sockets e epoll do SceNet

```text
NetSocket { int Id; int Domain, Type, Protocol; string Name; bool Nbio; uint RcvTimeoUs, SndTimeoUs (0 = infinito);
            SceNetSockaddrIn? Local, Peer; bool Listening; int AbortGen; bool Closed; object Lock }
NetEpoll  { int Id; Dictionary<int, (uint Events, ulong Data)> Entries; int AbortGen; bool Destroyed; object Lock }
```

IDs positivos, espaço próprio (não reaproveitar `_nextId` do HTTP). Socket inexistente/fechado → `SCE_NET_ERROR_EBADF`
0x80410109.

### Padrão de espera

Usar `Kernel.Wait.Until(sync, ready, timeout, abort, what)` (`Kernel/Sync.cs`): libera o core emulado, respeita tempo
guest/turbo, processa callbacks quando a função termina em `CB` (o binder liga `CallbackWait` pelo nome,
`HleFramework.cs:106`) e sai em `StopRequested`. Sinalizar com `Monitor.PulseAll(sync)` em Abort/Cancel/Destroy/Close.
Para timeout por valor, criar a sobrecarga mencionada acima.

---

## SceHttp

### sceHttpUriEscape
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceHttp · **NID:** 0x1DA2A673
- **Protótipo:** `int sceHttpUriEscape(char *out, unsigned int *require, unsigned int prepare, const char *in);` (`psp2/net/http.h`)
- **Comportamento:** percent-encoding da string `in` (terminada em NUL). Cada byte "seguro" é copiado; qualquer outro
  vira `%HH`. Escreve em `*require` o tamanho necessário **incluindo o NUL**. Padrão de uso: primeira chamada com
  `out = NULL`/`prepare = 0` para obter o tamanho, segunda com o buffer.
- **Algoritmo:**
  ```text
  if in == NULL                     -> return SCE_HTTP_ERROR_INVALID_VALUE
  need = 1
  for b in bytes(in) until 0:       need += Safe(b) ? 1 : 3
  if require != NULL:               *require = need
  if out == NULL:                   return 0            // só consulta de tamanho (a confirmar; Vita3K devolve INVALID_VALUE)
  if prepare < need:                return SCE_HTTP_ERROR_OUT_OF_MEMORY (0x80431022)   // a confirmar
  o = 0
  for b in bytes(in) until 0:
      if Safe(b): out[o++] = b
      else:       out[o++] = '%'; out[o++] = HEX[b >> 4]; out[o++] = HEX[b & 15]   // HEX = "0123456789ABCDEF"
  out[o] = 0; return 0
  Safe(b) = b in 'A'..'Z' | 'a'..'z' | '0'..'9' | '-' | '.' | '_' | '~'     // "unreserved" da RFC 3986
  ```
  **A confirmar:** (a) o conjunto seguro — a RFC 2396 também deixa `! * ' ( )` sem escape, e algumas libs preservam os
  reservados `: / ? # [ ] @ ! $ & ' ( ) * + , ; =`; (b) maiúsculas vs minúsculas no hex; (c) se espaço vira `%20` ou
  `+`. Como confirmar: homebrew no Vita escapando a string com todos os bytes 0x01–0xFF e imprimindo o resultado, ou
  localizar a tabela de 256 entradas em `libhttp.suprx`. Offline o conteúdo exato só afeta o tamanho do buffer e a URL
  que nunca sai do console, então o conjunto RFC 3986 desbloqueia o jogo.
- **Retorno/erros:** 0; `SCE_HTTP_ERROR_INVALID_VALUE` 0x804311FE (`in` nulo); `SCE_HTTP_ERROR_OUT_OF_MEMORY` 0x80431022
  (buffer pequeno). Exige `sceHttpInit`? O Vita3K checa `BEFORE_INIT`; **a confirmar** — recomendação: não checar
  (função utilitária pura; ser leniente não quebra jogo correto).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** classe `Http` (ou `Net`, `Online.cs`). Receber `in` como `uint` e ler **bytes**
  (`GuestMemory.Span`/loop até 0) — não usar `string` do binder, que decodifica texto e perderia bytes ≥ 0x80. Colocar o
  algoritmo num helper estático `HttpUri.Escape(ReadOnlySpan<byte>)` para teste de unidade.
- **Validação:** teste de unidade: `"a b&c=d/é"` (UTF-8) → `"a%20b%26c%3Dd%2F%C3%A9"`, `require` = 23; `"AZaz09-._~"`
  inalterado. `VitaSharp.Desktop imports PCSG00198` sem `sceHttpUriEscape` na lista de faltantes; boot do Sakatsuku sem
  o log `Unimplemented HLE function`.

### sceHttpUriUnescape
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0x1274D318
- **Protótipo:** `int sceHttpUriUnescape(char *out, unsigned int *require, unsigned int prepare, const char *in);` (`psp2/net/http.h`)
- **Comportamento/algoritmo:** inverso do escape.
  ```text
  if in == NULL -> SCE_HTTP_ERROR_INVALID_VALUE
  percorrer in: se in[i] == '%' e in[i+1], in[i+2] são hex (maiúsc. ou minúsc.) -> 1 byte (valor), i += 3
                senão -> copia in[i] literal (inclusive '%' inválido e '+')   // a confirmar: '%' inválido pode ser erro INVALID_URL 0x80433060
  need = bytes_produzidos + 1; *require = need (se não nulo)
  out == NULL -> return 0; prepare < need -> OUT_OF_MEMORY 0x80431022; senão escreve + NUL, return 0
  ```
  `+` **não** vira espaço (isso é `application/x-www-form-urlencoded`, não URI; a confirmar). Bytes decodificados podem
  ser 0x00 (`%00`): copiar e continuar? **A confirmar**; recomendação: copiar (o NUL final ainda é escrito).
- **Retorno/erros:** 0; `INVALID_VALUE` 0x804311FE; `OUT_OF_MEMORY` 0x80431022.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** helper `HttpUri.Unescape` ao lado do escape; mesma leitura por bytes.
- **Validação:** ida e volta `Unescape(Escape(x)) == x` para bytes 1–255; `"%4a%4A%zz%"` → `"JJ%zz%"`.

### sceHttpUriBuild
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0x47664424
- **Protótipo:** `int sceHttpUriBuild(char *out, unsigned int *require, unsigned int prepare, const SceHttpUriElement *srcElement, unsigned int option);` (`psp2/net/http.h`)
- **Comportamento/algoritmo:** monta uma URL a partir dos campos selecionados por `option` (`SceHttpUriBuildType`:
  SCHEME 0x01, HOSTNAME 0x02, PORT 0x04, PATH 0x08, USERNAME 0x10, PASSWORD 0x20, QUERY 0x40, FRAGMENT 0x80,
  ALL 0xFFFF). Ponteiro de campo nulo = string vazia.
  ```text
  s = ""
  if (opt & SCHEME) and scheme != "":  s += scheme + ":"; if !opaque: s += "//"
  if !opaque:
      if (opt & USERNAME) and username != "":
          s += username
          if (opt & PASSWORD) and password != "": s += ":" + password
          s += "@"
      if (opt & HOSTNAME): s += hostname
      if (opt & PORT) and port != 0: s += ":" + decimal(port)        // a confirmar: omitir porta padrão (80/443)?
  if (opt & PATH): s += path
  if (opt & QUERY) and query != "":      s += (query[0] == '?' ? "" : "?") + query
  if (opt & FRAGMENT) and fragment != "": s += (fragment[0] == '#' ? "" : "#") + fragment
  need = len(s) + 1; *require = need; out == NULL -> 0; prepare < need -> OUT_OF_MEMORY; escreve s + NUL; return 0
  ```
  O Vita3K (`net_utils::parse_url`) guarda `query` com o `?` e `fragment` com o `#` iniciais, por isso o teste do
  primeiro caractere: funciona com os dois formatos. **A confirmar** no hardware qual formato o `sceHttpUriParse` real
  produz e se "//" é emitido quando SCHEME não está em `option` mas HOSTNAME está (recomendação: não emitir).
- **Retorno/erros:** 0; `INVALID_VALUE` 0x804311FE (`srcElement` nulo); `OUT_OF_MEMORY` 0x80431022.
- **Estruturas:** `SceHttpUriElement` (0x2C): +0x00 `int opaque`, +0x04 `char *scheme`, +0x08 `username`, +0x0C
  `password`, +0x10 `hostname`, +0x14 `path`, +0x18 `query`, +0x1C `fragment`, +0x20 `unsigned short port`,
  +0x22 `reserved[10]`.
- **Implementação no VitaSharp:** helper `HttpUri.Build(UriElement, uint option)`; o mesmo record `UriElement` é usado
  por `sceHttpUriParse` (STUB abaixo) e `sceHttpUriMerge`.
- **Validação:** `Build(Parse(u), ALL) == u` para `http://user:pw@host:8080/a/b?x=1#f`; `option = HOSTNAME|PATH` →
  `host/a/b`.

### sceHttpUriMerge
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0x75027D1D
- **Protótipo:** `int sceHttpUriMerge(char *mergedUrl, const char *url, const char *relativeUrl, unsigned int *require, unsigned int prepare, unsigned int option);` (`psp2/net/http.h`)
- **Comportamento/algoritmo:** resolução de referência relativa, RFC 3986 §5.2.2 ("strict"), com base `url` e
  referência `relativeUrl`:
  ```text
  R = Parse(relativeUrl) (aceitando referência sem esquema); B = Parse(url)
  if R.scheme definido:       T = R; T.path = RemoveDotSegments(R.path)
  else if R.authority definida: T = R (com scheme de B); T.path = RemoveDotSegments(R.path)
  else if R.path == "":       T.path = B.path; T.query = R.query definido ? R.query : B.query
  else if R.path começa com '/': T.path = RemoveDotSegments(R.path); T.query = R.query
  else: T.path = RemoveDotSegments(MergePaths(B, R.path)); T.query = R.query
        MergePaths: se B tem authority e B.path == "" -> "/" + R.path; senão B.path até o último '/' (inclusive) + R.path
  T.scheme/authority herdados de B quando não vieram de R; T.fragment = R.fragment
  s = Build(T, ALL); need = len+1; *require; mergedUrl == NULL -> 0; prepare < need -> OUT_OF_MEMORY; escreve; 0
  ```
  `RemoveDotSegments` = RFC 3986 §5.2.4 (mesmo de `sceHttpUriSweepPath`). `option`: semântica desconhecida; aceitar 0
  e **a confirmar** outros valores (logar quando ≠ 0).
- **Retorno/erros:** 0; `url` nulo → `SCE_HTTP_ERROR_INVALID_URL` 0x80433060; `relativeUrl` nulo →
  `INVALID_VALUE` 0x804311FE (ambos como no Vita3K); `url` com esquema desconhecido → `SCE_HTTP_ERROR_UNKNOWN_SCHEME`
  0x80431061; `OUT_OF_MEMORY` 0x80431022.
- **Estruturas:** nenhuma além de `SceHttpUriElement` (interno).
- **Implementação no VitaSharp:** helper `HttpUri.Merge`. Não usar `System.Uri` (normaliza demais: muda caixa,
  remove porta padrão, re-escapa).
- **Validação:** exemplos da RFC 3986 §5.4.1 com base `http://a/b/c/d;p?q` (`g` → `http://a/b/c/g`,
  `../..` → `http://a/`, `?y` → `http://a/b/c/d;p?y`, `#s` → `http://a/b/c/d;p?q#s`).

### sceHttpUriSweepPath
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0x50737A3F
- **Protótipo:** `int sceHttpUriSweepPath(char *dst, const char *src, unsigned int srcSize);` (`psp2/net/http.h`)
- **Comportamento/algoritmo:** remove segmentos `.` e `..` do caminho. `dst` precisa ter pelo menos `srcSize` bytes (a
  saída nunca é maior que a entrada).
  ```text
  if srcSize == 0: return 0                                  // como no Vita3K, nada escrito
  if dst == NULL or src == NULL: return INVALID_VALUE 0x804311FE
  input = src[0 .. min(srcSize - 1, strlen(src))]            // srcSize inclui o NUL (Vita3K); a confirmar
  se input começa com '/': path = input; prefix = ""
  senão se contém "://": prefix = até o primeiro '/' após "://"; path = resto      // a confirmar
  senão: copia input para dst + NUL; return 0
  RemoveDotSegments(path) (RFC 3986 §5.2.4):
     in = path; out = ""
     loop enquanto in != "":
       A. in começa com "../" ou "./"  -> remove esse prefixo
       B. in começa com "/./" ou in == "/."  -> troca por "/"
       C. in começa com "/../" ou in == "/.." -> troca por "/" e remove o último segmento (e o '/' anterior) de out
       D. in == "." ou in == ".." -> in = ""
       E. senão move o primeiro segmento (com o '/' inicial, até o próximo '/' exclusivo) de in para out
  dst = prefix + out + NUL; return 0
  ```
  O Vita3K usa `std::filesystem::lexically_normal`, que também colapsa `//` — **não** fazer isso (a RFC preserva).
  Query/fragment (`?`, `#`) dentro de `src`: parar o "path" no primeiro `?`/`#` e copiar o resto sem alteração
  (a confirmar).
- **Retorno/erros:** 0 (a confirmar se o hardware devolve o comprimento resultante); `INVALID_VALUE` 0x804311FE.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** helper `HttpUri.RemoveDotSegments`, compartilhado com `Merge`.
- **Validação:** `/a/b/c/./../../g` → `/a/g`; `/a/../../b` → `/b`; `mid/content=5/../6` → inalterado (sem '/' inicial
  nem esquema).

### sceHttpParseStatusLine
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0x179C56DB
- **Protótipo:** `int sceHttpParseStatusLine(const char *statusLine, unsigned int lineLen, int *httpMajorVer, int *httpMinorVer, int *responseCode, const char **reasonPhrase, unsigned int *phraseLen);` (`psp2/net/http.h`)
- **Comportamento/algoritmo:** analisa a linha de status de uma resposta HTTP (`HTTP/1.1 200 OK\r\n`). O buffer **não**
  precisa terminar em NUL; considerar só `lineLen` bytes e parar no primeiro `\r` ou `\n`.
  ```text
  if statusLine == NULL -> SCE_HTTP_ERROR_PARSE_HTTP_INVALID_RESPONSE 0x80432060
  if algum ponteiro de saída nulo -> SCE_HTTP_ERROR_INVALID_VALUE 0x804311FE
  *httpMajorVer = *httpMinorVer = 0
  L = min(lineLen, posição do primeiro CR/LF)
  if L < 12 ("HTTP/x.y nnn") ou não começa com "HTTP/" -> PARSE_HTTP_INVALID_RESPONSE
  major = dígitos após "HTTP/" (≥1 dígito), exige '.', minor = dígitos (≥1); senão INVALID_RESPONSE
  exige 1 espaço (aceitar vários? a confirmar), depois exatamente 3 dígitos -> code; senão INVALID_RESPONSE
  após o código: fim da linha -> frase vazia; ' ' -> frase = resto até L
  *httpMajorVer = major; *httpMinorVer = minor; *responseCode = code
  *reasonPhrase = statusLine + offset_da_frase      // ponteiro para DENTRO do buffer do jogo
  *phraseLen = L - offset_da_frase                   // sem CR/LF e sem NUL
  return 0
  ```
  Diferenças em relação ao Vita3K (intencionais): o Vita3K aloca uma cópia da frase na memória guest (e aloca 1 byte a
  menos que copia) e soma 1 em `phraseLen`; o protótipo com `const char **` e o buffer sem NUL indicam ponteiro para o
  próprio buffer. **A confirmar:** valor de retorno em sucesso (0 no Vita3K; algumas libs devolvem o comprimento
  consumido) e se `phraseLen` inclui o terminador.
- **Retorno/erros:** 0; `PARSE_HTTP_INVALID_RESPONSE` 0x80432060; `INVALID_VALUE` 0x804311FE. Checa init no Vita3K
  (`BEFORE_INIT`); recomendação: não checar (pura).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** helper `HttpParse.StatusLine(ReadOnlySpan<byte>)` retornando offsets; o HLE só
  converte offset em endereço guest.
- **Validação:** `"HTTP/1.1 404 Not Found\r\n"` → 1, 1, 404, ptr+13, 9; `"HTTP/1.0 200\r\n"` → frase vazia;
  `"HTTX/1.1 200 OK"` → 0x80432060.

### sceHttpUriParse
- [x] **Prioridade:** STUB · **Biblioteca:** SceHttp · **NID:** 0x1D45F24E
- **Protótipo:** `int sceHttpUriParse(SceHttpUriElement *out, const char *srcUrl, void *pool, unsigned int *require, unsigned int prepare);` (`psp2/net/http.h`)
- **Stub atual:** `Online.cs:239` devolve sempre `SCE_HTTP_ERROR_INVALID_URL`, o que quebra qualquer jogo que decomponha
  uma URL (e é a base de `Build`/`Merge`).
- **Comportamento:** decompõe `srcUrl` em `SceHttpUriElement`; as strings vão para `pool` (buffer do jogo de `prepare`
  bytes), concatenadas com NUL; os ponteiros do elemento apontam para dentro de `pool`.
  ```text
  srcUrl == NULL -> INVALID_URL 0x80433060
  (pool == NULL ou out == NULL) e require == NULL -> INVALID_VALUE 0x804311FE
  scheme = até o primeiro ':'; aceitar só "http"/"https" (Vita3K) -> senão UNKNOWN_SCHEME 0x80431061   // a confirmar outros esquemas/opaque
  exige "//" após ':' -> senão INVALID_URL (Vita3K trata opaque como inválido; a confirmar)
  authority até o próximo '/', '?' ou '#': [user[:password]@]host[:port]
     user/password ≥ 256 (SCE_HTTP_USERNAME_MAX_SIZE) -> OUT_OF_SIZE 0x80431104
  path até '?'/'#'; query com o '?' inicial; fragment com o '#' inicial (formato do Vita3K)
  port ausente -> 0 (Vita3K; a confirmar se o hardware põe 80/443)
  need = |scheme|+|user|+|pw|+|host|+|path|+|query|+|fragment| + 7 (7 NULs); *require = need
  prepare < need -> OUT_OF_MEMORY 0x80431022
  se pool e out: zera out (0x2C), opaque = 0, copia cada string para pool na ordem acima, grava ponteiros e port
  return 0
  ```
  Nota: o Vita3K não trata host sem `@` mas com porta quando há credenciais e porta juntas
  (`user:pw@host:8080`); a implementação deve separar `userinfo@` primeiro e depois `host:port`.
- **Retorno/erros:** 0; `INVALID_URL` 0x80433060; `UNKNOWN_SCHEME` 0x80431061; `OUT_OF_SIZE` 0x80431104;
  `OUT_OF_MEMORY` 0x80431022; `INVALID_VALUE` 0x804311FE.
- **Estruturas:** `SceHttpUriElement` (ver `sceHttpUriBuild`).
- **Implementação no VitaSharp:** helper `HttpUri.Parse` + escrita no pool; mesma classe dos demais.
- **Validação:** `http://user:pw@host:8080/a/b?x=1#f` → campos esperados, `require` = 4+4+2+4+4+4+2+7 = 31.

### sceHttpParseResponseHeader
- [x] **Prioridade:** STUB · **Biblioteca:** SceHttp · **NID:** 0x03A6C89E
- **Protótipo:** `int sceHttpParseResponseHeader(const char *header, unsigned int headerLen, const char *fieldStr, const char **fieldValue, unsigned int *valueLen);` (`psp2/net/http.h`)
- **Stub atual:** `Online.cs:214` devolve sempre `PARSE_HTTP_NOT_FOUND`. Função pura: o jogo pode chamá-la com cabeçalhos
  próprios (ex.: lidos de cache), independentemente da rede.
- **Comportamento:** procura, nos `headerLen` bytes de `header`, a linha `Nome: valor` cujo nome é igual a `fieldStr`
  sem diferenciar maiúsculas; `*fieldValue` = ponteiro para o primeiro caractere do valor (após `:` e espaços/tabs),
  dentro do buffer do jogo; `*valueLen` = comprimento até o CRLF (sem espaços finais; a confirmar). Linhas de
  continuação (começando com SP/HT) fazem parte do valor? **a confirmar**; recomendação: não tratar (raras).
- **Retorno/erros:** 0 (a confirmar: algumas libs devolvem o comprimento da linha); não encontrado →
  `SCE_HTTP_ERROR_PARSE_HTTP_NOT_FOUND` 0x80432025; ponteiros nulos → `PARSE_HTTP_INVALID_VALUE` 0x804321FE (a
  confirmar vs `INVALID_VALUE` 0x804311FE).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** helper `HttpParse.FindHeader`; também reaproveitável em `sceHttpGetAllResponseHeaders`
  quando houver rede real. O Vita3K aloca cópia na memória guest; não fazer isso (vaza e o protótipo é `const char **`).
- **Validação:** `"Content-Type: text/html\r\nX-A:  1 \r\n"` com `"x-a"` → ptr para `1`, len 1.

### Requisições: criação, cabeçalhos, erros

#### sceHttpCreateRequest2
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0x857D9242
- **Protótipo:** `int sceHttpCreateRequest2(int connId, const char *method, const char *path, unsigned long long contentLength);` (inferido do Vita3K; não está em `http.h`)
- **Comportamento:** igual a `sceHttpCreateRequest`, mas o método é uma string (`"GET"`, `"POST"`, `"HEAD"`,
  `"OPTIONS"`, `"PUT"`, `"DELETE"`, `"TRACE"`, `"CONNECT"`). URL = URL da conexão + `path`.
- **Retorno/erros:** ID da requisição (> 0); `BEFORE_INIT` 0x80431001; conexão inexistente → `INVALID_ID` 0x80431100;
  `path`/`method` nulo → `INVALID_VALUE` 0x804311FE; método desconhecido → `SCE_HTTP_ERROR_UNKNOWN_METHOD` 0x8043106B
  (o Vita3K converte para −1 e cai nesse erro; a confirmar se métodos customizados são aceitos).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** classe `Http`; registra `HttpObj` Kind=Request com `ParentId = connId` e settings
  copiados da conexão. `contentLength` é `ulong` em par alinhado (r2:r3).
- **Validação:** criar template→conexão→requisição; `sceHttpDeleteRequest` do ID devolvido retorna 0, e um segundo delete
  retorna `INVALID_ID`.

#### sceHttpCreateRequestWithURL2
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0x1B6B7983
- **Protótipo:** `int sceHttpCreateRequestWithURL2(int connId, const char *method, const char *url, unsigned long long contentLength);` (inferido do Vita3K)
- **Comportamento:** como `sceHttpCreateRequestWithURL` com método em string. A URL é validada com o parser de
  `sceHttpUriParse`.
- **Retorno/erros:** ID; `BEFORE_INIT`; `INVALID_ID`; `INVALID_VALUE`; `UNKNOWN_METHOD` 0x8043106B; URL inválida →
  `INVALID_URL` 0x80433060; esquema desconhecido → `UNKNOWN_SCHEME` 0x80431061.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** idem `CreateRequest2`.
- **Validação:** idem, com URL absoluta.

#### sceHttpAbortRequestForce
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0x3D44A6E5
- **Protótipo:** `int sceHttpAbortRequestForce(int reqId);` (inferido; não está em `http.h`)
- **Comportamento:** aborta a requisição imediatamente (sem esperar o fechamento ordenado da conexão). Offline: marca
  `Aborted = true` e acorda quem espera na requisição (`sceHttpWaitRequest`, leituras bloqueantes); operações
  seguintes na requisição retornam `SCE_HTTP_ERROR_ABORTED` 0x80431080.
- **Retorno/erros:** 0; `BEFORE_INIT`; `INVALID_ID` 0x80431100.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** mesma rotina de `sceHttpAbortRequest` (STUB abaixo).
- **Validação:** abort seguido de `sceHttpSendRequest` → 0x80431080.

#### sceHttpAddRequestHeaderRaw
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0x31C00FBA
- **Protótipo:** desconhecido. Inferido: `int sceHttpAddRequestHeaderRaw(int id, const char *header, unsigned int headerLen /*?*/, unsigned int mode /*?*/);` — **a confirmar** pelo call site num jogo (nº de argumentos usados em r0–r3).
- **Comportamento:** adiciona uma linha de cabeçalho já formatada (`"Nome: valor"`) sem validação. Offline: separar no
  primeiro `:` e guardar como `AddRequestHeader` (modo ADD).
- **Retorno/erros:** 0; `BEFORE_INIT`; `INVALID_ID`; ponteiro nulo → `INVALID_VALUE`.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** declarar com 4 `uint` e logar os valores até confirmar.
- **Validação:** `sceHttpRequestGetAllHeaders` mostra a linha adicionada.

#### sceHttpRemoveRequestHeader
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0x5EB5F548
- **Protótipo:** `int sceHttpRemoveRequestHeader(int id, const char *name);` (`psp2/net/http.h`)
- **Comportamento:** remove todas as entradas com esse nome (sem diferenciar maiúsculas) da lista de cabeçalhos do
  objeto (template, conexão ou requisição).
- **Retorno/erros:** 0 (também quando o nome não existe, como no Vita3K; a confirmar `NOT_FOUND` 0x80431025);
  `BEFORE_INIT`; `INVALID_ID`; `name` nulo → `INVALID_VALUE`.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** lista `Headers` do `HttpObj`.
- **Validação:** add → remove → `RequestGetAllHeaders` sem a linha.

#### sceHttpRequestGetAllHeaders
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0xBAA34D18
- **Protótipo:** `int sceHttpRequestGetAllHeaders(int reqId, char **header, unsigned int *headerSize);` (inferido do Vita3K, análogo a `sceHttpGetAllResponseHeaders`)
- **Comportamento:** devolve os cabeçalhos de **requisição** que seriam enviados, formatados `Nome: valor\r\n` (inclui
  os automáticos `Host`, `User-Agent`, `Content-Length` quando aplicável? **a confirmar**). O buffer pertence à
  biblioteca e vale até a requisição ser apagada.
- **Retorno/erros:** 0; `BEFORE_INIT`; `INVALID_ID`; ponteiros nulos → `INVALID_VALUE`.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** alocar `HeadersGuestBuf` com `Emu.Memory.Alloc` (realocar se crescer) e liberar em
  `sceHttpDeleteRequest`. `*headerSize` = comprimento sem NUL (o Vita3K soma 1; a confirmar). Corrigir o bug do Vita3K
  (aloca 1 byte e copia a string toda).
- **Validação:** cabeçalho adicionado aparece no texto; tamanho bate com `strlen`.

#### sceHttpGetLastErrno
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0x9A68C3DE
- **Protótipo:** `int sceHttpGetLastErrno(int reqId, int *errNum);` (inferido do Vita3K)
- **Comportamento:** errno de rede do último erro de socket da requisição. Offline: após `sceHttpSendRequest` falhar,
  gravar o errno correspondente à falha de DNS; antes de enviar, 0.
- **Retorno/erros:** 0; `BEFORE_INIT`; `INVALID_ID`; `errNum` nulo → `INVALID_VALUE`. Valor gravado: **a confirmar** se
  é o errno positivo (225 = `RESOLVER_ENODNS`) ou o código `SCE_NET_ERROR_*` completo (0x804101E1); recomendação:
  errno positivo, coerente com `sceNetErrnoLoc`. O Vita3K grava o `errno` do host (incorreto).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** campo `LastErrno` no `HttpObj`, atualizado por `sceHttpSendRequest`.
- **Validação:** send falho → `GetLastErrno` ≠ 0.

#### sceHttpSetResponseHeaderMaxSize
- [x] **Prioridade:** P1 · **Biblioteca:** SceHttp · **NID:** 0xFD76E1FB
- **Protótipo:** `int sceHttpSetResponseHeaderMaxSize(int id, unsigned int headerSize);` (`psp2/net/http.h`)
- **Comportamento:** tamanho máximo do cabeçalho de resposta aceito (excedido → `TOO_LARGE_RESPONSE_HEADER` 0x80431073
  na leitura). Offline: só guarda em `S.ResponseHeaderMax`.
- **Retorno/erros:** 0; `BEFORE_INIT`; `INVALID_ID`; `headerSize > 5000` → `INVALID_VALUE` (Vita3K; a confirmar o
  limite real e se 0 é aceito).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** família de setters (abaixo). Aceita template/conexão/requisição.
- **Validação:** teste de setter.

### Família: configuração por objeto (getters/setters)

Semântica comum: `id` pode ser template, conexão ou requisição (o objeto guarda o valor em `HttpSettings`; objetos
criados depois herdam por cópia). Setter: valida init/ID e valor, grava, retorna 0. Getter: valida init/ID e ponteiro
(`INVALID_VALUE` 0x804311FE se nulo), escreve o valor, retorna 0. `enable` é 0/1; valor diferente → `INVALID_VALUE`
(a confirmar; recomendação: aceitar qualquer ≠ 0 como 1). Offline o valor nunca altera o resultado (a requisição falha
no envio), mas fica disponível para o getter e para a rede real.

| Função | NID | Prio | Protótipo | Campo / observação |
| --- | --- | --- | --- | --- |
| sceHttpSetAuthEnabled | 0x11EC42D0 | P1 | `int (int id, int enable)` (`http.h`) | `AuthEnabled` (default 1, a confirmar) |
| sceHttpGetAuthEnabled | 0x6727874C | P1 | `int (int id, int *enable)` (`http.h`) | |
| sceHttpSetAutoRedirect | 0x34891C3F | P1 | `int (int id, int enable)` (`http.h`) | `AutoRedirect` (default 1, a confirmar) |
| sceHttpGetAutoRedirect | 0x6EAD73EB | P1 | `int (int id, int *enable)` (`http.h`) | |
| sceHttpSetCookieEnabled | 0xAEE573A3 | P1 | `int (int id, int enable)` (`http.h`) | `CookieEnabled` (default a confirmar) |
| sceHttpGetCookieEnabled | 0x1B6EF66E | P1 | `int (int id, int *enable)` (`http.h`) | |
| sceHttpGetNonblock | 0xD65746BC | P1 | `int (int id, int *enable)` (inferido, par de `sceHttpSetNonblock`) | `Nonblock` |
| sceHttpSetAcceptEncodingGZIPEnabled | 0x7295C704 | P1 | `int (int id, int enable)` (inferido) | `AcceptGzip`: envia `Accept-Encoding: gzip` |
| sceHttpGetAcceptEncodingGZIPEnabled | 0xBBFA3C2A | P1 | `int (int id, int *enable)` (inferido) | |
| sceHttpSetInflateGZIPEnabled | 0x5104B13D | P1 | `int (int id, int enable)` (inferido) | `InflateGzip`: descomprime o corpo automaticamente |
| sceHttpSetDefaultAcceptEncodingGZIPEnabled | 0x1EA3BB9C | P1 | `int (int enable)` (inferido; global) | `HttpState.DefaultGzip`, aplicado a templates criados depois; só `BEFORE_INIT` como erro |
| sceHttpSetIcmOption | 0x23978CBC | P1 | `int (int id, int value)` (inferido; semântica de "ICM" desconhecida) | `IcmOption`; guardar e logar; a confirmar |
| sceHttpGetIcmOption | 0x61A4ADDE | P1 | `int (int id, int *value)` (inferido) | |
| sceHttpSetResponseHeaderMaxSize | 0xFD76E1FB | P1 | ver seção própria acima | |

Implementação: um par de helpers genéricos `SetSetting(id, Action<HttpSettings>)` / `GetSetting(id, Func<HttpSettings,int>, Ptr<int>)`.
Validação: para cada par, set → get devolve o mesmo valor; get em ID inválido → 0x80431100.

- [x] **Status:** família implementada em `Hle/Modules/Http.cs` (`SetSetting`/`GetSetting`).

### Família: callbacks

Guardam `(func, userArg)` no objeto; offline **nunca** são chamados (não há resposta de servidor, redirect, desafio de
autenticação nem cookie recebido). Erros: `BEFORE_INIT`, `INVALID_ID`; `cbfunc` nulo é aceito como "remover" (a
confirmar; o Vita3K exige não nulo em `sceHttpsSetSslCallback`). Rede real: invocar com `CallGuest` na thread que chamou
`sceHttpSendRequest` (ou na thread de `WaitRequest` em modo não bloqueante), com os argumentos dos typedefs de `http.h`.

| Função | NID | Prio | Protótipo (`http.h`) |
| --- | --- | --- | --- |
| sceHttpSetAuthInfoCallback | 0xE0A3A88D | P1 | `int (int id, SceHttpAuthInfoCallback cbfunc, void *userArg)` |
| sceHttpSetRedirectCallback | 0x4E08167D | P1 | `int (int id, SceHttpRedirectCallback cbfunc, void *userArg)` |
| sceHttpSetCookieRecvCallback | 0xD4F32A23 | P1 | `int (int id, SceHttpCookieRecvCallback cbfunc, void *userArg)` |
| sceHttpSetCookieSendCallback | 0x11C03867 | P1 | `int (int id, SceHttpCookieSendCallback cbfunc, void *userArg)` |

Validação: set retorna 0 em ID válido e 0x80431100 em inválido.

- [x] **Status:** família implementada (callbacks guardados, nunca chamados offline).

### Família: cookies

Jar global da biblioteca (`HttpState.Cookies`), independente de rede: o jogo pode adicionar cookies (ex.: restaurados
do save) e consultá-los. Modelo mínimo: lista de `{Name, Value, Domain, Path, Secure, ExpiresUtc?}`.

- **sceHttpAddCookie**: interpreta `cookie` (`cookieLength` bytes, sem exigir NUL) como um cabeçalho `Set-Cookie`
  (`nome=valor; Domain=...; Path=...; Expires=...; Secure`) recebido de `url`: Domain padrão = host da URL, Path padrão
  = diretório do path da URL (RFC 6265 §5.1.4). Substitui cookie de mesmo (nome, domínio, path).
- **sceHttpGetCookie**: monta `"n1=v1; n2=v2"` com os cookies cujo domínio casa com o host de `url` (igual ou sufixo
  `.dominio`), cujo path é prefixo do path, não expirados, e `Secure` só se `secure != 0`. `*cookieLength` = tamanho
  necessário (com NUL? **a confirmar**); `cookie == NULL` → só tamanho; `prepare` insuficiente → `OUT_OF_MEMORY`
  0x80431022 (a confirmar); nenhum cookie → `SCE_HTTP_ERROR_NOT_FOUND` 0x80431025 (a confirmar; alternativa: 0 com
  string vazia).
- **Limites** (`SetCookieMax*`): guardar; aplicar na inserção descartando os mais antigos. Valores 0 → `INVALID_VALUE`
  (a confirmar).
- **Export/Import**: formato do hardware é opaco ao jogo (só faz ida e volta pelo save). Exportar num formato privado do
  VitaSharp com magic `"VSCK"` + versão + registros; no import, se o magic não bater (buffer exportado por um Vita
  real), **ignorar e retornar 0** — nunca falhar nem travar.
- Todas: antes de init → `BEFORE_INIT` 0x80431001; ponteiro obrigatório nulo → `INVALID_VALUE` 0x804311FE.

| Função | NID | Prio | Protótipo | Offline |
| --- | --- | --- | --- | --- |
| sceHttpAddCookie | 0xBEDB988D | P1 | `int (const char *url, const char *cookie, unsigned int cookieLength)` (`http.h`) | insere no jar |
| sceHttpGetCookie | 0x70220BFA | P1 | `int (const char *url, char *cookie, unsigned int *cookieLength, unsigned int prepare, int secure)` (`http.h`) | consulta o jar |
| sceHttpCookieFlush | 0x0DACA8D4 | P1 | `int (void)` (inferido) | esvazia o jar; 0 |
| sceHttpCookieExport | 0x4259FB9E | P1 | `int (void *buffer, unsigned int bufferSize, unsigned int *exportSize)` (inferido) | jar → formato VSCK; `buffer == NULL` → só tamanho |
| sceHttpCookieImport | 0x9DF48282 | P1 | `int (const void *buffer, unsigned int bufferSize)` (inferido) | VSCK → jar; magic desconhecido → ignora, 0 |
| sceHttpGetCookieStats | 0x8EF6AF55 | P1 | `int (SceHttpCookieStats *stats)` (inferido; **layout desconhecido**) | ver nota |
| sceHttpSetCookieMaxNum | 0x5DAA4DEF | P1 | `int (unsigned int num)` (inferido) | guarda |
| sceHttpSetCookieMaxNumPerDomain | 0x635F961F | P1 | `int (unsigned int num)` (inferido) | guarda |
| sceHttpSetCookieMaxSize | 0xE9C083A5 | P1 | `int (unsigned int size)` (inferido) | guarda |
| sceHttpSetCookieTotalMaxSize | 0x5C4080BC | P1 | `int (unsigned int size)` (inferido) | guarda |

Nota `sceHttpGetCookieStats`: não escrever um layout inventado. Até confirmar (tamanho do buffer de pilha no call site
de um jogo, ou homebrew), implementar retornando `INVALID_VALUE` para ponteiro nulo e, para não nulo, logar e escrever
apenas zeros no tamanho confirmado. Se nenhum jogo instalado chamar, deixar sem implementar.

Implementação: classe `CookieJar` em `Hle/Modules/Http.cs`. Validação: add `"a=1; Path=/x"` para
`http://h/x/y` → get de `http://h/x/z` devolve `a=1`; de `http://h/w` devolve `NOT_FOUND`; export→flush→import
restaura.

- [x] **Status:** família implementada (`CookieJar`), exceto `sceHttpGetCookieStats` (layout desconhecido, nenhum jogo instalado importa). Passada p1: continua sem
  registro; `SceHttpCookieStats` não existe em vita-headers e o Vita3K é `UNIMPLEMENTED()` sem argumentos.

### Família: epoll e espera não bloqueante do HTTP

API para requisições em modo não bloqueante (`sceHttpSetNonblock`). Protótipos ausentes do `http.h`; inferidos pela API
equivalente do PS4 (mesma biblioteca libhttp) — **todos a confirmar** pelo call site antes de implementar:

```text
typedef void *SceHttpEpollHandle;
typedef struct SceHttpNBEvent { unsigned int events; unsigned int eventDetail; int id; void *userArg; } SceHttpNBEvent; // 0x10, inferido
events: IN 0x01, OUT 0x02, SOCK_ERR 0x08, HUP 0x10, ICM 0x20, RESOLVED 0x10000, RESOLVER_ERR 0x20000 (inferidos)
```

Modelo offline: `HttpEpoll { Dictionary<int reqId, uint userArg>; Queue<SceHttpNBEvent> Pending; bool Aborted }`.
Em modo não bloqueante, `sceHttpSendRequest` de uma requisição associada a um epoll retorna `SCE_HTTP_ERROR_BUSY`
0x80431021 (a confirmar; "em andamento") e enfileira **um** evento `RESOLVER_ERR` com `id = reqId`; o envio
subsequente retorna `SCE_HTTP_ERROR_NETWORK`. Sem epoll associado, manter a falha síncrona atual. `WaitRequest`
devolve os eventos pendentes ou espera até o timeout/abort.

| Função | NID | Prio | Protótipo (inferido) | Offline |
| --- | --- | --- | --- | --- |
| sceHttpCreateEpoll | 0x7C99AF67 | P1 | `int (SceHttpEpollHandle *eh)` (PS4 tem `int libhttpCtxId` antes) | cria `HttpEpoll`, grava handle (ID opaco), 0 |
| sceHttpDestroyEpoll | 0x07D9F8BB | P1 | `int (SceHttpEpollHandle eh)` | remove; acorda esperas com `ABORTED` |
| sceHttpSetEpoll | 0x0F1FD1B3 | P1 | `int (int id, SceHttpEpollHandle eh, void *userArg)` | associa requisição |
| sceHttpGetEpoll | 0x65FE983F | P1 | `int (int id, SceHttpEpollHandle *eh, void **userArg)` | lê associação; sem associação → `NOT_FOUND` 0x80431025 (a confirmar) |
| sceHttpUnsetEpoll | 0xCFB1DA4B | P1 | `int (int id)` | remove associação |
| sceHttpSetEpollId | 0x5CEB6554 | P1 | `int (int id, int epollId)` (desconhecido; talvez associa a um `sceNetEpoll`) | guardar e logar; a confirmar |
| sceHttpGetEpollId | 0x9E031D7C | P1 | `int (int id, int *epollId)` (desconhecido) | devolve o guardado |
| sceHttpWaitRequest | 0x94F7256A | P1 | `int (SceHttpEpollHandle eh, SceHttpNBEvent *nbev, int maxevents, int timeout)` | copia até `maxevents` pendentes e retorna a contagem; sem eventos: `Wait.Until` até `timeout` µs (−1 = infinito, a confirmar) e retorna 0; abort → `ABORTED` |
| sceHttpWaitRequestCB | 0x48650A83 | P1 | idem | idem, processando callbacks (sufixo CB) |
| sceHttpAbortWaitRequest | 0x42E7DFF1 | P1 | `int (SceHttpEpollHandle eh)` | marca `Aborted`, `PulseAll` |

Erros comuns: `BEFORE_INIT`; handle/ID inválido → `INVALID_ID` 0x80431100; `nbev` nulo com `maxevents > 0` →
`INVALID_VALUE`. Rede real: thread de host executando a requisição e enfileirando IN/OUT/HUP. Validação: homebrew de
teste ou jogo que use modo não bloqueante; nenhum dos 7 jogos importa estas funções hoje.

- [x] **Status:** família implementada com os protótipos inferidos acima (handle = ID opaco).

### Família: HTTPS / certificados

Offline nenhum handshake TLS acontece; as funções só guardam configuração.

| Função | NID | Prio | Protótipo | Offline |
| --- | --- | --- | --- | --- |
| sceHttpsEnableOption | 0x9FBE2869 | P1 | `int (unsigned int sslFlags)` (`http.h`) | `SslFlags \|= flags`; `BEFORE_INIT` |
| sceHttpsEnableOption2 | 0x1E93E8EF | P1 | `int (int tmplId, unsigned int sslFlags)` (inferido do Vita3K) | no template; `INVALID_ID` se não for template |
| sceHttpsDisableOption2 | 0x2E77B201 | P1 | `int (int tmplId, unsigned int sslFlags)` (inferido do Vita3K) | `&= ~flags` no template |
| sceHttpsEnableOptionPrivate | 0x72CB0741 | P1 | desconhecido; inferido `int (int id, unsigned int flags)` | aceitar, logar, 0; a confirmar |
| sceHttpsDisableOptionPrivate | 0x00659635 | P1 | desconhecido; inferido `int (int id, unsigned int flags)` | aceitar, logar, 0; a confirmar |
| sceHttpsUnloadCert | 0x8577833F | P1 | `int (void)` (`http.h`) | descarta certificados carregados por `sceHttpsLoadCert`; 0 |
| sceHttpsGetCaList | 0xF71AA58D | P1 | `int (SceHttpsCaList *caList)` (`http.h`) | `caCerts = NULL`, `caNum = 0`, 0 (lista de CAs do sistema vazia; rede real: ler `vs0:data/external/cert/CA_LIST.cer`, ver `sceSslInternalGetCertificateAuthority` em `libssl.h`) |
| sceHttpsFreeCaList | 0x56C95D94 | P1 | `int (SceHttpsCaList *caList)` (`http.h`) | libera o que `GetCaList` alocou (nada offline); 0 |
| sceHttpSslIsCtxCreated | 0x2D3F1281 | P1 | desconhecido; inferido `int (int id)` | retornar 0 (= "não criado"; nenhum contexto TLS existe offline); a confirmar se o retorno é booleano ou via ponteiro |
| sceHttpAuthCacheFlush | 0xAF03924C | P1 | `int (void)` (inferido) | limpa cache de credenciais (vazio offline); 0 |
| sceHttpRedirectCacheFlush | 0x8BAB3971 | P1 | `int (void)` (inferido) | limpa cache de redirects (vazio); 0 |

Flags (`SceHttpsFlag`): SERVER_VERIFY 0x01, CLIENT_VERIFY 0x02, CN_CHECK 0x04, NOT_AFTER_CHECK 0x08,
NOT_BEFORE_CHECK 0x10, KNOWN_CA_CHECK 0x20. **Não** repassar para OpenSSL/SChannel como o Vita3K faz (os valores não
correspondem); na rede real, traduzir para `RemoteCertificateValidationCallback`. Estrutura `SceHttpsCaList` (8):
+0 `void **caCerts`, +4 `int caNum`. Validação: set/get triviais; `GetCaList` devolve `caNum = 0`.

- [x] **Status:** família implementada.

### STUBs do SceHttp (estado descartado)

Todos em `Online.cs:196-226`, corpo `=> 0` ou `_nextId++` sem registro. Com o modelo de objetos:

#### sceHttpInit
- [x] **Prioridade:** STUB · **Biblioteca:** SceHttp · **NID:** 0x214926D9
- **Protótipo:** `int sceHttpInit(unsigned int poolSize);` (`psp2/net/http.h`)
- **Comportamento:** inicializa a biblioteca com um pool interno de `poolSize` bytes. Hoje não guarda estado, então
  nenhuma função pode retornar `BEFORE_INIT`/`ALREADY_INITED`.
- **Retorno/erros:** 0; segunda chamada → `SCE_HTTP_ERROR_ALREADY_INITED` 0x80431020; `poolSize == 0` → `INVALID_VALUE`
  0x804311FE (Vita3K).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `HttpState.Inited = true; PoolSize = poolSize`. `sceHttpGetMemoryPoolStats` passa a
  devolver `poolSize` (+0), `maxInuseSize` (+4), `currentInuseSize` (+8) coerentes (estimar 0 ou soma aproximada).
- **Validação:** Init duplo → 0x80431020; jogos que chamam Init uma vez continuam iguais (cenários de boot dos 7 jogos).

#### sceHttpTerm
- [x] **Prioridade:** STUB · **Biblioteca:** SceHttp · **NID:** 0xC9076666
- **Protótipo:** `int sceHttpTerm(void);` (`psp2/net/http.h`)
- **Comportamento:** encerra a biblioteca, liberando todos os templates/conexões/requisições (e buffers guest
  alocados por eles) e o jar de cookies.
- **Retorno/erros:** 0; sem init → `BEFORE_INIT` 0x80431001.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** limpar `HttpState`; acordar esperas de `WaitRequest` com `ABORTED`.
- **Validação:** Init→Term→Init funciona; Term sem Init → 0x80431001.

#### Delete/Abort/cabeçalhos/conteúdo

| Função | NID | Protótipo (`http.h`) | Comportamento correto | Erros |
| --- | --- | --- | --- | --- |
| sceHttpDeleteTemplate | 0xEC85ECFB | `int (int tmplId)` | remove o template (conexões já criadas continuam válidas, a confirmar) | `BEFORE_INIT`, `INVALID_ID` |
| sceHttpDeleteConnection | 0xF0F65C15 | `int (int connId)` | remove a conexão | idem |
| sceHttpDeleteRequest | 0x3D3D29AD | `int (int reqId)` | remove a requisição, libera `HeadersGuestBuf`, desassocia epoll | idem |
| sceHttpAbortRequest | 0xEA61662F | `int (int reqId)` | `Aborted = true`, `PulseAll`; ops seguintes → `ABORTED` 0x80431080 | idem |
| sceHttpAddRequestHeader | 0x7B51B122 | `int (int id, const char *name, const char *value, unsigned int mode)` | `mode` 0 = OVERWRITE (substitui o de mesmo nome), 1 = ADD; outro → `INVALID_VALUE` | idem + `INVALID_VALUE` para nulos |
| sceHttpSetRequestContentLength | 0x37C30C90 | `int (int id, unsigned long long contentLength)` | grava `ContentLength` (só requisição); após envio → `AFTER_SEND` 0x80431066 (a confirmar) | idem |

Os `sceHttpCreate*` (IL 18, `Online.cs:200-206`) não são triviais, mas precisam registrar o objeto: template guarda
`userAgent`, `httpVer` (1 ou 2; outro → `SCE_HTTP_ERROR_INVALID_VERSION` 0x8043106A), `autoProxyConf`; conexão valida
`tmplId` e o esquema (`http`/`https`, senão `UNKNOWN_SCHEME`); requisição valida `connId` e `method` (0–7, senão
`UNKNOWN_METHOD` 0x8043106B). `sceHttpSendRequest`/`GetStatusCode`/`ReadData`/`GetResponseContentLength`/
`GetAllResponseHeaders` passam a validar o ID e a devolver `ABORTED` se abortada, `BEFORE_SEND` 0x80431065 se ainda não
enviada, e `NETWORK` 0x80431063 após o envio falho. Validação: sequência completa template→conexão→requisição→send→
delete sem vazamento (contar `Objs`), e boot sem regressão em F1 2011, Devious Dungeon (27 imports HTTP) e Killzone.

#### Família: timeouts, retry e nonblock

| Função | NID | Protótipo (`http.h` exceto nonblock) | Campo | Validação de valor |
| --- | --- | --- | --- | --- |
| sceHttpSetConnectTimeOut | 0x237CA86E | `int (int id, unsigned int usec)` | `ConnectTimeoutUs` | qualquer (a confirmar mínimo) |
| sceHttpSetSendTimeOut | 0x8AE3F008 | `int (int id, unsigned int usec)` | `SendTimeoutUs` | idem |
| sceHttpSetRecvTimeOut | 0x94BF196E | `int (int id, unsigned int usec)` | `RecvTimeoutUs` | idem |
| sceHttpSetResolveTimeOut | 0x8455B5B3 | `int (int id, unsigned int usec)` | `ResolveTimeoutUs` | idem |
| sceHttpSetResolveRetry | 0x9AB56EA7 | `int (int id, int retry)` | `ResolveRetry` | `retry < 0` → `INVALID_VALUE` (a confirmar) |
| sceHttpSetNonblock | 0x27A98BDA | `int (int id, int enable)` (inferido; não está em `http.h`) | `Nonblock` | — |

- [x] **Prioridade:** STUB (as 6 acima) · **Biblioteca:** SceHttp
- **Comportamento:** setters da família de configuração: validam init/ID e gravam. Offline o timeout não muda o
  resultado (a falha de DNS é imediata); com rede real, `ResolveTimeoutUs × (ResolveRetry+1)` limita o resolver e os
  demais viram timeouts do socket. Getter só existe para nonblock (`sceHttpGetNonblock`, P1 acima).
- **Retorno/erros:** 0; `BEFORE_INIT`; `INVALID_ID`.
- **Implementação no VitaSharp:** helper `SetSetting`.
- **Validação:** `SetNonblock(id,1)` → `GetNonblock` devolve 1; ID inválido → 0x80431100.

#### Família: HTTPS legada

| Função | NID | Protótipo (`http.h`) | Comportamento correto |
| --- | --- | --- | --- |
| sceHttpsDisableOption | 0xC6D60403 | `int (unsigned int sslFlags)` | `SslFlags &= ~flags`; `BEFORE_INIT` |
| sceHttpsLoadCert | 0xAE8D7C33 | `int (int caCertNum, const SceHttpsData **caList, const SceHttpsData *cert, const SceHttpsData *privKey)` | copiar e guardar os blobs (`SceHttpsData` = +0 `char *ptr`, +4 `unsigned size`) para a rede real; `caCertNum < 0` ou `caList` nulo com `caCertNum > 0` → `INVALID_VALUE`; 0 |
| sceHttpsSetSslCallback | 0xA0926037 | `int (int id, SceHttpsCallback cbfunc, void *userArg)` | guardar no template; `INVALID_ID`; `cbfunc` nulo → `INVALID_VALUE` (Vita3K); offline nunca chamado |

- [x] **Prioridade:** STUB (as 3 acima) · **Biblioteca:** SceHttp
- **Retorno/erros/validação:** como na tabela; set/get coerentes com `sceHttpsEnableOption*`.
- **Implementação no VitaSharp:** campos em `HttpState`/`HttpObj`.

---

## SceNet

### STUBs de sockets

#### sceNetInit
- [x] **Prioridade:** STUB · **Biblioteca:** SceNet · **NID:** 0xEB03E265
- **Protótipo:** `int sceNetInit(SceNetInitParam *param);` (`psp2/net/net.h`)
- **Comportamento:** inicializa a pilha com memória do jogo. Hoje ignora tudo.
- **Retorno/erros:** 0; já inicializado → `SCE_NET_ERROR_EBUSY` 0x80410110; `param` nulo, `memory` nulo,
  `size < 0x4000` ou `flags != 0` → `SCE_NET_ERROR_EINVAL` 0x80410116 (critérios do Vita3K).
- **Estruturas:** `SceNetInitParam` (0xC): +0 `void *memory`, +4 `int size`, +8 `int flags`.
- **Implementação no VitaSharp:** flag `_netInited` em `Net`; funções de socket antes do init →
  `SCE_NET_ERROR_ENOTINIT` 0x804101C8 (a confirmar quais checam; o Vita3K checa em `EtherNtostr/Strton`). Gravar errno.
- **Validação:** cenários de boot dos 7 jogos (todos importam SceNet) sem regressão.

#### sceNetTerm
- [x] **Prioridade:** STUB · **Biblioteca:** SceNet · **NID:** 0xEA3CC286
- **Protótipo:** `int sceNetTerm(void);` (`psp2/net/net.h`)
- **Comportamento:** encerra; fecha sockets/epolls/resolvers abertos e acorda quem espera neles.
- **Retorno/erros:** 0; sem init → `ENOTINIT` 0x804101C8.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** limpar tabelas com `PulseAll` em cada objeto.
- **Validação:** Init→Term→Init.

#### sceNetSocketClose
- [x] **Prioridade:** STUB · **Biblioteca:** SceNet · **NID:** 0x29822B4D
- **Protótipo:** `int sceNetSocketClose(int s);` (`psp2/net/net.h`)
- **Comportamento:** fecha o socket; esperas em `recv`/`accept` nele terminam; o ID passa a ser inválido.
- **Retorno/erros:** 0; ID inexistente → `EBADF` 0x80410109 (+ errno).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** remover de `NetSocket` table, `Closed = true`, `PulseAll`; remover de todos os epolls.
- **Validação:** close duplo → 0x80410109.

#### sceNetSocketAbort
- [x] **Prioridade:** STUB · **Biblioteca:** SceNet · **NID:** 0x891C1B9B
- **Protótipo:** `int sceNetSocketAbort(int s, int flags);` (`psp2/net/net.h`)
- **Comportamento:** acorda operações bloqueadas no socket, que retornam `SCE_NET_ERROR_EINTR` 0x80410104 (a confirmar
  vs `ECANCELED` 0x80410157). `flags`: `RCV_PRESERVATION` 0x1 / `SND_PRESERVATION` 0x2 mantêm o estado abortado para
  operações futuras de recepção/envio (a confirmar); sem flag, aborta só as em andamento.
- **Retorno/erros:** 0; `s < 0` ou `flags` fora de 0..3 → `EINVAL` 0x80410116; inexistente → `EBADF` 0x80410109.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `AbortGen++` + `PulseAll`; o `abort` do `Wait.Until` em recv compara a geração.
- **Validação:** thread em `sceNetRecv` bloqueante sai com 0x80410104 após `SocketAbort` de outra thread.

#### sceNetSetsockopt
- [x] **Prioridade:** STUB · **Biblioteca:** SceNet · **NID:** 0x065505CA
- **Protótipo:** `int sceNetSetsockopt(int s, int level, int optname, const void *optval, unsigned int optlen);` (`psp2/net/net.h`)
- **Comportamento:** hoje descarta tudo; precisa guardar ao menos as opções que mudam o comportamento offline:
  `level = SCE_NET_SOL_SOCKET` (0xFFFF) com `SO_NBIO` 0x1100 (int, não bloqueante), `SO_RCVTIMEO` 0x1006 e
  `SO_SNDTIMEO` 0x1005 (int em µs no Vita; a confirmar vs `struct timeval`). Demais opções: aceitar e guardar num
  dicionário para `sceNetGetsockopt` devolver o mesmo valor.
- **Retorno/erros:** 0; `EBADF` 0x80410109; `optval` nulo ou `optlen < 4` → `EINVAL` 0x80410116 (a confirmar
  `EFAULT` 0x8041010E).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** campos de `NetSocket`; ajustar `sceNetGetsockopt` (`Online.cs:65`, hoje sempre 0)
  para ler deles e respeitar `*optlen`.
- **Validação:** set `SO_NBIO = 1` → get devolve 1.

#### sceNetRecv / sceNetRecvfrom
- [x] **Prioridade:** STUB · **Biblioteca:** SceNet · **NID:** 0x023643B7 (`sceNetRecv`), 0xB226138B (`sceNetRecvfrom`)
- **Protótipo:** `int sceNetRecv(int s, void *buf, unsigned int len, int flags);` / `int sceNetRecvfrom(int s, void *buf, unsigned int len, int flags, SceNetSockaddr *from, unsigned int *fromlen);` (`psp2/net/net.h`)
- **Stub atual:** `Online.cs:62-63` devolvem `EAGAIN` imediatamente mesmo em socket bloqueante → a thread do jogo
  que faz `recv` em loop vira busy loop.
- **Comportamento offline:** nunca chegam dados. Socket não bloqueante (`SO_NBIO`) ou `flags & SCE_NET_MSG_DONTWAIT`
  (0x80): `EAGAIN` 0x80410123 imediato (como hoje). Bloqueante: `Wait.Until` no `NetSocket.Lock` até
  `SO_RCVTIMEO` (se ≠ 0 → `EAGAIN` ao expirar, comportamento BSD), abort (`EINTR` 0x80410104) ou close (`EBADF`).
  Socket STREAM nunca conectado → `ENOTCONN` 0x80410139 imediato.
- **Retorno/erros:** como acima; ID inválido → `EBADF`; sempre gravar errno.
- **Estruturas:** `SceNetSockaddrIn` (0x10): +0 `sin_len`, +1 `sin_family`, +2 `sin_port` (BE), +4 `sin_addr`,
  +8 `sin_vport`, +0xA `sin_zero[6]`.
- **Implementação no VitaSharp:** `Net`, usando a sobrecarga de `Wait.Until` por valor. Incluir `sceNetRecv` em
  `ClassifyCost` de espera.
- **Validação:** cenários Killzone (`kz_cpu*`, `kz_idle*`: medir se alguma thread de rede some do perfil de CPU) e
  boot do Invizimals (26 imports de SceNet) sem travar. Risco: um jogo que hoje "funciona" por receber `EAGAIN` em
  socket bloqueante pode passar a esperar para sempre — nesse caso confirmar se o jogo usa `SO_RCVTIMEO`/abort.

### STUBs de epoll

Modelo: `NetEpoll` (ver "Infraestrutura"). Eventos (`SceNetEpollEventType`): IN 0x1, OUT 0x2, ERR 0x8, HUP 0x10,
DESCID 0x10000. Offline: nenhum socket fica legível; recomendação: também não reportar OUT (o envio falharia), ou seja,
`Wait` sempre expira — exceto `ERR|HUP` para socket fechado enquanto registrado (a confirmar).

#### sceNetEpollControl
- [x] **Prioridade:** STUB · **Biblioteca:** SceNet · **NID:** 0x4C8764AC
- **Protótipo:** `int sceNetEpollControl(int eid, int op, int id, SceNetEpollEvent *event);` (`psp2/net/net.h`)
- **Comportamento:** ADD 1 (registra `id` com `event->events` e `event->data`), MOD 2 (altera), DEL 3 (remove). `id`
  pode ser socket ou resolver (o Vita3K aceita resolver e ignora).
- **Retorno/erros:** 0; epoll inexistente → `EBADF` 0x80410109; `id` inexistente no ADD → `EBADF`; ADD de ID já
  registrado → `EEXIST` 0x80410111; MOD/DEL de não registrado → `ENOENT` 0x80410102; `op` inválido ou `event` nulo em
  ADD/MOD → `EINVAL` 0x80410116 (EEXIST/ENOENT por analogia ao epoll do Linux; a confirmar).
- **Estruturas:** `SceNetEpollEvent` (0x20): +0 `events`, +4 `reserved`, +8 `system[4]`, +0x18 `SceNetEpollData data`
  (8 bytes: `ptr`/`fd`/`u32`/`u64`/`ext{int id; uint u32}`).
- **Implementação no VitaSharp:** `Net`; guardar `events` e os 8 bytes de `data`.
- **Validação:** ADD duplo → 0x80410111.

#### sceNetEpollDestroy
- [x] **Prioridade:** STUB · **Biblioteca:** SceNet · **NID:** 0x7915CAF3
- **Protótipo:** `int sceNetEpollDestroy(int eid);` (`psp2/net/net.h`)
- **Comportamento:** destrói; esperas em andamento terminam com `EINTR` 0x80410104 (a confirmar vs `EBADF`).
- **Retorno/erros:** 0; inexistente → `EBADF`.
- **Implementação no VitaSharp:** `Destroyed = true`, `PulseAll`, remover da tabela.
- **Estruturas:** nenhuma.
- **Validação:** destroy duplo → `EBADF`.

#### sceNetEpollWait
- [x] **Prioridade:** STUB · **Biblioteca:** SceNet · **NID:** 0x45CE337D
- **Protótipo:** `int sceNetEpollWait(int eid, SceNetEpollEvent *events, int maxevents, int timeout);` (`psp2/net/net.h`)
- **Stub atual:** `Online.cs:81` ignora `eid` e dorme `max(timeout, 1000)` µs — com `timeout = -1` (infinito) dorme
  só 1 ms e retorna 0, criando um loop quente.
- **Comportamento:** espera até haver eventos registrados prontos, timeout ou abort. `timeout` em µs (Vita3K);
  `< 0` = infinito, 0 = só consulta (a confirmar). Retorna o número de eventos escritos em `events`.
- **Retorno/erros:** ≥ 0; `EBADF`; `maxevents <= 0` ou `events` nulo → `EINVAL`; abort/destroy → `EINTR` 0x80410104.
- **Estruturas:** `SceNetEpollEvent` (acima); na saída `events` e `data` copiados do registro.
- **Implementação no VitaSharp:** `Wait.Until(epoll.Lock, ...)` com timeout por valor; `ClassifyCost` de espera.
- **Validação:** `timeout = -1` sem sockets prontos bloqueia até `sceNetEpollAbort` (thread de teste); perfil de CPU
  dos cenários Killzone.

### Funções P1 do SceNet

#### sceNetEpollWaitCB
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0x92D3E767
- **Protótipo:** `int sceNetEpollWaitCB(int eid, SceNetEpollEvent *events, int maxevents, int timeout);` (`psp2/net/net.h`)
- **Comportamento:** igual a `sceNetEpollWait`, processando callbacks do kernel enquanto espera.
- **Retorno/erros:** idem.
- **Estruturas:** idem.
- **Implementação no VitaSharp:** `[Hle("sceNetEpollWait")] [Hle("sceNetEpollWaitCB")]` no mesmo método; o sufixo
  `CB` liga `CallbackWait` automaticamente (`HleFramework.cs:106`).
- **Validação:** idem `EpollWait`.

#### sceNetEpollAbort
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0x93FCC4E8
- **Protótipo:** `int sceNetEpollAbort(int eid, int flags);` (`psp2/net/net.h`)
- **Comportamento:** acorda as esperas no epoll, que retornam `EINTR` 0x80410104 (a confirmar). Com
  `SCE_NET_EPOLL_ABORT_FLAG_PRESERVATION` (0x1) o estado abortado persiste para esperas futuras até… (a confirmar o que
  limpa; provavelmente só o destroy).
- **Retorno/erros:** 0; `EBADF`; `flags` fora de 0..1 → `EINVAL`.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `AbortGen++` (+ `AbortSticky` se flag), `PulseAll`.
- **Validação:** thread em `EpollWait(-1)` sai após `EpollAbort`.

#### sceNetResolverAbort
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0x38EBBD57
- **Protótipo:** `int sceNetResolverAbort(int rid, int flags);` (`psp2/net/net.h`)
- **Comportamento:** aborta uma resolução em andamento. Offline as resoluções falham na hora (`ENODNS`), então não há
  o que abortar: validar e retornar 0. Flags: `NTOA_PRESERVATION` 0x1, `ATON_PRESERVATION` 0x2.
- **Retorno/erros:** 0; resolver inexistente → `EBADF` 0x80410109 (a confirmar vs `RESOLVER_EINTERNAL` 0x804101DC);
  flags inválidas → `EINVAL`.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** exige tabela de resolvers (hoje `sceNetResolverCreate` só devolve `_nextId++`).
  Rede real: `Dns.GetHostAddressesAsync` com `CancellationToken` cancelado aqui.
- **Validação:** abort em `rid` válido → 0; inválido → erro.

#### sceNetGetpeername
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0x2348D353
- **Protótipo:** `int sceNetGetpeername(int s, SceNetSockaddr *name, unsigned int *namelen);` (`psp2/net/net.h`)
- **Comportamento:** endereço do par conectado. Offline nenhum socket se conecta.
- **Retorno/erros:** `ENOTCONN` 0x80410139 (+ errno); `EBADF`; ponteiros nulos → `EINVAL`.
- **Estruturas:** `SceNetSockaddrIn` (ver `sceNetRecv`).
- **Implementação no VitaSharp:** `Net`; rede real: `Peer` do `NetSocket`.
- **Validação:** socket recém-criado → 0x80410139.

#### sceNetSendmsg
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0x99C579AE
- **Protótipo:** `int sceNetSendmsg(int s, const SceNetMsghdr *msg, int flags);` (`psp2/net/net.h`)
- **Comportamento:** envio scatter/gather: concatena os `msg_iovlen` buffers de `msg_iov` e envia para `msg_name`
  (ou o par conectado). Offline: mesmo resultado de `sceNetSendto` (falha com o erro de rede corrigido — ver tabela de
  problemas transversais).
- **Retorno/erros:** igual a `sceNetSendto`; `msg` nulo ou `msg_iovlen < 0` → `EINVAL`; `EBADF`.
- **Estruturas:** `SceNetMsghdr` (0x1C): +0 `msg_name`, +4 `msg_namelen`, +8 `msg_iov`, +0xC `msg_iovlen`,
  +0x10 `msg_control`, +0x14 `msg_controllen`, +0x18 `msg_flags`. `SceNetIovec` (8): +0 `iov_base`, +4 `iov_len`.
- **Implementação no VitaSharp:** delegar para a rotina de `sendto`.
- **Validação:** mesmo retorno que `sceNetSendto` com os mesmos dados.

#### sceNetRecvmsg
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0xDE94C6FE
- **Protótipo:** `int sceNetRecvmsg(int s, SceNetMsghdr *msg, int flags);` (`psp2/net/net.h`)
- **Comportamento:** recepção scatter/gather. Offline: mesma semântica de bloqueio de `sceNetRecvfrom` (STUB acima);
  ao expirar/abortar, `msg_flags = 0`.
- **Retorno/erros:** idem `sceNetRecvfrom`; `EINVAL` para `msg` nulo.
- **Estruturas:** `SceNetMsghdr`, `SceNetIovec`.
- **Implementação no VitaSharp:** delegar para a rotina de `recvfrom`.
- **Validação:** socket não bloqueante → `EAGAIN`.

#### sceNetGetSockIdInfo
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0x138CF1D6
- **Protótipo:** `int sceNetGetSockIdInfo(SceNetFdSet *fds, int sockinfoflags, int flags);` (`psp2/net/net.h`)
- **Comportamento:** preenche o bitset `fds` (1024 bits) com os IDs de socket existentes (do processo). Semântica de
  `sockinfoflags` desconhecida (a confirmar). Retorno: provavelmente o número de sockets (a confirmar).
- **Retorno/erros:** ≥ 0; `fds` nulo → `EINVAL`.
- **Estruturas:** `SceNetFdSet` (0x80): `unsigned int bits[32]`, bit `id` = `bits[id >> 5] & (1 << (id & 31))`.
- **Implementação no VitaSharp:** zerar e marcar IDs da tabela de sockets (IDs < 1024).
- **Validação:** criar 2 sockets → 2 bits marcados.

#### sceNetEtherNtostr
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0x84334EB2
- **Protótipo:** `int sceNetEtherNtostr(const SceNetEtherAddr *n, char *str, unsigned int len);` (`psp2/net/net.h`)
- **Comportamento/algoritmo:** formata o MAC como `"xx:xx:xx:xx:xx:xx"` (hex **minúsculo**, 2 dígitos) + NUL (18 bytes).
- **Retorno/erros:** 0; sem `sceNetInit` → `ENOTINIT` 0x804101C8; `n`/`str` nulos ou `len < 18` → `EINVAL` 0x80410116
  (Vita3K exige `len > 0x11`).
- **Estruturas:** `SceNetEtherAddr` (6): `unsigned char data[6]`.
- **Implementação no VitaSharp:** `Net`.
- **Validação:** MAC de `sceNetGetMacAddress` → `"00:1f:a7:56:49:54"`.

#### sceNetEtherStrton
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0xEEC6D75F
- **Protótipo:** `int sceNetEtherStrton(const char *str, SceNetEtherAddr *n);` (`psp2/net/net.h`)
- **Comportamento/algoritmo:** lê 6 grupos de 1–2 dígitos hex (maiúsc./minúsc.) separados por `:`. String inválida:
  o Vita3K ignora (usa `sscanf`) e retorna 0; recomendação: `EINVAL` (a confirmar).
- **Retorno/erros:** 0; `ENOTINIT`; nulos → `EINVAL`.
- **Estruturas:** `SceNetEtherAddr`.
- **Implementação no VitaSharp:** `Net`.
- **Validação:** ida e volta com `EtherNtostr`.

#### sceNetHtonll / sceNetNtohll
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0x12C19209 (`Htonll`), 0xFB3336A6 (`Ntohll`)
- **Protótipo:** `unsigned long long sceNetHtonll(unsigned long long host64);` / `unsigned long long sceNetNtohll(unsigned long long net64);` (`psp2/net/net.h`)
- **Comportamento:** inverte os 8 bytes (o Vita é little-endian).
- **Retorno/erros:** o valor invertido; sem erros.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `[Hle] ulong sceNetHtonll(ulong v) => BinaryPrimitives.ReverseEndianness(v);` (o binder
  lê o argumento do par r0:r1 e devolve em r0:r1).
- **Validação:** `0x0102030405060708` → `0x0807060504030201`.

#### sceNetSetDnsInfo
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0xD62EF218
- **Protótipo:** `int sceNetSetDnsInfo(SceNetDnsInfo *info, int flags);` (`psp2/net/net.h`)
- **Comportamento:** define os servidores DNS usados pelo resolver. Offline: guardar (sem efeito, o resolver falha).
- **Retorno/erros:** 0; `info` nulo → `EINVAL`; `flags != 0` → `EINVAL` (a confirmar).
- **Estruturas:** `SceNetDnsInfo` (8): `SceNetInAddr dns_addr[2]`.
- **Implementação no VitaSharp:** campo em `Net`; rede real: usar no resolver em vez do DNS do host (opcional).
- **Validação:** retorno 0.

#### sceNetClearDnsCache
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0xFEC1166D
- **Protótipo:** `int sceNetClearDnsCache(int flags);` (`psp2/net/net.h`)
- **Comportamento:** limpa o cache do resolver. Offline não há cache.
- **Retorno/erros:** 0; `flags != 0` → `EINVAL` (a confirmar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `=> 0` com validação.
- **Validação:** retorno 0.

#### sceNetShowIfconfig / sceNetShowRoute
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet · **NID:** 0x658B903B (`ShowIfconfig`), 0x6AB3B74B (`ShowRoute`)
- **Protótipo:** `int sceNetShowIfconfig(void *p, int b);` / `int sceNetShowRoute(void);` (`psp2/net/net.h`; significado dos parâmetros de `ShowIfconfig` desconhecido)
- **Comportamento:** depuração: imprimem a configuração de interfaces/rotas no TTY. Mesmo tratamento de
  `sceNetShowNetstat` (já implementado como `=> 0`).
- **Retorno/erros:** 0.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Log.Info` com o perfil offline (192.168.0.10/24, gw 192.168.0.1) e 0.
- **Validação:** retorno 0.

#### Dump (captura de pacotes) — sceNetDumpCreate / Read / Abort / Destroy
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet

| Função | NID | Protótipo (`net.h`) |
| --- | --- | --- |
| sceNetDumpCreate | 0xAFF9FA4D | `int (const char *name, int len, int flags)` |
| sceNetDumpRead | 0x04042925 | `int (int id, void *buf, int len, int *pflags)` |
| sceNetDumpAbort | 0x3B24E75F | `int (int id, int flags)` |
| sceNetDumpDestroy | 0x82DDCF63 | `int (int id)` |

- **Comportamento:** ferramenta de depuração que copia pacotes da pilha para um buffer de `len` bytes. Offline nenhum
  pacote trafega: `Create` valida (`len <= 0` → `EINVAL`) e devolve um ID; `Read` com `*pflags & SCE_NET_DUMP_DONTWAIT`
  (0x20) → `EAGAIN` 0x80410123; sem DONTWAIT espera até `Abort`/`Destroy` e retorna `EINTR` 0x80410104 (a confirmar);
  `PEEK` 0x10 e `OVERFLOW` 0x40 só fazem sentido com dados; `Abort` com `ABORT_FLAG_PRESERVATION` 0x1 mantém estado
  abortado. Em firmware de varejo o dump pode ser restrito (retornar `ENOTSUP`/`EPERM`)? **a confirmar**.
- **Retorno/erros:** ID > 0 / 0; `EBADF` para ID inválido.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** objeto simples em `Net` com o mesmo padrão de espera.
- **Validação:** Create→Read(DONTWAIT)→`EAGAIN`→Destroy.

#### Emulação de rede — sceNetEmulationSet / Get
- [x] **Prioridade:** P1 · **Biblioteca:** SceNet

| Função | NID | Protótipo (`net.h`) |
| --- | --- | --- |
| sceNetEmulationSet | 0x561DFD03 | `int (SceNetEmulationParam *param, int flags)` |
| sceNetEmulationGet | 0xAE3F4AC6 | `int (SceNetEmulationParam *param, int flags)` |

- **Comportamento:** configura perda/atraso artificiais (ferramenta de teste). `flags`: `ETH0` 1, `WLAN0` 2. Offline:
  `Set` copia os 0x200 bytes para um slot por interface; `Get` devolve o guardado (zeros por padrão). Em varejo pode
  exigir modo de desenvolvimento (a confirmar).
- **Retorno/erros:** 0; `param` nulo ou `flags` ∉ {1,2} → `EINVAL`.
- **Estruturas:** `SceNetEmulationParam` (0x200): +0 `version`, +2 `option_number`, +4 `current_version`, +6 `result`,
  +8 `flags`, +0xC `reserved1`, +0x10 `SceNetEmulationData send` (0xE0), +0xF0 `recv` (0xE0), +0x1D0 `seed`,
  +0x1D4 `reserved[44]`.
- **Implementação no VitaSharp:** dois `byte[0x200]` em `Net`.
- **Validação:** Set→Get devolve os mesmos bytes.

### Revisados sem alteração (SceNet)

`sceNetBind` 0x1296A94B, `sceNetListen` 0x7A8DA094, `sceNetShutdown` 0x69E50BB5 (offline basta validar o ID com a
tabela nova e gravar `Local` no bind para `sceNetGetsockname`), `sceNetGetSockInfo` 0xB1AF6840 (retornar 0 = nenhuma
entrada é coerente), `sceNetShowNetstat` 0x338EDC2E, `sceNetResolverDestroy` 0x3559F098 (validar ID quando houver a
tabela de resolvers).

---

## SceNetCtl

Correções no código existente (fazer junto com a P0): flag `_netctlInited` (STUB `sceNetCtlInit` abaixo), ponteiro
nulo → `INVALID_ADDR` 0x80412107, limite de callbacks → `CALLBACK_MAX` 0x80412103 (ver tabela transversal). Códigos
`SCE_NET_CTL_ERROR_*` (do Vita3K, `SceNetCtl.cpp:40-70`): NOT_INITIALIZED 0x80412101, NOT_TERMINATED 0x80412102,
CALLBACK_MAX 0x80412103, ID_NOT_FOUND 0x80412104, INVALID_ID 0x80412105, INVALID_CODE 0x80412106, INVALID_ADDR
0x80412107, NOT_CONNECTED 0x80412108, NOT_AVAIL 0x80412109, INVALID_TYPE 0x8041210F, INVALID_SIZE 0x80412111,
UNKNOWN_DEVICE 0x80412188. Eventos: NONE 0, DISCONNECTED 1, DISCONNECT_REQ_FINISHED 2, IPOBTAINED 3.

### sceNetCtlAdhocGetResult
- [x] **Prioridade:** P0 (Invizimals: The Resistance, PCSA00552) · **Biblioteca:** SceNetCtl · **NID:** 0x7AE0ED19
- **Protótipo:** `int sceNetCtlAdhocGetResult(int eventType, int *errorCode);` (`psp2/net/netctl.h`)
- **Comportamento:** devolve o código de erro associado ao último evento ad hoc do tipo `eventType` (tipicamente chamado
  de dentro do callback registrado com `sceNetCtlAdhocRegisterCallback`, logo após receber `DISCONNECTED`). O VitaSharp
  entrega `DISCONNECTED` uma vez aos callbacks ad hoc em `sceNetCtlCheckCallback` (`Online.cs:100-110`); o Invizimals
  chama esta função em seguida.
- **Offline:** `*errorCode = 0` (desconexão sem erro específico), retorno 0 — mesmo comportamento do Vita3K e do
  `sceNetCtlInetGetResult` existente. Alternativa, se o jogo exigir um motivo: `SCE_NET_CTL_ERROR_WIFI_DISABLED`
  0x80412113 (a confirmar pelo fluxo do jogo; não usar sem evidência).
- **Retorno/erros:** 0; sem init → `NOT_INITIALIZED` 0x80412101; `errorCode` nulo → `INVALID_ADDR` 0x80412107;
  `eventType` desconhecido → `INVALID_TYPE` 0x8041210F? (a confirmar; o Vita3K não valida — recomendação: não validar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Net` em `Online.cs`, ao lado de `sceNetCtlInetGetResult` (`Online.cs:115`).
  `[Hle] int sceNetCtlAdhocGetResult(int eventType, Ptr<int> errorCode)`.
- **Validação:** `VitaSharp.Desktop imports PCSA00552` sem a função na lista; boot do Invizimals passando pela checagem
  de rede sem `Unimplemented HLE function`.

### sceNetCtlAdhocGetPeerList
- [x] **Prioridade:** P1 · **Biblioteca:** SceNetCtl · **NID:** 0x77586C59
- **Protótipo:** `int sceNetCtlAdhocGetPeerList(unsigned int *buflen, void *buf);` (`psp2/net/netctl.h`)
- **Comportamento:** lista os pares ad hoc visíveis como uma lista encadeada de `SceNetCtlAdhocPeerInfo` dentro de `buf`.
  `*buflen`: na entrada, tamanho de `buf` em bytes; na saída, bytes usados/necessários (`buf == NULL` → só tamanho).
  O Vita3K trata `*buflen` como **contagem** de pares; o nome no header sugere bytes — **a confirmar** (homebrew ou
  call site). Offline: nenhum par → `*buflen = 0`, retorno 0.
- **Retorno/erros:** 0; `NOT_INITIALIZED` 0x80412101; `buflen` nulo → `INVALID_ADDR` 0x80412107. Com estado ad hoc
  desconectado o hardware pode devolver `NOT_CONNECTED` 0x80412108 (a confirmar); recomendação: 0 com lista vazia.
- **Estruturas:** `SceNetCtlAdhocPeerInfo` (8): +0 `next` (ponteiro guest para o próximo, NULL no último), +4
  `SceNetInAddr inet_addr`.
- **Implementação no VitaSharp:** `Net`; rede real: pares vindos da camada de matching/descoberta.
- **Validação:** retorno 0 e `*buflen = 0`.

### sceNetCtlGetIfStat
- [x] **Prioridade:** P1 · **Biblioteca:** SceNetCtl · **NID:** 0xDA130450
- **Protótipo:** `int sceNetCtlGetIfStat(int device, SceNetCtlIfStat *ifstat);` (inferido do Vita3K; não está em `netctl.h`)
- **Comportamento:** estatísticas de tráfego da interface. Offline: contadores zerados (ou bytes de fato "enviados" se
  um dia houver), `resetTick` = tick do boot.
- **Retorno/erros:** 0; `NOT_INITIALIZED`; `ifstat` nulo → `INVALID_ADDR` 0x80412107; `ifstat->size != 0x40` →
  `INVALID_SIZE` 0x80412111; `device` inválido → `UNKNOWN_DEVICE` 0x80412188 (valores válidos de `device` a confirmar;
  `SCE_NETCTL_INFO_GET_DEVICE` devolve 0 = wireless).
- **Estruturas:** `SceNetCtlIfStat` (0x40): +0 `size` (preenchido pelo jogo), +4 `totalSec`, +8 `txBytes` (u64),
  +0x10 `rxBytes` (u64), +0x18 `SceRtcTick resetTick`, +0x20 `reserved[8]`.
- **Implementação no VitaSharp:** `Net`; não sobrescrever `size`.
- **Validação:** `size` errado → 0x80412111.

### sceNetCtlGetPhoneMaxDownloadableSize
- [x] **Prioridade:** P1 · **Biblioteca:** SceNetCtl · **NID:** 0x4FB4BEA1
- **Protótipo:** `int sceNetCtlGetPhoneMaxDownloadableSize(long long *maxDownloadableSize);` (inferido do Vita3K)
- **Comportamento:** limite de download por rede celular (modelos 3G). O console emulado está em Wi-Fi: sem limite.
- **Retorno/erros:** 0 com `*max = 0x7FFFFFFFFFFFFFFF` (Vita3K); `NOT_INITIALIZED`; nulo → `INVALID_ADDR`. O valor
  "ilimitado" real é a confirmar (pode ser −1 ou erro `NOT_AVAIL` 0x80412109 fora de 3G).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Net`; `Ptr<long>`/escrever 8 bytes.
- **Validação:** retorno 0.

### sceNetCtlInit
- [x] **Prioridade:** STUB · **Biblioteca:** SceNetCtl · **NID:** 0x495CA1DB
- **Protótipo:** `int sceNetCtlInit();` (`psp2/net/netctl.h`)
- **Comportamento:** hoje `=> 0` sem estado; nenhuma função NetCtl consegue devolver `NOT_INITIALIZED`.
- **Retorno/erros:** 0; já inicializado → `NOT_TERMINATED` 0x80412102 (Vita3K).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `_netctlInited = true`; `sceNetCtlTerm` zera; as funções NetCtl checam o flag.
  Atenção: `sceNetCtlCheckCallback` de jogos que chamam antes do init deve continuar inofensivo (retornar
  `NOT_INITIALIZED` sem chamar callbacks).
- **Validação:** Init duplo → 0x80412102; boot sem regressão nos 7 jogos (todos importam NetCtl).

Revisado sem alteração: `sceNetCtlAdhocDisconnect` 0xED43B79A (`=> 0` é coerente com ad hoc desconectado).

---

## SceSsl

### sceSslFreeSslCertName
- [x] **Prioridade:** P1 · **Biblioteca:** SceSsl · **NID:** 0xC73687E4
- **Protótipo:** `int sceSslFreeSslCertName(SceSslCertName *certName);` (`psp2/libssl.h`)
- **Comportamento:** libera um nome obtido por `sceSslGetSubjectName`/`GetIssuerName`. Offline esses getters devolvem
  NULL (nenhum certificado existe), então nada é liberado.
- **Retorno/erros:** 0 para ponteiro não nulo; nulo → `SCE_SSL_ERROR_INVALID_VALUE` 0x804351FE (a confirmar vs 0);
  sem `sceSslInit` → `SCE_SSL_ERROR_BEFORE_INIT` 0x80435001 (a confirmar).
- **Estruturas:** `SceSslCertName` é opaco (`void`).
- **Implementação no VitaSharp:** `Net` (`Online.cs:230-238`).
- **Validação:** retorno coerente para nulo.

### sceSslInit / sceSslTerm
- [x] **Prioridade:** STUB · **Biblioteca:** SceSsl · **NID:** 0x3C733316 (`sceSslInit`), 0x03CE6E3A (`sceSslTerm`)
- **Protótipo:** `int sceSslInit(unsigned poolSize);` / `int sceSslTerm(void);` (`psp2/libssl.h`)
- **Comportamento:** hoje sem estado. Init: `ALREADY_INITED` 0x80435020 se repetido, `INVALID_VALUE` 0x804351FE se
  `poolSize == 0` (Vita3K); guarda `poolSize` para `sceSslGetMemoryPoolStats` (+0 `poolSize`, +4 `maxInuseSize`,
  +8 `currentInuseSize`). Term: `BEFORE_INIT` 0x80435001 se não inicializado.
- **Retorno/erros:** como acima.
- **Estruturas:** `SceSslMemoryPoolStats` (0x10).
- **Implementação no VitaSharp:** flag + pool em `Net`.
- **Validação:** Init duplo → 0x80435020; boot sem regressão (Devious Dungeon importa 9 funções SSL).

Revisados sem alteração: `sceSslGetSubjectName` 0x9B2F1BC1 e `sceSslGetIssuerName` 0x412711E5 devolvem NULL (correto
offline; não há certificado).

---

## SceLibRudp

Não há header do Rudp no vita-headers e o Vita3K tem todas as funções `UNIMPLEMENTED()`. Protótipos abaixo são
**inferidos** da libRudp do PS3 (`cellRudp`, mesma família de API) e das assinaturas já usadas em `Multiplayer.cs`.
Confirmar pelo call site em Killzone (PCSC00045) ou Sakatsuku (PCSG00198), que importam a biblioteca.

### Códigos de erro (a confirmar)

`Multiplayer.cs:98-100` usa NOT_INITIALIZED 0x80770001, INVALID_CONTEXT_ID 0x80770004, INVALID_ARGUMENT 0x80770005,
WOULDBLOCK 0x8077000D, NOT_ACCEPTABLE 0x80770011, CONN_RESET 0x80770019. A tabela da cellRudp do PS3 é: NOT_INITIALIZED
0x80770001, ALREADY_INITIALIZED 0x80770002, INVALID_CONTEXT_ID 0x80770003, INVALID_ARGUMENT 0x80770004, INVALID_OPTION
0x80770005, INVALID_MUXMODE 0x80770006, MEMORY 0x80770007, INTERNAL 0x80770008, CONN_RESET 0x80770009, CONN_REFUSED
0x8077000A, CONN_TIMEOUT 0x8077000B, CONN_VERSION_MISMATCH 0x8077000C, CONN_TRANSPORT_TYPE_MISMATCH 0x8077000D,
QUALITY_LEVEL_MISMATCH 0x8077000E, THREAD 0x8077000F, THREAD_IN_USE 0x80770010, NOT_ACCEPTABLE 0x80770011,
MSG_TOO_LARGE 0x80770012, NOT_BOUND 0x80770013, CANCELLED 0x80770014, INVALID_VPORT 0x80770015, WOULDBLOCK 0x80770016,
VPORT_IN_USE 0x80770017, VPORT_EXHAUSTED 0x80770018, INVALID_SOCKET 0x80770019, BUFFER_TOO_SMALL 0x8077001A,
MSG_MALFORMED 0x8077001B, ADDR_IN_USE 0x8077001C, ALREADY_BOUND 0x8077001D, ALREADY_EXISTS 0x8077001E,
INVALID_POLL_ID 0x8077001F, TOO_MANY_CONTEXTS 0x80770020, IN_PROGRESS 0x80770021, NO_EVENT_HANDLER 0x80770022,
PAYLOAD_TOO_LARGE 0x80770023, END_OF_DATA 0x80770024, ALREADY_ESTABLISHED 0x80770025, KEEP_ALIVE_FAILURE 0x80770026
(memória da `cellRudp.h` do RPCS3 — conferir a fonte antes de usar). As duas tabelas não batem (ex.: WOULDBLOCK
0x8077000D vs 0x80770016). **Como confirmar:** procurar no eboot de Killzone/Sakatsuku comparações com imediatos
0x8077xxxx logo após `sceRudpRead` (o jogo compara com WOULDBLOCK) e após `sceRudpPollWait`; ou desmontar
`librudp.suprx`. Até confirmar, não mudar os valores existentes; as funções novas usam os nomes e ficam centralizadas
em constantes para troca única.

### Modelo de poll

```text
RudpPoll { int Id; Dictionary<int ctxId, ushort reqEvents> Entries; int CancelGen; bool Destroyed; object Lock }
SceRudpPollEvent (inferido do PS3, 8 bytes): +0 int ctxId, +4 ushort reqEvents, +6 ushort rtnEvents
eventos (inferidos): READ 0x0001, WRITE 0x0002, FLUSH 0x0004, ERROR 0x0008
operações de controle (inferidas): ADD 1, MODIFY 2, DELETE 3
```

Offline nenhum contexto se conecta (`sceRudpInitiate`/`Activate` falham na hora), então nenhum evento fica pronto; o
poll só termina por timeout, cancel ou destroy.

### sceRudpPollWait
- [x] **Prioridade:** STUB · **Biblioteca:** SceLibRudp · **NID:** 0x55918C7C
- **Protótipo:** `int sceRudpPollWait(int pollId, SceRudpPollEvent *events, SceSize eventLen, SceRudpUsec timeout);` (inferido; `SceRudpUsec` = `unsigned long long` como no PS3 — a confirmar; é o tipo que `Multiplayer.cs:168` já usa)
- **Stub atual:** `Multiplayer.cs:168` (`=> 0`) ignora o timeout: a thread de rede do jogo que chama `PollWait` em loop
  vira **busy loop** (Killzone e Sakatsuku importam a função).
- **Comportamento:** espera até algum contexto registrado ter um dos eventos pedidos, o timeout expirar ou o poll ser
  cancelado/destruído. Escreve até `eventLen` entradas (`ctxId`, `reqEvents`, `rtnEvents`) e devolve a contagem.
  Offline: nunca há evento → bloquear pelo timeout e retornar 0.
  Semântica do timeout (µs) **a confirmar**: proposta `0` = não espera (retorna 0 já), `0xFFFFFFFFFFFFFFFF` (ou ≥ 2^63)
  = infinito. Confirmar desmontando como a librudp converte o valor ao chamar `sceNetEpollWait` ou observando o valor
  que Killzone passa (logar no primeiro chamado).
- **Retorno/erros:** ≥ 0; não inicializado → NOT_INITIALIZED; poll inexistente → INVALID_POLL_ID; `events` nulo com
  `eventLen > 0` → INVALID_ARGUMENT; cancelado (`sceRudpPollCancel`) → CANCELLED (a confirmar; alternativa: retornar
  0); destruído durante a espera → INVALID_POLL_ID ou CANCELLED (a confirmar). Valores: ver tabela de erros.
- **Estruturas:** `SceRudpPollEvent` (acima).
- **Implementação no VitaSharp:** `Rudp` em `Multiplayer.cs`; `Wait.Until(poll.Lock, ready: poll.Destroyed ||
  poll.CancelGen != gen, timeoutUs)` com a sobrecarga por valor; adicionar `sceRudpPollWait` à faixa de espera de
  `ClassifyCost`. O `ulong` vai em par alinhado (r2 está ocupado por `eventLen`, então o binder lê da pilha — conferir
  o offset no log da primeira chamada).
- **Validação:** cenários Killzone `kz_cpu*`/`kz_prof`/`kz_idle*`: a thread que chama `sceRudpPollWait` deixa de
  aparecer como consumidora de CPU; `kz_gameplay` sem regressão. Sakatsuku: boot sem travar.

### sceRudpPollCreate / Control / Destroy / Cancel
- [x] **Prioridade:** STUB · **Biblioteca:** SceLibRudp

| Função | NID | Protótipo (inferido) | Hoje | Correto |
| --- | --- | --- | --- | --- |
| sceRudpPollCreate | 0x943D96EC | `int (SceSize size)` | devolve contador sem registrar (IL 27) | registra `RudpPoll`; `size` = nº máx. de entradas (guardar; ADD além dele → INVALID_ARGUMENT, a confirmar); retorna ID > 0; NOT_INITIALIZED |
| sceRudpPollControl | 0x18668BD8 | `int (int pollId, int op, int ctxId, unsigned short events)` | `=> 0` | ADD/MODIFY/DELETE em `Entries`; poll inválido → INVALID_POLL_ID; ctx inválido → INVALID_CONTEXT_ID; op inválido → INVALID_ARGUMENT; ADD repetido → ALREADY_EXISTS (a confirmar) |
| sceRudpPollDestroy | 0xC9ACBDF0 | `int (int pollId)` | `=> 0` | remove; `Destroyed = true`; `PulseAll`; inválido → INVALID_POLL_ID |
| sceRudpPollCancel | 0xA9B92EFE | `int (int pollId)` | `=> 0` | `CancelGen++`; `PulseAll` (acorda `PollWait`); inválido → INVALID_POLL_ID |

- **Comportamento/erros:** como na tabela; `sceRudpTerminate(ctx)` remove o contexto de todos os polls; `sceRudpEnd`
  destrói todos os polls.
- **Estruturas:** `RudpPoll`.
- **Implementação no VitaSharp:** `Rudp`, `Dictionary<int, RudpPoll>`.
- **Validação:** thread em `PollWait(infinito)` acorda com `PollCancel` de outra thread; IDs inválidos → INVALID_POLL_ID.

### sceRudpSetMaxSegmentSize
- [x] **Prioridade:** STUB · **Biblioteca:** SceLibRudp · **NID:** 0xE1DD9645
- **Protótipo:** `int sceRudpSetMaxSegmentSize(unsigned short mss);` (inferido do PS3/`Multiplayer.cs:160`)
- **Comportamento:** define o MSS global; hoje descartado e `sceRudpGetMaxSegmentSize` devolve sempre 1280.
- **Retorno/erros:** 0; NOT_INITIALIZED; `mss` abaixo do mínimo/acima do máximo → INVALID_ARGUMENT (limites a
  confirmar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** campo `_mss = 1280` (default atual, a confirmar) lido por `GetMaxSegmentSize`.
- **Validação:** set 1000 → get 1000.

### sceRudpEnableInternalIOThread2
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibRudp · **NID:** 0x87EC465E
- **Protótipo:** `int sceRudpEnableInternalIOThread2(unsigned int stackSize, unsigned int priority, int cpuAffinityMask);` (inferido: versão 2 de `sceRudpEnableInternalIOThread` com afinidade; a confirmar o 3º argumento)
- **Comportamento:** cria a thread interna que lê o socket UDP e processa eventos (em vez do jogo chamar
  `sceRudpNetReceived`/`ProcessEvents`). Offline não há tráfego: não criar thread; registrar que o modo interno está
  ativo.
- **Retorno/erros:** 0; NOT_INITIALIZED; chamada repetida → THREAD_IN_USE (a confirmar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** mesmo corpo de `sceRudpEnableInternalIOThread` (`Multiplayer.cs:158`) + flag.
  Rede real: thread guest de serviço (padrão de `Gxm.cs:62`, `K.CreateThread` com entry 0) ou thread de host que
  chama os handlers via `CallGuest`.
- **Validação:** retorno 0 após `sceRudpInit`.

### sceRudpGetLocalInfo
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibRudp · **NID:** 0x4DC43692
- **Protótipo:** `int sceRudpGetLocalInfo(int ctxId, int *soc, SceNetSockaddr *addr, SceNetSocklen_t *addrLen, unsigned short *vport, unsigned char *muxMode);` (inferido do PS3 `cellRudpGetLocalInfo`)
- **Comportamento:** devolve o socket, o endereço local, a vport e o modo mux usados no `sceRudpBind` do contexto.
  Ponteiros nulos são ignorados (a confirmar).
- **Retorno/erros:** 0; INVALID_CONTEXT_ID; contexto não ligado → NOT_BOUND (a confirmar).
- **Estruturas:** `SceNetSockaddrIn` (0x10), ver `sceNetRecv`.
- **Implementação no VitaSharp:** guardar `sock`, `vport`, `muxMode` em `sceRudpBind` (`Multiplayer.cs:129`, hoje
  descartados) e o endereço via `sceNetGetsockname` do socket.
- **Validação:** Bind(ctx, s, 3658, 1) → GetLocalInfo devolve s, 3658, 1.

### sceRudpGetRemoteInfo
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibRudp · **NID:** 0x104E4D3B
- **Protótipo:** `int sceRudpGetRemoteInfo(int ctxId, SceNetSockaddr *addr, SceNetSocklen_t *addrLen, unsigned short *vport);` (inferido do PS3)
- **Comportamento:** endereço/vport do par passados a `sceRudpInitiate`/`Activate`. Offline essas chamadas falham, mas
  o endereço pedido pode ser guardado; sem destino conhecido → zeros.
- **Retorno/erros:** 0; INVALID_CONTEXT_ID.
- **Estruturas:** `SceNetSockaddrIn`.
- **Implementação no VitaSharp:** guardar `addr`/`vport` no contexto em `Initiate`/`Activate`.
- **Validação:** Initiate(…) falho → GetRemoteInfo devolve o endereço passado.

### sceRudpGetNumberOfPacketsToRead
- [x] **Prioridade:** P1 · **Biblioteca:** SceLibRudp · **NID:** 0x5D0460CD
- **Protótipo:** `int sceRudpGetNumberOfPacketsToRead(int ctxId);` (inferido do PS3)
- **Comportamento:** nº de mensagens prontas para `sceRudpRead`. Offline: 0.
- **Retorno/erros:** ≥ 0; INVALID_CONTEXT_ID; NOT_INITIALIZED.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `Valid(ctx) ? 0 : ERR_INVALID_CONTEXT_ID` (igual a `GetSizeReadable`).
- **Validação:** retorno 0 para contexto válido.

---

## SceNetAdhocMatching

Usada pelo Sakatsuku 2013 (10 funções importadas). Sem implementação no Vita3K (@7059dc2 todas `UNIMPLEMENTED()`).
Semântica derivada do `sceNetAdhocMatching` do PSP (API equivalente; o Vita mudou só assinaturas) — itens marcados "a
confirmar" referem-se a diferenças possíveis entre PSP e Vita.

### Modelo offline

O console não está em rede ad hoc (`sceNetCtlAdhocGetState` = DISCONNECTED) e **nunca vê pares**: a biblioteca
inicializa, contextos são criados e iniciados normalmente, mas nenhum hello é recebido, então **nenhum evento é
entregue ao handler** e toda operação dirigida a um par retorna `UNKNOWN_TARGET`. Para o jogo isso é idêntico a "estou
procurando jogadores e ninguém apareceu"; o usuário cancela pela UI do jogo, que chama `Stop`/`Delete`/`Term`.

```text
MatchingState { bool Inited; uint PoolSize, PoolPtr; Dictionary<int, MatchingCtx> Ctx; int NextId = 1; }
MatchingCtx   { int Id; int Mode; int MaxNum; ushort Port; int RxBufLen; uint HelloIntUs, KeepAliveIntUs, RexmtIntUs;
                int RetryCount; uint Handler; bool Running; byte[] HelloOpt; }
```

Erros (`psp2/net/adhoc_matching.h`): INVALID_MODE 0x80413101, INVALID_PORT 0x80413102, INVALID_MAXNUM 0x80413103,
RXBUF_TOO_SHORT 0x80413104, INVALID_OPTLEN 0x80413105, INVALID_ARG 0x80413106, INVALID_ID 0x80413107, ID_NOT_AVAIL
0x80413108, NO_SPACE 0x80413109, IS_RUNNING 0x8041310A, NOT_RUNNING 0x8041310B, UNKNOWN_TARGET 0x8041310C,
TARGET_NOT_READY 0x8041310D, EXCEED_MAXNUM 0x8041310E, REQUEST_IN_PROGRESS 0x8041310F, ALREADY_ESTABLISHED 0x80413110,
BUSY 0x80413111, ALREADY_INITIALIZED 0x80413112, NOT_INITIALIZED 0x80413113, PORT_IN_USE 0x80413114,
STACKSIZE_TOO_SHORT 0x80413115, INVALID_DATALEN 0x80413116, NOT_ESTABLISHED 0x80413117, DATA_BUSY 0x80413118,
INVALID_ALIGNMENT 0x80413119.

Modos: PARENT 1, CHILD 2, P2P 3. Eventos do handler (valores do PSP, **a confirmar** no Vita — ausentes do header):
HELLO 1, REQUEST 2, LEAVE 3, DENY 4, CANCEL 5, ACCEPT 6, ESTABLISHED 7, TIMEOUT 8, ERROR 9, BYE 10, DATA 11,
DATA_ACK 12, DATA_TIMEOUT 13. Handler: `void (*)(int id, int type, SceNetInAddr *addr, SceSize opt_len, void *opt)`.

Ordem das validações dentro de cada função: init → ID → estado (running) → argumentos (ordem exata a confirmar; só
importa quando há mais de um erro simultâneo).

Implementação: nova classe `public sealed class NetAdhocMatching : HleModule` em
`Hle/Modules/NetAdhocMatching.cs`. Rede real (resumo): thread de evento criada em `Start` (com prioridade/pilha/
afinidade recebidas) que envia hellos UDP broadcast na `port` a cada `hello_interval`, mantém a tabela de membros
(keep-alive/retry/rexmt) e chama o handler com `CallGuest` nessa thread; o protocolo de pacotes do Vita é a confirmar
por captura entre dois consoles.

### sceNetAdhocMatchingInit
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x8E0E0EAE
- **Protótipo:** `int sceNetAdhocMatchingInit(unsigned int pool_size, void *pool_ptr);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** inicializa a biblioteca usando o pool de memória do jogo (`pool_ptr`, `pool_size` bytes).
  Offline: guarda os valores (o pool não é usado).
- **Retorno/erros:** 0; já inicializada → ALREADY_INITIALIZED 0x80413112; `pool_size == 0` ou `pool_ptr` nulo →
  INVALID_ARG 0x80413106 (a confirmar; o PSP aloca o pool internamente e só valida o tamanho; tamanho mínimo a
  confirmar → NO_SPACE 0x80413109); `pool_ptr` desalinhado → INVALID_ALIGNMENT 0x80413119 (alinhamento a confirmar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `NetAdhocMatching`.
- **Validação:** `VitaSharp.Desktop imports PCSG00198` sem SceNetAdhocMatching na lista de faltantes; Init duplo →
  0x80413112.

### sceNetAdhocMatchingTerm
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x6E195CD1
- **Protótipo:** `int sceNetAdhocMatchingTerm(void);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** para e apaga todos os contextos e encerra a biblioteca.
- **Retorno/erros:** 0; não inicializada → NOT_INITIALIZED 0x80413113 (a confirmar; o PSP retorna 0 nesse caso —
  recomendação: 0, mais seguro).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** limpar `Ctx`, `Inited = false`.
- **Validação:** Init→Term→Init funciona.

### sceNetAdhocMatchingCreate
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x7BAD7EA2
- **Protótipo:** `int sceNetAdhocMatchingCreate(SceNetAdhocMatchingMode mode, int max_members, SceUShort16 port, int rx_buffer_len, unsigned int hello_interval, unsigned int keep_alive_interval, int retry_count, unsigned int rexmt_interval, SceNetAdhocMatchingCallback handler_callback);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** cria um contexto de matching (não inicia). Intervalos em µs. Devolve o ID (> 0).
- **Retorno/erros:** ID; NOT_INITIALIZED 0x80413113; `mode` ∉ {1,2,3} → INVALID_MODE 0x80413101; `max_members` fora
  de 2..16 → INVALID_MAXNUM 0x80413103 (faixa do PSP; em CHILD/P2P o PSP ignora/fixa; a confirmar); `port == 0` →
  INVALID_PORT 0x80413102 (a confirmar); `rx_buffer_len <= 0` → RXBUF_TOO_SHORT 0x80413104 (mínimo a confirmar);
  outro contexto já usa a mesma `port` → PORT_IN_USE 0x80413114; sem IDs livres → ID_NOT_AVAIL 0x80413108 (limite a
  confirmar); `handler_callback` nulo → aceitar (PSP aceita; o jogo só não recebe eventos).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** 9 argumentos: r0–r3 = mode, max_members, port, rx_buffer_len; pilha +0 =
  hello_interval, +4 keep_alive_interval, +8 retry_count, +0xC rexmt_interval, +0x10 handler (o binder resolve; usar
  `ushort port`/`uint`).
- **Validação:** Create com mesma porta duas vezes → 0x80413114; mode 0 → 0x80413101.

### sceNetAdhocMatchingDelete
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x7BCDD889
- **Protótipo:** `int sceNetAdhocMatchingDelete(int id);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** apaga o contexto.
- **Retorno/erros:** 0; NOT_INITIALIZED; ID inexistente → INVALID_ID 0x80413107; contexto em execução → IS_RUNNING
  0x8041310A (a confirmar; o PSP para e apaga — recomendação: parar e apagar, retornando 0, para não deixar o jogo
  preso se ele esquecer o Stop).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** remover de `Ctx` (libera a porta).
- **Validação:** Delete duplo → 0x80413107.

### sceNetAdhocMatchingStart
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceNetAdhocMatching · **NID:** 0xF13E17BE
- **Protótipo:** `int sceNetAdhocMatchingStart(int id, int thread_priority, int thread_stack_size, int thread_cpu_affinity_mask, int hello_opt_len, void *hello_opt);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** inicia o contexto: cria a thread de eventos e começa a anunciar (PARENT/P2P enviam hello com
  `hello_opt`; CHILD escuta). Offline: `Running = true`, copia `hello_opt` para `HelloOpt`; **não** cria thread e não
  entrega eventos.
- **Retorno/erros:** 0; NOT_INITIALIZED; INVALID_ID; já em execução → IS_RUNNING 0x8041310A; `hello_opt_len < 0` ou
  acima do máximo → INVALID_OPTLEN 0x80413105 (máximo a confirmar); `hello_opt_len > 0` com `hello_opt` nulo →
  INVALID_ARG 0x80413106; `thread_stack_size` abaixo do mínimo → STACKSIZE_TOO_SHORT 0x80413115 (mínimo a confirmar;
  não rejeitar até confirmar). Em modo CHILD `hello_opt` é ignorado.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** 6 argumentos (r0–r3 + 2 na pilha).
- **Validação:** Start duplo → 0x8041310A; Sakatsuku entra na tela de procura de jogadores e permite cancelar.

### sceNetAdhocMatchingStop
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x63ABB632
- **Protótipo:** `int sceNetAdhocMatchingStop(int id);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** para o contexto (envia BYE aos membros e encerra a thread no hardware). Offline: `Running = false`.
- **Retorno/erros:** 0; NOT_INITIALIZED; INVALID_ID; não em execução → NOT_RUNNING 0x8041310B (a confirmar; o PSP
  retorna 0 — recomendação: 0).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** `NetAdhocMatching`.
- **Validação:** Start→Stop→Start funciona.

### sceNetAdhocMatchingSelectTarget
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x9A6B1D0F
- **Protótipo:** `int sceNetAdhocMatchingSelectTarget(int id, SceNetInAddr *target, int opt_len, void *opt);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** CHILD: pede para entrar no grupo de `target`; PARENT: aceita o pedido de `target`; P2P: idem
  entre pares. Offline nenhum par é conhecido.
- **Retorno/erros:** offline **UNKNOWN_TARGET 0x8041310C**; NOT_INITIALIZED; INVALID_ID; não em execução →
  NOT_RUNNING 0x8041310B; `target` nulo → INVALID_ARG 0x80413106; `opt_len < 0` ou `opt_len > 0` com `opt` nulo →
  INVALID_OPTLEN 0x80413105 / INVALID_ARG. Em rede real: REQUEST_IN_PROGRESS, ALREADY_ESTABLISHED, EXCEED_MAXNUM,
  TARGET_NOT_READY.
- **Estruturas:** `SceNetInAddr` (4): `unsigned int s_addr` (ordem de rede).
- **Implementação no VitaSharp:** `NetAdhocMatching`.
- **Validação:** chamada com qualquer endereço → 0x8041310C.

### sceNetAdhocMatchingCancelTarget
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x04FF010C
- **Protótipo:** `int sceNetAdhocMatchingCancelTarget(int id, SceNetInAddr *target);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** cancela pedido/conexão com `target` (envia CANCEL/LEAVE no hardware).
- **Retorno/erros:** offline UNKNOWN_TARGET 0x8041310C; NOT_INITIALIZED; INVALID_ID; NOT_RUNNING; `target` nulo →
  INVALID_ARG.
- **Estruturas:** `SceNetInAddr`.
- **Implementação no VitaSharp:** `=> CancelTargetWithOpt(id, target, 0, 0)`.
- **Validação:** → 0x8041310C.

### sceNetAdhocMatchingCancelTargetWithOpt
- [x] **Prioridade:** P1 · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x54F6534D
- **Protótipo:** `int sceNetAdhocMatchingCancelTargetWithOpt(int id, SceNetInAddr *target, int opt_len, char *opt);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** como `CancelTarget`, enviando dados opcionais com o cancelamento.
- **Retorno/erros:** idem + INVALID_OPTLEN 0x80413105 para `opt_len` inválido.
- **Estruturas:** `SceNetInAddr`.
- **Implementação no VitaSharp:** rotina compartilhada.
- **Validação:** → 0x8041310C.

### sceNetAdhocMatchingSendData
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x83C0E435
- **Protótipo:** `int sceNetAdhocMatchingSendData(int id, SceNetInAddr *addr, int data_len, void *data);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** envia dados a um membro estabelecido (confirmação vem como evento DATA_ACK/DATA_TIMEOUT).
- **Retorno/erros:** offline UNKNOWN_TARGET 0x8041310C (nenhum membro); NOT_INITIALIZED; INVALID_ID; NOT_RUNNING;
  `addr`/`data` nulos → INVALID_ARG; `data_len <= 0` ou acima do máximo → INVALID_DATALEN 0x80413116 (máximo a
  confirmar). Rede real: NOT_ESTABLISHED, DATA_BUSY.
- **Estruturas:** `SceNetInAddr`.
- **Implementação no VitaSharp:** `NetAdhocMatching`.
- **Validação:** → 0x8041310C.

### sceNetAdhocMatchingAbortSendData
- [x] **Prioridade:** P0 (Sakatsuku 2013, PCSG00198) · **Biblioteca:** SceNetAdhocMatching · **NID:** 0xFE77831E
- **Protótipo:** `int sceNetAdhocMatchingAbortSendData(int id, SceNetInAddr *addr);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** cancela um envio pendente para `addr`.
- **Retorno/erros:** offline UNKNOWN_TARGET 0x8041310C; NOT_INITIALIZED; INVALID_ID; NOT_RUNNING; `addr` nulo →
  INVALID_ARG. (Sem envio pendente para um membro conhecido o PSP retorna 0.)
- **Estruturas:** `SceNetInAddr`.
- **Implementação no VitaSharp:** `NetAdhocMatching`.
- **Validação:** → 0x8041310C.

### sceNetAdhocMatchingSetHelloOpt
- [x] **Prioridade:** P1 · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x659D4B04
- **Protótipo:** `int sceNetAdhocMatchingSetHelloOpt(int id, int opt_len, void *opt);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** troca os dados enviados nos hellos (PARENT/P2P).
- **Retorno/erros:** 0; NOT_INITIALIZED; INVALID_ID; modo CHILD → INVALID_MODE 0x80413101; não em execução →
  NOT_RUNNING 0x8041310B (comportamento do PSP; a confirmar); INVALID_OPTLEN / INVALID_ARG como em `Start`.
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** copia para `HelloOpt`.
- **Validação:** Set→Get devolve os mesmos bytes.

### sceNetAdhocMatchingGetHelloOpt
- [x] **Prioridade:** P1 · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x90AFFEDD
- **Protótipo:** `int sceNetAdhocMatchingGetHelloOpt(int id, unsigned int *opt_len, void *opt);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** `*opt_len` entra com o tamanho de `opt` e sai com o tamanho do hello opt atual; `opt == NULL` →
  só tamanho; buffer menor → copia o que couber ou erro (a confirmar; recomendação: copiar `min` e devolver o tamanho
  real).
- **Retorno/erros:** 0; NOT_INITIALIZED; INVALID_ID; `opt_len` nulo → INVALID_ARG; modo CHILD → INVALID_MODE (a
  confirmar).
- **Estruturas:** nenhuma.
- **Implementação no VitaSharp:** lê `HelloOpt`.
- **Validação:** após `Start` com hello de 8 bytes, `*opt_len = 8`.

### sceNetAdhocMatchingGetMembers
- [x] **Prioridade:** P1 · **Biblioteca:** SceNetAdhocMatching · **NID:** 0x85B23CEB
- **Protótipo:** `int sceNetAdhocMatchingGetMembers(int id, unsigned int *members_count, struct SceNetAdhocMatchingMember *members);` (`psp2/net/adhoc_matching.h`)
- **Comportamento:** lista os membros do grupo; `*members_count` entra com a capacidade de `members` e sai com o nº
  escrito (ou total, se `members == NULL`). No PSP a lista inclui o próprio console (e o pai primeiro, no filho).
  Offline: o console não tem endereço ad hoc (`sceNetCtlAdhocGetInAddr` → NOT_CONNECTED), então devolver **0
  membros**; se o Sakatsuku se comportar mal com 0 (esperar a si mesmo na lista), devolver 1 entrada com o endereço
  que `sceNetCtlAdhocGetInAddr` passaria a devolver — a confirmar no jogo.
- **Retorno/erros:** 0; NOT_INITIALIZED; INVALID_ID; NOT_RUNNING 0x8041310B (a confirmar); `members_count` nulo →
  INVALID_ARG.
- **Estruturas:** `SceNetAdhocMatchingMember` (4): `SceNetInAddr addr`.
- **Implementação no VitaSharp:** `NetAdhocMatching`.
- **Validação:** contexto iniciado → `*members_count = 0`, retorno 0.
