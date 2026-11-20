# Aplicativos do computador (Windows, instalados e baixados)

Decisão de 20/11/2026, pedido do dono: "quero que as atendentes tenham acesso aos aplicativos do Windows e aos baixados, e quando entrar a primeira vez mande uma solicitação pedindo para a Central de TI liberar". Segue o mesmo modelo dos [sites nos navegadores](CHROME.md): o Terminal **deixa abrir** e **avisa**; o TI decide na Central, aba **Aplicativos**.

## O que a atendente vê

- Na tela inicial do Terminal (com servidor), o botão **Aplicativos**, ao lado de **Arquivos**, abre uma folha com três grupos: **Do Windows** (Calculadora, Bloco de Notas, Paint, Ferramenta de Captura, WordPad, Mapa de Caracteres, Notas Autoadesivas, os que existirem no computador), **Instalados neste computador** (atalhos `.lnk` do menu Iniciar, de todos os usuários e da conta dela) e **Baixados** (arquivos `.exe` na pasta Downloads).
- Cada aplicativo mostra a situação: *Liberado pelo TI*, *Bloqueado pelo TI* (não dá para clicar) ou *Na primeira vez, o TI recebe um pedido para liberar*.
- Ao abrir um aplicativo novo, ele abre e aparece o aviso "foi aberto. O TI recebeu um pedido para liberar este aplicativo". Ao tentar um bloqueado: "O TI bloqueou X neste computador". Se o TI bloquear um aplicativo que está aberto, o Terminal pede para ele fechar e, se em cinco segundos continuar aberto, encerra.
- O aplicativo aberto entra em **Abertos agora** (voltar e fechar) e no "em uso" que a Central mostra.

## Como funciona

1. O Terminal varre o computador quando a folha é aberta (`AppCatalog`, numa linha fora da interface): atalhos do menu Iniciar resolvidos pela interface `IShellLink` do Windows, lista fixa de aplicativos do Windows em `System32`, e `.exe` da pasta Downloads. Entram só caminhos completos de `.exe` que existem; ficam de fora atalhos de desinstalação e tudo o que `AppPolicy.IsRefused` recusa (ver abaixo).
2. O Terminal abre o aplicativo direto (`CreateProcess`, sem passar pelo Explorador), por isso a lista `RestrictRun` do bloqueio da conta não o impede. Isso é de propósito: o controle passa a ser a decisão do TI.
3. A decisão é por **nome do arquivo** (`spotify.exe`, em minúsculas), porque o caminho muda de um computador para outro (`C:\Users\<conta>\...`). O veredito (`AppPolicy.Decide`):
   - **Bloqueado** (`appsBlocked` da configuração): não abre; a tentativa vai para a Central.
   - **Liberado**: programa cadastrado para a máquina (mesmo caminho) ou nome em `appsAllowed`: abre e gera só o evento `opened` comum.
   - **Pedido**: tudo o mais abre e gera o evento `app_opened` (`item` = nome, `detail` = caminho).
4. Na Central, `app_opened` vira um pedido **dela** (tabela `DeviceApps`, um por computador e `.exe`), com alerta urgente ("Abriu o aplicativo spotify.exe, que espera a decisão do TI") e notificação ("Gabi (PC-01) abriu o aplicativo Spotify (spotify.exe) pela primeira vez. Decida se ela pode usar."). Abrir de novo soma **Vezes**, sem segundo alerta.
5. O TI escolhe **Vale para** ("Só <atendente>" ou "Todas as atendentes") e **Liberar** ou **Bloquear**. Decidir para todas (tabela `AppDecisions`) apaga os pedidos esperando daquele `.exe` em todas as atendentes e resolve os alertas. A decisão de uma atendente vale mais que a de todas; em **Já decididos**, decisões "Só <atendente>" têm o botão **Tirar** (`decision: "reset"`).
6. Tentativa num aplicativo **bloqueado para todas** (sem decisão só para ela) também vira pedido dela, com o selo "Bloqueado para todas: ela pediu"; o aplicativo continua bloqueado até o TI decidir. Bloqueado **só para ela**, a tentativa fica só no histórico.
7. Cada Terminal recebe em `GET /device/config` as listas `appsAllowed` e `appsBlocked` já calculadas para o computador dele (`AppEndpoints.DecidedAsync`). A configuração muda de versão a cada decisão, então o Terminal aplica em até 15 segundos.

## Rotas

| Rota | Permissão | Corpo / resposta |
|---|---|---|
| `GET /device-apps` | `apps.read` | `[{ exe, name, path, status, scope, deviceId, deviceLabel, opens, firstAt, lastAt, decidedBy, decidedAt, blockedForAll }]`; os `pending` primeiro |
| `POST /device-apps/decide` | `apps.executables.approve` | `{ exe, decision: "allow" \| "block" \| "reset", deviceId?, name? }`. `exe` aceita o nome ou um caminho; `reset` só com `deviceId` |

Liberar `cmd.exe`, `powershell.exe` e os demais recusados do contrato dá 400; bloqueá-los pode (é só reforço).

## O que nunca abre pela lista

Além dos recusados do contrato (`cmd`, `powershell`, `pwsh`, `wscript`, `cscript`, `mshta`, `regedit`, `rundll32`, `msiexec`, `bash`, `wsl`, `wt`), o Terminal não lista nem abre ferramentas de administração do Windows (`taskmgr`, `control`, `mmc`, `msconfig`, `gpedit`, `services`, `eventvwr`, `netsh`, `reg`, `sc`, `schtasks`, `certutil`, `bitsadmin`, `diskpart`, `wmic`, `dism`, `bcdedit`, `icacls`, `runas`, `psexec`, `net`, `shutdown`, `explorer`, `sethc`, `utilman`, entre outros), nem interpretadores e ferramentas de desenvolvedor (`git-bash`, `python`, `node`, `java`, `ssh`, `curl`…; lista completa em `AppPolicy.RefusedNames`), nem o próprio Terminal. Atalhos do menu Iniciar cujo destino fica na pasta do Windows (Ferramentas Administrativas, Sistema do Windows, Acessibilidade) também ficam de fora; da pasta do Windows só entram os aplicativos da lista fixa. Mesmo liberados pelo TI, continuam recusados. Se o TI precisar de um deles numa máquina, cadastra como programa em **Sites e programas**.

## Limites conhecidos

- Aplicativos da Microsoft Store que não têm `.exe` clássico nem atalho `.lnk` não aparecem. Calculadora, Bloco de Notas (Windows 11), Paint, Ferramenta de Captura e Notas Autoadesivas aparecem porque o Windows mantém um lançador em `System32`. Esse lançador **não abre nada na conta bloqueada** (conferido em 20/11/2026 nos PCs da Kamila e da Ana, Windows 10 22H2: "Abriu Calculadora" e "Fechou Calculadora" um segundo depois). Por isso, para esses cinco, o Terminal pede a abertura direto ao gerenciador de ativação do Windows pelo identificador do aplicativo (`StoreApps`, `IApplicationActivationManager`) e recebe o processo, que entra em **Abertos agora**; se o aplicativo da Loja não existir na conta (Bloco de Notas clássico no Windows 10), volta ao `.exe` comum.
- Um aplicativo **cadastrado como programa** em Sites e programas conta como liberado: abre sem pedido e não aparece na aba Aplicativos. Em 20/11/2026 a "calculadora" e o "Bloco de notas" já estavam cadastrados, por isso o teste com a Calculadora não gerou pedido. Para ver o pedido, teste com um aplicativo que não esteja cadastrado (Paint, por exemplo), ou exclua o cadastro.
- Um `.exe` baixado que seja instalador roda como a conta da atendente: instala só o que não pede administrador.
- A decisão por nome de arquivo não distingue dois programas diferentes com o mesmo `.exe`; o caminho fica visível no pedido (passe o mouse sobre o arquivo) para o TI conferir.
- O que a atendente abre por fora da lista (por exemplo, pelo Explorador de Arquivos) continua sob a `RestrictRun` do Windows: só os programas cadastrados abrem por lá.
