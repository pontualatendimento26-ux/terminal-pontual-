// Tipos e conexão em tempo real com o serviço de eventos.

export type AgentState = 'working' | 'waiting' | 'done' | 'error' | 'disconnected' | 'unknown';

export interface AgentEvent {
  type: string;
  hookEvent: string | null;
  tool: string | null;
  activity: string | null;
  file: string | null;
  detail: string | null;
  errorCode: string | null;
  errorMessage: string | null;
  taskSubject: string | null;
  occurredAt: string;
}

export interface AgentError {
  kind: 'tool' | 'turn';
  code: string | null;
  message: string | null;
  tool: string | null;
  at: string;
}

export interface AgentView {
  key: string;
  sessionId: string;
  shortSession: string;
  agentId: string | null;
  kind: 'main' | 'subagent';
  parentKey: string | null;
  /** Apelido dado pela Central para distinguir os personagens. */
  nickname: string;
  /** Tipo real informado pelo hook, ou um rótulo neutro. */
  name: string;
  agentType: string | null;
  identified: boolean;
  project: string | null;
  state: AgentState;
  stateReason: string | null;
  activity: string | null;
  currentTool: string | null;
  currentFile: string | null;
  task: string | null;
  taskSource: 'task' | 'spawn' | null;
  tasksDone: number;
  tasksTotal: number;
  startedAt: string | null;
  firstSeenAt: string;
  lastEventAt: string;
  endedAt: string | null;
  tools: Record<string, number>;
  files: string[];
  events: AgentEvent[];
  eventCount: number;
  errorCount: number;
  lastError: AgentError | null;
}

export interface Diagnostics {
  total: number;
  toolEventsWithAgentId: number;
  toolEventsWithoutAgentId: number;
  hooks: { hook: string; count: number; fields: string[] }[];
}

interface Payload {
  agents: AgentView[];
  diagnostics: Diagnostics;
  serverTime: string;
}

export const STATE_LABEL: Record<AgentState, string> = {
  working: 'Trabalhando',
  waiting: 'Aguardando',
  done: 'Concluído',
  error: 'Com erro',
  disconnected: 'Desconectado',
  unknown: 'Desconhecido',
};

// Cada estado tem um sinal próprio, para não depender só de cor.
export const STATE_GLYPH: Record<AgentState, string> = {
  working: '▶',
  waiting: '…',
  done: '✓',
  error: '!',
  disconnected: '○',
  unknown: '?',
};

export const ACTIVITY_LABEL: Record<string, string> = {
  coding: 'Programando',
  analyzing: 'Analisando',
  testing: 'Testando',
  delegating: 'Lançando subagente',
  working: 'Executando ferramenta',
};

// Códigos de erro informados pelo Claude Code, em linguagem simples.
// "short" cabe embaixo do personagem; "long" vai no cartão e no painel.
const ERROR_TEXT: Record<string, { short: string; long: string }> = {
  rate_limit: { short: 'Limite de uso', long: 'Limite de uso da conta Claude atingido' },
  usage_limit_reached: { short: 'Limite de uso', long: 'Limite de uso da conta Claude atingido' },
  authentication_failed: { short: 'Falha de login', long: 'Falha de login na conta Claude' },
  billing_error: { short: 'Cobrança', long: 'Problema de cobrança na conta Claude' },
  invalid_request: { short: 'Pedido recusado', long: 'A API recusou o pedido' },
  server_error: { short: 'Servidor fora', long: 'Erro no servidor da Anthropic' },
  max_output_tokens: { short: 'Resposta longa', long: 'A resposta passou do tamanho máximo' },
  interrupted: { short: 'Interrompido', long: 'Interrompido por você' },
};

/** Motivo curto, para a etiqueta embaixo do personagem. */
export function errorShort(error: AgentError | null): string {
  if (!error) return 'Com erro';
  if (error.code && ERROR_TEXT[error.code]) return ERROR_TEXT[error.code].short;
  if (error.kind === 'tool' && error.tool) return `Falhou: ${error.tool}`;
  return 'Com erro';
}

/** Motivo completo, para o cartão e o painel de detalhes. */
export function errorLong(error: AgentError | null): string {
  if (!error) return 'Com erro (motivo não registrado)';
  const where = error.kind === 'tool' && error.tool ? ` (${error.tool})` : '';
  if (error.code && ERROR_TEXT[error.code]) return `${ERROR_TEXT[error.code].long}${where}`;
  if (error.kind === 'tool') return `A ferramenta falhou${where}`;
  if (error.code && error.code !== 'unknown') return `O turno terminou com erro (${error.code})`;
  return 'O turno terminou com erro';
}

export interface HubHandlers {
  onAgents: (agents: Map<string, AgentView>, diagnostics: Diagnostics) => void;
  onConnection: (connected: boolean) => void;
}

/** Abre o canal em tempo real. Devolve a função que o fecha. */
export function connectHub(handlers: HubHandlers): () => void {
  const agents = new Map<string, AgentView>();
  const source = new EventSource('/api/v1/stream');

  source.addEventListener('open', () => handlers.onConnection(true));
  source.addEventListener('error', () => handlers.onConnection(false));

  source.addEventListener('snapshot', (message) => {
    const payload = JSON.parse((message as MessageEvent<string>).data) as Payload;
    agents.clear();
    for (const agent of payload.agents) agents.set(agent.key, agent);
    handlers.onConnection(true);
    handlers.onAgents(new Map(agents), payload.diagnostics);
  });

  source.addEventListener('update', (message) => {
    const payload = JSON.parse((message as MessageEvent<string>).data) as Payload;
    for (const agent of payload.agents) agents.set(agent.key, agent);
    handlers.onAgents(new Map(agents), payload.diagnostics);
  });

  return () => source.close();
}
