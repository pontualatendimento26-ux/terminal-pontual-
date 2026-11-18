# Terminal Pontual — Contrato da versão 1.0 de teste

Este documento é a fonte única para quem implementa a 1.0. Onde ele diverge de ARCHITECTURE.md, API.md ou DATABASE.md, **vale este**, só para a 1.0. As divergências são simplificações de uma versão de teste e estão listadas na seção 7.

## 1. Escopo

Dentro: servidor, Central do TI (web), tela da atendente (web) e programa Terminal (Windows) que hospeda essa tela e abre os sites reais.

Fora: bloqueio do Windows, serviços Windows (Service e Broker), suporte remoto, instalador, atualização automática, assinatura de configuração.

## 2. Pilha

- .NET 10. Servidor em ASP.NET Core (APIs mínimas), EF Core com **SQLite** (`data/pontual.db` ao lado do executável, criado na primeira execução).
- Servidor escuta em `http://0.0.0.0:5080`. Sem TLS nesta versão.
- Arquivos estáticos em `src/server/Pontual.Server/wwwroot/`: `central/` (Central do TI) e `terminal/` (tela da atendente). `/` redireciona para `/central/`.
- As duas telas são HTML, CSS e JavaScript puros, sem etapa de build, com o mesmo visual de `prototipos/index.html`.
- Terminal: WPF (.NET 10) com WebView2, em `src/terminal/Pontual.Terminal.Shell`.
- Reutilizar `Pontual.Domain.Authorization.PermissionMatrix` e `Pontual.Domain.Audit.AuditChain`, já prontos e testados.

## 3. Regras gerais da API

- Prefixo `/api/v1`. JSON em `camelCase`. Datas em ISO 8601 UTC.
- Erros: `{ "error": "mensagem em português" }` com o status HTTP adequado (400, 401, 403, 404, 409).
- Painel: cookie de sessão `pontual_admin` (`HttpOnly`, `SameSite=Strict`). Toda requisição que não seja `GET` exige o cabeçalho `X-Pontual: 1`; sem ele, 400.
- Terminal: cabeçalho `Authorization: Bearer <token>`.
- Toda ação de administrador que altera algo grava um registro de auditoria encadeado (`AuditChain`), com texto legível em português.
- A autorização de cada rota do painel usa a `PermissionMatrix` com a função do administrador logado. As permissões estão indicadas entre colchetes.

## 4. Rotas do painel

### Primeiro uso e sessão

| Rota | Corpo | Resposta |
|---|---|---|
| `GET /setup/status` | — | `{ needsSetup }` (verdadeiro se não existe administrador) |
| `POST /setup` | `{ name, email, password }` | 204. Cria o primeiro administrador como `superadmin`. Só funciona sem administradores; depois, 409 |
| `POST /auth/login` | `{ email, password }` | `{ id, name, email, role }` e o cookie. 401 se inválido |
| `POST /auth/logout` | — | 204 |
| `GET /auth/me` | — | `{ id, name, email, role, permissions: [códigos] }` ou 401 |
| `GET /admins` [admins.manage] | — | `[{ id, name, email, role, active }]` |
| `POST /admins` [admins.manage] | `{ name, email, password, role }` | o administrador criado. `role`: `superadmin`, `admin`, `support`, `auditor` |

Senha: mínimo de 10 caracteres. Hash com `PasswordHasher` do ASP.NET Core Identity.

### Máquinas

`DeviceDto`:

```json
{ "id": "uuid", "hostname": "PC-04", "attendantName": "Marina Alves",
  "state": "online", "windows": "Windows 10 Pro 22H2",
  "appsInUse": ["Safra"], "sessionStartedAt": "…", "lastSeenAt": "…",
  "openAlerts": 0, "maintenance": null, "observationMode": true }
```

`state`: `pending` (cadastrada, aguardando aprovação), `maintenance` (janela ativa), `offline` (sem sinal há mais de 45 s), `alert` (online com alerta aberto), `online`. `maintenance` é `{ reason, until }` ou `null`.

| Rota | Corpo | Resposta |
|---|---|---|
| `GET /devices` [devices.read] | — | `[DeviceDto]` |
| `POST /enrollment-codes` [enrollment.create] | — | `{ code, expiresAt }`. Código `XXXX-XXXX` (letras A–Z sem I e O, dígitos 2–9), uso único, validade de 30 minutos |
| `POST /devices/{id}/approve` [devices.manage] | `{ attendantName }` | `DeviceDto` |
| `PATCH /devices/{id}` [devices.manage] | `{ attendantName?, observationMode? }` | `DeviceDto` |
| `DELETE /devices/{id}` [devices.revoke] | — | 204. O token deixa de valer |
| `POST /devices/{id}/maintenance` [maintenance.manage] | `{ reason, minutes }` | `DeviceDto`. `reason` obrigatório; `minutes` entre 5 e 480 |
| `DELETE /devices/{id}/maintenance` [maintenance.manage] | — | `DeviceDto` |
| `GET /devices/{id}/history` [usage.read] | — | `[{ at, text }]`, os 100 mais recentes |

### Sites e programas

`ItemDto`:

```json
{ "id": "uuid", "name": "Safra", "kind": "site", "target": "https://epfweb.safra.com.br/Home/Login",
  "group": "Bancos e promotoras", "extraHosts": [], "enabled": true,
  "status": "active", "audience": null, "proposal": null }
```

- `kind`: `site` ou `program`. `target`: URL completa (site) ou caminho do executável (programa).
- `group`: `Bancos e promotoras`, `Comunicação` ou `Pontual e ferramentas`.
- `extraHosts`: outros domínios que o site usa (login, por exemplo).
- `status`: `active`, `blocked` (`enabled` falso) ou `pending` (programa aguardando a segunda aprovação).
- `audience`: `null` para todas as máquinas, ou a lista de ids de máquina.
- `proposal`: `{ by, at, reason }` enquanto `pending`.

| Rota | Corpo | Resposta |
|---|---|---|
| `GET /items` [apps.read] | — | `[ItemDto]` |
| `POST /items/sites` [apps.websites.manage] | `{ name, url, group, audience, extraHosts? }` | `ItemDto`, já ativo. Só `http` e `https` |
| `POST /items/programs` [apps.executables.propose] | `{ name, path, reason, audience }` | `ItemDto` com `status: pending` |
| `POST /items/{id}/approve` [apps.executables.approve] | — | `ItemDto`. 403 se quem aprova é quem propôs |
| `POST /items/{id}/reject` [apps.executables.approve] | — | 204, remove a proposta |
| `PATCH /items/{id}` | `{ name?, enabled?, audience?, extraHosts?, group? }` | `ItemDto`. Site: [apps.websites.manage]. Programa: [apps.executables.approve], e só `enabled` e `audience` |
| `DELETE /items/{id}` | — | 204. Mesma permissão do `PATCH` |

Programas recusados sempre, com 400: caminho que não termina em `.exe`, ou cujo nome de arquivo seja `cmd`, `powershell`, `pwsh`, `wscript`, `cscript`, `mshta`, `regedit`, `rundll32`, `msiexec`, `bash`, `wsl`, `wt`.

Na primeira execução, o servidor cadastra os sites de `docs/APLICATIVOS-E-SITES.md` (ativos, para todas as máquinas), usando a página de entrada de cada um como URL.

### Alertas, notificações e auditoria

| Rota | Resposta |
|---|---|
| `GET /alerts` [alerts.read] | `[{ id, deviceId, deviceLabel, text, at }]`, só os abertos |
| `POST /alerts/{id}/resolve` [alerts.handle] | 204 |
| `GET /notifications` [devices.read] | `[{ id, at, text, urgent, read }]`, as 100 mais recentes |
| `POST /notifications/read` [devices.read] | 204. Marca todas como lidas |
| `GET /audit` [audit.read] | `[{ sequence, at, actor, text }]`, os 200 mais recentes |
| `GET /audit/verify` [audit.read] | `{ intact, failedSequence, failure }` |
| `GET /observed-hosts` [apps.read] | `[{ host, itemId, itemName, deviceLabel, count, lastAt }]` |
| `POST /observed-hosts/allow` [apps.websites.manage] | corpo `{ host, itemId }`. Acrescenta o domínio a `extraHosts` do item e remove da lista |

`deviceLabel` é `"Nome da atendente (HOSTNAME)"`.

## 5. Rotas do terminal

| Rota | Corpo | Resposta |
|---|---|---|
| `POST /device/enroll` (sem token) | `{ code, hostname, windows }` | `{ deviceId, token }`. Máquina nasce `pending`. O servidor guarda só o SHA-256 do token |
| `POST /device/heartbeat` | `{ appsInUse: [nomes], sessionStartedAt }` | `{ state, configVersion, attendantName, maintenance, observationMode }`. `state`: `pending`, `active`, `maintenance`. 401 se a máquina foi removida |
| `GET /device/config` | — | `{ version, attendantName, observationMode, items: [{ id, name, kind, target, group, hosts: [domínios] }] }`. Só itens ativos e que a máquina pode ver. `hosts` = domínio do `target` mais `extraHosts`; vazio para programas |
| `POST /device/events` | `{ events: [{ type, item?, detail?, at }] }` | 204 |

O terminal envia o `heartbeat` a cada 15 segundos. `configVersion` é um inteiro global, incrementado a cada mudança em itens, públicos ou máquinas.

Tipos de evento e efeito no servidor:

| `type` | Quando | Efeito |
|---|---|---|
| `session_started` | O terminal abriu | Histórico |
| `opened` | Abriu um item (`item` = nome) | Histórico; notificação não urgente |
| `closed` | Fechou um item | Histórico |
| `blocked` | Tentou navegar para um domínio não permitido, com o modo de observação desligado (`detail` = domínio) | Histórico; **alerta**; notificação **urgente** |
| `observed` | Navegou para um domínio não listado, com o modo de observação ligado (`detail` = domínio, `item` = nome) | Entra em `observed-hosts`; notificação não urgente na primeira vez de cada domínio |
| `help` | A atendente clicou em "Chamar o TI" | Alerta "chamou o TI"; notificação não urgente |

### Modo de observação

Cada máquina tem `observationMode`, que nasce **ligado**. Os portais usam vários domínios (login, por exemplo), e a lista inicial só tem o domínio principal de cada um.

- Ligado: navegação para um domínio fora da lista é **permitida** e registrada (`observed`). O TI vê os domínios novos e os acrescenta ao item.
- Desligado: a navegação é **bloqueada** (`blocked`).

A regra vale para a navegação da página principal de cada aba. Um domínio é permitido se for igual a um dos `hosts` de qualquer item da máquina, ou subdomínio dele.

## 6. Ponte entre o Terminal e a tela da atendente

A tela da atendente é servida em `/terminal/` e conversa com a API do terminal usando o token. Ela precisa funcionar em dois ambientes:

- **Dentro do programa Terminal:** o programa injeta, antes de a página carregar, `window.PONTUAL = { token, hostname, hosted: true }`.
- **Num navegador comum, para teste:** sem `window.PONTUAL`, a página lê `?token=` da URL e usa `hosted: false`.

Mensagens da página para o programa, por `window.chrome.webview.postMessage(objeto)`:

| Mensagem | Efeito |
|---|---|
| `{ type: "open", id }` | Abre o item (site numa aba em tela cheia, ou inicia o programa) |
| `{ type: "focus", id }` | Volta para a aba já aberta do item |
| `{ type: "close", id }` | Fecha a aba |

Mensagem do programa para a página, por `postMessage`: `{ type: "state", open: [{ id, name }] }`, sempre que as abas abertas mudam.

Com `hosted: false`, a página simula: mantém a lista de abertos sozinha, abre o site numa nova aba do navegador e envia ela mesma os eventos `opened` e `closed`.

Quem envia `heartbeat` e eventos: o **programa**, quando `hosted` é verdadeiro; a **página**, quando é falso.

## 7. Simplificações da 1.0 em relação à arquitetura

| Arquitetura | Na 1.0 | Motivo |
|---|---|---|
| PostgreSQL | SQLite | Não exigir instalação de banco no teste |
| TLS | HTTP | Sem certificados no teste; uso só na rede local |
| Identidade da máquina por par de chaves | Token aleatório | Simplicidade no teste |
| Segundo fator no login | Só senha | Idem |
| Service e Broker | Um único programa na sessão da atendente | Sem bloqueio nesta versão |
| SignalR | Consulta periódica | Menos partes móveis no teste |
| Configuração assinada | Sem assinatura | Idem |
| React | JavaScript puro | Manter o visual idêntico ao protótipo aprovado |

Nenhuma destas simplificações deve ir para produção.
