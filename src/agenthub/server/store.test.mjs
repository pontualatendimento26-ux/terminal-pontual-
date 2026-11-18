import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createStore, normalizeEvent } from './store.mjs';

const T0 = Date.parse('2026-11-16T14:00:00.000Z');
const at = (seconds) => new Date(T0 + seconds * 1000).toISOString();

function ev(type, seconds, extra = {}) {
  return normalizeEvent({ sessionId: 's1', type, occurredAt: at(seconds), ...extra });
}

function storeAt(seconds, options = {}) {
  let clock = T0 + seconds * 1000;
  const store = createStore({ now: () => clock, ...options });
  return { store, setClock: (s) => (clock = T0 + s * 1000) };
}

test('evento inválido é recusado', () => {
  assert.equal(normalizeEvent({ type: 'tool.started', occurredAt: at(0) }), null);
  assert.equal(normalizeEvent({ sessionId: 's1', type: 'x', occurredAt: 'ontem' }), null);
  assert.equal(normalizeEvent('texto'), null);
});

test('sessão principal: aguardando, trabalhando, concluído, desconectado', () => {
  const { store } = storeAt(100);
  store.apply(ev('session.started', 0));
  assert.equal(store.get('s1:main').state, 'waiting');
  store.apply(ev('prompt.submitted', 1));
  assert.equal(store.get('s1:main').state, 'working');
  store.apply(ev('tool.started', 2, { tool: 'Edit', activity: 'coding', file: 'docs/API.md' }));
  const working = store.get('s1:main');
  assert.equal(working.activity, 'coding');
  assert.equal(working.currentTool, 'Edit');
  assert.deepEqual(working.files, ['docs/API.md']);
  assert.deepEqual(working.tools, { Edit: 1 });
  store.apply(ev('turn.stopped', 3));
  assert.equal(store.get('s1:main').state, 'done');
  store.apply(ev('session.ended', 4));
  assert.equal(store.get('s1:main').state, 'disconnected');
});

test('agente visto só por um evento sem regra fica com estado desconhecido', () => {
  const { store } = storeAt(10);
  store.apply(ev('hook.other', 0, { hookEvent: 'EventoNovo' }));
  const agent = store.get('s1:main');
  assert.equal(agent.state, 'unknown');
  assert.equal(agent.startedAt, null);
  assert.equal(agent.task, null);
});

test('subagente é separado da sessão e leva o tipo informado pelo hook', () => {
  const { store } = storeAt(10);
  store.apply(ev('agent.started', 0, { agentId: 'a1', agentType: 'ecc:architect' }));
  store.apply(ev('tool.started', 1, { agentId: 'a1', tool: 'Read', activity: 'analyzing' }));
  const sub = store.get('s1:a1');
  assert.equal(sub.kind, 'subagent');
  assert.equal(sub.name, 'ecc:architect');
  assert.equal(sub.parentKey, 's1:main');
  assert.equal(sub.state, 'working');
  assert.equal(sub.startedAt, at(0));
  store.apply(ev('agent.stopped', 2, { agentId: 'a1' }));
  assert.equal(store.get('s1:a1').state, 'done');
});

test('subagente sem tipo informado não recebe nome inventado', () => {
  const { store } = storeAt(10);
  store.apply(ev('tool.started', 0, { agentId: 'a9', tool: 'Grep', activity: 'analyzing' }));
  const sub = store.get('s1:a9');
  assert.equal(sub.name, 'Subagente');
  assert.equal(sub.agentType, null);
  assert.equal(sub.identified, false);
  assert.equal(sub.startedAt, null);
});

test('a tarefa do subagente só é ligada quando o evento traz o identificador dele', () => {
  const { store } = storeAt(10);
  store.apply(
    ev('tool.started', 0, { tool: 'Agent', spawn: { subagentType: 'x', description: 'Revisar docs' } }),
  );
  store.apply(ev('agent.started', 1, { agentId: 'a1', agentType: 'x' }));
  assert.equal(store.get('s1:a1').task, null, 'sem identificador, não se presume a tarefa');
  store.apply(
    ev('tool.finished', 2, {
      tool: 'Agent',
      spawn: { subagentType: 'x', description: 'Revisar docs' },
      spawnedAgentId: 'a1',
    }),
  );
  assert.equal(store.get('s1:a1').task, 'Revisar docs');
  assert.equal(store.get('s1:a1').taskSource, 'spawn');
});

test('cada agente recebe um apelido único e estável, sem trocar o tipo real', () => {
  const { store } = storeAt(10);
  store.apply(ev('session.started', 0));
  store.apply(ev('agent.started', 1, { agentId: 'a1', agentType: 'general-purpose' }));
  store.apply(ev('agent.started', 2, { agentId: 'a2', agentType: 'general-purpose' }));
  store.apply(ev('tool.started', 3, { agentId: 'a1', tool: 'Read' }));
  assert.equal(store.get('s1:main').nickname, 'Claude Almeida');
  store.apply(normalizeEvent({ sessionId: 's2', type: 'session.started', occurredAt: at(4) }));
  assert.equal(store.get('s2:main').nickname, 'Claude Barbosa');
  assert.equal(store.get('s1:a1').nickname, 'Ana');
  assert.equal(store.get('s1:a2').nickname, 'Bruno');
  assert.equal(store.get('s1:a1').name, 'general-purpose');
});

test('falha de ferramenta marca erro até o próximo evento', () => {
  const { store } = storeAt(10);
  store.apply(ev('tool.failed', 0, { tool: 'Bash' }));
  assert.equal(store.get('s1:main').state, 'error');
  assert.equal(store.get('s1:main').errorCount, 1);
  store.apply(ev('tool.started', 1, { tool: 'Bash', activity: 'working' }));
  assert.equal(store.get('s1:main').state, 'working');
});

test('o último erro guarda o motivo e continua visível no histórico', () => {
  const { store } = storeAt(10);
  store.apply(
    ev('turn.failed', 0, { error: { code: 'rate_limit', message: "You've hit your 5-hour limit" } }),
  );
  const agent = store.get('s1:main');
  assert.equal(agent.state, 'error');
  assert.equal(agent.lastError.kind, 'turn');
  assert.equal(agent.lastError.code, 'rate_limit');
  assert.equal(agent.lastError.message, "You've hit your 5-hour limit");
  assert.equal(agent.events.at(-1).errorCode, 'rate_limit');
});

test('falha antiga, sem motivo, fica com código e mensagem nulos', () => {
  const { store } = storeAt(10);
  store.apply(ev('tool.failed', 0, { tool: 'Bash' }));
  assert.deepEqual(
    { ...store.get('s1:main').lastError, at: null },
    { kind: 'tool', code: null, message: null, tool: 'Bash', at: null },
  );
});

test('trabalho sem eventos por muito tempo vira desconhecido', () => {
  const { store, setClock } = storeAt(1, { staleMs: 60_000 });
  store.apply(ev('tool.started', 0, { tool: 'Bash', activity: 'working' }));
  assert.equal(store.get('s1:main').state, 'working');
  setClock(200);
  const stale = store.get('s1:main');
  assert.equal(stale.state, 'unknown');
  assert.match(stale.stateReason, /Sem eventos há/);
});

test('evento atrasado entra no histórico sem mudar o estado', () => {
  const { store } = storeAt(10);
  store.apply(ev('turn.stopped', 5));
  store.apply(ev('tool.started', 3, { tool: 'Edit', activity: 'coding' }));
  const agent = store.get('s1:main');
  assert.equal(agent.state, 'done');
  assert.equal(agent.events.length, 2);
  assert.equal(agent.events[0].type, 'tool.started');
});

test('tarefas alimentam a tarefa atual e a contagem', () => {
  const { store } = storeAt(10);
  store.apply(ev('task.created', 0, { task: { id: 't1', subject: 'Escrever testes' } }));
  store.apply(ev('task.created', 1, { task: { id: 't2', subject: 'Rodar testes' } }));
  assert.equal(store.get('s1:main').task, 'Rodar testes');
  store.apply(ev('task.completed', 2, { task: { id: 't2', subject: 'Rodar testes' } }));
  const agent = store.get('s1:main');
  assert.equal(agent.task, 'Escrever testes');
  assert.equal(agent.tasksDone, 1);
  assert.equal(agent.tasksTotal, 2);
});

test('fim de sessão desconecta os subagentes ainda ativos, não os concluídos', () => {
  const { store } = storeAt(10);
  store.apply(ev('agent.started', 0, { agentId: 'a1' }));
  store.apply(ev('agent.started', 1, { agentId: 'a2' }));
  store.apply(ev('agent.stopped', 2, { agentId: 'a2' }));
  store.apply(ev('session.ended', 3));
  assert.equal(store.get('s1:a1').state, 'disconnected');
  assert.equal(store.get('s1:a2').state, 'done');
});

test('o diagnóstico relata quais campos os hooks trouxeram', () => {
  const { store } = storeAt(10);
  store.apply(ev('tool.started', 0, { hookEvent: 'PreToolUse', tool: 'Read', fieldsSeen: ['cwd', 'tool_name'] }));
  store.apply(
    ev('tool.started', 1, { hookEvent: 'PreToolUse', agentId: 'a1', tool: 'Read', fieldsSeen: ['agent_id'] }),
  );
  const diag = store.diagnostics();
  assert.equal(diag.total, 2);
  assert.equal(diag.toolEventsWithAgentId, 1);
  assert.equal(diag.toolEventsWithoutAgentId, 1);
  assert.deepEqual(diag.hooks[0], { hook: 'PreToolUse', count: 2, fields: ['agent_id', 'cwd', 'tool_name'] });
});
