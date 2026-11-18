#!/usr/bin/env node
// Encaminhador de hooks do Claude Code para a Central dos Agentes.
//
// O Claude Code executa este script a cada evento de hook e entrega o evento
// em JSON pela entrada padrão. O script converte o evento para o contrato da
// Central e o envia por HTTP. Regras:
//   - nunca envia conteúdo: nem instruções, nem comandos, nem saídas de
//     ferramenta, nem conteúdo de arquivos. Só nomes de ferramenta, caminhos
//     de arquivo, o título curto de tarefas e a descrição curta de subagentes;
//   - exceção: quando algo falha, envia um resumo curto do erro (até 240
//     caracteres), com senhas, chaves, tokens e caminhos pessoais mascarados,
//     para a Central conseguir mostrar o motivo da falha;
//   - nunca atrapalha o Claude Code: não imprime nada e sempre sai com 0;
//   - se a Central estiver fora do ar, guarda o evento em fila local.

import { readFileSync, appendFileSync, existsSync, renameSync, unlinkSync } from 'node:fs';
import { dirname, resolve, relative, basename, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '..', '..');
const KEY_FILE = resolve(ROOT, 'src', 'agenthub', 'data', 'ingest.key');
const QUEUE_FILE = resolve(HERE, '.queue.jsonl');
const HUB_URL = process.env.AGENTHUB_URL || 'http://127.0.0.1:4317';
const MAX_QUEUE = 2000;
const SEND_TIMEOUT_MS = 1500;

const TYPE_BY_HOOK = {
  SessionStart: 'session.started',
  SessionEnd: 'session.ended',
  UserPromptSubmit: 'prompt.submitted',
  PreToolUse: 'tool.started',
  PostToolUse: 'tool.finished',
  PostToolUseFailure: 'tool.failed',
  PermissionRequest: 'permission.requested',
  Notification: 'notification',
  SubagentStart: 'agent.started',
  SubagentStop: 'agent.stopped',
  TaskCreated: 'task.created',
  TaskCompleted: 'task.completed',
  Stop: 'turn.stopped',
  StopFailure: 'turn.failed',
};

const CODING_TOOLS = new Set(['Edit', 'Write', 'MultiEdit', 'NotebookEdit']);
const ANALYZING_TOOLS = new Set(['Read', 'Grep', 'Glob', 'WebFetch', 'WebSearch', 'LS']);
const SPAWN_TOOLS = new Set(['Agent', 'Task']);
const SHELL_TOOLS = new Set(['Bash', 'PowerShell']);

// Lista explícita de comandos de teste. O comando em si nunca é enviado:
// ele só é comparado aqui, na máquina de desenvolvimento.
const TEST_COMMAND =
  /(^|[\s;&|(])(npm (run )?test|npm t\b|npx (vitest|jest|playwright)|pnpm (run )?test|yarn test|node --test|dotnet test|pytest|cargo test|go test)\b/;

function str(value, max) {
  if (typeof value !== 'string') return null;
  const clean = value.replace(/\s+/g, ' ').trim();
  if (!clean) return null;
  return clean.length > max ? clean.slice(0, max - 1) + '…' : clean;
}

export function classifyActivity(tool, toolInput) {
  if (!tool) return null;
  if (CODING_TOOLS.has(tool)) return 'coding';
  if (ANALYZING_TOOLS.has(tool)) return 'analyzing';
  if (SPAWN_TOOLS.has(tool)) return 'delegating';
  if (SHELL_TOOLS.has(tool)) {
    const command = typeof toolInput?.command === 'string' ? toolInput.command : '';
    return TEST_COMMAND.test(command) ? 'testing' : 'working';
  }
  return 'working';
}

export function relativeFile(filePath, cwd) {
  if (typeof filePath !== 'string' || !filePath) return null;
  if (!cwd || !isAbsolute(filePath)) return filePath.replace(/\\/g, '/').slice(0, 300);
  const rel = relative(cwd, filePath);
  // Arquivo fora do projeto: só o nome, para não expor a estrutura do disco.
  if (!rel || rel.startsWith('..') || isAbsolute(rel)) return '…/' + basename(filePath);
  return rel.replace(/\\/g, '/').slice(0, 300);
}

const MAX_ERROR = 240;

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** Mascara o que pode ser segredo ou dado pessoal num texto de erro. */
export function sanitizeError(value, cwd) {
  if (typeof value !== 'string') return null;
  let clean = value;
  if (cwd) {
    // Caminhos dentro do projeto viram relativos (nas duas formas de barra).
    for (const root of new Set([cwd, cwd.replace(/\\/g, '/')])) {
      clean = clean.replace(new RegExp(escapeRegExp(root) + '[\\\\/]?', 'gi'), '');
    }
  }
  clean = clean
    // Pastas pessoais fora do projeto.
    .replace(/[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s"']+/gi, '…')
    .replace(/\/(home|Users)\/[^/\s"']+/g, '…')
    // Credenciais em cabeçalhos e em pares nome=valor.
    .replace(/\b(Bearer|Basic)\s+\S+/gi, '$1 ***')
    .replace(
      /\b([\w-]*(?:key|token|secret|senha|password|passwd|pwd|credential)[\w-]*)(\s*[=:]\s*)("[^"]*"|'[^']*'|[^\s;,&]+)/gi,
      '$1$2***',
    )
    // Sequências longas que parecem chaves ou hashes.
    .replace(/[A-Za-z0-9+/_-]{32,}={0,2}/g, '***');
  return str(clean, MAX_ERROR);
}

/** Resumo do erro de uma falha: um código e uma mensagem curta e mascarada. */
export function errorSummary(input, hookEvent, cwd) {
  if (hookEvent === 'PostToolUseFailure') {
    if (input.is_interrupt === true) return { code: 'interrupted', message: null };
    return { code: 'tool_error', message: sanitizeError(input.error, cwd) };
  }
  if (hookEvent === 'StopFailure') {
    const code = typeof input.error === 'string' ? str(input.error, 40) : null;
    // Numa falha de turno, a última mensagem é o texto do erro da API
    // (por exemplo, o aviso de limite de uso com o horário de liberação).
    const message =
      sanitizeError(input.error_details, cwd) ?? sanitizeError(input.last_assistant_message, cwd);
    return { code: code ?? 'unknown', message };
  }
  return null;
}

/** Converte a entrada de um hook no evento do contrato. Devolve null se não der. */
export function mapHookInput(input, now = new Date()) {
  if (!input || typeof input !== 'object') return null;
  const sessionId = str(input.session_id, 120);
  const hookEvent = str(input.hook_event_name, 60);
  if (!sessionId || !hookEvent) return null;

  const tool = str(input.tool_name, 80);
  const toolInput = input.tool_input && typeof input.tool_input === 'object' ? input.tool_input : null;
  const toolResponse =
    input.tool_response && typeof input.tool_response === 'object' ? input.tool_response : null;
  // A raiz do projeto é mais estável que o diretório corrente, que muda
  // quando o agente navega entre pastas.
  const cwd =
    process.env.CLAUDE_PROJECT_DIR || (typeof input.cwd === 'string' ? input.cwd : null);

  let spawn = null;
  let spawnedAgentId = null;
  if (tool && SPAWN_TOOLS.has(tool)) {
    spawn = {
      subagentType: str(toolInput?.subagent_type, 80),
      description: str(toolInput?.description, 100),
    };
    spawnedAgentId = str(toolResponse?.agentId ?? toolResponse?.agent_id, 120);
  }

  const taskId = str(input.task_id, 120);
  const task = taskId ? { id: taskId, subject: str(input.task_subject, 140) } : null;

  const detail =
    str(input.source, 40) ?? str(input.reason, 40) ?? str(input.notification_type, 40) ?? null;

  return {
    v: 1,
    sessionId,
    agentId: str(input.agent_id, 120),
    agentType: str(input.agent_type, 80),
    hookEvent,
    type: TYPE_BY_HOOK[hookEvent] ?? 'hook.other',
    tool,
    activity: tool ? classifyActivity(tool, toolInput) : null,
    file: relativeFile(toolInput?.file_path ?? toolInput?.path ?? toolInput?.notebook_path, cwd),
    project: cwd ? basename(cwd) : null,
    spawn,
    spawnedAgentId,
    task,
    detail,
    error: errorSummary(input, hookEvent, cwd),
    // Só os nomes dos campos recebidos, para a Central relatar o que os hooks
    // de fato informam. Nenhum valor é enviado aqui.
    fieldsSeen: Object.keys(input).sort().slice(0, 40),
    occurredAt: now.toISOString(),
  };
}

function readKey() {
  if (process.env.AGENTHUB_KEY) return process.env.AGENTHUB_KEY.trim();
  try {
    return readFileSync(KEY_FILE, 'utf8').trim() || null;
  } catch {
    return null;
  }
}

function takeQueue() {
  if (!existsSync(QUEUE_FILE)) return [];
  const taken = `${QUEUE_FILE}.${process.pid}`;
  try {
    renameSync(QUEUE_FILE, taken);
    const lines = readFileSync(taken, 'utf8').split('\n').filter(Boolean);
    unlinkSync(taken);
    const events = [];
    for (const line of lines.slice(-MAX_QUEUE)) {
      try {
        events.push(JSON.parse(line));
      } catch {
        // linha corrompida: descartada
      }
    }
    return events;
  } catch {
    return [];
  }
}

function putQueue(events) {
  try {
    appendFileSync(QUEUE_FILE, events.map((e) => JSON.stringify(e)).join('\n') + '\n');
  } catch {
    // sem onde guardar: o evento se perde, mas o Claude Code não é afetado
  }
}

async function send(events, key) {
  const response = await fetch(`${HUB_URL}/api/v1/agent-events`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-agenthub-key': key },
    body: JSON.stringify({ events }),
    signal: AbortSignal.timeout(SEND_TIMEOUT_MS),
  });
  return response.ok;
}

async function readStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString('utf8');
}

async function main() {
  let event = null;
  try {
    event = mapHookInput(JSON.parse(await readStdin()));
  } catch {
    return;
  }
  if (!event) return;

  const key = readKey();
  if (!key) {
    // A Central nunca foi iniciada nesta máquina: guarda para depois.
    putQueue([event]);
    return;
  }

  const events = [...takeQueue(), event];
  let delivered = false;
  try {
    delivered = await send(events, key);
  } catch {
    delivered = false;
  }
  if (!delivered) putQueue(events.slice(-MAX_QUEUE));
}

const invokedDirectly =
  process.argv[1] &&
  resolve(process.argv[1]).toLowerCase() === fileURLToPath(import.meta.url).toLowerCase();

if (invokedDirectly) {
  main()
    .catch(() => {})
    .finally(() => process.exit(0));
}
