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
  shell, ou com o primeiro jogo quando não há shell), como o carregador de plugins os carrega no boot do Vita. Todo
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
| Corrigir o código do próprio firmware por deslocamento | Impossível (não há código do firmware: o HLE o serve) |

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
