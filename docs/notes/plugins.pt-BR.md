# Plugins (de kernel `.skprx` e de usuário `.suprx`)

*English: [plugins.md](plugins.md).*

Os plugins são uma opção do emulador, **Ativar Plugins do Kernel**, desligada por padrão (o Vita não tem essa
configuração, como pausar ou gravar a tela). Desligada, roda o firmware puro e nenhum plugin carrega. Ligada, o
VitaSharp carrega os plugins que um Vita com homebrew carrega, pela mesma configuração de plugins, roda o código
original deles e oferece a eles as bibliotecas do framework de plugins que eles importam. Nada no emulador é escrito
para um plugin ou homebrew específico.

Ligue em Configurações > Avançado (Android: Configurações > Depuração), com `--kernel-plugins=on` na linha de comando
ou `VITASHARP_KERNEL_PLUGINS=1`; a chave do launcher e a do app valem sobre a variável.

## Como configurar

Copie os plugins do seu próprio Vita para a pasta de dados (`ur0:tai` é `fs/ur0/tai` nela) e liste-os em
`fs/ur0/tai/config.txt` (ou `fs/ux0/tai/config.txt`, que vale quando os dois existem), o arquivo que o ambiente homebrew lê no
Vita:

```
# plugins de kernel: carregados no kernel e iniciados quando um programa inicia
*KERNEL
ur0:tai/algum_plugin_de_kernel.skprx
# plugins de usuário para todo programa
*ALL
# plugins de usuário para um título
*PCSE00000
ux0:tai/algum_plugin.suprx
```

Sem `config.txt`, ou com a opção desligada, nenhum plugin é carregado.

A configuração fica na pasta do próprio emulador, `ur0:kernel_plugins/` ao lado de `ur0:tai` (no computador
`fs/ur0/kernel_plugins/`; Windows, Linux, macOS e Android igual): `ur0:kernel_plugins/config.txt`, lido com as mesmas
regras. As linhas dele são caminhos do Vita (`ur0:tai/kubridge.skprx`): os plugins ficam em `ur0:tai` / `ux0:tai`, onde
os homebrews conferem os arquivos; uma linha sem dispositivo (`meu.skprx`) é um arquivo de `ur0:kernel_plugins/`
(caminho relativo à configuração); um arquivo que não está lá é pulado com um aviso no log. Sem o
`ur0:kernel_plugins/config.txt` é lida a configuração do Vita acima. Só o carregador do emulador lê a configuração. Na
primeira vez com a opção ligada e sem configuração na pasta, o `config.txt` do Vita vai para `ur0:kernel_plugins/` sem
mudanças (uma cópia fica como `ur0:kernel_plugins/config.txt.bak`; o log avisa); um plugin que uma versão anterior tinha
movido para a pasta volta para o caminho do Vita que o `.bak` indica. A pasta das versões anteriores, `kernel_plugins/`
ao lado de `fs/` na pasta de dados, vai uma vez para `ur0:kernel_plugins/` (cada item que ainda não está lá; a pasta
antiga vazia é apagada). Os plugins, o arquivo do próprio framework e os arquivos de dados dos plugins nunca são
movidos. Nada mais é apagado.

O arquivo é lido com as regras do parser e do carregador de plugins do próprio ambiente homebrew: só
um arquivo vale (`ux0:tai/config.txt`, senão `ur0:tai/config.txt`); `#` começa um comentário, `*` uma seção, e
qualquer outra linha é o caminho de um módulo (sem os espaços do começo e do fim); os nomes de seção são comparados
exatos, com maiúsculas e minúsculas (`*main` é o shell, os outros nomes são IDs de título); `*ALL` vale para todo
programa, mas não para `*KERNEL`; uma seção escrita `*!NOME` que bate termina a leitura na próxima seção; uma linha de
256 caracteres ou mais, um caminho antes da primeira seção ou uma seção sem nome faz o carregador de plugins recusar o arquivo
inteiro, e aí nenhum plugin carrega. Os módulos de uma seção carregam na ordem do arquivo, e um que falha não impede
os outros.

## O que acontece

- Os módulos de `*KERNEL` são carregados na região do kernel (uma área que todo processo mapeia, do processo do kernel)
  e o `module_start` deles roda na hora numa thread de kernel; um módulo cujo início não informa sucesso é descarregado
  de novo, como faz o gerenciador de módulos do firmware. O log diz o que aconteceu com cada um
  (`kernel plugin ...: loaded and started`, `... module_start of X returned 0x2, unloaded`).
- Os módulos de `*KERNEL` carregam uma vez, no processo do kernel: o kernel único do sistema, criado no boot (com o
  shell, ou com o primeiro jogo quando não há shell), como o carregador de plugins os carrega no boot do Vita. Eles
  carregam no boot do sistema, antes de começar o lançamento do primeiro programa e qualquer que seja o título, então
  um lançamento que depois falha (um app0 que não monta, um título PSM sem o runtime) já os tem carregados; o log diz
  `kernel plugins: N of M loaded and started at system boot, before any application is launched` e as linhas de plugin
  das gravações terminam com `system boot` (um módulo de usuário com `with process TITLEID`). Todo
  processo os vê: o shell, um app que o shell abre ao lado dele, o programa que um LoadExec inicia. Ficam até o sistema
  terminar; um módulo de kernel que um programa carrega (o gerenciador de módulos do kernel) também é do kernel e
  continua depois do programa.
- Os handlers de eventos de processo do código de kernel (`ksceKernelRegisterProcEventHandler`) são chamados como o
  gerenciador de processos do firmware chama, na ordem de registro: create quando a thread principal do processo existe
  e antes de ele iniciar (um create que falha recusa o processo, e os handlers chamados antes dele recebem exit), exit
  quando o processo termina sozinho, kill quando é encerrado (fechado pelo shell, uma queda); stop quando é suspenso
  (tipos 0x200 e 0x800 antes de as threads pararem, 0x1000 depois) e start quando volta (0x20000, depois 0x40000); um
  aplicativo também manda o evento switch 0x11 ao ser suspenso e 0x10 ao voltar.
- Os módulos de `*ALL` e `*TITLEID` são carregados no processo de todo programa, também nos do próprio sistema (o carregador
  de plugins os carrega depois dos módulos pré-carregados do processo), e iniciam
  antes da thread principal. A seção é o ID de título do processo no kernel: `main` para o shell, o NPXS de um app do
  sistema.
- Um módulo que importa uma biblioteca que ninguém fornece (nenhum módulo carregado e nenhuma biblioteca do firmware que
  o VitaSharp implementa) não é iniciado; o log diz quais bibliotecas (`not started: no module provides ...`).
- Os módulos de kernel chamam as bibliotecas de kernel do firmware (SceSysmemForDriver, SceThreadmgrForDriver,
  SceModulemgrForKernel, SceCpuForDriver...): o VitaSharp as implementa com o comportamento e os códigos de erro do
  firmware. Programas de usuário só chegam a um módulo de kernel pelas bibliotecas exportadas como syscall; módulos de
  kernel nunca se ligam a código de usuário.
- Os módulos de kernel do próprio firmware (todos os `os0:kd/*.skprx` e os módulos dentro do `bootimage.skprx`)
  estão na lista de módulos do processo do kernel antes do primeiro outro módulo de kernel carregar, com nome, NID de
  módulo e tabelas de exportação e importação (`ksceKernelGetModuleList`, `ksceKernelGetModuleCB`). As importações de
  um módulo de kernel se ligam às entradas de exportação deles, que são código ARM de verdade (um `svc` para a função
  do HLE): código que um módulo de kernel escreve por cima de uma entrada roda para quem chama por ela no kernel. Os
  programas de usuário continuam chegando às exportações de syscall pelos stubs do HLE. Precisa do firmware instalado
  (`os0` do PSVUPDAT.PUP); sem ele nenhum módulo do firmware aparece.
- A CPU emulada não tem níveis de privilégio, então a checagem de permissão das cópias usuário/kernel é feita pela
  biblioteca do kernel: o intervalo do usuário tem de ser memória do próprio chamador, não memória do kernel.

## As bibliotecas do framework de plugins

No Vita o módulo do framework do ambiente homebrew carrega primeiro e fornece o carregador de plugins e as bibliotecas
que os outros plugins e os homebrews importam. Com a opção ligada, o VitaSharp oferece ele mesmo essas bibliotecas
(declaradas só na tabela de NIDs): informação de módulo por nome ou NID de módulo (o registro de 0x38 bytes, do módulo
mais novo para o mais antigo), deslocamentos nos segmentos de um módulo, as entradas de exportação e importação dele,
injeção de dados (os bytes originais guardados e escritos de volta na liberação) e os códigos de erro do framework
(0x9001xxxx). Um módulo listado que exporta essas bibliotecas (a cópia do framework do usuário) não é carregado: o log
diz `not loaded (the emulator provides its libraries)`. Com a opção desligada as funções não fazem nada, como
importações que ninguém fornece.

Os hooks de função (exportação, importação, deslocamento e endereço absoluto; kernel e usuário) seguem o contrato
publicado do framework: as primeiras instruções da função viram um salto para o primeiro hook; cada hook recebe um
registro de referência (next, func, old) e continua a chamada como a macro de continuação do framework (a função do
próximo hook ou, depois do último, um trampolim que roda as instruções substituídas e o resto da original). Os hooks
de uma função são ligados como o framework liga: o primeiro continua sendo o destino do salto e cada novo entra logo
depois dele (a ordem é primeiro, mais novo, ..., segundo; o framework não define a ordem). Liberar um hook mantém os
outros; liberar o último escreve os bytes originais de volta. Cada patch descarta só o código traduzido que ele cobre,
em todo processo. Um hook funciona igual numa função do guest (LLE) e numa função do firmware servida pelo HLE: um
hook de kernel numa exportação do firmware também toma, em todo processo (também nos criados depois), as stubs de
importação da função daquela biblioteca, então um hook numa exportação de syscall vê as chamadas dos programas de
usuário; a função de mesmo NID da biblioteca de kernel (a gêmea de kernel) é outra função. O hook roda na thread que
chamou, com o processo dela como o atual, como o código de kernel que um syscall roda (ksceKernelGetProcessId dá o
processo de usuário). Um hook ou injeção de um plugin de kernel num processo de usuário é feito na memória desse
processo, e os patches de um processo saem quando ele termina.

### O hook é usado? (o log)

Todo hook diz no log se as chamadas chegam nele, para qualquer plugin e qualquer tipo de hook (exportação, importação,
deslocamento, absoluto; kernel e usuário; em código do guest e numa função servida pelo HLE, inclusive as chamadas dos
programas de usuário por uma exportação de syscall):

- ao instalar: `plugin hook N on 0xADDR (<função> of library 0xLIB; export hook, kernel, 1 on the address): hook
  function 0x... (pid 0x..., by <módulo>)`; um hook que o framework recusa: `plugin hook N refused (0x9001xxxx): ...`;
- a primeira chamada que entra nele: `plugin hook N called: <função> of library 0xLIB from pid 0x... (thread <nome>) ->
  hook function 0x...`;
- a primeira vez que ele continua a chamada: `plugin hook N continued to the original: ...` (o old do registro) ou
  `plugin hook N continued to the next hook (plugin hook M): ...` (a função do próximo hook);
- no fim da sessão, depois das linhas PERF (no log do desktop e no do app Android): `Plugin hooks: I installed,
  C called, U never called, R refused` e uma linha por hook com o módulo que o instalou, as chamadas (e quem chamou
  primeiro), as continuações para a original e para o próximo hook, `never called` para um hook em que nenhuma chamada
  entrou, `released` para um que o plugin liberou.

N numera os hooks da sessão na ordem de instalação. A contagem é código do guest: o func e o old do registro são uma
pequena stub ARM do hook (na memória do kernel) que soma um no contador do hook (ldrex/strex, todos os núcleos) e segue
para a função do hook ou para o trampolim, mantendo os registradores e a pilha de quem chamou; por isso vale em todo
backend da CPU (interpretador, JIT IL e nativo, código pré-compilado) e em todo caminho até o hook. Só a chamada que
leva um contador de 0 a 1, e as chamadas rastreadas abaixo, entram no C# para as linhas do log; depois disso uma chamada custa poucas instruções e não aloca
nada. Um save state guarda os contadores (são memória do guest); um estado salvo por uma versão antiga ganha as stubs ao
carregar.

O que um hook faz, nas primeiras chamadas (8 por padrão; `VITASHARP_PLUGIN_HOOK_TRACE=N` define quantas, 0 nenhuma,
no máximo 256): uma linha por chamada, `plugin hook N call K: r0=... r1=... r2=... r3=... stack=...; continued to the
original, which returned 0x...; returned 0x... to the caller (changed); wrote r1 0x... bytes 0-3 (now ...)`. Ela diz se
o hook continuou (para a original ou para o próximo hook, com o valor que voltou) ou respondeu sozinho
(`answered itself`), o que quem chamou recebeu (`(changed)` quando não é o que a original ou o próximo hook devolveu) e
quais dos 32 bytes apontados por cada argumento mudaram durante a chamada. A stub guarda o lr de quem chamou numa
palavra própria e entrega ao hook a palavra de retorno da stub, que o devolve: os registradores e a pilha do guest
ficam como estavam, e uma chamada em andamento sobrevive a um save state. O resumo acrescenta, por hook, as chamadas que
responderam sozinhas e, nas chamadas rastreadas, os valores devolvidos (os 5 mais comuns), quantos foram alterados e
quantas escreveram numa saída. Depois das chamadas rastreadas, um hook custa só os contadores.

Uma gravação (F8) também guarda os hooks: o context.txt lista cada hook com os contadores no início e no fim (uma linha
`plugin hook:` separada por tabulações para cada um), o log.txt tem as linhas `called` e o report.txt tem a seção
"Plugin hooks" com o tipo, a função, o processo, o endereço, quem instalou, a função do hook, as chamadas e as
continuações durante a gravação e na sessão inteira, e `never called` ou o erro de um hook recusado. A análise lê só a
gravação, então quem a examina não precisa nem do jogo nem dos plugins. Ela também tem a seção "Plugins": o build
do emulador, o firmware instalado, a configuração de plugins lida (o caminho do Vita e as linhas) e cada módulo de
plugin dela (caminho do Vita, kernel ou usuário, nome do módulo, SHA-256 e tamanho do arquivo, o que aconteceu com
ele, as funções importadas que nada fornece). Só entram caminhos do Vita; um caminho do PC numa mensagem vira
`<data>` ou `%USERPROFILE%`, e nenhum arquivo de plugin é copiado.

As chamadas de módulo do framework para programas de usuário (taiLoadKernelModule, taiLoadStartKernelModuleForUser,
taiStartKernelModuleForUser, taiLoadStartModuleForPidForUser, as quatro chamadas de stop, taiUnloadKernelModule,
taiUnloadModuleForPid, taiMemcpyUserToKernel e taiMemcpyKernelToUser) seguem o contrato publicado dele: só um programa
com a classe de sistema pode chamá-las (ksceSblACMgrIsShell: bit 1 de capability, que o homebrew gerado como fake SELF
unsafe tem e o homebrew safe e os jogos de varejo não têm; senão 0x90010009); os argumentos vêm no registro de 0x14
bytes (size, pid, args, argp, flags), até 256 bytes de argp são copiados para um buffer de kernel que o
module_start/module_stop recebe, o caminho tem no máximo 255 caracteres e opt tem que ser NULL, com os códigos de erro
do framework para cada checagem. O módulo carrega pelo gerenciador de módulos de kernel do firmware, o carregador dos
plugins `*KERNEL`: um módulo de kernel vai para o kernel e sobrevive ao programa que o carregou; as chamadas de stop
escrevem o status do module_stop em `*res`. O UID devolvido é o do próprio módulo (o VitaSharp tem um espaço de UID só
para o kernel e os processos). É assim que um homebrew carrega o próprio módulo de kernel (o eboot do Adrenaline faz
isso primeiro). taiReloadConfig, taiReloadConfigForKernel e taiLoadPluginsForTitleForKernel ainda não são oferecidas.

Os módulos de usuário do firmware que o HLE serve (os módulos de os0:us que todo processo tem pré-carregados, como o
SceLibKernel, e os módulos de vs0 que um processo carrega) estão na lista de módulos do processo como o framework a vê:
na primeira vez que o framework olha os módulos de um processo, cada módulo de usuário do firmware que exporta uma
biblioteca que o processo importa, e que nenhum módulo carregado do processo exporta, entra na lista com o nome, o NID
de módulo e as tabelas de exportação do módulo do firmware, num endereço fixo da memória do processo, e a entrada de
cada função exportada é um stub do HLE. Eles vêm antes dos módulos carregados (foram pré-carregados), então o módulo
mais novo continua sendo o do programa. Um hook de exportação de um plugin de usuário num deles corrige a entrada e
toma as stubs de importação da função naquele processo (também as ligadas depois); os outros processos não são
afetados, como no Vita o código de um módulo de usuário é do processo dele. Esses módulos não são ligados: as
importações continuam se ligando ao HLE como antes. Precisam do firmware instalado (os0 e vs0 do PSVUPDAT.PUP); sem ele
a busca não acha esse módulo (0x90010002), como antes.

O framework não guarda lista de patches por módulo: um plugin libera os próprios hooks e injeções, normalmente no
module_stop dele, em qualquer ordem, enquanto o código corrigido ainda está no lugar; um plugin que para e descarrega
sem liberar deixa tudo instalado (como faz o fonte publicado do framework; um hook cujo código saiu com o módulo passa a
saltar para memória liberada, como no Vita). Só quando um processo termina os patches dele saem, sem escrever os bytes
originais de volta (o código saiu com o processo); os patches de kernel ficam até serem liberados.

| Mecanismo | Situação |
| --- | --- |
| Hook de exportação, kernel (módulos de kernel, inclusive os do firmware) | Feito |
| Hook de exportação, usuário, num módulo de usuário carregado | Feito |
| Hook de exportação, usuário, num módulo de usuário do firmware servido pelo HLE | Feito (na lista de módulos do processo; precisa do firmware) |
| Hook de importação (kernel e usuário) | Feito |
| Hooks por deslocamento e por endereço absoluto (kernel e usuário, ARM e Thumb) | Feito |
| Cadeia de hooks com continuação (next/func/old), na ordem do framework | Feito |
| Liberação no meio, na cabeça, do último | Feito |
| Injeção de dados (absoluta e por deslocamento no segmento), restaurada na liberação | Feito |
| Hook de kernel numa exportação de syscall do firmware: as chamadas de todo processo, também dos criados depois | Feito |
| Um processo termina: os patches dele saem, nada é escrito de volta | Feito |
| Um plugin para e descarrega: o module_stop dele libera os patches; nada é liberado por ele | Feito (como o framework) |
| Código traduzido: cada patch descarta os blocos que cobre; um bloco decodificado durante um patch é conferido de novo | Feito |
| Chamadas de módulo para programas de usuário (carregar, iniciar, parar, descarregar módulo de kernel ou de processo; cópias usuário/kernel) | Feito |
| Corrigir o código do próprio firmware por deslocamento | Impossível (não há código do firmware: o HLE o serve) |
| Os chamadores internos de uma função do firmware (o HLE chamando a própria função) passam pelo hook na entrada | Sim: toda chamada interna a uma função exportada passa pelo wrapper gerado `XxxThroughEntry` (ver abaixo) |

### Matriz de validação

Cada célula é um teste que aquece o código do chamador no caminho, instala o hook, confere que o hook roda com os
argumentos do chamador, que a continuação chega ao original e que o retorno do hook chega ao chamador, libera e confere
que os bytes e o resultado originais voltaram (`PluginHookMatrixTests`, `PluginHookMatrixProcessTests`); a ordem da
cadeia é testada por fuzz contra um modelo do framework (`PluginHookChainFuzzTests`, sementes fixas, 300 passos no
interpretador, 40 com código compilado). Caminhos: o interpretador, o backend IL, o backend nativo do host (x64 no x64;
ARM64 em host ARM64, os runners do macOS e o Android, o mesmo núcleo) e o código traduzido antes (a pré-compilação do
cache de código: traduzido antes de rodar, depois com hook).

| Hook | Interpretador | IL | Nativo | Pré-compilado |
| --- | --- | --- | --- | --- |
| Exportação de kernel, A32 e Thumb | ok | ok | ok | ok |
| Importação de kernel (stub) | ok | ok | ok | ok |
| Deslocamento de kernel (Thumb), absoluto (A32) | ok | ok | ok | ok |
| Exportação de kernel numa biblioteca de syscall de um módulo de kernel, chamador de usuário | ok | ok | ok | ok |
| Exportação de kernel numa syscall do firmware servida pelo HLE, chamador de usuário (vê o processo que chamou) | ok | ok | ok | ok |
| Exportação de usuário, A32 e Thumb; importação de usuário; deslocamento de usuário (A32 e Thumb) | ok | ok | ok | ok |
| Exportação de usuário num módulo de usuário do firmware servido pelo HLE (casca) | ok | ok | ok | ok |
| Hooks de kernel vistos por um aplicativo aberto depois deles, nas threads dele | ok | ok | ok | ok |
| Save state com os hooks: carrega com eles ativos; um de antes carrega sem eles | ok | ok | ok | ok |
| Fuzz da cadeia (kernel e usuário, 3 sementes cada) | ok | ok | ok | ok |
| Chamador interno do firmware (a chamada do próprio HLE à função com hook) | ok (`HleEntryCallTests`, `HleEntryCallLibraryTests`, `IoVfsMountTests.AHookOnTheVopLookupEntrySeesTheModulesOwnPathWalks`) | idem | idem | idem |

O sandbox ARM64 (Unicorn) roda blocos traduzidos soltos, não o despachante, então não é um caminho da matriz; a matriz
roda em ARM64 nativo quando o host é ARM64. Carregar estado com `VITASHARP_LOAD_KEEP_CODE=1` (traduções mantidas e
conferidas pelos bytes) é lido uma vez por processo e não é uma célula (o load padrão descarta todas as traduções).

### As chamadas do próprio firmware a uma função com hook

No Vita um hook de exportação corrige o código da função, então todo chamador passa por ele, inclusive as chamadas do
próprio módulo do firmware. As funções do firmware servidas pelo HLE são métodos C#; quando uma chama outra função
exportada, chama o wrapper gerado `XxxThroughEntry` (gerador de código `HleEntryCallGenerator`, feito para todo método
`[Hle]` que algum código chama assim). Sem hook o wrapper lê três campos estáticos (quantos processos do host têm hook de
usuário e se os módulos de kernel do firmware de algum sistema emulado têm entrada corrigida, recontado só quando o código
do guest muda) e chama o método C#, sem alocar nada (`HleEntryCallBenchmarks`, `HleEntryCallTests`). Com hook, acha-o onde o framework o pôs: a entrada
corrigida da função nos módulos de kernel do firmware (hook de um plugin de kernel, ou qualquer código escrito sobre a
entrada) ou o thunk do hook de um plugin de usuário na função, no processo do módulo (achado por biblioteca e NID: um NID que duas
bibliotecas exportam são duas funções, e o hook de uma não é o da outra); o código com hook roda na thread
que chamou com os argumentos como palavras AAPCS (valores de 64 bits num par par de registradores ou numa posição
alinhada da pilha) e o r0/r1 dele é o resultado. Função que o guest não consegue chamar pelo endereço (recebe uma string
C#, um float, o contexto do HLE ou argumentos variáveis) não tem wrapper. O analisador VSHLE010 acusa a chamada direta a
uma função exportada no VitaSharp.Core; as chamadas que não são a chamada do firmware àquela função ficam diretas com um
`#pragma` dizendo o porquê: as formas de syscall por trás dos wrappers de uma biblioteca de usuário (`_sceIo*`,
`_sceKernel*`, ...) e as entradas de kernel que dividem o corpo C# da chamada de usuário de mesmo nome (no Vita a chamada
de usuário chega ao kernel pela syscall, então o hook numa não é visto pela outra).

O início de processo do firmware chama o export ksceKernelStartPreloadingModules(pid) do modulemgr pelo endereço,
quando a thread principal do processo novo já existe (processmgr 3.74). O VitaSharp inicia ele mesmo os módulos
pré-carregados, então a chamada passa pela entrada do export quando um código de kernel o hookou: o hook roda numa
thread de kernel com o pid do processo novo, antes da thread principal do processo, para todo processo aberto depois do
hook. É assim que um módulo de kernel põe o seu módulo num processo (o módulo de kernel do Adrenaline põe o módulo de
usuário dele no processo do emulador de PSP). O processo que o shell inicia por caminho (sceAppMgrLaunchAppByPath4, o
emulador de PSP do firmware vs0:app/NPXS10028) tem como título de processo o título que o launch informa
(ksceKernelGetProcessTitleId, a seção da configuração de plugins): o da bolha que o shell abriu.

### O mount do app0 no lançamento

Uma pasta de aplicativo que ainda é uma imagem PFS é montada como o SceAppMgr 3.74 a monta ao lançar o título
(appmgr descompilado, só estudo): acha o arquivo da licença, lê 0x200 bytes com ksceIoOpen (flags 0x10001), ksceIoRead e
ksceIoClose do kernel, o SceNpDrm confere (ksceNpDrmGetRifInfo, ksceNpDrmGetRifVitaKey; o pfsmgr não importa nada do
SceNpDrm, a chave chega com o kscePfsMount) e o app0 é montado pelo ScePfsMgr com a chave. Quando código de kernel
enganchou uma dessas três funções de E/S, a leitura da licença passa pelas entradas delas numa thread de kernel, e o hook
vê a leitura do lançamento (a continuação chega ao HLE); sem hook o arquivo é lido direto. O passo da chave é do
processador seguro e responde 0x80020004 na unidade emulada, com ou sem hook. Uma falha diz o passo:
`<título>: the launch stopped at app0's mount, step N of 4 (<passo>): ... (0x........)` (1 licença, 2 leitura da
licença, 3 chave do título, 4 mount PFS); a mensagem ao usuário não muda.

### Caminhadas de caminho nos dispositivos do emulador

No Vita o iofilemgr percorre cada caminho elemento por elemento com o ksceVopLookup a partir do vnode raiz do
dispositivo (3.74, 0x81004bb8, só estudo), então um hook de kernel nele (o do fd_fix) vê toda abertura. No VitaSharp
ux0:, app0: e os outros dispositivos são pastas do host; quando um hook pegou o ksceVopLookup, sceIoOpen (a syscall do
usuário e o ksceIoOpen), sceIoDopen e sceIoGetstat percorrem o caminho pela entrada da função sobre os vnodes do host
(`IoFileMgr.VfsHost.cs`): o hook recebe cada elemento e o que ele responde vale (um erro falha a chamada, outro vnode a
leva ao caminho dele, uma abertura que cria continua criando). Sem esse hook nada é percorrido nem alocado
(`IoVopLookupHookTests`). As formas assíncronas (sceIoOpenAsync, sceIoDopenAsync, sceIoGetstatAsync e os outros pedidos
com caminho) rodam no Vita na thread de kernel do próprio iofilemgr (SceIoScheduler), que percorre o caminho do mesmo
jeito: com esse hook o escalonador de E/S roda o pedido com caminho numa thread de kernel com esse nome, no contexto do
processo que o enviou, então o hook é chamado para ele e a resposta dele é o resultado do pedido; sem hook o pedido
continua na thread do host do escalonador.

### A criação do processo no lançamento

O SceAppMgr 3.74 cria o processo de um título com ksceKernelCreateProcess(ID do título, tipo 0x1000000 para aplicativo
ou 0x2000000 para aplicativo do sistema, caminho do executável, opção de 0x40 bytes) antes de montar o app0, e não
lança nada quando a chamada falha. Quando código de kernel enganchou essa função (SceProcessmgrForKernel 0xEB1F8EF7 /
0x68068618 do FW 3.63 em diante; o 0x7A69DE86 / 0x71CF71FD do 3.60 o 3.74 não exporta, então um hook nele é recusado
como no console), todo lançamento (o primeiro título, os do shell, LoadExec) chama a entrada numa thread de kernel: o
hook roda, o original para o qual ele continua responde com o processo lançado, e um erro falha o lançamento com ele
(`KernelPluginBootOrderTests`). Sem hook nada roda.

## Limites

- Um plugin que corrige por deslocamento o código do próprio firmware não acha código do firmware ali (o VitaSharp
  implementa ele mesmo as bibliotecas do firmware).
- Os módulos de usuário do firmware servidos pelo HLE só aparecem nas buscas do framework de plugins (informação de
  módulo, exportações, hooks, deslocamentos); a lista do próprio gerenciador de módulos do processo
  (sceKernelGetModuleList) mostra só os módulos carregados. As variáveis exportadas deles não estão nas tabelas.
- Instruções indefinidas e prefetch aborts (saltos para endereços não mapeados) chegam aos handlers registrados no
  SceExcpmgr, no modo undefined/abort com os registradores banqueados e o SPSR dos modos de exceção do ARM; sem handler,
  ou quando nenhum handler trata, a falha é relatada como queda, como antes. Data aborts ainda não são entregues (um
  acesso a memória não mapeada lê zeros). O mascaramento de interrupções é só estado (não há controlador de
  interrupções).
