# Plugins (de kernel `.skprx` e de usuário `.suprx`)

*English: [plugins.md](plugins.md).*

O VitaSharp roda os plugins que um Vita com homebrew roda, pela mesma configuração de plugins e com o código original
deles: nada no emulador é escrito para um plugin específico. Alguns homebrews precisam de um (os ports de Android feitos
com o so_loader, como o Dead Space, precisam do **kubridge**).

## Como configurar

Copie os plugins do seu próprio Vita para a pasta de dados (`ur0:tai` é `fs/ur0/tai` nela) e liste-os em
`fs/ur0/tai/config.txt` (ou `fs/ux0/tai/config.txt`, que vale quando os dois existem), o arquivo que o taiHEN lê no
Vita:

```
# plugins de kernel: carregados no kernel e iniciados quando um programa inicia
*KERNEL
ur0:tai/kubridge.skprx
ur0:tai/fd_fix.skprx
# plugins de usuário para todo programa
*ALL
# plugins de usuário para um título
*PCSE00000
ux0:tai/algum_plugin.suprx
```

Sem `config.txt` nenhum plugin é carregado, e um programa que precisa de um falha como num Vita sem ele. O próprio
`taihen.skprx` não precisa estar na lista (veja abaixo).

## O que acontece

- Os módulos de `*KERNEL` são carregados na região do kernel (uma área que todo processo mapeia, do processo do kernel)
  e o `module_start` deles roda na hora numa thread de kernel; um módulo cujo início não informa sucesso é descarregado
  de novo, como faz o gerenciador de módulos do firmware. O log diz o que aconteceu com cada um
  (`kernel plugin ...: loaded and started`).
- Os módulos de `*ALL` e `*TITLEID` são carregados no processo do programa e iniciam antes da thread principal.
- Um módulo que importa uma biblioteca que ninguém fornece (nenhum módulo carregado e nenhuma HLE) não é iniciado; o log
  diz quais bibliotecas.
- Os módulos de kernel chamam as bibliotecas de kernel do firmware (SceSysmemForDriver, SceThreadmgrForDriver,
  SceModulemgrForKernel, SceCpuForDriver...): o VitaSharp as implementa com o comportamento e os códigos de erro do
  firmware. Programas de usuário só chegam a um módulo de kernel pelas bibliotecas exportadas como syscall (a biblioteca
  `kubridge` do kubridge, por exemplo); módulos de kernel nunca se ligam a código de usuário.
- A CPU emulada não tem níveis de privilégio, então a checagem de permissão das cópias usuário/kernel é feita pela
  biblioteca do kernel: o intervalo do usuário tem de ser memória do próprio chamador, não memória do kernel.

## taiHEN

No Vita, o taiHEN (`taihen.skprx`) é carregado pelo exploit e fornece o carregador de plugins e a API de hooks que os
outros plugins importam (`taihenForKernel`, `taihenModuleUtils`) e que os homebrews chamam (`taihen`, `taihenUnsafe`).
O VitaSharp não roda o `taihen.skprx`: o `module_start` dele só dá certo depois de instalar os próprios patches nas
checagens de assinatura e de DRM do firmware, e o VitaSharp não faz isso funcionar. Por isso o taiHEN é o único módulo
que não é do firmware e continua implementado pelo emulador (`Hle/Modules/Taihen.cs`, `Taihen.Kernel.cs`), com a
semântica e os códigos de erro do taiHEN; listado no `config.txt`, o módulo real carrega, o início falha e ele é
descarregado.

## Limites

- Os módulos de kernel vivem com o processo do programa para o qual foram carregados (o kernel ainda não é um processo
  único do sistema); são descarregados quando esse processo termina.
- Hooks em funções do firmware que o VitaSharp implementa em C# pegam as chamadas feitas pelos stubs de importação; as
  chamadas internas do próprio emulador não passam pelo hook. Um plugin que corrige o código do firmware por
  deslocamento (as chamadas de proteção de memória do kubridge, `kuKernelMemProtect` / `MemReserve` / `MemCommit`, o
  hook de VFS do fd_fix) não acha código do firmware ali: o hook do fd_fix não é instalado, e essas chamadas do kubridge
  param o programa (pulam para endereços do firmware que o plugin não achou), como num firmware cujos deslocamentos o
  plugin não conhece. O Dead Space não as usa.
- Exceções não são entregues a handlers de kernel (a CPU emulada relata uma falha do guest como queda); o mascaramento
  de interrupções é só estado (não há controlador de interrupções).
