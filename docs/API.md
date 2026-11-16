# Terminal Pontual — API

Status: **revisão 2**. Incorpora a revisão do agente `ecc:architect` e as decisões aprovadas pela Pontual. Os contratos detalhados (OpenAPI) são gerados a partir do código na Fase 1.

São **duas APIs em instâncias diferentes**: a de produção (seções 1 a 4) e a da Central dos Agentes (seção 5).

## 1. Convenções

- Base: `/api/v1`. JSON em UTF-8, propriedades em `camelCase`.
- Datas em ISO 8601, UTC. Identificadores `uuid`.
- Listas paginadas por cursor: `?limit=50&cursor=...`.
- Erros em *Problem Details* (RFC 9457).
- Escritas sensíveis aceitam `Idempotency-Key`.
- Limite de taxa por origem; `429` com `Retry-After`.

### Autorização

A coluna **Permissão** usa os códigos da matriz única em [SECURITY.md](SECURITY.md), seção 3. Aquela matriz é a fonte; esta tabela só diz qual permissão cada rota exige. Não existe "função mínima".

A coluna **Exig.** repete as exigências da matriz: **R** reautenticação · **J** justificativa no corpo · **P** prazo no corpo · **2A** dois administradores.

| Público | Prefixo | Autenticação |
|---|---|---|
| Painel do TI | `/api/v1/...` | Cookie de sessão + segundo fator |
| Terminais | `/api/v1/device/...` | *Token* curto obtido com a chave da máquina; estado da máquina verificado a cada requisição |

## 2. Painel administrativo

### Sessão

| Método | Rota | Descrição |
|---|---|---|
| POST | `/auth/login` | Senha; devolve desafio de segundo fator |
| POST | `/auth/mfa` | Conclui o login |
| POST | `/auth/mfa/enroll` | Cadastro do segundo fator; única ação possível antes dele |
| POST | `/auth/recovery` | Login com código de recuperação |
| POST | `/auth/reauthenticate` | Confirma identidade para ações com R |
| POST | `/auth/logout` | |
| GET | `/auth/me` | Usuário e permissões |

### Administradores, unidades e chaves

| Método | Rota | Permissão | Exig. |
|---|---|---|---|
| GET | `/admins` | `admins.manage` | |
| POST | `/admins` | `admins.manage` | R, J |
| PATCH | `/admins/{id}` | `admins.manage` | R, J |
| GET | `/sites` | `devices.read` | |
| POST, PATCH | `/sites`, `/sites/{id}` | `sites.manage` | |
| GET | `/signing-keys` | `audit.read` | |
| POST | `/signing-keys/rotate` | `keys.manage` | R, J |

### Máquinas

| Método | Rota | Permissão | Exig. |
|---|---|---|---|
| GET | `/devices`, `/devices/{id}` | `devices.read` | |
| PATCH | `/devices/{id}` | `devices.manage` | |
| POST | `/devices/{id}/approve` | `devices.manage` | |
| POST | `/devices/{id}/retire` | `devices.manage` | |
| PUT | `/devices/{id}/update-channel` | `devices.manage` | |
| POST | `/devices/{id}/revoke` | `devices.revoke` | R, J |
| POST | `/devices/{id}/keys/rotate` | `devices.manage` | |
| POST | `/enrollment-tokens` | `enrollment.create` | R |
| GET | `/devices/{id}/usage` | `usage.read` | |
| GET | `/devices/{id}/events` | `devices.read` | |
| GET | `/devices/{id}/status-history` | `devices.read` | |
| POST | `/devices/{id}/maintenance` | `maintenance.manage` | R, J, P |
| DELETE | `/devices/{id}/maintenance` | `maintenance.manage` | R, J |
| GET | `/devices/{id}/maintenance` | `devices.read` | |

`GET /devices/{id}` devolve o que o escritório virtual mostra: atendente atual, nome da máquina, status, aplicativos em uso, tempo de atividade, última comunicação e alertas abertos.

Revogar derruba a conexão da máquina imediatamente.

### Atendentes

| Método | Rota | Permissão | Exig. |
|---|---|---|---|
| GET | `/attendants`, `/attendants/{id}` | `attendants.read` | |
| POST, PATCH | `/attendants`, `/attendants/{id}` | `attendants.manage` | |
| GET | `/attendants/{id}/usage` | `usage.read.individual` | leitura auditada |
| POST | `/attendants/{id}/export` | `subject.export` | R, J |

### Aplicativos e configuração

| Método | Rota | Permissão | Exig. |
|---|---|---|---|
| GET | `/applications`, `/applications/{id}` | `apps.read` | |
| POST, PATCH | `/applications/websites`, `/applications/websites/{id}` | `apps.websites.manage` | |
| POST | `/change-requests` (tipo `executable.add` ou `executable.change`) | `apps.executables.propose` | R, J |
| GET | `/change-requests`, `/change-requests/{id}` | `apps.read` | |
| POST | `/change-requests/{id}/approve` | `apps.executables.approve` | R, 2A |
| POST | `/change-requests/{id}/reject` | `apps.executables.approve` | R, J |
| GET | `/config-profiles`, `/config-profiles/{id}` | `config.read` | |
| POST, PUT | `/config-profiles`, `/config-profiles/{id}` | `config.edit` | |
| POST | `/config-profiles/{id}/publish` | `config.publish` | R, J |
| GET | `/config-profiles/{id}/versions` | `config.read` | |
| GET | `/config-profiles/{id}/rollout` | `config.read` | |

Regras:

- Não existe rota para criar ou alterar executável diretamente. Só por proposta aprovada por outra pessoa.
- O servidor recusa `approve` de quem fez a proposta.
- `publish` recusa perfil que contenha executável sem aprovação vigente.

### Alertas e auditoria

| Método | Rota | Permissão | Exig. |
|---|---|---|---|
| GET | `/alerts` | `alerts.read` | |
| POST | `/alerts/{id}/acknowledge`, `/alerts/{id}/resolve` | `alerts.handle` | |
| GET | `/alert-rules` | `alerts.read` | |
| POST, PATCH | `/alert-rules`, `/alert-rules/{id}` | `alert-rules.manage` | R, J |
| GET | `/audit-log` | `audit.read` | leitura auditada |
| GET | `/audit-log/verify` | `audit.read` | confere a cadeia contra as âncoras |
| GET | `/audit-anchors` | `audit.read` | |
| POST | `/exports` | `audit.read` ou `usage.read`, conforme o conteúdo | R, J |

### Atualizações

| Método | Rota | Permissão | Exig. |
|---|---|---|---|
| GET | `/update-packages`, `/update-packages/{id}/rollout` | `updates.read` | |
| POST | `/update-packages` | `updates.release` | R, J |
| POST | `/update-packages/{id}/release` | `updates.release` | R, J |

O envio traz o pacote e o manifesto **já assinados** fora do servidor. O servidor confere a assinatura contra a chave pública de release e recusa `version_sequence` não crescente.

### Suporte remoto

| Método | Rota | Permissão | Exig. |
|---|---|---|---|
| POST | `/devices/{id}/support-sessions` | `support.request` | R, J, P |
| POST | `/support-sessions/{id}/formal-authorization` | `support.formal.authorize` | R, J, 2A |
| GET | `/support-sessions`, `/support-sessions/{id}` | `support.read` | |
| POST | `/support-sessions/{id}/join` | `support.request` (quem solicitou) | |
| POST | `/support-sessions/{id}/end` | `support.request` | |

`join` só responde depois do consentimento ou da autorização formal, e devolve uma credencial de curta duração para a ferramenta. Estas rotas existem a partir da Fase 6.

### Escritório virtual

| Método | Rota | Permissão |
|---|---|---|
| GET | `/office-layouts`, `/office-layouts/{id}` | `devices.read` |
| POST | `/office-layouts` | `layouts.manage` |
| PUT | `/office-layouts/{id}/stations` | `layouts.manage` |

## 3. Terminais

### REST

| Método | Rota | Descrição |
|---|---|---|
| POST | `/device/enroll` | Código de cadastro + chave pública + dados do Windows. Cria a máquina como **pendente** |
| POST | `/device/token/challenge` | Pede um desafio |
| POST | `/device/token` | Devolve o desafio assinado; só emite *token* para máquina ativa |
| POST | `/device/keys/rotate` | Registra nova chave, assinada pela atual |
| GET | `/device/config` | Configuração atual assinada |
| POST | `/device/telemetry` | Lote de uso e eventos |
| GET | `/device/updates/current` | Manifesto assinado destinado ao canal da máquina |
| GET | `/device/updates/{id}/package` | Download do pacote |
| POST | `/device/attendant-login` | **Só existe se a identificação for por login no terminal** (opção B de [SECURITY.md](SECURITY.md), seção 10). Não funciona offline |

### Hub SignalR `/hubs/device`

Do terminal para o servidor:

| Método | Conteúdo |
|---|---|
| `Heartbeat` | versão do agente, sequência de configuração aplicada, tempo de atividade |
| `ReportActivity` | aplicativos autorizados em uso |
| `ReportEvent` | evento de máquina |
| `AckConfig` | sequência aplicada, ou motivo da recusa |
| `AttendantSignedIn`, `AttendantSignedOut` | identificação da atendente |
| `SupportConsent` | resposta da atendente |
| `SupportEvent` | aviso exibido, início, término, expiração local |

Do servidor para o terminal — **conjunto fechado**:

| Mensagem | Conteúdo assinado | O que o terminal confere antes de agir |
|---|---|---|
| `ConfigChanged(sequence)` | A configuração baixada traz sequência, perfil e emissão | Assinatura, sequência maior que a aplicada, perfil da máquina, lista de bloqueio fixa |
| `UpdateAvailable(packageId)` | Manifesto assinado pela chave de release | Assinatura, editor do pacote, versão maior |
| `MaintenanceGrant(id, expiresAt)` | Máquina, início e prazo | Assinatura, prazo dentro do teto local |
| `MaintenanceEnd(id)` | Identificador | Assinatura |
| `SupportRequested(sessionId, adminName, mode, expiresAt)` | Sessão, modo e prazo | Assinatura, prazo; exibe consentimento ou aviso prévio |
| `SupportEnd(sessionId)` | Identificador | Assinatura |

Não existe mensagem para executar comando, script ou processo. Mensagem com assinatura inválida, sequência antiga ou prazo vencido é recusada e gera evento.

O que essas verificações **não** cobrem está em [SECURITY.md](SECURITY.md), seção 2.

## 4. Painel em tempo real

Hub SignalR `/hubs/admin`, do servidor para o painel:

| Mensagem | Uso |
|---|---|
| `DeviceStatusChanged` | muda a animação da estação |
| `DeviceActivityChanged` | aplicativos em uso |
| `AlertRaised`, `AlertUpdated` | alertas |
| `ConfigRolloutProgress` | andamento da distribuição |
| `ChangeRequestPending` | proposta aguardando segunda pessoa |
| `MaintenanceChanged` | manutenção iniciada, encerrada ou expirada |
| `SupportSessionChanged` | estado da sessão de suporte |

O painel só recebe o que a permissão do usuário cobre.

## 5. Central dos Agentes

Instância separada (`Pontual.AgentHub`), com endereço, banco e contas próprios. **Não compartilha autenticação, rede nem credenciais com a produção** e não expõe nenhuma rota de máquinas.

### Ingestão

`POST /api/v1/agent-events`, autenticada por chave de API da origem:

```json
{
  "events": [
    {
      "sessionId": "identificador-da-sessao-na-origem",
      "agentName": "security-reviewer",
      "agentType": "security-reviewer",
      "parentSessionId": null,
      "type": "tool.started",
      "tool": "Edit",
      "activity": "coding",
      "summary": "Editando src/server/Pontual.Server/Auth/LoginEndpoint.cs",
      "files": ["src/server/Pontual.Server/Auth/LoginEndpoint.cs"],
      "progress": null,
      "occurredAt": "2026-11-16T14:03:22Z"
    }
  ]
}
```

- `agentName` e `agentType` são **opcionais**. Se a origem não souber qual agente executou a ferramenta **[H-13]**, eles vão nulos e o evento é atribuído à sessão. O servidor nunca deduz o agente.
- `activity` é declarada pela origem: `coding`, `analyzing`, `testing`, `working`, `waiting`. O servidor não inspeciona comandos.
- `progress` é opcional; sem ele, não há percentual.
- O evento leva metadados, nunca conteúdo de arquivos, de comandos ou de conversas.

### Como a origem classifica a atividade

O encaminhador em `tools/agent-events/` roda na máquina de desenvolvimento e classifica **localmente**:

| Sinal na origem | `activity` |
|---|---|
| Ferramenta de edição ou escrita | `coding` |
| Ferramenta de leitura ou busca | `analyzing` |
| Comando que casa com a lista configurável de comandos de teste (por exemplo `dotnet test`, `npm test`) | `testing` |
| Outra ferramenta | `working` |
| Aguardando entrada ou permissão | `waiting` |

A lista de comandos de teste é uma regra explícita e configurável. O comando em si não é enviado. Um comando de teste fora da lista aparece como `working`: a tela pode mostrar menos do que aconteceu, nunca algo que não aconteceu.

### Estado exibido

| Evento | Estado do personagem |
|---|---|
| `tool.started` com `activity` | Programando, Analisando, Testando ou Trabalhando |
| `run.waiting` | Aguardando |
| `run.stopped` | Concluído |
| `tool.failed`, `run.failed` | Com erro |
| Sem eventos recentes | Fora de cena ou ocioso |

### Leitura

| Método | Rota |
|---|---|
| GET | `/agent-runs`, `/agent-runs/{id}`, `/agent-runs/{id}/events` |
| GET, PUT | `/agent-personas` |
| GET, POST | `/agent-sources` |

Hub SignalR `/hubs/agents`: `AgentRunChanged`, `AgentEventReceived`.
