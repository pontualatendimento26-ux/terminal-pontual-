import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mapHookInput, classifyActivity, relativeFile } from './forward.mjs';

const NOW = new Date('2026-11-16T14:00:00.000Z');
const CWD = process.platform === 'win32' ? 'C:\\Projetos\\Terminal-Pontual' : '/work/Terminal-Pontual';
const FILE =
  process.platform === 'win32'
    ? 'C:\\Projetos\\Terminal-Pontual\\docs\\API.md'
    : '/work/Terminal-Pontual/docs/API.md';
const OUTSIDE = process.platform === 'win32' ? 'C:\\Users\\alguem\\segredo.txt' : '/home/alguem/segredo.txt';

test('sem session_id ou sem nome do evento não gera evento', () => {
  assert.equal(mapHookInput({ hook_event_name: 'Stop' }, NOW), null);
  assert.equal(mapHookInput({ session_id: 's1' }, NOW), null);
  assert.equal(mapHookInput(null, NOW), null);
});

test('ferramenta de edição vira tool.started com atividade e arquivo relativo', () => {
  const event = mapHookInput(
    {
      session_id: 's1',
      hook_event_name: 'PreToolUse',
      cwd: CWD,
      tool_name: 'Edit',
      tool_input: { file_path: FILE, old_string: 'segredo antigo', new_string: 'segredo novo' },
    },
    NOW,
  );
  assert.equal(event.type, 'tool.started');
  assert.equal(event.activity, 'coding');
  assert.equal(event.file, 'docs/API.md');
  assert.equal(event.project, 'Terminal-Pontual');
  assert.equal(event.agentId, null);
  assert.equal(event.occurredAt, NOW.toISOString());
});

test('nenhum conteúdo de instrução, comando, arquivo ou saída é enviado', () => {
  const secret = 'CONTEUDO-QUE-NAO-PODE-SAIR';
  const inputs = [
    { session_id: 's1', hook_event_name: 'UserPromptSubmit', prompt: secret },
    {
      session_id: 's1',
      hook_event_name: 'PostToolUse',
      tool_name: 'Bash',
      tool_input: { command: `echo ${secret}` },
      tool_response: { stdout: secret },
    },
    {
      session_id: 's1',
      hook_event_name: 'PreToolUse',
      tool_name: 'Write',
      tool_input: { file_path: FILE, content: secret },
      cwd: CWD,
    },
    { session_id: 's1', hook_event_name: 'SubagentStop', last_assistant_message: secret },
    {
      session_id: 's1',
      hook_event_name: 'PostToolUseFailure',
      tool_name: 'Bash',
      tool_input: { command: secret },
      is_interrupt: true,
      error: secret,
    },
  ];
  for (const input of inputs) {
    const serialized = JSON.stringify(mapHookInput(input, NOW));
    assert.ok(!serialized.includes(secret), `vazou conteúdo em ${input.hook_event_name}`);
  }
});

test('falha de ferramenta envia o motivo resumido, sem segredos nem caminhos pessoais', () => {
  const key = 'sk-ant-' + 'a'.repeat(40);
  const event = mapHookInput(
    {
      session_id: 's1',
      hook_event_name: 'PostToolUseFailure',
      cwd: CWD,
      tool_name: 'Bash',
      tool_input: { command: 'npm test' },
      error: `Falhou ao abrir ${FILE} com password=abc123 e Bearer xyz.789 em ${OUTSIDE}; chave ${key}`,
    },
    NOW,
  );
  assert.equal(event.type, 'tool.failed');
  assert.equal(event.error.code, 'tool_error');
  const message = event.error.message;
  assert.ok(message.includes('docs'), message);
  assert.ok(!message.includes('abc123'), message);
  assert.ok(!message.includes('xyz.789'), message);
  assert.ok(!message.includes(key), message);
  assert.ok(!message.includes('alguem'), message);
  assert.ok(!message.includes('Projetos'), message);
});

test('motivo do erro é limitado a 240 caracteres', () => {
  const event = mapHookInput(
    { session_id: 's1', hook_event_name: 'PostToolUseFailure', tool_name: 'Bash', error: 'erro '.repeat(200) },
    NOW,
  );
  assert.ok(event.error.message.length <= 240);
});

test('interrupção pelo usuário não envia mensagem', () => {
  const event = mapHookInput(
    { session_id: 's1', hook_event_name: 'PostToolUseFailure', tool_name: 'Bash', is_interrupt: true, error: 'x' },
    NOW,
  );
  assert.deepEqual(event.error, { code: 'interrupted', message: null });
});

test('falha de turno envia o código e o aviso da API', () => {
  const event = mapHookInput(
    {
      session_id: 's1',
      hook_event_name: 'StopFailure',
      error: 'rate_limit',
      last_assistant_message: "You've hit your 5-hour limit · resets 6:30pm (America/Sao_Paulo)",
    },
    NOW,
  );
  assert.equal(event.type, 'turn.failed');
  assert.equal(event.error.code, 'rate_limit');
  assert.ok(event.error.message.includes('5-hour limit'));
});

test('eventos que não são falha não levam erro', () => {
  const event = mapHookInput({ session_id: 's1', hook_event_name: 'Stop', last_assistant_message: 'oi' }, NOW);
  assert.equal(event.error, null);
});

test('subagente é identificado por agent_id e agent_type quando o hook os informa', () => {
  const event = mapHookInput(
    {
      session_id: 's1',
      hook_event_name: 'PreToolUse',
      agent_id: 'agent-abc',
      agent_type: 'ecc:architect',
      tool_name: 'Read',
      tool_input: { file_path: FILE },
      cwd: CWD,
    },
    NOW,
  );
  assert.equal(event.agentId, 'agent-abc');
  assert.equal(event.agentType, 'ecc:architect');
  assert.equal(event.activity, 'analyzing');
});

test('lançamento de subagente carrega tipo, descrição curta e o identificador devolvido', () => {
  const event = mapHookInput(
    {
      session_id: 's1',
      hook_event_name: 'PostToolUse',
      tool_name: 'Agent',
      tool_input: { subagent_type: 'ecc:architect', description: 'Revisar documentos', prompt: 'texto longo' },
      tool_response: { agentId: 'agent-abc' },
    },
    NOW,
  );
  assert.deepEqual(event.spawn, { subagentType: 'ecc:architect', description: 'Revisar documentos' });
  assert.equal(event.spawnedAgentId, 'agent-abc');
  assert.ok(!JSON.stringify(event).includes('texto longo'));
});

test('comando de teste é classificado como teste; os demais, como trabalho', () => {
  assert.equal(classifyActivity('Bash', { command: 'cd src && npm test' }), 'testing');
  assert.equal(classifyActivity('Bash', { command: 'node --test server/' }), 'testing');
  assert.equal(classifyActivity('PowerShell', { command: 'dotnet test' }), 'testing');
  assert.equal(classifyActivity('Bash', { command: 'git status' }), 'working');
  assert.equal(classifyActivity('Bash', {}), 'working');
});

test('arquivo fora do projeto mostra só o nome', () => {
  assert.equal(relativeFile(OUTSIDE, CWD), '…/segredo.txt');
  assert.equal(relativeFile(null, CWD), null);
});

test('evento de hook desconhecido é repassado como hook.other', () => {
  const event = mapHookInput({ session_id: 's1', hook_event_name: 'EventoNovo' }, NOW);
  assert.equal(event.type, 'hook.other');
  assert.deepEqual(event.fieldsSeen, ['hook_event_name', 'session_id']);
});

test('tarefas trazem identificador e título', () => {
  const event = mapHookInput(
    { session_id: 's1', hook_event_name: 'TaskCreated', task_id: 't1', task_subject: 'Escrever testes' },
    NOW,
  );
  assert.equal(event.type, 'task.created');
  assert.deepEqual(event.task, { id: 't1', subject: 'Escrever testes' });
});
