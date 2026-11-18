# Central dos Agentes

Escritório 2D em pixel art que mostra, em tempo real, os agentes reais do Claude Code que trabalham neste projeto: a sessão principal e os subagentes.

É um módulo isolado. Não conhece o servidor de produção do Terminal Pontual nem as máquinas das atendentes, e escuta apenas em `127.0.0.1`.

## Como iniciar

```bash
cd src/agenthub
npm install      # só na primeira vez
npm start        # compila a interface e inicia o serviço
```

Depois abra <http://127.0.0.1:4317>.

Os eventos chegam sozinhos enquanto o Claude Code é usado neste projeto, pelos hooks declarados em `.claude/settings.json`. Sem eventos, o escritório fica vazio: não há dados de demonstração.

Outros comandos:

| Comando | O que faz |
|---|---|
| `npm run server` | Inicia só o serviço, usando a interface já compilada |
| `npm run dev` | Interface em modo de desenvolvimento, em <http://127.0.0.1:5173> (o serviço precisa estar rodando) |
| `npm test` | Testes do serviço e do encaminhador |
| `npm run typecheck` | Verificação de tipos da interface |

## Partes

| Parte | Onde | Papel |
|---|---|---|
| Captura | `.claude/settings.json` | Hooks do Claude Code que disparam a cada evento |
| Encaminhador | `tools/agent-events/forward.mjs` | Converte o evento, remove conteúdo e envia; guarda em fila se o serviço estiver fora |
| Serviço de eventos | `server/index.mjs` | Recebe, valida e distribui os eventos |
| Estado | `server/store.mjs` | Deriva o estado de cada agente apenas dos eventos recebidos |
| Histórico | `data/events.jsonl` | Eventos gravados; o estado é reconstruído ao reiniciar |
| Tempo real | `GET /api/v1/stream` | Server-Sent Events para a interface |
| Interface | `web/` | Cena em Phaser e painéis em React |

## O que é enviado e o que não é

Enviado: nome da ferramenta, caminho do arquivo relativo ao projeto, identificador e tipo do agente, título de tarefas e a descrição curta dada ao lançar um subagente.

Nunca enviado: instruções, comandos, saídas de ferramenta, conteúdo de arquivos e mensagens do agente.

## Estados

| Estado | Evento real que o provoca |
|---|---|
| Trabalhando | Instrução recebida, ferramenta iniciada ou concluída, subagente iniciado |
| Aguardando | Sessão recém-iniciada, pedido de permissão, notificação de espera |
| Concluído | Fim do turno da sessão principal, fim do subagente |
| Com erro | Falha de ferramenta ou de turno, até o próximo evento |
| Desconectado | Sessão encerrada |
| Desconhecido | Eventos insuficientes, ou agente trabalhando sem eventos há 10 minutos |

## Diferenças em relação à arquitetura documentada

Esta primeira versão foi feita para rodar na máquina de desenvolvimento sem instalar nada além do Node. Em relação a `docs/ARCHITECTURE.md`:

| Arquitetura | Nesta versão | Motivo |
|---|---|---|
| ASP.NET Core | Node, sem dependências no serviço | O .NET SDK não está instalado |
| SignalR | Server-Sent Events | Acompanha a troca acima |
| PostgreSQL | Arquivo `data/events.jsonl` | Não há PostgreSQL instalado |
| Contas próprias | Sem login; acesso só local | Escuta apenas em `127.0.0.1` |

O contrato de eventos é o de `docs/API.md`, seção 5, com campos a mais. A troca para a pilha documentada não muda o encaminhador nem a interface.

## Limites conhecidos

- A sessão principal não tem nome nos hooks e aparece como "Sessão principal".
- A tarefa atual só aparece quando o agente registra uma tarefa ou quando o lançamento do subagente traz uma descrição ligada ao identificador dele.
- Os hooks valem para sessões do Claude Code abertas neste projeto. Sessões em outros projetos não aparecem.
- A leitura do estado não exige autenticação; por isso o serviço não deve ser exposto fora da máquina.
- A fonte dos títulos vem do Google Fonts; sem internet, a interface usa uma fonte do sistema.
