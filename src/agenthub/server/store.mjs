// Estado da Central dos Agentes, derivado apenas de eventos recebidos.
// Nada aqui inventa nome, tarefa ou atividade: quando a informação não veio
// num evento, o campo fica nulo e o estado é "unknown".

const MAX_EVENTS = 200;
const MAX_FILES = 40;
const DEFAULT_STALE_MS = 10 * 60 * 1000;

export const STATES = ['working', 'waiting', 'done', 'error', 'disconnected', 'unknown'];

function text(value, max) {
  return typeof value === 'string' && value.trim() ? value.trim().slice(0, max) : null;
}

/** Valida e limita um evento vindo da rede. Devolve null se for inválido. */
export function normalizeEvent(raw) {
  if (!raw || typeof raw !== 'object') return null;
  const sessionId = text(raw.sessionId, 120);
  const type = text(raw.type, 60);
  const occurredAt = text(raw.occurredAt, 40);
  if (!sessionId || !type || !occurredAt || Number.isNaN(Date.parse(occurredAt))) return null;

  const spawn =
    raw.spawn && typeof raw.spawn === 'object'
      ? { subagentType: text(raw.spawn.subagentType, 80), description: text(raw.spawn.description, 100) }
      : null;
  const task =
    raw.task && typeof raw.task === 'object' && text(raw.task.id, 120)
      ? { id: text(raw.task.id, 120), subject: text(raw.task.subject, 140) }
      : null;

  return {
    sessionId,
    agentId: text(raw.agentId, 120),
    agentType: text(raw.agentType, 80),
    hookEvent: text(raw.hookEvent, 60),
    type,
    tool: text(raw.tool, 80),
    activity: text(raw.activity, 20),
    file: text(raw.file, 300),
    project: text(raw.project, 120),
    spawn,
    spawnedAgentId: text(raw.spawnedAgentId, 120),
    task,
    detail: text(raw.detail, 40),
    error:
      raw.error && typeof raw.error === 'object'
        ? { code: text(raw.error.code, 40), message: text(raw.error.message, 240) }
        : null,
    fieldsSeen: Array.isArray(raw.fieldsSeen)
      ? raw.fieldsSeen.filter((f) => typeof f === 'string').slice(0, 40).map((f) => f.slice(0, 60))
      : [],
    occurredAt: new Date(Date.parse(occurredAt)).toISOString(),
  };
}

// Apelidos dos personagens. São só rótulos para distinguir um agente do outro
// na tela; o tipo real informado pelo hook continua em "name" e "agentType".
const NICKNAMES = [
  'Ana', 'Bruno', 'Carla', 'Davi', 'Elisa', 'Felipe', 'Gabi', 'Heitor',
  'Íris', 'João', 'Karina', 'Lucas', 'Marina', 'Nuno', 'Olívia', 'Pedro',
  'Quésia', 'Rafael', 'Sofia', 'Tiago', 'Úrsula', 'Vitor', 'Wanda', 'Yuri',
];

// Sobrenomes das sessões principais: todas se chamam Claude, e o sobrenome
// distingue uma da outra (Claude Almeida, Claude Barbosa...).
const SURNAMES = [
  'Almeida', 'Barbosa', 'Cardoso', 'Duarte', 'Esteves', 'Ferreira', 'Gomes', 'Holanda',
  'Leite', 'Macedo', 'Nogueira', 'Oliveira', 'Pereira', 'Queiroz', 'Ribeiro', 'Siqueira',
  'Teixeira', 'Vasconcelos', 'Andrade', 'Bezerra', 'Campos', 'Dantas', 'Freitas', 'Guimarães',
  'Lacerda', 'Magalhães', 'Monteiro', 'Moreira', 'Nascimento', 'Pacheco', 'Rezende', 'Sampaio',
  'Tavares', 'Valente', 'Xavier', 'Zanetti', 'Brandão', 'Coutinho', 'Fontes', 'Mendes',
];

function nicknameFor(kind, index) {
  const list = kind === 'main' ? SURNAMES : NICKNAMES;
  const base = list[index % list.length];
  if (kind === 'main') {
    const round = Math.floor(index / list.length);
    return round === 0 ? `Claude ${base}` : `Claude ${base} ${round + 1}`;
  }
  const round = Math.floor(index / NICKNAMES.length);
  return round === 0 ? base : `${base} ${round + 1}`;
}

export function agentKey(sessionId, agentId) {
  return `${sessionId}:${agentId || 'main'}`;
}

export function createStore({ now = () => Date.now(), staleMs = DEFAULT_STALE_MS } = {}) {
  const agents = new Map();
  /** Descrição curta dada no lançamento de um subagente, à espera do agente. */
  const spawnTasks = new Map();
  const diagnostics = {
    total: 0,
    toolEventsWithAgentId: 0,
    toolEventsWithoutAgentId: 0,
    hooks: new Map(),
  };

  function ensure(event, time) {
    const key = agentKey(event.sessionId, event.agentId);
    let agent = agents.get(key);
    if (!agent) {
      const kind = event.agentId ? 'subagent' : 'main';
      const index = [...agents.values()].filter((other) => other.kind === kind).length;
      agent = {
        key,
        nickname: nicknameFor(kind, index),
        sessionId: event.sessionId,
        agentId: event.agentId,
        kind: event.agentId ? 'subagent' : 'main',
        parentKey: event.agentId ? agentKey(event.sessionId, null) : null,
        agentType: null,
        project: null,
        state: 'unknown',
        activity: null,
        currentTool: null,
        currentFile: null,
        task: null,
        taskSource: null,
        tasks: [],
        startedAt: null,
        firstSeenAt: time,
        lastEventAt: null,
        endedAt: null,
        tools: {},
        files: [],
        events: [],
        eventCount: 0,
        errorCount: 0,
        lastError: null,
      };
      const pending = spawnTasks.get(key);
      if (pending) {
        agent.task = pending;
        agent.taskSource = 'spawn';
      }
      agents.set(key, agent);
    }
    return agent;
  }

  function record(event) {
    diagnostics.total += 1;
    const hook = event.hookEvent || event.type;
    let entry = diagnostics.hooks.get(hook);
    if (!entry) {
      entry = { hook, count: 0, fields: new Set() };
      diagnostics.hooks.set(hook, entry);
    }
    entry.count += 1;
    for (const field of event.fieldsSeen) entry.fields.add(field);
    if (event.type.startsWith('tool.')) {
      if (event.agentId) diagnostics.toolEventsWithAgentId += 1;
      else diagnostics.toolEventsWithoutAgentId += 1;
    }
  }

  function openTask(agent) {
    for (let i = agent.tasks.length - 1; i >= 0; i -= 1) {
      if (!agent.tasks[i].done) return agent.tasks[i];
    }
    return null;
  }

  /** Último erro de um agente. Eventos antigos não trazem o motivo: código e mensagem ficam nulos. */
  function failure(event, time) {
    return {
      kind: event.type === 'turn.failed' ? 'turn' : 'tool',
      code: event.error?.code ?? null,
      message: event.error?.message ?? null,
      tool: event.tool,
      at: time,
    };
  }

  /** Aplica um evento já normalizado. Devolve as chaves dos agentes alterados. */
  function apply(event) {
    record(event);
    const time = Date.parse(event.occurredAt);
    const agent = ensure(event, time);
    const changed = new Set([agent.key]);

    // Eventos assíncronos podem chegar fora de ordem. Um evento mais antigo
    // que o último aplicado entra no histórico, mas não muda o estado.
    const late = agent.lastEventAt !== null && time < agent.lastEventAt;

    agent.eventCount += 1;
    agent.events.push({
      type: event.type,
      hookEvent: event.hookEvent,
      tool: event.tool,
      activity: event.activity,
      file: event.file,
      detail: event.detail,
      errorCode: event.error?.code ?? null,
      errorMessage: event.error?.message ?? null,
      taskSubject: event.task?.subject ?? event.spawn?.description ?? null,
      occurredAt: event.occurredAt,
    });
    if (late) agent.events.sort((a, b) => Date.parse(a.occurredAt) - Date.parse(b.occurredAt));
    if (agent.events.length > MAX_EVENTS) agent.events.splice(0, agent.events.length - MAX_EVENTS);

    if (event.agentType) agent.agentType = event.agentType;
    if (event.project) agent.project = event.project;
    if (event.tool && event.type === 'tool.started') {
      agent.tools[event.tool] = (agent.tools[event.tool] || 0) + 1;
    }
    if (event.file) {
      agent.files = [event.file, ...agent.files.filter((f) => f !== event.file)].slice(0, MAX_FILES);
    }

    // A descrição do lançamento só é ligada ao subagente quando o próprio
    // evento traz o identificador dele. Nunca por adivinhação.
    if (event.spawnedAgentId && event.spawn?.description) {
      const childKey = agentKey(event.sessionId, event.spawnedAgentId);
      spawnTasks.set(childKey, event.spawn.description);
      const child = agents.get(childKey);
      if (child && !child.task) {
        child.task = event.spawn.description;
        child.taskSource = 'spawn';
        changed.add(childKey);
      }
    }

    if (late) return [...changed];
    agent.lastEventAt = time;

    switch (event.type) {
      case 'session.started':
        agent.state = 'waiting';
        agent.startedAt ??= time;
        agent.endedAt = null;
        break;
      case 'prompt.submitted':
        agent.state = 'working';
        agent.activity = null;
        break;
      case 'tool.started':
        agent.state = 'working';
        agent.activity = event.activity;
        agent.currentTool = event.tool;
        agent.currentFile = event.file;
        break;
      case 'tool.finished':
        agent.state = 'working';
        agent.currentTool = null;
        agent.currentFile = null;
        break;
      case 'tool.failed':
        agent.state = 'error';
        agent.errorCount += 1;
        agent.lastError = failure(event, time);
        agent.currentTool = null;
        agent.currentFile = null;
        break;
      case 'permission.requested':
        agent.state = 'waiting';
        break;
      case 'notification':
        if (event.detail && /permission|idle|elicitation/i.test(event.detail)) agent.state = 'waiting';
        break;
      case 'agent.started':
        agent.state = 'working';
        agent.startedAt = time;
        agent.endedAt = null;
        break;
      case 'agent.stopped':
        agent.state = 'done';
        agent.endedAt = time;
        agent.currentTool = null;
        agent.currentFile = null;
        agent.activity = null;
        break;
      case 'turn.stopped':
        agent.state = 'done';
        agent.currentTool = null;
        agent.currentFile = null;
        agent.activity = null;
        break;
      case 'turn.failed':
        agent.state = 'error';
        agent.errorCount += 1;
        agent.lastError = failure(event, time);
        break;
      case 'task.created':
        if (event.task) {
          agent.tasks.push({ id: event.task.id, subject: event.task.subject, done: false });
          agent.task = event.task.subject;
          agent.taskSource = 'task';
        }
        break;
      case 'task.completed':
        if (event.task) {
          const found = agent.tasks.find((t) => t.id === event.task.id);
          if (found) found.done = true;
          else agent.tasks.push({ id: event.task.id, subject: event.task.subject, done: true });
          if (agent.taskSource === 'task') agent.task = openTask(agent)?.subject ?? null;
        }
        break;
      case 'session.ended':
        for (const other of agents.values()) {
          if (other.sessionId !== agent.sessionId) continue;
          if (other.key === agent.key || ['working', 'waiting', 'unknown', 'error'].includes(other.state)) {
            other.state = 'disconnected';
            other.endedAt ??= time;
            other.currentTool = null;
            other.currentFile = null;
            other.activity = null;
            changed.add(other.key);
          }
        }
        break;
      default:
        // Evento sem regra: fica no histórico e não muda o estado.
        break;
    }
    return [...changed];
  }

  function view(agent) {
    const current = now();
    let state = agent.state;
    let stateReason = null;
    if (state === 'working' && agent.lastEventAt !== null && current - agent.lastEventAt > staleMs) {
      state = 'unknown';
      stateReason = `Sem eventos há ${Math.round((current - agent.lastEventAt) / 60000)} min`;
    } else if (state === 'unknown') {
      stateReason = 'Os eventos recebidos não permitem determinar o estado';
    }
    const iso = (t) => (t === null ? null : new Date(t).toISOString());
    return {
      key: agent.key,
      sessionId: agent.sessionId,
      shortSession: agent.sessionId.slice(0, 8),
      agentId: agent.agentId,
      kind: agent.kind,
      parentKey: agent.parentKey,
      nickname: agent.nickname,
      // O nome é o tipo informado pelo hook. Sem ele, um rótulo neutro.
      name: agent.agentType ?? (agent.kind === 'main' ? 'Sessão principal' : 'Subagente'),
      agentType: agent.agentType,
      identified: agent.kind === 'main' || agent.agentType !== null,
      project: agent.project,
      state,
      stateReason,
      activity: state === 'working' ? agent.activity : null,
      currentTool: agent.currentTool,
      currentFile: agent.currentFile,
      task: agent.task,
      taskSource: agent.taskSource,
      tasksDone: agent.tasks.filter((t) => t.done).length,
      tasksTotal: agent.tasks.length,
      startedAt: iso(agent.startedAt),
      firstSeenAt: iso(agent.firstSeenAt),
      lastEventAt: iso(agent.lastEventAt ?? agent.firstSeenAt),
      endedAt: iso(agent.endedAt),
      tools: { ...agent.tools },
      files: [...agent.files],
      events: agent.events.slice(-60),
      eventCount: agent.eventCount,
      errorCount: agent.errorCount,
      lastError: agent.lastError ? { ...agent.lastError, at: iso(agent.lastError.at) } : null,
    };
  }

  return {
    apply,
    get: (key) => (agents.has(key) ? view(agents.get(key)) : null),
    // O Claude Code também dispara o fim de agentes internos (sem tipo e sem
    // nenhum outro evento). Eles não são agentes de trabalho e ficam fora da
    // lista; continuam contados no diagnóstico.
    list: () =>
      [...agents.values()]
        .filter(
          (agent) =>
            !(agent.kind === 'subagent' && agent.agentType === null && agent.events.every((e) => e.type === 'agent.stopped')),
        )
        .map(view),
    diagnostics: () => ({
      total: diagnostics.total,
      toolEventsWithAgentId: diagnostics.toolEventsWithAgentId,
      toolEventsWithoutAgentId: diagnostics.toolEventsWithoutAgentId,
      hooks: [...diagnostics.hooks.values()]
        .map((h) => ({ hook: h.hook, count: h.count, fields: [...h.fields].sort() }))
        .sort((a, b) => b.count - a.count),
    }),
  };
}
