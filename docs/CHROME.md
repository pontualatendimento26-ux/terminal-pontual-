# Sites nos navegadores (Chrome, Edge, Opera e Firefox)

Decisão de 20/11/2026: as atendentes usam navegadores com vários perfis (um WhatsApp Web em cada). Começou só com o Google Chrome; no mesmo dia passou a valer também para o Microsoft Edge, o Opera e o Mozilla Firefox, porque algumas atendentes usam outro navegador. O Terminal libera o navegador e **só avisa** a Central do TI quando um site não operacional é aberto. A atendente não vê aviso nenhum. O TI decide na Central, aba **Sites nos navegadores**, se o site fica liberado ou bloqueado. O bloqueio vale para todos, menos o Edge.

## Como funciona

1. O TI cadastra o navegador na Central como programa (Sites e programas, Adicionar programa). Caminhos comuns:
   - Chrome: `C:\Program Files\Google\Chrome\Application\chrome.exe`
   - Edge: `C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`
   - Firefox: `C:\Program Files\Mozilla Firefox\firefox.exe`
   - Opera: costuma ser por usuário, `%LOCALAPPDATA%\Programs\Opera\opera.exe` (Opera GX: `...\Programs\Opera GX\opera.exe`); confira no PC.
2. A cada 3 segundos o Terminal lê, pela Automação da Interface do Windows, o endereço da aba visível de cada janela dos quatro navegadores, de todos os perfis. Só a barra de endereço é lida; o conteúdo das páginas não.
3. Se o domínio não é operacional para aquela máquina (não está nos sites dela nem na lista liberada pelo TI), o Terminal envia `chrome_visit` com o nome do navegador no campo `item`. Enquanto o site continua na tela, envia `chrome_stay` uma vez por minuto. Só o domínio vai para a Central. (Os nomes dos eventos e da rota `/chrome-sites` ficaram como estavam, para os Terminais antigos continuarem funcionando.)
4. A Central cria um alerta urgente ("Abriu x num navegador...") e mostra o domínio em **Sites nos navegadores > Esperando decisão**. O histórico e a notificação dizem qual navegador.
5. O TI escolhe:
   - **Liberar**: o domínio (e subdomínios) para de gerar aviso em todos os computadores.
   - **Bloquear**: o Terminal desvia para uma página de "site bloqueado" a aba que estiver nesse domínio, em qualquer navegador menos o Edge, e grava o domínio na política de cada navegador da conta da atendente:
     - Chrome: `HKCU\Software\Policies\Google\Chrome\URLBlocklist` (vale na hora);
     - Edge: nunca bloqueia (veja "Edge: nenhum site bloqueado");
     - Firefox: `HKCU\Software\Policies\Mozilla\Firefox\WebsiteFilter\Block`, no formato `*://*.dominio/*` (o Firefox só lê ao ser aberto de novo; até lá vale o desvio da aba);
     - Opera: não aceita políticas; vale só o desvio da aba.
   A decisão resolve os alertas abertos daquele domínio (inclusive os antigos, com o texto "no Google Chrome"), fica na auditoria e pode ser trocada depois.

Os domínios do login do Google (`accounts.google.com`, `myaccount.google.com`, `accounts.youtube.com`) nunca geram aviso e não podem ser bloqueados, porque os perfis do Chrome dependem deles.

## Decisão por atendente (20/11/2026)

Pedido do dono: "a gabi pode usar o youtube mas a kamila não". Cada atendente tem o próprio pedido:

- Site novo aberto pela atendente vira um pedido **dela** (tabela `ChromeDeviceSites`, um por computador e domínio), com o navegador. Aparece em **Esperando decisão** com o nome dela, e o alerta e a notificação são do computador dela.
- Em cada pedido o TI escolhe **Vale para**: "Só <atendente>" (padrão) ou "Todas as atendentes", e então Liberar ou Bloquear. Decidir para todas apaga os pedidos esperando daquele domínio em todas as atendentes.
- Tentativa num site **bloqueado para todas** (sem decisão só para ela) também vira pedido dela, com alerta e o selo "Bloqueado para todas: ela pediu". O site continua bloqueado até o TI decidir. Bloqueado **só para ela**, a tentativa fica só no histórico (não pede de novo). Pedido do dono: "toda vez que uma atendente entrar num site que não está permitido ou bloqueado eu vou decidir".
- A decisão de uma atendente **vale mais** que a decisão para todas (`ChromeSites`), no domínio e nos subdomínios dele. Ex.: YouTube bloqueado para todas e liberado só para a Gabi.
- Em **Já decididos**, decisões "Só <atendente>" têm o botão **Tirar** (`decision: "reset"`): apaga a decisão dela e volta a valer a de todas.
- O formulário "Decidir um domínio" também escolhe a atendente.
- Cada Terminal recebe em `chromeAllowed`/`chromeBlocked` a lista já calculada para o computador dele (`ChromeEndpoints.DecidedAsync(db, deviceId)`); o Terminal não mudou.
- Limite conhecido: liberar só um subdomínio para uma atendente (ex.: `m.youtube.com`) não vence o bloqueio do domínio pai para todas (`youtube.com`).

## Edge: nenhum site bloqueado (24/11/2026)

Decisão do dono, a partir do Terminal 2.15.1: no Edge nenhum site é bloqueado, para nenhuma atendente. O instalador não grava mais o `*`, e o Terminal mantém a `URLBlocklist` do Edge vazia e não desvia abas dele. Os bloqueios do TI valem só no Chrome, no Opera e no Firefox. Site não operacional aberto no Edge continua gerando aviso; site que o TI já bloqueou abre no Edge sem aviso.

## Travas nos navegadores (instalador com bloqueio)

`Bloqueio-Comum.ps1` grava na conta da atendente:

- `chrome.exe`, `firefox.exe` e `opera.exe` (além do `msedge.exe`, que já estava) na lista de programas permitidos;
- Chrome: sem janela anônima (`IncognitoModeAvailability = 1`), sem convidado (`BrowserGuestModeEnabled = 0`), sem ferramentas de desenvolvedor (`DeveloperToolsAvailability = 2`);
- Edge: sem InPrivate (`InPrivateModeAvailability = 1`), sem convidado e sem ferramentas de desenvolvedor, com os mesmos valores;
- Firefox: sem janela privativa (`DisablePrivateBrowsing = 1`) e sem ferramentas de desenvolvedor (`DisableDeveloperTools = 1`);
- Opera: não aceita políticas, então não tem trava de janela privativa;
- permissão de escrita da conta **só** nas três chaves de lista de bloqueio, para o Terminal aplicar as decisões. As demais políticas continuam só de administrador.

Criar e trocar perfis continua liberado. Extensões não foram bloqueadas, para não desligar as que as atendentes já usam nos perfis.

## Limites conhecidos

- O WhatsApp instalado como aplicativo do navegador (janela sem barra de endereço) não é lido.
- No Opera, a janela privativa continua possível e o bloqueio depende só do desvio da aba.
- Conferido em 20/11/2026 (leitura e desvio): Edge ("Barra de pesquisa e endereços", campo de texto) e Firefox. No Firefox a barra é uma caixa de combinação e ele não informa o foco pela automação; por isso o Terminal lê o endereço da página da aba visível (o "documento" que não está fora da tela) e, para desviar, usa Ctrl+L com a janela do Firefox na frente. Opera GX: leitura conferida ("Barra de endereços").
- Janela minimizada (Firefox) ou vídeo em tela cheia (qualquer navegador) não mostra a barra: nada é lido enquanto estiver assim.
- O desvio só acontece com a janela do navegador na frente (a atendente olhando o site), para o Enter nunca cair em outro programa.
- Na instalação de teste (sem bloqueio) o Terminal não tem permissão para gravar as políticas: ele avisa e desvia a aba, mas a política não é gravada.
- PCs já instalados recebem as travas e as permissões novas quando a atendente clica em **Atualizar** (a atualização roda de novo o `GravarBloqueio`).
